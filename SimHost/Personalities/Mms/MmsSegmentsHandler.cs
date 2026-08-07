using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using SimHost.Application.Bods;
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
public sealed class MmsSegmentsHandler(ILogger<MmsSegmentsHandler> logger) : IBodHandler
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
                continue;
            }

            var record = new FunctionalLocationRecord
            {
                EquipmentNumber = nextNumber.ToString(),
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
            "MMS created {Created} record(s) from {Count} segment(s), none resolved [{CorrelationId}]",
            created, segments.Count, envelope.BodId);

        return BodHandlingResult.Applied(segments.Count, 0, 0);
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
}
