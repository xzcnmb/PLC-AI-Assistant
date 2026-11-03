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
        var result = KeyenceMnmGenerator.Generate("IF b THEN\r\n  out := 1;\r\nEND_IF", "Main", DeviceCode, functionBlock: false);

        Assert.True(result.Ok);
        Assert.Equal(0xFF, result.Payload[0]);
        Assert.Equal(0xFE, result.Payload[1]);
        Assert.Equal("Utf16Le", result.OutputEncoding);
        Assert.Equal(3, result.SourceLines);
        Assert.Equal("DEVICE:60\r\n;MODULE:Main\r\n;MODULE_TYPE:0\r\nAREA_ST\r\n;IF b THEN\r\n;  out := 1;\r\n;END_IF\r\nEND\r\nENDH\r\n",
            DecodeUtf16(result));
    }

    [Fact]
    public void Generate_FunctionBlock_WritesModuleType2()
    {
        var result = KeyenceMnmGenerator.Generate("out := 1;", "MyFb", DeviceCode, functionBlock: true);

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
        var result = KeyenceMnmGenerator.Generate("out := 1;", moduleName, DeviceCode, functionBlock: false);

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
        var result = KeyenceMnmGenerator.Generate("out := 1;", "Main", DeviceCode, functionBlock: false, utf16Le: false);

        Assert.True(result.Ok);
        Assert.Equal("Utf8", result.OutputEncoding);
        Assert.NotEqual(0xFF, result.Payload[0]);
        Assert.Contains("DEVICE:60", Encoding.UTF8.GetString(result.Payload));
    }

    [Fact]
    public void Generate_SourceLinesArePrefixedVerbatim_IncludingCommentLines()
    {
        var result = KeyenceMnmGenerator.Generate("out := 1;\r\n// note\r\nEND_IF", "Main", DeviceCode, functionBlock: false);

        Assert.True(result.Ok);
        Assert.Contains(";// note", DecodeUtf16(result));
    }
}
