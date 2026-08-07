namespace IsbmProvider.Models;

/// <summary>
/// Message body. ISBM allows XML (CCOM BOD) or JSON content. Large bodies are claim-checked
/// to Blob; <see cref="PayloadRef"/> holds the reference while <see cref="InlineContent"/> is null.
/// </summary>
public sealed record MessageContent
{
    /// <summary>e.g. "application/xml" for CCOM BODs, "application/json".</summary>
    public required string MediaType { get; init; }
    /// <summary>Inline body when small enough to carry on the broker message; else null.</summary>
    public string? InlineContent { get; init; }
    /// <summary>Claim-check reference (Blob path/SAS) when the body was offloaded.</summary>
    public string? PayloadRef { get; init; }
}

/// <summary>A publication/request/response message as seen on the wire (REST "Message" schema).</summary>
public sealed record IsbmMessage
{
    public string? MessageId { get; init; }
    public required MessageContent MessageContent { get; init; }
    /// <summary>1..* for publications; exactly one for requests.</summary>
    public IReadOnlyList<string> Topics { get; init; } = Array.Empty<string>();
    /// <summary>xs:duration, e.g. "P7D". Null = provider default expiry.</summary>
    public string? Expiry { get; init; }
    /// <summary>Set when this message was forwarded from another channel (traceability chain).</summary>
    public string? OriginalMessageId { get; init; }
}

/// <summary>Body of PostResponse — carries the RequestMessageID it answers.</summary>
public sealed record ResponsePost
{
    public required string RequestMessageId { get; init; }
    public required MessageContent MessageContent { get; init; }
}
