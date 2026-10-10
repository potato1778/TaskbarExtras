# TaskbarExtras 技术设计文档

| 项 | 值 |
|---|---|
| 版本 | v0.1（草案） |
| 日期 | 2026-10-05 |
| 状态 | **待评审** —— 未写代码 |
| 暂名 | `TaskbarExtras`（备选：`ShelfBar` / `RetroShelf`，可改） |
| 目标平台 | Windows 11 26H2（build 26300.9457），兼容 Win10 21H2+ |

---

## 0. 一句话

**不注入任何进程**，用一个 AppBar 窗口给 Windows 11 补回被微软砍掉的 shell 功能（显示桌面、完整任务栏菜单），并把外观做成可换皮肤——从 Win10 扁平风一路做到 XP Luna。

---

## 1. 问题与目标

### 1.1 要解决的问题

Windows 11 相对 Windows 10 砍掉了：

| 被砍掉的东西 | 现状 |
|---|---|
| 任务栏右键菜单的「显示桌面」 | **已消失**，且 26H2 上无法恢复（详见 §5.1） |
| 任务栏右键菜单的窗口排布（层叠/堆叠/并排） | 消失 |
| 任务栏右键菜单的「工具栏」 | 消失 |
| 任务栏右键菜单的「锁定任务栏」 | 消失 |
| 任务栏可拖到屏幕左/右/上 | 消失 |
| 任务栏图标不合并 + 显示标签 | 消失 |
| 经典开始菜单 | 消失 |

### 1.2 设计目标（按优先级）

1. **抗 Windows 更新** —— 只用文档化 API，Windows 升级不废
2. **可换皮肤** —— 皮肤是数据（XAML），不是代码，社区能贡献
3. **不做全局 hook** —— 不用 `WH_MOUSE_LL`、不注入 explorer（见 §6.1）
4. 零依赖分发（自包含 .NET）

### 1.3 非目标（明确不做）

- 替换整个 shell / 接管桌面图标与壁纸
- 替换系统托盘的通知区域（这是最难的部分，成本远超收益）
- 支持 Windows 8.1 及更早

---

## 2. 路线选择：自研 vs 贡献 RetroBar

在动手之前必须先回答这个问题。

| | 自研 TaskbarExtras | 给 RetroBar 提 PR |
|---|---|---|
| 已有基础 | 零 | AppBar、托盘、缩略图、多屏、45 语言、XAML 皮肤系统**全有** |
| 缺口 | 全部 | 只缺 7 / 8 / 10 三套皮肤 + 经典开始菜单 |
| 工期 | 阶段 1 几天，阶段 2 几周 | 一套皮肤几天 |
| 作品集信号 | 「我写了整个东西」 | 「我的 PR 被知名开源项目合并」 |
| 许可 | 自己的 | Apache-2.0（可 fork，需保留 NOTICE、标注修改文件） |
| 风险 | 可能烂尾 | 依赖维护者 review 节奏 |

**结论：两者不冲突，建议按顺序做。**

- 先给 RetroBar 写 **Win7 Aero 皮肤**（几天，纯 XAML，零 C#）→ 用最小成本验证「你到底喜不喜欢做这件事」，同时拿到一个真实的开源贡献
- 再决定要不要自研。若自研，**RetroBar 的皮肤格式就是现成的参考实现**，不用重新设计

本文档后续章节描述**自研**路线的设计。

---

## 3. 总体架构

### 3.1 分层

```
┌─────────────────────────────────────────────┐
│ TaskbarExtras.App        (WPF 宿主、托盘、设置) │
├─────────────────────────────────────────────┤
│ TaskbarExtras.Skins      (皮肤引擎 + 内置皮肤)  │  ← 纯数据驱动
├─────────────────────────────────────────────┤
│ TaskbarExtras.Actions    (动作注册表 + 实现)    │  ← 显示桌面/任务管理器/…
├─────────────────────────────────────────────┤
│ TaskbarExtras.Shell      (AppBar、窗口枚举、COM) │  ← 全部 P/Invoke 集中在这
└─────────────────────────────────────────────┘
```

**关键约束：所有 P/Invoke 与 COM 互操作只能出现在 `TaskbarExtras.Shell` 里。** 这样将来某天某个 API 变了，改动范围被限制在一个项目内。

### 3.2 进程与线程模型

- 单进程，单 UI 线程
- `SHAppBarMessage` **必须在拥有该窗口的 UI 线程上调用**（MSDN 未明写，但跨线程调用行为未定义）
- 系统发来的 AppBar 通知（`uCallbackMessage`）也到达 UI 线程 → 在 `HwndSourceHook` 里处理，不要阻塞

---

## 4. 核心机制一：AppBar

### 4.1 协议（已对 MSDN 核实）

```c
UINT_PTR SHAppBarMessage(DWORD dwMessage, PAPPBARDATA pData);

typedef struct _AppBarData {
  DWORD  cbSize;              // 必须填 sizeof(APPBARDATA)
  HWND   hWnd;                // 你的窗口
  UINT   uCallbackMessage;    // 自定义消息号，系统用它给你发通知
  UINT   uEdge;               // ABE_LEFT=0, ABE_TOP=1, ABE_RIGHT=2, ABE_BOTTOM=3
  RECT   rc;                  // 位置（进出参）
  LPARAM lParam;
} APPBARDATA;
```

消息常量（**全部已核实**）：

| 常量 | 值 | 用途 |
|---|---|---|
| `ABM_NEW` | `0x00` | 注册 AppBar，指定回调消息号 |
| `ABM_REMOVE` | `0x01` | 注销 |
| `ABM_QUERYPOS` | `0x02` | 请求系统校验一个候选位置 |
| `ABM_SETPOS` | `0x03` | 提交最终位置（系统据此保留屏幕空间） |
| `ABM_GETSTATE` | `0x04` | 取任务栏的 autohide / always-on-top 状态 |
| `ABM_GETTASKBARPOS` | `0x05` | 取**系统任务栏**的矩形与所在边 |
| `ABM_ACTIVATE` | `0x06` | 激活/取消激活 |
| `ABM_GETAUTOHIDEBAR` | `0x07` | 取某边的 autohide AppBar |
| `ABM_SETAUTOHIDEBAR` | `0x08` | 设置某边的 autohide AppBar |
| `ABM_WINDOWPOSCHANGED` | `0x09` | 通知系统你的位置变了 |
| `ABM_SETSTATE` | `0x0A` | 设置 autohide / always-on-top |
| `ABM_GETAUTOHIDEBAREX` | `0x0B` | 同上，但指定显示器 |
| `ABM_SETAUTOHIDEBAREX` | `0x0C` | 同上，但指定显示器 |

回调通知（系统 → 你）：

| 通知 | 你要做的事 |
|---|---|
| `ABN_POSCHANGED` (1) | **重新走一遍 QUERYPOS → SETPOS**，否则位置会被别的 AppBar 挤歪 |
| `ABN_FULLSCREENAPP` (2) | 有全屏应用时隐藏自己（仅 autohide 需要） |
| `ABN_STATECHANGE` (0) | autohide / always-on-top 状态变了 |
| `ABN_WINDOWARRANGE` (3) | 窗口排布菜单被展开 |

### 4.2 注册与定位时序

```csharp
// 1. 注册
var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(),
                           hWnd = hwnd, uCallbackMessage = WM_APPBAR_CALLBACK };
SHAppBarMessage(ABM_NEW, ref abd);

// 2. 查询位置（系统可能缩小你请求的矩形）
abd.uEdge = ABE_BOTTOM;
abd.rc = desiredRect;
SHAppBarMessage(ABM_QUERYPOS, ref abd);

// 3. 关键：按你的高度修正（系统可能把高度改成 0 或挤掉）
abd.rc.top = abd.rc.bottom - myHeight;   // 底部边时

// 4. 提交
SHAppBarMessage(ABM_SETPOS, ref abd);

// 5. 真正移动窗口（必须用 SetWindowPos，不能用 WPF 的 Left/Top）
SetWindowPos(hwnd, IntPtr.Zero, abd.rc.left, abd.rc.top,
             abd.rc.right - abd.rc.left, abd.rc.bottom - abd.rc.top,
             SWP_NOZORDER | SWP_NOACTIVATE);
```

**易错点**：

