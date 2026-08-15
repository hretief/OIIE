namespace SimHost.Domain.Cms;

/// <summary>
/// The organisational context a record belongs to, as CMS knows it.
///
/// Modelled on the shape real O&amp;M systems use for this: MMS holds
/// <c>dbo.SETUP_OWNER (OWNER_ID, OWNER_NAME)</c> with local integer keys. The values
/// are the same districts, but the key is each system's own — CMS's owner code is
/// meaningless in MMS, and neither is an iTwin GUID.
///
/// That is the entire reason this table exists rather than an <c>ITwinId</c> column.
/// Two systems naming the same district cannot be joined directly; they are related
/// by registering each local code with the CIR and asserting equivalence, which
/// yields one CIRID with several names.
/// </summary>
public class ContextOwnerRecord
{
    public long Id { get; set; }

    /// <summary>CMS's own key for the owner. Unique within CMS, meaningless outside it.</summary>
    public string OwnerCode { get; set; } = string.Empty;

    public string OwnerName { get; set; } = string.Empty;

    /// <summary>
    /// Null until a steward asserts this owner is equivalent to one another system
    /// holds. Unresolved is a visible state, not an absent one: it is what tells the
    /// operator the registry work has not been done yet.
    /// </summary>
    public Guid? Cirid { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// An asset installation or removal as an O&amp;M system recorded it.
///
/// This is the receiving end of OIIE Scenario 11. The condition monitoring system
/// cares about what was installed where and when, because its failure statistics are
/// meaningless without knowing which physical unit was in service over which interval.
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
    /// CMS's own context owner code for this event, or null when nothing has yet
    /// related the publisher's asserted context to one CMS recognises.
    ///
    /// Nullable because a publisher that asserts no context must still be recordable:
    /// dropping the event would lose real operational history, and inventing an owner
    /// would file it under a context nobody claimed.
    /// </summary>
    public string? OwnerCode { get; set; }

    /// <summary>
    /// The context the publisher asserted, kept exactly as received.
    ///
    /// CMS has no iTwin column and never will: a twin GUID is Bentley's context key,
    /// not this system's. Storing it raw as a foreign identifier — rather than as a
    /// native field — is what forces the relation to be established in the registry
    /// instead of assumed here.
    /// </summary>
    public string? ForeignOwnerSourceId { get; set; }

    public string? ForeignOwnerIdInSource { get; set; }

    /// <summary>
    /// Null until something resolves the publisher's identifiers to a shared identity.
    /// As with MMS in uc01, unresolved is a visible state rather than an absent one.
    /// </summary>
    public Guid? Cirid { get; set; }

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A functional location as the condition monitoring system knows it.
///
/// CMS holds its own record rather than reading MMS's, because the two systems are
/// separately deployed and a condition monitoring system that could only describe a
/// location by querying the maintenance system would not be interoperating with it,
/// it would be a view over it.
///
/// Like MMS's LightSystemInventory, CMS adopts identity rather than
/// minting it: the location was named upstream, and a locally minted FederationId
/// would create a competing identity for a thing CMS did not originate.
/// </summary>
public class MonitoredLocationRecord
{
    public long Id { get; set; }

    /// <summary>
    /// CMS's own local key, assigned on first sighting. Meaningless outside CMS,
    /// which is the point: it is the legacy-code case the CIR exists to resolve.
    /// </summary>
    public string LocationCode { get; set; } = string.Empty;

    /// <summary>
    /// Adopted from the publisher. Empty means nothing has yet told CMS what this is.
    /// </summary>
    public Guid FederationId { get; set; }

    public string? Designation { get; set; }

    /// <summary>Foreign identifier as received, kept raw until something resolves it.</summary>
    public string? ForeignSourceId { get; set; }

    public string? ForeignIdInSource { get; set; }

    /// <summary>CMS's own context owner code, once one is related to the asserted context.</summary>
    public string? OwnerCode { get; set; }

    /// <summary>The context the publisher asserted, kept raw for the registry to resolve.</summary>
    public string? ForeignOwnerSourceId { get; set; }

    public string? ForeignOwnerIdInSource { get; set; }

    /// <summary>Set once the registry resolves the foreign identifier to a shared identity.</summary>
    public Guid? Cirid { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A serialised asset as the condition monitoring system knows it.
///
/// Unlike MMS's <see cref="Mms.EquipmentRecord"/>, CMS never originates an asset: it
/// learns of one only when an installation event names it. So it adopts the
/// publisher's FederationId and never mints, which is why the uniqueness constraint
/// must tolerate the empty Guid that an unidentified asset carries.
///
/// <see cref="InstalledAtLocationCode"/> is current state derived from the event log,
/// held separately because a reliability question — what is fitted here now — should
/// not require replaying every event to answer.
/// </summary>
public class MonitoredAssetRecord
{
    public long Id { get; set; }

    /// <summary>CMS's own local key for the asset.</summary>
    public string AssetCode { get; set; } = string.Empty;

    /// <summary>Adopted from the publisher; CMS originates no assets of its own.</summary>
    public Guid FederationId { get; set; }

    public string? Designation { get; set; }

    public string? SerialNumber { get; set; }

    /// <summary>
    /// The CMS location code this asset is currently installed at, or null when the
    /// last event was a removal. Null is a real state: an asset in the workshop is
    /// genuinely installed nowhere.
    /// </summary>
    public string? InstalledAtLocationCode { get; set; }

    /// <summary>When the current installation began, per the event that established it.</summary>
    public DateTimeOffset? InstalledAt { get; set; }

    public string? ForeignSourceId { get; set; }

    public string? ForeignIdInSource { get; set; }

    /// <summary>CMS's own context owner code, once one is related to the asserted context.</summary>
    public string? OwnerCode { get; set; }

    /// <summary>The context the publisher asserted, kept raw for the registry to resolve.</summary>
    public string? ForeignOwnerSourceId { get; set; }

    public string? ForeignOwnerIdInSource { get; set; }

    public Guid? Cirid { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
