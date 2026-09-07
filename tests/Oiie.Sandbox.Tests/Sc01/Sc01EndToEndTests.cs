using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using EngEngine.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Oiie.Isbm.Client.Topology;
using RegLocationEngine.Application;
using SimHost.Application.Topology;
using Xunit;

namespace SimHost.Tests.Sc01;

/// <summary>
/// SC01 end to end, in memory: ENG publishes a design release and REG-LOCATION
/// files it as a proposal.
///
/// The point is the seam between them. Both engines now resolve their channel
/// and topics from the topology rather than from their own settings, and the
/// failure that change is meant to prevent is silent: a publisher and a
/// subscriber on URIs that differ by one character both report success forever
/// while nothing is delivered. A test that stubbed the channel would not see it.
///
/// So the broker here is keyed by channel URI and the two engines are given
/// deliberately *wrong* local settings. If topology is not consulted, ENG and
/// REG-LOCATION land on different channels and no tag is filed. The assertions
/// on the tag are therefore assertions about routing.
///
/// The real Topology YAML and the real TopologyLoader are used. Only the HTTP
/// hop is faked, because a test that hand-built the topology object would be
/// testing the shape it just invented rather than the file that ships.
/// </summary>
public class Sc01EndToEndTests
{
    private static readonly Guid IModelId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ITwinId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ElementGuid = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");

    private const string Enterprise = "acme";
    private const int SiteScopeId = 42;

    /// <summary>The channel SC01's topology declares, resolved for this twin.</summary>
    private static string ExpectedChannel =>
        $"/{Enterprise}/{ITwinId:D}/engineering/publication";

    [Fact]
    public async Task Eng_publishes_a_release_that_reg_location_files_as_a_proposal()
    {
        var broker = new FakeIsbmBroker();
        var topology = TopologyOverHttp();

        var registry = await PublishFromEngAsync(broker, topology);

        // The publication landed on the channel the topology declares, not on
        // either engine's configured channel.
        Assert.Equal([ExpectedChannel], broker.PublicationChannels);
        Assert.Single(broker.MessagesOn(ExpectedChannel));

        // Topics travel with the channel. A subscriber on the right channel
        // filtering for a topic nobody posts under receives nothing, which is
        // the same silent failure by a narrower route.
        Assert.Equal(["oiie:sc01/ccom:SyncSegments"], broker.PostedTopics.Single());

        var report = await IngestIntoRegLocationAsync(broker, topology, registry);

        Assert.Equal([ExpectedChannel], broker.SubscriptionChannels);
        Assert.Equal(["oiie:sc01/ccom:SyncSegments"], broker.SubscribedTopics.Single());

        Assert.Equal(1, report.MessagesRead);
        Assert.Equal(1, report.SegmentsSeen);
        Assert.Equal(0, report.Rejected);
        Assert.Equal(0, report.Failed);
        Assert.Equal(0, report.SiteUnknown);
        Assert.Equal(1, report.TagsProposed);

        var filed = Assert.Single(registry.Created);

        // ENG's federation identity carried through untouched. A minted id here
        // would look like success while creating a second copy of the same
        // location that nothing can reconcile.
        Assert.Equal(ElementGuid, filed.Guid);
        Assert.Equal("PMP-101", filed.Code);

        // Scoped to the site ENG named, not to the engine's configured default.
        Assert.Equal(SiteScopeId, filed.ScopeId);

        // Arrival is not acceptance. If this is ever Approved, the inbound leg
        // has walked straight past the gate it exists to feed.
        Assert.Equal("Proposed", filed.State);

        // Consumed, so a second drain does not refile it.
        Assert.Empty(broker.MessagesOn(ExpectedChannel));
    }

