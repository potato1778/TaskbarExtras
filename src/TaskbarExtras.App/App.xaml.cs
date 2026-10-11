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

    /// <summary>
    /// Second doorbell, for <c>--settings</c>. A second copy cannot open the panel itself — the
    /// single-instance check would have it exit first — so it rings this instead and the running
    /// instance does the showing.
    /// </summary>
    private const string OpenSettingsEventName = @"Local\TaskbarExtras.OpenSettings";

    private static Mutex? _singleInstance;
    private EventWaitHandle? _quitSignal;
    private EventWaitHandle? _settingsSignal;
    private bool _started;

    private TrayIcon? _tray;
    private TaskbarRightClickHook? _hook;
    private ClassicMenuWindow? _menu;
    private SettingsWindow? _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Settings first: the language and skin arguments below both consult the stored values,
        // and both must be resolved before the menu window exists.
        AppSettings.Load();
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

        // Likewise: writes a file, does not need the UI, and must work while another copy runs.
        if (TryHandleSkinSetArgument(e.Args))
        {
            Shutdown();
            return;
        }

        // `--settings` on an already-running copy is a request, not a second copy: hand it over
        // and go away. With nothing running there is nothing to hand it to, so it falls through
        // and the panel opens at the end of a normal start-up.
        var openSettingsOnStart = e.Args.Any(a => a is "--settings" or "--preferences");
        if (openSettingsOnStart && TrySignalRunningInstance(
                OpenSettingsEventName, "已让正在运行的 TaskbarExtras 打开设置面板。"))
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

        // Must happen before the menu window exists — see ApplySkin.
        ApplySkin(e.Args);

        _menu = new ClassicMenuWindow(Registry)
        {
            // App-level rows live here rather than in the action registry: these are settings of
            // this program, not things the Windows 10 taskbar menu could do — so they are not
            // shell actions. But this app has no main window and its tray icon is usually hidden
            // behind the overflow chevron, so the menu the user already opens is the only place
            // they will ever look for them.
            ExtraItemsFactory = withMnemonic => new[]
            {
                // Everything below the Win10 six, at the bottom. Windows 10's own menu had none of
                // these, so they stay out of the way and keep the six rows above untouched.
                //
                // One entry, not four. Start at sign-in, the skin picker and the appearance
                // heading used to be rows here, which turned the bottom of the menu into a
                // settings screen — a pile of options on every right-click that had nothing to do
                // with the taskbar. They live in SettingsWindow now; see docs/DESIGN.md §7.5.
                MenuItemViewModel.Command(
                    Localization.Get("menu.settings"), 'E',  // sEttings — 'S' is taken by 堆叠显示窗口
                    withMnemonic, ShowSettings),
                MenuItemViewModel.Command(
                    Localization.Get("menu.exit"), 'X',      // eXit
                    withMnemonic, () => Shutdown())
            }
        };

        // Pay the first-show cost now instead of on the user's first right-click.
        _menu.WarmUp();

        // The task buttons on a Windows 11 taskbar are XAML, so their positions exist nowhere in
        // the window tree — UI Automation is the only source, and it is polled on a background
        // thread so the mouse hook never has to make a cross-process call. Start it before the
        // hook goes live. See TaskbarButtonMap.
        TaskbarButtonMap.Start();

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
        _tray.SettingsRequested += (_, _) => ShowSettings();

        // Doorbells for `--quit` and `--settings`. Both are raised from a second process, so both
        // have to be marshalled onto the UI thread before touching any window.
        _quitSignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, QuitEventName, out _);
        ThreadPool.RegisterWaitForSingleObject(_quitSignal, OnQuitSignalled, null, Timeout.Infinite, executeOnlyOnce: false);

        _settingsSignal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, OpenSettingsEventName, out _);
        ThreadPool.RegisterWaitForSingleObject(_settingsSignal, OnOpenSettingsSignalled, null, Timeout.Infinite, executeOnlyOnce: false);

        var taskbar = TaskbarInfo.TryGetRect(out var tb) ? tb.ToString() : "<未找到>";
        var launchedAtSignIn = e.Args.Any(a => a == StartupRegistration.StartupArgument);
        _started = true;
        Log.Write($"启动完成。语言={Localization.Current} 钩子已装={hookInstalled} 任务栏={taskbar}"
                  + (launchedAtSignIn ? " （开机自启）" : string.Empty));

        if (openSettingsOnStart) ShowSettings();
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
    /// Opens the settings panel, or brings the open one forward.
    ///
    /// <para>
    /// One instance, reused: two copies of a settings window can disagree with each other, and the
    /// second one would be the one the user is not looking at. The window reads live state when it
    /// is constructed, so it is thrown away on close rather than kept — reopening it has to
    /// re-read the registry anyway, since Task Manager can change the start-up setting while the
    /// panel is shut.
    /// </para>
    /// </summary>
    private void ShowSettings()
    {
        if (_settings is { IsLoaded: true })
        {
            if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
            _settings.Activate();
            return;
        }

        _settings = new SettingsWindow();
        _settings.SkinChangeRequested += (_, skin) => SwitchSkin(skin);
        _settings.LanguageChangeRequested += (_, tag) => SwitchLanguage(tag);
        _settings.Closed += (_, _) => _settings = null;

        Log.Write("打开设置面板");
        _settings.Show();
        _settings.Activate();
    }

    private void OnOpenSettingsSignalled(object? state, bool timedOut)
    {
        Log.Write("收到 --settings 信号，打开设置面板");
        Dispatcher.BeginInvoke(new Action(ShowSettings));
    }

    private void OnQuitSignalled(object? state, bool timedOut)
    {
        Log.Write("收到 --quit 信号，退出");
        Dispatcher.BeginInvoke(new Action(Shutdown));
    }

    /// <summary>
    /// Tells an already-running instance to do something, via a named event. Returns immediately —
    /// this process never claims the single-instance mutex.
    /// </summary>
    /// <returns>True when an instance was listening.</returns>
    private static bool TrySignalRunningInstance(string eventName, string message)
    {
        ParentConsole.TryAttach();
        if (!EventWaitHandle.TryOpenExisting(eventName, out var handle)) return false;

        using (handle) handle.Set();
        Console.WriteLine(message);
        return true;
    }

    private static void QuitRunningInstance()
    {
        if (!TrySignalRunningInstance(QuitEventName, "已通知正在运行的 TaskbarExtras 退出。"))
            Console.WriteLine("没有正在运行的 TaskbarExtras 实例。");
    }

    private static void PrintHelp()
    {
        ParentConsole.TryAttach();
        Console.WriteLine("TaskbarExtras — 补回 Windows 11 被砍掉的任务栏右键菜单");
        Console.WriteLine();
        Console.WriteLine("  TaskbarExtras.exe                  启动（出现托盘图标）");
        Console.WriteLine("  TaskbarExtras.exe --settings       打开设置面板");
        Console.WriteLine("  TaskbarExtras.exe --quit           让正在运行的实例退出");
        Console.WriteLine("  TaskbarExtras.exe --autostart      查看是否已设为开机自启");
        Console.WriteLine("  TaskbarExtras.exe --autostart on   设为开机自启");
        Console.WriteLine("  TaskbarExtras.exe --autostart off  取消开机自启");
        Console.WriteLine("  TaskbarExtras.exe --lang zh|en     强制界面语言");
        Console.WriteLine("  TaskbarExtras.exe --skin win11|win10  本次运行的菜单外观");
        Console.WriteLine("  TaskbarExtras.exe --skin-set win11|win10");
        Console.WriteLine("                                     改默认外观并记住（下次启动生效）");
        Console.WriteLine("  TaskbarExtras.exe --preview        只显示一次菜单，用来预览皮肤");
        Console.WriteLine("  TaskbarExtras.exe --help           显示这段说明");
        Console.WriteLine();
        Console.WriteLine("外观、开机自启和界面语言都在设置面板里改：");
        Console.WriteLine("  右键任务栏空白处 →「设置…」，或右键托盘图标 →「设置…」");
        Console.WriteLine($"设置存在 {AppSettings.Location}");
        Console.WriteLine();
        Console.WriteLine("退出方式（任选其一）：");
        Console.WriteLine("  1. 右键任务栏空白处 → 「退出 TaskbarExtras」");
        Console.WriteLine("  2. 右键托盘图标（可能在 ^ 折叠区里）→ 退出");
        Console.WriteLine("  3. 运行 TaskbarExtras.exe --quit");
    }

    /// <summary>
    /// Handles <c>--skin-set win11|win10</c>: writes the choice to the settings file without
    /// starting the UI.
    ///
    /// <para>
    /// Separate from <c>--skin</c>, which only affects the run it was passed to. This one is the
    /// scriptable form of the menu's appearance picker, and it exists because the running instance
    /// cannot be told to reload — the skin is baked in when the menu window is constructed. So
    /// this reports whether a restart is needed rather than pretending it took effect live.
    /// </para>
    /// </summary>
    /// <returns>True when the argument was present and has been handled.</returns>
    private static bool TryHandleSkinSetArgument(string[] args)
    {
        var index = Array.FindIndex(args, a => a is "--skin-set" or "--set-skin");
        if (index < 0) return false;

        var value = index + 1 < args.Length ? args[index + 1].Trim().ToLowerInvariant() : null;
        ParentConsole.TryAttach();

        if (value is not ("win10" or "win11"))
        {
            Console.WriteLine("用法：TaskbarExtras.exe --skin-set win11|win10");
            return true;
        }

        AppSettings.SetSkin(value);
        Console.WriteLine(AppSettings.Save()
            ? $"默认外观已设为 {value}。重启 TaskbarExtras 后生效。"
            : "写入设置文件失败，未能保存。");
        return true;
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
    /// <summary>
    /// Resolves the language with the same precedence the skin uses: command line, then the stored
    /// setting, then whatever the OS culture implies (already applied by
    /// <see cref="Localization"/>'s initialiser).
    /// </summary>
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

        if (AppSettings.Language is { } stored) Localization.TrySetLanguage(stored);
    }

    /// <summary>
    /// Switches skin at runtime: remembers the choice, then relaunches so it takes effect.
    ///
    /// <para>
    /// A restart rather than a live swap, because the menu's XAML binds to the skin with
    /// <c>StaticResource</c> — resolving once at load time, so writing the dictionary afterwards
    /// would silently do nothing. Switching every brush to <c>DynamicResource</c> would make a
    /// live swap possible, but it is a large mechanical change across both skins for a setting
    /// users touch once. Documented here so the next person does not rediscover it.
    /// </para>
    /// </summary>
    private void SwitchSkin(string skin)
    {
        AppSettings.SetSkin(skin);
        Relaunch($"--skin {skin}");
    }

    /// <summary>
    /// Same deal for the interface language, and for the same reason: the menu rebuilds its labels
    /// on every open and would follow along, but the tray menu's labels are built once when the
    /// icon is created and would keep the old language until something else forced a restart.
    /// Restarting both together is honest; half-applying it is not.
    /// </summary>
    /// <param name="tag">Language tag, or null for "follow Windows".</param>
    private void SwitchLanguage(string? tag)
    {
        AppSettings.SetLanguage(tag);
        Relaunch(tag is null ? string.Empty : $"--lang {tag}");
    }

    /// <summary>
    /// Saves the settings and starts a fresh copy of this executable, then shuts this one down.
    ///
    /// <para>
    /// The arguments passed in are carried over explicitly rather than replayed from the original
    /// command line: the whole point is that the relaunch reflects the choice just made, so a
    /// <c>--skin</c> from the previous run must not survive it.
    /// </para>
    ///
    /// <para>
    /// Failure is reported and then ignored — the app keeps running with the old look rather than
    /// exiting into nothing. A user who ends up with a windowless process and no way back is a
    /// worse outcome than a skin that did not change.
    /// </para>
    /// </summary>
    private void Relaunch(string arguments)
    {
        if (!AppSettings.Save())
        {
            Log.Write("设置已切换，但未能写入磁盘 —— 重启后会恢复原样。");
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Log.Write("找不到自身路径，无法自动重启。请手动重启 TaskbarExtras。");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Arguments = arguments
            });
        }
        catch (Exception ex)
        {
            Log.Write($"重启失败，请手动启动: {ex.Message}");
            return;
        }

        Shutdown();
    }

    /// <summary>
    /// Picks the skin and swaps the merged dictionary.
    ///
    /// <para>
    /// Precedence is <b>command line &gt; stored setting &gt; built-in default</b>. The command
    /// line wins so <c>--skin win11</c> still works as a one-off override and <c>--preview</c> can
    /// show a skin without committing to it; otherwise the remembered choice from
    /// <see cref="AppSettings"/> is used, which is what makes a skin stick across restarts.
    /// </para>
    ///
    /// <para>
    /// The override is adopted into <see cref="AppSettings"/> in memory but not written to disk, so
    /// a <c>--skin</c> launch leaves the stored default alone — which is what makes it one-off.
    /// The one way it can still end up persisted is if the settings panel is used during that same
    /// run: <see cref="Relaunch"/> saves everything, including the skin currently in effect. That
    /// is deliberate — the panel reports what is on screen, so a user who changes the language
    /// while running <c>--skin win10</c> keeps win10 rather than being silently flipped back.
    /// </para>
    ///
    /// <para>
    /// <b>Must run before the menu window is constructed</b>: the menu's XAML binds to the skin
    /// with <c>StaticResource</c>, which resolves once at load time, so a swap afterwards would
    /// silently do nothing. (Supporting a live switch would mean moving every brush reference to
    /// <c>DynamicResource</c>.)
    /// </para>
    /// </summary>
    private static void ApplySkin(string[] args)
    {
        var requested = SkinFromArguments(args) ?? AppSettings.Skin;

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
            requested = AppSettings.DefaultSkin;
        }

        try
        {
            var dictionary = new ResourceDictionary { Source = new Uri($"Skins/{fileName}", UriKind.Relative) };
            Current.Resources.MergedDictionaries.Clear();
            Current.Resources.MergedDictionaries.Add(dictionary);
            AppSettings.SetSkin(requested);
            Log.Write($"皮肤 = {fileName}");
        }
        catch (Exception ex)
        {
            // Leave the dictionary from App.xaml in place rather than starting with no skin at all.
            Log.Write($"皮肤 '{fileName}' 加载失败，沿用默认: {ex.Message}");
        }
    }

    /// <summary>
    /// The skin named on the command line, or null when the argument is absent. Returning null
    /// rather than a default is what lets <see cref="ApplySkin"/> tell "the user asked for win11"
    /// apart from "the user said nothing" — the two need different treatment.
    /// </summary>
    private static string? SkinFromArguments(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--skin" or "-s") return args[i + 1].Trim().ToLowerInvariant();
        }

        return null;
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
        TaskbarButtonMap.Stop();
        _settings?.Close();
        _tray?.Dispose();
        _hook?.Dispose();
        _menu?.Close();
        _quitSignal?.Dispose();
        _settingsSignal?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
