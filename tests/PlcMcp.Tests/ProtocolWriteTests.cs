using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using PlcMcp.Adapters;
using PlcMcp.Adapters.Protocols;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Tests;

public sealed class ProtocolWriteTests
{
    private static TargetProfile Target(PlcVendor vendor, TransportKind transport, int port, int? unit = null, int timeout = 2000) =>
        new($"test-{vendor}", vendor, transport == TransportKind.S7Comm ? "S7-200 SMART" : "fixture", null, null,
            new("127.0.0.1", port, transport, Rack: 0, Slot: 1, Unit: unit, TimeoutMs: timeout),
            [transport], null, new([]), "physical-readonly");

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

    #region Safety and Guard Tests

    [Theory]
    [InlineData(SafetyClass.ReadOnly)]
    [InlineData(SafetyClass.Actuator)]
    [InlineData(SafetyClass.Safety)]
    [InlineData(SafetyClass.Handshake)]
    public void Modbus_RejectsNonParameterSafetyClass(SafetyClass safety)
    {
        var tag = ParameterTag("HR100", PlcDataType.Int16, safetyClass: safety);
        Assert.Throws<InvalidOperationException>(() => ModbusTcpAdapter.BuildWrite(1, 1, tag, (short)10));
    }

    [Theory]
    [InlineData(SafetyClass.ReadOnly)]
    [InlineData(SafetyClass.Actuator)]
    [InlineData(SafetyClass.Safety)]
    [InlineData(SafetyClass.Handshake)]
    public void Fins_RejectsNonParameterSafetyClass(SafetyClass safety)
    {
        var tag = ParameterTag("D100", PlcDataType.Int16, safetyClass: safety);
        Assert.Throws<InvalidOperationException>(() => FinsTcpAdapter.BuildWrite(1, 2, 0, 1, tag, (short)10));
    }

    [Theory]
    [InlineData(SafetyClass.ReadOnly)]
    [InlineData(SafetyClass.Actuator)]
    [InlineData(SafetyClass.Safety)]
    [InlineData(SafetyClass.Handshake)]
    public void Slmp_RejectsNonParameterSafetyClass(SafetyClass safety)
    {
        var tag = ParameterTag("D100", PlcDataType.Int16, safetyClass: safety);
        Assert.Throws<InvalidOperationException>(() => SlmpAdapter.BuildWrite(tag, (short)10));
    }

    [Theory]
    [InlineData(SafetyClass.ReadOnly)]
    [InlineData(SafetyClass.Actuator)]
    [InlineData(SafetyClass.Safety)]
    [InlineData(SafetyClass.Handshake)]
    public void S7_RejectsNonParameterSafetyClass(SafetyClass safety)
    {
        var tag = ParameterTag("VW100", PlcDataType.Int16, safetyClass: safety);
        Assert.Throws<InvalidOperationException>(() => S7ReadAdapter.BuildWriteFrame(1, tag, (short)10));
    }

    [Fact]
    public void AllProtocols_RejectBoolBitWritesForParameter()
    {
        var modbusTag = ParameterTag("HR100", PlcDataType.Bool);
        Assert.Throws<InvalidOperationException>(() => ModbusTcpAdapter.BuildWrite(1, 1, modbusTag, true));

        var finsTag = ParameterTag("D100.01", PlcDataType.Bool);
        Assert.Throws<InvalidOperationException>(() => FinsTcpAdapter.BuildWrite(1, 2, 0, 1, finsTag, true));

        var slmpTag = ParameterTag("M100", PlcDataType.Bool);
        Assert.Throws<InvalidOperationException>(() => SlmpAdapter.BuildWrite(slmpTag, true));

        var s7Tag = ParameterTag("V100.0", PlcDataType.Bool);
        Assert.Throws<InvalidOperationException>(() => S7ReadAdapter.BuildWriteFrame(1, s7Tag, true));
    }

