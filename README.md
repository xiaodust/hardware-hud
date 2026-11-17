# HardwareHud · 硬件实时监测悬浮窗

一个常驻桌面的极小硬件仪表盘：CPU / GPU / 内存的使用率与历史曲线，外加 GPU 温度、功耗、频率、风扇，以及磁盘与网络的实时吞吐。

纯 **.NET Framework 4.0+ + WPF**（本机实测 4.8），无任何第三方依赖、无需安装、单文件 exe（约 88 KB）。

可运行在 **Windows 7 SP1 / 8.1 / 10 / 11**：只用了 .NET Framework 4.0 就存在的 API，没有碰 `VisualTreeHelper.GetDpi`（4.6.2+）、`RuntimeInformation`（4.7.1+）、`AppContext`、`Task.Delay` 这类新 API；所有 P/Invoke 都按「缺 DLL 就退下一级」处理。

| 面板模式 | 极简模式 |
| --- | --- |
| ![面板模式](docs/panel.png) | ![极简模式](docs/minimal.png) |

托盘图标（任务栏 `^` 右侧）：

![托盘图标](docs/tray.png)

---

## 快速开始

桌面已有快捷方式 **`硬件监测`**，双击即可。它指向的是安装目录：

```
%LOCALAPPDATA%\Programs\HardwareHud\HardwareHud.exe
```

