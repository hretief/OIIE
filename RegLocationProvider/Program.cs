using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RegLocationProvider.Application;
using RegLocationProvider.Infrastructure.Notifications;
using RegLocationProvider.Infrastructure.Sql;

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

// No ISBM or CIR client is registered. REG-LOCATION emulates a customer
// functional location registry, and such a system does not know it is being
// integrated with. Whatever carries a registry change onto a channel lives
// outside this project.
builder.Services.Configure<RegLocationOptions>(builder.Configuration.GetSection("RegLocation"));
builder.Services.AddSingleton<IRegLocationStore, SqlRegLocationStore>();

// The one outward-facing seam: a stewardship decision is announced to a URL the
// registry was told about. It is still not publishing -- it knows no topic and
// no BOD -- but a real registry does raise change notifications, and without one
// an integrator could only learn of an approval by polling.
//
// Registered as a no-op unless a URL is configured, so the standalone case stays
// exactly as it was.
builder.Services.AddHttpClient<HttpApprovalNotifier>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<RegLocationOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(options.ApprovalNotificationKey))
        http.DefaultRequestHeaders.Add("x-functions-key", options.ApprovalNotificationKey);

    http.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton<IApprovalNotifier>(sp =>
    string.IsNullOrWhiteSpace(
        sp.GetRequiredService<IOptions<RegLocationOptions>>().Value.ApprovalNotificationUrl)
        ? new NullApprovalNotifier()
        : sp.GetRequiredService<HttpApprovalNotifier>());
builder.Services.AddHostedService<SchemaInitializer>();

builder.Build().Run();
