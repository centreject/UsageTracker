using System;
using Microsoft.Win32;

namespace UsageTracker
{
    /// <summary>HKCU\...\Run 에 자동 시작을 등록한다. 실행할 때마다 경로를 현재 위치로 고친다.</summary>
    static class AutoStart
    {
        const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "UsageTracker";

        public static void Apply(bool enabled, string exePath)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (key == null) return;
                    string want = "\"" + exePath + "\"";
                    if (enabled)
                    {
                        if (!string.Equals(key.GetValue(ValueName) as string, want, StringComparison.OrdinalIgnoreCase))
                            key.SetValue(ValueName, want);
                    }
                    else if (key.GetValue(ValueName) != null)
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
            }
            catch (Exception ex) { ErrorLog.Write("autostart", ex); }
        }
    }
}
