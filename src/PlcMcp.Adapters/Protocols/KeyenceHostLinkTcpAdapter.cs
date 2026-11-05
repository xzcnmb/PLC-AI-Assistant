using System.Globalization;
using System.Net.Sockets;
using System.Text;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Adapters.Protocols;

/// <summary>
/// KEYENCE KV-series runtime adapter communicating over the HostLink ASCII protocol (TCP port 8501).
/// </summary>
/// <remarks>
/// Wire format calibrated against KV STUDIO 12G 12.42.0.0 simulator (ACCommServer2):
///   Text commands terminated by '\r' (CR), ASCII response lines:
///     Single word read  : RD DM{addr}\r        -> reply: 00000 (5-digit decimal)
///     Sequence word read: RDS DM{addr} {count}\r -> reply: 01234 00000 00000 (space-separated 5-digit decimal words)
///     Single word write : WR DM{addr} {val}\r   -> reply: OK (negative values accepted, e.g. WR DM100 -567\r)
/// Only 16-bit word operations (Int16/UInt16) are exposed. Multi-word addresses are batched using RDS
/// when addresses are strictly consecutive (up to <see cref="MaxWordsPerFrame"/>).
/// </remarks>
public sealed class KeyenceHostLinkTcpAdapter : ReadOnlyTcpAdapter
{
    public const int DefaultPort = 8501;
    public const string ProtocolName = "kv-hostlink-tcp";
    public const int MaxWordsPerFrame = 125;

    public override string AdapterId => "keyence-hostlink-tcp";
    public override PlcVendor Vendor => PlcVendor.Keyence;
    public override IReadOnlyList<TransportKind> Transports => [TransportKind.KvHostLinkTcp, TransportKind.KvCommTcp];

    public new IReadOnlyList<CapabilityDescriptor> Capabilities { get; } =
    [
        new("read_tags", CapabilityStatus.Experimental, "Keyence 8501 HostLink ASCII protocol tested with loopback fixtures."),
        new("write_tags", CapabilityStatus.Experimental, "Keyence 8501 HostLink ASCII parameter writes (WR DM)."),
        new("browse_symbols", CapabilityStatus.Supported, "Configured manifest only; not online symbol discovery."),
        new("diagnostics", CapabilityStatus.Experimental, "TCP reachability and HostLink RD DM0 probe; does not report CPU RUN/STOP."),
        new("compile", CapabilityStatus.Unsupported, "Vendor engineering worker is not installed."),
        new("download", CapabilityStatus.Unsupported, "Vendor engineering worker is not installed.")
    ];

    protected override void ValidateTag(TagDefinition tag, TargetProfile target) => ParseAddress(tag);

    /// <summary>
    /// Validates a tag for HostLink DM read. Only 16-bit words (Int16/UInt16) are supported.
    /// </summary>
    public static (ushort Address, ushort Count) ParseAddress(TagDefinition tag)
    {
        if (tag.ByteOrder is not (null or PlcByteOrder.BigEndian))
            throw new ArgumentException("Keyence HostLink wire words are ASCII text representing 16-bit values; only BigEndian (or default) is supported.");
        if (tag.DataType is not (PlcDataType.Int16 or PlcDataType.UInt16))
            throw new ArgumentException($"Keyence HostLink read supports only Int16/UInt16 words; '{tag.DataType}' is not supported.");
        return (KeyenceAclTcpAdapter.ParseDmAddress(tag.NativeAddress), 1);
    }

    /// <summary>
    /// Validates a tag for HostLink DM write. Requires Parameter safety class and Int16/UInt16 data type.
    /// </summary>
    public static (ushort Address, ushort Count) ParseWrite(TagDefinition tag)
    {
        if (tag.SafetyClass != SafetyClass.Parameter)
            throw new InvalidOperationException($"Tag '{tag.Name}' has safety class '{tag.SafetyClass}'; only Parameter class tags can be written.");
        if (tag.DataType == PlcDataType.Bool)
            throw new InvalidOperationException("Direct bit writes are forbidden; only parameter registers can be written.");
        if (tag.DataType is not (PlcDataType.Int16 or PlcDataType.UInt16))
            throw new InvalidOperationException($"Keyence HostLink write supports only Int16/UInt16 words; '{tag.DataType}' is not supported.");
        return (KeyenceAclTcpAdapter.ParseDmAddress(tag.NativeAddress), 1);
    }

