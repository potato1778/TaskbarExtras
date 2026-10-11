using System.Globalization;

namespace TaskbarExtras.Actions;

/// <summary>
/// Two-language string table. Deliberately tiny and dependency-free.
///
/// <para>
/// The language is picked from the OS UI culture at startup and can be overridden with
/// <c>--lang en</c> / <c>--lang zh</c>. Labels are resolved when the menu is built rather than
/// cached at load time, so switching language takes effect on the next menu open.
/// </para>
/// </summary>
public static class Localization
{
    public enum Language { English, Chinese }

    private static readonly Dictionary<string, (string En, string Zh)> Table = new(StringComparer.Ordinal)
    {
        ["app.name"]                  = ("TaskbarExtras", "TaskbarExtras"),

        ["action.show-desktop"]       = ("Show desktop",           "显示桌面"),
        ["action.task-manager"]       = ("Task Manager",           "任务管理器"),
        ["action.cascade-windows"]    = ("Cascade windows",        "层叠窗口"),
        ["action.tile-horizontally"]  = ("Show windows stacked",   "堆叠显示窗口"),
        ["action.tile-vertically"]    = ("Show windows side by side", "并排显示窗口"),
        ["action.taskbar-settings"]   = ("Taskbar settings",       "任务栏设置"),

        ["tray.tooltip"]              = ("TaskbarExtras — taskbar menu enhancer",
                                         "TaskbarExtras — 任务栏右键菜单增强"),
        ["tray.open-menu"]            = ("Open menu",              "打开菜单"),
        ["tray.open-log"]             = ("Open log",               "打开日志"),
        ["tray.exit"]                 = ("Exit",                   "退出"),

        // A checkable row, so the tick tells you the current state without opening anything else.
        ["menu.startup"]              = ("Start at sign-in",       "开机自启"),
        ["startup.failed"]            = ("Could not change the start-up setting:",
                                         "无法修改开机自启设置："),

        // Shown at the bottom of the replacement menu. Windows 10 had no such item, but this
        // app has no main window and its tray icon is usually hidden behind the overflow
        // chevron — so without this there is no discoverable way to quit.
        ["menu.exit"]                 = ("Exit TaskbarExtras",     "退出 TaskbarExtras"),

        // One row in the menu, opening the settings panel. The app's own options used to be four
        // separate rows; they are settings of this program, not of the taskbar, so they moved
        // behind this one entry.
        ["menu.settings"]             = ("Settings\u2026",         "设置\u2026"),

        // The settings panel.
        ["settings.title"]            = ("TaskbarExtras settings", "TaskbarExtras 设置"),
        ["settings.startup.group"]    = ("Start-up",               "启动"),
        ["settings.startup.hint"]     = ("Start TaskbarExtras automatically when you sign in to Windows.",
                                         "登录 Windows 后自动启动 TaskbarExtras。"),
        ["settings.appearance.group"] = ("Appearance",             "外观"),
        ["settings.language.group"]   = ("Language",               "界面语言"),
        ["settings.language.system"]  = ("Follow Windows",         "跟随系统"),
        // Not translated: these two are the names of the languages themselves.
        ["settings.language.zh"]      = ("\u4e2d\u6587",           "\u4e2d\u6587"),
        ["settings.language.en"]      = ("English",                "English"),
        ["settings.note.restart"]     = ("Changing the appearance or the language restarts TaskbarExtras.",
                                         "改动外观或界面语言后，TaskbarExtras 会自动重启。"),
        ["settings.file"]             = ("Settings file: {0}",     "设置文件：{0}"),
        ["settings.open-log"]         = ("Open log",               "打开日志"),
        ["settings.close"]            = ("Close",                  "关闭"),

        // The skin names are product names and stay untranslated.
        ["menu.skin.win11"]           = ("Windows 11 style",       "Windows 11 风格"),
        ["menu.skin.win10"]           = ("Windows 10 style",       "Windows 10 风格"),
    };

    public static Language Current { get; private set; } = DetectFromSystem();

    public static void SetLanguage(Language language) => Current = language;

    /// <summary>Accepts "en", "zh", "zh-CN", "en-US"… Anything unrecognised is ignored.</summary>
    public static bool TrySetLanguage(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var two = tag.Length >= 2 ? tag[..2] : tag;
        switch (two.ToLowerInvariant())
        {
            case "en": Current = Language.English; return true;
            case "zh": Current = Language.Chinese; return true;
            default: return false;
        }
    }

    private static Language DetectFromSystem() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? Language.Chinese
            : Language.English;

    public static string Get(string key)
    {
        if (!Table.TryGetValue(key, out var entry)) return key;
        return Current == Language.Chinese ? entry.Zh : entry.En;
    }
}
