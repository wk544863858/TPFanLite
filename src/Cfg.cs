using System;
using System.IO;
using System.Text;

namespace TPFanLite
{
    // 配置：ini 读写 + 参数默认值
    // 2026-10-20 参数收敛：温度只留「起点 / 高点 / 档位 / 回差 / 稳定」五项，其余全部派生：
    //   风扇全速     : EMA ≥ 起点，持续 稳定
    //   风扇回自动   : EMA ≤ 起点 − 回差，持续 稳定
    //   功率阶梯     : 起点 → 高点 等分「档位」级（末级 = PinPl1MinW）；阶梯内部回差固定 0.5/1.5（不随回差变）
    //   暂停钉住     : EMA ≥ 高点，持续 稳定（交回固件/EC 自动）
    //   重新接管     : EMA ≤ 高点 − 回差，持续 稳定
    public class Cfg
    {
        public string FilePath;

        public int Mode = 0x80;        // 0x80=自动（智能控制） 0x40=全速；旧 manual1..7 载入时迁移为 auto

        // ---- 智能控制：风扇 + 功率统一调度（SmartCtrl）----
        // 原则：风扇先行（提前全速，只有噪音成本），功率后收（风扇顶不住才降 PL1）。
        public bool SmartEnabled = true;          // 智能控制总开关
        public double TempStart = 70.0;           // 起点：风扇全速 + 功率阶梯共同起点
        public double TempHyst = 5.0;             // 回差：风扇释放 = 起点−回差；重新接管 = 高点−回差
        public int HoldMs = 5000;                 // 稳定：所有温控确认时长（风扇升降/档位切换/释放/接管）
        public double TempPowerHigh = 98.0;       // 高点：阶梯末级（MinW）+ 暂停钉住点
        public int PowerSteps = 3;                // 阶梯挡位数（1..8）
        public double PowerStepDownC = 0.5;       // 阶梯内部回差（固定，不随「回差」变）
        public double PowerStepUpC = 1.5;         // 阶梯内部回差（固定，不随「回差」变）
        public bool PinPl1 = true;                // 功率钉住总开关（默认仅 AC 生效）
        public bool PerfFirst = false;            // 性能优先：电池供电时也钉住
        public double PinPl1MaxW = 35.0;          // 低温目标 PL1
        public double PinPl1MinW = 20.0;          // 高温下限（≈2.5GHz，远离 0.4-0.8G 地板）

        // ---- 派生值（不落 ini，由上面五项算出；旧键 tempfanrelease/pintemprelease/… 已废弃）----
        public double TempFanRelease { get { return TempStart - TempHyst; } }        // 风扇回自动温度
        public double PinTempRelease { get { return TempPowerHigh; } }               // 暂停钉住温度
        public double PinTempReengage { get { return TempPowerHigh - TempHyst; } }   // 重新接管温度
        public int FanEngageDwellMs { get { return HoldMs; } }
        public int PinReleaseDwellMs { get { return HoldMs; } }
        public int PinReengageDwellMs { get { return HoldMs; } }
        public int PowerStepDwellMs { get { return HoldMs; } }

        // ---- 兜底自救（钉住失效/关闭时：PL1 被压 + CPU 忙 → 全速风扇加速解封）----
        public bool RescueEnabled = true;
        public double RescuePl1W = 18.0;
        public int RescuePl1DwellMs = 5000;
        public double RescueBusyMinPct = 25.0;
        public double RescueExitPl1W = 28.0;
        public int RescueExitDwellMs = 12000;
        public double RescueExitBusyPct = 20.0;

        public bool CloseToTray = true;         // X 关窗：true=到托盘 false=真退出
        public bool MinimizeToTray = true;      // – 最小化：true=到托盘
        public bool AlwaysOnTop = true;         // 窗口始终置顶（TopMost）
        public bool DiagLog = false;            // 1Hz 诊断日志（%LOCALAPPDATA%\TPFanLite\diag-v4-YYYYMMDD.csv）
        public bool StartWithWindows = false;
        public int WinX = int.MinValue;
        public int WinY = int.MinValue;

        public Cfg(string path)
        {
            FilePath = path;
            Load();
        }

