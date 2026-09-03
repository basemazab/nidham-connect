// Auto-update from GitHub Releases: download the newer single exe next to the
// installed one, then swap it on the next launch (or now, with consent).
// No installer, no NSIS, no electron-updater — one file replaces one file.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace NidhamConnect
{
    public class UpdateInfo { public string Version; public string Url; public long Size; }

    public static class Updater
    {
        const string LATEST = "https://api.github.com/repos/basemazab/nidham-connect/releases/latest";
        public const string AssetName = "Nidham-Connect.exe";
        static string StagedPath { get { return App.ExePath + ".new"; } }
        static string ULog { get { return Path.Combine(Log.Dir ?? Path.GetTempPath(), "update.log"); } }

        static void ulog(string m)
        {
            try { File.AppendAllText(ULog, DateTime.UtcNow.ToString("o") + " " + m + "\r\n", new UTF8Encoding(false)); } catch { }
        }

        static bool Newer(string a, string b)
        {
            Version va, vb;
            if (!Version.TryParse(a.TrimStart('v', 'V'), out va) || !Version.TryParse(b.TrimStart('v', 'V'), out vb)) return false;
            return va > vb;
        }

        public static async Task<UpdateInfo> Check()
        {
            ulog("checking-for-update");
            string text = await Api.GetText(LATEST);
            Dictionary<string, object> r = Json.Parse(text);
            string tag = Json.Str(r, "tag_name", "");
            UpdateInfo info = null;
            foreach (object o in Json.Arr(r, "assets"))
            {
                Dictionary<string, object> a = o as Dictionary<string, object>;
                if (a == null || Json.Str(a, "name", "") != AssetName) continue;
                info = new UpdateInfo();
                info.Version = tag.TrimStart('v', 'V');
                info.Url = Json.Str(a, "browser_download_url", "");
                info.Size = Json.Int(a, "size", 0);
            }
            if (info == null || !Newer(info.Version, App.Version)) { ulog("update-not-available " + tag); return null; }
            ulog("update-available " + tag);
            return info;
        }

        // Download to <exe>.new.
        public static async Task Download(UpdateInfo info)
        {
            byte[] bytes = await Api.GetBytes(info.Url);
            if (bytes.Length < 20000 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z') throw new Exception("downloaded file is not an exe");
            if (info.Size > 0 && bytes.Length != info.Size) throw new Exception("size mismatch");
            File.WriteAllBytes(StagedPath, bytes);
            ulog("update-downloaded " + info.Version + " (" + bytes.Length + " bytes)");
        }

        public static bool HasStaged() { return File.Exists(StagedPath); }

        // Carry the original flags across the restart, and always come back
        // hidden — the customer did not ask for a window, they asked for an
        // update. Quoted so a --config path with spaces survives.
        static string BuildRelaunchArgs(string[] args)
        {
            List<string> keep = new List<string>();
            for (int i = 0; i < (args ?? new string[0]).Length; i++)
            {
                string a = args[i];
                if (a == "--minimized") continue; // added unconditionally below
                if (a == "--portable" || a == "--no-update") { keep.Add(a); continue; }
                if (a == "--config" && i + 1 < args.Length)
                {
                    // 🔴 This value ends up inside a cmd.exe `start "" "exe" args` line.
                    // A path containing a double quote would close the quoting and turn
                    // the rest into commands cmd would run. A directory path has no
                    // legitimate reason to contain a quote, a caret, an ampersand or a
                    // percent — so refuse rather than escape, and fall back to relaunching
                    // without it (the program then uses its default config dir, which is
                    // the correct behaviour for the installed copy anyway).
                    string cfg = args[++i];
                    const string unsafeChars = "\"^&|<>%\r\n";
                    if (cfg.IndexOfAny(unsafeChars.ToCharArray()) >= 0) continue;
                    keep.Add("--config");
                    keep.Add("\"" + cfg + "\"");
                    continue;
                }
            }
            keep.Add("--minimized");
            return string.Join(" ", keep.ToArray());
        }

        // Replace the running exe with the staged one and relaunch. The batch
        // retries the move until this process has exited and released the file.
        //
        // 🔴 `args` matters: the relaunch used to hard-code `--minimized`, so a
        // copy started with `--portable --config <dir>` came back WITHOUT them,
        // decided it wasn't installed, and copied itself into %LOCALAPPDATA%
        // with a Start-menu shortcut and an autostart entry. An update must
        // never change *where* the program lives or whether it starts with
        // Windows — it only changes the bytes.
        public static void ApplyAndRestart(string[] args)
        {
            if (!HasStaged()) return;
            string bat = Path.Combine(Path.GetTempPath(), "nidham-connect-update.cmd");
            string exe = App.ExePath;
            string relaunchArgs = BuildRelaunchArgs(args);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("set n=0");
            sb.AppendLine(":wait");
            sb.AppendLine("ping -n 2 127.0.0.1 >nul");
            sb.AppendLine("move /y \"" + exe + ".new\" \"" + exe + "\" >nul 2>&1 && goto run");
            sb.AppendLine("set /a n+=1");
            sb.AppendLine("if %n% lss 30 goto wait");
            sb.AppendLine(":run");
            sb.AppendLine("start \"\" \"" + exe + "\" " + relaunchArgs);
            sb.AppendLine("del \"%~f0\"");
            File.WriteAllText(bat, sb.ToString(), Encoding.ASCII);
            ulog("applying update");
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"");
            psi.CreateNoWindow = true; psi.UseShellExecute = false; psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(psi);
            Environment.Exit(0);
        }
    }
}
