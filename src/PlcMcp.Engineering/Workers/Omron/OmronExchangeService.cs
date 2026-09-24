using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace PlcMcp.Engineering.Workers.Omron;

#region Public Contract Records

/// <summary>
/// Symbol information extracted from Omron IEC 61131-10 XML or PLCopen XML.
/// </summary>
public sealed record OmronSymbolInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("dataType")] string DataType,
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("scope")] string Scope = "Global",
    [property: JsonPropertyName("initialValue")] string? InitialValue = null,
    [property: JsonPropertyName("comment")] string? Comment = null,
    [property: JsonPropertyName("isConstant")] bool IsConstant = false,
    [property: JsonPropertyName("isRetain")] bool IsRetain = false);

/// <summary>
/// POU information extracted from standard exchange files.
/// </summary>
public sealed record OmronPouInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("pouType")] string PouType,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("variableCount")] int VariableCount,
    [property: JsonPropertyName("variables")] IReadOnlyList<OmronSymbolInfo> Variables,
    [property: JsonPropertyName("bodySummary")] string? BodySummary,
    [property: JsonPropertyName("rawContentHash")] string RawContentHash);

/// <summary>
/// Data type declaration extracted from exchange files (structures, enums, unions).
/// </summary>
public sealed record OmronDataTypeInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("baseType")] string? BaseType,
    [property: JsonPropertyName("memberCount")] int MemberCount,
    [property: JsonPropertyName("members")] IReadOnlyList<string> Members);

/// <summary>
/// AutomationML CAEX object node information.
/// </summary>
public sealed record OmronCaexObjectInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("deviceType")] string? DeviceType,
    [property: JsonPropertyName("manufacturer")] string? Manufacturer,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("firmwareVersion")] string? FirmwareVersion,
    [property: JsonPropertyName("childCount")] int ChildCount);

/// <summary>
/// Diagnostic message from structural analysis.
/// </summary>
public sealed record OmronDiagnosticInfo(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Inspection report for an Omron exchange file (IEC 61131-10 XML, PLCopen XML, or AutomationML).
/// </summary>
public sealed record OmronExchangeReport(
    [property: JsonPropertyName("sourcePath")] string SourcePath,
    [property: JsonPropertyName("sourceSha256")] string SourceSha256,
    [property: JsonPropertyName("byteLength")] long ByteLength,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("targetDevice")] string? TargetDevice,
    [property: JsonPropertyName("pous")] IReadOnlyList<OmronPouInfo> Pous,
    [property: JsonPropertyName("symbols")] IReadOnlyList<OmronSymbolInfo> Symbols,
    [property: JsonPropertyName("dataTypes")] IReadOnlyList<OmronDataTypeInfo> DataTypes,
    [property: JsonPropertyName("objects")] IReadOnlyList<OmronCaexObjectInfo> Objects,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<OmronDiagnosticInfo> Diagnostics,
    [property: JsonPropertyName("limitations")] IReadOnlyList<string> Limitations,
    [property: JsonPropertyName("rawExtensionHashes")] IReadOnlyDictionary<string, string> RawExtensionHashes,
    [property: JsonPropertyName("validationLevel")] string ValidationLevel = "structural",
    [property: JsonPropertyName("isVendorCompiler")] bool IsVendorCompiler = false,
    [property: JsonPropertyName("disclaimer")] string Disclaimer = "Structural inspection only. Not a vendor compiler verification. No native Sysmac Studio export or execution.");

/// <summary>
/// Detailed difference for an individual POU.
/// </summary>
public sealed record OmronPouDiff(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("changeSummary")] string ChangeSummary,
    [property: JsonPropertyName("bodyDiff")] bool BodyDiff,
    [property: JsonPropertyName("variablesDiff")] bool VariablesDiff,
    [property: JsonPropertyName("rawHashDiff")] bool RawHashDiff);

/// <summary>
/// Detailed difference for an individual symbol.
/// </summary>
public sealed record OmronSymbolDiff(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("leftType")] string? LeftType,
    [property: JsonPropertyName("rightType")] string? RightType,
    [property: JsonPropertyName("leftAddress")] string? LeftAddress,
    [property: JsonPropertyName("rightAddress")] string? RightAddress,
    [property: JsonPropertyName("leftScope")] string? LeftScope,
    [property: JsonPropertyName("rightScope")] string? RightScope);

