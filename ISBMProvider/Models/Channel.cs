namespace IsbmProvider.Models;

/// <summary>An ISBM Channel: primary identifier is the ChannelURI; typed Publication or Request.</summary>
public sealed record Channel
{
    public required string ChannelUri { get; init; }
    public required ChannelType ChannelType { get; init; }
    public string? Description { get; init; }
    /// <summary>Opaque token identifiers (values live encrypted in Key Vault, never here).</summary>
    public IReadOnlyList<string> SecurityTokenIds { get; init; } = Array.Empty<string>();
}
