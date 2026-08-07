namespace Oiie.Isbm.Abstractions;

public enum IsbmChannelType
{
    Publication,
    Request
}

public enum IsbmSessionKind
{
    Publication,
    Subscription,
    ConsumerRequest,
    ProviderRequest
}

/// <summary>
/// Field names here mirror the ISBM 2.1 REST binding exactly. Renaming any of
/// them will break against the deployed provider:
///   channelUri   (not "uri")
///   mediaType    (not "contentType")
///   inlineContent(not "content")
/// </summary>
public sealed record IsbmChannel(
    string ChannelUri,
    IsbmChannelType ChannelType,
    string? Description);

public sealed record IsbmSession(
    string SessionId,
    IsbmSessionKind Kind,
    string ChannelUri,
    IReadOnlyCollection<string> Topics);

public sealed record IsbmMessageContent(
    string MediaType,
    string InlineContent)
{
    public static IsbmMessageContent Xml(string xml) =>
        new("application/xml", xml);
}

public sealed record IsbmMessage(
    string MessageId,
    IsbmMessageContent Content,
    IReadOnlyCollection<string> Topics,
    string? RequestMessageId,
    DateTimeOffset? Expiry,
    IReadOnlyDictionary<string, string> Properties);

public sealed class IsbmException : Exception
{
    public IsbmException(string faultCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        FaultCode = faultCode;
    }

    public string FaultCode { get; }
}
