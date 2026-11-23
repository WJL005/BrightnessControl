using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

static class Program
{
    const string WmiNamespace = "root\\WMI";

    static readonly string ExeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    static string LogDir { get { return Path.Combine(ExeDir, "logs"); } }

    static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); }
        catch { }

        bool noPause = Array.Exists(args, a => a.Equals("--no-pause", StringComparison.OrdinalIgnoreCase));
        string[] argv = Array.FindAll(args, a => !a.StartsWith("--"));

        string exeName = Path.GetFileNameWithoutExtension(
            Process.GetCurrentProcess().MainModule.FileName);

        CleanupOldLogs();

        try
        {
            if (argv.Length > 0)
            {
                string cmd = argv[0].ToLowerInvariant();

                if ((cmd == "set" || cmd == "-s") && argv.Length > 1)
                    return DoSet(ParsePercent(argv[1]));

                if (cmd == "show" || cmd == "-g")
                    return DoShow();

                if (cmd == "monitor" || cmd == "-m")
                {
                    int iv = argv.Length > 1 ? ParseInt(argv[1], 2) : 2;
                    return DoMonitor(iv);
                }

                int? argPercent = TryExtractPercent(argv[0]);
                if (argPercent.HasValue) return DoSet(argPercent.Value);

                PrintUsage();
                return 1;
            }

            // 文件名触发：亮度监控.exe / 亮度-100.exe / 亮度.exe
            if (exeName.IndexOf("监控", StringComparison.Ordinal) >= 0 ||
                exeName.IndexOf("monitor", StringComparison.OrdinalIgnoreCase) >= 0)
                return DoMonitor(2);

            int? namePercent = TryExtractPercent(exeName);
            if (namePercent.HasValue) return DoSet(namePercent.Value);

            return DoShow();
        }
        finally
        {
            if (!noPause && IsLaunchedByExplorer())
            {
                Console.WriteLine();
                Console.Write("按回车退出...");
                Console.ReadLine();
            }
        }
    }

    // ── 亮度读写 ──────────────────────────────────────────
    static int ReadBrightness()
    {
        using (var searcher = new ManagementObjectSearcher(
            WmiNamespace, "SELECT CurrentBrightness, Active FROM WmiMonitorBrightness"))
        {
            int? fallback = null;
            foreach (ManagementBaseObject o in searcher.Get())
            {
                object cb = null;
                try { cb = o["CurrentBrightness"]; } catch { }
                if (cb == null) continue;

                int v = Convert.ToInt32(cb);
                object active = null;
                try { active = o["Active"]; } catch { }
                if (active is bool && (bool)active) return v;
                if (!fallback.HasValue) fallback = v;
            }
            if (fallback.HasValue) return fallback.Value;
        }
        throw new InvalidOperationException("读不到内置屏亮度");
    }

    static bool SetBrightness(int percent)
    {
        using (var searcher = new ManagementObjectSearcher(
            WmiNamespace, "SELECT * FROM WmiMonitorBrightnessMethods"))
        {
            foreach (ManagementObject o in searcher.Get())
            {
                o.InvokeMethod("WmiSetBrightness",
                    new object[] { (uint)1, (byte)percent });
                return true;
            }
        }
        return false;
    }

    // ── 子命令 ────────────────────────────────────────────
    static int DoSet(int percent)
    {
        int before;
        try { before = ReadBrightness(); }
        catch { before = -1; }

        try
        {
            if (!SetBrightness(percent))
            {
                Console.WriteLine("未能控制亮度：系统没有暴露内置屏亮度接口。");
                Log("FAIL", string.Format("req={0}; before={1}; error=no-brightness-interface", percent, before));
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("设置亮度失败：" + ex.Message);
            Log("FAIL", string.Format("req={0}; before={1}; error={2}", percent, before, ex.Message));
            return 1;
        }

        Thread.Sleep(400);

        int after;
        try { after = ReadBrightness(); }
        catch
        {
            Console.WriteLine("已请求 " + percent + "%，但回读失败。");
            Log("SET", string.Format("req={0}; before={1}; after=read-failed; result=UNKNOWN", percent, before));
            return 0;
        }

        if (after == percent)
        {
            Console.WriteLine("亮度已设为 " + percent + "%");
            Log("SET", string.Format("req={0}; before={1}; after={2}; result=OK", percent, before, after));
            return 0;
        }

        Console.WriteLine("请求亮度 : " + percent + "%");
        Console.WriteLine("实际亮度 : " + after + "%   (Windows 回读值)");

        if (percent > 0)
        {
            int ratio = (int)Math.Round(after * 100.0 / percent);
            if (ratio < 95)
            {
                Console.WriteLine("提示：实际值约为请求值的 " + ratio + "%，说明另有一层在按该比例压低亮度。");
                Console.WriteLine("      常见来源：电源方案里的「节电模式亮度系数」。");
            }
            Log("SET", string.Format("req={0}; before={1}; after={2}; ratio={3}%; result=MISMATCH",
                percent, before, after, ratio));
        }
        else
        {
            Log("SET", string.Format("req={0}; before={1}; after={2}; result=MISMATCH",
                percent, before, after));
        }
        return 0;
    }

    static int DoShow()
    {
        try
        {
            int cur = ReadBrightness();
            Console.WriteLine("当前亮度：" + cur + "%");
            Log("SHOW", "current=" + cur);
            Console.WriteLine();
            PrintUsage();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("读取失败：" + ex.Message);
            Log("FAIL", "show; error=" + ex.Message);
            return 1;
        }
    }

    static int DoMonitor(int intervalSeconds)
    {
        if (intervalSeconds < 1) intervalSeconds = 1;

        int last;
        try { last = ReadBrightness(); }
        catch (Exception ex)
        {
            Console.WriteLine("读取失败：" + ex.Message);
            return 1;
        }

        Console.WriteLine("开始监听亮度变化，间隔 " + intervalSeconds + " 秒。按 Ctrl+C 结束。");
        Console.WriteLine("日志：" + Path.Combine(LogDir, "brightness-" + DateTime.Now.ToString("yyyyMMdd") + ".log"));
        Log("MON", string.Format("start; interval={0}s; current={1}", intervalSeconds, last));

        while (true)
        {
            Thread.Sleep(intervalSeconds * 1000);
            try
            {
                int cur = ReadBrightness();
                if (cur != last)
                {
                    Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "   " + last + "% -> " + cur + "%");
                    Log("CHANGE", string.Format("from={0}; to={1}", last, cur));
                    last = cur;
                }
            }
            catch { }
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("用法：");
        Console.WriteLine("  亮度-100.exe               双击即设为文件名里的数值");
        Console.WriteLine("  亮度.exe set <0-100>       设置亮度");
        Console.WriteLine("  亮度.exe show              显示当前亮度");
        Console.WriteLine("  亮度监控.exe               监听亮度变化并写日志");
        Console.WriteLine("  亮度.exe monitor [秒]      同上，默认 2 秒");
    }

    // ── 参数解析 ──────────────────────────────────────────
    static int ParsePercent(string s)
    {
        int v;
        if (!int.TryParse(s, out v) || v < 0 || v > 100)
            throw new ArgumentException("亮度必须是 0-100 的整数：" + s);
        return v;
    }

    static int ParseInt(string s, int fallback)
    {
        int v;
        return int.TryParse(s, out v) ? v : fallback;
    }

    static int? TryExtractPercent(string s)
    {
        var m = Regex.Match(s, @"(?:^|\D)(\d{1,3})(?:\D|$)");
        if (!m.Success) return null;
        int v;
        if (!int.TryParse(m.Groups[1].Value, out v)) return null;
        if (v < 0 || v > 100) return null;
        return v;
    }

    // ── 日志 ──────────────────────────────────────────────
    static void Log(string action, string detail)
    {
        try
        {
            if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
            string file = Path.Combine(LogDir, "brightness-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            string line = string.Format("{0} | {1,-5} | {2} | user={3} | host={4}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), action, detail,
                Environment.UserName, Environment.MachineName);
            File.AppendAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }

    static void CleanupOldLogs()
    {
        try
        {
            if (!Directory.Exists(LogDir)) return;
            DateTime cutoff = DateTime.Now.AddDays(-30);
            foreach (string f in Directory.GetFiles(LogDir, "*.log"))
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

    static bool IsLaunchedByExplorer()
    {
        try
        {
            uint pid = (uint)Process.GetCurrentProcess().Id;
            uint ppid = 0;
            using (var s = new ManagementObjectSearcher(
                "SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + pid))
            {
                foreach (ManagementBaseObject o in s.Get())
                {
                    ppid = Convert.ToUInt32(o["ParentProcessId"]);
                    break;
                }
            }
            if (ppid == 0) return false;

            using (var s = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_Process WHERE ProcessId=" + ppid))
            {
                foreach (ManagementBaseObject o in s.Get())
                {
                    string name = Convert.ToString(o["Name"]);
                    return string.Equals(name, "explorer.exe", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch { }
        return false;
    }
}
