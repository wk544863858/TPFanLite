// ============ 智能控制：统一调度「风扇 + 功率」 ============
// 四个机制合并为一个状态机（温度只认五项：起点/高点/档位/回差/稳定）：
//   1) 风扇先行：EMA≥起点 持续「稳定」→ 拉全速（爬升约 20s，提前拉才能压住峰值），代价只有噪音；
//   2) 功率后收：EMA 继续上升时按「阶梯」分级下压 PL1（N 档，内部回差固定 0.5/1.5）；
//   3) 极端保护：EMA≥高点 持续「稳定」→ 暂停钉住交回固件；EMA≤高点−回差 持续「稳定」→ 重新接管；
//   4) 兜底自救：钉住失效（实测 PL1 掉到低位）且 CPU 忙 → 全速风扇加速解封。
using System;
using System.IO;

namespace TPFanLite
{
    static class SmartCtrl
    {
        static Cfg cfg;

        // ---- 风扇状态 ----
        static double emaTemp = double.NaN;
        static bool fanFull;
        static DateTime fanEngageSince = DateTime.MinValue;
        static DateTime fanRelSince = DateTime.MinValue;

        // ---- 功率阶梯状态 ----
        static int powerLevel;                     // 0=MaxW ... PowerSteps=MinW
        static DateTime stepSince = DateTime.MinValue;

        // ---- 释放 / 接管 / 自救 ----
        static bool powerReleased;
        static bool rescueOn;
        static DateTime relSince = DateTime.MinValue;
        static DateTime reengSince = DateTime.MinValue;
        static DateTime resLowSince = DateTime.MinValue;
        static DateTime resHighSince = DateTime.MinValue;

        public static bool FanFull { get { return fanFull; } }
        public static bool PowerReleased { get { return powerReleased; } }
        public static double Pl1TargetW { get; private set; }
        public static double TempEma { get { return emaTemp; } }
        public static int PowerLevel { get { return powerLevel; } }

        /// <summary>0=正常 1=MAX 2=智能全速 3=高温释放 4=兜底自救</summary>
        public static int StatusCode
        {
            get
            {
                if (powerReleased) return 3;
                if (rescueOn) return 4;
                if (fanFull) return 2;
                return 0;
            }
        }

        public static void Configure(Cfg c)
        {
            cfg = c;
            Reset();
            Pl1TargetW = cfg.PinPl1MaxW;
            LogCfg("启动");
        }

        /// <summary>配置快照：阈值随时可改，写进 smart.log 才能在事后复盘时还原当时的参数</summary>
        public static void LogCfg(string tag)
        {
            if (cfg == null) return;
            LogRaw(string.Format("[{0}] 参数 起点={1:0}°C 高点={2:0}°C 档位={3} 回差={4:0}°C 稳定={5:0}s 上限={6:0.#}W 下限={7:0.#}W（风扇 {1:0}↔{8:0}，钉住 {2:0}↔{9:0}）",
                tag, cfg.TempStart, cfg.TempPowerHigh, cfg.PowerSteps, cfg.TempHyst, cfg.HoldMs / 1000.0,
                cfg.PinPl1MaxW, cfg.PinPl1MinW,
                cfg.TempStart - cfg.TempHyst, cfg.TempPowerHigh - cfg.TempHyst));
        }

        public static void Reset()
        {
            emaTemp = double.NaN;
            fanFull = false;
            powerReleased = false;
            rescueOn = false;
            powerLevel = 0;
            Pl1TargetW = (cfg != null) ? cfg.PinPl1MaxW : 35;
            fanEngageSince = fanRelSince = stepSince = DateTime.MinValue;
            relSince = reengSince = resLowSince = resHighSince = DateTime.MinValue;
        }

