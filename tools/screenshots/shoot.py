"""Regenerate the README screenshots.

Usage (from the repo root, after a Release build):

    python tools/screenshots/shoot.py

Writes docs/screenshot-{skin}-{lang}.png for the menu and
docs/screenshot-settings-{skin}-{lang}.png for the settings panel, for
win11/win10 x zh/en.

Two things make this less obvious than it looks:

1.  **Screen-DC BitBlt does not work.** The `--preview` menu hides — and the
    process quits — the moment anything steals focus, so by the time the pixels
    are grabbed the window is gone and the capture is desktop wallpaper.
    `PrintWindow` with `PW_RENDERFULLCONTENT` renders the window into a memory
    DC without needing it to be foreground, and without moving the mouse.

2.  **The two windows cannot be told apart by title.** Both are WPF
    `HwndWrapper[...]` windows in the same process, and the settings panel's
    title is translated. The window *style* is stable: the menu sets
    `ShowInTaskbar=False` (WS_EX_TOOLWINDOW, no WS_EX_APPWINDOW) and the panel
    is an ordinary window (WS_EX_APPWINDOW).
"""

import ctypes
import os
import subprocess
import sys
import time
from ctypes import wintypes

from PIL import Image

u32 = ctypes.WinDLL("user32", use_last_error=True)
gdi = ctypes.WinDLL("gdi32", use_last_error=True)

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.path.join(REPO, "src", "TaskbarExtras.App", "bin", "Release", "net9.0-windows", "TaskbarExtras.exe")
OUT = os.path.join(REPO, "docs")

PW_RENDERFULLCONTENT = 0x00000002
GWL_EXSTYLE = -20
WS_EX_APPWINDOW = 0x00040000

u32.SetProcessDPIAware()


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG),
        ("biHeight", wintypes.LONG), ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG),
        ("biYPelsPerMeter", wintypes.LONG), ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wintypes.DWORD * 3)]


class RECT(ctypes.Structure):
    _fields_ = [("left", wintypes.LONG), ("top", wintypes.LONG),
                ("right", wintypes.LONG), ("bottom", wintypes.LONG)]


def find_windows(kind):
    """Visible, on-screen TaskbarExtras windows of the requested kind."""
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, _):
        cls = ctypes.create_unicode_buffer(512)
        u32.GetClassNameW(hwnd, cls, 512)
        if not cls.value.startswith("HwndWrapper[TaskbarExtras"):
            return True
        if not u32.IsWindowVisible(hwnd):
            return True

        is_panel = bool(u32.GetWindowLongW(hwnd, GWL_EXSTYLE) & WS_EX_APPWINDOW)
        if is_panel != (kind == "settings"):
            return True

        r = RECT()
        u32.GetWindowRect(hwnd, ctypes.byref(r))
        # Warm-up windows park themselves far off-screen.
        if r.left > -10000 and (r.right - r.left) > 40:
            found.append((hwnd, r))
        return True

    u32.EnumWindows(cb, 0)
    return found


def grab(hwnd, rect):
    w = rect.right - rect.left
    h = rect.bottom - rect.top
    src = u32.GetWindowDC(hwnd)
    mem = gdi.CreateCompatibleDC(src)
    bmp = gdi.CreateCompatibleBitmap(src, w, h)
    old = gdi.SelectObject(mem, bmp)
    if not u32.PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT):
        u32.PrintWindow(hwnd, mem, 0)
    gdi.SelectObject(mem, old)

    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = w
    bi.bmiHeader.biHeight = -h          # negative => top-down rows
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    bi.bmiHeader.biCompression = 0      # BI_RGB
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)

    img = Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1).convert("RGB")
    gdi.DeleteObject(bmp)
    gdi.DeleteDC(mem)
    u32.ReleaseDC(hwnd, src)
    return img


