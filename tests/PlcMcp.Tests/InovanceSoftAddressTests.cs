using PlcMcp.Adapters.Protocols;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Tests;

public sealed class InovanceSoftAddressTests
{
    private static TagDefinition Tag(string address, PlcDataType type, PlcByteOrder? order = null) =>
        new("value", address, type, ByteOrder: order);

    [Theory]
    [InlineData("Y0", 0)]
    [InlineData("M0", 1024)]
    [InlineData("M1023", 2047)]
    [InlineData("S0", 9216)]
    [InlineData("T0", 13312)]
    [InlineData("C0", 14336)]
    public void BitDevices_MapToCoilsPerTable(string address, int expected)
    {
        var mapped = InovanceSoftAddress.Map("H5U", Tag(address, PlcDataType.Bool));

        Assert.Equal(ModbusArea.Coil, mapped.Area);
        Assert.Equal((byte)1, mapped.Function);
        Assert.Equal((ushort)expected, mapped.Address);
        Assert.Equal((ushort)1, mapped.Count);
        Assert.True(mapped.IsBit);
    }

    [Theory]
    [InlineData("X0", 0)]
    [InlineData("X10", 8)]
    [InlineData("X177", 127)]
    public void InputX_MapsToDiscreteInputsWithOctalNumbering(string address, int expected)
    {
        var mapped = InovanceSoftAddress.Map("H5U", Tag(address, PlcDataType.Bool));

        Assert.Equal(ModbusArea.DiscreteInput, mapped.Area);
        Assert.Equal((byte)2, mapped.Function);
        Assert.Equal((ushort)expected, mapped.Address);
    }

    [Theory]
    [InlineData("D0", 0)]
    [InlineData("D100", 100)]
    [InlineData("R0", 32768)]
    [InlineData("R100", 32868)]
    public void WordDevices_MapToHoldingRegisters(string address, int expected)
    {
        var mapped = InovanceSoftAddress.Map("H3U", Tag(address, PlcDataType.Int16));

        Assert.Equal(ModbusArea.HoldingRegister, mapped.Area);
        Assert.Equal((byte)3, mapped.Function);
        Assert.Equal((ushort)expected, mapped.Address);
        Assert.Equal((ushort)1, mapped.Count);
        Assert.False(mapped.IsBit);
    }

    [Theory]
    [InlineData(PlcDataType.Int16, 1)]
    [InlineData(PlcDataType.UInt16, 1)]
    [InlineData(PlcDataType.Int32, 2)]
    [InlineData(PlcDataType.UInt32, 2)]
    [InlineData(PlcDataType.Real, 2)]
    [InlineData(PlcDataType.Double, 4)]
    public void WordDevices_RegisterCountFollowsDataType(PlcDataType type, int expectedCount)
    {
        var order = expectedCount > 1 ? PlcByteOrder.BigEndian : (PlcByteOrder?)null;
        var mapped = InovanceSoftAddress.Map("H5U", Tag("D100", type, order));

        Assert.Equal((ushort)expectedCount, mapped.Count);
    }

    [Fact]
    public void Dd_IsDoubleWordAccessOnTheDRegisterFile()
    {
        var mapped = InovanceSoftAddress.Map("H5U", Tag("DD300", PlcDataType.Int32, PlcByteOrder.BigEndian));

        Assert.Equal(ModbusArea.HoldingRegister, mapped.Area);
        Assert.Equal((byte)3, mapped.Function);
        Assert.Equal((ushort)300, mapped.Address);
        Assert.Equal((ushort)2, mapped.Count);
        Assert.Equal("DD", mapped.Device);
    }

    [Theory]
    [InlineData(PlcDataType.Int16)]
    [InlineData(PlcDataType.Bool)]
    public void Dd_RejectsNonDoubleWordTypes(PlcDataType type)
    {
        Assert.Throws<ArgumentException>(() => InovanceSoftAddress.Map("H5U", Tag("DD100", type)));
    }

