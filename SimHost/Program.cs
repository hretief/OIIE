using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Oiie.Ccom;
using SimHost.Application.Bods;
using SimHost.Application.Cir;
using SimHost.Application.Classification;
using SimHost.Application.Inbox;
using SimHost.Application.Outbox;
using SimHost.Personalities.Eng;
using SimHost.Personalities.Mms;
using SimHost.Personalities.RegLocation;
using SimHost.Application.Participants;
using SimHost.Components;
using SimHost.Infrastructure.Blob;
using Oiie.Isbm.Client;
using SimHost.Domain.Common;
using SimHost.Infrastructure.Isbm;
using SimHost.Infrastructure.Sql;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---------------------------------------------------------
// DefaultAzureCredential picks up the developer's Visual Studio or Azure CLI
// sign-in, so Storage, Key Vault and App Insights work from an F5 session with
// no secrets on the workstation (spec §6.1).
var credential = new DefaultAzureCredential();

var keyVaultUri = builder.Configuration["KeyVault:Uri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), credential);
}

// --- Participants ----------------------------------------------------------
var personalitiesRoot = builder.Configuration["Sandbox:PersonalitiesPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "PersonalityPacks");

var personalities = PersonalityLoader.LoadAll(Path.GetFullPath(personalitiesRoot));
builder.Services.AddSingleton(new ParticipantRegistry(personalities));

// --- Infrastructure --------------------------------------------------------
builder.Services.AddSingleton<IParticipantConnectionStringProvider,
    KeyVaultConnectionStringProvider>();
builder.Services.AddSingleton<IParticipantDbContextFactory, ParticipantDbContextFactory>();
builder.Services.AddSingleton<IParticipantSchemaInitializer, ParticipantSchemaInitializer>();

// Storage and ISBM are optional at startup so the database work can proceed before
// either is wired. Each is registered only when configured, and the outbox — which
// needs both — stays dormant otherwise. Failing startup instead would block schema
// initialisation on dependencies it does not use.
var storageUri = builder.Configuration["Storage:BlobServiceUri"];
var storageConfigured = !string.IsNullOrWhiteSpace(storageUri)
                        && !storageUri.Contains("REPLACE", StringComparison.OrdinalIgnoreCase);

if (storageConfigured)
{
    builder.Services.AddSingleton(_ => new BlobServiceClient(new Uri(storageUri!), credential));
    builder.Services.AddSingleton<IPayloadStore, BlobPayloadStore>();
}
else
{
    // Registered so messaging still runs without a storage account. The archive
    // row is worth more than the payload body.
    builder.Services.AddSingleton<IPayloadStore, NullPayloadStore>();
}

// The client is now the real one extracted from the ws-CIR provider, so ISBM is
// wired whenever a participant declares a base URL.
var isbmConfigured = personalities.Any(p => !string.IsNullOrWhiteSpace(p.Isbm.BaseUrl));

if (isbmConfigured)
{
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<IsbmClientAccessor>();
    builder.Services.AddSingleton<IIsbmClientAccessor>(sp => sp.GetRequiredService<IsbmClientAccessor>());
    builder.Services.AddSingleton<IIsbmSessionStoreAccessor>(sp => sp.GetRequiredService<IsbmClientAccessor>());
}

// --- BOD -------------------------------------------------------------------
builder.Services.AddSingleton(_ =>
{
    var validator = new BodValidator();
    var schemaRoot = builder.Configuration["Sandbox:SchemasPath"]
        ?? Path.Combine(builder.Environment.ContentRootPath, "..", "schemas");
    validator.LoadDirectory(Path.GetFullPath(schemaRoot));
    return validator;
});

// --- Application services --------------------------------------------------
builder.Services.AddSingleton<DispatcherControl>();
builder.Services.AddSingleton<CcomAttributeMapperFactory>();
builder.Services.AddSingleton<CirTelemetry>();
builder.Services.AddSingleton<CirClient>();
builder.Services.AddSingleton<CirRegistrationService>();
builder.Services.AddSingleton<ClassFixtureLoader>();
builder.Services.AddSingleton<ClassificationRefresher>();

builder.Services.AddSingleton<IBodBuilder, SyncSegmentsBuilder>();
builder.Services.AddSingleton<EngService>();

builder.Services.AddSingleton<IBodBuilder, RegLocationSegmentsBuilder>();
builder.Services.AddSingleton<IBodHandler, SyncSegmentsHandler>();
builder.Services.AddSingleton<RegLocationService>();

builder.Services.AddSingleton<IBodHandler, MmsSegmentsHandler>();

if (isbmConfigured)
{
    builder.Services.AddSingleton<IsbmSessionManager>();
    builder.Services.AddSingleton<InboxTelemetry>();
    builder.Services.AddHostedService<InboxPump>();
}

if (isbmConfigured)
{
    builder.Services.AddHostedService<OutboxDispatcher>();
}

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

// Before anything else on /admin: these endpoints reset databases and delete
// channels, and a deployed instance is reachable by anyone who knows the URL.
app.UseMiddleware<SimHost.Middleware.AdminKeyMiddleware>();

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Creates each participant's tables from the EF model if they are absent. Safe to
// run on every start: it is a no-op once the sentinel table exists.
app.MapPost("/admin/schema/init", async (
    ParticipantRegistry registry,
    IParticipantSchemaInitializer initializer,
    CancellationToken ct) =>
{
    var results = new List<object>();

    foreach (var participant in registry.All)
    {
        var created = await initializer.EnsureTablesAsync(participant.ParticipantId, ct);
        var tables = await initializer.ListTablesAsync(participant.ParticipantId, ct);

        results.Add(new
        {
            participant.ParticipantId,
            participant.Schema,
            created,
            tableCount = tables.Count,
            tables
        });
    }

    return Results.Ok(results);
});

// Full reset: ISBM state first, then SQL. Ordering is the whole point.
//
// Closing sessions must happen BEFORE the IsbmSession table is dropped, or the ids
// are lost and the sessions leak on the provider — a simulator that leaks a session
// per reset degrades the very provider it exists to exercise.
//
// Channels are deleted and recreated rather than left alone, because a publication
// posted before the reset is still sitting on the channel and would be read by the
// next run as though it belonged to it. Stale publications leaking across runs is a
// confusing, intermittent failure mode.
app.MapPost("/admin/reset", async (
    ParticipantRegistry registry,
    IParticipantSchemaInitializer initializer,
    IIsbmClientAccessor clients,
    IIsbmSessionStoreAccessor stores,
    ClassFixtureLoader loader,
    ClassificationRefresher refresher,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var log = loggerFactory.CreateLogger("Reset");

    // Phased across all participants rather than looped per participant.
    //
    // Channels are shared: REG-LOCATION publishes to the same O&M channel MMS
    // subscribes to. A per-participant loop deletes that channel twice, and any
    // session opened between the two passes is silently destroyed by the second —
    // which surfaces later as "Session does not exist" on a post that should have
    // worked.
    var closed = 0;

    // 1. Close every session everywhere, while the ids are still readable.
    foreach (var participant in registry.All)
    {
        try
        {
            var client = clients.For(participant.ParticipantId);
            var store = stores.For(participant.ParticipantId);

            foreach (var (kind, _, sessionId, _) in await store.ListAsync(ct))
            {
                try
                {
                    await client.CloseSessionAsync(kind, sessionId, ct);
                    closed++;
                }
                catch (Exception ex)
                {
                    // A session the provider has already forgotten is the desired
                    // end state, so this must not abort the reset.
                    log.LogWarning("Could not close session {SessionId}: {Message}", sessionId, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not enumerate sessions for {ParticipantId}: {Message}",
                participant.ParticipantId, ex.Message);
        }
    }

    // 2. Purge each distinct channel the Sandbox owns, exactly once.
    //
    // Channels belonging to other systems are ensured but never deleted. The CIR
    // provider holds a long-lived provider-request session on its own channel;
    // deleting it would destroy that session and stop the provider consuming until
    // someone restarted it. Resetting our own state must not break somebody else's.
    var ownedChannels = registry.All
        .SelectMany(p => p.Config.Channels.Select(c => new
        {
            Uri = c.ChannelUri,
            IsRequest = c.Role is ChannelRole.RequestProvider or ChannelRole.RequestConsumer
        }))
        .GroupBy(c => c.Uri, StringComparer.Ordinal)
        .Select(g => new { Uri = g.Key, IsRequest = g.Any(c => c.IsRequest) })
        .ToList();

    var foreignChannels = registry.All
        .Select(p => p.Config.Cir.ChannelUri)
        .Where(uri => !string.IsNullOrWhiteSpace(uri))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    var anyClient = clients.For(registry.All.First().ParticipantId);
    var purged = new List<string>();
    var ensured = new List<string>();

    foreach (var channel in ownedChannels)
    {
        var type = channel.IsRequest ? IsbmChannelType.Request : IsbmChannelType.Publication;

        try
        {
            await anyClient.DeleteChannelAsync(channel.Uri, ct);
            await anyClient.CreateChannelAsync(channel.Uri, type, "OIIE Sandbox", null, ct);
            purged.Add(channel.Uri);
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not purge channel {ChannelUri}: {Message}", channel.Uri, ex.Message);
        }
    }

    foreach (var uri in foreignChannels)
    {
        try
        {
            // Create-if-absent only. Already-exists is the expected outcome.
            await anyClient.CreateChannelAsync(
                uri, IsbmChannelType.Request, "ws-CIR request channel", null, ct);
            ensured.Add(uri);
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not ensure channel {ChannelUri}: {Message}", uri, ex.Message);
        }
    }

    // 3. Tables, including the session table itself.
    var fixtureRoot = Path.GetFullPath(configuration["Sandbox:PersonalitiesPath"]
        ?? Path.Combine(environment.ContentRootPath, "PersonalityPacks"));

    var steps = new List<object>();

    foreach (var participant in registry.All)
    {
        await initializer.DropTablesAsync(participant.ParticipantId, ct);
        await initializer.EnsureTablesAsync(participant.ParticipantId, ct);

        // 4. Reference data lives in the tables just dropped. A reset that left
        // participants unable to classify anything would look like the model is
        // broken rather than empty.
        var fixtures = await loader.LoadAsync(participant, fixtureRoot, ct);

        steps.Add(new
        {
            participant.ParticipantId,
            schema = participant.Schema,
            classes = fixtures.Classes,
            propertyDefinitions = fixtures.Definitions
        });
    }

    await refresher.RefreshAllAsync(ct);

    log.LogInformation(
        "Reset complete: {Sessions} session(s) closed, {Channels} channel(s) purged, {Count} participant(s)",
        closed, purged.Count, registry.All.Count);

    return Results.Ok(new
    {
        sessionsClosed = closed,
        channelsPurged = purged,
        channelsEnsuredNotPurged = ensured,
        participants = steps
    });
});

// Day zero: everything the Sandbox can reach, torn down and rebuilt.
//
// Distinct from /admin/reset, which deliberately leaves other systems' channels
// alone. This one deletes them too, because the point is to clear queues and
// sessions that have outlived their usefulness — including a provider's own.
//
// That has a consequence the caller must act on: deleting a channel destroys every
// session on it, including sessions held by systems that are not this one. Those
// systems will keep polling dead session ids until they are told to re-open, and a
// poll loop that swallows session faults will look healthy while consuming nothing.
// The response lists what needs restarting.
app.MapPost("/admin/reset/day-zero", async (
    ParticipantRegistry registry,
    IParticipantSchemaInitializer initializer,
    IIsbmClientAccessor clients,
    IIsbmSessionStoreAccessor stores,
    ClassFixtureLoader loader,
    ClassificationRefresher refresher,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var log = loggerFactory.CreateLogger("DayZero");
    var client = clients.For(registry.All.First().ParticipantId);

    // 1. Close our own sessions while the ids are still readable.
    var closed = 0;

    foreach (var participant in registry.All)
    {
        try
        {
            var participantClient = clients.For(participant.ParticipantId);

            foreach (var (kind, _, sessionId, _) in await stores.For(participant.ParticipantId).ListAsync(ct))
            {
                try
                {
                    await participantClient.CloseSessionAsync(kind, sessionId, ct);
                    closed++;
                }
                catch
                {
                    // Already gone is the desired end state.
                }
            }
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not enumerate sessions for {ParticipantId}: {Message}",
                participant.ParticipantId, ex.Message);
        }
    }

    // 2. Delete and recreate every channel this deployment touches, ours and the
    // registry's. Deleting is what clears the queue: there is no drain-all operation.
    var owned = registry.All
        .SelectMany(p => p.Config.Channels.Select(c => new
        {
            Uri = c.ChannelUri,
            IsRequest = c.Role is ChannelRole.RequestProvider or ChannelRole.RequestConsumer,
            Ours = true
        }))
        .ToList();

    var foreign = registry.All
        .Where(p => !string.IsNullOrWhiteSpace(p.Config.Cir.ChannelUri))
        .Select(p => new { Uri = p.Config.Cir.ChannelUri, IsRequest = true, Ours = false })
        .ToList();

    var all = owned.Concat(foreign)
        .GroupBy(c => c.Uri, StringComparer.Ordinal)
        .Select(g => new
        {
            Uri = g.Key,
            IsRequest = g.Any(c => c.IsRequest),
            Ours = g.All(c => c.Ours)
        })
        .ToList();

    var rebuilt = new List<object>();

    foreach (var channel in all)
    {
        var type = channel.IsRequest ? IsbmChannelType.Request : IsbmChannelType.Publication;

        try
        {
            await client.DeleteChannelAsync(channel.Uri, ct);

            // Wait for the delete to become visible before recreating.
            //
            // Creating too soon returns "already exists", which this client treats as
            // success — so the rebuild silently becomes a no-op and reports ok. The
            // channel then survives with its queue and sessions intact, which is the
            // opposite of what day zero is for.
            var gone = false;

            for (var i = 0; i < 10 && !gone; i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300 * (i + 1)), ct);
                gone = await client.GetChannelAsync(channel.Uri, ct) is null;
            }

            if (!gone)
            {
                rebuilt.Add(new
                {
                    channel.Uri,
                    type = type.ToString(),
                    ours = channel.Ours,
                    ok = false,
                    error = "Still present after delete. Not recreated — the queue and any " +
                            "sessions on it were left as they were."
                });
                continue;
            }

            await client.CreateChannelAsync(channel.Uri, type, "OIIE Sandbox day zero", null, ct);
            rebuilt.Add(new { channel.Uri, type = type.ToString(), ours = channel.Ours, ok = true });
        }
        catch (Exception ex)
        {
            rebuilt.Add(new { channel.Uri, type = type.ToString(), ours = channel.Ours, ok = false, error = ex.Message });
        }
    }

    // 3. Tables and reference data.
    var fixtureRoot = Path.GetFullPath(configuration["Sandbox:PersonalitiesPath"]
        ?? Path.Combine(environment.ContentRootPath, "PersonalityPacks"));

    var participants = new List<object>();

    foreach (var participant in registry.All)
    {
        await initializer.DropTablesAsync(participant.ParticipantId, ct);
        await initializer.EnsureTablesAsync(participant.ParticipantId, ct);

        var fixtures = await loader.LoadAsync(participant, fixtureRoot, ct);

        participants.Add(new
        {
            participant.ParticipantId,
            schema = participant.Schema,
            classes = fixtures.Classes,
            propertyDefinitions = fixtures.Definitions
        });
    }

    await refresher.RefreshAllAsync(ct);

    var foreignRebuilt = rebuilt
        .Where(r => r.GetType().GetProperty("ours")?.GetValue(r) is false)
        .ToList();

    log.LogInformation("Day zero complete: {Channels} channel(s) rebuilt", rebuilt.Count);

    return Results.Ok(new
    {
        sessionsClosed = closed,
        channels = rebuilt,
        participants,
        actionRequired = foreignRebuilt.Count == 0
            ? []
            : new[]
            {
                "The CIR provider's sessions were destroyed with its channel. Call " +
                "POST {cirBaseUrl}/api/isbm/reset to make it re-open, or it will keep polling " +
                "a session the broker no longer knows about.",
                "The CIR registry's own data is NOT cleared by this call — entries registered " +
                "earlier still carry their CIRIDs. Clear it separately for a true day zero."
            }
    });
});

