using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PlcMcp.Engineering.Workers.AutoShop.InjectBridge;

/// <summary>
/// Configuration of the inject bridge pipe client. All probes are injectable
/// so tests run fully offline against a mock pipe server.
/// </summary>
public sealed class InjectBridgeClientOptions
{
    /// <summary>Pipe name (without the \\.\pipe\ prefix).</summary>
    public string PipeName { get; init; } = InjectBridgeProtocol.PipeName;

    /// <summary>Server machine; "." means the local machine.</summary>
    public string ServerName { get; init; } = ".";

    /// <summary>Per-call timeout. A timeout is never converted into a success.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>C# gate for the write surface (default: everything closed).</summary>
    public InjectBridgeGateOptions Gate { get; init; } = new();

    /// <summary>
    /// Overrides the transport for tests: (pipeName, serverName) -> stream.
    /// When set, no real named pipe is opened.
    /// </summary>
    public Func<string, string, Task<Stream>>? TransportFactory { get; init; }
}

/// <summary>
/// Result of one bridge call. <see cref="Succeeded"/> is false for transport
/// failures, timeouts, gate refusals, loopback refusals and protocol-level
/// errors; the native error code and message are preserved for diagnosis.
/// </summary>
public sealed record InjectBridgeCallResult(
    bool Succeeded,
    InjectBridgeResponse? Response,
    string? ErrorMessage = null,
    string? Endpoint = null,
    bool RefusedLocally = false)
{
    public static InjectBridgeCallResult Ok(InjectBridgeResponse response, string endpoint) =>
        new(true, response, null, endpoint);

    public static InjectBridgeCallResult Failed(string message, string endpoint, bool refusedLocally = false) =>
        new(false, null, message, endpoint, refusedLocally);
}

/// <summary>
/// Named-pipe client for the AutoShop inject bridge (JSON line protocol).
///
/// Security posture (mirrors ExternalWorkerProtocol):
/// - only the local machine and the fixed pipe name are used;
/// - non-gated methods must be in the protocol allowlist; write-surface
///   methods are only sent when the matching <see cref="InjectBridgeGateOptions"/>
///   gate is open;
/// - a non-loopback <c>target</c>/<c>ip</c> parameter is refused LOCALLY,
///   before anything touches the pipe;
/// - every call is bounded by a timeout and a cancellation token; timeouts,
///   disconnects and bad frames surface as failures, never as successes.
/// </summary>
public sealed class InjectBridgePipeClient : IAsyncDisposable
{
    private readonly InjectBridgeClientOptions _options;
    private readonly string _endpoint;
    private Stream? _stream;
    private readonly byte[] _readBuffer = new byte[4096];
    private readonly StringBuilder _lineBuffer = new();
    private int _requestCounter;
    private bool _disposed;

    public InjectBridgePipeClient(InjectBridgeClientOptions? options = null)
    {
        options?.Gate.Validate();
        _options = options ?? new InjectBridgeClientOptions();
        _endpoint = $"{_options.ServerName}\\{_options.PipeName}";
    }

    /// <summary>Endpoint description used in diagnostics ("." + pipe name).</summary>
    public string Endpoint => _endpoint;

    /// <summary>True once the transport is connected.</summary>
    public bool IsConnected => _stream != null && !_disposed;

    /// <summary>
    /// True when the method may go on the wire: allowlisted, and for the
    /// write surface the matching C# gate must be open.
    /// </summary>
    public static bool IsSendable(string method, InjectBridgeGateOptions gate)
    {
        if (InjectBridgeProtocol.AllowedMethods.Contains(method))
        {
            return true;
        }
        return method switch
        {
            InjectBridgeProtocol.MethodWriteElements => gate.AllowElementWrite,
            InjectBridgeProtocol.MethodForce => gate.AllowForce,
            InjectBridgeProtocol.MethodDownload => gate.AllowDownload,
            // compile is a future E3 wiring: the client may send it (the
            // bridge answers Unsupported honestly) but the C# hint must exist.
            InjectBridgeProtocol.MethodCompile => !string.IsNullOrEmpty(gate.CompileEntryHint),
            _ => false
        };
    }

    /// <summary>
    /// Connects the transport. With the default options this opens the local
    /// named pipe; tests inject a transport factory instead.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(InjectBridgePipeClient));

