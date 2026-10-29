using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Models;
using PlcMcp.Engineering.Workers.External;
using PlcMcp.Engineering.Workspace;

namespace PlcMcp.Engineering.Tests;

public class ExternalWorkerTests : IDisposable
{
    private readonly string _tempWorkspace;
    private readonly string _dummyExePath;

    public ExternalWorkerTests()
    {
        _tempWorkspace = Path.Combine(Path.GetTempPath(), "PlcMcp_ExtWorkerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempWorkspace);

        _dummyExePath = Path.Combine(_tempWorkspace, "mock_worker.exe");
        File.WriteAllBytes(_dummyExePath, new byte[] { 0x4D, 0x5A }); // mock file
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
            // Best effort
        }
    }

    #region Mock Process Runner & Fake Worker Console

    private sealed class MockExternalProcess : IExternalProcess
    {
        private readonly MemoryStream _inStream = new();
        private readonly MemoryStream _outStream = new();
        private readonly MemoryStream _errStream = new();
        private readonly TaskCompletionSource _exitTcs = new();
        private bool _isDisposed;

        public int Id { get; set; } = 12345;
        public bool HasExited => _exitTcs.Task.IsCompleted;
        public int ExitCode { get; set; } = 0;

        public StreamWriter StandardInput { get; }
        public StreamReader StandardOutput { get; }
        public StreamReader StandardError { get; }

        public Func<string, string?>? MessageHandler { get; set; }

        public MockExternalProcess(Func<string, string?>? messageHandler = null)
        {
            MessageHandler = messageHandler;

            var inWriter = new PipeStreamWriter(this);
            StandardInput = inWriter;

            var outPipe = new AsyncPipeStream(this);
            StandardOutput = new StreamReader(outPipe, Encoding.UTF8);

            var errPipe = new AsyncPipeStream(this);
            StandardError = new StreamReader(errPipe, Encoding.UTF8);

            _outPipeWriter = new StreamWriter(outPipe, Encoding.UTF8) { AutoFlush = true };
            _errPipeWriter = new StreamWriter(errPipe, Encoding.UTF8) { AutoFlush = true };
        }

        private readonly StreamWriter _outPipeWriter;
        private readonly StreamWriter _errPipeWriter;

        public void WriteStdoutLine(string line)
        {
            _outPipeWriter.WriteLine(line);
        }

        public void WriteStderrLine(string line)
        {
            _errPipeWriter.WriteLine(line);
        }

        public void SimulateExit(int code)
        {
            ExitCode = code;
            _exitTcs.TrySetResult();
        }

        public void Kill(bool entireProcessTree = true)
        {
            ExitCode = -9;
            _exitTcs.TrySetResult();
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            return _exitTcs.Task;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _exitTcs.TrySetResult();
            _outPipeWriter.Dispose();
            _errPipeWriter.Dispose();
        }

        private sealed class PipeStreamWriter : StreamWriter
        {
            private readonly MockExternalProcess _parent;

            public PipeStreamWriter(MockExternalProcess parent) : base(new MemoryStream())
            {
                _parent = parent;
            }

            public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
            {
                string input = buffer.ToString();
                if (_parent.MessageHandler != null)
                {
                    string? reply = _parent.MessageHandler(input);
                    if (reply != null)
                    {
                        _parent.WriteStdoutLine(reply);
                    }
                }
            }

