using System.Text.Json.Serialization;

namespace PlcMcp.Engineering.Models;

public sealed record ProjectSnapshot(
    [property: JsonPropertyName("sourcePath")] string SourcePath,
    [property: JsonPropertyName("workingPath")] string WorkingPath,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("byteLength")] long ByteLength,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("readOnly")] bool ReadOnly = true);

public sealed record ProjectMetadata(
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("fileFormat")] string FileFormat,
    [property: JsonPropertyName("version")] string? Version = null,
    [property: JsonPropertyName("author")] string? Author = null,
    [property: JsonPropertyName("pouCount")] int PouCount = 0,
    [property: JsonPropertyName("symbolCount")] int SymbolCount = 0);

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record StaticDiagnostic(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("severity")] DiagnosticSeverity Severity,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("column")] int Column,
    [property: JsonPropertyName("ruleName")] string RuleName);

public sealed record StaticCheckResult(
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<StaticDiagnostic> Diagnostics,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("isVendorCompiler")] bool IsVendorCompiler = false,
    [property: JsonPropertyName("disclaimer")] string Disclaimer = "Static heuristic precheck only. Not a vendor compiler verification.");

public sealed record EngineeringSymbol(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("address")] string? Address,
    [property: JsonPropertyName("dataType")] string DataType,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("scope")] string Scope = "Global");

public enum PouType
{
    Program,
    Function,
    FunctionBlock
}

public sealed record EngineeringPou(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("pouType")] PouType PouType,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("bodyText")] string? BodyText,
    [property: JsonPropertyName("variables")] IReadOnlyList<EngineeringSymbol> Variables);

public sealed record ParsedProject(
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("symbols")] IReadOnlyList<EngineeringSymbol> Symbols,
    [property: JsonPropertyName("pous")] IReadOnlyList<EngineeringPou> Pous,
    [property: JsonPropertyName("sourceSha256")] string SourceSha256);

public sealed record ProjectDiff(
    [property: JsonPropertyName("addedPous")] IReadOnlyList<string> AddedPous,
    [property: JsonPropertyName("removedPous")] IReadOnlyList<string> RemovedPous,
    [property: JsonPropertyName("modifiedPous")] IReadOnlyList<string> ModifiedPous,
    [property: JsonPropertyName("addedSymbols")] IReadOnlyList<string> AddedSymbols,
    [property: JsonPropertyName("removedSymbols")] IReadOnlyList<string> RemovedSymbols,
    [property: JsonPropertyName("modifiedSymbols")] IReadOnlyList<string> ModifiedSymbols,
    [property: JsonPropertyName("hasDifferences")] bool HasDifferences,
    [property: JsonPropertyName("summary")] string Summary);