def crop_to_content(img):
    """The menu window is AllowsTransparency=True, so PrintWindow composites the
    transparent margin against whatever is behind it.  Trim to the opaque card."""
    w, h = img.size
    px = img.load()

    def blank_column(x):
        return all(px[x, y] == px[x, 0] for y in range(0, h, 7))

    def blank_row(y):
        return all(px[x, y] == px[0, y] for x in range(0, w, 7))

    left = 0
    while left < w and blank_column(left):
        left += 1
    right = w - 1
    while right > left and blank_column(right):
        right -= 1
    top = 0
    while top < h and blank_row(top):
        top += 1
    bottom = h - 1
    while bottom > top and blank_row(bottom):
        bottom -= 1

    # One pixel of breathing room so the rounded corner is not clipped.
    return img.crop((max(left - 1, 0), max(top - 1, 0),
                     min(right + 2, w), min(bottom + 2, h)))


def kill_running():
    subprocess.run(["taskkill", "/IM", "TaskbarExtras.exe", "/F"], capture_output=True)
    time.sleep(0.5)


def launch(extra):
    kill_running()
    return subprocess.Popen([EXE] + extra, creationflags=subprocess.CREATE_NO_WINDOW)


def wait_for(kind, tries=80):
    for _ in range(tries):
        time.sleep(0.25)
        wins = find_windows(kind)
        if wins:
            return wins[0]
    return None


def shoot_menu(skin, lang):
    args = ["--preview", "--skin", skin] + (["--lang", "en"] if lang == "en" else [])

    # The preview menu hides — and the process quits — the moment anything steals
    # focus, so the window can disappear between the poll that found it and the
    # capture.  Retry the whole launch rather than losing the shot.
    for attempt in range(4):
        proc = launch(args)
        if wait_for("menu") is None:
            print(f"  ! menu window never appeared ({skin}/{lang}), attempt {attempt + 1}")
            proc.kill()
            time.sleep(0.8)
            continue

        time.sleep(0.5)                 # let XAML finish its first render
        wins = find_windows("menu")
        if not wins:
            proc.kill()
            time.sleep(0.8)
            continue

        hwnd, rect = wins[0]
        img = crop_to_content(grab(hwnd, rect))
        name = f"screenshot-{skin}-{lang}.png"
        img.save(os.path.join(OUT, name))
        print(f"  -> {name}  {img.size}  from ({rect.left},{rect.top})")
        proc.kill()
        time.sleep(0.5)
        return True

    print(f"  ! menu capture failed ({skin}/{lang})")
    return False


def shoot_settings(skin, lang):
    # --settings with nothing running falls through to a normal start-up and
    # opens the panel once the hook and the tray are up.
    args = ["--skin", skin, "--settings"] + (["--lang", "en"] if lang == "en" else [])
    proc = launch(args)

    if wait_for("settings") is None:
        print(f"  ! settings window never appeared ({skin}/{lang})")
        proc.kill()
        return False

    time.sleep(0.8)                     # the panel sizes itself to content
    wins = find_windows("settings")
    if not wins:
        proc.kill()
        print(f"  ! settings window vanished ({skin}/{lang})")
        return False

    hwnd, rect = wins[0]
    img = grab(hwnd, rect)              # normal window: no transparent margin to trim
    name = f"screenshot-settings-{skin}-{lang}.png"
    img.save(os.path.join(OUT, name))
    print(f"  -> {name}  {img.size}  from ({rect.left},{rect.top})")
    proc.kill()
    time.sleep(0.5)
    return True


def main():
    if not os.path.exists(EXE):
        print(f"not built: {EXE}")
        return 1

    # The stored skin is deliberately not touched: every run below passes --skin
    # explicitly, which never writes the settings file, and nothing here clicks a
    # radio in the panel. Regenerating the images must not change the machine.
    failures = 0
    for skin in ("win11", "win10"):
        for lang in ("zh", "en"):
            print(f"--- menu {skin} / {lang} ---")
            failures += 0 if shoot_menu(skin, lang) else 1
            print(f"--- settings {skin} / {lang} ---")
            failures += 0 if shoot_settings(skin, lang) else 1
    kill_running()

    print(f"done, {failures} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
