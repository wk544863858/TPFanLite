
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;


namespace TPFanLite
{
    // ============ 主界面 ============
    class MainForm : Form
    {
        Label lblTemps, lblFan, lblMode, lblPower;
        CheckBox chkEc, chkMax;
        CheckBox chkSmart, chkStartup;
        NumericUpDown nudStart, nudPowerHigh, nudSteps, nudDwell, nudHyst;
        Label lblStatus;
        NotifyIcon tray;
        System.Windows.Forms.Timer poll;
        MenuItem mSleep, miSleepLinux, miSleepWin;
        MenuItem miPerf;

        Cfg cfg;
        Icon appIcon;

        // ---- 状态：风扇档位只有两级（EC 自动 / MAX 全速），智能控制统一调度风扇与功率 ----
        // baseLevel   : 用户选的基底档位（0x80=EC自动 / 0x40=MAX全速），唯一持久化
        int baseLevel = 0x80;
        int appliedLevel = -1;      // 上次实际写入 EC 0x2F 的值；-1=未知，需重写
        bool smartActive = true;    // 智能控制开关（ini SmartEnabled）
        bool exiting = false;
        bool loading = true;
        bool syncing = false;

        double lastUtil = 0;

        // 无效读数过滤：EC 偶发返回 0x80=128°C（温度）、0xFFFF=65535rpm（转速）等哨兵值，
        // 直接参与调度会假触发全速/假降档，这里沿用上一有效值，并把次数汇总进 smart.log。
        int lastCpuT = 60, lastApsT = 60, lastCrdT = 60, lastBatT = 45, lastRpm = 0;
        int badReads;
        DateTime lastBadLog = DateTime.MinValue;

        static bool TempOk(int t) { return t >= 0 && t <= 110; }      // >110 视为无效（TjMax 100）
        static bool RpmOk(int r) { return r >= 0 && r <= 20000; }     // 0xFFFF=65535 视为无效

        void NoteBadRead(string what)
        {
            badReads++;
            DateTime now = DateTime.Now;
            if ((now - lastBadLog).TotalSeconds < 60) return;
            SmartCtrl.Note("无效读数 " + badReads + " 次（最近 " + what + "），已沿用上一有效值");
            badReads = 0;
            lastBadLog = now;
        }

        bool startHiddenMode = false;
        bool allowShow = false;

        const int WM_POWERBROADCAST = 0x0218;
        const int PBT_APMSUSPEND = 0x0004;
        const int PBT_APMRESUMEAUTOMATIC = 0x0012;

        float scale = 1f;
        int S(int v) { return (int)Math.Round(v * scale); }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr hdc, int index);