/// <summary>
/// Deterministic diff report comparing two exchange files.
/// </summary>
public sealed record OmronExchangeDiff(
    [property: JsonPropertyName("leftPath")] string LeftPath,
    [property: JsonPropertyName("rightPath")] string RightPath,
    [property: JsonPropertyName("leftSha256")] string LeftSha256,
    [property: JsonPropertyName("rightSha256")] string RightSha256,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("hasDifferences")] bool HasDifferences,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("addedPous")] IReadOnlyList<string> AddedPous,
    [property: JsonPropertyName("removedPous")] IReadOnlyList<string> RemovedPous,
    [property: JsonPropertyName("modifiedPous")] IReadOnlyList<OmronPouDiff> ModifiedPous,
    [property: JsonPropertyName("addedSymbols")] IReadOnlyList<string> AddedSymbols,
    [property: JsonPropertyName("removedSymbols")] IReadOnlyList<string> RemovedSymbols,
    [property: JsonPropertyName("modifiedSymbols")] IReadOnlyList<OmronSymbolDiff> ModifiedSymbols,
    [property: JsonPropertyName("addedDataTypes")] IReadOnlyList<string> AddedDataTypes,
    [property: JsonPropertyName("removedDataTypes")] IReadOnlyList<string> RemovedDataTypes,
    [property: JsonPropertyName("modifiedDataTypes")] IReadOnlyList<string> ModifiedDataTypes,
    [property: JsonPropertyName("addedCaexObjects")] IReadOnlyList<string> AddedCaexObjects,
    [property: JsonPropertyName("removedCaexObjects")] IReadOnlyList<string> RemovedCaexObjects,
    [property: JsonPropertyName("modifiedCaexObjects")] IReadOnlyList<string> ModifiedCaexObjects,
    [property: JsonPropertyName("rawExtensionDifferences")] IReadOnlyList<string> RawExtensionDifferences);

#endregion

/// <summary>
/// Offline reader and comparison service for Omron standard exchange files
/// (IEC 61131-10 XML, PLCopen XML, AutomationML CAEX).
/// Enforces strict path sandbox, XXE prohibition, size, depth, and node limits.
/// Operates purely offline without Sysmac Studio, nexcc, ACE, COM, or PLC connections.
/// </summary>
public sealed class OmronExchangeService
{
    private const long MaxFileSizeBytes = 4 * 1024 * 1024; // 4 MiB limit
    private const int MaxXmlDepth = 64;
    private const int MaxXmlNodeCount = 50_000;