- 第 3 步不能省。系统在 `QUERYPOS` 里可能把你请求的矩形改得面目全非，直接拿它的结果 `SETPOS` 会得到一个 0 高度的窗口。
- 窗口样式用 `WS_POPUP` + `WS_EX_TOOLWINDOW`（不进 Alt+Tab）。**不要设 `WS_EX_TOPMOST`** —— AppBar 的层叠顺序由 shell 管，自己设会打架。
- 窗口必须是**顶层窗口**，不能有 owner，不能是子窗口。
- 退出前必须 `ABM_REMOVE`，否则会在系统里留下一个幽灵 AppBar 占着屏幕空间（重启 explorer 才能清掉）。

### 4.3 ⚠️ 与系统任务栏共存 —— 本项目最大的未知

**系统任务栏自己就是一个 AppBar，占满了屏幕底边。**

我们在同一条边（`ABE_BOTTOM`）再注册一个 AppBar，系统会怎么安排？两种可能：

- (A) 把我们的条挤到任务栏**上方**，形成两层
- (B) 与任务栏**并排分掉**底边宽度（我们的条被挤到某个角落，宽度被压缩）

MSDN 明确说明 `ABM_GETTASKBARPOS` **只返回系统任务栏**，并且：

> 屏幕上有第三方的 app bar 时，未被系统任务栏覆盖的区域**未必对用户可见**。要取「既不被任务栏也不被其他 app bar 占用」的可用区域，用 `GetMonitorInfo`。

也就是说：**可用工作区要用 `GetMonitorInfo().rcWork`（已扣掉所有 app bar），而不是 `rcMonitor`（全屏）。** 这是个容易踩的坑。

**结论：已由 Spike S1 实测回答，答案是 (A)。**

> **✅ Spike S1 结果（2026-10-05，本机 26H2 build 26300.9457 实测）**
>
> 用 Python + ctypes 造了一个真正的 AppBar 窗口（`WS_POPUP` + `WS_EX_TOOLWINDOW`），
> 在系统任务栏正常存在的前提下注册底边 AppBar，高 30px：
>
> | 时刻 | `rcWork` | 任务栏 rect |
> |---|---|---|
> | 注册前 | `(0,0)-(2560,1528)` | `(0,1528)-(2560,1600)` 2560×72 |
> | `ABM_SETPOS` 后 | `(0,0)-(2560,**1498**)` | `(0,1528)-(2560,1600)` **未变** |
> | `ABM_REMOVE` 后 | `(0,0)-(2560,1528)` ✅ 还原 | — |
>
> - `ABM_QUERYPOS` **原样返回**了我请求的矩形，没有缩水
> - **我的 AppBar 落在任务栏正上方**（`(0,1498)-(2560,1528)`），全宽，任务栏毫发无损
> - 系统**确实**为我的 AppBar 保留了 30px（`rcWork` 底部从 1528 变 1498）
> - `ABM_REMOVE` 后 `rcWork` **精确还原**
> - 收到 2 次 `ABN_STATECHANGE` 回调，无 `ABN_POSCHANGED`
>
> → **方案 (A)：同边 AppBar 是「沿该边垂直堆叠」，不是并排分宽度。协议完全可靠。**

#### ⚠️ S1 的附加发现：AppBar 没有「窄幅」这个选项

补测：把请求矩形改成**只占右侧 500px 宽**（`(2060,1498)-(2560,1528)`）。

| 项 | 结果 |
|---|---|
| `ABM_QUERYPOS` / `ABM_SETPOS` 返回 | `(2060,1498)-(2560,1528)` 500×30 —— 窗口确实只有 500px 宽 |
| **`rcWork`** | **`(0,0)-(2560,1498)`** ← 整条边 2560px 全部被扣掉 30px |

> **只要注册 AppBar，代价就是「整条边 × 高度」的全宽带子，与你的窗口实际多宽无关。**
>
> 这条约束直接决定了 UI 形态（见 §4.4）。

### 4.4 备选方案：悬浮覆盖窗口

如果 S1 的结果不理想（比如 AppBar 被挤到难看的位置，或者高度只有几像素），退回到这个方案：

- 普通 `WS_POPUP | WS_EX_TOOLWINDOW | WS_EX_TOPMOST` 窗口
- 用 `SetWindowPos` 把它贴在任务栏右端的**上方**，看起来像任务栏的一部分
- 监听 `WM_DISPLAYCHANGE` / `WM_SETTINGCHANGE` / 定时轮询 `ABM_GETTASKBARPOS`，任务栏位置变了就跟着挪

**代价（必须如实写进 README）**：

- 它是 topmost，会**浮在最大化的窗口之上**，遮住内容
- 不占用工作区 → 窗口最大化时会被它盖住

| | AppBar | 悬浮覆盖 |
|---|---|---|
| 不遮内容 | ✅ 系统保证 | ❌ 会遮 |
| 视觉融入任务栏 | 一般（是独立一条） | ✅ 好 |
| 依赖未知行为 | ✅ 有（S1） | ❌ 无 |
| 复杂度 | 中 | 低 |

**S1 之后的修订决策：**

S1 证明了 AppBar 协议可靠，但附加发现说明**任何 AppBar 都要付出「全宽一条带子」的代价**。
对一个「就想加两个按钮」的工具来说这个视觉代价过高（所有最大化窗口被顶上去 30px，
任务栏上方多出一条明显不属于系统的横条）。

于是 UI 形态重新排序：

| 形态 | 屏幕空间代价 | 视觉影响 | 结论 |
|---|---|---|---|
| **A. AppBar 全宽带** | 全宽 × 高度（S1 实测确认） | 像第二条任务栏 | 降级为 **v0.2 可选**（默认关闭），留到做完整任务栏替换时启用 |
| **B. 悬浮 topmost 条** | 0，但会盖住最大化窗口内容 | 能融入任务栏 | 备选 |
| **C. 不占屏幕空间：托盘图标 + 自绘右键菜单** | **0** | **完全不改外观** | ✅ **MVP 首选** |

> **关键洞察：用户真正要的是「被微软砍掉的那个右键菜单」，不是「多一条任务栏」。**

MVP 形态因此改为 **C**：托盘图标 + 自绘的 Win10 风格任务栏菜单
（显示桌面 / 任务管理器 / 层叠窗口 / 堆叠显示 / 并排显示 / 任务栏设置），
并叠加 §6.4 的 **B2′**，让菜单能在**右键任务栏时**自动弹出。

AppBar 条（形态 A）降级为 v0.2 可选项 —— **反正 S1 已经把它的技术风险清零了**，
随时可以启用。

---

## 5. 核心机制二：动作实现

每个按钮/菜单项 = 一个 `IAction`。设计成注册表，新增动作不改核心代码。

### 5.1 显示桌面 —— 有个坑

**先纠正一个常见误解：`IShellDispatch` 里没有 `ToggleDesktop` 方法。**

已核实的 `IShellDispatch` 方法列表里，与桌面相关的只有：

| 方法 | MSDN 原话 | 对应 Win10 菜单项 |
|---|---|---|
| `MinimizeAll` | 等同于点任务栏的 **Show Desktop** 图标 | 显示桌面 |
| `UndoMinimizeALL` | 等同于**再点一次** Show Desktop 图标 | （还原） |
| `CascadeWindows` | 等同于右键任务栏选 **Cascade windows** | 层叠窗口 |
| `TileHorizontally` | 等同于右键任务栏选 **Show windows stacked** | 堆叠显示窗口 |
| `TileVertically` | 等同于右键任务栏选 **Show windows side by side** | 并排显示窗口 |
| `TrayProperties` | 等同于右键任务栏选 **Properties** | 任务栏设置 |
| `ShutdownWindows` | 等同于点开始菜单的 Shut Down | 关机 |

所以「显示桌面」是个 **toggle** 语义，没有单一 API，有两条实现路径：

**路径 A（推荐，文档化）**：用 `MinimizeAll` / `UndoMinimizeALL`，自己维护状态

```
if (桌面当前可见)  shell.UndoMinimizeALL();
else               shell.MinimizeAll();
```

问题：状态怎么判断？不能只靠自己的布尔量（用户可能用 `Win+D` 或别的方式切了桌面）。**必须能查询当前桌面是否可见**——这是本设计的一个开放问题，见 §10 风险 R4。

**路径 B（半文档化）**：`PostMessage(FindWindow("Shell_TrayWnd"), WM_COMMAND, 419, 0)`

`419` = `MIN_ALL`，`416` = `MIN_ALL_UNDO`。这是流传很久的命令 ID，但**属于任务栏内部实现，MSDN 无记载**。

