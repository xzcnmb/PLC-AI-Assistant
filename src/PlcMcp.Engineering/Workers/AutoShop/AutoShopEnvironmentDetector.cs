using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace PlcMcp.Engineering.Workers.AutoShop;

/// <summary>
/// Read-only detector for the local AutoShop installation and its InoVarSharedService.
/// All probes (filesystem, registry, TCP, process table) are injectable so tests run offline.
/// The detector never writes registry keys, starts/stops services, or touches AutoShop UI/project files.
/// </summary>
public sealed class AutoShopEnvironmentDetector
{
    private readonly AutoShopDetectionOptions _options;

    public AutoShopEnvironmentDetector(AutoShopDetectionOptions? options = null)
    {
        _options = options ?? new AutoShopDetectionOptions();
    }

    public AutoShopDetectionOptions Options => _options;

    public AutoShopEnvironmentReport Detect()
    {
        string? exePath = null;
        string? version = null;
        foreach (string candidate in _options.ExecutableCandidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            bool exists = _options.FileExists?.Invoke(candidate) ?? SafeFileExists(candidate);
            if (!exists)
            {
                continue;
            }

            exePath = candidate;
            version = _options.FileVersionReader is not null
                ? _options.FileVersionReader(candidate)
                : SafeFileVersion(candidate);
            break;
        }

        bool installed = exePath is not null;

        bool serviceRunning = _options.ServiceProcessProbe?.Invoke(_options.ServiceName)
            ?? SafeServiceProcessRunning(_options.ServiceName);

        int? registryPort = null;
        string? registryPortLocation = null;
        foreach (string keyPath in _options.RegistryPortKeyCandidates)
        {
            if (string.IsNullOrWhiteSpace(keyPath))
            {
                continue;
            }

            byte[]? raw = _options.RegistryValueReader is not null
                ? _options.RegistryValueReader(keyPath, _options.RegistryPortValueName)
                : SafeRegistryRead(keyPath, _options.RegistryPortValueName);
            if (raw is { Length: >= 2 })
            {
                int port = raw[0] | (raw[1] << 8);
                if (port is > 0 and <= 65535)
                {
                    registryPort = port;
                    registryPortLocation = $"{keyPath}\\{_options.RegistryPortValueName}";
                    break;
                }
            }
        }

        int effectivePort = registryPort ?? _options.DefaultPort;
        bool loopbackListening = TcpProbeAsync(effectivePort).GetAwaiter().GetResult();

        string? newestLog = _options.NewestLogFileProbe is not null
            ? _options.NewestLogFileProbe(_options.LogDirectory)
            : SafeNewestLogFile(_options.LogDirectory);

        string details = installed
            ? $"AutoShop executable detected at '{exePath}' (version {version ?? "unknown"}). Service '{_options.ServiceName}' process running: {serviceRunning}. " +
              $"Registry port evidence: {registryPortLocation ?? "none"} (value {(registryPort?.ToString(CultureInfo.InvariantCulture) ?? "n/a")}). " +
              $"Effective ITP endpoint 127.0.0.1:{effectivePort} listening: {loopbackListening}. Newest service log: {newestLog ?? "none"}. " +
              "No evidence of compile/download capability is implied."
            : $"AutoShop executable not found at candidate paths ({string.Join("; ", _options.ExecutableCandidates)}). " +
              $"Service '{_options.ServiceName}' process running: {serviceRunning}. ITP endpoint 127.0.0.1:{effectivePort} listening: {loopbackListening}.";

        return new AutoShopEnvironmentReport(
            Installed: installed,
            ExecutablePath: exePath,
            Version: version,
            ServiceName: _options.ServiceName,
            ServiceProcessRunning: serviceRunning,
            RegistryPort: registryPort,
            RegistryPortLocation: registryPortLocation,
            EffectivePort: effectivePort,
            LoopbackListening: loopbackListening,
            NewestLogFile: newestLog,
            Details: details);
    }

    private async Task<bool> TcpProbeAsync(int port)
    {
        if (_options.TcpProbe is not null)
        {
            return await _options.TcpProbe("127.0.0.1", port).ConfigureAwait(false);
        }

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
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

    private static string? SafeFileVersion(string path)
    {
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            return info.FileVersion ?? info.ProductVersion;
        }
        catch
        {
            return null;
        }
    }

    private static bool SafeServiceProcessRunning(string serviceName)
    {
        try
        {
            return Process.GetProcessesByName(serviceName) is { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? SafeRegistryRead(string keyPath, string valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            // Accept both "SOFTWARE\..." (HKLM) and "HKCU\SOFTWARE\..." forms.
            RegistryKey? hive;
            string subKey;
            if (keyPath.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
            {
                hive = Registry.CurrentUser;
                subKey = keyPath[5..];
            }
            else if (keyPath.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase))
            {
                hive = Registry.LocalMachine;
                subKey = keyPath[5..];
            }
            else
            {
                hive = Registry.LocalMachine;
                subKey = keyPath;
            }

            using RegistryKey? key = hive.OpenSubKey(subKey);
            return key?.GetValue(valueName) as byte[];
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeNewestLogFile(string logDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory))
            {
                return null;
            }

            return new DirectoryInfo(logDirectory)
                .EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch
        {
            return null;
        }
    }
}
