using System.Diagnostics;
using Microsoft.Win32;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Doctor;

namespace PlcMcp.Engineering.Workers.External;

/// <summary>
/// Specific descriptor and capability metadata for an external vendor engineering profile.
/// </summary>
public sealed record ExternalVendorProfile(
    PlcVendor Vendor,
    string VendorName,
    string ToolchainName,
    bool Installed,
    string? ExecutablePath,
    string? Version,
    string? Bitness,
    CapabilitySet Capabilities,
    string Details,
    string Disclaimer = "Detection is read-only heuristic and does not imply valid vendor license, API authorization, or vendor compiler execution.",
    string? RegistryVersion = null,
    IReadOnlyList<PlcMcp.Engineering.Workers.Omron.OmronComponentInfo>? OmronComponents = null,
    IReadOnlyList<PlcMcp.Engineering.Workers.Omron.OmronComRegistrationInfo>? OmronComRegistrations = null);

/// <summary>
/// Interface for detecting and inspecting vendor engineering software environments on the local host.
/// </summary>
public interface IVendorProfileDetector
{
    PlcVendor Vendor { get; }
    string VendorName { get; }
    string ToolchainName { get; }
    ExternalVendorProfile DetectProfile();
}

/// <summary>
/// Base class providing safe, read-only filesystem and registry detection routines.
/// Never alters system state, never launches processes, and strictly marks missing software as Unsupported.
/// </summary>
public abstract class BaseVendorProfileDetector : IVendorProfileDetector
{
    public abstract PlcVendor Vendor { get; }
    public abstract string VendorName { get; }
    public abstract string ToolchainName { get; }
    protected abstract string[] CandidatePaths { get; }
    protected virtual string? RegistryKeyPath => null;

