using Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RegLocationEngine.Application;
using Oiie.Cir.Client;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using RegLocationEngine.Infrastructure.RegLocation;
using RegLocationEngine.Infrastructure.State;
using SimHost.Tests.Sc01;
using SimHost.Application.Topology;
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
        Assert.All(broker.PublicationChannels, c => Assert.Equal(options.Value.ChannelUriFor(SiteA), c));
        var segments = broker.MessagesOn(options.Value.ChannelUriFor(SiteA))
            .SelectMany(m => BodEnvelope.Parse(m.RawContent).NounsAs(e => new Segment(e)));
        Assert.Equal(new[] { SiteA, SiteB }, segments.Select(s => s.RegistrationSite!.UUID).Order().ToArray());
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

    [Fact]
    public async Task Approval_reaches_both_routes_with_identical_classified_content_and_SC02_topic()
    {
        var registry = new RoutingRegistry
        {
            Scopes = { [ScopeA] = SiteA },
            Approved = { Tag(1, ScopeA), Tag(2, ScopeA) }
        };
        var broker = new FakeIsbmBroker();
        var options = EngineOptions();
        options.Value.ReturnApprovalsToEngineering = true;
        var cir = new ApprovalCir();
        var state = new MemoryApprovalState();
        var service = Service(registry, broker, options, state, cir);

        var report = await service.SweepAsync();

        Assert.Empty(report.Errors);
        Assert.Equal(2, report.TagsPublished);
        Assert.Equal(options.Value.ApprovalChannelsFor(SiteA), broker.PublicationChannels);
        Assert.All(broker.PostedTopics, topics => Assert.Equal(["oiie:sc02/ccom:SyncSegments"], topics));
        var operations = Assert.Single(broker.MessagesOn(options.Value.ChannelUriFor(SiteA)));
        var engineering = Assert.Single(broker.MessagesOn(options.Value.InboundChannelUriFor(SiteA)));
        Assert.Equal(operations.RawContent, engineering.RawContent);
        var segments = BodEnvelope.Parse(operations.RawContent).NounsAs(e => new Segment(e)).ToArray();
        Assert.Equal(2, segments.Length);
        Assert.All(segments, s =>
        {
            Assert.Equal(SiteA, s.RegistrationSite!.UUID);
            Assert.Equal(cir.ClassIdentity, s.Type!.UUID);
        });
        var query = Assert.Single(Assert.Single(cir.Queries));
        Assert.Equal("acme", query.RegistryFilter!.Id);
        Assert.Equal("RDL-CLASS", query.CategoryFilter!.Id);
        Assert.Equal("REG-LOCATION", query.CategoryFilter.SourceId);
        Assert.Equal("22", query.EntryFilter!.IdInSource);
        Assert.Equal("REG-LOCATION", query.EntryFilter.SourceId);
        Assert.False(query.EntryFilter.Inactive);

        var repeated = await service.SweepAsync();
        Assert.Equal(2, repeated.AlreadyPublished);
        Assert.Equal(2, broker.PublicationChannels.Count);

        await state.ClearAsync(CancellationToken.None);
        var replayed = await service.SweepAsync();
        Assert.Equal(2, replayed.TagsPublished);
        Assert.Equal(4, broker.PublicationChannels.Count);
        Assert.Equal(2, cir.Queries.Count);
    }

    [Fact]
    public async Task A_failed_second_route_leaves_the_revision_pending_for_retry()
    {
        var registry = new RoutingRegistry
        {
            Scopes = { [ScopeA] = SiteA },
            Approved = { Tag(1, ScopeA) }
        };
        var options = EngineOptions();
        options.Value.ReturnApprovalsToEngineering = true;
        var broker = new FakeIsbmBroker { FailPublicationChannel = options.Value.InboundChannelUriFor(SiteA) };
        var state = new MemoryApprovalState();
        var service = Service(registry, broker, options, state);

        var failed = await service.SweepAsync();
        Assert.Single(failed.Errors);
        Assert.Equal(0, failed.TagsPublished);
        Assert.Empty(state.Current.PublishedTags);
        Assert.Single(broker.MessagesOn(options.Value.ChannelUriFor(SiteA)));

        broker.FailPublicationChannel = null;
        var retried = await service.SweepAsync();
        Assert.Empty(retried.Errors);
        Assert.Equal(1, retried.TagsPublished);
        Assert.Single(state.Current.PublishedTags);
        Assert.Single(broker.MessagesOn(options.Value.InboundChannelUriFor(SiteA)));
    }

    [Fact]
    public async Task An_unavailable_operations_route_does_not_withhold_the_engineering_return()
    {
        var registry = new RoutingRegistry
        {
            Scopes = { [ScopeA] = SiteA },
            Approved = { Tag(1, ScopeA) }
        };
        var options = EngineOptions();
        options.Value.ReturnApprovalsToEngineering = true;
        var broker = new FakeIsbmBroker { FailPublicationChannel = options.Value.ChannelUriFor(SiteA) };
        var state = new MemoryApprovalState();

        var report = await Service(registry, broker, options, state).SweepAsync();

        Assert.Single(report.Errors);
        Assert.Equal(0, report.TagsPublished);
        Assert.Single(broker.MessagesOn(options.Value.InboundChannelUriFor(SiteA)));
        Assert.Empty(state.Current.PublishedTags);
    }

    [Theory]
    [InlineData("Proposed")]
    [InlineData("Rejected")]
    public async Task Unapproved_tags_do_not_reach_either_route(string status)
    {
        var tag = Tag(1, ScopeA);
        var registry = new RoutingRegistry
        {
            Scopes = { [ScopeA] = SiteA },
            Approved = { tag with { Tag = tag.Tag with { State = status } } }
        };
        var broker = new FakeIsbmBroker();
        var options = EngineOptions();
        options.Value.ReturnApprovalsToEngineering = true;
        var cir = new ApprovalCir();

        var report = await Service(registry, broker, options, cir: cir).SweepAsync();

        Assert.Single(report.Skipped);
        Assert.Empty(broker.PublicationChannels);
        Assert.Empty(cir.Queries);
    }

    [Fact]
    public async Task Missing_class_mapping_is_retried_on_the_same_service_when_CIR_is_seeded()
    {
        var registry = new RoutingRegistry
        {
            Scopes = { [ScopeA] = SiteA },
            Approved = { Tag(1, ScopeA) }
        };
        var broker = new FakeIsbmBroker();
        var cir = new ApprovalCir { Missing = true };
        var state = new MemoryApprovalState();
        var service = Service(registry, broker, EngineOptions(), state, cir);

        var missing = await service.SweepAsync();
        Assert.Contains("no governed CIR mapping", Assert.Single(missing.Skipped));
        Assert.Empty(state.Current.PublishedTags);
        Assert.Empty(broker.PublicationChannels);

        cir.Missing = false;
        Assert.Equal(1, (await service.SweepAsync()).TagsPublished);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CIR_outages_and_ambiguous_classes_are_visible_and_not_recorded(bool unavailable)
    {
        var registry = new RoutingRegistry
        {
            Scopes = { [ScopeA] = SiteA },
            Approved = { Tag(1, ScopeA) }
        };
        var broker = new FakeIsbmBroker();
        var cir = new ApprovalCir { Unavailable = unavailable, Ambiguous = !unavailable };
        var state = new MemoryApprovalState();

        var report = await Service(registry, broker, EngineOptions(), state, cir).SweepAsync();

        Assert.Single(report.Errors);
        Assert.Empty(broker.PublicationChannels);
        Assert.Empty(state.Current.PublishedTags);
    }

    [Fact]
    public async Task A_scope_that_acquires_a_site_is_reconsidered_by_the_next_sweep()
    {
        var registry = new RoutingRegistry
        {
            Scopes = { [ScopeA] = null },
            Approved = { Tag(1, ScopeA) }
        };
        var broker = new FakeIsbmBroker();
        var service = Service(registry, broker, EngineOptions());

        Assert.Single((await service.SweepAsync()).Skipped);
        registry.Scopes[ScopeA] = SiteA;
        Assert.Equal(1, (await service.SweepAsync()).TagsPublished);
    }

    [Fact]
    public async Task Approved_class_is_resolved_from_the_current_tag_not_the_original_design()
    {
        var tag = Tag(1, ScopeA);
        tag = tag with { Tag = tag.Tag with { ClassId = 99 } };
        var registry = new RoutingRegistry
        {
            Scopes = { [ScopeA] = SiteA },
            Approved = { tag }
        };
        var broker = new FakeIsbmBroker();
        var cir = new ApprovalCir();
        var options = EngineOptions();

        Assert.Equal(1, (await Service(registry, broker, options, cir: cir).SweepAsync()).TagsPublished);
        Assert.Equal("99", Assert.Single(Assert.Single(cir.Queries)).EntryFilter!.IdInSource);
        var message = Assert.Single(broker.MessagesOn(options.Value.ChannelUriFor(SiteA)));
        var segment = Assert.Single(BodEnvelope.Parse(message.RawContent).NounsAs(e => new Segment(e)));
        Assert.Equal("99", segment.Type!.IDInInfoSource);
    }

    [Fact]
    public void Coincident_route_overrides_do_not_duplicate_publications()
    {
        var options = new RegLocationEngineOptions
        {
            ChannelUriOverride = "/legacy/approvals",
            InboundChannelUriOverride = "/legacy/approvals"
        };
        Assert.Equal(["/legacy/approvals"], options.ApprovalChannelsFor(SiteA));
    }

    [Fact]
    public void SC02_segment_topology_matches_both_default_approval_routes_and_their_subscribers()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "Oiie.Sandbox.Core", "Topology")))
            root = root.Parent;
        Assert.NotNull(root);
        var scenario = TopologyLoader.Load(Path.Combine(root.FullName,
            "Oiie.Sandbox.Core", "Topology", "sc02-stewardship-approval.yaml"));
        var channels = scenario.Channels
            .Where(c => c.Topics.Contains("oiie:sc02/ccom:SyncSegments")).ToArray();
        var options = new RegLocationEngineOptions();

        Assert.Equal(options.ApprovalChannelsFor(SiteA).Order(),
            channels.Select(c => c.Resolve("acme", SiteA)).Order());
        Assert.All(channels, c => Assert.Equal("reg-location", c.Publisher));
        Assert.Contains("eng", Assert.Single(channels, c => c.Uri.Contains("/engineering/")).Subscribers);
        Assert.Contains("mms", Assert.Single(channels, c => c.Uri.Contains("/operations/")).Subscribers);
        Assert.All(channels, c => Assert.Equal(options.Topics, c.Topics));
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
            CirBaseUrl = "https://cir.example/api/",
            ReturnApprovalsToEngineering = false,

            // Off: CIR registration is a separate concern from routing, and a
            // fake for it here would assert on nothing these tests are about.
            RegisterInCir = false
        });

    private static RegLocationApprovalService Service(
        IRegLocationClient registry,
        FakeIsbmBroker broker,
        IOptions<RegLocationEngineOptions> options,
        IRegLocationEngineStateStore? state = null,
        ICirClient? cir = null) =>
        new(registry,
            broker,
            cir ?? new ApprovalCir(),
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
/// Class cross-references for the approval publisher. Instance registration
/// is disabled in routing tests.
/// </summary>
internal sealed class ApprovalCir : ICirClient
{
    public Guid ClassIdentity { get; } = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
    public List<IReadOnlyList<CirFilter>> Queries { get; } = [];
    public bool Missing { get; set; }
    public bool Ambiguous { get; set; }
    public bool Unavailable { get; set; }

    public Task<int> RegisterEntriesAsync(CreateRegistryRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<int> CancelEntriesAsync(IReadOnlyList<CirEntryIdentifier> entries, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<CirRegistry>> GetRegistryAsync(
        IReadOnlyList<CirFilter> filters, CancellationToken ct)
    {
        Queries.Add(filters);
        if (Unavailable) throw new CirClientException("CIR unavailable");
        if (Missing) return Task.FromResult<IReadOnlyList<CirRegistry>>([]);
        var id = Assert.Single(filters).EntryFilter!.IdInSource!;
        var entries = new List<CirEntry>
        {
            new(id, "REG-LOCATION", ClassIdentity, "acme", "rdl:Streetlight", null, [])
        };
        if (Ambiguous)
            entries.Add(new(id, "REG-LOCATION", Guid.NewGuid(), "acme", "rdl:Other", null, []));
        return Task.FromResult<IReadOnlyList<CirRegistry>>(
            [new("acme", [], [new("RDL-CLASS", "REG-LOCATION", [], entries)])]);
    }

}

internal sealed class UnusedCir : ICirClient
{
    public Task<int> RegisterEntriesAsync(CreateRegistryRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<int> CancelEntriesAsync(IReadOnlyList<CirEntryIdentifier> entries, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<CirRegistry>> GetRegistryAsync(
        IReadOnlyList<CirFilter> filters, CancellationToken ct) =>
        throw new NotSupportedException();
}
