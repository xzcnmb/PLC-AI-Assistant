using System.Security.Cryptography;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Workers.External;
using PlcMcp.Engineering.Workers.GxWorks3;

namespace PlcMcp.Engineering.Tests;

public class GxWorks3WorkerTests : IDisposable
{
    private readonly string _tempWorkspace;
    private readonly string _mockInstallRoot;
    private readonly string _mockManagedDir;
    private readonly string _mockGxw3ExePath;
    private readonly string _mockGxw3ExeSha256;
    private const string ExactVersion = "1.128.04519";

    public GxWorks3WorkerTests()
    {
        _tempWorkspace = Path.Combine(Path.GetTempPath(), "PlcMcp_GxWorks3Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempWorkspace);

        _mockInstallRoot = Path.Combine(_tempWorkspace, "gwork2");
        _mockManagedDir = Path.Combine(_mockInstallRoot, "GPPW3");
        Directory.CreateDirectory(_mockManagedDir);

        _mockGxw3ExePath = Path.Combine(_mockManagedDir, "GXW3.exe");
        File.WriteAllBytes(_mockGxw3ExePath, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x11, 0x22 });
        _mockGxw3ExeSha256 = ComputeSha256(_mockGxw3ExePath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempWorkspace))
            {
                Directory.Delete(_tempWorkspace, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private GxWorks3WorkerConfig CreateValidConfig() => new()
    {
        Gxw3ExePath = _mockGxw3ExePath,
        InstallationRoot = _mockInstallRoot,
        ManagedDirectory = _mockManagedDir,
        Gxw3Version = ExactVersion,
        Gxw3ExeSha256 = _mockGxw3ExeSha256,
        TimeoutSeconds = 45
    };

    private sealed class FakeGxDetector : IVendorProfileDetector
    {
        public PlcVendor Vendor => PlcVendor.Mitsubishi;
        public string VendorName => "Mitsubishi";
        public string ToolchainName => "Mitsubishi GX Works3";
        private readonly string _exePath;
        private readonly string _version;

        public FakeGxDetector(string exePath, string version = ExactVersion)
        {
            _exePath = exePath;
            _version = version;
        }

        public ExternalVendorProfile DetectProfile() =>
            new(
                Vendor: Vendor,
                VendorName: VendorName,
                ToolchainName: ToolchainName,
                Installed: true,
                ExecutablePath: _exePath,
                Version: _version,
                Bitness: "32-bit",
                Capabilities: GxWorksProfileDetector.BuildGxWorksCapabilities(isInstalled: true),
                Details: "Fake installed GX Works3",
                RegistryVersion: _version);
    }

    #region Configuration, Constructor & IsAvailable

    [Fact]
    public void GxWorks3Worker_IsAvailable_IsAlwaysFalseInP0()
    {
        var config = CreateValidConfig();
        var worker = new GxWorks3Worker(config, new FakeGxDetector(_mockGxw3ExePath));

        // IsAvailable must be strictly false in P0 to indicate no active engineering execution
        Assert.False(worker.IsAvailable);
        Assert.Equal("GxWorks3-Static-Worker", worker.Name);
        Assert.Equal(PlcVendor.Mitsubishi, worker.Vendor);
    }

    [Fact]
    public void GxWorks3WorkerConfig_Defaults_HasSensibleDefaultsWithoutEmptyIdentityFields()
    {
        var config = new GxWorks3WorkerConfig();

        Assert.Equal(PlcVendor.Mitsubishi, config.Vendor);
        Assert.Equal(string.Empty, config.Gxw3ExePath);
        Assert.Equal(string.Empty, config.InstallationRoot);
        Assert.Equal(string.Empty, config.ManagedDirectory);
        Assert.Null(config.ProjectRoot);
        Assert.Null(config.Gxw3Version);
        Assert.Null(config.Gxw3ExeSha256);
        Assert.Equal(60, config.TimeoutSeconds);
        Assert.Equal(10 * 1024 * 1024, config.MaxOutputBytes);
    }

    [Fact]
    public void Constructor_DoesNotThrowOnNonExistentOrRelativePaths()
    {
        var config = new GxWorks3WorkerConfig
        {
            Gxw3ExePath = "relative\\path\\to\\GXW3.exe",
            InstallationRoot = "relative\\install",
            ManagedDirectory = "relative\\managed"
        };

        // Construction must succeed without throwing; errors are surfaced gracefully in CheckDoctor
        var worker = new GxWorks3Worker(config);
        Assert.NotNull(worker);

        var report = worker.CheckDoctor();
        Assert.False(report.Healthy);
        Assert.Contains(report.Checks!, c => c.Name == "gxw3_executable" && !c.Passed);
        Assert.Contains(report.Checks!, c => c.Name == "installation_root" && !c.Passed);
        Assert.Contains(report.Checks!, c => c.Name == "managed_directory" && !c.Passed);
    }

    #endregion

    #region Static Doctor Diagnosis

    [Fact]
    public void CheckDoctor_WhenConfigValid_ReturnsHealthyWithExplicitServiceBusDisclaimer()
    {
        var config = CreateValidConfig();
        var worker = new GxWorks3Worker(config, new FakeGxDetector(_mockGxw3ExePath));

        var report = worker.CheckDoctor();

        Assert.True(report.Healthy);
        Assert.Equal("32-bit", report.Bitness);
        Assert.Contains("static installation checks passed", report.Details);
        Assert.Contains("Static diagnosis healthy != engineering API is usable (ServiceBus unverified)", report.Details);
        Assert.DoesNotContain("handshake-ready", report.Details);

        // servicebus_ready should not be present as an unpassed check blocking installation health
        Assert.DoesNotContain(report.Checks!, c => c.Name == "servicebus_ready");
    }

    [Fact]
    public void CheckDoctor_ProjectRootIsOptional_DoctorIsHealthyWhenOmitted()
    {
        var config = CreateValidConfig();
        config.ProjectRoot = null;

        var worker = new GxWorks3Worker(config, new FakeGxDetector(_mockGxw3ExePath));
        var report = worker.CheckDoctor();

        Assert.True(report.Healthy);
    }

    [Fact]
    public void CheckDoctor_WhenProjectRootProvidedAndInvalid_FailsClosed()
    {
        var config = CreateValidConfig();
        config.ProjectRoot = @"C:\NonExistent_Project_Root_Dir_xyz";

        var worker = new GxWorks3Worker(config, new FakeGxDetector(_mockGxw3ExePath));
        var report = worker.CheckDoctor();

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks!, c => c.Name == "project_root" && !c.Passed);
    }

    [Fact]
    public void CheckDoctor_WhenExactFourPartVersionMatches_Passes()
    {
        var config = CreateValidConfig();
        config.Gxw3Version = ExactVersion;

        var worker = new GxWorks3Worker(config, new FakeGxDetector(_mockGxw3ExePath, ExactVersion));
        var report = worker.CheckDoctor();

        Assert.True(report.Healthy);
        Assert.Contains(report.Checks!, c => c.Name == "version_pin" && c.Passed);
    }

    [Fact]
    public void CheckDoctor_WhenVersionIsPrefixOrPartial_StrictlyMismatches()
    {
        var config = CreateValidConfig();
        config.Gxw3Version = "1.128.0"; // Only 3 parts, does not match 4-part "1.128.04519"

        var worker = new GxWorks3Worker(config, new FakeGxDetector(_mockGxw3ExePath, ExactVersion));
        var report = worker.CheckDoctor();

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks!, c => c.Name == "version_pin" && !c.Passed);
    }