    public virtual ExternalVendorProfile DetectProfile()
    {
        // 1. Check candidate file paths
        foreach (var path in CandidatePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(path);
                    string version = vi.FileVersion ?? vi.ProductVersion ?? "Detected";
                    string bitness = DetectPeBitness(path);
                    if (bitness == "Unknown")
                    {
                        bitness = Environment.Is64BitOperatingSystem && !path.Contains("x86", StringComparison.OrdinalIgnoreCase)
                            ? "64-bit"
                            : "32-bit";
                    }

                    return new ExternalVendorProfile(
                        Vendor: Vendor,
                        VendorName: VendorName,
                        ToolchainName: ToolchainName,
                        Installed: true,
                        ExecutablePath: path,
                        Version: version,
                        Bitness: bitness,
                        Capabilities: BuildDefaultCapabilities(isInstalled: true),
                        Details: $"{ToolchainName} detected at '{path}'. Offline inspection/export/compilation requires verified worker bridge.");
                }
                catch (Exception ex)
                {
                    return new ExternalVendorProfile(
                        Vendor: Vendor,
                        VendorName: VendorName,
                        ToolchainName: ToolchainName,
                        Installed: true,
                        ExecutablePath: path,
                        Version: "Unknown",
                        Bitness: "Unknown",
                        Capabilities: BuildDefaultCapabilities(isInstalled: true),
                        Details: $"Found {ToolchainName} at '{path}', but metadata read failed: {ex.Message}");
                }
            }
        }

        // 2. Check Windows registry if configured
        if (OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(RegistryKeyPath))
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath);
                if (key != null)
                {
                    return new ExternalVendorProfile(
                        Vendor: Vendor,
                        VendorName: VendorName,
                        ToolchainName: ToolchainName,
                        Installed: true,
                        ExecutablePath: null,
                        Version: "Registry Entry Detected",
                        Bitness: "Unknown",
                        Capabilities: BuildDefaultCapabilities(isInstalled: true),
                        Details: $"{ToolchainName} registry installation key found at '{RegistryKeyPath}'.");
                }
            }
            catch
            {
                // Silently swallow registry permission exceptions
            }
        }

        // 3. Not installed -> Honestly return Unsupported without feigning
        return new ExternalVendorProfile(
            Vendor: Vendor,
            VendorName: VendorName,
            ToolchainName: ToolchainName,
            Installed: false,
            ExecutablePath: null,
            Version: null,
            Bitness: null,
            Capabilities: BuildDefaultCapabilities(isInstalled: false),
            Details: $"{ToolchainName} is NOT installed or detected at standard locations on this host. Features are Unsupported.");
    }

    public static string DetectPeBitness(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return "Unknown";

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x40)
                return "Unknown";

            using var reader = new BinaryReader(fs);

            // Read DOS Header e_magic
            ushort mz = reader.ReadUInt16();
            if (mz != 0x5A4D) // 'MZ'
                return "Unknown";

            // Seek to e_lfanew at 0x3C
            fs.Seek(0x3C, SeekOrigin.Begin);
            int peHeaderOffset = reader.ReadInt32();

            if (peHeaderOffset <= 0 || peHeaderOffset > fs.Length - 24)
                return "Unknown";

            // Seek to PE signature
            fs.Seek(peHeaderOffset, SeekOrigin.Begin);
            uint peSig = reader.ReadUInt32();
            if (peSig != 0x00004550) // 'PE\0\0'
                return "Unknown";

            // Read COFF Machine
            ushort machine = reader.ReadUInt16();
            return machine switch
            {
                0x014C => "32-bit", // IMAGE_FILE_MACHINE_I386
                0x8664 => "64-bit", // IMAGE_FILE_MACHINE_AMD64
                0x0200 => "64-bit", // IMAGE_FILE_MACHINE_IA64
                0xAA64 => "64-bit", // IMAGE_FILE_MACHINE_ARM64
                _ => "Unknown"
            };
        }
        catch
        {
            return "Unknown";
        }
    }

    protected static CapabilitySet BuildDefaultCapabilities(bool isInstalled)
    {
        var status = isInstalled ? CapabilityStatus.Experimental : CapabilityStatus.Unsupported;
        var offlineNotes = isInstalled
            ? "Available via external worker bridge when configured and allowlisted."
            : "Unavailable: Toolchain not detected on host.";

        var items = new List<CapabilityDescriptor>
        {
            new("InspectProject", status, offlineNotes),
            new("ExportPou", status, offlineNotes),
            new("ValidatePou", status, offlineNotes),
            new("CompileProject", status, isInstalled
                ? "Heuristic syntax validation / external worker compile only; never fakes official compiler verification."
                : "Unavailable: Toolchain not detected."),
            new("DiffProjects", status, offlineNotes),
            // Dangerous operations are ALWAYS Unsupported in this tier
            new("DownloadProject", CapabilityStatus.Unsupported, "Strictly rejected: Physical/online PLC download is forbidden without cryptographic hardware token."),
            new("SetRunMode", CapabilityStatus.Unsupported, "Strictly rejected: Changing PLC run/stop mode is forbidden in external worker protocol."),
            new("ForceIo", CapabilityStatus.Unsupported, "Strictly rejected: Forcing I/O is forbidden in external worker protocol."),
            new("HmiPublish", CapabilityStatus.Unsupported, "Strictly rejected: HMI publishing is forbidden in external worker protocol.")
        };

        return new CapabilitySet(items);
    }
}

/// <summary>
/// Profile detector for 3S CODESYS V3 engineering environment.
/// </summary>
public sealed class CodesysProfileDetector : BaseVendorProfileDetector
{
    public override PlcVendor Vendor => PlcVendor.Generic;
    public override string VendorName => "CODESYS";
    public override string ToolchainName => "CODESYS Development System V3";

    protected override string[] CandidatePaths
    {
        get
        {
            var paths = new List<string>
            {
                @"C:\Program Files\CODESYS 3.5\CODESYS\Common\CODESYS.exe",
                @"C:\Program Files (x86)\CODESYS 3.5\CODESYS\Common\CODESYS.exe",
                @"C:\Program Files\3S CODESYS\CODESYS\Common\CODESYS.exe"
            };

            var searchRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            foreach (var root in searchRoots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    continue;

                try
                {
                    foreach (var dir in Directory.GetDirectories(root, "*CODESYS*"))
                    {
                        var candidate = Path.Combine(dir, "CODESYS", "Common", "CODESYS.exe");
                        if (!paths.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                        {
                            paths.Add(candidate);
                        }
                    }
                }
                catch
                {
                    // Ignore filesystem scan errors
                }
            }

            return paths.ToArray();
        }
    }

    protected override string? RegistryKeyPath => @"SOFTWARE\3S-Smart Software Solutions\CODESYS";
}

/// <summary>
/// Profile detector for Siemens TIA Portal Openness environment.
/// </summary>
public sealed class TiaPortalProfileDetector : BaseVendorProfileDetector
{
    public override PlcVendor Vendor => PlcVendor.Siemens;
    public override string VendorName => "Siemens";
    public override string ToolchainName => "Siemens TIA Portal Openness";

