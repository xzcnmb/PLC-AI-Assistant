using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Workers.AutoShop.NativeBridge;

namespace PlcMcp.Engineering.Tests;

/// <summary>
/// Tests for the AutoShop native bridge (M1-M4): the read-only JSON-line
/// protocol, the named-pipe client against a mock pipe server, the services
/// facade, and the worker's safety posture. All tests run offline against
/// loopback named pipes; no AutoShop installation or PLC is required.
/// </summary>
public class NativeBridgeTests : IDisposable
{
    private readonly List<MockNativeBridgeServer> _servers = new();

    public void Dispose()
    {
        foreach (MockNativeBridgeServer server in _servers)
        {
            server.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------
    // Protocol model
    // ---------------------------------------------------------------

    [Fact]
    public void Protocol_AllowsOnlyReadOnlyMethods()
    {
        Assert.Contains(NativeBridgeProtocol.MethodHandshake, NativeBridgeProtocol.AllowedMethods);
        Assert.Contains(NativeBridgeProtocol.MethodCapabilities, NativeBridgeProtocol.AllowedMethods);
        Assert.Contains(NativeBridgeProtocol.MethodReadStatus, NativeBridgeProtocol.AllowedMethods);
        Assert.Contains(NativeBridgeProtocol.MethodReadElements, NativeBridgeProtocol.AllowedMethods);
        Assert.Contains(NativeBridgeProtocol.MethodShutdown, NativeBridgeProtocol.AllowedMethods);

        // No write/force/download/compile/run-stop method exists at all.
        foreach (string forbidden in new[] { "write", "force", "download", "compile", "run", "stop", "set" })
        {
            Assert.DoesNotContain(NativeBridgeProtocol.AllowedMethods, m => m.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Request_SerializesToNativeWireShape()
    {
        var request = new NativeBridgeRequest("7", NativeBridgeProtocol.MethodReadElements,
            new NativeBridgeRequestParams(Addresses: "D100,D101"));
        string json = JsonSerializer.Serialize(request);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("7", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("read_elements", doc.RootElement.GetProperty("method").GetString());
        Assert.Equal("D100,D101", doc.RootElement.GetProperty("params").GetProperty("addresses").GetString());
    }

    [Fact]
    public void Response_ParsesHandshakeResult()
    {
        // Exact shape answered by the native side (see BridgePipeServer.cpp).
        const string wire =
            "{\"id\":\"1\",\"result\":{\"workerName\":\"PlcMcpAutoShopNativeBridge\",\"workerVersion\":\"1.0.0\"," +
            "\"protocolVersion\":\"1.0\",\"vendor\":\"Inovance\",\"bitness\":\"x86\"," +
            "\"runtimeEnvironment\":\"in-process plugin\"," +
            "\"capabilities\":[\"environment_inspection\",\"comm_stack_read_only\"]," +
            "\"disclaimer\":\"reverse-engineered; no vendor endorsement; version-locked 4.12.0.2\"}}";
        NativeBridgeResponse? response = JsonSerializer.Deserialize<NativeBridgeResponse>(wire);

        Assert.NotNull(response);
        Assert.NotNull(response.Result);
        Assert.Equal("PlcMcpAutoShopNativeBridge", response.Result!.WorkerName);
        Assert.Equal("1.0", response.Result!.ProtocolVersion);
        Assert.Equal("x86", response.Result!.Bitness);
        Assert.Equal(new[] { "environment_inspection", "comm_stack_read_only" }, response.Result!.CapabilityList);
    }

    [Fact]
    public void Response_ParsesReadStatusResult()
    {
        const string wire =
            "{\"id\":\"3\",\"result\":{\"execMode\":0,\"execModeCallResult\":0,\"gcmState\":0," +
            "\"gcmStateCallResult\":0,\"evidence\":\"call-return codes only; no PLC connection verified\"}}";
        NativeBridgeResponse? response = JsonSerializer.Deserialize<NativeBridgeResponse>(wire);

        Assert.NotNull(response?.Result);
        Assert.Equal(0, response.Result!.ExecMode);
        Assert.Equal(0L, response.Result!.ExecModeCallResult);
        Assert.Contains("no PLC connection", response.Result!.Evidence);
    }

    // ---------------------------------------------------------------
    // Pipe client against a mock named-pipe server
    // ---------------------------------------------------------------

    [Fact]
    public async Task Client_Handshake_RoundTripsOverRealPipe()
    {
        var mock = StartMock(reply => BuildResult(reply, "{\"workerName\":\"PlcMcpAutoShopNativeBridge\"," +
            "\"workerVersion\":\"1.0.0\",\"protocolVersion\":\"1.0\",\"vendor\":\"Inovance\",\"bitness\":\"x86\"," +
            "\"runtimeEnvironment\":\"in-process plugin\",\"capabilities\":[\"comm_stack_read_only\"]}"));
        var options = new NativeBridgeClientOptions { PipeName = mock.PipeName, Timeout = TimeSpan.FromSeconds(3) };

        await using var client = new NativeBridgePipeClient(options);
        await client.ConnectAsync();
        NativeBridgeCallResult call = await client.CallAsync(
            NativeBridgeProtocol.MethodHandshake, new NativeBridgeRequestParams(Client: "test"));

        Assert.True(call.Succeeded, call.ErrorMessage);
        Assert.Equal("PlcMcpAutoShopNativeBridge", call.Response!.Result!.WorkerName);
        Assert.Equal("handshake", mock.LastMethod);
        Assert.Equal("test", mock.LastClient);
    }

    [Fact]
    public async Task Client_ReadStatus_SurfacesBridgeErrorAsFailure()
    {
        var mock = StartMock(reply => BuildError(reply, -32001, "CommStackShim.dll not available in plugin directory"));
        var options = new NativeBridgeClientOptions { PipeName = mock.PipeName, Timeout = TimeSpan.FromSeconds(3) };

        await using var client = new NativeBridgePipeClient(options);
        await client.ConnectAsync();
        NativeBridgeCallResult call = await client.CallAsync(NativeBridgeProtocol.MethodReadStatus);

        Assert.False(call.Succeeded);
        Assert.Contains("-32001", call.ErrorMessage);
        Assert.Contains("CommStackShim.dll", call.ErrorMessage);
    }

    [Fact]
    public async Task Client_RefusesNonAllowlistedMethod_Locally()
    {
        var mock = StartMock(reply => BuildResult(reply, "{}"));
        var options = new NativeBridgeClientOptions { PipeName = mock.PipeName };

        await using var client = new NativeBridgePipeClient(options);
        await client.ConnectAsync();
        NativeBridgeCallResult call = await client.CallAsync("write_elements");

        Assert.False(call.Succeeded);
        Assert.Contains("not part of the read-only", call.ErrorMessage);
        Assert.Empty(mock.ReceivedRequests);
    }

    [Fact]
    public async Task Client_TimesOut_WhenBridgeNeverAnswers()
    {
        var mock = StartMock(_ => (string?)null); // no reply
        var options = new NativeBridgeClientOptions { PipeName = mock.PipeName, Timeout = TimeSpan.FromMilliseconds(400) };

        await using var client = new NativeBridgePipeClient(options);
        await client.ConnectAsync();
        NativeBridgeCallResult call = await client.CallAsync(NativeBridgeProtocol.MethodReadStatus);

        Assert.False(call.Succeeded);
        Assert.Contains("timed out", call.ErrorMessage);
    }

    [Fact]
    public async Task Client_SurfacesMalformedResponseAsFailure()
    {
        var mock = StartMock(_ => "this-is-not-json");
        var options = new NativeBridgeClientOptions { PipeName = mock.PipeName, Timeout = TimeSpan.FromSeconds(3) };

        await using var client = new NativeBridgePipeClient(options);
        await client.ConnectAsync();
        NativeBridgeCallResult call = await client.CallAsync(NativeBridgeProtocol.MethodCapabilities);

        Assert.False(call.Succeeded);
        Assert.Contains("not valid JSON", call.ErrorMessage);
    }

    [Fact]
    public async Task Client_ReportsDisconnectAsFailure()
    {
        var mock = StartMock(_ => null, closeImmediately: true);
        var options = new NativeBridgeClientOptions { PipeName = mock.PipeName, Timeout = TimeSpan.FromSeconds(3) };

        await using var client = new NativeBridgePipeClient(options);
        await client.ConnectAsync();
        NativeBridgeCallResult call = await client.CallAsync(NativeBridgeProtocol.MethodReadStatus);

        Assert.False(call.Succeeded);
        Assert.True(
            call.ErrorMessage!.Contains("closed the connection", StringComparison.Ordinal) ||
            call.ErrorMessage!.Contains("transport failure", StringComparison.Ordinal),
            call.ErrorMessage);
    }

    [Fact]
    public async Task Client_ConnectAsync_PropagatesMissingPipeFailure()
    {
        var options = new NativeBridgeClientOptions
        {
            PipeName = "PlcMcpAbsentBridgePipe",
            Timeout = TimeSpan.FromMilliseconds(500)
        };
        await using var client = new NativeBridgePipeClient(options);

        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync());
    }

    // ---------------------------------------------------------------
    // Services facade
    // ---------------------------------------------------------------

    [Fact]
    public async Task Services_Handshake_MapsNativeAnswer()
    {
        var mock = StartMock(reply => BuildResult(reply,
            "{\"workerName\":\"PlcMcpAutoShopNativeBridge\",\"workerVersion\":\"1.0.0\"," +
            "\"protocolVersion\":\"1.0\",\"vendor\":\"Inovance\",\"bitness\":\"x86\"," +
            "\"runtimeEnvironment\":\"in-process plugin\",\"capabilities\":[\"comm_stack_read_only\"]}"));
        var services = new NativeBridgeServices(new NativeBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        NativeBridgeHandshakeReport report = await services.HandshakeAsync("test");

        Assert.True(report.Connected);
        Assert.True(report.Answered);
        Assert.True(report.MatchesExpectation(out _));
        Assert.Equal("x86", report.Bitness);
        Assert.Contains("no vendor endorsement", report.Disclaimer);
    }

    [Fact]
    public async Task Services_Handshake_IdentityMismatch_IsDetected()
    {
        var mock = StartMock(reply => BuildResult(reply,
            "{\"workerName\":\"SomeOtherWorker\",\"protocolVersion\":\"9.9\"}"));
        var services = new NativeBridgeServices(new NativeBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        NativeBridgeHandshakeReport report = await services.HandshakeAsync();

        Assert.True(report.Answered);
        Assert.False(report.MatchesExpectation(out string reason));
        Assert.Contains("WorkerName mismatch", reason);
    }

    [Fact]
    public async Task Services_ReadStatus_MapsCallResults()
    {
        var mock = StartMock(reply => reply.Method switch
        {
            "handshake" => BuildResult(reply,
                "{\"workerName\":\"PlcMcpAutoShopNativeBridge\",\"protocolVersion\":\"1.0\"}"),
            "read_status" => BuildResult(reply,
                "{\"execMode\":0,\"execModeCallResult\":0,\"gcmState\":0,\"gcmStateCallResult\":0," +
                "\"evidence\":\"call-return codes only; no PLC connection verified\"}"),
            _ => BuildError(reply, -32601, "method not found")
        });
        var services = new NativeBridgeServices(new NativeBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        NativeBridgeStatusReport report = await services.ReadStatusAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(0L, report.ExecModeCallResult);
        Assert.Equal(0L, report.GcmStateCallResult);
        Assert.Contains("no PLC connection", report.Evidence);
    }

    [Fact]
    public async Task Services_ReadStatus_UnreachableBridge_ReportsFailure()
    {
        var services = new NativeBridgeServices(new NativeBridgeClientOptions
        {
            PipeName = "PlcMcpAbsentBridgePipe",
            Timeout = TimeSpan.FromMilliseconds(400)
        });

        NativeBridgeStatusReport report = await services.ReadStatusAsync();

        Assert.False(report.Succeeded);
        Assert.Contains("failed", report.Message);
        Assert.Contains("read-only", report.Disclaimer);
    }

    [Fact]
    public async Task Services_ReadElements_RejectsEmptyAddressList()
    {
        var services = new NativeBridgeServices();
        NativeBridgeElementReadReport report = await services.ReadElementsAsync("   ");

        Assert.False(report.Succeeded);
        Assert.Contains("must not be empty", report.Message);
    }

    // ---------------------------------------------------------------
    // Worker safety posture
    // ---------------------------------------------------------------

    [Fact]
    public async Task Worker_HardRejects_DownloadAndForce()
    {
        var worker = new NativeBridgeWorker(new NativeBridgeServices());
        var download = new EngineeringJobRequest("j1", EngineeringJobType.CompileProject, PlcVendor.Inovance, @"D:\p",
            new Dictionary<string, string> { ["target"] = "plc-download" });
        var force = new EngineeringJobRequest("j2", EngineeringJobType.InspectProject, PlcVendor.Inovance, @"D:\p",
            new Dictionary<string, string> { ["mode"] = "force-on" });

        EngineeringExecutionResult r1 = await worker.ExecuteAsync(download);
        EngineeringExecutionResult r2 = await worker.ExecuteAsync(force);

        Assert.False(r1.Success);
        Assert.Equal(CapabilityStatus.Unsupported, r1.Status);
        Assert.Contains("Security violation", r1.Details);
        Assert.Contains("hard rejected", r1.Details);
        Assert.False(r2.Success);
        Assert.Contains("dangerous operation", r2.Details);
    }

    [Fact]
    public async Task Worker_InspectProject_WithMockBridge_IsExperimentalOnly()
    {
        var mock = StartMock(reply => reply.Method switch
        {
            "handshake" => BuildResult(reply,
                "{\"workerName\":\"PlcMcpAutoShopNativeBridge\",\"workerVersion\":\"1.0.0\"," +
                "\"protocolVersion\":\"1.0\",\"vendor\":\"Inovance\",\"bitness\":\"x86\"," +
                "\"runtimeEnvironment\":\"in-process plugin\",\"capabilities\":[\"comm_stack_read_only\"]}"),
            "read_status" => BuildResult(reply,
                "{\"execMode\":0,\"execModeCallResult\":0,\"gcmState\":0,\"gcmStateCallResult\":0," +
                "\"evidence\":\"call-return codes only; no PLC connection verified\"}"),
            _ => BuildError(reply, -32601, "method not found")
        });
        var worker = new NativeBridgeWorker(new NativeBridgeServices(new NativeBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        }));

        EngineeringExecutionResult result = await worker.ExecuteAsync(
            new EngineeringJobRequest("j3", EngineeringJobType.InspectProject, PlcVendor.Inovance, @"D:\p"));

        Assert.True(result.Success);
        Assert.Equal(CapabilityStatus.Experimental, result.Status); // never above Experimental
        Assert.Contains("no PLC connection is verified", result.Message);
        Assert.Contains("no vendor endorsement", result.Details);
    }

    [Fact]
    public async Task Worker_InspectProject_UnreachableBridge_IsUnsupported()
    {
        var worker = new NativeBridgeWorker(new NativeBridgeServices(new NativeBridgeClientOptions
        {
            PipeName = "PlcMcpAbsentBridgePipe",
            Timeout = TimeSpan.FromMilliseconds(400)
        }));

        EngineeringExecutionResult result = await worker.ExecuteAsync(
            new EngineeringJobRequest("j4", EngineeringJobType.InspectProject, PlcVendor.Inovance, @"D:\p"));

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
    }

    [Fact]
    public async Task Worker_Rejects_NonInspectJobTypes()
    {
        var worker = new NativeBridgeWorker(new NativeBridgeServices());

        EngineeringExecutionResult result = await worker.ExecuteAsync(
            new EngineeringJobRequest("j5", EngineeringJobType.ExportPou, PlcVendor.Inovance, @"D:\p"));

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("read-only inspection only", result.Message);
    }

    // ---------------------------------------------------------------
    // Mock pipe server (loopback named pipe, one client at a time)
    // ---------------------------------------------------------------

    private sealed class ReceivedRequest
    {
        public string Id { get; set; } = "1";
        public string Method { get; set; } = string.Empty;
        public string? Client { get; set; }
        public string? Addresses { get; set; }
    }

    private static string BuildResult(ReceivedRequest request, string resultJson) =>
        $"{{\"id\":\"{request.Id}\",\"result\":{resultJson}}}";

    private static string BuildError(ReceivedRequest request, int code, string message) =>
        $"{{\"id\":\"{request.Id}\",\"error\":{{\"code\":{code},\"message\":\"{message}\"}}}}";

    private sealed class MockNativeBridgeServer : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly Func<ReceivedRequest, string?> _responder;
        private readonly bool _closeImmediately;
        private readonly Task _loop;

        public string PipeName { get; }
        public List<ReceivedRequest> ReceivedRequests { get; } = new();
        public string? LastMethod { get; private set; }
        public string? LastClient { get; private set; }

        public MockNativeBridgeServer(
            string pipeName,
            Func<ReceivedRequest, string?> responder,
            bool closeImmediately = false)
        {
            PipeName = pipeName;
            _responder = responder;
            _closeImmediately = closeImmediately;
            _pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _loop = Task.Run(ServeLoopAsync);
        }

        private async Task ServeLoopAsync()
        {
            // Raw byte I/O with manual line framing: StreamReader/StreamWriter
            // over named pipes hangs on some runtimes (see the client comment).
            try
            {
                await _pipe.WaitForConnectionAsync().ConfigureAwait(false);
                if (_closeImmediately)
                {
                    _pipe.Disconnect();
                    return;
                }
                var buffer = new byte[4096];
                var pending = new StringBuilder();
                while (_pipe.IsConnected)
                {
                    int read = await _pipe.ReadAsync(buffer).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }
                    pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    int newline;
                    while ((newline = pending.ToString().IndexOf('\n')) >= 0)
                    {
                        string line = pending.ToString(0, newline).Trim('\r');
                        pending.Remove(0, newline + 1);
                        if (line.Length == 0)
                        {
                            continue;
                        }
                        var request = new ReceivedRequest();
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            if (doc.RootElement.TryGetProperty("id", out JsonElement idElement))
                            {
                                request.Id = idElement.GetString() ?? "1";
                            }
                            request.Method = doc.RootElement.GetProperty("method").GetString() ?? string.Empty;
                            if (doc.RootElement.TryGetProperty("params", out JsonElement paramsElement) &&
                                paramsElement.ValueKind == JsonValueKind.Object)
                            {
                                if (paramsElement.TryGetProperty("client", out JsonElement client))
                                {
                                    request.Client = client.GetString();
                                }
                                if (paramsElement.TryGetProperty("addresses", out JsonElement addresses))
                                {
                                    request.Addresses = addresses.GetString();
                                }
                            }
                        }
                        catch (JsonException)
                        {
                            // ignore malformed requests in the mock
                        }
                        ReceivedRequests.Add(request);
                        LastMethod = request.Method;
                        LastClient = request.Client;

                        string? response = _responder(request);
                        if (response == null)
                        {
                            continue; // silent server: forces a client timeout
                        }
                        byte[] payload = Encoding.UTF8.GetBytes(response + "\n");
                        await _pipe.WriteAsync(payload).ConfigureAwait(false);
                        await _pipe.FlushAsync().ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                // mock server teardown
            }
        }

        public void Dispose()
        {
            try { _pipe.Dispose(); } catch { }
        }
    }

    private MockNativeBridgeServer StartMock(
        Func<ReceivedRequest, string?> responder,
        bool closeImmediately = false)
    {
        var mock = new MockNativeBridgeServer(
            "PlcMcpTestBridge_" + Guid.NewGuid().ToString("N")[..12], responder, closeImmediately);
        _servers.Add(mock);
        return mock;
    }
}
