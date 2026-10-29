using System.Diagnostics;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Doctor;
using PlcMcp.Engineering.Workers.External;

namespace PlcMcp.Engineering.Tests;

public class GxWorks3DoctorTests
{
    private const string ExpectedHostGxWorks3Exe = @"D:\gwork2\GPPW3\GXW3.exe";
    private const string ExpectedFileVersion = "1.128.0.1";

    [Fact]
    public void GxWorksProfileDetector_DetectsHostInstallation_WhenPresent()
    {
        var detector = new GxWorksProfileDetector();
        var profile = detector.DetectProfile();

        Assert.NotNull(profile);
        Assert.Equal(PlcVendor.Mitsubishi, profile.Vendor);
        Assert.Equal("Mitsubishi", profile.VendorName);
        Assert.Equal("Mitsubishi GX Works3", profile.ToolchainName);

        if (File.Exists(ExpectedHostGxWorks3Exe))
        {
            Assert.True(profile.Installed);
            Assert.NotNull(profile.ExecutablePath);
            Assert.True(File.Exists(profile.ExecutablePath));
            Assert.Equal("32-bit", profile.Bitness);
            Assert.NotNull(profile.Version);
            Assert.Equal(ExpectedFileVersion, profile.Version);
            Assert.NotNull(profile.RegistryVersion);
            // Dynamic registry version (e.g. 1.128J or 1.128.04519), not hardcoded
            Assert.Contains("1.128", profile.RegistryVersion);
        }
    }

    [Fact]
    public void GxWorksProfileDetector_SupportsCustomExplicitPath()
    {
        if (File.Exists(ExpectedHostGxWorks3Exe))
        {
            var detector = new GxWorksProfileDetector(customPath: ExpectedHostGxWorks3Exe);
            var profile = detector.DetectProfile();

            Assert.True(profile.Installed);
            Assert.Equal(ExpectedHostGxWorks3Exe, profile.ExecutablePath, ignoreCase: true);
            Assert.Equal("32-bit", profile.Bitness);
            Assert.Equal(ExpectedFileVersion, profile.Version);
        }
    }

    [Fact]
    public void GxWorksProfileDetector_ExplicitCustomPath_NeverFallsBackToHostInstallation()
    {
        var fakePath = @"Z:\NonExistent\MELSOFT\GXW3\GXW3.exe";
        var detector = new GxWorksProfileDetector(customPath: fakePath);
        var profile = detector.DetectProfile();

        Assert.NotNull(profile);
        Assert.False(profile.Installed);
        Assert.Equal(fakePath, profile.ExecutablePath);
        Assert.Null(profile.Version);
        Assert.Contains("does not exist", profile.Details);
        Assert.Contains("does not fall back", profile.Details);
    }

    [Fact]
    public void GxWorksProfileDetector_PeBitnessDetection_ReadsRealHeadersWithoutInferringFromPath()
    {
        if (File.Exists(ExpectedHostGxWorks3Exe))
        {
            // GXW3.exe is in D:\gwork2\GPPW3\GXW3.exe (no "x86" substring in the path!)
            // A naive path check `path.Contains("x86")` would falsely mark it 64-bit on 64-bit OS!
            Assert.DoesNotContain("x86", ExpectedHostGxWorks3Exe, StringComparison.OrdinalIgnoreCase);

            string bitness = GxWorksProfileDetector.DetectPeBitness(ExpectedHostGxWorks3Exe);
            Assert.Equal("32-bit", bitness);
        }

        // Test with a 64-bit binary if available (e.g. cmd.exe on 64-bit OS)
        var cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (File.Exists(cmdPath) && Environment.Is64BitOperatingSystem)
        {
            string bitness = GxWorksProfileDetector.DetectPeBitness(cmdPath);
            Assert.Equal("64-bit", bitness);
        }

        // Test with non-existent or invalid file
        Assert.Equal("Unknown", GxWorksProfileDetector.DetectPeBitness(@"Z:\fake\does_not_exist.exe"));
    }

