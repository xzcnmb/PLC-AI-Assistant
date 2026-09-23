using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Models;

namespace PlcMcp.Engineering.Hmi;

public interface IHmiValidator
{
    HmiValidationResult Validate(
        HmiManifest manifest,
        IEnumerable<TagDefinition>? tagDefinitions = null,
        IEnumerable<EngineeringSymbol>? engineeringSymbols = null,
        bool requirePlcDefinitions = true);
}

public sealed class HmiValidator : IHmiValidator
{
    public HmiValidationResult Validate(
        HmiManifest manifest,
        IEnumerable<TagDefinition>? tagDefinitions = null,
        IEnumerable<EngineeringSymbol>? engineeringSymbols = null,
        bool requirePlcDefinitions = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var errors = new List<HmiValidationError>();

        // 1. Manifest Metadata Validation
        ValidateMetadata(manifest, errors);

        // 2. Screen Validation
        var screens = manifest.Screens ?? Array.Empty<HmiScreenDefinition>();
        var tagBindings = manifest.TagBindings ?? Array.Empty<HmiTagBinding>();
        var alarms = manifest.Alarms ?? Array.Empty<HmiAlarmEntry>();
        var recipes = manifest.Recipes ?? Array.Empty<HmiRecipeDefinition>();
        var localizations = manifest.Localizations ?? Array.Empty<HmiLocalizationEntry>();

        var validScreenIds = ValidateScreens(screens, tagBindings, errors);

        // 3. Tag Binding Validation
        var validTagMap = ValidateTagBindings(
            tagBindings,
            validScreenIds,
            tagDefinitions,
            engineeringSymbols,
            requirePlcDefinitions,
            errors);

        // 4. Localization Validation
        var validLocKeys = ValidateLocalizations(manifest, localizations, errors);

        // 5. Alarm Validation
        ValidateAlarms(alarms, validTagMap, validScreenIds, validLocKeys, tagDefinitions, errors);

        // 6. Recipe Validation
        ValidateRecipes(recipes, validTagMap, tagDefinitions, errors);

        bool isValid = errors.Count == 0;
        string summary = isValid
            ? $"HMI manifest '{manifest.HmiProjectName}' passed validation with {tagBindings.Count} tag(s), {screens.Count} screen(s), {alarms.Count} alarm(s), {recipes.Count} recipe(s)."
            : $"HMI manifest '{manifest.HmiProjectName}' failed validation with {errors.Count} error(s).";

        return new HmiValidationResult(
            IsValid: isValid,
            Errors: errors,
            TagBindingCount: tagBindings.Count,
            ScreenCount: screens.Count,
            AlarmCount: alarms.Count,
            RecipeCount: recipes.Count,
            LocalizationCount: localizations.Count,
            Summary: summary);
    }

    private static void ValidateMetadata(HmiManifest manifest, List<HmiValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(manifest.ManifestVersion))
        {
            errors.Add(new HmiValidationError(
                "VAL_META_VERSION", "Manifest", "manifestVersion", "Manifest version cannot be empty."));
        }

        if (string.IsNullOrWhiteSpace(manifest.HmiProjectName))
        {
            errors.Add(new HmiValidationError(
                "VAL_META_PROJECT_NAME", "Manifest", "hmiProjectName", "HMI project name cannot be empty."));
        }

        if (string.IsNullOrWhiteSpace(manifest.DefaultCulture))
        {
            errors.Add(new HmiValidationError(
                "VAL_META_DEFAULT_CULTURE", "Manifest", "defaultCulture", "Default culture cannot be empty."));
        }