        static int ClampI(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
        static double ClampD(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                bool sawHold = false;
                foreach (string raw in File.ReadAllLines(FilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    int n; bool b; double d;
                    switch (k)
                    {
                        case "mode":
                            if (v.Equals("full", StringComparison.OrdinalIgnoreCase)) Mode = 0x40;
                            else Mode = 0x80;    // auto / 旧 manual1..7 / 其他一律按自动
                            break;

                        // ---- 智能控制：五项 ----
                        case "smartenabled": if (bool.TryParse(v, out b)) SmartEnabled = b; break;
                        case "tempstart": if (double.TryParse(v, out d)) TempStart = ClampD(d, 40, 95); break;
                        case "tempfanfull": if (double.TryParse(v, out d)) TempStart = ClampD(d, 40, 95); break;   // 旧键兼容
                        case "temphyst": if (double.TryParse(v, out d)) TempHyst = ClampD(d, 2, 10); break;
                        case "holdms":
                            if (int.TryParse(v, out n)) { HoldMs = ClampI(n, 1000, 60000); sawHold = true; }
                            break;
                        case "powerstepdwellms":   // 旧键：并入 HoldMs
                            if (!sawHold && int.TryParse(v, out n)) HoldMs = ClampI(n, 1000, 60000);
                            break;
                        case "temppowerhigh": if (double.TryParse(v, out d)) TempPowerHigh = ClampD(d, 60, 105); break;
                        case "powersteps": if (int.TryParse(v, out n)) PowerSteps = ClampI(n, 1, 8); break;
                        // 旧键 tempfanrelease / pintemprelease / pintempreengage / fanengagedwellms /
                        //      pinreleasedwellms / pinreengagedwellms / powerstepdownc / powerstepupc 已并入派生值，忽略

                        // ---- 功率钉住 ----
                        case "perffirst": if (bool.TryParse(v, out b)) PerfFirst = b; break;
                        case "pinpl1": if (bool.TryParse(v, out b)) PinPl1 = b; break;
                        case "pinpl1maxw": if (double.TryParse(v, out d)) PinPl1MaxW = ClampD(d, 15, 45); break;
                        case "pinpl1minw": if (double.TryParse(v, out d)) PinPl1MinW = ClampD(d, 10, 35); break;

                        // ---- 兜底自救 ----
                        case "rescueenabled": if (bool.TryParse(v, out b)) RescueEnabled = b; break;
                        case "pl1rescue": if (bool.TryParse(v, out b)) RescueEnabled = b; break;   // 旧键兼容
                        case "rescuepl1w": if (double.TryParse(v, out d)) RescuePl1W = ClampD(d, 5, 40); break;
                        case "rescuepl1dwellms": if (int.TryParse(v, out n)) RescuePl1DwellMs = ClampI(n, 1000, 60000); break;
                        case "rescuebusyminpct": if (double.TryParse(v, out d)) RescueBusyMinPct = ClampD(d, 1, 100); break;
                        case "rescueexitpl1w": if (double.TryParse(v, out d)) RescueExitPl1W = ClampD(d, 10, 60); break;
                        case "rescueexitdwellms": if (int.TryParse(v, out n)) RescueExitDwellMs = ClampI(n, 1000, 120000); break;
                        case "rescueexitbusypct": if (double.TryParse(v, out d)) RescueExitBusyPct = ClampD(d, 0, 100); break;

                        case "closetotray": if (bool.TryParse(v, out b)) CloseToTray = b; break;
                        case "minimizetotray": if (bool.TryParse(v, out b)) MinimizeToTray = b; break;
                        case "alwaysontop": if (bool.TryParse(v, out b)) AlwaysOnTop = b; break;
                        case "diaglog": if (bool.TryParse(v, out b)) DiagLog = b; break;
                        case "startwithwindows": if (bool.TryParse(v, out b)) StartWithWindows = b; break;
                        case "winx": if (int.TryParse(v, out n)) WinX = n; break;
                        case "winy": if (int.TryParse(v, out n)) WinY = n; break;
                        // 旧键（智能温控/预热/手动档）已废弃，忽略
                    }
                }

                // 参数自洽：回差不能吃掉整个带（高点−回差 必须高于 起点）
                double maxHyst = TempPowerHigh - TempStart - 5;
                if (maxHyst < 2) maxHyst = 2;
                if (TempHyst > maxHyst) TempHyst = maxHyst;
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("; ===== TPFanLite 配置文件 =====");
                sb.AppendLine("; 程序自动维护，可手工编辑；保存后重启程序生效");
                sb.AppendLine("; ---- 模式 ----");
                sb.AppendLine("; Mode = auto（智能控制：风扇+功率统一调度） | full（MAX 全速）");
                sb.AppendLine("Mode=" + (Mode == 0x40 ? "full" : "auto"));
                sb.AppendLine("; ---- 智能控制：温度只看五项 ----");
                sb.AppendLine("; 起点：EMA>=起点 持续 稳定 → 风扇全速（同时是功率阶梯起点）");
                sb.AppendLine("; 回差：风扇 EMA<=起点-回差 持续 稳定 → 回 EC 自动；钉住 EMA<=高点-回差 持续 稳定 → 重新接管");
                sb.AppendLine("; 稳定：所有温控确认时长（风扇升降 / 档位切换 / 释放 / 接管）");
                sb.AppendLine("; 高点：EMA>=高点 持续 稳定 → 暂停钉住交回固件；阶梯末级=下限");
                sb.AppendLine("SmartEnabled=" + SmartEnabled);
                sb.AppendLine("TempStart=" + TempStart.ToString("0"));
                sb.AppendLine("TempHyst=" + TempHyst.ToString("0"));
                sb.AppendLine("HoldMs=" + HoldMs);
                sb.AppendLine("TempPowerHigh=" + TempPowerHigh.ToString("0"));
                sb.AppendLine("PowerSteps=" + PowerSteps);
                sb.AppendLine("; ---- 功率钉住 ----");
                sb.AppendLine("PinPl1=" + PinPl1);
                sb.AppendLine("PerfFirst=" + PerfFirst);
                sb.AppendLine("PinPl1MaxW=" + PinPl1MaxW.ToString("0.0"));
                sb.AppendLine("PinPl1MinW=" + PinPl1MinW.ToString("0.0"));
                sb.AppendLine("; ---- 兜底自救（钉住失效时全速风扇加速解封）----");
                sb.AppendLine("RescueEnabled=" + RescueEnabled);
                sb.AppendLine("RescuePl1W=" + RescuePl1W.ToString("0.0"));
                sb.AppendLine("RescuePl1DwellMs=" + RescuePl1DwellMs);
                sb.AppendLine("RescueBusyMinPct=" + RescueBusyMinPct.ToString("0"));
                sb.AppendLine("RescueExitPl1W=" + RescueExitPl1W.ToString("0.0"));
                sb.AppendLine("RescueExitDwellMs=" + RescueExitDwellMs);
                sb.AppendLine("RescueExitBusyPct=" + RescueExitBusyPct.ToString("0"));
                sb.AppendLine("; ---- 其它 ----");
                sb.AppendLine("; CloseToTray   : X 关窗时 true=最小化到托盘 / false=真正退出");
                sb.AppendLine("; MinimizeToTray: – 最小化时 true=收到托盘");
                sb.AppendLine("CloseToTray=" + CloseToTray);
                sb.AppendLine("MinimizeToTray=" + MinimizeToTray);
                sb.AppendLine("; AlwaysOnTop   : true=窗口始终置顶（最前端）/ false=普通窗口");
                sb.AppendLine("AlwaysOnTop=" + AlwaysOnTop);
                sb.AppendLine("; DiagLog       : true=每秒写诊断日志 %LOCALAPPDATA%\\TPFanLite\\diag-v4-YYYYMMDD.csv");
                sb.AppendLine("DiagLog=" + DiagLog);
                sb.AppendLine("StartWithWindows=" + StartWithWindows);
                sb.AppendLine("WinX=" + (WinX == int.MinValue ? "" : WinX.ToString()));
                sb.AppendLine("WinY=" + (WinY == int.MinValue ? "" : WinY.ToString()));
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
