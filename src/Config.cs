// Config store — one JSON file in %APPDATA%\Nidham Connect\config.json.
// Same folder and same keys as Nidham Connect 1.x (the Electron build), so a
// machine that already paired keeps its token when it upgrades to this build.
// Written atomically (tmp + replace) so a crash mid-write can't force re-pairing.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace NidhamConnect
{
    public class DeviceConfig
    {
        public string Key = "";
        public string Ip = "";
        public int Port = 4370;
        public int CommKey = 0;
        public string Name = "";
        public int SinceDays = 60;
        public string LastStamp = null; // "YYYY-MM-DD HH:MM:SS" of the last punch confirmed by the server
        public string Serial = "";
    }

    public class Config
    {
        public const string DefaultServer = "https://www.nidhamhr.com";
        public string Server = DefaultServer;
        public string Token = null;
        public string AgentId = null;
        public string CompanyName = "";
        public bool AutoLaunch = true;
        public int IntervalMin = 3;
        public List<DeviceConfig> Devices = new List<DeviceConfig>();

        public string Dir;
        string FilePath { get { return Path.Combine(Dir, "config.json"); } }
        public bool Paired { get { return !string.IsNullOrEmpty(Token); } }

        public static string DefaultDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nidham Connect");
        }

        public static Config Load(string dir)
        {
            Config c = new Config();
            c.Dir = dir;
            Directory.CreateDirectory(dir);
            try
            {
                if (File.Exists(c.FilePath))
                {
                    JavaScriptSerializer js = Json.Serializer();
                    Dictionary<string, object> d = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(c.FilePath));
                    if (d != null) c.Apply(d);
                }
            }
            catch (Exception) { /* corrupt file → defaults; the customer re-pairs */ }
            return c;
        }

        void Apply(Dictionary<string, object> d)
        {
            Server = Json.Str(d, "server", DefaultServer).TrimEnd('/');
            Token = Json.Str(d, "token", null);
            AgentId = Json.Str(d, "agentId", null);
            CompanyName = Json.Str(d, "companyName", "");
            AutoLaunch = Json.Bool(d, "autoLaunch", true);
            IntervalMin = Math.Max(2, Json.Int(d, "intervalMin", 3));
            Devices = new List<DeviceConfig>();
            object devs;
            if (d.TryGetValue("devices", out devs) && devs is ArrayList)
            {
                foreach (object o in (ArrayList)devs)
                {
                    Dictionary<string, object> dd = o as Dictionary<string, object>;
                    if (dd == null) continue;
                    DeviceConfig dc = new DeviceConfig();
                    dc.Key = Json.Str(dd, "key", "");
                    dc.Ip = Json.Str(dd, "ip", "");
                    dc.Port = Json.Int(dd, "port", 4370);
                    dc.CommKey = Json.Int(dd, "commKey", 0);
                    dc.Name = Json.Str(dd, "name", "");
                    dc.SinceDays = Json.Int(dd, "sinceDays", 60);
                    dc.LastStamp = Json.Str(dd, "lastStamp", null);
                    dc.Serial = Json.Str(dd, "serial", "");
                    if (dc.Key.Length == 0 || dc.Ip.Length == 0) continue;
                    Devices.Add(dc);
                }
            }
        }

        public void Save()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["server"] = Server;
            d["token"] = Token;
            d["agentId"] = AgentId;
            d["companyName"] = CompanyName;
            d["autoLaunch"] = AutoLaunch;
            d["intervalMin"] = IntervalMin;
            List<object> devs = new List<object>();
            foreach (DeviceConfig dc in Devices)
            {
                Dictionary<string, object> dd = new Dictionary<string, object>();
                dd["key"] = dc.Key; dd["ip"] = dc.Ip; dd["port"] = dc.Port; dd["commKey"] = dc.CommKey;
                dd["name"] = dc.Name; dd["sinceDays"] = dc.SinceDays; dd["lastStamp"] = dc.LastStamp; dd["serial"] = dc.Serial;
                devs.Add(dd);
            }
            d["devices"] = devs;
            string json = Json.Serializer().Serialize(d);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }

        public DeviceConfig FindDevice(string key)
        {
            foreach (DeviceConfig d in Devices) if (d.Key == key) return d;
            return null;
        }
    }

    // Small JSON helpers on top of the in-box JavaScriptSerializer (no NuGet).
    public static class Json
    {
        public static JavaScriptSerializer Serializer()
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = 64 * 1024 * 1024;
            return js;
        }
        public static Dictionary<string, object> Parse(string text)
        {
            try { return Serializer().Deserialize<Dictionary<string, object>>(text); }
            catch { return null; }
        }
        public static string Str(Dictionary<string, object> d, string k, string def)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return def;
            return Convert.ToString(v);
        }
        public static int Int(Dictionary<string, object> d, string k, int def)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return def;
            try { return Convert.ToInt32(v); } catch { return def; }
        }
        public static bool Bool(Dictionary<string, object> d, string k, bool def)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v).ToLowerInvariant();
            return s == "true" || s == "1";
        }
        public static ArrayList Arr(Dictionary<string, object> d, string k)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v)) return new ArrayList();
            return v as ArrayList ?? new ArrayList();
        }
    }
}
