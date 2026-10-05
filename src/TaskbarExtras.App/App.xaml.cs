using System.Threading;
using System.Windows;
using TaskbarExtras.Actions;
using TaskbarExtras.Shell;

// WPF's implicit usings put System.Windows.Localization in scope, so the name needs pinning.
using Localization = TaskbarExtras.Actions.Localization;

namespace TaskbarExtras.App;

public partial class App : System.Windows.Application
{
    /// <summary>Every menu entry comes from here. Adding an action is a one-line change.</summary>
    public static ActionRegistry Registry { get; } = ActionRegistry.CreateDefault();

    private static Mutex? _singleInstance;

    private TrayIcon? _tray;
    private TaskbarRightClickHook? _hook;
    private ClassicMenuWindow? _menu;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Reset();
        ApplyLanguageArgument(e.Args);

        if (!ClaimSingleInstance())
        {
            Log.Write("已有实例在运行，退出。");
            Shutdown();
            return;
        }

        _menu = new ClassicMenuWindow(Registry);

        // Pay the first-show cost now instead of on the user's first right-click.
        _menu.WarmUp();

        // Swallow the right-click at the source, so the shell's own menu never appears.
        // The previous implementation waited for the shell's menu to show and then dismissed
        // it, which flashed and felt laggy. See TaskbarRightClickHook for the full reasoning.
        _hook = new TaskbarRightClickHook();
        _hook.RightClickSwallowed += OnRightClickSwallowed;
        _hook.ButtonDown += OnButtonDown;
        var hookInstalled = _hook.Start();

        _tray = new TrayIcon();
        _tray.OpenMenuRequested += (_, _) => ShowMenuAtCursor();
        _tray.ExitRequested += (_, _) => Shutdown();

        var taskbar = TaskbarInfo.TryGetRect(out var tb) ? tb.ToString() : "<未找到>";
        Log.Write($"启动完成。语言={Localization.Current} 钩子已装={hookInstalled} 任务栏={taskbar}");
    }

    /// <summary>
    /// Runs on the hook thread, i.e. inside the low-level mouse hook. It must return quickly:
    /// a hook that blocks stalls mouse input system-wide and gets unhooked by Windows after
    /// <c>LowLevelHooksTimeout</c> (300 ms by default). So: log, post, return.
    /// </summary>
    private void OnRightClickSwallowed(object? sender, TaskbarRightClickEventArgs e)
    {
        Log.Write($"拦截任务栏右键 ({e.X},{e.Y}) 注入={e.Injected}");
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TaskbarInfo.TryGetRect(out var taskbar);
            _menu?.ShowAtPhysical(e.X, e.Y, taskbar);
        }));
    }

    /// <summary>
    /// Dismiss an open menu when the user clicks anywhere outside it.
    ///
    /// <para>
    /// This exists because the menu window deliberately never takes focus, so it never receives
    /// <c>Deactivated</c> and a plain click elsewhere would leave it stranded on screen — the
    /// exact bug reported the first time someone tried this. Routing dismissal through the mouse
    /// hook sidesteps the focus question entirely.
    /// </para>
    /// </summary>
    private void OnButtonDown(object? sender, MouseButtonDownEventArgs e)
    {
        var menuRect = _menu?.GetScreenRectOrNull();
        if (menuRect is null) return;                            // nothing is open
        if (menuRect.Value.Contains(e.X, e.Y)) return;           // inside the menu: let WPF handle it

        Dispatcher.BeginInvoke(new Action(() => _menu?.Hide()));
    }

    private void ShowMenuAtCursor()
    {
        if (_menu is null) return;
        TaskbarInfo.TryGetCursorPos(out var x, out var y);
        TaskbarInfo.TryGetRect(out var taskbar);
        Log.Write($"弹出自绘菜单（托盘）光标=({x},{y})");
        _menu.ShowAtPhysical(x, y, taskbar);
    }

    /// <summary>Supports <c>--lang en</c> / <c>--lang zh</c>; otherwise the OS UI language wins.</summary>
    private static void ApplyLanguageArgument(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--lang" or "-l")
            {
                Localization.TrySetLanguage(args[i + 1]);
                return;
            }
        }
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
        _hook?.Dispose();
        _menu?.Close();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