    /// <summary>
    /// Validates that a numeric value is within the tag's configured Minimum and Maximum range.
    /// </summary>
    public static void ValidateValueRange(TagDefinition tag, object value)
    {
        if (tag.Minimum is null && tag.Maximum is null) return;
        var num = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (tag.Minimum is double min && num < min)
            throw new ArgumentOutOfRangeException(nameof(value), $"Value {num} is below minimum {min}.");
        if (tag.Maximum is double max && num > max)
            throw new ArgumentOutOfRangeException(nameof(value), $"Value {num} is above maximum {max}.");
    }

    /// <summary>
    /// Builds the single-word read command: "RD DM{address}\r".
    /// </summary>
    public static string BuildReadCommand(ushort address) => $"RD DM{address}\r";

    /// <summary>
    /// Builds the single-word read command bytes in ASCII.
    /// </summary>
    public static byte[] BuildRead(ushort address) => Encoding.ASCII.GetBytes(BuildReadCommand(address));

    /// <summary>
    /// Builds the single-word read command bytes for a tag.
    /// </summary>
    public static byte[] BuildRead(TagDefinition tag) => BuildRead(ParseAddress(tag).Address);

    /// <summary>
    /// Builds the sequence-word read command: "RDS DM{startAddress} {count}\r".
    /// </summary>
    public static string BuildReadSequenceCommand(ushort startAddress, ushort count)
    {
        if (count is < 1 or > MaxWordsPerFrame)
            throw new ArgumentOutOfRangeException(nameof(count), $"Count must be between 1 and {MaxWordsPerFrame}.");
        return $"RDS DM{startAddress} {count}\r";
    }

    /// <summary>
    /// Builds the sequence-word read command bytes in ASCII.
    /// </summary>
    public static byte[] BuildReadSequence(ushort startAddress, ushort count) =>
        Encoding.ASCII.GetBytes(BuildReadSequenceCommand(startAddress, count));

    /// <summary>
    /// Builds the single-word write command text: "WR DM{address} {value}\r".
    /// </summary>
    public static string BuildWriteCommand(TagDefinition tag, object value)
    {
        var (address, _) = ParseWrite(tag);
        ValidateValueRange(tag, value);
        string valStr = tag.DataType switch
        {
            PlcDataType.Int16 => Convert.ToInt16(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            PlcDataType.UInt16 => Convert.ToUInt16(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Unsupported data type '{tag.DataType}' for Keyence HostLink write.")
        };
        return $"WR DM{address} {valStr}\r";
    }

    /// <summary>
    /// Builds the single-word write command bytes in ASCII.
    /// </summary>
    public static byte[] BuildWrite(TagDefinition tag, object value) =>
        Encoding.ASCII.GetBytes(BuildWriteCommand(tag, value));

    /// <summary>
    /// Convenience helper for building raw WR command bytes with an Int16 value.
    /// </summary>
    public static byte[] BuildWrite(ushort address, short value) =>
        Encoding.ASCII.GetBytes($"WR DM{address} {value.ToString(CultureInfo.InvariantCulture)}\r");

    /// <summary>
    /// Parses a HostLink read response into 16-bit unsigned word values.
    /// </summary>
    public static ushort[] ParseReadWords(string response, int expectedCount)
    {
        if (expectedCount is < 1 or > MaxWordsPerFrame)
            throw new ArgumentOutOfRangeException(nameof(expectedCount), $"Expected count must be between 1 and {MaxWordsPerFrame}.");

        var trimmed = response.Trim();
        if (trimmed.Length == 0)
            throw new InvalidDataException("Keyence HostLink returned an empty read response.");

        if (trimmed.StartsWith('E') && (trimmed.Length == 2 || (trimmed.Length > 2 && char.IsWhiteSpace(trimmed[2]))))
            throw new InvalidDataException($"Keyence HostLink returned error response: '{trimmed}'.");

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != expectedCount)
            throw new InvalidDataException($"Keyence HostLink read expected {expectedCount} words but received {tokens.Length} (response: '{trimmed}').");

        var words = new ushort[expectedCount];
        for (int i = 0; i < expectedCount; i++)
        {
            if (!int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var val))
                throw new InvalidDataException($"Keyence HostLink returned invalid word token '{tokens[i]}'.");
            words[i] = unchecked((ushort)val);
        }
        return words;
    }