⚠️ 26H2 上任务栏已改为 XAML 合成（实测：`Shell_TrayWnd` 下是 `Windows.UI.Composition.DesktopWindowContentBridge` + `MSTaskSwWClass`），**这个命令 ID 是否还能被正确路由，必须实测。**

> **✅ Spike S2 结果（2026-10-05 实测）：419 / 416 在 26H2 上仍然生效。**
> 发送前 6 个可见未最小化窗口 → 发 `WM_COMMAND 419`（MIN_ALL）后 **0 个** →
> 发 `416`（MIN_ALL_UNDO）后 **6 个全部还原**。
>
> 但**实现仍优先走文档化的 COM 路径**（`MinimizeAll` / `UndoMinimizeALL`），
> 把 419/416 作为已验证的备用方案 —— 它依赖任务栏内部命令 ID，属半文档化。

**已排除的方案**：`EnumWindows` + 逐个 `ShowWindow(SW_MINIMIZE)`。原因：无法正确还原、对 UWP/打包应用行为不一致、会破坏窗口的 z-order 状态。

### 5.2 任务管理器

`Process.Start("taskmgr.exe")`。若已运行则激活已有窗口（用 `EnumWindows` 找 `Taskmgr` 类名）。

### 5.3 动作注册表

```csharp
public interface IAction
{
    string Id { get; }          // "show-desktop"
    string DisplayName { get; } // "显示桌面"
    string IconKey { get; }     // 皮肤决定用什么图标
    bool CanExecute();
    void Execute();
}
```

内置动作：`show-desktop` / `task-manager` / `cascade-windows` / `tile-h` / `tile-v` / `taskbar-settings` / `lock-workstation`（`user32!LockWorkStation`，文档化）/ `open-config`。

配置里只写 `id`，动作实现由代码注册 → 配置文件不依赖代码细节。

---

## 6. 核心机制三：菜单

### 6.1 触发方式 —— 一个诚实的取舍

Win11 的**任务栏右键菜单没有任何第三方扩展点**（它不像文件资源管理器有 shell extension）。所以想「让显示桌面回到那个菜单里」，只有两条路：

| 方案 | 做法 | 优点 | 代价 |
|---|---|---|---|
| **(a) 自己的入口** | 点我们 AppBar 上的按钮 → 弹出自绘菜单 | 零 hackery，完全安全 | 不是「右键任务栏」，肌肉记忆要改 |
| **(b) 全局鼠标钩子** | `WH_MOUSE_LL` 检测右键落点是否在 `Shell_TrayWnd` 矩形内 → 弹自绘菜单 | 肌肉记忆完全一致 | 全局钩子；**AV 会盯上**；与「不注入」卖点自相矛盾 |

**决策：默认做 (a)。(b) 做成默认关闭的可选项，README 里如实写明代价。**

理由：(b) 的 `WH_MOUSE_LL` 虽然不注入，但它是全局输入钩子——安全软件对此敏感，而且一旦被拦就是「点了没反应」这种最难排查的故障。项目最大的卖点是「不碰系统内部」，不该为了一个交互习惯把它赔掉。

### 6.2 自绘菜单

- WPF `Popup`（`AllowsTransparency=true`，`StaysOpen=false`）
- 需要在菜单外点击时关闭 + 正确的 `WS_EX_NOACTIVATE` 行为，避免抢焦点
- 支持子菜单（工具栏）、分隔符、勾选项（锁定任务栏）
- 皮肤驱动（§7）：边框、内边距、高亮色、图标尺寸全部来自皮肤

### 6.3 同类工具的注入/劫持机制调研（2026-10-05）

要判断 (b) 的代价，先看清别人是怎么做的。

#### ExplorerPatcher（开源，机制有文档）

| 环节 | 做法 |
|---|---|
| 注入方式 | **DLL 搜索顺序劫持**。把主 DLL 改名成 `dxgi.dll` 放进应用目录（Windows 先搜应用目录再搜 System32） |
| 注入点 | `C:\Windows\dxgi.dll` → explorer.exe；`StartMenuExperienceHost_*\dxgi.dll` → StartMenuExperienceHost.exe；`ShellExperienceHost_*\dxgi.dll` → ShellExperienceHost.exe |
| 任务栏实现 | **按 Windows build 分发的独立实现**：`ep_taskbar.0.dll`（Win10 原生任务栏）/ `.1`（21H2）/ `.2`（22H2）/ `.3`（Dev）/ `.4`（Canary nuked tray）/ `.5`（Canary 最新）。安装时 `PickTaskbarDll()` 只装当前 build 对应的那一个 |
| 安装流程 | `taskkill /f /im explorer.exe` → 解内嵌 AES-256 加密 ZIP → 部署 → `regsvr32` 注册 COM → 重启 explorer |
| 致命副作用 | explorer 启动时会加载 EP 的 dxgi.dll；**该文件存在但被拦截加载 → explorer 直接起不来** |

**结论：EP 的本质是「每个 Windows build 一套任务栏实现」。** 这正是它必须跟着 Windows 更新发版的原因，也是用户实测 StartAllBack 3.9.16 在 26H2 上失效的同类病根。

#### StartAllBack（闭源，本机取证）

在本机做了完整取证，**排除了以下全部加载方式**：

| 检测项 | 结果 |
|---|---|
| `AppInit_DLLs`（HKLM + HKCU 两个视图） | 空 / 未设置 |
| `AppCertDlls` | 未设置 |
| **`C:\Windows` 根目录代理 DLL 劫持** | 该目录下**只有 2 个非微软 DLL**：`pyshellext.amd64.dll`（Python Software Foundation）、`twain_32.dll`（Twain Working Group）。**无任何劫持** |
| `ShellServiceObjectDelayLoad` | 0 项 |
| `ShellServiceObjects`（18 个 CLSID 逐个解析 InprocServer32） | **全部指向微软系统 DLL**（shell32 / stobject / wpdshserviceobj …） |
| `HKLM\SOFTWARE\Classes\CLSID`（**7836 个全扫**） | 0 命中 |
| `HKCU\SOFTWARE\Classes`（含其下 29 个 CLSID） | 0 命中 |
| `Shell Extensions\Approved` / `ShellIconOverlayIdentifiers` / `Browser Helper Objects` / `shellex\ContextMenuHandlers` | 0 命中 |
| `ShellExecuteHooks` / `Winlogon\Notify` | 键不存在 |
| Run / RunOnce / 同名服务 | 无 |
| `C:\Windows\System32\Tasks` | **读不到（拒绝访问）** |

**唯一剩下的候选是计划任务。** 旁证：中文汉化版说明里明确提到「自动删除 **StartAllBack Update 计划任务**」，说明官方安装包确实会创建计划任务。因此最可能的机制是**登录时由计划任务拉起辅助进程，再向 explorer 注入**（`CreateRemoteThread` 一类），这条路径需要管理员权限才能完整枚举。

**取证结论：StartAllBack 刻意把加载点放在了不可读的位置。** 这件事本身就是「为什么这类工具总被安全软件盯上」的答案。

#### 其他开源项目

| 项目 | 机制 | 是否注入 |
|---|---|---|
| **Windhawk**（GPL-3.0） | 注入框架 + mod 用**符号/模式匹配**定位并 patch 目标函数 | ✅ 注入 |
| **RetroBar**（Apache-2.0） | **完全不注入**。自己实现任务栏，靠 AppBar + 隐藏原生任务栏 | ❌ |
| **ContextMenuForWindows11** | **MSIX 打包**，走微软给**文件右键菜单**提供的文档化扩展点（`IExplorerCommand`） | ❌ |

**这里有一条极关键的分界线：**

- **文件 / 文件夹右键菜单** → 微软提供了**文档化扩展点**（MSIX + `IExplorerCommand`）。所以 ContextMenuForWindows11 完全不用注入，打包成 MSIX 就能加菜单项。
- **任务栏右键菜单** → **没有任何第三方扩展点**。

> **不是大家不想用文档化的办法，是任务栏菜单这条路上微软根本没修路。** 这就是为什么所有「恢复任务栏菜单」的工具都在注入。

### 6.4 (b) 方案的技术选型

如果确定要做 (b)，「检测用户在任务栏上点了右键」有三条**非注入**路线：

