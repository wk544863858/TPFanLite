// ============ CPU 忙闲占比（GetSystemTimes 差分，零驱动依赖） ============
// 本机 PDH "Processor Information" 计数器不可靠（% Processor Performance 钳制态恒 100%、
// % Processor Time 恒 0），故改用 kernel32 原生 API 两次差分。
using System;
using System.Runtime.InteropServices;

namespace TPFanLite
{
    static class CpuBusy
    {
        [DllImport("kernel32.dll")]
        static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        static long lastIdle = -1, lastKernel, lastUser;
        static double lastBusy = -1;

        /// <summary>全系统 busy 占比（%）。首次调用返回 -1。</summary>
        public static double Read()
        {
            long idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) return lastBusy;
            if (lastIdle >= 0)
            {
                long di = idle - lastIdle;
                long dt = (kernel - lastKernel) + (user - lastUser);   // kernel 时长含 idle
                if (dt > 0)
                {
                    double busy = 100.0 * (1.0 - (double)di / (double)dt);
                    if (busy >= 0 && busy <= 100) lastBusy = busy;
                }
            }
            lastIdle = idle; lastKernel = kernel; lastUser = user;
            return lastBusy;
        }
    }
}
