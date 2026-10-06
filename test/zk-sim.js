// A fake ZK-family fingerprint device — speaks the exact TCP-4370 (and UDP)
// dialect, so the whole agent can be exercised on one machine with zero
// hardware. Grown from device-agent/test/zk-sim.js (the byte layouts were
// derived there by reading the wire format) with two additions that matter
// for real devices:
//
//   • chunked mode — a device holding thousands of punches answers
//     CMD_DATA_WRRQ with a size, then ships the table in CMD_DATA_RDY chunks.
//     The inline path only ever exercised the small-table case.
//   • UDP mode — very old firmware has no TCP listener; records are 28-byte
//     users / 16-byte logs instead of 72/40.
//
//   TCP frame = 50 50 82 7d | u32LE payloadLen | payload
//   payload   = cmd u16 | chksum u16 | session u16 | reply u16 | body
//   UDP       = payload only

const net = require("net");
const dgram = require("dgram");

const CMD = {
  CONNECT: 1000,
  EXIT: 1001,
  AUTH: 1102,
  OPTIONS_RRQ: 11,
  GET_FREE_SIZES: 50,
  PREPARE_DATA: 1500,
  DATA: 1501,
  FREE_DATA: 1502,
  DATA_WRRQ: 1503,
  DATA_RDY: 1504,
  ACK_OK: 2000,
  ACK_UNAUTH: 2005,
};

const USHRT_MAX = 65535;
const MAX_CHUNK = 65472;

function chksum(buf) {
  let sum = 0;
  for (let i = 0; i < buf.length; i += 2) {
    if (i === buf.length - 1) sum += buf[i];
    else sum += buf.readUInt16LE(i);
    sum %= USHRT_MAX;
  }
  return USHRT_MAX - sum - 1;
}

function payload(cmd, session, reply, body) {
  const data = Buffer.isBuffer(body) ? body : Buffer.from(body ?? "");
  const p = Buffer.alloc(8 + data.length);
  p.writeUInt16LE(cmd, 0);
  p.writeUInt16LE(0, 2);
  p.writeUInt16LE(session, 4);
  p.writeUInt16LE(reply, 6);
  data.copy(p, 8);
  p.writeUInt16LE(chksum(p), 2);
  return p;
}

// The comm-key handshake body, same derivation as the client's MakeCommKey (public
// zk-protocol spec). The device rebuilds it from its own key + the session and
// compares, using the tick byte the client sent (byte 2 travels in the clear).
function makeCommKey(key, sessionId, ticks) {
  let k = 0;
  for (let i = 0; i < 32; i++) k = ((k << 1) | ((key >>> i) & 1)) >>> 0;
  k = (k + sessionId) >>> 0;
  const b1 = ((k >>> 8) & 0xff) ^ 0x4b;
  const b2 = ((k >>> 16) & 0xff) ^ 0x53;
  const b3 = ((k >>> 24) & 0xff) ^ 0x4f;
  const B = ticks & 0xff;
  return Buffer.from([b2 ^ B, b3 ^ B, B, b1 ^ B]);
}

function tcpFrame(p) {
  const prefix = Buffer.from([0x50, 0x50, 0x82, 0x7d, 0, 0, 0, 0]);
  prefix.writeUInt32LE(p.length, 4);
  return Buffer.concat([prefix, p]);
}

// ZK packed timestamp — inverse of the client's DecodeTime.
function packTime(d) {
  let t = (d.getFullYear() - 2000) * 12 + d.getMonth();
  t = t * 31 + (d.getDate() - 1);
  t = t * 24 + d.getHours();
  t = t * 60 + d.getMinutes();
  t = t * 60 + d.getSeconds();
  return t;
}

// 🔴 Records are pre-filled with 0xA5, not zeros. A real device puts real bytes in
// the fields we do not decode; a zero-filled simulator makes an over-wide read look
// correct, because whatever it swallowed was a NUL terminator. That is exactly how a
// u32 read of a u16 id, and a 24-byte read of a 9-byte id, passed every test. Poison
// the padding and the test fails the moment a parser reaches past its field.
function poisoned(size) {
  const b = Buffer.alloc(size, 0xa5);
  return b;
}

function userRecord72(u) {
  const b = poisoned(72);
  b.writeUInt16LE(u.uid, 0);
  b.writeUInt8(0, 2);
  b.fill(0, 3, 11); // password field — terminated
  b.fill(0, 11, 35);
  b.write(String(u.name ?? "").slice(0, 23), 11, 24, "ascii");
  b.writeUInt32LE(0, 35); // cardno
  b.fill(0, 48, 57);
  b.write(String(u.pin).slice(0, 9), 48, 9, "ascii"); // char[9] — a 9-char id has NO terminator
  return b;
}