| 方案 | 原理 | 优点 | 风险 |
|---|---|---|---|
| **B1. `WH_MOUSE_LL` 全局低级鼠标钩子** | 钩住所有鼠标事件，判断坐标是否落在 `Shell_TrayWnd` 矩形内 → **吞掉事件**并弹自己的菜单 | 文档化；能完全接管交互 | 全局输入钩子（AV 敏感）；回调**必须极快**（超时默认 300ms `LowLevelHooksTimeout`，卡住会**冻结全系统鼠标**）；需常驻消息循环；无法只针对单个窗口，只能靠坐标自己过滤 |
| ~~**B2. `SetWinEventHook(EVENT_SYSTEM_MENUPOPUPSTART)`**~~ | 系统弹出**经典**菜单时收到通知 | — | ❌ **S3 实测已排除**，见下 |
| **B3. UI Automation 事件订阅** | 订阅任务栏 structure-changed 事件，识别菜单元素 | 文档化 | 重；**无法吞掉原菜单**，只能事后替换（会闪一下）；权限受限时读不到（本机实测 UIA 拿不到 `Shell_TrayWnd`） |
| **B2′. `SetWinEventHook(EVENT_OBJECT_SHOW)` + 窗口类过滤** | 菜单窗口出现时收到通知 → 判断是不是任务栏的 → 关掉它、弹自己的 | **文档化、out-of-context、不吞输入** | 需按进程过滤（`Xaml_WindowedPopupClass` 也被其他 XAML 应用使用）；原菜单会**闪现一瞬** |

#### ✅ Spike S3 结果（2026-10-05，本机 26H2 build 26300.9457 实测）

**结论：B2 死了，但实验捞到了 B2′。**

实验设计：同一进程内挂 `EVENT_SYSTEM_MENUSTART/END`、`EVENT_SYSTEM_MENUPOPUPSTART/END`、
`EVENT_OBJECT_SHOW`，做「对照 + 实验」两阶段，并用截图确认动作真的生效。

| 阶段 | 动作 | 结果 |
|---|---|---|
| **A（对照）** | `Alt+Space` 打开窗口系统菜单（经典 Win32 菜单） | ✅ 出现 `#32768` 窗口（rect 0,59–446,610），且 **`EVENT_SYSTEM_MENUSTART` 触发** → **钩子本身工作正常** |
| **B（实验）** | 模拟右键点击任务栏空白处 (1364,1564) | ✅ 菜单**确实弹出**（截图 `s3_B_taskbar_menu.png` 可见「任务管理器 / 任务栏设置」，前台窗口变为 `Shell_TrayWnd`）<br>❌ **`EVENT_SYSTEM_MENU*` / `MENUPOPUP*` 一条都没触发** |

**原因**：Win11 任务栏菜单不是经典 `#32768` 菜单，而是一个 **XAML 弹出窗口**。
`EVENT_SYSTEM_MENU*` 这一族事件只覆盖经典 Win32 菜单。

**⭐ 但实验捞到了关键情报：这个菜单是一个真实存在的 HWND**

```
t=4.65s  EVENT_OBJECT_SHOW  cls='Xaml_WindowedPopupClass'  text='主机弹出窗口'  rect=(0,0,0,0)
```

**B2 的架构是对的，只是事件选错了。** 换成 `EVENT_OBJECT_SHOW` 并按窗口类过滤，
就能拿到和 B2 一样干净的方案 → 这就是 **B2′**。

#### B2′ 的实现要点（均由 S3 实测得出）

1. 挂 `SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, NULL, cb, 0, 0, WINEVENT_OUTOFCONTEXT)`
2. 回调里先判 `GetClassNameW(hwnd) == "Xaml_WindowedPopupClass"`
3. ⚠️ **不要用窗口位置过滤** —— 实测该窗口在 `EVENT_OBJECT_SHOW` 时刻 rect 是 `(0,0,0,0)`，
   XAML 弹窗是「先显示、后定位」。要么延后几十毫秒再取位置，要么改用**所属进程过滤**：
   `GetWindowThreadProcessId` 取 PID → 比对 `explorer.exe`
4. ⚠️ **不要用窗口标题过滤** —— 标题 `主机弹出窗口` 是**本地化字符串**，换语言即失效
5. ⚠️ `Xaml_WindowedPopupClass` 也是其他 WinUI/XAML 应用弹窗的类名 → **必须按进程过滤**
6. 确认是任务栏菜单后：关掉它（发 `Esc` 或 `WM_CLOSE`），再弹自己的菜单

**修订后的建议顺序：先做 B2′，不行再退 B1。** B1（全局鼠标钩子）仍是「确定能行但代价明确」的兜底。

> **下一步 Spike S3′**：写 B2′ 的最小验证 —— 挂 `EVENT_OBJECT_SHOW` + 类名/进程过滤，
> 确认能稳定捕获任务栏菜单，且不误伤其他 XAML 弹窗。

---

### 6.5 「任务按钮 vs 空白」的判定（2026-10-10，被用户实测打回来一次）

**症状**：右键任务栏上的应用图标，本该弹跳转列表，结果弹出了我们的菜单。

**为什么设计阶段没发现**：判定「这个点是不是任务栏空白处」用的是**枚举 `Shell_TrayWnd` 的子窗口、
看哪个窗口的 rect 包含这个点**。这台机器上开发时的自测点是从窗口矩形算出来的，永远落在
「判定为空白」的那一段里 —— 测试点自己挑对了，于是从没暴露。

**复现后的实测数据（关键）**：

```
任务栏子窗口树（本机 2560×1600 @150%）
  Shell_TrayWnd                     (0,1528)-(2560,1600)
    Start                           (0,1528)-(83,1600)     vis=False
    TrayNotifyWnd                   (1459,1528)-(2560,1600)
    ReBarWindow32                   (83,1528)-(875,1600)
      MSTaskSwWClass                (83,1528)-(875,1600)
        MSTaskListWClass            (83,1528)-(875,1600)   vis=False
```

按这个树，「空白区」被算成 `x=876..1458`。但用 UI Automation 读**真实**按钮位置：

```
WorkBuddy AI  (809,1528)-(875,1600)   ← MSTaskSwWClass 到此为止
VMware        (875,1528)-(941,1600)   ← ★ 落在「被判定为空白」的区域里
eduVPN        (941,1528)-(1007,1600)
WeChatAppEx   (1007,1528)-(1073,1600)
Typora        (1073,1528)-(1139,1600) ← 任务按钮实际到 1139
…（1139..1459 才是真正的空白）
```

**根因**：Windows 11 的任务栏是 XAML 画的，**真实按钮不是 HWND**，位置不出现在窗口树里。
`MSTaskSwWClass` 是遗留容器，它的矩形停在 875 不再更新。中间 264px 的按钮全部被当成空白。

**修复**：新增 `Shell/TaskbarButtonMap.cs`，用 UI Automation 枚举任务栏下的 `Button` 元素，
取它们的 `BoundingRectangle`，作为「这里不是空白」的依据。原有的窗口树判定保留为第二重保险
（覆盖 UIA 不报为 Button 的部分，也是 UIA 失效时的退路）。

**几个实现要点**：

| 要点 | 原因 |
|---|---|
| 用 `CacheRequest` 一次取回 `BoundingRectangle` | 不加缓存 35 ms，加了 17 ms（实测各 5 次） |
| **只取 `ControlType.Button`** | 取 `TrueCondition` 会把 `ControlType.Pane` 也拿进来，那些 Pane 的矩形覆盖整条任务栏 → 所有位置都会被判为「非空白」，功能直接失效 |
| **丢弃宽度 > 任务栏高度 × 3 的「按钮」** | UIA 会把某些容器报成 Button。实测有一个宽 668px 的 `Steam` 元素横跨半个托盘区 |
| **在后台 STA 线程每秒轮询，钩子只读快照** | 钩子超时是 300 ms，跨进程 UIA 调用不能放在关键路径上 |
| **快照为空时保留上一份** | 任务栏重建期间 UIA 可能返回空，零长度的映射会静默关掉整个判定 |
| **不发布空快照** | 同上 |

**实测开销**：0.188 s / 25 s = **0.75% 单核**（约 0.09% 总 CPU）。这个代价换掉一整类误判，值得。

**验证**（合成右键，读应用自己的日志）：

```
(300,1564)   空白处=False 阻挡=UIA button   → 放行（任务按钮）
(1000,1564)  空白处=False 阻挡=UIA button   → 放行（修复前会误判成空白）
(1300,1564)  空白处=True  阻挡=-            → 拦截（真正的空白）
```

**教训**：这次是「用窗口树代表视觉布局」这个假设错了，而它错了很久都没被发现，因为
**自测点的选取方式刚好绕开了它**。凡是「拿一个 A 去代表另一个 B」的映射，都要有一个
独立来源的对照（这里是 UIA 的 bounding rect），否则测出来的「通过」只证明自洽。

