using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Win32;

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
    GxSimulator
}

public sealed record VendorSoftwareReport(
    [property: JsonPropertyName("kind")] VendorSoftwareKind Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("installed")] bool Installed,
    [property: JsonPropertyName("executablePath")] string? ExecutablePath,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("details")] string Details,
    [property: JsonPropertyName("disclaimer")] string Disclaimer = "Detection is read-only heuristic and does not imply valid vendor license or API authorization.");

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
            VendorSoftwareKind.GxWorks3 => CheckGxWorks3(),
            VendorSoftwareKind.SysmacStudio => CheckSysmacStudio(),
            VendorSoftwareKind.Codesys => CheckCodesys(),
            VendorSoftwareKind.InoProShop => CheckInoProShop(),
            VendorSoftwareKind.PlcSim => CheckPlcSim(),
            VendorSoftwareKind.GxSimulator => CheckGxSimulator(),
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

    private static VendorSoftwareReport CheckGxWorks3()
    {
        var candidatePaths = new[]
        {
            @"C:\Program Files (x86)\MELSOFT\GXW3\GXW3.exe",
            @"C:\Program Files\MELSOFT\GXW3\GXW3.exe"
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                return new VendorSoftwareReport(
                    Kind: VendorSoftwareKind.GxWorks3,
                    Name: "Mitsubishi GX Works3",
                    Installed: true,
                    ExecutablePath: path,
                    Version: vi.FileVersion ?? vi.ProductVersion,
                    Details: $"GX Works3 executable found at {path}.");
            }
        }

        return new VendorSoftwareReport(
            Kind: VendorSoftwareKind.GxWorks3,
            Name: "Mitsubishi GX Works3",
            Installed: false,
            ExecutablePath: null,
            Version: null,
            Details: "Mitsubishi GX Works3 not detected.");
    }

    private static VendorSoftwareReport CheckSysmacStudio()
    {
        var candidatePaths = new[]
        {
            @"C:\Program Files (x86)\OMRON\Sysmac Studio\SysmacStudio.exe",
            @"C:\Program Files\OMRON\Sysmac Studio\SysmacStudio.exe"
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                return new VendorSoftwareReport(
                    Kind: VendorSoftwareKind.SysmacStudio,
                    Name: "Omron Sysmac Studio",
                    Installed: true,
                    ExecutablePath: path,
                    Version: vi.FileVersion ?? vi.ProductVersion,
                    Details: $"Sysmac Studio executable found at {path}.");
            }
        }

        return new VendorSoftwareReport(
            Kind: VendorSoftwareKind.SysmacStudio,
            Name: "Omron Sysmac Studio",
            Installed: false,
            ExecutablePath: null,
            Version: null,
            Details: "Omron Sysmac Studio not detected.");
    }

    private static VendorSoftwareReport CheckCodesys()
    {
        var candidatePaths = new[]
        {
            @"C:\Program Files\CODESYS 3.5\CODESYS\Common\CODESYS.exe",
            @"C:\Program Files (x86)\CODESYS 3.5\CODESYS\Common\CODESYS.exe",
            @"C:\Program Files\3S CODESYS\CODESYS\Common\CODESYS.exe"
        };

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
                    Details: $"Siemens PLCSIM executable found at {path}.");
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
            @"C:\Program Files (x86)\MELSOFT\SIM3\GXSim3.exe",
            @"C:\Program Files\MELSOFT\SIM3\GXSim3.exe"
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                return new VendorSoftwareReport(
                    Kind: VendorSoftwareKind.GxSimulator,
                    Name: "Mitsubishi GX Simulator",
                    Installed: true,
                    ExecutablePath: path,
                    Version: vi.FileVersion ?? vi.ProductVersion,
                    Details: $"GX Simulator executable found at {path}.");
            }
        }

        return new VendorSoftwareReport(
            Kind: VendorSoftwareKind.GxSimulator,
            Name: "Mitsubishi GX Simulator",
            Installed: false,
            ExecutablePath: null,
            Version: null,
            Details: "Mitsubishi GX Simulator not detected.");
    }
}
