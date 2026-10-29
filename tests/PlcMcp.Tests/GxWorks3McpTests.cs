using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlcMcp.Adapters;
using PlcMcp.Engineering.Workers.External;
using PlcMcp.Engineering.Workers.GxWorks3;
using PlcMcp.Server.Governance;
using PlcMcp.Server.Mcp;

namespace PlcMcp.Tests;

public sealed class GxWorks3McpTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _mockProbeExe;
    private readonly string _mockGxw3Exe;
    private readonly string _mockProbeSha256;

    public GxWorks3McpTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PlcMcp_GxWorks3Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _mockProbeExe = Path.Combine(_tempDir, "PlcMcp.GxWorks3.Worker.exe");
        File.WriteAllBytes(_mockProbeExe, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x01, 0x02 });
        _mockProbeSha256 = ComputeSha256(_mockProbeExe);

        _mockGxw3Exe = Path.Combine(_tempDir, "GXW3.exe");
        File.WriteAllBytes(_mockGxw3Exe, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x04 });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
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

    private GxWorks3ProbeConfig CreateValidConfig() => new()
    {
        ProbeExePath = _mockProbeExe,
        ProbeExeSha256 = _mockProbeSha256,
        Gxw3ExePath = _mockGxw3Exe,
        ExpectedVersion = "1.128.0.1"
    };

    private static async Task<JsonElement> CallAsync(McpServer server, string tool, object arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } });
        await server.ProcessLineAsync(request, output, error);
        using var doc = JsonDocument.Parse(output.ToString());
        return doc.RootElement.GetProperty("result").Clone();
    }

    [Fact]
    public void DefaultServer_DoesNotExposeGxWorks3Doctor_OrAnyGxWorks3Tools()
    {
        var governance = new ServerGovernanceServices(_tempDir);
        var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);

        Assert.Null(governance.GxWorks3Probe);
        Assert.DoesNotContain(router.ToolDescriptors, t => t.Name == "plc_gxworks3_doctor");
        Assert.DoesNotContain(router.ToolDescriptors, t => t.Name.Contains("gxworks3", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StaticGxWorks3Worker_RemainsUnavailableAndUnsupported()
    {
        var config = new GxWorks3WorkerConfig
        {
            Gxw3ExePath = _mockGxw3Exe,
            InstallationRoot = _tempDir,
            ManagedDirectory = _tempDir,
            Gxw3Version = "1.128.04519"
        };
        var worker = new GxWorks3Worker(config);

        Assert.False(worker.IsAvailable);
    }

    [Fact]
    public void ConfigValidation_FailsClosed_OnMissingOrIncompleteFields()
    {
        var config = new GxWorks3ProbeConfig();
        Assert.Throws<ArgumentException>(() => config.Validate());

        config.ProbeExePath = "relative/path.exe";
        Assert.Throws<ArgumentException>(() => config.Validate());

        config.ProbeExePath = _mockProbeExe;
        Assert.Throws<ArgumentException>(() => config.Validate());

        config.ProbeExeSha256 = _mockProbeSha256;
        Assert.Throws<ArgumentException>(() => config.Validate());

        config.Gxw3ExePath = "relative/gxw3.exe";
        config.ExpectedVersion = "1.128.0.1";
        Assert.Throws<ArgumentException>(() => config.Validate());

        // Tampered sha256
        config.Gxw3ExePath = _mockGxw3Exe;
        config.ProbeExeSha256 = "0000000000000000000000000000000000000000000000000000000000000000";
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void ServerGovernanceServices_ExposesGxWorks3Probe_OnlyWhenConfigProvided()
    {
        var validConfig = CreateValidConfig();
        var governance = new ServerGovernanceServices(_tempDir, gxworks3Config: validConfig);

        Assert.NotNull(governance.GxWorks3Probe);
        Assert.Equal(_mockProbeExe, governance.GxWorks3Probe.Config.ProbeExePath);
    }

    [Fact]
    public void McpToolRouter_RegistersPlc_GxWorks3_Doctor_AsReadOnly_WhenConfigured()
    {
        var validConfig = CreateValidConfig();
        var governance = new ServerGovernanceServices(_tempDir, gxworks3Config: validConfig);
        var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);

        var tool = router.ToolDescriptors.FirstOrDefault(t => t.Name == "plc_gxworks3_doctor");
        Assert.NotNull(tool);
        Assert.True(tool.ReadOnly);
        Assert.False(tool.Destructive);

        // Ensure no inspect/export/compile gxworks3 tools exist
        Assert.DoesNotContain(router.ToolDescriptors, t => t.Name == "plc_gxworks3_inspect");
        Assert.DoesNotContain(router.ToolDescriptors, t => t.Name == "plc_gxworks3_export");
        Assert.DoesNotContain(router.ToolDescriptors, t => t.Name == "plc_gxworks3_compile");
    }

    [Fact]
    public async Task Plc_GxWorks3_Doctor_ExecutesSuccessfully_WithFakeProcessRunner_AndEvidenceVisible()
    {
        var fakeRunner = new MockProcessRunner(input =>
        {
            using var doc = JsonDocument.Parse(input);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;

            if (method == "handshake")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        workerName = "GxWorks3-Metadata-Worker",
                        workerVersion = "1.0",
                        protocolVersion = "1.0",
                        vendor = "Mitsubishi",
                        bitness = "32-bit",
                        runtimeEnvironment = ".NET Framework 4.8 (x86)",
                        capabilities = new[] { "HostDiagnostics", "DependencyMetadata" }
                    }
                });
            }

            if (method == "doctor")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        healthy = true,
                        installed = true,
                        toolchainPath = _mockGxw3Exe,
                        toolchainVersion = "1.128.0.1",
                        bitness = "32-bit",
                        details = "GX Works3 core installation verified in 32-bit isolated diagnostic mode.",
                        checks = new[]
                        {
                            new { name = "EngineeringApiBoundary", passed = true, message = "engineeringApiVerified=false" }
                        },
                        evidence = new
                        {
                            engineeringApiVerified = false,
                            limitation = "ServiceBus, COM, and native/managed engineering controller APIs are not initialized."
                        }
                    }
                });
            }

            if (method == "shutdown")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new { status = "ok" }
                });
            }

            return null;
        });

        var validConfig = CreateValidConfig();
        var probeClient = new GxWorks3ProbeClient(validConfig, fakeRunner);
        var governance = new ServerGovernanceServices(_tempDir, gxworks3ProbeClient: probeClient);
        var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);
        var server = new McpServer(router);

        var result = await CallAsync(server, "plc_gxworks3_doctor", new { });

        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.True(result.GetProperty("healthy").GetBoolean());
        Assert.True(result.GetProperty("installed").GetBoolean());

        var evidence = result.GetProperty("evidence");
        Assert.False(evidence.GetProperty("engineeringApiVerified").GetBoolean());
    }

    [Fact]
    public async Task Plc_GxWorks3_Doctor_ReturnsIsError_WhenDoctorReportsUnhealthy()
    {
        var fakeRunner = new MockProcessRunner(input =>
        {
            using var doc = JsonDocument.Parse(input);
            string id = doc.RootElement.GetProperty("id").GetString()!;
            string method = doc.RootElement.GetProperty("method").GetString()!;

            if (method == "handshake")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        workerName = "GxWorks3-Metadata-Worker",
                        workerVersion = "1.0",
                        protocolVersion = "1.0",
                        vendor = "Mitsubishi",
                        bitness = "32-bit",
                        runtimeEnvironment = ".NET Framework 4.8 (x86)",
                        capabilities = new[] { "HostDiagnostics", "DependencyMetadata" }
                    }
                });
            }

            if (method == "doctor")
            {
                return JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    result = new
                    {
                        healthy = false,
                        installed = false,
                        toolchainPath = _mockGxw3Exe,
                        toolchainVersion = "",
                        bitness = "32-bit",
                        details = "GXW3.exe does not exist or version mismatch.",
                        checks = new[]
                        {
                            new { name = "FileVersionMatch", passed = false, message = "Mismatch" }
                        },
                        evidence = new
                        {
                            engineeringApiVerified = false
                        }
                    }
                });
            }

            return null;
        });

        var validConfig = CreateValidConfig();
        var probeClient = new GxWorks3ProbeClient(validConfig, fakeRunner);
        var governance = new ServerGovernanceServices(_tempDir, gxworks3ProbeClient: probeClient);
        var router = new McpToolRouter(DefaultPlcComposition.Create(), governance);
        var server = new McpServer(router);

        var result = await CallAsync(server, "plc_gxworks3_doctor", new { });

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.False(result.GetProperty("healthy").GetBoolean());
        Assert.False(result.GetProperty("evidence").GetProperty("engineeringApiVerified").GetBoolean());
    }

    #region Fake Process Runner & AsyncPipeStream

    private sealed class MockProcessRunner : IProcessRunner
    {
        private readonly Func<string, string?> _handler;

        public MockProcessRunner(Func<string, string?> handler)
        {
            _handler = handler;
        }

        public IExternalProcess Start(ProcessStartInfo startInfo)
        {
            return new MockExternalProcess(_handler);
        }
    }

    private sealed class MockExternalProcess : IExternalProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AsyncPipeStream _outPipe = new();
        private readonly AsyncPipeStream _errPipe = new();
        private readonly StreamWriter _outWriter;
        private readonly StreamWriter _errWriter;

        public int Id { get; set; } = 12345;
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

        public DelegateWriter(Action<string> onLine) : base(new MemoryStream(), Encoding.UTF8)
        {
            _onLine = onLine;
        }

        public override void WriteLine(string? value)
        {
            if (value != null) _onLine(value);
        }

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            _onLine(buffer.ToString());
            return Task.CompletedTask;
        }
    }

    private sealed class AsyncPipeStream : Stream
    {
        private readonly Queue<byte[]> _chunks = new();
        private readonly object _lock = new();
        private TaskCompletionSource<bool> _dataAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _currentChunk;
        private int _currentOffset;
        private bool _completed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Complete()
        {
            lock (_lock)
            {
                _completed = true;
                _dataAvailable.TrySetResult(true);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count == 0) return;
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            lock (_lock)
            {
                _chunks.Enqueue(copy);
                _dataAvailable.TrySetResult(true);
            }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task waitTask;
                lock (_lock)
                {
                    if (_currentChunk != null && _currentOffset < _currentChunk.Length)
                    {
                        int toCopy = Math.Min(count, _currentChunk.Length - _currentOffset);
                        Buffer.BlockCopy(_currentChunk, _currentOffset, buffer, offset, toCopy);
                        _currentOffset += toCopy;
                        if (_currentOffset >= _currentChunk.Length)
                        {
                            _currentChunk = null;
                            _currentOffset = 0;
                        }
                        return toCopy;
                    }

                    if (_chunks.Count > 0)
                    {
                        _currentChunk = _chunks.Dequeue();
                        _currentOffset = 0;
                        continue;
                    }

                    if (_completed) return 0;

                    _dataAvailable = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    waitTask = _dataAvailable.Task;
                }

                await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    #endregion
}
