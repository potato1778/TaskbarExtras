using System.Diagnostics;
using System.Windows;
using TaskbarExtras.Shell;

// WPF's implicit usings put System.Windows.Localization in scope, so the name needs pinning.
using Localization = TaskbarExtras.Actions.Localization;

namespace TaskbarExtras.App;

/// <summary>
/// The app's own settings, in an ordinary window.
///
/// <para>
/// These used to be four rows in the replacement menu — start at sign-in, an "Appearance" heading,
/// and the two skins. That made the menu a settings screen: the rows sat below the six the Windows
/// 10 taskbar had, so every right-click ended with a pile of options that had nothing to do with
/// the taskbar. They are settings of <i>this program</i>, so they belong in a window of this
/// program, and the menu goes back to being a menu.
/// </para>
///
/// <para>
/// The window reports changes as events instead of applying them. Two of the three settings change
/// state that is baked in at startup — the skin is a ResourceDictionary merged before any window
/// exists, and the tray labels are built once — so both are applied by relaunching, which is
/// <see cref="App"/>'s business, not this window's.
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// True while the constructor is copying the stored values into the controls. Writing
    /// <c>IsChecked</c> raises <c>Checked</c> exactly like a click does, so without this the window
    /// would treat its own initialisation as a user request — and since the skin is one of them,
    /// merely opening the panel would restart the app.
    /// </summary>
    private bool _loading = true;

    /// <summary>Raised with the skin name (<c>win11</c> / <c>win10</c>) the user picked.</summary>
    public event EventHandler<string>? SkinChangeRequested;

    /// <summary>Raised with a language tag, or null for "follow Windows".</summary>
    public event EventHandler<string?>? LanguageChangeRequested;

    public SettingsWindow()
    {
        InitializeComponent();
        ApplyLocalizedText();
        LoadCurrentValues();
        _loading = false;
    }

    private void ApplyLocalizedText()
    {
        Title = Localization.Get("settings.title");

        StartupHeader.Text = Localization.Get("settings.startup.group");
        StartupCheck.Content = Localization.Get("menu.startup");
        StartupHint.Text = Localization.Get("settings.startup.hint");

        AppearanceHeader.Text = Localization.Get("settings.appearance.group");
        Win10Radio.Content = Localization.Get("menu.skin.win10");
        Win11Radio.Content = Localization.Get("menu.skin.win11");

        ClassicMenuHeader.Text = Localization.Get("settings.classicmenu.group");
        ClassicMenuCheck.Content = Localization.Get("settings.classicmenu");
        ClassicMenuHint.Text = Localization.Get("settings.classicmenu.hint");
        RestartExplorerButton.Content = Localization.Get("settings.restart-explorer");
        RestartExplorerHint.Text = Localization.Get("settings.restart-explorer.hint");

        LanguageHeader.Text = Localization.Get("settings.language.group");
        LanguageSystemRadio.Content = Localization.Get("settings.language.system");
        LanguageZhRadio.Content = Localization.Get("settings.language.zh");
        LanguageEnRadio.Content = Localization.Get("settings.language.en");

        RestartNote.Text = Localization.Get("settings.note.restart");
        SettingsPathText.Text = string.Format(Localization.Get("settings.file"), AppSettings.Location);

        OpenLogButton.Content = Localization.Get("settings.open-log");
        CloseButton.Content = Localization.Get("settings.close");
    }

    /// <summary>
    /// Reads the live state rather than a cached copy. Start at sign-in lives in the registry and
    /// can be changed from Task Manager, so the registry is the only truthful source — and it is
    /// read here, at the moment the panel opens, not when the app started.
    /// </summary>
    private void LoadCurrentValues()
    {
        StartupCheck.IsChecked = StartupRegistration.IsEnabled;

        Win11Radio.IsChecked = AppSettings.Skin == "win11";
        Win10Radio.IsChecked = AppSettings.Skin == "win10";

        ClassicMenuCheck.IsChecked = ClassicContextMenu.IsEnabled;

        var language = AppSettings.Language?.Trim().ToLowerInvariant();
        LanguageSystemRadio.IsChecked = string.IsNullOrEmpty(language);
        LanguageZhRadio.IsChecked = language?.StartsWith("zh") == true;
        LanguageEnRadio.IsChecked = language?.StartsWith("en") == true;
    }

    private void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var turningOn = StartupCheck.IsChecked == true;

        // Re-read the current state inside StartupRegistration rather than trusting the checkbox:
        // the click says what the user wants, the registry says what is true, and the two can
        // disagree if Task Manager changed things in between.
        string? error;
        var ok = turningOn
            ? StartupRegistration.TryEnable(out error)
            : StartupRegistration.TryDisable(out error);

        Log.Write(turningOn
            ? ok
                ? $"已开启开机自启：{StartupRegistration.CommandLine}"
                : $"开启开机自启失败：{error}"
            : ok
                ? "已关闭开机自启"
                : $"关闭开机自启失败：{error}");

        if (ok) return;

        // A checkbox that quietly springs back is worse than one that explains itself.
        System.Windows.MessageBox.Show(this,
            $"{Localization.Get("startup.failed")}\n\n{error}",
            Localization.Get("app.name"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        // Put the box back to what the registry actually says — without treating that as a click.
        _loading = true;
        StartupCheck.IsChecked = StartupRegistration.IsEnabled;
        _loading = false;
    }

    /// <summary>
    /// Flips the file / desktop context menu between the modern and classic shells.
    ///
    /// <para>
    /// Applied immediately rather than at restart, unlike the skin: this setting lives entirely in
    /// the registry, so the running process holds no copy of it. What the shell does with it is
    /// Explorer's problem, and Explorer is told to re-read via
    /// <see cref="ClassicContextMenu.NotifyShell"/> — with a manual restart button offered next to
    /// the switch, because that notification does not always take.
    /// </para>
    ///
    /// <para>
    /// On failure the tick is put back to match the registry, same as the start-up checkbox. A
    /// switch that silently springs back teaches the user nothing.
    /// </para>
    /// </summary>
    private void OnClassicMenuChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var turningOn = ClassicMenuCheck.IsChecked == true;

        var ok = turningOn
            ? ClassicContextMenu.TryEnable(out var error)
            : ClassicContextMenu.TryDisable(out error);

        Log.Write(turningOn
            ? ok ? "已开启经典右键菜单（文件/桌面）" : $"开启经典右键菜单失败：{error}"
            : ok ? "已关闭经典右键菜单（恢复 Win11）" : $"关闭经典右键菜单失败：{error}");

        if (ok)
        {
            ClassicContextMenu.NotifyShell();
            return;
        }

        System.Windows.MessageBox.Show(this,
            $"{Localization.Get("settings.classicmenu.failed")}\n\n{error}",
            Localization.Get("app.name"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        _loading = true;
        ClassicMenuCheck.IsChecked = ClassicContextMenu.IsEnabled;
        _loading = false;
    }

    /// <summary>
    /// Restarts Explorer, after asking.
    ///
    /// <para>
    /// Confirmed rather than done silently: this closes every open File Explorer window and blanks
    /// the desktop for a second, which is not something to inflict on someone who only wanted to
    /// tick a box.
    /// </para>
    /// </summary>
    private void OnRestartExplorer(object sender, RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(this,
            Localization.Get("settings.restart-explorer.confirm"),
            Localization.Get("app.name"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        if (ClassicContextMenu.TryRestartExplorer(out var error))
        {
            Log.Write("已重启资源管理器");
            return;
        }

        Log.Write($"重启资源管理器失败：{error}");
        System.Windows.MessageBox.Show(this,
            $"{Localization.Get("settings.restart-explorer.failed")}\n\n{error}",
            Localization.Get("app.name"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void OnSkinChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var skin = Win10Radio.IsChecked == true ? "win10" : "win11";
        if (skin == AppSettings.Skin) return;

        Log.Write($"设置面板：切换到 {skin} 皮肤");
        SkinChangeRequested?.Invoke(this, skin);
    }

    private void OnLanguageChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        string? tag = LanguageZhRadio.IsChecked == true ? "zh"
            : LanguageEnRadio.IsChecked == true ? "en"
            : null;

        // The stored value may be a full tag ("zh-CN") while the radio is only ever "zh", so
        // compare the language part only — otherwise picking the language you already have would
        // restart the app for nothing.
        if (string.Equals(ShortTag(tag), ShortTag(AppSettings.Language), StringComparison.Ordinal))
            return;

        Log.Write($"设置面板：界面语言 -> {tag ?? "跟随系统"}");
        LanguageChangeRequested?.Invoke(this, tag);
    }

    private static string? ShortTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var trimmed = tag.Trim();
        return (trimmed.Length <= 2 ? trimmed : trimmed[..2]).ToLowerInvariant();
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Log.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"打开日志失败: {ex.Message}");
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
