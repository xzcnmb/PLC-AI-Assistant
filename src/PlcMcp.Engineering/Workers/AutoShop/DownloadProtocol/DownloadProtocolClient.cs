using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PlcMcp.Engineering.Workers.AutoShop.DownloadProtocol;

/// <summary>
/// Outcome of a protocol exchange that carries a daemon status code.
/// </summary>
public enum DownloadProtocolStatus
{
    Ok = 0,
    BadLength = 0x20,
    ProbeAnswer = 0x21,
    BadParaLength = 0x40,
    NotAuthorized = 0x41,
    WrongPassword = 0x43,
    ChunkOutOfOrder = 0x60,
    SessionNotFound = 0x61,
    LoadRestart = 0x82,
    SessionBusy = 0x83,
    OpenFailed = 0x84,
    AlreadyComplete = 0x85,
    AllocFailed = 0x86,
    LoadStatusOutOfRange = 0x87,
    /// <summary>The daemon sent no response within the timeout (transport failure, never success).</summary>
    NoResponse = unchecked((int)0xFFFF_FFFF),
}

/// <summary>Result of one file-transfer transaction against the local simulator daemon.</summary>
public sealed record DownloadFileResult(
    bool Ok,
    DownloadProtocolStatus Status,
    string Detail,
    byte[]? Content = null)
{
    public static DownloadFileResult FromStatus(DownloadProtocolStatus status, string detail) =>
        new(status == DownloadProtocolStatus.Ok, status, detail);
}

/// <summary>Version information read from the daemon (register-chain command 0xFF100000).</summary>
public sealed record DaemonVersionInfo(byte[] Raw);

