using SimHost.Application.Participants;

namespace SimHost.Application;

/// <summary>
/// Whether the optional dependencies are wired on this deployment.
///
/// Both the composition root and the health endpoints need these answers, and they
/// have to agree: a health endpoint reporting ISBM as configured while the pumps
/// declined to start is worse than no health endpoint. Defining each predicate once
/// is what makes that class of disagreement impossible rather than merely unlikely.
/// </summary>
public static class SandboxCapabilities
{
    /// <summary>
    /// True when a blob service URI is configured and is not a deployment placeholder.
    /// When false the sandbox still messages, storing the archive row without the payload.
    /// </summary>
    public static bool IsStorageConfigured(IConfiguration configuration)
    {
        var storageUri = configuration["Storage:BlobServiceUri"];

        return !string.IsNullOrWhiteSpace(storageUri)
               && !storageUri.Contains("REPLACE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when at least one personality declares an ISBM base URL. ISBM is wired
    /// per-participant, so one declaration is enough to need a client and the pumps.
    /// </summary>
    public static bool IsIsbmConfigured(IEnumerable<PersonalityConfig> personalities) =>
        personalities.Any(p => !string.IsNullOrWhiteSpace(p.Isbm.BaseUrl));

    /// <inheritdoc cref="IsIsbmConfigured(IEnumerable{PersonalityConfig})"/>
    public static bool IsIsbmConfigured(ParticipantRegistry registry) =>
        IsIsbmConfigured(registry.All.Select(p => p.Config));
}
