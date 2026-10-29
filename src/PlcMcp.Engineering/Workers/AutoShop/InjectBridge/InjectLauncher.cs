using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PlcMcp.Engineering.Workers.AutoShop.InjectBridge;

/// <summary>
/// Options for <see cref="InjectLauncher"/>. Everything the launcher touches is
/// injectable so tests run fully offline; the default paths match the native
/// build output of tools/AutoShopInjectBridge/build.cmd.
/// </summary>
public sealed class InjectLauncherOptions
{
    public const string DefaultAutoShopPath = @"D:\Inovance Control\AutoShop\AutoShop.exe";
    public const string DefaultInjectorPath = @"%LOCALAPPDATA%\AutoShopInjectBridge-run\AutoShopInjectBridgeInjector.exe";
    public const string DefaultBridgeDllPath = @"%LOCALAPPDATA%\AutoShopInjectBridge-run\AutoShopInjectBridge.dll";

    public InjectLauncherOptions()
    {
        InjectorExecutablePath = Environment.ExpandEnvironmentVariables(DefaultInjectorPath);
        BridgeDllPath = Environment.ExpandEnvironmentVariables(DefaultBridgeDllPath);
        OwnedPidJournalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "plcmcp-handoff", "inject-bridge", "owned-instances.jsonl");
        if (Environment.GetEnvironmentVariable("TEMP") is { Length: > 0 } temp)
        {
            OwnedPidJournalPath = Path.Combine(temp, "plcmcp-handoff", "inject-bridge", "owned-instances.jsonl");
        }
    }

    /// <summary>AutoShop executable that will be started (and validated) by the injector.</summary>
    public string AutoShopExecutablePath { get; init; } = DefaultAutoShopPath;

    /// <summary>Path of the native injector (VS2022 v143 x86, build.cmd output).</summary>
    public string InjectorExecutablePath { get; init; }

    /// <summary>Path of the bridge DLL handed to the injector.</summary>
    public string BridgeDllPath { get; init; }

    /// <summary>JSONL ownership journal (PID list on disk for cleanup).</summary>
    public string OwnedPidJournalPath { get; init; }

    /// <summary>Pipe name configured for the injected bridge.</summary>
    public string PipeName { get; init; } = InjectBridgeProtocol.PipeName;

    /// <summary>Extra AutoShop command line (e.g. a project path) passed through.</summary>
    public string? AutoShopArguments { get; init; }

    public TimeSpan InjectorTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>C# gate applied to every client created by this launcher.</summary>
    public InjectBridgeGateOptions Gate { get; init; } = new();

    // ---- injectable seams (tests) ------------------------------------------

    /// <summary>Overrides File.Exists probes.</summary>
    public Func<string, bool>? FileExists { get; init; }

    /// <summary>
    /// Runs the injector: (command) -> run result. The default implementation
    /// spawns the injector executable with redirected output.
    /// </summary>
    public Func<InjectInjectorCommand, InjectInjectorRunResult>? InjectorRunner { get; init; }

    /// <summary>
    /// Live process lookup: (pid) -> identity or null when gone/inaccessible.
    /// The default uses System.Diagnostics.Process (name, start time, image path).
    /// </summary>
    public Func<int, InjectLiveProcessIdentity?>? LiveProcessLookup { get; init; }

    /// <summary>Kills a verified process tree: (pid) -> killed. Default: Process.Kill(entireProcessTree:true).</summary>
    public Func<int, bool>? ProcessTreeKiller { get; init; }

    /// <summary>Creates the pipe client used for the post-injection handshake (tests).</summary>
    public Func<InjectBridgeClientOptions, IInjectBridgeHandshakeProbe>? HandshakeProbeFactory { get; init; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(AutoShopExecutablePath) || !Path.IsPathRooted(AutoShopExecutablePath))
        {
            throw new ArgumentException("AutoShopExecutablePath must be an absolute path.", nameof(AutoShopExecutablePath));
        }
        if (string.IsNullOrWhiteSpace(InjectorExecutablePath) || !Path.IsPathRooted(InjectorExecutablePath))
        {
            throw new ArgumentException("InjectorExecutablePath must be an absolute path.", nameof(InjectorExecutablePath));
        }
        if (string.IsNullOrWhiteSpace(BridgeDllPath) || !Path.IsPathRooted(BridgeDllPath))
        {
            throw new ArgumentException("BridgeDllPath must be an absolute path.", nameof(BridgeDllPath));
        }
        if (string.IsNullOrWhiteSpace(OwnedPidJournalPath) || !Path.IsPathRooted(OwnedPidJournalPath))
        {
            throw new ArgumentException("OwnedPidJournalPath must be an absolute path.", nameof(OwnedPidJournalPath));
        }
        if (!string.Equals(Path.GetFileName(AutoShopExecutablePath), "AutoShop.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "AutoShopExecutablePath must point at AutoShop.exe: the injector refuses any other image name.", nameof(AutoShopExecutablePath));
        }
        Gate.Validate();
    }
}

