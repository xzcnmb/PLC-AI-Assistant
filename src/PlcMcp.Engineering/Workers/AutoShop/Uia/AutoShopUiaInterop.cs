using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

namespace PlcMcp.Engineering.Workers.AutoShop.Uia;

/// <summary>Locates AutoShop windows. Injectable so orchestration is testable without a live app.</summary>
public interface IWindowLocator
{
    /// <summary>All windows matching the query.</summary>
    IReadOnlyList<UiaWindow> Find(WindowQuery query);

    /// <summary>First matching window, or null.</summary>
    UiaWindow? FindFirst(WindowQuery query);
}

/// <summary>
/// Delivers input to AutoShop. <see cref="Capabilities"/> reflects what actually works on the
/// host; callers must consult it and refuse to claim success when nothing is effective.
/// </summary>
public interface IInputSender
{
    /// <summary>Observed/declared input capabilities.</summary>
    InputCapabilities Capabilities { get; }

    /// <summary>Send an accelerator chord such as ["ctrl","n"]. Returns true only if delivered.</summary>
    bool TrySendAccelerator(IReadOnlyList<string> keys);

    /// <summary>Click the centre of a rectangle (physical pixels). Returns true only if delivered.</summary>
    bool TryClick(ScreenRect target);

    /// <summary>Post a WM_LBUTTONDOWN/UP to a window at client coordinates. Returns true if posted.</summary>
    bool TryPostClick(IntPtr hwnd, int clientX, int clientY);
}

/// <summary>Reads text from native AutoShop controls. Injectable for tests.</summary>
public interface ITextReader
{
    /// <summary>WM_GETTEXT of a window/control.</summary>
    string? ReadWindowText(IntPtr hwnd);

    /// <summary>All items of a list box (LB_GETTEXT), e.g. the information/output window.</summary>
    IReadOnlyList<string> ReadListBoxItems(IntPtr hwnd);

    /// <summary>Status-bar pane text (SB_GETTEXT), when available.</summary>
    string? ReadStatusBarText(IntPtr hwnd);
}

