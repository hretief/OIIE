using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oiie.Cir.Client;

namespace RegLocationEngine.Application;

/// <summary>
/// Records in CIR that a REG-LOCATION class_id means the governed RDL class an
/// inbound segment was typed with.
/// </summary>
/// <remarks>
/// The inbound half of the same cross-reference ENG writes on the way out. ENG
/// registers "my ENG.Streetlight is the RDL class whose GUID is X"; this
/// registers "my class_id 1703 is that same X". Neither participant's entry is
/// the mapping -- the mapping is the pair of them sharing a CIRID, which is what
/// lets a reader ask what a class means to somebody else instead of keeping a
/// private copy of their vocabulary.
///
/// The identity is never minted here. It arrives on the wire in
/// <c>Segment.Type.UUID</c>, and the interesting question is whether it can be
/// trusted: a publisher that could not resolve its class still sends a UUID, but
/// a derived one, hashed from the class key by the sender. Registering that
/// would put a fabricated identity into CIR under REG-LOCATION's name, and the
/// next reader would take it for governed -- the precise failure the outbound
/// leg exists to avoid, reintroduced from the other end.
///
/// So the UUID is verified before it is mirrored: CIR is asked whether anyone
/// has already registered it. A governed identity has been, by the publisher's
/// own outbound leg. A derived one has not. That makes the two legs deliberately
/// ordered rather than symmetric -- outbound seeds, inbound confirms -- and on a
/// first end-to-end run the inbound entry appears one drain later than the
/// outbound one. That is the ordering working, not a miss.
///
/// Advisory throughout. A cold CIR, an unverifiable UUID or a failed write all
/// leave the cross-reference unmade and the tag ingested regardless. An ingest
/// leg has proposals to file for a steward, and a missing registry entry is not
/// a reason to drop a location on the floor.
/// </remarks>
public sealed class CirClassRegistrar(
    ICirClient cir,
    IOptions<RegLocationEngineOptions> engineOptions,
    ILogger<CirClassRegistrar> logger)
{
    private readonly RegLocationEngineOptions _options = engineOptions.Value;

    // One class at a time. An ingest drain handles many segments of few
    // classes, so without this the same class would be checked concurrently by
    // every segment carrying it, all to fill one cache slot.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Keyed on class_id and the identity together, not class_id alone. A class
    // that has been re-pointed at a different RDL class is a different fact,
    // and a cache keyed only on the local id would keep answering for the old
    // one until the duration expired.
    private readonly Dictionary<(int ClassId, Guid Cirid), DateTimeOffset> _checked = [];

    /// <summary>
    /// Ensures CIR holds the cross-reference between this registry's class and
    /// the governed identity the segment carried.
    /// </summary>
    /// <param name="classId">The REG-LOCATION class_id the segment mapped to.</param>
    /// <param name="cirid">The identity from the inbound Segment.Type.UUID.</param>
    /// <param name="rdlKey">The governed key, for the entry's readable name.</param>
    public async Task EnsureAsync(
        int classId, Guid? cirid, string? rdlKey, CancellationToken ct = default)
    {
        if (!_options.RegisterClassIdentityInCir
            || string.IsNullOrWhiteSpace(_options.CirBaseUrl))
        {
            return;
        }

        // No identity on the wire is the unmapped case, not a failure: the
        // segment still files under the fallback class. There is simply nothing
        // to cross-reference it against.
        if (cirid is not { } identity || identity == Guid.Empty) return;

        await _gate.WaitAsync(ct);

        try
        {
            if (_checked.TryGetValue((classId, identity), out var until)
                && DateTimeOffset.UtcNow < until)
            {
                return;
            }

            if (await EnsureCoreAsync(classId, identity, rdlKey, ct))
            {
                _checked[(classId, identity)] =
                    DateTimeOffset.UtcNow.Add(_options.ClassCacheDuration);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <returns>
    /// True when the cross-reference is known to be in place and the answer is
    /// worth caching. False when CIR could not be reached or the identity could
    /// not be verified, both of which should be retried rather than remembered.
    /// </returns>
    private async Task<bool> EnsureCoreAsync(
        int classId, Guid identity, string? rdlKey, CancellationToken ct)
    {
        var idInSource = classId.ToString();

        IReadOnlyList<CirRegistry> holdings;

        try
        {
            // One query, two questions. Filtering on the CIRID alone returns
            // every participant's entry for this class, which answers both
            // "has anyone registered this identity" -- the verification -- and
            // "have we registered it" -- the idempotency check. Asking
            // separately would be two round trips for one fact.
            holdings = await cir.GetRegistryAsync(
                [
                    new CirFilter
                    {
                        CategoryFilter = new CirCategoryFilter { Id = _options.ClassCategoryId },
                        EntryFilter = new CirEntryFilter { Cirid = identity }
                    }
                ],
                ct);
        }
        catch (Exception ex) when (ex is CirClientException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                ex,
                "CIR could not be asked about class identity {Cirid}; the cross-reference " +
                "for class {ClassId} was left unmade.",
                identity, classId);

            return false;
        }

        var entries = holdings
            .SelectMany(r => r.Categories)
            .SelectMany(c => c.Entries)
            .ToList();

        if (entries.Count == 0)
        {
            // Nobody has registered this identity, so nothing vouches for it.
            // It is most likely a sender's derived fallback rather than a class
            // GUID from RDL, and mirroring it would launder a locally invented
            // identity into the registry as though it were governed.
            logger.LogInformation(
                "Segment class identity {Cirid} is not registered in CIR by any participant, " +
                "so it was not mirrored for class {ClassId}. It is probably a derived " +
                "identity rather than one RDL minted.",
                identity, classId);

            return false;
        }

        if (entries.Any(e =>
                string.Equals(e.SourceId, _options.SourceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.IdInSource, idInSource, StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogDebug(
                "CIR already cross-references class {ClassId} to {Cirid}.", classId, identity);

            return true;
        }

        var name = rdlKey is { Length: > 0 } ? rdlKey : entries[0].Name;

        var request = new CreateRegistryRequest(
            [
                new CirRegistry(
                    Id: RegistryId,
                    Description: [new CirLocalizedText("Reference data cross-references")],
                    Categories:
                    [
                        new CirCategory(
                            Id: _options.ClassCategoryId,
                            SourceId: _options.SourceId,
                            Description: [new CirLocalizedText("REG-LOCATION class to RDL class")],
                            Entries:
                            [
                                new CirEntry(
                                    IdInSource: idInSource,
                                    SourceId: _options.SourceId,
                                    Cirid: identity,
                                    SourceOwnerId: _options.Enterprise,
                                    Name: name,
                                    Description: new CirLocalizedText(
                                        $"REG-LOCATION class {classId} is classified as " +
                                        $"{name ?? identity.ToString()}."),
                                    Properties: [])
                            ])
                    ])
            ],
            // The identity is RDL's, arrived on the wire and was just verified
            // against the registry. Asking CIR to mint one would discard it.
            CreateCirid: false);

        try
        {
            await cir.RegisterEntriesAsync(request, ct);
        }
        catch (Exception ex) when (ex is CirClientException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(
                ex,
                "Registering the {Category} cross-reference for class {ClassId} failed; " +
                "ingestion continues.",
                _options.ClassCategoryId, classId);

            return false;
        }

        logger.LogInformation(
            "Registered REG-LOCATION class {ClassId} in CIR against governed class " +
            "identity {Cirid} ({Name}).",
            classId, identity, name ?? "(unnamed)");

        return true;
    }

    private string RegistryId =>
        string.IsNullOrWhiteSpace(_options.ClassRegistryId)
            ? _options.Enterprise
            : _options.ClassRegistryId;
}