    private static readonly XmlReaderSettings SafeReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        CloseInput = true
    };

    public string AllowedRoot { get; }
    private readonly string _allowedRootNormalized;
    private readonly string _allowedRootPrefix;

    public OmronExchangeService(string allowedRoot)
    {
        if (string.IsNullOrWhiteSpace(allowedRoot))
            throw new ArgumentException("Allowed root cannot be empty.", nameof(allowedRoot));

        if (!Path.IsPathFullyQualified(allowedRoot))
            throw new ArgumentException($"Allowed root must be a fully qualified path: '{allowedRoot}'", nameof(allowedRoot));

        var fullAllowed = Path.GetFullPath(allowedRoot);
        var rootPath = Path.GetPathRoot(fullAllowed);

        // Reject broad root directories like "C:\", "D:\", "/"
        if (string.Equals(fullAllowed.TrimEnd('\\', '/'), rootPath?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Allowed root cannot be a drive or root directory: '{allowedRoot}'", nameof(allowedRoot));
        }

        var dirInfo = new DirectoryInfo(fullAllowed);
        if (dirInfo.Exists && dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException($"Allowed root '{fullAllowed}' cannot be a reparse point or junction.");
        }

        AllowedRoot = fullAllowed;
        _allowedRootNormalized = Path.TrimEndingDirectorySeparator(fullAllowed);
        _allowedRootPrefix = _allowedRootNormalized + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Validates path sandbox, reparse point constraints, reads bytes once, verifies XML safety,
    /// and parses the exchange structure.
    /// </summary>
    public OmronExchangeReport Inspect(string absolutePath)
    {
        string fullPath = ValidateAndSanitizePath(absolutePath);

        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException($"Exchange file not found: '{fullPath}'", fullPath);

        if (fileInfo.Length > MaxFileSizeBytes)
        {
            throw new InvalidOperationException($"File '{fullPath}' exceeds maximum allowed size of 4 MiB ({fileInfo.Length} bytes).");
        }

        // Read bytes once for both SHA-256 calculation and XML parsing
        byte[] bytes = File.ReadAllBytes(fullPath);
        if (bytes.Length > MaxFileSizeBytes)
        {
            throw new InvalidOperationException($"File '{fullPath}' exceeds maximum allowed size of 4 MiB ({bytes.Length} bytes).");
        }

        string sourceSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        // Enforce safe XML pre-scan for DTD prohibition, depth, and node count limits
        PrecheckXmlLimits(bytes);

        // Parse DOM from the same verified memory buffer
        XDocument doc;
        using (var ms = new MemoryStream(bytes, writable: false))
        using (var reader = XmlReader.Create(ms, SafeReaderSettings))
        {
            doc = XDocument.Load(reader);
        }

        var root = doc.Root ?? throw new InvalidOperationException("XML document has no root element.");

        // Detect format and reject unknown root/namespace
        string format = DetectFormat(root);

        return format switch
        {
            "IEC-61131-10-XML" => ParseIec61131_10(fullPath, sourceSha256, bytes.Length, root),
            "PLCopen-XML" => ParsePlcopen(fullPath, sourceSha256, bytes.Length, root),
            "AutomationML-CAEX" => ParseAutomationMl(fullPath, sourceSha256, bytes.Length, root),
            _ => throw new InvalidOperationException($"Unsupported format '{format}'.")
        };
    }

    /// <summary>
    /// Compares two Omron exchange files and returns a deterministic, stably sorted diff.
    /// Distinguishes unsupported extension differences so as not to fake semantic equivalence.
    /// </summary>
    public OmronExchangeDiff Compare(string leftPath, string rightPath)
    {
        var left = Inspect(leftPath);
        var right = Inspect(rightPath);

        bool formatMismatch = !string.Equals(left.Format, right.Format, StringComparison.OrdinalIgnoreCase);

        // POUs comparison
        var leftPous = left.Pous.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
        var rightPous = right.Pous.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

        var addedPous = rightPous.Keys.Except(leftPous.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var removedPous = leftPous.Keys.Except(rightPous.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var commonPous = leftPous.Keys.Intersect(rightPous.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        var modifiedPous = new List<OmronPouDiff>();
        foreach (var name in commonPous)
        {
            var p1 = leftPous[name];
            var p2 = rightPous[name];

            bool bodyDiff = !string.Equals(p1.BodySummary, p2.BodySummary, StringComparison.Ordinal);
            bool typeDiff = !string.Equals(p1.PouType, p2.PouType, StringComparison.OrdinalIgnoreCase);
            bool langDiff = !string.Equals(p1.Language, p2.Language, StringComparison.OrdinalIgnoreCase);
            bool varCountDiff = p1.VariableCount != p2.VariableCount;
            bool rawHashDiff = !string.Equals(p1.RawContentHash, p2.RawContentHash, StringComparison.Ordinal);

            bool varDetailsDiff = false;
            if (!varCountDiff)
            {
                for (int i = 0; i < p1.Variables.Count; i++)
                {
                    var v1 = p1.Variables[i];
                    var v2 = p2.Variables[i];
                    if (!string.Equals(v1.Name, v2.Name, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(v1.DataType, v2.DataType, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(v1.Address, v2.Address, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(v1.Scope, v2.Scope, StringComparison.OrdinalIgnoreCase) ||
                        v1.IsConstant != v2.IsConstant ||
                        v1.IsRetain != v2.IsRetain)
                    {
                        varDetailsDiff = true;
                        break;
                    }
                }
            }

            if (bodyDiff || typeDiff || langDiff || varCountDiff || varDetailsDiff || rawHashDiff)
            {
                var reasons = new List<string>();
                if (typeDiff) reasons.Add($"Type: {p1.PouType}->{p2.PouType}");
                if (langDiff) reasons.Add($"Lang: {p1.Language}->{p2.Language}");
                if (bodyDiff) reasons.Add("Body modified");
                if (varCountDiff || varDetailsDiff) reasons.Add($"Vars: {p1.VariableCount}->{p2.VariableCount}");
                if (rawHashDiff && !bodyDiff && !typeDiff && !langDiff && !varCountDiff && !varDetailsDiff)
                    reasons.Add("Raw extension/substructure modified");

                modifiedPous.Add(new OmronPouDiff(
                    Name: name,
                    ChangeSummary: string.Join("; ", reasons),
                    BodyDiff: bodyDiff,
                    VariablesDiff: varCountDiff || varDetailsDiff,
                    RawHashDiff: rawHashDiff));
            }
        }
        modifiedPous = modifiedPous.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // Symbols comparison
        var leftSymbols = left.Symbols.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);
        var rightSymbols = right.Symbols.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);

        var addedSymbols = rightSymbols.Keys.Except(leftSymbols.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var removedSymbols = leftSymbols.Keys.Except(rightSymbols.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var commonSymbols = leftSymbols.Keys.Intersect(rightSymbols.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        var modifiedSymbols = new List<OmronSymbolDiff>();
        foreach (var name in commonSymbols)
        {
            var s1 = leftSymbols[name];
            var s2 = rightSymbols[name];
            if (!string.Equals(s1.DataType, s2.DataType, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(s1.Address, s2.Address, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(s1.Scope, s2.Scope, StringComparison.OrdinalIgnoreCase) ||
                s1.IsConstant != s2.IsConstant ||
                s1.IsRetain != s2.IsRetain)
            {
                modifiedSymbols.Add(new OmronSymbolDiff(
                    Name: name,
                    LeftType: s1.DataType,
                    RightType: s2.DataType,
                    LeftAddress: s1.Address,
                    RightAddress: s2.Address,
                    LeftScope: s1.Scope,
                    RightScope: s2.Scope));
            }
        }
        modifiedSymbols = modifiedSymbols.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // Data types comparison
        var leftDataTypes = left.DataTypes.ToDictionary(d => d.Name, d => d, StringComparer.OrdinalIgnoreCase);
        var rightDataTypes = right.DataTypes.ToDictionary(d => d.Name, d => d, StringComparer.OrdinalIgnoreCase);

        var addedDataTypes = rightDataTypes.Keys.Except(leftDataTypes.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var removedDataTypes = leftDataTypes.Keys.Except(rightDataTypes.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var commonDataTypes = leftDataTypes.Keys.Intersect(rightDataTypes.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        var modifiedDataTypes = new List<string>();
        foreach (var name in commonDataTypes)
        {
            var d1 = leftDataTypes[name];
            var d2 = rightDataTypes[name];
            if (!string.Equals(d1.Kind, d2.Kind, StringComparison.OrdinalIgnoreCase) ||
                d1.MemberCount != d2.MemberCount ||
                !d1.Members.SequenceEqual(d2.Members, StringComparer.Ordinal))
            {
                modifiedDataTypes.Add(name);
            }
        }
        modifiedDataTypes = modifiedDataTypes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        // CAEX objects comparison
        var leftCaex = left.Objects.ToDictionary(o => o.Path, o => o, StringComparer.OrdinalIgnoreCase);
        var rightCaex = right.Objects.ToDictionary(o => o.Path, o => o, StringComparer.OrdinalIgnoreCase);

        var addedCaexObjects = rightCaex.Keys.Except(leftCaex.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var removedCaexObjects = leftCaex.Keys.Except(rightCaex.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var commonCaex = leftCaex.Keys.Intersect(rightCaex.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        var modifiedCaexObjects = new List<string>();
        foreach (var path in commonCaex)
        {
            var o1 = leftCaex[path];
            var o2 = rightCaex[path];
            if (!string.Equals(o1.DeviceType, o2.DeviceType, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(o1.Model, o2.Model, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(o1.FirmwareVersion, o2.FirmwareVersion, StringComparison.OrdinalIgnoreCase) ||
                o1.ChildCount != o2.ChildCount)
            {
                modifiedCaexObjects.Add(path);
            }
        }
        modifiedCaexObjects = modifiedCaexObjects.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        // Raw extension differences (ensures proprietary unsupported extensions are never assumed identical)
        var rawExtensionDiffs = new List<string>();
        foreach (var k in right.RawExtensionHashes.Keys.Except(left.RawExtensionHashes.Keys, StringComparer.OrdinalIgnoreCase))
        {
            rawExtensionDiffs.Add($"+ Extension {k}");
        }
        foreach (var k in left.RawExtensionHashes.Keys.Except(right.RawExtensionHashes.Keys, StringComparer.OrdinalIgnoreCase))
        {
            rawExtensionDiffs.Add($"- Extension {k}");
        }
        foreach (var k in left.RawExtensionHashes.Keys.Intersect(right.RawExtensionHashes.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(left.RawExtensionHashes[k], right.RawExtensionHashes[k], StringComparison.Ordinal))
            {
                rawExtensionDiffs.Add($"~ Extension {k}");
            }
        }
        rawExtensionDiffs = rawExtensionDiffs.OrderBy(x => x, StringComparer.Ordinal).ToList();

        bool hasDifferences = formatMismatch ||
                              addedPous.Count > 0 || removedPous.Count > 0 || modifiedPous.Count > 0 ||
                              addedSymbols.Count > 0 || removedSymbols.Count > 0 || modifiedSymbols.Count > 0 ||
                              addedDataTypes.Count > 0 || removedDataTypes.Count > 0 || modifiedDataTypes.Count > 0 ||
                              addedCaexObjects.Count > 0 || removedCaexObjects.Count > 0 || modifiedCaexObjects.Count > 0 ||
                              rawExtensionDiffs.Count > 0 ||
                              !string.Equals(left.SourceSha256, right.SourceSha256, StringComparison.Ordinal);

        string summary = hasDifferences
            ? $"Diff: Format mismatch={formatMismatch}, POUs [+{addedPous.Count} -{removedPous.Count} ~{modifiedPous.Count}], Symbols [+{addedSymbols.Count} -{removedSymbols.Count} ~{modifiedSymbols.Count}], Types [+{addedDataTypes.Count} -{removedDataTypes.Count} ~{modifiedDataTypes.Count}], CAEX [+{addedCaexObjects.Count} -{removedCaexObjects.Count} ~{modifiedCaexObjects.Count}], Extensions [{rawExtensionDiffs.Count}]."
            : "Identical: No structural or raw content differences found.";

        return new OmronExchangeDiff(
            LeftPath: leftPath,
            RightPath: rightPath,
            LeftSha256: left.SourceSha256,
            RightSha256: right.SourceSha256,
            Format: left.Format,
            HasDifferences: hasDifferences,
            Summary: summary,
            AddedPous: addedPous,
            RemovedPous: removedPous,
            ModifiedPous: modifiedPous,
            AddedSymbols: addedSymbols,
            RemovedSymbols: removedSymbols,
            ModifiedSymbols: modifiedSymbols,
            AddedDataTypes: addedDataTypes,
            RemovedDataTypes: removedDataTypes,
            ModifiedDataTypes: modifiedDataTypes,
            AddedCaexObjects: addedCaexObjects,
            RemovedCaexObjects: removedCaexObjects,
            ModifiedCaexObjects: modifiedCaexObjects,
            RawExtensionDifferences: rawExtensionDiffs);
    }

    #region Private Parsing Implementations

    private static string DetectFormat(XElement root)
    {
        string localName = root.Name.LocalName;
        string ns = root.Name.NamespaceName;

        // 1. IEC 61131-10 XML: <Project> with IEC TC65 namespace or Smc extension
        if (string.Equals(localName, "Project", StringComparison.Ordinal) &&
            (ns.Contains("iec.ch", StringComparison.OrdinalIgnoreCase) ||
             ns.Contains("TC65", StringComparison.OrdinalIgnoreCase) ||
             root.Attributes().Any(a => a.Value.Contains("Smc", StringComparison.OrdinalIgnoreCase) || a.Value.Contains("IEC61131_10", StringComparison.OrdinalIgnoreCase))))
        {
            return "IEC-61131-10-XML";
        }

        // 2. PLCopen XML: <project> with plcopen.org namespace or standard elements
        if (string.Equals(localName, "project", StringComparison.OrdinalIgnoreCase) &&
            (ns.Contains("plcopen.org", StringComparison.OrdinalIgnoreCase) ||
             root.Elements().Any(e => e.Name.LocalName is "types" or "contentHeader" or "fileHeader")))
        {
            return "PLCopen-XML";
        }

        // 3. AutomationML CAEX: <CAEXFile> with CAEX / AutomationML attributes
        if (string.Equals(localName, "CAEXFile", StringComparison.OrdinalIgnoreCase) &&
            (root.Attribute("SchemaVersion") != null ||
             root.Attribute("FileName") != null ||
             ns.Contains("caex", StringComparison.OrdinalIgnoreCase) ||
             root.Elements().Any(e => e.Name.LocalName is "InstanceHierarchy" or "RoleClassLib" or "SystemUnitClassLib" or "AdditionalInformation")))
        {
            return "AutomationML-CAEX";
        }

        throw new InvalidOperationException($"Unrecognized or unsupported root element '<{localName}>' with namespace '{ns}'. Expected IEC 61131-10 (<Project>), PLCopen XML (<project>), or AutomationML (<CAEXFile>).");
    }

    private static void PrecheckXmlLimits(byte[] bytes)
    {
        using var msScan = new MemoryStream(bytes, writable: false);
        using var scanReader = XmlReader.Create(msScan, SafeReaderSettings);

        int nodeCount = 0;
        while (scanReader.Read())
        {
            nodeCount++;
            if (nodeCount > MaxXmlNodeCount)
            {
                throw new InvalidOperationException($"XML node count exceeded maximum limit of {MaxXmlNodeCount}.");
            }
            if (scanReader.Depth > MaxXmlDepth)
            {
                throw new InvalidOperationException($"XML depth {scanReader.Depth} exceeded maximum limit of {MaxXmlDepth}.");
            }
        }
    }

    private string ValidateAndSanitizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty.", nameof(path));

        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException($"Path must be fully qualified: '{path}'", nameof(path));

        var fullPath = Path.GetFullPath(path);

        if (!fullPath.StartsWith(_allowedRootPrefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, _allowedRootNormalized, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Path '{path}' is outside allowed root '{AllowedRoot}'.");
        }

        if (File.Exists(fullPath))
        {
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException($"File '{fullPath}' is a reparse point or symlink.");
            }

            // Verify all directory ancestors up to allowed root
            var currentDir = fileInfo.Directory;
            while (currentDir != null)
            {
                if (currentDir.Exists && currentDir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new UnauthorizedAccessException($"Ancestor directory '{currentDir.FullName}' is a reparse point or symlink.");
                }

                if (string.Equals(Path.TrimEndingDirectorySeparator(currentDir.FullName), _allowedRootNormalized, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                currentDir = currentDir.Parent;
            }
        }

        return fullPath;
    }

    private static OmronExchangeReport ParseIec61131_10(string fullPath, string sourceSha256, long byteLength, XElement root)
    {
        var contentHeader = root.Elements().FirstOrDefault(e => e.Name.LocalName == "ContentHeader");
        var fileHeader = root.Elements().FirstOrDefault(e => e.Name.LocalName == "FileHeader");

        string projectName = contentHeader?.Attribute("name")?.Value
            ?? fileHeader?.Attribute("companyName")?.Value
            ?? Path.GetFileNameWithoutExtension(fullPath);

        // Discover DeviceInfo
        var deviceInfo = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "DeviceInfo");
        string? targetDevice = null;
        if (deviceInfo != null)
        {
            string model = deviceInfo.Attribute("modelName")?.Value ?? "Unknown";
            string version = deviceInfo.Attribute("version")?.Value ?? "";
            targetDevice = string.IsNullOrWhiteSpace(version) ? model : $"{model} (v{version})";
        }

        var typesElem = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Types");
        var pous = new List<OmronPouInfo>();
        var dataTypes = new List<OmronDataTypeInfo>();

        if (typesElem != null)
        {
            // Extract POUs
            var pouElements = typesElem.Descendants()
                .Where(e => e.Name.LocalName is "Program" or "Function" or "FunctionBlock");

            foreach (var pe in pouElements)
            {
                string pouName = pe.Attribute("name")?.Value ?? "UnnamedPou";
                string pouType = pe.Name.LocalName;
                string language = "LD";
                string? bodySummary = null;

                var bodyElem = pe.Elements().FirstOrDefault(e => e.Name.LocalName is "MainBody" or "body" or "Body");
                if (bodyElem != null)
                {
                    var stElem = bodyElem.Descendants().FirstOrDefault(e => e.Name.LocalName is "ST" or "st");
                    if (stElem != null)
                    {
                        language = "ST";
                        string text = stElem.Value.Trim();
                        bodySummary = text.Length > 200 ? text.Substring(0, 200) + "..." : text;
                    }
                    else
                    {
                        var rungs = bodyElem.Descendants().Where(e => e.Name.LocalName == "Rung").ToList();
                        if (rungs.Count > 0)
                        {
                            language = "LD";
                            bodySummary = $"{rungs.Count} LD rung(s)";
                        }
                        else
                        {
                            var firstSection = bodyElem.Elements().FirstOrDefault();
                            language = firstSection?.Attribute("type")?.Value?.Contains("Ld") == true ? "LD" : "Unknown";
                            bodySummary = firstSection?.Name.LocalName ?? "BodyContent";
                        }
                    }
                }

                var pouVars = new List<OmronSymbolInfo>();
                var varGroups = pe.Elements().Where(e => e.Name.LocalName is "ExternalVars" or "Vars" or "InputVars" or "OutputVars" or "InOutVars" or "ReturnVars");
                foreach (var group in varGroups)
                {
                    string scope = group.Name.LocalName switch
                    {
                        "ExternalVars" => "External",
                        "InputVars" => "Input",
                        "OutputVars" => "Output",
                        "InOutVars" => "InOut",
                        "ReturnVars" => "Return",
                        _ => "Private"
                    };

                    bool groupConstant = group.Attribute("constant")?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
                    bool groupRetain = group.Attribute("retain")?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

                    foreach (var varElem in group.Elements().Where(e => e.Name.LocalName == "Variable"))
                    {
                        string vName = varElem.Attribute("name")?.Value ?? "unnamed";
                        string? vAddr = varElem.Elements().FirstOrDefault(e => e.Name.LocalName == "Address")?.Attribute("address")?.Value
                                        ?? varElem.Attribute("address")?.Value;
                        string vType = varElem.Descendants().FirstOrDefault(e => e.Name.LocalName == "TypeName")?.Value
                                       ?? varElem.Elements().FirstOrDefault(e => e.Name.LocalName == "Type")?.Elements().FirstOrDefault()?.Name.LocalName
                                       ?? "UNKNOWN";
                        string? vInit = varElem.Descendants().FirstOrDefault(e => e.Name.LocalName == "SimpleValue")?.Attribute("value")?.Value;
                        string? vComment = varElem.Elements().FirstOrDefault(e => e.Name.LocalName == "Documentation")?.Value?.Trim();
                        bool vConstant = groupConstant || varElem.Attribute("constant")?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
                        bool vRetain = groupRetain || varElem.Attribute("retain")?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

                        pouVars.Add(new OmronSymbolInfo(
                            Name: vName,
                            DataType: vType,
                            Address: vAddr,
                            Scope: scope,
                            InitialValue: vInit,
                            Comment: vComment,
                            IsConstant: vConstant,
                            IsRetain: vRetain));
                    }
                }

                string pouRawHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pe.ToString(SaveOptions.DisableFormatting)))).ToLowerInvariant();

                pous.Add(new OmronPouInfo(
                    Name: pouName,
                    PouType: pouType,
                    Language: language,
                    VariableCount: pouVars.Count,
                    Variables: pouVars,
                    BodySummary: bodySummary,
                    RawContentHash: pouRawHash));
            }

            // Extract Data Types
            foreach (var dt in typesElem.Descendants().Where(e => e.Name.LocalName == "DataTypeDecl"))
            {
                string dtName = dt.Attribute("name")?.Value ?? "UnnamedType";
                var userSpec = dt.Elements().FirstOrDefault(e => e.Name.LocalName == "UserDefinedTypeSpec");
                string kind = "Struct";
                string? baseType = null;
                var members = new List<string>();

                if (userSpec != null)
                {
                    string typeAttr = userSpec.Attribute("type")?.Value ?? "";
                    if (userSpec.Attribute("overlap")?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        kind = "Union";
                    }
                    else if (typeAttr.Contains("Enum", StringComparison.OrdinalIgnoreCase))
                    {
                        kind = "Enum";
                    }

                    baseType = userSpec.Elements().FirstOrDefault(e => e.Name.LocalName == "BaseType")?.Value;

                    foreach (var m in userSpec.Elements().Where(e => e.Name.LocalName == "Member"))
                    {
                        string mName = m.Attribute("name")?.Value ?? "unnamed";
                        string mType = m.Descendants().FirstOrDefault(e => e.Name.LocalName == "TypeName")?.Value ?? "UNKNOWN";
                        members.Add($"{mName}: {mType}");
                    }
                }

                dataTypes.Add(new OmronDataTypeInfo(
                    Name: dtName,
                    Kind: kind,
                    BaseType: baseType,
                    MemberCount: members.Count,
                    Members: members));
            }
        }

        // Global Configuration Variables
        var symbols = new List<OmronSymbolInfo>();
        var configElem = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Configuration");
        if (configElem != null)
        {
            foreach (var gvGroup in configElem.Descendants().Where(e => e.Name.LocalName == "GlobalVars"))
            {
                bool retain = gvGroup.Attribute("retain")?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
                bool constant = gvGroup.Attribute("constant")?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

                foreach (var varElem in gvGroup.Elements().Where(e => e.Name.LocalName == "Variable"))
                {
                    string vName = varElem.Attribute("name")?.Value ?? "unnamed";
                    string? vAddr = varElem.Elements().FirstOrDefault(e => e.Name.LocalName == "Address")?.Attribute("address")?.Value
                                    ?? varElem.Attribute("address")?.Value;
                    string vType = varElem.Descendants().FirstOrDefault(e => e.Name.LocalName == "TypeName")?.Value
                                   ?? varElem.Elements().FirstOrDefault(e => e.Name.LocalName == "Type")?.Elements().FirstOrDefault()?.Name.LocalName
                                   ?? "UNKNOWN";
                    string? vInit = varElem.Descendants().FirstOrDefault(e => e.Name.LocalName == "SimpleValue")?.Attribute("value")?.Value;
                    string? vComment = varElem.Elements().FirstOrDefault(e => e.Name.LocalName == "Documentation")?.Value?.Trim();

                    symbols.Add(new OmronSymbolInfo(
                        Name: vName,
                        DataType: vType,
                        Address: vAddr,
                        Scope: "Configuration",
                        InitialValue: vInit,
                        Comment: vComment,
                        IsConstant: constant || varElem.Attribute("constant")?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true,
                        IsRetain: retain || varElem.Attribute("retain")?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) == true));
                }
            }
        }

        // Capture raw extension hashes for AddData elements
        var rawExtHashes = new Dictionary<string, string>();
        foreach (var addData in root.Descendants().Where(e => e.Name.LocalName == "AddData"))
        {
            foreach (var child in addData.Elements())
            {
                string key = child.Name.LocalName;
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(child.ToString(SaveOptions.DisableFormatting)))).ToLowerInvariant();
                rawExtHashes[key] = hash;
            }
        }

        var limitations = new List<string>
        {
            "IEC 61131-10 XML inspection is structural only and does not execute vendor cross-compilation (nexcc).",
            "Complex graphical ladder network rung topologies are captured as summaries and raw content hashes.",
            "Proprietary Omron smcext extensions are hashed for non-tampering verification but not evaluated by Sysmac runtime."
        };

        var diagnostics = new List<OmronDiagnosticInfo>
        {
            new("Info", "OMR_001", $"Structural inspection completed: {pous.Count} POU(s), {symbols.Count} configuration symbol(s), {dataTypes.Count} data type(s).")
        };

        return new OmronExchangeReport(
            SourcePath: fullPath,
            SourceSha256: sourceSha256,
            ByteLength: byteLength,
            Format: "IEC-61131-10-XML",
            ProjectName: projectName,
            TargetDevice: targetDevice,
            Pous: pous,
            Symbols: symbols,
            DataTypes: dataTypes,
            Objects: Array.Empty<OmronCaexObjectInfo>(),
            Diagnostics: diagnostics,
            Limitations: limitations,
            RawExtensionHashes: rawExtHashes);
    }

    private static OmronExchangeReport ParsePlcopen(string fullPath, string sourceSha256, long byteLength, XElement root)
    {
        var contentHeader = root.Elements().FirstOrDefault(e => e.Name.LocalName == "contentHeader");
        var fileHeader = root.Elements().FirstOrDefault(e => e.Name.LocalName == "fileHeader");

        string projectName = contentHeader?.Attribute("name")?.Value
            ?? fileHeader?.Attribute("companyName")?.Value
            ?? Path.GetFileNameWithoutExtension(fullPath);

        var pous = new List<OmronPouInfo>();
        var symbols = new List<OmronSymbolInfo>();
        var dataTypes = new List<OmronDataTypeInfo>();

        var typesElem = root.Elements().FirstOrDefault(e => e.Name.LocalName == "types");
        if (typesElem != null)
        {
            // Global variables
            foreach (var gv in typesElem.Descendants().Where(e => e.Name.LocalName is "globalVars" or "variable"))
            {
                if (gv.Name.LocalName == "variable" && gv.Parent?.Name.LocalName == "globalVars")
                {
                    string vName = gv.Attribute("name")?.Value ?? "unnamed";
                    string? vAddr = gv.Attribute("address")?.Value;
                    string vType = gv.Elements().FirstOrDefault(e => e.Name.LocalName == "type")?.Elements().FirstOrDefault()?.Name.LocalName ?? "UNKNOWN";
                    symbols.Add(new OmronSymbolInfo(
                        Name: vName,
                        DataType: vType,
                        Address: vAddr,
                        Scope: "Global"));
                }
            }

            // POUs
            var pousElem = typesElem.Elements().FirstOrDefault(e => e.Name.LocalName == "pous");
            if (pousElem != null)
            {
                foreach (var pe in pousElem.Elements().Where(e => e.Name.LocalName == "pou"))
                {
                    string pouName = pe.Attribute("name")?.Value ?? "Unnamed";
                    string pouType = pe.Attribute("pouType")?.Value ?? "program";
                    string lang = "ST";

                    var body = pe.Elements().FirstOrDefault(e => e.Name.LocalName == "body");
                    string? bodySummary = null;
                    if (body != null)
                    {
                        var firstChild = body.Elements().FirstOrDefault();
                        lang = firstChild?.Name.LocalName ?? "ST";
                        string text = body.Value.Trim();
                        bodySummary = text.Length > 200 ? text.Substring(0, 200) + "..." : text;
                    }

                    var pouVars = new List<OmronSymbolInfo>();
                    var iface = pe.Elements().FirstOrDefault(e => e.Name.LocalName == "interface");
                    if (iface != null)
                    {
                        foreach (var v in iface.Descendants().Where(e => e.Name.LocalName == "variable"))
                        {
                            string vName = v.Attribute("name")?.Value ?? "unnamed";
                            string vType = v.Elements().FirstOrDefault(e => e.Name.LocalName == "type")?.Elements().FirstOrDefault()?.Name.LocalName ?? "UNKNOWN";
                            pouVars.Add(new OmronSymbolInfo(vName, vType, null, Scope: "Local"));
                        }
                    }

                    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pe.ToString(SaveOptions.DisableFormatting)))).ToLowerInvariant();
                    pous.Add(new OmronPouInfo(pouName, pouType, lang, pouVars.Count, pouVars, bodySummary, hash));
                }
            }
        }

        var limitations = new List<string>
        {
            "Standard PLCopen XML extraction; no vendor-specific execution."
        };

        var diagnostics = new List<OmronDiagnosticInfo>
        {
            new("Info", "OMR_002", $"Parsed PLCopen XML: {pous.Count} POU(s), {symbols.Count} global symbol(s).")
        };

        return new OmronExchangeReport(
            SourcePath: fullPath,
            SourceSha256: sourceSha256,
            ByteLength: byteLength,
            Format: "PLCopen-XML",
            ProjectName: projectName,
            TargetDevice: null,
            Pous: pous,
            Symbols: symbols,
            DataTypes: dataTypes,
            Objects: Array.Empty<OmronCaexObjectInfo>(),
            Diagnostics: diagnostics,
            Limitations: limitations,
            RawExtensionHashes: new Dictionary<string, string>());
    }

    private static OmronExchangeReport ParseAutomationMl(string fullPath, string sourceSha256, long byteLength, XElement root)
    {
        string projectName = root.Attribute("FileName")?.Value
            ?? root.Descendants().FirstOrDefault(e => e.Name.LocalName == "WriterProjectTitle")?.Value
            ?? Path.GetFileNameWithoutExtension(fullPath);

        var objects = new List<OmronCaexObjectInfo>();
        string? targetDevice = null;

        // Traverse InstanceHierarchy
        foreach (var hierarchy in root.Elements().Where(e => e.Name.LocalName == "InstanceHierarchy"))
        {
            string hierName = hierarchy.Attribute("Name")?.Value ?? "Hierarchy";
            TraverseInternalElements(hierarchy, hierName, objects, ref targetDevice);
        }

        var limitations = new List<string>
        {
            "AutomationML CAEX hierarchy inspection represents topology only and contains no executable logic."
        };

        var diagnostics = new List<OmronDiagnosticInfo>
        {
            new("Info", "OMR_003", $"Parsed AutomationML CAEX: {objects.Count} topological element(s) extracted.")
        };

        return new OmronExchangeReport(
            SourcePath: fullPath,
            SourceSha256: sourceSha256,
            ByteLength: byteLength,
            Format: "AutomationML-CAEX",
            ProjectName: projectName,
            TargetDevice: targetDevice,
            Pous: Array.Empty<OmronPouInfo>(),
            Symbols: Array.Empty<OmronSymbolInfo>(),
            DataTypes: Array.Empty<OmronDataTypeInfo>(),
            Objects: objects,
            Diagnostics: diagnostics,
            Limitations: limitations,
            RawExtensionHashes: new Dictionary<string, string>());
    }

    private static void TraverseInternalElements(XElement parent, string currentPath, List<OmronCaexObjectInfo> objects, ref string? targetDevice)
    {
        foreach (var ie in parent.Elements().Where(e => e.Name.LocalName == "InternalElement"))
        {
            string id = ie.Attribute("ID")?.Value ?? Guid.NewGuid().ToString("D");
            string name = ie.Attribute("Name")?.Value ?? "UnnamedElement";
            string path = $"{currentPath}/{name}";

            string? devType = null;
            string? mfg = null;
            string? model = null;
            string? fw = null;

            foreach (var attr in ie.Elements().Where(e => e.Name.LocalName == "Attribute"))
            {
                string aName = attr.Attribute("Name")?.Value ?? "";
                string aVal = attr.Elements().FirstOrDefault(e => e.Name.LocalName == "Value")?.Value ?? "";

                if (string.Equals(aName, "DeviceItemType", StringComparison.OrdinalIgnoreCase)) devType = aVal;
                else if (string.Equals(aName, "Manufacturer", StringComparison.OrdinalIgnoreCase)) mfg = aVal;
                else if (string.Equals(aName, "FirmwareVersion", StringComparison.OrdinalIgnoreCase)) fw = aVal;
                else if (string.Equals(aName, "TypeIdentifier", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(aName, "Model", StringComparison.OrdinalIgnoreCase))
                {
                    model = aVal.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase) ? aVal.Substring(12) : aVal;
                }
            }

            var children = ie.Elements().Where(e => e.Name.LocalName == "InternalElement").ToList();

            if (targetDevice == null && devType?.Contains("CPU", StringComparison.OrdinalIgnoreCase) == true && model != null)
            {
                targetDevice = string.IsNullOrWhiteSpace(fw) ? model : $"{model} (FW {fw})";
            }

            objects.Add(new OmronCaexObjectInfo(
                Id: id,
                Name: name,
                Path: path,
                DeviceType: devType,
                Manufacturer: mfg,
                Model: model,
                FirmwareVersion: fw,
                ChildCount: children.Count));

            TraverseInternalElements(ie, path, objects, ref targetDevice);
        }
    }

    #endregion
}
