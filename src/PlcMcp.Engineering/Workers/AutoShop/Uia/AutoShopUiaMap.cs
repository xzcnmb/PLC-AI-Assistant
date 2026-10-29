namespace PlcMcp.Engineering.Workers.AutoShop.Uia;

/// <summary>
/// The AutoShop window/control map discovered by UIA reconnaissance on 2026-10-24 against
/// AutoShop V4.12.0.2 (PID observed 24492), on a 150%-scaled 2560x1600 desktop.
///
/// IMPORTANT: all rectangles are PHYSICAL pixels captured with the measuring process made
/// per-monitor DPI aware (SetProcessDpiAwarenessContext(-3)). On a differently-scaled desktop
/// these values must be re-derived (see <see cref="DpiContext"/>); treat them as a reference
/// layout, not constants. AutoShop is a standard MFC MDI application (main class
/// "Afx:00400000:8:...", MDIClient, Afx:ToolBar/Afx:ControlBar/Afx:StatusBar), NOT a pure DuiLib
/// shell, so many surfaces have real HWNDs even though the menu bar and toolbars are owner-drawn
/// CMFC controls with no per-item HWND and no HMENU (GetMenu == 0).
/// </summary>
public static class AutoShopUiaMap
{
    /// <summary>Main frame window title substring used to locate the app.</summary>
    public const string MainWindowTitle = "AutoShop V4.12.0.2";

    /// <summary>Main frame window class prefix (MFC frame).</summary>
    public const string MainWindowClassPrefix = "Afx:00400000";

    /// <summary>Process name of the AutoShop executable.</summary>
    public const string ProcessName = "AutoShop";

    /// <summary>DPI context for the host the map was captured on (150% scaling).</summary>
    public static DpiContext CapturedDpi { get; } = new(
        PhysicalWidth: 2560, PhysicalHeight: 1600,
        VirtualWidth: 1707, VirtualHeight: 1067);

    /// <summary>Top-level menus discovered on the menu bar, left to right, with accelerators.</summary>
    public static IReadOnlyList<MenuPath> TopLevelMenus { get; } = new[]
    {
        new MenuPath("文件", Array.Empty<string>(), new[] { "alt", "f" }),
        new MenuPath("编辑", Array.Empty<string>(), new[] { "alt", "e" }),
        new MenuPath("视图", Array.Empty<string>(), new[] { "alt", "v" }),
        new MenuPath("PLC", Array.Empty<string>(), new[] { "alt", "p" }),
        new MenuPath("工具", Array.Empty<string>(), new[] { "alt", "t" }),
        new MenuPath("帮助", Array.Empty<string>(), new[] { "alt", "h" }),
    };

    /// <summary>
    /// The New Project entry point. In standard AutoShop this is 文件 &gt; 新建工程; the wizard is a
    /// model-selection dialog. Ctrl+N is the accelerator that reached the app during probing
    /// (it triggered the one-time "first-use module preparation"); whether it maps to 新建工程
    /// on a fully-initialised install is UNVERIFIED because synthetic menu/mouse input is blocked.
    /// </summary>
    public static MenuPath NewProjectMenu { get; } =
        new("文件", new[] { "新建工程" }, new[] { "ctrl", "n" });

    /// <summary>Named control map (physical pixels). Self-drawn CMFC items have no per-item HWND.</summary>
    public static IReadOnlyDictionary<string, UiaControl> Controls { get; } =
        new Dictionary<string, UiaControl>(StringComparer.Ordinal)
        {
            ["MainFrame"] = new UiaControl("MainFrame", MainWindowClassPrefix, new ScreenRect(39, 39, 1959, 1142), "AutoShop V4.12.0.2", true),
            ["MenuBar"] = new UiaControl("MenuBar", "Afx:ToolBar", new ScreenRect(51, 86, 1947, 128), "菜单", false),
            ["StandardToolbar"] = new UiaControl("StandardToolbar", "Afx:ToolBar", new ScreenRect(51, 128, 731, 173), "标准工具栏", false),
            ["CompileToolbar"] = new UiaControl("CompileToolbar", "Afx:ToolBar", new ScreenRect(731, 128, 827, 173), "编译工具栏", false),
            ["PlcToolbar"] = new UiaControl("PlcToolbar", "Afx:ToolBar", new ScreenRect(827, 128, 1718, 173), "PLC工具栏", false),
            ["LadderToolbar"] = new UiaControl("LadderToolbar", "Afx:ToolBar", new ScreenRect(639, 173, 1446, 218), "梯形图工具栏", false),
            ["SfcToolbar"] = new UiaControl("SfcToolbar", "Afx:ToolBar", new ScreenRect(51, 173, 639, 218), "顺序功能图", false),
            ["CommToolbar"] = new UiaControl("CommToolbar", "Afx:ToolBar", new ScreenRect(1446, 173, 1601, 218), string.Empty, false),
            ["VariableShareToolbar"] = new UiaControl("VariableShareToolbar", "Afx:ToolBar", new ScreenRect(1718, 128, 1948, 173), "变量共享", false),
            ["ProjectTree"] = new UiaControl("ProjectTree", "SysTreeView32", new ScreenRect(53, 260, 350, 863), string.Empty, true),
            ["ProjectPane"] = new UiaControl("ProjectPane", "Afx:ControlBar", new ScreenRect(51, 218, 351, 864), "工程管理", true),
            ["OutputPane"] = new UiaControl("OutputPane", "Afx:ControlBar", new ScreenRect(51, 870, 1947, 1095), "信息输出窗口", true),
            ["OutputList"] = new UiaControl("OutputList", "ListBox", new ScreenRect(56, 915, 1943, 1065), string.Empty, true),
            ["StatusBar"] = new UiaControl("StatusBar", "Afx:StatusBar", new ScreenRect(51, 1095, 1947, 1130), "就绪", true),
            ["MdiClient"] = new UiaControl("MdiClient", "MDIClient", new ScreenRect(357, 218, 1947, 864), string.Empty, true),
            ["CommLocalButton"] = new UiaControl("CommLocalButton", "Button", new ScreenRect(1460, 180, 1523, 210), "本地", true),
            ["CommUsbButton"] = new UiaControl("CommUsbButton", "Button", new ScreenRect(1532, 180, 1576, 210), "USB", true),
            ["VariableShareStatus"] = new UiaControl("VariableShareStatus", "Button", new ScreenRect(1731, 135, 1855, 165), "变量共享: 未连接", true),
        };

    /// <summary>Look up a named control, or null when absent.</summary>
    public static UiaControl? Control(string name) =>
        Controls.TryGetValue(name, out UiaControl? control) ? control : null;

    /// <summary>
    /// The compile result is expected in the information/output window message list
    /// (<see cref="Controls"/>["OutputList"], class ListBox) and/or the status bar. The list is a
    /// real HWND readable via LB_GETTEXT; the status bar via SB_GETTEXT. Compile is triggered from
    /// the Compile toolbar (a.k.a. 编译 &gt; 全部编译) or its accelerator (commonly F7; UNVERIFIED
    /// here because the app ignores synthetic input).
    /// </summary>
    public static IReadOnlyList<string> CompileResultSources { get; } = new[] { "OutputList", "StatusBar" };
}
