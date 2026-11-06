using System.Text;
using PlcMcp.Engineering.Workers.Keyence;

namespace PlcMcp.Engineering.Tests;

// Byte-level, offline tests for the MNM container generator. They assert the exact
// UTF-16LE byte layout, container line order and precheck behavior against the
// KV STUDIO 12G export grammar; no KV STUDIO installation is required.
public class KeyenceMnmGeneratorTests
{
    private const int DeviceCode = 60;

    private static string DecodeUtf16(KeyenceMnmResult result)
        => Encoding.Unicode.GetString(result.Payload, 2, result.Payload.Length - 2);

    [Fact]
    public void Generate_ScanProgram_BuildsGoldenUtf16LeContainer()
    {
        // 'out' is a KV reserved word and now fails the body variable check (KV STUDIO
        // 12.42 rejected 'out :=' at variable registration); the golden body uses 'sw'.
        var result = KeyenceMnmGenerator.Generate("IF b THEN\r\n  sw := 1;\r\nEND_IF", "Main", DeviceCode, functionBlock: false);

        Assert.True(result.Ok);
        Assert.Equal(0xFF, result.Payload[0]);
        Assert.Equal(0xFE, result.Payload[1]);
        Assert.Equal("Utf16Le", result.OutputEncoding);
        Assert.Equal(3, result.SourceLines);
        Assert.Equal("DEVICE:60\r\n;MODULE:Main\r\n;MODULE_TYPE:0\r\nAREA_ST\r\n;IF b THEN\r\n;  sw := 1;\r\n;END_IF\r\nEND\r\nENDH\r\n",
            DecodeUtf16(result));
    }

    [Fact]
    public void Generate_FunctionBlock_WritesModuleType2()
    {
        var result = KeyenceMnmGenerator.Generate("sw := 1;", "MyFb", DeviceCode, functionBlock: true);

        Assert.True(result.Ok);
        Assert.Contains(";MODULE_TYPE:2", DecodeUtf16(result));
    }

