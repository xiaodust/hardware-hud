// HardwareHud —— 桌面硬件实时监测悬浮窗
//   采集：CPU(GetSystemTimes) / 内存(GlobalMemoryStatusEx) / 磁盘·网络(PDH 英文计数器) / GPU(NVML 直连)
//   界面：WPF 全自绘，无边框磨砂面板 + 走纸式实时曲线
// 编译：build.bat（csc /target:winexe，C# 5 语法）
// 兼容：Windows 7 SP1 / 8.1 / 10 / 11 + .NET Framework 4.0 以上（Win8 起系统自带 4.5+）；
//       磁盘·网络用 PDH 英文计数器，不依赖系统显示语言；显卡只支持 NVIDIA（NVML）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;

namespace HardwareHud
{
    // ═══════════════════════════ 原生互操作 ═══════════════════════════
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint Low;
            public uint High;
            public ulong Value { get { return ((ulong)High << 32) | Low; } }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref int length);

        // 统计物理核心数：RelationProcessorCore 每条记录 = 一个物理核（混合架构不能用 线程数/2）
        public static int PhysicalCoreCount()
        {
            const int RelationProcessorCore = 0;   // RelationProcessorCore 枚举值是 0（1 才是 NumaNode）
            int len = 0;
            if (GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len) != 0 || len <= 0)
                return 0;

            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len) == 0) return 0;
                int offset = 0, count = 0;
                while (offset + 8 <= len)
                {
                    int rel = Marshal.ReadInt32(buf, offset);
                    int size = Marshal.ReadInt32(buf, offset + 4);
                    if (size <= 8) break;
                    if (rel == RelationProcessorCore) count++;
                    offset += size;
                }
                return count;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        [DllImport("kernel32.dll")]
        static extern int GetActiveProcessorCount(ushort groupNumber);

        // 逻辑处理器总数：Environment.ProcessorCount 在超过 64 个逻辑处理器的机器上
        // 会被处理器组截断（例如 96 线程只报 64），GetActiveProcessorCount 才是真实值
        public static int LogicalThreadCount()
        {
            try
            {
                int n = GetActiveProcessorCount(0xFFFF);   // ALL_PROCESSOR_GROUPS
                if (n > 0) return n;
            }
            catch { }
            try { return Environment.ProcessorCount; } catch { return 1; }
        }

        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_APPWINDOW = 0x00040000;

        // 让窗口在任务栏生成按钮：WPF 的 ShowInTaskbar 只处理了一半，
        // 无边框窗口还需要手动摘掉 TOOLWINDOW 并打上 APPWINDOW。
        public static void ForceAppWindow(IntPtr hwnd)
        {
            try
            {
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                ex = (ex & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW;
                SetWindowLong(hwnd, GWL_EXSTYLE, ex);
            }
            catch { }
        }

        // 反方向：只在托盘里留图标，任务栏和 Alt+Tab 都不出现。
        // 同样要手动打 TOOLWINDOW 并摘掉 APPWINDOW —— WPF 的 ShowInTaskbar=false
        // 在无边框窗口上会被随后的样式重设覆盖掉。
        public static void ForceToolWindow(IntPtr hwnd)
        {
            try
            {
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                ex = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
                SetWindowLong(hwnd, GWL_EXSTYLE, ex);
            }
            catch { }
        }

        // ── 通知区域（托盘）图标 ──
        public const uint NIM_ADD = 0;
        public const uint NIM_MODIFY = 1;
        public const uint NIM_DELETE = 2;

        public const uint NIF_MESSAGE = 0x01;
        public const uint NIF_ICON = 0x02;
        public const uint NIF_TIP = 0x04;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
        public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int ExtractIconEx(string lpszFile, int nIconIndex,
                                               out IntPtr phiconLarge, out IntPtr phiconSmall, int nIcons);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        // Explorer 重启后会广播 "TaskbarCreated"，收到它的程序要把托盘图标重新加回去，
        // 否则图标会永久消失 —— 这是"托盘图标不见了"最常见的原因。
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);
        public static readonly IntPtr IDI_APPLICATION = new IntPtr(32512);

        // Windows 11 把每个托盘图标的显示状态存在这里
        public const string NotifyIconSettingsKey = @"Control Panel\NotifyIconSettings";

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ── 窗口系统菜单（任务栏右键 / Alt+Space 用的就是它）──
        public const int GWL_STYLE = -16;
        public const int WS_SYSMENU = 0x00080000;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_FRAMECHANGED = 0x0020;

        public const uint MF_STRING = 0x0000;
        public const uint MF_SEPARATOR = 0x0800;
        public const uint MF_CHECKED = 0x0008;
        public const uint MF_BYCOMMAND = 0x0000;

        [DllImport("user32.dll")]
        public static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                               int X, int Y, int cx, int cy, uint uFlags);

        // 无边框窗口默认没有 WS_SYSMENU，任务栏右键就不会弹系统菜单。
        // 注意：只改样式还不够，必须 SWP_FRAMECHANGED 让系统重建非客户区，GetSystemMenu 才会返回菜单。
        public static void EnsureSystemMenu(IntPtr hwnd)
        {
            try
            {
                int style = GetWindowLong(hwnd, GWL_STYLE);
                if ((style & WS_SYSMENU) != 0) return;
                SetWindowLong(hwnd, GWL_STYLE, style | WS_SYSMENU);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                             SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
            }
            catch { }
        }

        // ── 显示器工作区（多屏 / 分辨率变化时判断窗口是否还在可见范围内）──
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

        // 取窗口所在显示器的工作区（物理像素）。老系统上没有这个 API 时返回 false，调用方退回主屏。
        public static bool TryGetWorkArea(IntPtr hwnd, out RECT work)
        {
            work = new RECT();
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (mon == IntPtr.Zero) return false;
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = (uint)Marshal.SizeOf(typeof(MONITORINFO));
                if (!GetMonitorInfo(mon, ref mi)) return false;
                if (mi.rcWork.Right - mi.rcWork.Left < 40) return false;
                if (mi.rcWork.Bottom - mi.rcWork.Top < 40) return false;
                work = mi.rcWork;
                return true;
            }
            catch { return false; }
        }

        // 致命错误提示框：不走 WPF（崩的可能正是 WPF），直接用 user32
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
        public static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        public const uint MB_OK = 0x00000000;
        public const uint MB_ICONERROR = 0x00000010;
        public const uint MB_SETFOREGROUND = 0x00010000;

        // DPI 感知的老接口：Win8.1+ 的 shcore / Vista+ 的 user32。
        // 调用方必须逐级 try/catch —— Win7 上 shcore.dll 根本不存在。
        [DllImport("shcore.dll")]
        public static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
    }

    // ═══════════════════════════ DPI 换算 ═══════════════════════════
    // VisualTreeHelper.GetDpi 是 .NET 4.6.2 才有的 API；在只装了 4.0~4.6.1 的机器上
    // 调用它会抛 MissingMethodException，整个程序启动即崩。用 4.0 就有的
    // PresentationSource.TransformToDevice 拿同样的缩放比，兼容面直接放大到 Win7 起。
    internal static class Screen
    {
        public static double Scale(Visual v)
        {
            try
            {
                if (v != null)
                {
                    PresentationSource src = PresentationSource.FromVisual(v);
                    if (src != null && src.CompositionTarget != null)
                    {
                        Matrix m = src.CompositionTarget.TransformToDevice;
                        if (m.M11 > 0.1 && m.M11 < 10) return m.M11;
                    }
                }
            }
            catch { }
            return 1.0;
        }
    }

    // ═══════════════════════════ PDH（英文性能计数器） ═══════════════════════════
    // 不用 System.Diagnostics.PerformanceCounter：它按系统语言拼计数器名，
    // 德语/法语/西班牙语 Windows 上 "PhysicalDisk" 这类英文名会直接抛异常（界面全是 "--"）。
    // PdhAddEnglishCounterW 走英文路径，通配实例枚举免去拼网络接口名，一个查询装下全部计数器。
    internal static class Pdh
    {
        // PDH_FMT_COUNTERVALUE：cStatus + 对齐空洞 + double（实测 double 在 offset 8）
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        public struct FMTVALUE
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double DoubleValue;
        }

        // PDH_FMT_COUNTERVALUE_ITEM_W
        [StructLayout(LayoutKind.Sequential)]
        public struct FMTITEM
        {
            public IntPtr Name;
            public FMTVALUE Value;
        }

        const uint FMT_DOUBLE = 0x00000200;    // PDH_FMT_DOUBLE
        const uint FMT_NOCAP100 = 0x00008000;  // PDH_FMT_NOCAP100：% Processor Performance 可以超过 100
        public const uint MORE_DATA = 0x800007D2;
        const uint INVALID_DATA = 0xC0000BC6;

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern uint PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern uint PdhAddEnglishCounterW(IntPtr query, string fullPath, IntPtr userData, out IntPtr counter);

        [DllImport("pdh.dll", ExactSpelling = true)]
        static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", ExactSpelling = true)]
        static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FMTVALUE value);

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufSize, out uint itemCount, IntPtr buffer);

        [DllImport("pdh.dll", ExactSpelling = true)]
        static extern uint PdhCloseQuery(IntPtr query);

        public static bool Open(out IntPtr query)
        {
            query = IntPtr.Zero;
            try { return PdhOpenQueryW(null, IntPtr.Zero, out query) == 0 && query != IntPtr.Zero; }
            catch { query = IntPtr.Zero; return false; }   // 极简系统没有 pdh.dll 时不要炸
        }

        public static void Close(IntPtr query)
        {
            if (query == IntPtr.Zero) return;
            try { PdhCloseQuery(query); } catch { }
        }

        // 加不进去的计数器返回 Zero：一条坏了不影响其它条
        public static IntPtr Add(IntPtr query, string path)
        {
            if (query == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                IntPtr c;
                return PdhAddEnglishCounterW(query, path, IntPtr.Zero, out c) == 0 ? c : IntPtr.Zero;
            }
            catch { return IntPtr.Zero; }
        }

        public static bool Collect(IntPtr query)
        {
            if (query == IntPtr.Zero) return false;
            try { return PdhCollectQueryData(query) == 0; } catch { return false; }
        }

        // 取标量值；首帧速率类计数器是 PDH_INVALID_DATA（没有基线），返回 false 即可
        public static bool TryGet(IntPtr counter, out double value)
        {
            value = 0;
            if (counter == IntPtr.Zero) return false;
            try
            {
                uint type;
                FMTVALUE v;
                uint rc = PdhGetFormattedCounterValue(counter, FMT_DOUBLE | FMT_NOCAP100, out type, out v);
                if (rc == INVALID_DATA) return false;
                if (rc != 0) return false;
                if (v.CStatus != 0) return false;
                double d = v.DoubleValue;
                if (double.IsNaN(d) || double.IsInfinity(d)) return false;
                value = d;
                return true;
            }
            catch { return false; }
        }

        // 通配实例（网络接口）：一次拿回所有实例名和值。路径里写 "\Network Interface(*)"，
        // 这样实例名带括号、中文、空格都不会把计数器路径拼断。
        public static bool TryGetArray(IntPtr counter, out string[] names, out double[] values)
        {
            names = new string[0];
            values = new double[0];
            if (counter == IntPtr.Zero) return false;
            try
            {
                uint size = 0, count = 0;
                uint rc = PdhGetFormattedCounterArrayW(counter, FMT_DOUBLE | FMT_NOCAP100, ref size, out count, IntPtr.Zero);
                if (rc != MORE_DATA || size == 0 || count == 0) return false;

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    IntPtr buf = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        uint got = 0;
                        rc = PdhGetFormattedCounterArrayW(counter, FMT_DOUBLE | FMT_NOCAP100, ref size, out got, buf);
                        if (rc != 0) continue;              // 实例数在两次调用之间变了：重来

                        int stride = Marshal.SizeOf(typeof(FMTITEM));
                        string[] ns = new string[got];
                        double[] vs = new double[got];
                        for (int i = 0; i < got; i++)
                        {
                            IntPtr item = new IntPtr(buf.ToInt64() + i * stride);
                            FMTITEM it = (FMTITEM)Marshal.PtrToStructure(item, typeof(FMTITEM));
                            string name = Marshal.PtrToStringUni(it.Name);
                            ns[i] = name != null ? name : "";
                            vs[i] = it.Value.CStatus == 0 ? it.Value.DoubleValue : double.NaN;
                        }
                        names = ns;
                        values = vs;
                        return true;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                return false;
            }
            catch { return false; }
        }
    }

    // ═══════════════════════════ NVML（NVIDIA 驱动直连） ═══════════════════════════
    internal static class Nvml
    {
        [DllImport("nvml.dll")]
        static extern int nvmlInit_v2();

        [DllImport("nvml.dll")]
        static extern int nvmlDeviceGetCount_v2(out int count);

        [DllImport("nvml.dll")]
        static extern int nvmlDeviceGetHandleByIndex_v2(int index, out IntPtr dev);

        [DllImport("nvml.dll")]
        static extern int nvmlDeviceGetName(IntPtr dev, StringBuilder name, int len);

        [StructLayout(LayoutKind.Sequential)]
        struct Utilization { public uint Gpu; public uint Memory; }

        [StructLayout(LayoutKind.Sequential)]
        struct MemInfo { public ulong Total; public ulong Free; public ulong Used; }

        [DllImport("nvml.dll")]
        static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out Utilization u);

        [DllImport("nvml.dll")]
        static extern int nvmlDeviceGetMemoryInfo(IntPtr dev, out MemInfo m);

        [DllImport("nvml.dll")]
        static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensor, out uint temp);

        [DllImport("nvml.dll")]
        static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint milliWatt);

        [DllImport("nvml.dll")]
        static extern int nvmlDeviceGetFanSpeed(IntPtr dev, out uint percent);

        [DllImport("nvml.dll")]
        static extern int nvmlDeviceGetNumFans(IntPtr dev, out uint count);

        [DllImport("nvml.dll")]
        static extern int nvmlDeviceGetClockInfo(IntPtr dev, int type, out uint mhz);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibrary(string fileName);

        static IntPtr[] _devices = new IntPtr[0];
        static string[] _names = new string[0];
        public static bool Available;

        // 开发自检用（--nogpu）：模拟没有 N 卡/驱动，验证 DXGI 兜底路径
        public static bool Disabled;

        static bool _preloadTried;
        static int _nextCheck;

        public static int Count { get { return _devices.Length; } }
        public static string[] Names { get { return _names; } }

        // nvml.dll 的位置因驱动版本而异：新驱动在 System32，老驱动在 NVSMI 目录。
        // 先按绝对路径 LoadLibrary 进来，后面的 DllImport("nvml.dll") 会按模块名命中已加载的模块。
        static void Preload()
        {
            if (_preloadTried) return;
            _preloadTried = true;
            try
            {
                List<string> cands = new List<string>();
                try { cands.Add(Path.Combine(Environment.SystemDirectory, "nvml.dll")); } catch { }
                try { cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvml.dll")); } catch { }
                try { cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"NVIDIA Corporation\NVSMI\nvml.dll")); } catch { }
                for (int i = 0; i < cands.Count; i++)
                {
                    try
                    {
                        if (File.Exists(cands[i]) && LoadLibrary(cands[i]) != IntPtr.Zero) return;
                    }
                    catch { }
                }
            }
            catch { }
        }

        // 初始化可以反复调用（NVML 内部引用计数）：驱动晚于本程序就绪、驱动重启、外接显卡热插拔都靠它
        public static void Init()
        {
            if (Disabled) { Reset(); return; }
            Preload();
            try
            {
                if (nvmlInit_v2() != 0) { Reset(); return; }
                int n;
                if (nvmlDeviceGetCount_v2(out n) != 0 || n <= 0) { Reset(); return; }

                IntPtr[] devs = new IntPtr[n];
                string[] names = new string[n];
                for (int i = 0; i < n; i++)
                {
                    IntPtr dev = IntPtr.Zero;
                    if (nvmlDeviceGetHandleByIndex_v2(i, out dev) != 0) { names[i] = "GPU " + i; continue; }
                    devs[i] = dev;
                    StringBuilder sb = new StringBuilder(96);
                    nvmlDeviceGetName(dev, sb, 96);
                    names[i] = sb.ToString();
                }
                _devices = devs;
                _names = names;
                Available = true;
            }
            catch { Reset(); }
        }

        static void Reset()
        {
            _devices = new IntPtr[0];
            _names = new string[0];
            Available = false;
        }

        // 采样线程每轮问一次：没就绪就隔 12 秒重试（开机自启时驱动常常还没起来），
        // 已就绪就每 30 秒核对一次设备数（热插拔 / 驱动重启）
        public static void Refresh()
        {
            if (Disabled) return;
            if (unchecked(Environment.TickCount - _nextCheck) < 0) return;
            _nextCheck = unchecked(Environment.TickCount + (Available ? 30000 : 12000));
            try
            {
                if (!Available) { Init(); return; }
                int n;
                if (nvmlDeviceGetCount_v2(out n) != 0 || n != _devices.Length) Init();
            }
            catch { Reset(); }
        }

        public static GpuStat[] Read()
        {
            if (!Available) return new GpuStat[0];
            try { return ReadCore(); }
            catch { Reset(); return new GpuStat[0]; }   // 驱动重启后句柄失效：清空，等 Refresh 重建
        }

        static GpuStat[] ReadCore()
        {
            List<GpuStat> list = new List<GpuStat>();
            for (int i = 0; i < _devices.Length; i++)
            {
                IntPtr dev = _devices[i];
                if (dev == IntPtr.Zero) continue;

                GpuStat g = new GpuStat();
                g.Index = i;
                g.Name = _names[i];
                g.NvmlMetrics = true;

                Utilization u;
                if (nvmlDeviceGetUtilizationRates(dev, out u) == 0)
                {
                    g.Util = (float)u.Gpu;
                    g.MemUtil = (float)u.Memory;
                }
                else { g.Util = float.NaN; g.MemUtil = float.NaN; }

                MemInfo m;
                if (nvmlDeviceGetMemoryInfo(dev, out m) == 0 && m.Total > 0)
                {
                    g.MemUsedMb = m.Used / 1048576.0f;
                    g.MemTotalMb = m.Total / 1048576.0f;
                }
                else { g.MemUsedMb = float.NaN; g.MemTotalMb = float.NaN; }

                uint t;
                g.TempC = nvmlDeviceGetTemperature(dev, 0, out t) == 0 ? (float)t : float.NaN;

                uint p;
                g.PowerW = nvmlDeviceGetPowerUsage(dev, out p) == 0 ? p / 1000.0f : float.NaN;

                g.FanPct = float.NaN;
                uint fans;
                if (nvmlDeviceGetNumFans(dev, out fans) == 0 && fans > 0)
                {
                    uint f;
                    if (nvmlDeviceGetFanSpeed(dev, out f) == 0) g.FanPct = (float)f;
                }

                uint c;
                g.ClockMhz = nvmlDeviceGetClockInfo(dev, 0, out c) == 0 ? (float)c : float.NaN;

                list.Add(g);
            }
            return list.ToArray();
        }
    }

    // ═══════════════════════════ DXGI（不限厂商的显卡型号） ═══════════════════════════
    // NVML 只认识 NVIDIA；没有 N 卡或没装驱动的机器上，以前只能显示「未检测到 NVIDIA 驱动」。
    // DXGI 能列出系统里真实的显示适配器（型号 + 独立显存），驱动不给指标时至少型号是对的。
    internal static class Dxgi
    {
        internal struct Adapter
        {
            public string Name;
            public ulong VramBytes;
            public uint Vendor;
            public long Luid;      // 与 PDH "GPU Engine"/"GPU Adapter Memory" 实例名里的 luid 对应
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public long AdapterLuid;
            public uint Flags;
        }

        [DllImport("dxgi.dll", ExactSpelling = true)]
        static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);
        delegate int GetDesc1Fn(IntPtr self, out ADAPTER_DESC1 desc);

        static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");

        static Adapter[] _cache = new Adapter[0];
        static bool _loaded;
        static int _nextCheck;

        // 直接按 vtable 取函数指针：比声明一整套 COM 接口少一半代码，也不用担心接口顺序记错。
        // IDXGIFactory1 里 EnumAdapters1 是第 12 个槽，IDXGIAdapter 里 GetDesc1 是第 10 个槽。
        static T Slot<T>(IntPtr obj, int index) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, index * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        public static Adapter[] Adapters()
        {
            // 首次必须刷新：不能用 TickCount 与 int.MinValue 做差值（那样会溢出成负数，永远不刷）
            if (!_loaded || unchecked(Environment.TickCount - _nextCheck) >= 0) Refresh();
            return _cache;
        }

        static void Refresh()
        {
            _loaded = true;
            _nextCheck = unchecked(Environment.TickCount + 15000);
            List<Adapter> list = new List<Adapter>();
            IntPtr factory = IntPtr.Zero;
            try
            {
                Guid iid = IID_IDXGIFactory1;
                if (CreateDXGIFactory1(ref iid, out factory) != 0 || factory == IntPtr.Zero) return;

                EnumAdapters1Fn next = Slot<EnumAdapters1Fn>(factory, 12);
                for (uint i = 0; i < 16; i++)
                {
                    IntPtr adapter = IntPtr.Zero;
                    if (next(factory, i, out adapter) != 0 || adapter == IntPtr.Zero) break;
                    try
                    {
                        GetDesc1Fn desc = Slot<GetDesc1Fn>(adapter, 10);
                        ADAPTER_DESC1 d;
                        if (desc(adapter, out d) != 0) continue;
                        string name = d.Description != null ? d.Description.Trim() : "";
                        bool software = (d.Flags & 2u) != 0;      // DXGI_ADAPTER_FLAG_SOFTWARE
                        if (software || name.Length == 0 || IsVirtual(name)) continue;
                        ulong vram = d.DedicatedVideoMemory.ToUInt64();

                        // 同一块卡在接了虚拟显示器（Todesk/GameViewer/串流）时会被重复枚举成
                        // 多个条目（LUID 不同但型号显存完全一样），按型号+厂商+显存去重
                        bool dup = false;
                        for (int k = 0; k < list.Count; k++)
                        {
                            if (list[k].Name == name && list[k].Vendor == d.VendorId && list[k].VramBytes == vram)
                            { dup = true; break; }
                        }
                        if (dup) continue;

                        Adapter a = new Adapter();
                        a.Name = name;
                        a.Vendor = d.VendorId;
                        a.VramBytes = vram;
                        a.Luid = d.AdapterLuid;
                        list.Add(a);
                    }
                    finally { if (adapter != IntPtr.Zero) Marshal.Release(adapter); }
                }
            }
            catch { }
            finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }

            _cache = list.ToArray();
        }

        // 远程桌面 / 虚拟显示器 / 基本显示适配器都不是用户理解的「显卡」
        static bool IsVirtual(string name)
        {
            string[] bad = new string[] { "Virtual", "Basic Render", "Basic Display", "Remote", "Mirror", "Indirect", "Parsec" };
            for (int i = 0; i < bad.Length; i++)
                if (name.IndexOf(bad[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // 没有 NVML 指标时用：只有型号和显存总量，利用率等一律 NaN（界面显示为 "--"）
        public static GpuStat[] ReadStats()
        {
            Adapter[] list = Adapters();
            if (list.Length == 0) return new GpuStat[0];
            GpuStat[] arr = new GpuStat[list.Length];
            for (int i = 0; i < list.Length; i++)
            {
                GpuStat g = new GpuStat();
                g.Index = i;
                g.Name = list[i].Name;
                g.Luid = list[i].Luid;
                g.Util = float.NaN;
                g.MemUtil = float.NaN;
                g.MemUsedMb = float.NaN;
                g.MemTotalMb = list[i].VramBytes > 0 ? list[i].VramBytes / 1048576f : float.NaN;
                g.TempC = float.NaN;
                g.PowerW = float.NaN;
                g.FanPct = float.NaN;
                g.ClockMhz = float.NaN;
                arr[i] = g;
            }
            return arr;
        }

        // 用 PDH "GPU Engine(*)\Utilization Percentage" 补利用率（不限厂商，Win10 1709+ 才有这组计数器）。
        // 实例名形如 pid_1234_luid_0x00000000_0x000158BF_phys_0_eng_0_engtype_3D：
        // 同一引擎被多个进程分着用，所以先按 (luid, phys, eng) 把所有进程的占比加起来，
        // 再取这块卡最忙的那个引擎作为利用率 —— 各引擎相加会超过 100%，取最大才和任务管理器一致。
        public static void ApplyUtil(GpuStat[] stats, string[] names, double[] values)
        {
            if (stats == null || stats.Length == 0 || names == null || values == null) return;
            Dictionary<string, double> engines = new Dictionary<string, double>();
            int n = Math.Min(names.Length, values.Length);
            for (int i = 0; i < n; i++)
            {
                double v = values[i];
                if (double.IsNaN(v) || v < 0) continue;
                long luid;
                string key;
                if (!ParseEngine(names[i], out luid, out key)) continue;
                double cur;
                if (engines.TryGetValue(key, out cur)) engines[key] = cur + v;
                else engines[key] = v;
            }
            Dictionary<long, double> busiest = new Dictionary<long, double>();
            foreach (KeyValuePair<string, double> kv in engines)
            {
                int bar = kv.Key.IndexOf('|');
                if (bar <= 0) continue;
                long luid;
                if (!long.TryParse(kv.Key.Substring(0, bar), NumberStyles.Integer, CultureInfo.InvariantCulture, out luid)) continue;
                double cur;
                if (!busiest.TryGetValue(luid, out cur) || kv.Value > cur) busiest[luid] = kv.Value;
            }
            for (int i = 0; i < stats.Length; i++)
            {
                double v;
                if (stats[i].Luid != 0 && float.IsNaN(stats[i].Util) && busiest.TryGetValue(stats[i].Luid, out v))
                    stats[i].Util = (float)Math.Min(100.0, Math.Max(0.0, v));
            }
        }

        // 用 PDH "GPU Adapter Memory(*)\Dedicated Usage" 补显存占用（实例名 luid_0x00000000_0x000158BF_phys_0）
        public static void ApplyMem(GpuStat[] stats, string[] names, double[] values)
        {
            if (stats == null || stats.Length == 0 || names == null || values == null) return;
            int n = Math.Min(names.Length, values.Length);
            for (int i = 0; i < stats.Length; i++)
            {
                if (stats[i].Luid == 0 || !float.IsNaN(stats[i].MemUsedMb)) continue;
                for (int k = 0; k < n; k++)
                {
                    double v = values[k];
                    if (double.IsNaN(v) || v <= 0) continue;
                    string[] p = names[k] != null ? names[k].Split('_') : new string[0];
                    if (p.Length < 5 || p[0] != "luid") continue;
                    long luid;
                    if (!ParseLuid(p[1], p[2], out luid) || luid != stats[i].Luid) continue;
                    float used = (float)(v / 1048576.0);
                    stats[i].MemUsedMb = used;
                    float total = stats[i].MemTotalMb;
                    if (!float.IsNaN(total) && total > 0)
                    {
                        stats[i].MemUtil = used / total * 100f;
                        if (stats[i].MemUtil > 100f) stats[i].MemUtil = 100f;
                    }
                    break;
                }
            }
        }

        static bool ParseEngine(string name, out long luid, out string key)
        {
            luid = 0;
            key = null;
            if (string.IsNullOrEmpty(name)) return false;
            string[] p = name.Split('_');
            if (p.Length < 9 || p[2] != "luid") return false;
            if (!ParseLuid(p[3], p[4], out luid)) return false;
            key = luid.ToString(CultureInfo.InvariantCulture) + "|" + p[6] + "|" + p[8];
            return true;
        }

        // 实例名里 luid 拆两段：0xHHHHHHHH（高 32 位）+ 0xLLLLLLLL（低 32 位）
        static bool ParseLuid(string hiText, string loText, out long luid)
        {
            luid = 0;
            if (string.IsNullOrEmpty(hiText) || string.IsNullOrEmpty(loText)) return false;
            if (hiText.Length < 3 || loText.Length < 3) return false;
            uint hi, lo;
            if (!uint.TryParse(hiText.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hi)) return false;
            if (!uint.TryParse(loText.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out lo)) return false;
            luid = ((long)hi << 32) | lo;
            return true;
        }
    }

    // ═══════════════════════════ 数据结构 ═══════════════════════════
    public sealed class GpuStat
    {
        public int Index;
        public string Name = "";
        public long Luid;          // 0 = 未知；用于把 PDH 的 GPU 计数器对到这块卡
        public bool NvmlMetrics;   // true = 温度/功耗/频率/风扇来自 NVML，可显示这几项
        public float Util;
        public float MemUtil;
        public float MemUsedMb;
        public float MemTotalMb;
        public float TempC;
        public float PowerW;
        public float FanPct;
        public float ClockMhz;
    }

    public sealed class Snapshot
    {
        public double Cpu;
        public double CpuGhz;
        public double RamPct;
        public double RamUsedGb;
        public double RamTotalGb;
        public double RamCommitGb;
        public double DiskRead;
        public double DiskWrite;
        public double NetIn;
        public double NetOut;
        public double UptimeSec;
        public GpuStat[] Gpus = new GpuStat[0];
    }

    // ═══════════════════════════ 采集器 ═══════════════════════════
    public sealed class Sampler
    {
        const int IntervalMs = 500;
        const int PdhRetryMs = 15000;

        Thread _thread;
        volatile bool _run;
        ulong _prevIdle, _prevKernel, _prevUser;
        bool _primed;

        // PDH：一个查询装下全部计数器，每条单独判成败 —— 少了磁盘计数器不该连网络一起没有
        IntPtr _query = IntPtr.Zero;
        IntPtr _cDiskRead, _cDiskWrite, _cCommit, _cCpuPerf, _cCpuFreq, _cNetIn, _cNetOut;
        IntPtr _cGpuUtil, _cGpuMem;      // GPU Engine / GPU Adapter Memory：不依赖厂商 SDK 的显卡指标
        IntPtr _gpuQuery;                // GPU 计数器单独一个查询：561 个引擎实例只在需要时枚举
        bool _gpuPdhOpen;
        bool _gpuPdhFailed;
        volatile bool _pdhOpen;
        // 初值必须让第一轮立刻尝试打开：Environment.TickCount 在开机超过 24.8 天后是负数，
        // 初值 0 会让 unchecked(TickCount - 0) >= 0 恒为假 —— 磁盘 / 网络 / 已提交 / CPU 频率
        // 将永远停在 "--"（这台 27 天未重启的机器上实测复现）
        int _pdhNextTry = unchecked(Environment.TickCount - 1);
        int _pdhFails;
        // 初值是 "off"、且 _pdhNextTry = 0 时，采样线程第一轮就会开门——但 DiagnosePage 之类
        // 早于第一帧读到的还是 "off"。这里显式给一个初值，避免日志里出现误导性的 pdh=off
        string _pdhStatus = "pending";
        int _gate = 6;

        public string CpuModel = "CPU";
        public int CpuCores;
        public int CpuThreads;
        public double CpuBaseMhz;
        public Action<Snapshot> OnSample;

        public string PdhStatus { get { return _pdhStatus; } }

        public Sampler()
        {
            // 只读注册表和逻辑处理器信息（几十微秒）。性能计数器以前在这里同步初始化，
            // 计数器损坏的机器上会把窗口卡住十几秒 —— 现在全部挪到采样线程里做。
            ReadCpuIdentity();
        }

        void ReadCpuIdentity()
        {
            CpuThreads = Native.LogicalThreadCount();
            CpuCores = Native.PhysicalCoreCount();   // 混合架构（P/E 核）不能用 线程数/2
            if (CpuCores <= 0) CpuCores = CpuThreads / 2;
            CpuBaseMhz = 0;

            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (k != null)
                    {
                        object name = k.GetValue("ProcessorNameString");
                        if (name != null) CpuModel = ShortCpuName(Convert.ToString(name));
                        object cores = k.GetValue("NumberOfCores");
                        if (cores != null) { int c; if (int.TryParse(Convert.ToString(cores), out c) && c > 0) CpuCores = c; }
                        object mhz = k.GetValue("~MHz");
                        if (mhz != null) { Convert.ToString(mhz); int m; if (int.TryParse(Convert.ToString(mhz), out m) && m > 0) CpuBaseMhz = m; }
                    }
                }
            }
            catch { }

            // 注册表里没有 ~MHz 时不在这里补：PDH 的 Processor Frequency 会在首帧补上，
            // 以前这条退路用的是性能计数器 + Sleep(120)，本就慢，还会在坏机器上卡住
        }

        // 把 Intel/AMD 各种写法收成一眼能看懂的型号：
        //   "12th Gen Intel(R) Core(TM) i5-12600KF"        → "i5-12600KF"
        //   "AMD Ryzen 7 5800X 8-Core Processor"           → "AMD Ryzen 7 5800X"
        //   "Intel(R) Xeon(R) Silver 4214R CPU @ 2.40GHz"  → "Intel Xeon Silver 4214R"
        //   "13th Gen Intel Core i9-13900K"                → "i9-13900K"
        static string ShortCpuName(string raw)
        {
            string s = raw.Trim();

            // 先摘 "Core(TM)" 之后的型号本体 —— 必须在删掉 (TM) 之前做，
            // 否则标记没了就再也找不到这个锚点（i5/i7/i9 这类消费级命名都靠它）
            int core = s.IndexOf("Core(TM)", StringComparison.OrdinalIgnoreCase);
            if (core >= 0) s = s.Substring(core + 9).Trim();

            s = s.Replace("(R)", "").Replace("(r)", "").Replace("(TM)", "").Replace("(tm)", "");
            s = s.Replace("®", "").Replace("™", "");

            // "@ 2.40GHz" 是主频，不是型号
            int at = s.IndexOf('@');
            if (at > 0) s = s.Substring(0, at).Trim();

            // "AMD Ryzen 5 5600G with Radeon Graphics" 后面那半句对识别型号没帮助
            int with = s.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
            if (with > 0) s = s.Substring(0, with).Trim();

            string[] parts = s.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int from = 0;

            // "12th Gen" 这种代际前缀（老机型注册表里没有 Core(TM) 时会出现）
            if (parts.Length >= 3 && parts[from + 1].Equals("Gen", StringComparison.OrdinalIgnoreCase) &&
                parts[from].Length > 2 && char.IsDigit(parts[from][0])) from += 2;

            // "Intel Core i5-12600KF" → "i5-12600KF"：只有后面紧跟 i3/i5/i7/i9 时才去掉厂商前缀，
            // 所以 "Intel Core Ultra 7 155H" 这类新命名会原样保留
            if (parts.Length >= 3 && from + 2 < parts.Length &&
                parts[from].Equals("Intel", StringComparison.OrdinalIgnoreCase) &&
                parts[from + 1].Equals("Core", StringComparison.OrdinalIgnoreCase) &&
                IsCoreModel(parts[from + 2]))
                from += 2;

            List<string> keep = new List<string>(parts.Length);
            for (int i = from; i < parts.Length; i++)
            {
                string p = parts[i];
                if (p.Equals("CPU", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Equals("Processor", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Equals("Dual", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Equals("Quad", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.EndsWith("-Core", StringComparison.OrdinalIgnoreCase)) continue;      // 8-Core
                if (p.EndsWith("Core", StringComparison.OrdinalIgnoreCase) && p.Length > 5 &&
                    char.IsDigit(p[0])) continue;                                            // 8Core
                keep.Add(p);
            }
            s = string.Join(" ", keep.ToArray()).Trim();
            return s.Length > 0 ? s : raw.Trim();
        }

        // i5-12600KF / i7 9700 / i9-13900K 这类
        static bool IsCoreModel(string token)
        {
            if (token == null || token.Length < 3) return false;
            if (token[0] != 'i' && token[0] != 'I') return false;
            if (token[1] != '3' && token[1] != '5' && token[1] != '7' && token[1] != '9') return false;
            return token[2] == '-' || char.IsDigit(token[2]);
        }

        // 英文计数器路径：PdhAddEnglishCounterW 在任何语言的 Windows 上都能解析，
        // 不受本地化计数器名（物理磁盘/Arbeitspeicher…）影响
        const string PathDiskRead = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
        const string PathDiskWrite = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";
        const string PathCommit = @"\Memory\Committed Bytes";
        const string PathCpuPerf = @"\Processor Information(_Total)\% Processor Performance";
        const string PathCpuFreq = @"\Processor Information(_Total)\Processor Frequency";
        const string PathNetIn = @"\Network Interface(*)\Bytes Received/sec";
        const string PathNetOut = @"\Network Interface(*)\Bytes Sent/sec";
        // Win10 1709+ 才有 GPU 计数器；老系统加不上就是 Zero，显卡只剩型号没有指标
        const string PathGpuUtil = @"\GPU Engine(*)\Utilization Percentage";
        const string PathGpuMem = @"\GPU Adapter Memory(*)\Dedicated Usage";

        void InitPdh()
        {
            ClosePdh();
            _pdhNextTry = unchecked(Environment.TickCount + PdhRetryMs);

            IntPtr q;
            if (!Pdh.Open(out q))
            {
                _pdhStatus = "no-pdh";
                return;
            }

            _cDiskRead = Pdh.Add(q, PathDiskRead);
            _cDiskWrite = Pdh.Add(q, PathDiskWrite);
            _cCommit = Pdh.Add(q, PathCommit);
            _cCpuPerf = Pdh.Add(q, PathCpuPerf);
            _cCpuFreq = Pdh.Add(q, PathCpuFreq);
            _cNetIn = Pdh.Add(q, PathNetIn);
            _cNetOut = Pdh.Add(q, PathNetOut);

            if (_cDiskRead == IntPtr.Zero && _cDiskWrite == IntPtr.Zero && _cCommit == IntPtr.Zero &&
                _cCpuPerf == IntPtr.Zero && _cCpuFreq == IntPtr.Zero &&
                _cNetIn == IntPtr.Zero && _cNetOut == IntPtr.Zero)
            {
                // 计数器一个都加不上（性能计数器库被禁用/损坏）：关掉查询，隔 15 秒再试，
                // 期间界面显示 "--"，但处理器/内存曲线照常
                Pdh.Close(q);
                _pdhStatus = "no-counters";
                return;
            }

            _query = q;
            _pdhOpen = true;
            _pdhFails = 0;
            _pdhStatus = "ok";
            Pdh.Collect(q);          // 速率类计数器要两次采集才有基线
        }

        // GPU 计数器只在显卡缺 NVML 指标时才用得上；单独一个查询，能开就开，开不了就永久记失败
        void EnsureGpuPdh()
        {
            if (_gpuPdhOpen || _gpuPdhFailed) return;
            IntPtr q;
            if (!Pdh.Open(out q)) { _gpuPdhFailed = true; return; }
            _cGpuUtil = Pdh.Add(q, PathGpuUtil);
            _cGpuMem = Pdh.Add(q, PathGpuMem);
            if (_cGpuUtil == IntPtr.Zero && _cGpuMem == IntPtr.Zero)
            {
                // Win10 1709 之前没有这组计数器：记下来，别再反复尝试
                Pdh.Close(q);
                _gpuPdhFailed = true;
                return;
            }
            _gpuQuery = q;
            _gpuPdhOpen = true;
            Pdh.Collect(q);
        }

        void CloseGpuPdh()
        {
            if (_gpuQuery != IntPtr.Zero) Pdh.Close(_gpuQuery);
            _gpuQuery = IntPtr.Zero;
            _gpuPdhOpen = false;
            _cGpuUtil = _cGpuMem = IntPtr.Zero;
        }

        void ClosePdh()
        {
            if (_query != IntPtr.Zero) Pdh.Close(_query);
            _query = IntPtr.Zero;
            _pdhOpen = false;
            _cDiskRead = _cDiskWrite = _cCommit = _cCpuPerf = _cCpuFreq = _cNetIn = _cNetOut = IntPtr.Zero;
            CloseGpuPdh();
        }

        public void Start()
        {
            _run = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "hud-sampler";
            _thread.Start();
        }

        public void Stop()
        {
            _run = false;
        }

        void Loop()
        {
            // 采样线程一旦抛异常就永久停帧，所以整轮都兜住；出错记日志，下一轮照常
            try
            {
                Native.FILETIME idle, kern, user;
                if (Native.GetSystemTimes(out idle, out kern, out user))
                {
                    _prevIdle = idle.Value;
                    _prevKernel = kern.Value;
                    _prevUser = user.Value;
                    _primed = true;
                }
            }
            catch { }

            Stopwatch sw = new Stopwatch();
            while (_run)
            {
                sw.Restart();
                try
                {
                    // 性能计数器打开得晚一点没关系；失败（计数器库被禁用等）就隔 15 秒重试
                    if (!_pdhOpen && unchecked(Environment.TickCount - _pdhNextTry) >= 0) InitPdh();
                    Nvml.Refresh();     // 显卡驱动晚就绪 / 驱动重启 / 热插拔

                    Snapshot s = Collect();
                    Action<Snapshot> cb = OnSample;
                    if (cb != null)
                    {
                        try { cb(s); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    HudWindow.LogLine("sampler: " + ex.GetType().Name + ": " + ex.Message);
                    try { Thread.Sleep(200); } catch { }
                }

                _gate++;
                if (_gate >= 240) { _gate = 0; try { GC.Collect(0); } catch { } }

                // 补足间隔，采集慢了也不让帧率漂移（原来固定 Sleep，采集耗时会被叠加进去）
                int spent = (int)sw.ElapsedMilliseconds;
                if (spent < IntervalMs) { try { Thread.Sleep(IntervalMs - spent); } catch { } }
            }
            ClosePdh();
        }

        Snapshot Collect()
        {
            Snapshot s = new Snapshot();

            Native.FILETIME idle, kern, user;
            if (_primed && Native.GetSystemTimes(out idle, out kern, out user))
            {
                ulong idleDelta = idle.Value - _prevIdle;
                ulong kernDelta = kern.Value - _prevKernel;
                ulong userDelta = user.Value - _prevUser;
                ulong total = kernDelta + userDelta;
                double busy = total > 0 ? 1.0 - (double)idleDelta / (double)total : 0.0;
                if (busy < 0) busy = 0;
                if (busy > 1) busy = 1;
                s.Cpu = busy * 100.0;
                _prevIdle = idle.Value;
                _prevKernel = kern.Value;
                _prevUser = user.Value;
            }
            else s.Cpu = double.NaN;

            Native.MEMORYSTATUSEX ms = new Native.MEMORYSTATUSEX();
            ms.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            if (Native.GlobalMemoryStatusEx(ref ms))
            {
                double totalGb = ms.ullTotalPhys / 1073741824.0;
                double freeGb = ms.ullAvailPhys / 1073741824.0;
                s.RamTotalGb = totalGb;
                s.RamUsedGb = totalGb - freeGb;
                s.RamPct = totalGb > 0 ? (totalGb - freeGb) / totalGb * 100.0 : 0;
            }
            else { s.RamTotalGb = s.RamUsedGb = s.RamPct = double.NaN; }

            s.CpuGhz = double.NaN;
            s.RamCommitGb = double.NaN;
            s.DiskRead = s.DiskWrite = s.NetIn = s.NetOut = double.NaN;

            // 显卡先取到（ReadPdh 里要往这块数据上补 PDH 的 GPU 计数器）
            s.Gpus = Nvml.Read();
            if (s.Gpus.Length == 0) s.Gpus = Dxgi.ReadStats();

            if (_pdhOpen) ReadPdh(s);

            s.UptimeSec = Native.GetTickCount64() / 1000.0;
            return s;
        }

        // 一次 PdhCollectQueryData 更新全部计数器；单条读失败只让那一项变 "--"
        void ReadPdh(Snapshot s)
        {
            if (!Pdh.Collect(_query))
            {
                _pdhFails++;
                if (_pdhFails >= 10)
                {
                    // 连续失败（计数器服务被禁用/重建）：关掉重开，隔 15 秒再试
                    ClosePdh();
                    _pdhStatus = "collect-fail";
                    _pdhNextTry = unchecked(Environment.TickCount + PdhRetryMs);
                }
                return;
            }
            _pdhFails = 0;

            double v;
            if (Pdh.TryGet(_cCommit, out v)) s.RamCommitGb = v / 1073741824.0;
            if (Pdh.TryGet(_cDiskRead, out v)) s.DiskRead = v;
            if (Pdh.TryGet(_cDiskWrite, out v)) s.DiskWrite = v;

            // 基准频率：注册表没有 ~MHz 的机器上，用 Processor Frequency 补
            if (CpuBaseMhz <= 0 && Pdh.TryGet(_cCpuFreq, out v) && v > 100 && v < 12000) CpuBaseMhz = v;

            double perf;
            if (CpuBaseMhz > 0 && Pdh.TryGet(_cCpuPerf, out perf) && perf > 0)
                s.CpuGhz = CpuBaseMhz * perf / 100.0 / 1000.0;

            string[] names;
            double[] vals;
            bool any = false;
            double ni = 0, no = 0;
            if (Pdh.TryGetArray(_cNetIn, out names, out vals))
            {
                any = true;
                for (int i = 0; i < vals.Length && i < names.Length; i++)
                    if (AcceptNet(names[i]) && !double.IsNaN(vals[i])) ni += vals[i];
            }
            if (Pdh.TryGetArray(_cNetOut, out names, out vals))
            {
                any = true;
                for (int i = 0; i < vals.Length && i < names.Length; i++)
                    if (AcceptNet(names[i]) && !double.IsNaN(vals[i])) no += vals[i];
            }
            if (any) { s.NetIn = ni; s.NetOut = no; }

            // 显卡指标兜底：NVML 拿不到（A 卡/核显/驱动没装）时，用 PDH 的 GPU 计数器补
            // 利用率与显存占用。Win10 1709 之前没有这组计数器，补不上就保持 "--"。
            // NVML 已经把指标给全时完全不碰这组计数器（GPU Engine 有几百个实例，枚举不便宜）
            if (s.Gpus != null && s.Gpus.Length > 0 && NeedsGpuFallback(s.Gpus))
            {
                EnsureGpuPdh();
                if (_gpuPdhOpen && Pdh.Collect(_gpuQuery))
                {
                    string[] gn;
                    double[] gv;
                    if (_cGpuUtil != IntPtr.Zero && Pdh.TryGetArray(_cGpuUtil, out gn, out gv))
                        Dxgi.ApplyUtil(s.Gpus, gn, gv);
                    if (_cGpuMem != IntPtr.Zero && Pdh.TryGetArray(_cGpuMem, out gn, out gv))
                        Dxgi.ApplyMem(s.Gpus, gn, gv);
                }
            }
        }

        // 只要有一块卡拿不到 NVML 利用率，就走 PDH 兜底（混装：N 卡 + A 卡/核显时两者都要照顾好）
        static bool NeedsGpuFallback(GpuStat[] gpus)
        {
            for (int i = 0; i < gpus.Length; i++)
                if (gpus[i] != null && !(gpus[i].NvmlMetrics && !float.IsNaN(gpus[i].Util))) return true;
            return false;
        }

        // 过滤掉虚拟/隧道网卡，只统计真实流量（和原来的过滤规则一致）
        static bool AcceptNet(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.IndexOf("isatap", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.IndexOf("Teredo", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.IndexOf("Loopback", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.IndexOf("Pseudo", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }
    }

    // ═══════════════════════════ 设置 ═══════════════════════════
    public sealed class Settings
    {
        public bool Topmost = true;
        public bool UseWindowsStartup = true;   // false = 用 Run 注册表键（桌面挂起时仍生效）
        public double OpacityLevel = 1.0;
        public int X = -999999;
        public int Y = -999999;
        public bool Autostart;
        public bool Minimal;                    // 极简模式：只显示文字，不画面板和曲线
        public bool ShowInTaskbar;              // 任务栏按钮；默认关，只留托盘图标

        // 配置目录：优先 %APPDATA%，不可写时依次退到 %LOCALAPPDATA%、exe 同目录。
        // 某些受管/受限用户配置下 ApplicationData 是不可写的，不能让设置整体失效。
        static string _dirCache;
        internal static string Dir
        {
            get
            {
                if (_dirCache != null) return _dirCache;
                string[] cands = new string[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HardwareHud"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HardwareHud"),
                    Path.Combine(ExeDir(), "HardwareHud")
                };
                for (int i = 0; i < cands.Length; i++)
                {
                    try
                    {
                        if (!Directory.Exists(cands[i])) Directory.CreateDirectory(cands[i]);
                        string probe = Path.Combine(cands[i], ".writable");
                        File.WriteAllText(probe, "1");
                        File.Delete(probe);
                        _dirCache = cands[i];
                        return _dirCache;
                    }
                    catch { }
                }
                _dirCache = cands[cands.Length - 1];
                return _dirCache;
            }
        }

        static string ExeDir()
        {
            try
            {
                Assembly a = Assembly.GetEntryAssembly();
                if (a != null && !string.IsNullOrEmpty(a.Location))
                    return Path.GetDirectoryName(a.Location);
            }
            catch { }
            try { return Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName); }
            catch { return Environment.CurrentDirectory; }
        }

        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        static string StartupFolder()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        }

        // 首选 .lnk 快捷方式：路径是 UTF-16 存的，中文/德文/日文路径都不会坏；
        // 老的 .vbs 写法用 ANSI 编码，非本地语言路径写出来就是乱码，只在极老系统上留作退路
        static string StartupLink { get { return Path.Combine(StartupFolder(), "HardwareHud.lnk"); } }
        static string StartupLegacyLink { get { return Path.Combine(StartupFolder(), "HardwareHud.vbs"); } }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                {
                    string[] lines = File.ReadAllLines(FilePath);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        // 不能用 Split('=')：值里带 '=' 的行会被整行丢掉。只切第一个等号。
                        int eq = lines[i].IndexOf('=');
                        if (eq <= 0) continue;
                        string k = lines[i].Substring(0, eq).Trim();
                        string v = lines[i].Substring(eq + 1).Trim();
                        if (k == "topmost") s.Topmost = v == "1";
                        else if (k == "opacity")
                        {
                            double d;
                            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0.5 && d <= 1.0)
                                s.OpacityLevel = d;
                        }
                        else if (k == "x")
                        {
                            int n;
                            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) s.X = n;
                        }
                        else if (k == "y")
                        {
                            int n;
                            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) s.Y = n;
                        }
                        else if (k == "minimal") s.Minimal = v == "1";
                        else if (k == "taskbar") s.ShowInTaskbar = v == "1";
                    }
                }
            }
            catch { }
            s.Autostart = File.Exists(StartupLink) || File.Exists(StartupLegacyLink) || RunKeyPresent();
            if (!File.Exists(StartupLink) && !File.Exists(StartupLegacyLink) && RunKeyPresent()) s.UseWindowsStartup = false;
            return s;
        }

        public void Save()
        {
            string[] lines = new string[]
            {
                "topmost=" + (Topmost ? "1" : "0"),
                "opacity=" + OpacityLevel.ToString("0.0", CultureInfo.InvariantCulture),
                "x=" + X.ToString(CultureInfo.InvariantCulture),
                "y=" + Y.ToString(CultureInfo.InvariantCulture),
                "minimal=" + (Minimal ? "1" : "0"),
                "taskbar=" + (ShowInTaskbar ? "1" : "0")
            };
            if (TryWrite(FilePath, lines)) return;

            // %APPDATA% 临时不可写（漫游配置锁、杀软拦截、磁盘只读）：换 %LOCALAPPDATA% 再试一次，
            // 成功就把后续日志/设置都指到那里
            try
            {
                string alt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HardwareHud");
                Directory.CreateDirectory(alt);
                string altFile = Path.Combine(alt, "settings.ini");
                if (TryWrite(altFile, lines))
                {
                    _dirCache = alt;
                    HudWindow.LogLine("settings: fallback to " + alt);
                }
            }
            catch { }
        }

        static bool TryWrite(string path, string[] lines)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, lines, new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        public void SetAutostart(bool on, string exePath)
        {
            if (!on)
            {
                try { if (File.Exists(StartupLink)) File.Delete(StartupLink); } catch { }
                try { if (File.Exists(StartupLegacyLink)) File.Delete(StartupLegacyLink); } catch { }
                ClearRunKey();
                Autostart = false;
                return;
            }

            // 首选 Startup 目录里的 .lnk：快捷方式里的路径是 UTF-16，中文/德文/日文路径都不会坏
            // （老的 .vbs 用 ANSI 写，非本地语言路径必乱码，且部分安全软件直接拦 .vbs 自启）
            if (UseWindowsStartup)
            {
                string err;
                if (HudWindow.TryCreateShortcut(StartupLink, exePath, out err))
                {
                    try { if (File.Exists(StartupLegacyLink)) File.Delete(StartupLegacyLink); } catch { }
                    ClearRunKey();
                    Autostart = true;
                    return;
                }
                UseWindowsStartup = false;
            }

            // 快捷方式建不出来（受管环境禁用 COM / 启动文件夹只读）才退到 Run 注册表键
            if (WriteRunKey(exePath))
            {
                try { if (File.Exists(StartupLegacyLink)) File.Delete(StartupLegacyLink); } catch { }
                Autostart = true;
            }
        }

        static bool WriteRunKey(string exePath)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return false;
                    k.SetValue("HardwareHud", "\"" + exePath + "\"", RegistryValueKind.String);
                    return true;
                }
            }
            catch { return false; }
        }

        static void ClearRunKey()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k != null) k.DeleteValue("HardwareHud", false);
                }
            }
            catch { }
        }

        static bool RunKeyPresent()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    object v = k.GetValue("HardwareHud");
                    return v != null && v.ToString().Trim().Length > 0;
                }
            }
            catch { return false; }
        }
    }


    // ═══════════════════════════ 视觉规范 ═══════════════════════════
    internal static class Look
    {
        public static readonly Color SlabTop = Color.FromArgb(255, 0x15, 0x1B, 0x1E);
        public static readonly Color SlabBottom = Color.FromArgb(255, 0x0A, 0x0E, 0x10);
        public static readonly Color Bezel = Color.FromArgb(255, 0x2A, 0x34, 0x38);
        public static readonly Color Well = Color.FromArgb(190, 0x05, 0x08, 0x09);
        public static readonly Color Ink = Color.FromArgb(255, 0xEC, 0xF1, 0xF3);
        public static readonly Color InkMid = Color.FromArgb(255, 0x9F, 0xAC, 0xB1);
        public static readonly Color InkDim = Color.FromArgb(255, 0x74, 0x82, 0x87);
        public static readonly Color InkFaint = Color.FromArgb(255, 0x4A, 0x55, 0x5A);
        public static readonly Color PenCpu = Color.FromArgb(255, 0xE3, 0xA5, 0x51);
        public static readonly Color PenGpu = Color.FromArgb(255, 0x52, 0xB4, 0xC4);
        public static readonly Color PenRam = Color.FromArgb(255, 0x95, 0xA9, 0xB0);
        public static readonly Color Alert = Color.FromArgb(255, 0xE6, 0x62, 0x4F);

        // 字体回退链：Bahnschrift 只有 Win10 1709+ 才有，老系统/精简系统退到雅黑、再退 Segoe UI；
        // 缺字形的语言环境（日/韩等）由 WPF 的字体链接机制再兜一层
        public static readonly FontFamily Face = new FontFamily("Bahnschrift SemiCondensed, Bahnschrift, Microsoft YaHei UI, Microsoft YaHei, Segoe UI");

        // 显卡不支持硬件渲染（远程桌面/虚拟机/无 WDDM 驱动的老机器，Tier 0/1）时，
        // 投影效果会走软件渲染且常画成黑块，此时整体关掉特效
        public static bool Effects = true;

        public static Brush Brush(Color c)
        {
            SolidColorBrush b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static Color Alpha(Color c, double a)
        {
            double k = Math.Max(0, Math.Min(1, a));
            return Color.FromArgb((byte)Math.Round(k * 255), c.R, c.G, c.B);
        }
    }

    // ═══════════════════════════ 画布 ═══════════════════════════
    public sealed class HudCanvas : FrameworkElement
    {
        public const double W = 348;
        const double Pad = 18;
        const int History = 112;

        const double HeaderTop = 13;
        const double RowsTop = 41;
        const double RowH = 86;
        const double BandH = 34;
        const double BandBaseline = 27;
        const double PlotTop = 36;
        const double PlotH = 32;
        const double DetailGap = 3;
        const double DetailH = 12;
        const double FooterH = 30;

        public double SlabHeight = 240;
        public Action ResetRequested;
        public string CpuTitle = "CPU";

        // ── 极简模式：只画文字，不画面板、网格、曲线 ──
        public const double MinW = 164;
        const double MinPadX = 11;
        const double MinPadY = 6;
        const double MinRowH = 21;
        const double MinLabelSize = 10;
        const double MinValueSize = 14;
        public bool Minimal;

        public double MinimalHeight { get { return MinPadY * 2 + _rows.Count * MinRowH; } }

        public int RowCount { get { return _rows.Count; } }
        public double RowHeight { get { return RowH; } }

        readonly List<Row> _rows = new List<Row>();
        readonly Dictionary<string, List<float>> _series = new Dictionary<string, List<float>>();
        Snapshot _snap;
        double _dpi = 1.0;

        public sealed class Row
        {
            public string Label;
            public string Pen;
            public Color Color;
            public int GpuIndex = -1;
            public bool IsGpu;
        }

        public HudCanvas()
        {
            SnapsToDevicePixels = true;
        }

        // 极简模式下画布只画几行文字，文字之间是空的。默认的命中测试只认"画过的像素"，
        // 点在空白处会直接穿到桌面上，右键菜单也就调不出来 —— 这里强制整块可命中。
        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        {
            return new PointHitTestResult(this, hitTestParameters.HitPoint);
        }

        public void BuildRows(int gpuCount, bool notify)
        {
            _rows.Clear();
            _series.Clear();

            AddRow("处理器", "cpu", Look.PenCpu, -1, false);
            if (gpuCount <= 0) AddRow("显卡", "gpu0", Look.PenGpu, -1, true);
            else
            {
                for (int i = 0; i < gpuCount; i++)
                {
                    string lbl = gpuCount == 1 ? "显卡" : "显卡 " + (i + 1).ToString(CultureInfo.InvariantCulture);
                    AddRow(lbl, "gpu" + i.ToString(CultureInfo.InvariantCulture), i == 0 ? Look.PenGpu : Look.PenCpu, i, true);
                }
            }
            AddRow("内存", "ram", Look.PenRam, -1, false);

            SlabHeight = RowsTop + _rows.Count * RowH + 8 + FooterH + 8;
            if (notify && ResetRequested != null) ResetRequested();
            InvalidateVisual();
        }

        void AddRow(string label, string pen, Color c, int gpuIndex, bool isGpu)
        {
            Row r = new Row();
            r.Label = label; r.Pen = pen; r.Color = c; r.GpuIndex = gpuIndex; r.IsGpu = isGpu;
            _rows.Add(r);
            _series[pen] = new List<float>(History);
        }

        public void Push(Snapshot s)
        {
            _snap = s;

            // 显卡数量变化要重建行；另外启动时没有卡（占位行 GpuIndex<0）而之后出现显卡时
            // 数量刚好相等，也得重建，否则那一行永远是 "--"
            int gpus = s.Gpus.Length;
            if (gpus != GpuRowCount() || (gpus > 0 && HasGpuPlaceholder())) BuildRows(gpus, true);

            Append("cpu", (float)s.Cpu);
            Append("ram", (float)s.RamPct);
            for (int i = 0; i < s.Gpus.Length; i++) Append("gpu" + i.ToString(CultureInfo.InvariantCulture), s.Gpus[i].Util);
            if (s.Gpus.Length == 0) Append("gpu0", float.NaN);

            InvalidateVisual();
        }

        bool HasGpuPlaceholder()
        {
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i].IsGpu && _rows[i].GpuIndex < 0) return true;
            return false;
        }

        int GpuRowCount()
        {
            int n = 0;
            for (int i = 0; i < _rows.Count; i++) if (_rows[i].IsGpu) n++;
            return n;
        }

        void Append(string key, float v)
        {
            List<float> list;
            if (!_series.TryGetValue(key, out list)) return;
            list.Add(v);
            while (list.Count > History) list.RemoveAt(0);
        }

        // ── 开发用：导出每行的精确排版框，便于在无法看图时校验 ──
        public void SaveLayout(string path)
        {
            try
            {
                StringBuilder sb = new StringBuilder();

                if (Minimal)
                {
                    sb.AppendLine("MODE=minimal canvas=" + F(MinW) + "x" + F(MinimalHeight) +
                                  " rows=" + _rows.Count + " rowH=" + F(MinRowH) +
                                  " padX=" + F(MinPadX) + " padY=" + F(MinPadY));
                    double right = MinW - MinPadX;
                    for (int i = 0; i < _rows.Count; i++)
                    {
                        Row r = _rows[i];
                        double y = MinPadY + i * MinRowH;
                        FormattedText lb = Text(MinLabel(r), MinLabelSize, FontWeights.Normal, Look.InkMid);
                        FormattedText vl = Text(MinValue(r), MinValueSize, FontWeights.SemiBold, Look.Ink);
                        double labelTop = y + (MinRowH - lb.Height) / 2;
                        double valueTop = y + (MinRowH - vl.Height) / 2;
                        double valueLeft = right - vl.WidthIncludingTrailingWhitespace;
                        sb.AppendLine("row[" + i + "] label=\"" + MinLabel(r) + "\" value=\"" + MinValue(r) + "\"");
                        sb.AppendLine("   label x=" + F(MinPadX) + " y=" + F(labelTop) + " w=" + F(lb.WidthIncludingTrailingWhitespace) + " h=" + F(lb.Height) +
                                      " right=" + F(MinPadX + lb.WidthIncludingTrailingWhitespace));
                        sb.AppendLine("   value x=" + F(valueLeft) + " y=" + F(valueTop) + " w=" + F(vl.WidthIncludingTrailingWhitespace) + " h=" + F(vl.Height) +
                                      " right=" + F(right));
                        sb.AppendLine("   CHECK label/value overlap = " + ((MinPadX + lb.WidthIncludingTrailingWhitespace > valueLeft) ? "YES <-- BAD" : "no"));
                        sb.AppendLine("   CHECK value right vs canvas(" + F(MinW) + ") = " + (right > MinW ? "OVERFLOW <-- BAD" : "ok"));
                        sb.AppendLine("   CHECK row bottom vs canvasH(" + F(MinimalHeight) + ") = " + ((valueTop + vl.Height) > MinimalHeight ? "OVERFLOW <-- BAD" : "ok"));
                    }
                    File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                    return;
                }

                sb.AppendLine("canvas=" + W + "x" + SlabHeight + " rows=" + _rows.Count + " rowH=" + RowH);
                sb.AppendLine("cores=" + CpuCores + " threads=" + CpuThreads + " model=" + CpuTitle);
                foreach (KeyValuePair<string, List<float>> kv in _series)
                {
                    List<float> L = kv.Value;
                    string rng = "";
                    if (L.Count > 0)
                    {
                        float mn = float.MaxValue, mx = float.MinValue;
                        int nan = 0;
                        for (int i = 0; i < L.Count; i++) { float v = L[i]; if (float.IsNaN(v) || float.IsInfinity(v)) { nan++; continue; } if (v < mn) mn = v; if (v > mx) mx = v; }
                        // 整段都是 NaN（例如显卡没指标）时不留 3.4e38 这种无意义的极值
                        rng = nan == L.Count ? " all-nan" : " min=" + F(mn) + " max=" + F(mx) + " nan=" + nan;
                    }
                    sb.AppendLine("series " + kv.Key + " count=" + L.Count + rng);
                }
                sb.AppendLine("pad=" + Pad + " rowsTop=" + RowsTop + " plotTop=" + PlotTop +
                              " plotH=" + PlotH + " detailGap=" + DetailGap);

                for (int i = 0; i < _rows.Count; i++)
                {
                    Row r = _rows[i];
                    double y = RowsTop + i * RowH;
                    double left = Pad - 1;
                    double right = W - Pad + 1;

                    FormattedText label = Text(r.Label, 10.5, FontWeights.Normal, Look.InkMid);
                    FormattedText unitTxt = Text("%", 11, FontWeights.Normal, Look.InkDim);
                    FormattedText numTxt = Text(RowNum(r), 23, FontWeights.SemiBold, Look.Ink);
                    string detail = RowDetail(r);
                    FormattedText d = null;
                    if (detail.Length > 0) { d = Text(detail, 10.5, FontWeights.Normal, Look.InkDim); d.MaxTextWidth = right - left; d.MaxLineCount = 1; }

                    double baseline = y + BandBaseline;
                    double unitRight = right;
                    double unitLeft = unitRight - unitTxt.WidthIncludingTrailingWhitespace;
                    double numRight = unitLeft - 3;
                    double numLeft = numRight - numTxt.WidthIncludingTrailingWhitespace;
                    double numTop = baseline - numTxt.Baseline;
                    double labelTop = baseline - label.Baseline;
                    double unitTop = baseline - unitTxt.Baseline;

                    Rect plot = new Rect(left, y + PlotTop, right - left, PlotH);
                    Rect labelBox = new Rect(left, labelTop, label.WidthIncludingTrailingWhitespace, label.Height);
                    Rect numBox = new Rect(numLeft, numTop, numTxt.WidthIncludingTrailingWhitespace, numTxt.Height);

                    // 显卡型号框：必须和标签、大数字都不重叠，且不被截成空
                    string model = GpuModel(r);
                    Rect modelBox = Rect.Empty;
                    double modelAvail = 0;
                    if (model.Length > 0)
                    {
                        double modelLeft = left + label.WidthIncludingTrailingWhitespace + 8;
                        modelAvail = numBox.X - 8 - modelLeft;
                        FormattedText mt = Text(model, 10.5, FontWeights.Normal, Look.InkDim);
                        double w = mt.WidthIncludingTrailingWhitespace;
                        modelBox = new Rect(modelLeft, baseline - mt.Baseline, Math.Min(w, Math.Max(0, modelAvail)), mt.Height);
                    }

                    sb.AppendLine("row[" + i + "] " + r.Label + " pen=" + r.Pen);
                    sb.AppendLine("   labelBox  x=" + F(labelBox.X) + " y=" + F(labelBox.Y) + " w=" + F(labelBox.Width) + " h=" + F(labelBox.Height) + " bottom=" + F(labelBox.Bottom));
                    if (model.Length > 0)
                        sb.AppendLine("   modelBox  x=" + F(modelBox.X) + " y=" + F(modelBox.Y) + " w=" + F(modelBox.Width) + " h=" + F(modelBox.Height) + " avail=" + F(modelAvail) + " text=\"" + model + "\"");
                    sb.AppendLine("   numBox    x=" + F(numBox.X) + " y=" + F(numBox.Y) + " w=" + F(numBox.Width) + " h=" + F(numBox.Height) + " right=" + F(numBox.Right) + " bottom=" + F(numBox.Bottom));
                    sb.AppendLine("   unitBox   right=" + F(unitRight) + " left=" + F(unitLeft) + " y=" + F(unitTop) + " h=" + F(unitTxt.Height) + " bottom=" + F(unitTop + unitTxt.Height));
                    sb.AppendLine("   baseline  y=" + F(baseline) + " labelBaseline=" + F(label.Baseline) + " numBaseline=" + F(numTxt.Baseline));
                    sb.AppendLine("   well      x=" + F(plot.X) + " y=" + F(plot.Y) + " w=" + F(plot.Width) + " h=" + F(plot.Height) + " bottom=" + F(plot.Bottom));
                    if (d != null)
                        sb.AppendLine("   detail    x=" + F(left) + " y=" + F(plot.Bottom + DetailGap) + " h=" + F(d.Height) + " bottom=" + F(plot.Bottom + DetailGap + d.Height));
                    sb.AppendLine("   CHECK label/num overlap = " + (labelBox.IntersectsWith(numBox) ? "YES <-- BAD" : "no"));
                    if (model.Length > 0)
                    {
                        sb.AppendLine("   CHECK model/label overlap = " + (modelBox.IntersectsWith(labelBox) ? "YES <-- BAD" : "no"));
                        sb.AppendLine("   CHECK model/num overlap   = " + (modelBox.IntersectsWith(numBox) ? "YES <-- BAD" : "no"));
                        sb.AppendLine("   CHECK model fits in avail = " + (modelAvail <= 30 ? "NO (hidden) <-- BAD" : "ok"));
                    }
                    sb.AppendLine("   CHECK num/well overlap   = " + (numBox.IntersectsWith(plot) ? "YES <-- BAD" : "no"));
                    sb.AppendLine("   CHECK label/well overlap = " + (labelBox.IntersectsWith(plot) ? "YES <-- BAD" : "no"));
                    if (d != null)
                    {
                        sb.AppendLine("   CHECK detail/well overlap= " + (new Rect(left, plot.Bottom + DetailGap, d.WidthIncludingTrailingWhitespace, d.Height).IntersectsWith(plot) ? "YES <-- BAD" : "no"));
                        sb.AppendLine("   CHECK detail bottom vs slabH(" + F(SlabHeight) + ") = " + (plot.Bottom + DetailGap + d.Height > SlabHeight ? "OVERFLOW <-- BAD" : "ok"));
                    }
                    sb.AppendLine("   CHECK rowBottom(" + F(y + RowH) + ") vs well bottom = " + (y + RowH < plot.Bottom ? "TIGHT <-- BAD" : "ok"));
                }

                // ── 文字宽度溢出检查（不受 MaxTextWidth 约束的真实宽度）──
                double avail = (W - Pad + 1) - (Pad - 1);
                sb.AppendLine("");
                sb.AppendLine("== 文字溢出检查 (可用宽度 " + F(avail) + ") ==");
                for (int i = 0; i < _rows.Count; i++)
                {
                    string d = RowDetail(_rows[i]);
                    if (d.Length == 0) continue;
                    FormattedText raw = Text(d, 10.5, FontWeights.Normal, Look.InkDim);
                    double wRaw = raw.WidthIncludingTrailingWhitespace;
                    sb.AppendLine("row[" + i + "] detail=\"" + d + "\" width=" + F(wRaw) +
                                  (wRaw > avail ? "  OVERFLOW by " + F(wRaw - avail) + " <-- BAD" : "  ok"));
                }
                if (_snap != null)
                {
                    FormattedText up = Text(UptimeText(), 10.5, FontWeights.Normal, Look.InkDim);
                    sb.AppendLine("header uptime=\"" + UptimeText() + "\" width=" + F(up.WidthIncludingTrailingWhitespace));
                    FormattedText ttl = Text(CpuTitle, 14, FontWeights.SemiBold, Look.Ink);
                    double ttlW = ttl.WidthIncludingTrailingWhitespace;
                    double upW = up.WidthIncludingTrailingWhitespace;
                    sb.AppendLine("header title=\"" + CpuTitle + "\" width=" + F(ttlW) +
                                  " titleRight+gap vs uptimeLeft=" + F((Pad - 1) + ttlW + 8) + " vs " + F((W - Pad + 1) - upW) +
                                  (((Pad - 1) + ttlW + 8 > (W - Pad + 1) - upW) ? "  COLLIDE <-- BAD" : "  ok"));

                    double diskW = Text("磁盘", 10.5, FontWeights.Normal, Look.InkFaint).WidthIncludingTrailingWhitespace;
                    double diskV = Text("读 " + FmtRate(_snap.DiskRead), 10.5, FontWeights.Normal, Look.InkMid).WidthIncludingTrailingWhitespace;
                    double diskV2 = Text("写 " + FmtRate(_snap.DiskWrite), 10.5, FontWeights.Normal, Look.InkMid).WidthIncludingTrailingWhitespace;
                    double diskEnd = (Pad - 1) + diskW + 6 + Math.Max(diskV, diskV2);
                    double nwV = Text("↓ " + FmtRate(_snap.NetIn), 10.5, FontWeights.Normal, Look.InkMid).WidthIncludingTrailingWhitespace;
                    double nwV2 = Text("↑ " + FmtRate(_snap.NetOut), 10.5, FontWeights.Normal, Look.InkMid).WidthIncludingTrailingWhitespace;
                    double nwK = Text("网络", 10.5, FontWeights.Normal, Look.InkFaint).WidthIncludingTrailingWhitespace;
                    double nwStart = (W - Pad + 1) - Math.Max(nwV, nwV2) - 6 - nwK;
                    sb.AppendLine("footer diskEnd=" + F(diskEnd) + " netStart=" + F(nwStart) +
                                  (diskEnd > nwStart ? "  COLLIDE <-- BAD" : "  ok"));
                }

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(path + ".error.txt", ex.ToString()); } catch { }
            }
        }

        static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        // ── 排版 ──
        FormattedText Text(string s, double size, FontWeight weight, Color color)
        {
            FormattedText ft = new FormattedText(
                s,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(Look.Face, FontStyles.Normal, weight, FontStretches.Normal),
                size, Look.Brush(color), _dpi);
            return ft;
        }

        void DrawRight(DrawingContext dc, FormattedText ft, double right, double top)
        {
            dc.DrawText(ft, new Point(right - ft.WidthIncludingTrailingWhitespace, top));
        }

        protected override void OnRender(DrawingContext dc)
        {
            _dpi = Screen.Scale(this);

            if (Minimal)
            {
                DrawMinimal(dc);
                return;
            }

            double h = SlabHeight;

            // 面板上沿的高光
            LinearGradientBrush topSheen = new LinearGradientBrush(
                Look.Alpha(Colors.White, 0.085), Look.Alpha(Colors.White, 0.0),
                new Point(0, 0), new Point(0, 1));
            topSheen.Freeze();
            dc.DrawRectangle(topSheen, null, new Rect(2, 1, W - 4, 30));

            // 头部：CPU 型号可能很长（"Xeon Silver 4214R"/"Ryzen 9 7950X3D"），
            // 留出右上角运行时间的宽度，超了就截断，别压到时间上
            FormattedText up = Text(UptimeText(), 10.5, FontWeights.Normal, Look.InkDim);
            FormattedText title = Text(CpuTitle, 14, FontWeights.SemiBold, Look.Ink);
            double titleAvail = (W - Pad + 1 - up.WidthIncludingTrailingWhitespace - 12) - (Pad - 1);
            if (titleAvail > 40 && title.WidthIncludingTrailingWhitespace > titleAvail)
            {
                title.MaxTextWidth = titleAvail;
                title.MaxLineCount = 1;
                title.Trimming = TextTrimming.CharacterEllipsis;
            }
            dc.DrawText(title, new Point(Pad - 1, HeaderTop));
            dc.DrawLine(new Pen(Look.Brush(Look.Alpha(Look.PenCpu, 0.55)), 1),
                new Point(Pad - 1, HeaderTop + title.Height + 3),
                new Point(Pad + title.WidthIncludingTrailingWhitespace, HeaderTop + title.Height + 3));

            DrawRight(dc, up, W - Pad + 1, HeaderTop + 4);

            // 行
            for (int i = 0; i < _rows.Count; i++)
                DrawRow(dc, _rows[i], RowsTop + i * RowH);

            // 底部
            DrawFooter(dc, RowsTop + _rows.Count * RowH + 9);

            // 底沿内阴影
            dc.DrawRectangle(Look.Brush(Look.Alpha(Colors.Black, 0.30)), null, new Rect(1, h - 2, W - 2, 1));
        }

        // ── 极简模式：只画文字 ──
        // 不画面板底色、边框、投影片、网格、曲线，也不画标题和页脚。
        // 用 1px 深色描边（八方向）保证白字压在白色/花哨壁纸上仍然清楚；
        // 不用模糊光晕，小字号会被糊掉。
        static readonly Color MinLabelColor = Color.FromArgb(255, 0xCF, 0xDA, 0xDD);
        static readonly Color MinOutline = Color.FromArgb(255, 0x06, 0x09, 0x0A);
        static readonly double[,] MinOutlineOffsets = new double[,] {
            { -1, 0 }, { 1, 0 }, { 0, -1 }, { 0, 1 },
            { -1, -1 }, { 1, -1 }, { -1, 1 }, { 1, 1 }
        };

        void DrawMinimal(DrawingContext dc)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                double y = MinPadY + i * MinRowH;
                double right = MinW - MinPadX;

                double v = RowValue(r);
                bool hot = !double.IsNaN(v) && !double.IsInfinity(v) && v >= 90;

                string labelText = MinLabel(r);
                string valueText = MinValue(r);

                FormattedText label = Text(labelText, MinLabelSize, FontWeights.Normal, MinLabelColor);
                FormattedText value = Text(valueText, MinValueSize, FontWeights.Bold, hot ? Look.Alert : Look.Ink);

                DrawOutline(dc, labelText, MinLabelSize, FontWeights.Normal, MinPadX, y + (MinRowH - label.Height) / 2);
                dc.DrawText(label, new Point(MinPadX, y + (MinRowH - label.Height) / 2));

                double valueLeft = right - value.WidthIncludingTrailingWhitespace;
                double valueTop = y + (MinRowH - value.Height) / 2;
                DrawOutline(dc, valueText, MinValueSize, FontWeights.Bold, valueLeft, valueTop);
                dc.DrawText(value, new Point(valueLeft, valueTop));
            }
        }

        void DrawOutline(DrawingContext dc, string s, double size, FontWeight weight, double x, double y)
        {
            FormattedText dark = Text(s, size, weight, MinOutline);
            for (int k = 0; k < 8; k++)
                dc.DrawText(dark, new Point(x + MinOutlineOffsets[k, 0], y + MinOutlineOffsets[k, 1]));
        }

        static string MinLabel(Row r)
        {
            if (r.IsGpu) return r.GpuIndex > 0 ? "GPU" + (r.GpuIndex + 1).ToString(CultureInfo.InvariantCulture) : "GPU";
            if (r.Pen == "ram") return "RAM";
            return "CPU";
        }

        // 极简值：CPU/内存 = 百分比；显卡 = 百分比（能读到温度时再跟一个温度）
        string MinValue(Row r)
        {
            double v = RowValue(r);
            string pct = (double.IsNaN(v) || double.IsInfinity(v))
                ? "--"
                : Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + "%";

            if (r.IsGpu && _snap != null && r.GpuIndex >= 0 && r.GpuIndex < _snap.Gpus.Length)
            {
                float t = _snap.Gpus[r.GpuIndex].TempC;
                if (!float.IsNaN(t)) pct += "   " + Math.Round(t).ToString("0", CultureInfo.InvariantCulture) + "°";
            }
            return pct;
        }

        // 托盘悬浮提示用的一行摘要：硬件监测   CPU 26%   GPU 32%   RAM 93%
        public string TrayText()
        {
            if (_snap == null || _rows.Count == 0) return "硬件监测";
            StringBuilder sb = new StringBuilder("硬件监测");
            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                double v = RowValue(r);
                sb.Append("   ").Append(MinLabel(r)).Append(' ');
                sb.Append((double.IsNaN(v) || double.IsInfinity(v))
                    ? "--"
                    : Math.Round(v).ToString("0", CultureInfo.InvariantCulture) + "%");
            }
            return sb.ToString();
        }

        void DrawRow(DrawingContext dc, Row r, double y)
        {
            double left = Pad - 1;
            double right = W - Pad + 1;

            // 标签、大数字、单位共用一条基线，避免数字越出本行撞到上一行说明
            double baseline = y + BandBaseline;
            double value = RowValue(r);
            bool hot = !double.IsNaN(value) && value >= 90;

            // 先算右边的大数字，才知道左边能留多宽给型号
            FormattedText unitTxt = Text("%", 11, FontWeights.Normal, Look.InkDim);
            DrawRight(dc, unitTxt, right, baseline - unitTxt.Baseline);
            double unitLeft = right - unitTxt.WidthIncludingTrailingWhitespace;

            FormattedText numTxt = Text(RowNum(r), 23, FontWeights.SemiBold, hot ? Look.Alert : Look.Ink);
            DrawRight(dc, numTxt, unitLeft - 3, baseline - numTxt.Baseline);

            FormattedText label = Text(r.Label, 10.5, FontWeights.Normal, Look.InkMid);
            dc.DrawText(label, new Point(left, baseline - label.Baseline));

            // 显卡型号跟在「显卡」后面：驱动读不到指标时，型号至少是对的
            string model = GpuModel(r);
            if (model.Length > 0)
            {
                double modelLeft = left + label.WidthIncludingTrailingWhitespace + 8;
                double avail = unitLeft - numTxt.WidthIncludingTrailingWhitespace - 3 - modelLeft - 8;
                if (avail > 30)
                {
                    FormattedText mt = Text(model, 10.5, FontWeights.Normal, Look.InkDim);
                    mt.MaxTextWidth = avail;
                    mt.MaxLineCount = 1;
                    mt.Trimming = TextTrimming.CharacterEllipsis;
                    dc.DrawText(mt, new Point(modelLeft, baseline - mt.Baseline));
                }
            }

            Rect plot = new Rect(left, y + PlotTop, right - left, PlotH);
            DrawWell(dc, plot, r);

            string detail = RowDetail(r);
            if (detail.Length > 0)
            {
                FormattedText d = Text(detail, 10.5, FontWeights.Normal, Look.InkDim);
                d.MaxTextWidth = right - left;
                d.MaxLineCount = 1;
                dc.DrawText(d, new Point(left, plot.Bottom + DetailGap));
            }
        }

        void DrawWell(DrawingContext dc, Rect r, Row row)
        {
            dc.DrawRoundedRectangle(Look.Brush(Look.Well), null, r, 4, 4);

            LinearGradientBrush lip = new LinearGradientBrush(
                Look.Alpha(Colors.Black, 0.50), Look.Alpha(Colors.White, 0.045),
                new Point(0, 0), new Point(0, 1));
            lip.Freeze();
            dc.DrawRoundedRectangle(lip, null, new Rect(r.X, r.Y, r.Width, 2), 2, 2);

            Brush grid = Look.Brush(Look.Alpha(Colors.White, 0.05));
            for (int i = 1; i <= 3; i++)
            {
                double gy = Math.Round(r.Y + r.Height * i / 4.0) + 0.5;
                dc.DrawLine(new Pen(grid, 1), new Point(r.X + 1, gy), new Point(r.Right - 1, gy));
            }

            dc.DrawLine(new Pen(Look.Brush(Look.Alpha(Colors.White, 0.09)), 1),
                new Point(r.Right - 0.5, r.Y + 1), new Point(r.Right - 0.5, r.Bottom - 1));

            List<float> list;
            if (!_series.TryGetValue(row.Pen, out list) || list == null || list.Count < 2) return;

            dc.PushClip(new RectangleGeometry(r, 4, 4));

            double x0 = r.X + 1, x1 = r.Right - 2;
            double span = History - 1;
            double offset = span - (list.Count - 1);
            double usable = r.Height - 3;

            List<Point> pts = new List<Point>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                float v = list[i];
                double px = x0 + (offset + i) / span * (x1 - x0);
                double py;
                if (float.IsNaN(v) || float.IsInfinity(v))
                    py = r.Bottom - 1;
                else
                {
                    double k = Math.Max(0, Math.Min(1, v / 100.0));
                    py = r.Bottom - 1.5 - k * usable;
                }
                pts.Add(new Point(px, py));
            }

            PathFigure fillFigure = new PathFigure();
            fillFigure.StartPoint = new Point(pts[0].X, r.Bottom - 0.5);
            fillFigure.IsClosed = true;
            PolyLineSegment fillSeg = new PolyLineSegment();
            for (int i = 0; i < pts.Count; i++) fillSeg.Points.Add(pts[i]);
            fillSeg.Points.Add(new Point(pts[pts.Count - 1].X, r.Bottom - 0.5));
            fillFigure.Segments.Add(fillSeg);
            PathGeometry fill = new PathGeometry();
            fill.Figures.Add(fillFigure);
            fill.Freeze();
            LinearGradientBrush fb = new LinearGradientBrush(
                Look.Alpha(row.Color, 0.26), Look.Alpha(row.Color, 0.0), new Point(0, 0), new Point(0, 1));
            fb.Freeze();
            dc.DrawGeometry(fb, null, fill);

            PathFigure lineFigure = new PathFigure();
            lineFigure.StartPoint = pts[0];
            PolyLineSegment lineSeg = new PolyLineSegment();
            for (int i = 1; i < pts.Count; i++) lineSeg.Points.Add(pts[i]);
            lineFigure.Segments.Add(lineSeg);
            PathGeometry line = new PathGeometry();
            line.Figures.Add(lineFigure);
            line.Freeze();
            dc.DrawGeometry(null, new Pen(Look.Brush(Look.Alpha(row.Color, 0.20)), 3.4), line);
            dc.DrawGeometry(null, new Pen(Look.Brush(row.Color), 1.25), line);

            dc.DrawEllipse(Look.Brush(Look.Alpha(row.Color, 0.95)), null, pts[pts.Count - 1], 2.1, 2.1);

            dc.Pop();
        }

        double RowValue(Row r)
        {
            if (_snap == null) return double.NaN;
            if (r.IsGpu)
            {
                if (r.GpuIndex < 0 || r.GpuIndex >= _snap.Gpus.Length) return double.NaN;
                return _snap.Gpus[r.GpuIndex].Util;
            }
            if (r.Pen == "ram") return _snap.RamPct;
            return _snap.Cpu;
        }

        string RowNum(Row r)
        {
            double v = RowValue(r);
            if (double.IsNaN(v) || double.IsInfinity(v)) return "--";
            return Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
        }

        string RowDetail(Row r)
        {
            if (r.IsGpu) return GpuDetail(r);
            if (r.Pen == "ram") return RamDetail();
            return CpuDetail();
        }

        string CpuDetail()
        {
            if (_snap == null) return "";
            string s = "";
            if (!double.IsNaN(_snap.CpuGhz))
                s = _snap.CpuGhz.ToString("0.00", CultureInfo.InvariantCulture) + " GHz";
            string threads = CpuCores > 0
                ? CpuCores.ToString(CultureInfo.InvariantCulture) + " 核 " + CpuThreads.ToString(CultureInfo.InvariantCulture) + " 线程"
                : CpuThreads.ToString(CultureInfo.InvariantCulture) + " 线程";
            return s.Length > 0 ? s + "    " + threads : threads;
        }

        string RamDetail()
        {
            if (_snap == null || double.IsNaN(_snap.RamTotalGb)) return "";
            string s = _snap.RamUsedGb.ToString("0.0", CultureInfo.InvariantCulture) + " / " +
                       _snap.RamTotalGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            if (!double.IsNaN(_snap.RamCommitGb))
                s += "    已提交 " + _snap.RamCommitGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            return s;
        }

        string GpuModel(Row r)
        {
            if (!r.IsGpu || _snap == null) return "";
            if (r.GpuIndex < 0 || r.GpuIndex >= _snap.Gpus.Length) return "";
            string n = _snap.Gpus[r.GpuIndex].Name;
            return n != null ? n.Trim() : "";
        }

        static void AddPart(StringBuilder sb, string text)
        {
            if (text == null || text.Length == 0) return;
            if (sb.Length > 0) sb.Append("    ");
            sb.Append(text);
        }

        string GpuDetail(Row r)
        {
            if (_snap == null) return "";
            if (r.GpuIndex < 0 || r.GpuIndex >= _snap.Gpus.Length)
                return "未检测到显卡";

            GpuStat g = _snap.Gpus[r.GpuIndex];
            StringBuilder sb = new StringBuilder();

            if (!float.IsNaN(g.MemUsedMb) && g.MemTotalMb > 0)
                AddPart(sb, (g.MemUsedMb / 1024f).ToString("0.0", CultureInfo.InvariantCulture) + " / " +
                            (g.MemTotalMb / 1024f).ToString("0.0", CultureInfo.InvariantCulture) + " GB");
            else if (!float.IsNaN(g.MemTotalMb) && g.MemTotalMb > 0)
                AddPart(sb, "显存 " + (g.MemTotalMb / 1024f).ToString("0.#", CultureInfo.InvariantCulture) + " GB");

            if (!float.IsNaN(g.TempC))
                AddPart(sb, Math.Round(g.TempC).ToString(CultureInfo.InvariantCulture) + "°C");

            // 只有 NVML 才给温度/功耗/频率/风扇；A 卡靠 PDH 补的利用率和显存没有这几项
            if (g.NvmlMetrics)
            {
                AddPart(sb, !float.IsNaN(g.PowerW) && g.PowerW > 1
                    ? Math.Round(g.PowerW).ToString(CultureInfo.InvariantCulture) + " W"
                    : "功耗 N/A");
                if (!float.IsNaN(g.ClockMhz) && g.ClockMhz > 0)
                    AddPart(sb, Math.Round(g.ClockMhz).ToString(CultureInfo.InvariantCulture) + " MHz");
                if (!float.IsNaN(g.FanPct))
                    AddPart(sb, "风扇 " + Math.Round(g.FanPct).ToString(CultureInfo.InvariantCulture) + "%");
            }

            if (sb.Length == 0) return "实时指标需 NVIDIA 驱动";
            return sb.ToString();
        }

        void DrawFooter(DrawingContext dc, double y)
        {
            double left = Pad - 1;
            double right = W - Pad + 1;
            double rowH = 14;

            if (_snap == null)
            {
                dc.DrawText(Text("正在读取硬件…", 10.5, FontWeights.Normal, Look.InkFaint), new Point(left, y));
                return;
            }

            // 磁盘
            DrawKeyValue(dc, "磁盘", "读 " + FmtRate(_snap.DiskRead), left, y);
            DrawKeyValue(dc, "", "写 " + FmtRate(_snap.DiskWrite), left, y + rowH);

            // 网络
            DrawKeyValueRight(dc, "网络", "↓ " + FmtRate(_snap.NetIn), right, y);
            DrawKeyValueRight(dc, "", "↑ " + FmtRate(_snap.NetOut), right, y + rowH);
        }

        void DrawKeyValue(DrawingContext dc, string key, string value, double x, double y)
        {
            FormattedText k = Text(key, 10.5, FontWeights.Normal, Look.InkFaint);
            dc.DrawText(k, new Point(x, y));
            FormattedText v = Text(value, 10.5, FontWeights.Normal, Look.InkMid);
            dc.DrawText(v, new Point(x + k.WidthIncludingTrailingWhitespace + 6, y));
        }

        void DrawKeyValueRight(DrawingContext dc, string key, string value, double right, double y)
        {
            FormattedText v = Text(value, 10.5, FontWeights.Normal, Look.InkMid);
            DrawRight(dc, v, right, y);
            double vLeft = right - v.WidthIncludingTrailingWhitespace;
            FormattedText k = Text(key, 10.5, FontWeights.Normal, Look.InkFaint);
            DrawRight(dc, k, vLeft - 6, y);
        }

        static string FmtRate(double bytes)
        {
            if (double.IsNaN(bytes) || bytes < 0) return "--";
            if (bytes >= 1048576) return (bytes / 1048576).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
            if (bytes >= 1024) return (bytes / 1024).ToString("0", CultureInfo.InvariantCulture) + " KB/s";
            return "0 B/s";
        }

        string UptimeText()
        {
            if (_snap == null) return "启动中";
            long total = (long)_snap.UptimeSec;
            long d = total / 86400, hh = (total % 86400) / 3600, mm = (total % 3600) / 60;
            if (d > 0) return "已运行 " + d.ToString(CultureInfo.InvariantCulture) + " 天 " + hh.ToString("00") + " 小时";
            return "已运行 " + hh.ToString("00") + ":" + mm.ToString("00");
        }

        int CpuCores = 1;
        int CpuThreads = Environment.ProcessorCount;

        public void SetCpuInfo(string model, int cores, int threads)
        {
            CpuTitle = model;
            CpuCores = cores;
            CpuThreads = threads;
        }
    }

    // ═══════════════════════════ 窗口 ═══════════════════════════
    public sealed class HudWindow : Window
    {
        const double ShadowPad = 26;

        const int WM_TRAY = 0x8001;         // 托盘回调消息
        const int WM_HOTKEY = 0x0312;
        const int WM_SYSCOMMAND = 0x0112;
        const int HOTKEY_TOGGLE = 0xB0B1;   // Ctrl+Alt+H 切换极简模式
        const uint MOD_ALT = 0x0001;
        const uint MOD_CONTROL = 0x0002;
        const uint VK_H = 0x48;

        // 系统菜单命令号（任务栏右键 / Alt+Space 里那几项）
        const int MENU_BASE = 0xE100;
        const int MENU_STEP = 0x10;

        readonly Settings _settings;
        readonly HudCanvas _canvas;
        readonly Border _slab;
        readonly Border _host;
        readonly Sampler _sampler;
        Popup _menu;

        HwndSource _src;
        IntPtr _hwnd;
        IntPtr _trayIcon;
        bool _trayAdded;
        bool _hotkeyAdded;
        string _lastTip = "";
        string _trayStatus = "none";
        string _hotkeyStatus = "none";
        string _sysMenuStatus = "none";
        DispatcherTimer _trayRetry;
        int _trayTries;
        EventWaitHandle _wake;
        bool _startLogged;
        bool _taskbarFallback;   // 本次运行因托盘失败临时打开任务栏按钮

        // 第二个实例启动时用它叫醒已经在跑的那个
        public const string WakeEventName = @"Local\HardwareHud.Wake";

        // Explorer（重新）启动时广播的消息，收到就重建托盘图标
        static readonly uint WM_TASKBARCREATED = Native.RegisterWindowMessage("TaskbarCreated");

        public HudWindow(Settings settings, bool minimal)
        {
            _settings = settings;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = settings.ShowInTaskbar;
            ShowActivated = false;
            Topmost = settings.Topmost;
            Opacity = settings.OpacityLevel;
            Title = "HardwareHud";
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                {
                    System.Windows.Media.Imaging.IconBitmapDecoder dec =
                        new System.Windows.Media.Imaging.IconBitmapDecoder(
                            new Uri("file:///" + exe.Replace("\\", "/")),
                            System.Windows.Media.Imaging.BitmapCreateOptions.None,
                            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    if (dec.Frames != null && dec.Frames.Count > 0)
                        Icon = dec.Frames[dec.Frames.Count - 1];
                }
            }
            catch { }

            _canvas = new HudCanvas();
            _canvas.Minimal = minimal;
            _canvas.ResetRequested = ApplySize;

            LinearGradientBrush bg = new LinearGradientBrush(Look.SlabTop, Look.SlabBottom,
                new Point(0, 0), new Point(0, 1));
            bg.Freeze();

            _slab = new Border();
            _slab.Background = bg;
            _slab.CornerRadius = new CornerRadius(13);
            _slab.BorderBrush = Look.Brush(Look.Bezel);
            _slab.BorderThickness = new Thickness(1);
            DropShadowEffect fx = new DropShadowEffect();
            fx.Color = Colors.Black;
            fx.BlurRadius = 26;
            fx.ShadowDepth = 7;
            fx.Direction = 270;
            fx.Opacity = 0.62;
            _slab.Effect = fx;

            Grid g = new Grid();
            g.Children.Add(_slab);
            g.Children.Add(_canvas);

            _canvas.HorizontalAlignment = HorizontalAlignment.Left;
            _canvas.VerticalAlignment = VerticalAlignment.Top;

            Border host = new Border();
            host.Padding = new Thickness(ShadowPad);
            host.Background = Brushes.Transparent;
            host.Child = g;
            Content = host;
            _host = host;

            _sampler = new Sampler();
            _canvas.SetCpuInfo(_sampler.CpuModel, _sampler.CpuCores, _sampler.CpuThreads);
            _sampler.OnSample = OnSample;

            ApplySize();

            if (_settings.X != -999999 && _settings.Y != -999999)
            {
                Left = _settings.X;
                Top = _settings.Y;
            }
            else
            {
                Left = SystemParameters.WorkArea.Right - Width - 18;
                Top = SystemParameters.WorkArea.Top + 18;
            }

            MouseLeftButtonDown += OnDragStart;
            MouseRightButtonUp += OnMenuRequest;
            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        void ApplySize()
        {
            if (_canvas.Minimal)
            {
                // 极简模式：面板整体不参与布局，窗口缩到文字块大小
                _slab.Visibility = Visibility.Collapsed;
                _host.Padding = new Thickness(0);

                Width = HudCanvas.MinW;
                Height = _canvas.MinimalHeight;
                _canvas.Width = HudCanvas.MinW;
                _canvas.Height = _canvas.MinimalHeight;
                RefreshChrome();
                return;
            }

            _slab.Visibility = Visibility.Visible;
            _host.Padding = new Thickness(ShadowPad);

            Width = HudCanvas.W + ShadowPad * 2;
            Height = _canvas.SlabHeight + ShadowPad * 2;
            _canvas.Width = HudCanvas.W;
            _canvas.Height = _canvas.SlabHeight;

            RefreshChrome();
        }

        public void Start()
        {
            _sampler.Start();
        }

        // 切换极简模式（右键菜单和 --toggle 共用同一段逻辑）
        public void ToggleMinimal()
        {
            bool next = !_canvas.Minimal;
            _canvas.Minimal = next;
            _settings.Minimal = next;

            // 宽度从 400 缩到 158 时以右边缘为锚，否则会从右上角"跳走"
            double rightEdge = Left + Width;
            ApplySize();
            Left = rightEdge - Width;
            KeepOnScreen();

            _settings.Save();
            _canvas.InvalidateVisual();
            BuildSystemMenu();
        }

        // ── 开发用：把面板本身渲染成 PNG（不经过屏幕抓取，尺寸精确）──
        public void SaveSnapshot(string path, double scale)
        {
            try
            {
                _canvas.UpdateLayout();
                int w = (int)Math.Ceiling(_canvas.ActualWidth > 0 ? _canvas.ActualWidth : HudCanvas.W);
                int h = (int)Math.Ceiling(_canvas.ActualHeight > 0 ? _canvas.ActualHeight : _canvas.SlabHeight);

                RenderTargetBitmap rtb = new RenderTargetBitmap(
                    (int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale),
                    96 * scale, 96 * scale, PixelFormats.Pbgra32);

                // 先把面板底色画进一张背景图，避免透明区在 PNG 里变成黑。
                // 极简模式本来就没有底色，这里铺一层中性灰只是为了像素自检时能测到文字。
                DrawingVisual dv = new DrawingVisual();
                using (DrawingContext dc = dv.RenderOpen())
                {
                    if (_canvas.Minimal)
                    {
                        dc.DrawRectangle(Look.Brush(Color.FromRgb(0x3A, 0x3A, 0x3A)), null, new Rect(0, 0, w, h));
                    }
                    else
                    {
                        LinearGradientBrush bg = new LinearGradientBrush(Look.SlabTop, Look.SlabBottom,
                            new Point(0, 0), new Point(0, 1));
                        bg.Freeze();
                        dc.DrawRoundedRectangle(bg, new Pen(Look.Brush(Look.Bezel), 1),
                            new Rect(0.5, 0.5, w - 1, h - 1), 13, 13);
                    }
                }

                rtb.Render(dv);
                rtb.Render(_canvas);

                PngBitmapEncoder enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                    enc.Save(fs);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(path + ".error.txt", ex.ToString()); } catch { }
            }
        }

        public void SaveLayout(string path)
        {
            _canvas.SaveLayout(path);
        }

        public void SaveGeometry(string path)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("canvasW=" + _canvas.ActualWidth.ToString("0.##", CultureInfo.InvariantCulture));
                sb.AppendLine("canvasH=" + _canvas.ActualHeight.ToString("0.##", CultureInfo.InvariantCulture));
                sb.AppendLine("slabH=" + _canvas.SlabHeight.ToString("0.##", CultureInfo.InvariantCulture));
                sb.AppendLine("window=" + Left.ToString("0.##", CultureInfo.InvariantCulture) + "," +
                              Top.ToString("0.##", CultureInfo.InvariantCulture) + " " +
                              Width.ToString("0.##", CultureInfo.InvariantCulture) + "x" +
                              Height.ToString("0.##", CultureInfo.InvariantCulture));
                sb.AppendLine("rows=" + _canvas.RowCount);
                sb.AppendLine("dpi=" + Screen.Scale(_canvas).ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("rowH=" + _canvas.RowHeight.ToString("0.##", CultureInfo.InvariantCulture));
                sb.AppendLine("tray=" + _trayStatus);
                sb.AppendLine("hotkey=" + _hotkeyStatus);
                sb.AppendLine("sysmenu=" + _sysMenuStatus);
                sb.AppendLine("taskbar=" + (ShowInTaskbar ? "on" : "off"));
                sb.AppendLine("tip=" + _canvas.TrayText());
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 硬件渲染层级：Tier 0/1 走软件渲染（远程桌面、虚拟机、老显卡/驱动）。
            // 这时投影特效会拖慢绘制、甚至被画成黑块，直接关掉，只保留纯色卡片。
            int tier = 0;
            try { tier = (RenderCapability.Tier >> 16) & 0xF; } catch { }
            if (tier < 2)
            {
                Look.Effects = false;
                try { if (_slab != null) _slab.Effect = null; } catch { }
            }
            LogLine("render tier=" + tier.ToString(CultureInfo.InvariantCulture));

            _hwnd = new WindowInteropHelper(this).Handle;

            _src = HwndSource.FromHwnd(_hwnd);
            if (_src != null) _src.AddHook(WndProc);

            _hotkeyAdded = Native.RegisterHotKey(_hwnd, HOTKEY_TOGGLE, MOD_CONTROL | MOD_ALT, VK_H);
            _hotkeyStatus = _hotkeyAdded ? "ok" : ("fail(err=" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ")");

            AddTrayIcon();

            _canvas.BuildRows(Nvml.Count, false);
            ApplySize();
            KeepOnScreen();

            // 样式必须放在最后：WPF 在布局/显示过程中会重写窗口样式，早设会被覆盖
            RefreshChrome();

            ListenForSecondInstance();
            // start 那一行挪到第一帧数据之后（见 OnSample）：那时 pdh= / gpu= 才是真实状态
        }

        // 双击快捷方式（第二个实例）时把已经在跑的这个叫到前面来，并直接弹出菜单 ——
        // 极简模式下窗口只有一小块文字，这是「找不到它」时的救命入口
        void ListenForSecondInstance()
        {
            try
            {
                _wake = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
                ThreadPool.RegisterWaitForSingleObject(_wake, delegate
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        try
                        {
                            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                            Native.SetForegroundWindow(_hwnd);
                            Activate();
                            ShowMenu();
                            LogLine("wake: second instance requested the menu");
                        }
                        catch { }
                    }));
                }, null, Timeout.Infinite, false);
            }
            catch { }
        }

        // 一行诊断文本：托盘 / 热键 / 系统菜单 / 任务栏 / 版本，出错时用户可以直接截图给我
        public string Diagnostics()
        {
            string stamp;
            try { stamp = File.GetLastWriteTime(ExePath()).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
            catch { stamp = "?"; }
            return "exe=" + ExePath() +
                   " build=" + stamp +
                   " tray=" + _trayStatus +
                   " hotkey=" + _hotkeyStatus +
                   " sysmenu=" + _sysMenuStatus +
                   " taskbar=" + (ShowInTaskbar ? "on" : "off") +
                   " minimal=" + (_canvas != null && _canvas.Minimal ? "1" : "0") +
                   " pdh=" + (_sampler != null ? _sampler.PdhStatus : "?") +
                   " gpu=" + (Nvml.Available ? Nvml.Count.ToString(CultureInfo.InvariantCulture) : "none") +
                   " pos=" + ((int)Left).ToString(CultureInfo.InvariantCulture) + "," + ((int)Top).ToString(CultureInfo.InvariantCulture) +
                   " log=" + LogPath();
        }

        public static string LogPath()
        {
            try { return Path.Combine(Settings.Dir, "HardwareHud.log"); }
            catch { return "(none)"; }
        }

        public static void LogLine(string text)
        {
            try
            {
                string path = LogPath();
                FileInfo fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 64 * 1024) fi.Delete();
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text + "\r\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        // 任务栏按钮的实际状态：用户的选择，或本次运行因托盘失败被强制打开
        bool TaskbarVisible
        {
            get { return _settings.ShowInTaskbar || _taskbarFallback; }
        }

        // 任务栏按钮 + 系统菜单：无边框全透明窗口这两样都得手动补
        void RefreshChrome()
        {
            if (_hwnd == IntPtr.Zero) return;
            // 只在托盘里显示时，必须主动打成 TOOLWINDOW，否则 WPF 后续的样式重设
            // 会把窗口重新加回任务栏与 Alt+Tab
            if (TaskbarVisible) Native.ForceAppWindow(_hwnd);
            else Native.ForceToolWindow(_hwnd);
            Native.EnsureSystemMenu(_hwnd);
            BuildSystemMenu();
        }

        // ── 托盘图标：即使缩成极简模式、或被最小化，这里也永远能打开配置菜单 ──
        void AddTrayIcon()
        {
            if (_trayAdded) return;
            try
            {
                IntPtr large, small;
                Native.ExtractIconEx(ExePath(), 0, out large, out small, 1);
                IntPtr use = small != IntPtr.Zero ? small : large;
                // 自身 exe 没取到图标时退到系统默认图标，保证托盘里一定有个东西可点
                if (use == IntPtr.Zero) use = Native.LoadIcon(IntPtr.Zero, Native.IDI_APPLICATION);
                _trayIcon = use;
                if (use == IntPtr.Zero)
                {
                    _trayStatus = "no-icon";
                    ScheduleTrayRetry();
                    return;
                }

                Native.NOTIFYICONDATA nid = NewTrayData();
                nid.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP;
                nid.hIcon = use;
                nid.szTip = "硬件监测";
                _trayAdded = Native.Shell_NotifyIcon(Native.NIM_ADD, ref nid);
                _trayStatus = _trayAdded ? "ok" : ("fail(size=" + nid.cbSize.ToString(CultureInfo.InvariantCulture) +
                                                  ",err=" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) +
                                                  ",hwnd=" + _hwnd.ToInt64().ToString(CultureInfo.InvariantCulture) + ")");
                if (!_trayAdded) ScheduleTrayRetry();
                else
                {
                    // 第一次加成功后把提示文案补上（新建 nid 的 tip 是空的）
                    UpdateTrayTip();
                    PromoteTrayIcon();
                }
            }
            catch (Exception ex)
            {
                _trayStatus = "error:" + ex.GetType().Name;
                ScheduleTrayRetry();
            }
        }

        // Windows 11 默认把新图标塞进"^ 隐藏的图标"里。图标第一次注册成功时，
        // 把它在自己那条记录上标成"在任务栏上显示"；用户之后手动改过的话就不再动。
        void PromoteTrayIcon()
        {
            try
            {
                string self = ExePath();
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(Native.NotifyIconSettingsKey, false))
                {
                    if (root == null) return;
                    foreach (string name in root.GetSubKeyNames())
                    {
                        using (RegistryKey sub = root.OpenSubKey(name, true))
                        {
                            if (sub == null) continue;
                            string path = sub.GetValue("ExecutablePath") as string;
                            if (path == null || string.Compare(path, self, true, CultureInfo.InvariantCulture) != 0) continue;
                            if (sub.GetValue("IsPromoted") != null) return;   // 用户已经表过态，别覆盖
                            sub.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                            return;
                        }
                    }
                }
            }
            catch { }
        }

        // 托盘在登录早期 / Explorer 刚起来时可能还没准备好，失败后每 2 秒重试，最多 20 次
        // （开机自启时 Explorer 常常晚于本程序，重试窗口给足 40 秒）
        void ScheduleTrayRetry()
        {
            if (_trayRetry != null) return;
            _trayRetry = new DispatcherTimer();
            _trayRetry.Interval = TimeSpan.FromSeconds(2);
            _trayRetry.Tick += delegate
            {
                if (_trayAdded || _trayTries >= 20)
                {
                    _trayRetry.Stop();
                    _trayRetry = null;          // 允许 Explorer 重启后重新开始一轮重试
                    if (!_trayAdded) FallBackToTaskbar("托盘图标 20 次重试均失败");
                    return;
                }
                _trayTries++;
                AddTrayIcon();
            };
            _trayRetry.Start();
        }

        // 托盘彻底加不上（受限桌面、组策略、Explorer 异常）：把任务栏按钮打开。
        // 否则关了任务栏之后，用户除了 Ctrl+Alt+H 就再没有能点到窗口的入口。
        // 这是本次运行的兜底，不写进设置 —— 下次启动仍按用户的选择只留托盘。
        void FallBackToTaskbar(string why)
        {
            if (TaskbarVisible) return;
            _taskbarFallback = true;
            try { ShowInTaskbar = true; } catch { }
            RefreshChrome();
            LogLine("taskbar=on (fallback: " + why + ")");
        }

        Native.NOTIFYICONDATA NewTrayData()
        {
            Native.NOTIFYICONDATA nid = new Native.NOTIFYICONDATA();
            nid.cbSize = (uint)Marshal.SizeOf(typeof(Native.NOTIFYICONDATA));
            nid.hWnd = _hwnd;
            nid.uID = 1;
            nid.uCallbackMessage = WM_TRAY;
            nid.szTip = "";
            nid.szInfo = "";
            nid.szInfoTitle = "";
            return nid;
        }

        void UpdateTrayTip()
        {
            if (!_trayAdded) return;
            string tip = _canvas.TrayText();
            if (tip == _lastTip) return;
            _lastTip = tip;
            try
            {
                Native.NOTIFYICONDATA nid = NewTrayData();
                nid.uFlags = Native.NIF_TIP;
                nid.szTip = tip;
                Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref nid);
            }
            catch { }
        }

        void RemoveTrayIcon()
        {
            if (_trayAdded)
            {
                try
                {
                    Native.NOTIFYICONDATA nid = NewTrayData();
                    Native.Shell_NotifyIcon(Native.NIM_DELETE, ref nid);
                }
                catch { }
                _trayAdded = false;
            }
            if (_trayIcon != IntPtr.Zero)
            {
                try { Native.DestroyIcon(_trayIcon); } catch { }
                _trayIcon = IntPtr.Zero;
            }
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == (int)WM_TASKBARCREATED)
            {
                // Explorer 重启：托盘被清空，重建图标
                _trayAdded = false;
                _trayTries = 0;
                AddTrayIcon();
                return IntPtr.Zero;
            }

            if (msg == 0x007E)      // WM_DISPLAYCHANGE：分辨率/显示器变化
            {
                // 拔掉副屏、改分辨率、缩放变更后窗口可能整个跑到外面去，这里重新收回来
                try
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                    {
                        try { KeepOnScreen(); } catch { }
                        try { ApplySize(); } catch { }
                    }));
                }
                catch { }
                return IntPtr.Zero;
            }

            if (msg == WM_TRAY)
            {
                const int WM_LBUTTONUP = 0x0202;
                const int WM_LBUTTONDBLCLK = 0x0203;
                const int WM_RBUTTONUP = 0x0205;
                const int WM_CONTEXTMENU = 0x007B;

                int ev = (int)(lParam.ToInt64() & 0xFFFF);
                if (ev == WM_RBUTTONUP || ev == WM_CONTEXTMENU)
                {
                    // Popup 想自动收起，必须让本进程在前台
                    try { Native.SetForegroundWindow(_hwnd); } catch { }
                    ShowMenu();
                    handled = true;
                }
                else if (ev == WM_LBUTTONDBLCLK)
                {
                    ToggleMinimal();
                    handled = true;
                }
                else if (ev == WM_LBUTTONUP)
                {
                    try
                    {
                        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                        Activate();
                    }
                    catch { }
                    handled = true;
                }
                return IntPtr.Zero;
            }

            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_TOGGLE)
            {
                ToggleMinimal();
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WM_SYSCOMMAND)
            {
                int cmd = wParam.ToInt32() & 0xFFF0;
                if (cmd >= MENU_BASE && cmd <= MENU_BASE + 6 * MENU_STEP)
                {
                    if (RunMenuCommand((cmd - MENU_BASE) / MENU_STEP))
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }
                }
            }

            return IntPtr.Zero;
        }

        // 窗口所在显示器的工作区里；只有在真的跑出屏幕（拔掉副屏、改分辨率）时才挪回来
        void KeepOnScreen()
        {
            Rect wa = WorkAreaOfThisWindow();
            if (Left + Width > wa.Right) Left = wa.Right - Width - 8;
            if (Top + Height > wa.Bottom) Top = wa.Bottom - Height - 8;
            if (Left < wa.Left) Left = wa.Left + 8;
            if (Top < wa.Top) Top = wa.Top + 8;
        }

        // 显式回到主屏（菜单里的「回到右上角」）
        void ActResetPosition()
        {
            Rect wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 18;
            Top = wa.Top + 18;
            _settings.X = (int)Math.Round(Left);
            _settings.Y = (int)Math.Round(Top);
            _settings.Save();
            KeepOnScreen();
        }

        // 取窗口当前所在显示器的工作区（多屏、不同 DPI 都按目标屏换算）；拿不到就退主屏
        Rect WorkAreaOfThisWindow()
        {
            try
            {
                IntPtr hwnd = _hwnd != IntPtr.Zero ? _hwnd : new WindowInteropHelper(this).Handle;
                Native.RECT r;
                if (hwnd != IntPtr.Zero && Native.TryGetWorkArea(hwnd, out r))
                {
                    double s = Screen.Scale(this);
                    // 物理像素 → WPF 设备无关单位（混合 DPI 下必须换算，否则副屏上会被拽偏）
                    return new Rect(r.Left / s, r.Top / s, (r.Right - r.Left) / s, (r.Bottom - r.Top) / s);
                }
            }
            catch { }
            return SystemParameters.WorkArea;
        }

        void OnDragStart(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch { }
            SavePos();
            _settings.Save();
        }

        void OnMenuRequest(object sender, MouseButtonEventArgs e)
        {
            ShowMenu();
        }

        void SavePos()
        {
            _settings.X = (int)Math.Round(Left);
            _settings.Y = (int)Math.Round(Top);
        }

        void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SavePos();
            _settings.Save();

            RemoveTrayIcon();
            if (_hotkeyAdded)
            {
                try { Native.UnregisterHotKey(_hwnd, HOTKEY_TOGGLE); } catch { }
                _hotkeyAdded = false;
            }
            if (_src != null)
            {
                try { _src.RemoveHook(WndProc); } catch { }
                _src = null;
            }

            _sampler.Stop();

            LogLine("exit " + Diagnostics());
            if (_wake != null) { try { _wake.Close(); } catch { } _wake = null; }
        }

        void OnSample(Snapshot s)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                _canvas.Push(s);
                UpdateTrayTip();
                // 第一次拿到真数据后再记 start：这一行里的 pdh= / gpu= 才有意义，
                // 采样线程还没来得及开查询时记的 pdh=off 会误导排错
                if (!_startLogged)
                {
                    _startLogged = true;
                    LogLine("start " + Diagnostics());
                }
            }));
        }

        void ShowMenu()
        {
            if (_menu != null && _menu.IsOpen) { _menu.IsOpen = false; return; }

            StackPanel stack = new StackPanel();

            AddMenuRow(stack, "始终置顶", _settings.Topmost, ActTopmost);
            AddMenuRow(stack, "不透明度 " + OpacityPercent(), false, ActOpacity);
            AddMenuRow(stack, "开机自启动", _settings.Autostart, ActAutostart);
            AddMenuRow(stack, "回到右上角", false, ActResetPosition);
            AddMenuRow(stack, "极简模式", _canvas.Minimal, ActMinimal);
            AddMenuRow(stack, "在任务栏显示", TaskbarVisible, ActTaskbar);
            // 托盘图标没加上时，多给一行手动补救的入口（正常情况不显示，避免菜单变长）
            if (!_trayAdded) AddMenuRow(stack, "重新添加托盘图标", false, ActRetryTray);
            AddMenuRow(stack, "桌面快捷方式", false, ActShortcut);
            AddMenuRow(stack, "诊断信息", false, ActDiagnostics);
            AddMenuRow(stack, "退出", false, ActExit);

            Border card = new Border();
            card.CornerRadius = new CornerRadius(9);
            card.BorderThickness = new Thickness(1);
            card.BorderBrush = Look.Brush(Look.Bezel);
            card.Background = Look.Brush(Color.FromArgb(255, 0x13, 0x1A, 0x1D));
            card.Padding = new Thickness(6);
            card.Child = stack;
            if (Look.Effects)
            {
                DropShadowEffect sfx = new DropShadowEffect();
                sfx.Color = Colors.Black;
                sfx.BlurRadius = 22;
                sfx.ShadowDepth = 6;
                sfx.Direction = 270;
                sfx.Opacity = 0.68;
                card.Effect = sfx;
            }

            _menu = new Popup();
            _menu.AllowsTransparency = true;
            _menu.PlacementTarget = this;
            _menu.Placement = PlacementMode.MousePoint;
            _menu.StaysOpen = false;
            _menu.Child = card;
            _menu.IsOpen = true;
        }

        string OpacityPercent()
        {
            return ((int)Math.Round(_settings.OpacityLevel * 100)).ToString(CultureInfo.InvariantCulture) + "%";
        }

        // ── 菜单动作：右键菜单、任务栏系统菜单、托盘三处共用 ──
        void ActTopmost()
        {
            _settings.Topmost = !_settings.Topmost;
            Topmost = _settings.Topmost;
            _settings.Save();
            BuildSystemMenu();
        }

        void ActOpacity()
        {
            double next = _settings.OpacityLevel - 0.1;
            if (next < 0.65) next = 1.0;
            _settings.OpacityLevel = next;
            Opacity = next;
            _settings.Save();
            BuildSystemMenu();
        }

        void ActAutostart()
        {
            _settings.SetAutostart(!_settings.Autostart, ExePath());
            _settings.Save();
            BuildSystemMenu();
        }

        void ActMinimal()
        {
            ToggleMinimal();
        }

        // 任务栏按钮开关：关掉后窗口只在通知区域留图标，任务栏和 Alt+Tab 都不出现
        void ActTaskbar()
        {
            // 托盘图标没加上时不能把任务栏也关掉 —— 那样就再没有能点到它的入口了
            if (TaskbarVisible && !_trayAdded)
            {
                MessageBox.Show("托盘图标当前不可用，任务栏按钮是唯一的入口，先别关。\n可以先点「重新添加托盘图标」重试一次。",
                                "硬件监测", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _settings.ShowInTaskbar = !TaskbarVisible;
            _taskbarFallback = false;
            try { ShowInTaskbar = _settings.ShowInTaskbar; } catch { }
            RefreshChrome();
            _settings.Save();
            LogLine("taskbar=" + (_settings.ShowInTaskbar ? "on" : "off"));
        }

        void ActShortcut()
        {
            string msg;
            if (!CreateDesktopShortcut(out msg))
                MessageBox.Show(msg, "硬件监测", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        void ActRetryTray()
        {
            _trayAdded = false;
            _trayTries = 0;
            AddTrayIcon();
            if (_trayAdded)
                MessageBox.Show("托盘图标已添加。\n如果任务栏上没看到，点任务栏右侧的 ^ 展开隐藏图标。",
                                "硬件监测", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("托盘图标添加失败：\n" + _trayStatus +
                                "\n\n可以改用：Ctrl+Alt+H 切换极简模式，Alt+Space 打开系统菜单。",
                                "硬件监测", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        void ActExit()
        {
            Close();
        }

        // 用户看不到托盘图标时，用这一行把现场情况一下问清楚
        void ActDiagnostics()
        {
            string text = Diagnostics();
            LogLine("diagnostics " + text);
            MessageBox.Show(text, "硬件监测 · 诊断信息", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        bool RunMenuCommand(int index)
        {
            switch (index)
            {
                case 0: ActMinimal(); return true;
                case 1: ActTopmost(); return true;
                case 2: ActOpacity(); return true;
                case 3: ActResetPosition(); return true;
                case 4: ActShortcut(); return true;
                case 5: ActExit(); return true;
                case 6: ActTaskbar(); return true;
            }
            return false;
        }

        // 把同一组开关挂进窗口系统菜单：任务栏右键、Alt+Space 都能打开。
        // 托盘被策略禁用、或极简模式小到不好点时，这里始终是可用入口。
        void BuildSystemMenu()
        {
            if (_hwnd == IntPtr.Zero)
            {
                _sysMenuStatus = "no-hwnd";
                return;
            }

            try
            {
                // 先 false 让系统把菜单建出来（此时 bRevert=true 反而会返回 NULL），
                // 再用 true 复位清掉上一轮追加的项，最后重新追加。
                IntPtr h = Native.GetSystemMenu(_hwnd, false);
                if (h == IntPtr.Zero)
                {
                    _sysMenuStatus = "no-menu";
                    return;
                }
                Native.GetSystemMenu(_hwnd, true);
                h = Native.GetSystemMenu(_hwnd, false);
                if (h == IntPtr.Zero)
                {
                    _sysMenuStatus = "no-menu";
                    return;
                }

                Native.AppendMenu(h, Native.MF_SEPARATOR, 0, null);
                AppendSysItem(h, 0, "极简模式", _canvas.Minimal);
                AppendSysItem(h, 1, "始终置顶", _settings.Topmost);
                AppendSysItem(h, 2, "不透明度 " + OpacityPercent(), false);
                AppendSysItem(h, 3, "回到右上角", false);
                AppendSysItem(h, 4, "桌面快捷方式", false);
                AppendSysItem(h, 5, "退出", false);
                AppendSysItem(h, 6, "在任务栏显示", TaskbarVisible);
                _sysMenuStatus = "ok";
            }
            catch (Exception ex)
            {
                _sysMenuStatus = "error:" + ex.GetType().Name;
            }
        }

        // 系统菜单命令号低 4 位会被 Windows 吃掉，所以按 0x10 步进
        void AppendSysItem(IntPtr hMenu, int index, string text, bool check)
        {
            uint flags = Native.MF_STRING;
            if (check) flags |= Native.MF_CHECKED;
            Native.AppendMenu(hMenu, flags, (uint)(MENU_BASE + index * MENU_STEP), text);
        }

        void AddMenuRow(StackPanel stack, string text, bool active, Action onClick)        {
            Border row = new Border();
            row.CornerRadius = new CornerRadius(5);
            row.Margin = new Thickness(0, 1, 0, 1);
            row.Background = Brushes.Transparent;
            row.Cursor = Cursors.Hand;

            TextBlock tb = new TextBlock();
            tb.Text = text;
            tb.FontFamily = Look.Face;
            tb.FontSize = 12;
            tb.Foreground = Look.Brush(active ? Look.Ink : Look.InkMid);

            if (active)
            {
                StackPanel inner = new StackPanel();
                inner.Orientation = Orientation.Horizontal;
                Border dot = new Border();
                dot.Width = 4;
                dot.Height = 4;
                dot.CornerRadius = new CornerRadius(2);
                dot.Margin = new Thickness(0, 0, 8, 0);
                dot.VerticalAlignment = VerticalAlignment.Center;
                dot.Background = Look.Brush(Look.PenGpu);
                inner.Children.Add(dot);
                inner.Children.Add(tb);
                row.Child = inner;
                row.Padding = new Thickness(9, 6, 14, 7);
            }
            else
            {
                row.Child = tb;
                row.Padding = new Thickness(21, 6, 14, 7);
            }

            Border captured = row;
            captured.MouseEnter += delegate { captured.Background = Look.Brush(Color.FromArgb(255, 0x1E, 0x28, 0x2C)); };
            captured.MouseLeave += delegate { captured.Background = Brushes.Transparent; };
            captured.MouseLeftButtonUp += delegate
            {
                if (_menu != null) _menu.IsOpen = false;
                try { onClick(); } catch { }
                _canvas.InvalidateVisual();
            };
            stack.Children.Add(row);
        }

        internal static string ExePath()
        {
            try
            {
                Assembly a = Assembly.GetEntryAssembly();
                if (a != null) return a.Location;
            }
            catch { }
            return Process.GetCurrentProcess().MainModule.FileName;
        }

        // 在任意位置写 .lnk：走 WScript.Shell 后期绑定，避免引用 IWshRuntimeLibrary。
        // 桌面快捷方式和「开机自启动」共用这一份逻辑（自启动优先用 .lnk 而不是 .vbs）。
        internal static bool TryCreateShortcut(string lnkPath, string exe, out string message)
        {
            message = "";
            try
            {
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                {
                    message = "找不到程序自身路径，无法创建快捷方式。";
                    return false;
                }
                if (string.IsNullOrEmpty(lnkPath))
                {
                    message = "快捷方式路径为空。";
                    return false;
                }
                string lnkDir = Path.GetDirectoryName(lnkPath);
                if (!string.IsNullOrEmpty(lnkDir) && !Directory.Exists(lnkDir)) Directory.CreateDirectory(lnkDir);

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    message = "系统缺少 WScript.Shell，无法创建快捷方式。";
                    return false;
                }

                object shell = Activator.CreateInstance(shellType);
                object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null,
                                                    shell, new object[] { lnkPath });
                Type scType = sc.GetType();
                string dir = Path.GetDirectoryName(exe);

                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
                scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { dir });
                scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { exe + ",0" });
                scType.InvokeMember("Description", BindingFlags.SetProperty, null, sc,
                                    new object[] { "CPU / GPU / 内存 实时硬件监测悬浮窗" });
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);

                message = lnkPath;
                return true;
            }
            catch (Exception ex)
            {
                message = Describe(ex);
                return false;
            }
        }

        // 把 COM 的多层异常拼成一行，便于用户截图反馈
        static string Describe(Exception ex)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("创建快捷方式失败：");
            Exception cur = ex;
            int depth = 0;
            while (cur != null && depth < 5)
            {
                if (depth > 0) sb.Append(" <- ");
                sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
                cur = cur.InnerException;
                depth++;
            }
            return sb.ToString();
        }

        // 在桌面创建（或修复）.lnk
        internal static bool CreateDesktopShortcut(out string message)
        {
            message = "";
            try
            {
                string exe = ExePath();
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                {
                    message = "找不到程序自身路径，无法创建快捷方式。";
                    return false;
                }

                string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk))
                {
                    message = "找不到桌面目录。";
                    return false;
                }

                return TryCreateShortcut(Path.Combine(desk, "硬件监测.lnk"), exe, out message);
            }
            catch (Exception ex)
            {
                message = Describe(ex);
                return false;
            }
        }
    }

    // ═══════════════════════════ 入口 ═══════════════════════════
    public static class Program
    {
        static int _dispatcherErrors;

        [STAThread]
        public static void Main(string[] args)
        {
            // 先装全局兜底再干活：任何漏掉的异常都要进日志，绝不能让程序"自己消失"
            InstallCrashHandlers();
            try
            {
                Run(args);
            }
            catch (Exception ex)
            {
                ReportFatal(ex);
                Environment.ExitCode = 1;
            }
        }

        static void Run(string[] args)
        {
            // ── 开发开关：--snapshot <png> [--scale N] [--wait MS] [--geometry <txt>] ──
            string shotPath = null, geoPath = null;
            double shotScale = 2.0;
            int waitMs = 2500;
            bool makeShortcut = false;
            bool forceMinimal = false;
            bool toggleMinimal = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--snapshot" && i + 1 < args.Length) shotPath = args[++i];
                else if (a == "--geometry" && i + 1 < args.Length) geoPath = args[++i];
                else if (a == "--scale" && i + 1 < args.Length) { double v; if (double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) shotScale = v; }
                else if (a == "--wait" && i + 1 < args.Length) { int v; if (int.TryParse(args[++i], out v)) waitMs = v; }
                else if (a == "--shortcut") makeShortcut = true;
                else if (a == "--minimal") forceMinimal = true;
                else if (a == "--toggle") toggleMinimal = true;
                else if (a == "--nogpu") Nvml.Disabled = true;   // 开发自检：模拟无 N 卡
            }

            if (makeShortcut)
            {
                string msg;
                bool ok = HudWindow.CreateDesktopShortcut(out msg);
                try
                {
                    string dir = Path.GetDirectoryName(HudWindow.ExePath());
                    if (!string.IsNullOrEmpty(dir))
                        File.WriteAllText(Path.Combine(dir, "shortcut-result.txt"),
                                          (ok ? "OK " : "FAIL ") + msg + "\r\n", Encoding.UTF8);
                }
                catch { }
                Environment.ExitCode = ok ? 0 : 1;
                return;
            }

            // --snapshot 是开发期自检：允许在正常实例运行的同时跑，否则它会静默什么都不做
            Mutex mutex = null;
            if (shotPath == null)
            {
                bool created;
                mutex = new Mutex(true, "HardwareHud.SingleInstance", out created);
                if (!created)
                {
                    // 已经把在跑的那个叫到前面来（双击快捷方式 = 「我找不到它」）
                    try
                    {
                        using (EventWaitHandle wake = EventWaitHandle.OpenExisting(HudWindow.WakeEventName))
                            wake.Set();
                    }
                    catch { }
                    return;
                }
            }

            // DPI 感知：Per-Monitor V2（Win10 1703+）→ 8.1 的 SetProcessDpiAwareness(2) → Vista 起的
            // SetProcessDPIAware()。老系统上缺少的 DLL/入口点都会抛异常，逐级退，退到底也不影响运行，
            // 只是高分屏上可能略糊，总比启动就崩强。
            try
            {
                bool ok = false;
                try { ok = Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
                catch { }
                if (!ok)
                {
                    try { ok = Native.SetProcessDpiAwareness(2) == 0; }
                    catch { }
                }
                if (!ok)
                {
                    try { Native.SetProcessDPIAware(); }
                    catch { }
                }
            }
            catch { }
            try { System.Windows.Media.RenderOptions.ProcessRenderMode = RenderMode.Default; }
            catch { }

            Nvml.Init();
            Settings settings = Settings.Load();

            // --minimal 只影响本次进程，不写回配置
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // 界面线程上的异常：记日志并吞掉（否则任何一个布局/绘制异常都会直接结束进程）。
            // 连续出错太多次说明状态已经坏了，这时写日志退出，用户重开即可，不用卡在半死状态。
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                _dispatcherErrors++;
                HudWindow.LogLine("dispatcher-ex #" + _dispatcherErrors.ToString(CultureInfo.InvariantCulture) +
                                  " " + Describe(e.Exception));
                e.Handled = true;
                if (_dispatcherErrors >= 30)
                {
                    HudWindow.LogLine("dispatcher-ex: too many, exiting");
                    Environment.Exit(4);
                }
            };

            HudWindow win = new HudWindow(settings, settings.Minimal || forceMinimal);
            if (toggleMinimal) win.ToggleMinimal();
            win.Start();

            if (shotPath != null)
            {
                DispatcherTimer timer = new DispatcherTimer();
                timer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, waitMs));
                timer.Tick += delegate
                {
                    timer.Stop();
                    win.SaveSnapshot(shotPath, shotScale);
                    if (geoPath != null) win.SaveGeometry(geoPath);
                    win.SaveLayout(shotPath + ".layout.txt");
                    app.Shutdown();
                };
                win.Loaded += delegate { timer.Start(); };
            }

            app.Run(win);

            if (mutex != null) GC.KeepAlive(mutex);
        }

        // ── 全局崩溃兜底 ────────────────────────────────────────────────
        static void InstallCrashHandlers()
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                HudWindow.LogLine("unhandled terminating=" + (e.IsTerminating ? "1" : "0") + " " + Describe(ex));
                if (e.IsTerminating) ShowFatalBox(ex);
            };

            // 4.0+ 上没被观察的 Task 异常默认会终止进程，这里统一吞掉并记日志
            TaskScheduler.UnobservedTaskException += delegate(object s, UnobservedTaskExceptionEventArgs e)
            {
                HudWindow.LogLine("task-ex " + Describe(e.Exception));
                e.SetObserved();
            };
        }

        static void ReportFatal(Exception ex)
        {
            HudWindow.LogLine("fatal " + Describe(ex));
            ShowFatalBox(ex);
        }

        // 一行异常摘要：类型 + 消息 + 最上面几层堆栈，够定位即可，别把日志撑爆
        static string Describe(Exception ex)
        {
            if (ex == null) return "(null)";
            StringBuilder sb = new StringBuilder();
            sb.Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
            try
            {
                string trace = ex.StackTrace;
                if (!string.IsNullOrEmpty(trace))
                {
                    string[] lines = trace.Split('\n');
                    for (int i = 0; i < lines.Length && i < 4; i++)
                    {
                        string ln = lines[i].Trim();
                        if (ln.Length > 0) sb.Append(" | ").Append(ln);
                    }
                }
            }
            catch { }
            if (ex.InnerException != null) sb.Append(" <- ").Append(Describe(ex.InnerException));
            return sb.ToString();
        }

        static void ShowFatalBox(Exception ex)
        {
            try
            {
                string text = "硬件监测遇到问题，已停止运行。\r\n\r\n" +
                              (ex != null ? ex.GetType().Name + ": " + ex.Message : "未知错误") +
                              "\r\n\r\n日志文件：\r\n" + HudWindow.LogPath();
                Native.MessageBox(IntPtr.Zero, text, "硬件监测",
                                  Native.MB_OK | Native.MB_ICONERROR | Native.MB_SETFOREGROUND);
            }
            catch { }
        }
    }
}
