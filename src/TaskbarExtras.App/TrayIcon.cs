using System.Drawing;
using System.Windows.Forms;
using TaskbarExtras.Actions;
using TaskbarExtras.Shell;

// WPF's implicit usings put System.Windows.Localization in scope, so the name needs pinning.
using Localization = TaskbarExtras.Actions.Localization;

namespace TaskbarExtras.App;

/// <summary>
/// Tray presence.
///
/// <para>
/// This is the guaranteed entry point: the taskbar right-click interception (see
/// <see cref="TaskbarExtras.Shell.TaskbarRightClickHook"/>) is the headline feature, but if it ever fails the
/// user must still be able to reach the menu and — more importantly — still be able to quit.
/// A tray icon with an explicit Exit item is what makes that true.
/// </para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _iconImage;
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

        _iconImage = CreateIcon();
        _icon = new NotifyIcon
        {
            Icon = _iconImage,
            Text = Localization.Get("tray.tooltip"),
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.MouseUp += OnMouseUp;
    }

    /// <summary>
    /// A distinctive icon instead of <c>SystemIcons.Application</c>.
    ///
    /// <para>
    /// Windows 11 puts new tray icons into the overflow flyout behind the <c>^</c> chevron, and a
    /// generic application glyph there is indistinguishable from a dozen other things. If the user
    /// cannot find the icon, they cannot quit the app — which is exactly what happened the first
    /// time this shipped.
    /// </para>
    /// </summary>
    private static Icon CreateIcon()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var plate = new SolidBrush(Color.FromArgb(255, 32, 32, 32));
            using var path = RoundedRect(new Rectangle(1, 1, size - 2, size - 2), 7);
            g.FillPath(plate, path);

            // Three stacked bars: a taskbar with its menu open.
            using var bar = new SolidBrush(Color.FromArgb(255, 242, 242, 242));
            using var accent = new SolidBrush(Color.FromArgb(255, 96, 165, 250));
            g.FillRectangle(bar, 7f, 8.5f, 18f, 3.5f);
            g.FillRectangle(bar, 7f, 14.5f, 18f, 3.5f);
            g.FillRectangle(accent, 7f, 20.5f, 11f, 3.5f);
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Clone so the returned Icon owns private data, then free the GDI handle immediately —
            // FromHandle does not take ownership and nothing else would release it.
            using var fromHandle = Icon.FromHandle(handle);
            return (Icon)fromHandle.Clone();
        }
        finally
        {
            IconInterop.DestroyIcon(handle);
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
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
        _iconImage.Dispose();
    }
}
