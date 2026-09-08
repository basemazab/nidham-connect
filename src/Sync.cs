// Per-device sync: read punches from the device, window them against the
// stored cursor, POST them in chunks. The cursor only advances after the
// server confirmed EVERY chunk — a mid-run failure re-sends next time and the
// server upsert makes the resend harmless. A trailing 48h window is always
// re-sent (idempotent) so a check-out that landed after the last run heals.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace NidhamConnect
{
    public class SyncOutcome
    {
        public int Sent;
        public int Accepted;
        public int OnDevice;
        public int UsersOnDevice;
        public List<Unmatched> Unmatched = new List<Unmatched>();
        public bool DeviceActive = true;
        public string NewStamp;
        public string Serial = "";
        public string Transport = "";
    }

    public static class Sync
    {
        const int CHUNK = 4000;               // under the server's MAX_PUNCHES=5000
        const double OVERLAP_HOURS = 48;

        public static string Stamp(DateTime d) { return d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }

        public static DateTime? ParseStamp(string s)
        {
            DateTime d;
            if (!string.IsNullOrEmpty(s) && DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
            return null;
        }

        public static async Task<SyncOutcome> Device(Config cfg, DeviceConfig dev, Action<string> log)
        {
            log("📡 بنكلم الجهاز " + Label(dev) + "...");
            ZkReadResult read = await Task.Run(() => Zk.Read(dev.Ip, dev.Port, dev.CommKey, 20000, true, null));
            log("✓ الجهاز رد (" + read.Transport + "): " + read.Punches.Count + " بصمة في الذاكرة، " + read.Users.Count + " موظف مسجّل");
            // 🔴 سطر تشخيصي — أول جهاز حقيقي هيتجرّب عند عميل، وإحنا هناخد
            // نسخة من اللوج بس. لو العدّاد بيقول ٥٠٠ والمفكوك ٠، السطر ده
            // بيقول السبب فورًا (طول السجل الغلط ولا الجدول رجع فاضي).
            log("   تفاصيل: عدّاد الجهاز " + read.LogCount + " بصمة/" + read.UserCount + " موظف · طول السجل "
                + read.LogRecordSize + "/" + read.UserRecordSize + " بايت · حجم الجدول " + read.LogBytes + "/" + read.UserBytes + " بايت"
                + (string.IsNullOrEmpty(read.DeviceName) ? "" : " · " + read.DeviceName)
                + (string.IsNullOrEmpty(read.Serial) ? "" : " #" + read.Serial));

            // 🔴 Device identity pinning — the ZK protocol has no auth by default
            // (comm key 0), so anything answering on this IP is trusted blindly
            // unless we check. Attendance feeds payroll, so a device that suddenly
            // reports a different serial than the one pinned at add-time is
            // treated as an impostor and refused BEFORE any data reaches the server.
            if (!string.IsNullOrEmpty(dev.Serial) && !string.IsNullOrEmpty(read.Serial) && read.Serial != dev.Serial)
            {
                throw new ZkException("SERIAL_MISMATCH",
                    "الجهاز على " + dev.Ip + " رد بسريال مختلف عن اللي اتسجّل (كان " + dev.Serial + "، دلوقتي " + read.Serial + ") — البيانات اتوقفت. لو الجهاز اتغيّر فعلًا، احذفه وضيفه تاني.");
            }

            DateTime since;
            DateTime? last = ParseStamp(dev.LastStamp);
            if (last != null) since = last.Value.AddHours(-OVERLAP_HOURS);
            else since = dev.SinceDays > 0 ? DateTime.Now.AddDays(-dev.SinceDays) : DateTime.MinValue;

            List<ZkPunch> fresh = new List<ZkPunch>();
            foreach (ZkPunch p in read.Punches) if (p.At >= since) fresh.Add(p);
            fresh.Sort((a, b) => a.At.CompareTo(b.At));

            List<object> users = new List<object>();
            foreach (ZkUser u in read.Users)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["pin"] = u.Pin; d["name"] = u.Name;
                users.Add(d);
            }

            SyncOutcome o = new SyncOutcome();
            o.Sent = fresh.Count; o.OnDevice = read.Punches.Count; o.UsersOnDevice = read.Users.Count;
            o.Serial = read.Serial ?? ""; o.Transport = read.Transport;

            if (fresh.Count == 0)
            {
                // Heartbeat — keeps «شغّال دلوقتي» honest and refreshes the unmatched report.
                SyncResponse r = await Api.Sync(cfg.Server, cfg.Token, dev, read.Serial, new List<object>(), users);
                o.Unmatched = r.Unmatched; o.DeviceActive = r.DeviceActive;
            }
            else
            {
                for (int i = 0; i < fresh.Count; i += CHUNK)
                {
                    List<object> chunk = new List<object>();
                    for (int j = i; j < Math.Min(i + CHUNK, fresh.Count); j++)
                    {
                        Dictionary<string, object> d = new Dictionary<string, object>();
                        d["pin"] = fresh[j].Pin;
                        d["date"] = fresh[j].At.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        d["time"] = fresh[j].At.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                        chunk.Add(d);
                    }
                    // user list rides with the first chunk only
                    SyncResponse r = await Api.Sync(cfg.Server, cfg.Token, dev, read.Serial, chunk, i == 0 ? users : new List<object>());
                    o.Accepted += r.Accepted;
                    o.Unmatched = r.Unmatched; o.DeviceActive = r.DeviceActive;
                    if (fresh.Count > CHUNK) log("⬆ اترفع " + Math.Min(i + CHUNK, fresh.Count) + " من " + fresh.Count + "...");
                }
            }
            o.NewStamp = fresh.Count > 0 ? Stamp(fresh[fresh.Count - 1].At) : dev.LastStamp;
            return o;
        }

        public static string Label(DeviceConfig d) { return string.IsNullOrEmpty(d.Name) ? d.Ip : d.Name; }

        // Arabic, actionable — the server's own message wins when it exists.
        public static string Arabic(Exception err)
        {
            ZkException z = err as ZkException;
            if (z != null)
            {
                switch (z.Code)
                {
                    case "COMM_KEY": return "الجهاز محمي بـ Comm Key — اكتب الرقم في إعدادات الجهاز هنا (تلاقيه في قايمة الجهاز: Comm → Security/Comm Key)، أو خليه صفر من الجهاز";
                    case "REFUSED": return "الجهاز رفض الاتصال — اتأكد إن العنوان صح وإن الجهاز شغّال";
                    case "TIMEOUT":
                    case "UNREACHABLE": return "مش لاقيين الجهاز — اتأكد إن الكمبيوتر ده على نفس شبكة الجهاز (نفس الراوتر) وإن كابل الشبكة في الجهاز متوصّل";
                    case "SERIAL_MISMATCH": return z.Message;
                    default: return "الجهاز رد بشكل غير متوقع (" + z.Message + ") — جرّب تاني، ولو استمر ابعتلنا السجل";
                }
            }
            ApiException a = err as ApiException;
            if (a != null)
            {
                if (!string.IsNullOrEmpty(a.ServerMessage)) return a.ServerMessage;
                if (a.Status == 0) return "مفيش إنترنت دلوقتي — البصمات محفوظة في الجهاز وهتترفع أول ما النت يرجع (" + a.Message + ")";
                if (a.Status == 401) return "الربط اتفصل من النظام — اعمل كود ربط جديد من صفحة الأجهزة";
                if (a.Status == 404) return "السيرفر مش لاقي خدمة الربط — دي مشكلة عندنا مش عندك. كلّم الدعم وقوله «كود 404 في الربط»";
                if (a.Status == 409) return "الجهاز ده مسجّل لشركة تانية في نِظام — كلمنا لو ده غلط";
                if (a.Status == 429) return "محاولات كتير ورا بعض — استنى ربع ساعة وجرّب تاني";
                if (a.Status >= 500) return "السيرفر واقع دلوقتي — استنى شوية وجرّب تاني";
                return "HTTP " + a.Status;
            }
            return string.IsNullOrEmpty(err.Message) ? "حصل خطأ غير متوقع" : err.Message;
        }
    }
}
