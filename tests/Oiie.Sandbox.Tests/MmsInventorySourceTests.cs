using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Oiie.Cir.Client;
using Oiie.Sandbox.Api.Providers;
using Xunit;

namespace SimHost.Tests;

public sealed class MmsInventorySourceTests
{
    private static readonly Guid Site = Guid.NewGuid();

    [Fact]
    public async Task Inventory_uses_exact_site_mapping_and_only_matching_MMS_owner_rows()
    {
        using var cir = Http(async (request, ct) =>
        {
            Assert.Equal("/api/queries/registry", request.RequestUri!.AbsolutePath);
            var query = await request.Content!.ReadFromJsonAsync<GetRegistryRequest>(ct);
            var filter = Assert.Single(query!.Filter);
            Assert.Equal("acme", filter.RegistryFilter!.Id);
            Assert.Equal("ITWIN-SITE", filter.CategoryFilter!.Id);
            Assert.Equal("MMS", filter.CategoryFilter.SourceId);
            Assert.Equal("MMS", filter.EntryFilter!.SourceId);
            Assert.Equal(Site, filter.EntryFilter.Cirid);
            Assert.False(filter.EntryFilter.Inactive);
            return Graph(
                new("other", [], [new("ITWIN-SITE", "MMS", [], [Entry("88")])]),
                new("acme", [],
                [
                    new("ASSET", "MMS", [], [Entry("99")]),
                    new("ITWIN-SITE", "CMS", [], [Entry("77")]),
                    new("ITWIN-SITE", "MMS", [],
                    [
                        Entry("5"), Entry("66") with { Inactive = true },
                        Entry("33") with { Cirid = Guid.NewGuid() },
                        Entry("44") with { SourceId = "CMS" }
                    ])
                ]));
        });
        using var mms = Http((request, _) =>
        {
            Assert.Equal("/api/lightunits", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Reply(new[]
            {
                Unit(2, "XX01", 5), Unit(3, "Other site's unit", 9),
                Unit(4, "Unowned", null), Unit(5, "AA01", 5)
            }));
        });
        var result = await Source(cir, mms).ListAsync(Site, CancellationToken.None);
        Assert.True(result.SiteMapped);
        Assert.Equal(Site, result.ITwinId);
        Assert.Equal(5, result.OwnerId);
        Assert.Equal(2, result.Count);
        Assert.Equal(["AA01", "XX01"], result.Items.Select(i => i.LightUnitName));
        Assert.Null(result.Items[1].LightSystemId);
        Assert.NotNull(result.Items[1].ExtAssetId);
    }

    [Fact]
    public async Task Missing_site_mapping_is_explicit_and_never_falls_back_to_all_assets()
    {
        using var cir = Http((_, _) => Task.FromResult(Graph()));
        using var mms = Http((_, _) => throw new InvalidOperationException("Unscoped MMS read."));
        var result = await Source(cir, mms).ListAsync(Site, CancellationToken.None);
        Assert.False(result.SiteMapped);
        Assert.Null(result.OwnerId);
        Assert.Equal(0, result.Count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-native-key")]
    public async Task Invalid_owner_mappings_are_errors_not_empty_inventory(string id)
    {
        using var cir = Http((_, _) => Task.FromResult(SiteGraph(Entry(id))));
        using var mms = Http((_, _) => throw new InvalidOperationException("Unexpected MMS read."));
        var error = await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            Source(cir, mms).ListAsync(Site, CancellationToken.None));
        Assert.Equal("CIR", error.Provider);
    }

    [Fact]
    public async Task Ambiguous_mapping_is_not_resolved_by_picking_the_first()
    {
        using var cir = Http((_, _) => Task.FromResult(SiteGraph(Entry("5"), Entry("6"))));
        using var mms = Http((_, _) => throw new InvalidOperationException("Unexpected MMS read."));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            Source(cir, mms).ListAsync(Site, CancellationToken.None));
    }

