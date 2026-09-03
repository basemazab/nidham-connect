// LAN scan — the «Search Device» button, like the ZK software's own. Sweeps
// every private /24 this PC sits on with a short TCP-4370 connect probe. A TCP
// accept is only a HINT; the caller confirms with a real protocol handshake.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NidhamConnect
{
    public class ScanProgress { public int Done; public int Total; public int Found; }

    public static class Scan
    {
        public const int ZK_PORT = 4370;
        const int PROBE_TIMEOUT_MS = 700;
        const int CONCURRENCY = 48;
        static readonly Regex Private = new Regex(@"^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)");

        public static List<string> LocalSubnets()
        {
            HashSet<string> bases = new HashSet<string>();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (!Private.IsMatch(ip)) continue; // never sweep a public range
                    string[] p = ip.Split('.');
                    bases.Add(p[0] + "." + p[1] + "." + p[2]);
                }
            }
            return new List<string>(bases);
        }

        static Task<bool> Probe(string ip)
        {
            TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                s.BeginConnect(IPAddress.Parse(ip), ZK_PORT, ar =>
                {
                    bool ok = false;
                    try { s.EndConnect(ar); ok = true; } catch { ok = false; }
                    try { s.Close(); } catch { }
                    tcs.TrySetResult(ok);
                }, null);
            }
            catch { tcs.TrySetResult(false); }
            Timer t = null;
            t = new Timer(_ =>
            {
                if (tcs.TrySetResult(false)) { try { s.Close(); } catch { } }
                if (t != null) t.Dispose();
            }, null, PROBE_TIMEOUT_MS, Timeout.Infinite);
            return tcs.Task;
        }

        public static async Task<List<string>> Run(Action<ScanProgress> onProgress)
        {
            List<string> targets = new List<string>();
            foreach (string b in LocalSubnets()) for (int h = 1; h <= 254; h++) targets.Add(b + "." + h);
            List<string> found = new List<string>();
            int idx = -1, done = 0;
            object gate = new object();
            Func<Task> worker = async () =>
            {
                while (true)
                {
                    int i = Interlocked.Increment(ref idx);
                    if (i >= targets.Count) return;
                    bool hit = await Probe(targets[i]);
                    int d = Interlocked.Increment(ref done);
                    if (hit) lock (gate) found.Add(targets[i]);
                    if (onProgress != null && d % 32 == 0)
                    {
                        ScanProgress p = new ScanProgress();
                        p.Done = d; p.Total = targets.Count; lock (gate) p.Found = found.Count;
                        onProgress(p);
                    }
                }
            };
            List<Task> ws = new List<Task>();
            for (int w = 0; w < CONCURRENCY; w++) ws.Add(worker());
            await Task.WhenAll(ws);
            found.Sort((a, b) => IpKey(a).CompareTo(IpKey(b)));
            return found;
        }

        static long IpKey(string ip)
        {
            string[] p = ip.Split('.');
            return (long.Parse(p[0]) << 24) | (long.Parse(p[1]) << 16) | (long.Parse(p[2]) << 8) | long.Parse(p[3]);
        }
    }
}
