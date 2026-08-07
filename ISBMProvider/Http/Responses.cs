using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;
using IsbmProvider.Models;

namespace IsbmProvider.Http;

/// <summary>JSON + ISBM-fault response helpers with the spec's status-code mappings.</summary>
public static class Responses
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<HttpResponseData> JsonAsync<T>(this HttpRequestData req, T body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var res = req.CreateResponse(status);
        res.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await res.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return res;
    }

    public static HttpResponseData NoContent(this HttpRequestData req)
        => req.CreateResponse(HttpStatusCode.NoContent);

    /// <summary>404 for ReadPublication/ReadResponse "no message" (spec maps empty queue to 404).</summary>
    public static HttpResponseData NoMessage(this HttpRequestData req)
        => req.CreateResponse(HttpStatusCode.NotFound);

    public static async Task<HttpResponseData> FaultAsync(this HttpRequestData req, IsbmFaultException fault)
    {
        var status = (HttpStatusCode)fault.StatusCode;
        var res = req.CreateResponse(status);
        res.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await res.WriteStringAsync(JsonSerializer.Serialize(new { fault = fault.Kind.ToString(), message = fault.Message }, JsonOptions));
        return res;
    }

    public static async Task<T?> ReadJsonAsync<T>(this HttpRequestData req)
    {
        using var reader = new StreamReader(req.Body);
        var raw = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(raw) ? default : JsonSerializer.Deserialize<T>(raw, JsonOptions);
    }

    public static string DecodeChannelUri(string routeValue) => Uri.UnescapeDataString(routeValue);
}
