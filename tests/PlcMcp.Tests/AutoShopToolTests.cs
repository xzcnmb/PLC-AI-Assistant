using System.Text.Json;
using PlcMcp.Adapters;
using PlcMcp.Engineering.Workers.AutoShop;
using PlcMcp.Server.Governance;
using PlcMcp.Server.Mcp;

namespace PlcMcp.Tests;

/// <summary>
/// Offline tests for the AutoShop MCP tool gating, the aggregated plc_autoshop_status report and
/// the plc_autoshop_simulator governance (parameter validation, duplicate-start protection,
/// PID ownership protection for stop). All probes and process management are injected; no test
/// touches a real AutoShop installation, the registry, real TCP endpoints or real processes.
/// </summary>
public sealed class AutoShopToolTests
{
    private const string FakeAutoShopExe = @"C:\fake\AutoShop\AutoShop.exe";
    private const string FakeDaemonPath = @"C:\fake\AutoShop\Simulator\h5u_daemon.exe";
    private const string FakeSimulatorDir = @"C:\fake\AutoShop\Simulator";

    [Fact]
    public void DefaultMode_DoesNotRegisterAnyAutoShopTools()
    {
        var root = NewDataRoot();
        try
        {
            var router = new McpToolRouter(DefaultPlcComposition.Create(), new ServerGovernanceServices(root));
            Assert.DoesNotContain(router.ToolDescriptors, x => x.Name is
                "plc_autoshop_status" or "plc_autoshop_simulator" or
                "plc_autoshop_inspect" or "plc_autoshop_read_shared_variables");
            Assert.DoesNotContain(router.ToolDescriptors, x => x.Name is
                "plc_autoshop_compile" or "plc_autoshop_download" or
                "plc_autoshop_generate_project" or "plc_autoshop_monitor_window");
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BridgeEnabled_RegistersStatusAndSimulatorToolsWithHonestAnnotations()
    {
        var root = NewDataRoot();
        try
        {
            var router = new McpToolRouter(DefaultPlcComposition.Create(), CreateGovernance(root));
            var status = Assert.Single(router.ToolDescriptors, x => x.Name == "plc_autoshop_status");
            var simulator = Assert.Single(router.ToolDescriptors, x => x.Name == "plc_autoshop_simulator");

            // readOnly hints match actual behaviour: status is pure read; simulator start/stop
            // mutate local process state.
            Assert.True(status.ReadOnly);
            Assert.False(status.Destructive);
            Assert.True(status.Annotations.ReadOnlyHint);
            Assert.False(simulator.ReadOnly);
            Assert.False(simulator.Destructive);

            // Disclaimer wording is mandatory on every AutoShop tool.
            foreach (var descriptor in new[] { status, simulator })
            {
                Assert.Contains("4.12.0.2", descriptor.Description, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("no vendor endorsement", descriptor.Description, StringComparison.OrdinalIgnoreCase);
            }

            // The simulator schema exposes the three documented actions.
            using var schema = JsonDocument.Parse(JsonSerializer.Serialize(simulator.InputSchema));
            var actions = schema.RootElement.GetProperty("properties").GetProperty("action")
                .GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Equal(new[] { "status", "start", "stop" }, actions);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task Status_AggregatesEnvironmentSimulatorAndHonestCapabilityMatrix()
    {
        var root = NewDataRoot();
        try
        {
            var driver = new FakeSimulatorDriver
            {
                Processes = { new AutoShopSimulatorProcess(4242, "h5u_daemon", FakeDaemonPath) }
            };
            var governance = CreateGovernance(
                root,
                OfflineDetector(installed: true, itpListening: false),
                driver,
                new AutoShopSimulatorOptions
                {
                    DaemonPathCandidates = [FakeDaemonPath],
                    FileExists = _ => true,
                    // Loopback probes only: Modbus TCP up, native protocol down.
                    TcpProbe = (_, port) => Task.FromResult(port == 502)
                });
            var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);

            var payload = await CallToolJson(router, "plc_autoshop_status", "{}");

            Assert.True(payload.GetProperty("environment").GetProperty("installed").GetBoolean());
            Assert.Equal("4.12.0.2", payload.GetProperty("environment").GetProperty("version").GetString());
            Assert.True(payload.GetProperty("simulator").GetProperty("running").GetBoolean());
            Assert.Equal(4242, payload.GetProperty("simulator").GetProperty("runningPids")[0].GetInt32());
            Assert.True(payload.GetProperty("simulator").GetProperty("modbusTcp").GetProperty("listening").GetBoolean());
            Assert.False(payload.GetProperty("simulator").GetProperty("nativeProtocol").GetProperty("listening").GetBoolean());

            // Aggregated summary and disclaimer are present and honest.
            Assert.Contains("h5u_daemon running: True", payload.GetProperty("summary").GetString());
            Assert.Contains("no vendor endorsement", payload.GetProperty("disclaimer").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("4.12.0.2", payload.GetProperty("disclaimer").GetString(), StringComparison.OrdinalIgnoreCase);

            // Capability matrix: Experimental where evidence exists, Unsupported elsewhere.
            Assert.Equal("Experimental", CapStatus(payload, "environment_inspection"));
            Assert.Equal("Unsupported", CapStatus(payload, "itp_register_handshake"));
            Assert.Equal("Experimental", CapStatus(payload, "runtime_read_modbus_tcp"));
            Assert.Equal("Experimental", CapStatus(payload, "simulator_start"));
            Assert.Equal("Unsupported", CapStatus(payload, "shared_variable_read"));
            // F1 (2026-10-28): the inject-bridge compile gate is open by default —
            // the compile-all chain is F1-verified and invoked in-process (offline
            // behaviour, no PLC action), so the honest matrix status is Experimental.
            Assert.Equal("Experimental", CapStatus(payload, "compile"));
            Assert.Equal("Unsupported", CapStatus(payload, "download"));
            Assert.Equal("Unsupported", CapStatus(payload, "force"));
            Assert.Equal("Unsupported", CapStatus(payload, "plc_write"));
            Assert.Equal("Unsupported", CapStatus(payload, "plc_run_stop"));
            Assert.Equal("Unsupported", CapStatus(payload, "plugin_framework"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task SimulatorStart_StartsDaemonRecordsOwnershipAndLogs()
    {
        var root = NewDataRoot();
        try
        {
            var driver = new FakeSimulatorDriver { NextStartPid = 4242 };
            var governance = CreateGovernance(root, simulatorDriver: driver);
            var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);

            var payload = await CallToolJson(router, "plc_autoshop_simulator", """{"action":"start"}""");

            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.True(payload.GetProperty("started").GetBoolean());
            Assert.False(payload.GetProperty("alreadyRunning").GetBoolean());
            Assert.Equal(4242, payload.GetProperty("pid").GetInt32());

            // The minimal driver was started with the daemon path and its own directory.
            var (exePath, workingDirectory) = Assert.Single(driver.StartCalls);
            Assert.Equal(FakeDaemonPath, exePath);
            Assert.Equal(FakeSimulatorDir, workingDirectory);

            // Governance: ownership registry and on-disk JSONL log were written.
            Assert.Contains("4242", File.ReadAllText(governance.AutoShopSimulator!.OwnershipFile));
            string log = File.ReadAllText(governance.AutoShopSimulator!.LogFile);
            Assert.Contains("\"action\":\"start\"", log);
            Assert.Contains("4242", log);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task SimulatorStart_SecondCallWhenRunning_DoesNotStartDuplicate()
    {
        var root = NewDataRoot();
        try
        {
            var driver = new FakeSimulatorDriver { NextStartPid = 4242 };
            var governance = CreateGovernance(root, simulatorDriver: driver);
            var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);

            await CallToolJson(router, "plc_autoshop_simulator", """{"action":"start"}""");
            var second = await CallToolJson(router, "plc_autoshop_simulator", """{"action":"start"}""");

            Assert.True(second.GetProperty("success").GetBoolean());
            Assert.False(second.GetProperty("started").GetBoolean());
            Assert.True(second.GetProperty("alreadyRunning").GetBoolean());
            Assert.Equal(4242, second.GetProperty("pid").GetInt32());
            Assert.Single(driver.StartCalls);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task SimulatorStart_WhenExternalDaemonRunning_DoesNotStartAnother()
    {
        var root = NewDataRoot();
        try
        {
            var driver = new FakeSimulatorDriver
            {
                Processes = { new AutoShopSimulatorProcess(999, "h5u_daemon", FakeDaemonPath) }
            };
            var governance = CreateGovernance(root, simulatorDriver: driver);
            var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);

            var payload = await CallToolJson(router, "plc_autoshop_simulator", """{"action":"start"}""");

            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.False(payload.GetProperty("started").GetBoolean());
            Assert.True(payload.GetProperty("alreadyRunning").GetBoolean());
            Assert.Equal(999, payload.GetProperty("pid").GetInt32());
            Assert.Empty(driver.StartCalls);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task SimulatorStart_MissingExecutable_FailsWithoutFakeSuccess()
    {
        var root = NewDataRoot();
        try
        {
            var driver = new FakeSimulatorDriver { NextStartPid = 4242 };
            var governance = CreateGovernance(root, simulatorDriver: driver, fileExists: _ => false);
            var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);

            var payload = await CallToolJson(router, "plc_autoshop_simulator", """{"action":"start"}""");

            Assert.False(payload.GetProperty("success").GetBoolean());
            Assert.False(payload.GetProperty("started").GetBoolean());
            Assert.Contains("not found", payload.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(driver.StartCalls);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task SimulatorStart_ProcessExitsImmediately_FailsWithoutFakeSuccess()
    {
        var root = NewDataRoot();
        try
        {
            var driver = new FakeSimulatorDriver { NextStartPid = 7, IsAliveOverride = _ => false };
            var governance = CreateGovernance(root, simulatorDriver: driver);
            var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);

            var payload = await CallToolJson(router, "plc_autoshop_simulator", """{"action":"start"}""");

            Assert.False(payload.GetProperty("success").GetBoolean());
            Assert.True(payload.GetProperty("started").GetBoolean());
            Assert.Equal(7, payload.GetProperty("pid").GetInt32());
            Assert.Contains("exited", payload.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task SimulatorStart_AliveButNeverListening_FailsAfterBoundedTimeout()
    {
        var root = NewDataRoot();
        try
        {
            var driver = new FakeSimulatorDriver { NextStartPid = 7, IsAliveOverride = _ => true };
            var governance = CreateGovernance(
                root,
                simulatorDriver: driver,
                options: new AutoShopSimulatorOptions
                {
                    DaemonPathCandidates = [FakeDaemonPath],
                    FileExists = _ => true,
                    TcpProbe = (_, _) => Task.FromResult(false),
                    StartTimeout = TimeSpan.FromMilliseconds(400),
                    StartPollInterval = TimeSpan.FromMilliseconds(50)
                });
            var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);

            var payload = await CallToolJson(router, "plc_autoshop_simulator", """{"action":"start"}""");

            Assert.False(payload.GetProperty("success").GetBoolean());
            Assert.True(payload.GetProperty("started").GetBoolean());
            Assert.Contains("listening", payload.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
            // The process was started by this server, so stop can still clean it up.
            Assert.Contains("7", File.ReadAllText(governance.AutoShopSimulator!.OwnershipFile));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task SimulatorStop_StopsOnlyTheOwnedPid()
    {
        var root = NewDataRoot();
        try
        {
            var driver = new FakeSimulatorDriver { NextStartPid = 4242 };
            var governance = CreateGovernance(root, simulatorDriver: driver);
            var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);

            await CallToolJson(router, "plc_autoshop_simulator", """{"action":"start"}""");
            var payload = await CallToolJson(router, "plc_autoshop_simulator", """{"action":"stop"}""");

            Assert.True(payload.GetProperty("success").GetBoolean());
            Assert.True(payload.GetProperty("stopped").GetBoolean());
            Assert.Equal(4242, payload.GetProperty("pid").GetInt32());
            Assert.Equal(new[] { 4242 }, driver.KillCalls);
            Assert.DoesNotContain("4242", File.ReadAllText(governance.AutoShopSimulator!.OwnershipFile));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task SimulatorStop_RefusesPidNotOwnedByThisServer()
    {
        var root = NewDataRoot();
        try
        {
            var driver = new FakeSimulatorDriver
            {
                Processes = { new AutoShopSimulatorProcess(999, "h5u_daemon", FakeDaemonPath) }
            };
            var governance = CreateGovernance(root, simulatorDriver: driver);
            var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);

            // No owned process at all.
            var none = await CallToolJson(router, "plc_autoshop_simulator", """{"action":"stop"}""");
            Assert.False(none.GetProperty("success").GetBoolean());
            Assert.Contains("ownership", none.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);

            // An externally started daemon must never be stopped, even when explicitly named.
            var external = await CallToolJson(router, "plc_autoshop_simulator", """{"action":"stop","pid":999}""");
            Assert.False(external.GetProperty("success").GetBoolean());
            Assert.Contains("not started by this server", external.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(driver.KillCalls);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task SimulatorStop_RefusesPidWithIdentityMismatch()
    {
        var root = NewDataRoot();
        try
        {
            var driver = new FakeSimulatorDriver
            {
                Processes = { new AutoShopSimulatorProcess(4242, "h5u_daemon", @"C:\fake\Other\unrelated.exe") },
                ExePathOverride = _ => @"C:\fake\Other\unrelated.exe"
            };
            var governance = CreateGovernance(root, simulatorDriver: driver);
            var ownershipFile = governance.AutoShopSimulator!.OwnershipFile;
            Directory.CreateDirectory(governance.AutoShopSimulator.StateDirectory);
            var seed = new
            {
                entries = new[]
                {
                    new { pid = 4242, exePath = FakeDaemonPath, launchedAtUtc = DateTimeOffset.UtcNow }
                }
            };
            File.WriteAllText(ownershipFile, JsonSerializer.Serialize(seed));

            var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);
            var payload = await CallToolJson(router, "plc_autoshop_simulator", """{"action":"stop"}""");

            Assert.False(payload.GetProperty("success").GetBoolean());
            Assert.Contains("PID ownership protection", payload.GetProperty("message").GetString());
            Assert.Empty(driver.KillCalls);
            // The recorded entry is kept for manual inspection.
            Assert.Contains("4242", File.ReadAllText(ownershipFile));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task SimulatorStop_MultipleOwnedPids_RequiresExplicitPid()
    {
        var root = NewDataRoot();
        try
        {
            var driver = new FakeSimulatorDriver
            {
                Processes =
                {
                    new AutoShopSimulatorProcess(100, "h5u_daemon", FakeDaemonPath),
                    new AutoShopSimulatorProcess(200, "h5u_daemon", FakeDaemonPath)
                }
            };
            var governance = CreateGovernance(root, simulatorDriver: driver);
            var ownershipFile = governance.AutoShopSimulator!.OwnershipFile;
            Directory.CreateDirectory(governance.AutoShopSimulator.StateDirectory);
            var seed = new
            {
                entries = new[]
                {
                    new { pid = 100, exePath = FakeDaemonPath, launchedAtUtc = DateTimeOffset.UtcNow },
                    new { pid = 200, exePath = FakeDaemonPath, launchedAtUtc = DateTimeOffset.UtcNow }
                }
            };
            File.WriteAllText(ownershipFile, JsonSerializer.Serialize(seed));
            var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);

            var ambiguous = await CallToolJson(router, "plc_autoshop_simulator", """{"action":"stop"}""");
            Assert.False(ambiguous.GetProperty("success").GetBoolean());
            Assert.Contains("explicit integer 'pid'", ambiguous.GetProperty("message").GetString());
            Assert.Empty(driver.KillCalls);

            var specific = await CallToolJson(router, "plc_autoshop_simulator", """{"action":"stop","pid":100}""");
            Assert.True(specific.GetProperty("success").GetBoolean());
            Assert.Equal(new[] { 100 }, driver.KillCalls);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task Simulator_UnknownOrMissingAction_IsRejected()
    {
        var root = NewDataRoot();
        try
        {
            var router = new McpToolRouter(DefaultPlcComposition.Create(), CreateGovernance(root));
            var unknown = await CallToolError(router, "plc_autoshop_simulator", """{"action":"restart"}""");
            Assert.Contains("status, start, stop", unknown);
            var missing = await CallToolError(router, "plc_autoshop_simulator", "{}");
            Assert.Contains("action", missing);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task StatusAndSimulatorTools_WithoutBridge_AreUnknownTools()
    {
        var root = NewDataRoot();
        try
        {
            var router = new McpToolRouter(DefaultPlcComposition.Create(), new ServerGovernanceServices(root));
            var status = await CallToolError(router, "plc_autoshop_status", "{}");
            Assert.Contains("Unknown tool", status);
            var simulator = await CallToolError(router, "plc_autoshop_simulator", """{"action":"status"}""");
            Assert.Contains("Unknown tool", simulator);
        }
        finally { Cleanup(root); }
    }

    private static ServerGovernanceServices CreateGovernance(
        string dataRoot,
        AutoShopEnvironmentDetector? detector = null,
        FakeSimulatorDriver? simulatorDriver = null,
        AutoShopSimulatorOptions? options = null,
        Func<string, bool>? fileExists = null)
    {
        var simOptions = options ?? new AutoShopSimulatorOptions
        {
            DaemonPathCandidates = [FakeDaemonPath],
            FileExists = fileExists ?? (_ => true),
            // Loopback probes only; the Modbus TCP endpoint answers by default.
            TcpProbe = (_, port) => Task.FromResult(port == 502)
        };
        return new ServerGovernanceServices(
            dataRoot,
            null,
            new AutoShopBridgeConfig(),
            autoShopDetector: detector ?? OfflineDetector(installed: true, itpListening: false),
            autoShopSimulatorOptions: simOptions,
            autoShopSimulatorDriver: simulatorDriver ?? new FakeSimulatorDriver());
    }

    private static AutoShopEnvironmentDetector OfflineDetector(bool installed, bool itpListening) =>
        new(new AutoShopDetectionOptions
        {
            ExecutableCandidates = [FakeAutoShopExe],
            FileExists = _ => installed,
            FileVersionReader = _ => "4.12.0.2",
            ServiceProcessProbe = _ => false,
            RegistryValueReader = (_, _) => null,
            TcpProbe = (_, _) => Task.FromResult(itpListening),
            NewestLogFileProbe = _ => null
        });

    private static async Task<JsonElement> CallToolJson(McpToolRouter router, string tool, string argsJson)
    {
        using var args = JsonDocument.Parse(argsJson);
        object response = await router.CallToolAsync(tool, args.RootElement.Clone(), CancellationToken.None);
        string responseJson = JsonSerializer.Serialize(response, JsonRpc.SerializerOptions);
        using var doc = JsonDocument.Parse(responseJson);
        Assert.False(doc.RootElement.GetProperty("isError").GetBoolean());
        string text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString()
            ?? throw new InvalidOperationException("Tool result has no text content.");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> CallToolError(McpToolRouter router, string tool, string argsJson)
    {
        using var args = JsonDocument.Parse(argsJson);
        object response = await router.CallToolAsync(tool, args.RootElement.Clone(), CancellationToken.None);
        string responseJson = JsonSerializer.Serialize(response, JsonRpc.SerializerOptions);
        using var doc = JsonDocument.Parse(responseJson);
        Assert.True(doc.RootElement.GetProperty("isError").GetBoolean());
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
    }

    private static string CapStatus(JsonElement payload, string name) =>
        payload.GetProperty("capabilities").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == name)
            .GetProperty("status").GetString();

    private static string NewDataRoot() =>
        Path.Combine(Path.GetTempPath(), "plcmcp_autoshop_tools_" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    /// <summary>In-memory process driver; never touches the real process table.</summary>
    private sealed class FakeSimulatorDriver : IAutoShopSimulatorProcessDriver
    {
        public List<AutoShopSimulatorProcess> Processes { get; } = [];
        public List<(string ExePath, string WorkingDirectory)> StartCalls { get; } = [];
        public List<int> KillCalls { get; } = [];
        public int? NextStartPid { get; set; }
        public string? NextStartError { get; set; }
        public Func<int, bool>? IsAliveOverride { get; set; }
        public Func<int, string?>? ExePathOverride { get; set; }
        public Func<int, bool>? KillResultOverride { get; set; }

        public IReadOnlyList<AutoShopSimulatorProcess> FindProcesses(string processName) =>
            Processes.Where(p => p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)).ToArray();

        public int? Start(string exePath, string workingDirectory, out string? error)
        {
            StartCalls.Add((exePath, workingDirectory));
            if (NextStartPid is int pid)
                Processes.Add(new AutoShopSimulatorProcess(pid, "h5u_daemon", exePath));
            error = NextStartError;
            return NextStartPid;
        }

        public bool IsAlive(int pid) =>
            IsAliveOverride?.Invoke(pid) ?? Processes.Any(p => p.Pid == pid);

        public string? GetExecutablePath(int pid) =>
            ExePathOverride?.Invoke(pid) ?? Processes.FirstOrDefault(p => p.Pid == pid)?.ExePath;

        public bool TryKill(int pid, out string? error)
        {
            KillCalls.Add(pid);
            error = null;
            if (KillResultOverride is not null) return KillResultOverride(pid);
            Processes.RemoveAll(p => p.Pid == pid);
            return true;
        }
    }
}
