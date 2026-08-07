using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CirProvider.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CirProvider.Infrastructure.Isbm;

/// <summary>
/// Talks to a ws-ISBM Service Provider over its REST binding.
///
/// Routes and payload shapes follow ISBM 2.x and are held in one place so they
/// can be corrected against a specific provider without touching the listener.
/// Two conventions this assumes, both worth verifying against the target:
///   - channelUri travels in the request body for session-open operations rather
///     than in the path, because a channel URI contains slashes
///   - message content is carried as an XML string with an explicit content type
/// </summary>
public sealed class IsbmRestClient(
    HttpClient http,
    IOptions<IsbmOptions> options,
    ILogger<IsbmRestClient> logger) : IIsbmClient
{
    private readonly IsbmOptions _options = options.Value;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // --- Sessions -----------------------------------------------------------

    public Task<string> OpenProviderRequestSessionAsync(
        string channelUri, IReadOnlyList<string> topics, CancellationToken ct = default) =>
        OpenSessionAsync("provider-request-sessions", channelUri, topics, includeFilters: false, ct);

    public Task<string> OpenSubscriptionSessionAsync(
        string channelUri, IReadOnlyList<string> topics, CancellationToken ct = default) =>
        // Subscription sessions declare filterExpressions and the provider's
        // content filter reads it on every publication. Omitting it leaves the
        // member null rather than empty, which is not the same thing.
        OpenSessionAsync("subscription-sessions", channelUri, topics, includeFilters: true, ct);

    /// <summary>
    /// The session-open request body. Isolated because it is the one payload whose
    /// shape is specific to the target provider rather than fixed by the ISBM
    /// specification — the channel URI contains slashes, so providers move it out
    /// of the path in their own way.
    /// </summary>
    /// <summary>
    /// Minimal session-open body: only members this client actually uses.
    ///
    /// Session-open DTOs differ per session type — expirationListenerUrl exists on
    /// subscription sessions but not on consumer-request sessions, for instance —
    /// and a provider configured with UnmappedMemberHandling.Disallow rejects
    /// anything it does not declare. Sending optional members "just in case" is
    /// therefore not free; send nothing that is not needed.
    /// </summary>
    private Dictionary<string, object?> BuildSessionOpenBody(
        string channelUri, IReadOnlyList<string> topics, bool includeFilters)
    {
        var body = new Dictionary<string, object?>
        {
            ["channelUri"] = channelUri,
            ["topics"] = topics
        };

        // An empty list means "match everything". Omitting the member entirely is
        // a different thing, and a filter engine that iterates it without a null
        // guard throws on every read.
        if (includeFilters) body["filterExpressions"] = Array.Empty<object>();

        // Polling is used, so a listener URL is only sent when one is configured.
        if (_options.ListenerUrl is { Length: > 0 }) body["listenerUrl"] = _options.ListenerUrl;
        if (_options.SecurityToken is { Length: > 0 }) body["securityToken"] = _options.SecurityToken;

        return body;
    }

    private async Task<string> OpenSessionAsync(
        string route, string channelUri, IReadOnlyList<string> topics, bool includeFilters, CancellationToken ct)
    {
        var body = BuildSessionOpenBody(channelUri, topics, includeFilters);
        var json = JsonSerializer.Serialize(body, Json);

        // Logged so a shape mismatch is diagnosable from one poll rather than by
        // guessing. Debug level: the security token would otherwise be in the log.
        logger.LogDebug("POST {Route} {Body}", route, json);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(route, content, ct);
        await ThrowIfFailed(response, $"open {route} with body {json}", ct);

        var payload = await response.Content.ReadFromJsonAsync<JsonNode>(Json, ct);

        var sessionId = Value(payload, "sessionId") ?? Value(payload, "SessionID") ?? Value(payload, "id")
            ?? throw new InvalidOperationException(
                $"The ISBM provider returned no session id for {route}: {payload}");

        logger.LogInformation("Opened ISBM session {SessionId} on {ChannelUri}.", sessionId, channelUri);
        return sessionId;
    }

    /// <summary>
    /// DELETE sessions/{id}. The one route not confirmed against the target
    /// provider, so a failure here is logged and swallowed by the caller rather
    /// than allowed to break session recovery.
    /// </summary>
    public async Task CloseSessionAsync(IsbmSessionKind kind, string sessionId, CancellationToken ct = default)
    {
        // Each session type closes through its own collection route.
        var collection = kind switch
        {
            IsbmSessionKind.ProviderRequest => "provider-request-sessions",
            IsbmSessionKind.Subscription => "subscription-sessions",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        using var response = await http.DeleteAsync($"{collection}/{Uri.EscapeDataString(sessionId)}", ct);

        // Already gone is the desired end state, not a failure.
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await ThrowIfFailed(response, "close session", ct);
    }

    // --- Requests -----------------------------------------------------------

    public Task<IsbmMessage?> ReadRequestAsync(string sessionId, CancellationToken ct = default) =>
        ReadAsync($"sessions/{Uri.EscapeDataString(sessionId)}/request", ct);

    public Task RemoveRequestAsync(string sessionId, CancellationToken ct = default) =>
        RemoveAsync($"sessions/{Uri.EscapeDataString(sessionId)}/request", ct);

    /// <summary>
    /// The request message id is in the path, matching ReadResponse. It ties the
    /// response back to its request, and is the correlation Annex A accepts in
    /// place of echoing OriginalApplicationArea.
    ///
    /// Do not also send it in the body: the provider runs with
    /// UnmappedMemberHandling.Disallow.
    /// </summary>
    public async Task PostResponseAsync(
        string sessionId, string requestMessageId, XElement content, CancellationToken ct = default)
    {
        var route = $"sessions/{Uri.EscapeDataString(sessionId)}"
                    + $"/requests/{Uri.EscapeDataString(requestMessageId)}/response";

        var body = new Dictionary<string, object?>
        {
            ["messageContent"] = new Dictionary<string, object?>
            {
                // MessageContent is { mediaType, inlineContent, payloadRef }.
                // 'mediaType' is required — a wrong name there fails deserialisation
                // before the handler runs and the provider answers 500 with no body.
                // 'inlineContent' is NOT required, so a wrong name there is worse:
                // the post succeeds and the payload is silently discarded.
                ["mediaType"] = "application/xml",
                ["inlineContent"] = content.ToString(SaveOptions.DisableFormatting)
            }
        };

        var json = JsonSerializer.Serialize(body, Json);
        logger.LogDebug("POST {Route} {Body}", route, json);

        using var payload = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(route, payload, ct);

        // Include the body on failure: the shape here is provider-specific and a
        // rejection is otherwise indistinguishable from a routing problem.
        await ThrowIfFailed(response, $"post response with body {Truncate(json)}", ct);
    }

    private static string Truncate(string value, int max = 400) =>
        value.Length <= max ? value : value[..max] + "…";

    // --- Publications -------------------------------------------------------

    public Task<IsbmMessage?> ReadPublicationAsync(string sessionId, CancellationToken ct = default) =>
        ReadAsync($"sessions/{Uri.EscapeDataString(sessionId)}/publication", ct);

    public Task RemovePublicationAsync(string sessionId, CancellationToken ct = default) =>
        RemoveAsync($"sessions/{Uri.EscapeDataString(sessionId)}/publication", ct);

    // --- Shared -------------------------------------------------------------

    private async Task<IsbmMessage?> ReadAsync(string route, CancellationToken ct)
    {
        using var response = await http.GetAsync(route, ct);

        // An empty queue is the normal case, and providers signal it differently.
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return null;

        await ThrowIfFailed(response, $"read {route}", ct);

        var raw = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var payload = JsonNode.Parse(raw);
        if (payload is null) return null;

        var messageId = Value(payload, "messageId") ?? Value(payload, "MessageID") ?? Value(payload, "id");
        if (messageId is null) return null;

        var contentNode = payload["messageContent"] ?? payload["MessageContent"];

        var payloadRef = Value(contentNode, "payloadRef");
        var contentText = Value(contentNode, "inlineContent")
                          ?? Value(contentNode, "InlineContent")
                          // Older or differently-shaped providers.
                          ?? Value(contentNode, "content")
                          ?? Value(contentNode, "Content");

        if (string.IsNullOrWhiteSpace(contentText) && !string.IsNullOrWhiteSpace(payloadRef))
        {
            // Large payloads are claim-checked out to blob storage. Dereferencing
            // that is a separate capability and is not implemented, so say so
            // plainly rather than reporting the message as malformed.
            logger.LogError(
                "ISBM message {MessageId} carries a claim-checked payload at {PayloadRef}; " +
                "dereferencing external payloads is not implemented.",
                messageId, payloadRef);
            return new IsbmMessage(messageId, null, $"payloadRef={payloadRef}", []);
        }

        contentText ??= contentNode?.ToString();

        // The media type is informational on the way in; ws-CIR BODs are XML.
        var mediaType = Value(contentNode, "mediaType") ?? Value(contentNode, "contentType");
        if (mediaType is not null && !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "ISBM message {MessageId} declares mediaType '{MediaType}'; ws-CIR BODs are XML.",
                messageId, mediaType);
        }

        if (string.IsNullOrWhiteSpace(contentText))
        {
            logger.LogWarning("ISBM message {MessageId} carried no content; skipping.", messageId);
            return null;
        }

        var element = ParseContent(messageId, contentText, logger);

        var topics = (payload["topics"] ?? payload["topic"]) is JsonArray array
            ? array.Select(t => t?.ToString() ?? string.Empty).ToList()
            : [];

        return new IsbmMessage(messageId, element, contentText, topics);
    }

    private async Task RemoveAsync(string route, CancellationToken ct)
    {
        using var response = await http.DeleteAsync(route, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await ThrowIfFailed(response, $"remove {route}", ct);
    }

    /// <summary>
    /// Message content should be XML, but providers wrap it differently: raw, JSON
    /// string-escaped, or base64. Rather than fail on the first shape that is not
    /// a bare element, try the plausible encodings and log the actual bytes if none
    /// works — "Data at the root level is invalid" alone says nothing useful.
    /// </summary>
    private static XElement? ParseContent(string messageId, string raw, ILogger logger)
    {
        var text = raw.Trim();

        // JSON string that survived into the value, quotes and escapes included.
        if (text.Length > 1 && text[0] == '"' && text[^1] == '"')
        {
            try { text = JsonSerializer.Deserialize<string>(text) ?? text; } catch { }
            text = text.Trim();
        }

        if (text.StartsWith('<'))
        {
            try { return XElement.Parse(text); }
            catch (System.Xml.XmlException ex)
            {
                logger.LogError(ex, "ISBM message {MessageId} is not well-formed XML: {Content}",
                    messageId, Truncate(text));
                return null;
            }
        }

        // Base64, which is how some providers carry an arbitrary payload.
        if (TryDecodeBase64(text, out var decoded) && decoded.TrimStart().StartsWith('<'))
        {
            try { return XElement.Parse(decoded); }
            catch (System.Xml.XmlException ex)
            {
                logger.LogError(ex, "ISBM message {MessageId} decoded from base64 but is not well-formed XML: {Content}",
                    messageId, Truncate(decoded));
                return null;
            }
        }

        logger.LogError(
            "ISBM message {MessageId} content is neither XML nor base64-encoded XML. First bytes: {Content}",
            messageId, Truncate(text, 200));
        return null;
    }

    private static bool TryDecodeBase64(string value, out string decoded)
    {
        decoded = string.Empty;
        var buffer = new byte[((value.Length * 3) + 3) / 4];
        if (!Convert.TryFromBase64String(value, buffer, out var written)) return false;

        try { decoded = Encoding.UTF8.GetString(buffer, 0, written); return true; }
        catch { return false; }
    }

    private static string? Value(JsonNode? node, string property) =>
        node?[property]?.GetValue<object>() switch
        {
            null => null,
            var v => v.ToString()
        };

    private static async Task ThrowIfFailed(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);

        // ISBM faults arrive as {"fault":"Session","message":"..."}. The fault name
        // matters more than the status: a provider may report a session problem as
        // 422, which is otherwise indistinguishable from a validation failure.
        string? fault = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try { fault = JsonNode.Parse(body)?["fault"]?.ToString(); } catch { }
        }

        throw new IsbmException(
            $"ISBM {what} failed with {(int)response.StatusCode} {response.StatusCode}: {body}",
            response.StatusCode,
            fault);
    }
}

public sealed class IsbmException(string message, HttpStatusCode status, string? fault = null)
    : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>The ISBM fault name, when the provider supplied one.</summary>
    public string? Fault { get; } = fault;

    /// <summary>
    /// The session is unusable — either the broker never had it, or it has expired.
    /// Recognised by the fault name as well as the status, because a Session fault
    /// can arrive as 422 rather than 404.
    /// </summary>
    public bool IsSessionProblem =>
        string.Equals(Fault, "Session", StringComparison.OrdinalIgnoreCase)
        || Status is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Conflict
        // A server error on a session-scoped call may mean the session's stored
        // state is no longer compatible with the provider — for instance after the
        // provider is redeployed. Retiring the id costs one extra open; keeping it
        // fails every poll indefinitely.
        || Status == HttpStatusCode.InternalServerError;
}
