// HardwareHud —— 桌面硬件实时监测悬浮窗
//   采集：CPU(GetSystemTimes) / 内存(GlobalMemoryStatusEx) / 磁盘·网络(性能计数器) / GPU(NVML 直连)
//   界面：WPF 全自绘，无边框磨砂面板 + 三条走纸式实时曲线
// 编译：build.bat（csc /target:winexe，C# 5 语法）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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

        static IntPtr[] _devices = new IntPtr[0];
        static string[] _names = new string[0];
        public static bool Available;

        public static int Count { get { return _devices.Length; } }
        public static string[] Names { get { return _names; } }

        public static void Init()
        {
            try
            {
                if (nvmlInit_v2() != 0) return;
                int n;
                if (nvmlDeviceGetCount_v2(out n) != 0 || n <= 0) return;

                _devices = new IntPtr[n];
                _names = new string[n];
                for (int i = 0; i < n; i++)
                {
                    IntPtr dev = IntPtr.Zero;
                    if (nvmlDeviceGetHandleByIndex_v2(i, out dev) != 0) { _names[i] = "GPU " + i; continue; }
                    _devices[i] = dev;
                    StringBuilder sb = new StringBuilder(96);
                    nvmlDeviceGetName(dev, sb, 96);
                    _names[i] = sb.ToString();
                }
                Available = true;
            }
            catch { Available = false; }
        }

        public static GpuStat[] Read()
        {
            if (!Available) return new GpuStat[0];
            List<GpuStat> list = new List<GpuStat>();
            for (int i = 0; i < _devices.Length; i++)
            {
                IntPtr dev = _devices[i];
                if (dev == IntPtr.Zero) continue;

                GpuStat g = new GpuStat();
                g.Index = i;
                g.Name = _names[i];

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

    // ═══════════════════════════ 数据结构 ═══════════════════════════
    public sealed class GpuStat
    {
        public int Index;
        public string Name = "";
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

        Thread _thread;
        volatile bool _run;
        ulong _prevIdle, _prevKernel, _prevUser;
        bool _primed;

        PerformanceCounter _diskRead, _diskWrite, _commit, _cpuPerf;
        PerformanceCounter[] _netIn, _netOut;
        bool _countersOk;
        bool _counterPrimed;
        int _gate = 6;

        public string CpuModel = "CPU";
        public int CpuCores;
        public int CpuThreads;
        public double CpuBaseMhz;
        public Action<Snapshot> OnSample;

        public Sampler()
        {
            ReadCpuIdentity();
            InitCounters();
        }

        void ReadCpuIdentity()
        {
            CpuThreads = Environment.ProcessorCount;
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

            if (CpuBaseMhz <= 0)
            {
                try
                {
                    PerformanceCounter f = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total");
                    f.NextValue();
                    Thread.Sleep(120);
                    float v = f.NextValue();
                    if (v > 100 && v < 9000) CpuBaseMhz = v;
                    f.Dispose();
                }
                catch { }
            }
        }

        static string ShortCpuName(string raw)
        {
            string s = raw.Trim();
            int i = s.IndexOf("Core(TM)", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) s = s.Substring(i + 9).Trim();
            s = s.Replace("CPU", "").Replace("Processor", "").Trim();
            while (s.Length > 0 && s[0] == ' ') s = s.Substring(1);
            return s.Length > 0 ? s : raw.Trim();
        }

        void InitCounters()
        {
            try
            {
                _diskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                _diskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                _commit = new PerformanceCounter("Memory", "Committed Bytes");
                _cpuPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");

                List<string> names = new List<string>();
                PerformanceCounterCategory cat = new PerformanceCounterCategory("Network Interface");
                foreach (string inst in cat.GetInstanceNames())
                {
                    if (inst.IndexOf("isatap", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (inst.IndexOf("Teredo", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (inst.IndexOf("Loopback", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (inst.IndexOf("Pseudo", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    names.Add(inst);
                }

                _netIn = new PerformanceCounter[names.Count];
                _netOut = new PerformanceCounter[names.Count];
                for (int i = 0; i < names.Count; i++)
                {
                    _netIn[i] = new PerformanceCounter("Network Interface", "Bytes Received/sec", names[i]);
                    _netOut[i] = new PerformanceCounter("Network Interface", "Bytes Sent/sec", names[i]);
                }
                _countersOk = true;
            }
            catch { _countersOk = false; }
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
            Native.FILETIME idle, kern, user;
            if (!Native.GetSystemTimes(out idle, out kern, out user)) return;
            _prevIdle = idle.Value;
            _prevKernel = kern.Value;
            _prevUser = user.Value;
            _primed = true;

            if (_countersOk)
            {
                try
                {
                    _diskRead.NextValue(); _diskWrite.NextValue(); _commit.NextValue(); _cpuPerf.NextValue();
                    for (int i = 0; i < _netIn.Length; i++) { _netIn[i].NextValue(); _netOut[i].NextValue(); }
                    _counterPrimed = true;
                }
                catch { _countersOk = false; }
            }

            Stopwatch sw = new Stopwatch();
            while (_run)
            {
                sw.Restart();
                Thread.Sleep(IntervalMs);
                Snapshot s = Collect();

                Action<Snapshot> cb = OnSample;
                if (cb != null)
                {
                    try { cb(s); } catch { }
                }

                _gate++;
                if (_gate >= 240) { _gate = 0; try { GC.Collect(0); } catch { } }
            }
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

                s.CpuGhz = double.NaN;
                if (_countersOk && CpuBaseMhz > 0)
                {
                    try
                    {
                        float perf = _cpuPerf.NextValue();
                        if (perf > 0) s.CpuGhz = CpuBaseMhz * perf / 100.0 / 1000.0;
                    }
                    catch { }
                }
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

            s.RamCommitGb = double.NaN;
            s.DiskRead = s.DiskWrite = s.NetIn = s.NetOut = double.NaN;
            if (_countersOk && _counterPrimed)
            {
                try
                {
                    s.RamCommitGb = _commit.NextValue() / 1073741824.0;
                    s.DiskRead = _diskRead.NextValue();
                    s.DiskWrite = _diskWrite.NextValue();
                    double ni = 0, no = 0;
                    for (int i = 0; i < _netIn.Length; i++)
                    {
                        ni += _netIn[i].NextValue();
                        no += _netOut[i].NextValue();
                    }
                    s.NetIn = ni;
                    s.NetOut = no;
                }
                catch { }
            }

            s.UptimeSec = Native.GetTickCount64() / 1000.0;
            s.Gpus = Nvml.Read();
            return s;
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

        static string StartupLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "HardwareHud.vbs"); }
        }

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
                        string[] kv = lines[i].Split('=');
                        if (kv.Length != 2) continue;
                        string k = kv[0].Trim();
                        string v = kv[1].Trim();
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
                    }
                }
            }
            catch { }
            s.Autostart = File.Exists(StartupLink) || RunKeyPresent();
            if (!File.Exists(StartupLink) && RunKeyPresent()) s.UseWindowsStartup = false;
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string[] lines = new string[]
                {
                    "topmost=" + (Topmost ? "1" : "0"),
                    "opacity=" + OpacityLevel.ToString("0.0", CultureInfo.InvariantCulture),
                    "x=" + X.ToString(CultureInfo.InvariantCulture),
                    "y=" + Y.ToString(CultureInfo.InvariantCulture),
                    "minimal=" + (Minimal ? "1" : "0")
                };
                File.WriteAllLines(FilePath, lines);
            }
            catch { }
        }

        public void SetAutostart(bool on, string exePath)
        {
            if (!on)
            {
                try { if (File.Exists(StartupLink)) File.Delete(StartupLink); } catch { }
                ClearRunKey();
                Autostart = false;
                return;
            }

            // 先试 Startup 快捷方式；写不进去（受限配置文件 / 受管企业环境）就退到 Run 注册表键。
            if (UseWindowsStartup)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(StartupLink));
                    string body = "' 由 HardwareHud 生成：登录时静默启动\r\n" +
                                  "CreateObject(\"WScript.Shell\").Run \"\"\"" + exePath + "\"\"\", 0, False\r\n";
                    File.WriteAllText(StartupLink, body, Encoding.Default);
                    ClearRunKey();
                    Autostart = true;
                    return;
                }
                catch { UseWindowsStartup = false; }
            }

            if (WriteRunKey(exePath))
            {
                try { if (File.Exists(StartupLink)) File.Delete(StartupLink); } catch { }
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

        public static readonly FontFamily Face = new FontFamily("Bahnschrift SemiCondensed, Bahnschrift, Microsoft YaHei UI");

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

            if (s.Gpus.Length != GpuRowCount() && s.Gpus.Length > 0) BuildRows(s.Gpus.Length, true);

            Append("cpu", (float)s.Cpu);
            Append("ram", (float)s.RamPct);
            for (int i = 0; i < s.Gpus.Length; i++) Append("gpu" + i.ToString(CultureInfo.InvariantCulture), s.Gpus[i].Util);
            if (s.Gpus.Length == 0) Append("gpu0", float.NaN);

            InvalidateVisual();
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
                        rng = " min=" + F(mn) + " max=" + F(mx) + " nan=" + nan;
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

                    sb.AppendLine("row[" + i + "] " + r.Label + " pen=" + r.Pen);
                    sb.AppendLine("   labelBox  x=" + F(labelBox.X) + " y=" + F(labelBox.Y) + " w=" + F(labelBox.Width) + " h=" + F(labelBox.Height) + " bottom=" + F(labelBox.Bottom));
                    sb.AppendLine("   numBox    x=" + F(numBox.X) + " y=" + F(numBox.Y) + " w=" + F(numBox.Width) + " h=" + F(numBox.Height) + " right=" + F(numBox.Right) + " bottom=" + F(numBox.Bottom));
                    sb.AppendLine("   unitBox   right=" + F(unitRight) + " left=" + F(unitLeft) + " y=" + F(unitTop) + " h=" + F(unitTxt.Height) + " bottom=" + F(unitTop + unitTxt.Height));
                    sb.AppendLine("   baseline  y=" + F(baseline) + " labelBaseline=" + F(label.Baseline) + " numBaseline=" + F(numTxt.Baseline));
                    sb.AppendLine("   well      x=" + F(plot.X) + " y=" + F(plot.Y) + " w=" + F(plot.Width) + " h=" + F(plot.Height) + " bottom=" + F(plot.Bottom));
                    if (d != null)
                        sb.AppendLine("   detail    x=" + F(left) + " y=" + F(plot.Bottom + DetailGap) + " h=" + F(d.Height) + " bottom=" + F(plot.Bottom + DetailGap + d.Height));
                    sb.AppendLine("   CHECK label/num overlap = " + (labelBox.IntersectsWith(numBox) ? "YES <-- BAD" : "no"));
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
            _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

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

            // 头部
            FormattedText title = Text(CpuTitle, 14, FontWeights.SemiBold, Look.Ink);
            dc.DrawText(title, new Point(Pad - 1, HeaderTop));
            dc.DrawLine(new Pen(Look.Brush(Look.Alpha(Look.PenCpu, 0.55)), 1),
                new Point(Pad - 1, HeaderTop + title.Height + 3),
                new Point(Pad + title.WidthIncludingTrailingWhitespace, HeaderTop + title.Height + 3));

            FormattedText up = Text(UptimeText(), 10.5, FontWeights.Normal, Look.InkDim);
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

            FormattedText label = Text(r.Label, 10.5, FontWeights.Normal, Look.InkMid);
            dc.DrawText(label, new Point(left, baseline - label.Baseline));

            FormattedText unitTxt = Text("%", 11, FontWeights.Normal, Look.InkDim);
            DrawRight(dc, unitTxt, right, baseline - unitTxt.Baseline);
            double unitLeft = right - unitTxt.WidthIncludingTrailingWhitespace;

            FormattedText numTxt = Text(RowNum(r), 23, FontWeights.SemiBold, hot ? Look.Alert : Look.Ink);
            DrawRight(dc, numTxt, unitLeft - 3, baseline - numTxt.Baseline);

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

        string GpuDetail(Row r)
        {
            if (_snap == null) return "";
            if (r.GpuIndex < 0 || r.GpuIndex >= _snap.Gpus.Length)
                return Nvml.Available ? "" : "未检测到 NVIDIA 驱动";

            GpuStat g = _snap.Gpus[r.GpuIndex];
            StringBuilder sb = new StringBuilder();
            if (!float.IsNaN(g.MemUsedMb) && g.MemTotalMb > 0)
                sb.Append((g.MemUsedMb / 1024f).ToString("0.0", CultureInfo.InvariantCulture))
                  .Append(" / ").Append((g.MemTotalMb / 1024f).ToString("0.0", CultureInfo.InvariantCulture)).Append(" GB");
            if (!float.IsNaN(g.TempC)) sb.Append("    ").Append(Math.Round(g.TempC).ToString(CultureInfo.InvariantCulture)).Append("°C");
            if (!float.IsNaN(g.PowerW) && g.PowerW > 1)
                sb.Append("    ").Append(Math.Round(g.PowerW).ToString(CultureInfo.InvariantCulture)).Append(" W");
            else
                sb.Append("    功耗 N/A");
            if (!float.IsNaN(g.ClockMhz) && g.ClockMhz > 0) sb.Append("    ").Append(Math.Round(g.ClockMhz).ToString(CultureInfo.InvariantCulture)).Append(" MHz");
            if (!float.IsNaN(g.FanPct)) sb.Append("    风扇 ").Append(Math.Round(g.FanPct).ToString(CultureInfo.InvariantCulture)).Append("%");
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
            ShowInTaskbar = true;
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
                sb.AppendLine("dpi=" + VisualTreeHelper.GetDpi(_canvas).PixelsPerDip.ToString("0.###", CultureInfo.InvariantCulture));
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
            LogLine("start " + Diagnostics());
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

        // 任务栏按钮 + 系统菜单：无边框全透明窗口这两样都得手动补
        void RefreshChrome()
        {
            if (_hwnd == IntPtr.Zero) return;
            Native.ForceAppWindow(_hwnd);
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

        // 托盘在登录早期 / Explorer 刚起来时可能还没准备好，失败后每 2 秒重试，最多 8 次
        void ScheduleTrayRetry()
        {
            if (_trayRetry != null) return;
            _trayRetry = new DispatcherTimer();
            _trayRetry.Interval = TimeSpan.FromSeconds(2);
            _trayRetry.Tick += delegate
            {
                if (_trayAdded || _trayTries >= 8)
                {
                    _trayRetry.Stop();
                    return;
                }
                _trayTries++;
                AddTrayIcon();
            };
            _trayRetry.Start();
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
                if (cmd >= MENU_BASE && cmd <= MENU_BASE + 5 * MENU_STEP)
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

        void KeepOnScreen()
        {
            Rect wa = SystemParameters.WorkArea;
            if (Left + Width > wa.Right) Left = wa.Right - Width - 8;
            if (Top + Height > wa.Bottom) Top = wa.Bottom - Height - 8;
            if (Left < wa.Left) Left = wa.Left + 8;
            if (Top < wa.Top) Top = wa.Top + 8;
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
            DropShadowEffect sfx = new DropShadowEffect();
            sfx.Color = Colors.Black;
            sfx.BlurRadius = 22;
            sfx.ShadowDepth = 6;
            sfx.Direction = 270;
            sfx.Opacity = 0.68;
            card.Effect = sfx;

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

        void ActResetPosition()
        {
            Left = SystemParameters.WorkArea.Right - Width - 18;
            Top = SystemParameters.WorkArea.Top + 18;
            SavePos();
            _settings.Save();
        }

        void ActMinimal()
        {
            ToggleMinimal();
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

        // 在桌面创建（或修复）.lnk：走 WScript.Shell 后期绑定，避免引用 IWshRuntimeLibrary
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

                string lnk = Path.Combine(desk, "硬件监测.lnk");

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    message = "系统缺少 WScript.Shell，无法创建快捷方式。";
                    return false;
                }

                object shell = Activator.CreateInstance(shellType);
                object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null,
                                                    shell, new object[] { lnk });
                Type scType = sc.GetType();
                string dir = Path.GetDirectoryName(exe);

                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
                scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { dir });
                scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { exe + ",0" });
                scType.InvokeMember("Description", BindingFlags.SetProperty, null, sc,
                                    new object[] { "CPU / GPU / 内存 实时硬件监测悬浮窗" });
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);

                message = lnk;
                return true;
            }
            catch (Exception ex)
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
                message = sb.ToString();
                return false;
            }
        }
    }

    // ═══════════════════════════ 入口 ═══════════════════════════
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
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

            bool created;
            Mutex mutex = new Mutex(true, "HardwareHud.SingleInstance", out created);
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

            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { }
            try { System.Windows.Media.RenderOptions.ProcessRenderMode = RenderMode.Default; }
            catch { }

            Nvml.Init();
            Settings settings = Settings.Load();

            // --minimal 只影响本次进程，不写回配置
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;

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

            GC.KeepAlive(mutex);
        }
    }
}
