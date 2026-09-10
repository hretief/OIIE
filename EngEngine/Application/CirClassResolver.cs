using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oiie.Ccom.Extensions;
using Oiie.Cir.Client;

namespace EngEngine.Application;

/// <summary>
/// Resolves an ENG EC class to the RDL class identity CIR already holds for it.
///
/// The identity published in SegmentType used to be derived --
/// <c>CcomUuid.ForReferenceData("MIMOSA-RDL", key)</c> hashed the RDL key into a
/// UUID. That value is well-formed and stable, and matches the RDL library only
/// by coincidence: nothing outside this engine ever agreed to it. A consumer
/// binding on it records an identity ENG invented as though it were governed.
///
/// CIR is asked instead. Reference data is minted by RDL and cross-referenced in
/// CIR, so the CIRID held against the ENG class name is an identity somebody
/// registered rather than one computed here. That is the whole point of the
/// registry for reference data: it is the published answer to "which RDL class
/// does this ENG class mean".
///
/// Advisory, like the taxonomy validator alongside it. A miss, a cold CIR or a
/// failed call all return null, and the caller falls back to the configured map.
/// A drain has markers to publish and an unresolvable class is not a reason to
/// stop publishing them.
///
/// Entries are keyed on ECClassId, not on the class name: IdInSource carries the
/// source system's own identifier, which is the same value the segment publishes
/// as IDInInfoSource. Keying on the name would put a renameable label where a
/// key belongs, and leave a receiver unable to look up the entry using the id it
/// was handed.
/// </summary>
public sealed class CirClassResolver(
    ICirClient cir,
    RdlTaxonomyValidator rdl,
    IOptions<EngEngineOptions> engineOptions,
    ILogger<CirClassResolver> logger)
{
    private readonly EngEngineOptions _options = engineOptions.Value;

    // One lookup at a time, for the reason the validator serialises its fetch:
    // a drain publishing many elements of one class would otherwise send the
    // same query concurrently to fill the same cache slot.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Negative results are cached alongside positive ones. An unmapped class is
    // the common case before CIR is seeded, and re-asking per element per drain
    // would put a round trip on the publish path for every element that will
    // never resolve.
    private readonly Dictionary<string, CachedClassIdentity> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    // Whether the last lookup got an answer from CIR, as opposed to failing to
    // reach it. Both produce a null identity and only one of them means "no
    // such mapping". Held as a field rather than returned because it is
    // meaningful only inside the gate, immediately after the lookup.
    private bool _lastLookupWasAnswered;

    // Whether the last registration attempt settled the question, in the same
    // sense and for the same reason as the field above.
    //
    // A registration declines for two quite different kinds of reason. The
    // class may be genuinely unmapped -- no OutboundRdlClassMap entry, or
    // registration switched off -- which is a decision recorded in
    // configuration and will read the same way on the next drain. Or RDL may
    // have been unable to answer: unreachable, or not yet holding the class
    // because it is still starting up or has just been re-seeded. Only the
    // first kind is worth remembering.
    private bool _lastRegisterWasSettled;

    /// <summary>
    /// The CIRID CIR holds for an EC class, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Null covers three different situations deliberately: resolution is
    /// disabled, CIR holds no cross-reference for this class, or CIR could not
    /// be reached. They are distinguished in the log, not in the return value,
    /// because the caller's response to all three is the same -- fall back to
    /// the configured map rather than publish a fabricated identity.
    /// </remarks>
    public async Task<Guid?> ResolveAsync(
        string? ecClassName, long ecClassId, CancellationToken ct = default)
    {
        if (!_options.ResolveClassIdentityFromCir || string.IsNullOrWhiteSpace(ecClassName))
            return null;

        await _gate.WaitAsync(ct);

        try
        {
            if (_cache.TryGetValue(ecClassName, out var cached) && cached.IsFresh)
                return cached.Cirid;

            var cirid = await LookupAsync(ecClassName, ecClassId, ct);

            // Only a genuine miss is worth registering. A CIR that could not be
            // reached also returns null from the lookup, but writing on that
            // would race a registration that may already exist and cannot be
            // seen -- so the write is attempted only when CIR answered and said
            // it holds nothing.
            _lastRegisterWasSettled = true;

            if (cirid is null && _lastLookupWasAnswered)
                cirid = await RegisterAsync(ecClassName, ecClassId, ct);

            // A null that nobody has actually settled is not an answer, and
            // caching it would turn a transient outage into a fixed one for the
            // whole cache duration. This is the same distinction the lookup
            // already draws for an unreachable CIR, applied to the RDL side of
            // the registration: an unreachable RDL, or one that does not yet
            // hold the class because it is still starting up or has just been
            // re-seeded, must be asked again on the next drain.
            //
            // Left uncached rather than cached briefly because the drain
            // interval is already the retry interval, and the failure costs a
            // lookup rather than a timeout -- the RDL fetch behind it has its
            // own failure cache, so a provider that is down is not re-dialled
            // per element.
            var settled = _lastLookupWasAnswered && _lastRegisterWasSettled;

            if (cirid is not null || settled)
            {
                _cache[ecClassName] = new CachedClassIdentity(
                    cirid, DateTimeOffset.UtcNow.Add(_options.ClassCacheDuration));
            }

            return cirid;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Forgets every resolved class identity, so the next drain re-asks CIR and
    /// re-registers anything it no longer holds.
    /// </summary>
    /// <remarks>
    /// Exists because this cache describes a database that something else can
    /// empty. Day zero wipes CIR, but the cache is process memory and survives
    /// it, so the engine goes on believing it registered a class that no longer
    /// exists anywhere. It then publishes the right identity while writing
    /// nothing -- and a receiver that verifies the identity against CIR before
    /// mirroring it finds nothing to verify, so the cross-reference silently
    /// never comes back.
    ///
    /// The entries are only a cache in the sense that they can be rebuilt; the
    /// fact they stand for lives in CIR, and a wipe there has to be reflected
    /// here or the two disagree until the duration expires.
    /// </remarks>
    public async Task ClearCacheAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);

        try
        {
            _cache.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Guid?> LookupAsync(string ecClassName, long ecClassId, CancellationToken ct)
    {
        // Keyed on ECClassId, ENG's internal identifier for the class, not on
        // the class name. IdInSource is "the id in the source system", the same
        // contract as IDInInfoSource on the wire -- and the segment already
        // publishes ECClassId there, so keying CIR on the name would mean a
        // receiver holding a segment could not use the id it was given to find
        // the entry. The name is descriptive and renameable; the id is neither.
        //
        // Category and source are pinned too, so an id that happens to collide
        // with an entry in some other category cannot answer this question.
        var idInSource = ecClassId.ToString();

        var filter = new CirFilter
        {
            RegistryFilter = string.IsNullOrWhiteSpace(RegistryId)
                ? null
                : new CirRegistryFilter { Id = RegistryId },

            CategoryFilter = new CirCategoryFilter
            {
                Id = _options.ClassCategoryId,
                SourceId = _options.SourceId
            },

            EntryFilter = new CirEntryFilter
            {
                IdInSource = idInSource,
                SourceId = _options.SourceId
            }
        };

        IReadOnlyList<CirRegistry> registries;

        _lastLookupWasAnswered = false;

        try
        {
            registries = await cir.GetRegistryAsync([filter], ct);
            _lastLookupWasAnswered = true;
        }
        catch (Exception ex) when (ex is CirClientException or HttpRequestException or TaskCanceledException)
        {
            // Not fatal and not re-raised: CIR being unavailable says nothing
            // about whether the mapping is correct, and the drain can still
            // publish using the configured map.
            logger.LogWarning(
                ex,
                "CIR could not be asked for the class identity of '{ClassName}'; " +
                "falling back to the configured map.",
                ecClassName);

            return null;
        }

        var entry = registries
            .SelectMany(r => r.Categories)
            .SelectMany(c => c.Entries)
            .FirstOrDefault(e =>
                string.Equals(e.IdInSource, idInSource, StringComparison.OrdinalIgnoreCase)
                && e.Cirid is not null);

        if (entry?.Cirid is not { } cirid)
        {
            logger.LogInformation(
                "CIR holds no {Category} cross-reference for EC class '{ClassName}' ({ClassId}).",
                _options.ClassCategoryId, ecClassName, idInSource);

            return null;
        }

        logger.LogInformation(
            "CIR resolved EC class '{ClassName}' ({ClassId}) to class identity {Cirid}.",
            ecClassName, idInSource, cirid);

        return cirid;
    }

    /// <summary>
    /// Creates the missing ENG-class-to-RDL-class cross-reference in CIR.
    /// </summary>
    /// <remarks>
    /// The CIRID is RDL's own UUID for the class, never a derived one. That is
    /// the whole value of the registration: an entry carrying a locally minted
    /// GUID would be a mapping to an identity no other participant holds, which
    /// is worse than no mapping at all because the next lookup would find it and
    /// publish it as though it were governed.
    ///
    /// So every step that could substitute a weaker identity declines instead:
    /// no configured RDL key, no class in the library, or no UUID on the class
    /// all return null and leave CIR untouched. The caller then falls back
    /// exactly as it did before, and the next drain tries again.
    /// </remarks>
    private async Task<Guid?> RegisterAsync(string ecClassName, long ecClassId, CancellationToken ct)
    {
        if (!_options.RegisterClassIdentityInCir) return null;

        if (_options.ResolveRdlClassKey(ecClassName) is not { Length: > 0 } rdlKey)
        {
            logger.LogInformation(
                "EC class '{ClassName}' has no OutboundRdlClassMap entry, so there is " +
                "no RDL class to register it against.",
                ecClassName);

            return null;
        }

        RdlClass? rdlClass;

        try
        {
            rdlClass = await rdl.FindClassAsync(rdlKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing was settled: RDL may hold the class perfectly well and
            // simply not have been reachable this pass.
            _lastRegisterWasSettled = false;

            logger.LogWarning(
                ex, "RDL could not be asked about '{RdlKey}'; no mapping was registered.", rdlKey);

            return null;
        }

        if (rdlClass?.Uuid is not { } cirid)
        {
            // Also unsettled, and this is the case that actually bites. RDL not
            // holding the key looks identical to RDL not being ready to say so:
            // a provider still starting up, or one whose reference data has just
            // been re-seeded, answers exactly this way for a short while. Caching
            // it would leave the class unregistered long after RDL was correct.
            _lastRegisterWasSettled = false;

            logger.LogWarning(
                "RDL offered no identity for '{RdlKey}', so no cross-reference was registered " +
                "for EC class '{ClassName}'. A registration would have had to invent one.",
                rdlKey, ecClassName);

            return null;
        }

        var request = new CreateRegistryRequest(
            [
                new CirRegistry(
                    Id: RegistryId ?? _options.SourceId,
                    Description: [new CirLocalizedText("Reference data cross-references")],
                    Categories:
                    [
                        new CirCategory(
                            Id: _options.ClassCategoryId,
                            SourceId: _options.SourceId,
                            Description: [new CirLocalizedText("ENG EC class to RDL class")],
                            Entries:
                            [
                                new CirEntry(
                                    // ENG's internal key for the class, matching
                                    // what the segment publishes as
                                    // IDInInfoSource. The readable names live in
                                    // Name and Description, where a rename costs
                                    // nothing.
                                    IdInSource: ecClassId.ToString(),
                                    SourceId: _options.SourceId,
                                    Cirid: cirid,
                                    SourceOwnerId: _options.Enterprise,
                                    Name: ecClassName,
                                    Description: new CirLocalizedText(
                                        $"{ecClassName} is classified as {rdlKey}."),
                                    Properties: [])
                            ])
                    ])
            ],
            // False deliberately: the identity is RDL's, and letting CIR mint
            // one would defeat the point of having asked RDL for it.
            CreateCirid: false);

        try
        {
            await cir.RegisterEntriesAsync(request, ct);
        }
        catch (Exception ex) when (ex is CirClientException or HttpRequestException or TaskCanceledException)
        {
            // CIR was reachable enough to be asked but not to be written to.
            // Whether the cross-reference exists is therefore still unknown, so
            // the next drain should try again rather than assume there is none.
            _lastRegisterWasSettled = false;

            logger.LogWarning(
                ex,
                "Registering the {Category} cross-reference for EC class '{ClassName}' failed; " +
                "publication continues on the configured map.",
                _options.ClassCategoryId, ecClassName);

            return null;
        }

        logger.LogInformation(
            "Registered EC class '{ClassName}' ({ClassId}) in CIR as {RdlKey} with RDL's " +
            "class identity {Cirid}.",
            ecClassName, ecClassId, rdlKey, cirid);

        return cirid;
    }

    private string? RegistryId =>
        string.IsNullOrWhiteSpace(_options.ClassRegistryId)
            ? _options.Enterprise
            : _options.ClassRegistryId;

    private readonly record struct CachedClassIdentity(Guid? Cirid, DateTimeOffset Until)
    {
        public bool IsFresh => DateTimeOffset.UtcNow < Until;
    }
}
