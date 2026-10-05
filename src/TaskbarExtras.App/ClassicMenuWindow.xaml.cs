using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TaskbarExtras.Actions;
using TaskbarExtras.Shell;

namespace TaskbarExtras.App;

/// <summary>
/// The replacement taskbar context menu.
///
/// <para>
/// It is a borderless, transparent, topmost WPF window rather than a Win32 HMENU, because a
/// Win32 menu cannot be skinned — and skinnable menus (Win10 today, Win7 Aero and XP Luna
/// later) are the whole point of the project. See docs/DESIGN.md §6.2 and §7.
/// </para>
/// </summary>
public partial class ClassicMenuWindow : Window
{
    private readonly ActionRegistry _registry;

    /// <summary>
    /// Extra rows appended at the bottom, supplied by the host. A factory rather than a list, so
    /// the rows are rebuilt on every open — which keeps their labels in step with the language
    /// and leaves room for dynamic content later.
    /// </summary>
    public Func<IReadOnlyList<MenuItemViewModel>>? ExtraItemsFactory { get; set; }

    public ClassicMenuWindow(ActionRegistry registry)
    {
        _registry = registry;
        InitializeComponent();
    }

    /// <summary>
    /// Show the menu anchored to a point, the way a native Win32 menu appears.
    /// </summary>
    /// <param name="anchorX">Physical pixel X of the click — the menu's left edge lands here.</param>
    /// <param name="anchorY">Physical pixel Y of the click — the menu's bottom edge lands here.</param>
    public void ShowAtPhysical(int anchorX, int anchorY)
    {
        // Re-evaluate CanExecute() every time the menu opens — "层叠窗口" is meaningless with
        // fewer than two windows, and that changes from second to second.
        Rebuild();

        // ---- Gotcha #1: a never-shown Window reports ActualWidth/Height == 0. ----
        // SizeToContent only resolves during a real layout pass, so UpdateLayout() on a window
        // that has not been shown tells us nothing. Show it fully transparent first, let layout
        // run, then position it and reveal. Measured without this fix: the menu landed 245 px off
        // the bottom of the screen, because both dimensions were treated as zero.
        Opacity = 0;
        if (!IsVisible) Show();
        UpdateLayout();

        // ---- Gotcha #2: the DPI conversion. ----
        // Everything the shell gave us is PHYSICAL pixels. WPF's Left/Top are DIPs. On a 150%
        // display those differ by 1.5x, so an unconverted value puts the menu 50% too far from
        // the click.
        var dpi = VisualTreeHelper.GetDpi(this);
        var windowWidthDip = ActualWidth > 0 ? ActualWidth : 240;
        var windowHeightDip = ActualHeight > 0 ? ActualHeight : 320;

        // The window is larger than the visible card: the skin puts a transparent margin around
        // the border so the drop shadow has somewhere to render. Positioning has to compensate,
        // or the menu lands offset from the cursor by exactly that margin.
        var shadowDip = TryFindResource("MenuBorderMargin") is Thickness margin ? margin.Left : 8;

        var windowWidthPx = windowWidthDip * dpi.DpiScaleX;
        var windowHeightPx = windowHeightDip * dpi.DpiScaleY;
        var shadowX = shadowDip * dpi.DpiScaleX;
        var shadowY = shadowDip * dpi.DpiScaleY;

        // Native menus are placed with their top-left at the cursor and then nudged to stay on
        // screen. For a click on a bottom taskbar that puts the visible bottom edge on the click
        // and the visible left edge on the click — which is why the real menu overlaps the
        // taskbar slightly. The first version centred the menu on the cursor and parked it
        // strictly above the taskbar, and it read as "not the real menu".
        var leftPhysical = anchorX - shadowX;
        var topPhysical = anchorY + shadowY - windowHeightPx;

        // Keep the VISIBLE card on screen, hence the shadow compensation. Note this clamps to the
        // MONITOR, not the work area: a menu is allowed to overlap the taskbar — the shell's own
        // menu does exactly that — and clamping to the work area pushes the menu up off the click.
        var bounds = TaskbarInfo.TryGetMonitorRect(out var monitor)
            ? monitor
            : new ScreenRect(0, 0, 1920, 1080);
        leftPhysical = Math.Clamp(leftPhysical,
            bounds.Left - shadowX,
            Math.Max(bounds.Left - shadowX, bounds.Right - windowWidthPx + shadowX));
        topPhysical = Math.Clamp(topPhysical,
            bounds.Top - shadowY,
            Math.Max(bounds.Top - shadowY, bounds.Bottom - windowHeightPx + shadowY));

        Left = leftPhysical / dpi.DpiScaleX;
        Top = topPhysical / dpi.DpiScaleY;

        Opacity = 1;
        Activate();
    }

    private void Rebuild()
    {
        var items = new List<MenuItemViewModel>();
        foreach (var action in _registry.All)
        {
            // A separator before the "settings" style entry, mirroring the Windows 10 menu.
            if (action.Id == "taskbar-settings") items.Add(MenuItemViewModel.Separator());
            items.Add(MenuItemViewModel.From(action));
        }

        var extras = ExtraItemsFactory?.Invoke();
        if (extras is { Count: > 0 })
        {
            items.Add(MenuItemViewModel.Separator());
            items.AddRange(extras);
        }

        ItemsHost.ItemsSource = items;
    }

    private void OnItemMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MenuItemViewModel vm }) return;
        if (!vm.IsEnabled) return;

        Hide();
        // Run after the menu is gone so the action's window changes are not fighting our own
        // Deactivated/Activate cycle.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (vm.Invoke is { } invoke) invoke();
                else if (vm.Action is { } action) action.Execute();
            }
            catch (Exception ex)
            {
                Log.Write($"菜单项执行失败: {ex.Message}");
            }
        }));
    }

    private void OnDeactivated(object? sender, EventArgs e) => Hide();

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
    }

    /// <summary>Physical-pixel rect of the menu while it is on screen, otherwise null.</summary>
    public ScreenRect? GetScreenRectOrNull()
    {
        if (!IsVisible) return null;
        var handle = new WindowInteropHelper(this).Handle;
        return WindowEnumerator.TryGetRect(handle, out var rect) ? rect : null;
    }

    /// <summary>
    /// Pay the one-off costs of showing a window — JIT, XAML template expansion, layout, font
    /// loading — while nobody is watching, so the first real popup is instant.
    ///
    /// <para>
    /// Measured effect: without this, the very first right-click on the taskbar visibly lags
    /// behind the click. It is a one-time cost, but it lands on exactly the moment the user is
    /// judging whether the tool feels responsive, so it is worth pre-paying at startup.
    /// </para>
    /// </summary>
    public void WarmUp()
    {
        try
        {
            Rebuild();
            Opacity = 0;
            Left = -32000;   // far off-screen; Opacity 0 keeps it invisible anyway
            Top = -32000;
            Show();
            UpdateLayout();
            Hide();
        }
        finally
        {
            Opacity = 1;
            Left = 0;
            Top = 0;
        }
    }
}
