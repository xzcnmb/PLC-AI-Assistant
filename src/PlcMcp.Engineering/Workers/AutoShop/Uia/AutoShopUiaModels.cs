using System.Globalization;

namespace PlcMcp.Engineering.Workers.AutoShop.Uia;

/// <summary>
/// Axis-aligned rectangle in a single coordinate space (either physical pixels or the
/// DPI-virtualized space a DPI-unaware process observes). All values are inclusive Left/Top,
/// exclusive Right/Bottom, matching Win32 RECT semantics used by GetWindowRect.
/// </summary>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
/// <param name="Right">Right edge (exclusive).</param>
/// <param name="Bottom">Bottom edge (exclusive).</param>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Width in pixels.</summary>
    public int Width => Right - Left;

    /// <summary>Height in pixels.</summary>
    public int Height => Bottom - Top;

    /// <summary>True when the rectangle has positive area.</summary>
    public bool IsValid => Width > 0 && Height > 0;

    /// <summary>Horizontal centre, rounded down.</summary>
    public int CenterX => Left + (Width / 2);

    /// <summary>Vertical centre, rounded down.</summary>
    public int CenterY => Top + (Height / 2);

    /// <summary>True when the point lies inside the rectangle.</summary>
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>Scale the rectangle by a single positive factor on both axes.</summary>
    public ScreenRect Scale(double factor) => ScaleXY(factor, factor);

    /// <summary>Scale the rectangle by independent X and Y factors (used for DPI transforms).</summary>
    public ScreenRect ScaleXY(double fx, double fy)
    {
        if (fx <= 0 || fy <= 0)
        {
            throw new ArgumentOutOfRangeException("DPI scale factors must be positive.");
        }

        return new ScreenRect(
            (int)Math.Floor(Left * fx),
            (int)Math.Floor(Top * fy),
            (int)Math.Ceiling(Right * fx),
            (int)Math.Ceiling(Bottom * fy));
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}");
}

/// <summary>
/// DPI context for a process. AutoShop runs on a 150% scaled desktop: a DPI-unaware process
/// observes a virtualized ~1707x1067 space while the physical desktop is 2560x1600. Every
/// coordinate handed to (or read from) the Win32/UIA layer must be interpreted in the right
/// space, otherwise clicks land ~1.5x off and screenshots are blurry. Callers must make their
/// measuring process per-monitor DPI aware (SetProcessDpiAwarenessContext(-3)) so GetWindowRect
/// and CopyFromScreen both report physical pixels that line up with synthetic-input coordinates.
/// </summary>
/// <param name="PhysicalWidth">Physical desktop width in pixels (e.g. 2560).</param>
/// <param name="PhysicalHeight">Physical desktop height in pixels (e.g. 1600).</param>
/// <param name="VirtualWidth">DPI-virtualized width a DPI-unaware process observes (e.g. 1707).</param>
/// <param name="VirtualHeight">DPI-virtualized height a DPI-unaware process observes (e.g. 1067).</param>
public readonly record struct DpiContext(int PhysicalWidth, int PhysicalHeight, int VirtualWidth, int VirtualHeight)
{
    /// <summary>Ratio physical/virtual on the X axis.</summary>
    public double ScaleX => VirtualWidth == 0 ? 1.0 : (double)PhysicalWidth / VirtualWidth;

    /// <summary>Ratio physical/virtual on the Y axis.</summary>
    public double ScaleY => VirtualHeight == 0 ? 1.0 : (double)PhysicalHeight / VirtualHeight;

    /// <summary>True when the physical and virtual spaces match (no scaling).</summary>
    public bool IsUnity => Math.Abs(ScaleX - 1.0) < 1e-9 && Math.Abs(ScaleY - 1.0) < 1e-9;

    /// <summary>Convert a rectangle from the virtualized space to physical pixels.</summary>
    public ScreenRect ToPhysical(ScreenRect rect) => rect.ScaleXY(ScaleX, ScaleY);

    /// <summary>Convert a rectangle from physical pixels to the virtualized space.</summary>
    public ScreenRect ToVirtual(ScreenRect rect)
    {
        double fx = ScaleX == 0 ? 1.0 : 1.0 / ScaleX;
        double fy = ScaleY == 0 ? 1.0 : 1.0 / ScaleY;
        return rect.ScaleXY(fx, fy);
    }

    /// <summary>Convert a point from the virtualized space to physical pixels.</summary>
    public (int X, int Y) ToPhysicalPoint(int x, int y) =>
        ((int)Math.Round(x * ScaleX), (int)Math.Round(y * ScaleY));

    /// <summary>Convert a point from physical pixels to the virtualized space.</summary>
    public (int X, int Y) ToVirtualPoint(int x, int y) =>
        ((int)Math.Round(x / (ScaleX == 0 ? 1.0 : ScaleX)), (int)Math.Round(y / (ScaleY == 0 ? 1.0 : ScaleY)));
}

