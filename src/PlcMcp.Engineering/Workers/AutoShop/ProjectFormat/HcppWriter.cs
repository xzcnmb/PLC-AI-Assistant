using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PlcMcp.Engineering.Workers.AutoShop.ProjectFormat;

/// <summary>Main record content: the source project name plus the (still opaque) program blob.</summary>
public sealed record HcppMainRecordSpec(string ProjectName, byte[] Blob);

/// <summary>One file entry to embed: name (e.g. "MAIN.LD") and raw payload bytes.</summary>
public sealed record HcppFileSpec(string Name, byte[] Content);

/// <summary>
/// Writer for AutoShop .hcpp containers. Produces the measured container shape:
/// PPCH header (36 bytes) + zlib stream + [uint32 len][data] record stream + 0xCDCDCDCD terminator.
/// <para>
/// IMPORTANT LIMITATION: this writer can only produce containers whose main program blob is
/// supplied by the caller (e.g. copied from a parsed sample). It CANNOT synthesize a valid
/// program blob — the blob encoding has not been reverse-engineered (see
/// docs/inovance-autoshop-project-format.md). Vendor compatibility is therefore UNVERIFIED;
/// the round-trip tests only prove self-consistency of this reader/writer pair.
/// </para>
/// </summary>
public static class HcppWriter
{
    /// <summary>
    /// Header variant observed on the small vendor templates. The writer always overwrites the
    /// measured size fields ([6..10) decompressed, [10..14) compressed) with the true values of
    /// the emitted container — verified exact on every real sample — so callers cannot emit
    /// inconsistent sizes. Field4 and the 6-byte marker have unresolved semantics and are
    /// copied from the chosen header unchanged.
    /// </summary>
    public static HcppHeader DefaultHeader { get; } = new()
    {
        Field4 = 0x0300,
        DecompressedSize = 0,
        CompressedSize = 0,
        Reserved = new byte[16],
        Marker = new byte[] { 0xCD, 0xCD, 0x0C, 0x00, 0x00, 0x00 },
    };

    /// <summary>Builds a complete .hcpp file.</summary>
    public static byte[] Write(
        HcppMainRecordSpec? main,
        IEnumerable<HcppFileSpec> files,
        HcppHeader? header = null)
    {
        var fileList = files?.ToList() ?? new List<HcppFileSpec>();
        foreach (var f in fileList)
        {
            if (string.IsNullOrWhiteSpace(f.Name) || f.Name.Length > 64)
                throw new ArgumentException($"file name must be 1..64 chars: '{f.Name}'");
            if (f.Content is null)
                throw new ArgumentException($"file content is null: '{f.Name}'");
        }
        if (main is not null && (string.IsNullOrWhiteSpace(main.ProjectName) || main.ProjectName.Length > 64))
            throw new ArgumentException($"project name must be 1..64 chars: '{main.ProjectName}'");

        var inner = BuildRecordStream(main, fileList);
        var payload = CompressZlib(inner);
        var result = new byte[HcppHeader.HeaderSize + payload.Length];
        // CompressedSize is the zlib stream WITHOUT its 4-byte checksum slot
        // (measured vendor definition: file length - 36 - 4, exact on both samples).
        (header ?? DefaultHeader)
            .WithSizes((uint)inner.Length, (uint)(payload.Length - 4))
            .ToBytes(HcppHeader.HeaderSize)
            .CopyTo(result, 0);
        payload.CopyTo(result, HcppHeader.HeaderSize);
        return result;
    }

