// G2 download-closed-loop: minimal loopback-only client for the h5u simulator
// register chain (serverId 1) run/stop/load commands.
//
// Companion of the D1 DownloadProtocolClient (file transfer, 12939). This
// client speaks the byte-verified frames from SimulatorControlFrames — the
// shapes proven against a local daemon on 2026-10-29 where the RUN write moved
// Modbus D100 from 0 to 4660 (docs/inovance-autoshop-download-closedloop.md §4).
//
// Red line: the factory resolves the host and refuses any non-loopback address
// BEFORE connecting, mirroring DownloadProtocolClient; frames never leave
// 127.0.0.0/8. Reverse-engineered, no vendor endorsement, version-locked to
// AutoShop 4.12.0.2 simulator binaries.

using System.Net;
using System.Net.Sockets;
using PlcMcp.Engineering.Workers.AutoShop.DownloadProtocol;

namespace PlcMcp.Engineering.Workers.AutoShop.InjectBridge;

/// <summary>
/// Testable seam: one request frame in, one raw answer payload (post-header)
/// out. The real implementation uses a loopback TCP socket; tests inject a
/// scripted dictionary of frame → answer bytes (D1 mock pattern).
/// </summary>
public interface ISimulatorControlChannel
{
    Task<SimulatorControlReply> SendAsync(byte[] frame, CancellationToken cancellationToken = default);
}

/// <summary>Raw loopback TCP channel for register-chain frames on 127.0.0.1:12939.</summary>
public sealed class SimulatorRegisterClient : ISimulatorControlChannel, IDisposable
{
    private readonly Func<string, int, TimeSpan, CancellationToken, Task<Stream>> _connect;
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;
    private Stream? _stream;
    private bool _disposed;

    private SimulatorRegisterClient(
        string host, int port, TimeSpan timeout,
        Func<string, int, TimeSpan, CancellationToken, Task<Stream>> connect)
    {
        _host = host;
        _port = port;
        _timeout = timeout;
        _connect = connect;
    }

    /// <summary>
    /// Creates a client against a hard-verified loopback endpoint. Any host that
    /// resolves to a non-loopback address is refused before connecting (red line).
    /// </summary>
    public static SimulatorRegisterClient Create(
        string host, int? port = null, TimeSpan? timeout = null,
        Func<string, int, TimeSpan, CancellationToken, Task<Stream>>? connect = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        int effectivePort = port ?? DownloadFrame.DefaultPort;
        if (effectivePort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), effectivePort, "Port must be within 1..65535.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Simulator control host '{host}' could not be resolved; only loopback endpoints are allowed.", ex);
        }
        if (addresses.Length == 0 || !addresses.All(IPAddress.IsLoopback))
        {
            throw new InvalidOperationException(
                $"Simulator control target must be a loopback address (127.0.0.1 / ::1); '{host}' resolves to " +
                string.Join(", ", addresses.Select(a => a.ToString())) +
                ". RUN/STOP frames are never sent to a non-loopback endpoint.");
        }

        return new SimulatorRegisterClient(
            host, effectivePort, timeout ?? TimeSpan.FromSeconds(5),
            connect ?? ConnectTcpAsync);
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
        return client.GetStream();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream ??= await _connect(_host, _port, _timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SimulatorControlReply> SendAsync(byte[] frame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var stream = _stream ?? throw new InvalidOperationException("not connected");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        await stream.WriteAsync(frame, cts.Token).ConfigureAwait(false);
        await stream.FlushAsync(cts.Token).ConfigureAwait(false);

        // Read one answer frame; honour its (well-formed for this chain) length word.
        var header = new byte[DownloadFrame.HeaderLength];
        await ReadExactlyAsync(stream, header, cts.Token).ConfigureAwait(false);
        int total = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header);
        int payloadLength = Math.Max(0, total - DownloadFrame.HeaderLength);
        var payload = new byte[payloadLength];
        if (payloadLength > 0)
        {
            await ReadExactlyAsync(stream, payload, cts.Token).ConfigureAwait(false);
        }
        return SimulatorControlFrames.ParseReply(payload);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("daemon closed the connection before a full answer frame");
            }
            read += n;
        }
    }

    /// <summary>Issue RUN (0xFF000020 ← 1).</summary>
    public Task<SimulatorControlReply> RunAsync(CancellationToken cancellationToken = default)
        => SendAsync(SimulatorControlFrames.BuildRun(), cancellationToken);

    /// <summary>Issue STOP (0xFF000020 ← 0).</summary>
    public Task<SimulatorControlReply> StopAsync(CancellationToken cancellationToken = default)
        => SendAsync(SimulatorControlFrames.BuildStop(), cancellationToken);

    /// <summary>Trigger program load (0xFF000010 ← 1). Simulator: answers stub status 0x82.</summary>
    public Task<SimulatorControlReply> TriggerLoadAsync(CancellationToken cancellationToken = default)
        => SendAsync(SimulatorControlFrames.BuildLoadTrigger(), cancellationToken);

    /// <summary>Poll program download status (0xFF000010 read).</summary>
    public Task<SimulatorControlReply> PollLoadStatusAsync(CancellationToken cancellationToken = default)
        => SendAsync(SimulatorControlFrames.BuildLoadPoll(), cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stream?.Dispose();
        _stream = null;
    }
}