        if (_options.TransportFactory != null)
        {
            _stream = await _options.TransportFactory(_options.PipeName, _options.ServerName)
                .ConfigureAwait(false);
        }
        else
        {
            var client = new NamedPipeClientStream(
                _options.ServerName, _options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync((int)_options.Timeout.TotalMilliseconds, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            _stream = client;
        }
    }

    /// <summary>
    /// Sends one request line and awaits the matching response line.
    /// Gate-closed and non-loopback requests are refused locally without
    /// touching the pipe (marked <see cref="InjectBridgeCallResult.RefusedLocally"/>).
    /// </summary>
    public async Task<InjectBridgeCallResult> CallAsync(
        string method,
        InjectBridgeRequestParams? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(InjectBridgePipeClient));

        if (!IsSendable(method, _options.Gate))
        {
            string gate = InjectBridgeProtocol.GatedMethods.Contains(method)
                ? $" the C# gate for '{method}' is closed."
                : string.Empty;
            return InjectBridgeCallResult.Failed(
                $"Method '{method}' is not sendable by this client configuration.{gate}",
                _endpoint, refusedLocally: true);
        }

        // Local loopback hard check on the wire parameters, mirroring the
        // bridge-side check: nothing with a non-loopback target leaves this
        // process.
        if (parameters != null)
        {
            if (!string.IsNullOrEmpty(parameters.Target) &&
                !InjectBridgeGateOptions.IsLoopbackAddress(parameters.Target))
            {
                return InjectBridgeCallResult.Failed(
                    $"Non-loopback target '{parameters.Target}' refused locally; the inject bridge only talks to 127.0.0.0/8.",
                    _endpoint, refusedLocally: true);
            }
            if (!string.IsNullOrEmpty(parameters.Ip) &&
                !InjectBridgeGateOptions.IsLoopbackAddress(parameters.Ip))
            {
                return InjectBridgeCallResult.Failed(
                    $"Non-loopback ip '{parameters.Ip}' refused locally; set_target only accepts 127.0.0.0/8.",
                    _endpoint, refusedLocally: true);
            }
        }

        if (_stream == null)
        {
            return InjectBridgeCallResult.Failed("Bridge transport is not connected.", _endpoint);
        }

        string id = Interlocked.Increment(ref _requestCounter).ToString();
        var request = new InjectBridgeRequest(id, method, parameters);
        string line = JsonSerializer.Serialize(request);

        using var timeoutCts = new CancellationTokenSource(_options.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            byte[] payload = Encoding.UTF8.GetBytes(line + "\n");
            await _stream.WriteAsync(payload, linkedCts.Token).ConfigureAwait(false);
            await _stream.FlushAsync(linkedCts.Token).ConfigureAwait(false);

            string? responseLine = await ReadLineAsync(linkedCts.Token).ConfigureAwait(false);
            if (responseLine == null)
            {
                return InjectBridgeCallResult.Failed(
                    "Bridge closed the connection before answering.", _endpoint);
            }

            InjectBridgeResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<InjectBridgeResponse>(responseLine);
            }
            catch (JsonException ex)
            {
                return InjectBridgeCallResult.Failed(
                    $"Bridge response is not valid JSON: {ex.Message}", _endpoint);
            }

            if (response == null || (response.Result == null && response.Error == null))
            {
                return InjectBridgeCallResult.Failed(
                    "Bridge response carries neither result nor error.", _endpoint);
            }

            if (response.Error != null)
            {
                return InjectBridgeCallResult.Failed(
                    $"Bridge error [{response.Error.Code}]: {response.Error.Message}", _endpoint);
            }

            return InjectBridgeCallResult.Ok(response, _endpoint);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return InjectBridgeCallResult.Failed(
                $"Inject bridge call '{method}' timed out after {_options.Timeout.TotalSeconds}s.", _endpoint);
        }
        catch (IOException ex)
        {
            return InjectBridgeCallResult.Failed(
                $"Inject bridge transport failure: {ex.Message}", _endpoint);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    // Raw byte reads with manual line framing. StreamReader.ReadLineAsync is
    // avoided on purpose: it hangs over named pipes on some runtimes
    // (observed on .NET 10 rolling a net8.0 assembly forward), while
    // Stream.ReadAsync honours cancellation and completion reliably.
    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            int newline = _lineBuffer.IndexOf('\n');
            if (newline >= 0)
            {
                string line = _lineBuffer.ToString(0, newline).Trim('\r');
                _lineBuffer.Remove(0, newline + 1);
                return line;
            }

            int read = await _stream!.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return _lineBuffer.Length == 0 ? null : _lineBuffer.ToString();
            }
            _lineBuffer.Append(Encoding.UTF8.GetString(_readBuffer, 0, read));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_stream != null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }
}

internal static class StringBuilderLineExtensions
{
    /// <summary>IndexOf for a single char, mirroring string.IndexOf semantics.</summary>
    public static int IndexOf(this StringBuilder builder, char value)
    {
        for (int i = 0; i < builder.Length; i++)
        {
            if (builder[i] == value)
            {
                return i;
            }
        }
        return -1;
    }
}
