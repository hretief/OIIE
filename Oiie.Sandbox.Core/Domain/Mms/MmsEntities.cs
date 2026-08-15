namespace SimHost.Domain.Mms;

/// <summary>
/// A serialised physical asset as the sandbox models it.
///
/// NOTE: this is NOT customer schema. No equipment table has been supplied, and
/// LIGHT_SYSTEM_INVENTORY has no serial number or nameplate data to stand in for
/// one. It is retained only so Scenario 11's install/removal machinery keeps
/// working until the real work-order and equipment tables are known, and it must
/// not be mistaken for something the customer has.
///
/// Because it is ours rather than theirs, it may hold a FederationId: MMS genuinely
/// originates a serialised asset read off a nameplate, and nothing upstream has
/// ever named it. That is the opposite of the LIGHT_SYSTEM_INVENTORY case, where
/// the row describes something other systems already know about and identity must
/// therefore come from the registry.
/// </summary>
public class EquipmentRecord
{
    public long Id { get; set; }

    public string EquipmentNumber { get; set; } = string.Empty;

    public Guid FederationId { get; set; }

    public string? Designation { get; set; }

    /// <summary>
    /// The LIGHT_SYSTEM_ID this asset is installed at, as a string, or null when
    /// removed and not yet reinstalled. Null is a real state: an asset in the
    /// workshop is genuinely installed nowhere.
    /// </summary>
    public string? FunctionalLocationNumber { get; set; }

    public string? SerialNumber { get; set; }

    public string? ModelNumber { get; set; }

