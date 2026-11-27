using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BrightnessApp
{
    static class Program
    {
        public const string Version = "1.5.1";
        public const string AppName = "屏亮管家";

        [STAThread]
        static void Main()
        {
            string[] args = Environment.GetCommandLineArgs();
            bool takeover = args.Length > 1 && args[1] == "--takeover";

            bool created;
            using (var mutex = new System.Threading.Mutex(true, "BrightnessControl.SingleInstance", out created))
            {
                // 已经在运行：不重复开窗口，直接让已有实例把自己叫出来
                if (!created && !takeover)
                {
                    Single.RequestShow();
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) =>
                {
                    try { LogStore.Write("ERROR", "ui-exception: " + e.Exception); } catch { }
                    MessageBox.Show(e.Exception.Message, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    try { LogStore.Write("ERROR", "crash: " + e.ExceptionObject); } catch { }
                };
                AppDomain.CurrentDomain.ProcessExit += (s, e) =>
                {
                    try { LogStore.Write("CONFIG", "process-exit"); } catch { }
                };
                Application.Run(new MainForm());
                GC.KeepAlive(mutex);
            }
        }
    }

    // ── 单实例唤醒 ───────────────────────────────────────────
    static class Single
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public static readonly uint ShowMessage = RegisterWindowMessage("BrightnessApp.ShowWindow");

        public static void RequestShow()
        {
            try
            {
                if (ShowMessage == 0) return;
                PostMessage(new IntPtr(0xFFFF), ShowMessage, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }
    }

    // ── 亮度读写 ─────────────────────────────────────────────
    static class Brightness
    {
        static readonly object Gate = new object();

        public static int Get()
        {
            lock (Gate)
            {
                Exception last = null;
                for (int i = 0; i < 3; i++)
                {
                    try { return GetOnce(); }
                    catch (Exception ex)
                    {
                        last = ex;
                        System.Threading.Thread.Sleep(120);
                    }
                }
                int v;
                if (TryGetViaProcess(out v)) return v;
                throw last;
            }
        }

        public static bool Set(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            lock (Gate)
            {
                Exception last = null;
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        if (SetOnce(percent)) return true;
                        last = null;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        System.Threading.Thread.Sleep(150);
                    }
                }
                if (TrySetViaProcess(percent)) return true;
                if (last != null) throw last;
                return false;
            }
        }

        // 进程内 WMI 客户端偶尔会失效（InvalidCastException），这时改用独立进程兜底
        static bool TryGetViaProcess(out int value)
        {
            value = -1;
            string text = RunPowerShell(
                "(Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness).CurrentBrightness", 5000);
            if (text == null) return false;

            string[] lines = text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string ln in lines)
            {
                int v;
                if (int.TryParse(ln.Trim(), out v) && v >= 0 && v <= 100)
                {
                    value = v;
                    return true;
                }
            }
            return false;
        }

        static bool TrySetViaProcess(int percent)
        {
            string cmd = "$m = Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods; "
                + "Invoke-CimMethod -InputObject $m -MethodName WmiSetBrightness "
                + "-Arguments @{ Timeout = 1; Brightness = " + percent + " } | Out-Null";
            return RunPowerShell(cmd, 8000) != null;
        }

        static string RunPowerShell(string command, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -Command \"" + command + "\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    return output;
                }
            }
            catch { return null; }
        }

        static int GetOnce()
        {
            using (var searcher = new ManagementObjectSearcher(
                "root\\WMI", "SELECT CurrentBrightness, Active FROM WmiMonitorBrightness"))
            {
                int? fallback = null;
                foreach (ManagementBaseObject o in searcher.Get())
                {
                    object cb = null;
                    try { cb = o["CurrentBrightness"]; } catch { }
                    if (cb == null) continue;

                    int v;
                    try { v = Convert.ToInt32(cb); }
                    catch { continue; }

                    object active = null;
                    try { active = o["Active"]; } catch { }
                    if (active is bool && (bool)active) return v;
                    if (!fallback.HasValue) fallback = v;
                }
                if (fallback.HasValue) return fallback.Value;
            }
            throw new InvalidOperationException("读不到内置屏亮度");
        }

        static bool SetOnce(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            using (var searcher = new ManagementObjectSearcher(
                "root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
            {
                foreach (ManagementObject o in searcher.Get())
                {
                    o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)percent });
                    return true;
                }
            }
            return false;
        }

        public static int LevelCount()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "root\\WMI", "SELECT Levels FROM WmiMonitorBrightness"))
                {
                    foreach (ManagementBaseObject o in searcher.Get())
                    {
                        object lv = null;
                        try { lv = o["Levels"]; } catch { }
                        if (lv != null)
                        {
                            int n = Convert.ToInt32(lv);
                            if (n > 0) return n;
                        }
                        var arr = o["Level"] as Array;
                        if (arr != null && arr.Length > 0) return arr.Length;
                    }
                }
            }
            catch { }
            return 0;
        }
    }

    // ── 外接屏：DDC/CI ───────────────────────────────────────
    class MonitorTarget
    {
        public bool Internal;
        public string Device;
        public string Name;

        public override string ToString() { return Name; }
    }

    static class Ddc
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);
        delegate bool PmAction(IntPtr hPm, uint min, uint max);

        [DllImport("user32.dll")]
        static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);

        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] array);

        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool GetMonitorBrightness(IntPtr hMonitor, ref uint min, ref uint cur, ref uint max);

        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool SetMonitorBrightness(IntPtr hMonitor, uint value);

        [DllImport("dxva2.dll", SetLastError = true)]
        static extern bool DestroyPhysicalMonitors(uint count, [In] PHYSICAL_MONITOR[] array);

        // 内置屏的型号代码，取自 WMI 亮度接口所在的那块面板
        public static string InternalCode()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("root\\WMI",
                    "SELECT InstanceName FROM WmiMonitorBrightness"))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        string inst = Convert.ToString(o["InstanceName"]);
                        string[] p = inst.Split('\\');
                        if (p.Length >= 2) return p[1];
                    }
                }
            }
            catch { }
            return "";
        }

        // 枚举支持 DDC/CI 亮度的外接屏
        public static List<MonitorTarget> ExternalTargets()
        {
            var names = WmiNameMap();
            var list = new List<MonitorTarget>();
            try
            {
                MonitorEnumProc proc = delegate(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data)
                {
                    var mi = new MONITORINFOEX();
                    mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                    if (!GetMonitorInfo(hMon, ref mi)) return true;

                    uint n = 0;
                    if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, ref n) || n == 0) return true;

                    var arr = new PHYSICAL_MONITOR[n];
                    if (!GetPhysicalMonitorsFromHMONITOR(hMon, n, arr)) return true;
                    try
                    {
                        uint mn = 0, cur = 0, mx = 0;
                        if (!GetMonitorBrightness(arr[0].hPhysicalMonitor, ref mn, ref cur, ref mx))
                            return true;   // 内置屏通常走不到这里，DDC 不支持

                        string code = DeviceCode(mi.szDevice);
                        string name;
                        if (!names.TryGetValue(code, out name) || string.IsNullOrEmpty(name))
                            name = code;
                        if (string.IsNullOrEmpty(name)) name = mi.szDevice;

                        var t = new MonitorTarget();
                        t.Internal = false;
                        t.Device = mi.szDevice;
                        t.Name = "外接屏 " + name + "（DDC）";
                        list.Add(t);
                    }
                    finally { DestroyPhysicalMonitors(n, arr); }
                    return true;
                };
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            }
            catch { }
            return list;
        }

        public static bool TryRead(string device, out int percent)
        {
            int value = -1;
            bool ok = RunOn(device, delegate(IntPtr hPm, uint min, uint max)
            {
                uint a = 0, cur = 0, b = 0;
                if (!GetMonitorBrightness(hPm, ref a, ref cur, ref b)) return false;
                value = ToPercent((int)cur, (int)min, (int)max);
                return value >= 0;
            });
            percent = value;
            return ok;
        }

        public static bool TryWrite(string device, int percent, out int readback)
        {
            int rb = -1;
            bool ok = RunOn(device, delegate(IntPtr hPm, uint min, uint max)
            {
                int raw = FromPercent(percent, (int)min, (int)max);
                if (!SetMonitorBrightness(hPm, (uint)raw)) return false;

                System.Threading.Thread.Sleep(220);
                uint a = 0, cur = 0, b = 0;
                if (GetMonitorBrightness(hPm, ref a, ref cur, ref b))
                    rb = ToPercent((int)cur, (int)min, (int)max);
                else
                    rb = percent;
                return true;
            });
            readback = rb;
            return ok;
        }

        static bool RunOn(string device, PmAction action)
        {
            bool ok = false;
            MonitorEnumProc proc = delegate(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data)
            {
                var mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (!GetMonitorInfo(hMon, ref mi)) return true;
                if (!string.Equals(mi.szDevice, device, StringComparison.OrdinalIgnoreCase)) return true;

                uint n = 0;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, ref n) || n == 0) return false;

                var arr = new PHYSICAL_MONITOR[n];
                if (!GetPhysicalMonitorsFromHMONITOR(hMon, n, arr)) return false;
                try
                {
                    uint mn = 0, cur = 0, mx = 0;
                    if (!GetMonitorBrightness(arr[0].hPhysicalMonitor, ref mn, ref cur, ref mx)) return false;
                    ok = action(arr[0].hPhysicalMonitor, mn, mx);
                }
                finally { DestroyPhysicalMonitors(n, arr); }
                return false;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            return ok;
        }

        static int ToPercent(int raw, int min, int max)
        {
            if (max <= min) return -1;
            int p = (int)Math.Round((raw - min) * 100.0 / (max - min));
            return Math.Max(0, Math.Min(100, p));
        }

        static int FromPercent(int percent, int min, int max)
        {
            if (max <= min) return percent;
            int raw = min + (int)Math.Round(percent * (max - min) / 100.0);
            return Math.Max(min, Math.Min(max, raw));
        }

        static string DeviceCode(string device)
        {
            try
            {
                var dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (!EnumDisplayDevices(device, 0, ref dd, 1)) return "";
                string[] p = (dd.DeviceID ?? "").Split('#');
                return p.Length >= 3 ? p[1] : "";
            }
            catch { return ""; }
        }

        // 型号代码 → 友好名称（从 WMI 读取实际显示器型号）
        static Dictionary<string, string> WmiNameMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var s = new ManagementObjectSearcher("root\\WMI",
                    "SELECT InstanceName, UserFriendlyName, ManufacturerName FROM WmiMonitorID"))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        string[] p = Convert.ToString(o["InstanceName"]).Split('\\');
                        if (p.Length < 2) continue;
                        string name = U16(o["UserFriendlyName"]);
                        if (string.IsNullOrEmpty(name)) name = U16(o["ManufacturerName"]);
                        if (!string.IsNullOrEmpty(name)) map[p[1]] = name;
                    }
                }
            }
            catch { }
            return map;
        }

        static string U16(object raw)
        {
            var sb = new StringBuilder();
            try
            {
                Array arr = raw as Array;
                if (arr == null) return "";
                foreach (object o in arr)
                {
                    ushort u;
                    try { u = Convert.ToUInt16(o); }
                    catch { continue; }
                    if (u == 0) break;
                    sb.Append((char)u);
                }
            }
            catch { }
            return sb.ToString().Trim();
        }
    }

    // ── 开机自启 ─────────────────────────────────────────────
    static class AutoStart
    {
        const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "BrightnessControl";

        public static bool Enabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    return k != null && k.GetValue(ValueName) != null;
                }
            }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(ValueName, false);
                }
            }
            catch { }
        }
    }

    // ── 日志 ─────────────────────────────────────────────────
    static class LogStore
    {
        public static readonly string Dir =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

        public static string TodayFile
        {
            get { return Path.Combine(Dir, "brightness-" + DateTime.Now.ToString("yyyyMMdd") + ".log"); }
        }

        // 界面展示用：用户目录替换成 %USERPROFILE%，避免暴露 Windows 用户名
        public static string DisplayPath(string path)
        {
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home) && path != null
                    && path.StartsWith(home, StringComparison.OrdinalIgnoreCase))
                    return "%USERPROFILE%" + path.Substring(home.Length);
            }
            catch { }
            return path;
        }

        public static void Write(string action, string detail)
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                string line = string.Format("{0} | {1,-6} | {2}",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), action, detail);
                File.AppendAllText(TodayFile, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }

        public static void Cleanup(int keepDays)
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                DateTime cutoff = DateTime.Now.AddDays(-keepDays);
                foreach (string f in Directory.GetFiles(Dir, "*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static string LastReadFile = "";

        public static List<string[]> ReadRecent(int maxLines)
        {
            var result = new List<string[]>();
            try
            {
                string file = TodayFile;
                if (!File.Exists(file) && Directory.Exists(Dir))
                {
                    string[] files = Directory.GetFiles(Dir, "brightness-*.log");
                    if (files.Length == 0) files = Directory.GetFiles(Dir, "*.log");
                    if (files.Length > 0)
                    {
                        string newest = files[0];
                        foreach (string f in files)
                            if (File.GetLastWriteTime(f) > File.GetLastWriteTime(newest)) newest = f;
                        file = newest;
                    }
                }
                LastReadFile = file;
                if (!File.Exists(file)) return result;

                string[] lines = File.ReadAllLines(file, new UTF8Encoding(false));
                for (int i = lines.Length - 1; i >= 0 && result.Count < maxLines; i--)
                {
                    string raw = lines[i];
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string[] parts = raw.Split(new string[] { " | " }, StringSplitOptions.None);
                    if (parts.Length >= 3) result.Add(new string[] { parts[0], parts[1].Trim(), parts[2] });
                    else result.Add(new string[] { "", "", raw });
                }
            }
            catch { }
            return result;
        }
    }

    // ── 电源设置（计划 / 模式 / 超时） ────────────────────────
    class PowerScheme
    {
        public string Guid;
        public string Name;

        public override string ToString() { return Name; }
    }

    class PowerState
    {
        public string SchemeGuid = "";
        public string SchemeName = "";
        public string AcOverlay = "";
        public string DcOverlay = "";
        public int AcVideoIdle = -1;
        public int DcVideoIdle = -1;
        public int AcStandbyIdle = -1;
        public int DcStandbyIdle = -1;
        public int AcMaxCpu = -1;
        public int DcMaxCpu = -1;
        public int AcMinCpu = -1;
        public int DcMinCpu = -1;
        public int AcCooling = -1;
        public int DcCooling = -1;
        public int AcBoost = -1;
        public int DcBoost = -1;
        public DateTime At = DateTime.Now;
    }

    static class PowerCtl
    {
        public const string OverlayBattery = "961cc777-2547-4f9d-8174-7d86181b8a7a";
        public const string OverlayMaxPerf = "ded574b5-45a0-4f42-8737-46345c09c238";

        const string Root = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
        const string SubVideo = "7516b95f-f776-4464-8c53-06167f40cc99";
        const string SubSleep = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
        const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
        const string SetVideoIdleId = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";
        const string SetStandbyIdleId = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da";
        const string SetMaxCpuId = "bc5038f7-23e0-4960-96da-33abaf5935ec";
        const string SetMinCpuId = "893dee8e-2bef-41e0-89c6-b55d0929964c";
        const string SetCoolingId = "94d3a615-a899-4ac5-ae2b-e4d8f634367f";
        const string SetBoostModeId = "be337238-0d82-4146-a960-4f3749d470c7";

        [DllImport("powrprof.dll")]
        static extern uint PowerSetActiveOverlayScheme(Guid scheme);

        [DllImport("powrprof.dll")]
        static extern uint PowerGetActualOverlayScheme(out Guid scheme);

        [DllImport("powrprof.dll")]
        static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);

        public static bool IsAdmin()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static bool OnAc()
        {
            try { return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online; }
            catch { return true; }
        }

        // 1=交流电，0=电池，-1=状态未知（切换瞬间可能读不到）
        public static int PowerLineState()
        {
            try
            {
                PowerLineStatus s = SystemInformation.PowerStatus.PowerLineStatus;
                if (s == PowerLineStatus.Online) return 1;
                if (s == PowerLineStatus.Offline) return 0;
                return -1;
            }
            catch { return -1; }
        }

        public static List<PowerScheme> ListSchemes()
        {
            var list = new List<PowerScheme>();
            try
            {
                int code;
                string text = Run("/list", out code);
                foreach (string ln in text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    Match m = Regex.Match(ln, @"GUID:\s*([0-9a-fA-F-]{36})\s*\((.*?)\)");
                    if (!m.Success) continue;
                    var ps = new PowerScheme();
                    ps.Guid = m.Groups[1].Value.ToLower();
                    ps.Name = m.Groups[2].Value.Trim();
                    list.Add(ps);
                }
            }
            catch { }
            return list;
        }

        public static PowerState Read()
        {
            var s = new PowerState();
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(Root))
                {
                    if (k != null)
                    {
                        s.SchemeGuid = Convert.ToString(k.GetValue("ActivePowerScheme"));
                        s.AcOverlay = Convert.ToString(k.GetValue("ActiveOverlayAcPowerScheme"));
                        s.DcOverlay = Convert.ToString(k.GetValue("ActiveOverlayDcPowerScheme"));
                    }
                }
            }
            catch { }

            s.SchemeName = SysInfo.SchemeName(s.SchemeGuid);
            s.At = DateTime.Now;

            try
            {
                Dictionary<string, int[]> map = QueryValues();
                int[] v;
                if (map.TryGetValue(SetVideoIdleId, out v)) { s.AcVideoIdle = v[0]; s.DcVideoIdle = v[1]; }
                if (map.TryGetValue(SetStandbyIdleId, out v)) { s.AcStandbyIdle = v[0]; s.DcStandbyIdle = v[1]; }
                if (map.TryGetValue(SetMaxCpuId, out v)) { s.AcMaxCpu = v[0]; s.DcMaxCpu = v[1]; }
                if (map.TryGetValue(SetMinCpuId, out v)) { s.AcMinCpu = v[0]; s.DcMinCpu = v[1]; }
                if (map.TryGetValue(SetCoolingId, out v)) { s.AcCooling = v[0]; s.DcCooling = v[1]; }
                if (map.TryGetValue(SetBoostModeId, out v)) { s.AcBoost = v[0]; s.DcBoost = v[1]; }
            }
            catch { }
            return s;
        }

        static Dictionary<string, int[]> QueryValues()
        {
            var map = new Dictionary<string, int[]>();
            int code;
            string text = Run("/q SCHEME_CURRENT", out code);
            if (string.IsNullOrEmpty(text)) return map;

            string cur = null;
            foreach (string ln in text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Match gm = Regex.Match(ln, @"GUID:\s*([0-9a-fA-F-]{36})");
                if (gm.Success) { cur = gm.Groups[1].Value.ToLower(); continue; }
                if (cur == null) continue;
                if (ln.IndexOf("索引:") < 0) continue;

                Match hm = Regex.Match(ln, @"0x([0-9a-fA-F]+)");
                if (!hm.Success) continue;

                int v = Convert.ToInt32(hm.Groups[1].Value, 16);
                int[] slot;
                if (!map.TryGetValue(cur, out slot))
                {
                    slot = new int[] { -1, -1 };
                    map[cur] = slot;
                }
                if (ln.IndexOf("交流") >= 0) slot[0] = v;
                else if (ln.IndexOf("直流") >= 0) slot[1] = v;
            }
            return map;
        }

        public static bool SetScheme(string guid, out string err)
        {
            int code;
            string text = Run("/setactive " + guid, out code);
            err = code == 0 ? "" : Clean(text, code);
            return code == 0;
        }

        public static bool SetOverlay(string guid, out string err)
        {
            err = "";
            try
            {
                Guid g = string.IsNullOrEmpty(guid) ? Guid.Empty : new Guid(guid);
                uint rc = PowerSetActiveOverlayScheme(g);
                if (rc != 0)
                {
                    err = rc == 5 ? "需要管理员权限" : ("返回码 " + rc);
                    return false;
                }
                return true;
            }
            catch (Exception ex) { err = ex.Message; return false; }
        }

        // 当前电源来源实际生效的模式（无覆盖层时返回全 0）
        public static string ActualOverlay()
        {
            try
            {
                Guid g;
                uint rc = PowerGetActualOverlayScheme(out g);
                if (rc != 0) return "";
                return g.ToString().ToLower();
            }
            catch { return ""; }
        }

        public static string EffectiveOverlay()
        {
            try
            {
                Guid g;
                uint rc = PowerGetEffectiveOverlayScheme(out g);
                if (rc != 0) return "";
                return g.ToString().ToLower();
            }
            catch { return ""; }
        }

        public static bool SetValue(bool ac, string sub, string setting, int value, out string err)
        {
            string args = (ac ? "/setacvalueindex " : "/setdcvalueindex ")
                + "SCHEME_CURRENT " + sub + " " + setting + " " + value;
            int code;
            string text = Run(args, out code);
            err = code == 0 ? "" : Clean(text, code);
            return code == 0;
        }

        public static bool SetVideoIdle(bool ac, int sec, out string err)
        {
            return SetValue(ac, SubVideo, SetVideoIdleId, sec, out err);
        }

        public static bool SetStandbyIdle(bool ac, int sec, out string err)
        {
            return SetValue(ac, SubSleep, SetStandbyIdleId, sec, out err);
        }

        public static bool SetMaxCpu(bool ac, int pct, out string err)
        {
            return SetValue(ac, SubProcessor, SetMaxCpuId, pct, out err);
        }

        public static bool SetMinCpu(bool ac, int pct, out string err)
        {
            return SetValue(ac, SubProcessor, SetMinCpuId, pct, out err);
        }

        // 系统散热方式：1 = 主动（风扇优先，减少降频），0 = 被动
        public static bool SetCooling(bool ac, int value, out string err)
        {
            return SetValue(ac, SubProcessor, SetCoolingId, value, out err);
        }

        // 睿频策略：0 = 关闭，1 = 启用，2 = 激进
        public static bool SetBoostMode(bool ac, int value, out string err)
        {
            return SetValue(ac, SubProcessor, SetBoostModeId, value, out err);
        }

        public static bool Activate(out string err)
        {
            int code;
            string text = Run("/setactive SCHEME_CURRENT", out code);
            err = code == 0 ? "" : Clean(text, code);
            return code == 0;
        }

        static string Clean(string text, int code)
        {
            if (string.IsNullOrEmpty(text)) return "返回码 " + code;
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > 120) text = text.Substring(0, 120);
            return text;
        }

        static string Run(string args, out int code)
        {
            code = -1;
            try
            {
                var psi = new ProcessStartInfo("powercfg.exe", args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    // powercfg 在部分系统上直接输出 UTF-8，先按严格 UTF-8 解码，失败再回退 GBK
                    string o = ReadText(p.StandardOutput.BaseStream);
                    string e = ReadText(p.StandardError.BaseStream);
                    if (!p.WaitForExit(20000))
                    {
                        try { p.Kill(); } catch { }
                        return o + " " + e;
                    }
                    code = p.ExitCode;
                    return o + " " + e;
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        static string ReadText(Stream stream)
        {
            byte[] buf;
            using (var ms = new MemoryStream())
            {
                byte[] tmp = new byte[8192];
                int n;
                while ((n = stream.Read(tmp, 0, tmp.Length)) > 0) ms.Write(tmp, 0, n);
                buf = ms.ToArray();
            }
            if (buf.Length == 0) return "";
            try { return new UTF8Encoding(false, true).GetString(buf); }
            catch { return Encoding.GetEncoding(936).GetString(buf); }
        }

        // ── 备份 / 撤销 ────────────────────────────────────
        public static string BackupPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "power-backup.txt"); }
        }

        // 插拔锁定专用备份，与「应用并备份」互不覆盖
        public static string LockBackupPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "power-lock-backup.txt"); }
        }

        public static void SaveSnapshot(PowerState s)
        {
            SaveSnapshot(s, BackupPath);
        }

        public static void SaveSnapshot(PowerState s, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("at=" + s.At.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("scheme=" + s.SchemeGuid + ";" + s.SchemeName);
            sb.AppendLine("overlayAc=" + s.AcOverlay);
            sb.AppendLine("overlayDc=" + s.DcOverlay);
            sb.AppendLine("videoAc=" + s.AcVideoIdle);
            sb.AppendLine("videoDc=" + s.DcVideoIdle);
            sb.AppendLine("standbyAc=" + s.AcStandbyIdle);
            sb.AppendLine("standbyDc=" + s.DcStandbyIdle);
            sb.AppendLine("maxCpuAc=" + s.AcMaxCpu);
            sb.AppendLine("maxCpuDc=" + s.DcMaxCpu);
            sb.AppendLine("minCpuAc=" + s.AcMinCpu);
            sb.AppendLine("minCpuDc=" + s.DcMinCpu);
            sb.AppendLine("coolAc=" + s.AcCooling);
            sb.AppendLine("coolDc=" + s.DcCooling);
            sb.AppendLine("boostAc=" + s.AcBoost);
            sb.AppendLine("boostDc=" + s.DcBoost);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        public static PowerState LoadSnapshot()
        {
            return LoadSnapshot(BackupPath);
        }

        public static PowerState LoadSnapshot(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;

                var s = new PowerState();
                bool any = false;
                DateTime savedAt = DateTime.MinValue;
                foreach (string raw in File.ReadAllLines(path, new UTF8Encoding(false)))
                {
                    int i = raw.IndexOf('=');
                    if (i <= 0) continue;
                    string k = raw.Substring(0, i).Trim();
                    string v = raw.Substring(i + 1).Trim();
                    any = true;

                    if (k == "at")
                    {
                        DateTime dt;
                        if (DateTime.TryParse(v, out dt)) savedAt = dt;
                    }
                    else if (k == "scheme")
                    {
                        int sp = v.IndexOf(';');
                        s.SchemeGuid = sp > 0 ? v.Substring(0, sp) : v;
                        s.SchemeName = sp > 0 ? v.Substring(sp + 1) : "";
                    }
                    else if (k == "overlayAc") s.AcOverlay = v;
                    else if (k == "overlayDc") s.DcOverlay = v;
                    else if (k == "videoAc") s.AcVideoIdle = ToInt(v);
                    else if (k == "videoDc") s.DcVideoIdle = ToInt(v);
                    else if (k == "standbyAc") s.AcStandbyIdle = ToInt(v);
                    else if (k == "standbyDc") s.DcStandbyIdle = ToInt(v);
                    else if (k == "maxCpuAc") s.AcMaxCpu = ToInt(v);
                    else if (k == "maxCpuDc") s.DcMaxCpu = ToInt(v);
                    else if (k == "minCpuAc") s.AcMinCpu = ToInt(v);
                    else if (k == "minCpuDc") s.DcMinCpu = ToInt(v);
                    else if (k == "coolAc") s.AcCooling = ToInt(v);
                    else if (k == "coolDc") s.DcCooling = ToInt(v);
                    else if (k == "boostAc") s.AcBoost = ToInt(v);
                    else if (k == "boostDc") s.DcBoost = ToInt(v);
                }
                if (!any) return null;

                try { s.At = File.GetLastWriteTime(path); } catch { }
                if (savedAt != DateTime.MinValue) s.At = savedAt;
                return s;
            }
            catch { return null; }
        }

        static int ToInt(string v)
        {
            int n;
            if (int.TryParse(v, out n)) return n;
            return -1;
        }

        public static string OverlayName(string guid)
        {
            if (string.IsNullOrEmpty(guid) ||
                guid.StartsWith("00000000", StringComparison.Ordinal)) return "平衡（默认）";
            if (string.Equals(guid, OverlayBattery, StringComparison.OrdinalIgnoreCase)) return "节电模式";
            if (string.Equals(guid, OverlayMaxPerf, StringComparison.OrdinalIgnoreCase)) return "更好的性能";
            return "其他模式";
        }
    }

    // ── 剪贴板（Win32 直写，比 OLE 的 Flush 稳，远程/自动化环境也能用）──
    static class Clip
    {
        [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll", SetLastError = true)] static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetClipboardData(uint format, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] static extern bool CloseClipboard();
        [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr size);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
        [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr mem);

        public static bool WriteText(string text)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        EmptyClipboard();
                        byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
                        IntPtr mem = GlobalAlloc(0x0042, (UIntPtr)bytes.Length); // GMEM_MOVEABLE
                        if (mem == IntPtr.Zero) return false;
                        IntPtr p = GlobalLock(mem);
                        if (p == IntPtr.Zero)
                        {
                            GlobalFree(mem);
                            return false;
                        }
                        Marshal.Copy(bytes, 0, p, bytes.Length);
                        GlobalUnlock(mem);
                        if (SetClipboardData(13, mem) == IntPtr.Zero) // CF_UNICODETEXT
                        {
                            GlobalFree(mem);
                            return false;
                        }
                        return true;
                    }
                    finally { CloseClipboard(); }
                }
                System.Threading.Thread.Sleep(100);
            }
            return false;
        }
    }

    // ── 简单的界面设置持久化 ─────────────────────────────────
    static class UiFile
    {
        static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-settings.txt"); }
        }

        public static string Get(string key, string fallback)
        {
            try
            {
                if (!File.Exists(FilePath)) return fallback;
                foreach (string raw in File.ReadAllLines(FilePath, new UTF8Encoding(false)))
                {
                    int i = raw.IndexOf('=');
                    if (i <= 0) continue;
                    if (raw.Substring(0, i).Trim() == key) return raw.Substring(i + 1).Trim();
                }
            }
            catch { }
            return fallback;
        }

        public static void Set(string key, string value)
        {
            try
            {
                var map = new List<string>();
                bool replaced = false;
                if (File.Exists(FilePath))
                {
                    foreach (string raw in File.ReadAllLines(FilePath, new UTF8Encoding(false)))
                    {
                        int i = raw.IndexOf('=');
                        if (i > 0 && raw.Substring(0, i).Trim() == key)
                        {
                            map.Add(key + "=" + value);
                            replaced = true;
                        }
                        else if (raw.Trim().Length > 0) map.Add(raw);
                    }
                }
                if (!replaced) map.Add(key + "=" + value);
                File.WriteAllLines(FilePath, map.ToArray(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // ── CPU 频率 / 占用率 ────────────────────────────────────
    // 本机没有功率传感器（Power Meter 与放电速率均为无效哨兵值），
    // 用「基频 × % Processor Performance」反映实际睿频，占用率取 % Processor Time。
    static class CpuMeter
    {
        static PerformanceCounter _freq;
        static PerformanceCounter _perf;
        static PerformanceCounter _time;
        static bool _failed;

        static void Init()
        {
            if (_freq != null || _failed) return;
            try
            {
                _freq = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total");
                _perf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
                _time = new PerformanceCounter("Processor Information", "% Processor Time", "_Total");
                _freq.NextValue();
                _perf.NextValue();
                _time.NextValue();
            }
            catch
            {
                _failed = true;
                _freq = null;
            }
        }

        public static void Preload()
        {
            Init();
        }

        public static double FreqMhz()
        {
            Init();
            if (_freq == null) return -1;
            try
            {
                float baseMhz = _freq.NextValue();
                float perf = _perf.NextValue();
                if (baseMhz <= 0) return -1;
                return baseMhz * perf / 100.0;
            }
            catch { return -1; }
        }

        public static int LoadPercent()
        {
            Init();
            if (_time == null) return -1;
            try
            {
                float v = _time.NextValue();
                if (v < 0) return -1;
                if (v > 100) v = 100;
                return (int)Math.Round(v);
            }
            catch { return -1; }
        }

        public static string FreqText()
        {
            double mhz = FreqMhz();
            if (mhz <= 0) return "读取失败";
            if (mhz >= 1000) return (mhz / 1000.0).ToString("0.00") + " GHz";
            return ((int)Math.Round(mhz)) + " MHz";
        }

        public static string LoadText()
        {
            int v = LoadPercent();
            return v < 0 ? "读取失败" : (v + "%");
        }
    }

    // ── 系统信息 ─────────────────────────────────────────────
    static class SysInfo
    {
        const string PowerRoot = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
        const string SubBrightness = "7516b95f-f776-4464-8c53-06167f40cc99";
        const string SetDimmed = "aded5e82-b909-4619-9949-f5d71dac0bcb";
        const string SetTimeout = "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e";
        const string SetAdaptive = "fbd9aa66-9553-4097-ba44-ed6e9d65eab8";
        const string SubEnergySaver = "de830923-a562-41af-a086-e3a2c6bad2da";
        const string SetBsBrightness = "13d09884-f74e-474a-a852-b6bde8ad03a8";

        public static string Gid()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(PowerRoot))
                {
                    if (k == null) return null;
                    return Convert.ToString(k.GetValue("ActivePowerScheme"));
                }
            }
            catch { return null; }
        }

        public static string SchemeName(string gid)
        {
            try
            {
                if (string.IsNullOrEmpty(gid)) return "未知";
                using (var k = Registry.LocalMachine.OpenSubKey(PowerRoot + "\\" + gid))
                {
                    if (k == null) return gid;
                    string fn = Convert.ToString(k.GetValue("FriendlyName"));
                    if (!string.IsNullOrEmpty(fn) && !fn.StartsWith("@")) return fn;
                    return gid.Substring(0, 8) + "…";
                }
            }
            catch { return "未知"; }
        }

        public static int ReadPowerValue(string gid, string sub, string setting, int fallback)
        {
            try
            {
                if (string.IsNullOrEmpty(gid)) return fallback;
                using (var k = Registry.LocalMachine.OpenSubKey(
                    PowerRoot + "\\" + gid + "\\" + sub + "\\" + setting))
                {
                    if (k == null) return fallback;
                    object dc = k.GetValue("DCSettingIndex");
                    if (dc != null) return Convert.ToInt32(dc);
                    object ac = k.GetValue("ACSettingIndex");
                    if (ac != null) return Convert.ToInt32(ac);
                }
            }
            catch { }
            return fallback;
        }

        public static int BsCoeff(string gid) { return ReadPowerValue(gid, SubEnergySaver, SetBsBrightness, 70); }
        public static int Adaptive(string gid) { return ReadPowerValue(gid, SubBrightness, SetAdaptive, 0); }
        public static int DimmedBrightness(string gid) { return ReadPowerValue(gid, SubBrightness, SetDimmed, 50); }
        public static int DisplayTimeoutSec(string gid) { return ReadPowerValue(gid, SubBrightness, SetTimeout, 0); }

        public static string VariBrightLevel()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (k == null) return "未找到";
                    foreach (string sub in k.GetSubKeyNames())
                    {
                        using (var sk = k.OpenSubKey(sub))
                        {
                            if (sk == null) continue;
                            object v = sk.GetValue("PP_UserVariBrightLevel");
                            if (v != null) return Convert.ToString(v);
                        }
                    }
                }
            }
            catch { }
            return "未找到";
        }

        static string U16(object o)
        {
            var arr = o as ushort[];
            if (arr == null) return "";
            var sb = new StringBuilder();
            foreach (ushort u in arr)
            {
                if (u == 0) break;
                sb.Append((char)u);
            }
            return sb.ToString().Trim();
        }

        public static string MonitorModel()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("root\\WMI",
                    "SELECT ManufacturerName, UserFriendlyName FROM WmiMonitorID"))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        string name = U16(o["UserFriendlyName"]);
                        string mfg = U16(o["ManufacturerName"]);
                        if (string.IsNullOrEmpty(name)) name = mfg;
                        if (!string.IsNullOrEmpty(name)) return name;
                    }
                }
            }
            catch { }
            return "未知";
        }

        public static string MonitorMakerDate()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("root\\WMI",
                    "SELECT ManufacturerName, YearOfManufacture, WeekOfManufacture FROM WmiMonitorID"))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        string mfg = U16(o["ManufacturerName"]);
                        int y = Convert.ToInt32(o["YearOfManufacture"]);
                        int w = Convert.ToInt32(o["WeekOfManufacture"]);
                        if (y >= 1990) return mfg + " · " + y + " 年第 " + w + " 周";
                        return mfg;
                    }
                }
            }
            catch { }
            return "未知";
        }

        public static int MonitorCount()
        {
            int n = 0;
            try
            {
                using (var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorID"))
                    foreach (ManagementBaseObject o in s.Get()) n++;
            }
            catch { }
            return n;
        }

        public static string GpuName()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_VideoController"))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        string n = Convert.ToString(o["Name"]);
                        if (!string.IsNullOrEmpty(n) && !IsVirtualName(n)) return n;
                    }
                    foreach (ManagementBaseObject o in s.Get())
                        return Convert.ToString(o["Name"]);
                }
            }
            catch { }
            return "未知";
        }

        public static string GpuDriver()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(
                    "SELECT Name, DriverVersion FROM Win32_VideoController"))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        string n = Convert.ToString(o["Name"]);
                        if (!string.IsNullOrEmpty(n) && !IsVirtualName(n))
                            return Convert.ToString(o["DriverVersion"]);
                    }
                }
            }
            catch { }
            return "未知";
        }

        public static bool IsVirtualName(string n)
        {
            string[] keys = { "iddcx", "indirect", "virtual", "virtualdisplay", "oray", "luminon",
                              "todesk", "sunlogin", "向日葵", "mirror", "spacedesk", "parsec" };
            string l = n.ToLowerInvariant();
            foreach (string k in keys) if (l.Contains(k)) return true;
            return false;
        }

        public static int VirtualAdapterCount()
        {
            int n = 0;
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
                {
                    foreach (ManagementBaseObject o in s.Get())
                    {
                        string name = Convert.ToString(o["Name"]);
                        if (!string.IsNullOrEmpty(name) && IsVirtualName(name)) n++;
                    }
                }
            }
            catch { }
            return n;
        }

        static string _esCache = null;
        static DateTime _esAt = DateTime.MinValue;

        public static string EnergySaverStatus()
        {
            if (_esCache != null && (DateTime.Now - _esAt).TotalSeconds < 60) return _esCache;
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -Command \"[Windows.System.Power.PowerManager,Windows.System,ContentType=WindowsRuntime]::EnergySaverStatus\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    string s = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(2500);
                    _esCache = string.IsNullOrEmpty(s) ? "未知" : s;
                    _esAt = DateTime.Now;
                    return _esCache;
                }
            }
            catch { return "未知"; }
        }

        public static string BatteryText()
        {
            try
            {
                PowerStatus ps = SystemInformation.PowerStatus;
                int pct = (int)Math.Round(ps.BatteryLifePercent * 100);
                string mode = ps.PowerLineStatus == PowerLineStatus.Online ? "已接电源" : "使用电池";
                if (pct < 0 || pct > 100) return mode;
                return pct + "% · " + mode;
            }
            catch { return "未知"; }
        }

        public static string BatteryRemaining()
        {
            try
            {
                int sec = SystemInformation.PowerStatus.BatteryLifeRemaining;
                if (sec <= 0) return "系统未估算";
                return (sec / 3600) + " 小时 " + ((sec % 3600) / 60) + " 分";
            }
            catch { return "未知"; }
        }

        public static string OsVersion()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        string name = Convert.ToString(k.GetValue("ProductName"));
                        string disp = Convert.ToString(k.GetValue("DisplayVersion"));
                        string build = Convert.ToString(k.GetValue("CurrentBuildNumber"));
                        return name + " " + disp + " (" + build + ")";
                    }
                }
            }
            catch { }
            return Environment.OSVersion.VersionString;
        }
    }

    // ── 大号滑动开关（左=ON，右=OFF） ─────────────────────────
    class PowerToggle : Control
    {
        bool _on;
        float _pos = 1F;                 // 滑块位置：0=最左(ON)，1=最右(OFF)
        readonly Timer _anim = new Timer();
        bool _hover;

        public string Title = "";
        public string SubOn = "";
        public string SubOff = "";

        public event EventHandler CheckedChanged;

        public PowerToggle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Font = new Font("Microsoft YaHei UI", 9F);
            _anim.Interval = 12;
            _anim.Tick += (s, e) => StepAnim();
        }

        public bool Checked
        {
            get { return _on; }
            set
            {
                if (_on == value)
                {
                    _pos = value ? 0F : 1F;
                    Invalidate();
                    return;
                }
                _on = value;
                _anim.Start();
                Invalidate();
                EventHandler h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        // 不触发事件、不做动画（用于程序同步状态）
        public void SetCheckedSilent(bool value)
        {
            _on = value;
            _pos = value ? 0F : 1F;
            _anim.Stop();
            Invalidate();
        }

        void StepAnim()
        {
            float target = _on ? 0F : 1F;
            float d = target - _pos;
            if (Math.Abs(d) < 0.04F) { _pos = target; _anim.Stop(); }
            else _pos += d * 0.28F;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) Checked = !_on;
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
            int radius = r.Height / 2;

            Color track = _on ? Color.FromArgb(205, 228, 250)
                              : (_hover ? Color.FromArgb(226, 236, 248) : Color.FromArgb(235, 241, 250));
            Color border = _on ? Color.FromArgb(72, 138, 208) : Color.FromArgb(150, 178, 210);

            using (var path = RoundRect(r, radius))
            {
                using (var b = new SolidBrush(track)) g.FillPath(b, path);
                using (var p = new Pen(border, _on ? 2F : 1F)) g.DrawPath(p, path);
            }

            int knob = r.Height - 12;
            int minX = r.Left + 6;
            int maxX = r.Right - 6 - knob;
            int kx = (int)Math.Round(minX + (maxX - minX) * _pos);
            int ky = r.Top + 6;
            using (var kp = RoundRect(new Rectangle(kx, ky, knob, knob), knob / 2))
            {
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, kp);
                using (var p = new Pen(Color.FromArgb(110, 152, 196), 1.4F)) g.DrawPath(p, kp);
            }

            // 滑块里的 ON / OFF
            string st = _on ? "ON" : "OFF";
            using (var f = new Font(Font.FontFamily, 8.5F, FontStyle.Bold))
            using (var b = new SolidBrush(_on ? Color.FromArgb(24, 90, 160) : Color.FromArgb(120, 136, 154)))
            {
                SizeF sz = g.MeasureString(st, f);
                g.DrawString(st, f, b, kx + (knob - sz.Width) / 2F, ky + (knob - sz.Height) / 2F);
            }

            // 标题 + 状态说明
            int textLeft = 16, textRight = Width - 16;
            var area = new Rectangle(textLeft, 6, Math.Max(40, textRight - textLeft), Height - 12);
            using (var f = new Font(Font.FontFamily, 11.5F, FontStyle.Bold))
            using (var b = new SolidBrush(_on ? Color.FromArgb(12, 56, 104) : Color.FromArgb(48, 66, 88)))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(Title, f, b, new Rectangle(area.Left, area.Top, area.Width, area.Height - 14), sf);
            }
            using (var f = new Font(Font.FontFamily, 8.5F))
            using (var b = new SolidBrush(_on ? Color.FromArgb(36, 96, 152) : Color.FromArgb(128, 142, 158)))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                string sub = _on ? SubOn : SubOff;
                g.DrawString(sub, f, b, new Rectangle(area.Left, area.Bottom - 18, area.Width, 18), sf);
            }
        }

        static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = Math.Max(2, radius * 2);
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ── 左侧导航项（图标 + 文字，选中为浅蓝胶囊） ─────────────
    class NavButton : Control
    {
        public string Glyph = "";
        public bool Active;
        bool _hover;

        public NavButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) OnClick(EventArgs.Empty);
            base.OnMouseDown(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle area = new Rectangle(8, 2, Width - 16, Height - 4);

            Color back = Active ? Color.FromArgb(205, 228, 250)
                        : (_hover ? Color.FromArgb(233, 241, 252) : Color.Empty);
            if (back != Color.Empty)
            {
                using (var p = RoundRect(area, 8))
                using (var b = new SolidBrush(back))
                    g.FillPath(b, p);
            }

            Color fore = Active ? Color.FromArgb(18, 66, 116) : Color.FromArgb(64, 70, 82);
            if (Glyph.Length > 0)
            {
                using (var f = new Font("Segoe MDL2 Assets", 11F))
                using (var b = new SolidBrush(fore))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(Glyph, f, b, new Rectangle(area.Left + 4, area.Top, 30, area.Height), sf);
                }
            }
            using (var f = new Font(Font.FontFamily, 10F, Active ? FontStyle.Bold : FontStyle.Regular))
            using (var b = new SolidBrush(fore))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
                g.DrawString(Text, f, b, new Rectangle(area.Left + 40, area.Top, area.Width - 44, area.Height), sf);
            }
        }

        static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = Math.Max(2, radius * 2);
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ── 一键模式的进度窗与执行器 ─────────────────────────────
    // work：在后台线程执行的系统改动；step：报告当前步骤文案
    delegate void ModeWork(List<string> errs, Action<string> step);
    delegate void ModeFinish(List<string> errs);

    class ModeProgressForm : Form
    {
        readonly Label _lblTitle = new Label();
        readonly Label _lblStep = new Label();
        readonly Label _lblClock = new Label();
        readonly Label _lblHint = new Label();
        readonly ProgressBar _bar = new ProgressBar();
        readonly Timer _timer = new Timer();
        readonly int _total;
        int _done;
        int _elapsed;
        bool _allowClose;

        public ModeProgressForm(string title, int totalSteps)
        {
            _total = Math.Max(1, totalSteps);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.White;
            ClientSize = new Size(404, 196);
            Font = new Font("Microsoft YaHei UI", 9F);

            var head = new Panel { BackColor = Color.FromArgb(240, 246, 253), Dock = DockStyle.Top, Height = 50 };
            _lblTitle.Text = title;
            _lblTitle.Font = new Font(Font.FontFamily, 11.5F, FontStyle.Bold);
            _lblTitle.ForeColor = Color.FromArgb(20, 70, 130);
            _lblTitle.SetBounds(16, 14, 372, 24);
            head.Controls.Add(_lblTitle);
            Controls.Add(head);

            _lblStep.Text = "正在准备，请稍候…";
            _lblStep.SetBounds(16, 62, 372, 20);
            Controls.Add(_lblStep);

            _bar.SetBounds(16, 88, 372, 16);
            _bar.Minimum = 0;
            _bar.Maximum = 100;
            _bar.Style = ProgressBarStyle.Continuous;
            Controls.Add(_bar);

            _lblClock.SetBounds(16, 112, 372, 20);
            _lblClock.ForeColor = Color.FromArgb(34, 92, 150);
            Controls.Add(_lblClock);

            _lblHint.Text = "正在修改系统电源设置，期间界面可能轻微卡顿，请勿重复点击。";
            _lblHint.SetBounds(16, 140, 372, 20);
            _lblHint.ForeColor = Color.Gray;
            Controls.Add(_lblHint);

            _timer.Interval = 1000;
            _timer.Tick += delegate { _elapsed++; UpdateClock(); };
            Shown += delegate { UpdateClock(); _timer.Start(); };
            UpdateClock();
        }

        // 供自检使用
        internal string StepText { get { return _lblStep.Text; } }
        internal string ClockText { get { return _lblClock.Text; } }
        internal int BarValue { get { return _bar.Value; } }

        void UpdateClock()
        {
            if (_elapsed <= 10)
                _lblClock.Text = "已等待 " + _elapsed + " 秒 · 预计还需 " + (10 - _elapsed) + " 秒";
            else
                _lblClock.Text = "已等待 " + _elapsed + " 秒 · 正在收尾，请再稍等…";
        }

        public void StepForward(string text)
        {
            _done++;
            if (_done > _total) _done = _total;
            _lblStep.Text = "（" + _done + "/" + _total + "）" + text;
            int pct = (int)Math.Round(100.0 * _done / _total);
            if (pct > _bar.Value) _bar.Value = Math.Min(100, pct);
        }

        public void FinishNow()
        {
            _timer.Stop();
            _allowClose = true;
            try { _bar.Value = 100; } catch { }
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Color.FromArgb(122, 162, 206)))
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }

        // 执行期间屏蔽 Esc / Alt+F4，避免用户中途关掉对话框
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            return true;
        }
    }

    static class ModeRunner
    {
        public static string ConfirmText(bool perf)
        {
            if (perf)
                return "即将「释放全部性能」。\r\n\r\n"
                     + "整个过程大约需要 10 秒：程序要逐项修改电源模式、处理器上限、散热策略和节电设置，"
                     + "期间鼠标和界面可能短暂卡顿，这是正常现象，请不要重复点击。\r\n\r\n"
                     + "完成后会弹窗告诉你结果。\r\n\r\n是否现在开始？";
            return "即将开启「超级节能」。\r\n\r\n"
                 + "整个过程大约需要 10 秒：程序要逐项修改电源计划、处理器上限、散热策略、睿频和节电设置，"
                 + "期间鼠标和界面可能短暂卡顿，这是正常现象，请不要重复点击。\r\n\r\n"
                 + "完成后会弹窗告诉你结果。\r\n\r\n是否现在开始？";
        }

        // 显示进度窗 → 后台线程执行 work → 关闭进度窗 → 回到界面线程执行 finish
        public static void Run(IWin32Window owner, string title, int totalSteps, ModeWork work, ModeFinish finish)
        {
            var errs = new List<string>();
            using (var dlg = new ModeProgressForm(title, totalSteps))
            {
                var ready = new System.Threading.ManualResetEvent(false);
                dlg.Shown += delegate { ready.Set(); };

                var th = new System.Threading.Thread(delegate()
                {
                    try
                    {
                        ready.WaitOne();
                        work(errs, delegate(string text)
                        {
                            try { dlg.BeginInvoke(new Action(delegate { dlg.StepForward(text); })); }
                            catch { }
                        });
                    }
                    catch (Exception ex)
                    {
                        errs.Add(ex.Message);
                    }
                    finally
                    {
                        try { dlg.BeginInvoke(new Action(delegate { dlg.FinishNow(); })); }
                        catch { }
                    }
                });
                th.IsBackground = true;
                th.Start();

                dlg.ShowDialog(owner);
                th.Join(3000);
            }

            finish(errs);
        }
    }

    // ── 主窗口 ───────────────────────────────────────────────
    class MainForm : Form
    {
        readonly Label _lblBig = new Label();
        readonly TrackBar _track = new TrackBar();
        readonly Button _btnApply = new Button();
        readonly ComboBox _target = new ComboBox();
        readonly List<MonitorTarget> _targets = new List<MonitorTarget>();
        readonly CheckBox _chkBoth = new CheckBox();
        readonly CheckBox _chkAuto = new CheckBox();
        readonly ComboBox _logFilter = new ComboBox();

        // 标签宽度按这个字体实测，保证右缘不会压到后面的下拉框边框
        static readonly Font CaptionFont = PickFont();

        readonly ComboBox _plan = new ComboBox();
        readonly ComboBox _overlay = new ComboBox();
        readonly ComboBox _acOff = new ComboBox();
        readonly ComboBox _acSleep = new ComboBox();
        readonly ComboBox _acMaxCpu = new ComboBox();
        readonly ComboBox _dcOff = new ComboBox();
        readonly ComboBox _dcSleep = new ComboBox();
        readonly CheckBox _chkAutoBright = new CheckBox();
        readonly ComboBox _acBrightLevel = new ComboBox();
        readonly ComboBox _dcBrightLevel = new ComboBox();
        readonly Button _btnElevate = new Button();
        readonly Button _btnApplyPower = new Button();
        readonly Button _btnUndoPower = new Button();
        readonly Button _btnSyncAc = new Button();
        readonly Button _btnSyncDc = new Button();
        readonly Button _btnRestoreAllPower = new Button();
        readonly PowerToggle _tgPerf = new PowerToggle();
        readonly PowerToggle _tgSaver = new PowerToggle();
        bool _modeSyncing;
        readonly Label _lblPowerState = new Label();
        readonly Label _lblPowerMsg = new Label();
        readonly CheckBox _chkLock = new CheckBox();
        readonly ComboBox _lockAc = new ComboBox();
        readonly ComboBox _lockDc = new ComboBox();
        readonly Button _btnLockRestore = new Button();
        readonly Button _btnLockApply = new Button();
        readonly CheckBox _chkSaver = new CheckBox();
        PowerState _powerState;
        bool _powerSyncing;
        bool _lockSyncing;
        bool _lastOnline;
        bool _havePowerSource;

        readonly Dictionary<string, Label> _info = new Dictionary<string, Label>();
        readonly Label _msg = new Label();
        readonly ListView _list = new ListView();
        readonly Label _lblLogPath = new Label();

        // 名词提示（鼠标悬停显示解释）
        readonly ToolTip _tip = new ToolTip();
        bool _tipsOn = true;

        // 左侧导航与分页
        readonly Panel _pageHost = new Panel();
        readonly Label _navMode = new Label();
        readonly Label _navPower = new Label();
        NavButton[] _navs;
        Panel[] _pages;

        NotifyIcon _tray;
        bool _exiting;
        bool _msgSticky;

        Timer _debounce;
        Timer _poll;
        Timer _retry;
        Timer _flash;

        bool _syncing;
        bool _busy;
        bool _polling;
        int _retryLeft = 3;
        int _lastKnown = -1;
        int _extKnown = -1;

        public MainForm()
        {
            Text = Program.AppName + " " + Program.Version;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(1080, 740);
            MinimumSize = new Size(1090, 660);
            Font = PickFont();
            BackColor = Color.FromArgb(250, 250, 252);
            Icon = MakeIcon();

            _tipsOn = UiFile.Get("tips", "1") != "0";
            _tip.ShowAlways = true;
            _tip.InitialDelay = 350;
            _tip.ReshowDelay = 100;
            _tip.AutoPopDelay = 30000;
            _tip.Active = _tipsOn;

            BuildLayout();
            BuildTray();

            LogStore.Cleanup(30);

            _debounce = new Timer { Interval = 350 };
            _debounce.Tick += (s, e) => { _debounce.Stop(); ApplyBrightness(_track.Value); };

            _poll = new Timer { Interval = 2000 };
            _poll.Tick += (s, e) => { PollBrightness(); CheckPowerSource(); UpdateCpuMeter(); };
            System.Threading.ThreadPool.QueueUserWorkItem(delegate { CpuMeter.Preload(); });

            _retry = new Timer { Interval = 1500 };
            _retry.Tick += (s, e) => { _retry.Stop(); RefreshStatus(); };

            _flash = new Timer { Interval = 2200 };
            _flash.Tick += (s, e) => { _flash.Stop(); if (!_msgSticky) _msg.Text = ""; };

            Shown += (s, e) =>
            {
                FitToScreen();
                ReloadTargets();
                SyncFromTarget();
                RefreshStatus();
                RefreshPowerUi();
                FixComboDropDown(this);
                int st0 = PowerCtl.PowerLineState();
                if (st0 >= 0)
                {
                    _lastOnline = st0 == 1;
                    _havePowerSource = true;
                }
                EnforceLock(PowerCtl.OnAc());
                LoadLogs();
                _poll.Start();

                // 刚开机时电源服务可能还没就绪（powercfg 返回空），1.5 秒后再校准一次
                var warmup = new System.Windows.Forms.Timer { Interval = 1500 };
                warmup.Tick += (s2, e2) =>
                {
                    warmup.Stop();
                    warmup.Dispose();
                    try { RefreshPowerUi(); RefreshStatus(); } catch { }
                };
                warmup.Start();
            };

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }

        // 下拉列表默认可能沿用控件创建时的旧宽度（之后 SetBounds 改宽度就会错位），
        // 统一显式设成控件当前宽度，保证展开列表与下拉框左右对齐。
        void FixComboDropDown(Control root)
        {
            foreach (Control c in root.Controls)
            {
                ComboBox cb = c as ComboBox;
                if (cb != null)
                {
                    // DropDownWidth 就是展开列表的窗口宽度，与控件同宽即完全对齐
                    try { cb.DropDownWidth = cb.Width; } catch { }
                    StyleCombo(cb);
                }
                else
                {
                    FixComboDropDown(c);
                }
            }
        }

        // 本机主题把 DropDownList 的闭合框画成浅灰底（像被禁用），这里改成自绘白底，
        // 文字垂直居中，禁用时才用浅灰底 + 灰字。
        static void StyleCombo(ComboBox cb)
        {
            cb.DrawMode = DrawMode.OwnerDrawFixed;
            cb.DrawItem -= ComboDrawItem;
            cb.DrawItem += ComboDrawItem;
        }

        static void ComboDrawItem(object sender, DrawItemEventArgs e)
        {
            ComboBox cb = sender as ComboBox;
            if (cb == null) return;

            bool onEdit = (e.State & DrawItemState.ComboBoxEdit) == DrawItemState.ComboBoxEdit;
            bool disabled = !cb.Enabled;
            bool selected = !onEdit && (e.State & DrawItemState.Selected) == DrawItemState.Selected;

            Color back = Color.White;
            if (disabled) back = Color.FromArgb(243, 243, 243);
            else if (selected) back = Color.FromArgb(204, 228, 247);

            using (var brush = new SolidBrush(back))
                e.Graphics.FillRectangle(brush, e.Bounds);

            if (e.Index >= 0 && e.Index < cb.Items.Count)
            {
                string text = cb.GetItemText(cb.Items[e.Index]);
                Color fore = disabled ? Color.FromArgb(150, 150, 155) : Color.FromArgb(32, 32, 32);
                // 闭合框里 e.Bounds 已经把箭头区域挖掉了，窄框放不下「80%」「15 分钟」这类文字，
                // 所以闭合状态按控件整宽留白，展开列表沿用条目自己的宽度。
                int avail = onEdit ? Math.Max(6, cb.ClientSize.Width - 8) : Math.Max(6, e.Bounds.Width - 3);
                Rectangle box = new Rectangle(e.Bounds.X + 3, e.Bounds.Y, avail, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, text, cb.Font, box, fore,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            if (!onEdit && !disabled
                && (e.State & DrawItemState.Focus) == DrawItemState.Focus
                && (e.State & DrawItemState.NoFocusRect) == 0)
            {
                e.DrawFocusRectangle();
            }
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            try
            {
                if (e.Mode != PowerModes.StatusChange) return;
                BeginInvoke((MethodInvoker)CheckPowerSource);
            }
            catch { }
        }

        static Font PickFont()
        {
            try { return new Font("Microsoft YaHei UI", 9F); }
            catch
            {
                try { return new Font("Microsoft YaHei", 9F); }
                catch { return SystemFonts.DefaultFont; }
            }
        }

        void FitToScreen()
        {
            try
            {
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                int w = Math.Min(Width, wa.Width - 16);
                int h = Math.Min(Height, wa.Height - 16);
                Size = new Size(w, h);
                Location = new Point(wa.Left + (wa.Width - w) / 2,
                                     wa.Top + (wa.Height - h) / 2);
            }
            catch (Exception ex) { try { LogStore.Write("WARN", "fit fail: " + ex.Message); } catch { } }
        }

        void BuildLayout()
        {
            SuspendLayout();

            _pageHost.Dock = DockStyle.Fill;
            _pageHost.Padding = new Padding(12, 10, 12, 10);
            _pageHost.BackColor = Color.FromArgb(250, 250, 252);

            _pages = new Panel[5];
            _pages[0] = MakePage(BuildQuickPage());
            _pages[1] = MakePage(BuildBrightnessPage());
            _pages[2] = MakePage(BuildPowerPanel());
            _pages[3] = MakePage(BuildLogPanel());
            _pages[4] = MakePage(BuildSettingsPage());
            foreach (Panel p in _pages)
            {
                p.Visible = false;
                _pageHost.Controls.Add(p);
            }

            Controls.Add(_pageHost);
            Controls.Add(BuildSidebar());
            ShowPage(0);

            ResumeLayout();
        }

        static Panel MakePage(Control content)
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            content.Dock = DockStyle.Fill;
            page.Controls.Add(content);
            return page;
        }

        Control BuildSidebar()
        {
            var side = new Panel { Dock = DockStyle.Left, Width = 168, BackColor = Color.FromArgb(246, 248, 252) };

            var brand = new Label
            {
                Text = Program.AppName,
                Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
                ForeColor = Color.FromArgb(28, 40, 58),
                AutoSize = false
            };
            brand.SetBounds(20, 16, 132, 26);

            var ver = new Label
            {
                Text = "版本 " + Program.Version,
                ForeColor = Color.Gray,
                AutoSize = false
            };
            ver.SetBounds(20, 42, 132, 18);

            var sep = new Panel { BackColor = Color.FromArgb(226, 232, 240) };
            sep.SetBounds(16, 68, 136, 1);

            string[,] items = new string[,]
            {
                { "\uE945", "快速设置" },
                { "\uE706", "亮度" },
                { "\uE7E8", "电源" },
                { "\uE81C", "日志" },
                { "\uE713", "设置" }
            };

            _navs = new NavButton[items.GetLength(0)];
            for (int i = 0; i < _navs.Length; i++)
            {
                var nb = new NavButton { Text = items[i, 1], Glyph = items[i, 0] };
                nb.SetBounds(8, 78 + i * 50, 152, 44);
                int idx = i;
                nb.Click += (s, e) => ShowPage(idx);
                _navs[i] = nb;
                side.Controls.Add(nb);
            }

            _navMode.Text = "当前：常规";
            _navMode.ForeColor = Color.FromArgb(90, 96, 108);
            _navMode.AutoSize = false;
            _navMode.SetBounds(20, 0, 132, 20);

            _navPower.Text = "电源：--";
            _navPower.ForeColor = Color.FromArgb(120, 126, 138);
            _navPower.AutoSize = false;
            _navPower.SetBounds(20, 0, 132, 20);

            side.Controls.Add(brand);
            side.Controls.Add(ver);
            side.Controls.Add(sep);
            side.Controls.Add(_navMode);
            side.Controls.Add(_navPower);

            side.Resize += (s, e) =>
            {
                _navPower.Top = side.ClientSize.Height - 30;
                _navMode.Top = _navPower.Top - 22;
            };
            side.Paint += (s, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(230, 235, 242)))
                    e.Graphics.DrawLine(pen, side.ClientSize.Width - 1, 0, side.ClientSize.Width - 1, side.ClientSize.Height);
            };

            return side;
        }

        void ShowPage(int index)
        {
            if (_pages == null || index < 0 || index >= _pages.Length) return;
            for (int i = 0; i < _pages.Length; i++)
            {
                _pages[i].Visible = i == index;
                if (_navs != null && i < _navs.Length)
                {
                    _navs[i].Active = i == index;
                    _navs[i].Invalidate();
                }
            }
            _pageHost.PerformLayout();
        }

        // 亮度页：亮度控制 + 联动开关
        Control BuildBrightnessPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

            var opts = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 44,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(2, 10, 0, 0)
            };

            _chkBoth.Text = "两块屏一起调";
            _chkBoth.AutoSize = true;
            _chkBoth.Margin = new Padding(4, 0, 20, 0);
            _chkBoth.CheckedChanged += (s, e) =>
            {
                _target.Enabled = !_chkBoth.Checked;
                if (_chkBoth.Checked)
                {
                    Flash("已开启联动：滑杆、预设、快捷键会同时作用于内置屏和外接屏。", false);
                }
                else
                {
                    SyncFromTarget();
                    _msg.Text = "";
                }
            };

            var hint = new Label
            {
                Text = "快捷键 Ctrl+Alt+↑ / ↓ 微调 5%",
                AutoSize = true,
                ForeColor = Color.Gray,
                Margin = new Padding(6, 2, 0, 0)
            };

            opts.Controls.Add(_chkBoth);
            opts.Controls.Add(hint);

            Tip(_chkBoth, "勾选后，滑杆、预设按钮、快捷键会同时调整内置屏和外接屏。\r\n取消勾选时只调整「控制目标」里选中的那一块屏。");
            Tip(hint, "全局快捷键：按住 Ctrl+Alt 再按 ↑ / ↓，可把亮度提高/降低 5%，不用先打开窗口。");

            Control top = BuildTopPanel();
            top.Dock = DockStyle.Top;
            top.Height = 190;

            page.Controls.Add(opts);
            page.Controls.Add(top);
            return page;
        }

        // 设置页：开机自启、快捷键与托盘说明
        Control BuildSettingsPage()
        {
            var gb = new GroupBox { Text = "程序设置", Dock = DockStyle.Fill, Padding = new Padding(14) };

            _chkAuto.Text = "开机自启";
            _chkAuto.AutoSize = false;
            _chkAuto.SetBounds(18, 32, 140, 24);
            _chkAuto.Checked = AutoStart.Enabled();
            _chkAuto.CheckedChanged += (s, e) =>
            {
                AutoStart.Set(_chkAuto.Checked);
                LogStore.Write("CONFIG", "autostart=" + (_chkAuto.Checked ? "on" : "off"));
                Flash(_chkAuto.Checked ? "已设置开机自启。" : "已取消开机自启。", false);
            };

            var chkTips = new CheckBox
            {
                Text = "名词提示（鼠标悬停看解释）",
                AutoSize = false
            };
            chkTips.SetBounds(250, 32, 250, 24);
            chkTips.Checked = _tipsOn;
            chkTips.CheckedChanged += (s, e) =>
            {
                _tipsOn = chkTips.Checked;
                _tip.Active = _tipsOn;
                UiFile.Set("tips", _tipsOn ? "1" : "0");
                Flash(_tipsOn ? "已开启名词提示：把鼠标停在任何设置项上即可看解释。" : "已关闭名词提示。", false);
            };
            Tip(chkTips, "开启后，把鼠标停在按钮、下拉框、状态项目上约 0.3 秒，就会弹出技术名词的详细解释。\r\n关闭后所有提示都不再弹出。");

            var l1 = new Label
            {
                Text = "快捷键：Ctrl + Alt + ↑ / ↓ 微调 5%",
                AutoSize = false,
                ForeColor = Color.FromArgb(70, 74, 84)
            };
            l1.SetBounds(18, 72, 460, 22);

            var l2 = new Label
            {
                Text = "关闭窗口后驻留托盘，双击托盘图标可再次打开。",
                AutoSize = false,
                ForeColor = Color.FromArgb(70, 74, 84)
            };
            l2.SetBounds(18, 100, 520, 22);

            var l3 = new Label
            {
                Text = "「两块屏一起调」开关在「亮度」页；插拔自动锁定在「电源」页；一键模式和系统状态都在首页「快速设置」。",
                AutoSize = false,
                ForeColor = Color.Gray
            };
            l3.SetBounds(18, 128, 640, 22);

            var cap = new Label
            {
                Text = "系统入口",
                AutoSize = false,
                ForeColor = Color.DimGray
            };
            cap.SetBounds(18, 170, 80, 20);

            var btnPower = MakeButton("电源选项", 96, (s, e) => StartSafe("control.exe", "powercfg.cpl"));
            btnPower.Location = new Point(18, 194);
            var btnDisp = MakeButton("系统显示设置", 116, (s, e) => StartSafe("explorer.exe", "ms-settings:display"));
            btnDisp.Location = new Point(122, 194);

            Tip(_chkAuto, "开机时自动启动本程序并驻留托盘，方便用快捷键调亮度。");
            Tip(btnPower, "打开 Windows 自带的「电源选项」控制面板。");
            Tip(btnDisp, "打开 Windows 的「显示设置」，可调整分辨率、缩放、多显示器排列等。");
            Tip(l1, "全局快捷键：按住 Ctrl+Alt 再按 ↑ / ↓，亮度提高/降低 5%。");
            Tip(l2, "点窗口右上角关闭时程序不退出，而是缩到托盘继续运行；双击托盘图标可重新打开。");

            gb.Controls.Add(_chkAuto);
            gb.Controls.Add(chkTips);
            gb.Controls.Add(l1);
            gb.Controls.Add(l2);
            gb.Controls.Add(l3);
            gb.Controls.Add(cap);
            gb.Controls.Add(btnPower);
            gb.Controls.Add(btnDisp);
            return gb;
        }

        // 快速设置：一键模式（顶部）+ 系统状态（下方），由原「一键模式」「状态」两页合并
        Control BuildQuickPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

            Control info = BuildInfoPanel();
            page.Controls.Add(info);
            page.Controls.Add(BuildStatusBar());

            var tip = new Label
            {
                Text = "两个开关互斥，开一个会自动关掉另一个；关闭时还原到打开前的状态。\r\n"
                     + "释放全部性能：处理器 100%、散热主动、关闭插电节电。\r\n"
                     + "超级节能：处理器限 30%、散热被动、关闭睿频，插电和电池都锁最省电。\r\n"
                     + "点击后大约需要 10 秒生效，期间会弹出进度窗并显示倒计时，请勿重复点击。",
                ForeColor = Color.Gray,
                AutoSize = false,
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(2, 0, 0, 0),
                Dock = DockStyle.Top,
                Height = 68
            };
            Tip(tip, "散热主动 / 被动：Windows 的散热策略。主动＝优先提高风扇转速再降频；被动＝尽量不加速风扇，先降频。\r\n睿频（Turbo Boost）：CPU 在功耗和温度允许时自动超出标称频率运行，关闭后峰值性能下降，但更省电、更凉。");
            page.Controls.Add(tip);

            page.Controls.Add(BuildModeToggles());
            return page;
        }

        // 快速设置页顶部的诊断动作条（原状态页的按钮）
        Control BuildStatusBar()
        {
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 44,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(2, 8, 0, 0)
            };
            var bVbOff = MakeButton("关闭 Vari-Bright（释放性能）", 208, (s, e) => RunFix("-Target VariBright -Apply"));
            var bVbOn = MakeButton("恢复 Vari-Bright（开启节能）", 208, (s, e) => RunFix("-Target VariBright -Restore -Apply"));
            var bRefresh = MakeButton("刷新全部", 96, (s, e) =>
            {
                _retryLeft = 3;
                ReloadTargets();
                SyncFromTarget();
                RefreshStatus();
                LoadLogs();
            });
            var bReport = MakeButton("导出诊断报告", 120, (s, e) => ExportReport());

            Tip(bRefresh, "重新读取屏幕、亮度、电源方案、CPU 频率/占用等信息，并刷新日志列表。");
            Tip(bReport, "把当前系统状态和最近操作记录导出成 txt 并自动打开，便于排查问题。\r\n报告里不含计算机名和用户名。");
            Tip(bVbOff, "关闭 Vari-Bright，释放性能：把等级设为 0，不再自动压暗画面。\r\n画面更亮、更准，对比度更好，适合看图、看视频、做设计；代价是耗电略增。\r\n\r\n修改需要管理员权限，程序会先备份原值，可用「恢复 Vari-Bright（开启节能）」还原。");
            Tip(bVbOn, "恢复 Vari-Bright，开启节能：把等级恢复到修改前（通常是出厂默认的开启状态）。\r\n系统会按功耗和画面内容自动降低背光电流、压低对比度，更省电；副作用是画面偏灰、偏暗、色彩变淡。");

            bar.Controls.Add(bVbOff);
            bar.Controls.Add(bVbOn);
            bar.Controls.Add(bRefresh);
            bar.Controls.Add(bReport);
            return bar;
        }

        Control BuildTopPanel()
        {
            var gb = new GroupBox { Text = "屏幕亮度", Dock = DockStyle.Fill, Padding = new Padding(10) };

            _lblBig.Text = "--%";
            _lblBig.Font = new Font(Font.FontFamily, 30F, FontStyle.Bold);
            _lblBig.ForeColor = Color.FromArgb(30, 30, 40);
            _lblBig.SetBounds(14, 16, 170, 58);
            _lblBig.TextAlign = ContentAlignment.MiddleLeft;

            var cap = new Label
            {
                Text = "当前亮度",
                ForeColor = Color.DimGray,
                AutoSize = false
            };
            cap.SetBounds(18, 76, 170, 20);

            _track.Minimum = 0;
            _track.Maximum = 100;
            _track.TickFrequency = 10;
            _track.SmallChange = 1;
            _track.LargeChange = 10;
            _track.SetBounds(196, 12, 370, 56);
            _track.ValueChanged += (s, e) =>
            {
                _lblBig.Text = _track.Value + "%";
                if (_syncing) return;
                _debounce.Stop();
                _debounce.Start();
            };

            var btnMinus = new Button
            {
                Text = "-5",
                FlatStyle = FlatStyle.System
            };
            btnMinus.SetBounds(574, 22, 34, 32);
            btnMinus.Click += (s, e) => Nudge(-5);

            var btnPlus = new Button
            {
                Text = "+5",
                FlatStyle = FlatStyle.System
            };
            btnPlus.SetBounds(612, 22, 34, 32);
            btnPlus.Click += (s, e) => Nudge(5);

            _btnApply.Text = "应用";
            _btnApply.SetBounds(652, 22, 92, 40);
            _btnApply.FlatStyle = FlatStyle.System;
            _btnApply.Click += (s, e) => { _debounce.Stop(); ApplyBrightness(_track.Value); };

            var presetFlow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = false
            };
            presetFlow.SetBounds(14, 98, 570, 40);

            _msg.AutoSize = false;
            _msg.AutoEllipsis = true;
            _msg.SetBounds(196, 72, 600, 20);
            _msg.ForeColor = Color.Firebrick;
            _msg.Text = "";

            var capTarget = new Label
            {
                Text = "控制目标",
                ForeColor = Color.DimGray,
                AutoSize = false
            };
            capTarget.SetBounds(598, 104, 58, 20);

            _target.DropDownStyle = ComboBoxStyle.DropDownList;
            _target.SetBounds(660, 100, 190, 26);
            _target.SelectedIndexChanged += (s, e) =>
            {
                if (_syncing) return;
                SyncFromTarget();
                RefreshStatus();
            };

            int[] presets = new int[] { 100, 90, 80, 70, 60, 50 };
            foreach (int v in presets)
            {
                var b = new Button
                {
                    Text = v + "%",
                    Size = new Size(88, 32),
                    Margin = new Padding(0, 0, 8, 0),
                    FlatStyle = FlatStyle.System
                };
                int level = v;
                b.Click += (s, e) => { _debounce.Stop(); ApplyBrightness(level); };
                Tip(b, "把当前控制目标的亮度直接设为 " + v + "%。");
                presetFlow.Controls.Add(b);
            }

            Tip(_lblBig, "当前控制目标的亮度百分比。");
            Tip(cap, "当前控制目标的亮度百分比。内置屏走系统 WMI 接口，外接屏走 DDC/CI。");
            Tip(_track, "拖动滑杆设置亮度，松手约 0.4 秒后自动应用；也可以拖动后点「应用」。");
            Tip(btnMinus, "在当前亮度基础上减 5%。");
            Tip(btnPlus, "在当前亮度基础上加 5%。");
            Tip(_btnApply, "立即把滑杆上的亮度写入当前控制目标。");
            Tip(capTarget, "选择要调节哪一块屏：内置屏用 WMI 调背光；外接屏用 DDC/CI（显示器需支持亮度控制）。");
            Tip(_target, "内置屏：通过 Windows 的 WMI 亮度接口调节背光。\r\n外接屏：通过 DDC/CI 指令经显卡接口调节，需要显示器支持，部分显示器只能读不能写。");

            gb.Controls.Add(_lblBig);
            gb.Controls.Add(cap);
            gb.Controls.Add(_track);
            gb.Controls.Add(btnMinus);
            gb.Controls.Add(btnPlus);
            gb.Controls.Add(_btnApply);
            gb.Controls.Add(presetFlow);
            gb.Controls.Add(_msg);
            gb.Controls.Add(capTarget);
            gb.Controls.Add(_target);
            return gb;
        }

        // ── 电源与插电设置 ──────────────────────────────────
        static readonly int[] TimeValues = new int[] { 60, 120, 180, 240, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 0 };
        static readonly int[] CpuValues = new int[] { 100, 95, 90, 85, 80, 75, 70, 60, 50, 30 };
        static readonly int[] LevelValues = new int[] { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10 };

        static string TimeText(int sec)
        {
            if (sec <= 0) return "从不";
            return (sec / 60) + " 分钟";
        }

        static int TimeValue(ComboBox box)
        {
            int i = box.SelectedIndex;
            if (i < 0 || i >= TimeValues.Length) return -1;
            return TimeValues[i];
        }

        static int CpuValue(ComboBox box)
        {
            int i = box.SelectedIndex;
            if (i < 0 || i >= CpuValues.Length) return -1;
            return CpuValues[i];
        }

        static int LevelValue(ComboBox box)
        {
            int i = box.SelectedIndex;
            if (i < 0 || i >= LevelValues.Length) return 80;
            return LevelValues[i];
        }

        static int FindTimeIndex(int sec)
        {
            for (int i = 0; i < TimeValues.Length; i++)
                if (TimeValues[i] == sec) return i;
            if (sec <= 0) return TimeValues.Length - 1;
            return TimeValues.Length - 2;   // 不在列表里就取 180 分钟
        }

        static int FindCpuIndex(int pct)
        {
            for (int i = 0; i < CpuValues.Length; i++)
                if (CpuValues[i] == pct) return i;
            return 0;
        }

        static int FindLevelIndex(int pct)
        {
            for (int i = 0; i < LevelValues.Length; i++)
                if (LevelValues[i] == pct) return i;
            return 2;
        }

        // 并排两个大号滑动开关：左=ON，右=OFF；两者互斥（快速设置页顶部）
        Control BuildModeToggles()
        {
            var panel = new Panel { Dock = DockStyle.Top, Height = 72 };

            _tgPerf.Title = "释放全部性能";
            _tgPerf.SubOn = "已开启 · 插电不限频";
            _tgPerf.SubOff = "已关闭 · 点一下开启";
            _tgPerf.CheckedChanged += (s, e) => OnModeToggled(_tgPerf, true);

            _tgSaver.Title = "超级节能";
            _tgSaver.SubOn = "已开启 · 插电/电池都锁最省";
            _tgSaver.SubOff = "已关闭 · 点一下开启";
            _tgSaver.CheckedChanged += (s, e) => OnModeToggled(_tgSaver, false);

            panel.Controls.Add(_tgPerf);
            panel.Controls.Add(_tgSaver);

            Tip(_tgPerf, "释放全部性能（会先备份当前设置，关闭开关即还原）：\r\n• 最小/最大处理器状态都设为 100%，不降频\r\n• 散热方式设为「主动」：风扇更积极\r\n• 睿频策略恢复为允许加速\r\n• 关闭插电时的节电模式限制\r\n适合插电做重活；更耗电、更热、风扇更响。");
            Tip(_tgSaver, "超级节能（会先备份当前设置，关闭开关即还原）：\r\n• 最大处理器状态限制为 30%，最小 5%\r\n• 散热方式设为「被动」：尽量不加速风扇，先降频\r\n• 关闭睿频（Turbo Boost），不让 CPU 临时超频\r\n• 插电和电池都锁定最省电的电源模式\r\n适合长时间离电使用，性能会明显下降。");

            panel.Resize += (s, e) =>
            {
                int gap = 12;
                int w = Math.Max(240, (panel.ClientSize.Width - gap) / 2);
                _tgPerf.SetBounds(0, 0, w, 72);
                _tgSaver.SetBounds(w + gap, 0, w, 72);
            };

            return panel;
        }

        void OnModeToggled(PowerToggle src, bool perf)
        {
            if (_modeSyncing) return;
            if (src.Checked) BeginSetMode(perf);
            else BeginRestoreMode();
        }

        // 打开某个模式：先提示约 10 秒的等待，再后台执行并显示进度，完成后弹窗告知结果
        void BeginSetMode(bool perf)
        {
            PowerToggle self = perf ? _tgPerf : _tgSaver;
            string name = perf ? "释放全部性能" : "超级节能";

            DialogResult ans = MessageBox.Show(this, ModeRunner.ConfirmText(perf), "开启" + name + "？",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
            if (ans != DialogResult.Yes)
            {
                // 用户取消：把开关静默拨回去，保持与系统实际状态一致
                _modeSyncing = true;
                try { self.SetCheckedSilent(false); }
                finally { _modeSyncing = false; }
                return;
            }

            bool fromOther = perf ? _tgSaver.Checked : _tgPerf.Checked;
            int steps = (perf ? 7 : 11) + (fromOther ? 7 : 0);

            ModeRunner.Run(this, perf ? "正在释放全部性能" : "正在开启超级节能", steps,
                delegate(List<string> errs, Action<string> step)
                {
                    if (fromOther)
                    {
                        step("先还原上一个模式");
                        RestoreModeWork(errs, step);
                    }
                    if (perf) ApplyPerformanceWork(errs, step);
                    else ApplySaverWork(errs, step);
                },
                delegate(List<string> errs)
                {
                    _powerState = PowerCtl.Read();
                    UiFile.Set("mode", perf ? "perf" : "saver");
                    RefreshPowerUi();
                    RefreshStatus();
                    RefreshModeToggles();
                    LoadLogs();
                    ShowModeResult(perf, errs);
                });
        }

        // 关闭开关：还原到打开前的备份（同样显示进度窗）
        void BeginRestoreMode()
        {
            if (PowerCtl.LoadSnapshot() == null)
            {
                _modeSyncing = true;
                try
                {
                    _tgPerf.SetCheckedSilent(false);
                    _tgSaver.SetCheckedSilent(false);
                }
                finally { _modeSyncing = false; }
                MessageBox.Show(this, "没找到打开前的备份，未做还原。", "还原",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ModeRunner.Run(this, "正在还原到打开前的状态", 6,
                delegate(List<string> errs, Action<string> step) { RestoreModeWork(errs, step); },
                delegate(List<string> errs)
                {
                    _tgPerf.SetCheckedSilent(false);
                    _tgSaver.SetCheckedSilent(false);
                    UiFile.Set("mode", "");
                    _powerState = PowerCtl.Read();
                    RefreshPowerUi();
                    RefreshStatus();
                    RefreshModeToggles();
                    LogStore.Write("CONFIG", "mode=off: " + (errs.Count == 0 ? "OK" : string.Join(";", errs.ToArray())));
                    LoadLogs();

                    if (errs.Count == 0)
                        MessageBox.Show(this, "已关闭，并还原到打开前的状态。", "已完成",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else
                    {
                        string bad = "已关闭，部分还原失败：" + string.Join("；", errs.ToArray());
                        ShowPowerMsg(bad, true);
                        MessageBox.Show(this, bad, "部分完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                });
        }

        // 执行完毕：更新界面并弹窗告知结果
        void ShowModeResult(bool perf, List<string> errs)
        {
            string ok = perf
                ? "已释放全部性能：处理器不再降频、散热主动、插电节电已关闭。\r\n\r\n适合插电做重活，会更耗电，发热和风扇噪音也会增加。"
                : "超级节能已开启：插电和电池都锁定最省电（处理器 30%、被动散热、关闭睿频）。\r\n\r\n性能会明显下降，但更省电、机器更凉。";

            if (errs.Count > 0)
            {
                string tail = PowerCtl.IsAdmin() ? "" : "；可点『以管理员身份重启』再试";
                string bad = "部分改动没成功：" + string.Join("；", errs.ToArray()) + tail;
                ShowPowerMsg(bad, true);
                _btnElevate.Visible = true;
                MessageBox.Show(this, bad + "\r\n\r\n" + ok, "部分完成", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                ShowPowerMsg(perf ? "已释放全部性能：不上限、散热主动、插电节电已关闭。"
                                  : "超级节能已开启：插电和电池都锁定最省电（30% + 被动散热 + 关睿频）。", false);
                MessageBox.Show(this, ok, "已完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // —— 以下 Work 方法只做系统改动与写日志，不碰界面控件，可在后台线程执行 ——

        // 释放全部性能：插电不限频、散热主动、关掉插电节电
        void ApplyPerformanceWork(List<string> errs, Action<string> step)
        {
            PowerState now = PowerCtl.Read();
            try { PowerCtl.SaveSnapshot(now); } catch { }
            LogStore.Write("CONFIG", "mode=perf: overlay=maxperf mincpu=100 maxcpu=100 cooling=active saverAc=off");

            string err;

            step("备份当前设置并切换为「更好的性能」");
            if (!PowerCtl.SetOverlay(PowerCtl.OverlayMaxPerf, out err)) errs.Add("电源模式：" + err);

            step("设置最小处理器状态为 100%");
            if (!PowerCtl.SetMinCpu(true, 100, out err)) errs.Add("最小处理器：" + err);

            step("设置最大处理器状态为 100%");
            if (!PowerCtl.SetMaxCpu(true, 100, out err)) errs.Add("最大处理器：" + err);

            step("把散热方式改为「主动」");
            if (!PowerCtl.SetCooling(true, 1, out err)) errs.Add("散热方式：" + err);

            step("恢复睿频加速");
            if (!PowerCtl.SetBoostMode(true, 2, out err)) errs.Add("睿频策略：" + err);

            // 关键：插电若被“节电模式”强制开启，CPU 会被锁在基频、完全没有睿频
            step("关闭插电时的节电限制");
            SaverPolicy.SaveBackupForce();
            string saverMsg;
            if (!SaverPolicy.AllowPluggedIn(out saverMsg)) errs.Add("插电节电限制：" + saverMsg);

            step("让系统立即生效");
            PowerCtl.Activate(out err);
        }

        // 超级节能：插电/电池都锁最省
        void ApplySaverWork(List<string> errs, Action<string> step)
        {
            PowerState now = PowerCtl.Read();
            try { PowerCtl.SaveSnapshot(now); } catch { }

            string err;

            // 有「超级节能」方案就切过去，再逐项压实
            step("切换电源计划为「超级节能」");
            PowerScheme target = null;
            foreach (PowerScheme ps in PowerCtl.ListSchemes())
                if (ps.Name != null && ps.Name.IndexOf("超级节能") >= 0) { target = ps; break; }
            if (target != null && !string.Equals(target.Guid, now.SchemeGuid, StringComparison.OrdinalIgnoreCase))
            {
                if (!PowerCtl.SetScheme(target.Guid, out err)) errs.Add("电源计划：" + err);
            }

            step("电源模式切换为「节电模式」");
            if (!PowerCtl.SetOverlay(PowerCtl.OverlayBattery, out err)) errs.Add("电源模式：" + err);

            bool admin = PowerCtl.IsAdmin();
            step("插电：最大处理器 30%");
            if (!PowerCtl.SetMaxCpu(true, 30, out err) && admin) errs.Add("插电·最大处理器：" + err);
            step("插电：最小处理器 5%");
            if (!PowerCtl.SetMinCpu(true, 5, out err) && admin) errs.Add("插电·最小处理器：" + err);
            step("插电：散热方式改为被动");
            if (!PowerCtl.SetCooling(true, 0, out err) && admin) errs.Add("插电·散热方式：" + err);
            step("插电：关闭睿频");
            if (!PowerCtl.SetBoostMode(true, 0, out err) && admin) errs.Add("插电·睿频：" + err);
            step("电池：最大处理器 30%");
            if (!PowerCtl.SetMaxCpu(false, 30, out err) && admin) errs.Add("电池·最大处理器：" + err);
            step("电池：最小处理器 5%");
            if (!PowerCtl.SetMinCpu(false, 5, out err) && admin) errs.Add("电池·最小处理器：" + err);
            step("电池：散热方式改为被动");
            if (!PowerCtl.SetCooling(false, 0, out err) && admin) errs.Add("电池·散热方式：" + err);
            step("电池：关闭睿频");
            if (!PowerCtl.SetBoostMode(false, 0, out err) && admin) errs.Add("电池·睿频：" + err);

            // 插电、电池都强制开启节电模式
            step("插电和电池都开启节电模式");
            SaverPolicy.SaveBackupForce();
            string saverMsg;
            if (!SaverPolicy.ForceAlwaysOn(out saverMsg)) errs.Add("节电模式：" + saverMsg);

            step("让系统立即生效");
            PowerCtl.Activate(out err);
            LogStore.Write("CONFIG", "mode=saver: maxcpu=30 cooling=passive boost=off saver=always");
        }

        // 还原到打开前的备份（不碰界面，可在后台线程执行）
        bool RestoreModeWork(List<string> errs, Action<string> step)
        {
            PowerState snap = PowerCtl.LoadSnapshot();
            if (snap == null) return false;

            string err;
            bool online = PowerCtl.OnAc();
            bool admin = PowerCtl.IsAdmin();

            step("还原电源计划");
            if (!string.IsNullOrEmpty(snap.SchemeGuid) && !PowerCtl.SetScheme(snap.SchemeGuid, out err)) errs.Add("电源计划：" + err);

            // 一路还原 AC 与 DC，避免插拔后状态不一致
            step("还原处理器与散热设置");
            if (snap.AcMaxCpu > 0 && !PowerCtl.SetMaxCpu(true, snap.AcMaxCpu, out err) && admin) errs.Add("插电·最大处理器：" + err);
            if (snap.AcMinCpu >= 0 && !PowerCtl.SetMinCpu(true, snap.AcMinCpu, out err) && admin) errs.Add("插电·最小处理器：" + err);
            if (snap.AcCooling >= 0 && !PowerCtl.SetCooling(true, snap.AcCooling, out err) && admin) errs.Add("插电·散热：" + err);
            if (snap.AcBoost >= 0 && !PowerCtl.SetBoostMode(true, snap.AcBoost, out err) && admin) errs.Add("插电·睿频：" + err);
            if (snap.DcMaxCpu > 0 && !PowerCtl.SetMaxCpu(false, snap.DcMaxCpu, out err) && admin) errs.Add("电池·最大处理器：" + err);
            if (snap.DcMinCpu >= 0 && !PowerCtl.SetMinCpu(false, snap.DcMinCpu, out err) && admin) errs.Add("电池·最小处理器：" + err);
            if (snap.DcCooling >= 0 && !PowerCtl.SetCooling(false, snap.DcCooling, out err) && admin) errs.Add("电池·散热：" + err);
            if (snap.DcBoost >= 0 && !PowerCtl.SetBoostMode(false, snap.DcBoost, out err) && admin) errs.Add("电池·睿频：" + err);

            step("还原关屏与睡眠时间");
            if (snap.AcVideoIdle >= 0 && !PowerCtl.SetVideoIdle(true, snap.AcVideoIdle, out err)) errs.Add("插电·关屏：" + err);
            if (snap.AcStandbyIdle >= 0 && !PowerCtl.SetStandbyIdle(true, snap.AcStandbyIdle, out err)) errs.Add("插电·睡眠：" + err);
            if (snap.DcVideoIdle >= 0 && !PowerCtl.SetVideoIdle(false, snap.DcVideoIdle, out err)) errs.Add("电池·关屏：" + err);
            if (snap.DcStandbyIdle >= 0 && !PowerCtl.SetStandbyIdle(false, snap.DcStandbyIdle, out err)) errs.Add("电池·睡眠：" + err);

            step("还原电源模式");
            if (!PowerCtl.SetOverlay(online ? snap.AcOverlay : snap.DcOverlay, out err)) errs.Add("电源模式：" + err);

            // 节电策略按打开前的备份还原
            step("还原节电模式");
            string saverMsg;
            if (!SaverPolicy.RestoreBackup(out saverMsg)) errs.Add("节电模式：" + saverMsg);

            step("让系统立即生效");
            PowerCtl.Activate(out err);
            return true;
        }

        // 依据系统实际状态点亮对应开关
        void RefreshModeToggles()
        {
            PowerState s = _powerState;
            if (s == null) return;

            string mode = UiFile.Get("mode", "");
            bool saverOn = false, perfOn = false;

            if (mode == "saver")
            {
                bool forced = SaverPolicy.ForcedBoth;
                saverOn = forced || (s.AcMaxCpu > 0 && s.AcMaxCpu <= 30 && s.AcCooling == 0);
                if (!saverOn) { UiFile.Set("mode", ""); mode = ""; }
            }
            else if (mode == "perf")
            {
                perfOn = s.AcMaxCpu >= 100 && s.AcMinCpu >= 100;
                if (!perfOn) { UiFile.Set("mode", ""); mode = ""; }
            }

            _tgPerf.SetCheckedSilent(perfOn);
            _tgSaver.SetCheckedSilent(saverOn);
            _navMode.Text = "当前：" + (saverOn ? "超级节能" : (perfOn ? "释放性能" : "常规"));
        }

        Control BuildPowerPanel()
        {
            var gb = new GroupBox { Text = "电源与插电设置（修改前自动备份，可一键撤销）", Dock = DockStyle.Fill, Font = Font };

            AddCaption(gb, "电源计划", 12, 28);
            _plan.SetBounds(80, 24, 180, 24);
            _plan.DropDownStyle = ComboBoxStyle.DropDownList;
            gb.Controls.Add(_plan);

            AddCaption(gb, "电源模式", 276, 28);
            _overlay.SetBounds(344, 24, 140, 24);
            _overlay.DropDownStyle = ComboBoxStyle.DropDownList;
            _overlay.Items.AddRange(new object[] { "节电模式", "平衡（默认）", "更好的性能" });
            _overlay.SelectedIndex = 0;
            gb.Controls.Add(_overlay);

            _btnApplyPower.Text = "应用并备份";
            _btnApplyPower.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            _btnApplyPower.SetBounds(496, 22, 104, 30);
            _btnApplyPower.Click += (s, e) => ApplyPowerSettings();
            gb.Controls.Add(_btnApplyPower);

            _btnUndoPower.Text = "撤销上次修改";
            _btnUndoPower.SetBounds(608, 22, 110, 30);
            _btnUndoPower.Click += (s, e) => UndoPower();
            gb.Controls.Add(_btnUndoPower);

            var btnPowerCpl = new Button { Text = "电源选项", FlatStyle = FlatStyle.System };
            btnPowerCpl.SetBounds(726, 22, 96, 30);
            btnPowerCpl.Click += (s, e) => StartSafe("control.exe", "powercfg.cpl");
            gb.Controls.Add(btnPowerCpl);

            _btnElevate.Text = "以管理员身份重启";
            _btnElevate.SetBounds(12, 188, 128, 28);
            _btnElevate.Visible = false;   // 只有确实写入失败时才提示提权
            _btnElevate.Click += (s, e) => RelaunchAsAdmin();
            gb.Controls.Add(_btnElevate);

            _lblPowerState.TextAlign = ContentAlignment.MiddleRight;
            _lblPowerState.ForeColor = Color.FromArgb(90, 90, 100);
            _lblPowerState.AutoEllipsis = true;
            gb.Controls.Add(_lblPowerState);

            // 第二行：插电
            AddCaption(gb, "插电时", 12, 64);
            AddCaption(gb, "关屏", 68, 64);
            InitTime(gb, _acOff, 108, 60);
            AddCaption(gb, "睡眠", 194, 64);
            InitTime(gb, _acSleep, 234, 60);
            AddCaption(gb, "最大处理器", 322, 64);
            _acMaxCpu.SetBounds(416, 60, 66, 24);
            _acMaxCpu.DropDownStyle = ComboBoxStyle.DropDownList;
            for (int i = 0; i < CpuValues.Length; i++) _acMaxCpu.Items.Add(CpuValues[i] + "%");
            _acMaxCpu.SelectedIndex = 0;
            gb.Controls.Add(_acMaxCpu);

            // 第三行：电池
            AddCaption(gb, "电池时", 12, 96);
            AddCaption(gb, "关屏", 68, 96);
            InitTime(gb, _dcOff, 108, 92);
            AddCaption(gb, "睡眠", 194, 96);
            InitTime(gb, _dcSleep, 234, 92);

            _btnSyncAc.Text = "复制到插电";
            _btnSyncAc.SetBounds(680, 90, 96, 28);
            _btnSyncAc.Click += (s, e) => SyncPower(true);
            gb.Controls.Add(_btnSyncAc);

            _btnSyncDc.Text = "复制到电池";
            _btnSyncDc.SetBounds(680, 58, 96, 28);
            _btnSyncDc.Click += (s, e) => SyncPower(false);
            gb.Controls.Add(_btnSyncDc);

            // 第四行：一键预设 + 插拔电自动亮度
            var p1 = new Button { Text = "省电模式" };
            p1.SetBounds(12, 122, 88, 28);
            p1.Click += (s, e) => ApplyPreset(0);
            gb.Controls.Add(p1);

            var p2 = new Button { Text = "均衡模式" };
            p2.SetBounds(104, 122, 88, 28);
            p2.Click += (s, e) => ApplyPreset(1);
            gb.Controls.Add(p2);

            var p3 = new Button { Text = "性能模式" };
            p3.SetBounds(196, 122, 88, 28);
            p3.Click += (s, e) => ApplyPreset(2);
            gb.Controls.Add(p3);

            _btnRestoreAllPower.Text = "全部还原";
            _btnRestoreAllPower.SetBounds(680, 122, 96, 28);
            _btnRestoreAllPower.Click += (s, e) => RestoreAll();
            gb.Controls.Add(_btnRestoreAllPower);

            _chkAutoBright.Text = "插拔电源自动设亮度";
            _chkAutoBright.SetBounds(298, 126, 150, 22);
            _chkAutoBright.CheckedChanged += (s, e) =>
            {
                if (_powerSyncing) return;
                UiFile.Set("autoBright", _chkAutoBright.Checked ? "1" : "0");
                _acBrightLevel.Enabled = _chkAutoBright.Checked;
                _dcBrightLevel.Enabled = _chkAutoBright.Checked;
                LogStore.Write("CONFIG", "auto-bright=" + (_chkAutoBright.Checked ? "on" : "off"));
                ShowPowerMsg(_chkAutoBright.Checked ? "已启用：插电/拔电时自动切换亮度。" : "已关闭自动亮度切换。", false);
            };
            gb.Controls.Add(_chkAutoBright);

            AddCaption(gb, "插电", 454, 128);
            InitLevel(gb, _acBrightLevel, 496, 122);
            _acBrightLevel.SelectedIndexChanged += (s, e) =>
            {
                if (_powerSyncing) return;
                UiFile.Set("acBright", LevelValue(_acBrightLevel).ToString());
            };
            AddCaption(gb, "电池", 558, 128);
            InitLevel(gb, _dcBrightLevel, 600, 122);
            _dcBrightLevel.SelectedIndexChanged += (s, e) =>
            {
                if (_powerSyncing) return;
                UiFile.Set("dcBright", LevelValue(_dcBrightLevel).ToString());
            };

            // 第五行：插拔自动锁定模式
            _chkLock.Text = "插拔自动锁定";
            _chkLock.SetBounds(12, 162, 150, 22);
            _chkLock.CheckedChanged += (s, e) => ToggleLock(_chkLock.Checked);
            gb.Controls.Add(_chkLock);

            AddCaption(gb, "插电", 168, 166);
            _lockAc.SetBounds(210, 160, 110, 24);
            _lockAc.DropDownStyle = ComboBoxStyle.DropDownList;
            _lockAc.Items.AddRange(new object[] { "节电模式", "平衡（默认）", "更好的性能" });
            _lockAc.SelectedIndex = 2;
            _lockAc.Enabled = false;
            _lockAc.SelectedIndexChanged += (s, e) =>
            {
                if (_lockSyncing) return;
                UiFile.Set("lockAc", OverlayGuidAt(_lockAc));
                EnforceLock(PowerCtl.OnAc());
            };
            gb.Controls.Add(_lockAc);

            AddCaption(gb, "电池", 330, 166);
            _lockDc.SetBounds(372, 160, 110, 24);
            _lockDc.DropDownStyle = ComboBoxStyle.DropDownList;
            _lockDc.Items.AddRange(new object[] { "节电模式", "平衡（默认）", "更好的性能" });
            _lockDc.SelectedIndex = 0;
            _lockDc.Enabled = false;
            _lockDc.SelectedIndexChanged += (s, e) =>
            {
                if (_lockSyncing) return;
                UiFile.Set("lockDc", OverlayGuidAt(_lockDc));
                EnforceLock(PowerCtl.OnAc());
            };
            gb.Controls.Add(_lockDc);

            _btnLockRestore.Text = "关闭并还原";
            _btnLockRestore.SetBounds(492, 158, 120, 28);
            _btnLockRestore.Click += (s, e) => RestoreLockBackup();
            gb.Controls.Add(_btnLockRestore);

            _btnLockApply.Text = "立即应用";
            _btnLockApply.SetBounds(620, 158, 100, 28);
            _btnLockApply.Click += (s, e) => ApplyLockNow();
            gb.Controls.Add(_btnLockApply);

            _chkSaver.Text = "节电模式常开";
            _chkSaver.SetBounds(730, 162, 120, 22);
            _chkSaver.CheckedChanged += (s, e) =>
            {
                if (_powerSyncing) return;
                ToggleSaver(_chkSaver.Checked);
            };
            gb.Controls.Add(_chkSaver);

            _lblPowerMsg.SetBounds(12, 192, 520, 20);
            _lblPowerMsg.AutoEllipsis = true;
            gb.Controls.Add(_lblPowerMsg);

            var hint = new Label
            {
                Text = "模式只作用于当前电源来源；修改前会自动备份，可用「撤销上次修改」还原。",
                ForeColor = Color.Gray,
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            hint.SetBounds(12, 216, 600, 20);
            gb.Controls.Add(hint);

            Tip(_plan, "电源计划（Power Scheme）：Windows 的一套电源策略集合，例如「平衡」「高性能」「节能」。\r\n它决定处理器、硬盘、显卡在不同场景下的省电方式。");
            Tip(_overlay, "电源模式（Power Mode，也叫电源模式叠加）：在现有电源计划上再叠加一层性能倾向——节电 / 平衡 / 更好的性能。\r\n本程序只修改当前电源来源（插电或电池）对应的那一层。");
            Tip(_btnApplyPower, "把上面修改的电源计划、电源模式、关屏/睡眠时间和最大处理器状态写入系统。\r\n修改前会自动备份旧值，可用「撤销上次修改」还原。");
            Tip(_btnUndoPower, "把最近一次「应用并备份」涉及的项目还原成修改前的值。");
            Tip(btnPowerCpl, "打开 Windows 自带的「电源选项」控制面板，可查看或新建电源计划。");
            Tip(_acOff, "插电时，无人操作多久后关闭屏幕（只关屏，不一定睡眠）。");
            Tip(_acSleep, "插电时，无人操作多久后进入睡眠。睡眠会暂停程序运行，唤醒后继续。");
            Tip(_acMaxCpu, "最大处理器状态：限制 CPU 最高只能跑到标称频率的百分比。\r\n100% 表示不限制；数值越低越省电、越凉，但性能越弱。");
            Tip(_dcOff, "使用电池时，无人操作多久后关闭屏幕（只关屏，不一定睡眠）。");
            Tip(_dcSleep, "使用电池时，无人操作多久后进入睡眠。");
            Tip(_btnSyncDc, "把「插电时」这一行的关屏、睡眠、最大处理器设置复制到「电池时」。");
            Tip(_btnSyncAc, "把「电池时」这一行的关屏、睡眠、最大处理器设置复制到「插电时」。");
            Tip(p1, "一键套用省电预设：缩短关屏/睡眠时间、限制处理器上限，适合用电池时延长续航。");
            Tip(p2, "恢复为 Windows 默认的平衡策略：性能与耗电折中。");
            Tip(p3, "提高处理器上限、散热更积极，适合插电做重活，但更耗电、更热。");
            Tip(_chkAutoBright, "勾选后，插上电源或拔掉电源时，自动把亮度切到下面设定的「插电亮度 / 电池亮度」。");
            Tip(_acBrightLevel, "插电时自动切换到的亮度百分比。");
            Tip(_dcBrightLevel, "使用电池时自动切换到的亮度百分比。");
            Tip(_chkLock, "插拔电源时自动切换电源模式：插电用一个模式、电池用另一个模式，并持续检查纠正。");
            Tip(_lockAc, "插电时强制使用的电源模式。锁定后会定期检查，被系统改回时自动再设回去。");
            Tip(_lockDc, "使用电池时强制使用的电源模式。");
            Tip(_btnLockRestore, "关闭插拔自动锁定，并把锁定期间改过的电源模式还原成锁定前的值。");
            Tip(_btnLockApply, "不等插拔事件，立即按当前选择强制应用一次电源模式。");
            Tip(_chkSaver, "让 Windows 的「节电模式」（Battery Saver）常开，即使插着电源也生效。\r\n它会限制后台活动、可能压暗屏幕，以换取更低功耗。");
            Tip(_btnRestoreAllPower, "把本程序改过的全部电源相关设置还原到最初的备份。");
            Tip(_btnElevate, "当前权限不足以写电源设置时，以管理员身份重新启动本程序。");
            Tip(_lblPowerMsg, "显示最近一次电源操作的结果或失败原因。");
            Tip(_lblPowerState, "显示当前电源来源、电源计划和电源模式的实时状态。");

            // 窗口变宽时把右侧按钮贴右排列，避免被挤住或裁掉
            gb.Resize += (s, e) =>
            {
                LayoutPowerPanel(gb);
                hint.Width = Math.Max(120, gb.ClientSize.Width - 24 - (_btnElevate.Visible ? 136 : 0));
            };
            LayoutPowerPanel(gb);

            return gb;
        }

        // 电源设置区自适应：右缘按钮贴右排列
        void LayoutPowerPanel(GroupBox gb)
        {
            int w = gb.ClientSize.Width;
            if (w < 400) return;
            int right = w - 12;

            _btnSyncDc.Left = right - _btnSyncDc.Width;
            _btnSyncAc.Left = right - _btnSyncAc.Width;
            _btnRestoreAllPower.Left = right - _btnRestoreAllPower.Width;

            _lblPowerMsg.SetBounds(12, 192, Math.Max(160, right - 24 - 210), 20);
            _lblPowerState.SetBounds(right - 200, 192, 200, 20);
            _btnElevate.SetBounds(right - 128, 214, 128, 28);
        }

        static void AddCaption(Control parent, string text, int x, int y)
        {
            var lb = new Label
            {
                Text = text,
                ForeColor = Color.FromArgb(70, 70, 80),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft
            };
            lb.SetBounds(x, y, CaptionTextWidth(text), 20);
            parent.Controls.Add(lb);
        }

        // 旧的「字数×16」估算会把标签撑得比文字宽很多，右缘压到下拉框边框上
        static int CaptionTextWidth(string text)
        {
            int w = TextRenderer.MeasureText(text, CaptionFont, new Size(int.MaxValue, 20),
                TextFormatFlags.NoPadding).Width;
            return w + 8;
        }

        static void InitTime(Control parent, ComboBox box, int x, int y)
        {
            box.SetBounds(x, y, 78, 24);
            box.DropDownStyle = ComboBoxStyle.DropDownList;
            for (int i = 0; i < TimeValues.Length; i++) box.Items.Add(TimeText(TimeValues[i]));
            box.SelectedIndex = FindTimeIndex(600);
            parent.Controls.Add(box);
        }

        static void InitLevel(Control parent, ComboBox box, int x, int y)
        {
            box.SetBounds(x, y, 58, 24);
            box.DropDownStyle = ComboBoxStyle.DropDownList;
            for (int i = 0; i < LevelValues.Length; i++) box.Items.Add(LevelValues[i] + "%");
            box.SelectedIndex = FindLevelIndex(80);
            box.Enabled = false;
            parent.Controls.Add(box);
        }

        string OverlayGuidFromCombo()
        {
            if (_overlay.SelectedIndex == 0) return PowerCtl.OverlayBattery;
            if (_overlay.SelectedIndex == 2) return PowerCtl.OverlayMaxPerf;
            return "";
        }

        static bool SameGuid(string a, string b)
        {
            string x = a == null ? "" : a.Trim().ToLower();
            string y = b == null ? "" : b.Trim().ToLower();
            if (x.StartsWith("00000000")) x = "";
            if (y.StartsWith("00000000")) y = "";
            return x == y;
        }

        static int OverlayIndex(string guid)
        {
            if (string.Equals(guid, PowerCtl.OverlayBattery, StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(guid, PowerCtl.OverlayMaxPerf, StringComparison.OrdinalIgnoreCase)) return 2;
            return 1;
        }

        static int IntOf(string text, int fallback)
        {
            int v;
            if (int.TryParse(text, out v)) return v;
            return fallback;
        }

        void ShowPowerMsg(string text, bool error)
        {
            _lblPowerMsg.Text = text;
            _lblPowerMsg.ForeColor = error ? Color.Firebrick : Color.FromArgb(20, 110, 60);
            if (error) LogStore.Write("WARN", "power-ui: " + text);
        }

        void RefreshPowerUi()
        {
            _powerSyncing = true;
            try
            {
                PowerState s = PowerCtl.Read();
                _powerState = s;

                List<PowerScheme> schemes = PowerCtl.ListSchemes();
                _plan.BeginUpdate();
                _plan.Items.Clear();
                foreach (PowerScheme ps in schemes) _plan.Items.Add(ps);
                _plan.EndUpdate();
                for (int i = 0; i < _plan.Items.Count; i++)
                {
                    PowerScheme ps = _plan.Items[i] as PowerScheme;
                    if (ps != null && string.Equals(ps.Guid, s.SchemeGuid, StringComparison.OrdinalIgnoreCase))
                    {
                        _plan.SelectedIndex = i;
                        break;
                    }
                }

                bool online = PowerCtl.OnAc();
                _overlay.SelectedIndex = OverlayIndex(online ? s.AcOverlay : s.DcOverlay);
                _acOff.SelectedIndex = FindTimeIndex(s.AcVideoIdle);
                _acSleep.SelectedIndex = FindTimeIndex(s.AcStandbyIdle);
                _dcOff.SelectedIndex = FindTimeIndex(s.DcVideoIdle);
                _dcSleep.SelectedIndex = FindTimeIndex(s.DcStandbyIdle);
                _acMaxCpu.SelectedIndex = FindCpuIndex(s.AcMaxCpu);

                _lblPowerState.Text = (online ? "插电" : "电池") + " · "
                    + PowerCtl.OverlayName(online ? s.AcOverlay : s.DcOverlay);

                _chkAutoBright.Checked = UiFile.Get("autoBright", "0") == "1";
                _acBrightLevel.SelectedIndex = FindLevelIndex(IntOf(UiFile.Get("acBright", "80"), 80));
                _dcBrightLevel.SelectedIndex = FindLevelIndex(IntOf(UiFile.Get("dcBright", "50"), 50));
                _acBrightLevel.Enabled = _chkAutoBright.Checked;
                _dcBrightLevel.Enabled = _chkAutoBright.Checked;

                _lockSyncing = true;
                try
                {
                    _lockAc.SelectedIndex = OverlayIndex(UiFile.Get("lockAc", PowerCtl.OverlayMaxPerf));
                    _lockDc.SelectedIndex = OverlayIndex(UiFile.Get("lockDc", PowerCtl.OverlayBattery));
                    _chkLock.Checked = UiFile.Get("lockMode", "0") == "1";
                    _lockAc.Enabled = _chkLock.Checked;
                    _lockDc.Enabled = _chkLock.Checked;
                    _chkSaver.Checked = SaverPolicy.IsOn();
                }
                finally { _lockSyncing = false; }

                RefreshModeToggles();
            }
            catch (Exception ex)
            {
                try { LogStore.Write("WARN", "refresh-power: " + ex.Message); } catch { }
            }
            finally { _powerSyncing = false; }
        }

        void ApplyPreset(int kind)
        {
            _powerSyncing = true;
            try
            {
                if (kind == 0)
                {
                    _overlay.SelectedIndex = 0;
                    _acOff.SelectedIndex = FindTimeIndex(300);
                    _acSleep.SelectedIndex = FindTimeIndex(900);
                    _dcOff.SelectedIndex = FindTimeIndex(180);
                    _dcSleep.SelectedIndex = FindTimeIndex(600);
                    _acMaxCpu.SelectedIndex = FindCpuIndex(100);
                }
                else if (kind == 1)
                {
                    _overlay.SelectedIndex = 1;
                    _acOff.SelectedIndex = FindTimeIndex(600);
                    _acSleep.SelectedIndex = FindTimeIndex(1800);
                    _dcOff.SelectedIndex = FindTimeIndex(240);
                    _dcSleep.SelectedIndex = FindTimeIndex(1200);
                    _acMaxCpu.SelectedIndex = FindCpuIndex(100);
                }
                else
                {
                    _overlay.SelectedIndex = 2;
                    _acOff.SelectedIndex = FindTimeIndex(1800);
                    _acSleep.SelectedIndex = FindTimeIndex(0);
                    _dcOff.SelectedIndex = FindTimeIndex(600);
                    _dcSleep.SelectedIndex = FindTimeIndex(1800);
                    _acMaxCpu.SelectedIndex = FindCpuIndex(100);
                }
            }
            finally { _powerSyncing = false; }

            ApplyPowerSettings();
        }

        void ApplyPowerSettings()
        {
            if (_powerSyncing) return;

            PowerState now = _powerState;
            if (now == null)
            {
                ShowPowerMsg("还没读到电源状态，请点『刷新全部』后重试。", true);
                return;
            }

            bool online = PowerCtl.OnAc();
            string curOverlay = online ? now.AcOverlay : now.DcOverlay;
            string wantOverlay = OverlayGuidFromCombo();

            PowerScheme ps = _plan.SelectedItem as PowerScheme;
            string planGuid = ps == null ? now.SchemeGuid : ps.Guid;
            string planName = ps == null ? now.SchemeName : ps.Name;

            int acOff = TimeValue(_acOff);
            int acSleep = TimeValue(_acSleep);
            int acCpu = CpuValue(_acMaxCpu);
            int dcOff = TimeValue(_dcOff);
            int dcSleep = TimeValue(_dcSleep);

            var changes = new List<string>();
            if (!string.Equals(planGuid, now.SchemeGuid, StringComparison.OrdinalIgnoreCase))
                changes.Add("电源计划：" + now.SchemeName + " → " + planName);
            if (!SameGuid(wantOverlay, curOverlay))
                changes.Add("电源模式（" + (online ? "插电" : "电池") + "）："
                    + PowerCtl.OverlayName(curOverlay) + " → " + PowerCtl.OverlayName(wantOverlay));
            if (acOff >= 0 && acOff != now.AcVideoIdle)
                changes.Add("插电 · 关屏：" + TimeText(now.AcVideoIdle) + " → " + TimeText(acOff));
            if (acSleep >= 0 && acSleep != now.AcStandbyIdle)
                changes.Add("插电 · 睡眠：" + TimeText(now.AcStandbyIdle) + " → " + TimeText(acSleep));
            if (acCpu > 0 && acCpu != now.AcMaxCpu)
                changes.Add("插电 · 最大处理器：" + now.AcMaxCpu + "% → " + acCpu + "%");
            if (dcOff >= 0 && dcOff != now.DcVideoIdle)
                changes.Add("电池 · 关屏：" + TimeText(now.DcVideoIdle) + " → " + TimeText(dcOff));
            if (dcSleep >= 0 && dcSleep != now.DcStandbyIdle)
                changes.Add("电池 · 睡眠：" + TimeText(now.DcStandbyIdle) + " → " + TimeText(dcSleep));

            if (changes.Count == 0)
            {
                ShowPowerMsg("没有需要改动的项。", false);
                return;
            }

            if (acOff > 0 && acSleep > 0 && acOff > acSleep)
            {
                ShowPowerMsg("插电：关屏时间不能大于睡眠时间。", true);
                return;
            }
            if (dcOff > 0 && dcSleep > 0 && dcOff > dcSleep)
            {
                ShowPowerMsg("电池：关屏时间不能大于睡眠时间。", true);
                return;
            }

            var tips = new List<string>();
            if (acCpu > 0 && acCpu < 80) tips.Add("最大处理器状态低于 80%，插电时也会明显变慢");
            if (acOff == 0) tips.Add("插电时永不关屏会增加耗电");
            if (acSleep == 0) tips.Add("插电时永不休眠（合盖仍会按系统策略处理）");

            string body = "即将修改：\r\n\r\n  " + string.Join("\r\n  ", changes.ToArray());
            if (tips.Count > 0) body += "\r\n\r\n注意事项：\r\n  " + string.Join("\r\n  ", tips.ToArray());
            body += "\r\n\r\n修改前会自动备份到 power-backup.txt，可点『撤销上次修改』还原。";

            if (MessageBox.Show(body, "确认修改电源设置", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
                return;

            try { PowerCtl.SaveSnapshot(now); } catch { }
            LogStore.Write("CONFIG", "power-apply: " + string.Join("; ", changes.ToArray()));

            var errs = new List<string>();
            string err;

            if (!string.Equals(planGuid, now.SchemeGuid, StringComparison.OrdinalIgnoreCase)
                && !PowerCtl.SetScheme(planGuid, out err)) errs.Add("电源计划：" + err);
            if (!SameGuid(wantOverlay, curOverlay)
                && !PowerCtl.SetOverlay(wantOverlay, out err)) errs.Add("电源模式：" + err);

            if (acOff >= 0 && acOff != now.AcVideoIdle && !PowerCtl.SetVideoIdle(true, acOff, out err)) errs.Add("插电·关屏：" + err);
            if (acSleep >= 0 && acSleep != now.AcStandbyIdle && !PowerCtl.SetStandbyIdle(true, acSleep, out err)) errs.Add("插电·睡眠：" + err);
            if (acCpu > 0 && acCpu != now.AcMaxCpu && !PowerCtl.SetMaxCpu(true, acCpu, out err)) errs.Add("插电·最大处理器：" + err);
            if (dcOff >= 0 && dcOff != now.DcVideoIdle && !PowerCtl.SetVideoIdle(false, dcOff, out err)) errs.Add("电池·关屏：" + err);
            if (dcSleep >= 0 && dcSleep != now.DcStandbyIdle && !PowerCtl.SetStandbyIdle(false, dcSleep, out err)) errs.Add("电池·睡眠：" + err);

            PowerCtl.Activate(out err);

            PowerState after = PowerCtl.Read();
            _powerState = after;
            UiFile.Set("mode", "");   // 手动改过设置，两个开关回到未开启状态
            RefreshPowerUi();
            RefreshStatus();

            if (errs.Count > 0)
            {
                string tail = PowerCtl.IsAdmin() ? "" : "；可点『以管理员身份重启』";
                ShowPowerMsg("部分修改失败：" + string.Join("；", errs.ToArray()) + tail, true);
                _btnElevate.Visible = true;
            }
            else
            {
                bool ok = !string.Equals(after.SchemeGuid, planGuid, StringComparison.OrdinalIgnoreCase) ? false
                    : (acOff < 0 || after.AcVideoIdle == acOff)
                      && (acSleep < 0 || after.AcStandbyIdle == acSleep)
                      && (acCpu <= 0 || after.AcMaxCpu == acCpu)
                      && (dcOff < 0 || after.DcVideoIdle == dcOff)
                      && (dcSleep < 0 || after.DcStandbyIdle == dcSleep)
                      && SameGuid(PowerCtl.ActualOverlay(), wantOverlay);

                ShowPowerMsg(ok ? "已应用并备份，全部项回读一致。" : "已应用，但部分项回读不一致，建议点『撤销上次修改』。", !ok);
                LogStore.Write("CONFIG", "power-result: " + (ok ? "OK" : "MISMATCH"));
            }
            LoadLogs();
        }

        void UndoPower()
        {
            PowerState snap = PowerCtl.LoadSnapshot();
            if (snap == null)
            {
                ShowPowerMsg("还没有备份，先用『应用并备份』改一次再撤销。", true);
                return;
            }

            string when = snap.At.ToString("MM-dd HH:mm");
            string msg = "将把电源设置还原为 " + when + " 的备份：\r\n\r\n"
                + "  电源计划：" + (string.IsNullOrEmpty(snap.SchemeName) ? snap.SchemeGuid : snap.SchemeName) + "\r\n"
                + "  电源模式：" + PowerCtl.OverlayName(PowerCtl.OnAc() ? snap.AcOverlay : snap.DcOverlay) + "\r\n"
                + "  插电 · 关屏/睡眠：" + TimeText(snap.AcVideoIdle) + " / " + TimeText(snap.AcStandbyIdle) + "\r\n"
                + "  电池 · 关屏/睡眠：" + TimeText(snap.DcVideoIdle) + " / " + TimeText(snap.DcStandbyIdle) + "\r\n"
                + "  插电 · 最大处理器：" + snap.AcMaxCpu + "%\r\n"
                + (snap.AcMinCpu > 0 ? "  插电 · 最小处理器：" + snap.AcMinCpu + "%\r\n" : "")
                + (snap.AcCooling >= 0 ? "  插电 · 散热方式：" + (snap.AcCooling == 1 ? "主动" : "被动") + "\r\n" : "")
                + "\r\n继续？";

            if (MessageBox.Show(msg, "撤销上次修改", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK)
                return;

            var errs = new List<string>();
            string err;

            if (!string.IsNullOrEmpty(snap.SchemeGuid) && !PowerCtl.SetScheme(snap.SchemeGuid, out err)) errs.Add("电源计划：" + err);
            if (!PowerCtl.SetOverlay(PowerCtl.OnAc() ? snap.AcOverlay : snap.DcOverlay, out err)) errs.Add("电源模式：" + err);
            if (snap.AcVideoIdle >= 0 && !PowerCtl.SetVideoIdle(true, snap.AcVideoIdle, out err)) errs.Add("插电·关屏：" + err);
            if (snap.AcStandbyIdle >= 0 && !PowerCtl.SetStandbyIdle(true, snap.AcStandbyIdle, out err)) errs.Add("插电·睡眠：" + err);
            if (snap.AcMaxCpu > 0 && !PowerCtl.SetMaxCpu(true, snap.AcMaxCpu, out err)) errs.Add("插电·最大处理器：" + err);
            if (snap.AcMinCpu >= 0 && !PowerCtl.SetMinCpu(true, snap.AcMinCpu, out err)) errs.Add("插电·最小处理器：" + err);
            if (snap.AcCooling >= 0 && !PowerCtl.SetCooling(true, snap.AcCooling, out err)) errs.Add("插电·散热方式：" + err);
            if (snap.DcVideoIdle >= 0 && !PowerCtl.SetVideoIdle(false, snap.DcVideoIdle, out err)) errs.Add("电池·关屏：" + err);
            if (snap.DcStandbyIdle >= 0 && !PowerCtl.SetStandbyIdle(false, snap.DcStandbyIdle, out err)) errs.Add("电池·睡眠：" + err);

            PowerCtl.Activate(out err);

            _powerState = PowerCtl.Read();
            UiFile.Set("mode", "");   // 撤销后开关回到未开启状态
            RefreshPowerUi();
            RefreshStatus();
            LoadLogs();
            LogStore.Write("CONFIG", "power-undo: " + (errs.Count == 0 ? "OK" : string.Join(";", errs.ToArray())));
            ShowPowerMsg(errs.Count == 0 ? "已还原到备份状态。" : ("还原部分失败：" + string.Join("；", errs.ToArray())), errs.Count > 0);
        }

        // 插电/电池 的关屏、睡眠、亮度互相同步（点“应用并备份”后生效）
        void SyncPower(bool toAc)
        {
            _powerSyncing = true;
            try
            {
                if (toAc)
                {
                    _acOff.SelectedIndex = _dcOff.SelectedIndex;
                    _acSleep.SelectedIndex = _dcSleep.SelectedIndex;
                    _acBrightLevel.SelectedIndex = _dcBrightLevel.SelectedIndex;
                }
                else
                {
                    _dcOff.SelectedIndex = _acOff.SelectedIndex;
                    _dcSleep.SelectedIndex = _acSleep.SelectedIndex;
                    _dcBrightLevel.SelectedIndex = _acBrightLevel.SelectedIndex;
                }
            }
            finally { _powerSyncing = false; }

            ShowPowerMsg(toAc ? "已把电池的关屏/睡眠/亮度复制到插电，点『应用并备份』生效。"
                              : "已把插电的关屏/睡眠/亮度复制到电池，点『应用并备份』生效。", false);
            LogStore.Write("CONFIG", "sync " + (toAc ? "dc->ac" : "ac->dc"));
        }

        // 一键还原：计划、模式、超时、处理器、节电策略全部恢复到最早的备份
        void RestoreAll()
        {
            PowerState snap = PowerCtl.LoadSnapshot(PowerCtl.BackupPath);
            if (snap == null) snap = PowerCtl.LoadSnapshot(PowerCtl.LockBackupPath);
            if (snap == null)
            {
                ShowPowerMsg("还没有任何备份，无法还原。", true);
                return;
            }

            bool saverOn = SaverPolicy.IsOn();
            string msg = "将把电源设置全部还原为 " + snap.At.ToString("MM-dd HH:mm") + " 的备份：\r\n\r\n"
                + "  电源计划：" + (string.IsNullOrEmpty(snap.SchemeName) ? snap.SchemeGuid : snap.SchemeName) + "\r\n"
                + "  插电/电池 模式：" + PowerCtl.OverlayName(snap.AcOverlay) + " / " + PowerCtl.OverlayName(snap.DcOverlay) + "\r\n"
                + "  插电 关屏/睡眠：" + TimeText(snap.AcVideoIdle) + " / " + TimeText(snap.AcStandbyIdle) + "\r\n"
                + "  电池 关屏/睡眠：" + TimeText(snap.DcVideoIdle) + " / " + TimeText(snap.DcStandbyIdle) + "\r\n"
                + "  插电/电池 最大处理器：" + snap.AcMaxCpu + "% / " + snap.DcMaxCpu + "%\r\n"
                + (snap.AcMinCpu > 0 ? "  插电/电池 最小处理器：" + snap.AcMinCpu + "% / " + snap.DcMinCpu + "%\r\n" : "")
                + (snap.AcCooling >= 0 ? "  插电/电池 散热方式：" + (snap.AcCooling == 1 ? "主动" : "被动") + "\r\n" : "")
                + "  插拔自动锁定：关闭\r\n"
                + (saverOn ? "  节电模式常开：关闭并还原策略\r\n" : "")
                + "\r\n继续？";

            if (MessageBox.Show(msg, "全部还原", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
                return;

            var errs = new List<string>();
            string err;

            if (!string.IsNullOrEmpty(snap.SchemeGuid) && !PowerCtl.SetScheme(snap.SchemeGuid, out err)) errs.Add("计划：" + err);
            if (!PowerCtl.SetOverlay(PowerCtl.OnAc() ? snap.AcOverlay : snap.DcOverlay, out err)) errs.Add("模式：" + err);
            if (snap.AcVideoIdle >= 0 && !PowerCtl.SetVideoIdle(true, snap.AcVideoIdle, out err)) errs.Add("插电·关屏：" + err);
            if (snap.AcStandbyIdle >= 0 && !PowerCtl.SetStandbyIdle(true, snap.AcStandbyIdle, out err)) errs.Add("插电·睡眠：" + err);
            if (snap.AcMaxCpu > 0 && !PowerCtl.SetMaxCpu(true, snap.AcMaxCpu, out err)) errs.Add("插电·处理器：" + err);
            if (snap.AcMinCpu >= 0 && !PowerCtl.SetMinCpu(true, snap.AcMinCpu, out err)) errs.Add("插电·最小处理器：" + err);
            if (snap.AcCooling >= 0 && !PowerCtl.SetCooling(true, snap.AcCooling, out err)) errs.Add("插电·散热：" + err);
            if (snap.DcVideoIdle >= 0 && !PowerCtl.SetVideoIdle(false, snap.DcVideoIdle, out err)) errs.Add("电池·关屏：" + err);
            if (snap.DcStandbyIdle >= 0 && !PowerCtl.SetStandbyIdle(false, snap.DcStandbyIdle, out err)) errs.Add("电池·睡眠：" + err);
            if (snap.DcMaxCpu > 0 && !PowerCtl.SetMaxCpu(false, snap.DcMaxCpu, out err)) errs.Add("电池·处理器：" + err);
            if (snap.DcMinCpu >= 0 && !PowerCtl.SetMinCpu(false, snap.DcMinCpu, out err)) errs.Add("电池·最小处理器：" + err);
            if (snap.DcCooling >= 0 && !PowerCtl.SetCooling(false, snap.DcCooling, out err)) errs.Add("电池·散热：" + err);
            PowerCtl.Activate(out err);

            // 只取消勾选锁定，避免再写一次覆盖刚还原的模式
            UiFile.Set("lockMode", "0");
            _lockSyncing = true;
            try
            {
                _chkLock.Checked = false;
                _lockAc.Enabled = false;
                _lockDc.Enabled = false;
            }
            finally { _lockSyncing = false; }

            string saverMsg = "";
            if (saverOn) SaverPolicy.Apply(false, out saverMsg);

            _powerState = PowerCtl.Read();
            RefreshPowerUi();
            RefreshStatus();
            LoadLogs();
            LogStore.Write("CONFIG", "restore-all " + (errs.Count == 0 ? "OK" : string.Join(";", errs.ToArray())));
            ShowPowerMsg(errs.Count == 0 ? "已全部还原到备份状态。" : ("部分还原失败：" + string.Join("；", errs.ToArray())), errs.Count > 0);
        }

        // 把当前状态整理成文字，方便粘贴排查
        void CopyStateSummary()
        {
            PowerState st = _powerState;
            var sb = new StringBuilder();
            sb.AppendLine(Program.AppName + " " + Program.Version + " · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("电源来源：" + (PowerCtl.OnAc() ? "插电" : "电池"));
            if (st != null)
            {
                sb.AppendLine("电源计划：" + st.SchemeName + " (" + st.SchemeGuid + ")");
                sb.AppendLine("插电模式：" + PowerCtl.OverlayName(st.AcOverlay) + "；电池模式：" + PowerCtl.OverlayName(st.DcOverlay));
                sb.AppendLine("插电 关屏/睡眠：" + TimeText(st.AcVideoIdle) + " / " + TimeText(st.AcStandbyIdle) + "，最大处理器 " + st.AcMaxCpu + "%");
                sb.AppendLine("电池 关屏/睡眠：" + TimeText(st.DcVideoIdle) + " / " + TimeText(st.DcStandbyIdle) + "，最大处理器 " + st.DcMaxCpu + "%");
            }
            sb.AppendLine("插拔锁定：" + (UiFile.Get("lockMode", "0") == "1" ? "开" : "关")
                + "；节电模式常开：" + (SaverPolicy.IsOn() ? "开" : "关"));
            // 远程桌面/自动化环境里 OLE 的 Flush 会抛“所请求的剪贴板操作失败”，
            // 这里改用 Win32 直接写入，失败时重试
            if (Clip.WriteText(sb.ToString()))
                ShowPowerMsg("当前状态已复制到剪贴板。", false);
            else
                ShowPowerMsg("复制失败：剪贴板被占用，请稍后再试。", true);
        }

        // ── 插拔自动锁定模式 ─────────────────────────────────
        static string OverlayGuidAt(ComboBox box)
        {
            if (box.SelectedIndex == 0) return PowerCtl.OverlayBattery;
            if (box.SelectedIndex == 2) return PowerCtl.OverlayMaxPerf;
            return "";
        }

        // 拔插/启动时，按锁定设置强制切换当前电源来源的电源模式
        void EnforceLock(bool online, bool notify = false)
        {
            if (!_chkLock.Checked) return;

            string target = online ? OverlayGuidAt(_lockAc) : OverlayGuidAt(_lockDc);
            if (string.IsNullOrEmpty(target)) return;

            // 注意：PowerGetActualOverlayScheme 只反映“存的值”，系统可能并没有真正应用，
            // 所以这里不做相同就跳过的优化，每次电源变化都强制写一次。
            string before = PowerCtl.ActualOverlay();
            string err;
            bool ok = PowerCtl.SetOverlay(target, out err);
            string after = PowerCtl.ActualOverlay();

            if (ok)
            {
                LogStore.Write("CONFIG", "lock " + (online ? "ac" : "dc") + "=" + PowerCtl.OverlayName(target)
                    + (SameGuid(before, target) ? " forced" : "")
                    + " before=" + ShortGuid(before) + " after=" + ShortGuid(after));
                ShowPowerMsg("已按锁定设置切到「" + PowerCtl.OverlayName(target) + "」", false);
                if (notify)
                {
                    try
                    {
                        if (_tray != null) _tray.ShowBalloonTip(2500, "电源模式已锁定",
                            (online ? "插电" : "电池") + "：" + PowerCtl.OverlayName(target), ToolTipIcon.Info);
                    }
                    catch { }
                }
                RefreshPowerUi();
                LoadLogs();
            }
            else
            {
                LogStore.Write("WARN", "lock fail " + (online ? "ac" : "dc") + ": " + err + " before=" + ShortGuid(before));
                ShowPowerMsg("锁定切换失败：" + err, true);
            }
        }

        static string ShortGuid(string g)
        {
            return string.IsNullOrEmpty(g) ? "-" : (g.Length > 8 ? g.Substring(0, 8) : g);
        }

        // 「立即应用」：把锁定目标立刻套到当前电源来源，并明确告知结果
        void ApplyLockNow()
        {
            if (!_chkLock.Checked)
            {
                ShowPowerMsg("还没有启用锁定，请先勾选「插拔自动锁定」。", true);
                return;
            }

            bool online = PowerCtl.OnAc();
            string target = online ? OverlayGuidAt(_lockAc) : OverlayGuidAt(_lockDc);
            string name = PowerCtl.OverlayName(target);
            string before = PowerCtl.ActualOverlay();
            string err;

            if (PowerCtl.SetOverlay(target, out err))
            {
                string after = PowerCtl.ActualOverlay();
                string msg = "应用成功：" + (online ? "插电" : "电池") + " → 「" + name + "」（" + DateTime.Now.ToString("HH:mm:ss") + "）";
                ShowPowerMsg(msg, false);
                LogStore.Write("CONFIG", "lock-apply " + (online ? "ac" : "dc") + "=" + name + " OK"
                    + " before=" + ShortGuid(before) + " after=" + ShortGuid(after));
                RefreshPowerUi();
                RefreshStatus();
                LoadLogs();
                try
                {
                    if (_tray != null) _tray.ShowBalloonTip(2500, "电源模式已应用", (online ? "插电" : "电池") + "：" + name, ToolTipIcon.Info);
                }
                catch { }
            }
            else
            {
                ShowPowerMsg("应用失败：" + err, true);
                LogStore.Write("WARN", "lock-apply fail: " + err + " before=" + ShortGuid(before));
            }
        }

        // 轮询电源来源变化：不依赖电源事件广播，2 秒内必能发现拔插
        void CheckPowerSource()
        {
            try
            {
                int st = PowerCtl.PowerLineState();
                if (st < 0) return; // 切换瞬间读不到，下一轮再说
                bool online = st == 1;
                _navPower.Text = online ? "电源：插电" : "电源：电池";
                if (!_havePowerSource)
                {
                    _havePowerSource = true;
                    _lastOnline = online;
                    return;
                }
                if (online == _lastOnline) return;
                _lastOnline = online;
                PowerState ps = PowerCtl.Read();
                LogStore.Write("CONFIG", "power-source " + (online ? "ac" : "dc")
                    + " plan=" + ShortGuid(ps.SchemeGuid)
                    + " acOv=" + ShortGuid(ps.AcOverlay) + " dcOv=" + ShortGuid(ps.DcOverlay)
                    + " actual=" + ShortGuid(PowerCtl.ActualOverlay())
                    + " effective=" + ShortGuid(PowerCtl.EffectiveOverlay()));
                RefreshStatus();
                RefreshPowerUi();
                EnforceLock(online, true);

                // 系统/OEM 服务可能稍后才套用自己的模式，再确认两次
                var again = new Timer();
                again.Interval = 1800;
                again.Tick += delegate
                {
                    again.Stop();
                    again.Dispose();
                    if (PowerCtl.PowerLineState() != (online ? 1 : 0)) return;
                    EnforceLock(online);

                    var late = new Timer();
                    late.Interval = 2500;
                    late.Tick += delegate
                    {
                        late.Stop();
                        late.Dispose();
                        if (PowerCtl.PowerLineState() == (online ? 1 : 0)) EnforceLock(online);
                    };
                    late.Start();
                };
                again.Start();

                if (!_chkAutoBright.Checked) return;
                int pct = online ? LevelValue(_acBrightLevel) : LevelValue(_dcBrightLevel);
                ApplyBrightness(pct);
                ShowPowerMsg("检测到" + (online ? "接入电源" : "改用电池") + "，已把亮度设为 " + pct + "%", false);
                LogStore.Write("CONFIG", "auto-bright " + (online ? "ac" : "dc") + "=" + pct);
                LoadLogs();
            }
            catch { }
        }

        void ToggleLock(bool on)
        {
            if (_lockSyncing) return;

            if (!on)
            {
                RestoreLockBackup();
                return;
            }

            // 先完整备份当前状态，再允许修改
            try
            {
                PowerCtl.SaveSnapshot(PowerCtl.Read(), PowerCtl.LockBackupPath);
            }
            catch (Exception ex)
            {
                _lockSyncing = true;
                _chkLock.Checked = false;
                _lockSyncing = false;
                ShowPowerMsg("备份失败，未启用锁定：" + ex.Message, true);
                return;
            }

            UiFile.Set("lockMode", "1");
            UiFile.Set("lockAc", OverlayGuidAt(_lockAc));
            UiFile.Set("lockDc", OverlayGuidAt(_lockDc));
            _lockAc.Enabled = true;
            _lockDc.Enabled = true;

            string acName = PowerCtl.OverlayName(OverlayGuidAt(_lockAc));
            string dcName = PowerCtl.OverlayName(OverlayGuidAt(_lockDc));
            LogStore.Write("CONFIG", "lock-on ac=" + acName + " dc=" + dcName);
            EnforceLock(PowerCtl.OnAc());
            ShowPowerMsg("已启用：插电用「" + acName + "」，电池用「" + dcName + "」", false);
            LoadLogs();
        }

        void RestoreLockBackup()
        {
            bool wasOn = _chkLock.Checked || UiFile.Get("lockMode", "0") == "1";
            PowerState snap = PowerCtl.LoadSnapshot(PowerCtl.LockBackupPath);
            bool online = PowerCtl.OnAc();

            _lockSyncing = true;
            _chkLock.Checked = false;
            _lockAc.Enabled = false;
            _lockDc.Enabled = false;
            _lockSyncing = false;
            UiFile.Set("lockMode", "0");

            if (!wasOn && snap == null)
            {
                ShowPowerMsg("当前没有启用锁定。", false);
                return;
            }

            if (snap == null)
            {
                LogStore.Write("CONFIG", "lock-off no-backup");
                ShowPowerMsg("已关闭锁定（没找到备份文件，未做还原）。", false);
                RefreshPowerUi();
                return;
            }

            string target = online ? snap.AcOverlay : snap.DcOverlay;
            string err;
            bool ok = PowerCtl.SetOverlay(target, out err);
            string name = PowerCtl.OverlayName(target);

            RefreshPowerUi();
            LoadLogs();
            LogStore.Write("CONFIG", "lock-off " + (online ? "ac" : "dc") + "=" + name + (ok ? "" : " FAIL"));
            ShowPowerMsg(ok
                ? "已关闭锁定，当前" + (online ? "插电" : "电池") + "模式还原为「" + name + "」"
                : "已关闭锁定，但还原失败：" + err, !ok);
        }

        // 节电模式常开：写入/删除系统“节能器电池阈值”策略（需管理员，会弹一次 UAC）
        void ToggleSaver(bool on)
        {
            if (on) SaverPolicy.SaveBackup();

            string msg;
            bool ok;
            Cursor old = Cursor;
            try { Cursor = Cursors.WaitCursor; } catch { }
            try { ok = SaverPolicy.Apply(on, out msg); }
            finally { try { Cursor = old; } catch { } }

            if (!ok)
            {
                _powerSyncing = true;
                try { _chkSaver.Checked = SaverPolicy.IsOn(); }
                finally { _powerSyncing = false; }
            }

            LogStore.Write("CONFIG", "saver-lock " + (on ? "on" : "off") + (ok ? " OK" : " FAIL " + msg));
            ShowPowerMsg(msg, !ok);
            LoadLogs();
        }

        void RelaunchAsAdmin()
        {
            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath, "--takeover");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                LogStore.Write("CONFIG", "relaunch as admin");
                _exiting = true;
                Close();
            }
            catch
            {
                ShowPowerMsg("未获得管理员授权，只有电源模式/计划这类不需要提权的项可改。", true);
            }
        }

        // 状态表里各技术名词的解释
        static string InfoTip(string key)
        {
            switch (key)
            {
                case "CPU 频率":
                    return "处理器当前实际运行频率（标称主频 × 性能百分比）。\r\n省电档会明显低于标称频率，性能档会接近或达到最高频率。";
                case "CPU 占用":
                    return "所有程序合计占用 CPU 的比例。数值高说明当前有程序在大量计算。";
                case "屏幕型号":
                    return "内置屏面板的型号代码（从显示器 EDID 读取），用于区分不同面板。";
                case "显卡":
                    return "当前使用的显卡或核显型号。亮度、Vari-Bright 等显示相关功能由它控制。";
                case "制造商 / 日期":
                    return "显示器厂商代码与生产年份、周数（从显示器信息读取）。";
                case "驱动版本":
                    return "显卡驱动的版本号。显示异常或亮度调节失效时，可据此判断是否需要更新驱动。";
                case "亮度档位":
                    return "这块屏固件支持的亮度级数与范围，例如 0-100（101 级）表示可调 101 个档位。";
                case "显示器数量":
                    return "系统当前识别到的显示器数量（含内置屏与虚拟显示器）。";
                case "外接屏":
                    return "外接显示器的数量与型号。外接屏亮度通过 DDC/CI 调节，需要显示器支持。";
                case "外接屏亮度":
                    return "外接屏当前亮度（DDC/CI 读回值）。部分显示器不支持读回，会显示「未知」。";
                case "当前亮度":
                    return "内置屏当前亮度百分比。";
                case "电源方案":
                    return "Windows 当前启用的电源计划（平衡、高性能、节电等），它决定处理器、硬盘、显卡的省电策略。";
                case "节电模式":
                    return "Windows 的「节电模式 / Battery Saver」：限制后台活动、可能压暗屏幕来延长续航。\r\n本程序可以让它「常开」，即使插着电源也生效。";
                case "节电亮度系数":
                    return "开启节电模式时，系统把背光乘以的百分比系数。系数越低，屏幕越暗、越省电。";
                case "自适应亮度":
                    return "根据环境光传感器自动调整屏幕亮度。桌面显示器一般没有该传感器，通常显示「已关闭」。";
                case "变暗后亮度":
                    return "系统在「一段时间无操作但还没关屏」时，把屏幕变暗到的亮度百分比。";
                case "显示器超时":
                    return "无人操作多久后自动关闭屏幕（只关屏，不一定进入睡眠）。";
                case "Vari-Bright":
                    return "AMD 显卡的低功耗显示技术：自动降低背光电流和对比度来省电。\r\n等级越高省电越明显，但画面可能偏灰偏暗。可在「状态」页顶部按钮一键关闭或恢复。";
                case "虚拟显示驱动":
                    return "远程桌面、投屏、虚拟显示器等软件创建的虚拟显示适配器，可能影响亮度接口的可用性。";
                case "电池":
                    return "电池剩余电量与当前供电状态（已接电源 / 使用电池）。";
                case "剩余时间":
                    return "系统根据当前功耗对电池续航的估算值；刚插拔电源时可能暂时显示「系统未估算」。";
                case "系统版本":
                    return "Windows 的产品名称、功能更新版本与内部版本号。";
                case "日志文件":
                    return "今天写入的操作日志文件名；日志里记录每次亮度与电源设置的变更结果。";
                case "日志条数":
                    return "当前筛选条件下显示的日志条数。";
            }
            return "";
        }

        // 给控件挂名词提示；总开关关闭时 _tip.Active=false，全部提示都不弹
        void Tip(Control c, string text)
        {
            if (c == null || string.IsNullOrEmpty(text)) return;
            _tip.SetToolTip(c, text);
        }

        Button MakeButton(string text, int width, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(width, 32),
                Margin = new Padding(0, 0, 8, 0),
                FlatStyle = FlatStyle.System
            };
            b.Click += onClick;
            return b;
        }

        Control BuildInfoPanel()
        {
            var gb = new GroupBox { Text = "系统状态", Dock = DockStyle.Fill, Padding = new Padding(10) };
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 4,
                Padding = new Padding(0)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            string[] captions = new string[]
            {
                "CPU 频率", "CPU 占用",
                "屏幕型号", "显卡",
                "制造商 / 日期", "驱动版本",
                "亮度档位", "显示器数量",
                "外接屏", "外接屏亮度",
                "当前亮度", "电源方案",
                "节电模式", "节电亮度系数",
                "自适应亮度", "变暗后亮度",
                "显示器超时", "Vari-Bright",
                "虚拟显示驱动", "电池",
                "剩余时间", "系统版本",
                "日志文件", "日志条数"
            };

            for (int i = 0; i < captions.Length; i += 2)
            {
                int row = i / 2;
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

                Label c1 = MakeCaption(captions[i]);
                grid.Controls.Add(c1, 0, row);
                var v1 = MakeValue();
                grid.Controls.Add(v1, 1, row);
                _info[captions[i]] = v1;
                string t1 = InfoTip(captions[i]);
                Tip(c1, t1);
                Tip(v1, t1);

                if (i + 1 < captions.Length)
                {
                    Label c2 = MakeCaption(captions[i + 1]);
                    grid.Controls.Add(c2, 2, row);
                    var v2 = MakeValue();
                    grid.Controls.Add(v2, 3, row);
                    _info[captions[i + 1]] = v2;
                    string t2 = InfoTip(captions[i + 1]);
                    Tip(c2, t2);
                    Tip(v2, t2);
                }
            }

            scroll.Controls.Add(grid);
            gb.Controls.Add(scroll);
            return gb;
        }

        Label MakeCaption(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.DimGray
            };
        }

        Label MakeValue()
        {
            return new Label
            {
                Text = "-",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            };
        }

        Control BuildLogPanel()
        {
            var gb = new GroupBox { Text = "操作记录", Dock = DockStyle.Fill, Padding = new Padding(10) };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 32 };

            var btnOpen = new Button
            {
                Text = "打开日志文件夹",
                Size = new Size(124, 28),
                Location = new Point(0, 2),
                FlatStyle = FlatStyle.System
            };
            btnOpen.Click += (s, e) => OpenFolder();

            var btnReload = new Button
            {
                Text = "重新读取",
                Size = new Size(92, 28),
                Location = new Point(132, 2),
                FlatStyle = FlatStyle.System
            };
            btnReload.Click += (s, e) => LoadLogs();

            var btnCopyState = new Button
            {
                Text = "复制状态",
                Size = new Size(88, 28),
                Location = new Point(908, 2),
                FlatStyle = FlatStyle.System
            };
            btnCopyState.Click += (s, e) => CopyStateSummary();

            _lblLogPath.ForeColor = Color.Gray;
            _lblLogPath.AutoSize = false;
            _lblLogPath.AutoEllipsis = true;
            _lblLogPath.SetBounds(392, 8, 500, 20);
            _lblLogPath.Text = LogStore.DisplayPath(LogStore.TodayFile);

            var capFilter = new Label
            {
                Text = "只显示",
                AutoSize = false,
                ForeColor = Color.DimGray
            };
            capFilter.SetBounds(232, 6, 50, 20);

            _logFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            _logFilter.SetBounds(284, 3, 100, 24);
            _logFilter.Items.AddRange(new object[] { "全部", "设置 SET", "变更 CHANGE", "异常", "其他" });
            _logFilter.SelectedIndex = 0;
            _logFilter.SelectedIndexChanged += (s, e) => LoadLogs();

            bottom.Controls.Add(btnOpen);
            bottom.Controls.Add(btnReload);
            bottom.Controls.Add(btnCopyState);
            bottom.Controls.Add(capFilter);
            bottom.Controls.Add(_logFilter);
            bottom.Controls.Add(_lblLogPath);

            // 窗口宽度变化时，「复制状态」贴右，日志路径自动伸缩
            bottom.Resize += (s, e) =>
            {
                btnCopyState.Left = Math.Max(0, bottom.ClientSize.Width - btnCopyState.Width);
                _lblLogPath.Width = Math.Max(120, btnCopyState.Left - 8 - _lblLogPath.Left);
            };

            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            Tip(_list, "操作日志：记录每次亮度设置、电源修改、模式切换的结果。\r\nSET＝亮度写入；CHANGE＝电源设置变更；CONFIG＝程序配置；WARN/FAIL＝警告或失败。\r\n鼠标悬停某一行可看完整详情。");
            _list.FullRowSelect = true;
            _list.GridLines = true;
            _list.HideSelection = false;
            _list.Columns.Add("时间", 150);
            _list.Columns.Add("操作", 70);
            _list.Columns.Add("详情", 700);

            gb.Controls.Add(_list);
            gb.Controls.Add(bottom);

            gb.Resize += (s, e) =>
            {
                if (_list.Columns.Count > 2)
                    _list.Columns[2].Width = Math.Max(300, _list.ClientSize.Width - 240);
            };
            return gb;
        }

        // ── 行为 ─────────────────────────────────────────────
        void ReloadTargets()
        {
            string keep = "";
            MonitorTarget old = _target.SelectedItem as MonitorTarget;
            if (old != null) keep = old.Name;

            _syncing = true;
            try
            {
                _targets.Clear();

                var inner = new MonitorTarget();
                inner.Internal = true;
                string code = Ddc.InternalCode();
                inner.Name = string.IsNullOrEmpty(code)
                    ? "内置屏（WMI）"
                    : "内置屏 " + code + "（WMI）";
                _targets.Add(inner);

                _targets.AddRange(Ddc.ExternalTargets());

                _target.Items.Clear();
                foreach (MonitorTarget t in _targets) _target.Items.Add(t);

                int sel = 0;
                if (keep.Length > 0)
                {
                    for (int i = 0; i < _targets.Count; i++)
                        if (_targets[i].Name == keep) { sel = i; break; }
                }
                if (_target.Items.Count > 0) _target.SelectedIndex = sel;

                _extKnown = -1;
                foreach (MonitorTarget t in _targets)
                {
                    if (t.Internal) continue;
                    int v;
                    if (Ddc.TryRead(t.Device, out v)) { _extKnown = v; break; }
                }
            }
            finally { _syncing = false; }
        }

        MonitorTarget CurrentTarget()
        {
            return _target.SelectedItem as MonitorTarget;
        }

        int TargetGet(MonitorTarget t)
        {
            if (t == null || t.Internal) return Brightness.Get();
            int v;
            if (Ddc.TryRead(t.Device, out v)) return v;
            throw new InvalidOperationException("外接屏 DDC 读取失败");
        }

        bool TargetSet(MonitorTarget t, int percent, out int readback)
        {
            if (t == null || t.Internal)
            {
                if (!Brightness.Set(percent)) { readback = -1; return false; }
                System.Threading.Thread.Sleep(320);
                try { readback = Brightness.Get(); }
                catch { readback = -1; }
                return true;
            }
            return Ddc.TryWrite(t.Device, percent, out readback);
        }

        static int Clamp(int v)
        {
            return Math.Max(0, Math.Min(100, v));
        }

        void SyncFromTarget()
        {
            MonitorTarget t = CurrentTarget();
            if (t == null) return;
            try
            {
                int v = TargetGet(t);
                _lastKnown = v;
                _syncing = true;
                if (_track.Value != Clamp(v)) _track.Value = Clamp(v);
                _lblBig.Text = v + "%";
                _syncing = false;
                _msg.Text = "";
            }
            catch
            {
                _syncing = false;
                _lblBig.Text = "--%";
                _msg.Text = "读取 " + t.Name + " 的亮度失败。";
            }
        }

        void ApplyBrightness(int percent)
        {
            if (_busy) return;

            if (_chkBoth.Checked && _targets.Count > 0)
            {
                _busy = true;
                try
                {
                    foreach (MonitorTarget t in _targets) ApplyOne(t, percent);
                    SyncFromTarget();
                    RefreshStatus();
                    LoadLogs();
                    Flash("已把全部屏幕设为 " + percent + "%", false);
                }
                finally { _busy = false; }
                return;
            }

            MonitorTarget cur = CurrentTarget();
            if (cur == null) return;

            _busy = true;
            try
            {
                bool ok = ApplyOne(cur, percent);
                RefreshStatus();
                LoadLogs();
                if (ok) Flash("已把 " + cur.Name + " 设为 " + percent + "%", false);
            }
            finally { _busy = false; }
        }

        bool ApplyOne(MonitorTarget t, int percent)
        {
            string tag = "target=" + t.Name + "; ";

            int before = -1;
            try { before = TargetGet(t); }
            catch (Exception ex)
            {
                LogStore.Write("WARN", tag + "read-before-failed: "
                    + ex.GetType().Name + ": " + ex.Message + " @ " + Trace(ex));
            }

            int after = -1;
            try
            {
                if (!TargetSet(t, percent, out after))
                {
                    LogStore.Write("FAIL", tag + "req=" + percent + "; before=" + before
                        + "; error=no-brightness-interface");
                    Flash(t.Internal
                        ? "未能控制内置屏：系统没有暴露亮度接口。"
                        : "未能控制 " + t.Name + "：显示器不支持 DDC/CI，或已被关闭。", true);
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogStore.Write("FAIL", string.Format("{0}req={1}; before={2}; error={3}: {4} @ {5}",
                    tag, percent, before, ex.GetType().Name, ex.Message, Trace(ex)));
                Flash("设置失败：" + ex.GetType().Name + " " + ex.Message, true);
                return false;
            }

            if (!t.Internal) _extKnown = after;

            bool isCurrent = ReferenceEquals(t, CurrentTarget());
            if (isCurrent)
            {
                _lastKnown = after;
                _syncing = true;
                if (after >= 0 && _track.Value != Clamp(after)) _track.Value = Clamp(after);
                _lblBig.Text = (after >= 0 ? after + "%" : "--%");
                _syncing = false;
            }

            if (after < 0)
                LogStore.Write("SET", string.Format("{0}req={1}; before={2}; after=read-failed; result=UNKNOWN",
                    tag, percent, before));
            else if (after == percent)
                LogStore.Write("SET", string.Format("{0}req={1}; before={2}; after={3}; result=OK",
                    tag, percent, before, after));
            else
                LogStore.Write("SET", string.Format("{0}req={1}; before={2}; after={3}; result=MISMATCH",
                    tag, percent, before, after));

            return true;
        }

        void Flash(string text, bool error)
        {
            _msgSticky = error;
            _msg.ForeColor = error ? Color.Firebrick : Color.FromArgb(0, 118, 60);
            _msg.Text = text;
            _flash.Stop();
            if (!error) _flash.Start();
        }

        void Nudge(int delta)
        {
            if (_busy) return;
            int v = Clamp(_track.Value + delta);
            _syncing = true;
            _track.Value = v;
            _lblBig.Text = v + "%";
            _syncing = false;
            ApplyBrightness(v);
        }

        // ── 全局快捷键 ───────────────────────────────────────
        const uint MOD_ALT = 0x0001;
        const uint MOD_CONTROL = 0x0002;
        const int HK_UP = 1;
        const int HK_DOWN = 2;

        [DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);

        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                RegisterHotKey(Handle, HK_UP, MOD_CONTROL | MOD_ALT, 0x26);
                RegisterHotKey(Handle, HK_DOWN, MOD_CONTROL | MOD_ALT, 0x28);
            }
            catch { }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            try
            {
                UnregisterHotKey(Handle, HK_UP);
                UnregisterHotKey(Handle, HK_DOWN);
            }
            catch { }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312)
            {
                int id = m.WParam.ToInt32();
                if (id == HK_UP) { Nudge(5); return; }
                if (id == HK_DOWN) { Nudge(-5); return; }
            }
            if (Single.ShowMessage != 0 && m.Msg == (int)Single.ShowMessage)
            {
                RestoreFromTray();
                return;
            }
            base.WndProc(ref m);
        }

        // ── 托盘 ─────────────────────────────────────────────
        void BuildTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主界面", null, (s, e) => RestoreFromTray());
            menu.Items.Add(new ToolStripSeparator());

            int[] presets = new int[] { 100, 90, 80, 70, 60, 50 };
            foreach (int v in presets)
            {
                int level = v;
                menu.Items.Add(level + "%", null, (s, e) => ApplyBrightness(level));
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("省电模式", null, (s, e) => ApplyPreset(0));
            menu.Items.Add("均衡模式", null, (s, e) => ApplyPreset(1));
            menu.Items.Add("性能模式", null, (s, e) => ApplyPreset(2));

            var saverItem = new ToolStripMenuItem("节电模式常开（需管理员）");
            saverItem.Click += (s, e) => { ToggleSaver(!SaverPolicy.IsOn()); saverItem.Checked = SaverPolicy.IsOn(); };
            menu.Opening += (s, e) => { saverItem.Checked = SaverPolicy.IsOn(); };
            menu.Items.Add(saverItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => { _exiting = true; Close(); });

            _tray = new NotifyIcon();
            _tray.Icon = Icon;
            _tray.Text = Program.AppName + " " + Program.Version;
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            _tray.DoubleClick += (s, e) => RestoreFromTray();
        }

        void RestoreFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                try
                {
                    if (_tray != null)
                        _tray.ShowBalloonTip(1500, Program.AppName, "已最小化到托盘，仍在后台运行。双击图标可再次打开。", ToolTipIcon.Info);
                }
                catch { }
                return;
            }
            LogStore.Write("CONFIG", "exit reason=" + e.CloseReason);
            base.OnFormClosing(e);
        }

        static Icon MakeIcon()
        {
            // 优先使用编译进 exe 的多尺寸图标，资源缺失时退回 exe 关联图标
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream("BrightnessApp.app.ico"))
                {
                    if (s != null) return new Icon(s);
                }
            }
            catch { }
            try
            {
                Icon exe = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (exe != null) return exe;
            }
            catch { }
            return SystemIcons.Application;
        }

        static string Trace(Exception ex)
        {
            try
            {
                string s = (ex.ToString() ?? "").Replace("\r", " ").Replace("\n", " | ");
                if (s.Length > 700) s = s.Substring(0, 700);
                return s;
            }
            catch { return ""; }
        }

        void PollBrightness()
        {
            if (_busy || _polling) return;
            _polling = true;
            try
            {
                MonitorTarget t = CurrentTarget();
                if (t == null) return;
                int cur = TargetGet(t);
                if (!t.Internal) _extKnown = cur;

                if (cur != _lastKnown)
                {
                    if (_lastKnown >= 0)
                        LogStore.Write("CHANGE", string.Format("target={0}; from={1}; to={2}",
                            t.Name, _lastKnown, cur));

                    _lastKnown = cur;
                    _syncing = true;
                    _track.Value = Clamp(cur);
                    _lblBig.Text = cur + "%";
                    _syncing = false;

                    RefreshStatus();
                    LoadLogs();
                }
            }
            catch { }
            finally { _polling = false; }
        }

        void RefreshStatus()
        {
            try
            {
                MonitorTarget t = CurrentTarget();
                int bright = -1;
                try { bright = TargetGet(t); } catch { }

                if (bright >= 0)
                {
                    _lblBig.Text = bright + "%";
                    if (_track.Value != Clamp(bright))
                    {
                        _syncing = true;
                        _track.Value = Clamp(bright);
                        _syncing = false;
                    }
                }

                int levels = Brightness.LevelCount();

                string gid = SysInfo.Gid();
                int coeff = SysInfo.BsCoeff(gid);
                int adaptive = SysInfo.Adaptive(gid);
                int dimmed = SysInfo.DimmedBrightness(gid);
                int timeout = SysInfo.DisplayTimeoutSec(gid);
                string vb = SysInfo.VariBrightLevel();

                string extNames = "";
                int extCount = 0;
                foreach (MonitorTarget x in _targets)
                {
                    if (x.Internal) continue;
                    extCount++;
                    if (extNames.Length > 0) extNames += " / ";
                    extNames += x.Name.Replace("外接屏 ", "").Replace("（DDC）", "");
                }

                Set("屏幕型号", SysInfo.MonitorModel(), false);
                Set("制造商 / 日期", SysInfo.MonitorMakerDate(), false);
                Set("显卡", SysInfo.GpuName(), false);
                Set("驱动版本", SysInfo.GpuDriver(), false);
                Set("亮度档位", levels > 0 ? ("0-" + (levels - 1) + "（" + levels + " 级）") : "未知", false);
                Set("显示器数量", SysInfo.MonitorCount() + " 台", false);
                Set("外接屏", extCount > 0 ? (extCount + " 台 · " + extNames) : "未检测到", extCount == 0);
                Set("外接屏亮度", _extKnown >= 0 ? (_extKnown + "%") : "未知", false);
                Set("当前亮度", bright >= 0 ? (bright + "%") : "读取失败", bright < 0);
                Set("电源方案", SysInfo.SchemeName(gid), false);
                Set("节电模式", SysInfo.EnergySaverStatus(), false);
                Set("节电亮度系数", coeff + "%", coeff < 100);
                Set("自适应亮度", adaptive == 1 ? "已启用" : "已关闭", adaptive == 1);
                Set("变暗后亮度", dimmed + "%", false);
                Set("显示器超时", timeout > 0 ? (timeout / 60) + " 分钟" : "从不", false);
                Set("Vari-Bright", vb == "未找到" ? "未找到" : ("等级 " + vb), vb != "0" && vb != "未找到");
                Set("虚拟显示驱动", SysInfo.VirtualAdapterCount() + " 个", SysInfo.VirtualAdapterCount() > 0);
                Set("电池", SysInfo.BatteryText(), false);
                Set("剩余时间", SysInfo.BatteryRemaining(), false);
                UpdateCpuMeter();
                Set("系统版本", SysInfo.OsVersion(), false);

                // WMI 客户端偶尔会短暂失效，导致若干字段取不到值：延迟再取一次
                if (HasUnknown() && _retryLeft > 0)
                {
                    _retryLeft--;
                    _retry.Stop();
                    _retry.Start();
                }
            }
            catch { }
        }

        // CPU 频率/占用：每 2 秒刷新，可直观看到性能档、节能档是否生效
        void UpdateCpuMeter()
        {
            Set("CPU 频率", CpuMeter.FreqText(), false);
            Set("CPU 占用", CpuMeter.LoadText(), false);
        }

        bool HasUnknown()
        {
            return Value("屏幕型号") == "未知"
                || Value("显卡") == "未知"
                || Value("驱动版本") == "未知"
                || Value("亮度档位") == "未知"
                || Value("显示器数量") == "0 台";
        }

        string Value(string key)
        {
            Label lb;
            if (_info.TryGetValue(key, out lb)) return lb.Text;
            return "";
        }

        void Set(string key, string value, bool warn)
        {
            Label lb;
            if (_info.TryGetValue(key, out lb))
            {
                lb.Text = value;
                lb.ForeColor = warn ? Color.DarkOrange : Color.FromArgb(30, 30, 40);
            }
        }

        void LoadLogs()
        {
            try
            {
                _list.BeginUpdate();
                _list.Items.Clear();
                List<string[]> rows = LogStore.ReadRecent(400);
                int filter = _logFilter.SelectedIndex < 0 ? 0 : _logFilter.SelectedIndex;
                int shownCount = 0;
                foreach (string[] row in rows)
                {
                    if (!MatchFilter(filter, row[1])) continue;
                    var item = new ListViewItem(row[0]);
                    item.SubItems.Add(row[1]);
                    item.SubItems.Add(row[2]);
                    if (row[1] == "FAIL") item.ForeColor = Color.Firebrick;
                    else if (row[1] == "WARN") item.ForeColor = Color.DarkOrange;
                    _list.Items.Add(item);
                    shownCount++;
                }
                _list.EndUpdate();

                string shown = string.IsNullOrEmpty(LogStore.LastReadFile)
                    ? LogStore.TodayFile : LogStore.LastReadFile;
                _lblLogPath.Text = LogStore.DisplayPath(shown);
                Set("日志文件", Path.GetFileName(shown), false);
                Set("日志条数", shownCount + " 条", false);
            }
            catch { }
        }

        static bool MatchFilter(int filter, string action)
        {
            if (filter == 0) return true;
            if (filter == 1) return action == "SET";
            if (filter == 2) return action == "CHANGE";
            if (filter == 3) return action == "FAIL" || action == "WARN";
            return action != "SET" && action != "CHANGE" && action != "FAIL" && action != "WARN";
        }

        void OpenFolder()
        {
            try
            {
                if (!Directory.Exists(LogStore.Dir)) Directory.CreateDirectory(LogStore.Dir);
                Process.Start("explorer.exe", LogStore.Dir);
            }
            catch { }
        }

        void ExportReport()
        {
            try
            {
                if (!Directory.Exists(LogStore.Dir)) Directory.CreateDirectory(LogStore.Dir);
                string file = Path.Combine(LogStore.Dir,
                    "report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");

                var sb = new StringBuilder();
                sb.AppendLine("亮度和显示状态诊断报告");
                sb.AppendLine("导出时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                MonitorTarget t = CurrentTarget();
                sb.AppendLine("控制目标：" + (t != null ? t.Name : "-"));
                sb.AppendLine(new string('-', 60));

                foreach (KeyValuePair<string, Label> kv in _info)
                    sb.AppendLine(string.Format("{0,-14}{1}", kv.Key, kv.Value.Text));

                sb.AppendLine(new string('-', 60));
                sb.AppendLine("最近操作记录：");
                foreach (string[] row in LogStore.ReadRecent(200))
                    sb.AppendLine(string.Format("{0} | {1,-6} | {2}", row[0], row[1], row[2]));

                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
                LogStore.Write("REPORT", "file=" + Path.GetFileName(file));
                LoadLogs();
                Process.Start("notepad.exe", file);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出失败：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void RunFix(string args)
        {
            try
            {
                string ps1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fix-Brightness-Source.ps1");
                if (!File.Exists(ps1))
                {
                    MessageBox.Show("找不到 Fix-Brightness-Source.ps1，请确认它和本程序在同一目录。",
                        "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -NoExit -File \"" + ps1 + "\" " + args)
                {
                    Verb = "runas",
                    UseShellExecute = true
                };

                LogStore.Write("FIX", args);
                Process.Start(psi);
                LoadLogs();
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败（可能取消了管理员授权）：" + ex.Message,
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void StartSafe(string file, string args)
        {
            try { Process.Start(file, args); }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_poll != null) _poll.Stop();
            if (_debounce != null) _debounce.Stop();
            if (_retry != null) _retry.Stop();
            if (_flash != null) _flash.Stop();
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }

            // 退出时如果锁定还开着，把当前电源来源的模式还原，避免留下副作用
            try
            {
                if (UiFile.Get("lockMode", "0") == "1")
                {
                    PowerState snap = PowerCtl.LoadSnapshot(PowerCtl.LockBackupPath);
                    if (snap != null)
                    {
                        string err;
                        PowerCtl.SetOverlay(PowerCtl.OnAc() ? snap.AcOverlay : snap.DcOverlay, out err);
                        UiFile.Set("lockMode", "0");
                        LogStore.Write("CONFIG", "lock-off exit-restore");
                    }
                }
            }
            catch { }

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            base.OnFormClosed(e);
        }
    }

    // ── 节电模式（Battery Saver）常开策略 ──────────────────────────
    // Win10 没有可编程开关的接口，只能用“节能器电池阈值”策略：阈值=100% 时，
    // 用电池会自动开启节电模式；插电时由系统自行管理。
    static class SaverPolicy
    {
        const string KeyPath =
            @"SOFTWARE\Policies\Microsoft\Power\PowerSettings\E69653CA-CF7F-4F05-AA73-CB833FA90AD4";

        public static string BackupPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "battery-saver-backup.txt"); }
        }

        public static bool IsOn()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(KeyPath))
                {
                    if (k == null) return false;
                    object v = k.GetValue("DCSettingIndex");
                    if (v == null) return false;
                    return Convert.ToInt32(v) >= 100;
                }
            }
            catch { return false; }
        }

        // 插电与电池都被强制锁定节电模式
        public static bool ForcedBoth
        {
            get
            {
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(KeyPath))
                    {
                        if (k == null) return false;
                        object ac = k.GetValue("ACSettingIndex");
                        object dc = k.GetValue("DCSettingIndex");
                        if (ac == null || dc == null) return false;
                        return Convert.ToInt32(ac) >= 100 && Convert.ToInt32(dc) >= 100;
                    }
                }
                catch { return false; }
            }
        }

        // 插电、电池都强制开启节电模式（超级节能用）
        public static bool ForceAlwaysOn(out string msg)
        {
            msg = "";
            string script = Path.Combine(Path.GetTempPath(), "saver-force-on.ps1");
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("$ErrorActionPreference='SilentlyContinue'");
                sb.AppendLine("$key='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Power\\PowerSettings\\E69653CA-CF7F-4F05-AA73-CB833FA90AD4'");
                sb.AppendLine("New-Item $key -Force | Out-Null");
                sb.AppendLine("New-ItemProperty $key -Name ACSettingIndex -Value 100 -PropertyType DWord -Force | Out-Null");
                sb.AppendLine("New-ItemProperty $key -Name DCSettingIndex -Value 100 -PropertyType DWord -Force | Out-Null");
                sb.AppendLine("gpupdate /force /target:computer | Out-Null");
                File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));

                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(90000))
                    {
                        try { p.Kill(); } catch { }
                        msg = "提权任务超时，请手动再试一次";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                msg = "未获得管理员授权：" + ex.Message;
                return false;
            }

            if (!ForcedBoth)
            {
                msg = "策略已写入，但系统尚未生效（可稍后重试）";
                return false;
            }
            return true;
        }

        // 按备份文件还原节电策略（不判断 IsOn，因为备份本身可能就带 DC=100）
        public static bool RestoreBackup(out string msg)
        {
            msg = "";
            string script = Path.Combine(Path.GetTempPath(), "saver-restore.ps1");
            try
            {
                File.WriteAllText(script, BuildScript(false), new UTF8Encoding(false));

                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(90000))
                    {
                        try { p.Kill(); } catch { }
                        msg = "提权任务超时";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                msg = "未获得管理员授权：" + ex.Message;
                return false;
            }
            return true;
        }

        // 确保插电时不被节电模式限制：把策略里的“插电阈值”改为 0（电池阈值保留）
        // 阈值=100 会让系统在插电时也强制节电，CPU 被钉在基频、完全失去睿频
        public static bool AllowPluggedIn(out string msg)
        {
            msg = "";
            if (AcThresholdOk()) return true;

            string script = Path.Combine(Path.GetTempPath(), "saver-ac-off.ps1");
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("$ErrorActionPreference='SilentlyContinue'");
                sb.AppendLine("$key='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Power\\PowerSettings\\E69653CA-CF7F-4F05-AA73-CB833FA90AD4'");
                sb.AppendLine("if (Test-Path $key) { New-ItemProperty $key -Name ACSettingIndex -Value 0 -PropertyType DWord -Force | Out-Null }");
                sb.AppendLine("gpupdate /force /target:computer | Out-Null");
                File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));

                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(90000))
                    {
                        try { p.Kill(); } catch { }
                        msg = "提权任务超时，请手动再试一次";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                msg = "未获得管理员授权：" + ex.Message;
                return false;
            }

            if (!AcThresholdOk())
            {
                msg = "策略已写入，但系统尚未生效（可稍后重试）";
                return false;
            }
            return true;
        }

        static bool AcThresholdOk()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(KeyPath))
                {
                    if (k == null) return true;
                    object v = k.GetValue("ACSettingIndex");
                    if (v == null) return true;
                    return Convert.ToInt32(v) == 0;
                }
            }
            catch { return false; }
        }

        // 首次启用前把原始策略状态存下来，方便日后原样回滚
        public static void SaveBackup()
        {
            if (File.Exists(BackupPath)) return;
            WriteBackup();
        }

        // 每次打开开关都记录当时的真实状态，保证“关闭时还原到打开前”
        public static void SaveBackupForce()
        {
            WriteBackup();
        }

        static void WriteBackup()
        {
            bool existed = false;
            string dc = "", ac = "";
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(KeyPath))
                {
                    if (k != null)
                    {
                        existed = true;
                        object o1 = k.GetValue("DCSettingIndex");
                        object o2 = k.GetValue("ACSettingIndex");
                        if (o1 != null) dc = o1.ToString();
                        if (o2 != null) ac = o2.ToString();
                    }
                }
            }
            catch { }

            var sb = new StringBuilder();
            sb.AppendLine("at=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("existed=" + (existed ? "1" : "0"));
            sb.AppendLine("dc=" + dc);
            sb.AppendLine("ac=" + ac);
            File.WriteAllText(BackupPath, sb.ToString(), new UTF8Encoding(false));
        }

        public static bool Apply(bool on, out string msg)
        {
            msg = "";
            string script = Path.Combine(Path.GetTempPath(), "saver-lock.ps1");
            try
            {
                File.WriteAllText(script, BuildScript(on), new UTF8Encoding(false));

                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(90000))
                    {
                        try { p.Kill(); } catch { }
                        msg = "提权任务超时，请手动再试一次";
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                msg = "未获得管理员授权：" + ex.Message;
                return false;
            }

            bool now = IsOn();
            if (now != on)
            {
                msg = on ? "策略已写入，但系统尚未生效（可能需重启）" : "策略已删除，但系统尚未还原";
                return false;
            }
            msg = on ? "已启用：用电池时自动保持节电模式" : "已还原为原始策略";
            return true;
        }

        static string BuildScript(bool on)
        {
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference='Stop'");
            sb.AppendLine("$key='HKLM:\\SOFTWARE\\Policies\\Microsoft\\Power\\PowerSettings\\E69653CA-CF7F-4F05-AA73-CB833FA90AD4'");

            if (on)
            {
                sb.AppendLine("New-Item $key -Force | Out-Null");
                sb.AppendLine("New-ItemProperty $key -Name DCSettingIndex -Value 100 -PropertyType DWord -Force | Out-Null");
                // 插电阈值必须保持 0：写成 100 会在插电时也强制节电模式，CPU 被锁在基频无法睿频
                sb.AppendLine("New-ItemProperty $key -Name ACSettingIndex -Value 0 -PropertyType DWord -Force | Out-Null");
            }
            else
            {
                bool existed = false;
                string dc = "", ac = "";
                try
                {
                    foreach (string line in File.ReadAllLines(BackupPath))
                    {
                        int p = line.IndexOf('=');
                        if (p < 0) continue;
                        string key = line.Substring(0, p), val = line.Substring(p + 1).Trim();
                        if (key == "existed") existed = val == "1";
                        else if (key == "dc") dc = val;
                        else if (key == "ac") ac = val;
                    }
                }
                catch { }

                if (!existed)
                {
                    sb.AppendLine("Remove-Item $key -Recurse -Force -ErrorAction SilentlyContinue");
                }
                else
                {
                    sb.AppendLine("New-Item $key -Force | Out-Null");
                    if (dc.Length > 0)
                        sb.AppendLine("New-ItemProperty $key -Name DCSettingIndex -Value " + dc + " -PropertyType DWord -Force | Out-Null");
                    else
                        sb.AppendLine("Remove-ItemProperty $key -Name DCSettingIndex -ErrorAction SilentlyContinue");
                    if (ac.Length > 0)
                        sb.AppendLine("New-ItemProperty $key -Name ACSettingIndex -Value " + ac + " -PropertyType DWord -Force | Out-Null");
                    else
                        sb.AppendLine("Remove-ItemProperty $key -Name ACSettingIndex -ErrorAction SilentlyContinue");
                }
            }

            sb.AppendLine("gpupdate /force /target:computer | Out-Null");
            return sb.ToString();
        }
    }
}
