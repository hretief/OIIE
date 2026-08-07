namespace SimHost.Domain.RegLocation;

public enum StewardshipState { Proposed, Approved, Rejected }

/// <summary>
/// A functional location in the authoritative model.
///
/// REG-LOCATION's own vocabulary and its own key series: an incoming ENG tag
/// TIC-106 becomes LOC-000412 here. The registry does not adopt the source's
/// identifier, which is precisely why something has to reconcile the two.
/// </summary>
public class Location
{
    public long Id { get; set; }

    /// <summary>Registry surrogate, e.g. LOC-000412.</summary>
    public string LocationCode { get; set; } = string.Empty;

    public string? Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Reference-data class as bound locally, which may be an ancestor of what the sender sent.</summary>
    public string? ClassKey { get; set; }

    /// <summary>Set when the sender classified more specifically than this participant understands.</summary>
    public string? RequestedClassKey { get; set; }

    public string? Area { get; set; }

    /// <summary>Where it came from. Retained so provenance survives independently of the message archive.</summary>
    public string SourceParticipant { get; set; } = string.Empty;

    public string SourceIdentifier { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class LocationParent
{
    public long Id { get; set; }

    public string ParentLocationCode { get; set; } = string.Empty;

    public string ChildLocationCode { get; set; } = string.Empty;
}

/// <summary>
/// A proposed change awaiting a steward.
///
/// REG-LOCATION is a governance gate rather than a relay: arrival is not
/// acceptance, and the steward's approval is what admits data to the authoritative
/// model and triggers republication.
/// </summary>
public class StewardshipItem
{
    public long Id { get; set; }

    public Guid SourceMessageId { get; set; }

    public string SourceParticipant { get; set; } = string.Empty;

    public string SourceIdentifier { get; set; } = string.Empty;

    public string? ProposedName { get; set; }

    public string? ProposedDescription { get; set; }

    public string? RequestedClassKey { get; set; }

    /// <summary>Null when the class could not be bound at all.</summary>
    public string? BoundClassKey { get; set; }

    /// <summary>True when bound to an ancestor rather than the class the sender named.</summary>
    public bool ClassDegraded { get; set; }

    public int PropertiesMapped { get; set; }

    public int PropertiesUnmapped { get; set; }

    public StewardshipState State { get; set; } = StewardshipState.Proposed;

    public string? DecidedBy { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public string? RejectReason { get; set; }

    /// <summary>Assigned on approval, not before.</summary>
    public string? LocationCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
