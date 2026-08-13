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
/// Records relationships between functional locations arriving on the O&amp;M channel.
///
/// A maintenance planner needs the topology, not just the list: a pump that has lost
/// its power supply is a different job from a pump with a failed bearing, and without
/// the edge the two look identical in the work order.
///
/// Endpoints are kept as the registry's codes rather than translated into MMS's own
/// equipment numbers. MMS originated neither end and has no standing to restate the
/// relationship in its own vocabulary — the same reason it stores a foreign
/// identifier raw instead of resolving it. Joining an edge to local records is the
/// registry's job via <see cref="FunctionalLocationRecord.ForeignIdInSource"/>, and
/// an edge naming a location MMS has not been told about yet is stored anyway rather
/// than dropped: message order is not something a receiver should depend on, and the
/// segment may simply be behind it.
/// </summary>
public sealed class MmsSegmentConnectionsHandler(
    ILogger<MmsSegmentConnectionsHandler> logger) : IBodHandler
{
    public (string Verb, string Noun) Handles => ("Sync", "SegmentMeshConnections");

    public string? ParticipantId => MmsService.ParticipantId;

    public async Task<BodHandlingResult> HandleAsync(
        ParticipantContext participant,
        ParticipantDbContext db,
        BodEnvelope envelope,
        Guid messageId,
        CancellationToken ct)
    {
        var meshes = envelope.NounsAs(e => new SegmentMesh(e));

        // The mesh is the wire envelope CCOM requires for connections, not something
        // MMS models, so it is unwrapped and discarded exactly as at the registry.
        var connections = meshes.SelectMany(m => m.Connection).ToList();

        if (connections.Count == 0)
        {
            return BodHandlingResult.Rejected("The BOD carried no connections.");
        }

        var foreignSourceId = envelope.SenderLogicalId ?? "unknown";
        var applied = 0;
        var malformed = new List<string>();

        foreach (var connection in connections)
        {
            var fromId = connection.From?.IDInInfoSource;
            var toId = connection.To?.IDInInfoSource;

            if (string.IsNullOrWhiteSpace(fromId) || string.IsNullOrWhiteSpace(toId))
            {
                malformed.Add($"{fromId ?? "?"} -> {toId ?? "?"}");
                continue;
            }

            var federationId = connection.UUID;

            var existing = federationId != Guid.Empty
                ? await db.Set<LocationRelationshipRecord>()
                    .FirstOrDefaultAsync(r => r.FederationId == federationId, ct)
                : await db.Set<LocationRelationshipRecord>().FirstOrDefaultAsync(
                    r => r.FromLocationId == fromId
                        && r.ToLocationId == toId
                        && r.TypeKey == connection.Type!.IDInInfoSource, ct);

            var record = existing ?? new LocationRelationshipRecord
            {
                FederationId = federationId,
                ForeignSourceId = connection.From?.InfoSource?.ShortName ?? foreignSourceId
            };

            record.FromLocationId = fromId;
            record.ToLocationId = toId;
            record.TypeKey = connection.Type?.IDInInfoSource ?? string.Empty;
            // Both readings are kept so a planner looking at either end sees the
            // relationship described the right way round.
            record.ForwardRole = connection.Type?.ShortName;
            record.InverseRole = connection.Type?.Description;
            record.UpdatedAt = DateTimeOffset.UtcNow;

            if (existing is null)
            {
                db.Set<LocationRelationshipRecord>().Add(record);
            }

            db.Provenance.Add(new ProvenanceEntry
            {
                MessageId = messageId,
                EntityType = nameof(LocationRelationshipRecord),
                EntityKey = $"{fromId}->{toId}",
                Action = existing is null ? ProvenanceAction.Created : ProvenanceAction.Updated,
                Actor = foreignSourceId,
                ChangeSummary = JsonSerializer.Serialize(new
                {
                    from = fromId,
                    to = toId,
                    typeKey = record.TypeKey,
                    record.ForwardRole,
                    record.InverseRole
                })
            });

            applied++;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "MMS recorded {Count} location relationship(s) [{CorrelationId}]",
            applied, envelope.BodId);

        return malformed.Count > 0
            ? BodHandlingResult.Rejected(
                $"Accepted {applied} connection(s); rejected {malformed.Count} naming no endpoint: " +
                string.Join("; ", malformed))
            : BodHandlingResult.Applied(applied, 0, 0);
    }
}
