# TaskbarExtras

[English](README.md) · **简体中文**

**Windows 11 把任务栏右键菜单砍得只剩两项。这个项目把它补回来 —— 而且不往 `explorer.exe` 里注入一个字节。**

Windows 10 上右键点任务栏，你会得到：*工具栏 · 层叠窗口 · 堆叠显示窗口 · 并排显示窗口 · 显示桌面 · 任务管理器 · 锁定任务栏 · 任务栏设置*。

Windows 11 上你只得到 *任务管理器* 和 *任务栏设置*。

![补回来的任务栏右键菜单，运行于 Windows 11 26H2](docs/screenshot-menu-zh.png)

右键点任务栏的**空白背景**，出现的就是这个 —— 大约 **40 毫秒**，而且系统自带的菜单根本不会出现。

---

## 为什么不用现成的 StartAllBack / ExplorerPatcher？

两个都已经存在，而且都能用。区别在于**实现方式**，而这个区别有可测量的代价。

### 它们靠注入。这个不注入。

| | 方式 | 加载点 |
|---|---|---|
| **ExplorerPatcher** | DLL 搜索顺序劫持 | 把主 DLL 改名成 `dxgi.dll`，放进 `C:\Windows`、`StartMenuExperienceHost_*\`、`ShellExperienceHost_*\`，让 Windows 抢在真正的 `System32` 副本之前加载它 |
| **StartAllBack** | 未公开 | 在所有用户可读的位置都查不到。`AppInit_DLLs`、`AppCertDlls`、`C:\Windows` 代理 DLL 劫持、`ShellServiceObjects`、`ShellExecuteHooks`、`Winlogon\Notify`、`HKLM\SOFTWARE\Classes\CLSID`（**7836 个键全扫**）、`HKCU\SOFTWARE\Classes`、`Shell Extensions\Approved`、`shellex\ContextMenuHandlers`、Run 键与同名服务 —— **全部干净**。唯一剩下的候选是计划任务，而枚举它需要管理员权限 |
| **TaskbarExtras** | **什么都不注入** | 没有 DLL、不钩进别的进程、不需要提权。它只是创建自己的窗口，并在**自己的进程内**装一个鼠标钩子 |

### 注入的代价是真实的，而且我量过

ExplorerPatcher 会**为每个 Windows build 分发一套独立的任务栏实现** —— `ep_taskbar.0.dll` 对应 Win10 原生任务栏，`.1` 对应 21H2，`.2` 对应 22H2，`.3` 对应 Dev，`.4`/`.5` 对应 Canary。一旦微软删掉这些模块依赖的代码，它们就废了。

这不是假设。就在这个项目里，StartAllBack **3.9.16**（落后 11 个版本，2025 年 10 月发布）在机器升级到 Windows 11 **26H2（build 26300.9457）**的那一刻就**静默失效了** —— 而那个 build 在它发布时还不存在。它的配置键 `HKCU\SOFTWARE\StartIsBack` 停在 `Disabled = 1`，DLL 却还驻留在 `explorer.exe` 里。

**注入式工具继承的是一份永久的维护负担**：每次 Windows 功能更新都可能打坏它，而且故障形态是「shell 坏了」而不是「功能降级」。

这个项目押的是相反的一边。它调用的一切都是文档化的：

- `WH_MOUSE_LL` —— 鼠标钩子 API（`SetWindowsHookEx`），跑在我们自己的进程里
- `IShellDispatch` —— shell 自己的窗口排布动词
- `SHAppBarMessage` —— appbar 协议（已实现，目前可选开启）
- `EnumWindows`、`GetMonitorInfo`、`DwmGetWindowAttribute` —— 普通窗口管理

没有任何一处依赖私有结构体布局或 RVA 特征码。

---

## 工作原理

### 1. 拦截右键 —— 以及前两次失败的尝试

这部分最有意思，因为**最显然的做法全都不work**。

**尝试一：`EVENT_SYSTEM_MENUPOPUPSTART`。** 「菜单即将弹出」听起来完全对。但它对 Windows 11 任务栏菜单**一次都不触发** —— 因为那个菜单不是经典 Win32 的 `#32768` 菜单，而是 XAML 弹窗。整个 `EVENT_SYSTEM_MENU*` 事件族对它都是瞎的。

这个否定结论我是靠**对照实验**才敢下的：

| 阶段 | 动作 | 结果 |
|---|---|---|
| **对照** | `Alt+Space` 打开窗口系统菜单（真正的经典菜单） | 出现 `#32768` 窗口，**且 `EVENT_SYSTEM_MENUSTART` 触发** → 钩子本身没坏 |
| **实验** | 右键任务栏空白处 | 菜单**确实弹出**，但 `EVENT_SYSTEM_MENU*` **零触发** |

