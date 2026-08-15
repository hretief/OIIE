using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Oiie.Ccom.Types;
using SimHost.Application.Identity;
using SimHost.Application.Scenarios;
using SimHost.Domain.Common;
using SimHost.Domain.Mms;
using SimHost.Infrastructure.Sql;

namespace SimHost.Personalities.Mms;

public sealed record WorkOrderResult(
    bool Published,
    string OrderNumber,
    string EquipmentNumber,
    string FunctionalLocationNumber,
    string EventKind,
    IReadOnlyList<string> Findings);

/// <summary>
/// MMS's maintenance workflow, and the trigger for OIIE Scenario 11.
///
/// This is the first thing MMS publishes. Through phase 1 it only ever consumed:
/// engineering structure arrived from REG-LOCATION and MMS was the end of the chain.
/// Asset installation inverts that, because the technician's entry into the
/// maintenance system is the only record that a physical change happened at all —
/// no design authority and no registry can know it.
///
/// Publication happens on sign-off rather than on completion of the physical work.
/// The two are separated by an audit step in the use case, and publishing at
/// completion would mean broadcasting configuration changes that a subsequent
/// rejection would have to retract — retraction being a far harder problem for
/// every downstream consumer than waiting.
/// </summary>
public sealed class MmsWorkOrderService(
    IParticipantDbContextFactory factory,
    ITagIdentityService identities,
    ScenarioRunContext runContext,
    ILogger<MmsWorkOrderService> logger)
{
    /// <summary>
    /// Registers a serialised asset MMS itself originates.
    ///
    /// MMS mints identity here, which is not a reversal of the rule that it never
    /// mints. That rule concerns things other systems already own: issuing a fresh
    /// identity for a functional location ENG designed would create a second identity
    /// for one location. A serialised asset read off a nameplate in the field has no
    /// prior owner, so declining to identify it would leave downstream consumers with
    /// an asset they cannot refer to at all.
    /// </summary>
    public async Task<EquipmentRecord> RegisterEquipmentAsync(
        string equipmentNumber,
        string? designation,
        string? serialNumber,
        string? modelNumber,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(equipmentNumber);

        await using var db = factory.Create(MmsService.ParticipantId);

        var existing = await db.Set<EquipmentRecord>()
            .FirstOrDefaultAsync(e => e.EquipmentNumber == equipmentNumber, ct);

        if (existing is not null)
        {
            existing.Designation = designation ?? existing.Designation;
            existing.SerialNumber = serialNumber ?? existing.SerialNumber;
            existing.ModelNumber = modelNumber ?? existing.ModelNumber;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        // Derived from the serial number where there is one, so re-running a scenario
        // identifies the same physical asset rather than minting a new identity for it
        // on every reset. A random Guid would make the run view churn for no reason.
        var federationId = CcomUuid.FromKey(
            "Asset", $"{MmsService.SourceId}\u001f{serialNumber ?? equipmentNumber}");

        var record = new EquipmentRecord
        {
            EquipmentNumber = equipmentNumber,
            FederationId = federationId,
            Designation = designation,
            SerialNumber = serialNumber,
            ModelNumber = modelNumber
        };

        db.Set<EquipmentRecord>().Add(record);

        var assignment = identities.RegisterCode(
            federationId, MmsService.ParticipantId, equipmentNumber);
        db.Codes.Add(assignment);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "MMS registered equipment {EquipmentNumber} as {FederationId}",
            equipmentNumber, federationId);

        return record;
    }

    /// <summary>
    /// Raises a work order against an asset and a functional location.
    ///
    /// The location must already be known to MMS. That is a real constraint rather
    /// than defensive coding: Use Case 5 is explicitly predicated on Use Cases 1 and
    /// 10 having populated the maintenance system first, and a work order against a
    /// location MMS has never heard of would be a data-entry error in the field.
    ///
    /// <paramref name="functionalLocation"/> may be MMS's own equipment number, the
    /// foreign identifier it was provisioned under, or the designation it was
    /// provisioned with. All three are accepted because MMS mints its location
    /// numbers on receipt, so nothing outside MMS — including a scenario file — can
    /// know the number in advance; and a planner in the real system searches by the
    /// engineering tag on the drawing, not by MMS's surrogate key or by the
    /// registry's identifier, neither of which appears on the work request.
    /// </summary>
    public async Task<WorkOrder> RaiseAsync(
        string orderNumber,
        AssetEventKind eventKind,
        string equipmentNumber,
        string functionalLocation,
        string? description,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderNumber);

        await using var db = factory.Create(MmsService.ParticipantId);

        // Matched by LIGHT_SYSTEM_ID or by its alternate key. The foreign-identifier
        // match that used to be possible here is gone: the real schema has no column
        // for one, so a caller quoting the sender's identifier can no longer be
        // served without going through ws-CIR.
        var location = await db.Set<LightSystemInventory>()
            .FirstOrDefaultAsync(
                l => l.LightSystemId.ToString() == functionalLocation
                    || l.LightSystemName == functionalLocation, ct);

        if (location is null)
        {
            throw new InvalidOperationException(
                $"MMS holds no light system '{functionalLocation}'. " +
                "Use Case 5 assumes Use Case 1 or 10 has provisioned it first.");
        }

        var existing = await db.Set<WorkOrder>()
            .FirstOrDefaultAsync(w => w.OrderNumber == orderNumber, ct);

        if (existing is not null)
        {
            return existing;
        }

        var order = new WorkOrder
        {
            OrderNumber = orderNumber,
            EventKind = eventKind,
            State = WorkOrderState.Open,
            EquipmentNumber = equipmentNumber,

            // Stored as MMS's own key regardless of how it was named here, so the
            // work order refers to the location the way MMS does.
            FunctionalLocationNumber = location.LightSystemId.ToString(),
            Description = description
        };

        db.Set<WorkOrder>().Add(order);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "MMS raised work order {OrderNumber} to {EventKind} {Equipment} at {Location}",
            orderNumber, eventKind, equipmentNumber, order.FunctionalLocationNumber);

        return order;
    }

    /// <summary>
    /// Records that the technician physically did the work.
    ///
    /// Scenario 10 would have an I&amp;C Device Monitoring System independently sense
    /// the change here and let MMS reconcile it against recent orders. That scenario
    /// is out of scope, so this is the technician's unverified word — which is
    /// precisely the gap Scenario 10 exists to close, left visible rather than
    /// papered over with an automatic confirmation nothing actually observed.
    /// </summary>
    public async Task<WorkOrder> CompleteAsync(
        string orderNumber,
        DateTimeOffset occurredAt,
        string? performedBy,
        CancellationToken ct = default)
    {
        await using var db = factory.Create(MmsService.ParticipantId);

        var order = await db.Set<WorkOrder>()
            .FirstOrDefaultAsync(w => w.OrderNumber == orderNumber, ct)
            ?? throw new InvalidOperationException($"No work order '{orderNumber}'.");

        order.State = WorkOrderState.Completed;
        order.OccurredAt = occurredAt;
        order.PerformedBy = performedBy;

        await db.SaveChangesAsync(ct);
        return order;
    }

    /// <summary>
    /// Signs off a completed work order, applies the configuration change, and
    /// queues the Scenario 11 publication.
    ///
    /// The asset's installed location and the outbox row commit in one transaction.
    /// Splitting them would let MMS believe an asset had moved while the message
    /// saying so was lost, and no downstream system would ever learn of it.
    /// </summary>
    public async Task<WorkOrderResult> SignOffAsync(
        string orderNumber,
        string channelUri,
        string? topic,
        string signedOffBy,
        CancellationToken ct = default)
    {
        await using var db = factory.Create(MmsService.ParticipantId);

        var order = await db.Set<WorkOrder>()
            .FirstOrDefaultAsync(w => w.OrderNumber == orderNumber, ct)
            ?? throw new InvalidOperationException($"No work order '{orderNumber}'.");

        if (order.State != WorkOrderState.Completed)
        {
            return new WorkOrderResult(
                false, orderNumber, order.EquipmentNumber, order.FunctionalLocationNumber,
                order.EventKind.ToString(),
                [$"Work order '{orderNumber}' is {order.State}, not Completed."]);
        }

        var equipment = await db.Set<EquipmentRecord>()
            .FirstOrDefaultAsync(e => e.EquipmentNumber == order.EquipmentNumber, ct);

        if (equipment is null)
        {
            return new WorkOrderResult(
                false, orderNumber, order.EquipmentNumber, order.FunctionalLocationNumber,
                order.EventKind.ToString(),
                [$"MMS holds no equipment '{order.EquipmentNumber}'."]);
        }

        // The configuration change itself. On removal the asset is installed nowhere,
        // which is a real state and not missing data — it is in the workshop.
        equipment.FunctionalLocationNumber = order.EventKind == AssetEventKind.Install
            ? order.FunctionalLocationNumber
            : null;
        equipment.UpdatedAt = DateTimeOffset.UtcNow;

        order.State = WorkOrderState.SignedOff;
        order.SignedOffAt = DateTimeOffset.UtcNow;
        order.SignedOffBy = signedOffBy;

        var correlationId = Guid.NewGuid().ToString();

        db.Outbox.Add(new OutboxItem
        {
            ContainerType = nameof(WorkOrder),
            ContainerKey = order.OrderNumber,
            EntityType = nameof(WorkOrder),
            EntityKeys = JsonSerializer.Serialize(new[] { order.OrderNumber }),
            ChangeKind = ChangeKind.Add,
            Verb = "Sync",
            Noun = "AssetSegmentEvents",
            Pattern = MessagePattern.Publication,
            ChannelUri = channelUri,
            Topic = topic,
            CorrelationId = correlationId,
            State = OutboxState.Pending,
            ScenarioRunId = runContext.CurrentRunId
        });

        db.Provenance.Add(new ProvenanceEntry
        {
            EntityType = nameof(EquipmentRecord),
            EntityKey = equipment.EquipmentNumber,
            Action = ProvenanceAction.Updated,
            Actor = signedOffBy,
            ChangeSummary = JsonSerializer.Serialize(new
            {
                order.OrderNumber,
                eventKind = order.EventKind.ToString(),
                installedAt = equipment.FunctionalLocationNumber
            })
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "MMS signed off {OrderNumber}: {EventKind} of {Equipment} at {Location} [{CorrelationId}]",
            orderNumber, order.EventKind, order.EquipmentNumber,
            order.FunctionalLocationNumber, correlationId);

        return new WorkOrderResult(
            true, orderNumber, order.EquipmentNumber, order.FunctionalLocationNumber,
            order.EventKind.ToString(), []);
    }
}
