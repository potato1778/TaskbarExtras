namespace TaskbarExtras.Shell;

/// <summary>
/// Brings one of our own windows to the foreground in the one situation where Windows refuses to.
///
/// <para>
/// <b>The problem.</b> The replacement menu is opened by a low-level mouse hook that swallows the
/// right-click. That is what makes it feel instant, but it also means <b>no window received the
/// input</b> — and Windows only lets a process set the foreground window if it is already in
/// front, was started by the process that is, or received the input that prompted the change.
/// We satisfy none of those. So <c>SetForegroundWindow</c> quietly fails, the menu appears but
/// is not focused, and every keystroke goes to whatever was in front before — which is exactly
/// how the mnemonic keys were found to be dead in normal use while working perfectly in
/// <c>--preview</c>, where the process had only just started and was allowed to come forward.
/// </para>
/// <para>
/// <b>The way around it.</b> Attach our thread's input queue to the current foreground thread's
/// for the duration of the call. While attached, the two threads share an input state and the
/// foreground check sees us as part of the thread that already has it. This is the long-standing
/// documented workaround; the attach is released immediately afterwards so the queues are only
/// joined for the two calls.
/// </para>
/// </summary>
public static class WindowFocus
{
    /// <summary>
    /// Tries to make <paramref name="window"/> the foreground window. Returns true when it worked.
    /// </summary>
    /// <param name="detail">
    /// What each step did, for the log. This path has several places to fail silently — the
    /// foreground call being refused, the attach being refused, SetFocus returning null — and
    /// they all look the same from the outside.
    /// </param>
    public static bool TryBringToForeground(IntPtr window, out string detail)
    {
        detail = "未执行";
        if (window == IntPtr.Zero)
        {
            detail = "窗口句柄为空";
            return false;
        }

        // The cheap path first: if the foreground lock happens to allow it, take it.
        if (NativeMethods.GetForegroundWindow() == window)
        {
            detail = "本来就是前台";
            return true;
        }

        if (NativeMethods.SetForegroundWindow(window))
        {
            detail = "SetForegroundWindow 直接成功";
            return true;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var thisThread = NativeMethods.GetCurrentThreadId();

        if (foregroundThread == 0 || foregroundThread == thisThread)
        {
            detail = $"SetForegroundWindow 失败，且前台线程 {foregroundThread} 不可附加（本线程 {thisThread}）";
            return false;
        }

        if (!NativeMethods.AttachThreadInput(thisThread, foregroundThread, true))
        {
            detail = $"SetForegroundWindow 失败，AttachThreadInput 也被拒（前台线程 {foregroundThread}）";
            return false;
        }

        var focusResult = IntPtr.Zero;
        try
        {
            NativeMethods.SetForegroundWindow(window);
            focusResult = NativeMethods.SetFocus(window);
        }
        finally
        {
            // Always detach: leaving two threads sharing an input queue means one of them
            // blocking stalls the other's input too.
            NativeMethods.AttachThreadInput(thisThread, foregroundThread, false);
        }

        var nowForeground = NativeMethods.GetForegroundWindow();
        var ok = nowForeground == window;
        detail = $"附加到线程 {foregroundThread} 后：前台={(ok ? "本窗口" : "仍不是")}，SetFocus={(focusResult == IntPtr.Zero ? "返回 NULL" : "成功")}";
        return ok;
    }
}
