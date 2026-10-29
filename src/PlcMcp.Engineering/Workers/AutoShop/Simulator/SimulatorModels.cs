using PlcMcp.Contracts.Models;

namespace PlcMcp.Engineering.Workers.AutoShop.Simulator;

// Result records for the AutoShop H5U simulator automation layer (SimulatorLauncher /
// SimulatorProbe). The layer automates the vendor daemon
// "D:\Inovance Control\AutoShop\Simulator\h5u_daemon.exe" which serves
//   * Modbus TCP on 0.0.0.0:502 (FC1/2/3/4 read, FC5/6 write; unit-agnostic), and
//   * the AutoShop native protocol on 127.0.0.1:12939,
// as verified against the live daemon on 2026-10-24 (see docs/inovance-autoshop-simulator.md).
// The delivered code path is strictly read-only: no Modbus write frame is ever built and
// every TCP endpoint is loopback-validated.

/// <summary>Outcome of the launcher's executable discovery step.</summary>
public sealed record SimulatorDiscoveryResult(
    bool Found,
    string? ExecutablePath,
    string? WorkingDirectory,
    string Details);

/// <summary>
/// Health of the simulator endpoints. Per the acceptance definition the overall verdict
/// requires BOTH loopback ports (Modbus TCP 502 and the native AutoShop port 12939) to be
/// connectable; owned-process liveness is reported separately.
/// </summary>
public sealed record SimulatorHealthReport(
    bool ModbusPortReachable,
    bool NativePortReachable,
    bool OwnedInstanceAlive,
    int? OwnedProcessId,
    bool OverallHealthy,
    string Details,
    DateTimeOffset Timestamp);

/// <summary>Identity of one simulator daemon process (owned or observed).</summary>
public sealed record SimulatorInstanceInfo(
    int ProcessId,
    string ProcessName,
    DateTime StartTimeUtc,
    bool Owned);

public enum SimulatorStartOutcome
{
    StartedNewProcess,
    ReusedOwnedInstance,
    ReusedForeignInstance,
    Failed
}

/// <summary>Result of StartAsync / EnsureRunningAsync.</summary>
public sealed record SimulatorStartResult(
    SimulatorStartOutcome Outcome,
    bool Success,
    bool Owned,
    SimulatorInstanceInfo? Instance,
    string Details,
    string? Error,
    DateTimeOffset Timestamp);

public enum SimulatorStopOutcome
{
    Killed,
    AlreadyExited,
    Refused,
    StopTimedOut
}

/// <summary>Per-process outcome of a stop attempt.</summary>
public sealed record SimulatorStopEntry(
    int ProcessId,
    SimulatorStopOutcome Outcome,
    string Reason);

/// <summary>Result of StopAsync. Only instances started (and recorded) by this launcher are ever killed.</summary>
public sealed record SimulatorStopResult(
    bool Success,
    IReadOnlyList<SimulatorStopEntry> Entries,
    bool? PortsFreedAfterStop,
    string Details,
    DateTimeOffset Timestamp);

/// <summary>One read-only Modbus probe measurement against the simulator.</summary>
public sealed record SimulatorRegisterReading(
    string ProbeKind,
    ushort Address,
    ushort Count,
    byte FunctionCode,
    ushort[] Values,
    double LatencyMs,
    QualityCode Quality,
    string? Error,
    DateTimeOffset Timestamp);

/// <summary>Result of the optional native-protocol probe (TestHost.exe wrapper).</summary>
public sealed record NativeProbeResult(
    bool Attempted,
    bool Skipped,
    string? SkipReason,
    int? ExitCode,
    bool Online,
    string OutputTail,
    double DurationMs,
    string? Error);

/// <summary>Aggregated probe report: a small set of read-only Modbus reads plus the native probe.</summary>
public sealed record SimulatorProbeReport(
    bool ModbusProbeOk,
    IReadOnlyList<SimulatorRegisterReading> Readings,
    NativeProbeResult? Native,
    string Details,
    DateTimeOffset Timestamp);

/// <summary>Parsed Modbus read response (FC1/2/3/4) used by the probe.</summary>
public sealed record SimulatorReadResponse(
    ushort Transaction,
    byte Unit,
    byte FunctionCode,
    ushort[] Values);