    /// <summary>
    /// Verifies that a write response line equals "OK".
    /// </summary>
    public static void VerifyWriteResponse(string response)
    {
        var trimmed = response.Trim();
        if (!string.Equals(trimmed, "OK", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Keyence HostLink write failed with response: '{trimmed}'.");
    }

    /// <summary>
    /// Verifies write response bytes.
    /// </summary>
    public static void VerifyWriteResponse(ReadOnlySpan<byte> response) =>
        VerifyWriteResponse(Encoding.ASCII.GetString(response));

    public override async Task<ProbeResult> ProbeAsync(TargetProfile target, CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(target.Endpoint.TimeoutMs);
        using var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(target.Endpoint.Host, target.Endpoint.Port, deadline.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await stream.WriteAsync(BuildRead(0), deadline.Token).ConfigureAwait(false);
            var response = await ReadResponseLineAsync(stream, deadline.Token).ConfigureAwait(false);
            var trimmed = response.Trim();
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return new(target.Id, true, false, "HOSTLINK_CONNECTED",
                    "TCP connected; Keyence HostLink ASCII 'RD DM0' acknowledged. CPU model/identity bytes are not decoded in this tier.",
                    DateTimeOffset.UtcNow);
            }

            return new(target.Id, true, false, "TCP_CONNECTED",
                $"TCP reachable; HostLink probe answered with unexpected response '{trimmed}'.",
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(target.Id, false, false, "TIMEOUT", "TCP connection timed out.", DateTimeOffset.UtcNow);
        }
        catch (SocketException ex)
        {
            return new(target.Id, false, false, "CONNECTION_FAILED", ex.SocketErrorCode.ToString(), DateTimeOffset.UtcNow);
        }
    }

    protected override async Task<IReadOnlyList<TagValue>> ReadConnectedAsync(NetworkStream stream, TargetProfile target,
        IReadOnlyList<TagDefinition> tags, CancellationToken cancellationToken)
    {
        var parsed = new (int Index, ushort Address)[tags.Count];
        for (int k = 0; k < tags.Count; k++)
        {
            var (address, _) = ParseAddress(tags[k]);
            parsed[k] = (k, address);
        }

        var result = new TagValue[tags.Count];
        var ordered = parsed.OrderBy(x => x.Address).ThenBy(x => x.Index).ToArray();
        int p = 0;
        while (p < ordered.Length)
        {
            int runEnd = p;
            while (runEnd + 1 < ordered.Length &&
                   (int)ordered[runEnd + 1].Address == (int)ordered[runEnd].Address + 1 &&
                   runEnd + 1 - p < MaxWordsPerFrame)
            {
                runEnd++;
            }

            ushort start = ordered[p].Address;
            int count = runEnd - p + 1;
            var request = count == 1 ? BuildRead(start) : BuildReadSequence(start, (ushort)count);
            await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            var responseLine = await ReadResponseLineAsync(stream, cancellationToken).ConfigureAwait(false);
            var words = ParseReadWords(responseLine, count);
            for (int j = p; j <= runEnd; j++)
            {
                var tag = tags[ordered[j].Index];
                object value = tag.DataType == PlcDataType.UInt16 ? (object)words[j - p] : unchecked((short)words[j - p]);
                result[ordered[j].Index] = Value(tag, value);
            }

            p = runEnd + 1;
        }

        return result;
    }

    /// <summary>
    /// Writes a single 16-bit parameter word using the "WR DM{address} {value}\r" command.
    /// </summary>
    public async Task<TagValue> WriteParameterAsync(TargetProfile target, TagDefinition tag, object value,
        CancellationToken cancellationToken = default)
    {
        ValidateTarget(target);
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
            var response = await ReadResponseLineAsync(stream, deadline.Token).ConfigureAwait(false);
            VerifyWriteResponse(response);
            return Value(tag, value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Keyence HostLink write timed out; connection was closed.");
        }
    }

    /// <summary>
    /// Reads a single non-empty line of response from the TCP stream, tolerating trailing \r, \n, \r\n, and whitespace.
    /// </summary>
    internal static async Task<string> ReadResponseLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await ReadRawLineAsync(stream, cancellationToken).ConfigureAwait(false);
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }
    }

    private static async Task<string> ReadRawLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (ms.Length > 0) break;
                throw new EndOfStreamException("Connection closed by peer before receiving response.");
            }
            byte b = buffer[0];
            if (b == (byte)'\r' || b == (byte)'\n')
            {
                break;
            }
            ms.WriteByte(b);
            if (ms.Length > 4096)
                throw new InvalidDataException("Keyence HostLink response line exceeded maximum allowed length (4096 bytes).");
        }

        return Encoding.ASCII.GetString(ms.ToArray());
    }
}