/// <summary>A discovered top-level or child window with its class, title and rectangle.</summary>
/// <param name="Handle">Native window handle.</param>
/// <param name="ProcessId">Owning process id.</param>
/// <param name="ClassName">Win32 window class name.</param>
/// <param name="Title">Window text (caption or control label).</param>
/// <param name="Rect">Rectangle in the finder's coordinate space.</param>
/// <param name="Visible">Whether the window is currently visible.</param>
public sealed record UiaWindow(
    IntPtr Handle,
    int ProcessId,
    string ClassName,
    string Title,
    ScreenRect Rect,
    bool Visible)
{
    /// <summary>True when the class name contains the given fragment (ordinal, case-insensitive).</summary>
    public bool ClassContains(string fragment) =>
        !string.IsNullOrEmpty(fragment) && ClassName.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the title contains the given fragment (ordinal, case-insensitive).</summary>
    public bool TitleContains(string fragment) =>
        !string.IsNullOrEmpty(fragment) && Title.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A single discovered control/menu element of interest with a human label.</summary>
/// <param name="Name">Stable logical name used by the automation layer (e.g. "FileMenu", "CompileToolbar").</param>
/// <param name="ClassName">Win32 class of the backing window, if any.</param>
/// <param name="Rect">Rectangle in physical pixels.</param>
/// <param name="Text">Last observed text, if any.</param>
/// <param name="HasNativeHandle">False for self-drawn (CMFC/DuiLib) elements with no per-item HWND.</param>
public sealed record UiaControl(
    string Name,
    string ClassName,
    ScreenRect Rect,
    string? Text,
    bool HasNativeHandle);

/// <summary>
/// A menu path expressed as the ordered top-level menu plus optional item labels, with the
/// keyboard accelerator sequence that reaches it. Accelerators are the primary navigation
/// mechanism because AutoShop ignores synthetic mouse input (see the UIA map doc).
/// </summary>
/// <param name="TopLevel">Top-level menu label in the original locale (e.g. "文件").</param>
/// <param name="Items">Ordered submenu item labels, empty for a top-level action.</param>
/// <param name="AcceleratorKeys">Ordered key tokens to send (e.g. ["ctrl", "n"]).</param>
public sealed record MenuPath(string TopLevel, IReadOnlyList<string> Items, IReadOnlyList<string> AcceleratorKeys)
{
    /// <summary>Full breadcrumb label, e.g. "文件 &gt; 新建工程".</summary>
    public string Breadcrumb => Items.Count == 0 ? TopLevel : $"{TopLevel} > {string.Join(" > ", Items)}";
}

/// <summary>Predicate for locating windows. All non-null fragments must match (ordinal, case-insensitive).</summary>
/// <param name="ProcessId">Restrict to a process id, or null for any.</param>
/// <param name="ClassNameContains">Required class-name fragment, or null.</param>
/// <param name="TitleContains">Required title fragment, or null.</param>
/// <param name="VisibleOnly">When true, only visible windows match.</param>
public sealed record WindowQuery(int? ProcessId, string? ClassNameContains, string? TitleContains, bool VisibleOnly)
{
    /// <summary>A query matching every window of a process.</summary>
    public static WindowQuery ForProcess(int processId) => new(processId, null, null, false);

    /// <summary>Pure predicate: does the given window satisfy this query?</summary>
    public bool Matches(UiaWindow window)
    {
        if (window is null)
        {
            return false;
        }

        if (ProcessId is int pid && window.ProcessId != pid)
        {
            return false;
        }

        if (VisibleOnly && !window.Visible)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(ClassNameContains) && !window.ClassContains(ClassNameContains))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(TitleContains) && !window.TitleContains(TitleContains))
        {
            return false;
        }

        return true;
    }
}

/// <summary>How the automation layer intends to deliver an interaction to AutoShop.</summary>
public enum InputMethod
{
    /// <summary>Real or injected mouse click at a rectangle centre.</summary>
    MouseClick,

