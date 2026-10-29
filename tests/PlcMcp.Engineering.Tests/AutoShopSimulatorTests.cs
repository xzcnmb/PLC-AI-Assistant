using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PlcMcp.Adapters.Protocols;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Workers.AutoShop.Simulator;

namespace PlcMcp.Engineering.Tests;

/// <summary>
/// Tests for the AutoShop H5U simulator automation layer (SimulatorLauncher / SimulatorProbe).
/// All tests are offline: processes are fakes, TCP endpoints are loopback mock servers, and
/// every probe delegate is injectable. Real-daemon integration tests carry the
/// LocalSimulator trait and are skipped unless PLCMCP_LOCAL_SIMULATOR=1 (CI never needs them).
/// </summary>
public class AutoShopSimulatorTests : IDisposable
{
    private const string MissingTestHost = "definitely-missing-testhost-3f2a.exe";
    private readonly List<string> _tempFiles = [];
    private readonly List<MockModbusServer> _servers = [];

    public void Dispose()
    {
        foreach (MockModbusServer server in _servers)
        {
            server.Dispose();
        }

        foreach (string file in _tempFiles)
        {
            try { File.Delete(file); } catch { }
        }
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------
    // Probe frame construction: read-only by construction, golden bytes
    // ---------------------------------------------------------------

    [Fact]
    public void BuildReadRequest_Fc3_MatchesGoldenBytes()
    {
        // transaction 1, unit 1, FC3, address 100 (D100->HR100), count 1
        byte[] frame = SimulatorProbe.BuildReadRequest(1, 1, 3, 100, 1);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03, 0x00, 0x64, 0x00, 0x01 }, frame);
    }

    [Fact]
    public void BuildReadRequest_Fc1_M0Coil_MatchesGoldenBytes()
    {
        // transaction 0xFFFF, unit 255, FC1, address 1024 (M0->coil 1024), count 1
        byte[] frame = SimulatorProbe.BuildReadRequest(0xFFFF, 255, 1, 1024, 1);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x06, 0xFF, 0x01, 0x04, 0x00, 0x00, 0x01 }, frame);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(15)]
    [InlineData(16)]
    public void BuildReadRequest_RejectsEveryWriteFunctionCode(byte function)
    {
        // Zero-write red line: a Modbus write frame is structurally impossible.
        Assert.Throws<ArgumentException>(() => SimulatorProbe.BuildReadRequest(1, 1, function, 0, 1));
    }

    [Theory]
    [InlineData(0, 1)]        // zero count
    [InlineData(126, 1)]      // FC3 allows at most 125 registers
    [InlineData(1, 0)]        // unit 0 (broadcast) forbidden
    public void BuildReadRequest_RejectsInvalidCountsOrUnit(int count, byte unit)
    {
        Assert.Throws<ArgumentException>(() => SimulatorProbe.BuildReadRequest(1, unit, 3, 0, (ushort)count));
    }

    [Fact]
    public void BuildReadRequest_RejectsRangeBeyondAddressSpace()
    {
        Assert.Throws<ArgumentException>(() => SimulatorProbe.BuildReadRequest(1, 1, 3, 65535, 2));
    }

    [Fact]
    public void ParseReadResponse_Fc3_DecodesBigEndianRegisters()
    {
        // response to transaction 5: unit 1, FC3, byteCount 2, data 0x1234
        byte[] frame = { 0x00, 0x05, 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x12, 0x34 };
        SimulatorReadResponse response = SimulatorProbe.ParseReadResponse(frame, 5, 1, 3, 1);
        Assert.Equal(new ushort[] { 0x1234 }, response.Values);
        Assert.Equal((byte)3, response.FunctionCode);
        Assert.Equal((ushort)5, response.Transaction);
    }

    [Fact]
    public void ParseReadResponse_Fc1_DecodesCoilBitsLsbFirst()
    {
        // 10 coils, byteCount 2, data 0x05 0x01: bits 0, 2 and 8 set
        byte[] frame = { 0x00, 0x02, 0x00, 0x00, 0x00, 0x05, 0x01, 0x01, 0x02, 0x05, 0x01 };
        SimulatorReadResponse response = SimulatorProbe.ParseReadResponse(frame, 2, 1, 1, 10);
        Assert.Equal(1, response.Values[0]);
        Assert.Equal(0, response.Values[1]);
        Assert.Equal(1, response.Values[2]);
        Assert.Equal(1, response.Values[8]);
        Assert.Equal(0, response.Values[9]);
    }

    [Fact]
    public void ParseReadResponse_ExceptionResponse_ThrowsWithExceptionCode()
    {
        byte[] frame = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x03, 0x01, 0x83, 0x02 };
        ModbusProbeException ex = Assert.Throws<ModbusProbeException>(
            () => SimulatorProbe.ParseReadResponse(frame, 1, 1, 3, 1));
        Assert.Equal(0x02, ex.ExceptionCode);
    }

    [Theory]
    [InlineData(0)]   // transaction mismatch
    [InlineData(1)]   // wrong unit
    [InlineData(2)]   // wrong byte count
    [InlineData(3)]   // wrong function
    [InlineData(4)]   // truncated frame
    public void ParseReadResponse_RejectsMalformedFrames(int caseIndex)
    {
        byte[] good = { 0x00, 0x05, 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x12, 0x34 };
        byte[] frame = caseIndex switch
        {
            0 => [0x00, 0x06, 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x12, 0x34],
            1 => [0x00, 0x05, 0x00, 0x00, 0x00, 0x05, 0x02, 0x03, 0x02, 0x12, 0x34],
            2 => [0x00, 0x05, 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x03, 0x12, 0x34, 0x00],
            3 => [0x00, 0x05, 0x00, 0x00, 0x00, 0x05, 0x01, 0x04, 0x02, 0x12, 0x34],
            _ => [0x00, 0x05, 0x00, 0x00]
        };
        Assert.ThrowsAny<InvalidDataException>(
            () => SimulatorProbe.ParseReadResponse(frame, 5, 1, 3, 1));
    }

    // ---------------------------------------------------------------
    // Probe against a loopback mock daemon: values, latency, quality
    // ---------------------------------------------------------------

    [Fact]
    public async Task ProbeHoldingRegistersAsync_CannedServer_ReturnsValueAndLatency()
    {
        MockModbusServer server = StartMockServer();
        var probe = new SimulatorProbe(new SimulatorProbeOptions { ModbusPort = server.Port, TimeoutMs = 2000 });

        SimulatorRegisterReading reading = await probe.ProbeHoldingRegistersAsync(100, 1);

        Assert.Equal(QualityCode.Good, reading.Quality);
        Assert.Null(reading.Error);
        Assert.Equal(new ushort[] { 0x1011 }, reading.Values);
        Assert.True(reading.LatencyMs >= 0, $"latency {reading.LatencyMs} should be measured");
        Assert.Equal(3, reading.FunctionCode);
        // Zero-write evidence: the mock only ever received read requests.
        Assert.All(server.Requests, r => Assert.True(r[7] is 1 or 2 or 3 or 4, $"unexpected function {r[7]} on the wire"));
    }

    [Fact]
    public async Task ProbeCoilsAsync_CannedServer_DecodesBits()
    {
        MockModbusServer server = StartMockServer();
        var probe = new SimulatorProbe(new SimulatorProbeOptions { ModbusPort = server.Port, TimeoutMs = 2000 });

        SimulatorRegisterReading reading = await probe.ProbeCoilsAsync(1024, 3);

        Assert.Equal(QualityCode.Good, reading.Quality);
        Assert.Equal(1, reading.Values[0]); // responder data 0x03 -> bits 0 and 1 set
        Assert.Equal(1, reading.Values[1]);
        Assert.Equal(0, reading.Values[2]);
        Assert.Equal(1, reading.FunctionCode);
    }

    [Fact]
    public async Task Probe_TimeoutWhenServerSilent_ReportsBadQualityWithTimeout()
    {
        MockModbusServer server = StartMockServer(silent: true);
        var probe = new SimulatorProbe(new SimulatorProbeOptions { ModbusPort = server.Port, TimeoutMs = 200 });

        SimulatorRegisterReading reading = await probe.ProbeHoldingRegistersAsync(0, 1);

        Assert.Equal(QualityCode.Bad, reading.Quality);
        Assert.NotNull(reading.Error);
        Assert.Contains("timed out", reading.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Probe_ConnectionRefused_ReportsBadQualityWithError()
    {
        // Take a free port and immediately release it: nothing listens there.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int deadPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var probe = new SimulatorProbe(new SimulatorProbeOptions { ModbusPort = deadPort, TimeoutMs = 1000 });
        SimulatorRegisterReading reading = await probe.ProbeHoldingRegistersAsync(0, 1);

        Assert.Equal(QualityCode.Bad, reading.Quality);
        Assert.NotNull(reading.Error);
    }

    [Fact]
    public async Task ProbeAsync_FullDefaultSet_AllGood_NativeSkippedWhenBinaryMissing()
    {
        MockModbusServer server = StartMockServer();
        var probe = new SimulatorProbe(new SimulatorProbeOptions
        {
            ModbusPort = server.Port,
            TimeoutMs = 2000,
            TestHostPath = Path.Combine(Path.GetTempPath(), MissingTestHost)
        });

        SimulatorProbeReport report = await probe.ProbeAsync();

        Assert.True(report.ModbusProbeOk);
        Assert.Equal(4, report.Readings.Count); // D0, D100, R0 + M0 defaults
        Assert.All(report.Readings, r => Assert.Equal(QualityCode.Good, r.Quality));
        Assert.NotNull(report.Native);
        Assert.True(report.Native.Skipped);
        Assert.Contains("TestHost", report.Native.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeOptions_RejectsNonLoopbackHost()
    {
        Assert.Throws<ArgumentException>(() => new SimulatorProbe(new SimulatorProbeOptions { Host = "10.0.0.5" }));
        Assert.Throws<ArgumentException>(() => new SimulatorProbe(new SimulatorProbeOptions { NativeTarget = "192.168.1.10:12939" }));
    }

    [Fact]
    public void ProbeOptions_RejectsInvalidUnitOrTimeoutOrPorts()
    {
        Assert.Throws<ArgumentException>(() => new SimulatorProbe(new SimulatorProbeOptions { UnitId = 0 }));
        Assert.Throws<ArgumentException>(() => new SimulatorProbe(new SimulatorProbeOptions { TimeoutMs = 50 }));
        Assert.Throws<ArgumentException>(() => new SimulatorProbe(new SimulatorProbeOptions { ModbusPort = 0 }));
        Assert.Throws<ArgumentException>(() => new SimulatorProbe(new SimulatorProbeOptions { NativeTarget = "no-port-here" }));
    }

    // ---------------------------------------------------------------
    // Native probe wrapper (offline: real child process, no vendor binary)
    // ---------------------------------------------------------------

    [Fact]
    public async Task ProbeNativeAsync_MissingBinary_SkipsWithReason()
    {
        var probe = new SimulatorProbe(new SimulatorProbeOptions
        {
            TestHostPath = Path.Combine(Path.GetTempPath(), MissingTestHost)
        });

        NativeProbeResult result = await probe.ProbeNativeAsync();

        Assert.False(result.Attempted);
        Assert.True(result.Skipped);
        Assert.NotNull(result.SkipReason);
    }

    [Fact]
    public async Task ProbeNativeAsync_RunsHarness_ParsesOnlineMarker()
    {
        string script = Path.Combine(Path.GetTempPath(), $"plcmcp-fake-testhost-{Guid.NewGuid():N}.cmd");
        // NOTE: no '>' in the echoed text — cmd would treat it as output redirection.
        await File.WriteAllTextAsync(script,
            "@echo off\r\necho   PlcMcpComm_SetTarget rc=0\r\necho   PlcMcpComm_IsOnline 1 (ONLINE)\r\nexit /b 0\r\n");
        _tempFiles.Add(script);

        var probe = new SimulatorProbe(new SimulatorProbeOptions
        {
            TestHostPath = script,
            NativeTarget = "127.0.0.1:12939"
        });

        NativeProbeResult result = await probe.ProbeNativeAsync();

        Assert.True(result.Attempted, $"not attempted: {result.SkipReason} {result.Error}");
        Assert.False(result.Skipped);
        Assert.True(result.Online, $"no ONLINE marker in output: '{result.OutputTail}' error: {result.Error}");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ONLINE", result.OutputTail, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // Launcher: discovery
    // ---------------------------------------------------------------

    [Fact]
    public void Discover_FindsExecutable_AndReportsWorkingDirectory()
    {
        var launcher = new SimulatorLauncher(
            new SimulatorLauncherOptions { ExecutablePath = @"D:\Inovance Control\AutoShop\Simulator\h5u_daemon.exe" },
            fileExists: _ => true);

        SimulatorDiscoveryResult discovery = launcher.Discover();

        Assert.True(discovery.Found);
        Assert.Equal(@"D:\Inovance Control\AutoShop\Simulator\h5u_daemon.exe", discovery.ExecutablePath);
        Assert.Equal(@"D:\Inovance Control\AutoShop\Simulator", discovery.WorkingDirectory);
    }

    [Fact]
    public void Discover_MissingExecutable_ReportsNotFoundWithoutThrowing()
    {
        var launcher = new SimulatorLauncher(
            new SimulatorLauncherOptions { ExecutablePath = @"D:\nowhere\h5u_daemon.exe" },
            fileExists: _ => false);

        SimulatorDiscoveryResult discovery = launcher.Discover();

        Assert.False(discovery.Found);
        Assert.Null(discovery.ExecutablePath);
    }

    // ---------------------------------------------------------------
    // Launcher: start / reuse / ownership
    // ---------------------------------------------------------------

    [Fact]
    public async Task StartAsync_StartsProcessWithSimulatorWorkingDirectory_AndReportsOwned()
    {
        var fake = new FakeSimulatorProcess(4242);
        SimulatorProcessStartSpec? spec = null;
        bool started = false;
        var launcher = new SimulatorLauncher(OfflineOptions(), fileExists: _ => true,
            processStarter: s => { spec = s; started = true; return fake; },
            tcpProbe: (h, p, t, ct) => Task.FromResult(started && (p is 502 or 12939)));

        SimulatorStartResult result = await launcher.StartAsync();

        Assert.Equal(SimulatorStartOutcome.StartedNewProcess, result.Outcome);
        Assert.True(result.Success);
        Assert.True(result.Owned);
        Assert.NotNull(result.Instance);
        Assert.Equal(4242, result.Instance.ProcessId);
        Assert.Equal(@"D:\sim", spec!.WorkingDirectory); // directory of the exe, lowercased on Windows
        Assert.Equal(@"D:\sim\h5u_daemon.exe", spec.ExecutablePath);
    }

    [Fact]
    public async Task StartAsync_HealthyForeignDaemon_IsReusedWithoutStartingAnything()
    {
        bool started = false;
        var launcher = new SimulatorLauncher(OfflineOptions(), fileExists: _ => true,
            processStarter: _ => { started = true; return new FakeSimulatorProcess(1); },
            tcpProbe: PortsUp(502, 12939));

        SimulatorStartResult result = await launcher.StartAsync();

        Assert.Equal(SimulatorStartOutcome.ReusedForeignInstance, result.Outcome);
        Assert.True(result.Success);
        Assert.False(result.Owned); // a daemon this launcher did not start must never be killed later
        Assert.False(started);
    }

    [Fact]
    public async Task StartAsync_OwnedInstanceStillAlive_IsReusedNotRestarted()
    {
        var fake = new FakeSimulatorProcess(77);
        int startCalls = 0;
        bool started = false;
        var launcher = new SimulatorLauncher(OfflineOptions(), fileExists: _ => true,
            processStarter: _ => { startCalls++; started = true; return fake; },
            tcpProbe: (h, p, t, ct) => Task.FromResult(started && (p is 502 or 12939)));

        await launcher.StartAsync();
        SimulatorStartResult second = await launcher.StartAsync();

        Assert.Equal(1, startCalls);
        Assert.Equal(SimulatorStartOutcome.ReusedOwnedInstance, second.Outcome);
        Assert.True(second.Owned);
        Assert.Equal(77, second.Instance!.ProcessId);
    }

    [Fact]
    public async Task StartAsync_MissingExecutable_FailsWithoutStarting()
    {
        bool started = false;
        var launcher = new SimulatorLauncher(OfflineOptions(), fileExists: _ => false,
            processStarter: _ => { started = true; return new FakeSimulatorProcess(1); },
            tcpProbe: PortsDown());

        SimulatorStartResult result = await launcher.StartAsync();

        Assert.Equal(SimulatorStartOutcome.Failed, result.Outcome);
        Assert.False(result.Success);
        Assert.False(started);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task StartAsync_PortsNeverOpen_CleansUpTheInstanceItStarted()
    {
        var fake = new FakeSimulatorProcess(999);
        var launcher = new SimulatorLauncher(
            new SimulatorLauncherOptions
            {
                ExecutablePath = @"D:\sim\h5u_daemon.exe",
                StartupTimeoutMs = 60,
                PollIntervalMs = 10,
                HealthTimeoutMs = 50
            },
            fileExists: _ => true,
            processStarter: _ => fake,
            tcpProbe: PortsDown());

        SimulatorStartResult result = await launcher.StartAsync();

        Assert.Equal(SimulatorStartOutcome.Failed, result.Outcome);
        Assert.False(result.Success);
        Assert.True(fake.KillCalled); // only the instance this launcher created is killed
        Assert.True(fake.KillEntireTree);
        Assert.Contains("stopped", result.Details, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // Launcher: stop — only own instances, never foreign ones
    // ---------------------------------------------------------------

    [Fact]
    public async Task StopAsync_KillsOnlyTheOwnedInstance_AndReportsPortsFreed()
    {
        var fake = new FakeSimulatorProcess(36636);
        HashSet<int> ports = [502, 12939];
        bool started = false;
        var launcher = new SimulatorLauncher(OfflineOptions(), fileExists: _ => true,
            processStarter: _ => { started = true; return fake; },
            tcpProbe: (h, p, t, ct) => Task.FromResult(started && ports.Contains(p)));

        await launcher.StartAsync();
        ports.Clear();
        SimulatorStopResult stop = await launcher.StopAsync();

        Assert.True(stop.Success);
        Assert.Single(stop.Entries);
        Assert.Equal(SimulatorStopOutcome.Killed, stop.Entries[0].Outcome);
        Assert.Equal(36636, stop.Entries[0].ProcessId);
        Assert.True(fake.KillCalled);
        Assert.True(fake.KillEntireTree);
        Assert.True(stop.PortsFreedAfterStop);
    }

    [Fact]
    public async Task StopAsync_ProcessNameChanged_RefusesToKill()
    {
        var fake = new FakeSimulatorProcess(1000);
        bool started = false;
        var launcher = new SimulatorLauncher(OfflineOptions(), fileExists: _ => true,
            processStarter: _ => { started = true; return fake; },
            tcpProbe: (h, p, t, ct) => Task.FromResult(started && (p is 502 or 12939)));

        await launcher.StartAsync();
        fake.ProcessName = "totally-not-the-daemon";
        SimulatorStopResult stop = await launcher.StopAsync();

        Assert.False(stop.Success);
        Assert.Equal(SimulatorStopOutcome.Refused, stop.Entries[0].Outcome);
        Assert.False(fake.KillCalled);
        Assert.Contains("NOT killed", stop.Entries[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopAsync_StartTimeChanged_PidReuseProtection_RefusesToKill()
    {
        var fake = new FakeSimulatorProcess(1001);
        bool started = false;
        var launcher = new SimulatorLauncher(OfflineOptions(), fileExists: _ => true,
            processStarter: _ => { started = true; return fake; },
            tcpProbe: (h, p, t, ct) => Task.FromResult(started && (p is 502 or 12939)));

        await launcher.StartAsync();
        fake.StartTimeUtc = fake.StartTimeUtc.AddHours(1); // simulates PID reuse by a different process
        SimulatorStopResult stop = await launcher.StopAsync();

        Assert.False(stop.Success);
        Assert.Equal(SimulatorStopOutcome.Refused, stop.Entries[0].Outcome);
        Assert.False(fake.KillCalled);
    }

    [Fact]
    public async Task StopAsync_NoOwnedInstance_ForeignDaemonLeftUntouched()
    {
        var launcher = new SimulatorLauncher(OfflineOptions(), fileExists: _ => true,
            processStarter: _ => throw new InvalidOperationException("must not start"),
            tcpProbe: PortsUp());

        SimulatorStopResult stop = await launcher.StopAsync();

        Assert.True(stop.Success); // nothing to stop is not a failure
        Assert.Empty(stop.Entries);
        Assert.Null(stop.PortsFreedAfterStop);
        Assert.Contains("NOT owned", stop.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopAsync_AlreadyExitedInstance_ReportedWithoutKill()
    {
        var fake = new FakeSimulatorProcess(1002);
        bool started = false;
        var launcher = new SimulatorLauncher(OfflineOptions(), fileExists: _ => true,
            processStarter: _ => { started = true; return fake; },
            tcpProbe: (h, p, t, ct) => Task.FromResult(started && (p is 502 or 12939)));

        await launcher.StartAsync();
        fake.HasExited = true;
        SimulatorStopResult stop = await launcher.StopAsync();

        Assert.True(stop.Success);
        Assert.Equal(SimulatorStopOutcome.AlreadyExited, stop.Entries[0].Outcome);
        Assert.False(fake.KillCalled);
    }

    // ---------------------------------------------------------------
    // Launcher: health check
    // ---------------------------------------------------------------

    [Fact]
    public async Task CheckHealthAsync_BothPortsUp_IsHealthy()
    {
        var launcher = new SimulatorLauncher(OfflineOptions(), tcpProbe: PortsUp(502, 12939));

        SimulatorHealthReport health = await launcher.CheckHealthAsync();

        Assert.True(health.ModbusPortReachable);
        Assert.True(health.NativePortReachable);
        Assert.True(health.OverallHealthy);
        Assert.False(health.OwnedInstanceAlive);
    }

    [Fact]
    public async Task CheckHealthAsync_OnePortDown_IsUnhealthyWithDetail()
    {
        var launcher = new SimulatorLauncher(OfflineOptions(), tcpProbe: PortsUp(502));

        SimulatorHealthReport health = await launcher.CheckHealthAsync();

        Assert.True(health.ModbusPortReachable);
        Assert.False(health.NativePortReachable);
        Assert.False(health.OverallHealthy);
        Assert.Contains("BOTH", health.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckHealthAsync_ReportsOwnedInstanceLiveness()
    {
        var fake = new FakeSimulatorProcess(555);
        bool started = false;
        var launcher = new SimulatorLauncher(OfflineOptions(), fileExists: _ => true,
            processStarter: _ => { started = true; return fake; },
            tcpProbe: (h, p, t, ct) => Task.FromResult(started && (p is 502 or 12939)));

        SimulatorHealthReport before = await launcher.CheckHealthAsync();
        await launcher.StartAsync();
        SimulatorHealthReport after = await launcher.CheckHealthAsync();

        Assert.False(before.OwnedInstanceAlive);
        Assert.True(after.OwnedInstanceAlive);
        Assert.Equal(555, after.OwnedProcessId);
    }

    // ---------------------------------------------------------------
    // Launcher: option validation (loopback red line encoded in code)
    // ---------------------------------------------------------------

    [Fact]
    public void LauncherOptions_RejectsNonLoopbackHostAndInvalidValues()
    {
        Assert.Throws<ArgumentException>(() => new SimulatorLauncher(
            new SimulatorLauncherOptions { HealthHost = "10.1.2.3" }));
        Assert.Throws<ArgumentException>(() => new SimulatorLauncher(
            new SimulatorLauncherOptions { ExecutablePath = "relative/path/h5u_daemon.exe" }));
        Assert.Throws<ArgumentException>(() => new SimulatorLauncher(
            new SimulatorLauncherOptions { ModbusPort = 70000 }));
        Assert.Throws<ArgumentException>(() => new SimulatorLauncher(
            new SimulatorLauncherOptions { HealthTimeoutMs = 10 }));
        Assert.Throws<ArgumentException>(() => new SimulatorLauncher(
            new SimulatorLauncherOptions { StartupTimeoutMs = 100, PollIntervalMs = 200 }));
    }

    // ---------------------------------------------------------------
    // LocalSimulator integration tests (real daemon on 127.0.0.1, opt-in)
    // ---------------------------------------------------------------

    [LocalSimulatorFact]
    [Trait("LocalSimulator", "1")]
    public async Task ClosedLoop_RealSimulator_FullChain_RecordsEvidence()
    {
        var launcher = new SimulatorLauncher();
        SimulatorStartResult start = await launcher.EnsureRunningAsync();
        Assert.True(start.Success, start.Details);

        SimulatorHealthReport health = await launcher.CheckHealthAsync();
        Assert.True(health.OverallHealthy, health.Details);

        var probe = new SimulatorProbe();
        SimulatorProbeReport probeReport = await probe.ProbeAsync();
        Assert.True(probeReport.ModbusProbeOk, probeReport.Details);

        // Adapter chain: the same mapping the MCP server uses (D->HR0, M->coil1024, X->DI0, R->HR32768).
        var adapter = new InovanceModbusTcpAdapter();
        (TargetProfile target, IReadOnlyList<TagDefinition> tags) = BuildSimTarget();
        IReadOnlyList<TagValue> values = await adapter.ReadAsync(target, tags);
        Assert.Equal(tags.Count, values.Count);
        Assert.All(values, v => Assert.Equal(QualityCode.Good, v.Quality));

        WriteClosedLoopEvidence(health, probeReport, values);

        // If this test itself started the daemon, leave a clean process state behind.
        if (start.Owned && start.Outcome == SimulatorStartOutcome.StartedNewProcess)
        {
            SimulatorStopResult stop = await launcher.StopAsync();
            Assert.True(stop.Success, stop.Details);
        }
    }

    [LocalSimulatorFact]
    [Trait("LocalSimulator", "1")]
    public async Task ClosedLoop_RealSimulator_AdapterReadsD100_M0_X0_R100_AllGood()
    {
        var adapter = new InovanceModbusTcpAdapter();
        (TargetProfile target, IReadOnlyList<TagDefinition> tags) = BuildSimTarget();

        IReadOnlyList<TagValue> values = await adapter.ReadAsync(target, tags);

        foreach (TagValue value in values)
        {
            Assert.Equal(QualityCode.Good, value.Quality);
            Assert.Null(value.Error);
        }
    }

    [LocalSimulatorFact]
    [Trait("LocalSimulator", "1")]
    public async Task ClosedLoop_RealSimulator_NativeProbe_ReportsOnlineWhenHarnessPresent()
    {
        var probe = new SimulatorProbe();
        NativeProbeResult native = await probe.ProbeNativeAsync();

        if (native.Attempted)
        {
            Assert.True(native.Online, $"TestHost output did not report ONLINE: {native.OutputTail}");
            Assert.Equal(0, native.ExitCode);
        }
        else
        {
            // Harness not installed on this machine: the skip must be explicit, never silent.
            Assert.True(native.Skipped);
            Assert.NotNull(native.SkipReason);
        }
    }

    private static (TargetProfile, IReadOnlyList<TagDefinition>) BuildSimTarget()
    {
        var target = new TargetProfile(
            "sim-inovance-live", PlcVendor.Inovance, "H5U", "H5U", null,
            new EndpointProfile("127.0.0.1", 502, TransportKind.ModbusTcp, Unit: 1, TimeoutMs: 3000),
            [TransportKind.ModbusTcp], null, new CapabilitySet([]), "simulator-local", IsSimulation: false);
        var tags = new List<TagDefinition>
        {
            new("D100", "D100", PlcDataType.Int32, ByteOrder: PlcByteOrder.BigEndian),
            new("M0", "M0", PlcDataType.Bool),
            new("X0", "X0", PlcDataType.Bool),
            new("R100", "R100", PlcDataType.Int16)
        };
        return (target, tags);
    }

    private static void WriteClosedLoopEvidence(
        SimulatorHealthReport health,
        SimulatorProbeReport probeReport,
        IReadOnlyList<TagValue> adapterValues)
    {
        // The closed-loop script sets this env var to a path inside the shared handoff
        // directory; without it the test writes nothing anywhere.
        string? resultPath = Environment.GetEnvironmentVariable("PLCMCP_SIMULATOR_RESULT_PATH");
        if (string.IsNullOrWhiteSpace(resultPath))
        {
            return;
        }

        var payload = new
        {
            recordedAt = DateTimeOffset.UtcNow,
            health,
            probe = new
            {
                probeReport.ModbusProbeOk,
                probeReport.Details,
                readings = probeReport.Readings.Select(r => new
                {
                    r.ProbeKind, address = r.Address, function = r.FunctionCode, values = r.Values,
                    latencyMs = r.LatencyMs, quality = r.Quality.ToString(), r.Error
                }),
                native = probeReport.Native
            },
            adapter = adapterValues.Select(v => new
            {
                v.Name, v.Value, dataType = v.DataType.ToString(), quality = v.Quality.ToString(),
                v.NativeAddress, timestamp = v.Timestamp
            })
        };
        File.WriteAllText(resultPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------

    private static SimulatorLauncherOptions OfflineOptions() => new()
    {
        ExecutablePath = @"D:\sim\h5u_daemon.exe",
        StartupTimeoutMs = 500,
        PollIntervalMs = 10,
        HealthTimeoutMs = 50
    };

    private static Func<string, int, TimeSpan, CancellationToken, Task<bool>> PortsUp(params int[] ports)
    {
        // Default: both simulator ports (Modbus 502 + native 12939) answer.
        HashSet<int> open = ports.Length == 0 ? [502, 12939] : [.. ports];
        return (host, port, timeout, ct) => Task.FromResult(open.Contains(port));
    }

    private static Func<string, int, TimeSpan, CancellationToken, Task<bool>> PortsDown() =>
        (host, port, timeout, ct) => Task.FromResult(false);

    private MockModbusServer StartMockServer(bool silent = false)
    {
        var server = new MockModbusServer(silent);
        _servers.Add(server);
        return server;
    }

    /// <summary>
    /// Loopback Modbus slave mock. Accepts any 12-byte read request, records it, and answers
    /// with a well-formed response. Register data bytes are 0x10, 0x11, ...; coil data byte 0
    /// is 0x03 (bits 0 and 1 set). When silent, accepts and never answers (timeout testing).
    /// </summary>
    private sealed class MockModbusServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly bool _silent;

        public MockModbusServer(bool silent)
        {
            _silent = silent;
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
                    var request = new byte[12];
                    await stream.ReadExactlyAsync(request).ConfigureAwait(false);
                    lock (Requests)
                    {
                        Requests.Add(request);
                    }

                    if (_silent)
                    {
                        // hold the connection open without answering; the probe times out
                        await Task.Delay(10_000).ConfigureAwait(false);
                        return;
                    }

                    byte[] response = BuildResponse(request);
                    await stream.WriteAsync(response).ConfigureAwait(false);
                }
            }
            catch
            {
                // client disconnected or listener stopped
            }
        }

        private static byte[] BuildResponse(byte[] request)
        {
            byte unit = request[6];
            byte function = request[7];
            ushort count = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(10));
            int dataBytes = function is 1 or 2 ? (count + 7) / 8 : count * 2;
            var response = new byte[9 + dataBytes];
            BinaryPrimitives.WriteUInt16BigEndian(response, BinaryPrimitives.ReadUInt16BigEndian(request));
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), (ushort)(3 + dataBytes));
            response[6] = unit;
            response[7] = function;
            response[8] = (byte)dataBytes;
            for (int i = 0; i < dataBytes; i++)
            {
                response[9 + i] = function is 1 or 2 ? (byte)0x03 : (byte)(0x10 + i);
            }

            return response;
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
        }
    }

    /// <summary>Fake daemon process with mutable identity for ownership-refusal tests.</summary>
    private sealed class FakeSimulatorProcess(int id) : ISimulatorProcess
    {
        public int Id { get; } = id;

        public string ProcessName { get; set; } = "h5u_daemon";

        public DateTime StartTimeUtc { get; set; } = DateTime.UtcNow;

        public bool HasExited { get; set; }

        public bool KillCalled { get; private set; }

        public bool KillEntireTree { get; private set; }

        public int WaitForExitCalls { get; private set; }

        public void Kill(bool entireProcessTree)
        {
            KillCalled = true;
            KillEntireTree = entireProcessTree;
            HasExited = true;
        }

        public bool WaitForExit(int milliseconds)
        {
            WaitForExitCalls++;
            return HasExited;
        }

        public void Dispose() { }
    }
}

/// <summary>
/// Fact that only runs against the real local AutoShop H5U simulator daemon on 127.0.0.1.
/// Skipped (never failed) unless PLCMCP_LOCAL_SIMULATOR=1, so CI and offline runs are unaffected.
/// See docs/inovance-autoshop-simulator.md and the closed-loop script in the handoff directory.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LocalSimulatorFactAttribute : FactAttribute
{
    public LocalSimulatorFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PLCMCP_LOCAL_SIMULATOR"), "1", StringComparison.Ordinal))
        {
            Skip = "Requires the local AutoShop H5U simulator daemon on 127.0.0.1; set PLCMCP_LOCAL_SIMULATOR=1 to enable (see docs/inovance-autoshop-simulator.md).";
        }
    }
}
