"""Right-click the desktop and screenshot the menu that appears.

Prerequisites met: a real desktop point (Progman) is reachable now that the covering
windows have been minimised. This script:

  1. restores the taskbar (it was minimised as collateral damage),
  2. picks a desktop point that is not under the taskbar and not under a desktop icon,
  3. synthesises a right-click there,
  4. grabs the screen,
  5. dismisses the menu with Escape.

The screenshot is the ground truth for "did the classic menu come back". Run it once with
the shadow key on and once with it off and compare the two images.
"""

import ctypes
import ctypes.wintypes as w
import sys
import time

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)

SW_RESTORE = 9
MOUSEEVENTF_RIGHTDOWN = 0x0008
MOUSEEVENTF_RIGHTUP = 0x0010

user32.WindowFromPoint.restype = w.HWND
user32.WindowFromPoint.argtypes = [w.POINT]
user32.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
user32.GetAncestor.restype = w.HWND
user32.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]

ULONG_PTR = ctypes.c_ulonglong if ctypes.sizeof(ctypes.c_void_p) == 8 else ctypes.c_ulong


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [
        ("dx", w.LONG),
        ("dy", w.LONG),
        ("mouseData", w.DWORD),
        ("dwFlags", w.DWORD),
        ("time", w.DWORD),
        ("dwExtraInfo", ULONG_PTR),
    ]


class INPUT(ctypes.Structure):
    class _U(ctypes.Union):
        _fields_ = [("mi", MOUSEINPUT)]

    _anonymous_ = ("u",)
    _fields_ = [("type", w.DWORD), ("u", _U)]


def class_name(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def owner_class(x, y):
    leaf = user32.WindowFromPoint(w.POINT(x, y))
    if not leaf:
        return ""
    top = user32.GetAncestor(leaf, 2) or leaf
    return class_name(top)


def restore_taskbar():
    hwnd = user32.FindWindowW("Shell_TrayWnd", None)
    if hwnd:
        user32.ShowWindow(hwnd, SW_RESTORE)
        return True
    return False


def pick_point():
    """A desktop pixel clear of the taskbar and of the icon grid at the top-left."""
    screen_w = user32.GetSystemMetrics(0)
    screen_h = user32.GetSystemMetrics(1)
    tray = user32.FindWindowW("Shell_TrayWnd", None)
    tray_top = screen_h
    if tray:
        r = w.RECT()
        user32.GetWindowRect(tray, ctypes.byref(r))
        tray_top = r.top

    for y in range(screen_h // 2, tray_top - 40, 20):
        for x in range(screen_w // 2, screen_w - 40, 20):
            if owner_class(x, y) in {"Progman", "WorkerW"}:
                return x, y
    return None


def right_click(x, y):
    user32.SetCursorPos(x, y)
    time.sleep(0.25)
    for flag_down, flag_up in ((MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),):
        for flag in (flag_down, flag_up):
            inp = INPUT()
            inp.type = 0  # INPUT_MOUSE
            inp.mi = MOUSEINPUT(x, y, 0, flag, 0, 0)
            user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))
            time.sleep(0.08)


def grab(path):
    """BitBlt the primary screen into a PNG."""
    from PIL import Image

    screen_w = user32.GetSystemMetrics(0)
    screen_h = user32.GetSystemMetrics(1)
    hdc = user32.GetDC(0)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, screen_w, screen_h)
    gdi32.SelectObject(mem, bmp)

    SRCCOPY = 0x00CC0020
    gdi32.BitBlt(mem, 0, 0, screen_w, screen_h, hdc, 0, 0, SRCCOPY)

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [
            ("biSize", w.DWORD),
            ("biWidth", w.LONG),
            ("biHeight", w.LONG),
            ("biPlanes", w.WORD),
            ("biBitCount", w.WORD),
            ("biCompression", w.DWORD),
            ("biSizeImage", w.DWORD),
            ("biXPelsPerMeter", w.LONG),
            ("biYPelsPerMeter", w.LONG),
            ("biClrUsed", w.DWORD),
            ("biClrImportant", w.DWORD),
        ]

    hdr = BITMAPINFOHEADER()
    hdr.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    hdr.biWidth = screen_w
    hdr.biHeight = -screen_h  # top-down
    hdr.biPlanes = 1
    hdr.biBitCount = 32
    hdr.biCompression = 0

    buf = ctypes.create_string_buffer(screen_w * screen_h * 4)
    gdi32.GetDIBits(mem, bmp, 0, screen_h, buf, ctypes.byref(hdr), 0)

    img = Image.frombuffer("RGBA", (screen_w, screen_h), buf, "raw", "BGRA", 0, 1)
    img.convert("RGB").save(path)

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(0, hdc)


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "desktop_menu.png"

    restored = restore_taskbar()
    print(f"taskbar restored: {restored}")
    time.sleep(0.6)

    pt = pick_point()
    if pt is None:
        print("FAIL: no clear desktop point")
        return 1
    print(f"right-clicking desktop at {pt}  (owner={owner_class(*pt)})")

    right_click(*pt)
    time.sleep(1.1)
    grab(out)
    print(f"saved {out}")

    # Dismiss, then put the pointer somewhere neutral.
    user32.keybd_event(0x1B, 0, 0, 0)  # VK_ESCAPE down
    user32.keybd_event(0x1B, 0, 2, 0)  # up
    time.sleep(0.3)
    user32.SetCursorPos(user32.GetSystemMetrics(0) // 2, 30)
    print("menu dismissed")
    return 0


if __name__ == "__main__":
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        pass
    sys.exit(main())