    /// <summary>WM_LBUTTONDOWN/UP posted directly to a window handle (bypasses the input stream).</summary>
    MousePostMessage,

    /// <summary>Keyboard accelerator chord (e.g. Ctrl+N) routed through TranslateAccelerator.</summary>
    KeyboardAccelerator,

    /// <summary>Menu-bar navigation via Alt/mnemonic + arrow keys + Enter.</summary>
    KeyboardMenuNavigation,
}

/// <summary>
/// What input channels are actually usable against the target, learned from probing.
/// AutoShop on this host ignores synthetic mouse (SendInput, window-mcp Click and posted
/// WM_LBUTTON* all had zero effect) while a keyboard accelerator (Ctrl+N) did reach it, so the
/// planner prefers keyboard and refuses to claim success when only blocked channels remain.
/// </summary>
/// <param name="MouseInjectionEffective">True when injected mouse actually changes the UI.</param>
/// <param name="KeyboardAcceleratorEffective">True when accelerator keys reach the app.</param>
/// <param name="KeyboardMenuNavigationEffective">True when Alt/arrow menu navigation works.</param>
public sealed record InputCapabilities(
    bool MouseInjectionEffective,
    bool KeyboardAcceleratorEffective,
    bool KeyboardMenuNavigationEffective)
{
    /// <summary>Capabilities describing a fully responsive target.</summary>
    public static InputCapabilities AllEffective { get; } = new(true, true, true);

    /// <summary>Capabilities describing the observed AutoShop host (mouse blocked, accelerator only).</summary>
    public static InputCapabilities ObservedAutoShop { get; } = new(false, true, false);

    /// <summary>True when at least one channel can drive the UI.</summary>
    public bool AnyEffective => MouseInjectionEffective || KeyboardAcceleratorEffective || KeyboardMenuNavigationEffective;
}

/// <summary>
/// Pure planner that chooses the least-privileged effective input method for a desired action.
/// Returns null when no effective channel exists so callers surface an honest Unsupported result
/// instead of pretending an action succeeded.
/// </summary>
public static class InputPlanner
{
    /// <summary>Choose a method for triggering an action that has a known accelerator chord.</summary>
    public static InputMethod? ChooseForAcceleratedAction(InputCapabilities caps) =>
        caps.KeyboardAcceleratorEffective ? InputMethod.KeyboardAccelerator
        : caps.MouseInjectionEffective ? InputMethod.MouseClick
        : null;

    /// <summary>Choose a method for a menu-driven action (no direct accelerator known).</summary>
    public static InputMethod? ChooseForMenuAction(InputCapabilities caps) =>
        caps.KeyboardMenuNavigationEffective ? InputMethod.KeyboardMenuNavigation
        : caps.MouseInjectionEffective ? InputMethod.MouseClick
        : null;

    /// <summary>Choose a method for a plain coordinate click (toolbar button, wizard control).</summary>
    public static InputMethod? ChooseForCoordinateClick(InputCapabilities caps) =>
        caps.MouseInjectionEffective ? InputMethod.MouseClick
        : null;
}

/// <summary>How a piece of text was (or will be) read from the AutoShop UI.</summary>
public enum TextCaptureStrategy
{
    /// <summary>WM_GETTEXT / GetWindowText on a native control (dialogs, status-bar panes, labels).</summary>
    WindowText,

    /// <summary>LB_GETTEXT on a list box (the information/output window message list).</summary>
    ListBoxText,

    /// <summary>SB_GETTEXT on a status bar.</summary>
    StatusBarText,

    /// <summary>On-screen OCR of a self-drawn region (menus, toolbars, canvas).</summary>
    Ocr,
}

/// <summary>
/// Pure selector mapping a Win32 window class to the most reliable text-capture strategy.
/// Standard controls use their native message; self-drawn CMFC/DuiLib surfaces fall back to OCR.
/// </summary>
public static class TextCaptureSelector
{
    /// <summary>Pick a capture strategy for a window class. Returns <see cref="TextCaptureStrategy.Ocr"/> for self-drawn classes.</summary>
    public static TextCaptureStrategy ForClass(string className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return TextCaptureStrategy.Ocr;
        }

        // List boxes (the information/output window) are read item-by-item.
        if (className.Contains("ListBox", StringComparison.OrdinalIgnoreCase))
        {
            return TextCaptureStrategy.ListBoxText;
        }

