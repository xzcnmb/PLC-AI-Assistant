using System.Text.Json.Serialization;

namespace PlcMcp.Engineering.Workers.AutoShop.NativeBridge;

/// <summary>
/// Wire protocol of the AutoShop native bridge: one JSON object per line over
/// the named pipe <c>\\.\pipe\PlcMcpAutoShopBridge</c> (UTF-8, LF terminated).
///
/// The native side (in-process plugin + M3 shim) lives in
/// <c>tools/AutoShopNativeBridge/</c>; this file is the C# mirror of the same
/// protocol (see <c>BridgePipeServer.cpp</c> for the reference implementation).
///
/// The protocol is strictly read-only: there is deliberately no request method
/// that writes, forces, downloads, compiles, or starts/stops anything.
/// </summary>
public static class NativeBridgeProtocol
{
    /// <summary>Fixed pipe name (local machine only).</summary>
    public const string PipeName = "PlcMcpAutoShopBridge";

    /// <summary>Protocol version implemented by both sides.</summary>
    public const string ProtocolVersion = "1.0";

    /// <summary>Worker identity reported by the native side.</summary>
    public const string WorkerName = "PlcMcpAutoShopNativeBridge";

    /// <summary>Bridge is version-locked to this AutoShop build.</summary>
    public const string LockedVendorVersion = "4.12.0.2";

    public const string Disclaimer =
        "AutoShop interfaces used by this bridge were obtained by reverse engineering; " +
        "there is no vendor endorsement. Version-locked to AutoShop 4.12.0.2 (x86). " +
        "This bridge is strictly read-only: no variable write, force, download, compile, " +
        "or run/stop path exists.";

    // ---- methods -----------------------------------------------------------

    /// <summary>Handshake; params: { "client": "&lt;name&gt;" }.</summary>
    public const string MethodHandshake = "handshake";

    /// <summary>Capability map; no params.</summary>
    public const string MethodCapabilities = "capabilities";

    /// <summary>PLC status read (exec mode / GCM state call results); no params.</summary>
    public const string MethodReadStatus = "read_status";

    /// <summary>Element read; params: { "addresses": "D100,D101" } (comma separated).</summary>
    public const string MethodReadElements = "read_elements";

    /// <summary>Stops the pipe server; no params.</summary>
    public const string MethodShutdown = "shutdown";

    /// <summary>Methods accepted by this client. Anything else is rejected locally.</summary>
    public static readonly HashSet<string> AllowedMethods = new(StringComparer.Ordinal)
    {
        MethodHandshake,
        MethodCapabilities,
        MethodReadStatus,
        MethodReadElements,
        MethodShutdown
    };
}

/// <summary>JSON-RPC-style request line (id + method + params).</summary>
public sealed record NativeBridgeRequest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] NativeBridgeRequestParams? Params = null);

public sealed record NativeBridgeRequestParams(
    [property: JsonPropertyName("client")] string? Client = null,
    [property: JsonPropertyName("addresses")] string? Addresses = null);

/// <summary>JSON-RPC-style response line (result or error, never both).</summary>
public sealed record NativeBridgeResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("result")] NativeBridgeResult? Result = null,
    [property: JsonPropertyName("error")] NativeBridgeError? Error = null);

/// <summary>
/// Structured error. Codes mirror the native side:
/// -32700 parse error, -32601 method not found, -32602 invalid params,
/// -32001 shim unavailable, -32002 comm-stack call failed.
/// </summary>
public sealed record NativeBridgeError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Result payload of the native bridge. Fields are nullable because the
/// native side only fills the members relevant to the request.
/// </summary>
public sealed record NativeBridgeResult(
    [property: JsonPropertyName("workerName")] string? WorkerName = null,
    [property: JsonPropertyName("workerVersion")] string? WorkerVersion = null,
    [property: JsonPropertyName("protocolVersion")] string? ProtocolVersion = null,
    [property: JsonPropertyName("vendor")] string? Vendor = null,
    [property: JsonPropertyName("bitness")] string? Bitness = null,
    [property: JsonPropertyName("runtimeEnvironment")] string? RuntimeEnvironment = null,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string>? CapabilityList = null,
    [property: JsonPropertyName("disclaimer")] string? Disclaimer = null,
    [property: JsonPropertyName("comm_stack_read_only")] string? CommStackReadOnly = null,
    [property: JsonPropertyName("variable_read")] string? VariableRead = null,
    [property: JsonPropertyName("compile")] string? Compile = null,
    [property: JsonPropertyName("download")] string? Download = null,
    [property: JsonPropertyName("force")] string? Force = null,
    [property: JsonPropertyName("run_stop")] string? RunStop = null,
    [property: JsonPropertyName("execMode")] int? ExecMode = null,
    [property: JsonPropertyName("execModeCallResult")] long? ExecModeCallResult = null,
    [property: JsonPropertyName("gcmState")] int? GcmState = null,
    [property: JsonPropertyName("gcmStateCallResult")] long? GcmStateCallResult = null,
    [property: JsonPropertyName("evidence")] string? Evidence = null,
    [property: JsonPropertyName("count")] int? Count = null,
    [property: JsonPropertyName("values")] string? Values = null,
    [property: JsonPropertyName("stopping")] bool? Stopping = null);