/// <summary>Minimal handshake probe seam for the launcher (tests inject mocks).</summary>
public interface IInjectBridgeHandshakeProbe
{
    Task<InjectBridgeHandshakeReport> HandshakeAsync(string clientName = "PlcMcp", CancellationToken cancellationToken = default);
}

/// <summary>
/// Starts, health-checks and cleanly stops **self-started** AutoShop instances
/// with the inject bridge loaded (tools/AutoShopInjectBridge).
///
/// Red lines encoded here:
/// - the AutoShop process is only ever created BY the injector (suspended) and
///   injected before its main thread resumes; there is no "inject into
///   existing PID" path anywhere in this stack;
/// - every started PID is journaled (owned-instances.jsonl) together with the
///   exe path and creation time;
/// - StopAsync re-verifies the live identity (process name + exe path +
///   creation time) of every journaled instance and REFUSES to kill on any
///   mismatch (PID reuse, foreign process, user-interactive instance);
/// - a failed start cleans up the instance it just created;
/// - no registry writes, no installation-directory writes; the only network
///   path is the local named pipe.
/// </summary>
public sealed class InjectLauncher
{
    private readonly InjectLauncherOptions _options;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<InjectInjectorCommand, InjectInjectorRunResult> _injectorRunner;
    private readonly Func<int, InjectLiveProcessIdentity?> _liveProcessLookup;
    private readonly Func<int, bool> _treeKiller;
    private readonly Func<InjectBridgeClientOptions, IInjectBridgeHandshakeProbe>? _handshakeProbeFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly List<InjectOwnedInstanceRecord> _owned = [];
    private const string ToleranceNote = "creation-time tolerance 10s";

    public InjectLauncher(InjectLauncherOptions? options = null)
    {
        _options = options ?? new InjectLauncherOptions();
        _options.Validate();
        _fileExists = _options.FileExists ?? (path => File.Exists(path));
        _injectorRunner = _options.InjectorRunner ?? DefaultInjectorRunner;
        _liveProcessLookup = _options.LiveProcessLookup ?? DefaultLiveProcessLookup;
        _treeKiller = _options.ProcessTreeKiller ?? DefaultTreeKiller;
        _handshakeProbeFactory = _options.HandshakeProbeFactory;
    }

    public InjectLauncherOptions Options => _options;

    /// <summary>Instances started by this launcher and not yet stopped.</summary>
    public IReadOnlyList<InjectOwnedInstanceRecord> OwnedInstances
    {
        get { lock (_stateLock) { return _owned.ToArray(); } }
    }

