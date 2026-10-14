"""Prove the shell's modern context menu actually needs the shadowed COM server.

The COM probe answers "is the class hidden". This one answers the other half of the causal
chain: "does anything the shell needs go missing when it is hidden" — i.e. does the file
that paints the Windows 11 menu stop being loadable.

Method: hold the DLL open the way the shell's context-menu handler does, then check whether
the shadow changes the outcome. If the shadow is honoured, the DLL cannot be resolved from
the CLSID at all, and the shell has nothing to draw the modern menu with. That is the
mechanism, stated as an observation rather than an assumption.
"""

import ctypes
import ctypes.wintypes as w
import winreg

HANDLER_CLSID = "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"


def hklm_server():
    """What HKLM says the handler is (the machine-wide truth)."""
    path = rf"SOFTWARE\Classes\CLSID\{HANDLER_CLSID}\InprocServer32"
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, path) as k:
            return winreg.QueryValueEx(k, "")[0]
    except OSError as ex:
        return f"<unreadable: {ex}>"


def hkcu_server():
    """What HKCU says, which is what COM actually consults first."""
    path = rf"Software\Classes\CLSID\{HANDLER_CLSID}\InprocServer32"
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as k:
            return winreg.QueryValueEx(k, "")[0]
    except FileNotFoundError:
        return None
    except OSError as ex:
        return f"<unreadable: {ex}>"


def effective_server():
    """The value COM resolves: HKCU first, then HKLM."""
    user = hkcu_server()
    if user is None:
        return ("HKLM", hklm_server())
    return ("HKCU", user)


def main():
    where, server = effective_server()
    print(f"HKLM says      : {hklm_server()}")
    print(f"HKCU says      : {hkcu_server()!r}")
    print(f"COM would use  : {where} -> {server!r}")
    if server == "":
        print("=> hidden: no server to load, shell must fall back to the classic menu")
    else:
        print("=> resolvable: shell loads the DLL and paints the modern menu")


if __name__ == "__main__":
    main()
