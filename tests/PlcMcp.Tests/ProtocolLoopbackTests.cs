using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PlcMcp.Adapters;
using PlcMcp.Adapters.Protocols;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Tests;

public sealed class ProtocolLoopbackTests
{
    private static TargetProfile Target(int port, TransportKind transport, int timeout = 2000) =>
        new("fixture", transport == TransportKind.S7Comm ? PlcVendor.Siemens : PlcVendor.Generic,
            transport == TransportKind.S7Comm ? "S7-200 SMART" : "fixture", null, null,
            new("127.0.0.1", port, transport, Rack: 0, Slot: 1, Unit: transport == TransportKind.ModbusTcp ? 1 : 0, TimeoutMs: timeout),
            [transport], null, new([]), "physical-readonly");

    private static async Task<byte[]> Read(NetworkStream stream, int count, CancellationToken ct)
    {
        var data = new byte[count];
        await stream.ReadExactlyAsync(data, ct);
        return data;
    }

    private static async Task WithServer(Func<NetworkStream, CancellationToken, Task> server,
        Func<int, Task> client)
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
    public async Task Modbus_ReadsFragmentedResponseThroughConfiguredRuntime()
    {
        await WithServer(async (stream, ct) =>
        {
            var frame = await Read(stream, 12, ct);
            Assert.Equal(Convert.FromHexString("000100000006010300640001"), frame);
            var response = Convert.FromHexString("000100000005010302FF85");
            await stream.WriteAsync(response.AsMemory(0, 3), ct);
            await Task.Delay(10, ct);
            await stream.WriteAsync(response.AsMemory(3), ct);
        }, async port =>
        {
            var host = ConfiguredPlcComposition.Create(new(1,
                [new("fixture", PlcVendor.Inovance, "H5U", new("127.0.0.1", port, TransportKind.ModbusTcp, Unit: 1),
                    [new("Pressure", "HR100", PlcDataType.Int16)])]));
            Assert.Equal(CapabilityStatus.Experimental, host.Service.GetTarget("fixture").Capabilities.GetStatus("read_tags"));
            var values = await host.Service.ReadAsync("fixture", ["Pressure"]);
            Assert.Equal((short)-123, Assert.IsType<short>(Assert.Single(values).Value));
            Assert.Equal(QualityCode.Good, values[0].Quality);
        });
    }

    [Theory]
    [InlineData("000200000005010302007B")]
    [InlineData("000100000003018302")]
    [InlineData("000100000005010402007B")]
    public async Task Modbus_RejectsWrongTransactionExceptionAndWrongFunction(string frame)
    {
        await WithServer(async (stream, ct) =>
        {
            await Read(stream, 12, ct);
            await stream.WriteAsync(Convert.FromHexString(frame), ct);
        }, async port => await Assert.ThrowsAsync<InvalidDataException>(() => new ModbusTcpAdapter().ReadAsync(
            Target(port, TransportKind.ModbusTcp), [new("Value", "HR100", PlcDataType.Int16)])));
    }

    [Fact]
    public async Task Modbus_ReadDeadlineClosesStalledConnection()
    {
        await WithServer(async (stream, ct) =>
        {
            await Read(stream, 12, ct);
            var oneByte = new byte[1];
            Assert.Equal(0, await stream.ReadAsync(oneByte, ct));
        }, async port => await Assert.ThrowsAsync<TimeoutException>(() => new ModbusTcpAdapter().ReadAsync(
            Target(port, TransportKind.ModbusTcp, 150), [new("Value", "HR0", PlcDataType.Int16)])));
    }

    [Fact]
    public async Task Slmp_ReadsWordAndBitWithoutWriteCommands()
    {
        await WithServer(async (stream, ct) =>
        {
            var first = await Read(stream, 21, ct);
            Assert.Equal(Convert.FromHexString("500000FFFF03000C00100001040000640000A80100"), first);
            await stream.WriteAsync(Convert.FromHexString("D00000FFFF03000400000085FF"), ct);
            var second = await Read(stream, 21, ct);
            Assert.Equal(0x0401, BinaryPrimitives.ReadUInt16LittleEndian(second.AsSpan(11)));
            Assert.Equal((byte)1, second[13]);
            Assert.Equal((byte)0x9C, second[18]);
            Assert.Equal((byte)0x10, second[15]);
            await stream.WriteAsync(Convert.FromHexString("D00000FFFF03000300000010"), ct);
        }, async port =>
        {
            var result = await new SlmpAdapter().ReadAsync(Target(port, TransportKind.Slmp),
                [new("Word", "D100", PlcDataType.Int16), new("Bit", "X10", PlcDataType.Bool)]);
            Assert.Equal((short)-123, Assert.IsType<short>(result[0].Value));
            Assert.True(Assert.IsType<bool>(result[1].Value));
        });
    }

    [Fact]
    public async Task Fins_NegotiatesNodesAndKeepsTcpEnvelopeForMemoryRead()
    {
        await WithServer(async (stream, ct) =>
        {
            Assert.Equal(Convert.FromHexString("46494E530000000C000000000000000000000000"), await Read(stream, 20, ct));
            await stream.WriteAsync(Convert.FromHexString("46494E530000001000000001000000000000000A00000014"), ct);
            var request = await Read(stream, 34, ct);
            Assert.Equal(Convert.FromHexString("46494E530000001A0000000200000000800002001400000A00010101820064000001"), request);
            await stream.WriteAsync(Convert.FromHexString("46494E53000000180000000200000000C00002000A000014000101010000FF85"), ct);
        }, async port =>
        {
            var result = await new FinsTcpAdapter().ReadAsync(Target(port, TransportKind.Fins), [new("Word", "D100", PlcDataType.Int16)]);
            Assert.Equal((short)-123, Assert.IsType<short>(Assert.Single(result).Value));
        });
    }

    [Fact]
    public async Task S7_NegotiatesSessionAndReadsSmartVMemoryAsDb1()
    {
        await WithServer(async (stream, ct) =>
        {
            static async Task<byte[]> Packet(NetworkStream stream, CancellationToken ct)
            {
                var header = await Read(stream, 4, ct);
                Assert.Equal((byte)3, header[0]);
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
                return header.Concat(await Read(stream, length - 4, ct)).ToArray();
            }
            var connect = await Packet(stream, ct);
            Assert.Equal((byte)0xE0, connect[5]);
            var confirm = connect.ToArray();
            confirm[5] = 0xD0;
            await stream.WriteAsync(confirm, ct);
            var setup = await Packet(stream, ct);
            var setupAck = Convert.FromHexString("0300001B02F080320300000000000800000000F0000001000101E0");
            setupAck[11] = setup[11]; setupAck[12] = setup[12];
            await stream.WriteAsync(setupAck, ct);
            var read = await Packet(stream, ct);
            Assert.Equal((byte)4, read[17]);
            Assert.Equal((byte)0x84, read[27]);
            Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(read.AsSpan(25)));
            var reply = Convert.FromHexString("0300001B02F0803203000000000002000600000401FF040010FF85");
            reply[11] = read[11]; reply[12] = read[12];
            await stream.WriteAsync(reply, ct);
        }, async port =>
        {
            var result = await new S7ReadAdapter().ReadAsync(Target(port, TransportKind.S7Comm), [new("Word", "VW100", PlcDataType.Int16)]);
            Assert.Equal((short)-123, Assert.IsType<short>(Assert.Single(result).Value));
        });
    }
}