    [Theory]
    [InlineData("1Bad")]
    [InlineData("has space")]
    [InlineData("colon:name")]
    [InlineData("")]
    public void Generate_InvalidModuleName_Fails(string moduleName)
    {
        var result = KeyenceMnmGenerator.Generate("sw := 1;", moduleName, DeviceCode, functionBlock: false);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Payload);
    }

    [Fact]
    public void Generate_DeviceCodeOutOfRange_Fails()
    {
        var result = KeyenceMnmGenerator.Generate("out := 1;", "Main", 300, functionBlock: false);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Generate_CommentOnlySource_FailsNoExecutable()
    {
        var result = KeyenceMnmGenerator.Generate("// only a comment", "Main", DeviceCode, functionBlock: false);

        Assert.False(result.Ok);
        Assert.Contains("No executable", result.Error);
    }

    [Fact]
    public void Generate_DeclarationWrapperInBody_Fails()
    {
        var result = KeyenceMnmGenerator.Generate("PROGRAM Main\r\nout := 1;\r\nEND_PROGRAM", "Main", DeviceCode, functionBlock: false);

        Assert.False(result.Ok);
        Assert.Contains("wrapper", result.Error);
    }

    [Fact]
    public void Generate_ContainerInjectionLine_Fails()
    {
        var result = KeyenceMnmGenerator.Generate("DEVICE:99\r\nout := 1;", "Main", DeviceCode, functionBlock: false);

        Assert.False(result.Ok);
        Assert.Contains("injection", result.Error);
    }

    [Fact]
    public void Generate_FbCallInBody_WarnsButSucceedsAndKeepsLine()
    {
        var result = KeyenceMnmGenerator.Generate("FBEXEC MyFb MyInst", "Main", DeviceCode, functionBlock: false);

        Assert.True(result.Ok);
        Assert.Contains(result.Warnings, w => w.Contains("FB", StringComparison.Ordinal) || w.Contains("instance", StringComparison.Ordinal));
        Assert.Contains(";FBEXEC MyFb MyInst", DecodeUtf16(result));
    }

    [Fact]
    public void Generate_TimerWithExplicitNumber_WarnsError61()
    {
        var result = KeyenceMnmGenerator.Generate("TMR 5  100", "Main", DeviceCode, functionBlock: false);

        Assert.True(result.Ok);
        Assert.Contains(result.Warnings, w => w.Contains("61", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_Utf8Mode_HasNoBomAndKeepsContainer()
    {
        var result = KeyenceMnmGenerator.Generate("sw := 1;", "Main", DeviceCode, functionBlock: false, utf16Le: false);

        Assert.True(result.Ok);
        Assert.Equal("Utf8", result.OutputEncoding);
        Assert.NotEqual(0xFF, result.Payload[0]);
        Assert.Contains("DEVICE:60", Encoding.UTF8.GetString(result.Payload));
    }

    [Fact]
    public void Generate_SourceLinesArePrefixedVerbatim_IncludingCommentLines()
    {
        var result = KeyenceMnmGenerator.Generate("sw := 1;\r\n// note\r\nEND_IF", "Main", DeviceCode, functionBlock: false);

        Assert.True(result.Ok);
        Assert.Contains(";// note", DecodeUtf16(result));
    }

    // Reserved-word module name rejection. KV STUDIO is case-insensitive and rejects
    // registrations colliding with reserved words ("与预约语重复"): 'b' (link relay B) and
    // 'out' were verified rejected on KV STUDIO 12.42 on 2026-11-04; the rest of the list
    // is the same family (device classes B/C/T/V/D/M/X/Y/R, DM/EM/SD, ST keywords).
    [Theory]
    [InlineData("b")]
    [InlineData("B")]
    [InlineData("out")]
    [InlineData("OUT")]
    [InlineData("Var")]
    [InlineData("IF")]
    [InlineData("DM")]
    [InlineData("t")]
    public void Generate_ModuleNameIsReservedWord_FailsRegardlessOfCase(string moduleName)
    {
        var result = KeyenceMnmGenerator.Generate("sw := 1;", moduleName, DeviceCode, functionBlock: false);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Contains("reserved word", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Payload);
    }

    // Device designators ("class letters + digits") are reserved device names in KV STUDIO,
    // so they cannot be module names either.
    [Theory]
    [InlineData("DM100")]
    [InlineData("B1")]
    [InlineData("w0")]
    public void Generate_ModuleNameIsDeviceDesignator_Fails(string moduleName)
    {
        var result = KeyenceMnmGenerator.Generate("sw := 1;", moduleName, DeviceCode, functionBlock: false);

        Assert.False(result.Ok);
        Assert.Contains("device designator", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // Bare device designators used as values in the statement body: KV-ST should reach
    // devices only via variable-table bindings. Warning, not Error - see the generator
    // comment for the rationale (a user variable like C100 would also match offline).
    [Fact]
    public void Generate_BareDeviceDesignatorInBody_Warns()
    {
        var result = KeyenceMnmGenerator.Generate("sw := DM100;\r\nresult := 2;", "Main", DeviceCode, functionBlock: false);

        Assert.True(result.Ok);
        Assert.Contains(result.Warnings, w => w.Contains("device designator", StringComparison.OrdinalIgnoreCase));
    }

    // A bare device designator on the LEFT of ':=' is a direct device-address assignment,
    // which KV-ST forbids outright (verified decision 2026-11-06): hard FAIL, unlike the
    // right-side value case above which stays a warning.
    [Theory]
    [InlineData("DM100 := 1;")]
    [InlineData("sw := 2;\r\nW0 := DM5;")]
    [InlineData("b1 := TRUE;")]
    public void Generate_BareDeviceDesignatorAsAssignmentTarget_Fails(string source)
    {
        var result = KeyenceMnmGenerator.Generate(source, "Main", DeviceCode, functionBlock: false);

        Assert.False(result.Ok);
        Assert.Contains("bare KV device designator", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Payload);
    }

    // Comments and string literals are stripped before every body scan, so a reserved
    // word or designator there must not fail or warn.
    [Fact]
    public void Generate_DeviceDesignatorInCommentOrString_NoWarning()
    {
        var result = KeyenceMnmGenerator.Generate(
            "// write DM100 here\r\nmsg := 'DM100';\r\n(* B1 too *)\r\nsw := 2;",
            "Main", DeviceCode, functionBlock: false);

        Assert.True(result.Ok);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("device designator", StringComparison.OrdinalIgnoreCase));
    }

    // ST variable registration reserved-word check (KV STUDIO 12.42 live finding
    // 2026-11-06): 'out' as an assignment target is rejected by the variable table
    // ("与预约语重复"), so the offline precheck must fail it before importing. Matching
    // is case-insensitive like KV STUDIO itself.
    [Theory]
    [InlineData("out := 1;")]
    [InlineData("OUT := 1;")]
    [InlineData("b := 1;")]
    [InlineData("m := 2;")]
    public void Generate_BodyVariableNameIsReservedWord_Fails(string source)
    {
        var result = KeyenceMnmGenerator.Generate(source, "Main", DeviceCode, functionBlock: false);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Contains("reserved word", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Payload);
    }

    // 'result' was verified safe on KV STUDIO 12.42 (renamed b/out -> sw/result compiled
    // cleanly), so reserved-word-looking-but-safe names must pass with no failure.
    [Fact]
    public void Generate_SwAndResultBody_CleanPass()
    {
        var result = KeyenceMnmGenerator.Generate("IF sw THEN\r\n  result := 1;\r\nEND_IF", "MotorCtl", DeviceCode, functionBlock: false);

        Assert.True(result.Ok);
        Assert.Empty(result.Warnings);
    }

    // Reserved words that only appear as operators/values (not ':=' targets) must not
    // trip the variable-name check: 'result := sw AND flag' was the P0 live-verified body.
    [Fact]
    public void Generate_ReservedWordsAsValuesDoNotFail()
    {
        var result = KeyenceMnmGenerator.Generate("sw := TRUE;\r\nresult := sw AND flag;", "MotorCtl", DeviceCode, functionBlock: false);

        Assert.True(result.Ok);
    }

    // Statement-terminator normalization (KV STUDIO 12.42 live finding 2026-11-06):
    // assignment lines missing the trailing ';' gain it in the container body; lines
    // that already have it and block keywords stay byte-identical.
    [Fact]
    public void Generate_MissingSemicolonOnAssignment_AutoAppendsAndWarns()
    {
        var result = KeyenceMnmGenerator.Generate("result := TRUE\r\nsw := FALSE;", "Main", DeviceCode, functionBlock: false);

        Assert.True(result.Ok);
        Assert.Contains(";result := TRUE;", DecodeUtf16(result));
        Assert.Contains(";sw := FALSE;", DecodeUtf16(result));
        Assert.Contains(result.Warnings, w => w.Contains("Auto-appended missing ';'", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_BlockKeywordsAndExistingTerminators_Untouched()
    {
        var result = KeyenceMnmGenerator.Generate(
            "IF sw THEN\r\n  result := 1;\r\nEND_IF\r\nFOR i := 1 TO 5 DO\r\nEND_FOR",
            "Main", DeviceCode, functionBlock: false);

        Assert.True(result.Ok);
        string text = DecodeUtf16(result);
        Assert.Contains(";IF sw THEN", text);                   // no ';' added to IF
        Assert.Contains(";  result := 1;", text);               // existing ';' untouched (no doubling)
        Assert.DoesNotContain("result := 1;;", text);
        Assert.Contains(";END_IF", text);
        Assert.Contains(";FOR i := 1 TO 5 DO", text);           // compound header with ':=' stays verbatim
        Assert.DoesNotContain("TO 5 DO;", text);
        Assert.Empty(result.Warnings);                          // nothing was fixed
    }

    [Fact]
    public void Generate_MultiLineContinuationAssignment_Untouched()
    {
        var result = KeyenceMnmGenerator.Generate("result := sw AND\r\nflag", "Main", DeviceCode, functionBlock: false);

        Assert.True(result.Ok);
        Assert.Contains(";result := sw AND", DecodeUtf16(result));   // ends in continuation word: no ';'
        Assert.Contains(";flag", DecodeUtf16(result));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Generate_TrailingLineCommentAfterAssignment_SemicolonBeforeComment()
    {
        var result = KeyenceMnmGenerator.Generate("sw := 1  // set switch", "Main", DeviceCode, functionBlock: false);

        Assert.True(result.Ok);
        Assert.Contains(";sw := 1;  // set switch", DecodeUtf16(result));
    }
}