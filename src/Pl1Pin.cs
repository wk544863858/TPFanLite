// ============ PL1 钉住执行器：TVicPort 映射 MCHBAR，直接写 RAPL PKG_POWER_LIMIT ============
// 背景（2026-10-19 实测）：EC 固件以约 0.1W/s 把动态 PL1（MCHBAR+0x59A0 bits[14:0]，1/8W）
// 从 35W 磨到 8W，且外部写入后 <200ms 内会被其衰减值覆盖；每 100ms 重写可稳定压住。
// 本类只做"执行"：目标值由 SmartCtrl 每秒计算后通过 SetTarget 下发。
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace TPFanLite
{
    static class Pl1Pin
    {
        [DllImport("TVicPort.dll", CallingConvention = CallingConvention.StdCall)]
        static extern IntPtr MapPhysToLinear(uint PhysAddr, uint Size);
        [DllImport("TVicPort.dll", CallingConvention = CallingConvention.StdCall)]
        static extern bool UnmapMemory(IntPtr LinearAddr);

        const uint MchbarBase = 0xFEDC0000;   // TGL-U MCHBAR 固定地址（BIOS 1.71 DSDT 声明 + 实测）
        const int OffPl1 = 0x59A0;            // PPL1[14:0] / PL1E[15] / CLP1[16]
        const int OffPl2 = 0x59A4;            // PL2 参考值（自检用）
        const int MapSize = 0x10000;          // 必须 ≥64KB：4KB 映射会读到错误数据（实测）
        const int WriteMs = 100;              // 重写周期（固件 <200ms 覆盖，100ms 可压住）

        static IntPtr map = IntPtr.Zero;
        static Thread th;
        static volatile bool stop;
        static readonly object gate = new object();

        static bool available;
        static bool enabled = true;      // ini PinPl1
        static bool pinActive;           // SmartCtrl 决定当前是否写入（AC 且未释放）
        static double actualW, targetW;
        static string lastErr = "";

        public static bool Available { get { return available; } }
        public static bool Enabled { get { lock (gate) return enabled; } set { lock (gate) enabled = value; } }
        public static bool PinActive { get { lock (gate) return pinActive; } }
        public static double TargetW { get { lock (gate) return targetW; } }
        public static double ActualW { get { lock (gate) return actualW; } }
        public static string LastError { get { lock (gate) return lastErr; } }
        /// <summary>当前实际生效的 PL1（钉住中=目标值，否则=固件值）。</summary>
        public static double EffectiveW { get { lock (gate) return pinActive ? targetW : actualW; } }

        public static void Configure(Cfg cfg)
        {
            lock (gate) enabled = cfg.PinPl1;
        }

        /// <summary>SmartCtrl 每秒下发：目标瓦数与是否写入（AC + 未释放 + 开关打开）。</summary>
        public static void SetTarget(double w, bool active)
        {
            lock (gate)
            {
                if (w < 8) w = 8;
                if (w > 45) w = 45;
                targetW = w;
                pinActive = active;
            }
        }

        /// <summary>映射 MCHBAR 并自检；失败则禁用（不影响其它功能）。</summary>
        public static bool Start()
        {
            if (map != IntPtr.Zero) return available;
            try
            {
                if (!Ec.Opened) Ec.Open();   // MapPhysToLinear 需要 TVicPort 驱动已打开
                map = MapPhysToLinear(MchbarBase, MapSize);
                if (map == IntPtr.Zero) { lastErr = "MCHBAR 映射失败（TVicPort 驱动不可用？）"; Log("FAIL " + lastErr); return false; }
                uint pl1 = (uint)Marshal.ReadInt32(map, OffPl1);
                uint pl2 = (uint)Marshal.ReadInt32(map, OffPl2);
                int r1 = (int)(pl1 & 0x7FFF), r2 = (int)(pl2 & 0x7FFF);
                if (r1 < 8 || r1 > 2048 || r2 < 64 || r2 > 2048)
                {
                    lastErr = string.Format("寄存器自检失败 PL1raw={0} PL2raw={1}（BIOS 布局变化？）", r1, r2);
                    Log("FAIL " + lastErr);
                    UnmapMemory(map); map = IntPtr.Zero;
                    return false;
                }
                available = true;
                Log(string.Format("在线 MCHBAR=0x{0:X} PL1={1:0.00}W PL2={2:0.00}W", MchbarBase, r1 / 8.0, r2 / 8.0));
                stop = false;
                th = new Thread(Loop);
                th.IsBackground = true;
                th.Start();
                return true;
            }
            catch (Exception ex) { lastErr = ex.Message; Log("EX " + ex); return false; }
        }

        public static void Stop()
        {
            stop = true;
            try { if (th != null) th.Join(500); } catch { }
            try { if (map != IntPtr.Zero) { UnmapMemory(map); map = IntPtr.Zero; } } catch { }
            available = false;
        }

        // 100ms 一轮：先读固件当前值（诊断用），再写我们的目标
        static void Loop()
        {
            while (!stop)
            {
                try
                {
                    double tgt; bool active;
                    lock (gate) { tgt = targetW; active = available && enabled && pinActive; }
                    double act = ReadW();
                    if (active) WriteW(tgt);
                    lock (gate) { actualW = act; }
                }
                catch (Exception ex)
                {
                    lock (gate) lastErr = ex.Message;
                    Log("EX " + ex.Message);
                }
                Thread.Sleep(WriteMs);
            }
        }

        static double ReadW()
        {
            uint v = (uint)Marshal.ReadInt32(map, OffPl1);
            return (v & 0x7FFFu) / 8.0;
        }

        static void WriteW(double w)
        {
            int raw = (int)Math.Round(w * 8.0);
            if (raw < 8) raw = 8;
            if (raw > 2047) raw = 2047;
            uint old = (uint)Marshal.ReadInt32(map, OffPl1);
            uint nv = (old & 0xFFFF0000u) | (uint)(raw | 0x8000);   // 保留高位，置 PL1E=1
            Marshal.WriteInt32(map, OffPl1, (int)nv);
        }

        static void Log(string msg)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TPFanLite");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "pl1pin.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss  ") + msg + "\r\n");
            }
            catch { }
        }
    }
}
