using System.Diagnostics;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Doctor;
using PlcMcp.Engineering.Workers.External;
using PlcMcp.Engineering.Workers.Omron;

namespace PlcMcp.Engineering.Tests;

public class OmronDoctorTests
{
    private const string ExpectedHostSysmacExe = @"C:\Program Files\OMRON\Sysmac Studio\SysmacStudio.exe";
    private const string ExpectedHostCxServerExe = @"C:\Program Files (x86)\OMRON\CX-Server\cdmsvr20.exe";

    #region Host Detection & Dynamic FileVersion

    [Fact]
    public void OmronInstallationDoctor_DiagnoseHost_DetectsSysmacStudio_WhenPresent()
    {
        var report = OmronInstallationDoctor.DiagnoseHost();

        Assert.NotNull(report);
        Assert.NotNull(report.Summary);
        Assert.Contains("Omron P0 Diagnosis", report.Summary);
        Assert.Contains("does not imply valid vendor license", report.Disclaimer);

        if (File.Exists(ExpectedHostSysmacExe))
        {
            Assert.True(report.SysmacInstalled);
            Assert.NotNull(report.SysmacExePath);
            Assert.True(File.Exists(report.SysmacExePath));
            Assert.Equal("64-bit", report.SysmacBitness);

            // Dynamic FileVersion verification from file itself
            var vi = FileVersionInfo.GetVersionInfo(report.SysmacExePath);
            string expectedVer = vi.FileVersion ?? vi.ProductVersion!;
            Assert.Equal(expectedVer, report.SysmacVersion);
            Assert.Contains("1.60", report.SysmacVersion);

            Assert.NotNull(report.SysmacRegistryVersion);
            Assert.Contains("1.60", report.SysmacRegistryVersion);
        }
    }

    [Fact]
    public void OmronInstallationDoctor_DiagnoseHost_DetectsCxServer_WhenPresent()
    {
        var report = OmronInstallationDoctor.DiagnoseHost();

        Assert.NotNull(report);

        if (File.Exists(ExpectedHostCxServerExe))
        {
            Assert.True(report.CxServerInstalled);
            Assert.NotNull(report.CxServerExePath);
            Assert.True(File.Exists(report.CxServerExePath));
            Assert.Equal("32-bit", report.CxServerBitness);

            // Dynamic FileVersion verification from file itself (e.g. 5,1,1,4)
            var vi = FileVersionInfo.GetVersionInfo(report.CxServerExePath);
            string expectedVer = vi.FileVersion ?? vi.ProductVersion!;
            Assert.Equal(expectedVer, report.CxServerVersion);

            Assert.NotNull(report.CxServerRegistryVersion);
            Assert.False(string.IsNullOrWhiteSpace(report.CxServerRegistryVersion));
        }
    }

    #endregion

    #region Components & Real PE Bitness

    [Fact]
    public void OmronInstallationDoctor_DetectsComponents_WithRealPeBitnessAndHash()
    {
        var report = OmronInstallationDoctor.DiagnoseHost();
        Assert.NotEmpty(report.Components);

        if (File.Exists(ExpectedHostSysmacExe))
        {
            // 1. SysmacStudio.exe
            var sysmac = report.Components.FirstOrDefault(c => c.Name == "SysmacStudio.exe");
            Assert.NotNull(sysmac);
            Assert.True(sysmac.Exists);
            Assert.Equal("64-bit", sysmac.Bitness);
            Assert.NotNull(sysmac.Sha256);
            Assert.Equal(64, sysmac.Sha256.Length);

            // 2. Ace.ScriptHost.exe (64-bit)
            var ace = report.Components.FirstOrDefault(c => c.Name == "Ace.ScriptHost.exe");
            Assert.NotNull(ace);
            Assert.True(ace.Exists);
            Assert.Equal("64-bit", ace.Bitness);
            Assert.NotNull(ace.Sha256);

            // 3. nexcc.exe (in builder2, PE machine 0x014C => 32-bit without x86 in directory path)
            var nexcc = report.Components.FirstOrDefault(c => c.Name == "nexcc.exe");
            Assert.NotNull(nexcc);
            Assert.True(nexcc.Exists);
            Assert.Equal("32-bit", nexcc.Bitness);
            Assert.NotNull(nexcc.Sha256);

            // 4. SysmacDiff.exe (64-bit)
            var diff = report.Components.FirstOrDefault(c => c.Name == "SysmacDiff.exe");
            Assert.NotNull(diff);
            Assert.True(diff.Exists);
            Assert.Equal("64-bit", diff.Bitness);
            Assert.NotNull(diff.Sha256);

            // 5. RuntimeSimulator.exe (32-bit)
            var sim = report.Components.FirstOrDefault(c => c.Name == "RuntimeSimulator.exe");
            Assert.NotNull(sim);
            Assert.True(sim.Exists);
            Assert.Equal("32-bit", sim.Bitness);
            Assert.NotNull(sim.Sha256);
        }

        if (File.Exists(ExpectedHostCxServerExe))
        {
            // cdmsvr20.exe (32-bit)
            var cdm = report.Components.FirstOrDefault(c => c.Name == "cdmsvr20.exe");
            Assert.NotNull(cdm);
            Assert.True(cdm.Exists);
            Assert.Equal("32-bit", cdm.Bitness);
            Assert.NotNull(cdm.Sha256);
        }
    }

