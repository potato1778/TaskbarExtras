using System.Diagnostics;
using TaskbarExtras.Shell;

namespace TaskbarExtras.Actions;

/// <summary>
/// Toggle the desktop.
///
/// <para>
/// There is no <c>ToggleDesktop</c> API — MSDN's <c>IShellDispatch</c> only offers
/// <c>MinimizeAll</c> and <c>UndoMinimizeALL</c>. So we have to decide which one to call,
/// which means answering "is the desktop showing right now?".
/// </para>
/// <para>
/// A private boolean is not enough: the user may have used Win+D or the peek hotkey.
/// Instead we ask the window manager — if no application window is currently visible and
/// un-minimised, the desktop must be showing. See docs/DESIGN.md §5.1 and risk R4.
/// </para>
/// </summary>
public sealed class ShowDesktopAction : IAction
{
    public string Id => "show-desktop";
    public string DisplayName => Localization.Get("action.show-desktop");
    public string IconKey => "show-desktop";
    public char Mnemonic => 'D';   // Desktop

    public bool CanExecute() => true;

    public bool Execute()
    {
        var desktopIsShowing = WindowEnumerator.GetAppWindows().Count == 0;
        return desktopIsShowing ? ShellDispatch.UndoMinimizeAll() : ShellDispatch.MinimizeAll();
    }
}

public sealed class TaskManagerAction : IAction
{
    public string Id => "task-manager";
    public string DisplayName => Localization.Get("action.task-manager");
    public string IconKey => "task-manager";
    public char Mnemonic => 'K';   // tasK — Windows 10 uses K here, not T

    public bool CanExecute() => true;

    public bool Execute()
    {
        try
        {
            // UseShellExecute lets the shell decide about elevation, and it resolves taskmgr.exe
            // from the system directory rather than the current one.
            Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class CascadeWindowsAction : IAction
{
    public string Id => "cascade-windows";
    public string DisplayName => Localization.Get("action.cascade-windows");
    public string IconKey => "cascade";
    public char Mnemonic => 'C';   // Cascade
    public bool CanExecute() => WindowEnumerator.GetAppWindows().Count > 1;
    public bool Execute() => ShellDispatch.CascadeWindows();
}

public sealed class TileHorizontallyAction : IAction
{
    public string Id => "tile-horizontally";
    public string DisplayName => Localization.Get("action.tile-horizontally");
    public string IconKey => "tile-h";
    public char Mnemonic => 'S';   // Stacked
    public bool CanExecute() => WindowEnumerator.GetAppWindows().Count > 1;
    public bool Execute() => ShellDispatch.TileHorizontally();
}

public sealed class TileVerticallyAction : IAction
{
    public string Id => "tile-vertically";
    public string DisplayName => Localization.Get("action.tile-vertically");
    public string IconKey => "tile-v";
    public char Mnemonic => 'I';   // sIde by side — same letter Windows 7 used
    public bool CanExecute() => WindowEnumerator.GetAppWindows().Count > 1;
    public bool Execute() => ShellDispatch.TileVertically();
}

public sealed class TaskbarSettingsAction : IAction
{
    public string Id => "taskbar-settings";
    public string DisplayName => Localization.Get("action.taskbar-settings");
    public string IconKey => "settings";
    public char Mnemonic => 'P';   // Properties — the row Windows 10 put at the bottom

    public bool CanExecute() => true;

    public bool Execute()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
