using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
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

    public static void ApplyByteOrder(Span<byte> bytes, PlcByteOrder order)
    {
        var size = bytes.Length;
        if (order == PlcByteOrder.LittleEndian)
        {
            bytes.Reverse();
        }
        else if (order == PlcByteOrder.ByteSwap)
        {
            for (var i = 0; i + 1 < size; i += 2)
                (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
        }
        else if (order == PlcByteOrder.WordSwap)
        {
            for (var i = 0; i + 3 < size; i += 4)
            {
                (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
                (bytes[i + 1], bytes[i + 3]) = (bytes[i + 3], bytes[i + 1]);
            }
        }
    }

    public static object Decode(ReadOnlySpan<byte> bytes, PlcDataType type, PlcByteOrder order)
    {
        var size = Size(type);
        if (bytes.Length != size) throw new InvalidDataException("Response payload length does not match the declared data type.");
        var value = bytes.ToArray();
        ApplyByteOrder(value, order);
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

    public static byte[] Encode(object value, PlcDataType type, PlcByteOrder order)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (value is JsonElement je)
        {
            value = type switch
            {
                PlcDataType.Bool => je.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? je.GetBoolean()
                    : bool.Parse(je.GetRawText()),
                PlcDataType.Int16 => je.GetInt16(),
                PlcDataType.UInt16 => je.GetUInt16(),
                PlcDataType.Int32 => je.GetInt32(),
                PlcDataType.UInt32 => je.GetUInt32(),
                PlcDataType.Real => je.GetSingle(),
                PlcDataType.Double => je.GetDouble(),
                _ => throw new NotSupportedException("Physical string writes require a vendor string layout and are not implemented.")
            };
        }

        var size = Size(type);
        var bytes = new byte[size];
        switch (type)
        {
            case PlcDataType.Bool:
                bool b = value switch
                {
                    bool bv => bv,
                    int iv => iv != 0,
                    short sv => sv != 0,
                    byte bv2 => bv2 != 0,
                    long lv => lv != 0,
                    _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
                };
                bytes[0] = (byte)(b ? 1 : 0);
                break;
            case PlcDataType.Int16:
                BinaryPrimitives.WriteInt16BigEndian(bytes, Convert.ToInt16(value, CultureInfo.InvariantCulture));
                break;
            case PlcDataType.UInt16:
                BinaryPrimitives.WriteUInt16BigEndian(bytes, Convert.ToUInt16(value, CultureInfo.InvariantCulture));
                break;
            case PlcDataType.Int32:
                BinaryPrimitives.WriteInt32BigEndian(bytes, Convert.ToInt32(value, CultureInfo.InvariantCulture));
                break;
            case PlcDataType.UInt32:
                BinaryPrimitives.WriteUInt32BigEndian(bytes, Convert.ToUInt32(value, CultureInfo.InvariantCulture));
                break;
            case PlcDataType.Real:
                BinaryPrimitives.WriteSingleBigEndian(bytes, Convert.ToSingle(value, CultureInfo.InvariantCulture));
                break;
            case PlcDataType.Double:
                BinaryPrimitives.WriteDoubleBigEndian(bytes, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            default:
                throw new NotSupportedException("Physical string writes require a vendor string layout and are not implemented.");
        }
        ApplyByteOrder(bytes, order);
        return bytes;
    }
}