    [Fact]
    public void Modbus_RejectsNonHoldingRegisterAreas()
    {
        var coilTag = ParameterTag("C100", PlcDataType.Int16);
        Assert.Throws<InvalidOperationException>(() => ModbusTcpAdapter.BuildWrite(1, 1, coilTag, 10));

        var diTag = ParameterTag("DI100", PlcDataType.Int16);
        Assert.Throws<InvalidOperationException>(() => ModbusTcpAdapter.BuildWrite(1, 1, diTag, 10));

        var irTag = ParameterTag("IR100", PlcDataType.Int16);
        Assert.Throws<InvalidOperationException>(() => ModbusTcpAdapter.BuildWrite(1, 1, irTag, 10));
    }

    [Fact]
    public void Fins_RejectsNonDMAreas()
    {
        var cioTag = ParameterTag("CIO100", PlcDataType.Int16);
        Assert.Throws<InvalidOperationException>(() => FinsTcpAdapter.BuildWrite(1, 2, 0, 1, cioTag, 10));

        var wTag = ParameterTag("W100", PlcDataType.Int16);
        Assert.Throws<InvalidOperationException>(() => FinsTcpAdapter.BuildWrite(1, 2, 0, 1, wTag, 10));

        var aTag = ParameterTag("A100", PlcDataType.Int16);
        Assert.Throws<InvalidOperationException>(() => FinsTcpAdapter.BuildWrite(1, 2, 0, 1, aTag, 10));
    }

    [Fact]
    public void Slmp_RejectsBitAndOutputDevices()
    {
        var xTag = ParameterTag("X10", PlcDataType.Int16);
        Assert.Throws<InvalidOperationException>(() => SlmpAdapter.BuildWrite(xTag, 10));

        var yTag = ParameterTag("Y10", PlcDataType.Int16);
        Assert.Throws<InvalidOperationException>(() => SlmpAdapter.BuildWrite(yTag, 10));

        var mTag = ParameterTag("M10", PlcDataType.Int16);
        Assert.Throws<InvalidOperationException>(() => SlmpAdapter.BuildWrite(mTag, 10));

        var bTag = ParameterTag("B10", PlcDataType.Int16);
        Assert.Throws<InvalidOperationException>(() => SlmpAdapter.BuildWrite(bTag, 10));
    }

    [Fact]
    public void S7_RejectsInputOutputAndMemoryAreas()
    {
        var inputTag = ParameterTag("IW0", PlcDataType.Int16);
        Assert.Throws<InvalidOperationException>(() => S7ReadAdapter.BuildWriteFrame(1, inputTag, 10));

        var outputTag = ParameterTag("QW0", PlcDataType.Int16);
        Assert.Throws<InvalidOperationException>(() => S7ReadAdapter.BuildWriteFrame(1, outputTag, 10));

        var memoryTag = ParameterTag("MW0", PlcDataType.Int16);
        Assert.Throws<InvalidOperationException>(() => S7ReadAdapter.BuildWriteFrame(1, memoryTag, 10));
    }

