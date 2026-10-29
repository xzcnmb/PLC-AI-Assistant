using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Workers.External;

namespace PlcMcp.Engineering.Workers.Codesys;

/// <summary>
/// Configuration for the CODESYS offline engineering worker.
///
/// This worker drives the locally installed CODESYS Development System through its
/// official ScriptEngine (IronPython 2.7, <c>--runscript</c>). It is deliberately
/// pinned to an exact executable, profile and script hash so that a mismatched or
/// missing installation fails closed instead of silently running an unknown toolchain.
///
/// The worker is strictly offline: it may open a disposable working copy, inspect the
/// application tree, export PLCopen XML and run build/rebuild/clean. It must never
/// invoke ScriptOnline, download, start/stop, force values or write back the project.
/// </summary>
public sealed class CodesysWorkerConfig
{
    /// <summary>
    /// Absolute path to the pinned CODESYS.exe. Must also appear in
    /// <see cref="ExternalEngineeringWorkerConfig.AllowedExecutablePaths"/>.
    /// </summary>
    public string CodesysExePath { get; set; } = string.Empty;

    /// <summary>
    /// Exact CODESYS profile name, e.g. "CODESYS V3.5 SP22 Patch 3".
    /// Required together with <see cref="CodesysVersion"/>; a mismatch fails closed.
    /// </summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>
    /// Expected CODESYS product version string (FileVersion / ProductVersion).
    /// Used for a soft identity check that is reported honestly in diagnostics.
    /// </summary>
    public string? CodesysVersion { get; set; }

    /// <summary>
    /// Absolute path to the versioned IronPython 2.7 driver script.
    /// </summary>
    public string DriverScriptPath { get; set; } = string.Empty;

    /// <summary>
    /// Expected SHA-256 of <see cref="DriverScriptPath"/> (lowercase hex).
    /// The worker refuses to launch when this is empty or does not match the file on disk.
    /// </summary>
    public string DriverScriptSha256 { get; set; } = string.Empty;

    /// <summary>
    /// Absolute project root. Only paths beneath this root are accepted; the worker
    /// still operates exclusively on a disposable working copy.
    /// </summary>
    public string ProjectRoot { get; set; } = string.Empty;

    /// <summary>
    /// Total wall-clock budget for a single offline job. CODESYS builds are slow,
    /// so this defaults to a larger value than a generic external worker.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 150;

    /// <summary>
    /// Hard cap on captured stdout+stderr bytes before the process tree is killed.
    /// </summary>
    public long MaxOutputBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>
    /// Timeout for the startup evidence handshake. No project is opened during this probe.
    /// </summary>
    public int StartupProbeTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Controls whether the isolated working copy is writable for offline build output.
    /// The source project is never writable.
    /// </summary>
    public bool AllowWritableWorkingCopy { get; set; } = true;

    /// <summary>
    /// Pinned expected worker identity. Set from the driver script so that a substituted
    /// script cannot impersonate the known worker. Enforced when
    /// <see cref="RequirePinnedIdentity"/> is true.
    /// </summary>
    public string ExpectedWorkerName { get; set; } = "Codesys-ScriptEngine-Worker";

    public string ExpectedWorkerVersion { get; set; } = "1.0";
    public string ExpectedProtocolVersion { get; set; } = "1.0";

    /// <summary>
    /// Fail closed when the worker identity does not match the pinned expectation.
    /// Defaults to true because CODESYS drives a real vendor toolchain.
    /// </summary>
    public bool RequirePinnedIdentity { get; set; } = true;

    public PlcVendor Vendor => PlcVendor.Generic;

    /// <summary>
    /// Projects the CODESYS configuration into the generic external worker configuration.
    /// Only offline operation names are passed through; the generic worker still hard-rejects
    /// any dangerous physical/online operation regardless of this projection.
    /// </summary>
    public ExternalEngineeringWorkerConfig ToExternalWorkerConfig()
    {
        return new ExternalEngineeringWorkerConfig
        {
            ExecutablePath = CodesysExePath,
            AllowedExecutablePaths = string.IsNullOrWhiteSpace(CodesysExePath) ? new() : new() { CodesysExePath },
            AllowedWorkspaceRoots = string.IsNullOrWhiteSpace(ProjectRoot) ? new() : new() { ProjectRoot },
            TimeoutSeconds = TimeoutSeconds,
            MaxOutputBytes = MaxOutputBytes,
            WorkingCopyReadOnly = !AllowWritableWorkingCopy,
            ExpectedWorkerName = ExpectedWorkerName,
            ExpectedWorkerVersion = ExpectedWorkerVersion,
            ExpectedProtocolVersion = ExpectedProtocolVersion,
            RequirePinnedIdentity = RequirePinnedIdentity,
            RawArguments = BuildRawArguments(),
            GracefulShutdown = true
        };
    }

    /// <summary>
    /// Builds the empirically verified CODESYS command line. The CODESYS 3.5.22 legacy
    /// parser requires quotes around the profile and runscript values to survive as a
    /// single raw command-line string; ProcessStartInfo.ArgumentList does not preserve
    /// the needed form.
    /// </summary>
    public string BuildRawArguments()
    {
        if (string.IsNullOrWhiteSpace(ProfileName) || string.IsNullOrWhiteSpace(DriverScriptPath))
            return string.Empty;

        return $"--noUI --noConsole --skipProjectRecovery --skipUnlicensedPlugins --profile=\"{ProfileName}\" --runscript=\"{DriverScriptPath}\"";
    }
}
