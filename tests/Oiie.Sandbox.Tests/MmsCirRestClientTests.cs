using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MmsEngine.Infrastructure.Cir;
using Xunit;

namespace SimHost.Tests;

public sealed class MmsCirRestClientTests
{
    private static readonly Guid Identity = Guid.NewGuid();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Query_is_qualified_and_rechecks_registry_category_source_identity_and_activity()
    {
        using var http = Http(async (request, ct) =>
        {
            Assert.Equal("/api/queries/registry", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(Json, ct);
            var filter = Assert.Single(body.GetProperty("filter").EnumerateArray());
            Assert.Equal("acme", filter.GetProperty("registryFilter").GetProperty("id").GetString());
            Assert.Equal("ITWIN-SITE", filter.GetProperty("categoryFilter").GetProperty("id").GetString());
            Assert.Equal(Identity, filter.GetProperty("entryFilter").GetProperty("cirid").GetGuid());
            return Graph(
                new("other", [], [new("ITWIN-SITE", "MMS", [], [Entry()])]),
                new("acme", [],
                [
                    new("ASSET", "MMS", [], [Entry()]),
                    new("ITWIN-SITE", "MMS", [],
                    [
                        Entry(), Entry() with { Inactive = true },
                        Entry() with { SourceId = "CMS" },
                        Entry() with { Cirid = Guid.NewGuid() }
                    ])
                ]));
        });
        var entries = await new CirRestClient(http).FindEntriesAsync(
            "acme", "ITWIN-SITE", Identity, "MMS", null, CancellationToken.None);
        Assert.Equal("9", Assert.Single(entries).Entry.IdInSource);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Class_registration_409_is_accepted_only_for_an_identical_active_mapping(bool conflict)
    {
        using var http = Http((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/registries")
                ? new HttpResponseMessage(HttpStatusCode.Conflict)
                : Graph(new CirRegistry("acme", [], [new("RDL-CLASS", "MMS", [],
                    [Entry() with { Cirid = conflict ? Guid.NewGuid() : Identity }])]))));
        var client = new CirRestClient(http);
        var operation = client.EnsureEntryAsync("acme", "RDL-CLASS", "MMS", Entry(), CancellationToken.None);
        if (conflict) await Assert.ThrowsAsync<CirClientException>(() => operation);
        else await operation;
    }

    [Fact]
    public async Task Older_CIR_is_rejected_before_a_cross_category_command_is_sent()
    {
        var commands = 0;
        using var http = Http((request, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/health")) commands++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(new { status = "healthy" }) });
        });
        await Assert.ThrowsAsync<CirClientException>(() =>
            new CirRestClient(http).CreateEquivalentEntryAsync(Request(), CancellationToken.None));
        Assert.Equal(0, commands);
    }

    [Fact]
    public async Task Equivalent_command_preserves_anchor_and_explicit_destination_fields()
    {
        var commands = 0;
        using var http = Http(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/health")) return Health();
            commands++;
            Assert.Equal("/api/equivalent-entries", request.RequestUri.AbsolutePath);
            var received = Assert.Single(
                (await request.Content!.ReadFromJsonAsync<EquivalentEntryRequest[]>(Json, ct))!);
            Assert.Equal("FunctionalLocation", received.CategoryId);
            Assert.Equal("REG-LOCATION", received.CategorySourceId);
            Assert.Equal("7", received.ExistingIdInSource);
            Assert.Equal("ASSET", received.DestinationCategoryId);
            Assert.Equal("MMS", received.DestinationCategorySourceId);
            Assert.Equal(Identity, received.Entry.Cirid);
            return new(HttpStatusCode.Created);
        });
        await new CirRestClient(http).CreateEquivalentEntryAsync(Request(), CancellationToken.None);
        Assert.Equal(1, commands);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("different-uuid")]
    [InlineData("different-category")]
    [InlineData("missing")]
    public async Task Equivalent_409_requires_exact_destination_identity_verification(string existing)
    {
        using var http = Http((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/health") ? Health()
            : request.RequestUri.AbsolutePath.EndsWith("/equivalent-entries")
                ? new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("Conflict") }
                : Graph(new CirRegistry("acme", [], [new("ASSET",
                    existing == "different-category" ? "other" : "MMS", [],
                    existing == "missing" ? [] :
                    [Entry() with { Cirid = existing == "different-uuid" ? Guid.NewGuid() : Identity }])]))));
        var task = new CirRestClient(http).CreateEquivalentEntryAsync(Request(), CancellationToken.None);
        if (existing == "same") await task;
        else await Assert.ThrowsAsync<CirClientException>(() => task);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Query_HTTP_failure_is_not_a_missing_mapping(HttpStatusCode status)
    {
        using var http = Http((_, _) => Task.FromResult(
            new HttpResponseMessage(status) { Content = new StringContent("failure") }));
        await Assert.ThrowsAsync<CirClientException>(() => new CirRestClient(http).FindEntriesAsync(
            "acme", "ASSET", Identity, "MMS", null, CancellationToken.None));
    }

    private static CirEntry Entry() => new("9", "MMS", Identity, "acme", "WW01", null, []);
    private static EquivalentEntryRequest Request() =>
        new("7", "REG-LOCATION", "acme", "FunctionalLocation", "REG-LOCATION", Entry(), "ASSET", "MMS");
    private static HttpResponseMessage Health() =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new { destinationCategoryEquivalence = true }) };
    private static HttpResponseMessage Graph(params CirRegistry[] registries) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new { registry = registries }, options: Json) };
    private static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Handler(send)) { BaseAddress = new Uri("https://cir.example/api/") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
