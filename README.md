# TaskbarExtras

**Windows 11 cut the taskbar right-click menu down to two entries. This brings it back — without injecting a single byte into `explorer.exe`.**

On Windows 10, right-clicking the taskbar gave you: *Toolbars · Cascade windows · Show windows stacked · Show windows side by side · Show desktop · Task Manager · Lock the taskbar · Taskbar settings.*

On Windows 11 you get *Task Manager* and *Taskbar settings*. That's it. This project restores the rest.

```
┌──────────────────────────┐
│  显示桌面                 │
│  任务管理器               │
│  ─────────────────────   │
│  层叠窗口                 │
│  堆叠显示窗口             │
│  并排显示窗口             │
│  ─────────────────────   │
│  任务栏设置               │
└──────────────────────────┘
```

![The restored taskbar context menu, running on Windows 11 26H2](docs/screenshot-menu.png)

---

## Why not just use StartAllBack or ExplorerPatcher?

Fair question — both already exist and both work. The difference is *how* they work, and that difference has a measurable cost.

### They inject. This doesn't.

| | Approach | Load vector |
|---|---|---|
| **ExplorerPatcher** | DLL search-order hijacking | Renames its main DLL to `dxgi.dll` and drops it in `C:\Windows`, `StartMenuExperienceHost_*\`, and `ShellExperienceHost_*\` so Windows loads it before the real `System32` copy |
| **StartAllBack** | Undisclosed | Not registered in any user-readable location — `AppInit_DLLs`, `AppCertDlls`, `C:\Windows` proxy-DLL hijacking, `ShellServiceObjects`, `ShellExecuteHooks`, `Winlogon\Notify`, `HKLM\SOFTWARE\Classes\CLSID` (all 7836 keys scanned), `HKCU\SOFTWARE\Classes`, `Shell Extensions\Approved`, `shellex\ContextMenuHandlers`, Run keys and same-named services are **all clean**. The only remaining candidate is a scheduled task, which needs admin rights to enumerate |
| **TaskbarExtras** | **Nothing is injected** | No DLL, no hook, no elevation. It creates its own window and listens to documented `SetWinEventHook` notifications |

### The cost of injecting is real, and I measured it

ExplorerPatcher ships a **separate taskbar implementation per Windows build** — `ep_taskbar.0.dll` for the Windows 10 taskbar, `.1` for 21H2, `.2` for 22H2, `.3` for Dev, `.4`/`.5` for Canary. When Microsoft removes the code those modules depend on, they break.

That is not a hypothetical. In this very project, StartAllBack **3.9.16** — eleven versions behind, released October 2025 — silently stopped working the moment the machine moved to Windows 11 **26H2 (build 26300.9457)**, a build that did not exist when it shipped. Its config key `HKCU\SOFTWARE\StartIsBack` was sitting at `Disabled = 1` while its DLL was still resident in `explorer.exe`. The taskbar menu it was supposed to enhance was gone.

A tool that injects inherits a permanent maintenance burden: every Windows feature update is a potential break, and the failure mode is a broken shell rather than a degraded feature.

This project takes the opposite bet. Everything it calls is documented:

- `SHAppBarMessage` — appbar protocol (`shellapi.h`)
- `SetWinEventHook` — cross-process window notifications
- `IShellDispatch` — the shell's own window-arrangement verbs
- `EnumWindows`, `GetMonitorInfo`, `DwmGetWindowAttribute` — plain window management

Nothing depends on a private struct layout or an RVA signature. **When Windows updates, this keeps working.**

---

## How it works

### 1. Detecting the right-click on the taskbar

The obvious candidate is `EVENT_SYSTEM_MENUPOPUPSTART` — "a menu is about to pop up". It does not work, and finding out why is the most interesting part of this project.

**It fires zero times for the Windows 11 taskbar menu.** The taskbar menu is not a classic Win32 `#32768` menu; it is a XAML popup. The entire `EVENT_SYSTEM_MENU*` family only covers classic menus.

I only trusted that negative result after a control experiment:

| Phase | Action | Result |
|---|---|---|
| **Control** | `Alt+Space` → window system menu (a real classic menu) | `#32768` window appears at `(0,59)-(446,610)` **and `EVENT_SYSTEM_MENUSTART` fires** → the hook itself works |
| **Test** | Simulated right-click on empty taskbar at `(1364,1564)` | Menu **does** open (verified by screenshot; foreground window becomes `Shell_TrayWnd`) — but **zero** `EVENT_SYSTEM_MENU*` / `MENUPOPUP*` events |

Without both halves that conclusion would have been worthless: a hook that never fires and a click that never landed look identical from the outside.

**But the experiment also turned up the fix.** The XAML popup *is* a real HWND, with a stable class name:

```
EVENT_OBJECT_SHOW  cls='Xaml_WindowedPopupClass'  text='主机弹出窗口'  rect=(0,0,0,0)
```

So `TaskbarMenuWatcher` hooks `EVENT_OBJECT_SHOW` instead, and applies three filters — all three are needed:

1. class name == `Xaml_WindowedPopupClass`
2. owning process == `explorer.exe`
3. **the cursor is currently over the taskbar**

