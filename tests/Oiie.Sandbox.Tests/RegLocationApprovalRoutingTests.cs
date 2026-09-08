using Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RegLocationEngine.Application;
using RegLocationEngine.Infrastructure.Cir;
using RegLocationEngine.Infrastructure.RegLocation;
using RegLocationEngine.Infrastructure.State;
using SimHost.Tests.Sc01;
using Xunit;

namespace SimHost.Tests;

/// <summary>
/// Where an approved tag gets published, and what happens when that cannot be
/// answered.
///
/// The outbound leg used to take its channel from configuration, which meant one
/// pinned iTwin for the whole registry and, worse, silence when the pin was
/// absent: the sweep reported a misconfiguration and no approval reached CIR at
/// all. The route is now data -- a tag names its scope, a scope names its site --
/// and both halves of that are pinned here.
///
/// The skip case earns its place. A tag whose scope has no site must be reported
/// and left unrecorded, because recording it would mean the engine never looked
/// at it again on the day its scope acquired one. In production that failure is
/// invisible: a location that simply never publishes.
/// </summary>
public class RegLocationApprovalRoutingTests
{
    private static readonly Guid SiteA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SiteB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private const int ScopeA = 10;
    private const int ScopeB = 20;
    private const int ScopeWithoutSite = 30;

    [Fact]
    public async Task Each_tag_is_published_on_the_channel_of_the_site_its_scope_names()
    {
        var registry = new RoutingRegistry
        {
            Scopes = { [ScopeA] = SiteA, [ScopeB] = SiteB },
            Approved = { Tag(1, ScopeA), Tag(2, ScopeB) }
        };

        var broker = new FakeIsbmBroker();
        var options = EngineOptions();

        var report = await Service(registry, broker, options).SweepAsync();

        Assert.Empty(report.Errors);
        Assert.Empty(report.Skipped);
        Assert.Equal(2, report.TagsPublished);

        // Two channels, not one. A single publication naming both sites would be
        // delivered to whichever site's subscribers happened to be on it.
        Assert.Equal(
            [options.Value.ChannelUriFor(SiteA), options.Value.ChannelUriFor(SiteB)],
            broker.PublicationChannels.Order().ToArray());
    }

    [Fact]
    public async Task A_tag_whose_scope_names_no_site_is_reported_and_left_unpublished()
    {
        var registry = new RoutingRegistry
        {
            Scopes = { [ScopeA] = SiteA, [ScopeWithoutSite] = null },
            Approved = { Tag(1, ScopeA), Tag(2, ScopeWithoutSite) }
        };

        var broker = new FakeIsbmBroker();
        var state = new MemoryApprovalState();

        var report = await Service(registry, broker, EngineOptions(), state).SweepAsync();

        // The routable one still went. One unroutable tag is not a reason to
        // withhold the rest of the sweep.
        Assert.Equal(1, report.TagsPublished);
        Assert.Single(broker.PublicationChannels);

        var finding = Assert.Single(report.Skipped);
        Assert.Contains("names no site", finding);

        // Not an error: nothing is broken, the scope simply has no site yet.
        Assert.Empty(report.Errors);

        // Only the published one is remembered, so the next sweep tries the
        // other again once its scope has a site.
        Assert.Single(state.Current.PublishedTags);
    }

    [Fact]
    public async Task A_configured_twin_still_pins_every_publication_to_one_channel()
    {
        var registry = new RoutingRegistry
        {
            Scopes = { [ScopeA] = SiteA, [ScopeB] = SiteB },
            Approved = { Tag(1, ScopeA), Tag(2, ScopeB) }
        };

        var broker = new FakeIsbmBroker();
        var options = EngineOptions();
        options.Value.ITwinFederationId = SiteA;

        var report = await Service(registry, broker, options).SweepAsync();

        Assert.Equal(2, report.TagsPublished);
        Assert.Equal([options.Value.ChannelUriFor(SiteA)], broker.PublicationChannels);
    }

    [Fact]
    public async Task A_sweep_no_longer_needs_a_configured_twin()
    {
        var registry = new RoutingRegistry
        {
            Scopes = { [ScopeA] = SiteA },
            Approved = { Tag(1, ScopeA) }
        };

        var report = await Service(registry, new FakeIsbmBroker(), EngineOptions()).SweepAsync();

        // The guard that used to stop here is gone. Its message named a setting
        // deployments no longer carry, so every approval failed configuration
        // rather than publishing.
        Assert.Empty(report.Errors);
        Assert.Equal(1, report.TagsPublished);
    }

    /// <summary>
    /// Corrections to an approved location are filed as extra rows sharing its
    /// federation GUID, so a sweep sees the superseded content alongside the
    /// current content. Publishing both would send downstream two Sync BODs for
    /// one location and let arrival order decide which wins.
    /// </summary>
    [Fact]
    public async Task Only_the_highest_revision_of_a_federated_location_is_published()
    {
        var guid = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

        var registry = new RoutingRegistry
        {
            Scopes = { [ScopeA] = SiteA },
            Approved =
            {
                Revision(1, ScopeA, guid, 0, "Feed pump"),
                Revision(2, ScopeA, guid, 1, "Feed pump (corrected)")
            }
        };

        var broker = new FakeIsbmBroker();
        var state = new MemoryApprovalState();

        var report = await Service(registry, broker, EngineOptions(), state).SweepAsync();

        Assert.Empty(report.Errors);
        Assert.Equal(1, report.TagsPublished);

        // The superseded row is not remembered either, so nothing records it as
        // sent when it never was.
        var key = Assert.Single(state.Current.PublishedTags);
        Assert.Equal(RegLocationEngineState.KeyFor(guid, 1), key);
    }

