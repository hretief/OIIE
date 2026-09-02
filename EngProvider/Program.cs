using System.Text.Json;
using System.Text.Json.Serialization;
using EngProvider.Application;
using EngProvider.Infrastructure.Notifications;
using EngProvider.Infrastructure.Sql;
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

// No ISBM or CIR client is registered. ENG emulates a customer engineering tool,
// and such a tool does not know it is being integrated with. The engine that
// carries a release onto a channel lives outside this project.
builder.Services.Configure<EngOptions>(builder.Configuration.GetSection("Eng"));
builder.Services.AddSingleton<IEngDesignStore, SqlEngDesignStore>();

// The one outward-facing seam: cutting a named version is announced to a URL ENG
// was told about. It is still not publishing -- it knows no topic and no BOD --
// but a real design tool does raise change notifications, and without one an
// integrator could only learn of a release by polling.
//
// Registered as a no-op unless a URL is configured, so the standalone case stays
// exactly as it was.
builder.Services.AddHttpClient<HttpNamedVersionNotifier>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<EngOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(options.NamedVersionNotificationKey))
        http.DefaultRequestHeaders.Add("x-functions-key", options.NamedVersionNotificationKey);

    http.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton<INamedVersionNotifier>(sp =>
    string.IsNullOrWhiteSpace(
        sp.GetRequiredService<IOptions<EngOptions>>().Value.NamedVersionNotificationUrl)
        ? new NullNamedVersionNotifier()
        : sp.GetRequiredService<HttpNamedVersionNotifier>());
builder.Services.AddHostedService<SchemaInitializer>();

builder.Build().Run();