缺了任何一半，这个结论都是废的：一个从不触发的钩子，和一次没点上的点击，从外面看一模一样。

**尝试二：监听 `EVENT_OBJECT_SHOW`。** 那个 XAML 弹窗其实是个真 HWND，类名固定（`Xaml_WindowedPopupClass`）。于是我等它出现、把它关掉、再弹出自己的菜单。**这个方案是能跑通的，而且我真的交付了。** 然后第一个真正使用它的人说了三件事：

> 系统自带菜单先闪一下 · 延迟感很大 · 点别处你自己的菜单不消失

三条都是这个设计的固有缺陷。**你无法在一样东西出现之前替换它。** 而且因为菜单窗口**故意不取焦点**，它永远收不到 `Deactivated`，所以「点别处关闭」根本没有地方挂。

**尝试三：鼠标钩子，也就是最终交付的方案。** `WH_MOUSE_LL` 让我们能在**系统看到这次点击之前**就把它吞掉，于是原生菜单根本不会出现：

- **不闪** —— 因为没有东西需要替换
- **约 40ms 而不是 300ms+** —— 因为整个往返链路消失了
- **点别处关闭复用同一个钩子** —— 菜单打开时，任何落在菜单矩形外的按下都关掉它，完全绕开了「没焦点」这个死结

代价是它是全局输入钩子，所以我把作用范围压到了最小。

### 2. 钩子只在任务栏的「空白背景」上生效

吞掉任务栏上所有的右键会破坏用户依赖的东西。钩子先判断落点是否在任务栏矩形内，然后**排除每一个可交互的子窗口**：

| 排除的类名 | 在那里右键意味着什么 |
|---|---|
| `Start` | Win+X 高级用户菜单 |
| `MSTaskSwWClass`、`MSTaskListWClass`、`ReBarWindow32` | 任务按钮上的跳转列表 |
| `TrayNotifyWnd`、`TrayShowDesktopButtonWnd` | 托盘图标各自的菜单、时钟 |

只有剩下的背景才弹我们的菜单。这一点弄错，造成的倒退会比要解决的问题严重得多。

钩子回调还必须极快：它跑在钩子内部，而系统会**卸载任何超过 `LowLevelHooksTimeout`（默认 300ms）的钩子** —— 一个阻塞的钩子会**冻结全系统的鼠标输入**。所以回调只做一次矩形判定，剩下的丢给 dispatcher，立刻返回。

### 3. 菜单项都是文档化的 shell 动词

MSDN 的 `IShellDispatch` 提供的动词，正好就是 Windows 10 菜单用的那些：

| Windows 10 菜单项 | 文档化 API |
|---|---|
| 层叠窗口 | `CascadeWindows` |
| 堆叠显示窗口 | `TileHorizontally` |
| 并排显示窗口 | `TileVertically` |
| 任务栏设置 | `TrayProperties` |

**「显示桌面」是例外 —— 这里还得纠正一个常见误解：`IShellDispatch` 里根本没有 `ToggleDesktop` 方法。** 它只提供 `MinimizeAll` 和 `UndoMinimizeALL`。所以 toggle 语义只能**推断**，也就是必须回答「现在桌面是不是正显示着」。

一个私有布尔量不够 —— 用户可能按了 `Win+D`。所以我们去问窗口管理器：**如果当前没有任何「可见且未最小化」的应用窗口，那桌面一定是显示着的。**（用 `EnumWindows`，过滤条件：可见 / 无 owner / 非工具窗口 / 未最小化 / 未被 DWM cloaked / 有标题。）

兜底还有一条半文档化的路 —— 向 `Shell_TrayWnd` 发 `WM_COMMAND`，`419` = `MIN_ALL`、`416` = `MIN_ALL_UNDO`。**已在 26H2 上实测有效**：6 个可见窗口 → 发 419 → 0 个 → 发 416 → 6 个全部还原。

### 4. 菜单是 WPF 窗口，不是 `HMENU`

Win32 原生菜单无法换皮肤。而可换皮肤正是这个项目的核心，所以菜单是一个无边框、透明、置顶的 WPF `Window`，它的全部外观都放在一个 `ResourceDictionary` 里。

把 `Skins/Win10Flat.xaml` 换成另一个字典，就是「换皮肤」的全部内容。**不涉及任何 C# 改动。**

---

## 实测数据

全部来自 **Windows 11 26H2，build 26300.9457**，2560×1600，**150% 缩放**（逻辑分辨率 1707×1067）。

### 拦截方案：尝试二 vs 尝试三

| 指标 | `EVENT_OBJECT_SHOW` | `WH_MOUSE_LL` |
|---|---|---|
| 系统菜单出现次数 | 每次必现（可见闪烁） | **0 次** |
| 右键 → 菜单可见 | ≥300 ms | **38 / 43 ms** |
| 点别处关闭 | ✗ | ✓ |

