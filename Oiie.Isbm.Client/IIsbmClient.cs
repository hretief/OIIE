using System.Net;
using System.Xml.Linq;

namespace Oiie.Isbm.Client;

/// <summary>
/// Connection settings for one ISBM client. One instance per participant: each is
/// an independent application with its own channel authorization.
/// </summary>
public sealed class IsbmClientOptions
{
    /// <summary>Base URL of the ws-ISBM Service Provider, e.g. https://isbm-func-x.azurewebsites.net/api</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Function key or equivalent, sent as x-functions-key. Azure platform auth for the Function App.</summary>
    public string? ApiKey { get; set; }

    /// <summary>ws-ISBM channel authorization token (§2.2). Distinct from ApiKey.</summary>
    public string? SecurityToken { get; set; }

    /// <summary>Callback URL for push delivery. Polling is used when empty.</summary>
    public string? ListenerUrl { get; set; }
}

/// <summary>
/// One message read from an ISBM channel.
///
/// <see cref="Content"/> is null when the payload could not be parsed as XML. The
/// message is still returned rather than swallowed: an unreadable message at the
/// head of a queue blocks every message behind it until something removes it, and
/// reporting the queue as empty hides that completely.
/// </summary>
public sealed record IsbmMessage(
    string MessageId,
    XElement? Content,
    string RawContent,
    IReadOnlyList<string> Topics);

public enum IsbmChannelType
{
    Publication,
    Request
}

public sealed record IsbmChannel(
    string ChannelUri,
    IsbmChannelType ChannelType,
    string? Description);

public enum IsbmSessionKind
{
    /// <summary>Posts publications. Producer side of publish-subscribe.</summary>
    Publication,

    /// <summary>Reads publications. Consumer side of publish-subscribe.</summary>
    Subscription,

    /// <summary>Posts requests and reads responses. Consumer side of request-response.</summary>
    ConsumerRequest,

    /// <summary>Reads requests and posts responses. Provider side of request-response.</summary>
    ProviderRequest
}

/// <summary>
/// The ISA-95.00.06 Messaging Service Model operations the OIIE ecosystem uses.
///
/// Extracted from the ws-CIR provider, where the provider-request and subscription
/// halves have been verified against a live ISBM 2.1 provider. The publication and
/// consumer-request halves are new: ws-CIR neither publishes nor issues requests,
/// so those routes are inferred from the same conventions and are marked
/// accordingly on each member. Treat an unmarked member as known-good and a marked
/// one as needing confirmation against Bruno or the provider source before being
/// relied on.
/// </summary>
public interface IIsbmClient
{
    // --- Channel management -------------------------------------------------
    //
    // UNVERIFIED: ws-CIR consumes channels someone else created, so none of these
    // routes have been exercised. They are needed by the Sandbox because a
    // simulator that resets every session must be able to create and purge its own
    // channels rather than depend on manual setup.

    Task<IsbmChannel> CreateChannelAsync(
        string channelUri,
        IsbmChannelType channelType,
        string? description = null,
        IReadOnlyList<string>? securityTokens = null,
        CancellationToken ct = default);

    /// <summary>Null when the channel does not exist, rather than throwing.</summary>
    Task<IsbmChannel?> GetChannelAsync(string channelUri, CancellationToken ct = default);

    Task<IReadOnlyList<IsbmChannel>> GetChannelsAsync(CancellationToken ct = default);

    /// <summary>Idempotent: deleting a channel that is already gone is not a failure.</summary>
    Task DeleteChannelAsync(string channelUri, CancellationToken ct = default);

    // --- Publish-subscribe: producer ---------------------------------------

    /// <summary>UNVERIFIED route. ws-CIR does not publish.</summary>
    Task<string> OpenPublicationSessionAsync(
        string channelUri, CancellationToken ct = default);

    /// <summary>UNVERIFIED route. ws-CIR does not publish.</summary>
    Task<string> PostPublicationAsync(
        string sessionId,
        XElement content,
        IReadOnlyList<string> topics,
        DateTimeOffset? expiry = null,
        CancellationToken ct = default);