function userRecord28(u) {
  const b = poisoned(28);
  b.writeUInt16LE(u.uid, 0);
  b.writeUInt8(0, 2);
  b.fill(0, 8, 16);
  b.write(String(u.name ?? "").slice(0, 7), 8, 8, "ascii");
  b.writeUInt32LE(Number(u.pin) || 0, 24);
  return b;
}

function attRecord40(r, index) {
  const b = poisoned(40);
  b.writeUInt16LE(index % USHRT_MAX, 0);
  b.fill(0, 2, 11);
  b.write(String(r.pin).slice(0, 9), 2, 9, "ascii"); // char[9] — a 9-char id has NO terminator
  b.writeUInt8(1, 26); // verify type
  b.writeUInt32LE(packTime(r.at), 27);
  b.writeUInt8(0, 31); // state
  return b;
}

function attRecord16(r) {
  const b = poisoned(16);
  b.writeUInt16LE(Number(r.pin) || 0, 0);
  // bytes 2-3 stay poisoned on purpose: the id here is 16 bits, and a parser that
  // reads 32 must fail.
  b.writeUInt32LE(packTime(r.at), 4);
  return b;
}

// table payload = u32 byte-count + packed records (client does .subarray(4))
function tableBody(records) {
  const data = Buffer.concat(records);
  const head = Buffer.alloc(4);
  head.writeUInt32LE(data.length, 0);
  return Buffer.concat([head, data]);
}

/**
 * startSim({port, serial, deviceName, users, punches, commKeyLocked,
 *           commKeyAccepts, deviceKey, chunked, udp, silentTcp})
 *
 *   deviceKey: the key set on the device. When given, AUTH is accepted only if
 *              it carries exactly this key (0 included); without it the old
 *              commKeyAccepts switch decides.
 *   silentTcp: UDP mode only. ALSO accept TCP on the same port and never say a
 *              word. That is the first real device (2026-10-06): TCP 4370 open,
 *              CONNECT unanswered, the protocol only spoken over UDP.
 */
