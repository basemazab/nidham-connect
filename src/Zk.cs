// ============================================================================
// Zk.cs — ZK-family fingerprint device client (TCP 4370, UDP fallback).
// ============================================================================
// The same wire dialect ZKTime / Attendance Management speak, which is why it
// works on devices that have no ADMS/Cloud menu at all. Written from the
// byte-exact simulator in test/zk-sim.js and the public protocol spec
// (adrobinoga/zk-protocol) — no GPL code.
//
//   TCP frame  = 50 50 82 7d | u32LE payloadLen | payload
//   payload    = cmd u16 | chksum u16 | session u16 | reply u16 | body
//   UDP        = payload only (no prefix)
//
// Bulk tables (users, attendance) arrive either inline in one CMD_DATA reply
// (small devices / the simulator) or chunked: DATA_WRRQ → PREPARE_DATA(size)
// → for each chunk DATA_RDY(start,len) → [PREPARE_DATA] DATA… ACK_OK.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NidhamConnect
{
    public class ZkException : Exception
    {
        public readonly string Code; // COMM_KEY | TIMEOUT | REFUSED | UNREACHABLE | PROTOCOL
        public ZkException(string code, string message) : base(message) { Code = code; }
    }

    public class ZkUser { public string Pin = ""; public string Name = ""; }
    public class ZkPunch { public string Pin = ""; public DateTime At; }

    public class ZkReadResult
    {
        public string Serial = "";
        public string DeviceName = "";
        public string Transport = "";
        public int UserCount;
        public int LogCount;
        // Diagnostics — the first contact with a REAL device happens at a
        // customer's site, and we get one log paste to understand it. These
        // say which dialect and which record layout the device actually used.
        public int UserRecordSize;
        public int LogRecordSize;
        public int UserBytes;
        public int LogBytes;
        public List<ZkUser> Users = new List<ZkUser>();
        public List<ZkPunch> Punches = new List<ZkPunch>();
    }

    static class ZkCmd
    {
        public const ushort CONNECT = 1000, EXIT = 1001, AUTH = 1102;
        public const ushort OPTIONS_RRQ = 11, GET_FREE_SIZES = 50;
        public const ushort PREPARE_DATA = 1500, DATA = 1501, FREE_DATA = 1502, DATA_WRRQ = 1503, DATA_RDY = 1504;
        public const ushort REG_EVENT = 500;
        public const ushort ACK_OK = 2000, ACK_ERROR = 2001, ACK_DATA = 2002, ACK_UNAUTH = 2005;
        public static readonly byte[] REQ_USERS = { 0x01, 0x09, 0x00, 0x05, 0, 0, 0, 0, 0, 0, 0 };
        public static readonly byte[] REQ_LOGS = { 0x01, 0x0d, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
    }

    interface IZkTransport : IDisposable
    {
        string Name { get; }
        void Send(byte[] payload);
        byte[] Receive(int timeoutMs); // one payload (header+body); realtime events skipped
    }

    // ── TCP ──
    class TcpTransport : IZkTransport
    {
        readonly TcpClient client;
        readonly NetworkStream stream;
        public string Name { get { return "tcp"; } }

        public TcpTransport(string ip, int port, int timeoutMs)
        {
            client = new TcpClient();
            IAsyncResult ar = client.BeginConnect(ip, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                client.Close();
                throw new ZkException("TIMEOUT", "connect timeout");
            }
            client.EndConnect(ar); // SocketException when refused / unreachable
            client.NoDelay = true;
            stream = client.GetStream();
        }

        public void Send(byte[] payload)
        {
            byte[] frame = new byte[8 + payload.Length];
            frame[0] = 0x50; frame[1] = 0x50; frame[2] = 0x82; frame[3] = 0x7d;
            Zk.W32(frame, 4, (uint)payload.Length);
            Buffer.BlockCopy(payload, 0, frame, 8, payload.Length);
            stream.Write(frame, 0, frame.Length);
            stream.Flush();
        }

        byte[] ReadExact(int n, DateTime deadline)
        {
            byte[] buf = new byte[n];
            int got = 0;
            while (got < n)
            {
                int remain = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remain <= 0) throw new ZkException("TIMEOUT", "read timeout");
                stream.ReadTimeout = remain;
                int r;
                try { r = stream.Read(buf, got, n - got); }
                catch (IOException e) { throw new ZkException("TIMEOUT", "read timeout: " + e.Message); }
                if (r <= 0) throw new ZkException("PROTOCOL", "socket closed by device");
                got += r;
            }
            return buf;
        }

        public byte[] Receive(int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                byte[] head = ReadExact(8, deadline);
                if (!(head[0] == 0x50 && head[1] == 0x50 && head[2] == 0x82 && head[3] == 0x7d))
                    throw new ZkException("PROTOCOL", "bad frame prefix");
                int len = (int)Zk.U32(head, 4);
                if (len < 8 || len > 4 * 1024 * 1024) throw new ZkException("PROTOCOL", "bad frame length " + len);
                byte[] payload = ReadExact(len, deadline);
                if (Zk.U16(payload, 0) == ZkCmd.REG_EVENT) continue; // realtime punch event — not our reply
                return payload;
            }
        }

        public void Dispose()
        {
            try { stream.Close(); } catch { }
            try { client.Close(); } catch { }
        }
    }

    // ── UDP (very old firmware) ──
    class UdpTransport : IZkTransport
    {
        readonly UdpClient udp;
        public string Name { get { return "udp"; } }

        public UdpTransport(string ip, int port)
        {
            udp = new UdpClient(0);
            udp.Connect(IPAddress.Parse(ip), port);
        }

        public void Send(byte[] payload) { udp.Send(payload, payload.Length); }

        public byte[] Receive(int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                int remain = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remain <= 0) throw new ZkException("TIMEOUT", "udp timeout");
                udp.Client.ReceiveTimeout = remain;
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] d;
                try { d = udp.Receive(ref ep); }
                catch (SocketException e)
                {
                    if (e.SocketErrorCode == SocketError.TimedOut) throw new ZkException("TIMEOUT", "udp timeout");
                    throw new ZkException("UNREACHABLE", e.Message);
                }
                if (d.Length < 8) continue;
                if (Zk.U16(d, 0) == ZkCmd.REG_EVENT) continue;
                return d;
            }
        }

        public void Dispose() { try { udp.Close(); } catch { } }
    }

    // ── session ──
    public class ZkSession : IDisposable
    {
        readonly IZkTransport t;
        readonly int timeoutMs;
        readonly int commKey;
        ushort session;
        ushort replyId;
        bool authed;

        public string Transport { get { return t.Name; } }
        public bool IsUdp { get { return t.Name == "udp"; } }

        ZkSession(IZkTransport transport, int commKey, int timeoutMs)
        {
            t = transport; this.commKey = commKey; this.timeoutMs = timeoutMs;
        }

        public static ZkSession Open(string ip, int port, int commKey, int timeoutMs)
        {
            IZkTransport tr;
            try
            {
                tr = new TcpTransport(ip, port, timeoutMs);
            }
            catch (SocketException se)
            {
                if (se.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    // Old firmware: no TCP listener at all — try the UDP dialect.
                    tr = new UdpTransport(ip, port);
                }
                else if (se.SocketErrorCode == SocketError.TimedOut || se.SocketErrorCode == SocketError.HostUnreachable
                         || se.SocketErrorCode == SocketError.NetworkUnreachable)
                {
                    throw new ZkException("UNREACHABLE", se.Message);
                }
                else throw new ZkException("REFUSED", se.Message);
            }
            ZkSession s = new ZkSession(tr, commKey, timeoutMs);
            try { s.Connect(); }
            catch (ZkException e)
            {
                s.Dispose();
                // UDP got no answer at all → nothing is listening there.
                if (tr is UdpTransport && e.Code == "TIMEOUT") throw new ZkException("REFUSED", "no device answered on tcp or udp");
                // Some firmware accepts TCP 4370 and never answers on it: the
                // protocol is only spoken over UDP. The first real device
                // (2026-10-06) did exactly this, so a silent TCP connect gets one
                // UDP try before giving up.
                if (tr is TcpTransport && e.Code == "TIMEOUT") return OpenUdp(ip, port, commKey, timeoutMs, e);
                throw;
            }
            return s;
        }

        static ZkSession OpenUdp(string ip, int port, int commKey, int timeoutMs, ZkException tcpSilence)
        {
            ZkSession u = new ZkSession(new UdpTransport(ip, port), commKey, timeoutMs);
            try { u.Connect(); return u; }
            catch (ZkException e)
            {
                u.Dispose();
                // Silent on UDP too: report the original TCP silence (the familiar
                // «مش لاقيين الجهاز»), not a UDP detail.
                if (e.Code == "TIMEOUT") throw tcpSilence;
                throw;
            }
        }

        static byte[] Packet(ushort cmd, ushort session, ushort reply, byte[] body)
        {
            byte[] p = new byte[8 + body.Length];
            Zk.W16(p, 0, cmd); Zk.W16(p, 2, 0); Zk.W16(p, 4, session); Zk.W16(p, 6, reply);
            Buffer.BlockCopy(body, 0, p, 8, body.Length);
            Zk.W16(p, 2, Checksum(p));
            return p;
        }

        static ushort Checksum(byte[] b)
        {
            int sum = 0;
            for (int i = 0; i < b.Length; i += 2)
            {
                if (i == b.Length - 1) sum += b[i];
                else sum += Zk.U16(b, i);
                sum %= 65535;
            }
            return (ushort)(65535 - sum - 1);
        }

        byte[] Exchange(ushort cmd, byte[] body, int timeout)
        {
            if (cmd == ZkCmd.CONNECT) { session = 0; replyId = 0; }
            else replyId = (ushort)((replyId + 1) % 65535);
            t.Send(Packet(cmd, session, replyId, body));
            byte[] r = null;
            for (int i = 0; i < 4; i++)
            {
                r = t.Receive(timeout);
                // A stale bare ACK from an earlier chunk read can sit in the stream;
                // devices echo our reply id, so skip bare frames that don't match.
                if (cmd == ZkCmd.CONNECT || Zk.U16(r, 6) == replyId || r.Length > 8) break;
            }
            return r;
        }

        void Connect()
        {
            byte[] r = Exchange(ZkCmd.CONNECT, new byte[0], Math.Min(timeoutMs, 5000));
            ushort rc = Zk.U16(r, 0);
            session = Zk.U16(r, 4);
            if (rc == ZkCmd.ACK_UNAUTH) Auth();
            else if (rc != ZkCmd.ACK_OK) throw new ZkException("PROTOCOL", "unexpected connect reply " + rc);
        }

        void Auth()
        {
            // The handshake goes out with the configured key even when it is 0:
            // some firmware answers UNAUTH to every CONNECT and accepts the
            // handshake with 0, which is what the official ZK software sends when
            // its password field is 0 (the first real device, 2026-10-06). It is
            // one attempt with the configured key; a rejection is a clear
            // COMM_KEY, never a retry with other keys.
            byte[] r = Exchange(ZkCmd.AUTH, MakeCommKey(Math.Max(0, commKey), session, 50), timeoutMs);
            if (Zk.U16(r, 0) != ZkCmd.ACK_OK)
                throw new ZkException("COMM_KEY", commKey <= 0 ? "device requires a comm key" : "comm key rejected by device");
            authed = true;
        }

        // Command with automatic one-shot auth when the device answers UNAUTH.
        byte[] Cmd(ushort cmd, byte[] body)
        {
            byte[] r = Exchange(cmd, body, timeoutMs);
            if (Zk.U16(r, 0) == ZkCmd.ACK_UNAUTH)
            {
                if (authed) throw new ZkException("COMM_KEY", "device rejected the session");
                Auth();
                r = Exchange(cmd, body, timeoutMs);
                if (Zk.U16(r, 0) == ZkCmd.ACK_UNAUTH) throw new ZkException("COMM_KEY", "device rejected the session");
            }
            return r;
        }

        // Comm-key handshake per the public zk-protocol spec:
        // bit-reverse the key, add the session id, XOR with "ZKSO", swap the
        // 16-bit halves, then XOR three of the four bytes with the tick byte.
        public static byte[] MakeCommKey(int key, ushort sessionId, int ticks)
        {
            uint k = 0;
            for (int i = 0; i < 32; i++) k = (k << 1) | (((uint)key >> i) & 1u);
            k = unchecked(k + sessionId);
            byte b0 = (byte)(k & 0xff), b1 = (byte)((k >> 8) & 0xff), b2 = (byte)((k >> 16) & 0xff), b3 = (byte)((k >> 24) & 0xff);
            b0 ^= (byte)'Z'; b1 ^= (byte)'K'; b2 ^= (byte)'S'; b3 ^= (byte)'O';
            byte B = (byte)(ticks & 0xff);
            return new byte[] { (byte)(b2 ^ B), (byte)(b3 ^ B), B, (byte)(b1 ^ B) };
        }

        public string GetOption(string name)
        {
            byte[] r = Cmd(ZkCmd.OPTIONS_RRQ, Encoding.ASCII.GetBytes(name + "\0"));
            if (Zk.U16(r, 0) != ZkCmd.ACK_OK || r.Length <= 8) return "";
            string s = Encoding.ASCII.GetString(r, 8, r.Length - 8);
            int nul = s.IndexOf('\0'); if (nul >= 0) s = s.Substring(0, nul);
            int eq = s.IndexOf('=');
            return (eq >= 0 ? s.Substring(eq + 1) : s).Trim();
        }

        // [users, logs] — counts the device reports, used to pick record sizes.
        public int[] GetSizes()
        {
            byte[] r = Cmd(ZkCmd.GET_FREE_SIZES, new byte[0]);
            if (Zk.U16(r, 0) != ZkCmd.ACK_OK || r.Length < 8 + 36) return new int[] { 0, 0 };
            return new int[] { (int)Zk.U32(r, 8 + 16), (int)Zk.U32(r, 8 + 32) };
        }

        public byte[] ReadTable(byte[] req, Action<int, int> progress)
        {
            try { Cmd(ZkCmd.FREE_DATA, new byte[0]); } catch (ZkException e) { if (e.Code == "COMM_KEY") throw; }
            byte[] r = Cmd(ZkCmd.DATA_WRRQ, req);
            ushort rc = Zk.U16(r, 0);
            byte[] data;
            if (rc == ZkCmd.DATA)
            {
                data = Zk.Slice(r, 8);
                DrainQuiet();
            }
            else if (rc == ZkCmd.ACK_OK || rc == ZkCmd.PREPARE_DATA)
            {
                if (r.Length < 13) { data = new byte[0]; }
                else
                {
                    int size = (int)Zk.U32(r, 9); // body[1..5]
                    data = ReadChunks(size, progress);
                }
            }
            else throw new ZkException("PROTOCOL", "unexpected table reply " + rc);
            try { Exchange(ZkCmd.FREE_DATA, new byte[0], 3000); } catch { }
            return data;
        }

        // A trailing bare ACK can follow a table read; eat it (short timeout) so
        // the next command's reply isn't misattributed. Never fatal.
        void DrainQuiet()
        {
            try { t.Receive(300); }
            catch (ZkException) { }
        }

        // One chunk at a time (ask → drain → ask). Firing every DATA_RDY up
        // front works on a LAN but stalls over a slow link once the device's
        // send buffer fills, which truncates the download.
        byte[] ReadChunks(int size, Action<int, int> progress)
        {
            MemoryStream ms = new MemoryStream();
            const int MAX_CHUNK = 65472;
            int start = 0;
            while (start < size)
            {
                int chunk = Math.Min(MAX_CHUNK, size - start);
                byte[] body = new byte[8];
                Zk.W32(body, 0, (uint)start); Zk.W32(body, 4, (uint)chunk);
                replyId = (ushort)((replyId + 1) % 65535);
                t.Send(Packet(ZkCmd.DATA_RDY, session, replyId, body));
                int got = 0;
                while (got < chunk)
                {
                    byte[] f = t.Receive(timeoutMs);
                    ushort c = Zk.U16(f, 0);
                    if (c == ZkCmd.DATA)
                    {
                        int take = Math.Min(f.Length - 8, chunk - got);
                        ms.Write(f, 8, take);
                        got += take;
                        continue;
                    }
                    // PREPARE_DATA announces the chunk; a bare ACK_OK here is a
                    // leftover from the previous chunk. Neither carries data.
                    if (c == ZkCmd.PREPARE_DATA || c == ZkCmd.ACK_OK) continue;
                    if (c == ZkCmd.ACK_UNAUTH) throw new ZkException("COMM_KEY", "device rejected the read");
                    throw new ZkException("PROTOCOL", "unexpected chunk reply " + c);
                }
                DrainQuiet(); // the device's end-of-chunk ACK, if it sends one
                start += chunk;
                if (progress != null) progress(Math.Min(start, size), size);
            }
            return ms.ToArray();
        }

        public void Dispose()
        {
            try { Exchange(ZkCmd.EXIT, new byte[0], 1500); } catch { }
            t.Dispose();
        }
    }

    public static class Zk
    {
        public static ushort U16(byte[] b, int o) { return (ushort)(b[o] | (b[o + 1] << 8)); }
        public static uint U32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }
        public static void W16(byte[] b, int o, ushort v) { b[o] = (byte)(v & 0xff); b[o + 1] = (byte)(v >> 8); }
        public static void W32(byte[] b, int o, uint v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
        public static byte[] Slice(byte[] b, int from)
        {
            if (from >= b.Length) return new byte[0];
            byte[] r = new byte[b.Length - from];
            Buffer.BlockCopy(b, from, r, 0, r.Length);
            return r;
        }
        static string CStr(byte[] b, int o, int len)
        {
            if (o >= b.Length) return "";
            int n = Math.Min(len, b.Length - o);
            int end = o;
            while (end < o + n && b[end] != 0) end++;
            return Encoding.ASCII.GetString(b, o, end - o).Trim();
        }

        // ZK packed timestamp → local DateTime (null when the device clock is garbage).
        public static DateTime? DecodeTime(uint t)
        {
            uint sec = t % 60; t /= 60;
            uint min = t % 60; t /= 60;
            uint hour = t % 24; t /= 24;
            uint day = t % 31 + 1; t /= 31;
            uint month = t % 12 + 1; t /= 12;
            uint year = t + 2000;
            if (year < 2000 || year > 2099) return null;
            try { return new DateTime((int)year, (int)month, (int)day, (int)hour, (int)min, (int)sec); }
            catch { return null; }
        }

        static int RecordSize(byte[] table, int count, int[] allowed, int fallback)
        {
            if (table.Length < 4) return fallback;
            int total = (int)U32(table, 0);
            if (count > 0 && total > 0)
            {
                int rs = total / count;
                foreach (int a in allowed) if (rs == a) return a;
            }
            // Count unknown: pick the first allowed size that tiles the data exactly.
            int body = table.Length - 4;
            foreach (int a in allowed) if (body > 0 && body % a == 0) return a;
            return fallback;
        }

        public static List<ZkUser> ParseUsers(byte[] table, int count, bool udp) { int rs; return ParseUsers(table, count, udp, out rs); }

        public static List<ZkUser> ParseUsers(byte[] table, int count, bool udp, out int recordSize)
        {
            List<ZkUser> users = new List<ZkUser>();
            recordSize = 0;
            if (table == null || table.Length <= 4) return users;
            int rs = RecordSize(table, count, udp ? new[] { 28, 72 } : new[] { 72, 28 }, udp ? 28 : 72);
            recordSize = rs;
            for (int o = 4; o + rs <= table.Length; o += rs)
            {
                ZkUser u = new ZkUser();
                if (rs == 72)
                {
                    u.Name = CStr(table, o + 11, 24); // name runs to the cardno at 35
                    u.Pin = CStr(table, o + 48, 9);
                }
                else
                {
                    u.Name = CStr(table, o + 8, 8);
                    u.Pin = U32(table, o + 24).ToString();
                }
                if (u.Pin.Length == 0) continue;
                users.Add(u);
            }
            return users;
        }

        public static List<ZkPunch> ParseLogs(byte[] table, int count, bool udp) { int rs; return ParseLogs(table, count, udp, out rs); }

        public static List<ZkPunch> ParseLogs(byte[] table, int count, bool udp, out int recordSize)
        {
            List<ZkPunch> punches = new List<ZkPunch>();
            recordSize = 0;
            if (table == null || table.Length <= 4) return punches;
            // 🔴 Only the two documented layouts. An earlier version also guessed at
            // an 8-byte record; a guess that silently yields wrong ATTENDANCE is worse
            // than failing loudly, and 8 divides 16 so the guess could even win over
            // the real layout. If a device ever needs another size, the diagnostic log
            // line reports the record size and table length, so we will know.
            int rs = RecordSize(table, count, udp ? new[] { 16, 40 } : new[] { 40, 16 }, udp ? 16 : 40);
            recordSize = rs;
            for (int o = 4; o + rs <= table.Length; o += rs)
            {
                string pin; uint tt;
                // Field widths are the protocol's, not a guess: in the 40-byte record
                // the id is char[9] at offset 2 (reading further swallows the next
                // field when the id fills all 9 bytes with no NUL), and in the 16-byte
                // record the id is a u16 — NOT a u32. Reading it as 32 bits happened to
                // pass every test because the simulator left the next two bytes zero.
                // (A consequence worth knowing: on a device that speaks the 16-byte
                // format, an employee id above 65535 cannot exist at all.)
                if (rs == 40) { pin = CStr(table, o + 2, 9); tt = U32(table, o + 27); }
                else { pin = U16(table, o).ToString(); tt = U32(table, o + 4); }
                if (pin.Length == 0) continue;
                DateTime? at = DecodeTime(tt);
                if (at == null) continue; // never invent a time
                punches.Add(new ZkPunch { Pin = pin, At = at.Value });
            }
            return punches;
        }

        // One call reads everything a sync needs.
        public static ZkReadResult Read(string ip, int port, int commKey, int timeoutMs, bool withLogs, Action<int, int> progress)
        {
            ZkReadResult res = new ZkReadResult();
            using (ZkSession s = ZkSession.Open(ip, port, commKey, timeoutMs))
            {
                res.Transport = s.Transport;
                res.Serial = s.GetOption("~SerialNumber").Replace(" ", "");
                try { res.DeviceName = s.GetOption("~DeviceName"); } catch (ZkException) { res.DeviceName = ""; }
                int[] sizes;
                try { sizes = s.GetSizes(); } catch (ZkException e) { if (e.Code == "COMM_KEY") throw; sizes = new int[] { 0, 0 }; }
                res.UserCount = sizes[0]; res.LogCount = sizes[1];
                if (!withLogs) return res;
                int urs = 0, lrs = 0;
                try
                {
                    byte[] utab = s.ReadTable(ZkCmd.REQ_USERS, null);
                    res.UserBytes = utab.Length;
                    res.Users = ParseUsers(utab, sizes[0], s.IsUdp, out urs);
                }
                catch (ZkException e) { if (e.Code == "COMM_KEY") throw; res.Users = new List<ZkUser>(); }
                byte[] ltab = s.ReadTable(ZkCmd.REQ_LOGS, progress);
                res.LogBytes = ltab.Length;
                res.Punches = ParseLogs(ltab, sizes[1], s.IsUdp, out lrs);
                res.UserRecordSize = urs; res.LogRecordSize = lrs;
            }
            return res;
        }
    }
}
