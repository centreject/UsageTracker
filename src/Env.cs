using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace UsageTracker
{
    /// <summary>Tracker가 쓰는 바깥 세계(시계·창·입력·소리). 자체 시험에서 가짜로 바꿔 끼운다.</summary>
    interface IEnv
    {
        long Ts();                                // 단조 시계 (Stopwatch 단위)
        DateTime Now();                           // 벽시계
        uint IdleMs();                            // 마지막 입력 이후 경과
        IntPtr Foreground();
        string AppOf(IntPtr hwnd);                // 맨 앞 창 → 앱 ID (제외 대상이면 null)
        bool IsFullscreen(IntPtr hwnd);
        HashSet<string> AudioApps();              // 소리 내는 앱 ID
        HashSet<string> VisibleApps();            // 보이는 창이 있는 앱 ID
        string NameOf(string appId);
        List<AppInfo> NewApps { get; }
        void ResetApps();                         // 전체 초기화 후 앱 정보를 처음부터 다시 모은다
    }

    /// <summary>실제 Windows 구현.</summary>
    sealed class RealEnv : IEnv
    {
        readonly AppResolver apps;
        Dictionary<uint, string> pathCache = new Dictionary<uint, string>();

        public RealEnv(AppResolver apps) { this.apps = apps; }

        public long Ts() { return Stopwatch.GetTimestamp(); }
        public DateTime Now() { return DateTime.Now; }
        public uint IdleMs() { return Native.IdleMilliseconds(); }
        public IntPtr Foreground() { return Native.GetForegroundWindow(); }
        public string AppOf(IntPtr hwnd) { return apps.ForegroundApp(hwnd); }
        public string NameOf(string appId) { return apps.NameOf(appId); }
        public List<AppInfo> NewApps { get { return apps.NewApps; } }
        public void ResetApps() { apps.Reset(); }

        public HashSet<string> AudioApps()
        {
            pathCache = new Dictionary<uint, string>();   // 주기 작업마다 새로 (pid 재사용 대비)
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (uint pid in Audio.ActivePids())
            {
                string id = apps.RunningApp(pid, IntPtr.Zero, pathCache);
                if (id != null) set.Add(id);
            }
            return set;
        }

        public HashSet<string> VisibleApps()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cache = pathCache;
            Native.EnumWindows((h, l) =>
            {
                if (!Native.IsWindowVisible(h) || Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
                if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
                if (Native.GetWindowTextLength(h) == 0) return true;
                int cloaked;
                if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out cloaked, 4) == 0 && (cloaked & Native.DWM_CLOAKED_APP) != 0) return true;

                uint pid = Native.WindowPid(h);
                if (Native.ClassName(h) == "ApplicationFrameWindow")
                {
                    uint inner = AppResolver.InnerUwpPid(h, pid);
                    if (inner == 0) return true;
                    pid = inner;
                }
                string id = apps.RunningApp(pid, h, cache);
                if (id != null) set.Add(id);
                return true;
            }, IntPtr.Zero);
            return set;
        }

        public bool IsFullscreen(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || hwnd == Native.GetShellWindow() || hwnd == Native.GetDesktopWindow()) return false;
            string cls = Native.ClassName(hwnd);
            if (cls == "Progman" || cls == "WorkerW") return false;
            Native.RECT r;
            if (!Native.GetWindowRect(hwnd, out r)) return false;
            IntPtr mon = Native.MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/);
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            if (!Native.GetMonitorInfo(mon, ref mi)) return false;
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                   r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }
    }
}
