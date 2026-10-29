using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlcMcp.Engineering.Workers.AutoShop;

/// <summary>
/// ITP (Inovance Test Protocol, internal name of the InoVarSharedService wire protocol) packet types.
/// Only <see cref="Register"/> is used by this read-only bridge; all other values are documented
/// from disassembly and are never constructed by this client.
/// </summary>
public enum ItpPacketType
{
    /// <summary>Register handshake: JSON payload { "Path": "..." }, answer { "Response": "&lt;code&gt;" }.</summary>
    Register = 1,

    /// <summary>Packet parse/relay request (server logs "ParsePacket packetID=%d").</summary>
    ParsePacket = 2,

    /// <summary>Shared-variable data transfer (server relay; binary payload layout not verified).</summary>
    DataTransferBase = 3,
}

/// <summary>
/// 28-byte fixed ITP packet header followed by the payload.
/// Layout verified empirically against InoVarSharedService on 127.0.0.1:13939 (2026-10-23) and by
/// disassembly of PluginCommunicate.dll (CPacketHandler::ProcessPacketHeader / IsValidCompletePacket).
/// </summary>
public readonly struct ItpPacketHeader
{
    /// <summary>Header magic, 0xA50EA50E on the wire as 0E A5 0E A5 (little-endian).</summary>
    public const uint Magic = 0xA50EA50E;

    /// <summary>Fixed header size in bytes.</summary>
    public const int HeaderSize = 28;

    /// <summary>Maximum complete packet size accepted by the service (0x5B4).</summary>
    public const int MaxPacketSize = 0x5B4;

    /// <summary>Maximum payload size accepted by the service (0x598 = MaxPacketSize - HeaderSize).</summary>
    public const int MaxPayloadSize = MaxPacketSize - HeaderSize;

    public const int MagicOffset = 0x00;
    public const int DataLengthOffset = 0x04;
    public const int PacketTypeOffset = 0x08;
    public const int ConnectIdOffset = 0x0C;
    public const int PacketIdOffset = 0x10;
    public const int CrcOffset = 0x1A;

    public required uint MagicValue { get; init; }
    public required int DataLength { get; init; }
    public required ItpPacketType PacketType { get; init; }
    public required int ConnectId { get; init; }
    public required ushort PacketId { get; init; }
    public required ushort Crc { get; init; }

    public bool HasValidMagic => MagicValue == Magic;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < HeaderSize)
        {
            throw new ArgumentException("Destination must be at least 28 bytes.", nameof(destination));
        }

        destination.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(MagicOffset, 4), MagicValue);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(DataLengthOffset, 4), DataLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(PacketTypeOffset, 4), (uint)PacketType);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(ConnectIdOffset, 4), ConnectId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(PacketIdOffset, 2), PacketId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(CrcOffset, 2), Crc);
    }

    public byte[] ToBytes()
    {
        var buffer = new byte[HeaderSize];
        WriteTo(buffer);
        return buffer;
    }

    public static bool TryParse(ReadOnlySpan<byte> source, out ItpPacketHeader header)
    {
        header = default;
        if (source.Length < HeaderSize)
        {
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(MagicOffset, 4));
        if (magic != Magic)
        {
            return false;
        }

        int dataLength = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(DataLengthOffset, 4));
        if (dataLength < 0 || dataLength > MaxPayloadSize)
        {
            return false;
        }

        header = new ItpPacketHeader
        {
            MagicValue = magic,
            DataLength = dataLength,
            PacketType = (ItpPacketType)BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(PacketTypeOffset, 4)),
            ConnectId = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(ConnectIdOffset, 4)),
            PacketId = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(PacketIdOffset, 2)),
            Crc = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(CrcOffset, 2))
        };
        return true;
    }
}

/// <summary>
/// CRC-16 exactly as implemented by PluginCommunicate.dll!g_GenCrc16Code:
/// reflected polynomial 0xA001, init 0xFFFF, no final xor, result byte-swapped before storage.
/// The service checks the CRC over the payload when DataLength &gt; 0, otherwise over the first
/// 26 header bytes (header without the CRC field).
/// </summary>
public static class ItpCrc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 1) != 0)
                {
                    crc = (ushort)((crc >> 1) ^ 0xA001);
                }
                else
                {
                    crc >>= 1;
                }
            }
        }

        return (ushort)(((crc >> 8) | (crc << 8)) & 0xFFFF);
    }
}

