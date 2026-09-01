namespace EngProvider.Application;

/// <summary>
/// Configuration for the ENG emulation.
/// </summary>
public sealed class EngOptions
{
    public string SqlConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Applies the ENG schema at startup. Left on for dev and demo; a real
    /// customer system would have its schema managed elsewhere.
    /// </summary>
    public bool AutoCreateSchema { get; set; } = true;
}

/// <summary>
/// The ENG design store.
///
/// Expressed in ENG's own vocabulary — iTwin, iModel, ECClass, Element,
/// NamedVersion — and knows nothing of CCOM, Segments, BODs or functional
/// locations. An engineering design tool does not know it is being integrated
/// with; translation is the integrator's job.
///
/// There is no Tag type here. An element IS what an engineer calls a tag, so a
/// separate type would be a second name for one thing and would eventually
/// disagree with itself.
///
/// Note what is absent. Nothing here publishes: creating a named version stops
/// at the database, and carrying it onto a channel is the engine's work.
/// Nothing here validates either — a named version is a marker, not a gate, so
/// ENG states what the design says and REG-LOCATION judges whether it is fit to
/// accept.
/// </summary>
public interface IEngDesignStore
{
    // ---- Lifecycle --------------------------------------------------------

    /// <summary>
    /// Drops every table and recreates them empty.
    ///
    /// For day zero only. ENG is the authoritative source, so this discards the
    /// record rather than resetting a cache: every iTwin, element and named
    /// version goes, including twins added through the UI. It exists because a
    /// demo database accumulates twins from previous runs, and a reset that left
    /// them behind would not be a reset.
    ///
    /// Recreating is part of the same call rather than left to the next restart,
    /// since a host that is already running will not re-run its initializer and
    /// would answer every query with an invalid-object error until bounced.
    /// </summary>
    Task ResetAsync(CancellationToken ct);

    // ---- iTwins and iModels ----------------------------------------------

    Task<IReadOnlyList<EngITwin>> GetITwinsAsync(CancellationToken ct);

    Task<EngITwin?> FindITwinAsync(Guid iTwinId, CancellationToken ct);

    /// <summary>
    /// Records an iTwin, or updates the one already stored under this id.
    ///
    /// Idempotent because the trigger it serves is not: an iTwinCreated event
    /// can be redelivered, and the sandbox UI can be asked to add a twin that
    /// is already present. Neither should produce a second project.
    /// </summary>
    Task<EngITwin> UpsertITwinAsync(UpsertITwinRequest request, CancellationToken ct);

    /// <summary>
    /// The stable UUID for a site type, minted on first sight.
    ///
    /// Site.Type.UUID must be the same for every iTwin of a given type, or
    /// REG-LOCATION receives two site types nothing can tell apart. The platform
    /// supplies only the type string, so the identifier is ENG's to invent and
    /// then to remember.
    /// </summary>
    Task<Guid> GetOrCreateITwinTypeUuidAsync(string typeName, CancellationToken ct);

    Task<IReadOnlyList<EngIModel>> GetIModelsAsync(Guid? iTwinId, CancellationToken ct);

    Task<EngIModel?> FindIModelAsync(Guid iModelId, CancellationToken ct);

    // ---- Schema catalogue -------------------------------------------------

    /// <summary>
    /// The classes an element may be created on.
    ///
    /// Abstract classes are excluded: the database rejects an element created on
    /// one, so offering them would only invite a guaranteed failure.
    /// </summary>
    Task<IReadOnlyList<EngClass>> GetConcreteClassesAsync(CancellationToken ct);

    // ---- Elements ---------------------------------------------------------

    Task<EngElement?> FindElementAsync(long ecInstanceId, CancellationToken ct);

    /// <summary>
    /// Finds an element by the code it carries within an iModel.
    ///
    /// This is the lookup for a caller holding only what a person would read off
    /// a drawing. CodeValue is unique per iModel, not globally, so the iModel has
    /// to be named.
    /// </summary>
    Task<EngElement?> FindElementByCodeAsync(Guid iModelId, string codeValue, CancellationToken ct);

