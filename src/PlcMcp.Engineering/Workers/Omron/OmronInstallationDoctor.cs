using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Workers.External;

namespace PlcMcp.Engineering.Workers.Omron;

// P0 read-only Omron diagnosis types.
//
// These types were referenced by the vendor-integration branch (CODESYS/GX Works3/Omron
// work) but never shipped with definitions; this file implements them to the contract
// fixed by tests/PlcMcp.Engineering.Tests/OmronDoctorTests.cs and
// docs/omron-agent-handoff.md. Everything here is strictly read-only: file existence,
// FileVersionInfo, PE header bitness, SHA-256, and static registry observation (32/64
// views). No COM activation, no process start, no PLC connection.

/// <summary>One discovered vendor component file (static path list + live facts).</summary>
public sealed record OmronComponentInfo(
    string Name,
    string Path,
    bool Exists,
    string? Bitness,
    string? Sha256);

/// <summary>Static COM registration observation for one ProgID (never activated).</summary>
public sealed record OmronComRegistrationInfo(
    string ProgId,
    bool Registered,
    string? Clsid,
    string? ServerType,
    string? ServerPath,
    bool ServerPathExists,
    string RegistryView);

/// <summary>P0 host diagnosis result for Omron tooling.</summary>
public sealed record OmronDoctorReport(
    bool SysmacInstalled,
    string? SysmacExePath,
    string? SysmacVersion,
    string? SysmacBitness,
    string? SysmacRegistryVersion,
    bool CxServerInstalled,
    string? CxServerExePath,
    string? CxServerVersion,
    string? CxServerBitness,
    string? CxServerRegistryVersion,
    IReadOnlyList<OmronComponentInfo> Components,
    IReadOnlyList<OmronComRegistrationInfo> ComRegistrations,
    CapabilitySet Capabilities,
    string Summary,
    string Disclaimer);

/// <summary>
/// Read-only Omron (Sysmac Studio / CX-Server) host doctor. Detection only; every
/// operational capability is Unsupported by construction (P0 tier).
/// </summary>
public sealed class OmronInstallationDoctor
{
    public const string DefaultSysmacExePath = @"C:\Program Files\OMRON\Sysmac Studio\SysmacStudio.exe";
    public const string DefaultCxServerExePath = @"C:\Program Files (x86)\OMRON\CX-Server\cdmsvr20.exe";

    public static readonly string[] DefaultSysmacCandidatePaths =
    [
        DefaultSysmacExePath
    ];

    public static readonly string[] DefaultCxServerCandidatePaths =
    [
        DefaultCxServerExePath
    ];

    // The two entries with a gold CLSID are verified against a real CX-Server 5.1.1.4
    // installation (see docs/omron-agent-handoff.md); the remaining ProgIDs are the
    // documented CX-Server family surface and are enumerated even when unregistered.
    public static readonly string[] ExpectedOmronProgIds =
    [
        "CXServer.Communications",
        "Omron.DeviceManager",
        "CXServer.RS232C",
        "CXServer.TCPIP",
        "CXServer.SYSMAC",
        "CXServer.ControllerLink",
        "CXServer.CS",
        "CXServer.CJ",
        "CXServer.CP1",
        "CXServer.C200",
        "CXServer.CQM1",
        "CXServer.CPM1",
        "CXServer.EtherNetIP"
    ];

    private static readonly string[] ComponentFiles =
    [
        DefaultSysmacExePath,
        @"C:\Program Files\OMRON\Sysmac Studio\Ace.ScriptHost.exe",
        @"C:\Program Files\OMRON\Sysmac Studio\builder2\nexcc.exe",
        @"C:\Program Files\OMRON\Sysmac Studio\SysmacDiff.exe",
        @"C:\Program Files\OMRON\Sysmac Studio\RuntimeSimulator.exe",
        DefaultCxServerExePath
    ];

    private const string SummaryPrefix = "Omron P0 Diagnosis: read-only static observation only; ";
    private const string DisclaimerText = "Detection is read-only and does not imply valid vendor license, API authorization, or vendor compiler execution.";

    private readonly string? _sysmacExePath;
    private readonly string? _cxServerExePath;