    protected override string[] CandidatePaths => new[]
    {
        @"C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19\Siemens.Engineering.dll",
        @"C:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18\Siemens.Engineering.dll",
        @"C:\Program Files\Siemens\Automation\Portal V17\PublicAPI\V17\Siemens.Engineering.dll",
        @"C:\Program Files\Siemens\Automation\Portal V16\PublicAPI\V16\Siemens.Engineering.dll"
    };

    protected override string? RegistryKeyPath => @"SOFTWARE\Siemens\Automation\Openness";
}

/// <summary>
/// Profile detector for Mitsubishi GX Works3 environment.
/// Supports standard installation locations (including D:\gwork2\GPPW3\GXW3.exe), custom paths, and registry discovery.
/// Strictly read-only: does not start GXW3, does not open projects, does not connect to PLC, and does not call ServiceBus.
/// </summary>
public sealed class GxWorksProfileDetector : BaseVendorProfileDetector
{
    private readonly string? _customPath;

    public static readonly string[] DefaultCandidatePaths = new[]
    {
        @"D:\gwork2\GPPW3\GXW3.exe",
        @"C:\Program Files (x86)\MELSOFT\GXW3\GXW3.exe",
        @"C:\Program Files\MELSOFT\GXW3\GXW3.exe",
        @"D:\gwork2\GXW3.exe",
        @"C:\gwork2\GPPW3\GXW3.exe",
        @"C:\gwork2\GXW3.exe"
    };

    public GxWorksProfileDetector(string? customPath = null)
    {
        _customPath = customPath;
    }

    public override PlcVendor Vendor => PlcVendor.Mitsubishi;
    public override string VendorName => "Mitsubishi";
    public override string ToolchainName => "Mitsubishi GX Works3";

    protected override string[] CandidatePaths => DefaultCandidatePaths;

    protected override string? RegistryKeyPath => @"SOFTWARE\MITSUBISHI\SWnDN-GPPW3\CurrentVersion";

    public ExternalVendorProfile DetectProfile(string? customPath)
    {
        return DetectProfileCore(customPath ?? _customPath);
    }

    public override ExternalVendorProfile DetectProfile()
    {
        return DetectProfileCore(_customPath);
    }

    private ExternalVendorProfile DetectProfileCore(string? customPath)
    {
        // 1. Explicit custom path has highest priority. If specified, NEVER fall back to another installation.
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            string? resolvedPath = ResolveExePath(customPath);
            if (resolvedPath != null && File.Exists(resolvedPath))
            {
                return CreateInstalledProfile(resolvedPath, registryVersion: null);
            }

            return new ExternalVendorProfile(
                Vendor: Vendor,
                VendorName: VendorName,
                ToolchainName: ToolchainName,
                Installed: false,
                ExecutablePath: customPath,
                Version: null,
                Bitness: null,
                Capabilities: BuildGxWorksCapabilities(isInstalled: false),
                Details: $"Custom GX Works3 path '{customPath}' does not exist. Explicit path does not fall back to other installations.");
        }

        // 2. Discover from Registry (using Registry32 / Registry64 without duplicating WOW6432Node)
        var (regExePath, regProductVer, regKeyPath) = DiscoverGxWorks3Registry();
        if (!string.IsNullOrWhiteSpace(regExePath) && File.Exists(regExePath))
        {
            return CreateInstalledProfile(regExePath, regProductVer);
        }

        // 3. Fixed default candidate paths
        foreach (var path in DefaultCandidatePaths)
        {
            if (File.Exists(path))
            {
                return CreateInstalledProfile(path, regProductVer);
            }
        }

