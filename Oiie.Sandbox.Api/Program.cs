using Azure.Identity;
using Oiie.Sandbox.Api.Endpoints;
using Oiie.Sandbox.Api.Middleware;
using Oiie.Sandbox.Api.Providers;
using Oiie.Sandbox.Api.Services;
using SimHost.Application;
using SimHost.Application.Participants;
using SimHost.Infrastructure.Isbm;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---------------------------------------------------------
// DefaultAzureCredential picks up the developer's Visual Studio or Azure CLI
// sign-in, so Storage, Key Vault and App Insights work from an F5 session with
// no secrets on the workstation (spec §6.1).
var credential = new DefaultAzureCredential();

builder.Configuration.AddSandboxKeyVault(credential);

// --- Engine ----------------------------------------------------------------
builder.Services.AddSandboxCore(builder.Configuration, builder.Environment, credential);

// The sandbox no longer moves messages. EngEngine publishes from a watermark over
// EngProvider, and RegLocationEngine drains the subscription and files proposals, so
// a pump here would be a second mover competing for the same ISBM sessions rather
// than a participant doing its own integration (DR-022).
//
// The cost is that MMS outbound goes dormant: MmsWorkOrderService still enqueues to
// its outbox and nothing drains it. Accepted deliberately -- the ENG to REG-LOCATION
// handover this demonstrates never reaches MMS -- and it lifts when MMS gets a
// provider and engine of its own.

// --- ENG Functions apps ----------------------------------------------------
//
// The provider and engine that carry the SyncSites bootstrap. Reached over HTTP
// rather than by project reference: they are separate Functions hosts that
// emulate a customer system and its integration engine, and the sandbox must
// reach them the way anything else would.
//
// The key is set once here rather than per call. Both hosts are ours and share
// a key in the sandbox; a deployment that separates them would need a client
// each, which is a larger change than adding a second header.
builder.Services.AddHttpClient<EngEngineClient>((sp, http) =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var key = configuration["Sandbox:EngFunctionsKey"];

    if (!string.IsNullOrWhiteSpace(key))
    {
        http.DefaultRequestHeaders.Add("x-functions-key", key);
    }

    // Longer than a UI click would suggest, because the engine's leg of this
    // opens an ISBM session and publishes before it answers.
    http.Timeout = TimeSpan.FromSeconds(60);
});

// Channel provisioning is shared by the admin endpoint and the startup hook
// below, so it is registered rather than written inline in either.
builder.Services.AddScoped<IsbmChannelProvisioner>();

// --- Provider read-through -------------------------------------------------
//
// The ENG and REG-LOCATION panels read the deployed customer-system emulators.
// There is no sandbox-backed alternative any more: the sandbox does not hold
// participant state, so the provider apps are the only place the panels' data
// exists.
//
// A backend-for-frontend rather than letting the React app call them directly:
// the provider apps authenticate with a function key, and a key in a browser
// bundle is a key anyone with devtools can lift and replay against a customer
// system. Holding it here keeps the app same-origin and the key server-side.
//
// Missing configuration fails startup rather than degrading. A host that boots
// without providers can only serve panels that error on every read, and doing
// that quietly turns a configuration mistake into a support call about missing
// data.
var providerOptions = builder.Configuration
    .GetSection(ProviderOptions.SectionName).Get<ProviderOptions>() ?? new ProviderOptions();

var unconfigured = new List<string>();

if (!providerOptions.Eng.IsConfigured)
{
    unconfigured.Add($"{ProviderOptions.SectionName}:Eng");
}

if (!providerOptions.RegLocation.IsConfigured)
{
    unconfigured.Add($"{ProviderOptions.SectionName}:RegLocation");
}

if (unconfigured.Count > 0)
{
    throw new InvalidOperationException(
        "The sandbox reads all participant state from the provider apps, so they must "
        + "be configured before it can start. Missing BaseUrl or Key for: "
        + string.Join(", ", unconfigured) + ".");
}

builder.Services.AddHttpClient<EngProviderClient>((sp, http) =>
    ConfigureProvider(http, providerOptions.Eng));

builder.Services.AddHttpClient<RegLocationProviderClient>((sp, http) =>
    ConfigureProvider(http, providerOptions.RegLocation));

builder.Services.AddScoped<IEngSource, ProviderEngSource>();
builder.Services.AddScoped<IRegLocationSource, ProviderRegLocationSource>();

static void ConfigureProvider(HttpClient http, ProviderEndpointOptions options)
{
    // Trailing slash matters. Without it, a relative route replaces the last
    // path segment and /api is silently dropped from every call.
    var baseUrl = options.BaseUrl!.TrimEnd('/') + "/";

    http.BaseAddress = new Uri(baseUrl);
    http.DefaultRequestHeaders.Add("x-functions-key", options.Key);

    // Shorter than the engine client's 60 seconds: these are reads against a
    // database, not a leg that opens an ISBM session. Long enough to absorb a
    // cold start on a Basic plan, which is the realistic worst case.
    http.Timeout = TimeSpan.FromSeconds(30);
}

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

using (var scope = app.Services.CreateScope())
{
    // Channels are provisioned here rather than assumed to exist.
    //
    // Without this, a deployment whose channels were never created fails at the
    // first publish with a 404 naming the channel, and the outbox exhausts its
    // retries and stops — while the UI has already reported the release as
    // queued. Creating them is idempotent, so the cost of doing it on every
    // start is a few calls; the cost of not doing it is a silent dead end.
    if (SandboxCapabilities.IsIsbmConfigured(app.Services.GetRequiredService<ParticipantRegistry>()))
    {
        try
        {
            var ensured = await scope.ServiceProvider
                .GetRequiredService<IsbmChannelProvisioner>().EnsureAllAsync();

            var failed = ensured.Where(r => !r.Created).ToList();

            foreach (var failure in failed)
            {
                app.Logger.LogWarning(
                    "Could not ensure ISBM channel {ChannelUri} for {ParticipantId}: {Error}",
                    failure.ChannelUri, failure.ParticipantId, failure.Error);
            }

            app.Logger.LogInformation(
                "ISBM channels ensured: {Ok} ok, {Failed} failed.",
                ensured.Count - failed.Count, failed.Count);
        }
        catch (Exception ex)
        {
            // Non-fatal, like the blocks above: the sandbox is still usable for
            // anything that does not publish, and /admin/isbm/channels/ensure can
            // be run by hand once ISBM is reachable.
            app.Logger.LogWarning(ex,
                "Could not ensure ISBM channels at startup; run POST /admin/isbm/channels/ensure.");
        }
    }
}

app.Run();
