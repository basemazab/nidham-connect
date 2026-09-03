// نِظام كونكت 2 — entry point.
// One small .NET Framework exe (nothing to install, no Electron): pair once
// with a code, find the fingerprint device on the LAN, then quietly pull
// punches and push them to نِظام every few minutes. Lives in the tray.
//
// Headless modes (used by the test harness and support):
//   --version
//   --probe <ip[:port]> [--commkey N] [--out file]     handshake + serial
//   --pair <CODE> --config <dir> [--out file]          exchange code → token
//   --sync-once --config <dir> [--out file]            sync all configured devices
//   --uninstall [--purge]                              remove autostart, shortcut, exe
//   --portable                                         run from here, never install
//   --minimized                                        start hidden in the tray

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NidhamConnect
{
    public static class App
    {
        public const string Version = "2.0.3";
        public const string ProductName = "نِظام كونكت";
        public static string ExePath { get { return Application.ExecutablePath; } }
        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nidham Connect"); }
        }
        public static string InstalledExe { get { return Path.Combine(InstallDir, "Nidham-Connect.exe"); } }
        public const string RunKeyName = "NidhamConnect";
    }

    public class Args
    {
        readonly List<string> a;
        public readonly string[] Raw;
        public Args(string[] args) { a = new List<string>(args); Raw = args; }
        public bool Has(string flag) { return a.IndexOf(flag) >= 0; }
        public string Get(string flag)
        {
            int i = a.IndexOf(flag);
            return i >= 0 && i + 1 < a.Count ? a[i + 1] : null;
        }
    }

    static class Program
    {
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        static Mutex single;

        [STAThread]
        static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Args a = new Args(args);
            string cfgDir = a.Get("--config") ?? Config.DefaultDir();
            try { Directory.CreateDirectory(cfgDir); } catch { }
            Log.Dir = cfgDir;

            if (a.Has("--version")) { Emit(a, "{\"version\":\"" + App.Version + "\"}"); return 0; }
            if (a.Has("--probe")) return Cli.Probe(a);
            if (a.Has("--pair")) return Cli.Pair(a, cfgDir);
            if (a.Has("--sync-once")) return Cli.SyncOnce(a, cfgDir);

            // ── GUI ──
            if (a.Has("--uninstall")) return Cli.Uninstall(a);

            // 🔴 The program used to copy itself into %LOCALAPPDATA%, add a Start-menu
            // shortcut and register itself to start with Windows — all BEFORE showing a
            // single window. However convenient, "installs itself and sets itself to
            // start with Windows without asking" is the textbook description of a
            // potentially unwanted program, and code-signing programmes reject it on
            // sight. The window opens first and asks now. Declining is a real option:
            // the program runs perfectly well from wherever it was downloaded.
            bool portable = a.Has("--portable") || a.Has("--config");
            bool offerInstall = !portable && Install.NeedsInstall();

            bool created;
            single = new Mutex(true, "Local\\NidhamConnect.Single", out created);
            if (!created)
            {
                // Another copy is running — ask it to show itself and leave.
                try { EventWaitHandle.OpenExisting("Local\\NidhamConnect.Show").Set(); } catch { }
                return 0;
            }

            if (Updater.HasStaged()) Updater.ApplyAndRestart(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Config cfg = Config.Load(cfgDir);
            Log.Write(App.ProductName + " " + App.Version + " اشتغل");
            Application.Run(new MainForm(cfg, a.Has("--minimized"), a.Has("--selftest"), a, offerInstall));
            GC.KeepAlive(single);
            return 0;
        }

        // Headless output: --out <file> when given (what the tests read), and
        // the parent console when there is one.
        public static void Emit(Args a, string json)
        {
            string outFile = a.Get("--out");
            if (!string.IsNullOrEmpty(outFile))
            {
                try { File.WriteAllText(outFile, json, new UTF8Encoding(false)); } catch { }
            }
            try
            {
                AttachConsole(-1);
                using (Stream s = Console.OpenStandardOutput())
                using (StreamWriter w = new StreamWriter(s, new UTF8Encoding(false)))
                {
                    w.WriteLine(json);
                }
            }
            catch { }
        }
    }

    // First run from Downloads: copy ourselves to %LOCALAPPDATA%\Nidham Connect,
    // add Start-menu shortcut + autostart, and relaunch from there. Portable by
    // nature — the "installer" is a file copy, nothing to uninstall but a folder.
    static class Install
    {
        public static bool NeedsInstall()
        {
            try
            {
                return !string.Equals(Path.GetFullPath(App.ExePath), Path.GetFullPath(App.InstalledExe), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// Called ONLY after the user agreed. Copies to %LOCALAPPDATA%, adds the
        /// Start-menu shortcut, optionally registers autostart, then relaunches there.
        public static bool Run(bool autostart)
        {
            try
            {
                Directory.CreateDirectory(App.InstallDir);
                File.Copy(App.ExePath, App.InstalledExe, true);
                try { Shortcut(App.InstalledExe); } catch (Exception e) { Log.Write("اختصار قايمة ابدأ: " + e.Message); }
                if (autostart) { try { Autostart.Set(true); } catch (Exception e) { Log.Write("التشغيل التلقائي: " + e.Message); } }
                Log.Write("اتنسخ البرنامج لـ " + App.InstalledExe + (autostart ? " + هيشتغل مع ويندوز" : ""));
                ProcessStartInfo psi = new ProcessStartInfo(App.InstalledExe, "");
                psi.UseShellExecute = true;
                Process.Start(psi);
                return true;
            }
            catch (Exception e)
            {
                Log.Write("التثبيت فشل: " + e.Message + " — البرنامج هيكمّل شغل من مكانه");
                return false;
            }
        }

        /// Undo everything Run() did, and report what was actually removed rather than
        /// claiming success. 🔴 A program that can install itself MUST be able to remove
        /// itself — a tray program with no uninstall is, again, what a PUP looks like.
        public static List<string> Uninstall(bool alsoData)
        {
            List<string> removed = new List<string>();
            try { if (Autostart.IsSet()) { Autostart.Set(false); removed.Add("مدخل التشغيل مع ويندوز"); } } catch { }
            try
            {
                string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "نِظام كونكت.lnk");
                if (File.Exists(lnk)) { File.Delete(lnk); removed.Add("اختصار قايمة ابدأ"); }
            }
            catch { }
            if (alsoData)
            {
                try
                {
                    string dir = Log.Dir;
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        foreach (string f in new[] { "config.json", "config.json.tmp", "agent.log", "update.log" })
                        {
                            string full = Path.Combine(dir, f);
                            if (File.Exists(full)) File.Delete(full);
                        }
                        removed.Add("الإعدادات والسجلات");
                    }
                }
                catch (Exception e) { Log.Write("مسح البيانات: " + e.Message); }
            }
            // An installed copy cannot delete itself while running, so a batch waits for
            // this process to exit — the same shape as the updater's swap.
            try
            {
                if (!NeedsInstall())
                {
                    string bat = Path.Combine(Path.GetTempPath(), "nidham-connect-uninstall.cmd");
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("@echo off");
                    sb.AppendLine("set n=0");
                    sb.AppendLine(":wait");
                    sb.AppendLine("ping -n 2 127.0.0.1 >nul");
                    sb.AppendLine("rd /s /q \"" + App.InstallDir + "\" 2>nul");
                    sb.AppendLine("if exist \"" + App.InstalledExe + "\" ( set /a n+=1 & if %n% lss 30 goto wait )");
                    sb.AppendLine("del \"%~f0\"");
                    File.WriteAllText(bat, sb.ToString(), Encoding.ASCII);
                    ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"");
                    psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.WindowStyle = ProcessWindowStyle.Hidden;
                    Process.Start(psi);
                    removed.Add("ملف البرنامج");
                }
                else removed.Add("ملف البرنامج (شغّال من مكانه — امسحه بإيدك)");
            }
            catch (Exception e) { Log.Write("مسح ملف البرنامج: " + e.Message); }
            return removed;
        }

        // Start-menu shortcut via WScript.Shell late binding (no COM interop refs).
        static void Shortcut(string exe)
        {
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string lnk = Path.Combine(programs, "نِظام كونكت.lnk");
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            object shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type st = sc.GetType();
            st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { exe });
            st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(exe) });
            st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { "نِظام كونكت — رفع البصمات لنِظام تلقائيًا" });
            st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
        }
    }

    static class Autostart
    {
        const string KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static void Set(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KEY))
            {
                // The RUNNING exe, not the canonical install path: a portable
                // copy that registers %LOCALAPPDATA%\… points Windows at a file
                // that may not exist.
                if (on) k.SetValue(App.RunKeyName, "\"" + App.ExePath + "\" --minimized");
                else k.DeleteValue(App.RunKeyName, false);
            }
        }
        public static bool IsSet()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KEY))
            {
                return k != null && k.GetValue(App.RunKeyName) != null;
            }
        }
    }

    // Headless commands — same code paths the UI uses.
    static class Cli
    {
        static string Esc(string s)
        {
            if (s == null) return "null";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ") + "\"";
        }

        public static int Uninstall(Args a)
        {
            List<string> removed = Install.Uninstall(a.Has("--purge"));
            Program.Emit(a, "{\"ok\":true,\"removed\":[" + string.Join(",", removed.ConvertAll(Esc).ToArray()) + "]}");
            return 0;
        }

        public static int Probe(Args a)
        {
            string target = a.Get("--probe") ?? "";
            string ip = target; int port = 4370;
            int c = target.LastIndexOf(':');
            if (c > 0) { ip = target.Substring(0, c); int.TryParse(target.Substring(c + 1), out port); }
            int key = 0; int.TryParse(a.Get("--commkey") ?? "0", out key);
            try
            {
                ZkReadResult r = Zk.Read(ip, port, key, 8000, a.Has("--full"), null);
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"ok\":true,\"serial\":" + Esc(r.Serial) + ",\"deviceName\":" + Esc(r.DeviceName) + ",\"transport\":" + Esc(r.Transport));
                sb.Append(",\"userCount\":" + r.UserCount + ",\"logCount\":" + r.LogCount);
                sb.Append(",\"logRecordSize\":" + r.LogRecordSize + ",\"userRecordSize\":" + r.UserRecordSize);
                sb.Append(",\"logBytes\":" + r.LogBytes + ",\"userBytes\":" + r.UserBytes);
                sb.Append(",\"users\":" + r.Users.Count + ",\"punches\":" + r.Punches.Count);
                // A sample of what we actually decoded. On a real device this is the
                // difference between "it read 500 rows" and "it read 500 rows CORRECTLY".
                sb.Append(",\"sampleUsers\":[");
                for (int i = 0; i < Math.Min(3, r.Users.Count); i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"pin\":" + Esc(r.Users[i].Pin) + ",\"name\":" + Esc(r.Users[i].Name) + "}");
                }
                sb.Append("],\"samplePunches\":[");
                for (int i = 0; i < Math.Min(3, r.Punches.Count); i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"pin\":" + Esc(r.Punches[i].Pin) + ",\"at\":" + Esc(r.Punches[i].At.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)) + "}");
                }
                sb.Append("]}");
                Program.Emit(a, sb.ToString());
                return 0;
            }
            catch (ZkException e)
            {
                Program.Emit(a, "{\"ok\":false,\"code\":" + Esc(e.Code) + ",\"error\":" + Esc(e.Message) + ",\"arabic\":" + Esc(Sync.Arabic(e)) + "}");
                return 2;
            }
            catch (Exception e)
            {
                Program.Emit(a, "{\"ok\":false,\"code\":\"OTHER\",\"error\":" + Esc(e.Message) + "}");
                return 3;
            }
        }

        public static int Pair(Args a, string cfgDir)
        {
            Config cfg = Config.Load(cfgDir);
            string srv = a.Get("--server"); if (!string.IsNullOrEmpty(srv)) cfg.Server = srv.TrimEnd('/');
            try
            {
                PairResult p = Api.Pair(cfg.Server, (a.Get("--pair") ?? "").Trim().ToUpperInvariant(), Environment.MachineName).GetAwaiter().GetResult();
                cfg.Token = p.Token; cfg.AgentId = p.AgentId; cfg.CompanyName = p.CompanyName;
                cfg.Save();
                Log.Write("🔗 اتربطنا بشركة «" + p.CompanyName + "» بنجاح");
                Program.Emit(a, "{\"ok\":true,\"companyName\":" + Esc(p.CompanyName) + ",\"agentId\":" + Esc(p.AgentId) + "}");
                return 0;
            }
            catch (Exception e)
            {
                ApiException ae = e as ApiException;
                Log.Write("❌ الاقتران فشل — " + Sync.Arabic(e));
                Program.Emit(a, "{\"ok\":false,\"status\":" + (ae != null ? ae.Status : 0) + ",\"error\":" + Esc(Sync.Arabic(e)) + "}");
                return 2;
            }
        }

        public static int SyncOnce(Args a, string cfgDir)
        {
            Config cfg = Config.Load(cfgDir);
            string srv = a.Get("--server"); if (!string.IsNullOrEmpty(srv)) cfg.Server = srv.TrimEnd('/');
            if (!cfg.Paired) { Program.Emit(a, "{\"ok\":false,\"error\":\"not paired\"}"); return 2; }
            StringBuilder sb = new StringBuilder("{\"ok\":true,\"devices\":[");
            bool first = true; int failures = 0;
            foreach (DeviceConfig dev in cfg.Devices)
            {
                if (!first) sb.Append(","); first = false;
                try
                {
                    SyncOutcome o = Sync.Device(cfg, dev, Log.Write).GetAwaiter().GetResult();
                    dev.LastStamp = o.NewStamp; if (!string.IsNullOrEmpty(o.Serial)) dev.Serial = o.Serial;
                    cfg.Save();
                    sb.Append("{\"key\":" + Esc(dev.Key) + ",\"ok\":true,\"sent\":" + o.Sent + ",\"accepted\":" + o.Accepted + ",\"onDevice\":" + o.OnDevice);
                    sb.Append(",\"users\":" + o.UsersOnDevice + ",\"unmatched\":" + o.Unmatched.Count + ",\"newStamp\":" + Esc(o.NewStamp) + ",\"serial\":" + Esc(o.Serial) + ",\"transport\":" + Esc(o.Transport) + "}");
                    Log.Write("✅ " + Sync.Label(dev) + ": اترفع " + o.Sent + " بصمة (" + o.Accepted + " يوم حضور)");
                }
                catch (Exception e)
                {
                    failures++;
                    ZkException z = e as ZkException;
                    Log.Write("❌ " + Sync.Label(dev) + ": " + Sync.Arabic(e));
                    sb.Append("{\"key\":" + Esc(dev.Key) + ",\"ok\":false,\"code\":" + Esc(z != null ? z.Code : "API") + ",\"error\":" + Esc(Sync.Arabic(e)) + "}");
                }
            }
            sb.Append("]}");
            Program.Emit(a, sb.ToString());
            return failures == 0 ? 0 : 1;
        }
    }
}
