using System.Buffers.Binary;
using System.Text;
using PlcMcp.Engineering.Workers.Keyence;

namespace PlcMcp.Engineering.Tests;

public class KeyenceProjectSnapshotTests
{
    /// <summary>
    /// Builds the verified 40-byte .kpr header layout: magic "PROJECT\0", version
    /// 0x38, flags 0x200, stamp 0B FA 5C 03 01 00 01 00, payloadLen (LE), and an
    /// arbitrary tail.
    /// </summary>
    private static byte[] BuildKprHeader(uint payloadLen)
    {
        var header = new byte[40];
        Encoding.ASCII.GetBytes("PROJECT").CopyTo(header, 0); // 0x00: "PROJECT" + NUL pad
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x08, 4), 0x38);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x0C, 4), 0x200);
        byte[] stamp = [0x0B, 0xFA, 0x5C, 0x03, 0x01, 0x00, 0x01, 0x00];
        stamp.CopyTo(header, 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x18, 4), payloadLen);
        byte[] tail = [0xDE, 0xAD, 0xBE, 0xEF];
        tail.CopyTo(header, 0x1C);
        return header;
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "KeyenceProjectSnapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp directory.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of the temp directory.
        }
    }

    [Fact]
    public void KeyenceProjectSnapshot_Take_ParsesRealShapedHeader()
    {
        string dir = CreateTempDir();
        try
        {
            byte[] header = BuildKprHeader(1890);
            var file = new byte[40 + 1890];
            header.CopyTo(file, 0);
            file.AsSpan(40).Fill(0xAB);
            File.WriteAllBytes(Path.Combine(dir, "Proj.kpr"), file);

            var result = KeyenceProjectSnapshot.Take(dir);

            Assert.True(result.ProjectDirExists);
            Assert.NotNull(result.KprHeader);
            Assert.Equal("PROJECT", result.KprHeader.Magic);
            Assert.Equal(0x38u, result.KprHeader.Version);
            Assert.Equal(0x200u, result.KprHeader.Flags);
            Assert.Equal("0BFA5C0301000100", result.KprHeader.StampHex);
            Assert.Equal(1890u, result.KprHeader.PayloadLength);
            Assert.True(result.KprHeader.SizeRuleHolds);
            Assert.Contains(result.Members, m => m.FileName == "Proj.kpr");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void KeyenceProjectSnapshot_Take_ReportsNoKpr_WhenOnlyMemberContainersPresent()
    {
        string dir = CreateTempDir();
        try
        {
            // Header-only container claiming payloadLen=1890 in a 40-byte file:
            // the size rule is violated, and there is no .kpr to parse.
            File.WriteAllBytes(Path.Combine(dir, "Bad.prgx"), BuildKprHeader(1890));

            var result = KeyenceProjectSnapshot.Take(dir);

            Assert.Null(result.KprHeader);
            Assert.Contains(result.Warnings, w => w.Contains("No .kpr", StringComparison.Ordinal));
            Assert.Contains(result.Members, m => m.FileName == "Bad.prgx");
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void KeyenceProjectSnapshot_Take_ReportsSizeRuleViolation_Honestly()
    {
        string dir = CreateTempDir();
        try
        {
            // 40-byte .kpr claiming payloadLen=300: header parses, rule does not hold.
            File.WriteAllBytes(Path.Combine(dir, "Small.kpr"), BuildKprHeader(300));

            var result = KeyenceProjectSnapshot.Take(dir);

            Assert.NotNull(result.KprHeader);
            Assert.Equal(300u, result.KprHeader.PayloadLength);
            Assert.Equal(40, result.KprHeader.FileSize);
            Assert.False(result.KprHeader.SizeRuleHolds);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void KeyenceProjectSnapshot_Take_SummariesMemberXml()
    {
        string dir = CreateTempDir();
        try
        {
            const string xml = "<workspace><project/><fb/></workspace>";
            File.WriteAllText(Path.Combine(dir, "WsTreeEnv.xml"), xml);

            var result = KeyenceProjectSnapshot.Take(dir);

            var summary = Assert.Single(result.XmlSummaries);
            Assert.Equal("WsTreeEnv.xml", summary.FileName);
            Assert.Equal("workspace", summary.RootElement);
            Assert.Equal(2, summary.NodeCount);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public void KeyenceProjectSnapshot_Take_MissingDirectory_Reported()
    {
        var result = KeyenceProjectSnapshot.Take(@"Z:\No\Such\Dir");

        Assert.False(result.ProjectDirExists);
        Assert.Null(result.KprHeader);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void KeyenceProjectSnapshot_Take_EmptyDir_HasNoKprAndZeroMembers()
    {
        string dir = CreateTempDir();
        try
        {
            var result = KeyenceProjectSnapshot.Take(dir);

            Assert.True(result.ProjectDirExists);
            Assert.Null(result.KprHeader);
            Assert.Empty(result.Members);
            Assert.Contains("0 members", result.Summary);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }
}
