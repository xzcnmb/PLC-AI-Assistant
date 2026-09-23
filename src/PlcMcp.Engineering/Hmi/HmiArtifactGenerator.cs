using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Models;
using PlcMcp.Engineering.Workspace;

namespace PlcMcp.Engineering.Hmi;

public interface IHmiArtifactGenerator
{
    HmiArtifactPackage Generate(
        HmiManifest manifest,
        string targetDirectory,
        IEnumerable<TagDefinition>? tagDefinitions = null,
        IEnumerable<EngineeringSymbol>? engineeringSymbols = null,
        bool requirePlcDefinitions = true,
        string? allowedRootDirectory = null);

    HmiDiffReport Compare(HmiManifest baseManifest, HmiManifest targetManifest);
}

public sealed class HmiArtifactGenerator : IHmiArtifactGenerator
{
    private readonly IHmiValidator _validator;
    private readonly string? _defaultAllowedRoot;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public HmiArtifactGenerator(IHmiValidator? validator = null, string? defaultAllowedRoot = null)
    {
        _validator = validator ?? new HmiValidator();
        _defaultAllowedRoot = defaultAllowedRoot;
    }

    public HmiArtifactPackage Generate(
        HmiManifest manifest,
        string targetDirectory,
        IEnumerable<TagDefinition>? tagDefinitions = null,
        IEnumerable<EngineeringSymbol>? engineeringSymbols = null,
        bool requirePlcDefinitions = true,
        string? allowedRootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new ArgumentException("Target directory cannot be empty.", nameof(targetDirectory));

        // 1. Strict Validation
        var validation = _validator.Validate(manifest, tagDefinitions, engineeringSymbols, requirePlcDefinitions);
        if (!validation.IsValid)
        {
            throw new HmiValidationException(validation);
        }

        // 2. Sandboxed output directory handling
        var allowedRoot = allowedRootDirectory ?? _defaultAllowedRoot;
        string fullTarget;
        if (!string.IsNullOrWhiteSpace(allowedRoot))
        {
            fullTarget = ProjectWorkspaceManager.SanitizePath(targetDirectory, allowedRoot);
        }
        else
        {
            fullTarget = Path.GetFullPath(targetDirectory);
            if (!Path.IsPathFullyQualified(targetDirectory))
            {
                throw new UnauthorizedAccessException($"Target directory '{targetDirectory}' must be a fully qualified path when no allowed root is specified.");
            }
        }

        if (Directory.Exists(fullTarget) && Directory.EnumerateFileSystemEntries(fullTarget).Any())
        {
            throw new IOException($"Target directory '{fullTarget}' must be empty; existing files will never be overwritten.");
        }
        Directory.CreateDirectory(fullTarget);

        var generatedFiles = new List<HmiArtifactFile>();

        // 3. Output files:
        // A. manifest.json
        var manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
        var manifestFile = WriteArtifactFile(fullTarget, "manifest.json", manifestJson, "json");
        generatedFiles.Add(manifestFile);

        // B. tags.json & tags.csv
        var tagsJson = JsonSerializer.Serialize(manifest.TagBindings, JsonOptions);
        generatedFiles.Add(WriteArtifactFile(fullTarget, "tags.json", tagsJson, "json"));

        var tagsCsv = GenerateTagsCsv(manifest.TagBindings);
        generatedFiles.Add(WriteArtifactFile(fullTarget, "tags.csv", tagsCsv, "csv"));

        // C. screens.json & screens.csv
        var screensJson = JsonSerializer.Serialize(manifest.Screens, JsonOptions);
        generatedFiles.Add(WriteArtifactFile(fullTarget, "screens.json", screensJson, "json"));

        var screensCsv = GenerateScreensCsv(manifest.Screens);
        generatedFiles.Add(WriteArtifactFile(fullTarget, "screens.csv", screensCsv, "csv"));

        // D. alarms.json & alarms.csv
        var alarmsJson = JsonSerializer.Serialize(manifest.Alarms, JsonOptions);
        generatedFiles.Add(WriteArtifactFile(fullTarget, "alarms.json", alarmsJson, "json"));

        var alarmsCsv = GenerateAlarmsCsv(manifest.Alarms);
        generatedFiles.Add(WriteArtifactFile(fullTarget, "alarms.csv", alarmsCsv, "csv"));

        // E. recipes.json & recipes.csv
        var recipesJson = JsonSerializer.Serialize(manifest.Recipes, JsonOptions);
        generatedFiles.Add(WriteArtifactFile(fullTarget, "recipes.json", recipesJson, "json"));

        var recipesCsv = GenerateRecipesCsv(manifest.Recipes);
        generatedFiles.Add(WriteArtifactFile(fullTarget, "recipes.csv", recipesCsv, "csv"));

        // F. localizations.json & localizations.csv
        var locJson = JsonSerializer.Serialize(manifest.Localizations, JsonOptions);
        generatedFiles.Add(WriteArtifactFile(fullTarget, "localizations.json", locJson, "json"));

        var locCsv = GenerateLocalizationsCsv(manifest.Localizations, manifest.SupportedCultures);
        generatedFiles.Add(WriteArtifactFile(fullTarget, "localizations.csv", locCsv, "csv"));

        // 4. Compute package summary hash
        var combinedSummary = string.Join("\n", generatedFiles.OrderBy(f => f.RelativePath).Select(f => $"{f.RelativePath}:{f.Sha256}:{f.ByteLength}"));
        var packageSummaryHash = ComputeSha256(Encoding.UTF8.GetBytes(combinedSummary));

        var package = new HmiArtifactPackage(
            ManifestVersion: manifest.ManifestVersion,
            HmiProjectName: manifest.HmiProjectName,
            TargetVendor: manifest.TargetVendor,
            OutputDirectory: fullTarget,
            Files: generatedFiles,
            ManifestSha256: manifestFile.Sha256,
            PackageSummarySha256: packageSummaryHash,
            CreatedAt: DateTimeOffset.UtcNow);

        // G. package.json metadata
        var packageJson = JsonSerializer.Serialize(package, JsonOptions);
        WriteArtifactFile(fullTarget, "package.json", packageJson, "json");

        return package;
    }

