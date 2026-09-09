using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RdlProvider.Application;
using RdlProvider.Infrastructure.Sql;

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

// No ISBM or CIR client is registered, following RegLocationProvider. RDL
// emulates a reference data library, and such a system does not know it is
// being integrated with. What answers a class-library request on a channel is
// RdlEngine, not this project.
//
// No SchemaInitializer either. RDL shares the EIS database with REG-LOCATION
// and reads the same dbo.class_objects table; RegLocationProvider owns that
// DDL and applies it. Two apps creating the same tables would race on a cold
// start, and worse, would let the two definitions drift apart unnoticed.
builder.Services.Configure<RdlOptions>(builder.Configuration.GetSection("Rdl"));
builder.Services.AddSingleton<IRdlStore, SqlRdlStore>();

builder.Build().Run();
