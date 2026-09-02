using System.Net.Http.Json;
using System.Text.Json;
using EngProvider.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EngProvider.Infrastructure.Notifications;

/// <summary>
/// Posts the named-version fact to a configured URL.
///
/// A plain HTTP POST rather than a queue or a channel client, because the
/// receiver is not this project's concern and should be swappable without ENG
/// being rebuilt. In the demo that URL is EngEngine's webhook; in a customer
/// deployment it would be whatever they already have.
/// </summary>
public sealed class HttpNamedVersionNotifier(
    HttpClient http,
    IOptions<EngOptions> options,
    ILogger<HttpNamedVersionNotifier> logger) : INamedVersionNotifier
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task NotifyNamedVersionCreatedAsync(
        NamedVersionCreatedNotification notification, CancellationToken ct)
    {
        var url = options.Value.NamedVersionNotificationUrl;

        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            using var response = await http.PostAsJsonAsync(url, notification, Json, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Named version notification for {Version} was refused with {Status}.",
                    notification.NamedVersionId, (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallowed on purpose. The marker is already committed; failing the
            // author's request now would roll nothing back and would make ENG
            // unavailable whenever its listener is. The listener is expected to
            // reconcile by polling named-versions, which is what EngEngine's
            // timer already does -- this notification only shortens the wait.
            logger.LogError(ex,
                "Named version notification for {Version} failed to send.",
                notification.NamedVersionId);
        }
    }
}
