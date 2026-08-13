using SimHost.Domain.Common;

namespace Oiie.Sandbox.Api.Middleware;

/// <summary>
/// Requires a shared key on /admin routes.
///
/// These endpoints reset databases, delete channels and close other systems'
/// sessions. On a workstation that is fine; on a public URL it is an unauthenticated
/// destructive API, and the Sandbox is deployed precisely so other people can reach
/// it.
///
/// A shared key rather than Entra: this guards a simulator against accidents and
/// casual discovery, not a production system against attackers. Entra sign-in is the
/// right answer for the UI, and is a separate piece of work.
///
/// When no key is configured the gate is open, which keeps local development
/// frictionless — and the health endpoint reports the state so a deployed instance
/// running unprotected is visible rather than assumed.
/// </summary>
public sealed class AdminKeyMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<AdminKeyMiddleware> logger)
{
    /// <summary>
    /// Re-exposed from <see cref="SandboxAdminKey"/> so existing call sites keep
    /// working; the constant itself belongs to the engine, which also has to send it.
    /// </summary>
    public const string HeaderName = SandboxAdminKey.HeaderName;

    private readonly string? _key = configuration[SandboxAdminKey.ConfigurationKey];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (!path.StartsWithSegments("/admin") || string.IsNullOrWhiteSpace(_key))
        {
            await next(context);
            return;
        }

        var supplied = context.Request.Headers[HeaderName].FirstOrDefault()
            ?? context.Request.Query[SandboxAdminKey.QueryName].FirstOrDefault();

        // Fixed-time comparison. Overkill for a simulator, but a string comparison
        // that short-circuits is a habit worth not forming.
        var ok = supplied is not null
                 && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                     System.Text.Encoding.UTF8.GetBytes(supplied),
                     System.Text.Encoding.UTF8.GetBytes(_key));

        if (!ok)
        {
            logger.LogWarning(
                "Rejected {Method} {Path} from {Ip}: missing or wrong admin key",
                context.Request.Method, path, context.Connection.RemoteIpAddress);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Admin endpoints require a key.",
                header = HeaderName,
                hint = "Sandbox:AdminKey is set on this instance. Pass it as a header, or as " +
                       "?adminKey= for convenience in a browser."
            });
            return;
        }

        await next(context);
    }
}
