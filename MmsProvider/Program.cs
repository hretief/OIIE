using System.Text.Json;
using System.Text.Json.Serialization;
using MmsProvider.Application;
using MmsProvider.Infrastructure.Sql;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

builder.Services.Configure<MmsOptions>(builder.Configuration.GetSection("Mms"));
builder.Services.AddSingleton<IMmsAssetStore, SqlMmsAssetStore>();
builder.Services.AddHostedService<SchemaInitializer>();

builder.Build().Run();
