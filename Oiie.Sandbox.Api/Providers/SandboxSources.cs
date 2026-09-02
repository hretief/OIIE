using SimHost.Application.Participants;
using SimHost.Domain.RegLocation;
using SimHost.Personalities.Eng;
using SimHost.Personalities.RegLocation;

namespace Oiie.Sandbox.Api.Providers;

/// <summary>
/// The ENG panels reading the sandbox's own in-process participant.
///
/// This is the behaviour the sandbox has always had, moved behind an interface
/// so the endpoints can be pointed elsewhere without being rewritten. It stays
/// as the fallback for a clone with no provider configuration, and as the only
/// source that can populate the instrument attributes.
/// </summary>
public sealed class SandboxEngSource(EngService eng, ParticipantRegistry registry) : IEngSource
{
    public bool IsProviderBacked => false;

    public async Task<IReadOnlyList<TwinView>> ListTwinsAsync(CancellationToken ct)
    {
        var twins = await eng.ListTwinsAsync(ct);

        return twins
            .Select(t => new TwinView(t.Id, t.Code, t.Name, t.Description, t.CreatedAt))
            .ToList();
    }

    public async Task<IReadOnlyList<SegmentView>> ListSegmentsAsync(Guid twinId, CancellationToken ct)
    {
        var tags = await eng.ListTagsAsync(twinId, ct);

        return tags.Select(t => new SegmentView(
            t.Id,
            t.TagNumber,
            // The sandbox always mints one, so an all-zero value would mean an
            // unsaved row rather than a tag nobody has federated.
            t.FederationId == Guid.Empty ? null : t.FederationId,
            t.IModelId,
            t.ServiceDescription,
            t.UnitNumber,
            t.ClassKey,
            t.RangeMinimum,
            t.RangeMaximum,
            t.ControlAction,
            t.PidReference,
            t.Maturity.ToString(),
            // The sandbox holds the marker's key rather than its name. Rendering
            // the id keeps the column populated without a second query, and the
            // provider-backed source supplies the name where it has one.
            t.PublishedInVersionId?.ToString(),
            t.UpdatedAt)).ToList();
    }

    /// <summary>
    /// Authors a tag in the sandbox's own participant.
    ///
    /// The pre-existing behaviour, unchanged: this source both reads and writes
    /// the participant database, so a tag added here appears in the list it
    /// serves.
    ///
    /// Unlike the provider, this path can allocate. A caller supplying a code
    /// prefix instead of a number gets the next in the series, which is the
    /// sandbox's identity service doing what ENG has no facility for.
    /// </summary>
    public async Task<AuthoredSegment> AddSegmentAsync(
        Guid twinId, NewSegment segment, CancellationToken ct)
    {
        var tag = await eng.AddTagAsync(
            segment.TagNumber, segment.ServiceDescription, segment.UnitNumber,
            segment.ClassKey, segment.RangeMinimum, segment.RangeMaximum,
            segment.ControlAction, segment.CodePrefix, twinId,
            segment.FederationId, segment.IModelId, ct);

        return new AuthoredSegment(
            tag.TagNumber,
            tag.FederationId,
            tag.ITwinId,
            tag.IModelId,
            tag.Maturity.ToString());
    }

    /// <summary>
    /// Promotes in the sandbox's own participant, unchanged.
    ///
    /// This path publishes as part of promoting: the gate runs, and a pass writes
    /// the outbox row that carries the BOD. That is the sandbox's original
    /// design and it stays, because nothing polls this store -- if promotion did
    /// not enqueue here, nothing else would.
    ///
    /// The channel is resolved here rather than passed in so the two sources
    /// present the same signature. Publishing is a detail of how this one
    /// releases, not something a caller should have to know to ask for a release.
    /// </summary>
    public async Task<PromotionOutcome> PromoteAsync(
        Guid twinId, string versionName, CancellationToken ct)
    {
        var publisher = registry.Get(EngService.ParticipantId).Config.Channels
            .FirstOrDefault(c => c.Role == ChannelRole.Publisher)
            ?? throw new InvalidOperationException("ENG has no publisher channel configured.");

        var result = await eng.PromoteAsync(
            versionName, publisher.ChannelUri, publisher.Topics.FirstOrDefault(), twinId, ct);

        return new PromotionOutcome(
            result.Released,
            result.NamedVersionId,
            result.Name,
            result.TagCount,
            // One marker, always: the sandbox scopes a named version to the twin
            // rather than to a model, so there is never more than one to report.
            MarkerCount: 1,
            result.Findings);
    }
}

/// <summary>
/// The stewardship panel reading the sandbox's own participant.
/// </summary>
public sealed class SandboxRegLocationSource(RegLocationService reg) : IRegLocationSource
{
    public bool IsProviderBacked => false;

    // The sandbox owns the whole gate, so it can refuse as well as admit.
    public bool CanReject => true;

    public async Task<IReadOnlyList<StewardshipView>> GetQueueAsync(
        string? twin, bool includeDecided, CancellationToken ct)
    {
        var queue = await reg.GetQueueAsync(
            twin, includeDecided ? null : StewardshipState.Proposed, ct);

        return queue.Select(s => new StewardshipView(
            s.Id.ToString(),
            s.SourceParticipant,
            s.SourceIdentifier,
            s.ProposedName ?? string.Empty,
            s.RequestedClassKey,
            s.BoundClassKey,
            s.ClassDegraded,
            s.PropertiesMapped,
            s.PropertiesUnmapped,
            s.State.ToString(),
            s.CreatedAt,
            s.ContextSourceId + ":" + s.ContextIdInSource)).ToList();
    }

    /// <summary>
    /// Not implemented here.
    ///
    /// The sandbox's approval publishes to a channel, which means it needs the
    /// participant registry to find the publisher. That is endpoint knowledge
    /// rather than source knowledge, so the existing endpoint keeps doing it and
    /// this method is never reached in sandbox mode.
    /// </summary>
    public Task<int> ApproveAsync(IReadOnlyCollection<long>? ids, CancellationToken ct) =>
        throw new NotSupportedException(
            "Sandbox approval runs through the existing endpoint, which owns channel resolution.");
}
