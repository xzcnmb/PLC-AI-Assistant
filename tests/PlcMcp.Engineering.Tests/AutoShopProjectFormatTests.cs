using System.Buffers.Binary;
using System.Text;
using PlcMcp.Engineering.Workers.AutoShop.ProjectFormat;

namespace PlcMcp.Engineering.Tests;

/// <summary>
/// Offline unit tests for the AutoShop .hcpp (PPCH) container reader/writer.
/// <para>
/// Every test builds its bytes in memory — no vendor files are referenced, so nothing here
/// depends on a live AutoShop install. The structures under test mirror the measured layout
/// of real vendor samples (Template_Project\*\Library_Template\*.hcpp, AutoShop V4.12.0.2):
/// 36-byte PPCH header, zlib stream, [uint32 len][data] record stream, 0xCDCDCDCD terminator.
/// </para>
/// <para>
/// The round-trip tests prove self-consistency of this reader/writer pair only. They do NOT
/// prove AutoShop can open generated containers: the main program blob's encoding has not
/// been reverse-engineered (see docs/inovance-autoshop-project-format.md).
/// </para>
/// </summary>
public class AutoShopProjectFormatTests
{
    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static byte[] Zlib(byte[] data) => HcppWriter.CompressZlib(data);

    private static byte[] Record(byte[] data)
    {
        var result = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), (uint)data.Length);
        data.CopyTo(result, 4);
        return result;
    }

    private static byte[] MainRecordData(string projectName, byte[] blob, uint groupHint)
    {
        var name = Encoding.UTF8.GetBytes(projectName);
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes((uint)name.Length));
        ms.Write(name);
        ms.Write(BitConverter.GetBytes((uint)blob.Length));
        ms.Write(blob);
        ms.Write(BitConverter.GetBytes(groupHint));
        return ms.ToArray();
    }

    private static byte[] CombinedRecordData(string name, byte[] content, uint trailer = 0)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes((uint)nameBytes.Length));
        ms.Write(nameBytes);
        ms.Write(BitConverter.GetBytes((uint)content.Length));
        ms.Write(content);
        ms.Write(BitConverter.GetBytes(trailer));
        return ms.ToArray();
    }

    private static byte[] Container(byte[] inner)
    {
        var result = new byte[HcppHeader.HeaderSize + inner.Length];
        HcppWriter.DefaultHeader.ToBytes(HcppHeader.HeaderSize).CopyTo(result, 0);
        inner.CopyTo(result, HcppHeader.HeaderSize);
        return result;
    }

    // ---------------------------------------------------------------
    // Adler32 (RFC 1950 test vectors)
    // ---------------------------------------------------------------

    [Fact]
    public void Adler32_KnownVectors()
    {
        Assert.Equal(1u, HcppFraming.Adler32(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0x024D0127u, HcppFraming.Adler32(Encoding.ASCII.GetBytes("abc")));
        // Cross-checked against zlib.adler32(b"Wikipedia") == 0x11E60398.
        Assert.Equal(0x11E60398u, HcppFraming.Adler32(Encoding.ASCII.GetBytes("Wikipedia")));
    }

    // ---------------------------------------------------------------
    // Header
    // ---------------------------------------------------------------

    [Fact]
    public void Header_RoundTripsThroughBytes()
    {
        var header = new HcppHeader
        {
            Field4 = 0x0300,
            DecompressedSize = 24867,
            CompressedSize = 15481,
            Reserved = new byte[16],
            Marker = new byte[] { 0xCD, 0xCD, 0x0C, 0x00, 0x00, 0x00 },
            PayloadOffset = HcppHeader.HeaderSize,
        };
        var bytes = header.ToBytes(HcppHeader.HeaderSize);
        Assert.Equal(36, bytes.Length);
        Assert.Equal("PPCH"u8, bytes.AsSpan(0, 4));

        var file = new byte[36];
        bytes.CopyTo(file, 0);
        var parsed = HcppHeader.Parse(file, 36);
        Assert.Equal(0x0300, parsed.Field4);
        Assert.Equal(24867u, parsed.DecompressedSize);
        Assert.Equal(15481u, parsed.CompressedSize);
        Assert.Equal(new byte[] { 0xCD, 0xCD, 0x0C, 0x00, 0x00, 0x00 }, parsed.Marker);
    }

    [Fact]
    public void TryRead_RejectsNonPpchMagic()
    {
        var junk = Encoding.ASCII.GetBytes("this is not a PPCH container at all........");
        var ok = HcppReader.TryRead(junk, out var container, out var error);
        Assert.False(ok);
        Assert.Null(container);
        Assert.Contains("magic", error);
    }

    [Fact]
    public void TryRead_RejectsTruncatedFile()
    {
        var ok = HcppReader.TryRead(new byte[10], out var container, out var error);
        Assert.False(ok);
        Assert.Null(container);
        Assert.Contains("too small", error);
    }

    // ---------------------------------------------------------------
    // zlib stream location (two vendor header variants + all FLEVEL values)
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x5E)]
    [InlineData(0x9C)]
    [InlineData(0xDA)]
    public void LocateZlibStream_AcceptsAllValidZlibHeaders(byte flg)
    {
        // Build a minimal valid zlib header at the observed payload offset.
        var header = new byte[HcppHeader.HeaderSize + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), HcppHeader.MagicLittleEndian);
        header[36] = 0x78;
        header[37] = flg;
        Assert.Equal(36, HcppReader.LocateZlibStream(header));
    }

    [Fact]
    public void LocateZlibStream_RejectsInvalidFcheck()
    {
        var header = new byte[HcppHeader.HeaderSize + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), HcppHeader.MagicLittleEndian);
        header[36] = 0x78;
        header[37] = 0x00; // (0x78*256 + 0x00) % 31 != 0
        Assert.Equal(-1, HcppReader.LocateZlibStream(header));
    }

    // ---------------------------------------------------------------
    // Record stream
    // ---------------------------------------------------------------

    [Fact]
    public void ParseRecordStream_TerminatesOnCDCDCDCD()
    {
        var inner = Concat(
            Record(Encoding.ASCII.GetBytes("MAIN.LD")),
            Record(Encoding.ASCII.GetBytes("x")),
            BitConverter.GetBytes(HcppFraming.Terminator),
            Record(Encoding.ASCII.GetBytes("AFTER-TERMINATOR"))); // must be ignored
        var records = HcppReader.ParseRecordStream(inner);
        Assert.Equal(2, records.Count);
        Assert.Equal("MAIN.LD", Encoding.ASCII.GetString(records[0].Data));
    }

    [Fact]
    public void ParseRecordStream_ThrowsOnOverrun()
    {
        var inner = Concat(BitConverter.GetBytes(0xFFFFFFu), new byte[] { 1, 2, 3 });
        var ex = Assert.Throws<HcppFormatException>(() => HcppReader.ParseRecordStream(inner));
        Assert.Contains("overruns", ex.Message);
    }

    [Fact]
    public void MainRecord_ParsesUtf8NameBlobAndGroupHint()
    {
        // Mirrors the vendor shape: main record carries the source project file name
        // (UTF-8 on Chinese systems) + opaque program blob + following-group size hint.
        var blob = Enumerable.Range(0, 500).Select(i => (byte)(i * 7 % 256)).ToArray();
        var inner = Concat(
            Record(MainRecordData("模板A.hcp", blob, groupHint: 1467)),
            BitConverter.GetBytes(HcppFraming.Terminator));
        var records = HcppReader.ParseRecordStream(inner);
        var main = Assert.Single(records);
        Assert.Equal(HcppRecordKind.Main, main.Kind);
        Assert.Equal("模板A.hcp", main.MainProjectName);
        Assert.Equal(blob, main.MainBlob);
        Assert.Equal(1467u, main.MainGroupSizeHint);
    }

    [Fact]
    public void CombinedFileRecord_ParsesNameContentAndTrailer()
    {
        // Measured shape (Template A1.hcpp record for SBR_001.LD): the combined record
        // embeds the 31-byte AutoShop header INSIDE its content and ends with a 4-byte
        // trailer. Main always occupies index 0, so the combined record follows it here.
        var content = Concat(HcppFraming.ContentHeaderTemplate, Encoding.ASCII.GetBytes("payload-bytes"));
        var mainBlob = new byte[] { 0x01, 0x02 };
        var inner = Concat(
            Record(MainRecordData("模板A.hcp", mainBlob, groupHint: 999)),
            Record(CombinedRecordData("SBR_001.LD", content, trailer: 0x1234)),
            BitConverter.GetBytes(HcppFraming.Terminator));
        var records = HcppReader.ParseRecordStream(inner);
        Assert.Equal(2, records.Count);
        Assert.Equal(HcppRecordKind.Main, records[0].Kind);
        var record = records[1];
        Assert.Equal(HcppRecordKind.CombinedFile, record.Kind);
        Assert.Equal("SBR_001.LD", record.FileName);
        Assert.Equal(content, record.FileContent);
        Assert.Equal(0x1234u, record.Trailer);
    }

    [Fact]
    public void ContentRecord_IsDetectedByAutoShopHeader()
    {
        // Bare content records carry no trailing field (measured in Template A1.hcpp).
        var payload = Encoding.ASCII.GetBytes("VAR-LIST-CONTENT");
        var contentRecord = Concat(HcppFraming.ContentHeaderTemplate, payload);
        var inner = Concat(
            Record(contentRecord),
            BitConverter.GetBytes(HcppFraming.Terminator));
        var record = Assert.Single(HcppReader.ParseRecordStream(inner));
        Assert.Equal(HcppRecordKind.FileContent, record.Kind);
        Assert.Equal("AutoShop", Encoding.ASCII.GetString(record.ContentHeader![..8]));
        Assert.Equal(payload, record.FileContent);
    }

    [Fact]
    public void BareNameAndContent_AssembleIntoFileList()
    {
        var payload = Encoding.ASCII.GetBytes("MAIN-PROGRAM");
        var inner = Concat(
            Record(MainRecordData("proj.hcp", new byte[] { 1, 2, 3 }, groupHint: (uint)(4 + 7 + 4 + 31 + payload.Length))),
            Record(Encoding.ASCII.GetBytes("MAIN.LD")),
            Record(Concat(HcppFraming.ContentHeaderTemplate, payload)),
            BitConverter.GetBytes(HcppFraming.Terminator));
        var records = HcppReader.ParseRecordStream(inner);
        var files = HcppReader.AssembleFiles(records);
        var file = Assert.Single(files);
        Assert.Equal("MAIN.LD", file.Name);
        Assert.Equal(payload, file.Content);
    }

    // ---------------------------------------------------------------
    // End-to-end container round trip (synthetic, self-consistent only)
    // ---------------------------------------------------------------

    [Fact]
    public void RoundTrip_MainAndFiles_PreservesEverything()
    {
        var blob = Enumerable.Range(0, 300).Select(i => (byte)(i * 13 % 256)).ToArray();
        var main = new HcppMainRecordSpec("模板A.hcp", blob);
        var files = new[]
        {
            new HcppFileSpec("MAIN.LD", Encoding.ASCII.GetBytes("ld-payload-1")),
            new HcppFileSpec("VarList.gdt", Encoding.ASCII.GetBytes("var-payload-2")),
            new HcppFileSpec("PortConfig.cfg", new byte[] { 0x00, 0xFF, 0x10 }),
        };

        var bytes = HcppWriter.Write(main, files);
        Assert.True(HcppReader.TryRead(bytes, out var container, out var error), error);

        Assert.NotNull(container);
        Assert.Equal(36, container!.Header.PayloadOffset);

        var mainRec = container.MainRecord;
        Assert.NotNull(mainRec);
        Assert.Equal("模板A.hcp", mainRec!.MainProjectName);
        Assert.Equal(blob, mainRec.MainBlob);

        Assert.Equal(3, container.Files.Count);
        Assert.Equal("MAIN.LD", container.Files[0].Name);
        Assert.Equal(Convert.ToHexString(Encoding.ASCII.GetBytes("ld-payload-1")), Convert.ToHexString(container.Files[0].Content));
        Assert.Equal("VarList.gdt", container.Files[1].Name);
        Assert.Equal(Convert.ToHexString(Encoding.ASCII.GetBytes("var-payload-2")), Convert.ToHexString(container.Files[1].Content));
        Assert.Equal("PortConfig.cfg", container.Files[2].Name);
        Assert.Equal(Convert.ToHexString(new byte[] { 0x00, 0xFF, 0x10 }), Convert.ToHexString(container.Files[2].Content));
    }

    [Fact]
    public void RoundTrip_MainGroupSizeHint_MatchesMeasuredVendorPattern()
    {
        var files = new[]
        {
            new HcppFileSpec("MAIN.LD", new byte[] { 1, 2, 3, 4 }),
            new HcppFileSpec("MAIN.mon", new byte[] { 5, 6 }),
        };
        var bytes = HcppWriter.Write(new HcppMainRecordSpec("p.hcp", new byte[] { 9 }), files);
        HcppReader.TryRead(bytes, out var container, out _);
        var main = container!.MainRecord!;

        // Measured vendor convention (exact on Template A1.hcpp and 连续式立包机.hcpp):
        // hint = first file entry's name record (4 + name) + its content record
        // (4 + 31 + payload) + 4. Bare content records carry no trailing field.
        var first = files[0];
        var expected = 4 + Encoding.ASCII.GetByteCount(first.Name)
                     + 4 + HcppFraming.ContentHeaderSize + first.Content.Length
                     + 4;
        Assert.Equal((uint)expected, main.MainGroupSizeHint);
    }

    [Fact]
    public void RoundTrip_HeaderCarriesMeasuredSizeFields()
    {
        // The writer fills the measured size fields ([6..10) decompressed, [10..14)
        // compressed = file length - 36 - 4), verified exact on every real sample.
        var files = new[] { new HcppFileSpec("A.LD", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }) };
        var bytes = HcppWriter.Write(null, files);
        Assert.True(HcppReader.TryRead(bytes, out var container, out var error), error);
        Assert.Equal((uint)container!.InnerStream.Length, container.Header.DecompressedSize);
        Assert.Equal((uint)(bytes.Length - HcppHeader.HeaderSize - 4), container.Header.CompressedSize);
    }

    [Fact]
    public void RoundTrip_WithoutMainRecord_ParsesFilesOnly()
    {
        var bytes = HcppWriter.Write(null, new[] { new HcppFileSpec("MDI.cfg", new byte[] { 7, 7, 7 }) });
        Assert.True(HcppReader.TryRead(bytes, out var container, out var error), error);
        Assert.Null(container!.MainRecord);
        var file = Assert.Single(container.Files);
        Assert.Equal("MDI.cfg", file.Name);
        Assert.Equal(new byte[] { 7, 7, 7 }, file.Content);
    }

    [Fact]
    public void RoundTrip_ZlibStreamIsReadableByStandardTools()
    {
        var bytes = HcppWriter.Write(null, new[] { new HcppFileSpec("A.LD", Encoding.ASCII.GetBytes("hello")) });
        // 0x78 0x9C header + deflate + adler32 trailer (big-endian per RFC 1950 network byte order)
        Assert.Equal(0x78, bytes[36]);
        Assert.Equal(0x9C, bytes[37]);
        var adler = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(bytes.Length - 4, 4));
        var inner = HcppReader.DecompressZlib(bytes, 36);
        Assert.Equal(adler, HcppFraming.Adler32(inner));
    }

    // ---------------------------------------------------------------
    // Writer validation
    // ---------------------------------------------------------------

    [Fact]
    public void Writer_RejectsOversizedOrEmptyNames()
    {
        Assert.Throws<ArgumentException>(() =>
            HcppWriter.Write(null, new[] { new HcppFileSpec(new string('x', 65), new byte[] { 1 }) }));
        Assert.Throws<ArgumentException>(() =>
            HcppWriter.Write(null, new[] { new HcppFileSpec("", new byte[] { 1 }) }));
        Assert.Throws<ArgumentException>(() =>
            HcppWriter.Write(new HcppMainRecordSpec("", new byte[] { 1 }), Array.Empty<HcppFileSpec>()));
    }

    [Fact]
    public void Writer_RejectsNullContent()
    {
        Assert.Throws<ArgumentException>(() =>
            HcppWriter.Write(null, new[] { new HcppFileSpec("A.LD", null!) }));
    }

    // ---------------------------------------------------------------
    // Vendor-shaped synthetic container (layout lock-in)
    // ---------------------------------------------------------------

    [Fact]
    public void VendorShapedContainer_ParsesEndToEnd()
    {
        // Hand-built inner stream reproducing the measured 模板B.hcpp record pattern:
        // main record + bare name record + content record (no trailing field) + terminator.
        var payload = Encoding.ASCII.GetBytes("LD-PROGRAM-BYTES");
        var contentRecord = Concat(HcppFraming.ContentHeaderTemplate, payload);
        var nameRecord = Encoding.ASCII.GetBytes("MAIN.LD");
        var groupHint = (uint)(4 + nameRecord.Length + 4 + contentRecord.Length);

        var inner = Concat(
            Record(MainRecordData("模板A.hcp", Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(), groupHint)),
            Record(nameRecord),
            Record(contentRecord),
            BitConverter.GetBytes(HcppFraming.Terminator));

        var file = Concat(HcppWriter.DefaultHeader.ToBytes(36), Zlib(inner));
        Assert.True(HcppReader.TryRead(file, out var container, out var error), error);

        Assert.Equal("PPCH"u8, file.AsSpan(0, 4));
        Assert.NotNull(container!.MainRecord);
        Assert.Equal("模板A.hcp", container.MainRecord!.MainProjectName);
        Assert.Equal(groupHint, container.MainRecord.MainGroupSizeHint);
        var file0 = Assert.Single(container.Files);
        Assert.Equal("MAIN.LD", file0.Name);
        Assert.Equal(payload, file0.Content);
    }

    // ---------------------------------------------------------------
    // Real-sample-shaped cases (measured on Template A1.hcpp and the
    // 304 KB 连续式立包机.hcpp project; no runtime dependency on the install)
    // ---------------------------------------------------------------

    [Fact]
    public void CombinedFile_EmptyContentWithNonzeroTrailer_Parses()
    {
        // Measured MDI.cfg record (Template A1.hcpp): contentLen == 0, trailer == 0x2E.
        var mainBlob = new byte[] { 0x01, 0x02 };
        var inner = Concat(
            Record(MainRecordData("模板A.hcp", mainBlob, groupHint: 1)),
            Record(CombinedRecordData("MDI.cfg", Array.Empty<byte>(), trailer: 0x2E)),
            BitConverter.GetBytes(HcppFraming.Terminator));
        var records = HcppReader.ParseRecordStream(inner);
        Assert.Equal(2, records.Count);
        Assert.Equal(HcppRecordKind.CombinedFile, records[1].Kind);
        Assert.Equal("MDI.cfg", records[1].FileName);
        Assert.Empty(records[1].FileContent!);
        Assert.Equal(0x2Eu, records[1].Trailer);
    }

    [Fact]
    public void EncryptedContentRecord_PairsWithPrecedingBareName()
    {
        // Measured final records (Template A1.hcpp): the bare name record
        // "LoginPassowrd.dat" is followed by a 17-byte encrypted payload record with no
        // AutoShop header (classified Unknown — it contains control characters). The file
        // list must still contain it.
        var encrypted = new byte[] { 0xB0, 0xDB, 0xB5, 0xD4, 0xA7, 0xA4, 0x87, 0xA6, 0xD7, 0xD5, 0xCD, 0xDC, 0x7A, 0xD7, 0x9B, 0x31, 0x02 };
        var inner = Concat(
            Record(MainRecordData("模板A.hcp", new byte[] { 1 }, groupHint: 1)),
            Record(Encoding.ASCII.GetBytes("LoginPassowrd.dat")),
            Record(encrypted),
            BitConverter.GetBytes(HcppFraming.Terminator));
        var records = HcppReader.ParseRecordStream(inner);
        Assert.Equal(3, records.Count);
        Assert.Equal(HcppRecordKind.FileName, records[1].Kind);
        Assert.Equal(HcppRecordKind.Unknown, records[2].Kind);

        var files = HcppReader.AssembleFiles(records);
        var file = Assert.Single(files);
        Assert.Equal("LoginPassowrd.dat", file.Name);
        Assert.Equal(encrypted, file.Content);
    }

    [Fact]
    public void ParseRecordStream_WithoutTerminator_ParsesToEndOfStream()
    {
        // Measured on the 304 KB 连续式立包机.hcpp real project: the record stream ends
        // exactly at the end of the inflated data — no 0xCDCDCDCD terminator. The headerless
        // payload mirrors the real config.ini record (contains \r\n control characters, so it
        // classifies as Unknown rather than as a plausible bare name).
        var payload = Encoding.ASCII.GetBytes("[Comm_Param]\r\nType=1");
        var inner = Concat(
            Record(MainRecordData("p.hcp", new byte[] { 1 }, groupHint: 1)),
            Record(Encoding.ASCII.GetBytes("folder.txt")),
            Record(payload));
        var records = HcppReader.ParseRecordStream(inner);
        Assert.Equal(3, records.Count);
        Assert.Equal("folder.txt", records[1].FileName);
        Assert.Equal(HcppRecordKind.Unknown, records[2].Kind);
        Assert.Equal(payload, records[2].Data);
        var files = HcppReader.AssembleFiles(records);
        Assert.Single(files);
        Assert.Equal("folder.txt", files[0].Name);
        Assert.Equal(payload, files[0].Content);
    }

    [Fact]
    public void ContentRecord_VariantVersionStrings_StillDetected()
    {
        // Measured variants of the 31-byte content header: "2.6"/"6.0" on the small
        // templates, "5.02"/"5.03" on the large real project. Detection is by the
        // "AutoShop" prefix only, so all variants classify as FileContent.
        foreach (var (version, subVersion) in new[] { ("2.6", "6.0"), ("5.02", "5.03"), ("2.0", "6.0") })
        {
            var header = new byte[HcppFraming.ContentHeaderSize];
            Encoding.ASCII.GetBytes("AutoShop").CopyTo(header, 0);
            Encoding.ASCII.GetBytes(version).CopyTo(header, 20);
            Encoding.ASCII.GetBytes(subVersion).CopyTo(header, 25);
            var payload = Encoding.ASCII.GetBytes("body");
            var inner = Concat(Record(Concat(header, payload)));
            var record = Assert.Single(HcppReader.ParseRecordStream(inner));
            Assert.Equal(HcppRecordKind.FileContent, record.Kind);
            Assert.Equal(payload, record.FileContent);
        }
    }

    [Fact]
    public void VendorShapedTemplateA1_ParsesEndToEnd()
    {
        // Synthesized replica of Template A1.hcpp's measured record stream:
        // Main("模板A.hcp") + (bare name, content) pairs — MAIN.LD/INT_001.LD/MAIN.mon/
        // DeviceTable.dev/PortConfig.cfg (AutoShop-header content) — combined records for
        // SBR_001.LD/VarList.gdt/Config.sdt/ElemUseInfo.esi/MDI.cfg (MDI.cfg empty content,
        // 0x2E trailer) — LoginPassowrd.dat bare name + encrypted content — terminator.
        var mainBlob = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var main = new HcppMainRecordSpec("模板A.hcp", mainBlob);
        var bare = new[]
        {
            new HcppFileSpec("MAIN.LD", Encoding.ASCII.GetBytes("ld-1")),
            new HcppFileSpec("INT_001.LD", Encoding.ASCII.GetBytes("ld-2")),
            new HcppFileSpec("MAIN.mon", Encoding.ASCII.GetBytes("mon")),
            new HcppFileSpec("DeviceTable.dev", new byte[] { 0x00, 0x01 }),
            new HcppFileSpec("PortConfig.cfg", Encoding.ASCII.GetBytes("cfg")),
        };
        var combined = new[]
        {
            new HcppFileSpec("SBR_001.LD", Concat(HcppFraming.ContentHeaderTemplate, Encoding.ASCII.GetBytes("sbr"))),
            new HcppFileSpec("VarList.gdt", Concat(HcppFraming.ContentHeaderTemplate, Encoding.ASCII.GetBytes("gdt"))),
            new HcppFileSpec("Config.sdt", Concat(HcppFraming.ContentHeaderTemplate, Encoding.ASCII.GetBytes("sdt"))),
            new HcppFileSpec("ElemUseInfo.esi", Concat(HcppFraming.ContentHeaderTemplate, Encoding.ASCII.GetBytes("esi"))),
            new HcppFileSpec("MDI.cfg", Array.Empty<byte>()),
        };
        // Real measured encrypted payload (contains control characters, so it classifies
        // as Unknown and pairs with the preceding bare name record).
        var encrypted = new byte[] { 0xB0, 0xDB, 0xB5, 0xD4, 0xA7, 0xA4, 0x87, 0xA6, 0xD7, 0xD5, 0xCD, 0xDC, 0x7A, 0xD7, 0x9B, 0x31, 0x02 };

        var bytes = HcppWriter.Write(main, bare);
        // Append combined + encrypted records by rebuilding the inner stream by hand:
        var inner = HcppReader.DecompressZlib(bytes, HcppHeader.HeaderSize);
        var records = new List<byte[]>
        {
            CombinedRecordData("SBR_001.LD", combined[0].Content, trailer: 0x524),
            CombinedRecordData("VarList.gdt", combined[1].Content, trailer: 0x145),
            CombinedRecordData("Config.sdt", combined[2].Content, trailer: 0x128),
            CombinedRecordData("ElemUseInfo.esi", combined[3].Content, trailer: 0xF3),
            CombinedRecordData("MDI.cfg", combined[4].Content, trailer: 0x2E),
            Encoding.ASCII.GetBytes("LoginPassowrd.dat"),
            encrypted,
        };
        using var ms = new MemoryStream();
        ms.Write(inner, 0, inner.Length - 4); // without the terminator the writer appended
        foreach (var r in records)
            ms.Write(Record(r));
        ms.Write(BitConverter.GetBytes(HcppFraming.Terminator));

        var file = Concat(HcppWriter.DefaultHeader.ToBytes(36), Zlib(ms.ToArray()));
        Assert.True(HcppReader.TryRead(file, out var container, out var error), error);

        Assert.NotNull(container!.MainRecord);
        Assert.Equal("模板A.hcp", container.MainRecord!.MainProjectName);
        Assert.Equal(mainBlob, container.MainRecord.MainBlob);

        var names = container.Files.Select(f => f.Name).ToArray();
        // This synthetic replica writes all bare file pairs first, then the combined
        // records (the real Template A1.hcpp interleaves them — see the harness report).
        var expected = new[]
        {
            "MAIN.LD", "INT_001.LD", "MAIN.mon", "DeviceTable.dev", "PortConfig.cfg",
            "SBR_001.LD", "VarList.gdt", "Config.sdt", "ElemUseInfo.esi", "MDI.cfg",
            "LoginPassowrd.dat",
        };
        Assert.Equal(expected, names);
        Assert.Equal(11, container.Files.Count);
        Assert.Equal(0x2Eu, container.Records.First(r => r.FileName == "MDI.cfg").Trailer);
        Assert.Equal(encrypted, container.Files.First(f => f.Name == "LoginPassowrd.dat").Content);
    }

    // ---------------------------------------------------------------
    // MainBlobScanner: segment recon of the opaque Main program blob
    // ---------------------------------------------------------------

    /// <summary>
    /// Fixed obfuscation cycle measured in every examined Main blob's odd positions
    /// (Template A1.hcpp 18,944 B and 连续式立包机.hcpp 263,612 B; see
    /// docs/inovance-autoshop-program-blob.md).
    /// </summary>
    private static readonly byte[] KnownCycle = Encoding.ASCII.GetBytes("SlotaecSfwr");

    /// <summary>
    /// Builds a blob in the MEASURED structure: prologue blob[0..2) then alternating
    /// [payload][cycle] with the cycle phase-locked so blob[21] == 'S' (odd-slot index i
    /// maps to cycle[(i - 10) mod 11], exactly as measured on the real samples).
    /// extractedPayload[1] must be the prologue intrusion byte ('j' on real samples).
    /// </summary>
    private static byte[] BuildKnownStructureBlob(byte[] extractedPayload)
    {
        var blob = new List<byte>(extractedPayload.Length + 32);
        blob.Add(extractedPayload[0]); // blob[0]
        blob.Add(extractedPayload[1]); // blob[1] = 'j' intrusion (odd position, deviates from cycle)
        int oddIdx = 1;
        for (int p = 2; p < extractedPayload.Length; p++)
        {
            blob.Add(extractedPayload[p]); // even position: payload byte
            blob.Add(KnownCycle[((oddIdx - 10) % 11 + 11) % 11]); // odd position: cycle byte
            oddIdx++;
        }
        return blob.ToArray();
    }

    /// <summary>Deterministic xorshift32 filler - high-entropy-looking payload for tests.</summary>
    private static byte[] DeterministicPayload(int length)
    {
        var payload = new byte[length];
        uint state = 0x12345678;
        for (int i = 0; i < length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            payload[i] = (byte)state;
        }
        return payload;
    }

    [Fact]
    public void MainBlobScanner_RecoversKnownCycleFromSyntheticBlob()
    {
        // Measured prologue: payload[0]='b', payload[1]='j' (Template A1 blob[0..2)).
        var payload = new byte[4096];
        payload[0] = (byte)'b';
        payload[1] = (byte)'j';
        DeterministicPayload(4096).AsSpan(2, 4094).CopyTo(payload.AsSpan(2));
        var blob = BuildKnownStructureBlob(payload);

        bool ok = MainBlobScanner.TryScan(blob, out var report, out var error);
        Assert.True(ok, error);
        Assert.NotNull(report);
        Assert.True(report.MatchesKnownStructure, string.Join("; ", report.Findings));
        Assert.Equal(11, report.CyclePeriod);
        Assert.Equal(2, report.CycleStride);
        Assert.Equal("SlotaecSfwr", report.CycleChars);
        Assert.Equal("SlotaecSfwr", MainBlobScanner.KnownCycleChars);
        Assert.Equal(21, MainBlobScanner.KnownCycleStartOffset);
        Assert.True(report.CyclePurity >= MainBlobScanner.KnownStructurePurityThreshold);
    }

    [Fact]
    public void MainBlobScanner_ExtractPayloadRoundTripsSyntheticBlob()
    {
        var payload = new byte[2048];
        payload[0] = (byte)'b';
        payload[1] = (byte)'j';
        DeterministicPayload(2048).AsSpan(2, 2046).CopyTo(payload.AsSpan(2));
        var blob = BuildKnownStructureBlob(payload);

        var extracted = MainBlobScanner.ExtractPayload(blob);
        Assert.Equal(payload, extracted);
        Assert.Equal(2048, report_PayloadLength(blob));
    }

    private static int report_PayloadLength(byte[] blob)
    {
        MainBlobScanner.TryScan(blob, out var report, out _);
        return report!.PayloadLength;
    }

    [Fact]
    public void MainBlobScanner_TryScanRejectsSmallBlob()
    {
        var small = new byte[32];
        bool ok = MainBlobScanner.TryScan(small, out var report, out var error);
        Assert.False(ok);
        Assert.Null(report);
        Assert.Contains("too small", error);
        Assert.Throws<HcppFormatException>(() => MainBlobScanner.Scan(small));
    }

    [Fact]
    public void MainBlobScanner_RandomBytesDoNotMatchKnownStructure()
    {
        var blob = DeterministicPayload(4096);
        bool ok = MainBlobScanner.TryScan(blob, out var report, out var error);
        Assert.True(ok, error);
        Assert.NotNull(report);
        Assert.False(report.MatchesKnownStructure);
        // Random bytes: no 11-char cycle should dominate the odd positions.
        Assert.True(report.CyclePurity < 0.5, $"unexpected purity {report.CyclePurity:P2}");
    }

    [Fact]
    public void MainBlobScanner_SegmentsMarkSuspendedRuns()
    {
        var payload = new byte[4096];
        payload[0] = (byte)'b';
        payload[1] = (byte)'j';
        DeterministicPayload(4096).AsSpan(2, 4094).CopyTo(payload.AsSpan(2));
        var blob = new List<byte>(BuildKnownStructureBlob(payload));

        // Inject a suspension far from the prologue run: overwrite 4 consecutive cycle-slot
        // bytes (slots 20..23 = blob offsets 41,43,45,47).
        int[] injected = { 0xAA, 0xBB, 0xCC, 0xDD };
        int firstOdd = 41;
        for (int k = 0; k < injected.Length; k++)
            blob[firstOdd + 2 * k] = (byte)injected[k];

        bool ok = MainBlobScanner.TryScan(blob.ToArray(), out var report, out var error);
        Assert.True(ok, error);
        Assert.NotNull(report);
        // 4 injected intrusions + the prologue 'j' at blob[1] (also a deviation).
        Assert.Equal(5, report.DeviationCount);
        Assert.Equal(2, report.SuspendedRunCount);
        Assert.Contains(report.Segments, s => s.Kind == MainBlobSegmentKind.CycleSuspendedPayload);
        // The interleaved majority of the blob is still recognized.
        Assert.Contains(report.Segments, s => s.Kind == MainBlobSegmentKind.Interleaved);
        // Small intrusions are tolerated: the real Template A1 blob has 69 deviation bytes
        // (99.27% purity) and still matches the known structure, so 4 injected bytes must too.
        Assert.True(report.MatchesKnownStructure);
        Assert.True(report.CyclePurity >= MainBlobScanner.KnownStructurePurityThreshold);
    }

    [Fact]
    public void MainBlobScanner_PayloadProfileFlagsEncryptedAndDetectsMagic()
    {
        var payload = new byte[4096];
        payload[0] = (byte)'b';
        payload[1] = (byte)'j';
        DeterministicPayload(4096).AsSpan(2, 4094).CopyTo(payload.AsSpan(2));
        var blob = BuildKnownStructureBlob(payload);

        bool ok = MainBlobScanner.TryScan(blob, out var report, out _);
        Assert.True(ok);
        Assert.NotNull(report);
        // xorshift filler: entropy ~7.9 bits/byte, printable fraction ~0.37 -> flagged encrypted.
        Assert.True(report.PayloadLooksEncrypted);
        Assert.True(report.PayloadEntropy >= MainBlobScanner.EncryptedEntropyThreshold);
        Assert.Null(report.PayloadMagic);

        // A payload that starts with a known signature is detected.
        var ppchPayload = new byte[4096];
        Encoding.ASCII.GetBytes("PPCH").CopyTo(ppchPayload, 0);
        DeterministicPayload(4096).AsSpan(4, 4092).CopyTo(ppchPayload.AsSpan(4));
        var ppchBlob = BuildKnownStructureBlob(ppchPayload);
        MainBlobScanner.TryScan(ppchBlob, out var ppchReport, out _);
        Assert.NotNull(ppchReport);
        Assert.Equal("PPCH", ppchReport.PayloadMagic);

        var elfPayload = new byte[4096];
        elfPayload[0] = 0x7F;
        Encoding.ASCII.GetBytes("ELF").CopyTo(elfPayload, 1);
        DeterministicPayload(4096).AsSpan(4, 4092).CopyTo(elfPayload.AsSpan(4));
        var elfBlob = BuildKnownStructureBlob(elfPayload);
        MainBlobScanner.TryScan(elfBlob, out var elfReport, out _);
        Assert.NotNull(elfReport);
        Assert.Equal("ELF", elfReport.PayloadMagic);
    }

    [Fact]
    public void MainBlobScanner_ScanRejectsInvalidStride()
    {
        var blob = DeterministicPayload(256);
        Assert.Throws<HcppFormatException>(() => MainBlobScanner.Scan(blob, cycleStride: 1));
        Assert.Throws<HcppFormatException>(() => MainBlobScanner.ExtractPayload(blob, 0));
    }

    /// <summary>
    /// Real-sample validation (env-gated: set PLCMCP_REAL_AUTOSHOP_SAMPLES=1 on a machine with
    /// the AutoShop install). Verifies the scanner against the measured Template A1 blob:
    /// cycle "SlotaecSfwr", purity >= 0.95, encrypted payload profile, 9,541-byte payload.
    /// </summary>
    [Fact]
    public void MainBlobScanner_RealTemplateSample_WhenAvailable()
    {
        string template = Path.Combine(
            @"D:\Inovance Control\AutoShop\Template_Project\English\Library_Template\Template A1",
            "Template A1.hcpp");
        bool enabled = Environment.GetEnvironmentVariable("PLCMCP_REAL_AUTOSHOP_SAMPLES") == "1";
        if (!enabled || !File.Exists(template))
        {
            // Offline CI: skip silently - the synthetic tests above carry the guarantee.
            return;
        }

        var containerBytes = File.ReadAllBytes(template);
        Assert.True(HcppReader.TryRead(containerBytes, out var container, out var error), error);
        Assert.NotNull(container!.MainRecord);
        var blob = container.MainRecord.MainBlob!;
        Assert.Equal(18944, blob.Length);

        bool ok = MainBlobScanner.TryScan(blob, out var report, out var scanError);
        Assert.True(ok, scanError);
        Assert.NotNull(report);
        Assert.True(report.MatchesKnownStructure, string.Join("; ", report.Findings));
        Assert.Equal("SlotaecSfwr", report.CycleChars);
        Assert.Equal(11, report.CyclePeriod);
        Assert.Equal(2, report.CycleStride);
        Assert.Equal(9541, report.PayloadLength);
        Assert.Equal(69, report.DeviationCount);
        Assert.Equal(17, report.SuspendedRunCount);
        Assert.True(report.PayloadLooksEncrypted);
        Assert.Null(report.PayloadMagic);

        // V5.x large project (连续式立包机): same structure, 263,612 B blob, 210 deviations.
        string large = Path.Combine(
            @"D:\Inovance Control\AutoShop\Template_Project\Simplified_Chinese\Library_Template\连续式立包机",
            "连续式立包机.hcpp");
        if (!File.Exists(large))
            return;
        Assert.True(HcppReader.TryRead(File.ReadAllBytes(large), out var largeContainer, out var largeError), largeError);
        var largeBlob = largeContainer!.MainRecord!.MainBlob!;
        Assert.Equal(263612, largeBlob.Length);
        bool ok2 = MainBlobScanner.TryScan(largeBlob, out var largeReport, out var largeScanError);
        Assert.True(ok2, largeScanError);
        Assert.NotNull(largeReport);
        Assert.True(largeReport.MatchesKnownStructure, string.Join("; ", largeReport.Findings));
        Assert.Equal("SlotaecSfwr", largeReport.CycleChars);
        Assert.Equal(132016, largeReport.PayloadLength);
        Assert.Equal(210, largeReport.DeviationCount);
        Assert.Equal(56, largeReport.SuspendedRunCount);
        Assert.True(largeReport.PayloadLooksEncrypted);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
