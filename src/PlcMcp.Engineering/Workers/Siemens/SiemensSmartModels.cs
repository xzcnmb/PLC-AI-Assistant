using System.Text.Json.Serialization;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Models;

namespace PlcMcp.Engineering.Workers.Siemens;

public sealed record SiemensSmartBridgeConfig(
    [property: JsonPropertyName("pythonExecutablePath")] string? PythonExecutablePath = null,
    [property: JsonPropertyName("smart200PackagePath")] string? Smart200PackagePath = null,
    [property: JsonPropertyName("stepTimeoutSeconds")] int StepTimeoutSeconds = 120,
    [property: JsonPropertyName("allowedWorkspaceRoots")] IReadOnlyList<string>? AllowedWorkspaceRoots = null);

public sealed record SmartProjectOverviewResult(
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("internalVersion")] string? InternalVersion,
    [property: JsonPropertyName("projectName")] string? ProjectName,
    [property: JsonPropertyName("decompressedBytes")] long DecompressedBytes,
    [property: JsonPropertyName("systemTableFound")] bool SystemTableFound,
    [property: JsonPropertyName("symbolCount")] int SymbolCount,
    [property: JsonPropertyName("pouNames")] IReadOnlyList<string> PouNames,
    [property: JsonPropertyName("functionBlockKinds")] int FunctionBlockKinds,
    [property: JsonPropertyName("functionBlockTotal")] int FunctionBlockTotal,
    [property: JsonPropertyName("topFunctionBlocks")] IReadOnlyList<IReadOnlyList<object>> TopFunctionBlocks,
    [property: JsonPropertyName("limitations")] string Limitations);

public sealed record SmartValidationBlockReport(
    [property: JsonPropertyName("blockName")] string BlockName,
    [property: JsonPropertyName("totalNetworks")] int? TotalNetworks,
    [property: JsonPropertyName("invalidNetworks")] IReadOnlyList<int> InvalidNetworks,
    [property: JsonPropertyName("error")] string? Error);

public sealed record SmartValidationResult(
    [property: JsonPropertyName("allValid")] bool AllValid,
    [property: JsonPropertyName("completed")] bool Completed,
    [property: JsonPropertyName("blocks")] IReadOnlyDictionary<string, SmartValidationBlockReport> Blocks,
    [property: JsonPropertyName("disclaimer")] string Disclaimer = "Validated against Siemens POU_IsValidNet engine logic.");

public sealed record SmartCompileVerifyResult(
    [property: JsonPropertyName("stage1StructurePassed")] bool Stage1StructurePassed,
    [property: JsonPropertyName("stage2CompilePassed")] bool Stage2CompilePassed,
    [property: JsonPropertyName("stage3EngineValidatePassed")] bool Stage3EngineValidatePassed,
    [property: JsonPropertyName("stage4RoundtripPassed")] bool Stage4RoundtripPassed,
    [property: JsonPropertyName("stage5PersistedPassed")] bool Stage5PersistedPassed,
    [property: JsonPropertyName("overallPassed")] bool OverallPassed,
    [property: JsonPropertyName("workingPath")] string WorkingPath,
    [property: JsonPropertyName("sourceSha256Before")] string SourceSha256Before,
    [property: JsonPropertyName("sourceSha256After")] string SourceSha256After,
    [property: JsonPropertyName("workingSha256Before")] string WorkingSha256Before,
    [property: JsonPropertyName("workingSha256After")] string WorkingSha256After,
    [property: JsonPropertyName("details")] IReadOnlyDictionary<string, object?> Details);
