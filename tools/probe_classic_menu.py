"""Decide whether Windows 11's shell is still honouring the HKCU shadow key.

The visual check (right-click the desktop, look at the menu) is the ground truth, but it
needs a human at the keyboard. This probe answers the mechanical half of the question
without one: when the shadow key exists, does anything at all change about what the shell
does with that CLSID?

Approach: ask COM directly. CoCreateInstance on the handler CLSID should succeed when HKLM
wins (the real DLL loads) and fail with REGDB_E_CLASSNOTREG when our HKCU shadow hides it.
That is the exact resolution step the shell performs, done through the public API rather
than by guessing. If disabling changes the outcome, the shadow works and the visual result
follows; if COM resolves identically either way, the shadow is being ignored and the
feature cannot work on this build.
"""

import ctypes
import ctypes.wintypes as w
import winreg


def reg_state():
    path = (
        r"Software\Classes\CLSID"
        r"\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32"
    )
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as k:
            value, _ = winreg.QueryValueEx(k, "")
        return f"HKCU shadow PRESENT (default={value!r})"
    except FileNotFoundError:
        return "HKCU shadow ABSENT"
    except OSError as ex:
        return f"HKCU read error: {ex}"


def resolve_clsid():
    """CoCreateInstance the handler CLSID and report the HRESULT."""
    ole32 = ctypes.WinDLL("ole32")
    ole32.CoInitializeEx(None, 0x2)  # APARTMENTTHREADED, ignore RPC_E_CHANGED_MODE

    # CLSID {86ca1aa0-34aa-4e8b-a509-50c905bae2a2}
    class GUID(ctypes.Structure):
        _fields_ = [
            ("Data1", ctypes.c_ulong),
            ("Data2", ctypes.c_ushort),
            ("Data3", ctypes.c_ushort),
            ("Data4", ctypes.c_ubyte * 8),
        ]

    clsid = GUID(
        0x86CA1AA0,
        0x34AA,
        0x4E8B,
        (ctypes.c_ubyte * 8)(0xA5, 0x09, 0x50, 0xC9, 0x05, 0xBA, 0xE2, 0xA2),
    )
    iid_unknown = GUID(
        0x00000000,
        0x0000,
        0x0000,
        (ctypes.c_ubyte * 8)(0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46),
    )

    pv = ctypes.c_void_p()
    hr = ole32.CoCreateInstance(
        ctypes.byref(clsid), None, 0x1 | 0x4, ctypes.byref(iid_unknown), ctypes.byref(pv)
    )
    return hr & 0xFFFFFFFF


def describe(hr):
    if hr == 0:
        return "S_OK — handler resolved (a server loaded)"
    if hr == 0x80040154:
        return "REGDB_E_CLASSNOTREG — no server found (class hidden)"
    return f"hr=0x{hr:08X}"


def main():
    print("registry :", reg_state())
    print("CoCreate :", describe(resolve_clsid()))


if __name__ == "__main__":
    main()