    [Fact]
    public void OmronInstallationDoctor_PeBitness_ReadsHeadersDirectlyWithoutPathGuessing()
    {
        if (File.Exists(ExpectedHostSysmacExe))
        {
            // nexcc.exe is under 'C:\Program Files\OMRON\Sysmac Studio\builder2\nexcc.exe'
            // Path does NOT contain "x86", but real PE header is 32-bit!
            var nexccPath = Path.Combine(Path.GetDirectoryName(ExpectedHostSysmacExe)!, "builder2", "nexcc.exe");
            if (File.Exists(nexccPath))
            {
                Assert.DoesNotContain("x86", nexccPath, StringComparison.OrdinalIgnoreCase);
                string bitness = BaseVendorProfileDetector.DetectPeBitness(nexccPath);
                Assert.Equal("32-bit", bitness);
            }
        }
    }

    #endregion

    #region Static COM Registration Discovery

    [Fact]
    public void OmronInstallationDoctor_DetectsStaticComRegistrations_WithoutActivation()
    {
        var report = OmronInstallationDoctor.DiagnoseHost();
        Assert.NotEmpty(report.ComRegistrations);

        // Verify all 13 ProgIDs are represented
        Assert.Equal(OmronInstallationDoctor.ExpectedOmronProgIds.Length, report.ComRegistrations.Count);

        if (File.Exists(ExpectedHostCxServerExe))
        {
            // CXServer.Communications
            var comms = report.ComRegistrations.FirstOrDefault(c => c.ProgId == "CXServer.Communications");
            Assert.NotNull(comms);
            Assert.True(comms.Registered);
            Assert.NotNull(comms.Clsid);
            Assert.Equal("{B76F5410-3E8A-11D3-85A9-005004606465}", comms.Clsid, ignoreCase: true);
            Assert.Equal("LocalServer32", comms.ServerType);
            Assert.NotNull(comms.ServerPath);
            Assert.True(comms.ServerPathExists);
            Assert.Equal("Registry32", comms.RegistryView);

            // Omron.DeviceManager
            var devMgr = report.ComRegistrations.FirstOrDefault(c => c.ProgId == "Omron.DeviceManager");
            Assert.NotNull(devMgr);
            Assert.True(devMgr.Registered);
            Assert.Equal("{BB8B9E1A-363F-4814-AE99-42B8AD610ADA}", devMgr.Clsid, ignoreCase: true);
            Assert.Equal("InprocServer32", devMgr.ServerType);
            Assert.NotNull(devMgr.ServerPath);
            Assert.True(devMgr.ServerPathExists);
            Assert.Equal("Registry32", devMgr.RegistryView);
        }
    }

    #endregion

    #region Capabilities Strictly Unsupported in P0

