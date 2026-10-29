using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PlcMcp.Engineering.Workers.AutoShop.Simulator;

/// <summary>
/// Options for <see cref="SimulatorLauncher"/>. All network endpoints are loopback-validated:
/// a non-loopback host is rejected at construction, encoding the "experiments on 127.0.0.1
/// only" red line into the code itself.
/// </summary>
public sealed class SimulatorLauncherOptions
{
    public const string DefaultExecutablePath = @"D:\Inovance Control\AutoShop\Simulator\h5u_daemon.exe";

    /// <summary>Vendor daemon executable. It must be started with the Simulator directory as working directory.</summary>
    public string ExecutablePath { get; init; } = DefaultExecutablePath;

    /// <summary>Expected process name (without extension); used to verify PID ownership before a kill.</summary>
    public string ProcessName { get; init; } = "h5u_daemon";

    /// <summary>Host used for TCP health probes. Loopback only.</summary>
    public string HealthHost { get; init; } = "127.0.0.1";

    public int ModbusPort { get; init; } = 502;
    public int NativePort { get; init; } = 12939;
    public int HealthTimeoutMs { get; init; } = 2000;
    public int StartupTimeoutMs { get; init; } = 15000;
    public int PollIntervalMs { get; init; } = 250;
    public int StopTimeoutMs { get; init; } = 10000;

    internal void Validate()
    {
        EnsureLoopbackHost(HealthHost, nameof(HealthHost));
        if (ExecutablePath is not { Length: > 0 } || !Path.IsPathRooted(ExecutablePath))
            throw new ArgumentException("ExecutablePath must be an absolute path to h5u_daemon.exe.", nameof(ExecutablePath));
        if (string.IsNullOrWhiteSpace(ProcessName))
            throw new ArgumentException("ProcessName must not be empty.", nameof(ProcessName));
        ValidatePort(ModbusPort, nameof(ModbusPort));
        ValidatePort(NativePort, nameof(NativePort));
        if (HealthTimeoutMs is < 50 or > 30000)
            throw new ArgumentException("HealthTimeoutMs must be between 50 and 30000 ms.", nameof(HealthTimeoutMs));
        if (StartupTimeoutMs < PollIntervalMs)
            throw new ArgumentException("StartupTimeoutMs must be at least PollIntervalMs.", nameof(StartupTimeoutMs));
        if (PollIntervalMs is < 10 or > 5000)
            throw new ArgumentException("PollIntervalMs must be between 10 and 5000 ms.", nameof(PollIntervalMs));
        if (StopTimeoutMs is < 100 or > 60000)
            throw new ArgumentException("StopTimeoutMs must be between 100 and 60000 ms.", nameof(StopTimeoutMs));
    }

    internal static void ValidatePort(int port, string paramName)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentException("TCP port must be between 1 and 65535.", paramName);
    }

    internal static void EnsureLoopbackHost(string host, string paramName)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Host must not be empty.", paramName);
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return;
        if (IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address))
            return;
        throw new ArgumentException(
            $"Host '{host}' is not a loopback address. The simulator automation layer only ever talks to 127.0.0.1/localhost/::1.", paramName);
    }
}

/// <summary>
/// Discovers, starts, health-checks and cleanly stops the AutoShop H5U simulator daemon
/// (h5u_daemon.exe). Facts the launcher relies on (verified 2026-10-24 against the live
/// vendor binary): the daemon runs WITHOUT the AutoShop UI and without elevation, it must
/// be started with the Simulator directory as its working directory, and it listens on
/// 127.0.0.1:502 (Modbus TCP) and 127.0.0.1:12939 (AutoShop native protocol).
///
/// Ownership safety: only processes started by this launcher instance are ever killed.
/// Each started process is recorded with PID, process name and creation timestamp; StopAsync
/// re-verifies all three from the live process handle and REFUSES to kill on any mismatch
/// (e.g. PID reuse or a foreign daemon). A daemon that was already running before this
/// launcher was created is reported as "foreign" and never terminated.
/// All probes are injectable; tests run fully offline.
/// </summary>
public sealed class SimulatorLauncher
{
    private readonly SimulatorLauncherOptions _options;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<SimulatorProcessStartSpec, ISimulatorProcess> _processStarter;
    private readonly Func<string, int, TimeSpan, CancellationToken, Task<bool>> _tcpProbe;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly List<OwnedInstance> _owned = [];
    private readonly object _outputLock = new();
    private readonly List<string> _recentOutput = [];

