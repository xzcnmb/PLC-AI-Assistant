using System.Text;
using System.Text.RegularExpressions;

namespace PlcMcp.Engineering.Workers.Keyence;

// Offline builder for KV STUDIO MNM program-module containers (ST source embedding).
//
// The container grammar below was byte-verified against KV STUDIO 12G exports
// (documented by the upstream KeyenceAgent project, MIT): a line-oriented text
// envelope around the ST body, written as UTF-16LE + BOM by default.
//
// This is a pure string/byte transformation plus offline red-line prechecks. It
// does NOT talk to KV STUDIO, does NOT compile anything, and the prechecks are
// heuristics only - passing them does not imply the vendor compiler accepts the
// module, and importing still requires a deviceCode taken from the target
// project's own MNM export.

/// <summary>Offline MNM build result: exact container payload, or the first validation failure.</summary>
public sealed record KeyenceMnmResult(
    bool Ok,
    string ModuleName,
    int ModuleType,
    int DeviceCode,
    int SourceLines,
    string OutputEncoding,
    IReadOnlyList<string> Warnings,
    byte[] Payload,
    string? Error);

/// <summary>
/// Builds a KV STUDIO MNM program-module container from ST source text, with
/// offline red-line prechecks (declaration wrappers, container injection,
/// control characters) and import warnings (FB instances, timer numbers,
/// device code plausibility).
/// </summary>
public static class KeyenceMnmGenerator
{
    /// <summary>;MODULE_TYPE value for a scan program.</summary>
    public const int ModuleTypeScan = 0;

    /// <summary>;MODULE_TYPE value for a user function block.</summary>
    public const int ModuleTypeFunctionBlock = 2;

    /// <summary>Device codes observed in byte-verified KV STUDIO 12G exports.</summary>
    public static readonly int[] KnownDeviceCodes = [59, 60, 63];

    private const string ModuleNamePatternText = "^[A-Za-z_][A-Za-z0-9_]{0,47}$";

    private static readonly Regex ModuleNamePattern = new(ModuleNamePatternText, RegexOptions.Compiled);

    // Declaration wrappers live in KV STUDIO variable tables, never in the MNM body.
    // Order is the report order: the first word found in this list is named in the error.
    private static readonly string[] DeclarationWrapperWords =
    [
        "VAR", "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT", "VAR_GLOBAL", "VAR_TEMP",
        "VAR_ACCESS", "END_VAR", "PROGRAM", "END_PROGRAM", "FUNCTION_BLOCK",
        "END_FUNCTION_BLOCK", "FUNCTION", "END_FUNCTION"
    ];

    // Header/area lines of the container itself must never appear in the source body.
    private static readonly string[] InjectionPrefixes = ["DEVICE:", "MODULE:", "MODULE_TYPE:"];
    private static readonly string[] InjectionExactLines = ["AREA_ST", "ENDH"];

