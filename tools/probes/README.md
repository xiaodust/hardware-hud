# 能力探测脚本

这些是开发期间用来确认「这台机器到底能不能读到某项数据」的一次性脚本，纯 PowerShell，不依赖本仓库的任何构建产物。排查 issue 时把它们跑一遍，输出可以直接贴在问题里。

| 脚本 | 作用 |
| --- | --- |
| `probe-counters.ps1` | 用 `PerformanceCounter` 读 `Processor Information` / `Memory` / `PhysicalDisk` / `System` / `Process` 五个类别，并测一下 16 个逻辑核逐核读一次的耗时；用来对比 `PerformanceCounter` 与程序里 PDH 英文计数器路径的差异 |
| `probe-gpu.ps1` | 列出含 `GPU` 的性能计数器类别、`GPU Engine` 实例数与计数器名、`GPU Adapter Memory` 实例，以及 `Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine` 的属性；用来确认 PDH GPU 兜底路径是否可用（需要 Win10 1709+） |
| `probe-nvml.ps1` | 内嵌 C# 直接 P/Invoke `nvml.dll`：初始化、卡数、型号、利用率、显存、温度、功耗、风扇、核心频率，逐项打印 `rc`；用来确认 NVML 在某个驱动版本上支持哪些指标 |
| `probe-nvml2.ps1` | 继续探测 `nvml.dll` 的「边角」接口：功耗上限、累计能耗、风扇转速 RPM、温度阈值、各频率类型、PCI 信息；用来判断功耗 / 风扇 `N/A` 是「不支持」还是「读错了」 |

运行方式（在本目录或仓库根目录都行）：

```
powershell -ExecutionPolicy Bypass -File tools\probes\probe-nvml.ps1
```

注意：

- `probe-nvml*.ps1` 需要机器上装了 NVIDIA 驱动（`nvml.dll` 由驱动提供），A 卡 / 核显机器上会直接失败，这是预期行为。
- `probe-gpu.ps1` 与 `probe-counters.ps1` 只用系统自带的 .NET / WMI，不需要额外安装。
- 输出里会带上机器上的显卡型号、CPU 型号等硬件信息，贴到公开 issue 前请自行确认不介意。
