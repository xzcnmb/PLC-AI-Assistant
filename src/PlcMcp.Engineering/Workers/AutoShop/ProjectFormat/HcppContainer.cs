using System.Buffers.Binary;
using System.Text;

namespace PlcMcp.Engineering.Workers.AutoShop.ProjectFormat;

/// <summary>
/// PPCH container header (AutoShop .hcpp library/project package).
/// Measured layout (36 bytes, little-endian), reverse-engineered from real vendor files
/// (Template_Project\English\Library_Template\Template A1\Template A1.hcpp and
/// Template_Project\Simplified_Chinese\Library_Template\连续式立包机\连续式立包机.hcpp):
/// <code>
///  [0..4)   magic "PPCH"
///  [4..6)   uint16 - semantics UNRESOLVED (0x0300 on the V4.12 templates, 0x0200 on a V5.x project)
///  [6..10)  uint32 - decompressed record-stream size (measured exact on both samples)
///  [10..14) uint32 - compressed payload size = file length - 36 - 4 (measured exact on both samples)
///  [14..30) 16 reserved bytes (all-zero on both measured samples)
///  [30..36) 6-byte marker (0xCD 0xCD 0x0C 00 00 00 on small templates; 00 00 01 01 00 00 on a V5.x project)
///  [36..)   zlib stream (78 xx header + deflate data + 4-byte checksum slot)
/// </code>
/// The size fields were verified exact on every measured sample, but the reader still locates
/// the zlib stream by scanning for the 0x78 CMF byte (tolerant of future variants) and does not
/// enforce the size fields. See docs/inovance-autoshop-project-format.md.
/// </summary>
public sealed class HcppHeader
{
    public const int HeaderSize = 36;

    /// <summary>"PPCH" interpreted as a little-endian uint32 (bytes 50 50 43 48).</summary>
    public const uint MagicLittleEndian = 0x48435050;

    public ushort Field4 { get; init; }
    public uint DecompressedSize { get; init; }
    public uint CompressedSize { get; init; }
    public byte[] Reserved { get; init; } = new byte[16];
    public byte[] Marker { get; init; } = new byte[6];

    /// <summary>Byte offset where the zlib stream starts (observed 36; located by scan).</summary>
    public int PayloadOffset { get; init; }

    public bool HasMagic(ReadOnlySpan<byte> file) =>
        file.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(file) == MagicLittleEndian;

    public static HcppHeader Parse(ReadOnlySpan<byte> file, int payloadOffset)
    {
        if (file.Length < HeaderSize)
            throw new HcppFormatException($"PPCH header truncated: {file.Length} bytes.");
        return new HcppHeader
        {
            Field4 = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(4, 2)),
            DecompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(6, 4)),
            CompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(10, 4)),
            Reserved = file.Slice(14, 16).ToArray(),
            Marker = file.Slice(30, 6).ToArray(),
            PayloadOffset = payloadOffset,
        };
    }

    /// <summary>Returns a copy of this header with the measured size fields set.</summary>
    public HcppHeader WithSizes(uint decompressed, uint compressed) => new()
    {
        Field4 = Field4,
        DecompressedSize = decompressed,
        CompressedSize = compressed,
        Reserved = Reserved,
        Marker = Marker,
        PayloadOffset = PayloadOffset,
    };

    public byte[] ToBytes(int payloadOffset)
    {
        var bytes = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), MagicLittleEndian);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), Field4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(6, 4), DecompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10, 4), CompressedSize);
        Reserved.CopyTo(bytes.AsSpan(14, 16));
        Marker.CopyTo(bytes.AsSpan(30, 6));
        return bytes;
    }
}

/// <summary>Record shapes observed in the decompressed PPCH record stream.</summary>
public enum HcppRecordKind
{
    /// <summary>First record: [namelen][project name][bloblen][main program blob][group-size hint].</summary>
    Main,

    /// <summary>Bare file-name record, e.g. "MAIN.LD" (no nested framing).</summary>
    FileName,

    /// <summary>Content record starting with the 31-byte "AutoShop" header, then the file payload.</summary>
    FileContent,

    /// <summary>Combined record: [namelen][name][contentlen][content][4-byte trailer].</summary>
    CombinedFile,

    /// <summary>Anything else (opaque payload).</summary>
    Unknown,
}

/// <summary>
/// One [uint32 LE length][data] record of the decompressed PPCH stream. The stream is
/// terminated by the 0xCDCDCDCD filler marker (observed) or by end-of-stream.
/// </summary>
public sealed class HcppRecord
{
    public required int Offset { get; init; }
    public required byte[] Data { get; init; }
    public required HcppRecordKind Kind { get; init; }

    // ---- Main record fields ----
    public string? MainProjectName { get; init; }
    public byte[]? MainBlob { get; init; }
    public uint MainGroupSizeHint { get; init; }

    // ---- File fields (FileName / FileContent / CombinedFile) ----
    public string? FileName { get; init; }
    public byte[]? FileContent { get; init; }

    /// <summary>31-byte "AutoShop" + version header preceding a file payload (FileContent records).</summary>
    public byte[]? ContentHeader { get; init; }

    /// <summary>4-byte trailing field observed on Main and CombinedFile records (0 on most file records).</summary>
    public uint Trailer { get; init; }
}

/// <summary>A parsed .hcpp container: header, raw decompressed record stream, and records.</summary>
public sealed class HcppContainer
{
    public required HcppHeader Header { get; init; }
    public required byte[] InnerStream { get; init; }
    public required IReadOnlyList<HcppRecord> Records { get; init; }

    /// <summary>Assembled (name, content) file list; content is the raw payload after the AutoShop header.</summary>
    public IReadOnlyList<(string Name, byte[] Content)> Files { get; init; } = Array.Empty<(string, byte[])>();

    public HcppRecord? MainRecord => Records.Count > 0 && Records[0].Kind == HcppRecordKind.Main
        ? Records[0]
        : null;
}

/// <summary>Thrown when a .hcpp file is structurally invalid (as far as the format is understood).</summary>
public sealed class HcppFormatException : Exception
{
    public HcppFormatException(string message) : base(message) { }
    public HcppFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Shared helpers: zlib wrapping, adler32, record framing constants.</summary>
public static class HcppFraming
{
    /// <summary>Stream terminator marker observed at the end of the record stream.</summary>
    public const uint Terminator = 0xCDCDCDCD;

    /// <summary>"AutoShop" + 12 zero bytes + "2.6\0\0" + "6.0\0\0\0" — content-record prefix.</summary>
    public const int ContentHeaderSize = 31;

    public static readonly byte[] ContentHeaderTemplate;

    static HcppFraming()
    {
        ContentHeaderTemplate = new byte[ContentHeaderSize];
        Encoding.ASCII.GetBytes("AutoShop").CopyTo(ContentHeaderTemplate, 0);
        // bytes 8..19 observed zero (reserved)
        Encoding.ASCII.GetBytes("2.6").CopyTo(ContentHeaderTemplate, 20);
        Encoding.ASCII.GetBytes("6.0").CopyTo(ContentHeaderTemplate, 25);
        // bytes 28..30 observed zero
    }

    /// <summary>Standard adler32 (RFC 1950), needed because .NET has no zlib-with-header API.</summary>
    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint mod = 65521;
        uint a = 1, b = 0;
        foreach (var t in data)
        {
            a = (a + t) % mod;
            b = (b + a) % mod;
        }
        return (b << 16) | a;
    }
}