        // MFC status bar (owner-drawn panes but exposes SB_GETTEXT).
        if (className.Contains("StatusBar", StringComparison.OrdinalIgnoreCase) ||
            className.StartsWith("Afx:StatusBar", StringComparison.OrdinalIgnoreCase))
        {
            return TextCaptureStrategy.StatusBarText;
        }

        // Standard Win32 controls with a real HWND carry their text via WM_GETTEXT.
        if (className is "Button" or "Static" or "Edit" or "ComboBox" or "#32770"
            || className.Contains("SysListView", StringComparison.OrdinalIgnoreCase)
            || className.Contains("SysTreeView", StringComparison.OrdinalIgnoreCase)
            || className.Contains("RichEdit", StringComparison.OrdinalIgnoreCase))
        {
            return TextCaptureStrategy.WindowText;
        }

        // Everything else (CMFC toolbars/menus, DuiLib skins, MDI canvas) is self-drawn -> OCR.
        return TextCaptureStrategy.Ocr;
    }
}

/// <summary>Confidence in a captured piece of text, surfaced so callers never over-trust it.</summary>
public enum CaptureConfidence
{
    /// <summary>Read from a native control message; exact.</summary>
    Exact,

    /// <summary>Read via OCR; may contain recognition errors.</summary>
    OcrApproximate,
}

