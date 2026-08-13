namespace SimHost.Domain.Common;

/// <summary>
/// The shared-key protocol used to guard the Sandbox admin surface.
///
/// The header name lives in the engine rather than beside the middleware because
/// both ends of the exchange need it and they no longer live in the same project:
/// the middleware that enforces the key is a concern of the API host, while
/// <c>SandboxResetService</c> is engine code that calls the admin endpoints over
/// HTTP and has to present the key. A constant shared by both is what stops the
/// header name being spelled twice.
/// </summary>
public static class SandboxAdminKey
{
    /// <summary>Header carrying the admin key on /admin requests.</summary>
    public const string HeaderName = "x-sandbox-admin-key";

    /// <summary>
    /// Query-string alternative, for reaching a guarded endpoint from a browser
    /// address bar where setting a header is not practical.
    /// </summary>
    public const string QueryName = "adminKey";

    /// <summary>Configuration key holding the expected value.</summary>
    public const string ConfigurationKey = "Sandbox:AdminKey";
}
