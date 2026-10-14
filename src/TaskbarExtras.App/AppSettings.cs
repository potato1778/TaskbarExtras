using System.IO;
using System.Text.Json;

namespace TaskbarExtras.App;

/// <summary>
/// The handful of choices that must survive a restart.
///
/// <para>
/// Until now every setting was a command-line flag that died with the process: launch with
/// <c>--skin win10</c> and you got the Windows 10 skin, but only for that run. Since the app is
/// usually started at sign-in with no arguments at all, the choice was effectively
/// unmakeable — pick a skin, reboot, and it silently reverted to the default. This file is what
/// fixes that.
/// </para>
///
/// <para>
/// Stored as JSON under <c>%APPDATA%\TaskbarExtras\settings.json</c>. Deliberately not the
/// registry: this is user configuration, it belongs with the user's files, it is easy to inspect
/// and easy to delete. Writing is best-effort — a settings file that cannot be written must never
/// take the app down, it just means the choice is not remembered.
/// </para>
///
/// <para>
/// Precedence at startup is <b>command line &gt; this file &gt; built-in default</b>. The command
/// line wins so that <c>--skin win11</c> still works as a one-off override, and so that
/// <c>--preview</c> can show a skin without committing to it.
/// </para>
/// </summary>
public static class AppSettings
{
    /// <summary>
    /// Skin used when neither the command line nor the settings file names one.
    ///
    /// <para>
    /// Windows 10 rather than Windows 11, deliberately. The whole point of this program is to put
    /// back what Windows 11 removed, so the honest default is the menu the machine's owner is
    /// actually trying to get back — the Windows 10 one. Defaulting to the native Windows 11 look
    /// would make the app blend in so well that a new user could not tell it was running.
    /// </para>
    ///
    /// <para>
    /// Note that this is only the <i>fallback</i>: an existing <c>settings.json</c> wins over it,
    /// so upgrading does not yank the skin out from under anyone who already chose one. Only a
    /// fresh install — or a deleted settings file — lands here.
    /// </para>
    /// </summary>
    public const string DefaultSkin = "win10";

    private static readonly string Directory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarExtras");

    private static readonly string FilePath = Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Skin name, e.g. <c>win10</c>. Never null; falls back to <see cref="DefaultSkin"/>.</summary>
    public static string Skin { get; private set; } = DefaultSkin;

    /// <summary>Language tag such as <c>en</c> / <c>zh</c>, or null to follow the OS.</summary>
    public static string? Language { get; private set; }

    public static string Location => FilePath;

    /// <summary>
    /// Reads the file, if there is one. A malformed or unreadable file is treated as "no file":
    /// the app must start, and a corrupt settings file would otherwise make it unfixable without
    /// knowing where to look.
    /// </summary>
    public static void Load()
    {
        try
        {
            if (!System.IO.File.Exists(FilePath)) return;

            var model = JsonSerializer.Deserialize<Model>(System.IO.File.ReadAllText(FilePath));
            if (model is null) return;

            if (!string.IsNullOrWhiteSpace(model.Skin)) Skin = model.Skin.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(model.Language)) Language = model.Language.Trim();
        }
        catch (Exception ex)
        {
            Log.Write($"读取设置失败，使用默认值: {ex.Message}");
        }
    }

    /// <summary>
    /// Persists the current values. Returns false when the write failed; callers surface that to
    /// the user rather than pretending the choice stuck.
    /// </summary>
    public static bool Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var json = JsonSerializer.Serialize(new Model { Skin = Skin, Language = Language }, JsonOptions);
            System.IO.File.WriteAllText(FilePath, json);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"保存设置失败: {ex.Message}");
            return false;
        }
    }

    public static void SetSkin(string skin)
    {
        Skin = string.IsNullOrWhiteSpace(skin) ? DefaultSkin : skin.Trim().ToLowerInvariant();
    }

    public static void SetLanguage(string? language)
    {
        Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
    }

    private sealed class Model
    {
        public string? Skin { get; set; }
        public string? Language { get; set; }
    }
}
