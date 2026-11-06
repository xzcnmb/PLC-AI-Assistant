using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlcMcp.Engineering.Workers.Keyence;

// KEYENCE KV STUDIO "generate + deploy" orchestrator (C# side).
//
// One call drives five gates:
//   1. mnm       - offline ST -> MNM generation via KeyenceMnmGenerator (pure local, no IDE);
//   2. import    - PowerShell script imports the .mnm into the project (gate "import");
//   3. variables - PowerShell script registers the variable table (gate "variables");
//   4. compile   - PowerShell script runs the KV STUDIO conversion and parses its result (gate "compile");
//   5. persist   - project save + on-disk fingerprint check (gate "persist").
//
// Everything that actually touches KV STUDIO lives in scripts/kv_studio_deploy.ps1
// (owned separately; frozen contract below). This class only: builds the MNM, spawns
// powershell.exe with a strict ArgumentList (no command-line string concatenation, so
// paths with spaces and quotes cannot inject), waits with a bounded timeout, parses
// kv-deploy-result.json plus the machine-readable stdout summary line, maps the exit
// code, and cross-checks persistence with an independent directory fingerprint
// (KeyenceProjectSnapshot-style). The script result is authoritative; fingerprint
// disagreements are surfaced as messages, never silently "fixed".
//
// Frozen script contract (both sides implement exactly this):
//   powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\scripts\kv_studio_deploy.ps1
//     -ProjectDir <abs .kpr dir> -MnmPath <abs .mnm> -ModuleName <name>
//     [-VariablesTsv <abs tsv>] [-KvsExe <path>] [-KvUia <path>]
//     [-WaitCompileSeconds <int>] [-OutDir <abs dir>] [-DryRun]
//   exit codes: 0 ok, 1 args, 2 environment, 3 start/bind, 10 import FAIL,
//               20 variables FAIL, 30 compile NG, 40 persist FAIL, 50 timeout, other fatal;
//   <OutDir>\kv-deploy-result.json carries gates/compile/importDetail/persist/messages;
//   stdout ends with: "KV-DEPLOY-RESULT <code> import=I variables=V compile=C persist=P".

/// <summary>Normalized outcome of one deploy gate (null on the report = the gate never ran/was not reported).</summary>
public enum KeyenceGateOutcome
{
    Pass,
    Fail,
    Skip
}

/// <summary>
/// Server-side options for the gated KV deploy pipeline (--keyence-deploy). Non-null in
/// ServerGovernanceServices only when the operator explicitly enabled the tool; paths
/// here are defaults that individual tool calls may override.
/// </summary>
public sealed record KeyenceDeployOptions
{
    /// <summary>Absolute Kvs.exe path (default: KeyenceInstallationDoctor.DefaultIdeExePath).</summary>
    public string? KvsExe { get; init; }

    /// <summary>Absolute kv-uia.exe path forwarded to the deployment script.</summary>
    public string? KvUia { get; init; }

    /// <summary>Absolute path of scripts\kv_studio_deploy.ps1 (default: discovered from the app base).</summary>
    public string? ScriptPath { get; init; }
}

/// <summary>One variable-table entry for the gate-3 TSV (name &lt;TAB&gt; dataType).</summary>
public sealed record KeyenceDeployVariable(string Name, string DataType);

/// <summary>Validated request for one "generate + deploy" run.</summary>
public sealed record KeyenceDeployRequest
{
    /// <summary>ST executable statements only (same red lines as KeyenceMnmGenerator).</summary>
    public required string Source { get; init; }

    /// <summary>Module name; rules identical to KeyenceMnmGenerator (^[A-Za-z_][A-Za-z0-9_]{0,47}$).</summary>
    public required string ModuleName { get; init; }

    /// <summary>DEVICE code from the target project's own MNM export (0..255).</summary>
    public required int DeviceCode { get; init; }

    /// <summary>"scan" or "function_block".</summary>
    public string Category { get; init; } = "scan";

    /// <summary>Absolute path of the .kpr project directory to operate on.</summary>
    public required string ProjectDir { get; init; }

    /// <summary>Optional variable-table rows registered by the script before compiling.</summary>
    public IReadOnlyList<KeyenceDeployVariable>? Variables { get; init; }

    /// <summary>Kvs.exe path override (default: the doctor's known 12G location).</summary>
    public string? KvsExe { get; init; }

    /// <summary>kv-uia.exe path override forwarded to the script.</summary>
    public string? KvUia { get; init; }

    /// <summary>Seconds the script waits for the conversion result (contract default 40).</summary>
    public int WaitCompileSeconds { get; init; } = 40;

    /// <summary>Script/C# shared output directory; default %TEMP%\kv-deploy.</summary>
    public string? OutDir { get; init; }

    /// <summary>Dry run: the script exercises discovery paths but performs no IDE action.</summary>
    public bool DryRun { get; init; }

    /// <summary>Deployment script; default: scripts\kv_studio_deploy.ps1 found upward from the app base.</summary>
    public string? ScriptPath { get; init; }

    /// <summary>powershell executable; default "powershell.exe".</summary>
    public string? PowerShellExe { get; init; }

    /// <summary>
    /// Rejects invalid input before any process starts: absolute project directory that is not
    /// inside the KV STUDIO installation tree, module-name/device-code/category/variable/wait checks.
    /// Throws ArgumentException for bad values and InvalidOperationException for impossible paths.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Source))
        {
            throw new ArgumentException("Parameter 'source' is required and cannot be empty.");
        }

        if (Source.Length > 1_048_576)
        {
            throw new ArgumentException("Inline ST source is too large (maximum 1 MiB characters).");
        }

        if (string.IsNullOrWhiteSpace(ModuleName))
        {
            throw new ArgumentException("Parameter 'moduleName' is required and cannot be empty.");
        }

        if (DeviceCode is < 0 or > 255)
        {
            throw new ArgumentException("Parameter 'deviceCode' must be 0..255 (take it from the target project's own MNM export).");
        }

        if (Category is not ("scan" or "function_block"))
        {
            throw new ArgumentException($"Parameter 'category' must be 'scan' or 'function_block', got '{Category}'.");
        }

        if (string.IsNullOrWhiteSpace(ProjectDir))
        {
            throw new ArgumentException("Parameter 'projectDir' is required and must be an absolute path.");
        }

