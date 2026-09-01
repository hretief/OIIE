using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RegLocationProvider.Application;

namespace RegLocationProvider.Infrastructure.Notifications;

/// <summary>
/// Posts the approval fact to a configured URL.
///
/// A plain HTTP POST rather than a queue or a channel client, because the
/// receiver is not this project's concern and should be swappable without
/// REG-LOCATION being rebuilt. In the demo that URL is the RegLocation engine's
/// webhook; in a customer deployment it would be whatever they already have.
/// </summary>
public sealed class HttpApprovalNotifier(
    HttpClient http,
    IOptions<RegLocationOptions> options,
    ILogger<HttpApprovalNotifier> logger) : IApprovalNotifier
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task NotifyTagApprovedAsync(TagApprovedNotification notification, CancellationToken ct)
    {
        var url = options.Value.ApprovalNotificationUrl;

        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            using var response = await http.PostAsJsonAsync(url, notification, Json, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Approval notification for tag {TagId} was refused with {Status}.",
                    notification.TagId, (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallowed on purpose. The approval is already committed; failing
            // the steward's request now would roll nothing back and would make
            // this registry unavailable whenever its listener is. The listener
            // is expected to reconcile by reading the proposed/approved queues.
            logger.LogError(ex, "Approval notification for tag {TagId} failed to send.", notification.TagId);
        }
    }
}
