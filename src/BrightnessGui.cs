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

        public static void Write(string action, string detail)
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                string line = string.Format("{0} | {1,-6} | {2} | user={3} | host={4}",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), action, detail,
                    Environment.UserName, Environment.MachineName);
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

        [DllImport("powrprof.dll")]
        static extern uint PowerSetActiveOverlayScheme(Guid scheme);

        [DllImport("powrprof.dll")]
        static extern uint PowerGetActualOverlayScheme(out Guid scheme);

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
            if (string.Equals(guid, OverlayBattery, StringComparison.OrdinalIgnoreCase)) return "更好的续航";
            if (string.Equals(guid, OverlayMaxPerf, StringComparison.OrdinalIgnoreCase)) return "更好的性能";
            return "其他模式";
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
        readonly Label _lblPowerState = new Label();
        readonly Label _lblPowerMsg = new Label();
        readonly CheckBox _chkLock = new CheckBox();
        readonly ComboBox _lockAc = new ComboBox();
        readonly ComboBox _lockDc = new ComboBox();
        readonly Button _btnLockRestore = new Button();
        PowerState _powerState;
        bool _powerSyncing;
        bool _lockSyncing;

        readonly Dictionary<string, Label> _info = new Dictionary<string, Label>();
        readonly Label _msg = new Label();
        readonly ListView _list = new ListView();
        readonly Label _lblLogPath = new Label();

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
            Text = "亮度控制 · " + Environment.MachineName + " · " + Environment.UserName;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(1080, 992);
            MinimumSize = new Size(900, 700);
            Font = PickFont();
            BackColor = Color.FromArgb(250, 250, 252);
            Icon = MakeIcon();

            BuildLayout();
            BuildTray();

            LogStore.Cleanup(30);

            _debounce = new Timer { Interval = 350 };
            _debounce.Tick += (s, e) => { _debounce.Stop(); ApplyBrightness(_track.Value); };

            _poll = new Timer { Interval = 2000 };
            _poll.Tick += (s, e) => PollBrightness();

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
                EnforceLock(PowerCtl.OnAc());
                LoadLogs();
                _poll.Start();
            };

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            try
            {
                if (e.Mode != PowerModes.StatusChange) return;

                bool online = PowerCtl.OnAc();

                BeginInvoke((MethodInvoker)delegate
                {
                    // 电源来源变了，模式/超时下拉框要按新来源重新对齐
                    RefreshPowerUi();
                    EnforceLock(online);

                    // 系统可能在电源事件之后才套用自己的模式，稍后再确认一次
                    var again = new Timer();
                    again.Interval = 1500;
                    again.Tick += delegate
                    {
                        again.Stop();
                        again.Dispose();
                        EnforceLock(PowerCtl.OnAc());
                    };
                    again.Start();

                    if (!_chkAutoBright.Checked) return;

                    int pct = online ? LevelValue(_acBrightLevel) : LevelValue(_dcBrightLevel);
                    ApplyBrightness(pct);
                    ShowPowerMsg("检测到" + (online ? "接入电源" : "改用电池") + "，已把亮度设为 " + pct + "%", false);
                    LogStore.Write("CONFIG", "auto-bright " + (online ? "ac" : "dc") + "=" + pct);
                    LoadLogs();
                });
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
            catch { }
        }

        void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(12)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 156));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 188));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 176));

            root.Controls.Add(BuildTopPanel(), 0, 0);
            root.Controls.Add(BuildActionPanel(), 0, 1);
            root.Controls.Add(BuildOptionsPanel(), 0, 2);
            root.Controls.Add(BuildPowerPanel(), 0, 3);
            root.Controls.Add(BuildInfoPanel(), 0, 4);
            root.Controls.Add(BuildLogPanel(), 0, 5);

            Controls.Add(root);
        }

        Control BuildTopPanel()
        {
            var gb = new GroupBox { Text = "亮度控制", Dock = DockStyle.Fill, Padding = new Padding(10) };

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
                presetFlow.Controls.Add(b);
            }

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

        Control BuildOptionsPanel()
        {
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(2, 6, 2, 0)
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

            _chkAuto.Text = "开机自启";
            _chkAuto.AutoSize = true;
            _chkAuto.Margin = new Padding(4, 0, 20, 0);
            _chkAuto.Checked = AutoStart.Enabled();
            _chkAuto.CheckedChanged += (s, e) =>
            {
                AutoStart.Set(_chkAuto.Checked);
                LogStore.Write("CONFIG", "autostart=" + (_chkAuto.Checked ? "on" : "off"));
                Flash(_chkAuto.Checked ? "已设置开机自启。" : "已取消开机自启。", false);
            };

            var hint = new Label
            {
                Text = "快捷键 Ctrl+Alt+↑ / ↓ 微调 5%　｜　关闭窗口后驻留托盘，双击图标可再次打开",
                AutoSize = true,
                ForeColor = Color.Gray,
                Margin = new Padding(6, 2, 0, 0)
            };

            flow.Controls.Add(_chkBoth);
            flow.Controls.Add(_chkAuto);
            flow.Controls.Add(hint);
            return flow;
        }

        // ── 电源与插电设置 ──────────────────────────────────
        static readonly int[] TimeValues = new int[] { 60, 120, 180, 240, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 0 };
        static readonly int[] CpuValues = new int[] { 100, 95, 90, 85, 80, 75, 70, 60, 50 };
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

        Control BuildPowerPanel()
        {
            var gb = new GroupBox { Text = "电源与插电设置（修改前自动备份，可一键撤销）", Dock = DockStyle.Fill };

            AddCaption(gb, "电源计划", 12, 28);
            _plan.SetBounds(74, 24, 190, 26);
            _plan.DropDownStyle = ComboBoxStyle.DropDownList;
            gb.Controls.Add(_plan);

            AddCaption(gb, "电源模式", 274, 28);
            _overlay.SetBounds(336, 24, 150, 26);
            _overlay.DropDownStyle = ComboBoxStyle.DropDownList;
            _overlay.Items.AddRange(new object[] { "更好的续航", "平衡（默认）", "更好的性能" });
            _overlay.SelectedIndex = 0;
            gb.Controls.Add(_overlay);

            var btnApply = new Button { Text = "应用并备份", Font = new Font(Font.FontFamily, 9F, FontStyle.Bold) };
            btnApply.SetBounds(498, 22, 104, 30);
            btnApply.Click += (s, e) => ApplyPowerSettings();
            gb.Controls.Add(btnApply);

            var btnUndo = new Button { Text = "撤销上次修改" };
            btnUndo.SetBounds(610, 22, 110, 30);
            btnUndo.Click += (s, e) => UndoPower();
            gb.Controls.Add(btnUndo);

            _btnElevate.Text = "以管理员身份重启";
            _btnElevate.SetBounds(728, 22, 128, 30);
            _btnElevate.Visible = false;   // 只有确实写入失败时才提示提权
            _btnElevate.Click += (s, e) => RelaunchAsAdmin();
            gb.Controls.Add(_btnElevate);

            _lblPowerState.SetBounds(862, 28, 170, 20);
            _lblPowerState.TextAlign = ContentAlignment.MiddleRight;
            _lblPowerState.ForeColor = Color.FromArgb(90, 90, 100);
            _lblPowerState.AutoEllipsis = true;
            gb.Controls.Add(_lblPowerState);

            // 第二行：插电 / 电池 的超时与处理器上限
            AddCaption(gb, "插电时", 12, 64);
            AddCaption(gb, "关屏", 72, 64);
            InitTime(gb, _acOff, 114, 60);
            AddCaption(gb, "睡眠", 200, 64);
            InitTime(gb, _acSleep, 242, 60);
            AddCaption(gb, "最大处理器", 328, 64);
            _acMaxCpu.SetBounds(420, 60, 66, 24);
            _acMaxCpu.DropDownStyle = ComboBoxStyle.DropDownList;
            for (int i = 0; i < CpuValues.Length; i++) _acMaxCpu.Items.Add(CpuValues[i] + "%");
            _acMaxCpu.SelectedIndex = 0;
            gb.Controls.Add(_acMaxCpu);

            AddCaption(gb, "电池时", 504, 64);
            AddCaption(gb, "关屏", 564, 64);
            InitTime(gb, _dcOff, 606, 60);
            AddCaption(gb, "睡眠", 692, 64);
            InitTime(gb, _dcSleep, 734, 60);

            // 第三行：一键预设 + 插拔电自动亮度
            var p1 = new Button { Text = "省电模式" };
            p1.SetBounds(12, 94, 88, 28);
            p1.Click += (s, e) => ApplyPreset(0);
            gb.Controls.Add(p1);

            var p2 = new Button { Text = "均衡模式" };
            p2.SetBounds(104, 94, 88, 28);
            p2.Click += (s, e) => ApplyPreset(1);
            gb.Controls.Add(p2);

            var p3 = new Button { Text = "性能模式" };
            p3.SetBounds(196, 94, 88, 28);
            p3.Click += (s, e) => ApplyPreset(2);
            gb.Controls.Add(p3);

            _chkAutoBright.Text = "插拔电源自动设亮度";
            _chkAutoBright.SetBounds(298, 98, 150, 22);
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

            AddCaption(gb, "插电", 454, 100);
            InitLevel(gb, _acBrightLevel, 496, 96);
            _acBrightLevel.SelectedIndexChanged += (s, e) =>
            {
                if (_powerSyncing) return;
                UiFile.Set("acBright", LevelValue(_acBrightLevel).ToString());
            };
            AddCaption(gb, "电池", 558, 100);
            InitLevel(gb, _dcBrightLevel, 600, 96);
            _dcBrightLevel.SelectedIndexChanged += (s, e) =>
            {
                if (_powerSyncing) return;
                UiFile.Set("dcBright", LevelValue(_dcBrightLevel).ToString());
            };

            // 第四行：插拔自动锁定模式
            _chkLock.Text = "插拔自动锁定";
            _chkLock.SetBounds(12, 124, 150, 22);
            _chkLock.CheckedChanged += (s, e) => ToggleLock(_chkLock.Checked);
            gb.Controls.Add(_chkLock);

            AddCaption(gb, "插电", 168, 128);
            _lockAc.SetBounds(210, 124, 110, 24);
            _lockAc.DropDownStyle = ComboBoxStyle.DropDownList;
            _lockAc.Items.AddRange(new object[] { "更好的续航", "平衡（默认）", "更好的性能" });
            _lockAc.SelectedIndex = 2;
            _lockAc.Enabled = false;
            _lockAc.SelectedIndexChanged += (s, e) =>
            {
                if (_lockSyncing) return;
                UiFile.Set("lockAc", OverlayGuidAt(_lockAc));
                EnforceLock(PowerCtl.OnAc());
            };
            gb.Controls.Add(_lockAc);

            AddCaption(gb, "电池", 330, 128);
            _lockDc.SetBounds(372, 124, 110, 24);
            _lockDc.DropDownStyle = ComboBoxStyle.DropDownList;
            _lockDc.Items.AddRange(new object[] { "更好的续航", "平衡（默认）", "更好的性能" });
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
            _btnLockRestore.SetBounds(492, 122, 120, 28);
            _btnLockRestore.Click += (s, e) => RestoreLockBackup();
            gb.Controls.Add(_btnLockRestore);

            _lblPowerMsg.SetBounds(12, 156, 420, 20);
            _lblPowerMsg.AutoEllipsis = true;
            gb.Controls.Add(_lblPowerMsg);

            var hint = new Label
            {
                Text = "模式只作用于当前电源来源；锁定与还原的原始状态记录在 power-lock-backup.txt",
                ForeColor = Color.Gray,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft
            };
            hint.SetBounds(440, 156, 610, 20);
            gb.Controls.Add(hint);

            return gb;
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
            lb.SetBounds(x, y, text.Length * 16 + 6, 20);
            parent.Controls.Add(lb);
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
                }
                finally { _lockSyncing = false; }
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
                + "  插电 · 最大处理器：" + snap.AcMaxCpu + "%\r\n\r\n继续？";

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
            if (snap.DcVideoIdle >= 0 && !PowerCtl.SetVideoIdle(false, snap.DcVideoIdle, out err)) errs.Add("电池·关屏：" + err);
            if (snap.DcStandbyIdle >= 0 && !PowerCtl.SetStandbyIdle(false, snap.DcStandbyIdle, out err)) errs.Add("电池·睡眠：" + err);

            PowerCtl.Activate(out err);

            _powerState = PowerCtl.Read();
            RefreshPowerUi();
            RefreshStatus();
            LoadLogs();
            LogStore.Write("CONFIG", "power-undo: " + (errs.Count == 0 ? "OK" : string.Join(";", errs.ToArray())));
            ShowPowerMsg(errs.Count == 0 ? "已还原到备份状态。" : ("还原部分失败：" + string.Join("；", errs.ToArray())), errs.Count > 0);
        }

        // ── 插拔自动锁定模式 ─────────────────────────────────
        static string OverlayGuidAt(ComboBox box)
        {
            if (box.SelectedIndex == 0) return PowerCtl.OverlayBattery;
            if (box.SelectedIndex == 2) return PowerCtl.OverlayMaxPerf;
            return "";
        }

        // 拔插/启动时，按锁定设置强制切换当前电源来源的电源模式
        void EnforceLock(bool online)
        {
            if (!_chkLock.Checked) return;

            string target = online ? OverlayGuidAt(_lockAc) : OverlayGuidAt(_lockDc);
            if (SameGuid(PowerCtl.ActualOverlay(), target)) return;

            string err;
            if (PowerCtl.SetOverlay(target, out err))
            {
                LogStore.Write("CONFIG", "lock " + (online ? "ac" : "dc") + "=" + PowerCtl.OverlayName(target));
                ShowPowerMsg("已按锁定设置切到「" + PowerCtl.OverlayName(target) + "」", false);
                RefreshPowerUi();
                LoadLogs();
            }
            else
            {
                ShowPowerMsg("锁定切换失败：" + err, true);
            }
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

        Control BuildActionPanel()
        {
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(2, 4, 0, 0)
            };

            flow.Controls.Add(MakeButton("刷新全部", 96, (s, e) =>
            {
                _retryLeft = 3;
                ReloadTargets();
                SyncFromTarget();
                RefreshStatus();
                LoadLogs();
            }));
            flow.Controls.Add(MakeButton("导出诊断报告", 120, (s, e) => ExportReport()));
            flow.Controls.Add(MakeButton("打开日志文件夹", 124, (s, e) => OpenFolder()));
            flow.Controls.Add(MakeButton("关闭 Vari-Bright", 128, (s, e) => RunFix("-Target VariBright -Apply")));
            flow.Controls.Add(MakeButton("恢复 Vari-Bright", 128, (s, e) => RunFix("-Target VariBright -Restore -Apply")));
            flow.Controls.Add(MakeButton("电源选项", 96, (s, e) => StartSafe("control.exe", "powercfg.cpl")));
            flow.Controls.Add(MakeButton("系统显示设置", 116, (s, e) => StartSafe("explorer.exe", "ms-settings:display")));

            return flow;
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

                grid.Controls.Add(MakeCaption(captions[i]), 0, row);
                var v1 = MakeValue();
                grid.Controls.Add(v1, 1, row);
                _info[captions[i]] = v1;

                if (i + 1 < captions.Length)
                {
                    grid.Controls.Add(MakeCaption(captions[i + 1]), 2, row);
                    var v2 = MakeValue();
                    grid.Controls.Add(v2, 3, row);
                    _info[captions[i + 1]] = v2;
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

            _lblLogPath.ForeColor = Color.Gray;
            _lblLogPath.AutoSize = false;
            _lblLogPath.AutoEllipsis = true;
            _lblLogPath.SetBounds(392, 8, 590, 20);
            _lblLogPath.Text = LogStore.TodayFile;

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
            bottom.Controls.Add(capFilter);
            bottom.Controls.Add(_logFilter);
            bottom.Controls.Add(_lblLogPath);

            _list.Dock = DockStyle.Fill;
            _list.View = View.Details;
            _list.FullRowSelect = true;
            _list.GridLines = true;
            _list.HideSelection = false;
            _list.Columns.Add("时间", 150);
            _list.Columns.Add("操作", 70);
            _list.Columns.Add("详情", 700);

            gb.Controls.Add(_list);
            gb.Controls.Add(bottom);
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
            menu.Items.Add("退出", null, (s, e) => { _exiting = true; Close(); });

            _tray = new NotifyIcon();
            _tray.Icon = Icon;
            _tray.Text = "亮度控制";
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
                        _tray.ShowBalloonTip(1500, "亮度控制", "已最小化到托盘，仍在后台运行。双击图标可再次打开。", ToolTipIcon.Info);
                }
                catch { }
                return;
            }
            LogStore.Write("CONFIG", "exit reason=" + e.CloseReason);
            base.OnFormClosing(e);
        }

        static Icon MakeIcon()
        {
            try
            {
                using (var bmp = new Bitmap(32, 32))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var brush = new SolidBrush(Color.FromArgb(255, 178, 40)))
                        g.FillEllipse(brush, 9, 9, 14, 14);
                    using (var pen = new Pen(Color.FromArgb(226, 142, 16), 2.4f))
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4;
                            g.DrawLine(pen,
                                (float)(16 + Math.Cos(a) * 10), (float)(16 + Math.Sin(a) * 10),
                                (float)(16 + Math.Cos(a) * 14), (float)(16 + Math.Sin(a) * 14));
                        }
                    }
                    IntPtr h = bmp.GetHicon();
                    return Icon.FromHandle(h);
                }
            }
            catch { return SystemIcons.Application; }
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
                _lblLogPath.Text = shown;
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
                sb.AppendLine("计算机：" + Environment.MachineName + "    用户：" + Environment.UserName);
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
}