    [Fact]
    public void GxWorksProfileDetector_Capabilities_StrictlyUnsupportedInP0Tier()
    {
        var installedCaps = GxWorksProfileDetector.BuildGxWorksCapabilities(isInstalled: true);
        var uninstalledCaps = GxWorksProfileDetector.BuildGxWorksCapabilities(isInstalled: false);

        // All capabilities in P0 diagnosis tier must be Unsupported (only toolchain installation observability)
        foreach (var cap in installedCaps.Items)
        {
            Assert.Equal(CapabilityStatus.Unsupported, cap.Status);
        }

        // Dangerous and online capabilities must ALWAYS be Unsupported
        var strictlyForbidden = new[]
        {
            "InspectProject",
            "ExportPou",
            "ValidatePou",
            "CompileProject",
            "DiffProjects",
            "DownloadProject",
            "SetRunMode",
            "ForceIo",
            "WritePlc",
            "OnlineMonitor",
            "OnlineConnect",
            "HmiPublish"
        };

        foreach (var forbidden in strictlyForbidden)
        {
            var status = installedCaps.GetStatus(forbidden);
            Assert.Equal(CapabilityStatus.Unsupported, status);

            var uninstalledStatus = uninstalledCaps.GetStatus(forbidden);
            Assert.Equal(CapabilityStatus.Unsupported, uninstalledStatus);
        }

        // All uninstalled capabilities must also be Unsupported
        foreach (var cap in uninstalledCaps.Items)
        {
            Assert.Equal(CapabilityStatus.Unsupported, cap.Status);
        }
    }

    [Fact]
    public void VendorDoctor_CheckGxWorks3_DetectsHostInstallation()
    {
        var doctor = new VendorDoctor();
        var report = doctor.CheckSoftware(VendorSoftwareKind.GxWorks3);

        Assert.NotNull(report);
        Assert.Equal(VendorSoftwareKind.GxWorks3, report.Kind);
        Assert.Equal("Mitsubishi GX Works3", report.Name);

        if (File.Exists(ExpectedHostGxWorks3Exe))
        {
            Assert.True(report.Installed);
            Assert.NotNull(report.ExecutablePath);
            Assert.True(File.Exists(report.ExecutablePath));
            Assert.Equal(ExpectedFileVersion, report.Version);
            Assert.Equal("32-bit", report.Bitness);
            Assert.NotNull(report.RegistryVersion);
            Assert.Contains("1.128", report.RegistryVersion);
            Assert.Contains("GX Works3 detected", report.Details);
        }
    }

    [Fact]
    public void VendorDoctor_CheckGxWorks3_StaticMethodWithCustomPath()
    {
        if (File.Exists(ExpectedHostGxWorks3Exe))
        {
            var report = VendorDoctor.CheckGxWorks3(ExpectedHostGxWorks3Exe);
            Assert.True(report.Installed);
            Assert.Equal(ExpectedHostGxWorks3Exe, report.ExecutablePath, ignoreCase: true);
            Assert.Equal(ExpectedFileVersion, report.Version);
            Assert.Equal("32-bit", report.Bitness);
        }
    }

    [Fact]
    public void VendorDoctor_RunFullDiagnosis_IncludesEnhancedGxWorks3()
    {
        var doctor = new VendorDoctor();
        var report = doctor.RunFullDiagnosis();

        Assert.NotNull(report);
        var gxw3Item = report.Items.FirstOrDefault(i => i.Kind == VendorSoftwareKind.GxWorks3);
        Assert.NotNull(gxw3Item);

        if (File.Exists(ExpectedHostGxWorks3Exe))
        {
            Assert.True(gxw3Item.Installed);
            Assert.Equal(ExpectedHostGxWorks3Exe, gxw3Item.ExecutablePath, ignoreCase: true);
            Assert.Equal(ExpectedFileVersion, gxw3Item.Version);
            Assert.Equal("32-bit", gxw3Item.Bitness);
            Assert.NotNull(gxw3Item.RegistryVersion);
            Assert.Contains("1.128", gxw3Item.RegistryVersion);
        }
    }

    [Fact]
    public void VendorProfileDetectorRegistry_DiscoversGxWorksProfile()
    {
        var registry = new VendorProfileDetectorRegistry();
        var detector = registry.GetDetector("Mitsubishi");

        Assert.NotNull(detector);
        var profile = detector.DetectProfile();
        Assert.Equal(PlcVendor.Mitsubishi, profile.Vendor);

        if (File.Exists(ExpectedHostGxWorks3Exe))
        {
            Assert.True(profile.Installed);
            Assert.Equal("32-bit", profile.Bitness);
            Assert.Equal(ExpectedFileVersion, profile.Version);
        }
    }
}
