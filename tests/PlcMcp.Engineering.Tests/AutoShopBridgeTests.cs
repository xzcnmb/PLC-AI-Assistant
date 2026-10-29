using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PlcMcp.Adapters;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Doctor;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Workers.AutoShop;
using PlcMcp.Server.Governance;
using PlcMcp.Server.Mcp;

namespace PlcMcp.Engineering.Tests;

/// <summary>
/// Tests for the read-only AutoShop (InoVarSharedService / ITP) engineering bridge.
/// All tests run offline: the ITP endpoint is a loopback mock TCP server that replays
/// captured ITP packets, and every environment probe is injectable.
/// </summary>
public class AutoShopBridgeTests : IDisposable
{
    private readonly List<TcpListener> _listeners = new();

    public void Dispose()
    {
        foreach (TcpListener listener in _listeners)
        {
            try { listener.Stop(); } catch { }
        }
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------
    // ITP framing unit tests (golden values captured from the real
    // InoVarSharedService on 127.0.0.1:13939, 2026-10-23)
    // ---------------------------------------------------------------

    [Fact]
    public void ItpCrc16_MatchesServiceObservedValue()
    {
        // Register payload used against the live service; the answer header carried crc=0x4F37
        // over the answer payload, and the service accepted our request crc 0xAB75 for this payload.
        byte[] payload = Encoding.UTF8.GetBytes("{\"Path\": \"D:\\\\PLCmcp\\\\itp-readonly-probe\"}");
        ushort crc = ItpCrc16.Compute(payload);
        Assert.Equal(0xAB75, crc);
    }

    [Fact]
    public void ItpPacket_CreateRegister_MatchesCapturedWireBytes()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"Path\": \"D:\\\\PLCmcp\\\\itp-readonly-probe\"}");
        ItpPacket packet = ItpPacket.Create(ItpPacketType.Register, payload, packetId: 1);