        if (!Path.IsPathRooted(ProjectDir))
        {
            throw new ArgumentException($"Parameter 'projectDir' must be an absolute path, got '{ProjectDir}'.");
        }

        string projectFull = NormalizeAbsolute(ProjectDir, nameof(ProjectDir));
        string ideDir = Path.GetDirectoryName(KvsExe ?? KeyenceInstallationDoctor.DefaultIdeExePath) ?? string.Empty;
        if (IsWithin(ideDir, projectFull))
        {
            throw new InvalidOperationException(
                "Parameter 'projectDir' must not live inside the KV STUDIO installation directory " +
                "(the IDE writes BackUP/Temp files next to the project). Operate on a project copy.");
        }

        if (Variables is not null)
        {
            foreach (var variable in Variables)
            {
                if (variable is null || string.IsNullOrWhiteSpace(variable.Name) || string.IsNullOrWhiteSpace(variable.DataType))
                {
                    throw new ArgumentException("Every entry of 'variables' needs a non-empty name and dataType.");
                }

                if (variable.Name.Contains('\t') || variable.Name.Contains('\n') || variable.Name.Contains('\r')
                    || variable.DataType.Contains('\t') || variable.DataType.Contains('\n') || variable.DataType.Contains('\r'))
                {
                    throw new ArgumentException($"Variable '{variable.Name}' must not contain tab or newline characters (TSV injection).");
                }
            }
        }

        if (WaitCompileSeconds is < 5 or > 600)
        {
            throw new ArgumentException("Parameter 'waitCompileSeconds' must be 5..600.");
        }

        if (OutDir is not null && !Path.IsPathRooted(OutDir))
        {
            throw new ArgumentException($"Parameter 'outDir' must be an absolute path, got '{OutDir}'.");
        }

        if (ScriptPath is not null && !Path.IsPathRooted(ScriptPath))
        {
            throw new ArgumentException($"Parameter 'scriptPath' must be an absolute path, got '{ScriptPath}'.");
        }
    }

    internal static string NormalizeAbsolute(string path, string paramName)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"Parameter '{paramName}' is not a valid path: '{path}' ({ex.Message})");
        }
    }

    internal static bool IsWithin(string root, string candidate)
    {
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        string fullRoot;
        try
        {
            fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch
        {
            return false;
        }

        string fullCandidate = Path.TrimEndingDirectorySeparator(candidate);
        return string.Equals(fullRoot, fullCandidate, StringComparison.OrdinalIgnoreCase)
               || fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || fullCandidate.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Parsed compile-error row of the KV STUDIO "转换结果" text.</summary>
public sealed record KeyenceCompileError(
    string? Module,
    int? Line,
    int? StLine,
    int? Code,
    string? Kind,
    string? Message,
    string? Symbol);

/// <summary>Compact compile verdict carried on the report.</summary>
public sealed record KeyenceCompileSummary(
    string? State,
    int? ErrorCount,
    int? WarningCount,
    string? RawHeader,
    KeyenceCompileError? FirstError);

/// <summary>Five-gate deploy report; Passed=true only when the exit code is 0 and every gate reported PASS/SKIP.</summary>
public sealed record KeyenceDeployReport(
    bool Passed,
    IReadOnlyDictionary<string, string?> Stages,
    string? ProjectDir,
    string? ModuleName,
    string? OutputDir,
    string? MnmPath,
    KeyenceCompileSummary? Compile,
    IReadOnlyList<KeyenceCompileError> Errors,
    IReadOnlyList<string> Messages,
    IReadOnlyList<string> Warnings,
    string? FatalError,
    int? ExitCode,
    object? Detail)
{
    /// <summary>Gate names in execution order (stage 1 is the offline MNM generation).</summary>
    public static readonly string[] GateNames = ["mnm", "import", "variables", "compile", "persist"];

    public string? OutcomeOf(string gate) =>
        Stages.TryGetValue(gate, out var value) ? value : null;
}

/// <summary>Typed view of the script's kv-deploy-result.json (nulls = field absent from the document).</summary>
public sealed record KeyenceDeployScriptResult(
    string? ScriptVersion,
    string? ProjectDir,
    string? ModuleName,
    bool? DryRun,
    IReadOnlyDictionary<string, KeyenceGateOutcome?> Gates,
    KeyenceScriptCompile? Compile,
    KeyenceScriptImportDetail? ImportDetail,
    KeyenceScriptPersist? Persist,
    IReadOnlyList<string> Messages,
    IReadOnlyList<string> MalformedFields);

/// <summary>compile object of the contract JSON.</summary>
public sealed record KeyenceScriptCompile(
    string? State,
    int? ErrorCount,
    int? WarningCount,
    string? RawHeader,
    IReadOnlyList<KeyenceCompileError> Errors,
    string? RawText);

/// <summary>importDetail object of the contract JSON.</summary>
public sealed record KeyenceScriptImportDetail(
    bool? ModuleFoundInTree,
    IReadOnlyList<string> Dialogs);

/// <summary>persist object of the contract JSON.</summary>
public sealed record KeyenceScriptPersist(
    string? FingerprintBefore,
    string? FingerprintAfter,
    bool? Changed);

/// <summary>Observed outcome of one script invocation.</summary>
public sealed record KvDeployRunResult(
    int? ExitCode,
    string Stdout,
    string? ResultJsonPath,
    bool TimedOut);

/// <summary>
/// Process-level seam for the PowerShell deployment script. Injectable so the whole
/// orchestration is unit-testable offline (FakeRunner); <see cref="ProcessKvDeployRunner"/>
/// is the production implementation.
/// </summary>
public interface IKvDeployRunner
{
    /// <summary>
    /// Runs <paramref name="arguments"/> under <paramref name="executable"/> with a hard timeout.
    /// On timeout the process (and tree) is killed and TimedOut=true with ExitCode=null.
    /// Implementations must never see a concatenated command line: arguments go through
    /// ProcessStartInfo.ArgumentList.
    /// </summary>
    Task<KvDeployRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string resultJsonPath,
        CancellationToken cancellationToken);
}

/// <summary>Production runner: powershell.exe child process, redirected stdout, bounded timeout, tree kill.</summary>
public sealed class ProcessKvDeployRunner : IKvDeployRunner
{
    private const int MaxEchoBytes = 8192;

    public async Task<KvDeployRunResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string resultJsonPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Injection red line: array-passed arguments only, never a command-line string.
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"Could not start '{executable}': {ex.Message}", ex);
        }

        var stdoutTask = ReadCappedAsync(process.StandardOutput, MaxEchoBytes, cancellationToken);
        var stderrTask = ReadCappedAsync(process.StandardError, MaxEchoBytes, cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        bool exited;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            exited = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Contract exit code 50 means the script's own timeout; a wall-clock overrun here
            // means the child is wedged - kill the tree and report the gates as unresolved.
            KillTree(process);
            exited = false;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            KillTree(process);
            cancellationToken.ThrowIfCancellationRequested();
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        int? exitCode = exited ? process.ExitCode : null;
        string? resultPath = File.Exists(resultJsonPath) ? resultJsonPath : null;

        string echo = stdout;
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            echo += "\n[stderr] " + stderr;
        }

        return new KvDeployRunResult(exitCode, echo, resultPath, TimedOut: !exited);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between HasExited and Kill - fine.
        }
        catch (Exception)
        {
            // Best-effort cleanup only; the caller still reports the timeout honestly.
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, int maxChars, CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        var chunk = new char[1024];
        int total = 0;
        bool truncated = false;
        try
        {
            int count;
            while ((count = await reader.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (truncated)
                {
                    continue; // keep draining so the child never blocks on a full pipe
                }

                if (total + count > maxChars)
                {
                    buffer.Append(chunk, 0, Math.Max(0, maxChars - total));
                    buffer.Append("...[truncated]");
                    truncated = true;
                    total = maxChars;
                    continue;
                }

                buffer.Append(chunk, 0, count);
                total += count;
            }
        }
        catch (Exception)
        {
            // A killed process can leave pipes in a broken state; whatever was read is kept.
        }

        return buffer.ToString();
    }
}

