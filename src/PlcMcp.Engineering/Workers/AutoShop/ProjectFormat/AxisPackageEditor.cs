using PlcMcp.AutoShopNativeBridge.Crypto;

namespace PlcMcp.Engineering.Workers.AutoShop.ProjectFormat;

/// <summary>Result of locating the axis.xml member inside an .hcpp container.</summary>
public sealed record AxisFileLocation(string Name, byte[] Content);

/// <summary>
/// Offline read/modify/write pipeline for AutoShop project packages (.hcpp) with an axis
/// configuration (xml\axis.xml) member:
///
/// <code>
///   HcppReader.Read(bytes)                      -- P1 container parser (real-sample verified)
///     -> locate the axis.xml member (name match, any directory prefix)
///     -> AxisConfigXml.TryParse                 -- structure-preserving (RE-derived schema)
///     -> caller edits the AxisConfigDocument
///     -> AxisConfigXml.Write -> HcppWriter.Write -- re-emit the container (main blob byte-identical)
///     -> HcppReader.Read (verify) [+ AutoShopProjectCrypt.DecryptBlob for the main XML path]
/// </code>
///
/// Encryption note: the .hcpp member files (including axis.xml) are stored PLAINTEXT inside the
/// container — the UCodeCrypt ADD/XOR layer (key "SclSoftware") protects the MAIN record only
/// (the .hcp project XML; docs/inovance-autoshop-project-encryption.md). The axis path therefore
/// needs no decryption; the main-XML edit path (<see cref="TryEditMainXmlAndRepackage"/>) wraps
/// DecryptBlob/EncryptBlob around the caller's edit.
///
/// Vendor-compatibility caveat (inherited from P1): HcppWriter containers are self-consistent
/// (HcppReader reads them back) but "AutoShop can open the result" is UNVERIFIED — the CLI open
/// path fails on unmodified vendor templates too (docs/inovance-autoshop-open-and-compile.md).
/// </summary>
public static class AxisPackageEditor
{
    /// <summary>Candidate member names for the axis configuration inside a container.</summary>
    private static bool IsAxisFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        var n = name.Replace('\\', '/');
        var leaf = n.Length > 0 && n[(n.Length - 1)..] is "/" ? n[..^1] : n;
        var fileName = leaf.Contains('/')
            ? leaf[(leaf.LastIndexOf('/') + 1)..]
            : leaf;
        return fileName.Equals("axis.xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Finds the axis.xml member. Returns null (no error) when absent.</summary>
    public static AxisFileLocation? FindAxisFile(HcppContainer container)
    {
        foreach (var (name, content) in container.Files)
        {
            if (IsAxisFileName(name))
                return new AxisFileLocation(name, content);
        }
        return null;
    }

    /// <summary>
    /// Loads the axis configuration from an .hcpp package. Returns false with a reason when the
    /// container is invalid or has no axis.xml member (absent axis config is a normal H3U-style
    /// project — callers must distinguish via <paramref name="hasAxisFile"/>).
    /// </summary>
    public static bool TryLoad(
        byte[] hcpp,
        out AxisConfigDocument? document,
        out bool hasAxisFile,
        out string? error)
    {
        document = null;
        hasAxisFile = false;
        error = null;
        if (!HcppReader.TryRead(hcpp, out var container, out error))
            return false;

        var axis = FindAxisFile(container!);
        if (axis is null)
            return true; // valid container, no axis member
        hasAxisFile = true;

        var text = DecodeXml(axis.Content);
        if (!AxisConfigXml.TryParse(text, out document, out error))
            return false;
        return true;
    }

    /// <summary>
    /// Read → edit → write round trip on the axis.xml member. The main record blob passes
    /// through byte-identical (no re-encryption needed for member edits).
    /// </summary>
    public static bool TryEditAxisAndRepackage(
        byte[] hcpp,
        Action<AxisConfigDocument> edit,
        out byte[]? repacked,
        out string? error)
    {
        repacked = null;
        error = null;
        if (!HcppReader.TryRead(hcpp, out var container, out error))
            return false;

        var axis = FindAxisFile(container!);
        if (axis is null)
        {
            error = "container has no axis.xml member.";
            return false;
        }
        var text = DecodeXml(axis.Content);
        if (!AxisConfigXml.TryParse(text, out var doc, out error))
            return false;

        try
        {
            edit(doc!);
        }
        catch (Exception ex)
        {
            error = $"axis edit callback failed: {ex.Message}";
            return false;
        }

        var newText = AxisConfigXml.Write(doc!);
        var newFiles = container.Files
            .Select(f => f.Name == axis.Name
                ? new HcppFileSpec(f.Name, System.Text.Encoding.UTF8.GetBytes(newText))
                : new HcppFileSpec(f.Name, f.Content))
            .ToList();

        var main = container.MainRecord;
        var mainSpec = main is null
            ? null
            : new HcppMainRecordSpec(main.MainProjectName!, main.MainBlob!);
        repacked = HcppWriter.Write(mainSpec, newFiles);
        return true;
    }

    /// <summary>
    /// Main-XML (project property) edit path: DecryptBlob(main blob) → caller edit on the
    /// UTF-16LE project XML → EncryptBlob → repackage. Demonstrates the D3 decrypt→edit→
    /// re-encrypt loop end to end.
    /// </summary>
    public static bool TryEditMainXmlAndRepackage(
        byte[] hcpp,
        Func<string, string> editMainXml,
        out byte[]? repacked,
        out string? error)
    {
        repacked = null;
        error = null;
        if (!HcppReader.TryRead(hcpp, out var container, out error))
            return false;
        var main = container!.MainRecord;
        if (main is null)
        {
            error = "container has no main record.";
            return false;
        }

        string xml;
        try
        {
            var plain = AutoShopProjectCrypt.DecryptBlob(main.MainBlob!);
            xml = System.Text.Encoding.Unicode.GetString(plain);
            // strip the U+FEFF BOM char (the vendor blob starts FF FE); it is re-emitted below
            if (xml.Length > 0 && xml[0] == '\uFEFF')
                xml = xml[1..];
        }
        catch (Exception ex)
        {
            error = $"main blob decryption failed: {ex.Message}";
            return false;
        }

        string newXml;
        try
        {
            newXml = editMainXml(xml);
        }
        catch (Exception ex)
        {
            error = $"main xml edit callback failed: {ex.Message}";
            return false;
        }

        byte[] newBlob;
        try
        {
            // keep the UTF-16LE BOM the vendor writer emits (D3: FF FE + UTF-16LE XML)
            var body = System.Text.Encoding.Unicode.GetBytes(newXml);
            var plain = new byte[body.Length + 2];
            plain[0] = 0xFF;
            plain[1] = 0xFE;
            body.CopyTo(plain, 2);
            newBlob = AutoShopProjectCrypt.EncryptBlob(plain);
        }
        catch (Exception ex)
        {
            error = $"main blob re-encryption failed: {ex.Message}";
            return false;
        }

        var newFiles = container.Files
            .Select(f => new HcppFileSpec(f.Name, f.Content))
            .ToList();
        repacked = HcppWriter.Write(
            new HcppMainRecordSpec(main.MainProjectName!, newBlob),
            newFiles);
        return true;
    }

    /// <summary>
    /// Verification helper: re-reads a repacked container and returns (axisXml text, decrypted
    /// main XML) for assertions. Decrypts the main blob when present.
    /// </summary>
    public static bool TryVerify(
        byte[] hcpp,
        out string? axisXml,
        out string? mainXml,
        out string? error)
    {
        axisXml = null;
        mainXml = null;
        error = null;
        if (!HcppReader.TryRead(hcpp, out var container, out error))
            return false;
        var axis = FindAxisFile(container!);
        if (axis is not null)
            axisXml = DecodeXml(axis.Content);
        var main = container.MainRecord;
        if (main is not null)
        {
            try
            {
                var plain = AutoShopProjectCrypt.DecryptBlob(main.MainBlob!);
                mainXml = System.Text.Encoding.Unicode.GetString(plain);
            }
            catch (Exception ex)
            {
                error = $"main blob decryption failed during verify: {ex.Message}";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Decodes member XML text. Vendor member files are UTF-8 (with or without BOM) per the
    /// observed containers; UTF-16 is accepted for synthetic packages.
    /// </summary>
    private static string DecodeXml(byte[] content)
    {
        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
            return System.Text.Encoding.Unicode.GetString(content, 2, content.Length - 2);
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(content, 3, content.Length - 3);
        return System.Text.Encoding.UTF8.GetString(content);
    }
}