    private sealed record OwnedInstance(ISimulatorProcess Process, int ProcessId, string ProcessName, DateTime StartTimeUtc);

    public SimulatorLauncher(
        SimulatorLauncherOptions? options = null,
        Func<string, bool>? fileExists = null,
        Func<SimulatorProcessStartSpec, ISimulatorProcess>? processStarter = null,
        Func<string, int, TimeSpan, CancellationToken, Task<bool>>? tcpProbe = null)
    {
        _options = options ?? new SimulatorLauncherOptions();
        _options.Validate();
        _fileExists = fileExists ?? (path => File.Exists(path));
        _processStarter = processStarter ?? DefaultStartProcess;
        _tcpProbe = tcpProbe ?? DefaultTcpProbeAsync;
    }

    public SimulatorLauncherOptions Options => _options;

    /// <summary>Last stdout/stderr lines captured from instances started by this launcher (bounded ring).</summary>
    public IReadOnlyList<string> RecentOutput
    {
        get { lock (_outputLock) { return _recentOutput.ToArray(); } }
    }

    /// <summary>Checks whether the vendor daemon executable exists and reports its working directory.</summary>
    public SimulatorDiscoveryResult Discover()
    {
        bool exists;
        try
        {
            exists = _fileExists(_options.ExecutablePath);
        }
        catch (Exception ex)
        {
            return new SimulatorDiscoveryResult(false, null, null,
                $"Executable probe failed for '{_options.ExecutablePath}': {ex.Message}");
        }

        string? workingDirectory = exists ? Path.GetDirectoryName(_options.ExecutablePath) : null;
        string details = exists
            ? $"Simulator daemon found at '{_options.ExecutablePath}'; required working directory '{workingDirectory}'."
            : $"Simulator daemon not found at '{_options.ExecutablePath}'. Install AutoShop (H5U online simulator component) or point " +
              $"{nameof(SimulatorLauncherOptions.ExecutablePath)} at the existing h5u_daemon.exe.";
        return new SimulatorDiscoveryResult(exists, exists ? _options.ExecutablePath : null, workingDirectory, details);
    }

