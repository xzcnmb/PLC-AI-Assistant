namespace PlcMcp.Engineering.Workers.AutoShop.Uia;

/// <summary> Tunables and key bindings for the AutoShop UIA automation layer.</summary>
public sealed class AutoShopUiaOptions
{
    /// <summary>AutoShop process id to scope window queries; null means any process.</summary>
    public int? ProcessId { get; init; }

    /// <summary>How long to wait for a compile to finish before reading output.</summary>
    public TimeSpan CompileTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>How long to wait for a dialog/wizard to appear.</summary>
    public TimeSpan DialogTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Accelerator chord for compile (commonly F7; UNVERIFIED on this host).</summary>
    public IReadOnlyList<string> CompileAcceleratorKeys { get; init; } = new[] { "f7" };

    /// <summary>Accelerator chord that reached the app during probing (triggers first-use prep / new).</summary>
    public IReadOnlyList<string> NewProjectAcceleratorKeys { get; init; } = new[] { "ctrl", "n" };

    /// <summary>Accelerator chord for open project.</summary>
    public IReadOnlyList<string> OpenProjectAcceleratorKeys { get; init; } = new[] { "ctrl", "o" };
}

/// <summary>Result of a project open/create operation.</summary>
/// <param name="Success">True only when the operation positively completed.</param>
/// <param name="Message">Human-readable outcome.</param>
/// <param name="Artifacts">Files/directories produced or opened, when known.</param>
/// <param name="Method">Input method used, when one was applied.</param>
/// <param name="Reason">Reason for a non-success outcome.</param>
public sealed record ProjectOpResult(
    bool Success,
    string Message,
    IReadOnlyList<string> Artifacts,
    InputMethod? Method,
    string? Reason)
{
    /// <summary>Build a blocked/failed result (never a fake success).</summary>
    public static ProjectOpResult Fail(string message, string? reason, InputMethod? method = null) =>
        new(false, message, Array.Empty<string>(), method, reason);
}

/// <summary>
/// Triggers an AutoShop compile and captures the result. Chooses an input method from the live
/// <see cref="InputCapabilities"/>; when no effective channel exists it returns an unconfirmed
/// result instead of pretending the compile ran. On success it reads the information/output
/// window list and the status bar (both native HWNDs) and parses them via
/// <see cref="CompileOutputParser"/>.
/// </summary>
public sealed class CompileTrigger
{
    private readonly IInputSender _input;
    private readonly ITextReader _text;
    private readonly IWindowLocator _windows;
    private readonly AutoShopUiaOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Create a compile trigger.</summary>
    public CompileTrigger(
        IInputSender input,
        ITextReader text,
        IWindowLocator windows,
        AutoShopUiaOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _options = options ?? new AutoShopUiaOptions();
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    }

    /// <summary>Trigger a compile and return the parsed result.</summary>
    public async Task<CompileTriggerResult> TriggerAsync(CancellationToken cancellationToken = default)
    {
        (bool delivered, InputMethod? method) = Deliver();
        if (method is null || !delivered)
        {
            return CompileTriggerResult.Unconfirmed(
                method ?? InputMethod.KeyboardAccelerator,
                CaptureConfidence.Exact,
                method is null
                    ? "no effective input channel: synthetic mouse is blocked on this host and menu-keyboard navigation is unavailable"
                    : "compile trigger could not be delivered");
        }

        await _delay(_options.CompileTimeout, cancellationToken).ConfigureAwait(false);

        string? raw = ReadCompileOutput();
        CaptureConfidence confidence = raw is null ? CaptureConfidence.Exact : CaptureConfidence.Exact;
        return CompileOutputParser.Parse(raw, method.Value, confidence);
    }

    private (bool Delivered, InputMethod? Method) Deliver()
    {
        InputMethod? accel = InputPlanner.ChooseForAcceleratedAction(_input.Capabilities);
        if (accel == InputMethod.KeyboardAccelerator && _input.TrySendAccelerator(_options.CompileAcceleratorKeys))
        {
            return (true, InputMethod.KeyboardAccelerator);
        }

        InputMethod? menu = InputPlanner.ChooseForMenuAction(_input.Capabilities);
        if (menu == InputMethod.KeyboardMenuNavigation)
        {
            // 编译 > 全部编译 via Alt+C then arrows/enter is host-specific; best-effort.
            return (_input.TrySendAccelerator(new[] { "alt", "c" }), InputMethod.KeyboardMenuNavigation);
        }

        InputMethod? click = InputPlanner.ChooseForCoordinateClick(_input.Capabilities);
        if (click is not null)
        {
            UiaControl? toolbar = AutoShopUiaMap.Control("CompileToolbar");
            if (toolbar is not null && _input.TryClick(toolbar.Rect))
            {
                return (true, click.Value);
            }
        }

        return (false, accel ?? menu ?? click);
    }

