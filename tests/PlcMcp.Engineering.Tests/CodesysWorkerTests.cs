using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Workers.Codesys;
using PlcMcp.Engineering.Workers.External;
using PlcMcp.Engineering.Workspace;

namespace PlcMcp.Engineering.Tests;

public class CodesysWorkerTests : IDisposable
{
    private const string Profile = "CODESYS V3.5 SP22 Patch 3";
    private const string ProductVersion = "3.5.22.30";

    private readonly string _tempWorkspace;
    private readonly string _mockExePath;
    private readonly string _mockScriptPath;
    private readonly string _mockScriptSha256;

    public CodesysWorkerTests()
    {
        _tempWorkspace = Path.Combine(Path.GetTempPath(), "PlcMcp_CodesysTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempWorkspace);

        _mockExePath = Path.Combine(_tempWorkspace, "CODESYS.exe");
        File.WriteAllBytes(_mockExePath, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });

        _mockScriptPath = Path.Combine(_tempWorkspace, "codesys_worker.py");
        File.WriteAllText(_mockScriptPath, "# mock codesys python worker\nprint('hello')\n");
        _mockScriptSha256 = ComputeSha256(_mockScriptPath);
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
            // Best-effort cleanup
        }
    }

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private CodesysWorkerConfig CreateValidConfig() => new()
    {
        CodesysExePath = _mockExePath,
        ProfileName = Profile,
        CodesysVersion = ProductVersion,
        DriverScriptPath = _mockScriptPath,
        DriverScriptSha256 = _mockScriptSha256,
        ProjectRoot = _tempWorkspace,
        ExpectedWorkerName = "Codesys-ScriptEngine-Worker",
        ExpectedWorkerVersion = "1.0",
        ExpectedProtocolVersion = "1.0",
        RequirePinnedIdentity = true
    };

    private static string HandshakeReply(string id, string workerName, string runtime, params string[] capabilities) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            result = new WorkerHandshakeResponse(
                WorkerName: workerName,
                WorkerVersion: "1.0",
                ProtocolVersion: "1.0",
                Vendor: "CODESYS",
                Bitness: "64-bit",
                RuntimeEnvironment: runtime,
                Capabilities: capabilities)
        });

    private static string DoctorReply(string id) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            result = new WorkerDoctorReport(
                Healthy: true,
                Installed: true,
                ToolchainPath: "CODESYS.exe",
                ToolchainVersion: "3.5.22.30",
                Bitness: "64-bit",
                Details: "ScriptEngine available; no project opened.",
                Checks: new[] { new WorkerDoctorCheckItem("ScriptEngine", true, "ok") })
        });

    private static string OkHandshake(string id) =>
        HandshakeReply(id, "Codesys-ScriptEngine-Worker", "CODESYS.exe " + Profile + ", ScriptEngine.plugin 4.2.0.0",
            "InspectProject", "ExportPou", "ValidatePou", "CompileProject");

    #region Fakes

    private sealed class FakeInstalledDetector : IVendorProfileDetector
    {
        public PlcVendor Vendor => PlcVendor.Generic;
        public string VendorName => "CODESYS";
        public string ToolchainName => "CODESYS Development System V3";
        private readonly string _exePath;

        public FakeInstalledDetector(string exePath) => _exePath = exePath;

        public ExternalVendorProfile DetectProfile() =>
            new(
                Vendor: Vendor,
                VendorName: VendorName,
                ToolchainName: ToolchainName,
                Installed: true,
                ExecutablePath: _exePath,
                Version: "3.5.22.30",
                Bitness: "64-bit",
                Capabilities: new CapabilitySet(Array.Empty<CapabilityDescriptor>()),
                Details: "Fake installed CODESYS");
    }

    private sealed class MockExternalProcess : IExternalProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AsyncPipeStream _outPipe = new();
        private readonly AsyncPipeStream _errPipe = new();
        private readonly StreamWriter _outWriter;
        private readonly StreamWriter _errWriter;

        public int Id { get; set; } = 54321;
        public bool HasExited => _exit.Task.IsCompleted;
        public int ExitCode { get; set; }

        public StreamWriter StandardInput { get; }
        public StreamReader StandardOutput { get; }
        public StreamReader StandardError { get; }

        public MockExternalProcess(Func<string, string?> handler)
        {
            StandardOutput = new StreamReader(_outPipe, Encoding.UTF8);
            StandardError = new StreamReader(_errPipe, Encoding.UTF8);
            _outWriter = new StreamWriter(_outPipe, Encoding.UTF8) { AutoFlush = true };
            _errWriter = new StreamWriter(_errPipe, Encoding.UTF8) { AutoFlush = true };

            StandardInput = new DelegateWriter(line =>
            {
                string? response = null;
                try
                {
                    response = handler(line);
                }
                finally
                {
                    if (response != null)
                    {
                        _outWriter.WriteLine(response);
                    }

                    if (line.Contains("\"shutdown\"", StringComparison.Ordinal))
                    {
                        SimulateExit(0);
                    }
                }
            });
        }

        public void SimulateExit(int code)
        {
            ExitCode = code;
            _exit.TrySetResult();
        }

        public void Kill(bool entireProcessTree = true)
        {
            SimulateExit(-1);
            _outPipe.Complete();
            _errPipe.Complete();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => _exit.Task.WaitAsync(cancellationToken);

        public void Dispose()
        {
            _outPipe.Dispose();
            _errPipe.Dispose();
        }
    }

    private sealed class DelegateWriter : StreamWriter
    {
        private readonly Action<string> _onLine;

        public DelegateWriter(Action<string> onLine) : base(Stream.Null) => _onLine = onLine;

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            _onLine(buffer.ToString());
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(string? value)
        {
            _onLine(value ?? string.Empty);
            return Task.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AsyncPipeStream : Stream
    {
        private readonly Queue<byte[]> _queue = new();
        private readonly object _lock = new();
        private bool _isCompleted;
        private byte[]? _current;
        private int _offset;

        public void Complete()
        {
            lock (_lock)
            {
                _isCompleted = true;
                Monitor.PulseAll(_lock);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            lock (_lock)
            {
                _queue.Enqueue(copy);
                Monitor.PulseAll(_lock);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                while (_current == null || _offset >= _current.Length)
                {
                    if (_queue.Count > 0)
                    {
                        _current = _queue.Dequeue();
                        _offset = 0;
                        break;
                    }
                    if (_isCompleted)
                    {
                        return 0;
                    }
                    Monitor.Wait(_lock, 50);
                }

                int toCopy = Math.Min(count, _current.Length - _offset);
                Buffer.BlockCopy(_current, _offset, buffer, offset, toCopy);
                _offset += toCopy;
                return toCopy;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>Runs a fresh mock process per launch, mirroring a real one-shot worker.</summary>
    private sealed class MockProcessRunner : IProcessRunner
    {
        private readonly Func<int, MockExternalProcess> _factory;
        public List<ProcessStartInfo> StartInfos { get; } = new();
        public int StartCount => StartInfos.Count;

        public MockProcessRunner(Func<string, string?> handler)
            => _factory = _ => new MockExternalProcess(handler);

        public MockProcessRunner(Func<int, string, string?> handler)
            => _factory = index => new MockExternalProcess(line => handler(index, line));

        public IExternalProcess Start(ProcessStartInfo startInfo)
        {
            StartInfos.Add(startInfo);
            return _factory(StartInfos.Count - 1);
        }
    }

    /// <summary>A runner that fails to produce a process at all (simulates a broken launch).</summary>
    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public IExternalProcess Start(ProcessStartInfo startInfo) =>
            throw new InvalidOperationException("Simulated launch failure.");
    }

    #endregion

    #region Doctor / configuration

    [Fact]
    public void CheckDoctor_WhenStartupHandshakeSucceeds_ReturnsHealthy()
    {
        var runner = new MockProcessRunner(line =>
        {
            using var doc = JsonDocument.Parse(line);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;
            return method switch
            {
                "handshake" => OkHandshake(id),
                "doctor" => DoctorReply(id),
                _ => null
            };
        });

        var worker = new CodesysWorker(CreateValidConfig(), new FakeInstalledDetector(_mockExePath), new ProjectWorkspaceManager(_tempWorkspace), runner);

        var report = worker.CheckDoctor();

        Assert.True(report.Healthy);
        Assert.Contains(report.Checks!, c => c.Name == "scriptengine_profile" && c.Passed);
        Assert.Contains(report.Checks!, c => c.Name == "worker_identity" && c.Passed);
        Assert.True(worker.IsAvailable);

        // The probe must have used the raw command line with preserved quotes.
        var info = Assert.Single(runner.StartInfos);
        Assert.False(info.UseShellExecute);
        Assert.Empty(info.ArgumentList);
        Assert.Contains($"--profile=\"{Profile}\"", info.Arguments);
        Assert.Contains($"--runscript=\"{_mockScriptPath}\"", info.Arguments);
    }

    [Fact]
    public void CheckDoctor_WhenScriptHashMismatch_FailsClosedWithoutLaunching()
    {
        var config = CreateValidConfig();
        config.DriverScriptSha256 = new string('0', 64);

        var runner = new MockProcessRunner(_ => throw new InvalidOperationException("Must not launch."));
        var worker = new CodesysWorker(config, new FakeInstalledDetector(_mockExePath), new ProjectWorkspaceManager(_tempWorkspace), runner);

        var report = worker.CheckDoctor();

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks!, c => c.Name == "script_sha256" && !c.Passed);
        Assert.Equal(0, runner.StartCount);
    }

    [Fact]
    public void CheckDoctor_WhenVersionPinDoesNotMatch_FailsClosed()
    {
        var config = CreateValidConfig();
        config.CodesysVersion = "3.5.12";

        var worker = new CodesysWorker(config, new FakeInstalledDetector(_mockExePath), new ProjectWorkspaceManager(_tempWorkspace));
        var report = worker.CheckDoctor();

        Assert.False(report.Healthy);
        Assert.Contains(report.Checks!, c => c.Name == "version_pin" && !c.Passed);
    }

    [Fact]
    public void CheckDoctor_WhenStartupHandshakeFails_ReportsFailure()
    {
        var runner = new MockProcessRunner(_ => null); // never answers
        var worker = new CodesysWorker(CreateValidConfig(), new FakeInstalledDetector(_mockExePath), new ProjectWorkspaceManager(_tempWorkspace), runner);

        var report = worker.CheckDoctor();

        Assert.False(report.Healthy);
        Assert.False(worker.IsAvailable);
    }

    #endregion

    #region Safety rejection

    [Theory]
    [InlineData("download")]
    [InlineData("login")]
    [InlineData("force")]
    [InlineData("upload")]
    [InlineData("save")]
    [InlineData("delete")]
    public async Task ExecuteAsync_RejectsProhibitedOptionKeywords(string keyword)
    {
        var runner = new MockProcessRunner(_ => null);
        var worker = new CodesysWorker(CreateValidConfig(), new FakeInstalledDetector(_mockExePath), new ProjectWorkspaceManager(_tempWorkspace), runner);

        string sample = Path.Combine(_tempWorkspace, "demo.project");
        File.WriteAllText(sample, "DATA");

        var job = new EngineeringJobRequest(
            JobId: "job-opt",
            JobType: EngineeringJobType.InspectProject,
            Vendor: PlcVendor.Generic,
            ProjectPath: sample,
            Options: new Dictionary<string, string> { ["operation_mode"] = "ScriptOnline." + keyword });

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("rejected option", result.Details);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsUnsupportedJobType()
    {
        var worker = new CodesysWorker(CreateValidConfig(), new FakeInstalledDetector(_mockExePath), new ProjectWorkspaceManager(_tempWorkspace));
        string sample = Path.Combine(_tempWorkspace, "demo.project");
        File.WriteAllText(sample, "DATA");

        var job = new EngineeringJobRequest("job-diff", EngineeringJobType.DiffProjects, PlcVendor.Generic, sample);
        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("does not support job type", result.Details);
    }

    #endregion

    #region Offline operations

    [Fact]
    public async Task ExecuteAsync_InspectProject_CompletesAndCalibratesToExperimental()
    {
        string sourceProj = Path.Combine(_tempWorkspace, "original.project");
        File.WriteAllText(sourceProj, "ORIGINAL-PROJECT-CONTENT");
        string originalSha = ComputeSha256(sourceProj);

        string artifactSha = string.Empty;
        var runner = new MockProcessRunner(line =>
        {
            using var doc = JsonDocument.Parse(line);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;

            if (method == "handshake") return OkHandshake(id);
            if (method == "doctor") return DoctorReply(id);
            if (method == "submit")
            {
                string workProjectPath = doc.RootElement.GetProperty("params").GetProperty("projectPath").GetString()!;
                string workDir = Path.GetDirectoryName(workProjectPath)!;
                string artPath = Path.Combine(workDir, "codesys_inspect.json");
                File.WriteAllText(artPath, "{\"inspected\": true}");
                artifactSha = ComputeSha256(artPath);
                return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new WorkerJobStatusResponse("job-1", "completed", 1.0, "OK", 0) });
            }
            if (method == "artifacts")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new WorkerArtifactsResponse("job-1", new[]
                    {
                        new WorkerArtifactDescriptor("codesys_inspect.json", "codesys_inspect.json", "JSON", 20, artifactSha)
                    })
                });
            }
            return null;
        });

        var worker = new CodesysWorker(CreateValidConfig(), new FakeInstalledDetector(_mockExePath), new ProjectWorkspaceManager(_tempWorkspace), runner);

        var job = new EngineeringJobRequest("job-1", EngineeringJobType.InspectProject, PlcVendor.Generic, sourceProj);
        var result = await worker.ExecuteAsync(job);

        Assert.True(result.Success);
        Assert.Equal(CapabilityStatus.Experimental, result.Status);
        Assert.NotNull(result.ExportedOutputs);
        Assert.True(result.ExportedOutputs!.ContainsKey("codesys_inspect.json"));

        // Original project must remain byte-for-byte identical.
        Assert.Equal(originalSha, ComputeSha256(sourceProj));
    }

    [Fact]
    public async Task ExecuteAsync_CompileProject_HandlesStatusPollingAndArtifacts()
    {
        string sourceProj = Path.Combine(_tempWorkspace, "compile.project");
        File.WriteAllText(sourceProj, "COMPILE-PROJECT-DATA");

        int statusPolls = 0;
        string artifactSha = string.Empty;

        var runner = new MockProcessRunner(line =>
        {
            using var doc = JsonDocument.Parse(line);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;

            if (method == "handshake") return OkHandshake(id);
            if (method == "doctor") return DoctorReply(id);
            if (method == "submit")
            {
                string workProjectPath = doc.RootElement.GetProperty("params").GetProperty("projectPath").GetString()!;
                string workDir = Path.GetDirectoryName(workProjectPath)!;
                string artPath = Path.Combine(workDir, "codesys_compile_report.json");
                File.WriteAllText(artPath, "{\"mode\":\"build\",\"messages\":[]}");
                artifactSha = ComputeSha256(artPath);
                return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new WorkerJobStatusResponse("job-2", "running", 0.5, "Building...", null) });
            }
            if (method == "status")
            {
                statusPolls++;
                return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new WorkerJobStatusResponse("job-2", "completed", 1.0, "Build finished", 0) });
            }
            if (method == "artifacts")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new WorkerArtifactsResponse("job-2", new[]
                    {
                        new WorkerArtifactDescriptor("codesys_compile_report.json", "codesys_compile_report.json", "BuildReport", 24, artifactSha)
                    })
                });
            }
            return null;
        });

        var worker = new CodesysWorker(CreateValidConfig(), new FakeInstalledDetector(_mockExePath), new ProjectWorkspaceManager(_tempWorkspace), runner);

        var job = new EngineeringJobRequest("job-2", EngineeringJobType.CompileProject, PlcVendor.Generic, sourceProj,
            new Dictionary<string, string> { ["mode"] = "build" });
        var result = await worker.ExecuteAsync(job);

        Assert.True(result.Success);
        Assert.Equal(CapabilityStatus.Experimental, result.Status);
        Assert.True(statusPolls >= 1);
        Assert.True(result.ExportedOutputs!.ContainsKey("codesys_compile_report.json"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenIdentityMismatchesAtExecution_ReturnsExperimentalFailure()
    {
        string sourceProj = Path.Combine(_tempWorkspace, "demo.project");
        File.WriteAllText(sourceProj, "DATA");

        // First process (startup probe) is honest; the second (job) impersonates.
        var runner = new MockProcessRunner((index, line) =>
        {
            using var doc = JsonDocument.Parse(line);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;
            if (method == "handshake")
            {
                return index == 0
                    ? OkHandshake(id)
                    : HandshakeReply(id, "Imposter-Codesys-Worker", "CODESYS.exe " + Profile, "InspectProject", "ExportPou", "CompileProject");
            }
            if (method == "doctor") return DoctorReply(id);
            return null;
        });

        var worker = new CodesysWorker(CreateValidConfig(), new FakeInstalledDetector(_mockExePath), new ProjectWorkspaceManager(_tempWorkspace), runner);

        var job = new EngineeringJobRequest("job-mismatch", EngineeringJobType.InspectProject, PlcVendor.Generic, sourceProj);
        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Experimental, result.Status);
        Assert.Contains("identity verification failed", result.Message);
        Assert.Contains("Imposter-Codesys-Worker", result.Details);
    }

    [Fact]
    public async Task ExecuteAsync_WhenArtifactPathEscapes_Fails()
    {
        string sourceProj = Path.Combine(_tempWorkspace, "demo.project");
        File.WriteAllText(sourceProj, "DATA");

        var runner = new MockProcessRunner(line =>
        {
            using var doc = JsonDocument.Parse(line);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;
            if (method == "handshake") return OkHandshake(id);
            if (method == "doctor") return DoctorReply(id);
            if (method == "submit")
                return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new WorkerJobStatusResponse("job-esc", "completed", 1.0, "OK", 0) });
            if (method == "artifacts")
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new WorkerArtifactsResponse("job-esc", new[]
                    {
                        new WorkerArtifactDescriptor("escaped", "../../outside.txt", "TXT", 10, new string('a', 64))
                    })
                });
            return null;
        });

        var worker = new CodesysWorker(CreateValidConfig(), new FakeInstalledDetector(_mockExePath), new ProjectWorkspaceManager(_tempWorkspace), runner);

        var job = new EngineeringJobRequest("job-esc", EngineeringJobType.InspectProject, PlcVendor.Generic, sourceProj);
        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Experimental, result.Status);
        Assert.Contains("escapes constrained working directory", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WhenArtifactHashMismatches_Fails()
    {
        string sourceProj = Path.Combine(_tempWorkspace, "demo.project");
        File.WriteAllText(sourceProj, "DATA");

        var runner = new MockProcessRunner(line =>
        {
            using var doc = JsonDocument.Parse(line);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;
            if (method == "handshake") return OkHandshake(id);
            if (method == "doctor") return DoctorReply(id);
            if (method == "submit")
            {
                string workProjectPath = doc.RootElement.GetProperty("params").GetProperty("projectPath").GetString()!;
                string workDir = Path.GetDirectoryName(workProjectPath)!;
                File.WriteAllText(Path.Combine(workDir, "art.xml"), "<xml>data</xml>");
                return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new WorkerJobStatusResponse("job-hash", "completed", 1.0, "OK", 0) });
            }
            if (method == "artifacts")
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new WorkerArtifactsResponse("job-hash", new[]
                    {
                        new WorkerArtifactDescriptor("art.xml", "art.xml", "XML", 15, new string('f', 64))
                    })
                });
            return null;
        });

        var worker = new CodesysWorker(CreateValidConfig(), new FakeInstalledDetector(_mockExePath), new ProjectWorkspaceManager(_tempWorkspace), runner);

        var job = new EngineeringJobRequest("job-hash", EngineeringJobType.InspectProject, PlcVendor.Generic, sourceProj);
        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Experimental, result.Status);
        Assert.Contains("integrity check failed", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStartupProbeFails_ReturnsUnsupportedWithoutJobExecution()
    {
        var runner = new ThrowingProcessRunner();
        var worker = new CodesysWorker(CreateValidConfig(), new FakeInstalledDetector(_mockExePath), new ProjectWorkspaceManager(_tempWorkspace), runner);

        string sourceProj = Path.Combine(_tempWorkspace, "demo.project");
        File.WriteAllText(sourceProj, "DATA");

        var job = new EngineeringJobRequest("job-not-ready", EngineeringJobType.InspectProject, PlcVendor.Generic, sourceProj);
        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("not ready", result.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCompileReturnsCompletedWithoutArtifacts_Fails()
    {
        string sourceProj = Path.Combine(_tempWorkspace, "demo.project");
        File.WriteAllText(sourceProj, "DATA");

        var runner = new MockProcessRunner(line =>
        {
            using var doc = JsonDocument.Parse(line);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;
            if (method == "handshake") return OkHandshake(id);
            if (method == "doctor") return DoctorReply(id);
            if (method == "submit")
                return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new WorkerJobStatusResponse("job-noart", "completed", 1.0, "OK", 0) });
            if (method == "artifacts")
                return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new WorkerArtifactsResponse("job-noart", Array.Empty<WorkerArtifactDescriptor>()) });
            return null;
        });

        var worker = new CodesysWorker(CreateValidConfig(), new FakeInstalledDetector(_mockExePath), new ProjectWorkspaceManager(_tempWorkspace), runner);

        var job = new EngineeringJobRequest("job-noart", EngineeringJobType.CompileProject, PlcVendor.Generic, sourceProj);
        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Contains("no build artifacts", result.Message);
    }

    #endregion
}