即 `C:\Users\<你>\AppData\Local\Programs\HardwareHud\`，不需要管理员权限，卸载时删掉这个目录和桌面快捷方式即可。

> **不要直接运行 `F:\agent study\hardware-hud\dist\HardwareHud.exe`。**
> 这台机器上，从工作区路径（`F:\agent study\...`）启动的进程会被拦掉三件事：通知区域图标注册失败（`tray=fail(size=976,err=0)`），`%APPDATA%` 不可写（配置只能退化存到 exe 旁边），连「桌面快捷方式 / 开机自启动 `.lnk`」也会失败（`UnauthorizedAccessException：无法保存快捷方式`，WScript.Shell 的 `Save` 被拒）。同一个 exe 放到 C 盘任何位置都正常（`tray=ok`，配置写 `%APPDATA%\HardwareHud\`，快捷方式正常）。`dist\` 只是构建产物，`build.bat` 编完会自动覆盖到安装目录。

也可以直接运行安装目录里的 exe：

```
"%LOCALAPPDATA%\Programs\HardwareHud\HardwareHud.exe"
```

窗口默认出现在屏幕右上角，**左键拖动可移动**，**右键打开菜单**，同时也出现在**任务栏**和**通知区域**。

菜单项：

| 菜单 | 说明 |
| --- | --- |
| 极简模式 | 只显示 CPU / GPU / 内存 三行文字，不画面板与曲线（勾上即为开启） |
| 始终置顶 | 是否压在其他窗口之上 |
| 不透明度 | 循环切换 100 / 90 / … / 70 % |
| 回到右上角 | 复位到默认位置 |
| 开机自启动 | 在「启动」文件夹写 `HardwareHud.lnk`（静默启动）；失败时退回注册表 `HKCU\...\Run` |
| 桌面快捷方式 | 在桌面创建或修复 `硬件监测.lnk` |
| 诊断信息 | 弹出一个框列出 exe 路径、构建时间、托盘 / 热键 / 系统菜单 / 任务栏的注册状态和日志路径，看不到托盘图标时用它取证 |
| 重新添加托盘图标 | 只在托盘图标注册失败时才出现，点一下会重试并告诉你具体原因 |
| 退出 | 保存位置后退出 |

位置、置顶、不透明度、极简模式、自启动状态保存在 `%APPDATA%\HardwareHud\settings.ini`，下次启动自动恢复。
如果该目录不可写（受限用户配置 / 受管企业环境 / 只读共享），会依次退到 `%LOCALAPPDATA%\HardwareHud`、再退到 exe 同目录下的 `HardwareHud\`；运行中保存失败也会当场换目录重试，不会因为一处写失败就丢掉全部设置。配置行按「第一个 `=`」切分，值里带 `=`（比如路径）也不会被整行丢掉。

开机自启动默认在「启动」文件夹放一个 `HardwareHud.lnk`（指向当前 exe，静默启动、不闪黑窗口），老版本留下的 `HardwareHud.vbs` 会被顺手删掉；快捷方式建不出来时退回注册表 `HKCU\...\CurrentVersion\Run`。三种形式任意一种存在，菜单里就显示为已开启，点一下能全部关掉并清理干净。

单实例运行（重复启动会直接退出）。替换成新版本 exe 后，记得先用菜单「退出」关掉正在运行的那个实例 —— 老进程不会自动获得新功能。

**找不到窗口时**：再双击一次桌面快捷方式就行 —— 第二个实例不会真的启动，而是把已经在跑的那个叫到最前面并直接弹出配置菜单。极简模式下窗口只有一小块文字，这是最省事的唤起方式。

每次启动和退出都会在配置目录（通常是 `%APPDATA%\HardwareHud\`）写一行 `HardwareHud.log`，记录 exe 路径、构建时间、托盘 / 热键 / 系统菜单 / 任务栏的注册结果、窗口位置、`pdh=`（磁盘/网络计数器状态）和 `gpu=`（NVML 认到的卡数，`none` 表示走 DXGI + PDH 兜底）以及 `render tier=`（WPF 渲染层级）。采样线程、UI 线程、后台任务的意外异常也都落在这里（`sampler:` / `dispatcher-ex` / `unhandled` / `fatal` / `settings: fallback to`），不会再有「窗口突然消失、日志里什么都没有」的情况。超过 64 KB 自动清空。

---

## 随时找到它：任务栏、托盘与热键

极简模式下窗口只有一小块文字，所以除了「右键窗口」，还有四条随时可用入口：

| 入口 | 操作 | 能得到什么 |
| --- | --- | --- |
| 任务栏按钮 | 点击 | 让窗口获得焦点（之后可 `Alt+Space` 或直接右键窗口） |
| 系统菜单 | 窗口有焦点时按 `Alt+Space` | 原生菜单，内容与右键菜单完全一致 |
| 通知区域图标 | 双击 / 右键 | 双击切极简；右键弹出完整菜单；鼠标停留显示当前 CPU / GPU / 内存数值 |
| 快捷键 `Ctrl+Alt+H` | 任意位置按下 | 立刻切换极简模式 |

程序会把 `WS_EX_APPWINDOW` 强制打开（WPF 的 `ShowInTaskbar` 对无边框透明窗口不够用），所以任务栏一定有它的按钮；系统菜单用 `WS_SYSMENU` + `SetWindowPos(SWP_FRAMECHANGED)` 补出来 —— 无边框窗口默认是没有系统菜单的。

**托盘图标在 Windows 11 上默认被收进 `^`「隐藏的图标」里**，这是系统的行为，不是程序没加上。想让它常驻任务栏：

- 点任务栏右下角的 `^`，找到「硬件监测」，在「任务栏设置 → 其他系统托盘图标」里把它改成「在任务栏上显示」；
- 或者 设置 → 个性化 → 任务栏 → 其他系统托盘图标 → 打开「硬件监测」开关。

程序自己也会做一步：图标第一次注册成功后，Windows 会立刻在 `HKCU\Control Panel\NotifyIconSettings` 下为它建一条记录，程序随即把这条记录标成 `IsPromoted=1`，于是图标直接出现在任务栏上（通常当场生效，最迟下次启动）。你如果手动改成了「隐藏」，程序就不会再覆盖你的选择。

Explorer 重启（崩溃、切换显示设置等）会清空托盘，程序收到系统的 `TaskbarCreated` 广播后会自动把图标加回去；登录早期托盘尚未就绪时，会每 2 秒重试，最多 20 次（约 40 秒）—— 开机自启时 Explorer 常常比程序晚十几秒才起来，8 次是不够的。

托盘图标即使注册失败（某些受限桌面会拒绝 `Shell_NotifyIcon`），任务栏、系统菜单和热键这三条路依然有效；此时菜单里会多出一行「重新添加托盘图标」，点它能看到具体的失败原因。

---

## 界面

```
┌────────────────────────────────────────┐
│ HARDWARE·HUD            i5-12600KF     │   ← 标题 + CPU 型号
│ ────────────────────────────────────── │
│ 处理器              ▃▅▂▇▃▅ 46 %        │   ← 大数字 + 112 点历史曲线
│ 10 核 16 线程                          │   ← 补充信息行
│                                        │
│ 显卡                ▁▂▃▂▅▃▂▁ 24 %        │   ← 标签后面就是显卡型号
│ 1.7 / 8.0 GB  37°C  功耗 N/A  465 MHz   │
│                                        │
│ 内存                ▇▇▇▇▇▇▇▇ 81 %        │
│ 12.8 / 15.8 GB                         │
│ ────────────────────────────────────── │
│ 磁盘 ↓ 2.1 MB/s       网络 ↓ 8.4 KB/s   │
│ 磁盘 ↑ 0.3 MB/s       网络 ↑ 1.2 KB/s   │
└────────────────────────────────────────┘
```

- 每行一条 112 点的滑动历史（500 ms 采样 → 约 56 秒窗口）。
- 三行配色：CPU 琥珀 `#E3A551`、GPU 青 `#52B4C4`、内存 灰蓝 `#95A9B0`。
- 支持多 GPU：检测到多块卡时，显卡行会自动按 `显卡 1 / 显卡 2` 展开。
- 显卡行的标签后面直接跟随型号（如 `显卡 NVIDIA GeForce RTX 4060  24 %`），空间不够时按可用宽度截断成省略号，不会压到右侧的大数字。
- 标题栏的 CPU 型号做过精简：`12th Gen Intel(R) Core(TM) i5-12600KF` → `i5-12600KF`，`AMD Ryzen 9 7950X3D 16-Core Processor` → `AMD Ryzen 9 7950X3D`，`Intel(R) Xeon(R) Silver 4214R CPU @ 2.40GHz` → `Intel Xeon Silver 4214R`；型号太长时自动省略，避免和右上角的运行时间重叠。

