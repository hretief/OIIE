using System.Xml.Linq;

namespace CirProvider.Application;

public sealed class IsbmOptions
{
    /// <summary>Base URL of the ws-ISBM Service Provider, e.g. https://isbm-func-x.azurewebsites.net/api</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Function key or equivalent, sent as x-functions-key.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Channel carrying the six request-response BODs.</summary>
    public string RequestChannelUri { get; set; } = "/OIIE/CIR/Request";

    /// <summary>
    /// Channel carrying the five BODs that define no response — the four Cancel
    /// BODs and ChangeEntryCIRID. Those are publications, not requests.
    /// </summary>
    public string PublicationChannelUri { get; set; } = "/OIIE/CIR/Publication";

    /// <summary>
    /// Topics to open sessions against.
    ///
    /// Deliberately empty by default: configuration binding ADDS to an existing
    /// collection rather than replacing it, so a non-empty initialiser plus an
    /// Isbm__Topics__0 setting yields a duplicated list. <see cref="EffectiveTopics"/>
    /// supplies the fallback instead.
    /// </summary>
    public List<string> Topics { get; set; } = [];

    public const string DefaultTopic = "ws-CIR";

    /// <summary>Configured topics, de-duplicated, falling back to the default.</summary>
    public IReadOnlyList<string> EffectiveTopics =>
        Topics.Count == 0 ? [DefaultTopic] : Topics.Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// ws-ISBM channel authorization token (§2.2). Distinct from ApiKey, which is
    /// Azure platform authentication for the Function App itself.
    /// </summary>
    public string? SecurityToken { get; set; }

    /// <summary>Callback URL for providers supporting asynchronous notification. Polling is used when empty.</summary>
    public string? ListenerUrl { get; set; }

    /// <summary>Set false to keep the listener dormant, e.g. before the ISBM provider exists.</summary>
    public bool Enabled { get; set; }

    /// <summary>Messages drained per tick. Bounded so one tick cannot run past the timer.</summary>
    public int MaxMessagesPerPoll { get; set; } = 20;

    /// <summary>Also consume the publication channel. Off by default.</summary>
    public bool ConsumePublications { get; set; } = true;
}

/// <summary>
/// One message read from an ISBM channel.
///
/// <see cref="Content"/> is null when the payload could not be parsed as XML.
/// The message is still returned rather than swallowed: an unreadable message at
/// the head of a queue blocks every message behind it until something removes it,
/// and reporting the queue as empty hides that completely.
/// </summary>
public sealed record IsbmMessage(
    string MessageId,
    XElement? Content,
    string RawContent,
    IReadOnlyList<string> Topics);

/// <summary>
/// The subset of the ISA-95.00.06 Messaging Service Model this provider needs.
/// ws-CIR is a request provider: it reads requests posted by consumers and posts
/// responses back, and separately subscribes for the no-response BODs.
/// </summary>
public interface IIsbmClient
{
    Task<string> OpenProviderRequestSessionAsync(string channelUri, IReadOnlyList<string> topics, CancellationToken ct = default);

    Task<string> OpenSubscriptionSessionAsync(string channelUri, IReadOnlyList<string> topics, CancellationToken ct = default);

    /// <summary>Reads the request at the head of the queue, or null when the queue is empty.</summary>
    Task<IsbmMessage?> ReadRequestAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Posts the response. The request message id becomes the response's
    /// OriginalMessageID, which is the correlation Annex A lets us use instead of
    /// echoing OriginalApplicationArea.
    /// </summary>
    Task PostResponseAsync(string sessionId, string requestMessageId, XElement content, CancellationToken ct = default);

    /// <summary>Removes the head request, advancing the queue.</summary>
    Task RemoveRequestAsync(string sessionId, CancellationToken ct = default);

    Task<IsbmMessage?> ReadPublicationAsync(string sessionId, CancellationToken ct = default);

    Task RemovePublicationAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Closes a session. The route is per session type — there is no shared
    /// DELETE sessions/{id} — so the kind has to be supplied.
    /// </summary>
    Task CloseSessionAsync(IsbmSessionKind kind, string sessionId, CancellationToken ct = default);
}

public enum IsbmSessionKind
{
    ProviderRequest,
    Subscription
}

/// <summary>
/// Session ids must outlive a Function instance: on Consumption the host recycles
/// freely, and re-opening a session on every tick would leak sessions on the
/// broker. Persisted alongside the registry data for that reason.
/// </summary>
public interface IIsbmSessionStore
{
    Task<string?> GetAsync(IsbmSessionKind kind, string channelUri, CancellationToken ct = default);

    Task SaveAsync(IsbmSessionKind kind, string channelUri, string sessionId, CancellationToken ct = default);

    Task ClearAsync(IsbmSessionKind kind, CancellationToken ct = default);

    Task<IReadOnlyList<(IsbmSessionKind Kind, string ChannelUri, string SessionId, DateTimeOffset OpenedUtc)>>
        ListAsync(CancellationToken ct = default);
}
