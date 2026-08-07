using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace CirProvider.Functions;

internal static class RequestExtensions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Reads and deserializes the request body. Explicit rather than relying on
    /// parameter binding, so the serializer options are the ones we configured.
    /// </summary>
    public static async Task<T> ReadJsonAsync<T>(this HttpRequest request, CancellationToken ct)
    {
        var value = await JsonSerializer.DeserializeAsync<T>(request.Body, Json, ct);
        return value ?? throw new JsonException($"Request body was empty or null; expected {typeof(T).Name}.");
    }
}
