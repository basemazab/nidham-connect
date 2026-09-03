# Privacy — نِظام كونكت (Nidham Connect)

*آخر تحديث: ٣ سبتمبر ٢٠٢٦ — النسخة 2.0.3*

## بالعربي (المختصر)

البرنامج ده بيعمل حاجة واحدة: بياخد البصمات من جهاز الحضور اللي في شركتك
ويرفعها لحساب **شركتك انت** في نِظام. مش بيبعت أي حاجة لأي حد تاني.

**اللي بيتقري من الجهاز وبيترفع:**

- رقم الموظف على الجهاز (Enroll ID) ووقت وتاريخ كل بصمة.
- قايمة الموظفين المسجّلين على الجهاز: الرقم والاسم زي ما هو مكتوب في الجهاز
  (بيستخدم عشان لما يكون فيه رقم مش متطابق مع نِظام نقولك مين هو بالاسم بدل
  رقم أصمّ).
- رقم الجهاز التسلسلي واسمه وعنوانه على الشبكة، عشان النظام يعرف البصمات جاية
  منين.

**اللي مابيتبعتش خالص:** مفيش بصمة إصبع ولا صورة وش — الجهاز نفسه مابيديهاش
والبرنامج مابيطلبهاش. مفيش تتبّع، مفيش تحليلات، مفيش إعلانات، مفيش أي ملف من
الكمبيوتر.

**بيتصل بمين:**

| العنوان | ليه |
|---|---|
| `https://www.nidhamhr.com` (أو السيرفر اللي انت حاططه) | الاقتران ورفع البصمات |
| `https://api.github.com/repos/basemazab/nidham-connect/releases/latest` | يشوف فيه نسخة أحدث ولا لأ |

فحص التحديث ده بيبعت بس طلب عادي لجيت-هَب زي أي متصفح — مفيش أي بيانات منك
فيه. لو مش عايزه أصلًا: شغّل البرنامج بـ`--no-update`.

**اللي محفوظ على الكمبيوتر:** ملف `config.json` في
`%APPDATA%\Nidham Connect` فيه عنوان السيرفر، توكن الربط، وعنوان الجهاز
وآخر بصمة اترفعت. ومعاه `agent.log` — سجل التشغيل اللي بتبعته للدعم لما حاجة
تقف.

**اختيارك:**

- البرنامج مابيعملش **أي حاجة** قبل ما تكتب كود الربط بإيدك.
- أول ما يفتح بيسألك قبل ما يثبّت نفسه أو يشتغل مع ويندوز — ولو قلت لأ،
  بيشتغل من مكانه عادي.
- «إزالة البرنامج» من القايمة اللي جنب الساعة بتشيل كل حاجة، وبتسألك تحب تمسح
  الإعدادات والسجل كمان.
- من نِظام نفسه: «فصل البرنامج» في صفحة الأجهزة بتوقف الرفع فورًا.

**بيانات الموظفين:** بيانات الحضور دي بيانات شخصية بتخص موظفينك، وانت
(الشركة) المسؤول عنها قانونًا — البرنامج ده مجرد وسيلة نقل بين جهازك وحسابك.
اللي بيحصل بيها بعد كده محكوم بشروط استخدام نِظام وبقانون حماية البيانات
الشخصية المصري (قانون ١٥١ لسنة ٢٠٢٠).

---

## In English

Nidham Connect does one thing: it reads attendance records from the
fingerprint device on your company's local network and uploads them to **your
own** Nidham HR account. It sends nothing to anyone else.

**What it reads and transmits**

- The employee number stored on the device (Enroll ID) and the date and time
  of each punch.
- The device's enrolled-user list: number and the name as typed into the
  device. This exists so that when a number on the device has no matching
  employee in Nidham, the software can tell HR *who* rather than showing a
  bare number.
- The device's serial number, name and LAN address, so the server knows which
  device the records came from.

**What it never transmits.** No fingerprint templates and no face images —
the device does not expose them and this program does not ask for them. No
telemetry, no analytics, no advertising identifiers, no files from the
computer.

**Network destinations** — the complete list, both greppable in the source:

| Endpoint | Purpose | Source |
|---|---|---|
| `https://www.nidhamhr.com` (or the server the operator configures) | Pairing and punch upload | `src/Config.cs`, `src/Api.cs` |
| `https://api.github.com/repos/basemazab/nidham-connect/releases/latest` | Update check | `src/Updater.cs` |

The update check is an ordinary unauthenticated GitHub API request carrying no
user data. It can be disabled entirely with `--no-update`.

**Local storage.** `%APPDATA%\Nidham Connect\config.json` holds the server
URL, the pairing token, the configured device address and the timestamp of
the last uploaded punch. `agent.log` in the same folder is a plain-text
activity log intended to be sent to support when something fails.

**Consent and opt-out**

- The program does nothing at all until the operator types a pairing code
  obtained from their own Nidham dashboard. There is no default account and no
  data leaves the machine before pairing.
- On first run it *asks* before copying itself to `%LOCALAPPDATA%`, adding a
  Start-menu shortcut, or registering to start with Windows. Declining is
  fully supported — it then runs from wherever it was placed.
- "إزالة البرنامج" (Uninstall) in the tray menu reverses all of the above and
  offers to delete the configuration and logs. `--uninstall --purge` does the
  same from the command line.
- Revoking the agent from the Nidham dashboard invalidates the token
  immediately; the program stops uploading and asks to be re-paired.

**Controller / processor.** The attendance data is personal data about the
operator's own employees. The operator (the company) is the data controller;
this program is transport between their device and their account. Handling
after upload is governed by the Nidham HR terms of service and by Egypt's
Personal Data Protection Law 151/2020.

**Contact.** basemazab640@gmail.com
