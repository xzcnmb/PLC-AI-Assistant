using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using PlcMcp.Engineering.Workers.External;

namespace PlcMcp.Engineering.Doctor;

public enum VendorSoftwareKind
{
    TiaPortalOpenness,
    MicroWinSmart,
    GxWorks3,
    SysmacStudio,
    Codesys,
    InoProShop,
    PlcSim,
    GxSimulator,
    CxServer
}

public sealed record VendorSoftwareReport(
    [property: JsonPropertyName("kind")] VendorSoftwareKind Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("installed")] bool Installed,
    [property: JsonPropertyName("executablePath")] string? ExecutablePath,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("details")] string Details,
    [property: JsonPropertyName("disclaimer")] string Disclaimer = "Detection is read-only heuristic and does not imply valid vendor license or API authorization.",
    [property: JsonPropertyName("registryVersion")] string? RegistryVersion = null,
    [property: JsonPropertyName("bitness")] string? Bitness = null,
    [property: JsonPropertyName("omronReport")] PlcMcp.Engineering.Workers.Omron.OmronDoctorReport? OmronReport = null);

public sealed record SystemDoctorReport(
    [property: JsonPropertyName("scannedAt")] DateTimeOffset ScannedAt,
    [property: JsonPropertyName("items")] IReadOnlyList<VendorSoftwareReport> Items,
    [property: JsonPropertyName("installedCount")] int InstalledCount,
    [property: JsonPropertyName("summary")] string Summary);

public interface IVendorDoctor
{
    SystemDoctorReport RunFullDiagnosis();
    VendorSoftwareReport CheckSoftware(VendorSoftwareKind kind);
}

public sealed class VendorDoctor : IVendorDoctor
{
    private readonly string? _customGxWorks3Path;
    private readonly string? _customSysmacStudioPath;
    private readonly string? _customCxServerPath;

    public VendorDoctor(
        string? customGxWorks3Path = null,
        string? customSysmacStudioPath = null,
        string? customCxServerPath = null)
    {
        _customGxWorks3Path = customGxWorks3Path;
        _customSysmacStudioPath = customSysmacStudioPath;
        _customCxServerPath = customCxServerPath;
    }

    public SystemDoctorReport RunFullDiagnosis()
    {
        var items = new List<VendorSoftwareReport>
        {
            CheckSoftware(VendorSoftwareKind.MicroWinSmart),
            CheckSoftware(VendorSoftwareKind.TiaPortalOpenness),
            CheckSoftware(VendorSoftwareKind.GxWorks3),
            CheckSoftware(VendorSoftwareKind.SysmacStudio),
            CheckSoftware(VendorSoftwareKind.Codesys),
            CheckSoftware(VendorSoftwareKind.InoProShop),
            CheckSoftware(VendorSoftwareKind.PlcSim),
            CheckSoftware(VendorSoftwareKind.GxSimulator)
        };

        int installed = items.Count(i => i.Installed);
        string summary = $"Vendor Doctor Scan: {installed}/{items.Count} vendor toolchains detected on local host.";

        return new SystemDoctorReport(
            ScannedAt: DateTimeOffset.UtcNow,
            Items: items,
            InstalledCount: installed,
            Summary: summary);
    }