            public override Task FlushAsync() => Task.CompletedTask;
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class AsyncPipeStream : Stream
        {
            private readonly MockExternalProcess _parent;
            private readonly BlockingCollection<byte[]> _blocks = new();
            private byte[]? _currentBlock;
            private int _currentOffset;

            public AsyncPipeStream(MockExternalProcess parent)
            {
                _parent = parent;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() { }

            public override void Write(byte[] buffer, int offset, int count)
            {
                var copy = new byte[count];
                Buffer.BlockCopy(buffer, offset, copy, 0, count);
                _blocks.Add(copy);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                while (_currentBlock == null || _currentOffset >= _currentBlock.Length)
                {
                    try
                    {
                        if (!_blocks.TryTake(out _currentBlock, 500))
                        {
                            if (_parent.HasExited)
                            {
                                return 0;
                            }
                            continue;
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        return 0;
                    }
                    _currentOffset = 0;
                }

                int toCopy = Math.Min(count, _currentBlock.Length - _currentOffset);
                Buffer.BlockCopy(_currentBlock, _currentOffset, buffer, offset, toCopy);
                _currentOffset += toCopy;
                return toCopy;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }

    private sealed class MockProcessRunner : IProcessRunner
    {
        public ProcessStartInfo? LastStartInfo { get; private set; }
        public MockExternalProcess NextProcess { get; set; }

        public MockProcessRunner(MockExternalProcess? nextProcess = null)
        {
            NextProcess = nextProcess ?? new MockExternalProcess();
        }

        public IExternalProcess Start(ProcessStartInfo startInfo)
        {
            LastStartInfo = startInfo;
            return NextProcess;
        }
    }

    #endregion

    [Fact]
    public void SecurityPolicy_FailClosed_WhenNoExecutableConfigured()
    {
        var policy = new ExternalWorkerSecurityPolicy();
        Assert.Throws<UnauthorizedAccessException>(() => policy.ValidateExecutablePath(_dummyExePath));
    }

    [Fact]
    public void SecurityPolicy_Throws_WhenExecutableNotInAllowlist()
    {
        var policy = new ExternalWorkerSecurityPolicy();
        policy.AllowedExecutablePaths.Add(Path.Combine(_tempWorkspace, "another.exe"));

        Assert.Throws<UnauthorizedAccessException>(() => policy.ValidateExecutablePath(_dummyExePath));
    }

    [Fact]
    public void SecurityPolicy_Throws_WhenWorkspaceRootsDisallowPath()
    {
        var policy = new ExternalWorkerSecurityPolicy();
        policy.AllowedWorkspaceRoots.Add(_tempWorkspace);

        string evilPath = @"C:\Windows\System32\calc.exe";
        Assert.Throws<UnauthorizedAccessException>(() => policy.ValidateAndSanitizePath(evilPath));
    }

    [Fact]
    public void SecurityPolicy_HardRejects_DownloadRunStopForceHmiPublish()
    {
        var forbiddenOps = new[] { "Download", "RunStop", "Force", "HmiPublish", "DownloadProject", "SetRunMode" };
        foreach (var op in forbiddenOps)
        {
            bool rejected = ExternalWorkerSecurityPolicy.IsHardRejectedOperation(op, null, out string reason);
            Assert.True(rejected);
            Assert.Contains("HARD REJECTED", reason);
        }

        var dangerousOptions = new Dictionary<string, string>
        {
            ["allow_download"] = "true"
        };
        bool rejectedOpt = ExternalWorkerSecurityPolicy.IsHardRejectedOperation("Inspect", dangerousOptions, out string optReason);
        Assert.True(rejectedOpt);
        Assert.Contains("hard rejected", optReason);
    }

    [Fact]
    public async Task ExternalWorkerClient_DoesNotUseShell_AndPassesArgumentsDirectly()
    {
        var mockRunner = new MockProcessRunner();
        var policy = new ExternalWorkerSecurityPolicy();
        policy.AllowedExecutablePaths.Add(_dummyExePath);

        var args = new[] { "--worker-mode", "--vendor", "CODESYS" };
        var client = new ExternalWorkerClient(_dummyExePath, args, policy, mockRunner);

        await client.StartAsync();

        Assert.NotNull(mockRunner.LastStartInfo);
        Assert.False(mockRunner.LastStartInfo.UseShellExecute);
        Assert.True(mockRunner.LastStartInfo.CreateNoWindow);
        Assert.Equal(3, mockRunner.LastStartInfo.ArgumentList.Count);
        Assert.Equal("--worker-mode", mockRunner.LastStartInfo.ArgumentList[0]);
        Assert.Equal("--vendor", mockRunner.LastStartInfo.ArgumentList[1]);
        Assert.Equal("CODESYS", mockRunner.LastStartInfo.ArgumentList[2]);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task ExternalWorkerClient_HandshakeAndDoctor_SucceedsWithFakeWorker()
    {
        var mockProc = new MockExternalProcess(input =>
        {
            using var doc = JsonDocument.Parse(input);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;

            if (method == "handshake")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerHandshakeResponse(
                        WorkerName: "Fake-Codesys-Worker",
                        WorkerVersion: "1.0.0",
                        ProtocolVersion: "1.0",
                        Vendor: "CODESYS",
                        Bitness: "64-bit",
                        RuntimeEnvironment: "Mock-CLI",
                        Capabilities: new[] { "InspectProject", "ExportPou", "CompileProject" },
                        CapabilityEvidence: new Dictionary<string, string> { ["Evidence"] = "Simulation-Only" })
                });
            }
            if (method == "doctor")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerDoctorReport(
                        Healthy: true,
                        Installed: true,
                        ToolchainPath: @"C:\Tools\Codesys.exe",
                        ToolchainVersion: "3.5.19",
                        Bitness: "64-bit",
                        Details: "Doctor check passed in mock test.")
                });
            }
            return null;
        });

        var mockRunner = new MockProcessRunner(mockProc);
        var policy = new ExternalWorkerSecurityPolicy();
        policy.AllowedExecutablePaths.Add(_dummyExePath);

        await using var client = new ExternalWorkerClient(_dummyExePath, null, policy, mockRunner);
        await client.StartAsync();

        var handshake = await client.HandshakeAsync();
        Assert.Equal("Fake-Codesys-Worker", handshake.WorkerName);
        Assert.Equal("64-bit", handshake.Bitness);
        Assert.Contains("CompileProject", handshake.Capabilities);

        var doctor = await client.DoctorAsync();
        Assert.True(doctor.Healthy);
        Assert.Equal("64-bit", doctor.Bitness);
    }