/// <summary>Waits for a dialog/window to appear, honouring a timeout. Injectable for tests.</summary>
public interface IDialogWaiter
{
    /// <summary>Wait until a window matches <paramref name="query"/> or the timeout elapses.</summary>
    Task<UiaWindow?> WaitForAsync(WindowQuery query, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Real Win32 window locator over EnumWindows/EnumChildWindows. Requires Windows and the target
/// process; not exercised in CI. Coordinates are returned in the caller's DPI space, so callers
/// that need physical pixels must first call <see cref="Win32Dpi.SetPerMonitorAware"/>.
/// </summary>
public sealed class Win32WindowLocator : IWindowLocator
{
    /// <inheritdoc />
    public IReadOnlyList<UiaWindow> Find(WindowQuery query)
    {
        List<UiaWindow> results = new();
        EnumWindows((top, _) =>
        {
            TryDescribe(top, 0, results, query, includeChildren: true);
            return true;
        }, IntPtr.Zero);
        return results;
    }

    /// <inheritdoc />
    public UiaWindow? FindFirst(WindowQuery query)
    {
        UiaWindow? found = null;
        EnumWindows((top, _) =>
        {
            TryDescribe(top, 0, new List<UiaWindow>(), query, includeChildren: true);
            var scratch = new List<UiaWindow>();
            TryDescribe(top, 0, scratch, query, includeChildren: true);
            if (scratch.Count > 0)
            {
                found = scratch[0];
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static void TryDescribe(IntPtr hwnd, int depth, List<UiaWindow> results, WindowQuery query, bool includeChildren)
    {
        int pid;
        _ = GetWindowThreadProcessId(hwnd, out pid);
        string className = GetClassName(hwnd);
        string title = GetWindowText(hwnd);
        bool visible = IsWindowVisible(hwnd);
        _ = GetWindowRect(hwnd, out RECT rc);
        var window = new UiaWindow(hwnd, pid, className, title, new ScreenRect(rc.Left, rc.Top, rc.Right, rc.Bottom), visible);
        if (query.Matches(window))
        {
            results.Add(window);
        }

        if (includeChildren && depth < 4)
        {
            EnumChildWindows(hwnd, (child, _) =>
            {
                TryDescribe(child, depth + 1, results, query, includeChildren: true);
                return true;
            }, IntPtr.Zero);
        }
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        _ = Native.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        int len = Native.GetWindowTextLength(hwnd);
        if (len <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(len + 1);
        _ = Native.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private const int GWL_STYLE = -16;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hwnd, EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct RECT { public readonly int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rc);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
}

/// <summary>Real Win32 text reader. Requires Windows; not exercised in CI.</summary>
public sealed class Win32TextReader : ITextReader
{
    /// <inheritdoc />
    public string? ReadWindowText(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        int len = Native.SendMessage(hwnd, Native.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero).ToInt32();
        if (len <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(len + 1);
        _ = Native.SendMessage(hwnd, Native.WM_GETTEXT, (IntPtr)sb.Capacity, sb);
        return sb.ToString();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReadListBoxItems(IntPtr hwnd)
    {
        var items = new List<string>();
        if (hwnd == IntPtr.Zero)
        {
            return items;
        }

        int count = Native.SendMessage(hwnd, Native.LB_GETCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
        var sb = new StringBuilder(1024);
        for (int i = 0; i < count && i < 10000; i++)
        {
            sb.Clear();
            int len = Native.SendMessage(hwnd, Native.LB_GETTEXT, (IntPtr)i, sb).ToInt32();
            if (len >= 0)
            {
                items.Add(sb.ToString());
            }
        }

        return items;
    }

    /// <inheritdoc />
    public string? ReadStatusBarText(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        // Concatenate all pane texts (SB_GETTEXTLEN/SB_GETTEXT per part).
        int parts = Native.SendMessage(hwnd, Native.SB_GETPARTS, IntPtr.Zero, IntPtr.Zero).ToInt32();
        if (parts <= 0)
        {
            return ReadWindowText(hwnd);
        }

        var sb = new StringBuilder();
        var buffer = new StringBuilder(512);
        for (int i = 0; i < parts && i < 64; i++)
        {
            buffer.Clear();
            // SB_GETTEXT: wParam low word = part index, high word = max chars.
            IntPtr w = (IntPtr)(i | (buffer.Capacity << 16));
            _ = Native.SendMessage(hwnd, Native.SB_GETTEXT, w, buffer);
            if (buffer.Length > 0)
            {
                if (sb.Length > 0)
                {
                    sb.Append(" | ");
                }

                sb.Append(buffer);
            }
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }
}

/// <summary>
/// Real input sender. Delivers keyboard accelerators and (best-effort) mouse clicks/posts.
/// On hosts where AutoShop ignores synthetic mouse, <see cref="Capabilities"/> should be
/// constructed with MouseInjectionEffective=false so orchestration never claims a mouse success.
/// Requires Windows; not exercised in CI.
/// </summary>
public sealed class Win32InputSender : IInputSender
{
    /// <summary>Create a sender with explicit capabilities (from a prior probe).</summary>
    public Win32InputSender(InputCapabilities capabilities) => Capabilities = capabilities;

    /// <inheritdoc />
    public InputCapabilities Capabilities { get; }

    /// <inheritdoc />
    public bool TrySendAccelerator(IReadOnlyList<string> keys)
    {
        if (keys is null || keys.Count == 0)
        {
            return false;
        }

        var vks = new List<(ushort Vk, bool KeyUp)>();
        foreach (string raw in keys)
        {
            if (!VirtualKeys.TryParse(raw, out ushort vk))
            {
                return false;
            }

            vks.Add((vk, false));
        }

        // Modifiers down in order, then keys up in reverse (chord semantics).
        var inputs = new List<Native.INPUTK>();
        for (int i = 0; i < vks.Count; i++)
        {
            inputs.Add(Native.KeyInput(vks[i].Vk, false));
        }

        for (int i = vks.Count - 1; i >= 0; i--)
        {
            inputs.Add(Native.KeyInput(vks[i].Vk, true));
        }

        uint sent = Native.SendInputK((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.INPUTK>());
        return sent == inputs.Count;
    }

    /// <inheritdoc />
    public bool TryClick(ScreenRect target)
    {
        if (!target.IsValid)
        {
            return false;
        }

        return TryPostClickAtPhysical(target.CenterX, target.CenterY);
    }

    /// <inheritdoc />
    public bool TryPostClick(IntPtr hwnd, int clientX, int clientY)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        IntPtr l = (IntPtr)((clientY << 16) | (clientX & 0xFFFF));
        _ = Native.PostMessage(hwnd, Native.WM_LBUTTONDOWN, IntPtr.Zero, l);
        _ = Native.PostMessage(hwnd, Native.WM_LBUTTONUP, IntPtr.Zero, l);
        return true;
    }

    // Absolute (normalized) SendInput click at physical coordinates.
    private static bool TryPostClickAtPhysical(int x, int y)
    {
        int sw = Native.GetSystemMetrics(0);
        int sh = Native.GetSystemMetrics(1);
        if (sw <= 0 || sh <= 0)
        {
            return false;
        }

        int nx = (int)Math.Round(x * 65535.0 / (sw - 1));
        int ny = (int)Math.Round(y * 65535.0 / (sh - 1));
        Native.INPUTK[] inputs =
        {
            Native.MouseInput(Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE, nx, ny),
            Native.MouseInput(Native.MOUSEEVENTF_LEFTDOWN, nx, ny),
            Native.MouseInput(Native.MOUSEEVENTF_LEFTUP, nx, ny),
        };
        uint sent = Native.SendInputK((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUTK>());
        return sent == inputs.Length;
    }
}

/// <summary>
/// Polling dialog waiter built on an <see cref="IWindowLocator"/> and an injectable delay so the
/// timeout loop is unit-testable without a live app.
/// </summary>
public sealed class DialogWaiter : IDialogWaiter
{
    private readonly IWindowLocator _locator;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _pollInterval;

    /// <summary>Create a waiter.</summary>
    /// <param name="locator">Window source.</param>
    /// <param name="pollInterval">Poll cadence (default 250ms).</param>
    /// <param name="delay">Delay function; defaults to a real Task.Day. Injected in tests.</param>
    public DialogWaiter(IWindowLocator locator, TimeSpan? pollInterval = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    }

    /// <inheritdoc />
    public async Task<UiaWindow?> WaitForAsync(WindowQuery query, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UiaWindow? match = _locator.FindFirst(query);
            if (match is not null)
            {
                return match;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            TimeSpan wait = _pollInterval;
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining < wait)
            {
                wait = remaining;
            }

            if (wait <= TimeSpan.Zero)
            {
                return null;
            }

            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Minimal DPI helper so measurement processes observe physical pixels.</summary>
public static class Win32Dpi
{
    /// <summary>
    /// Make the current process per-monitor-v2 DPI aware so GetWindowRect/CopyFromScreen report
    /// physical pixels. Idempotent; safe to call at process start. Returns true on success.
    /// </summary>
    public static bool SetPerMonitorAware()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return Native.SetProcessDpiAwarenessContext(new IntPtr(-3)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}

/// <summary>Parses key tokens like "ctrl", "n", "f7", "alt" into virtual-key codes.</summary>
public static class VirtualKeys
{
    /// <summary>Parse a key token to a VK code. Returns false for unknown tokens.</summary>
    public static bool TryParse(string token, out ushort vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        switch (token.Trim().ToLowerInvariant())
        {
            case "ctrl":
            case "control":
                vk = 0x11; return true;
            case "alt":
            case "menu":
                vk = 0x12; return true;
            case "shift":
                vk = 0x10; return true;
            case "enter":
            case "return":
                vk = 0x0D; return true;
            case "esc":
            case "escape":
                vk = 0x1B; return true;
            case "tab":
                vk = 0x09; return true;
            case "space":
                vk = 0x20; return true;
            case "f1": vk = 0x70; return true;
            case "f2": vk = 0x71; return true;
            case "f3": vk = 0x72; return true;
            case "f4": vk = 0x73; return true;
            case "f5": vk = 0x74; return true;
            case "f6": vk = 0x75; return true;
            case "f7": vk = 0x76; return true;
            case "f8": vk = 0x77; return true;
            case "f9": vk = 0x78; return true;
            case "f10": vk = 0x79; return true;
            case "f11": vk = 0x7A; return true;
            case "f12": vk = 0x7B; return true;
        }

        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c;
                return true;
            }
        }

        return false;
    }
}

/// <summary>Raw user32 P/Invoke surface used by the real UIA services. Windows only.</summary>
[ExcludeFromCodeCoverage]
internal static class Native
{
    public const int WM_GETTEXT = 0x000D;
    public const int WM_GETTEXTLENGTH = 0x000E;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int LB_GETCOUNT = 0x018B;
    public const int LB_GETTEXT = 0x0189;
    public const int SB_GETPARTS = 0x0406;
    public const int SB_GETTEXT = 0x0402;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    // INPUT with a type tag plus a mouse/keyboard union so one SendInput handles both.
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUTK
    {
        public int type;
        public INPUT_UNION U;
    }

    /// <summary>Build a keyboard INPUT (type=1).</summary>
    public static INPUTK KeyInput(ushort vk, bool keyUp) => new()
    {
        type = 1,
        U = new INPUT_UNION
        {
            ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = keyUp ? KEYEVENTF_KEYUP : 0u },
        },
    };

    /// <summary>Build a mouse INPUT (type=0).</summary>
    public static INPUTK MouseInput(uint flags, int dx, int dy) => new()
    {
        type = 0,
        U = new INPUT_UNION
        {
            mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags },
        },
    };

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SendInput")]
    public static extern uint SendInputK(uint nInputs, [MarshalAs(UnmanagedType.LPArray)] INPUTK[] pInputs, int cbSize);
}
