// HardwareHud 安装 / 卸载程序
//   编译：build.bat setup —— 会把 dist\HardwareHud.exe 作为资源嵌进来，但**不**往壳里塞别的东西
//   安装：无需管理员，全部写在当前用户下（%LOCALAPPDATA% + HKCU）
//
//   Setup.exe                      显示界面，装到 %LOCALAPPDATA%\Programs\HardwareHud
//   Setup.exe /S                   静默安装（不弹窗、不等待）
//   Setup.exe /dir:"D:\Tools\Hud"  指定安装目录
//   Setup.exe /update              覆盖升级：只替换 exe 和图标，快捷方式/卸载信息保持不动
//   Setup.exe /nolaunch            装完不自动启动
//   Setup.exe /uninstall           卸载（配 /S 静默，配 /purge 连设置和日志一起删）
//
// 版本号从被解压出来的 HardwareHud.exe 里读，安装程序自己不再维护一份。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HardwareHudSetup
{
    internal static class Program
    {
        public const string AppExe = "HardwareHud.exe";
        public const string AppIcon = "HardwareHud.ico";
        public const string Uninstaller = "uninstall.exe";
        public const string DisplayName = "硬件监测 (HardwareHud)";
        public const string Publisher = "xiao_dust";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HardwareHud";
        const string ResourceName = "HardwareHud.exe";

        public static bool Silent;
        public static bool Update;
        public static bool NoLaunch;
        public static bool Uninstall;
        public static bool Purge;
        public static string Dir;
        public static bool DirExplicit;   // 命令行给了 /dir:，卸载时就不用再看自己住在哪

        [STAThread]
        public static int Main(string[] args)
        {
            if (!ParseArgs(args))
            {
                Console.Error.WriteLine("用法见 installer/Setup.cs 文件头注释");
                return 2;
            }

            try
            {
                Log("start dir=" + Dir + " silent=" + Silent + " update=" + Update + " uninstall=" + Uninstall +
                    " args=" + string.Join(" ", args));
                if (Uninstall)
                {
                    // 从「应用和功能」点进来的卸载器没带 /dir：它自己就住在安装目录里，按自己的位置算
                    if (!DirExplicit)
                    {
                        string exeDir = null;
                        try { exeDir = Path.GetDirectoryName(ExePath()); }
                        catch { }
                        if (!string.IsNullOrEmpty(exeDir)) Dir = exeDir;
                    }

                    // 卸载器自己就住在安装目录里：先把自己搬到 TEMP 再删目录，否则删不掉自己
                    if (IsRunningFromInstallDir()) return Relocate();

                    if (!Silent && !ConfirmUninstall()) return 0;
                    return RunUninstall();
                }

                if (Silent || Update) return RunInstall(null);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (SetupForm f = new SetupForm())
                {
                    Application.Run(f);
                    return f.ExitCode;
                }
            }
            catch (Exception ex)
            {
                // 静默模式没人看得到弹窗，把原因落到文件里，方便用户把安装目录下的日志发过来
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "HardwareHud-setup-error.txt"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + ex + "\r\n", Encoding.UTF8);
                }
                catch { }
                if (Silent) return 1;
                MessageBox.Show("安装程序出错：\n" + ex.Message, "硬件监测", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static bool ParseArgs(string[] args)
        {
            string cand = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "/S" || a == "-s" || a == "/silent") Silent = true;
                else if (a == "/update") Update = true;
                else if (a == "/nolaunch") NoLaunch = true;
                else if (a == "/uninstall" || a == "-uninstall") Uninstall = true;
                else if (a == "/purge") Purge = true;
                else if (a.StartsWith("/dir:")) cand = a.Substring(5).Trim('"');
                else if ((a == "/dir" || a == "-d") && i + 1 < args.Length) cand = args[++i].Trim('"');
                else if (a == "/?" || a == "/help" || a == "-h") return false;
            }
            Dir = string.IsNullOrEmpty(cand) ? DefaultDir() : Path.GetFullPath(cand);
            DirExplicit = !string.IsNullOrEmpty(cand);
            return true;
        }

        public static string DefaultDir()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root))
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData\\Local");
            return Path.Combine(Path.Combine(root, "Programs"), "HardwareHud");
        }

        public static string ExePath()
        {
            try { return Assembly.GetExecutingAssembly().Location; }
            catch { return Process.GetCurrentProcess().MainModule.FileName; }
        }

        static bool IsRunningFromInstallDir()
        {
            try
            {
                string me = Path.GetFullPath(ExePath());
                string target = Path.GetFullPath(Path.Combine(Dir, Uninstaller));
                return string.Compare(me, target, true) == 0;
            }
            catch { return false; }
        }

        // 把卸载任务交给 TEMP 里的一份副本，自己立刻退出，安装目录才能被完整删除
        static int Relocate()
        {
            string temp = Path.Combine(Path.GetTempPath(),
                "HardwareHud-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
            try
            {
                File.Copy(ExePath(), temp, true);
            }
            catch (Exception ex)
            {
                // TEMP 不可写（受限桌面、磁盘满、安全软件拦截）时不要卡死：
                // 就地卸载，删得掉多少算多少，剩下的交给退出后的 del/rmdir
                Log("relocate failed: " + ex.GetType().Name + " " + ex.Message + " -> uninstall in place");
                return RunUninstall();
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("/uninstall");
            if (Silent) sb.Append(" /S");
            if (Purge) sb.Append(" /purge");
            if (!string.IsNullOrEmpty(Dir)) sb.Append(" /dir:\"").Append(Dir).Append("\"");

            ProcessStartInfo psi = new ProcessStartInfo(temp, sb.ToString());
            psi.UseShellExecute = false;
            Process.Start(psi);
            return 0;
        }

        // ── 安装 ────────────────────────────────────────────────────────
        public static int RunInstall(Action<int, string> report)
        {
            byte[] payload = ReadPayload();
            if (payload == null)
            {
                Fail(report, "安装包不完整：里面没有找到 " + AppExe);
                return 1;
            }

            Step(report, 5, "关闭正在运行的程序…");
            KillRunning();

            Step(report, 20, "创建目录 " + Dir);
            Directory.CreateDirectory(Dir);

            Step(report, 40, "释放 " + AppExe);
            string exe = Path.Combine(Dir, AppExe);
            WriteWithRetry(exe, payload);

            Step(report, 55, "释放图标");
            byte[] icon = ReadResource("HardwareHud.ico");
            if (icon != null) WriteWithRetry(Path.Combine(Dir, AppIcon), icon);

            string version = FileVersion(exe);

            Step(report, 65, "创建快捷方式");
            string desc = "硬件监测：CPU / GPU / 内存实时曲线";
            if (!CreateShortcut(Path.Combine(DesktopDir(), "硬件监测.lnk"), exe, desc))
                Log("desktop shortcut failed");

            if (!Update)
            {
                Step(report, 75, "设置开机自启动");
                if (!CreateShortcut(Path.Combine(StartupDir(), "HardwareHud.lnk"), exe, desc))
                    Log("startup shortcut failed");
            }
            Step(report, 85, "登记卸载信息");
            string self = Path.Combine(Dir, Uninstaller);
            try { File.Copy(ExePath(), self, true); } catch { }
            WriteUninstallEntry(version, exe, self);

            Step(report, 95, "完成");
            if (!NoLaunch)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(exe);
                    psi.WorkingDirectory = Dir;
                    Process.Start(psi);
                }
                catch { }
            }
            Step(report, 100, "已安装 " + DisplayName + " " + version);
            return 0;
        }

        static void Fail(Action<int, string> report, string msg)
        {
            if (report != null) report(0, msg);
            else if (!Silent) MessageBox.Show(msg, "硬件监测", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        static void Step(Action<int, string> report, int pct, string msg)
        {
            Log(pct + "% " + msg);
            if (report != null) report(pct, msg);
            else if (!Silent) Console.WriteLine(msg);
        }

        // 安装过程留一份日志：静默安装失败时唯一的现场证据
        public static void Log(string text)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + "\r\n";
            string[] targets = new string[]
            {
                Path.Combine(Path.GetTempPath(), "HardwareHud-setup.log"),
                Path.Combine(Dir, "setup.log")
            };
            for (int i = 0; i < targets.Length; i++)
            {
                try
                {
                    File.AppendAllText(targets[i], line, new UTF8Encoding(false));
                    return;
                }
                catch { }
            }
        }

        static byte[] ReadPayload()
        {
            return ReadResource(ResourceName);
        }

        static byte[] ReadResource(string name)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (s == null) return null;
                    byte[] buf = new byte[s.Length];
                    int off = 0;
                    while (off < buf.Length)
                    {
                        int n = s.Read(buf, off, buf.Length - off);
                        if (n <= 0) break;
                        off += n;
                    }
                    if (off != buf.Length) Array.Resize(ref buf, off);
                    return buf;
                }
            }
            catch { return null; }
        }

        // 杀软/索引器偶尔会短暂占用刚写入的文件，重试几次比直接失败友好
        static void WriteWithRetry(string path, byte[] data)
        {
            Exception last = null;
            for (int i = 0; i < 10; i++)
            {
                try { File.WriteAllBytes(path, data); return; }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(200);
                }
            }
            throw new IOException("写不进 " + path + "：" + (last == null ? "" : last.Message));
        }

        public static void KillRunning()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("HardwareHud"))
                {
                    try { p.Kill(); p.WaitForExit(4000); }
                    catch { }
                }
            }
            catch { }
            Thread.Sleep(250);
        }

        static string DesktopDir()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }

        static string StartupDir()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        }

        // WScript.Shell 后期绑定：不引 COM 互操作程序集，老系统上也不挑版本
        static bool CreateShortcut(string lnk, string target, string desc)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return false;
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type sct = sc.GetType();
                sct.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                sct.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
                sct.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
                sct.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { desc });
                sct.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.ReleaseComObject(sc);
                Marshal.ReleaseComObject(shell);
                return true;
            }
            catch { return false; }
        }

        static void WriteUninstallEntry(string version, string exe, string uninstaller)
        {
            Log("uninstall-entry begin ver=" + version);
            try
            {
                WriteUninstallEntryCore(version, exe, uninstaller);
                Log("uninstall-entry done");
            }
            catch (Exception ex)
            {
                // 不让卸载登记失败毁掉整个安装：程序本体已经就位，
                // 最多是「应用和功能」里看不到这一项
                Log("uninstall-entry FAILED (continuing) " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void WriteUninstallEntryCore(string version, string exe, string uninstaller)
        {
            long sizeKb = 0;
            try
            {
                foreach (string f in Directory.GetFiles(Dir))
                    sizeKb += new FileInfo(f).Length / 1024;
            }
            catch { }

            // { 名称, 值, 类型 }
            string[][] vals = new string[][]
            {
                new string[] { "DisplayName", DisplayName, "REG_SZ" },
                new string[] { "DisplayVersion", version, "REG_SZ" },
                new string[] { "Publisher", Publisher, "REG_SZ" },
                new string[] { "DisplayIcon", exe + ",0", "REG_SZ" },
                new string[] { "InstallLocation", Dir, "REG_SZ" },
                new string[] { "InstallDate", DateTime.Now.ToString("yyyyMMdd"), "REG_SZ" },
                new string[] { "UninstallString", "\"" + uninstaller + "\" /uninstall", "REG_SZ" },
                new string[] { "QuietUninstallString", "\"" + uninstaller + "\" /uninstall /S", "REG_SZ" },
                new string[] { "NoModify", "1", "REG_DWORD" },
                new string[] { "NoRepair", "1", "REG_DWORD" },
                new string[] { "EstimatedSize", ((int)Math.Min(sizeKb, 1048575)).ToString(), "REG_DWORD" }
            };

            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    if (k != null)
                    {
                        for (int i = 0; i < vals.Length; i++)
                        {
                            bool numeric = vals[i][2] == "REG_DWORD";
                            k.SetValue(vals[i][0], numeric ? (object)int.Parse(vals[i][1]) : vals[i][1],
                                numeric ? RegistryValueKind.DWord : RegistryValueKind.String);
                        }
                    }
                }
                Log("uninstall-entry written direct");
                return;
            }
            catch (Exception ex)
            {
                // 受限环境（或者被安全软件拦截）里 .NET 写注册表会被拒，
                // 退回 reg.exe —— 它是系统自带、通常有额外放行
                Log("uninstall-entry direct failed: " + ex.GetType().Name + ", retry via reg.exe");
            }

            if (!WriteValuesViaRegExe(vals))
                throw new UnauthorizedAccessException("无法写入卸载登记项（请检查是否被安全软件拦截）");
            Log("uninstall-entry written via reg.exe");
        }

        // 用 reg.exe 逐个写值：绕开 .NET 直写注册表被拒的情况
        static bool WriteValuesViaRegExe(string[][] vals)
        {
            for (int i = 0; i < vals.Length; i++)
            {
                ProcessStartInfo ps = new ProcessStartInfo("reg.exe",
                    "add \"HKCU\\" + UninstallKey + "\" /v " + vals[i][0] + " /t " + vals[i][2] +
                    " /d " + Quote(vals[i][1]) + " /f");
                ps.UseShellExecute = false;
                ps.CreateNoWindow = true;
                try
                {
                    using (Process p = Process.Start(ps))
                    {
                        if (p == null) return false;
                        if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return false; }
                        if (p.ExitCode != 0) return false;
                    }
                }
                catch { return false; }
            }
            return true;
        }

        static string Quote(string s)
        {
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        // "1.3.0.0" 这种去掉末尾的 .0，显示成 "1.3.0"
        public static string FileVersion(string exe)
        {
            try
            {
                FileVersionInfo vi = FileVersionInfo.GetVersionInfo(exe);
                string v = vi.FileVersion;
                if (string.IsNullOrEmpty(v)) v = vi.ProductVersion;
                if (string.IsNullOrEmpty(v)) return "?";
                string[] parts = v.Split('.');
                int n = parts.Length;
                while (n > 3 && parts[n - 1] == "0") n--;
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < n; i++)
                {
                    if (i > 0) sb.Append('.');
                    sb.Append(parts[i]);
                }
                return sb.ToString();
            }
            catch { return "?"; }
        }

        public static string InstalledVersion()
        {
            string exe = Path.Combine(Dir, AppExe);
            if (!File.Exists(exe)) return null;
            return FileVersion(exe);
        }

        // ── 卸载 ────────────────────────────────────────────────────────
        static bool ConfirmUninstall()
        {
            string ver = InstalledVersion();
            string what = ver == null ? DisplayName : DisplayName + " " + ver;
            DialogResult r = MessageBox.Show(
                "确定要卸载 " + what + " 吗？\n\n" +
                "会删除安装目录、桌面快捷方式和开机自启动。" +
                (Purge ? "\n设置和日志也会一起删除。" : "\n你的设置和日志会保留。"),
                "卸载 · 硬件监测", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            return r == DialogResult.Yes;
        }

        public static int RunUninstall()
        {
            KillRunning();

            try { File.Delete(Path.Combine(DesktopDir(), "硬件监测.lnk")); } catch { }
            try { File.Delete(Path.Combine(DesktopDir(), "HardwareHud.lnk")); } catch { }
            try { File.Delete(Path.Combine(StartupDir(), "HardwareHud.lnk")); } catch { }
            try { File.Delete(Path.Combine(StartupDir(), "HardwareHud.vbs")); } catch { }

            try
            {
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    if (run != null) run.DeleteValue("HardwareHud", false);
            }
            catch { }

            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }

            if (Purge) DeleteSettings();

            // 目录里有正在运行的 self 时删不掉，重试几轮
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    if (!Directory.Exists(Dir)) break;
                    Directory.Delete(Dir, true);
                    break;
                }
                catch { Thread.Sleep(300); }
            }

            if (!Silent)
            {
                string msg = Directory.Exists(Dir)
                    ? "部分文件没能删除（可能还在被占用）：\n" + Dir + "\n\n重启后再手动删除这个目录即可。"
                    : "已卸载。";
                if (Purge) msg += "\n设置和日志已删除。";
                MessageBox.Show(msg, "卸载 · 硬件监测", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            ScheduleSelfDelete();
            return 0;
        }

        static void DeleteSettings()
        {
            try { Directory.Delete(Path.Combine(SettingsRoot("ApplicationData"), "HardwareHud"), true); } catch { }
            try { Directory.Delete(Path.Combine(SettingsRoot("LocalApplicationData"), "HardwareHud"), true); } catch { }
        }

        static string SettingsRoot(string which)
        {
            try
            {
                Environment.SpecialFolder f = which == "ApplicationData"
                    ? Environment.SpecialFolder.ApplicationData
                    : Environment.SpecialFolder.LocalApplicationData;
                return Environment.GetFolderPath(f);
            }
            catch { return Path.GetTempPath(); }
        }

        // 卸载器自己是 TEMP 里的临时副本，退出后请 cmd 帮忙删掉
        static void ScheduleSelfDelete()
        {
            try
            {
                string me = ExePath();
                if (me == null) return;

                // 从 TEMP 副本跑的：只删自己。
                // 就地卸载的：先删自己，再把整个安装目录清干净（此时目录里只剩我们的 exe）
                string args;
                if (me.IndexOf(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) == 0)
                    args = "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + me + "\"";
                else
                    args = "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + me + "\" & rmdir /s /q \"" + Dir + "\"";

                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch { }
        }

        // ── 界面 ────────────────────────────────────────────────────────
        sealed class SetupForm : Form
        {
            readonly TextBox _path = new TextBox();
            readonly Button _browse = new Button();
            readonly Button _go = new Button();
            readonly Button _close = new Button();
            readonly ProgressBar _bar = new ProgressBar();
            readonly Label _status = new Label();
            readonly Label _head = new Label();
            readonly Label _note = new Label();
            public int ExitCode;

            public SetupForm()
            {
                Text = "安装 · 硬件监测 (HardwareHud)";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(470, 240);
                Font = new Font("Microsoft YaHei UI", 9F);
                BackColor = Color.FromArgb(0x14, 0x1A, 0x1D);
                ForeColor = Color.FromArgb(0xE6, 0xEE, 0xF2);

                string ver = null;
                byte[] payload = ReadPayload();
                if (payload != null)
                {
                    // 版本号藏在被嵌入的 exe 里，解出来读一次；TEMP 不可写时退到安装包旁边
                    string tmp = null;
                    try
                    {
                        tmp = Path.Combine(Path.GetTempPath(), "hh-ver-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".exe");
                        File.WriteAllBytes(tmp, payload);
                    }
                    catch
                    {
                        tmp = null;
                        try
                        {
                            string beside = Path.GetDirectoryName(ExePath());
                            if (!string.IsNullOrEmpty(beside))
                            {
                                tmp = Path.Combine(beside, "hh-ver-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".exe");
                                File.WriteAllBytes(tmp, payload);
                            }
                        }
                        catch { tmp = null; }
                    }
                    if (tmp != null)
                    {
                        ver = FileVersion(tmp);
                        try { File.Delete(tmp); } catch { }
                    }
                }

                _head.Text = "硬件监测 (HardwareHud) " + (ver ?? "");
                _head.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
                _head.ForeColor = Color.FromArgb(0xFF, 0xD2, 0x7A);
                _head.SetBounds(20, 16, 430, 26);

                Label sub = new Label();
                sub.Text = "单文件绿色程序，约 107 KB，不需要 .NET 之外的任何运行库。";
                sub.ForeColor = Color.FromArgb(0x8A, 0x9A, 0xA2);
                sub.SetBounds(20, 46, 430, 20);

                Label where = new Label();
                where.Text = "安装到：";
                where.SetBounds(20, 86, 60, 20);

                _path.SetBounds(82, 83, 300, 24);
                _path.Text = Dir;
                _path.SelectionStart = 0;
                _path.SelectionLength = 0;
                _path.BackColor = Color.FromArgb(0x0E, 0x13, 0x15);
                _path.ForeColor = Color.FromArgb(0xE6, 0xEE, 0xF2);
                _path.BorderStyle = BorderStyle.FixedSingle;

                _browse.Text = "更改…";
                _browse.SetBounds(388, 82, 64, 26);
                _browse.FlatStyle = FlatStyle.Flat;
                _browse.Click += delegate { Browse(); };

                _note.Text = HintText(ver);
                _note.ForeColor = Color.FromArgb(0x8A, 0x9A, 0xA2);
                _note.SetBounds(20, 116, 430, 40);

                _bar.SetBounds(20, 162, 430, 8);
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Maximum = 100;

                _status.SetBounds(20, 176, 430, 20);
                _status.ForeColor = Color.FromArgb(0x6E, 0x7E, 0x86);

                _go.Text = "安装";
                _go.SetBounds(280, 202, 84, 28);
                _go.FlatStyle = FlatStyle.Flat;
                _go.BackColor = Color.FromArgb(0xFF, 0xD2, 0x7A);
                _go.ForeColor = Color.FromArgb(0x14, 0x1A, 0x1D);
                _go.Click += delegate { DoInstall(); };

                _close.Text = "取消";
                _close.SetBounds(372, 202, 84, 28);
                _close.FlatStyle = FlatStyle.Flat;
                _close.Click += delegate { Close(); };

                Controls.Add(_head);
                Controls.Add(sub);
                Controls.Add(where);
                Controls.Add(_path);
                Controls.Add(_browse);
                Controls.Add(_note);
                Controls.Add(_bar);
                Controls.Add(_status);
                Controls.Add(_go);
                Controls.Add(_close);
                AcceptButton = _go;
                CancelButton = _close;
            }

            void Browse()
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = "选择安装位置";
                    d.SelectedPath = _path.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) _path.Text = d.SelectedPath;
                }
            }

            string HintText(string target)
            {
                string have = InstalledVersion();
                if (have != null && have != "?" && have != "0.0.0" && have != "0.0.0.0")
                    return "这台机器上已经装了 " + have + "，继续会升级到 " + target + "，设置和位置都会保留。";
                if (have != null)
                    return "检测到旧的安装，继续会覆盖升级到 " + target + "，设置和位置都会保留。";
                return "不需要管理员权限：程序、快捷方式和卸载信息都写在你自己的用户目录里。";
            }

            void DoInstall()
            {
                Dir = _path.Text.Trim();
                if (Dir.Length == 0) { MessageBox.Show("安装目录不能为空。"); return; }
                try { Dir = Path.GetFullPath(Dir); }
                catch { MessageBox.Show("安装目录无效：" + Dir); return; }

                _go.Enabled = false;
                _browse.Enabled = false;
                _path.Enabled = false;
                _close.Text = "关闭";

                int rc = RunInstall(delegate (int pct, string msg)
                {
                    if (pct > _bar.Value) _bar.Value = Math.Min(100, pct);
                    _status.Text = msg;
                    Application.DoEvents();
                });

                ExitCode = rc;
                if (rc == 0)
                {
                    _head.Text = "安装完成";
                    _note.Text = "已在桌面和开始菜单的启动项里创建快捷方式，程序已经启动。\n" +
                                 "任务栏右侧的 ^ 里可以找到托盘图标。";
                    _close.Text = "关闭";
                    _go.Visible = false;
                }
                else
                {
                    _head.Text = "安装失败";
                    _go.Enabled = true;
                    _browse.Enabled = true;
                    _path.Enabled = true;
                }
            }
        }
    }
}
