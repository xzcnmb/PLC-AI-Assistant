using PlcMcp.Engineering.Workers.Codesys;
using PlcMcp.Engineering.Workers.External;

namespace PlcMcp.Engineering.Tests;

public sealed class CodesysWorkerConfigTests
{
    [Fact]
    public void BuildRawArguments_PreservesRequiredQuotesAndOfflineFlags()
    {
        var config = new CodesysWorkerConfig
        {
            ProfileName = "CODESYS V3.5 SP22 Patch 3",
            DriverScriptPath = @"D:\PLCMCP\scripts\codesys_worker.py"
        };

        string raw = config.BuildRawArguments();

        Assert.Contains("--noUI", raw);
        Assert.Contains("--noConsole", raw);
        Assert.Contains("--skipProjectRecovery", raw);
        Assert.Contains("--skipUnlicensedPlugins", raw);
        Assert.Contains("--profile=\"CODESYS V3.5 SP22 Patch 3\"", raw);
        Assert.Contains("--runscript=\"D:\\PLCMCP\\scripts\\codesys_worker.py\"", raw);
        ExternalWorkerSecurityPolicy.ValidateRawArguments(raw);
    }

    [Fact]
    public void ToExternalWorkerConfig_UsesPinnedExecutableAndWritableCopyOnlyWhenExplicit()
    {
        var config = new CodesysWorkerConfig
        {
            CodesysExePath = @"C:\Program Files\CODESYS 3.5.22.30\CODESYS\Common\CODESYS.exe",
            ProfileName = "CODESYS V3.5 SP22 Patch 3",
            DriverScriptPath = @"D:\PLCMCP\scripts\codesys_worker.py",
            DriverScriptSha256 = new string('a', 64),
            ProjectRoot = @"D:\PLCMCP\scratch",
            AllowWritableWorkingCopy = true
        };

        var external = config.ToExternalWorkerConfig();

        Assert.Equal(config.CodesysExePath, external.ExecutablePath);
        Assert.Contains(config.CodesysExePath, external.AllowedExecutablePaths);
        Assert.Contains(config.ProjectRoot, external.AllowedWorkspaceRoots);
        Assert.False(external.WorkingCopyReadOnly);
        Assert.True(external.RequirePinnedIdentity);
        Assert.Equal("Codesys-ScriptEngine-Worker", external.ExpectedWorkerName);
        Assert.Equal(config.BuildRawArguments(), external.RawArguments);
        Assert.Empty(external.Arguments);
    }

    [Theory]
    [InlineData("--profile=\"name\"\r\ncalc.exe")]
    [InlineData("--profile=\"name\" & calc.exe")]
    [InlineData("--profile=\"unmatched")]
    public void ValidateRawArguments_RejectsInjectionAndMalformedInput(string value)
    {
        Assert.Throws<ArgumentException>(() => ExternalWorkerSecurityPolicy.ValidateRawArguments(value));
    }
}