        // 4. Registry key present but executable not found on disk
        if (OperatingSystem.IsWindows() && (!string.IsNullOrWhiteSpace(regKeyPath) || !string.IsNullOrWhiteSpace(regProductVer)))
        {
            return new ExternalVendorProfile(
                Vendor: Vendor,
                VendorName: VendorName,
                ToolchainName: ToolchainName,
                Installed: true,
                ExecutablePath: null,
                Version: regProductVer ?? "Detected",
                Bitness: "32-bit",
                Capabilities: BuildGxWorksCapabilities(isInstalled: true),
                Details: $"{ToolchainName} registry installation found at '{regKeyPath ?? "SOFTWARE\\MITSUBISHI"}'.",
                RegistryVersion: regProductVer);
        }

        // 5. Not installed -> Unsupported
        return new ExternalVendorProfile(
            Vendor: Vendor,
            VendorName: VendorName,
            ToolchainName: ToolchainName,
            Installed: false,
            ExecutablePath: null,
            Version: null,
            Bitness: null,
            Capabilities: BuildGxWorksCapabilities(isInstalled: false),
            Details: $"{ToolchainName} is NOT installed or detected at standard locations on this host. Features are Unsupported.");
    }

    private ExternalVendorProfile CreateInstalledProfile(string path, string? registryVersion)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            string version = vi.FileVersion ?? vi.ProductVersion ?? "Detected";
            string bitness = DetectPeBitness(path);
            if (bitness == "Unknown")
            {
                bitness = "32-bit";
            }

            string regNote = !string.IsNullOrWhiteSpace(registryVersion)
                ? $", Registry Version: {registryVersion}"
                : "";

