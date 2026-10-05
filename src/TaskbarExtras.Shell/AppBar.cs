namespace TaskbarExtras.Shell;

public sealed class AppBarNotificationEventArgs : EventArgs
{
    public int Code { get; init; }
    public string Name => Code switch
    {
        NativeMethods.ABN_STATECHANGE => nameof(NativeMethods.ABN_STATECHANGE),
        NativeMethods.ABN_POSCHANGED => nameof(NativeMethods.ABN_POSCHANGED),
        NativeMethods.ABN_FULLSCREENAPP => nameof(NativeMethods.ABN_FULLSCREENAPP),
        NativeMethods.ABN_WINDOWARRANGE => nameof(NativeMethods.ABN_WINDOWARRANGE),
        _ => $"ABN_{Code}"
    };
}

/// <summary>
/// Thin wrapper over the documented AppBar protocol (<c>SHAppBarMessage</c>).
///
/// <para><b>Measured behaviour on 26H2 build 26300.9457 (Spike S1):</b></para>
/// <list type="bullet">
///   <item>Registering a bottom-edge AppBar while the real taskbar occupies the bottom edge
///         makes ours stack <i>above</i> the taskbar. The taskbar is not moved.</item>
///   <item><c>ABM_QUERYPOS</c> returned our requested rect unchanged.</item>
///   <item>The system <i>does</i> reserve space: <c>rcWork</c> lost exactly our height.</item>
///   <item><c>ABM_REMOVE</c> restores <c>rcWork</c> exactly.</item>
///   <item><b>Gotcha:</b> the reserved band is always the FULL edge, no matter how narrow the
///         window is. Asking for a 500px-wide bar still costs a 2560px-wide band. That is why
///         the bar is an opt-in feature rather than the default UI — see docs/DESIGN.md §4.4.</item>
/// </list>
///
/// <para>
/// The host window must call <see cref="HandleMessage"/> from its window procedure, otherwise
/// <c>ABN_POSCHANGED</c> is missed and other AppBars will push ours out of position.
/// </para>
/// </summary>
public sealed class AppBar : IDisposable
{
    /// <summary>The message id the shell will use to notify us. Must be handled by the host window.</summary>
    public const int CallbackMessage = 0x8001; // WM_APP + 1

    private IntPtr _hwnd;
    private bool _registered;
    private bool _disposed;

    public event EventHandler<AppBarNotificationEventArgs>? Notification;

    public bool IsRegistered => _registered;

    public bool Register(IntPtr hwnd)
    {
        if (_registered) return true;
        _hwnd = hwnd;
        var data = NewData(hwnd);
        data.uCallbackMessage = CallbackMessage;
        var result = NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data);
        _registered = result != UIntPtr.Zero;
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        var data = NewData(_hwnd);
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);
        _registered = false;
    }

    /// <summary>
    /// Query-then-set. The height correction between the two calls is mandatory: the shell is
    /// free to return a zero-height rect from QUERYPOS, and committing that verbatim yields an
    /// invisible bar.
    /// </summary>
    public bool TrySetPosition(uint edge, int thickness, out ScreenRect actual)
    {
        actual = default;
        if (!_registered) return false;
        if (!TaskbarInfo.TryGetWorkArea(out var work)) return false;

        var data = NewData(_hwnd);
        data.uEdge = edge;
        data.rc = edge switch
        {
            NativeMethods.ABE_BOTTOM => new NativeMethods.RECT { Left = work.Left, Top = work.Bottom - thickness, Right = work.Right, Bottom = work.Bottom },
            NativeMethods.ABE_TOP => new NativeMethods.RECT { Left = work.Left, Top = work.Top, Right = work.Right, Bottom = work.Top + thickness },
            NativeMethods.ABE_LEFT => new NativeMethods.RECT { Left = work.Left, Top = work.Top, Right = work.Left + thickness, Bottom = work.Bottom },
            NativeMethods.ABE_RIGHT => new NativeMethods.RECT { Left = work.Right - thickness, Top = work.Top, Right = work.Right, Bottom = work.Bottom },
            _ => throw new ArgumentOutOfRangeException(nameof(edge))
        };

        NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref data);

        // Re-assert our thickness — the shell may have shrunk it.
        switch (edge)
        {
            case NativeMethods.ABE_BOTTOM: data.rc.Top = data.rc.Bottom - thickness; break;
            case NativeMethods.ABE_TOP: data.rc.Bottom = data.rc.Top + thickness; break;
            case NativeMethods.ABE_LEFT: data.rc.Right = data.rc.Left + thickness; break;
            case NativeMethods.ABE_RIGHT: data.rc.Left = data.rc.Right - thickness; break;
        }

        var ok = NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref data) != UIntPtr.Zero;
        actual = new ScreenRect(data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);
        return ok;
    }

    /// <summary>The rect of the real Windows taskbar, as reported by the shell.</summary>
    public static bool TryGetTaskbarRect(out ScreenRect rect)
    {
        rect = default;
        var data = new NativeMethods.APPBARDATA { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.APPBARDATA>() };
        if (NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETTASKBARPOS, ref data) == UIntPtr.Zero) return false;
        rect = new ScreenRect(data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);
        return true;
    }

    /// <summary>Call this from the host window procedure for <see cref="CallbackMessage"/>.</summary>
    public void HandleMessage(int message, IntPtr lParam)
    {
        if (message != CallbackMessage) return;
        var code = (int)lParam.ToInt64();
        Notification?.Invoke(this, new AppBarNotificationEventArgs { Code = code });

        if (code == NativeMethods.ABN_POSCHANGED)
        {
            // Something else moved us. Re-run the query/set cycle.
            TrySetPosition(_edge, _thickness, out _);
        }
    }

    private uint _edge = NativeMethods.ABE_BOTTOM;
    private int _thickness;

    /// <summary>Remember the last requested geometry so ABN_POSCHANGED can re-apply it.</summary>
    public bool TrySetPositionAndRemember(uint edge, int thickness, out ScreenRect actual)
    {
        _edge = edge;
        _thickness = thickness;
        return TrySetPosition(edge, thickness, out actual);
    }

    private static NativeMethods.APPBARDATA NewData(IntPtr hwnd) => new()
    {
        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.APPBARDATA>(),
        hWnd = hwnd
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Forgetting this leaves a ghost AppBar reserving screen space until explorer restarts.
        Unregister();
    }
}
