// End-to-end without hardware: fake device (exact TCP-4370 / UDP dialect) →
// the REAL built exe → fake نِظام server. Verifies the wire mapping, the JSON
// the server would receive, cursor advancement, chunk splitting, the
// heartbeat path, comm-key handling, and the 401 unpair.
//
//   node test/e2e.mjs            (build first: .\build.ps1)
//
// 🔴 The exe is a GUI-subsystem binary, so headless commands report through
// `--out <file>`, not stdout. Every assertion below reads that file.

import http from "node:http";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { startSim } = require("./zk-sim.js");

const here = path.dirname(fileURLToPath(import.meta.url));
const EXE = path.join(here, "..", "dist", "Nidham-Connect.exe");
if (!fs.existsSync(EXE)) {
  console.error("dist/Nidham-Connect.exe مش موجود — شغّل .\\build.ps1 الأول");
  process.exit(1);
}

const PORT_TCP = 14370;
const PORT_BIG = 14371;
const PORT_UDP = 14372;
const PORT_KEY = 14373;
const PORT_KEYBAD = 14374;
const PORT_API = 18099;

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "nc-e2e-"));
const p2 = (n) => String(n).padStart(2, "0");
const now = new Date();
const today = `${now.getFullYear()}-${p2(now.getMonth() + 1)}-${p2(now.getDate())}`;
const at = (h, m, s) => {
  const d = new Date();
  d.setHours(h, m, s, 0);
  return d;
};

let failures = 0;
const check = (name, fn) => {
  try {
    fn();
    console.log(`  ✓ ${name}`);
  } catch (e) {
    failures++;
    console.error(`  ✗ ${name}: ${e.message}`);
  }
};

// ── run the exe headlessly and read its --out JSON ──
let runSeq = 0;
function run(args, { timeoutMs = 60000 } = {}) {
  const outFile = path.join(tmp, `out-${++runSeq}.json`);
  return new Promise((resolve) => {
    const child = spawn(EXE, [...args, "--out", outFile], { windowsHide: true });
    const timer = setTimeout(() => { try { child.kill(); } catch {} }, timeoutMs);
    child.on("exit", (code) => {
      clearTimeout(timer);
      let json = null;
      try { json = JSON.parse(fs.readFileSync(outFile, "utf8")); } catch {}
      resolve({ code, json });
    });
  });
}

// ── fake نِظام server ──
const received = [];
let syncStatus = 200;
let syncBody = null;
const api = http.createServer((req, res) => {
  let body = "";
  req.on("data", (c) => (body += c));
  req.on("end", () => {
    let json = null;
    try { json = JSON.parse(body || "{}"); } catch { json = { __unparsable: body }; }
    received.push({ url: req.url, method: req.method, auth: req.headers.authorization, ua: req.headers["user-agent"], json });
    res.setHeader("Content-Type", "application/json; charset=utf-8");
    if (req.url.startsWith("/api/device-agent/pair")) {
      if (String(json?.code) === "GOODCODE") {
        res.end(JSON.stringify({ token: "nda_" + "a".repeat(48), agent_id: "agent-1", company_name: "بيت الطعمية" }));
      } else {
        res.statusCode = 401;
        res.end(JSON.stringify({ error: "الكود مش صحيح أو انتهت صلاحيته — اعمل كود جديد من صفحة الأجهزة" }));
      }
      return;
    }
    if (syncStatus !== 200) {
      res.statusCode = syncStatus;
      res.end(JSON.stringify(syncBody ?? { error: "التوكن مش صالح — اعمل ربط من جديد بكود من صفحة الأجهزة" }));
      return;
    }
    const accepted = new Set((json.punches ?? []).map((p) => `${p.pin}|${p.date}`)).size;
    res.end(JSON.stringify({
      ok: true,
      accepted,
      unmatched: [{ pin: "99", name: "عامل من غير كود" }],
      device_active: true,
    }));
  });
});
await new Promise((r) => api.listen(PORT_API, "127.0.0.1", r));
const SERVER = `http://127.0.0.1:${PORT_API}`;

