"""Report when explorer.exe started, to tell whether it re-read shell registrations."""

import ctypes
import ctypes.wintypes as w
import subprocess
import csv
import io
from datetime import datetime, timedelta

k32 = ctypes.WinDLL("kernel32", use_last_error=True)

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
k32.OpenProcess.restype = w.HANDLE
k32.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]


def start_time(pid):
    h = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return None
    creation = w.FILETIME()
    exit_t = w.FILETIME()
    kernel = w.FILETIME()
    user = w.FILETIME()
    ok = k32.GetProcessTimes(
        h,
        ctypes.byref(creation),
        ctypes.byref(exit_t),
        ctypes.byref(kernel),
        ctypes.byref(user),
    )
    k32.CloseHandle(h)
    if not ok:
        return None
    ft = (creation.dwHighDateTime << 32) | creation.dwLowDateTime
    return datetime(1601, 1, 1) + timedelta(microseconds=ft // 10)


def main():
    out = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq explorer.exe", "/FO", "CSV", "/NH"],
        capture_output=True,
        text=True,
    ).stdout
    for row in csv.reader(io.StringIO(out)):
        if row and row[0].lower().startswith("explorer"):
            pid = int(row[1])
            print(f"explorer pid {pid} started {start_time(pid)}")


if __name__ == "__main__":
    main()
