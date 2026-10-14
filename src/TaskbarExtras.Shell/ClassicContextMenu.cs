using Microsoft.Win32;

namespace TaskbarExtras.Shell;

/// <summary>
/// Switches the file / desktop / Explorer context menu between the Windows 11 modern one and the
/// classic Windows 10 one, by shadowing a single shell CLSID under HKCU.
///
/// <para>
/// <b>Why a registry toggle instead of drawing anything.</b> The Windows 11 context menu is not a
/// Win32 menu. On this machine it produces no <c>#32768</c> popup and no child window at all — it
/// is painted straight onto the shell's XAML composition surface. Measured three ways and
/// compared against a control (<c>Alt+Space</c> on Notepad, which does produce a real
/// <c>#32768</c>), so "nothing appeared" is a real result rather than a broken probe. There is no
/// <c>HMENU</c> to obtain, which means there is nothing to summon and nothing to re-skin. Full
/// evidence in docs/DESIGN.md §6.7.
/// </para>
///
/// <para>
/// What does work is the same trick <b>Shift+right-click</b> uses. Windows 11 draws its menu by
/// loading <c>{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}</c>, registered machine-wide as "File Explorer
/// Context Menu" pointing at <c>Windows.UI.FileExplorer.dll</c>. <c>HKCU\Software\Classes</c> is
/// resolved <i>before</i> <c>HKLM\Software\Classes</c>, so a copy of that key in HKCU with an empty
/// <c>InprocServer32</c> means COM finds no server to load and the shell falls back to the classic
/// menu. Shift does the same thing temporarily; we do it persistently.
/// </para>
///
/// <para>
/// <b>What this buys.</b> The menu is the system's own, not an imitation: every installed shell
/// extension (archive tools, version control, antivirus entries) is present, and it is
/// pixel-identical to Windows 10 because it <i>is</i> the Windows 10 menu. No injection, no hook,
/// no undocumented API — just a registry location, readable and removable by the user at will.
/// </para>
///
/// <para>
/// <b>What it cannot do.</b> We do not control how the classic menu looks; the shell decides. So
/// this is deliberately <i>not</i> part of the skin system — a skin describes how our own drawn
/// taskbar menu looks, and this switch has no appearance of its own to describe. That is also why
/// it is an independent option rather than something the Win11 skin could suppress: there is only
/// one classic look, and calling for the classic menu means calling for that look.
/// </para>
/// </summary>
public static class ClassicContextMenu
{
    /// <summary>
    /// The shell's "File Explorer Context Menu" handler. Registered under HKLM pointing at
    /// Windows.UI.FileExplorer.dll — that DLL is what paints the modern menu.
    /// </summary>
    private const string HandlerClsid = "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    private const string ClsidKeyPath = @"Software\Classes\CLSID\" + HandlerClsid;
    private const string InProcServerKeyPath = ClsidKeyPath + @"\InprocServer32";

    /// <summary>
    /// True when the classic menu is switched on.
    ///
    /// <para>
    /// The test is the empty value specifically, not merely the key's existence: the key is only
    /// meaningful because an empty <c>InprocServer32</c> defeats the HKLM registration. A key with
    /// a real path in it would be a different (and broken) thing, and reporting that as "on" would
    /// be wrong.
    /// </para>
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(InProcServerKeyPath, writable: false);
                return key?.GetValue(string.Empty) is string value && value.Length == 0;
            }
            catch
            {
                // Cannot prove it is on, so report off — same reasoning as StartupRegistration.
                return false;
            }
        }
    }

    /// <summary>
    /// Creates the HKCU shadow key. Writes at most two levels and never touches HKLM.
    /// </summary>
    /// <returns>True on success; <paramref name="error"/> explains a failure.</returns>
    public static bool TryEnable(out string? error)
    {
        error = null;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(InProcServerKeyPath, writable: true);
            if (key is null)
            {
                error = "无法创建注册表键";
                return false;
            }

            // Empty string, not absent. The value has to exist and be empty for the shadow to
            // take effect; deleting the value instead would leave HKLM winning again.
            key.SetValue(string.Empty, string.Empty, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Removes the HKCU shadow, restoring the Windows 11 menu.
    ///
    /// <para>
    /// Deletes the <c>InprocServer32</c> subkey, then the CLSID key — but <c>DeleteSubKey</c>
    /// refuses when a key still has children, which is exactly the guard wanted here. This class
    /// only ever created those two levels, so anything else under them is not ours to remove and
    /// the refusal is the correct outcome. Returns true when there was nothing to remove.
    /// </para>
    /// </summary>
    public static bool TryDisable(out string? error)
    {
        error = null;

        try
        {
            Registry.CurrentUser.DeleteSubKey(InProcServerKeyPath, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKey(ClsidKeyPath, throwOnMissingSubKey: false);
            return true;
        }
        catch (ArgumentException)
        {
            // A key still had children, so it is not ours to delete. The shadow value itself is
            // already gone by then — InprocServer32 is the innermost key — so the menu has
            // reverted, which is what the caller asked for.
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Tells the shell the class registrations changed, so it re-reads the CLSID and drops the
    /// menu handler it already resolved.
    ///
    /// <para>
    /// <c>SHChangeNotify</c> with <c>SHCNE_ASSOCCHANGED</c> is the documented "associations moved"
    /// signal and is the cheap first attempt — the common case then needs no restart at all.
    /// Explorer does not always drop a cached context-menu handler on this alone, which is why the
    /// settings panel also offers an explicit Explorer restart next to the switch.
    /// </para>
    ///
    /// <para>
    /// Best-effort by design: if it does nothing, the setting still applies at the next Explorer
    /// start, and surfacing a failure here would be noise rather than information.
    /// </para>
    /// </summary>
    public static void NotifyShell()
    {
        try
        {
            NativeMethods.SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Nothing useful to do or say; the setting is already written to the registry.
        }
    }

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    /// <summary>
    /// Restarts Explorer so it definitely picks up the change.
    ///
    /// <para>
    /// The blunt fallback for when <see cref="NotifyShell"/> is not enough. It is deliberately
    /// separate from the toggle rather than done automatically: killing Explorer closes every open
    /// File Explorer window and blanks the desktop for a second, which is far too disruptive to do
    /// behind the user's back. The panel asks first.
    /// </para>
    ///
    /// <para>
    /// Explorer is started detached, and it is started as <c>explorer.exe</c> with no arguments
    /// rather than by reviving the old process — that is what makes Windows treat it as a fresh
    /// shell start and re-read the registrations.
    /// </para>
    /// </summary>
    /// <returns>True when Explorer was signalled; <paramref name="error"/> explains a failure.</returns>
    public static bool TryRestartExplorer(out string? error)
    {
        error = null;

        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("explorer"))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    // One stubborn instance should not stop the others from going down.
                    error ??= ex.Message;
                }
                finally
                {
                    process.Dispose();
                }
            }

            // Windows does not always respawn the shell on its own, so start it explicitly.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true
            });

            return error is null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
