using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PlcMcp.Adapters.Protocols;
using PlcMcp.AutoShopNativeBridge.Crypto;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Workers.AutoShop.DownloadProtocol;
using PlcMcp.Engineering.Workers.AutoShop.InjectBridge;
using PlcMcp.Engineering.Workers.AutoShop.ProjectFormat;
using PlcMcp.Engineering.Workers.AutoShop.Uia;

namespace PlcMcp.Server.Governance;

/// <summary>
/// Injectable options for <see cref="AutoShopInjectTools"/>. Defaults follow the
/// E2/E3 deliverables: the native build output of tools/AutoShopInjectBridge/build.cmd
/// and the E3 compile-all entry point (0x15A2EC0, verified via the injection bridge
/// three times, docs/inovance-autoshop-open-and-compile.md). Every probe is
/// injectable so tests run fully offline.
/// </summary>
public sealed class AutoShopInjectToolsOptions
{
    /// <summary>AutoShop executable started (and validated) by the injector.</summary>
    public string AutoShopExecutablePath { get; init; } = InjectLauncherOptions.DefaultAutoShopPath;

    /// <summary>Native injector executable (VS2022 v143 x86 build output).</summary>
    public string InjectorExecutablePath { get; init; } =
        Environment.ExpandEnvironmentVariables(InjectLauncherOptions.DefaultInjectorPath);

    /// <summary>Native bridge DLL handed to the injector.</summary>
    public string BridgeDllPath { get; init; } = Environment.ExpandEnvironmentVariables(
        InjectLauncherOptions.DefaultBridgeDllPath);

    /// <summary>JSONL ownership journal shared with <see cref="InjectLauncher"/>.</summary>
    public string OwnedPidJournalPath { get; init; } = new InjectLauncherOptions().OwnedPidJournalPath;

    /// <summary>Named pipe configured for the injected bridge.</summary>
    public string PipeName { get; init; } = InjectBridgeProtocol.PipeName;

    /// <summary>
    /// F1-verified compile-all entry chain (RELOCS_STRIPPED AutoShop 4.12.0.2 x86,
    /// ImageBase 0x400000; docs/inovance-autoshop-write-signatures.md section 5).
    /// Configured as the bridge compileEntryRva hint; the native bridge answers
    /// with a submit receipt (the compile gate is open by default; the bridge
    /// checks the 0x161B620 project gate first and reports a clear error when no
    /// project is open).
    /// </summary>
    public string CompileEntryHint { get; init; } =
        "0x15A2EC0 (F1-verified compile-all entry; root=0x1581A70, cmp=0x1581CA0, project gate=0x161B620)";

    /// <summary>Loopback port set as the bridge communication target (simulator native protocol).</summary>
    public int TargetPort { get; init; } = 12939;

    /// <summary>
    /// Loopback Modbus port of the H5U simulator used for the RUN/STOP register-alive
    /// evidence reads (D100 before/after, G2 docs §3.4). Evidence reads are always
    /// 127.0.0.1; this only selects the port.
    /// </summary>
    public int SimulatorModbusPort { get; init; } = 502;

    /// <summary>
    /// Overrides the simulator register-chain control channel (tests inject a scripted
    /// fake). Production leaves this null and uses <see cref="SimulatorRegisterClient"/>
    /// whose factory hard-refuses any non-loopback endpoint before connecting.
    /// </summary>
    public Func<string, int, ISimulatorControlChannel>? SimulatorControlChannelFactory { get; init; }

    /// <summary>
    /// Overrides the file-transfer download step (tests inject a fake transport).
    /// Production leaves this null and uses <see cref="DownloadProtocolClient"/>
    /// (login + chunked transfer), whose factory hard-refuses non-loopback endpoints.
    /// Signature: (plan file, host, port, cancellation) -> (ok, detail receipt).
    /// </summary>
    public Func<DownloadPayloadFile, string, int, CancellationToken, Task<(bool Ok, string Detail)>>? DownloadStepOverride { get; init; }

    /// <summary>
    /// Overrides the Modbus TCP transport for the RUN/STOP register evidence reads
    /// (tests: in-memory fake server; production: TCP to 127.0.0.1 only).
    /// </summary>
    public Func<string, int, Task<Stream>>? SimulatorModbusTransportFactory { get; init; }

    public TimeSpan InjectorTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan OpenPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Artifact poll window after a compile request the bridge accepted (E3: 25-40 s).</summary>
    public TimeSpan CompilePollTimeout { get; init; } = TimeSpan.FromSeconds(40);

    /// <summary>
    /// Grace window that keeps the artifact poll running after the FIRST new artifact:
    /// E3 evidence shows *.foid may land before the *.compile (the vendor writes the
    /// artifacts in sequence), so the poll must not stop on the first file.
    /// </summary>
    public TimeSpan CompileGraceTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Shorter poll window when the bridge honestly answered Unsupported (nothing will appear).</summary>
    public TimeSpan BridgeUnsupportedPollTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan CompilePollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Overrides the per-pid window probe (tests). Null means "no window observed".</summary>
    public Func<int, AutoShopWindowSnapshot?>? WindowProbe { get; init; }

    /// <summary>Creates the per-open launcher (tests inject fakes; default: real launcher).</summary>
    public Func<InjectLauncherOptions, InjectLauncher>? LauncherFactory { get; init; }

    /// <summary>Overrides File.Exists probes used by launcher discovery (tests).</summary>
    public Func<string, bool>? FileExists { get; init; }
}

/// <summary>Window evidence collected for one AutoShop pid (read-only EnumWindows data).</summary>
/// <param name="MainFrameTitles">Titles of windows whose class starts with the MFC frame prefix.</param>
/// <param name="DialogTitles">Titles of visible modal dialog windows (class #32770).</param>
/// <param name="DialogTexts">Child Static control texts of the visible dialogs (failure evidence).</param>
public sealed record AutoShopWindowSnapshot(
    IReadOnlyList<string> MainFrameTitles,
    IReadOnlyList<string> DialogTitles,
    IReadOnlyList<string> DialogTexts);

/// <summary>Outcome of an open attempt, classified from window evidence (E3 four-state + extras).</summary>
public enum AutoShopOpenOutcome
{
    /// <summary>Main frame title contains the directory/HCP name; no failure dialog.</summary>
    Opened,

    /// <summary>No window of the started pid observed within the bounded window.</summary>
    NoWindow,

    /// <summary>The injector/start failed before a window could exist.</summary>
    LauncherFailed,

    /// <summary>7596: "加载Hcp文件中的工程属性信息错误" (decrypt/XML/Version query gate).</summary>
    PropertyLoadFailed,

    /// <summary>7597: 62203 "版本高于当前程序" / "Hcp文件版本不兼容" (Version > 1).</summary>
    VersionIncompatible,

    /// <summary>7598: "加载Hcp文件的文件列表信息错误" (//project/file query empty).</summary>
    FileListLoadFailed,

    /// <summary>62220: "打开工程失败" (file element count 0).</summary>
    OpenFailed,

    /// <summary>CLI path dialog: "工程 ... 已不存在或文件路径超出系统限制".</summary>
    FileNotFound,

    /// <summary>"打开文件：%s 时出错" (a declared file failed to open in the project directory).</summary>
    FileOpenError,

    /// <summary>A modal dialog appeared with none of the known failure texts.</summary>
    UnknownDialog,
}

/// <summary>Pure classifier mapping window evidence onto <see cref="AutoShopOpenOutcome"/> (E3 §2.2/§4.2).</summary>
public static class AutoShopOpenClassification
{
    /// <summary>Dialog evidence markers; matched in priority order (all are reversed strings from 4.12.0.2).</summary>
    public static readonly (AutoShopOpenOutcome Outcome, string Marker)[] Markers =
    [
        (AutoShopOpenOutcome.FileNotFound, "已不存在或文件路径超出系统限制"),
        (AutoShopOpenOutcome.VersionIncompatible, "版本高于当前程序"),
        (AutoShopOpenOutcome.VersionIncompatible, "版本不兼容"),
        (AutoShopOpenOutcome.PropertyLoadFailed, "工程属性信息错误"),
        (AutoShopOpenOutcome.FileListLoadFailed, "文件列表信息错误"),
        (AutoShopOpenOutcome.OpenFailed, "打开工程失败"),
        (AutoShopOpenOutcome.FileOpenError, "打开文件："),
    ];

    /// <summary>
    /// Benign dialog markers (transient splash screens observed on 4.12.0.2, e.g. the
    /// "正在加载中" loading dialog). A dialog whose texts are ALL benign keeps the poll
    /// running instead of failing the open — only failure dialogs end it.
    /// </summary>
    public static readonly string[] BenignDialogMarkers = ["正在加载中", "Loading"];

