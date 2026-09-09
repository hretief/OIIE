using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oiie.Isbm.Client;

namespace RdlEngine.Application;

public sealed record RdlDrainReport
{
    public int RequestsHandled { get; init; }
    public int ResponsesPosted { get; init; }
    public int Skipped { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>
    /// Messages removed without being answered, with a preview. Surfaced in the
    /// report rather than only in logs: an unreadable message is a
    /// configuration problem between two systems, and whoever ran the drain is
    /// the person who can act on it.
    /// </summary>
    public IReadOnlyList<string> Discarded { get; init; } = [];

    public bool Idle => RequestsHandled == 0;
}

/// <summary>
/// Reads GetTaxonomySet requests from ISBM and posts ShowTaxonomySet back.
///
/// A provider-request session only — RDL answers questions and publishes
/// nothing, so there is no subscription half here as there is in ws-CIR.
///
/// Modelled on CirProvider's IsbmBodListener, including its retention rules: a
/// message is removed once answered or once found unanswerable, but left in
/// place when posting the response failed, so the next drain retries it.
/// </summary>
public sealed class TaxonomySetRequestListener(
    IIsbmClient isbm,
    TaxonomySetResponder responder,
    IOptions<RdlIsbmOptions> options,
    ILogger<TaxonomySetRequestListener> logger)
{
    private readonly RdlIsbmOptions _options = options.Value;

    /// <summary>
    /// Session id held across drains. Re-opening on every tick leaks sessions
    /// on the broker and discards its record of what has already been read.
    ///
    /// In memory rather than persisted, unlike ws-CIR's SQL-backed store: this
    /// engine has no database, and a lost session on a host recycle costs one
    /// re-open rather than a correctness problem, because an unanswered request
    /// stays queued.
    /// </summary>
    private string? _sessionId;

    public async Task<RdlDrainReport> DrainAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            logger.LogDebug("ISBM listener is disabled.");
            return new RdlDrainReport();
        }

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return new RdlDrainReport { Errors = ["Isbm__BaseUrl is not configured."] };
        }

        var errors = new List<string>();
        var discarded = new List<string>();
        int handled = 0, responded = 0, skipped = 0;

        try
        {
            var sessionId = await EnsureSessionAsync(ct);

            for (var i = 0; i < _options.MaxMessagesPerPoll; i++)
            {
                var message = await isbm.ReadRequestAsync(sessionId, ct);
                if (message is null) break;

                handled++;

                if (message.Content is null)
                {
                    var note = $"request {message.MessageId} was not XML: {Preview(message.RawContent)}";
                    logger.LogError("Discarding unreadable request {MessageId}: {Content}",
                        message.MessageId, Preview(message.RawContent, 400));
                    discarded.Add(note);
                    skipped++;
                    await isbm.RemoveRequestAsync(sessionId, ct);
                    continue;
                }

                var bodName = message.Content.Name.LocalName;

                try
                {
                    var response = await responder.RespondAsync(
                        new XDocument(message.Content), ct);

                    if (response is null)
                    {
                        // Not a GetTaxonomySet. Someone else's message, or a
                        // BOD this engine has no opinion on. Removing it would
                        // destroy a message another participant may be waiting
                        // on, so it is left alone and the drain stops rather
                        // than spinning on the same head.
                        var note = $"{bodName} (message {message.MessageId}) is not a GetTaxonomySet; left queued.";
                        logger.LogWarning(
                            "Message {MessageId} is a {Bod}, which this engine does not answer; leaving it queued.",
                            message.MessageId, bodName);
                        discarded.Add(note);
                        skipped++;
                        break;
                    }

                    await isbm.PostResponseAsync(sessionId, message.MessageId, response.Root!, ct);
                    responded++;

                    logger.LogInformation(
                        "Answered {Bod} (message {MessageId}) with {Response}.",
                        bodName, message.MessageId, response.Root?.Name.LocalName);
                }
                catch (Exception ex)
                {
                    // The response could not be posted — a transport failure,
                    // not a bad document. Leave the message so the next drain
                    // retries it.
                    var note = $"{bodName} (message {message.MessageId}) failed: {ex.Message}";
                    logger.LogError(ex,
                        "Could not complete {Bod} (message {MessageId}); leaving it queued for retry.",
                        bodName, message.MessageId);
                    errors.Add(note);
                    break;
                }

                await isbm.RemoveRequestAsync(sessionId, ct);
            }
        }
        catch (Exception ex)
        {
            // A failure part-way through must still report what already
            // happened, or the drain claims nothing was handled while the
            // broker plainly shows otherwise.
            logger.LogError(ex, "Draining the RDL request channel failed.");
            errors.Add($"requests: {ex.Message}");

            // The session may be the thing that broke; drop it so the next
            // drain opens a fresh one.
            _sessionId = null;
        }

        return new RdlDrainReport
        {
            RequestsHandled = handled,
            ResponsesPosted = responded,
            Skipped = skipped,
            Errors = errors,
            Discarded = discarded
        };
    }

    private async Task<string> EnsureSessionAsync(CancellationToken ct)
    {
        if (_sessionId is { Length: > 0 })
        {
            return _sessionId;
        }

        _sessionId = await isbm.OpenProviderRequestSessionAsync(
            _options.RequestChannelUri, _options.EffectiveTopics, ct);

        logger.LogInformation(
            "Opened provider request session {SessionId} on {Channel}.",
            _sessionId, _options.RequestChannelUri);

        return _sessionId;
    }

    private static string Preview(string? raw, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "(empty)";

        var flattened = raw.Replace('\r', ' ').Replace('\n', ' ').Trim();

        return flattened.Length <= max ? flattened : flattened[..max] + "…";
    }
}
