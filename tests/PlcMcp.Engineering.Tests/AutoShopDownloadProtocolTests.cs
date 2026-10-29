using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PlcMcp.Engineering.Workers.AutoShop.DownloadProtocol;

namespace PlcMcp.Engineering.Tests;

/// <summary>
/// Tests for the AutoShop H5U simulator 12939 download protocol (DownloadFrame / DownloadProtocolClient).
/// All tests are offline: the daemon is a loopback mock TCP server that replays the exact frames
/// captured on a real locally started h5u_daemon.exe instance. Real-daemon integration tests carry
/// the LocalSimulator trait and are skipped unless PLCMCP_LOCAL_SIMULATOR=1 (CI never needs them).
/// </summary>
public class AutoShopDownloadProtocolTests : IDisposable
{
    private readonly List<MockDaemonServer> _servers = [];

    public void Dispose()
    {
        foreach (MockDaemonServer server in _servers)
        {
            server.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private MockDaemonServer StartMock(params Func<byte[], byte[]?>[] responders)
    {
        var server = new MockDaemonServer(responders);
        _servers.Add(server);
        return server;
    }

    private static DownloadProtocolClient CreateClient(int port) =>
        DownloadProtocolClient.Create("127.0.0.1", port, TimeSpan.FromSeconds(2));

    // ---------------------------------------------------------------
    // Frame model: golden bytes captured on a real h5u_daemon instance
    // ---------------------------------------------------------------

    [Fact]
    public void Build_produces_the_captured_frame_bytes()
    {
        // Version read request captured on the daemon:
        // 12 00 | 02 | 01 | 00000000 | 01 00 | 00 00 10 FF | F0 00 00 00
        var payload = new byte[10];
        payload[0] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), DownloadFrame.RegisterCommands.VersionInfoRead);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6), 0xF0);

        byte[] frame = DownloadFrame.Build(DownloadFrame.ProtocolVersion, DownloadFrame.ServerIds.Registers, payload);

        Assert.Equal(
            "12000201000000000100000010fff0000000",
            Convert.ToHexString(frame).ToLowerInvariant());
    }

    [Fact]
    public void Build_download_session_matches_captured_bytes()
    {
        // Download-session command captured on the daemon (project\project.hcpp, total 7):
        // 26 00 | 02 | 04 | 00000000 | 02 00 | 00000000 00000000 | 40 00 00 00 | 07 00 00 00 | "project\project.hcpp\0"
        var command = new byte[0x12 + 20 + 1];
        command[0] = DownloadFrame.FilePayloadTypes.DownloadSession;
        BinaryPrimitives.WriteInt32LittleEndian(command.AsSpan(0x0A), 64);
        BinaryPrimitives.WriteInt32LittleEndian(command.AsSpan(0x0E), 7);
        Encoding.ASCII.GetBytes("project\\project.hcpp").CopyTo(command.AsSpan(0x12));

        byte[] frame = DownloadFrame.Build(DownloadFrame.ProtocolVersion, DownloadFrame.ServerIds.FileTransfer, command);

        // Structural assertions over the captured frame shape (47 = 0x2F bytes total:
        // 8 header + 2 type + 8 reserved + 4 chunk size + 4 total + 20 filename + 1 NUL).
        Assert.Equal(0x2F, frame.Length);
        Assert.Equal(0x002F, BinaryPrimitives.ReadUInt16LittleEndian(frame));
        Assert.Equal(DownloadFrame.ProtocolVersion, frame[2]);
        Assert.Equal(DownloadFrame.ServerIds.FileTransfer, frame[3]);
        Assert.Equal<byte>(new byte[4], frame[4..8]);
        Assert.Equal(DownloadFrame.FilePayloadTypes.DownloadSession, frame[8]);
        Assert.Equal(64, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(0x12, 4)));   // chunk size at payload+0xA
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(0x16, 4)));    // total at payload+0xE
        Assert.Equal("project\\project.hcpp", Encoding.ASCII.GetString(frame, 0x1A, 20)); // filename at payload+0x12
        Assert.Equal(0, frame[^1]);                                                        // NUL terminator
    }

    [Fact]
    public void TryParse_round_trips_a_captured_answer()
    {
        // Version read answer captured on the daemon (250 bytes, payload [05][240 bytes]).
        var frame = new byte[250];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, 250);
        frame[2] = 0x02;
        frame[3] = 0x01;
        frame[8] = 0x05;

        Assert.True(DownloadFrame.TryParse(frame, out DownloadFrameView view));
        Assert.Equal(250, view.TotalLength);
        Assert.Equal(0x02, view.Version);
        Assert.Equal(0x01, view.ServerId);
        Assert.Equal<byte>(new byte[4], view.Reserved);
        Assert.Equal(242, view.Payload.Length);
        Assert.Equal((byte)0x05, view.PayloadType);
        Assert.Null(view.ErrorStatus);
    }

    [Fact]
    public void TryParse_rejects_length_mismatch_and_short_buffers()
    {
        var frame = new byte[14];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, 20); // stale/mismatched length word

        Assert.False(DownloadFrame.TryParse(frame, out _));
        Assert.False(DownloadFrame.TryParse(new byte[4], out _));
        Assert.False(DownloadFrame.TryParse(Array.Empty<byte>(), out _));
    }

    [Fact]
    public void TryParse_extracts_error_status_from_error_payload()
    {
        // Error answer captured on the daemon: 0E 00 | 02 | 04 | 00000000 | 0F 00 | 41 00 00 00
        byte[] frame =
        [
            0x0E, 0x00, 0x02, 0x04, 0x00, 0x00, 0x00, 0x00,
            0x0F, 0x00, 0x41, 0x00, 0x00, 0x00,
        ];

        Assert.True(DownloadFrame.TryParse(frame, out DownloadFrameView view));
        Assert.Equal((uint)DownloadProtocolStatus.NotAuthorized, view.ErrorStatus);
    }

    [Fact]
    public void Build_rejects_payloads_over_the_daemon_limit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DownloadFrame.Build(DownloadFrame.ProtocolVersion, 4, new byte[DownloadFrame.MaxFrameLength]));
    }

    // ---------------------------------------------------------------
    // Loopback safety gate
    // ---------------------------------------------------------------

    [Fact]
    public void Create_rejects_non_loopback_hosts_before_any_connection()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DownloadProtocolClient.Create("192.168.1.1"));
        Assert.Contains("loopback", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Throws<InvalidOperationException>(() => DownloadProtocolClient.Create("example.com"));
        Assert.Throws<InvalidOperationException>(() => DownloadProtocolClient.Create("10.0.0.5"));
        Assert.Throws<ArgumentException>(() => DownloadProtocolClient.Create(" "));
    }

    [Fact]
    public void Create_accepts_loopback_hosts()
    {
        var client = DownloadProtocolClient.Create("127.0.0.1", 12939);
        Assert.Equal("127.0.0.1:12939", client.Endpoint);
        client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var client2 = DownloadProtocolClient.Create("localhost", 12939);
        Assert.Equal("localhost:12939", client2.Endpoint);
        client2.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    // ---------------------------------------------------------------
    // Probe / version read / login against a mock daemon (captured frames)
    // ---------------------------------------------------------------

    [Fact]
    public async Task Probe_reads_the_captured_8byte_answer_with_stale_length_word()
    {
        // Real daemon answer to a 10-byte probe: 0A 00 | 02 | FF | 00000000 (8 bytes, stale word).
        MockDaemonServer server = StartMock(_ => [0x0A, 0x00, 0x02, 0xFF, 0x00, 0x00, 0x00, 0x00]);
        await using var client = CreateClient(server.Port);
        await client.ConnectAsync();

        Assert.Equal(DownloadProtocolStatus.ProbeAnswer, await client.ProbeAsync());
        Assert.Equal(10, server.Requests[0].Length); // probe frame was 10 bytes (8 header + 2 empty payload)
        Assert.Equal(0xFF, server.Requests[0][2]);
    }

    [Fact]
    public async Task Probe_reports_timeout_as_failure_not_success()
    {
        MockDaemonServer server = StartMock(_ => null); // silent daemon
        await using var client = CreateClient(server.Port);
        await client.ConnectAsync();

        // The client's read path treats a missing answer as a failure (EndOfStream/IOException/timeout).
        await Assert.ThrowsAnyAsync<Exception>(() => client.ProbeAsync());
        Assert.False(client.IsLoggedIn);
    }

    [Fact]
    public async Task ReadVersion_parses_the_captured_250byte_answer()
    {
        // Captured answer: 250 bytes, ver 2, server id 1, payload [05][240 bytes].
        var answer = new byte[250];
        BinaryPrimitives.WriteUInt16LittleEndian(answer, 250);
        answer[2] = 0x02;
        answer[3] = 0x01;
        answer[8] = 0x05;
        MockDaemonServer server = StartMock(_ => answer);

        await using var client = CreateClient(server.Port);
        await client.ConnectAsync();
        DaemonVersionInfo version = await client.ReadVersionAsync();

        Assert.Equal(240, version.Raw.Length);
        Assert.Equal(18, server.Requests[0].Length); // 8 header + 10 payload
        Assert.Equal(DownloadFrame.ServerIds.Registers, server.Requests[0][3]);
    }

    [Fact]
    public async Task ReadVersion_surfaces_rejection_instead_of_fake_success()
    {
        // Error answer: [0F][0x40] = bad para length.
        byte[] answer = [0x0E, 0x00, 0x02, 0x01, 0x00, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x40, 0x00, 0x00, 0x00];
        MockDaemonServer server = StartMock(_ => answer);

        await using var client = CreateClient(server.Port);
        await client.ConnectAsync();

        await Assert.ThrowsAsync<IOException>(() => client.ReadVersionAsync());
    }

    [Fact]
    public async Task Login_accepts_captured_answer_and_sets_state()
    {
        // Captured login answer: 09 00 | 02 | 80 | 00000000 | 06 (payload [06] = OK).
        MockDaemonServer server = StartMock(_ => [0x09, 0x00, 0x02, 0x80, 0x00, 0x00, 0x00, 0x00, 0x06]);

        await using var client = CreateClient(server.Port);
        await client.ConnectAsync();

        Assert.Equal(DownloadProtocolStatus.Ok, await client.LoginAsync("plcmcp", "0"));
        Assert.True(client.IsLoggedIn);

        byte[] request = server.Requests[0];
        Assert.Equal(74, request.Length); // 8 header + 66 payload
        Assert.Equal(DownloadFrame.ServerIds.Login, request[3]);
        Assert.Equal(DownloadFrame.LoginSubTypes.Login, request[8]);
        // user at payload+2, password at payload+34 (32 bytes each)
        Assert.Equal("plcmcp", Encoding.ASCII.GetString(request, 10, 6));
    }

    [Fact]
    public async Task Login_reports_wrong_password_and_never_fakes_success()
    {
        // Error answer: [0F][0x43] = wrong password.
        byte[] answer = [0x0E, 0x00, 0x02, 0x80, 0x00, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x43, 0x00, 0x00, 0x00];
        MockDaemonServer server = StartMock(_ => answer);

        await using var client = CreateClient(server.Port);
        await client.ConnectAsync();

        Assert.Equal(DownloadProtocolStatus.WrongPassword, await client.LoginAsync("plcmcp", "bad"));
        Assert.False(client.IsLoggedIn);
    }

    [Fact]
    public async Task Login_timeout_does_not_set_logged_in()
    {
        MockDaemonServer server = StartMock(_ => null);
        await using var client = CreateClient(server.Port);
        await client.ConnectAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => client.LoginAsync("plcmcp", "0"));
        Assert.False(client.IsLoggedIn);
    }

    // ---------------------------------------------------------------
    // Download transaction against a mock daemon (captured ack frames)
    // ---------------------------------------------------------------

    [Fact]
    public async Task DownloadFile_runs_the_full_verified_transaction()
    {
        // Captured exchange for "project\\project.hcpp" (7 bytes, chunk 64):
        // cmd ack   : 0E 00 | 02 | 04 | 00000000 | 06 00 | 07 00 00 00
        // data acks : 0E 00 | 02 | 04 | 00000000 | 06 00 | index
        // finish    : the daemon never answers.
        byte[] cmdAck = [0x0E, 0x00, 0x02, 0x04, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x07, 0x00, 0x00, 0x00];
        byte[] dataAck(int index) =>
            [0x0E, 0x00, 0x02, 0x04, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, (byte)index, 0x00, 0x00, 0x00];

        List<byte[]> sent = [];
        MockDaemonServer server = StartMock(request =>
        {
            sent.Add(request);
            if (request[8] == DownloadFrame.FilePayloadTypes.DownloadSession)
            {
                return cmdAck;
            }
            if (request[8] == DownloadFrame.FilePayloadTypes.DataChunk)
            {
                return dataAck(BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(10, 4)));
            }
            return null; // finish: no answer
        });

        await using var client = CreateClient(server.Port);
        await client.ConnectAsync();
        DownloadFileResult result = await client.DownloadFileAsync("project\\project.hcpp", "Empty\r\n"u8.ToArray(), 64);

        Assert.True(result.Ok);
        Assert.Equal(DownloadProtocolStatus.Ok, result.Status);
        // The finish frame has no answer, so the mock records it asynchronously; wait boundedly.
        for (int waits = 0; waits < 50 && sent.Count < 3; waits++)
        {
            await Task.Delay(10);
        }
        Assert.Equal(3, sent.Count); // 1 command + 1 data chunk + 1 finish
        Assert.Equal(DownloadFrame.FilePayloadTypes.DownloadSession, sent[0][8]);
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(sent[0].AsSpan(0x16, 4))); // total at payload+0x0E
        Assert.Equal(DownloadFrame.FilePayloadTypes.DataChunk, sent[1][8]);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(sent[1].AsSpan(10, 4)));
        Assert.Equal((byte)'E', sent[1][0x12]); // "Empty..." data at payload+0x0A
        Assert.Equal(DownloadFrame.FilePayloadTypes.Finish, sent[2][8]);
    }

    [Fact]
    public async Task DownloadFile_rejects_protected_files_without_login_using_captured_status()
    {
        // Captured daemon answer for a .foid download without login: [0F][0x41] NotAuthorized.
        byte[] answer = [0x0E, 0x00, 0x02, 0x04, 0x00, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x41, 0x00, 0x00, 0x00];
        MockDaemonServer server = StartMock(_ => answer);

        await using var client = CreateClient(server.Port);
        await client.ConnectAsync();
        DownloadFileResult result = await client.DownloadFileAsync("d1test.foid", new byte[] { 1, 2, 3 });

        Assert.False(result.Ok);
        Assert.Equal(DownloadProtocolStatus.NotAuthorized, result.Status);
    }

    [Fact]
    public async Task DownloadFile_reports_chunk_rejection_with_captured_status()
    {
        byte[] cmdAck = [0x0E, 0x00, 0x02, 0x04, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x03, 0x00, 0x00, 0x00];
        byte[] chunkError = [0x0E, 0x00, 0x02, 0x04, 0x00, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x60, 0x00, 0x00, 0x00];
        MockDaemonServer server = StartMock(request =>
            request[8] == DownloadFrame.FilePayloadTypes.DownloadSession ? cmdAck : chunkError);

        await using var client = CreateClient(server.Port);
        await client.ConnectAsync();
        DownloadFileResult result = await client.DownloadFileAsync("stprog.o", new byte[300], 128);

        Assert.False(result.Ok);
        Assert.Equal(DownloadProtocolStatus.ChunkOutOfOrder, result.Status);
    }

    [Fact]
    public async Task DownloadFile_validates_arguments()
    {
        await using var client = CreateClient(1);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.DownloadFileAsync(" ", new byte[1]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.DownloadFileAsync("a.foid", new byte[1], 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.DownloadFileAsync("a.foid", new byte[1], DownloadFrame.MaxPayloadLength + 1));
    }

    // ---------------------------------------------------------------
    // Upload transaction against a mock daemon (captured data frames)
    // ---------------------------------------------------------------

    [Fact]
    public async Task UploadFile_reads_the_captured_data_frames()
    {
        // Captured exchange for "project\\project.hcpp" (7 bytes):
        // cmd ack : [06][total=7]      request ack: [05][index=0][len=7]["Empty\r\n"]
        byte[] cmdAck = [0x0E, 0x00, 0x02, 0x04, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x07, 0x00, 0x00, 0x00];
        byte[] dataAnswer =
        [
            0x19, 0x00, 0x02, 0x04, 0x00, 0x00, 0x00, 0x00,
            0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00,
            (byte)'E', (byte)'m', (byte)'p', (byte)'t', (byte)'y', 0x0D, 0x0A,
        ];
        MockDaemonServer server = StartMock(request =>
            request[8] == DownloadFrame.FilePayloadTypes.UploadSession
                ? cmdAck
                : request[8] == DownloadFrame.FilePayloadTypes.UploadRead ? dataAnswer : null);

        await using var client = CreateClient(server.Port);
        await client.ConnectAsync();
        DownloadFileResult result = await client.UploadFileAsync("project\\project.hcpp", 32);

        Assert.True(result.Ok);
        Assert.Equal(DownloadFrame.FilePayloadTypes.UploadSession, server.Requests[0][8]);
        Assert.Equal(DownloadFrame.FilePayloadTypes.UploadRead, server.Requests[1][8]);
    }

    // ---------------------------------------------------------------
    // Real daemon (env-gated; never runs on CI)
    // ---------------------------------------------------------------

    [Fact]
    [Trait("Category", "LocalSimulator")]
    public async Task Real_daemon_probe_version_read_and_download_roundtrip()
    {
        if (Environment.GetEnvironmentVariable("PLCMCP_LOCAL_SIMULATOR") != "1")
        {
            return; // requires a locally started h5u_daemon.exe on 127.0.0.1:12939 (loopback only)
        }

        await using var client = DownloadProtocolClient.Create("127.0.0.1", 12939, TimeSpan.FromSeconds(5));
        await client.ConnectAsync();
        Assert.Equal(DownloadProtocolStatus.ProbeAnswer, await client.ProbeAsync());
        DaemonVersionInfo version = await client.ReadVersionAsync();
        Assert.Equal(240, version.Raw.Length);

        // Login, download a unique file, upload it back, and verify the round-trip.
        // Daemon facts verified on the live instance: a session left open by a disconnected
        // client blocks further transfers of the same file (0x83) until the daemon restarts, so
        // this test uses a unique per-run file name and always finishes its sessions.
        Assert.Equal(DownloadProtocolStatus.Ok, await client.LoginAsync("plcmcp", "0"));
        Assert.True(await client.QueryLoginStateAsync());
        string filename = $"d1cs-{DateTime.UtcNow.Ticks:x}.foid";
        byte[] payload = "plcmcp download roundtrip"u8.ToArray();
        DownloadFileResult download = await client.DownloadFileAsync(filename, payload, 8);
        Assert.True(download.Ok, $"{download.Status}: {download.Detail}");
        DownloadFileResult upload = await client.UploadFileAsync(filename, 8);
        Assert.True(upload.Ok, upload.Detail);
        Assert.Equal<byte>(payload, upload.Content ?? []);
        Assert.Equal(DownloadProtocolStatus.Ok, await client.LogoutAsync());
        Assert.False(await client.QueryLoginStateAsync());
    }

    // ---------------------------------------------------------------
    // Mock daemon
    // ---------------------------------------------------------------

    /// <summary>Replays scripted answers to the client; records every received frame.</summary>
    private sealed class MockDaemonServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Func<byte[], byte[]?>[] _responders;

        public MockDaemonServer(params Func<byte[], byte[]?>[] responders)
        {
            _responders = responders.Length > 0 ? responders : [_ => null];
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public List<byte[]> Requests { get; } = [];

        public int Port { get; }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = HandleClientAsync(client);
                }
            }
            catch
            {
                // listener stopped; shutdown path
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    NetworkStream stream = client.GetStream();
                    while (true)
                    {
                        byte[]? frame = await ReadFrameAsync(stream).ConfigureAwait(false);
                        if (frame is null)
                        {
                            return;
                        }
                        Requests.Add(frame);

                        int responderIndex = Requests.Count - 1;
                        Func<byte[], byte[]?> responder = responderIndex < _responders.Length
                            ? _responders[responderIndex]
                            : _responders[^1];
                        byte[]? answer = responder(frame);
                        if (answer is not null)
                        {
                            await stream.WriteAsync(answer).ConfigureAwait(false);
                            await stream.FlushAsync().ConfigureAwait(false);
                        }
                    }
                }
            }
            catch
            {
                // client disconnected or shutdown
            }
        }

        private static async Task<byte[]?> ReadFrameAsync(NetworkStream stream)
        {
            var header = new byte[8];
            if (!await ReadExactlyAsync(stream, header).ConfigureAwait(false))
            {
                return null;
            }
            ushort total = BinaryPrimitives.ReadUInt16LittleEndian(header);
            if (total < 8 || total > 4096)
            {
                return header; // malformed; let the caller inspect it
            }
            var body = new byte[total];
            Buffer.BlockCopy(header, 0, body, 0, 8);
            if (!await ReadExactlyAsync(stream, body.AsMemory(8)).ConfigureAwait(false))
            {
                return body;
            }
            return body;
        }

        private static async Task<bool> ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer.Slice(read)).ConfigureAwait(false);
                if (n <= 0)
                {
                    return false;
                }
                read += n;
            }
            return true;
        }

        public void Dispose()
        {
            _listener.Stop();
            GC.SuppressFinalize(this);
        }
    }
}
