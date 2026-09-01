namespace EngEngine.Application;

/// <summary>
/// An iTwin as the engine reads it back from ENG.
///
/// This is the site context a design belongs to, and it is what SyncSites
/// carries. The platform-sourced fields are nullable because ENG stores what
/// it was told: a twin registered from a sparse payload has a code and little
/// else, and the builder decides what that is enough to publish.
/// </summary>
public sealed record EngITwin(
    Guid ITwinId,
    string Code,
    string? Description,
    DateTime CreatedUtc,
    string? DisplayName,
    string? Number,
    string? TwinClass,
    string? SubClass,
    string? TwinType);

/// <summary>
/// A model, and the iTwin it belongs to.
///
/// The iTwin matters to the engine for one reason: the ISBM channel convention
/// is rooted in the iTwin's federation id, not the iModel's. An iTwin may hold
/// several iModels representing different disciplines, and they all publish onto
/// the one channel with the discipline expressed as a topic. So the engine has
/// to walk from the model it watches up to the twin that owns it.
/// </summary>
public sealed record EngIModel(
    Guid IModelId,
    Guid ITwinId,
    string Code,
    string? Description,
    DateTime CreatedUtc);

/// <summary>
/// A marker in an ENG iModel's history, as it arrives over the wire.
///
/// Deliberately a local copy of EngProvider's EngNamedVersion rather than a
/// shared type. ENG emulates a customer system and the engine is a client of
/// it; a shared assembly would make the two one deployable, and the engine would
/// stop proving that ENG can be replaced by a real iModels service.
///
/// Three of these fields must not leave the engine:
///
///   NamedVersionId   ENG's local BIGINT key. Meaningless anywhere else.
///   ChangesetId      ENG-internal. It may be used here for correlation and
///                    logging, but it must never appear in a published BOD:
///                    a consumer that stored it would be reconciling against
///                    an identifier only ENG can interpret.
///   ChangesetIndex   Same, and additionally a position rather than an identity.
///
/// VersionGuid and Name are the two a consumer may legitimately hold.
/// </summary>
public sealed record EngNamedVersion(
    long NamedVersionId,
    Guid VersionGuid,
    Guid IModelId,
    string Name,
    string? Description,
    string ChangesetId,
    int ChangesetIndex,
    string CreatedBy,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    int ElementCount);

/// <summary>
/// An element inside a marker: what an engineer calls a tag.
///
/// There is no maturity or status field, because ENG has none. Whether this
/// element is published is a question about markers, not about the element, and
/// the engine answers it by which marker it arrived under.
///
/// FederationGuid is nullable: ENG does not mint it, since assigning a
/// federation identity is the job of whoever federates. An element without one
/// is not published at all -- see EngSegmentsBuilder.IsPublishable. An earlier
/// version substituted a deterministic UUID here, which fabricated an identity
/// that would collide with the real one the day someone federated the element.
/// </summary>
public sealed record EngElement(
    long ECInstanceId,
    Guid IModelId,
    long ECClassId,
    string FullyQualifiedECClassName,
    Guid? FederationGuid,
    string? CodeValue,
    string? UserLabel,
    string? DisplayName,
    long? ParentECInstanceId,
    int ChangesetIndex,
    DateTime CreatedUtc,
    DateTime ModifiedUtc);
