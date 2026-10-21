using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MmsEngine.Application;
using MmsEngine.Infrastructure.Cir;
using MmsEngine.Infrastructure.Mms;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Bods;
using Oiie.Ccom.Types;
using Oiie.Isbm.Client;
using SimHost.Tests.Sc01;
using Xunit;

namespace SimHost.Tests;

public sealed class MmsSegmentIngestionTests
{
    [Fact]
    public async Task Approved_segment_maps_exactly_and_replay_renames_the_same_MMS_row()
    {
        var f = new Fixture();
        await f.Bootstrap();
        await f.Publish(f.Segment);
        var report = await f.Service.DrainAsync();
        Assert.Equal(1, report.MessagesAcknowledged);
        Assert.Equal(1, report.AssetsRegistered);
        Assert.Equal(0, report.Failed);
        var row = Assert.Single(f.Mms.Rows).Value;
        Assert.Equal(new LightUnitUpsert(f.Segment.UUID, "WW01", null, null, null, 9, null), row);
        Assert.NotEqual(f.Segment.FullName, row.LightUnitName);
        var registration = Assert.Single(f.Cir.EquivalentRequests);
        Assert.Equal("FunctionalLocation", registration.CategoryId);
        Assert.Equal("REG-LOCATION", registration.CategorySourceId);
        Assert.Equal("7", registration.ExistingIdInSource);
        Assert.Equal("ASSET", registration.DestinationCategoryId);
        Assert.Equal("MMS", registration.DestinationCategorySourceId);
        Assert.Equal("101", registration.Entry.IdInSource);
        Assert.Equal(f.Segment.UUID, registration.Entry.Cirid);

        f.Segment.ShortName = "WW01-corrected";
        await f.Publish(f.Segment);
        // A fresh process must reuse the stable subscription and native upsert key.
        var replay = await f.NewService().DrainAsync();
        Assert.Equal(1, replay.MessagesAcknowledged);
        Assert.Equal("WW01-corrected", Assert.Single(f.Mms.Rows).Value.LightUnitName);
        Assert.Single(f.Cir.EquivalentRequests);
        Assert.All(f.Broker.SubscriberIds, id => Assert.Equal($"MMS:segments:{f.Site:D}", id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CIR_failure_after_MMS_write_retains_message_and_retry_is_idempotent(bool responseLost)
    {
        var f = new Fixture();
        await f.Bootstrap();
        await f.Publish(f.Segment);
        f.Cir.FailEquivalent = true;
        f.Cir.CommitBeforeFailure = responseLost;
        var failed = await f.Service.DrainAsync();
        Assert.Equal(1, failed.UnitsUpserted);
        Assert.Equal(0, failed.MessagesAcknowledged);
        Assert.Equal(1, failed.Failed);
        Assert.Single(f.Broker.MessagesOn(f.Channel));
        Assert.Single(f.Mms.Rows);

        f.Cir.FailEquivalent = false;
        var retried = await f.Service.DrainAsync();
        Assert.Equal(1, retried.MessagesAcknowledged);
        Assert.Equal(2, f.Mms.Writes.Count);
        Assert.Single(f.Mms.Rows);
        Assert.Empty(f.Broker.MessagesOn(f.Channel));
        Assert.Equal(f.Segment.UUID, Assert.Single(f.Cir.Entries, e => e.CategoryId == "ASSET").Entry.Cirid);
    }

    [Theory]
    [InlineData("class")]
    [InlineData("owner")]
    [InlineData("anchor")]
    public async Task Missing_identity_defers_without_writing_and_recovers_on_the_next_poll(string missing)
    {
        var f = new Fixture();
        var category = missing switch
        {
            "class" => "RDL-CLASS",
            "owner" => "ITWIN-SITE",
            _ => "FunctionalLocation"
        };
        var removed = f.Cir.Entries.Where(e => e.CategoryId == category).ToArray();
        f.Cir.Entries.RemoveAll(e => e.CategoryId == category);
        await f.Bootstrap();
        await f.Publish(f.Segment);
        var deferred = await f.Service.DrainAsync();
        Assert.Equal(1, deferred.Deferred);
        Assert.Equal(0, deferred.Failed);
        Assert.Equal(0, deferred.MessagesAcknowledged);
        Assert.Empty(f.Mms.Rows);
        Assert.NotEmpty(deferred.Notes);

        f.Cir.Entries.AddRange(removed);
        Assert.Equal(1, (await f.Service.DrainAsync()).MessagesAcknowledged);
    }

    [Fact]
    public async Task Class_seed_is_verified_in_CIR_and_then_read_back_for_dispatch()
    {
        var f = new Fixture();
        f.Cir.Entries.RemoveAll(e => e.CategoryId == "RDL-CLASS" && e.Entry.SourceId == "MMS");
        await f.Bootstrap();
        await f.Publish(f.Segment);
        var report = await f.Service.DrainAsync();
        Assert.Equal(1, report.MessagesAcknowledged);
        var seeded = Assert.Single(f.Cir.Seeded);
        Assert.Equal(f.ClassId, seeded.Cirid);
        Assert.Equal(IncomingSegmentMapper.LightUnitTable, seeded.IdInSource);
        Assert.True(f.Cir.ClassReads >= 2);
    }

    [Fact]
    public async Task A_configured_class_name_does_not_authorize_an_unregistered_class_UUID()
    {
        var f = new Fixture();
        f.Segment.Type!.UUID = Guid.NewGuid();
        await f.Bootstrap();
        await f.Publish(f.Segment);
        Assert.Equal(1, (await f.Service.DrainAsync()).Deferred);
        Assert.Empty(f.Cir.Seeded);
        Assert.Empty(f.Mms.Rows);
    }

    [Fact]
    public async Task Runtime_dispatch_uses_the_CIR_UUID_mapping_not_config_or_readable_name()
    {
        var f = new Fixture();
        f.Options.InboundRdlTableMap.Clear();
        f.Segment.Type!.ShortName = "renamed";
        await f.Bootstrap();
        await f.Publish(f.Segment);
        Assert.Equal(1, (await f.Service.DrainAsync()).MessagesAcknowledged);
    }

    [Fact]
    public async Task Unsupported_CIR_table_is_reported_and_never_written()
    {
        var f = new Fixture();
        f.Cir.Entries.RemoveAll(e => e.CategoryId == "RDL-CLASS" && e.Entry.SourceId == "MMS");
        f.Cir.Add("RDL-CLASS", "MMS", "ARBITRARY_TABLE", f.ClassId);
        await f.Bootstrap();
        await f.Publish(f.Segment);
        var report = await f.Service.DrainAsync();
        Assert.Equal(1, report.Deferred);
        Assert.Contains(report.Notes, n => n.Contains("ARBITRARY_TABLE"));
        Assert.Empty(f.Mms.Rows);
    }

    [Theory]
    [InlineData("RDL-CLASS")]
    [InlineData("ITWIN-SITE")]
    public async Task Ambiguous_category_qualified_mappings_are_not_guessed(string category)
    {
        var f = new Fixture();
        f.Cir.Add(category, "other-registrar", "99", category == "RDL-CLASS" ? f.ClassId : f.Site, "MMS");
        await f.Bootstrap();
        await f.Publish(f.Segment);
        var report = await f.Service.DrainAsync();
        Assert.Equal(1, report.Failed);
        Assert.Empty(f.Mms.Rows);
        Assert.Single(f.Broker.MessagesOn(f.Channel));
    }

    [Theory]
    [InlineData("site")]
    [InlineData("uuid")]
    [InlineData("type")]
    [InlineData("name")]
    [InlineData("native-key")]
    public async Task Invalid_segment_facts_are_retained_without_writes(string invalid)
    {
        var f = new Fixture();
        switch (invalid)
        {
            case "site": f.Segment.RegistrationSite!.UUID = Guid.NewGuid(); break;
            case "uuid": f.Segment.UUID = Guid.Empty; break;
            case "type": f.Segment.Type = null; break;
            case "name": f.Segment.ShortName = ""; break;
            case "native-key": f.Segment.IDInInfoSource = ""; break;
        }
        await f.Bootstrap();
        await f.Publish(f.Segment);
        var report = await f.Service.DrainAsync();
        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.MessagesAcknowledged);
        Assert.Empty(f.Mms.Rows);
    }

    [Theory]
    [InlineData("ENG", "Replace")]
    [InlineData("REG-LOCATION", "Delete")]
    public async Task Nonapproved_or_unsupported_actions_cannot_change_MMS(string sender, string action)
    {
        var f = new Fixture();
        await f.Bootstrap();
        await f.Publish([f.Segment], sender, action);
        Assert.Equal(1, (await f.Service.DrainAsync()).Failed);
        Assert.Empty(f.Mms.Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_rejections_retain_the_publication_for_repair_or_retry(bool transient)
    {
        var f = new Fixture();
        f.Mms.RejectId = f.Segment.UUID;
        f.Mms.TransientRejection = transient;
        await f.Bootstrap();
        await f.Publish(f.Segment);
        var failed = await f.Service.DrainAsync();
        Assert.Equal(1, failed.Failed);
        Assert.Equal(0, failed.MessagesAcknowledged);
        Assert.Empty(f.Cir.EquivalentRequests);
        f.Mms.RejectId = null;
        Assert.Equal(1, (await f.Service.DrainAsync()).MessagesAcknowledged);
    }

    [Fact]
    public async Task Partially_processed_publication_replays_completed_units_without_duplicates()
    {
        var f = new Fixture();
        var second = f.CreateSegment(Guid.NewGuid(), "8");
        f.Cir.Add("FunctionalLocation", "REG-LOCATION", "8", second.UUID);
        f.Mms.RejectId = second.UUID;
        await f.Bootstrap();
        await f.Publish([f.Segment, second]);
        var failed = await f.Service.DrainAsync();
        Assert.Equal(1, failed.UnitsUpserted);
        Assert.Equal(0, failed.MessagesAcknowledged);
        f.Mms.RejectId = null;
        Assert.Equal(1, (await f.Service.DrainAsync()).MessagesAcknowledged);
        Assert.Equal(2, f.Mms.Rows.Count);
        Assert.Equal(2, f.Cir.Entries.Count(e => e.CategoryId == "ASSET"));
    }

    [Fact]
    public async Task Existing_native_asset_bound_to_another_UUID_is_not_treated_as_success()
    {
        var f = new Fixture();
        f.Cir.Add("ASSET", "MMS", "101", Guid.NewGuid());
        await f.Bootstrap();
        await f.Publish(f.Segment);
        Assert.Equal(1, (await f.Service.DrainAsync()).Failed);
        Assert.Single(f.Broker.MessagesOn(f.Channel));
        Assert.Empty(f.Cir.EquivalentRequests);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-an-owner-id")]
    public async Task Invalid_CIR_owner_keys_are_not_guessed(string nativeId)
    {
        var f = new Fixture();
        f.Cir.Entries.RemoveAll(e => e.CategoryId == "ITWIN-SITE");
        f.Cir.Add("ITWIN-SITE", "MMS", nativeId, f.Site);
        await f.Bootstrap();
        await f.Publish(f.Segment);
        Assert.Equal(1, (await f.Service.DrainAsync()).Failed);
        Assert.Empty(f.Mms.Rows);
    }

    [Fact]
    public async Task One_blocked_site_does_not_prevent_an_independent_site_from_draining()
    {
        var f = new Fixture();
        await f.Bootstrap();
        var otherSite = Guid.NewGuid();
        var other = f.CreateSegment(Guid.NewGuid(), "8");
        other.RegistrationSite!.UUID = otherSite;
        f.Cir.Add("ITWIN-SITE", "MMS", "10", otherSite);
        f.Cir.Add("FunctionalLocation", "REG-LOCATION", "8", other.UUID);
        await f.Broker.CreateChannelAsync(f.Options.SegmentsChannelUriFor(otherSite), IsbmChannelType.Publication);
        await f.Service.BootstrapAsync();
        var bod = new SyncSegments("Replace");
        bod.ApplicationArea.Sender = new Sender { LogicalID = "REG-LOCATION" };
        bod.With(other);
        var publisher = await f.Broker.OpenPublicationSessionAsync(f.Options.SegmentsChannelUriFor(otherSite));
        await f.Broker.PostPublicationAsync(publisher, bod.CreateDocument().Root!, f.Options.SegmentsTopics);
        f.Cir.Entries.RemoveAll(e => e.CategoryId == "ITWIN-SITE" && e.Entry.Cirid == f.Site);
        await f.Publish(f.Segment);
        var report = await f.Service.DrainAsync();
        Assert.Equal(1, report.Deferred);
        Assert.Equal(1, report.MessagesAcknowledged);
        Assert.Equal(other.UUID, Assert.Single(f.Mms.Rows).Key);
        Assert.Single(f.Broker.MessagesOn(f.Channel));
    }

    [Fact]
    public async Task Duplicate_segment_UUIDs_are_rejected_before_any_MMS_write()
    {
        var f = new Fixture();
        await f.Bootstrap();
        await f.Publish([f.Segment, f.Segment]);
        Assert.Equal(1, (await f.Service.DrainAsync()).Failed);
        Assert.Empty(f.Mms.Rows);
    }

    [Fact]
    public async Task Bootstrap_selects_operations_channels_and_opens_all_before_the_poll_budget()
    {
        var f = new Fixture();
        var otherSite = Guid.NewGuid();
        await f.Broker.CreateChannelAsync(f.Options.SegmentsChannelUriFor(otherSite), IsbmChannelType.Publication);
        await f.Broker.CreateChannelAsync($"/acme/{f.Site:D}/engineering/publication", IsbmChannelType.Publication);
        await f.Broker.CreateChannelAsync($"/another/{f.Site:D}/operations/publication", IsbmChannelType.Publication);
        await f.Broker.CreateChannelAsync("/acme/enterprise/sites/publication", IsbmChannelType.Publication);
        f.Options.MaxMessagesPerPoll = 1;
        var bootstrap = await f.Bootstrap();
        Assert.Equal(2, bootstrap.SubscriptionsReady);
        Assert.Equal(0, bootstrap.MessagesRead);
        Assert.All(f.Broker.SubscriptionChannels, c => Assert.EndsWith("/operations/publication", c));
        Assert.All(f.Broker.SubscribedTopics, t => Assert.Equal(["oiie:sc02/ccom:SyncSegments"], t));
        f.Options.ITwinFederationId = f.Site;
        Assert.Equal(1, (await f.NewService().BootstrapAsync()).SitesDiscovered);
    }

    [Fact]
    public async Task Reset_and_expired_sessions_reopen_the_same_durable_subscriber()
    {
        var f = new Fixture();
        await f.Bootstrap();
        await f.Publish(f.Segment);
        f.Broker.FailNextSubscriptionRead = true;
        var report = await f.Service.DrainAsync();
        Assert.Equal(1, report.SessionsReopened);
        Assert.Equal(1, report.MessagesAcknowledged);
        await f.Service.ResetAsync();
        Assert.Equal(0, f.Service.SubscriptionCount);
        await f.Publish(f.Segment);
        Assert.Equal(1, (await f.Service.DrainAsync()).MessagesAcknowledged);
        Assert.All(f.Broker.SubscriberIds, id => Assert.Equal($"MMS:segments:{f.Site:D}", id));
    }

    [Fact]
    public async Task Bootstrap_revalidates_cached_sessions_before_saying_subscribers_are_ready()
    {
        var f = new Fixture();
        await f.Bootstrap();
        await f.Broker.CloseSessionAsync(IsbmSessionKind.Subscription,
            Assert.Single(f.Broker.SubscriptionSessions));
        var repaired = await f.Service.BootstrapAsync();
        Assert.Equal(1, repaired.SessionsReopened);
        Assert.Equal(1, repaired.SubscriptionsReady);
        Assert.Equal(2, f.Broker.SubscriptionSessions.Count);
        await f.Publish(f.Segment);
        Assert.Equal(1, (await f.Service.DrainAsync()).MessagesAcknowledged);
    }

    [Fact]
    public void MMS_default_subscription_matches_the_REG_LOCATION_operations_publication()
    {
        var site = Guid.NewGuid();
        var publisher = new RegLocationEngine.Application.RegLocationEngineOptions();
        var subscriber = new MmsEngineOptions();
        Assert.Contains(subscriber.SegmentsChannelUriFor(site), publisher.ApprovalChannelsFor(site));
        Assert.Equal(publisher.Topics, subscriber.SegmentsTopics);
        Assert.Equal(publisher.LogicalId, subscriber.ApprovedLogicalId);
        Assert.Equal(publisher.SourceId, subscriber.ApprovedSourceId);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("no-cir")]
    [InlineData("no-register")]
    public async Task Unready_ingestion_never_acknowledges_or_writes(string setting)
    {
        var f = new Fixture();
        if (setting == "disabled") f.Options.SegmentsIngestEnabled = false;
        if (setting == "no-cir") f.Options.CirBaseUrl = null;
        if (setting == "no-register") f.Options.RegisterInCir = false;
        await f.Bootstrap();
        await f.Publish(f.Segment);
        var report = await f.Service.DrainAsync();
        Assert.Equal(0, report.MessagesAcknowledged);
        Assert.Empty(f.Mms.Rows);
        Assert.NotEmpty(report.Notes);
        Assert.Single(f.Broker.MessagesOn(f.Channel));
    }

    [Fact]
    public async Task Malformed_BOD_does_not_get_acknowledged()
    {
        var f = new Fixture();
        await f.Bootstrap();
        var publisher = await f.Broker.OpenPublicationSessionAsync(f.Channel);
        await f.Broker.PostPublicationAsync(publisher, new XElement("invalid"), f.Options.SegmentsTopics);
        Assert.Equal(1, (await f.Service.DrainAsync()).Failed);
        Assert.Single(f.Broker.MessagesOn(f.Channel));
    }

    [Fact]
    public async Task Cancellation_is_not_turned_into_an_ingestion_report()
    {
        var f = new Fixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.DrainAsync(cts.Token));
    }

    private sealed class Fixture
    {
        public readonly Guid Site = Guid.NewGuid();
        public readonly Guid ClassId = Guid.NewGuid();
        public readonly FakeIsbmBroker Broker = new();
        public readonly RecordingMms Mms = new();
        public readonly RecordingCir Cir = new();
        public readonly MmsEngineOptions Options = new()
        {
            MmsBaseUrl = "https://mms.example/api",
            CirBaseUrl = "https://cir.example/api",
            SegmentsIngestEnabled = true
        };
        public Segment Segment { get; }
        public SegmentIngestionService Service { get; }
        public string Channel => Options.SegmentsChannelUriFor(Site);

        public Fixture()
        {
            Segment = CreateSegment(Guid.NewGuid(), "7");
            Cir.Add("RDL-CLASS", "RDL", "rdl:Streetlight", ClassId);
            Cir.Add("RDL-CLASS", "MMS", IncomingSegmentMapper.LightUnitTable, ClassId);
            Cir.Add("ITWIN-SITE", "MMS", "9", Site);
            Cir.Add("FunctionalLocation", "REG-LOCATION", "7", Segment.UUID);
            Service = NewService();
        }

        public Segment CreateSegment(Guid id, string nativeId) => new()
        {
            UUID = id, IDInInfoSource = nativeId,
            ShortName = "WW01", FullName = "WW 01", Description = "Not a destination column",
            RegistrationSite = new Site { UUID = Site },
            Type = new SegmentType { UUID = ClassId, ShortName = "rdl:Streetlight" }
        };

        public SegmentIngestionService NewService() => new(Broker, Mms, new IncomingSegmentMapper(),
            new SegmentIdentityService(Cir, Microsoft.Extensions.Options.Options.Create(Options)),
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<SegmentIngestionService>.Instance);

        public async Task<SegmentIngestionReport> Bootstrap()
        {
            await Broker.CreateChannelAsync(Channel, IsbmChannelType.Publication);
            return await Service.BootstrapAsync();
        }

        public Task Publish(Segment segment) => Publish([segment]);
        public async Task Publish(Segment[] segments, string sender = "REG-LOCATION", string action = "Replace")
        {
            var bod = new SyncSegments(action);
            bod.ApplicationArea.Sender = new Sender { LogicalID = sender };
            foreach (var segment in segments) bod.With(segment);
            var publisher = await Broker.OpenPublicationSessionAsync(Channel);
            await Broker.PostPublicationAsync(publisher, bod.CreateDocument().Root!, Options.SegmentsTopics);
        }
    }

    private sealed class RecordingMms : IMmsClient
    {
        public Dictionary<Guid, LightUnitUpsert> Rows { get; } = [];
        public List<LightUnitUpsert> Writes { get; } = [];
        public Guid? RejectId { get; set; }
        public bool TransientRejection { get; set; }
        private readonly Dictionary<Guid, long> _keys = [];

        public Task<LightUnitUpsertResult> UpsertLightUnitsAsync(IReadOnlyList<LightUnitUpsert> units, CancellationToken ct)
        {
            var unit = Assert.Single(units);
            if (unit.ExtAssetId == RejectId)
                return Task.FromResult(new LightUnitUpsertResult([],
                    [new(unit.ExtAssetId.ToString(), "Owner unavailable", TransientRejection)]));
            var created = !Rows.ContainsKey(unit.ExtAssetId);
            if (created) _keys[unit.ExtAssetId] = 101 + Rows.Count;
            Rows[unit.ExtAssetId] = unit;
            Writes.Add(unit);
            return Task.FromResult(new LightUnitUpsertResult(
                [new(_keys[unit.ExtAssetId], unit.ExtAssetId, created)], []));
        }
        public Task<LightSystemUpsertResult> UpsertLightSystemsAsync(IReadOnlyList<LightSystemUpsert> systems, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<MmsLookup>> GetLightSystemClassCodesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<OwnerUpsertResult> UpsertOwnersAsync(IReadOnlyList<OwnerUpsert> owners, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<MmsLookup>> GetOwnersAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingCir : ICirClient
    {
        public List<LocatedCirEntry> Entries { get; } = [];
        public List<CirEntry> Seeded { get; } = [];
        public List<EquivalentEntryRequest> EquivalentRequests { get; } = [];
        public bool FailEquivalent { get; set; }
        public bool CommitBeforeFailure { get; set; }
        public int ClassReads { get; private set; }

        public void Add(string category, string registrar, string nativeId, Guid id, string? source = null) =>
            Entries.Add(new(category, registrar,
                new CirEntry(nativeId, source ?? registrar, id, "acme", nativeId, null, [])));

        public Task<IReadOnlyList<LocatedCirEntry>> FindEntriesAsync(string registryId, string categoryId,
            Guid? cirid, string? sourceId, string? idInSource, CancellationToken ct)
        {
            Assert.Equal("acme", registryId);
            if (categoryId == "RDL-CLASS") ClassReads++;
            return Task.FromResult<IReadOnlyList<LocatedCirEntry>>(
                Entries.Where(e => e.CategoryId == categoryId && e.Entry.Inactive != true
                    && (cirid is null || e.Entry.Cirid == cirid)
                    && (sourceId is null || e.Entry.SourceId == sourceId)
                    && (idInSource is null || e.Entry.IdInSource == idInSource)).ToArray());
        }

        public Task EnsureEntryAsync(string registryId, string categoryId, string categorySourceId,
            CirEntry entry, CancellationToken ct)
        {
            Seeded.Add(entry);
            Entries.Add(new(categoryId, categorySourceId, entry));
            return Task.CompletedTask;
        }

        public Task CreateEquivalentEntryAsync(EquivalentEntryRequest request, CancellationToken ct)
        {
            EquivalentRequests.Add(request);
            var anchor = Assert.Single(Entries, e => e.CategoryId == request.CategoryId
                && e.CategorySourceId == request.CategorySourceId
                && e.Entry.IdInSource == request.ExistingIdInSource
                && e.Entry.SourceId == request.ExistingSourceId);
            if (!FailEquivalent || CommitBeforeFailure)
                Entries.Add(new(request.DestinationCategoryId, request.DestinationCategorySourceId,
                    request.Entry with { Cirid = anchor.Entry.Cirid }));
            if (FailEquivalent) throw new CirClientException("CIR unavailable");
            return Task.CompletedTask;
        }

        public Task<int> RegisterEntriesAsync(CreateRegistryRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> FindIdInSourceAsync(Guid cirid, string sourceId, CancellationToken ct) => throw new NotSupportedException();
    }
}
