using System.Text.RegularExpressions;
using PlcMcp.Engineering.Models;

namespace PlcMcp.Engineering.Analyzers;

public interface IStPrecheckAnalyzer
{
    StaticCheckResult Analyze(string sourceCode, string? filename = null);
}

public sealed class StPrecheckAnalyzer : IStPrecheckAnalyzer
{
    private static readonly Regex UnclosedCommentRegex = new(@"\(\*(?:(?!\*\)).)*$", RegexOptions.Singleline);
    private static readonly Regex UnterminatedStringRegex = new(@"'(?:[^'\\]|\\.)*$", RegexOptions.Multiline);
    private static readonly Regex AssignmentComparisonConfuseRegex = new(@"^\s*IF\b.*(?<![:<>=])=(?!=).*THEN", RegexOptions.Multiline | RegexOptions.IgnoreCase);

    public StaticCheckResult Analyze(string sourceCode, string? filename = null)
    {
        var diagnostics = new List<StaticDiagnostic>();

        if (string.IsNullOrWhiteSpace(sourceCode))
        {
            return new StaticCheckResult(
                Passed: true,
                Diagnostics: Array.Empty<StaticDiagnostic>(),
                Summary: "Source code is empty.",
                IsVendorCompiler: false);
        }

        // Rule 1: Check block balance (IF / END_IF, CASE / END_CASE, FOR / END_FOR, WHILE / END_WHILE)
        CheckBlockBalance(sourceCode, "IF", "END_IF", "ST001", "Unmatched IF/END_IF statement", diagnostics);
        CheckBlockBalance(sourceCode, "CASE", "END_CASE", "ST002", "Unmatched CASE/END_CASE statement", diagnostics);
        CheckBlockBalance(sourceCode, "FOR", "END_FOR", "ST003", "Unmatched FOR/END_FOR statement", diagnostics);
        CheckBlockBalance(sourceCode, "WHILE", "END_WHILE", "ST004", "Unmatched WHILE/END_WHILE statement", diagnostics);
        CheckBlockBalance(sourceCode, "REPEAT", "END_REPEAT", "ST005", "Unmatched REPEAT/END_REPEAT statement", diagnostics);
        CheckBlockBalance(sourceCode, "VAR", "END_VAR", "ST006", "Unmatched VAR/END_VAR block", diagnostics);
        CheckBlockBalance(sourceCode, "FUNCTION_BLOCK", "END_FUNCTION_BLOCK", "ST007", "Unmatched FUNCTION_BLOCK/END_FUNCTION_BLOCK", diagnostics);
        CheckBlockBalance(sourceCode, "FUNCTION", "END_FUNCTION", "ST008", "Unmatched FUNCTION/END_FUNCTION", diagnostics);
        CheckBlockBalance(sourceCode, "PROGRAM", "END_PROGRAM", "ST009", "Unmatched PROGRAM/END_PROGRAM", diagnostics);

        // Rule 2: Unclosed comments (* ... *)
        CheckUnclosedComments(sourceCode, diagnostics);

        // Rule 3: Parenthesis balance ()
        CheckParenthesisBalance(sourceCode, diagnostics);

        // Rule 4: Semicolon termination check on executable statements
        CheckSemicolonTerminators(sourceCode, diagnostics);

        // Rule 5: Common syntax warnings (e.g. IF a = b instead of equality vs assignment in ST where = is equal, := is assignment)
        CheckSuspiciousAssignments(sourceCode, diagnostics);

        bool passed = diagnostics.All(d => d.Severity != DiagnosticSeverity.Error);
        string summary = passed
            ? $"Precheck PASSED with {diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning)} warning(s)."
            : $"Precheck FAILED with {diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error)} error(s), {diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning)} warning(s).";

        return new StaticCheckResult(
            Passed: passed,
            Diagnostics: diagnostics,
            Summary: summary,
            IsVendorCompiler: false,
            Disclaimer: "Static heuristic precheck only. Not a vendor compiler verification.");
    }

    private static void CheckBlockBalance(
        string code,
        string startKeyword,
        string endKeyword,
        string ruleCode,
        string message,
        List<StaticDiagnostic> diagnostics)
    {
        // Strip comments and strings first
        var sanitizedLines = GetLinesWithoutCommentsAndStrings(code);

        int depth = 0;
        int lastStartLine = 1;

        var startPattern = new Regex($@"\b{startKeyword}\b", RegexOptions.IgnoreCase);
        var endPattern = new Regex($@"\b{endKeyword}\b", RegexOptions.IgnoreCase);

        // Disambiguate FUNCTION vs FUNCTION_BLOCK, END_FUNCTION vs END_FUNCTION_BLOCK
        bool isFunctionOnly = string.Equals(startKeyword, "FUNCTION", StringComparison.OrdinalIgnoreCase);

        for (int i = 0; i < sanitizedLines.Count; i++)
        {
            var line = sanitizedLines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (isFunctionOnly && Regex.IsMatch(line, @"\bFUNCTION_BLOCK\b", RegexOptions.IgnoreCase))
            {
                continue;
            }

            var startMatches = startPattern.Matches(line);
            var endMatches = endPattern.Matches(line);

            foreach (Match m in startMatches)
            {
                if (isFunctionOnly && line.Substring(m.Index).StartsWith("FUNCTION_BLOCK", StringComparison.OrdinalIgnoreCase))
                    continue;
                depth++;
                lastStartLine = i + 1;
            }

            foreach (Match m in endMatches)
            {
                if (isFunctionOnly && line.Substring(m.Index).StartsWith("END_FUNCTION_BLOCK", StringComparison.OrdinalIgnoreCase))
                    continue;
                depth--;
                if (depth < 0)
                {
                    diagnostics.Add(new StaticDiagnostic(
                        ruleCode,
                        $"Unexpected '{endKeyword}' without matching '{startKeyword}'.",
                        DiagnosticSeverity.Error,
                        i + 1,
                        m.Index + 1,
                        "BlockBalance"));
                    depth = 0;
                }
            }
        }

        if (depth > 0)
        {
            diagnostics.Add(new StaticDiagnostic(
                ruleCode,
                $"{message}. Missing '{endKeyword}'. Opened around line {lastStartLine}.",
                DiagnosticSeverity.Error,
                lastStartLine,
                1,
                "BlockBalance"));
        }
    }