/// <summary>A single compile diagnostic line.</summary>
/// <param name="Severity">"error", "warning" or "info".</param>
/// <param name="Text">Original diagnostic text.</param>
/// <param name="Location">Optional block/network/line location, when parseable.</param>
public sealed record CompileDiagnostic(string Severity, string Text, string? Location)
{
    /// <summary>True for error severity.</summary>
    public bool IsError => string.Equals(Severity, "error", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for warning severity.</summary>
    public bool IsWarning => string.Equals(Severity, "warning", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Result of a compile trigger. <see cref="Succeeded"/> is only true when the capture positively
/// confirms a successful compile; an unreadable/absent result yields Succeeded=false with a reason,
/// so the layer never fabricates success.
/// </summary>
/// <param name="Succeeded">True only on positive confirmation of a clean compile.</param>
/// <param name="Confirmed">True when the result text was actually captured and parsed.</param>
/// <param name="Method">Input method used to trigger the compile.</param>
/// <param name="Confidence">Confidence of the captured text.</param>
/// <param name="RawText">Raw captured output, when available.</param>
/// <param name="Diagnostics">Parsed error/warning/info lines.</param>
/// <param name="Reason">Human-readable reason when not confirmed or not succeeded.</param>
public sealed record CompileTriggerResult(
    bool Succeeded,
    bool Confirmed,
    InputMethod Method,
    CaptureConfidence Confidence,
    string? RawText,
    IReadOnlyList<CompileDiagnostic> Diagnostics,
    string? Reason)
{
    /// <summary>Error-level diagnostics only.</summary>
    public IEnumerable<CompileDiagnostic> Errors => Diagnostics.Where(d => d.IsError);

    /// <summary>Warning-level diagnostics only.</summary>
    public IEnumerable<CompileDiagnostic> Warnings => Diagnostics.Where(d => d.IsWarning);

    /// <summary>Build a "could not confirm" result (never a fake success).</summary>
    public static CompileTriggerResult Unconfirmed(InputMethod method, CaptureConfidence confidence, string reason) =>
        new(false, false, method, confidence, null, Array.Empty<CompileDiagnostic>(), reason);

    /// <summary>Build a confirmed failure result carrying parsed diagnostics.</summary>
    public static CompileTriggerResult Failed(InputMethod method, CaptureConfidence confidence, string raw, IReadOnlyList<CompileDiagnostic> diagnostics) =>
        new(false, true, method, confidence, raw, diagnostics, "compile reported diagnostics");
}

/// <summary>
/// Pure parser that turns captured compile-output text into a <see cref="CompileTriggerResult"/>.
/// Recognises the Chinese AutoShop/Inovance phrases for pass/fail plus generic "error"/"警告"
/// markers and optional "file(line)" or "[block]" locations. Deliberately conservative: unknown
/// text is treated as unconfirmed rather than success.
/// </summary>
public static class CompileOutputParser
{
    // Positive completion phrases observed/expected in Inovance compile output.
    private static readonly string[] SuccessMarkers =
    {
        "编译通过", "编译成功", "编译完成", "编译正常", "没有错误", "无错误", "0 错误", "Compile OK", "Compile succeeded",
    };

    // Explicit failure markers.
    private static readonly string[] FailureMarkers =
    {
        "编译失败", "编译错误", "存在错误", "有错误", "Compile failed", "Compile error",
    };

    // Zero-count summary phrases: "0 错误 0 警告" is a PASS line, not an error.
    private static readonly string[] ZeroErrorMarkers =
    {
        "0 错误", "0错误", "无错误", "没有错误", "0 error", "0 errors", "no error", "no errors",
    };

    private static readonly string[] ZeroWarningMarkers =
    {
        "0 警告", "0警告", "无警告", "没有警告", "0 warning", "0 warnings", "no warning", "no warnings",
    };

    /// <summary>Parse raw compile output. <paramref name="raw"/> may be null/empty.</summary>
    public static CompileTriggerResult Parse(string? raw, InputMethod method, CaptureConfidence confidence)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return CompileTriggerResult.Unconfirmed(method, confidence, "no compile output captured");
        }

        var diagnostics = new List<CompileDiagnostic>();
        bool sawError = false, sawWarning = false, sawFailureMarker = false, sawSuccessMarker = false;

        foreach (string rawLine in raw.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            foreach (string marker in FailureMarkers)
            {
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    sawFailureMarker = true;
                }
            }

            foreach (string marker in SuccessMarkers)
            {
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    sawSuccessMarker = true;
                }
            }

            bool zeroError = ZeroErrorMarkers.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase));
            bool zeroWarning = ZeroWarningMarkers.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase));

            string? severity = null;
            if (!zeroError && (line.Contains("错误", StringComparison.Ordinal) || line.Contains("error", StringComparison.OrdinalIgnoreCase)))
            {
                severity = "error";
                sawError = true;
            }
            else if (!zeroWarning && (line.Contains("警告", StringComparison.Ordinal) || line.Contains("warning", StringComparison.OrdinalIgnoreCase)))
            {
                severity = "warning";
                sawWarning = true;
            }

            if (severity is null)
            {
                continue;
            }

            // Do not double-count the summary pass line as an error.
            if (severity == "error" && SuccessMarkers.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase)) && !sawFailureMarker)
            {
                continue;
            }

            diagnostics.Add(new CompileDiagnostic(severity, line, ExtractLocation(line)));
        }

        bool succeeded = !sawFailureMarker && !sawError && (sawSuccessMarker || diagnostics.Count == 0 && !sawWarning);
        if (sawError || sawFailureMarker)
        {
            succeeded = false;
        }

        if (!succeeded)
        {
            return CompileTriggerResult.Failed(method, confidence, raw, diagnostics);
        }

        return new CompileTriggerResult(
            Succeeded: true,
            Confirmed: true,
            Method: method,
            Confidence: confidence,
            RawText: raw,
            Diagnostics: diagnostics,
            Reason: sawSuccessMarker ? "compile reported success" : "no diagnostics reported");
    }

    // Best-effort extraction of a "path(line)" / "path:line" / "[block]" location token.
    private static string? ExtractLocation(string line)
    {
        int open = line.IndexOf('(');
        int close = line.IndexOf(')');
        if (open >= 0 && close > open)
        {
            return line.Substring(open + 1, close - open - 1).Trim();
        }

        int colon = line.IndexOf(':');
        if (colon > 0 && colon < line.Length - 1 && char.IsDigit(line[colon + 1]))
        {
            int start = line.LastIndexOf(' ', colon);
            return line.Substring(start + 1, colon - start).Trim();
        }

        return null;
    }
}

/// <summary>Outcome of probing for an AutoShop online simulator / virtual PLC.</summary>
/// <param name="Exists">True when a simulator feature was positively found.</param>
/// <param name="HowTo">Menu path / steps to start it, when known.</param>
/// <param name="Endpoint">Virtual PLC endpoint (ip:port / transport), when known.</param>
/// <param name="Verified">True only when the simulator was started and confirmed running.</param>
/// <param name="Evidence">What was actually observed.</param>
/// <param name="Notes">Caveats, including when discovery was blocked by input protection.</param>
public sealed record SimulatorReport(
    bool Exists,
    string? HowTo,
    string? Endpoint,
    bool Verified,
    string? Evidence,
    string? Notes)
{
    /// <summary>A report stating discovery was blocked (honest non-result).</summary>
    public static SimulatorReport Blocked(string reason) =>
        new(false, null, null, false, null, reason);
}