/// <summary>
/// Client for the AutoShop H5U simulator's native protocol on TCP 127.0.0.1:12939.
///
/// Scope and safety (reverse-engineered protocol, no vendor endorsement; see
/// docs/inovance-autoshop-download-protocol.md):
/// - <b>Loopback-only by construction</b>: every connect hard-validates that the resolved endpoint
///   is a loopback address; any non-loopback target throws before a socket is created. Download,
///   upload and login frames can therefore never be sent to a physical PLC from this client.
/// - Only verified exchanges are implemented: probe, version read (register chain, read-only),
///   login/logout (0x80 chain), file download/upload/finish (file-transfer chain).
/// - Not implemented (untested against the daemon, deliberately absent): RUN/STOP simulator
///   control (0xFF000020), program-load polling (0xFF000010), element read/write (server id 3).
/// - Timeouts and bad frames surface as failures; a timeout is never converted into success.
/// </summary>
public sealed class DownloadProtocolClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;
    private readonly Func<string, int, TimeSpan, CancellationToken, Task<Stream>> _connect;
    private Stream? _stream;
    private bool _disposed;

    public string Endpoint => $"{_host}:{_port}";

    public bool IsLoggedIn { get; private set; }

    private DownloadProtocolClient(string host, int port, TimeSpan timeout,
        Func<string, int, TimeSpan, CancellationToken, Task<Stream>> connect)
    {
        _host = host;
        _port = port;
        _timeout = timeout;
        _connect = connect;
    }

    /// <summary>
    /// Creates a client. The host must resolve to a loopback address (127.0.0.0/8 or ::1) —
    /// this is the hard safety gate that keeps every frame (including download payloads) on the
    /// local machine. The port defaults to the simulator's native port 12939.
    /// </summary>
    public static DownloadProtocolClient Create(string host, int? port = null, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Download protocol host cannot be empty.", nameof(host));
        }
        int effectivePort = port ?? DownloadFrame.DefaultPort;
        if (effectivePort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), effectivePort, "Port must be within 1..65535.");
        }

        // Hard loopback gate: resolve now so a hostname that maps to a non-loopback address is
        // rejected before any connection attempt.
        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Download protocol host '{host}' could not be resolved; only loopback endpoints are allowed.", ex);
        }

        bool loopback = addresses.Length > 0 && addresses.All(a => IPAddress.IsLoopback(a));
        if (!loopback)
        {
            throw new InvalidOperationException(
                $"Download protocol target must be a loopback address (127.0.0.1 / ::1); '{host}' resolves to " +
                string.Join(", ", addresses.Select(a => a.ToString())) +
                ". Download frames are never sent to a non-loopback endpoint.");
        }

        return new DownloadProtocolClient(host, effectivePort, timeout ?? TimeSpan.FromSeconds(5), ConnectTcpAsync);
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

    /// <summary>Opens the TCP connection to the daemon.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is not null)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        _stream = await _connect(_host, _port, _timeout, cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the probe frame (version byte 0xFF &gt; 2) and expects the daemon's 8-byte answer
    /// with ServerId 0xFF. Read-only; no side effects. The answer's length word is stale
    /// (kept from the request), so this reads the raw byte count instead of the length word.
    /// </summary>
    public async Task<DownloadProtocolStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        // The probe answer is always exactly 8 bytes and its length word is stale (kept from the
        // request), so this reads the raw byte count instead of trusting the length word.
        // A probe frame shorter than 8 payload-free bytes can hit the daemon's zero-length recv
        // path; the captured exchange used a 10-byte frame (8 header + 2 payload bytes).
        byte[] answer = await ExchangeRawAsync(
            DownloadFrame.Build(DownloadFrame.ProbeVersion, 0, new byte[2]),
            expectedAnswerLength: 8,
            cancellationToken).ConfigureAwait(false);
        return answer.Length == 8 && answer[2] == DownloadFrame.ProtocolVersion && answer[3] == 0xFF
            ? DownloadProtocolStatus.ProbeAnswer
            : DownloadProtocolStatus.NoResponse;
    }

    /// <summary>
    /// Reads the daemon's version information (register chain, command 0xFF100000, 240 bytes).
    /// Read-only. Verified against the local daemon: answer payload [0x05][240 bytes].
    /// </summary>
    public async Task<DaemonVersionInfo> ReadVersionAsync(CancellationToken cancellationToken = default)
    {
        var payload = new byte[10];
        payload[0] = 0x01; // register-chain sub-type: read
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), DownloadFrame.RegisterCommands.VersionInfoRead);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6), 0xF0);

        byte[] answer = await ExchangeAsync(
            DownloadFrame.Build(DownloadFrame.ProtocolVersion, DownloadFrame.ServerIds.Registers, payload),
            cancellationToken).ConfigureAwait(false);
        if (answer.Length < 2 || answer[0] != 0x05)
        {
            throw new IOException(
                $"Version read rejected by the daemon (status {(answer.Length >= 6 ? BinaryPrimitives.ReadUInt32LittleEndian(answer.AsSpan(2, 4)) : 0):X}).");
        }
        return new DaemonVersionInfo(answer.Skip(2).ToArray());
    }

    /// <summary>
    /// Logs in on the 0x80 chain. Verified: the simulator's check_userlogin_data stub accepts any
    /// credentials; the answer payload is [0x06] on success, [0x0F][0x43] on a wrong password.
    /// The authorized flag is per-connection — login and the following file transfer must run on
    /// the same client instance.
    /// </summary>
    public async Task<DownloadProtocolStatus> LoginAsync(string user, string password, CancellationToken cancellationToken = default)
    {
        var payload = new byte[66];
        payload[0] = DownloadFrame.LoginSubTypes.Login;
        Encoding.ASCII.GetBytes(user ?? string.Empty).AsSpan(0, Math.Min(32, user?.Length ?? 0)).CopyTo(payload.AsSpan(2));
        Encoding.ASCII.GetBytes(password ?? string.Empty).AsSpan(0, Math.Min(32, password?.Length ?? 0)).CopyTo(payload.AsSpan(34));

        byte[] answer = await ExchangeAsync(
            DownloadFrame.Build(DownloadFrame.ProtocolVersion, DownloadFrame.ServerIds.Login, payload),
            cancellationToken).ConfigureAwait(false);
        if (answer.Length >= 1 && answer[0] == 0x06)
        {
            IsLoggedIn = true;
            return DownloadProtocolStatus.Ok;
        }
        return ToStatus(answer);
    }

    /// <summary>Logs out (clears the per-connection authorized flag).</summary>
    public async Task<DownloadProtocolStatus> LogoutAsync(CancellationToken cancellationToken = default)
    {
        var payload = new byte[2];
        payload[0] = DownloadFrame.LoginSubTypes.Logout;
        byte[] answer = await ExchangeAsync(
            DownloadFrame.Build(DownloadFrame.ProtocolVersion, DownloadFrame.ServerIds.Login, payload),
            cancellationToken).ConfigureAwait(false);
        IsLoggedIn = false;
        return ToStatus(answer);
    }

    /// <summary>
    /// Queries the per-connection login state (monitor chain, sub-type 3). Verified on the
    /// daemon: the answer payload is [0x03][result] with result 1 when authorized. Read-only
    /// (unlike sub-type 2, which clears the authorized flag daemon-side).
    /// </summary>
    public async Task<bool> QueryLoginStateAsync(CancellationToken cancellationToken = default)
    {
        var payload = new byte[6];
        payload[0] = DownloadFrame.MonitorSubTypes.QueryLoginState;
        byte[] answer = await ExchangeAsync(
            DownloadFrame.Build(DownloadFrame.ProtocolVersion, DownloadFrame.ServerIds.Monitor, payload),
            cancellationToken).ConfigureAwait(false);
        return answer.Length >= 6 && BinaryPrimitives.ReadUInt32LittleEndian(answer.AsSpan(2, 4)) == 1;
    }

    /// <summary>
    /// Downloads (writes) a file into the daemon's working directory via a verified file-transfer
    /// transaction: download-session command → data chunks → finish. The filename must be relative
    /// to the daemon's app directory. Protected file types (.hcpp/.foid) require a prior successful
    /// <see cref="LoginAsync"/> on the same connection; unprotected types do not.
    /// </summary>
    /// <param name="filename">Relative file name, e.g. "project\project.hcpp" or "stprog.o".</param>
    /// <param name="content">Exact bytes to write.</param>
    /// <param name="chunkSize">Chunk length carried in the session command (payload ≤ 1023).</param>
    public async Task<DownloadFileResult> DownloadFileAsync(
        string filename, ReadOnlyMemory<byte> content, int chunkSize = 128, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, DownloadFrame.MaxPayloadLength);

        // 1. Session command: [02][0][resv 8][chunkSize@0xA][total@0xE][filename@0x12].
        var command = new byte[0x12 + filename.Length + 1];
        command[0] = DownloadFrame.FilePayloadTypes.DownloadSession;
        BinaryPrimitives.WriteInt32LittleEndian(command.AsSpan(0x0A), chunkSize);
        BinaryPrimitives.WriteInt32LittleEndian(command.AsSpan(0x0E), content.Length);
        Encoding.ASCII.GetBytes(filename).CopyTo(command.AsSpan(0x12));

        byte[] answer = await ExchangeAsync(
            DownloadFrame.Build(DownloadFrame.ProtocolVersion, DownloadFrame.ServerIds.FileTransfer, command),
            cancellationToken).ConfigureAwait(false);
        if (answer.Length < 1 || answer[0] != DownloadFrame.FilePayloadTypes.UploadRead)
        {
            return DownloadFileResult.FromStatus(ToStatus(answer), "Download-session command rejected by the daemon.");
        }

        // 2. Data chunks: [05][0][index@2][len@6][data@0xA]; the daemon acks with [06][index].
        int index = 0;
        for (int offset = 0; offset < content.Length; offset += chunkSize)
        {
            int length = Math.Min(chunkSize, content.Length - offset);
            var packet = new byte[0x0A + length];
            packet[0] = DownloadFrame.FilePayloadTypes.DataChunk;
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(2), index);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(6), length);
            content.Span.Slice(offset, length).CopyTo(packet.AsSpan(0x0A));

            byte[] ack = await ExchangeAsync(
                DownloadFrame.Build(DownloadFrame.ProtocolVersion, DownloadFrame.ServerIds.FileTransfer, packet),
                cancellationToken).ConfigureAwait(false);
            if (ack.Length < 1 || ack[0] != DownloadFrame.FilePayloadTypes.UploadRead)
            {
                return DownloadFileResult.FromStatus(ToStatus(ack), $"Data chunk {index} rejected by the daemon.");
            }
            index++;
        }

        // 3. Finish: [0F]; the daemon closes the session and never answers this frame.
        byte[] finish = DownloadFrame.Build(
            DownloadFrame.ProtocolVersion, DownloadFrame.ServerIds.FileTransfer,
            new byte[] { DownloadFrame.FilePayloadTypes.Finish, 0, 0, 0, 0, 0 });
        await SendAsync(finish, cancellationToken).ConfigureAwait(false);
        return new DownloadFileResult(true, DownloadProtocolStatus.Ok, $"{filename}: {content.Length} bytes transferred.");
    }

    /// <summary>
    /// Uploads (reads) a file from the daemon's working directory: upload-session command →
    /// chunk requests → finish. Verified: the daemon answers each request with
    /// [0x05][index][len][data] and the session command echoes the actual file size.
    /// </summary>
    public async Task<DownloadFileResult> UploadFileAsync(
        string filename, int chunkSize = 128, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, DownloadFrame.MaxPayloadLength);

        // Verified against the daemon: upload and download session commands share the same field
        // positions — chunk size at payload+0x0A, total/pad at payload+0x0E (the upload's total
        // comes from ftell, so the field is unused), filename at payload+0x12.
        var command = new byte[0x12 + filename.Length + 1];
        command[0] = DownloadFrame.FilePayloadTypes.UploadSession;
        BinaryPrimitives.WriteInt32LittleEndian(command.AsSpan(0x0A), chunkSize);
        Encoding.ASCII.GetBytes(filename).CopyTo(command.AsSpan(0x12));

        byte[] answer = await ExchangeAsync(
            DownloadFrame.Build(DownloadFrame.ProtocolVersion, DownloadFrame.ServerIds.FileTransfer, command),
            cancellationToken).ConfigureAwait(false);
        if (answer.Length < 6 || answer[0] != DownloadFrame.FilePayloadTypes.UploadRead)
        {
            return DownloadFileResult.FromStatus(ToStatus(answer), "Upload-session command rejected by the daemon.");
        }
        int total = BinaryPrimitives.ReadInt32LittleEndian(answer.AsSpan(2, 4));

        var content = new MemoryStream();
        for (int index = 0; index * chunkSize < total || (total == 0 && index == 0); index++)
        {
            var request = new byte[10];
            request[0] = DownloadFrame.FilePayloadTypes.UploadRead;
            BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(2), index);

            byte[] data = await ExchangeAsync(
                DownloadFrame.Build(DownloadFrame.ProtocolVersion, DownloadFrame.ServerIds.FileTransfer, request),
                cancellationToken).ConfigureAwait(false);
            if (data.Length < 10 || data[0] != DownloadFrame.FilePayloadTypes.DataChunk)
            {
                return DownloadFileResult.FromStatus(ToStatus(data), $"Chunk request {index} rejected by the daemon.");
            }
            int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(6, 4));
            content.Write(data, 0x0A, length);
            if (length < chunkSize || content.Length >= total)
            {
                break;
            }
        }

        byte[] finish = DownloadFrame.Build(
            DownloadFrame.ProtocolVersion, DownloadFrame.ServerIds.FileTransfer,
            new byte[] { DownloadFrame.FilePayloadTypes.Finish, 0, 0, 0, 0, 0 });
        await SendAsync(finish, cancellationToken).ConfigureAwait(false);
        return new DownloadFileResult(true, DownloadProtocolStatus.Ok,
            $"{filename}: {content.Length} of {total} bytes read.", content.ToArray());
    }

    /// <summary>Reads the bytes uploaded from the daemon by the last <see cref="UploadFileAsync"/>.</summary>
    public static byte[] GetUploadedBytes(DownloadFileResult result, MemoryStream stream) => stream.ToArray();

    private static DownloadProtocolStatus ToStatus(byte[] answer)
    {
        if (answer is null || answer.Length == 0)
        {
            return DownloadProtocolStatus.NoResponse;
        }
        // Login/logout answers are bare [0x06] payloads (frame length 9).
        if (answer[0] == 0x06)
        {
            return DownloadProtocolStatus.Ok;
        }
        if (answer.Length >= 6 && answer[0] == DownloadFrame.FilePayloadTypes.Error)
        {
            uint status = BinaryPrimitives.ReadUInt32LittleEndian(answer.AsSpan(2, 4));
            return Enum.IsDefined(typeof(DownloadProtocolStatus), unchecked((int)status))
                ? (DownloadProtocolStatus)status
                : DownloadProtocolStatus.NoResponse;
        }
        return DownloadProtocolStatus.NoResponse;
    }

    private async Task SendAsync(byte[] frame, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stream stream = _stream ?? throw new InvalidOperationException("Not connected; call ConnectAsync first.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        await stream.WriteAsync(frame, cts.Token).ConfigureAwait(false);
        await stream.FlushAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a frame and reads exactly one answer frame using the length word. Daemon exchanges
    /// always answer with a consistent length word; only the probe answer keeps it stale
    /// (handled by <paramref name="expectProbeAnswer"/>).
    /// </summary>
    private async Task<byte[]> ExchangeAsync(byte[] frame, CancellationToken cancellationToken, int? expectedAnswerLength = null)
    {
        await SendAsync(frame, cancellationToken).ConfigureAwait(false);
        byte[] answer = await ReadAnswerAsync(expectedAnswerLength, cancellationToken).ConfigureAwait(false);
        return answer.Length > DownloadFrame.HeaderLength
            ? answer[DownloadFrame.HeaderLength..]
            : answer;
    }

    private async Task<byte[]> ExchangeRawAsync(byte[] frame, int? expectedAnswerLength, CancellationToken cancellationToken) =>
        await ExchangeAsync(frame, cancellationToken, expectedAnswerLength).ConfigureAwait(false);

    private async Task<byte[]> ReadAnswerAsync(int? expectedAnswerLength, CancellationToken cancellationToken)
    {
        Stream stream = _stream ?? throw new InvalidOperationException("Not connected; call ConnectAsync first.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        int total;
        byte[]? consumedHeader = null;
        if (expectedAnswerLength.HasValue)
        {
            // Probe-style exchange: the daemon sends a fixed byte count and keeps the length word
            // stale, so the word cannot be trusted — read exactly the expected count.
            total = expectedAnswerLength.Value;
        }
        else
        {
            var header = new byte[2];
            await ReadExactlyAsync(stream, header, cts.Token).ConfigureAwait(false);
            ushort declared = BinaryPrimitives.ReadUInt16LittleEndian(header);
            if (declared < DownloadFrame.HeaderLength || declared > DownloadFrame.MaxFrameLength)
            {
                throw new IOException($"Daemon answered with an invalid frame length 0x{declared:X}.");
            }
            total = declared;
            consumedHeader = header; // these two bytes are part of the frame
        }

        var body = new byte[total];
        if (consumedHeader is not null)
        {
            body[0] = consumedHeader[0];
            body[1] = consumedHeader[1];
            await ReadExactlyAsync(stream, body.AsMemory(2), cts.Token).ConfigureAwait(false);
        }
        else
        {
            await ReadExactlyAsync(stream, body, cts.Token).ConfigureAwait(false);
        }
        return body;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.Slice(read), cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new EndOfStreamException("The daemon closed the connection before a complete frame arrived.");
            }
            read += n;
        }
    }

    /// <summary>Closes the connection without sending anything.</summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
        _stream?.Dispose();
        _stream = null;
        return ValueTask.CompletedTask;
    }
}
