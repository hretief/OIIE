namespace RegLocationEngine.Infrastructure.RegLocation;

/// <summary>
/// REG-LOCATION's records as the engine sees them over the wire.
///
/// Declared here rather than referenced from RegLocationProvider on purpose. The
/// two projects share these shapes today, and a ProjectReference would make that
/// sharing structural: the emulation and the integration would compile as one
/// unit, and it would become possible to call the store directly and never
/// notice the boundary had gone. A real customer registry is reachable only over
/// HTTP, so this engine is too.
///
/// The cost is a duplicated record. That is the intended cost -- it is what
/// makes a breaking change to the provider's API show up here as a decision
/// rather than as a silent recompile.
/// </summary>
public sealed record RegTag(
    int TagId,
    int ItemId,
    int ClassId,
    string Code,
    int Revision,
    string Name,
    string State);

public sealed record RegObject(
    int ObjectId,
    int ObjectType,
    Guid? Guid,
    int ScopeId,
    byte HideFlags,
    byte LockFlags,
    DateTime? DateAdded,
    int? AddedBy,
    DateTime? DateChanged,
    int? ChangedBy);

public sealed record RegTagDetail(RegTag Tag, RegObject Object);

/// <summary>
/// What the provider posts when a steward approves a tag.
///
/// Thin by design: identity and provenance only. The engine reads the details
/// back rather than trusting the payload, because the notification is a claim
/// about a past moment and the registry is the only thing that knows the present
/// one -- an approval reversed between send and receipt must not still publish.
/// </summary>
public sealed record TagApprovedNotification(
    int TagId,
    string Code,
    int Revision,
    Guid? Guid,
    int ScopeId,
    string DecidedBy,
    DateTimeOffset DecidedAt);

/// <summary>
/// The tag states the engine names. Mirrors the provider's RegTagState.
/// </summary>
public static class RegTagStates
{
    public const string Proposed = "Proposed";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

/// <summary>
/// What the engine posts to file an incoming segment as a proposal.
///
/// State is present and is always Proposed on this path. The provider's own
/// default is Approved -- chosen so the bootstrap seed kept working -- so
/// leaving it out here would admit every published segment to the registry
/// without a steward ever seeing it.
/// </summary>
public sealed record CreateTagRequest(
    int ItemId,
    int ClassId,
    string Code,
    int Revision,
    string Name,
    int ScopeId,
    Guid? Guid,
    string? State);

/// <summary>
/// What the engine puts to correct a tag it has already proposed.
///
/// Mirrors the provider's UpdateTagRequest. ItemId and the federation GUID are
/// absent for the same reason they are absent there: an edit changes what the
/// tag says, not which thing it is or where it lives.
/// </summary>
public sealed record UpdateTagRequest(
    int ClassId,
    string Code,
    int Revision,
    string Name);

// ---- Sites ---------------------------------------------------------------

public sealed record RegScope(
    int ScopeId,
    string Name,
    int NamespaceId,
    int? ContextObjectId,
    int? ContextObjectType,
    int? ParentId,
    bool IsEnabled,
    int UsageCount);

/// <summary>
/// What a cascaded scope delete removed.
///
/// ItemsDeleted is load-bearing rather than informational: it is the Provider's
/// answer to whether the site type was still in use, and the engine reads it to
/// decide whether the matching SITE-TYPE entry may be cancelled. Asking CIR to
/// drop a type another site still references would leave that site with an
/// identity no consumer can resolve.
/// </summary>
public sealed record ScopeCascadeResult(
    int ScopeId,
    int TagsDeleted,
    int SerialsDeleted,
    int ItemsDeleted,
    int ScopesDeleted);

public sealed record RegItem(
    int ItemId,
    int NamespaceId,
    int UnitId,
    int TrnId,
    string? Code,
    string? Description,
    string? ItemType);

public sealed record RegSerial(
    int SerialId,
    int ItemId,
    string Name,
    string? Description);

public sealed record RegSerialDetail(RegSerial Serial, RegObject Object);

/// <summary>
/// What the engine posts to create the scope a site's contents live in.
/// </summary>
public sealed record CreateScopeRequest(
    string Name,
    int NamespaceId,
    int? ParentId,
    int? ContextObjectId,
    int? ContextObjectType,
    Guid? Guid);

/// <summary>
/// What the engine posts to create the item a site type becomes.
///
/// The namespace, unit and TRN come from configuration rather than the BOD.
/// They are how the registry partitions its own identifiers, and a sender has
/// no standing to assert one.
/// </summary>
public sealed record CreateItemRequest(
    int NamespaceId,
    int UnitId,
    int TrnId,
    int ScopeId,
    Guid? Guid,
    string? Code,
    string? Description,
    string? ItemType);

/// <summary>What the engine posts to create the serial representing one site.</summary>
public sealed record CreateSerialRequest(
    int ItemId,
    string Name,
    int ScopeId,
    string? Description,
    Guid? Guid);

/// <summary>
/// Points a scope at the object whose context it represents.
///
/// Sent after the serial exists, because the scope has to be created first for
/// the serial to have somewhere to live -- so the link can only be made on a
/// second pass.
/// </summary>
public sealed record SetScopeContextRequest(
    int ContextObjectId,
    int ContextObjectType);