// ── devices ──
// 🔴 "123456789" is deliberate: the id field is char[9], so a 9-digit id fills it with
// NO null terminator. A parser that reads past its field only misbehaves on exactly
// this input — with a short id it stops at the NUL and looks correct. This fixture is
// what turns the poisoned padding in zk-sim.js into an actual failing test.
const LONG_PIN = "123456789";
const users = [
  { uid: 1, pin: "5", name: "Ahmed" },
  { uid: 2, pin: "7", name: "Mona" },
  { uid: 3, pin: "99", name: "NoCode" },
  { uid: 4, pin: LONG_PIN, name: "LongCode" },
];
const punches = [
  { pin: LONG_PIN, at: at(7, 45, 0) },   // first, so --probe's sample covers it
  { pin: "5", at: at(8, 58, 0) },
  { pin: "5", at: at(17, 3, 21) },
  { pin: "7", at: at(9, 12, 5) },
  { pin: "99", at: at(8, 0, 0) },
];

const sim = await startSim({ port: PORT_TCP, serial: "SIMX99887766", users, punches });

// 4,500 punches → 3 protocol chunks on the wire AND 2 upload chunks to نِظام.
const bigPunches = [];
for (let i = 0; i < 4500; i++) {
  const d = new Date();
  d.setHours(6 + Math.floor(i / 900), i % 60, Math.floor(i / 60) % 60, 0);
  bigPunches.push({ pin: String(100 + (i % 7)), at: d });
}
const big = await startSim({ port: PORT_BIG, serial: "SIMBIG0001", users, punches: bigPunches, chunked: true });
// 🔴 The 16-byte (UDP-era) attendance record stores the employee id as a **u16**,
// so an id above 65535 physically cannot exist on such a device. The 9-digit
// fixture used for TCP therefore gets its own numeric equivalent here — and 65535
// is chosen deliberately: it fills all 16 bits, so a parser that read 32 bits would
// pick up the poisoned bytes after it and return a wildly wrong id.
const UDP_MAX_PIN = "65535";
const udpUsers = [
  { uid: 1, pin: "5", name: "Ahmed" },
  { uid: 2, pin: "7", name: "Mona" },
  { uid: 3, pin: "99", name: "NoCode" },
  { uid: 4, pin: UDP_MAX_PIN, name: "MaxCode" },
];
const udpPunches = [
  { pin: UDP_MAX_PIN, at: at(7, 45, 0) },
  { pin: "5", at: at(8, 58, 0) },
  { pin: "5", at: at(17, 3, 21) },
  { pin: "7", at: at(9, 12, 5) },
  { pin: "99", at: at(8, 0, 0) },
];
const udpSim = await startSim({ port: PORT_UDP, udp: true, serial: "SIMUDP0001", users: udpUsers, punches: udpPunches });
const keyed = await startSim({ port: PORT_KEY, serial: "SIMKEY0001", users, punches, commKeyLocked: true, commKeyAccepts: true });
const keyBad = await startSim({ port: PORT_KEYBAD, users, punches, commKeyLocked: true, commKeyAccepts: false });

// ── 1) version ──
// Read the expected version from the source, never a literal: a hard-coded
// version turns every release bump into a red test for no reason, and people
// then learn to edit the assertion instead of reading it.
const SRC_VERSION = fs.readFileSync(path.join(here, "..", "src", "Program.cs"), "utf8")
  .match(/Version\s*=\s*"([\d.]+)"/)?.[1];
console.log("1) البرنامج بيشتغل ويردّ نسخته");
{
  const r = await run(["--version"]);
  check("الملف المتبني نسخته = اللي في السورس", () => {
    assert.ok(SRC_VERSION, "مش لاقيين رقم النسخة في src/Program.cs");
    assert.equal(r.json?.version, SRC_VERSION);
  });
  check("خروج صفر", () => assert.equal(r.code, 0));
}

