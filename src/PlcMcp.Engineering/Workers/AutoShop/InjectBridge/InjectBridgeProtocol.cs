using System.Text.Json.Serialization;

namespace PlcMcp.Engineering.Workers.AutoShop.InjectBridge;

/// <summary>
/// Wire protocol of the AutoShop inject bridge: one JSON object per line over
/// the named pipe <c>\\.\pipe\PlcMcpAutoShopInjectBridge</c> (UTF-8, LF).
///
/// The native side lives in <c>tools/AutoShopInjectBridge/src/bridge/</c>
/// (see BridgePipeServer.cpp for the reference implementation); this file is
/// the C# mirror of the same protocol.
///
/// Safety architecture (two independent layers over the vendor stack):
/// 1. C# gate (<see cref="InjectBridgeGateOptions"/>): the client refuses to
///    send write-surface methods unless the corresponding gate is open;
/// 2. bridge gate + loopback hard check: the DLL re-checks its own config gate
///    and refuses every non-loopback target BEFORE any vendor interaction.
///
/// The vendor write/force/download exports are resolved but never invoked by
/// the delivered bridge; compile is an E3 address slot. Honesty rules apply:
/// no answer is ever converted into a fake success.
/// </summary>
public static class InjectBridgeProtocol
{
    /// <summary>Fixed pipe name (local machine only; PIPE_REJECT_REMOTE_CLIENTS).</summary>
    public const string PipeName = "PlcMcpAutoShopInjectBridge";

    /// <summary>Protocol version implemented by both sides.</summary>
    public const string ProtocolVersion = "1.0";

    /// <summary>Worker identity reported by the native bridge.</summary>
    public const string WorkerName = "PlcMcpAutoShopInjectBridge";

    /// <summary>Bridge is version-locked to this AutoShop build (x86).</summary>
    public const string LockedVendorVersion = "4.12.0.2";

    public const string Disclaimer =
        "AutoShop interfaces used by this bridge were obtained by reverse engineering; " +
        "there is no vendor endorsement. Version-locked to AutoShop 4.12.0.2 (x86). " +
        "The bridge only runs inside AutoShop instances started and journaled by the injector, " +
        "talks to loopback targets only, and never presents a reservation as an executed action.";

    // ---- methods -----------------------------------------------------------

    public const string MethodHandshake = "handshake";
    public const string MethodCapabilities = "capabilities";
    public const string MethodSetTarget = "set_target";
    public const string MethodReadStatus = "read_status";
    public const string MethodReadElements = "read_elements";
    public const string MethodWriteElements = "write_elements";
    public const string MethodForce = "force";
    public const string MethodDownload = "download";
    public const string MethodCompile = "compile";
    public const string MethodShutdown = "shutdown";

    /// <summary>Methods the client may put on the wire without a gate.</summary>
    public static readonly HashSet<string> AllowedMethods = new(StringComparer.Ordinal)
    {
        MethodHandshake,
        MethodCapabilities,
        MethodSetTarget,
        MethodReadStatus,
        MethodReadElements,
        MethodShutdown
    };

    /// <summary>Write-surface methods: sendable only when the matching gate is open.</summary>
    public static readonly HashSet<string> GatedMethods = new(StringComparer.Ordinal)
    {
        MethodWriteElements,
        MethodForce,
        MethodDownload,
        MethodCompile
    };

    /// <summary>All methods the client knows about.</summary>
    public static IEnumerable<string> AllMethods => AllowedMethods.Concat(GatedMethods);

    // ---- structured error codes (mirror of BridgeApi.h) --------------------

    public const int ErrMissingMethod = -32600;
    public const int ErrMethodNotFound = -32601;
    public const int ErrInvalidParams = -32602;
    public const int ErrVendorModuleAbsent = -32001;
    public const int ErrVendorCallFailed = -32002;
    public const int ErrLoopbackRequired = -32003;
    public const int ErrBridgeException = -32004;
    public const int ErrGateClosed = -32005;
    public const int ErrUnsupported = -32006;
}

/// <summary>JSON-RPC-style request line (id + method + params).</summary>
public sealed record InjectBridgeRequest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] InjectBridgeRequestParams? Params = null);

public sealed record InjectBridgeRequestParams(
    [property: JsonPropertyName("client")] string? Client = null,
    [property: JsonPropertyName("addresses")] string? Addresses = null,
    [property: JsonPropertyName("values")] string? Values = null,
    [property: JsonPropertyName("target")] string? Target = null,
    [property: JsonPropertyName("ip")] string? Ip = null,
    [property: JsonPropertyName("port")] int? Port = null,
    // G1 read_elements variants (docs/inovance-autoshop-monitor-values.md):
    // enum values for the vendor statics (default 0/0 accepted combo; the F1
    // UI call sites use 1/0), settle-window ms, and the bounded experiment
    // switches (frame-CWnd shim / active-map report / watch-buffer word
    // surface). Null fields are ignored by the native parser.
    [property: JsonPropertyName("mode")] int? Mode = null,
    [property: JsonPropertyName("level")] int? Level = null,
    [property: JsonPropertyName("settleMs")] int? SettleMs = null,
    [property: JsonPropertyName("wnd")] int? Wnd = null,
    [property: JsonPropertyName("activeMap")] int? ActiveMap = null,
    [property: JsonPropertyName("watch")] int? Watch = null,
    [property: JsonPropertyName("online")] int? Online = null);