function startSim(opts = {}) {
  const state = {
    port: opts.port ?? 4370,
    serial: opts.serial ?? "SIM0012345678",
    deviceName: opts.deviceName ?? "F18/Sim",
    users: opts.users ?? [],
    punches: opts.punches ?? [],
    commKeyLocked: !!opts.commKeyLocked,     // demands AUTH
    commKeyAccepts: opts.commKeyAccepts !== false, // ...and accepts it
    deviceKey: typeof opts.deviceKey === "number" ? opts.deviceKey : null,
    silentTcp: !!opts.silentTcp,
    chunked: !!opts.chunked,
    udp: !!opts.udp,
    session: 0x5aa5,
    hits: [],
    authed: false,
    pending: null, // {buf} staged table awaiting DATA_RDY
  };

  const usersBody = () => tableBody(state.users.map(state.udp ? userRecord28 : userRecord72));
  const logsBody = () =>
    tableBody(state.punches.map((p, i) => (state.udp ? attRecord16(p) : attRecord40(p, i))));

  function handle(send, p) {
    const cmd = p.readUInt16LE(0);
    const replyId = p.readUInt16LE(6);
    const body = p.subarray(8);
    state.hits.push(cmd);

    if (cmd === CMD.CONNECT) {
      state.authed = false;
      if (state.commKeyLocked) return send(CMD.ACK_UNAUTH, replyId, "");
      return send(CMD.ACK_OK, replyId, "");
    }
    if (cmd === CMD.AUTH) {
      // With deviceKey the sim checks WHICH key the client sent (so «no key
      // configured» and «the key is 0» are told apart); without it, the old
      // switch only proves the client SENDS auth and recovers.
      const accepted =
        state.deviceKey !== null
          ? body.length >= 4 && makeCommKey(state.deviceKey, state.session, body[2]).equals(body.subarray(0, 4))
          : state.commKeyAccepts;
      if (!accepted) return send(CMD.ACK_UNAUTH, replyId, "");
      state.authed = true;
      return send(CMD.ACK_OK, replyId, "");
    }
    if (state.commKeyLocked && !state.authed) return send(CMD.ACK_UNAUTH, replyId, "");

    if (cmd === CMD.EXIT || cmd === CMD.FREE_DATA) return send(CMD.ACK_OK, replyId, "");

    if (cmd === CMD.OPTIONS_RRQ) {
      const keyword = body.toString("ascii").replace(/\0.*$/, "");
      const value =
        keyword === "~SerialNumber" ? state.serial :
        keyword === "~DeviceName" ? state.deviceName : "";
      return send(CMD.ACK_OK, replyId, `${keyword}=${value}\0`);
    }
    if (cmd === CMD.GET_FREE_SIZES) {
      // client reads users @ body 16, logs @ body 32, capacity @ body 64
      const b = Buffer.alloc(72);
      b.writeUInt32LE(state.users.length, 16);
      b.writeUInt32LE(state.punches.length, 32);
      b.writeUInt32LE(100000, 64);
      return send(CMD.ACK_OK, replyId, b);
    }
    if (cmd === CMD.DATA_WRRQ) {
      const table = body.length > 1 ? body[1] : 0;
      const buf = table === 0x09 ? usersBody() : table === 0x0d ? logsBody() : Buffer.alloc(0);
      if (!state.chunked) return send(CMD.DATA, replyId, buf);
      // Chunked: announce the size, wait for CMD_DATA_RDY windows.
      state.pending = buf;
      const head = Buffer.alloc(8);
      head.writeUInt8(0, 0);
      head.writeUInt32LE(buf.length, 1);
      return send(CMD.ACK_OK, replyId, head);
    }
    if (cmd === CMD.DATA_RDY) {
      const start = body.readUInt32LE(0);
      const size = body.readUInt32LE(4);
      const buf = state.pending ?? Buffer.alloc(0);
      const slice = buf.subarray(start, Math.min(start + size, buf.length));
      const head = Buffer.alloc(8);
      head.writeUInt32LE(slice.length, 0);
      send(CMD.PREPARE_DATA, replyId, head);
      // Split across frames the way a real device does — proves the client
      // reassembles instead of assuming one packet per chunk.
      const PIECE = 16384;
      for (let o = 0; o < slice.length; o += PIECE) {
        send(CMD.DATA, replyId, slice.subarray(o, Math.min(o + PIECE, slice.length)));
      }
      return send(CMD.ACK_OK, replyId, "");
    }
    return send(CMD.ACK_OK, replyId, "");
  }

  if (state.udp) {
    const sock = dgram.createSocket("udp4");
    sock.on("message", (msg, rinfo) => {
      if (msg.length < 8) return;
      const send = (cmd, replyId, body) => {
        const p = payload(cmd, state.session, replyId, body);
        sock.send(p, rinfo.port, rinfo.address);
      };
      handle(send, msg);
    });
    // TCP that accepts and never answers (silentTcp): connections are held open
    // and dropped on close, nothing is ever written back.
    const mute = state.silentTcp ? net.createServer((s) => { mute.held.push(s); s.on("error", () => {}); }) : null;
    if (mute) mute.held = [];
    const listenMute = () =>
      mute ? new Promise((r) => mute.listen(state.port, "127.0.0.1", r)) : Promise.resolve();
    const closeMute = () =>
      mute
        ? new Promise((r) => {
            for (const s of mute.held) s.destroy();
            mute.close(() => r());
          })
        : Promise.resolve();
    return new Promise((resolve) => {
      sock.bind(state.port, "127.0.0.1", async () => {
        await listenMute();
        resolve({
          state,
          close: async () => {
            await closeMute();
            await new Promise((r) => sock.close(() => r()));
          },
        });
      });
    });
  }

  const server = net.createServer((sock) => {
    let buf = Buffer.alloc(0);
    const send = (cmd, replyId, body) => {
      try { sock.write(tcpFrame(payload(cmd, state.session, replyId, body))); } catch { /* closed */ }
    };
    sock.on("data", (chunk) => {
      buf = Buffer.concat([buf, chunk]);
      while (buf.length >= 16) {
        const len = buf.readUInt32LE(4);
        if (buf.length < 8 + len) break;
        const p = buf.subarray(8, 8 + len);
        buf = buf.subarray(8 + len);
        handle(send, p);
      }
    });
    sock.on("error", () => {});
  });

  return new Promise((resolve) => {
    server.listen(state.port, "127.0.0.1", () =>
      resolve({
        state,
        close: () => new Promise((r) => server.close(() => r())),
      }),
    );
  });
}

module.exports = { startSim, packTime };