    // --- Publish-subscribe: consumer ---------------------------------------

    Task<string> OpenSubscriptionSessionAsync(
        string channelUri, IReadOnlyList<string> topics, CancellationToken ct = default);

    Task<IsbmMessage?> ReadPublicationAsync(string sessionId, CancellationToken ct = default);

    Task RemovePublicationAsync(string sessionId, CancellationToken ct = default);

    // --- Request-response: consumer ----------------------------------------

    /// <summary>UNVERIFIED route. ws-CIR is a request provider, not a consumer.</summary>
    Task<string> OpenConsumerRequestSessionAsync(
        string channelUri, CancellationToken ct = default);

    /// <summary>UNVERIFIED route. Returns the request message id used to read the response.</summary>
    Task<string> PostRequestAsync(
        string sessionId,
        XElement content,
        IReadOnlyList<string> topics,
        DateTimeOffset? expiry = null,
        CancellationToken ct = default);

    /// <summary>UNVERIFIED route, though it mirrors the verified PostResponse path.</summary>
    Task<IsbmMessage?> ReadResponseAsync(
        string sessionId, string requestMessageId, CancellationToken ct = default);

    /// <summary>UNVERIFIED route.</summary>
    Task RemoveResponseAsync(
        string sessionId, string requestMessageId, CancellationToken ct = default);

    // --- Request-response: provider ----------------------------------------

    Task<string> OpenProviderRequestSessionAsync(
        string channelUri, IReadOnlyList<string> topics, CancellationToken ct = default);

    /// <summary>Reads the request at the head of the queue, or null when the queue is empty.</summary>
    Task<IsbmMessage?> ReadRequestAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Posts the response. The request message id becomes the response's
    /// OriginalMessageID, which is the correlation Annex A accepts instead of
    /// echoing OriginalApplicationArea.
    /// </summary>
    Task PostResponseAsync(
        string sessionId, string requestMessageId, XElement content, CancellationToken ct = default);

    /// <summary>Removes the head request, advancing the queue.</summary>
    Task RemoveRequestAsync(string sessionId, CancellationToken ct = default);

    // --- Lifecycle ----------------------------------------------------------

    /// <summary>
    /// Whether the provider acknowledges the session yet.
    ///
    /// UNVERIFIED route. Needed because session state is eventually consistent: a
    /// post issued immediately after opening can fail against a session the provider
    /// has not yet made visible, and for publication and consumer-request sessions
    /// there is no queue to poll as a proxy.
    /// </summary>
    Task<bool> SessionExistsAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Closes a session. The route is per session type — there is no shared
    /// DELETE sessions/{id} — so the kind has to be supplied.
    /// </summary>
    Task CloseSessionAsync(IsbmSessionKind kind, string sessionId, CancellationToken ct = default);
}

/// <summary>
/// Session ids must outlive the process. Re-opening a session on every tick leaks
/// sessions on the broker, and on a recycling host — Azure Functions Consumption,
/// or an App Service restart mid-demo — that happens constantly.
///
/// Keyed on kind plus channel because a participant may hold several sessions at
/// once: publishing on one channel while subscribing on another is the normal
/// case, not an edge case.
/// </summary>
public interface IIsbmSessionStore
{
    Task<string?> GetAsync(IsbmSessionKind kind, string channelUri, CancellationToken ct = default);

    Task SaveAsync(IsbmSessionKind kind, string channelUri, string sessionId, CancellationToken ct = default);

    Task ClearAsync(IsbmSessionKind kind, CancellationToken ct = default);

    Task<IReadOnlyList<(IsbmSessionKind Kind, string ChannelUri, string SessionId, DateTimeOffset OpenedUtc)>>
        ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Last message id handled on a session. Deduplicates across restarts, where the
    /// provider may re-present a message that was read but not removed.
    /// </summary>
    Task<string?> GetCursorAsync(string sessionId, CancellationToken ct = default);

    Task SetCursorAsync(string sessionId, string messageId, CancellationToken ct = default);
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
