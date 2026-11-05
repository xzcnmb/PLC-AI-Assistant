using System.Buffers.Binary;
using System.Net.Sockets;
using PlcMcp.Contracts.Models;

namespace PlcMcp.Adapters.Protocols;

// Read-only KEYENCE KV-series runtime over the ACL binary protocol over TCP.
//
// Wire format from static reverse engineering of the installed KV STUDIO 12G 12.42.0.0
// binaries (KVComm.dll / LDKCOM.dll / LbkIp.dll; see kvrev/ACL_WIRE_FORMAT.md):
//   every TCP frame is a 10-byte big-endian header || ACL body (byte-verbatim):
//     [0..3] BE32 total frame length (ACL body + 10)
//     [4..5] BE16 ACL body length
//     [6..7] station/route bytes - semantics undetermined by the RE; sent as 0
//     [8..9] BE16 checksum = -(sum of header[0..7] + all body bytes) & 0xFFFF
//   DM word read : 13 <addr BE16> <count BE16>   -> reply: 13 00 <data words BE16>
//   PLC type qry : 03 (1 byte)                   -> reply: 03 00 <data words BE16>
// Undetermined items from the RE report are never assumed: only 16-bit word reads are
// exposed (multi-word 32/64-bit word order is unverified), reply status codes other
// than 0x00 are reported as unverified numerics, and the checksum coverage mask is
// pinned to the report's stated formula. Consecutive DM word addresses are merged into
// a single 0x13 frame (up to MaxWordsPerFrame) so a monitoring window needs far fewer
// round-trips; the reply words are mapped back to the caller's tag order.
// Loopback-tested only; hardware qualification pending. Physical writes are disabled:
// CanWrite is false, WriteAsync throws (inherited default), the runtime never routes
// writes here.
public sealed class KeyenceAclTcpAdapter : ReadOnlyTcpAdapter
{
    public const int DefaultPort = 8500;
    public const string ProtocolName = "kv-comm-tcp";
    public const int MaxWordsPerFrame = 125;

    public override string AdapterId => "keyence-acl-tcp";
    public override PlcVendor Vendor => PlcVendor.Keyence;
    public override IReadOnlyList<TransportKind> Transports => [TransportKind.KvCommTcp];

    protected override void ValidateTag(TagDefinition tag, TargetProfile target) => ParseAddress(tag);

    /// <summary>
    /// Validates a tag for the ACL DM read frame. Only 16-bit word types carry a verified
    /// wire mapping (one big-endian word); 32/64-bit word order is undetermined and rejected.
    /// </summary>
    public static (ushort Address, ushort Count) ParseAddress(TagDefinition tag)
    {
        if (tag.ByteOrder is not (null or PlcByteOrder.BigEndian))
            throw new ArgumentException("Keyence ACL wire words are big-endian; only BigEndian (or default) is supported for 16-bit reads.");
        if (tag.DataType is not (PlcDataType.Int16 or PlcDataType.UInt16))
            throw new ArgumentException(
                $"Keyence ACL read supports only Int16/UInt16 words; '{tag.DataType}' is not verified for this frame (multi-word order undetermined).");
        return (ParseDmAddress(tag.NativeAddress), 1);
    }

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
            await stream.WriteAsync(BuildFrame([0x03]), deadline.Token).ConfigureAwait(false);
            var header = await ReceiveAsync(stream, 10, deadline.Token).ConfigureAwait(false);
            var bodyLen = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            if (bodyLen is < 2 or > 4096)
                throw new InvalidDataException($"ACL type-query reply length {bodyLen} is outside the expected range.");
            var body = await ReceiveAsync(stream, bodyLen, deadline.Token).ConfigureAwait(false);
            if (!VerifyChecksum(header, body))
                throw new InvalidDataException("ACL reply checksum mismatch.");
            if (body[0] == 0x03 && body[1] == 0x00)
            {
                return new(target.Id, true, false, "ACL_CONNECTED",
                    "TCP connected; ACL 0x03 type query acknowledged (status 0). CPU model/identity bytes are not decoded in this tier.",
                    DateTimeOffset.UtcNow);
            }

