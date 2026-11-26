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

        // 최근 2분 30초 안에 소리가 들린 앱은 소리를 내는 중으로 본다 (1분 주기 두 번 + 여유)
        readonly AudioMemory audioMemory = new AudioMemory(Clock.MsToTs(150 * 1000));

        public HashSet<string> AudioApps()
        {
            pathCache = new Dictionary<uint, string>();   // 주기 작업마다 새로 (pid 재사용 대비)
            var heard = new List<string>();
            foreach (uint pid in Audio.ActivePids())
            {
                string id = apps.RunningApp(pid, IntPtr.Zero, pathCache);
                if (id != null) heard.Add(id);
            }
            return audioMemory.Update(heard, Ts());
        }

        public HashSet<string> VisibleApps()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cache = pathCache;
            var windowPids = new HashSet<uint>();   // 보이는 창이 하나라도 있는 프로세스 (CLI에서 뺀다)
            Native.EnumWindows((h, l) =>
            {
                if (Native.IsWindowVisible(h)) windowPids.Add(Native.WindowPid(h));
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

            // 터미널 안의 CLI 도구 (창이 없어서 위에서는 안 잡힌다)
            try
            {
                foreach (uint pid in CliApps.Pick(CliApps.Snapshot(), windowPids))
                {
                    try
                    {
                        string id = apps.CliApp(pid, cache);
                        if (id != null) set.Add(id);
                    }
                    catch (Exception ex) { ErrorLog.Write("cli", ex); }
                }
            }
            catch (Exception ex) { ErrorLog.Write("cli", ex); }
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
            return IsFullscreenRect(r, mi.rcMonitor, Native.IsZoomed(hwnd), Native.GetWindowLong(hwnd, Native.GWL_STYLE));
        }

        /// <summary>
        /// 모니터 전체를 덮는지 판정한다. 작업 표시줄이 없는 보조 모니터·자동 숨김 환경에서는
        /// 일반 최대화 창(제목 표시줄 + 크기 조절 테두리)도 모니터를 덮으므로 전체 화면에서 뺀다.
        /// </summary>
        internal static bool IsFullscreenRect(Native.RECT r, Native.RECT mon, bool zoomed, int style)
        {
            bool covers = r.Left <= mon.Left && r.Top <= mon.Top &&
                          r.Right >= mon.Right && r.Bottom >= mon.Bottom;
            if (!covers) return false;
            if (zoomed && (style & Native.WS_CAPTION) == Native.WS_CAPTION && (style & Native.WS_THICKFRAME) != 0)
                return false;
            return true;
        }
    }
}
