# TaskbarExtras

[English](README.en.md) · **简体中文**

把 Windows 11 删掉的任务栏右键菜单补回来。

![win11 皮肤](docs/screenshot-win11-zh.png)

## 起因

那天我左手端着快乐水，右手刚离开键盘，想回桌面开个东西。

按 `Win+D` 得腾出手，我没有。于是很自然地右键任务栏，准备点「显示桌面」。

结果这个选项没了。

微软你在干什么啊 😡

行，我自己加回来。 ε=( o｀ω′)ノ

## 能干嘛

右键任务栏的空白处：

| | |
|---|---|
| 显示桌面 | 最小化所有窗口，再点一次还原 |
| 任务管理器 | |
| 层叠窗口 | |
| 堆叠显示窗口 | |
| 并排显示窗口 | |
| 任务栏设置 | |
| 开机自启 | 勾选项，点一下切换 |
| 退出 TaskbarExtras | |

只有任务栏**空白处**会被拦截。右键开始按钮还是 Win+X，右键任务图标还是跳转列表，右键托盘图标还是各自的菜单 —— 这些都没动。

## 跑起来

去 [Releases](../../releases) 下载，或者自己编：

```bash
dotnet publish src/TaskbarExtras.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o publish
```

出来一个 exe，双击就行。不用管理员权限，也不用预先装 .NET。

```
TaskbarExtras.exe                     启动
TaskbarExtras.exe --quit              让正在运行的实例退出
TaskbarExtras.exe --autostart         看是否已设为开机自启
TaskbarExtras.exe --autostart on|off  开 / 关开机自启
TaskbarExtras.exe --lang zh|en        强制界面语言（默认跟随系统）
TaskbarExtras.exe --skin win11|win10  菜单外观（默认 win11）
TaskbarExtras.exe --preview           只显示一次菜单，用来预览皮肤
TaskbarExtras.exe --help              显示说明
```

## 怎么退出

没有主窗口，所以没东西可以关。三条路：

1. 右键任务栏 → 菜单最后一项「退出 TaskbarExtras」
2. 右键托盘图标 → 退出（图标在 `^` 折叠区里，深色方块加三道横杠）
3. `TaskbarExtras.exe --quit`

## 开机自启

右键任务栏，菜单里有一项「开机自启」，点一下就切换。勾上就是下次登录时自动起来。

命令行也行：

```bash
TaskbarExtras.exe --autostart on
TaskbarExtras.exe --autostart off
```

写的是 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，用户级，不需要管理员权限。

有个坑值得说：任务管理器的「启动」页可以禁用启动项，而**它不会删掉 Run 里的值**，只是在另一个地方记一笔。所以只看 Run 键会以为一切正常，实际根本不会启动。这个程序两处都看 —— 你在任务管理器里禁用了，菜单里的勾就会消失，再点一下能重新生效。

顺带：如果你把 exe 挪到了别的目录，旧的注册项就指向一个不存在的地方。这种情况菜单里也显示为未勾选，点一下会重新登记当前路径。

## 皮肤

菜单外观全写在 `ResourceDictionary` 里。加一套皮肤就是加一个 xaml，不用改 C#。

| win11（默认） | win10 |
|---|---|
| ![win11](docs/screenshot-win11-zh.png) | ![win10](docs/screenshot-win10-zh.png) |

```bash
TaskbarExtras.exe --preview --skin win10
```

两套是按各自年代的实物做的，不是同一套换个颜色：

- **win11**：近白卡片、8px 圆角、柔和阴影、中性灰悬停。
- **win10**：`#F2F2F2` 扁平底、**直角**、更深的灰边框，标签里带快捷键字母 —— `任务管理器(K)` 这种。**那些字母真的能按**，不是画上去好看的。

## 两条原则

**不注入任何进程。** 不往 `explorer.exe` 里塞 DLL，不打补丁，不改系统文件。它就是个普通进程，关了什么都不留。

**只用文档化 API。** 拦截右键用 `WH_MOUSE_LL`（跑在自己进程里），窗口排布用 `IShellDispatch`，任务栏位置用 `SHAppBarMessage` 和 `GetMonitorInfo`。没有私有结构体偏移，也没有特征码扫描。

原因很实际：靠注入实现的工具，每次 Windows 更新都得跟着改，没跟上就整个坏掉。实测数据和推演过程在 [docs/DESIGN.md](docs/DESIGN.md)。

## 下一步

- [ ] 菜单加动画（淡入、滑出）
- [ ] 做更多样式
- [ ] Windows 10 风格的开始菜单
- [ ] 恢复磁贴

### 不做

- **恢复 Windows 10 风格的系统托盘区（小图标）** —— Windows 11 的托盘是 XAML 画的，
  没有文档化接口能改它的图标尺寸，也读不到图标图像。要做只能注入 explorer 或自己重画整条任务栏，
  两条路都超出这个项目的定位。实测数据在 [docs/DESIGN.md](docs/DESIGN.md) §8.5。

## 已知问题

- 只在单屏 150% 缩放上测过。双屏和混合 DPI 没验证。
- 右键到菜单出来有约 40ms 延迟 —— 鼠标钩子拦下右键再渲染，这个开销消不掉。
- 没有配置文件，皮肤和语言只能靠命令行参数。
- 没做和其他任务栏工具的兼容，一起装可能打架。
- 区分「右键的是任务栏空白处还是应用按钮」要靠 UI Automation（Windows 11 的任务按钮是 XAML 画的，位置不在窗口树里）。它每秒刷新一次快照。万一 UIA 读不到，会退回旧的窗口树判定，那种情况下在 Windows 11 上可能误判。
- 字母快捷键要求菜单能拿到键盘焦点。绝大多数情况没问题；万一拿不到（日志里会写一行警告），字母不响应，但鼠标点击照常。

## 许可

MIT。
