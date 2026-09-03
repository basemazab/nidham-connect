# نِظام كونكت — Nidham Connect

برنامج ويندوز صغير بيوصّل **جهاز البصمة اللي في شركتك** بحساب شركتك في
[نِظام](https://www.nidhamhr.com): بيقرا البصمات من الجهاز على الشبكة المحلية
ويرفعها تلقائيًا، من غير ما تدخل قوايم الجهاز ولا تظبط راوتر ولا تعمل أي حاجة
على الشبكة.

> [English below](#english) · الرخصة: [MIT](LICENSE) · الخصوصية:
> [PRIVACY.md](PRIVACY.md) · الأمان: [SECURITY.md](SECURITY.md)

---

## المشكلة اللي بيحلها

أجهزة البصمة المنتشرة في مصر (ZKTeco وأشباهها) فيها طريقة رسمية عشان الجهاز
يبعت البصمات بنفسه للسحابة اسمها ADMS أو Cloud Server. المشكلة إن القايمة دي
مش موجودة أصلًا في أغلب الأجهزة اللي في السوق، ولو موجودة فضبطها فوق طاقة
المستخدم العادي.

نِظام كونكت بيقلب الاتجاه — زي برنامج ZKTime بتاع الجهاز نفسه اللي الناس
متعودة عليه: **البرنامج هو اللي بيكلّم الجهاز** على الشبكة المحلية (بروتوكول
TCP على المنفذ 4370، الموجود في كل أجهزة العيلة دي حتى القديم جدًا)، وبعدين
بيرفع لنِظام على HTTPS. يعني مفيش port forwarding ولا إعدادات راوتر.

## بيشتغل مع إيه

أجهزة **ZKTeco** والأجهزة المبنية على نفس البروتوكول (وده شامل أغلب الماركات
المعاد تسميتها في السوق المصري). البرنامج بيتكلم:

- **TCP على المنفذ 4370** — الطريقة الأساسية.
- **UDP** — للأجهزة القديمة جدًا اللي مالهاش مستمع TCP أصلًا.
- أجهزة عليها **Comm Key**: اكتب الرقم في إعدادات الجهاز داخل البرنامج.

مش متأكد إن جهازك مدعوم؟ شغّل من الـCMD:

```
Nidham-Connect.exe --probe 192.168.1.201 --full
```

بيقولك الجهاز رد ولا لأ، ورقمه التسلسلي، وكام بصمة في ذاكرته، وعيّنة من أول
سجلات زي ما البرنامج قراها بالظبط.

## طريقة الاستخدام

1. نزّل `Nidham-Connect.exe` من [آخر إصدار](https://github.com/basemazab/nidham-connect/releases/latest)
   على أي كمبيوتر متوصّل بنفس شبكة الجهاز (كمبيوتر الـHR عادةً)، و**افتحه**.
   مفيش مثبّت — ملف واحد، دوسة واحدة.
2. أول ما يفتح هيسألك: تحب أثبّته وأشغّله مع ويندوز؟ الرد «لأ» اختيار حقيقي —
   هيشتغل من مكانه عادي.
3. من نِظام: الإعدادات ← ربط أجهزة البصمة ← «اعمل كود ربط»، واكتب الكود في
   البرنامج.
4. دوس «دوّر في الشبكة» — هيلاقي الجهاز، ومن ساعتها البصمات بتترفع كل ٣ دقايق
   لوحدها.

> **رقم الموظف على الجهاز (Enroll ID) لازم يساوي «كود الموظف» في نِظام.** أي
> رقم مش متطابق البرنامج بيقولك عليه **بالاسم** المكتوب على الجهاز، وبيظهر
> كمان في صفحة الأجهزة عشان الـHR يصلّحه.

### ويندوز هيحذّرك أول مرة

الملف **لسه مش موقّع رقميًا**، فويندوز بيتعامل معاه كناشر غير معروف: هتشوف
شاشة زرقا «Windows protected your PC» ← دوس **More info** ← **Run anyway**.
وده بالظبط السبب اللي الكود اتنشر عامًا عشانه — تقديم على شهادة توقيع مجانية
من [SignPath Foundation](https://signpath.org/) للمشاريع مفتوحة المصدر. شوف
[CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md).

## الملفات على جهازك

| المكان | إيه فيه |
|---|---|
| `%LOCALAPPDATA%\Nidham Connect\` | البرنامج نفسه (لو وافقت على التثبيت) |
| `%APPDATA%\Nidham Connect\config.json` | السيرفر، توكن الربط، وإعدادات الجهاز |
| `%APPDATA%\Nidham Connect\agent.log` | سجل التشغيل — **أول حاجة تبعتها للدعم** |

الإزالة: «إزالة البرنامج» من القايمة اللي جنب الساعة، أو
`Nidham-Connect.exe --uninstall` (وزوّد `--purge` لو عايز تمسح الإعدادات
والسجل كمان).

## البناء من الكود

مفيش SDK ولا npm ولا أي حاجة تتنزّل. المترجم اللي بيبني البرنامج ده جاي جوّه
ويندوز نفسه:

```powershell
.\build.ps1          # يطلّع dist\Nidham-Connect.exe
.\build.ps1 -Test    # يبني ويشغّل الاختبارات (محتاج node)
```

---

<a name="english"></a>

## English

Nidham Connect is a small Windows tray program that connects a company's
**fingerprint attendance device** to their own account on
[Nidham HR](https://www.nidhamhr.com), an Egyptian HR/payroll service. It
reads attendance records from the device over the local network and uploads
them over HTTPS, so no router configuration or device cloud menu is needed.

**How it works.** ZKTeco-family devices support a push mode (ADMS / "Cloud
Server"), but that menu is absent from most devices actually deployed in the
Egyptian market. So this program inverts the direction: it speaks the same
TCP/4370 protocol the manufacturer's own ZKTime software uses, pulls the
attendance log, and posts it to the operator's Nidham tenant. Pairing is a
one-time 8-character code from the operator's dashboard, exchanged for a
bearer token stored locally.

**What it reads and sends** — the full list is in [PRIVACY.md](PRIVACY.md):
employee numbers and punch timestamps, plus the device's enrolled-user list
(number and name) so unmatched numbers can be reported by name. No fingerprint
templates, no images, no telemetry.

**About the network scan.** The "search the network" button attempts a TCP
connect to **one port (4370)** across the `/24` of a private address the
machine already holds, to locate the operator's own attendance device — the
same thing the manufacturer's software does. It never probes other ports,
never attempts authentication, and never tries to bypass a device's Comm Key;
if a device is protected it says so and asks the operator for the key. Adding
the device by IP address skips the scan entirely. See
[`src/Scan.cs`](src/Scan.cs) — it is about 100 lines — and
[SECURITY.md](SECURITY.md).

**Consent.** Nothing is transmitted before the operator pairs the program with
a code from their own dashboard. On first run it asks before installing
itself, creating a Start-menu shortcut, or registering to start with Windows,
and it ships a working uninstall (`--uninstall [--purge]`, or the tray menu).

### Building

No SDK, no package manager. The compiler ships inside Windows:

```powershell
.\build.ps1          # produces dist\Nidham-Connect.exe (~380 KB)
.\build.ps1 -Test    # build, then run the test suite (needs node)
```

The build is `csc.exe` from .NET Framework 4.x over `src\*.cs`, with the icon
and manifest from `build\`. The same script runs in
[CI](.github/workflows/build.yml) on every push.

### Tests

```
node test\e2e.mjs
```

`test/zk-sim.js` is a **byte-exact simulator** of a ZK device: it speaks the
real TCP-4370 frame format (and the UDP dialect), serves 72/28-byte user
records and 40/16-byte attendance records, and supports chunked table
transfers, Comm Key authentication and failure modes. The suite drives the
**real built executable** against it — pairing, syncing, cursor handling and
error reporting — over ~58 assertions, with no hardware involved.

Padding inside the simulated records is deliberately filled with non-zero
bytes. A zero-filled simulator makes an over-wide field read look correct,
because whatever it swallowed happened to be a terminator; poisoning the
padding makes the test fail the moment a parser reaches past its field.

### Layout

| Path | Role |
|---|---|
| `src/Zk.cs` | The ZK protocol client — TCP/UDP transport, framing, Comm Key auth, chunked table reads, record parsing |
| `src/Scan.cs` | The bounded local-network device search |
| `src/Api.cs` | Pairing and sync HTTP calls |
| `src/Sync.cs` | Cursor, resend window, chunking, Arabic error messages |
| `src/Config.cs` | The JSON config file and its atomic write |
| `src/Updater.cs` | Update check and self-replacement |
| `src/MainForm.cs` | Window, tray, first-run consent, uninstall |
| `src/Program.cs` | Entry point, headless commands, install/uninstall |
| `test/zk-sim.js` | The byte-exact device simulator |
| `test/e2e.mjs` | End-to-end suite driving the built exe |

### Headless commands

Useful for support and for scripting:

```
Nidham-Connect.exe --version
Nidham-Connect.exe --probe <ip[:port]> [--commkey N] [--full] [--out file]
Nidham-Connect.exe --pair <CODE> [--server URL] [--config DIR]
Nidham-Connect.exe --sync-once [--config DIR]
Nidham-Connect.exe --uninstall [--purge]
Nidham-Connect.exe --portable | --minimized | --no-update
```

## Licence

[MIT](LICENSE). "نِظام" / "Nidham HR" and the application icon are the
product's branding and are not licensed for use as your own product identity;
the MIT grant covers the code.

## Protocol credit

The ZK wire format implemented here was written from the publicly documented
protocol ([adrobinoga/zk-protocol](https://github.com/adrobinoga/zk-protocol))
and validated against the simulator in this repository. Structure and naming
are this project's own.
