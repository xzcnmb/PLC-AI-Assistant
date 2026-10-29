using System.Net.Sockets;

namespace PlcMcp.Engineering.Workers.AutoShop;

/// <summary>
/// Read-only client for the InoVarSharedService ITP protocol on the local loopback endpoint.
///
/// Scope and safety:
/// - Only ever constructs ITP packet type 1 (register handshake) and reads the answer.
///   No variable-write, force, download, compile, run/stop or service-control path exists in this client.
/// - Loopback-only by default; the caller supplies host/port (both injectable for tests).
/// - Every read is bounded by the caller's timeout/cancellation; a timeout is surfaced as
///   <see cref="TimeoutException"/> and never converted into a successful value.
/// </summary>
public sealed class ItpClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;
    private readonly Func<TimeSpan, CancellationToken, Task<Stream>> _connect;
    private Stream? _stream;
    private bool _disposed;

    public string Endpoint => $"{_host}:{_port}";

    public bool IsConnected => _stream is not null && !_disposed;

    private ItpClient(string host, int port, TimeSpan timeout, Func<TimeSpan, CancellationToken, Task<Stream>> connect)
    {
        _host = host;
        _port = port;
        _timeout = timeout;
        _connect = connect;
    }

    public static ItpClient Create(string host, int port, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("ITP host cannot be empty.", nameof(host));
        }
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "ITP port must be within 1..65535.");
        }

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);
        return new ItpClient(host, port, effectiveTimeout, (t, ct) => ConnectTcpAsync(host, port, t, ct));
    }

    private static async Task<Stream> ConnectTcpAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        // NetworkStream from TcpClient owns the socket; disposing the stream closes the connection.
        return client.GetStream();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is not null)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        _stream = await _connect(_timeout, cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs the register handshake for the supplied path and returns the service answer.
    /// Read-only: the only payload ever sent is { "Path": ... }.
    /// </summary>
    public async Task<ItpHandshakeResult> RegisterAsync(string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is null)
        {
            throw new InvalidOperationException("ITP client is not connected. Call ConnectAsync first.");
        }

        var requestPayload = new ItpRegisterRequest(path ?? string.Empty).SerializePayload();
        var packet = ItpPacket.Create(ItpPacketType.Register, requestPayload);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        try
        {
            await _stream.WriteAsync(packet.ToBytes(), cts.Token).ConfigureAwait(false);
            await _stream.FlushAsync(cts.Token).ConfigureAwait(false);

            ItpPacket answer = await ReadPacketAsync(cts.Token).ConfigureAwait(false);
            var response = ItpRegisterResponse.Parse(answer.Payload);
            string message = string.Equals(response.Response, ItpRegisterResponse.ResponseQueryFail, StringComparison.Ordinal)
                ? "Service answered the register handshake but reports no shared-variable publisher for this path (Response=3; service log: 'Query Fail,ResponseMsg = 3')."
                : $"Service answered the register handshake with Response='{response.Response}'.";
            return new ItpHandshakeResult(true, true, response.Response, message, Endpoint);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"ITP register handshake against {Endpoint} timed out after {_timeout.TotalSeconds:F1}s.");
        }
    }

    /// <summary>
    /// Reads one complete ITP packet from the stream. Used for handshake answers; would also be used
    /// for pushed data packets (format unverified; this bridge does not decode them).
    /// </summary>
    public async Task<ItpPacket> ReadPacketAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is null)
        {
            throw new InvalidOperationException("ITP client is not connected. Call ConnectAsync first.");
        }

        byte[] headerBuffer = await ReadExactlyAsync(ItpPacketHeader.HeaderSize, cancellationToken).ConfigureAwait(false);
        if (!ItpPacketHeader.TryParse(headerBuffer, out ItpPacketHeader header))
        {
            throw new InvalidDataException("ITP response header magic/length invalid; not an ITP packet.");
        }

        byte[] payload = header.DataLength == 0
            ? Array.Empty<byte>()
            : await ReadExactlyAsync(header.DataLength, cancellationToken).ConfigureAwait(false);

        return new ItpPacket { Header = header, Payload = payload };
    }

    private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await _stream!.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException($"ITP stream closed after {read} of {count} bytes.");
            }
            read += n;
        }
        return buffer;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_stream is not null)
        {
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best effort close
            }
            _stream = null;
        }
    }
}
