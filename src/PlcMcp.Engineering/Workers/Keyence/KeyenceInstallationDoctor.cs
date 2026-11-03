using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Workers.External;

namespace PlcMcp.Engineering.Workers.Keyence;

// P0 read-only Keyence (KV STUDIO) diagnosis types.
//
// Mirrors the Omron P0 doctor (see Workers/Omron/OmronInstallationDoctor.cs):
// strictly read-only - file existence, FileVersionInfo, PE header bitness, SHA-256,
// and static registry observation (32/64 views). No COM activation, no process
// start, no KV STUDIO launch, no PLC connection.
//
// The three ProgIDs below were verified against a real KV STUDIO 12G installation:
// all resolve to LocalServer32 servers under the 32-bit (WOW6432Node) classes view.
// KV STUDIO is a 32-bit toolchain; the 64-bit CLSID view does not carry these
// registrations on the reference host.

/// <summary>One discovered Keyence component file (static path list + live facts).</summary>
public sealed record KeyenceComponentInfo(
    string Name,
    string Path,
    bool Exists,
    string? Bitness,
    string? Sha256);

/// <summary>Static COM registration observation for one ProgID (never activated).</summary>
public sealed record KeyenceComRegistrationInfo(
    string ProgId,
    bool Registered,
    string? Clsid,
    string? ServerType,
    string? ServerPath,
    bool ServerPathExists,
    string RegistryView);

/// <summary>P0 host diagnosis result for Keyence tooling.</summary>
public sealed record KeyenceDoctorReport(
    bool Installed,
    string? IdeExePath,
    string? IdeVersion,
    string? IdeBitness,
    string? LauncherExePath,
    string? LauncherVersion,
    string? CommServerExePath,
    string? RegistryVersion,
    IReadOnlyList<KeyenceComponentInfo> Components,
    IReadOnlyList<KeyenceComRegistrationInfo> ComRegistrations,
    CapabilitySet Capabilities,
    string Summary,
    string Disclaimer,
    KeyenceKvsMonSettings? KvsMonSettings = null);

/// <summary>Parsed connection/simulator settings from the live KvsMon.ini (read-only).</summary>
public sealed record KeyenceKvsMonSettings(
    string? IniPath,
    bool IniExists,
    string? IfMode,
    string? LastIpAddress,
    string? LastIpPort,
    string? AddrNetwork,
    string? AddrNode,
    string? SystemExtPortNo,
    string? HostLinkPortNo,
    string? ScanExecutionInterval,
    string? MachineNo);

/// <summary>
/// Read-only Keyence (KV STUDIO 12G / KV-Comm) host doctor. Detection only; every
/// operational capability is Unsupported by construction (P0 tier).
/// </summary>
public sealed class KeyenceInstallationDoctor
{
    public const string DefaultIdeExePath = @"C:\Program Files (x86)\KEYENCE\KVS12G\KVS12\KVS\Kvs.exe";
    public const string DefaultLauncherExePath = @"C:\Program Files (x86)\KEYENCE\KVS12G\KvsLauncher.exe";
    public const string DefaultCommServerExePath = @"C:\Program Files (x86)\Common Files\KEYENCE\AC\KvCommunication\AcCommServer2.exe";

    // Verified against a real KV STUDIO 12G install: all three classes are registered
    // in the 32-bit classes view (HKLM\SOFTWARE\WOW6432Node\Classes) as LocalServer32.
    public static readonly string[] ExpectedKeyenceProgIds =
    [
        "ACCommServer2.ACComm2",       // {B4D8317A-685B-4605-8BA3-8B2DE8751958} -> AcCommServer2.exe (ACL byte pipe)
        "KVCServer.KVControl",         // {FC3CFD6D-D71D-429A-83A8-16D0BD06752B} -> KVCServer.exe (cross-app broker)
        "PathManager2.ACCommPathMgr2"  // {EF3F3EA9-4227-4733-B1FC-502346EA4C2B} -> AcPathManager2.exe (path/broker manager)
    ];