**新增依赖**：Shell 项目加了 `FrameworkReference Microsoft.WindowsDesktop.App`，
只为 `UIAutomationClient`。Shell 层因此多了一个非 P/Invoke 的框架引用 —— 权衡是：
手写 `IUIAutomation` COM 接口能避免它，但那是大量易碎的互操作代码。项目仍然不含任何 WPF 类型。

---

### 6.6 助记键：五个坑，其中三个只在中文机器上出现（2026-10-10）

Win10 皮肤的标签里写了快捷键字母（`任务管理器(K)`）。**画上去容易，让它真的能按，踩了五个坑。**
如果只画不绑，那就是装饰 —— 所以这一节值得记。

| # | 坑 | 现象 | 解法 |
|---|---|---|---|
| 1 | `Key.System` | Alt+字母的 `e.Key` 是 `Key.System` | 真实键在 `e.SystemKey` |
| 2 | **`Key.ImeProcessed`** | **中文输入法在跑时，按 X 得到 `Key.ImeProcessed`** | 真实键在 `e.ImeProcessedKey` |
| 3 | **IME 处理的键不触发 `KeyDown`** | 坑 2 的连带后果 | 必须用 `PreviewKeyDown` |
| 4 | `Focus()` 与 `WM_ACTIVATE` 竞态 | 窗口没有焦点元素 → 键盘事件没有路由源 | 延迟聚焦（见下） |
| 5 | 前台锁定 | 菜单出现但拿不到焦点 | `AttachThreadInput`（见下） |

**坑 2/3 是这样暴露的**：在 `KeyDown` 里加了日志，按 X 之后**一行日志都没有**。
挂 `HwndSource.AddHook` 打印原始窗口消息，才看到 `PreviewKeyDown ImeProcessed`。

> **教训**：`KeyDown` 里没有日志，**不等于**按键没送到窗口。必须能区分
> 「事件没触发」和「触发了但没匹配」—— 否则只能瞎猜。这和 §6.5 是同一类错误的第二次。

**坑 4 的细节**：`Activate()` 之后立刻 `Focus()`，如果窗口是**第二次**显示（`WarmUp()` 已经显示过一次）
且进程本来不在前台，`Focus()` 会赶在 WPF 处理 `WM_ACTIVATE` 之前执行而落空。
**`--preview` 模式下永远复现不了** —— 进程刚启动就是前台，这个竞态必胜。
修法是 `Dispatcher.BeginInvoke(..., DispatcherPriority.Input)` 再聚焦一次。

> ⚠️ **不要把它放进 `Activated` 事件里。** 试过：在 WPF 处理 `WM_ACTIVATE` 的过程中重入 `Focus()`
> 会把键盘输入**彻底**搞坏 —— 连原本能工作的 `--preview` 也一起坏掉，比原 bug 更糟。

**坑 5 的细节**：菜单是被我们自己的钩子**吞掉右键**之后弹出的，**没有任何窗口收到那次输入**，
所以不满足 `SetForegroundWindow` 的前置条件。`WindowFocus` 用 `AttachThreadInput` 临时附加到
当前前台线程的输入队列，绕过这个限制，用完立即分离（不分离会让两个线程共享输入队列）。

**验证**（正常模式、装钩子、合成右键 + 合成按键）：

```
[hook] 任务栏内右键 (1300,1564) 空白处=True 阻挡=-
拦截任务栏右键 (1300,1564)
退出                                    ← 按 X 命中「退出 TaskbarExtras(X)」
```

**IME 残留**：IME 比应用更早看到按键。消费掉一个助记字母后，输入法缓冲区里还留着那个字符，
下次打字会冒出来。匹配成功后调 `ImmNotifyIME(NI_COMPOSITIONSTR, CPS_CANCEL)` 丢掉 ——
只在匹配成功时调用，所以不会打断正在打字的人。

> **这一节的五个坑，有四个在 `--preview` 模式下全部测不出来。** 这是本项目第二次
> 「测试方式本身掩盖了 bug」——第一次是 §6.5 的 UIA。**功能要在真实入口上验证，不能只在
> 方便测试的那个入口上验证。**

---

## 7. 核心机制四：皮肤引擎

### 7.1 分层

皮肤**只描述外观，不描述行为**：

```
SkinDescriptor (元数据: 名称/作者/适用DPI)
    ├─ Metrics   : 高度、内边距、圆角、字号、图标尺寸
    ├─ Brushes   : 背景/悬停/按下/边框/文字
    ├─ Assets    : 位图素材（9-slice）
    └─ Templates : ControlTemplate（可选，给需要特殊结构的皮肤）
```

### 7.2 皮肤格式：XAML `ResourceDictionary`

**直接沿用 RetroBar 的做法**（已验证可行）：皮肤是 `ResourceDictionary`，放在 `Skins/` 目录，用户可自行添加。

优点：
- 非程序员也能改（这是作品集里很好的叙事：「社区可以贡献皮肤」）
- 热重载容易（改完 XAML 不用重编译）
- 有现成参考实现

皮肤清单（按实现顺序）：

| 皮肤 | 难度 | 备注 |
|---|---|---|
| Win10 扁平 | ★ | 先做这个，最简单，也是用户最想要的 |
| Win7 Aero | ★★★ | 需要玻璃效果，见 §7.3 |
| Win8 Metro | ★★ | 扁平但配色不同，成本低 |
| XP Luna | ★★★ | 圆角渐变 + 位图，需要 9-slice |

### 7.3 Aero 玻璃 —— 一个真实的坑

Win7 的 Aero 玻璃用 `DwmEnableBlurBehindWindow`。**这个 API 从 Windows 8 起就失效了**（系统仍返回成功，但什么都不做）。

现代可行的替代：

| 方案 | 状态 | 评价 |
|---|---|---|
| `DWMWA_SYSTEMBACKDROP_TYPE`（Mica / Acrylic / Tabbed） | **文档化**，Win11 22H2+ | ✅ 首选，但效果是 Mica 不是 Aero |
| `SetWindowCompositionAttribute` | **未文档化** | 能做出接近 Aero 的模糊，但要签名匹配 → 会被 Windows 更新搞坏 |
| 自己画静态渐变 + 半透明 | 文档化 | 最稳，但没真模糊，视觉上差一截 |

**决策：Win7 Aero 皮肤先用 `DWMWA_SYSTEMBACKDROP_TYPE` + 静态渐变模拟。** 不引入未文档化 API —— 这是本项目的核心原则，皮肤不能破例。

---

## 8. 多显示器与 DPI

### 8.1 DPI

- **必须声明 Per-Monitor V2**：`app.manifest` 里
  ```xml
  <dpiAwareness>PerMonitorV2</dpiAwareness>
  <dpiAware>true/pm</dpiAware>
  ```
- 处理 `WM_DPICHANGED`：用 `lParam` 给出的建议矩形（suggested rect）调整窗口，**不要自己按新 DPI 重算**
- 皮肤资源按 DPI 变体加载

> 用户当前环境：**2560×1600 @ 150% 缩放**（实测任务栏逻辑尺寸 1707×1067）。这是真实测试场，但**混合 DPI（150% + 100% 外接屏）是经典雷区，必须专门测。**

### 8.2 多显示器

- 用 `EnumDisplayMonitors` / `GetMonitorInfo`，**不要用 `System.Windows.Forms.Screen`**（WPF 项目里引 WinForms 只为这个不值得）
- 关键区分：
  - `rcMonitor` = 整块屏幕
  - `rcWork` = **扣掉任务栏和所有 app bar 之后的可用区** ← 定位时用这个
- 每屏一个实例还是一个实例跨屏？**建议每屏一个 AppBar**（`ABM_SETAUTOHIDEBAREX` 支持指定显示器）
- 监听 `WM_DISPLAYCHANGE` 重建
- ⚠️ 注意：副屏的任务栏是 `Shell_SecondaryTrayWnd`，`ABM_GETTASKBARPOS` **不返回它**（用户当前环境实测没有副屏任务栏）

---

## 9. 配置

`%APPDATA%\TaskbarExtras\config.json`（`System.Text.Json`，带 schema 版本号便于将来迁移）

```jsonc
{
  "schemaVersion": 1,
  "skin": "win10-flat",
  "edge": "bottom",
  "monitor": "primary",
  "autoHide": false,
  "items": [
    { "action": "show-desktop",  "label": "显示桌面" },
    { "action": "task-manager",  "label": "任务管理器" }
  ]
}
```