---

## 极简模式

右键菜单 → **极简模式**（或双击托盘图标、或按 `Ctrl+Alt+H`）。打开后整个面板（底色、边框、投影、网格、曲线、标题与页脚）全部消失，
右上角只剩三行文字，窗口从 400×397 缩到 **164×75**：

```
CPU            28%
GPU   24%  47°
RAM            83%
```

- 每行只有「指标名 + 数值」，GPU 行在能读到温度时再跟一个 `47°`。
- 文字带 **1px 八方向深色描边**，压在白色或花哨壁纸上也读得清，不依赖任何底图。
- 数值 ≥ 90% 时数字转为警示红 `#E6624F`（和面板模式同一套规则）。
- 依然可以左键拖动、右键打开菜单；切换时以右边缘为锚，不会从右上角跳走。
- 开关状态写入 `settings.ini` 的 `minimal=1`，重启后保持。
- 画布做了整块命中测试，点在文字之间的空白处也不会「穿透」到桌面。

> 忘了怎么退出极简模式时：双击托盘图标、按 `Ctrl+Alt+H`、或让窗口获得焦点后按 `Alt+Space` → 「极简模式」。

> 极简模式只保留三项最必要的指标，磁盘与网络吞吐不在其中。

---

## 数据来源

| 指标 | 来源 |
| --- | --- |
| CPU 使用率 | `GetSystemTimes`（内核态 + 空闲时间差），采样线程 500 ms |
| CPU 频率 | PDH `\Processor Information(_Total)\% Processor Performance` × 注册表 `~MHz` 基准（基准读不到时用 PDH `Processor Frequency`） |
| CPU 核心 / 线程 | `GetLogicalProcessorInformationEx(RelationProcessorCore)` |
| 内存总量 / 可用 | `GlobalMemoryStatusEx` |
| 内存已提交 | PDH `\Memory\Committed Bytes` |
| GPU 型号 | **DXGI** `CreateDXGIFactory1` + `IDXGIFactory1.EnumAdapters1`（不限厂商；同一块卡被多个虚拟显示器枚举成多条时按「型号 + 厂商 + 显存」去重，虚拟显示适配器与 `Microsoft Basic Render Driver` 会被过滤掉） |
| GPU 利用率 / 显存占用 | 优先 **NVML** `nvmlDeviceGetUtilizationRates` / `nvmlDeviceGetMemoryInfo`；拿不到时（A 卡、核显、驱动没装）退 PDH `\GPU Engine(*)\Utilization Percentage` 与 `\GPU Adapter Memory(*)\Dedicated Usage` |
| GPU 温度·功耗·风扇·频率 | **NVML 直连** `nvml.dll`（仅 NVIDIA；这几项 PDH 拿不到） |
| 磁盘吞吐 | PDH `\PhysicalDisk(_Total)\Disk Read Bytes/sec` / `Disk Write Bytes/sec` || 网络吞吐 | PDH `\Network Interface(*)\Bytes Received/sec` / `Bytes Sent/sec`（通配枚举所有实例后过滤 isatap / Teredo / Loopback / Pseudo） |

