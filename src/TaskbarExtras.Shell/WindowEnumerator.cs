using System.Text;

namespace TaskbarExtras.Shell;

/// <summary>
/// Enumerates "app windows" — the ones a user would expect to see in a task list.
/// Used by the show-desktop toggle to answer the question the shell refuses to answer
/// directly: "is the desktop currently showing?"
/// </summary>
public static class WindowEnumerator
{
    public static IReadOnlyList<IntPtr> GetAppWindows()
    {
        var list = new List<IntPtr>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (IsAppWindow(hWnd)) list.Add(hWnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// A window counts as an "app window" when it is visible, top-level, not a tool
    /// window, not minimised, not DWM-cloaked (that is how UWP apps hide), and has a title.
    /// </summary>
    public static bool IsAppWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        if (!NativeMethods.IsWindowVisible(hWnd)) return false;
        if (NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return false;

        var exStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return false;

        if (NativeMethods.IsIconic(hWnd)) return false;

        if (IsCloaked(hWnd)) return false;

        return !string.IsNullOrWhiteSpace(GetTitle(hWnd));
    }

    /// <summary>DWM-cloaked windows are invisible to the user but still report WS_VISIBLE.</summary>
    public static bool IsCloaked(IntPtr hWnd)
    {
        if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) != 0)
            return false;
        return cloaked != 0;
    }

    /// <summary>Physical-pixel rect of any window. Kept here so callers never P/Invoke directly.</summary>
    public static bool TryGetRect(IntPtr hWnd, out ScreenRect rect)
    {
        rect = default;
        if (hWnd == IntPtr.Zero) return false;
        if (!NativeMethods.GetWindowRect(hWnd, out var r)) return false;
        rect = new ScreenRect(r.Left, r.Top, r.Right, r.Bottom);
        return true;
    }

    public static string GetTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
