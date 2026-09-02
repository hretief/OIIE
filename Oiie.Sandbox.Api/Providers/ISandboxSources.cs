namespace Oiie.Sandbox.Api.Providers;

/// <summary>
/// A segment as the UI consumes it, whichever system supplied it.
///
/// The nullable attributes are the honest part of this type. The sandbox's own
/// participants hold service descriptions, ranges and control actions because
/// the demo invented them; ENG does not, because a real engineering system
/// keeps that on the element's properties and ENG has not modelled them yet.
/// Rather than fabricate values, the provider-backed source leaves them null
/// and the panel shows them empty — the gap is a fact about the integration and
/// hiding it would make the sandbox look more finished than it is.
/// </summary>
/// <param name="Maturity">
/// Null from the provider. ENG deliberately stores no per-element status, so
/// there is nothing faithful to put here; <paramref name="PublishedInVersion"/>
/// carries the same information in the form ENG actually holds it.
/// </param>
/// <param name="PublishedInVersion">
/// The name of the named version whose changeset range covers this element, or
/// null when it sits ahead of every marker. This is ENG's answer to "is it
/// released", derived on read rather than stored.
/// </param>
public sealed record SegmentView(
    long Id,
    string? TagNumber,
    Guid? FederationId,
    Guid IModelId,
    string? ServiceDescription,
    string? UnitNumber,
    string? ClassKey,
    decimal? RangeMinimum,
    decimal? RangeMaximum,
    string? ControlAction,
    string? PidReference,
    string? Maturity,
    string? PublishedInVersion,
    DateTimeOffset UpdatedAt);

/// <summary>A twin as the UI consumes it.</summary>
public sealed record TwinView(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    DateTimeOffset CreatedAt);

/// <summary>
/// A stewardship row as the UI consumes it.
///
/// <paramref name="RequestedClassKey"/> and the property-mapping counts are
/// null from the provider for the same reason the segment attributes are: class
/// degradation happens in the engine on the way in, and the registry records
/// only the outcome. What the registry does know — the class it bound, and the
/// federation GUID linking back to the ENG element — is populated.
/// </summary>
public sealed record StewardshipView(
    string Id,
    string SourceParticipant,
    string? SourceIdentifier,
    string ProposedName,
    string? RequestedClassKey,
    string? BoundClassKey,
    bool? ClassDegraded,
    int? PropertiesMapped,
    int? PropertiesUnmapped,
    string State,
    DateTimeOffset CreatedAt,
    string? AssertedContext);

/// <summary>
/// A segment a caller wants authored, in the terms the ENG panel offers.
///
/// ClassKey is a key from whichever catalog the panel offered, not an
/// ECClassId: the UI picks by name and only the provider-backed source can
/// resolve one to ENG's own identifier. Each implementation interprets it
/// against the vocabulary it actually holds.
///
/// FederationId is adopted when given and minted when not. The distinction
/// matters and is preserved here rather than defaulted at the edge, because a
/// segment that already has an identity elsewhere must keep it.
///
/// ElementId identifies an existing element to change. Null means "create".
/// The distinction cannot be inferred from the code: a source that matched on
/// the code alone could not tell a correction from a genuine duplicate.
/// </summary>
public sealed record NewSegment(
    string? TagNumber,
    string? ServiceDescription,
    string? UnitNumber,
    string? ClassKey,
    decimal? RangeMinimum,
    decimal? RangeMaximum,
    string? ControlAction,
    string? CodePrefix,
    Guid? FederationId,
    Guid? IModelId,
    long? ElementId = null);

/// <summary>
/// What was authored, as the panel needs to report it back.
///
/// The identity is echoed because in the allocation case the caller chose
/// neither the code nor the federation id and has no other way to learn what it
/// was given.
///
/// Maturity is null from the provider: ENG stores no per-element status, and
/// reporting a made-up one would be the same invention the read path already
/// refuses.
/// </summary>
public sealed record AuthoredSegment(
    string TagNumber,
    // Nullable because an edit does not restate the identity: the caller did
    // not send one and the source must not invent one to fill this field.
    Guid? FederationId,
    Guid ITwinId,
    Guid? IModelId,
    string? Maturity);