// ── 2) probe: TCP inline ──
console.log("2) قراية جهاز TCP عادي");
{
  const r = await run(["--probe", `127.0.0.1:${PORT_TCP}`, "--full"]);
  check("ok", () => assert.equal(r.json?.ok, true));
  check("السيريال اتقرا", () => assert.equal(r.json.serial, "SIMX99887766"));
  check("اسم الجهاز اتقرا", () => assert.equal(r.json.deviceName, "F18/Sim"));
  check("الناقل tcp", () => assert.equal(r.json.transport, "tcp"));
  check("عدّاد الجهاز: 4 مستخدمين / 5 بصمات", () => {
    assert.equal(r.json.userCount, 4);
    assert.equal(r.json.logCount, 5);
  });
  check("اتفكّ 4 مستخدمين و5 بصمات", () => {
    assert.equal(r.json.users, 4);
    assert.equal(r.json.punches, 5);
  });
  check("الأكواد والأسماء اتفكّت بقيمها الصح (مش بالعدد بس)", () =>
    assert.deepEqual(r.json.sampleUsers, [
      { pin: "5", name: "Ahmed" },
      { pin: "7", name: "Mona" },
      { pin: "99", name: "NoCode" },
    ]));
  // 🔴 الفحص اللي بيمسك القراءة الأعرض من الحقل: كود ٩ خانات بيملا char[9] من غير
  // NUL، فالقارئ اللي بيقرا ٢٤ بايت بيبلع الحشو المسموم اللي بعده.
  check("كود ٩ خانات بيملا الحقل من غير NUL — بيرجع مظبوط", () =>
    assert.equal(r.json.samplePunches[0].pin, LONG_PIN));
  check("أوقات البصمات مظبوطة", () =>
    assert.equal(r.json.samplePunches[0].at, `${today} 07:45:00`));
}

// ── 3) probe: chunked (the path a real full device uses) ──
console.log("3) جهاز بذاكرة مليانة — التحميل على دفعات");
{
  const r = await run(["--probe", `127.0.0.1:${PORT_BIG}`, "--full"], { timeoutMs: 120000 });
  check("ok", () => assert.equal(r.json?.ok, true));
  check("كل الـ4500 بصمة اتجمّعت من الدفعات", () => assert.equal(r.json.punches, 4500));
  check("السيريال صح", () => assert.equal(r.json.serial, "SIMBIG0001"));
}

// ── 4) probe: UDP (very old firmware) ──
console.log("4) جهاز قديم UDP");
{
  const r = await run(["--probe", `127.0.0.1:${PORT_UDP}`, "--full"]);
  check("ok", () => assert.equal(r.json?.ok, true));
  check("الناقل udp", () => assert.equal(r.json.transport, "udp"));
  check("سجلات 16 بايت اتفكّت", () => assert.equal(r.json.punches, 5));
  check("مستخدمين 28 بايت اتفكّوا", () => assert.equal(r.json.users, 4));
  // 🔴 القيم مش العدد: الكود في سجل الـ16 بايت طوله 16 بت، والقراءة بـ32 بت
  // بتبلع البايتين اللي بعده. المحاكي بيسمّم البايتين دول عشان ده يفشل هنا.
  check("كود سجل الـ16 بايت 16 بت مش 32", () =>
    assert.equal(r.json.samplePunches[0].pin, UDP_MAX_PIN));
  check("أسماء وأكواد 28 بايت بقيمها", () =>
    assert.deepEqual(r.json.sampleUsers[0], { pin: "5", name: "Ahmed" }));
}

// ── 5) comm key ──
console.log("5) جهاز عليه Comm Key");
{
  const ok = await run(["--probe", `127.0.0.1:${PORT_KEY}`, "--commkey", "1234", "--full"]);
  check("بالمفتاح: بيوصل ويقرا", () => {
    assert.equal(ok.json?.ok, true);
    assert.equal(ok.json.punches, 5);
  });
  const noKey = await run(["--probe", `127.0.0.1:${PORT_KEY}`]);
  check("من غير مفتاح: COMM_KEY مش فشل غامض", () => assert.equal(noKey.json?.code, "COMM_KEY"));
  check("والرسالة عربي وبتقول يعمل إيه", () => assert.ok(/Comm Key/.test(noKey.json.arabic)));
  const bad = await run(["--probe", `127.0.0.1:${PORT_KEYBAD}`, "--commkey", "9999"]);
  check("مفتاح غلط → COMM_KEY برضه", () => assert.equal(bad.json?.code, "COMM_KEY"));
}

// ── 6) unreachable device ──
console.log("6) جهاز مش موجود");
{
  const r = await run(["--probe", "127.0.0.1:14999"]);
  check("مش بيعلّق ولا بيقع", () => assert.equal(r.json?.ok, false));
  check("رسالة عربي مفيدة", () => assert.ok(/الجهاز|الشبكة/.test(r.json.arabic ?? "")));
}

