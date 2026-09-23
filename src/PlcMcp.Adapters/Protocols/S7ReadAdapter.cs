using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using PlcMcp.Contracts.Models;
using S7.Net;
using InvalidDataException = System.IO.InvalidDataException;

namespace PlcMcp.Adapters.Protocols;

public sealed class S7ReadAdapter : IPlcProtocolAdapter
{
    public string AdapterId => "siemens-s7comm";
    public PlcVendor Vendor => PlcVendor.Siemens;
    public IReadOnlyList<TransportKind> Transports => [TransportKind.S7Comm];
    public IReadOnlyList<CapabilityDescriptor> Capabilities { get; } =
    [
        new("read_tags", CapabilityStatus.Experimental, "S7netplus S7comm reads; actual CPU/firmware qualification pending. Optimized DB and S7comm-plus are not supported."),
        new("browse_symbols", CapabilityStatus.Supported, "Configured manifest only."),
        new("diagnostics", CapabilityStatus.Experimental, "S7 session negotiation only; not CPU identity or mode."),
        new("write_tags", CapabilityStatus.Unsupported, "Physical writes are not implemented."),
        new("compile", CapabilityStatus.Unsupported, "No engineering backend installed."),
        new("download", CapabilityStatus.Unsupported, "No engineering backend installed.")
    ];

    public static (DataType Area, int Db, int Offset, int? Bit) ParseAddress(TagDefinition tag)
    {
        var db = Regex.Match(tag.NativeAddress, @"^DB([0-9]+)\.DB([XBWDL])([0-9]+)(?:\.([0-7]))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var memory = Regex.Match(tag.NativeAddress, @"^([VMIQ])([BWDL]?)([0-9]+)(?:\.([0-7]))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!db.Success && !memory.Success) throw new ArgumentException("S7 addresses must be DB1.DBX0.0, DB1.DBW0, DB1.DBD0, VD100, VW100, M0.0, I0.0 or Q0.0.");
        var match = db.Success ? db : memory;
        var areaName = match.Groups[1].Value.ToUpperInvariant();
        var width = match.Groups[2].Value.ToUpperInvariant();
        if (!int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset is < 0 or > 0x1FFFFF)
            throw new ArgumentException("S7 byte offset is outside the 24-bit bit-address range.");
        var number = 0;
        if (db.Success && (!int.TryParse(areaName, out number) || number is < 1 or > 65535)) throw new ArgumentException("Invalid DB number.");
        var bit = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : (int?)null;
        var size = WireValue.Size(tag.DataType);
        if ((tag.DataType == PlcDataType.Bool) != bit.HasValue) throw new ArgumentException("S7 Bool requires .bit; numeric reads require a byte/word/dword address.");
        if (bit.HasValue && width is not ("" or "X")) throw new ArgumentException("S7 bit address must use X or no width.");
        var declaredSize = width switch { "B" => 1, "W" => 2, "D" => 4, "L" => 8, _ => size };
        if (declaredSize != size || offset + size > 0x200000) throw new ArgumentException("S7 address width does not match data type or exceeds address space.");
        var area = db.Success || areaName == "V" ? DataType.DataBlock : areaName switch
        {
            "M" => DataType.Memory, "I" => DataType.Input, "Q" => DataType.Output,
            _ => throw new ArgumentException("Unsupported S7 area.")
        };
        return (area, db.Success ? number : areaName == "V" ? 1 : 0, offset, bit);
    }

