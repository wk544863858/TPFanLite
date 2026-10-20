
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;


namespace TPFanLite
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        static void DumpControls(StringBuilder sb, Control parent, int depth)
        {
            foreach (Control c in parent.Controls)
            {
                string extra = "";
                CheckBox cb = c as CheckBox;
                if (cb != null) extra = "  autoSize=" + cb.AutoSize;
                sb.AppendLine(new string(' ', depth * 2) + c.GetType().Name
                    + "  bounds=" + c.Bounds
                    + "  font=" + c.Font.Name + "/" + c.Font.SizeInPoints.ToString("0.0") + "pt/h" + c.Font.Height
                    + "  pref=" + c.PreferredSize
                    + extra
                    + "  text=[" + c.Text + "]");
                DumpControls(sb, c, depth + 1);
            }
        }

        public static string BiosLogPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TPFanLite");
            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "bios-op.log");
        }

        public static string InstallLogPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TPFanLite");
            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "driver-install.log");
        }

        public static bool IsElevated
        {
            get
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id)
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
        }

        static string RunTool(string exe, string args, out int exitCode)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string e = p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);
                    exitCode = p.ExitCode;
                    return (o + e).Trim();
                }
            }
            catch (Exception ex) { exitCode = -1; return "EX " + ex.Message; }
        }

        /// <summary>真实 System32 路径：32 位进程里 System32 会被 WOW64 重定向到 SysWOW64，必须走 Sysnative。</summary>
        static string DriverSysPath()
        {
            string windir = Environment.GetEnvironmentVariable("windir") ?? "C:\\Windows";
            string sysDir = (IntPtr.Size == 4 && Environment.Is64BitOperatingSystem)
                ? Path.Combine(windir, "Sysnative")
                : Path.Combine(windir, "System32");
            return Path.Combine(sysDir, "drivers", "TVicPort64.sys");
        }

        /// <summary>安装/修复 TVicPort 驱动（需要管理员；由主实例用 runas 拉起，幂等）。</summary>
        public static int InstallTvicPortDriver()
        {
            StringBuilder sb = new StringBuilder();
            Action<string> L = delegate(string s) { sb.AppendLine(DateTime.Now.ToString("HH:mm:ss") + "  " + s); };
            L("TPFanLite --install-driver");
            try
            {
                L("管理员: " + IsElevated);
                if (!IsElevated) { L("ERROR 需要管理员权限"); WriteInstallLog(sb); return 2; }

                // 真实 System32 路径（见 DriverSysPath 注释）
                string dest = DriverSysPath();
                int rc;
                string q = RunTool("sc.exe", "query TVicPort64", out rc);
                L("sc query => rc=" + rc + " " + q.Replace("\r\n", " | "));
                if (rc == 0 && q.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    L("已安装且在运行，跳过");
                    WriteInstallLog(sb);
                    return 0;
                }

                // 1) 驱动来源：优先内嵌资源（完整版构建）；否则用程序目录下的 TVicPort64.sys（-NoDriver 构建）
                string tmp = Path.Combine(Path.GetTempPath(), "TVicPort64.sys");
                using (Stream rs = Assembly.GetExecutingAssembly().GetManifestResourceStream("TVicPort64.sys"))
                {
                    if (rs != null)
                    {
                        using (FileStream fs = File.Create(tmp)) { rs.CopyTo(fs); }
                        L("解出内嵌驱动: " + tmp + " (" + new FileInfo(tmp).Length + " B)");
                    }
                    else
                    {
                        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                        string[] cand = { Path.Combine(exeDir, "TVicPort64.sys"), Path.Combine(exeDir, "assets\\TVicPort64.sys") };
                        string found = null;
                        foreach (string c in cand) { if (File.Exists(c)) { found = c; break; } }
                        if (found == null)
                        {
                            L("ERROR 未找到 TVicPort64.sys：构建时未嵌入，程序目录也没有该文件");
                            L("提示：从 EnTech Taiwan 下载 TVicPort (www.entechtaiwan.com/files/tvicport.zip)，把 64 位驱动 TVicPort64.sys 放到程序目录后重试");
                            WriteInstallLog(sb);
                            return 3;
                        }
                        File.Copy(found, tmp, true);
                        L("使用外部驱动: " + found);
                    }
                }

                // 2) 复制到 drivers 并校验
                File.Copy(tmp, dest, true);
                L("复制到: " + dest + " (exists=" + File.Exists(dest) + ")");

                // 3) 服务不存在则创建
                if (rc != 0)
                {
                    string c = RunTool("sc.exe", "create TVicPort64 type= kernel start= auto error= normal binPath= System32\\drivers\\TVicPort64.sys Group= \"Extended Base\"", out rc);
                    L("sc create => rc=" + rc + " " + c.Replace("\r\n", " | "));
                }

                // 4) 启动；失败则删掉重建（处理历史残留/错误 binPath）
                string st = RunTool("sc.exe", "start TVicPort64", out rc);
                L("sc start => rc=" + rc + " " + st.Replace("\r\n", " | "));
                if (rc != 0)
                {
                    string del = RunTool("sc.exe", "delete TVicPort64", out rc);
                    L("sc delete => rc=" + rc + " " + del.Replace("\r\n", " | "));
                    string c2 = RunTool("sc.exe", "create TVicPort64 type= kernel start= auto error= normal binPath= System32\\drivers\\TVicPort64.sys Group= \"Extended Base\"", out rc);
                    L("sc create(2) => rc=" + rc + " " + c2.Replace("\r\n", " | "));
                    st = RunTool("sc.exe", "start TVicPort64", out rc);
                    L("sc start(2) => rc=" + rc + " " + st.Replace("\r\n", " | "));
                }
                Thread.Sleep(1500);
                string q2 = RunTool("sc.exe", "query TVicPort64", out rc);
                L("sc query after => rc=" + rc + " " + q2.Replace("\r\n", " | "));
                bool opened = Ec.Open();
                L("OpenTVicPort => " + opened);
                bool ok = opened;
                L(ok ? "OK 驱动安装完成" : "ERROR 安装后仍不可用（看上面 sc 输出；安全启动/杀软可能拦截）");
                WriteInstallLog(sb);
                return ok ? 0 : 4;
            }
            catch (Exception ex)
            {
                L("EX " + ex);
                WriteInstallLog(sb);
                return 5;
            }
        }

        /// <summary>卸载 TVicPort 驱动（需要管理员；停止并删除服务，顺带删除驱动文件，幂等）。</summary>
        public static int UninstallTvicPortDriver()
        {
            StringBuilder sb = new StringBuilder();
            Action<string> L = delegate(string s) { sb.AppendLine(DateTime.Now.ToString("HH:mm:ss") + "  " + s); };
            L("TPFanLite --uninstall-driver");
            try
            {
                L("管理员: " + IsElevated);
                if (!IsElevated) { L("ERROR 需要管理员权限"); WriteInstallLog(sb); return 2; }

                int rc;
                string q = RunTool("sc.exe", "query TVicPort64", out rc);
                L("sc query => rc=" + rc + " " + q.Replace("\r\n", " | "));
                if (rc != 0) { L("驱动未安装，跳过"); WriteInstallLog(sb); return 0; }

                string st = RunTool("sc.exe", "stop TVicPort64", out rc);
                L("sc stop => rc=" + rc + " " + st.Replace("\r\n", " | "));
                Thread.Sleep(1000);
                string del = RunTool("sc.exe", "delete TVicPort64", out rc);
                L("sc delete => rc=" + rc + " " + del.Replace("\r\n", " | "));

                string dest = DriverSysPath();
                try { if (File.Exists(dest)) { File.Delete(dest); L("删除驱动文件: " + dest); } }
                catch (Exception ex) { L("删除文件失败（可手动删）: " + ex.Message); }

                Thread.Sleep(500);
                string q2 = RunTool("sc.exe", "query TVicPort64", out rc);
                L("sc query after => rc=" + rc + " " + q2.Replace("\r\n", " | "));
                bool gone = q2.IndexOf("SERVICE_NAME", StringComparison.OrdinalIgnoreCase) < 0 && q2.IndexOf("TVicPort64", StringComparison.OrdinalIgnoreCase) < 0;
                L(gone ? "OK 驱动已卸载（重启后驱动内存彻底释放）" : "WARN 服务可能仍在，需重启");
                WriteInstallLog(sb);
                return gone ? 0 : 4;
            }
            catch (Exception ex)
            {
                L("EX " + ex);
                WriteInstallLog(sb);
                return 5;
            }
        }

        static void WriteInstallLog(StringBuilder sb)
        {
            string txt = sb.ToString();
            try { Console.WriteLine(txt); } catch { }
            try { File.WriteAllText(InstallLogPath(), txt, Encoding.UTF8); } catch { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            bool selftest = false, startHidden = false, guitest = false, dumplayout = false, setstartup = false, unsetstartup = false, metertest = false;
            foreach (string a in args)
            {
                if (a == "--selftest") selftest = true;
                if (a == "--metertest") metertest = true;
                if (a == "--tray") startHidden = true;
                if (a == "--guitest") guitest = true;
                if (a == "--dumplayout") dumplayout = true;
                if (a == "--setstartup") setstartup = true;
                if (a == "--unsetstartup") unsetstartup = true;
                if (a == "--about") MainForm.AboutOnStart = true;
            }

            // 命令行：--install-driver（安装/修复 TVicPort 驱动，需管理员；主实例用 runas 拉起）
            foreach (string a in args)
                if (a == "--install-driver") return InstallTvicPortDriver();
            // 命令行：--uninstall-driver（卸载 TVicPort 驱动，需管理员）
            foreach (string a in args)
                if (a == "--uninstall-driver") return UninstallTvicPortDriver();

            // 命令行：--bios-set "Name,Value"（以管理员子进程执行，结果写 bios-op.log）
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] != "--bios-set" || i + 1 >= args.Length) continue;
                string spec = args[i + 1];
                int comma = spec.IndexOf(',');
                string name = comma > 0 ? spec.Substring(0, comma) : spec;
                string value = comma > 0 ? spec.Substring(comma + 1) : "";
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("SET " + name + "," + value + " => " + Bios.Set(name, value));
                sb.AppendLine("SAVE => " + Bios.Save());
                sb.AppendLine("READBACK => " + (Bios.Read(name) ?? "(读取失败)"));
                string txt = sb.ToString();
                try { Console.WriteLine(txt); } catch { }
                try { File.WriteAllText(BiosLogPath(), txt, Encoding.UTF8); } catch { }
                return 0;
            }


            if (dumplayout)
            {
                string log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layout.txt");
                try
                {
                    try { SetProcessDPIAware(); } catch { }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Cfg c0 = new Cfg(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TPFanLite.ini"));
                    MainForm f0 = new MainForm(false, c0);
                    f0.Show();
                    Application.DoEvents();
                    StringBuilder sb0 = new StringBuilder();
                    sb0.AppendLine("ClientSize=" + f0.ClientSize + "  Size=" + f0.Size);
                    DumpControls(sb0, f0, 0);
                    File.WriteAllText(log, sb0.ToString(), Encoding.UTF8);
                    f0.Close();
                    return 0;
                }
                catch (Exception ex) { try { File.WriteAllText(log, "ERROR " + ex, Encoding.UTF8); } catch { } return 1; }
            }

            if (selftest)
            {
                StringBuilder sb = new StringBuilder();
                int rc = 0;
                try
                {
                    sb.AppendLine("TPFanLite selftest " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                    sb.AppendLine("open=" + Ec.Open() + " driverOpened=" + Ec.DriverOpened());
                    sb.AppendLine("EC_CTRL(0x1604) samples: " + Ec.PortDiag());
                    int cpu = Ec.ReadEC(0x78), aps = Ec.ReadEC(0x79), crd = Ec.ReadEC(0x7A), bat = Ec.ReadEC(0x7C), pwr = Ec.ReadEC(0xC2);
                    sb.AppendLine(string.Format("temps: cpu={0} aps={1} crd={2} bat={3} pwr={4}", cpu, aps, crd, bat, pwr));
                    sb.AppendLine("rpm=" + Ec.Rpm(0));
                    sb.AppendLine("fanctrl_before=0x" + Ec.FanCtrl.ToString("X2"));
                    Ec.SetFan(0x40); Thread.Sleep(2500);
                    sb.AppendLine("write 0x40 -> readback=0x" + Ec.FanCtrl.ToString("X2") + " rpm=" + Ec.Rpm(0));
                    Ec.SetFan(0x80); Thread.Sleep(2500);
                    sb.AppendLine("restore 0x80 -> readback=0x" + Ec.FanCtrl.ToString("X2") + " rpm=" + Ec.Rpm(0));
                }
                catch (Exception ex) { sb.AppendLine("ERROR: " + ex.ToString()); rc = 1; }
                finally { Ec.Close(); }
                string txt = sb.ToString();
                try { Console.WriteLine(txt); } catch { }
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tpfanlite-selftest.txt"), txt, Encoding.UTF8); } catch { }
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "tpfanlite-selftest.txt"), txt, Encoding.UTF8); } catch { }
                return rc;
            }

            if (setstartup || unsetstartup)
            {
                bool ok = MainForm.SetStartup(setstartup);
                string mode = setstartup ? "setstartup" : "unsetstartup";
                Console.WriteLine(mode + " => " + (ok ? "OK" : "FAIL"));
                try
                {
                    string lg = Path.Combine(Path.GetTempPath(), "tpfanlite-startup.log");
                    if (File.Exists(lg)) Console.WriteLine("--- startup log ---\r\n" + File.ReadAllText(lg));
                }
                catch { }
                return ok ? 0 : 1;
            }

            // 监控读数自检：PL1 钉住状态 / 实测 PL1 / busy，各打印 5 次
            if (metertest)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("TPFanLite metertest " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Cfg mc = new Cfg(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TPFanLite.ini"));
                Pl1Pin.Configure(mc);
                bool pinOk = Pl1Pin.Start();
                if (pinOk) Pl1Pin.SetTarget(mc.PinPl1MaxW, true);   // 自检时按目标直接钉住
                for (int i = 0; i < 5; i++)
                {
                    double busy = CpuBusy.Read();
                    sb.AppendLine(string.Format("t{0}: pin={1}  PL1={2:0.0}W  target={3:0}W  busy={4:0}%",
                        i,
                        pinOk ? "OK" : ("FAIL " + Pl1Pin.LastError),
                        Pl1Pin.EffectiveW, Pl1Pin.TargetW, busy));
                    Thread.Sleep(1000);
                }
                Pl1Pin.Stop();
                string txt = sb.ToString();
                try { Console.WriteLine(txt); } catch { }
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "metertest.txt"), txt, Encoding.UTF8); } catch { }
                return 0;
            }

            bool isNew;
            Mutex single = new Mutex(true, "Global\\TPFanLite_SingleInstance", out isNew);
            if (!isNew && !guitest)
            {
                // 已有实例在跑：把它唤出来，而不是静默退出
                try
                {
                    EventWaitHandle ev = EventWaitHandle.OpenExisting("TPFanLite_Show");
                    ev.Set();
                }
                catch { }
                return 0;
            }
            GC.KeepAlive(single);

            if (guitest)
            {
                string log = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "guitest.txt");
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Cfg c = new Cfg(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TPFanLite.ini"));
                    MainForm f = new MainForm(false, c);
                    System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                    t.Interval = 2000;
                    t.Tick += delegate { t.Stop(); f.Close(); };
                    f.Shown += delegate { t.Start(); };
                    Application.Run(f);
                    File.WriteAllText(log, "guitest OK " + DateTime.Now.ToString("HH:mm:ss"), Encoding.UTF8);
                    return 0;
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(log, "ERROR: " + ex.ToString(), Encoding.UTF8); } catch { }
                    return 1;
                }
            }

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Cfg cfg = new Cfg(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TPFanLite.ini"));
            Application.Run(new MainForm(startHidden, cfg));
            return 0;
        }
    }
}
