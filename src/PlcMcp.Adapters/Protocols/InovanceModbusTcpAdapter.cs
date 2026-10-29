using System.Buffers.Binary;
using System.Net.Sockets;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Adapters.Protocols;

// Read-only Inovance H5U/H3U runtime over Modbus TCP.
//
// Connection, batch budget, timeout, quality-code and per-tag semantics are inherited from
// ReadOnlyTcpAdapter; frame construction and write-response verification delegate to the
// public statics of ModbusTcpAdapter so the wire behaviour is identical to the generic
// Modbus profile (ModbusTcpAdapter's own public behaviour is untouched). Response validation
// mirrors ModbusTcpAdapter.ReadConnectedAsync because it exposes no reusable hook.
//
// The only difference to ModbusTcpAdapter is the address layer: Inovance soft elements are
// translated to Modbus function/address/count through the explicit InovanceSoftAddress table
// before the shared frame builder runs.
//
// Physical writes stay disabled: CanWrite is false (inherited default), WriteAsync throws
// NotSupportedException (inherited default), and the runtime never routes writes here.
// BuildWriteFrame/WriteParameterAsync exist only for frame generation and localhost loopback
// tests, exactly like the four existing physical adapters.
public sealed class InovanceModbusTcpAdapter : ReadOnlyTcpAdapter
{
    public override string AdapterId => "inovance-modbus-tcp";
    public override PlcVendor Vendor => PlcVendor.Inovance;
    public override IReadOnlyList<TransportKind> Transports => [TransportKind.ModbusTcp];

    protected override void ValidateTag(TagDefinition tag, TargetProfile target) =>
        InovanceSoftAddress.Map(InovanceSoftAddress.ResolveModel(target.Model, target.Family), tag);

    protected override async Task<IReadOnlyList<TagValue>> ReadConnectedAsync(NetworkStream stream, TargetProfile target,
        IReadOnlyList<TagDefinition> tags, CancellationToken cancellationToken)
    {
        var unit = target.Endpoint.Unit ?? 1;
        if (unit is < 1 or > 255) throw new ArgumentException("Modbus unit must be 1 to 255; broadcast unit 0 is not supported.");
        var model = InovanceSoftAddress.ResolveModel(target.Model, target.Family);
        var result = new List<TagValue>();
        ushort transaction = 0;
        foreach (var tag in tags)
        {
            var mapped = InovanceSoftAddress.Map(model, tag);
            var request = ModbusTcpAdapter.BuildRead(++transaction, (byte)unit, ToModbusTag(tag, mapped));
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
                WireValue.Decode(payload.AsSpan(2), tag.DataType, mapped.ByteOrder);
            result.Add(Value(tag, value));
        }
        return result;
    }

    // Parameter-write frame policy mirrors ModbusTcpAdapter: Parameter safety class only, no
    // bit/coil writes, D/R registers only, engineering range enforced. Frame generation only.
    public static (ushort Address, ushort Count) ParseWrite(TagDefinition tag, string model)
    {
        if (tag.SafetyClass != SafetyClass.Parameter)
            throw new InvalidOperationException($"Tag '{tag.Name}' has safety class '{tag.SafetyClass}'; only Parameter class tags can be written.");
        if (tag.DataType == PlcDataType.Bool)
            throw new InvalidOperationException("Direct bit/coil writes are forbidden; only parameter registers can be written.");
        var mapped = InovanceSoftAddress.Map(model, tag);
        if (mapped.Device is not ("D" or "R"))
            throw new InvalidOperationException(
                $"Inovance device '{mapped.Device}' is not permitted for parameter writes. Only D or R registers are allowed.");
        return (mapped.Address, mapped.Count);
    }

    public static byte[] BuildWriteFrame(ushort transaction, byte unit, string model, TagDefinition tag, object value)
    {
        var (address, _) = ParseWrite(tag, model);
        return ModbusTcpAdapter.BuildWrite(transaction, unit, new TagDefinition(tag.Name, $"HR{address}", tag.DataType,
            tag.Unit, tag.Minimum, tag.Maximum, tag.CanRead, tag.CanWrite, tag.SafetyClass,
            tag.Aliases, tag.Description, tag.ByteOrder), value);
    }

    // Loopback-test-only write path; identical shape to ModbusTcpAdapter.WriteParameterAsync.
    // Never reachable from Runtime/MCP: ProtocolRuntimeClient.WriteAsync always throws and
    // physical configurations must set canWrite=false.
    public async Task<TagValue> WriteParameterAsync(TargetProfile target, TagDefinition tag, object value,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        var model = InovanceSoftAddress.ResolveModel(target.Model, target.Family);
        var (address, count) = ParseWrite(tag, model);
        ModbusTcpAdapter.ValidateValueRange(tag, value);
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
            var request = BuildWriteFrame(transaction, (byte)unit, model, tag, value);
            await stream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            var header = await ReceiveAsync(stream, 7, deadline.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            if (length is < 2 or > 254)
                throw new InvalidDataException("Invalid Modbus response length.");
            var payload = await ReceiveAsync(stream, length - 1, deadline.Token).ConfigureAwait(false);
            var fullResponse = new byte[7 + payload.Length];
            header.CopyTo(fullResponse, 0);
            payload.CopyTo(fullResponse, 7);
            ModbusTcpAdapter.VerifyWriteResponse(fullResponse, transaction, (byte)unit, address, count);
            return Value(tag, value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("PLC write timed out; connection was closed.");
        }
    }

    // Presents the mapped Inovance address in the generic Modbus grammar so the shared
    // ModbusTcpAdapter frame builders can be reused unchanged.
    private static TagDefinition ToModbusTag(TagDefinition tag, InovanceMapping mapped) =>
        new(tag.Name, mapped.Area switch
        {
            ModbusArea.Coil => $"C{mapped.Address}",
            ModbusArea.DiscreteInput => $"DI{mapped.Address}",
            ModbusArea.HoldingRegister => $"HR{mapped.Address}",
            _ => $"IR{mapped.Address}"
        }, tag.DataType, tag.Unit, tag.Minimum, tag.Maximum, tag.CanRead, tag.CanWrite, tag.SafetyClass,
            tag.Aliases, tag.Description, tag.ByteOrder);
}
