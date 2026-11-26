using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace UsageTracker.Ui
{
    /// <summary>화면 모드 진입점. 창을 닫으면 프로세스가 끝나 메모리를 모두 돌려준다.</summary>
    static class UiApp
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        public static int Run(string dataDir, string[] args)
        {
            Recorder.DataDir = dataDir;
            int view = 0; string select = null;
            bool settingsOnly = false, devMode = false, favOnly = false;
            string search = null;
            foreach (var a in args)
            {
                if (a.StartsWith("--view=")) int.TryParse(a.Substring(7), out view);
                else if (a.StartsWith("--select=")) select = a.Substring(9);
                else if (a == "--settings") settingsOnly = true;
                else if (a == "--favonly") favOnly = true;
                else if (a.StartsWith("--search=")) search = a.Substring(9);
                else if (a.StartsWith("--theme=")) Theme.Forced = a.Substring(8);
                else if (a.StartsWith("--data=")) devMode = true;
            }

            if (settingsOnly)
            {
                // 트레이 "설정...": 설정 창만 띄운다 (메인 창과 따로)
                bool first;
                using (var m = new Mutex(true, "Local\\UsageTracker.Settings", out first))
                {
                    if (!first) { Activate("설정 - 사용 시간 기록"); return 0; }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new SettingsForm(Theme.FromSystem(), dataDir, devMode));
                }
                return 0;
            }
            bool created;
            // 시험 폴더(--data=)로 연 창은 폴더마다 따로 띄운다. 실제 창을 앞으로 가져오지 않게.
            string uiMutex = "Local\\UsageTracker.UI" + (devMode ? "." + Program.Fnv(dataDir.TrimEnd('\\').ToLowerInvariant()) : "");
            using (var mutex = new Mutex(true, uiMutex, out created))
            {
                if (!created)
                {
                    // 이미 열려 있으면 그 창을 앞으로
                    if (!devMode) Activate(MainForm.WindowTitle);
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => ErrorLog.Write("ui", e.Exception);
                Application.Run(new MainForm(dataDir) { InitialView = view, InitialSelect = select, DevMode = devMode, InitialFavOnly = favOnly, InitialSearch = search });
            }
            return 0;
        }

        static void Activate(string title)
        {
            IntPtr h = FindWindow(null, title);
            if (h != IntPtr.Zero) { ShowWindow(h, 9 /*SW_RESTORE*/); SetForegroundWindow(h); }
        }
    }
}
