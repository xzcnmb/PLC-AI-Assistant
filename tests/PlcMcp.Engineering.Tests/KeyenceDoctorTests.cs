using System.Diagnostics;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Doctor;
using PlcMcp.Engineering.Workers.External;
using PlcMcp.Engineering.Workers.Keyence;

namespace PlcMcp.Engineering.Tests;

public class KeyenceDoctorTests
{
    private const string ExpectedHostIdeExe = @"C:\Program Files (x86)\KEYENCE\KVS12G\KVS12\KVS\Kvs.exe";
    private const string ExpectedHostCommServerExe = @"C:\Program Files (x86)\Common Files\KEYENCE\AC\KvCommunication\AcCommServer2.exe";

    #region Host Detection & Dynamic FileVersion

    [Fact]
    public void KeyenceInstallationDoctor_DiagnoseHost_DetectsIde_WhenPresent()
    {
        var report = KeyenceInstallationDoctor.DiagnoseHost();

        Assert.NotNull(report);
        Assert.NotNull(report.Summary);
        Assert.Contains("Keyence P0 Diagnosis", report.Summary);
        Assert.Contains("does not imply valid vendor license", report.Disclaimer);
        Assert.Contains("reverse engineering", report.Disclaimer);
        Assert.NotNull(report.Components);
        Assert.NotNull(report.ComRegistrations);

        if (File.Exists(ExpectedHostIdeExe))
        {
            Assert.True(report.Installed);
            Assert.NotNull(report.IdeExePath);
            Assert.True(File.Exists(report.IdeExePath));
            // KV STUDIO 12G is a 32-bit toolchain (verified on the reference host).
            Assert.Equal("32-bit", report.IdeBitness);

            var vi = FileVersionInfo.GetVersionInfo(report.IdeExePath);
            Assert.Equal(vi.FileVersion ?? vi.ProductVersion!, report.IdeVersion);
            Assert.False(string.IsNullOrWhiteSpace(report.IdeVersion));
            Assert.NotNull(report.LauncherExePath);
            Assert.True(File.Exists(report.LauncherExePath));
        }
        else
        {
            Assert.False(report.Installed);
        }
    }

    #endregion

    #region Components & Real PE Bitness

    [Fact]
    public void KeyenceInstallationDoctor_DetectsComponents_WithRealPeBitnessAndHash()
    {
        var report = KeyenceInstallationDoctor.DiagnoseHost();
        Assert.NotEmpty(report.Components);

        if (File.Exists(ExpectedHostIdeExe))
        {
            var kvs = report.Components.FirstOrDefault(c => c.Name == "Kvs.exe");
            Assert.NotNull(kvs);
            Assert.True(kvs.Exists);
            Assert.Equal("32-bit", kvs.Bitness);
            Assert.NotNull(kvs.Sha256);
            Assert.Equal(64, kvs.Sha256.Length);

            var kvComm = report.Components.FirstOrDefault(c => c.Name == "KVComm.dll");
            Assert.NotNull(kvComm);
            Assert.True(kvComm.Exists);
            Assert.Equal("32-bit", kvComm.Bitness);
            Assert.NotNull(kvComm.Sha256);

            var ldk = report.Components.FirstOrDefault(c => c.Name == "LDKCOM.dll");
            Assert.NotNull(ldk);
            Assert.True(ldk.Exists);
            Assert.Equal("32-bit", ldk.Bitness);
            Assert.NotNull(ldk.Sha256);

            // Bundled PLC emulators are observed statically only (P0 red line).
            var emu = report.Components.FirstOrDefault(c => c.Name == "KvEmu1000.dll");
            Assert.NotNull(emu);
            Assert.True(emu.Exists);
        }

        if (File.Exists(ExpectedHostCommServerExe))
        {
            var comm = report.Components.FirstOrDefault(c => c.Name == "AcCommServer2.exe");
            Assert.NotNull(comm);
            Assert.True(comm.Exists);
            Assert.Equal("32-bit", comm.Bitness);
            Assert.NotNull(comm.Sha256);
        }
    }

    #endregion

    #region Static COM Registration Discovery

