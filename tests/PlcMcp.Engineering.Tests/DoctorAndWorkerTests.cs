using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Doctor;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Workers.Siemens;
using PlcMcp.Engineering.Workspace;

namespace PlcMcp.Engineering.Tests;

public class DoctorAndWorkerTests : IDisposable
{
    private readonly string _tempTestDir;

    public DoctorAndWorkerTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), "PlcMcp_TestDoctor_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempTestDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempTestDir))
            {
                foreach (var file in Directory.GetFiles(_tempTestDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_tempTestDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void VendorDoctor_RunFullDiagnosis_ReturnsReportWithAllExpectedVendors()
    {
        var doctor = new VendorDoctor();
        var report = doctor.RunFullDiagnosis();

        Assert.NotNull(report);
        Assert.Equal(8, report.Items.Count);

        var kinds = report.Items.Select(i => i.Kind).ToHashSet();
        Assert.Contains(VendorSoftwareKind.MicroWinSmart, kinds);
        Assert.Contains(VendorSoftwareKind.TiaPortalOpenness, kinds);
        Assert.Contains(VendorSoftwareKind.GxWorks3, kinds);
        Assert.Contains(VendorSoftwareKind.SysmacStudio, kinds);
        Assert.Contains(VendorSoftwareKind.Codesys, kinds);
        Assert.Contains(VendorSoftwareKind.InoProShop, kinds);
        Assert.Contains(VendorSoftwareKind.PlcSim, kinds);
        Assert.Contains(VendorSoftwareKind.GxSimulator, kinds);

        // Verify each report item contains honest disclaimer
        foreach (var item in report.Items)
        {
            Assert.Contains("does not imply valid vendor license", item.Disclaimer);
        }

        // On this specific machine, MicroWIN SMART is physically installed
        var smartReport = report.Items.First(i => i.Kind == VendorSoftwareKind.MicroWinSmart);
        Assert.True(smartReport.Installed);
        Assert.NotNull(smartReport.ExecutablePath);
        Assert.True(File.Exists(smartReport.ExecutablePath));
    }

    [Fact]
    public async Task UnsupportedEngineeringWorker_ExecuteAsync_ReportsUnsupportedHonoringCapabilityStatus()
    {
        var worker = new UnsupportedEngineeringWorker(PlcVendor.Mitsubishi, "GX-Works3-Worker");

        Assert.False(worker.IsAvailable);
        Assert.Equal("GX-Works3-Worker", worker.Name);

        var job = new EngineeringJobRequest(
            JobId: "job-101",
            JobType: EngineeringJobType.CompileProject,
            Vendor: PlcVendor.Mitsubishi,
            ProjectPath: @"C:\dummy\project.gx3");

        var result = await worker.ExecuteAsync(job);

        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("No vendor engineering backend worker is installed", result.Message);
    }

    [Fact]
    public async Task MicroWinSmartOfflineWorker_InspectProject_UsesWorkingCopyAndKeepsOriginalUntouched()
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new VendorDoctor();
        var worker = new MicroWinSmartOfflineWorker(workspaceManager, doctor);

        // Create a dummy .smart file
        var sampleFile = Path.Combine(_tempTestDir, "test_demo.smart");
        byte[] originalContent = [0x53, 0x4D, 0x41, 0x52, 0x54, 0x00, 0x01];
        File.WriteAllBytes(sampleFile, originalContent);
        var originalSha = workspaceManager.ComputeSha256(sampleFile);

        var job = new EngineeringJobRequest(
            JobId: "job-smart-01",
            JobType: EngineeringJobType.InspectProject,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile);

        var result = await worker.ExecuteAsync(job);

        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.Equal(CapabilityStatus.Supported, result.Status);
        Assert.NotNull(result.ParsedProject);
        Assert.Equal("Siemens-S7-200-SMART-V2", result.ParsedProject.Format);

        // Verify original project file is untouched
        var postSha = workspaceManager.ComputeSha256(sampleFile);
        Assert.Equal(originalSha, postSha);
    }
}
