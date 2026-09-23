using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlcMcp.Server.Mcp;

public sealed record JsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string? JsonRpc = null,
    [property: JsonPropertyName("id")] JsonElement Id = default,
    [property: JsonPropertyName("method")] string? Method = null,
    [property: JsonPropertyName("params")] JsonElement Params = default)
{
    public bool IsNotification => Id.ValueKind == JsonValueKind.Undefined;
    public bool HasValidId => Id.ValueKind is JsonValueKind.String or JsonValueKind.Number;
}

public static class JsonRpc
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Result(JsonElement id, object result) => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id = ExtractId(id),
        result
    }, SerializerOptions);

    public static string Error(JsonElement id, int code, string message, object? data = null) => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id = ExtractId(id),
        error = data is null
            ? (object)new { code, message }
            : new { code, message, data }
    }, SerializerOptions);

    private static object? ExtractId(JsonElement id)
    {
        return id.ValueKind switch
        {
            JsonValueKind.Number when id.TryGetInt64(out var l) => l,
            JsonValueKind.Number => id.GetDouble(),
            JsonValueKind.String => id.GetString(),
            _ => null
        };
    }
}
