using System.Globalization;
using PlcMcp.Engineering.Workers.AutoShop.Uia;

namespace PlcMcp.Engineering.Tests;

/// <summary>
/// Offline unit tests for the AutoShop UIA automation layer. Every test uses injected fakes for
/// window location / input / text reading / dialog waiting, so nothing here touches a live
/// AutoShop install. The pure logic under test (coordinate/DPI transforms, window matching,
/// input-method planning, text-capture selection, compile-output parsing) is what the real-UI
/// scripts in tools/ depend on.
/// </summary>
public class AutoShopUiaTests
{
    // ---------------------------------------------------------------
    // ScreenRect
    // ---------------------------------------------------------------

    [Fact]
    public void ScreenRect_DerivesSizeCenterAndContainment()
    {
        var r = new ScreenRect(100, 200, 300, 260);
        Assert.Equal(200, r.Width);
        Assert.Equal(60, r.Height);
        Assert.True(r.IsValid);
        Assert.Equal(200, r.CenterX);
        Assert.Equal(230, r.CenterY);
        Assert.True(r.Contains(150, 230));
        Assert.False(r.Contains(300, 230)); // right edge exclusive
        Assert.False(r.Contains(99, 230));
    }

    [Fact]
    public void ScreenRect_InvalidWhenNonPositiveArea()
    {
        Assert.False(new ScreenRect(10, 10, 10, 20).IsValid);
        Assert.False(new ScreenRect(10, 10, 5, 5).IsValid);
    }

