using System.Globalization;
using System.Text.RegularExpressions;
using PlcMcp.Contracts.Models;
using S7.Net;

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
