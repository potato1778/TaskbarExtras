using System.Runtime.InteropServices;

namespace TaskbarExtras.Shell;

public sealed class TaskbarRightClickEventArgs : EventArgs
{
    public int X { get; init; }
    public int Y { get; init; }
    /// <summary>True when the click was synthetic (SendInput / mouse_event) rather than physical.</summary>
    public bool Injected { get; init; }
}

public enum MouseButton { Left, Right, Middle }

public sealed class MouseButtonDownEventArgs : EventArgs
{
    public MouseButton Button { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
}

/// <summary>
/// Swallows a right-click on the taskbar's empty area before the shell ever sees it, then
/// reports it so the host can show its own menu.
///
/// <para><b>Why a low-level mouse hook rather than a WinEvent?</b> The first implementation
/// watched for the shell's own menu to appear (<c>EVENT_OBJECT_SHOW</c> on
/// <c>Xaml_WindowedPopupClass</c>) and then dismissed it. That worked, but it was measurably
/// bad in two ways, both reported by the first user to try it:</para>
/// <list type="number">
///   <item>the native menu <b>flashes on screen first</b>, because you cannot replace a thing
///         before it exists;</item>
///   <item>it feels <b>laggy</b>, because the whole round trip is
///         native-menu-shown → WinEvent delivered → dismiss → layout our menu.</item>
/// </list>
/// <para>
/// A hook inverts the order: the click is swallowed at the source, so the native menu never
/// appears and our menu is the only thing the user ever sees. The cost is that this is a global
/// input hook, which is why it is scoped as tightly as possible — see
/// <see cref="TaskbarInfo.IsOverEmptyTaskbarArea"/>.
/// </para>
///
/// <para>
/// <b>Threading:</b> install it on a thread with a message loop. <c>WH_MOUSE_LL</c> callbacks are
/// delivered to the installing thread, so in this app they run on the WPF UI thread. That means
/// handlers may touch WPF objects directly — but they run <i>inside</i> the hook, so they must
/// return fast (the system unhooks a hook that takes longer than <c>LowLevelHooksTimeout</c>,
/// 300 ms by default, and a hook that blocks stalls mouse input system-wide). The handler here
/// does one cheap rectangle test and posts the rest to the dispatcher.
/// </para>
/// </summary>
public sealed class TaskbarRightClickHook : IDisposable
{
    private readonly NativeMethods.LowLevelMouseProc _callback; // MUST be kept alive, or the GC collects the thunk
    private IntPtr _hook;
    private bool _disposed;

    public TaskbarRightClickHook()
    {
        _callback = OnMouse;
    }

    /// <summary>Master switch, so the hook can be disabled without uninstalling it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Raised when a right-click on empty taskbar space has already been swallowed. Fired on the
    /// hook thread — do not show windows synchronously from here.
    /// </summary>
    public event EventHandler<TaskbarRightClickEventArgs>? RightClickSwallowed;

    /// <summary>
    /// Raised for every button-down anywhere on screen, on the hook thread. Used by the host to
    /// dismiss an open menu when the user clicks elsewhere — which is why clicking outside works
    /// even though the menu never takes focus.
    /// </summary>
    public event EventHandler<MouseButtonDownEventArgs>? ButtonDown;

    /// <summary>
    /// Raised on the hook thread whenever a right-click lands on the taskbar, whether we took it
    /// or not, with the reason. Without this, "the hook did not fire" and "the hook fired but
    /// decided not to act" are indistinguishable from the outside.
    /// </summary>
    public event EventHandler<string>? Diagnostic;

    public bool IsRunning => _hook != IntPtr.Zero;

    public bool Start()
    {
        if (_hook != IntPtr.Zero) return true;
        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _callback,
            NativeMethods.GetModuleHandle(null),
            0);
        return _hook != IntPtr.Zero;
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        var message = (int)wParam.ToInt64();
        var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

        // Dismissal runs for every button-down, including clicks we do not swallow.
        var button = message switch
        {
            NativeMethods.WM_LBUTTONDOWN => (MouseButton?)MouseButton.Left,
            NativeMethods.WM_RBUTTONDOWN => MouseButton.Right,
            NativeMethods.WM_MBUTTONDOWN => MouseButton.Middle,
            _ => null
        };
        if (button is { } pressed)
        {
            ButtonDown?.Invoke(this, new MouseButtonDownEventArgs
            {
                Button = pressed,
                X = info.pt.X,
                Y = info.pt.Y
            });
        }

        if (!Enabled) return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        // Only the right button.
        if (message != NativeMethods.WM_RBUTTONDOWN && message != NativeMethods.WM_RBUTTONUP)
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        // Diagnostics only for right-clicks that actually land on the taskbar, so this stays
        // quiet in normal use.
        var overTaskbar = TaskbarInfo.TryGetRect(out var taskbarRect)
                          && taskbarRect.Contains(info.pt.X, info.pt.Y);
        if (!overTaskbar) return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        var overEmpty = TaskbarInfo.IsOverEmptyTaskbarArea(info.pt.X, info.pt.Y, out var blocking);
        Diagnostic?.Invoke(this,
            $"任务栏内右键 ({info.pt.X},{info.pt.Y}) 空白处={overEmpty} 阻挡={blocking ?? "-"}");

        if (!overEmpty) return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        // Swallow BOTH the down and the up. Which one the shell uses to open its menu is an
        // implementation detail; eating both makes it irrelevant.
        if (message == NativeMethods.WM_RBUTTONDOWN)
        {
            RightClickSwallowed?.Invoke(this, new TaskbarRightClickEventArgs
            {
                X = info.pt.X,
                Y = info.pt.Y,
                Injected = (info.flags & NativeMethods.LLMHF_INJECTED) != 0
            });
        }

        return new IntPtr(1); // non-zero = do not pass the message on
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