            return new ExternalVendorProfile(
                Vendor: Vendor,
                VendorName: VendorName,
                ToolchainName: ToolchainName,
                Installed: true,
                ExecutablePath: path,
                Version: version,
                Bitness: bitness,
                Capabilities: BuildGxWorksCapabilities(isInstalled: true),
                Details: $"{ToolchainName} detected at '{path}'. FileVersion: {version}{regNote}, Bitness: {bitness}. P0 read-only diagnosis tier.",
                RegistryVersion: registryVersion);
        }
        catch (Exception ex)
        {
            return new ExternalVendorProfile(
                Vendor: Vendor,
                VendorName: VendorName,
                ToolchainName: ToolchainName,
                Installed: true,
                ExecutablePath: path,
                Version: "Unknown",
                Bitness: "Unknown",
                Capabilities: BuildGxWorksCapabilities(isInstalled: true),
                Details: $"Found {ToolchainName} at '{path}', but metadata read failed: {ex.Message}",
                RegistryVersion: registryVersion);
        }
    }

    public static string? ResolveExePath(string path)
    {
        var trimmed = path.Trim('\"', ' ', '\t');
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        if (File.Exists(trimmed))
            return trimmed;

        if (Directory.Exists(trimmed))
        {
            var p1 = Path.Combine(trimmed, "GXW3.exe");
            if (File.Exists(p1)) return p1;
            var p2 = Path.Combine(trimmed, "GPPW3", "GXW3.exe");
            if (File.Exists(p2)) return p2;
        }

        return trimmed;
    }

    public static CapabilitySet BuildGxWorksCapabilities(bool isInstalled)
    {
        var offlineNotes = isInstalled
            ? "P0 read-only diagnosis tier provides toolchain installation observability only. Execution requires future verified worker bridge."
            : "Unavailable: Toolchain not detected on host.";

        var items = new List<CapabilityDescriptor>
        {
            // Offline capabilities: strictly Unsupported in P0 read-only tier
            new("InspectProject", CapabilityStatus.Unsupported, offlineNotes),
            new("ExportPou", CapabilityStatus.Unsupported, offlineNotes),
            new("ValidatePou", CapabilityStatus.Unsupported, offlineNotes),
            new("CompileProject", CapabilityStatus.Unsupported, offlineNotes),
            new("DiffProjects", CapabilityStatus.Unsupported, offlineNotes),

            // Dangerous & Online operations: strictly Unsupported
            new("DownloadProject", CapabilityStatus.Unsupported, "Strictly rejected: Physical/online PLC download is forbidden without cryptographic hardware token."),
            new("SetRunMode", CapabilityStatus.Unsupported, "Strictly rejected: Changing PLC run/stop mode is forbidden in external worker protocol."),
            new("ForceIo", CapabilityStatus.Unsupported, "Strictly rejected: Forcing I/O is forbidden in external worker protocol."),
            new("WritePlc", CapabilityStatus.Unsupported, "Strictly rejected: Online writing to PLC memory/tags is forbidden in P0 read-only tier."),
            new("OnlineMonitor", CapabilityStatus.Unsupported, "Strictly rejected: Online connection or monitoring to PLC is forbidden in P0 read-only tier."),
            new("OnlineConnect", CapabilityStatus.Unsupported, "Strictly rejected: Establishing online connection to physical PLC is forbidden."),
            new("HmiPublish", CapabilityStatus.Unsupported, "Strictly rejected: HMI publishing is forbidden in external worker protocol.")
        };

        return new CapabilitySet(items);
    }

    public static (string? ExePath, string? RegistryVersion, string? KeyPath) DiscoverGxWorks3Registry()
    {
        if (!OperatingSystem.IsWindows())
            return (null, null, null);

        var views = new[] { RegistryView.Registry32, RegistryView.Registry64 };
        string? foundExePath = null;
        string? foundVersion = null;
        string? foundKeyPath = null;

        foreach (var view in views)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);

                // 1. SWnDN-GPPW3\App -> File Location
                try
                {
                    using var appKey = baseKey.OpenSubKey(@"SOFTWARE\MITSUBISHI\SWnDN-GPPW3\App");
                    if (appKey?.GetValue("File Location") is string fileLoc && !string.IsNullOrWhiteSpace(fileLoc))
                    {
                        foundKeyPath ??= @"SOFTWARE\MITSUBISHI\SWnDN-GPPW3\App";
                        if (File.Exists(fileLoc))
                        {
                            foundExePath ??= fileLoc;
                        }
                    }
                }
                catch { }

                // 2. SWnDN-GPPW3\CurrentVersion -> InstallPath & MajorVersion/MinorVersion
                try
                {
                    using var cvKey = baseKey.OpenSubKey(@"SOFTWARE\MITSUBISHI\SWnDN-GPPW3\CurrentVersion");
                    if (cvKey != null)
                    {
                        foundKeyPath ??= @"SOFTWARE\MITSUBISHI\SWnDN-GPPW3\CurrentVersion";
                        var major = cvKey.GetValue("MajorVersion")?.ToString();
                        var minor = cvKey.GetValue("MinorVersion")?.ToString();
                        if (!string.IsNullOrWhiteSpace(major) && !string.IsNullOrWhiteSpace(minor))
                        {
                            foundVersion ??= $"{major}.{minor}";
                        }

                        if (cvKey.GetValue("InstallPath") is string installPath && !string.IsNullOrWhiteSpace(installPath))
                        {
                            var c1 = Path.Combine(installPath, "GXW3.exe");
                            var c2 = Path.Combine(installPath, "GPPW3", "GXW3.exe");
                            if (File.Exists(c1))
                                foundExePath ??= c1;
                            else if (File.Exists(c2))
                                foundExePath ??= c2;
                        }
                    }
                }
                catch { }

                // 3. Uninstall entries -> DisplayName == "GX Works3"
                try
                {
                    using var uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstallKey != null)
                    {
                        foreach (var subName in uninstallKey.GetSubKeyNames())
                        {
                            try
                            {
                                using var sub = uninstallKey.OpenSubKey(subName);
                                if (sub == null) continue;

                                var dispName = sub.GetValue("DisplayName") as string;
                                if (dispName != null && dispName.Contains("GX Works3", StringComparison.OrdinalIgnoreCase))
                                {
                                    foundKeyPath ??= $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{subName}";
                                    var dispVer = sub.GetValue("DisplayVersion") as string;
                                    if (!string.IsNullOrWhiteSpace(dispVer))
                                    {
                                        foundVersion ??= dispVer;
                                    }

                                    var installLoc = sub.GetValue("InstallLocation") as string;
                                    if (!string.IsNullOrWhiteSpace(installLoc))
                                    {
                                        var c1 = Path.Combine(installLoc, "GPPW3", "GXW3.exe");
                                        var c2 = Path.Combine(installLoc, "GXW3.exe");
                                        if (File.Exists(c1))
                                            foundExePath ??= c1;
                                        else if (File.Exists(c2))
                                            foundExePath ??= c2;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                // 4. Fallback: SOFTWARE\MITSUBISHI\GXW3
                try
                {
                    using var gxw3Key = baseKey.OpenSubKey(@"SOFTWARE\MITSUBISHI\GXW3");
                    if (gxw3Key != null)
                    {
                        foundKeyPath ??= @"SOFTWARE\MITSUBISHI\GXW3";
                    }
                }
                catch { }

                if (foundExePath != null && foundVersion != null)
                    break;
            }
            catch { }
        }

        return (foundExePath, foundVersion, foundKeyPath);
    }
}

/// <summary>
/// Profile detector for Omron Sysmac Studio environment.
/// </summary>
public sealed class SysmacProfileDetector : BaseVendorProfileDetector
{
    private readonly string? _customPath;
    private readonly string? _customCxPath;

    public SysmacProfileDetector(string? customPath = null, string? customCxServerPath = null)
    {
        _customPath = customPath;
        _customCxPath = customCxServerPath;
    }

    public override PlcVendor Vendor => PlcVendor.Omron;
    public override string VendorName => "Omron";
    public override string ToolchainName => "Omron Sysmac Studio";

    protected override string[] CandidatePaths => PlcMcp.Engineering.Workers.Omron.OmronInstallationDoctor.DefaultSysmacCandidatePaths;

    protected override string? RegistryKeyPath => @"SOFTWARE\OMRON\Sysmac Studio";

    public ExternalVendorProfile DetectProfile(string? customSysmacPath, string? customCxServerPath = null)
    {
        return DetectProfileCore(customSysmacPath ?? _customPath, customCxServerPath ?? _customCxPath);
    }

    public override ExternalVendorProfile DetectProfile()
    {
        return DetectProfileCore(_customPath, _customCxPath);
    }

    private ExternalVendorProfile DetectProfileCore(string? customPath, string? customCxPath)
    {
        var doctorReport = PlcMcp.Engineering.Workers.Omron.OmronInstallationDoctor.DiagnoseHost(customPath, customCxPath);

        if (!string.IsNullOrWhiteSpace(customPath) && !doctorReport.SysmacInstalled)
        {
            return new ExternalVendorProfile(
                Vendor: Vendor,
                VendorName: VendorName,
                ToolchainName: ToolchainName,
                Installed: false,
                ExecutablePath: customPath,
                Version: null,
                Bitness: null,
                Capabilities: doctorReport.Capabilities,
                Details: $"Custom Sysmac Studio path '{customPath}' does not exist. Explicit path does not fall back to other installations.");
        }

        if (doctorReport.SysmacInstalled)
        {
            string regNote = !string.IsNullOrWhiteSpace(doctorReport.SysmacRegistryVersion)
                ? $", Registry Version: {doctorReport.SysmacRegistryVersion}"
                : "";

            string cxNote = doctorReport.CxServerInstalled
                ? $", CX-Server detected at '{doctorReport.CxServerExePath}' ({doctorReport.CxServerBitness})"
                : ", CX-Server not detected";

            string details = $"{ToolchainName} detected at '{doctorReport.SysmacExePath}'. " +
                             $"FileVersion: {doctorReport.SysmacVersion}{regNote}, Bitness: {doctorReport.SysmacBitness}{cxNote}. " +
                             $"P0 read-only diagnosis tier; all operational capabilities Unsupported.";

            return new ExternalVendorProfile(
                Vendor: Vendor,
                VendorName: VendorName,
                ToolchainName: ToolchainName,
                Installed: true,
                ExecutablePath: doctorReport.SysmacExePath,
                Version: doctorReport.SysmacVersion,
                Bitness: doctorReport.SysmacBitness,
                Capabilities: doctorReport.Capabilities,
                Details: details,
                RegistryVersion: doctorReport.SysmacRegistryVersion,
                OmronComponents: doctorReport.Components,
                OmronComRegistrations: doctorReport.ComRegistrations);
        }

        return new ExternalVendorProfile(
            Vendor: Vendor,
            VendorName: VendorName,
            ToolchainName: ToolchainName,
            Installed: false,
            ExecutablePath: null,
            Version: null,
            Bitness: null,
            Capabilities: doctorReport.Capabilities,
            Details: $"{ToolchainName} is NOT installed or detected on this host. Features are Unsupported.",
            OmronComponents: doctorReport.Components,
            OmronComRegistrations: doctorReport.ComRegistrations);
    }

    public PlcMcp.Engineering.Workers.Omron.OmronDoctorReport DiagnoseOmron(string? customSysmacPath = null, string? customCxServerPath = null)
    {
        return PlcMcp.Engineering.Workers.Omron.OmronInstallationDoctor.DiagnoseHost(
            customSysmacPath ?? _customPath,
            customCxServerPath ?? _customCxPath);
    }
}

/// <summary>
/// Profile detector for Omron CX-Server environment.
/// </summary>
public sealed class CxServerProfileDetector : BaseVendorProfileDetector
{
    private readonly string? _customPath;

    public CxServerProfileDetector(string? customPath = null)
    {
        _customPath = customPath;
    }

    public override PlcVendor Vendor => PlcVendor.Omron;
    public override string VendorName => "Omron";
    public override string ToolchainName => "Omron CX-Server";

    protected override string[] CandidatePaths => PlcMcp.Engineering.Workers.Omron.OmronInstallationDoctor.DefaultCxServerCandidatePaths;

    protected override string? RegistryKeyPath => @"SOFTWARE\OMRON\CX-Server";

    public ExternalVendorProfile DetectProfile(string? customPath)
    {
        return DetectProfileCore(customPath ?? _customPath);
    }

    public override ExternalVendorProfile DetectProfile()
    {
        return DetectProfileCore(_customPath);
    }

    private ExternalVendorProfile DetectProfileCore(string? customPath)
    {
        var (exe, ver, bit, regVer, installed, details) =
            PlcMcp.Engineering.Workers.Omron.OmronInstallationDoctor.DetectCxServer(customPath);

        return new ExternalVendorProfile(
            Vendor: Vendor,
            VendorName: VendorName,
            ToolchainName: ToolchainName,
            Installed: installed,
            ExecutablePath: exe,
            Version: ver,
            Bitness: bit,
            Capabilities: PlcMcp.Engineering.Workers.Omron.OmronInstallationDoctor.BuildOmronCapabilities(installed),
            Details: details,
            RegistryVersion: regVer);
    }
}

/// <summary>
/// Profile detector for Inovance InoProShop environment.
/// </summary>
public sealed class InoProShopProfileDetector : BaseVendorProfileDetector
{
    public override PlcVendor Vendor => PlcVendor.Inovance;
    public override string VendorName => "Inovance";
    public override string ToolchainName => "Inovance InoProShop";

    protected override string[] CandidatePaths => new[]
    {
        @"C:\Program Files (x86)\Inovance\InoProShop\InoProShop.exe",
        @"C:\Program Files\Inovance\InoProShop\InoProShop.exe"
    };

    protected override string? RegistryKeyPath => @"SOFTWARE\Inovance\InoProShop";
}

/// <summary>
/// Registry containing all per-vendor profile detectors.
/// </summary>
public sealed class VendorProfileDetectorRegistry
{
    private readonly Dictionary<string, IVendorProfileDetector> _detectors = new(StringComparer.OrdinalIgnoreCase);

    public VendorProfileDetectorRegistry(IEnumerable<IVendorProfileDetector>? detectors = null)
    {
        var list = detectors ?? new IVendorProfileDetector[]
        {
            new CodesysProfileDetector(),
            new TiaPortalProfileDetector(),
            new GxWorksProfileDetector(),
            new SysmacProfileDetector(),
            new InoProShopProfileDetector()
        };

        foreach (var d in list)
        {
            _detectors[d.VendorName] = d;
            _detectors[d.ToolchainName] = d;
            _detectors[d.Vendor.ToString()] = d;
        }
    }

    public IVendorProfileDetector? GetDetector(string vendorOrToolchain)
    {
        _detectors.TryGetValue(vendorOrToolchain, out var d);
        return d;
    }

    public IReadOnlyList<ExternalVendorProfile> DetectAll()
    {
        return _detectors.Values
            .DistinctBy(d => d.ToolchainName)
            .Select(d => d.DetectProfile())
            .ToList();
    }
}