        byte[] wire = packet.ToBytes();
        Assert.Equal(28 + payload.Length, wire.Length);
        Assert.Equal(new byte[] { 0x0E, 0xA5, 0x0E, 0xA5 }, wire[..4]); // magic 0xA50EA50E LE
        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(4, 4)));   // dataLen
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(8, 4)));   // type=Register
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(0x10, 2))); // packetId
        Assert.Equal((ushort)0xAB75, BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(0x1A, 2))); // crc
        Assert.Equal(payload, wire[28..]);
    }

    [Fact]
    public void ItpPacket_HeaderRoundTrip_AndRejectsBadMagicOrLength()
    {
        ItpPacket packet = ItpPacket.Create(ItpPacketType.Register, Encoding.UTF8.GetBytes("{\"Path\":\"x\"}"));
        byte[] wire = packet.ToBytes();

        Assert.True(ItpPacketHeader.TryParse(wire, out ItpPacketHeader parsed));
        Assert.Equal(ItpPacketHeader.Magic, parsed.MagicValue);
        Assert.Equal(ItpPacketType.Register, parsed.PacketType);
        Assert.Equal(12, parsed.DataLength);

        byte[] badMagic = wire[..];
        badMagic[0] ^= 0xFF;
        Assert.False(ItpPacketHeader.TryParse(badMagic, out _));

        byte[] badLen = wire[..];
        BinaryPrimitives.WriteInt32LittleEndian(badLen.AsSpan(4, 4), ItpPacketHeader.MaxPayloadSize + 1);
        Assert.False(ItpPacketHeader.TryParse(badLen, out _));

        Assert.False(ItpPacketHeader.TryParse(new byte[10], out _));
    }

    [Fact]
    public void ItpPacket_RejectsOversizedPayload()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ItpPacket.Create(ItpPacketType.Register, new byte[ItpPacketHeader.MaxPayloadSize + 1]));
    }

    [Fact]
    public void ItpRegisterResponse_ParsesStyledServiceAnswer()
    {
        // Exact bytes answered by the live service for a register handshake (styled jsoncpp).
        byte[] answerPayload = Encoding.UTF8.GetBytes("{\n   \"Response\" : \"3\"\n}\n");
        var response = ItpRegisterResponse.Parse(answerPayload);
        Assert.Equal("3", response.Response);
    }

    // ---------------------------------------------------------------
    // ITP client against a mock TCP server (packet replay)
    // ---------------------------------------------------------------

    [Fact]
    public async Task ItpClient_RegisterAsync_ReplaysServiceAnswer()
    {
        var mock = new MockItpServer();
        int port = mock.Start(_listeners);

        await using var client = ItpClient.Create("127.0.0.1", port, TimeSpan.FromSeconds(3));
        await client.ConnectAsync();
        ItpHandshakeResult result = await client.RegisterAsync("D:\\probe\\readonly");

        Assert.True(result.Connected);
        Assert.True(result.Answered);
        Assert.Equal("3", result.ResponseCode);
        Assert.False(result.HasDataSource);
        Assert.Contains("no shared-variable publisher", result.Message);
        Assert.Equal($"127.0.0.1:{port}", result.Endpoint);

        // The mock server must have received exactly one well-formed register packet.
        ItpPacket received = Assert.Single(mock.ReceivedPackets);
        Assert.Equal(ItpPacketType.Register, received.Header.PacketType);
        // JSON escapes backslashes; the path on the wire is D:\probe\readonly.
        Assert.Equal("{\"Path\":\"D:\\\\probe\\\\readonly\"}", Encoding.UTF8.GetString(received.Payload));
    }

    [Fact]
    public async Task ItpClient_RegisterAsync_TimesOut_WhenServiceNeverAnswers()
    {
        var mock = new MockItpServer { Reply = false };
        int port = mock.Start(_listeners);

        await using var client = ItpClient.Create("127.0.0.1", port, TimeSpan.FromMilliseconds(400));
        await client.ConnectAsync();

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.RegisterAsync("D:\\probe"));
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task ItpClient_RegisterAsync_RejectsNonItpAnswer()
    {
        var mock = new MockItpServer { CorruptAnswerMagic = true };
        int port = mock.Start(_listeners);

        await using var client = ItpClient.Create("127.0.0.1", port, TimeSpan.FromSeconds(3));
        await client.ConnectAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => client.RegisterAsync("D:\\probe"));
    }

    [Fact]
    public async Task ItpClient_RegisterAsync_SurfacesStreamClose()
    {
        var mock = new MockItpServer { CloseImmediately = true };
        int port = mock.Start(_listeners);

        await using var client = ItpClient.Create("127.0.0.1", port, TimeSpan.FromSeconds(3));
        await client.ConnectAsync();

        // EndOfStreamException derives from IOException; a reset connection surfaces as either.
        await Assert.ThrowsAnyAsync<IOException>(() => client.RegisterAsync("D:\\probe"));
    }

    [Fact]
    public void ItpClient_RejectsInvalidEndpoints()
    {
        Assert.Throws<ArgumentException>(() => ItpClient.Create("", 13939));
        Assert.Throws<ArgumentOutOfRangeException>(() => ItpClient.Create("127.0.0.1", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ItpClient.Create("127.0.0.1", 70000));
    }

    // ---------------------------------------------------------------
    // Environment detector (injected paths / registry / TCP probes)
    // ---------------------------------------------------------------

    [Fact]
    public void Detector_ReportsInstalledEnvironment_FromInjectedEvidence()
    {
        var options = new AutoShopDetectionOptions
        {
            ExecutableCandidates = new[] { @"D:\fake\AutoShop\AutoShop.exe" },
            FileExists = p => p.EndsWith("AutoShop.exe", StringComparison.OrdinalIgnoreCase),
            FileVersionReader = _ => "4.12.0.2",
            RegistryValueReader = (_, _) => new byte[] { 0x73, 0x36 }, // 0x3673 = 13939 LE
            ServiceProcessProbe = _ => true,
            TcpProbe = (_, port) => Task.FromResult(port == 13939),
            NewestLogFileProbe = _ => @"D:\fake\SharedVaribleLog\2026-10-23_1.log"
        };

        var report = new AutoShopEnvironmentDetector(options).Detect();

        Assert.True(report.Installed);
        Assert.Equal(@"D:\fake\AutoShop\AutoShop.exe", report.ExecutablePath);
        Assert.Equal("4.12.0.2", report.Version);
        Assert.True(report.ServiceProcessRunning);
        Assert.Equal(13939, report.RegistryPort);
        Assert.Contains("AutoServicePort", report.RegistryPortLocation);
        Assert.Equal(13939, report.EffectivePort);
        Assert.True(report.LoopbackListening);
        Assert.Contains("does not imply valid vendor license", report.Disclaimer);
    }

    [Fact]
    public void Detector_UsesRegistryPort_WhenPersisted()
    {
        var options = new AutoShopDetectionOptions
        {
            ExecutableCandidates = new[] { @"D:\fake\AutoShop\AutoShop.exe" },
            FileExists = _ => true,
            FileVersionReader = _ => "4.12.0.2",
            RegistryValueReader = (_, _) => new byte[] { 0x30, 0x36 }, // 0x3630 = 13872 LE
            ServiceProcessProbe = _ => false,
            TcpProbe = (_, port) => Task.FromResult(port == 13872),
            NewestLogFileProbe = _ => null
        };

        var report = new AutoShopEnvironmentDetector(options).Detect();

        Assert.Equal(13872, report.RegistryPort);
        Assert.Equal(13872, report.EffectivePort);
        Assert.True(report.LoopbackListening);
        Assert.False(report.ServiceProcessRunning);
    }

    [Fact]
    public void Detector_NotInstalled_FallsBackToDefaultPort()
    {
        var options = new AutoShopDetectionOptions
        {
            ExecutableCandidates = new[] { @"D:\missing\AutoShop.exe" },
            FileExists = _ => false,
            RegistryValueReader = (_, _) => null,
            ServiceProcessProbe = _ => false,
            TcpProbe = (_, _) => Task.FromResult(false),
            NewestLogFileProbe = _ => null
        };

        var report = new AutoShopEnvironmentDetector(options).Detect();

        Assert.False(report.Installed);
        Assert.Null(report.ExecutablePath);
        Assert.Null(report.RegistryPort);
        Assert.Equal(13939, report.EffectivePort);
        Assert.False(report.LoopbackListening);
        Assert.Contains("not found", report.Details);
    }

    // ---------------------------------------------------------------
    // VendorDoctor integration with injected detection
    // ---------------------------------------------------------------

    [Fact]
    public void VendorDoctor_AutoShopCheck_UsesInjectedOptions()
    {
        var installedOptions = new AutoShopDetectionOptions
        {
            ExecutableCandidates = new[] { @"D:\fake\AutoShop\AutoShop.exe" },
            FileExists = _ => true,
            FileVersionReader = _ => "4.12.0.2",
            RegistryValueReader = (_, _) => new byte[] { 0x73, 0x36 },
            ServiceProcessProbe = _ => true,
            TcpProbe = (_, _) => Task.FromResult(true),
            NewestLogFileProbe = _ => null
        };

        var doctor = new VendorDoctor(installedOptions);
        var report = doctor.CheckSoftware(VendorSoftwareKind.AutoShop);

        Assert.True(report.Installed);
        Assert.Equal("Inovance AutoShop", report.Name);
        Assert.Equal("4.12.0.2", report.Version);
        Assert.Contains("reverse engineering", report.Disclaimer);

        var missingOptions = new AutoShopDetectionOptions
        {
            ExecutableCandidates = Array.Empty<string>(),
            FileExists = _ => false,
            RegistryValueReader = (_, _) => null,
            ServiceProcessProbe = _ => false,
            TcpProbe = (_, _) => Task.FromResult(false),
            NewestLogFileProbe = _ => null
        };
        var missing = new VendorDoctor(missingOptions).CheckSoftware(VendorSoftwareKind.AutoShop);
        Assert.False(missing.Installed);
        Assert.Contains("not found", missing.Details);
    }

    [Fact]
    public void VendorDoctor_DefaultConstructor_StillWorks()
    {
        var report = new VendorDoctor().RunFullDiagnosis();
        Assert.Contains(report.Items, i => i.Kind == VendorSoftwareKind.AutoShop);
    }

    // ---------------------------------------------------------------
    // Bridge services & worker (mock TCP server handshake)
    // ---------------------------------------------------------------

    [Fact]
    public async Task BridgeServices_Inspect_PerformsReadOnlyHandshake()
    {
        var mock = new MockItpServer();
        int port = mock.Start(_listeners);

        var services = CreateServices(port, installed: true);
        AutoShopInspectionReport report = await services.InspectAsync();

        Assert.True(report.EnvironmentInstalled);
        Assert.NotNull(report.Handshake);
        Assert.True(report.Handshake!.Answered);
        Assert.Equal("3", report.Handshake.ResponseCode);
        Assert.Contains("register handshake", report.Summary);
        Assert.StartsWith("Unsupported", report.Capabilities["shared_variable_read"]);
        Assert.StartsWith("Unsupported", report.Capabilities["download"]);
        Assert.Contains("reverse engineering", report.Disclaimer);
        Assert.Single(mock.ReceivedPackets); // exactly one register packet, nothing else
    }

    [Fact]
    public async Task BridgeServices_ReadSharedVariables_ReportsUnsupportedWithReason()
    {
        var mock = new MockItpServer();
        int port = mock.Start(_listeners);

        var services = CreateServices(port, installed: true);
        AutoShopVariableReadReport report = await services.ReadSharedVariablesAsync(TimeSpan.FromMilliseconds(300));

        Assert.True(report.Connected);
        Assert.True(report.HandshakeAnswered);
        Assert.Equal("3", report.ResponseCode);
        Assert.Equal("Unsupported", report.VariableReadCapability);
        Assert.Contains("No shared-variable publisher", report.Reason);
        Assert.Equal(0, report.PushPacketsObserved);
    }

    [Fact]
    public async Task BridgeServices_ReadSharedVariables_CountsPushedPackets_WithoutDecoding()
    {
        var mock = new MockItpServer { ResponseCode = "0", PushAfterAnswer = true };
        int port = mock.Start(_listeners);

        var services = CreateServices(port, installed: true);
        AutoShopVariableReadReport report = await services.ReadSharedVariablesAsync(TimeSpan.FromSeconds(2));

        Assert.True(report.HandshakeAnswered);
        Assert.Equal("0", report.ResponseCode);
        Assert.True(report.PushPacketsObserved >= 1);
        Assert.Equal("Unsupported", report.VariableReadCapability);
        Assert.Contains("not reverse-engineered or verified", report.Reason);
    }

    [Fact]
    public async Task BridgeServices_Inspect_HandlesUnreachableEndpoint()
    {
        // TCP probe reports nothing listening, so no handshake is attempted at all.
        var services = new AutoShopBridgeServices(
            new AutoShopBridgeConfig { Port = 13940, Timeout = TimeSpan.FromSeconds(3) },
            new AutoShopEnvironmentDetector(new AutoShopDetectionOptions
            {
                ExecutableCandidates = new[] { @"D:\fake\AutoShop\AutoShop.exe" },
                FileExists = _ => false,
                RegistryValueReader = (_, _) => null,
                ServiceProcessProbe = _ => false,
                TcpProbe = (_, _) => Task.FromResult(false),
                NewestLogFileProbe = _ => null
            }));
        AutoShopInspectionReport report = await services.InspectAsync();

        Assert.False(report.EnvironmentInstalled);
        Assert.Null(report.Handshake);
        Assert.Contains("not listening", report.Summary);
        Assert.StartsWith("Unsupported", report.Capabilities["itp_register_handshake"]);
    }

    [Fact]
    public async Task BridgeServices_ProbeHandshake_ConnectionRefused_ReportsFailedHandshake()
    {
        // TCP probe says listening but the real connect is refused (e.g. service died between probes).
        var services = new AutoShopBridgeServices(
            new AutoShopBridgeConfig { Port = 13941, Timeout = TimeSpan.FromSeconds(3) },
            new AutoShopEnvironmentDetector(new AutoShopDetectionOptions
            {
                ExecutableCandidates = new[] { @"D:\fake\AutoShop\AutoShop.exe" },
                FileExists = _ => true,
                FileVersionReader = _ => "4.12.0.2",
                RegistryValueReader = (_, _) => null,
                ServiceProcessProbe = _ => true,
                TcpProbe = (_, _) => Task.FromResult(true),
                NewestLogFileProbe = _ => null
            }));

        ItpHandshakeResult handshake = await services.ProbeHandshakeAsync();

        Assert.False(handshake.Connected);
        Assert.False(handshake.Answered);
        Assert.False(handshake.HasDataSource);
        Assert.Contains("failed", handshake.Message);
    }

    [Fact]
    public async Task BridgeWorker_InspectProject_ReturnsExperimentalEvidence()
    {
        var mock = new MockItpServer();
        int port = mock.Start(_listeners);
        var worker = new AutoShopBridgeWorker(CreateServices(port, installed: true));

        Assert.True(worker.IsAvailable);
        Assert.Equal(PlcVendor.Inovance, worker.Vendor);
        Assert.Equal("AutoShop-ITP-Bridge", worker.Name);

        var job = new EngineeringJobRequest("job-autoshop-1", EngineeringJobType.InspectProject, PlcVendor.Inovance, "not-used");
        var result = await worker.ExecuteAsync(job);

        Assert.True(result.Success);
        Assert.Equal(CapabilityStatus.Experimental, result.Status);
        Assert.NotEqual(CapabilityStatus.Supported, result.Status);
        Assert.Contains("register handshake", result.Message);
        Assert.Contains("reverse engineering", result.Details);
    }

    [Fact]
    public async Task BridgeWorker_RejectsCompileAndDownloadOperations()
    {
        var worker = new AutoShopBridgeWorker(CreateServices(13940, installed: true));

        var compile = await worker.ExecuteAsync(
            new EngineeringJobRequest("job-c", EngineeringJobType.CompileProject, PlcVendor.Inovance, "x"));
        Assert.False(compile.Success);
        Assert.Equal(CapabilityStatus.Unsupported, compile.Status);
        Assert.Contains("read-only environment inspection only", compile.Message);

        var download = await worker.ExecuteAsync(
            new EngineeringJobRequest("job-d", EngineeringJobType.InspectProject, PlcVendor.Inovance, "x",
                new Dictionary<string, string> { ["operation"] = "download_to_plc" }));
        Assert.False(download.Success);
        Assert.Equal(CapabilityStatus.Unsupported, download.Status);
        Assert.Contains("refused by safety policy", download.Message);
    }

    // ---------------------------------------------------------------
    // MCP tool registration gating (--autoshop-bridge switch)
    // ---------------------------------------------------------------

    [Fact]
    public void McpRouter_WithoutBridge_HidesAutoShopTools()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "plcmcp_autoshop_router_" + Guid.NewGuid().ToString("N"));
        try
        {
            var governance = new ServerGovernanceServices(dataRoot);
            var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);

            Assert.DoesNotContain(router.ToolDescriptors, x => x.Name is "plc_autoshop_inspect" or "plc_autoshop_read_shared_variables");
            Assert.Null(governance.AutoShopBridge);
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public async Task McpRouter_WithBridge_RegistersAndExecutesReadOnlyTools()
    {
        var mock = new MockItpServer();
        int port = mock.Start(_listeners);
        var dataRoot = Path.Combine(Path.GetTempPath(), "plcmcp_autoshop_router_" + Guid.NewGuid().ToString("N"));
        try
        {
            var detector = new AutoShopEnvironmentDetector(CreateDetectorOptions(port, installed: true));
            var governance = new ServerGovernanceServices(
                dataRoot,
                autoShopBridgeConfig: new AutoShopBridgeConfig { Port = port },
                autoShopDetector: detector);
            var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);

            var inspectDescriptor = Assert.Single(router.ToolDescriptors, x => x.Name == "plc_autoshop_inspect");
            Assert.True(inspectDescriptor.ReadOnly);
            Assert.False(inspectDescriptor.Destructive);
            var readDescriptor = Assert.Single(router.ToolDescriptors, x => x.Name == "plc_autoshop_read_shared_variables");
            Assert.True(readDescriptor.ReadOnly);

            using var args = JsonDocument.Parse("{}");
            var inspectResponse = await router.CallToolAsync("plc_autoshop_inspect", args.RootElement, CancellationToken.None);
            using var inspectDoc = JsonDocument.Parse(JsonSerializer.Serialize(inspectResponse, JsonRpc.SerializerOptions));
            Assert.False(inspectDoc.RootElement.GetProperty("isError").GetBoolean());
            Assert.True(inspectDoc.RootElement.GetProperty("environmentInstalled").GetBoolean());
            Assert.StartsWith("Unsupported", inspectDoc.RootElement.GetProperty("capabilities").GetProperty("shared_variable_read").GetString());

            using var readArgs = JsonDocument.Parse("{\"observationSeconds\":0.5}");
            var readResponse = await router.CallToolAsync("plc_autoshop_read_shared_variables", readArgs.RootElement, CancellationToken.None);
            using var readDoc = JsonDocument.Parse(JsonSerializer.Serialize(readResponse, JsonRpc.SerializerOptions));
            Assert.False(readDoc.RootElement.GetProperty("isError").GetBoolean());
            Assert.Equal("Unsupported", readDoc.RootElement.GetProperty("variableReadCapability").GetString());
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static AutoShopDetectionOptions CreateDetectorOptions(int port, bool installed) => new()
    {
        ExecutableCandidates = new[] { @"D:\fake\AutoShop\AutoShop.exe" },
        FileExists = _ => installed,
        FileVersionReader = _ => "4.12.0.2",
        RegistryValueReader = (_, _) => new byte[] { (byte)(port & 0xFF), (byte)(port >> 8) },
        ServiceProcessProbe = _ => installed,
        TcpProbe = (_, p) => Task.FromResult(p == port),
        NewestLogFileProbe = _ => null
    };

    private static AutoShopBridgeServices CreateServices(int port, bool installed) => new(
        new AutoShopBridgeConfig { Port = port, Timeout = TimeSpan.FromSeconds(3) },
        new AutoShopEnvironmentDetector(CreateDetectorOptions(port, installed)));

    /// <summary>
    /// Loopback mock of InoVarSharedService: validates the ITP packet framing (magic, CRC),
    /// records received packets, and replays captured register answers.
    /// </summary>
    private sealed class MockItpServer
    {
        public bool Reply { get; set; } = true;
        public bool CorruptAnswerMagic { get; set; }
        public bool CloseImmediately { get; set; }
        public string ResponseCode { get; set; } = "3";
        public bool PushAfterAnswer { get; set; }

        public List<ItpPacket> ReceivedPackets { get; } = new();

        private TcpListener? _listener;
        private CancellationTokenSource _cts = new();

        public int Start(List<TcpListener> registry)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            registry.Add(_listener);
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return port;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener is not null)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                if (CloseImmediately)
                {
                    return;
                }

                var header = new byte[28];
                int read = 0;
                while (read < 28)
                {
                    int n = await stream.ReadAsync(header.AsMemory(read, 28 - read), ct).ConfigureAwait(false);
                    if (n == 0) return;
                    read += n;
                }

                if (!ItpPacketHeader.TryParse(header, out ItpPacketHeader parsedHeader))
                {
                    return;
                }

                var payload = new byte[parsedHeader.DataLength];
                read = 0;
                while (read < payload.Length)
                {
                    int n = await stream.ReadAsync(payload.AsMemory(read, payload.Length - read), ct).ConfigureAwait(false);
                    if (n == 0) return;
                    read += n;
                }

                ReceivedPackets.Add(new ItpPacket { Header = parsedHeader, Payload = payload });

                if (!Reply)
                {
                    // Hold the connection open without answering (timeout scenario).
                    try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); } catch { }
                    return;
                }

                byte[] answerPayload = Encoding.UTF8.GetBytes($"{{\n   \"Response\" : \"{ResponseCode}\"\n}}\n");
                ItpPacket answer = ItpPacket.Create(ItpPacketType.Register, answerPayload);
                byte[] wire = answer.ToBytes();
                if (CorruptAnswerMagic)
                {
                    wire[0] ^= 0xFF;
                }

                await stream.WriteAsync(wire, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);

                if (PushAfterAnswer)
                {
                    // A well-framed but deliberately opaque "data" packet (type 3, binary body).
                    // The bridge must count it without decoding it.
                    byte[] pushBody = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
                    ItpPacket push = ItpPacket.Create(ItpPacketType.DataTransferBase, pushBody);
                    await stream.WriteAsync(push.ToBytes(), ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
    }
}
