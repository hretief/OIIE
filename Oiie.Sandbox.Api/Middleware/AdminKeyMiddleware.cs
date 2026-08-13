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
///
/// A small set of read-only routes is exempt so the Workflow Orchestration app,
/// which is served from this host and runs in a browser, can display the sandbox
/// without a key in its bundle. See <see cref="ReadOnlyRoutes"/>.
///
/// TEMPORARY: three workflow writes are also exempt, so the end-to-end workflow
/// can be driven from the browser without a key. See
/// <see cref="UnauthenticatedWriteRoutes"/>; that list is meant to be deleted when
/// real sign-in lands.
/// </summary>
public sealed class AdminKeyMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<AdminKeyMiddleware> logger)
{
    /// <summary>
    /// Re-exposed from <see cref="SandboxAdminKey"/> so existing call sites keep
    /// working; the constant itself belongs to the engine, which also has to send it.
    /// </summary>
    public const string HeaderName = SandboxAdminKey.HeaderName;

    /// <summary>
    /// Routes a browser may read without a key.
    ///
    /// Listed explicitly rather than allowing every GET. The method is not a
    /// reliable guide here: <c>GET /admin/cir/await-response</c> re-reads an open
    /// consumer session and settles messages, and <c>GET /admin/cir/diagnose</c>
    /// exercises the provider — both have effects a reader would not expect. An
    /// allow-list fails closed, so a new endpoint is guarded until someone decides
    /// otherwise.
    ///
    /// What this concedes: anyone who finds the URL can read the sandbox's
    /// contents. That is accepted for a demo. It does not let them change
    /// anything — every write, reset and channel operation still needs the key.
    ///
    /// Prefixes, so <c>/admin/eng/tags?iTwinId=...</c> and the participant-scoped
    /// routes match without enumerating every participant.
    /// </summary>
    private static readonly string[] ReadOnlyRoutes =
    [
        "/admin/eng/twins",
        "/admin/eng/tags",
        "/admin/reg-location/stewardship",
        "/admin/reg-location/locations",
        "/admin/mms/locations",
        "/admin/scenarios",
    ];

    /// <summary>
    /// Read-only routes that carry a participant id in the path, matched on the
    /// trailing segment because the middle segment varies.
    /// </summary>
    private static readonly string[] ReadOnlySuffixes =
    [
        "/class-catalog",
        "/messages",
        "/outbox",
    ];

    /// <summary>
    /// TEMPORARY. Writes the Workflow Orchestration app makes, exempted so the
    /// end-to-end workflow can be exercised in the browser without pasting a key
    /// at every step.
    ///
    /// This is a deliberate hole: anyone who finds the URL can author segments,
    /// publish a design and approve stewardship. It is acceptable only because the
    /// sandbox holds simulated data that /admin/reset can recreate. It is NOT
    /// acceptable once the sandbox carries anything anyone depends on.
    ///
    /// Kept as its own list, separate from the read exemptions, so that replacing
    /// this with real sign-in is a matter of deleting this array and the branch
    /// that reads it — the read exemptions can then be judged on their own merits.
    ///
    /// Still excluded: /admin/reset, /admin/schema/*, and every ISBM channel
    /// operation. Those destroy state rather than add to it, so an accident there
    /// costs a redeploy rather than an undo.
    /// </summary>
    private static readonly string[] UnauthenticatedWriteRoutes =
    [
        "/admin/eng/tags",
        "/admin/eng/promote",
        "/admin/reg-location/approve",
    ];

    private readonly string? _key = configuration[SandboxAdminKey.ConfigurationKey];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (!path.StartsWithSegments("/admin") || string.IsNullOrWhiteSpace(_key))
        {
            await next(context);
            return;
        }

        if (IsReadOnly(context.Request))
        {
            await next(context);
            return;
        }

        if (IsUnauthenticatedWrite(context.Request))
        {
            // Logged at warning, not debug: an unauthenticated write is the thing
            // we would want to find in the logs when this is tightened, and a quiet
            // exemption is one that gets forgotten.
            logger.LogWarning(
                "Allowing unauthenticated {Method} {Path} from {Ip}: temporary workflow exemption",
                context.Request.Method, path, context.Connection.RemoteIpAddress);

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

    /// <summary>
    /// True when the request is one a browser may make without a key.
    ///
    /// The method is checked as well as the path: several exempt routes also
    /// accept a POST that writes — <c>/admin/eng/tags</c> authors a segment, and
    /// <c>/admin/scenarios/{id}/run</c> starts a run — so matching on path alone
    /// would open a write path while appearing to exempt a read.
    /// </summary>
    private static bool IsReadOnly(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;

        foreach (var route in ReadOnlyRoutes)
        {
            if (path.StartsWith(route, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var suffix in ReadOnlySuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True for the temporary write exemptions. See
    /// <see cref="UnauthenticatedWriteRoutes"/> — this exists to be deleted.
    ///
    /// Matched exactly rather than by prefix: /admin/eng/promote must not also
    /// exempt some later /admin/eng/promote-all, and an exemption that grows on
    /// its own is the kind that outlives the reason for it.
    /// </summary>
    private static bool IsUnauthenticatedWrite(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method))
        {
            return false;
        }

        var path = request.Path.Value ?? string.Empty;

        foreach (var route in UnauthenticatedWriteRoutes)
        {
            if (path.Equals(route, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
