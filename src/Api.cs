// نِظام HTTP API — pair once, then sync. Errors carry the HTTP status and the
// server's Arabic message (which is always more precise than a local guess).

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace NidhamConnect
{
    public class ApiException : Exception
    {
        public readonly int Status;         // 0 = network failure
        public readonly string ServerMessage;
        public ApiException(int status, string serverMessage, string message) : base(message)
        {
            Status = status; ServerMessage = serverMessage;
        }
    }

    public class PairResult { public string Token; public string AgentId; public string CompanyName; }

    public class Unmatched { public string Pin; public string Name; }

    public class SyncResponse
    {
        public int Accepted;
        public List<Unmatched> Unmatched = new List<Unmatched>();
        public bool DeviceActive = true;
    }

    public static class Api
    {
        static readonly HttpClient http = Build();

        static HttpClient Build()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpClient c = new HttpClient();
            c.Timeout = TimeSpan.FromSeconds(90);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("NidhamConnect/" + App.Version);
            return c;
        }

        static async Task<Dictionary<string, object>> Post(string url, string token, Dictionary<string, object> body)
        {
            string json = Json.Serializer().Serialize(body);
            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage res;
            try { res = await http.SendAsync(req); }
            catch (Exception e) { throw new ApiException(0, null, e.InnerException != null ? e.InnerException.Message : e.Message); }
            string text = "";
            try { text = await res.Content.ReadAsStringAsync(); } catch { }
            Dictionary<string, object> parsed = Json.Parse(text) ?? new Dictionary<string, object>();
            if (!res.IsSuccessStatusCode)
            {
                string serverMsg = Json.Str(parsed, "error", null);
                throw new ApiException((int)res.StatusCode, serverMsg, serverMsg ?? ("HTTP " + (int)res.StatusCode));
            }
            return parsed;
        }

        public static async Task<PairResult> Pair(string server, string code, string hostname)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["code"] = code; body["hostname"] = hostname; body["version"] = App.Version;
            Dictionary<string, object> r = await Post(server.TrimEnd('/') + "/api/device-agent/pair", null, body);
            PairResult p = new PairResult();
            p.Token = Json.Str(r, "token", null);
            p.AgentId = Json.Str(r, "agent_id", null);
            p.CompanyName = Json.Str(r, "company_name", "");
            if (string.IsNullOrEmpty(p.Token)) throw new ApiException(200, null, "server returned no token");
            return p;
        }

        public static async Task<SyncResponse> Sync(string server, string token, DeviceConfig dev, string serial,
                                                    List<object> punches, List<object> users)
        {
            Dictionary<string, object> device = new Dictionary<string, object>();
            device["key"] = dev.Key; device["serial"] = serial ?? ""; device["name"] = dev.Name ?? ""; device["ip"] = dev.Ip;
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["version"] = App.Version; body["device"] = device; body["punches"] = punches; body["users"] = users;
            Dictionary<string, object> r = await Post(server.TrimEnd('/') + "/api/device-agent/sync", token, body);
            SyncResponse s = new SyncResponse();
            s.Accepted = Json.Int(r, "accepted", 0);
            s.DeviceActive = Json.Bool(r, "device_active", true);
            foreach (object o in Json.Arr(r, "unmatched"))
            {
                Dictionary<string, object> u = o as Dictionary<string, object>;
                if (u == null) continue;
                s.Unmatched.Add(new Unmatched { Pin = Json.Str(u, "pin", ""), Name = Json.Str(u, "name", "") });
            }
            return s;
        }

        // Device-read failure (or «no devices added») → /api/device-agent/report.
        // Before 2.1.0 a failed read never reached the server: /sync is only called
        // AFTER a successful read, so the devices page showed a paired agent with
        // zero punches and no reason (6 of 6 on production, 2026-09-27).
        public static async Task Report(string server, string token, DeviceConfig dev, string code, string message, int configuredDevices)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["version"] = App.Version; body["code"] = code; body["message"] = message ?? "";
            body["configured_devices"] = configuredDevices;
            if (dev != null)
            {
                Dictionary<string, object> device = new Dictionary<string, object>();
                device["key"] = dev.Key; device["name"] = dev.Name ?? ""; device["ip"] = dev.Ip;
                body["device"] = device;
            }
            await Post(server.TrimEnd('/') + "/api/device-agent/report", token, body);
        }

        public static async Task<string> GetText(string url)
        {
            HttpResponseMessage res = await http.GetAsync(url);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync();
        }

        public static async Task<byte[]> GetBytes(string url)
        {
            HttpResponseMessage res = await http.GetAsync(url);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsByteArrayAsync();
        }
    }
}
