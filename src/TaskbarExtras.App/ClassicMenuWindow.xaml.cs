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

    public ClassicMenuWindow(ActionRegistry registry)
    {
        _registry = registry;
        InitializeComponent();
    }

    /// <summary>
    /// Show the menu anchored to a point on the taskbar.
    /// </summary>
    /// <param name="anchorX">Physical pixel X the menu should be centred on.</param>
    /// <param name="anchorY">Unused today; kept so callers do not have to change later.</param>
    /// <param name="taskbar">Physical rect of the taskbar, so the menu can sit right above it.</param>
    public void ShowAtPhysical(int anchorX, int anchorY, ScreenRect? taskbar)
    {
        // Re-evaluate CanExecute() every time the menu opens — "层叠窗口" is meaningless with
        // fewer than two windows, and that changes from second to second.
        Rebuild();

        // ---- Gotcha #1: a never-shown Window reports ActualWidth/Height == 0. ----
        // SizeToContent only resolves during a real layout pass, so UpdateLayout() on a window
        // that has not been shown tells us nothing. Show it fully transparent first, let layout
        // run, then position it and reveal. Measured without this fix: the menu landed at
        // (anchorX, taskbar.Top) — 245 px hanging off the bottom of the screen — because both
        // dimensions were treated as zero.
        Opacity = 0;
        if (!IsVisible) Show();
        UpdateLayout();

        // ---- Gotcha #2: the DPI conversion. ----
        // Everything the shell gave us is PHYSICAL pixels. WPF's Left/Top are DIPs. On a 150%
        // display those differ by 1.5x, so an unconverted value puts the menu 50% too far from
        // the corner.
        var dpi = VisualTreeHelper.GetDpi(this);
        var menuWidthDip = ActualWidth > 0 ? ActualWidth : 240;
        var menuHeightDip = ActualHeight > 0 ? ActualHeight : 320;

        var work = TaskbarInfo.TryGetWorkArea(out var w) ? w : new ScreenRect(0, 0, 1920, 1080);

        // Bottom-aligned just above the taskbar, horizontally centred on the cursor.
        var menuHeightPx = menuHeightDip * dpi.DpiScaleY;
        var topPhysical = taskbar is { } tb && tb.Height > 0
            ? tb.Top - menuHeightPx
            : anchorY - menuHeightPx;
        var leftPhysical = anchorX - (menuWidthDip * dpi.DpiScaleX) / 2.0;

        // Keep it inside the work area.
        var minLeftDip = work.Left / dpi.DpiScaleX;
        var maxLeftDip = work.Right / dpi.DpiScaleX - menuWidthDip;
        Left = Math.Clamp(leftPhysical / dpi.DpiScaleX, minLeftDip, Math.Max(minLeftDip, maxLeftDip));
        Top = Math.Max(work.Top / dpi.DpiScaleY, topPhysical / dpi.DpiScaleY);

        Opacity = 1;
        Activate();
    }

    private void Rebuild()
    {
        var items = new List<MenuItemViewModel>();
        foreach (var action in _registry.All)
        {
            // A separator before the two "settings" style entries, mirroring the Windows 10 menu.
            if (action.Id == "taskbar-settings") items.Add(MenuItemViewModel.Separator());
            items.Add(MenuItemViewModel.From(action));
        }
        ItemsHost.ItemsSource = items;
    }

    private void OnItemMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MenuItemViewModel vm }) return;
        if (vm.Action is null || !vm.IsEnabled) return;

        Hide();
        // Run after the menu is gone so the action's window changes are not fighting our own
        // Deactivated/Activate cycle.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                vm.Action.Execute();
            }
            catch (Exception ex)
            {
                Log.Write($"动作 {vm.Action.Id} 执行失败: {ex.Message}");
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