**关于 PDH（性能数据助手）英文计数器**：老做法用 `System.Diagnostics.PerformanceCounter`，它必须用「系统显示语言」下的计数器名（中文系统是 `物理磁盘`，德文系统又是另一套），在非英文 Windows 上很容易直接抛 `InvalidOperationException`，而且原来这四项挂在同一个 `try` 里 —— 一项失败，磁盘、网络、已提交内存、CPU 频率全部变成 `N/A`。现在统一走 `PdhAddEnglishCounterW`：名字永远是英文，任何语言的 Windows 都能解析；所有计数器挂在同一个查询上，一次 `PdhCollectQueryData` 刷新全部；网络实例名用 `(*)` 通配枚举，不再依赖本地化的类别名。PDH 完全不可用的系统（极少数精简掉 PDH 的机器）只是这几项显示 `N/A`，CPU / GPU / 内存照常。

**关于 NVML 直连**：不像常见做法那样每次 `spawn nvidia-smi.exe` 再解析文本，这里在进程内一次性 `nvmlInit_v2`，之后按需调用 `nvmlDeviceGet*`。开销从「每轮几十毫秒 + 一个子进程」降到「微秒级函数调用」。

初始化也不再是「一次定生死」：开机自启时显卡驱动常常比程序晚就绪，NVML 会每 12 秒重试一次；跑起来之后每 30 秒核对一次设备数，驱动重启、显卡热插拔、eGPU 接入导致的卡数变化会自动跟上，显卡行也会跟着重排（不会一直停在 `--`）。

**关于 DXGI 与 PDH 的显卡兜底**：型号来自 DXGI（任何厂商都有），所以 A 卡 / 核显机器也能看到显卡型号。实时指标上，NVIDIA 走 NVML（利用率、显存、温度、功耗、风扇、频率全都齐），其它卡退到 PDH 的 `\GPU Engine(*)\Utilization Percentage` 和 `\GPU Adapter Memory(*)\Dedicated Usage` —— 只补利用率和显存占用，温度 / 功耗 / 风扇 / 频率这几项 PDH 没有对应计数器，会留空。这两项 PDH 计数器是 Win10 1709 才加的，更老的系统上显卡行只剩型号。GPU Engine 计数器会枚举出几百个实例（每个进程 × 每个引擎 × 每块卡各一条），所以它跑在一个**独立的 PDH 查询**里，只在 NVML 拿不到指标时才采集，NVML 齐全时一次都不碰。

**任何一项指标拿不到都不会让程序出错**，只会以 `N/A` / 空白显示：

- RTX 4060 笔记本 / 桌面版的 `nvmlDeviceGetPowerUsage` 在本机返回 `rc=3`（不支持），功耗位显示 `功耗 N/A`，其余指标照常。
- 没有 NVIDIA 显卡时，显卡行显示「未检测到 NVIDIA 驱动」（有 DXGI 可用时型号照常显示），CPU 与内存仍正常工作。
- 没有 CPU 温度传感器（`MSAcpi_ThermalZoneTemperature` 返回 0 个实例的机器）时，不显示 CPU 温度。

---

## 兼容性

「换台电脑还能不能跑」是这一版的重点。下面每一条都对应一种真实会遇到的机器：

