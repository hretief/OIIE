using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Oiie.Isbm.Client;
using Oiie.Isbm.Client.Topology;
using RegLocationEngine.Application;
using Oiie.Cir.Client;
using RegLocationEngine.Infrastructure.RegLocation;
using RegLocationEngine.Infrastructure.State;

// FunctionsApplication.CreateBuilder is the 2.x entry point. It wraps
// HostApplicationBuilder, so services are reached directly rather than through
// a ConfigureServices callback. Requires Worker and Worker.Sdk on 2.x.
var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Web defaults give camelCase; enums must be opted in explicitly.
builder.Services.Configure<JsonSerializerOptions>(o =>
{
    o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.Configure<RegLocationEngineOptions>(
    builder.Configuration.GetSection("RegLocationEngine"));

// --- Upstream: REG-LOCATION ------------------------------------------------
//
// A typed client over HTTP rather than a project reference. RegLocationProvider
// emulates a customer system and must stay reachable only the way a real one
// would be.
builder.Services.AddHttpClient<IRegLocationClient, RegLocationRestClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<RegLocationEngineOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(options.RegLocationBaseUrl))
    {
        // A trailing slash matters: without it the last path segment is replaced
        // rather than appended when relative routes are resolved.
        http.BaseAddress = new Uri(options.RegLocationBaseUrl.TrimEnd('/') + "/");
    }

    if (!string.IsNullOrWhiteSpace(options.RegLocationApiKey))
        http.DefaultRequestHeaders.Add("x-functions-key", options.RegLocationApiKey);

    http.Timeout = TimeSpan.FromSeconds(60);
});

// --- Downstream: CIR -------------------------------------------------------

builder.Services.AddHttpClient<ICirClient, CirRestClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<RegLocationEngineOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(options.CirBaseUrl))
    {
        http.BaseAddress = new Uri(options.CirBaseUrl.TrimEnd('/') + "/");
    }

    if (!string.IsNullOrWhiteSpace(options.CirApiKey))
        http.DefaultRequestHeaders.Add("x-functions-key", options.CirApiKey);

    http.Timeout = TimeSpan.FromSeconds(60);
});

// --- Downstream: ISBM ------------------------------------------------------

builder.Services.Configure<IsbmClientOptions>(builder.Configuration.GetSection("Isbm"));

builder.Services.AddHttpClient<IIsbmClient, IsbmRestClient>((sp, http) =>
{
    var isbm = sp.GetRequiredService<IOptions<IsbmClientOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(isbm.BaseUrl))
    {
        http.BaseAddress = new Uri(isbm.BaseUrl.TrimEnd('/') + "/");
    }

    if (!string.IsNullOrWhiteSpace(isbm.ApiKey))
        http.DefaultRequestHeaders.Add("x-functions-key", isbm.ApiKey);

    http.Timeout = TimeSpan.FromSeconds(60);
});

// IsbmRestClient takes IsbmClientOptions directly rather than IOptions<>, so the
// typed-client registration above cannot construct it unaided.
builder.Services.AddTransient(sp =>
    sp.GetRequiredService<IOptions<IsbmClientOptions>>().Value);

// --- Topology --------------------------------------------------------------
//
// The subscriber end of the channel declarations. Read from the sandbox so this
// engine and the publisher cannot disagree about where a message goes; without
// a base address the client returns null and the configured channels stand.
builder.Services.AddHttpClient<TopologyClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<RegLocationEngineOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(options.SandboxBaseUrl))
    {
        http.BaseAddress = new Uri(options.SandboxBaseUrl.TrimEnd('/') + "/");
    }

    http.Timeout = TimeSpan.FromSeconds(15);
});

// --- Engine ----------------------------------------------------------------

builder.Services.AddSingleton(sp =>
{
    // AzureWebJobsStorage is already required by the Functions host, so the
    // engine does not introduce a new dependency by reusing it for its own small
    // state.
    var connection = builder.Configuration["AzureWebJobsStorage"]
        ?? throw new InvalidOperationException(
            "AzureWebJobsStorage is not configured; RegLocationEngine stores its published set there.");

    return new BlobServiceClient(connection);
});

builder.Services.AddSingleton<IRegLocationEngineStateStore, BlobRegLocationEngineStateStore>();
builder.Services.AddSingleton<RegLocationSegmentsBuilder>();
builder.Services.AddSingleton<RegLocationApprovalService>();

builder.Services.AddSingleton<IncomingSegmentMapper>();

// Singleton for its cache: an ingest drain handles many segments of few
// classes, and a per-request instance would re-ask CIR about the same class for
// every segment carrying it.
builder.Services.AddSingleton<CirClassRegistrar>();

// Singleton because it holds the ISBM subscription session between polls. A
// scoped or transient ingestor would open a new session on every timer tick,
// and the broker's record of what this consumer has already read would be
// discarded along with it.
builder.Services.AddSingleton<SegmentIngestionService>();

builder.Services.AddSingleton<IncomingSiteMapper>();

// Singleton for the same session-holding reason as the segment ingestor above.
builder.Services.AddSingleton<SiteIngestionService>();

builder.Build().Run();