设计要点：`items` 是数组且只引用 `action` 的 id → 用户加按钮不用改代码；`schemaVersion` 让将来的破坏性变更可迁移。

### 9.1 开机自启（已实现）

入口三处：替换菜单里的勾选项、托盘菜单里的勾选项、`--autostart on|off`。

**存储位置**：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，
值名 `TaskbarExtras`，内容 `"<exe 全路径>" --startup`。

为什么不选别的：

| 方案 | 否决理由 |
|---|---|
| `HKLM\...\Run` | 需要管理员权限。一个装全局鼠标钩子的工具索要提权本身就是危险信号，而且没有任何功能需要它 |
| 启动文件夹的快捷方式 | 写 `.lnk` 要走 COM（`IShellLink` + `IPersistFile`）；注册表写一个字符串就够 |
| 计划任务 | 能延迟启动或提权，但同样要 COM 或 `schtasks`。复杂度换不来收益 —— 「登录就启动」本身就是需求 |

**坑一：任务管理器禁用启动项不会删掉 Run 值。** 它在
`HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run` 里另写一个
12 字节 `REG_BINARY`，首字节是状态。只读 Run 键会把「已被禁用」误报成「已启用」——
菜单上就是一个骗人的勾。实测：

| `StartupApproved\Run\TaskbarExtras[0]` | 程序判定 |
|---|---|
| 值不存在 | 已启用 |
| `0x02` | 已启用 |
| `0x06` | 已启用 |
| `0x03` | **未启用** |

对照组：删掉 Run 值、`StartupApproved` 保持 `0x02` → 判定未启用（证明两个条件都真的在看）。

> ⚠️ **边界**：这几个字节的语义是从公开资料加实测反推的，
> **没有真的打开任务管理器把开关拨一遍来对照**（那需要人工点图形界面）。
> 程序在四种输入下的行为都验证过，但「任务管理器实际写哪个值」这一环没闭环。
> 若将来发现它写别的值，改一个判断即可。

**坑二：exe 移动后注册项指向失效路径。** 换目录后旧注册项还在，指向一个可能已不存在的文件。
所以 `IsEnabled` 不只看「有没有值」，还比对路径是否等于当前 exe。不匹配就显示未勾选，
点一下按当前路径重新登记。（实测：Run 值指向 `C:\Somewhere\Else\...` → 正确报未启用。）

**坑三：命令行必须带引号。** `C:\Program Files\...` 不加引号会在第一个空格处被切断。
读取时也按引号优先解析。

**坑四：开启时必须清掉任务管理器的禁用记录。** 否则用户点了「开机自启」、勾也亮了，
Windows 那边却仍然记着「已禁用」—— 界面说生效了，功能没生效。
清掉它，「开」才能压过之前的「关」，无论那个「关」是在哪记的。

---

## 10. 风险清单

| # | 风险 | 影响 | 缓解 |
|---|---|---|---|
| **R1** | **AppBar 与任务栏共存行为未知**（§4.3） | 🔴 决定 UI 方案可行性 | **Spike S1 先行，半天**。不通过则退回悬浮方案 |
| **R2** | 任务栏命令 ID 419/416 在 26H2 可能失效（§5.1） | 🟡 显示桌面需换实现 | Spike S2；不行就靠 `MinimizeAll`/`UndoMinimizeALL` |
| **R3** | Aero 玻璃无文档化 API（§7.3） | 🟡 Win7 皮肤效果打折 | 用 Mica 模拟，不碰未文档化 API |
| **R4** | 「桌面当前是否可见」无法可靠查询（§5.1） | 🟡 toggle 语义可能反了 | 调研；退路：改用两个明确动作（最小化全部 / 还原全部）而非一个 toggle |
| **R5** | 混合 DPI 多屏 | 🟡 布局错乱 | 早期就用双屏测；manifest 必须 PerMonitorV2 |
| **R6** | 皮肤素材版权（微软 Luna / Aero 原始位图） | 🟠 法律 | **自己重画或用社区授权素材**，绝不打包含微软位图的包 |
| **R7** | 与 StartAllBack / ExplorerPatcher 冲突 | 🟡 行为诡异 | 启动时检测这两个的注册表键并提示用户 |
| **R8** | 无代码签名 → Smart App Control 拦截 | 🟢 低 | 用户环境已关（`VerifiedAndReputablePolicyState=0`）；README 说明 |
| **R9** | 环境缺 .NET SDK | 🟢 低 | 用 scoop 装（用户级免管理员） |
| **R10** | 当前只有单显示器 | 🟡 测不了多屏 | 用模拟/虚拟机，或借外接屏 |
| **R11** | (b) 方案用全局鼠标钩子 → AV 误报 / 钩子卡死冻结全系统鼠标（§6.4 B1） | 🟠 中 | 优先走 B2（`EVENT_SYSTEM_MENUPOPUPSTART`）；B1 仅作兜底且默认关闭 |
| **R12** | (b) 方案依赖 `Shell_TrayWnd` 矩形做坐标判定 | 🟡 中 | 任务栏自动隐藏/多屏/DPI 变化时要重新取矩形；用 `ABM_GETTASKBARPOS` 而非硬编码 |
| **R13** | `StartupApproved` 的状态字节语义未用任务管理器 GUI 实测（§9.1） | 🟢 低 | 四种输入下的判定都验证过；若发现新值，改一个判断即可 |

---

## 11. 里程碑

| 里程碑 | 内容 | 出口标准 |
|---|---|---|
| **M0** | Spike S1 + S2 | 明确 AppBar 共存行为与 419 命令可用性，**据此锁定 §4.4 选型** |
| **M1** | 可运行骨架（**已按 S1 结论修订形态**） | 托盘图标 + 自绘 Win10 风格菜单，7 项动作全部可用。**从托盘打开为必达**；**B2′ 任务栏右键触发为增强**（若 S3′ 不通过则降级，不影响交付） |
| **M2** | 配置化 | JSON 配置 + 设置界面，用户能自己加按钮 |
| **M3** | 自绘菜单 | Win10 风格完整菜单（含 5 个窗口排布命令） |
| **M4** | 皮肤引擎 | 抽象出皮肤接口 + Win10 皮肤 + Win7 Aero 皮肤 |
| **M5** | 发布 | 自包含单文件发布 + README + GitHub Actions 构建 + LICENSE |

**M1 是心理关口** —— 能跑起来就说明方向对了。

---

## 12. 仓库结构

```
TaskbarExtras/
├─ .github/workflows/build.yml
├─ docs/
│  ├─ DESIGN.md            ← 本文档
│  └─ spikes/              ← S1/S2 的结论记录
├─ src/
│  ├─ TaskbarExtras.App/       WPF 宿主、托盘、设置窗口
│  ├─ TaskbarExtras.Shell/     AppBar、窗口枚举、COM 互操作  ← 唯一的 P/Invoke 层
│  ├─ TaskbarExtras.Actions/   动作注册表与实现
│  └─ TaskbarExtras.Skins/     皮肤引擎 + 内置皮肤 XAML
├─ tests/TaskbarExtras.Tests/
├─ Directory.Build.props      统一 TFM / 版本号 / 可空引用
├─ LICENSE                    MIT
└─ README.md                  ← 架构取舍叙事（见下）
```

**README 是作品集的门面。** 必须写清楚：

> **为什么我不注入 explorer** —— 对比 StartAllBack / ExplorerPatcher 的注入式做法（每次 Windows 功能更新都可能失效，实测 3.9.16 在 26H2 上直接失效），说明本项目选择纯文档化 API 的代价与收益。

这一段比多写两个功能更能体现工程判断力。

---

## 13. 测试策略

| 层次 | 内容 |
|---|---|
| 单元测试 | 配置解析与迁移、动作注册表、皮肤元数据校验 |
| 手动矩阵 | 单屏/双屏 × 100%/150%/混合 DPI × 任务栏上/下/左/右 × 任务栏自动隐藏开/关 |
| 回归 | **每次 Windows 功能更新后跑一遍手动矩阵**（这是本项目存在意义的验证） |
| 冲突 | 分别与 StartAllBack / ExplorerPatcher 同时安装测试 |

---

## 14. API 速查

### 已核实为文档化