    [Fact]
    public void KeyenceInstallationDoctor_DetectsStaticComRegistrations_WithoutActivation()
    {
        var report = KeyenceInstallationDoctor.DiagnoseHost();
        Assert.NotEmpty(report.ComRegistrations);
        Assert.Equal(KeyenceInstallationDoctor.ExpectedKeyenceProgIds.Length, report.ComRegistrations.Count);

        if (File.Exists(ExpectedHostCommServerExe))
        {
            var comm = report.ComRegistrations.FirstOrDefault(c => c.ProgId == "ACCommServer2.ACComm2");
            Assert.NotNull(comm);
            Assert.True(comm.Registered);
            Assert.Equal("{B4D8317A-685B-4605-8BA3-8B2DE8751958}", comm.Clsid, ignoreCase: true);
            Assert.Equal("LocalServer32", comm.ServerType);
            Assert.NotNull(comm.ServerPath);
            Assert.True(comm.ServerPathExists);
            Assert.Equal("Registry32", comm.RegistryView);

            var kvc = report.ComRegistrations.FirstOrDefault(c => c.ProgId == "KVCServer.KVControl");
            Assert.NotNull(kvc);
            Assert.True(kvc.Registered);
            Assert.Equal("{FC3CFD6D-D71D-429A-83A8-16D0BD06752B}", kvc.Clsid, ignoreCase: true);
            Assert.Equal("LocalServer32", kvc.ServerType);
            Assert.NotNull(kvc.ServerPath);

            var pathMgr = report.ComRegistrations.FirstOrDefault(c => c.ProgId == "PathManager2.ACCommPathMgr2");
            Assert.NotNull(pathMgr);
            Assert.True(pathMgr.Registered);
            Assert.Equal("{EF3F3EA9-4227-4733-B1FC-502346EA4C2B}", pathMgr.Clsid, ignoreCase: true);
            Assert.Equal("LocalServer32", pathMgr.ServerType);
            Assert.NotNull(pathMgr.ServerPath);
        }
    }

    #endregion

    #region Capabilities Strictly Unsupported in P0

