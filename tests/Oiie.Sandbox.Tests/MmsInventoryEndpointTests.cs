using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Oiie.Sandbox.Api.Endpoints;
using Oiie.Sandbox.Api.Middleware;
using Oiie.Sandbox.Api.Providers;
using Xunit;

namespace SimHost.Tests;

public sealed class MmsInventoryEndpointTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("mapped")]
    [InlineData("unmapped")]
    [InlineData("outage")]
    public async Task Live_route_is_read_only_and_preserves_validation_and_failure_semantics(string scenario)
    {
        var site = Guid.NewGuid();
        var source = new Source(site, scenario);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sandbox:AdminKey"] = "local-endpoint-test-key"
        });
        builder.Services.AddSingleton<IMmsInventorySource>(source);
        await using var app = builder.Build();
        app.UseMiddleware<AdminKeyMiddleware>();
        app.MapMmsInventoryEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var query = scenario switch
            {
                "missing" => "",
                "empty" => "?twin=" + Guid.Empty,
                "malformed" => "?twin=not-a-uuid",
                _ => "?twin=" + site
            };
            // No admin/function key in this browser-facing read.
            using var response = await http.GetAsync("/admin/mms/lightunits" + query);
            using var write = await http.PostAsync("/admin/mms/lightunits" + query, null);
            Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
            if (scenario is "missing" or "empty" or "malformed")
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal(0, source.Reads);
            }
            else if (scenario == "outage")
            {
                Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
                Assert.Contains("MMS is unavailable", await response.Content.ReadAsStringAsync());
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var inventory = await response.Content.ReadFromJsonAsync<MmsInventoryResponse>();
                Assert.Equal(site, inventory!.ITwinId);
                Assert.Equal(scenario == "mapped", inventory.SiteMapped);
                Assert.Equal(scenario == "mapped" ? 1 : 0, inventory.Count);
            }
        }
        finally { await app.StopAsync(); }
    }

    private sealed class Source(Guid site, string scenario) : IMmsInventorySource
    {
        public int Reads { get; private set; }
        public Task<MmsInventoryResponse> ListAsync(Guid twinId, CancellationToken ct)
        {
            Reads++;
            Assert.Equal(site, twinId);
            if (scenario == "outage")
                throw new ProviderUnavailableException("MMS", "lightunits", new HttpRequestException());
            return Task.FromResult(scenario == "unmapped" ? new MmsInventoryResponse(site, null, [])
                : new MmsInventoryResponse(site, 5,
                    [new(2, "XX01", null, null, null, 5, Guid.NewGuid(), null, null)]));
        }
    }
}
