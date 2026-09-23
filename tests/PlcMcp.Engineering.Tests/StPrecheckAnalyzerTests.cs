using PlcMcp.Engineering.Analyzers;
using PlcMcp.Engineering.Models;

namespace PlcMcp.Engineering.Tests;

public class StPrecheckAnalyzerTests
{
    private readonly StPrecheckAnalyzer _analyzer = new();

    [Fact]
    public void Analyze_ValidStructuredText_ReturnsPassedWithVendorDisclaimer()
    {
        var code = """
            PROGRAM MainLogic
            VAR
                bStart : BOOL;
                nCounter : INT;
            END_VAR

            IF bStart THEN
                nCounter := nCounter + 1;
            END_IF;
            END_PROGRAM
            """;

        var result = _analyzer.Analyze(code);

        Assert.True(result.Passed);
        Assert.False(result.IsVendorCompiler);
        Assert.Contains("Not a vendor compiler verification", result.Disclaimer);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Analyze_UnmatchedIfStatement_ReportsError()
    {
        var code = """
            IF bSensor THEN
                nSpeed := 100;
            // Missing END_IF
            """;

        var result = _analyzer.Analyze(code);

        Assert.False(result.Passed);
        Assert.Contains(result.Diagnostics, d => d.Code == "ST001" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Analyze_UnclosedComment_ReportsError()
    {
        var code = """
            (* This is an unclosed comment
            nSpeed := 10;
            """;

        var result = _analyzer.Analyze(code);

        Assert.False(result.Passed);
        Assert.Contains(result.Diagnostics, d => d.Code == "ST010" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Analyze_UnbalancedParenthesis_ReportsError()
    {
        var code = """
            nResult := (10 + (20 * 2);
            """;

        var result = _analyzer.Analyze(code);

        Assert.False(result.Passed);
        Assert.Contains(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && (d.Code == "ST011" || d.Code == "ST012"));
    }

    [Fact]
    public void Analyze_SuspiciousAssignmentInIfCondition_ReportsWarning()
    {
        var code = """
            IF bSensor := TRUE THEN
                nSpeed := 50;
            END_IF;
            """;

        var result = _analyzer.Analyze(code);

        Assert.Contains(result.Diagnostics, d => d.Code == "ST014" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Analyze_MissingSemicolon_ReportsWarning()
    {
        var code = """
            nValue := 42
            """;

        var result = _analyzer.Analyze(code);

        Assert.Contains(result.Diagnostics, d => d.Code == "ST013" && d.Severity == DiagnosticSeverity.Warning);
    }
}