    [Fact]
    public async Task ExternalWorkerClient_SubmitJob_HardRejectsDangerousOperation_WithoutSendingToProcess()
    {
        var mockProc = new MockExternalProcess();
        var mockRunner = new MockProcessRunner(mockProc);
        var policy = new ExternalWorkerSecurityPolicy();
        policy.AllowedExecutablePaths.Add(_dummyExePath);
        policy.AllowedWorkspaceRoots.Add(_tempWorkspace);

        await using var client = new ExternalWorkerClient(_dummyExePath, null, policy, mockRunner);
        await client.StartAsync();

        string dummyProj = Path.Combine(_tempWorkspace, "proj.project");
        File.WriteAllText(dummyProj, "test");

        var submitReq = new WorkerSubmitJobRequest(
            JobId: "job-1",
            Operation: "DownloadProject",
            ProjectPath: dummyProj);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SubmitJobAsync(submitReq));
        Assert.Contains("HARD REJECTED", ex.Message);
    }

    [Fact]
    public async Task ExternalWorkerClient_Timeout_KillsProcessAndThrowsTimeoutException()
    {
        // Worker never answers
        var mockProc = new MockExternalProcess(input => null);
        var mockRunner = new MockProcessRunner(mockProc);
        var policy = new ExternalWorkerSecurityPolicy
        {
            RpcTimeout = TimeSpan.FromMilliseconds(200)
        };
        policy.AllowedExecutablePaths.Add(_dummyExePath);

        await using var client = new ExternalWorkerClient(_dummyExePath, null, policy, mockRunner);
        await client.StartAsync();

        await Assert.ThrowsAsync<TimeoutException>(() => client.HandshakeAsync());
    }

    [Fact]
    public void VendorProfileDetectors_ReportUnsupported_WhenSoftwareNotInstalled()
    {
        var registry = new VendorProfileDetectorRegistry();
        var profiles = registry.DetectAll();

        // 5 vendors: CODESYS, TIA, GX, Sysmac, InoProShop
        Assert.Equal(5, profiles.Count);

        foreach (var profile in profiles)
        {
            Assert.NotNull(profile.VendorName);
            Assert.NotNull(profile.ToolchainName);
            Assert.Contains("does not imply valid vendor license", profile.Disclaimer);

            // Verify dangerous capabilities are strictly unsupported
            Assert.Equal(CapabilityStatus.Unsupported, profile.Capabilities.GetStatus("DownloadProject"));
            Assert.Equal(CapabilityStatus.Unsupported, profile.Capabilities.GetStatus("SetRunMode"));
            Assert.Equal(CapabilityStatus.Unsupported, profile.Capabilities.GetStatus("ForceIo"));
            Assert.Equal(CapabilityStatus.Unsupported, profile.Capabilities.GetStatus("HmiPublish"));

            if (!profile.Installed)
            {
                Assert.Equal(CapabilityStatus.Unsupported, profile.Capabilities.GetStatus("InspectProject"));
                Assert.Contains("NOT installed", profile.Details);
            }
        }
    }

    [Fact]
    public async Task ExternalEngineeringWorker_ExecuteAsync_FullInspectionFlow_WithMockWorker()
    {
        string dummyProj = Path.Combine(_tempWorkspace, "demo.project");
        File.WriteAllText(dummyProj, "CODESYS-PROJECT-DUMMY-DATA");

        var mockDetector = new FakeInstalledDetector(PlcVendor.Generic, "CODESYS", "CODESYS Development System V3");
        var wsManager = new ProjectWorkspaceManager(_tempWorkspace);

        var mockProc = new MockExternalProcess(input =>
        {
            using var doc = JsonDocument.Parse(input);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;

            if (method == "handshake")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerHandshakeResponse(
                        WorkerName: "Codesys-External-Worker",
                        WorkerVersion: "1.0",
                        ProtocolVersion: "1.0",
                        Vendor: "CODESYS",
                        Bitness: "64-bit",
                        RuntimeEnvironment: "Mock",
                        Capabilities: new[] { "InspectProject", "ExportPou" })
                });
            }
            if (method == "submit")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerJobStatusResponse("job-42", "completed", 1.0, "Success", 0)
                });
            }
            if (method == "artifacts")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerArtifactsResponse("job-42", new[]
                    {
                        new WorkerArtifactDescriptor("PLC_PRG.st", "export/PLC_PRG.st", "ST", 100, "dummy-sha")
                    })
                });
            }
            return null;
        });

        var mockRunner = new MockProcessRunner(mockProc);

        var config = new ExternalEngineeringWorkerConfig
        {
            ExecutablePath = _dummyExePath,
            AllowedExecutablePaths = new() { _dummyExePath },
            AllowedWorkspaceRoots = new() { _tempWorkspace },
            TimeoutSeconds = 10
        };

        var worker = new ExternalEngineeringWorker(
            PlcVendor.Generic,
            "CodesysWorker",
            config,
            mockDetector,
            wsManager,
            mockRunner);

        Assert.True(worker.IsAvailable);

        var job = new EngineeringJobRequest(
            JobId: "job-42",
            JobType: EngineeringJobType.InspectProject,
            Vendor: PlcVendor.Generic,
            ProjectPath: dummyProj);

        var result = await worker.ExecuteAsync(job);

        // Fake worker reporting completed with fictitious unverified artifact fails and never promoted to Supported
        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Experimental, result.Status);
        Assert.Contains("Artifact validation failed", result.Message);
    }

    [Fact]
    public async Task ExternalEngineeringWorker_RefusesProhibitedOperation_Download()
    {
        string dummyProj = Path.Combine(_tempWorkspace, "demo.project");
        File.WriteAllText(dummyProj, "TEST");

        var mockDetector = new FakeInstalledDetector(PlcVendor.Siemens, "Siemens", "Siemens TIA Portal Openness");
        var wsManager = new ProjectWorkspaceManager(_tempWorkspace);

        var config = new ExternalEngineeringWorkerConfig
        {
            ExecutablePath = _dummyExePath,
            AllowedExecutablePaths = new() { _dummyExePath },
            AllowedWorkspaceRoots = new() { _tempWorkspace }
        };

        var worker = new ExternalEngineeringWorker(
            PlcVendor.Siemens,
            "TiaWorker",
            config,
            mockDetector,
            wsManager);

        var dangerousJob = new EngineeringJobRequest(
            JobId: "job-bad",
            JobType: EngineeringJobType.InspectProject,
            Vendor: PlcVendor.Siemens,
            ProjectPath: dummyProj,
            Options: new Dictionary<string, string> { ["operation"] = "download_to_plc" });

        var result = await worker.ExecuteAsync(dangerousJob);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("refused by safety policy", result.Message);
    }

    [Fact]
    public async Task ExternalWorkerClient_KillsProcess_WhenOutputBytesExceedQuota()
    {
        var mockProc = new MockExternalProcess(input =>
        {
            // Return a huge payload exceeding small quota
            return new string('X', 2000);
        });
        var mockRunner = new MockProcessRunner(mockProc);
        var policy = new ExternalWorkerSecurityPolicy
        {
            MaxOutputBytes = 500,
            RpcTimeout = TimeSpan.FromSeconds(2)
        };
        policy.AllowedExecutablePaths.Add(_dummyExePath);

        await using var client = new ExternalWorkerClient(_dummyExePath, null, policy, mockRunner);
        await client.StartAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => client.HandshakeAsync());
        Assert.True(mockProc.HasExited);
        Assert.Contains("MaxOutputBytes exceeded", client.StderrTail);
    }

    [Fact]
    public async Task ExternalWorkerClient_CollectsStderrTail_AndExitCode()
    {
        MockExternalProcess? procRef = null;
        var mockProc = new MockExternalProcess(input =>
        {
            using var doc = JsonDocument.Parse(input);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            procRef?.WriteStderrLine("Worker log warning: experimental parser loaded.");
            return JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = id,
                result = new WorkerHandshakeResponse("TestWorker", "1.0", "1.0", "Generic", "64-bit", "CLI", new[] { "Inspect" })
            });
        });
        procRef = mockProc;

        var mockRunner = new MockProcessRunner(mockProc);
        var policy = new ExternalWorkerSecurityPolicy();
        policy.AllowedExecutablePaths.Add(_dummyExePath);

        await using var client = new ExternalWorkerClient(_dummyExePath, null, policy, mockRunner);
        await client.StartAsync();

        await client.HandshakeAsync();
        Assert.Contains("Worker log warning", client.StderrTail);

        mockProc.SimulateExit(42);
        Assert.Equal(42, client.ExitCode);
    }

    [Fact]
    public async Task ExternalWorkerClient_CancelJob_WorksProperly()
    {
        var mockProc = new MockExternalProcess(input =>
        {
            using var doc = JsonDocument.Parse(input);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;

            if (method == "cancel")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new { cancelled = true, jobId = "job-99" }
                });
            }
            return null;
        });

        var mockRunner = new MockProcessRunner(mockProc);
        var policy = new ExternalWorkerSecurityPolicy();
        policy.AllowedExecutablePaths.Add(_dummyExePath);

        await using var client = new ExternalWorkerClient(_dummyExePath, null, policy, mockRunner);
        await client.StartAsync();

        bool cancelled = await client.CancelJobAsync("job-99");
        Assert.True(cancelled);
    }

    [Fact]
    public async Task ExternalWorkerClient_RealPythonFixture_EndToEnd()
    {
        string? pythonExe = FindPythonExecutable();
        if (pythonExe == null)
        {
            // Skip if python is unexpectedly not present
            return;
        }

        string scriptPath = Path.Combine(_tempWorkspace, "fake_worker_fixture.py");
        string pythonScript = @"
import sys
import json

for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        req = json.loads(line)
    except Exception:
        continue
    req_id = req.get('id')
    method = req.get('method')
    params = req.get('params') or {}

    if method == 'handshake':
        resp = {
            'jsonrpc': '2.0',
            'id': req_id,
            'result': {
                'workerName': 'Python-Fake-Worker',
                'workerVersion': '1.0.0-mock',
                'protocolVersion': '1.0',
                'vendor': 'CODESYS',
                'bitness': '64-bit',
                'runtimeEnvironment': 'Python',
                'capabilities': ['InspectProject', 'ExportPou', 'CompileProject'],
                'capabilityEvidence': {'source': 'fixture'}
            }
        }
        sys.stdout.write(json.dumps(resp) + '\n')
        sys.stdout.flush()
    elif method == 'doctor':
        resp = {
            'jsonrpc': '2.0',
            'id': req_id,
            'result': {
                'healthy': True,
                'installed': True,
                'toolchainPath': 'C:\\fake\\tool.exe',
                'toolchainVersion': '1.0',
                'bitness': '64-bit',
                'details': 'Python fake doctor ok',
                'checks': [{'name': 'Interpreter', 'passed': True, 'message': 'OK'}]
            }
        }
        sys.stdout.write(json.dumps(resp) + '\n')
        sys.stdout.flush()
    elif method == 'submit':
        op = params.get('operation')
        jid = params.get('jobId')
        sys.stderr.write(f'Running job {jid} op {op}\n')
        sys.stderr.flush()
        resp = {
            'jsonrpc': '2.0',
            'id': req_id,
            'result': {
                'jobId': jid,
                'state': 'completed',
                'progress': 1.0,
                'message': 'Success from python fixture',
                'exitCode': 0
            }
        }
        sys.stdout.write(json.dumps(resp) + '\n')
        sys.stdout.flush()
    elif method == 'artifacts':
        jid = params.get('jobId')
        resp = {
            'jsonrpc': '2.0',
            'id': req_id,
            'result': {
                'jobId': jid,
                'artifacts': [{
                    'name': 'result.st',
                    'relativePath': 'out/result.st',
                    'artifactType': 'ST',
                    'sizeBytes': 42,
                    'sha256': 'mocksha'
                }]
            }
        }
        sys.stdout.write(json.dumps(resp) + '\n')
        sys.stdout.flush()
    elif method == 'cancel':
        resp = {
            'jsonrpc': '2.0',
            'id': req_id,
            'result': {'cancelled': True}
        }
        sys.stdout.write(json.dumps(resp) + '\n')
        sys.stdout.flush()
";
        File.WriteAllText(scriptPath, pythonScript.Trim());

        string dummyProj = Path.Combine(_tempWorkspace, "sample.project");
        File.WriteAllText(dummyProj, "MOCK-PLC-CODE");

        var policy = new ExternalWorkerSecurityPolicy();
        policy.AllowedExecutablePaths.Add(pythonExe);
        policy.AllowedWorkspaceRoots.Add(_tempWorkspace);

        await using var client = new ExternalWorkerClient(
            pythonExe,
            new[] { scriptPath },
            policy);

        await client.StartAsync();
        Assert.True(client.IsRunning);

        // 1. Handshake
        var handshake = await client.HandshakeAsync();
        Assert.Equal("Python-Fake-Worker", handshake.WorkerName);
        Assert.Equal("64-bit", handshake.Bitness);
        Assert.NotNull(handshake.CapabilityEvidence);

        // 2. Doctor
        var doc = await client.DoctorAsync();
        Assert.True(doc.Healthy);
        Assert.Equal("64-bit", doc.Bitness);

        // 3. Submit
        var submitResp = await client.SubmitJobAsync(new WorkerSubmitJobRequest(
            JobId: "py-job-1",
            Operation: "InspectProject",
            ProjectPath: dummyProj));
        Assert.Equal("completed", submitResp.State);

        // 4. Artifacts
        var arts = await client.GetArtifactsAsync("py-job-1");
        Assert.Single(arts.Artifacts);
        Assert.Equal("result.st", arts.Artifacts[0].Name);

        // 5. Cancel
        bool cancelled = await client.CancelJobAsync("py-job-1");
        Assert.True(cancelled);

        // 6. Hard rejection of dangerous operation
        var badReq = new WorkerSubmitJobRequest(
            JobId: "py-bad-1",
            Operation: "RunStop",
            ProjectPath: dummyProj);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SubmitJobAsync(badReq));
        Assert.Contains("HARD REJECTED", ex.Message);
    }

    [Fact]
    public async Task ExternalEngineeringWorker_WhenToolchainNotInstalled_ReturnsUnsupported()
    {
        // Use an explicitly uninstalled detector: on this host CODESYS may genuinely be
        // present, and a real detector would then correctly report Installed.
        var missingDetector = new FakeMissingDetector(PlcVendor.Generic, "CODESYS", "CODESYS Development System V3");
        var wsManager = new ProjectWorkspaceManager(_tempWorkspace);
        string dummyProj = Path.Combine(_tempWorkspace, "demo.project");
        File.WriteAllText(dummyProj, "TEST");

        var config = new ExternalEngineeringWorkerConfig
        {
            ExecutablePath = _dummyExePath,
            AllowedExecutablePaths = new() { _dummyExePath },
            AllowedWorkspaceRoots = new() { _tempWorkspace }
        };

        var worker = new ExternalEngineeringWorker(
            PlcVendor.Generic,
            "RealCodesysWorker",
            config,
            missingDetector,
            wsManager);

        var job = new EngineeringJobRequest(
            JobId: "job-uninstalled",
            JobType: EngineeringJobType.InspectProject,
            Vendor: PlcVendor.Generic,
            ProjectPath: dummyProj);

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("not installed or detected", result.Message);
    }

    [Fact]
    public async Task ExternalEngineeringWorker_FakeWorker_ReturnsCompletedWithoutFiles_CompileProject_Fails()
    {
        string dummyProj = Path.Combine(_tempWorkspace, "demo.project");
        File.WriteAllText(dummyProj, "TEST-PROJECT");

        var mockDetector = new FakeInstalledDetector(PlcVendor.Generic, "CODESYS", "CODESYS V3");
        var wsManager = new ProjectWorkspaceManager(_tempWorkspace);

        var mockProc = new MockExternalProcess(input =>
        {
            using var doc = JsonDocument.Parse(input);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;

            if (method == "handshake")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerHandshakeResponse("FakeWorker", "1.0", "1.0", "CODESYS", "64-bit", "Mock", new[] { "CompileProject" })
                });
            }
            if (method == "submit")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerJobStatusResponse("job-compile", "completed", 1.0, "Worker says all good", 0)
                });
            }
            if (method == "artifacts")
            {
                // Returns 0 artifacts
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerArtifactsResponse("job-compile", Array.Empty<WorkerArtifactDescriptor>())
                });
            }
            return null;
        });

        var config = new ExternalEngineeringWorkerConfig
        {
            ExecutablePath = _dummyExePath,
            AllowedExecutablePaths = new() { _dummyExePath },
            AllowedWorkspaceRoots = new() { _tempWorkspace }
        };

        var worker = new ExternalEngineeringWorker(
            PlcVendor.Generic,
            "CodesysWorker",
            config,
            mockDetector,
            wsManager,
            new MockProcessRunner(mockProc));

        var job = new EngineeringJobRequest(
            JobId: "job-compile",
            JobType: EngineeringJobType.CompileProject,
            Vendor: PlcVendor.Generic,
            ProjectPath: dummyProj);

        var result = await worker.ExecuteAsync(job);

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Experimental, result.Status);
        Assert.Contains("Compilation failed", result.Message);
        Assert.Contains("0 artifacts", result.Details);
    }

    [Fact]
    public async Task ExternalEngineeringWorker_InvalidArtifactPath_OrHashMismatch_Fails()
    {
        string dummyProj = Path.Combine(_tempWorkspace, "demo.project");
        File.WriteAllText(dummyProj, "TEST-PROJECT");

        var mockDetector = new FakeInstalledDetector(PlcVendor.Generic, "CODESYS", "CODESYS V3");
        var wsManager = new ProjectWorkspaceManager(_tempWorkspace);

        // Case A: Traversal path escapes working directory
        var mockProcA = new MockExternalProcess(input =>
        {
            using var doc = JsonDocument.Parse(input);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;

            if (method == "handshake")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerHandshakeResponse("WorkerA", "1.0", "1.0", "CODESYS", "64-bit", "Mock", new[] { "InspectProject" })
                });
            }
            if (method == "submit")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerJobStatusResponse("job-esc", "completed", 1.0, "OK", 0)
                });
            }
            if (method == "artifacts")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerArtifactsResponse("job-esc", new[]
                    {
                        new WorkerArtifactDescriptor("evil", "../../outside.txt", "TXT", 10, "dummy")
                    })
                });
            }
            return null;
        });

        var configA = new ExternalEngineeringWorkerConfig
        {
            ExecutablePath = _dummyExePath,
            AllowedExecutablePaths = new() { _dummyExePath },
            AllowedWorkspaceRoots = new() { _tempWorkspace }
        };

        var workerA = new ExternalEngineeringWorker(
            PlcVendor.Generic,
            "WorkerA",
            configA,
            mockDetector,
            wsManager,
            new MockProcessRunner(mockProcA));

        var jobA = new EngineeringJobRequest(
            JobId: "job-esc",
            JobType: EngineeringJobType.InspectProject,
            Vendor: PlcVendor.Generic,
            ProjectPath: dummyProj);

        var resultA = await workerA.ExecuteAsync(jobA);
        Assert.False(resultA.Success);
        Assert.Equal(CapabilityStatus.Experimental, resultA.Status);
        Assert.Contains("escapes constrained working directory", resultA.Message);

        // Case B: File exists but SHA256 mismatch
        var mockProcB = new MockExternalProcess(input =>
        {
            using var doc = JsonDocument.Parse(input);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;

            if (method == "handshake")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerHandshakeResponse("WorkerB", "1.0", "1.0", "CODESYS", "64-bit", "Mock", new[] { "InspectProject" })
                });
            }
            if (method == "submit")
            {
                string lastSubmittedWorkPath = doc.RootElement.GetProperty("params").GetProperty("projectPath").GetString()!;
                string workDir = Path.GetDirectoryName(lastSubmittedWorkPath)!;
                string artifactDir = Path.Combine(workDir, "export");
                Directory.CreateDirectory(artifactDir);
                File.WriteAllText(Path.Combine(artifactDir, "out.st"), "REAL-ARTIFACT-CONTENT");

                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerJobStatusResponse("job-hash", "completed", 1.0, "OK", 0)
                });
            }
            if (method == "artifacts")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerArtifactsResponse("job-hash", new[]
                    {
                        new WorkerArtifactDescriptor("out.st", "export/out.st", "ST", 21, "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")
                    })
                });
            }
            return null;
        });

        var configB = new ExternalEngineeringWorkerConfig
        {
            ExecutablePath = _dummyExePath,
            AllowedExecutablePaths = new() { _dummyExePath },
            AllowedWorkspaceRoots = new() { _tempWorkspace }
        };

        var workerB = new ExternalEngineeringWorker(
            PlcVendor.Generic,
            "WorkerB",
            configB,
            mockDetector,
            wsManager,
            new MockProcessRunner(mockProcB));

        var resultB = await workerB.ExecuteAsync(jobA);
        Assert.False(resultB.Success);
        Assert.Equal(CapabilityStatus.Experimental, resultB.Status);
        Assert.Contains("integrity check failed", resultB.Message);
    }

    [Fact]
    public async Task ExternalEngineeringWorker_LegitimateArtifact_ReturnsExperimentalOnly_NeverSupported()
    {
        string dummyProj = Path.Combine(_tempWorkspace, "demo.project");
        File.WriteAllText(dummyProj, "TEST-PROJECT");

        var mockDetector = new FakeInstalledDetector(PlcVendor.Generic, "CODESYS", "CODESYS V3");
        var wsManager = new ProjectWorkspaceManager(_tempWorkspace);

        byte[] realContent = Encoding.UTF8.GetBytes("PROGRAM Main\nVAR\nEND_VAR\nEND_PROGRAM");
        string realSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(realContent)).ToLowerInvariant();

        var mockProc = new MockExternalProcess(input =>
        {
            using var doc = JsonDocument.Parse(input);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;

            if (method == "handshake")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerHandshakeResponse("LegitWorker", "1.2.3", "1.0", "CODESYS", "64-bit", "Mock", new[] { "InspectProject" })
                });
            }
            if (method == "submit")
            {
                string workPath = doc.RootElement.GetProperty("params").GetProperty("projectPath").GetString()!;
                string workDir = Path.GetDirectoryName(workPath)!;
                string exportDir = Path.Combine(workDir, "artifacts");
                Directory.CreateDirectory(exportDir);
                File.WriteAllBytes(Path.Combine(exportDir, "Main.st"), realContent);

                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerJobStatusResponse("job-legit", "completed", 1.0, "OK", 0)
                });
            }
            if (method == "artifacts")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerArtifactsResponse("job-legit", new[]
                    {
                        new WorkerArtifactDescriptor("Main.st", "artifacts/Main.st", "ST", realContent.Length, realSha)
                    })
                });
            }
            return null;
        });

        var config = new ExternalEngineeringWorkerConfig
        {
            ExecutablePath = _dummyExePath,
            AllowedExecutablePaths = new() { _dummyExePath },
            AllowedWorkspaceRoots = new() { _tempWorkspace },
            ExpectedWorkerName = "LegitWorker",
            ExpectedWorkerVersion = "1.2.3",
            ExpectedProtocolVersion = "1.0",
            RequirePinnedIdentity = true
        };

        var worker = new ExternalEngineeringWorker(
            PlcVendor.Generic,
            "LegitWorker",
            config,
            mockDetector,
            wsManager,
            new MockProcessRunner(mockProc));

        var job = new EngineeringJobRequest(
            JobId: "job-legit",
            JobType: EngineeringJobType.InspectProject,
            Vendor: PlcVendor.Generic,
            ProjectPath: dummyProj);

        var result = await worker.ExecuteAsync(job);

        Assert.True(result.Success);
        // CRITICAL SECURITY ASSERTION: Self-reported completion must NEVER be promoted to Supported!
        Assert.Equal(CapabilityStatus.Experimental, result.Status);
        Assert.NotEqual(CapabilityStatus.Supported, result.Status);
        Assert.NotNull(result.ExportedOutputs);
        Assert.True(result.ExportedOutputs.ContainsKey("Main.st"));
        Assert.True(File.Exists(result.ExportedOutputs["Main.st"]));
        Assert.Contains("TrustLevel=Experimental", result.Details);
    }

    [Fact]
    public async Task ExternalEngineeringWorker_IdentityPinning_MismatchedOrUnpinned_FailsClosed()
    {
        string dummyProj = Path.Combine(_tempWorkspace, "demo.project");
        File.WriteAllText(dummyProj, "TEST-PROJECT");

        var mockDetector = new FakeInstalledDetector(PlcVendor.Generic, "CODESYS", "CODESYS V3");
        var wsManager = new ProjectWorkspaceManager(_tempWorkspace);

        Func<MockExternalProcess> createProc = () => new MockExternalProcess(input =>
        {
            using var doc = JsonDocument.Parse(input);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;

            if (method == "handshake")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id,
                    result = new WorkerHandshakeResponse("RogueWorker", "9.9.9", "1.0", "CODESYS", "64-bit", "Mock", new[] { "InspectProject" })
                });
            }
            return null;
        });

        // 1. Mismatched worker name fails closed
        var configMismatch = new ExternalEngineeringWorkerConfig
        {
            ExecutablePath = _dummyExePath,
            AllowedExecutablePaths = new() { _dummyExePath },
            AllowedWorkspaceRoots = new() { _tempWorkspace },
            ExpectedWorkerName = "ExpectedWorkerName",
            RequirePinnedIdentity = true
        };

        var workerMismatch = new ExternalEngineeringWorker(
            PlcVendor.Generic,
            "WorkerMismatch",
            configMismatch,
            mockDetector,
            wsManager,
            new MockProcessRunner(createProc()));

        var job = new EngineeringJobRequest(
            JobId: "job-id-test",
            JobType: EngineeringJobType.InspectProject,
            Vendor: PlcVendor.Generic,
            ProjectPath: dummyProj);

        var resultMismatch = await workerMismatch.ExecuteAsync(job);
        Assert.False(resultMismatch.Success);
        Assert.Equal(CapabilityStatus.Experimental, resultMismatch.Status);
        Assert.Contains("Worker identity verification failed", resultMismatch.Message);

        // 2. Unpinned configuration with RequirePinnedIdentity=true fails closed
        var configUnpinned = new ExternalEngineeringWorkerConfig
        {
            ExecutablePath = _dummyExePath,
            AllowedExecutablePaths = new() { _dummyExePath },
            AllowedWorkspaceRoots = new() { _tempWorkspace },
            RequirePinnedIdentity = true
        };

        var workerUnpinned = new ExternalEngineeringWorker(
            PlcVendor.Generic,
            "WorkerUnpinned",
            configUnpinned,
            mockDetector,
            wsManager,
            new MockProcessRunner(createProc()));

        var resultUnpinned = await workerUnpinned.ExecuteAsync(job);
        Assert.False(resultUnpinned.Success);
        Assert.Equal(CapabilityStatus.Experimental, resultUnpinned.Status);
        Assert.Contains("Worker identity is not pinned", resultUnpinned.Message);
    }

    [Fact]
    public void SecurityPolicy_RejectsBroadWorkspaceRoots()
    {
        string rootDir = Path.GetPathRoot(Environment.CurrentDirectory)!;
        Assert.Throws<UnauthorizedAccessException>(() => ExternalWorkerSecurityPolicy.ValidateWorkspaceRoot(rootDir));

        string tempRoot = Path.GetTempPath();
        Assert.Throws<UnauthorizedAccessException>(() => ExternalWorkerSecurityPolicy.ValidateWorkspaceRoot(tempRoot));

        var config = new ExternalEngineeringWorkerConfig
        {
            ExecutablePath = _dummyExePath,
            AllowedExecutablePaths = new() { _dummyExePath },
            AllowedWorkspaceRoots = new() { rootDir }
        };

        var mockDetector = new FakeInstalledDetector(PlcVendor.Generic, "CODESYS", "CODESYS V3");
        var wsManager = new ProjectWorkspaceManager(_tempWorkspace);

        Assert.Throws<UnauthorizedAccessException>(() =>
            new ExternalEngineeringWorker(PlcVendor.Generic, "Test", config, mockDetector, wsManager));
    }

    private static string? FindPythonExecutable()
    {
        var candidates = new[]
        {
            @"C:\Users\ASUS\AppData\Local\hermes\hermes-agent\venv\Scripts\python.exe",
            @"C:\Users\ASUS\AppData\Local\Programs\Python\Python313\python.exe"
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return Path.GetFullPath(c);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = Path.Combine(dir, "python.exe");
                if (File.Exists(p)) return Path.GetFullPath(p);
            }
        }
        return null;
    }

    private sealed class FakeMissingDetector : IVendorProfileDetector
    {
        public PlcVendor Vendor { get; }
        public string VendorName { get; }
        public string ToolchainName { get; }

        public FakeMissingDetector(PlcVendor vendor, string vendorName, string toolchainName)
        {
            Vendor = vendor;
            VendorName = vendorName;
            ToolchainName = toolchainName;
        }

        public ExternalVendorProfile DetectProfile() => new(
            Vendor,
            VendorName,
            ToolchainName,
            Installed: false,
            ExecutablePath: null,
            Version: null,
            Bitness: null,
            Capabilities: new CapabilitySet(new List<CapabilityDescriptor>()),
            Details: "CODESYS is not installed or detected in this isolated test.");
    }

    private sealed class FakeInstalledDetector : IVendorProfileDetector
    {
        public PlcVendor Vendor { get; }
        public string VendorName { get; }
        public string ToolchainName { get; }

        public FakeInstalledDetector(PlcVendor vendor, string vendorName, string toolchainName)
        {
            Vendor = vendor;
            VendorName = vendorName;
            ToolchainName = toolchainName;
        }

        public ExternalVendorProfile DetectProfile()
        {
            return new ExternalVendorProfile(
                Vendor: Vendor,
                VendorName: VendorName,
                ToolchainName: ToolchainName,
                Installed: true,
                ExecutablePath: @"C:\Mock\Tool.exe",
                Version: "1.0.0",
                Bitness: "64-bit",
                Capabilities: new CapabilitySet(new List<CapabilityDescriptor>
                {
                    new("InspectProject", CapabilityStatus.Supported, "Supported in mock"),
                    new("DownloadProject", CapabilityStatus.Unsupported, "Forbidden")
                }),
                Details: "Mock detector installed");
        }
    }
}