    [Fact]
    public void ValueOutOfRange_ThrowsArgumentOutOfRangeException()
    {
        var tag = ParameterTag("HR100", PlcDataType.Int16, min: 0, max: 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusTcpAdapter.BuildWrite(1, 1, tag, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusTcpAdapter.BuildWrite(1, 1, tag, 101));
    }

    [Fact]
    public async Task PhysicalAdapters_RemainNonWritableByDefault()
    {
        IPlcProtocolAdapter[] adapters = [new ModbusTcpAdapter(), new FinsTcpAdapter(), new SlmpAdapter(), new S7ReadAdapter()];
        foreach (var adapter in adapters)
        {
            var target = Target(adapter.Vendor, adapter.Transports[0], 502);
            Assert.False(adapter.CanWrite(target));
            Assert.Equal(CapabilityStatus.Unsupported, adapter.Capabilities.Single(c => c.Name == "write_tags").Status);
            await Assert.ThrowsAsync<NotSupportedException>(() => adapter.WriteAsync(target, ParameterTag("HR100", PlcDataType.Int16), 10));
        }
    }

    #endregion

    #region Pure Function Frame Construction & Response Verification Tests

    [Fact]
    public void Modbus_BuildWrite_Function16_Int16()
    {
        var tag = ParameterTag("HR100", PlcDataType.Int16);
        var frame = ModbusTcpAdapter.BuildWrite(0x1234, 1, tag, (short)0x0205);

        // Header: Transaction (2), Protocol=0 (2), Length=9 (2), Unit=1 (1)
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(frame));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2)));
        Assert.Equal((ushort)9, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4))); // 1 + 1 + 2 + 2 + 1 + 2 = 9
        Assert.Equal((byte)1, frame[6]);
        // PDU: FC=0x10, Addr=100, Count=1, ByteCount=2, Data=02 05
        Assert.Equal((byte)0x10, frame[7]);
        Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(8)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(10)));
        Assert.Equal((byte)2, frame[12]);
        Assert.Equal((byte)0x02, frame[13]);
        Assert.Equal((byte)0x05, frame[14]);
    }

    [Fact]
    public void Modbus_BuildWrite_Function16_Double_WordSwap()
    {
        var tag = ParameterTag("HR200", PlcDataType.Double, order: PlcByteOrder.WordSwap);
        var frame = ModbusTcpAdapter.BuildWrite(0x0001, 2, tag, 1.0);

        Assert.Equal((ushort)15, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(4))); // 7 + 8 = 15
        Assert.Equal((byte)2, frame[6]);
        Assert.Equal((byte)0x10, frame[7]);
        Assert.Equal((ushort)200, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(8)));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(10)));
        Assert.Equal((byte)8, frame[12]);
        // Double 1.0 WordSwap = 00 00 3F F0 00 00 00 00
        Assert.Equal(Convert.FromHexString("00003FF000000000"), frame.AsSpan(13).ToArray());
    }

    [Fact]
    public void Modbus_VerifyWriteResponse_HandlesSuccessAndExceptions()
    {
        var validResponse = Convert.FromHexString("123400000006011000640001");
        ModbusTcpAdapter.VerifyWriteResponse(validResponse, 0x1234, 1, 100, 1);

        var exceptionResponse = Convert.FromHexString("123400000003019002");
        var ex = Assert.Throws<InvalidDataException>(() => ModbusTcpAdapter.VerifyWriteResponse(exceptionResponse, 0x1234, 1, 100, 1));
        Assert.Contains("Modbus exception code 02", ex.Message);

        var mismatchedTx = Convert.FromHexString("999900000006011000640001");
        Assert.Throws<InvalidDataException>(() => ModbusTcpAdapter.VerifyWriteResponse(mismatchedTx, 0x1234, 1, 100, 1));
    }

    [Fact]
    public void Fins_BuildWrite_MemoryAreaWrite_D100()
    {
        var tag = ParameterTag("D100", PlcDataType.Int16);
        var frame = FinsTcpAdapter.BuildWrite(10, 20, 0, 1, tag, (short)0x0304);

        Assert.Equal("FINS", System.Text.Encoding.ASCII.GetString(frame, 0, 4));
        Assert.Equal(28u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4))); // 8 + 20 = 28
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(8)));
        // FINS payload
        Assert.Equal((byte)0x80, frame[16]);
        Assert.Equal((byte)20, frame[20]); // DA1
        Assert.Equal((byte)10, frame[23]); // SA1
        Assert.Equal((byte)1, frame[25]);  // SID
        Assert.Equal((byte)1, frame[26]);  // MRC = 0x01
        Assert.Equal((byte)2, frame[27]);  // SRC = 0x02 (Memory Area Write)
        Assert.Equal((byte)0x82, frame[28]); // Area = DM word
        Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(29)));
        Assert.Equal((byte)0, frame[31]); // bit
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(32))); // count
        Assert.Equal((byte)0x03, frame[34]);
        Assert.Equal((byte)0x04, frame[35]);
    }

    [Fact]
    public void Fins_VerifyWriteResponse_HandlesEndCodes()
    {
        var validPayload = Convert.FromHexString("C00002000A000014000101020000");
        FinsTcpAdapter.VerifyWriteResponse(validPayload, 10, 20, 1);

        var errorPayload = Convert.FromHexString("C00002000A000014000101021103");
        var ex = Assert.Throws<InvalidDataException>(() => FinsTcpAdapter.VerifyWriteResponse(errorPayload, 10, 20, 1));
        Assert.Contains("FINS end code 1103", ex.Message);
    }

    [Fact]
    public void Slmp_BuildWrite_DeviceWrite_D100()
    {
        var tag = ParameterTag("D100", PlcDataType.Int16);
        var frame = SlmpAdapter.BuildWrite(tag, (short)0x0205);

        Assert.Equal((byte)0x50, frame[0]);
        Assert.Equal((byte)0x00, frame[1]);
        Assert.Equal((ushort)14, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(7))); // 12 + 2 = 14
        Assert.Equal((ushort)0x1401, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(11))); // Command 1401H (01 14)
        Assert.Equal((ushort)0x0000, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(13))); // Subcommand
        Assert.Equal(100, frame[15] | (frame[16] << 8) | (frame[17] << 16));
        Assert.Equal((byte)0xA8, frame[18]); // D register
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(19))); // Points
        // Data in LittleEndian: 0x0205 -> 05 02
        Assert.Equal((byte)0x05, frame[21]);
        Assert.Equal((byte)0x02, frame[22]);
    }

    [Fact]
    public void Slmp_VerifyWriteResponse_HandlesSuccessAndEndCodes()
    {
        var header = Convert.FromHexString("D00000FFFF03000200");
        var successPayload = Convert.FromHexString("0000");
        SlmpAdapter.VerifyWriteResponse(header, successPayload);

        var errorPayload = Convert.FromHexString("3140");
        var ex = Assert.Throws<InvalidDataException>(() => SlmpAdapter.VerifyWriteResponse(header, errorPayload));
        Assert.Contains("SLMP end code 4031", ex.Message);
    }

    [Fact]
    public void S7_BuildWriteFrame_Int16_DB1()
    {
        var tag = ParameterTag("VW100", PlcDataType.Int16);
        var frame = S7ReadAdapter.BuildWriteFrame(0x0102, tag, (short)0x1234);

        // TPKT
        Assert.Equal((byte)3, frame[0]);
        Assert.Equal((ushort)37, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(2))); // 35 + 2 = 37
        // COTP
        Assert.Equal((byte)0xF0, frame[5]);
        // S7comm Header
        Assert.Equal((byte)0x32, frame[7]);
        Assert.Equal((byte)0x01, frame[8]); // Job
        Assert.Equal((ushort)0x0102, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(11))); // PduRef
        // Parameter: Function 0x05
        Assert.Equal((byte)0x05, frame[17]);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(25))); // DB 1
        Assert.Equal((byte)0x84, frame[27]); // Area DB
        // Bit address for offset 100 = 800
        int bitAddr = (frame[28] << 16) | (frame[29] << 8) | frame[30];
        Assert.Equal(800, bitAddr);
        // Data
        Assert.Equal((byte)0x12, frame[35]);
        Assert.Equal((byte)0x34, frame[36]);
    }

    [Fact]
    public void S7_VerifyWriteResponse_HandlesSuccessAndErrorCode()
    {
        var success = Convert.FromHexString("0300001602F0803203000001020002000100000501FF");
        S7ReadAdapter.VerifyWriteResponse(success, 0x0102);

        var itemError = Convert.FromHexString("0300001602F080320300000102000200010000050105");
        var ex = Assert.Throws<InvalidDataException>(() => S7ReadAdapter.VerifyWriteResponse(itemError, 0x0102));
        Assert.Contains("S7 write item failed with code 05", ex.Message);
    }

    #endregion

    #region Localhost Loopback Virtual PLC Tests

    [Fact]
    public async Task Modbus_WriteParameterAsync_LoopbackSuccess()
    {
        await WithServer(async (stream, ct) =>
        {
            var request = await Read(stream, 15, ct); // 13 + 2 = 15
            Assert.Equal((byte)0x10, request[7]);
            Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8)));
            Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(10)));
            Assert.Equal((short)-123, BinaryPrimitives.ReadInt16BigEndian(request.AsSpan(13)));

            // Echo response
            var response = Convert.FromHexString("000100000006011000640001");
            await stream.WriteAsync(response, ct);
        }, async port =>
        {
            var adapter = new ModbusTcpAdapter();
            var target = Target(PlcVendor.Generic, TransportKind.ModbusTcp, port, unit: 1);
            var tag = ParameterTag("HR100", PlcDataType.Int16);
            var result = await adapter.WriteParameterAsync(target, tag, (short)-123);

            Assert.Equal(QualityCode.Good, result.Quality);
            Assert.Equal((short)-123, Assert.IsType<short>(result.Value));
        });
    }

    [Fact]
    public async Task Modbus_WriteParameterAsync_LoopbackException()
    {
        await WithServer(async (stream, ct) =>
        {
            await Read(stream, 15, ct);
            var exception = Convert.FromHexString("000100000003019002");
            await stream.WriteAsync(exception, ct);
        }, async port =>
        {
            var adapter = new ModbusTcpAdapter();
            var target = Target(PlcVendor.Generic, TransportKind.ModbusTcp, port, unit: 1);
            var tag = ParameterTag("HR100", PlcDataType.Int16);
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => adapter.WriteParameterAsync(target, tag, (short)10));
            Assert.Contains("Modbus exception code 02", ex.Message);
        });
    }

    [Fact]
    public async Task Modbus_WriteParameterAsync_LoopbackTimeout()
    {
        await WithServer(async (stream, ct) =>
        {
            await Read(stream, 15, ct);
            // Stalled server, never replies
            await Task.Delay(2000, ct);
        }, async port =>
        {
            var adapter = new ModbusTcpAdapter();
            var target = Target(PlcVendor.Generic, TransportKind.ModbusTcp, port, unit: 1, timeout: 150);
            var tag = ParameterTag("HR100", PlcDataType.Int16);
            await Assert.ThrowsAsync<TimeoutException>(() => adapter.WriteParameterAsync(target, tag, (short)10));
        });
    }

    [Fact]
    public async Task Fins_WriteParameterAsync_LoopbackSuccess()
    {
        await WithServer(async (stream, ct) =>
        {
            // 1. Handshake request
            var hsReq = await Read(stream, 20, ct);
            Assert.Equal(Convert.FromHexString("46494E530000000C000000000000000000000000"), hsReq);
            // Handshake reply
            await stream.WriteAsync(Convert.FromHexString("46494E530000001000000001000000000000000A00000014"), ct);

            // 2. FINS Write request: 16 (TCP) + 18 (FINS) + 2 (Data) = 36 bytes
            var writeReq = await Read(stream, 36, ct);
            Assert.Equal((byte)0x01, writeReq[26]); // MRC
            Assert.Equal((byte)0x02, writeReq[27]); // SRC
            Assert.Equal((byte)0x82, writeReq[28]); // DM
            Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16BigEndian(writeReq.AsSpan(29)));
            Assert.Equal((short)-123, BinaryPrimitives.ReadInt16BigEndian(writeReq.AsSpan(34)));

            // FINS Write reply: 16 + 14 = 30 bytes
            var reply = Convert.FromHexString("46494E53000000160000000200000000C00002000A000014000101020000");
            await stream.WriteAsync(reply, ct);
        }, async port =>
        {
            var adapter = new FinsTcpAdapter();
            var target = Target(PlcVendor.Omron, TransportKind.Fins, port);
            var tag = ParameterTag("D100", PlcDataType.Int16);
            var result = await adapter.WriteParameterAsync(target, tag, (short)-123);

            Assert.Equal(QualityCode.Good, result.Quality);
            Assert.Equal((short)-123, Assert.IsType<short>(result.Value));
        });
    }

    [Fact]
    public async Task Fins_WriteParameterAsync_LoopbackErrorEndCode()
    {
        await WithServer(async (stream, ct) =>
        {
            await Read(stream, 20, ct);
            await stream.WriteAsync(Convert.FromHexString("46494E530000001000000001000000000000000A00000014"), ct);
            await Read(stream, 36, ct);
            // End code 1103 (Address range error)
            var reply = Convert.FromHexString("46494E53000000160000000200000000C00002000A000014000101021103");
            await stream.WriteAsync(reply, ct);
        }, async port =>
        {
            var adapter = new FinsTcpAdapter();
            var target = Target(PlcVendor.Omron, TransportKind.Fins, port);
            var tag = ParameterTag("D100", PlcDataType.Int16);
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => adapter.WriteParameterAsync(target, tag, (short)10));
            Assert.Contains("FINS end code 1103", ex.Message);
        });
    }

    [Fact]
    public async Task Fins_WriteParameterAsync_LoopbackTimeout()
    {
        await WithServer(async (stream, ct) =>
        {
            await Read(stream, 20, ct);
            await Task.Delay(2000, ct);
        }, async port =>
        {
            var adapter = new FinsTcpAdapter();
            var target = Target(PlcVendor.Omron, TransportKind.Fins, port, timeout: 150);
            var tag = ParameterTag("D100", PlcDataType.Int16);
            await Assert.ThrowsAsync<TimeoutException>(() => adapter.WriteParameterAsync(target, tag, (short)10));
        });
    }

    [Fact]
    public async Task Slmp_WriteParameterAsync_LoopbackSuccess()
    {
        await WithServer(async (stream, ct) =>
        {
            var request = await Read(stream, 23, ct); // 21 + 2 = 23
            Assert.Equal((ushort)0x1401, BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(11)));
            Assert.Equal((byte)0xA8, request[18]); // D
            Assert.Equal(100, request[15] | (request[16] << 8) | (request[17] << 16));
            Assert.Equal((short)-123, BinaryPrimitives.ReadInt16LittleEndian(request.AsSpan(21)));

            // Response: D0 00 00 FF FF 03 00 02 00 00 00
            var response = Convert.FromHexString("D00000FFFF030002000000");
            await stream.WriteAsync(response, ct);
        }, async port =>
        {
            var adapter = new SlmpAdapter();
            var target = Target(PlcVendor.Mitsubishi, TransportKind.Slmp, port);
            var tag = ParameterTag("D100", PlcDataType.Int16);
            var result = await adapter.WriteParameterAsync(target, tag, (short)-123);

            Assert.Equal(QualityCode.Good, result.Quality);
            Assert.Equal((short)-123, Assert.IsType<short>(result.Value));
        });
    }

    [Fact]
    public async Task Slmp_WriteParameterAsync_LoopbackErrorEndCode()
    {
        await WithServer(async (stream, ct) =>
        {
            await Read(stream, 23, ct);
            // End code 4031
            var response = Convert.FromHexString("D00000FFFF030002003140");
            await stream.WriteAsync(response, ct);
        }, async port =>
        {
            var adapter = new SlmpAdapter();
            var target = Target(PlcVendor.Mitsubishi, TransportKind.Slmp, port);
            var tag = ParameterTag("D100", PlcDataType.Int16);
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => adapter.WriteParameterAsync(target, tag, (short)10));
            Assert.Contains("SLMP end code 4031", ex.Message);
        });
    }

    [Fact]
    public async Task Slmp_WriteParameterAsync_LoopbackTimeout()
    {
        await WithServer(async (stream, ct) =>
        {
            await Read(stream, 23, ct);
            await Task.Delay(2000, ct);
        }, async port =>
        {
            var adapter = new SlmpAdapter();
            var target = Target(PlcVendor.Mitsubishi, TransportKind.Slmp, port, timeout: 150);
            var tag = ParameterTag("D100", PlcDataType.Int16);
            await Assert.ThrowsAsync<TimeoutException>(() => adapter.WriteParameterAsync(target, tag, (short)10));
        });
    }

    [Fact]
    public async Task S7_WriteParameterAsync_LoopbackSuccess()
    {
        await WithServer(async (stream, ct) =>
        {
            static async Task<byte[]> Packet(NetworkStream s, CancellationToken token)
            {
                var header = await Read(s, 4, token);
                Assert.Equal((byte)3, header[0]);
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
                return header.Concat(await Read(s, length - 4, token)).ToArray();
            }

            // 1. COTP Connect
            var connect = await Packet(stream, ct);
            Assert.Equal((byte)0xE0, connect[5]);
            var confirm = connect.ToArray();
            confirm[5] = 0xD0;
            await stream.WriteAsync(confirm, ct);

            // 2. Setup communication
            var setup = await Packet(stream, ct);
            var setupAck = Convert.FromHexString("0300001B02F080320300000000000800000000F0000001000101E0");
            setupAck[11] = setup[11]; setupAck[12] = setup[12];
            await stream.WriteAsync(setupAck, ct);

            // 3. Write packet
            var write = await Packet(stream, ct);
            Assert.Equal((byte)5, write[17]); // Function 5 = Write Var
            Assert.Equal((byte)0x84, write[27]); // DB
            Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(write.AsSpan(25))); // DB 1 (V memory)
            Assert.Equal((short)-123, BinaryPrimitives.ReadInt16BigEndian(write.AsSpan(35)));

            // 4. Write Ack response (22 bytes)
            var reply = Convert.FromHexString("0300001602F0803203000000000002000100000501FF");
            reply[11] = write[11]; reply[12] = write[12];
            await stream.WriteAsync(reply, ct);
        }, async port =>
        {
            var adapter = new S7ReadAdapter();
            var target = Target(PlcVendor.Siemens, TransportKind.S7Comm, port);
            var tag = ParameterTag("VW100", PlcDataType.Int16);
            var result = await adapter.WriteParameterAsync(target, tag, (short)-123);

            Assert.Equal(QualityCode.Good, result.Quality);
            Assert.Equal((short)-123, Assert.IsType<short>(result.Value));
        });
    }

    [Fact]
    public async Task S7_WriteParameterAsync_LoopbackError()
    {
        await WithServer(async (stream, ct) =>
        {
            static async Task<byte[]> Packet(NetworkStream s, CancellationToken token)
            {
                var header = await Read(s, 4, token);
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
                return header.Concat(await Read(s, length - 4, token)).ToArray();
            }

            var connect = await Packet(stream, ct);
            var confirm = connect.ToArray();
            confirm[5] = 0xD0;
            await stream.WriteAsync(confirm, ct);

            var setup = await Packet(stream, ct);
            var setupAck = Convert.FromHexString("0300001B02F080320300000000000800000000F0000001000101E0");
            setupAck[11] = setup[11]; setupAck[12] = setup[12];
            await stream.WriteAsync(setupAck, ct);

            var write = await Packet(stream, ct);
            // S7 write item error (code 0x05)
            var reply = Convert.FromHexString("0300001602F080320300000000000200010000050105");
            reply[11] = write[11]; reply[12] = write[12];
            await stream.WriteAsync(reply, ct);
        }, async port =>
        {
            var adapter = new S7ReadAdapter();
            var target = Target(PlcVendor.Siemens, TransportKind.S7Comm, port);
            var tag = ParameterTag("VW100", PlcDataType.Int16);
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => adapter.WriteParameterAsync(target, tag, (short)10));
            Assert.Contains("S7 write error", ex.Message);
        });
    }

    [Fact]
    public async Task S7_WriteParameterAsync_LoopbackTimeout()
    {
        await WithServer(async (stream, ct) =>
        {
            var header = await Read(stream, 4, ct);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
            await Read(stream, length - 4, ct);
            await Task.Delay(2000, ct);
        }, async port =>
        {
            var adapter = new S7ReadAdapter();
            var target = Target(PlcVendor.Siemens, TransportKind.S7Comm, port, timeout: 150);
            var tag = ParameterTag("VW100", PlcDataType.Int16);
            await Assert.ThrowsAsync<TimeoutException>(() => adapter.WriteParameterAsync(target, tag, (short)10));
        });
    }

    #endregion
}
