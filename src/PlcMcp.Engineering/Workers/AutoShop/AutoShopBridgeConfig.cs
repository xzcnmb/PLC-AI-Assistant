namespace PlcMcp.Engineering.Workers.AutoShop;

/// <summary>
/// Injectable detection options for the local AutoShop / InoVarSharedService environment.
/// Every probe is overridable so that tests run fully offline without a real AutoShop installation.
/// </summary>
public sealed class AutoShopDetectionOptions
{
    /// <summary>Candidate AutoShop.exe locations (defaults match the V4.12.0.2 installation layout).</summary>
    public IReadOnlyList<string> ExecutableCandidates { get; init; } = new[]
    {
        @"D:\Inovance Control\AutoShop\AutoShop.exe",
        @"C:\Program Files (x86)\Inovance Control\AutoShop\AutoShop.exe",
        @"C:\Inovance Control\AutoShop\AutoShop.exe"
    };

    /// <summary>Windows service name of the shared-variable service.</summary>
    public string ServiceName { get; init; } = "InoVarSharedService";

    /// <summary>Registry locations (relative to a hive root) that may hold the persisted service port.</summary>
    public IReadOnlyList<string> RegistryPortKeyCandidates { get; init; } = new[]
    {
        @"SOFTWARE\WOW6432Node\AutoServicePort",
        @"SOFTWARE\AutoServicePort",
        @"HKCU\SOFTWARE\AutoServicePort"
    };

    /// <summary>Registry value name holding the 2-byte little-endian port.</summary>
    public string RegistryPortValueName { get; init; } = "Data";

    /// <summary>Log directory of the shared-variable service (read-only evidence source).</summary>
    public string LogDirectory { get; init; } = @"D:\Inovance Control\AutoShop\AutoVarShared\SharedVaribleLog";

    /// <summary>Default loopback port used when the registry value cannot be read.</summary>
    public int DefaultPort { get; init; } = 13939;

    /// <summary>Overrides <c>File.Exists</c> for executable candidates (tests).</summary>
    public Func<string, bool>? FileExists { get; init; }

    /// <summary>Overrides <c>FileVersionInfo.GetVersionInfo(path).FileVersion</c> (tests).</summary>
    public Func<string, string?>? FileVersionReader { get; init; }

    /// <summary>
    /// Reads a registry value's raw bytes: (hiveRootRelativeKeyPath, valueName) -> bytes or null.
    /// Default implementation uses Microsoft.Win32.Registry (read-only).
    /// </summary>
    public Func<string, string, byte[]?>? RegistryValueReader { get; init; }

    /// <summary>Reports whether the named service process is running (default: process table lookup).</summary>
    public Func<string, bool>? ServiceProcessProbe { get; init; }

    /// <summary>Probes whether a TCP endpoint is accepting connections (default: real loopback connect).</summary>
    public Func<string, int, Task<bool>>? TcpProbe { get; init; }

    /// <summary>Returns the newest log file path under the log directory (default: directory enumeration).</summary>
    public Func<string, string?>? NewestLogFileProbe { get; init; }
}

/// <summary>
/// Evidence report for the local AutoShop engineering environment.
/// Detection is read-only heuristic evidence; it does not imply a valid vendor license, API
/// authorization, or any support for compile/download operations.
/// </summary>
public sealed record AutoShopEnvironmentReport(
    bool Installed,
    string? ExecutablePath,
    string? Version,
    string? ServiceName,
    bool ServiceProcessRunning,
    int? RegistryPort,
    string? RegistryPortLocation,
    int EffectivePort,
    bool LoopbackListening,
    string? NewestLogFile,
    string Details,
    string Disclaimer = "AutoShop interfaces used by this bridge were obtained by reverse engineering; there is no vendor endorsement. Version-locked to AutoShop 4.12.0.2. Detection is read-only heuristic and does not imply valid vendor license or API authorization.");

/// <summary>
/// Configuration for the AutoShop engineering bridge. Disabled by default; the bridge is only
/// wired into the server behind an explicit --autoshop-bridge switch.
/// </summary>
public sealed class AutoShopBridgeConfig
{
    /// <summary>Loopback host of InoVarSharedService.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>TCP port of InoVarSharedService (registry-persisted; default 13939 for 4.12.0.2).</summary>
    public int Port { get; init; } = 13939;

    /// <summary>
    /// Path registered during the ITP handshake. The service answers Response=3 ("Query Fail")
    /// when no AutoShop publisher is registered for this path.
    /// </summary>
    public string RegisterPath { get; init; } = "PlcMcp.AutoShopBridge";

    /// <summary>Per-operation timeout for connect/handshake.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>When true, only loopback hosts are accepted.</summary>
    public bool RequireLoopback { get; init; } = true;
}