    [Fact]
    public void OmronInstallationDoctor_Capabilities_AllOperationsStrictlyUnsupportedInP0()
    {
        var report = OmronInstallationDoctor.DiagnoseHost();
        var caps = report.Capabilities;

        // Offline capabilities
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("InspectProject"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("ExportPou"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("ValidatePou"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("CompileProject"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("DiffProjects"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("AceScript"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("NexccCompile"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("ExportXml"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("ExportAml"));

        // Dangerous and online capabilities
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("DownloadProject"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("SetRunMode"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("ForceIo"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("WritePlc"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("OnlineMonitor"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("OnlineConnect"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("HmiPublish"));

        // Every capability descriptor must have Unsupported status
        foreach (var cap in caps.Items)
        {
            Assert.Equal(CapabilityStatus.Unsupported, cap.Status);
        }
    }

    #endregion

    #region Custom Explicit Paths & Isolation

    [Fact]
    public void OmronInstallationDoctor_SupportsExplicitCustomPaths()
    {
        if (File.Exists(ExpectedHostSysmacExe))
        {
            var doctor = new OmronInstallationDoctor(sysmacExePath: ExpectedHostSysmacExe);
            var report = doctor.Diagnose();

            Assert.True(report.SysmacInstalled);
            Assert.Equal(ExpectedHostSysmacExe, report.SysmacExePath, ignoreCase: true);
            Assert.Equal("64-bit", report.SysmacBitness);
        }

        if (File.Exists(ExpectedHostCxServerExe))
        {
            var doctor = new OmronInstallationDoctor(cxServerExePath: ExpectedHostCxServerExe);
            var report = doctor.Diagnose();

            Assert.True(report.CxServerInstalled);
            Assert.Equal(ExpectedHostCxServerExe, report.CxServerExePath, ignoreCase: true);
            Assert.Equal("32-bit", report.CxServerBitness);
        }
    }

    [Fact]
    public void OmronInstallationDoctor_ExplicitCustomPath_NeverFallsBackToHostInstallation()
    {
        var fakeSysmac = @"Z:\NonExistent\OMRON\SysmacStudio.exe";
        var fakeCx = @"Z:\NonExistent\OMRON\cdmsvr20.exe";

        var doctor = new OmronInstallationDoctor(fakeSysmac, fakeCx);
        var report = doctor.Diagnose();

        Assert.False(report.SysmacInstalled);
        Assert.Equal(fakeSysmac, report.SysmacExePath);
        Assert.Null(report.SysmacVersion);

        Assert.False(report.CxServerInstalled);
        Assert.Equal(fakeCx, report.CxServerExePath);
        Assert.Null(report.CxServerVersion);
    }

    #endregion

    #region SysmacProfileDetector & VendorDoctor Integration

    [Fact]
    public void SysmacProfileDetector_DetectProfile_ReturnsComprehensiveProfile()
    {
        var detector = new SysmacProfileDetector();
        var profile = detector.DetectProfile();

        Assert.NotNull(profile);
        Assert.Equal(PlcVendor.Omron, profile.Vendor);
        Assert.Equal("Omron", profile.VendorName);
        Assert.Equal("Omron Sysmac Studio", profile.ToolchainName);

        if (File.Exists(ExpectedHostSysmacExe))
        {
            Assert.True(profile.Installed);
            Assert.Equal(ExpectedHostSysmacExe, profile.ExecutablePath, ignoreCase: true);
            Assert.Equal("64-bit", profile.Bitness);
            Assert.NotNull(profile.OmronComponents);
            Assert.NotEmpty(profile.OmronComponents);
            Assert.NotNull(profile.OmronComRegistrations);
            Assert.NotEmpty(profile.OmronComRegistrations);
        }
    }

    [Fact]
    public void VendorDoctor_CheckSoftware_SysmacStudio_IncludesOmronDoctorReport()
    {
        var doctor = new VendorDoctor();
        var report = doctor.CheckSoftware(VendorSoftwareKind.SysmacStudio);

        Assert.NotNull(report);
        Assert.Equal(VendorSoftwareKind.SysmacStudio, report.Kind);
        Assert.Equal("Omron Sysmac Studio", report.Name);

        if (File.Exists(ExpectedHostSysmacExe))
        {
            Assert.True(report.Installed);
            Assert.Equal("64-bit", report.Bitness);
            Assert.NotNull(report.OmronReport);
            Assert.True(report.OmronReport.SysmacInstalled);
            Assert.NotEmpty(report.OmronReport.Components);
            Assert.NotEmpty(report.OmronReport.ComRegistrations);
        }
    }

    [Fact]
    public void VendorDoctor_CheckSoftware_CxServer_ReturnsExpectedProfile()
    {
        var doctor = new VendorDoctor();
        var report = doctor.CheckSoftware(VendorSoftwareKind.CxServer);

        Assert.NotNull(report);
        Assert.Equal(VendorSoftwareKind.CxServer, report.Kind);
        Assert.Equal("Omron CX-Server", report.Name);

        if (File.Exists(ExpectedHostCxServerExe))
        {
            Assert.True(report.Installed);
            Assert.Equal("32-bit", report.Bitness);
            Assert.Equal(ExpectedHostCxServerExe, report.ExecutablePath, ignoreCase: true);
        }
    }

    [Fact]
    public void VendorDoctor_DiagnoseOmron_StaticAndInstance_ProvideDirectAccess()
    {
        var instanceReport = new VendorDoctor().DiagnoseOmron();
        var staticReport = VendorDoctor.DiagnoseOmron();

        Assert.NotNull(instanceReport);
        Assert.NotNull(staticReport);

        if (File.Exists(ExpectedHostSysmacExe))
        {
            Assert.True(instanceReport.SysmacInstalled);
            Assert.True(staticReport.SysmacInstalled);
            Assert.Equal(instanceReport.SysmacVersion, staticReport.SysmacVersion);
        }
    }

    #endregion
}