    [Fact]
    public void BoolOnWordDevice_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => InovanceSoftAddress.Map("H5U", Tag("D100", PlcDataType.Bool)));
        Assert.Contains("numeric", ex.Message);
    }

    [Fact]
    public void NumericOnBitDevice_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => InovanceSoftAddress.Map("H5U", Tag("M0", PlcDataType.Int16)));
        Assert.Contains("Bool", ex.Message);
    }

    [Fact]
    public void UnknownModel_ThrowsWithSupportedModelList()
    {
        var ex = Assert.Throws<ArgumentException>(() => InovanceSoftAddress.Map("AM400", Tag("D100", PlcDataType.Int16)));
        Assert.Contains("AM400", ex.Message);
        Assert.Contains("H3U", ex.Message);
        Assert.Contains("H5U", ex.Message);
    }

    [Fact]
    public void DeviceAboveModelLimit_Throws()
    {
        Assert.Throws<ArgumentException>(() => InovanceSoftAddress.Map("H5U", Tag("D32768", PlcDataType.Int16)));
        Assert.Throws<ArgumentException>(() => InovanceSoftAddress.Map("H5U", Tag("X2000", PlcDataType.Bool)));
    }

    [Fact]
    public void MappedAddressBeyondModbusSpace_Throws()
    {
        // R32767 maps to holding register 65535; a Double needs four registers.
        Assert.Throws<ArgumentException>(() =>
            InovanceSoftAddress.Map("H5U", Tag("R32767", PlcDataType.Double, PlcByteOrder.BigEndian)));
    }

    [Fact]
    public void MultiWordRequiresExplicitByteOrder()
    {
        var ex = Assert.Throws<ArgumentException>(() => InovanceSoftAddress.Map("H5U", Tag("D100", PlcDataType.Real)));
        Assert.Contains("ByteOrder", ex.Message);
        Assert.Contains("BigEndian", ex.Message);
    }

    [Fact]
    public void SingleWordFallsBackToModelDefaultByteOrder()
    {
        var mapped = InovanceSoftAddress.Map("H5U", Tag("D100", PlcDataType.Int16));
        Assert.Equal(PlcByteOrder.BigEndian, mapped.ByteOrder);

        var explicitOrder = InovanceSoftAddress.Map("H5U", Tag("D100", PlcDataType.Int16, PlcByteOrder.ByteSwap));
        Assert.Equal(PlcByteOrder.ByteSwap, explicitOrder.ByteOrder);
    }

    [Theory]
    [InlineData("HR100", ModbusArea.HoldingRegister, 100, 3)]
    [InlineData("IR100", ModbusArea.InputRegister, 100, 4)]
    [InlineData("DI100", ModbusArea.DiscreteInput, 100, 2)]
    public void ModbusNativeAddresses_PassThroughWithoutModelTable(string address, ModbusArea area, int expected, byte function)
    {
        var type = function < 3 ? PlcDataType.Bool : PlcDataType.Int16;
        var mapped = InovanceSoftAddress.Map("unknown-model", Tag(address, type));

        Assert.Equal(area, mapped.Area);
        Assert.Equal(function, mapped.Function);
        Assert.Equal((ushort)expected, mapped.Address);
        Assert.Equal("modbus-native", mapped.Model);
    }

    [Theory]
    [InlineData("HR100", PlcDataType.Bool)]
    [InlineData("IR100", PlcDataType.Bool)]
    [InlineData("DI100", PlcDataType.Int16)]
    public void ModbusNative_TypeFunctionMismatch_Throws(string address, PlcDataType type)
    {
        Assert.Throws<ArgumentException>(() => InovanceSoftAddress.Map("H5U", Tag(address, type)));
    }

    [Fact]
    public void ModbusNative_BeyondModbusSpace_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            InovanceSoftAddress.Map("H5U", Tag("HR65535", PlcDataType.Double, PlcByteOrder.BigEndian)));
    }

    [Theory]
    [InlineData(" d100 ")]
    [InlineData("D100")]
    [InlineData("dd100")]
    public void DevicePrefixIsCaseInsensitiveAndTrimmed(string address)
    {
        var type = address.Trim().ToUpperInvariant().StartsWith("DD") ? PlcDataType.Int32 : PlcDataType.Int16;
        var mapped = InovanceSoftAddress.Map("h5u", Tag(address, type, PlcByteOrder.BigEndian));

        Assert.Equal((ushort)100, mapped.Address);
        Assert.Equal("H5U", mapped.Model);
    }

    [Theory]
    [InlineData("Q0")]
    [InlineData("MW100")]
    [InlineData("")]
    [InlineData("D")]
    public void UnsupportedGrammar_ThrowsWithBothGrammarsListed(string address)
    {
        var ex = Assert.Throws<ArgumentException>(() => InovanceSoftAddress.Map("H5U", Tag(address, PlcDataType.Int16)));
        Assert.Contains("X/Y/M/S/T/C/D/R/DD", ex.Message);
        Assert.Contains("HR/IR/DI", ex.Message);
    }

    [Fact]
    public void StringDataType_ThrowsNotSupportedLikeTheOtherProtocols()
    {
        Assert.Throws<NotSupportedException>(() => InovanceSoftAddress.Map("H5U", Tag("D100", PlcDataType.String)));
    }

    [Fact]
    public void SupportedModels_ContainsH3UAndH5U()
    {
        Assert.Equal(["H3U", "H5U"], InovanceSoftAddress.SupportedModels);
    }

    [Fact]
    public void ResolveModel_PrefersModelOverFamilyAndNormalizes()
    {
        Assert.Equal("H5U", InovanceSoftAddress.ResolveModel(" h5u ", "H3U"));
        Assert.Equal("H3U", InovanceSoftAddress.ResolveModel(null, " h3u "));
        Assert.Equal(string.Empty, InovanceSoftAddress.ResolveModel(null, null));
    }

    [Fact]
    public void MappingTable_RangesAreDisjointAndInsideModbusSpace()
    {
        foreach (var model in InovanceSoftAddress.Models)
        {
            var byArea = model.Devices.Values.GroupBy(d => d.Area);
            foreach (var group in byArea)
            {
                var ranges = group.Select(d => (d.Device, From: d.Base, To: d.Base + d.DeviceLimit + 1)).ToArray();
                foreach (var (device, from, to) in ranges)
                {
                    Assert.InRange(from, 0, ModbusAddressSpaceCeiling);
                    Assert.True(to <= ModbusAddressSpaceCeiling,
                        $"Model '{model.Model}' device '{device}' ends at {to}, beyond the Modbus address space.");
                }
                for (var i = 0; i < ranges.Length; i++)
                for (var j = i + 1; j < ranges.Length; j++)
                {
                    // DD is the intentional double-word overlay of the D register file.
                    var pair = new[] { ranges[i].Device, ranges[j].Device };
                    if (pair.Order(StringComparer.Ordinal).SequenceEqual(new[] { "D", "DD" }))
                        continue;
                    Assert.True(ranges[i].To <= ranges[j].From || ranges[j].To <= ranges[i].From,
                        $"Model '{model.Model}' devices '{ranges[i].Device}' and '{ranges[j].Device}' overlap in {group.Key}.");
                }
            }
        }
    }

    private const int ModbusAddressSpaceCeiling = 65536;
}
