using System.Runtime.InteropServices;

namespace TaskbarExtras.Shell;

/// <summary>
/// Every native declaration used by this project.
/// Nothing outside this project is allowed to P/Invoke. See docs/DESIGN.md §3.1.
/// </summary>
internal static class NativeMethods
{
    // ---------------------------------------------------------------- AppBar

    internal const uint ABM_NEW = 0x00000000;
    internal const uint ABM_REMOVE = 0x00000001;
    internal const uint ABM_QUERYPOS = 0x00000002;
    internal const uint ABM_SETPOS = 0x00000003;
    internal const uint ABM_GETSTATE = 0x00000004;
    internal const uint ABM_GETTASKBARPOS = 0x00000005;
    internal const uint ABM_ACTIVATE = 0x00000006;
    internal const uint ABM_GETAUTOHIDEBAR = 0x00000007;
    internal const uint ABM_SETAUTOHIDEBAR = 0x00000008;
    internal const uint ABM_WINDOWPOSCHANGED = 0x00000009;
    internal const uint ABM_SETSTATE = 0x0000000A;
    internal const uint ABM_GETAUTOHIDEBAREX = 0x0000000B;
    internal const uint ABM_SETAUTOHIDEBAREX = 0x0000000C;

    internal const uint ABE_LEFT = 0;
    internal const uint ABE_TOP = 1;
    internal const uint ABE_RIGHT = 2;
    internal const uint ABE_BOTTOM = 3;

    internal const int ABN_STATECHANGE = 0;
    internal const int ABN_POSCHANGED = 1;
    internal const int ABN_FULLSCREENAPP = 2;
    internal const int ABN_WINDOWARRANGE = 3;

    [StructLayout(LayoutKind.Sequential)]
    internal struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    internal static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    // ---------------------------------------------------------------- WinEvent

    internal const uint EVENT_SYSTEM_MENUSTART = 0x00000004;
    internal const uint EVENT_SYSTEM_MENUEND = 0x00000005;
    internal const uint EVENT_SYSTEM_MENUPOPUPSTART = 0x0000000A;
    internal const uint EVENT_SYSTEM_MENUPOPUPEND = 0x0000000B;
    internal const uint EVENT_OBJECT_SHOW = 0x8002;

    internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    internal const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    internal delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // ---------------------------------------------------------------- Windows

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
    }

    internal const uint GW_OWNER = 4;
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;

    internal const uint WM_COMMAND = 0x0111;

    // Classic "Show Desktop" command IDs posted to Shell_TrayWnd.
    // VERIFIED WORKING on Windows 11 26H2 build 26300.9457 — see docs/DESIGN.md §5.1.
    // Semi-documented: these are taskbar internals, hence the COM path is preferred.
    internal const int MIN_ALL = 419;
    internal const int MIN_ALL_UNDO = 416;

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    internal static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // ---------------------------------------------------------------- Foreground / focus

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    // ---------------------------------------------------------------- IME

    /// <summary>
    /// Cancels an in-progress input-method composition. Needed because an IME sees a key before
    /// we do: when a mnemonic letter is consumed from the menu, the input method is already
    /// holding that character and would commit it later.
    /// </summary>
    internal const uint NI_COMPOSITIONSTR = 0x0015;
    internal const uint CPS_CANCEL = 0x0004;

    [DllImport("imm32.dll")]
    internal static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImmNotifyIME(IntPtr hIMC, uint dwAction, uint dwIndex, uint dwValue);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // ---------------------------------------------------------------- Monitors

    internal const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ---------------------------------------------------------------- DWM

    internal const uint DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, uint dwAttribute, out int pvAttribute, int cbAttribute);

    // ---------------------------------------------------------------- Low-level mouse hook

    internal const int WH_MOUSE_LL = 14;

    internal const int WM_LBUTTONDOWN = 0x0201;
    internal const int WM_RBUTTONDOWN = 0x0204;
    internal const int WM_RBUTTONUP = 0x0205;
    internal const int WM_MBUTTONDOWN = 0x0207;

    internal const uint LLMHF_INJECTED = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetWindowsHookExW")]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ---------------------------------------------------------------- Foreground

    internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    // ---------------------------------------------------------------- Console

    internal const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachConsole(int dwProcessId);

    // ---------------------------------------------------------------- Icons

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    // ---------------------------------------------------------------- DPI

    internal const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