/// <summary>Contract exit-code semantics.</summary>
public sealed record KeyenceExitCodeMeaning(string Label, string? Gate, bool Fatal, string Description);

/// <summary>
/// Offline orchestrator: gate 1 generation plus gates 2-5 via the PowerShell deployment script.
/// Never talks to KV STUDIO directly; every IDE-side action belongs to the script contract.
/// </summary>
public sealed class KeyenceDeployProgram
{
    /// <summary>Contract file name written by the script under OutDir.</summary>
    public const string ResultFileName = "kv-deploy-result.json";

    /// <summary>Script contract version this class was written against.</summary>
    public const string ExpectedScriptVersion = "1.0.0";

    /// <summary>Relative script location inside the repository, used for default discovery.</summary>
    public const string DefaultScriptRelativePath = @"scripts\kv_studio_deploy.ps1";

    private static readonly string[] ScriptGates = ["import", "variables", "compile", "persist"];

    private static readonly Regex SummaryLinePattern = new(
        @"^KV-DEPLOY-RESULT\s+(-?\d+)\s+import=(?<import>\w+)\s+variables=(?<variables>\w+)\s+compile=(?<compile>\w+)\s+persist=(?<persist>\w+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Compile-text row shapes observed on KV STUDIO 12.42 (docs/HANDOFF-2026-11-03-keyence.md):
    //   Module(行 00003)(ST行 0002)[错误 1232]:"X": detail [symbol]
    //   Module(1)(行 00005)[警告 61]: detail
    //   [错误709]：...  /  [错误214]：...[未登记变量名]   (no module prefix at all)
    private static readonly Regex CompileLinePattern = new(
        @"^(?<module>[A-Za-z_][\w.$]*(?:\(\d+\))?)?\s*" +
        @"(?:\(\s*(?:行|line)\s*(?<line>\d+)\s*\))?" +
        @"(?:\(\s*ST\s*(?:行|line)\s*(?<stline>\d+)\s*\))?" +
        @"\s*\[\s*(?<kind>错误|警告|error|warning)\s*(?<code>\d+)\s*\]" +
        @"\s*(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LeadingSymbolPattern = new(
        @"^\s*[:：]?\s*""(?<symbol>[^""\r\n]+)""\s*[:：]?\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IKvDeployRunner _runner;

    public KeyenceDeployProgram(IKvDeployRunner? runner = null)
    {
        _runner = runner ?? new ProcessKvDeployRunner();
    }

    /// <summary>Default script path: walks up from the app base directory looking for scripts\kv_studio_deploy.ps1.</summary>
    public static string? FindDefaultScriptPath()
    {
        var roots = new List<string>();
        try
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && roots.Count < 8; dir = dir.Parent)
            {
                roots.Add(dir.FullName);
            }
        }
        catch
        {
            // Base-directory walk is best effort.
        }

        foreach (var root in roots)
        {
            string candidate = Path.Combine(root, DefaultScriptRelativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Default output directory from the contract (%TEMP%\kv-deploy).</summary>
    public static string DefaultOutputDirectory() =>
        Path.Combine(Path.GetTempPath(), "kv-deploy");

    /// <summary>
    /// Runs the full five-gate pipeline. Input validation throws (ArgumentException /
    /// InvalidOperationException); every flow failure comes back as a report instead.
    /// </summary>
    public async Task<KeyenceDeployReport> RunAsync(KeyenceDeployRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        string projectDir = KeyenceDeployRequest.NormalizeAbsolute(request.ProjectDir, nameof(request.ProjectDir));
        string outDir = KeyenceDeployRequest.NormalizeAbsolute(request.OutDir ?? DefaultOutputDirectory(), nameof(request.OutDir));
        string moduleName = request.ModuleName;

        var stages = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["mnm"] = null,
            ["import"] = null,
            ["variables"] = null,
            ["compile"] = null,
            ["persist"] = null
        };
        var messages = new List<string>();
        var warnings = new List<string>();
        var errors = new List<KeyenceCompileError>();

        Directory.CreateDirectory(outDir);

        // ---- Gate 1: static precheck + MNM generation (pure local; on failure the script is never called) ----
        bool functionBlock = string.Equals(request.Category, "function_block", StringComparison.OrdinalIgnoreCase);
        var mnm = KeyenceMnmGenerator.Generate(request.Source, moduleName, request.DeviceCode, functionBlock);
        if (!mnm.Ok)
        {
            stages["mnm"] = "FAIL";
            return BuildReport(
                passed: false, stages, projectDir, moduleName, outDir, mnmPath: null,
                compile: null, errors, messages, warnings,
                fatalError: "KV_DEPLOY_MNM_GENERATION_FAILED: " + mnm.Error,
                exitCode: null,
                detail: new { gate = "mnm", generatorError = mnm.Error });
        }

        foreach (var warning in mnm.Warnings)
        {
            warnings.Add("[mnm] " + warning);
        }

        string mnmPath = Path.Combine(outDir, moduleName + ".mnm");
        try
        {
            await File.WriteAllBytesAsync(mnmPath, mnm.Payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stages["mnm"] = "FAIL";
            return BuildReport(
                false, stages, projectDir, moduleName, outDir, mnmPath,
                null, errors, messages, warnings,
                fatalError: $"KV_DEPLOY_MNM_WRITE_FAILED: could not write '{mnmPath}': {ex.Message}",
                exitCode: null,
                detail: new { gate = "mnm" });
        }

        stages["mnm"] = "PASS";

        // ---- Environment prechecks before spawning anything ----
        string scriptPath = KeyenceDeployRequest.NormalizeAbsolute(
            request.ScriptPath ?? FindDefaultScriptPath() ?? Path.Combine(AppContext.BaseDirectory, DefaultScriptRelativePath),
            nameof(request.ScriptPath));
        if (!File.Exists(scriptPath))
        {
            stages["import"] = "FAIL";
            messages.Add("部署脚本缺失: KV STUDIO deployment script is missing at " + scriptPath +
                         " (expected at <repo>\\" + DefaultScriptRelativePath + ").");
            return BuildReport(
                false, stages, projectDir, moduleName, outDir, mnmPath,
                null, errors, messages, warnings,
                fatalError: "KV_DEPLOY_SCRIPT_MISSING",
                exitCode: null,
                detail: new { gate = "import", scriptPath });
        }

        if (!Directory.Exists(projectDir))
        {
            stages["import"] = "FAIL";
            messages.Add($"Project directory does not exist: '{projectDir}' (environment gate 2 on the script side).");
            return BuildReport(
                false, stages, projectDir, moduleName, outDir, mnmPath,
                null, errors, messages, warnings,
                fatalError: "KV_DEPLOY_PROJECT_DIR_MISSING",
                exitCode: 2,
                detail: new { gate = "environment", projectDir });
        }

        string kvsExe = KeyenceDeployRequest.NormalizeAbsolute(request.KvsExe ?? KeyenceInstallationDoctor.DefaultIdeExePath, nameof(request.KvsExe));
        if (!request.DryRun && !File.Exists(kvsExe))
        {
            stages["import"] = "FAIL";
            messages.Add($"Kvs.exe not found at '{kvsExe}' (environment gate 2; set --keyence-kvs-exe or the kvsExe parameter).");
            return BuildReport(
                false, stages, projectDir, moduleName, outDir, mnmPath,
                null, errors, messages, warnings,
                fatalError: "KV_DEPLOY_KVS_MISSING",
                exitCode: 2,
                detail: new { gate = "environment", kvsExe });
        }

        // Independent before-fingerprint for the gate-5 cross-check (advisory; the script stays authoritative).
        string? fingerprintBefore = null;
        bool fingerprintPartial = false;
        try
        {
            fingerprintBefore = DirectoryFingerprint(projectDir, out fingerprintPartial);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            messages.Add($"Directory fingerprint (before) unavailable: {ex.Message}");
        }

        // ---- Gates 2-5: run the contract script ----
        string resultPath = Path.Combine(outDir, ResultFileName);
        TryDelete(resultPath);
        string? tsvPath = WriteVariablesTsv(request.Variables, outDir, moduleName, messages);
        var invocation = BuildInvocation(request, scriptPath, mnmPath, outDir, tsvPath);
        TimeSpan timeout = ComputeProcessTimeout(request.WaitCompileSeconds);

        KvDeployRunResult run;
        try
        {
            run = await _runner.RunAsync(
                invocation.Executable, invocation.Arguments, timeout, resultPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            messages.Add(ex.Message);
            return BuildReport(
                false, stages, projectDir, moduleName, outDir, mnmPath,
                null, errors, messages, warnings,
                fatalError: "KV_DEPLOY_PROCESS_START_FAILED",
                exitCode: null,
                detail: new { gate = "environment", executable = invocation.Executable });
        }

        KeyenceDeployScriptResult? result = null;
        if (run.ResultJsonPath is not null)
        {
            try
            {
                result = ParseResultJson(await File.ReadAllTextAsync(run.ResultJsonPath, cancellationToken).ConfigureAwait(false));
                foreach (var malformed in result.MalformedFields)
                {
                    messages.Add("kv-deploy-result.json: " + malformed);
                }
            }
            catch (JsonException ex)
            {
                messages.Add($"kv-deploy-result.json is not valid JSON: {ex.Message}");
            }
        }
        else if (!run.TimedOut)
        {
            messages.Add("The script produced no kv-deploy-result.json (contract file missing); " +
                         "gate results below are taken from the stdout summary line and the exit code only.");
        }

        var summaryStages = ParseSummaryLine(run.Stdout);

        // Script-side gate outcomes: JSON wins per gate, stdout summary fills the gaps.
        ApplyStage(result, "import", stages, summaryStages, 0);
        ApplyStage(result, "variables", stages, summaryStages, 1);
        ApplyStage(result, "compile", stages, summaryStages, 2);
        ApplyStage(result, "persist", stages, summaryStages, 3);

        string? fatalError = null;
        if (run.TimedOut)
        {
            // Unreported gates stay null (= undetermined): the kill means we never learned the
            // verdict. The timeout FatalError is the honest failure reason for the whole chain.
            fatalError = $"KV_DEPLOY_TIMEOUT: the deployment script exceeded the {timeout.TotalSeconds:0}s wall-clock budget and was killed; " +
                         "gates 2-5 are undetermined (a script-side timeout would have answered exit code 50).";
            messages.Add(fatalError);
        }

        int? exitCode = run.ExitCode;
        if (exitCode.HasValue)
        {
            var meaning = MapExitCode(exitCode.Value);
            ReconcileExitCode(exitCode.Value, meaning, result, stages, messages);
            if (meaning.Fatal && meaning.Label != "OK")
            {
                fatalError ??= $"KV_DEPLOY_{meaning.Label}: exit code {exitCode.Value} ({meaning.Description})";
            }
        }
        else if (!run.TimedOut)
        {
            fatalError ??= "KV_DEPLOY_NO_EXIT_CODE: the script process ended but no exit code was observable.";
        }

        // ---- Compile detail (gate 4 evidence) ----
        KeyenceCompileSummary? compile = null;
        if (result?.Compile is { } scriptCompile)
        {
            var scriptErrors = scriptCompile.Errors.Count > 0
                ? scriptCompile.Errors
                : ParseCompileErrors(scriptCompile.RawText);
            errors.AddRange(scriptErrors);

            string? state = scriptCompile.State;
            string? header = scriptCompile.RawHeader;
            if (state is null && header is not null)
            {
                string normalized = header.Trim();
                state = normalized.Contains("OK", StringComparison.OrdinalIgnoreCase) ? "OK"
                      : normalized.Contains("NG", StringComparison.OrdinalIgnoreCase) ? "NG"
                      : null;
            }

            int? errorCount = scriptCompile.ErrorCount
                              ?? (scriptErrors.Count > 0 ? scriptErrors.Count(e => IsErrorKind(e.Kind)) : null);
            int? warningCount = scriptCompile.WarningCount
                               ?? (scriptErrors.Count > 0 ? scriptErrors.Count(e => !IsErrorKind(e.Kind)) : null);
            compile = new KeyenceCompileSummary(
                state,
                errorCount,
                warningCount,
                header,
                scriptErrors.FirstOrDefault(e => IsErrorKind(e.Kind)));

            if (state is null && scriptCompile.RawText is not null)
            {
                messages.Add("compile.state/rawHeader missing in kv-deploy-result.json; inferred from raw text only.");
            }
        }

        // ---- Gate 5 cross-check: independent directory fingerprint after the run ----
        bool? changedCross = null;
        if (fingerprintBefore is not null)
        {
            try
            {
                string fingerprintAfter = DirectoryFingerprint(projectDir, out bool partialAfter);
                changedCross = !string.Equals(fingerprintBefore, fingerprintAfter, StringComparison.Ordinal);
                if (fingerprintPartial || partialAfter)
                {
                    messages.Add("Directory fingerprint is partial (file budget exceeded); cross-check degraded to advisory.");
                }

                if (result?.Persist?.Changed is bool scriptChanged && stages["persist"] == "PASS")
                {
                    messages.Add(scriptChanged == changedCross
                        ? "Fingerprint cross-check agrees with the script persist gate."
                        : $"Fingerprint cross-check disagrees with the script persist gate: script reported changed={scriptChanged}, " +
                          $"directory fingerprint reports changed={changedCross} (the script result stays authoritative).");
                }
                else if (!request.DryRun)
                {
                    messages.Add($"Independent fingerprint: project {(changedCross == true ? "changed" : "UNCHANGED")} on disk after the run.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                messages.Add($"Directory fingerprint cross-check unavailable: {ex.Message}");
            }
        }

        bool allGatesDecided = stages.Values.All(value => value is not null);
        bool passed = exitCode == 0
                      && !run.TimedOut
                      && fatalError is null
                      && allGatesDecided
                      && stages["import"] is "PASS" or "SKIP"
                      && stages["variables"] is "PASS" or "SKIP"
                      && stages["compile"] is "PASS" or "SKIP"
                      && stages["persist"] is "PASS" or "SKIP";

        foreach (var message in result?.Messages ?? [])
        {
            messages.Add("[script] " + message);
        }

        // ---- Detail: lean on success, full diagnostics on failure (_slim_report style) ----
        object detail = passed
            ? new
            {
                mnmPath,
                resultJson = run.ResultJsonPath,
                scriptVersion = result?.ScriptVersion,
                dryRun = result?.DryRun ?? request.DryRun,
                fingerprintChanged = changedCross
            }
            : new
            {
                mnmPath,
                resultJson = run.ResultJsonPath,
                scriptVersion = result?.ScriptVersion,
                exitMeaning = exitCode.HasValue ? MapExitCode(exitCode.Value).Description : null,
                importDetail = result?.ImportDetail,
                persist = result?.Persist,
                fingerprintBefore = ShortHash(fingerprintBefore),
                fingerprintChanged = changedCross,
                scriptEcho = Truncate(run.Stdout, 2048)
            };

        return BuildReport(passed, stages, projectDir, moduleName, outDir, mnmPath,
            compile, errors, messages, warnings, fatalError, exitCode, detail);
    }

    private static KeyenceDeployReport BuildReport(
        bool passed,
        Dictionary<string, string?> stages,
        string? projectDir,
        string? moduleName,
        string? outputDir,
        string? mnmPath,
        KeyenceCompileSummary? compile,
        IReadOnlyList<KeyenceCompileError> errors,
        IReadOnlyList<string> messages,
        IReadOnlyList<string> warnings,
        string? fatalError,
        int? exitCode,
        object? detail) =>
        new(
            Passed: passed,
            Stages: new Dictionary<string, string?>(stages, StringComparer.Ordinal),
            ProjectDir: projectDir,
            ModuleName: moduleName,
            OutputDir: outputDir,
            MnmPath: mnmPath,
            Compile: compile,
            Errors: errors,
            Messages: messages,
            Warnings: warnings,
            FatalError: fatalError,
            ExitCode: exitCode,
            Detail: detail);

    /// <summary>Process wall-clock budget: the script's own compile wait plus generous setup slack.</summary>
    public static TimeSpan ComputeProcessTimeout(int waitCompileSeconds) =>
        TimeSpan.FromSeconds(Math.Clamp(waitCompileSeconds + 260, 300, 3600));

    /// <summary>Absolute-resolved invocation: powershell.exe plus the whole argument vector.</summary>
    public sealed record KvDeployInvocation(string Executable, IReadOnlyList<string> Arguments);

    public static KvDeployInvocation BuildInvocation(
        KeyenceDeployRequest request,
        string scriptPath,
        string mnmPath,
        string outDir,
        string? variablesTsvPath)
    {
        var arguments = new List<string>
        {
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", KeyenceDeployRequest.NormalizeAbsolute(scriptPath, nameof(scriptPath))
        };
        arguments.AddRange(BuildArguments(request, mnmPath, outDir, variablesTsvPath));
        return new KvDeployInvocation(request.PowerShellExe ?? "powershell.exe", arguments);
    }

    /// <summary>
    /// Script-side parameter vector (contract shape). Every path is GetFullPath-verified and
    /// passed as its own list element; nothing is ever string-interpolated into a command line.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(KeyenceDeployRequest request, string mnmPath, string outDir) =>
        BuildArguments(request, mnmPath, outDir, variablesTsvPath: null);

    public static IReadOnlyList<string> BuildArguments(
        KeyenceDeployRequest request,
        string mnmPath,
        string outDir,
        string? variablesTsvPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        mnmPath = KeyenceDeployRequest.NormalizeAbsolute(mnmPath, nameof(mnmPath));
        outDir = KeyenceDeployRequest.NormalizeAbsolute(outDir, nameof(outDir));

        var arguments = new List<string>
        {
            "-ProjectDir", KeyenceDeployRequest.NormalizeAbsolute(request.ProjectDir, nameof(request.ProjectDir)),
            "-MnmPath", mnmPath,
            "-ModuleName", request.ModuleName
        };

        if (variablesTsvPath is not null)
        {
            arguments.Add("-VariablesTsv");
            arguments.Add(KeyenceDeployRequest.NormalizeAbsolute(variablesTsvPath, nameof(variablesTsvPath)));
        }

        if (!string.IsNullOrWhiteSpace(request.KvsExe))
        {
            arguments.Add("-KvsExe");
            arguments.Add(KeyenceDeployRequest.NormalizeAbsolute(request.KvsExe, nameof(request.KvsExe)));
        }

        if (!string.IsNullOrWhiteSpace(request.KvUia))
        {
            arguments.Add("-KvUia");
            arguments.Add(KeyenceDeployRequest.NormalizeAbsolute(request.KvUia, nameof(request.KvUia)));
        }

        arguments.Add("-WaitCompileSeconds");
        arguments.Add(request.WaitCompileSeconds.ToString(CultureInfo.InvariantCulture));

        arguments.Add("-OutDir");
        arguments.Add(outDir);

        if (request.DryRun)
        {
            arguments.Add("-DryRun");
        }

        return arguments;
    }

    /// <summary>Writes the optional gate-3 TSV (name&lt;TAB&gt;dataType rows, UTF-8) next to the MNM.</summary>
    private static string? WriteVariablesTsv(
        IReadOnlyList<KeyenceDeployVariable>? variables,
        string outDir,
        string moduleName,
        List<string> messages)
    {
        if (variables is null || variables.Count == 0)
        {
            return null;
        }

        string tsvPath = Path.Combine(outDir, moduleName + "-variables.tsv");
        try
        {
            var sb = new StringBuilder();
            foreach (var variable in variables)
            {
                sb.Append(variable.Name).Append('\t').Append(variable.DataType).Append('\n');
            }

            File.WriteAllText(tsvPath, sb.ToString(), new UTF8Encoding(false));
            return tsvPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            messages.Add($"Variable TSV could not be written to '{tsvPath}': {ex.Message} (gate 3 will run without it).");
            return null;
        }
    }

    /// <summary>
    /// Parses the trailing machine-readable stdout line
    /// "KV-DEPLOY-RESULT &lt;code&gt; import=I variables=V compile=C persist=P"; the four values are
    /// PASS/FAIL/SKIP. Returns the empty list when no well-formed summary line is present.
    /// </summary>
    public static IReadOnlyList<string> ParseSummaryLine(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        foreach (var rawLine in stdout.Split('\n').Reverse())
        {
            var match = SummaryLinePattern.Match(rawLine.Trim());
            if (match.Success)
            {
                return
                [
                    NormalizeStage(match.Groups["import"].Value) ?? "FAIL",
                    NormalizeStage(match.Groups["variables"].Value) ?? "FAIL",
                    NormalizeStage(match.Groups["compile"].Value) ?? "FAIL",
                    NormalizeStage(match.Groups["persist"].Value) ?? "FAIL"
                ];
            }
        }

        return [];
    }

    /// <summary>Parses one gate token (PASS/FAIL/SKIP, case-insensitive); unknown values become null.</summary>
    public static string? NormalizeStage(string? token) => token?.Trim().ToUpperInvariant() switch
    {
        "PASS" => "PASS",
        "FAIL" => "FAIL",
        "SKIP" => "SKIP",
        _ => null
    };

    private static void ApplyStage(
        KeyenceDeployScriptResult? result,
        string gate,
        Dictionary<string, string?> stages,
        IReadOnlyList<string> summaryStages,
        int summaryIndex)
    {
        string? outcome = null;
        if (result is not null && result.Gates.TryGetValue(gate, out var parsed))
        {
            outcome = parsed switch
            {
                KeyenceGateOutcome.Pass => "PASS",
                KeyenceGateOutcome.Fail => "FAIL",
                KeyenceGateOutcome.Skip => "SKIP",
                _ => null
            };
        }

        if (outcome is null && summaryIndex < summaryStages.Count)
        {
            outcome = summaryStages[summaryIndex];
        }

        stages[gate] = outcome;
    }

    /// <summary>Full contract exit-code table.</summary>
    public static KeyenceExitCodeMeaning MapExitCode(int code) => code switch
    {
        0 => new("OK", null, false, "Deployment completed successfully."),
        1 => new("ARGS", null, true, "Parameter error (bad/missing script parameters)."),
        2 => new("ENVIRONMENT", null, true, "Environment missing: Kvs.exe, kv-uia or the project directory is not present."),
        3 => new("START_BIND", "import", true, "KV STUDIO could not be started or bound."),
        10 => new("IMPORT_FAIL", "import", true, "MNM import failed (gate import)."),
        20 => new("VARIABLES_FAIL", "variables", true, "Variable registration failed (gate variables)."),
        30 => new("COMPILE_NG", "compile", true, "Conversion/compile reported NG (gate compile)."),
        40 => new("PERSIST_FAIL", "persist", true, "Save/persist to disk failed (gate persist)."),
        50 => new("TIMEOUT", null, true, "The script hit its own timeout before the gates closed."),
        _ => new("FATAL", null, true, $"Unexpected fatal exit code {code}.")
    };

    /// <summary>Convenience overload: the exit-code meaning as it applies to an assembled report.</summary>
    public static KeyenceExitCodeMeaning MapExitCode(int code, KeyenceDeployReport report)
    {
        var meaning = MapExitCode(code);
        if (code == 0 && report.Stages.Values.Any(s => s is "FAIL" or null))
        {
            return meaning with { Fatal = true, Description = meaning.Description + " The gate report disagrees; trust the report." };
        }

        return meaning;
    }

    /// <summary>
    /// Uses the exit code to reconcile stage gaps: a named failure code marks its gate and the
    /// later script gates FAIL where neither JSON nor summary reported them; exit 0 with missing
    /// gates marks them FAIL as a contract violation.
    /// </summary>
    public static void ReconcileExitCode(
        int code,
        KeyenceExitCodeMeaning meaning,
        KeyenceDeployScriptResult? result,
        Dictionary<string, string?> stages,
        List<string> messages)
    {
        if (code == 0)
        {
            foreach (var gate in ScriptGates)
            {
                if (stages[gate] is null)
                {
                    stages[gate] = "FAIL";
                    messages.Add($"Exit code 0 but gate '{gate}' was never reported; treated as FAIL (contract violated by the script).");
                }
            }

            return;
        }

        if (meaning.Gate is not null)
        {
            int idx = Array.IndexOf(ScriptGates, meaning.Gate);
            for (int i = idx; i < ScriptGates.Length; i++)
            {
                if (stages[ScriptGates[i]] is null)
                {
                    stages[ScriptGates[i]] = "FAIL";
                    if (i == idx)
                    {
                        messages.Add($"Exit code {code} names gate '{meaning.Gate}' ({meaning.Description}); gate marked FAIL.");
                    }
                }
            }
        }

        if (code is not (1 or 2 or 3 or 10 or 20 or 30 or 40 or 50))
        {
            messages.Add($"Unexpected script exit code {code}: {meaning.Description}");
        }
        else if (meaning.Gate is null && meaning.Fatal)
        {
            messages.Add($"Script failed before/without a gate verdict: {meaning.Description}");
        }
    }

    /// <summary>
    /// Parses the kv-deploy-result.json contract document. Unknown/missing fields stay null and
    /// are collected in <see cref="KeyenceDeployScriptResult.MalformedFields"/>; malformed JSON
    /// throws JsonException (the caller reports it as a contract violation).
    /// </summary>
    public static KeyenceDeployScriptResult ParseResultJson(string json)
    {
        using var document = JsonDocument.Parse(json ?? throw new JsonException("Empty result document."));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("kv-deploy-result.json root must be an object.");
        }

        var malformed = new List<string>();

        string? scriptVersion = OptionalString(root, "scriptVersion");
        if (scriptVersion is null)
        {
            malformed.Add("missing 'scriptVersion'.");
        }
        else if (scriptVersion != ExpectedScriptVersion)
        {
            malformed.Add($"'scriptVersion' {scriptVersion} differs from the expected contract {ExpectedScriptVersion}.");
        }

        var gates = new Dictionary<string, KeyenceGateOutcome?>(StringComparer.Ordinal);
        if (root.TryGetProperty("gates", out var gatesElement) && gatesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in gatesElement.EnumerateObject())
            {
                string? raw = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                string? normalized = NormalizeStage(raw);
                if (normalized is null)
                {
                    malformed.Add($"gates.{property.Name} = '{property.Value.GetRawText()}' is not PASS/FAIL/SKIP.");
                }

                gates[property.Name] = normalized switch
                {
                    "PASS" => KeyenceGateOutcome.Pass,
                    "FAIL" => KeyenceGateOutcome.Fail,
                    "SKIP" => KeyenceGateOutcome.Skip,
                    _ => null
                };
            }
        }
        else
        {
            malformed.Add("missing 'gates' object.");
        }

        KeyenceScriptCompile? compile = null;
        if (root.TryGetProperty("compile", out var compileElement) && compileElement.ValueKind == JsonValueKind.Object)
        {
            var compileErrors = new List<KeyenceCompileError>();
            if (compileElement.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in errorsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        malformed.Add("compile.errors contains a non-object row.");
                        continue;
                    }

                    compileErrors.Add(new KeyenceCompileError(
                        OptionalString(item, "module"),
                        OptionalInt(item, "line"),
                        OptionalInt(item, "stLine"),
                        OptionalInt(item, "code"),
                        OptionalString(item, "kind"),
                        OptionalString(item, "message"),
                        OptionalString(item, "symbol")));
                }
            }

            string? state = OptionalString(compileElement, "state");
            if (state is not null && state is not ("OK" or "NG"))
            {
                malformed.Add($"compile.state '{state}' is neither OK nor NG.");
            }

            compile = new KeyenceScriptCompile(
                state,
                OptionalInt(compileElement, "errorCount"),
                OptionalInt(compileElement, "warningCount"),
                OptionalString(compileElement, "rawHeader"),
                compileErrors,
                OptionalString(compileElement, "rawText"));
        }
        else
        {
            malformed.Add("missing 'compile' object.");
        }

        KeyenceScriptImportDetail? importDetail = null;
        if (root.TryGetProperty("importDetail", out var importElement) && importElement.ValueKind == JsonValueKind.Object)
        {
            var dialogs = new List<string>();
            if (importElement.TryGetProperty("dialogs", out var dialogsElement) && dialogsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var dialog in dialogsElement.EnumerateArray())
                {
                    dialogs.Add(dialog.ValueKind == JsonValueKind.String ? dialog.GetString() ?? "" : dialog.GetRawText());
                }
            }

            bool? found = importElement.TryGetProperty("moduleFoundInTree", out var foundElement)
                          && foundElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? foundElement.GetBoolean()
                : null;
            importDetail = new KeyenceScriptImportDetail(found, dialogs);
        }

        KeyenceScriptPersist? persist = null;
        if (root.TryGetProperty("persist", out var persistElement) && persistElement.ValueKind == JsonValueKind.Object)
        {
            bool? changed = persistElement.TryGetProperty("changed", out var changedElement)
                            && changedElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? changedElement.GetBoolean()
                : null;
            persist = new KeyenceScriptPersist(
                OptionalString(persistElement, "fingerprintBefore"),
                OptionalString(persistElement, "fingerprintAfter"),
                changed);
        }

        var resultMessages = new List<string>();
        if (root.TryGetProperty("messages", out var messagesElement) && messagesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messagesElement.EnumerateArray())
            {
                resultMessages.Add(message.ValueKind == JsonValueKind.String ? message.GetString() ?? "" : message.GetRawText());
            }
        }

        return new KeyenceDeployScriptResult(
            scriptVersion,
            OptionalString(root, "projectDir"),
            OptionalString(root, "moduleName"),
            root.TryGetProperty("dryRun", out var dryRunElement) && dryRunElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? dryRunElement.GetBoolean()
                : null,
            gates,
            compile,
            importDetail,
            persist,
            resultMessages,
            malformed);
    }

