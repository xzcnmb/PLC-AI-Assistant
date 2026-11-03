using System.Buffers.Binary;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PlcMcp.Engineering.Workers.Keyence;

// Read-only snapshot of a KV STUDIO project directory.
//
// Pure offline analysis: .kpr container header, member file inventory, and XML
// summaries. Nothing here claims to decode encrypted payloads; unknown bytes are
// hex-reported verbatim and size-rule violations are reported honestly rather
// than guessed at.
//
// Verified 40-byte .kpr header layout (from a real KV STUDIO 12G blank project,
// the "PROJECT\0" family):
//   off   size  field        byte evidence
//   0x00  8     magic        ASCII, NUL-padded, e.g. "PROJECT\0"
//   0x08  4     version      little-endian u32, e.g. 0x00000038
//   0x0C  4     flags        little-endian u32, e.g. 0x00000200
//   0x10  8     stamp        8 raw bytes (family/version marker), hex-verbatim
//   0x18  4     payloadLen   little-endian u32, e.g. 0x00000762 (=1890)
//   0x1C  4     tail         4 raw bytes (undetermined), hex-verbatim
// The rule size == payloadLen + 40 holds for the .kpr family; some member
// containers (e.g. All.prgx) violate it - SizeRuleHolds reports this honestly.

/// <summary>Parsed 40-byte header of a .kpr container (raw fields hex-reported).</summary>
public sealed record KeyenceKprHeader(
    string Magic,
    uint Version,
    uint Flags,
    string StampHex,
    uint PayloadLength,
    string TailHex,
    int FileSize,
    bool SizeRuleHolds);

/// <summary>One file discovered under the project directory.</summary>
public sealed record KeyenceMemberInfo(string FileName, string Extension, long Size);

/// <summary>Root element and descendant count of one member XML file.</summary>
public sealed record KeyenceXmlSummary(string FileName, string RootElement, int NodeCount);

/// <summary>Read-only snapshot result for one KV STUDIO project directory.</summary>
public sealed record KeyenceProjectSnapshotResult(
    string ProjectDir,
    bool ProjectDirExists,
    KeyenceKprHeader? KprHeader,
    IReadOnlyList<KeyenceMemberInfo> Members,
    IReadOnlyList<KeyenceXmlSummary> XmlSummaries,
    IReadOnlyList<string> Warnings,
    string Summary);

/// <summary>
/// Read-only snapshot of a KV STUDIO project directory: .kpr header, member
/// inventory, and XML summaries. Detection and reporting only; encrypted
/// payloads are never decoded.
/// </summary>
public static class KeyenceProjectSnapshot
{
    private const int HeaderSize = 40;
    private const long MaxParseSize = 1024 * 1024;

    private static readonly string[] SkippedDirectoryNames = ["obj", "bin", ".git"];

    /// <summary>Take a snapshot of one project directory (never writes anything).</summary>
    public static KeyenceProjectSnapshotResult Take(string projectDir)
    {
        var warnings = new List<string>();
        string dir = projectDir ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            warnings.Add($"Project directory does not exist: '{dir}'.");
            return new KeyenceProjectSnapshotResult(
                ProjectDir: dir,
                ProjectDirExists: false,
                KprHeader: null,
                Members: [],
                XmlSummaries: [],
                Warnings: warnings,
                Summary: "KV STUDIO project snapshot: project directory does not exist.");
        }

        var members = new List<KeyenceMemberInfo>();
        var xmlFiles = new List<(string FileName, string Path)>();
        CollectMembers(dir, members, xmlFiles, warnings);
        members = [.. members.OrderBy(m => m.FileName, StringComparer.Ordinal)];

        var xmlSummaries = new List<KeyenceXmlSummary>();
        foreach (var (fileName, path) in xmlFiles.OrderBy(f => f.FileName, StringComparer.Ordinal))
        {
            var summary = SummarizeXml(fileName, path, warnings);
            if (summary is not null)
            {
                xmlSummaries.Add(summary);
            }
        }

        KeyenceKprHeader? kprHeader = null;
        string? kprPath = FindKprFile(dir, warnings);
        if (kprPath is not null)
        {
            kprHeader = ParseKprHeader(kprPath, warnings);
        }

        string summaryLine = $"KV STUDIO project snapshot: {members.Count} members, " +
                             $"{(kprHeader is null ? 0 : 1)} .kpr header(s) parsed, " +
                             $"{xmlSummaries.Count} XML summary(ies).";

