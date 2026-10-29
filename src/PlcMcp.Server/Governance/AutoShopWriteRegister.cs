using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using PlcMcp.Contracts.Models;
using PlcMcp.Adapters.Protocols;

namespace PlcMcp.Server.Governance;

/// <summary>
/// Options for <see cref="AutoShopWriteRegisterService"/>. The transport is
/// injectable so tests run fully offline against a fake Modbus TCP server.
/// </summary>
public sealed class AutoShopWriteRegisterOptions
{
    /// <summary>Per-call timeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Overrides the transport for tests: (host, port) -> stream. When set, no
    /// real TCP connection is opened.
    /// </summary>
    public Func<string, int, Task<Stream>>? TransportFactory { get; init; }
}

/// <summary>Report of plc_autoshop_write_register. Honest: readback is evidence, never assumed.</summary>
public sealed record AutoShopWriteRegisterReport(
    bool Success,
    string Status,
    string TargetId,
    string Address,
    object? Value,
    string Host,
    int Port,
    ushort ModbusAddress,
    byte FunctionCode,
    string Area,
    string Device,
    bool PhysicalWriteAllowed,
    object? ReadBackValue,
    string Message,
    string Disclaimer,
    string LogPath);

/// <summary>
/// Loopback-first Modbus TCP write executor for the AutoShop toolset (phase three,
/// F3): writes a single register (FC6) or single coil (FC5) to a configured Inovance
/// ModbusTcp target, then reads the same address back (FC3/FC1) for closed-loop
/// evidence.
///
/// Governance (mirrors docs/contracts.md and the inject-bridge posture):
/// - the tool is only registered behind the explicit --autoshop-bridge switch;
/// - by default ONLY simulation targets on 127.0.0.0/8 hosts are writable (the local
///   H5U software simulator); simulation targets on non-loopback hosts and physical
///   targets require the explicit --autoshop-allow-physical-write switch (default OFF);
/// - the Inovance soft-element mapping table is an unverified documentation default:
///   users must align it with the target project's Modbus mapping (capability stays
///   Experimental); a missing model/device row is an error, never a guess;
/// - frame construction follows the shared ModbusTcpAdapter MBAP conventions
///   (big-endian transaction/protocol/length, echo verification);
/// - every failure surfaces as a failure; nothing is ever reported as written when
///   the wire transaction did not complete.
/// </summary>
public sealed class AutoShopWriteRegisterService
{
    /// <summary>Honest-disclaimer text quoted in every report.</summary>
    public const string DisclaimerText =
        "Write path uses Modbus TCP FC5/FC6 against a configured target. By default only simulation " +
        "targets on 127.0.0.0/8 hosts are writable (the local H5U software simulator); physical targets " +
        "require the explicit --autoshop-allow-physical-write switch plus operator approval. The Inovance " +
        "soft-element mapping table is an unverified documentation default - align it with the target " +
        "project's Modbus address mapping before any physical use. Simulator results never prove " +
        "hardware behaviour.";

    private readonly AutoShopWriteRegisterOptions _options;
    private readonly Func<string, bool> _loopbackCheck;
    private readonly string _logPath;

