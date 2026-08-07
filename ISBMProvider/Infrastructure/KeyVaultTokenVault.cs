using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;
using IsbmProvider.Abstractions;

namespace IsbmProvider.Infrastructure;

/// <summary>
/// Azure Key Vault implementation of <see cref="ITokenVault"/>.
/// Stores ISBM security tokens (serialized UsernameToken JSON) as Key Vault secrets,
/// encrypted at rest — satisfying the Level 2+ requirement that "tokens MUST be stored
/// encrypted by the ISBM Service Provider."
///
/// Naming:  isbm-{hash(channelUri)}-{hash(tokenContent)}
///   - Deterministic so ValidateAsync can check existence by name (no listing required).
///   - Channel hash groups related tokens; token hash avoids duplicates.
///
/// Tags:    channelUri stored as a tag for admin/audit lookups.
/// </summary>
public sealed class KeyVaultTokenVault : ITokenVault
{
    private readonly SecretClient _client;
    private readonly ILogger<KeyVaultTokenVault> _log;

    public KeyVaultTokenVault(SecretClient client, ILogger<KeyVaultTokenVault> log)
    {
        _client = client;
        _log = log;
    }

    public async Task<string> StoreTokenAsync(string channelUri, string rawToken, CancellationToken ct = default)
    {
        var secretName = SecretName(channelUri, rawToken);
        var secret = new KeyVaultSecret(secretName, rawToken);
        secret.Properties.Tags["channelUri"] = channelUri;
        secret.Properties.ContentType = "application/json";

        await _client.SetSecretAsync(secret, ct);
        _log.LogInformation("Token stored in Key Vault: {SecretName} for channel {Channel}", secretName, channelUri);
        return secretName;
    }

    public async Task<string?> RemoveTokenAsync(string channelUri, string tokenId, CancellationToken ct = default)
    {
        try
        {
            var nameToDelete = tokenId.StartsWith("isbm-") ? tokenId : SecretName(channelUri, tokenId);

            var operation = await _client.StartDeleteSecretAsync(nameToDelete, ct);
            await operation.WaitForCompletionAsync(ct);

            try { await _client.PurgeDeletedSecretAsync(nameToDelete, ct); }
            catch (RequestFailedException) { }

            _log.LogInformation("Token removed from Key Vault: {SecretName}", nameToDelete);
            return nameToDelete;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _log.LogWarning("Token not found in Key Vault for removal: {TokenId}", tokenId);
            return null;
        }
    }

    public async Task<bool> ValidateAsync(string channelUri, string? presentedToken, CancellationToken ct = default)
    {
        // No token presented — valid only if the channel has no tokens (open channel).
        // The caller (middleware/function) should check the channel's token list first;
        // if the channel IS secured and no token is presented, reject before reaching here.
        if (string.IsNullOrEmpty(presentedToken))
            return true;

        var secretName = SecretName(channelUri, presentedToken);
        try
        {
            var secret = await _client.GetSecretAsync(secretName, cancellationToken: ct);
            // Verify the stored value matches (belt-and-suspenders against hash collision).
            return secret.Value.Value == presentedToken;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    /// <summary>
    /// Deterministic secret name from channelUri + token content.
    /// Key Vault secret names: alphanumeric + hyphens, max 127 chars.
    /// </summary>
    private static string SecretName(string channelUri, string tokenContent)
    {
        var channelHash = HashShort(channelUri);
        var tokenHash = HashShort(tokenContent);
        return $"isbm-{channelHash}-{tokenHash}";
    }

    private static string HashShort(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 10).ToLowerInvariant(); // 20 hex chars
    }
}
