using Azure.Identity;
using SimHost.Application;
using SimHost.Application.Participants;
using SimHost.Components;
using SimHost.Infrastructure.Isbm;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---------------------------------------------------------
// DefaultAzureCredential picks up the developer's Visual Studio or Azure CLI
// sign-in, so Storage, Key Vault and App Insights work from an F5 session with
// no secrets on the workstation (spec §6.1).
var credential = new DefaultAzureCredential();

builder.Configuration.AddSandboxKeyVault(credential);

// --- Engine ----------------------------------------------------------------
// The operator UI composes the same engine the API does, and reads through it
// directly: these pages are diagnostic views over participant stores and run
// history, and routing every grid through HTTP would add a hop without adding a
// guarantee. The engine is shared as a library, so there is one definition of a
// participant and one of a scenario whichever host is looking at it.
//
// The message pumps are deliberately NOT registered here. They belong to the API
// host alone: two processes draining the same ISBM sessions would settle each
// other's messages and make delivery nondeterministic.
builder.Services.AddSandboxCore(builder.Configuration, builder.Environment, credential);

// --- Telemetry and UI ------------------------------------------------------
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Resolved so the UI can report session state. The API host owns the pumps that
// drain these sessions; this host only observes them.
if (SandboxCapabilities.IsIsbmConfigured(app.Services.GetRequiredService<ParticipantRegistry>()))
{
    var accessor = app.Services.GetRequiredService<IsbmClientAccessor>();
    accessor.Manager = app.Services.GetRequiredService<IsbmSessionManager>();
}

app.Run();
