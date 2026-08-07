using System.Net.Mime;
using System.Text.Json;
using CirProvider.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CirProvider.Middleware;

/// <summary>
/// Projects ws-CIR faults onto RFC 9457 problem details. The OAGIS message model
/// allows several faults per response, so the payload always carries an array and
/// the status line reflects the first one.
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
        catch (Exception ex)
        {
            var logger = context.InstanceServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("CirProvider.Faults");

            var http = context.GetHttpContext();
            if (http is null)
            {
                logger.LogError(ex, "Unhandled non-HTTP exception.");
                throw;
            }

            var (status, body) = Translate(ex, http.Request.Path);

            if (status >= 500)
                logger.LogError(ex, "Unhandled exception.");
            else
                logger.LogWarning("{Fault}: {Message}", ex.GetType().Name, ex.Message);

            http.Response.StatusCode = status;
            http.Response.ContentType = "application/problem+json";
            await http.Response.WriteAsync(JsonSerializer.Serialize(body, Json));
        }
    }

    private static (int Status, object Body) Translate(Exception ex, string instance) => ex switch
    {
        CirFaultException fault => (fault.StatusCode, new
        {
            type = "https://www.openoandm.org/ws-cir/1.0/fault",
            title = fault.Faults[0].Code.ToString(),
            status = fault.StatusCode,
            detail = fault.Message,
            instance,
            faults = fault.Faults.Select(f => new { code = f.Code.ToString(), detail = f.Detail })
        }),

        NotImplementedException nie => (501, new
        {
            type = "https://www.openoandm.org/ws-cir/1.0/not-implemented",
            title = "NotImplemented",
            status = 501,
            detail = nie.Message,
            instance
        }),

        System.Xml.XmlException xe => (400, new
        {
            type = "https://www.openoandm.org/ws-cir/1.0/malformed-bod",
            title = "MalformedBod",
            status = 400,
            detail = xe.Message,
            instance
        }),

        NotSupportedException nse => (400, new
        {
            type = "https://www.openoandm.org/ws-cir/1.0/unknown-bod",
            title = "UnknownBod",
            status = 400,
            detail = nse.Message,
            instance
        }),

        JsonException je => (400, new
        {
            type = "https://tools.ietf.org/html/rfc9457",
            title = "MalformedRequest",
            status = 400,
            detail = je.Message,
            instance
        }),

        _ => (500, new
        {
            type = "https://tools.ietf.org/html/rfc9457",
            title = "InternalServerError",
            status = 500,
            detail = "An unexpected error occurred.",
            instance
        })
    };
}
