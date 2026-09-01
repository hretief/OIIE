using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MmsEngine.Application;
using MmsEngine.Infrastructure.Cir;
using MmsEngine.Infrastructure.Mms;
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

builder.Services.Configure<MmsEngineOptions>(
    builder.Configuration.GetSection("MmsEngine"));

// --- Upstream: MMS ---------------------------------------------------------
//
// A typed client over HTTP rather than a project reference. MmsProvider
// emulates a customer system and must stay reachable only the way a real one
// would be.
builder.Services.AddHttpClient<IMmsClient, MmsRestClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<MmsEngineOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(options.MmsBaseUrl))
    {
        // A trailing slash matters: without it the last path segment is replaced
        // rather than appended when relative routes are resolved.
        http.BaseAddress = new Uri(options.MmsBaseUrl.TrimEnd('/') + "/");
    }

    if (!string.IsNullOrWhiteSpace(options.MmsApiKey))
        http.DefaultRequestHeaders.Add("x-functions-key", options.MmsApiKey);

    http.Timeout = TimeSpan.FromSeconds(60);
});

// --- Downstream: CIR -------------------------------------------------------

builder.Services.AddHttpClient<ICirClient, CirRestClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<MmsEngineOptions>>().Value;

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

// --- Engine ----------------------------------------------------------------

builder.Services.AddSingleton<IncomingSiteMapper>();

// Singleton because it holds the ISBM subscription session between polls. A
// scoped or transient ingestor would open a new session on every timer tick,
// and the broker's record of what this consumer has already read would be
// discarded along with it.
builder.Services.AddSingleton<SiteIngestionService>();

builder.Build().Run();
