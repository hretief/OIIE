using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using SimHost.Application.Bods;
using SimHost.Application.Cir;
using SimHost.Application.Classification;
using SimHost.Application.Identity;
using SimHost.Application.Participants;
using SimHost.Domain.Common;
using SimHost.Domain.Mms;
using SimHost.Infrastructure.Sql;

namespace SimHost.Personalities.Mms;

/// <summary>
/// Creates light system inventory rows from segments arriving on the O&amp;M channel.
///
/// MMS is the end of the chain and the participant with the least context. It
/// receives LOC-000001 from REG-LOCATION and has no idea what that is — no shared
/// key, no prior integration, nothing but a string from a system it has never heard
/// of.
///
/// It cannot even write that string down. The customer schema has no column for a
/// foreign identifier, a FederationId or a CIRID, so the sender's identity is not
/// retained anywhere locally: the inbound name becomes LIGHT_SYSTEM_NAME and
/// everything else about where the row came from survives only in provenance and in
/// ws-CIR. Matching on re-receipt is therefore by name alone, which is weaker than
/// matching on a foreign key and will fail if the sender renames something.
///
/// That weakness is the honest consequence of the constraint rather than a defect to
/// paper over, and it is precisely the problem the registry exists to solve.
///
/// Because MMS files inventory under OWNER_ID and nothing else, a segment whose
/// context the registry cannot relate to an owner is refused rather than filed. Such
/// a row would carry no owner, fall outside every twin-scoped view, and be visible to
/// nobody afterwards -- accepted by the transport and lost to the business in the same
/// motion. Refusing keeps the message visible as Rejected with the registry's reason,
/// so the missing relation is something a steward can act on instead of something
/// discovered later by counting rows.
/// </summary>
public sealed class MmsSegmentsHandler(
    ITagIdentityService identities,
    CcomAttributeMapperFactory mappers,
    MmsContextResolver context,
    ILogger<MmsSegmentsHandler> logger) : IBodHandler
{
    public (string Verb, string Noun) Handles => ("Sync", "Segments");

    public string? ParticipantId => MmsService.ParticipantId;

    public async Task<BodHandlingResult> HandleAsync(
        ParticipantContext participant,
        ParticipantDbContext db,
        BodEnvelope envelope,
        Guid messageId,
        CancellationToken ct)
    {
        var segments = envelope.NounsAs(e => new Segment(e));
        if (segments.Count == 0)
        {
            return BodHandlingResult.Rejected("The BOD carried no segments.");
        }

        var nextId = await NextLightSystemIdAsync(db, ct);
        var created = 0;
        var mapper = mappers.For(participant);
        var mapped = 0;
        var unmapped = 0;

        // One resolution per distinct twin rather than per segment. The registry
        // round-trip is the expensive part of this handler and a batch normally
        // carries many segments from the same plant.
        var ownerByTwin = new Dictionary<string, OwnerResolution>(StringComparer.OrdinalIgnoreCase);

        // Every context is resolved before anything is written. A segment naming a
        // context the registry cannot relate to an owner stops the batch here, while
        // the database is still untouched -- rejecting after a partial write would
        // leave some rows filed and the message marked Rejected, which is a worse
        // account of what happened than not writing at all.
        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment.IDInInfoSource))
            {
                continue;
            }

            var resolution = await ResolveOwnerAsync(segment, ownerByTwin, ct);

            if (resolution.IsBlocked)
            {
                logger.LogWarning(
                    "MMS rejected {Count} segment(s): {Reason} [{CorrelationId}]",
                    segments.Count, resolution.Reason, envelope.BodId);

                return BodHandlingResult.Rejected(
                    $"{resolution.Reason}. Relate the iTwin to an OWNER_ID in ws-CIR, then resend.");
            }
        }

        foreach (var segment in segments)
        {
            var foreignSourceId = segment.InfoSource?.ShortName ?? envelope.SenderLogicalId ?? "unknown";
            var foreignId = segment.IDInInfoSource;

            if (string.IsNullOrWhiteSpace(foreignId))
            {
                continue;
            }

            // The name is all MMS can match on: LIGHT_SYSTEM_NAME is the alternate
            // key and there is no column holding what the sender called this. Two
            // systems describing the same light system under different names remain
            // invisible to each other until the registry relates them.
            var lightSystemName = segment.FullName ?? segment.ShortName ?? foreignId;

            // Which owner this belongs to, asked of the registry rather than
            // inferred. The segment carries the originator's context as its
            // RegistrationSite -- an iTwin GUID that MMS has no column for and no
            // means of interpreting. ws-CIR is the only place the correspondence
            // between that twin and an OWNER_ID exists, which is precisely the job
            // the registry was introduced to do.
            //
            // Resolved above and cached, so this cannot fail here: a context that
            // could not be related has already stopped the batch. Null therefore
            // means the segment asserted no context at all, which files an unowned
            // row -- the honest record of a sender that named no plant.
            var ownerId = (await ResolveOwnerAsync(segment, ownerByTwin, ct)).OwnerId;

            var existing = await db.Set<LightSystemInventory>()
                .FirstOrDefaultAsync(r => r.LightSystemName == lightSystemName, ct);

            if (existing is not null)
            {
                // A previously unowned row is adopted once the registry can name its
                // owner, so relating a twin after the fact repairs what already
                // arrived instead of requiring a resend. An owner already set is left
                // alone: reassignment is a stewardship decision, not an ingest one.
                existing.OwnerId ??= ownerId;

                var updated = Ingest(
                    participant, db, mapper, segment, existing.LightSystemId.ToString(),
                    foreignSourceId, messageId);

                mapped += updated.MappedCount;
                unmapped += updated.UnmappedCount;
                continue;
            }

            var row = new LightSystemInventory
            {
                LightSystemId = nextId,
                LightSystemName = lightSystemName,

                // Undetermined and Proposed, because that is what MMS actually knows.
                // The sender's classification is not MMS's classification, and
                // guessing a class code would assert a taxonomy decision nobody made.
                // The real value is set by a planner once the system is surveyed.
                LightSystemClassCodeId = UndeterminedClassCodeId,
                LightSystemStatusId = ProposedStatusId,

                // OWNER_ID is MMS's context key, and only a registry assertion can
                // say which owner a foreign context corresponds to. Resolved above
                // through ws-CIR rather than inferred here; null when the registry
                // has nothing to say, which is the state a steward needs to see.
                OwnerId = ownerId
            };

            nextId++;
            created++;

            db.Set<LightSystemInventory>().Add(row);

            // MMS keeps what it was sent, understood or not.
            //
            // It has no RDL of its own to speak of, so almost everything arrives
            // unmapped — and that is the honest result rather than a reason to discard.
            // A maintenance system that silently drops the control action because it has
            // no column for it is the failure this whole model argues against; retaining
            // it flagged means the value is still there when someone teaches MMS what it
            // means.
            var ingestion = Ingest(
                participant, db, mapper, segment, row.LightSystemId.ToString(),
                foreignSourceId, messageId);

            mapped += ingestion.MappedCount;
            unmapped += ingestion.UnmappedCount;

            // The registry is told which local key now stands for the sender's
            // identity. This is the only durable record of the correspondence, since
            // no MMS column can hold it.
            if (segment.UUID != Guid.Empty)
            {
                var assignment = identities.RegisterCode(
                    segment.UUID, MmsService.ParticipantId, row.LightSystemId.ToString());
                assignment.AdoptedFromRemote = true;
                db.Codes.Add(assignment);
            }

            db.Provenance.Add(new ProvenanceEntry
            {
                MessageId = messageId,
                EntityType = nameof(LightSystemInventory),
                EntityKey = row.LightSystemId.ToString(),
                Action = ProvenanceAction.Created,
                Actor = "system",
                ChangeSummary = JsonSerializer.Serialize(new
                {
                    foreignSourceId,
                    foreignId,
                    resolved = ownerId is not null,
                    ownerId
                })
            });
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "MMS created {Created} record(s) from {Count} segment(s), {Mapped} mapped and " +
            "{Unmapped} retained unmapped, {Resolved} of {Contexts} context(s) resolved [{CorrelationId}]",
            created, segments.Count, mapped, unmapped,
            ownerByTwin.Values.Count(v => v.OwnerId is not null), ownerByTwin.Count, envelope.BodId);

        return BodHandlingResult.Applied(segments.Count, mapped, unmapped);
    }

    /// <summary>
    /// The OWNER_ID for the context a segment asserts.
    ///
    /// The lookup is by the identifier alone rather than the source that issued it,
    /// because <see cref="MmsContextResolver"/> already scopes resolution to ENG's
    /// key space; a non-ENG context therefore resolves to nothing rather than
    /// colliding with an ENG twin that happens to share an identifier.
    ///
    /// Three outcomes are distinguished rather than collapsed into a nullable, because
    /// they call for different handling. Asserting no context at all is a legitimate
    /// state and files an unowned row. Asserting a context the registry cannot relate
    /// to an owner is not: the relation is a steward's act that has not happened yet,
    /// and filing the row anyway would put it outside every twin-scoped view where
    /// nobody would ever see it again.
    ///
    /// A registry that cannot be reached is reported as unresolvable too. MMS is in no
    /// position to tell "no relation exists" from "nobody answered", and guessing in
    /// either direction is worse than saying which happened and stopping.
    /// </summary>
    private async Task<OwnerResolution> ResolveOwnerAsync(
        Segment segment,
        Dictionary<string, OwnerResolution> cache,
        CancellationToken ct)
    {
        var site = segment.RegistrationSite;

        if (site?.IDInInfoSource is not { Length: > 0 } twinId)
        {
            return OwnerResolution.NoContext;
        }

        if (cache.TryGetValue(twinId, out var cached))
        {
            return cached;
        }

        OwnerResolution outcome;

        try
        {
            var resolution = await context.ResolveOwnerIdAsync(twinId, ct);

            outcome = resolution is { IsResolved: true, OwnerId: { } ownerId }
                ? OwnerResolution.Resolved(ownerId)
                : OwnerResolution.Unresolvable(twinId, resolution.Reason);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MMS context resolution failed for {TwinId}.", twinId);
            outcome = OwnerResolution.Unresolvable(twinId, $"the registry could not be reached ({ex.Message})");
        }

        cache[twinId] = outcome;
        return outcome;
    }

    /// <summary>
    /// What the registry could say about the context a segment asserted.
    ///
    /// <paramref name="Reason"/> is carried rather than re-derived so the rejection a
    /// steward reads is the registry's own account of why it could not answer, not a
    /// generic message written at the point of failure.
    /// </summary>
    private readonly record struct OwnerResolution(
        bool HasContext,
        long? OwnerId,
        string? Reason)
    {
        internal static readonly OwnerResolution NoContext = new(false, null, null);

        internal static OwnerResolution Resolved(long ownerId) => new(true, ownerId, null);

        internal static OwnerResolution Unresolvable(string twinId, string? reason) =>
            new(true, null, $"context {twinId} is not related to any MMS owner: " +
                            $"{reason ?? "the registry gave no reason"}");

        /// <summary>True when a context was asserted but no owner could be named.</summary>
        internal bool IsBlocked => HasContext && OwnerId is null;
    }

    /// <summary>
    /// Retains a segment's attributes against the record MMS created for it.
    ///
    /// The effective set is empty because MMS classifies nothing: it has no taxonomy to
    /// sanction a property against, so every incoming value is retained unmapped. That is
    /// the accurate description of a legacy consumer, and passing a fabricated set here
    /// would report understanding the system does not have.
    ///
    /// Transport metadata is excluded for the same reason REG-LOCATION excludes it: the
    /// class chain and the unmapped marker describe how to read the segment, not the
    /// equipment, and retaining them would misreport them as maintenance data.
    /// </summary>
    private static PropertyIngestionResult Ingest(
        ParticipantContext participant,
        ParticipantDbContext db,
        CcomAttributeMapper mapper,
        Segment segment,
        string equipmentNumber,
        string fromParticipant,
        Guid messageId)
    {
        var (incoming, _) = mapper.Extract(segment);

        var ingestible = incoming
            .Where(p => !p.DefinitionKey.StartsWith("sandbox:", StringComparison.Ordinal))
            .ToList();

        var ingestion = participant.Ingestor.Ingest(
            nameof(LightSystemInventory),
            equipmentNumber,
            ingestible,
            EffectivePropertySet.Empty,
            fromParticipant,
            messageId,
            DateTimeOffset.UtcNow);

        // Definitions inferred from the wire are stored so the value has a name to
        // display, rather than surfacing as a bare GUID nobody can interpret.
        foreach (var definition in ingestion.InferredDefinitions)
        {
            if (!db.PropertyDefinitions.Local.Any(d => d.Id == definition.Id)
                && !db.PropertyDefinitions.Any(d => d.Id == definition.Id))
            {
                db.PropertyDefinitions.Add(definition);
            }
        }

        foreach (var value in ingestion.Values)
        {
            db.PropertyValues.Add(value);
        }

        return ingestion;
    }

    /// <summary>
    /// The next LIGHT_SYSTEM_ID, allocated as MAX+1 in keeping with the sandbox's
    /// single-writer assumption. See MmsInventoryWriter for why this is not safe
    /// against a concurrently writing customer system.
    /// </summary>
    private static async Task<long> NextLightSystemIdAsync(
        ParticipantDbContext db, CancellationToken ct)
    {
        var highest = await db.Set<LightSystemInventory>()
            .MaxAsync(r => (long?)r.LightSystemId, ct) ?? 0;

        return highest + 1;
    }

    /// <summary>LIGHT_SYSTEM_CLASS_CODE_ID 9, 'Undetermined'.</summary>
    private const long UndeterminedClassCodeId = 9;

    /// <summary>ASSET_STATUS_ID 3, 'Proposed' — received but not yet surveyed.</summary>
    private const long ProposedStatusId = 3;
}

public sealed class MmsService
{
    public const string ParticipantId = "mms";

    /// <summary>CIR Entry.SourceID, matching the personality pack.</summary>
    public const string SourceId = "MMS";
}
