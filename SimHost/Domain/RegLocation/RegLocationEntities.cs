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

    /// <summary>
    /// The identity, normally adopted from the FederationId the originator sent.
    /// REG-LOCATION mints one only for a location it originates itself. Issuing a new
    /// identity for something it merely received would create a second identity for
    /// one real thing, which is the duplication the federation model exists to stop.
    /// </summary>
    public Guid FederationId { get; set; }

    /// <summary>
    /// The registry's own code, e.g. LOC-000412. A second label for the same entity,
    /// not a second identity — the sender's code remains equally valid.
    /// </summary>
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
/// A directed logical relationship between two locations in the authoritative model,
/// e.g. a power supply supplies a pump.
///
/// Endpoints are held twice: as the sender's identifiers, always, and as
/// REG-LOCATION's own location codes once both ends have cleared the gate. The
/// registry states relationships in its own vocabulary like everything else it
/// accepts, but it cannot do so before approval has minted the codes to state them
/// in — and an edge published alongside its endpoints necessarily arrives first.
///
/// So an edge naming ends that are still proposals is retained unresolved rather
/// than rejected. Rejecting it would make the sender responsible for republishing
/// after an approval it cannot observe, and would lose the fact that the
/// relationship was asserted at all. <see cref="IsResolved"/> is what distinguishes
/// an edge waiting for its endpoints from one the registry can act on.
///
/// The mesh that carried the edge is not retained. CCOM has no envelope for a
/// free-standing connection, so the sender must wrap edges in a network to publish
/// them at all; that container is a property of the wire format, and keeping it here
/// would import a structure the registry does not otherwise model.
/// </summary>
public class LocationConnection
{
    public long Id { get; set; }

    /// <summary>
    /// The edge's identity as asserted by the originator, adopted rather than
    /// reminted for the same reason <see cref="Location.FederationId"/> is.
    /// </summary>
    public Guid FederationId { get; set; }

    /// <summary>Source: the supplier in a Supplies edge. Null until both ends are approved.</summary>
    public string? FromLocationCode { get; set; }

    /// <summary>Sink: the supplied in a Supplies edge. Null until both ends are approved.</summary>
    public string? ToLocationCode { get; set; }

    /// <summary>
    /// The sender's identifier for the source end, e.g. BBFQ0032.
    ///
    /// Kept permanently rather than only until resolution. It is how the edge is
    /// matched to a proposal at approval time, and afterwards it is the record of
    /// what the sender actually asserted — the same reason a proposal keeps its
    /// SourceIdentifier after a Location exists.
    /// </summary>
    public string FromSourceIdentifier { get; set; } = string.Empty;

    /// <summary>The sender's identifier for the sink end, e.g. P-101.</summary>
    public string ToSourceIdentifier { get; set; } = string.Empty;

    /// <summary>
    /// True once both endpoints have been approved and the codes above are filled in.
    ///
    /// Stored rather than derived from the codes being non-null so that the
    /// distinction survives in queries and in the repository browser, where "waiting
    /// for its endpoints" and "malformed" would otherwise look identical.
    /// </summary>
    public bool IsResolved { get; set; }

    /// <summary>The kind of relationship, e.g. eng:Supplies.</summary>
    public string TypeKey { get; set; } = string.Empty;

    /// <summary>Reading from source to sink, as named by the sender, e.g. "Supplies".</summary>
    public string? ForwardRole { get; set; }

    /// <summary>Reading from sink to source, e.g. "Supplied By".</summary>
    public string? InverseRole { get; set; }

    public int? Order { get; set; }

    public string SourceParticipant { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
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

    /// <summary>
    /// The identity the sender asserted, carried through the gate unchanged.
    ///
    /// Held on the proposal rather than assigned at approval because the steward is
    /// deciding whether to accept the entity, not what it is. Empty means the sender
    /// supplied no identity, which the steward should see rather than have silently
    /// repaired on their behalf.
    /// </summary>
    public Guid FederationId { get; set; }

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