    private static bool IsBenignDialog(string dialogEvidence)
    {
        foreach (string text in dialogEvidence.Split(" | "))
        {
            bool benign = BenignDialogMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
            if (!benign)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Classifies one snapshot. Returns NoWindow when neither a dialog nor a matching title exists.</summary>
    public static (AutoShopOpenOutcome Outcome, string Evidence) Classify(
        AutoShopWindowSnapshot? snapshot, string dirName, string hcpFileName)
    {
        if (snapshot is null)
        {
            return (AutoShopOpenOutcome.NoWindow, "no window evidence collected.");
        }

        // Child Static texts are the primary failure evidence (E3 dialog forensics); dialog
        // titles are a tolerant secondary channel. A dialog whose texts are ALL benign keeps
        // the poll running — only failure/unknown dialogs end it.
        string dialogTexts = string.Join(" | ",
            snapshot.DialogTexts.Where(t => !string.IsNullOrWhiteSpace(t)));
        string dialogTitles = string.Join(" | ",
            snapshot.DialogTitles.Where(t => !string.IsNullOrWhiteSpace(t)));
        if (dialogTexts.Length > 0)
        {
            foreach (var (outcome, marker) in Markers)
            {
                if (dialogTexts.Contains(marker, StringComparison.Ordinal))
                {
                    return (outcome, $"dialog evidence: {dialogTexts}");
                }
            }
            if (IsBenignDialog(dialogTexts))
            {
                return (AutoShopOpenOutcome.NoWindow, $"benign dialog (poll continues): {dialogTexts}");
            }
            return (AutoShopOpenOutcome.UnknownDialog, $"dialog evidence: {dialogTexts}");
        }
        if (dialogTitles.Length > 0)
        {
            foreach (var (outcome, marker) in Markers)
            {
                if (dialogTitles.Contains(marker, StringComparison.Ordinal))
                {
                    return (outcome, $"dialog evidence: {dialogTitles}");
                }
            }
            return (AutoShopOpenOutcome.UnknownDialog, $"dialog evidence (title only): {dialogTitles}");
        }

        string titleEvidence = string.Join(" | ", snapshot.MainFrameTitles);
        if (titleEvidence.Length > 0)
        {
            string withoutExtension = Path.GetFileNameWithoutExtension(hcpFileName);
            bool titleMatches =
                (!string.IsNullOrWhiteSpace(dirName) && titleEvidence.Contains(dirName, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(withoutExtension) && titleEvidence.Contains(withoutExtension, StringComparison.OrdinalIgnoreCase));
            if (titleMatches)
            {
                return (AutoShopOpenOutcome.Opened, $"main frame title: {titleEvidence}");
            }
        }

        return (AutoShopOpenOutcome.NoWindow,
            titleEvidence.Length > 0 ? $"main frame title (not yet the project): {titleEvidence}" : "no matching window yet.");
    }
}

/// <summary>Report of plc_autoshop_open_project. Failure is never reported as success.</summary>
public sealed record AutoShopOpenProjectReport(
    bool Success,
    string Outcome,
    string ProjectDir,
    string? HcpPath,
    bool HcpFreshCopy,
    int? Pid,
    string? WindowTitle,
    IReadOnlyList<string> DialogEvidence,
    string PrepareSummary,
    string Message,
    string Disclaimer,
    string LogPath);

/// <summary>One compile artifact discovered on disk with its decryption summary.</summary>
public sealed record AutoShopCompileArtifact(
    string Name,
    long Length,
    DateTimeOffset LastWriteUtc,
    bool IsNew,
    bool DecryptedOk,
    string? DecryptedHeadHex,
    string? DecryptError);

/// <summary>Report of plc_autoshop_compile. Honest: the delivered bridge answers Unsupported.</summary>
public sealed record AutoShopCompileReport(
    bool Success,
    string Status,
    string ProjectDir,
    string? EntryHint,
    bool BridgeAnswered,
    string BridgeMessage,
    IReadOnlyList<AutoShopCompileArtifact> Artifacts,
    bool PollTimedOut,
    string Message,
    string Disclaimer,
    string LogPath);

/// <summary>One generated project file with its on-disk length.</summary>
public sealed record AutoShopGeneratedFile(string Name, long Length);

/// <summary>Report of plc_autoshop_generate_project (offline construction + round-trip verification).</summary>
public sealed record AutoShopGenerateProjectReport(
    bool Success,
    string ProjectDir,
    string? HcpPath,
    string? HcppPath,
    IReadOnlyList<AutoShopGeneratedFile> Files,
    string XmlSummary,
    bool RoundTripOk,
    string? RoundTripError,
    string Message,
    string Disclaimer,
    string LogPath);

/// <summary>Report of plc_autoshop_monitor_read (in-process inject-bridge read; never presented as hardware data).</summary>
public sealed record AutoShopMonitorReadReport(
    bool Success,
    string Status,
    int? HostPid,
    string TargetEndpoint,
    bool? TargetStored,
    bool StatusReceiptSucceeded,
    string? StatusReceipt,
    bool ElementReceiptSucceeded,
    string? ElementReceipt,
    string Message,
    string Disclaimer,
    string LogPath);

/// <summary>
/// Report of plc_autoshop_download (G2 closed loop over the D1 12939 protocol). Honest
/// per-step receipts: transport acks, the load-trigger verdict (simulator stub status
/// 0x82 is NEVER reported as a program load), and the RUN control-word verdict.
/// </summary>
public sealed record AutoShopDownloadReport(
    bool Success,
    string Status,
    string ProjectDir,
    string? PayloadPath,
    long PayloadLength,
    string? PayloadMd5,
    string? RemoteName,
    string TargetEndpoint,
    bool PhysicalDownloadAllowed,
    bool Downloaded,
    string? LoadVerdict,
    string? RunVerdict,
    string? StopVerdict,
    IReadOnlyList<string> Steps,
    string Message,
    string Disclaimer,
    string LogPath);

/// <summary>
/// Report of plc_autoshop_run_stop (G2 register-chain control word). Carries the exact
/// wire frame as hex, the daemon reply verdict and the Modbus D100 before/after values
/// read from the loopback simulator (RUN evidence: registers go live; STOP retains them).
/// </summary>
public sealed record AutoShopRunStopReport(
    bool Success,
    string Status,
    string Action,
    string TargetEndpoint,
    string FrameHex,
    string? ReplyVerdict,
    bool PhysicalDownloadAllowed,
    string ModbusEndpoint,
    ushort? RegisterBefore,
    ushort? RegisterAfter,
    string? RegisterEvidenceNote,
    string Message,
    string Disclaimer,
    string LogPath);

/// <summary>One axis definition reported by plc_autoshop_axis_config (RE-derived schema, structural view).</summary>
public sealed record AutoShopAxisSummary(
    string? Name,
    string Kind,
    string? AxisType,
    int? Mode,
    bool? EnableLimitSet,
    double? ForwardLimitValue,
    double? NegativeLimitValue,
    double? RotationPeriod,
    bool? IsVirtual,
    bool? IsContour,
    string? OutputDev,
    string? InputDev,
    string? SlaveName,
    int? Row);

/// <summary>One applied axis edit (axis name, field name, new value).</summary>
public sealed record AutoShopAxisEdit(string AxisName, string Field, object? NewValue);

/// <summary>
/// Report of plc_autoshop_axis_config. Structure-level only: the axis.xml schema is
/// reverse-engineered (no real H5U sample was available), so the list is a structural
/// view and whether the vendor UI consumes the values is unverified.
/// </summary>
public sealed record AutoShopAxisConfigReport(
    bool Success,
    string Status,
    string ProjectDir,
    string? HcppPath,
    bool HasAxisFile,
    IReadOnlyList<AutoShopAxisSummary> Axes,
    IReadOnlyList<AutoShopAxisEdit> AppliedEdits,
    bool Repacked,
    bool RoundTripOk,
    string? RoundTripError,
    string? OpenOutcome,
    string Message,
    string Disclaimer,
    string LogPath);

/// <summary>One file entry requested for plc_autoshop_generate_project.</summary>
public sealed record AutoShopGeneratedFileSpec(string Name, byte[] Content);

/// <summary>
/// E4 MCP integration of the AutoShop inject bridge (phase three): open project,
/// compile, generate project, in-process monitor read and axis configuration.
/// Everything is gated behind the explicit --autoshop-bridge switch (the tools are
/// not registered at all without it).
///
/// Security posture (mirrors the inject bridge docs):
/// - the AutoShop instance is only ever started BY the inject bridge launcher
///   (injector-owned pid, journaled, handshake-verified); it is never injected into a
///   user-interactive instance, and a failed open stops the instance again;
/// - project directories are validated: absolute, existing, and never inside the
///   AutoShop installation directory (the app writes BackUP/Temp/config.ini next to
///   the project, so the install tree must stay untouched);
/// - the .hcp is decrypted in place by AutoShop on open (consumed): when a .hcpp
///   source exists it is re-unpacked into fresh bytes before every open;
/// - compile goes through the F1-verified in-process compile-all chain (offline
///   behaviour, no PLC action; the project gate is checked first);
/// - the monitor read is an in-process vendor read (G1 value-path upgrade: map fill via
///   the non-stub primitives, dual-slot polling — the tag+0x0C CString delivery slot the
///   vendor write-back parser assigns and the +0x20 double mirror — with a delivered
///   discriminator), loopback targets only; delivery is blocked on the in-process session
///   establishment which is not reverse-engineered, so values stay call-level evidence
///   until a backend answers;
/// - element write and force are NOT exposed (vendor write exports are stubs, never
///   invoked; the bridge gates default closed). The download and run/stop tools ARE
///   registered behind this switch but are loopback-simulator-only by default: the D1
///   protocol clients hard-refuse non-loopback endpoints at construction (red line), a
///   physical target additionally requires the explicit --autoshop-allow-physical-download
///   operator switch (default OFF) plus approval, and physical download/run remains
///   unverified (Experimental at best); the simulator program-load path is a stub
///   (status 0x82) and is never reported as a program load;
/// - all polls are bounded by timeouts and poll caps; failures surface the captured
///   dialog evidence (four-state mapping) instead of a fake success;
/// - every call is logged to an on-disk JSONL log; all operations are serialized
///   through one service-level gate (single experiment instance, single pipe client);
/// - reverse-engineered interfaces, no vendor endorsement, version-locked 4.12.0.2 x86.
/// </summary>
public sealed class AutoShopInjectTools
{
    public const string DisclaimerText = InjectBridgeProtocol.Disclaimer;

    private static readonly JsonSerializerOptions LogJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly AutoShopInjectToolsOptions _options;
    private readonly string _stateDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private AutoShopInjectSession? _session;

    public AutoShopInjectTools(string stateDirectory, AutoShopInjectToolsOptions? options = null)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _options = options ?? new AutoShopInjectToolsOptions();
    }

    public string StateDirectory => _stateDirectory;
    public string LogFile => Path.Combine(_stateDirectory, "autoshop-inject.log");
    public AutoShopInjectToolsOptions Options => _options;

    /// <summary>The currently opened project session (null until open_project succeeds).</summary>
    public AutoShopInjectSession? CurrentSession
    {
        get { lock (_stateLock) { return _session; } }
    }

    // ------------------------------------------------------------------ open

    /// <summary>
    /// Opens an AutoShop project directory: prepares a fresh .hcp (re-unpacked from the
    /// .hcpp source when present, since AutoShop consumes the .hcp in place), starts an
    /// owned AutoShop instance with the bridge injected (AutoShop.exe &lt;path&gt;.hcp) and
    /// polls the window state until success or a classified failure dialog appears.
    /// </summary>
    public async Task<AutoShopOpenProjectReport> OpenProjectAsync(string projectDir, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await OpenProjectCoreAsync(projectDir, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AutoShopOpenProjectReport> OpenProjectCoreAsync(string projectDir, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string disclaimer = DisclaimerText;

        // 1. Directory validation: absolute, existing, never inside the install tree.
        string? dirError = ValidateProjectDirectory(projectDir);
        if (dirError is not null)
        {
            await LogAsync("open_project", false, dirError, null, projectDir).ConfigureAwait(false);
            return new AutoShopOpenProjectReport(false, AutoShopOpenOutcome.LauncherFailed.ToString(),
                projectDir, null, false, null, null, [], "directory not prepared.", dirError, disclaimer, LogFile);
        }
        string fullDir = Path.GetFullPath(projectDir);

        // 2. Prepare a fresh .hcp (the .hcp is decrypted in place on every open).
        PrepareResult prepare;
        try
        {
            prepare = PrepareHcp(fullDir);
        }
        catch (Exception ex) when (ex is IOException or HcppFormatException or ArgumentException or InvalidOperationException)
        {
            string message = $"Project preparation failed: {ex.Message} Success is not claimed.";
            await LogAsync("open_project", false, message, null, fullDir).ConfigureAwait(false);
            return new AutoShopOpenProjectReport(false, AutoShopOpenOutcome.LauncherFailed.ToString(),
                fullDir, null, false, null, null, [], "project not prepared.", message, disclaimer, LogFile);
        }
        if (prepare.Error is not null)
        {
            await LogAsync("open_project", false, prepare.Error, null, fullDir).ConfigureAwait(false);
            return new AutoShopOpenProjectReport(false, AutoShopOpenOutcome.LauncherFailed.ToString(),
                fullDir, null, false, null, null, [], prepare.Summary, prepare.Error, disclaimer, LogFile);
        }

        string hcpPath = prepare.HcpPath!;
        string dirName = Path.GetFileName(fullDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string hcpFileName = Path.GetFileName(hcpPath);

        // 3. Stop the previous experiment instance (single instance at a time).
        await StopSessionCoreAsync(cancellationToken).ConfigureAwait(false);

        // 4. Start an owned AutoShop instance with the bridge injected; the .hcp path is
        //    passed as the AutoShop command line (E3 open method).
        var launcherOptions = new InjectLauncherOptions
        {
            AutoShopExecutablePath = _options.AutoShopExecutablePath,
            InjectorExecutablePath = _options.InjectorExecutablePath,
            BridgeDllPath = _options.BridgeDllPath,
            OwnedPidJournalPath = _options.OwnedPidJournalPath,
            PipeName = _options.PipeName,
            AutoShopArguments = hcpPath,
            InjectorTimeout = _options.InjectorTimeout,
            StopTimeout = _options.StopTimeout,
            Gate = new InjectBridgeGateOptions { CompileEntryHint = _options.CompileEntryHint },
            FileExists = _options.FileExists
        };
        InjectLauncher launcher = _options.LauncherFactory?.Invoke(launcherOptions) ?? new InjectLauncher(launcherOptions);
        InjectStartResult start = await launcher.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!start.Success || start.ProcessId is null)
        {
            string message = $"AutoShop instance start failed: {start.Details} The project was not opened; nothing was left running.";
            await LogAsync("open_project", false, message, null, fullDir).ConfigureAwait(false);
            return new AutoShopOpenProjectReport(false, AutoShopOpenOutcome.LauncherFailed.ToString(),
                fullDir, hcpPath, prepare.Fresh, null, null, [], prepare.Summary, message, disclaimer, LogFile);
        }

        // 5. Poll the window state (bounded; fail fast on a classified dialog).
        int pid = start.ProcessId.Value;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        AutoShopWindowSnapshot? snapshot = null;
        (AutoShopOpenOutcome outcome, string evidence) = (AutoShopOpenOutcome.NoWindow, "no window evidence collected.");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot = ProbeWindows(pid);
            (outcome, evidence) = AutoShopOpenClassification.Classify(snapshot, dirName, hcpFileName);
            if (outcome is not AutoShopOpenOutcome.NoWindow)
            {
                break;
            }
            if (DateTimeOffset.UtcNow + _options.OpenPollInterval >= startedAt + _options.OpenTimeout)
            {
                break;
            }
            await Task.Delay(_options.OpenPollInterval, cancellationToken).ConfigureAwait(false);
        }

        if (outcome is not AutoShopOpenOutcome.Opened)
        {
            // Self-start, self-clean: the instance this launcher created is stopped again.
            InjectStopResult cleanup = await launcher.StopAsync(cancellationToken).ConfigureAwait(false);
            string message = $"Project open did not succeed (outcome: {outcome}; {evidence}) " +
                $"after {(DateTimeOffset.UtcNow - startedAt).TotalSeconds:0.#} s. The experiment instance was stopped again " +
                $"({cleanup.Entries.Count} cleanup entries). Success is not claimed.";
            await LogAsync("open_project", false, message, pid, fullDir).ConfigureAwait(false);
            return new AutoShopOpenProjectReport(false, outcome.ToString(),
                fullDir, hcpPath, prepare.Fresh, pid, LastFrameTitle(snapshot),
                snapshot?.DialogTexts.Concat(snapshot.DialogTitles).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray() ?? [],
                prepare.Summary, message, disclaimer, LogFile);
        }

        // 6. Success: keep the instance for monitor/compile; the session owns cleanup.
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = _options.PipeName,
            Timeout = _options.InjectorTimeout,
            Gate = new InjectBridgeGateOptions { CompileEntryHint = _options.CompileEntryHint }
        });
        lock (_stateLock)
        {
            _session = new AutoShopInjectSession(
                launcher, pid, _options.PipeName, fullDir, hcpPath, services, DateTimeOffset.UtcNow);
        }
        string ok = $"Project opened (outcome: {outcome}; {evidence}) after " +
            $"{(DateTimeOffset.UtcNow - startedAt).TotalSeconds:0.#} s; AutoShop instance pid {pid} is owned by this server " +
            "(inject bridge injected, handshake verified) and can be closed by the next open or server shutdown. " +
            "This is a local IDE instance, not a PLC connection.";
        await LogAsync("open_project", true, ok, pid, fullDir).ConfigureAwait(false);
        return new AutoShopOpenProjectReport(true, outcome.ToString(),
            fullDir, hcpPath, prepare.Fresh, pid, LastFrameTitle(snapshot), [],
            prepare.Summary, ok, disclaimer, LogFile);
    }

    // ------------------------------------------------------------------ compile

    /// <summary>
    /// Compiles the currently opened project: sends the bridge compile request
    /// (F1-verified in-process chain: root 0x1581A70 -> cmpMgr 0x1581CA0 ->
    /// compileAll 0x15A2EC0, project gate 0x161B620 checked first; compile = offline
    /// behaviour, no PLC action), polls the Compile directory for artifacts and
    /// decrypts each .compile with the UCodeCrypt decrypter. A bridge that answers
    /// Unsupported (old DLL / disabled surface) is reported honestly.
    /// </summary>
    public async Task<AutoShopCompileReport> CompileAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CompileCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AutoShopCompileReport> CompileCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AutoShopInjectSession? session = CurrentSession;
        string disclaimer = DisclaimerText;
        if (session is null)
        {
            string gateMessage = "compile: Unsupported. The current-project gate is closed: no project has been opened by " +
                "this server session; call plc_autoshop_open_project first. No bridge call was made.";
            await LogAsync("compile", false, gateMessage, null, null).ConfigureAwait(false);
            return new AutoShopCompileReport(false, "Unsupported", string.Empty, _options.CompileEntryHint,
                false, gateMessage, [], false, gateMessage, disclaimer, LogFile);
        }

        string projectDir = session.ProjectDir;
        IReadOnlyList<AutoShopCompileArtifact> baseline = CollectArtifacts(projectDir, isNewPredicate: _ => false);

        // Bridge compile request. The C# gate carries the E3 entry hint; the native
        // bridge answers Unsupported until the signature evidence lands.
        InjectBridgeCompileReport bridge = await session.Services.CompileAsync(cancellationToken).ConfigureAwait(false);
        bool bridgeSaysUnsupported = !bridge.Succeeded;
        TimeSpan pollWindow = bridgeSaysUnsupported
            ? _options.BridgeUnsupportedPollTimeout
            : _options.CompilePollTimeout;

        // Artifact polling (bounded). The poll keeps a short grace window after
        // the first new artifact (E3: *.foid may land before the *.compile).
        DateTimeOffset deadline = DateTimeOffset.UtcNow + pollWindow;
        List<AutoShopCompileArtifact> artifacts = [];
        bool timedOut = false;
        DateTimeOffset? firstArtifactAt = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            artifacts = CollectArtifacts(projectDir, isNewPredicate: p => !baseline.Any(b => string.Equals(b.Name, Path.GetFileName(p), StringComparison.OrdinalIgnoreCase)))
                .Where(a => a.IsNew)
                .ToList();
            if (artifacts.Count > 0)
            {
                bool hasCompile = artifacts.Any(a => a.Name.EndsWith(".compile", StringComparison.OrdinalIgnoreCase));
                firstArtifactAt ??= DateTimeOffset.UtcNow;
                if (hasCompile || DateTimeOffset.UtcNow >= firstArtifactAt + _options.CompileGraceTimeout)
                {
                    break;
                }
            }
            if (DateTimeOffset.UtcNow + _options.CompilePollInterval >= deadline)
            {
                timedOut = true;
                break;
            }
            await Task.Delay(_options.CompilePollInterval, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<AutoShopCompileArtifact> all = baseline.Concat(artifacts).ToList();
        string status = bridgeSaysUnsupported ? "Unsupported" : "Experimental";
        string message;
        if (bridgeSaysUnsupported)
        {
            message = bridge.Message + (artifacts.Count > 0
                ? $" {artifacts.Count} new artifact(s) appeared during the poll window (from a parallel or previous trigger); " +
                  "they are listed with their decryption summary below but are NOT evidence that this compile request executed."
                : " No new compile artifacts appeared; success is not claimed.");
        }
        else
        {
            message = artifacts.Count > 0
                ? $"compile: {artifacts.Count} new artifact(s) appeared and were decrypted for verification."
                : $"compile: no new artifacts appeared within the {pollWindow.TotalSeconds:0.#} s poll window; success is not claimed.";
        }
        IReadOnlyList<AutoShopCompileArtifact> newCompiles = artifacts
            .Where(a => a.Name.EndsWith(".compile", StringComparison.OrdinalIgnoreCase))
            .ToList();
        await LogAsync("compile", !bridgeSaysUnsupported && newCompiles.Count > 0, message, session.Pid, projectDir).ConfigureAwait(false);
        return new AutoShopCompileReport(
            !bridgeSaysUnsupported && newCompiles.Count > 0 && newCompiles.All(a => a.DecryptedOk),
            status, projectDir, _options.CompileEntryHint,
            BridgeAnswered: true, bridge.Message, all, timedOut, message, disclaimer, LogFile);
    }

    // ------------------------------------------------------------------ generate

    /// <summary>
    /// Generates an openable AutoShop project directory (E3 construction): the project XML
    /// is built (or a real .hcpp source is unpacked byte-faithfully), encrypted with the
    /// UCodeCrypt encrypter into &lt;name&gt;.hcp, written together with the declared files
    /// (full disk content including the 31-byte AutoShop header) and wrapped into a .hcpp
    /// container for fresh-copy re-opens. The result is verified by an offline round trip.
    /// </summary>
    public async Task<AutoShopGenerateProjectReport> GenerateProjectAsync(
        string projectDir,
        string? model,
        string? projectName,
        string? projectDescription,
        string? hardwareFile,
        string? sourceHcpp,
        IReadOnlyList<AutoShopGeneratedFileSpec>? files,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GenerateCoreAsync(projectDir, model, projectName, projectDescription,
                hardwareFile, sourceHcpp, files, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AutoShopGenerateProjectReport> GenerateCoreAsync(
        string projectDir, string? model, string? projectName, string? projectDescription,
        string? hardwareFile, string? sourceHcpp, IReadOnlyList<AutoShopGeneratedFileSpec>? files,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string disclaimer = DisclaimerText;
        try
        {
            string? dirError = ValidateProjectDirectory(projectDir, allowMissing: true);
            if (dirError is not null)
            {
                await LogAsync("generate_project", false, dirError, null, projectDir).ConfigureAwait(false);
                return Fail(dirError);
            }
            string fullDir = Path.GetFullPath(projectDir);
            Directory.CreateDirectory(fullDir);

            byte[] hcppBytes;
            byte[] hcpBytes;
            List<(string Name, byte[] DiskContent)> diskFiles = [];
            string? usedModel = model;
            string? usedProjectName = projectName;
            string xmlSummary;

            if (!string.IsNullOrWhiteSpace(sourceHcpp))
            {
                // Byte-faithful unpack (E3 §7.1): MainBlob -> .hcp; file records -> disk files
                // with their FULL content (the 31-byte header is part of the disk file).
                string? sourceError = ValidateSourceFile(sourceHcpp);
                if (sourceError is not null)
                {
                    string message = $"Source .hcpp not found or outside the allowed roots: {sourceHcpp} ({sourceError})";
                    await LogAsync("generate_project", false, message, null, fullDir).ConfigureAwait(false);
                    return Fail(message);
                }
                byte[] sourceBytes = await File.ReadAllBytesAsync(sourceHcpp, cancellationToken).ConfigureAwait(false);
                if (!HcppReader.TryRead(sourceBytes, out HcppContainer? container, out string? parseError) || container is null)
                {
                    string message = $"Source .hcpp could not be parsed: {parseError}";
                    await LogAsync("generate_project", false, message, null, fullDir).ConfigureAwait(false);
                    return Fail(message);
                }
                if (container.MainRecord?.MainBlob is not { } mainBlob || string.IsNullOrWhiteSpace(container.MainRecord.MainProjectName))
                {
                    string message = "Source .hcpp carries no main record; an openable project cannot be constructed.";
                    await LogAsync("generate_project", false, message, null, fullDir).ConfigureAwait(false);
                    return Fail(message);
                }
                hcppBytes = sourceBytes;
                hcpBytes = mainBlob;
                diskFiles = UnpackRecords(container);
                string mainName = Path.GetFileName(container.MainRecord.MainProjectName);
                usedProjectName ??= Path.GetFileNameWithoutExtension(mainName);

                // Model evidence: decrypt the project XML and read GCMModal when possible.
                if (AutoShopProjectCrypt.TryDecryptProjectXml(mainBlob, out string? xml, out _) && xml is not null
                    && usedModel is null)
                {
                    try
                    {
                        usedModel = XElement.Parse(xml).Element("project")?.Element("GCMModal")?.Value;
                    }
                    catch (System.Xml.XmlException)
                    {
                        // model stays null; the report notes the XML did not parse
                    }
                }
                xmlSummary = $"source .hcpp: {Path.GetFileName(sourceHcpp)}; main record '{mainName}'; " +
                    $"{diskFiles.Count} file record(s) unpacked byte-faithfully (31-byte headers preserved).";
            }
            else
            {
                // From-scratch construction: XML -> UCodeCrypt encrypt -> .hcp; files with the
                // 31-byte AutoShop header; container via HcppWriter.
                string resolvedModel = string.IsNullOrWhiteSpace(usedModel) ? "H3U" : usedModel!.Trim();
                string resolvedName = string.IsNullOrWhiteSpace(usedProjectName)
                    ? Path.GetFileName(fullDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    : usedProjectName!.Trim();
                if (resolvedName.Length is 0 or > 64)
                {
                    string message = "projectName must be 1..64 characters.";
                    await LogAsync("generate_project", false, message, null, fullDir).ConfigureAwait(false);
                    return Fail(message);
                }
                usedModel = resolvedModel;
                usedProjectName = resolvedName;
                string resolvedHardware = string.IsNullOrWhiteSpace(hardwareFile) ? resolvedModel + ".dll" : hardwareFile!.Trim();

                // Effective file list: the measured Template A1 trio as placeholder defaults
                // (the .LD/.gdt internal format is not reverse-engineered); a caller spec of
                // the same name overrides its content, extra caller specs are appended.
                var effective = new List<HcppFileSpec>();
                IReadOnlyList<AutoShopGeneratedFileSpec> provided = files ?? [];
                foreach (string defaultName in new[] { "MAIN.LD", "VarList.gdt", "Config.sdt" })
                {
                    AutoShopGeneratedFileSpec? overrideSpec = provided.FirstOrDefault(
                        f => string.Equals(f.Name, defaultName, StringComparison.OrdinalIgnoreCase));
                    effective.Add(new HcppFileSpec(defaultName, overrideSpec?.Content ?? []));
                }
                foreach (AutoShopGeneratedFileSpec spec in provided)
                {
                    if (!effective.Any(e => string.Equals(e.Name, spec.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        effective.Add(new HcppFileSpec(spec.Name, spec.Content));
                    }
                }

                string xml = BuildProjectXml(resolvedName, resolvedModel, resolvedHardware, projectDescription, effective);
                // FF FE BOM + UTF-16LE (the decrypted A1 main blob = FF FE + UTF-16LE XML).
                byte[] plaintext = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
                hcpBytes = AutoShopProjectCrypt.EncryptBlob(plaintext);
                byte[] containerBytes = HcppWriter.Write(
                    new HcppMainRecordSpec(resolvedName + ".hcp", hcpBytes),
                    effective);
                hcppBytes = containerBytes;
                foreach (HcppFileSpec spec in effective)
                {
                    // Disk content = full content (31-byte AutoShop header + payload), matching
                    // the container content records byte for byte.
                    byte[] disk = new byte[HcppFraming.ContentHeaderSize + spec.Content.Length];
                    HcppFraming.ContentHeaderTemplate.CopyTo(disk, 0);
                    spec.Content.CopyTo(disk, HcppFraming.ContentHeaderSize);
                    diskFiles.Add((spec.Name, disk));
                }
                xmlSummary = $"constructed XML: <project> Version=1, GCMModal={resolvedModel}, " +
                    $"HardwareFile={resolvedHardware}, {effective.Count} file element(s); " +
                    "XML field values beyond Version/GCMModal follow Template A1 patterns (unverified).";
            }

            // Write everything. The .hcp file name is forced ASCII-safe (the injector
            // converts the AutoShop command line through ANSI; non-ASCII paths are mangled).
            string hcpName = AsciiSafeHcpName(string.IsNullOrWhiteSpace(usedProjectName) ? "project.hcp" : usedProjectName!);
            string hcpPath = Path.Combine(fullDir, hcpName);
            string hcppPath = Path.Combine(fullDir, Path.ChangeExtension(hcpName, ".hcpp"));
            await File.WriteAllBytesAsync(hcpPath, hcpBytes, cancellationToken).ConfigureAwait(false);
            foreach ((string name, byte[] disk) in diskFiles)
            {
                if (IsUnsafeMemberName(name))
                {
                    string message = $"Declared file name escapes the project directory: '{name}'.";
                    await LogAsync("generate_project", false, message, null, fullDir).ConfigureAwait(false);
                    return Fail(message);
                }
                WriteMemberFile(fullDir, name, disk);
            }
            await File.WriteAllBytesAsync(hcppPath, hcppBytes, cancellationToken).ConfigureAwait(false);

            // Offline round-trip verification: container parses, blob decrypts, XML is the project.
            (bool roundTripOk, string? roundTripError) = VerifyRoundTrip(hcppBytes, hcpBytes);
            string ok = roundTripOk
                ? $"Project generated in '{fullDir}': {Path.GetFileName(hcpPath)} ({hcpBytes.Length} B), " +
                  $"{Path.GetFileName(hcppPath)} ({hcppBytes.Length} B), {diskFiles.Count} file(s); " +
                  $"round-trip verified offline (container parse + UCodeCrypt decrypt + XML parse). " +
                  "Open validation is the job of plc_autoshop_open_project."
                : $"Project written but the offline round-trip verification failed: {roundTripError}";
            await LogAsync("generate_project", roundTripOk, ok, null, fullDir).ConfigureAwait(false);
            return new AutoShopGenerateProjectReport(
                roundTripOk, fullDir, hcpPath, hcppPath,
                diskFiles.Select(f => new AutoShopGeneratedFile(f.Name, f.DiskContent.Length)).ToList(),
                xmlSummary, roundTripOk, roundTripError, ok, disclaimer, LogFile);

            AutoShopGenerateProjectReport Fail(string message) =>
                new(false, projectDir, null, null, [], string.Empty, false, message, message, disclaimer, LogFile);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or HcppFormatException or InvalidOperationException)
        {
            string message = $"Project generation failed: {ex.Message}";
            await LogAsync("generate_project", false, message, null, projectDir).ConfigureAwait(false);
            return new AutoShopGenerateProjectReport(false, projectDir, null, null, [], string.Empty,
                false, message, message, disclaimer, LogFile);
        }
    }

    // ------------------------------------------------------------------ monitor read

    /// <summary>
    /// Read-only monitor through the injected bridge: stores the loopback target
    /// (127.0.0.1, fixed port) and returns the read_status call receipt plus the
    /// in-process read_elements values. The bridge fills the vendor CSoftAddrMap
    /// through the non-stub primitives (tagSoftAddr ctor + CMap::SetAt), calls
    /// ReadElemValue inside the AutoShop process and reads the tag+0x20 value slot
    /// (F2 pipeline). Loopback-simulator verified; never presented as hardware data.
    /// </summary>
    public async Task<AutoShopMonitorReadReport> MonitorReadAsync(string addresses, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await MonitorReadCoreAsync(addresses, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AutoShopMonitorReadReport> MonitorReadCoreAsync(string addresses, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AutoShopInjectSession? session = CurrentSession;
        string disclaimer = DisclaimerText;
        if (session is null)
        {
            string gateMessage = "monitor_read: no injected AutoShop instance is owned by this server; " +
                "call plc_autoshop_open_project first. No bridge call was made.";
            await LogAsync("monitor_read", false, gateMessage, null, null).ConfigureAwait(false);
            return new AutoShopMonitorReadReport(false, "Unsupported", null,
                $"127.0.0.1:{_options.TargetPort}", null, false, null, false, null, gateMessage, disclaimer, LogFile);
        }

        string endpoint = $"127.0.0.1:{_options.TargetPort}";
        InjectBridgeSetTargetReport setTarget = await session.Services
            .SetTargetAsync("127.0.0.1", _options.TargetPort, cancellationToken).ConfigureAwait(false);
        InjectBridgeStatusReport status = await session.Services.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        InjectBridgeElementReadReport elements = await session.Services
            .ReadElementsAsync(addresses, cancellationToken).ConfigureAwait(false);

        string statusReceipt = status.Succeeded
            ? $"execMode={status.ExecMode} (call {status.ExecModeCallResult}), gcmState={status.GcmState} (call {status.GcmStateCallResult}); {status.Evidence}"
            : $"read_status failed: {status.Message}";
        string elementReceipt = elements.Succeeded
            ? $"count={elements.Count}; {elements.Note}" + (string.IsNullOrWhiteSpace(elements.Values) ? "" : $"; values={elements.Values}")
            : $"read_elements failed: {elements.Message}";
        bool success = setTarget.Succeeded && status.Succeeded && elements.Succeeded;
        // G1 value-path verdict (docs/inovance-autoshop-monitor-values.md): the write-back
        // slot is located (tag+0x0C CString; the earlier +0x20-double assumption was
        // refuted) and the raw-HWND crash root cause is explained (CWnd* vs HWND misuse,
        // the shim form faults nothing). The remaining blocker is the in-process session
        // establishment, which is not reverse-engineered: the injected process emits zero
        // wire bytes, so every delivered count is 0 on the current backend. The delivered
        // discriminator grades the wording — the tool never softens it.
        string writeBackVerdict = elements.Delivered > 0
            ? $"HONEST VERDICT: VALUE WRITE-BACK OBSERVED (delivered={elements.Delivered}): the vendor stack answered these reads and wrote the tag+0x0C delivery slot — " +
              "the reported values are vendor-stack values as delivered against the loopback simulator (still Experimental, never hardware-verified)."
            : "HONEST VERDICT: the vendor value write-back is NOT closed — the delivery slot and callback path are fully located (tag+0x0C CString, dual-slot polling with a delivered " +
              "discriminator) and the earlier raw-HWND fault is explained (CWnd* vs HWND misuse, not a UI-state gap), but the in-process session establishment is not reverse-engineered " +
              "(the injected process sends zero wire bytes), so the reported values are CALL-LEVEL evidence, never PLC data; a value of 0 may " +
              "mean either 'register is 0' or 'no response'.";
        string message = success
            ? $"monitor_read answered (in-process vendor read): target {endpoint} stored; status and element results below. " +
              "The injected bridge filled the vendor element map inside the AutoShop process (SetAt/tagCtor5) and " +
              "the read was accepted (rc=0). " + writeBackVerdict
            : "monitor_read did not complete: " + (setTarget.Succeeded ? statusReceipt : setTarget.Message);
        await LogAsync("monitor_read", success, message, session.Pid, session.ProjectDir).ConfigureAwait(false);
        return new AutoShopMonitorReadReport(
            success, success ? "Experimental" : "Unsupported", session.Pid, endpoint,
            setTarget.Stored, status.Succeeded, statusReceipt,
            elements.Succeeded, elementReceipt, message, disclaimer, LogFile);
    }

    // ------------------------------------------------------------------ download / run-stop (G2 closed loop)

    /// <summary>
    /// Downloads the repackaged compile product (PPCH container <c>project.hcpp</c>) into the
    /// loopback H5U simulator workspace over the D1 12939 protocol (login → chunked file
    /// transfer → finish), then triggers program load and issues the RUN control word — the
    /// G2-verified minimal closed loop (<see cref="AutoShopDownloadClosedLoop"/>).
    ///
    /// Governance (docs/inovance-integration-plan.md phase three, docs/contracts.md):
    /// - target is HARD-LIMITED to 127.0.0.1: both protocol clients refuse any non-loopback
    ///   endpoint at construction, before a socket is created;
    /// - a non-loopback (physical) host is rejected by default; the explicit operator switch
    ///   --autoshop-allow-physical-download (default OFF) + approval only reaches that deep
    ///   check, which still refuses in this version — physical download stays
    ///   Experimental(真机待验) and nothing is ever sent off loopback;
    /// - per-step receipts are returned verbatim: transport acks, the load-trigger verdict
    ///   (the simulator answers the documented stub status 0x82 — NEVER a program load),
    ///   and the RUN/STOP acks; a failed transfer never sends RUN;
    /// - logging is on-disk JSONL; failures are failures.
    /// </summary>
    public async Task<AutoShopDownloadReport> DownloadAsync(
        string projectDir,
        string? host,
        int? port,
        bool includeStop,
        bool allowPhysicalDownload,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DownloadCoreAsync(projectDir, host, port, includeStop, allowPhysicalDownload, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AutoShopDownloadReport> DownloadCoreAsync(
        string projectDir, string? host, int? port, bool includeStop, bool allowPhysicalDownload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string disclaimer = DisclaimerText;
        string effectiveHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host!.Trim();
        int effectivePort = port ?? _options.TargetPort;
        string endpoint = $"{effectiveHost}:{effectivePort}";

        async Task<AutoShopDownloadReport> FailAsync(string message, string? payloadPath = null, long length = 0, string? md5 = null)
        {
            await LogAsync("download", false, message, null, payloadPath is null ? projectDir : Path.GetDirectoryName(payloadPath))
                .ConfigureAwait(false);
            return new AutoShopDownloadReport(false, "Unsupported", projectDir, payloadPath, length, md5, null,
                endpoint, allowPhysicalDownload, false, null, null, null, [], message, disclaimer, LogFile);
        }

        // 1. Project directory validation (same rules as open/axis: absolute, existing,
        //    never inside the AutoShop installation tree).
        string? dirError = ValidateProjectDirectory(projectDir);
        if (dirError is not null)
        {
            return await FailAsync($"download: {dirError}");
        }
        string fullDir = Path.GetFullPath(projectDir);

        // 2. Locate the download payload: the repackaged .hcpp. When compile artifacts are
        //    present the newest .hcpp is the compile pipeline's "生成下载文件" (E3 §5.4);
        //    without them the container may be an uncompiled source — said honestly.
        string[] hcppFiles = SafeGetFiles(fullDir, "*.hcpp");
        if (hcppFiles.Length == 0)
        {
            return await FailAsync(
                "download: no .hcpp payload found in the project directory. Produce one with plc_autoshop_generate_project " +
                "(and compile it with plc_autoshop_compile — the compile pipeline repackages the downloadable project.hcpp). " +
                "Nothing was sent.");
        }
        string payloadPath = hcppFiles
            .OrderByDescending(p => { try { return File.GetLastWriteTimeUtc(p); } catch (IOException) { return DateTimeOffset.MinValue; } })
            .First();
        byte[] payloadBytes;
        try
        {
            payloadBytes = await File.ReadAllBytesAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return await FailAsync($"download: the payload could not be read: {ex.Message}");
        }
        string payloadMd5;
        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            payloadMd5 = Convert.ToHexString(md5.ComputeHash(payloadBytes)).ToLowerInvariant();
        }
        string payloadNote = SafeGetFiles(Path.Combine(fullDir, "Compile"), "*.compile").Length > 0
            ? $"payload '{Path.GetFileName(payloadPath)}' ({payloadBytes.Length} B, md5 {payloadMd5}) chosen as the newest .hcpp with compile artifacts present (repacked download file)"
            : $"payload '{Path.GetFileName(payloadPath)}' ({payloadBytes.Length} B, md5 {payloadMd5}); NOTE: no Compile\\*.compile artifacts in this directory — the container may be an uncompiled source, its program content is NOT claimed";

        // 3. PPCH container guard (G2 §1.3): a raw .compile/.foid blob is refused, never wrapped.
        IReadOnlyList<DownloadPayloadFile> plan;
        try
        {
            plan = DownloadPayloadPlan.ForRepackedProject(payloadBytes);
        }
        catch (ArgumentException ex)
        {
            return await FailAsync($"download: {ex.Message}", payloadPath, payloadBytes.Length, payloadMd5);
        }

        // 4. Governance: loopback by construction; physical requires the operator switch and
        //    even then the protocol clients' deep loopback red line still refuses (nothing
        //    ever leaves 127.0.0.0/8 in this version).
        if (!IsLoopbackHost(effectiveHost))
        {
            string physicalMessage;
            if (!allowPhysicalDownload)
            {
                physicalMessage = "download: PHYSICAL DOWNLOAD IS FORBIDDEN BY DEFAULT — only the loopback simulator (127.0.0.1) is a " +
                    "download target. Enable --autoshop-allow-physical-download (operator decision; physical targets additionally require " +
                    "approval per the deployment policy) to even reach the transport. Nothing was sent.";
            }
            else
            {
                // Operator gate open: honour the attempt as far as the deep red line allows —
                // surface the protocol clients' own refusal verbatim (construction resolves the
                // host and never creates a socket for a non-loopback address).
                string refusal;
                try
                {
                    SimulatorRegisterClient.Create(effectiveHost, effectivePort).Dispose();
                    refusal = "the transport did not refuse at construction (unexpected)";
                }
                catch (InvalidOperationException ex)
                {
                    refusal = ex.Message;
                }
                physicalMessage = "download: the operator switch is ON, but the D1 protocol layer keeps its deep loopback red line — frames are " +
                    $"never sent to a non-loopback endpoint: {refusal} Physical download remains Experimental(真机待验): the " +
                    "load path on hardware (non-stub load_down_file_start) and the download state machine are unverified. Nothing was sent.";
            }
            return await FailAsync(physicalMessage, payloadPath, payloadBytes.Length, payloadMd5);
        }

        // 5. Loopback closed loop (G2): download → load trigger → RUN (→ optional STOP).
        ISimulatorControlChannel? control = null;
        try
        {
            control = _options.SimulatorControlChannelFactory?.Invoke(effectiveHost, effectivePort)
                ?? SimulatorRegisterClient.Create(effectiveHost, effectivePort);
            Func<DownloadPayloadFile, CancellationToken, Task<(bool Ok, string Detail)>> downloadAsync;
            if (_options.DownloadStepOverride is { } overrideStep)
            {
                downloadAsync = (file, ct) => overrideStep(file, effectiveHost, effectivePort, ct);
            }
            else
            {
                downloadAsync = BuildDefaultDownloadStep(effectiveHost, effectivePort);
            }

            DownloadClosedLoopResult result = await AutoShopDownloadClosedLoop.RunAsync(
                downloadAsync, control, plan, includeStop, cancellationToken).ConfigureAwait(false);

            string? loadVerdict = result.LoadTrigger is { } load
                ? load.Kind == SimulatorControlKind.Error && load.Status == 0x82
                    ? "load trigger (0xFF000010←1) answered daemon status 0x82 — the SIMULATOR LOAD IS A STUB " +
                      "(load_down_file_start): the downloaded file is NOT activated as the running program " +
                      "(program_load = Unsupported on the simulator; hardware load path unverified)"
                    : $"load trigger (0xFF000010←1): {DescribeControlReply(load)}"
                : null;
            string? runVerdict = result.Run is { } run
                ? run.Kind == SimulatorControlKind.Ack
                    ? "RUN (0xFF000020←1) acked [06] — on the loopback simulator this moves the registers live " +
                      "(G2 evidence 2026-10-29: Modbus D100 0→4660 via the factory default app; hardware RUN unverified)"
                    : $"RUN (0xFF000020←1): {DescribeControlReply(run)}"
                : null;
            string? stopVerdict = result.Stop is { } stop
                ? stop.Kind == SimulatorControlKind.Ack
                    ? "STOP (0xFF000020←0) acked [06] — the simulator RETAINS the last register values (stop is not a reset)"
                    : $"STOP (0xFF000020←0): {DescribeControlReply(stop)}"
                : null;

            bool success = result.Downloaded && result.Run?.Accepted == true;
            string message = success
                ? $"download closed loop answered on the loopback simulator: {payloadNote}; " +
                  $"{plan[0].RemoteName}: transport chunks acked (finish is one-way). Byte-exact landing into the daemon " +
                  "workspace was verified in the G2 experiment for this payload shape (md5 equality). " +
                  (loadVerdict is not null ? loadVerdict + ". " : "") +
                  (runVerdict is not null ? runVerdict + ". " : "") +
                  (stopVerdict is not null ? stopVerdict + ". " : "") +
                  "Local simulator demo only — never claimed as hardware behaviour; reverse-engineered, version-locked."
                : "download did not complete: " + string.Join("; ", result.Steps) +
                  " (a failed transfer never sends RUN). Success is not claimed.";
            await LogAsync("download", success, message, null, fullDir).ConfigureAwait(false);
            return new AutoShopDownloadReport(success, success ? "Experimental" : "Unsupported", fullDir,
                payloadPath, payloadBytes.Length, payloadMd5, plan[0].RemoteName, endpoint,
                allowPhysicalDownload, result.Downloaded, loadVerdict, runVerdict, stopVerdict,
                result.Steps, message, disclaimer, LogFile);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or SocketException
                                   or ArgumentException or TimeoutException or InvalidDataException
                                   or EndOfStreamException)
        {
            // Transport-level failure (daemon not listening, session refused, hard check):
            // frame-level evidence verbatim, no fake success.
            return await FailAsync($"download attempt on {endpoint} failed with {ex.GetType().Name}: {ex.Message}. " +
                "Is the local H5U simulator daemon running (plc_autoshop_simulator action=start)? Nothing is claimed as landed.",
                payloadPath, payloadBytes.Length, payloadMd5);
        }
        finally
        {
            (control as IDisposable)?.Dispose(); // self-clean the loopback connection (red line)
        }
    }

    /// <summary>
    /// Starts (RUN, 0xFF000020←1) or stops (STOP, 0xFF000020←0) the loopback H5U simulator by
    /// writing the G2-verified register-chain control word, and returns the daemon ack plus
    /// Modbus D100 before/after values as the "registers alive" evidence (RUN moved D100
    /// 0→4660 in the G2 experiment; STOP acks and the simulator retains the last values).
    /// Loopback-only by default; physical endpoints follow the same
    /// --autoshop-allow-physical-download gate and the clients' deep loopback red line.
    /// </summary>
    public async Task<AutoShopRunStopReport> RunStopAsync(
        string action,
        string? host,
        int? port,
        bool readRegisterEvidence,
        bool allowPhysicalDownload,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunStopCoreAsync(action, host, port, readRegisterEvidence, allowPhysicalDownload, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AutoShopRunStopReport> RunStopCoreAsync(
        string action, string? host, int? port, bool readRegisterEvidence, bool allowPhysicalDownload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string disclaimer = DisclaimerText;
        string normalized = (action ?? string.Empty).Trim().ToLowerInvariant();
        bool isRun = normalized == "run";
        string effectiveHost = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host!.Trim();
        int effectivePort = port ?? _options.TargetPort;
        string endpoint = $"{effectiveHost}:{effectivePort}";
        string modbusEndpoint = $"127.0.0.1:{_options.SimulatorModbusPort}";
        string frameHex = Convert.ToHexString(isRun ? SimulatorControlFrames.BuildRun() : SimulatorControlFrames.BuildStop());

        async Task<AutoShopRunStopReport> FailAsync(string message, string? verdict = null, string? note = null,
            ushort? before = null, ushort? after = null)
        {
            await LogAsync("run_stop", false, message, null, null).ConfigureAwait(false);
            return new AutoShopRunStopReport(false, "Unsupported", normalized, endpoint, frameHex, verdict,
                allowPhysicalDownload, modbusEndpoint, before, after, note, message, disclaimer, LogFile);
        }

        if (!isRun && normalized != "stop")
        {
            return await FailAsync($"run_stop: parameter 'action' must be 'run' or 'stop'; got '{action}'. No control word was sent.");
        }

        // Governance mirrors plc_autoshop_download: loopback only by default; the physical
        // switch reaches the protocol clients' deep refusal but never bypasses it.
        if (!IsLoopbackHost(effectiveHost))
        {
            if (!allowPhysicalDownload)
            {
                return await FailAsync("run_stop: PHYSICAL RUN/STOP IS FORBIDDEN BY DEFAULT — only the loopback simulator (127.0.0.1) is a " +
                    "run/stop target. Enable --autoshop-allow-physical-download (operator decision; physical targets additionally " +
                    "require approval) to even reach the transport. Nothing was sent.");
            }
            string refusal;
            try
            {
                SimulatorRegisterClient.Create(effectiveHost, effectivePort).Dispose();
                refusal = "the transport did not refuse at construction (unexpected)";
            }
            catch (InvalidOperationException ex)
            {
                refusal = ex.Message;
            }
            return await FailAsync("run_stop: the operator switch is ON, but the simulator control chain keeps its deep loopback red line — " +
                $"0xFF000020 frames are never sent to a non-loopback endpoint: {refusal} Hardware CPU RUN/STOP remains " +
                "Unsupported (the physical control path is not reversed). Nothing was sent.");
        }

        ISimulatorControlChannel? control = null;
        try
        {
            control = _options.SimulatorControlChannelFactory?.Invoke(effectiveHost, effectivePort)
                ?? SimulatorRegisterClient.Create(effectiveHost, effectivePort);

            // Register evidence (G2 §3.4): D100 read over Modbus TCP from the loopback
            // simulator BEFORE and AFTER the control-word write. Evidence only — a failed
            // read never changes the run verdict, it is reported honestly.
            (bool ok1, ushort? before, string detail1) = readRegisterEvidence
                ? await ReadSimulatorD100Async(cancellationToken).ConfigureAwait(false)
                : (false, null, "register evidence read skipped");

            SimulatorControlReply reply = await control.SendAsync(
                isRun ? SimulatorControlFrames.BuildRun() : SimulatorControlFrames.BuildStop(), cancellationToken)
                .ConfigureAwait(false);

            (bool ok2, ushort? after, string detail2) = readRegisterEvidence
                ? await ReadSimulatorD100Async(cancellationToken).ConfigureAwait(false)
                : (false, null, string.Empty);

            string verdict = DescribeControlReply(reply);
            bool success = reply.Accepted;

            string? registerNote = null;
            if (readRegisterEvidence)
            {
                var parts = new List<string>();
                if (ok1 && ok2)
                {
                    registerNote = $"Modbus D100 ({modbusEndpoint}): before={before} → after={after}";
                    if (isRun && before != after)
                    {
                        registerNote += " — registers went LIVE after RUN (G2 verified shape: 0→4660 with the factory default app)";
                    }
                    else if (isRun)
                    {
                        registerNote += " — value unchanged; RUN acked but the program is idle/zeroed (evidence, not a failure of the ack)";
                    }
                    else
                    {
                        registerNote += " — STOP acked and the simulator RETAINS the last values (stop is not a reset)";
                    }
                }
                else
                {
                    registerNote = $"register evidence read failed ({detail1}{(ok1 ? string.Empty : "; " + detail2)}) — the run/stop verdict stands on the native-protocol ack alone";
                }
            }

            string message = success
                ? $"run_stop {normalized} answered on {endpoint}: {verdict}. frame={frameHex}. " +
                  (registerNote is not null ? registerNote + ". " : string.Empty) +
                  "Local simulator demo only — never claimed as hardware behaviour; reverse-engineered, version-locked 4.12.0.2."
                : $"run_stop {normalized} did NOT get an accepted answer from {endpoint}: {verdict}. frame={frameHex}. " +
                  (registerNote is not null ? registerNote + ". " : string.Empty) +
                  "Success is not claimed.";
            await LogAsync("run_stop", success, message, null, null).ConfigureAwait(false);
            return new AutoShopRunStopReport(success, success ? "Experimental" : "Unsupported", normalized,
                endpoint, frameHex, verdict, allowPhysicalDownload, modbusEndpoint, before, after, registerNote,
                message, disclaimer, LogFile);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or SocketException
                                   or ArgumentException or TimeoutException or InvalidDataException
                                   or EndOfStreamException)
        {
            return await FailAsync($"run_stop attempt on {endpoint} failed with {ex.GetType().Name}: {ex.Message}. " +
                "Is the local H5U simulator daemon running (plc_autoshop_simulator action=start)? Nothing is claimed.");
        }
        finally
        {
            (control as IDisposable)?.Dispose(); // self-clean the loopback connection (red line)
        }
    }

    /// <summary>
    /// Reads the simulator D100 over Modbus TCP FC3 from 127.0.0.1 only (RUN/STOP register
    /// evidence, G2 §3.4). The address goes through the same explicit Inovance mapping as
    /// every other Inovance surface (no guessing); every failure produces a report, not an
    /// exception — the control-word verdict never depends on this read.
    /// </summary>
    private async Task<(bool Ok, ushort? Value, string Detail)> ReadSimulatorD100Async(CancellationToken cancellationToken)
    {
        const string host = "127.0.0.1";
        int port = _options.SimulatorModbusPort;
        try
        {
            var tag = new TagDefinition("run_stop_evidence", "D100", PlcDataType.UInt16);
            InovanceMapping mapping = InovanceSoftAddress.Map("H5U", tag);
            byte[] request = ModbusTcpAdapter.BuildRead(1, 1, new TagDefinition("readback", $"HR{mapping.Address}", PlcDataType.UInt16));

            Stream stream = _options.SimulatorModbusTransportFactory is { } factory
                ? await factory(host, port).ConfigureAwait(false)
                : await ConnectLoopbackModbusAsync(host, port, cancellationToken).ConfigureAwait(false);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                await stream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
                var header = new byte[7];
                await ReadExactlyAsync(stream, header, deadline.Token).ConfigureAwait(false);
                int length = (header[4] << 8) | header[5];
                if (length is < 2 or > 254)
                {
                    return (false, null, "invalid Modbus response length");
                }
                // FC3 answer after the 7-byte MBAP header: [fc][byteCount][data...];
                // exception answers are [fc|0x80][code] (payload length 2).
                var payload = new byte[length - 1];
                await ReadExactlyAsync(stream, payload, deadline.Token).ConfigureAwait(false);
                byte fc = payload[0];
                if (fc == (3 | 0x80))
                {
                    return (false, null, $"Modbus FC3 exception code {(payload.Length > 1 ? payload[1].ToString("X2") : "?")}");
                }
                if (fc != 3 || payload.Length < 4)
                {
                    return (false, null, $"unexpected Modbus FC3 response (fc {fc:X2}, {payload.Length} payload bytes)");
                }
                ushort value = (ushort)((payload[2] << 8) | payload[3]);
                return (true, value, $"HR{mapping.Address}={value}");
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ArgumentException
                                   or InvalidOperationException or InvalidDataException or EndOfStreamException)
        {
            return (false, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<Stream> ConnectLoopbackModbusAsync(string host, int port, CancellationToken cancellationToken)
    {
        // Defence in depth: the evidence reader hard-refuses any non-loopback host.
        if (!IsLoopbackHost(host))
        {
            throw new InvalidOperationException($"register evidence reads are loopback-only; refused host '{host}'.");
        }
        var client = new TcpClient { NoDelay = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await client.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
            return client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("Modbus response stream ended before the frame was complete.");
            }
            read += n;
        }
    }

    private static Func<DownloadPayloadFile, CancellationToken, Task<(bool Ok, string Detail)>> BuildDefaultDownloadStep(
        string host, int port) =>
        async (file, ct) =>
        {
            // Production wiring (G2 §5.2): login + chunked file transfer on the same
            // loopback connection; .hcpp is protected, so the login is mandatory.
            var client = DownloadProtocolClient.Create(host, port);
            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
                var login = await client.LoginAsync("plcmcp", "0", ct).ConfigureAwait(false);
                var result = await client.DownloadFileAsync(file.RemoteName, file.Bytes, 256, ct).ConfigureAwait(false);
                return (result.Ok, $"{client.Endpoint} login={login}: {result.Detail}");
            }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        };

    private static string DescribeControlReply(SimulatorControlReply reply) => reply.Kind switch
    {
        SimulatorControlKind.Ack => "acked [06]",
        SimulatorControlKind.Value => $"value={reply.Value?.ToString() ?? "?"}",
        SimulatorControlKind.Error => $"daemon status 0x{reply.Status:X2}",
        _ => "no/unknown answer",
    };

    /// <summary>Loopback host test mirroring the protocol clients' red line (the deep check still resolves+validates).</summary>
    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase) ||
        (host.Split('.') is { Length: 4 } parts &&
         parts.All(p => int.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int v) && v is >= 0 and <= 255) &&
         host.StartsWith("127.", StringComparison.Ordinal));

    // ------------------------------------------------------------------ axis config

    /// <summary>
    /// Reads (and optionally edits, repackages and re-opens) the axis configuration
    /// (xml\axis.xml member) of the .hcpp project package in the given directory
    /// (F2 pipeline: <see cref="AxisPackageEditor"/>). Writes touch PROJECT FILES only —
    /// no PLC action is possible through this path. A container without an axis member
    /// is reported honestly (hasAxisFile=false, "no axis project"); the axis schema is
    /// reverse-engineered (no real H5U sample was available), so the report is a
    /// structural view and vendor consumption of edited values is unverified.
    /// </summary>
    public async Task<AutoShopAxisConfigReport> AxisConfigAsync(
        string projectDir,
        IReadOnlyDictionary<string, object?>? edits,
        bool open,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AxisConfigCoreAsync(projectDir, edits, open, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AutoShopAxisConfigReport> AxisConfigCoreAsync(
        string projectDir,
        IReadOnlyDictionary<string, object?>? edits,
        bool open,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string disclaimer = DisclaimerText;
        string? dirError = ValidateProjectDirectory(projectDir);
        if (dirError is not null)
        {
            await LogAsync("axis_config", false, dirError, null, projectDir).ConfigureAwait(false);
            return new AutoShopAxisConfigReport(false, "Unsupported", projectDir, null, false, [], [],
                false, false, null, null, dirError, disclaimer, LogFile);
        }
        string fullDir = Path.GetFullPath(projectDir);
        string[] hcppFiles = SafeGetFiles(fullDir, "*.hcpp");
        if (hcppFiles.Length != 1)
        {
            string message = hcppFiles.Length == 0
                ? $"No .hcpp project package found in '{fullDir}'; an axis configuration can only be read " +
                  "from a .hcpp container (generate one with plc_autoshop_generate_project first)."
                : $"Ambiguous project directory: {hcppFiles.Length} .hcpp sources found; expected one.";
            await LogAsync("axis_config", false, message, null, fullDir).ConfigureAwait(false);
            return new AutoShopAxisConfigReport(false, "Unsupported", fullDir, null, false, [], [],
                false, false, null, null, message, disclaimer, LogFile);
        }
        string hcppPath = hcppFiles[0];
        byte[] hcppBytes = await File.ReadAllBytesAsync(hcppPath, cancellationToken).ConfigureAwait(false);

        if (!AxisPackageEditor.TryLoad(hcppBytes, out AxisConfigDocument? document, out bool hasAxisFile, out string? loadError))
        {
            string message = $"The axis configuration could not be loaded: {loadError}";
            await LogAsync("axis_config", false, message, null, fullDir).ConfigureAwait(false);
            return new AutoShopAxisConfigReport(false, "Unsupported", fullDir, hcppPath, hasAxisFile, [], [],
                false, false, null, null, message, disclaimer, LogFile);
        }

        var axes = document?.Axes.Select(a => new AutoShopAxisSummary(
            a.Name, a.Kind.ToString(), a.AxisType, a.Mode, a.EnableLimitSet,
            a.ForwardLimitValue, a.NegativeLimitValue, a.RotationPeriod,
            a.IsVirtual, a.IsContour, a.OutputDev, a.InputDev, a.SlaveName, a.Row)).ToList()
            ?? [];

        if (edits is null || edits.Count == 0)
        {
            string message = hasAxisFile
                ? $"Axis configuration of '{Path.GetFileName(hcppPath)}' read: {axes.Count} axis definition(s) " +
                  $"(group '{document!.AxesGroupName ?? "AxesGroup"}'). Structure-level view only: the axis.xml " +
                  "schema is reverse-engineered and the vendor consumption of the values is unverified. " +
                  "No edits were applied."
                : $"No axis project: '{Path.GetFileName(hcppPath)}' contains no xml\\axis.xml member " +
                  "(a normal H3U-style project has no axis configuration); the axis list is empty and no " +
                  "edits can be applied.";
            await LogAsync("axis_config", true, message, null, fullDir).ConfigureAwait(false);
            return new AutoShopAxisConfigReport(true, "Experimental", fullDir, hcppPath, hasAxisFile, axes, [],
                false, true, null, null, message, disclaimer, LogFile);
        }

        if (!hasAxisFile)
        {
            string message = $"No axis project: '{Path.GetFileName(hcppPath)}' contains no xml\\axis.xml " +
                "member (a normal H3U-style project has no axis configuration); the requested axis edits " +
                "cannot be applied. Nothing was written.";
            await LogAsync("axis_config", false, message, null, fullDir).ConfigureAwait(false);
            return new AutoShopAxisConfigReport(false, "Unsupported", fullDir, hcppPath, false, axes, [],
                false, false, null, null, message, disclaimer, LogFile);
        }

        // Apply the edits ("AxisName.Field" -> new value) through the F2 pipeline.
        var applied = new List<AutoShopAxisEdit>();
        if (!AxisPackageEditor.TryEditAxisAndRepackage(hcppBytes, doc =>
        {
            foreach ((string key, object? value) in edits)
            {
                int dot = key.IndexOf('.');
                if (dot <= 0 || dot == key.Length - 1)
                {
                    throw new ArgumentException(
                        $"Axis edit key '{key}' must be '<axisName>.<field>' (e.g. 'X1.ForwardLimitValue').");
                }
                string axisName = key[..dot];
                string field = key[(dot + 1)..];
                AxisDefinition? axis = doc.Axes.FirstOrDefault(
                    a => string.Equals(a.Name, axisName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException(
                        $"No axis named '{axisName}' in the axis configuration. Available axes: " +
                        string.Join(", ", doc.Axes.Select(a => a.Name ?? "(unnamed)")) + ".");
                ApplyAxisField(axis, field, value);
                applied.Add(new AutoShopAxisEdit(axisName, field, value));
            }
        }, out byte[]? repacked, out string? editError) || repacked is null)
        {
            string message = $"The axis edits could not be applied: {editError}";
            await LogAsync("axis_config", false, message, null, fullDir).ConfigureAwait(false);
            return new AutoShopAxisConfigReport(false, "Unsupported", fullDir, hcppPath, true, axes, [],
                false, false, null, null, message, disclaimer, LogFile);
        }

        await File.WriteAllBytesAsync(hcppPath, repacked, cancellationToken).ConfigureAwait(false);

        // Readback verification: the repacked container parses, the axis member carries the edit.
        (bool roundTripOk, string? roundTripError) = VerifyAxisRoundTrip(repacked, applied);

        string? openOutcome = null;
        string openSuffix = string.Empty;
        if (open && roundTripOk)
        {
            AutoShopOpenProjectReport openReport = await OpenProjectCoreAsync(fullDir, cancellationToken).ConfigureAwait(false);
            openOutcome = openReport.Outcome;
            openSuffix = openReport.Success
                ? $" Re-open validation: {openReport.Outcome} (pid {openReport.Pid}, title '{openReport.WindowTitle}')."
                : $" Re-open validation did NOT succeed (outcome: {openReport.Outcome}); success is not claimed for the open step.";
        }

        string ok = roundTripOk
            ? $"Axis configuration edited and repackaged into '{Path.GetFileName(hcppPath)}': " +
              $"{applied.Count} edit(s) applied ({string.Join(", ", applied.Select(e => $"{e.AxisName}.{e.Field}"))}); " +
              $"{axes.Count} axis definition(s). Round-trip verified offline (container re-parse + member readback). " +
              "Structure-level only: the axis schema is reverse-engineered and the vendor consumption of edited " +
              "values is unverified (no real H5U axis sample was available)." + openSuffix
            : $"Axis edits were written to '{Path.GetFileName(hcppPath)}' but the offline readback verification " +
              $"failed: {roundTripError}";
        await LogAsync("axis_config", roundTripOk, ok, null, fullDir).ConfigureAwait(false);
        return new AutoShopAxisConfigReport(roundTripOk, "Experimental", fullDir, hcppPath, true, axes, applied,
            true, roundTripOk, roundTripError, openOutcome, ok, disclaimer, LogFile);
    }

    private static void ApplyAxisField(AxisDefinition axis, string field, object? value)
    {
        switch (field)
        {
            case "Name": axis.Name = value as string; break;
            case "AxisType": axis.AxisType = value as string; break;
            case "OutputDev": axis.OutputDev = value as string; break;
            case "InputDev": axis.InputDev = value as string; break;
            case "SlaveName": axis.SlaveName = value as string; break;
            case "Mode": axis.Mode = AsNullableInt(value); break;
            case "Row": axis.Row = AsNullableInt(value); break;
            case "EnableLimitSet": axis.EnableLimitSet = AsNullableBool(value); break;
            case "IsVirtual": axis.IsVirtual = AsNullableBool(value); break;
            case "IsContour": axis.IsContour = AsNullableBool(value); break;
            case "ForwardLimitValue": axis.ForwardLimitValue = AsNullableDouble(value); break;
            case "NegativeLimitValue": axis.NegativeLimitValue = AsNullableDouble(value); break;
            case "RotationPeriod": axis.RotationPeriod = AsNullableDouble(value); break;
            default:
                throw new KeyNotFoundException(
                    $"Unknown axis field '{field}'. Supported fields: Name, AxisType, OutputDev, InputDev, " +
                    "SlaveName (string); Mode, Row (int); EnableLimitSet, IsVirtual, IsContour (bool); " +
                    "ForwardLimitValue, NegativeLimitValue, RotationPeriod (double).");
        }
    }

    private static int? AsNullableInt(object? value) => value switch
    {
        null or JsonElement { ValueKind: JsonValueKind.Null } => null,
        int i => i,
        long l => checked((int)l),
        double d => checked((int)d),
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetInt32(),
        string s when int.TryParse(s, out int parsed) => parsed,
        _ => throw new ArgumentException($"Axis field expects an integer value; got '{value}'.")
    };

    private static bool? AsNullableBool(object? value) => value switch
    {
        null or JsonElement { ValueKind: JsonValueKind.Null } => null,
        bool b => b,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        string s when bool.TryParse(s, out bool parsed) => parsed,
        _ => throw new ArgumentException($"Axis field expects a boolean value; got '{value}'.")
    };

    private static double? AsNullableDouble(object? value) => value switch
    {
        null or JsonElement { ValueKind: JsonValueKind.Null } => null,
        double d => d,
        int i => i,
        long l => l,
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
        string s when double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out double parsed) => parsed,
        _ => throw new ArgumentException($"Axis field expects a numeric value; got '{value}'.")
    };

    private static (bool Ok, string? Error) VerifyAxisRoundTrip(byte[] repacked, IReadOnlyList<AutoShopAxisEdit> applied)
    {
        if (!AxisPackageEditor.TryVerify(repacked, out string? axisXml, out string? mainXml, out string? error))
        {
            return (false, error);
        }
        if (string.IsNullOrWhiteSpace(axisXml))
        {
            return (false, "the repacked container no longer carries an axis.xml member.");
        }
        if (string.IsNullOrWhiteSpace(mainXml))
        {
            return (false, "the repacked container's main record no longer decrypts (main XML lost).");
        }
        foreach (AutoShopAxisEdit edit in applied)
        {
            if (edit.NewValue is null)
            {
                continue;
            }
            if (!axisXml.Contains(edit.AxisName, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"the axis '{edit.AxisName}' is missing from the repacked axis.xml.");
            }
        }
        return (true, null);
    }

    // ------------------------------------------------------------------ stop

    /// <summary>
    /// Stops the owned experiment instance (identity re-verified) and recovers any
    /// journaled instance from a previous crash. Called on server shutdown.
    /// </summary>
    public async Task<InjectStopResult> StopOwnedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InjectStopResult sessionStop = await StopSessionCoreAsync(cancellationToken).ConfigureAwait(false);
            var launcher = new InjectLauncher(BuildLauncherOptions(null));
            InjectStopResult journalStop = await launcher.StopFromJournalAsync(cancellationToken).ConfigureAwait(false);
            return new InjectStopResult(
                sessionStop.Success && journalStop.Success,
                sessionStop.Entries.Concat(journalStop.Entries).ToList(),
                $"{sessionStop.Details} {journalStop.Details}", DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ internals

    private async Task<InjectStopResult> StopSessionCoreAsync(CancellationToken cancellationToken)
    {
        AutoShopInjectSession? session;
        lock (_stateLock)
        {
            session = _session;
            _session = null;
        }
        if (session is null)
        {
            return new InjectStopResult(true, [],
                "No opened AutoShop instance to stop. Instances not started by this server are never touched.",
                DateTimeOffset.UtcNow);
        }
        InjectStopResult stop = await session.Launcher.StopAsync(cancellationToken).ConfigureAwait(false);
        await LogAsync("stop_owned", stop.Success, stop.Details, session.Pid, session.ProjectDir).ConfigureAwait(false);
        return stop;
    }

    private InjectLauncherOptions BuildLauncherOptions(string? autoShopArguments) => new()
    {
        AutoShopExecutablePath = _options.AutoShopExecutablePath,
        InjectorExecutablePath = _options.InjectorExecutablePath,
        BridgeDllPath = _options.BridgeDllPath,
        OwnedPidJournalPath = _options.OwnedPidJournalPath,
        PipeName = _options.PipeName,
        AutoShopArguments = autoShopArguments,
        InjectorTimeout = _options.InjectorTimeout,
        StopTimeout = _options.StopTimeout,
        Gate = new InjectBridgeGateOptions { CompileEntryHint = _options.CompileEntryHint },
        FileExists = _options.FileExists
    };

    private string? ValidateProjectDirectory(string? path, bool allowMissing = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "projectDir is required and cannot be empty.";
        }
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return $"projectDir is not a valid path: {ex.Message}";
        }
        if (!Path.IsPathRooted(full))
        {
            return $"projectDir must be an absolute path: '{path}'.";
        }
        if (full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length <= 3)
        {
            return "projectDir must not be a drive root.";
        }
        string? installDir = Path.GetDirectoryName(_options.AutoShopExecutablePath);
        if (!string.IsNullOrEmpty(installDir) &&
            full.StartsWith(installDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return $"projectDir must not be inside the AutoShop installation directory ({installDir}); " +
                "AutoShop writes BackUP/Temp/config.ini next to the project and the install tree must stay untouched.";
        }
        if (!Directory.Exists(full))
        {
            if (allowMissing)
            {
                return null; // generate creates it
            }
            return $"projectDir does not exist: '{full}'.";
        }
        return null;
    }

    /// <summary>
    /// Validates a source .hcpp FILE path: absolute and existing. The source is only ever
    /// READ (never written), so a vendor template inside the installation directory is an
    /// allowed input; all writes go to the project directory, which the projectDir
    /// validation keeps outside the install tree.
    /// </summary>
    private string? ValidateSourceFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "sourceHcpp is required and cannot be empty.";
        }
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return $"sourceHcpp is not a valid path: {ex.Message}";
        }
        if (!Path.IsPathRooted(full))
        {
            return $"sourceHcpp must be an absolute path: '{path}'.";
        }
        if (!File.Exists(full))
        {
            return $"sourceHcpp does not exist: '{full}'.";
        }
        return null;
    }

    private sealed record PrepareResult(string? HcpPath, bool Fresh, string Summary, string? Error);

    /// <summary>
    /// Prepares the .hcp to open. When a .hcpp source exists it is re-unpacked into fresh
    /// bytes (the .hcp is decrypted in place on every open — a stale copy may be consumed);
    /// a lone .hcp is used as-is with an honest note.
    /// </summary>
    private PrepareResult PrepareHcp(string fullDir)
    {
        string[] hcppFiles = SafeGetFiles(fullDir, "*.hcpp");
        string[] hcpFiles = SafeGetFiles(fullDir, "*.hcp");
        if (hcppFiles.Length > 1)
        {
            return new PrepareResult(null, false, "no prepare.",
                $"Ambiguous project directory: {hcppFiles.Length} .hcpp sources found; expected one.");
        }

        if (hcppFiles.Length == 1)
        {
            byte[] sourceBytes = File.ReadAllBytes(hcppFiles[0]);
            if (!HcppReader.TryRead(sourceBytes, out HcppContainer? container, out string? error) || container is null)
            {
                return new PrepareResult(null, false, "no prepare.", $"The .hcpp source could not be parsed: {error}");
            }
            if (container.MainRecord?.MainBlob is not { } mainBlob)
            {
                return new PrepareResult(null, false, "no prepare.", "The .hcpp source carries no main record.");
            }
            // The .hcp FILE name is only a path handed to AutoShop.exe (the container's main
            // record name is internal data). The injector converts paths through ANSI, so the
            // on-disk name is forced ASCII-safe; non-ASCII project names keep "project.hcp".
            string hcpPath = Path.Combine(fullDir, AsciiSafeHcpName(container.MainRecord.MainProjectName ?? "project.hcp"));
            File.WriteAllBytes(hcpPath, mainBlob);
            List<(string Name, byte[] Content)> records = UnpackRecords(container);
            foreach ((string name, byte[] content) in records)
            {
                if (IsUnsafeMemberName(name))
                {
                    return new PrepareResult(null, false, "no prepare.",
                        $"The .hcpp declares a member name that would escape the project directory: '{name}'.");
                }
                WriteMemberFile(fullDir, name, content);
            }
            return new PrepareResult(hcpPath, true,
                $"Fresh .hcp re-unpacked from '{Path.GetFileName(hcppFiles[0])}': '{Path.GetFileName(hcpPath)}' " +
                $"({mainBlob.Length} B) + {records.Count} file record(s) written byte-faithfully (31-byte headers preserved).",
                null);
        }

        if (hcpFiles.Length == 1)
        {
            return new PrepareResult(hcpFiles[0], false,
                $"Using the existing '{Path.GetFileName(hcpFiles[0])}' as-is. NOTE: AutoShop decrypts the .hcp in place " +
                "when it opens a project; this is not a guaranteed fresh copy. Place a .hcpp source next to it for one.",
                null);
        }

        if (hcpFiles.Length > 1)
        {
            return new PrepareResult(null, false, "no prepare.",
                $"Ambiguous project directory: {hcpFiles.Length} .hcp files found; expected one.");
        }

        return new PrepareResult(null, false, "no prepare.",
            "The project directory contains neither a .hcp nor a .hcpp file; nothing can be opened. " +
            "Use plc_autoshop_generate_project to construct one.");
    }

    /// <summary>
    /// Byte-faithful file extraction (E3 §7.1): a bare file-name record paired with the next
    /// content record yields the record's FULL data (header included); a combined record
    /// yields its own content.
    /// </summary>
    private static List<(string Name, byte[] Content)> UnpackRecords(HcppContainer container)
    {
        var files = new List<(string, byte[])>();
        IReadOnlyList<HcppRecord> records = container.Records;
        for (int i = 0; i < records.Count; i++)
        {
            HcppRecord rec = records[i];
            switch (rec.Kind)
            {
                case HcppRecordKind.CombinedFile when rec.FileName is not null && rec.FileContent is not null:
                    files.Add((rec.FileName, rec.FileContent));
                    break;
                case HcppRecordKind.FileName when rec.FileName is not null && i + 1 < records.Count:
                    HcppRecord next = records[i + 1];
                    if (next.Kind is HcppRecordKind.FileContent or HcppRecordKind.Unknown)
                    {
                        // FULL record data (the 31-byte AutoShop header belongs to the disk file).
                        files.Add((rec.FileName, next.Data));
                        i++;
                    }
                    break;
            }
        }
        return files;
    }

    /// <summary>
    /// Path-safety rule for declared container members: RELATIVE SUBDIRECTORY names are
    /// allowed (the F2 axis layout stores the axis configuration at xml\axis.xml INSIDE
    /// the unpacked project directory); what is never allowed is anything that could
    /// escape the project directory: rooted/absolute paths, ".." traversal, drive or
    /// alternate-data-stream colons.
    /// </summary>
    private static bool IsUnsafeMemberName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }
        if (Path.IsPathRooted(name))
        {
            return true;
        }
        if (name.Contains(".."))
        {
            return true;
        }
        if (name.Contains(':'))
        {
            return true;
        }
        return false;
    }

    private static void WriteMemberFile(string fullDir, string name, byte[] content)
    {
        string target = Path.Combine(fullDir, name);
        string? parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        File.WriteAllBytes(target, content);
    }

    private static (bool Ok, string? Error) VerifyRoundTrip(byte[] hcppBytes, byte[] hcpBytes)
    {
        if (!HcppReader.TryRead(hcppBytes, out HcppContainer? container, out string? error) || container is null)
        {
            return (false, $"the generated .hcpp does not parse: {error}");
        }
        if (container.MainRecord?.MainBlob is not { } mainBlob)
        {
            return (false, "the generated .hcpp carries no main record.");
        }
        if (!mainBlob.AsSpan().SequenceEqual(hcpBytes))
        {
            return (false, "the generated .hcp does not match the container main blob.");
        }
        if (!AutoShopProjectCrypt.TryDecryptProjectXml(mainBlob, out string? xml, out string? decryptError) || xml is null)
        {
            return (false, $"the generated .hcp does not decrypt to project XML: {decryptError}");
        }
        try
        {
            XElement root = XElement.Parse(xml);
            if (root.Name.LocalName != "project")
            {
                return (false, $"the decrypted XML root is <{root.Name.LocalName}>, expected <project>.");
            }
            if (!uint.TryParse(root.Element("Version")?.Value, out uint version) || version > 1)
            {
                return (false, "the decrypted XML Version is missing or > 1 (AutoShop refuses to open it).");
            }
        }
        catch (System.Xml.XmlException ex)
        {
            return (false, $"the decrypted XML does not parse: {ex.Message}");
        }
        return (true, null);
    }

    /// <summary>Builds the minimal project XML following the measured Template A1 structure.</summary>
    private static string BuildProjectXml(
        string projectName, string model, string hardwareFile, string? description,
        IReadOnlyList<HcppFileSpec> files)
    {
        string now = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var entries = new List<(int Id, string Name)>();
        int nextId = 1;
        foreach (HcppFileSpec spec in files)
        {
            entries.Add((nextId++, spec.Name));
        }

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n");
        sb.Append("<project>\r\n");
        sb.Append("\t<Version>1</Version>\r\n");
        sb.Append("\t<ProjectDesc>").Append(Escape(description ?? projectName + " (generated)")).Append("</ProjectDesc>\r\n");
        sb.Append("\t<DefaultEditor>0</DefaultEditor>\r\n");
        sb.Append("\t<GCMModal>").Append(Escape(model)).Append("</GCMModal>\r\n");
        sb.Append("\t<CPUVersion>RT1.0</CPUVersion>\r\n");
        sb.Append("\t<Capbility>64000</Capbility>\r\n");
        sb.Append("\t<MaxFileID>").Append(entries.Count).Append("</MaxFileID>\r\n");
        sb.Append("\t<MaxSubID>1</MaxSubID>\r\n");
        sb.Append("\t<MaxIntID>1</MaxIntID>\r\n");
        sb.Append("\t<MaxMCID>0</MaxMCID>\r\n");
        sb.Append("\t<MaxCncID>0</MaxCncID>\r\n");
        sb.Append("\t<HardwareFile>").Append(Escape(hardwareFile)).Append("</HardwareFile>\r\n");
        sb.Append("\t<historycount>1</historycount>\r\n");
        sb.Append("\t<historyversions>\r\n\t<v0>\r\n\t<projnameex>").Append(Escape(projectName)).Append("</projnameex>\r\n");
        sb.Append("\t<asversion>").Append(Escape("generated by PlcMcp")).Append("</asversion>\r\n");
        sb.Append("\t<date>").Append(now).Append("</date>\r\n");
        sb.Append("\t<operate>版本变更</operate>\r\n");
        sb.Append("\t<lasttime>").Append(now).Append("</lasttime></v0></historyversions>\r\n");
        sb.Append("\t<TscFile></TscFile>\r\n");
        sb.Append("\t<AuthorityType>0</AuthorityType>\r\n");
        sb.Append("\t<AuthorityCode></AuthorityCode>\r\n");
        sb.Append("\t<VarAllocVersion>0</VarAllocVersion>\r\n");
        sb.Append("\t<ProjVersion>").Append(Escape("generated by PlcMcp")).Append("</ProjVersion>\r\n");
        sb.Append("\t<ProjPassword></ProjPassword>\r\n");
        sb.Append("\t<ProjEncodingFormat>GB2312</ProjEncodingFormat>\r\n");
        sb.Append("\t<HCPFileVer>1</HCPFileVer>\r\n");
        foreach ((int id, string name) in entries)
        {
            bool isProgram = name.EndsWith(".LD", StringComparison.OrdinalIgnoreCase);
            sb.Append("\t<file id=\"").Append(id).Append("\">\r\n");
            sb.Append("\t\t<FileType>").Append(isProgram ? "1" : "3").Append("</FileType>\r\n");
            sb.Append("\t\t<ProgType>").Append(isProgram ? "0" : "3").Append("</ProgType>\r\n");
            sb.Append("\t\t<Author></Author>\r\n");
            sb.Append("\t\t<Caption>").Append(Escape(Path.GetFileNameWithoutExtension(name))).Append("</Caption>\r\n");
            sb.Append("\t\t<Alias>").Append(Escape(Path.GetFileNameWithoutExtension(name))).Append("</Alias>\r\n");
            sb.Append("\t\t<CreateTime>").Append(now).Append("</CreateTime>\r\n");
            sb.Append("\t\t<FileName>").Append(Escape(name)).Append("</FileName>\r\n");
            sb.Append("\t\t<LastModifyTime>").Append(now).Append("</LastModifyTime>\r\n");
            sb.Append("\t\t<FileDesc></FileDesc>\r\n");
            sb.Append("\t\t<POUID>").Append(isProgram ? "0" : "-1").Append("</POUID>\r\n");
            sb.Append("\t\t<Timer>0</Timer>\r\n");
            sb.Append("\t\t<Encrypted>0</Encrypted>\r\n");
            sb.Append("\t\t<SysSTFunc>0</SysSTFunc>\r\n");
            sb.Append("\t\t<StbParentID>0</StbParentID>\r\n");
            sb.Append("\t\t<nStbRow>-1</nStbRow>\r\n");
            sb.Append("\t\t<nStbCol>-1</nStbCol>\r\n");
            sb.Append("\t</file>\r\n");
        }
        sb.Append("</project>\r\n");
        return sb.ToString();

        static string Escape(string value) => System.Security.SecurityElement.Escape(value);
    }

    /// <summary>
    /// ASCII-safe .hcp file name: the injector converts the AutoShop command line through
    /// ANSI (wide -> narrow -> byte-widening), so non-ASCII file names are mangled before
    /// AutoShop sees them (observed on 4.12.0.2: a "模板A.hcp" path arrived garbled and the
    /// app answered "已不存在或文件路径超出系统限制"). Non-ASCII names fall back to
    /// "project.hcp"; the container's internal main record name is unaffected.
    /// </summary>
    private static string AsciiSafeHcpName(string name)
    {
        string candidate = name.EndsWith(".hcp", StringComparison.OrdinalIgnoreCase) ? name : name + ".hcp";
        bool asciiSafe = candidate.All(c => c is > (char)0x20 and < (char)0x7F);
        return asciiSafe ? candidate : "project.hcp";
    }

    private IReadOnlyList<AutoShopCompileArtifact> CollectArtifacts(string projectDir, Func<string, bool> isNewPredicate)
    {
        var artifacts = new List<AutoShopCompileArtifact>();
        string compileDir = Path.Combine(projectDir, "Compile");
        foreach (string file in SafeGetFiles(compileDir, "*.compile").Concat(SafeGetFiles(projectDir, "*.foid")).Concat(SafeGetFiles(projectDir, "*.hcpp")))
        {
            try
            {
                var info = new FileInfo(file);
                bool isNew = isNewPredicate(file);
                if (file.EndsWith(".compile", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    byte[] plain = AutoShopProjectCrypt.DecryptBlob(bytes);
                    var head = new StringBuilder();
                    foreach (byte b in plain.Take(16))
                    {
                        head.Append(b.ToString("X2"));
                    }
                    artifacts.Add(new AutoShopCompileArtifact(
                        Path.GetFileName(file), info.Length, info.LastWriteTimeUtc, isNew,
                        DecryptedOk: true, head.ToString(), null));
                }
                else
                {
                    artifacts.Add(new AutoShopCompileArtifact(
                        Path.GetFileName(file), info.Length, info.LastWriteTimeUtc, isNew,
                        DecryptedOk: false, null, "not a .compile artifact; no decryption attempted"));
                }
            }
            catch (Exception ex) when (ex is IOException or System.Security.SecurityException)
            {
                artifacts.Add(new AutoShopCompileArtifact(
                    Path.GetFileName(file), 0, DateTimeOffset.MinValue, isNewPredicate(file),
                    DecryptedOk: false, null, $"unreadable: {ex.Message}"));
            }
        }
        return artifacts.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private AutoShopWindowSnapshot? ProbeWindows(int pid)
    {
        if (_options.WindowProbe is not null)
        {
            return _options.WindowProbe(pid);
        }
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            var locator = new Win32WindowLocator();
            var reader = new Win32TextReader();
            var frames = locator.Find(new WindowQuery(pid, AutoShopUiaMap.MainWindowClassPrefix, null, true));
            var dialogs = locator.Find(new WindowQuery(pid, "#32770", null, true));
            // The finder enumerates children too: every Static-class window of the pid is a
            // dialog label candidate (failure evidence for the four-state mapping).
            var dialogTexts = locator.Find(new WindowQuery(pid, "Static", null, true))
                .Select(w => reader.ReadWindowText(w.Handle))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Cast<string>()
                .ToList();
            return new AutoShopWindowSnapshot(
                frames.Select(w => w.Title).Where(t => !string.IsNullOrWhiteSpace(t)).ToList(),
                dialogs.Select(w => w.Title).Where(t => !string.IsNullOrWhiteSpace(t)).ToList(),
                dialogTexts);
        }
        catch
        {
            return null; // window evidence unavailable: the classifier keeps polling
        }
    }

    private static string? LastFrameTitle(AutoShopWindowSnapshot? snapshot) =>
        snapshot?.MainFrameTitles.Count > 0 ? snapshot.MainFrameTitles[^1] : null;

    private static string[] SafeGetFiles(string dir, string pattern)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : [];
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or ArgumentException)
        {
            return [];
        }
    }

    private async Task LogAsync(string action, bool success, string message, int? pid, string? projectDir)
    {
        try
        {
            Directory.CreateDirectory(_stateDirectory);
            var entry = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                action,
                success,
                pid,
                projectDir,
                message
            };
            await File.AppendAllTextAsync(LogFile, JsonSerializer.Serialize(entry, LogJsonOptions) + Environment.NewLine).ConfigureAwait(false);
        }
        catch
        {
            // Logging is best-effort; it must never turn a result into a fake success.
        }
    }

    /// <summary>One opened AutoShop experiment instance owned by this server.</summary>
    public sealed record AutoShopInjectSession(
        InjectLauncher Launcher,
        int Pid,
        string PipeName,
        string ProjectDir,
        string HcpPath,
        InjectBridgeServices Services,
        DateTimeOffset OpenedAt);
}
