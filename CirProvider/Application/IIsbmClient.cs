/// <summary>
/// The ISBM message record, session kinds and fault exception are shared with the
/// rest of the OIIE solution and live in <c>Oiie.Isbm.Client</c>. They were
/// duplicated here while ws-CIR was a separate repository.
///
/// Re-exported through this namespace so application code referring to them keeps
/// compiling unchanged.
///
/// The interfaces below are deliberately NOT taken from the shared library. The
/// shared <c>IIsbmClient</c> covers the whole Messaging Service Model — channel
/// management, publishing, consumer requests — and the shared
/// <c>IIsbmSessionStore</c> adds message cursors. ws-CIR is only a request
/// provider plus a subscriber, so adopting those would force it to implement
/// routes it never calls. It keeps the narrow interfaces it actually uses.
///
/// The shared <c>IsbmSessionKind</c> carries four values rather than the two
/// ws-CIR uses. That is safe for existing <c>cir.IsbmSession</c> rows because
/// <c>SqlIsbmSessionStore</c> persists the kind by name, not by ordinal, and both
/// names it writes are present in the shared enum.
/// </summary>
global using IsbmException = Oiie.Isbm.Client.IsbmException;
global using IsbmMessage = Oiie.Isbm.Client.IsbmMessage;
global using IsbmSessionKind = Oiie.Isbm.Client.IsbmSessionKind;

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
