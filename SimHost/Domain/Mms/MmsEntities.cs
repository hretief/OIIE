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

/// <summary>Equipment installed at a functional location. Populated in a later increment.</summary>
public class EquipmentRecord
{
    public long Id { get; set; }

    public string EquipmentNumber { get; set; } = string.Empty;

    public string? Designation { get; set; }

    public string? FunctionalLocationNumber { get; set; }

    public string? SerialNumber { get; set; }

    public Guid? Cirid { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
