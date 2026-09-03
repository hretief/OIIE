namespace EngEngine.Application;

/// <summary>
/// An iTwin as the engine reads it back from ENG.
///
/// This is the site context a design belongs to, and it is what SyncSites
/// carries. The fields mirror the iTwin platform contract, which is what ENG
/// now stores; they are nullable because ENG stores what it was told, and a
/// twin registered from a sparse payload may carry little beyond its id. The
/// builder decides what that is enough to publish.
/// </summary>
public sealed record EngITwin(
    Guid ITwinId,
    DateTime CreatedUtc,
    string? Class = null,
    string? SubClass = null,
    string? Type = null,
    string? DisplayName = null,
    string? Number = null,
    string? Status = null,
    Guid? ParentITwinId = null,
    string? Description = null,

    /// <summary>
    /// The stored identity of the twin's boundary, carried straight through to
    /// Site.Type.UUID. Null means the twin has no boundary yet and is skipped
    /// by the publisher.
    /// </summary>
    Guid? ITwinTypeId = null,

    /// <summary>
    /// The boundary's name as ENG holds it on the type row, published as
    /// Site.Type.ShortName. Sourced from the same row as ITwinTypeId so the two
    /// halves of the published Type cannot disagree.
    /// </summary>
    string? ITwinTypeNumber = null)
{
    /// <summary>
    /// A short handle for the twin, for logs and for naming it in one string.
    /// Mirrors the provider's derivation: the engineering number first, then the
    /// display name, and the id as a last resort so this is never blank.
    /// </summary>
    public string Handle =>
        FirstNonBlank(Number, DisplayName) ?? ITwinId.ToString();

    private static string? FirstNonBlank(params string?[] values) =>
        Array.Find(values, v => !string.IsNullOrWhiteSpace(v));
}

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
