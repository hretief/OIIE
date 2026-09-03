using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;
using IsbmProvider.Models;

namespace IsbmProvider.Http;

/// <summary>
/// Global middleware that catches:
///   - <see cref="IsbmFaultException"/> → structured ISBM fault response
///   - <see cref="JsonException"/> → 400 with the deserialization error (no more bare 500s)
/// </summary>
public sealed class FaultMiddleware : IFunctionsWorkerMiddleware
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (IsbmFaultException ex)
        {
            var log = context.GetLogger<FaultMiddleware>();
            log.LogWarning("ISBM fault: {Kind} — {Message}", ex.Kind, ex.Message);
            await WriteErrorResponseAsync(context, (HttpStatusCode)ex.StatusCode,
                new { fault = ex.Kind.ToString(), message = ex.Message });
        }
        catch (JsonException ex)
        {
            var log = context.GetLogger<FaultMiddleware>();
            log.LogWarning("JSON deserialization error: {Message}", ex.Message);
            await WriteErrorResponseAsync(context, HttpStatusCode.BadRequest,
                new { fault = "DeserializationError", message = ex.Message, path = ex.Path });
        }
        catch (Azure.Messaging.ServiceBus.ServiceBusException ex)
        {
            // Broker faults were previously unhandled and surfaced as bare 500s
            // with no body, which is what made a missing subscription look like
            // the broker was failing at random. Mapped here so the caller is told
            // what is actually wrong.
            var log = context.GetLogger<FaultMiddleware>();

            var (status, kind) = ex.Reason switch
            {
                Azure.Messaging.ServiceBus.ServiceBusFailureReason.MessagingEntityNotFound
                    => (HttpStatusCode.NotFound, "ChannelFault"),

                // The message lock expired or was already settled. Retryable by
                // reading again, and not the caller's mistake.
                Azure.Messaging.ServiceBus.ServiceBusFailureReason.MessageLockLost or
                Azure.Messaging.ServiceBus.ServiceBusFailureReason.SessionLockLost
                    => (HttpStatusCode.Conflict, "SessionFault"),

                Azure.Messaging.ServiceBus.ServiceBusFailureReason.ServiceBusy or
                Azure.Messaging.ServiceBus.ServiceBusFailureReason.ServiceTimeout
                    => (HttpStatusCode.ServiceUnavailable, "OperationFault"),

                _ => (HttpStatusCode.InternalServerError, "OperationFault")
            };

            log.LogError(ex, "Service Bus fault: {Reason}", ex.Reason);

            await WriteErrorResponseAsync(context, status,
                new { fault = kind, message = ex.Message, reason = ex.Reason.ToString() });
        }
    }

    private static async Task WriteErrorResponseAsync(FunctionContext context, HttpStatusCode status, object body)
    {
        var req = await context.GetHttpRequestDataAsync();
        if (req is not null)
        {
            var res = req.CreateResponse(status);
            res.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await res.WriteStringAsync(JsonSerializer.Serialize(body, Json));
            context.GetInvocationResult().Value = res;
        }
    }
}