        return new KeyenceProjectSnapshotResult(
            ProjectDir: dir,
            ProjectDirExists: true,
            KprHeader: kprHeader,
            Members: members,
            XmlSummaries: xmlSummaries,
            Warnings: warnings,
            Summary: summaryLine);
    }

    private static string? FindKprFile(string dir, List<string> warnings)
    {
        try
        {
            // The Win32 "*.kpr" pattern can also match longer extensions (legacy
            // behaviour), so the extension is re-checked and the candidate list is
            // ordered by file name to make "first" deterministic.
            string? first = Directory.EnumerateFiles(dir, "*.kpr")
                .Where(f => string.Equals(Path.GetExtension(f), ".kpr", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .FirstOrDefault();
            if (first is null)
            {
                warnings.Add("No .kpr container found in directory.");
            }

            return first;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not enumerate directory for .kpr: {ex.Message}");
            return null;
        }
    }

    private static KeyenceKprHeader? ParseKprHeader(string path, List<string> warnings)
    {
        string name = Path.GetFileName(path);
        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not stat .kpr '{name}': {ex.Message}");
            return null;
        }

        if (size < HeaderSize)
        {
            warnings.Add($"Truncated .kpr ({size} bytes < 40).");
            return null;
        }

        if (size > MaxParseSize)
        {
            warnings.Add($".kpr '{name}' exceeds the 1 MiB snapshot limit; header not parsed.");
            return null;
        }

        try
        {
            var bytes = new byte[HeaderSize];
            using (var stream = File.OpenRead(path))
            {
                int read = stream.ReadAtLeast(bytes, HeaderSize, throwOnEndOfStream: false);
                if (read < HeaderSize)
                {
                    warnings.Add($"Truncated .kpr ({read} bytes < 40).");
                    return null;
                }
            }

            string magic = Encoding.ASCII.GetString(bytes, 0, 8).TrimEnd('\0');
            if (magic.Length == 0)
            {
                magic = "(empty)";
            }

            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x18, 4));
            return new KeyenceKprHeader(
                Magic: magic,
                Version: BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x08, 4)),
                Flags: BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x0C, 4)),
                StampHex: Convert.ToHexString(bytes.AsSpan(0x10, 8)),
                PayloadLength: payloadLength,
                TailHex: Convert.ToHexString(bytes.AsSpan(0x1C, 4)),
                FileSize: (int)size,
                SizeRuleHolds: (long)payloadLength + HeaderSize == size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not read .kpr '{name}': {ex.Message}");
            return null;
        }
    }

    private static void CollectMembers(string dir, List<KeyenceMemberInfo> members, List<(string FileName, string Path)> xmlFiles, List<string> warnings)
    {
        foreach (string file in SafeEnumerateFiles(dir, warnings))
        {
            try
            {
                string fileName = Path.GetFileName(file);
                long size = new FileInfo(file).Length;
                string extension = Path.GetExtension(fileName).ToUpperInvariant();
                members.Add(new KeyenceMemberInfo(fileName, extension, size));
                if (extension == ".XML" && size <= MaxParseSize)
                {
                    xmlFiles.Add((fileName, file));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not inspect file '{file}': {ex.Message}");
            }
        }

        string[] subDirs;
        try
        {
            subDirs = Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not enumerate subdirectories of '{dir}': {ex.Message}");
            return;
        }

        foreach (string sub in subDirs)
        {
            string subName = Path.GetFileName(sub);
            if (SkippedDirectoryNames.Any(s => string.Equals(s, subName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            CollectMembers(sub, members, xmlFiles, warnings);
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir, List<string> warnings)
    {
        try
        {
            return Directory.EnumerateFiles(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not enumerate files in '{dir}': {ex.Message}");
            return [];
        }
    }

    private static KeyenceXmlSummary? SummarizeXml(string fileName, string path, List<string> warnings)
    {
        try
        {
            var doc = XDocument.Load(path);
            // Count descendants of the root element; on XDocument, Descendants()
            // itself includes the root, which would over-count by one.
            int nodeCount = doc.Root?.Descendants().Count() ?? 0;
            return new KeyenceXmlSummary(fileName, doc.Root?.Name.LocalName ?? "(none)", nodeCount);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            warnings.Add($"XML parse failed: '{fileName}': {ex.Message}");
            return null;
        }
    }
}