    public Guid? Cirid { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A light system as the maintenance system actually records it.
///
/// This maps dbo.LIGHT_SYSTEM_INVENTORY column for column, and the absences are
/// the significant part. There is no FederationId, no Cirid and no foreign
/// identifier, because the customer schema has no column for any of them and we
/// may not add one. Adding a column here would put a second, competing identity
/// inside a schema that is not ours — which is the mistake DR-008 records.
/// Shared identity therefore lives only in ws-CIR, registered against
/// LIGHT_SYSTEM_ID and resolved on read.
/// </summary>
public class LightSystemInventory
{
    /// <summary>
    /// LIGHT_SYSTEM_ID. The only key MMS has, so it is also the only thing CIR can
    /// register an entry against.
    /// </summary>
    public long LightSystemId { get; set; }

    public string LightSystemName { get; set; } = string.Empty;

    public long LightSystemClassCodeId { get; set; }

    public long? LightSystemStatusId { get; set; }

    /// <summary>
    /// OWNER_ID — MMS's context key, the counterpart of an iTwin id in ENG.
    ///
    /// Nullable in the real schema, and that null is a genuine state rather than
    /// missing data: a row with no owner can never resolve to a twin. Such rows
    /// should surface as explicitly context-less, not vanish from a filtered view,
    /// because silently dropping them would misreport the inventory as smaller
    /// than it is.
    /// </summary>
    public long? OwnerId { get; set; }
}

/// <summary>
/// dbo.LIGHT_SYSTEM_CLASS_CODE. Reference data owned by the customer; the sandbox
/// reads it and never writes it.
/// </summary>
public class LightSystemClassCode
{
    public long LightSystemClassCodeId { get; set; }

    public string LightSystemClassCodeName { get; set; } = string.Empty;

    public bool ActiveFlag { get; set; } = true;

    public string? UserUpdate { get; set; }

    public DateTime? DateUpdate { get; set; }
}

/// <summary>
/// dbo.SETUP_ASSET_STATUS. Shared across asset types, which is why it is not
/// prefixed LIGHT_SYSTEM_.
/// </summary>
public class SetupAssetStatus
{
    public long AssetStatusId { get; set; }

    public string AssetStatusName { get; set; } = string.Empty;

    public bool ActiveFlag { get; set; } = true;

    public string? UserUpdate { get; set; }

    public DateTime? DateUpdate { get; set; }
}

/// <summary>
/// dbo.SETUP_OWNER — the districts and units that own inventory.
///
/// This is the table an iTwin resolves to. The resolution is registry-mediated:
/// nothing here records which twin an owner corresponds to, and nothing should.
/// </summary>
public class SetupOwner
{
    public long OwnerId { get; set; }

    public string OwnerName { get; set; } = string.Empty;

    public bool ActiveFlag { get; set; } = true;

    public string? UserUpdate { get; set; }

    public DateTime? DateUpdate { get; set; }
}

/// <summary>Whether a work order installed an asset or removed one.</summary>
public enum AssetEventKind { Install, Removal }

/// <summary>
/// Work order state. Scenario 11 publishes on sign-off, not on completion of the
/// physical work: the two differ by an audit step, and publishing early would tell
/// O&amp;M systems something that a subsequent rejection would have to retract.
/// </summary>
public enum WorkOrderState { Open, Completed, SignedOff }

/// <summary>
/// A maintenance work order for the removal or installation of a serialised asset.
///
/// This is the trigger for OIIE Scenario 11. The use case describes a longer
/// business process — work request, planning, approval, technician hand-off — but
/// the only part with an interoperability consequence is the completed, signed-off
/// order, which is what gets published. The earlier stages are modelled just enough
/// to make the sign-off meaningful rather than instantaneous.
///
/// Scenario 10 would additionally have an I&amp;C Device Monitoring System sense the
/// change and let MMS reconcile it against recent orders. That is deliberately
/// absent: without it, MMS publishes on the technician's word alone, which is
/// exactly the unverified case that Scenario 10 exists to improve on.
///
/// NOTE: no customer work-order table has been supplied, so this is a sandbox-only
/// construct. Scenario 11 is parked pending the real table names; it is retained
/// rather than deleted so the scenario can be revived without being rebuilt.
/// </summary>
public class WorkOrder
{
    public long Id { get; set; }

    public string OrderNumber { get; set; } = string.Empty;

    public AssetEventKind EventKind { get; set; }

    public WorkOrderState State { get; set; } = WorkOrderState.Open;

    /// <summary>The serialised asset installed or removed.</summary>
    public string EquipmentNumber { get; set; } = string.Empty;

    /// <summary>The functional location it was installed at or removed from.</summary>
    public string FunctionalLocationNumber { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>
    /// When the physical work happened — not when the row was written. Scenario 11
    /// requires the timestamp of the installation or removal itself, and on a
    /// paper-then-keyboard workflow those can be days apart.
    /// </summary>
    public DateTimeOffset? OccurredAt { get; set; }

    /// <summary>The technician who performed the work. Optional context per Scenario 11.</summary>
    public string? PerformedBy { get; set; }

    public DateTimeOffset? SignedOffAt { get; set; }

    public string? SignedOffBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A relationship between two functional locations, as told to MMS by the registry.
///
/// MMS holds this because a maintenance planner needs to know what feeds what: a
/// pump losing its power supply is a different job from a pump with a failed
/// bearing, and without the edge the two are indistinguishable in the work order.
///
/// Endpoints are held as the registry's codes rather than resolved to MMS's own
/// equipment numbers. MMS did not originate either end and has no authority to
/// restate the relationship in its own vocabulary; the codes are what it was told,
/// and ws-CIR resolution is how they join to anything local. Storing an edge whose
/// ends MMS cannot yet see is therefore normal, not an error.
///
/// NOTE: no customer table has been supplied for this yet, so it remains a
/// sandbox-only construct and is not part of the real MMS schema.
/// </summary>
public class LocationRelationshipRecord
{
    public long Id { get; set; }

    /// <summary>
    /// The edge's identity as adopted from the sender, never reminted — the same
    /// rule that governs <see cref="EquipmentRecord.FederationId"/>.
    /// </summary>
    public Guid FederationId { get; set; }

    /// <summary>The registry's code for the source end, e.g. LOC-000002.</summary>
    public string FromLocationId { get; set; } = string.Empty;

    /// <summary>The registry's code for the sink end.</summary>
    public string ToLocationId { get; set; } = string.Empty;

    /// <summary>The kind of relationship, e.g. eng:Supplies.</summary>
    public string TypeKey { get; set; } = string.Empty;

    /// <summary>Reading from source to sink, e.g. "Supplies".</summary>
    public string? ForwardRole { get; set; }

    /// <summary>Reading from sink to source, e.g. "Supplied By".</summary>
    public string? InverseRole { get; set; }

    /// <summary>The system that stated the relationship, e.g. REG-LOCATION.</summary>
    public string? ForeignSourceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
