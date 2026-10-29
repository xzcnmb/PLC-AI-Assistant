using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Jobs;
using PlcMcp.Engineering.Workers.AutoShop.InjectBridge;

namespace PlcMcp.Engineering.Tests;

/// <summary>
/// Tests for the AutoShop inject bridge (E2): the JSON-line protocol, the
/// named-pipe client against a mock pipe server, the services facade (gate
/// matrix + loopback validation), the launcher (parameter validation, PID
/// ownership protection, cleanup) and the worker's safety posture.
///
/// ALL tests are offline: they run against loopback named pipes and fakes.
/// No AutoShop installation, no real injection. The single real-injection
/// probe is env-gated (<see cref="LocalSimulatorFactAttribute"/>) and skipped
/// unless PLCMCP_LOCAL_SIMULATOR=1.
/// </summary>
public class InjectBridgeTests : IDisposable
{
    private readonly List<MockInjectBridgeServer> _servers = new();
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (MockInjectBridgeServer server in _servers)
        {
            server.Dispose();
        }
        foreach (string file in _tempFiles)
        {
            try { File.Delete(file); } catch { }
        }
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------
    // Protocol model
    // ---------------------------------------------------------------

    [Fact]
    public void Protocol_NonGatedMethods_AreReadOnly()
    {
        var allowed = InjectBridgeProtocol.AllowedMethods;
        Assert.Contains(InjectBridgeProtocol.MethodHandshake, allowed);
        Assert.Contains(InjectBridgeProtocol.MethodCapabilities, allowed);
        Assert.Contains(InjectBridgeProtocol.MethodSetTarget, allowed);
        Assert.Contains(InjectBridgeProtocol.MethodReadStatus, allowed);
        Assert.Contains(InjectBridgeProtocol.MethodReadElements, allowed);
        Assert.Contains(InjectBridgeProtocol.MethodShutdown, allowed);
        Assert.Equal(6, allowed.Count);
    }

    [Fact]
    public void Protocol_WriteSurface_IsSeparatedBehindGates()
    {
        var gated = InjectBridgeProtocol.GatedMethods;
        Assert.Contains(InjectBridgeProtocol.MethodWriteElements, gated);
        Assert.Contains(InjectBridgeProtocol.MethodForce, gated);
        Assert.Contains(InjectBridgeProtocol.MethodDownload, gated);
        Assert.Contains(InjectBridgeProtocol.MethodCompile, gated);

        // No overlap between the always-sendable surface and the gated one.
        Assert.Empty(InjectBridgeProtocol.AllowedMethods.Intersect(gated));
    }

    [Fact]
    public void Request_SerializesToNativeWireShape()
    {
        var request = new InjectBridgeRequest("7", InjectBridgeProtocol.MethodReadElements,
            new InjectBridgeRequestParams(Addresses: "D100,D101"));
        string json = JsonSerializer.Serialize(request);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("7", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("read_elements", doc.RootElement.GetProperty("method").GetString());
        Assert.Equal("D100,D101", doc.RootElement.GetProperty("params").GetProperty("addresses").GetString());
    }

    [Fact]
    public void Response_ParsesHandshakeAndReservationShapes()
    {
        // Exact shapes answered by the native bridge (see BridgePipeServer.cpp).
        const string handshake =
            "{\"id\":\"1\",\"result\":{\"workerName\":\"PlcMcpAutoShopInjectBridge\",\"workerVersion\":\"1.0.0\"," +
            "\"protocolVersion\":\"1.0\",\"vendor\":\"Inovance\",\"bitness\":\"x86\"," +
            "\"runtimeEnvironment\":\"injected in-process (own started instance)\",\"pid\":1234," +
            "\"capabilities\":[\"environment_inspection\",\"comm_stack_read_only_loopback_first\",\"element_write_reservation\"],\"disclaimer\":\"x\"}}";
        InjectBridgeResponse? parsed = JsonSerializer.Deserialize<InjectBridgeResponse>(handshake);
        Assert.NotNull(parsed?.Result);
        Assert.Equal("PlcMcpAutoShopInjectBridge", parsed.Result!.WorkerName);
        Assert.Equal(1234, parsed.Result!.Pid);
        Assert.Equal("x86", parsed.Result!.Bitness);

        const string reservation =
            "{\"id\":\"8\",\"result\":{\"method\":\"write_elements\",\"resolved\":true,\"invoked\":false," +
            "\"export\":\"?WriteElemValue@CCommunicateEth@@\",\"exportRva\":\"0x00037A40\"," +
            "\"reason\":\"vendor export resolved but not invoked\"}}";
        parsed = JsonSerializer.Deserialize<InjectBridgeResponse>(reservation);
        Assert.NotNull(parsed?.Result);
        Assert.True(parsed.Result!.Resolved);
        Assert.False(parsed.Result!.Invoked); // never invoked in the delivered state
        Assert.Contains("WriteElemValue", parsed.Result!.Export);

        const string error =
            "{\"id\":\"5\",\"error\":{\"code\":-32003,\"message\":\"non-loopback target refused\"}}";
        parsed = JsonSerializer.Deserialize<InjectBridgeResponse>(error);
        Assert.Null(parsed!.Result);
        Assert.Equal(InjectBridgeProtocol.ErrLoopbackRequired, parsed.Error!.Code);
    }

    // ---------------------------------------------------------------
    // Gate options (loopback hard validation)
    // ---------------------------------------------------------------

    [Fact]
    public void GateOptions_LoopbackValidation()
    {
        Assert.True(InjectBridgeGateOptions.IsLoopbackAddress("127.0.0.1"));
        Assert.True(InjectBridgeGateOptions.IsLoopbackAddress("127.9.9.9"));
        Assert.True(InjectBridgeGateOptions.IsLoopbackAddress("localhost"));

        Assert.False(InjectBridgeGateOptions.IsLoopbackAddress("8.8.8.8"));
        Assert.False(InjectBridgeGateOptions.IsLoopbackAddress("192.168.1.1"));
        Assert.False(InjectBridgeGateOptions.IsLoopbackAddress("256.1.1.1"));
        Assert.False(InjectBridgeGateOptions.IsLoopbackAddress("1.2.3"));
        Assert.False(InjectBridgeGateOptions.IsLoopbackAddress(""));
        Assert.False(InjectBridgeGateOptions.IsLoopbackAddress(null));
    }

    [Fact]
    public void GateOptions_Defaults_AreAllClosed()
    {
        var gate = new InjectBridgeGateOptions();
        Assert.False(gate.AllowElementWrite);
        Assert.False(gate.AllowForce);
        Assert.False(gate.AllowDownload);
        Assert.Null(gate.CompileEntryHint);
        Assert.True(gate.RequireLoopbackTarget);
        // F1: compile is open by default; the write surface stays deterministically closed.
        string conditions = gate.DescribeGateConditions();
        Assert.Contains("compile is OPEN by default", conditions, StringComparison.Ordinal);
        Assert.Contains("stub", conditions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GateOptions_CannotDisableLoopbackRequirement()
    {
        // Validation runs at every consumer entry point (client/services/
        // launcher constructors); disabling the loopback requirement must be
        // impossible to smuggle past it.
        Assert.Throws<ArgumentException>(() =>
        {
            var gate = new InjectBridgeGateOptions { RequireLoopbackTarget = false };
            gate.Validate();
        });
        // And the default gate passes validation.
        new InjectBridgeGateOptions().Validate();
    }

    // ---------------------------------------------------------------
    // Pipe client against a mock named-pipe server
    // ---------------------------------------------------------------

    [Fact]
    public async Task Client_Handshake_RoundTripsOverRealPipe()
    {
        var mock = StartMock(reply => BuildResult(reply,
            "{\"workerName\":\"PlcMcpAutoShopInjectBridge\",\"workerVersion\":\"1.0.0\"," +
            "\"protocolVersion\":\"1.0\",\"vendor\":\"Inovance\",\"bitness\":\"x86\"," +
            "\"runtimeEnvironment\":\"injected in-process (own started instance)\",\"pid\":99}"));

        await using var client = new InjectBridgePipeClient(
            new InjectBridgeClientOptions { PipeName = mock.PipeName, Timeout = TimeSpan.FromSeconds(3) });
        await client.ConnectAsync();
        InjectBridgeCallResult call = await client.CallAsync(
            InjectBridgeProtocol.MethodHandshake, new InjectBridgeRequestParams(Client: "test"));

        Assert.True(call.Succeeded, call.ErrorMessage);
        Assert.Equal("PlcMcpAutoShopInjectBridge", call.Response!.Result!.WorkerName);
        Assert.Equal("handshake", mock.LastMethod);
        Assert.Equal("test", mock.LastClient);
    }

    [Fact]
    public async Task Client_RefusesNonAllowlistedMethod_Locally()
    {
        var mock = StartMock(reply => BuildResult(reply, "{}"));
        await using var client = new InjectBridgePipeClient(
            new InjectBridgeClientOptions { PipeName = mock.PipeName });
        await client.ConnectAsync();

        InjectBridgeCallResult call = await client.CallAsync("download_plc_now");

        Assert.False(call.Succeeded);
        Assert.True(call.RefusedLocally);
        Assert.Empty(mock.ReceivedRequests); // nothing touched the pipe
    }

    [Fact]
    public async Task Client_WriteElements_GateClosed_IsRefusedLocallyWithoutPipeTraffic()
    {
        var mock = StartMock(reply => BuildResult(reply, "{}"));
        await using var client = new InjectBridgePipeClient(
            new InjectBridgeClientOptions { PipeName = mock.PipeName }); // default gate: closed
        await client.ConnectAsync();

        InjectBridgeCallResult call = await client.CallAsync(
            InjectBridgeProtocol.MethodWriteElements,
            new InjectBridgeRequestParams(Addresses: "D100", Target: "127.0.0.1"));

        Assert.False(call.Succeeded);
        Assert.True(call.RefusedLocally);
        Assert.Contains("gate", call.ErrorMessage);
        Assert.Empty(mock.ReceivedRequests);
    }

    [Fact]
    public async Task Client_WriteElements_GateOpen_GoesOnTheWire()
    {
        var mock = StartMock(reply => BuildResult(reply,
            "{\"method\":\"write_elements\",\"resolved\":true,\"invoked\":false," +
            "\"export\":\"?WriteElemValue@CCommunicateEth@@\",\"reason\":\"not invoked\"}"));
        var gate = new InjectBridgeGateOptions { AllowElementWrite = true };
        await using var client = new InjectBridgePipeClient(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Gate = gate,
            Timeout = TimeSpan.FromSeconds(3)
        });
        await client.ConnectAsync();

        InjectBridgeCallResult call = await client.CallAsync(
            InjectBridgeProtocol.MethodWriteElements,
            new InjectBridgeRequestParams(Addresses: "D100", Target: "127.0.0.1"));

        Assert.True(call.Succeeded, call.ErrorMessage);
        Assert.True(call.Response!.Result!.Resolved);
        Assert.False(call.Response!.Result!.Invoked);
        Assert.Single(mock.ReceivedRequests);
        Assert.Equal("write_elements", mock.LastMethod);
    }

    [Fact]
    public async Task Client_NonLoopbackTarget_IsRefusedLocallyWithoutPipeTraffic()
    {
        var mock = StartMock(reply => BuildResult(reply, "{}"));
        await using var client = new InjectBridgePipeClient(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Gate = new InjectBridgeGateOptions { AllowElementWrite = true }
        });
        await client.ConnectAsync();

        InjectBridgeCallResult write = await client.CallAsync(
            InjectBridgeProtocol.MethodWriteElements,
            new InjectBridgeRequestParams(Addresses: "D100", Target: "192.168.1.1"));
        InjectBridgeCallResult setTarget = await client.CallAsync(
            InjectBridgeProtocol.MethodSetTarget,
            new InjectBridgeRequestParams(Ip: "8.8.8.8", Port: 12939));

        Assert.True(write.RefusedLocally);
        Assert.True(setTarget.RefusedLocally);
        Assert.Contains("loopback", write.ErrorMessage);
        Assert.Contains("loopback", setTarget.ErrorMessage);
        Assert.Empty(mock.ReceivedRequests);
    }

    [Fact]
    public async Task Client_TimesOut_WhenBridgeNeverAnswers()
    {
        var mock = StartMock(_ => null); // silent server
        await using var client = new InjectBridgePipeClient(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromMilliseconds(400)
        });
        await client.ConnectAsync();

        InjectBridgeCallResult call = await client.CallAsync(InjectBridgeProtocol.MethodReadStatus);

        Assert.False(call.Succeeded);
        Assert.Contains("timed out", call.ErrorMessage);
    }

