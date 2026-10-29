using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlcMcp.Server.Governance;

/// <summary>
/// Snapshot of one process matching the AutoShop H5U simulator daemon process name.
/// </summary>
public sealed record AutoShopSimulatorProcess(int Pid, string ProcessName, string? ExePath);

/// <summary>
/// Minimal process-management seam behind the AutoShop simulator tools.
///
/// TODO(P2): replace the default <see cref="SystemProcessDriver"/> with P2's
/// <c>SimulatorLauncher</c> interface once it is delivered in
/// src/PlcMcp.Engineering/Workers/AutoShop (start/stop with vendor lifecycle
/// handling). Until then this minimal driver is the only process path; every
/// stop goes through PID ownership protection below.
/// </summary>
public interface IAutoShopSimulatorProcessDriver
{
    /// <summary>Lists processes whose name matches (case-insensitive) the given name.</summary>
    IReadOnlyList<AutoShopSimulatorProcess> FindProcesses(string processName);

    /// <summary>Starts an executable with a working directory; returns the PID, or null with an error message.</summary>
    int? Start(string exePath, string workingDirectory, out string? error);

    /// <summary>Reports whether a process with the given PID is alive.</summary>
    bool IsAlive(int pid);

    /// <summary>Main executable path of the process, or null when unavailable (e.g. access denied).</summary>
    string? GetExecutablePath(int pid);

    /// <summary>Terminates the process; returns true only when the process exited.</summary>
    bool TryKill(int pid, out string? error);
}

/// <summary>Default driver over System.Diagnostics; used only for the local simulator daemon.</summary>
public sealed class SystemProcessDriver : IAutoShopSimulatorProcessDriver
{
    public IReadOnlyList<AutoShopSimulatorProcess> FindProcesses(string processName)
    {
        try
        {
            Process[] processes = Process.GetProcessesByName(processName);
            try
            {
                return processes
                    .Select(p => new AutoShopSimulatorProcess(p.Id, p.ProcessName, SafeExePath(p)))
                    .ToArray();
            }
            finally
            {
                foreach (Process p in processes) p.Dispose();
            }
        }
        catch
        {
            return [];
        }
    }

