using System.Text.Json;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Doctor;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Models;
using PlcMcp.Engineering.Workers.Siemens;
using PlcMcp.Engineering.Workspace;

namespace PlcMcp.Engineering.Tests;

public class SiemensSmartEngineeringWorkerTests : IDisposable
{
    private readonly string _tempTestDir;

    public SiemensSmartEngineeringWorkerTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), "PlcMcp_TestSmartWorker_" + Guid.NewGuid().ToString("N"));
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

    private sealed class MockSmartBridge : ISiemensSmartBridge
    {
        public bool IsAvailable { get; set; } = true;
        public string? ResolvedPythonPath => @"C:\Mock\python.exe";
        public string? ResolvedMicroWinPath => @"C:\Mock\MWSmart.exe";

        public Func<string, IReadOnlyDictionary<string, string>, string>? OnExecute { get; set; }

        public Task<string> ExecuteCommandAsync(
            string commandName,
            IReadOnlyDictionary<string, string> args,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            if (OnExecute != null)
            {
                return Task.FromResult(OnExecute(commandName, args));
            }

            return Task.FromResult("{\"success\":true}");
        }
    }

    private sealed class MockDoctor : IVendorDoctor
    {
        public bool Installed { get; set; } = true;

        public SystemDoctorReport RunFullDiagnosis() => throw new NotImplementedException();

        public VendorSoftwareReport CheckSoftware(VendorSoftwareKind kind)
        {
            return new VendorSoftwareReport(
                Kind: kind,
                Name: "STEP 7-MicroWIN SMART",
                Installed: Installed,
                ExecutablePath: @"C:\Program Files (x86)\Siemens\STEP 7-MicroWIN SMART\MWSmart.exe",
                Version: "V2.8.2.0",
                Details: "Mocked doctor report");
        }
    }

    [Fact]
    public async Task Worker_RejectsProhibitedOperation_DownloadOrRun()
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge);

        var sampleFile = Path.Combine(_tempTestDir, "test.smart");
        File.WriteAllBytes(sampleFile, [0x01, 0x02]);

        var prohibitedJob = new EngineeringJobRequest(
            JobId: "job-forbidden-01",
            JobType: EngineeringJobType.CompileProject,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile,
            Options: new Dictionary<string, string>
            {
                ["download_to_plc"] = "true"
            });

        var result = await worker.ExecuteAsync(prohibitedJob);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("Prohibited operation", result.Details);
    }

    [Fact]
    public async Task Worker_PathTraversal_Rejected()
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        var config = new SiemensSmartBridgeConfig
        {
            AllowedWorkspaceRoots = new[] { _tempTestDir }
        };
        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, config);

        var outsideFile = Path.Combine(Path.GetTempPath(), "outside.smart");
        File.WriteAllBytes(outsideFile, [0x01, 0x02]);

        try
        {
            var job = new EngineeringJobRequest(
                JobId: "job-path-01",
                JobType: EngineeringJobType.InspectProject,
                Vendor: PlcVendor.Siemens,
                ProjectPath: outsideFile);

            var result = await worker.ExecuteAsync(job);
            Assert.False(result.Success);
            Assert.Contains("Path validation failed", result.Message);
        }
        finally
        {
            if (File.Exists(outsideFile)) File.Delete(outsideFile);
        }
    }

    [Fact]
    public async Task Worker_InspectProject_PreservesSourceHashAndParsesCorrectly()
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        var config = new SiemensSmartBridgeConfig
        {
            AllowedWorkspaceRoots = new[] { _tempTestDir }
        };

        bridge.OnExecute = (cmd, args) =>
        {
            Assert.Equal("overview", cmd);
            return JsonSerializer.Serialize(new
            {
                success = true,
                format = "V2",
                project_name = "MockProject",
                symbol_count = 42,
                pou_names = new[] { "MAIN", "SBR0_Init", "INT0_Timer" },
                function_block_kinds = 5,
                function_block_total = 20,
                limitations = "Offline parse limits apply."
            });
        };

        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, config);

        var sampleFile = Path.Combine(_tempTestDir, "demo.smart");
        byte[] originalBytes = [0x53, 0x4D, 0x41, 0x52, 0x54, 0x32, 0x30, 0x30];
        File.WriteAllBytes(sampleFile, originalBytes);
        string expectedSha = workspaceManager.ComputeSha256(sampleFile);

        var job = new EngineeringJobRequest(
            JobId: "job-inspect-01",
            JobType: EngineeringJobType.InspectProject,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile);

        var result = await worker.ExecuteAsync(job);

        Assert.True(result.Success);
        Assert.NotNull(result.ParsedProject);
        Assert.Equal("MockProject", result.ParsedProject.ProjectName);
        Assert.Equal(3, result.ParsedProject.Pous.Count);

        // Verify source hash remains 100% untouched
        string currentSha = workspaceManager.ComputeSha256(sampleFile);
        Assert.Equal(expectedSha, currentSha);
    }

    [Fact]
    public async Task Worker_ValidatePou_FailsWhenInvalidNetworksDetected()
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        var config = new SiemensSmartBridgeConfig
        {
            AllowedWorkspaceRoots = new[] { _tempTestDir }
        };

        bridge.OnExecute = (cmd, args) =>
        {
            Assert.Equal("validate_project", cmd);
            return JsonSerializer.Serialize(new
            {
                success = true,
                completed = true,
                all_valid = false,
                blocks = new Dictionary<string, object>
                {
                    ["SBR0"] = new
                    {
                        nets = 5,
                        invalid = new[] { 2, 4 }
                    }
                }
            });
        };

        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, config);

        var sampleFile = Path.Combine(_tempTestDir, "demo_val.smart");
        File.WriteAllBytes(sampleFile, [0x01, 0x02, 0x03]);
        string originalSha = workspaceManager.ComputeSha256(sampleFile);

        var job = new EngineeringJobRequest(
            JobId: "job-val-01",
            JobType: EngineeringJobType.ValidatePou,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile,
            Options: new Dictionary<string, string>
            {
                ["blockNames"] = "SBR0"
            });

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.NotNull(result.StaticCheck);
        Assert.False(result.StaticCheck.Passed);
        Assert.Equal(2, result.StaticCheck.Diagnostics.Count);
        Assert.Contains("Network 2", result.StaticCheck.Diagnostics[0].Message);
        Assert.Contains("Network 4", result.StaticCheck.Diagnostics[1].Message);

        // Verify source hash remained strictly unchanged
        Assert.Equal(originalSha, workspaceManager.ComputeSha256(sampleFile));
    }

    [Fact]
    public async Task Worker_CompileProject_NeverClaimsSuccessUnlessAllStagesPass()
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        var config = new SiemensSmartBridgeConfig
        {
            AllowedWorkspaceRoots = new[] { _tempTestDir }
        };

        // Simulate a sneaky case: stage 2 (COMPILE ret=0) is PASS, but stage 3 (POU_IsValidNet) is FAIL
        bridge.OnExecute = (cmd, args) =>
        {
            Assert.Equal("deploy_verify", cmd);
            return JsonSerializer.Serialize(new
            {
                success = true,
                report = new
                {
                    stage1_structure = "PASS",
                    stage2_compile = "PASS", // ret=0 will fool naive checks!
                    stage3_engine_validate = "FAIL", // invalid networks detected!
                    stage4_roundtrip = "PASS",
                    stage5_persisted = "PASS",
                    passed = false
                }
            });
        };

        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, config);

        var sampleFile = Path.Combine(_tempTestDir, "demo_comp.smart");
        File.WriteAllBytes(sampleFile, [0x11, 0x22, 0x33]);
        string originalSha = workspaceManager.ComputeSha256(sampleFile);

        var awlFile = Path.Combine(_tempTestDir, "test.awl");
        File.WriteAllText(awlFile, "TITLE=Subroutine\nNetwork 1\nLD I0.0\n= Q0.0\n");

        var job = new EngineeringJobRequest(
            JobId: "job-comp-01",
            JobType: EngineeringJobType.CompileProject,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile,
            Options: new Dictionary<string, string>
            {
                ["awlFiles"] = awlFile
            });

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.NotNull(result.StaticCheck);
        Assert.False(result.StaticCheck.Passed);
        Assert.Contains("5-stage verification FAILED", result.StaticCheck.Summary);
        Assert.Equal(originalSha, workspaceManager.ComputeSha256(sampleFile));
    }

    [Fact]
    public async Task Worker_RealSmart200_OfflineOverview_RealProjectTest()
    {
        // Integration test with the real smart200_mcp python package on real file D:\SMart200 MCP\work\_autoflow.smart
        string realSmartFile = @"D:\SMart200 MCP\work\_autoflow.smart";
        if (!File.Exists(realSmartFile))
            return; // Skip if file not found

        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new VendorDoctor();
        var bridge = new SiemensSmartBridge();

        if (!bridge.IsAvailable)
            return; // Skip if python runtime not available

        var config = new SiemensSmartBridgeConfig
        {
            AllowedWorkspaceRoots = new[] { _tempTestDir, Path.GetDirectoryName(realSmartFile)! }
        };
        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, config);
        string originalSha = workspaceManager.ComputeSha256(realSmartFile);

        var job = new EngineeringJobRequest(
            JobId: "job-real-overview",
            JobType: EngineeringJobType.InspectProject,
            Vendor: PlcVendor.Siemens,
            ProjectPath: realSmartFile);

        var result = await worker.ExecuteAsync(job);

        Assert.True(result.Success);
        Assert.Equal(CapabilityStatus.Supported, result.Status);
        Assert.NotNull(result.ParsedProject);
        Assert.Contains("Siemens-S7-200-SMART", result.ParsedProject.Format);
        Assert.True(result.ParsedProject.Pous.Count > 0);

        // Verify original file strictly unchanged
        Assert.Equal(originalSha, workspaceManager.ComputeSha256(realSmartFile));
    }

    [Fact]
    public async Task Worker_DefaultNoRoots_FailsClosed()
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        // Default config without AllowedWorkspaceRoots -> MUST fail closed
        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, new SiemensSmartBridgeConfig());

        var sampleFile = Path.Combine(_tempTestDir, "demo_failclosed.smart");
        File.WriteAllBytes(sampleFile, [0x01, 0x02]);

        var job = new EngineeringJobRequest(
            JobId: "job-failclosed-01",
            JobType: EngineeringJobType.InspectProject,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile);

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("fail closed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("bad|name")]
    [InlineData("bad/name")]
    [InlineData("bad\\name")]
    [InlineData("bad:name")]
    [InlineData("bad\0name")]
    [InlineData("bad*name")]
    [InlineData("   ")]
    [InlineData("")]
    public async Task Worker_ExportPou_RejectsUnsafeBlockName(string unsafeBlockName)
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        var config = new SiemensSmartBridgeConfig { AllowedWorkspaceRoots = new[] { _tempTestDir } };
        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, config);

        var sampleFile = Path.Combine(_tempTestDir, "demo_export_unsafe.smart");
        File.WriteAllBytes(sampleFile, [0x01, 0x02]);

        var job = new EngineeringJobRequest(
            JobId: "job-export-unsafe",
            JobType: EngineeringJobType.ExportPou,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile,
            Options: new Dictionary<string, string>
            {
                ["blockName"] = unsafeBlockName
            });

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("blockName", result.Message);
    }

    [Fact]
    public async Task Worker_ExportPou_AcceptsSafeBlockNamesAndSanitizesPath()
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        var config = new SiemensSmartBridgeConfig { AllowedWorkspaceRoots = new[] { _tempTestDir } };

        string safeBlockName = "初始化_SBR-01 Main";
        bridge.OnExecute = (cmd, args) =>
        {
            if (cmd == "export_blocks")
            {
                var namesToPaths = JsonSerializer.Deserialize<Dictionary<string, string>>(args["names_to_paths"])!;
                string awlPath = namesToPaths[safeBlockName];
                // Simulate generated awl file
                File.WriteAllText(awlPath, "TITLE=Subroutine\nNetwork 1\nLD I0.0\n");
                return JsonSerializer.Serialize(new { success = true });
            }
            if (cmd == "analyze_awl")
            {
                return JsonSerializer.Serialize(new { success = true, analysis = new { networks = 1 } });
            }
            return JsonSerializer.Serialize(new { success = true });
        };

        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, config);
        var sampleFile = Path.Combine(_tempTestDir, "demo_export_safe.smart");
        File.WriteAllBytes(sampleFile, [0x01, 0x02]);

        var job = new EngineeringJobRequest(
            JobId: "job-export-safe",
            JobType: EngineeringJobType.ExportPou,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile,
            Options: new Dictionary<string, string>
            {
                ["blockName"] = safeBlockName
            });

        var result = await worker.ExecuteAsync(job);

        Assert.True(result.Success);
        Assert.Equal(CapabilityStatus.Supported, result.Status);
        Assert.NotNull(result.ExportedOutputs);
        Assert.True(result.ExportedOutputs.ContainsKey(safeBlockName));
        string targetAwlPath = result.ExportedOutputs[safeBlockName];
        Assert.True(Path.IsPathFullyQualified(targetAwlPath));
        Assert.Contains("export_out", targetAwlPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SBR0, ../evil")]
    [InlineData("SBR0|evil")]
    [InlineData("SBR0, bad/slash")]
    public async Task Worker_ValidatePou_RejectsEmptyOrUnsafeBlockNames(string invalidBlockNames)
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        var config = new SiemensSmartBridgeConfig { AllowedWorkspaceRoots = new[] { _tempTestDir } };
        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, config);

        var sampleFile = Path.Combine(_tempTestDir, "demo_val_unsafe.smart");
        File.WriteAllBytes(sampleFile, [0x01, 0x02]);

        var job = new EngineeringJobRequest(
            JobId: "job-val-unsafe",
            JobType: EngineeringJobType.ValidatePou,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile,
            Options: new Dictionary<string, string>
            {
                ["blockNames"] = invalidBlockNames
            });

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
    }

    [Fact]
    public async Task Worker_ValidatePou_IncompleteValidation_FailsWithDiagnostic()
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        var config = new SiemensSmartBridgeConfig { AllowedWorkspaceRoots = new[] { _tempTestDir } };

        bridge.OnExecute = (cmd, args) => JsonSerializer.Serialize(new
        {
            success = true,
            completed = false, // Engine was interrupted or incomplete
            all_valid = true,
            blocks = new Dictionary<string, object>
            {
                ["SBR0"] = new { nets = 1, invalid = Array.Empty<int>() }
            }
        });

        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, config);
        var sampleFile = Path.Combine(_tempTestDir, "demo_val_incomplete.smart");
        File.WriteAllBytes(sampleFile, [0x01, 0x02]);

        var job = new EngineeringJobRequest(
            JobId: "job-val-incomplete",
            JobType: EngineeringJobType.ValidatePou,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile,
            Options: new Dictionary<string, string> { ["blockNames"] = "SBR0" });

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.NotNull(result.StaticCheck);
        Assert.False(result.StaticCheck.Passed);
        Assert.DoesNotContain("0 invalid networks", result.StaticCheck.Summary);
        Assert.Contains(result.StaticCheck.Diagnostics, d => d.Code == "SMART_VALIDATE_INCOMPLETE");
    }

    [Fact]
    public async Task Worker_ValidatePou_MissingOrEmptyBlocks_FailsWithDiagnostic()
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        var config = new SiemensSmartBridgeConfig { AllowedWorkspaceRoots = new[] { _tempTestDir } };

        bridge.OnExecute = (cmd, args) => JsonSerializer.Serialize(new
        {
            success = true,
            completed = true,
            all_valid = true,
            blocks = new Dictionary<string, object>() // Missing/empty blocks dictionary!
        });

        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, config);
        var sampleFile = Path.Combine(_tempTestDir, "demo_val_noblocks.smart");
        File.WriteAllBytes(sampleFile, [0x01, 0x02]);

        var job = new EngineeringJobRequest(
            JobId: "job-val-noblocks",
            JobType: EngineeringJobType.ValidatePou,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile,
            Options: new Dictionary<string, string> { ["blockNames"] = "SBR0" });

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.NotNull(result.StaticCheck);
        Assert.False(result.StaticCheck.Passed);
        Assert.DoesNotContain("0 invalid networks", result.StaticCheck.Summary);
        Assert.Contains(result.StaticCheck.Diagnostics, d => d.Code == "SMART_VALIDATE_NO_BLOCKS");
    }

    [Fact]
    public async Task Worker_ValidatePou_CorruptedInvalidArray_HandledGracefully()
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        var config = new SiemensSmartBridgeConfig { AllowedWorkspaceRoots = new[] { _tempTestDir } };

        bridge.OnExecute = (cmd, args) => JsonSerializer.Serialize(new
        {
            success = true,
            completed = true,
            all_valid = true,
            blocks = new Dictionary<string, object>
            {
                ["SBR0"] = new
                {
                    nets = 2,
                    invalid = new object[] { "corrupted_string", 3 } // string instead of int
                }
            }
        });

        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, config);
        var sampleFile = Path.Combine(_tempTestDir, "demo_val_corrupt.smart");
        File.WriteAllBytes(sampleFile, [0x01, 0x02]);

        var job = new EngineeringJobRequest(
            JobId: "job-val-corrupt",
            JobType: EngineeringJobType.ValidatePou,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile,
            Options: new Dictionary<string, string> { ["blockNames"] = "SBR0" });

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.NotNull(result.StaticCheck);
        Assert.False(result.StaticCheck.Passed);
        Assert.Contains(result.StaticCheck.Diagnostics, d => d.Code == "SMART_VALIDATE_INVALID_TYPE");
    }

    [Fact]
    public async Task Worker_CompileProject_RejectsUnsafeOrNonExistentAwlFiles()
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        var config = new SiemensSmartBridgeConfig { AllowedWorkspaceRoots = new[] { _tempTestDir } };
        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, config);

        var sampleFile = Path.Combine(_tempTestDir, "demo_comp_awl.smart");
        File.WriteAllBytes(sampleFile, [0x01, 0x02]);

        // Case 1: non existent file
        var nonExistentPath = Path.Combine(_tempTestDir, "non_existent.awl");
        var jobNonExistent = new EngineeringJobRequest(
            JobId: "job-comp-noawl",
            JobType: EngineeringJobType.CompileProject,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile,
            Options: new Dictionary<string, string> { ["awlFiles"] = nonExistentPath });

        var resNonExistent = await worker.ExecuteAsync(jobNonExistent);
        Assert.False(resNonExistent.Success);
        Assert.Contains("does not exist", resNonExistent.Message);

        // Case 2: outside workspace root
        var outsideAwl = Path.Combine(Path.GetTempPath(), "outside.awl");
        File.WriteAllText(outsideAwl, "LD I0.0");
        try
        {
            var jobOutside = new EngineeringJobRequest(
                JobId: "job-comp-outsideawl",
                JobType: EngineeringJobType.CompileProject,
                Vendor: PlcVendor.Siemens,
                ProjectPath: sampleFile,
                Options: new Dictionary<string, string> { ["awlFiles"] = outsideAwl });

            var resOutside = await worker.ExecuteAsync(jobOutside);
            Assert.False(resOutside.Success);
            Assert.Contains("validation failed", resOutside.Message);
        }
        finally
        {
            if (File.Exists(outsideAwl)) File.Delete(outsideAwl);
        }
    }

    [Theory]
    [InlineData("[]")] // Array instead of object
    [InlineData("\"string\"")] // Primitive string
    [InlineData("{\"invalid;key\": \"I0.0\"}")] // Invalid character in key
    [InlineData("{\"safe_key\": 123}")] // Value is not string
    [InlineData("{\"safe_key\": \"bad address;;\"}")] // Value invalid address
    public async Task Worker_CompileProject_RejectsInvalidSymbols(string invalidSymbolsJson)
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        var config = new SiemensSmartBridgeConfig { AllowedWorkspaceRoots = new[] { _tempTestDir } };
        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, config);

        var sampleFile = Path.Combine(_tempTestDir, "demo_comp_syms.smart");
        File.WriteAllBytes(sampleFile, [0x01, 0x02]);

        var awlFile = Path.Combine(_tempTestDir, "valid.awl");
        File.WriteAllText(awlFile, "LD I0.0");

        var job = new EngineeringJobRequest(
            JobId: "job-comp-invalidsyms",
            JobType: EngineeringJobType.CompileProject,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile,
            Options: new Dictionary<string, string>
            {
                ["awlFiles"] = awlFile,
                ["symbols"] = invalidSymbolsJson
            });

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("symbol", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Worker_InspectProject_V3OfflineParsableFalse_ReturnsUnsupportedWithLimitations()
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        var config = new SiemensSmartBridgeConfig { AllowedWorkspaceRoots = new[] { _tempTestDir } };

        bridge.OnExecute = (cmd, args) => JsonSerializer.Serialize(new
        {
            success = true,
            format = "V3",
            offline_parsable = false,
            reason = "Encrypted container stream",
            limitations = "V3 (.smartV3) data segment is encrypted. Offline overview is unsupported."
        });

        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, config);
        var sampleFile = Path.Combine(_tempTestDir, "demo_v3.smartV3");
        File.WriteAllBytes(sampleFile, [0x53, 0x4D, 0x41, 0x52, 0x54, 0x56, 0x33]);

        var job = new EngineeringJobRequest(
            JobId: "job-inspect-v3",
            JobType: EngineeringJobType.InspectProject,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile);

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("unsupported for V3", result.Message);
        Assert.Contains("encrypted", result.Details);
    }

    [Fact]
    public async Task Worker_ExecuteAsync_CatchesBridgeAndTimeoutExceptions()
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        var config = new SiemensSmartBridgeConfig { AllowedWorkspaceRoots = new[] { _tempTestDir } };

        bridge.OnExecute = (cmd, args) => throw new TimeoutException("Simulated bridge timeout.");

        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, config);
        var sampleFile = Path.Combine(_tempTestDir, "demo_timeout.smart");
        File.WriteAllBytes(sampleFile, [0x01, 0x02]);

        var job = new EngineeringJobRequest(
            JobId: "job-timeout-01",
            JobType: EngineeringJobType.InspectProject,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile);

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Experimental, result.Status);
        Assert.Contains("timed out", result.Message);
    }

    [Fact]
    public async Task Worker_ExecuteAsync_CatchesJsonException_ReturnsGracefully()
    {
        var workspaceManager = new ProjectWorkspaceManager(_tempTestDir);
        var doctor = new MockDoctor();
        var bridge = new MockSmartBridge();
        var config = new SiemensSmartBridgeConfig { AllowedWorkspaceRoots = new[] { _tempTestDir } };

        bridge.OnExecute = (cmd, args) => "This is not valid JSON at all!";

        var worker = new SiemensSmartEngineeringWorker(workspaceManager, doctor, bridge, config);
        var sampleFile = Path.Combine(_tempTestDir, "demo_badjson.smart");
        File.WriteAllBytes(sampleFile, [0x01, 0x02]);

        var job = new EngineeringJobRequest(
            JobId: "job-badjson-01",
            JobType: EngineeringJobType.InspectProject,
            Vendor: PlcVendor.Siemens,
            ProjectPath: sampleFile);

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Experimental, result.Status);
        Assert.Contains("JSON", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
