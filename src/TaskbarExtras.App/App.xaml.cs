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

    /// <summary>
    /// Named kernel object used as a doorbell: a second copy of the exe sets it and the running
    /// instance shuts down. This is what makes <c>--quit</c> work.
    /// </summary>
    private const string QuitEventName = @"Local\TaskbarExtras.Quit";

    private static Mutex? _singleInstance;
    private EventWaitHandle? _quitSignal;
    private bool _started;

    private TrayIcon? _tray;
    private TaskbarRightClickHook? _hook;
    private ClassicMenuWindow? _menu;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyLanguageArgument(e.Args);

        if (e.Args.Any(a => a is "--help" or "-h" or "/?"))
        {
            PrintHelp();
            Shutdown();
            return;
        }

        if (e.Args.Any(a => a is "--quit" or "-q"))
        {
            QuitRunningInstance();
            Shutdown();
            return;
        }

        Log.Reset();

        if (!ClaimSingleInstance())
        {
            Log.Write("已有实例在运行，退出。");
            Shutdown();
            return;
        }

        _menu = new ClassicMenuWindow(Registry)
        {
            // App-level rows live here rather than in the action registry: quitting is not
            // something the Windows 10 taskbar menu could do, so it is not a shell action —
            // but this app has no main window, and its tray icon is usually hidden behind the
            // overflow chevron, so without this there is no discoverable way out.
            ExtraItemsFactory = () => new[]
            {
                MenuItemViewModel.Command(Localization.Get("menu.exit"), () => Shutdown())
            }
        };

        // Pay the first-show cost now instead of on the user's first right-click.
        _menu.WarmUp();

        // Swallow the right-click at the source, so the shell's own menu never appears.
        _hook = new TaskbarRightClickHook();
        _hook.RightClickSwallowed += OnRightClickSwallowed;
        _hook.ButtonDown += OnButtonDown;
        _hook.Diagnostic += (_, message) => Log.Write($"[hook] {message}");
        var hookInstalled = _hook.Start();

        _tray = new TrayIcon();
        _tray.OpenMenuRequested += (_, _) => ShowMenuAtCursor();
        _tray.ExitRequested += (_, _) => Shutdown();

        // Doorbell for `--quit`.
        _quitSignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, QuitEventName, out _);
        ThreadPool.RegisterWaitForSingleObject(_quitSignal, OnQuitSignalled, null, Timeout.Infinite, executeOnlyOnce: false);

        var taskbar = TaskbarInfo.TryGetRect(out var tb) ? tb.ToString() : "<未找到>";
        _started = true;
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
    /// <c>Deactivated</c> and a plain click elsewhere would leave it stranded on screen. Routing
    /// dismissal through the mouse hook sidesteps the focus question entirely.
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

    private void OnQuitSignalled(object? state, bool timedOut)
    {
        Log.Write("收到 --quit 信号，退出");
        Dispatcher.BeginInvoke(new Action(Shutdown));
    }

    /// <summary>
    /// Tell an already-running instance to stop, via the named event. Returns immediately —
    /// this process never claims the single-instance mutex.
    /// </summary>
    private static void QuitRunningInstance()
    {
        ParentConsole.TryAttach();
        if (EventWaitHandle.TryOpenExisting(QuitEventName, out var handle))
        {
            using (handle) handle.Set();
            Console.WriteLine("已通知正在运行的 TaskbarExtras 退出。");
        }
        else
        {
            Console.WriteLine("没有正在运行的 TaskbarExtras 实例。");
        }
    }

    private static void PrintHelp()
    {
        ParentConsole.TryAttach();
        Console.WriteLine("TaskbarExtras — 补回 Windows 11 被砍掉的任务栏右键菜单");
        Console.WriteLine();
        Console.WriteLine("  TaskbarExtras.exe                  启动（出现托盘图标）");
        Console.WriteLine("  TaskbarExtras.exe --quit           让正在运行的实例退出");
        Console.WriteLine("  TaskbarExtras.exe --lang zh|en     强制界面语言");
        Console.WriteLine("  TaskbarExtras.exe --help           显示这段说明");
        Console.WriteLine();
        Console.WriteLine("退出方式（任选其一）：");
        Console.WriteLine("  1. 右键任务栏空白处 → 「退出 TaskbarExtras」");
        Console.WriteLine("  2. 右键托盘图标（可能在 ^ 折叠区里）→ 退出");
        Console.WriteLine("  3. 运行 TaskbarExtras.exe --quit");
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
        // The `--quit` invocation also reaches OnExit, and logging "退出" there would make the
        // log look like the app exited twice.
        if (_started) Log.Write("退出");
        _tray?.Dispose();
        _hook?.Dispose();
        _menu?.Close();
        _quitSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