// Schema-only reset. Leaves ISBM state alone, so use /admin/reset unless you know
// the channels are already clean.
app.MapPost("/admin/schema/reset", async (
    ParticipantRegistry registry,
    IParticipantSchemaInitializer initializer,
    CancellationToken ct) =>
{
    foreach (var participant in registry.All)
    {
        await initializer.DropTablesAsync(participant.ParticipantId, ct);
        await initializer.EnsureTablesAsync(participant.ParticipantId, ct);
    }

    return Results.Ok(new { reset = registry.All.Count });
});

// Confirms each participant can actually connect as its own contained user, which
// is the first real test of the grants provisioned by deploy/provision.ps1.
app.MapGet("/health/sql", async (
    ParticipantRegistry registry,
    IParticipantDbContextFactory factory,
    CancellationToken ct) =>
{
    var results = new List<object>();

    foreach (var participant in registry.All)
    {
        try
        {
            await using var db = factory.Create(participant.ParticipantId);
            var identity = await db.Database
                .SqlQueryRaw<string>("SELECT CONCAT(USER_NAME(), '|', SCHEMA_NAME()) AS Value")
                .SingleAsync(ct);

            var parts = identity.Split('|');
            results.Add(new
            {
                participant.ParticipantId,
                connected = true,
                user = parts[0],
                defaultSchema = parts.Length > 1 ? parts[1] : null
            });
        }
        catch (Exception ex)
        {
            results.Add(new { participant.ParticipantId, connected = false, error = ex.Message });
        }
    }

    return Results.Ok(results);
});

