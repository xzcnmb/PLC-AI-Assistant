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
/// control characters, module names colliding with KV reserved words) and
/// import warnings (FB instances, timer numbers, device code plausibility,
/// bare device designators in the body).
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

    // Curated KV reserved words for module-name checking. KV STUDIO is case-insensitive and
    // rejects names colliding with any of them when registering a variable or module
    // (vendor dialog "与预约语重复" / "reserved word duplicate"). Composition:
    // - observed rejected on KV STUDIO 12.42 (2026-11-04 local test): 'b' (link relay B), 'out';
    // - same-family single-letter device class names: C counter, T timer, V, W link register,
    //   X input, Y output, M internal relay, R edge relay, plus D;
    // - multi-letter KV device classes: DM data memory, EM expanded memory, SD special data;
    // - KV ST keywords: IEC 61131-3 structure/declaration keywords (OUT/IN/VAR*/IF/...),
    //   operators, literals and standard type names.
    // This is a deliberately conservative list, not a vendor-exhaustive one.
    public static readonly string[] KvReservedWords =
    [
        // Device class names (case-insensitive exact matches).
        "B", "C", "D", "DM", "EM", "M", "R", "SD", "T", "V", "W", "X", "Y",
        // IO / flow-control keywords.
        "IN", "INPUT", "OUT", "OUTPUT",
        "IF", "THEN", "ELSIF", "ELSE", "END_IF",
        "FOR", "TO", "DO", "BY", "WHILE", "END_WHILE",
        "REPEAT", "UNTIL", "END_REPEAT",
        "CASE", "END_CASE",
        "RETURN", "EXIT", "GOTO",
        "AND", "OR", "XOR", "NOT", "MOD",
        "TRUE", "FALSE", "NULL",
        // Declaration keywords (same words the body check bans as wrappers).
        "VAR", "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT", "VAR_GLOBAL", "VAR_TEMP", "VAR_STAT",
        "VAR_ACCESS", "END_VAR",
        "PROGRAM", "END_PROGRAM", "FUNCTION", "END_FUNCTION",
        "FUNCTION_BLOCK", "END_FUNCTION_BLOCK",
        "TYPE", "END_TYPE", "STRUCT", "END_STRUCT", "ARRAY", "OF", "AT",
        "CONSTANT", "INITIALIZE", "END_INITIALIZE",
        // Standard data type names.
        "BOOL", "BYTE", "WORD", "DWORD", "SINT", "INT", "DINT",
        "USINT", "UINT", "UDINT", "REAL", "LREAL", "TIME", "STRING"
    ];

    private static readonly HashSet<string> KvReservedWordSet =
        new(KvReservedWords, StringComparer.OrdinalIgnoreCase);

    // Bare device designators ("prefix class + digits", e.g. DM100, W0, B1) are address
    // aliases, not identifiers: KV-ST reaches devices only through variable-table
    // bindings, so they never legally appear in the ST body. Longest classes first so
    // alternation prefers DM/EM/SD over their single-letter prefixes.
    private static readonly Regex DeviceDesignatorPattern = new(
        @"\b(?:DM|EM|SD|B|C|D|M|R|T|V|W|X|Y)\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Assignment targets (identifier left of ':=') are the variable names KV STUDIO
    // registers in the variable table; they may not collide with reserved words or be
    // bare device designators. Captured from the comment/string-stripped body.
    private static readonly Regex AssignmentTargetPattern = new(
        @"\b([A-Za-z_][A-Za-z0-9_]*)\s*:=", RegexOptions.Compiled);

    // Header/area lines of the container itself must never appear in the source body.
    private static readonly string[] InjectionPrefixes = ["DEVICE:", "MODULE:", "MODULE_TYPE:"];
    private static readonly string[] InjectionExactLines = ["AREA_ST", "ENDH"];

    private static readonly Regex FbCallPattern = new(@"\b(?:FBEXEC|FBEAND)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimerPattern = new(@"\b(?:TMR|TMH|TMS|TMU)\s*\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ControlCharPattern = new("[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]", RegexOptions.Compiled);

    /// <summary>
    /// Builds the MNM container for one module. Validation order: module name shape,
    /// module name reserved-word/device-name collision, device code, executable
    /// content, declaration wrappers, container injection, control characters, module
    /// name ':' check, ST-body variable names (every ':=' assignment target colliding
    /// with a reserved word or being a bare device designator fails the build; verified
    /// on KV STUDIO 12.42 for 'out :='). The first failure is returned as Ok=false with
    /// Error set and an empty payload; otherwise all applicable warnings are attached to
    /// an Ok=true result (including bare device designators found in the statement body
    /// and a count of missing ';' terminators auto-appended to assignment lines during
    /// container build).
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

        // 2. Module names are registered case-insensitively by KV STUDIO and a collision
        //    with a reserved word or a device designator is rejected there ("reserved word
        //    duplicate", verified on KV STUDIO 12.42 for 'b' and 'out'), so refuse it hard.
        if (KvReservedWordSet.Contains(moduleName))
        {
            return Fail(moduleName, moduleType, deviceCode, encodingName,
                $"Module name '{moduleName}' collides with a KV reserved word (matching is case-insensitive); " +
                "KV STUDIO rejects it at registration. Use a descriptive name (e.g. 'MotorCtl' instead of 'out').");
        }

        if (DeviceDesignatorPattern.IsMatch(moduleName))
        {
            return Fail(moduleName, moduleType, deviceCode, encodingName,
                $"Module name '{moduleName}' is a KV device designator (class letters + number, e.g. DM100/W0/B1); " +
                "device names are reserved by KV STUDIO. Declare a variable bound to the device instead.");
        }

        // 3. Device code range (must come from the target project's own export).
        if (deviceCode is < 0 or > 255)
        {
            return Fail(moduleName, moduleType, deviceCode, encodingName,
                $"DeviceCode {deviceCode} must be 0..255 (take it from the target project's own MNM export; " +
                "known-good: 60 KV-X520, 59 KV-X310, 63 BOOL ladder example).");
        }

        // 4. Comments and string literals stripped, something executable must remain.
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

        // 5. Declaration wrappers belong in KV STUDIO variable tables, not the body.
        foreach (var word in DeclarationWrapperWords)
        {
            if (Regex.IsMatch(stripped, @"\b" + word + @"\b", RegexOptions.IgnoreCase))
            {
                return Fail(moduleName, moduleType, deviceCode, encodingName,
                    $"ST declaration wrapper '{word}' is not allowed in the program body; " +
                    "register declarations in KV STUDIO variable tables.");
            }
        }

        // 6. Container header/area lines must not be smuggled through the source.
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

        // 7. Control characters other than \t, \n, \r are unsupported.
        if (ControlCharPattern.IsMatch(source))
        {
            return Fail(moduleName, moduleType, deviceCode, encodingName,
                "Unsupported control character in source.");
        }

        // 8. The module name is embedded literally into ";MODULE:<name>".
        if (moduleName.Contains(':', StringComparison.Ordinal))
        {
            return Fail(moduleName, moduleType, deviceCode, encodingName,
                "Module name must not contain ':'.");
        }

        // 9. ST-body variable names: every assignment target (identifier left of ':=')
        //    becomes a KV STUDIO variable-table entry. KV STUDIO is case-insensitive and
        //    rejects names colliding with a reserved word at variable registration
        //    ("与预约语重复"; 'out' verified rejected on KV STUDIO 12.42 in the
        //    2026-11-06 live run - it failed the whole compile), so refuse them hard.
        //    A bare device designator on the left of ':=' is likewise illegal: KV-ST
        //    forbids direct device-address assignment outside variable bindings.
        foreach (Match target in AssignmentTargetPattern.Matches(stripped))
        {
            string name = target.Groups[1].Value;
            if (KvReservedWordSet.Contains(name))
            {
                return Fail(moduleName, moduleType, deviceCode, encodingName,
                    $"ST variable name '{name}' (assignment target before ':=') collides with a KV reserved word " +
                    "(matching is case-insensitive), e.g. 'out'/'b'; KV STUDIO rejects it at variable registration " +
                    "(\"与预约语重复\" / reserved word duplicate) and the module then fails to compile. " +
                    "Rename the variable ('sw'/'result' were verified safe on KV STUDIO 12.42).");
            }

            if (Regex.IsMatch(name, @"^(?:DM|EM|SD|B|C|D|M|R|T|V|W|X|Y)\d+$", RegexOptions.IgnoreCase))
            {
                return Fail(moduleName, moduleType, deviceCode, encodingName,
                    $"ST variable name '{name}' (assignment target before ':=') is a bare KV device designator " +
                    "(class letters + number, e.g. DM100/W0/B1); KV-ST does not allow assigning device " +
                    "addresses directly - declare a variable bound to the device in the variable table instead.");
            }
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

        // Bare device designators in statements (comments/strings already stripped) are
        // reported as a warning, not an error: most are true KV-ST violations (device
        // access must go through a variable-table device binding), but the offline
        // heuristic cannot see the variable table, so a user variable that happens to
        // spell class-letters + digits (e.g. a constant named 'C100') would also match.
        // Failing the build outright would reject sources the vendor compiler may accept.
        var deviceHits = DeviceDesignatorPattern.Matches(stripped);
        if (deviceHits.Count > 0)
        {
            var names = deviceHits.Select(m => m.Value.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Take(5);
            warnings.Add("Bare device designator(s) in body (" + string.Join(", ", names) +
                         (deviceHits.Count > 5 ? ", ..." : "") +
                         "): KV-ST should access devices only through variable-table entries " +
                         "with a device binding; direct DM100/W0/B1-style addresses in the body usually " +
                         "fail to compile or collide with the local variable scope.");
        }

        var lines = new List<string>
        {
            $"DEVICE:{deviceCode}",
            $";MODULE:{moduleName}",
            $";MODULE_TYPE:{moduleType}",
            "AREA_ST"
        };
        // Statement-terminator normalization (KV STUDIO 12.42 live finding 2026-11-06):
        // KV-ST requires ';' at the end of every executable statement, and AI/user-written
        // ST bodies routinely omit it, which makes the whole 转换 (compile) fail. Fix the
        // container body conservatively - see NormalizeStatementSemicolons. Comment/string
        // blanking is position-preserving (including line breaks), so the stripped text
        // splits into the same line count and indexes align one-to-one.
        var rawBodyLines = GetLogicalLines(source);
        var bodyLines = NormalizeStatementSemicolons(rawBodyLines, GetLogicalLines(stripped), out int semicolonsFixed);
        foreach (var bodyLine in bodyLines)
        {
            lines.Add(";" + bodyLine);
        }
        if (semicolonsFixed > 0)
        {
            warnings.Add($"Auto-appended missing ';' to {semicolonsFixed} assignment line(s): KV-ST requires every " +
                         "executable statement to end with ';' (sources without the terminators fail the KV STUDIO " +
                         "conversion); the container body was normalized, keep the source terminated as well.");
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

    // Lines starting with these openers are compound-statement headers, never terminated
    // by ';' (e.g. 'FOR i := 1 TO 5 DO' contains ':=' but must not gain a terminator).
    private static readonly Regex StatementOpenerPattern = new(
        @"^(FOR|IF|WHILE|REPEAT|CASE|ELSIF|ELSE|END_IF|END_FOR|END_WHILE|END_REPEAT|END_CASE|RETURN|EXIT|GOTO)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Trailing words that mean the expression continues on the next line (multi-line
    // assignment); appending ';' to such a line would break the statement.
    private static readonly string[] ContinuationWords =
        ["AND", "OR", "XOR", "MOD", "TO", "BY", "DO", "OF", "THEN", "ELSE", "UNTIL", "NOT"];

    /// <summary>
    /// Conservative statement-terminator normalization of the container body lines.
    /// A ';' is appended only when all of the following hold for the line (comments and
    /// string literals blanked, so terminators are never inferred from commented-out or
    /// quoted text): the line contains ':=' (an assignment), does not already end with
    /// ';', is not a compound-statement header (IF/FOR/...), and ends like a complete
    /// statement (letter/digit/'_'/')' and not a continuation operator word). Block
    /// keywords and every other line shape are left byte-identical. The ';' is inserted
    /// before any trailing line comment so it stays outside the comment.
    /// </summary>
    private static string[] NormalizeStatementSemicolons(string[] bodyLines, string[] strippedLines, out int fixedCount)
    {
        fixedCount = 0;
        var result = new string[bodyLines.Length];
        for (int i = 0; i < bodyLines.Length; i++)
        {
            string line = bodyLines[i];
            string strippedLine = i < strippedLines.Length ? strippedLines[i] : line;
            string trimmed = strippedLine.Trim();
            if (trimmed.Length > 0
                && trimmed.Contains(":=", StringComparison.Ordinal)
                && !trimmed.EndsWith(';')
                && !StatementOpenerPattern.IsMatch(trimmed)
                && EndsLikeCompleteStatement(trimmed))
            {
                line = line.Insert(SemicolonInsertionIndex(line, strippedLine), ";");
                fixedCount++;
            }

            result[i] = line;
        }

        return result;
    }

    private static bool EndsLikeCompleteStatement(string trimmed)
    {
        char last = trimmed[trimmed.Length - 1];
        if (!(char.IsLetterOrDigit(last) || last == '_' || last == ')'))
        {
            return false;
        }

        if (char.IsLetter(last) || last == '_')
        {
            int start = trimmed.Length;
            while (start > 0 && (char.IsLetterOrDigit(trimmed[start - 1]) || trimmed[start - 1] == '_'))
            {
                start--;
            }

            string word = trimmed[start..];
            return !ContinuationWords.Any(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }

    // StripCommentsAndStrings preserves every character position (comments and quoted
    // values become spaces), so the last non-blank character of the stripped line is the
    // last real code character of the original line. The ';' goes right after it, which
    // keeps it outside trailing comments and after leading ones.
    private static int SemicolonInsertionIndex(string line, string strippedLine)
    {
        int k = strippedLine.Length - 1;
        while (k >= 0 && char.IsWhiteSpace(strippedLine[k]))
        {
            k--;
        }

        return Math.Min(k + 1, line.Length);
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