// ── 7) pairing ──
console.log("7) الاقتران");
const cfgDir = path.join(tmp, "cfg");
{
  const bad = await run(["--pair", "WRONGCOD", "--config", cfgDir, "--server", SERVER]);
  check("كود غلط: ok=false", () => assert.equal(bad.json?.ok, false));
  check("رسالة السيرفر العربي هي اللي بتظهر", () =>
    assert.ok(String(bad.json.error).includes("الكود مش صحيح")));
  check("مفيش توكن اتحفظ", () => assert.ok(!fs.existsSync(path.join(cfgDir, "config.json")) ||
    !JSON.parse(fs.readFileSync(path.join(cfgDir, "config.json"), "utf8")).token));

  const good = await run(["--pair", "goodcode", "--config", cfgDir, "--server", SERVER]);
  check("كود صح: ok=true واسم الشركة رجع", () => {
    assert.equal(good.json?.ok, true);
    assert.equal(good.json.companyName, "بيت الطعمية");
  });
  const cfg = JSON.parse(fs.readFileSync(path.join(cfgDir, "config.json"), "utf8"));
  check("التوكن اتحفظ في config.json", () => assert.ok(String(cfg.token).startsWith("nda_")));
  check("الكود اتبعت كابيتال (المستخدم كتبه صغير)", () => {
    const req = received.find((r) => r.url.includes("/pair") && r.json.code === "GOODCODE");
    assert.ok(req, "مفيش طلب فيه الكود بحروف كبيرة");
    assert.equal(req.json.version, SRC_VERSION);
    assert.ok(String(req.json.hostname).length > 0);
  });
  check("الاقتران بيسيب أثر في agent.log", () => {
    const log = fs.readFileSync(path.join(cfgDir, "agent.log"), "utf8");
    assert.ok(/الاقتران فشل/.test(log), "الفشل لازم يتسجّل");
    assert.ok(/اتربطنا بشركة/.test(log), "النجاح لازم يتسجّل");
  });

  // register the device the sync will read
  cfg.devices = [{
    key: "11112222-3333-4444-5555-666677778888",
    ip: "127.0.0.1", port: PORT_TCP, commKey: 0,
    name: "بصمة الاختبار", sinceDays: 60, lastStamp: null, serial: "",
  }];
  fs.writeFileSync(path.join(cfgDir, "config.json"), JSON.stringify(cfg, null, 2));
}

// ── 8) sync ──
console.log("8) المزامنة → الشكل اللي بيوصل السيرفر");
{
  const before = received.length;
  const r = await run(["--sync-once", "--config", cfgDir, "--server", SERVER]);
  check("ok", () => assert.equal(r.json?.ok, true));
  const d = r.json.devices[0];
  check("كل البصمات اتبعتت", () => assert.equal(d.sent, 5));
  check("الكيرسور وقف على آخر بصمة", () => assert.equal(d.newStamp, `${today} 17:03:21`));
  check("السيريال الحقيقي اترفع", () => assert.equal(d.serial, "SIMX99887766"));
  const sent = received[before];
  check("التوكن في الهيدر", () => assert.ok(String(sent.auth).startsWith("Bearer nda_")));
  check("الـUser-Agent بيعرّف بالبرنامج", () => assert.ok(sent.ua?.startsWith(`NidhamConnect/${SRC_VERSION}`), `UA was: ${sent.ua}`));
  check("device.key و serial في الطلب", () => {
    assert.equal(sent.json.device.key, "11112222-3333-4444-5555-666677778888");
    assert.equal(sent.json.device.serial, "SIMX99887766");
    assert.equal(sent.json.device.ip, "127.0.0.1");
  });
  check("شكل البصمة {pin,date,time} بالظبط", () =>
    assert.deepEqual(Object.keys(sent.json.punches[0]).sort(), ["date", "pin", "time"]));
  check("البصمات مرتبة زمنيًا وبالتوقيت الصح", () =>
    assert.deepEqual(sent.json.punches, [
      { pin: LONG_PIN, date: today, time: "07:45:00" },
      { pin: "99", date: today, time: "08:00:00" },
      { pin: "5", date: today, time: "08:58:00" },
      { pin: "7", date: today, time: "09:12:05" },
      { pin: "5", date: today, time: "17:03:21" },
    ]));
  check("قايمة موظفين الجهاز بالأسماء راحت معاها", () => {
    assert.equal(sent.json.users.length, 4);
    assert.deepEqual(sent.json.users[0], { pin: "5", name: "Ahmed" });
    assert.deepEqual(sent.json.users[3], { pin: LONG_PIN, name: "LongCode" });
  });
  check("الكيرسور اتكتب في config.json", () => {
    const cfg = JSON.parse(fs.readFileSync(path.join(cfgDir, "config.json"), "utf8"));
    assert.equal(cfg.devices[0].lastStamp, `${today} 17:03:21`);
    assert.equal(cfg.devices[0].serial, "SIMX99887766");
  });
}