    private string? ReadCompileOutput()
    {
        var parts = new List<string>();

        UiaWindow? list = _windows.FindFirst(new WindowQuery(_options.ProcessId, "ListBox", null, true));
        if (list is not null)
        {
            parts.AddRange(_text.ReadListBoxItems(list.Handle));
        }

        UiaWindow? status = _windows.FindFirst(new WindowQuery(_options.ProcessId, "StatusBar", null, true))
                             ?? _windows.FindFirst(new WindowQuery(_options.ProcessId, "Afx:StatusBar", null, true));
        if (status is not null)
        {
            string? statusText = _text.ReadStatusBarText(status.Handle);
            if (!string.IsNullOrWhiteSpace(statusText))
            {
                parts.Add(statusText);
            }
        }

        // Filter blank lines; keep order.
        var nonEmpty = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return nonEmpty.Count == 0 ? null : string.Join('\n', nonEmpty);
    }
}

/// <summary>
/// Opens an existing AutoShop project. Given the observed input protection this is delivered via
/// the open-project accelerator (Ctrl+O) when available, then waits for the file dialog; the file
/// dialog's standard controls are driven/read through the injected services. Returns a blocked
/// result when no effective input channel exists rather than claiming the project opened.
/// </summary>
public sealed class ProjectOpener
{
    private readonly IInputSender _input;
    private readonly IWindowLocator _windows;
    private readonly IDialogWaiter _dialogs;
    private readonly AutoShopUiaOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Create a project opener.</summary>
    public ProjectOpener(
        IInputSender input,
        IWindowLocator windows,
        IDialogWaiter dialogs,
        AutoShopUiaOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _options = options ?? new AutoShopUiaOptions();
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    }

    /// <summary>Open a project by path. Returns a blocked result if input is unavailable.</summary>
    public async Task<ProjectOpResult> OpenAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return ProjectOpResult.Fail("open project", "project path is empty");
        }

        InputMethod? method = InputPlanner.ChooseForAcceleratedAction(_input.Capabilities);
        if (method != InputMethod.KeyboardAccelerator || !_input.TrySendAccelerator(_options.OpenProjectAcceleratorKeys))
        {
            return ProjectOpResult.Fail(
                $"open project '{projectPath}'",
                "no effective input channel to launch the open dialog (synthetic mouse blocked; Ctrl+O accelerator unavailable)",
                method);
        }

        UiaWindow? dialog = await _dialogs.WaitForAsync(
            new WindowQuery(_options.ProcessId, "#32770", null, true),
            _options.DialogTimeout,
            cancellationToken).ConfigureAwait(false);

        if (dialog is null)
        {
            return ProjectOpResult.Fail(
                $"open project '{projectPath}'",
                "open dialog did not appear within the timeout",
                method);
        }

        // A real run would type the path into the dialog's filename edit and press Enter, then
        // confirm the project tree repopulates. Without an effective way to set edit text we do
        // not claim the open succeeded.
        return ProjectOpResult.Fail(
            $"open project '{projectPath}'",
            "open dialog detected but path entry is not automatable with the current input capabilities",
            method);
    }
}

/// <summary>
/// Creates an empty H5U/H3U project via the New Project wizard. Documents and encodes the
/// intended flow (文件 &gt; 新建工程 &gt; select model &gt; set path &gt; finish). Because AutoShop
/// ignores synthetic mouse and menu-keyboard navigation on this host, actual wizard driving is
/// not possible here; the factory returns a blocked result with the exact steps recorded so a
/// human (or a fixed input path) can complete it, and so S1 knows where samples are expected.
/// </summary>
public sealed class SampleProjectFactory
{
    /// <summary>Supported AutoShop PLC model families for sample projects.</summary>
    public static readonly IReadOnlyList<string> KnownModels = new[] { "H5U", "H3U" };

    private readonly IInputSender _input;
    private readonly IWindowLocator _windows;
    private readonly IDialogWaiter _dialogs;
    private readonly AutoShopUiaOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Create a sample-project factory.</summary>
    public SampleProjectFactory(
        IInputSender input,
        IWindowLocator windows,
        IDialogWaiter dialogs,
        AutoShopUiaOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _options = options ?? new AutoShopUiaOptions();
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    }

    /// <summary>
    /// The recorded New Project flow. Exposed so callers/tests can assert the intended steps and
    /// so a human can follow them when automation input is blocked.
    /// </summary>
    public static IReadOnlyList<string> NewProjectSteps(string model) => new[]
    {
        $"Menu: {AutoShopUiaMap.NewProjectMenu.Breadcrumb}",
        "Wizard step 1: choose PLC series/family, select model '" + model + "'",
        "Wizard step 2: set project name and save location",
        "Wizard step 3: confirm/finish to create the empty project",
        "Save via 文件 > 保存 (Ctrl+S) to persist the ." + model.ToLowerInvariant() + "u project",
    };