        var supported = manifest.SupportedCultures ?? Array.Empty<string>();
        if (supported.Count == 0)
        {
            errors.Add(new HmiValidationError(
                "VAL_META_SUPPORTED_CULTURES", "Manifest", "supportedCultures", "Supported cultures list cannot be empty."));
        }
        else if (!string.IsNullOrWhiteSpace(manifest.DefaultCulture) &&
                 !supported.Contains(manifest.DefaultCulture, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(new HmiValidationError(
                "VAL_META_DEFAULT_CULTURE_NOT_SUPPORTED", "Manifest", "defaultCulture",
                $"Default culture '{manifest.DefaultCulture}' must be included in supported cultures list."));
        }
    }

    private static HashSet<string> ValidateScreens(
        IReadOnlyList<HmiScreenDefinition> screens,
        IReadOnlyList<HmiTagBinding> tagBindings,
        List<HmiValidationError> errors)
    {
        var screenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tagNames = new HashSet<string>(
            tagBindings.Select(t => t.HmiTagName).Where(n => !string.IsNullOrWhiteSpace(n)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var screen in screens)
        {
            if (string.IsNullOrWhiteSpace(screen.ScreenId))
            {
                errors.Add(new HmiValidationError(
                    "VAL_SCREEN_EMPTY_ID", "Screen", screen.Name ?? "Unnamed", "Screen ID cannot be empty."));
                continue;
            }

            if (!screenIds.Add(screen.ScreenId))
            {
                errors.Add(new HmiValidationError(
                    "VAL_SCREEN_DUPLICATE_ID", "Screen", screen.ScreenId,
                    $"Duplicate screen ID '{screen.ScreenId}' detected."));
            }

            if (screen.BoundTagNames != null)
            {
                foreach (var tag in screen.BoundTagNames)
                {
                    if (!tagNames.Contains(tag))
                    {
                        errors.Add(new HmiValidationError(
                            "VAL_SCREEN_TAG_REF_MISSING", "Screen", screen.ScreenId,
                            $"Screen '{screen.ScreenId}' references non-existent HMI tag '{tag}'."));
                    }
                }
            }
        }

        return screenIds;
    }

    private static Dictionary<string, HmiTagBinding> ValidateTagBindings(
        IReadOnlyList<HmiTagBinding> tagBindings,
        HashSet<string> validScreenIds,
        IEnumerable<TagDefinition>? tagDefinitions,
        IEnumerable<EngineeringSymbol>? engineeringSymbols,
        bool requirePlcDefinitions,
        List<HmiValidationError> errors)
    {
        var tagMap = new Dictionary<string, HmiTagBinding>(StringComparer.OrdinalIgnoreCase);

        var (plcTagByName, plcTagByAddress) = BuildPlcTagLookups(tagDefinitions);
        var (symbolByName, symbolByAddress) = BuildSymbolLookups(engineeringSymbols);
        bool hasPlcSources = plcTagByName.Count > 0 || plcTagByAddress.Count > 0 ||
                             symbolByName.Count > 0 || symbolByAddress.Count > 0;

        if (requirePlcDefinitions && !hasPlcSources && tagBindings.Count > 0)
        {
            // Fail closed: PLC definitions are required, cannot skip tag binding verification
            errors.Add(new HmiValidationError(
                "VAL_PLC_SOURCE_MISSING", "TagBinding", "Global",
                "PLC tag definitions or PLCopen engineering symbols are required for validation. Tag verification cannot be skipped."));
        }

        foreach (var tag in tagBindings)
        {
            if (string.IsNullOrWhiteSpace(tag.HmiTagName))
            {
                errors.Add(new HmiValidationError(
                    "VAL_TAG_EMPTY_NAME", "TagBinding", "Unnamed", "HMI tag name cannot be empty."));
                continue;
            }

            if (tagMap.ContainsKey(tag.HmiTagName))
            {
                errors.Add(new HmiValidationError(
                    "VAL_TAG_DUPLICATE_NAME", "TagBinding", tag.HmiTagName,
                    $"Duplicate HMI tag name '{tag.HmiTagName}' detected."));
                continue;
            }

            tagMap[tag.HmiTagName] = tag;

            if (string.IsNullOrWhiteSpace(tag.PlcSymbolOrAddress))
            {
                errors.Add(new HmiValidationError(
                    "VAL_TAG_EMPTY_PLC_REF", "TagBinding", tag.HmiTagName,
                    $"PLC symbol or address for tag '{tag.HmiTagName}' cannot be empty."));
                continue;
            }

            // Screen ID references check
            if (tag.ScreenIds != null)
            {
                foreach (var screenId in tag.ScreenIds)
                {
                    if (!validScreenIds.Contains(screenId))
                    {
                        errors.Add(new HmiValidationError(
                            "VAL_TAG_SCREEN_REF_MISSING", "TagBinding", tag.HmiTagName,
                            $"HMI tag '{tag.HmiTagName}' references undefined screen ID '{screenId}'."));
                    }
                }
            }

            // PLC symbol/address resolution and consistency check
            if (hasPlcSources)
            {
                ResolveAndValidatePlcSource(
                    tag,
                    plcTagByName,
                    plcTagByAddress,
                    symbolByName,
                    symbolByAddress,
                    errors);
            }
        }

        return tagMap;
    }

    private static (Dictionary<string, TagDefinition> byName, Dictionary<string, TagDefinition> byAddress)
        BuildPlcTagLookups(IEnumerable<TagDefinition>? tags)
    {
        var byName = new Dictionary<string, TagDefinition>(StringComparer.OrdinalIgnoreCase);
        var byAddress = new Dictionary<string, TagDefinition>(StringComparer.OrdinalIgnoreCase);

        if (tags == null) return (byName, byAddress);

        foreach (var t in tags)
        {
            foreach (var name in t.AllNames)
            {
                byName.TryAdd(name, t);
            }

            if (!string.IsNullOrWhiteSpace(t.NativeAddress))
            {
                byAddress.TryAdd(t.NativeAddress, t);
            }
        }

        return (byName, byAddress);
    }

    private static (Dictionary<string, EngineeringSymbol> byName, Dictionary<string, EngineeringSymbol> byAddress)
        BuildSymbolLookups(IEnumerable<EngineeringSymbol>? symbols)
    {
        var byName = new Dictionary<string, EngineeringSymbol>(StringComparer.OrdinalIgnoreCase);
        var byAddress = new Dictionary<string, EngineeringSymbol>(StringComparer.OrdinalIgnoreCase);

        if (symbols == null) return (byName, byAddress);

        foreach (var s in symbols)
        {
            if (!string.IsNullOrWhiteSpace(s.Name))
            {
                byName.TryAdd(s.Name, s);
            }

            if (!string.IsNullOrWhiteSpace(s.Address))
            {
                byAddress.TryAdd(s.Address, s);
            }
        }

        return (byName, byAddress);
    }

    private static void ResolveAndValidatePlcSource(
        HmiTagBinding tag,
        Dictionary<string, TagDefinition> plcTagByName,
        Dictionary<string, TagDefinition> plcTagByAddress,
        Dictionary<string, EngineeringSymbol> symbolByName,
        Dictionary<string, EngineeringSymbol> symbolByAddress,
        List<HmiValidationError> errors)
    {
        // 1. Try TagDefinition
        if (plcTagByName.TryGetValue(tag.PlcSymbolOrAddress, out var tagDef) ||
            plcTagByAddress.TryGetValue(tag.PlcSymbolOrAddress, out tagDef))
        {
            ValidateAgainstTagDefinition(tag, tagDef, errors);
            return;
        }

        // 2. Try EngineeringSymbol
        if (symbolByName.TryGetValue(tag.PlcSymbolOrAddress, out var sym) ||
            symbolByAddress.TryGetValue(tag.PlcSymbolOrAddress, out sym))
        {
            ValidateAgainstEngineeringSymbol(tag, sym, errors);
            return;
        }

        // Not resolved
        errors.Add(new HmiValidationError(
            "VAL_TAG_PLC_REF_NOT_FOUND", "TagBinding", tag.HmiTagName,
            $"PLC symbol or address '{tag.PlcSymbolOrAddress}' referenced by HMI tag '{tag.HmiTagName}' was not found in PLC tags or symbols."));
    }

    private static void ValidateAgainstTagDefinition(
        HmiTagBinding tag,
        TagDefinition tagDef,
        List<HmiValidationError> errors)
    {
        // Type check
        if (tag.ExpectedDataType != tagDef.DataType)
        {
            errors.Add(new HmiValidationError(
                "VAL_TAG_TYPE_MISMATCH", "TagBinding", tag.HmiTagName,
                $"Data type mismatch for tag '{tag.HmiTagName}': HMI expected '{tag.ExpectedDataType}', but PLC tag '{tagDef.Name}' is '{tagDef.DataType}'."));
        }

        // Access rights
        if (tag.AccessRight is HmiAccessRight.WriteOnly or HmiAccessRight.ReadWrite)
        {
            if (!tagDef.CanWrite)
            {
                errors.Add(new HmiValidationError(
                    "VAL_TAG_WRITE_PERMISSION_DENIED", "TagBinding", tag.HmiTagName,
                    $"HMI tag '{tag.HmiTagName}' requests write access, but PLC tag '{tagDef.Name}' is read-only (CanWrite=false)."));
            }
        }

        if (tag.AccessRight is HmiAccessRight.ReadOnly or HmiAccessRight.ReadWrite)
        {
            if (!tagDef.CanRead)
            {
                errors.Add(new HmiValidationError(
                    "VAL_TAG_READ_PERMISSION_DENIED", "TagBinding", tag.HmiTagName,
                    $"HMI tag '{tag.HmiTagName}' requests read access, but PLC tag '{tagDef.Name}' has CanRead=false."));
            }
        }

        // Unit check
        if (!string.IsNullOrWhiteSpace(tag.ExpectedUnit) && !string.IsNullOrWhiteSpace(tagDef.Unit))
        {
            if (!string.Equals(tag.ExpectedUnit.Trim(), tagDef.Unit.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new HmiValidationError(
                    "VAL_TAG_UNIT_MISMATCH", "TagBinding", tag.HmiTagName,
                    $"Unit mismatch for tag '{tag.HmiTagName}': HMI expected '{tag.ExpectedUnit}', but PLC tag '{tagDef.Name}' has '{tagDef.Unit}'."));
            }
        }
    }

    private static void ValidateAgainstEngineeringSymbol(
        HmiTagBinding tag,
        EngineeringSymbol sym,
        List<HmiValidationError> errors)
    {
        // Type check
        if (!IsCompatiblePlcopenType(tag.ExpectedDataType, sym.DataType))
        {
            errors.Add(new HmiValidationError(
                "VAL_TAG_TYPE_MISMATCH", "TagBinding", tag.HmiTagName,
                $"Data type mismatch for tag '{tag.HmiTagName}': HMI expected '{tag.ExpectedDataType}', but PLCopen symbol '{sym.Name}' is '{sym.DataType}'."));
        }

        // Access check: Physical Input addresses (%I / I) cannot be written by HMI
        if (tag.AccessRight is HmiAccessRight.WriteOnly or HmiAccessRight.ReadWrite)
        {
            if (IsPhysicalInputAddress(sym.Address ?? tag.PlcSymbolOrAddress))
            {
                errors.Add(new HmiValidationError(
                    "VAL_TAG_WRITE_PERMISSION_DENIED", "TagBinding", tag.HmiTagName,
                    $"HMI tag '{tag.HmiTagName}' requests write access to physical input address '{sym.Address ?? tag.PlcSymbolOrAddress}'. Physical inputs cannot be written by HMI."));
            }
        }
    }

    public static bool IsPhysicalInputAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var trimmed = address.Trim();

        // Standard IEC: %I... (e.g. %IX0.0, %IW100, %ID100, %IB0)
        if (trimmed.StartsWith("%I", StringComparison.OrdinalIgnoreCase)) return true;

        // Siemens/Mitsubishi syntax: I0.0, IW100, ID100, IB0, X0, X001
        if (trimmed.StartsWith("I", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 1 &&
            (char.IsDigit(trimmed[1]) || trimmed[1] is 'W' or 'w' or 'D' or 'd' or 'B' or 'b' or 'X' or 'x'))
        {
            return true;
        }

        if (trimmed.StartsWith("X", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 1 &&
            (char.IsDigit(trimmed[1]) || trimmed[1] is '0' or '1' or '2' or '3' or '4' or '5' or '6' or '7' or '8' or '9'))
        {
            return true;
        }

        return false;
    }

    public static bool IsCompatiblePlcopenType(PlcDataType expected, string actualPlcopenType)
    {
        if (string.IsNullOrWhiteSpace(actualPlcopenType)) return false;
        var cleanType = actualPlcopenType.Trim().ToUpperInvariant();

        return expected switch
        {
            PlcDataType.Bool => cleanType is "BOOL" or "BIT",
            PlcDataType.Int16 => cleanType is "INT" or "INT16" or "SHORT" or "SINT",
            PlcDataType.UInt16 => cleanType is "UINT" or "UINT16" or "WORD" or "BYTE" or "USINT",
            PlcDataType.Int32 => cleanType is "DINT" or "INT32" or "LONG",
            PlcDataType.UInt32 => cleanType is "UDINT" or "UINT32" or "DWORD",
            PlcDataType.Real => cleanType is "REAL" or "FLOAT" or "SINGLE",
            PlcDataType.Double => cleanType is "LREAL" or "DOUBLE" or "FLOAT64",
            PlcDataType.String => cleanType is "STRING" or "WSTRING" or "CHAR",
            _ => false
        };
    }

    private static HashSet<string> ValidateLocalizations(
        HmiManifest manifest,
        IReadOnlyList<HmiLocalizationEntry> localizations,
        List<HmiValidationError> errors)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var loc in localizations)
        {
            if (string.IsNullOrWhiteSpace(loc.Key))
            {
                errors.Add(new HmiValidationError(
                    "VAL_LOC_EMPTY_KEY", "Localization", "Unnamed", "Localization key cannot be empty."));
                continue;
            }

            if (!keys.Add(loc.Key))
            {
                errors.Add(new HmiValidationError(
                    "VAL_LOC_DUPLICATE_KEY", "Localization", loc.Key,
                    $"Duplicate localization key '{loc.Key}' detected."));
                continue;
            }

            var translations = loc.Translations ?? new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(manifest.DefaultCulture))
            {
                var defaultTranslation = translations.FirstOrDefault(x => string.Equals(x.Key, manifest.DefaultCulture, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(defaultTranslation.Key) || string.IsNullOrWhiteSpace(defaultTranslation.Value))
                {
                    errors.Add(new HmiValidationError(
                        "VAL_LOC_DEFAULT_CULTURE_MISSING", "Localization", loc.Key,
                        $"Localization key '{loc.Key}' is missing a translation for default culture '{manifest.DefaultCulture}'."));
                }
            }
        }

        return keys;
    }