| 场景 | 处理方式 |
| --- | --- |
| Windows 7 SP1 / 8.1 | 只调用 .NET 4.0 就有的 API；DPI 感知三级回退：`SetProcessDpiAwarenessContext`（Win10 1703+）→ `SetProcessDpiAwareness`（Win8.1+，shcore）→ `SetProcessDPIAware`（Vista+，user32），缺哪个模块就退下一级 |
| 系统语言不是中文 / 英文（德 / 法 / 西 / 日…） | 磁盘、网络、已提交内存、CPU 频率全部改用 PDH **英文**计数器，不再依赖本地化的性能计数器名 |
| 高 DPI（125 % / 150 % / 200 %）、缩放改来改去 | 窗口尺寸、位置、工作区判断都用 `CompositionTarget.TransformToDevice` 的实时缩放换算，不写死像素，也不用 4.6.2 才有的 `GetDpi` |
| 多显示器、副屏拔插 | 「回到右上角」按主屏算；平时启动只把窗口约束在**它自己那块屏**的工作区内（`MonitorFromWindow` + `GetMonitorInfo`），副屏上的窗口不会再被拽回主屏；收到 `WM_DISPLAYCHANGE` 后重新贴合 |
| 显卡驱动晚于程序启动、驱动重启、热插拔 / eGPU | NVML 每 12 秒重试初始化，就绪后每 30 秒核对设备数，变化就重建 |
| 没有 NVIDIA 显卡、只有核显或 A 卡 | 型号走 DXGI（不限厂商），利用率和显存占用退 PDH GPU 计数器（Win10 1709+）；温度 / 功耗 / 风扇 / 频率留空，CPU 与内存照常 |
| 超过 64 个逻辑处理器（工作站 / 双路服务器） | 线程数用 `GetActiveProcessorCount(ALL_PROCESSOR_GROUPS)` 取，不会像 `Environment.ProcessorCount` 那样在 96 线程的机器上只报 64 |
| 远程桌面 / 虚拟机 / 软件渲染（远程会话默认关硬件加速） | 渲染层级 < 2 时自动去掉面板投影和菜单阴影，避免软件渲染下的糊字与卡顿 |
| 性能计数器损坏、精简系统没有 PDH 数据 | 采样线程不会因为一次异常整体死掉；PDH 打开失败或连续 10 次采集失败会自动关闭并在 15 秒后重试，界面继续刷新 CPU / GPU / 内存 |
| 受限用户、受管企业环境 | 配置目录三级回退；自启动失败退回注册表 Run 键；托盘被拒也不影响任务栏、系统菜单、热键三条入口 |
| 未知异常 | `AppDomain.UnhandledException`、`DispatcherUnhandledException`、`TaskScheduler.UnobservedTaskException` 三处兜底：写日志 + 原生提示框，不再静默消失 |

另外：单文件 exe、AnyCPU（32 / 64 位系统都能跑）、不需要管理员权限、不注册系统服务或驱动、不写入任何位置以外的目录（除了你选的开机自启项）。

---

## 从源码构建

无需 Visual Studio、无需 .NET SDK，只用 Windows 自带的 .NET Framework 编译器：

```
build.bat
```

输出 `dist\HardwareHud.exe`。脚本内部就是一行 `csc.exe`：

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
  /target:winexe /optimize+ /out:dist\HardwareHud.exe
  /win32icon:assets\HardwareHud.ico
  /r:System.dll /r:System.Core.dll /r:System.Xaml.dll
  /r:...\WPF\WindowsBase.dll /r:...\WPF\PresentationCore.dll
  /r:...\WPF\PresentationFramework.dll
  src\HardwareHud.cs
