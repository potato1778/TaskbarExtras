namespace TaskbarExtras.Shell;

/// <summary>A rectangle in physical (per-monitor-v2 aware) screen pixels.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    /// <summary>True when (x, y) is inside, optionally grown by <paramref name="tolerance"/> px on every side.</summary>
    public bool Contains(int x, int y, int tolerance = 0) =>
        x >= Left - tolerance && x < Right + tolerance &&
        y >= Top - tolerance && y < Bottom + tolerance;

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

/// <summary>
/// Locates the real Windows taskbar. Everything here returns PHYSICAL pixels, so the
/// process must already be per-monitor-v2 DPI aware (see app.manifest) or the numbers
/// will be wrong on scaled displays.
/// </summary>
public static class TaskbarInfo
{
    public const string PrimaryTaskbarClass = "Shell_TrayWnd";
    public const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";

    /// <summary>Handle of the primary taskbar, or IntPtr.Zero if not found.</summary>
    public static IntPtr Handle => NativeMethods.FindWindow(PrimaryTaskbarClass, null);

    public static bool TryGetRect(out ScreenRect rect)
    {
        rect = default;
        var h = Handle;
        if (h == IntPtr.Zero) return false;
        if (!NativeMethods.GetWindowRect(h, out var r)) return false;
        rect = new ScreenRect(r.Left, r.Top, r.Right, r.Bottom);
        return true;
    }

    /// <summary>Handle of the secondary (per-monitor) taskbar, or IntPtr.Zero if absent.</summary>
    public static IntPtr SecondaryHandle => NativeMethods.FindWindow(SecondaryTaskbarClass, null);

    /// <summary>
    /// Mouse cursor position in PHYSICAL pixels. Exposed here rather than letting callers
    /// reach for WinForms' Cursor.Position, so that every coordinate in the app comes from
    /// the same DPI-aware source.
    /// </summary>
    public static bool TryGetCursorPos(out int x, out int y)
    {
        x = y = 0;
        if (!NativeMethods.GetCursorPos(out var pt)) return false;
        x = pt.X;
        y = pt.Y;
        return true;
    }

    /// <summary>
    /// Child windows of the taskbar that own their own right-click behaviour. A right-click
    /// inside any of these must be left alone, or we break things the user relies on:
    /// jump lists on task buttons, the Win+X menu on the Start button, per-icon tray menus.
    /// </summary>
    private static readonly string[] InteractiveChildClasses =
    {
        "Start",                    // right-click = Win+X power user menu
        "MSTaskSwWClass",           // the task list  -> jump lists
        "MSTaskListWClass",
        "ReBarWindow32",            // toolbar host that contains the task list
        "TrayNotifyWnd",            // clock + notification area -> their own menus
        "TrayShowDesktopButtonWnd",
    };

    /// <summary>
    /// True when a point is over the taskbar's <b>empty</b> background — the only place where
    /// right-clicking should bring up our replacement menu.
    /// </summary>
    /// <remarks>
    /// This check is what makes the hook approach safe. Swallowing every right-click over the
    /// taskbar would kill jump lists and the Win+X menu, which would be a far worse regression
    /// than the problem being solved.
    /// </remarks>
    public static bool IsOverEmptyTaskbarArea(int x, int y) => IsOverEmptyTaskbarArea(x, y, out _);

    /// <summary>
    /// Same as <see cref="IsOverEmptyTaskbarArea(int,int)"/>, but reports <i>which</i> child
    /// window blocked the point. This exists because the first version of this check returned a
    /// bare bool, and when the hook silently stopped firing there was no way to tell whether the
    /// point had missed the taskbar entirely or landed on a child window — both looked identical
    /// from the outside. Diagnostics for a global hook are not optional.
    /// </summary>
    /// <param name="blockingClass">
    /// Class name of the child window that owns the point, or null when nothing blocked it.
    /// Null does <b>not</b> mean the point was over the taskbar — check
    /// <see cref="TryGetRect"/> separately.
    /// </param>
    public static bool IsOverEmptyTaskbarArea(int x, int y, out string? blockingClass)
    {
        blockingClass = null;
        if (!TryGetRect(out var taskbar) || !taskbar.Contains(x, y)) return false;

        var taskbarHandle = Handle;
        if (taskbarHandle == IntPtr.Zero) return true;

        string? blocked = null;
        NativeMethods.EnumChildWindows(taskbarHandle, (child, _) =>
        {
            var className = WindowEnumerator.GetClassName(child);
            if (Array.IndexOf(InteractiveChildClasses, className) < 0) return true;
            if (WindowEnumerator.TryGetRect(child, out var rect) && rect.Contains(x, y))
            {
                blocked = className;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        blockingClass = blocked;
        return blocked is null;
    }

    private static bool TryGetMonitorInfo(out NativeMethods.MONITORINFO info)
    {
        info = default;
        var handle = Handle;
        if (handle == IntPtr.Zero)
            handle = NativeMethods.MonitorFromWindow(IntPtr.Zero, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var monitor = NativeMethods.MonitorFromWindow(handle, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (monitor == IntPtr.Zero) return false;
        info = new NativeMethods.MONITORINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        return NativeMethods.GetMonitorInfo(monitor, ref info);
    }

    /// <summary>
    /// Work area of the taskbar's monitor — the screen minus the taskbar and every other appbar.
    /// </summary>
    public static bool TryGetWorkArea(out ScreenRect work)
    {
        work = default;
        if (!TryGetMonitorInfo(out var info)) return false;
        work = new ScreenRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
        return true;
    }

    /// <summary>
    /// Full rect of the taskbar's monitor.
    ///
    /// <para>
    /// Use this, <b>not</b> <see cref="TryGetWorkArea"/>, when clamping a popup menu. A menu is
    /// allowed to overlap the taskbar — that is exactly what the shell's own menu does — so
    /// clamping to the work area pushes the menu up and away from the click point. Measured:
    /// clamping to rcWork moved the menu 33 px off the click.
    /// </para>
    /// </summary>
    public static bool TryGetMonitorRect(out ScreenRect monitor)
    {
        monitor = default;
        if (!TryGetMonitorInfo(out var info)) return false;
        monitor = new ScreenRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
        return true;
    }
}
