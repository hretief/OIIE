namespace Oiie.Isbm.Abstractions;

/// <summary>
/// Contract the Sandbox depends on. The concrete implementation is the
/// IsbmRestClient extracted from the ws-CIR provider (spec §4) — no new HTTP
/// code is written here, because the wire shapes are already known-good there.
///
/// Implement this by adapting the extracted client; do not reimplement.
/// </summary>
public interface IIsbmClient
{
    // --- Channel management -------------------------------------------------

    Task<IsbmChannel> CreateChannelAsync(
        string channelUri,
        IsbmChannelType channelType,
        string? description = null,
        CancellationToken ct = default);

    Task<IsbmChannel?> GetChannelAsync(string channelUri, CancellationToken ct = default);

    Task<IReadOnlyList<IsbmChannel>> GetChannelsAsync(CancellationToken ct = default);

    Task DeleteChannelAsync(string channelUri, CancellationToken ct = default);

    // --- Provider publication -----------------------------------------------

    Task<IsbmSession> OpenPublicationSessionAsync(
        string channelUri,
        CancellationToken ct = default);

    Task<string> PostPublicationAsync(
        string sessionId,
        IsbmMessageContent content,
        IReadOnlyCollection<string> topics,
        DateTimeOffset? expiry = null,
        CancellationToken ct = default);

    // --- Consumer publication -----------------------------------------------

    Task<IsbmSession> OpenSubscriptionSessionAsync(
        string channelUri,
        IReadOnlyCollection<string> topics,
        string? listenerUri = null,
        CancellationToken ct = default);

    Task<IsbmMessage?> ReadPublicationAsync(string sessionId, CancellationToken ct = default);

    Task RemovePublicationAsync(string sessionId, CancellationToken ct = default);

    // --- Consumer request ---------------------------------------------------

    Task<IsbmSession> OpenConsumerRequestSessionAsync(
        string channelUri,
        string? listenerUri = null,
        CancellationToken ct = default);

    Task<string> PostRequestAsync(
        string sessionId,
        IsbmMessageContent content,
        IReadOnlyCollection<string> topics,
        DateTimeOffset? expiry = null,
        CancellationToken ct = default);

    Task<IsbmMessage?> ReadResponseAsync(
        string sessionId,
        string requestMessageId,
        CancellationToken ct = default);

    Task RemoveResponseAsync(
        string sessionId,
        string requestMessageId,
        CancellationToken ct = default);

    // --- Provider request ---------------------------------------------------

    Task<IsbmSession> OpenProviderRequestSessionAsync(
        string channelUri,
        IReadOnlyCollection<string> topics,
        string? listenerUri = null,
        CancellationToken ct = default);

    Task<IsbmMessage?> ReadRequestAsync(string sessionId, CancellationToken ct = default);

    Task RemoveRequestAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// NOTE: posts to sessions/{sessionId}/requests/{requestMessageId}/response —
    /// not sessions/{sessionId}/response.
    /// </summary>
    Task<string> PostResponseAsync(
        string sessionId,
        string requestMessageId,
        IsbmMessageContent content,
        CancellationToken ct = default);

    // --- Session lifecycle --------------------------------------------------

    /// <summary>
    /// Opens a session and confirms it is readable before returning. Sessions are
    /// not immediately usable after open because of Durable Entity eventual
    /// consistency — see SessionHelper.OpenAndConfirmAsync in the CIR provider.
    /// </summary>
    Task<IsbmSession> OpenAndConfirmAsync(
        Func<CancellationToken, Task<IsbmSession>> open,
        CancellationToken ct = default);

    Task CloseSessionAsync(string sessionId, CancellationToken ct = default);
}
