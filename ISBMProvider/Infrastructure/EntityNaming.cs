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

    private static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant(); // 16 hex chars
    }
}