        static float GetSysScale()
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            int dpi = GetDeviceCaps(dc, 88 /*LOGPIXELSX*/);
            ReleaseDC(IntPtr.Zero, dc);
            if (dpi <= 0) dpi = 96;
            return dpi / 96f;
        }

        static Icon MakeIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(28, 88, 156)))
                    g.FillEllipse(bg, 1, 1, 30, 30);
                using (SolidBrush fg = new SolidBrush(Color.White))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        System.Drawing.Drawing2D.GraphicsState st = g.Save();
                        g.TranslateTransform(16f, 16f);
                        g.RotateTransform(i * 120f);
                        g.FillEllipse(fg, -2f, -13f, 5f, 12f);
                        g.Restore(st);
                    }
                    g.FillEllipse(fg, 14, 14, 4, 4);
                }
            }
            IntPtr h = bmp.GetHicon();
            return Icon.FromHandle(h);
        }

        // 优先用 exe 内嵌的多尺寸 .ico（16/24/32/48/64/128/256），按当前 DPI 取最合适尺寸，避免 32px 位图放大发糊
        static Icon LoadAppIcon()
        {
            try
            {
                using (Stream s = typeof(MainForm).Assembly.GetManifestResourceStream("TPFanLite.ico"))
                {
                    if (s != null)
                    {
                        int sz = SystemInformation.SmallIconSize.Width;
                        if (sz < 24) sz = 24;
                        if (sz > 256) sz = 256;
                        return new Icon(s, new Size(sz, sz));
                    }
                }
            }
            catch { }
            return MakeIcon();
        }


        CheckBox MakeCheck(string text)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = false;
            c.Size = new Size(S(80), S(26));
            c.FlatStyle = FlatStyle.Flat;
            c.Font = new Font("Microsoft YaHei UI", 9.5F);
            c.ForeColor = Color.FromArgb(40, 44, 52);
            c.Margin = Padding.Empty;
            return c;
        }

        // 调度参数行的小工具：标签与数值框都交给网格居中，不要自己算坐标
        Label MakeSchedLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Microsoft YaHei UI", 9F);   // 必须先设字体再 AutoSize，否则按默认字体测宽会被裁
            l.AutoSize = true;      // 高度按字体自适应，避免缩放下被裁切
            l.ForeColor = Color.FromArgb(80, 86, 96);
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Margin = Padding.Empty;
            l.Anchor = AnchorStyles.None;
            return l;
        }

        NumericUpDown MakeSchedNud(int min, int max)
        {
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Font = new Font("Microsoft YaHei UI", 9F);
            n.Margin = Padding.Empty;
            n.Size = new Size(S(44), S(24));
            n.Anchor = AnchorStyles.None;
            return n;
        }

        // 秒为单位的数值框（步长确认时长等）
        NumericUpDown MakeSchedNudSec(int min, int max)
        {
            NumericUpDown n = MakeSchedNud(min, max);
            return n;
        }

        // 双缓冲的网格容器
        class GridPanel : TableLayoutPanel
        {
            public GridPanel() { DoubleBuffered = true; Margin = Padding.Empty; Padding = Padding.Empty; }
        }

        // 网格入格：col/row 从 0 起，span>1 跨列；Anchor 决定水平贴左/贴右与垂直居中
        void GridAdd(Control c, TableLayoutPanel tlp, int col, int row, int span, AnchorStyles a)
        {
            c.Margin = Padding.Empty;
            tlp.Controls.Add(c, col, row);
            if (span > 1) tlp.SetColumnSpan(c, span);
            c.Anchor = a;
        }

        /// <summary>调试/验证用：启动即弹出「关于」（--about）</summary>
        public static bool AboutOnStart;

        public MainForm(bool startHidden, Cfg config)
        {
            cfg = config;
            startHiddenMode = startHidden;
            scale = GetSysScale();

            appIcon = LoadAppIcon();
            Icon = appIcon;
            Text = "TPFanLite - 智能控制 (EC)";
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 9.5F);
            BackColor = Color.White;
            DoubleBuffered = true;
            ClientSize = new Size(S(460), S(152));
            Shown += delegate
            {
                if (!AboutOnStart) return;
                AboutOnStart = false;
                BeginInvoke((MethodInvoker)delegate { About.Show(this, scale); });
            };
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = cfg.AlwaysOnTop;   // ini AlwaysOnTop：窗口显示时总在最前（置顶不抢焦点）

            // 网格：左边距 24（= 卡片左边缘 12 + 内缩 12），右边距 448；四行等高 28，状态行 16
            // ---- 顶部状态卡：温度 / 风扇 / 模式 / 功率 ----
            Panel pTop = new Panel();
            pTop.SetBounds(S(12), S(8), S(436), S(56));
            pTop.BackColor = Color.FromArgb(246, 248, 251);

            lblTemps = new Label();
            lblTemps.SetBounds(S(12), S(12), S(412), S(18));   // 卡内 x=12 → 窗口绝对 24，与下方网格左基准线一致
            lblTemps.Font = new Font("Consolas", 9F);
            lblTemps.ForeColor = Color.FromArgb(70, 76, 86);
            lblTemps.Text = "CPU --C   APS --C   CRD --C   BAT --C";
            lblTemps.TextAlign = ContentAlignment.MiddleLeft;

            lblFan = new Label();
            lblFan.SetBounds(S(12), S(29), S(160), S(28));
            lblFan.Font = new Font("Consolas", 11F, FontStyle.Bold);
            lblFan.ForeColor = Color.FromArgb(26, 79, 139);
            lblFan.Text = "风扇 ---- rpm";
            lblFan.TextAlign = ContentAlignment.MiddleLeft;

            lblMode = new Label();
            lblMode.SetBounds(S(178), S(29), S(94), S(28));
            lblMode.Font = new Font("Microsoft YaHei UI", 10F);
            lblMode.ForeColor = Color.FromArgb(40, 44, 52);
            lblMode.Text = "EC 自动";
            lblMode.TextAlign = ContentAlignment.MiddleLeft;

            lblPower = new Label();
            lblPower.SetBounds(S(276), S(29), S(148), S(28));   // 右缘 424（卡内）= 绝对 436，与网格右缘一致
            lblPower.Font = new Font("Consolas", 9F);
            lblPower.TextAlign = ContentAlignment.MiddleRight;
            lblPower.Text = "PL1 --W";
            lblPower.ForeColor = Color.Gray;

            pTop.Controls.Add(lblTemps);
            pTop.Controls.Add(lblFan);
            pTop.Controls.Add(lblMode);
            pTop.Controls.Add(lblPower);

            // ---- 主区：TableLayoutPanel 网格（列宽定义一次、各行共享，单元格自动垂直居中） ----
            GridPanel tlp = new GridPanel();
            tlp.SetBounds(S(24), S(70), S(412), S(74));
            tlp.BackColor = Color.White;
            tlp.ColumnCount = 10; tlp.RowCount = 3;
            int[] cw = { 100, 34, 52, 22, 34, 52, 22, 34, 46, 16 };
            for (int i = 0; i < cw.Length; i++) tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(cw[i])));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, S(28)));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, S(28)));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, S(18)));

            // 第 1 行：风扇模式 + 智能控制 + 开机启动
            chkEc = MakeCheck("EC 自动");
            chkEc.CheckedChanged += delegate
            {
                if (loading || syncing) return;
                if (chkEc.Checked) ApplyModeUi(0x80);
            };
            chkMax = MakeCheck("MAX 全速");
            chkMax.CheckedChanged += delegate
            {
                if (loading || syncing) return;
                if (chkMax.Checked) ApplyModeUi(0x40);
            };
            chkSmart = MakeCheck("智能控制");
            chkSmart.CheckedChanged += delegate
            {
                smartActive = chkSmart.Checked;
                if (loading) return;
                SmartCtrl.Reset();
                ApplyEffective();
                Tick();
                Persist();
            };
            chkStartup = MakeCheck("开机启动");
            chkStartup.CheckedChanged += delegate
            {
                if (loading) return;
                if (!SetStartup(chkStartup.Checked))
                {
                    loading = true; chkStartup.Checked = !chkStartup.Checked; loading = false;
                    SetStatus("开机启动设置失败：注册表被安全软件拦截");
                    MessageBox.Show(this,
                        "开机启动设置失败：写入注册表被安全软件拦截。\n\n" +
                        "本机火绒 HIPS 对某些目录下的程序（例如开发工作区目录）会同时禁止\n" +
                        "写注册表、写临时文件，并连带拦截它启动的 reg.exe。\n\n" +
                        "解决办法：把 TPFanLite 安装到 %LOCALAPPDATA%\\TPFanLite\\ 后运行\n" +
                        "（该位置实测可正常写入），或在火绒「防护中心」中对该程序放行。",
                        "TPFanLite", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Persist();
            };

            // 第 2 行：调度参数（全速起点 / 功率低点 / 功率高点 / 挡位数）
            // 用网格对齐（与主网格同思路）：列宽固定、Margin=Empty、Anchor=None → 单元格内水平垂直居中
            TableLayoutPanel pSched = new TableLayoutPanel();
            pSched.Margin = Padding.Empty;
            pSched.Padding = Padding.Empty;
            pSched.BackColor = Color.White;
            // 五项：起点 / 高点 / 档位 / 回差 / 稳定（列宽合计 412，与主网格同宽）
            int[] sw = { 32, 38, 12, 32, 38, 12, 32, 50, 32, 38, 12, 32, 38, 14 };
            pSched.ColumnCount = sw.Length; pSched.RowCount = 1;
            for (int i = 0; i < sw.Length; i++) pSched.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(sw[i])));
            pSched.RowStyles.Add(new RowStyle(SizeType.Absolute, S(28)));

            Label lStart = MakeSchedLabel("起点");
            lStart.Anchor = AnchorStyles.Left;          // 首列贴左，和“EC 自动”同一起点
            nudStart = MakeSchedNud(40, 95);
            Label u1 = MakeSchedLabel("°");

            Label lHigh = MakeSchedLabel("高点");
            nudPowerHigh = MakeSchedNud(60, 105);
            Label u2 = MakeSchedLabel("°");

            Label lStep = MakeSchedLabel("档位");
            nudSteps = MakeSchedNud(1, 8);

            Label lHyst = MakeSchedLabel("回差");
            nudHyst = MakeSchedNud(2, 10);
            Label u3 = MakeSchedLabel("°");

            Label lDwell = MakeSchedLabel("稳定");
            nudDwell = MakeSchedNudSec(1, 60);
            Label u4 = MakeSchedLabel("s");
            u4.Anchor = AnchorStyles.Right;             // 末列贴右，和“开机启动”同一右缘

            EventHandler schedChanged = delegate
            {
                if (loading || syncing) return;
                ApplySchedFromUi();
                Persist();
                SmartCtrl.Reset();          // 参数变化后按当前温度重新计算
                SmartCtrl.LogCfg("参数变更");
                Tick();
            };
            nudStart.ValueChanged += schedChanged;
            nudPowerHigh.ValueChanged += schedChanged;
            nudSteps.ValueChanged += schedChanged;
            nudHyst.ValueChanged += schedChanged;
            nudDwell.ValueChanged += schedChanged;

            pSched.Controls.Add(lStart, 0, 0); pSched.Controls.Add(nudStart, 1, 0); pSched.Controls.Add(u1, 2, 0);
            pSched.Controls.Add(lHigh, 3, 0); pSched.Controls.Add(nudPowerHigh, 4, 0); pSched.Controls.Add(u2, 5, 0);
            pSched.Controls.Add(lStep, 6, 0); pSched.Controls.Add(nudSteps, 7, 0);
            pSched.Controls.Add(lHyst, 8, 0); pSched.Controls.Add(nudHyst, 9, 0); pSched.Controls.Add(u3, 10, 0);
            pSched.Controls.Add(lDwell, 11, 0); pSched.Controls.Add(nudDwell, 12, 0); pSched.Controls.Add(u4, 13, 0);

            // 状态行
            lblStatus = new Label();
            lblStatus.Size = new Size(S(412), S(18));
            lblStatus.Font = new Font("Microsoft YaHei UI", 8F);
            lblStatus.ForeColor = Color.FromArgb(130, 136, 146);
            lblStatus.Text = "EC 正常";
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;

            // 入格（列号见列宽表）：默认拉伸填满列宽并垂直居中；右对齐项只锚 Right
            GridAdd(chkEc, tlp, 0, 0, 1, AnchorStyles.Left | AnchorStyles.Right);
            GridAdd(chkMax, tlp, 1, 0, 3, AnchorStyles.Left | AnchorStyles.Right);
            GridAdd(chkSmart, tlp, 4, 0, 3, AnchorStyles.Left | AnchorStyles.Right);
            GridAdd(chkStartup, tlp, 7, 0, 3, AnchorStyles.Right);
            GridAdd(pSched, tlp, 0, 1, 10, AnchorStyles.Left | AnchorStyles.Right);
            GridAdd(lblStatus, tlp, 0, 2, 10, AnchorStyles.Left | AnchorStyles.Right);

            Controls.Add(pTop);
            Controls.Add(tlp);

            // ---- 托盘图标（常驻显示）----
            tray = new NotifyIcon();
            tray.Icon = appIcon;
            tray.Text = "TPFanLite - 智能控制";
            tray.Visible = true;
            ContextMenu cm = new ContextMenu();
            cm.MenuItems.Add("显示主窗口", delegate { ShowMe(); });
            cm.MenuItems.Add("-");
            cm.MenuItems.Add("EC 自动", delegate { ApplyModeUi(0x80); });
            cm.MenuItems.Add("MAX 全速", delegate { ApplyModeUi(0x40); });
            cm.MenuItems.Add("-");
            // 性能优先：开启后电池供电也钉住 PL1（默认只在 AC 下钉）
            miPerf = new MenuItem("性能优先");
            miPerf.Checked = cfg.PerfFirst;
            miPerf.Click += delegate
            {
                cfg.PerfFirst = !cfg.PerfFirst;
                miPerf.Checked = cfg.PerfFirst;
                SmartCtrl.Note("性能优先 " + (cfg.PerfFirst ? "ON（电池下也钉住）" : "OFF"));
                Persist();
                Tick();
            };
            cm.MenuItems.Add(miPerf);
            // 睡眠模式（BIOS SleepState）直接挂在根菜单（原“高级（BIOS）”只剩这一项）
            mSleep = new MenuItem("睡眠模式");
            miSleepLinux = new MenuItem("Linux (S3)", delegate { SetSleepState("Linux"); });
            miSleepWin = new MenuItem("Windows 10（现代待机）", delegate { SetSleepState("Windows 10"); });
            miSleepLinux.RadioCheck = true;
            miSleepWin.RadioCheck = true;
            mSleep.MenuItems.Add(miSleepLinux);
            mSleep.MenuItems.Add(miSleepWin);
            mSleep.Popup += delegate { UpdateSleepChecks(); };
            cm.MenuItems.Add(mSleep);
            cm.MenuItems.Add("-");
            MenuItem mDrv = new MenuItem("TVicPort 驱动");
            mDrv.MenuItems.Add("安装 / 修复", delegate { EnsureTvicPortDriver(true); });
            mDrv.MenuItems.Add("卸载", delegate { UninstallTvicPortDriverUi(); });
            cm.MenuItems.Add(mDrv);
            cm.MenuItems.Add("-");
            cm.MenuItems.Add("关于", delegate { About.Show(this, scale); });
            cm.MenuItems.Add("退出", delegate { exiting = true; Close(); });
            tray.ContextMenu = cm;
            tray.DoubleClick += delegate { ShowMe(); };

            poll = new System.Windows.Forms.Timer();
            poll.Interval = 1000;
            poll.Tick += delegate { Tick(); };
            poll.Start();


            // 驱动自检：未安装 TVicPort 时提示一键安装（提权子进程 --install-driver）
            EnsureTvicPortDriver(false);

            // PL1 钉住：TVicPort 映射 MCHBAR 后直接写 RAPL PKG_POWER_LIMIT（与 EC 同一驱动）
            Pl1Pin.Configure(cfg);
            SmartCtrl.Configure(cfg);
            if (!Pl1Pin.Start()) SetStatus("PL1 钉住不可用: " + Pl1Pin.LastError);

            // 监听"唤出窗口"信号（第二个实例被启动时触发）
            try
            {
                Thread th = new Thread(delegate()
                {
                    try
                    {
                        EventWaitHandle ev = new EventWaitHandle(false, EventResetMode.AutoReset, "TPFanLite_Show");
                        while (true)
                        {
                            ev.WaitOne();
                            if (IsDisposed) break;
                            try { BeginInvoke((MethodInvoker)delegate { ShowMe(); }); } catch { }
                        }
                    }
                    catch { }
                });
                th.IsBackground = true;
                th.Start();
            }
            catch { }

            // ---- 应用配置 ----
            chkEc.Checked = (cfg.Mode == 0x80);
            chkMax.Checked = (cfg.Mode == 0x40);
            chkSmart.Checked = cfg.SmartEnabled;
            smartActive = cfg.SmartEnabled;
            syncing = true;
            nudStart.Value = (decimal)cfg.TempStart;
            nudPowerHigh.Value = (decimal)cfg.TempPowerHigh;
            nudHyst.Value = (decimal)Math.Max(2, Math.Min(10, cfg.TempHyst));
            nudDwell.Value = Math.Max(1, Math.Min(60, cfg.HoldMs / 1000));
            nudSteps.Value = Math.Max(1, Math.Min(8, cfg.PowerSteps));
            syncing = false;
            chkStartup.Checked = cfg.StartWithWindows;
            if (cfg.StartWithWindows && !SetStartup(true))
                chkStartup.Checked = false;   // 被安全软件拦截：回弹并让 Persist() 写成 false
            baseLevel = cfg.Mode;
            if (cfg.WinX != int.MinValue && cfg.WinY != int.MinValue)
            {
                Rectangle vs = SystemInformation.VirtualScreen;
                if (cfg.WinX >= vs.Left && cfg.WinX < vs.Right - 100 && cfg.WinY >= vs.Top && cfg.WinY < vs.Bottom - 100)
                {
                    StartPosition = FormStartPosition.Manual;
                    Location = new Point(cfg.WinX, cfg.WinY);
                }
            }

            loading = false;
            Persist();                       // 首次运行即生成配置文件
            UpdateSmartEnabledUi();          // MAX 档起机时智能温控置灰
            appliedLevel = -1;
            ApplyEffective();
            if (startHiddenMode) { allowShow = false; }
        }

        // 启动时若 --tray，则不显示窗口（避免闪一下）
        protected override void SetVisibleCore(bool value)
        {
            if (startHiddenMode && !allowShow) { base.SetVisibleCore(false); return; }
            base.SetVisibleCore(value);
        }

        void ShowMe()
        {
            allowShow = true;
            Show();
            WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            TopMost = cfg.AlwaysOnTop;   // 每次唤出都按 ini 重新应用置顶
            Activate();
            BringToFront();
        }


        void Persist()
        {
            ApplySchedFromUi();
            cfg.Mode = baseLevel;
            cfg.SmartEnabled = chkSmart.Checked;
            cfg.StartWithWindows = chkStartup.Checked;
            cfg.Save();
        }

        // 把 GUI 上的调度参数写回 cfg（低点/高点保持至少 5°C 间距）
        void ApplySchedFromUi()
        {
            int st = (int)nudStart.Value;
            int hi = (int)nudPowerHigh.Value;
            if (hi < st + 5)
            {
                syncing = true;
                nudPowerHigh.Value = Math.Min((int)nudPowerHigh.Maximum, st + 5);
                syncing = false;
                hi = (int)nudPowerHigh.Value;
            }
            cfg.TempStart = st;
            cfg.TempPowerHigh = hi;
            cfg.PowerSteps = (int)nudSteps.Value;
            cfg.HoldMs = (int)nudDwell.Value * 1000;
            // 回差不能吃掉整个带：高点−回差 至少高于起点 5°C
            int maxHyst = Math.Max(2, hi - st - 5);
            if ((int)nudHyst.Value > maxHyst)
            {
                syncing = true;
                nudHyst.Value = Math.Min((int)nudHyst.Maximum, maxHyst);
                syncing = false;
            }
            cfg.TempHyst = (int)nudHyst.Value;
        }

        void ApplyModeUi(int v)
        {
            syncing = true;
            chkEc.Checked = (v == 0x80);
            chkMax.Checked = (v == 0x40);
            syncing = false;
            baseLevel = (v == 0x40) ? 0x40 : 0x80;
            SmartCtrl.Reset();        // 换档后按当前状态重新判定
            UpdateSmartEnabledUi();   // MAX 全速时智能控制失效并置灰
            ApplyEffective();
            Tick();
            Persist();
        }

        // MAX 全速是绝对档位：温控与自救都不参与；其余档位可被温控/自救临时顶到全速
        void UpdateSmartEnabledUi()
        {
            bool en = (baseLevel != 0x40);
            if (chkSmart.Enabled != en) chkSmart.Enabled = en;
        }

        // 唯一决定写入 EC 0x2F 的值（MAX 全速 / 智能控制的全速请求 / EC 自动）
        int Effective()
        {
            if (baseLevel == 0x40) return 0x40;
            if (smartActive && SmartCtrl.FanFull) return 0x40;
            return 0x80;
        }

        void ApplyEffective()
        {
            int v = Effective();
            if (v == appliedLevel) return;      // 无变化不重复写 EC
            try
            {
                if (!Ec.Opened && !Ec.Open()) { SetStatus("无法打开 EC 驱动 (TVicPort)"); return; }
                Ec.SetFan(v);
                appliedLevel = v;
            }
            catch (Exception ex) { SetStatus("写 EC 失败: " + ex.Message); appliedLevel = -1; }
        }

        void Tick()
        {
            try
            {
                if (!Ec.Opened && !Ec.Open()) { SetStatus("无法打开 EC 驱动 (TVicPort)"); return; }
                // 0xD2/0xC2 等并非温度：实测 0xC2 全程恒为 0x42(66)，是静态配置字节，勿当传感器
                // 无效读数过滤（EC 偶发 0x80 / 0xFFFF 哨兵）：保留上一有效值，避免假触发全速与假降档
                int cpuRaw = Ec.ReadEC(0x78), apsRaw = Ec.ReadEC(0x79), crdRaw = Ec.ReadEC(0x7A), batRaw = Ec.ReadEC(0x7C);
                int cpu = TempOk(cpuRaw) ? cpuRaw : lastCpuT;
                if (!TempOk(cpuRaw)) NoteBadRead("CPU " + cpuRaw + "°C");
                int aps = TempOk(apsRaw) ? apsRaw : lastApsT;
                if (!TempOk(apsRaw)) NoteBadRead("APS " + apsRaw + "°C");
                int crd = TempOk(crdRaw) ? crdRaw : lastCrdT;
                if (!TempOk(crdRaw)) NoteBadRead("CRD " + crdRaw + "°C");
                int bat = TempOk(batRaw) ? batRaw : lastBatT;
                if (!TempOk(batRaw)) NoteBadRead("BAT " + batRaw + "°C");
                lastCpuT = cpu; lastApsT = aps; lastCrdT = crd; lastBatT = bat;

                int rpmRaw = Ec.Rpm(0);
                int rpm = RpmOk(rpmRaw) ? rpmRaw : lastRpm;
                if (!RpmOk(rpmRaw)) NoteBadRead("RPM " + rpmRaw);
                lastRpm = rpm;
                int fc = Ec.FanCtrl;
                lblTemps.Text = string.Format("CPU {0,2}C  APS {1,2}C  CRD {2,2}C  BAT {3,2}C", cpu, aps, crd, bat);
                lblFan.Text = string.Format("风扇 {0,5} rpm", rpm);
                lblMode.Text = ModeText(fc);
                lastUtil = CpuBusy.Read();   // busy 占比（GetSystemTimes 差分）
                PowerStatus ps = SystemInformation.PowerStatus;
                bool onAc = ps.PowerLineStatus == PowerLineStatus.Online;
                // 统一门控：风扇与功率共用同一开关——AC 供电，或电池下打开 性能优先
                bool smartOn = smartActive && (onAc || cfg.PerfFirst);
                if (smartOn) SmartCtrl.Update(cpu, lastUtil, onAc, baseLevel == 0x40);
                else { SmartCtrl.Reset(); Pl1Pin.SetTarget(cfg.PinPl1MaxW, (onAc || cfg.PerfFirst) && cfg.PinPl1); }
                double pl1 = Pl1Pin.EffectiveW;
                double pinW = Pl1Pin.TargetW;
                int pinState = !Pl1Pin.Available ? 0 : (SmartCtrl.PowerReleased ? 2 : (Pl1Pin.PinActive ? 1 : 0));
                string pinTxt = "";
                if (!Pl1Pin.Available) pinTxt = " N/A";
                else if (!cfg.PinPl1) pinTxt = " OFF";
                else if (SmartCtrl.PowerReleased) pinTxt = " REL";
                else if (!Pl1Pin.PinActive) pinTxt = " IDLE";   // 电池供电
                else pinTxt = " PIN" + pinW.ToString("0") + "W";
                lblPower.Text = string.Format("PL1 {0,4:0.0}W{1}", pl1, pinTxt);
                lblPower.ForeColor = (pl1 > 0 && pl1 <= cfg.RescuePl1W && lastUtil >= cfg.RescueBusyMinPct) ? Color.Firebrick : Color.Black;
                ApplyEffective();          // 统一落 EC
                int st = StatusCode();
                if (cfg.DiagLog) DiagWrite(cpu, aps, crd, bat, rpm, fc, pl1, pinW, pinState, smartActive ? SmartCtrl.PowerLevel : 0, smartActive ? SmartCtrl.TempEma : cpu, lastUtil, appliedLevel == 0x40 ? 1 : 0, st);
                string stText;
                if (st == 0 && smartActive && !onAc && !cfg.PerfFirst) stText = "电池模式·智能控制暂停";
                else if (st == 1) stText = "MAX 全速";
                else if (st == 2) stText = "智能·风扇全速";
                else if (st == 3) stText = "高温·钉住已释放";
                else if (st == 4) stText = "兜底自救·全速";
                else stText = "智能控制中";
                SetStatus(stText);
                tray.Text = "TPFanLite  " + cpu + "C  " + rpm + "rpm  " + stText;
            }
            catch (Exception ex) { SetStatus("读取失败: " + ex.Message); }
        }

        // 控制状态码（写进 diag 日志，用于事后归因“为什么当时是全速”）
        // 0=智能正常 1=MAX 2=智能风扇全速 3=高温释放 4=兜底自救
        int StatusCode()
        {
            if (baseLevel == 0x40) return 1;
            if (!smartActive) return 0;
            return SmartCtrl.StatusCode;
        }

        // 1Hz 诊断日志（ini DiagLog=true）：复盘调度决策所需的完整现场
        // 列：time,ts,cpu,aps,crd,bat,rpm,fanctrl,pl1_w,pin_w,pin_state,pl_level,temp_ema,busy_pct,fan,base,applied,status,on_ac,battery_pct
        // pin_state: 0=未启用/不可用 1=钉住中 2=高温释放；fan: 1=全速；pl_level: 0..PowerSteps 阶梯级；temp_ema: 用于决策的平滑温度
        static DateTime lastDiagSec = DateTime.MinValue;
        void DiagWrite(int cpu, int aps, int crd, int bat, int rpm, int fc, double pl1, double pinW, int pinState, int level, double ema, double busy, int fanFull, int status)
        {
            try
            {
                DateTime now = DateTime.Now;
                if (now.Date == lastDiagSec.Date && now.Second == lastDiagSec.Second) return;   // 同一秒只记一条
                lastDiagSec = now;
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TPFanLite");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "diag-v4-" + now.ToString("yyyyMMdd") + ".csv");
                const string header = "time,ts,cpu,aps,crd,bat,rpm,fanctrl,pl1_w,pin_w,pin_state,pl_level,temp_ema,busy_pct,fan,base,applied,status,on_ac,battery_pct";
                bool needHeader = !File.Exists(file);
                PowerStatus ps = SystemInformation.PowerStatus;
                using (StreamWriter w = new StreamWriter(file, true, new UTF8Encoding(false)))
                {
                    if (needHeader) w.WriteLine(header);
                    w.WriteLine(string.Format("{0},{1},{2},{3},{4},{5},{6},0x{7:X2},{8},{9},{10},{11},{12:0},{13:F0},{14},0x{15:X2},0x{16:X2},{17},{18},{19}",
                        now.ToString("yyyy-MM-dd HH:mm:ss"),
                        DateTimeOffset.Now.ToUnixTimeSeconds(),
                        cpu, aps, crd, bat, rpm, fc,
                        pl1.ToString("0.0"),
                        pinW.ToString("0.0"),
                        pinState,
                        level,
                        double.IsNaN(ema) ? cpu : ema,
                        busy,
                        fanFull,
                        baseLevel,
                        appliedLevel,
                        status,
                        ps.PowerLineStatus == PowerLineStatus.Online ? 1 : 0,
                        (int)Math.Round(ps.BatteryLifePercent * 100)));
                }
            }
            catch { }
        }
        static string ModeText(int fc)
        {
            if (fc == 0x80) return "EC 自动";
            if (fc == 0x40) return "全速";
            if (fc >= 1 && fc <= 7) return "手动 " + fc + " 档";
            return "0x" + fc.ToString("X2");
        }

        void SetStatus(string s) { lblStatus.Text = s; }

        // ---- TVicPort 驱动：未安装时提示一键安装（提权子进程 --install-driver，弹一次 UAC）----
        void EnsureTvicPortDriver(bool manual)
        {
            if (!manual)
            {
                for (int i = 0; i < 3; i++) { if (Ec.Open()) return; Thread.Sleep(200); }
                if (MessageBox.Show("未检测到 TVicPort 驱动（风扇、温度、PL1 钉住都依赖它）。\r\n\r\n是否现在安装？需要管理员权限，会弹一次 UAC。",
                    "TPFanLite", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            }
            int rc = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--install-driver");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                using (Process p = Process.Start(psi)) { p.WaitForExit(90000); rc = p.ExitCode; }
            }
            catch (Exception ex)
            {
                MessageBox.Show("驱动安装被取消或失败：" + ex.Message, "TPFanLite", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            bool ok = false;
            for (int i = 0; i < 5 && !ok; i++) { ok = Ec.Open(); if (!ok) Thread.Sleep(300); }
            string detail = "";
            try { if (File.Exists(Program.InstallLogPath())) detail = "\r\n\r\n" + File.ReadAllText(Program.InstallLogPath()); } catch { }
            MessageBox.Show((ok ? "TVicPort 驱动已就绪。" : "驱动安装失败（rc=" + rc + "），可重试或手动处理。") + detail,
                "TPFanLite", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }

        // 卸载驱动：先释放本进程对驱动的占用（EC 句柄 + MCHBAR 映射），再提权执行卸载，成功后退出程序
        void UninstallTvicPortDriverUi()
        {
            if (MessageBox.Show("将停止并删除 TVicPort 驱动。\r\n卸载后风扇、温度、PL1 钉住都不可用（可从菜单重新安装恢复）。\r\n\r\n继续？",
                "TPFanLite", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { Pl1Pin.Stop(); } catch { }
            try { Ec.Close(); } catch { }
            int rc = -1;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--uninstall-driver");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                using (Process p = Process.Start(psi)) { p.WaitForExit(60000); rc = p.ExitCode; }
            }
            catch (Exception ex)
            {
                MessageBox.Show("卸载被取消或失败：" + ex.Message, "TPFanLite", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string detail = "";
            try { if (File.Exists(Program.InstallLogPath())) detail = "\r\n\r\n" + File.ReadAllText(Program.InstallLogPath()); } catch { }
            MessageBox.Show((rc == 0 ? "TVicPort 驱动已卸载，程序将退出。" : "卸载未完成（rc=" + rc + "），可重启后再试。") + detail,
                "TPFanLite", MessageBoxButtons.OK, rc == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            if (rc == 0) { exiting = true; Close(); }
        }

        // ---- 睡眠模式（BIOS SleepState）：Linux = S3；Windows 10 = 现代待机（重启生效）----
        void UpdateSleepChecks()
        {
            string cur = null;
            try { cur = Bios.Read("SleepState"); } catch { }
            if (cur == null)
            {
                mSleep.Text = "睡眠模式：未知";
                miSleepLinux.Checked = false;
                miSleepWin.Checked = false;
                return;
            }
            string v = cur.Contains(",") ? cur.Substring(cur.IndexOf(',') + 1) : cur;
            miSleepLinux.Checked = v.Equals("Linux", StringComparison.OrdinalIgnoreCase);
            miSleepWin.Checked = v.StartsWith("Windows", StringComparison.OrdinalIgnoreCase);
            mSleep.Text = "睡眠模式：" + (miSleepLinux.Checked ? "Linux (S3)" : miSleepWin.Checked ? "Windows 10" : v);
        }

        void SetSleepState(string value)
        {
            if (MessageBox.Show("修改 BIOS：睡眠模式 = " + value + "\r\n保存后需要重启电脑才生效。继续？", "TPFanLite",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            string log = Program.BiosLogPath();
            try { if (File.Exists(log)) File.Delete(log); } catch { }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--bios-set \"SleepState," + value + "\"");
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                using (Process p = Process.Start(psi)) { p.WaitForExit(30000); }
            }
            catch (Exception ex)
            {
                MessageBox.Show("操作失败: " + ex.Message, "TPFanLite", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string txt = File.Exists(log) ? File.ReadAllText(log) : "未收到操作结果（可能被拒绝或超时）。";
            MessageBox.Show(txt + "\r\n（重启后生效）", "TPFanLite", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---- 开机启动 (HKCU，无需管理员) ----
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "TPFanLite";

        /// <summary>
        /// 写入/删除 HKCU 启动项。先直接写注册表，若被 HIPS（火绒）按程序拦截，
        /// 则回退为委托系统签名的 reg.exe 代写（HIPS 一般按实际执行动作的进程判定）。
        /// 最后回读校验，任一环节失败返回 false。
        /// </summary>
        public static bool SetStartup(bool on)
        {
            StartupLog("SetStartup(" + on + ") called; exe=" + Application.ExecutablePath);
            // 1) 直接写
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --tray");
                    else k.DeleteValue(RunName, false);
                }
                if (VerifyStartup(on)) return true;
            }
            catch (Exception ex) { StartupLog("direct: " + ex.GetType().Name + ": " + ex.Message); }

            // 2) 回退：委托系统签名的 reg.exe 写入。
            //    注意：本程序可能被 HIPS 限制为只能写自己的目录（%TEMP% 会被拒），
            //    所以 .reg 临时文件优先放程序目录，其次 %TEMP%。
            try
            {
                string body = null;
                if (on)
                {
                    string exe = Application.ExecutablePath.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    body = "Windows Registry Editor Version 5.00\r\n\r\n" +
                        "[HKEY_CURRENT_USER\\" + RunKey + "]\r\n" +
                        "\"" + RunName + "\"=\"\\\"" + exe + "\\\" --tray\"\r\n";
                }
                string tmp = null;
                foreach (string d in new string[] { AppDomain.CurrentDomain.BaseDirectory, Path.GetTempPath() })
                {
                    try
                    {
                        string f = Path.Combine(d, "tpfanlite_startup.reg");
                        File.WriteAllText(f, body ?? "", Encoding.Unicode);
                        tmp = f;
                        break;
                    }
                    catch (Exception e2) { StartupLog("write .reg to " + d + " failed: " + e2.Message); }
                }
                try
                {
                    if (on)
                    {
                        if (tmp == null) { StartupLog("no writable dir for .reg"); return false; }
                        RunHidden("reg.exe", "import \"" + tmp + "\"");
                    }
                    else RunHidden("reg.exe", "delete \"HKCU\\" + RunKey + "\" /v " + RunName + " /f");
                }
                finally { if (tmp != null) { try { File.Delete(tmp); } catch { } } }
                bool ok = VerifyStartup(on);
                if (!ok) StartupLog("reg.exe fallback ran but verify failed");
                return ok;
            }
            catch (Exception ex) { StartupLog("reg fallback: " + ex.Message); return false; }
        }

        static bool VerifyStartup(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return (k != null && k.GetValue(RunName) != null) == on;
            }
            catch { return false; }
        }

        static void RunHidden(string file, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    string se = p.StandardError.ReadToEnd();
                    p.WaitForExit(5000);
                    StartupLog("run " + file + " exit=" + p.ExitCode + " out=[" + so.Trim() + "] err=[" + se.Trim() + "]");
                }
            }
            catch (Exception ex) { StartupLog("run " + file + ": " + ex.Message); }
        }

        static void StartupLog(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss  ") + msg + "\r\n";
            try { Console.WriteLine("[startup] " + msg); } catch { }
            string[] paths = new string[] {
                Path.Combine(Path.GetTempPath(), "tpfanlite-startup.log"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tpfanlite-startup.log")
            };
            foreach (string p in paths)
            {
                try { File.AppendAllText(p, line, Encoding.UTF8); } catch { }
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_POWERBROADCAST)
            {
                int ev = m.WParam.ToInt32();
                if (ev == PBT_APMSUSPEND)
                {
                    // 挂起前交还 EC 自动，避免全速跨越休眠；智能控制状态清空
                    SmartCtrl.Reset();
                    try { Ec.SetFan(0x80); appliedLevel = 0x80; } catch { appliedLevel = -1; }
                }
                else if (ev == PBT_APMRESUMEAUTOMATIC)
                {
                    // 唤醒后按当前状态重新判定
                    SmartCtrl.Reset();
                    appliedLevel = -1;
                    ApplyEffective();
                }
            }
            base.WndProc(ref m);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && cfg != null && cfg.MinimizeToTray && tray != null)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    Hide();
                    ShowInTaskbar = false;
                });
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing && cfg.CloseToTray)
            {
                e.Cancel = true;
                Persist();
                Hide();
                ShowInTaskbar = false;
                return;
            }
            cfg.Mode = baseLevel;
            cfg.WinX = Location.X;
            cfg.WinY = Location.Y;
            cfg.Save();
            try { Pl1Pin.Stop(); } catch { }     // 停止重写，固件 <200ms 内自行接管
            try { Ec.SetFan(0x80); } catch { }   // 真正退出时交还 EC 自动
            Ec.Close();
            tray.Visible = false;
            tray.Dispose();
            poll.Stop();
            base.OnFormClosing(e);
        }
    }
}