    /// <summary>
    /// Parses the KV STUDIO "转换结果" error-list text into structured rows. Recognized shapes:
    ///   "Module(行 00003)(ST行 0002)[错误 1232]:\"X\": detail [symbol]"
    ///   "Module(1)(行 00005)[警告 61]: detail"
    ///   "[错误709]：detail" / "[错误214]：...[未登记变量名]" (no module prefix).
    /// Header/aggregate rows ("转换结果", "总指令数...", "程序容量...") and blank lines are skipped;
    /// unrecognized rows containing an inline [错误/警告 n] marker fall back to a Message-only
    /// entry so nothing is silently dropped.
    /// </summary>
    public static IReadOnlyList<KeyenceCompileError> ParseCompileErrors(string? rawText)
    {
        var list = new List<KeyenceCompileError>();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return list;
        }

        foreach (var rawLine in rawText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            // Header and aggregate lines carry no [错误 nnn]/[警告 nnn] marker.
            if (line.StartsWith("转换结果", StringComparison.Ordinal)
                || line.StartsWith("总指令数", StringComparison.Ordinal)
                || line.StartsWith("程序容量", StringComparison.Ordinal)
                || line.StartsWith("注释部分", StringComparison.Ordinal))
            {
                continue;
            }

            var match = CompileLinePattern.Match(line);
            if (match.Success)
            {
                string kind = IsErrorKindToken(match.Groups["kind"].Value) ? "错误" : "警告";
                string rest = match.Groups["rest"].Value.Trim();
                string? symbol = null;

                // Quoted leading token: [错误 1232]:"X": detail
                var symbolMatch = LeadingSymbolPattern.Match(rest);
                if (symbolMatch.Success)
                {
                    symbol = symbolMatch.Groups["symbol"].Value.Trim();
                    rest = symbolMatch.Groups["message"].Value.Trim();
                    if (symbol.Length == 0)
                    {
                        symbol = null;
                    }
                }
                else
                {
                    // No quoted leading symbol: drop a separator colon left by the marker.
                    rest = rest.TrimStart();
                    if (rest.Length > 0 && (rest[0] == ':' || rest[0] == '：'))
                    {
                        rest = rest[1..].TrimStart();
                    }
                }

                list.Add(new KeyenceCompileError(
                    match.Groups["module"].Success && match.Groups["module"].Value.Length > 0
                        ? match.Groups["module"].Value
                        : null,
                    OptionalGroupInt(match.Groups["line"]),
                    OptionalGroupInt(match.Groups["stline"]),
                    OptionalGroupInt(match.Groups["code"]),
                    kind,
                    rest.Length == 0 ? null : rest,
                    symbol));
                continue;
            }

            // Best-effort fallback for rows with an inline marker but an unknown layout.
            var inline = Regex.Match(line, @"\[\s*(错误|警告|error|warning)\s*(\d+)\s*\]", RegexOptions.IgnoreCase);
            if (inline.Success)
            {
                list.Add(new KeyenceCompileError(
                    null, null, null,
                    int.TryParse(inline.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : null,
                    IsErrorKindToken(inline.Groups[1].Value) ? "错误" : "警告",
                    line,
                    null));
            }
        }

        return list;
    }

