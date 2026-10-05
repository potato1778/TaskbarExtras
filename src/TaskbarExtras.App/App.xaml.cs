using System.Threading;
using System.Windows;
using TaskbarExtras.Actions;
using TaskbarExtras.Shell;

namespace TaskbarExtras.App;

// Fully qualified because WinForms is enabled in this project (for NotifyIcon), which puts
// System.Windows.Forms.Application and System.Windows.Application in scope at the same time.
public partial class App : System.Windows.Application
{
    /// <summary>Every menu entry comes from here. Adding an action is a one-line change.</summary>
    public static ActionRegistry Registry { get; } = ActionRegistry.CreateDefault();

    private static Mutex? _singleInstance;

    private TrayIcon? _tray;
    private TaskbarMenuWatcher? _watcher;
    private ClassicMenuWindow? _menu;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Reset();

        if (!ClaimSingleInstance())
        {
            Log.Write("已有实例在运行，退出。");
            Shutdown();
            return;
        }

        _menu = new ClassicMenuWindow(Registry);

        // ---- The headline feature: take over the taskbar right-click menu. ----
        _watcher = new TaskbarMenuWatcher();
        _watcher.Diagnostic += (_, message) => Log.Write($"[watcher] {message}");
        _watcher.MenuShown += (_, _) => OnTaskbarMenuShown();
        _watcher.Start();

        // ---- The guaranteed fallback: a tray icon. ----
        _tray = new TrayIcon();
        _tray.OpenMenuRequested += (_, _) => ShowMenuAtCursor();
        _tray.ExitRequested += (_, _) => Shutdown();

        var taskbar = TaskbarInfo.TryGetRect(out var tb) ? tb.ToString() : "<未找到>";
        Log.Write($"启动完成。任务栏={taskbar}  钩子运行中={_watcher.IsRunning}");
    }

    private void OnTaskbarMenuShown()
    {
        // The native menu is already dismissed by the watcher; the cursor is still sitting on
        // the taskbar, which is exactly where the user expects the replacement to appear.
        ShowMenuAtCursor();
    }

    private void ShowMenuAtCursor()
    {
        if (_menu is null) return;
        TaskbarInfo.TryGetCursorPos(out var x, out var y);
        TaskbarInfo.TryGetRect(out var taskbar);
        Log.Write($"弹出自绘菜单 光标=({x},{y}) 任务栏={taskbar}");
        _menu.ShowAtPhysical(x, y, taskbar);
    }

    private static bool ClaimSingleInstance()
    {
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\TaskbarExtras.SingleInstance", out var createdNew);
        return createdNew;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Write("退出");
        _tray?.Dispose();
        _watcher?.Dispose();
        _menu?.Close();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