    /// <summary>Checks that the injector, bridge DLL and AutoShop exe are present.</summary>
    public InjectDiscoveryResult Discover()
    {
        bool injector = _fileExists(_options.InjectorExecutablePath);
        bool dll = _fileExists(_options.BridgeDllPath);
        bool exe = _fileExists(_options.AutoShopExecutablePath);
        string details = $"injector={injector} ({_options.InjectorExecutablePath}); bridgeDll={dll} " +
                         $"({_options.BridgeDllPath}); autoShop={exe} ({_options.AutoShopExecutablePath}).";
        if (!injector || !dll || !exe)
        {
            details += " Build the native artifacts with tools\\AutoShopInjectBridge\\build.cmd and check the paths.";
        }
        return new InjectDiscoveryResult(injector && dll && exe, injector, dll, exe, details);
    }

    /// <summary>
    /// Starts an independent AutoShop instance with the bridge injected and
    /// verifies the pipe handshake. On any failure after the process was
    /// created, the instance this launcher started is stopped again.
    /// </summary>
    public async Task<InjectStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InjectDiscoveryResult discovery = Discover();
            if (!discovery.Ready)
            {
                return Failed(null, "discovery", discovery.Details, null);
            }

            // The AutoShop command line is passed through verbatim by the injector
            // (command = quoted exe + " " + extraArgs). To survive both argv hops
            // (C# -> injector, injector -> CreateProcessW) the value is delivered as
            // "\"<extra>\"" so the final AutoShop command line quotes paths with
            // spaces correctly.
            string arguments =
                $"launch --exe \"{_options.AutoShopExecutablePath}\" --dll \"{_options.BridgeDllPath}\" " +
                $"--pidfile \"{_options.OwnedPidJournalPath}\" --pipe \"{_options.PipeName}\" " +
                (_options.AutoShopArguments is { Length: > 0 } extra ? $"--args \"\\\"{extra}\\\"\" " : string.Empty) +
                $"--allow-write {(_options.Gate.AllowElementWrite ? 1 : 0)} " +
                $"--allow-force {(_options.Gate.AllowForce ? 1 : 0)} " +
                $"--allow-download {(_options.Gate.AllowDownload ? 1 : 0)} " +
                CompileRvaArgument();

            InjectInjectorRunResult run = _injectorRunner(
                new InjectInjectorCommand("launch", arguments, _options.InjectorTimeout));
            if (run.Error != null)
            {
                return Failed(null, "injector-run", $"Injector could not run: {run.Error}", run.Error);
            }
            if (run.ExitCode != 0)
            {
                return Failed(null, "injector-exit",
                    $"Injector exited {run.ExitCode}: {LastStdoutLine(run) ?? run.StandardError}",
                    run.StandardError);
            }

            if (!TryParseLastJsonLine(run.StandardOutput, out JsonDocument? eventDoc) || eventDoc == null)
            {
                return Failed(null, "injector-output",
                    "Injector produced no parsable JSON event line.", run.StandardOutput);
            }
            using (eventDoc)
            {
                if (eventDoc.RootElement.TryGetProperty("event", out JsonElement ev) &&
                    ev.GetString() != "launched")
                {
                    string reason = eventDoc.RootElement.TryGetProperty("reason", out JsonElement r)
                        ? r.GetString() ?? "(no reason)"
                        : "(no reason)";
                    return Failed(null, "injector-failed", $"Injector reported failure: {reason}", run.StandardOutput);
                }
                if (!eventDoc.RootElement.TryGetProperty("pid", out JsonElement pidElement))
                {
                    return Failed(null, "injector-output", "Injector event has no pid.", run.StandardOutput);
                }
                int pid = pidElement.GetInt32();
                bool pipeReady = eventDoc.RootElement.TryGetProperty("pipeReady", out JsonElement pr) &&
                                 pr.ValueKind == JsonValueKind.True;

                var record = new InjectOwnedInstanceRecord(
                    "launch", pid, "AutoShop.exe",
                    _options.AutoShopExecutablePath, _options.BridgeDllPath,
                    _options.PipeName,
                    eventDoc.RootElement.TryGetProperty("startedUtc", out JsonElement su)
                        ? su.GetString() ?? string.Empty
                        : string.Empty);
                if (record.StartedUtc.Length == 0)
                {
                    // The injector's stdout launch event carries no startedUtc; its journal
                    // record does (written before the event). Recover it so the in-memory
                    // stop verification (creation-time check) can pass for this instance.
                    InjectOwnedInstanceRecord? journaled = ReadJournal().LastOrDefault(
                        r => r.Event == "launch" && r.Pid == pid);
                    if (journaled is not null)
                    {
                        record = record with { StartedUtc = journaled.StartedUtc };
                    }
                }
                lock (_stateLock)
                {
                    _owned.Add(record);
                }

                // Post-injection handshake through the pipe (loopback, read-only).
                string mismatch = string.Empty;
                InjectBridgeHandshakeReport handshake = await ProbeHandshakeAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!handshake.Connected || !handshake.Answered ||
                    !handshake.MatchesExpectation(out mismatch))
                {
                    // The gate is held by this StartAsync call; use the
                    // gate-free core so the cleanup cannot deadlock.
                    InjectStopResult cleanup = await StopCoreAsync(cancellationToken).ConfigureAwait(false);
                    return Failed(pid, "handshake",
                        $"Bridge handshake failed ({mismatch}); the started instance was stopped again " +
                        $"({cleanup.Entries.Count} cleanup entries).",
                        run.StandardOutput);
                }