/// <summary>
/// What a promotion did, in terms both sources can answer.
///
/// Deliberately not EngService's PromotionResult: that record carries a single
/// NamedVersionId, and ENG cuts one marker per iModel because a marker is pinned
/// within one model's history. MarkerCount is what tells the two apart.
///
/// Findings are the gate's refusals. They differ by source -- the sandbox checks
/// classification and service description, ENG checks federated identity -- and
/// that difference is real rather than an inconsistency to paper over: each
/// store can only refuse on what it actually holds.
/// </summary>
public sealed record PromotionOutcome(
    bool Released,
    long NamedVersionId,
    string Name,
    int SegmentCount,
    int MarkerCount,
    IReadOnlyList<string> Findings);

/// <summary>
/// Where the ENG panels get their data, and where authoring and release are
/// sent.
///
/// Two implementations: one reading the sandbox's in-process participant, one
/// reading the deployed ENG provider over HTTP. The interface exists so the
/// endpoints do not have to know which, and so the choice is made once in DI
/// rather than at every call site.
/// </summary>
public interface IEngSource
{
    /// <summary>
    /// True when this is reading the real provider apps. Reported to the UI so
    /// an operator can tell whether they are looking at a customer system or at
    /// the sandbox's own rehearsal of one.
    /// </summary>
    bool IsProviderBacked { get; }

    Task<IReadOnlyList<TwinView>> ListTwinsAsync(CancellationToken ct);

    Task<IReadOnlyList<SegmentView>> ListSegmentsAsync(Guid twinId, CancellationToken ct);

    /// <summary>
    /// Authors a segment in the same store this source reads from.
    ///
    /// On the interface rather than left to the endpoint precisely because the
    /// two must not diverge: writing to the sandbox while reading the provider
    /// is why an authored segment could return 200 and then never appear in the
    /// list. Binding both to one source makes that particular mismatch
    /// unrepresentable.
    /// </summary>
    Task<AuthoredSegment> AddSegmentAsync(
        Guid twinId, NewSegment segment, CancellationToken ct);

    /// <summary>
    /// Releases the twin's unpublished work as a named version.
    ///
    /// On the interface for the same reason as AddSegmentAsync: a marker cut in
    /// one store cannot release elements held in another. Promoting through the
    /// sandbox while the panel reads the provider would report a release that
    /// the elements on screen were never part of.
    ///
    /// Note what this does not do when provider-backed: it does not publish.
    /// Creating the marker is the whole act, and carrying it onto ISBM is
    /// EngEngine's work, done off its own poll rather than on this thread. That
    /// separation is the point -- the handover has to survive the operator
    /// closing the browser.
    /// </summary>
    Task<PromotionOutcome> PromoteAsync(
        Guid twinId, string versionName, CancellationToken ct);
}

/// <summary>
/// Where the stewardship panel gets its data, and where approvals are sent.
/// </summary>
public interface IRegLocationSource
{
    /// <inheritdoc cref="IEngSource.IsProviderBacked"/>
    bool IsProviderBacked { get; }

    /// <summary>
    /// The steward's queue.
    /// </summary>
    /// <param name="includeDecided">
    /// False for the working queue, true to show approved rows beside
    /// outstanding ones.
    /// </param>
    Task<IReadOnlyList<StewardshipView>> GetQueueAsync(
        string? twin, bool includeDecided, CancellationToken ct);

    /// <summary>
    /// Admits proposals to the registry, returning how many were admitted.
    /// </summary>
    /// <param name="ids">
    /// The rows to approve, or null for the whole queue.
    /// </param>
    Task<int> ApproveAsync(IReadOnlyCollection<long>? ids, CancellationToken ct);

    /// <summary>
    /// Whether this source can refuse a proposal.
    ///
    /// False when provider-backed: REG-LOCATION defines a Rejected state but
    /// exposes no route that reaches it. The endpoint reports that rather than
    /// silently accepting a request it cannot honour.
    /// </summary>
    bool CanReject { get; }
}
