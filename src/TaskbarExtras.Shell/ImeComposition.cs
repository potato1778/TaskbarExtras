namespace TaskbarExtras.Shell;

/// <summary>
/// Discards an in-progress input-method composition.
///
/// <para>
/// An IME sees a keystroke before the application does. When a mnemonic letter is consumed from
/// the menu, the input method is already holding that character in its composition buffer, and
/// it would be committed the next time the user types — a stray "x" appearing out of nowhere.
/// Cancelling the composition throws it away.
/// </para>
/// <para>
/// Only ever called after a mnemonic has matched, so it cannot interrupt someone who is actually
/// typing.
/// </para>
/// </summary>
public static class ImeComposition
{
    public static void Cancel(IntPtr window)
    {
        if (window == IntPtr.Zero) return;

        var context = NativeMethods.ImmGetContext(window);
        if (context == IntPtr.Zero) return;   // no IME attached to this window

        try
        {
            NativeMethods.ImmNotifyIME(context, NativeMethods.NI_COMPOSITIONSTR, NativeMethods.CPS_CANCEL, 0);
        }
        finally
        {
            NativeMethods.ImmReleaseContext(window, context);
        }
    }
}