    private static RegTagDetail Tag(int tagId, int scopeId) =>
        new(
            new RegTag(tagId, 11, 22, $"TIC-{tagId}", 1, $"Tag {tagId}", RegTagStates.Approved),
            new RegObject(tagId, 1, Guid.NewGuid(), scopeId, 0, 0, null, null, null, null));

    /// <summary>One revision of a location, named by its federation GUID.</summary>
    private static RegTagDetail Revision(int tagId, int scopeId, Guid guid, int revision, string name) =>
        new(
            new RegTag(tagId, 11, 22, "PMP-101", revision, name, RegTagStates.Approved),
            new RegObject(tagId, 1, guid, scopeId, 0, 0, null, null, null, null));

    private static IOptions<RegLocationEngineOptions> EngineOptions() =>
        Options.Create(new RegLocationEngineOptions
        {
            Enabled = true,
            RegLocationBaseUrl = "https://reg.example/api/",
            Enterprise = "acme",
            SourceId = "REG-LOCATION",
            LogicalId = "REG-LOCATION",

            // Off: CIR registration is a separate concern from routing, and a
            // fake for it here would assert on nothing these tests are about.
            RegisterInCir = false
        });

    private static RegLocationApprovalService Service(
        IRegLocationClient registry,
        FakeIsbmBroker broker,
        IOptions<RegLocationEngineOptions> options,
        IRegLocationEngineStateStore? state = null) =>
        new(registry,
            broker,
            new UnusedCir(),
            state ?? new MemoryApprovalState(),
            new RegLocationSegmentsBuilder(options),
            options,
            NullLogger<RegLocationApprovalService>.Instance);
}

/// <summary>
/// A registry that answers only what the outbound leg asks: which tags are
/// approved, and which site a scope belongs to.
///
/// A scope present with a null site is deliberately distinct from a scope that
/// is absent. The first is a real scope nobody has bootstrapped a site for --
/// the case the skip finding exists for -- and the second is a dangling
/// reference.
/// </summary>
internal sealed class RoutingRegistry : IRegLocationClient
{
    public Dictionary<int, Guid?> Scopes { get; } = [];

    public List<RegTagDetail> Approved { get; } = [];

    public Task<IReadOnlyList<RegTagDetail>> GetApprovedTagsAsync(int? scopeId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RegTagDetail>>(
            [.. Approved.Where(t => scopeId is null || t.Object.ScopeId == scopeId)]);

    public Task<RegScope?> FindScopeAsync(int scopeId, CancellationToken ct) =>
        Task.FromResult(
            Scopes.TryGetValue(scopeId, out var site)
                ? new RegScope(scopeId, $"Scope {scopeId}", 1, null, null, null, true, 0, site)
                : null);

    // --- Not exercised by the outbound leg ---------------------------------

    public Task<RegTagDetail?> GetTagAsync(int tagId, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<RegTagDetail>> FindTagsByGuidAsync(Guid guid, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<RegTagDetail> CreateTagAsync(CreateTagRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<RegTagDetail?> UpdateTagAsync(int tagId, UpdateTagRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<RegScope?> FindScopeByGuidAsync(Guid guid, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<RegScope> CreateScopeAsync(CreateScopeRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<ScopeCascadeResult?> DeleteScopeCascadeAsync(int scopeId, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task SetScopeContextAsync(int scopeId, SetScopeContextRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<RegItem?> FindItemByGuidAsync(Guid guid, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<RegItem> CreateItemAsync(CreateItemRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<RegSerialDetail?> FindSerialByGuidAsync(Guid guid, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<RegSerialDetail> CreateSerialAsync(CreateSerialRequest request, CancellationToken ct) =>
        throw new NotSupportedException();
}

/// <summary>
/// Engine state in memory, so a test can read what a pass chose to remember.
/// </summary>
internal sealed class MemoryApprovalState : IRegLocationEngineStateStore
{
    public RegLocationEngineState Current { get; private set; } = new();

    public Task<(RegLocationEngineState State, ETag ETag)> ReadAsync(CancellationToken ct) =>
        Task.FromResult((Current, new ETag("v1")));

    public Task<bool> TryWriteAsync(RegLocationEngineState state, ETag expected, CancellationToken ct)
    {
        Current = state;
        return Task.FromResult(true);
    }

    public Task ClearAsync(CancellationToken ct)
    {
        Current = new RegLocationEngineState();
        return Task.CompletedTask;
    }
}

/// <summary>
/// CIR is switched off in these tests, so a call here means a routing test has
/// quietly become a registration test.
/// </summary>
internal sealed class UnusedCir : ICirClient
{
    public Task<int> RegisterEntriesAsync(CreateRegistryRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<int> CancelEntriesAsync(IReadOnlyList<CirEntryIdentifier> entries, CancellationToken ct) =>
        throw new NotSupportedException();
}