    public HmiDiffReport Compare(HmiManifest baseManifest, HmiManifest targetManifest)
    {
        ArgumentNullException.ThrowIfNull(targetManifest);

        string? baseSha = null;
        if (baseManifest != null)
        {
            var baseBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(baseManifest, JsonOptions));
            baseSha = ComputeSha256(baseBytes);
        }

        var targetBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(targetManifest, JsonOptions));
        var targetSha = ComputeSha256(targetBytes);

        var diffs = new List<HmiDiffItem>();

        if (baseManifest == null)
        {
            diffs.Add(new HmiDiffItem("Manifest", "Added", targetManifest.HmiProjectName, "Full initial HMI manifest."));
            return new HmiDiffReport(
                HasDifferences: true,
                BaseSha256: null,
                TargetSha256: targetSha,
                Diffs: diffs,
                Summary: $"Target manifest has {diffs.Count} difference(s) compared to empty base.");
        }

        // Compare project level
        if (!string.Equals(baseManifest.HmiProjectName, targetManifest.HmiProjectName, StringComparison.Ordinal))
        {
            diffs.Add(new HmiDiffItem("Project", "Modified", "HmiProjectName",
                $"Changed from '{baseManifest.HmiProjectName}' to '{targetManifest.HmiProjectName}'."));
        }
        if (baseManifest.TargetVendor != targetManifest.TargetVendor)
        {
            diffs.Add(new HmiDiffItem("Project", "Modified", "TargetVendor",
                $"Changed from '{baseManifest.TargetVendor}' to '{targetManifest.TargetVendor}'."));
        }
        if (!string.Equals(baseManifest.ManifestVersion, targetManifest.ManifestVersion, StringComparison.Ordinal))
        {
            diffs.Add(new HmiDiffItem("Project", "Modified", "ManifestVersion",
                $"Changed from '{baseManifest.ManifestVersion}' to '{targetManifest.ManifestVersion}'."));
        }

        // Compare Screens
        CompareScreens(baseManifest.Screens, targetManifest.Screens, diffs);

        // Compare TagBindings
        CompareTagBindings(baseManifest.TagBindings, targetManifest.TagBindings, diffs);

        // Compare Alarms
        CompareAlarms(baseManifest.Alarms, targetManifest.Alarms, diffs);

        // Compare Recipes
        CompareRecipes(baseManifest.Recipes, targetManifest.Recipes, diffs);

        // Compare Localizations
        CompareLocalizations(baseManifest.Localizations, targetManifest.Localizations, diffs);

        bool hasDifferences = diffs.Count > 0;
        string summary = hasDifferences
            ? $"HMI manifest comparison detected {diffs.Count} change(s)."
            : "No differences found between HMI manifests.";

        return new HmiDiffReport(
            HasDifferences: hasDifferences,
            BaseSha256: baseSha,
            TargetSha256: targetSha,
            Diffs: diffs,
            Summary: summary);
    }

    private static void CompareScreens(
        IReadOnlyList<HmiScreenDefinition>? baseList,
        IReadOnlyList<HmiScreenDefinition>? targetList,
        List<HmiDiffItem> diffs)
    {
        var bMap = (baseList ?? Array.Empty<HmiScreenDefinition>()).ToDictionary(x => x.ScreenId, StringComparer.OrdinalIgnoreCase);
        var tMap = (targetList ?? Array.Empty<HmiScreenDefinition>()).ToDictionary(x => x.ScreenId, StringComparer.OrdinalIgnoreCase);

        foreach (var added in tMap.Keys.Except(bMap.Keys, StringComparer.OrdinalIgnoreCase))
        {
            diffs.Add(new HmiDiffItem("Screen", "Added", added, $"Screen '{added}' added."));
        }
        foreach (var removed in bMap.Keys.Except(tMap.Keys, StringComparer.OrdinalIgnoreCase))
        {
            diffs.Add(new HmiDiffItem("Screen", "Removed", removed, $"Screen '{removed}' removed."));
        }
        foreach (var common in tMap.Keys.Intersect(bMap.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var b = bMap[common];
            var t = tMap[common];
            var changes = new List<string>();
            if (b.Name != t.Name) changes.Add($"Name: '{b.Name}' -> '{t.Name}'");
            if (b.Description != t.Description) changes.Add($"Description: '{b.Description}' -> '{t.Description}'");
            var bTags = string.Join(",", (b.BoundTagNames ?? Array.Empty<string>()).OrderBy(x => x));
            var tTags = string.Join(",", (t.BoundTagNames ?? Array.Empty<string>()).OrderBy(x => x));
            if (bTags != tTags) changes.Add($"BoundTagNames: [{bTags}] -> [{tTags}]");

            if (changes.Count > 0)
            {
                diffs.Add(new HmiDiffItem("Screen", "Modified", common, string.Join("; ", changes)));
            }
        }
    }

    private static void CompareTagBindings(
        IReadOnlyList<HmiTagBinding>? baseList,
        IReadOnlyList<HmiTagBinding>? targetList,
        List<HmiDiffItem> diffs)
    {
        var bMap = (baseList ?? Array.Empty<HmiTagBinding>()).ToDictionary(x => x.HmiTagName, StringComparer.OrdinalIgnoreCase);
        var tMap = (targetList ?? Array.Empty<HmiTagBinding>()).ToDictionary(x => x.HmiTagName, StringComparer.OrdinalIgnoreCase);

        foreach (var added in tMap.Keys.Except(bMap.Keys, StringComparer.OrdinalIgnoreCase))
        {
            diffs.Add(new HmiDiffItem("TagBinding", "Added", added, $"Tag '{added}' bound to '{tMap[added].PlcSymbolOrAddress}'."));
        }
        foreach (var removed in bMap.Keys.Except(tMap.Keys, StringComparer.OrdinalIgnoreCase))
        {
            diffs.Add(new HmiDiffItem("TagBinding", "Removed", removed, $"Tag '{removed}' removed."));
        }
        foreach (var common in tMap.Keys.Intersect(bMap.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var b = bMap[common];
            var t = tMap[common];
            var changes = new List<string>();
            if (b.PlcSymbolOrAddress != t.PlcSymbolOrAddress) changes.Add($"PlcSymbolOrAddress: '{b.PlcSymbolOrAddress}' -> '{t.PlcSymbolOrAddress}'");
            if (b.AccessRight != t.AccessRight) changes.Add($"AccessRight: {b.AccessRight} -> {t.AccessRight}");
            if (b.ExpectedDataType != t.ExpectedDataType) changes.Add($"ExpectedDataType: {b.ExpectedDataType} -> {t.ExpectedDataType}");
            if (b.ExpectedUnit != t.ExpectedUnit) changes.Add($"ExpectedUnit: '{b.ExpectedUnit}' -> '{t.ExpectedUnit}'");

            if (changes.Count > 0)
            {
                diffs.Add(new HmiDiffItem("TagBinding", "Modified", common, string.Join("; ", changes)));
            }
        }
    }

    private static void CompareAlarms(
        IReadOnlyList<HmiAlarmEntry>? baseList,
        IReadOnlyList<HmiAlarmEntry>? targetList,
        List<HmiDiffItem> diffs)
    {
        var bMap = (baseList ?? Array.Empty<HmiAlarmEntry>()).ToDictionary(x => x.AlarmId, StringComparer.OrdinalIgnoreCase);
        var tMap = (targetList ?? Array.Empty<HmiAlarmEntry>()).ToDictionary(x => x.AlarmId, StringComparer.OrdinalIgnoreCase);

        foreach (var added in tMap.Keys.Except(bMap.Keys, StringComparer.OrdinalIgnoreCase))
        {
            diffs.Add(new HmiDiffItem("Alarm", "Added", added, $"Alarm '{added}' added."));
        }
        foreach (var removed in bMap.Keys.Except(tMap.Keys, StringComparer.OrdinalIgnoreCase))
        {
            diffs.Add(new HmiDiffItem("Alarm", "Removed", removed, $"Alarm '{removed}' removed."));
        }
        foreach (var common in tMap.Keys.Intersect(bMap.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var b = bMap[common];
            var t = tMap[common];
            var changes = new List<string>();
            if (b.TriggerTag != t.TriggerTag) changes.Add($"TriggerTag: '{b.TriggerTag}' -> '{t.TriggerTag}'");
            if (b.Severity != t.Severity) changes.Add($"Severity: {b.Severity} -> {t.Severity}");
            if (b.MessageKey != t.MessageKey) changes.Add($"MessageKey: '{b.MessageKey}' -> '{t.MessageKey}'");
            if (b.HighLimit != t.HighLimit) changes.Add($"HighLimit: {b.HighLimit} -> {t.HighLimit}");
            if (b.LowLimit != t.LowLimit) changes.Add($"LowLimit: {b.LowLimit} -> {t.LowLimit}");

            if (changes.Count > 0)
            {
                diffs.Add(new HmiDiffItem("Alarm", "Modified", common, string.Join("; ", changes)));
            }
        }
    }

    private static void CompareRecipes(
        IReadOnlyList<HmiRecipeDefinition>? baseList,
        IReadOnlyList<HmiRecipeDefinition>? targetList,
        List<HmiDiffItem> diffs)
    {
        var bMap = (baseList ?? Array.Empty<HmiRecipeDefinition>()).ToDictionary(x => x.RecipeId, StringComparer.OrdinalIgnoreCase);
        var tMap = (targetList ?? Array.Empty<HmiRecipeDefinition>()).ToDictionary(x => x.RecipeId, StringComparer.OrdinalIgnoreCase);

        foreach (var added in tMap.Keys.Except(bMap.Keys, StringComparer.OrdinalIgnoreCase))
        {
            diffs.Add(new HmiDiffItem("Recipe", "Added", added, $"Recipe '{added}' added."));
        }
        foreach (var removed in bMap.Keys.Except(tMap.Keys, StringComparer.OrdinalIgnoreCase))
        {
            diffs.Add(new HmiDiffItem("Recipe", "Removed", removed, $"Recipe '{removed}' removed."));
        }
        foreach (var common in tMap.Keys.Intersect(bMap.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var b = bMap[common];
            var t = tMap[common];
            if (b.Name != t.Name)
            {
                diffs.Add(new HmiDiffItem("Recipe", "Modified", common, $"Name: '{b.Name}' -> '{t.Name}'"));
            }

            var bParams = (b.Parameters ?? Array.Empty<HmiRecipeParameter>()).ToDictionary(p => p.ParameterName, StringComparer.OrdinalIgnoreCase);
            var tParams = (t.Parameters ?? Array.Empty<HmiRecipeParameter>()).ToDictionary(p => p.ParameterName, StringComparer.OrdinalIgnoreCase);

            foreach (var pAdded in tParams.Keys.Except(bParams.Keys, StringComparer.OrdinalIgnoreCase))
            {
                diffs.Add(new HmiDiffItem("RecipeParameter", "Added", $"{common}.{pAdded}", $"Parameter '{pAdded}' added."));
            }
            foreach (var pRemoved in bParams.Keys.Except(tParams.Keys, StringComparer.OrdinalIgnoreCase))
            {
                diffs.Add(new HmiDiffItem("RecipeParameter", "Removed", $"{common}.{pRemoved}", $"Parameter '{pRemoved}' removed."));
            }
            foreach (var pCommon in tParams.Keys.Intersect(bParams.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var bp = bParams[pCommon];
                var tp = tParams[pCommon];
                var pChanges = new List<string>();
                if (bp.BoundTag != tp.BoundTag) pChanges.Add($"BoundTag: '{bp.BoundTag}' -> '{tp.BoundTag}'");
                if (bp.MinValue != tp.MinValue) pChanges.Add($"MinValue: {bp.MinValue} -> {tp.MinValue}");
                if (bp.MaxValue != tp.MaxValue) pChanges.Add($"MaxValue: {bp.MaxValue} -> {tp.MaxValue}");
                if (bp.DefaultValue != tp.DefaultValue) pChanges.Add($"DefaultValue: {bp.DefaultValue} -> {tp.DefaultValue}");

                if (pChanges.Count > 0)
                {
                    diffs.Add(new HmiDiffItem("RecipeParameter", "Modified", $"{common}.{pCommon}", string.Join("; ", pChanges)));
                }
            }
        }
    }

    private static void CompareLocalizations(
        IReadOnlyList<HmiLocalizationEntry>? baseList,
        IReadOnlyList<HmiLocalizationEntry>? targetList,
        List<HmiDiffItem> diffs)
    {
        var bMap = (baseList ?? Array.Empty<HmiLocalizationEntry>()).ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var tMap = (targetList ?? Array.Empty<HmiLocalizationEntry>()).ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var added in tMap.Keys.Except(bMap.Keys, StringComparer.OrdinalIgnoreCase))
        {
            diffs.Add(new HmiDiffItem("Localization", "Added", added, $"Key '{added}' added."));
        }
        foreach (var removed in bMap.Keys.Except(tMap.Keys, StringComparer.OrdinalIgnoreCase))
        {
            diffs.Add(new HmiDiffItem("Localization", "Removed", removed, $"Key '{removed}' removed."));
        }
        foreach (var common in tMap.Keys.Intersect(bMap.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var b = bMap[common];
            var t = tMap[common];
            var bTrans = b.Translations ?? new Dictionary<string, string>();
            var tTrans = t.Translations ?? new Dictionary<string, string>();

            var changedLangs = new List<string>();
            foreach (var lang in bTrans.Keys.Union(tTrans.Keys, StringComparer.OrdinalIgnoreCase))
            {
                bTrans.TryGetValue(lang, out var bVal);
                tTrans.TryGetValue(lang, out var tVal);
                if (bVal != tVal)
                {
                    changedLangs.Add($"{lang}: '{bVal}' -> '{tVal}'");
                }
            }

            if (changedLangs.Count > 0)
            {
                diffs.Add(new HmiDiffItem("Localization", "Modified", common, string.Join("; ", changedLangs)));
            }
        }
    }

    private static HmiArtifactFile WriteArtifactFile(string outputDir, string relativePath, string content, string format)
    {
        var fullPath = Path.Combine(outputDir, relativePath);
        ProjectWorkspaceManager.SanitizePath(fullPath, outputDir);

        var bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(fullPath, bytes);

        var sha256 = ComputeSha256(bytes);
        return new HmiArtifactFile(
            RelativePath: relativePath,
            Sha256: sha256,
            ByteLength: bytes.LongLength,
            Format: format);
    }

    private static string ComputeSha256(byte[] bytes)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string EscapeCsv(string? value)
    {
        if (value == null) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private static string GenerateTagsCsv(IReadOnlyList<HmiTagBinding>? tags)
    {
        var sb = new StringBuilder();
        sb.AppendLine("HmiTagName,PlcSymbolOrAddress,AccessRight,ExpectedDataType,ExpectedUnit,ScreenIds,Description");
        if (tags != null)
        {
            foreach (var t in tags)
            {
                var screens = t.ScreenIds != null ? string.Join(";", t.ScreenIds) : string.Empty;
                sb.AppendLine($"{EscapeCsv(t.HmiTagName)},{EscapeCsv(t.PlcSymbolOrAddress)},{t.AccessRight},{t.ExpectedDataType},{EscapeCsv(t.ExpectedUnit)},{EscapeCsv(screens)},{EscapeCsv(t.Description)}");
            }
        }
        return sb.ToString();
    }

    private static string GenerateScreensCsv(IReadOnlyList<HmiScreenDefinition>? screens)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ScreenId,Name,Description,BoundTagNames");
        if (screens != null)
        {
            foreach (var s in screens)
            {
                var tags = s.BoundTagNames != null ? string.Join(";", s.BoundTagNames) : string.Empty;
                sb.AppendLine($"{EscapeCsv(s.ScreenId)},{EscapeCsv(s.Name)},{EscapeCsv(s.Description)},{EscapeCsv(tags)}");
            }
        }
        return sb.ToString();
    }

    private static string GenerateAlarmsCsv(IReadOnlyList<HmiAlarmEntry>? alarms)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AlarmId,TriggerTag,Severity,MessageKey,ScreenId,HighLimit,LowLimit");
        if (alarms != null)
        {
            foreach (var a in alarms)
            {
                sb.AppendLine($"{EscapeCsv(a.AlarmId)},{EscapeCsv(a.TriggerTag)},{a.Severity},{EscapeCsv(a.MessageKey)},{EscapeCsv(a.ScreenId)},{a.HighLimit},{a.LowLimit}");
            }
        }
        return sb.ToString();
    }

    private static string GenerateRecipesCsv(IReadOnlyList<HmiRecipeDefinition>? recipes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RecipeId,RecipeName,ParameterName,BoundTag,DataType,MinValue,MaxValue,DefaultValue,Unit");
        if (recipes != null)
        {
            foreach (var r in recipes)
            {
                if (r.Parameters != null)
                {
                    foreach (var p in r.Parameters)
                    {
                        sb.AppendLine($"{EscapeCsv(r.RecipeId)},{EscapeCsv(r.Name)},{EscapeCsv(p.ParameterName)},{EscapeCsv(p.BoundTag)},{p.DataType},{p.MinValue},{p.MaxValue},{p.DefaultValue},{EscapeCsv(p.Unit)}");
                    }
                }
            }
        }
        return sb.ToString();
    }

    private static string GenerateLocalizationsCsv(IReadOnlyList<HmiLocalizationEntry>? localizations, IReadOnlyList<string>? supportedCultures)
    {
        var sb = new StringBuilder();
        var cultures = (supportedCultures ?? Array.Empty<string>()).ToList();
        if (cultures.Count == 0) cultures.Add("en-US");

        sb.Append("Key");
        foreach (var c in cultures)
        {
            sb.Append($",{EscapeCsv(c)}");
        }
        sb.AppendLine();

        if (localizations != null)
        {
            foreach (var loc in localizations)
            {
                sb.Append(EscapeCsv(loc.Key));
                var dict = loc.Translations ?? new Dictionary<string, string>();
                foreach (var c in cultures)
                {
                    dict.TryGetValue(c, out var val);
                    sb.Append($",{EscapeCsv(val)}");
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
