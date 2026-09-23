using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Adapters.Protocols;

public sealed class FinsTcpAdapter : ReadOnlyTcpAdapter
{
    public override string AdapterId => "omron-fins-tcp";
    public override PlcVendor Vendor => PlcVendor.Omron;
    public override IReadOnlyList<TransportKind> Transports => [TransportKind.Fins];

    private static (byte Area, ushort Word, byte Bit, ushort Count) Parse(TagDefinition tag)
    {
        var match = Regex.Match(tag.NativeAddress, @"^(DM|D|CIO|W|H|A)([0-9]+)(?:\.([0-9]{1,2}))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !ushort.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var word))
            throw new ArgumentException("FINS addresses must be D100, DM100, CIO0.00, W0.05, H0 or A0.");
        var bitAccess = tag.DataType == PlcDataType.Bool;
        if (bitAccess != match.Groups[3].Success) throw new ArgumentException("FINS Bool requires .bit; numeric types require a word address.");
        var bit = match.Groups[3].Success ? byte.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : (byte)0;
        if (bit > 15) throw new ArgumentException("FINS bit must be 0 through 15.");
        var size = WireValue.Size(tag.DataType);
        if (size > 2 && tag.ByteOrder is null) throw new ArgumentException("Multiword FINS values require explicit ByteOrder.");
        var count = (ushort)Math.Max(1, size / 2);
        if (word + count > 65536) throw new ArgumentException("FINS word range is outside address space.");
        byte area = match.Groups[1].Value.ToUpperInvariant() switch
        {
            "D" or "DM" => bitAccess ? (byte)0x02 : (byte)0x82,
            "CIO" => bitAccess ? (byte)0x30 : (byte)0xB0,
            "W" => bitAccess ? (byte)0x31 : (byte)0xB1,
            "H" => bitAccess ? (byte)0x32 : (byte)0xB2,
            _ => bitAccess ? (byte)0x33 : (byte)0xB3
        };
        return (area, word, bit, count);
    }

    protected override void ValidateTag(TagDefinition tag) => Parse(tag);

    public static byte[] BuildRead(byte source, byte destination, byte unit, byte sid, TagDefinition tag)
    {
        var (area, word, bit, count) = Parse(tag);
        var fins = new byte[18];
        fins[0] = 0x80;
        fins[2] = 2;
        fins[4] = destination;
        fins[5] = unit;
        fins[7] = source;
        fins[9] = sid;
        fins[10] = 1;
        fins[11] = 1;
        fins[12] = area;
        BinaryPrimitives.WriteUInt16BigEndian(fins.AsSpan(13), word);
        fins[15] = bit;
        BinaryPrimitives.WriteUInt16BigEndian(fins.AsSpan(16), count);
        return Wrap(2, fins);
    }

    private static byte[] Wrap(uint command, byte[] data)
    {
        var frame = new byte[16 + data.Length];
        Encoding.ASCII.GetBytes("FINS").CopyTo(frame, 0);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)(8 + data.Length));
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), command);
        data.CopyTo(frame, 16);
        return frame;
    }

    private static async Task<byte[]> ReceiveFrameAsync(NetworkStream stream, uint command, CancellationToken ct)
    {
        var header = await ReceiveAsync(stream, 16, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        if (Encoding.ASCII.GetString(header, 0, 4) != "FINS" || length > 8192 || length < (command == 1 ? 16u : 22u))
            throw new InvalidDataException("Invalid FINS/TCP header length or signature.");
        var error = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12));
        if (error != 0) throw new InvalidDataException($"FINS/TCP error {error:X8}.");
        if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)) != command)
            throw new InvalidDataException("Unexpected FINS/TCP command in response.");
        return await ReceiveAsync(stream, (int)length - 8, ct).ConfigureAwait(false);
    }

    protected override async Task<IReadOnlyList<TagValue>> ReadConnectedAsync(NetworkStream stream, TargetProfile target,
        IReadOnlyList<TagDefinition> tags, CancellationToken cancellationToken)
    {
        var unit = target.Endpoint.Unit ?? 0;
        if (unit is < 0 or > 255) throw new ArgumentException("FINS destination unit must be a byte.");
        await stream.WriteAsync(Wrap(0, new byte[4]), cancellationToken).ConfigureAwait(false);
        var handshake = await ReceiveFrameAsync(stream, 1, cancellationToken).ConfigureAwait(false);
        if (handshake.Length != 8) throw new InvalidDataException("FINS/TCP node handshake length mismatch.");
        var source = BinaryPrimitives.ReadUInt32BigEndian(handshake);
        var destination = BinaryPrimitives.ReadUInt32BigEndian(handshake.AsSpan(4));
        if (source is < 1 or > 254 || destination is < 1 or > 254) throw new InvalidDataException("FINS/TCP handshake returned invalid node numbers.");
        var result = new List<TagValue>();
        byte sid = 0;
        foreach (var tag in tags)
        {
            await stream.WriteAsync(BuildRead((byte)source, (byte)destination, (byte)unit, ++sid, tag), cancellationToken).ConfigureAwait(false);
            var reply = await ReceiveFrameAsync(stream, 2, cancellationToken).ConfigureAwait(false);
            if (reply.Length < 14 || (reply[0] & 0x40) == 0 || reply[4] != source || reply[7] != destination || reply[9] != sid || reply[10] != 1 || reply[11] != 1)
                throw new InvalidDataException("FINS response addressing, service id or command mismatch.");
            var endCode = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(12));
            if (endCode != 0) throw new InvalidDataException($"FINS end code {endCode:X4}.");
            var size = WireValue.Size(tag.DataType);
            if (reply.Length != 14 + size) throw new InvalidDataException("FINS data length mismatch.");
            if (tag.DataType == PlcDataType.Bool && reply[14] > 1) throw new InvalidDataException("Invalid FINS bit value.");
            result.Add(Value(tag, WireValue.Decode(reply.AsSpan(14), tag.DataType, tag.ByteOrder ?? PlcByteOrder.BigEndian)));
        }
        return result;
    }
}
