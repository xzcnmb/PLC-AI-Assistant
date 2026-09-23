using System.Buffers.Binary;
using PlcMcp.Adapters;
using PlcMcp.Adapters.Protocols;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Tests;

public sealed class AdapterContractTests
{
    private static TargetProfile Target(PlcVendor vendor, TransportKind transport, int port, int? unit = null) =>
        new($"test-{vendor}", vendor, "test", "fixture", null,
            new("127.0.0.1", port, transport, Unit: unit), [transport], null,
            new([new("read_tags", CapabilityStatus.Experimental, "fixture")]), "test", false);

    private static TagDefinition Tag(string address, PlcDataType type, PlcByteOrder? order = null) =>
        new("value", address, type, ByteOrder: order);

    [Fact]
    public void ModbusReadFrame_UsesTransactionUnitFunctionAndAddress()
    {
        var frame = ModbusTcpAdapter.BuildRead(0x1234, 7, Tag("HR100", PlcDataType.Int16));
        Assert.Equal(12, frame.Length);
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(frame));
        Assert.Equal((byte)7, frame[6]);
        Assert.Equal((byte)3, frame[7]);
        Assert.Equal(100, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(8)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(10)));
    }

    [Fact]
    public void SlmpReadFrame_Uses3EBinaryLittleEndianFields()
    {
        var frame = SlmpAdapter.BuildRead(Tag("D100", PlcDataType.Int16));
        Assert.Equal((byte)0x50, frame[0]);
        Assert.Equal((byte)0x00, frame[1]);
        Assert.Equal(0x0401, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(11)));
        Assert.Equal(100, frame[15] | (frame[16] << 8) | (frame[17] << 16));
        Assert.Equal((byte)0xA8, frame[18]);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(19)));
    }

    [Fact]
    public void FinsReadFrame_ContainsTcpHeaderAndMemoryAreaRead()
    {
        var frame = FinsTcpAdapter.BuildRead(10, 20, 0, 4, Tag("D100", PlcDataType.Int16));
        Assert.Equal("FINS", System.Text.Encoding.ASCII.GetString(frame, 0, 4));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(8)));
        Assert.Equal(0x0101, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(26)));
        Assert.Equal((byte)0x82, frame[28]);
        Assert.Equal((ushort)100, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(29)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(32)));
    }

    [Fact]
    public void PhysicalAdaptersNeverAdvertiseWrites()
    {
        IPlcProtocolAdapter[] adapters = [new S7ReadAdapter(), new ModbusTcpAdapter(), new FinsTcpAdapter(), new SlmpAdapter()];
        Assert.All(adapters, adapter =>
        {
            Assert.False(adapter.CanWrite(Target(PlcVendor.Generic, adapter.Transports[0], 1)));
            Assert.Equal(CapabilityStatus.Unsupported, adapter.Capabilities.Single(x => x.Name == "write_tags").Status);
        });
    }
}
