using Microsoft.Win32;

namespace TaskbarExtras.Shell;

/// <summary>
/// Makes the app start with the user's session, using the per-user Run key.
///
/// <para>
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> rather than the machine-wide
/// equivalent: this app has no business being elevated, and a tool that installs a global mouse
/// hook asking for administrator rights is a red flag. Starting with the user's own session is
/// all that is needed anyway.
/// </para>
/// <para>
/// The Run key is only half the story. Task Manager's Startup tab can disable an entry without
/// deleting it — Windows records that decision somewhere else, as a binary blob under
/// <c>...\Explorer\StartupApproved\Run</c>. A disabled entry stays in the Run key looking
/// perfectly healthy, so a check that only reads the Run key would put a tick next to something
/// that never actually starts. <see cref="IsEnabled"/> reads both.
/// </para>
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovalKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>Name of the Run value. Also the name Task Manager shows.</summary>
    private const string ValueName = "TaskbarExtras";

    /// <summary>
    /// Written into the registered command line so the app can tell it was launched by Windows
    /// rather than by a person. It does not change behaviour today; it makes the log readable.
    /// </summary>
    public const string StartupArgument = "--startup";

    /// <summary>Full path of the running executable, or null if it cannot be determined.</summary>
    public static string? ExecutablePath => Environment.ProcessPath;

    /// <summary>The exact command line stored in the Run key.</summary>
    public static string CommandLine => ExecutablePath is { } path ? $"\"{path}\" {StartupArgument}" : string.Empty;

    /// <summary>
    /// True when Windows will actually start this app at sign-in: an entry exists, it points at
    /// <i>this</i> copy of the executable, and it has not been disabled from Task Manager.
    ///
    /// <para>
    /// The path check matters because the exe moves. Downloading a new build to a different
    /// folder leaves the old registration behind, pointing at a file that may no longer exist —
    /// and reporting that as "enabled" would be a lie.
    /// </para>
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                if (run?.GetValue(ValueName, defaultValue: null) is not string stored) return false;
                if (!TargetsThisExecutable(stored)) return false;
                return !IsDisabledInTaskManager();
            }
            catch
            {
                // A registry read that fails means "cannot prove it is enabled", which for a tick
                // mark should read as off.
                return false;
            }
        }
    }

    /// <summary>Registers the app to start at sign-in. Overwrites whatever was there before.</summary>
    public static bool TryEnable(out string? error)
    {
        error = null;

        if (ExecutablePath is null)
        {
            error = "无法确定自身可执行文件路径";
            return false;
        }

        try
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true))
            {
                if (run is null)
                {
                    error = "无法打开 Run 注册表键";
                    return false;
                }

                run.SetValue(ValueName, CommandLine, RegistryValueKind.String);
            }

            // A "disabled" decision from Task Manager lives in a different key and would keep
            // overriding the value just written. Clearing it is what makes the menu item behave
            // like a switch: turning it on has to win over an earlier off, wherever that off
            // was recorded.
            try
            {
                using var approval = Registry.CurrentUser.OpenSubKey(ApprovalKeyPath, writable: true);
                approval?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch
            {
                // Best effort. Worst case the entry is enabled but still suppressed, and the
                // menu will show it as off next time it opens — which is the truth.
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Removes the registration. Returns true even if there was nothing to remove.</summary>
    public static bool TryDisable(out string? error)
    {
        error = null;

        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// True when Task Manager has disabled the entry.
    ///
    /// <para>
    /// The record is a 12-byte REG_BINARY whose first byte is the state: 2 (and 6, seen on some
    /// builds) means enabled, 3 means disabled. No record at all means Windows has never been
    /// asked to disable it, which counts as enabled.
    /// </para>
    /// </summary>
    private static bool IsDisabledInTaskManager()
    {
        try
        {
            using var approval = Registry.CurrentUser.OpenSubKey(ApprovalKeyPath, writable: false);
            if (approval?.GetValue(ValueName) is not byte[] { Length: > 0 } state) return false;
            return state[0] is not (0x02 or 0x06);
        }
        catch
        {
            return false;
        }
    }

    private static bool TargetsThisExecutable(string storedCommand)
    {
        var target = ExtractExecutablePath(storedCommand);
        return target is not null
            && ExecutablePath is { } self
            && string.Equals(target, self, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pulls the executable path out of a command line. Quoted paths win — that is why the
    /// command line is written with quotes in the first place, since <c>C:\Program Files\…</c>
    /// would otherwise be split at the first space.
    /// </summary>
    private static string? ExtractExecutablePath(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) return null;

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }
}
