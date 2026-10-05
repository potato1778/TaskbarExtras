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
    /// True when the mouse cursor is currently over the taskbar. Used as the decisive
    /// filter for <see cref="TaskbarMenuWatcher"/>: a XAML popup owned by explorer.exe
    /// appearing while the cursor sits on the taskbar is, in practice, the taskbar menu.
    /// </summary>
    public static bool IsCursorOverTaskbar(int tolerance = 4)
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return false;
        return TryGetRect(out var rect) && rect.Contains(pt.X, pt.Y, tolerance);
    }

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
    public static bool IsOverEmptyTaskbarArea(int x, int y)
    {
        if (!TryGetRect(out var taskbar) || !taskbar.Contains(x, y)) return false;

        var taskbarHandle = Handle;
        if (taskbarHandle == IntPtr.Zero) return true;

        var blocked = false;
        NativeMethods.EnumChildWindows(taskbarHandle, (child, _) =>
        {
            var className = WindowEnumerator.GetClassName(child);
            if (Array.IndexOf(InteractiveChildClasses, className) < 0) return true;
            if (WindowEnumerator.TryGetRect(child, out var rect) && rect.Contains(x, y))
            {
                blocked = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return !blocked;
    }

    /// <summary>
    /// True when a specific point is over the taskbar. Preferred over
    /// <see cref="IsCursorOverTaskbar"/> when the coordinates come from a mouse hook, because
    /// those are the coordinates of the click being processed, not "wherever the cursor is by
    /// the time we get round to asking".
    /// </summary>
    public static bool IsPointOverTaskbar(int x, int y, int tolerance = 2) =>
        TryGetRect(out var rect) && rect.Contains(x, y, tolerance);

    /// <summary>The work area of the monitor the taskbar lives on (excludes taskbar and all other appbars).</summary>
    public static bool TryGetWorkArea(out ScreenRect work)
    {
        work = default;
        var h = Handle;
        if (h == IntPtr.Zero) h = NativeMethods.MonitorFromWindow(IntPtr.Zero, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var mon = NativeMethods.MonitorFromWindow(h, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (mon == IntPtr.Zero) return false;
        var mi = new NativeMethods.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(mon, ref mi)) return false;
        work = new ScreenRect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
        return true;
    }
}