// ── 8b) pinned serial mismatch — refuses an impostor instead of trusting it ──
// The ZK wire protocol has no device authentication (comm key 0 by default),
// so anything answering at the configured IP is trusted unless the app checks
// the serial itself against what was pinned earlier. Attendance feeds payroll,
// so this must fail closed: no data reaches the server, and the stale config
// on disk must NOT be overwritten with the impostor's identity.
console.log("8b) تغيير السيريال — الجهاز يترفض");
{
  const cfg = JSON.parse(fs.readFileSync(path.join(cfgDir, "config.json"), "utf8"));
  const staleLastStamp = cfg.devices[0].lastStamp;
  cfg.devices[0].serial = "WRONGSERIAL999"; // no longer matches the sim's real serial
  fs.writeFileSync(path.join(cfgDir, "config.json"), JSON.stringify(cfg, null, 2));
  const before = received.length;
  const r = await run(["--sync-once", "--config", cfgDir, "--server", SERVER]);
  check("السيريال المختلف بيوقف المزامنة", () => assert.equal(r.json.devices[0].ok, false));
  check("الكود SERIAL_MISMATCH", () => assert.equal(r.json.devices[0].code, "SERIAL_MISMATCH"));
  check("رسالة عربي بتقول سريال مختلف", () => assert.ok(/سريال مختلف/.test(r.json.devices[0].error)));
  check("مفيش طلب اتبعت للسيرفر خالص", () => assert.equal(received.length, before));
  check("الكيرسور والسيريال المحفوظين متغيّروش", () => {
    const after = JSON.parse(fs.readFileSync(path.join(cfgDir, "config.json"), "utf8"));
    assert.equal(after.devices[0].serial, "WRONGSERIAL999");
    assert.equal(after.devices[0].lastStamp, staleLastStamp);
  });
  // restore the correct pinned serial for the remaining tests
  cfg.devices[0].serial = "SIMX99887766";
  fs.writeFileSync(path.join(cfgDir, "config.json"), JSON.stringify(cfg, null, 2));
}

// ── 9) idempotent resend + heartbeat ──
console.log("9) نافذة الـ48 ساعة والـheartbeat");
{
  const before = received.length;
  const r = await run(["--sync-once", "--config", cfgDir, "--server", SERVER]);
  check("الكيرسور موجود ومع ذلك بيعيد إرسال آخر 48 ساعة", () => assert.equal(r.json.devices[0].sent, 5));
  check("طلب واحد بس اترفع", () => assert.equal(received.length - before, 1));

  const future = new Date(Date.now() + 3 * 24 * 3600 * 1000);
  const cfg = JSON.parse(fs.readFileSync(path.join(cfgDir, "config.json"), "utf8"));
  cfg.devices[0].lastStamp = `${future.getFullYear()}-${p2(future.getMonth() + 1)}-${p2(future.getDate())} 00:00:00`;
  fs.writeFileSync(path.join(cfgDir, "config.json"), JSON.stringify(cfg, null, 2));
  const before2 = received.length;
  const r2 = await run(["--sync-once", "--config", cfgDir, "--server", SERVER]);
  check("كيرسور بعد كل البصمات → صفر إرسال", () => assert.equal(r2.json.devices[0].sent, 0));
  check("ومع ذلك heartbeat اتبعت (عشان «شغّال دلوقتي» تفضل صادقة)", () => {
    assert.equal(received.length - before2, 1);
    assert.equal(received[before2].json.punches.length, 0);
    assert.equal(received[before2].json.users.length, 4);
  });
  check("الـunmatched رجعت للبرنامج", () => assert.equal(r2.json.devices[0].unmatched, 1));
}