            return new(target.Id, true, false, "TCP_CONNECTED",
                $"TCP reachable; ACL type query answered with unexpected echo 0x{body[0]:X2}/status 0x{body[1]:X2}.",
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
        // LDKCOM's KzReadDM reads a (start, count) window in one call; mirror that by merging
        // tags whose DM word addresses are consecutive into one frame (cap MaxWordsPerFrame).
        // Duplicate addresses never merge (the run requires strictly increasing +1), so every
        // tag resolves to a distinct word. Output stays in the caller's tag order.
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
            var request = BuildReadFrame(start, (ushort)count, stationRoute: 0);
            await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            var words = await ReadDmWordsAsync(stream, count, cancellationToken).ConfigureAwait(false);
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

    private static async Task<ushort[]> ReadDmWordsAsync(NetworkStream stream, int count, CancellationToken cancellationToken)
    {
        var header = await ReceiveAsync(stream, 10, cancellationToken).ConfigureAwait(false);
        var bodyLen = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        int expected = 2 + count * 2;
        if (bodyLen is < 2 or > 4096 || bodyLen < expected)
            throw new InvalidDataException($"ACL DM reply length {bodyLen} is outside the expected range (at least {expected}).");
        var body = await ReceiveAsync(stream, bodyLen, cancellationToken).ConfigureAwait(false);
        if (!VerifyChecksum(header, body))
            throw new InvalidDataException("ACL reply checksum mismatch (expected -(sum of header[0..7] + body) & 0xFFFF).");
        if (body.Length < expected || body[0] != 0x13)
            throw new InvalidDataException($"ACL reply did not echo DM read opcode 0x13 (got 0x{body[0]:X2}).");
        if (body[1] != 0x00)
            throw new InvalidDataException($"ACL DM read status 0x{body[1]:X2} (non-zero; the status table is unverified - see kvrev/ACL_WIRE_FORMAT.md).");
        var words = new ushort[count];
        for (int j = 0; j < count; j++)
        {
            words[j] = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2 + j * 2));
        }

        return words;
    }

    /// <summary>
    /// Parses a Keyence DM word address string (e.g. "1234", "D1234", "DM1234") into a 0..65535 word number.
    /// </summary>
    public static ushort ParseDmAddress(string address)
    {
        // ACL treats DM as a plain decimal word number; accept "1234", "D1234" or "DM1234".
        var s = address.Trim();
        if (s.StartsWith("DM", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        else if (s.StartsWith("D", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        if (!ushort.TryParse(s, out var word))
            throw new ArgumentException($"Keyence DM address '{address}' is not a valid 0..65535 word number.");
        return word;
    }

    /// <summary>Builds the 10-byte big-endian ACL/TCP header for a body.</summary>
    public static byte[] BuildHeader(ReadOnlySpan<byte> body, ushort stationRoute = 0)
    {
        if (body.Length is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(body), "ACL body length must be 1..65535.");
        var header = new byte[10];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length + 10);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)body.Length);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), stationRoute);
        ushort sum = 0;
        for (int i = 0; i < 8; i++) sum += header[i];
        foreach (var b in body) sum += b;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)(0 - sum));
        return header;
    }

    /// <summary>Builds a complete TCP frame: 10-byte header || ACL body.</summary>
    public static byte[] BuildFrame(ReadOnlySpan<byte> body, ushort stationRoute = 0)
    {
        var header = BuildHeader(body, stationRoute);
        var frame = new byte[header.Length + body.Length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame.AsSpan(header.Length));
        return frame;
    }

    /// <summary>Builds a DM word-read request frame: 13 <addr BE16> <count BE16>.</summary>
    public static byte[] BuildReadFrame(ushort address, ushort count, ushort stationRoute = 0)
    {
        if (count is < 1 or > MaxWordsPerFrame)
            throw new ArgumentOutOfRangeException(nameof(count), "DM word count must be 1..125 (conservative cap; the RE did not resolve the vendor maximum).");
        var body = new byte[5];
        body[0] = 0x13;
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(3), count);
        return BuildFrame(body, stationRoute);
    }

    /// <summary>Verifies the BE16 checksum at header[8..9] against header[0..7] + body.</summary>
    public static bool VerifyChecksum(ReadOnlySpan<byte> header, ReadOnlySpan<byte> body)
    {
        if (header.Length != 10) return false;
        ushort sum = 0;
        for (int i = 0; i < 8; i++) sum += header[i];
        foreach (var b in body) sum += b;
        return BinaryPrimitives.ReadUInt16BigEndian(header.Slice(8, 2)) == (ushort)(0 - sum);
    }
}