                return new InjectStartResult(
                    InjectStartOutcome.StartedAndInjected, true, pid, pipeReady, _options.PipeName,
                    $"AutoShop instance pid {pid} started suspended, bridge injected, pipe " +
                    $"'{_options.PipeName}' {(pipeReady ? "ready" : "not observed ready")}, handshake answered " +
                    $"by worker '{handshake.WorkerName}' v{handshake.WorkerVersion}.",
                    null, DateTimeOffset.UtcNow);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops ONLY the instances this launcher started (journal + live identity
    /// re-verification). A PID whose live name, exe path or creation time does
    /// not match the record is refused and never killed.
    /// </summary>
    public async Task<InjectStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stop implementation WITHOUT gate acquisition (the gate must already be
    /// held by the caller — <see cref="StopAsync"/> or a StartAsync cleanup).
    /// </summary>
    private async Task<InjectStopResult> StopCoreAsync(CancellationToken cancellationToken = default)
    {
        List<InjectOwnedInstanceRecord> snapshot;
        lock (_stateLock)
        {
            snapshot = [.. _owned];
        }

        var entries = new List<InjectStopEntry>();
        foreach (InjectOwnedInstanceRecord record in snapshot)
        {
            entries.Add(await StopOwnedAsync(record).ConfigureAwait(false));
            lock (_stateLock)
            {
                _owned.Remove(record);
            }
        }

        bool success = entries.All(e => e.Outcome is InjectStopOutcome.Killed or InjectStopOutcome.AlreadyExited);
        string details = entries.Count == 0
            ? "No owned inject-bridge instance to stop. Instances not started by this launcher are never touched."
            : $"{entries.Count} owned instance(s) processed ({ToleranceNote}).";
        return new InjectStopResult(success, entries, details, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Stops the instances found in the journal file (crash recovery path):
    /// same identity re-verification, then tree kill, then a "stop" record.
    /// </summary>
    public Task<InjectStopResult> StopFromJournalAsync(CancellationToken cancellationToken = default)
    {
        List<InjectOwnedInstanceRecord> records = ReadJournal();
        var entries = new List<InjectStopEntry>();
        foreach (InjectOwnedInstanceRecord record in records.Where(r => r.Event == "launch"))
        {
            entries.Add(StopVerifiedAsync(record));
        }
        bool success = entries.All(e => e.Outcome is InjectStopOutcome.Killed or InjectStopOutcome.AlreadyExited);
        return Task.FromResult(new InjectStopResult(
            success, entries,
            entries.Count == 0 ? "Journal has no live launch records." : $"Journal stop processed {entries.Count} record(s).",
            DateTimeOffset.UtcNow));
    }

    /// <summary>Reads the ownership journal from disk (forensics/cleanup).</summary>
    public List<InjectOwnedInstanceRecord> ReadJournal()
    {
        var records = new List<InjectOwnedInstanceRecord>();
        try
        {
            if (!File.Exists(_options.OwnedPidJournalPath))
            {
                return records;
            }
            foreach (string line in File.ReadAllLines(_options.OwnedPidJournalPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                try
                {
                    InjectOwnedInstanceRecord? record = JsonSerializer.Deserialize<InjectOwnedInstanceRecord>(line);
                    if (record != null)
                    {
                        records.Add(record);
                    }
                }
                catch (JsonException)
                {
                    // skip malformed lines; the journal is append-only evidence
                }
            }
        }
        catch (IOException)
        {
            // unreadable journal: report empty rather than guessing
        }
        return records;
    }

    // ---- internals ---------------------------------------------------------

    private async Task<InjectStopEntry> StopOwnedAsync(InjectOwnedInstanceRecord record)
    {
        InjectStopEntry entry = await Task.Run(() => StopVerifiedAsync(record)).ConfigureAwait(false);
        AppendJournal(new InjectOwnedInstanceRecord(
            "stop", record.Pid, record.ProcessName, record.Exe, record.Dll, record.PipeName,
            record.StartedUtc));
        return entry;
    }

    private InjectStopEntry StopVerifiedAsync(InjectOwnedInstanceRecord record)
    {
        InjectLiveProcessIdentity? live = _liveProcessLookup(record.Pid);
        if (live == null)
        {
            return new InjectStopEntry(record.Pid, InjectStopOutcome.AlreadyExited,
                "Owned instance already exited; nothing was killed.");
        }

        bool sameName = string.Equals(live.ProcessName, "AutoShop", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(live.ProcessName + ".exe", record.ProcessName, StringComparison.OrdinalIgnoreCase);
        bool sameExe = string.IsNullOrEmpty(live.ExePath) ||
                       string.Equals(live.ExePath, record.Exe, StringComparison.OrdinalIgnoreCase);
        bool sameTime = DateTime.TryParse(record.StartedUtc,
                            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out DateTime recorded) &&
                        Math.Abs((live.StartTimeUtc - recorded).TotalSeconds) <= 10;
        if (!sameName || !sameExe || !sameTime)
        {
            return new InjectStopEntry(record.Pid, InjectStopOutcome.Refused,
                $"Ownership verification failed (name '{live.ProcessName}' vs '{record.ProcessName}', " +
                $"exe '{live.ExePath}' vs '{record.Exe}', start {live.StartTimeUtc:O} vs {record.StartedUtc}); " +
                "the PID may have been reused or replaced and was NOT killed.");
        }

        try
        {
            bool killed = _treeKiller(record.Pid);
            return killed
                ? new InjectStopEntry(record.Pid, InjectStopOutcome.Killed,
                    $"Killed owned instance pid {record.Pid} (entire process tree).")
                : new InjectStopEntry(record.Pid, InjectStopOutcome.StopFailed,
                    $"Kill was issued for pid {record.Pid} but the process did not exit in time.");
        }
        catch (Exception ex)
        {
            return new InjectStopEntry(record.Pid, InjectStopOutcome.Refused,
                $"Kill attempt on owned instance pid {record.Pid} failed: {ex.Message}");
        }
    }

    private async Task<InjectBridgeHandshakeReport> ProbeHandshakeAsync(CancellationToken cancellationToken)
    {
        var clientOptions = new InjectBridgeClientOptions
        {
            PipeName = _options.PipeName,
            Timeout = _options.HandshakeTimeout,
            Gate = _options.Gate
        };
        if (_handshakeProbeFactory != null)
        {
            IInjectBridgeHandshakeProbe probe = _handshakeProbeFactory(clientOptions);
            return await probe.HandshakeAsync("PlcMcp", cancellationToken).ConfigureAwait(false);
        }

        var services = new InjectBridgeServices(clientOptions);
        return await services.HandshakeAsync("PlcMcp", cancellationToken).ConfigureAwait(false);
    }

    private void AppendJournal(InjectOwnedInstanceRecord record)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_options.OwnedPidJournalPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.AppendAllText(_options.OwnedPidJournalPath,
                JsonSerializer.Serialize(record) + Environment.NewLine);
        }
        catch (IOException)
        {
            // journal best-effort; the in-memory list remains authoritative for this run
        }
    }

    private static InjectStartResult Failed(int? pid, string stage, string details, string? error) =>
        new(InjectStartOutcome.Failed, false, pid, false, null,
            $"Start failed at '{stage}': {details}", error, DateTimeOffset.UtcNow);

    private static string? LastStdoutLine(InjectInjectorRunResult run)
    {
        string[] lines = run.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : null;
    }

    private static bool TryParseLastJsonLine(string text, out JsonDocument? document)
    {
        document = null;
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            try
            {
                document = JsonDocument.Parse(lines[i]);
                return true;
            }
            catch (JsonException)
            {
                // try the next line up
            }
        }
        return false;
    }

    /// <summary>
    /// E3 compile-entry wiring: when the gate hint carries a hex address, the
    /// injector receives <c>--compile-rva &lt;decimal&gt;</c> so the native bridge
    /// pending config gets hasCompileEntry=true. The bridge STILL answers
    /// Unsupported until the calling convention and signature are verified
    /// (BridgePipeServer.cpp HandleCompile) — this argument only documents the
    /// intended configuration on the native side. The bridge's JsonExtractInt
    /// parses decimal only, so the hex token is converted to a decimal string.
    /// </summary>
    private string CompileRvaArgument()
    {
        string? hex = ExtractHexToken(_options.Gate.CompileEntryHint);
        if (hex is null)
        {
            return string.Empty;
        }
        if (!long.TryParse(hex[2..], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out long value) || value is <= 0 or > uint.MaxValue)
        {
            return string.Empty;
        }
        return $"--compile-rva {value.ToString(System.Globalization.CultureInfo.InvariantCulture)} ";
    }

    /// <summary>Extracts the first 0x-prefixed hex token from a free-text hint (null when absent).</summary>
    internal static string? ExtractHexToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var match = System.Text.RegularExpressions.Regex.Match(
            text, @"0x[0-9A-Fa-f]{1,8}", System.Text.RegularExpressions.RegexOptions.None);
        return match.Success ? match.Value : null;
    }

    private static InjectInjectorRunResult DefaultInjectorRunner(InjectInjectorCommand command)    {
        // The default runner resolves the injector path from a fresh default
        // options instance (the launcher options are not visible in a static
        // seam; tests inject their own runner anyway).
        var defaultOptions = new InjectLauncherOptions();
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = defaultOptions.InjectorExecutablePath,
                Arguments = command.Arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            process.Start();
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit((int)command.Timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new InjectInjectorRunResult(-1, stdout, stderr, "injector timed out");
            }
            return new InjectInjectorRunResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new InjectInjectorRunResult(-1, string.Empty, string.Empty, ex.Message);
        }
    }

    private static InjectLiveProcessIdentity? DefaultLiveProcessLookup(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            string? exePath = null;
            try
            {
                exePath = process.MainModule?.FileName;
            }
            catch
            {
                // access denied / exited: identity falls back to name+time
            }
            return new InjectLiveProcessIdentity(pid, process.ProcessName, exePath, process.StartTime.ToUniversalTime());
        }
        catch
        {
            return null; // gone or inaccessible: never guess
        }
    }

    private static bool DefaultTreeKiller(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            return process.WaitForExit(15000);
        }
        catch (InvalidOperationException)
        {
            return true; // already gone
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Discovery report of <see cref="InjectLauncher.Discover"/>.</summary>
public sealed record InjectDiscoveryResult(
    bool Ready,
    bool InjectorPresent,
    bool BridgeDllPresent,
    bool AutoShopPresent,
    string Details);
