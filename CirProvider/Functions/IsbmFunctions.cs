using CirProvider.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CirProvider.Functions;

public sealed class IsbmFunctions(
    IsbmBodListener listener,
    IIsbmSessionStore sessions,
    IOptions<IsbmOptions> options,
    ILogger<IsbmFunctions> logger)
{
    private readonly IsbmOptions _options = options.Value;

    /// <summary>
    /// Polls the ISBM channels. A timer rather than a long-running background
    /// service because the Consumption host recycles freely and would kill one.
    ///
    /// RunOnStartup is deliberately off: it would fire on every scale-out
    /// instance and every cold start, multiplying polls against the broker.
    ///
    /// The setting name is flat rather than Isbm__PollSchedule. A %...% binding
    /// expression is resolved by WebJobs as a literal setting name, before the
    /// double underscore is folded into a configuration section — so the
    /// sectioned form never resolves, and the function fails indexing and is
    /// silently disabled at startup rather than failing loudly at run time.
    /// </summary>
    [Function("IsbmPoll")]
    public async Task IsbmPoll(
        [TimerTrigger("%IsbmPollSchedule%")] TimerInfo timer,
        CancellationToken ct)
    {
        if (!_options.Enabled) return;

        var report = await listener.DrainAsync(ct);

        if (report.Errors.Count > 0)
        {
            logger.LogError("ISBM poll completed with errors: {Errors}", string.Join("; ", report.Errors));
            return;
        }

        if (report.Idle) return;

        logger.LogInformation(
            "ISBM poll handled {Requests} request(s), posted {Responses} response(s), " +
            "handled {Publications} publication(s), skipped {Skipped}.",
            report.RequestsHandled, report.ResponsesPosted, report.PublicationsHandled, report.Skipped);
    }

    /// <summary>Drains on demand, so a round trip can be tested without waiting for the timer.</summary>
    [Function("IsbmDrain")]
    public async Task<IActionResult> IsbmDrain(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "isbm/drain")] HttpRequest req,
        CancellationToken ct)
    {
        var report = await listener.DrainAsync(ct);
        return new OkObjectResult(report);
    }

    /// <summary>Configuration and live session state, without exposing the API key or token.</summary>
    [Function("IsbmStatus")]
    public async Task<IActionResult> IsbmStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "isbm/status")] HttpRequest req,
        CancellationToken ct)
    {
        var open = await sessions.ListAsync(ct);

        return new OkObjectResult(new
        {
            enabled = _options.Enabled,
            baseUrl = _options.BaseUrl,
            requestChannelUri = _options.RequestChannelUri,
            publicationChannelUri = _options.PublicationChannelUri,
            consumePublications = _options.ConsumePublications,
            topics = _options.EffectiveTopics,
            maxMessagesPerPoll = _options.MaxMessagesPerPoll,
            hasApiKey = !string.IsNullOrWhiteSpace(_options.ApiKey),
            hasSecurityToken = !string.IsNullOrWhiteSpace(_options.SecurityToken),
            sessions = open.Select(s => new
            {
                kind = s.Kind.ToString(),
                channelUri = s.ChannelUri,
                sessionId = s.SessionId,
                openedUtc = s.OpenedUtc
            })
        });
    }

    /// <summary>
    /// Closes and forgets the stored sessions. The recovery path when the broker
    /// has been reset and the persisted ids are stale.
    /// </summary>
    [Function("IsbmResetSessions")]
    public async Task<IActionResult> IsbmResetSessions(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "isbm/reset")] HttpRequest req,
        [FromServices] IIsbmClient isbm,
        CancellationToken ct)
    {
        var open = await sessions.ListAsync(ct);

        foreach (var session in open)
        {
            try
            {
                await isbm.CloseSessionAsync(session.Kind, session.SessionId, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not close ISBM session {SessionId}; forgetting it anyway.", session.SessionId);
            }

            await sessions.ClearAsync(session.Kind, ct);
        }

        return new OkObjectResult(new { closed = open.Count });
    }
}
