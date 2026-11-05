using System.Net;
using System.Net.Sockets;
using System.Text;
using PlcMcp.Adapters;
using PlcMcp.Adapters.Protocols;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Tests;

/// <summary>
/// Loopback tests for the Keyence HostLink ASCII protocol (port 8501).
/// Exercises RD (single-word read), RDS (sequence-word read), WR (single-word write),
/// probe handshake, safety guards, address validation, and response parsing.
/// </summary>
public sealed class KeyenceHostLinkLoopbackTests
{
    private static TargetProfile Target(string family, int port, int timeout = 2000, TransportKind transport = TransportKind.KvHostLinkTcp) =>
        new("fixture", PlcVendor.Keyence, family, null, null,
            new("127.0.0.1", port, transport, TimeoutMs: timeout),
            [transport], null, new([]), "physical-readonly");

    private static TagDefinition WordTag(string address, PlcDataType type = PlcDataType.Int16, bool canWrite = false,
        SafetyClass safetyClass = SafetyClass.ReadOnly, double? min = null, double? max = null) =>
        new("word", address, type, Minimum: min, Maximum: max, CanWrite: canWrite, SafetyClass: safetyClass);

    private static async Task<string> ReadAsciiCommand(NetworkStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct);
            if (read == 0) break;
            if (buffer[0] == (byte)'\r')
            {
                break;
            }
            ms.WriteByte(buffer[0]);
        }
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    private static async Task WithServer(Func<NetworkStream, CancellationToken, Task> server, Func<int, Task> client)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var task = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            await server(connection.GetStream(), timeout.Token);
        });
        try
        {
            await client(((IPEndPoint)listener.LocalEndpoint).Port);
            await task;
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            try { await task; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task KeyenceHostLink_ReadsSingleDmWordThroughConfiguredRuntime()
    {
        await WithServer(async (stream, ct) =>
        {
            var cmd = await ReadAsciiCommand(stream, ct);
            Assert.Equal("RD DM100", cmd);
            var reply = Encoding.ASCII.GetBytes("00123\r\n");
            await stream.WriteAsync(reply, ct);
        }, async port =>
        {
            var host = ConfiguredPlcComposition.Create(new(1,
                [new("fixture", PlcVendor.Keyence, "KV-8000", new("127.0.0.1", port, TransportKind.KvHostLinkTcp),
                    [WordTag("D100")])]));
            Assert.Equal(CapabilityStatus.Experimental, host.Service.GetTarget("fixture").Capabilities.GetStatus("read_tags"));
            var values = await host.Service.ReadAsync("fixture", ["word"]);

            Assert.Equal((short)123, Assert.IsType<short>(Assert.Single(values).Value));
            Assert.Equal(QualityCode.Good, Assert.Single(values).Quality);
        });
    }

    [Fact]
    public async Task KeyenceHostLink_ReadsUnsignedWord()
    {
        await WithServer(async (stream, ct) =>
        {
            var cmd = await ReadAsciiCommand(stream, ct);
            Assert.Equal("RD DM100", cmd);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("64969\r"), ct);
        }, async port =>
        {
            var result = await new KeyenceHostLinkTcpAdapter().ReadAsync(Target("KV-8000", port),
                [WordTag("D100", PlcDataType.UInt16)]);
            Assert.Equal((ushort)64969, Assert.IsType<ushort>(Assert.Single(result).Value));
        });
    }

    [Fact]
    public async Task KeyenceHostLink_ReadsNegativeInt16_BothUnsignedAndSignedText()
    {
        // 1. Simulator returns 5-digit decimal 64969 for -567 (16-bit modular representation)
        await WithServer(async (stream, ct) =>
        {
            Assert.Equal("RD DM100", await ReadAsciiCommand(stream, ct));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("64969\r\n"), ct);
        }, async port =>
        {
            var result = await new KeyenceHostLinkTcpAdapter().ReadAsync(Target("KV-8000", port),
                [WordTag("D100", PlcDataType.Int16)]);
            Assert.Equal((short)-567, Assert.IsType<short>(Assert.Single(result).Value));
        });

        // 2. Tolerates signed format if returned by some devices
        await WithServer(async (stream, ct) =>
        {
            Assert.Equal("RD DM100", await ReadAsciiCommand(stream, ct));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("-567\r\n"), ct);
        }, async port =>
        {
            var result = await new KeyenceHostLinkTcpAdapter().ReadAsync(Target("KV-8000", port),
                [WordTag("D100", PlcDataType.Int16)]);
            Assert.Equal((short)-567, Assert.IsType<short>(Assert.Single(result).Value));
        });
    }

    [Fact]
    public async Task KeyenceHostLink_ReadsConsecutiveWordsInSingleRdsFrame()
    {
        await WithServer(async (stream, ct) =>
        {
            // D100, D101, D102 consecutive -> single RDS frame with count 3
            var cmd = await ReadAsciiCommand(stream, ct);
            Assert.Equal("RDS DM100 3", cmd);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("01234 00000 00567\r\n"), ct);
        }, async port =>
        {
            var result = await new KeyenceHostLinkTcpAdapter().ReadAsync(Target("KV-8000", port),
                [WordTag("D100"), WordTag("D101"), WordTag("D102")]);
            Assert.Equal(3, result.Count);
            Assert.Equal((short)1234, Assert.IsType<short>(result[0].Value));
            Assert.Equal((short)0, Assert.IsType<short>(result[1].Value));
            Assert.Equal((short)567, Assert.IsType<short>(result[2].Value));
        });
    }

    [Fact]
    public async Task KeyenceHostLink_SplitsGappedAddressesIntoMultipleRdFrames()
    {
        int frames = 0;
        await WithServer(async (stream, ct) =>
        {
            Assert.Equal("RD DM100", await ReadAsciiCommand(stream, ct)); frames++;
            await stream.WriteAsync(Encoding.ASCII.GetBytes("00100\r"), ct);
            Assert.Equal("RD DM105", await ReadAsciiCommand(stream, ct)); frames++;
            await stream.WriteAsync(Encoding.ASCII.GetBytes("00105\r"), ct);
        }, async port =>
        {
            var result = await new KeyenceHostLinkTcpAdapter().ReadAsync(Target("KV-8000", port),
                [WordTag("D100"), WordTag("D105")]);
            Assert.Equal(2, frames);
            Assert.Equal((short)100, Assert.IsType<short>(result[0].Value));
            Assert.Equal((short)105, Assert.IsType<short>(result[1].Value));
        });
    }

    [Fact]
    public async Task KeyenceHostLink_KeepsInputOrderForUnsortedBatchedTags()
    {
        await WithServer(async (stream, ct) =>
        {
            // Requested D101 then D100 -> merged into one RDS frame starting at D100
            Assert.Equal("RDS DM100 2", await ReadAsciiCommand(stream, ct));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("00100 00101\r\n"), ct);
        }, async port =>
        {
            var result = await new KeyenceHostLinkTcpAdapter().ReadAsync(Target("KV-8000", port),
                [WordTag("D101"), WordTag("D100")]);
            Assert.Equal(2, result.Count);
            Assert.Equal((short)101, Assert.IsType<short>(result[0].Value));
            Assert.Equal((short)100, Assert.IsType<short>(result[1].Value));
        });
    }

    [Fact]
    public async Task KeyenceHostLink_Probe_AcknowledgesRdDm0()
    {
        await WithServer(async (stream, ct) =>
        {
            var cmd = await ReadAsciiCommand(stream, ct);
            Assert.Equal("RD DM0", cmd);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("00000\r\n"), ct);
        }, async port =>
        {
            var probe = await new KeyenceHostLinkTcpAdapter().ProbeAsync(Target("KV-8000", port));
            Assert.True(probe.Reachable);
            Assert.False(probe.IsSimulation);
            Assert.Equal("HOSTLINK_CONNECTED", probe.State);
        });
    }

    [Fact]
    public async Task KeyenceHostLink_Probe_ReportsTcpConnectedOnUnexpectedResponse()
    {
        await WithServer(async (stream, ct) =>
        {
            Assert.Equal("RD DM0", await ReadAsciiCommand(stream, ct));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("E0\r\n"), ct);
        }, async port =>
        {
            var probe = await new KeyenceHostLinkTcpAdapter().ProbeAsync(Target("KV-8000", port));
            Assert.True(probe.Reachable);
            Assert.Equal("TCP_CONNECTED", probe.State);
            Assert.Contains("E0", probe.Message);
        });
    }

    [Fact]
    public async Task KeyenceHostLink_WritesSingleWord_ParameterWrite_PositiveAndNegative()
    {
        // 1. Positive value
        await WithServer(async (stream, ct) =>
        {
            var cmd = await ReadAsciiCommand(stream, ct);
            Assert.Equal("WR DM100 1234", cmd);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("OK\r\n"), ct);
        }, async port =>
        {
            var adapter = new KeyenceHostLinkTcpAdapter();
            var tag = WordTag("DM100", PlcDataType.Int16, canWrite: true, safetyClass: SafetyClass.Parameter);
            var result = await adapter.WriteParameterAsync(Target("KV-8000", port), tag, (short)1234);
            Assert.Equal((short)1234, result.Value);
            Assert.Equal(QualityCode.Good, result.Quality);
        });

        // 2. Negative value
        await WithServer(async (stream, ct) =>
        {
            var cmd = await ReadAsciiCommand(stream, ct);
            Assert.Equal("WR DM100 -567", cmd);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("OK\r"), ct);
        }, async port =>
        {
            var adapter = new KeyenceHostLinkTcpAdapter();
            var tag = WordTag("DM100", PlcDataType.Int16, canWrite: true, safetyClass: SafetyClass.Parameter);
            var result = await adapter.WriteParameterAsync(Target("KV-8000", port), tag, (short)-567);
            Assert.Equal((short)-567, result.Value);
            Assert.Equal(QualityCode.Good, result.Quality);
        });
    }

    [Fact]
    public async Task KeyenceHostLink_Write_RejectsNonOkReply()
    {
        await WithServer(async (stream, ct) =>
        {
            Assert.Equal("WR DM100 10", await ReadAsciiCommand(stream, ct));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("E0\r\n"), ct);
        }, async port =>
        {
            var adapter = new KeyenceHostLinkTcpAdapter();
            var tag = WordTag("DM100", PlcDataType.Int16, canWrite: true, safetyClass: SafetyClass.Parameter);
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
                adapter.WriteParameterAsync(Target("KV-8000", port), tag, (short)10));
            Assert.Contains("E0", ex.Message);
        });
    }

    [Fact]
    public async Task KeyenceHostLink_RejectsNonWordTypeBeforeConnecting()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new KeyenceHostLinkTcpAdapter().ReadAsync(
            Target("KV-8000", port: 1), [WordTag("D100", PlcDataType.Real)]));
        await Assert.ThrowsAsync<ArgumentException>(() => new KeyenceHostLinkTcpAdapter().ReadAsync(
            Target("KV-8000", port: 1), [WordTag("D100", PlcDataType.Bool)]));
    }

    [Fact]
    public async Task KeyenceHostLink_RejectsBadDmAddressBeforeConnecting()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new KeyenceHostLinkTcpAdapter().ReadAsync(
            Target("KV-8000", port: 1), [WordTag("Q100")]));
        await Assert.ThrowsAsync<ArgumentException>(() => new KeyenceHostLinkTcpAdapter().ReadAsync(
            Target("KV-8000", port: 1), [WordTag("ABC")]));
    }

    [Fact]
    public async Task KeyenceHostLink_ReadDeadlineClosesStalledConnection()
    {
        await WithServer(async (stream, ct) =>
        {
            await ReadAsciiCommand(stream, ct);
            var oneByte = new byte[1];
            Assert.Equal(0, await stream.ReadAsync(oneByte, ct));
        }, async port => await Assert.ThrowsAsync<TimeoutException>(() => new KeyenceHostLinkTcpAdapter().ReadAsync(
            Target("KV-8000", port, timeout: 150), [WordTag("D100")])));
    }

    [Fact]
    public void KeyenceHostLink_ResponseVerification_DetectsMismatchesAndErrors()
    {
        // Check word count mismatch (expected 3, got 2)
        var ex1 = Assert.Throws<InvalidDataException>(() =>
            KeyenceHostLinkTcpAdapter.ParseReadWords("00001 00002", 3));
        Assert.Contains("expected 3 words but received 2", ex1.Message);

        // Check error response E0
        var ex2 = Assert.Throws<InvalidDataException>(() =>
            KeyenceHostLinkTcpAdapter.ParseReadWords("E0", 1));
        Assert.Contains("error response", ex2.Message);

        // Check non-numeric token
        var ex3 = Assert.Throws<InvalidDataException>(() =>
            KeyenceHostLinkTcpAdapter.ParseReadWords("00001 INVALID", 2));
        Assert.Contains("invalid word token", ex3.Message);

        // VerifyWriteResponse accepts OK (case insensitive) and rejects others
        KeyenceHostLinkTcpAdapter.VerifyWriteResponse("OK");
        KeyenceHostLinkTcpAdapter.VerifyWriteResponse("OK\r\n");
        KeyenceHostLinkTcpAdapter.VerifyWriteResponse("ok");
        var ex4 = Assert.Throws<InvalidDataException>(() =>
            KeyenceHostLinkTcpAdapter.VerifyWriteResponse("E1"));
        Assert.Contains("E1", ex4.Message);
    }

    [Fact]
    public void KeyenceHostLink_WriteSafetyGuards_RejectInvalidTagsAndRanges()
    {
        // Non-parameter safety class
        var readOnlyTag = WordTag("DM100", PlcDataType.Int16, canWrite: true, safetyClass: SafetyClass.ReadOnly);
        Assert.Throws<InvalidOperationException>(() => KeyenceHostLinkTcpAdapter.BuildWrite(readOnlyTag, (short)10));

        var actuatorTag = WordTag("DM100", PlcDataType.Int16, canWrite: true, safetyClass: SafetyClass.Actuator);
        Assert.Throws<InvalidOperationException>(() => KeyenceHostLinkTcpAdapter.BuildWrite(actuatorTag, (short)10));

        // Bool tag
        var boolTag = WordTag("DM100", PlcDataType.Bool, canWrite: true, safetyClass: SafetyClass.Parameter);
        Assert.Throws<InvalidOperationException>(() => KeyenceHostLinkTcpAdapter.BuildWrite(boolTag, true));

        // Value out of range
        var rangedTag = WordTag("DM100", PlcDataType.Int16, canWrite: true, safetyClass: SafetyClass.Parameter, min: 0, max: 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyenceHostLinkTcpAdapter.BuildWrite(rangedTag, (short)-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => KeyenceHostLinkTcpAdapter.BuildWrite(rangedTag, (short)101));
    }

    [Fact]
    public void KeyenceHostLink_BuildCommand_TerminatesWithCr()
    {
        var readCmd = KeyenceHostLinkTcpAdapter.BuildReadCommand(100);
        Assert.Equal("RD DM100\r", readCmd);

        var rdsCmd = KeyenceHostLinkTcpAdapter.BuildReadSequenceCommand(100, 5);
        Assert.Equal("RDS DM100 5\r", rdsCmd);

        var tag = WordTag("DM200", PlcDataType.Int16, canWrite: true, safetyClass: SafetyClass.Parameter);
        var writeCmd = KeyenceHostLinkTcpAdapter.BuildWriteCommand(tag, (short)999);
        Assert.Equal("WR DM200 999\r", writeCmd);
    }

    [Fact]
    public void KeyenceHostLink_SelectAdapter_ResolvesByTransportAndPort()
    {
        IPlcProtocolAdapter[] adapters = [new KeyenceAclTcpAdapter(), new KeyenceHostLinkTcpAdapter()];

        // Explicit KvHostLinkTcp transport
        var adapter1 = ConfiguredPlcComposition.SelectAdapter(adapters, TransportKind.KvHostLinkTcp, PlcVendor.Keyence);
        Assert.IsType<KeyenceHostLinkTcpAdapter>(adapter1);

        // KvCommTcp on port 8501 resolves to HostLink adapter
        var adapter2 = ConfiguredPlcComposition.SelectAdapter(adapters, TransportKind.KvCommTcp, PlcVendor.Keyence, port: 8501);
        Assert.IsType<KeyenceHostLinkTcpAdapter>(adapter2);

        // KvCommTcp on port 8500 resolves to ACL adapter
        var adapter3 = ConfiguredPlcComposition.SelectAdapter(adapters, TransportKind.KvCommTcp, PlcVendor.Keyence, port: 8500);
        Assert.IsType<KeyenceAclTcpAdapter>(adapter3);

        // KvCommTcp with default/null port falls back to ACL adapter (backward compatibility)
        var adapter4 = ConfiguredPlcComposition.SelectAdapter(adapters, TransportKind.KvCommTcp, PlcVendor.Keyence);
        Assert.IsType<KeyenceAclTcpAdapter>(adapter4);
    }

    [Fact]
    public void KeyenceHostLinkExampleProfile_LoadsAsReadOnlyTarget()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "profiles", "keyence-hostlink.example.json");
        Assert.True(File.Exists(path), $"Missing example configuration: {path}");
        var host = ConfiguredPlcComposition.Load(path);

        var target = Assert.Single(host.Service.ListTargets());
        Assert.Equal(PlcVendor.Keyence, target.Vendor);
        Assert.Equal(TransportKind.KvHostLinkTcp, target.Endpoint.Transport);
        Assert.Equal(8501, target.Endpoint.Port);
        Assert.Equal(CapabilityStatus.Experimental, target.Capabilities.GetStatus("read_tags"));
        Assert.Equal(3, host.Service.ListTags(target.Id).Count);
        Assert.All(host.Service.ListTags(target.Id), tag => Assert.False(tag.CanWrite));
    }
}