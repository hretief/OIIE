using System.Security.Cryptography;
using System.Text;

namespace IsbmProvider.Infrastructure;

/// <summary>
/// Maps ISBM ChannelURIs (which contain '/', e.g. "/Enterprise/Site/Area") and SessionIDs onto
/// valid Azure Service Bus entity names. ChannelURIs are hashed to a short, stable, always-valid
/// token; SessionIDs (GUIDs) are used verbatim as subscription names (36 chars, within the 50 limit).
/// </summary>
public static class EntityNaming
{
    public static string PublicationTopic(string channelUri) => "pub-" + Hash(channelUri);
    public static string RequestQueue(string channelUri)     => "req-" + Hash(channelUri);
    public static string ResponseTopic(string channelUri)    => "resp-" + Hash(channelUri);

    /// <summary>Subscription name for a session (GUID). Valid characters, well under 50 chars.</summary>
    public static string Subscription(string sessionId) => sessionId;

    /// <summary>
    /// Subscription name for a durable subscriber, or for a session when no
    /// subscriber id was supplied.
    /// </summary>
    /// <remarks>
    /// Hashed rather than used verbatim because a subscriber id is human-chosen
    /// -- "reglocation-sites" -- and Service Bus constrains subscription names
    /// to 50 characters and a restricted alphabet. Hashing accepts any id
    /// without asking callers to know those rules, and is stable, which is the
    /// entire point: the same id must resolve to the same subscription after a
    /// restart or the backlog is abandoned.
    ///
    /// Prefixed to keep the two kinds distinguishable when reading the
    /// namespace. A bare GUID is a session-scoped subscription that will be
    /// orphaned when its process dies; a 'sub-' name is durable and expected to
    /// outlive any one instance.
    /// </remarks>
    public static string SubscriptionFor(string? subscriberId, string sessionId) =>
        string.IsNullOrWhiteSpace(subscriberId)
            ? sessionId
            : "sub-" + Hash(subscriberId);

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant(); // 16 hex chars
    }
}
