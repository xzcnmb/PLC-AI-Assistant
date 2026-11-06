using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlcMcp.Adapters;
using PlcMcp.Engineering.Workers.Keyence;
using PlcMcp.Server.Governance;
using PlcMcp.Server.Mcp;

namespace PlcMcp.Tests;

// Offline verification of the KV STUDIO "generate + deploy" pipeline
// (KeyenceDeployProgram). The deployment script itself is owned separately and is NOT
// present in these tests: gates 2-5 are exercised through an injected fake runner that
// reproduces the frozen contract (kv-deploy-result.json + KV-DEPLOY-RESULT stdout line +
// exit codes). The real powershell.exe invocation is covered by the two subprocess tests
// below with a temporary contract-conforming fake script, so no KV STUDIO is required.
public sealed class KeyenceDeployTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    private string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kvdeploy-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static KeyenceDeployRequest MakeRequest(
        string projectDir,
        string outDir,
        string scriptPath,
        string? kvsExe = null,
        string moduleName = "MotorCtl",
        string source = "sw := TRUE;\r\nresult := sw AND flag;",
        int deviceCode = 60,
        IReadOnlyList<KeyenceDeployVariable>? variables = null,
        bool dryRun = false)
        => new()
        {
            Source = source,
            ModuleName = moduleName,
            DeviceCode = deviceCode,
            Category = "scan",
            ProjectDir = projectDir,
            OutDir = outDir,
            ScriptPath = scriptPath,
            KvsExe = kvsExe,
            Variables = variables,
            DryRun = dryRun
        };

    // Fake Kvs.exe lives in its own directory: putting it inside the project directory would
    // trip the real red line "project must not be inside the KV STUDIO installation directory".
    private string CreateFakeKvs()
    {
        string installDir = CreateTempDir();
        string kvs = Path.Combine(installDir, "Kvs.exe");
        File.WriteAllBytes(kvs, [0x4D, 0x5A]);
        return kvs;
    }

    private sealed class FakePlan
    {
        public int ExitCode;
        public string Stdout = "KV-DEPLOY-RESULT 0 import=PASS variables=PASS compile=PASS persist=PASS";
        public string? ResultJson;
        public bool TimedOut;
        public Action? BeforeReturn;
    }

    private sealed class FakeKvDeployRunner : IKvDeployRunner
    {
        private readonly Func<FakePlan> _plan;

        public int Calls;
        public string? LastExecutable;
        public IReadOnlyList<string>? LastArguments;
        public TimeSpan? LastTimeout;
        public string? LastResultJsonPath;

        public FakeKvDeployRunner(Func<FakePlan>? plan = null)
        {
            _plan = plan ?? DefaultPlan;
        }

        private static FakePlan DefaultPlan() => new();

        public Task<KvDeployRunResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            string resultJsonPath,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastExecutable = executable;
            LastArguments = arguments;
            LastTimeout = timeout;
            LastResultJsonPath = resultJsonPath;

            var plan = _plan();
            string? resultPath = null;
            if (plan.ResultJson is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resultJsonPath)!);
                File.WriteAllText(resultJsonPath, plan.ResultJson, new UTF8Encoding(false));
                resultPath = resultJsonPath;
            }

            plan.BeforeReturn?.Invoke();

            return Task.FromResult(new KvDeployRunResult(
                plan.TimedOut ? null : plan.ExitCode,
                plan.Stdout,
                resultPath,
                plan.TimedOut));
        }
    }

    private static string BuildResultJson(
        string import = "PASS",
        string variables = "PASS",
        string compile = "PASS",
        string persist = "PASS",
        string state = "OK",
        int errorCount = 0,
        int warningCount = 0,
        string rawHeader = "转换结果 OK",
        string errors = "[]",
        string rawText = "",
        bool moduleFound = true,
        bool changed = true,
        string? scriptVersion = "\"1.0.0\"",
        bool dryRun = false,
        string messages = "[\"ps-fake ran\"]",
        bool omitGates = false,
        bool omitCompile = false)
    {
        var sb = new StringBuilder("{");
        if (scriptVersion is not null)
        {
            sb.Append("\"scriptVersion\":").Append(scriptVersion).Append(',');
        }

        sb.Append("\"projectDir\":\"C:\\\\proj\\\\qa\",\"moduleName\":\"MotorCtl\",\"dryRun\":")
          .Append(dryRun ? "true" : "false").Append(',');

        if (!omitGates)
        {
            sb.Append("\"gates\":{\"import\":\"").Append(import)
              .Append("\",\"variables\":\"").Append(variables)
              .Append("\",\"compile\":\"").Append(compile)
              .Append("\",\"persist\":\"").Append(persist).Append("\"},");
        }

        if (!omitCompile)
        {
            sb.Append("\"compile\":{\"state\":\"").Append(state)
              .Append("\",\"errorCount\":").Append(errorCount)
              .Append(",\"warningCount\":").Append(warningCount)
              .Append(",\"rawHeader\":\"").Append(rawHeader)
              .Append("\",\"errors\":").Append(errors)
              .Append(",\"rawText\":\"").Append(rawText).Append("\"},");
        }

        sb.Append("\"importDetail\":{\"moduleFoundInTree\":").Append(moduleFound ? "true" : "false")
          .Append(",\"dialogs\":[\"覆盖|插入?\"]},")
          .Append("\"persist\":{\"fingerprintBefore\":\"beforehash\",\"fingerprintAfter\":\"afterhash\",\"changed\":")
          .Append(changed ? "true" : "false").Append("},")
          .Append("\"messages\":").Append(messages).Append('}');
        return sb.ToString();
    }

    // ---------------- Request validation ----------------

    [Fact]
    public void Validate_RejectsRelativeProjectDir()
    {
        var request = MakeRequest("relative\\projects", CreateTempDir(), @"C:\s.ps1");
        Assert.Throws<ArgumentException>(() => request.Validate());
    }

    [Fact]
    public void Validate_RejectsProjectDirInsideInstallDirectory()
    {
        var request = new KeyenceDeployRequest
        {
            Source = "a := TRUE;",
            ModuleName = "Ok1",
            DeviceCode = 60,
            ProjectDir = @"C:\Program Files (x86)\KEYENCE\KVS12G\KVS12\KVS\MyPrj",
            OutDir = CreateTempDir(),
            ScriptPath = @"C:\s.ps1"
        };
        Assert.Throws<InvalidOperationException>(() => request.Validate());
    }

    [Fact]
    public void Validate_RejectsDeviceCodeOutOfRangeAndBadCategory()
    {
        var projectDir = CreateTempDir();
        Assert.Throws<ArgumentException>(() => MakeRequest(projectDir, CreateTempDir(), @"C:\s.ps1", deviceCode: 256).Validate());
        Assert.Throws<ArgumentException>(() => MakeRequest(projectDir, CreateTempDir(), @"C:\s.ps1", deviceCode: -1).Validate());
        var badCategory = MakeRequest(projectDir, CreateTempDir(), @"C:\s.ps1") with { Category = "ladder" };
        Assert.Throws<ArgumentException>(() => badCategory.Validate());
    }

    [Fact]
    public void Validate_RejectsTsvInjectionInVariableNames()
    {
        var request = MakeRequest(
            CreateTempDir(), CreateTempDir(), @"C:\s.ps1",
            variables: [new KeyenceDeployVariable("bad\tname", "BOOL")]);
        Assert.Throws<ArgumentException>(() => request.Validate());

        var emptyType = MakeRequest(
            CreateTempDir(), CreateTempDir(), @"C:\s.ps1",
            variables: [new KeyenceDeployVariable("ok", " ")]);
        Assert.Throws<ArgumentException>(() => emptyType.Validate());
    }

    [Fact]
    public void Validate_RejectsWaitOutOfRangeAndRelativeOutDir()
    {
        var projectDir = CreateTempDir();
        var wait = MakeRequest(projectDir, CreateTempDir(), @"C:\s.ps1") with { WaitCompileSeconds = 900 };
        Assert.Throws<ArgumentException>(() => wait.Validate());
        var outDir = MakeRequest(projectDir, "relative\\out", @"C:\s.ps1");
        Assert.Throws<ArgumentException>(() => outDir.Validate());
    }

    // ---------------- Gate 1: MNM generation failures never reach the script ----------------

    [Theory]
    [InlineData("b", "sw := TRUE;", 60)]                   // reserved-word module name
    [InlineData("DM100", "sw := TRUE;", 60)]               // device-designator module name
    [InlineData("Ok1", "(* only comments *)", 60)]         // no executable statements
    [InlineData("Ok1", "DEVICE:60\r\nsw := TRUE;", 60)]    // container injection line
    public async Task MnmGate_Failure_StopsBeforeScript(string moduleName, string source, int deviceCode)
    {
        var runner = new FakeKvDeployRunner();
        var deploy = new KeyenceDeployProgram(runner);
        var projectDir = CreateTempDir();
        File.WriteAllText(Path.Combine(projectDir, "test.kpr"), "stub");
        var script = Path.Combine(CreateTempDir(), "kv_studio_deploy.ps1");
        File.WriteAllText(script, "#fake");

        var report = await deploy.RunAsync(
            MakeRequest(projectDir, CreateTempDir(), script, moduleName: moduleName, source: source, deviceCode: deviceCode));

        Assert.False(report.Passed);
        Assert.Equal("FAIL", report.OutcomeOf("mnm"));
        Assert.Null(report.OutcomeOf("import"));
        Assert.StartsWith("KV_DEPLOY_MNM_GENERATION_FAILED", report.FatalError);
        Assert.Equal(0, runner.Calls); // script never invoked on gate 1 failure
    }

    [Fact]
    public async Task MnmGate_Warnings_CarriedIntoReport_AndPipelineContinues()
    {
        var runner = new FakeKvDeployRunner();
        var deploy = new KeyenceDeployProgram(runner);
        var projectDir = CreateTempDir();
        File.WriteAllText(Path.Combine(projectDir, "test.kpr"), "stub");
        var script = Path.Combine(CreateTempDir(), "s.ps1");
        File.WriteAllText(script, "#fake");
        var kvs = CreateFakeKvs();

        // Bare device designator in the body: generator warning, not a hard failure.
        var report = await deploy.RunAsync(
            MakeRequest(projectDir, CreateTempDir(), script, kvsExe: kvs, source: "DM100 := 5;"));

        Assert.Contains(report.Warnings, w => w.Contains("Bare device designator", StringComparison.Ordinal));
        Assert.Equal(1, runner.Calls);
    }

    // ---------------- Pure helpers ----------------

    [Theory]
    [InlineData(0, "OK", null, false)]
    [InlineData(1, "ARGS", null, true)]
    [InlineData(2, "ENVIRONMENT", null, true)]
    [InlineData(3, "START_BIND", "import", true)]
    [InlineData(10, "IMPORT_FAIL", "import", true)]
    [InlineData(20, "VARIABLES_FAIL", "variables", true)]
    [InlineData(30, "COMPILE_NG", "compile", true)]
    [InlineData(40, "PERSIST_FAIL", "persist", true)]
    [InlineData(50, "TIMEOUT", null, true)]
    [InlineData(77, "FATAL", null, true)]
    public void MapExitCode_FullContractTable(int code, string label, string? gate, bool fatal)
    {
        var meaning = KeyenceDeployProgram.MapExitCode(code);
        Assert.Equal(label, meaning.Label);
        Assert.Equal(gate, meaning.Gate);
        Assert.Equal(fatal, meaning.Fatal);
    }

    [Fact]
    public void MapExitCode_WithReport_FlagsContractDisagreement()
    {
        var failing = new KeyenceDeployReport(
            Passed: false,
            Stages: new Dictionary<string, string?> { ["compile"] = "FAIL" },
            ProjectDir: null, ModuleName: null, OutputDir: null, MnmPath: null,
            Compile: null, Errors: [], Messages: [], Warnings: [],
            FatalError: null, ExitCode: 0, Detail: null);
        Assert.True(KeyenceDeployProgram.MapExitCode(0, failing).Fatal);

        var consistent = failing with
        {
            Passed = true,
            Stages = new Dictionary<string, string?> { ["compile"] = "PASS" }
        };
        Assert.False(KeyenceDeployProgram.MapExitCode(0, consistent).Fatal);
    }

    [Fact]
    public void ComputeProcessTimeout_Bounds()
    {
        Assert.Equal(TimeSpan.FromSeconds(300), KeyenceDeployProgram.ComputeProcessTimeout(40));
        Assert.Equal(TimeSpan.FromSeconds(300), KeyenceDeployProgram.ComputeProcessTimeout(5));  // lower clamp
        Assert.Equal(TimeSpan.FromSeconds(600), KeyenceDeployProgram.ComputeProcessTimeout(340));
        Assert.Equal(TimeSpan.FromSeconds(860), KeyenceDeployProgram.ComputeProcessTimeout(600));
    }

    [Fact]
    public void BuildArguments_ExactContractShape()
    {
        var request = MakeRequest(
            @"C:\Projects\my qa 2\proj", @"D:\Temp\kv-deploy", @"C:\repo\scripts\kv_studio_deploy.ps1",
            kvsExe: @"C:\KVS 12\Kvs.exe");
        var arguments = KeyenceDeployProgram.BuildArguments(request, @"D:\Temp\kv-deploy\MotorCtl.mnm", @"D:\Temp\kv-deploy");

        Assert.Equal(
            [
                "-ProjectDir", @"C:\Projects\my qa 2\proj",
                "-MnmPath", @"D:\Temp\kv-deploy\MotorCtl.mnm",
                "-ModuleName", "MotorCtl",
                "-KvsExe", @"C:\KVS 12\Kvs.exe",
                "-WaitCompileSeconds", "40",
                "-OutDir", @"D:\Temp\kv-deploy"
            ], arguments);
        // Every path stays its own argument element: no concatenated command line = no injection surface.
        Assert.DoesNotContain(arguments, a => a.Contains(" -ProjectDir ", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildArguments_VariablesAndDryRun()
    {
        var request = MakeRequest(
            @"C:\p", @"C:\o", @"C:\s.ps1",
            variables: [new KeyenceDeployVariable("sw", "BOOL")],
            dryRun: true) with { KvsExe = @"C:\kvs\Kvs.exe", KvUia = @"C:\tools\kv-uia.exe", WaitCompileSeconds = 45 };
        var arguments = KeyenceDeployProgram.BuildArguments(
            request, @"C:\o\MotorCtl.mnm", @"C:\o", @"C:\o\MotorCtl-variables.tsv");

        Assert.Equal("-VariablesTsv", arguments[6]);
        Assert.Equal(@"C:\o\MotorCtl-variables.tsv", arguments[7]);
        Assert.Equal("-KvsExe", arguments[8]);
        Assert.Equal("-KvUia", arguments[10]);
        Assert.Equal("-WaitCompileSeconds", arguments[12]);
        Assert.Equal("45", arguments[13]);
        Assert.Equal("-OutDir", arguments[14]);
        Assert.Equal("-DryRun", arguments[^1]);
    }

    [Fact]
    public void BuildInvocation_UsesPowershellAndNoProfile()
    {
        var request = MakeRequest(@"C:\p", @"C:\o", @"C:\s.ps1");
        var invocation = KeyenceDeployProgram.BuildInvocation(request, @"C:\repo\scripts\kv_studio_deploy.ps1", @"C:\o\M.mnm", @"C:\o", null);
        Assert.Equal("powershell.exe", invocation.Executable);
        Assert.Equal(
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\repo\scripts\kv_studio_deploy.ps1"],
            invocation.Arguments.Take(5).ToArray());
        Assert.Equal("-ProjectDir", invocation.Arguments[5]);
    }

    [Theory]
    [InlineData("KV-DEPLOY-RESULT 0 import=PASS variables=PASS compile=PASS persist=PASS", new[] { "PASS", "PASS", "PASS", "PASS" })]
    [InlineData("prefix noise\nKV-DEPLOY-RESULT 30 import=PASS variables=PASS compile=FAIL persist=SKIP\n", new[] { "PASS", "PASS", "FAIL", "SKIP" })]
    [InlineData("KV-DEPLOY-RESULT 10 import=PASS variables=skip compile=FAIL persist=FAIL", new[] { "PASS", "SKIP", "FAIL", "FAIL" })]
    [InlineData("no summary here", new string[0])]
    public void ParseSummaryLine_Shapes(string stdout, string[] expected)
    {
        Assert.Equal(expected, KeyenceDeployProgram.ParseSummaryLine(stdout));
    }

    [Fact]
    public void NormalizeStage_Tokens()
    {
        Assert.Equal("PASS", KeyenceDeployProgram.NormalizeStage("pass"));
        Assert.Equal("SKIP", KeyenceDeployProgram.NormalizeStage(" Skip "));
        Assert.Null(KeyenceDeployProgram.NormalizeStage("maybe"));
        Assert.Null(KeyenceDeployProgram.NormalizeStage(null));
    }

    // ---------------- ParseCompileErrors ----------------

    [Fact]
    public void ParseCompileErrors_EmptyOrNull_ReturnsNothing()
    {
        Assert.Empty(KeyenceDeployProgram.ParseCompileErrors(null));
        Assert.Empty(KeyenceDeployProgram.ParseCompileErrors(""));
        Assert.Empty(KeyenceDeployProgram.ParseCompileErrors("   \n\t\n"));
    }

    [Fact]
    public void ParseCompileErrors_OkHeaderOnly()
    {
        var rows = KeyenceDeployProgram.ParseCompileErrors("转换结果 OK\r\n总指令数: 2\r\n程序容量 OK 错误 0 警告 0\r\n");
        Assert.Empty(rows);
    }

    [Fact]
    public void ParseCompileErrors_Ng_MixedRowShapes()
    {
        string text = string.Join("\n",
            "转换结果 NG",
            "MotorCtl(行 00003)(ST行 0002)[错误 1232]:\"sw\": 标识符未在变量表登记 [变量名]",
            "Main(1)(行 00005)[警告 61]:定时器编号与其他模块重复",
            "[错误709]：没有可以转换的程序",
            "[错误214]：FB实例未登记[未登记变量名]",
            "[警告33]: ENDH 可省略",
            "总指令数: 2");

        var rows = KeyenceDeployProgram.ParseCompileErrors(text);
        Assert.Equal(5, rows.Count);

        Assert.Equal("MotorCtl", rows[0].Module);
        Assert.Equal(3, rows[0].Line);
        Assert.Equal(2, rows[0].StLine);
        Assert.Equal(1232, rows[0].Code);
        Assert.Equal("错误", rows[0].Kind);
        Assert.Equal("sw", rows[0].Symbol);
        Assert.Equal("标识符未在变量表登记 [变量名]", rows[0].Message);

        Assert.Equal("Main(1)", rows[1].Module);
        Assert.Equal(5, rows[1].Line);
        Assert.Null(rows[1].StLine);
        Assert.Equal(61, rows[1].Code);
        Assert.Equal("警告", rows[1].Kind);
        Assert.Equal("定时器编号与其他模块重复", rows[1].Message);

        Assert.Null(rows[2].Module);
        Assert.Equal(709, rows[2].Code);
        Assert.Equal("错误", rows[2].Kind);
        Assert.Equal("没有可以转换的程序", rows[2].Message);

        Assert.Equal(214, rows[3].Code);
        Assert.Equal("错误", rows[3].Kind);

        Assert.Equal(33, rows[4].Code);
        Assert.Equal("警告", rows[4].Kind);
    }

    [Fact]
    public void ParseCompileErrors_UnknownLayoutWithMarker_FallsBackToMessageRow()
    {
        var rows = KeyenceDeployProgram.ParseCompileErrors("乱码前缀 [错误 42] 无法归类");
        Assert.Single(rows);
        Assert.Null(rows[0].Module);
        Assert.Equal(42, rows[0].Code);
        Assert.Equal("错误", rows[0].Kind);
        Assert.Contains("无法归类", rows[0].Message, StringComparison.Ordinal);
    }

    // ---------------- ParseResultJson ----------------

    [Fact]
    public void ParseResultJson_FullContract()
    {
        var result = KeyenceDeployProgram.ParseResultJson(BuildResultJson(
            import: "PASS", variables: "SKIP", compile: "FAIL", persist: "PASS",
            state: "NG", errorCount: 1, warningCount: 0, rawHeader: "转换结果 NG",
            errors: "[{\"module\":\"MotorCtl\",\"line\":3,\"stLine\":null,\"code\":1232,\"kind\":\"错误\",\"message\":\"未登记\",\"symbol\":\"sw\"}]"));

        Assert.Equal("1.0.0", result.ScriptVersion);
        Assert.False(result.DryRun);
        Assert.Equal(KeyenceGateOutcome.Pass, result.Gates["import"]);
        Assert.Equal(KeyenceGateOutcome.Skip, result.Gates["variables"]);
        Assert.Equal(KeyenceGateOutcome.Fail, result.Gates["compile"]);
        Assert.Equal(KeyenceGateOutcome.Pass, result.Gates["persist"]);
        Assert.Equal("NG", result.Compile!.State);
        Assert.Equal(1, result.Compile.ErrorCount);
        Assert.Equal("转换结果 NG", result.Compile.RawHeader);
        Assert.Single(result.Compile.Errors);
        Assert.Equal(1232, result.Compile.Errors[0].Code);
        Assert.Null(result.Compile.Errors[0].StLine);
        Assert.True(result.ImportDetail!.ModuleFoundInTree);
        Assert.Single(result.ImportDetail.Dialogs);
        Assert.True(result.Persist!.Changed);
        Assert.Equal("beforehash", result.Persist.FingerprintBefore);
        Assert.Contains("ps-fake ran", result.Messages);
        Assert.Empty(result.MalformedFields);
    }

    [Fact]
    public void ParseResultJson_MissingFields_RecordsMalformedWithoutThrowing()
    {
        var result = KeyenceDeployProgram.ParseResultJson("{\"gates\":{}}");
        Assert.Contains(result.MalformedFields, m => m.Contains("scriptVersion", StringComparison.Ordinal));
        Assert.Contains(result.MalformedFields, m => m.Contains("compile", StringComparison.Ordinal));
        Assert.Empty(result.Gates);
        Assert.Null(result.Compile);
        Assert.Null(result.Persist);
    }

    [Fact]
    public void ParseResultJson_UnexpectedGateValueAndVersionFlagged()
    {
        var result = KeyenceDeployProgram.ParseResultJson(
            "{\"scriptVersion\":\"2.0.0\",\"gates\":{\"import\":\"MAYBE\"},\"compile\":{}}");
        Assert.Null(result.Gates["import"]);
        Assert.Contains(result.MalformedFields, m => m.Contains("gates.import", StringComparison.Ordinal));
        Assert.Contains(result.MalformedFields, m => m.Contains("scriptVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseResultJson_MalformedJson_ThrowsJsonException()
    {
        // JsonReaderException derives from JsonException; Assert.Throws requires an exact match.
        Assert.ThrowsAny<JsonException>(() => KeyenceDeployProgram.ParseResultJson("{not json"));
        Assert.ThrowsAny<JsonException>(() => KeyenceDeployProgram.ParseResultJson("5"));
    }

    // ---------------- FakeRunner end-to-end ----------------

    [Fact]
    public async Task EndToEnd_AllGatesPass_SuccessReport()
    {
        var projectDir = CreateTempDir();
        File.WriteAllText(Path.Combine(projectDir, "test.kpr"), "stub-kpr");
        var outDir = CreateTempDir();
        var script = Path.Combine(CreateTempDir(), "kv_studio_deploy.ps1");
        File.WriteAllText(script, "#fake script");
        var kvs = CreateFakeKvs();

        var runner = new FakeKvDeployRunner(() => new FakePlan
        {
            ExitCode = 0,
            ResultJson = BuildResultJson(changed: true),
            // Simulate the script mutating the project (save), so the independent
            // fingerprint agrees with persist.changed=true.
            BeforeReturn = () => File.WriteAllText(Path.Combine(projectDir, "All.prgx"), "saved")
        });

        var deploy = new KeyenceDeployProgram(runner);
        var report = await deploy.RunAsync(MakeRequest(projectDir, outDir, script, kvsExe: kvs));

        Assert.True(report.Passed);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal("PASS", report.OutcomeOf("mnm"));
        Assert.Equal("PASS", report.OutcomeOf("import"));
        Assert.Equal("PASS", report.OutcomeOf("compile"));
        Assert.Equal("PASS", report.OutcomeOf("persist"));
        Assert.Null(report.FatalError);
        Assert.NotNull(report.Compile);
        Assert.Equal("OK", report.Compile.State);
        Assert.Equal(1, runner.Calls);
        Assert.Equal("powershell.exe", runner.LastExecutable);
        Assert.Equal(TimeSpan.FromSeconds(300), runner.LastTimeout);
        Assert.Contains("-MnmPath", runner.LastArguments!);
        Assert.Contains(Path.Combine(outDir, "MotorCtl.mnm"), runner.LastArguments!);
        Assert.True(File.Exists(Path.Combine(outDir, "MotorCtl.mnm")));
        Assert.Equal(Path.Combine(outDir, "kv-deploy-result.json"), runner.LastResultJsonPath);
        Assert.Contains(report.Messages, m => m.Contains("agrees", StringComparison.Ordinal));
        Assert.NotNull(report.Detail);
        Assert.Equal(5, KeyenceDeployReport.GateNames.Length);
    }

    [Fact]
    public async Task EndToEnd_CompileNG_Exit30_MarksLaterGatesAndCarriesErrors()
    {
        var projectDir = CreateTempDir();
        File.WriteAllText(Path.Combine(projectDir, "test.kpr"), "stub");
        var script = Path.Combine(CreateTempDir(), "s.ps1");
        File.WriteAllText(script, "#fake");
        var kvs = CreateFakeKvs();

        var runner = new FakeKvDeployRunner(() => new FakePlan
        {
            ExitCode = 30,
            Stdout = "KV-DEPLOY-RESULT 30 import=PASS variables=PASS compile=FAIL persist=FAIL",
            ResultJson = BuildResultJson(
                compile: "FAIL", state: "NG", errorCount: 1,
                rawHeader: "转换结果 NG",
                errors: "[{\"module\":\"MotorCtl\",\"line\":3,\"stLine\":2,\"code\":1232,\"kind\":\"错误\",\"message\":\"未登记\",\"symbol\":\"sw\"}]",
                persist: "FAIL")
        });

        var report = await new KeyenceDeployProgram(runner).RunAsync(MakeRequest(projectDir, CreateTempDir(), script, kvsExe: kvs));

        Assert.False(report.Passed);
        Assert.Equal(30, report.ExitCode);
        Assert.StartsWith("KV_DEPLOY_COMPILE_NG", report.FatalError);
        Assert.Equal("FAIL", report.OutcomeOf("compile"));
        Assert.Equal("PASS", report.OutcomeOf("import"));
        Assert.Equal("NG", report.Compile!.State);
        Assert.Equal(1, report.Compile.ErrorCount);
        Assert.Equal(1232, report.Errors[0].Code);
        Assert.Equal("sw", report.Compile.FirstError!.Symbol);
    }

    [Fact]
    public async Task EndToEnd_FingerprintDisagreement_KeepsScriptAuthoritative()
    {
        var projectDir = CreateTempDir();
        File.WriteAllText(Path.Combine(projectDir, "test.kpr"), "stub");
        var script = Path.Combine(CreateTempDir(), "s.ps1");
        File.WriteAllText(script, "#fake");
        var kvs = CreateFakeKvs();

        // Script claims changed=true but touches nothing: fingerprint says unchanged.
        var runner = new FakeKvDeployRunner(() => new FakePlan { ExitCode = 0, ResultJson = BuildResultJson(changed: true) });
        var report = await new KeyenceDeployProgram(runner).RunAsync(MakeRequest(projectDir, CreateTempDir(), script, kvsExe: kvs));

        Assert.True(report.Passed); // script result stays authoritative
        Assert.Contains(report.Messages, m => m.Contains("disagrees", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EndToEnd_ScriptMissing_ReportedAsEnvironmentFailure()
    {
        var projectDir = CreateTempDir();
        File.WriteAllText(Path.Combine(projectDir, "test.kpr"), "stub");
        var runner = new FakeKvDeployRunner();
        var deploy = new KeyenceDeployProgram(runner);

        var report = await deploy.RunAsync(
            MakeRequest(projectDir, CreateTempDir(), @"C:\definitely\not\here\kv_studio_deploy.ps1"));

        Assert.False(report.Passed);
        Assert.Equal("KV_DEPLOY_SCRIPT_MISSING", report.FatalError);
        Assert.Equal("FAIL", report.OutcomeOf("import"));
        Assert.Contains(report.Messages, m => m.Contains("部署脚本缺失", StringComparison.Ordinal));
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task EndToEnd_KvsMissing_EnvironmentGate()
    {
        var projectDir = CreateTempDir();
        File.WriteAllText(Path.Combine(projectDir, "test.kpr"), "stub");
        var script = Path.Combine(CreateTempDir(), "s.ps1");
        File.WriteAllText(script, "#fake");
        var runner = new FakeKvDeployRunner();
        var deploy = new KeyenceDeployProgram(runner);

        var missingKvs = Path.Combine(CreateTempDir(), "missing.exe");
        var report = await deploy.RunAsync(
            MakeRequest(projectDir, CreateTempDir(), script, kvsExe: missingKvs));

        Assert.False(report.Passed);
        Assert.Equal("KV_DEPLOY_KVS_MISSING", report.FatalError);
        Assert.Equal(2, report.ExitCode);
        Assert.Equal(0, runner.Calls);

        // DryRun skips the local KVS precheck (the script side owns environment checks).
        var dry = new FakeKvDeployRunner(() => new FakePlan { ExitCode = 0, ResultJson = BuildResultJson(dryRun: true) });
        var dryReport = await new KeyenceDeployProgram(dry).RunAsync(
            MakeRequest(projectDir, CreateTempDir(), script, kvsExe: missingKvs, dryRun: true));
        Assert.Equal(1, dry.Calls);
        Assert.True(dryReport.Passed);
    }

    [Fact]
    public async Task EndToEnd_Timeout_ReportsUndeterminedGates()
    {
        var projectDir = CreateTempDir();
        File.WriteAllText(Path.Combine(projectDir, "test.kpr"), "stub");
        var script = Path.Combine(CreateTempDir(), "s.ps1");
        File.WriteAllText(script, "#fake");
        var kvs = CreateFakeKvs();

        // The fake models the wall-clock overrun: the process was killed, so the runner
        // reports TimedOut with no exit code and no result file.
        var runner = new FakeKvDeployRunner(() => new FakePlan { TimedOut = true, Stdout = "" });
        var report = await new KeyenceDeployProgram(runner).RunAsync(MakeRequest(projectDir, CreateTempDir(), script, kvsExe: kvs));

        Assert.False(report.Passed);
        Assert.Null(report.ExitCode);
        Assert.StartsWith("KV_DEPLOY_TIMEOUT", report.FatalError);
        Assert.Equal("PASS", report.OutcomeOf("mnm"));
        Assert.Null(report.OutcomeOf("import"));   // undetermined, not faked
        Assert.Null(report.OutcomeOf("variables"));
        Assert.Null(report.OutcomeOf("compile"));
        Assert.Null(report.OutcomeOf("persist"));
    }

    [Fact]
    public async Task EndToEnd_ResultJsonMissing_FallsBackToSummaryAndExitCode()
    {
        var projectDir = CreateTempDir();
        File.WriteAllText(Path.Combine(projectDir, "test.kpr"), "stub");
        var script = Path.Combine(CreateTempDir(), "s.ps1");
        File.WriteAllText(script, "#fake");
        var kvs = CreateFakeKvs();

        // Exit 10 (import FAIL) with no JSON and no summary line at all.
        var runner = new FakeKvDeployRunner(() => new FakePlan { ExitCode = 10, Stdout = "boom" });
        var report = await new KeyenceDeployProgram(runner).RunAsync(MakeRequest(projectDir, CreateTempDir(), script, kvsExe: kvs));

        Assert.False(report.Passed);
        Assert.StartsWith("KV_DEPLOY_IMPORT_FAIL", report.FatalError);
        Assert.Equal("FAIL", report.OutcomeOf("import"));
        // Named-gate reconciliation marks the gate and the later ones FAIL: they did not complete.
        Assert.Equal("FAIL", report.OutcomeOf("variables"));
        Assert.Equal("FAIL", report.OutcomeOf("compile"));
        Assert.Equal("FAIL", report.OutcomeOf("persist"));
        Assert.Contains(report.Messages, m => m.Contains("no kv-deploy-result.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EndToEnd_ResultJsonCorrupt_ReportedAsContractViolation()
    {
        var projectDir = CreateTempDir();
        File.WriteAllText(Path.Combine(projectDir, "test.kpr"), "stub");
        var script = Path.Combine(CreateTempDir(), "s.ps1");
        File.WriteAllText(script, "#fake");
        var kvs = CreateFakeKvs();

        var runner = new FakeKvDeployRunner(() => new FakePlan
        {
            ExitCode = 0,
            ResultJson = "{ this is not json",
            Stdout = "KV-DEPLOY-RESULT 0 import=PASS variables=PASS compile=PASS persist=PASS"
        });
        var report = await new KeyenceDeployProgram(runner).RunAsync(MakeRequest(projectDir, CreateTempDir(), script, kvsExe: kvs));

        Assert.Contains(report.Messages, m => m.Contains("not valid JSON", StringComparison.Ordinal));
        Assert.True(report.Passed); // the summary line carries the gates through
    }

    [Fact]
    public async Task EndToEnd_VariablesWrittenAsTsv_AndPassedToScript()
    {
        var projectDir = CreateTempDir();
        File.WriteAllText(Path.Combine(projectDir, "test.kpr"), "stub");
        var outDir = CreateTempDir();
        var script = Path.Combine(CreateTempDir(), "s.ps1");
        File.WriteAllText(script, "#fake");
        var kvs = CreateFakeKvs();

        var runner = new FakeKvDeployRunner(() => new FakePlan { ExitCode = 0, ResultJson = BuildResultJson() });
        var request = MakeRequest(projectDir, outDir, script, kvsExe: kvs,
            variables: [new KeyenceDeployVariable("sw", "BOOL"), new KeyenceDeployVariable("result", "BOOL")]);
        var report = await new KeyenceDeployProgram(runner).RunAsync(request);

        Assert.True(report.Passed);
        var tsv = Path.Combine(outDir, "MotorCtl-variables.tsv");
        Assert.True(File.Exists(tsv));
        Assert.Equal("sw\tBOOL\nresult\tBOOL\n", File.ReadAllText(tsv));
        Assert.Contains("-VariablesTsv", runner.LastArguments!);
        Assert.Contains(tsv, runner.LastArguments!);
    }

    [Fact]
    public async Task EndToEnd_ExitZeroButGatesUnreported_MarkedAsContractViolation()
    {
        var projectDir = CreateTempDir();
        File.WriteAllText(Path.Combine(projectDir, "test.kpr"), "stub");
        var script = Path.Combine(CreateTempDir(), "s.ps1");
        File.WriteAllText(script, "#fake");
        var kvs = CreateFakeKvs();

        var runner = new FakeKvDeployRunner(() => new FakePlan
        {
            ExitCode = 0,
            Stdout = "",
            ResultJson = BuildResultJson(omitGates: true, omitCompile: true)
        });
        var report = await new KeyenceDeployProgram(runner).RunAsync(MakeRequest(projectDir, CreateTempDir(), script, kvsExe: kvs));

        Assert.False(report.Passed);
        Assert.Equal("FAIL", report.OutcomeOf("import"));
        Assert.Contains(report.Messages, m => m.Contains("contract violated", StringComparison.Ordinal));
    }

    // ---------------- Real powershell subprocess (contract-conforming fake script, no KV STUDIO) ----------------

    private const string FakeContractScript = """
        param(
          [Parameter(Mandatory=$true)][string]$ProjectDir,
          [Parameter(Mandatory=$true)][string]$MnmPath,
          [Parameter(Mandatory=$true)][string]$ModuleName,
          [string]$VariablesTsv,
          [string]$KvsExe,
          [string]$KvUia,
          [int]$WaitCompileSeconds = 40,
          [string]$OutDir,
          [switch]$DryRun)

        if (-not (Test-Path -LiteralPath $MnmPath)) { Write-Error 'mnm missing'; exit 1 }
        $dir = if ($OutDir) { $OutDir } else { $env:TEMP }
        New-Item -ItemType Directory -Force -Path $dir | Out-Null

        $ok = -not $env:KV_FAKE_NG
        $gates = if ($ok) {
            @{ import = 'PASS'; variables = 'PASS'; compile = 'PASS'; persist = 'PASS' }
        } else {
            @{ import = 'PASS'; variables = 'PASS'; compile = 'FAIL'; persist = 'FAIL' }
        }
        $compile = if ($ok) {
            @{ state = 'OK'; errorCount = 0; warningCount = 0; rawHeader = 'OK'; errors = @(); rawText = '' }
        } else {
            @{ state = 'NG'; errorCount = 1; warningCount = 0; rawHeader = 'NG'; errors = @(
                @{ module = $ModuleName; line = 3; stLine = 2; code = 1232; kind = 'err'; message = 'x'; symbol = 'sw' }); rawText = '' }
        }
        $result = [ordered]@{
            scriptVersion = '1.0.0'
            projectDir    = $ProjectDir
            moduleName    = $ModuleName
            dryRun        = [bool]$DryRun
            gates         = $gates
            compile       = $compile
            importDetail  = @{ moduleFoundInTree = $true; dialogs = @() }
            persist       = @{ fingerprintBefore = 'a'; fingerprintAfter = 'b'; changed = $ok }
            messages      = @('ps-fake ran', ('wait=' + $WaitCompileSeconds))
        }
        $json = $result | ConvertTo-Json -Depth 6 -Compress
        [System.IO.File]::WriteAllText((Join-Path $dir 'kv-deploy-result.json'), $json, (New-Object System.Text.UTF8Encoding($false)))

        $i = $gates.import; $v = $gates.variables; $c = $gates.compile; $p = $gates.persist
        $code = if ($ok) { 0 } else { 30 }
        Write-Output ('KV-DEPLOY-RESULT ' + $code + ' import=' + $i + ' variables=' + $v + ' compile=' + $c + ' persist=' + $p)
        exit $code
        """;

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Skip = "Requires Windows powershell.exe for the subprocess contract test.";
            }
        }
    }

    [WindowsFact]
    [Trait("Category", "KeyenceDeploySubprocess")]
    public async Task Subprocess_RealFakeScript_AllGatesPassThroughPowershell()
    {
        var projectDir = CreateTempDir();
        File.WriteAllText(Path.Combine(projectDir, "test.kpr"), "stub");
        var outDir = CreateTempDir();
        var script = Path.Combine(CreateTempDir(), "kv_studio_deploy.ps1");
        File.WriteAllText(script, FakeContractScript, new UTF8Encoding(true)); // BOM: PS 5.1 UTF-8 reader
        var kvs = CreateFakeKvs();

        // Default production runner (ProcessKvDeployRunner) against the temporary fake script.
        var deploy = new KeyenceDeployProgram();
        var report = await deploy.RunAsync(MakeRequest(projectDir, outDir, script, kvsExe: kvs));

        Assert.True(report.Passed);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal("PASS", report.OutcomeOf("compile"));
        Assert.NotNull(report.Compile);
        Assert.Equal("OK", report.Compile.State);
        Assert.Contains(report.Messages, m => m.Contains("ps-fake ran", StringComparison.Ordinal));
        Assert.Contains(report.Messages, m => m.Contains("wait=40", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(outDir, "kv-deploy-result.json")));
    }

    [WindowsFact]
    [Trait("Category", "KeyenceDeploySubprocess")]
    public async Task Subprocess_RealFakeScript_CompileNGPath()
    {
        var projectDir = CreateTempDir();
        File.WriteAllText(Path.Combine(projectDir, "test.kpr"), "stub");
        var script = Path.Combine(CreateTempDir(), "kv_studio_deploy.ps1");
        File.WriteAllText(script, FakeContractScript, new UTF8Encoding(true));
        var kvs = CreateFakeKvs();

        var previous = Environment.GetEnvironmentVariable("KV_FAKE_NG");
        Environment.SetEnvironmentVariable("KV_FAKE_NG", "1");
        try
        {
            var report = await new KeyenceDeployProgram().RunAsync(
                MakeRequest(projectDir, CreateTempDir(), script, kvsExe: kvs));

            Assert.False(report.Passed);
            Assert.Equal(30, report.ExitCode);
            Assert.StartsWith("KV_DEPLOY_COMPILE_NG", report.FatalError);
            Assert.Equal("FAIL", report.OutcomeOf("compile"));
            Assert.Equal(1232, Assert.Single(report.Errors).Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KV_FAKE_NG", previous);
        }
    }

    // ---------------- MCP router wiring ----------------

    private static JsonObject CallToNode(object raw) =>
        JsonSerializer.SerializeToNode(raw)!.AsObject();

    private static string FirstTextContent(JsonObject node) =>
        node["content"]!.AsArray()[0]!["text"]!.GetValue<string>();

    [Fact]
    public void Router_KeyenceDeployTool_RegisteredOnlyBehindGovernanceSwitch()
    {
        var plain = new McpToolRouter(DefaultPlcComposition.Create(), new ServerGovernanceServices(CreateTempDir()));
        Assert.DoesNotContain(plain.ToolDescriptors, t => t.Name == "plc_keyence_deploy_program");

        var enabled = new McpToolRouter(DefaultPlcComposition.Create(), new ServerGovernanceServices(
            CreateTempDir(),
            keyenceDeployOptions: new KeyenceDeployOptions(),
            kvDeployRunner: new FakeKvDeployRunner()));
        var descriptor = Assert.Single(enabled.ToolDescriptors, t => t.Name == "plc_keyence_deploy_program");
        Assert.False(descriptor.Annotations.ReadOnlyHint);
        Assert.False(descriptor.Annotations.DestructiveHint);
    }

    [Fact]
    public async Task Router_CallUnknownWhenDisabled_IsError()
    {
        var router = new McpToolRouter(DefaultPlcComposition.Create(), new ServerGovernanceServices(CreateTempDir()));
        using var args = JsonDocument.Parse("{}");
        var node = CallToNode(await router.CallToolAsync("plc_keyence_deploy_program", args.RootElement, CancellationToken.None));
        Assert.True(node["isError"]!.GetValue<bool>());
        Assert.Contains("Unknown tool", FirstTextContent(node), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Router_Handler_ValidationErrorsSurfaceAsToolErrors()
    {
        var router = new McpToolRouter(DefaultPlcComposition.Create(), new ServerGovernanceServices(
            CreateTempDir(),
            keyenceDeployOptions: new KeyenceDeployOptions(),
            kvDeployRunner: new FakeKvDeployRunner()));

        using var args = JsonDocument.Parse(
            "{\"source\":\"a := TRUE;\",\"moduleName\":\"MotorCtl\",\"deviceCode\":60," +
            "\"projectDir\":\"relative\\\\p\",\"dryRun\":true}");
        var node = CallToNode(await router.CallToolAsync("plc_keyence_deploy_program", args.RootElement, CancellationToken.None));
        Assert.True(node["isError"]!.GetValue<bool>());
        Assert.Contains("absolute", FirstTextContent(node), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Router_Handler_DryRun_EndToEndThroughMcp()
    {
        var projectDir = CreateTempDir();
        File.WriteAllText(Path.Combine(projectDir, "test.kpr"), "stub");
        var outDir = CreateTempDir();
        var script = Path.Combine(CreateTempDir(), "kv_studio_deploy.ps1");
        File.WriteAllText(script, "#fake");
        var kvs = CreateFakeKvs();

        var runner = new FakeKvDeployRunner(() => new FakePlan { ExitCode = 0, ResultJson = BuildResultJson(dryRun: true) });
        var router = new McpToolRouter(DefaultPlcComposition.Create(), new ServerGovernanceServices(
            CreateTempDir(),
            keyenceDeployOptions: new KeyenceDeployOptions { ScriptPath = script, KvsExe = kvs },
            kvDeployRunner: runner));

        using var args = JsonDocument.Parse(
            "{\"source\":\"sw := TRUE;\",\"moduleName\":\"MotorCtl\",\"deviceCode\":60," +
            $"\"projectDir\":\"{JsonEscape(projectDir)}\",\"outDir\":\"{JsonEscape(outDir)}\",\"dryRun\":true," +
            "\"variables\":[{\"name\":\"sw\",\"dataType\":\"BOOL\"}]}");
        var node = CallToNode(await router.CallToolAsync("plc_keyence_deploy_program", args.RootElement, CancellationToken.None));

        Assert.False(node["isError"]!.GetValue<bool>());
        var payload = JsonNode.Parse(FirstTextContent(node))!;
        Assert.True(payload["passed"]!.GetValue<bool>());
        Assert.Equal("PASS", payload["stages"]!["mnm"]!.GetValue<string>());
        Assert.Equal("PASS", payload["stages"]!["persist"]!.GetValue<string>());
        Assert.Equal(1, runner.Calls);
        var tsv = Path.Combine(outDir, "MotorCtl-variables.tsv");
        Assert.True(File.Exists(tsv));
    }

    private static string JsonEscape(string path) => path.Replace("\\", "\\\\");

    // ---------------- Live KV STUDIO smoke (environment-gated; skipped by default) ----------------

    private sealed class KeyenceLiveFactAttribute : FactAttribute
    {
        public KeyenceLiveFactAttribute()
        {
            bool envEnabled = string.Equals(Environment.GetEnvironmentVariable("KV_DEPLOY_LIVE"), "1", StringComparison.Ordinal);
            bool kvsInstalled = File.Exists(KeyenceInstallationDoctor.DefaultIdeExePath);
            bool scriptPresent =
                File.Exists(Path.Combine(AppContext.BaseDirectory, "scripts", "kv_studio_deploy.ps1")) ||
                File.Exists(Path.Combine(FindRepoRoot(), "scripts", "kv_studio_deploy.ps1"));
            if (!envEnabled || !kvsInstalled || !scriptPresent)
            {
                Skip = "Requires KV_DEPLOY_LIVE=1, a real KV STUDIO install and the real scripts/kv_studio_deploy.ps1.";
            }
        }

        private static string FindRepoRoot()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "PlcMcp.slnx")))
                {
                    return dir.FullName;
                }
            }

            return AppContext.BaseDirectory;
        }
    }

    [KeyenceLiveFact]
    public async Task KeyenceDeploy_LiveScript_Smoke_OnProjectCopy()
    {
        // Deliberately narrow: point KV_DEPLOY_LIVE/KV_DEPLOY_LIVE_PROJECT at an existing
        // project COPY plus the real script, then confirm the five-gate report round-trips.
        // Never a physical PLC download.
        var projectDir = Environment.GetEnvironmentVariable("KV_DEPLOY_LIVE_PROJECT")
            ?? throw new InvalidOperationException("KV_DEPLOY_LIVE_PROJECT (absolute project copy dir) is required.");
        var moduleName = Environment.GetEnvironmentVariable("KV_DEPLOY_LIVE_MODULE") ?? "LiveSmoke";

        var report = await new KeyenceDeployProgram().RunAsync(new KeyenceDeployRequest
        {
            Source = "sw := TRUE;",
            ModuleName = moduleName,
            DeviceCode = 60,
            ProjectDir = projectDir,
            Variables = [new KeyenceDeployVariable("sw", "BOOL")],
            WaitCompileSeconds = 60
        });

        Assert.NotNull(report.Stages["mnm"]);
        Assert.NotNull(report.ExitCode);
    }
}