Filter 3 is the decisive one. That class name is shared by every WinUI/XAML popup on the system, and explorer itself uses XAML surfaces for Start, Search and the notification centre. "The cursor is on the taskbar" eliminates all of them for free.

Three implementation details, each learned the hard way:

- **Do not filter on window position.** The popup reports `rect = (0,0,0,0)` at `EVENT_OBJECT_SHOW` time — XAML popups are shown *then* positioned. Wait ~60 ms, or filter on the owning process instead.
- **Do not filter on window title.** `主机弹出窗口` is a localised string.
- **Do not skip the process filter.** `Xaml_WindowedPopupClass` is not ours alone.

### 2. The actions are documented shell verbs

MSDN's `IShellDispatch` exposes exactly the verbs the Windows 10 menu used:

| Windows 10 menu entry | Documented API |
|---|---|
| 层叠窗口 | `CascadeWindows` |
| 堆叠显示窗口 | `TileHorizontally` |
| 并排显示窗口 | `TileVertically` |
| 任务栏设置 | `TrayProperties` |

**"Show desktop" is the exception — and there is a correction to make here.** `IShellDispatch` has **no `ToggleDesktop` method**. It only offers `MinimizeAll` and `UndoMinimizeALL`. So the toggle has to be *inferred*, which means answering "is the desktop showing right now?".

A private boolean is not good enough — the user may have pressed `Win+D`. Instead we ask the window manager: if no application window is currently visible and un-minimised, the desktop must be showing. (`EnumWindows`, filtered for visible / ownerless / non-tool / non-minimised / non-DWM-cloaked / titled.)

As a fallback there is a semi-documented path — `PostMessage(Shell_TrayWnd, WM_COMMAND, 419)` for `MIN_ALL` and `416` for `MIN_ALL_UNDO`. **Verified working on 26H2**: 6 visible windows → 419 → 0 → 416 → all 6 restored.

### 3. The menu is a WPF window, not an `HMENU`

A Win32 menu cannot be skinned. Since swappable skins (Win10 today, Win7 Aero and XP Luna later) are the point of the project, the menu is a borderless transparent topmost `Window` whose entire appearance lives in a `ResourceDictionary`.

Swapping `Skins/Win10Flat.xaml` for another dictionary is the whole of "changing skins". No C# involved.

---

## What I measured

All figures from **Windows 11 26H2, build 26300.9457**, 2560×1600 at **150% scaling** (logical 1707×1067).

### AppBar vs. the real taskbar

The unknown was whether registering our own appbar on the same edge as the taskbar would work, and where it would land. It works, and it stacks:

| Moment | `rcWork` | Taskbar rect |
|---|---|---|
| Before | `(0,0)-(2560,1528)` | `(0,1528)-(2560,1600)` 2560×72 |
| After `ABM_SETPOS` | `(0,0)-(2560,**1498**)` | `(0,1528)-(2560,1600)` **unchanged** |
| After `ABM_REMOVE` | `(0,0)-(2560,1528)` ✅ restored | — |

`ABM_QUERYPOS` returned the requested rect unchanged, and the shell reserved exactly our height.

**The gotcha that changed the design:** the reserved band is always the **full edge**, no matter how narrow your window is.

| Request | Window size | `rcWork` cost |
|---|---|---|
| Full width, 30 px tall | 2560×30 | full width × 30 |
| **Right 500 px only**, 30 px tall | 500×30 | **still full width × 30** |

So there is no such thing as a small appbar. Any appbar costs every maximised window 30 px of height. For a tool whose actual job is a *context menu*, that is a bad trade — which is why the appbar bar is an opt-in extra rather than the default UI.

---

## Status

**v0.1 — working skeleton.** Tray icon, skinnable Win10-style menu, all seven actions functional, taskbar right-click interception implemented.

Roadmap:

| | |
|---|---|
| **v0.1** | Tray + skinnable menu + taskbar interception ✅ |
| **v0.2** | Optional appbar bar (opt-in, default off — the mechanism is already implemented and measured) |
| **v0.3** | Skin engine formalised; Win7 Aero and XP Luna skins |
| **v0.4** | Config file, per-monitor support, multi-monitor testing |

**Not planned:** replacing the shell, taking over the notification area, or injecting into anything.

---

## Build

```bash
git clone https://github.com/potato1778/TaskbarExtras
cd TaskbarExtras
dotnet build TaskbarExtras.slnx -c Release
```

Requires the .NET SDK. The app targets `net9.0-windows` and needs no administrator rights.

> **Smart App Control must be off.** Not because of anything this app does, but because unsigned binaries are blocked by it. The app declares `asInvoker` and requests no elevation.

---

## Testing matrix

| Axis | Values |
|---|---|
| Displays | single, dual |
| Scaling | 100%, 150%, mixed |
| Taskbar edge | bottom, top, left, right |
| Taskbar | always visible, auto-hide |
| Conflicts | with / without StartAllBack, ExplorerPatcher |

Every Windows feature update should re-run this matrix — that is the entire thesis of the project.

---

## Licence

MIT. See [LICENSE](LICENSE).

Design notes, measurements and the reasoning behind every decision above live in [`docs/DESIGN.md`](docs/DESIGN.md).
