namespace IsbmProvider.Models;

/// <summary>GET /configuration/supported-operations response (spec §5.9.1).</summary>
public sealed record SupportedOperations
{
    public bool IsXmlFilteringEnabled { get; init; } = true;
    public bool IsJsonFilteringEnabled { get; init; } = true;
    public IReadOnlyList<string> SupportedContentFilteringLanguages { get; init; } =
        new[] { "XPath10", "JSONPath" };
    public SupportedAuthentications SupportedAuthentications { get; init; } = new();
    public int SecurityLevelConformance { get; init; }
    public bool IsDeadLetteringEnabled { get; init; } = true;
    public bool IsChannelCreationEnabled { get; init; } = true;
    public bool IsOpenChannelSecuringEnabled { get; init; } = true;
    public bool IsWhitelistRequired { get; init; }
    public string? DefaultExpiryDuration { get; init; }
    public string? AdditionalInformationUrl { get; init; }
    /// <summary>Non-spec courtesy flag: this deployment is REST-only, SOAP unsupported.</summary>
    public string ConformanceStatement { get; init; } =
        "REST/OpenAPI 3.0.1 interface fully supported. SOAP 1.1/1.2 interface NOT supported (declared partial conformance).";
}

public sealed record SupportedAuthentications
{
    public IReadOnlyList<string> RestSupportedAuthenticationScheme { get; init; } =
        new[] { "Bearer", "Basic" };
    // soapSupportedTokenSchema intentionally empty — SOAP is out of scope.
    public IReadOnlyList<object> SoapSupportedTokenSchema { get; init; } = Array.Empty<object>();
}

/// <summary>GET /configuration/security-details response (spec §5.9.2).</summary>
public sealed record SecurityDetails
{
    public bool IsTlsEnabled { get; init; } = true;
    public bool IsSecurityTokenRequired { get; init; }
    public bool IsSecurityTokenEncryptionEnabled { get; init; } = true;
    public bool IsCertificateRequired { get; init; }
    public bool IsRbacEnabled { get; init; }
    public bool IsKeyManagementServiceEnabled { get; init; } = true;
    public bool IsEndToEndMessageEncryptionEnabled { get; init; }
}
