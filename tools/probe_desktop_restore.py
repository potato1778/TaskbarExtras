"""Restore the windows that probe_desktop_uncover.py minimised, so the desktop is left
exactly as it was found.

Minimising was necessary to reach a desktop coordinate; un-minimising is the courtesy that
keeps the probe from being a nuisance. Windows are matched by class name rather than by
remembered handles, because the handles may be gone by the time this runs.
"""

import ctypes
import ctypes.wintypes as w
import subprocess

user32 = ctypes.WinDLL("user32", use_last_error=True)
SW_RESTORE = 9
SW_SHOW = 5

user32.EnumWindows.restype = w.BOOL
user32.IsIconic.restype = w.BOOL
user32.IsWindowVisible.restype = w.BOOL


def class_name(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def pid_of(hwnd):
    pid = w.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return pid.value


def proc_name(pid):
    out = subprocess.run(
        ["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
        capture_output=True,
        text=True,
    ).stdout.strip()
    parts = out.split(",")
    return parts[0].strip('"') if parts and parts[0].startswith('"') else "?"


# Classes that were minimised and must come back. Notably *not* WebView / pet overlays —
# only what our own probe put away.
TARGETS = {"Chrome_WidgetWin_1", "Shell_TrayWnd"}


def main():
    restored = []

    def visit(hwnd, _):
        cls = class_name(hwnd)
        if cls not in TARGETS:
            return True
        if not user32.IsWindowVisible(hwnd):
            return True
        if not user32.IsIconic(hwnd):
            return True
        user32.ShowWindow(hwnd, SW_RESTORE if cls != "Shell_TrayWnd" else SW_SHOW)
        restored.append((cls, pid_of(hwnd)))
        return True

    WNDENUMPROC = ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    user32.EnumWindows(WNDENUMPROC(visit), 0)

    for cls, pid in restored:
        print(f"  restored {cls} ({proc_name(pid)})")
    print(f"restored {len(restored)} window(s)")
    if not restored:
        print("nothing was minimised — desktop left as found")


if __name__ == "__main__":
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        pass
    main()