    public VendorSoftwareReport CheckSoftware(VendorSoftwareKind kind)
    {
        return kind switch
        {
            VendorSoftwareKind.MicroWinSmart => CheckMicroWinSmart(),
            VendorSoftwareKind.TiaPortalOpenness => CheckTiaOpenness(),
            VendorSoftwareKind.GxWorks3 => CheckGxWorks3(_customGxWorks3Path),
            VendorSoftwareKind.SysmacStudio => CheckSysmacStudio(_customSysmacStudioPath, _customCxServerPath),
            VendorSoftwareKind.Codesys => CheckCodesys(),
            VendorSoftwareKind.InoProShop => CheckInoProShop(),
            VendorSoftwareKind.PlcSim => CheckPlcSim(),
            VendorSoftwareKind.GxSimulator => CheckGxSimulator(),
            VendorSoftwareKind.CxServer => CheckCxServer(_customCxServerPath),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    private static VendorSoftwareReport CheckMicroWinSmart()
    {
        var candidatePaths = new[]
        {
            @"C:\Program Files (x86)\Siemens\STEP 7-MicroWIN SMART\MWSmart.exe",
            @"C:\Program Files\Siemens\STEP 7-MicroWIN SMART\MWSmart.exe"
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                string version = vi.FileVersion ?? vi.ProductVersion ?? "Detected";
                return new VendorSoftwareReport(
                    Kind: VendorSoftwareKind.MicroWinSmart,
                    Name: "STEP 7-MicroWIN SMART",
                    Installed: true,
                    ExecutablePath: path,
                    Version: version,
                    Details: $"MicroWIN SMART executable detected at {path}.");
            }
        }

        return new VendorSoftwareReport(
            Kind: VendorSoftwareKind.MicroWinSmart,
            Name: "STEP 7-MicroWIN SMART",
            Installed: false,
            ExecutablePath: null,
            Version: null,
            Details: "STEP 7-MicroWIN SMART not found at default installation paths.");
    }

    private static VendorSoftwareReport CheckTiaOpenness()
    {
        // Check Registry or typical Openness DLL paths (e.g. Siemens.Engineering.dll)
        var candidateRoots = new[]
        {
            @"C:\Program Files\Siemens\Automation\Portal V18\PublicAPI\V18\Siemens.Engineering.dll",
            @"C:\Program Files\Siemens\Automation\Portal V17\PublicAPI\V17\Siemens.Engineering.dll",
            @"C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19\Siemens.Engineering.dll",
            @"C:\Program Files\Siemens\Automation\Portal V16\PublicAPI\V16\Siemens.Engineering.dll"
        };

        foreach (var path in candidateRoots)
        {
            if (File.Exists(path))
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                return new VendorSoftwareReport(
                    Kind: VendorSoftwareKind.TiaPortalOpenness,
                    Name: "Siemens TIA Portal Openness",
                    Installed: true,
                    ExecutablePath: path,
                    Version: vi.FileVersion ?? vi.ProductVersion ?? "Detected",
                    Details: $"TIA Openness API assembly located at {path}.");
            }
        }

        // Try registry check on Windows
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness");
                if (key != null)
                {
                    return new VendorSoftwareReport(
                        Kind: VendorSoftwareKind.TiaPortalOpenness,
                        Name: "Siemens TIA Portal Openness",
                        Installed: true,
                        ExecutablePath: null,
                        Version: "Registry Entry Found",
                        Details: "TIA Openness registry key found.");
                }
            }
            catch
            {
                // Ignore registry permissions
            }
        }

        return new VendorSoftwareReport(
            Kind: VendorSoftwareKind.TiaPortalOpenness,
            Name: "Siemens TIA Portal Openness",
            Installed: false,
            ExecutablePath: null,
            Version: null,
            Details: "TIA Portal Openness assembly/registry not detected.");
    }

    public static VendorSoftwareReport CheckGxWorks3(string? customPath = null)
    {
        var detector = new GxWorksProfileDetector(customPath);
        var profile = detector.DetectProfile();

        return new VendorSoftwareReport(
            Kind: VendorSoftwareKind.GxWorks3,
            Name: profile.ToolchainName,
            Installed: profile.Installed,
            ExecutablePath: profile.ExecutablePath,
            Version: profile.Version,
            Details: profile.Details,
            RegistryVersion: profile.RegistryVersion,
            Bitness: profile.Bitness);
    }

    public static VendorSoftwareReport CheckSysmacStudio(string? customSysmacPath = null, string? customCxServerPath = null)
    {
        var detector = new SysmacProfileDetector(customSysmacPath, customCxServerPath);
        var profile = detector.DetectProfile();
        var omronReport = detector.DiagnoseOmron(customSysmacPath, customCxServerPath);

        return new VendorSoftwareReport(
            Kind: VendorSoftwareKind.SysmacStudio,
            Name: profile.ToolchainName,
            Installed: profile.Installed,
            ExecutablePath: profile.ExecutablePath,
            Version: profile.Version,
            Details: profile.Details,
            RegistryVersion: profile.RegistryVersion,
            Bitness: profile.Bitness,
            OmronReport: omronReport);
    }

    public static VendorSoftwareReport CheckCxServer(string? customCxServerPath = null)
    {
        var detector = new CxServerProfileDetector(customCxServerPath);
        var profile = detector.DetectProfile();

        return new VendorSoftwareReport(
            Kind: VendorSoftwareKind.CxServer,
            Name: profile.ToolchainName,
            Installed: profile.Installed,
            ExecutablePath: profile.ExecutablePath,
            Version: profile.Version,
            Details: profile.Details,
            RegistryVersion: profile.RegistryVersion,
            Bitness: profile.Bitness);
    }

    public PlcMcp.Engineering.Workers.Omron.OmronDoctorReport DiagnoseOmron()
    {
        return PlcMcp.Engineering.Workers.Omron.OmronInstallationDoctor.DiagnoseHost(
            _customSysmacStudioPath,
            _customCxServerPath);
    }

    public static PlcMcp.Engineering.Workers.Omron.OmronDoctorReport DiagnoseOmron(
        string? customSysmacPath = null,
        string? customCxServerPath = null)
    {
        return PlcMcp.Engineering.Workers.Omron.OmronInstallationDoctor.DiagnoseHost(
            customSysmacPath,
            customCxServerPath);
    }

    private static VendorSoftwareReport CheckCodesys()
    {
        var candidatePaths = new List<string>
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
                    if (!candidatePaths.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        candidatePaths.Add(candidate);
                    }
                }
            }
            catch
            {
                // Ignore filesystem scan errors
            }
        }

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                return new VendorSoftwareReport(
                    Kind: VendorSoftwareKind.Codesys,
                    Name: "CODESYS V3",
                    Installed: true,
                    ExecutablePath: path,
                    Version: vi.FileVersion ?? vi.ProductVersion,
                    Details: $"CODESYS executable found at {path}.");
            }
        }

        return new VendorSoftwareReport(
            Kind: VendorSoftwareKind.Codesys,
            Name: "CODESYS V3",
            Installed: false,
            ExecutablePath: null,
            Version: null,
            Details: "CODESYS V3 not detected.");
    }

    private static VendorSoftwareReport CheckInoProShop()
    {
        var candidatePaths = new[]
        {
            @"C:\Program Files (x86)\Inovance\InoProShop\InoProShop.exe",
            @"C:\Program Files\Inovance\InoProShop\InoProShop.exe"
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                return new VendorSoftwareReport(
                    Kind: VendorSoftwareKind.InoProShop,
                    Name: "Inovance InoProShop",
                    Installed: true,
                    ExecutablePath: path,
                    Version: vi.FileVersion ?? vi.ProductVersion,
                    Details: $"InoProShop executable found at {path}.");
            }
        }

        return new VendorSoftwareReport(
            Kind: VendorSoftwareKind.InoProShop,
            Name: "Inovance InoProShop",
            Installed: false,
            ExecutablePath: null,
            Version: null,
            Details: "Inovance InoProShop not detected.");
    }

    private static VendorSoftwareReport CheckPlcSim()
    {
        var candidatePaths = new[]
        {
            @"C:\Program Files\Siemens\Automation\PLCSIMADV\Siemens.Simatic.PlcSim.Advanced.UserInterface.exe",
            @"C:\Program Files (x86)\Siemens\Automation\S7-PLCSIM\s7wsvwix.exe"
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                return new VendorSoftwareReport(
                    Kind: VendorSoftwareKind.PlcSim,
                    Name: "Siemens S7-PLCSIM",
                    Installed: true,
                    ExecutablePath: path,
                    Version: vi.FileVersion ?? vi.ProductVersion,
                    Details: $"S7-PLCSIM executable found at {path}.");
            }
        }

        return new VendorSoftwareReport(
            Kind: VendorSoftwareKind.PlcSim,
            Name: "Siemens S7-PLCSIM",
            Installed: false,
            ExecutablePath: null,
            Version: null,
            Details: "Siemens S7-PLCSIM not detected.");
    }

    private static VendorSoftwareReport CheckGxSimulator()
    {
        var candidatePaths = new[]
        {
            @"D:\gwork2\GPPW3\GXSimulator3\Common\GXS3SysSim.exe",
            @"C:\Program Files (x86)\MELSOFT\GXW3\GXSimulator3\Common\GXS3SysSim.exe",
            @"C:\Program Files\MELSOFT\GXW3\GXSimulator3\Common\GXS3SysSim.exe"
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                return new VendorSoftwareReport(
                    Kind: VendorSoftwareKind.GxSimulator,
                    Name: "Mitsubishi GX Simulator3",
                    Installed: true,
                    ExecutablePath: path,
                    Version: vi.FileVersion ?? vi.ProductVersion,
                    Details: $"GX Simulator3 executable found at {path}.");
            }
        }

        return new VendorSoftwareReport(
            Kind: VendorSoftwareKind.GxSimulator,
            Name: "Mitsubishi GX Simulator3",
            Installed: false,
            ExecutablePath: null,
            Version: null,
            Details: "Mitsubishi GX Simulator3 not detected.");
    }
}