    /// <summary>Create an empty project for the given model, saved under <paramref name="saveDirectory"/>.</summary>
    public async Task<ProjectOpResult> CreateEmptyAsync(string model, string saveDirectory, CancellationToken cancellationToken = default)
    {
        if (!KnownModels.Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            return ProjectOpResult.Fail($"create {model} sample", $"unknown model '{model}'; expected one of {string.Join("/", KnownModels)}");
        }

        if (string.IsNullOrWhiteSpace(saveDirectory))
        {
            return ProjectOpResult.Fail($"create {model} sample", "save directory is empty");
        }

        // The wizard is mouse/menu driven; on this host neither channel is effective.
        InputMethod? menuMethod = InputPlanner.ChooseForMenuAction(_input.Capabilities);
        InputMethod? clickMethod = InputPlanner.ChooseForCoordinateClick(_input.Capabilities);
        if (menuMethod is null && clickMethod is null)
        {
            return ProjectOpResult.Fail(
                $"create {model} sample under '{saveDirectory}'",
                "New Project wizard cannot be driven: synthetic mouse is blocked on this host and menu-keyboard navigation is unavailable. " +
                "Follow SampleProjectFactory.NewProjectSteps(model) manually, then re-run so S1 can consume the samples.",
                menuMethod ?? clickMethod);
        }

        // If a channel were available we would: trigger new project, wait for the wizard dialog,
        // select the model, set the path, finish, then save. Detecting the wizard:
        UiaWindow? wizard = await _dialogs.WaitForAsync(
            new WindowQuery(_options.ProcessId, "#32770", null, true),
            _options.DialogTimeout,
            cancellationToken).ConfigureAwait(false);

        if (wizard is null)
        {
            return ProjectOpResult.Fail(
                $"create {model} sample under '{saveDirectory}'",
                "New Project wizard did not appear within the timeout",
                menuMethod ?? clickMethod);
        }

        return ProjectOpResult.Fail(
            $"create {model} sample under '{saveDirectory}'",
            "wizard detected but model selection / path entry require effective input that is unavailable on this host",
            menuMethod ?? clickMethod);
    }
}

/// <summary>
/// Probes for an AutoShop online simulator / virtual PLC. On a host with effective menu
/// navigation it would open the online/simulation menu and look for the simulator entry; here it
/// records what was observed from static reconnaissance (the "变量共享" toolbar button and the
/// InoVarSharedService endpoint belong to agent B's ITP bridge, not a PLC simulator) and returns a
/// blocked report when the menu cannot be reached.
/// </summary>
public sealed class SimulatorProbe
{
    private readonly IInputSender _input;
    private readonly IWindowLocator _windows;
    private readonly AutoShopUiaOptions _options;

    /// <summary>Create a simulator probe.</summary>
    public SimulatorProbe(IInputSender input, IWindowLocator windows, AutoShopUiaOptions? options = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _options = options ?? new AutoShopUiaOptions();
    }

    /// <summary>Probe for a simulator. Never claims a verified simulator without positive evidence.</summary>
    public Task<SimulatorReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Static evidence gathered during reconnaissance (see the UIA map doc):
        //  - No menu could be opened (input protection), so the online/simulation menu is unread.
        //  - The "变量共享" (variable sharing) toolbar button + InoVarSharedService 127.0.0.1:13939
        //    are the ITP shared-variable service (agent B), NOT an H5U online PLC simulator.
        //  - The comm toolbar exposes 本地/USB channel buttons (no simulator endpoint seen).
        InputMethod? menuMethod = InputPlanner.ChooseForMenuAction(_input.Capabilities);
        if (menuMethod is null)
        {
            return Task.FromResult(SimulatorReport.Blocked(
                "AutoShop online/simulation menu could not be opened: synthetic mouse is blocked and " +
                "menu-keyboard navigation is unavailable on this host, so simulator presence is unverified. " +
                "Static reconnaissance found no simulator endpoint; the 变量共享 button and InoVarSharedService " +
                "127.0.0.1:13939 are the ITP shared-variable service (not a PLC simulator)."));
        }

        // A future effective path would open PLC/在线 menu and search for 在线模拟/仿真 entries.
        return Task.FromResult(new SimulatorReport(
            Exists: false,
            HowTo: null,
            Endpoint: null,
            Verified: false,
            Evidence: "menu navigation available but no simulator entry confirmed",
            Notes: "probe completed without positive simulator evidence"));
    }
}