- `SHAppBarMessage` / `APPBARDATA` / `ABM_*` / `ABE_*` — shellapi.h
- `IShellDispatch`：`CascadeWindows` / `TileHorizontally` / `TileVertically` / `MinimizeAll` / `UndoMinimizeALL` / `TrayProperties` / `ShutdownWindows` / `FileRun`
- `GetMonitorInfo`（`rcWork` vs `rcMonitor`）
- `EnumWindows` / `SetWinEventHook` / `GetWindowLongPtr`
- `DwmRegisterThumbnail` / `DwmUpdateThumbnailProperties`（缩略图，M2 以后）
- `IShellItemImageFactory::GetImage`（高质量图标）
- `DWMWA_SYSTEMBACKDROP_TYPE`（Mica / Acrylic，Win11 22H2+）
- `LockWorkStation` / `Process.Start`

### 半文档化（需实测，尽量不用）

- `Shell_TrayWnd` 的 `WM_COMMAND` 419 / 416
- `SetWindowCompositionAttribute`
- `RegisterShellHookWindow`

### 明确不用

- `WH_MOUSE_LL`（除非用户显式开启 §6.1(b)）
- 任何 DLL 注入 / API hooking
- `DwmEnableBlurBehindWindow`（Win8 起失效）

---

## 8.5 系统托盘区（Win10 小图标）—— 可行性结论：不做

**用户需求（2026-10-10）**：「任务栏托盘的图标优化，Win11 的太大了，回到 Win10 样式」，
并澄清指的是**整个系统托盘区**，不是本程序自己的托盘图标。

**结论：在「不注入 + 只用文档化 API」的约束下做不到。** 下面是实测依据，不是推测。

#### 实测 1：托盘区已经是 XAML，没有可操作的 HWND

```
Shell_TrayWnd  (0,1528)-(2560,1600)
  ├─ Windows.UI.Composition.DesktopWindowContentBridge  ← 整条任务栏的 XAML 合成宿主
  │    └─ Windows.UI.Input.InputSite.WindowClass         ← 只有输入站点，没有按钮
  ├─ Start                          (0,1528)-(83,1600)
  ├─ TrayNotifyWnd                  (1459,1528)-(2560,1600)   ← ★ 一个 HWND，内部无子窗口
  ├─ ReBarWindow32                  (83,1528)-(875,1600)
  │    └─ MSTaskSwWClass → MSTaskListWClass（遗留容器，rect 不再更新）
  └─ TrayDummySearchControl         (0,1528)-(0,1528)  空
```

*`TrayNotifyWnd` 子树枚举结果：**零个子窗口**。* Win10 时代每个托盘图标是一个
`ToolbarWindow32` 子窗口（可以用 `TB_BUTTON` 消息读图标位置和图像），
**Win11 全部改成 XAML 元素，一个 HWND 都没有** —— 和 §6.5 任务按钮的情况完全一样。

#### 实测 2：UIA 只能拿到位置和名字，拿不到图像

UIA 树里托盘图标确实存在，类名 `SystemTray.NormalButton`（本机 14 个）：

```
- ControlType.Button name='NVIDIA 设置'        cls='SystemTray.NormalButton'  (1651,1528)-(1699,1600)
  └─ ControlType.Image                        cls='Image'                    (1663,1552)-(1687,1576)
- ControlType.Button name='任务栏输入指示 …'   cls='SystemTray.NormalButton'  (2179,1528)-(2245,1600)
- ControlType.Button name='网络 QwQ_5G …'     cls='SystemTray.AccentButton'  (2251,1528)-(2287,1600)
- ControlType.Button name='音量 …'             cls='SystemTray.OmniButtonCenter'
- ControlType.Button name='时钟 …'             cls='SystemTray.OmniButtonLeft'
- ControlType.Button name='显示桌面'           cls='SystemTray.ShowDesktopButton' (2542,1528)-(2560,1600)
```

★ 关键：**UIA 没有「Image 图案」模式**（`AutomationPattern` 里不存在取位图的成员）。
`Image` 元素只能给出 `BoundingRectangle`。实测各属性可用性：

| 属性 | 结果 |
|---|---|
| `HelpText` / `ItemStatus` / `AcceleratorKey` | `<不支持>` |
| `LocalizedControlType` | `按钮`（无信息量） |
| `Value` | 不支持 |
| `Invoke` | ✅ 支持（触发左键点击是可行的） |
| 位图本身 | **拿不到** |

→ 能把位置读准、能触发点击，**但复制不出图标**。

#### 三条路都堵死

| 方案 | 为什么不行 |
|---|---|
| **A. 用 `ITrayNotify` / `NotifyIconSettings` 读图标列表和图像** | 未文档化 COM 接口 + 私有结构体布局。**直接违背项目第 1 条原则**（见 §1.2）。且 Win11 上 explorer 已改用 XAML 渲染，这条老路能否取到图像本身就存疑 |
| **B. 在 `TrayNotifyWnd` 上盖一个自绘窗口** | 能遮住原托盘，但要复刻 20+ 个图标、时钟、输入法指示、网络/音量/电池、通知中心、显示桌面按钮的**全部交互**（左键右键中键滚轮、拖拽排序、悬停提示、badge、进度条叠加）。而且被遮住的原托盘仍然在跑 —— 位置一变就对不齐。**成本远超收益**，且必然出现「点了没反应 / 点错」这类最难排查的故障 |
| **C. 用 `SetWindowPos` 缩小任务栏高度/托盘区** | Win11 的任务栏高度由 XAML 布局决定，不是 HWND 尺寸。改 `Shell_TrayWnd` 的 rect 不会让 XAML 内容跟着缩，只会错位 |

#### 为什么 Win10 时代可以、Win11 不行

Win10 的托盘是**真正 Win32 工具栏控件**：`TrayNotifyWnd` 下有 `ToolbarWindow32`，
可以用 `TB_GETBUTTON` 枚举图标、`TB_GETIMAGELIST` 拿到 `HIMAGELIST`、
`ImageList_GetIcon` 取出位图，再用自己的工具栏按任意尺寸重画。**全部文档化。**

Win11 把这层整体换成了 XAML（`Windows.UI.Composition` 合成），
图标变成了 XAML 元素（`SystemTray.NormalButton` / `Image`）。
微软没有为「替换托盘渲染」提供任何文档化扩展点。

> 和 §6.3 的结论一致：**不是大家不想走文档化的路，是这条路微软根本没修。**
> 恢复托盘样式的工具（ExplorerPatcher、StartAllBack）全靠按 build 分发的
> `ep_taskbar.*.dll` / 注入 explorer —— 正是本项目明确拒绝的做法。

#### 那还能做什么（已实现的部分）

用户诉求里「图标太大」这半句，**本程序自己的托盘图标**已经解决了：
16/20/24/32/48 五个尺寸打包进一个 `.ico`（PNG 帧），Windows 按需取整帧，
小尺寸下不再被重采样糊掉。这是唯一能控制的部分。

#### 若将来一定要做

唯一现实中可行的形态是 **A′：自己实现整个通知区域**（RetroBar 路线），
即「隐藏原生任务栏 + 自己画一条完整的任务栏」。那已经超出本项目的定位
（§1.3 明确列为非目标），且需要按 Windows build 维护多套实现。
**结论：列为「不做」，理由和成本记录在此，不留悬念。**

---

## 8.6 快速设置面板（Win11 右下角那个面板）—— 同样不做

**用户需求（2026-10-10）**：贴了一张 Win11 快速设置面板的截图，问能不能回到 Win10 样式 ——
WLAN / 蓝牙 / 电量 / 音量**各自独立成一个图标**，而不是现在这样挤在一个面板里、图标还是拼图式的大按钮。

**结论：比 §8.5 更彻底 —— 连面板里的控件都读不到，更别说换。**

#### 实测 1：这个面板是个 XAML island，Win32 侧只有一个空壳

点右下角那个「网络/音量/电池」合并按钮，面板的窗口类是 **`ControlCenterWindow`**，
走 `GetForegroundWindow()` 能拿到句柄（本机 65850），但：

```
ControlCenterWindow '快速设置'  (1984,0)-(2560,1528)
  └─ Windows.UI.Input.InputSite.WindowClass   (1984,0)-(1984,0)   ← 零尺寸，纯输入站点
```

**Win32 子窗口树只有这一个零尺寸的输入站点。** 面板里那十来个按钮、两根滑块、
电量行 —— 全是 XAML 元素，一个 HWND 都没有。

⚠️ **它不在 `EnumWindows` 的列表里，`FindWindowW("ControlCenterWindow")` 返回 0。**
必须用 `GetForegroundWindow()` 抓（或者在它显示后按实时前台句柄取）。
本机实测：点击后 0ms 内前台就变成它 —— 开关是立即的。

