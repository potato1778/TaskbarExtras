namespace TaskbarExtras.Shell;

public sealed class TaskbarMenuEventArgs : EventArgs
{
    /// <summary>Where the native menu was about to appear — use it to place our replacement.</summary>
    public ScreenRect Anchor { get; init; }
    public IntPtr NativeMenuHandle { get; init; }
}

/// <summary>
/// Detects a right-click on the taskbar and swallows the native menu that follows.
///
/// <para><b>Why not EVENT_SYSTEM_MENUPOPUPSTART?</b> Measured on Windows 11 26H2 build
/// 26300.9457: the taskbar menu is <i>not</i> a classic <c>#32768</c> menu, it is a XAML
/// popup, so the whole <c>EVENT_SYSTEM_MENU*</c> family never fires for it. A control
/// experiment (Alt+Space, a real classic menu) proved the hook itself was working.
/// See docs/DESIGN.md §6.4.</para>
///
/// <para>The popup <i>is</i> a real HWND though, with a stable class name, so we filter on
/// that instead. Three filters are applied, and all three are needed:</para>
/// <list type="number">
///   <item>class name == <c>Xaml_WindowedPopupClass</c></item>
///   <item>owning process == explorer.exe</item>
///   <item>the cursor is currently over the taskbar</item>
/// </list>
/// <para>
/// Filter 3 is the decisive one: the same class is used by every WinUI/XAML app's popups,
/// and explorer itself pops up XAML surfaces for Start / Search / the notification centre.
/// "Cursor is on the taskbar" rules all of those out cheaply.
/// </para>
///
/// <para><b>Known limitation:</b> the native menu is shown first and dismissed by us, so it
/// can flash for a frame. A <c>WH_MOUSE_LL</c> hook could swallow the click before it ever
/// reaches the shell, but that is a global input hook — see docs/DESIGN.md §6.4 B1/B2′.</para>
/// </summary>
public sealed class TaskbarMenuWatcher : IDisposable
{
    public const string XamlPopupClass = "Xaml_WindowedPopupClass";

    private const uint WM_CLOSE = 0x0010;

    /// <summary>Measured: the XAML popup reports rect (0,0,0,0) at EVENT_OBJECT_SHOW time.</summary>
    private const int PositionSettleMs = 60;

    private readonly NativeMethods.WinEventDelegate _callback; // MUST be kept alive or the GC collects the thunk
    private readonly uint _explorerPid;

    private IntPtr _hook;
    private bool _disposed;

    /// <remarks>
    /// Deliberately has no WPF dependency — this layer must stay UI-framework agnostic, and
    /// it must be callable from a thread that pumps messages (the caller's UI thread).
    /// </remarks>
    public TaskbarMenuWatcher()
    {
        _callback = OnWinEvent;
        _explorerPid = FindExplorerPid();
    }

    /// <summary>Master switch. Diagnostics can turn this off to observe the native menu untouched.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Raised on the UI thread right after the native taskbar menu has been dismissed.</summary>
    public event EventHandler<TaskbarMenuEventArgs>? MenuShown;

    /// <summary>Every candidate popup we saw, for diagnostics. Independent of <see cref="Enabled"/>.</summary>
    public event EventHandler<string>? Diagnostic;

    public bool IsRunning => _hook != IntPtr.Zero;

    /// <summary>Must be called on a thread that pumps messages (the WPF UI thread).</summary>
    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_SHOW,
            IntPtr.Zero, _callback, 0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private void OnWinEvent(IntPtr hHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed || hwnd == IntPtr.Zero) return;
        if (eventType != NativeMethods.EVENT_OBJECT_SHOW) return;

        // Filter 1 — cheap, kills almost everything.
        if (WindowEnumerator.GetClassName(hwnd) != XamlPopupClass) return;

        // Filter 2 — this class is shared by every WinUI app.
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != _explorerPid) return;

        // Filter 3 — the decisive one.
        if (!TaskbarInfo.IsCursorOverTaskbar()) return;

        if (!Enabled)
        {
            Diagnostic?.Invoke(this, $"检测到任务栏菜单（未拦截，Enabled=false）hwnd=0x{hwnd:X}");
            return;
        }

        // OUTOFCONTEXT callbacks arrive on this (our UI) thread, which is idle by definition
        // at this moment — the native menu just appeared. Blocking briefly here is safe and
        // far simpler than marshalling a delayed re-check.
        Thread.Sleep(PositionSettleMs);

        if (!NativeMethods.IsWindowVisible(hwnd)) return;

        NativeMethods.GetWindowRect(hwnd, out var r);
        var anchor = new ScreenRect(r.Left, r.Top, r.Right, r.Bottom);
        if (anchor.Width <= 0 || anchor.Height <= 0)
        {
            // Could not settle; fall back to the taskbar's own rect so the menu still lands sensibly.
            TaskbarInfo.TryGetRect(out anchor);
        }

        NativeMethods.PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Diagnostic?.Invoke(this, $"已拦截任务栏菜单 hwnd=0x{hwnd:X} 锚点={anchor}");

        MenuShown?.Invoke(this, new TaskbarMenuEventArgs { Anchor = anchor, NativeMenuHandle = hwnd });
    }

    private static uint FindExplorerPid()
    {
        var tray = TaskbarInfo.Handle;
        if (tray == IntPtr.Zero) return 0;
        NativeMethods.GetWindowThreadProcessId(tray, out var pid);
        return pid;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