    private static readonly string[] ComponentFiles =
    [
        DefaultIdeExePath,                                                                          // Kvs.exe (IDE)
        DefaultLauncherExePath,                                                                     // KvsLauncher.exe
        @"C:\Program Files (x86)\KEYENCE\KVS12G\KVS12\KVS\KVComm.dll",                              // native frame library (523 exports)
        @"C:\Program Files (x86)\KEYENCE\KVS12G\KVS12\KVS\Kvs.BinaryCommand.dll",                   // managed binary command protocol
        @"C:\Program Files (x86)\KEYENCE\KVS12G\KVS12\KVS\ACCommClientBridge.dll",                  // managed ACL client bridge
        @"C:\Program Files (x86)\Common Files\KEYENCE\AC\KvCommunication\LDKCOM.dll",               // classic LDK COM API (KzOpenDM/KzReadDM)
        DefaultCommServerExePath,                                                                   // AcCommServer2.exe (ACL LOCAL server)
        @"C:\Program Files (x86)\KEYENCE\AppCtrlUtl\ShareCmt\KVCServer.exe",                        // KVCServer broker
        @"C:\Program Files (x86)\Common Files\KEYENCE\AC\KvCommunication\KvEmu1000.dll"             // bundled PLC emulator (static only)
    ];

    private const string SummaryPrefix = "Keyence P0 Diagnosis: read-only static observation only; ";
    private const string DisclaimerText =
        "Detection is read-only and does not imply valid vendor license, API authorization, or vendor API execution. " +
        "KV STUDIO 12G interfaces were identified by inspection of installed files and registry entries (reverse engineering); there is no vendor endorsement.";

    private readonly string? _ideExePath;
    private readonly string? _commServerExePath;
    private readonly string? _kvsMonIniPath;

    public KeyenceInstallationDoctor(string? ideExePath = null, string? commServerExePath = null, string? kvsMonIniPath = null)
    {
        _ideExePath = ideExePath;
        _commServerExePath = commServerExePath;
        _kvsMonIniPath = kvsMonIniPath;
    }

    public static KeyenceDoctorReport DiagnoseHost(string? customIdePath = null, string? customCommServerPath = null)
        => new KeyenceInstallationDoctor(customIdePath, customCommServerPath).Diagnose();

    /// <summary>Single source for the P0 Keyence capability set: every operation Unsupported.</summary>
    public static CapabilitySet BuildKeyenceCapabilities(bool installed)
    {
        return new CapabilitySet(
        [
            new CapabilityDescriptor("InspectProject", CapabilityStatus.Unsupported, installed ? "P0 diagnosis tier only; no project inspection." : "Not installed."),
            new CapabilityDescriptor("ExportPou", CapabilityStatus.Unsupported, "No exporter in P0."),
            new CapabilityDescriptor("ValidatePou", CapabilityStatus.Unsupported, "No validator in P0."),
            new CapabilityDescriptor("CompileProject", CapabilityStatus.Unsupported, "No compiler access in P0."),
            new CapabilityDescriptor("KvScript", CapabilityStatus.Unsupported, "KV STUDIO macro host is observed statically only."),
            new CapabilityDescriptor("DownloadProject", CapabilityStatus.Unsupported, "Download is not available in P0."),
            new CapabilityDescriptor("SetRunMode", CapabilityStatus.Unsupported, "RUN/STOP is not available in P0."),
            new CapabilityDescriptor("ForceIo", CapabilityStatus.Unsupported, "Forcing is not available in P0."),
            new CapabilityDescriptor("WritePlc", CapabilityStatus.Unsupported, "PLC writes are not available in P0."),
            new CapabilityDescriptor("OnlineMonitor", CapabilityStatus.Unsupported, "Online monitoring is not available in P0."),
            new CapabilityDescriptor("OnlineConnect", CapabilityStatus.Unsupported, "COM/LDK interfaces are observed statically only; never activated."),
            new CapabilityDescriptor("AclComm", CapabilityStatus.Unsupported, "ACCommServer2/ACLSendnReceive2 are observed statically only."),
            new CapabilityDescriptor("KvEmu", CapabilityStatus.Unsupported, "Bundled KvEmu* simulator DLLs are observed statically only.")
        ]);
    }

