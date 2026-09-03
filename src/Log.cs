// Log ring buffer (shown in the UI) + append-only agent.log for support.
// 🔴 Every pairing / sync attempt — success or failure — must leave a line
// here: a silent failure is undiagnosable from the customer's side.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NidhamConnect
{
    public static class Log
    {
        const int MAX = 400;
        static readonly List<string> buf = new List<string>();
        static readonly object gate = new object();
        public static string Dir;
        public static event Action<string> Line;

        public static void Write(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + " " + msg;
            lock (gate)
            {
                buf.Add(line);
                if (buf.Count > MAX) buf.RemoveAt(0);
                try
                {
                    if (!string.IsNullOrEmpty(Dir))
                        File.AppendAllText(Path.Combine(Dir, "agent.log"), line + "\r\n", new UTF8Encoding(false));
                }
                catch { }
            }
            Action<string> h = Line;
            if (h != null) { try { h(line); } catch { } }
        }

        public static string All()
        {
            lock (gate) { return string.Join("\r\n", buf.ToArray()); }
        }
    }
}
