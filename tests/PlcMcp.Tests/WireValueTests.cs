using System.Buffers.Binary;
using System.Text.Json;
using PlcMcp.Adapters.Protocols;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Tests;

public sealed class WireValueTests
{
    [Fact]
    public void Double_WordSwap_FixedVector_RegressionTest()
    {
        // Double 1.0 in IEEE 754 BigEndian is: 3F F0 00 00 00 00 00 00
        // Words: W0=3FF0, W1=0000, W2=0000, W3=0000
        // In CDAB WordSwap:
        // (W0, W1) swapped -> W1, W0: 00 00 3F F0
        // (W2, W3) swapped -> W3, W2: 00 00 00 00
        // Resulting wire bytes: 00 00 3F F0 00 00 00 00
        byte[] wireBytes = [0x00, 0x00, 0x3F, 0xF0, 0x00, 0x00, 0x00, 0x00];

        var decoded = WireValue.Decode(wireBytes, PlcDataType.Double, PlcByteOrder.WordSwap);
        Assert.Equal(1.0, Assert.IsType<double>(decoded));

        var encoded = WireValue.Encode(1.0, PlcDataType.Double, PlcByteOrder.WordSwap);
        Assert.Equal(wireBytes, encoded);
    }

    [Fact]
    public void Double_WordSwap_DistinctBytesVector()
    {
        // 8 distinct bytes: A B C D E F G H = 01 02 03 04 05 06 07 08
        // WordSwap (CDAB pairwise) wire layout:
        // W1 (C, D) = 03 04, W0 (A, B) = 01 02, W3 (G, H) = 07 08, W2 (E, F) = 05 06
        // Wire: 03 04 01 02 07 08 05 06
        byte[] wire = [0x03, 0x04, 0x01, 0x02, 0x07, 0x08, 0x05, 0x06];
        double expectedValue = BitConverter.Int64BitsToDouble(0x0102030405060708L);

        var decoded = WireValue.Decode(wire, PlcDataType.Double, PlcByteOrder.WordSwap);
        Assert.Equal(expectedValue, Assert.IsType<double>(decoded));

        var encoded = WireValue.Encode(expectedValue, PlcDataType.Double, PlcByteOrder.WordSwap);
        Assert.Equal(wire, encoded);
    }

    [Theory]
    [InlineData(PlcByteOrder.BigEndian, "3FF0000000000000")]
    [InlineData(PlcByteOrder.LittleEndian, "000000000000F03F")]
    [InlineData(PlcByteOrder.ByteSwap, "F03F000000000000")]
    [InlineData(PlcByteOrder.WordSwap, "00003FF000000000")]
    public void Double_OnePointZero_AllByteOrders(PlcByteOrder order, string expectedHex)
    {
        var expectedBytes = Convert.FromHexString(expectedHex);

        var decoded = (double)WireValue.Decode(expectedBytes, PlcDataType.Double, order);
        Assert.Equal(1.0, decoded);

        var encoded = WireValue.Encode(1.0, PlcDataType.Double, order);
        Assert.Equal(expectedBytes, encoded);
    }

    [Theory]
    [InlineData(PlcByteOrder.BigEndian, "0102030405060708")]
    [InlineData(PlcByteOrder.LittleEndian, "0807060504030201")]
    [InlineData(PlcByteOrder.ByteSwap, "0201040306050807")]
    [InlineData(PlcByteOrder.WordSwap, "0304010207080506")]
    public void Double_EightBytesPattern_AllByteOrders(PlcByteOrder order, string expectedHex)
    {
        var expectedBytes = Convert.FromHexString(expectedHex);
        var expectedValue = BitConverter.Int64BitsToDouble(0x0102030405060708L);

        var decoded = (double)WireValue.Decode(expectedBytes, PlcDataType.Double, order);
        Assert.Equal(expectedValue, decoded);

        var encoded = WireValue.Encode(expectedValue, PlcDataType.Double, order);
        Assert.Equal(expectedBytes, encoded);
    }