/// <summary>
/// Register request payload (ITP packet type 1). The service parses the "Path" member (jsoncpp)
/// and logs "Parse register sPath=%s;".
/// </summary>
public sealed record ItpRegisterRequest(
    [property: JsonPropertyName("Path")] string Path)
{
    public byte[] SerializePayload() => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));
}

/// <summary>
/// Register answer payload. The service replies with styled jsoncpp JSON: { "Response" : "&lt;code&gt;" }.
/// </summary>
public sealed record ItpRegisterResponse(
    [property: JsonPropertyName("Response")] string Response)
{
    public const string ResponseQueryFail = "3";

    public static ItpRegisterResponse Parse(ReadOnlySpan<byte> payload)
    {
        string text = Encoding.UTF8.GetString(payload);
        ItpRegisterResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ItpRegisterResponse>(text);
        }
        catch (JsonException)
        {
            parsed = null;
        }

        return parsed ?? new ItpRegisterResponse(text.Trim());
    }
}

/// <summary>
/// A complete ITP packet (header + payload).
/// </summary>
public readonly struct ItpPacket
{
    public required ItpPacketHeader Header { get; init; }
    public required byte[] Payload { get; init; }

    public int TotalLength => ItpPacketHeader.HeaderSize + Payload.Length;

    public static ItpPacket Create(ItpPacketType type, ReadOnlySpan<byte> payload, ushort packetId = 0, int connectId = 0)
    {
        if (payload.Length > ItpPacketHeader.MaxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), $"ITP payload exceeds {ItpPacketHeader.MaxPayloadSize} bytes.");
        }

        byte[] payloadCopy = payload.ToArray();

        // When the payload is empty the service checks the CRC over the first 26 header bytes
        // (the header without the CRC field); otherwise over the payload.
        Span<byte> headerSeed = stackalloc byte[ItpPacketHeader.HeaderSize];
        headerSeed.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(headerSeed.Slice(ItpPacketHeader.MagicOffset, 4), ItpPacketHeader.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(headerSeed.Slice(ItpPacketHeader.DataLengthOffset, 4), payloadCopy.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(headerSeed.Slice(ItpPacketHeader.PacketTypeOffset, 4), (uint)type);
        BinaryPrimitives.WriteInt32LittleEndian(headerSeed.Slice(ItpPacketHeader.ConnectIdOffset, 4), connectId);
        BinaryPrimitives.WriteUInt16LittleEndian(headerSeed.Slice(ItpPacketHeader.PacketIdOffset, 2), packetId);
        ushort crc = payloadCopy.Length > 0
            ? ItpCrc16.Compute(payloadCopy)
            : ItpCrc16.Compute(headerSeed[..26]);

        var header = new ItpPacketHeader
        {
            MagicValue = ItpPacketHeader.Magic,
            DataLength = payloadCopy.Length,
            PacketType = type,
            ConnectId = connectId,
            PacketId = packetId,
            Crc = crc
        };

        byte[] buffer = new byte[ItpPacketHeader.HeaderSize + payloadCopy.Length];
        header.WriteTo(buffer);
        payloadCopy.CopyTo(buffer.AsSpan(ItpPacketHeader.HeaderSize));
        return new ItpPacket { Header = header, Payload = payloadCopy };
    }

    public byte[] ToBytes()
    {
        byte[] buffer = new byte[TotalLength];
        Header.WriteTo(buffer);
        Payload.CopyTo(buffer.AsSpan(ItpPacketHeader.HeaderSize));
        return buffer;
    }
}

/// <summary>
/// Result of an ITP register handshake attempt.
/// </summary>
public sealed record ItpHandshakeResult(
    bool Connected,
    bool Answered,
    string? ResponseCode,
    string Message,
    string? Endpoint = null)
{
    /// <summary>
    /// True when the service answered with a response code that is not the observed
    /// "no data source" failure (code 3). Only codes verified on the wire are treated as success.
    /// </summary>
    public bool HasDataSource =>
        Answered &&
        !string.IsNullOrWhiteSpace(ResponseCode) &&
        !string.Equals(ResponseCode, ItpRegisterResponse.ResponseQueryFail, StringComparison.Ordinal);
}
