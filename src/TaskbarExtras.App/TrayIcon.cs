using System.Drawing;
using System.Windows.Forms;
using TaskbarExtras.Actions;

// WPF's implicit usings put System.Windows.Localization in scope, so the name needs pinning.
using Localization = TaskbarExtras.Actions.Localization;

namespace TaskbarExtras.App;

/// <summary>
/// Tray presence.
///
/// <para>
/// This is the guaranteed entry point: the taskbar-menu interception (see
/// <see cref="Shell.TaskbarMenuWatcher"/>) is the headline feature, but if it ever fails the
/// user must still be able to reach the menu and — more importantly — still be able to quit.
/// A tray icon with an explicit Exit item is what makes that true.
/// </para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private bool _disposed;

    /// <summary>Left-click: show the menu (same menu the taskbar right-click shows).</summary>
    public event EventHandler? OpenMenuRequested;

    /// <summary>From the tray's own menu. Without this the process would be unkillable by normal means.</summary>
    public event EventHandler? ExitRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(Localization.Get("tray.open-menu"), null, (_, _) => OpenMenuRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Localization.Get("tray.open-log"), null, (_, _) => OpenLog());
        menu.Items.Add(Localization.Get("tray.exit"), null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = Localization.Get("tray.tooltip"),
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.MouseUp += OnMouseUp;
    }

    private void OnMouseUp(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        // Right-click is owned by the ContextMenuStrip above, so only react to the left button.
        if (e.Button == MouseButtons.Left)
            OpenMenuRequested?.Invoke(this, EventArgs.Empty);
    }

    private static void OpenLog()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.Path) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