    /// <summary>
    /// Independent persistence cross-check: SHA-256 over a size/name manifest of the key project
    /// files (*.kpr, *.prgx, *.kstx, *.mnm; bounded walk, obj/bin/.git skipped). Advisory only -
    /// the script's own persist gate stays authoritative.
    /// </summary>
    public static string DirectoryFingerprint(string projectDir, out bool partial, int maxFiles = 2000)
    {
        var files = new List<string>();
        int budget = maxFiles;
        CollectFingerprintFiles(Path.GetFullPath(projectDir), files, ref budget);
        partial = budget < 0;

        files.Sort(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var file in files)
        {
            try
            {
                sb.Append(Path.GetFileName(file)).Append('|').Append(new FileInfo(file).Length).Append('\n');
            }
            catch (Exception)
            {
                sb.Append(Path.GetFileName(file)).Append("|<unreadable>\n");
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static void CollectFingerprintFiles(string dir, List<string> files, ref int budget)
    {
        if (budget < 0)
        {
            return;
        }

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(dir);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var entry in entries)
        {
            string name = Path.GetFileName(entry);
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
            {
                if (name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                CollectFingerprintFiles(entry, files, ref budget);
            }
            else if (IsKeyProjectFile(name))
            {
                if (--budget < 0)
                {
                    return;
                }

                files.Add(entry);
            }
        }
    }

    private static bool IsKeyProjectFile(string fileName) =>
        fileName.EndsWith(".kpr", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".prgx", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".kstx", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".mnm", StringComparison.OrdinalIgnoreCase);

    private static bool IsErrorKind(string? kind) =>
        kind is null || kind == "错误" || kind.Equals("error", StringComparison.OrdinalIgnoreCase);

    private static bool IsErrorKindToken(string token) =>
        token.StartsWith("错", StringComparison.Ordinal) || token.Equals("error", StringComparison.OrdinalIgnoreCase);

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? OptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : null;

    private static int? OptionalGroupInt(Group group) =>
        group.Success && int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // A stale result file is handled by the content check below; deletion failure is not fatal.
        }
    }

    private static string? ShortHash(string? hash) =>
        hash is null ? null : hash[..Math.Min(16, hash.Length)];

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[^max..];
}