    [Fact]
    public void KeyenceInstallationDoctor_Capabilities_AllOperationsStrictlyUnsupportedInP0()
    {
        var report = KeyenceInstallationDoctor.DiagnoseHost();
        var caps = report.Capabilities;

        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("InspectProject"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("CompileProject"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("DownloadProject"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("SetRunMode"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("ForceIo"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("WritePlc"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("OnlineMonitor"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("OnlineConnect"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("AclComm"));
        Assert.Equal(CapabilityStatus.Unsupported, caps.GetStatus("KvEmu"));

        foreach (var cap in caps.Items)
        {
            Assert.Equal(CapabilityStatus.Unsupported, cap.Status);
        }
    }

    #endregion

    #region Custom Explicit Paths & Isolation

    [Fact]
    public void KeyenceInstallationDoctor_SupportsExplicitCustomPaths()
    {
        if (File.Exists(ExpectedHostIdeExe))
        {
            var doctor = new KeyenceInstallationDoctor(ideExePath: ExpectedHostIdeExe);
            var report = doctor.Diagnose();

            Assert.True(report.Installed);
            Assert.Equal(ExpectedHostIdeExe, report.IdeExePath, ignoreCase: true);
            Assert.Equal("32-bit", report.IdeBitness);
        }

        if (File.Exists(ExpectedHostCommServerExe))
        {
            var doctor = new KeyenceInstallationDoctor(commServerExePath: ExpectedHostCommServerExe);
            var report = doctor.Diagnose();

            Assert.Equal(ExpectedHostCommServerExe, report.CommServerExePath, ignoreCase: true);
        }
    }

    [Fact]
    public void KeyenceInstallationDoctor_ExplicitCustomPath_NeverFallsBackToHostInstallation()
    {
        var fakeIde = @"Z:\NonExistent\KEYENCE\Kvs.exe";
        var fakeComm = @"Z:\NonExistent\KEYENCE\AcCommServer2.exe";

        var doctor = new KeyenceInstallationDoctor(fakeIde, fakeComm);
        var report = doctor.Diagnose();

        Assert.False(report.Installed);
        Assert.Equal(fakeIde, report.IdeExePath);
        Assert.Null(report.IdeVersion);
        Assert.Equal(fakeComm, report.CommServerExePath);
    }

    #endregion

    #region VendorDoctor Integration

    [Fact]
    public void VendorDoctor_CheckSoftware_Keyence_ReturnsExpectedReport()
    {
        var doctor = new VendorDoctor();
        var report = doctor.CheckSoftware(VendorSoftwareKind.Keyence);

        Assert.NotNull(report);
        Assert.Equal(VendorSoftwareKind.Keyence, report.Kind);
        Assert.Equal("KEYENCE KV STUDIO 12G", report.Name);

        if (File.Exists(ExpectedHostIdeExe))
        {
            Assert.True(report.Installed);
            Assert.Equal("32-bit", report.Bitness);
            Assert.Equal(ExpectedHostIdeExe, report.ExecutablePath, ignoreCase: true);
            Assert.NotNull(report.KeyenceReport);
            Assert.True(report.KeyenceReport.Installed);
            Assert.NotEmpty(report.KeyenceReport.Components);
            Assert.NotEmpty(report.KeyenceReport.ComRegistrations);
        }
    }

    [Fact]
    public void VendorDoctor_DiagnoseKeyence_StaticAndInstance_ProvideDirectAccess()
    {
        var instanceReport = new VendorDoctor().DiagnoseKeyence();
        var staticReport = VendorDoctor.DiagnoseKeyence();

        Assert.NotNull(instanceReport);
        Assert.NotNull(staticReport);

        if (File.Exists(ExpectedHostIdeExe))
        {
            Assert.True(instanceReport.Installed);
            Assert.True(staticReport.Installed);
            Assert.Equal(instanceReport.IdeVersion, staticReport.IdeVersion);
        }
    }

    #endregion


    [Fact]
    public void KeyenceInstallationDoctor_ReadsKvsMonSettings_FromInjectedFile()
    {
        var ini = Path.Combine(Path.GetTempPath(), "kvsmon-test-" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            System.IO.File.WriteAllText(ini,
                "[MonConfig]\nIFMode=4\nMachineNo=-1\nIpPortNo=8500\nIpAddress=192.168.250.11\n[AclSetup]\nAddrNetwork=254\nAddrNode=254\n[SimulatorSettings]\nSystemExtPortNo=8500\nHostLinkPortNo=8501\nScanExecutionInterval=100\n",
                System.Text.Encoding.Unicode);
            var doctor = new KeyenceInstallationDoctor(kvsMonIniPath: ini);
            var report = doctor.Diagnose();

            Assert.NotNull(report.KvsMonSettings);
            Assert.True(report.KvsMonSettings.IniExists);
            Assert.Equal(ini, report.KvsMonSettings.IniPath, ignoreCase: true);
            Assert.Equal("4", report.KvsMonSettings.IfMode);
            Assert.Equal("192.168.250.11", report.KvsMonSettings.LastIpAddress);
            Assert.Equal("8500", report.KvsMonSettings.LastIpPort);
            Assert.Equal("254", report.KvsMonSettings.AddrNetwork);
            Assert.Equal("254", report.KvsMonSettings.AddrNode);
            Assert.Equal("8500", report.KvsMonSettings.SystemExtPortNo);
            Assert.Equal("8501", report.KvsMonSettings.HostLinkPortNo);
            Assert.Equal("100", report.KvsMonSettings.ScanExecutionInterval);
            Assert.Equal("-1", report.KvsMonSettings.MachineNo);
        }
        finally
        {
            if (File.Exists(ini)) File.Delete(ini);
        }
    }

    [Fact]
    public void KeyenceInstallationDoctor_MissingKvsMonIni_ReportsNotExists()
    {
        var doctor = new KeyenceInstallationDoctor(kvsMonIniPath: @"Z:\NonExistent\KvsMon.ini");
        var report = doctor.Diagnose();

        Assert.NotNull(report.KvsMonSettings);
        Assert.False(report.KvsMonSettings.IniExists);
        Assert.Equal(@"Z:\NonExistent\KvsMon.ini", report.KvsMonSettings.IniPath);
        Assert.Null(report.KvsMonSettings.SystemExtPortNo);
        Assert.Null(report.KvsMonSettings.IfMode);
    }

    [Fact]
    public void KeyenceInstallationDoctor_ReadsLiveHostKvsMonIni_WhenPresent()
    {
        var settings = new KeyenceInstallationDoctor().Diagnose().KvsMonSettings;
        var defaultIni = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"KEYENCE\KVS12G\KVS12\KVS\KvsMon.ini");
        if (File.Exists(defaultIni))
        {
            Assert.NotNull(settings);
            Assert.True(settings.IniExists);
            Assert.False(string.IsNullOrWhiteSpace(settings.SystemExtPortNo));
            Assert.False(string.IsNullOrWhiteSpace(settings.HostLinkPortNo));
            Assert.False(string.IsNullOrWhiteSpace(settings.LastIpAddress));
        }
    }
}