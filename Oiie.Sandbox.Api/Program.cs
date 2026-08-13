using Azure.Identity;
using Oiie.Sandbox.Api.Endpoints;
using Oiie.Sandbox.Api.Middleware;
using SimHost.Application;
using SimHost.Application.Classification;
using SimHost.Application.Participants;
using SimHost.Infrastructure.Isbm;
using SimHost.Infrastructure.Sql;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---------------------------------------------------------
// DefaultAzureCredential picks up the developer's Visual Studio or Azure CLI
// sign-in, so Storage, Key Vault and App Insights work from an F5 session with
// no secrets on the workstation (spec §6.1).
var credential = new DefaultAzureCredential();

builder.Configuration.AddSandboxKeyVault(credential);

// --- Engine ----------------------------------------------------------------
builder.Services.AddSandboxCore(builder.Configuration, builder.Environment, credential);

// Only this host runs the pumps. The Blazor UI composes the same engine so it can
// read and drive participants directly, but if it also drained the ISBM sessions the
// two processes would settle each other's messages and delivery would stop being
// deterministic.
builder.Services.AddSandboxMessagePumps();

// --- Telemetry -------------------------------------------------------------
builder.Services.AddApplicationInsightsTelemetry();

// The Blazor host redirected failures to an /Error razor page. An API has no such
// page, so unhandled failures are rendered as ProblemDetails instead. Without this
// registration the parameterless UseExceptionHandler below has nothing to write.
builder.Services.AddProblemDetails();

// --- CORS ------------------------------------------------------------------
// The Workflow Orchestration React app is served from its own origin (the Vite dev
// server locally, static hosting when deployed), so every call it makes is
// cross-origin and fails preflight without this.
//
// Origins come from configuration rather than being hardcoded because they differ
// per environment. In Development an empty list falls back to the Vite defaults,
// which keeps a fresh clone working with no configuration at all; in any other
// environment an empty list means no cross-origin caller is permitted, which is
// the correct default for an API whose /admin routes reset databases.
var corsOrigins = builder.Configuration
    .GetSection("Sandbox:AllowedCorsOrigins").Get<string[]>() ?? [];

if (corsOrigins.Length == 0 && builder.Environment.IsDevelopment())
{
    // The Vite dev server's port, from WorkflowOrchestration/vite.config.ts. Not
    // Vite's usual 5173: the Figma scaffold pins PORT ?? 8443 with strictPort.
    //
    // Mostly belt and braces, since vite.config.ts proxies /admin to the API and
    // the browser therefore sees same-origin during development. This matters when
    // the app is pointed straight at the API instead, via VITE_SANDBOX_API.
    corsOrigins = ["http://localhost:8443", "https://localhost:8443"];
}

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (corsOrigins.Length == 0)
    {
        return;
    }

    // Not AllowAnyOrigin: the admin key travels in a header, and a policy that
    // reflects any origin would let any page a browser visits drive this sandbox.
    policy.WithOrigins(corsOrigins)
          .AllowAnyHeader()
          .AllowAnyMethod();
}));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
}

app.UseHttpsRedirection();

// The Workflow Orchestration app, served from this host's wwwroot.
//
// Same origin as the API it drives, which is the point: no CORS, and no admin
// key in a browser bundle. A separately hosted app would need both, and that
// key resets databases and deletes channels -- not something to put somewhere
// devtools can read it.
//
// Absent in development, where Vite serves the app and proxies /admin here.
app.UseDefaultFiles();
app.UseStaticFiles();

// Ahead of the admin guard deliberately. A CORS preflight is an OPTIONS request
// that carries no headers of its own, so the admin key is absent -- if the guard
// ran first it would reject the preflight and the real request would never be
// sent, presenting as an opaque browser-side failure with nothing in the logs.
app.UseCors();

// Before anything else on /admin: these endpoints reset databases and delete
// channels, and a deployed instance is reachable by anyone who knows the URL.
app.UseMiddleware<AdminKeyMiddleware>();

app.MapSandboxAdminEndpoints();

// Anything not matched above is a client-side route, so the app's own shell
// answers it.
//
// Deliberately not a blanket MapFallbackToFile: that also caught /admin and
// /health, so a mistyped or removed endpoint answered 200 with HTML instead of
// 404. A caller then sees success and a JSON parse failure rather than "no such
// endpoint", and test-sandbox.ps1 in particular would report something far less
// useful than a missing route.
app.MapFallback(async context =>
{
    var path = context.Request.Path;

    if (path.StartsWithSegments("/admin") || path.StartsWithSegments("/health"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "No such endpoint.",
            path = path.Value
        });

        return;
    }

    // No wwwroot in development, where Vite serves the app instead. Answering
    // 404 is honest there; falling through to a missing file would surface as a
    // 500 that suggests the API is broken rather than simply not hosting a UI.
    var shell = Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html");

    if (!File.Exists(shell))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(shell);
});

if (SandboxCapabilities.IsIsbmConfigured(app.Services.GetRequiredService<ParticipantRegistry>()))
{
    var accessor = app.Services.GetRequiredService<IsbmClientAccessor>();
    accessor.Manager = app.Services.GetRequiredService<IsbmSessionManager>();
}

using (var scope = app.Services.CreateScope())
{
    // The orchestration tables are not created by any participant's reset, so without
    // this the first scenario run fails on an invalid object name rather than on
    // anything to do with the scenario.
    try
    {
        await scope.ServiceProvider
            .GetRequiredService<ISandboxSchemaInitializer>().EnsureTablesAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex,
            "Could not ensure the scenario orchestration tables; scenario runs will fail " +
            "until the sandbox schema is reachable.");
    }

    // Without this, a restart leaves every participant with an empty snapshot and
    // classification silently stops working until something reseeds.
    try
    {
        await scope.ServiceProvider.GetRequiredService<ClassificationRefresher>().RefreshAllAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex,
            "Could not load classification at startup; run POST /admin/schema/seed.");
    }
}

app.Run();
