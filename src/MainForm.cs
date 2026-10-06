// The window + tray. Two screens: pair (enter the 8-char code) and main
// (devices, sync, log). Closing hides to the tray — the agent's whole point
// is staying alive; quitting is explicit from the tray menu.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NidhamConnect
{
    public class MainForm : Form
    {
        readonly Config cfg;
        readonly bool startMinimized;
        readonly bool selfTest;
        readonly Args cliArgs;
        readonly bool offerInstall;

        NotifyIcon tray;
        Panel pairPanel, mainPanel;
        TextBox codeBox, logBox, serverBox;
        Label statusLbl, companyLbl, unmatchedLbl, pairMsg;
        ListView list;
        Button pairBtn, scanBtn, addBtn, syncBtn, removeBtn;
        CheckBox autoChk;
        System.Windows.Forms.Timer syncTimer, bootTimer, updateTimer;
        bool syncing, quitting, hintShown, suppressAutoChk;
        readonly Dictionary<string, string> lastText = new Dictionary<string, string>();
        readonly Dictionary<string, bool> lastOk = new Dictionary<string, bool>();
        readonly Dictionary<string, DateTime> lastWhen = new Dictionary<string, DateTime>();
        EventWaitHandle showEvent;

        static readonly Color Cyan = Color.FromArgb(8, 145, 178);
        static readonly Color Ink = Color.FromArgb(30, 41, 59);
        static readonly Color Muted = Color.FromArgb(100, 116, 139);

        public MainForm(Config cfg, bool startMinimized, bool selfTest, Args cliArgs, bool offerInstall)
        {
            this.cfg = cfg; this.startMinimized = startMinimized; this.selfTest = selfTest; this.cliArgs = cliArgs;
            this.offerInstall = offerInstall;
            Text = App.ProductName;
            Font = new Font("Segoe UI", 10f);
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
            StartPosition = FormStartPosition.CenterScreen;
            // 🔴 Sized for the machine this actually runs on: the HR desk PC.
            // 1366×768 at 125% scaling is still common there, and WinForms scales
            // this form up by the font ratio on top of whatever we ask for — a
            // 760px client area measured 807px tall on a 125% screen and the
            // bottom buttons fell off. Start small and clamp to the work area.
            ClientSize = new Size(540, 600);
            MinimumSize = new Size(500, 480);
            BackColor = Color.White;
            try { Icon = Icon.ExtractAssociatedIcon(App.ExePath); } catch { }

            BuildUi();
            BuildTray();
            ClampToScreen();
            Log.Line += OnLogLine;
            foreach (string l in Log.All().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)) AppendLog(l);

            syncTimer = new System.Windows.Forms.Timer();
            syncTimer.Interval = Math.Max(2, cfg.IntervalMin) * 60 * 1000;
            syncTimer.Tick += (s, e) => RunSync("auto");
            syncTimer.Start();

            bootTimer = new System.Windows.Forms.Timer();
            bootTimer.Interval = selfTest ? 1500 : 10000;
            bootTimer.Tick += (s, e) => { bootTimer.Stop(); if (selfTest) SelfTestReport(); else RunSync("auto"); };
            bootTimer.Start();

            updateTimer = new System.Windows.Forms.Timer();
            updateTimer.Interval = 6 * 60 * 60 * 1000;
            updateTimer.Tick += (s, e) => CheckUpdate(false);
            if (!selfTest && !cliArgs.Has("--no-update"))
            {
                updateTimer.Start();
                System.Windows.Forms.Timer first = new System.Windows.Forms.Timer();
                first.Interval = 30000;
                first.Tick += (s, e) => { first.Stop(); CheckUpdate(false); };
                first.Start();
            }

            // Second instance → show me.
            try
            {
                showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\NidhamConnect.Show");
                ThreadPool.RegisterWaitForSingleObject(showEvent, (st, timedOut) => { try { BeginInvoke(new Action(ShowMe)); } catch { } }, null, -1, false);
            }
            catch { }

            Refresh();
        }

        // Never open taller or wider than the usable desktop, and re-centre after
        // shrinking. WinForms' own font-ratio scaling happens before this runs,
        // which is exactly how the window grew past a 768px screen.
        void ClampToScreen()
        {
            Rectangle work = Screen.FromPoint(Location).WorkingArea;
            int w = Math.Min(Width, work.Width - 20);
            int h = Math.Min(Height, work.Height - 20);
            if (w != Width || h != Height)
            {
                MinimumSize = new Size(Math.Min(MinimumSize.Width, w), Math.Min(MinimumSize.Height, h));
                Size = new Size(w, h);
            }
            Location = new Point(
                work.X + Math.Max(0, (work.Width - Width) / 2),
                work.Y + Math.Max(0, (work.Height - Height) / 2));
        }

        // ── UI ──
        void BuildUi()
        {
            Panel header = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = Color.FromArgb(236, 254, 255), Padding = new Padding(16, 10, 16, 8) };
            Label title = new Label { Text = "نِظام كونكت", Font = new Font("Segoe UI", 16f, FontStyle.Bold), ForeColor = Ink, AutoSize = true, Location = new Point(16, 8) };
            Label sub = new Label { Text = "رفع البصمات لنِظام تلقائيًا — v" + App.Version, ForeColor = Muted, AutoSize = true, Location = new Point(16, 44) };
            statusLbl = new Label { AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Padding = new Padding(8, 3, 8, 3), Anchor = AnchorStyles.Top | AnchorStyles.Left, Location = new Point(400, 14) };
            header.Controls.Add(title); header.Controls.Add(sub); header.Controls.Add(statusLbl);
            header.Resize += (s, e) => statusLbl.Location = new Point(header.ClientSize.Width - statusLbl.Width - 16, 14);

            // Log (bottom)
            Panel logPanel = new Panel { Dock = DockStyle.Bottom, Height = 150, Padding = new Padding(16, 6, 16, 8) };
            Label logTitle = new Label { Text = "سجل التشغيل", Dock = DockStyle.Top, Height = 24, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), ForeColor = Ink };
            logBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 9f), BackColor = Color.FromArgb(248, 250, 252), BorderStyle = BorderStyle.FixedSingle };
            FlowLayoutPanel logBtns = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
            Button copyBtn = Btn("نسخ السجل", false);
            copyBtn.Click += (s, e) => { try { Clipboard.SetText(Log.All()); Log.Write("📋 السجل اتنسخ — الصقه في رسالة الدعم"); } catch { } };
            Button updBtn = Btn("تحقق من تحديث", false);
            updBtn.Click += (s, e) => CheckUpdate(true);
            logBtns.Controls.Add(copyBtn); logBtns.Controls.Add(updBtn);
            logPanel.Controls.Add(logBox); logPanel.Controls.Add(logBtns); logPanel.Controls.Add(logTitle);

            Panel content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 4) };
            pairPanel = BuildPairPanel();
            mainPanel = BuildMainPanel();
            content.Controls.Add(mainPanel); content.Controls.Add(pairPanel);

            Controls.Add(content); Controls.Add(logPanel); Controls.Add(header);
        }

        // Keep a wrapping label's MaximumSize tied to its container's real width.
        // Without this a Label measures itself against a hard-coded MaximumSize,
        // and any text past that point is CLIPPED, not wrapped — the tip line
        // under the pairing box lost its second half on a 540px window.
        static void Fit(Control container, Label label)
        {
            EventHandler resize = (s, e) =>
            {
                int w = container.ClientSize.Width - container.Padding.Horizontal
                        - label.Margin.Horizontal - SystemInformation.VerticalScrollBarWidth - 4;
                if (w > 40) label.MaximumSize = new Size(w, 0);
            };
            container.Resize += resize;
            resize(container, EventArgs.Empty);
        }

        static Button Btn(string text, bool primary)
        {
            Button b = new Button { Text = text, AutoSize = true, Padding = new Padding(10, 4, 10, 4), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 8, 0) };
            b.FlatAppearance.BorderColor = primary ? Cyan : Color.FromArgb(203, 213, 225);
            b.BackColor = primary ? Cyan : Color.White;
            b.ForeColor = primary ? Color.White : Ink;
            if (primary) b.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            return b;
        }

        // 🔴 TableLayoutPanel, not FlowLayoutPanel. A FlowLayoutPanel gives an
        // AutoSize label its *preferred* width, so a long wrapping label needs a
        // hard MaximumSize — and when that guess is wider than the space the
        // panel actually has, the tail is CLIPPED rather than wrapped. That is
        // exactly what ate the second half of the tip line under the code box on
        // a 540px window. A single 100%-wide column wraps against the real width
        // at every size, with nothing to keep in sync.
        Panel BuildPairPanel()
        {
            Panel p = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            TableLayoutPanel f = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
            };
            f.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            Action<Control, Padding> add = (c, m) =>
            {
                c.Margin = m;
                f.Controls.Add(c);
                f.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            };
            // A wrapping label: Dock=Fill inside a percent column re-wraps on
            // every resize with no width guess anywhere.
            Func<string, Color, Label> wrap = (text, color) => new Label
            {
                Text = text, ForeColor = color, AutoSize = true, Dock = DockStyle.Fill,
            };

            add(new Label { Text = "١) اربط البرنامج بحسابك في نِظام", Font = new Font("Segoe UI", 12f, FontStyle.Bold), ForeColor = Ink, AutoSize = true }, new Padding(0, 6, 0, 6));
            add(wrap("افتح نِظام على المتصفح → الإعدادات → ربط أجهزة البصمة → «اعمل كود ربط»، واكتب الكود هنا:", Muted), new Padding(0, 0, 0, 10));

            codeBox = new TextBox { Font = new Font("Consolas", 20f, FontStyle.Bold), CharacterCasing = CharacterCasing.Upper, MaxLength = 16, Width = 280, RightToLeft = RightToLeft.No, TextAlign = HorizontalAlignment.Center };
            codeBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoPair(); } };
            add(codeBox, new Padding(0, 0, 0, 10));

            pairBtn = Btn("اربط", true);
            pairBtn.AutoSize = false; pairBtn.Width = 280; pairBtn.Height = 42;
            pairBtn.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            pairBtn.Click += (s, e) => DoPair();
            add(pairBtn, new Padding(0, 0, 0, 6));

            pairMsg = wrap("", Color.Firebrick);
            add(pairMsg, new Padding(0, 6, 0, 6));

            LinkLabel open = new LinkLabel { Text = "فتح صفحة الأجهزة في المتصفح ↗", AutoSize = true };
            open.LinkClicked += (s, e) => OpenDevicesPage();
            add(open, new Padding(0, 4, 0, 12));

            add(wrap("💡 بعد الربط، البرنامج هيدوّر على جهاز البصمة في الشبكة لوحده. لازم الكمبيوتر ده يكون على نفس شبكة الجهاز (نفس الراوتر).", Muted), new Padding(0, 0, 0, 8));
            // 🔴 The customer is entitled to know what leaves this machine BEFORE they
            // pair, not in a policy document they will never open. This program reads
            // employees' attendance records — that is personal data.
            add(wrap("🔒 اللي بيتبعت: أرقام الموظفين على الجهاز وأسماءهم زي ما هي مكتوبة فيه، وأوقات البصمات. بيروح لحساب شركتك في نِظام بس، على اتصال مشفّر. مفيش أي حاجة تانية بتتبعت، ومفيش تتبّع ولا إعلانات.", Muted), new Padding(0, 0, 0, 14));

            CheckBox adv = new CheckBox { Text = "إعدادات متقدمة", AutoSize = true, ForeColor = Muted };
            FlowLayoutPanel advRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = false, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            advRow.Controls.Add(new Label { Text = "السيرفر:", AutoSize = true, Margin = new Padding(0, 10, 6, 0) });
            serverBox = new TextBox { Width = 240, RightToLeft = RightToLeft.No, Text = cfg.Server };
            advRow.Controls.Add(serverBox);
            Button saveSrv = Btn("حفظ", false);
            saveSrv.Click += (s, e) =>
            {
                string v = serverBox.Text.Trim().TrimEnd('/');
                // 🔴 HTTPS only. This address is where a long-lived tenant bearer token
                // gets sent on every sync; accepting http:// let anyone who could set it
                // (or trick someone into setting it) collect that token in cleartext.
                // localhost is the one exception, for the test harness.
                bool okUrl = System.Text.RegularExpressions.Regex.IsMatch(v, @"^https://[\w.-]+(:\d+)?$")
                    || System.Text.RegularExpressions.Regex.IsMatch(v, @"^http://(localhost|127\.0\.0\.1)(:\d+)?$");
                if (!okUrl) { pairMsg.ForeColor = Color.Firebrick; pairMsg.Text = "لازم يبدأ بـ https:// — العنوان ده بيتبعت عليه توكن الربط"; return; }
                cfg.Server = v; cfg.Save(); pairMsg.ForeColor = Color.SeaGreen; pairMsg.Text = "اتحفظ السيرفر: " + v;
            };
            advRow.Controls.Add(saveSrv);
            adv.CheckedChanged += (s, e) => advRow.Visible = adv.Checked;
            add(adv, new Padding(0, 0, 0, 4));
            add(advRow, new Padding(0, 0, 0, 8));

            p.Controls.Add(f);
            return p;
        }

        Panel BuildMainPanel()
        {
            Panel p = new Panel { Dock = DockStyle.Fill };
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            FlowLayoutPanel top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            companyLbl = new Label { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = Ink, Margin = new Padding(0, 4, 12, 4) };
            LinkLabel link = new LinkLabel { Text = "صفحة الأجهزة في نِظام ↗", AutoSize = true, Margin = new Padding(0, 6, 12, 4) };
            link.LinkClicked += (s, e) => OpenDevicesPage();
            Button unpair = Btn("فصل الربط", false); unpair.ForeColor = Color.Firebrick;
            unpair.Click += (s, e) =>
            {
                if (MessageBox.Show(this, "فصل الربط؟ البرنامج هيحتاج كود جديد عشان يرفع بصمات تاني.", App.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) != DialogResult.Yes) return;
                cfg.Token = null; cfg.AgentId = null; cfg.CompanyName = ""; cfg.Save();
                Log.Write("🔌 اتفصل الربط — البرنامج محتاج كود جديد");
                Refresh();
            };
            top.Controls.Add(companyLbl); top.Controls.Add(link); top.Controls.Add(unpair);
            t.Controls.Add(top, 0, 0);

            t.Controls.Add(new Label { Text = "أجهزة البصمة", AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = Ink, Margin = new Padding(0, 8, 0, 4) }, 0, 1);

            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, RightToLeftLayout = true, Font = new Font("Segoe UI", 9.5f) };
            list.Columns.Add("الجهاز", 150); list.Columns.Add("العنوان", 110); list.Columns.Add("آخر مزامنة", 90); list.Columns.Add("النتيجة", 220);
            // Fixed pixel widths overflow at 125% scaling and the address column
            // gets an ellipsis — and the device's IP is the first thing a support
            // call needs. Share the real width out instead.
            list.Resize += (s, e) =>
            {
                int w = list.ClientSize.Width - 4;
                if (w < 200) return;
                int[] pct = { 30, 22, 16, 32 };
                int used = 0;
                for (int i = 0; i < list.Columns.Count; i++)
                {
                    int cw = i == list.Columns.Count - 1 ? w - used : w * pct[i] / 100;
                    list.Columns[i].Width = cw;
                    used += cw;
                }
            };
            t.Controls.Add(list, 0, 2);

            FlowLayoutPanel btns = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Margin = new Padding(0, 8, 0, 4) };
            scanBtn = Btn("🔍 دوّر في الشبكة", true); scanBtn.Click += (s, e) => DoScan();
            addBtn = Btn("➕ إضافة بعنوان IP", false); addBtn.Click += (s, e) => DoAdd();
            syncBtn = Btn("🔄 مزامنة دلوقتي", false); syncBtn.Click += (s, e) => RunSync("manual");
            removeBtn = Btn("🗑 حذف المحدد", false); removeBtn.Click += (s, e) => DoRemove();
            btns.Controls.Add(scanBtn); btns.Controls.Add(addBtn); btns.Controls.Add(syncBtn); btns.Controls.Add(removeBtn);
            t.Controls.Add(btns, 0, 3);

            unmatchedLbl = new Label { AutoSize = true, ForeColor = Color.FromArgb(146, 64, 14), BackColor = Color.FromArgb(255, 251, 235), Padding = new Padding(8), Visible = false, Margin = new Padding(0, 4, 0, 4) };
            t.Controls.Add(unmatchedLbl, 0, 4);
            Fit(t, unmatchedLbl);

            autoChk = new CheckBox { Text = "شغّل البرنامج تلقائيًا مع ويندوز", AutoSize = true, Margin = new Padding(0, 6, 0, 4) };
            // 🔴 Guarded: Refresh() assigns autoChk.Checked, which raises this
            // event — so simply opening the window used to write an autostart
            // entry the customer never asked for.
            autoChk.CheckedChanged += (s, e) =>
            {
                if (suppressAutoChk) return;
                cfg.AutoLaunch = autoChk.Checked; cfg.Save();
                try { Autostart.Set(autoChk.Checked); } catch (Exception ex) { Log.Write("autostart: " + ex.Message); }
            };
            t.Controls.Add(autoChk, 0, 5);
            p.Controls.Add(t);
            return p;
        }

        void BuildTray()
        {
            tray = new NotifyIcon { Text = "نِظام كونكت — رفع البصمات تلقائيًا", Visible = true };
            try { tray.Icon = Icon; } catch { }
            if (tray.Icon == null) tray.Icon = SystemIcons.Application;
            ContextMenuStrip m = new ContextMenuStrip { RightToLeft = RightToLeft.Yes };
            m.Items.Add("فتح نِظام كونكت", null, (s, e) => ShowMe());
            m.Items.Add("مزامنة دلوقتي", null, (s, e) => RunSync("manual"));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("إزالة البرنامج من الكمبيوتر", null, (s, e) => DoUninstall());
            m.Items.Add("خروج نهائي (هيوقف رفع البصمات)", null, (s, e) => { quitting = true; Close(); });
            tray.ContextMenuStrip = m;
            tray.DoubleClick += (s, e) => ShowMe();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (startMinimized && !selfTest) { Hide(); return; }
            if (offerInstall && !selfTest) AskToInstall();
        }

        // 🔴 Asked, never assumed. Everything this dialog describes used to happen
        // silently before the first window appeared.
        void AskToInstall()
        {
            using (Form d = new Form())
            {
                d.Text = "تثبيت نِظام كونكت";
                d.RightToLeft = RightToLeft.Yes; d.RightToLeftLayout = true;
                d.Font = Font; d.FormBorderStyle = FormBorderStyle.FixedDialog;
                d.MaximizeBox = false; d.MinimizeBox = false; d.ShowInTaskbar = false;
                d.StartPosition = FormStartPosition.CenterParent;
                d.ClientSize = new Size(480, 300);
                d.BackColor = Color.White;

                TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16) };
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                Action<Control, Padding> add = (c, m) => { c.Margin = m; t.Controls.Add(c); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); };
                Func<string, Font, Color, Label> lab = (txt, f, col) => new Label { Text = txt, Font = f, ForeColor = col, AutoSize = true, Dock = DockStyle.Fill };

                add(lab("تحب أثبّت البرنامج على الكمبيوتر ده؟", new Font("Segoe UI", 12f, FontStyle.Bold), Ink), new Padding(0, 0, 0, 8));
                add(lab("عشان البصمات تترفع لوحدها والبرنامج مايضيعش لما تنضّف مجلد التنزيلات، هعمل التلاتة دول — ومش هعمل أي حاجة تانية:", new Font("Segoe UI", 10f), Muted), new Padding(0, 0, 0, 8));
                add(lab("• أنسخ نفسي في:\n   " + App.InstallDir + "\n• أحط اختصار في قايمة ابدأ\n• أشتغل تلقائيًا مع ويندوز (تقدر تلغيها من الشاشة الجاية في أي وقت)", new Font("Segoe UI", 10f), Ink), new Padding(0, 0, 0, 10));
                add(lab("ولو غيّرت رأيك: «إزالة البرنامج» من القايمة اللي جنب الساعة بتشيل كل ده.", new Font("Segoe UI", 9.5f), Muted), new Padding(0, 0, 0, 12));

                FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
                Button yes = Btn("تمام، ثبّته", true); yes.AutoSize = false; yes.Width = 150; yes.Height = 40;
                Button no = Btn("لأ، شغّله من هنا بس", false); no.AutoSize = false; no.Width = 190; no.Height = 40;
                row.Controls.Add(yes); row.Controls.Add(no);
                add(row, new Padding(0, 0, 0, 0));

                bool install = false;
                yes.Click += (s2, e2) => { install = true; d.Close(); };
                no.Click += (s2, e2) => { d.Close(); };
                d.AcceptButton = yes; d.CancelButton = no;
                d.ShowDialog(this);

                if (!install)
                {
                    Log.Write("المستخدم اختار يشغّله من مكانه — مفيش تثبيت ولا تشغيل تلقائي");
                    cfg.AutoLaunch = false; cfg.Save();
                    return;
                }
                Log.Write("المستخدم وافق على التثبيت");
                if (Install.Run(true)) { quitting = true; tray.Visible = false; Environment.Exit(0); }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!quitting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!hintShown)
                {
                    hintShown = true;
                    try { tray.ShowBalloonTip(4000, "نِظام كونكت شغّال في الخلفية", "البصمات بتترفع تلقائيًا. للفتح: دوس على الأيقونة جنب الساعة.", ToolTipIcon.Info); } catch { }
                }
                return;
            }
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        void DoUninstall()
        {
            DialogResult r = MessageBox.Show(this,
                "هيتشال: مدخل التشغيل مع ويندوز، اختصار قايمة ابدأ، وملف البرنامج.\n\n" +
                "تحب أمسح كمان الإعدادات والسجل (هتحتاج كود ربط جديد لو رجّعته)؟\n\n" +
                "نعم = امسح كل حاجة · لا = سيب الإعدادات · إلغاء = بلاش",
                "إزالة نِظام كونكت", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button3, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            if (r == DialogResult.Cancel) return;
            List<string> removed = Install.Uninstall(r == DialogResult.Yes);
            MessageBox.Show(this, "اتشال:\n• " + string.Join("\n• ", removed.ToArray()) + "\n\nالبرنامج هيقفل دلوقتي.",
                "تمت الإزالة", MessageBoxButtons.OK, MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
            quitting = true; tray.Visible = false;
            Environment.Exit(0);
        }

        void ShowMe()
        {
            Show(); WindowState = FormWindowState.Normal; Activate();
        }

        void OpenDevicesPage()
        {
            try { Process.Start(cfg.Server.TrimEnd('/') + "/dashboard/settings/devices"); } catch { }
        }

        void OnLogLine(string line)
        {
            try { if (IsHandleCreated) BeginInvoke(new Action(() => AppendLog(line))); } catch { }
        }

        void AppendLog(string line)
        {
            if (logBox.TextLength > 60000) logBox.Text = logBox.Text.Substring(logBox.TextLength - 40000);
            logBox.AppendText(line + Environment.NewLine);
        }

        // ── state → UI ──
        public new void Refresh()
        {
            bool paired = cfg.Paired;
            pairPanel.Visible = !paired;
            mainPanel.Visible = paired;
            if (paired)
            {
                companyLbl.Text = "مرتبط بشركة: " + (string.IsNullOrEmpty(cfg.CompanyName) ? "؟" : cfg.CompanyName);
                suppressAutoChk = true;
                autoChk.Checked = cfg.AutoLaunch;
                suppressAutoChk = false;
                RefreshList();
            }
            if (!paired) { SetStatus("غير مرتبط", Color.FromArgb(254, 226, 226), Color.Firebrick); }
            else if (syncing) SetStatus("بيزامن…", Color.FromArgb(224, 242, 254), Cyan);
            else if (cfg.Devices.Count == 0) SetStatus("مفيش أجهزة لسه", Color.FromArgb(254, 243, 199), Color.FromArgb(146, 64, 14));
            else SetStatus("شغّال", Color.FromArgb(220, 252, 231), Color.FromArgb(21, 128, 61));
            syncBtn.Enabled = !syncing && cfg.Devices.Count > 0;
            scanBtn.Enabled = !syncing;
        }

        void SetStatus(string text, Color back, Color fore)
        {
            statusLbl.Text = text; statusLbl.BackColor = back; statusLbl.ForeColor = fore;
            statusLbl.Location = new Point(statusLbl.Parent.ClientSize.Width - statusLbl.Width - 16, 14);
        }

        void RefreshList()
        {
            list.BeginUpdate();
            list.Items.Clear();
            int unmatchedTotal = 0;
            foreach (DeviceConfig d in cfg.Devices)
            {
                ListViewItem it = new ListViewItem(Sync.Label(d));
                it.SubItems.Add(d.Ip + (d.Port != 4370 ? ":" + d.Port : ""));
                DateTime when;
                it.SubItems.Add(lastWhen.TryGetValue(d.Key, out when) ? when.ToString("HH:mm") : (d.LastStamp != null ? "—" : "لسه"));
                string txt; bool ok;
                it.SubItems.Add(lastText.TryGetValue(d.Key, out txt) ? txt : "في انتظار أول مزامنة");
                if (lastOk.TryGetValue(d.Key, out ok)) it.ForeColor = ok ? Color.FromArgb(21, 128, 61) : Color.Firebrick;
                it.Tag = d.Key;
                list.Items.Add(it);
            }
            list.EndUpdate();
            unmatchedLbl.Visible = false;
            foreach (KeyValuePair<string, int> kv in unmatchedCounts) unmatchedTotal += kv.Value;
            if (unmatchedTotal > 0)
            {
                unmatchedLbl.Text = "⚠ " + unmatchedTotal + " رقم على الجهاز من غير موظف مطابق في نِظام — التفاصيل بالاسم في صفحة الأجهزة، ومن هناك تقدر تعمل الموظفين الناقصين بضغطة.";
                unmatchedLbl.Visible = true;
            }
        }
        readonly Dictionary<string, int> unmatchedCounts = new Dictionary<string, int>();

        // ── actions ──
        async void DoPair()
        {
            string code = codeBox.Text.Trim().Replace("-", "").Replace(" ", "").ToUpperInvariant();
            if (code.Length < 6) { pairMsg.ForeColor = Color.Firebrick; pairMsg.Text = "اكتب كود الربط زي ما هو ظاهر في نِظام"; return; }
            pairBtn.Enabled = false; pairMsg.ForeColor = Muted; pairMsg.Text = "بنربط…";
            try
            {
                PairResult p = await Api.Pair(cfg.Server, code, Environment.MachineName);
                cfg.Token = p.Token; cfg.AgentId = p.AgentId; cfg.CompanyName = p.CompanyName; cfg.Save();
                Log.Write("🔗 اتربطنا بشركة «" + p.CompanyName + "» بنجاح");
                try { Autostart.Set(cfg.AutoLaunch); } catch { }
                pairMsg.Text = ""; codeBox.Text = "";
                Refresh();
                if (cfg.Devices.Count == 0) DoScan();
            }
            catch (Exception e)
            {
                string msg = Sync.Arabic(e);
                ApiException ae = e as ApiException;
                Log.Write("❌ الاقتران فشل — " + (ae != null ? "السيرفر رجّع " + ae.Status + ": " : "") + msg);
                pairMsg.ForeColor = Color.Firebrick; pairMsg.Text = msg;
            }
            finally { pairBtn.Enabled = true; }
        }

        async void DoScan()
        {
            scanBtn.Enabled = false;
            Log.Write("🔍 بندوّر على أجهزة البصمة في الشبكة...");
            try
            {
                List<string> subnets = Scan.LocalSubnets();
                if (subnets.Count == 0) { Log.Write("❌ الكمبيوتر ده مش متوصّل بأي شبكة محلية"); return; }
                List<string> candidates = await Scan.Run(p => { });
                List<ScanHit> hits = new List<ScanHit>();
                foreach (string ip in candidates)
                {
                    ScanHit h = new ScanHit { Ip = ip };
                    try
                    {
                        ZkReadResult r = await Task.Run(() => Zk.Read(ip, Scan.ZK_PORT, 0, 6000, false, null));
                        h.Serial = r.Serial; h.Name = r.DeviceName; h.Ok = true;
                    }
                    catch (ZkException e) { h.Ok = false; h.CommKey = e.Code == "COMM_KEY"; }
                    catch (Exception) { h.Ok = false; }
                    if (h.Ok || h.CommKey) hits.Add(h);
                }
                if (hits.Count == 0)
                {
                    Log.Write("مفيش أجهزة ظهرت في الشبكة (" + string.Join(", ", subnets.ToArray()) + ".x) — اتأكد إن الجهاز شغّال ومتوصّل بنفس الراوتر، أو ضيفه بعنوانه");
                    return;
                }
                Log.Write("✓ لقينا " + hits.Count + " جهاز");
                using (ScanResultDialog d = new ScanResultDialog(hits, cfg))
                {
                    if (d.ShowDialog(this) == DialogResult.OK && d.Added > 0)
                    {
                        cfg.Save(); Refresh(); RunSync("auto");
                    }
                }
            }
            catch (Exception e) { Log.Write("❌ الفحص فشل: " + e.Message); }
            finally { scanBtn.Enabled = true; Refresh(); }
        }

        void DoAdd()
        {
            using (AddDeviceDialog d = new AddDeviceDialog(cfg))
            {
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    cfg.Save(); Log.Write("➕ اتضاف جهاز: " + Sync.Label(d.Result)); Refresh(); RunSync("auto");
                }
            }
        }

        void DoRemove()
        {
            if (list.SelectedItems.Count == 0) { Log.Write("اختار جهاز من القايمة الأول"); return; }
            string key = (string)list.SelectedItems[0].Tag;
            DeviceConfig d = cfg.FindDevice(key);
            if (d == null) return;
            if (MessageBox.Show(this, "حذف «" + Sync.Label(d) + "» من البرنامج؟ (البصمات اللي اترفعت بتفضل في نِظام)", App.ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) != DialogResult.Yes) return;
            cfg.Devices.Remove(d); cfg.Save();
            lastText.Remove(key); lastOk.Remove(key); lastWhen.Remove(key); unmatchedCounts.Remove(key);
            Log.Write("🗑 اتشال جهاز: " + Sync.Label(d));
            Refresh();
        }

        async void RunSync(string trigger)
        {
            if (!cfg.Paired || syncing) return;
            if (cfg.Devices.Count == 0)
            {
                // Paired with nothing to read: the server used to see silence. Say why.
                await Sync.ReportNoDevices(cfg, Log.Write);
                return;
            }
            syncing = true; Refresh();
            Log.Write(trigger == "manual" ? "🔄 مزامنة يدوية..." : "🔄 مزامنة تلقائية...");
            foreach (DeviceConfig dev in new List<DeviceConfig>(cfg.Devices))
            {
                Exception readFailure = null;
                try
                {
                    SyncOutcome o = await Sync.Device(cfg, dev, Log.Write);
                    dev.LastStamp = o.NewStamp; if (!string.IsNullOrEmpty(o.Serial)) dev.Serial = o.Serial;
                    cfg.Save();
                    lastWhen[dev.Key] = DateTime.Now; lastOk[dev.Key] = true;
                    unmatchedCounts[dev.Key] = o.Unmatched.Count;
                    if (!o.DeviceActive) { lastText[dev.Key] = "الجهاز موقوف من صفحة الأجهزة"; Log.Write("⏸ " + Sync.Label(dev) + ": موقوف من نِظام"); }
                    else if (o.Sent > 0) { lastText[dev.Key] = "اترفع " + o.Sent + " بصمة (" + o.Accepted + " يوم حضور)"; Log.Write("✅ " + Sync.Label(dev) + ": " + lastText[dev.Key]); }
                    else { lastText[dev.Key] = "مفيش بصمات جديدة (" + o.OnDevice + " في الجهاز)"; Log.Write("✓ " + Sync.Label(dev) + ": مفيش بصمات جديدة"); }
                    if (o.Unmatched.Count > 0) Log.Write("⚠ " + o.Unmatched.Count + " رقم على الجهاز من غير موظف مطابق — التفاصيل في صفحة الأجهزة في نِظام");
                }
                catch (Exception e)
                {
                    string msg = Sync.Arabic(e);
                    lastWhen[dev.Key] = DateTime.Now; lastOk[dev.Key] = false; lastText[dev.Key] = msg;
                    Log.Write("❌ " + Sync.Label(dev) + ": " + msg);
                    ApiException ae = e as ApiException;
                    if (ae != null && ae.Status == 401)
                    {
                        cfg.Token = null; cfg.AgentId = null; cfg.Save();
                        break;
                    }
                    readFailure = e;
                }
                // C# 5 (the csc inside Windows) can't await inside catch.
                if (readFailure != null) await Sync.ReportFailure(cfg, dev, readFailure, Log.Write);
                RefreshList();
            }
            syncing = false; Refresh();
        }

        async void CheckUpdate(bool manual)
        {
            try
            {
                UpdateInfo info = await Updater.Check();
                if (info == null) { if (manual) Log.Write("✓ أنت على أحدث نسخة (" + App.Version + ")"); return; }
                Log.Write("⬇ فيه نسخة جديدة " + info.Version + " — بنحمّلها في الخلفية...");
                await Updater.Download(info);
                DialogResult r = MessageBox.Show(this, "نسخة جديدة من نِظام كونكت جاهزة (" + info.Version + "). نقفل ونتحدّث دلوقتي؟ (لو «لا» هتتحدّث لوحدها مع التشغيل الجاي)", "تحديث جديد", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
                if (r == DialogResult.Yes) { quitting = true; tray.Visible = false; Updater.ApplyAndRestart(cliArgs.Raw); }
            }
            catch (Exception e) { if (manual) Log.Write("التحقق من التحديث فشل: " + e.Message); }
        }

        // `--selftest`: prove the window boots into the right screen, then exit.
        void SelfTestReport()
        {
            bool ok = (pairPanel.Visible != mainPanel.Visible) && tray.Visible && IsHandleCreated;
            string json = "{\"selftest\":" + (ok ? "\"PASS\"" : "\"FAIL\"") + ",\"paired\":" + (cfg.Paired ? "true" : "false")
                + ",\"pairScreen\":" + (pairPanel.Visible ? "true" : "false") + ",\"mainScreen\":" + (mainPanel.Visible ? "true" : "false")
                + ",\"status\":\"" + statusLbl.Text + "\",\"devices\":" + cfg.Devices.Count + "}";
            Program.Emit(cliArgs, json);
            quitting = true;
            Environment.ExitCode = ok ? 0 : 1;
            Close();
        }
    }

    class ScanHit { public string Ip; public string Serial = ""; public string Name = ""; public bool Ok; public bool CommKey; }

    // Pick which of the found devices to add.
    class ScanResultDialog : Form
    {
        public int Added;
        public ScanResultDialog(List<ScanHit> hits, Config cfg)
        {
            Text = "أجهزة اتلقت في الشبكة"; RightToLeft = RightToLeft.Yes; RightToLeftLayout = true;
            Font = new Font("Segoe UI", 10f); ClientSize = new Size(460, 340); StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            Label l = new Label { Text = "علّم على الأجهزة اللي عايز البرنامج يرفع منها:", Dock = DockStyle.Top, Height = 30, Padding = new Padding(10, 8, 10, 0) };
            CheckedListBox box = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, Font = new Font("Segoe UI", 10f) };
            foreach (ScanHit h in hits)
            {
                bool already = false;
                foreach (DeviceConfig d in cfg.Devices) if (d.Ip == h.Ip) already = true;
                string label = h.Ip + "  —  " + (h.CommKey ? "محمي بـ Comm Key (ضيفه يدويًا واكتب الرقم)" : (string.IsNullOrEmpty(h.Name) ? "جهاز بصمة" : h.Name) + (string.IsNullOrEmpty(h.Serial) ? "" : "  #" + h.Serial)) + (already ? "  (متضاف بالفعل)" : "");
                int i = box.Items.Add(label, !already && !h.CommKey);
            }
            FlowLayoutPanel btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(10, 8, 10, 0) };
            Button ok = new Button { Text = "أضِف المحدد", AutoSize = true, DialogResult = DialogResult.OK };
            Button cancel = new Button { Text = "إلغاء", AutoSize = true, DialogResult = DialogResult.Cancel };
            btns.Controls.Add(ok); btns.Controls.Add(cancel);
            ok.Click += (s, e) =>
            {
                for (int i = 0; i < hits.Count; i++)
                {
                    if (!box.GetItemChecked(i)) continue;
                    ScanHit h = hits[i];
                    bool already = false;
                    foreach (DeviceConfig d in cfg.Devices) if (d.Ip == h.Ip) already = true;
                    if (already || h.CommKey) continue;
                    DeviceConfig dc = new DeviceConfig { Key = Guid.NewGuid().ToString(), Ip = h.Ip, Port = Scan.ZK_PORT, Name = "جهاز البصمة (" + h.Ip + ")", Serial = h.Serial ?? "" };
                    cfg.Devices.Add(dc); Added++;
                    Log.Write("➕ اتضاف جهاز: " + dc.Name + (string.IsNullOrEmpty(h.Serial) ? "" : " #" + h.Serial));
                }
            };
            Controls.Add(box); Controls.Add(btns); Controls.Add(l);
            AcceptButton = ok; CancelButton = cancel;
        }
    }

    // Manual add by IP (+ port, comm key, name, how far back to read).
    class AddDeviceDialog : Form
    {
        public DeviceConfig Result;
        readonly Config cfg;
        TextBox ip, port, key, name, days; Label msg; Button test, ok;
        // Serial pinned by the last successful "اختبار الاتصال" — only trusted
        // in DoOk() if the IP field still matches what was actually tested.
        string testedIp = null;
        string testedSerial = "";

        public AddDeviceDialog(Config cfg)
        {
            this.cfg = cfg;
            Text = "إضافة جهاز بعنوان IP"; RightToLeft = RightToLeft.Yes; RightToLeftLayout = true;
            Font = new Font("Segoe UI", 10f); ClientSize = new Size(440, 330); StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ip = Row(t, "عنوان IP للجهاز", "", false);
            port = Row(t, "المنفذ (Port)", "4370", false);
            key = Row(t, "Comm Key (لو موجود)", "0", false);
            name = Row(t, "اسم الجهاز", "", true);
            days = Row(t, "اقرا آخر كام يوم أول مرة", "60", false);
            Label hint = new Label { Text = "تلاقي العنوان في شاشة الجهاز: Menu → Comm → Ethernet → IP Address", AutoSize = true, ForeColor = Color.FromArgb(100, 116, 139), MaximumSize = new Size(400, 0) };
            t.Controls.Add(hint); t.SetColumnSpan(hint, 2);
            msg = new Label { AutoSize = true, MaximumSize = new Size(400, 0), ForeColor = Color.Firebrick };
            t.Controls.Add(msg); t.SetColumnSpan(msg, 2);
            FlowLayoutPanel btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(10, 8, 10, 0) };
            test = new Button { Text = "اختبار الاتصال", AutoSize = true };
            ok = new Button { Text = "أضِف", AutoSize = true };
            Button cancel = new Button { Text = "إلغاء", AutoSize = true, DialogResult = DialogResult.Cancel };
            test.Click += (s, e) => DoTest();
            ok.Click += (s, e) => DoOk();
            btns.Controls.Add(ok); btns.Controls.Add(test); btns.Controls.Add(cancel);
            Controls.Add(t); Controls.Add(btns);
            CancelButton = cancel;
        }

        static TextBox Row(TableLayoutPanel t, string label, string def, bool rtl)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
            TextBox b = new TextBox { Text = def, Dock = DockStyle.Fill, RightToLeft = rtl ? RightToLeft.Yes : RightToLeft.No, Margin = new Padding(0, 4, 0, 4) };
            t.Controls.Add(b);
            return b;
        }

        bool Validate(out string cleanIp, out int p, out int k, out int d)
        {
            cleanIp = ip.Text.Trim(); p = 4370; k = 0; d = 60;
            if (!System.Text.RegularExpressions.Regex.IsMatch(cleanIp, @"^\d{1,3}(\.\d{1,3}){3}$")) { msg.Text = "اكتب عنوان IP صحيح — تلاقيه في شاشة الجهاز: Comm → Ethernet → IP Address"; return false; }
            int.TryParse(port.Text.Trim(), out p); if (p <= 0 || p > 65535) p = 4370;
            int.TryParse(key.Text.Trim(), out k); if (k < 0) k = 0;
            int.TryParse(days.Text.Trim(), out d); if (d < 0) d = 60;
            return true;
        }

        async void DoTest()
        {
            string cip; int p, k, d;
            if (!Validate(out cip, out p, out k, out d)) return;
            test.Enabled = false; msg.ForeColor = Color.FromArgb(100, 116, 139); msg.Text = "بنكلم الجهاز…";
            try
            {
                ZkReadResult r = await Task.Run(() => Zk.Read(cip, p, k, 8000, false, null));
                msg.ForeColor = Color.SeaGreen;
                msg.Text = "✓ الجهاز رد: " + (string.IsNullOrEmpty(r.DeviceName) ? "جهاز بصمة" : r.DeviceName) + (string.IsNullOrEmpty(r.Serial) ? "" : " #" + r.Serial) + " — " + r.LogCount + " بصمة في الذاكرة";
                if (string.IsNullOrEmpty(name.Text.Trim()) && !string.IsNullOrEmpty(r.DeviceName)) name.Text = r.DeviceName;
                // 🔴 Pin the serial now so the sync loop can later detect a
                // swapped device at this IP (Sync.Device's mismatch check).
                testedIp = cip; testedSerial = r.Serial ?? "";
            }
            catch (Exception e) { msg.ForeColor = Color.Firebrick; msg.Text = Sync.Arabic(e); testedIp = null; }
            finally { test.Enabled = true; }
        }

        void DoOk()
        {
            string cip; int p, k, d;
            if (!Validate(out cip, out p, out k, out d)) return;
            foreach (DeviceConfig dc in cfg.Devices) if (dc.Ip == cip && dc.Port == p) { msg.Text = "الجهاز ده متضاف بالفعل"; return; }
            // Only trust the pinned serial if it was tested against this exact
            // IP — if the user changed the IP field after testing, don't carry
            // a stale serial over to the wrong device. Not tested at all is
            // fine too: the first successful sync pins it instead.
            string serial = (testedIp == cip) ? testedSerial : "";
            Result = new DeviceConfig { Key = Guid.NewGuid().ToString(), Ip = cip, Port = p, CommKey = k, SinceDays = d, Name = string.IsNullOrEmpty(name.Text.Trim()) ? "جهاز البصمة (" + cip + ")" : name.Text.Trim(), Serial = serial };
            cfg.Devices.Add(Result);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