    /// <summary>
    /// Builds the decompressed record stream: optional main record, then (name, content) record
    /// pairs, then the 0xCDCDCDCD terminator. The main record's trailing group-size hint follows
    /// the MEASURED vendor convention (exact on both real samples): the first file entry's name
    /// record (4 + name bytes) plus its content record (4 + 31 + payload bytes) plus 4. The
    /// meaning of the trailing 4 bytes is unresolved — possibly a trailer in the vendor's
    /// internal group framing (see docs/inovance-autoshop-project-format.md). An empty file
    /// list yields a hint of 0 (unverified).
    /// </summary>
    internal static byte[] BuildRecordStream(HcppMainRecordSpec? main, List<HcppFileSpec> files)
    {
        using var buffer = new MemoryStream();
        void WriteRecord(byte[] data)
        {
            buffer.Write(BitConverter.GetBytes((uint)data.Length), 0, 4);
            buffer.Write(data, 0, data.Length);
        }

        if (main is not null)
        {
            // Measured vendor convention (Template A1.hcpp: 11 + 1456 + 4 = 1471;
            // 连续式立包机.hcpp: 11 + 2779 + 4 = 2794) — computed BEFORE serialization.
            var groupSize = 0L;
            if (files.Count > 0)
            {
                var first = files[0];
                groupSize = 4 + Encoding.UTF8.GetByteCount(first.Name)
                          + 4 + HcppFraming.ContentHeaderSize + first.Content.Length
                          + 4;
            }

            // Names are UTF-8 in the vendor container (the real main record of
            // Template A1.hcpp carries "模板A.hcp" as UTF-8).
            var nameBytes = Encoding.UTF8.GetBytes(main.ProjectName);
            using var mainRec = new MemoryStream();
            mainRec.Write(BitConverter.GetBytes((uint)nameBytes.Length), 0, 4);
            mainRec.Write(nameBytes, 0, nameBytes.Length);
            mainRec.Write(BitConverter.GetBytes((uint)main.Blob.Length), 0, 4);
            mainRec.Write(main.Blob, 0, main.Blob.Length);
            mainRec.Write(BitConverter.GetBytes((uint)groupSize), 0, 4);
            WriteRecord(mainRec.ToArray());
        }

        // File records follow the main record (measured vendor layout: bare name record
        // paired with the next AutoShop-header content record).
        foreach (var f in files)
        {
            WriteRecord(Encoding.UTF8.GetBytes(f.Name));
            WriteRecord(BuildContentRecord(f.Content));
        }

        buffer.Write(BitConverter.GetBytes(HcppFraming.Terminator), 0, 4);
        return buffer.ToArray();
    }

    /// <summary>
    /// Content record: 31-byte AutoShop header + payload. Measured in real vendor files the
    /// bare content records carry NO trailing field (the 4-byte trailer exists only on
    /// CombinedFile records, which this writer does not emit).
    /// </summary>
    internal static byte[] BuildContentRecord(byte[] content)
    {
        var result = new byte[HcppFraming.ContentHeaderSize + content.Length];
        HcppFraming.ContentHeaderTemplate.CopyTo(result, 0);
        content.CopyTo(result, HcppFraming.ContentHeaderSize);
        return result;
    }

    /// <summary>
    /// Raw-deflate compress, then wrap with a 0x78 0x9C zlib header and an adler32 trailer.
    /// The adler32 is written BIG-endian per RFC 1950 (network byte order). Note: the vendor's
    /// own trailing 4 bytes do NOT verify as a standard adler32 in either byte order (measured
    /// on real templates — see docs/inovance-autoshop-project-format.md), so that field cannot
    /// be vendor-matched; we emit the standards-compliant form so ordinary zlib tools can
    /// verify our generated containers. The reader ignores the field either way.
    /// </summary>
    public static byte[] CompressZlib(byte[] data)
    {
        using var deflate = new MemoryStream();
        using (var compressor = new DeflateStream(deflate, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(data, 0, data.Length);
        }
        var deflateBytes = deflate.ToArray();
        var result = new byte[2 + deflateBytes.Length + 4];
        result[0] = 0x78;
        result[1] = 0x9C;
        deflateBytes.CopyTo(result, 2);
        BinaryPrimitives.WriteUInt32BigEndian(
            result.AsSpan(2 + deflateBytes.Length, 4), HcppFraming.Adler32(data));
        return result;
    }
}
