using System.Net.Http.Json;

namespace SimHost.Application.Scenarios;

/// <summary>What a reset did, or why it could not be done.</summary>
public sealed record ResetOutcome(bool Succeeded, string Summary, string? Detail = null);

/// <summary>
/// Runs the admin reset endpoints on behalf of the UI.
///
/// Calls the endpoints over HTTP rather than invoking the underlying services directly,
/// even though both live in this process. The endpoints are already the definition of
/// what a reset means \u2014 sessions, channels, tables, reference data and run history, in
/// that order \u2014 and reimplementing that ordering behind a button would create a second
/// definition free to drift from the first. It also keeps the admin-key guard on the
/// path it protects instead of quietly bypassing it.
/// </summary>
public sealed class SandboxResetService(
    IHttpClientFactory clients,
    IConfiguration configuration,
    ILogger<SandboxResetService> logger)
{
    /// <summary>
    /// Tears down and rebuilds participant state: tables, reference data, run history,
    /// and this deployment's own ISBM channels.
    /// </summary>
    public Task<ResetOutcome> ResetAsync(string baseUrl, CancellationToken ct = default) =>
        PostAsync(baseUrl, "/admin/reset", "Environment reset", ct);

    /// <summary>
    /// Day zero. Also deletes channels belonging to other systems, which destroys their
    /// sessions — they will keep polling dead ids until restarted.
    /// </summary>
    public Task<ResetOutcome> DayZeroAsync(string baseUrl, CancellationToken ct = default) =>
        PostAsync(baseUrl, "/admin/reset/day-zero", "Day zero", ct);

    private async Task<ResetOutcome> PostAsync(
        string baseUrl, string path, string label, CancellationToken ct)
    {
        try
        {
            using var client = clients.CreateClient();

            // Long, because day zero deletes and recreates every channel and waits for
            // each delete to be observable before recreating it.
            client.Timeout = TimeSpan.FromMinutes(5);

            using var request = new HttpRequestMessage(
                HttpMethod.Post, new Uri(new Uri(baseUrl), path));

            var key = configuration["Sandbox:AdminKey"];

            if (!string.IsNullOrWhiteSpace(key))
            {
                request.Headers.Add(Middleware.AdminKeyMiddleware.HeaderName, key);
            }

            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "{Label} failed with {Status}: {Body}", label, response.StatusCode, body);

                return new ResetOutcome(
                    false, $"{label} failed ({(int)response.StatusCode}).", body);
            }

            logger.LogInformation("{Label} completed from the UI.", label);

            return new ResetOutcome(true, $"{label} complete.", body);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Label} could not be run.", label);
            return new ResetOutcome(false, $"{label} could not be run.", ex.Message);
        }
    }
}
