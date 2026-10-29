using System.Globalization;
using System.Text.RegularExpressions;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Adapters.Protocols;

// Modbus region an Inovance soft element is mapped to. The region also selects the
// Modbus function code used on the wire (Coil=1, DiscreteInput=2, HoldingRegister=3, InputRegister=4).
public enum ModbusArea
{
    Coil,
    DiscreteInput,
    HoldingRegister,
    InputRegister
}

// One row of the explicit Inovance soft-element -> Modbus mapping table.
// Base is the zero-based Modbus address of device number 0; DeviceLimit is the inclusive
// maximum device number this row accepts; Radix is the radix of the numeric part
// (8 for octal X/Y per the H3U/H5U manuals, 10 otherwise).
public sealed record InovanceDeviceMap(
    string Device,
    ModbusArea Area,
    int Base,
    int DeviceLimit,
    int Radix,
    bool IsBit,
    bool IsDoubleWord = false);

// Per-model mapping table entry. DefaultByteOrder documents the word order the target
// project's Modbus mapping is expected to use; multi-word tags must still set ByteOrder
// explicitly on the tag (four-protocol convention, no guessing).
public sealed record InovanceModelMap(
    string Model,
    IReadOnlyDictionary<string, InovanceDeviceMap> Devices,
    PlcByteOrder DefaultByteOrder);

// Result of mapping one Inovance soft-element (or Modbus-native) address to Modbus TCP.
public sealed record InovanceMapping(
    string Model,
    string Device,
    int DeviceNumber,
    ModbusArea Area,
    byte Function,
    ushort Address,
    ushort Count,
    PlcByteOrder ByteOrder,
    bool IsBit);

// Explicit Inovance H5U/H3U soft-element -> Modbus TCP mapping.
//
// Policy (mirrors docs/inovance-integration-plan.md, agent A):
//   * No implicit conversion. A soft element is only readable through a row of the
//     per-model table below; a missing model/device row is an explicit error, never a guess.
//   * The table shipped here is an unverified documentation default. It has not been
//     qualified against real hardware; users must align it with the target project's
//     Modbus address mapping before production use (capability stays Experimental).
//   * HR/IR/DI are Modbus-native zero-based addresses and pass through unchanged; they do
//     not consult the model table. On Inovance targets the bare "C" prefix is the Inovance
//     counter bit device (per the plan's device list), not the generic Modbus coil form;
//     the generic coil form remains available on Generic-vendor Modbus targets.
//   * Byte order follows the four-protocol convention: 16-bit values need no order,
//     32/64-bit values must declare ByteOrder explicitly on the tag.
public static class InovanceSoftAddress
{
    private const int ModbusAddressSpace = 65536;

