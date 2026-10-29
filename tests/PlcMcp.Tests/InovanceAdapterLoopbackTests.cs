using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PlcMcp.Adapters;
using PlcMcp.Adapters.Protocols;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Tests;

public sealed class InovanceAdapterLoopbackTests
{
    private static TargetProfile Target(string family, int port, int? unit = 1, int timeout = 2000) =>
        new("fixture", PlcVendor.Inovance, family, null, null,
            new("127.0.0.1", port, TransportKind.ModbusTcp, Unit: unit, TimeoutMs: timeout),
            [TransportKind.ModbusTcp], null, new([]), "physical-readonly");

    private static TagDefinition ParameterTag(string address, PlcDataType type, double? min = null, double? max = null,
        PlcByteOrder? order = null, SafetyClass safetyClass = SafetyClass.Parameter) =>
        new("param", address, type, Minimum: min, Maximum: max, CanWrite: true, SafetyClass: safetyClass, ByteOrder: order);

    private static async Task<byte[]> Read(NetworkStream stream, int count, CancellationToken ct)
    {
        var data = new byte[count];
        await stream.ReadExactlyAsync(data, ct);
        return data;
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
    public async Task Inovance_ReadsSoftElementsThroughConfiguredRuntime()
    {
        await WithServer(async (stream, ct) =>
        {
            // D100 (Int16) -> holding register 100, FC3
            Assert.Equal(Convert.FromHexString("000100000006010300640001"), await Read(stream, 12, ct));
            await stream.WriteAsync(Convert.FromHexString("000100000005010302FF85"), ct);
            // M0 (Bool) -> coil 1024, FC1
            Assert.Equal(Convert.FromHexString("000200000006010104000001"), await Read(stream, 12, ct));
            await stream.WriteAsync(Convert.FromHexString("00020000000401010101"), ct);
        }, async port =>
        {
            var host = ConfiguredPlcComposition.Create(new(1,
                [new("fixture", PlcVendor.Inovance, "H5U", new("127.0.0.1", port, TransportKind.ModbusTcp, Unit: 1),
                    [new("Pressure", "D100", PlcDataType.Int16), new("RunFlag", "M0", PlcDataType.Bool)])]));
            Assert.Equal(CapabilityStatus.Experimental, host.Service.GetTarget("fixture").Capabilities.GetStatus("read_tags"));
            var values = await host.Service.ReadAsync("fixture", ["Pressure", "RunFlag"]);

            Assert.Equal(2, values.Count);
            Assert.Equal((short)-123, Assert.IsType<short>(values.Single(v => v.Name == "Pressure").Value));
            Assert.True(Assert.IsType<bool>(values.Single(v => v.Name == "RunFlag").Value));
            Assert.All(values, v => Assert.Equal(QualityCode.Good, v.Quality));
        });
    }

    [Fact]
    public async Task Inovance_ReadsOctalInputAndRRegister()
    {
        await WithServer(async (stream, ct) =>
        {
            // X10 is octal -> discrete input 8, FC2
            Assert.Equal(Convert.FromHexString("000100000006010200080001"), await Read(stream, 12, ct));
            await stream.WriteAsync(Convert.FromHexString("00010000000401020101"), ct);
            // R100 -> holding register 32768 + 100 = 32868 (0x8064), FC3
            Assert.Equal(Convert.FromHexString("000200000006010380640001"), await Read(stream, 12, ct));
            await stream.WriteAsync(Convert.FromHexString("000200000005010302FF85"), ct);
        }, async port =>
        {
            var result = await new InovanceModbusTcpAdapter().ReadAsync(Target("H5U", port),
                [new("Input", "X10", PlcDataType.Bool), new("Register", "R100", PlcDataType.Int16)]);
            Assert.True(Assert.IsType<bool>(result[0].Value));
            Assert.Equal((short)-123, Assert.IsType<short>(result[1].Value));
        });
    }

    [Fact]
    public async Task Inovance_ReadDecodesRealWithExplicitWordSwap()
    {
        await WithServer(async (stream, ct) =>
        {
            // D200 (Real, WordSwap) -> holding registers 200/201
            Assert.Equal(Convert.FromHexString("000100000006010300C80002"), await Read(stream, 12, ct));
            // Real 1.0 in WordSwap wire order
            await stream.WriteAsync(Convert.FromHexString("00010000000701030400003F80"), ct);
        }, async port =>
        {
            var result = await new InovanceModbusTcpAdapter().ReadAsync(Target("H5U", port),
                [new("Pressure", "D200", PlcDataType.Real, ByteOrder: PlcByteOrder.WordSwap)]);
            Assert.Equal(1.0f, Assert.IsType<float>(Assert.Single(result).Value));
        });
    }

    [Fact]
    public async Task Inovance_ReadRejectsUnknownModelBeforeConnecting()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new InovanceModbusTcpAdapter().ReadAsync(
            Target("AM400", port: 1), [new("Value", "D100", PlcDataType.Int16)]));
    }

    [Fact]
    public async Task Inovance_ReadRejectsBadSoftElementBeforeConnecting()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new InovanceModbusTcpAdapter().ReadAsync(
            Target("H5U", port: 1), [new("Value", "Q0", PlcDataType.Bool)]));
    }

    [Fact]
    public async Task Inovance_ReadRejectsTypeMismatchBeforeConnecting()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new InovanceModbusTcpAdapter().ReadAsync(
            Target("H5U", port: 1), [new("Value", "D100", PlcDataType.Bool)]));
    }

    [Fact]
    public async Task Inovance_ReadRejectsRealWithoutByteOrderBeforeConnecting()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new InovanceModbusTcpAdapter().ReadAsync(
            Target("H5U", port: 1), [new("Value", "D200", PlcDataType.Real)]));
    }

    [Fact]
    public async Task Inovance_ReadDeadlineClosesStalledConnection()
    {
        await WithServer(async (stream, ct) =>
        {
            await Read(stream, 12, ct);
            var oneByte = new byte[1];
            Assert.Equal(0, await stream.ReadAsync(oneByte, ct));
        }, async port => await Assert.ThrowsAsync<TimeoutException>(() => new InovanceModbusTcpAdapter().ReadAsync(
            Target("H5U", port, timeout: 150), [new("Value", "M0", PlcDataType.Bool)])));
    }

    [Theory]
    [InlineData("000100000003018302")]           // exception code
    [InlineData("000200000005010302FF85")]       // wrong transaction
    [InlineData("000100000005010402FF85")]       // wrong function code
    public async Task Inovance_ReadRejectsMalformedResponses(string frame)
    {
        await WithServer(async (stream, ct) =>
        {
            await Read(stream, 12, ct);
            await stream.WriteAsync(Convert.FromHexString(frame), ct);
        }, async port => await Assert.ThrowsAsync<InvalidDataException>(() => new InovanceModbusTcpAdapter().ReadAsync(
            Target("H5U", port), [new("Value", "D100", PlcDataType.Int16)])));
    }

    [Fact]
    public async Task Inovance_ProbeReportsTcpConnectedOnly()
    {
        await WithServer(async (_, ct) => await Task.Delay(TimeSpan.FromSeconds(2), ct), async port =>
        {
            var probe = await new InovanceModbusTcpAdapter().ProbeAsync(Target("H5U", port));
            Assert.True(probe.Reachable);
            Assert.False(probe.IsSimulation);
            Assert.Equal("TCP_CONNECTED", probe.State);
        });
    }

    [Fact]
    public void Inovance_BuildWriteFrame_MapsDRegisterToHoldingRegister()
    {
        var frame = InovanceModbusTcpAdapter.BuildWriteFrame(0x1234, 2, "H5U", ParameterTag("D100", PlcDataType.Int16), (short)0x0205);

        Assert.Equal(15, frame.Length);
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(frame));
        Assert.Equal((ushort)9, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4)));
        Assert.Equal((byte)2, frame[6]);
        Assert.Equal((byte)0x10, frame[7]);
        Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(8)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(10)));
        Assert.Equal((byte)2, frame[12]);
        Assert.Equal((byte)0x02, frame[13]);
        Assert.Equal((byte)0x05, frame[14]);
    }

    [Fact]
    public void Inovance_BuildWriteFrame_MapsRRegisterToItsTableBase()
    {
        var frame = InovanceModbusTcpAdapter.BuildWriteFrame(1, 1, "H5U", ParameterTag("R10", PlcDataType.Int16), (short)7);

        Assert.Equal((byte)0x10, frame[7]);
        Assert.Equal((ushort)32778, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(8)));
    }

    [Theory]
    [InlineData(SafetyClass.ReadOnly)]
    [InlineData(SafetyClass.Actuator)]
    [InlineData(SafetyClass.Safety)]
    [InlineData(SafetyClass.Handshake)]
    public void Inovance_BuildWriteFrame_RejectsNonParameterSafetyClass(SafetyClass safety)
    {
        Assert.Throws<InvalidOperationException>(() =>
            InovanceModbusTcpAdapter.BuildWriteFrame(1, 1, "H5U", ParameterTag("D100", PlcDataType.Int16, safetyClass: safety), (short)1));
    }

    [Theory]
    [InlineData("M0")]
    [InlineData("X0")]
    [InlineData("DD300")]
    [InlineData("HR100")]
    [InlineData("IR100")]
    public void Inovance_BuildWriteFrame_RejectsBitDoubleWordAndNativeAreas(string address)
    {
        var type = address.StartsWith("DD") ? PlcDataType.Int32 : address is "M0" or "X0" ? PlcDataType.Bool : PlcDataType.Int16;
        var order = type == PlcDataType.Int32 ? PlcByteOrder.BigEndian : (PlcByteOrder?)null;
        Assert.Throws<InvalidOperationException>(() =>
            InovanceModbusTcpAdapter.BuildWriteFrame(1, 1, "H5U", ParameterTag(address, type, order: order), (short)1));
    }

    [Fact]
    public void Inovance_BuildWriteFrame_RequiresExplicitByteOrderForMultiWord()
    {
        Assert.Throws<ArgumentException>(() =>
            InovanceModbusTcpAdapter.BuildWriteFrame(1, 1, "H5U", ParameterTag("D100", PlcDataType.Real), 1.0f));
    }

    [Fact]
    public void Inovance_BuildWriteFrame_EnforcesEngineeringRange()
    {
        var tag = ParameterTag("D100", PlcDataType.Int16, min: 0, max: 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => InovanceModbusTcpAdapter.BuildWriteFrame(1, 1, "H5U", tag, (short)-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => InovanceModbusTcpAdapter.BuildWriteFrame(1, 1, "H5U", tag, (short)101));
    }

    [Fact]
    public async Task Inovance_WriteParameterAsync_LoopbackSuccess()
    {
        await WithServer(async (stream, ct) =>
        {
            var request = await Read(stream, 15, ct);
            Assert.Equal((byte)0x10, request[7]);
            Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8)));
            Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(10)));
            Assert.Equal((short)-123, BinaryPrimitives.ReadInt16BigEndian(request.AsSpan(13)));
            await stream.WriteAsync(Convert.FromHexString("000100000006011000640001"), ct);
        }, async port =>
        {
            var result = await new InovanceModbusTcpAdapter().WriteParameterAsync(Target("H5U", port),
                ParameterTag("D100", PlcDataType.Int16), (short)-123);
            Assert.Equal(QualityCode.Good, result.Quality);
            Assert.Equal((short)-123, Assert.IsType<short>(result.Value));
        });
    }

    [Fact]
    public async Task Inovance_WriteParameterAsync_LoopbackException()
    {
        await WithServer(async (stream, ct) =>
        {
            await Read(stream, 15, ct);
            await stream.WriteAsync(Convert.FromHexString("000100000003019002"), ct);
        }, async port =>
        {
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => new InovanceModbusTcpAdapter()
                .WriteParameterAsync(Target("H5U", port), ParameterTag("D100", PlcDataType.Int16), (short)10));
            Assert.Contains("Modbus exception code 02", ex.Message);
        });
    }

    [Fact]
    public async Task Inovance_WriteParameterAsync_LoopbackTimeout()
    {
        await WithServer(async (stream, ct) =>
        {
            await Read(stream, 15, ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }, async port => await Assert.ThrowsAsync<TimeoutException>(() => new InovanceModbusTcpAdapter()
            .WriteParameterAsync(Target("H5U", port, timeout: 150), ParameterTag("D100", PlcDataType.Int16), (short)10)));
    }

    [Fact]
    public async Task Inovance_PhysicalWriteEntryIsNotExposed()
    {
        IPlcProtocolAdapter adapter = new InovanceModbusTcpAdapter();
        var target = Target("H5U", port: 502);

        Assert.False(adapter.CanWrite(target));
        Assert.Equal(CapabilityStatus.Unsupported, adapter.Capabilities.Single(c => c.Name == "write_tags").Status);
        Assert.Equal(CapabilityStatus.Experimental, adapter.Capabilities.Single(c => c.Name == "read_tags").Status);
        Assert.Equal(CapabilityStatus.Unsupported, adapter.Capabilities.Single(c => c.Name == "compile").Status);
        Assert.Equal(CapabilityStatus.Unsupported, adapter.Capabilities.Single(c => c.Name == "download").Status);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            adapter.WriteAsync(target, ParameterTag("D100", PlcDataType.Int16), (short)10));
    }

    [Fact]
    public void InovanceExampleProfile_LoadsAsReadOnlyTarget()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "profiles", "inovance.example.json");
        Assert.True(File.Exists(path), $"Missing example configuration: {path}");
        var host = ConfiguredPlcComposition.Load(path);

        var target = Assert.Single(host.Service.ListTargets());
        Assert.False(target.IsSimulation);
        Assert.Equal(PlcVendor.Inovance, target.Vendor);
        Assert.Equal("H5U", target.Family);
        Assert.Equal(CapabilityStatus.Experimental, target.Capabilities.GetStatus("read_tags"));
        Assert.Equal(CapabilityStatus.Unsupported, target.Capabilities.GetStatus("write_tags"));
        Assert.Equal(11, host.Service.ListTags(target.Id).Count);
        Assert.All(host.Service.ListTags(target.Id), tag => Assert.False(tag.CanWrite));
    }
}