    /// <summary>
    /// Elements matching the given filters.
    ///
    /// <paramref name="modifiedSince"/> restricts the result to elements changed
    /// at or after that instant, which is what an incremental reader outside ENG
    /// needs in order to avoid re-reading the whole model on every pass. It is
    /// inclusive, and callers are expected to overlap their window rather than
    /// resume exactly where they stopped: a row committed during the previous
    /// query but stamped just before it would otherwise never be seen.
    /// </summary>
    Task<IReadOnlyList<EngElement>> GetElementsAsync(
        Guid? iModelId,
        long? namedVersionId,
        DateTime? modifiedSince,
        CancellationToken ct);

    /// <summary>
    /// Creates or updates elements in an iModel.
    ///
    /// The target is the iModel, not a named version: elements accumulate as
    /// work is done and are not authored into a baseline. Each call is stamped
    /// with the next changeset position in that iModel, so a batch lands
    /// together and later markers can derive whether it falls inside them.
    ///
    /// Elements at or below the most recent marker are refused: that content has
    /// already been published, and editing it would change what a consumer was
    /// told after the fact.
    /// </summary>
    Task<ElementUpsertResult> UpsertElementsAsync(
        Guid iModelId,
        IReadOnlyList<ElementUpsert> elements,
        CancellationToken ct);

    // ---- Named versions ---------------------------------------------------

    /// <summary>
    /// Named versions, optionally restricted to one iModel and to those changed
    /// at or after <paramref name="modifiedSince"/>.
    ///
    /// Ordered by ModifiedUtc when reading incrementally, so a reader that stops
    /// partway through resumes without a gap.
    /// </summary>
    Task<IReadOnlyList<EngNamedVersion>> GetNamedVersionsAsync(
        Guid? iModelId,
        DateTime? modifiedSince,
        CancellationToken ct);

    Task<EngNamedVersion?> FindNamedVersionAsync(long namedVersionId, CancellationToken ct);

    /// <summary>
    /// Creates a named version, pinning it at the iModel's current position.
    ///
    /// This is the whole act. There is nothing to declare first and release
    /// later: the marker is created already pinned, and whatever sits below it
    /// is thereby baselined. Nothing is validated on the way through — ENG
    /// states what the design says, and REG-LOCATION decides whether it is
    /// acceptable.
    ///
    /// A marker may legitimately be the first in its iModel, in which case it
    /// captures everything from day 0.
    /// </summary>
    Task<EngNamedVersion> CreateNamedVersionAsync(NamedVersionDraft draft, CancellationToken ct);

    /// <summary>
    /// The elements a marker contains, derived from changeset position rather
    /// than read from stored membership.
    ///
    /// This is the answer to "what was handed over at this baseline", and it is
    /// computed the same way every time it is asked, however long after the
    /// marker was cut.
    /// </summary>
    Task<IReadOnlyList<EngElement>> GetNamedVersionElementsAsync(
        long namedVersionId,
        CancellationToken ct);
}

// ---- iTwins and iModels --------------------------------------------------

/// <summary>
/// The project or asset an iModel belongs to.
///
/// Everything from DisplayName onward is what the iTwin platform returns from
/// GET /iTwins/{id} rather than anything ENG derives. All nullable, because a
/// twin seeded before the sandbox called the platform has none of it.
/// </summary>
public sealed record EngITwin(
    Guid ITwinId,
    string Code,
    string? Description,
    DateTime CreatedUtc,
    string? DisplayName = null,
    string? Number = null,
    string? TwinClass = null,
    string? SubClass = null,
    string? TwinType = null);

/// <summary>
/// Registers an iTwin the sandbox has been told about, or refreshes one it
/// already knows.
///
/// Keyed on ITwinId rather than Code, because the id is the platform's own
/// federation identifier and survives a project being renamed.
/// </summary>
public sealed record UpsertITwinRequest(
    Guid ITwinId,
    string Code,
    string? Description = null,
    string? DisplayName = null,
    string? Number = null,
    string? TwinClass = null,
    string? SubClass = null,
    string? TwinType = null);