// Creates every channel each participant is bound to. Idempotent, and run before
// the first publish rather than assumed: the Sandbox resets constantly, and a
// simulator that needs manual channel setup between runs is not resettable.
app.MapPost("/admin/isbm/channels/ensure", async (
    ParticipantRegistry registry,
    IIsbmClientAccessor clients,
    CancellationToken ct) =>
{
    var results = new List<object>();

    foreach (var participant in registry.All)
    {
        var client = clients.For(participant.ParticipantId);

        // Several participants may bind the same channel in different roles, and a
        // channel is created once regardless of how many bind it.
        var channels = participant.Config.Channels
            .GroupBy(c => c.ChannelUri, StringComparer.Ordinal)
            .ToList();

        // The CIR channel is not a peer binding, so it is not in Channels — but it
        // still has to exist before anything can register.
        var cirChannel = participant.Config.Cir.ChannelUri;

        foreach (var group in channels)
        {
            var isRequestChannel = group.Any(c =>
                c.Role is ChannelRole.RequestProvider or ChannelRole.RequestConsumer);

            var type = isRequestChannel ? IsbmChannelType.Request : IsbmChannelType.Publication;

            try
            {
                await client.CreateChannelAsync(
                    group.Key, type, $"OIIE Sandbox: {participant.ParticipantId}", null, ct);

                results.Add(new
                {
                    participant.ParticipantId,
                    channelUri = group.Key,
                    channelType = type.ToString(),
                    created = true
                });
            }
            catch (Exception ex)
            {
                results.Add(new
                {
                    participant.ParticipantId,
                    channelUri = group.Key,
                    channelType = type.ToString(),
                    created = false,
                    error = ex.Message
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(cirChannel)
            && !channels.Any(g => string.Equals(g.Key, cirChannel, StringComparison.Ordinal)))
        {
            try
            {
                await client.CreateChannelAsync(
                    cirChannel, IsbmChannelType.Request, "OIIE Sandbox: ws-CIR", null, ct);

                results.Add(new
                {
                    participant.ParticipantId,
                    channelUri = cirChannel,
                    channelType = nameof(IsbmChannelType.Request),
                    created = true
                });
            }
            catch (Exception ex)
            {
                results.Add(new
                {
                    participant.ParticipantId,
                    channelUri = cirChannel,
                    channelType = nameof(IsbmChannelType.Request),
                    created = false,
                    error = ex.Message
                });
            }
        }
    }

    return Results.Ok(results);
});

app.MapGet("/admin/isbm/channels", async (
    ParticipantRegistry registry, IIsbmClientAccessor clients, CancellationToken ct) =>
{
    var first = registry.All.FirstOrDefault()
        ?? throw new InvalidOperationException("No participants configured.");

    var channels = await clients.For(first.ParticipantId).GetChannelsAsync(ct);
    return Results.Ok(channels);
});

// Requeues failed outbox items. After fixing whatever the provider objected to,
// this avoids having to recreate the domain change to get another attempt.
app.MapPost("/admin/{participantId}/outbox/retry", async (
    string participantId,
    IParticipantDbContextFactory factory,
    CancellationToken ct) =>
{
    await using var db = factory.Create(participantId);

    var failed = await db.Outbox.Where(o => o.State == OutboxState.Failed).ToListAsync(ct);

    foreach (var item in failed)
    {
        item.State = OutboxState.Pending;
        item.Attempts = 0;
        item.LastError = null;
    }

    await db.SaveChangesAsync(ct);
    return Results.Ok(new { requeued = failed.Count });
});

// What the far end of the chain actually knows. Cirid is null on every row until a
// registry resolves the foreign identifiers, and that gap is the point.
// Registers a participant's own entries. Kept explicit rather than folded into
// each release event, so a registration round trip can be exercised on its own
// before three participants depend on it.
app.MapPost("/admin/{participantId}/cir/register", async (
    string participantId, CirRegistrationService service, CancellationToken ct) =>
{
    var result = await service.SyncAsync(participantId, ct);
    return result.Faults.Count > 0 ? Results.UnprocessableEntity(result) : Results.Ok(result);
});

// Publish-subscribe loopback: this app subscribes and publishes on the same
// channel, seconds apart, in one process.
//
// Isolates the Sandbox from the provider for pub/sub the way /admin/cir/loopback
// does for request/response. If a message posted after a confirmed-open
// subscription cannot be read back, nothing about participant configuration,
// timing or session lifecycle explains it.
app.MapPost("/admin/isbm/loopback", async (
    string? channel,
    string? topic,
    ParticipantRegistry registry,
    IIsbmClientAccessor clients,
    CancellationToken ct) =>
{
    var participant = registry.All.First();
    var client = clients.For(participant.ParticipantId);

    var channelUri = channel ?? participant.Config.Channels
        .FirstOrDefault(c => c.Role == ChannelRole.Publisher)?.ChannelUri
        ?? "/OIIE-SANDBOX/Enterprise/Site/Eng";

    var topicName = topic ?? "Segments";

    var steps = new List<object>();
    string? subscription = null;
    string? publication = null;

    void Step(string name, bool ok, string? detail = null) =>
        steps.Add(new { step = name, ok, detail });

    try
    {
        // Subscription first, and deliberately so: a subscription receives only what
        // is published after it opens, so opening second would prove nothing.
        subscription = await client.OpenSubscriptionSessionAsync(channelUri, [topicName], ct);
        Step("open subscription", true, subscription);

        publication = await client.OpenPublicationSessionAsync(channelUri, ct);
        Step("open publication session", true, publication);

        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        var probeId = Guid.NewGuid().ToString();
        var content = new System.Xml.Linq.XElement("LoopbackProbe",
            new System.Xml.Linq.XAttribute("id", probeId));

        var messageId = await client.PostPublicationAsync(
            publication, content, [topicName], null, ct);
        Step("post publication", true, messageId);

        Oiie.Isbm.Client.IsbmMessage? received = null;
        var drained = 0;

        for (var i = 0; i < 20 && received is null; i++)
        {
            var message = await client.ReadPublicationAsync(subscription, ct);

            if (message is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                continue;
            }

            if (message.MessageId == messageId)
            {
                received = message;
                break;
            }

            drained++;
            await client.RemovePublicationAsync(subscription, ct);
        }

        if (drained > 0)
        {
            Step("drain earlier publications", true, $"{drained} from earlier runs");
        }

        if (received is null)
        {
            Step("read publication", false,
                $"posted {messageId} on {channelUri} topic '{topicName}' after the subscription " +
                "was open, and read nothing back within 10s");

            return Results.UnprocessableEntity(new
            {
                channelUri,
                topic = topicName,
                steps,
                interpretation =
                    "A publication posted after a confirmed-open subscription on the same channel " +
                    "and topic was not delivered. This is provider-side: no participant " +
                    "configuration or session timing is involved."
            });
        }

        Step("read publication", true, received.Content?.Attribute("id")?.Value);
        await client.RemovePublicationAsync(subscription, ct);
        Step("remove publication", true, null);

        return Results.Ok(new
        {
            channelUri,
            topic = topicName,
            steps,
            interpretation = "Publish-subscribe delivery works on this channel and topic."
        });
    }
    catch (Exception ex)
    {
        Step("failed", false, ex.Message);
        return Results.UnprocessableEntity(new { channelUri, topic = topicName, steps });
    }
    finally
    {
        foreach (var (kind, session) in new[]
                 {
                     (IsbmSessionKind.Subscription, subscription),
                     (IsbmSessionKind.Publication, publication)
                 })
        {
            if (session is null) continue;
            try { await client.CloseSessionAsync(kind, session, ct); } catch { }
        }
    }
});

// Request/response loopback on the CIR channel, with this app playing both roles.
//
// The point is to isolate our client from the CIR provider. If this passes, the
// consumer-request routes are right and the problem is that the provider is not
// listening on this channel or these topics. If it fails, the routes are wrong and
// the provider was never the issue.
//
// Order matters: the provider session must be open BEFORE the request is posted. An
// ISBM request queue, like a subscription, only delivers what arrives after the
// session exists — which is also why the earlier diagnostic returning nothing
// proved nothing.
app.MapPost("/admin/cir/loopback", async (
    ParticipantRegistry registry,
    IIsbmClientAccessor clients,
    CancellationToken ct) =>
{
    var participant = registry.All.First();
    var channelUri = participant.Config.Cir.ChannelUri;
    var client = clients.For(participant.ParticipantId);

    const string topic = "GetRegistry";
    var steps = new List<object>();

    string? providerSession = null;
    string? consumerSession = null;

    void Step(string name, bool ok, string? detail = null) =>
        steps.Add(new { step = name, ok, detail });

    try
    {
        // No channel purge. Delete-then-create races against the provider — the
        // create can still see the old channel — and it is unnecessary here: a
        // provider read returns the OLDEST queued request, so draining until our own
        // message appears deals with earlier runs without touching the channel.
        var rest = client as Oiie.Isbm.Client.IsbmRestClient;

        providerSession = await client.OpenProviderRequestSessionAsync(channelUri, [topic], ct);
        Step("open provider-request session", true,
            $"{providerSession} | provider said: {rest?.LastSessionOpenResponse}");

        consumerSession = await client.OpenConsumerRequestSessionAsync(channelUri, ct);
        Step("open consumer-request session", true,
            $"{consumerSession} | provider said: {rest?.LastSessionOpenResponse}");

        // Short settle, then retry on failure. Waiting longer made this fail more
        // often, not less, so the session is short-lived rather than slow to appear.
        await Task.Delay(TimeSpan.FromMilliseconds(750), ct);

        var request = new System.Xml.Linq.XElement(
            System.Xml.Linq.XName.Get("GetRegistry", Oiie.Ccom.Namespaces.Cir),
            new System.Xml.Linq.XElement(
                System.Xml.Linq.XName.Get("Probe", Oiie.Ccom.Namespaces.Cir), "loopback"));

        string requestMessageId;

        try
        {
            requestMessageId = await client.PostRequestAsync(consumerSession, request, [topic], null, ct);
        }
        catch (Oiie.Isbm.Client.IsbmException ex) when (ex.IsSessionProblem)
        {
            // Re-open and post immediately: the window between opening and using a
            // session is where these fail, so the shorter it is the better.
            Step("post request", false, $"first attempt: {ex.Message}");

            consumerSession = await client.OpenConsumerRequestSessionAsync(channelUri, ct);
            requestMessageId = await client.PostRequestAsync(consumerSession, request, [topic], null, ct);
        }

        Step("post request", true, requestMessageId);

        // Read until our own message appears. Anything older is drained rather than
        // answered: responding to someone else's request is worse than discarding it,
        // and on a purged channel there should be nothing else anyway.
        Oiie.Isbm.Client.IsbmMessage? received = null;
        var drained = 0;

        for (var i = 0; i < 20 && received is null; i++)
        {
            var message = await client.ReadRequestAsync(providerSession, ct);

            if (message is null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                continue;
            }

            if (message.MessageId == requestMessageId)
            {
                received = message;
                break;
            }

            drained++;
            await client.RemoveRequestAsync(providerSession, ct);
        }

        if (drained > 0)
        {
            Step("drain stale requests", true, $"{drained} left over from earlier runs");
        }

        if (received is null)
        {
            Step("read request as provider", false,
                "Nothing arrived. The request was accepted but not delivered — most often a " +
                "topic the provider session is not subscribed to.");
            return Results.UnprocessableEntity(new { channelUri, topic, steps });
        }

        // The id the provider reads is not the id the consumer got back from
        // PostRequest. Which of the two keys the response is anyone's guess from the
        // specification, so try both rather than assume.
        Step("read request as provider", true,
            $"matched {received.MessageId} — the id PostRequest returned, so the consumer's " +
            "message id is the correlation key on both sides");

        var response = new System.Xml.Linq.XElement(
            System.Xml.Linq.XName.Get("GetRegistryResponse", Oiie.Ccom.Namespaces.Cir));

        await client.PostResponseAsync(providerSession, received.MessageId, response, ct);
        Step("post response keyed on the provider's request id", true, received.MessageId);

        await client.RemoveRequestAsync(providerSession, ct);
        Step("remove request", true, null);

        async Task<Oiie.Isbm.Client.IsbmMessage?> TryReadAsync(string key)
        {
            for (var i = 0; i < 6; i++)
            {
                try
                {
                    var message = await client.ReadResponseAsync(consumerSession, key, ct);
                    if (message is not null) return message;
                }
                catch (Oiie.Isbm.Client.IsbmException)
                {
                    // A key the provider does not recognise is a valid answer here,
                    // not an error worth aborting the probe for.
                    return null;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
            }

            return null;
        }

        var byConsumerId = await TryReadAsync(requestMessageId);
        Step("read response keyed on the consumer's request id", byConsumerId is not null,
            byConsumerId?.Content?.Name.LocalName ?? "not readable with this key");

        var byProviderId = byConsumerId is null ? await TryReadAsync(received.MessageId) : null;

        if (byConsumerId is null)
        {
            Step("read response keyed on the provider's request id", byProviderId is not null,
                byProviderId?.Content?.Name.LocalName ?? "not readable with this key");
        }

        var answer = byConsumerId ?? byProviderId;

        if (answer is null)
        {
            Step("read response as consumer", false,
                "Neither key reads the response. Either the read route is wrong, or a response " +
                "is not readable on a different session from the one that posted it.");
            return Results.UnprocessableEntity(new { channelUri, topic, steps });
        }

        var workingKey = byConsumerId is not null ? requestMessageId : received.MessageId;
        await client.RemoveResponseAsync(consumerSession, workingKey, ct);
        Step("remove response", true,
            byConsumerId is not null
                ? "Correlate on the id PostRequest returned."
                : "Correlate on the id the provider read. The consumer cannot know it, so " +
                  "something else must carry it.");

        return Results.Ok(new
        {
            channelUri,
            topic,
            steps,
            interpretation =
                "The consumer-request path works. If CIR calls still time out, the CIR provider " +
                "is not listening on this channel or not subscribed to these topics."
        });
    }
    catch (Exception ex)
    {
        Step("failed", false, ex.Message);
        return Results.UnprocessableEntity(new { channelUri, topic, steps });
    }
    finally
    {
        foreach (var (kind, session) in new[]
                 {
                     (IsbmSessionKind.ProviderRequest, providerSession),
                     (IsbmSessionKind.ConsumerRequest, consumerSession)
                 })
        {
            if (session is null) continue;
            try { await client.CloseSessionAsync(kind, session, ct); } catch { }
        }
    }
});

// The exact BOD last sent to the registry, and whatever came back.
//
// A provider that consumes a request and discards it leaves nothing to work from:
// no response, no fault, and a queue that is simply emptier than before. The
// literal document is the only thing that lets the other side reproduce it.
app.MapGet("/admin/cir/last", async (
    CirTelemetry telemetry,
    ParticipantRegistry registry,
    string? participantId,
    CancellationToken ct) =>
{
    // Read from each participant's own schema rather than from memory: on App
    // Service the instance that made the request need not be the one answering
    // this call, and an empty answer here reads like "nothing was ever sent".
    var ids = participantId is { Length: > 0 }
        ? new[] { participantId }
        : registry.All.Select(p => p.ParticipantId).ToArray();

    var exchanges = new List<object>();

    foreach (var id in ids)
    {
        foreach (var e in await telemetry.RecentAsync(id, 5, ct))
        {
            exchanges.Add(new
            {
                e.ParticipantId,
                e.Bod,
                e.CorrelationId,
                e.ChannelUri,
                e.Topic,
                e.RequestMessageId,
                e.ConsumerSessionId,
                e.WaitedSeconds,
                e.Outcome,
                e.ResponseVerb,
                faults = string.IsNullOrWhiteSpace(e.FaultsJson)
                    ? Array.Empty<string>()
                    : System.Text.Json.JsonSerializer.Deserialize<string[]>(e.FaultsJson) ?? [],
                e.SentUtc,
                e.AnsweredUtc,
                e.RequestXml,
                e.ResponseXml
            });
        }
    }

    return Results.Ok(exchanges);
});

// Resumes waiting for a response to a request that already timed out.
//
// The registration client gives up after its configured timeout and stops reading.
// If the provider consumes the request late — because its listener is a timer that
// was not firing, and something else woke it — the response is written to a session
// nobody is listening on any more, and the run reports "no response" for a request
// that was in fact answered.
//
// That distinction is the whole diagnosis: a late answer means the only fault is
// listener scheduling, while continued silence means there is a second fault in
// producing the acknowledgement. The ids needed to tell them apart are already on
// the persisted exchange, so this costs nothing to ask.
app.MapGet("/admin/cir/await-response", async (
    CirTelemetry telemetry,
    IIsbmClientAccessor clients,
    string participantId,
    int? seconds,
    CancellationToken ct) =>
{
    var exchange = (await telemetry.RecentAsync(participantId, 1, ct)).FirstOrDefault();

    if (exchange is null)
    {
        return Results.NotFound(new
        {
            participantId,
            detail = "No CIR exchange has been recorded for this participant."
        });
    }

    if (exchange.RequestMessageId is not { Length: > 0 } requestMessageId ||
        exchange.ConsumerSessionId is not { Length: > 0 } sessionId)
    {
        return Results.UnprocessableEntity(new
        {
            exchange.CorrelationId,
            detail = "The exchange has no request message id or consumer session id, " +
                     "so the request never reached the post."
        });
    }

    var client = clients.For(participantId);
    var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds ?? 30);
    var waited = 0;
    string? error = null;

    while (DateTimeOffset.UtcNow < deadline)
    {
        try
        {
            var message = await client.ReadResponseAsync(sessionId, requestMessageId, ct);

            if (message?.Content is not null)
            {
                var response = Oiie.Ccom.Cir.CirResponse.Parse(
                    new System.Xml.Linq.XDocument(message.Content));

                exchange.ResponseXml = response.RawXml;
                exchange.ResponseVerb = response.Verb;
                exchange.Outcome = response.HasFaults ? "Faulted" : "AnsweredLate";
                exchange.AnsweredUtc = DateTimeOffset.UtcNow;

                await client.RemoveResponseAsync(sessionId, requestMessageId, ct);

                return Results.Ok(new
                {
                    answered = true,
                    exchange.CorrelationId,
                    requestMessageId,
                    consumerSessionId = sessionId,
                    verb = response.Verb,
                    faults = response.Faults.Select(f => $"{f.Kind}: {f.Detail}").ToArray(),
                    waitedSeconds = waited,
                    responseXml = response.RawXml,
                    interpretation =
                        "The provider did answer, after the client had stopped listening. The " +
                        "acknowledgement is produced correctly; what failed is only when the " +
                        "request was picked up."
                });
            }
        }
        catch (IsbmException ex)
        {
            // A dead session reads the same as an empty queue unless the fault is
            // surfaced, and those have opposite meanings here.
            error = ex.Message;
            break;
        }

        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        waited++;
    }

    return Results.Ok(new
    {
        answered = false,
        exchange.CorrelationId,
        requestMessageId,
        consumerSessionId = sessionId,
        waitedSeconds = waited,
        error,
        interpretation = error is not null
            ? "The response session could not be read. If it reports a Session fault the " +
              "session is gone, and the response — if one was written — is unreachable."
            : "Still nothing on the session the request was posted on. The provider consumed " +
              "the request and has not written an acknowledgement, which is a fault in " +
              "addition to the listener not firing on its own."
    });
});

// Distinguishes "nobody consumed the request" from "consumed but no response".
//
// Opens a provider-request session on the CIR channel and reads what is queued. If
// requests are sitting there, the CIR provider is not listening on this channel or
// not subscribed to these topics — the channel URI is Sandbox configuration and has
// to match what the provider was deployed with. If the queue is empty, something
// consumed them and the problem is on the response side.
//
// Consuming here is destructive by nature, so it does not remove what it reads.
app.MapGet("/admin/cir/diagnose", async (
    ParticipantRegistry registry,
    IIsbmClientAccessor clients,
    CancellationToken ct) =>
{
    var participant = registry.All.First();
    var channelUri = participant.Config.Cir.ChannelUri;
    var client = clients.For(participant.ParticipantId);

    // The configured topic, not a guess: subscribing to the wrong one here would
    // report an empty queue and wrongly exonerate the channel.
    var topics = new[] { participant.Config.Cir.RequestTopic };

    string? sessionId = null;
    var pending = new List<object>();
    string? error = null;

    try
    {
        sessionId = await client.OpenProviderRequestSessionAsync(channelUri, topics, ct);

        // A short window: this competes with the real provider for the queue, so it
        // must not sit here draining messages the CIR provider should receive.
        for (var i = 0; i < 5; i++)
        {
            var message = await client.ReadRequestAsync(sessionId, ct);
            if (message is null) break;

            pending.Add(new
            {
                message.MessageId,
                topics = message.Topics,
                root = message.Content?.Name.LocalName,
                preview = message.RawContent.Length > 300
                    ? message.RawContent[..300] + "…"
                    : message.RawContent
            });
        }
    }
    catch (Exception ex)
    {
        error = ex.Message;
    }
    finally
    {
        if (sessionId is not null)
        {
            try
            {
                await client.CloseSessionAsync(IsbmSessionKind.ProviderRequest, sessionId, ct);
            }
            catch
            {
                // Best effort: a leaked diagnostic session is not worth failing on.
            }
        }
    }

    return Results.Ok(new
    {
        channelUri,
        subscribedTopics = topics,
        pendingRequests = pending.Count,
        pending,
        error,
        warning = "This opens a competing provider-request session on the CIR provider's own " +
                  "channel. ISBM hands a queued request to one provider session, so calling " +
                  "this BEFORE a drain can check the message out to the Sandbox and leave the " +
                  "drain nothing to find — which reads as the provider discarding it. Probe " +
                  "after the drain, never before, and do not leave it running.",
        interpretation = pending.Count > 0
            ? "Requests are queued and unconsumed. The provider is not consuming this channel " +
              "and topic, or has not woken — its listener is a timer trigger, so a cold app " +
              "must be started by the scale controller first."
            : "Nothing queued. Either the provider consumed the request and did not respond, " +
              "or nothing was posted. Check the CIR provider's logs for the BODID."
    });
});

// Asks the registry what a foreign identifier is, and what else it is called.
app.MapGet("/admin/{participantId}/cir/resolve", async (
    string participantId,
    string sourceId,
    string idInSource,
    ParticipantRegistry registry,
    CirClient cir,
    CancellationToken ct) =>
{
    var participant = registry.Get(participantId);
    var result = await cir.ResolveAsync(participant, sourceId, idInSource, ct);

    return Results.Ok(new
    {
        result.Cirid,
        result.FromCache,
        result.Detail,
        equivalents = result.Equivalents.Select(e => new
        {
            e.SourceID,
            e.IDInSource,
            e.Name,
            e.CIRID
        })
    });
});

app.MapGet("/admin/mms/locations", async (
    IParticipantDbContextFactory factory, CancellationToken ct) =>
{
    await using var db = factory.Create(MmsService.ParticipantId);

    var records = await db.Set<SimHost.Domain.Mms.FunctionalLocationRecord>()
        .OrderBy(r => r.EquipmentNumber)
        .Select(r => new
        {
            r.EquipmentNumber,
            r.Designation,
            r.PlannerGroup,
            foreignIdentifier = r.ForeignSourceId + ":" + r.ForeignIdInSource,
            r.Cirid,
            resolved = r.Cirid != null
        })
        .ToListAsync(ct);

    return Results.Ok(records);
});

app.MapGet("/admin/reg-location/stewardship", async (
    RegLocationService service, CancellationToken ct) =>
{
    var queue = await service.GetQueueAsync(ct);

    return Results.Ok(queue.Select(s => new
    {
        s.Id,
        s.SourceParticipant,
        s.SourceIdentifier,
        s.ProposedName,
        s.RequestedClassKey,
        s.BoundClassKey,
        s.ClassDegraded,
        s.PropertiesMapped,
        s.PropertiesUnmapped,
        state = s.State.ToString(),
        s.CreatedAt
    }));
});

// REG-LOCATION's release event: approval admits proposals to the authoritative
// model, assigns registry identifiers, and republishes.
app.MapPost("/admin/reg-location/approve", async (
    RegLocationService service, ParticipantRegistry registry, CancellationToken ct) =>
{
    var publisher = registry.Get(RegLocationService.ParticipantId).Config.Channels
        .FirstOrDefault(c => c.Role == ChannelRole.Publisher)
        ?? throw new InvalidOperationException("REG-LOCATION has no publisher channel configured.");

    var result = await service.ApproveAllAsync(
        publisher.ChannelUri, publisher.Topics.FirstOrDefault(), "steward", ct);

    return Results.Ok(result);
});

app.MapPost("/admin/reg-location/reject", async (
    RegLocationService service, RejectRequest request, CancellationToken ct) =>
    Results.Ok(await service.RejectAllAsync(request.Reason, "steward", ct)));

app.MapGet("/admin/reg-location/locations", async (
    IParticipantDbContextFactory factory, CancellationToken ct) =>
{
    await using var db = factory.Create(RegLocationService.ParticipantId);

    var locations = await db.Set<SimHost.Domain.RegLocation.Location>()
        .OrderBy(l => l.LocationCode)
        .ToListAsync(ct);

    return Results.Ok(locations);
});

app.MapPost("/admin/schema/seed", async (
    ParticipantRegistry registry,
    ClassFixtureLoader loader,
    ClassificationRefresher refresher,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    CancellationToken ct) =>
{
    var root = Path.GetFullPath(configuration["Sandbox:PersonalitiesPath"]
        ?? Path.Combine(environment.ContentRootPath, "PersonalityPacks"));

    var results = new List<FixtureLoadResult>();

    foreach (var participant in registry.All)
    {
        results.Add(await loader.LoadAsync(participant, root, ct));
    }

    await refresher.RefreshAllAsync(ct);
    return Results.Ok(results);
});

// What a participant can actually resolve. Two participants asked the same
// question will answer differently, which is the point of the asymmetric fixtures.
app.MapGet("/admin/{participantId}/classes", (
    string participantId, ParticipantRegistry registry) =>
{
    var participant = registry.Get(participantId);
    var source = participant.ClassificationSource;

    var keys = new[]
    {
        "rdl:Equipment", "rdl:Instrument",
        "rdl:TemperatureIndicatingController", "rdl:SafetyCritical"
    };

    return Results.Ok(keys.Select(key =>
    {
        var held = source.FindClassByKey(key);

        if (held is null)
        {
            return new { key, held = false, chain = Array.Empty<string>(), properties = Array.Empty<object>() };
        }

        var chain = participant.Resolver.BuildTaxonomyChain(held.Id);
        var effective = participant.Resolver.Compose(chain, []);

        return new
        {
            key,
            held = true,
            chain = chain.Select(c => c.ClassKey).ToArray(),
            properties = effective.Properties.Select(object (p) => new
            {
                definition = p.Definition.DefinitionKey,
                requirement = p.Constraint.Requirement.ToString(),
                from = p.ContributedByClassName,
                p.Constraint.MinValue,
                p.Constraint.MaxValue
            }).ToArray()
        };
    }));
});

app.MapPost("/admin/eng/tags", async (
    EngService eng, AddTagRequest request, CancellationToken ct) =>
{
    var tag = await eng.AddTagAsync(
        request.TagNumber, request.ServiceDescription, request.UnitNumber, request.ClassKey,
        request.RangeMinimum, request.RangeMaximum, request.ControlAction, ct);

    return Results.Ok(new { tag.Id, tag.TagNumber, maturity = tag.Maturity.ToString() });
});

// The release event. Only a passing validation gate writes outbox rows.
app.MapPost("/admin/eng/promote", async (
    EngService eng, ParticipantRegistry registry, PromoteRequest request, CancellationToken ct) =>
{
    var publisher = registry.Get("eng").Config.Channels
        .FirstOrDefault(c => c.Role == ChannelRole.Publisher)
        ?? throw new InvalidOperationException("ENG has no publisher channel configured.");

    var result = await eng.PromoteAsync(
        request.Name, publisher.ChannelUri, publisher.Topics.FirstOrDefault(), ct);

    return result.Released ? Results.Ok(result) : Results.UnprocessableEntity(result);
});

// The message archive, which is what makes a round trip observable without a UI.
app.MapGet("/admin/{participantId}/messages", async (
    string participantId,
    IParticipantDbContextFactory factory,
    CancellationToken ct) =>
{
    await using var db = factory.Create(participantId);

    var messages = await db.Messages
        .OrderByDescending(m => m.OccurredAt)
        .Take(50)
        .Select(m => new
        {
            m.Direction,
            m.Pattern,
            m.Verb,
            m.Noun,
            m.ChannelUri,
            m.Topic,
            m.CorrelationId,
            m.IsbmMessageId,
            m.ValidationStatus,
            m.ProcessingStatus,
            m.ProcessingDetail,
            m.ContentBytes,
            m.OccurredAt
        })
        .ToListAsync(ct);

    return Results.Ok(messages);
});

app.MapGet("/admin/{participantId}/outbox", async (
    string participantId,
    IParticipantDbContextFactory factory,
    CancellationToken ct) =>
{
    await using var db = factory.Create(participantId);

    var items = await db.Outbox
        .OrderByDescending(o => o.CreatedAt)
        .Take(50)
        .ToListAsync(ct);

    return Results.Ok(items);
});

// Which ISBM sessions each participant currently holds.
//
// The difference between "no subscription is open" and "a subscription is open and
// nothing was delivered" is the difference between a Sandbox fault and a provider
// fault, and without this they look identical from outside: an empty message
// archive either way.
app.MapGet("/health/isbm/sessions", async (
    ParticipantRegistry registry,
    IIsbmSessionStoreAccessor stores,
    IServiceProvider services,
    CancellationToken ct) =>
{
    // Pump activity, so "nothing arrived" can be separated into "polling and finding
    // nothing", "not polling", and "failing every read".
    var telemetry = services.GetService<InboxTelemetry>();
    var results = new List<object>();

    foreach (var participant in registry.All)
    {
        var expected = participant.Config.Channels
            .Where(c => c.Role == ChannelRole.Subscriber)
            .Select(c => c.ChannelUri)
            .ToList();

        List<object> open = [];
        string? error = null;

        try
        {
            open = (await stores.For(participant.ParticipantId).ListAsync(ct))
                .Select(object (s) => new
                {
                    kind = s.Kind.ToString(),
                    s.ChannelUri,
                    s.SessionId,
                    s.OpenedUtc,
                    ageSeconds = (int)(DateTimeOffset.UtcNow - s.OpenedUtc).TotalSeconds
                })
                .ToList();
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        var openChannels = open
            .Select(o => o.GetType().GetProperty("ChannelUri")?.GetValue(o) as string)
            .Where(u => u is not null)
            .ToList();

        var polling = telemetry?.All
            .Where(b => b.ParticipantId == participant.ParticipantId)
            .Select(object (b) => new
            {
                b.ChannelUri,
                b.Topics,
                b.SessionId,
                b.Polls,
                b.EmptyReads,
                b.MessagesRead,
                b.Failures,
                lastPollSecondsAgo = b.LastPollUtc is null
                    ? (int?)null
                    : (int)(DateTimeOffset.UtcNow - b.LastPollUtc.Value).TotalSeconds,
                lastMessageSecondsAgo = b.LastMessageUtc is null
                    ? (int?)null
                    : (int)(DateTimeOffset.UtcNow - b.LastMessageUtc.Value).TotalSeconds,
                b.LastError
            })
            .ToList() ?? [];

        results.Add(new
        {
            participant.ParticipantId,
            subscriberChannels = expected,
            polling,
            // A subscription only receives what is published after it opens, so a
            // channel with no open session is not merely idle — anything published
            // to it meanwhile is gone.
            missingSubscriptions = expected.Where(c => !openChannels.Contains(c)).ToList(),
            sessions = open,
            error
        });
    }

    return Results.Ok(results);
});

// Reports whether each participant's Key Vault secret resolved, without exposing
// it. The fingerprint is enough to compare against the value provisioning wrote:
// same fingerprint means the mismatch is elsewhere, different means the database
// and the vault have drifted apart.
app.MapGet("/health/secrets", (IConfiguration configuration, ParticipantRegistry registry) =>
{
    var environment = configuration["Sandbox:Environment"];

    string Fingerprint(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    var names = registry.All.Select(p => p.ParticipantId)
        .Concat(["orchestrator", "tower"]);

    return Results.Ok(new
    {
        environment,
        sqlServer = configuration["Sandbox:SqlServer"],
        database = configuration["Sandbox:Database"],
        keyVault = configuration["KeyVault:Uri"],
        secrets = names.Select(name =>
        {
            var key = $"sandbox-sql-{environment}-{name}";
            var value = configuration[key];
            return new
            {
                secret = key,
                found = !string.IsNullOrEmpty(value),
                length = value?.Length ?? 0,
                fingerprint = string.IsNullOrEmpty(value) ? null : Fingerprint(value)
            };
        })
    });
});

// Diagnostics — confirms personalities loaded and schemas resolved without
// needing the UI, which is useful on a first run.
app.MapGet("/health/participants", (ParticipantRegistry registry, BodValidator validator) =>
    Results.Ok(new
    {
        participants = registry.All.Select(p => new
        {
            p.ParticipantId,
            p.Config.DisplayName,
            p.Schema,
            p.Config.SourceId,
            channels = p.Config.Channels.Count
        }),
        schemaNamespaces = validator.KnownNamespaces,
        storageConfigured,
        isbmConfigured,
        // Reported so an unprotected deployment is visible rather than assumed.
        adminKeyRequired = !string.IsNullOrWhiteSpace(builder.Configuration["Sandbox:AdminKey"])
    }));

if (isbmConfigured)
{
    var accessor = app.Services.GetRequiredService<IsbmClientAccessor>();
    accessor.Manager = app.Services.GetRequiredService<IsbmSessionManager>();
}

using (var scope = app.Services.CreateScope())
{
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


internal sealed record AddTagRequest(
    string TagNumber,
    string? ServiceDescription,
    string? UnitNumber,
    string? ClassKey,
    decimal? RangeMinimum = null,
    decimal? RangeMaximum = null,
    string? ControlAction = null);

internal sealed record PromoteRequest(string Name);

internal sealed record RejectRequest(string Reason);