### AppBar 与真实任务栏共存

当时的未知是：在系统任务栏占据同一条边的前提下，再注册一个自己的 appbar 会怎样、会落在哪。结论是可行，而且是**垂直堆叠**：

| 时刻 | `rcWork` | 任务栏矩形 |
|---|---|---|
| 注册前 | `(0,0)-(2560,1528)` | `(0,1528)-(2560,1600)` 2560×72 |
| `ABM_SETPOS` 后 | `(0,0)-(2560,**1498**)` | `(0,1528)-(2560,1600)` **未变** |
| `ABM_REMOVE` 后 | `(0,0)-(2560,1528)` ✅ 还原 | — |

**改变设计的那个坑：保留区域永远是「整条边」，与你的窗口实际多宽无关。**

| 请求 | 窗口尺寸 | `rcWork` 代价 |
|---|---|---|
| 全宽，高 30px | 2560×30 | 全宽 × 30 |
| **只占右侧 500px**，高 30px | 500×30 | **仍然是全宽 × 30** |

所以**不存在「小小的 appbar」**。任何 appbar 都会让每个最大化窗口矮 30px。对一个本职工作只是弹个菜单的工具来说，这买卖不划算 —— 所以 appbar 条**已实现但默认关闭**。

---

## 状态

**v0.2 —— 能用，而且已经被人用过。** 托盘图标、可换皮肤的 Win10 风格菜单、七项动作全部可用、鼠标钩子拦截右键、中英双语界面。

| | |
|---|---|
| **v0.1** | 托盘 + 可换皮肤菜单 + 用 `EVENT_OBJECT_SHOW` 拦截 |
| **v0.2** | 改用鼠标钩子（不闪、约 40ms）、点别处关闭、中英双语 ✅ |
| **v0.3** | 皮肤引擎正式化；Win7 Aero 与 XP Luna 皮肤 |
| **v0.4** | 配置文件、可选 appbar 条、多显示器实测 |

**明确不做：** 替换整个 shell、接管通知区域、往任何进程里注入。

---

## 怎么跑起来

**推荐 —— 自包含单文件。** 目标机器上什么都不用装：

```bash
dotnet publish src/TaskbarExtras.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o publish/win-x64
```

产出一个 `TaskbarExtras.exe`，双击即可，托盘会出现图标。

**从源码构建（框架依赖）：**

```bash
git clone https://github.com/potato1778/TaskbarExtras
cd TaskbarExtras
dotnet build TaskbarExtras.slnx -c Release
```

需要 .NET SDK，目标框架 `net9.0-windows`。**不需要管理员权限** —— manifest 里声明的是 `asInvoker`。

### 语言

界面跟随系统 UI 语言（英文 / 中文）。也可以强制指定：

```
TaskbarExtras.exe --lang zh
TaskbarExtras.exe --lang en
```

### ⚠️ 报「You must install or update .NET to run this application」

**先别急着去装 .NET，先查一个环境变量：**

```
echo %DOTNET_ROOT%
```

如果它指向一个 **SDK** 目录，那么 apphost 会**只去那里**找 Windows Desktop 运行时，**不会回退**到 `C:\Program Files\dotnet`。而用 **scoop** 装 .NET SDK 正好会设置这个用户级变量 —— 而 SDK 压缩包里只带 SDK 自己那个版本的 `Microsoft.WindowsDesktop.App`。结果就是：机器上明明装着 9.0 运行时，一个 `net9.0-windows` 程序却拒绝启动。（这不是假设，这一节就是这么来的。）

两条出路：

- **用自包含发布**（见上）。完全不查运行时。
- **删掉用户级 `DOTNET_ROOT`** —— `setx DOTNET_ROOT ""`，然后重开一个终端。`dotnet` CLI 不需要它，它靠自己的可执行文件路径定位根目录。

另外本项目还设了 `<RollForward>Major</RollForward>` 作为双保险，让框架依赖版接受**更高主版本**的运行时，而不是死要求 9.0。

> **Smart App Control 必须关闭。** 不是因为本程序做了什么，而是它会拦截未签名的可执行文件。

---

## 测试矩阵

| 维度 | 取值 |
|---|---|
| 显示器 | 单屏、双屏 |
| 缩放 | 100%、150%、混合 |
| 任务栏位置 | 下、上、左、右 |
| 任务栏 | 常显、自动隐藏 |
| 冲突 | 有 / 无 StartAllBack、ExplorerPatcher |

每次 Windows 功能更新之后都应该重跑一遍这个矩阵 —— **这正是本项目存在的全部理由**。

---

## 许可

MIT，见 [LICENSE](LICENSE)。

设计笔记、全部实测数据、以及每一个决策背后的推理 —— **包括我判断错的那一个** —— 都在 [`docs/DESIGN.md`](docs/DESIGN.md)。