    [Fact]
    public async Task Redelivery_does_not_file_the_location_twice()
    {
        var broker = new FakeIsbmBroker();
        var topology = TopologyOverHttp();

        var registry = await PublishFromEngAsync(broker, topology);

        // The same publication delivered again, as it would be after a crash
        // between filing and removal. The registry is the idempotency check, so
        // the second pass must recognise it.
        var first = await IngestIntoRegLocationAsync(broker, topology, registry);
        await RepublishAsync(broker, topology);
        var second = await IngestIntoRegLocationAsync(broker, topology, registry);

        Assert.Equal(1, first.TagsProposed);
        Assert.Equal(0, second.TagsProposed);
        Assert.Equal(1, second.AlreadyKnown);
        Assert.Single(registry.Created);
    }

    [Fact]
    public async Task A_correction_to_a_promoted_element_overwrites_the_proposed_tag()
    {
        var broker = new FakeIsbmBroker();
        var topology = TopologyOverHttp();

        var registry = await PublishFromEngAsync(broker, topology);
        var first = await IngestIntoRegLocationAsync(broker, topology, registry);

        Assert.Equal(1, first.TagsProposed);

        var filedTagId = Assert.Single(registry.FindTagsByGuidAsync(ElementGuid, CancellationToken.None).Result).Tag.TagId;

        // ENG re-promotes the same element, edited: same FederationGuid, same
        // inbound revision slot, different name. This is what a corrected,
        // re-promoted element looks like on the wire.
        await RepublishAsync(broker, topology, Element() with { UserLabel = "Feed pump (corrected)" });

        var second = await IngestIntoRegLocationAsync(broker, topology, registry);

        // The correction reached REG-LOCATION as a write to the tag this leg
        // already proposed, not as a second tag and not as a no-op.
        Assert.Equal(1, second.TagsProposed);
        Assert.Equal(0, second.AlreadyKnown);
        Assert.Single(registry.Created);
        Assert.Single(registry.Corrected);
        Assert.Equal(filedTagId, registry.Corrected[0].TagId);

        var corrected = Assert.Single(
            await registry.FindTagsByGuidAsync(ElementGuid, CancellationToken.None));
        Assert.Equal("Feed pump (corrected)", corrected.Tag.Name);
        Assert.Equal(filedTagId, corrected.Tag.TagId);

        // Still Proposed: a correction is not an approval, it is the same
        // pending decision with different content for the steward to see.
        Assert.Equal("Proposed", corrected.Tag.State);
    }

    [Fact]
    public async Task Segments_are_deferred_when_the_site_has_no_scope()
    {
        var broker = new FakeIsbmBroker();
        var topology = TopologyOverHttp();

        var registry = await PublishFromEngAsync(broker, topology);

        // A registry that has not yet ingested the site. SyncSites establishes
        // the scope, and this leg must not invent one -- filing against a
        // plausible default would put the location in the wrong plant.
        var empty = new FakeRegLocationClient([]);

        var report = await IngestIntoRegLocationAsync(broker, topology, empty);

        Assert.Equal(1, report.SegmentsSeen);
        Assert.Equal(1, report.SiteUnknown);
        Assert.Equal(0, report.TagsProposed);
        Assert.Empty(empty.Created);

        _ = registry;
    }

    // --- Legs ---------------------------------------------------------------

    private static async Task<FakeRegLocationClient> PublishFromEngAsync(
        FakeIsbmBroker broker, TopologyClient topology)
    {
        await RepublishAsync(broker, topology);

        return new FakeRegLocationClient(new Dictionary<Guid, int> { [ITwinId] = SiteScopeId });
    }

    private static async Task RepublishAsync(FakeIsbmBroker broker, TopologyClient topology, EngElement? element = null)
    {
        var options = Options.Create(new EngEngineOptions
        {
            Enabled = true,
            EngBaseUrl = "https://eng.test/api",
            IModelId = IModelId,
            Enterprise = Enterprise,
            SourceId = "ENG",
            LogicalId = "ENG",
            ParticipantId = "eng",
            ScenarioId = "sc01",

            // Deliberately wrong. If the engine falls back to these instead of
            // consulting the topology, the publication lands somewhere the
            // subscriber is not listening and the test fails on the channel
            // assertion rather than mysteriously later.
            ChannelUriOverride = "/wrong/eng/channel",
            Topics = ["oiie:wrong/ccom:SyncSegments"],
        });

        var service = new EngPublicationService(
            new FakeEngClient(IModelId, ITwinId, Marker(), [element ?? Element()]),
            broker,
            new FakeEngStateStore(),
            new EngSegmentsBuilder(options),
            topology,
            options,
            NullLogger<EngPublicationService>.Instance);

        var report = await service.DrainAsync();

        Assert.Empty(report.Errors);
        Assert.Equal(1, report.MarkersPublished);
    }

