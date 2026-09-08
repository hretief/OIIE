using Oiie.Ccom;
using Oiie.Ccom.Oagis;
using Oiie.Isbm.Client;
using Oiie.Sandbox.Api.Providers;
using Oiie.Sandbox.Api.Services;
using SimHost.Application;
using SimHost.Application.Identity;
using SimHost.Application.Participants;
using SimHost.Application.Topology;
using SimHost.Domain.Common;
using SimHost.Infrastructure.Blob;
using SimHost.Infrastructure.Isbm;

namespace Oiie.Sandbox.Api.Endpoints;

/// <summary>
/// The Sandbox admin and health surface.
///
/// This is the contract every client uses: the Blazor operator UI, the Workflow
/// Orchestration app, the Bruno collections, and the reset calls the scenario
/// runner makes on itself. The bodies are unchanged from when they lived in the
/// SimHost Program.cs -- splitting the API away from the UI was a move, not a
/// renegotiation of the contract.
/// </summary>
public static class SandboxAdminEndpoints
{
    public static WebApplication MapSandboxAdminEndpoints(this WebApplication app)
    {
// Full reset: closes nothing of our own, then purges channels.
//
// Channels are deleted and recreated rather than left alone, because a publication
// posted before the reset is still sitting on the channel and would be read by the
// next run as though it belonged to it. Stale publications leaking across runs is a
// confusing, intermittent failure mode.
app.MapPost("/admin/reset", async (
    ParticipantRegistry registry,
    IIsbmClientAccessor clients,
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

    // Purge each distinct channel the Sandbox owns, exactly once.
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

    // Participant data lives in the providers, so a channel reset touches no
    // tables. Listed for the caller's benefit only.
    var steps = registry.All.Select(p => p.ParticipantId).ToList();

    log.LogInformation(
        "Reset complete: {Channels} channel(s) purged, {Count} participant(s)",
        purged.Count, registry.All.Count);

    return Results.Ok(new
    {
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
// It also drops the CIR registry the engines write into. Identity is the one
// thing that used to survive a reset, and because CIR entries key on the integer
// ids REG-LOCATION restarts from 1, a surviving registry collided with the first
// sites created afterwards -- a greenfield run failing on a duplicate that
// belonged to the previous one.
//
// That has a consequence the caller must act on: deleting a channel destroys every
// session on it, including sessions held by systems that are not this one. Those
// systems will keep polling dead session ids until they are told to re-open, and a
// poll loop that swallows session faults will look healthy while consuming nothing.
// The response lists what needs restarting.
app.MapPost("/admin/reset/day-zero", async (
    ParticipantRegistry registry,
    IIsbmClientAccessor clients,
    EngEngineClient engine,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILoggerFactory loggerFactory,
    CancellationToken ct) =>
{
    var log = loggerFactory.CreateLogger("DayZero");
    var client = clients.For(registry.All.First().ParticipantId);

    // 1. Delete every channel the provider holds, then recreate the ones the
    // registry expects. Deleting is what clears the queue: there is no drain-all
    // operation.
    //
    // The delete list comes from the provider itself rather than from the
    // registry, because a channel created by an earlier demo — or by a workflow
    // that has since been removed — is exactly the clutter day zero exists to
    // remove, and the registry has no record of it to delete. The recreate list
    // stays registry-driven: a channel nobody is configured to use should not be
    // conjured back, and only the registry knows the intended publication or
    // request type.
    var expected = registry.All
        .SelectMany(p => p.Config.Channels.Select(c => new
        {
            Uri = c.ChannelUri,
            IsRequest = c.Role is ChannelRole.RequestProvider or ChannelRole.RequestConsumer,
            Ours = true
        }))
        .Concat(registry.All
            .Where(p => !string.IsNullOrWhiteSpace(p.Config.Cir.ChannelUri))
            .Select(p => new { Uri = p.Config.Cir.ChannelUri, IsRequest = true, Ours = false }))
        .GroupBy(c => c.Uri, StringComparer.Ordinal)
        .Select(g => new
        {
            Uri = g.Key,
            IsRequest = g.Any(c => c.IsRequest),
            Ours = g.All(c => c.Ours)
        })
        .ToList();

    var expectedUris = expected
        .Select(c => c.Uri)
        .ToHashSet(StringComparer.Ordinal);

    // Everything the provider currently reports. If this call fails the wipe
    // cannot be honest about what it removed, so it is not swallowed.
    var discovered = new List<string>();
    string? discoveryError = null;

    try
    {
        discovered = (await client.GetChannelsAsync(ct))
            .Select(c => c.ChannelUri)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToList();
    }
    catch (Exception ex)
    {
        discoveryError = ex.Message;
        log.LogWarning(ex,
            "Could not enumerate channels; only registry-known channels will be deleted.");
    }

    var toDelete = discovered
        .Concat(expectedUris)
        .ToHashSet(StringComparer.Ordinal);

    var rebuilt = new List<object>();
    var removed = new List<object>();

    foreach (var uri in toDelete)
    {
        var match = expected.FirstOrDefault(c => string.Equals(c.Uri, uri, StringComparison.Ordinal));
        var type = match is { IsRequest: true } ? IsbmChannelType.Request : IsbmChannelType.Publication;

        try
        {
            await client.DeleteChannelAsync(uri, ct);

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
                gone = await client.GetChannelAsync(uri, ct) is null;
            }

            if (!gone)
            {
                rebuilt.Add(new
                {
                    Uri = uri,
                    type = type.ToString(),
                    ours = match?.Ours ?? false,
                    ok = false,
                    error = "Still present after delete. Not recreated — the queue and any " +
                            "sessions on it were left as they were."
                });
                continue;
            }

            // Not in the registry: deleted deliberately and left gone.
            if (match is null)
            {
                removed.Add(new { Uri = uri, ok = true });
                continue;
            }

            await client.CreateChannelAsync(uri, type, "OIIE Sandbox day zero", null, ct);
            rebuilt.Add(new { Uri = uri, type = type.ToString(), ours = match.Ours, ok = true });
        }
        catch (Exception ex)
        {
            if (match is null)
            {
                removed.Add(new { Uri = uri, ok = false, error = ex.Message });
                continue;
            }

            rebuilt.Add(new { Uri = uri, type = type.ToString(), ours = match.Ours, ok = false, error = ex.Message });
        }
    }

    // 2. Participants.
    //
    // Nothing is laid down here any more, and nothing is dropped: each
    // participant's data belongs to its provider, which is reset over HTTP
    // below. Sites, owners and twins all arrive the same way -- over the bus,
    // or not at all.
    var participants = registry.All.Select(p => p.ParticipantId).ToList();

    // 3. The external systems: the CIR registry, ENG's database, the engine's
    //    watermark, and the REG-LOCATION, MMS and CMS databases.
    //
    // Separate from the participant schemas above because none of these are
    // sandbox participants: each is a Functions host with its own database,
    // reached over HTTP like anything else would reach it. Without this an iTwin
    // added through the UI outlives every reset, and the next demo starts with
    // the previous one's twins, locations and assets already present -- which is
    // exactly the state a greenfield walkthrough is meant to rule out.
    //
    // CIR goes first, before the databases whose rows it identifies. Identity
    // outliving its data is the harmful order: CIR entries key on integer ids
    // that the reset restarts from 1, so a registry left populated collides with
    // the first sites created afterwards. The reverse -- rows briefly without
    // identity -- is repaired by the next registration.
    var providerProblems = new List<string>(await engine.ResetCirAsync(ct));

    providerProblems.AddRange(await engine.ResetAsync(ct));

    log.LogInformation(
        "Day zero complete: {Channels} channel(s) rebuilt, {Removed} removed, {Providers} provider problem(s)",
        rebuilt.Count, removed.Count, providerProblems.Count);

    // Warnings are assembled rather than listed literally: the wipe now removes
    // channels the registry never knew about, and which sessions that breaks
    // depends on what was actually found.
    //
    // The CIR provider's own sessions are not warned about here: ResetCirAsync
    // reopens them over HTTP once the channels are back, and reports it itself
    // if that fails.
    var actionRequired = new List<string>();

    if (removed.Count > 0)
    {
        actionRequired.Add(
            $"{removed.Count} channel(s) not known to the registry were deleted and NOT " +
            "recreated. Any system still holding a session on one will keep polling an id " +
            "the broker has forgotten; restart it or have it re-open.");
    }

    if (discoveryError is not null)
    {
        actionRequired.Add(
            $"Channels could not be enumerated ({discoveryError}), so only registry-known " +
            "channels were deleted. Channels left by earlier demos may still be present.");
    }

    actionRequired.AddRange(providerProblems);

    return Results.Ok(new
    {
        channels = rebuilt,
        channelsRemoved = removed,
        participants,
        providersReset = providerProblems.Count == 0,
        actionRequired
    });
});

// Creates every channel each participant is bound to. Idempotent, and run before
// the first publish rather than assumed: the Sandbox resets constantly, and a
// simulator that needs manual channel setup between runs is not resettable.
app.MapPost("/admin/isbm/channels/ensure", async (
    IsbmChannelProvisioner provisioner,
    CancellationToken ct) =>
{
    var results = await provisioner.EnsureAllAsync(ct);
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


// Publish-subscribe loopback: this app subscribes and publishes on the same
// channel, seconds apart, in one process.
//
// Isolates the Sandbox from the provider. If a message posted after a
// confirmed-open subscription cannot be read back, nothing about participant
// configuration, timing or session lifecycle explains it.
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


// Scoped to the twin the caller is looking at. The proposal carries the context
// the sender asserted, so the filter is a column match here rather than a registry
// resolution -- a proposal has not been admitted to the model and has no registry
// identity yet. Omitting the twin returns every context, which the batch scenarios
// rely on.
//
// The twin is named by GUID. It is the only identifier a twin has that does not
// change: the code and name are display labels the owning system may edit, so
// matching on those would break this queue on a rename.
//
// state defaults to Proposed, which is the working queue. Passing "all" returns
// decided rows too, so a steward can see what was approved beside what is still
// outstanding rather than watching rows vanish on approval.
//
// The vocabulary is listed here rather than taken from an enum because the states
// are the provider's, and the sandbox only forwards the caller's choice.
string[] StewardshipStates = ["Proposed", "Approved", "Rejected"];

app.MapGet("/admin/reg-location/stewardship", async (
    IRegLocationSource source, string? twin, string? state, CancellationToken ct) =>
{
    bool includeDecided;

    if (string.IsNullOrWhiteSpace(state))
    {
        includeDecided = false;
    }
    else if (string.Equals(state, "all", StringComparison.OrdinalIgnoreCase))
    {
        includeDecided = true;
    }
    else if (StewardshipStates.Contains(state, StringComparer.OrdinalIgnoreCase))
    {
        // Anything other than the working queue needs the decided rows loaded
        // before it can be narrowed to the state asked for.
        includeDecided = !string.Equals(state, "Proposed", StringComparison.OrdinalIgnoreCase);
    }
    else
    {
        // Named but unrecognised is a caller error, not a reason to silently fall
        // back to the default and return a queue they did not ask for.
        return Results.BadRequest(new
        {
            error = $"Unknown stewardship state '{state}'.",
            allowed = StewardshipStates.Append("all").ToArray()
        });
    }

    var queue = await source.GetQueueAsync(twin, includeDecided, ct);

    // A specific decided state was asked for, so the superset loaded above is
    // narrowed here rather than in each source.
    if (!string.IsNullOrWhiteSpace(state)
        && !string.Equals(state, "all", StringComparison.OrdinalIgnoreCase))
    {
        queue = queue
            .Where(s => string.Equals(s.State, state, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    return Results.Ok(new
    {
        count = queue.Count,
        items = queue
    });
});

// REG-LOCATION's release event: approval admits proposals to the authoritative
// model, assigns registry identifiers, and republishes.
app.MapPost("/admin/reg-location/approve", async (
    IRegLocationSource source,
    ApproveRequest? request, CancellationToken ct) =>
{
    // Approval records the decision in the registry and stops there. Nothing is
    // published from here: REG-LOCATION notifies its engine, and the engine
    // carries the approval onto a channel. A caller watching for a downstream
    // effect is therefore watching the engine, and will see nothing if it is not
    // deployed and subscribed.
    var approved = await source.ApproveAsync(request?.ProposalIds, ct);

    return Results.Ok(new
    {
        approved,
        published = false,
        note = "Recorded in REG-LOCATION. Publication is the engine's, not the registry's."
    });
});

// The registry defines a Rejected state but exposes no route that reaches it.
// Answering 501 rather than accepting the request and doing nothing: a steward
// who believes they refused a tag, and finds it approved later, is worse off
// than one told plainly that refusal is unavailable here.
app.MapPost("/admin/reg-location/reject", (RejectRequest request) =>
    Results.Problem(
        title: "Rejection is not available against REG-LOCATION.",
        detail: "The registry exposes only an approve route. Refusing a proposal "
              + "would have to be modelled there before it can be offered here.",
        statusCode: StatusCodes.Status501NotImplemented));

// The classes ENG itself can store an element against.
//
// The only class catalog left. The Sandbox used to hold its own reference-data
// library (rdl:*) and answer "what can this participant bind" from it, but that
// library had no writer and no reader once BOD handling moved to the engines.
// This one answers "what can an element actually be created on" from ENG's EC
// metadata (ENG.*), and its keys are the only ones that resolve to an ECClassId
// the provider will accept on a write.
app.MapGet("/admin/eng/element-class-catalog", async (
    EngProviderClient client, CancellationToken ct) =>
{
    var classes = await client.GetClassesAsync(ct);

    return Results.Ok(classes
        // Abstract classes are filtered out by the provider already, but an
        // element cannot be created on one and a picker should not offer it.
        .Where(c => !string.Equals(c.ClassModifier, "Abstract", StringComparison.OrdinalIgnoreCase))
        .OrderBy(c => c.FullyQualifiedName, StringComparer.OrdinalIgnoreCase)
        .Select(c => new
        {
            // The fully qualified name is the key, because that is what round
            // trips: an element read back reports FullyQualifiedECClassName,
            // and a picker whose value did not match it would show a stored
            // class as "not in reference data".
            key = c.FullyQualifiedName,
            name = c.DisplayLabel ?? c.ClassName,

            // The id the write path needs. Carried so the caller does not have
            // to resolve the name a second time.
            ecClassId = c.ECClassId
        }));
});

// --- Topology --------------------------------------------------------------

// The channel topology, resolved. Serves both the UI, which renders the journey
// from it, and the participant engines, which read their own channels and topics
// from it rather than from their local settings.
//
// Serving it rather than having each consumer read the files is what keeps the
// engines separate hosts: they cannot share a project reference, and a copy of
// the topology deployed alongside each one would be a copy that can go stale.
//
// {iTwinId} is left unsubstituted unless a twin is supplied, so a caller can see
// the shape of a per-iTwin channel without naming one.
app.MapGet("/admin/topology", (
    TopologyRegistry topology,
    IConfiguration configuration,
    Guid? iTwinId) =>
{
    var enterprise = configuration["EngEngine:Enterprise"] ?? "acme";

    return Results.Ok(new
    {
        enterprise,
        iTwinId,
        scenarios = topology.Scenarios.Select(s => new
        {
            s.ScenarioId,
            s.Name,
            s.Scenario,
            s.UseCase,
            completion = s.Completion is null ? null : new
            {
                s.Completion.Participant,
                s.Completion.Contains,
                s.Completion.Description,
            },
            channels = s.Channels.Select(c => new
            {
                uri = c.Resolve(enterprise, iTwinId),
                template = c.Uri,
                c.Type,
                c.Publisher,
                c.Subscribers,
                c.Topics,
                c.Description,
                c.IsPerITwin,
            }),
        }),
    });
});


// The twins ENG holds designs for.
app.MapGet("/admin/eng/twins", async (IEngSource source, CancellationToken ct) =>
    Results.Ok(await source.ListTwinsAsync(ct)));

// Brings an iTwin that already exists on the platform into the sandbox.
//
// This is the UI's "add an existing iTwin" action, and it is the entry point to
// the SyncSites workflow: the twin is recorded in ENG, given its own publication
// channel, then announced, and the REG-LOCATION engine turns that announcement
// into the Scope, Item and Serial that every later SyncSegments needs somewhere
// to land.
//
// Only the ENG provider is written. The sandbox holds no twin of its own: it is
// a control plane, and a second copy here would be a record nothing reads and
// everything could disagree with.
//
// Neither a failed channel nor a failed announcement is thrown. The twin is
// genuinely registered by then, and answering with an error would invite a retry
// that looks like a duplicate rather than telling the operator which downstream
// piece is not running.
app.MapPost("/admin/eng/itwins/add", async (
    EngEngineClient engine,
    IsbmChannelProvisioner channels,
    AddITwinRequest request,
    CancellationToken ct) =>
{
    if (request.ITwinId == Guid.Empty)
    {
        return Results.BadRequest(new { error = "iTwinId is required and cannot be empty." });
    }

    // Both names are optional on the platform, so neither can be relied on for a
    // code. Falling back to the id keeps the twin nameable rather than rejecting
    // a legitimate one over a field it never carried.
    var code = Blank(request.Number) ?? Blank(request.DisplayName) ?? request.ITwinId.ToString();
    var name = Blank(request.DisplayName) ?? Blank(request.Number) ?? code;

    // Before the announcement, not after: SyncSegments for this twin will be
    // published onto this channel, and a channel created only once something
    // tries to publish is a channel that is missing exactly when it is first
    // needed.
    var channel = await channels.EnsureForITwinAsync(request.ITwinId, name, ct);

    var detail = await engine.BootstrapAsync(request, ct);

    return Results.Ok(new AddITwinResult(
        request.ITwinId, code, name,
        Registered: detail is null,
        Announced: detail is null,
        Detail: detail,
        ChannelUri: channel.ChannelUri,
        ChannelError: channel.Created ? null : channel.Error));
});

static string? Blank(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value.Trim();

// Removes an iTwin and lets the deletion cascade through the ecosystem.
//
// The mirror of the add above: ENG drops the twin, the engine announces it as a
// SyncSites carrying the Delete action code, and the REG-LOCATION engine cancels
// the site's CIR entries, cascades the scope delete and tears down the per-iTwin
// channels.
//
// Provider-backed only. Without a provider the sandbox is itself the system of
// record, and there is no Functions host to carry the announcement -- removing
// the local row would leave every downstream registry holding a site nothing
// will ever retract. Refusing is the honest answer.
app.MapDelete("/admin/eng/itwins/{iTwinId:guid}", async (
    EngEngineClient engine,
    Guid iTwinId,
    CancellationToken ct) =>
{
    if (!engine.IsProviderConfigured)
    {
        return Results.BadRequest(new
        {
            error = "No ENG provider is configured, so the deletion cannot be propagated."
        });
    }

    var detail = await engine.TeardownAsync(iTwinId, ct);

    return Results.Ok(new DeleteITwinResult(
        iTwinId,
        Removed: detail is null,
        Announced: detail is null,
        Detail: detail));
});

// The iModels ENG will accept elements against, for one twin.
//
// The browser could read these from the platform itself -- it holds the IMS
// token -- but what matters when authoring is which iModels ENG knows, and those
// are only the ones that have been synced. Listing the provider's view keeps the
// picker from offering a model the POST would then refuse.
app.MapGet("/admin/eng/imodels", async (
    EngEngineClient engine,
    Guid? iTwinId,
    HttpRequest http,
    CancellationToken ct) =>
{
    if (ResolveTwin(iTwinId, http) is not { } twin)
    {
        return Results.BadRequest(new { error = "iTwinId is required, in the query or the x-itwin-id header." });
    }

    if (!engine.IsProviderConfigured)
    {
        return Results.Ok(Array.Empty<EngIModelSummary>());
    }

    return Results.Ok(await engine.GetIModelsAsync(twin, ct));
});

// Records an iModel the browser read from the platform.
//
// The sandbox has no platform credentials of its own, so the caller carries the
// detail it has just read rather than the server fetching it again -- the same
// reason AddITwinRequest carries the twin's classification.
app.MapPost("/admin/eng/imodels/sync", async (
    EngEngineClient engine,
    SyncIModelRequest request,
    CancellationToken ct) =>
{
    if (request.IModelId == Guid.Empty || request.ITwinId == Guid.Empty)
    {
        return Results.BadRequest(new { error = "iModelId and iTwinId are both required." });
    }

    // Both platform names are optional, so neither can be relied on for a code.
    var code = Blank(request.Code) ?? Blank(request.DisplayName) ?? request.IModelId.ToString();

    var detail = await engine.SyncIModelAsync(
        request.IModelId, request.ITwinId, code, Blank(request.Description), ct);

    return detail is null
        ? Results.Ok(new { recorded = true, iModelId = request.IModelId, code })
        : Results.Ok(new { recorded = false, iModelId = request.IModelId, code, detail });
});

// The twin a request works in.
//
// Body first, then header. The header exists so a client can set the twin once
// for a session rather than repeating it in every payload; the body wins because
// a request that names a twin explicitly means it.
//
// There is no fallback. ENG owns the twins, and picking one on the caller's
// behalf would answer a question about a plant they did not ask about.
static Guid? ResolveTwin(Guid? fromBody, HttpRequest http)
{
    if (fromBody is { } body && body != Guid.Empty)
    {
        return body;
    }

    var header = http.Headers["x-itwin-id"].FirstOrDefault();

    return Guid.TryParse(header, out var parsed) && parsed != Guid.Empty ? parsed : null;
}

// ENG's tags, scoped to one twin. Without the scope this would report every plant's
// design as though it were one model, which is the confusion the twin exists to end.
app.MapGet("/admin/eng/tags", async (
    IEngSource source, HttpRequest http, Guid? iTwinId, CancellationToken ct) =>
{
    if (ResolveTwin(iTwinId, http) is not { } twin)
    {
        return Results.BadRequest(new { error = "iTwinId is required, in the query or the x-itwin-id header." });
    }

    var tags = await source.ListSegmentsAsync(twin, ct);

    return Results.Ok(new
    {
        iTwinId = twin,
        count = tags.Count,

        tags = tags.Select(t => new
        {
            t.Id,
            t.TagNumber,
            federationId = t.FederationId,
            iModelId = t.IModelId,
            t.ServiceDescription,
            t.UnitNumber,
            t.ClassKey,
            t.RangeMinimum,
            t.RangeMaximum,
            t.ControlAction,
            t.PidReference,

            // ENG stores no per-element Maturity, so t.Maturity is always null for
            // a provider-backed segment; reporting it verbatim would leave the UI
            // believing nothing is ever published. PublishedInVersion is what ENG
            // actually tracks -- a marker's changeset range covering the element --
            // so it is the source of truth here, with t.Maturity kept only as a
            // fallback for a source that does report one directly.
            maturity = t.Maturity ?? (t.PublishedInVersion is not null ? "Published" : "WorkInProgress"),
            t.PublishedInVersion,
            t.UpdatedAt
        })
    });
});

// Authored through IEngSource, the same source GET above reads.
//
// Previously this wrote the sandbox participant directly while the GET read
// whichever source was configured. With the ENG provider configured those were
// two different databases, so a segment saved here returned 200 and then never
// appeared in the list. Both now go to one place by construction.
app.MapPost("/admin/eng/tags", async (
    IEngSource source, HttpRequest http, AddTagRequest request, CancellationToken ct) =>
{
    if (ResolveTwin(request.ITwinId, http) is not { } twin)
    {
        return Results.BadRequest(new { error = "iTwinId is required, in the body or the x-itwin-id header." });
    }

    try
    {
        var segment = await source.AddSegmentAsync(twin, new NewSegment(
            request.TagNumber, request.ServiceDescription, request.UnitNumber,
            request.ClassKey, request.RangeMinimum, request.RangeMaximum,
            request.ControlAction, request.CodePrefix,
            request.FederationId, request.IModelId, request.ElementId), ct);

        // The identity is returned because in the allocation case the caller did not
        // choose either value and has no other way to learn what it was given. The twin
        // is returned for the same reason: it may have come from a header or a default.
        return Results.Ok(new
        {
            segment.TagNumber,
            federationId = segment.FederationId,
            iTwinId = segment.ITwinId,
            iModelId = segment.IModelId,
            maturity = segment.Maturity
        });
    }
    catch (InvalidOperationException ex)
    {
        // A federation id already in use, a class the source does not hold, or a
        // missing iModel. 409 rather than 400: the request is well-formed and the
        // user did nothing malformed -- they named something real that the state
        // of the twin refuses.
        return Results.Conflict(new { error = ex.Message });
    }
    catch (ProviderUnavailableException ex)
    {
        // Distinguished from the refusal above: nothing was decided, so this is
        // worth retrying, and telling the user their element was rejected would
        // be false.
        return Results.Problem(
            title: "ENG is unavailable.",
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// A candidate identity for an operator who has no register to copy one from.
//
// Server-side rather than generated in the browser, so a suggested id is minted by
// the same service and the same version-7 scheme as one ENG assigns itself. A UUID
// built in the client would be indistinguishable to a reviewer but would not be
// time-ordered, and the sandbox is meant to demonstrate the real identity rules
// rather than something that merely looks like them.
//
// Nothing is reserved or persisted. This only answers "what would you have used",
// and the identity is not real until it is submitted with a segment.
app.MapGet("/admin/eng/federation-id/suggest", (ITagIdentityService identities) =>
    Results.Ok(new { federationId = identities.Mint() }));

// The release act, routed through the same source the ENG panel reads.
//
// This creates a marker in ENG and publishes nothing: EngEngine carries the
// handover onto ISBM off its own poll, so the release happens asynchronously
// from the click that caused it.
app.MapPost("/admin/eng/promote", async (
    IEngSource source, HttpRequest http,
    PromoteRequest request, CancellationToken ct) =>
{
    if (ResolveTwin(request.ITwinId, http) is not { } twin)
    {
        return Results.BadRequest(new { error = "iTwinId is required, in the body or the x-itwin-id header." });
    }

    var result = await source.PromoteAsync(twin, request.Name, ct);

    // Reshaped to the names the panel already binds to. The wire contract
    // predates the source split and changing it here would break the UI for a
    // rename that buys nothing.
    var body = new
    {
        released = result.Released,
        namedVersionId = result.NamedVersionId,
        name = result.Name,
        tagCount = result.SegmentCount,
        markerCount = result.MarkerCount,
        findings = result.Findings,
    };

    return result.Released ? Results.Ok(body) : Results.UnprocessableEntity(body);
});

// Diagnostics — confirms personalities loaded and BOD schemas resolved without
// needing the UI, which is useful on a first run.
app.MapGet("/health/participants", (
    ParticipantRegistry registry, BodValidator validator, IConfiguration configuration) =>
    Results.Ok(new
    {
        participants = registry.All.Select(p => new
        {
            p.ParticipantId,
            p.Config.DisplayName,
            p.Config.SourceId,
            channels = p.Config.Channels.Count
        }),
        schemaNamespaces = validator.KnownNamespaces,
        // A namespace can be listed above and still be unusable, because a
        // compilation fault drops the whole set. Without these the bod_valid
        // concern says only "schemas were unavailable" and gives nothing to act on.
        schemaDiagnostics = validator.LoadDiagnostics,
        storageConfigured = SandboxCapabilities.IsStorageConfigured(configuration),
        isbmConfigured = SandboxCapabilities.IsIsbmConfigured(registry),
        // Reported so an unprotected deployment is visible rather than assumed.
        adminKeyRequired = !string.IsNullOrWhiteSpace(configuration[SandboxAdminKey.ConfigurationKey])
    }));


        return app;
    }
}

/// <summary>
/// Supply <see cref="TagNumber"/> to author a specific tag, or <see cref="CodePrefix"/>
/// to have the identity service allocate the next one in that series.
/// </summary>
internal sealed record AddTagRequest(
    string? TagNumber,
    string? ServiceDescription,
    string? UnitNumber,
    string? ClassKey,
    decimal? RangeMinimum = null,
    decimal? RangeMaximum = null,
    string? ControlAction = null,
    string? CodePrefix = null,
    Guid? ITwinId = null,
    Guid? FederationId = null,
    /// <summary>
    /// The iModel the element's data comes from. Supplied by the picker when
    /// authoring; on an edit it is the element's own model, since the upsert
    /// is scoped by model.
    /// </summary>
    Guid? IModelId = null,
    /// <summary>
    /// ENG's ECInstanceId, sent only when editing. Absent means "create":
    /// ENG matches an existing element by this id, so an edit that omitted it
    /// would insert a second element and collide on the code.
    /// </summary>
    long? ElementId = null);

internal sealed record PromoteRequest(string Name, Guid? ITwinId = null);

internal sealed record RejectRequest(string Reason);

/// <summary>The proposals a steward chose. Null or empty means the whole queue.</summary>
internal sealed record ApproveRequest(IReadOnlyCollection<long>? ProposalIds);
