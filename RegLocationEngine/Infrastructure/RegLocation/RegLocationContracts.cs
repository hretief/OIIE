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
