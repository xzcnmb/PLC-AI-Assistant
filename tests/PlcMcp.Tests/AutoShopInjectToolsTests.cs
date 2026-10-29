using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PlcMcp.Adapters;
using PlcMcp.AutoShopNativeBridge.Crypto;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Workers.AutoShop;
using PlcMcp.Engineering.Workers.AutoShop.InjectBridge;
using PlcMcp.Engineering.Workers.AutoShop.ProjectFormat;
using PlcMcp.Server.Governance;
using PlcMcp.Server.Mcp;

namespace PlcMcp.Tests;

/// <summary>
/// Offline tests for the E4 AutoShop inject-bridge MCP tools (plc_autoshop_open_project,
/// plc_autoshop_compile, plc_autoshop_generate_project, plc_autoshop_monitor_read):
/// directory validation, fresh-.hcp preparation (E3 construction), the four-state window
/// classification, compile artifact polling + UCodeCrypt decryption summaries, project
/// generation round trips, gate conditions and the no-bridge registration behaviour.
///
/// ALL tests are offline: the injector, the window probe and the pipe transport are
/// injected fakes; no test starts AutoShop, touches the real process table, the registry
/// or a physical PLC. The single real end-to-end chain is env-gated
/// (<see cref="InjectLocalFactAttribute"/>) and skipped unless PLCMCP_LOCAL_SIMULATOR=1.
/// </summary>
public sealed class AutoShopInjectToolsTests : IDisposable
{
    private const string FakeAutoShopExe = @"C:\fake\AutoShop\AutoShop.exe";
    private const string FakeInjector = @"C:\fake\AutoShopInjectBridgeInjector.exe";
    private const string FakeBridgeDll = @"C:\fake\AutoShopInjectBridge.dll";

    private readonly List<string> _tempDirs = [];
    private readonly List<MockInjectBridgeServer> _servers = [];

    public void Dispose()
    {
        foreach (MockInjectBridgeServer server in _servers)
        {
            server.Dispose();
        }
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch { }
        }
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------
    // Registration / gating
    // ---------------------------------------------------------------

