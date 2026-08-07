using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using IsbmProvider.Abstractions;

namespace IsbmProvider.Infrastructure;

/// <summary>
/// HTTP implementation of <see cref="INotificationDispatcher"/>.
///
/// NotifyListener (spec §5.3.1 REST):
///   PUT {listenerUrl}/notifications/{session-id}/{message-id}
///   Body: { "topics": [...], "requestMessageId": "..." }
///   Response: 204 No Content
///
/// MessageExpired (spec §5.4.1 REST):
///   PUT {expirationListenerUrl}/expirations/{session-id}/{message-id}
///   Body: { "originalMessageId": "..." }
///   Response: 204 No Content
/// </summary>
public sealed class HttpNotificationDispatcher : INotificationDispatcher
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HttpNotificationDispatcher> _log;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public HttpNotificationDispatcher(IHttpClientFactory httpFactory, ILogger<HttpNotificationDispatcher> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public async Task NotifyAsync(string listenerUrl, string sessionId, string messageId,
        IReadOnlyList<string> topics, string? originalMessageId, CancellationToken ct = default)
    {
        var url = $"{listenerUrl.TrimEnd('/')}/notifications/{sessionId}/{messageId}";
        var body = new NotificationBody
        {
            Topics = topics,
            RequestMessageId = originalMessageId   // only for consumer request response notifications
        };

        try
        {
            using var http = _httpFactory.CreateClient("IsbmNotifications");
            using var response = await http.PutAsJsonAsync(url, body, Json, ct);
            _log.LogInformation("NotifyListener → {Url} returned {Status}", url, response.StatusCode);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "NotifyListener → {Url} failed", url);
            // Spec defines no faults on notification — fire-and-forget with logging.
            // Retry is handled by the Service Bus trigger's delivery count / dead-letter.
        }
    }

    public async Task NotifyExpiryAsync(string expirationListenerUrl, string sessionId,
        string messageId, CancellationToken ct = default)
    {
        var url = $"{expirationListenerUrl.TrimEnd('/')}/expirations/{sessionId}/{messageId}";
        var body = new ExpirationBody();

        try
        {
            using var http = _httpFactory.CreateClient("IsbmNotifications");
            using var response = await http.PutAsJsonAsync(url, body, Json, ct);
            _log.LogInformation("MessageExpired → {Url} returned {Status}", url, response.StatusCode);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MessageExpired → {Url} failed", url);
        }
    }

    private sealed class NotificationBody
    {
        public IReadOnlyList<string>? Topics { get; init; }
        public string? RequestMessageId { get; init; }
    }

    private sealed class ExpirationBody
    {
        public string? OriginalMessageId { get; init; }
    }
}