    private static void ValidateAlarms(
        IReadOnlyList<HmiAlarmEntry> alarms,
        Dictionary<string, HmiTagBinding> tagMap,
        HashSet<string> validScreenIds,
        HashSet<string> validLocKeys,
        IEnumerable<TagDefinition>? tagDefinitions,
        List<HmiValidationError> errors)
    {
        var (plcTagByName, plcTagByAddress) = BuildPlcTagLookups(tagDefinitions);
        var alarmIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var alarm in alarms)
        {
            if (string.IsNullOrWhiteSpace(alarm.AlarmId))
            {
                errors.Add(new HmiValidationError(
                    "VAL_ALARM_EMPTY_ID", "Alarm", "Unnamed", "Alarm ID cannot be empty."));
                continue;
            }

            if (!alarmIds.Add(alarm.AlarmId))
            {
                errors.Add(new HmiValidationError(
                    "VAL_ALARM_DUPLICATE_ID", "Alarm", alarm.AlarmId,
                    $"Duplicate alarm ID '{alarm.AlarmId}' detected."));
            }

            // Message Key check
            if (string.IsNullOrWhiteSpace(alarm.MessageKey))
            {
                errors.Add(new HmiValidationError(
                    "VAL_ALARM_EMPTY_MSG_KEY", "Alarm", alarm.AlarmId,
                    $"Alarm '{alarm.AlarmId}' has empty messageKey."));
            }
            else if (!validLocKeys.Contains(alarm.MessageKey))
            {
                errors.Add(new HmiValidationError(
                    "VAL_ALARM_LOCALIZATION_REF_MISSING", "Alarm", alarm.AlarmId,
                    $"Alarm '{alarm.AlarmId}' references missing localization key '{alarm.MessageKey}'."));
            }

            // Screen ID check
            if (!string.IsNullOrWhiteSpace(alarm.ScreenId) && !validScreenIds.Contains(alarm.ScreenId))
            {
                errors.Add(new HmiValidationError(
                    "VAL_ALARM_SCREEN_REF_MISSING", "Alarm", alarm.AlarmId,
                    $"Alarm '{alarm.AlarmId}' references undefined screen ID '{alarm.ScreenId}'."));
            }

            // Trigger Tag check
            if (string.IsNullOrWhiteSpace(alarm.TriggerTag))
            {
                errors.Add(new HmiValidationError(
                    "VAL_ALARM_EMPTY_TRIGGER_TAG", "Alarm", alarm.AlarmId,
                    $"Alarm '{alarm.AlarmId}' has empty triggerTag."));
                continue;
            }

            if (!tagMap.TryGetValue(alarm.TriggerTag, out var boundTag))
            {
                errors.Add(new HmiValidationError(
                    "VAL_ALARM_TAG_REF_MISSING", "Alarm", alarm.AlarmId,
                    $"Alarm '{alarm.AlarmId}' references non-existent HMI trigger tag '{alarm.TriggerTag}'."));
                continue;
            }

            // Digital vs Analog limits check
            if (boundTag.ExpectedDataType == PlcDataType.Bool)
            {
                if (alarm.HighLimit.HasValue || alarm.LowLimit.HasValue)
                {
                    errors.Add(new HmiValidationError(
                        "VAL_ALARM_DIGITAL_WITH_LIMITS", "Alarm", alarm.AlarmId,
                        $"Digital alarm '{alarm.AlarmId}' on Bool tag '{boundTag.HmiTagName}' cannot define HighLimit or LowLimit."));
                }
            }
            else
            {
                // Analog limits check
                if (alarm.HighLimit.HasValue && alarm.LowLimit.HasValue)
                {
                    if (alarm.LowLimit.Value > alarm.HighLimit.Value)
                    {
                        errors.Add(new HmiValidationError(
                            "VAL_ALARM_LIMIT_RANGE_INVALID", "Alarm", alarm.AlarmId,
                            $"Alarm '{alarm.AlarmId}' LowLimit ({alarm.LowLimit.Value}) is greater than HighLimit ({alarm.HighLimit.Value})."));
                    }
                }

                // Range check against PLC Tag Definition if available
                TagDefinition? tagDef = null;
                plcTagByName.TryGetValue(boundTag.PlcSymbolOrAddress, out tagDef);
                if (tagDef == null)
                {
                    plcTagByAddress.TryGetValue(boundTag.PlcSymbolOrAddress, out tagDef);
                }

                if (tagDef != null)
                {
                    if (alarm.HighLimit.HasValue && tagDef.Maximum.HasValue && alarm.HighLimit.Value > tagDef.Maximum.Value)
                    {
                        errors.Add(new HmiValidationError(
                            "VAL_ALARM_LIMIT_OUT_OF_BOUNDS", "Alarm", alarm.AlarmId,
                            $"Alarm '{alarm.AlarmId}' HighLimit ({alarm.HighLimit.Value}) exceeds PLC tag '{tagDef.Name}' physical maximum ({tagDef.Maximum.Value})."));
                    }

                    if (alarm.LowLimit.HasValue && tagDef.Minimum.HasValue && alarm.LowLimit.Value < tagDef.Minimum.Value)
                    {
                        errors.Add(new HmiValidationError(
                            "VAL_ALARM_LIMIT_OUT_OF_BOUNDS", "Alarm", alarm.AlarmId,
                            $"Alarm '{alarm.AlarmId}' LowLimit ({alarm.LowLimit.Value}) is below PLC tag '{tagDef.Name}' physical minimum ({tagDef.Minimum.Value})."));
                    }
                }
            }
        }
    }

    private static void ValidateRecipes(
        IReadOnlyList<HmiRecipeDefinition> recipes,
        Dictionary<string, HmiTagBinding> tagMap,
        IEnumerable<TagDefinition>? tagDefinitions,
        List<HmiValidationError> errors)
    {
        var (plcTagByName, plcTagByAddress) = BuildPlcTagLookups(tagDefinitions);
        var recipeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var recipe in recipes)
        {
            if (string.IsNullOrWhiteSpace(recipe.RecipeId))
            {
                errors.Add(new HmiValidationError(
                    "VAL_RECIPE_EMPTY_ID", "Recipe", recipe.Name ?? "Unnamed", "Recipe ID cannot be empty."));
                continue;
            }

            if (!recipeIds.Add(recipe.RecipeId))
            {
                errors.Add(new HmiValidationError(
                    "VAL_RECIPE_DUPLICATE_ID", "Recipe", recipe.RecipeId,
                    $"Duplicate recipe ID '{recipe.RecipeId}' detected."));
            }

            var parameters = recipe.Parameters ?? Array.Empty<HmiRecipeParameter>();
            var paramNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var param in parameters)
            {
                if (string.IsNullOrWhiteSpace(param.ParameterName))
                {
                    errors.Add(new HmiValidationError(
                        "VAL_RECIPE_PARAM_EMPTY_NAME", "Recipe", recipe.RecipeId,
                        $"Recipe '{recipe.RecipeId}' contains a parameter with an empty name."));
                    continue;
                }

                if (!paramNames.Add(param.ParameterName))
                {
                    errors.Add(new HmiValidationError(
                        "VAL_RECIPE_PARAM_DUPLICATE_NAME", "Recipe", $"{recipe.RecipeId}.{param.ParameterName}",
                        $"Duplicate parameter name '{param.ParameterName}' in recipe '{recipe.RecipeId}'."));
                }

                if (string.IsNullOrWhiteSpace(param.BoundTag))
                {
                    errors.Add(new HmiValidationError(
                        "VAL_RECIPE_PARAM_EMPTY_TAG", "Recipe", $"{recipe.RecipeId}.{param.ParameterName}",
                        $"Recipe parameter '{param.ParameterName}' has empty boundTag."));
                    continue;
                }

                if (!tagMap.TryGetValue(param.BoundTag, out var boundTag))
                {
                    errors.Add(new HmiValidationError(
                        "VAL_RECIPE_TAG_REF_MISSING", "Recipe", $"{recipe.RecipeId}.{param.ParameterName}",
                        $"Recipe parameter '{param.ParameterName}' references non-existent HMI tag '{param.BoundTag}'."));
                    continue;
                }

                // Recipe must be able to write parameter to PLC
                if (boundTag.AccessRight == HmiAccessRight.ReadOnly)
                {
                    errors.Add(new HmiValidationError(
                        "VAL_RECIPE_TAG_READ_ONLY", "Recipe", $"{recipe.RecipeId}.{param.ParameterName}",
                        $"Recipe parameter '{param.ParameterName}' bound to tag '{param.BoundTag}' which is configured as ReadOnly in HMI."));
                }

                // Type match
                if (param.DataType != boundTag.ExpectedDataType)
                {
                    errors.Add(new HmiValidationError(
                        "VAL_RECIPE_PARAM_TYPE_MISMATCH", "Recipe", $"{recipe.RecipeId}.{param.ParameterName}",
                        $"Recipe parameter '{param.ParameterName}' data type '{param.DataType}' does not match bound tag expected data type '{boundTag.ExpectedDataType}'."));
                }

                // Min / Max range
                if (param.MinValue > param.MaxValue)
                {
                    errors.Add(new HmiValidationError(
                        "VAL_RECIPE_RANGE_INVALID", "Recipe", $"{recipe.RecipeId}.{param.ParameterName}",
                        $"Recipe parameter '{param.ParameterName}' MinValue ({param.MinValue}) is greater than MaxValue ({param.MaxValue})."));
                }

                // Default value bounds
                if (param.DefaultValue.HasValue)
                {
                    if (param.DefaultValue.Value < param.MinValue || param.DefaultValue.Value > param.MaxValue)
                    {
                        errors.Add(new HmiValidationError(
                            "VAL_RECIPE_DEFAULT_OUT_OF_BOUNDS", "Recipe", $"{recipe.RecipeId}.{param.ParameterName}",
                            $"Recipe parameter '{param.ParameterName}' DefaultValue ({param.DefaultValue.Value}) is outside range [{param.MinValue}, {param.MaxValue}]."));
                    }
                }

                // Physical bounds check against PLC TagDefinition
                TagDefinition? tagDef = null;
                plcTagByName.TryGetValue(boundTag.PlcSymbolOrAddress, out tagDef);
                if (tagDef == null)
                {
                    plcTagByAddress.TryGetValue(boundTag.PlcSymbolOrAddress, out tagDef);
                }

                if (tagDef != null)
                {
                    if (tagDef.Minimum.HasValue && param.MinValue < tagDef.Minimum.Value)
                    {
                        errors.Add(new HmiValidationError(
                            "VAL_RECIPE_PARAM_OUT_OF_BOUNDS", "Recipe", $"{recipe.RecipeId}.{param.ParameterName}",
                            $"Recipe parameter '{param.ParameterName}' MinValue ({param.MinValue}) is below PLC tag '{tagDef.Name}' physical minimum ({tagDef.Minimum.Value})."));
                    }

                    if (tagDef.Maximum.HasValue && param.MaxValue > tagDef.Maximum.Value)
                    {
                        errors.Add(new HmiValidationError(
                            "VAL_RECIPE_PARAM_OUT_OF_BOUNDS", "Recipe", $"{recipe.RecipeId}.{param.ParameterName}",
                            $"Recipe parameter '{param.ParameterName}' MaxValue ({param.MaxValue}) exceeds PLC tag '{tagDef.Name}' physical maximum ({tagDef.Maximum.Value})."));
                    }

                    // Unit check
                    if (!string.IsNullOrWhiteSpace(param.Unit) && !string.IsNullOrWhiteSpace(tagDef.Unit))
                    {
                        if (!string.Equals(param.Unit.Trim(), tagDef.Unit.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add(new HmiValidationError(
                                "VAL_RECIPE_PARAM_UNIT_MISMATCH", "Recipe", $"{recipe.RecipeId}.{param.ParameterName}",
                                $"Recipe parameter '{param.ParameterName}' unit '{param.Unit}' does not match PLC tag '{tagDef.Name}' unit '{tagDef.Unit}'."));
                        }
                    }
                }
            }
        }
    }
}
