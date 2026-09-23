using System.Buffers.Binary;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Adapters.Protocols;

public static class WireValue
{
    public static int Size(PlcDataType type) => type switch
    {
        PlcDataType.Bool => 1,
        PlcDataType.Int16 or PlcDataType.UInt16 => 2,
        PlcDataType.Int32 or PlcDataType.UInt32 or PlcDataType.Real => 4,
        PlcDataType.Double => 8,
        _ => throw new NotSupportedException("Physical string reads require a vendor string layout and are not implemented.")
    };

    public static object Decode(ReadOnlySpan<byte> bytes, PlcDataType type, PlcByteOrder order)
    {
        var size = Size(type);
        if (bytes.Length != size) throw new InvalidDataException("Response payload length does not match the declared data type.");
        var value = bytes.ToArray();
        if (order == PlcByteOrder.LittleEndian) Array.Reverse(value);
        else if (order == PlcByteOrder.ByteSwap)
            for (var i = 0; i + 1 < size; i += 2) (value[i], value[i + 1]) = (value[i + 1], value[i]);
        else if (order == PlcByteOrder.WordSwap)
            for (var i = 0; i < size / 2; i += 2)
            {
                (value[i], value[size - 2 - i]) = (value[size - 2 - i], value[i]);
                (value[i + 1], value[size - 1 - i]) = (value[size - 1 - i], value[i + 1]);
            }
        return type switch
        {
            PlcDataType.Bool => value[0] != 0,
            PlcDataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(value),
            PlcDataType.UInt16 => BinaryPrimitives.ReadUInt16BigEndian(value),
            PlcDataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(value),
            PlcDataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(value),
            PlcDataType.Real => BinaryPrimitives.ReadSingleBigEndian(value),
            PlcDataType.Double => BinaryPrimitives.ReadDoubleBigEndian(value),
            _ => throw new NotSupportedException()
        };
    }
}