    [Theory]
    [InlineData(PlcByteOrder.BigEndian, "01020304")]
    [InlineData(PlcByteOrder.LittleEndian, "04030201")]
    [InlineData(PlcByteOrder.ByteSwap, "02010403")]
    [InlineData(PlcByteOrder.WordSwap, "03040102")]
    public void Real_FourBytesPattern_AllByteOrders(PlcByteOrder order, string expectedHex)
    {
        var expectedBytes = Convert.FromHexString(expectedHex);
        var expectedValue = BitConverter.Int32BitsToSingle(0x01020304);

        var decoded = (float)WireValue.Decode(expectedBytes, PlcDataType.Real, order);
        Assert.Equal(expectedValue, decoded);

        var encoded = WireValue.Encode(expectedValue, PlcDataType.Real, order);
        Assert.Equal(expectedBytes, encoded);
    }

    [Theory]
    [InlineData(PlcByteOrder.BigEndian, "3F800000")]
    [InlineData(PlcByteOrder.LittleEndian, "0000803F")]
    [InlineData(PlcByteOrder.ByteSwap, "803F0000")]
    [InlineData(PlcByteOrder.WordSwap, "00003F80")]
    public void Real_OnePointZero_AllByteOrders(PlcByteOrder order, string expectedHex)
    {
        var expectedBytes = Convert.FromHexString(expectedHex);

        var decoded = (float)WireValue.Decode(expectedBytes, PlcDataType.Real, order);
        Assert.Equal(1.0f, decoded);

        var encoded = WireValue.Encode(1.0f, PlcDataType.Real, order);
        Assert.Equal(expectedBytes, encoded);
    }

    [Theory]
    [InlineData(PlcByteOrder.BigEndian, "01020304")]
    [InlineData(PlcByteOrder.LittleEndian, "04030201")]
    [InlineData(PlcByteOrder.ByteSwap, "02010403")]
    [InlineData(PlcByteOrder.WordSwap, "03040102")]
    public void Int32_Pattern_AllByteOrders(PlcByteOrder order, string expectedHex)
    {
        var expectedBytes = Convert.FromHexString(expectedHex);
        const int expectedValue = 0x01020304;

        var decoded = (int)WireValue.Decode(expectedBytes, PlcDataType.Int32, order);
        Assert.Equal(expectedValue, decoded);

        var encoded = WireValue.Encode(expectedValue, PlcDataType.Int32, order);
        Assert.Equal(expectedBytes, encoded);
    }

    [Theory]
    [InlineData(PlcByteOrder.BigEndian, "01020304")]
    [InlineData(PlcByteOrder.LittleEndian, "04030201")]
    [InlineData(PlcByteOrder.ByteSwap, "02010403")]
    [InlineData(PlcByteOrder.WordSwap, "03040102")]
    public void UInt32_Pattern_AllByteOrders(PlcByteOrder order, string expectedHex)
    {
        var expectedBytes = Convert.FromHexString(expectedHex);
        const uint expectedValue = 0x01020304;

        var decoded = (uint)WireValue.Decode(expectedBytes, PlcDataType.UInt32, order);
        Assert.Equal(expectedValue, decoded);

        var encoded = WireValue.Encode(expectedValue, PlcDataType.UInt32, order);
        Assert.Equal(expectedBytes, encoded);
    }

    [Theory]
    [InlineData(PlcByteOrder.BigEndian, "0102")]
    [InlineData(PlcByteOrder.LittleEndian, "0201")]
    [InlineData(PlcByteOrder.ByteSwap, "0201")]
    [InlineData(PlcByteOrder.WordSwap, "0102")] // WordSwap on 16-bit word is no-op
    public void Int16_Pattern_AllByteOrders(PlcByteOrder order, string expectedHex)
    {
        var expectedBytes = Convert.FromHexString(expectedHex);
        const short expectedValue = 0x0102;

        var decoded = (short)WireValue.Decode(expectedBytes, PlcDataType.Int16, order);
        Assert.Equal(expectedValue, decoded);

        var encoded = WireValue.Encode(expectedValue, PlcDataType.Int16, order);
        Assert.Equal(expectedBytes, encoded);
    }

