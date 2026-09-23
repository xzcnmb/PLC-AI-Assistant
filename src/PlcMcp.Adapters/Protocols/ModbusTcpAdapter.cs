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

    public static (ushort Address, ushort Count) ParseWrite(TagDefinition tag)
    {
        if (tag.SafetyClass != SafetyClass.Parameter)
            throw new InvalidOperationException($"Tag '{tag.Name}' has safety class '{tag.SafetyClass}'; only Parameter class tags can be written.");
        if (tag.DataType == PlcDataType.Bool)
            throw new InvalidOperationException("Direct bit/coil writes are forbidden; only parameter registers can be written.");
        var match = Regex.Match(tag.NativeAddress, @"^(HR|IR|C|DI)([0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !ushort.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var address))
            throw new ArgumentException("Modbus addresses must be zero-based HR100, IR100, C100 or DI100; vendor MW/D addresses require an explicit mapping.");
        var prefix = match.Groups[1].Value.ToUpperInvariant();
        if (prefix != "HR")
            throw new InvalidOperationException($"Address prefix '{prefix}' is not permitted for parameter writes. Only Holding Registers (HR) are allowed.");
        var size = WireValue.Size(tag.DataType);
        if (size > 2 && tag.ByteOrder is null)
            throw new ArgumentException("32/64-bit Modbus values require explicit ByteOrder in the manifest.");
        var count = (ushort)(size / 2);
        if (address + count > 65536)
            throw new ArgumentException("Modbus range exceeds address space.");
        return (address, count);
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

    public static byte[] BuildWrite(ushort transaction, byte unit, TagDefinition tag, object value)
    {
        var (address, count) = ParseWrite(tag);
        ValidateValueRange(tag, value);
        var data = WireValue.Encode(value, tag.DataType, tag.ByteOrder ?? PlcByteOrder.BigEndian);
        if (data.Length != count * 2)
            throw new InvalidOperationException("Encoded data length does not match register count.");

        var request = new byte[13 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), transaction);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)(7 + data.Length));
        request[6] = unit;
        request[7] = 0x10;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), address);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10), count);
        request[12] = (byte)data.Length;
        data.CopyTo(request.AsSpan(13));
        return request;
    }

    public static void VerifyWriteResponse(ReadOnlySpan<byte> response, ushort expectedTransaction, byte expectedUnit, ushort expectedAddress, ushort expectedCount)
    {
        if (response.Length < 9)
            throw new InvalidDataException("Modbus response frame is too short.");
        var transaction = BinaryPrimitives.ReadUInt16BigEndian(response[..2]);
        var protocol = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(2, 2));
        var length = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(4, 2));
        var unit = response[6];
        var fc = response[7];

        if (transaction != expectedTransaction || protocol != 0 || unit != expectedUnit)
            throw new InvalidDataException("Modbus response header/transaction mismatch.");

        if (fc == 0x90)
        {
            var exceptionCode = response.Length > 8 ? response[8] : (byte)0;
            throw new InvalidDataException($"Modbus exception code {exceptionCode:X2}.");
        }

        if (fc != 0x10)
            throw new InvalidDataException($"Unexpected Modbus function code {fc:X2} in response.");

        if (response.Length < 12 || length != 6)
            throw new InvalidDataException("Modbus write response length mismatch.");

        var respAddress = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(8, 2));
        var respCount = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(10, 2));
        if (respAddress != expectedAddress || respCount != expectedCount)
            throw new InvalidDataException("Modbus write response address or quantity mismatch.");
    }

    public async Task<TagValue> WriteParameterAsync(TargetProfile target, TagDefinition tag, object value,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        var (address, count) = ParseWrite(tag);
        ValidateValueRange(tag, value);
        var unit = target.Endpoint.Unit ?? 1;
        if (unit is < 1 or > 255)
            throw new ArgumentException("Modbus unit must be 1 to 255; broadcast unit 0 is not supported.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(target.Endpoint.TimeoutMs);
        using var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(target.Endpoint.Host, target.Endpoint.Port, deadline.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            const ushort transaction = 1;
            var request = BuildWrite(transaction, (byte)unit, tag, value);
            await stream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            var header = await ReceiveAsync(stream, 7, deadline.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            if (length is < 2 or > 254)
                throw new InvalidDataException("Invalid Modbus response length.");
            var payload = await ReceiveAsync(stream, length - 1, deadline.Token).ConfigureAwait(false);
            var fullResponse = new byte[7 + payload.Length];
            header.CopyTo(fullResponse, 0);
            payload.CopyTo(fullResponse, 7);
            VerifyWriteResponse(fullResponse, transaction, (byte)unit, address, count);
            return Value(tag, value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("PLC write timed out; connection was closed.");
        }
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
