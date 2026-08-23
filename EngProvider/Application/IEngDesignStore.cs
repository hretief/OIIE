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
/// Note what is absent on the write side: nothing here publishes. Releasing a
/// named version stops at the database. Carrying a release onto a channel is
/// the engine's work.
/// </summary>
public interface IEngDesignStore
{
    // ---- iTwins and iModels ----------------------------------------------

    Task<IReadOnlyList<EngITwin>> GetITwinsAsync(CancellationToken ct);

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

    Task<IReadOnlyList<EngElement>> GetElementsAsync(
        Guid? iModelId,
        long? namedVersionId,
        bool? released,
        CancellationToken ct);

    /// <summary>
    /// Creates elements in, or updates elements of, an open draft version.
    ///
    /// Every element belongs to a named version from creation, so the target
    /// version is required rather than inferred. Writing into a released version
    /// is refused: a release is a handover record, and what was handed over
    /// cannot be edited afterwards.
    /// </summary>
    Task<ElementUpsertResult> UpsertElementsAsync(
        long namedVersionId,
        IReadOnlyList<ElementUpsert> elements,
        CancellationToken ct);

    // ---- Named versions ---------------------------------------------------

    Task<IReadOnlyList<EngNamedVersion>> GetNamedVersionsAsync(Guid? iModelId, CancellationToken ct);

    Task<EngNamedVersion?> FindNamedVersionAsync(long namedVersionId, CancellationToken ct);

    Task<EngNamedVersion> CreateNamedVersionAsync(NamedVersionDraft draft, CancellationToken ct);

    /// <summary>
    /// Releases a named version, if the gate allows it.
    ///
    /// Edits accrue without ceremony inside a draft; release is the deliberate
    /// act. Any open finding blocks it, as does an empty version.
    ///
    /// The gate is enforced by the database, not re-implemented here. This method
    /// reports the reasons a release was refused; it does not decide them, and so
    /// cannot drift out of step with the rule actually in force.
    /// </summary>
    Task<ReleaseResult> ReleaseNamedVersionAsync(long namedVersionId, CancellationToken ct);

    // ---- Validation findings ----------------------------------------------

    Task<IReadOnlyList<EngValidationFinding>> GetFindingsAsync(
        long namedVersionId,
        bool openOnly,
        CancellationToken ct);

    Task<EngValidationFinding> RaiseFindingAsync(FindingDraft draft, CancellationToken ct);

    /// <summary>
    /// Marks a finding resolved. Returns false when no such open finding exists.
    ///
    /// Resolution is explicit because an open finding is what holds the release
    /// gate shut; clearing findings implicitly would quietly open it.
    /// </summary>
    Task<bool> ResolveFindingAsync(long findingId, string? resolvedBy, CancellationToken ct);
}

/// <summary>
/// Release state of a named version, and so of every element in it.
/// </summary>
public enum NamedVersionState { Draft, Released }

// ---- iTwins and iModels --------------------------------------------------

/// <summary>
/// The project or asset an iModel belongs to.
/// </summary>
public sealed record EngITwin(
    Guid ITwinId,
    string Code,
    string? Description,
    DateTime CreatedUtc);

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
/// Maturity is not a field on the element. It is read from the version the
/// element belongs to, exposed here as <see cref="IsReleased"/>. A stored
/// per-element status could contradict the version, so there is not one.
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
    long NamedVersionId,
    string NamedVersionName,
    NamedVersionState NamedVersionState,
    bool IsReleased,
    DateTime CreatedUtc,
    DateTime ModifiedUtc);

/// <summary>
/// An element a caller wants to exist.
///
/// ECInstanceId is absent on create and supplied on update; the database
/// allocates it, so a caller cannot choose one.
///
/// NamedVersionId is absent: it is a parameter of the call, not a per-item
/// field, so a batch cannot span versions and half-succeed.
///
/// FederationGuid is absent. ENG does not mint that identifier — it is assigned
/// by whoever federates.
/// </summary>
public sealed record ElementUpsert(
    long? ECInstanceId,
    long ECClassId,
    string? CodeValue,
    string? UserLabel,
    string? DisplayName,
    long? ParentECInstanceId);

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
/// The release container, and what ENG actually hands over.
/// </summary>
public sealed record EngNamedVersion(
    long NamedVersionId,
    Guid IModelId,
    string Name,
    string? Description,
    NamedVersionState State,
    string CreatedBy,
    DateTime CreatedUtc,
    DateTime? ReleasedUtc,
    int ElementCount,
    int OpenFindingCount);

public sealed record NamedVersionDraft(
    Guid IModelId,
    string Name,
    string? Description,
    string? CreatedBy);

/// <summary>
/// Outcome of a release attempt.
///
/// Released is false when the gate refused, in which case Reason says why and
/// Findings carries whatever was still open. The version is untouched.
/// </summary>
public sealed record ReleaseResult(
    bool Released,
    long NamedVersionId,
    string Name,
    NamedVersionState State,
    int ElementCount,
    string? Reason,
    IReadOnlyList<EngValidationFinding> Findings);

// ---- Validation findings -------------------------------------------------

/// <summary>
/// One reason a named version cannot be released.
///
/// The element is identified by CodeValue rather than by ECInstanceId, because a
/// finding may concern a code that resolves to no element at all — which is
/// itself one of the things worth objecting to.
/// </summary>
public sealed record EngValidationFinding(
    long FindingId,
    long NamedVersionId,
    string? CodeValue,
    string Severity,
    string Message,
    string State,
    DateTime CreatedUtc,
    DateTime? ResolvedUtc,
    string? ResolvedBy);

public sealed record FindingDraft(
    long NamedVersionId,
    string? CodeValue,
    string Severity,
    string Message);

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
