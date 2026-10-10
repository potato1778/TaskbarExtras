using System.Drawing;
using System.IO;
using System.Text;
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
    private readonly ToolStripMenuItem _startupItem;
    private bool _disposed;

    /// <summary>Left-click: show the menu (same menu the taskbar right-click shows).</summary>
    public event EventHandler? OpenMenuRequested;

    /// <summary>From the tray's own menu. Without this the process would be unkillable by normal means.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>
    /// Flip start-at-sign-in. Routed back to the host rather than handled here so the setting is
    /// changed in exactly one place, error dialog included.
    /// </summary>
    public event EventHandler? StartupToggleRequested;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(Localization.Get("tray.open-menu"), null, (_, _) => OpenMenuRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());

        _startupItem = new ToolStripMenuItem(Localization.Get("menu.startup"));
        _startupItem.Click += (_, _) => StartupToggleRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_startupItem);

        // The tick has to be refreshed on every open, not set once at construction: the setting
        // can also be changed from the replacement menu or from the command line, and a stale
        // tick would be worse than none.
        menu.Opening += (_, _) => _startupItem.Checked = StartupRegistration.IsEnabled;

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
    /// <para>
    /// Drawn at several sizes rather than once at 32x32. Windows asks for 16 px in a 100% tray,
    /// 24 px at 150%, 32 px for large icons — and when it is handed a single bitmap for all of
    /// those it resamples, which turns a three-bar glyph into mush at the small end. Supplying
    /// each size lets it pick an exact frame instead.
    /// </para>
    /// </summary>
    private static Icon CreateIcon()
    {
        (int Size, byte[] Png)[] frames =
        [
            (16, RenderIconPng(16)),
            (20, RenderIconPng(20)),
            (24, RenderIconPng(24)),
            (32, RenderIconPng(32)),
            (48, RenderIconPng(48)),
        ];
        return IconFromPngFrames(frames);
    }

    /// <summary>
    /// Draws the glyph at one size. Everything is expressed against a 32 px design grid and
    /// scaled, so the shapes stay proportional instead of drifting at small sizes.
    /// </summary>
    private static byte[] RenderIconPng(int size)
    {
        var k = size / 32f;

        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var plate = new SolidBrush(Color.FromArgb(255, 32, 32, 32));
            using var path = RoundedRect(1 * k, 1 * k, 30 * k, 30 * k, 7 * k);
            g.FillPath(plate, path);

            // Three stacked bars: a taskbar with its menu open.
            using var bar = new SolidBrush(Color.FromArgb(255, 242, 242, 242));
            using var accent = new SolidBrush(Color.FromArgb(255, 96, 165, 250));
            g.FillRectangle(bar, 7 * k, 8.5f * k, 18 * k, 3.5f * k);
            g.FillRectangle(bar, 7 * k, 14.5f * k, 18 * k, 3.5f * k);
            g.FillRectangle(accent, 7 * k, 20.5f * k, 11 * k, 3.5f * k);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>
    /// Assembles a multi-size .ico in memory.
    ///
    /// <para>
    /// PNG-compressed frames are used rather than the older BMP-with-masks form: every Windows
    /// this app runs on understands them, and it avoids hand-writing a BITMAPINFOHEADER plus the
    /// two colour masks per frame. The header is the documented ICONDIR/ICONDIRENTRY layout.
    /// </para>
    /// </summary>
    private static Icon IconFromPngFrames((int Size, byte[] Png)[] frames)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0);                 // reserved
            writer.Write((ushort)1);                 // 1 = icon (2 would be a cursor)
            writer.Write((ushort)frames.Length);

            var offset = 6 + 16 * frames.Length;
            foreach (var (size, png) in frames)
            {
                writer.Write((byte)size);            // 0 means 256; none of ours are that big
                writer.Write((byte)size);
                writer.Write((byte)0);               // palette entries
                writer.Write((byte)0);               // reserved
                writer.Write((ushort)1);             // colour planes
                writer.Write((ushort)32);            // bits per pixel
                writer.Write(png.Length);
                writer.Write(offset);
                offset += png.Length;
            }

            foreach (var (_, png) in frames) writer.Write(png);
        }

        stream.Position = 0;
        return new Icon(stream);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(float x, float y, float w, float h, float radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
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