    [Theory]
    [InlineData(PlcByteOrder.BigEndian, "0102")]
    [InlineData(PlcByteOrder.LittleEndian, "0201")]
    [InlineData(PlcByteOrder.ByteSwap, "0201")]
    [InlineData(PlcByteOrder.WordSwap, "0102")]
    public void UInt16_Pattern_AllByteOrders(PlcByteOrder order, string expectedHex)
    {
        var expectedBytes = Convert.FromHexString(expectedHex);
        const ushort expectedValue = 0x0102;

        var decoded = (ushort)WireValue.Decode(expectedBytes, PlcDataType.UInt16, order);
        Assert.Equal(expectedValue, decoded);

        var encoded = WireValue.Encode(expectedValue, PlcDataType.UInt16, order);
        Assert.Equal(expectedBytes, encoded);
    }

    [Theory]
    [InlineData(true, "01")]
    [InlineData(false, "00")]
    public void Bool_EncodingAndDecoding(bool value, string expectedHex)
    {
        var expectedBytes = Convert.FromHexString(expectedHex);

        var decoded = (bool)WireValue.Decode(expectedBytes, PlcDataType.Bool, PlcByteOrder.BigEndian);
        Assert.Equal(value, decoded);

        var encoded = WireValue.Encode(value, PlcDataType.Bool, PlcByteOrder.BigEndian);
        Assert.Equal(expectedBytes, encoded);
    }

    [Fact]
    public void Encode_JsonElement_ParsesProperly()
    {
        using var doc = JsonDocument.Parse("{\"bool\": true, \"int16\": -500, \"uint16\": 60000, \"int32\": -100000, \"uint32\": 200000, \"real\": 12.5, \"double\": 12345.6789}");
        var root = doc.RootElement;

        Assert.Equal((byte)1, WireValue.Encode(root.GetProperty("bool"), PlcDataType.Bool, PlcByteOrder.BigEndian)[0]);
        Assert.Equal((short)-500, WireValue.Decode(WireValue.Encode(root.GetProperty("int16"), PlcDataType.Int16, PlcByteOrder.BigEndian), PlcDataType.Int16, PlcByteOrder.BigEndian));
        Assert.Equal((ushort)60000, WireValue.Decode(WireValue.Encode(root.GetProperty("uint16"), PlcDataType.UInt16, PlcByteOrder.BigEndian), PlcDataType.UInt16, PlcByteOrder.BigEndian));
        Assert.Equal(-100000, WireValue.Decode(WireValue.Encode(root.GetProperty("int32"), PlcDataType.Int32, PlcByteOrder.BigEndian), PlcDataType.Int32, PlcByteOrder.BigEndian));
        Assert.Equal(200000u, WireValue.Decode(WireValue.Encode(root.GetProperty("uint32"), PlcDataType.UInt32, PlcByteOrder.BigEndian), PlcDataType.UInt32, PlcByteOrder.BigEndian));
        Assert.Equal(12.5f, (float)WireValue.Decode(WireValue.Encode(root.GetProperty("real"), PlcDataType.Real, PlcByteOrder.BigEndian), PlcDataType.Real, PlcByteOrder.BigEndian));
        Assert.Equal(12345.6789, (double)WireValue.Decode(WireValue.Encode(root.GetProperty("double"), PlcDataType.Double, PlcByteOrder.BigEndian), PlcDataType.Double, PlcByteOrder.BigEndian), 4);
    }

    [Fact]
    public void Decode_ThrowsOnPayloadLengthMismatch()
    {
        byte[] tooShort = [0x01, 0x02];
        Assert.Throws<InvalidDataException>(() => WireValue.Decode(tooShort, PlcDataType.Int32, PlcByteOrder.BigEndian));
    }

    [Fact]
    public void String_ThrowsNotSupportedException()
    {
        Assert.Throws<NotSupportedException>(() => WireValue.Size(PlcDataType.String));
        Assert.Throws<NotSupportedException>(() => WireValue.Encode("hello", PlcDataType.String, PlcByteOrder.BigEndian));
        Assert.Throws<NotSupportedException>(() => WireValue.Decode(new byte[4], PlcDataType.String, PlcByteOrder.BigEndian));
    }

    [Fact]
    public void Encode_NullValue_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => WireValue.Encode(null!, PlcDataType.Int16, PlcByteOrder.BigEndian));
    }
}
