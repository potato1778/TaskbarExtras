"""Minimise every window that covers the desktop, then report desktop reachability.

The overlay problem is environmental, not a probe bug: a maximised Chrome window owns
every sampled point. Minimising is safe and reversible, and it is the only way to reach a
desktop coordinate without touching the mouse or the user's window layout permanently.
"""

import ctypes
import ctypes.wintypes as w
import subprocess

user32 = ctypes.WinDLL("user32", use_last_error=True)

user32.WindowFromPoint.restype = w.HWND
user32.WindowFromPoint.argtypes = [w.POINT]
user32.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
user32.GetWindowThreadProcessId.argtypes = [w.HWND, ctypes.POINTER(w.DWORD)]
user32.GetAncestor.restype = w.HWND

SW_MINIMIZE = 6
GA_ROOT = 2


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


def desktop_hit():
    """First sampled point whose top-level owner is the desktop, or None."""
    desktop = {"Progman", "WorkerW", "SHELLDLL_DefView"}
    for y in range(4, user32.GetSystemMetrics(1) - 4, 60):
        for x in range(4, user32.GetSystemMetrics(0) - 4, 60):
            leaf = user32.WindowFromPoint(w.POINT(x, y))
            if not leaf:
                continue
            top = user32.GetAncestor(leaf, GA_ROOT) or leaf
            if class_name(top) in desktop:
                return x, y, top
    return None


def covers():
    """Top-level windows owning sampled points, deduped."""
    seen = {}
    for y in range(4, user32.GetSystemMetrics(1) - 4, 90):
        for x in range(4, user32.GetSystemMetrics(0) - 4, 90):
            leaf = user32.WindowFromPoint(w.POINT(x, y))
            if not leaf:
                continue
            top = user32.GetAncestor(leaf, GA_ROOT) or leaf
            seen.setdefault(top, class_name(top))
    return seen


def main():
    print("before:", "desktop reachable" if desktop_hit() else "desktop covered")

    covers_now = covers()
    print(f"windows owning screen points: {len(covers_now)}")
    minimised = []
    for hwnd, cls in covers_now.items():
        if cls in {"Progman", "WorkerW", "SHELLDLL_DefView"}:
            continue
        if user32.IsIconic(hwnd):
            continue
        if user32.ShowWindow(hwnd, SW_MINIMIZE):
            minimised.append((hwnd, cls, pid_of(hwnd)))

    for hwnd, cls, pid in minimised:
        print(f"  minimised 0x{hwnd:X} {cls} ({proc_name(pid)})")
    print(f"minimised {len(minimised)} window(s)")

    hit = desktop_hit()
    print("after :", "desktop reachable" if hit else "desktop STILL covered")
    if hit:
        print(f"  first desktop point: ({hit[0]}, {hit[1]}) hwnd=0x{hit[2]:X} {class_name(hit[2])}")


if __name__ == "__main__":
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        pass
    main()
