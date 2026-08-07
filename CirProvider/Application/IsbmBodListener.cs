using CirProvider.Domain.Bod;
using CirProvider.Infrastructure.Isbm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CirProvider.Application;

public sealed record IsbmDrainReport
{
    public int RequestsHandled { get; init; }
    public int ResponsesPosted { get; init; }
    public int PublicationsHandled { get; init; }
    public int Skipped { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>
    /// Messages that were removed without being processed, with a preview of the
    /// payload. Surfaced in the report rather than only in logs: an unreadable
    /// message is a configuration problem between two systems, and whoever is
    /// running the drain is the person who can act on it.
    /// </summary>
    public IReadOnlyList<string> Discarded { get; init; } = [];

    public bool Idle => RequestsHandled == 0 && PublicationsHandled == 0;
}

/// <summary>
/// Bridges ws-ISBM to the Annex A dispatcher.
///
/// Two channels, because the BOD catalogue has two shapes. The six
/// request-response BODs arrive on a ProviderRequest session and get a response
/// posted back. The five with no response — the four Cancel BODs and
/// ChangeEntryCIRID — arrive as publications on a Subscription session.
/// </summary>
public sealed class IsbmBodListener(
    IIsbmClient isbm,
    IIsbmSessionStore sessions,
    IBodDispatcher dispatcher,
    IOptions<IsbmOptions> options,
    ILogger<IsbmBodListener> logger)
{
    private readonly IsbmOptions _options = options.Value;

    public async Task<IsbmDrainReport> DrainAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            logger.LogDebug("ISBM listener is disabled.");
            return new IsbmDrainReport();
        }

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return new IsbmDrainReport { Errors = ["Isbm__BaseUrl is not configured."] };
        }

        var errors = new List<string>();
        var discarded = new List<string>();
        int requests = 0, responses = 0, publications = 0, skipped = 0;

        // Counters are passed in rather than returned: a failure part-way through
        // must still report what already happened, or the drain claims nothing was
        // handled while the store plainly shows otherwise.
        var requestCounters = new DrainCounters();
        try
        {
            await DrainRequestsAsync(requestCounters, discarded, errors, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Draining the request channel failed.");
            errors.Add($"requests: {ex.Message}");
        }
        requests = requestCounters.Handled;
        responses = requestCounters.Responded;
        skipped = requestCounters.Skipped;

        if (_options.ConsumePublications)
        {
            var publicationCounters = new DrainCounters();
            try
            {
                await DrainPublicationsAsync(publicationCounters, discarded, errors, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Draining the publication channel failed.");
                errors.Add($"publications: {ex.Message}");
            }
            publications = publicationCounters.Handled;
        }

        return new IsbmDrainReport
        {
            RequestsHandled = requests,
            ResponsesPosted = responses,
            PublicationsHandled = publications,
            Skipped = skipped,
            Errors = errors,
            Discarded = discarded
        };
    }

    // -----------------------------------------------------------------------

    private sealed class DrainCounters
    {
        public int Handled;
        public int Responded;
        public int Skipped;
    }

    private async Task DrainRequestsAsync(
        DrainCounters counters, List<string> discarded, List<string> errors, CancellationToken ct)
    {
        var (sessionId, justOpened) = await EnsureSessionAsync(
            IsbmSessionKind.ProviderRequest, _options.RequestChannelUri,
            (uri, topics, token) => isbm.OpenProviderRequestSessionAsync(uri, topics, token), ct);

        for (var i = 0; i < _options.MaxMessagesPerPoll; i++)
        {
            var message = await ReadWithSessionRecoveryAsync(
                IsbmSessionKind.ProviderRequest, sessionId, justOpened && i == 0,
                (id, token) => isbm.ReadRequestAsync(id, token), ct);

            if (message is null) break;

            counters.Handled++;

            // Unreadable payload. Removing it is the only way to unblock the
            // queue, so record what it was before dropping it.
            if (message.Content is null)
            {
                var note = $"request {message.MessageId} was not XML: {Preview(message.RawContent)}";
                logger.LogError("Discarding unreadable request {MessageId}: {Content}",
                    message.MessageId, Preview(message.RawContent, 400));
                discarded.Add(note);
                counters.Skipped++;
                await isbm.RemoveRequestAsync(sessionId, ct);
                continue;
            }

            // The sender correlates on BODID, so every log line carries it.
            var bodId = BodIdOf(message.Content);
            var bodName = message.Content.Name.LocalName;

            try
            {
                var response = await dispatcher.DispatchAsync(new System.Xml.Linq.XDocument(message.Content), ct);

                if (response is not null)
                {
                    // The request message id becomes OriginalMessageID, which is
                    // the correlation Annex A accepts instead of echoing
                    // OriginalApplicationArea.
                    await isbm.PostResponseAsync(sessionId, message.MessageId, response.Root!, ct);
                    counters.Responded++;

                    logger.LogInformation(
                        "Answered {Bod} BODID {BodId} (message {MessageId}) with {Response}.",
                        bodName, bodId, message.MessageId, response.Root?.Name.LocalName);
                }
                else
                {
                    // Either a BOD the catalogue gives no response, or one whose
                    // confirmation code suppressed it. A sender expecting a reply
                    // sees silence, so it is worth stating which.
                    logger.LogInformation(
                        "Handled {Bod} BODID {BodId} (message {MessageId}); no response BOD is due.",
                        bodName, bodId, message.MessageId);
                }
            }
            catch (NotSupportedException ex)
            {
                // Not a ws-CIR BOD at all, so there is no response BOD to fault
                // on. Removing it is the only way to advance the queue.
                var note = $"{bodName} BODID {bodId} (message {message.MessageId}) is not a ws-CIR BOD: {ex.Message}";
                logger.LogWarning(ex, "Discarding unrecognised message {MessageId}: {Content}",
                    message.MessageId, Truncate(message.Content.ToString()));
                discarded.Add(note);
                counters.Skipped++;
            }
            catch (Exception ex)
            {
                // The dispatcher answers a recognised BOD with a fault rather than
                // throwing, so reaching here means the response could not be
                // posted — a transport failure, not a bad document. Leave the
                // message in place so the next drain retries it.
                var note = $"{bodName} BODID {bodId} (message {message.MessageId}) failed: {ex.Message}";
                logger.LogError(ex,
                    "Could not complete {Bod} BODID {BodId} (message {MessageId}); leaving it queued for retry.",
                    bodName, bodId, message.MessageId);
                errors.Add(note);

                // Deliberately NOT removed: the document was fine and the failure
                // was ours, so the next drain should retry it.
                break;
            }

            // Removed on every other path. A message left at the head replays
            // forever, and the sender has already been answered — with a fault
            // if the request could not be carried out.
            await isbm.RemoveRequestAsync(sessionId, ct);
        }

    }

    private async Task DrainPublicationsAsync(
        DrainCounters counters, List<string> discarded, List<string> errors, CancellationToken ct)
    {
        var (sessionId, justOpened) = await EnsureSessionAsync(
            IsbmSessionKind.Subscription, _options.PublicationChannelUri,
            (uri, topics, token) => isbm.OpenSubscriptionSessionAsync(uri, topics, token), ct);

        for (var i = 0; i < _options.MaxMessagesPerPoll; i++)
        {
            var message = await ReadWithSessionRecoveryAsync(
                IsbmSessionKind.Subscription, sessionId, justOpened && i == 0,
                (id, token) => isbm.ReadPublicationAsync(id, token), ct);

            if (message is null) break;

            counters.Handled++;

            // Unreadable payload. Removing it is the only way to unblock the
            // queue, so record what it was before dropping it.
            if (message.Content is null)
            {
                var note = $"publication {message.MessageId} was not XML: {Preview(message.RawContent)}";
                logger.LogError("Discarding unreadable publication {MessageId}: {Content}",
                    message.MessageId, Preview(message.RawContent, 400));
                discarded.Add(note);
                await isbm.RemovePublicationAsync(sessionId, ct);
                continue;
            }

            try
            {
                var response = await dispatcher.DispatchAsync(new System.Xml.Linq.XDocument(message.Content), ct);

                // Publications carry the BODs that define no response. Anything
                // that produces one arrived on the wrong channel.
                if (response is not null)
                {
                    logger.LogWarning(
                        "Publication {MessageId} produced a {Bod}, which has nowhere to go. " +
                        "Request-response BODs belong on the request channel.",
                        message.MessageId, response.Root?.Name.LocalName);
                }
            }
            catch (NotSupportedException ex)
            {
                logger.LogWarning(ex, "Discarding unrecognised publication {MessageId}.", message.MessageId);
            }

            await isbm.RemovePublicationAsync(sessionId, ct);
        }

    }

    private async Task<(string SessionId, bool JustOpened)> EnsureSessionAsync(
        IsbmSessionKind kind,
        string channelUri,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<string>> open,
        CancellationToken ct)
    {
        var existing = await sessions.GetAsync(kind, channelUri, ct);
        if (existing is not null) return (existing, false);

        var sessionId = await open(channelUri, _options.EffectiveTopics, ct);
        await sessions.SaveAsync(kind, channelUri, sessionId, ct);
        return (sessionId, true);
    }

    /// <summary>
    /// Reads, retrying briefly when the session was opened moments ago.
    ///
    /// The target provider now confirms its Durable Entity is open before
    /// returning a session id, so this is defensive rather than load-bearing —
    /// but the race is inherent to any provider that acknowledges an open before
    /// committing state, and discarding the id on a transient miss would open a
    /// fresh session every tick and leak them on the broker.
    ///
    /// A session problem on a session we did NOT just open is real: the id is
    /// discarded so the next tick starts clean.
    /// </summary>
    private async Task<IsbmMessage?> ReadWithSessionRecoveryAsync(
        IsbmSessionKind kind,
        string sessionId,
        bool justOpened,
        Func<string, CancellationToken, Task<IsbmMessage?>> read,
        CancellationToken ct)
    {
        var attempts = justOpened ? 3 : 1;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return await read(sessionId, ct);
            }
            catch (IsbmException ex) when (ex.IsSessionProblem)
            {
                if (attempt < attempts)
                {
                    var delay = TimeSpan.FromSeconds(attempt);
                    logger.LogInformation(
                        "Session {SessionId} was not visible yet ({Fault}); retrying in {Delay}s.",
                        sessionId, ex.Fault ?? ex.Status.ToString(), delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    continue;
                }

                logger.LogWarning(ex, "Discarding unusable ISBM session {SessionId}.", sessionId);
                await sessions.ClearAsync(kind, ct);
                return null;
            }
        }

        return null;
    }

    private static string Truncate(string value, int max = 512) =>
        value.Length <= max ? value : value[..max] + "…";

    /// <summary>
    /// The BODID from the ApplicationArea. It is what the sender correlates on,
    /// so it belongs in every log line about a message.
    /// </summary>
    private static string BodIdOf(System.Xml.Linq.XElement bod)
    {
        var area = bod.Elements().FirstOrDefault(e => e.Name.LocalName == "ApplicationArea");
        return area?.Elements().FirstOrDefault(e => e.Name.LocalName == "BODID")?.Value ?? "(none)";
    }

    /// <summary>Enough of the payload to identify its encoding, with whitespace flattened.</summary>
    private static string Preview(string value, int max = 200)
    {
        var flat = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return Truncate(flat, max);
    }
}
