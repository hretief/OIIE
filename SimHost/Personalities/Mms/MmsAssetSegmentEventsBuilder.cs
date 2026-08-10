using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Oiie.Ccom.Bods;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using SimHost.Application.Bods;
using SimHost.Application.Participants;
using SimHost.Domain.Common;
using SimHost.Domain.Mms;
using SimHost.Infrastructure.Sql;

namespace SimHost.Personalities.Mms;

/// <summary>
/// Publishes asset installation and removal events — OIIE Scenario 11.
///
/// The BOD is built from signed-off work orders, so what goes on the wire is
/// derived from committed maintenance records rather than authored alongside them.
///
/// The payload is deliberately close to the scenario's stated minimum: the
/// functional location, the serialised asset, and the timestamp of the event, with
/// the agent and work order carried as the optional context the scenario names.
/// Sending MMS's whole asset record instead would be easy and would misrepresent the
/// scenario as a data-synchronisation flow, when it is a notification that something
/// physically changed.
/// </summary>
public sealed class MmsAssetSegmentEventsBuilder : IBodBuilder
{
    /// <summary>
    /// The CCOM reference-data identifiers named by OIIE Scenario 11.
    ///
    /// Hard-coded because they are published constants of the standard, not sandbox
    /// choices. Deriving them via <see cref="CcomUuid"/> as the sandbox does for its
    /// own reference data would produce stable, self-consistent, and wrong values —
    /// a real consumer keyed on the MIMOSA identifiers would silently fail to
    /// recognise the event type.
    /// </summary>
    private static readonly Guid InstallEventType =
        Guid.Parse("ecc99353-412b-4995-bd71-1cbc6fc16c7c");

    private static readonly Guid RemovalEventType =
        Guid.Parse("3a45e126-b234-42a0-b3b1-07c29522d02d");

    public (string Verb, string Noun) Handles => ("Sync", "AssetSegmentEvents");

    public string? ParticipantId => MmsService.ParticipantId;

    public async Task<XDocument> BuildAsync(
        ParticipantContext participant,
        ParticipantDbContext db,
        OutboxItem item,
        CancellationToken ct)
    {
        var orderNumbers = JsonSerializer.Deserialize<List<string>>(item.EntityKeys) ?? [];

        var orders = await db.Set<WorkOrder>()
            .AsNoTracking()
            .Where(w => orderNumbers.Contains(w.OrderNumber))
            .OrderBy(w => w.OrderNumber)
            .ToListAsync(ct);

        var bod = new SyncAssetSegmentEvents(ActionCodes.Add);

        bod.ApplicationArea.BODID = item.CorrelationId;
        bod.ApplicationArea.Sender = new Sender
        {
            LogicalID = participant.Config.LogicalId,
            ComponentID = "SimHost",
            ReferenceID = item.ContainerKey
        };

        var infoSource = new InfoSource
        {
            UUID = CcomUuid.ForInfoSource(participant.Config.SourceId),
            ShortName = participant.Config.SourceId
        };

        var mimosa = new InfoSource
        {
            UUID = CcomUuid.ForInfoSource("MIMOSA"),
            ShortName = "MIMOSA"
        };

        foreach (var order in orders)
        {
            var equipment = await db.Set<EquipmentRecord>()
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.EquipmentNumber == order.EquipmentNumber, ct);

            var location = await db.Set<FunctionalLocationRecord>()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    l => l.EquipmentNumber == order.FunctionalLocationNumber, ct);

            var install = order.EventKind == AssetEventKind.Install;

            var evt = new AssetSegmentEvent
            {
                // Identity of the event, derived from the work order so a re-run
                // republishes the same event rather than inventing a new one.
                UUID = CcomUuid.FromKey(
                    "AssetSegmentEvent", $"{participant.Config.SourceId}\u001f{order.OrderNumber}"),
                IDInInfoSource = order.OrderNumber,
                InfoSource = infoSource,
                ShortName = install ? "Install" : "Removal",
                Description = order.Description,
                Type = new EventType
                {
                    UUID = install ? InstallEventType : RemovalEventType,
                    IDInInfoSource = install
                        ? "Installation of Asset on Segment"
                        : "Removal of Asset on Segment",
                    InfoSource = mimosa,
                    ShortName = install ? "Install" : "Removal"
                },

                // The scenario's mandatory timestamp is when the work happened, not
                // when this message was built. Falling back to sign-off time would be
                // wrong by however long the audit took.
                EventDateTime = order.OccurredAt is { } occurred
                    ? Instant.FromDateTimeOffset(occurred)
                    : null
            };

            if (equipment is not null)
            {
                evt.Asset = new Asset
                {
                    UUID = equipment.FederationId,
                    IDInInfoSource = equipment.EquipmentNumber,
                    InfoSource = infoSource,
                    ShortName = equipment.EquipmentNumber,
                    FullName = equipment.Designation,
                    SerialNumber = equipment.SerialNumber
                };

                if (equipment.ModelNumber is { Length: > 0 })
                {
                    evt.Asset.Model = new Model
                    {
                        UUID = CcomUuid.FromKey("Model", equipment.ModelNumber),
                        IDInInfoSource = equipment.ModelNumber,
                        InfoSource = infoSource,
                        ModelNumber = equipment.ModelNumber
                    };
                }
            }

            if (location is not null)
            {
                // The identity MMS adopted when REG-LOCATION told it about this
                // location, not MMS's own equipment number. Sending the legacy number
                // as the identity would hand the consumer a code only MMS can resolve,
                // and the whole point of carrying the FederationId is that the
                // consumer can recognise the location without asking anyone.
                evt.Segment = new Segment
                {
                    UUID = location.FederationId,
                    IDInInfoSource = location.EquipmentNumber,
                    InfoSource = infoSource,
                    ShortName = location.EquipmentNumber,
                    FullName = location.Designation
                };
            }

            // Optional Scenario 11 context: the agent and the calendared work order.
            // Carried as attributes because CCOM's AssetSegmentEvent has no dedicated
            // element for either, and inventing elements would break schema validity.
            if (order.PerformedBy is { Length: > 0 })
            {
                evt.Attribute.Add(Context(evt.UUID, "sandbox:PerformedBy", "Performed by", order.PerformedBy));
            }

            evt.Attribute.Add(Context(evt.UUID, "sandbox:WorkOrder", "Work order", order.OrderNumber));

            bod.With(evt);
        }

        return bod.CreateDocument();
    }

    private static Oiie.Ccom.Types.Attribute Context(
        Guid owner, string key, string name, string value) => new()
        {
            UUID = CcomUuid.ForValue(owner, key),
            ShortName = name,
            Type = new AttributeType
            {
                UUID = CcomUuid.ForReferenceData(null, key),
                IDInInfoSource = key,
                ShortName = name
            },
            ValueContent = new TextContent { Text = value }
        };
}