    private static async Task<IngestionReport> IngestIntoRegLocationAsync(
        FakeIsbmBroker broker, TopologyClient topology, FakeRegLocationClient registry)
    {
        var options = Options.Create(new RegLocationEngineOptions
        {
            Enabled = true,
            RegLocationBaseUrl = "https://reg.test/api",
            Enterprise = Enterprise,
            SourceId = "REG-LOCATION",
            LogicalId = "REG-LOCATION",
            ParticipantId = "reg-location",
            ScenarioId = "sc01",
            ITwinFederationId = ITwinId,

            // Wrong for the same reason as ENG's, and independently so: both
            // ends previously derived this URI separately, and agreement rested
            // on two settings files happening to match.
            InboundChannelUriOverride = "/wrong/reg/channel",
            InboundTopics = ["oiie:wrong/ccom:SyncSegments"],
        });

        var service = new SegmentIngestionService(
            broker,
            registry,
            new IncomingSegmentMapper(options, NullLogger<IncomingSegmentMapper>.Instance),
            topology,
            options,
            NullLogger<SegmentIngestionService>.Instance);

        return await service.DrainAsync(CancellationToken.None);
    }

    // --- Topology -----------------------------------------------------------

    /// <summary>
    /// A TopologyClient backed by the real Topology YAML, served through a stub
    /// handler in the same JSON shape as GET /admin/topology.
    /// </summary>
    private static TopologyClient TopologyOverHttp()
    {
        var scenarios = TopologyLoader.LoadAll(TopologyRoot());

        Assert.NotEmpty(scenarios);

        var sc01 = Assert.Single(scenarios, s => s.ScenarioId == "sc01");

        var payload = new
        {
            enterprise = Enterprise,
            scenarios = new[]
            {
                new
                {
                    scenarioId = sc01.ScenarioId,
                    name = sc01.Name,
                    scenario = sc01.Scenario,
                    useCase = sc01.UseCase,
                    channels = sc01.Channels.Select(c => new
                    {
                        uri = c.Resolve(Enterprise, ITwinId),
                        template = c.Uri,
                        type = c.Type,
                        publisher = c.Publisher,
                        subscribers = c.Subscribers,
                        topics = c.Topics,
                        description = c.Description,
                        isPerITwin = c.IsPerITwin,
                    }),
                },
            },
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });

        var http = new HttpClient(new StubHandler(json))
        {
            BaseAddress = new Uri("https://sandbox.test/"),
        };

        return new TopologyClient(http, NullLogger<TopologyClient>.Instance);
    }

    /// <summary>
    /// The Topology directory in the repository.
    ///
    /// Walked up to from the test output rather than linked as content, so the
    /// test reads the same files the sandbox deploys. A copy would keep passing
    /// after the originals were edited.
    /// </summary>
    private static string TopologyRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Oiie.Sandbox.Core", "Topology");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate Oiie.Sandbox.Core/Topology above " + AppContext.BaseDirectory);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    // --- Fixtures -----------------------------------------------------------

    private static EngNamedVersion Marker() =>
        new(1, Guid.Parse("33333333-3333-3333-3333-333333333333"), IModelId,
            "Design Release 3", "Third release", "changeset-1", 1, "engineer",
            DateTime.UtcNow, DateTime.UtcNow, 1);

    private static EngElement Element() =>
        new(44732, IModelId, 91, "Mechanical:Pump", ElementGuid, "PMP-101",
            "Feed pump", "Feed pump", null, 1, DateTime.UtcNow, DateTime.UtcNow);
}
