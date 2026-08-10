using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using SimHost.Application.Bods;
using SimHost.Application.Classification;
using SimHost.Application.Identity;
using SimHost.Application.Participants;
using SimHost.Domain.Common;
using SimHost.Domain.Mms;
using SimHost.Infrastructure.Sql;

namespace SimHost.Personalities.Mms;

/// <summary>
/// Creates maintenance records from segments arriving on the O&amp;M channel.
///
/// MMS is the end of the chain and the participant with the least context. It
/// receives LOC-000001 from REG-LOCATION and has no idea what that is — no shared
/// key, no prior integration, nothing but a string from a system it has never heard
/// of. It stores the foreign identifier raw and leaves Cirid null.
///
/// That null is the point. Until a registry resolves it, MMS cannot tell whether
/// this is new equipment or something it already holds under a different name, and
/// a maintenance planner would eventually raise a duplicate work order. Building
/// this before the CIR exists is deliberate: the resolution only means something
/// once the problem it solves has been seen.
/// </summary>
public sealed class MmsSegmentsHandler(
    ITagIdentityService identities,
    CcomAttributeMapperFactory mappers,
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

        var nextNumber = await NextEquipmentNumberAsync(db, ct);
        var created = 0;
        var mapper = mappers.For(participant);
        var mapped = 0;
        var unmapped = 0;

        foreach (var segment in segments)
        {
            var foreignSourceId = segment.InfoSource?.ShortName ?? envelope.SenderLogicalId ?? "unknown";
            var foreignId = segment.IDInInfoSource;

            if (string.IsNullOrWhiteSpace(foreignId))
            {
                continue;
            }

            // The only match available without a registry: an exact foreign
            // identifier seen before. A second system describing the same physical
            // thing under a different identifier is invisible here.
            var existing = await db.Set<FunctionalLocationRecord>()
                .FirstOrDefaultAsync(r =>
                    r.ForeignSourceId == foreignSourceId && r.ForeignIdInSource == foreignId, ct);

            if (existing is not null)
            {
                existing.Designation = segment.FullName ?? segment.ShortName;
                existing.UpdatedAt = DateTimeOffset.UtcNow;

                var updated = Ingest(
                    participant, db, mapper, segment, existing.EquipmentNumber,
                    foreignSourceId, messageId);

                mapped += updated.MappedCount;
                unmapped += updated.UnmappedCount;
                continue;
            }

            var equipmentNumber = nextNumber.ToString();

            var record = new FunctionalLocationRecord
            {
                // Adopted, never minted. MMS is a legacy consumer, not a master of
                // identity: the sender has already said what this thing is, and
                // issuing a competing identity would be the third identity for one
                // pump. Empty when the sender asserted none, which leaves the record
                // visibly unidentified rather than falsely identified.
                FederationId = segment.UUID,
                EquipmentNumber = equipmentNumber,
                Designation = segment.FullName ?? segment.ShortName,
                PlannerGroup = "MP1",
                CostCentre = "CC-4400",
                ForeignSourceId = foreignSourceId,
                ForeignIdInSource = foreignId,
                Cirid = null
            };

            nextNumber++;
            created++;

            db.Set<FunctionalLocationRecord>().Add(record);

            // MMS keeps what it was sent, understood or not.
            //
            // It has no RDL of its own to speak of, so almost everything arrives
            // unmapped — and that is the honest result rather than a reason to discard.
            // A maintenance system that silently drops the control action because it has
            // no column for it is the failure this whole model argues against; retaining
            // it flagged means the value is still there when someone teaches MMS what it
            // means.
            var ingestion = Ingest(
                participant, db, mapper, segment, record.EquipmentNumber,
                foreignSourceId, messageId);

            mapped += ingestion.MappedCount;
            unmapped += ingestion.UnmappedCount;

            // The legacy equipment number is registered against the adopted identity.
            // This is the case the whole model is for: a system with its own numbering
            // that predates any federation, joined to the identity rather than
            // renumbered to match it.
            if (record.FederationId != Guid.Empty)
            {
                var assignment = identities.RegisterCode(
                    record.FederationId, MmsService.ParticipantId, equipmentNumber);
                assignment.AdoptedFromRemote = true;
                db.Codes.Add(assignment);
            }

            db.Provenance.Add(new ProvenanceEntry
            {
                MessageId = messageId,
                EntityType = nameof(FunctionalLocationRecord),
                EntityKey = record.EquipmentNumber,
                Action = ProvenanceAction.Created,
                Actor = "system",
                ChangeSummary = JsonSerializer.Serialize(new
                {
                    foreignSourceId,
                    foreignId,
                    resolved = false
                })
            });
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "MMS created {Created} record(s) from {Count} segment(s), {Mapped} mapped and " +
            "{Unmapped} retained unmapped, none resolved [{CorrelationId}]",
            created, segments.Count, mapped, unmapped, envelope.BodId);

        return BodHandlingResult.Applied(segments.Count, mapped, unmapped);
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
            nameof(FunctionalLocationRecord),
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
    /// Legacy numeric keys from the system's own sequence. Starting at 234441 so the
    /// first record is 234441 rather than 1 — a key that looks like a real
    /// maintenance system's, not like a demo's.
    /// </summary>
    private static async Task<int> NextEquipmentNumberAsync(
        ParticipantDbContext db, CancellationToken ct)
    {
        var numbers = await db.Set<FunctionalLocationRecord>()
            .Select(r => r.EquipmentNumber)
            .ToListAsync(ct);

        var highest = numbers
            .Select(n => int.TryParse(n, out var value) ? value : 0)
            .DefaultIfEmpty(234440)
            .Max();

        return highest + 1;
    }
}

public sealed class MmsService
{
    public const string ParticipantId = "mms";

    /// <summary>CIR Entry.SourceID, matching the personality pack.</summary>
    public const string SourceId = "MMS";
}