    public int? Start(string exePath, string workingDirectory, out string? error)
    {
        error = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process? process = Process.Start(startInfo);
            if (process is null)
            {
                error = "Process.Start returned null.";
                return null;
            }
            using (process)
            {
                return process.Id;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public bool IsAlive(int pid)
    {
        try
        {
            using Process? process = Process.GetProcessById(pid);
            return process is not null && !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public string? GetExecutablePath(int pid)
    {
        try
        {
            using Process? process = Process.GetProcessById(pid);
            return process?.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    public bool TryKill(int pid, out string? error)
    {
        error = null;
        try
        {
            Process? process = Process.GetProcessById(pid);
            if (process is null) return true;
            using (process)
            {
                if (process.HasExited) return true;
                process.Kill(entireProcessTree: true);
                return process.WaitForExit(5000) && process.HasExited;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? SafeExePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Injectable options for the AutoShop H5U software simulator daemon (h5u_daemon.exe).
/// Every probe (filesystem, TCP, process management) is injectable so tests run fully offline.
/// All probes are loopback-only by construction: there is no user-facing host parameter, and the
/// probe helper rejects any non-loopback host.
/// </summary>
public sealed class AutoShopSimulatorOptions
{
    /// <summary>Candidate h5u_daemon.exe locations (defaults match the V4.12.0.2 simulator layout).</summary>
    public IReadOnlyList<string> DaemonPathCandidates { get; init; } = new[]
    {
        @"D:\Inovance Control\AutoShop\Simulator\h5u_daemon.exe",
        @"C:\Inovance Control\AutoShop\Simulator\h5u_daemon.exe",
        @"C:\Program Files (x86)\Inovance Control\AutoShop\Simulator\h5u_daemon.exe"
    };

    /// <summary>Process name of the H5U software simulator daemon.</summary>
    public string ProcessName { get; init; } = "h5u_daemon";

    /// <summary>Modbus TCP port the daemon listens on (loopback probe target; verified on 4.12.0.2).</summary>
    public int ModbusPort { get; init; } = 502;

    /// <summary>AutoShop native protocol port the daemon listens on (loopback probe target; verified on 4.12.0.2).</summary>
    public int NativePort { get; init; } = 12939;

    /// <summary>Loopback host used for every probe; non-loopback hosts are rejected.</summary>
    public string LoopbackHost { get; init; } = "127.0.0.1";

    /// <summary>Bounded verification window after start (process alive + Modbus endpoint listening).</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Poll interval during the bounded start verification.</summary>
    public TimeSpan StartPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Overrides <c>File.Exists</c> for daemon path candidates (tests).</summary>
    public Func<string, bool>? FileExists { get; init; }

    /// <summary>Probes loopback TCP reachability: (host, port) -> listening. Default: real connect to 127.0.0.1.</summary>
    public Func<string, int, Task<bool>>? TcpProbe { get; init; }
}

/// <summary>Read-only discovery report for the local H5U simulator daemon.</summary>
public sealed record AutoShopSimulatorStatusReport(
    string DaemonPath,
    bool DaemonFilePresent,
    bool Running,
    IReadOnlyList<int> RunningPids,
    IReadOnlyList<int> OwnedPids,
    int ModbusPort,
    int NativePort,
    bool ModbusPortListening,
    bool NativePortListening,
    string Summary,
    string Disclaimer);

/// <summary>Result of a simulator start/stop action. Failure is never reported as success.</summary>
public sealed record AutoShopSimulatorActionResult(
    string Action,
    bool Success,
    bool Started,
    bool AlreadyRunning,
    bool Stopped,
    int? Pid,
    string Message,
    string? LogPath,
    string Disclaimer);

/// <summary>
/// Governance facade for the local AutoShop H5U software simulator daemon (h5u_daemon.exe).
///
/// Security posture:
/// - simulator start/stop are local Windows process operations (not PLC actions), so they need
///   no approval token; PLC download/write paths remain nonexistent in this server;
/// - duplicate-start protection: when a daemon is already running, no second instance is started;
/// - stop honors PID ownership protection: only PIDs recorded by this server's own start
///   (with identity re-verified against process name and the recorded executable path) may be
///   stopped; there is no bare taskkill of arbitrary processes;
/// - every action is logged to an on-disk JSONL file; start verification is bounded by a
///   timeout and a failed start is never reported as success;
/// - all probes are loopback-only (127.0.0.1); any non-loopback host is rejected.
/// </summary>
public sealed class AutoShopSimulatorServices
{
    public const string DisclaimerText =
        "The h5u_daemon simulator endpoints were identified by reverse engineering; there is no vendor endorsement. " +
        "Version-locked to AutoShop 4.12.0.2. Simulator start/stop manage a local Windows process only and never " +
        "contact, write to, force, download to, or compile a PLC.";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AutoShopSimulatorOptions _options;
    private readonly IAutoShopSimulatorProcessDriver _driver;
    private readonly string _stateDirectory;

    public AutoShopSimulatorServices(
        string stateDirectory,
        AutoShopSimulatorOptions? options = null,
        IAutoShopSimulatorProcessDriver? driver = null)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _options = options ?? new AutoShopSimulatorOptions();
        _driver = driver ?? new SystemProcessDriver();
    }

    public string StateDirectory => _stateDirectory;
    public string OwnershipFile => Path.Combine(_stateDirectory, "ownership.json");
    public string LogFile => Path.Combine(_stateDirectory, "simulator.log");
    public AutoShopSimulatorOptions Options => _options;

    /// <summary>Read-only discovery: daemon process, ownership and loopback endpoint state.</summary>
    public async Task<AutoShopSimulatorStatusReport> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processes = await Task.Run(() => _driver.FindProcesses(_options.ProcessName), cancellationToken).ConfigureAwait(false);
        var runningPids = processes.Select(p => p.Pid).OrderBy(x => x).ToArray();
        var owned = (await ReadOwnershipAsync(prune: true, cancellationToken).ConfigureAwait(false))
            .Select(e => e.Pid).OrderBy(x => x).ToArray();
        bool modbusListening = await ProbeLoopbackAsync(_options.ModbusPort, cancellationToken).ConfigureAwait(false);
        bool nativeListening = await ProbeLoopbackAsync(_options.NativePort, cancellationToken).ConfigureAwait(false);
        (string daemonPath, bool daemonPresent) = ResolveDaemonPath();

        string summary = $"h5u_daemon running: {processes.Count > 0} ({string.Join(", ", runningPids)}); " +
            $"daemon executable present: {daemonPresent} ({daemonPath ?? "none found"}); " +
            $"owned by this server: [{string.Join(", ", owned)}]; " +
            $"loopback 127.0.0.1:{_options.ModbusPort} (Modbus TCP) listening: {modbusListening}; " +
            $"loopback 127.0.0.1:{_options.NativePort} (AutoShop native) listening: {nativeListening}.";

        return new AutoShopSimulatorStatusReport(
            DaemonPath: daemonPath ?? string.Empty,
            DaemonFilePresent: daemonPresent,
            Running: processes.Count > 0,
            RunningPids: runningPids,
            OwnedPids: owned,
            ModbusPort: _options.ModbusPort,
            NativePort: _options.NativePort,
            ModbusPortListening: modbusListening,
            NativePortListening: nativeListening,
            Summary: summary,
            Disclaimer: DisclaimerText);
    }

    /// <summary>
    /// Starts the local simulator daemon when none is running (duplicate-start protection),
    /// then verifies within a bounded window that the process stays alive and the Modbus
    /// loopback endpoint starts listening. Failure is never reported as success.
    /// </summary>
    public async Task<AutoShopSimulatorActionResult> StartAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var running = await Task.Run(() => _driver.FindProcesses(_options.ProcessName), cancellationToken).ConfigureAwait(false);
            if (running.Count > 0)
            {
                var owned = await ReadOwnershipAsync(prune: true, cancellationToken).ConfigureAwait(false);
                int pid = running[0].Pid;
                bool ours = owned.Any(e => e.Pid == pid);
                string message = $"h5u_daemon already running (pid {pid}, owner: {(ours ? "this server" : "external process")}); " +
                    "no second instance started (duplicate-start protection).";
                await LogAsync("start", pid, success: true, message).ConfigureAwait(false);
                return new AutoShopSimulatorActionResult("start", Success: true, Started: false, AlreadyRunning: true,
                    Stopped: false, Pid: pid, Message: message, LogPath: LogFile, Disclaimer: DisclaimerText);
            }

            (string? exePath, _) = ResolveDaemonPath();
            if (exePath is null)
            {
                string message = $"h5u_daemon.exe not found at candidate paths ({string.Join("; ", _options.DaemonPathCandidates)}); " +
                    "AutoShop simulator is not installed at the known locations; nothing was started.";
                await LogAsync("start", null, success: false, message).ConfigureAwait(false);
                return new AutoShopSimulatorActionResult("start", Success: false, Started: false, AlreadyRunning: false,
                    Stopped: false, Pid: null, Message: message, LogPath: LogFile, Disclaimer: DisclaimerText);
            }

            string workingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
            string? startError = null;
            int? startedPid = await Task.Run(() => _driver.Start(exePath, workingDirectory, out startError), cancellationToken)
                .ConfigureAwait(false);
            if (startedPid is null)
            {
                string message = $"Failed to start h5u_daemon.exe: {startError ?? "unknown error"}. Success is not claimed.";
                await LogAsync("start", null, success: false, message).ConfigureAwait(false);
                return new AutoShopSimulatorActionResult("start", Success: false, Started: false, AlreadyRunning: false,
                    Stopped: false, Pid: null, Message: message, LogPath: LogFile, Disclaimer: DisclaimerText);
            }

            int pidValue = startedPid.Value;
            // Record ownership immediately so stop can always clean up a process we launched.
            await AddOwnershipAsync(pidValue, exePath, cancellationToken).ConfigureAwait(false);

            DateTimeOffset deadline = DateTimeOffset.UtcNow + _options.StartTimeout;
            bool processAlive = true;
            bool listening = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_driver.IsAlive(pidValue))
                {
                    processAlive = false;
                    break;
                }
                if (await ProbeLoopbackAsync(_options.ModbusPort, cancellationToken).ConfigureAwait(false))
                {
                    listening = true;
                    break;
                }
                if (DateTimeOffset.UtcNow + _options.StartPollInterval >= deadline) break;
                await Task.Delay(_options.StartPollInterval, cancellationToken).ConfigureAwait(false);
            }

            if (!processAlive)
            {
                string message = $"h5u_daemon process (pid {pidValue}) exited immediately after start; the Modbus loopback " +
                    $"endpoint was never verified; start failed (not reported as success). Ownership entry is kept for cleanup.";
                await LogAsync("start", pidValue, success: false, message).ConfigureAwait(false);
                return new AutoShopSimulatorActionResult("start", Success: false, Started: true, AlreadyRunning: false,
                    Stopped: false, Pid: pidValue, Message: message, LogPath: LogFile, Disclaimer: DisclaimerText);
            }
            if (!listening)
            {
                string message = $"h5u_daemon process (pid {pidValue}) is alive but 127.0.0.1:{_options.ModbusPort} did not " +
                    $"start listening within {_options.StartTimeout.TotalSeconds:0.#} s; success is not claimed; the process " +
                    "was started by this server and can be stopped by action=stop.";
                await LogAsync("start", pidValue, success: false, message).ConfigureAwait(false);
                return new AutoShopSimulatorActionResult("start", Success: false, Started: true, AlreadyRunning: false,
                    Stopped: false, Pid: pidValue, Message: message, LogPath: LogFile, Disclaimer: DisclaimerText);
            }

            string ok = $"h5u_daemon started (pid {pidValue}); loopback 127.0.0.1:{_options.ModbusPort} (Modbus TCP) is listening; " +
                $"127.0.0.1:{_options.NativePort} (AutoShop native) is the expected second endpoint; PID recorded for stop ownership. " +
                "This is a local software simulator process, not a physical PLC.";
            await LogAsync("start", pidValue, success: true, ok).ConfigureAwait(false);
            return new AutoShopSimulatorActionResult("start", Success: true, Started: true, AlreadyRunning: false,
                Stopped: false, Pid: pidValue, Message: ok, LogPath: LogFile, Disclaimer: DisclaimerText);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Stops a simulator process previously started by this server (PID ownership protection).
    /// Processes owned by external callers (including a manually started daemon) are never stopped.
    /// </summary>
    public async Task<AutoShopSimulatorActionResult> StopAsync(int? requestedPid, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read unpruned so "started by this server but already exited" is distinguishable
            // from "never started by this server".
            var entries = await ReadOwnershipAsync(prune: false, cancellationToken).ConfigureAwait(false);
            OwnershipEntry entry;
            if (requestedPid is not null)
            {
                entry = entries.FirstOrDefault(e => e.Pid == requestedPid.Value);
                if (entry is null)
                {
                    string message = $"PID {requestedPid.Value} was not started by this server (ownership registry: " +
                        $"{(entries.Count == 0 ? "empty" : string.Join(", ", entries.Select(e => e.Pid)))}); refusing to stop a " +
                        "process this server does not own (PID ownership protection; no bare taskkill).";
                    await LogAsync("stop", requestedPid, success: false, message).ConfigureAwait(false);
                    return new AutoShopSimulatorActionResult("stop", Success: false, Started: false, AlreadyRunning: false,
                        Stopped: false, Pid: requestedPid, Message: message, LogPath: LogFile, Disclaimer: DisclaimerText);
                }
            }
            else
            {
                var aliveEntries = new List<OwnershipEntry>();
                foreach (OwnershipEntry e in entries)
                {
                    if (await Task.Run(() => _driver.IsAlive(e.Pid), cancellationToken).ConfigureAwait(false))
                        aliveEntries.Add(e);
                }
                if (entries.Count > 0 && aliveEntries.Count == 0)
                {
                    await WriteOwnershipAsync([], cancellationToken).ConfigureAwait(false);
                    string message = "All simulator processes previously started by this server have already exited; " +
                        "stale ownership entries removed.";
                    await LogAsync("stop", null, success: true, message).ConfigureAwait(false);
                    return new AutoShopSimulatorActionResult("stop", Success: true, Started: false, AlreadyRunning: false,
                        Stopped: true, Pid: null, Message: message, LogPath: LogFile, Disclaimer: DisclaimerText);
                }
                if (aliveEntries.Count == 0)
                {
                    string message = "No simulator processes were started by this server (ownership registry is empty); " +
                        "refusing to stop anything. Use action=status to discover externally started instances.";
                    await LogAsync("stop", null, success: false, message).ConfigureAwait(false);
                    return new AutoShopSimulatorActionResult("stop", Success: false, Started: false, AlreadyRunning: false,
                        Stopped: false, Pid: null, Message: message, LogPath: LogFile, Disclaimer: DisclaimerText);
                }
                if (aliveEntries.Count > 1)
                {
                    string message = $"Multiple simulator processes were started by this server ({string.Join(", ", aliveEntries.Select(e => e.Pid))}); " +
                        "pass an explicit integer 'pid' for action=stop.";
                    await LogAsync("stop", null, success: false, message).ConfigureAwait(false);
                    return new AutoShopSimulatorActionResult("stop", Success: false, Started: false, AlreadyRunning: false,
                        Stopped: false, Pid: null, Message: message, LogPath: LogFile, Disclaimer: DisclaimerText);
                }
                entry = aliveEntries[0];
            }

            if (!_driver.IsAlive(entry.Pid))
            {
                await RemoveOwnershipAsync(entry.Pid, cancellationToken).ConfigureAwait(false);
                string message = $"Simulator process (pid {entry.Pid}) already exited; stale ownership entry removed.";
                await LogAsync("stop", entry.Pid, success: true, message).ConfigureAwait(false);
                return new AutoShopSimulatorActionResult("stop", Success: true, Started: false, AlreadyRunning: false,
                    Stopped: true, Pid: entry.Pid, Message: message, LogPath: LogFile, Disclaimer: DisclaimerText);
            }

            // Identity re-verification before any termination: the PID must still be the daemon
            // process name and still run the recorded executable (protection against PID reuse).
            var running = await Task.Run(() => _driver.FindProcesses(_options.ProcessName), cancellationToken).ConfigureAwait(false);
            if (!running.Any(p => p.Pid == entry.Pid))
            {
                string message = $"PID {entry.Pid} is no longer an '{_options.ProcessName}' process; refusing to stop " +
                    "(PID ownership protection).";
                await LogAsync("stop", entry.Pid, success: false, message).ConfigureAwait(false);
                return new AutoShopSimulatorActionResult("stop", Success: false, Started: false, AlreadyRunning: false,
                    Stopped: false, Pid: entry.Pid, Message: message, LogPath: LogFile, Disclaimer: DisclaimerText);
            }
            string? actualPath = await Task.Run(() => _driver.GetExecutablePath(entry.Pid), cancellationToken).ConfigureAwait(false);
            if (actualPath is not null && !PathsEqual(actualPath, entry.ExePath))
            {
                string message = $"PID {entry.Pid} now runs '{actualPath}' which does not match the recorded simulator " +
                    $"executable '{entry.ExePath}' (possible PID reuse); refusing to stop (PID ownership protection).";
                await LogAsync("stop", entry.Pid, success: false, message).ConfigureAwait(false);
                return new AutoShopSimulatorActionResult("stop", Success: false, Started: false, AlreadyRunning: false,
                    Stopped: false, Pid: entry.Pid, Message: message, LogPath: LogFile, Disclaimer: DisclaimerText);
            }

            string? killError = null;
            bool killed = await Task.Run(() =>
            {
                bool ok = _driver.TryKill(entry.Pid, out var error);
                killError = error;
                return ok;
            }, cancellationToken).ConfigureAwait(false);
            if (!killed)
            {
                string message = $"Failed to stop simulator process (pid {entry.Pid}): {killError ?? "process did not exit"}; " +
                    "ownership entry is kept; success is not claimed.";
                await LogAsync("stop", entry.Pid, success: false, message).ConfigureAwait(false);
                return new AutoShopSimulatorActionResult("stop", Success: false, Started: false, AlreadyRunning: false,
                    Stopped: false, Pid: entry.Pid, Message: message, LogPath: LogFile, Disclaimer: DisclaimerText);
            }

            await RemoveOwnershipAsync(entry.Pid, cancellationToken).ConfigureAwait(false);
            string stopped = $"Simulator process stopped (pid {entry.Pid}); ownership entry removed.";
            await LogAsync("stop", entry.Pid, success: true, stopped).ConfigureAwait(false);
            return new AutoShopSimulatorActionResult("stop", Success: true, Started: false, AlreadyRunning: false,
                Stopped: true, Pid: entry.Pid, Message: stopped, LogPath: LogFile, Disclaimer: DisclaimerText);
        }
        finally
        {
            Gate.Release();
        }
    }

    private (string? Path, bool Present) ResolveDaemonPath()
    {
        foreach (string candidate in _options.DaemonPathCandidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            bool exists = _options.FileExists?.Invoke(candidate) ?? SafeFileExists(candidate);
            if (exists) return (candidate, true);
        }
        return (null, false);
    }

    private async Task<bool> ProbeLoopbackAsync(int port, CancellationToken cancellationToken)
    {
        string host = _options.LoopbackHost;
        if (!IsLoopbackHost(host))
            throw new InvalidOperationException("AutoShop simulator probes are restricted to loopback endpoints.");
        if (_options.TcpProbe is not null)
            return await _options.TcpProbe(host, port).ConfigureAwait(false);
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsLoopbackHost(string host) => host switch
    {
        "127.0.0.1" or "::1" => true,
        _ when host.StartsWith("127.", StringComparison.Ordinal) => true,
        _ when host.Equals("localhost", StringComparison.OrdinalIgnoreCase) => true,
        _ => false
    };

    private async Task<List<OwnershipEntry>> ReadOwnershipAsync(bool prune, CancellationToken cancellationToken)
    {
        if (!File.Exists(OwnershipFile)) return [];
        OwnershipState? state;
        try
        {
            state = JsonSerializer.Deserialize<OwnershipState>(await File.ReadAllTextAsync(OwnershipFile, cancellationToken).ConfigureAwait(false), StateJsonOptions);
        }
        catch
        {
            // A corrupt registry must not silently become "nothing owned": report empty so stop
            // refuses to act; the raw file stays on disk for manual inspection.
            return [];
        }
        var entries = state?.Entries ?? [];
        if (!prune) return entries;
        var alive = new List<OwnershipEntry>();
        foreach (OwnershipEntry e in entries)
        {
            if (await Task.Run(() => _driver.IsAlive(e.Pid), cancellationToken).ConfigureAwait(false))
                alive.Add(e);
        }
        if (alive.Count != entries.Count)
            await WriteOwnershipAsync(alive, cancellationToken).ConfigureAwait(false);
        return alive;
    }

    private async Task AddOwnershipAsync(int pid, string exePath, CancellationToken cancellationToken)
    {
        var entries = await ReadOwnershipAsync(prune: true, cancellationToken).ConfigureAwait(false);
        if (entries.Any(e => e.Pid == pid)) return;
        entries.Add(new OwnershipEntry(pid, exePath, DateTimeOffset.UtcNow));
        await WriteOwnershipAsync(entries, cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveOwnershipAsync(int pid, CancellationToken cancellationToken)
    {
        var entries = await ReadOwnershipAsync(prune: false, cancellationToken).ConfigureAwait(false);
        await WriteOwnershipAsync(entries.Where(e => e.Pid != pid).ToList(), cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteOwnershipAsync(List<OwnershipEntry> entries, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_stateDirectory);
        var state = new OwnershipState(entries);
        await File.WriteAllTextAsync(OwnershipFile, JsonSerializer.Serialize(state, StateJsonOptions), cancellationToken).ConfigureAwait(false);
    }

    private async Task LogAsync(string action, int? pid, bool success, string message)
    {
        try
        {
            Directory.CreateDirectory(_stateDirectory);
            var entry = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                action,
                pid,
                success,
                message
            };
            await File.AppendAllTextAsync(LogFile, JsonSerializer.Serialize(entry, StateJsonOptions) + Environment.NewLine).ConfigureAwait(false);
        }
        catch
        {
            // Logging is best-effort; it must never turn a result into a fake success.
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record OwnershipState([property: JsonPropertyName("entries")] List<OwnershipEntry> Entries);

    private sealed record OwnershipEntry(
        [property: JsonPropertyName("pid")] int Pid,
        [property: JsonPropertyName("exePath")] string ExePath,
        [property: JsonPropertyName("launchedAtUtc")] DateTimeOffset LaunchedAtUtc);
}
