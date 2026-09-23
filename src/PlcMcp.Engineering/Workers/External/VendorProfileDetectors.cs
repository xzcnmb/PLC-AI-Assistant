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
    string Disclaimer = "Detection is read-only heuristic and does not imply valid vendor license, API authorization, or vendor compiler execution.");

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
                    string bitness = Environment.Is64BitOperatingSystem && !path.Contains("x86", StringComparison.OrdinalIgnoreCase)
                        ? "64-bit"
                        : "32-bit";

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

    protected override string[] CandidatePaths => new[]
    {
        @"C:\Program Files\CODESYS 3.5\CODESYS\Common\CODESYS.exe",
        @"C:\Program Files (x86)\CODESYS 3.5\CODESYS\Common\CODESYS.exe",
        @"C:\Program Files\3S CODESYS\CODESYS\Common\CODESYS.exe"
    };

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
/// </summary>
public sealed class GxWorksProfileDetector : BaseVendorProfileDetector
{
    public override PlcVendor Vendor => PlcVendor.Mitsubishi;
    public override string VendorName => "Mitsubishi";
    public override string ToolchainName => "Mitsubishi GX Works3";

    protected override string[] CandidatePaths => new[]
    {
        @"C:\Program Files (x86)\MELSOFT\GXW3\GXW3.exe",
        @"C:\Program Files\MELSOFT\GXW3\GXW3.exe"
    };

    protected override string? RegistryKeyPath => @"SOFTWARE\MITSUBISHI\GXW3";
}

/// <summary>
/// Profile detector for Omron Sysmac Studio environment.
/// </summary>
public sealed class SysmacProfileDetector : BaseVendorProfileDetector
{
    public override PlcVendor Vendor => PlcVendor.Omron;
    public override string VendorName => "Omron";
    public override string ToolchainName => "Omron Sysmac Studio";

    protected override string[] CandidatePaths => new[]
    {
        @"C:\Program Files (x86)\OMRON\Sysmac Studio\SysmacStudio.exe",
        @"C:\Program Files\OMRON\Sysmac Studio\SysmacStudio.exe"
    };

    protected override string? RegistryKeyPath => @"SOFTWARE\OMRON\Sysmac Studio";
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
