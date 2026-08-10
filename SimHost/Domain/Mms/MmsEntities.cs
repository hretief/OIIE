namespace SimHost.Domain.Mms;

/// <summary>
/// A functional location as the maintenance system knows it.
///
/// MMS is a legacy system with legacy keys: numeric, meaningless, and assigned by
/// its own sequence. It has no idea what LOC-000001 or TIC-106 are, which is the
/// entire point — three systems, three identifiers, one physical thing.
/// </summary>
public class FunctionalLocationRecord
{
    public long Id { get; set; }

    /// <summary>
    /// The identity as adopted from the inbound message. MMS never mints: it is not a
    /// master of identity, it is a legacy system holding its own codes for things
    /// other people originated. Empty means nothing has told it what this is yet.
    /// </summary>
    public Guid FederationId { get; set; }

    /// <summary>
    /// MMS's legacy code, e.g. 234443. Registered against the FederationId rather
    /// than standing in for it — this is exactly the legacy-code case CIR resolves.
    /// </summary>
    public string EquipmentNumber { get; set; } = string.Empty;

    public string? Designation { get; set; }

    public string? CostCentre { get; set; }

    public string? PlannerGroup { get; set; }

    /// <summary>
    /// Foreign identifier as received, kept raw. Until something resolves it, this
    /// is all MMS has — a string from a system it has never heard of.
    /// </summary>
    public string? ForeignSourceId { get; set; }

    public string? ForeignIdInSource { get; set; }

    /// <summary>
    /// Set once the registry resolves the foreign identifier to a shared identity.
    /// Null means unresolved, which is visibly different from having no identity.
    /// </summary>
    public Guid? Cirid { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A serialised physical asset as the maintenance system knows it.
///
/// Unlike <see cref="FunctionalLocationRecord"/>, this is a thing MMS genuinely
/// originates: the serial number is read off a nameplate by a technician, and no
/// upstream system has ever named it. So MMS does mint a FederationId here, which is
/// not a contradiction of "MMS never mints" — that rule is about identity for things
/// other systems already own. The distinction matters: minting for a foreign
/// location would create a competing identity, whereas refusing to mint for an asset
/// only it has seen would leave the asset unidentifiable downstream.
/// </summary>
public class EquipmentRecord
{
    public long Id { get; set; }

    public string EquipmentNumber { get; set; } = string.Empty;

    /// <summary>
    /// Minted by MMS, because MMS is the originating system for serialised assets.
    /// </summary>
    public Guid FederationId { get; set; }

    public string? Designation { get; set; }

    /// <summary>
    /// The location this asset is currently installed at, or null when removed and
    /// not yet reinstalled. Null is a real state, not missing data — an asset in the
    /// workshop is genuinely installed nowhere.
    /// </summary>
    public string? FunctionalLocationNumber { get; set; }

    public string? SerialNumber { get; set; }

    public string? ModelNumber { get; set; }

    public Guid? Cirid { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
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