    private static readonly Regex FbCallPattern = new(@"\b(?:FBEXEC|FBEAND)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimerPattern = new(@"\b(?:TMR|TMH|TMS|TMU)\s*\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ControlCharPattern = new("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]", RegexOptions.Compiled);

    /// <summary>
    /// Builds the MNM container for one module. Validation order: module name,
    /// device code, executable content, declaration wrappers, container injection,
    /// control characters, module name ':' check. The first failure is returned as
    /// Ok=false with Error set and an empty payload; otherwise all applicable
    /// warnings are attached to an Ok=true result.
    /// </summary>
    public static KeyenceMnmResult Generate(string source, string moduleName, int deviceCode, bool functionBlock, bool utf16Le = true)
    {
        source ??= string.Empty;
        moduleName ??= string.Empty;
        int moduleType = functionBlock ? ModuleTypeFunctionBlock : ModuleTypeScan;
        string encodingName = utf16Le ? "Utf16Le" : "Utf8";

        // 1. Module name shape.
        if (!ModuleNamePattern.IsMatch(moduleName))
        {
            return Fail(moduleName, moduleType, deviceCode, encodingName,
                $"Module name '{moduleName}' is invalid ({ModuleNamePatternText}).");
        }

        // 2. Device code range (must come from the target project's own export).
        if (deviceCode is < 0 or > 255)
        {
            return Fail(moduleName, moduleType, deviceCode, encodingName,
                $"DeviceCode {deviceCode} must be 0..255 (take it from the target project's own MNM export; " +
                "known-good: 60 KV-X520, 59 KV-X310, 63 BOOL ladder example).");
        }

        // 3. Comments and string literals stripped, something executable must remain.
        string stripped;
        try
        {
            stripped = StripCommentsAndStrings(source);
        }
        catch (InvalidDataException ex)
        {
            return Fail(moduleName, moduleType, deviceCode, encodingName,
                "KV_ST_SOURCE_UNTERMINATED: " + ex.Message);
        }

        if (stripped.Trim().Length == 0)
        {
            return Fail(moduleName, moduleType, deviceCode, encodingName,
                "No executable ST statements (only comments/whitespace).");
        }

        // 4. Declaration wrappers belong in KV STUDIO variable tables, not the body.
        foreach (var word in DeclarationWrapperWords)
        {
            if (Regex.IsMatch(stripped, @"\b" + word + @"\b", RegexOptions.IgnoreCase))
            {
                return Fail(moduleName, moduleType, deviceCode, encodingName,
                    $"ST declaration wrapper '{word}' is not allowed in the program body; " +
                    "register declarations in KV STUDIO variable tables.");
            }
        }

        // 5. Container header/area lines must not be smuggled through the source.
        foreach (var rawLine in GetLogicalLines(source))
        {
            string line = rawLine.Trim();
            bool rejected = InjectionPrefixes.Any(p => line.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                            || InjectionExactLines.Any(e => string.Equals(line, e, StringComparison.OrdinalIgnoreCase));
            if (rejected)
            {
                return Fail(moduleName, moduleType, deviceCode, encodingName,
                    $"Container injection line rejected: '{line}'.");
            }
        }

        // 6. Control characters other than \t, \n, \r are unsupported.
        if (ControlCharPattern.IsMatch(source))
        {
            return Fail(moduleName, moduleType, deviceCode, encodingName,
                "Unsupported control character in source.");
        }

        // 7. The module name is embedded literally into ";MODULE:<name>".
        if (moduleName.Contains(':', StringComparison.Ordinal))
        {
            return Fail(moduleName, moduleType, deviceCode, encodingName,
                "Module name must not contain ':'.");
        }

        var warnings = new List<string>();
        if (FbCallPattern.IsMatch(stripped))
        {
            warnings.Add("FB call(s) found in body: register each FB instance variable in KV STUDIO " +
                         "(scope=local, owner_program=<module>, name=<instance>, data_type=<FB>) " +
                         "or compile fails [error 214].");
        }

        if (TimerPattern.IsMatch(stripped))
        {
            warnings.Add("Timer instruction with an explicit number found: do not reuse the same " +
                         "timer number in another module [error 61].");
        }

        if (!KnownDeviceCodes.Contains(deviceCode))
        {
            warnings.Add($"DeviceCode {deviceCode} is not in the known-good set [59,60,63]; " +
                         "take it from the target project's own export before importing.");
        }

        var lines = new List<string>
        {
            $"DEVICE:{deviceCode}",
            $";MODULE:{moduleName}",
            $";MODULE_TYPE:{moduleType}",
            "AREA_ST"
        };
        var bodyLines = GetLogicalLines(source);
        foreach (var bodyLine in bodyLines)
        {
            lines.Add(";" + bodyLine);
        }

        lines.Add("END");
        lines.Add("ENDH");
        string text = string.Join("\r\n", lines) + "\r\n";

        byte[] payload = utf16Le
            ? [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)]
            : Encoding.UTF8.GetBytes(text);

        return new KeyenceMnmResult(
            Ok: true,
            ModuleName: moduleName,
            ModuleType: moduleType,
            DeviceCode: deviceCode,
            SourceLines: bodyLines.Length,
            OutputEncoding: encodingName,
            Warnings: warnings,
            Payload: payload,
            Error: null);
    }

    /// <summary>Splits text into logical lines: \r\n and \r become \n, exactly one trailing \n is removed.</summary>
    private static string[] GetLogicalLines(string text)
    {
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        return normalized.Split('\n');
    }

    private static KeyenceMnmResult Fail(string moduleName, int moduleType, int deviceCode, string encodingName, string error)
        => new(
            Ok: false,
            ModuleName: moduleName,
            ModuleType: moduleType,
            DeviceCode: deviceCode,
            SourceLines: 0,
            OutputEncoding: encodingName,
            Warnings: [],
            Payload: [],
            Error: error);

    /// <summary>
    /// Single-pass replacement of line comments, nested C-style block comments and
    /// nested IEC-style comments with spaces, so word checks cannot fire on
    /// commented-out code. Quoted single- and double-quoted values are
    /// blanked too (backslash or $ escapes the next character; doubled quotes escape).
    /// Throws on an unterminated comment or quoted value.
    /// </summary>
    private static string StripCommentsAndStrings(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\r' && text[i] != '\n')
                {
                    sb.Append(' ');
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i = SkipNestedComment(sb, text, i, "/*", "*/");
                continue;
            }

            if (c == '(' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i = SkipNestedComment(sb, text, i, "(*", "*)");
                continue;
            }

            if (c == '\'' || c == '"')
            {
                i = SkipQuoted(sb, text, i);
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static int SkipNestedComment(StringBuilder sb, string text, int start, string open, string close)
    {
        int depth = 0;
        int i = start;
        while (i < text.Length)
        {
            if (Matches(text, i, open))
            {
                depth++;
                AppendSpaces(sb, open.Length);
                i += open.Length;
                continue;
            }

            if (Matches(text, i, close))
            {
                depth--;
                AppendSpaces(sb, close.Length);
                i += close.Length;
                if (depth == 0)
                {
                    return i;
                }

                continue;
            }

            char c = text[i];
            sb.Append(c == '\r' || c == '\n' ? c : ' ');
            i++;
        }

        throw new InvalidDataException("Unterminated ST comment or quoted value.");
    }

    private static int SkipQuoted(StringBuilder sb, string text, int start)
    {
        char quote = text[start];
        AppendSpaces(sb, 1);
        int i = start + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' || c == '$')
            {
                // Escape: this character and the next one are consumed.
                AppendSpaces(sb, 1);
                if (i + 1 < text.Length)
                {
                    AppendSpaces(sb, 1);
                    i += 2;
                }
                else
                {
                    i++;
                }

                continue;
            }

            if (c == quote)
            {
                if (i + 1 < text.Length && text[i + 1] == quote)
                {
                    // Doubled quote is an escaped quote, the string continues.
                    AppendSpaces(sb, 2);
                    i += 2;
                    continue;
                }

                AppendSpaces(sb, 1);
                return i + 1;
            }

            sb.Append(c == '\r' || c == '\n' ? c : ' ');
            i++;
        }

        throw new InvalidDataException("Unterminated ST comment or quoted value.");
    }

    private static bool Matches(string text, int index, string token)
        => index + token.Length <= text.Length && text.AsSpan(index, token.Length).SequenceEqual(token.AsSpan());

    private static void AppendSpaces(StringBuilder sb, int count)
    {
        for (int k = 0; k < count; k++)
        {
            sb.Append(' ');
        }
    }
}
