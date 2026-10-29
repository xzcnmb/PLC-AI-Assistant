using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PlcMcp.Engineering.Workers.AutoShop.ProjectFormat;

/// <summary>
/// Reader for AutoShop .hcpp containers (PPCH magic + zlib + length-prefixed record stream).
/// <para>
/// Evidence: real vendor files under <c>Template_Project\*\Library_Template\*.hcpp</c>
/// (AutoShop V4.12.0.2). The container framing, the record stream, the 31-byte "AutoShop"
/// content header and the 0xCDCDCDCD terminator are measured facts. The main program blob
/// (record #0) is exposed as opaque bytes — its encoding has NOT been recovered (see
/// docs/inovance-autoshop-project-format.md). The reader never guesses blob semantics.
/// </para>
/// </summary>
public static class HcppReader
{
    /// <summary>Parses a .hcpp file. Returns false (with a reason) instead of throwing on bad input.</summary>
    public static bool TryRead(byte[] file, out HcppContainer? container, out string? error)
    {
        container = null;
        error = null;
        try
        {
            container = Read(file);
            return true;
        }
        catch (HcppFormatException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        {
            error = $"unexpected container error: {ex.Message}";
            return false;
        }
    }

    public static HcppContainer Read(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length < HcppHeader.HeaderSize)
            throw new HcppFormatException($"file too small for PPCH header: {file.Length} bytes.");

        var headerSpan = file.AsSpan();
        if (!headerSpan[..4].SequenceEqual("PPCH"u8))
            throw new HcppFormatException("not a PPCH container (magic mismatch).");

        var payloadOffset = LocateZlibStream(file);
        if (payloadOffset < 0)
            throw new HcppFormatException("no zlib stream found in header region.");

        var header = HcppHeader.Parse(headerSpan, payloadOffset);
        var inner = DecompressZlib(file, payloadOffset);
        var records = ParseRecordStream(inner);
        var files = AssembleFiles(records);
        return new HcppContainer { Header = header, InnerStream = inner, Records = records, Files = files };
    }

    /// <summary>
    /// Locates the zlib stream by scanning for a valid zlib header (CMF=0x78, FCHECK valid)
    /// within the header region. Tolerant of the two observed header variants.
    /// </summary>
    public static int LocateZlibStream(byte[] file)
    {
        var max = Math.Min(file.Length - 2, 96);
        for (var i = 30; i <= max; i++)
        {
            if (file[i] != 0x78) continue;
            var flg = file[i + 1];
            if ((0x78 * 256 + flg) % 31 != 0) continue; // RFC 1950 header check
            if ((flg & 0x20) != 0) continue; // preset dictionary not used by vendor writer
            return i;
        }
        return -1;
    }

