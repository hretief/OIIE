using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Oiie.Isbm.Client;

/// <summary>
/// Talks to a ws-ISBM Service Provider over its REST binding.
///
/// Extracted from the ws-CIR provider rather than rewritten. The provider-request
/// and subscription paths here are the ones verified against a live ISBM 2.1
/// provider, and several of their details are not what the specification text
/// suggests:
///
///   - session-open carries channelUri in the request BODY, not the path. The
///     specification shows /channels/{channel-uri}/publication-sessions, but a
///     channel URI contains slashes.
///   - filterExpressions must be present on subscription sessions. Omitting it
///     leaves the member null rather than empty, and the provider's filter engine
///     iterates it without a null guard.
///   - the response route is sessions/{id}/requests/{requestMessageId}/response,
///     not sessions/{id}/response.
///   - message content members are mediaType and inlineContent. A wrong name on
///     mediaType fails deserialisation loudly; a wrong name on inlineContent
///     succeeds and silently discards the payload, which is far worse.
///   - the provider runs with UnmappedMemberHandling.Disallow, so sending an
///     optional member "just in case" is rejected outright.
///
/// The publication and consumer-request paths are new — ws-CIR neither publishes
/// nor issues requests — and follow the same conventions by inference. They are
/// marked UNVERIFIED on the interface.
/// </summary>
public sealed class IsbmRestClient(
    HttpClient http,
    IsbmClientOptions options,
    ILogger<IsbmRestClient> logger) : IIsbmClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Raw body of the most recent session-open, for diagnostics. Not thread-safe and
    /// not meant to be: it exists so a probe can report what the provider actually
    /// said, rather than what we parsed out of it.
    /// </summary>
    public string? LastSessionOpenResponse { get; private set; }

    // --- Channels -----------------------------------------------------------

    /// <summary>
    /// A channel URI contains slashes, so it cannot sit in a path segment without
    /// encoding. Session-open solved that by moving it to the body, and the same
    /// convention is assumed here. If the provider rejects this shape, the logged
    /// body makes the correction a one-line change.
    /// </summary>
    public async Task<IsbmChannel> CreateChannelAsync(
        string channelUri,
        IsbmChannelType channelType,
        string? description = null,
        IReadOnlyList<string>? securityTokens = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            // 'channelUri', not 'uri'. The same member name session-open uses — the
            // provider's DTOs are consistent with each other even where the
            // specification text is not.
            ["channelUri"] = channelUri,
            ["channelType"] = channelType.ToString()
        };

        if (!string.IsNullOrWhiteSpace(description)) body["description"] = description;
        if (securityTokens is { Count: > 0 }) body["securityTokens"] = securityTokens;

        var json = JsonSerializer.Serialize(body, Json);
        logger.LogDebug("POST channels {Body}", json);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("channels", content, ct);

        // Already present is the desired end state, not a failure: channel creation
        // runs on every start so a reset does not require manual setup.
        //
        // This provider reports a duplicate as 422 with fault "Operation" rather than
        // 409, so the status alone is not enough to recognise it — a 422 is otherwise
        // a genuine validation failure and must still throw.
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            var conflictBody = await response.Content.ReadAsStringAsync(ct);

            if (conflictBody.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Channel {ChannelUri} already exists.", channelUri);
                return new IsbmChannel(channelUri, channelType, description);
            }

            throw new IsbmException(
                $"ISBM create channel failed with {(int)response.StatusCode}: {conflictBody}",
                response.StatusCode,
                TryReadFault(conflictBody));
        }

        await ThrowIfFailed(response, $"create channel with body {Truncate(json)}", ct);

        logger.LogInformation("Created {ChannelType} channel {ChannelUri}.", channelType, channelUri);
        return new IsbmChannel(channelUri, channelType, description);
    }

    public async Task<IsbmChannel?> GetChannelAsync(string channelUri, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"channels/{Uri.EscapeDataString(channelUri)}", ct);

        if (response.StatusCode is HttpStatusCode.NotFound) return null;
        await ThrowIfFailed(response, $"get channel {channelUri}", ct);

        var payload = await response.Content.ReadFromJsonAsync<JsonNode>(Json, ct);
        return ToChannel(payload);
    }

    public async Task<IReadOnlyList<IsbmChannel>> GetChannelsAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("channels", ct);
        await ThrowIfFailed(response, "get channels", ct);

        var payload = await response.Content.ReadFromJsonAsync<JsonNode>(Json, ct);

        var array = payload as JsonArray ?? payload?["channels"] as JsonArray;
        if (array is null) return [];

        return array.Select(ToChannel).OfType<IsbmChannel>().ToList();
    }

    public async Task DeleteChannelAsync(string channelUri, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"channels/{Uri.EscapeDataString(channelUri)}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await ThrowIfFailed(response, $"delete channel {channelUri}", ct);

        logger.LogInformation("Deleted channel {ChannelUri}.", channelUri);
    }

    private static IsbmChannel? ToChannel(JsonNode? node)
    {
        var uri = Value(node, "channelUri") ?? Value(node, "uri") ?? Value(node, "URI");
        if (uri is null) return null;

        var typeText = Value(node, "channelType") ?? Value(node, "type");
        var type = Enum.TryParse<IsbmChannelType>(typeText, ignoreCase: true, out var parsed)
            ? parsed
            : IsbmChannelType.Publication;

        return new IsbmChannel(uri, type, Value(node, "description"));
    }

    // --- Session collections ------------------------------------------------

    private static string CollectionFor(IsbmSessionKind kind) => kind switch
    {
        IsbmSessionKind.Publication => "publication-sessions",
        IsbmSessionKind.Subscription => "subscription-sessions",
        IsbmSessionKind.ConsumerRequest => "consumer-request-sessions",
        IsbmSessionKind.ProviderRequest => "provider-request-sessions",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public Task<string> OpenPublicationSessionAsync(string channelUri, CancellationToken ct = default) =>
        OpenSessionAsync(IsbmSessionKind.Publication, channelUri, [], includeFilters: false, ct);

    public Task<string> OpenSubscriptionSessionAsync(
        string channelUri, IReadOnlyList<string> topics, CancellationToken ct = default) =>
        // Subscription sessions declare filterExpressions and the provider's content
        // filter reads it on every publication. Omitting it leaves the member null
        // rather than empty, which is not the same thing.
        OpenSessionAsync(IsbmSessionKind.Subscription, channelUri, topics, includeFilters: true, ct);

    public Task<string> OpenConsumerRequestSessionAsync(string channelUri, CancellationToken ct = default) =>
        OpenSessionAsync(IsbmSessionKind.ConsumerRequest, channelUri, [], includeFilters: false, ct);

    public Task<string> OpenProviderRequestSessionAsync(
        string channelUri, IReadOnlyList<string> topics, CancellationToken ct = default) =>
        OpenSessionAsync(IsbmSessionKind.ProviderRequest, channelUri, topics, includeFilters: false, ct);

    /// <summary>
    /// Minimal session-open body: only members this client actually uses.
    ///
    /// Session-open DTOs differ per session type — expirationListenerUrl exists on
    /// subscription sessions but not on consumer-request sessions, for instance —
    /// and a provider configured with UnmappedMemberHandling.Disallow rejects
    /// anything it does not declare. Sending optional members "just in case" is
    /// therefore not free.
    /// </summary>
    private Dictionary<string, object?> BuildSessionOpenBody(
        IsbmSessionKind kind, string channelUri, IReadOnlyList<string> topics, bool includeFilters)
    {
        var body = new Dictionary<string, object?>
        {
            ["channelUri"] = channelUri
        };

        // Publication and consumer-request sessions do not subscribe to anything, so
        // a topics member on those is an unmapped member rather than an empty list.
        if (kind is IsbmSessionKind.Subscription or IsbmSessionKind.ProviderRequest)
        {
            body["topics"] = topics;
        }

        if (includeFilters)
        {
            body["filterExpressions"] = Array.Empty<object>();
        }

        if (options.ListenerUrl is { Length: > 0 }) body["listenerUrl"] = options.ListenerUrl;
        if (options.SecurityToken is { Length: > 0 }) body["securityToken"] = options.SecurityToken;

        return body;
    }

    private async Task<string> OpenSessionAsync(
        IsbmSessionKind kind,
        string channelUri,
        IReadOnlyList<string> topics,
        bool includeFilters,
        CancellationToken ct)
    {
        var route = CollectionFor(kind);
        var body = BuildSessionOpenBody(kind, channelUri, topics, includeFilters);
        var json = JsonSerializer.Serialize(body, Json);

        // Logged so a shape mismatch is diagnosable from one call rather than by
        // guessing. Debug level: the security token would otherwise be in the log.
        logger.LogDebug("POST {Route} {Body}", route, json);

        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(route, content, ct);
        await ThrowIfFailed(response, $"open {route} with body {json}", ct);

        var raw = await response.Content.ReadAsStringAsync(ct);
        LastSessionOpenResponse = $"{route} -> {(int)response.StatusCode} {Truncate(raw)}";

        var payload = string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw);

        var sessionId = Value(payload, "sessionId") ?? Value(payload, "SessionID") ?? Value(payload, "id")
            ?? throw new InvalidOperationException(
                $"The ISBM provider returned no session id for {route}: {Truncate(raw)}");

        // Logged with the raw body: if a session is reported missing on the very next
        // call, the question is whether the id being used is actually the session id,
        // and only the unparsed response answers that.
        logger.LogInformation(
            "Opened {Kind} session {SessionId} on {ChannelUri}. Provider returned: {Raw}",
            kind, sessionId, channelUri, Truncate(raw));

        return sessionId;
    }

    public async Task<bool> SessionExistsAsync(string sessionId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"sessions/{Uri.EscapeDataString(sessionId)}", ct);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return false;

        // A route that does not exist is not the same as a session that does not
        // exist, and treating it as such would make every confirmation loop spin
        // until it times out. Report unknown as present and let the caller proceed.
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogDebug(
                "Session probe for {SessionId} returned {Status}: {Body}",
                sessionId, response.StatusCode, body);
            return true;
        }

        return true;
    }

    public async Task CloseSessionAsync(
        IsbmSessionKind kind, string sessionId, CancellationToken ct = default)
    {
        // Each session type closes through its own collection route.
        var route = $"{CollectionFor(kind)}/{Uri.EscapeDataString(sessionId)}";

        using var response = await http.DeleteAsync(route, ct);

        // Already gone is the desired end state, not a failure.
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await ThrowIfFailed(response, "close session", ct);
    }

    // --- Publications -------------------------------------------------------

    public async Task<string> PostPublicationAsync(
        string sessionId,
        XElement content,
        IReadOnlyList<string> topics,
        DateTimeOffset? expiry = null,
        CancellationToken ct = default)
    {
        // Plural: posting adds to a collection, whereas reading and removing act on
        // the head of the queue and use the singular. The verified PostResponse
        // route shows the same split — sessions/{id}/requests/{id}/response.
        var route = $"sessions/{Uri.EscapeDataString(sessionId)}/publications";

        var body = new Dictionary<string, object?>
        {
            ["messageContent"] = MessageContent(content),
            ["topics"] = topics
        };

        // ISO-8601 duration, not a timestamp: ISBM expresses expiry as a lifetime.
        if (expiry is { } value)
        {
            body["expiry"] = System.Xml.XmlConvert.ToString(value - DateTimeOffset.UtcNow);
        }

        return await PostForMessageIdAsync(route, body, ct);
    }

    public Task<IsbmMessage?> ReadPublicationAsync(string sessionId, CancellationToken ct = default) =>
        ReadAsync($"sessions/{Uri.EscapeDataString(sessionId)}/publication", ct);

    public Task RemovePublicationAsync(string sessionId, CancellationToken ct = default) =>
        RemoveAsync($"sessions/{Uri.EscapeDataString(sessionId)}/publication", ct);

    // --- Requests -----------------------------------------------------------

    public async Task<string> PostRequestAsync(
        string sessionId,
        XElement content,
        IReadOnlyList<string> topics,
        DateTimeOffset? expiry = null,
        CancellationToken ct = default)
    {
        var route = $"sessions/{Uri.EscapeDataString(sessionId)}/requests";

        var body = new Dictionary<string, object?>
        {
            ["messageContent"] = MessageContent(content),
            ["topics"] = topics
        };

        if (expiry is { } value)
        {
            body["expiry"] = System.Xml.XmlConvert.ToString(value - DateTimeOffset.UtcNow);
        }

        return await PostForMessageIdAsync(route, body, ct);
    }

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
        var route = ResponseRoute(sessionId, requestMessageId);

        var body = new Dictionary<string, object?>
        {
            ["messageContent"] = MessageContent(content)
        };

        var json = JsonSerializer.Serialize(body, Json);
        logger.LogDebug("POST {Route} {Body}", route, json);

        using var payload = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(route, payload, ct);

        // Include the body on failure: the shape here is provider-specific and a
        // rejection is otherwise indistinguishable from a routing problem.
        await ThrowIfFailed(response, $"post response with body {Truncate(json)}", ct);
    }

    public Task<IsbmMessage?> ReadResponseAsync(
        string sessionId, string requestMessageId, CancellationToken ct = default) =>
        ReadAsync(ResponseRoute(sessionId, requestMessageId), ct);

    public Task RemoveResponseAsync(
        string sessionId, string requestMessageId, CancellationToken ct = default) =>
        RemoveAsync(ResponseRoute(sessionId, requestMessageId), ct);

    private static string ResponseRoute(string sessionId, string requestMessageId) =>
        $"sessions/{Uri.EscapeDataString(sessionId)}"
        + $"/requests/{Uri.EscapeDataString(requestMessageId)}/response";

    // --- Shared -------------------------------------------------------------

    /// <summary>
    /// MessageContent is { mediaType, inlineContent, payloadRef }. 'mediaType' is
    /// required — a wrong name there fails deserialisation before the handler runs
    /// and the provider answers 500 with no body. 'inlineContent' is NOT required,
    /// so a wrong name there is worse: the post succeeds and the payload is
    /// silently discarded.
    /// </summary>
    private static Dictionary<string, object?> MessageContent(XElement content) => new()
    {
        ["mediaType"] = "application/xml",
        ["inlineContent"] = content.ToString(SaveOptions.DisableFormatting)
    };

    private async Task<string> PostForMessageIdAsync(
        string route, Dictionary<string, object?> body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, Json);
        logger.LogDebug("POST {Route} {Body}", route, json);

        using var payload = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(route, payload, ct);
        await ThrowIfFailed(response, $"post {route} with body {Truncate(json)}", ct);

        var raw = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Some providers answer 201 with the id in Location and no body.
            var location = response.Headers.Location?.ToString();
            return location?.Split('/').LastOrDefault()
                   ?? throw new InvalidOperationException(
                       $"The ISBM provider returned no message id for {route}.");
        }

        var node = JsonNode.Parse(raw);
        return Value(node, "messageId") ?? Value(node, "MessageID") ?? Value(node, "id")
            ?? throw new InvalidOperationException(
                $"The ISBM provider returned no message id for {route}: {Truncate(raw)}");
    }

    private async Task<IsbmMessage?> ReadAsync(string route, CancellationToken ct)
    {
        using var response = await http.GetAsync(route, ct);

        if (response.StatusCode == HttpStatusCode.NoContent) return null;

        var raw = await response.Content.ReadAsStringAsync(ct);

        // A 404 means either an empty queue or a session the provider has forgotten,
        // and the two are only distinguishable by the body: a dead session carries
        // {"fault":"Session"}, an empty queue carries nothing.
        //
        // Conflating them is silent and permanent. A caller polling a dead session
        // sees "no messages" on every read, never throws, and so never triggers the
        // recovery that would re-open it — the consumer looks healthy and idle while
        // messages pile up on the channel.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var fault = TryReadFault(raw);

            if (fault is null && string.IsNullOrWhiteSpace(raw)) return null;

            if (fault is null) return null;

            throw new IsbmException(
                $"ISBM read {route} failed with 404 and fault '{fault}': {raw}",
                response.StatusCode,
                fault);
        }

        await ThrowIfFailed(response, $"read {route}", ct);

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

        var mediaType = Value(contentNode, "mediaType") ?? Value(contentNode, "contentType");
        if (mediaType is not null && !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "ISBM message {MessageId} declares mediaType '{MediaType}'; OIIE BODs are XML.",
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
    /// string-escaped, or base64. Rather than fail on the first shape that is not a
    /// bare element, try the plausible encodings and log the actual bytes if none
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
                logger.LogError(ex,
                    "ISBM message {MessageId} decoded from base64 but is not well-formed XML: {Content}",
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

    private static string Truncate(string value, int max = 400) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string? TryReadFault(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonNode.Parse(body)?["fault"]?.ToString(); } catch { return null; }
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

        // A 404 carrying no body is the framework saying no route matched, not the
        // provider raising a ChannelFault or SessionFault. The two look identical in
        // a status code and have nothing in common as problems, so say which it is.
        if (response.StatusCode == HttpStatusCode.NotFound && string.IsNullOrWhiteSpace(body))
        {
            throw new IsbmException(
                $"ISBM {what} returned 404 with no fault body, which means the route does not " +
                "exist on this provider rather than that the channel or session is missing.",
                response.StatusCode,
                fault: null);
        }

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
