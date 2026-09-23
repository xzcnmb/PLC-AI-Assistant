using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Adapters.Protocols;

public sealed class ModbusTcpAdapter : ReadOnlyTcpAdapter
{
    public override string AdapterId => "modbus-tcp";
    public override PlcVendor Vendor => PlcVendor.Generic;
    public override IReadOnlyList<TransportKind> Transports => [TransportKind.ModbusTcp];

    private static (byte Function, ushort Address, ushort Count) Parse(TagDefinition tag)
    {
        var match = Regex.Match(tag.NativeAddress, @"^(HR|IR|C|DI)([0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !ushort.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var address))
            throw new ArgumentException("Modbus addresses must be zero-based HR100, IR100, C100 or DI100; vendor MW/D addresses require an explicit mapping.");
        var function = match.Groups[1].Value.ToUpperInvariant() switch { "HR" => (byte)3, "IR" => (byte)4, "C" => (byte)1, _ => (byte)2 };
        if ((function < 3) != (tag.DataType == PlcDataType.Bool))
            throw new ArgumentException("C/DI require Bool; HR/IR require a numeric data type.");
        var size = WireValue.Size(tag.DataType);
        if (size > 2 && tag.ByteOrder is null) throw new ArgumentException("32/64-bit Modbus values require explicit ByteOrder in the manifest.");
        var count = (ushort)Math.Max(1, size / 2);
        if (address + count > 65536) throw new ArgumentException("Modbus range exceeds address space.");
        return (function, address, count);
    }

    protected override void ValidateTag(TagDefinition tag) => Parse(tag);

    public static byte[] BuildRead(ushort transaction, byte unit, TagDefinition tag)
    {
        var (function, address, count) = Parse(tag);
        var request = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(request, transaction);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), 6);
        request[6] = unit;
        request[7] = function;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), address);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10), count);
        return request;
    }

    protected override async Task<IReadOnlyList<TagValue>> ReadConnectedAsync(NetworkStream stream, TargetProfile target,
        IReadOnlyList<TagDefinition> tags, CancellationToken cancellationToken)
    {
        var unit = target.Endpoint.Unit ?? 1;
        if (unit is < 1 or > 255) throw new ArgumentException("Modbus unit must be 1 to 255; broadcast unit 0 is not supported.");
        var result = new List<TagValue>();
        ushort transaction = 0;
        foreach (var tag in tags)
        {
            var request = BuildRead(++transaction, (byte)unit, tag);
            await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            var header = await ReceiveAsync(stream, 7, cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            if (BinaryPrimitives.ReadUInt16BigEndian(header) != transaction || header[2] != 0 || header[3] != 0 ||
                header[6] != unit || length is < 3 or > 254)
                throw new InvalidDataException("Modbus response header/transaction mismatch.");
            var payload = await ReceiveAsync(stream, length - 1, cancellationToken).ConfigureAwait(false);
            if (payload[0] == (request[7] | 0x80)) throw new InvalidDataException($"Modbus exception code {payload[1]:X2}.");
            var expectedBytes = tag.DataType == PlcDataType.Bool ? 1 : WireValue.Size(tag.DataType);
            if (payload[0] != request[7] || payload.Length != expectedBytes + 2 || payload[1] != expectedBytes)
                throw new InvalidDataException("Modbus function or byte count mismatch.");
            object value = tag.DataType == PlcDataType.Bool ? (payload[2] & 1) != 0 :
                WireValue.Decode(payload.AsSpan(2), tag.DataType, tag.ByteOrder ?? PlcByteOrder.BigEndian);
            result.Add(Value(tag, value));
        }
        return result;
    }
}
