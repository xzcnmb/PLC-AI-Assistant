using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlcMcp.Engineering.Workers.AutoShop.InjectBridge;

// Models for the inject bridge launcher: the injector process command surface,
// the ownership journal and the start/stop results. Ownership protection
// mirrors SimulatorLauncher: only instances this launcher started (and whose
// live identity still matches the recorded exe/name/creation time) are ever
// stopped.

/// <summary>One ownership record in the JSONL journal.</summary>
public sealed record InjectOwnedInstanceRecord(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("processName")] string ProcessName,
    [property: JsonPropertyName("exe")] string Exe,
    [property: JsonPropertyName("dll")] string Dll,
    [property: JsonPropertyName("pipeName")] string PipeName,
    [property: JsonPropertyName("startedUtc")] string StartedUtc,
    [property: JsonPropertyName("startedFileTime")] string? StartedFileTime = null,
    [property: JsonPropertyName("recordedUtc")] string? RecordedUtc = null);

/// <summary>Injector command handed to the injectable runner.</summary>
public sealed record InjectInjectorCommand(
    string Verb,
    string Arguments,
    TimeSpan Timeout);

/// <summary>Outcome of one injector run.</summary>
public sealed record InjectInjectorRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string? Error = null);

/// <summary>Outcome of StartAsync.</summary>
public enum InjectStartOutcome
{
    StartedAndInjected,
    Failed
}

/// <summary>Result of StartAsync.</summary>
public sealed record InjectStartResult(
    InjectStartOutcome Outcome,
    bool Success,
    int? ProcessId,
    bool PipeReady,
    string? PipeName,
    string Details,
    string? Error,
    DateTimeOffset Timestamp);

/// <summary>Per-process outcome of a stop attempt.</summary>
public sealed record InjectStopEntry(
    int ProcessId,
    InjectStopOutcome Outcome,
    string Reason);

public enum InjectStopOutcome
{
    Killed,
    AlreadyExited,
    Refused,
    StopFailed
}

/// <summary>Result of StopAsync. Only journaled, re-verified instances are killed.</summary>
public sealed record InjectStopResult(
    bool Success,
    IReadOnlyList<InjectStopEntry> Entries,
    string Details,
    DateTimeOffset Timestamp);

/// <summary>Live identity of a PID as seen through the process table (injectable).</summary>
public sealed record InjectLiveProcessIdentity(
    int ProcessId,
    string ProcessName,
    string? ExePath,
    DateTime StartTimeUtc);