    [Fact]
    public void ScreenRect_Scale_RejectsNonPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenRect(0, 0, 10, 10).Scale(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenRect(0, 0, 10, 10).ScaleXY(1, -1));
    }

    // ---------------------------------------------------------------
    // DpiContext (150% scaled desktop: 2560x1600 physical vs 1707x1067 virtual)
    // ---------------------------------------------------------------

    [Fact]
    public void DpiContext_Scale_IsAboutOnePointFive()
    {
        var dpi = AutoShopUiaMap.CapturedDpi;
        Assert.InRange(dpi.ScaleX, 1.49, 1.51);
        Assert.InRange(dpi.ScaleY, 1.49, 1.51);
        Assert.False(dpi.IsUnity);
    }

    [Fact]
    public void DpiContext_Unity_WhenSpacesMatch()
    {
        var dpi = new DpiContext(1920, 1080, 1920, 1080);
        Assert.True(dpi.IsUnity);
        var r = new ScreenRect(10, 20, 110, 70);
        Assert.Equal(r, dpi.ToPhysical(r));
        Assert.Equal(r, dpi.ToVirtual(r));
    }

    [Fact]
    public void DpiContext_VirtualToPhysical_MapsMenuBarApproximately()
    {
        // A DPI-unaware process saw the menu bar at (34,57,1298,85); physical should be ~1.5x.
        var dpi = AutoShopUiaMap.CapturedDpi;
        var physical = dpi.ToPhysical(new ScreenRect(34, 57, 1298, 85));
        Assert.InRange(physical.Left, 49, 53);
        Assert.InRange(physical.Top, 83, 87);
        Assert.InRange(physical.Right, 1944, 1950);
        Assert.InRange(physical.Bottom, 126, 130);
    }

    [Fact]
    public void DpiContext_RoundTrip_PreservesRectangleWithinOnePixel()
    {
        var dpi = AutoShopUiaMap.CapturedDpi;
        var original = new ScreenRect(51, 86, 1947, 128);
        var round = dpi.ToVirtual(dpi.ToPhysical(original));
        Assert.InRange(round.Left, original.Left - 1, original.Left + 1);
        Assert.InRange(round.Top, original.Top - 1, original.Top + 1);
        Assert.InRange(round.Right, original.Right - 1, original.Right + 1);
        Assert.InRange(round.Bottom, original.Bottom - 1, original.Bottom + 1);
    }

    [Fact]
    public void DpiContext_PointTransforms()
    {
        var dpi = AutoShopUiaMap.CapturedDpi;
        (int px, int py) = dpi.ToPhysicalPoint(100, 100);
        Assert.InRange(px, 148, 152);
        Assert.InRange(py, 148, 152);
    }

    // ---------------------------------------------------------------
    // UiaWindow / WindowQuery
    // ---------------------------------------------------------------

    private static UiaWindow MakeWindow(string className, string title, bool visible = true, int pid = 24492) =>
        new((IntPtr)0x1000, pid, className, title, new ScreenRect(0, 0, 10, 10), visible);

    [Fact]
    public void WindowQuery_MatchesOnAllProvidedFragments()
    {
        var q = new WindowQuery(24492, "ListBox", null, true);
        Assert.True(q.Matches(MakeWindow("ListBox", "output")));
        Assert.False(q.Matches(MakeWindow("Button", "output")));         // class mismatch
        Assert.False(q.Matches(MakeWindow("ListBox", "x", pid: 1)));     // pid mismatch
        Assert.False(q.Matches(MakeWindow("ListBox", "x", visible: false))); // visible-only
    }

    [Fact]
    public void WindowQuery_NullFragmentsMatchAnything()
    {
        var q = WindowQuery.ForProcess(24492);
        Assert.True(q.Matches(MakeWindow("Anything", "whatever")));
        Assert.False(q.Matches(MakeWindow("Anything", "whatever", pid: 99)));
    }

    [Fact]
    public void WindowQuery_TitleContainsIsCaseInsensitive()
    {
        var q = new WindowQuery(null, null, "autoshop", false);
        Assert.True(q.Matches(MakeWindow("Afx", "AutoShop V4.12.0.2")));
    }

    // ---------------------------------------------------------------
    // InputPlanner (encodes the observed input-protection reality)
    // ---------------------------------------------------------------

    [Fact]
    public void InputPlanner_AllEffective_PrefersKeyboard()
    {
        Assert.Equal(InputMethod.KeyboardAccelerator, InputPlanner.ChooseForAcceleratedAction(InputCapabilities.AllEffective));
        Assert.Equal(InputMethod.KeyboardMenuNavigation, InputPlanner.ChooseForMenuAction(InputCapabilities.AllEffective));
        Assert.Equal(InputMethod.MouseClick, InputPlanner.ChooseForCoordinateClick(InputCapabilities.AllEffective));
    }

    [Fact]
    public void InputPlanner_ObservedAutoShop_AcceleratorOnly()
    {
        // Mouse blocked, accelerator works, menu navigation does not.
        Assert.Equal(InputMethod.KeyboardAccelerator, InputPlanner.ChooseForAcceleratedAction(InputCapabilities.ObservedAutoShop));
        Assert.Null(InputPlanner.ChooseForMenuAction(InputCapabilities.ObservedAutoShop));
        Assert.Null(InputPlanner.ChooseForCoordinateClick(InputCapabilities.ObservedAutoShop));
    }

    [Fact]
    public void InputPlanner_NothingEffective_ReturnsNull()
    {
        var none = new InputCapabilities(false, false, false);
        Assert.False(none.AnyEffective);
        Assert.Null(InputPlanner.ChooseForAcceleratedAction(none));
        Assert.Null(InputPlanner.ChooseForMenuAction(none));
        Assert.Null(InputPlanner.ChooseForCoordinateClick(none));
    }

    [Fact]
    public void InputPlanner_MouseOnly_FallsBackToClick()
    {
        var mouseOnly = new InputCapabilities(true, false, false);
        Assert.Equal(InputMethod.MouseClick, InputPlanner.ChooseForAcceleratedAction(mouseOnly));
        Assert.Equal(InputMethod.MouseClick, InputPlanner.ChooseForMenuAction(mouseOnly));
        Assert.Equal(InputMethod.MouseClick, InputPlanner.ChooseForCoordinateClick(mouseOnly));
    }

    // ---------------------------------------------------------------
    // TextCaptureSelector
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("ListBox", TextCaptureStrategy.ListBoxText)]
    [InlineData("Afx:StatusBar:400000:8:10003:10", TextCaptureStrategy.StatusBarText)]
    [InlineData("Button", TextCaptureStrategy.WindowText)]
    [InlineData("Static", TextCaptureStrategy.WindowText)]
    [InlineData("Edit", TextCaptureStrategy.WindowText)]
    [InlineData("SysListView32", TextCaptureStrategy.WindowText)]
    [InlineData("SysTreeView32", TextCaptureStrategy.WindowText)]
    [InlineData("#32770", TextCaptureStrategy.WindowText)]
    [InlineData("Afx:ToolBar:400000:8:10003:10", TextCaptureStrategy.Ocr)]  // self-drawn CMFC
    [InlineData("MDIClient", TextCaptureStrategy.Ocr)]
    [InlineData("", TextCaptureStrategy.Ocr)]
    public void TextCaptureSelector_MapsClassToStrategy(string className, TextCaptureStrategy expected)
    {
        Assert.Equal(expected, TextCaptureSelector.ForClass(className));
    }

    // ---------------------------------------------------------------
    // VirtualKeys
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("ctrl", 0x11)]
    [InlineData("alt", 0x12)]
    [InlineData("shift", 0x10)]
    [InlineData("enter", 0x0D)]
    [InlineData("f7", 0x76)]
    [InlineData("f10", 0x79)]
    [InlineData("n", 0x4E)]
    [InlineData("N", 0x4E)]
    [InlineData("5", 0x35)]
    public void VirtualKeys_ParsesKnownTokens(string token, ushort expected)
    {
        Assert.True(VirtualKeys.TryParse(token, out ushort vk));
        Assert.Equal(expected, vk);
    }

    [Fact]
    public void VirtualKeys_RejectsUnknown()
    {
        Assert.False(VirtualKeys.TryParse("bogus", out _));
        Assert.False(VirtualKeys.TryParse("", out _));
    }

    // ---------------------------------------------------------------
    // CompileOutputParser (pure, the core "did it compile?" decision)
    // ---------------------------------------------------------------

    [Fact]
    public void CompileParser_ChineseSuccess_IsSucceeded()
    {
        var result = CompileOutputParser.Parse("编译通过\n0 错误 0 警告", InputMethod.KeyboardAccelerator, CaptureConfidence.Exact);
        Assert.True(result.Confirmed);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void CompileParser_EnglishSuccess_IsSucceeded()
    {
        var result = CompileOutputParser.Parse("Compile OK", InputMethod.KeyboardAccelerator, CaptureConfidence.Exact);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void CompileParser_EmptyOutput_IsUnconfirmedNotSuccess()
    {
        var result = CompileOutputParser.Parse("   ", InputMethod.KeyboardAccelerator, CaptureConfidence.Exact);
        Assert.False(result.Confirmed);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void CompileParser_NullOutput_IsUnconfirmed()
    {
        var result = CompileOutputParser.Parse(null, InputMethod.KeyboardAccelerator, CaptureConfidence.Exact);
        Assert.False(result.Confirmed);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void CompileParser_ErrorLine_IsFailedWithDiagnostic()
    {
        var result = CompileOutputParser.Parse("主程序(12): 错误: 语法错误", InputMethod.KeyboardAccelerator, CaptureConfidence.Exact);
        Assert.True(result.Confirmed);
        Assert.False(result.Succeeded);
        CompileDiagnostic err = Assert.Single(result.Errors);
        Assert.Contains("语法错误", err.Text, StringComparison.Ordinal);
        Assert.Equal("12", err.Location);
    }

    [Fact]
    public void CompileParser_FailureMarker_IsFailed()
    {
        var result = CompileOutputParser.Parse("编译失败", InputMethod.KeyboardAccelerator, CaptureConfidence.Exact);
        Assert.True(result.Confirmed);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void CompileParser_WarningOnly_IsSucceededWithWarning()
    {
        var result = CompileOutputParser.Parse("编译通过\n网络1: 警告: 未使用的变量", InputMethod.KeyboardAccelerator, CaptureConfidence.Exact);
        Assert.True(result.Succeeded);
        CompileDiagnostic warn = Assert.Single(result.Warnings);
        Assert.Contains("未使用的变量", warn.Text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------
    // DialogWaiter (fake locator + instant delay)
    // ---------------------------------------------------------------

    [Fact]
    public async Task DialogWaiter_ReturnsWindow_WhenItAppears()
    {
        var seq = new Queue<UiaWindow?>(new UiaWindow?[] { null, null, MakeWindow("#32770", "向导") });
        var locator = new FakeWindowLocator(() => seq.Count > 0 ? seq.Dequeue() : MakeWindow("#32770", "向导"));
        var waiter = new DialogWaiter(locator, TimeSpan.FromMilliseconds(1), (_, _) => Task.CompletedTask);

        UiaWindow? found = await waiter.WaitForAsync(new WindowQuery(24492, "#32770", null, true), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal("向导", found!.Title);
    }

    [Fact]
    public async Task DialogWaiter_ReturnsNull_OnTimeout()
    {
        var locator = new FakeWindowLocator(() => null);
        var waiter = new DialogWaiter(locator, TimeSpan.FromMilliseconds(5), (_, _) => Task.CompletedTask);

        UiaWindow? found = await waiter.WaitForAsync(new WindowQuery(24492, "#32770", null, true), TimeSpan.FromMilliseconds(60), CancellationToken.None);
        Assert.Null(found);
    }

    // ---------------------------------------------------------------
    // CompileTrigger orchestration with fakes
    // ---------------------------------------------------------------

    [Fact]
    public async Task CompileTrigger_NoEffectiveChannel_IsUnconfirmed()
    {
        var input = new FakeInputSender(new InputCapabilities(false, false, false));
        var trigger = new CompileTrigger(input, new FakeTextReader(), new FakeWindowLocator(() => null), delay: (_, _) => Task.CompletedTask);

        CompileTriggerResult result = await trigger.TriggerAsync();
        Assert.False(result.Confirmed);
        Assert.False(result.Succeeded);
        Assert.Contains("no effective input channel", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompileTrigger_AcceleratorDelivered_SuccessCaptured()
    {
        var input = new FakeInputSender(InputCapabilities.ObservedAutoShop) { AcceleratorResult = true };
        var text = new FakeTextReader { ListItems = new[] { "开始编译...", "编译通过" } };
        var windows = new FakeWindowLocator(() => MakeWindow("ListBox", ""));
        var trigger = new CompileTrigger(input, text, windows, delay: (_, _) => Task.CompletedTask);

        CompileTriggerResult result = await trigger.TriggerAsync();
        Assert.True(result.Succeeded);
        Assert.True(result.Confirmed);
        Assert.Contains("f7", input.LastAccelerator ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompileTrigger_AcceleratorDelivered_ErrorCaptured()
    {
        var input = new FakeInputSender(InputCapabilities.ObservedAutoShop) { AcceleratorResult = true };
        var text = new FakeTextReader { ListItems = new[] { "编译失败", "主程序(3): 错误: 未知符号" } };
        var windows = new FakeWindowLocator(() => MakeWindow("ListBox", ""));
        var trigger = new CompileTrigger(input, text, windows, delay: (_, _) => Task.CompletedTask);

        CompileTriggerResult result = await trigger.TriggerAsync();
        Assert.False(result.Succeeded);
        Assert.True(result.Confirmed);
        Assert.Contains(result.Errors, d => d.Text.Contains("未知符号", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompileTrigger_DeliveredButNoOutput_IsUnconfirmed()
    {
        var input = new FakeInputSender(InputCapabilities.ObservedAutoShop) { AcceleratorResult = true };
        var text = new FakeTextReader { ListItems = Array.Empty<string>() };
        var windows = new FakeWindowLocator(() => null); // no output window found
        var trigger = new CompileTrigger(input, text, windows, delay: (_, _) => Task.CompletedTask);

        CompileTriggerResult result = await trigger.TriggerAsync();
        Assert.False(result.Succeeded);
        Assert.False(result.Confirmed);
    }

    // ---------------------------------------------------------------
    // ProjectOpener / SampleProjectFactory / SimulatorProbe blocked paths
    // ---------------------------------------------------------------

    [Fact]
    public async Task ProjectOpener_NoEffectiveChannel_IsBlocked()
    {
        var opener = new ProjectOpener(new FakeInputSender(new InputCapabilities(false, false, false)), new FakeWindowLocator(() => null), new FakeDialogWaiter(() => null));
        ProjectOpResult result = await opener.OpenAsync(@"D:\x\p.h5u");
        Assert.False(result.Success);
        Assert.Contains("no effective input channel", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SampleProjectFactory_UnknownModel_Rejected()
    {
        var factory = new SampleProjectFactory(new FakeInputSender(InputCapabilities.ObservedAutoShop), new FakeWindowLocator(() => null), new FakeDialogWaiter(() => null));
        ProjectOpResult result = await factory.CreateEmptyAsync("H9U", @"C:\tmp\samples");
        Assert.False(result.Success);
        Assert.Contains("unknown model", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SampleProjectFactory_BlockedChannel_RecordsSteps()
    {
        var factory = new SampleProjectFactory(new FakeInputSender(new InputCapabilities(false, false, false)), new FakeWindowLocator(() => null), new FakeDialogWaiter(() => null));
        ProjectOpResult result = await factory.CreateEmptyAsync("H5U", @"C:\tmp\samples");
        Assert.False(result.Success);
        Assert.Contains("cannot be driven", result.Reason, StringComparison.OrdinalIgnoreCase);
        // The recorded steps still document the intended flow for a human / fixed input path.
        Assert.Contains(SampleProjectFactory.NewProjectSteps("H5U"), s => s.Contains("H5U", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SimulatorProbe_BlockedChannel_IsNotVerified()
    {
        var probe = new SimulatorProbe(new FakeInputSender(new InputCapabilities(false, false, false)), new FakeWindowLocator(() => null));
        SimulatorReport report = await probe.ProbeAsync();
        Assert.False(report.Exists);
        Assert.False(report.Verified);
        Assert.NotNull(report.Notes);
    }

    [Fact]
    public void UiaMap_ExposesExpectedControls()
    {
        Assert.NotNull(AutoShopUiaMap.Control("CompileToolbar"));
        Assert.NotNull(AutoShopUiaMap.Control("OutputList"));
        Assert.NotNull(AutoShopUiaMap.Control("VariableShareToolbar"));
        Assert.Null(AutoShopUiaMap.Control("DoesNotExist"));
        Assert.Equal(6, AutoShopUiaMap.TopLevelMenus.Count);
        Assert.Contains(AutoShopUiaMap.CompileResultSources, s => s == "OutputList");
    }

    // ---------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------

    private sealed class FakeInputSender : IInputSender
    {
        public FakeInputSender(InputCapabilities capabilities) => Capabilities = capabilities;

        public InputCapabilities Capabilities { get; }

        public bool AcceleratorResult { get; set; }

        public bool ClickResult { get; set; }

        public string? LastAccelerator { get; private set; }

        public bool TrySendAccelerator(IReadOnlyList<string> keys)
        {
            LastAccelerator = string.Join('+', keys);
            return AcceleratorResult;
        }

        public bool TryClick(ScreenRect target) => ClickResult;

        public bool TryPostClick(IntPtr hwnd, int clientX, int clientY) => ClickResult;
    }

    private sealed class FakeTextReader : ITextReader
    {
        public IReadOnlyList<string> ListItems { get; set; } = Array.Empty<string>();

        public string? WindowText { get; set; }

        public string? ReadWindowText(IntPtr hwnd) => WindowText;

        public IReadOnlyList<string> ReadListBoxItems(IntPtr hwnd) => ListItems;

        public string? ReadStatusBarText(IntPtr hwnd) => WindowText;
    }

    private sealed class FakeWindowLocator : IWindowLocator
    {
        private readonly Func<UiaWindow?> _factory;

        public FakeWindowLocator(Func<UiaWindow?> factory) => _factory = factory;

        public IReadOnlyList<UiaWindow> Find(WindowQuery query)
        {
            UiaWindow? w = _factory();
            return w is not null && query.Matches(w) ? new[] { w } : Array.Empty<UiaWindow>();
        }

        public UiaWindow? FindFirst(WindowQuery query)
        {
            UiaWindow? w = _factory();
            return w is not null && query.Matches(w) ? w : null;
        }
    }

    private sealed class FakeDialogWaiter : IDialogWaiter
    {
        private readonly Func<UiaWindow?> _factory;

        public FakeDialogWaiter(Func<UiaWindow?> factory) => _factory = factory;

        public Task<UiaWindow?> WaitForAsync(WindowQuery query, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(_factory());
    }
}
