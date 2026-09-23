using System.Text.Json;
using PlcMcp.Contracts;

namespace PlcMcp.Server.Mcp;

public sealed class McpServer
{
    private readonly McpToolRouter _router;

    public McpServer(McpToolRouter router)
    {
        _router = router;
    }

    public async Task RunAsync(TextReader input, TextWriter output, TextWriter errorLog, CancellationToken cancellationToken = default)
    {
        await errorLog.WriteLineAsync($"[INFO] {ContractConstants.ProductName} v{ContractConstants.ProductVersion} (MCP {ContractConstants.ProtocolVersion}) started. Listening on stdio...").ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                await errorLog.WriteLineAsync("[INFO] stdin reached EOF. Server shutting down.").ConfigureAwait(false);
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            await ProcessLineAsync(line, output, errorLog, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ProcessLineAsync(string line, TextWriter output, TextWriter errorLog, CancellationToken cancellationToken = default)
    {
        JsonRpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<JsonRpcRequest>(line, JsonRpc.SerializerOptions);
        }
        catch (JsonException ex)
        {
            await errorLog.WriteLineAsync($"[ERROR] Failed to parse JSON-RPC request: {ex.Message}").ConfigureAwait(false);
            string errResponse = JsonRpc.Error(default, JsonRpc.ParseError, "Parse error: Invalid JSON.");
            await output.WriteLineAsync(errResponse).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await errorLog.WriteLineAsync("[WARN] Received null JSON-RPC request.").ConfigureAwait(false);
            string errResponse = JsonRpc.Error(default, JsonRpc.InvalidRequest, "Invalid JSON-RPC request.");
            await output.WriteLineAsync(errResponse).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            return;
        }

        // Validate jsonrpc must be "2.0"
        if (request.JsonRpc != "2.0")
        {
            await errorLog.WriteLineAsync($"[WARN] Invalid jsonrpc version '{request.JsonRpc}'; expected '2.0'.").ConfigureAwait(false);
            if (!request.IsNotification)
            {
                string errResponse = JsonRpc.Error(request.Id, JsonRpc.InvalidRequest, "Invalid JSON-RPC: version must be '2.0'.");
                await output.WriteLineAsync(errResponse).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
            return;
        }

        // If not notification, id must be string or number (not boolean, array, or object)
        if (!request.IsNotification && !request.HasValidId)
        {
            await errorLog.WriteLineAsync("[WARN] Invalid JSON-RPC id: must be string, number, or null/omitted for notifications.").ConfigureAwait(false);
            string errResponse = JsonRpc.Error(default, JsonRpc.InvalidRequest, "Invalid JSON-RPC id: must be string or number.");
            await output.WriteLineAsync(errResponse).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Method))
        {
            await errorLog.WriteLineAsync("[WARN] Received invalid JSON-RPC request without method.").ConfigureAwait(false);
            if (!request.IsNotification)
            {
                string errResponse = JsonRpc.Error(request.Id, JsonRpc.InvalidRequest, "Invalid JSON-RPC request: 'method' is required.");
                await output.WriteLineAsync(errResponse).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
            return;
        }

        try
        {
            await HandleRequestAsync(request, output, errorLog, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await errorLog.WriteLineAsync($"[ERROR] Unexpected error processing '{request.Method}': {ex}").ConfigureAwait(false);
            if (!request.IsNotification)
            {
                string errResponse = JsonRpc.Error(request.Id, JsonRpc.InternalError, $"Internal error: {ex.Message}");
                await output.WriteLineAsync(errResponse).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task HandleRequestAsync(JsonRpcRequest request, TextWriter output, TextWriter errorLog, CancellationToken cancellationToken)
    {
        if (request.IsNotification)
        {
            await HandleNotificationAsync(request, errorLog, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (request.Method)
        {
            case "initialize":
                await HandleInitializeAsync(request, output).ConfigureAwait(false);
                break;

            case "notifications/initialized":
            case "initialized":
                await HandleInitializedNotificationAsync(request, output, errorLog).ConfigureAwait(false);
                break;

            case "ping":
                await WriteResponseAsync(output, JsonRpc.Result(request.Id, new { })).ConfigureAwait(false);
                break;

            case "tools/list":
                await HandleToolsListAsync(request, output).ConfigureAwait(false);
                break;

            case "tools/call":
                await HandleToolsCallAsync(request, output, errorLog, cancellationToken).ConfigureAwait(false);
                break;

            default:
                await errorLog.WriteLineAsync($"[WARN] Unrecognized method '{request.Method}' requested.").ConfigureAwait(false);
                await WriteResponseAsync(output, JsonRpc.Error(request.Id, JsonRpc.MethodNotFound, $"Method '{request.Method}' not found.")).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleNotificationAsync(JsonRpcRequest request, TextWriter errorLog, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "initialize":
                await errorLog.WriteLineAsync("[INFO] Received notification 'initialize'. Notifications do not receive responses.").ConfigureAwait(false);
                break;

            case "notifications/initialized":
            case "initialized":
                await errorLog.WriteLineAsync("[INFO] Client initialized successfully via notification.").ConfigureAwait(false);
                break;

            case "ping":
                await errorLog.WriteLineAsync("[INFO] Received ping notification.").ConfigureAwait(false);
                break;

            case "tools/list":
                await errorLog.WriteLineAsync("[INFO] Received notification 'tools/list'. Notifications do not receive responses.").ConfigureAwait(false);
                break;

            case "tools/call":
                await errorLog.WriteLineAsync("[WARN] tools/call requires a request id; notification ignored without execution.").ConfigureAwait(false);
                break;

            default:
                await errorLog.WriteLineAsync($"[WARN] Unrecognized notification method '{request.Method}' received. Discarded without response.").ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleToolsCallNotificationAsync(JsonRpcRequest request, TextWriter errorLog, CancellationToken cancellationToken)
    {
        if (request.Params.ValueKind != JsonValueKind.Object)
        {
            await errorLog.WriteLineAsync("[WARN] Notification 'tools/call' rejected: parameters must be an object.").ConfigureAwait(false);
            return;
        }

        if (!request.Params.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
        {
            await errorLog.WriteLineAsync("[WARN] Notification 'tools/call' rejected: missing or invalid 'name' property.").ConfigureAwait(false);
            return;
        }

        string toolName = nameProp.GetString()!;
        JsonElement args = default;
        if (request.Params.TryGetProperty("arguments", out var argumentsProp))
        {
            if (argumentsProp.ValueKind != JsonValueKind.Object)
            {
                await errorLog.WriteLineAsync("[WARN] Notification 'tools/call' rejected: 'arguments' must be an object.").ConfigureAwait(false);
                return;
            }
            args = argumentsProp;
        }
        else if (request.Params.TryGetProperty("args", out var argsProp))
        {
            if (argsProp.ValueKind != JsonValueKind.Object)
            {
                await errorLog.WriteLineAsync("[WARN] Notification 'tools/call' rejected: 'args' must be an object.").ConfigureAwait(false);
                return;
            }
            args = argsProp;
        }
        else
        {
            args = request.Params;
        }

        await errorLog.WriteLineAsync($"[INFO] Executing tool '{toolName}' via notification...").ConfigureAwait(false);
        try
        {
            await _router.CallToolAsync(toolName, args, cancellationToken).ConfigureAwait(false);
            await errorLog.WriteLineAsync($"[INFO] Tool '{toolName}' notification execution completed.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await errorLog.WriteLineAsync($"[ERROR] Error executing tool '{toolName}' via notification: {ex.Message}").ConfigureAwait(false);
        }
    }

    private static async Task HandleInitializeAsync(JsonRpcRequest request, TextWriter output)
    {
        var result = new
        {
            protocolVersion = ContractConstants.ProtocolVersion,
            capabilities = new
            {
                tools = new
                {
                    listChanged = false
                }
            },
            serverInfo = new
            {
                name = ContractConstants.ProductName,
                version = ContractConstants.ProductVersion
            }
        };

        await WriteResponseAsync(output, JsonRpc.Result(request.Id, result)).ConfigureAwait(false);
    }

    private static async Task HandleInitializedNotificationAsync(JsonRpcRequest request, TextWriter output, TextWriter errorLog)
    {
        await errorLog.WriteLineAsync("[INFO] Client initialized successfully.").ConfigureAwait(false);

        if (!request.IsNotification)
        {
            await WriteResponseAsync(output, JsonRpc.Result(request.Id, new { })).ConfigureAwait(false);
        }
    }

    private async Task HandleToolsListAsync(JsonRpcRequest request, TextWriter output)
    {
        var result = new
        {
            tools = _router.ToolDescriptors
        };

        await WriteResponseAsync(output, JsonRpc.Result(request.Id, result)).ConfigureAwait(false);
    }

    private async Task HandleToolsCallAsync(JsonRpcRequest request, TextWriter output, TextWriter errorLog, CancellationToken cancellationToken)
    {
        if (request.Params.ValueKind != JsonValueKind.Object)
        {
            await WriteResponseAsync(output, JsonRpc.Error(request.Id, JsonRpc.InvalidParams, "Parameters for 'tools/call' must be an object.")).ConfigureAwait(false);
            return;
        }

        if (!request.Params.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
        {
            await WriteResponseAsync(output, JsonRpc.Error(request.Id, JsonRpc.InvalidParams, "Missing or invalid 'name' property in 'tools/call' parameters.")).ConfigureAwait(false);
            return;
        }

        string toolName = nameProp.GetString()!;
        JsonElement args = default;
        if (request.Params.TryGetProperty("arguments", out var argumentsProp))
        {
            if (argumentsProp.ValueKind != JsonValueKind.Object)
            {
                await WriteResponseAsync(output, JsonRpc.Error(request.Id, JsonRpc.InvalidParams, "Parameter 'arguments' for 'tools/call' must be an object.")).ConfigureAwait(false);
                return;
            }
            args = argumentsProp;
        }
        else if (request.Params.TryGetProperty("args", out var argsProp))
        {
            if (argsProp.ValueKind != JsonValueKind.Object)
            {
                await WriteResponseAsync(output, JsonRpc.Error(request.Id, JsonRpc.InvalidParams, "Parameter 'args' for 'tools/call' must be an object.")).ConfigureAwait(false);
                return;
            }
            args = argsProp;
        }
        else
        {
            args = request.Params;
        }

        await errorLog.WriteLineAsync($"[INFO] Executing tool '{toolName}'...").ConfigureAwait(false);
        var result = await _router.CallToolAsync(toolName, args, cancellationToken).ConfigureAwait(false);

        await WriteResponseAsync(output, JsonRpc.Result(request.Id, result)).ConfigureAwait(false);
    }

    private static async Task WriteResponseAsync(TextWriter output, string json)
    {
        await output.WriteLineAsync(json).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }
}