#### 实测 2：UIA 连一个控件都看不到

```
AutomationElement.FromHandle(65850)
  name='快速设置'  ControlType=Pane  bounding=(1984,0)-(576,1528)

  FindAll(Children)    = 0 个元素
  FindAll(Subtree)     = 1 个元素   ← 只有它自己
  FindAll(Descendants) = 0 个元素
```

★ **整棵 UIA 子树里只有一个元素：`ControlCenterWindow` 本身。**
对比 §8.5 的托盘：托盘至少还能拿到 14 个 `SystemTray.NormalButton` 的位置和名字。
这个面板**连"有哪些控件"都问不出来**，遑论去改它的布局或样式。

原因：`ControlCenterWindow` 是 XAML content island，它的 UIA provider 没有把内部
可视化树暴露给跨进程客户端（和任务栏的 `TaskbarFrameAutomationPeer` 不同，
后者是主动暴露的）。

#### 三条路，比 §8.5 更死

| 方案 | 为什么不行 |
|---|---|
| **A. 重新排布面板里的控件** | UIA 看不到任何控件 → **没有任何地址可以去操作**。这不是"难"，是"没有入口" |
| **B. 盖一层自绘面板替换** | 得从零实现：6 个大开关（WLAN/蓝牙/飞行/辅助功能/节能/实时字幕）、亮度滑块、音量滑块 + 输出设备选择、电量读数、设置齿轮、右上角编辑按钮。**而且每个开关的实际动作要走未文档化路径**（连 WLAN 开关都不是文档化的 API），面板状态还得自己轮询同步 |
| **C. 关掉它、换成自己的** | 就算关得掉（`Esc` 或点空白处），也没有任何文档化事件能告诉我"用户点了那个合并按钮" —— 那个按钮在 XAML 里，`Shell_TrayWnd` 的窗口树里看不到，只能靠鼠标钩子按坐标猜。为了一个"面板换皮"再挂一个全局钩子，代价和收益完全不成比例 |

#### 那 Win10 是怎么做到「分开」的

Win10 的 WLAN / 音量 / 电量 / 操作中心是**各自独立的 flyout**，每个都注册在
`Shell_TrayWnd` 的 `TrayNotifyWnd` 下，图标是独立的 `ToolbarWindow32` 按钮。

Win11 把它们**合并成了一个 `ControlCenterWindow`**，这是产品设计层面的改动，
不是渲染层面的。**要在 Win11 上"分开"，等于自己重新实现这四个 flyout**，
还要接管各自的触发按钮 —— 回到 §8.5 的结论：那就是自己写整个 shell。

#### 现实可用的替代品（都在 Windows 自己身上）

用户想要的「各自独立」，Windows 本身就留了口子：

| 想要的效果 | 官方做法 |
|---|---|
| 音量 → 独立图标/独立面板 | 音量图标可以**单独显示在任务栏**（设置 → 个性化 → 任务栏 → 系统托盘图标）。点它弹的是**老的音量 flyout**，不是快速设置面板 |
| 电量 → 独立 | 同上，电量可以单独显示 |
| WLAN / 蓝牙 → 独立 | 同上，可以各自显示成独立图标，点开是各自的**老式 flyout** |
| 时钟 | `SystemTray.OmniButtonLeft`，本来就在面板外 |

**把图标设成「独立显示」后，点击弹出的就是 Win10 那种单功能小面板，不是这个拼图式快速设置。**
这是唯一不需要写代码、又能拿到「分开」效果的路径。

#### 补充实测（2026-10-10）：`NotifyIconSettings` 能读，但系统图标没有真图

追查「独立显示」这条路时顺手把 `HKCU\Control Panel\NotifyIconSettings\*` 全读了一遍
（本机 71 个条目）。这是 Win11 存托盘图标状态的地方，每个条目一个哈希键，值有：

| 值名 | 类型 | 含义 |
|---|---|---|
| `ExecutablePath` | REG_SZ | 图标所属程序。系统图标写 `{F38BF404-…}\explorer.exe` |
| `IconGuid` | REG_SZ | 系统图标的 GUID（`{7820AE7x-23E3-4229-82C1-E41CB67D5B9C}` 一族） |
| `UID` | REG_DWORD | 无 GUID 的老式图标用这个 |
| `IsPromoted` | REG_DWORD | **1 = 单独显示在任务栏上**（即"独立显示"） |
| `IconSnapshot` | REG_BINARY | 图标位图，**PNG 格式** |
| `Publisher` / `InitialTooltip` | REG_SZ | 显示用 |

★ **`IconSnapshot` 确实是 PNG，但只对第三方图标有真图。**
实测把全部快照解出来数「不同颜色数」：

| 图标 | 尺寸 | 不同颜色数 | 不透明像素 |
|---|---|---|---|
| NVIDIA（第三方） | 128×128 | **191** | 16384 |
| 某第三方 | 32×32 | **891** | 1020 |
| 蓝牙（系统） | 24×24 | 7 | 388 |
| 网络 `UID=1124`（系统） | 24×24 | **1** | 94 |
| `0x78`（系统） | 24×24 | **1** | 94 |

→ 系统图标的快照是**单色占位字形**（1 种颜色 / 94 个不透明像素，解出来是个通用圆角方框），
不是真实图标。原因：WLAN / 音量 / 电量这些在 Win11 里是 **Segoe Fluent 字体字形**，
由 XAML 在渲染时合成（还会随连接状态变色），**从不作为位图存盘**。
第三方应用是自己 `Shell_NotifyIcon` 提交 HICON，explorer 才落一份 PNG 快照。

**结论**：
- ✅ **`IsPromoted` 可读写** → 「让某个托盘图标独立显示」这条是通的（= 官方设置里的那个开关）。
  但**改完要重启 explorer 才生效**，不是实时。
- ❌ **拿不到 WLAN / 音量 / 电量的 Win10 风格位图** → 没有图就没法在别处重画它们。

所以「让它们分开显示」能做到，「把它们换成 Win10 样子」做不到 —— 后者卡在没有源图像。

> 记录这条是为了避免以后重复研究：**这个面板不是一个"还没找到 API"的难题，
> 而是一个"微软没有提供任何 API"的死路**。§8.5 和 §8.6 的结论一致，
> 但 §8.6 更彻底 —— 连读都读不到。而 `IconSnapshot` 这条线索虽然能读到 PNG，
> 也只覆盖第三方图标，救不了系统图标。

---

## 附录 A：本机环境现状（2026-10-05 实测）

| 项 | 值 |
|---|---|
| OS | Windows 11 **26H2**，build **26300.9457**，`ProductName` 仍写 "Windows 10 Home China"（Win11 已知怪癖，非篡改） |
| 屏幕 | 2560×1600 **@ 150% 缩放**（逻辑 1707×1067） |
| 显示器数 | 1（无 `Shell_SecondaryTrayWnd`） |
| 任务栏 | 底边，左对齐（`TaskbarAl=0`），高度 48px（逻辑） |
| 任务栏结构 | `Shell_TrayWnd` → `Windows.UI.Composition.DesktopWindowContentBridge`（XAML 合成）+ `TrayNotifyWnd` + `ReBarWindow32` + `MSTaskSwWClass` |
| 显示桌面按钮 | **不存在**（无 `TrayShowDesktopButtonWnd`；`TaskbarSd=1` 写入后重启 explorer 仍无效） |
| Aero Peek | 已关（`DisablePreviewDesktop=1`） |
| .NET | **无 SDK**；运行时 WindowsDesktop.App 6.0.36 / 8.0.31 / 9.0.20 / 10.0.12 |
| 工具链 | git 2.55.0 ✅ / scoop ✅ / winget 1.29.380 ✅ / VS Code ✅ / **无 gh CLI** |
| Smart App Control | **已关闭**（`VerifiedAndReputablePolicyState=0`） |
| 冲突软件 | StartAllBack 3.9.16 已安装但 `Disabled=1`（DLL 仍注入 explorer，**装本项目前须卸载**） |
| GitHub | `potato1778` |

---

## 附录 B：待决策项

1. **项目名**（`TaskbarExtras` / `ShelfBar` / `RetroShelf`）
2. 先做 RetroBar 的 Win7 皮肤（§2 建议的顺序），还是直接自研？
3. §6.1 的 (b) 全局钩子方案要不要做（默认关闭的可选项）
4. 许可证：MIT 还是 Apache-2.0（若要参考 RetroBar 的皮肤文件格式，注意其 Apache-2.0 的 NOTICE 要求）
