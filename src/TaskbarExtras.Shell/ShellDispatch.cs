using System.Reflection;

namespace TaskbarExtras.Shell;

/// <summary>
/// The documented way to drive the shell's window-arrangement commands.
///
/// <para>
/// <c>IShellDispatch</c> (MSDN) exposes exactly the verbs that the Windows 10 taskbar
/// context menu used: <c>CascadeWindows</c>, <c>TileHorizontally</c> ("Show windows
/// stacked"), <c>TileVertically</c> ("Show windows side by side"), <c>MinimizeAll</c>,
/// <c>UndoMinimizeALL</c>, <c>TrayProperties</c>.
/// </para>
///
/// <para>
/// Note there is <b>no</b> <c>ToggleDesktop</c> method — the "Show Desktop" toggle has to
/// be built on top of MinimizeAll/UndoMinimizeALL with our own state heuristic.
/// See docs/DESIGN.md §5.1.
/// </para>
/// </summary>
public static class ShellDispatch
{
    private const string ShellProgId = "Shell.Application";

    private static object? TryCreateShell()
    {
        var type = Type.GetTypeFromProgID(ShellProgId);
        return type is null ? null : Activator.CreateInstance(type);
    }

    private static bool TryInvoke(string method)
    {
        try
        {
            var type = Type.GetTypeFromProgID(ShellProgId);
            if (type is null) return false;
            var shell = Activator.CreateInstance(type);
            if (shell is null) return false;
            type.InvokeMember(method, BindingFlags.InvokeMethod, null, shell, null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Fallback for "show desktop", verified working on 26H2 build 26300.9457.</summary>
    private static bool PostTaskbarCommand(int commandId)
    {
        var tray = TaskbarInfo.Handle;
        if (tray == IntPtr.Zero) return false;
        return NativeMethods.PostMessage(tray, NativeMethods.WM_COMMAND, new IntPtr(commandId), IntPtr.Zero);
    }

    // ---------------------------------------------------------------- show desktop

    /// <summary>Minimise every window. Falls back to the semi-documented taskbar command id.</summary>
    public static bool MinimizeAll() =>
        TryInvoke("MinimizeAll") || PostTaskbarCommand(NativeMethods.MIN_ALL);

    /// <summary>Restore the windows minimised by the last MinimizeAll.</summary>
    public static bool UndoMinimizeAll() =>
        TryInvoke("UndoMinimizeALL") || PostTaskbarCommand(NativeMethods.MIN_ALL_UNDO);

    // ---------------------------------------------------------------- arrangement

    public static bool CascadeWindows() => TryInvoke("CascadeWindows");
    public static bool TileHorizontally() => TryInvoke("TileHorizontally");
    public static bool TileVertically() => TryInvoke("TileVertically");

    /// <summary>Opens the classic "Taskbar and Start Menu Properties" dialog.</summary>
    public static bool TrayProperties() => TryInvoke("TrayProperties");
}