```

唯一的源文件是 `src\HardwareHud.cs`（约 3100 行，C# 5 语法，因为自带的 csc 只支持到 C# 5）。

> 重新编译前请先退出正在运行的实例，否则 `csc` 会因为文件被占用而报 `CS0016`。

---

## 开发期诊断开关

程序内置了一套「不经过屏幕抓取」的自检手段，用于精确验证渲染结果：

```
HardwareHud.exe --snapshot out.png --scale 2 --wait 5000
```

| 参数 | 作用 |
| --- | --- |
| `--snapshot <png>` | 把面板用 `RenderTargetBitmap` 渲染成 PNG 后退出；可以和正在运行的正常实例并存（自检模式不抢单实例锁） |
| `--scale <n>` | 渲染倍率（默认 2） |
| `--wait <ms>` | 采样多久后再截图（默认 2500；设为 60000 可拿到满历史） |
| `--geometry <txt>` | 额外输出窗口 / 画布的实际几何，以及 `tray=` / `hotkey=` / `sysmenu=` / `taskbar=` / `pdh=` / `gpu=` / `tip=` 这些注册与采集状态 |
| `--shortcut` | 在桌面创建 / 修复快捷方式后退出，结果写入 exe 同目录的 `shortcut-result.txt`（注意：exe 放在 `F:\` 工作区时这一步一定会失败，见上面的警告） |
| `--minimal` | 本次进程强制以极简模式启动（不写回配置） |
| `--toggle` | 启动后再切换一次模式，用来验证两种模式之间的互相切换 |
| `--nogpu` | 强制关掉 NVML，模拟「这台机器没有 NVIDIA 卡」，用来验证 DXGI 型号 + PDH GPU 计数器的兜底路径 |

同时会在 PNG 旁边写出 `out.png.layout.txt`：逐行的 label / 数字 / 单位 / 曲线窗口 / 说明行的精确包围盒，外加自动检查的 **文字是否重叠、是否越界、是否溢出可用宽度、序列点数与极值**。所有检查项应全部为 `no` / `ok`。

```
$ .\dist\HardwareHud.exe --snapshot shot.png --wait 60000
$ .\.build\CurveCheck.exe shot.png 34 154 628 64   # 验曲线：40 列每列取值 + 有无曲线
```

---

## 目录结构

```
hardware-hud/
├─ src/HardwareHud.cs      唯一源文件（采集 / 视觉 / 窗口 / 菜单 / 托盘）
├─ assets/HardwareHud.ico  多尺寸图标（16…256，构建时嵌入 exe）
├─ build.bat               一行 csc 构建脚本（ASCII + CRLF）
├─ dist/HardwareHud.exe    产物
├─ tools/capture.ps1       整屏截图（早期手工核对用）
└─ .build/                 开发期像素分析工具（csc 独立编译，可整目录删除）
   ├─ CurveCheck.exe       曲线可见性 / 高度校验
   ├─ Blues.exe            低饱和（内存）曲线校验
   ├─ Grab.exe             按窗口句柄抓屏
   ├─ MkIcon.exe           生成多尺寸 ICO
   ├─ PixProbe.exe         采样列饱和度
   ├─ ColDump.exe          单列逐像素 RGB
   ├─ CellAudit.exe        分格统计彩色像素
   ├─ CoreProbe.exe        枚举 GetLogicalProcessorInformationEx
   ├─ Fx.exe / Fx2.exe     WPF 绘制原语的隔离对照组
   └─ Ascii / Ink / Bands / Pix / Audit / WellDump   早期 ASCII 降采样工具
```

安装方式：把桌面快捷方式 `硬件监测` 指向 `dist\HardwareHud.exe` 即可（图标已嵌入 exe，快捷方式直接用 `,0`）。

---

## 已知限制

- **仅 Windows**。WPF + Win32 互操作，没有跨平台打算。
- **GPU 的实时指标分两档**：NVIDIA 卡经 NVML 拿全（利用率、显存、温度、功耗、风扇、频率）；AMD / Intel 核显走 DXGI 型号 + PDH GPU 计数器，只有利用率和显存占用，温度 / 功耗 / 风扇 / 频率留空。这块 PDH 计数器需要 Win10 1709 及以上。
- 采样 500 ms、历史 112 点，也就是固定约 56 秒窗口；没有做可配置。
- 极简模式与面板模式共用同一份采样和同一套阈值，但极简模式只显示 CPU / GPU / 内存三项。
- **Windows 7 需要自行装 .NET Framework 4.x**（系统自带的只有 3.5）；Win7 上没有 `Bahnschrift` 字体，标题会自动退到 Microsoft YaHei UI / Segoe UI，排版不变。
- 显卡功耗位在部分型号上显示 `功耗 N/A`：NVML 对该卡返回「不支持」，不是读取失败，其余指标正常。
- 通知区域图标需要系统托盘可用；被组策略或受限桌面拒绝时会静默跳过（其余入口不受影响）。
- `--snapshot` 等开关属于开发设施，保留在发布版本里但不影响正常使用。
