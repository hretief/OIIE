namespace SimHost.Domain.OmReliability;

/// <summary>
/// An asset installation or removal as an O&amp;M system recorded it.
///
/// This is the receiving end of OIIE Scenario 11. The reliability system cares about
/// what was installed where and when, because its failure statistics are meaningless
/// without knowing which physical unit was in service over which interval.
///
/// Note that this is an append-only event log rather than a mutable "current
/// installation" field. Scenario 11 publishes discrete events, and collapsing them
/// into current state on arrival would discard the history that makes the data
/// worth receiving — a reliability engineer needs to know a pump ran for eight
/// months, not merely that it is installed today.
/// </summary>
public class AssetInstallationEvent
{
    public long Id { get; set; }

    /// <summary>Identity of the event itself, as asserted by the publisher.</summary>
    public Guid FederationId { get; set; }

    /// <summary>
    /// Install or Removal, as carried by the CCOM EventType. Held as the received
    /// text rather than an enum: an event type this system does not recognise must
    /// still be recordable, and an enum would force it to be discarded or mislabelled.
    /// </summary>
    public string EventKind { get; set; } = string.Empty;

    /// <summary>The CCOM EventType UUID exactly as sent, so an unrecognised type stays traceable.</summary>
    public Guid EventTypeId { get; set; }

    /// <summary>Identity of the serialised asset, as adopted from the publisher.</summary>
    public Guid AssetFederationId { get; set; }

    /// <summary>The publisher's identifier for the asset, kept raw.</summary>
    public string? AssetIdInSource { get; set; }

    public string? AssetSerialNumber { get; set; }

    public string? AssetDesignation { get; set; }

    /// <summary>Identity of the functional location, as adopted from the publisher.</summary>
    public Guid LocationFederationId { get; set; }

    /// <summary>The publisher's identifier for the location, kept raw.</summary>
    public string? LocationIdInSource { get; set; }

    public string? LocationDesignation { get; set; }

    /// <summary>
    /// When the work physically happened, per Scenario 11's mandatory timestamp.
    /// Distinct from <see cref="ReceivedAt"/>: a late-entered work order can arrive
    /// long after the event, and treating receipt as occurrence would silently
    /// corrupt any time-in-service calculation built on this table.
    /// </summary>
    public DateTimeOffset? OccurredAt { get; set; }

    /// <summary>Optional Scenario 11 context: who did the work.</summary>
    public string? PerformedBy { get; set; }

    /// <summary>Optional Scenario 11 context: the originating work order.</summary>
    public string? WorkOrderNumber { get; set; }

    public string? SourceParticipant { get; set; }

    /// <summary>
    /// Null until something resolves the publisher's identifiers to a shared identity.
    /// As with MMS in uc01, unresolved is a visible state rather than an absent one.
    /// </summary>
    public Guid? Cirid { get; set; }

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
