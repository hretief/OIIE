using System.Text.Json;
using System.Text.Json.Serialization;
using CirProvider.Application;
using CirProvider.Infrastructure.Isbm;
using CirProvider.Infrastructure.Sql;
using CirProvider.Middleware;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

// FunctionsApplication.CreateBuilder is the 2.x entry point. It wraps
// HostApplicationBuilder, so services are reached directly rather than through
// a ConfigureServices callback. Requires Worker and Worker.Sdk on 2.x.
var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.UseMiddleware<FaultMiddleware>();

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

builder.Services.Configure<CirOptions>(builder.Configuration.GetSection("Cir"));
builder.Services.AddSingleton<ICirStore, SqlCirStore>();
builder.Services.AddSingleton<IBodDispatcher, BodDispatcher>();

// --- ws-ISBM binding -------------------------------------------------------
builder.Services.Configure<IsbmOptions>(builder.Configuration.GetSection("Isbm"));
builder.Services.AddSingleton<IIsbmSessionStore, SqlIsbmSessionStore>();
builder.Services.AddSingleton<IsbmBodListener>();

builder.Services.AddHttpClient<IIsbmClient, IsbmRestClient>((sp, http) =>
{
    var isbm = sp.GetRequiredService<IOptions<IsbmOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(isbm.BaseUrl))
    {
        // A trailing slash matters: without it the last path segment is replaced
        // rather than appended when relative routes are resolved.
        http.BaseAddress = new Uri(isbm.BaseUrl.TrimEnd('/') + "/");
    }

    if (!string.IsNullOrWhiteSpace(isbm.ApiKey))
        http.DefaultRequestHeaders.Add("x-functions-key", isbm.ApiKey);

    http.Timeout = TimeSpan.FromSeconds(60);
});
builder.Services.AddHostedService<SchemaInitializer>();

builder.Build().Run();
