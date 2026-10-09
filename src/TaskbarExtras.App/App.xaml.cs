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

        // Before the single-instance check: the setting lives in the registry, so it can be
        // changed whether or not an instance is already running.
        if (TryHandleAutostartArgument(e.Args))
        {
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

        // Must happen before the menu window exists — see ApplySkinArgument.
        ApplySkinArgument(e.Args);

        _menu = new ClassicMenuWindow(Registry)
        {
            // App-level rows live here rather than in the action registry: these are settings of
            // this program, not things the Windows 10 taskbar menu could do — so they are not
            // shell actions. But this app has no main window and its tray icon is usually hidden
            // behind the overflow chevron, so the menu the user already opens is the only place
            // they will ever look for them.
            ExtraItemsFactory = () => new[]
            {
                MenuItemViewModel.Toggle(
                    Localization.Get("menu.startup"),
                    StartupRegistration.IsEnabled,
                    ToggleStartup),
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

        if (e.Args.Any(a => a is "--preview" or "-p"))
        {
            // Show the menu once, anchored to the middle of the taskbar, and quit when it is
            // dismissed. Lets you look at a skin without installing anything, and makes
            // screenshots deterministic — no synthesised clicks involved.
            _hook.Enabled = false;   // dismissal still works, right-click swallowing does not
            _hook.Start();
            _menu.IsVisibleChanged += (_, _) => { if (!_menu.IsVisible) Shutdown(); };

            if (!TaskbarInfo.TryGetRect(out var bar)) bar = new ScreenRect(0, 1528, 2560, 1600);
            var anchorX = bar.Left + bar.Width / 2;
            var anchorY = bar.Top + bar.Height / 2;
            Log.Write($"preview 模式：在 ({anchorX},{anchorY}) 显示菜单");
            _menu.ShowAtPhysical(anchorX, anchorY);
            return;
        }

        var hookInstalled = _hook.Start();

        _tray = new TrayIcon();
        _tray.OpenMenuRequested += (_, _) => ShowMenuAtCursor();
        _tray.ExitRequested += (_, _) => Shutdown();
        _tray.StartupToggleRequested += (_, _) => ToggleStartup();

        // Doorbell for `--quit`.
        _quitSignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, QuitEventName, out _);
        ThreadPool.RegisterWaitForSingleObject(_quitSignal, OnQuitSignalled, null, Timeout.Infinite, executeOnlyOnce: false);

        var taskbar = TaskbarInfo.TryGetRect(out var tb) ? tb.ToString() : "<未找到>";
        var launchedAtSignIn = e.Args.Any(a => a == StartupRegistration.StartupArgument);
        _started = true;
        Log.Write($"启动完成。语言={Localization.Current} 钩子已装={hookInstalled} 任务栏={taskbar}"
                  + (launchedAtSignIn ? " （开机自启）" : string.Empty));
    }

    /// <summary>
    /// Runs on the hook thread, i.e. inside the low-level mouse hook. It must return quickly:
    /// a hook that blocks stalls mouse input system-wide and gets unhooked by Windows after
    /// <c>LowLevelHooksTimeout</c> (300 ms by default). So: log, post, return.
    /// </summary>
    private void OnRightClickSwallowed(object? sender, TaskbarRightClickEventArgs e)
    {
        Log.Write($"拦截任务栏右键 ({e.X},{e.Y}) 注入={e.Injected}");
        Dispatcher.BeginInvoke(new Action(() => _menu?.ShowAtPhysical(e.X, e.Y)));
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
        Log.Write($"弹出自绘菜单（托盘）光标=({x},{y})");
        _menu.ShowAtPhysical(x, y);
    }

    /// <summary>
    /// Flip the start-at-sign-in setting. The current state is re-read here rather than passed
    /// in, so the toggle does the opposite of what is actually registered even if the registry
    /// changed behind our back since the menu was built.
    /// </summary>
    private static void ToggleStartup()
    {
        var turningOn = !StartupRegistration.IsEnabled;

        bool ok;
        string? error;
        if (turningOn) ok = StartupRegistration.TryEnable(out error);
        else ok = StartupRegistration.TryDisable(out error);

        Log.Write(turningOn
            ? ok
                ? $"已开启开机自启：{StartupRegistration.CommandLine}"
                : $"开启开机自启失败：{error}"
            : ok
                ? "已关闭开机自启"
                : $"关闭开机自启失败：{error}");

        if (ok) return;

        // A menu row that does nothing when clicked is worse than an error message. This is the
        // only place the app shows a dialog, and it only happens on the failure path.
        System.Windows.Forms.MessageBox.Show(
            $"{Localization.Get("startup.failed")}\n\n{error}",
            Localization.Get("app.name"),
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Warning);
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
        Console.WriteLine("  TaskbarExtras.exe --autostart      查看是否已设为开机自启");
        Console.WriteLine("  TaskbarExtras.exe --autostart on   设为开机自启");
        Console.WriteLine("  TaskbarExtras.exe --autostart off  取消开机自启");
        Console.WriteLine("  TaskbarExtras.exe --lang zh|en     强制界面语言");
        Console.WriteLine("  TaskbarExtras.exe --skin win11|win10  菜单外观（默认 win11）");
        Console.WriteLine("  TaskbarExtras.exe --preview        只显示一次菜单，用来预览皮肤");
        Console.WriteLine("  TaskbarExtras.exe --help           显示这段说明");
        Console.WriteLine();
        Console.WriteLine("退出方式（任选其一）：");
        Console.WriteLine("  1. 右键任务栏空白处 → 「退出 TaskbarExtras」");
        Console.WriteLine("  2. 右键托盘图标（可能在 ^ 折叠区里）→ 退出");
        Console.WriteLine("  3. 运行 TaskbarExtras.exe --quit");
    }

    /// <summary>
    /// Handles <c>--autostart</c> (report) and <c>--autostart on|off</c> (change).
    ///
    /// <para>
    /// Deliberately does not touch the running instance: the setting is stored in the registry
    /// and the menu re-reads it every time it opens, so a running copy picks the change up by
    /// itself. There is nothing to notify.
    /// </para>
    /// </summary>
    /// <returns>True when the argument was present and has been handled.</returns>
    private static bool TryHandleAutostartArgument(string[] args)
    {
        var index = Array.FindIndex(args, a => a is "--autostart" or "--startup-setting");
        if (index < 0) return false;

        var value = index + 1 < args.Length ? args[index + 1].Trim().ToLowerInvariant() : null;
        ParentConsole.TryAttach();

        switch (value)
        {
            case "on" or "enable" or "true":
                Console.WriteLine(StartupRegistration.TryEnable(out var enableError)
                    ? $"已开启开机自启：{StartupRegistration.CommandLine}"
                    : $"开启开机自启失败：{enableError}");
                break;

            case "off" or "disable" or "false":
                Console.WriteLine(StartupRegistration.TryDisable(out var disableError)
                    ? "已关闭开机自启。"
                    : $"关闭开机自启失败：{disableError}");
                break;

            default:
                if (StartupRegistration.IsEnabled)
                {
                    Console.WriteLine($"开机自启：已开启");
                    Console.WriteLine($"  {StartupRegistration.CommandLine}");
                }
                else
                {
                    Console.WriteLine("开机自启：未开启");
                    Console.WriteLine($"  当前程序：{StartupRegistration.ExecutablePath ?? "<未知>"}");
                }

                Console.WriteLine();
                Console.WriteLine("用法：TaskbarExtras.exe --autostart on|off");
                break;
        }

        return true;
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

    /// <summary>
    /// Swaps the merged skin dictionary. <b>Must run before the menu window is constructed</b>:
    /// the menu's XAML binds to the skin with <c>StaticResource</c>, which resolves once at load
    /// time, so a swap afterwards would silently do nothing. (Supporting a live switch would mean
    /// moving every brush reference to <c>DynamicResource</c>.)
    /// </summary>
    private static void ApplySkinArgument(string[] args)
    {
        var requested = "win11";
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--skin" or "-s") requested = args[i + 1].Trim().ToLowerInvariant();
        }

        var fileName = requested switch
        {
            "win10" => "Win10.xaml",
            "win11" => "Win11.xaml",
            _ => null
        };

        if (fileName is null)
        {
            Log.Write($"未知皮肤 '{requested}'，回退到 win11");
            fileName = "Win11.xaml";
        }

        try
        {
            var dictionary = new ResourceDictionary { Source = new Uri($"Skins/{fileName}", UriKind.Relative) };
            Current.Resources.MergedDictionaries.Clear();
            Current.Resources.MergedDictionaries.Add(dictionary);
            Log.Write($"皮肤 = {fileName}");
        }
        catch (Exception ex)
        {
            // Leave the dictionary from App.xaml in place rather than starting with no skin at all.
            Log.Write($"皮肤 '{fileName}' 加载失败，沿用默认: {ex.Message}");
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