    public OmronInstallationDoctor(string? sysmacExePath = null, string? cxServerExePath = null)
    {
        _sysmacExePath = sysmacExePath;
        _cxServerExePath = cxServerExePath;
    }

    public static OmronDoctorReport DiagnoseHost(string? customSysmacPath = null, string? customCxServerPath = null)
        => new OmronInstallationDoctor(customSysmacPath, customCxServerPath).Diagnose();

    /// <summary>
    /// CX-Server focused static probe used by <c>CxServerProfileDetector</c>.
    /// Read-only; returns a positional tuple (exe path, version, bitness, registry version,
    /// installed, details).
    /// </summary>
    public static (string Exe, string? Ver, string? Bit, string? RegVer, bool Installed, string Details) DetectCxServer(string? customPath = null)
    {
        string path = customPath ?? DefaultCxServerExePath;
        bool installed = File.Exists(path);
        string? ver = null;
        string? bit = null;
        if (installed)
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            ver = vi.FileVersion ?? vi.ProductVersion;
            bit = BaseVendorProfileDetector.DetectPeBitness(path);
        }

        string regVer = ReadRegistryVersion(RegistryKeyPathCxServer) ?? string.Empty;
        string details = installed
            ? $"Omron CX-Server detected at '{path}' (FileVersion: {ver}, Bitness: {bit}). P0 read-only diagnosis tier; all operational capabilities Unsupported."
            : $"Omron CX-Server NOT installed or detected on this host. Features are Unsupported.";
        return (path, ver, bit, regVer, installed, details);
    }

    /// <summary>Single source for the P0 Omron capability set: every operation Unsupported.</summary>
    public static CapabilitySet BuildOmronCapabilities(bool installed)
    {
        return new CapabilitySet(
        [
            new CapabilityDescriptor("InspectProject", CapabilityStatus.Unsupported, installed ? "P0 diagnosis tier only; no project inspection." : "Not installed."),
            new CapabilityDescriptor("ExportPou", CapabilityStatus.Unsupported, "No exporter in P0."),
            new CapabilityDescriptor("ValidatePou", CapabilityStatus.Unsupported, "No validator in P0."),
            new CapabilityDescriptor("CompileProject", CapabilityStatus.Unsupported, "No compiler access in P0."),
            new CapabilityDescriptor("DiffProjects", CapabilityStatus.Unsupported, "No diff engine in P0."),
            new CapabilityDescriptor("AceScript", CapabilityStatus.Unsupported, "Ace.ScriptHost is observed statically only."),
            new CapabilityDescriptor("NexccCompile", CapabilityStatus.Unsupported, "nexcc.exe is observed statically only."),
            new CapabilityDescriptor("ExportXml", CapabilityStatus.Unsupported, "Standard-exchange tools are separate (plc_omron_exchange_*)."),
            new CapabilityDescriptor("ExportAml", CapabilityStatus.Unsupported, "Standard-exchange tools are separate (plc_omron_exchange_*)."),
            new CapabilityDescriptor("DownloadProject", CapabilityStatus.Unsupported, "Download is not available in P0."),
            new CapabilityDescriptor("SetRunMode", CapabilityStatus.Unsupported, "RUN/STOP is not available in P0."),
            new CapabilityDescriptor("ForceIo", CapabilityStatus.Unsupported, "Forcing is not available in P0."),
            new CapabilityDescriptor("WritePlc", CapabilityStatus.Unsupported, "PLC writes are not available in P0."),
            new CapabilityDescriptor("OnlineMonitor", CapabilityStatus.Unsupported, "Online monitoring is not available in P0."),
            new CapabilityDescriptor("OnlineConnect", CapabilityStatus.Unsupported, "Online connection is not available in P0."),
            new CapabilityDescriptor("HmiPublish", CapabilityStatus.Unsupported, "HMI publishing is not available in P0.")
        ]);
    }

    public OmronDoctorReport Diagnose()
    {
        string sysmacPath = _sysmacExePath ?? DefaultSysmacExePath;
        string cxPath = _cxServerExePath ?? DefaultCxServerExePath;

        var components = DiscoverComponents(sysmacPath, cxPath);
        var registrations = DiscoverComRegistrations();

        string? sysmacVersion = null;
        string? sysmacBitness = null;
        if (File.Exists(sysmacPath))
        {
            var vi = FileVersionInfo.GetVersionInfo(sysmacPath);
            sysmacVersion = vi.FileVersion ?? vi.ProductVersion;
            sysmacBitness = BaseVendorProfileDetector.DetectPeBitness(sysmacPath);
        }

        string? cxVersion = null;
        string? cxBitness = null;
        if (File.Exists(cxPath))
        {
            var vi = FileVersionInfo.GetVersionInfo(cxPath);
            cxVersion = vi.FileVersion ?? vi.ProductVersion;
            cxBitness = BaseVendorProfileDetector.DetectPeBitness(cxPath);
        }

        bool sysmacInstalled = File.Exists(sysmacPath);
        bool cxInstalled = File.Exists(cxPath);
        var capabilities = BuildOmronCapabilities(sysmacInstalled);
        string summary = $"{SummaryPrefix}Sysmac Studio: {(sysmacInstalled ? $"detected ({sysmacVersion}, {sysmacBitness})" : "not detected")}; " +
                         $"CX-Server: {(cxInstalled ? $"detected ({cxVersion}, {cxBitness})" : "not detected")}; " +
                         $"{components.Count(c => c.Exists)}/{components.Count} components present; " +
                         $"{registrations.Count(r => r.Registered)}/{registrations.Count} COM registrations present.";

        return new OmronDoctorReport(
            SysmacInstalled: sysmacInstalled,
            SysmacExePath: sysmacPath,
            SysmacVersion: sysmacVersion,
            SysmacBitness: sysmacBitness,
            SysmacRegistryVersion: ReadRegistryVersion(RegistryKeyPathSysmac),
            CxServerInstalled: cxInstalled,
            CxServerExePath: cxPath,
            CxServerVersion: cxVersion,
            CxServerBitness: cxBitness,
            CxServerRegistryVersion: ReadRegistryVersion(RegistryKeyPathCxServer),
            Components: components,
            ComRegistrations: registrations,
            Capabilities: capabilities,
            Summary: summary,
            Disclaimer: DisclaimerText);
    }

    private const string RegistryKeyPathSysmac = @"SOFTWARE\OMRON\Sysmac Studio";
    private const string RegistryKeyPathCxServer = @"SOFTWARE\OMRON\CX-Server";

    private static string? ReadRegistryVersion(string relativeKeyPath)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(relativeKeyPath);
                if (key is null)
                {
                    continue;
                }

                foreach (var valueName in new[] { "Version", "RegVersion", "SysmacStudioVersion", "CXServerVersion" })
                {
                    var value = key.GetValue(valueName) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
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

    private static IReadOnlyList<OmronComponentInfo> DiscoverComponents(string sysmacPath, string cxPath)
    {
        var list = new List<OmronComponentInfo>();
        foreach (var candidate in ComponentFiles)
        {
            string name = Path.GetFileName(candidate);
            string effective = Path.GetFileName(candidate) == "SysmacStudio.exe"
                ? sysmacPath
                : Path.GetFileName(candidate) == "cdmsvr20.exe"
                    ? cxPath
                    : candidate;
            bool exists = File.Exists(effective);
            list.Add(new OmronComponentInfo(
                Name: name,
                Path: effective,
                Exists: exists,
                Bitness: exists ? BaseVendorProfileDetector.DetectPeBitness(effective) : null,
                Sha256: exists ? ComputeSha256(effective) : null));
        }

        return list;
    }

    private static IReadOnlyList<OmronComRegistrationInfo> DiscoverComRegistrations()
    {
        var list = new List<OmronComRegistrationInfo>();
        foreach (var progId in ExpectedOmronProgIds)
        {
            list.Add(ObserveProgId(progId));
        }

        return list;
    }

    private static OmronComRegistrationInfo ObserveProgId(string progId)
    {
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

                return new OmronComRegistrationInfo(
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

        return new OmronComRegistrationInfo(
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