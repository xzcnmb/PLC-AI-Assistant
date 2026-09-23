using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Adapters.Protocols;

public sealed class SlmpAdapter : ReadOnlyTcpAdapter
{
    public override string AdapterId => "mitsubishi-slmp-3e";
    public override PlcVendor Vendor => PlcVendor.Mitsubishi;
    public override IReadOnlyList<TransportKind> Transports => [TransportKind.Slmp];

    private static (int Address, byte Device, bool Bit) Parse(TagDefinition tag)
    {
        var match = Regex.Match(tag.NativeAddress, @"^(D|R|W|M|X|Y|B)([0-9A-F]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) throw new ArgumentException("SLMP supports D/R/W numeric and M/X/Y/B Bool devices.");
        var device = match.Groups[1].Value.ToUpperInvariant();
        var style = device is "X" or "Y" or "W" or "B" ? NumberStyles.AllowHexSpecifier : NumberStyles.None;
        if (!int.TryParse(match.Groups[2].Value, style, CultureInfo.InvariantCulture, out var address) || address is < 0 or > 0xFFFFFF)
            throw new ArgumentException("SLMP address is outside the 24-bit device range.");
        var bit = device is "M" or "X" or "Y" or "B";
        if (bit != (tag.DataType == PlcDataType.Bool)) throw new ArgumentException("Bit devices require Bool, word devices require a numeric type.");
        var size = WireValue.Size(tag.DataType);
        if (address + Math.Max(1, size / 2) > 0x1000000) throw new ArgumentException("SLMP read crosses the device address limit.");
        return (address, device switch { "D" => 0xA8, "R" => 0xAF, "W" => 0xB4, "M" => 0x90, "X" => 0x9C, "Y" => 0x9D, _ => (byte)0xA0 }, bit);
    }

    protected override void ValidateTag(TagDefinition tag) => Parse(tag);

    public static byte[] BuildRead(TagDefinition tag)
    {
        var (address, device, bit) = Parse(tag);
        var request = new byte[21];
        request[0] = 0x50;
        request[3] = 0xFF;
        request[4] = 0xFF;
        request[5] = 3;
        request[7] = 12;
        request[9] = 16;
        request[11] = 1;
        request[12] = 4;
        request[13] = bit ? (byte)1 : (byte)0;
        request[15] = (byte)address;
        request[16] = (byte)(address >> 8);
        request[17] = (byte)(address >> 16);
        request[18] = device;
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(19), (ushort)(bit ? 1 : WireValue.Size(tag.DataType) / 2));
        return request;
    }

    protected override async Task<IReadOnlyList<TagValue>> ReadConnectedAsync(NetworkStream stream, TargetProfile target,
        IReadOnlyList<TagDefinition> tags, CancellationToken cancellationToken)
    {
        if (target.Endpoint.Unit is not (null or 0)) throw new ArgumentException("This SLMP profile supports only local station 0.");
        var result = new List<TagValue>();
        foreach (var tag in tags)
        {
            await stream.WriteAsync(BuildRead(tag), cancellationToken).ConfigureAwait(false);
            var header = await ReceiveAsync(stream, 9, cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(7));
            if (header[0] != 0xD0 || header[1] != 0 || header[2] != 0 || header[3] != 0xFF ||
                header[4] != 0xFF || header[5] != 3 || header[6] != 0 || length is < 2 or > 8192)
                throw new InvalidDataException("Invalid SLMP 3E response header.");
            var payload = await ReceiveAsync(stream, length, cancellationToken).ConfigureAwait(false);
            var endCode = BinaryPrimitives.ReadUInt16LittleEndian(payload);
            if (endCode != 0) throw new InvalidDataException($"SLMP end code {endCode:X4}.");
            var size = WireValue.Size(tag.DataType);
            if (payload.Length != size + 2) throw new InvalidDataException("SLMP response length mismatch.");
            object value;
            if (tag.DataType == PlcDataType.Bool)
            {
                if (payload[2] is not (0x00 or 0x10)) throw new InvalidDataException("SLMP single-bit response has invalid nibble encoding.");
                value = payload[2] == 0x10;
            }
            else value = WireValue.Decode(payload.AsSpan(2), tag.DataType, tag.ByteOrder ?? PlcByteOrder.LittleEndian);
            result.Add(Value(tag, value));
        }
        return result;
    }
}