    // Soft elements per the H5U/H3U manual semantics listed in the integration plan:
    // bit X/Y/M/S/T/C, word D/R, double word DD.
    private static readonly Regex SoftElementExpression = new(
        @"^(DD|D|R|X|Y|M|S|T|C)([0-9]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Modbus-native grammar of ModbusTcpAdapter minus "C" (see class comment on precedence).
    private static readonly Regex ModbusNativeExpression = new(
        @"^(HR|IR|DI)([0-9]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Shared default layout for the shipped models. All rows are disjoint and stay inside
    // the 16-bit Modbus address space; the invariant is covered by unit tests. Adjust per
    // model when hardware evidence arrives.
    private static InovanceModelMap CreateModel(string model) => new(model, new Dictionary<string, InovanceDeviceMap>(StringComparer.OrdinalIgnoreCase)
    {
        ["X"] = new("X", ModbusArea.DiscreteInput, 0, 1023, 8, true),
        ["Y"] = new("Y", ModbusArea.Coil, 0, 1023, 8, true),
        ["M"] = new("M", ModbusArea.Coil, 1024, 8191, 10, true),
        ["S"] = new("S", ModbusArea.Coil, 9216, 4095, 10, true),
        ["T"] = new("T", ModbusArea.Coil, 13312, 1023, 10, true),
        ["C"] = new("C", ModbusArea.Coil, 14336, 1023, 10, true),
        ["D"] = new("D", ModbusArea.HoldingRegister, 0, 32767, 10, false),
        ["R"] = new("R", ModbusArea.HoldingRegister, 32768, 32767, 10, false),
        ["DD"] = new("DD", ModbusArea.HoldingRegister, 0, 32767, 10, false, true)
    }, PlcByteOrder.BigEndian);

    private static readonly InovanceModelMap[] ModelTable =
    [
        CreateModel("H3U"),
        CreateModel("H5U")
    ];

    public static IReadOnlyList<InovanceModelMap> Models { get; } = ModelTable;

    public static IReadOnlyList<string> SupportedModels { get; } = ModelTable.Select(m => m.Model).ToArray();

    // Callers pass model when set, otherwise family; the resolved string must match a table key.
    public static string ResolveModel(string? model, string? family) =>
        (model ?? family ?? string.Empty).Trim().ToUpperInvariant();

    public static InovanceMapping Map(string model, TagDefinition tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var size = WireValue.Size(tag.DataType);
        var raw = tag.NativeAddress?.Trim() ?? string.Empty;
        var soft = SoftElementExpression.Match(raw);
        if (soft.Success)
            return MapSoftElement(model?.Trim().ToUpperInvariant() ?? string.Empty, tag, size, soft);
        var native = ModbusNativeExpression.Match(raw);
        if (native.Success)
            return MapModbusNative(tag, size, native);
        throw new ArgumentException(
            $"Inovance address '{tag.NativeAddress}' is neither a supported soft element (X/Y/M/S/T/C/D/R/DD) " +
            "nor a Modbus-native address (HR/IR/DI).");
    }

    private static InovanceMapping MapSoftElement(string model, TagDefinition tag, int size, Match match)
    {
        var device = match.Groups[1].Value.ToUpperInvariant();
        var digits = match.Groups[2].Value;
        var modelMap = ModelTable.FirstOrDefault(m => m.Model == model)
            ?? throw new ArgumentException(
                $"No Inovance soft-element mapping table for model '{model}'. Supported models: {string.Join(", ", SupportedModels)}. " +
                "Add or adjust the table to match the target project instead of guessing an address.");
        if (!modelMap.Devices.TryGetValue(device, out var deviceMap))
            throw new ArgumentException(
                $"Inovance device '{device}' has no mapping row for model '{model}'. Supported devices: " +
                $"{string.Join("/", modelMap.Devices.Keys)}.");
        if (deviceMap.IsBit != (tag.DataType == PlcDataType.Bool))
            throw new ArgumentException(deviceMap.IsBit
                ? $"Inovance bit device '{device}' requires a Bool tag; '{tag.DataType}' is a numeric data type."
                : $"Inovance word device '{device}' requires a numeric tag; Bool is only valid on X/Y/M/S/T/C.");
        if (deviceMap.IsDoubleWord && size < 4)
            throw new ArgumentException(
                $"Inovance double-word device 'DD' requires a 32/64-bit data type (Int32, UInt32, Real or Double), not '{tag.DataType}'.");
        if (!TryParseNumber(digits, deviceMap.Radix, out var deviceNumber))
            throw new ArgumentException(
                $"Inovance device '{device}' numbers use radix {deviceMap.Radix} for model '{model}'; '{digits}' is not a valid number.");
        if (deviceNumber > deviceMap.DeviceLimit)
            throw new ArgumentException(
                $"Inovance address '{tag.NativeAddress}' exceeds the '{model}' table limit {deviceMap.DeviceLimit} for device '{device}'.");
        var order = ResolveOrder(tag, modelMap.DefaultByteOrder, $"Inovance model '{modelMap.Model}'", size);
        var count = (ushort)Math.Max(1, size / 2);
        var address = deviceMap.Base + deviceNumber;
        if (address + count > ModbusAddressSpace)
            throw new ArgumentException(
                $"Inovance address '{tag.NativeAddress}' maps to Modbus address {address} (+{count}), beyond the 65536 Modbus address space.");
        return new InovanceMapping(modelMap.Model, device, deviceNumber, deviceMap.Area,
            FunctionFor(deviceMap.Area), (ushort)address, count, order, deviceMap.IsBit);
    }

    private static InovanceMapping MapModbusNative(TagDefinition tag, int size, Match match)
    {
        var prefix = match.Groups[1].Value.ToUpperInvariant();
        if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var address))
            throw new ArgumentException($"Modbus-native address '{tag.NativeAddress}' is not a zero-based decimal address.");
        var function = prefix switch { "HR" => (byte)3, "IR" => (byte)4, _ => (byte)2 };
        var area = prefix switch
        {
            "HR" => ModbusArea.HoldingRegister,
            "IR" => ModbusArea.InputRegister,
            _ => ModbusArea.DiscreteInput
        };
        if ((function < 3) != (tag.DataType == PlcDataType.Bool))
            throw new ArgumentException("HR/IR require a numeric data type; DI requires Bool.");
        var order = ResolveOrder(tag, PlcByteOrder.BigEndian, $"Modbus-native address '{tag.NativeAddress}'", size);
        var count = (ushort)Math.Max(1, size / 2);
        if (address + count > ModbusAddressSpace)
            throw new ArgumentException($"Modbus-native address '{tag.NativeAddress}' exceeds the 65536 Modbus address space.");
        return new InovanceMapping("modbus-native", prefix, address, area, function, (ushort)address, count, order, function < 3);
    }

    private static PlcByteOrder ResolveOrder(TagDefinition tag, PlcByteOrder fallback, string context, int size)
    {
        if (tag.ByteOrder is { } order) return order;
        if (size > 2)
            throw new ArgumentException(
                $"32/64-bit values require an explicit ByteOrder in the manifest; {context} documents '{fallback}' as the expected " +
                "order but the order is never guessed.");
        return fallback;
    }

    private static bool TryParseNumber(string digits, int radix, out int value)
    {
        value = 0;
        foreach (var ch in digits)
        {
            var digit = ch - '0';
            if (digit < 0 || digit >= radix || value > (int.MaxValue - digit) / radix)
                return false;
            value = value * radix + digit;
        }
        return true;
    }

    public static byte FunctionFor(ModbusArea area) => area switch
    {
        ModbusArea.Coil => 1,
        ModbusArea.DiscreteInput => 2,
        ModbusArea.HoldingRegister => 3,
        _ => 4
    };
}