    [Fact]
    public void CheckDoctor_WhenExeSha256Mismatches_FailsClosed()
    {
        var config = CreateValidConfig();
        config.Gxw3ExeSha256 = new string('0', 64);

        var worker = new GxWorks3Worker(config, new FakeGxDetector(_mockGxw3ExePath));
        var report = worker.CheckDoctor();

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks!, c => c.Name == "executable_hash" && !c.Passed);
    }

    [Fact]
    public void CheckDoctor_WhenManagedDirectoryNotUnderInstallationRoot_FailsClosed()
    {
        string separateDir = Path.Combine(_tempWorkspace, "other_folder");
        Directory.CreateDirectory(separateDir);

        var config = CreateValidConfig();
        config.ManagedDirectory = separateDir;

        var worker = new GxWorks3Worker(config, new FakeGxDetector(_mockGxw3ExePath));
        var report = worker.CheckDoctor();

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks!, c => c.Name == "managed_directory" && !c.Passed);
    }

    #endregion

    #region Strict Options Allowlist & Safety Rejection

    [Theory]
    [InlineData("foobar", "123")]
    [InlineData("clear", "true")]
    [InlineData("format", "xml")]
    [InlineData("download", "force")]
    [InlineData("dry_run", "false")]
    public async Task ExecuteAsync_StrictAllowlist_RejectsAnyOptionsDictionary(string key, string value)
    {
        var worker = new GxWorks3Worker(CreateValidConfig(), new FakeGxDetector(_mockGxw3ExePath));

        var job = new EngineeringJobRequest(
            JobId: "job-any-option",
            JobType: EngineeringJobType.InspectProject,
            Vendor: PlcVendor.Mitsubishi,
            ProjectPath: @"D:\some\project.gx3",
            Options: new Dictionary<string, string> { [key] = value });

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("no options are supported under GxWorks3 P0 safety policy", result.Message);
        Assert.Contains(key, result.Details);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsCompileProjectAndUnknownJobs()
    {
        var worker = new GxWorks3Worker(CreateValidConfig(), new FakeGxDetector(_mockGxw3ExePath));

        var job = new EngineeringJobRequest(
            JobId: "job-compile",
            JobType: EngineeringJobType.CompileProject,
            Vendor: PlcVendor.Mitsubishi,
            ProjectPath: @"D:\some\project.gx3");

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("does not support job type 'CompileProject'", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsVendorMismatch()
    {
        var worker = new GxWorks3Worker(CreateValidConfig(), new FakeGxDetector(_mockGxw3ExePath));

        var job = new EngineeringJobRequest(
            JobId: "job-mismatch",
            JobType: EngineeringJobType.InspectProject,
            Vendor: PlcVendor.Siemens,
            ProjectPath: @"D:\some\project.gx3");

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("Expected Mitsubishi", result.Message);
    }

    #endregion

    #region Honest Unsupported Reporting Without Reading Customer Files

    [Theory]
    [InlineData(EngineeringJobType.InspectProject)]
    [InlineData(EngineeringJobType.ExportPou)]
    [InlineData(EngineeringJobType.ValidatePou)]
    [InlineData(EngineeringJobType.DiffProjects)]
    public async Task ExecuteAsync_OfflineCandidates_HonestlyReturnsUnsupportedWithoutReadingCustomerFiles(EngineeringJobType jobType)
    {
        var worker = new GxWorks3Worker(CreateValidConfig(), new FakeGxDetector(_mockGxw3ExePath));

        // Note: Project file does NOT even need to exist, proving the worker never touches or reads customer disk files
        string nonExistentPath = @"D:\does_not_exist\customer_project.gx3";

        var job = new EngineeringJobRequest(
            JobId: $"job-{jobType}",
            JobType: jobType,
            Vendor: PlcVendor.Mitsubishi,
            ProjectPath: nonExistentPath);

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("ServiceBus integration is unverified", result.Message);
        Assert.Contains("Phase 0/1 Safety Gate active", result.Details);
        Assert.Contains("no project files read or opened", result.Details);
    }

    #endregion
}
