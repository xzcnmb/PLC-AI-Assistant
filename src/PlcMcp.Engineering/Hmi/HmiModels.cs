using System.Text.Json.Serialization;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Models;

namespace PlcMcp.Engineering.Hmi;

public enum HmiAccessRight
{
    ReadOnly,
    WriteOnly,
    ReadWrite
}

public enum HmiAlarmSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

public sealed record HmiScreenDefinition(
    [property: JsonPropertyName("screenId")] string ScreenId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("boundTagNames")] IReadOnlyList<string>? BoundTagNames = null);

public sealed record HmiTagBinding(
    [property: JsonPropertyName("hmiTagName")] string HmiTagName,
    [property: JsonPropertyName("plcSymbolOrAddress")] string PlcSymbolOrAddress,
    [property: JsonPropertyName("accessRight")] HmiAccessRight AccessRight,
    [property: JsonPropertyName("expectedDataType")] PlcDataType ExpectedDataType,
    [property: JsonPropertyName("expectedUnit")] string? ExpectedUnit = null,
    [property: JsonPropertyName("screenIds")] IReadOnlyList<string>? ScreenIds = null,
    [property: JsonPropertyName("description")] string? Description = null);

public sealed record HmiAlarmEntry(
    [property: JsonPropertyName("alarmId")] string AlarmId,
    [property: JsonPropertyName("triggerTag")] string TriggerTag,
    [property: JsonPropertyName("severity")] HmiAlarmSeverity Severity,
    [property: JsonPropertyName("messageKey")] string MessageKey,
    [property: JsonPropertyName("screenId")] string? ScreenId = null,
    [property: JsonPropertyName("highLimit")] double? HighLimit = null,
    [property: JsonPropertyName("lowLimit")] double? LowLimit = null);

public sealed record HmiRecipeParameter(
    [property: JsonPropertyName("parameterName")] string ParameterName,
    [property: JsonPropertyName("boundTag")] string BoundTag,
    [property: JsonPropertyName("dataType")] PlcDataType DataType,
    [property: JsonPropertyName("minValue")] double MinValue,
    [property: JsonPropertyName("maxValue")] double MaxValue,
    [property: JsonPropertyName("defaultValue")] double? DefaultValue = null,
    [property: JsonPropertyName("unit")] string? Unit = null);

public sealed record HmiRecipeDefinition(
    [property: JsonPropertyName("recipeId")] string RecipeId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("parameters")] IReadOnlyList<HmiRecipeParameter> Parameters);

public sealed record HmiLocalizationEntry(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("translations")] IReadOnlyDictionary<string, string> Translations);

public sealed record HmiManifest(
    [property: JsonPropertyName("manifestVersion")] string ManifestVersion,
    [property: JsonPropertyName("hmiProjectName")] string HmiProjectName,
    [property: JsonPropertyName("targetVendor")] PlcVendor TargetVendor,
    [property: JsonPropertyName("defaultCulture")] string DefaultCulture,
    [property: JsonPropertyName("supportedCultures")] IReadOnlyList<string> SupportedCultures,
    [property: JsonPropertyName("screens")] IReadOnlyList<HmiScreenDefinition> Screens,
    [property: JsonPropertyName("tagBindings")] IReadOnlyList<HmiTagBinding> TagBindings,
    [property: JsonPropertyName("alarms")] IReadOnlyList<HmiAlarmEntry> Alarms,
    [property: JsonPropertyName("recipes")] IReadOnlyList<HmiRecipeDefinition> Recipes,
    [property: JsonPropertyName("localizations")] IReadOnlyList<HmiLocalizationEntry> Localizations);

public sealed record HmiValidationError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("component")] string Component,
    [property: JsonPropertyName("identifier")] string Identifier,
    [property: JsonPropertyName("message")] string Message);

public sealed record HmiValidationResult(
    [property: JsonPropertyName("isValid")] bool IsValid,
    [property: JsonPropertyName("errors")] IReadOnlyList<HmiValidationError> Errors,
    [property: JsonPropertyName("tagBindingCount")] int TagBindingCount,
    [property: JsonPropertyName("screenCount")] int ScreenCount,
    [property: JsonPropertyName("alarmCount")] int AlarmCount,
    [property: JsonPropertyName("recipeCount")] int RecipeCount,
    [property: JsonPropertyName("localizationCount")] int LocalizationCount,
    [property: JsonPropertyName("summary")] string Summary);

public sealed record HmiArtifactFile(
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("byteLength")] long ByteLength,
    [property: JsonPropertyName("format")] string Format);

public sealed record HmiArtifactPackage(
    [property: JsonPropertyName("manifestVersion")] string ManifestVersion,
    [property: JsonPropertyName("hmiProjectName")] string HmiProjectName,
    [property: JsonPropertyName("targetVendor")] PlcVendor TargetVendor,
    [property: JsonPropertyName("outputDirectory")] string OutputDirectory,
    [property: JsonPropertyName("files")] IReadOnlyList<HmiArtifactFile> Files,
    [property: JsonPropertyName("manifestSha256")] string ManifestSha256,
    [property: JsonPropertyName("packageSummarySha256")] string PackageSummarySha256,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("disclaimer")] string Disclaimer = "Vendor-neutral offline HMI configuration artifact. Does not pretend to be proprietary WinCC/GOT/NA native binary project.");

public sealed record HmiDiffItem(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("changeType")] string ChangeType,
    [property: JsonPropertyName("identifier")] string Identifier,
    [property: JsonPropertyName("details")] string Details);

public sealed record HmiDiffReport(
    [property: JsonPropertyName("hasDifferences")] bool HasDifferences,
    [property: JsonPropertyName("baseSha256")] string? BaseSha256,
    [property: JsonPropertyName("targetSha256")] string TargetSha256,
    [property: JsonPropertyName("diffs")] IReadOnlyList<HmiDiffItem> Diffs,
    [property: JsonPropertyName("summary")] string Summary);
