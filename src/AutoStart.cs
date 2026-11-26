using System;
using Microsoft.Win32;

namespace UsageTracker
{
    /// <summary>
    /// 자동 시작: HKCU\...\Run 에 등록하고, Windows "시작 앱" 승인 목록(Explorer\StartupApproved\Run)에도 "사용"으로 넣는다.
    /// 승인 목록에 항목이 없으면 Windows 11이 로그인 때 실행을 건너뛰는 경우가 있다 (2026-11-26 실제 확인).
    /// 사용자가 Windows 설정 → 시작 앱에서 끈 경우(값의 첫 바이트가 홀수)는 앱이 시작할 때 덮어쓰지 않고,
    /// 사용자가 앱에서 직접 "자동 시작 켜기"를 고를 때만 다시 켠다.
    /// </summary>
    static class AutoStart
    {
        const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string ValueName = "UsageTracker";

        /// <summary>
        /// enabled: 켤지 끌지. userChoice: 사용자가 방금 앱에서 고른 것이면 true
        /// (그때만 Windows 설정에서 꺼 둔 것을 다시 켠다).
        /// </summary>
        public static void Apply(bool enabled, string exePath, bool userChoice = false)
        {
            try
            {
                using (var run = Registry.CurrentUser.CreateSubKey(RunPath))
                using (var approved = Registry.CurrentUser.CreateSubKey(ApprovedPath))
                {
                    if (run == null) return;
                    string want = "\"" + exePath + "\"";
                    if (enabled)
                    {
                        if (!string.Equals(run.GetValue(ValueName) as string, want, StringComparison.OrdinalIgnoreCase))
                            run.SetValue(ValueName, want);
                        if (approved != null)
                        {
                            var cur = approved.GetValue(ValueName) as byte[];
                            bool missing = cur == null || cur.Length == 0;
                            bool disabledByUser = !missing && (cur[0] & 1) == 1;
                            if (missing || (disabledByUser && userChoice))
                                approved.SetValue(ValueName, EnabledValue(), RegistryValueKind.Binary);
                        }
                    }
                    else
                    {
                        if (run.GetValue(ValueName) != null) run.DeleteValue(ValueName, false);
                        if (approved != null && approved.GetValue(ValueName) != null) approved.DeleteValue(ValueName, false);
                    }
                }
            }
            catch (Exception ex) { ErrorLog.Write("autostart", ex); }
        }

        /// <summary>Windows 설정 → 시작 앱에서 꺼 둔 상태인지 (앱 설정은 켜져 있어도 실행되지 않는다).</summary>
        public static bool DisabledInWindows()
        {
            try
            {
                using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedPath))
                {
                    var cur = approved == null ? null : approved.GetValue(ValueName) as byte[];
                    return cur != null && cur.Length > 0 && (cur[0] & 1) == 1;
                }
            }
            catch { return false; }
        }

        /// <summary>작업 관리자·설정이 "사용"일 때 쓰는 값: 02 + 0 11바이트.</summary>
        static byte[] EnabledValue()
        {
            var v = new byte[12];
            v[0] = 2;
            return v;
        }
    }
}