    [Theory]
    [InlineData("CIR")]
    [InlineData("MMS")]
    public async Task Provider_failures_name_the_failed_dependency(string dependency)
    {
        using var cir = Http((_, _) => Task.FromResult(
            dependency == "CIR" ? new(HttpStatusCode.ServiceUnavailable) : SiteGraph(Entry("5"))));
        using var mms = Http((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var error = await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            Source(cir, mms).ListAsync(Site, CancellationToken.None));
        Assert.Equal(dependency, error.Provider);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"registry":[{"id":"acme","categories":null}]}""")]
    public async Task Malformed_CIR_success_is_not_reported_as_no_mapping(string body)
    {
        using var cir = Http((_, _) => Task.FromResult(Raw(body)));
        using var mms = Http((_, _) => throw new InvalidOperationException("Unexpected MMS read."));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            Source(cir, mms).ListAsync(Site, CancellationToken.None));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""[{"lightUnitId":0,"lightUnitName":"XX01"}]""")]
    [InlineData("""[{"lightUnitId":2,"lightUnitName":""}]""")]
    [InlineData("""[{"lightUnitId":2,"lightUnitName":"XX01"},{"lightUnitId":2,"lightUnitName":"YY01"}]""")]
    public async Task Malformed_MMS_success_is_not_an_empty_or_successful_inventory(string body)
    {
        using var cir = Http((_, _) => Task.FromResult(SiteGraph(Entry("5"))));
        using var mms = Http((_, _) => Task.FromResult(Raw(body)));
        var error = await Assert.ThrowsAsync<ProviderUnavailableException>(() =>
            Source(cir, mms).ListAsync(Site, CancellationToken.None));
        Assert.Equal("MMS", error.Provider);
    }

    [Fact]
    public async Task Empty_twin_is_rejected_before_any_provider_read()
    {
        using var cir = Http((_, _) => throw new InvalidOperationException("Unexpected request."));
        using var mms = Http((_, _) => throw new InvalidOperationException("Unexpected request."));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Source(cir, mms).ListAsync(Guid.Empty, CancellationToken.None));
    }

    [Theory]
    [InlineData("2026-10-21T09:32:36.464", "2026-10-21T09:32:36.464Z")]
    [InlineData("2026-10-21T09:32:36.464Z", "2026-10-21T09:32:36.464Z")]
    [InlineData("2026-10-21T11:32:36.464+02:00", "2026-10-21T09:32:36.464Z")]
    public async Task Provider_SQL_timestamps_are_UTC_even_without_an_offset(string timestamp, string expected)
    {
        using var cir = Http((_, _) => Task.FromResult(SiteGraph(Entry("5"))));
        using var mms = Http((_, _) => Task.FromResult(Raw($$"""
            [{"lightUnitId":2,"lightUnitName":"XX01","ownerId":5,"dateUpdate":"{{timestamp}}"}]
            """)));
        var result = await Source(cir, mms).ListAsync(Site, CancellationToken.None);
        Assert.Equal(DateTimeOffset.Parse(expected), Assert.Single(result.Items).DateUpdate);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_relabelled_as_a_provider_outage()
    {
        using var cancellation = new CancellationTokenSource();
        using var cir = Http((_, ct) =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation not forwarded.");
        });
        using var mms = Http((_, _) => throw new InvalidOperationException("Unexpected MMS read."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Source(cir, mms).ListAsync(Site, cancellation.Token));
    }

    private static MmsInventorySource Source(HttpClient cir, HttpClient mms) =>
        new(cir, new MmsProviderClient(mms, NullLogger<MmsProviderClient>.Instance),
            new ConfigurationBuilder().Build(), NullLogger<MmsInventorySource>.Instance);
    private static CirEntry Entry(string id) => new(id, "MMS", Site, "acme", "District", null, []);
    private static MmsLightUnitDto Unit(long id, string name, long? owner) =>
        new(id, name, null, null, null, owner, Guid.NewGuid(), null, DateTimeOffset.UtcNow);
    private static HttpResponseMessage SiteGraph(params CirEntry[] entries) =>
        Graph(new CirRegistry("acme", [], [new("ITWIN-SITE", "MMS", [], entries)]));
    private static HttpResponseMessage Graph(params CirRegistry[] registries) =>
        Reply(new GetRegistryResponse(registries));
    private static HttpResponseMessage Reply<T>(T body) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    private static HttpResponseMessage Raw(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Handler(send)) { BaseAddress = new("https://provider.example/api/") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