/// <summary>JSON-RPC-style response line (result or error, never both).</summary>
public sealed record InjectBridgeResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("result")] InjectBridgeResult? Result = null,
    [property: JsonPropertyName("error")] InjectBridgeError? Error = null);

/// <summary>
/// Structured error. Codes mirror the native side; see
/// <see cref="InjectBridgeProtocol"/> for the constants.
/// </summary>
public sealed record InjectBridgeError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Result payload of the inject bridge. Fields are nullable because each
/// method fills only the members relevant to it.
/// </summary>
public sealed record InjectBridgeResult(
    // handshake
    [property: JsonPropertyName("workerName")] string? WorkerName = null,
    [property: JsonPropertyName("workerVersion")] string? WorkerVersion = null,
    [property: JsonPropertyName("protocolVersion")] string? ProtocolVersion = null,
    [property: JsonPropertyName("vendor")] string? Vendor = null,
    [property: JsonPropertyName("bitness")] string? Bitness = null,
    [property: JsonPropertyName("runtimeEnvironment")] string? RuntimeEnvironment = null,
    [property: JsonPropertyName("pid")] int? Pid = null,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string>? CapabilityList = null,
    [property: JsonPropertyName("disclaimer")] string? Disclaimer = null,
    // capability matrix
    [property: JsonPropertyName("comm_stack_read_only")] string? CommStackReadOnly = null,
    [property: JsonPropertyName("variable_read")] string? VariableRead = null,
    [property: JsonPropertyName("element_write")] string? ElementWrite = null,
    [property: JsonPropertyName("force")] string? Force = null,
    [property: JsonPropertyName("download")] string? Download = null,
    [property: JsonPropertyName("compile")] string? Compile = null,
    [property: JsonPropertyName("run_stop")] string? RunStop = null,
    // set_target
    [property: JsonPropertyName("stored")] bool? Stored = null,
    [property: JsonPropertyName("ip")] string? Ip = null,
    [property: JsonPropertyName("port")] int? Port = null,
    [property: JsonPropertyName("loopbackOnly")] bool? LoopbackOnly = null,
    // read_status
    [property: JsonPropertyName("execMode")] int? ExecMode = null,
    [property: JsonPropertyName("execModeCallResult")] long? ExecModeCallResult = null,
    [property: JsonPropertyName("gcmState")] int? GcmState = null,
    [property: JsonPropertyName("gcmStateCallResult")] long? GcmStateCallResult = null,
    [property: JsonPropertyName("evidence")] string? Evidence = null,
    // read_elements / write reservation
    [property: JsonPropertyName("count")] int? Count = null,
    [property: JsonPropertyName("values")] string? Values = null,
    [property: JsonPropertyName("note")] string? Note = null,
    // G1 read_elements value-delivery fields (docs/inovance-autoshop-monitor-
    // values.md): Delivered counts the tagSoftAddr+0x0C CStrings that actually
    // changed (a daemon-answered read); ValueSlots mirrors them.
    [property: JsonPropertyName("delivered")] int? Delivered = null,
    [property: JsonPropertyName("valueSlots")] string? ValueSlots = null,
    [property: JsonPropertyName("watchRc")] long? WatchRc = null,
    [property: JsonPropertyName("onlineRc")] long? OnlineRc = null,
    [property: JsonPropertyName("activeMap")] string? ActiveMap = null,
    [property: JsonPropertyName("activeMapCount")] int? ActiveMapCount = null,
    [property: JsonPropertyName("onlineBefore")] string? OnlineBefore = null,
    [property: JsonPropertyName("variant")] string? Variant = null,
    [property: JsonPropertyName("method")] string? RequestMethod = null,
    [property: JsonPropertyName("resolved")] bool? Resolved = null,
    [property: JsonPropertyName("invoked")] bool? Invoked = null,
    [property: JsonPropertyName("export")] string? Export = null,
    [property: JsonPropertyName("exportRva")] string? ExportRva = null,
    [property: JsonPropertyName("exportAddress")] string? ExportAddress = null,
    [property: JsonPropertyName("reason")] string? Reason = null,
    // shutdown
    [property: JsonPropertyName("stopping")] bool? Stopping = null);
