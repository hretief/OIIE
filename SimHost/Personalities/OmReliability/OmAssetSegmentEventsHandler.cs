using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using SimHost.Application.Bods;
using SimHost.Application.Participants;
using SimHost.Domain.Common;
using SimHost.Domain.OmReliability;
using SimHost.Infrastructure.Sql;

namespace SimHost.Personalities.OmReliability;

/// <summary>
/// Records asset installation and removal events — the receiving half of OIIE
/// Scenario 11.
///
/// This participant is thinner than MMS by design. It holds no locations, no assets,
/// and no reference data: everything it knows arrives by publication. That makes it
/// the clearest demonstration of what a purely provisioned O&amp;M system can and
/// cannot do with what it is sent.
///
/// It resolves nothing. The identifiers arrive from MMS, and until a registry
/// reconciles them the reliability system cannot tell whether the asset it just
/// heard about is one it already tracks under another name. As with MMS in uc01,
/// leaving <see cref="AssetInstallationEvent.Cirid"/> null makes that visible rather
/// than papering over it with a false match on the source identifier.
/// </summary>
public sealed class OmAssetSegmentEventsHandler(
    ILogger<OmAssetSegmentEventsHandler> logger) : IBodHandler
{
    public (string Verb, string Noun) Handles => ("Sync", "AssetSegmentEvents");

    public string? ParticipantId => OmReliabilityService.ParticipantId;

    public async Task<BodHandlingResult> HandleAsync(
        ParticipantContext participant,
        ParticipantDbContext db,
        BodEnvelope envelope,
        Guid messageId,
        CancellationToken ct)
    {
        var events = envelope.NounsAs(e => new AssetSegmentEvent(e));
        if (events.Count == 0)
        {
            return BodHandlingResult.Rejected("The BOD carried no asset segment events.");
        }

        var sourceParticipant = envelope.SenderLogicalId ?? "unknown";
        var recorded = 0;

        foreach (var evt in events)
        {
            // Idempotent on the event identity. Scenario 11 is a publication, and a
            // consumer that re-reads a message after a restart must not double-count
            // an installation — time-in-service figures would silently drift.
            var existing = await db.Set<AssetInstallationEvent>()
                .AnyAsync(e => e.FederationId == evt.UUID, ct);

            if (existing)
            {
                continue;
            }

            var attributes = ReadAttributes(evt);

            db.Set<AssetInstallationEvent>().Add(new AssetInstallationEvent
            {
                FederationId = evt.UUID,

                // Taken from the event type's own name, not inferred from the GUID.
                // An event type this system has never seen is still recorded under
                // whatever the publisher called it, rather than discarded for failing
                // to match one of two known identifiers.
                EventKind = evt.Type?.ShortName
                    ?? evt.Type?.IDInInfoSource
                    ?? evt.ShortName
                    ?? "Unknown",
                EventTypeId = evt.Type?.UUID ?? Guid.Empty,

                AssetFederationId = evt.Asset?.UUID ?? Guid.Empty,
                AssetIdInSource = evt.Asset?.IDInInfoSource,
                AssetSerialNumber = evt.Asset?.SerialNumber,
                AssetDesignation = evt.Asset?.FullName ?? evt.Asset?.ShortName,

                LocationFederationId = evt.Segment?.UUID ?? Guid.Empty,
                LocationIdInSource = evt.Segment?.IDInInfoSource,
                LocationDesignation = evt.Segment?.FullName ?? evt.Segment?.ShortName,

                OccurredAt = evt.EventDateTime?.ToDateTimeOffset(),
                PerformedBy = attributes.GetValueOrDefault("sandbox:PerformedBy"),
                WorkOrderNumber = attributes.GetValueOrDefault("sandbox:WorkOrder"),
                SourceParticipant = sourceParticipant,
                Cirid = null
            });

            db.Provenance.Add(new ProvenanceEntry
            {
                MessageId = messageId,
                EntityType = nameof(AssetInstallationEvent),
                EntityKey = evt.IDInInfoSource ?? evt.UUID.ToString(),
                Action = ProvenanceAction.Created,
                Actor = "system",
                ChangeSummary = JsonSerializer.Serialize(new
                {
                    eventKind = evt.Type?.ShortName,
                    asset = evt.Asset?.IDInInfoSource,
                    location = evt.Segment?.IDInInfoSource,
                    resolved = false
                })
            });

            recorded++;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "OM-RELIABILITY recorded {Recorded} of {Count} asset event(s) from {Source}, " +
            "none resolved [{CorrelationId}]",
            recorded, events.Count, sourceParticipant, envelope.BodId);

        return BodHandlingResult.Applied(recorded, 0, 0);
    }

    /// <summary>
    /// Flattens the event's attributes by reference-data key.
    ///
    /// Scenario 11's optional context — the agent and the work order — has no
    /// dedicated element on CCOM's AssetSegmentEvent, so it travels as attributes.
    /// </summary>
    private static Dictionary<string, string> ReadAttributes(AssetSegmentEvent evt)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var attribute in evt.Attribute)
        {
            var key = attribute.Type?.IDInInfoSource;
            var value = (attribute.ValueContent as TextContent)?.Text;

            if (key is { Length: > 0 } && value is { Length: > 0 })
            {
                result[key] = value;
            }
        }

        return result;
    }
}

public sealed class OmReliabilityService
{
    public const string ParticipantId = "om-reliability";

    public const string SourceId = "OM-RELIABILITY";
}