        /// <summary>每秒调用：输入温度/忙闲/供电，输出风扇状态与功率目标（直接下发 Pl1Pin）。</summary>
        public static void Update(int cpuTemp, double busy, bool onAc, bool maxMode)
        {
            if (cfg == null) return;
            DateTime now = DateTime.Now;
            emaTemp = double.IsNaN(emaTemp) ? cpuTemp : (emaTemp * 0.6 + cpuTemp * 0.4);

            // ---- 1. 风扇：提前全速（MAX 档恒全速；进入需持续确认，防忙闲抖动反复起停）----
            // 只看温度：过全速起点就拉全速（超过低点即启动），不看忙闲
            bool wantFan = maxMode || emaTemp >= cfg.TempStart;
            if (wantFan && !fanFull)
            {
                if (fanEngageSince == DateTime.MinValue) fanEngageSince = now;
                else if ((now - fanEngageSince).TotalMilliseconds >= cfg.FanEngageDwellMs)
                { fanFull = true; fanEngageSince = DateTime.MinValue; fanRelSince = DateTime.MinValue; Log("风扇全速 ON"); }
            }
            else if (!wantFan)
            {
                fanEngageSince = DateTime.MinValue;
                if (fanFull)
                {
                    // 释放回差：温度降到 TempFanRelease 以下并持续 2s（纯温度，避免与 busy 抖动共振）
                    if (emaTemp <= cfg.TempFanRelease)
                    {
                        if (fanRelSince == DateTime.MinValue) fanRelSince = now;
                        else if ((now - fanRelSince).TotalMilliseconds >= cfg.HoldMs) { fanFull = false; fanRelSince = DateTime.MinValue; Log("风扇全速 OFF"); }
                    }
                    else fanRelSince = DateTime.MinValue;
                }
            }
            else fanEngageSince = DateTime.MinValue;

            // ---- 2. 功率阶梯：温度越低功率越高；级间带温度回差 + 时间确认 ----
            //    暂停钉住期间冻结阶梯（重新接管时从末级 20W 起步，抑制「高点↔接管点」来回循环）
            if (!powerReleased)
            {
                int want = RawLevel(emaTemp);
                int cand = powerLevel;
                if (want > powerLevel)
                {
                    // 降档（功率变小）：需超过目标级边界 DownC 度
                    if (emaTemp >= BoundaryTemp(want) + cfg.PowerStepDownC) cand = want;
                }
                else if (want < powerLevel)
                {
                    // 回档（功率变大）：需低于当前级边界 UpC 度
                    if (emaTemp <= BoundaryTemp(powerLevel) - cfg.PowerStepUpC) cand = want;
                }
                if (cand != powerLevel)
                {
                    if (stepSince == DateTime.MinValue) stepSince = now;
                    else if ((now - stepSince).TotalMilliseconds >= cfg.PowerStepDwellMs)
                    {
                        powerLevel = cand; stepSince = DateTime.MinValue;
                        Log(string.Format("功率阶梯 → {0} 级（{1:0}W）", powerLevel, LevelToW(powerLevel)));
                    }
                }
                else stepSince = DateTime.MinValue;
            }
            Pl1TargetW = powerReleased ? cfg.PinPl1MinW : LevelToW(powerLevel);

            // ---- 3. 极端保护：温度撞释放点 → 暂停钉住；降到接管点 → 重新接管 ----
            if (powerReleased)
            {
                if (emaTemp <= cfg.PinTempReengage)
                {
                    if (reengSince == DateTime.MinValue) reengSince = now;
                    else if ((now - reengSince).TotalMilliseconds >= cfg.PinReengageDwellMs)
                    { powerReleased = false; reengSince = DateTime.MinValue; Log("重新接管功率"); }
                }
                else reengSince = DateTime.MinValue;
            }
            else
            {
                if (emaTemp >= cfg.PinTempRelease)
                {
                    if (relSince == DateTime.MinValue) relSince = now;
                    else if ((now - relSince).TotalMilliseconds >= cfg.PinReleaseDwellMs)
                    { powerReleased = true; relSince = DateTime.MinValue; Log("温度过高，释放钉住"); }
                }
                else relSince = DateTime.MinValue;
            }

            // ---- 4. 兜底自救：钉住失效 + CPU 忙 → 全速风扇加速解封 ----
            // 性能优先：电池下也照常钉住（由托盘「性能优先」开关控制）
            bool pinWorks = (onAc || cfg.PerfFirst) && cfg.PinPl1 && !powerReleased;
            double effPl1 = pinWorks ? Pl1TargetW : Pl1Pin.ActualW;
            if (cfg.RescueEnabled && !maxMode && !rescueOn && !pinWorks
                && effPl1 > 0 && effPl1 <= cfg.RescuePl1W && busy >= cfg.RescueBusyMinPct)
            {
                if (resLowSince == DateTime.MinValue) resLowSince = now;
                else if ((now - resLowSince).TotalMilliseconds >= cfg.RescuePl1DwellMs)
                { rescueOn = true; resLowSince = DateTime.MinValue; Log("兜底自救 ON（PL1 " + effPl1.ToString("0.0") + "W）"); }
            }
            else if (!(effPl1 > 0 && effPl1 <= cfg.RescuePl1W && busy >= cfg.RescueBusyMinPct))
                resLowSince = DateTime.MinValue;

            if (rescueOn)
            {
                if (busy < 5) { rescueOn = false; Log("兜底自救 OFF（负载散去）"); }
                else if (effPl1 >= cfg.RescueExitPl1W)
                {
                    if (resHighSince == DateTime.MinValue) resHighSince = now;
                    else if ((now - resHighSince).TotalMilliseconds >= cfg.RescueExitDwellMs)
                    { rescueOn = false; resHighSince = DateTime.MinValue; Log("兜底自救 OFF（PL1 恢复）"); }
                }
                else if (busy < cfg.RescueExitBusyPct) { rescueOn = false; Log("兜底自救 OFF（负载低）"); }
                else resHighSince = DateTime.MinValue;
                if (rescueOn) fanFull = true;   // 自救期间强制全速
            }

            // ---- 下发执行器 ----
            Pl1Pin.SetTarget(Pl1TargetW, pinWorks);
        }