    /// <summary>Inflates the zlib stream: skip the 2-byte zlib header, deflate, ignore adler32.</summary>
    public static byte[] DecompressZlib(byte[] file, int payloadOffset)
    {
        // DeflateStream stops at the end of the deflate data; the trailing adler32 is simply unused.
        using var input = new MemoryStream(file, payloadOffset + 2, file.Length - payloadOffset - 2, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// Parses [uint32 LE length][data] records until the 0xCDCDCDCD terminator or end of stream.
    /// </summary>
    public static List<HcppRecord> ParseRecordStream(byte[] inner)
    {
        var records = new List<HcppRecord>();
        var offset = 0;
        while (offset + 4 <= inner.Length)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(inner.AsSpan(offset, 4));
            if (length == HcppFraming.Terminator)
                break; // observed stream terminator
            if (length > int.MaxValue || offset + 4 + length > inner.Length)
                throw new HcppFormatException(
                    $"record at offset {offset} overruns stream (length {length}, remaining {inner.Length - offset - 4}).");
            var data = inner[(offset + 4)..(int)(offset + 4 + length)];
            records.Add(Classify(offset, data, records.Count));
            offset += 4 + (int)length;
        }
        return records;
    }

    private static HcppRecord Classify(int offset, byte[] data, int index)
    {
        if (index == 0 && TryParseMain(data, out var mainName, out var mainBlob, out var groupHint))
        {
            return new HcppRecord
            {
                Offset = offset, Data = data, Kind = HcppRecordKind.Main,
                MainProjectName = mainName, MainBlob = mainBlob, MainGroupSizeHint = groupHint,
            };
        }

        if (StartsWithAscii(data, "AutoShop"))
        {
            var header = data[..Math.Min(HcppFraming.ContentHeaderSize, data.Length)];
            var content = data.Length > HcppFraming.ContentHeaderSize
                ? data[HcppFraming.ContentHeaderSize..]
                : Array.Empty<byte>();
            return new HcppRecord
            {
                Offset = offset, Data = data, Kind = HcppRecordKind.FileContent,
                ContentHeader = header, FileContent = content,
            };
        }

        if (TryParseCombined(data, out var name, out var content2, out var trailer))
        {
            return new HcppRecord
            {
                Offset = offset, Data = data, Kind = HcppRecordKind.CombinedFile,
                FileName = name, FileContent = content2, Trailer = trailer,
            };
        }

        if (IsPlausibleName(data))
        {
            return new HcppRecord
            {
                Offset = offset, Data = data, Kind = HcppRecordKind.FileName,
                FileName = Encoding.UTF8.GetString(data),
            };
        }

        return new HcppRecord { Offset = offset, Data = data, Kind = HcppRecordKind.Unknown };
    }

    /// <summary>Main record: [uint32 namelen][name][uint32 bloblen][blob][uint32 group-size hint].</summary>
    internal static bool TryParseMain(byte[] data, out string name, out byte[] blob, out uint groupHint)
    {
        name = string.Empty; blob = Array.Empty<byte>(); groupHint = 0;
        if (data.Length < 12) return false;
        var nameLen = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (nameLen is 0 or > 64) return false;
        if (data.Length < 4 + nameLen + 4) return false;
        var nameBytes = data[4..(int)(4 + nameLen)];
        if (!IsPlausibleName(nameBytes)) return false;
        var blobLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)(4 + nameLen), 4));
        if (blobLen > int.MaxValue) return false;
        var expected = 4 + nameLen + 4 + blobLen + 4;
        if (expected != data.Length) return false;
        name = Encoding.UTF8.GetString(nameBytes);
        blob = data[(int)(8 + nameLen)..(int)(8 + nameLen + blobLen)];
        groupHint = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)(8 + nameLen + blobLen), 4));
        return true;
    }

    /// <summary>Combined record: [uint32 namelen][name][uint32 contentlen][content][uint32 trailer].</summary>
    internal static bool TryParseCombined(byte[] data, out string name, out byte[] content, out uint trailer)
    {
        name = string.Empty; content = Array.Empty<byte>(); trailer = 0;
        if (data.Length < 13) return false;
        var nameLen = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (nameLen is 0 or > 64) return false;
        if (data.Length < 4 + nameLen + 4) return false;
        var nameBytes = data[4..(int)(4 + nameLen)];
        if (!IsPlausibleName(nameBytes)) return false;
        var contentLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)(4 + nameLen), 4));
        if (contentLen > int.MaxValue) return false;
        // Trailer is optional in the wild: accept both exact (with trailer) and 4-short (without).
        if (4 + nameLen + 4 + contentLen + 4 == data.Length)
        {
            name = Encoding.UTF8.GetString(nameBytes);
            content = data[(int)(8 + nameLen)..(int)(8 + nameLen + contentLen)];
            trailer = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)(8 + nameLen + contentLen), 4));
            return true;
        }
        if (4 + nameLen + 4 + contentLen == data.Length)
        {
            name = Encoding.UTF8.GetString(nameBytes);
            content = data[(int)(8 + nameLen)..];
            return true;
        }
        return false;
    }

    /// <summary>
    /// Pairs bare FileName records with the following content record; CombinedFile records stand alone.
    /// Measured pattern (real templates): every bare name record is IMMEDIATELY followed by its
    /// content record. That payload normally starts with the 31-byte "AutoShop" header
    /// (FileContent), but payloads lacking that header — the encrypted LoginPassowrd.dat blob,
    /// plain-text config.ini / HMI.csv, trace data — classify as Unknown and still pair.
    /// </summary>
    public static List<(string Name, byte[] Content)> AssembleFiles(List<HcppRecord> records)
    {
        var files = new List<(string, byte[])>();
        for (var i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            switch (rec.Kind)
            {
                case HcppRecordKind.CombinedFile when rec.FileName is not null && rec.FileContent is not null:
                    files.Add((rec.FileName, rec.FileContent));
                    break;
                case HcppRecordKind.FileName when rec.FileName is not null && i + 1 < records.Count:
                    var next = records[i + 1];
                    if (next.Kind is HcppRecordKind.FileContent or HcppRecordKind.Unknown)
                    {
                        files.Add((rec.FileName, next.FileContent ?? next.Data));
                        i++; // consume the paired content record
                    }
                    break;
            }
        }
        return files;
    }

    internal static bool StartsWithAscii(byte[] data, string prefix)
    {
        if (data.Length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i]) return false;
        }
        return true;
    }

    internal static bool IsPrintableAscii(byte[] data)
    {
        foreach (var b in data)
        {
            if (b is < 0x20 or > 0x7E) return false;
        }
        return true;
    }

    /// <summary>
    /// Plausible embedded file/project name: valid UTF-8, 1..64 chars, no control characters.
    /// Vendor names include non-ASCII UTF-8 (e.g. the main record's "模板A.hcp").
    /// </summary>
    internal static bool IsPlausibleName(byte[] data)
    {
        if (data.Length is 0 or > 128) return false;
        try
        {
            var text = Encoding.UTF8.GetString(data);
            if (text.Length is 0 or > 64) return false;
            foreach (var c in text)
            {
                if (char.IsControl(c)) return false;
            }
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