    [Fact]
    public void BridgeEnabled_RegistersInjectToolsWithHonestAnnotations()
    {
        string root = NewTempDir("gov");
        var services = new ServerGovernanceServices(root, null, new AutoShopBridgeConfig(),
            autoShopInjectOptions: new AutoShopInjectToolsOptions
            {
                AutoShopExecutablePath = FakeAutoShopExe,
                InjectorExecutablePath = FakeInjector,
                BridgeDllPath = FakeBridgeDll
            });
        var router = new McpToolRouter(DefaultPlcComposition.Create(), services);

        var open = Assert.Single(router.ToolDescriptors, x => x.Name == "plc_autoshop_open_project");
        var compile = Assert.Single(router.ToolDescriptors, x => x.Name == "plc_autoshop_compile");
        var generate = Assert.Single(router.ToolDescriptors, x => x.Name == "plc_autoshop_generate_project");
        var monitor = Assert.Single(router.ToolDescriptors, x => x.Name == "plc_autoshop_monitor_read");
        var axis = Assert.Single(router.ToolDescriptors, x => x.Name == "plc_autoshop_axis_config");
        var write = Assert.Single(router.ToolDescriptors, x => x.Name == "plc_autoshop_write_register");
        var download = Assert.Single(router.ToolDescriptors, x => x.Name == "plc_autoshop_download");
        var runStop = Assert.Single(router.ToolDescriptors, x => x.Name == "plc_autoshop_run_stop");

        // readOnly/destructive hints: monitor_read is read-only; axis_config is not
        // (it writes project files, never PLC); write_register is a destructive write;
        // G3: download and run_stop are PLC actions on the loopback simulator —
        // destructive, never read-only.
        Assert.False(open.ReadOnly);
        Assert.False(open.Destructive);
        Assert.False(compile.ReadOnly);
        Assert.False(compile.Destructive);
        Assert.False(generate.ReadOnly);
        Assert.False(generate.Destructive);
        Assert.True(monitor.ReadOnly);
        Assert.False(monitor.Destructive);
        Assert.False(axis.ReadOnly);
        Assert.False(axis.Destructive);
        Assert.False(write.ReadOnly);
        Assert.True(write.Destructive);
        Assert.False(download.ReadOnly);
        Assert.True(download.Destructive);
        Assert.False(runStop.ReadOnly);
        Assert.True(runStop.Destructive);

        // Disclaimer wording is mandatory on every tool; no tool claims PLC data or writes.
        foreach (var descriptor in new[] { open, compile, generate, monitor, axis, write, download, runStop })
        {
            Assert.Contains("4.12.0.2", descriptor.Description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no vendor endorsement", descriptor.Description, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("journaled", open.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Experimental", compile.Description, StringComparison.Ordinal);
        Assert.Contains("no PLC action", compile.Description, StringComparison.Ordinal);
        Assert.Contains("in-process", monitor.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CALL-LEVEL evidence", monitor.Description, StringComparison.Ordinal);
        Assert.Contains("WRITE-BACK is not closed", monitor.Description, StringComparison.OrdinalIgnoreCase);
        // G1 wording fix: the raw-HWND sentence is replaced; the blocker is named.
        Assert.DoesNotContain("frame-HWND delivery experiment faults", monitor.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("session establishment", monitor.Description, StringComparison.Ordinal);
        Assert.Contains("delivered", monitor.Description, StringComparison.Ordinal);
        Assert.Contains("no axis project", axis.Description, StringComparison.Ordinal);
        Assert.Contains("FORBIDDEN", write.Description, StringComparison.Ordinal);
        Assert.Contains("--autoshop-allow-physical-write", write.Description, StringComparison.Ordinal);
        // G3: honest loopback-simulator scope + physical gate wording.
        Assert.Contains("--autoshop-allow-physical-download", download.Description, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", download.Description, StringComparison.Ordinal);
        Assert.Contains("simulator", download.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LOAD is a stub", download.Description, StringComparison.Ordinal);
        Assert.Contains("NOT activated", download.Description, StringComparison.Ordinal);
        Assert.Contains("--autoshop-allow-physical-download", runStop.Description, StringComparison.Ordinal);
        Assert.Contains("0xFF000020", runStop.Description, StringComparison.Ordinal);
        Assert.Contains("D100", runStop.Description, StringComparison.Ordinal);
        Assert.Contains("Local simulator demo ONLY", runStop.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DefaultMode_DoesNotRegisterInjectTools()
    {
        string root = NewTempDir("govnone");
        try
        {
            var router = new McpToolRouter(DefaultPlcComposition.Create(), new ServerGovernanceServices(root));
            Assert.DoesNotContain(router.ToolDescriptors, x => x.Name is
                "plc_autoshop_open_project" or "plc_autoshop_compile" or
                "plc_autoshop_generate_project" or "plc_autoshop_monitor_read" or
                "plc_autoshop_axis_config" or "plc_autoshop_write_register" or
                "plc_autoshop_download" or "plc_autoshop_run_stop");
            string downloadError = await CallToolError(router, "plc_autoshop_download", """{"projectDir":"C:\\x"}""");
            Assert.Contains("Unknown tool", downloadError);
            string runStopError = await CallToolError(router, "plc_autoshop_run_stop", """{"action":"run"}""");
            Assert.Contains("Unknown tool", runStopError);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task CallTool_WithoutBridge_UnknownTool()
    {
        string root = NewTempDir("govcall");
        try
        {
            var router = new McpToolRouter(DefaultPlcComposition.Create(), new ServerGovernanceServices(root));
            string error = await CallToolError(router, "plc_autoshop_open_project", """{"projectDir":"C:\\x"}""");
            Assert.Contains("Unknown tool", error);
        }
        finally { Cleanup(root); }
    }

    // ---------------------------------------------------------------
    // Open project: directory validation
    // ---------------------------------------------------------------

    [Fact]
    public async Task OpenProject_MissingProjectDir_FailsWithoutLaunching()
    {
        var harness = new FakeInjectHarness();
        AutoShopInjectTools tools = NewTools(harness: harness);
        var report = await tools.OpenProjectAsync(@"C:\plcmcp_missing_dir_no_such");

        Assert.False(report.Success);
        Assert.Equal(AutoShopOpenOutcome.LauncherFailed.ToString(), report.Outcome);
        Assert.Empty(harness.Commands); // the injector was never run
    }

    [Fact]
    public async Task OpenProject_DirWithoutHcpOrHcpp_Fails()
    {
        var harness = new FakeInjectHarness();
        AutoShopInjectTools tools = NewTools(harness: harness);
        string dir = NewTempDir("empty");
        var report = await tools.OpenProjectAsync(dir);

        Assert.False(report.Success);
        Assert.Contains("neither a .hcp nor a .hcpp", report.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Commands);
    }

    [Fact]
    public async Task OpenProject_DirInsideInstallTree_Rejected()
    {
        var harness = new FakeInjectHarness();
        AutoShopInjectTools tools = NewTools(harness: harness);
        var report = await tools.OpenProjectAsync(@"C:\fake\AutoShop\projects");

        Assert.False(report.Success);
        Assert.Contains("installation directory", report.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Commands);
    }

    [Fact]
    public async Task OpenProject_DriveRoot_Rejected()
    {
        var harness = new FakeInjectHarness();
        AutoShopInjectTools tools = NewTools(harness: harness);
        var report = await tools.OpenProjectAsync(@"C:\");

        Assert.False(report.Success);
        Assert.Contains("drive root", report.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Commands);
    }

    // ---------------------------------------------------------------
    // Open project: fresh .hcp preparation (E3 construction)
    // ---------------------------------------------------------------

    [Fact]
    public async Task OpenProject_HcppOnly_ReunpacksFreshHcpAndOpens()
    {
        string dir = NewTempDir("openfresh");
        byte[] hcpBytes = BuildTestProjectSource(dir); // writes only the .hcpp source
        var harness = new FakeInjectHarness();
        AutoShopInjectTools tools = NewTools(harness: harness, probe: pid => SuccessSnapshot(dir));

        var report = await tools.OpenProjectAsync(dir);

        Assert.True(report.Success, report.Message);
        Assert.True(report.HcpFreshCopy);
        Assert.NotNull(report.HcpPath);
        // The fresh .hcp decrypts back to the project XML.
        Assert.True(AutoShopProjectCrypt.TryDecryptProjectXml(File.ReadAllBytes(report.HcpPath!), out string? xml, out string? error),
            error ?? "decrypt failed");
        Assert.Contains("<project>", xml);
        // Declared files were written byte-faithfully (with the 31-byte header).
        string varList = Path.Combine(dir, "VarList.gdt");
        Assert.True(File.Exists(varList));
        Assert.Equal("AutoShop", Encoding.ASCII.GetString(File.ReadAllBytes(varList), 0, 8));
        // The injector received the .hcp path as the AutoShop command line.
        Assert.Single(harness.Commands);
        Assert.Contains(report.HcpPath!, harness.Commands[0].Arguments);
        // The session is kept for compile/monitor.
        Assert.NotNull(tools.CurrentSession);
        Assert.Equal(dir, tools.CurrentSession!.ProjectDir);
        Assert.Equal(hcpBytes, File.ReadAllBytes(report.HcpPath!));
    }

    [Fact]
    public async Task OpenProject_WindowSuccess_KeepsSessionAndReportsTitle()
    {
        string dir = NewTempDir("openok");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness();
        AutoShopInjectTools tools = NewTools(harness: harness, probe: pid => SuccessSnapshot(dir));

        var report = await tools.OpenProjectAsync(dir);

        Assert.True(report.Success, report.Message);
        Assert.Equal(AutoShopOpenOutcome.Opened.ToString(), report.Outcome);
        Assert.Equal(4242, report.Pid);
        Assert.Contains(Path.GetFileName(dir), report.WindowTitle, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(report.DialogEvidence);
        Assert.NotNull(tools.CurrentSession);
        Assert.Empty(harness.KilledPids); // success keeps the instance alive
    }

    [Fact]
    public async Task OpenProject_FailureDialogs_MapFourStatesAndCleanUp()
    {
        (string DialogText, AutoShopOpenOutcome Expected)[] cases =
        [
            ("加载Hcp文件中的工程属性信息错误，工程打开失败！请尝试重新打开工程目录下Hcpp文件！", AutoShopOpenOutcome.PropertyLoadFailed),
            ("所选工程文件版本高于当前程序，请先升级程序", AutoShopOpenOutcome.VersionIncompatible),
            ("打开工程失败", AutoShopOpenOutcome.OpenFailed),
            ("工程 \"C:\\x\" 已不存在或文件路径超出系统限制！", AutoShopOpenOutcome.FileNotFound),
            ("打开文件：VarList.gdt 时出错", AutoShopOpenOutcome.FileOpenError),
            ("某种完全未知的对话框文本", AutoShopOpenOutcome.UnknownDialog),
        ];

        foreach ((string dialogText, AutoShopOpenOutcome expected) in cases)
        {
            string dir = NewTempDir("opendialog");
            BuildTestProjectSource(dir);
            var harness = new FakeInjectHarness();
            AutoShopInjectTools tools = NewTools(harness: harness,
                probe: pid => new AutoShopWindowSnapshot([], ["#32770"], [dialogText]));

            var report = await tools.OpenProjectAsync(dir);

            Assert.False(report.Success, dialogText);
            Assert.Equal(expected.ToString(), report.Outcome);
            Assert.Contains(dialogText, string.Join(" | ", report.DialogEvidence), StringComparison.Ordinal);
            // Self-start, self-clean: the instance the launcher created was stopped again.
            Assert.Equal(new[] { 4242 }, harness.KilledPids);
            Assert.Null(tools.CurrentSession);
        }
    }

    [Fact]
    public void Classification_BenignLoadingDialog_KeepsPolling()
    {
        var loading = AutoShopOpenClassification.Classify(
            new AutoShopWindowSnapshot(["AutoShop V4.12.0.2"], ["#32770"], ["正在加载中"]), "p2", "模板A.hcp");
        Assert.Equal(AutoShopOpenOutcome.NoWindow, loading.Outcome);
        Assert.Contains("poll continues", loading.Evidence, StringComparison.Ordinal);

        // A benign dialog TOGETHER with a failure dialog still classifies as the failure state.
        var mixed = AutoShopOpenClassification.Classify(
            new AutoShopWindowSnapshot([], ["#32770"], ["正在加载中", "加载Hcp文件中的工程属性信息错误，工程打开失败！请尝试重新打开工程目录下Hcpp文件！"]),
            "p2", "模板A.hcp");
        Assert.Equal(AutoShopOpenOutcome.PropertyLoadFailed, mixed.Outcome);
    }

    [Fact]
    public async Task OpenProject_BenignLoadingDialog_ContinuesUntilSuccess()
    {
        string dir = NewTempDir("openbenign");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness();
        int polls = 0;
        AutoShopInjectTools tools = NewTools(harness: harness, probe: pid => ++polls == 1
            ? new AutoShopWindowSnapshot(["AutoShop V4.12.0.2"], ["#32770"], ["正在加载中"])
            : SuccessSnapshot(dir));

        var report = await tools.OpenProjectAsync(dir);

        Assert.True(report.Success, report.Message);
        Assert.Equal(AutoShopOpenOutcome.Opened.ToString(), report.Outcome);
        Assert.True(polls >= 2);
        Assert.Empty(harness.KilledPids);
    }

    [Fact]
    public void Classification_MapsEvidenceWithoutPipes()
    {
        var success = AutoShopOpenClassification.Classify(SuccessSnapshot(@"C:\proj\p2"), "p2", "模板A.hcp");
        Assert.Equal(AutoShopOpenOutcome.Opened, success.Outcome);

        var noMatch = AutoShopOpenClassification.Classify(
            new AutoShopWindowSnapshot(["AutoShop V4.12.0.2 - [MAIN]"], [], []), "p2", "模板A.hcp");
        Assert.Equal(AutoShopOpenOutcome.NoWindow, noMatch.Outcome);

        var none = AutoShopOpenClassification.Classify(null, "p2", "模板A.hcp");
        Assert.Equal(AutoShopOpenOutcome.NoWindow, none.Outcome);

        var version = AutoShopOpenClassification.Classify(
            new AutoShopWindowSnapshot([], ["对话框"], ["Hcp文件版本不兼容，工程打开失败！请升级Autoshop！"]), "p2", "模板A.hcp");
        Assert.Equal(AutoShopOpenOutcome.VersionIncompatible, version.Outcome);
    }

    [Fact]
    public async Task OpenProject_NoWindowWithinTimeout_FailsBoundedAndCleansUp()
    {
        string dir = NewTempDir("opennone");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness();
        AutoShopInjectTools tools = NewTools(harness: harness, probe: pid => null,
            openTimeout: TimeSpan.FromMilliseconds(400), openPoll: TimeSpan.FromMilliseconds(50));

        DateTimeOffset start = DateTimeOffset.UtcNow;
        var report = await tools.OpenProjectAsync(dir);

        Assert.False(report.Success);
        Assert.Equal(AutoShopOpenOutcome.NoWindow.ToString(), report.Outcome);
        Assert.True((DateTimeOffset.UtcNow - start).TotalMilliseconds < 5000, "the poll must stay bounded");
        Assert.Equal(new[] { 4242 }, harness.KilledPids);
    }

    [Fact]
    public async Task OpenProject_LauncherFailure_ReportsStageWithoutFakeSuccess()
    {
        string dir = NewTempDir("openlaunch");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness
        {
            InjectorExitCode = 2,
            InjectorStdout = "{\"event\":\"failed\",\"stage\":\"validate-exe\",\"reason\":\"not x86\"}"
        };
        AutoShopInjectTools tools = NewTools(harness: harness, probe: pid => SuccessSnapshot(dir));

        var report = await tools.OpenProjectAsync(dir);

        Assert.False(report.Success);
        Assert.Equal(AutoShopOpenOutcome.LauncherFailed.ToString(), report.Outcome);
        Assert.Contains("validate-exe", report.Message, StringComparison.Ordinal);
        Assert.Empty(harness.KilledPids); // nothing was created
        Assert.Null(tools.CurrentSession);
    }

    [Fact]
    public async Task OpenProject_HandshakeFailure_CleansUpAndFails()
    {
        string dir = NewTempDir("openhshake");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness { HandshakeAnswered = false };
        AutoShopInjectTools tools = NewTools(harness: harness, probe: pid => SuccessSnapshot(dir));

        var report = await tools.OpenProjectAsync(dir);

        Assert.False(report.Success);
        Assert.Equal(AutoShopOpenOutcome.LauncherFailed.ToString(), report.Outcome);
        Assert.Contains("handshake", report.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(tools.CurrentSession);
    }

    [Fact]
    public async Task OpenProject_SecondCall_StopsPreviousInstance()
    {
        string dir = NewTempDir("opentwice");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness();
        AutoShopInjectTools tools = NewTools(harness: harness, probe: pid => SuccessSnapshot(dir));

        await tools.OpenProjectAsync(dir);
        var second = await tools.OpenProjectAsync(dir);

        Assert.True(second.Success, second.Message);
        Assert.Equal(new[] { 4242 }, harness.KilledPids); // the previous instance was stopped
        Assert.Equal(2, harness.Commands.Count);
    }

    [Fact]
    public async Task OpenProject_LogsToDisk()
    {
        string dir = NewTempDir("openlog");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness();
        AutoShopInjectTools tools = NewTools(harness: harness, probe: pid => SuccessSnapshot(dir));

        await tools.OpenProjectAsync(dir);

        string log = File.ReadAllText(tools.LogFile);
        Assert.Contains("\"action\":\"open_project\"", log, StringComparison.Ordinal);
        Assert.Contains("4242", log);
    }

    // ---------------------------------------------------------------
    // Compile: gate, polling, decryption
    // ---------------------------------------------------------------

    [Fact]
    public async Task Compile_WithoutOpenProject_HonestUnsupported()
    {
        AutoShopInjectTools tools = NewTools();
        var report = await tools.CompileAsync();

        Assert.False(report.Success);
        Assert.Equal("Unsupported", report.Status);
        Assert.Contains("plc_autoshop_open_project", report.Message, StringComparison.Ordinal);
        Assert.False(report.BridgeAnswered); // no bridge call was made
    }

    [Fact]
    public async Task Compile_BridgeUnsupported_ReportsVerbatimReason()
    {
        string dir = NewTempDir("cmpunsp");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness();
        var mock = StartMock(reply => BuildError(reply, InjectBridgeProtocol.ErrUnsupported,
            "Unsupported: compile entry RVA 0x15a2ec0 is configured but its calling convention and signature are not verified"));
        AutoShopInjectTools tools = NewTools(harness: harness, pipeName: mock.PipeName, probe: pid => SuccessSnapshot(dir));
        await tools.OpenProjectAsync(dir);

        var report = await tools.CompileAsync();

        Assert.False(report.Success);
        Assert.Equal("Unsupported", report.Status);
        Assert.True(report.BridgeAnswered);
        Assert.Contains("Unsupported", report.BridgeMessage, StringComparison.Ordinal);
        Assert.Contains("0x15a2ec0", report.BridgeMessage, StringComparison.Ordinal);
        Assert.Equal("compile", mock.LastMethod); // the request went on the wire honestly
        Assert.Equal(dir, report.ProjectDir);
    }

    [Fact]
    public async Task Compile_PollsAndDecryptsNewArtifacts()
    {
        string dir = NewTempDir("cmppoll");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness();
        byte[] payload = { 0x53, 0x41, 0x03, 0x00, 0x02, 0x00, 0x01, 0x50 };
        Directory.CreateDirectory(Path.Combine(dir, "Compile"));
        // A parallel/previous trigger drops the artifact while the compile request is in flight.
        var mock = StartMock(reply => reply.Method == "compile"
            ? DropAndFail(reply, Path.Combine(dir, "Compile", "SBR_001.compile"), payload)
            : BuildError(reply, -32601, "method not found"));
        AutoShopInjectTools tools = NewTools(harness: harness, pipeName: mock.PipeName, probe: pid => SuccessSnapshot(dir),
            unsupportedPoll: TimeSpan.FromSeconds(2), pollInterval: TimeSpan.FromMilliseconds(50));
        await tools.OpenProjectAsync(dir);

        var report = await tools.CompileAsync();

        Assert.Equal("Unsupported", report.Status); // the bridge still answered Unsupported honestly
        AutoShopCompileArtifact artifact = Assert.Single(report.Artifacts, a => a.Name == "SBR_001.compile");
        Assert.True(artifact.IsNew);
        Assert.True(artifact.DecryptedOk);
        Assert.StartsWith("5341030002000150", artifact.DecryptedHeadHex, StringComparison.Ordinal);
        Assert.Contains("NOT evidence", report.Message, StringComparison.Ordinal); // no fake success
    }

    [Fact]
    public async Task Compile_BridgeAccepts_ArtifactsVerified()
    {
        string dir = NewTempDir("cmpok");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness();
        byte[] payload = { 0x53, 0x41, 0x03, 0x00, 0x02, 0x00, 0x01, 0x50 };
        Directory.CreateDirectory(Path.Combine(dir, "Compile"));
        var mock = StartMock(reply => reply.Method == "compile"
            ? DropAndAccept(reply, Path.Combine(dir, "Compile", "SBR_001.compile"), payload)
            : BuildError(reply, -32601, "method not found"));
        AutoShopInjectTools tools = NewTools(harness: harness, pipeName: mock.PipeName, probe: pid => SuccessSnapshot(dir),
            compilePoll: TimeSpan.FromSeconds(2), pollInterval: TimeSpan.FromMilliseconds(50));
        await tools.OpenProjectAsync(dir);

        var report = await tools.CompileAsync();

        Assert.True(report.Success, report.Message);
        Assert.Equal("Experimental", report.Status); // never above Experimental
        Assert.True(report.BridgeAnswered);
        Assert.Contains("submitted", report.BridgeMessage, StringComparison.Ordinal); // the F1 chain submit receipt
        AutoShopCompileArtifact artifact = Assert.Single(report.Artifacts, a => a.Name == "SBR_001.compile");
        Assert.True(artifact.IsNew);
        Assert.True(artifact.DecryptedOk);
        Assert.False(report.PollTimedOut);
    }

    [Fact]
    public async Task Compile_BridgeProjectGateFailure_ReportsReasonWithoutFakeSuccess()
    {
        // The bridge checked the 0x161B620 project gate first and found no project:
        // the clear gate reason surfaces verbatim; nothing is claimed as compiled.
        string dir = NewTempDir("cmpgate");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness();
        var mock = StartMock(reply => reply.Method == "compile"
            ? BuildError(reply, InjectBridgeProtocol.ErrVendorCallFailed,
                "compile refused/failed (rc=-32002): project gate (0x161B620) returned 0: no project is open in this AutoShop instance; compile-all was NOT invoked")
            : BuildError(reply, -32601, "method not found"));
        AutoShopInjectTools tools = NewTools(harness: harness, pipeName: mock.PipeName, probe: pid => SuccessSnapshot(dir),
            unsupportedPoll: TimeSpan.FromMilliseconds(400), pollInterval: TimeSpan.FromMilliseconds(50));
        await tools.OpenProjectAsync(dir);

        var report = await tools.CompileAsync();

        Assert.False(report.Success);
        Assert.True(report.BridgeAnswered);
        Assert.Contains("project gate", report.BridgeMessage, StringComparison.Ordinal);
        Assert.Contains("compile-all was NOT invoked", report.BridgeMessage, StringComparison.Ordinal);
        Assert.Equal("compile", mock.LastMethod);
    }

    [Fact]
    public async Task Compile_PreExistingArtifacts_AreNotClaimedAsNew()
    {
        string dir = NewTempDir("cmpbase");
        BuildTestProjectSource(dir);
        // A pre-existing artifact from an earlier run.
        Directory.CreateDirectory(Path.Combine(dir, "Compile"));
        byte[] payload = { 0x53, 0x41, 0x03, 0x00 };
        File.WriteAllBytes(Path.Combine(dir, "Compile", "SBR_001.compile"), AutoShopProjectCrypt.EncryptBlob(payload));

        var harness = new FakeInjectHarness();
        var mock = StartMock(reply => BuildError(reply, InjectBridgeProtocol.ErrUnsupported, "Unsupported: no compile entry point"));
        AutoShopInjectTools tools = NewTools(harness: harness, pipeName: mock.PipeName, probe: pid => SuccessSnapshot(dir),
            unsupportedPoll: TimeSpan.FromMilliseconds(400), pollInterval: TimeSpan.FromMilliseconds(50));
        await tools.OpenProjectAsync(dir);

        var report = await tools.CompileAsync();

        Assert.False(report.Success);
        AutoShopCompileArtifact artifact = Assert.Single(report.Artifacts, a => a.Name == "SBR_001.compile");
        Assert.False(artifact.IsNew); // the baseline: reported with evidence, never as "new"
        Assert.True(artifact.DecryptedOk);
    }

    // ---------------------------------------------------------------
    // Generate project: XML construction and container round trips
    // ---------------------------------------------------------------

    [Fact]
    public async Task Generate_MinimalProject_RoundTripsOffline()
    {
        string dir = NewTempDir("gen");
        AutoShopInjectTools tools = NewTools();

        var report = await tools.GenerateProjectAsync(dir, "H3U", null, null, null, null, null);

        Assert.True(report.Success, report.RoundTripError ?? report.Message);
        Assert.True(report.RoundTripOk);
        Assert.NotNull(report.HcpPath);
        Assert.NotNull(report.HcppPath);
        Assert.True(File.Exists(report.HcpPath));
        Assert.True(File.Exists(report.HcppPath));

        // The .hcp decrypts to the project XML: root, Version=1, GCMModal, declared files.
        byte[] hcpBytes = File.ReadAllBytes(report.HcpPath!);
        Assert.True(AutoShopProjectCrypt.TryDecryptProjectXml(hcpBytes, out string? xml, out string? error), error);
        XElement root = XElement.Parse(xml!);
        Assert.Equal("project", root.Name.LocalName);
        Assert.Equal("1", root.Element("Version")?.Value);
        Assert.Equal("H3U", root.Element("GCMModal")?.Value);
        Assert.Equal(3, root.Elements("file").Count());
        var fileNames = root.Elements("file").Elements("FileName").Select(e => e.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Subset(fileNames, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MAIN.LD", "VarList.gdt", "Config.sdt" });

        // Declared files exist with the 31-byte AutoShop header as their full content.
        foreach (string name in new[] { "MAIN.LD", "VarList.gdt", "Config.sdt" })
        {
            byte[] content = File.ReadAllBytes(Path.Combine(dir, name));
            Assert.Equal(31, content.Length); // header-only placeholder payload
            Assert.Equal("AutoShop", Encoding.ASCII.GetString(content, 0, 8));
        }

        // The .hcpp container parses and its main blob matches the .hcp bytes.
        Assert.True(HcppReader.TryRead(File.ReadAllBytes(report.HcppPath!), out HcppContainer? container, out string? parseError), parseError);
        Assert.NotNull(container!.MainRecord);
        Assert.True(container.MainRecord!.MainBlob!.AsSpan().SequenceEqual(hcpBytes));
        Assert.Equal(3, container.Files.Count);
    }

    [Fact]
    public async Task Generate_ContainerBytesRoundTrip_ThroughCrypt()
    {
        string dir = NewTempDir("genrt");
        AutoShopInjectTools tools = NewTools();

        var report = await tools.GenerateProjectAsync(dir, "H5U", "RT 项目", null, null, null, null);

        Assert.True(report.Success, report.RoundTripError ?? report.Message);
        byte[] hcpBytes = File.ReadAllBytes(report.HcpPath!);
        byte[] plain = AutoShopProjectCrypt.DecryptBlob(hcpBytes);
        // Decrypt(Encrypt(x)) == x (exact inverse, known-answer self-test of the crypt pair).
        Assert.True(AutoShopProjectCrypt.EncryptBlob(plain).AsSpan().SequenceEqual(hcpBytes));
        string xml = Encoding.Unicode.GetString(plain, 2, plain.Length - 2);
        Assert.StartsWith("<?xml ", xml, StringComparison.Ordinal);
        XElement root = XElement.Parse(xml);
        Assert.Equal("H5U", root.Element("GCMModal")?.Value);
        Assert.Contains("RT 项目", root.Element("ProjectDesc")?.Value);
    }

    [Fact]
    public async Task Generate_FromSourceHcpp_UnpacksByteFaithfully()
    {
        string dir = NewTempDir("gensrc");
        byte[] hcpBytes = AutoShopProjectCrypt.EncryptBlob(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n<project>\r\n\t<Version>1</Version>\r\n" +
            "\t<GCMModal>H3U</GCMModal>\r\n\t<HCPFileVer>1</HCPFileVer>\r\n</project>")).ToArray());
        string sourceHcpp = Path.Combine(dir, "SrcProj.hcpp");
        File.WriteAllBytes(sourceHcpp, HcppWriter.Write(
            new HcppMainRecordSpec("SrcProj.hcp", hcpBytes),
            new[] { new HcppFileSpec("VarList.gdt", Encoding.ASCII.GetBytes("test-content")) }));

        AutoShopInjectTools tools = NewTools();
        string outDir = NewTempDir("genout");
        var report = await tools.GenerateProjectAsync(outDir, null, null, null, null, sourceHcpp, null);

        Assert.True(report.Success, report.RoundTripError ?? report.Message);
        // The generated .hcp is the source main blob, byte-identical.
        Assert.True(File.ReadAllBytes(report.HcpPath!).AsSpan().SequenceEqual(hcpBytes));
        // Declared files keep their FULL record bytes (31-byte header included).
        byte[] varList = File.ReadAllBytes(Path.Combine(outDir, "VarList.gdt"));
        Assert.Equal("AutoShop", Encoding.ASCII.GetString(varList, 0, 8));
        Assert.EndsWith("test-content", Encoding.ASCII.GetString(varList), StringComparison.Ordinal);
        // The container copy parses; the model was read from the decrypted XML.
        Assert.Contains("file record(s) unpacked byte-faithfully", report.XmlSummary, StringComparison.Ordinal);
        Assert.True(HcppReader.TryRead(File.ReadAllBytes(report.HcppPath!), out HcppContainer? container, out _));
        Assert.Equal("SrcProj.hcp", container!.MainRecord!.MainProjectName);
    }

    [Fact]
    public async Task Generate_RejectsInstallDir()
    {
        AutoShopInjectTools tools = NewTools();
        var report = await tools.GenerateProjectAsync(@"C:\fake\AutoShop\gen", null, null, null, null, null, null);

        Assert.False(report.Success);
        Assert.Contains("installation directory", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_RejectsOverlongProjectName()
    {
        string dir = NewTempDir("genlong");
        AutoShopInjectTools tools = NewTools();
        var report = await tools.GenerateProjectAsync(dir, "H3U", new string('x', 65), null, null, null, null);

        Assert.False(report.Success);
        Assert.Contains("1..64", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_MissingSourceHcpp_FailsHonest()
    {
        string dir = NewTempDir("genmiss");
        AutoShopInjectTools tools = NewTools();
        var report = await tools.GenerateProjectAsync(dir, null, null, null, null, @"C:\plcmcp_no_such.hcpp", null);

        Assert.False(report.Success);
        Assert.Contains("not found", report.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------
    // Monitor read: call receipts only, never PLC data
    // ---------------------------------------------------------------

    [Fact]
    public async Task MonitorRead_WithoutSession_HonestUnsupported()
    {
        AutoShopInjectTools tools = NewTools();
        var report = await tools.MonitorReadAsync("D100");

        Assert.False(report.Success);
        Assert.Equal("Unsupported", report.Status);
        Assert.Contains("plc_autoshop_open_project", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MonitorRead_WithSession_ReturnsVerbatimCallReceipts()
    {
        string dir = NewTempDir("monok");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness();
        var mock = StartMock(reply => reply.Method switch
        {
            "set_target" => BuildResult(reply, "{\"stored\":true,\"ip\":\"127.0.0.1\",\"port\":12939,\"loopbackOnly\":true}"),
            "read_status" => BuildResult(reply, "{\"execMode\":0,\"execModeCallResult\":0,\"gcmState\":0," +
                "\"gcmStateCallResult\":0,\"evidence\":\"call-return codes only; loopback target; no PLC verified\"}"),
            "read_elements" => BuildResult(reply, "{\"count\":2,\"values\":\"D100=4660;M0=1\",\"note\":\"in-process vendor read: " +
                "CSoftAddrMap filled via tagSoftAddr 5-arg ctor + CMap::SetAt, tag+0x20 value slot\"}"),
            _ => BuildError(reply, -32601, "method not found")
        });
        AutoShopInjectTools tools = NewTools(harness: harness, pipeName: mock.PipeName, probe: pid => SuccessSnapshot(dir));
        await tools.OpenProjectAsync(dir);

        var report = await tools.MonitorReadAsync("D100,M0");

        Assert.True(report.Success, report.Message);
        Assert.Equal("Experimental", report.Status);
        Assert.Equal(4242, report.HostPid);
        Assert.Equal("127.0.0.1:12939", report.TargetEndpoint);
        Assert.True(report.TargetStored);
        Assert.True(report.StatusReceiptSucceeded);
        Assert.Contains("execMode=0", report.StatusReceipt, StringComparison.Ordinal);
        Assert.True(report.ElementReceiptSucceeded);
        Assert.Contains("count=2", report.ElementReceipt, StringComparison.Ordinal);
        // The in-process values (tag+0x20 slot) flow through to the receipt.
        Assert.Contains("values=D100=4660;M0=1", report.ElementReceipt, StringComparison.Ordinal);
        Assert.Contains("in-process vendor read", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MonitorRead_G1DeliveryVerdict_FlowsVerbatim()
    {
        // G1 (docs/inovance-autoshop-monitor-values.md): the bridge answers
        // read_elements with delivered=N + a dynamic verdict note. The tool
        // must surface it verbatim — a delivered read says PLC data, an
        // undelivered read says CALL-LEVEL, and the tool never softens it.
        string dir = NewTempDir("mong1");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness();
        var mock = StartMock(reply => reply.Method switch
        {
            "set_target" => BuildResult(reply, "{\"stored\":true,\"ip\":\"127.0.0.1\",\"port\":12939,\"loopbackOnly\":true}"),
            "read_status" => BuildResult(reply, "{\"execMode\":0,\"execModeCallResult\":0,\"gcmState\":0,\"gcmStateCallResult\":0,\"evidence\":\"x\"}"),
            "read_elements" => BuildResult(reply,
                "{\"count\":1,\"delivered\":1,\"values\":\"M0=1\",\"valueSlots\":\"M0=1\"," +
                "\"watchRc\":-1,\"activeMap\":\"0x00000000\",\"activeMapCount\":-1,\"onlineBefore\":\"1\"," +
                "\"variant\":\"wnd=null(proven-safe baseline);\"," +
                "\"note\":\"delivered=1 - VALUE WRITE-BACK CLOSED: daemon-answered reads; these values are PLC data\"}"),
            _ => BuildError(reply, -32601, "method not found")
        });
        AutoShopInjectTools tools = NewTools(harness: harness, pipeName: mock.PipeName, probe: pid => SuccessSnapshot(dir));
        await tools.OpenProjectAsync(dir);

        var report = await tools.MonitorReadAsync("M0");

        Assert.True(report.Success, report.Message);
        Assert.Contains("delivered=1", report.ElementReceipt, StringComparison.Ordinal);
        Assert.Contains("VALUE WRITE-BACK CLOSED", report.ElementReceipt, StringComparison.Ordinal);
        Assert.Contains("M0=1", report.ElementReceipt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MonitorRead_G1ZeroDelivered_KeepsCallLevelHonesty()
    {
        string dir = NewTempDir("mong1z");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness();
        var mock = StartMock(reply => reply.Method switch
        {
            "set_target" => BuildResult(reply, "{\"stored\":true,\"ip\":\"127.0.0.1\",\"port\":12939,\"loopbackOnly\":true}"),
            "read_status" => BuildResult(reply, "{\"execMode\":0,\"execModeCallResult\":0,\"gcmState\":0,\"gcmStateCallResult\":0,\"evidence\":\"x\"}"),
            "read_elements" => BuildResult(reply,
                "{\"count\":2,\"delivered\":0,\"values\":\"D100=0?;M0=0?\",\"valueSlots\":\"D100=-;M0=-\"," +
                "\"note\":\"delivered=0 - VALUE WRITE-BACK NOT OBSERVED: values remain CALL-LEVEL evidence\"}"),
            _ => BuildError(reply, -32601, "method not found")
        });
        AutoShopInjectTools tools = NewTools(harness: harness, pipeName: mock.PipeName, probe: pid => SuccessSnapshot(dir));
        await tools.OpenProjectAsync(dir);

        var report = await tools.MonitorReadAsync("D100,M0");

        Assert.True(report.Success, report.Message);
        Assert.Contains("delivered=0", report.ElementReceipt, StringComparison.Ordinal);
        Assert.Contains("NOT OBSERVED", report.ElementReceipt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MonitorRead_AllZeroValues_AreStillAClosedLoop()
    {
        // Simulator registers are all 0: "value=0 with a successful read" is the
        // honest closed-loop evidence; no failure is fabricated.
        string dir = NewTempDir("monzero");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness();
        var mock = StartMock(reply => reply.Method switch
        {
            "set_target" => BuildResult(reply, "{\"stored\":true,\"ip\":\"127.0.0.1\",\"port\":12939,\"loopbackOnly\":true}"),
            "read_status" => BuildResult(reply, "{\"execMode\":0,\"execModeCallResult\":0,\"gcmState\":0,\"gcmStateCallResult\":0,\"evidence\":\"x\"}"),
            "read_elements" => BuildResult(reply, "{\"count\":2,\"values\":\"D200=0;M1=0\",\"note\":\"in-process vendor read\"}"),
            _ => BuildError(reply, -32601, "method not found")
        });
        AutoShopInjectTools tools = NewTools(harness: harness, pipeName: mock.PipeName, probe: pid => SuccessSnapshot(dir));
        await tools.OpenProjectAsync(dir);

        var report = await tools.MonitorReadAsync("D200,M1");

        Assert.True(report.Success, report.Message);
        Assert.Equal("Experimental", report.Status);
        Assert.Contains("D200=0;M1=0", report.ElementReceipt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MonitorRead_TargetIsHardcodedLoopback()
    {
        string dir = NewTempDir("monloop");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness();
        var mock = StartMock(reply => reply.Method switch
        {
            "set_target" => BuildResult(reply, "{\"stored\":true,\"ip\":\"127.0.0.1\",\"port\":12939,\"loopbackOnly\":true}"),
            "read_status" => BuildResult(reply, "{\"execMode\":0,\"execModeCallResult\":0,\"gcmState\":0,\"gcmStateCallResult\":0,\"evidence\":\"x\"}"),
            "read_elements" => BuildResult(reply, "{\"count\":0,\"note\":\"x\"}"),
            _ => BuildError(reply, -32601, "method not found")
        });
        AutoShopInjectTools tools = NewTools(harness: harness, pipeName: mock.PipeName, probe: pid => SuccessSnapshot(dir));
        await tools.OpenProjectAsync(dir);

        var report = await tools.MonitorReadAsync("D100");

        Assert.True(report.Success, report.Message);
        // The tool has no target parameter: the set_target ip is always 127.0.0.1 (loopback by construction).
        Assert.Equal("127.0.0.1", mock.LastSetTargetIp);
    }

    [Fact]
    public async Task MonitorRead_PreconditionsMissing_ReportsReason()
    {
        string dir = NewTempDir("monmiss");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness();
        var mock = StartMock(reply => reply.Method switch
        {
            "set_target" => BuildResult(reply, "{\"stored\":true,\"ip\":\"127.0.0.1\",\"port\":12939,\"loopbackOnly\":true}"),
            "read_status" => BuildError(reply, InjectBridgeProtocol.ErrLoopbackRequired, "no loopback target stored"),
            "read_elements" => BuildError(reply, InjectBridgeProtocol.ErrLoopbackRequired, "no loopback target stored"),
            _ => BuildError(reply, -32601, "method not found")
        });
        AutoShopInjectTools tools = NewTools(harness: harness, pipeName: mock.PipeName, probe: pid => SuccessSnapshot(dir));
        await tools.OpenProjectAsync(dir);

        var report = await tools.MonitorReadAsync("D100");

        Assert.False(report.Success);
        Assert.Equal("Unsupported", report.Status);
        Assert.Contains("-32003", report.StatusReceipt, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // Axis config: structural read, edit round trips, no-axis honesty
    // ---------------------------------------------------------------

    /// <summary>Builds a synthetic .hcpp with an xml\axis.xml member (F2 template).</summary>
    private static byte[] BuildAxisProjectSource(string dir, bool withAxis)
    {
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n<project>\r\n\t<Version>1</Version>\r\n" +
            "\t<GCMModal>H5U</GCMModal>\r\n\t<HCPFileVer>1</HCPFileVer>\r\n</project>";
        byte[] hcpBytes = AutoShopProjectCrypt.EncryptBlob(
            Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray());
        byte[] axisXml = Encoding.UTF8.GetBytes(AxisConfigXml.BuildTemplate("G1", new[]
        {
            (AxisKind.X, "X1", "Pulse", true, 1000.0, -1000.0, 5.0),
            (AxisKind.Y, "Y1", "Pulse", false, 2000.0, -2000.0, 10.0)
        }));
        var files = new List<HcppFileSpec> { new("VarList.gdt", []) };
        if (withAxis)
        {
            files.Add(new HcppFileSpec("xml/axis.xml", axisXml));
        }
        byte[] hcpp = HcppWriter.Write(new HcppMainRecordSpec("AxisProj.hcp", hcpBytes), files);
        File.WriteAllBytes(Path.Combine(dir, "AxisProj.hcpp"), hcpp);
        return hcpp;
    }

    [Fact]
    public async Task AxisConfig_ReadOnly_ListsAxes()
    {
        string dir = NewTempDir("axisread");
        BuildAxisProjectSource(dir, withAxis: true);
        AutoShopInjectTools tools = NewTools();

        var report = await tools.AxisConfigAsync(dir, null, open: false);

        Assert.True(report.Success, report.Message);
        Assert.Equal("Experimental", report.Status);
        Assert.True(report.HasAxisFile);
        Assert.Equal(2, report.Axes.Count);
        Assert.Equal("X1", report.Axes[0].Name);
        Assert.Equal(AxisKind.X.ToString(), report.Axes[0].Kind);
        Assert.Equal(1000.0, report.Axes[0].ForwardLimitValue);
        Assert.Equal(2000.0, report.Axes[1].ForwardLimitValue);
        Assert.Empty(report.AppliedEdits);
        Assert.False(report.Repacked);
        // No-axis members would be a lie here; the honest note names the schema caveat.
        Assert.Contains("reverse-engineered", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AxisConfig_NoAxisMember_ReportsNoAxisProject()
    {
        string dir = NewTempDir("axisnone");
        BuildTestProjectSource(dir); // a container without an axis member
        AutoShopInjectTools tools = NewTools();

        var report = await tools.AxisConfigAsync(dir, null, open: false);

        Assert.True(report.Success, report.Message); // a read-only miss is not an error
        Assert.False(report.HasAxisFile);
        Assert.Empty(report.Axes);
        Assert.Contains("No axis project", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AxisConfig_EditWithoutAxisMember_FailsHonest()
    {
        string dir = NewTempDir("axiseditnone");
        BuildTestProjectSource(dir);
        AutoShopInjectTools tools = NewTools();

        var report = await tools.AxisConfigAsync(dir,
            new Dictionary<string, object?> { ["X1.ForwardLimitValue"] = 4321.5 }, open: false);

        Assert.False(report.Success);
        Assert.False(report.HasAxisFile);
        Assert.Contains("No axis project", report.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be applied", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AxisConfig_EditAndRepackage_RoundTripsOffline()
    {
        string dir = NewTempDir("axisedit");
        BuildAxisProjectSource(dir, withAxis: true);
        AutoShopInjectTools tools = NewTools();

        var report = await tools.AxisConfigAsync(dir, new Dictionary<string, object?>
        {
            ["X1.ForwardLimitValue"] = 4321.5,
            ["Y1.EnableLimitSet"] = true
        }, open: false);

        Assert.True(report.Success, report.RoundTripError ?? report.Message);
        Assert.True(report.Repacked);
        Assert.True(report.RoundTripOk);
        Assert.Equal(2, report.AppliedEdits.Count);
        Assert.Equal("X1", report.AppliedEdits[0].AxisName);
        Assert.Equal("ForwardLimitValue", report.AppliedEdits[0].Field);
        // The .hcpp on disk carries the edit.
        byte[] onDisk = File.ReadAllBytes(report.HcppPath!);
        Assert.True(AxisPackageEditor.TryLoad(onDisk, out AxisConfigDocument? doc, out bool hasAxis, out string? error), error);
        Assert.True(hasAxis);
        Assert.Equal(4321.5, doc!.Axes.Single(a => a.Name == "X1").ForwardLimitValue);
        Assert.True(doc.Axes.Single(a => a.Name == "Y1").EnableLimitSet);
        // The main blob still decrypts (D3 loop intact).
        Assert.True(AutoShopProjectCrypt.TryDecryptProjectXml(
            HcppReader.Read(onDisk).MainRecord!.MainBlob!, out string? xml, out _), "main blob lost");
        Assert.Contains("<project>", xml);
    }

    [Fact]
    public async Task AxisConfig_UnknownField_FailsWithSupportedFieldList()
    {
        string dir = NewTempDir("axisunk");
        BuildAxisProjectSource(dir, withAxis: true);
        AutoShopInjectTools tools = NewTools();

        var report = await tools.AxisConfigAsync(dir,
            new Dictionary<string, object?> { ["X1.NotAField"] = 1 }, open: false);

        Assert.False(report.Success);
        Assert.Contains("Unknown axis field", report.Message, StringComparison.Ordinal);
        Assert.Contains("ForwardLimitValue", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AxisConfig_OpenValidation_ReopensTheProject()
    {
        string dir = NewTempDir("axisopen");
        BuildAxisProjectSource(dir, withAxis: true);
        var harness = new FakeInjectHarness();
        AutoShopInjectTools tools = NewTools(harness: harness, probe: pid => SuccessSnapshot(dir));

        var report = await tools.AxisConfigAsync(dir,
            new Dictionary<string, object?> { ["X1.ForwardLimitValue"] = 4321.5 }, open: true);

        Assert.True(report.Success, report.RoundTripError ?? report.Message);
        Assert.Equal(AutoShopOpenOutcome.Opened.ToString(), report.OpenOutcome);
        Assert.Single(harness.Commands); // the open ran once through the owned instance
        Assert.Contains("Re-open validation: Opened", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AxisConfig_MissingHcpp_ReportsHonest()
    {
        string dir = NewTempDir("axismissing");
        AutoShopInjectTools tools = NewTools();

        var report = await tools.AxisConfigAsync(dir, null, open: false);

        Assert.False(report.Success);
        Assert.Contains("No .hcpp project package", report.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // Write register: loopback gates, physical switch, fake server
    // ---------------------------------------------------------------

    /// <summary>In-memory Modbus TCP server (loopback-free): stateful FC1/3/5/6.</summary>
    private sealed class FakeModbusServer
    {
        private readonly Dictionary<ushort, bool> _coils = [];
        private readonly Dictionary<ushort, ushort> _registers = [];
        public int TransactionCounter { get; private set; }

        public Stream Duplex() => new FakeModbusStream(this);

        public byte[] Handle(byte[] request)
        {
            int transaction = (request[0] << 8) | request[1];
            TransactionCounter++;
            byte fc = request[7];
            ushort address = (ushort)((request[8] << 8) | request[9]);
            ushort count = (ushort)((request[10] << 8) | request[11]);
            byte[] response;
            switch (fc)
            {
                case 5:
                {
                    bool on = request[10] == 0xFF;
                    _coils[address] = on;
                    response = (byte[])request.Clone(); // echo
                    break;
                }
                case 6:
                {
                    ushort value = (ushort)((request[10] << 8) | request[11]);
                    _registers[address] = value;
                    response = (byte[])request.Clone(); // echo
                    break;
                }
                case 1:
                {
                    var data = new List<byte>();
                    for (int i = 0; i < count; i++)
                    {
                        bool on = _coils.TryGetValue((ushort)(address + i), out bool v) && v;
                        if (i % 8 == 0) data.Add(0);
                        if (on) data[^1] |= (byte)(1 << (i % 8));
                    }
                    response = BuildReadResponse(transaction, fc, data.ToArray());
                    break;
                }
                case 3:
                {
                    var data = new List<byte>();
                    for (int i = 0; i < count; i++)
                    {
                        ushort v = _registers.TryGetValue((ushort)(address + i), out ushort rv) ? rv : (ushort)0;
                        data.Add((byte)(v >> 8));
                        data.Add((byte)(v & 0xFF));
                    }
                    response = BuildReadResponse(transaction, fc, data.ToArray());
                    break;
                }
                default:
                    response = [request[0], request[1], 0, 0, 0, 3, request[6], (byte)(fc | 0x80), 1];
                    break;
            }
            return response;
        }

        private static byte[] BuildReadResponse(int transaction, byte fc, byte[] data)
        {
            var response = new byte[9 + data.Length];
            response[0] = (byte)(transaction >> 8);
            response[1] = (byte)(transaction & 0xFF);
            response[4] = 0;
            response[5] = (byte)(3 + data.Length);
            response[6] = 1;
            response[7] = fc;
            response[8] = (byte)data.Length;
            data.CopyTo(response, 9);
            return response;
        }

        /// <summary>Duplex stream that answers one Modbus response per request (stateful).</summary>
        private sealed class FakeModbusStream : Stream
        {
            private readonly FakeModbusServer _server;
            private byte[] _pending = [];

            public FakeModbusStream(FakeModbusServer server) => _server = server;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                _pending = _server.Handle(buffer.ToArray());
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                int n = Math.Min(buffer.Length, _pending.Length);
                _pending.AsSpan(0, n).CopyTo(buffer.Span);
                _pending = _pending[n..];
                return n;
            }

            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override void Write(byte[] buffer, int offset, int count) =>
                WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override long Length => _pending.Length;
            public override long Position { get => 0; set => throw new NotSupportedException(); }
        }
    }

    private static TargetProfile SimulatorTarget(int port = 502) =>
        new("sim-inovance", PlcVendor.Inovance, "H5U", null, null,
            new("127.0.0.1", port, TransportKind.ModbusTcp, Unit: 1, TimeoutMs: 3000),
            [TransportKind.ModbusTcp], null, new([]), "simulator-readonly", IsSimulation: true);

    private static TargetProfile PhysicalTarget() =>
        new("plc-inovance", PlcVendor.Inovance, "H5U", null, null,
            new("192.168.1.10", 502, TransportKind.ModbusTcp, Unit: 1, TimeoutMs: 3000),
            [TransportKind.ModbusTcp], null, new([]), "physical-readonly");

    private static AutoShopWriteRegisterService NewWriteService(AutoShopWriteRegisterOptions options, string stateDir)
    {
        Directory.CreateDirectory(stateDir);
        return new AutoShopWriteRegisterService(Path.Combine(stateDir, "write.log"), options);
    }

    [Fact]
    public async Task WriteRegister_LoopbackSimulationTarget_SucceedsWithReadback()
    {
        string stateDir = NewTempDir("wrstate");
        var server = new FakeModbusServer();
        var service = NewWriteService(new AutoShopWriteRegisterOptions
        {
            TransportFactory = (_, _) => Task.FromResult<Stream>(server.Duplex())
        }, stateDir);

        var word = await service.WriteAsync(SimulatorTarget(), "D100", 4660, allowPhysicalWrite: false);
        var coil = await service.WriteAsync(SimulatorTarget(), "M0", true, allowPhysicalWrite: false);

        Assert.True(word.Success, word.Message);
        Assert.Equal("Experimental", word.Status);
        Assert.Equal((byte)6, word.FunctionCode); // FC6 single register
        Assert.Equal("holding-register", word.Area);
        Assert.Equal((ushort)4660, Assert.IsType<ushort>(word.ReadBackValue));
        Assert.True(coil.Success, coil.Message);
        Assert.Equal((byte)5, coil.FunctionCode); // FC5 single coil
        Assert.Equal("coil", coil.Area);
        Assert.Equal(true, Assert.IsType<bool>(coil.ReadBackValue));
    }

    [Fact]
    public async Task WriteRegister_PhysicalTarget_DefaultForbidden()
    {
        string stateDir = NewTempDir("wrphys");
        var service = NewWriteService(new AutoShopWriteRegisterOptions(), stateDir);

        var report = await service.WriteAsync(PhysicalTarget(), "D100", 1, allowPhysicalWrite: false);

        Assert.False(report.Success);
        Assert.Equal("Unsupported", report.Status);
        Assert.Contains("FORBIDDEN", report.Message, StringComparison.Ordinal);
        Assert.Contains("--autoshop-allow-physical-write", report.Message, StringComparison.Ordinal);
        // No transport was ever created (nothing reached the wire).
    }

    [Fact]
    public async Task WriteRegister_PhysicalTarget_SwitchOn_WritesThroughFakeServer()
    {
        // The physical gate only opens with --autoshop-allow-physical-write; the
        // transport here is an in-memory fake, so zero physical action happens.
        string stateDir = NewTempDir("wrphysok");
        var server = new FakeModbusServer();
        var service = NewWriteService(new AutoShopWriteRegisterOptions
        {
            TransportFactory = (_, _) => Task.FromResult<Stream>(server.Duplex())
        }, stateDir);

        var report = await service.WriteAsync(PhysicalTarget(), "D100", 1234, allowPhysicalWrite: true);

        Assert.True(report.Success, report.Message);
        Assert.True(report.PhysicalWriteAllowed);
        Assert.Equal((ushort)1234, Assert.IsType<ushort>(report.ReadBackValue));
    }

    [Fact]
    public async Task WriteRegister_NonLoopbackSimulationTarget_DefaultForbidden()
    {
        string stateDir = NewTempDir("wrsimnet");
        var service = NewWriteService(new AutoShopWriteRegisterOptions(), stateDir);
        var target = SimulatorTarget() with
        {
            Endpoint = new EndpointProfile("10.0.0.5", 502, TransportKind.ModbusTcp, Unit: 1)
        };

        var report = await service.WriteAsync(target, "D100", 1, allowPhysicalWrite: false);

        Assert.False(report.Success);
        Assert.Contains("non-loopback", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteRegister_DoubleWordDD_RefusedFc5Fc6Only()
    {
        string stateDir = NewTempDir("wrdd");
        var service = NewWriteService(new AutoShopWriteRegisterOptions(), stateDir);

        var report = await service.WriteAsync(SimulatorTarget(), "DD100", 1, allowPhysicalWrite: false);

        Assert.False(report.Success);
        Assert.Contains("FC16", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteRegister_UnknownAddress_FailsWithoutTransport()
    {
        string stateDir = NewTempDir("wrbad");
        var server = new FakeModbusServer();
        int transports = 0;
        var service = NewWriteService(new AutoShopWriteRegisterOptions
        {
            TransportFactory = (_, _) => { transports++; return Task.FromResult<Stream>(server.Duplex()); }
        }, stateDir);

        var report = await service.WriteAsync(SimulatorTarget(), "Z999", 1, allowPhysicalWrite: false);

        Assert.False(report.Success);
        Assert.Equal(0, transports); // the mapping error is local; nothing touched the wire
    }

    [Fact]
    public async Task WriteRegister_NonInovanceVendor_Refused()
    {
        string stateDir = NewTempDir("wrvendor");
        var service = NewWriteService(new AutoShopWriteRegisterOptions(), stateDir);
        var target = new TargetProfile("sim-generic", PlcVendor.Generic, "Generic", null, null,
            new("127.0.0.1", 502, TransportKind.ModbusTcp, Unit: 1), [TransportKind.ModbusTcp],
            null, new([]), "sim", IsSimulation: true);

        var report = await service.WriteAsync(target, "D100", 1, allowPhysicalWrite: false);

        Assert.False(report.Success);
        Assert.Contains("Inovance", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteRegister_ServiceLevelLoopbackPolicy_MatchesToolGate()
    {
        // The static classifier mirrors the service behaviour: loopback allowed by
        // default, everything else needs the switch.
        Assert.Null(AutoShopWriteRegisterService.ClassifyTarget(SimulatorTarget(), "127.0.0.1", allowPhysicalWrite: false));
        Assert.Null(AutoShopWriteRegisterService.ClassifyTarget(SimulatorTarget(), "localhost", allowPhysicalWrite: false));
        Assert.NotNull(AutoShopWriteRegisterService.ClassifyTarget(SimulatorTarget(), "10.0.0.5", allowPhysicalWrite: false));
        Assert.Null(AutoShopWriteRegisterService.ClassifyTarget(SimulatorTarget(), "10.0.0.5", allowPhysicalWrite: true));
        Assert.NotNull(AutoShopWriteRegisterService.ClassifyTarget(PhysicalTarget(), "192.168.1.10", allowPhysicalWrite: false));
        Assert.Null(AutoShopWriteRegisterService.ClassifyTarget(PhysicalTarget(), "192.168.1.10", allowPhysicalWrite: true));
    }

    // ---------------------------------------------------------------
    // Stop owned (shutdown hygiene)
    // ---------------------------------------------------------------

    [Fact]
    public async Task StopOwned_StopsSessionAndReportsEmptyWithoutSession()
    {
        string dir = NewTempDir("stopowned");
        BuildTestProjectSource(dir);
        var harness = new FakeInjectHarness();
        AutoShopInjectTools tools = NewTools(harness: harness, probe: pid => SuccessSnapshot(dir));

        var none = await tools.StopOwnedAsync();
        Assert.True(none.Success);
        Assert.Empty(harness.KilledPids);

        await tools.OpenProjectAsync(dir);
        var stop = await tools.StopOwnedAsync();
        Assert.True(stop.Success, stop.Details);
        Assert.Equal(new[] { 4242 }, harness.KilledPids);
        Assert.Null(tools.CurrentSession);
    }

    // ---------------------------------------------------------------
    // G3 download / run-stop: loopback closed loop over injected fakes
    // ---------------------------------------------------------------

    /// <summary>Scripted register-chain channel mirroring the G2 daemon replies (offline).</summary>
    private sealed class FakeSimulatorControlChannel : ISimulatorControlChannel
    {
        public List<byte[]> Sent { get; } = [];
        private readonly Func<byte[], byte[]> _reply;
        public FakeSimulatorControlChannel(Func<byte[], byte[]> reply) => _reply = reply;
        public Task<SimulatorControlReply> SendAsync(byte[] frame, CancellationToken cancellationToken = default)
        {
            Sent.Add(frame);
            return Task.FromResult(SimulatorControlFrames.ParseReply(_reply(frame)));
        }
    }

    /// <summary>Replies matching the 2026-10-29 daemon: load trigger -> [0F][00][82], control words -> [06].</summary>
    private static byte[] G2DaemonReply(byte[] frame)
    {
        string hex = Convert.ToHexString(frame);
        if (hex.Contains("02001000")) return new byte[] { 0x0F, 0x00, 0x82, 0x00 }; // load trigger stub 0x82
        return new byte[] { 0x06 }; // RUN/STOP ack
    }

    /// <summary>Writes a PPCH-magic payload file into the project dir; returns its bytes.</summary>
    private static byte[] BuildDownloadPayloadDir(string dir)
    {
        byte[] payload = new byte[64];
        "PPCH"u8.CopyTo(payload);
        payload[4] = 0x01; // any body — the plan only guards the magic
        File.WriteAllBytes(Path.Combine(dir, "project.hcpp"), payload);
        return payload;
    }

    [Fact]
    public async Task Download_Loopback_FakeClients_ReturnsStepEvidence()
    {
        string dir = NewTempDir("dlloop");
        byte[] payload = BuildDownloadPayloadDir(dir);
        var control = new FakeSimulatorControlChannel(G2DaemonReply);
        int stepCalls = 0;
        AutoShopInjectTools tools = NewTools(
            controlFactory: (_, _) => control,
            downloadStep: (file, host, port, _) =>
            {
                stepCalls++;
                return Task.FromResult((true, $"{host}:{port} login=Ok: {file.RemoteName}: {file.Bytes.Length} bytes transferred."));
            });

        var report = await tools.DownloadAsync(dir, null, null, includeStop: true, allowPhysicalDownload: false);

        Assert.True(report.Success, report.Message);
        Assert.Equal("Experimental", report.Status);
        Assert.Equal("127.0.0.1:12939", report.TargetEndpoint);
        Assert.False(report.PhysicalDownloadAllowed);
        Assert.True(report.Downloaded);
        Assert.Equal(@"project\project.hcpp", report.RemoteName);
        Assert.Equal(payload.Length, report.PayloadLength);
        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            Assert.Equal(Convert.ToHexString(md5.ComputeHash(payload)).ToLowerInvariant(), report.PayloadMd5);
        }

        // Per-step evidence (G2 §3.3/§3.4 shapes): transport acks, honest stub load verdict,
        // RUN ack with the register-alive wording.
        Assert.Equal(1, stepCalls);
        Assert.Equal(3, control.Sent.Count); // load trigger, RUN, STOP
        Assert.Equal("16000201000000000200100000FF0400000001000000", Convert.ToHexString(control.Sent[0]));
        Assert.Equal("16000201000000000200200000FF0400000001000000", Convert.ToHexString(control.Sent[1]));
        Assert.Equal("16000201000000000200200000FF0400000000000000", Convert.ToHexString(control.Sent[2]));
        Assert.NotNull(report.LoadVerdict);
        Assert.Contains("0x82", report.LoadVerdict, StringComparison.Ordinal);
        Assert.Contains("STUB", report.LoadVerdict, StringComparison.Ordinal);
        Assert.Contains("NOT activated", report.LoadVerdict, StringComparison.Ordinal);
        Assert.NotNull(report.RunVerdict);
        Assert.Contains("acked [06]", report.RunVerdict, StringComparison.Ordinal);
        Assert.Contains("4660", report.RunVerdict, StringComparison.Ordinal);
        Assert.NotNull(report.StopVerdict);
        Assert.Contains("RETAINS", report.StopVerdict, StringComparison.Ordinal);
        Assert.Contains(report.Steps, s => s.StartsWith("download", StringComparison.Ordinal));
        Assert.Contains(report.Steps, s => s.StartsWith("RUN", StringComparison.Ordinal));
        // The honest scope phrase and the on-disk log.
        Assert.Contains("Local simulator demo only", report.Message, StringComparison.Ordinal);
        Assert.Contains("download", File.ReadAllText(tools.LogFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_TransferFails_NeverSendsRun()
    {
        string dir = NewTempDir("dlfail");
        BuildDownloadPayloadDir(dir);
        var control = new FakeSimulatorControlChannel(G2DaemonReply);
        AutoShopInjectTools tools = NewTools(
            controlFactory: (_, _) => control,
            downloadStep: (_, _, _, _) => Task.FromResult((false, "session rejected by daemon (status 0x61)")));

        var report = await tools.DownloadAsync(dir, null, null, includeStop: false, allowPhysicalDownload: false);

        Assert.False(report.Success);
        Assert.Equal("Unsupported", report.Status);
        Assert.False(report.Downloaded);
        Assert.Empty(control.Sent); // a failed transfer never sends control frames
        Assert.Null(report.RunVerdict);
        Assert.Contains("failed transfer never sends RUN", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_NoPayloadFile_FailsWithoutTransport()
    {
        string dir = NewTempDir("dlmissing");
        int controlCalls = 0;
        AutoShopInjectTools tools = NewTools(controlFactory: (_, _) => { controlCalls++; return new FakeSimulatorControlChannel(G2DaemonReply); });

        var report = await tools.DownloadAsync(dir, null, null, includeStop: false, allowPhysicalDownload: false);

        Assert.False(report.Success);
        Assert.Contains("no .hcpp payload", report.Message, StringComparison.Ordinal);
        Assert.Equal(0, controlCalls);
    }

    [Fact]
    public async Task Download_NonContainerPayload_RejectedByPpchGuard()
    {
        string dir = NewTempDir("dlnoppch");
        File.WriteAllBytes(Path.Combine(dir, "project.hcpp"), new byte[] { 0x53, 0x41, 0x4A, 0x03, 1, 2, 3, 4 });
        int controlCalls = 0;
        AutoShopInjectTools tools = NewTools(controlFactory: (_, _) => { controlCalls++; return new FakeSimulatorControlChannel(G2DaemonReply); });

        var report = await tools.DownloadAsync(dir, null, null, includeStop: false, allowPhysicalDownload: false);

        Assert.False(report.Success);
        Assert.Contains("PPCH container", report.Message, StringComparison.Ordinal);
        Assert.Equal(0, controlCalls); // the payload guard is local; nothing touched the wire
    }

    [Fact]
    public async Task Download_PhysicalHost_DefaultRefused_NoTransportTouched()
    {
        string dir = NewTempDir("dlphys");
        BuildDownloadPayloadDir(dir);
        int controlCalls = 0, stepCalls = 0;
        AutoShopInjectTools tools = NewTools(
            controlFactory: (_, _) => { controlCalls++; return new FakeSimulatorControlChannel(G2DaemonReply); },
            downloadStep: (_, _, _, _) => { stepCalls++; return Task.FromResult((true, "x")); });

        var report = await tools.DownloadAsync(dir, "10.0.0.9", 12939, includeStop: false, allowPhysicalDownload: false);

        Assert.False(report.Success);
        Assert.Equal("Unsupported", report.Status);
        Assert.Contains("FORBIDDEN", report.Message, StringComparison.Ordinal);
        Assert.Contains("--autoshop-allow-physical-download", report.Message, StringComparison.Ordinal);
        Assert.Equal(0, controlCalls);
        Assert.Equal(0, stepCalls);
    }

    [Fact]
    public async Task Download_PhysicalHost_SwitchOn_StillRefusedByDeepLoopbackLine()
    {
        // The operator switch only REACHES the protocol clients' deep red line; it never
        // bypasses it. SimulatorRegisterClient.Create resolves "10.0.0.9" (no socket, no
        // traffic) and throws — the tool must surface that refusal verbatim and mark the
        // physical path Experimental(真机待验).
        string dir = NewTempDir("dlphysok");
        BuildDownloadPayloadDir(dir);
        int controlCalls = 0;
        AutoShopInjectTools tools = NewTools(
            controlFactory: (_, _) => { controlCalls++; return new FakeSimulatorControlChannel(G2DaemonReply); });

        var report = await tools.DownloadAsync(dir, "10.0.0.9", 12939, includeStop: false, allowPhysicalDownload: true);

        Assert.False(report.Success);
        Assert.True(report.PhysicalDownloadAllowed);
        Assert.Contains("deep loopback red line", report.Message, StringComparison.Ordinal);
        Assert.Contains("Experimental", report.Message, StringComparison.Ordinal);
        Assert.Equal(0, controlCalls); // the injected factory is loopback-only; production refusal surfaced
    }

    [Fact]
    public async Task Download_DaemonNotListening_ReportsHonestTransportFailure()
    {
        string dir = NewTempDir("dlnosim");
        BuildDownloadPayloadDir(dir);
        // No seams: the production wiring tries a real loopback socket to port 1 (closed
        // by construction on any machine — a refused connect, never a fake success).
        AutoShopInjectTools tools = NewTools(nativePort: 1);

        var report = await tools.DownloadAsync(dir, null, null, includeStop: false, allowPhysicalDownload: false);

        Assert.False(report.Success);
        Assert.Equal("Unsupported", report.Status);
        Assert.Contains("failed with", report.Message, StringComparison.Ordinal);
        Assert.Contains("plc_autoshop_simulator", report.Message, StringComparison.Ordinal);
        Assert.False(report.Downloaded);
    }

    [Fact]
    public async Task RunStop_Run_RegistersAliveEvidence()
    {
        string dir = NewTempDir("rsrun");
        _ = dir;
        var modbus = new FakeModbusServer();
        var control = new FakeSimulatorControlChannel(frame =>
        {
            // RUN (control word 1) makes the factory-default app live: HR100 0 -> 4660,
            // mirroring the G2 observation (docs/inovance-autoshop-download-closedloop.md §3.4).
            string hex = Convert.ToHexString(frame);
            if (hex.Contains("200000FF") && hex.EndsWith("01000000"))
            {
                byte[] fc6 = [0, 9, 0, 0, 0, 6, 1, 6, 0, 100, 0x12, 0x34];
                modbus.Handle(fc6);
            }
            return new byte[] { 0x06 };
        });
        AutoShopInjectTools tools = NewTools(
            controlFactory: (_, _) => control,
            modbusTransport: (_, _) => Task.FromResult<Stream>(modbus.Duplex()));

        var report = await tools.RunStopAsync("run", null, null, readRegisterEvidence: true, allowPhysicalDownload: false);

        Assert.True(report.Success, report.Message);
        Assert.Equal("Experimental", report.Status);
        Assert.Equal("run", report.Action);
        Assert.Equal("127.0.0.1:12939", report.TargetEndpoint);
        Assert.Equal("16000201000000000200200000FF0400000001000000", report.FrameHex);
        Assert.Contains("acked [06]", report.ReplyVerdict, StringComparison.Ordinal);
        Assert.Equal((ushort)0, report.RegisterBefore);
        Assert.Equal((ushort)4660, report.RegisterAfter);
        Assert.NotNull(report.RegisterEvidenceNote);
        Assert.Contains("went LIVE", report.RegisterEvidenceNote, StringComparison.Ordinal);
        Assert.Contains("D100", report.RegisterEvidenceNote, StringComparison.Ordinal);
        Assert.Equal(1, control.Sent.Count);
    }

    [Fact]
    public async Task RunStop_Stop_RetainsValuesEvidence()
    {
        var modbus = new FakeModbusServer();
        byte[] seed = [0, 9, 0, 0, 0, 6, 1, 6, 0, 100, 0x12, 0x34]; // HR100 = 4660
        modbus.Handle(seed);
        var control = new FakeSimulatorControlChannel(_ => new byte[] { 0x06 });
        AutoShopInjectTools tools = NewTools(
            controlFactory: (_, _) => control,
            modbusTransport: (_, _) => Task.FromResult<Stream>(modbus.Duplex()));

        var report = await tools.RunStopAsync("stop", null, null, readRegisterEvidence: true, allowPhysicalDownload: false);

        Assert.True(report.Success, report.Message);
        Assert.Equal("16000201000000000200200000FF0400000000000000", report.FrameHex);
        Assert.Equal((ushort)4660, report.RegisterBefore);
        Assert.Equal((ushort)4660, report.RegisterAfter); // STOP does not clear values (honest)
        Assert.Contains("RETAINS", report.RegisterEvidenceNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunStop_InvalidAction_RejectsWithoutFrame()
    {
        int controlCalls = 0;
        AutoShopInjectTools tools = NewTools(controlFactory: (_, _) => { controlCalls++; return new FakeSimulatorControlChannel(_ => new byte[] { 6 }); });

        var report = await tools.RunStopAsync("reset", null, null, readRegisterEvidence: false, allowPhysicalDownload: false);

        Assert.False(report.Success);
        Assert.Equal("Unsupported", report.Status);
        Assert.Contains("must be 'run' or 'stop'", report.Message, StringComparison.Ordinal);
        Assert.Equal(0, controlCalls);
    }

    [Fact]
    public async Task RunStop_PhysicalHost_DefaultRefused_AndSwitchOnMeetsDeepRefusal()
    {
        int controlCalls = 0;
        AutoShopInjectTools tools = NewTools(controlFactory: (_, _) => { controlCalls++; return new FakeSimulatorControlChannel(_ => new byte[] { 6 }); });

        var off = await tools.RunStopAsync("run", "192.168.1.10", 12939, readRegisterEvidence: false, allowPhysicalDownload: false);
        Assert.False(off.Success);
        Assert.Contains("PHYSICAL RUN/STOP IS FORBIDDEN BY DEFAULT", off.Message, StringComparison.Ordinal);
        Assert.Contains("--autoshop-allow-physical-download", off.Message, StringComparison.Ordinal);

        var on = await tools.RunStopAsync("run", "192.168.1.10", 12939, readRegisterEvidence: false, allowPhysicalDownload: true);
        Assert.False(on.Success);
        Assert.True(on.PhysicalDownloadAllowed);
        Assert.Contains("deep loopback red line", on.Message, StringComparison.Ordinal);
        Assert.Contains("Hardware CPU RUN/STOP remains Unsupported", on.Message, StringComparison.Ordinal);

        Assert.Equal(0, controlCalls); // nothing ever reaches a channel for a non-loopback host
    }

    [Fact]
    public async Task RunStop_EvidenceReadFails_VerdictStandsOnAck()
    {
        var control = new FakeSimulatorControlChannel(_ => new byte[] { 0x06 });
        AutoShopInjectTools tools = NewTools(
            controlFactory: (_, _) => control,
            modbusTransport: (_, _) => Task.FromException<Stream>(new System.Net.Sockets.SocketException(10061))); // connection refused

        var report = await tools.RunStopAsync("run", null, null, readRegisterEvidence: true, allowPhysicalDownload: false);

        Assert.True(report.Success, report.Message); // the ack is the verdict; evidence is annotation
        Assert.Equal("acked [06]", report.ReplyVerdict);
        Assert.Null(report.RegisterAfter);
        Assert.Contains("register evidence read failed", report.RegisterEvidenceNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RouterDownloadAndRunStop_SchemaAndPayloadThroughFakes()
    {
        string root = NewTempDir("rg3");
        string dir = NewTempDir("rg3proj");
        BuildDownloadPayloadDir(dir);
        var modbus = new FakeModbusServer();
        var control = new FakeSimulatorControlChannel(G2DaemonReply);
        var services = new ServerGovernanceServices(root, null, new AutoShopBridgeConfig(),
            autoShopInjectOptions: new AutoShopInjectToolsOptions
            {
                AutoShopExecutablePath = FakeAutoShopExe,
                InjectorExecutablePath = FakeInjector,
                BridgeDllPath = FakeBridgeDll,
                SimulatorControlChannelFactory = (_, _) => control,
                DownloadStepOverride = (file, host, port, _) =>
                    Task.FromResult((true, $"{host}:{port}: {file.RemoteName} transferred.")),
                SimulatorModbusTransportFactory = (_, _) => Task.FromResult<Stream>(modbus.Duplex())
            });
        var router = new McpToolRouter(DefaultPlcComposition.Create(), services);

        var dl = await CallToolJson(router, "plc_autoshop_download",
            "{\"projectDir\":" + JsonSerializer.Serialize(dir) + ",\"includeStop\":false}");
        Assert.True(dl.GetProperty("downloaded").GetBoolean());
        Assert.Equal("Experimental", dl.GetProperty("status").GetString());
        Assert.Contains("0x82", dl.GetProperty("loadVerdict").GetString(), StringComparison.Ordinal);

        var rs = await CallToolJson(router, "plc_autoshop_run_stop", """{"action":"run"}""");
        Assert.True(rs.GetProperty("success").GetBoolean());
        Assert.Equal("16000201000000000200200000FF0400000001000000", rs.GetProperty("frameHex").GetString());

        string err = await CallToolError(router, "plc_autoshop_download", "{}");
        Assert.Contains("projectDir", err, StringComparison.Ordinal);
        // An invalid action is an honest FAILED REPORT (success=false), never a thrown
        // error and never a frame on the wire.
        var badAction = await CallToolJson(router, "plc_autoshop_run_stop", """{"action":"dance"}""");
        Assert.False(badAction.GetProperty("success").GetBoolean());
        Assert.Contains("must be 'run' or 'stop'", badAction.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // Real end-to-end (env-gated; never runs offline / in CI)
    // ---------------------------------------------------------------

    [InjectLocalFact]
    [Trait("LocalSimulator", "1")]
    public async Task RealChain_GenerateOpenCompileMonitor_OnLocalAutoShop()
    {
        // Requires: a local AutoShop installation, the native artifacts
        // (tools\AutoShopInjectBridge\build.cmd), the H5U simulator daemon and
        // PLCMCP_LOCAL_SIMULATOR=1. Zero physical PLC action: the instance is
        // self-started and cleaned up, the monitor targets 127.0.0.1 only.
        var discovery = new InjectLauncher().Discover();
        if (!discovery.Ready)
        {
            return; // native artifacts not built on this machine: nothing to prove here
        }

        string stateDir = Path.Combine(Path.GetTempPath(), "plcmcp-e4-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDir);
        var tools = new AutoShopInjectTools(stateDir);
        try
        {
            // Use a vendor template copy as the source when one exists (E3 smoke
            // evidence is template-based; from-scratch placeholder content may
            // hit the compile 模块 gate honestly).
            string? template = Directory.GetFiles(
                    @"D:\Inovance Control\AutoShop\Template_Project", "*.hcpp", SearchOption.AllDirectories)
                .FirstOrDefault();
            string projectDir = Path.Combine(stateDir, "proj");
            var generate = template is null
                ? await tools.GenerateProjectAsync(projectDir, "H3U", null, null, null, null, null)
                : await tools.GenerateProjectAsync(projectDir, null, null, null, null, template, null);
            Assert.True(generate.Success, generate.RoundTripError ?? generate.Message);

            var open = await tools.OpenProjectAsync(projectDir);
            Assert.True(open.Success, open.Message);

            var compile = await tools.CompileAsync();
            // The F1-verified chain is invoked in-process: the honest verdict is
            // artifact-based. A project-gate or vendor-side refusal surfaces as
            // a verbatim message, never a fake success.
            Assert.True(compile.BridgeAnswered, compile.BridgeMessage);
            Assert.True(compile.Status == "Experimental",
                $"compile status {compile.Status}: {compile.Message} | bridge: {compile.BridgeMessage}");
            if (compile.Success)
            {
                Assert.Contains(compile.Artifacts, a => a.IsNew && a.Name.EndsWith(".compile", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                Assert.True(compile.PollTimedOut || compile.BridgeMessage.Length > 0,
                    "a failed compile must record its failure point");
            }

            // Monitor loopback: start the local simulator daemon when none runs
            // (owned start), write the Modbus baseline on 127.0.0.1:502 (loopback
            // only, red-line compliant) and read the same addresses through the
            // in-process bridge read.
            var simulator = new AutoShopSimulatorServices(Path.Combine(stateDir, "simulator"));
            var simStatus = await simulator.GetStatusAsync();
            bool simStartedHere = false;
            if (!simStatus.Running && simStatus.DaemonFilePresent)
            {
                var started = await simulator.StartAsync();
                simStartedHere = started.Started;
                simStatus = await simulator.GetStatusAsync();
            }
            try
            {
                if (simStatus.ModbusPortListening)
                {
                    ushort d100 = ModbusWriteWord(502, 100, 0x1234);
                    Assert.Equal(0x1234, d100);
                    var monitor = await tools.MonitorReadAsync("D100,M0");
                    Assert.True(monitor.Success, monitor.Message);
                    Assert.True(monitor.ElementReceiptSucceeded, monitor.ElementReceipt);
                    // The in-process read must carry values (the tag+0x20 slot); a
                    // value=0 with a successful read is the honest closed-loop form
                    // when the register is 0 — here the baseline wrote 0x1234.
                    Assert.Contains("values=", monitor.ElementReceipt, StringComparison.Ordinal);
                }
            }
            finally
            {
                if (simStartedHere)
                {
                    await simulator.StopAsync(null);
                }
            }
        }
        finally
        {
            await tools.StopOwnedAsync();
            try
            {
                foreach (string file in Directory.EnumerateFiles(stateDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(stateDir, recursive: true);
            }
            catch { }
        }
    }

    /// <summary>Minimal loopback-only Modbus FC6 write + FC3 readback (smoke evidence).</summary>
    private static ushort ModbusWriteWord(int port, ushort address, ushort value)
    {
        using var client = new System.Net.Sockets.TcpClient { NoDelay = true };
        var connect = client.ConnectAsync("127.0.0.1", port);
        if (!connect.Wait(3000) || !client.Connected)
        {
            throw new System.IO.InvalidDataException("Modbus loopback connect failed/timed out.");
        }
        client.ReceiveTimeout = 3000;
        client.SendTimeout = 3000;
        var stream = client.GetStream();
        byte[] write = [0, 1, 0, 0, 0, 6, 1, 6, (byte)(address >> 8), (byte)address, (byte)(value >> 8), (byte)value];
        stream.Write(write);
        var header = new byte[7];
        ReadExactly(stream, header);
        // FC6 echo: MBAP length field = 6 = unit(1) + fc(1) + address(2) + value(2);
        // after the 7-byte header 5 bytes remain.
        var rest = new byte[5];
        ReadExactly(stream, rest);
        byte[] read = [0, 2, 0, 0, 0, 6, 1, 3, (byte)(address >> 8), (byte)address, 0, 1];
        stream.Write(read);
        ReadExactly(stream, header);
        // FC3 single register: MBAP length = 5 = unit(1) + fc(1) + byteCount(1) + data(2);
        // after the header 4 bytes remain (fc, byteCount, data).
        var payload = new byte[4];
        ReadExactly(stream, payload);
        return (ushort)((payload[2] << 8) | payload[3]);
    }

    private static void ReadExactly(System.Net.Sockets.NetworkStream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
            {
                throw new System.IO.InvalidDataException("Modbus loopback stream ended early.");
            }
            read += n;
        }
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    /// <summary>Builds a minimal project source: only a .hcpp container (no .hcp, no files).</summary>
    private static byte[] BuildTestProjectSource(string dir)
    {
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n<project>\r\n\t<Version>1</Version>\r\n" +
            "\t<GCMModal>H3U</GCMModal>\r\n\t<HCPFileVer>1</HCPFileVer>\r\n" +
            "\t<file id=\"1\">\r\n\t\t<FileType>3</FileType>\r\n\t\t<FileName>VarList.gdt</FileName>\r\n\t</file>\r\n</project>";
        byte[] hcpBytes = AutoShopProjectCrypt.EncryptBlob(
            Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray());
        byte[] hcpp = HcppWriter.Write(
            new HcppMainRecordSpec("TestProj.hcp", hcpBytes),
            [new HcppFileSpec("VarList.gdt", [])]);
        File.WriteAllBytes(Path.Combine(dir, "TestProj.hcpp"), hcpp);
        return hcpBytes;
    }

    private static AutoShopWindowSnapshot SuccessSnapshot(string projectDir) => new(
        [$"AutoShop V4.12.0.2 {projectDir} - [MAIN]"], [], []);

    private AutoShopInjectTools NewTools(
        FakeInjectHarness? harness = null,
        string? pipeName = null,
        Func<int, AutoShopWindowSnapshot?>? probe = null,
        TimeSpan? openTimeout = null,
        TimeSpan? openPoll = null,
        TimeSpan? unsupportedPoll = null,
        TimeSpan? compilePoll = null,
        TimeSpan? pollInterval = null,
        Func<string, int, ISimulatorControlChannel>? controlFactory = null,
        Func<DownloadPayloadFile, string, int, CancellationToken, Task<(bool Ok, string Detail)>>? downloadStep = null,
        Func<string, int, Task<Stream>>? modbusTransport = null,
        int? nativePort = null,
        int? modbusPort = null)
    {
        var h = harness ?? new FakeInjectHarness();
        string stateRoot = NewTempDir("state");
        var options = new AutoShopInjectToolsOptions
        {
            SimulatorControlChannelFactory = controlFactory,
            DownloadStepOverride = downloadStep,
            SimulatorModbusTransportFactory = modbusTransport,
            TargetPort = nativePort ?? 12939,
            SimulatorModbusPort = modbusPort ?? 502,
            AutoShopExecutablePath = FakeAutoShopExe,
            InjectorExecutablePath = FakeInjector,
            BridgeDllPath = FakeBridgeDll,
            OwnedPidJournalPath = Path.Combine(stateRoot, "owned-instances.jsonl"),
            PipeName = pipeName ?? InjectBridgeProtocol.PipeName,
            InjectorTimeout = TimeSpan.FromSeconds(3),
            OpenTimeout = openTimeout ?? TimeSpan.FromSeconds(3),
            OpenPollInterval = openPoll ?? TimeSpan.FromMilliseconds(50),
            CompilePollTimeout = compilePoll ?? TimeSpan.FromSeconds(1),
            BridgeUnsupportedPollTimeout = unsupportedPoll ?? TimeSpan.FromMilliseconds(600),
            CompilePollInterval = pollInterval ?? TimeSpan.FromMilliseconds(50),
            WindowProbe = probe,
            FileExists = _ => true,
            LauncherFactory = o => new InjectLauncher(new InjectLauncherOptions
            {
                AutoShopExecutablePath = o.AutoShopExecutablePath,
                InjectorExecutablePath = o.InjectorExecutablePath,
                BridgeDllPath = o.BridgeDllPath,
                OwnedPidJournalPath = o.OwnedPidJournalPath,
                PipeName = o.PipeName,
                AutoShopArguments = o.AutoShopArguments,
                InjectorTimeout = o.InjectorTimeout,
                StopTimeout = o.StopTimeout,
                Gate = o.Gate,
                FileExists = o.FileExists,
                InjectorRunner = h.Runner,
                LiveProcessLookup = h.Lookup,
                ProcessTreeKiller = h.Killer,
                HandshakeProbeFactory = _ => h.Probe
            })
        };
        return new AutoShopInjectTools(stateRoot, options);
    }

    private string NewTempDir(string purpose)
    {
        string dir = Path.Combine(Path.GetTempPath(), "plcmcp_inject_tools_" + purpose + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void Cleanup(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(root, recursive: true);
    }

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

    private static string BuildResult(ReceivedRequest request, string resultJson) =>
        $"{{\"id\":\"{request.Id}\",\"result\":{resultJson}}}";

    private static string BuildError(ReceivedRequest request, int code, string message) =>
        $"{{\"id\":\"{request.Id}\",\"error\":{{\"code\":{code},\"message\":\"{message}\"}}}}";

    /// <summary>Drops a fake .compile artifact mid-request, then answers Unsupported.</summary>
    private static string DropAndFail(ReceivedRequest request, string path, byte[] payload)
    {
        File.WriteAllBytes(path, AutoShopProjectCrypt.EncryptBlob(payload));
        return BuildError(request, InjectBridgeProtocol.ErrUnsupported, "Unsupported: no compile entry point");
    }

    /// <summary>Drops a fake .compile artifact mid-request, then answers with the F1 submit receipt.</summary>
    private static string DropAndAccept(ReceivedRequest request, string path, byte[] payload)
    {
        File.WriteAllBytes(path, AutoShopProjectCrypt.EncryptBlob(payload));
        return BuildResult(request, "{\"compile\":\"submitted\",\"compileEntryRva\":\"0x15A2EC0\",\"pid\":4242}");
    }

    private MockInjectBridgeServer StartMock(Func<ReceivedRequest, string?> responder)
    {
        var mock = new MockInjectBridgeServer("PlcMcpTestInjectTools_" + Guid.NewGuid().ToString("N")[..12], responder);
        _servers.Add(mock);
        return mock;
    }

    // ---------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------

    /// <summary>Fake injector/window/process harness; never touches the real process table.</summary>
    private sealed class FakeInjectHarness
    {
        public List<InjectInjectorCommand> Commands { get; } = [];
        public List<int> KilledPids { get; } = [];
        public int NextPid { get; set; } = 4242;
        public bool HandshakeAnswered { get; set; } = true;
        public int InjectorExitCode { get; set; }
        public string InjectorStdout { get; set; } = string.Empty;

        public InjectInjectorRunResult Runner(InjectInjectorCommand command)
        {
            Commands.Add(command);
            if (InjectorExitCode != 0 || InjectorStdout.Length > 0)
            {
                return new InjectInjectorRunResult(InjectorExitCode, InjectorStdout, string.Empty);
            }
            string stdout =
                $"{{\"event\":\"launched\",\"pid\":{NextPid},\"pipeReady\":true," +
                $"\"startedUtc\":\"{DateTime.UtcNow.ToString("o")}\"}}";
            return new InjectInjectorRunResult(0, stdout, string.Empty);
        }

        public IInjectBridgeHandshakeProbe Probe => new FakeProbe(HandshakeAnswered);

        public Func<int, InjectLiveProcessIdentity?> Lookup => pid =>
            new InjectLiveProcessIdentity(pid, "AutoShop", @"C:\fake\AutoShop\AutoShop.exe", DateTime.UtcNow);

        public bool Killer(int pid)
        {
            KilledPids.Add(pid);
            return true;
        }
    }

    private sealed class FakeProbe : IInjectBridgeHandshakeProbe
    {
        private readonly bool _answered;
        public FakeProbe(bool answered) => _answered = answered;

        public Task<InjectBridgeHandshakeReport> HandshakeAsync(string clientName = "PlcMcp", CancellationToken cancellationToken = default)
        {
            InjectBridgeHandshakeReport report = _answered
                ? new InjectBridgeHandshakeReport(true, true, InjectBridgeProtocol.WorkerName, "1.0.0",
                    InjectBridgeProtocol.ProtocolVersion, "x86", "injected", 4242,
                    new[] { "comm_stack_read_only_loopback_first" }, "ok", InjectBridgeProtocol.Disclaimer)
                : new InjectBridgeHandshakeReport(false, false, null, null, null, null, null, null,
                    Array.Empty<string>(), "not answered", InjectBridgeProtocol.Disclaimer);
            return Task.FromResult(report);
        }
    }

    private sealed class ReceivedRequest
    {
        public string Id { get; set; } = "1";
        public string Method { get; set; } = string.Empty;
        public string? SetTargetIp { get; set; }
    }

    /// <summary>Loopback named-pipe mock; accepts sequential connections, one client at a time.</summary>
    private sealed class MockInjectBridgeServer : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly Func<ReceivedRequest, string?> _responder;
        private readonly CancellationTokenSource _cts = new();

        public string PipeName { get; }
        public List<ReceivedRequest> ReceivedRequests { get; } = [];
        public string? LastMethod { get; private set; }
        public string? LastSetTargetIp { get; private set; }

        public MockInjectBridgeServer(string pipeName, Func<ReceivedRequest, string?> responder)
        {
            PipeName = pipeName;
            _responder = responder;
            _pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _ = Task.Run(ServeLoopAsync);
        }

        private async Task ServeLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await _pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    var buffer = new byte[4096];
                    var pending = new StringBuilder();
                    while (_pipe.IsConnected && !_cts.IsCancellationRequested)
                    {
                        int read = await _pipe.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }
                        pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
                        int newline;
                        while ((newline = pending.ToString().IndexOf('\n')) >= 0)
                        {
                            string line = pending.ToString(0, newline).Trim('\r');
                            pending.Remove(0, newline + 1);
                            if (line.Length == 0)
                            {
                                continue;
                            }
                            var request = new ReceivedRequest();
                            try
                            {
                                using var doc = JsonDocument.Parse(line);
                                request.Id = doc.RootElement.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? "1" : "1";
                                request.Method = doc.RootElement.GetProperty("method").GetString() ?? string.Empty;
                                if (doc.RootElement.TryGetProperty("params", out JsonElement p) &&
                                    p.ValueKind == JsonValueKind.Object &&
                                    p.TryGetProperty("ip", out JsonElement ip) &&
                                    ip.ValueKind == JsonValueKind.String)
                                {
                                    request.SetTargetIp = ip.GetString();
                                    LastSetTargetIp = request.SetTargetIp;
                                }
                            }
                            catch (JsonException)
                            {
                                // ignore malformed requests in the mock
                            }
                            ReceivedRequests.Add(request);
                            LastMethod = request.Method;
                            string? response = _responder(request);
                            if (response == null)
                            {
                                continue;
                            }
                            byte[] payload = Encoding.UTF8.GetBytes(response + "\n");
                            await _pipe.WriteAsync(payload, _cts.Token).ConfigureAwait(false);
                            await _pipe.FlushAsync(_cts.Token).ConfigureAwait(false);
                        }
                    }
                    _pipe.Disconnect();
                }
            }
            catch
            {
                // mock server teardown
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _pipe.Dispose(); } catch { }
        }
    }

    /// <summary>Real end-to-end probe: requires the local AutoShop install; skipped offline.</summary>
    private sealed class InjectLocalFactAttribute : FactAttribute
    {
        public InjectLocalFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("PLCMCP_LOCAL_SIMULATOR"), "1", StringComparison.Ordinal))
            {
                Skip = "Requires the local AutoShop installation and the built inject-bridge artifacts; set PLCMCP_LOCAL_SIMULATOR=1 to enable.";
            }
        }
    }
}
