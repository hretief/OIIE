using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using EngEngine.Application;
using EngEngine.Infrastructure.Eng;
using EngEngine.Infrastructure.State;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oiie.Isbm.Client.Topology;
using Oiie.Isbm.Client;

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

builder.Services.Configure<EngEngineOptions>(builder.Configuration.GetSection("EngEngine"));

// --- Upstream: ENG ---------------------------------------------------------
//
// A typed client over HTTP rather than a project reference. EngProvider emulates
// a customer system and must stay reachable only the way a real one would be.
builder.Services.AddHttpClient<IEngClient, EngRestClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<EngEngineOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(options.EngBaseUrl))
    {
        // A trailing slash matters: without it the last path segment is replaced
        // rather than appended when relative routes are resolved.
        http.BaseAddress = new Uri(options.EngBaseUrl.TrimEnd('/') + "/");
    }

    if (!string.IsNullOrWhiteSpace(options.EngApiKey))
        http.DefaultRequestHeaders.Add("x-functions-key", options.EngApiKey);

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
// Where this engine publishes is a property of the system, not of this app, so
// it is read from the sandbox rather than only from local settings. Registered
// unconditionally: the client returns null without a base address, and every
// caller falls back to its configured channel, so an engine deployed without a
// sandbox URL behaves exactly as it did before.
builder.Services.AddHttpClient<TopologyClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<EngEngineOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(options.SandboxBaseUrl))
    {
        http.BaseAddress = new Uri(options.SandboxBaseUrl.TrimEnd('/') + "/");
    }

    http.Timeout = TimeSpan.FromSeconds(15);
});

// --- Engine ----------------------------------------------------------------

builder.Services.AddSingleton(sp =>
{
    // AzureWebJobsStorage is already required by the Functions host, so the engine
    // does not introduce a new dependency by reusing it for its own small state.
    var connection = builder.Configuration["AzureWebJobsStorage"]
        ?? throw new InvalidOperationException(
            "AzureWebJobsStorage is not configured; EngEngine stores its watermark there.");

    return new BlobServiceClient(connection);
});

builder.Services.AddSingleton<IEngEngineStateStore, BlobEngEngineStateStore>();
builder.Services.AddSingleton<EngSegmentsBuilder>();
builder.Services.AddSingleton<EngPublicationService>();
builder.Services.AddSingleton<EngSitesBuilder>();
builder.Services.AddSingleton<EngSitePublicationService>();

builder.Build().Run();