    public static (DataType Area, int Db, int Offset) ParseWriteAddress(TagDefinition tag)
    {
        if (tag.SafetyClass != SafetyClass.Parameter)
            throw new InvalidOperationException($"Tag '{tag.Name}' has safety class '{tag.SafetyClass}'; only Parameter class tags can be written.");
        if (tag.DataType == PlcDataType.Bool)
            throw new InvalidOperationException("Direct bit writes are forbidden; only parameter registers can be written.");

        var (area, db, offset, bit) = ParseAddress(tag);
        if (bit.HasValue)
            throw new InvalidOperationException("Bit access is forbidden for parameter writes.");
        if (area != DataType.DataBlock)
            throw new InvalidOperationException($"Area '{area}' is not permitted for parameter writes. Only DataBlock (DB / V) is allowed.");
        return (area, db, offset);
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

    public static byte[] BuildWriteFrame(ushort pduRef, TagDefinition tag, object value)
    {
        var (area, db, offset) = ParseWriteAddress(tag);
        ValidateValueRange(tag, value);
        var data = WireValue.Encode(value, tag.DataType, tag.ByteOrder ?? PlcByteOrder.BigEndian);

        var frame = new byte[35 + data.Length];
        // TPKT
        frame[0] = 0x03;
        frame[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)frame.Length);
        // COTP DT
        frame[4] = 0x02;
        frame[5] = 0xF0;
        frame[6] = 0x80;
        // S7comm Header
        frame[7] = 0x32;
        frame[8] = 0x01; // Job
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(9), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(11), pduRef);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(13), 14); // Parameter length = 14
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(15), (ushort)(4 + data.Length)); // Data length = 4 + data.Length
        // Parameter (Write Var)
        frame[17] = 0x05; // Function: Write Var
        frame[18] = 0x01; // Item count: 1
        frame[19] = 0x12; // Spec type
        frame[20] = 0x0A; // Address spec length: 10
        frame[21] = 0x10; // Syntax ID: S7Any
        frame[22] = 0x02; // Transport size: BYTE
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(23), (ushort)data.Length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(25), (ushort)db);
        frame[27] = 0x84; // DB area code
        var bitAddress = offset * 8;
        frame[28] = (byte)(bitAddress >> 16);
        frame[29] = (byte)(bitAddress >> 8);
        frame[30] = (byte)bitAddress;
        // Data item
        frame[31] = 0x00; // Return code reserved
        frame[32] = 0x04; // Transport size: Byte/Word/Dword
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(33), (ushort)(data.Length * 8)); // Bit count
        data.CopyTo(frame.AsSpan(35));
        return frame;
    }

    public static void VerifyWriteResponse(ReadOnlySpan<byte> response, ushort expectedPduRef)
    {
        if (response.Length < 22)
            throw new InvalidDataException("S7 response frame too short.");
        if (response[0] != 0x03 || response[1] != 0x00 || response[5] != 0xF0 || response[7] != 0x32 || response[8] != 0x03)
            throw new InvalidDataException("Invalid S7 response header.");
        var pduRef = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(11, 2));
        if (pduRef != expectedPduRef)
            throw new InvalidDataException("S7 PDU reference mismatch.");
        var error = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(17, 2));
        if (error != 0)
            throw new InvalidDataException($"S7 error code {error:X4}.");
        if (response[19] != 0x05)
            throw new InvalidDataException("Unexpected function in S7 write response.");
        var returnCode = response[21];
        if (returnCode != 0xFF)
            throw new InvalidDataException($"S7 write item failed with code {returnCode:X2}.");
    }

    public async Task<TagValue> WriteParameterAsync(TargetProfile target, TagDefinition tag, object value,
        CancellationToken cancellationToken = default)
    {
        var address = ParseWriteAddress(tag);
        ValidateValueRange(tag, value);
        var bytes = WireValue.Encode(value, tag.DataType, tag.ByteOrder ?? PlcByteOrder.BigEndian);
        using var client = CreateClient(target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(target.Endpoint.TimeoutMs);
        try
        {
            await client.OpenAsync(timeout.Token).ConfigureAwait(false);
            await client.WriteBytesAsync(address.Area, address.Db, address.Offset, bytes, timeout.Token).ConfigureAwait(false);
            return new(tag.Name, value, tag.DataType, tag.Unit, QualityCode.Good, DateTimeOffset.UtcNow, tag.NativeAddress);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("S7 write timed out; connection closed.");
        }
        catch (PlcException ex)
        {
            throw new InvalidDataException($"S7 write error: {ex.Message}", ex);
        }
    }

    private static Plc CreateClient(TargetProfile target)
    {
        var endpoint = target.Endpoint;
        if (target.IsSimulation || endpoint.Transport != TransportKind.S7Comm || string.IsNullOrWhiteSpace(endpoint.Host) ||
            endpoint.Port is < 1 or > 65535 || endpoint.TimeoutMs is < 100 or > 30000 ||
            endpoint.Rack is null or < 0 or > 7 || endpoint.Slot is null or < 0 or > 31)
            throw new ArgumentException("S7 requires an explicit rack (0..7), slot (0..31), valid host/port and timeout (100..30000 ms).");
        var cpu = target.Family.ToUpperInvariant() switch
        {
            "S7-200 SMART" => CpuType.S7200Smart,
            "S7-200" => CpuType.S7200,
            "S7-300" => CpuType.S7300,
            "S7-400" => CpuType.S7400,
            "S7-1200" => CpuType.S71200,
            "S7-1500" => CpuType.S71500,
            _ => throw new ArgumentException("Unsupported S7 family; choose an exact S7-200 SMART/200/300/400/1200/1500 family.")
        };
        return new Plc(cpu, endpoint.Host, endpoint.Port, (short)endpoint.Rack.Value, (short)endpoint.Slot.Value)
        { ReadTimeout = endpoint.TimeoutMs, WriteTimeout = endpoint.TimeoutMs };
    }

    public async Task<ProbeResult> ProbeAsync(TargetProfile target, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(target.Endpoint.TimeoutMs);
        try
        {
            await client.OpenAsync(timeout.Token).ConfigureAwait(false);
            return new(target.Id, true, false, "S7_SESSION", "S7 negotiation succeeded; CPU identity and RUN/STOP mode have not been read.", DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(target.Id, false, false, "TIMEOUT", "S7 negotiation timed out.", DateTimeOffset.UtcNow);
        }
        catch (PlcException ex)
        {
            return new(target.Id, false, false, "PROTOCOL_ERROR", ex.Message, DateTimeOffset.UtcNow);
        }
    }

    public async Task<IReadOnlyList<TagValue>> ReadAsync(TargetProfile target, IReadOnlyList<TagDefinition> tags,
        CancellationToken cancellationToken = default)
    {
        if (tags.Count is < 1 or > 128) throw new ArgumentException("Read batches must contain 1 to 128 tags.");
        var parsed = tags.Select(tag => (Tag: tag, Address: ParseAddress(tag))).ToArray();
        if (tags.Any(t => !t.CanRead)) throw new InvalidOperationException("Manifest denies reading one or more tags.");
        using var client = CreateClient(target);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(target.Endpoint.TimeoutMs);
        try
        {
            await client.OpenAsync(timeout.Token).ConfigureAwait(false);
            var result = new List<TagValue>();
            foreach (var (tag, address) in parsed)
            {
                var bytes = await client.ReadBytesAsync(address.Area, address.Db, address.Offset, WireValue.Size(tag.DataType), timeout.Token).ConfigureAwait(false);
                object value = address.Bit is int bit ? (bytes[0] & (1 << bit)) != 0 : WireValue.Decode(bytes, tag.DataType, tag.ByteOrder ?? PlcByteOrder.BigEndian);
                result.Add(new(tag.Name, value, tag.DataType, tag.Unit, QualityCode.Good, DateTimeOffset.UtcNow, tag.NativeAddress));
            }
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("S7 read timed out; connection closed.");
        }
    }
}