/// <summary>
/// A model. CodeValue is unique within one of these, which is what makes a code
/// unambiguous: two projects may each hold a P-101 and mean different pumps.
/// </summary>
public sealed record EngIModel(
    Guid IModelId,
    Guid ITwinId,
    string Code,
    string? Description,
    DateTime CreatedUtc);

// ---- Schema catalogue ----------------------------------------------------

/// <summary>
/// An EC class an element may be created on.
/// </summary>
public sealed record EngClass(
    long ECClassId,
    string SchemaName,
    string SchemaVersion,
    string ClassName,
    string FullyQualifiedName,
    string? DisplayLabel,
    string ClassModifier);

// ---- Elements ------------------------------------------------------------

/// <summary>
/// ENG's system of record — an element, which is what an engineer calls a tag.
///
/// There is no maturity field. An element is not draft or released; it sits at
/// a changeset position, and whether that position falls inside a published
/// baseline is a question about markers rather than about the element. A stored
/// per-element status would eventually contradict the markers, so there is not
/// one.
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

/// <summary>
/// An element a caller wants to exist.
///
/// ECInstanceId is absent on create and supplied on update; the database
/// allocates it, so a caller cannot choose one.
///
/// The iModel is a parameter of the call, not a per-item field, so a batch
/// cannot span iModels and half-succeed. ChangesetIndex is absent for the same
/// reason and because the store allocates it: a caller choosing its own
/// position could place work inside an already-published baseline.
///
/// FederationGuid is accepted but never invented. ENG does not mint it — the
/// identifier is assigned by whoever federates — so it arrives from the caller
/// that made that decision, and an element nobody has federated stays without
/// one. Null on update leaves any existing value alone, since "I did not
/// mention it" and "remove the identity other systems hold" are different
/// requests and only the first is ever meant.
/// </summary>
public sealed record ElementUpsert(
    long? ECInstanceId,
    long ECClassId,
    string? CodeValue,
    string? UserLabel,
    string? DisplayName,
    long? ParentECInstanceId,
    Guid? FederationGuid = null);

public sealed record ElementUpsertResult(
    IReadOnlyList<UpsertedElement> Elements,
    IReadOnlyList<UpsertRejection> Rejections)
{
    public int Created => Elements.Count(e => e.Created);
    public int Updated => Elements.Count(e => !e.Created);
}

public sealed record UpsertedElement(long ECInstanceId, string? CodeValue, bool Created);

// ---- Named versions ------------------------------------------------------

/// <summary>
/// A marker in an iModel's history, and what ENG hands over.
///
/// Carries two identifier sets. NamedVersionId is this database's own key, a
/// BIGINT allocated locally. VersionGuid, ChangesetId and ChangesetIndex are
/// what a real iModels would put in an event: a consumer outside ENG holds
/// those and never sees the BIGINT.
///
/// The changeset pair is never null. A marker is pinned at creation, because an
/// unpinned marker has no position and its contents could not be derived.
///
/// ElementCount is derived, not stored — it is the size of the range this
/// marker covers, recomputed on read like the membership itself.
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

public sealed record NamedVersionDraft(
    Guid IModelId,
    string Name,
    string? Description,
    string? CreatedBy);

/// <summary>
/// Something ENG declined to store, and why. Shared by every write path: the
/// reason a customer system says no does not depend on what was asked.
/// </summary>
/// <param name="Key">
/// The identifier that was refused, so a caller can tell which item in a batch
/// this refers to.
/// </param>
/// <param name="Transient">
/// True only for causes a later identical attempt could clear: deadlock, lock
/// timeout, connection failure. An abstract class or a duplicate code is a fact
/// about the request and will never clear on retry.
/// </param>
public sealed record UpsertRejection(string Key, string Reason, bool Transient);
