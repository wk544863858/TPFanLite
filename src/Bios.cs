using System;
using System.Management;
using System.Text;

namespace TPFanLite
{
    /// <summary>
    /// Lenovo ThinkPad BIOS WMI（root\wmi）只读枚举 / 写设置，需要管理员权限。
    /// 写流程：Lenovo_SetBiosSetting("SleepState,Linux") → Lenovo_SaveBiosSettings("") → 重启生效。
    /// 已知设置：SleepState = Linux(S3) / Windows 10(现代待机)。
    /// </summary>
    static class Bios
    {
        const string NS = @"root\wmi";
        public static string LastError = "";

        static ManagementBaseObject Invoke(string cls, string method, string parameter)
        {
            try
            {
                // Lenovo 的 BIOS WMI 是单例实例方法（ACPI\PNP0C14），必须取实例再调用
                using (ManagementClass mc = new ManagementClass(NS, cls, null))
                {
                    ManagementObjectCollection col = mc.GetInstances();
                    foreach (ManagementObject mo in col)
                    {
                        using (mo)
                        {
                            ManagementBaseObject inP = mo.GetMethodParameters(method);
                            inP["parameter"] = parameter ?? "";
                            ManagementBaseObject outP = mo.InvokeMethod(method, inP, null);
                            return outP;
                        }
                    }
                }
                LastError = "未找到 WMI 实例 " + cls;
                return null;
            }
            catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; return null; }
        }

        static string ReturnOf(ManagementBaseObject o)
        {
            if (o == null) return "ERROR: " + LastError;
            try { return Convert.ToString(o["return"]) ?? "(无返回)"; }
            catch { return "(无返回)"; }
        }

        public static string Set(string setting, string value)
        {
            return ReturnOf(Invoke("Lenovo_SetBiosSetting", "SetBiosSetting", setting + "," + value));
        }

        public static string Save()
        {
            return ReturnOf(Invoke("Lenovo_SaveBiosSettings", "SaveBiosSettings", ""));
        }

        public static string[] ReadMany(string[] settings)
        {
            string[] result = new string[settings.Length];
            try
            {
                ManagementClass mc = new ManagementClass(NS, "Lenovo_BiosSetting", null);
                ManagementObjectCollection col = mc.GetInstances();
                foreach (ManagementObject mo in col)
                {
                    string s = Convert.ToString(mo["CurrentSetting"]);
                    mo.Dispose();
                    if (s == null) continue;
                    for (int i = 0; i < settings.Length; i++)
                        if (result[i] == null && s.StartsWith(settings[i] + ",", StringComparison.OrdinalIgnoreCase))
                            result[i] = s;
                }
            }
            catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; }
            return result;
        }

        public static string Read(string setting)
        {
            return ReadMany(new string[] { setting })[0];
        }

        public static string Dump()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                ManagementClass mc = new ManagementClass(NS, "Lenovo_BiosSetting", null);
                ManagementObjectCollection col = mc.GetInstances();
                int n = 0;
                foreach (ManagementObject mo in col) { sb.AppendLine("  " + Convert.ToString(mo["CurrentSetting"])); mo.Dispose(); n++; }
                sb.Insert(0, "Lenovo_BiosSetting 共 " + n + " 项\r\n");
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex.GetType().Name + ": " + ex.Message); }
            return sb.ToString();
        }
    }
}
