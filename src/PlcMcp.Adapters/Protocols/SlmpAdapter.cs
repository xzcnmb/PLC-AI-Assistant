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

    public static (int Address, byte Device, ushort Points) ParseWrite(TagDefinition tag)
    {
        if (tag.SafetyClass != SafetyClass.Parameter)
            throw new InvalidOperationException($"Tag '{tag.Name}' has safety class '{tag.SafetyClass}'; only Parameter class tags can be written.");
        if (tag.DataType == PlcDataType.Bool)
            throw new InvalidOperationException("Direct bit writes are forbidden; only parameter registers can be written.");
        var match = Regex.Match(tag.NativeAddress, @"^(D|R|W|M|X|Y|B)([0-9A-F]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            throw new ArgumentException("SLMP supports D/R/W numeric and M/X/Y/B Bool devices.");
        var device = match.Groups[1].Value.ToUpperInvariant();
        if (device is "M" or "X" or "Y" or "B")
            throw new InvalidOperationException($"Device '{device}' is a bit/IO device and cannot be written as a parameter.");
        if (device is not ("D" or "R"))
            throw new InvalidOperationException($"Device '{device}' is not permitted for parameter writes. Only D or R registers are allowed.");
        if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var address) || address is < 0 or > 0xFFFFFF)
            throw new ArgumentException("SLMP address is outside the 24-bit device range.");
        var size = WireValue.Size(tag.DataType);
        var points = (ushort)(size / 2);
        if (address + points > 0x1000000)
            throw new ArgumentException("SLMP write crosses the device address limit.");
        byte deviceCode = device == "D" ? (byte)0xA8 : (byte)0xAF;
        return (address, deviceCode, points);
    }

    public static void ValidateValueRange(TagDefinition tag, object value)
    {
        if (tag.Minimum is null && tag.Maximum is null) return;
        var num = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (tag.Minimum is double min && num < min)
            throw new ArgumentOutOfRangeException(nameof(value), $"Value {num} is below minimum {min}.");
        if (tag.Maximum is double max && num > max)
            throw new ArgumentOutOfRangeException(nameof(value), $"Value {num} is above maximum {max}.");
    }

    public static byte[] BuildWrite(TagDefinition tag, object value)
    {
        var (address, device, points) = ParseWrite(tag);
        ValidateValueRange(tag, value);
        var data = WireValue.Encode(value, tag.DataType, tag.ByteOrder ?? PlcByteOrder.LittleEndian);
        if (data.Length != points * 2)
            throw new InvalidOperationException("Encoded data length does not match point count.");

        var request = new byte[21 + data.Length];
        request[0] = 0x50;
        request[3] = 0xFF;
        request[4] = 0xFF;
        request[5] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(7), (ushort)(12 + data.Length));
        request[9] = 16;
        request[11] = 1;
        request[12] = 0x14;
        request[13] = 0;
        request[15] = (byte)address;
        request[16] = (byte)(address >> 8);
        request[17] = (byte)(address >> 16);
        request[18] = device;
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(19), points);
        data.CopyTo(request.AsSpan(21));
        return request;
    }

    public static void VerifyWriteResponse(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        if (header.Length < 9)
            throw new InvalidDataException("SLMP response header too short.");
        if (header[0] != 0xD0 || header[1] != 0 || header[2] != 0 || header[3] != 0xFF ||
            header[4] != 0xFF || header[5] != 3 || header[6] != 0)
            throw new InvalidDataException("Invalid SLMP 3E response header.");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(7, 2));
        if (length < 2 || payload.Length < length)
            throw new InvalidDataException("SLMP response length mismatch.");
        var endCode = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2));
        if (endCode != 0)
            throw new InvalidDataException($"SLMP end code {endCode:X4}.");
    }

    public async Task<TagValue> WriteParameterAsync(TargetProfile target, TagDefinition tag, object value,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        if (target.Endpoint.Unit is not (null or 0))
            throw new ArgumentException("This SLMP profile supports only local station 0.");
        ParseWrite(tag);
        ValidateValueRange(tag, value);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(target.Endpoint.TimeoutMs);
        using var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(target.Endpoint.Host, target.Endpoint.Port, deadline.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            var request = BuildWrite(tag, value);
            await stream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            var header = await ReceiveAsync(stream, 9, deadline.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(7));
            if (length is < 2 or > 8192)
                throw new InvalidDataException("Invalid SLMP 3E response length.");
            var payload = await ReceiveAsync(stream, length, deadline.Token).ConfigureAwait(false);
            VerifyWriteResponse(header, payload);
            return Value(tag, value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("SLMP write timed out; connection was closed.");
        }
    }

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