    [Fact]
    public async Task Client_SurfacesMalformedResponseAsFailure()
    {
        var mock = StartMock(_ => "this-is-not-json");
        await using var client = new InjectBridgePipeClient(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });
        await client.ConnectAsync();

        InjectBridgeCallResult call = await client.CallAsync(InjectBridgeProtocol.MethodCapabilities);

        Assert.False(call.Succeeded);
        Assert.Contains("not valid JSON", call.ErrorMessage);
    }

    [Fact]
    public async Task Client_ReportsDisconnectAsFailure()
    {
        var mock = StartMock(_ => null, closeImmediately: true);
        await using var client = new InjectBridgePipeClient(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });
        await client.ConnectAsync();

        InjectBridgeCallResult call = await client.CallAsync(InjectBridgeProtocol.MethodReadStatus);

        Assert.False(call.Succeeded);
        Assert.True(
            call.ErrorMessage!.Contains("closed the connection", StringComparison.Ordinal) ||
            call.ErrorMessage!.Contains("transport failure", StringComparison.Ordinal),
            call.ErrorMessage);
    }

    [Fact]
    public async Task Client_ConnectAsync_PropagatesMissingPipeFailure()
    {
        await using var client = new InjectBridgePipeClient(new InjectBridgeClientOptions
        {
            PipeName = "PlcMcpAbsentInjectBridgePipe",
            Timeout = TimeSpan.FromMilliseconds(500)
        });
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync());
    }

    // ---------------------------------------------------------------
    // Services facade
    // ---------------------------------------------------------------

    [Fact]
    public async Task Services_Handshake_MapsBridgeAnswer()
    {
        var mock = StartMock(reply => BuildResult(reply,
            "{\"workerName\":\"PlcMcpAutoShopInjectBridge\",\"workerVersion\":\"1.0.0\"," +
            "\"protocolVersion\":\"1.0\",\"vendor\":\"Inovance\",\"bitness\":\"x86\"," +
            "\"runtimeEnvironment\":\"injected in-process (own started instance)\",\"pid\":4242," +
            "\"capabilities\":[\"comm_stack_read_only_loopback_first\"]}"));
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeHandshakeReport report = await services.HandshakeAsync("test");

        Assert.True(report.Connected);
        Assert.True(report.Answered);
        Assert.True(report.MatchesExpectation(out _));
        Assert.Equal(4242, report.HostPid);
        Assert.Contains("no vendor endorsement", report.Disclaimer);
    }

    [Fact]
    public async Task Services_Handshake_IdentityMismatch_IsDetected()
    {
        var mock = StartMock(reply => BuildResult(reply,
            "{\"workerName\":\"SomeOtherWorker\",\"protocolVersion\":\"9.9\"}"));
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeHandshakeReport report = await services.HandshakeAsync();

        Assert.True(report.Answered);
        Assert.False(report.MatchesExpectation(out string reason));
        Assert.Contains("WorkerName mismatch", reason);
    }

    [Fact]
    public async Task Services_Capabilities_MapVerbatim()
    {
        var mock = StartMock(reply => BuildResult(reply,
            "{\"comm_stack_read_only\":\"Experimental: ...\",\"variable_read\":\"Unsupported: no verified publisher/connection\"," +
            "\"element_write\":\"Experimental (reservation)\",\"force\":\"Unsupported: bridge gate closed (allowForce=false, default)\"," +
            "\"download\":\"Unsupported: bridge gate closed (allowDownload=false, default)\"," +
            "\"compile\":\"Unsupported: E3 entry-point slot reserved\",\"run_stop\":\"Unsupported\"}"));
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeCapabilityMatrix matrix = await services.CapabilitiesAsync();

        Assert.True(matrix.Succeeded);
        Assert.Contains("Unsupported", matrix.Force);
        Assert.Contains("default", matrix.Download);
        Assert.Contains("E3", matrix.Compile);
        Assert.Equal("Unsupported", matrix.RunStop);
    }

    [Fact]
    public async Task Services_SetTarget_NonLoopback_IsRefusedLocally()
    {
        var mock = StartMock(reply => BuildResult(reply, "{}"));
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeSetTargetReport report = await services.SetTargetAsync("192.168.1.1", 12939);

        Assert.False(report.Succeeded);
        Assert.Contains("refused locally", report.Message);
        Assert.Empty(mock.ReceivedRequests);
    }

    [Fact]
    public async Task Services_SetTarget_Loopback_RoundTrips()
    {
        var mock = StartMock(reply => BuildResult(reply,
            "{\"stored\":true,\"ip\":\"127.0.0.1\",\"port\":12939,\"loopbackOnly\":true}"));
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeSetTargetReport report = await services.SetTargetAsync("127.0.0.1", 12939);

        Assert.True(report.Succeeded, report.Message);
        Assert.True(report.Stored);
        Assert.True(report.LoopbackOnly);
        Assert.Equal("set_target", mock.LastMethod);
    }

    [Fact]
    public async Task Services_ReadStatus_MapsCallResults()
    {
        var mock = StartMock(reply => reply.Method switch
        {
            "read_status" => BuildResult(reply,
                "{\"execMode\":0,\"execModeCallResult\":0,\"gcmState\":0,\"gcmStateCallResult\":0," +
                "\"evidence\":\"call-return codes only; loopback target; no PLC verified\"}"),
            _ => BuildError(reply, -32601, "method not found")
        });
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeStatusReport report = await services.ReadStatusAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(0L, report.ExecModeCallResult);
        Assert.Contains("no PLC verified", report.Evidence);
    }

    [Fact]
    public async Task Services_ReadElements_RejectsEmptyAddressList()
    {
        var services = new InjectBridgeServices();
        InjectBridgeElementReadReport report = await services.ReadElementsAsync("   ");
        Assert.False(report.Succeeded);
        Assert.Contains("must not be empty", report.Message);
    }

    [Fact]
    public async Task Services_WriteReservation_GateClosed_IsHonestUnsupportedWithoutPipeTraffic()
    {
        var mock = StartMock(reply => BuildResult(reply, "{}"));
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        foreach (string method in new[]
                 {
                     InjectBridgeProtocol.MethodWriteElements,
                     InjectBridgeProtocol.MethodForce,
                     InjectBridgeProtocol.MethodDownload
                 })
        {
            InjectBridgeWriteReport report = await services.WriteReservationAsync(method, "D100");
            Assert.False(report.Succeeded, method);
            Assert.False(report.GateOpen, method);
            Assert.True(report.HonestUnsupported, method);
            Assert.Contains("Unsupported", report.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(mock.ReceivedRequests); // closed gates never reach the pipe
    }

    [Fact]
    public async Task Services_WriteReservation_GateOpen_NonLoopbackTarget_RefusedLocally()
    {
        var mock = StartMock(reply => BuildResult(reply, "{}"));
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Gate = new InjectBridgeGateOptions { AllowElementWrite = true },
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeWriteReport report = await services.WriteReservationAsync(
            InjectBridgeProtocol.MethodWriteElements, "D100", target: "8.8.8.8");

        Assert.False(report.Succeeded);
        Assert.Contains("refused locally", report.Message);
        Assert.Empty(mock.ReceivedRequests);
    }

    [Fact]
    public async Task Services_WriteReservation_GateOpen_MapsResolvedNotInvoked()
    {
        var mock = StartMock(reply => BuildResult(reply,
            "{\"method\":\"write_elements\",\"resolved\":true,\"invoked\":false," +
            "\"export\":\"?WriteElemValue@CCommunicateEth@@\",\"exportRva\":\"0x00037A40\"," +
            "\"reason\":\"vendor export resolved but not invoked\"}"));
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Gate = new InjectBridgeGateOptions { AllowElementWrite = true },
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeWriteReport report = await services.WriteReservationAsync(
            InjectBridgeProtocol.MethodWriteElements, "D100", target: "127.0.0.1");

        Assert.True(report.Succeeded, report.Message);
        Assert.True(report.GateOpen);
        Assert.True(report.Resolved);
        Assert.False(report.Invoked);
        Assert.Contains("NOT invoked", report.Message);
    }

    [Fact]
    public async Task Services_WriteReservation_GateOpen_BridgeGateClosed_SurfacesHonestUnsupported()
    {
        // The C# gate is open but the bridge DLL gate is closed: the bridge
        // answers -32005 and the facade must report it as honest Unsupported,
        // never as success.
        var mock = StartMock(reply => BuildError(reply, InjectBridgeProtocol.ErrGateClosed,
            "gate closed: allowWrite=false in the bridge config"));
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Gate = new InjectBridgeGateOptions { AllowElementWrite = true },
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeWriteReport report = await services.WriteReservationAsync(
            InjectBridgeProtocol.MethodWriteElements, "D100", target: "127.0.0.1");

        Assert.False(report.Succeeded);
        Assert.True(report.HonestUnsupported);
        Assert.Contains("-32005", report.Message);
    }

    [Fact]
    public async Task Services_Compile_WithoutE3Hint_IsUnsupportedLocally()
    {
        var mock = StartMock(reply => BuildResult(reply, "{}"));
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeCompileReport report = await services.CompileAsync();

        Assert.False(report.Succeeded);
        Assert.False(report.EntryConfigured);
        Assert.Contains("E3", report.Message);
        Assert.Empty(mock.ReceivedRequests);
    }

    [Fact]
    public async Task Services_Compile_WithHint_BridgeStillAnswersUnsupported()
    {
        var mock = StartMock(reply => BuildError(reply, InjectBridgeProtocol.ErrUnsupported,
            "no compile entry point is configured"));
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Gate = new InjectBridgeGateOptions { CompileEntryHint = "0x1197CD8 (from E3, pending)" },
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeCompileReport report = await services.CompileAsync();

        Assert.False(report.Succeeded);
        Assert.True(report.EntryConfigured);
        Assert.Contains("Unsupported", report.Message);
        Assert.Equal("compile", mock.LastMethod);
    }

    [Fact]
    public async Task Services_Compile_BridgeAccepts_MapsSubmitReceipt()
    {
        // F1: the compile gate is open by default — the bridge answers with the
        // submit receipt; only a result answer surfaces as Succeeded.
        var mock = StartMock(reply => BuildResult(reply,
            "{\"compile\":\"submitted\",\"compileEntryRva\":\"0x15A2EC0\",\"note\":\"compile-all submitted " +
            "asynchronously; artifacts appear under <projectDir>\\\\Compile\"}"));
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Gate = new InjectBridgeGateOptions { CompileEntryHint = "0x15A2EC0 (F1-verified)" },
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeCompileReport report = await services.CompileAsync();

        Assert.True(report.Succeeded, report.Message);
        Assert.True(report.EntryConfigured);
        Assert.False(report.HonestUnsupported);
        Assert.Contains("submitted", report.Message, StringComparison.Ordinal);
        Assert.Equal("compile", mock.LastMethod);
    }

    [Fact]
    public async Task Services_Compile_ProjectGateRefusal_SurfacesAsFailure()
    {
        var mock = StartMock(reply => BuildError(reply, InjectBridgeProtocol.ErrVendorCallFailed,
            "compile refused/failed (rc=-32002): project gate (0x161B620) returned 0: no project is open"));
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Gate = new InjectBridgeGateOptions { CompileEntryHint = "0x15A2EC0 (F1-verified)" },
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeCompileReport report = await services.CompileAsync();

        Assert.False(report.Succeeded);
        Assert.Contains("project gate", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Services_ReadElements_MapsInProcessValues()
    {
        // F2-era response shape (no G1 delivery fields): the report stays
        // honest — call-level wording, Delivered null.
        var mock = StartMock(reply => reply.Method switch
        {
            "read_elements" => BuildResult(reply,
                "{\"count\":2,\"values\":\"D100=4660;M0=1\",\"note\":\"in-process vendor read: CSoftAddrMap " +
                "filled via tagSoftAddr 5-arg ctor + CMap::SetAt (non-stub primitives), tag+0x20 value slot\"}"),
            _ => BuildError(reply, -32601, "method not found")
        });
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeElementReadReport report = await services.ReadElementsAsync("D100,M0");

        Assert.True(report.Succeeded, report.Message);
        Assert.Equal(2, report.Count);
        Assert.Equal("D100=4660;M0=1", report.Values);
        Assert.Contains("in-process vendor read", report.Note, StringComparison.Ordinal);
        Assert.Contains("in-process vendor read", report.Message, StringComparison.Ordinal);
        Assert.Null(report.Delivered);
    }

    [Fact]
    public async Task Services_ReadElements_G1_MapsDeliveryFields()
    {
        // G1 (docs/inovance-autoshop-monitor-values.md): the bridge reports
        // delivered=N (CString tag+0x0C slots that changed = daemon-answered
        // reads), the per-slot mirror and the variant receipts. A delivered
        // read is promoted to the value-carrying wording; the baseline with
        // delivered=0 stays CALL-LEVEL.
        var mock = StartMock(reply => reply.Method switch
        {
            "read_elements" => BuildResult(reply,
                "{\"count\":2,\"delivered\":1,\"values\":\"D100=0?;M0=1\",\"valueSlots\":\"M0=1;D100=-\"," +
                "\"watchRc\":2,\"activeMap\":\"0x00000000\",\"activeMapCount\":-1,\"onlineBefore\":\"1\"," +
                "\"variant\":\"wnd=null(proven-safe baseline);\",\"note\":\"g1\"}"),
            _ => BuildError(reply, -32601, "method not found")
        });
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeElementReadReport report = await services.ReadElementsAsync("D100,M0");

        Assert.True(report.Succeeded, report.Message);
        Assert.Equal(1, report.Delivered);
        Assert.Equal("M0=1;D100=-", report.ValueSlots);
        Assert.Equal(2, report.WatchRc);
        Assert.Contains("DELIVERED values", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Services_ReadElements_G1_ZeroDeliveredStaysCallLevel()
    {
        var mock = StartMock(reply => reply.Method switch
        {
            "read_elements" => BuildResult(reply,
                "{\"count\":1,\"delivered\":0,\"values\":\"M0=0?\",\"valueSlots\":\"M0=-\",\"watchRc\":-1," +
                "\"variant\":\"wnd=null(proven-safe baseline);\",\"note\":\"g1\"}"),
            _ => BuildError(reply, -32601, "method not found")
        });
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeElementReadReport report = await services.ReadElementsAsync("M0");

        Assert.True(report.Succeeded, report.Message);
        Assert.Equal(0, report.Delivered);
        Assert.Contains("CALL-LEVEL", report.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PLC data", report.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Services_ReadElements_G1_VariantIsForwardedInParams()
    {
        string? captured = null;
        var mock = StartMock(reply =>
        {
            if (reply.Method == "read_elements")
            {
                captured = reply.ParamsJson;
                return BuildResult(reply, "{\"count\":1,\"delivered\":0,\"values\":\"M0=0?\"}");
            }
            return BuildError(reply, -32601, "method not found");
        });
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        var variant = new InjectBridgeReadVariant(
            Mode: 1, Level: 0, SettleMs: 12000,
            UseMainFrameWnd: true, ReportActiveMap: true, TryWatchPath: true,
            TryOnlineProbe: true);
        InjectBridgeElementReadReport report = await services.ReadElementsAsync("M0", variant);

        Assert.True(report.Succeeded, report.Message);
        Assert.NotNull(captured);
        Assert.Contains("\"mode\":1", captured, StringComparison.Ordinal);
        Assert.Contains("\"settleMs\":12000", captured, StringComparison.Ordinal);
        Assert.Contains("\"wnd\":1", captured, StringComparison.Ordinal);
        Assert.Contains("\"activeMap\":1", captured, StringComparison.Ordinal);
        Assert.Contains("\"watch\":1", captured, StringComparison.Ordinal);
        Assert.Contains("\"online\":1", captured, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Services_ReadElements_G1_OnlineRcMaps()
    {
        var mock = StartMock(reply => reply.Method switch
        {
            "read_elements" => BuildResult(reply,
                "{\"count\":1,\"delivered\":0,\"values\":\"M0=0?\",\"onlineRc\":-1,\"watchRc\":-1," +
                "\"variant\":\"online=fault(0xc0000005);\"}"),
            _ => BuildError(reply, -32601, "method not found")
        });
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        var report = await services.ReadElementsAsync("M0",
            new InjectBridgeReadVariant(TryOnlineProbe: true));

        Assert.True(report.Succeeded, report.Message);
        Assert.Equal(-1, report.OnlineRc);
        Assert.Contains("online=fault", report.Variant, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Services_ReadElements_BaselineSendsNoVariantParams()
    {
        // The plain overload must not activate any G1 variant: the native
        // side parses only numeric values, and null fields must never be
        // forwarded as switches (the proven F2 baseline stays the default).
        string? captured = null;
        var mock = StartMock(reply =>
        {
            if (reply.Method == "read_elements")
            {
                captured = reply.ParamsJson;
                return BuildResult(reply, "{\"count\":1,\"delivered\":0,\"values\":\"M0=0?\"}");
            }
            return BuildError(reply, -32601, "method not found");
        });
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        });

        await services.ReadElementsAsync("M0");

        Assert.NotNull(captured);
        Assert.DoesNotContain("\"mode\":1", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("\"wnd\":1", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("\"watch\":1", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("\"activeMap\":1", captured, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // Worker safety posture
    // ---------------------------------------------------------------

    [Fact]
    public async Task Worker_HardRejects_DownloadAndForce()
    {
        var worker = new InjectBridgeWorker(new InjectBridgeServices());
        var download = new EngineeringJobRequest("j1", EngineeringJobType.CompileProject, PlcVendor.Inovance, @"D:\p",
            new Dictionary<string, string> { ["target"] = "plc-download" });
        var force = new EngineeringJobRequest("j2", EngineeringJobType.InspectProject, PlcVendor.Inovance, @"D:\p",
            new Dictionary<string, string> { ["mode"] = "force-on" });

        EngineeringExecutionResult r1 = await worker.ExecuteAsync(download);
        EngineeringExecutionResult r2 = await worker.ExecuteAsync(force);

        Assert.False(r1.Success);
        Assert.Equal(CapabilityStatus.Unsupported, r1.Status);
        Assert.Contains("Security violation", r1.Details);
        Assert.False(r2.Success);
        Assert.Contains("dangerous operation", r2.Details);
    }

    [Fact]
    public async Task Worker_Rejects_NonInspectJobTypes_WithGateText()
    {
        var worker = new InjectBridgeWorker(new InjectBridgeServices());

        EngineeringExecutionResult result = await worker.ExecuteAsync(
            new EngineeringJobRequest("j3", EngineeringJobType.ExportPou, PlcVendor.Inovance, @"D:\p"));

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
        Assert.Contains("read-only inspection only", result.Message);
        Assert.Contains("E3", result.Details);
    }

    [Fact]
    public async Task Worker_InspectProject_WithMockBridge_IsExperimentalOnly()
    {
        var mock = StartMock(reply => BuildResult(reply,
            "{\"workerName\":\"PlcMcpAutoShopInjectBridge\",\"workerVersion\":\"1.0.0\"," +
            "\"protocolVersion\":\"1.0\",\"vendor\":\"Inovance\",\"bitness\":\"x86\"," +
            "\"runtimeEnvironment\":\"injected in-process (own started instance)\",\"pid\":7," +
            "\"capabilities\":[\"comm_stack_read_only_loopback_first\"]}"));
        var worker = new InjectBridgeWorker(new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Timeout = TimeSpan.FromSeconds(3)
        }));

        EngineeringExecutionResult result = await worker.ExecuteAsync(
            new EngineeringJobRequest("j4", EngineeringJobType.InspectProject, PlcVendor.Inovance, @"D:\p"));

        Assert.True(result.Success);
        Assert.Equal(CapabilityStatus.Experimental, result.Status); // never above Experimental
        Assert.Contains("no PLC connection is verified", result.Message);
    }

    [Fact]
    public async Task Worker_InspectProject_UnreachableBridge_IsUnsupported()
    {
        var worker = new InjectBridgeWorker(new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = "PlcMcpAbsentInjectBridgePipe",
            Timeout = TimeSpan.FromMilliseconds(400)
        }));

        EngineeringExecutionResult result = await worker.ExecuteAsync(
            new EngineeringJobRequest("j5", EngineeringJobType.InspectProject, PlcVendor.Inovance, @"D:\p"));

        Assert.False(result.Success);
        Assert.Equal(CapabilityStatus.Unsupported, result.Status);
    }

    [Fact]
    public void Worker_CapabilityMatrix_NeverAboveUnsupportedForWriteSurface()
    {
        var worker = new InjectBridgeWorker(new InjectBridgeServices(
            new InjectBridgeClientOptions { PipeName = "PlcMcpAbsentInjectBridgePipe", Timeout = TimeSpan.FromMilliseconds(300) }));

        IReadOnlyDictionary<string, string> capabilities = worker.DescribeCapabilities();

        Assert.False(capabilities.ContainsKey("inject_bridge") &&
                     capabilities["inject_bridge"].Contains("Supported:", StringComparison.Ordinal));
        Assert.Contains("Unsupported", capabilities["inject_bridge"]);
    }

    // ---------------------------------------------------------------
    // Launcher: parameter validation, discovery, PID ownership
    // ---------------------------------------------------------------

    private static InjectLauncherOptions BuildTestOptions(
        string? journalPath = null,
        Func<InjectInjectorCommand, InjectInjectorRunResult>? runner = null,
        Func<int, InjectLiveProcessIdentity?>? lookup = null,
        Func<int, bool>? killer = null,
        IInjectBridgeHandshakeProbe? probe = null,
        Func<string, bool>? fileExists = null)
    {
        string journal = journalPath ?? Path.Combine(Path.GetTempPath(), "plcmcp-inject-tests",
            $"journal-{Guid.NewGuid():N}.jsonl");
        return new InjectLauncherOptions
        {
            AutoShopExecutablePath = @"D:\Inovance Control\AutoShop\AutoShop.exe",
            InjectorExecutablePath = @"C:\fake\AutoShopInjectBridgeInjector.exe",
            BridgeDllPath = @"C:\fake\AutoShopInjectBridge.dll",
            OwnedPidJournalPath = journal,
            Gate = new InjectBridgeGateOptions(),
            InjectorRunner = runner,
            LiveProcessLookup = lookup,
            ProcessTreeKiller = killer,
            HandshakeProbeFactory = probe != null ? _ => probe : null,
            FileExists = fileExists
        };
    }

    private sealed class FakeHandshakeProbe : IInjectBridgeHandshakeProbe
    {
        private readonly bool _answered;
        public FakeHandshakeProbe(bool answered = true) => _answered = answered;

        public Task<InjectBridgeHandshakeReport> HandshakeAsync(string clientName = "PlcMcp", CancellationToken cancellationToken = default)
        {
            InjectBridgeHandshakeReport report = _answered
                ? new InjectBridgeHandshakeReport(true, true, InjectBridgeProtocol.WorkerName, "1.0.0",
                    InjectBridgeProtocol.ProtocolVersion, "x86", "injected", 4242,
                    new[] { "comm_stack_read_only_loopback_first" }, "ok", InjectBridgeProtocol.Disclaimer)
                : new InjectBridgeHandshakeReport(false, false, null, null, null, null, null, null,
                    Array.Empty<string>(), "not answered", InjectBridgeProtocol.Disclaimer);
            return Task.FromResult(report);
        }
    }

    [Fact]
    public void Launcher_Options_Validate_AutoShopNameAndRootedPaths()
    {
        // Validation runs in the constructor; bad shapes must be refused.
        Assert.Throws<ArgumentException>(() => new InjectLauncher(new InjectLauncherOptions
        {
            AutoShopExecutablePath = @"D:\Inovance Control\AutoShop\AutoShopX.exe"
        }));
        Assert.Throws<ArgumentException>(() => new InjectLauncher(new InjectLauncherOptions
        {
            InjectorExecutablePath = "relative\\injector.exe"
        }));
        Assert.Throws<ArgumentException>(() => new InjectLauncher(new InjectLauncherOptions
        {
            BridgeDllPath = "relative\\bridge.dll"
        }));
        // Valid options construct cleanly.
        _ = new InjectLauncher(BuildTestOptions());
    }

    [Fact]
    public void Launcher_Discover_ReportsMissingArtifacts()
    {
        var launcher = new InjectLauncher(BuildTestOptions(fileExists: _ => false));

        InjectDiscoveryResult discovery = launcher.Discover();

        Assert.False(discovery.Ready);
        Assert.False(discovery.InjectorPresent);
        Assert.Contains("build.cmd", discovery.Details);
    }

    [Fact]
    public async Task Launcher_Start_FailsBeforeProcessCreation_WithoutOwnedPid()
    {
        var launcher = new InjectLauncher(BuildTestOptions(
            runner: _ => new InjectInjectorRunResult(2,
                "{\"event\":\"failed\",\"stage\":\"validate-exe\",\"reason\":\"not x86\"}", ""),
            fileExists: _ => true));

        InjectStartResult result = await launcher.StartAsync();

        Assert.False(result.Success);
        Assert.Null(result.ProcessId);
        Assert.Contains("validate-exe", result.Details);
        Assert.Empty(launcher.OwnedInstances);
    }

    [Fact]
    public async Task Launcher_Start_Success_RecordsOwnedPidAndHandshakes()
    {
        int? recordedPid = null;
        var options = BuildTestOptions(
            runner: command =>
            {
                Assert.Contains("--exe", command.Arguments);
                Assert.Contains("AutoShop.exe", command.Arguments);
                Assert.Contains("--allow-force 0", command.Arguments);
                recordedPid = 4242;
                return new InjectInjectorRunResult(0,
                    "{\"event\":\"launched\",\"pid\":4242,\"dllBase\":\"0x04000000\",\"init\":0," +
                    "\"resumed\":true,\"pipeReady\":true,\"pipeName\":\"PlcMcpAutoShopInjectBridge\"}", "");
            },
            probe: new FakeHandshakeProbe(),
            fileExists: _ => true);
        var launcher = new InjectLauncher(options);

        InjectStartResult result = await launcher.StartAsync();

        Assert.True(result.Success, result.Details);
        Assert.Equal(InjectStartOutcome.StartedAndInjected, result.Outcome);
        Assert.Equal(4242, result.ProcessId);
        Assert.True(result.PipeReady);
        Assert.Single(launcher.OwnedInstances);
        Assert.Equal(4242, launcher.OwnedInstances[0].Pid);
        Assert.Equal("AutoShop.exe", launcher.OwnedInstances[0].ProcessName);
        Assert.NotNull(recordedPid);
    }

    [Fact]
    public async Task Launcher_Start_HandshakeFailure_CleansUpTheStartedInstance()
    {
        var killed = new List<int>();
        string startedUtc = DateTime.UtcNow.ToString("o");
        var launcher = new InjectLauncher(BuildTestOptions(
            runner: _ => new InjectInjectorRunResult(0,
                "{\"event\":\"launched\",\"pid\":4242,\"pipeReady\":false," +
                $"\"startedUtc\":\"{startedUtc}\"}}", ""),
            lookup: pid => new InjectLiveProcessIdentity(pid, "AutoShop",
                @"D:\Inovance Control\AutoShop\AutoShop.exe", DateTime.UtcNow),
            killer: pid => { killed.Add(pid); return true; },
            probe: new FakeHandshakeProbe(answered: false),
            fileExists: _ => true));

        InjectStartResult result = await launcher.StartAsync();

        Assert.False(result.Success);
        Assert.Contains("handshake", result.Details);
        Assert.Contains("stopped again", result.Details);
        Assert.Equal(new[] { 4242 }, killed); // the instance this launcher created was stopped
        Assert.Empty(launcher.OwnedInstances);
    }

    [Fact]
    public async Task Launcher_Stop_KillsOnlyOwnershipVerifiedInstances()
    {
        var killed = new List<int>();
        DateTime started = DateTime.UtcNow;
        var options = BuildTestOptions(
            lookup: pid => pid switch
            {
                111 => new InjectLiveProcessIdentity(111, "AutoShop",
                    @"D:\Inovance Control\AutoShop\AutoShop.exe", started),
                222 => new InjectLiveProcessIdentity(222, "AutoShop",
                    @"D:\Inovance Control\AutoShop\AutoShop.exe", started),
                333 => new InjectLiveProcessIdentity(333, "explorer", null, DateTime.UtcNow),
                _ => null
            },
            killer: pid => { killed.Add(pid); return true; });
        var launcher = new InjectLauncher(options);

        // Simulate three owned records: one healthy, one PID-reused (start time drift), one foreign name.
        typeof(InjectLauncher)
            .GetField("_owned", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(launcher, new List<InjectOwnedInstanceRecord>
            {
                new("launch", 111, "AutoShop.exe", @"D:\Inovance Control\AutoShop\AutoShop.exe",
                    "bridge.dll", "pipe", started.ToString("o")),
                new("launch", 222, "AutoShop.exe", @"D:\Inovance Control\AutoShop\AutoShop.exe",
                    "bridge.dll", "pipe", started.AddDays(-30).ToString("o")),
                new("launch", 333, "AutoShop.exe", @"D:\Inovance Control\AutoShop\AutoShop.exe",
                    "bridge.dll", "pipe", DateTime.UtcNow.ToString("o"))
            });

        InjectStopResult result = await launcher.StopAsync();

        Assert.Equal(new[] { 111 }, killed); // 222 refused (start-time mismatch), 333 refused (name mismatch)
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(InjectStopOutcome.Killed, result.Entries[0].Outcome);
        Assert.Equal(InjectStopOutcome.Refused, result.Entries[1].Outcome);
        Assert.Contains("NOT killed", result.Entries[1].Reason);
        Assert.Equal(InjectStopOutcome.Refused, result.Entries[2].Outcome);
    }

    [Fact]
    public async Task Launcher_Stop_AlreadyExited_IsReportedNotFailed()
    {
        var options = BuildTestOptions(
            lookup: _ => null,
            killer: _ => true);
        var launcher = new InjectLauncher(options);
        typeof(InjectLauncher)
            .GetField("_owned", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(launcher, new List<InjectOwnedInstanceRecord>
            {
                new("launch", 111, "AutoShop.exe", @"D:\Inovance Control\AutoShop\AutoShop.exe",
                    "bridge.dll", "pipe", DateTime.UtcNow.ToString("o"))
            });

        InjectStopResult result = await launcher.StopAsync();

        Assert.True(result.Success);
        Assert.Equal(InjectStopOutcome.AlreadyExited, result.Entries[0].Outcome);
    }

    [Fact]
    public async Task Launcher_StopFromJournal_OnlyTouchesVerifiedLaunchRecords()
    {
        string journal = Path.Combine(Path.GetTempPath(), "plcmcp-inject-tests",
            $"journal-{Guid.NewGuid():N}.jsonl");
        _tempFiles.Add(journal);
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        DateTime started = DateTime.UtcNow;
        await File.WriteAllLinesAsync(journal, new[]
        {
            JsonSerializer.Serialize(new InjectOwnedInstanceRecord("launch", 555, "AutoShop.exe",
                @"D:\Inovance Control\AutoShop\AutoShop.exe", "AutoShopInjectBridge.dll",
                InjectBridgeProtocol.PipeName, started.ToString("o"))),
            JsonSerializer.Serialize(new InjectOwnedInstanceRecord("launch", 666, "AutoShop.exe",
                @"D:\Inovance Control\AutoShop\AutoShop.exe", "AutoShopInjectBridge.dll",
                InjectBridgeProtocol.PipeName, started.AddDays(-90).ToString("o"))),
            JsonSerializer.Serialize(new InjectOwnedInstanceRecord("stop", 777, "AutoShop.exe",
                @"D:\Inovance Control\AutoShop\AutoShop.exe", "AutoShopInjectBridge.dll",
                InjectBridgeProtocol.PipeName, started.ToString("o")))
        });

        var killed = new List<int>();
        var options = BuildTestOptions(journalPath: journal,
            lookup: pid => pid switch
            {
                555 => new InjectLiveProcessIdentity(555, "AutoShop",
                    @"D:\Inovance Control\AutoShop\AutoShop.exe", started),
                666 => new InjectLiveProcessIdentity(666, "explorer", null, started), // PID reused
                _ => null
            },
            killer: pid => { killed.Add(pid); return true; });
        var launcher = new InjectLauncher(options);

        InjectStopResult result = await launcher.StopFromJournalAsync();

        Assert.Equal(new[] { 555 }, killed); // 666: identity mismatch -> refused; 777: stop record -> skipped
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(InjectStopOutcome.Killed, result.Entries[0].Outcome);
        Assert.Equal(InjectStopOutcome.Refused, result.Entries[1].Outcome);
    }

    // ---------------------------------------------------------------
    // G2 download closed loop: byte-exact frame construction + payload
    // plan + loopback gates (docs/inovance-autoshop-download-closedloop.md).
    // All fixtures below are the EXACT bytes captured against the local
    // h5u simulator daemon on 2026-10-29 (loopback only).
    // ---------------------------------------------------------------

    [Fact]
    public void ClosedLoop_RunFrame_MatchesCapturedWireBytes()
    {
        // captured tx: header 16 00 | 02 01 | 00*4 | payload 02 00 20 00 00 ff 04 00 00 00 01 00 00 00
        Assert.Equal(
            "16000201000000000200200000FF0400000001000000",
            Convert.ToHexString(SimulatorControlFrames.BuildRun()));
    }

    [Fact]
    public void ClosedLoop_StopAndLoadFrames_MatchCapturedWireBytes()
    {
        Assert.Equal(
            "16000201000000000200200000FF0400000000000000",
            Convert.ToHexString(SimulatorControlFrames.BuildStop()));
        Assert.Equal(
            "16000201000000000100100000FF0400000000000000",
            Convert.ToHexString(SimulatorControlFrames.BuildLoadPoll()));
        Assert.Equal(
            "16000201000000000200100000FF0400000001000000",
            Convert.ToHexString(SimulatorControlFrames.BuildLoadTrigger()));
    }

    [Fact]
    public void ClosedLoop_RegisterSubType_Validation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SimulatorControlFrames.BuildRegisterCommand(0x03, 0xFF000020, 1));
        // read/write sub-types accepted:
        Assert.NotEmpty(SimulatorControlFrames.BuildRegisterCommand(SimulatorControlFrames.ReadSubType, 0xFF000010, 0));
    }

    [Fact]
    public void ClosedLoop_ParseReply_AllObservedShapes()
    {
        // [06] ack (RUN/STOP)
        SimulatorControlReply ack = SimulatorControlFrames.ParseReply(new byte[] { 0x06 });
        Assert.True(ack.Accepted);
        Assert.Equal(SimulatorControlKind.Ack, ack.Kind);

        // load trigger rejection: payload 0f 00 82 00 00 00 -> status 0x82 (LoadRestart stub)
        SimulatorControlReply stub = SimulatorControlFrames.ParseReply(new byte[] { 0x0F, 0x00, 0x82, 0x00, 0x00, 0x00 });
        Assert.False(stub.Accepted);
        Assert.Equal(SimulatorControlKind.Error, stub.Kind);
        Assert.Equal((ushort)0x82, stub.Status);

        // load poll value: payload 05 00 00 00 00 00 -> value 0
        SimulatorControlReply value = SimulatorControlFrames.ParseReply(new byte[] { 0x05, 0x00, 0x00, 0x00, 0x00, 0x00 });
        Assert.True(value.Accepted);
        Assert.Equal(0u, value.Value);

        Assert.Equal(SimulatorControlKind.Unknown, SimulatorControlFrames.ParseReply(Array.Empty<byte>()).Kind);
    }

    [Fact]
    public void ClosedLoop_SessionFrame_MatchesCapturedFileTransferBytes()
    {
        // D1 doc §2.3 measured shape (session command: chunk 64, 7-byte content);
        // with the 20-char remote name "project\project.hcpp" the total frame is
        // 8+18+20+1 = 47 = 0x2F (the doc's 0x26 sample line miscounted the name).
        var file = new DownloadPayloadFile(DownloadPayloadPlan.ProjectHcppRemoteName, "Empty\r\n"u8.ToArray());
        byte[] frame = DownloadPayloadPlan.BuildSessionFrame(file, 64);
        Assert.Equal(
            "2F00020400000000020000000000000000004000000007000000"
            + "70726F6A6563745C70726F6A6563742E6863707000",
            Convert.ToHexString(frame));
    }

    [Fact]
    public void ClosedLoop_PayloadPlan_MinimalProjectIsHcppByteIdentity()
    {
        byte[] hcpp = new byte[64];
        "PPCH"u8.CopyTo(hcpp);
        IReadOnlyList<DownloadPayloadFile> plan = DownloadPayloadPlan.ForRepackedProject(hcpp);
        Assert.Single(plan);
        Assert.Equal(@"project\project.hcpp", plan[0].RemoteName);
        Assert.True(plan[0].Bytes.ToArray().SequenceEqual(hcpp)); // verbatim — no re-encoding
    }

    [Fact]
    public void ClosedLoop_PayloadPlan_RejectsRawNonContainerBlob()
    {
        // a .compile artifact (SAJ... header) is NOT a valid single-file payload
        byte[] compileBlob = { 0x53, 0x41, 0x4A, 0x03, 0x00, 0x02, 0x00, 0x01, 0x50 };
        Assert.Throws<ArgumentException>(() => DownloadPayloadPlan.ForRepackedProject(compileBlob));
        Assert.Throws<ArgumentException>(() => DownloadPayloadPlan.EnsureHcppContainer(new byte[2]));
    }

    [Fact]
    public void ClosedLoop_PayloadPlan_FullOrderFollowsVendorCallSites()
    {
        byte[] hcpp = new byte[8];
        "PPCH"u8.CopyTo(hcpp);
        var artifacts = new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["symbol.sym"] = new byte[] { 1 },
            ["stprog.o"] = new byte[] { 2 },
            // userprog.foid omitted deliberately (LD-only projects lack members)
        };
        IReadOnlyList<DownloadPayloadFile> plan = DownloadPayloadPlan.ForFullProject(hcpp, artifacts);

        Assert.Equal(@"project\project.hcpp", plan[0].RemoteName);
        Assert.Equal("stprog.o", plan[1].RemoteName); // vendor order: stprog.o before symbol.sym
        Assert.Equal("symbol.sym", plan[2].RemoteName);
    }

    [Fact]
    public void ClosedLoop_SessionFrame_ChunkBoundsValidated()
    {
        var file = new DownloadPayloadFile("stprog.o", new byte[4]);
        Assert.Throws<ArgumentOutOfRangeException>(() => DownloadPayloadPlan.BuildSessionFrame(file, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => DownloadPayloadPlan.BuildSessionFrame(file, PlcMcp.Engineering.Workers.AutoShop.DownloadProtocol.DownloadFrame.MaxPayloadLength + 1));
    }

    private sealed class ScriptedControlChannel : ISimulatorControlChannel
    {
        public List<byte[]> Sent { get; } = new();
        private readonly Func<byte[], byte[]> _reply;
        public ScriptedControlChannel(Func<byte[], byte[]> reply) => _reply = reply;
        public Task<SimulatorControlReply> SendAsync(byte[] frame, CancellationToken cancellationToken = default)
        {
            Sent.Add(frame);
            return Task.FromResult(SimulatorControlFrames.ParseReply(_reply(frame)));
        }
    }

    [Fact]
    public async Task ClosedLoop_Runner_ReplaysCapturedDaemonReplies()
    {
        // replays the 2026-10-29 daemon behaviour: load trigger -> [0F][82];
        // RUN/STOP -> [06]; download succeeds
        var control = new ScriptedControlChannel(frame =>
        {
            string hex = Convert.ToHexString(frame);
            if (hex.Contains("02001000")) return new byte[] { 0x0F, 0x00, 0x82, 0x00 };   // load trigger stub 0x82
            return new byte[] { 0x06 };                                                   // control word ack
        });
        byte[] hcpp = new byte[16];
        "PPCH"u8.CopyTo(hcpp);
        var plan = DownloadPayloadPlan.ForRepackedProject(hcpp);

        DownloadClosedLoopResult result = await AutoShopDownloadClosedLoop.RunAsync(
            (file, _) => Task.FromResult((true, $"{file.RemoteName}: {file.Bytes.Length} bytes transferred.")),
            control, plan);

        Assert.True(result.Downloaded);
        Assert.Equal((ushort)0x82, result.LoadTrigger!.Value.Status); // load is stub on the simulator
        Assert.Equal(SimulatorControlKind.Ack, result.Run!.Value.Kind);
        Assert.Equal(SimulatorControlKind.Ack, result.Stop!.Value.Kind);
        Assert.Equal(3, control.Sent.Count);                          // trigger, RUN, STOP
        Assert.Contains(result.Steps, s => s.StartsWith("RUN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClosedLoop_Runner_DownloadFailure_SendsNoControlFrames()
    {
        var control = new ScriptedControlChannel(_ => new byte[] { 0x06 });
        var plan = DownloadPayloadPlan.ForRepackedProject("PPCH...."u8.ToArray());

        DownloadClosedLoopResult result = await AutoShopDownloadClosedLoop.RunAsync(
            (_, _) => Task.FromResult((false, "session rejected")),
            control, plan);

        Assert.False(result.Downloaded);
        Assert.Empty(control.Sent); // never RUN a PLC/simulator after a failed transfer
        Assert.Null(result.Run);
    }

    [Fact]
    public async Task ClosedLoop_Runner_EmptyPlanRejected()
    {
        var control = new ScriptedControlChannel(_ => new byte[] { 0x06 });
        await Assert.ThrowsAsync<ArgumentException>(
            () => AutoShopDownloadClosedLoop.RunAsync((_, _) => Task.FromResult((true, "x")), control, Array.Empty<DownloadPayloadFile>()));
    }

    // ---------------------------------------------------------------
    // G3 integration anchors: the step-wording the Server tools surface verbatim,
    // plus the env-gated real loopback daemon run (LocalSimulator, skipped offline).
    // ---------------------------------------------------------------

    [Fact]
    public async Task ClosedLoop_Runner_NoStop_SkipsStopFrame_AndStubIsDescribedHonestly()
    {
        // The G3 Server report claims two things it must be able to anchor here:
        // (1) includeStop=false sends exactly TWO control frames (trigger + RUN, no STOP);
        // (2) the load step surfaces the stub as "daemon status 0x82", never as success.
        var control = new ScriptedControlChannel(frame =>
            Convert.ToHexString(frame).Contains("02001000")
                ? new byte[] { 0x0F, 0x00, 0x82, 0x00 }
                : new byte[] { 0x06 });
        byte[] hcpp = new byte[32];
        "PPCH"u8.CopyTo(hcpp);

        DownloadClosedLoopResult result = await AutoShopDownloadClosedLoop.RunAsync(
            (file, _) => Task.FromResult((true, $"{file.RemoteName}: {file.Bytes.Length} bytes transferred.")),
            control, DownloadPayloadPlan.ForRepackedProject(hcpp), includeStop: false);

        Assert.True(result.Downloaded);
        Assert.Equal(2, control.Sent.Count);
        Assert.Null(result.Stop);
        Assert.DoesNotContain(result.Steps, s => s.StartsWith("STOP", StringComparison.Ordinal));
        string loadStep = Assert.Single(result.Steps, s => s.StartsWith("load trigger", StringComparison.Ordinal));
        Assert.Contains("daemon status 0x82", loadStep, StringComparison.Ordinal);
        Assert.False(result.LoadTrigger!.Value.Accepted); // the stub is an ERROR reply, not acceptance
    }

    [Fact]
    public async Task ClosedLoop_Runner_RunUnanswered_ResultCarriesNoFakeRun()
    {
        // A daemon that answers RUN with an unknown/absent reply must surface as such —
        // the Server tool keys Success on Run.Accepted, so this shape is the failure anchor.
        var control = new ScriptedControlChannel(_ => Array.Empty<byte>());
        byte[] hcpp = new byte[8];
        "PPCH"u8.CopyTo(hcpp);

        DownloadClosedLoopResult result = await AutoShopDownloadClosedLoop.RunAsync(
            (_, _) => Task.FromResult((true, "ok")), control,
            DownloadPayloadPlan.ForRepackedProject(hcpp), includeStop: false);

        Assert.True(result.Downloaded);
        Assert.NotNull(result.Run);
        Assert.False(result.Run!.Value.Accepted);
        Assert.Equal(SimulatorControlKind.Unknown, result.Run!.Value.Kind);
        Assert.Contains(result.Steps, s => s.Contains("no/unknown answer", StringComparison.Ordinal));
    }

    [Fact]
    public void SimulatorRegisterClient_NonLoopbackTarget_RefusedBeforeConnect()
    {
        // red line: RUN frames must never leave 127.0.0.0/8
        Assert.Throws<InvalidOperationException>(() => SimulatorRegisterClient.Create("8.8.8.8"));
        Assert.Throws<InvalidOperationException>(() => SimulatorRegisterClient.Create("localhost.localdomain4"));
        // loopback accepted:
        using var ok = SimulatorRegisterClient.Create("127.0.0.1", 12939);
        Assert.NotNull(ok);
    }

    [Fact]
    public async Task Services_WriteReservation_Download_GateOpen_BridgeG2Receipt_SurfacesAsHonestUnsupported()
    {
        // the upgraded bridge answer (G2 verdict, -32006) must map to honest
        // Unsupported on the C# side — never to success
        var mock = StartMock(reply => reply.Method == InjectBridgeProtocol.MethodDownload
            ? BuildError(reply, InjectBridgeProtocol.ErrUnsupported,
                "Unsupported: the in-process vendor Download export was proven non-transferring standalone "
                + "(G2 harness: rc=0 accepted, zero wire frames); the loopback closed loop runs on the "
                + "D1 12939 protocol layer (file transfer + 0xFF000010 load + 0xFF000020 RUN)")
            : BuildResult(reply, "{}"));
        var services = new InjectBridgeServices(new InjectBridgeClientOptions
        {
            PipeName = mock.PipeName,
            Gate = new InjectBridgeGateOptions { AllowDownload = true },
            Timeout = TimeSpan.FromSeconds(3)
        });

        InjectBridgeWriteReport report = await services.WriteReservationAsync(
            InjectBridgeProtocol.MethodDownload, "project\\project.hcpp", target: "127.0.0.1");

        Assert.False(report.Succeeded);
        Assert.True(report.GateOpen);
        Assert.True(report.HonestUnsupported);
        Assert.Contains("-32006", report.Message);
        Assert.Contains(mock.ReceivedRequests, r => r.Method == InjectBridgeProtocol.MethodDownload);
    }

    // ---------------------------------------------------------------
    // Real injection (env-gated; never runs offline)
    // ---------------------------------------------------------------

    [LocalSimulatorFact]
    [Trait("LocalSimulator", "1")]
    public async Task RealInjection_AgainstLocalAutoShop_InstanceStartsAndBridgeAnswers()
    {
        // Requires: built native artifacts (tools\AutoShopInjectBridge\build.cmd)
        // and a local AutoShop installation. Skipped unless PLCMCP_LOCAL_SIMULATOR=1.
        var launcher = new InjectLauncher();
        InjectDiscoveryResult discovery = launcher.Discover();
        if (!discovery.Ready)
        {
            return; // native artifacts not built on this machine: nothing to prove here
        }

        InjectStartResult start = await launcher.StartAsync();
        Assert.True(start.Success, start.Details);
        try
        {
            var services = new InjectBridgeServices(new InjectBridgeClientOptions
            {
                PipeName = start.PipeName!,
                Timeout = TimeSpan.FromSeconds(5)
            });
            InjectBridgeCapabilityMatrix capabilities = await services.CapabilitiesAsync();
            Assert.True(capabilities.Succeeded);
            Assert.Contains("Experimental", capabilities.Compile); // F1: the compile gate is open (in-process chain)
        }
        finally
        {
            InjectStopResult stop = await launcher.StopAsync();
            Assert.True(stop.Success, stop.Details);
        }
    }

    [LocalSimulatorFact]
    [Trait("LocalSimulator", "1")]
    public async Task RealLoop_RunStopControlWords_OnLocalSimulatorDaemon()
    {
        // G3 real-chain anchor (env-gated, skipped offline): RUN/STOP control words against
        // a locally RUNNING h5u daemon on 127.0.0.1:12939 ONLY (both clients hard-refuse
        // non-loopback; zero physical PLC). Deliberately NO file download here: the
        // byte-exact download landing is already G2 evidence
        // (docs/inovance-autoshop-download-closedloop.md §3.3, workspace copy) and a
        // synthetic payload would clobber a user-facing daemon workspace. Connections are
        // disposed here (self-clean); STOP leaves register values retained (G2, honest).
        if (!IsLoopbackPortOpen(12939))
        {
            return; // no daemon listening on loopback 12939: nothing to prove on this machine
        }
        using var ctl = SimulatorRegisterClient.Create("127.0.0.1", 12939);
        SimulatorControlReply run = await ctl.RunAsync();
        Assert.True(run.Accepted, $"RUN control word must be acked by the daemon (G2 §3.4): {run.Kind} {run.Status}");
        SimulatorControlReply poll = await ctl.PollLoadStatusAsync();
        Assert.True(poll.Accepted);
        SimulatorControlReply stop = await ctl.StopAsync();
        Assert.True(stop.Accepted);
    }

    private static bool IsLoopbackPortOpen(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            return client.ConnectAsync(System.Net.IPAddress.Loopback, port).Wait(1500);
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------
    // Mock pipe server (loopback named pipe, one client at a time)
    // ---------------------------------------------------------------

    private sealed class ReceivedRequest
    {
        public string Id { get; set; } = "1";
        public string Method { get; set; } = string.Empty;
        public string? Client { get; set; }
        public string? ParamsJson { get; set; }
    }

    private static string BuildResult(ReceivedRequest request, string resultJson) =>
        $"{{\"id\":\"{request.Id}\",\"result\":{resultJson}}}";

    private static string BuildError(ReceivedRequest request, int code, string message) =>
        $"{{\"id\":\"{request.Id}\",\"error\":{{\"code\":{code},\"message\":\"{message}\"}}}}";

    private sealed class MockInjectBridgeServer : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly Func<ReceivedRequest, string?> _responder;
        private readonly bool _closeImmediately;

        public string PipeName { get; }
        public List<ReceivedRequest> ReceivedRequests { get; } = new();
        public string? LastMethod { get; private set; }
        public string? LastClient { get; private set; }

        public MockInjectBridgeServer(
            string pipeName,
            Func<ReceivedRequest, string?> responder,
            bool closeImmediately = false)
        {
            PipeName = pipeName;
            _responder = responder;
            _closeImmediately = closeImmediately;
            _pipe = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _ = Task.Run(ServeLoopAsync);
        }

        private async Task ServeLoopAsync()
        {
            // Raw byte I/O with manual line framing (mirrors the client).
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
                                request.ParamsJson = paramsElement.GetRawText();
                                if (paramsElement.TryGetProperty("client", out JsonElement clientElement))
                                {
                                    request.Client = clientElement.GetString();
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

    private MockInjectBridgeServer StartMock(
        Func<ReceivedRequest, string?> responder,
        bool closeImmediately = false)
    {
        var mock = new MockInjectBridgeServer(
            "PlcMcpTestInjectBridge_" + Guid.NewGuid().ToString("N")[..12], responder, closeImmediately);
        _servers.Add(mock);
        return mock;
    }
}