    private static void CheckUnclosedComments(string code, List<StaticDiagnostic> diagnostics)
    {
        int openIdx = 0;
        while ((openIdx = code.IndexOf("(*", openIdx, StringComparison.Ordinal)) != -1)
        {
            int closeIdx = code.IndexOf("*)", openIdx + 2, StringComparison.Ordinal);
            if (closeIdx == -1)
            {
                int line = GetLineNumber(code, openIdx);
                diagnostics.Add(new StaticDiagnostic(
                    "ST010",
                    "Unclosed comment '(*'. Expected '*)'.",
                    DiagnosticSeverity.Error,
                    line,
                    1,
                    "CommentBalance"));
                break;
            }
            openIdx = closeIdx + 2;
        }
    }

    private static void CheckParenthesisBalance(string code, List<StaticDiagnostic> diagnostics)
    {
        var sanitizedLines = GetLinesWithoutCommentsAndStrings(code);
        int balance = 0;
        for (int i = 0; i < sanitizedLines.Count; i++)
        {
            var line = sanitizedLines[i];
            for (int col = 0; col < line.Length; col++)
            {
                char c = line[col];
                if (c == '(') balance++;
                else if (c == ')')
                {
                    balance--;
                    if (balance < 0)
                    {
                        diagnostics.Add(new StaticDiagnostic(
                            "ST011",
                            "Unexpected closing parenthesis ')'.",
                            DiagnosticSeverity.Error,
                            i + 1,
                            col + 1,
                            "ParenthesisBalance"));
                        balance = 0;
                    }
                }
            }
        }

        if (balance > 0)
        {
            diagnostics.Add(new StaticDiagnostic(
                "ST012",
                $"{balance} unclosed opening parenthesis '('.",
                DiagnosticSeverity.Error,
                sanitizedLines.Count,
                1,
                "ParenthesisBalance"));
        }
    }

    private static void CheckSemicolonTerminators(string code, List<StaticDiagnostic> diagnostics)
    {
        var sanitizedLines = GetLinesWithoutCommentsAndStrings(code);
        for (int i = 0; i < sanitizedLines.Count; i++)
        {
            var raw = sanitizedLines[i].Trim();
            if (string.IsNullOrEmpty(raw)) continue;

            // Lines that typically do NOT end with semicolon:
            // IF ... THEN, FOR ... DO, WHILE ... DO, CASE ... OF, REPEAT, VAR, END_VAR, FUNCTION, END_IF etc.
            if (raw.EndsWith("THEN", StringComparison.OrdinalIgnoreCase) ||
                raw.EndsWith("DO", StringComparison.OrdinalIgnoreCase) ||
                raw.EndsWith("OF", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("VAR", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("END_", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("PROGRAM", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("FUNCTION", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("TYPE", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("STRUCT", StringComparison.OrdinalIgnoreCase) ||
                raw.EndsWith("BEGIN", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("REPEAT", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("ELSE", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("ELSIF", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Statements containing assignment ':=' or function calls should normally terminate with ';'
            if (raw.Contains(":=") && !raw.EndsWith(";"))
            {
                // Peek next line to see if it's a multiline statement
                if (i + 1 < sanitizedLines.Count && sanitizedLines[i + 1].Trim().StartsWith(";"))
                {
                    continue;
                }

                diagnostics.Add(new StaticDiagnostic(
                    "ST013",
                    $"Possible missing semicolon ';' at end of statement: '{raw}'.",
                    DiagnosticSeverity.Warning,
                    i + 1,
                    raw.Length,
                    "StatementTermination"));
            }
        }
    }

    private static void CheckSuspiciousAssignments(string code, List<StaticDiagnostic> diagnostics)
    {
        // Detect := inside IF condition (which is invalid in ST)
        var lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Regex.IsMatch(line, @"\bIF\b.*:=.*THEN", RegexOptions.IgnoreCase))
            {
                diagnostics.Add(new StaticDiagnostic(
                    "ST014",
                    "Suspicious assignment operator ':=' detected in IF condition. Did you mean '='?",
                    DiagnosticSeverity.Warning,
                    i + 1,
                    1,
                    "SyntaxConvention"));
            }
        }
    }

    private static List<string> GetLinesWithoutCommentsAndStrings(string code)
    {
        // Replace string literals '...' with spaces
        string noStrings = Regex.Replace(code, @"'[^']*'", m => new string(' ', m.Length));
        // Replace block comments (* ... *) with spaces
        string noBlockComments = Regex.Replace(noStrings, @"\(\*.*?\*\)", m => new string(' ', m.Length), RegexOptions.Singleline);
        // Replace single line comments // ... with spaces
        string noLineComments = Regex.Replace(noBlockComments, @"//.*$", "", RegexOptions.Multiline);

        return noLineComments.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
    }

    private static int GetLineNumber(string text, int charIndex)
    {
        int line = 1;
        for (int i = 0; i < charIndex && i < text.Length; i++)
        {
            if (text[i] == '\n') line++;
        }
        return line;
    }
}