    public AutoShopWriteRegisterService(string logPath, AutoShopWriteRegisterOptions? options = null)
    {
        _logPath = Path.GetFullPath(logPath);
        _options = options ?? new AutoShopWriteRegisterOptions();
        _loopbackCheck = address => string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (address.Split('.') is { Length: 4 } parts &&
             parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int v) && v is >= 0 and <= 255) &&
             address.StartsWith("127.", StringComparison.Ordinal));
    }

    public string LogPath => _logPath;

    /// <summary>
    /// Governance decision: writable when (a) the target is a simulation on a loopback
    /// host (local simulator, default allowed), or (b) the operator turned on
    /// --autoshop-allow-physical-write. Returns a refusal reason (null = allowed).
    /// </summary>
    public static string? ClassifyTarget(TargetProfile target, string host, bool allowPhysicalWrite)
    {
        bool loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (host.Split('.') is { Length: 4 } parts &&
             parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int v) && v is >= 0 and <= 255) &&
             host.StartsWith("127.", StringComparison.Ordinal));
        if (loopback)
        {
            return null; // the local simulator loopback: the only default-allowed surface
        }
        if (!target.IsSimulation)
        {
            return allowPhysicalWrite
                ? null // explicit operator switch: physical write requested by configuration
                : "Physical write is FORBIDDEN by default: target '" + target.Id + "' is not a simulation target. " +
                  "Enable --autoshop-allow-physical-write (operator decision, requires approval) to allow physical targets.";
        }
        return allowPhysicalWrite
            ? null
            : "Simulation target '" + target.Id + "' uses a non-loopback host '" + host + "'; only 127.0.0.0/8 " +
              "targets are writable by default. Enable --autoshop-allow-physical-write (operator decision) to allow it.";
    }

    /// <summary>
    /// Writes one Inovance soft element (or Modbus-native HR address) through Modbus
    /// TCP FC5 (single coil) / FC6 (single register) and reads it back. Double-word
    /// (DD) writes need FC16 and are NOT supported by this tool (fc5/fc6 only).
    /// </summary>
    public async Task<AutoShopWriteRegisterReport> WriteAsync(
        TargetProfile target,
        string address,
        object? value,
        bool allowPhysicalWrite,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (target.Vendor != PlcVendor.Inovance)
            {
                return await FailAsync(target, address, value, 0, 0, string.Empty, string.Empty, false,
                    $"Target '{target.Id}' has vendor '{target.Vendor}'; plc_autoshop_write_register only writes Inovance targets.",
                    cancellationToken).ConfigureAwait(false);
            }
            if (target.Endpoint.Transport != TransportKind.ModbusTcp ||
                !target.RuntimeProtocols.Contains(TransportKind.ModbusTcp))
            {
                return await FailAsync(target, address, value, 0, 0, string.Empty, string.Empty, false,
                    $"Target '{target.Id}' does not speak Modbus TCP; plc_autoshop_write_register only writes ModbusTcp targets.",
                    cancellationToken).ConfigureAwait(false);
            }

            string? refusal = ClassifyTarget(target, target.Endpoint.Host, allowPhysicalWrite);
            if (refusal is not null)
            {
                await LogAsync(target.Id, address, false, refusal).ConfigureAwait(false);
                return new AutoShopWriteRegisterReport(false, "Unsupported", target.Id, address, value,
                    target.Endpoint.Host, target.Endpoint.Port, 0, 0, string.Empty, string.Empty, allowPhysicalWrite,
                    null, refusal, DisclaimerText, _logPath);
            }

            // Map the address through the explicit Inovance table (no guessing).
            // Bit devices (X/Y/M/S/T/C coil areas) -> Bool + FC5; word devices
            // (D/R holding registers) -> UInt16 + FC6; DD -> FC16, unsupported here.
            (byte FunctionCode, ushort ModbusAddress, string Area, string Device, int DeviceNumber) mapped;
            try
            {
                mapped = MapWriteAddress(target, address);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                return await FailAsync(target, address, value, 0, 0, string.Empty, string.Empty, allowPhysicalWrite,
                    ex.Message, cancellationToken).ConfigureAwait(false);
            }

            if (mapped.FunctionCode == 5)
            {
                bool on = value switch
                {
                    null => throw new ArgumentException("Parameter 'value' is required for a coil write."),
                    bool b => b,
                    JsonElement { ValueKind: JsonValueKind.True } => true,
                    JsonElement { ValueKind: JsonValueKind.False } => false,
                    _ => Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0
                };
                return await WriteCoilAsync(target, address, value, mapped, on, allowPhysicalWrite, cancellationToken)
                    .ConfigureAwait(false);
            }
            return await WriteRegisterAsync(target, address, value, mapped, allowPhysicalWrite, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException
                       or ArgumentOutOfRangeException or InvalidDataException or TimeoutException
                       or SocketException or IOException)
        {
            string message = $"write_register failed: {ex.Message}";
            await LogAsync(target.Id, address, false, message).ConfigureAwait(false);
            return new AutoShopWriteRegisterReport(false, "Unsupported", target.Id, address, value,
                target.Endpoint.Host, target.Endpoint.Port, 0, 0, string.Empty, string.Empty, allowPhysicalWrite,
                null, message, DisclaimerText, _logPath);
        }
    }

    private async Task<AutoShopWriteRegisterReport> WriteCoilAsync(
        TargetProfile target, string address, object? value,
        (byte FunctionCode, ushort ModbusAddress, string Area, string Device, int DeviceNumber) mapped, bool on,
        bool allowPhysicalWrite, CancellationToken cancellationToken)
    {
        byte unit = (byte)(target.Endpoint.Unit ?? 1);
        const ushort transaction = 1;
        byte[] request = BuildFc5(transaction, unit, mapped.ModbusAddress, on);
        byte[] full = await TransactAsync(target, request, cancellationToken).ConfigureAwait(false);
        VerifyFc5Response(full, transaction, unit, mapped.ModbusAddress, on);

        // Closed loop: read the same coil back (FC1).
        bool readBack = await ReadBitBackAsync(target, mapped.ModbusAddress, unit, cancellationToken)
            .ConfigureAwait(false);
        bool success = readBack == on;
        string message = success
            ? $"Wrote {mapped.Device}{mapped.DeviceNumber} (Modbus {mapped.Area} address {mapped.ModbusAddress}, " +
              $"unit {unit}) via FC5: {on}. Readback via FC1: {readBack}. Loopback/simulator result only - " +
              "not hardware behaviour; align the soft-element mapping with the target project before physical use."
            : $"Wrote {mapped.Area} address {mapped.ModbusAddress} via FC5 ({on}) but the readback differs " +
              $"({readBack}); the write is NOT confirmed. Nothing is claimed as written.";
        await LogAsync(target.Id, address, success, message).ConfigureAwait(false);
        return new AutoShopWriteRegisterReport(success, success ? "Experimental" : "Unsupported",
            target.Id, address, on, target.Endpoint.Host, target.Endpoint.Port,
            mapped.ModbusAddress, mapped.FunctionCode, mapped.Area, mapped.Device, allowPhysicalWrite,
            readBack, message, DisclaimerText, _logPath);
    }

    private async Task<AutoShopWriteRegisterReport> WriteRegisterAsync(
        TargetProfile target, string address, object? value,
        (byte FunctionCode, ushort ModbusAddress, string Area, string Device, int DeviceNumber) mapped,
        bool allowPhysicalWrite, CancellationToken cancellationToken)
    {
        double numeric = value switch
        {
            null => throw new ArgumentException("Parameter 'value' is required for a register write."),
            JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
            _ => Convert.ToDouble(value, CultureInfo.InvariantCulture)
        };
        if (!double.IsFinite(numeric) || numeric < 0 || numeric > 65535 || numeric != Math.Floor(numeric))
        {
            throw new ArgumentException(
                $"Register value must be an integer 0..65535 (raw Modbus FC6 payload); got '{value}'.");
        }
        ushort raw = (ushort)numeric;

        byte unit = (byte)(target.Endpoint.Unit ?? 1);
        const ushort transaction = 1;
        byte[] request = BuildFc6(transaction, unit, mapped.ModbusAddress, raw);
        byte[] full = await TransactAsync(target, request, cancellationToken).ConfigureAwait(false);
        VerifyFc6Response(full, transaction, unit, mapped.ModbusAddress, raw);

        // Closed loop: read the same register back (FC3).
        ushort readBack = await ReadWordBackAsync(target, mapped.ModbusAddress, unit, cancellationToken)
            .ConfigureAwait(false);
        bool success = readBack == raw;
        string message = success
            ? $"Wrote register {mapped.Area} address {mapped.ModbusAddress} (unit {unit}) via FC6: {raw}. " +
              $"Readback via FC3: {readBack}. Loopback/simulator result only - not hardware behaviour; " +
              "align the soft-element mapping with the target project before physical use."
            : $"Wrote register {mapped.Area} address {mapped.ModbusAddress} via FC6 ({raw}) but the readback " +
              $"differs ({readBack}); the write is NOT confirmed. Nothing is claimed as written.";
        await LogAsync(target.Id, address, success, message).ConfigureAwait(false);
        return new AutoShopWriteRegisterReport(success, success ? "Experimental" : "Unsupported",
            target.Id, address, raw, target.Endpoint.Host, target.Endpoint.Port,
            mapped.ModbusAddress, mapped.FunctionCode, mapped.Area, mapped.Device, allowPhysicalWrite,
            readBack, message, DisclaimerText, _logPath);
    }

    private async Task<AutoShopWriteRegisterReport> FailAsync(
        TargetProfile target, string address, object? value,
        ushort modbusAddress, byte functionCode, string area, string device, bool allowPhysicalWrite,
        string message, CancellationToken cancellationToken)
    {
        await LogAsync(target.Id, address, false, message).ConfigureAwait(false);
        return new AutoShopWriteRegisterReport(false, "Unsupported", target.Id, address, value,
            target.Endpoint.Host, target.Endpoint.Port, modbusAddress, functionCode, area, device,
            allowPhysicalWrite, null, message, DisclaimerText, _logPath);
    }

    // ---- address mapping ---------------------------------------------------

    private static (byte FunctionCode, ushort ModbusAddress, string Area, string Device, int DeviceNumber) MapWriteAddress(
        TargetProfile target, string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("Parameter 'address' is required (e.g. 'D100' or 'M0').");
        }
        string raw = address.Trim();
        // Same explicit Inovance table as the runtime adapter (no guessing): a
        // TagDefinition whose DataType matches the device class (Bool on bit
        // devices, UInt16 on word devices, Int32 on DD; DD is refused by the
        // FC5/FC6-only rule after mapping). ByteOrder is declared BigEndian
        // (the table's documented default) so the mapping never guesses.
        var tag = new TagDefinition("write_target", raw, InferWriteDataType(raw), null, null, null,
            true, true, SafetyClass.Parameter, null, null, PlcByteOrder.BigEndian);
        InovanceMapping mapped = InovanceSoftAddress.Map(
            InovanceSoftAddress.ResolveModel(target.Model, target.Family), tag);
        if (mapped.Device == "DD" || mapped.Count > 1)
        {
            throw new InvalidOperationException(
                "Double-word (DD) writes need Modbus FC16; this tool only issues FC5/FC6 single " +
                "coil/register writes. Write the two halves with D addresses instead.");
        }
        string area = mapped.Area switch
        {
            ModbusArea.Coil => "coil",
            ModbusArea.HoldingRegister => "holding-register",
            _ => throw new InvalidOperationException(
                $"Mapped area '{mapped.Area}' is read-only (FC2/FC4); only coil (FC5) and holding-register (FC6) writes are allowed.")
        };
        return (mapped.Area == ModbusArea.Coil ? (byte)5 : (byte)6, mapped.Address, area, mapped.Device, mapped.DeviceNumber);
    }

    private static PlcDataType InferWriteDataType(string raw)
    {
        // Match the InovanceSoftAddress soft-element grammar to choose the data
        // type the mapping table expects (bit devices require Bool, word devices
        // a numeric type; DD is double-word and is refused by the FC5/FC6-only
        // rule after mapping). HR passthrough stays word.
        var match = System.Text.RegularExpressions.Regex.Match(
            raw, @"^(DD|D|R|X|Y|M|S|T|C|HR|IR|DI)([0-9]+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new ArgumentException(
                "Address must be an Inovance soft element (X/Y/M/S/T/C/D/R) or a Modbus-native " +
                "HR address (e.g. 'D100', 'M0', 'HR100').");
        }
        string prefix = match.Groups[1].Value.ToUpperInvariant();
        return prefix switch
        {
            "X" or "Y" or "M" or "S" or "T" or "C" => PlcDataType.Bool,
            "DD" => PlcDataType.Int32,
            _ => PlcDataType.UInt16
        };
    }

    // ---- wire helpers (MBAP conventions mirror ModbusTcpAdapter) ------------

    private static byte[] BuildFc5(ushort transaction, byte unit, ushort address, bool on)
    {
        var request = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(request, transaction);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), 6);
        request[6] = unit;
        request[7] = 5;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), address);
        request[10] = on ? (byte)0xFF : (byte)0x00;
        request[11] = 0;
        return request;
    }

    private static byte[] BuildFc6(ushort transaction, byte unit, ushort address, ushort value)
    {
        var request = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(request, transaction);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), 6);
        request[6] = unit;
        request[7] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), address);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10), value);
        return request;
    }

    private static void VerifyFc5Response(ReadOnlySpan<byte> response, ushort transaction, byte unit, ushort address, bool on)
    {
        if (response.Length < 12)
        {
            throw new InvalidDataException("Modbus FC5 response frame is too short.");
        }
        if (BinaryPrimitives.ReadUInt16BigEndian(response) != transaction || response[2] != 0 || response[3] != 0 ||
            response[6] != unit)
        {
            throw new InvalidDataException("Modbus FC5 response header/transaction mismatch.");
        }
        if (response[7] == (5 | 0x80))
        {
            throw new InvalidDataException($"Modbus exception code {response[9]:X2}.");
        }
        if (response[7] != 5 ||
            BinaryPrimitives.ReadUInt16BigEndian(response.Slice(8, 2)) != address ||
            response[10] != (on ? (byte)0xFF : (byte)0x00))
        {
            throw new InvalidDataException("Modbus FC5 response address/value echo mismatch.");
        }
    }

    private static void VerifyFc6Response(ReadOnlySpan<byte> response, ushort transaction, byte unit, ushort address, ushort value)
    {
        if (response.Length < 12)
        {
            throw new InvalidDataException("Modbus FC6 response frame is too short.");
        }
        if (BinaryPrimitives.ReadUInt16BigEndian(response) != transaction || response[2] != 0 || response[3] != 0 ||
            response[6] != unit)
        {
            throw new InvalidDataException("Modbus FC6 response header/transaction mismatch.");
        }
        if (response[7] == (6 | 0x80))
        {
            throw new InvalidDataException($"Modbus exception code {response[9]:X2}.");
        }
        if (response[7] != 6 ||
            BinaryPrimitives.ReadUInt16BigEndian(response.Slice(8, 2)) != address ||
            BinaryPrimitives.ReadUInt16BigEndian(response.Slice(10, 2)) != value)
        {
            throw new InvalidDataException("Modbus FC6 response address/value echo mismatch.");
        }
    }

    private async Task<byte[]> TransactAsync(TargetProfile target, byte[] request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Timeout);
        Stream stream = await ConnectAsync(target.Endpoint.Host, target.Endpoint.Port, deadline.Token)
            .ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            var header = await ReceiveAsync(stream, 7, deadline.Token).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            if (length is < 2 or > 254)
            {
                throw new InvalidDataException("Invalid Modbus response length.");
            }
            var payload = new byte[length - 1];
            await ReceiveIntoAsync(stream, payload, deadline.Token).ConfigureAwait(false);
            var full = new byte[7 + payload.Length];
            header.CopyTo(full, 0);
            payload.CopyTo(full, 7);
            return full;
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> ReadBitBackAsync(TargetProfile target, ushort address, byte unit, CancellationToken cancellationToken)
    {
        var tag = new TagDefinition("readback", $"C{address}", PlcDataType.Bool);
        byte[] request = ModbusTcpAdapter.BuildRead(1, unit, tag);
        byte[] full = await TransactAsync(target, request, cancellationToken).ConfigureAwait(false);
        if (full[7] == (1 | 0x80))
        {
            throw new InvalidDataException($"Modbus readback exception code {full[9]:X2}.");
        }
        return (full[9] & 1) != 0;
    }

    private async Task<ushort> ReadWordBackAsync(TargetProfile target, ushort address, byte unit, CancellationToken cancellationToken)
    {
        var tag = new TagDefinition("readback", $"HR{address}", PlcDataType.UInt16);
        byte[] request = ModbusTcpAdapter.BuildRead(1, unit, tag);
        byte[] full = await TransactAsync(target, request, cancellationToken).ConfigureAwait(false);
        if (full[7] == (3 | 0x80))
        {
            throw new InvalidDataException($"Modbus readback exception code {full[9]:X2}.");
        }
        return BinaryPrimitives.ReadUInt16BigEndian(full.AsSpan(9, 2));
    }

    private async Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        if (_options.TransportFactory is { } factory)
        {
            return await factory(host, port).ConfigureAwait(false);
        }
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReceiveAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        await ReceiveIntoAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    private static async Task ReceiveIntoAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new InvalidDataException("Modbus response stream ended before the frame was complete.");
            }
            read += n;
        }
    }

    private async Task LogAsync(string targetId, string address, bool success, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            string entry = System.Text.Json.JsonSerializer.Serialize(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                tool = "plc_autoshop_write_register",
                targetId,
                address,
                success,
                message
            });
            await File.AppendAllTextAsync(_logPath, entry + Environment.NewLine).ConfigureAwait(false);
        }
        catch
        {
            // Logging is best-effort; it must never turn a result into a fake success.
        }
    }
}
