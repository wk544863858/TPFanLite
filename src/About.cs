using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TPFanLite
{
    /// <summary>「关于」窗口：署名 + 版本/构建/机型。走托盘菜单弹出，紧凑两行信息，与主界面同一字体与缩放。</summary>
    static class About
    {
        const string Author = "wukai";

        public static void Show(IWin32Window owner, float scale)
        {
            Func<int, int> S = delegate(int v) { return (int)Math.Round(v * scale); };   // C# 5：不支持局部函数

            using (Form f = new Form())
            {
                f.Text = "关于 TPFanLite";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.BackColor = Color.White;
                f.Font = new Font("Microsoft YaHei UI", 9F);
                f.AutoScaleMode = AutoScaleMode.None;
                f.KeyPreview = true;
                f.KeyDown += delegate { if (f.DialogResult != DialogResult.OK) { f.DialogResult = DialogResult.Cancel; f.Close(); } };

                Label title = new Label();
                title.Text = "TPFanLite";
                title.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);
                title.ForeColor = Color.FromArgb(28, 88, 156);
                title.SetBounds(S(22), S(10), S(320), S(36));
                f.Controls.Add(title);

                Label sub = new Label();
                sub.Text = "ThinkPad 风扇控制 + 动态功耗上限钉住（EC / RAPL）";
                sub.Font = new Font("Microsoft YaHei UI", 8.25F);
                sub.ForeColor = Color.Gray;
                sub.SetBounds(S(24), S(46), S(330), S(20));
                f.Controls.Add(sub);

                Panel line = new Panel();
                line.BackColor = Color.FromArgb(232, 232, 232);
                line.SetBounds(S(22), S(72), S(336), 1);
                f.Controls.Add(line);

                // 版本 · 构建 · 机型压成一行
                Label info = new Label();
                info.Text = VersionText() + "  ·  " + BuildText() + "  ·  " + ModelText();
                info.ForeColor = Color.FromArgb(40, 40, 40);
                info.SetBounds(S(24), S(80), S(340), S(22));
                f.Controls.Add(info);

                // 作者一行
                Label sign = new Label();
                sign.Text = Author;
                sign.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
                sign.ForeColor = Color.Black;
                sign.SetBounds(S(24), S(104), S(340), S(22));
                f.Controls.Add(sign);

                Button ok = new Button();
                ok.Text = "确定";
                ok.DialogResult = DialogResult.OK;
                ok.SetBounds(S(380) - S(20) - S(76), S(132), S(76), S(28));
                f.Controls.Add(ok);
                f.AcceptButton = ok;
                f.CancelButton = ok;

                f.ClientSize = new Size(S(380), S(176));
                f.TopMost = owner is Form;   // 托盘弹出时保证压在其它窗口之上
                f.ShowDialog(owner);
            }
        }

        static string VersionText()
        {
            try
            {
                FileVersionInfo vi = FileVersionInfo.GetVersionInfo(Application.ExecutablePath);
                string v = (vi.FileVersion ?? "").Trim();
                if (v.Length > 0 && v != "0.0.0.0" && v != "0.0.0.0.0")
                {
                    string[] ps = v.Split('.');
                    if (ps.Length > 3) v = ps[0] + "." + ps[1] + "." + ps[2];   // 0.3.0.0 → 0.3.0
                    return "v" + v;
                }
            }
            catch { }
            return "开发版";
        }

        static string BuildText()
        {
            try { return File.GetLastWriteTime(Application.ExecutablePath).ToString("yyyy-MM-dd HH:mm"); }
            catch { return "未知"; }
        }

        static string ModelText()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                {
                    if (k != null)
                    {
                        string man = k.GetValue("SystemManufacturer") as string;
                        string prod = k.GetValue("SystemProductName") as string;
                        string s = ((man ?? "") + " " + (prod ?? "")).Trim();
                        if (s.Length > 0) return s;
                    }
                }
            }
            catch { }
            return "未知";
        }
    }
}
