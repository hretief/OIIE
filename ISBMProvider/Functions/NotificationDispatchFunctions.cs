using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using IsbmProvider.Abstractions;
using IsbmProvider.Models;

namespace IsbmProvider.Functions;

/// <summary>
/// Service Bus-triggered workers that dispatch ISBM NotifyListener (§5.3) and
/// MessageExpired (§5.4) callbacks to subscriber endpoints.
///
/// Pipeline: PostPublication → broker publishes notification event to isbm-notifications →
/// this trigger fires → looks up sessions with ListenerURLs via ISessionRegistry →
/// calls INotificationDispatcher for each.
/// </summary>
public sealed class NotificationDispatchFunctions(
    INotificationDispatcher dispatcher,
    ISessionRegistry sessions,
    ILogger<NotificationDispatchFunctions> log)
{
    /// <summary>
    /// Fires when a publication/request is posted. Looks up all sessions on the channel
    /// that have a ListenerURL and dispatches a PUT notification to each.
    /// </summary>
    [Function("NotifyOnMessage")]
    public async Task NotifyOnMessage(
        [ServiceBusTrigger("isbm-notifications", "dispatch", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message)
    {
        var channelUri = message.ApplicationProperties.TryGetValue("isbm.channelUri", out var c) ? c as string : null;
        var messageId = message.ApplicationProperties.TryGetValue("isbm.messageId", out var m) ? m as string : null;
        var topicsRaw = message.ApplicationProperties.TryGetValue("isbm.topics", out var t) ? t as string : "";
        var originalMessageId = message.ApplicationProperties.TryGetValue("isbm.originalMessageId", out var o) ? o as string : null;
        var topics = (topicsRaw ?? "").Trim('|').Split('|', StringSplitOptions.RemoveEmptyEntries);

        if (string.IsNullOrEmpty(channelUri) || string.IsNullOrEmpty(messageId))
        {
            log.LogWarning("NotifyOnMessage: missing channelUri or messageId, skipping.");
            return;
        }

        var notifiable = sessions.GetNotifiableSessions(channelUri);
        if (notifiable.Count == 0)
        {
            log.LogDebug("NotifyOnMessage: no sessions with ListenerURL on channel {Channel}", channelUri);
            return;
        }

        foreach (var session in notifiable)
        {
            // Filter: only notify if the session's topics intersect with the message's topics
            // (for request-response notifications, topics may be empty — always notify)
            if (topics.Length > 0 && session.Topics.Count > 0 &&
                !session.Topics.Any(st => topics.Contains(st)))
                continue;

            await dispatcher.NotifyAsync(
                session.ListenerUrl!, session.SessionId, messageId,
                topics.Length > 0 ? topics.Intersect(session.Topics).ToArray() : topics,
                originalMessageId);
        }

        log.LogInformation("NotifyOnMessage dispatched for message {MessageId} on {Channel} ({Count} sessions)",
            messageId, channelUri, notifiable.Count);
    }

    /// <summary>
    /// Fires when a message expires (dead-letters or explicit expiry event).
    /// Notifies sessions with ExpirationListenerURL.
    /// </summary>
    [Function("NotifyOnExpiry")]
    public async Task NotifyOnExpiry(
        [ServiceBusTrigger("isbm-expired", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage expired)
    {
        var channelUri = expired.ApplicationProperties.TryGetValue("isbm.channelUri", out var c) ? c as string : null;
        var messageId = expired.ApplicationProperties.TryGetValue("isbm.messageId", out var m) ? m as string : null;

        if (string.IsNullOrEmpty(channelUri) || string.IsNullOrEmpty(messageId))
        {
            log.LogWarning("NotifyOnExpiry: missing channelUri or messageId, skipping.");
            return;
        }

        var expirable = sessions.GetExpirableSessions(channelUri);
        foreach (var session in expirable)
        {
            await dispatcher.NotifyExpiryAsync(session.ExpirationListenerUrl!, session.SessionId, messageId);
        }

        log.LogInformation("NotifyOnExpiry dispatched for message {MessageId} on {Channel} ({Count} sessions)",
            messageId, channelUri, expirable.Count);
    }
}
