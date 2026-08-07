using System.Text;
using IsbmProvider.Abstractions;
using IsbmProvider.Models;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace IsbmProvider.Http;

/// <summary>
/// Validates that the caller presents a valid security token for secured channels.
/// Called explicitly by functions that operate on channels.
///
/// Token extraction:
///   Authorization: Basic base64(username:password)  → serialized as UsernameToken JSON
///   Authorization: Bearer {token}                   → raw token string
///
/// If the channel has no tokens (open/unsecured), validation always passes.
/// If the channel has tokens and no Authorization header is present → SecurityTokenFault (401).
/// </summary>
public sealed class TokenValidator
{
    private readonly IChannelStore _channels;
    private readonly ITokenVault _tokens;
    private readonly ILogger<TokenValidator> _log;

    public TokenValidator(IChannelStore channels, ITokenVault tokens, ILogger<TokenValidator> log)
    {
        _channels = channels;
        _tokens = tokens;
        _log = log;
    }

    /// <summary>
    /// Validates the caller's token for the given channel.
    /// Returns null if valid; returns an IsbmFaultException if invalid (caller should throw or return it).
    /// </summary>
    public async Task<IsbmFaultException?> ValidateAsync(HttpRequestData req, string channelUri, CancellationToken ct = default)
    {
        var channel = await _channels.GetAsync(channelUri, ct);
        if (channel is null)
            return IsbmFaultException.Channel();

        // Open channel — no tokens required
        if (channel.SecurityTokenIds.Count == 0)
            return null;

        // Secured channel — extract and validate the presented token
        var presentedToken = ExtractToken(req);
        if (presentedToken is null)
        {
            _log.LogWarning("Secured channel {Uri}: no Authorization header presented", channelUri);
            return IsbmFaultException.SecurityToken("Channel is secured. Provide an Authorization header.");
        }

        var valid = await _tokens.ValidateAsync(channelUri, presentedToken, ct);
        if (!valid)
        {
            _log.LogWarning("Secured channel {Uri}: invalid token presented", channelUri);
            return IsbmFaultException.SecurityToken("Invalid security token.");
        }

        return null; // valid
    }

    private static string? ExtractToken(HttpRequestData req)
    {
        if (!req.Headers.TryGetValues("Authorization", out var values))
            return null;

        var header = values.FirstOrDefault();
        if (string.IsNullOrEmpty(header))
            return null;

        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            // Decode base64 → username:password → serialize as UsernameToken JSON
            // to match the format stored in Key Vault
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
                var parts = decoded.Split(':', 2);
                if (parts.Length == 2)
                {
                    // Serialize to match what AddSecurityToken stores
                    return System.Text.Json.JsonSerializer.Serialize(
                        new { Username = parts[0], Password = parts[1] });
                }
            }
            catch { /* fall through to return null */ }
        }
        else if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header[7..];
        }

        return null;
    }
}