    /// <summary>
    /// Health check per the acceptance definition: TCP connect on both loopback ports
    /// (Modbus 502 and native 12939) plus liveness of owned instances.
    /// </summary>
    public async Task<SimulatorHealthReport> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromMilliseconds(_options.HealthTimeoutMs);
        bool modbus = await _tcpProbe(_options.HealthHost, _options.ModbusPort, timeout, cancellationToken).ConfigureAwait(false);
        bool native = await _tcpProbe(_options.HealthHost, _options.NativePort, timeout, cancellationToken).ConfigureAwait(false);
        int? ownedPid = GetAliveOwnedProcessId();
        bool healthy = modbus && native;
        string details =
            $"Modbus 127.0.0.1:{_options.ModbusPort} reachable: {modbus}; native 127.0.0.1:{_options.NativePort} reachable: {native}; " +
            (ownedPid is { } pid ? $"owned instance alive (pid {pid})." : "no owned instance alive.") +
            (healthy ? "" : " Overall health requires BOTH ports to be connectable.");
        return new SimulatorHealthReport(modbus, native, ownedPid.HasValue, ownedPid, healthy, details, DateTimeOffset.UtcNow);
    }

    /// <summary>Starts the daemon if no healthy simulator is already listening; reuses otherwise.</summary>
    public Task<SimulatorStartResult> EnsureRunningAsync(CancellationToken cancellationToken = default) => StartAsync(cancellationToken);

    public async Task<SimulatorStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OwnedInstance? aliveOwned = GetAliveOwnedInstance();
            if (aliveOwned is not null)
            {
                return new SimulatorStartResult(SimulatorStartOutcome.ReusedOwnedInstance, true, true,
                    ToInfo(aliveOwned),
                    $"Owned simulator instance (pid {aliveOwned.ProcessId}) is still running; start skipped.",
                    null, DateTimeOffset.UtcNow);
            }

            SimulatorHealthReport health = await CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            if (health.OverallHealthy)
            {
                return new SimulatorStartResult(SimulatorStartOutcome.ReusedForeignInstance, true, false, null,
                    $"A simulator is already listening on {_options.HealthHost}:{_options.ModbusPort}/" +
                    $"{_options.NativePort} and was NOT started by this launcher; it is reused for probing but never stopped by this launcher.",
                    null, DateTimeOffset.UtcNow);
            }

            SimulatorDiscoveryResult discovery = Discover();
            if (!discovery.Found)
            {
                return new SimulatorStartResult(SimulatorStartOutcome.Failed, false, false, null, discovery.Details,
                    "Simulator executable not found.", DateTimeOffset.UtcNow);
            }

            ISimulatorProcess process;
            try
            {
                process = _processStarter(new SimulatorProcessStartSpec(discovery.ExecutablePath!, discovery.WorkingDirectory!));
            }
            catch (Exception ex)
            {
                return new SimulatorStartResult(SimulatorStartOutcome.Failed, false, false, null,
                    $"Failed to start '{discovery.ExecutablePath}': {ex.Message}", ex.ToString(), DateTimeOffset.UtcNow);
            }

            var owned = new OwnedInstance(process, process.Id, process.ProcessName, process.StartTimeUtc);
            lock (_stateLock)
            {
                _owned.Add(owned);
            }

            long startedAt = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds < _options.StartupTimeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                health = await CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                if (health.OverallHealthy)
                {
                    return new SimulatorStartResult(SimulatorStartOutcome.StartedNewProcess, true, true, ToInfo(owned),
                        $"Simulator daemon started (pid {owned.ProcessId}) with working directory '{discovery.WorkingDirectory}'; " +
                        $"both ports answered after {Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F0} ms.",
                        null, DateTimeOffset.UtcNow);
                }

                await Task.Delay(_options.PollIntervalMs, cancellationToken).ConfigureAwait(false);
            }

            // Ports never opened: the instance we started is ours to clean up.
            SimulatorStopEntry cleanup = await StopOwnedInstanceAsync(owned).ConfigureAwait(false);
            return new SimulatorStartResult(SimulatorStartOutcome.Failed, false, false, ToInfo(owned),
                $"Simulator daemon started (pid {owned.ProcessId}) but ports {_options.ModbusPort}/{_options.NativePort} did not open " +
                $"within {_options.StartupTimeoutMs} ms; the instance this launcher started was stopped ({cleanup.Outcome}).",
                $"Startup health check never turned healthy. Recent output: {string.Join(" | ", RecentOutput.Take(8))}",
                DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops ONLY the instances this launcher started. For each owned instance the live
    /// process name, PID and creation timestamp are re-verified against the record taken at
    /// start; any mismatch (PID reuse, renamed process, stale record) is refused and never killed.
    /// A daemon not owned by this launcher is left untouched.
    /// </summary>
    public async Task<SimulatorStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<OwnedInstance> snapshot;
            lock (_stateLock)
            {
                snapshot = [.. _owned];
            }

            var entries = new List<SimulatorStopEntry>();
            int killed = 0;
            foreach (OwnedInstance instance in snapshot)
            {
                SimulatorStopEntry entry = await StopOwnedInstanceAsync(instance).ConfigureAwait(false);
                if (entry.Outcome == SimulatorStopOutcome.Killed)
                {
                    killed++;
                }

                entries.Add(entry);
                lock (_stateLock)
                {
                    _owned.Remove(instance);
                }
            }

            bool? portsFreed = null;
            string portNote = "no owned instance was stopped";
            if (killed > 0)
            {
                SimulatorHealthReport after = await CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                portsFreed = !after.ModbusPortReachable && !after.NativePortReachable;
                portNote = portsFreed.Value
                    ? "both loopback ports are free after stop"
                    : $"ports {_options.ModbusPort}/{_options.NativePort} still answer after stop (a daemon this launcher did not start is still listening; it was left untouched)";
            }

            bool success = entries.All(e => e.Outcome is SimulatorStopOutcome.Killed or SimulatorStopOutcome.AlreadyExited);
            string details = entries.Count == 0
                ? $"No owned simulator instance to stop. A daemon already listening on {_options.HealthHost}:{_options.ModbusPort}/" +
                  $"{_options.NativePort} is NOT owned by this launcher and was left untouched by design."
                : $"{entries.Count} owned instance(s) processed; {portNote}.";
            return new SimulatorStopResult(success, entries, portsFreed, details, DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- internals -------------------------------------------------------

    private async Task<SimulatorStopEntry> StopOwnedInstanceAsync(OwnedInstance instance)
    {
        ISimulatorProcess process = instance.Process;
        if (process.HasExited)
        {
            process.Dispose();
            return new SimulatorStopEntry(instance.ProcessId, SimulatorStopOutcome.AlreadyExited,
                "Owned instance already exited; nothing was killed.");
        }

        // Ownership re-verification: live identity must match the record taken at start time.
        string liveName = process.ProcessName;
        DateTime liveStart = process.StartTimeUtc;
        int livePid = process.Id;
        bool sameName = string.Equals(liveName, instance.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(liveName, _options.ProcessName, StringComparison.OrdinalIgnoreCase);
        bool sameTime = liveStart != DateTime.MinValue &&
                        Math.Abs((liveStart - instance.StartTimeUtc).TotalSeconds) <= 2;
        bool samePid = livePid == instance.ProcessId;
        if (!sameName || !sameTime || !samePid)
        {
            return new SimulatorStopEntry(livePid, SimulatorStopOutcome.Refused,
                "Ownership verification failed " +
                $"(name '{liveName}' vs '{instance.ProcessName}'/'{_options.ProcessName}', " +
                $"start {liveStart:O} vs {instance.StartTimeUtc:O}, pid {livePid} vs {instance.ProcessId}); " +
                "the process may have been reused or replaced and was NOT killed.");
        }

        try
        {
            process.Kill(entireProcessTree: true);
            bool exited = process.WaitForExit(_options.StopTimeoutMs);
            process.Dispose();
            return exited
                ? new SimulatorStopEntry(instance.ProcessId, SimulatorStopOutcome.Killed,
                    $"Killed owned instance pid {instance.ProcessId} (entire process tree) and waited for exit.")
                : new SimulatorStopEntry(instance.ProcessId, SimulatorStopOutcome.StopTimedOut,
                    $"Kill was issued for pid {instance.ProcessId} but the process did not exit within {_options.StopTimeoutMs} ms.");
        }
        catch (Exception ex)
        {
            return new SimulatorStopEntry(instance.ProcessId, SimulatorStopOutcome.Refused,
                $"Kill attempt on owned instance pid {instance.ProcessId} failed: {ex.Message}");
        }
    }

    private int? GetAliveOwnedProcessId() => GetAliveOwnedInstance()?.ProcessId;

    private OwnedInstance? GetAliveOwnedInstance()
    {
        lock (_stateLock)
        {
            return _owned.FirstOrDefault(o => !o.Process.HasExited);
        }
    }

    private static SimulatorInstanceInfo ToInfo(OwnedInstance owned) =>
        new(owned.ProcessId, owned.ProcessName, owned.StartTimeUtc, Owned: true);

    private void ReportOutput(string line)
    {
        lock (_outputLock)
        {
            _recentOutput.Add(line);
            if (_recentOutput.Count > 100)
            {
                _recentOutput.RemoveRange(0, _recentOutput.Count - 100);
            }
        }
    }

    private ISimulatorProcess DefaultStartProcess(SimulatorProcessStartSpec spec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.ExecutablePath,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        Process process = Process.Start(psi) ?? throw new InvalidOperationException(
            $"Process.Start returned null for '{spec.ExecutablePath}'.");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) ReportOutput(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) ReportOutput("[stderr] " + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new SystemSimulatorProcess(process);
    }

    private static async Task<bool> DefaultTcpProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