        // 温度 → 阶梯序号：0 = 最高功率；PowerSteps = 最低功率
        static int RawLevel(double t)
        {
            double span = cfg.TempPowerHigh - cfg.TempStart;
            if (span <= 0) return cfg.PowerSteps;
            if (t <= cfg.TempStart) return 0;
            if (t >= cfg.TempPowerHigh) return cfg.PowerSteps;
            int k = (int)Math.Floor((t - cfg.TempStart) / span * cfg.PowerSteps);
            if (k < 0) k = 0;
            if (k > cfg.PowerSteps) k = cfg.PowerSteps;
            return k;
        }

        // 第 k 级的下边界温度（k>=1 有效）
        static double BoundaryTemp(int k)
        {
            if (k <= 0) return cfg.TempStart;
            if (k >= cfg.PowerSteps) return cfg.TempPowerHigh;
            return cfg.TempStart + (cfg.TempPowerHigh - cfg.TempStart) * k / cfg.PowerSteps;
        }

        // 第 k 级对应的 PL1（0=MaxW，PowerSteps=MinW）
        static double LevelToW(int k)
        {
            if (k < 0) k = 0;
            if (k > cfg.PowerSteps) k = cfg.PowerSteps;
            if (cfg.PowerSteps <= 0) return cfg.PinPl1MinW;
            double w = cfg.PinPl1MaxW - (cfg.PinPl1MaxW - cfg.PinPl1MinW) * k / cfg.PowerSteps;
            if (w < cfg.PinPl1MinW) w = cfg.PinPl1MinW;
            if (w > cfg.PinPl1MaxW) w = cfg.PinPl1MaxW;
            return w;
        }

        /// <summary>外部（菜单/UI）事件也写进 smart.log，保持同一时间线</summary>
        public static void Note(string msg) { LogRaw(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss  ") + msg); }

        static void Log(string msg)
        {
            LogRaw(msg + "（温度 " + (double.IsNaN(emaTemp) ? 0 : emaTemp).ToString("0") + "°C，目标 " + Pl1TargetW.ToString("0") + "W）");
        }

        static void LogRaw(string msg)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TPFanLite");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "smart.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss  ") + msg + "\r\n");
            }
            catch { }
        }
    }
}
