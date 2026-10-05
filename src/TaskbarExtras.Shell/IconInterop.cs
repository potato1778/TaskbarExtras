namespace TaskbarExtras.Shell;

/// <summary>
/// GDI icon handle helpers.
///
/// <para>
/// <c>Icon.FromHandle</c> does not take ownership of the HICON it is given, and
/// <c>Bitmap.GetHicon()</c> allocates one that nothing else will free. Getting this wrong leaks a
/// GDI object per call — invisible in testing, and a real resource leak in a long-running tray app.
/// </para>
/// </summary>
public static class IconInterop
{
    public static void DestroyIcon(IntPtr hIcon)
    {
        if (hIcon != IntPtr.Zero) NativeMethods.DestroyIcon(hIcon);
    }
}
