using System.Net.Http.Json;
using Oiie.Isbm.Client;
using SimHost.Application.Participants;
using SimHost.Infrastructure.Isbm;

namespace SimHost.Application.Scenarios;

/// <summary>What a reset did, or why it could not be done.</summary>
public sealed record ResetOutcome(bool Succeeded, string Summary, string? Detail = null);

/// <summary>
/// Runs the admin reset endpoints on behalf of the UI.
///
/// Calls the endpoints over HTTP rather than invoking the underlying services directly,
/// even though both live in this process. The endpoints are already the definition of
/// what a reset means \u2014 sessions, channels, tables, reference data and run history, in
/// that order \u2014 and reimplementing that ordering behind a button would create a second
/// definition free to drift from the first. It also keeps the admin-key guard on the
/// path it protects instead of quietly bypassing it.
/// </summary>
public sealed class SandboxResetService(
    IHttpClientFactory clients,
    IConfiguration configuration,
    ParticipantRegistry registry,
    ILogger<SandboxResetService> logger,
    IsbmSessionManager? sessions = null)
{
    /// <summary>
    /// Tears down and rebuilds participant state: tables, reference data, run history,
    /// and this deployment's own ISBM channels.
    /// </summary>
    public Task<ResetOutcome> ResetAsync(string baseUrl, CancellationToken ct = default) =>
        PostAsync(baseUrl, "/admin/reset", "Environment reset", ct);

    /// <summary>
    /// Day zero. Also deletes channels belonging to other systems, which destroys their
    /// sessions — they will keep polling dead ids until restarted.
    /// </summary>
    public Task<ResetOutcome> DayZeroAsync(string baseUrl, CancellationToken ct = default) =>
        PostAsync(baseUrl, "/admin/reset/day-zero", "Day zero", ct);

    /// <summary>
    /// Honours a scenario's <c>setup.reset</c>, if it declares one.
    ///
    /// Must be called before the run row is created, not from inside the runner: a reset
    /// purges run history, so a run that reset itself mid-flight would delete the row it
    /// was about to write its own steps into.
    ///
    /// A failed reset returns rather than throws, leaving the decision to the caller —
    /// but callers should treat it as fatal. A scenario asking for a reset is asserting
    /// against a known-empty environment, and running it against whatever happened to be
    /// left over produces failures that describe the previous run rather than this one.
    /// </summary>
    public async Task<ResetOutcome?> ApplyAsync(
        ScenarioDefinition scenario, string baseUrl, CancellationToken ct = default)
    {
        if (!scenario.Setup.Reset)
        {
            return null;
        }

        logger.LogInformation(
            "{ScenarioId} declares setup.reset; clearing the environment before it starts.",
            scenario.Id);

        var outcome = await ResetAsync(baseUrl, ct);

        if (outcome.Succeeded)
        {
            await ReopenSubscriptionsAsync(scenario, ct);
        }

        return outcome;
    }

    /// <summary>
    /// Re-opens the subscriptions the scenario declares, before it checks they are open.
    ///
    /// A reset closes every session and recreates the channels, and nothing re-subscribes
    /// eagerly: the inbox pump opens a subscription lazily on its next poll, seconds
    /// later. The run's own precondition would therefore abort with "these declared
    /// subscriptions are not open" — correctly, but for a condition the reset itself
    /// created and that resolves on its own moments afterwards.
    ///
    /// Opening them here rather than sleeping makes the wait exactly as long as it needs
    /// to be, and turns a provider that genuinely cannot subscribe into an error at the
    /// point of subscribing rather than an abort attributed to the scenario.
    /// </summary>
    private async Task ReopenSubscriptionsAsync(
        ScenarioDefinition scenario, CancellationToken ct)
    {
        if (sessions is null)
        {
            return;
        }

        var required = scenario.Setup.Channels
            .Where(c => string.Equals(c.Type, "Publication", StringComparison.OrdinalIgnoreCase))
            .SelectMany(c => c.Subscribers.Select(s => (Subscriber: s, c.Uri)))
            .ToList();

        foreach (var (subscriber, uri) in required)
        {
            // Topics come from the participant's own binding rather than the scenario
            // file: a subscription opened on the wrong topics is open but deaf, which
            // passes the precondition and then fails every message assertion.
            var binding = registry.TryGet(subscriber, out var participant)
                ? participant!.Config.Channels.FirstOrDefault(c =>
                    c.Role == ChannelRole.Subscriber &&
                    c.ChannelUri.EndsWith(uri, StringComparison.OrdinalIgnoreCase))
                : null;

            try
            {
                await sessions.GetOrOpenAsync(
                    subscriber,
                    IsbmSessionKind.Subscription,
                    binding?.ChannelUri ?? uri,
                    binding?.Topics ?? [],
                    ct);
            }
            catch (Exception ex)
            {
                // Logged rather than thrown: the run's own precondition check is the
                // authority on whether the subscription is usable, and it reports the
                // failure with evidence. Throwing here would pre-empt it with less.
                logger.LogWarning(
                    ex, "Could not re-open {Subscriber}'s subscription on {ChannelUri} after reset.",
                    subscriber, uri);
            }
        }
    }

    private async Task<ResetOutcome> PostAsync(
        string baseUrl, string path, string label, CancellationToken ct)
    {
        try
        {
            using var client = clients.CreateClient();

            // Long, because day zero deletes and recreates every channel and waits for
            // each delete to be observable before recreating it.
            client.Timeout = TimeSpan.FromMinutes(5);

            using var request = new HttpRequestMessage(
                HttpMethod.Post, new Uri(new Uri(baseUrl), path));

            var key = configuration["Sandbox:AdminKey"];

            if (!string.IsNullOrWhiteSpace(key))
            {
                request.Headers.Add(Middleware.AdminKeyMiddleware.HeaderName, key);
            }

            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "{Label} failed with {Status}: {Body}", label, response.StatusCode, body);

                return new ResetOutcome(
                    false, $"{label} failed ({(int)response.StatusCode}).", body);
            }

            logger.LogInformation("{Label} completed from the UI.", label);

            return new ResetOutcome(true, $"{label} complete.", body);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Label} could not be run.", label);
            return new ResetOutcome(false, $"{label} could not be run.", ex.Message);
        }
    }
}