// ── 10) upload chunking ──
console.log("10) جهاز مليان → الرفع بيتقسّم");
{
  const cfg = JSON.parse(fs.readFileSync(path.join(cfgDir, "config.json"), "utf8"));
  cfg.devices = [{
    key: "99998888-7777-6666-5555-444433332222",
    ip: "127.0.0.1", port: PORT_BIG, commKey: 0, name: "الجهاز المليان",
    sinceDays: 60, lastStamp: null, serial: "",
  }];
  fs.writeFileSync(path.join(cfgDir, "config.json"), JSON.stringify(cfg, null, 2));
  const before = received.length;
  const r = await run(["--sync-once", "--config", cfgDir, "--server", SERVER], { timeoutMs: 180000 });
  check("كل الـ4500 اترفعت", () => assert.equal(r.json.devices[0].sent, 4500));
  check("اتقسّمت على طلبين (سقف السيرفر 5000)", () => assert.equal(received.length - before, 2));
  check("كل دفعة تحت الحد", () => {
    assert.equal(received[before].json.punches.length, 4000);
    assert.equal(received[before + 1].json.punches.length, 500);
  });
  check("قايمة الموظفين مع أول دفعة بس", () => {
    assert.equal(received[before].json.users.length, 4);
    assert.equal(received[before + 1].json.users.length, 0);
  });
}

// ── 11) 401 unpairs ──
console.log("11) السيرفر بيرفض التوكن");
{
  syncStatus = 401;
  const r = await run(["--sync-once", "--config", cfgDir, "--server", SERVER], { timeoutMs: 180000 });
  check("الجهاز بيرجّع فشل", () => assert.equal(r.json.devices[0].ok, false));
  check("برسالة السيرفر العربي", () => assert.ok(/التوكن مش صالح/.test(r.json.devices[0].error)));
  syncStatus = 200;
}

// ── 12) the window boots ──
console.log("12) الواجهة بتفتح");
{
  const r = await run(["--selftest", "--config", cfgDir, "--server", SERVER], { timeoutMs: 60000 });
  check("SELFTEST PASS", () => assert.equal(r.json?.selftest, "PASS"));
  check("مرتبط → شاشة الأجهزة مش شاشة الكود", () => {
    assert.equal(r.json.paired, true);
    assert.equal(r.json.mainScreen, true);
    assert.equal(r.json.pairScreen, false);
  });

  const freshDir = path.join(tmp, "fresh");
  const r2 = await run(["--selftest", "--config", freshDir], { timeoutMs: 60000 });
  check("غير مرتبط → شاشة الكود", () => {
    assert.equal(r2.json?.selftest, "PASS");
    assert.equal(r2.json.paired, false);
    assert.equal(r2.json.pairScreen, true);
  });
}

// ── 13) update-signature verification never becomes a silent no-op ──
// This can't prove a REAL signature verifies true without the production
// private key (deliberately not in this repo — see sign-release.ps1's own
// self-check for that half). What it guards forever, with no secret needed,
// is the far more dangerous regression: a "verification" that quietly
// accepts anything. If this ever goes green with a fixed `true`, these fail.
console.log("13) توقيع التحديث بيرفض توقيع مش صحيح");
{
  const realExe = EXE;
  const garbageSig = path.join(tmp, "garbage.sig");

  fs.writeFileSync(garbageSig, Buffer.alloc(0));
  let r = await run(["--verify-sig", realExe, "--sig", garbageSig]);
  check("توقيع فاضي → مرفوض", () => assert.equal(r.json?.ok, false));

  fs.writeFileSync(garbageSig, crypto.randomBytes(256)); // right shape for RSA-2048, wrong content
  r = await run(["--verify-sig", realExe, "--sig", garbageSig]);
  check("توقيع عشوائي بنفس طول RSA-2048 → مرفوض", () => assert.equal(r.json?.ok, false));

  fs.writeFileSync(garbageSig, crypto.randomBytes(3));
  r = await run(["--verify-sig", realExe, "--sig", garbageSig]);
  check("توقيع بطول غلط → مرفوض من غير ما يقع", () => assert.equal(r.json?.ok, false));

  r = await run(["--verify-sig", realExe, "--sig", path.join(tmp, "does-not-exist.sig")]);
  check("ملف توقيع مش موجود → مرفوض من غير ما يقع", () => assert.equal(r.json?.ok, false));
}

await sim.close();
await big.close();
await udpSim.close();
await keyed.close();
await keyBad.close();
await new Promise((r) => api.close(r));
try { fs.rmSync(tmp, { recursive: true, force: true }); } catch {}

// 🔴 The exit code is the whole point of a release gate — print it loudly and
// never let a trailing handle turn a failure into a hang.
if (failures > 0) {
  console.error(`\n${failures} فشل`);
  process.exitCode = 1;
} else {
  console.log("\n✅ e2e كله عدّى");
  process.exitCode = 0;
}
setTimeout(() => process.exit(process.exitCode ?? 0), 3000).unref();