    public KeyenceDoctorReport Diagnose()
    {
        string idePath = _ideExePath ?? DefaultIdeExePath;
        string commServerPath = _commServerExePath ?? DefaultCommServerExePath;

        var components = DiscoverComponents(idePath, commServerPath);
        var registrations = DiscoverComRegistrations();

        string? ideVersion = null;
        string? ideBitness = null;
        if (File.Exists(idePath))
        {
            var vi = FileVersionInfo.GetVersionInfo(idePath);
            ideVersion = vi.FileVersion ?? vi.ProductVersion ?? "Detected";
            ideBitness = BaseVendorProfileDetector.DetectPeBitness(idePath);
        }

        string? launcherVersion = null;
        string launcherPath = DefaultLauncherExePath;
        if (File.Exists(launcherPath))
        {
            var vi = FileVersionInfo.GetVersionInfo(launcherPath);
            launcherVersion = vi.FileVersion ?? vi.ProductVersion ?? "Detected";
        }

        bool installed = File.Exists(idePath);
        var capabilities = BuildKeyenceCapabilities(installed);
        string summary = $"{SummaryPrefix}KV STUDIO: {(installed ? $"detected ({ideVersion}, {ideBitness})" : "not detected")}; " +
                         $"{components.Count(c => c.Exists)}/{components.Count} components present; " +
                         $"{registrations.Count(r => r.Registered)}/{registrations.Count} COM registrations present (32-bit view).";

        return new KeyenceDoctorReport(
            Installed: installed,
            IdeExePath: idePath,
            IdeVersion: ideVersion,
            IdeBitness: ideBitness,
            LauncherExePath: launcherPath,
            LauncherVersion: launcherVersion,
            CommServerExePath: commServerPath,
            RegistryVersion: ReadRegistryVersion(),
            Components: components,
            ComRegistrations: registrations,
            Capabilities: capabilities,
            Summary: summary,
            Disclaimer: DisclaimerText,
            KvsMonSettings: ReadKvsMonSettings());
    }

    private static readonly string[] RegistryKeys =
    [
        @"SOFTWARE\KEYENCE\KVS",
        @"SOFTWARE\KEYENCE\KVSLauncher"
    ];

    private static readonly string[] RegistryVersionValueNames =
    [
        "Version",
        "RegVersion",
        "VersionString",
        "DisplayVersion"
    ];

    private static string? DefaultKvsMonIniPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"KEYENCE\KVS12G\KVS12\KVS\KvsMon.ini");

    private KeyenceKvsMonSettings ReadKvsMonSettings()
    {
        string path = _kvsMonIniPath ?? DefaultKvsMonIniPath();
        var missing = new KeyenceKvsMonSettings(path, false, null, null, null, null, null, null, null, null, null);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return missing;
        }

        try
        {
            var pairs = ParseIni(ReadIniText(path));
            if (pairs is null)
            {
                return missing;
            }

            string? Get(string section, string key) =>
                pairs.TryGetValue(section + "." + key, out var v) ? v : null;
            return new KeyenceKvsMonSettings(
                path,
                true,
                Get("MonConfig", "IFMode"),
                Get("MonConfig", "IpAddress"),
                Get("MonConfig", "IpPortNo"),
                Get("AclSetup", "AddrNetwork"),
                Get("AclSetup", "AddrNode"),
                Get("SimulatorSettings", "SystemExtPortNo"),
                Get("SimulatorSettings", "HostLinkPortNo"),
                Get("SimulatorSettings", "ScanExecutionInterval"),
                Get("MonConfig", "MachineNo"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return missing;
        }
    }

    private static Dictionary<string, string>? ParseIni(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line.Substring(1, line.Length - 2).Trim();
                continue;
            }

            if (section is null)
            {
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            pairs[section + "." + line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
        }

        return pairs;
    }

    private static string? ReadIniText(string path)
    {
        var raw = File.ReadAllBytes(path);
        if (raw.Length is < 3 or > 512 * 1024)
        {
            return null;
        }

        // KV STUDIO writes KvsMon.ini as UTF-16LE; fall back to UTF-8 for robustness.
        bool utf16 = (raw[0] == 0xFF && raw[1] == 0xFE) || (raw[0] == 0xFE && raw[1] == 0xFF);
        if (!utf16)
        {
            int nul = 0;
            int probe = Math.Min(raw.Length, 4096);
            for (int i = 0; i < probe; i += 2)
            {
                if (raw[i] == 0)
                {
                    nul++;
                }
            }

            utf16 = nul > probe / 8;
        }

        var text = utf16 ? Encoding.Unicode.GetString(raw) : Encoding.UTF8.GetString(raw);
        return text.TrimStart('\uFEFF');
    }

    private static string? ReadRegistryVersion()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                foreach (var relativeKeyPath in RegistryKeys)
                {
                    using var key = baseKey.OpenSubKey(relativeKeyPath);
                    if (key is null)
                    {
                        continue;
                    }

                    foreach (var valueName in RegistryVersionValueNames)
                    {
                        var value = key.GetValue(valueName) as string;
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value;
                        }
                    }
                }
            }
            catch
            {
                // Registry access is best-effort only.
            }
        }

        return null;
    }

    private static IReadOnlyList<KeyenceComponentInfo> DiscoverComponents(string idePath, string commServerPath)
    {
        var list = new List<KeyenceComponentInfo>();
        foreach (var candidate in ComponentFiles)
        {
            string name = Path.GetFileName(candidate);
            string effective = name == "Kvs.exe"
                ? idePath
                : name == "AcCommServer2.exe"
                    ? commServerPath
                    : candidate;
            bool exists = File.Exists(effective);
            list.Add(new KeyenceComponentInfo(
                Name: name,
                Path: effective,
                Exists: exists,
                Bitness: exists ? BaseVendorProfileDetector.DetectPeBitness(effective) : null,
                Sha256: exists ? ComputeSha256(effective) : null));
        }

        return list;
    }

    private static IReadOnlyList<KeyenceComRegistrationInfo> DiscoverComRegistrations()
    {
        var list = new List<KeyenceComRegistrationInfo>();
        foreach (var progId in ExpectedKeyenceProgIds)
        {
            list.Add(ObserveProgId(progId));
        }

        return list;
    }

    private static KeyenceComRegistrationInfo ObserveProgId(string progId)
    {
        // KV STUDIO is 32-bit: the classes are registered under the WOW6432Node view on
        // x64 hosts (verified on the reference installation). Both views are observed.
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
                using var progKey = classes.OpenSubKey(progId);
                if (progKey is null)
                {
                    continue;
                }

                string? clsid = progKey.OpenSubKey("CLSID")?.GetValue(null) as string;
                if (string.IsNullOrWhiteSpace(clsid))
                {
                    continue;
                }

                clsid = clsid.Trim('{', '}');
                var normalizedClsid = "{" + clsid + "}";
                using var clsidKey = classes.OpenSubKey(@"CLSID\" + normalizedClsid);
                string? serverType = null;
                string? serverPath = null;
                if (clsidKey is not null)
                {
                    foreach (var candidate in new[] { "LocalServer32", "InprocServer32" })
                    {
                        var candidatePath = clsidKey.OpenSubKey(candidate)?.GetValue(null) as string;
                        if (!string.IsNullOrWhiteSpace(candidatePath))
                        {
                            serverType = candidate;
                            serverPath = candidatePath.Trim('"');
                            break;
                        }
                    }
                }

                return new KeyenceComRegistrationInfo(
                    ProgId: progId,
                    Registered: true,
                    Clsid: normalizedClsid,
                    ServerType: serverType,
                    ServerPath: serverPath,
                    ServerPathExists: serverPath is not null && File.Exists(serverPath),
                    RegistryView: view == RegistryView.Registry32 ? "Registry32" : "Registry64");
            }
            catch
            {
                // Registry access is best-effort only.
            }
        }

        return new KeyenceComRegistrationInfo(
            ProgId: progId,
            Registered: false,
            Clsid: null,
            ServerType: null,
            ServerPath: null,
            ServerPathExists: false,
            RegistryView: "Registry32");
    }

    private static string? ComputeSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
    }
}