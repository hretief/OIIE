using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RegLocationProvider.Application;
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
builder.Services.AddHostedService<SchemaInitializer>();

builder.Build().Run();
