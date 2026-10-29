using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PlcMcp.Engineering.Workers.AutoShop.NativeBridge;

/// <summary>
/// Configuration of the native bridge pipe client. All probes are injectable
/// so tests run fully offline against a mock pipe server.
/// </summary>
public sealed class NativeBridgeClientOptions
{
    /// <summary>Pipe name (without the \\.\pipe\ prefix).</summary>
    public string PipeName { get; init; } = NativeBridgeProtocol.PipeName;

    /// <summary>Server machine; "." means the local machine.</summary>
    public string ServerName { get; init; } = ".";

    /// <summary>Per-call timeout. A timeout is never converted into a success.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Overrides the transport for tests: (pipeName, serverName) -> stream pair.
    /// When set, no real named pipe is opened.
    /// </summary>
    public Func<string, string, Task<Stream>>? TransportFactory { get; init; }
}

/// <summary>
/// Result of one bridge call. <see cref="Succeeded"/> is false for transport
/// failures, timeouts and protocol-level errors; the native error code and
/// message are preserved for diagnosis.
/// </summary>
public sealed record NativeBridgeCallResult(
    bool Succeeded,
    NativeBridgeResponse? Response,
    string? ErrorMessage = null,
    string? Endpoint = null)
{
    public static NativeBridgeCallResult Ok(NativeBridgeResponse response, string endpoint) =>
        new(true, response, null, endpoint);

    public static NativeBridgeCallResult Failed(string message, string endpoint) =>
        new(false, null, message, endpoint);
}

/// <summary>
/// Named-pipe client for the AutoShop native bridge (JSON line protocol).
///
/// Security posture (mirrors ExternalWorkerProtocol):
/// - only the local machine and the fixed pipe name are used;
/// - only methods in <see cref="NativeBridgeProtocol.AllowedMethods"/> are sent;
/// - every call is bounded by a timeout and a cancellation token; timeouts and
///   disconnects surface as failures, never as successful empty results;
/// - the client never writes to any PLC: the protocol itself has no write
///   method, and the dangerous-operation guard is enforced again in
///   <see cref="NativeBridgeWorker"/>.
/// </summary>
public sealed class NativeBridgePipeClient : IAsyncDisposable
{
    private readonly NativeBridgeClientOptions _options;
    private readonly string _endpoint;
    private Stream? _stream;
    private readonly byte[] _readBuffer = new byte[4096];
    private readonly StringBuilder _lineBuffer = new();
    private int _requestCounter;
    private bool _disposed;

    public NativeBridgePipeClient(NativeBridgeClientOptions? options = null)
    {
        _options = options ?? new NativeBridgeClientOptions();
        _endpoint = $"{_options.ServerName}\\{_options.PipeName}";
    }

    /// <summary>Endpoint description used in diagnostics ("." + pipe name).</summary>
    public string Endpoint => _endpoint;

    /// <summary>True once the transport is connected.</summary>
    public bool IsConnected => _stream != null && !_disposed;

    /// <summary>
    /// Connects the transport. With the default options this opens the local
    /// named pipe; tests inject a transport factory instead.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(NativeBridgePipeClient));

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
    /// The method must be in the protocol allowlist; anything else is refused
    /// locally without touching the pipe.
    /// </summary>
    public async Task<NativeBridgeCallResult> CallAsync(
        string method,
        NativeBridgeRequestParams? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(NativeBridgePipeClient));

        if (!NativeBridgeProtocol.AllowedMethods.Contains(method))
        {
            return NativeBridgeCallResult.Failed(
                $"Method '{method}' is not part of the read-only native bridge protocol.", _endpoint);
        }

        if (_stream == null)
        {
            return NativeBridgeCallResult.Failed("Bridge transport is not connected.", _endpoint);
        }

        string id = Interlocked.Increment(ref _requestCounter).ToString();
        var request = new NativeBridgeRequest(id, method, parameters);
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
                return NativeBridgeCallResult.Failed(
                    "Bridge closed the connection before answering.", _endpoint);
            }

            NativeBridgeResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<NativeBridgeResponse>(responseLine);
            }
            catch (JsonException ex)
            {
                return NativeBridgeCallResult.Failed(
                    $"Bridge response is not valid JSON: {ex.Message}", _endpoint);
            }

            if (response == null || (response.Result == null && response.Error == null))
            {
                return NativeBridgeCallResult.Failed(
                    "Bridge response carries neither result nor error.", _endpoint);
            }

            if (response.Error != null)
            {
                return NativeBridgeCallResult.Failed(
                    $"Bridge error [{response.Error.Code}]: {response.Error.Message}", _endpoint);
            }

            return NativeBridgeCallResult.Ok(response, _endpoint);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return NativeBridgeCallResult.Failed(
                $"Native bridge call '{method}' timed out after {_options.Timeout.TotalSeconds}s.", _endpoint);
        }
        catch (IOException ex)
        {
            return NativeBridgeCallResult.Failed(
                $"Native bridge transport failure: {ex.Message}", _endpoint);
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
