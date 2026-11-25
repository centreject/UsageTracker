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
            int view = 0; string select = null;
            foreach (var a in args)
            {
                if (a.StartsWith("--view=")) int.TryParse(a.Substring(7), out view);
                else if (a.StartsWith("--select=")) select = a.Substring(9);
            }
            bool created;
            using (var mutex = new Mutex(true, "Local\\UsageTracker.UI", out created))
            {
                if (!created)
                {
                    // 이미 열려 있으면 그 창을 앞으로
                    IntPtr h = FindWindow(null, MainForm.WindowTitle);
                    if (h != IntPtr.Zero) { ShowWindow(h, 9 /*SW_RESTORE*/); SetForegroundWindow(h); }
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => ErrorLog.Write("ui", e.Exception);
                Application.Run(new MainForm(dataDir) { InitialView = view, InitialSelect = select });
            }
            return 0;
        }
    }
}
