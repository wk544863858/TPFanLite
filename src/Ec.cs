
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;


namespace TPFanLite
{
    // ============ EC 访问层（复用 TPFanControl 的 TVicPort 驱动） ============
    static class Ec
    {
        [DllImport("TVicPort.dll", CallingConvention = CallingConvention.StdCall)] static extern bool OpenTVicPort();
        [DllImport("TVicPort.dll", CallingConvention = CallingConvention.StdCall)] static extern void CloseTVicPort();
        [DllImport("TVicPort.dll", CallingConvention = CallingConvention.StdCall)] static extern bool IsDriverOpened();
        [DllImport("TVicPort.dll", CallingConvention = CallingConvention.StdCall)] static extern byte ReadPort(ushort port);
        [DllImport("TVicPort.dll", CallingConvention = CallingConvention.StdCall)] static extern void WritePort(ushort port, byte val);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string path);

        const ushort CTRL = 0x1604, DATA = 0x1600;
        const byte IBF = 0x02, OBF = 0x01;

        public static bool Opened = false;

        static bool WaitFlags(int port, int flags, bool want, int timeoutMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                byte d = ReadPort((ushort)port);
                if (((d & flags) != 0) == want) return true;
                Thread.Sleep(2);
            }
            return false;
        }

        static void Drain()
        {
            int guard = 0;
            while (guard++ < 8)
            {
                byte d = ReadPort(CTRL);
                if ((d & OBF) == 0) break;
                ReadPort(DATA);
                Thread.Sleep(2);
            }
        }

        public static int ReadEC(int offset)
        {
            Drain();
            if (!WaitFlags(CTRL, IBF, false, 800)) throw new Exception("EC读超时#1");
            WritePort(CTRL, 0x80);
            if (!WaitFlags(CTRL, IBF, false, 800)) throw new Exception("EC读超时#2");
            WritePort(DATA, (byte)offset);
            if (!WaitFlags(CTRL, IBF, false, 800)) throw new Exception("EC读超时#3");
            if (!WaitFlags(CTRL, OBF, true, 800)) throw new Exception("EC读超时#4(数据未就绪)");
            return ReadPort(DATA);
        }

        public static void WriteEC(int offset, byte value)
        {
            Drain();
            if (!WaitFlags(CTRL, IBF, false, 800)) throw new Exception("EC写超时#1");
            WritePort(CTRL, 0x81);
            if (!WaitFlags(CTRL, IBF, false, 800)) throw new Exception("EC写超时#2");
            WritePort(DATA, (byte)offset);
            if (!WaitFlags(CTRL, IBF, false, 800)) throw new Exception("EC写超时#3");
            WritePort(DATA, value);
            if (!WaitFlags(CTRL, IBF, false, 800)) throw new Exception("EC写超时#4");
            Thread.Sleep(5);
            Drain();
        }

        public static bool Open()
        {
            try
            {
                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TVicPort.dll");
                if (File.Exists(local)) LoadLibrary(local);
                else if (File.Exists(@"C:\Windows\System\TVicPort.dll")) LoadLibrary(@"C:\Windows\System\TVicPort.dll");
                Opened = OpenTVicPort();
                return Opened;
            }
            catch { return false; }
        }

        public static void Close()
        {
            try { if (Opened) CloseTVicPort(); } catch { }
            Opened = false;
        }

        public static int Rpm(int fan)
        {
            WriteEC(0x31, (byte)fan);
            Thread.Sleep(25);
            int lo = ReadEC(0x84), hi = ReadEC(0x85);
            return (hi << 8) | lo;
        }

        public static int FanCtrl { get { return ReadEC(0x2F); } }
        public static void SetFan(int v) { WriteEC(0x2F, (byte)v); }

        public static bool DriverOpened()
        {
            try { return IsDriverOpened(); } catch { return false; }
        }

        public static string PortDiag()
        {
            StringBuilder s = new StringBuilder();
            for (int i = 0; i < 8; i++)
            {
                try { s.Append("0x" + ReadPort(CTRL).ToString("X2") + " "); }
                catch (Exception e) { s.Append("ERR(" + e.Message + ") "); }
                Thread.Sleep(100);
            }
            return s.ToString();
        }
    }
}
