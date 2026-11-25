using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace UsageTracker
{
    /// <summary>
    /// 기록 모드의 핵심 상태 기계. 열린 구간을 상태에 맞게 열고 닫는다.
    ///   활성(A): 잠금·절전·일시정지·자리 비움이 아니고 맨 앞 앱이 있을 때
    ///   실행(R): 잠금·절전·일시정지가 아닐 때 (자리 비움 중에도 계속)
    ///   PC 사용(U): 잠금·절전·자리 비움이 아닐 때 (일시정지와 무관)
    ///   PC 켜짐(P): 절전이 아닐 때
    /// </summary>
    sealed class Tracker
    {
        readonly AppResolver apps;
        readonly SessionWriter writer;

        Segment active, used, on;
        readonly Dictionary<string, Segment> running = new Dictionary<string, Segment>(StringComparer.OrdinalIgnoreCase);

        IntPtr fgHwnd;
        string fgApp;
        string lastActiveApp;
        bool locked, suspended, idle, paused;
        DateTime pauseUntil;          // DateTime.MaxValue = 다시 켤 때까지
        long lastTickTs;

        public int IdleThresholdMs = 5 * 60 * 1000;
        public int TickIntervalMs = 60 * 1000;

        public Tracker(AppResolver apps, SessionWriter writer)
        {
            this.apps = apps;
            this.writer = writer;
        }

        public bool IsIdle { get { return idle; } }
        public bool IsPaused { get { return paused; } }
        public DateTime PauseUntil { get { return pauseUntil; } }
        public bool IsLocked { get { return locked; } }

        public string StatusText()
        {
            if (suspended) return "절전";
            if (locked) return "잠금";
            if (paused) return pauseUntil == DateTime.MaxValue ? "일시정지 중" : "일시정지 중 (" + pauseUntil.ToString("HH:mm") + "까지)";
            if (idle) return "자리 비움";
            // 트레이를 누르면 맨 앞 창이 작업 표시줄로 바뀌므로, 마지막으로 기록한 앱을 보여 준다
            string id = active != null ? active.AppId : lastActiveApp;
            return id != null ? "기록 중: " + apps.NameOf(id) : "기록 중";
        }

        bool ActiveAllowed { get { return !locked && !suspended && !paused && !idle; } }
        bool RunningAllowed { get { return !locked && !suspended && !paused; } }
        bool UsedAllowed { get { return !locked && !suspended && !idle; } }
        bool OnAllowed { get { return !suspended; } }

        // ---------- 시작·종료 ----------

        public void Start()
        {
            int recovered = writer.Recover(DateTime.Now);
            if (recovered > 0) ErrorLog.Write("recover", new Exception("비정상 종료로 열린 구간 " + recovered + "개를 마지막 저장 시점에서 닫음"));
            lastTickTs = Clock.Ts();
            OnForeground(Native.GetForegroundWindow());
        }

        public void Shutdown()
        {
            CloseAll(Clock.Ts());
            Flush();
        }

        // ---------- 이벤트 ----------

        public void OnForeground(IntPtr hwnd)
        {
            fgHwnd = hwnd;
            fgApp = apps.ForegroundApp(hwnd);
            Reconcile();
        }

        public void SetLocked(bool value)
        {
            if (locked == value) return;
            locked = value;
            idle = false;
            if (!value) RefreshForeground();
            Reconcile();
        }

        public void SetSuspended(bool value)
        {
            if (suspended == value) return;
            suspended = value;
            idle = false;
            if (value)
            {
                Reconcile();
                CloseRunning(Clock.Ts());
                Flush();
            }
            else
            {
                lastTickTs = Clock.Ts();
                RefreshForeground();
                Reconcile();
            }
        }

        /// <summary>minutes &gt; 0: 그만큼 일시정지, minutes &lt; 0: 다시 켤 때까지, 0: 재개.</summary>
        public void SetPause(int minutes)
        {
            paused = minutes != 0;
            pauseUntil = minutes > 0 ? DateTime.Now.AddMinutes(minutes) : DateTime.MaxValue;
            if (!paused) RefreshForeground();
            Reconcile();
        }

        /// <summary>시스템 시각이 바뀌면 열린 구간을 모두 닫고 새 시각으로 다시 연다.</summary>
        public void OnTimeChanged()
        {
            CloseAll(Clock.Ts());
            lastTickTs = Clock.Ts();
            Reconcile();
        }

        void RefreshForeground()
        {
            fgHwnd = Native.GetForegroundWindow();
            fgApp = apps.ForegroundApp(fgHwnd);
        }

        // ---------- 주기 작업 (기본 1분) ----------

        public void Tick()
        {
            long ts = Clock.Ts();
            DateTime now = DateTime.Now;

            // 타이머가 오래 멈췄다면(절전 알림 누락 등) 그 사이는 기록하지 않는다
            long gapMs = (ts - lastTickTs) * 1000 / Stopwatch.Frequency;
            if (gapMs > TickIntervalMs * 3L)
            {
                long cut = lastTickTs + Clock.MsToTs(TickIntervalMs);
                CloseAll(cut);
                RefreshForeground();
            }
            lastTickTs = ts;

            SplitAtMidnight(now);

            if (suspended) return;

            var pathCache = new Dictionary<uint, string>();
            var audioApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (uint pid in Audio.ActivePids())
            {
                string id = apps.RunningApp(pid, IntPtr.Zero, pathCache);
                if (id != null) audioApps.Add(id);
            }

            // 자리 비움 판정: 입력이 없어도 맨 앞 앱이 소리를 내거나 전체 화면이면 사용 중으로 본다
            if (!idle && !locked)
            {
                uint idleMs = Native.IdleMilliseconds();
                bool exception = fgApp != null && (audioApps.Contains(fgApp) || IsFullscreen(fgHwnd));
                if (idleMs >= IdleThresholdMs && !exception) EnterIdle(ts, idleMs);
            }

            // 실행 시간: 보이는 창이 있거나 소리를 내는 앱 + 지금 활성 앱
            if (RunningAllowed)
            {
                var set = VisibleApps(pathCache);
                set.UnionWith(audioApps);
                if (active != null && AppResolver.CountsAsRunning(active.AppId)) set.Add(active.AppId);

                var toClose = new List<string>();
                foreach (var kv in running) if (!set.Contains(kv.Key)) toClose.Add(kv.Key);
                foreach (var id in toClose) { Close(running[id], ts); running.Remove(id); }
                foreach (var id in set) EnsureRunning(id, ts, now);
            }
        }

        /// <summary>자리 비움 중 짧은 간격으로 호출. 입력이 돌아오면 true.</summary>
        public bool IdlePoll()
        {
            if (!idle) return true;
            uint idleMs = Native.IdleMilliseconds();
            if (idleMs > 5000) return false;
            idle = false;
            RefreshForeground();
            long ts = Clock.Ts() - Clock.MsToTs(idleMs);
            ReconcileAt(ts, DateTime.Now.AddMilliseconds(-idleMs));
            return true;
        }

        void EnterIdle(long ts, uint idleMs)
        {
            idle = true;
            long at = ts - Clock.MsToTs(idleMs);   // 마지막 입력 시점까지만 집계
            if (active != null) { Close(active, Math.Max(at, active.StartTs)); active = null; }
            if (used != null) { Close(used, Math.Max(at, used.StartTs)); used = null; }
        }

        void SplitAtMidnight(DateTime now)
        {
            if (on != null && on.Start.Date < now.Date) on = SplitSeg(on, now.Date);
            if (used != null && used.Start.Date < now.Date) used = SplitSeg(used, now.Date);
            if (active != null && active.Start.Date < now.Date) active = SplitSeg(active, now.Date);
            var keys = new List<string>(running.Keys);
            foreach (var k in keys)
                if (running[k].Start.Date < now.Date) running[k] = SplitSeg(running[k], now.Date);
        }

        Segment SplitSeg(Segment s, DateTime midnight)
        {
            long ts = Clock.TsAt(s, midnight);
            writer.AddClosed(s, midnight);
            return new Segment { Kind = s.Kind, AppId = s.AppId, Start = midnight, StartTs = ts };
        }

        // ---------- 구간 열고 닫기 ----------

        void Reconcile()
        {
            ReconcileAt(Clock.Ts(), DateTime.Now);
        }

        void ReconcileAt(long ts, DateTime now)
        {
            Toggle(ref on, OnAllowed, 'P', ts, now);
            Toggle(ref used, UsedAllowed, 'U', ts, now);

            string want = ActiveAllowed ? fgApp : null;
            if (active != null && !string.Equals(active.AppId, want, StringComparison.OrdinalIgnoreCase))
            {
                Close(active, ts);
                active = null;
            }
            if (active == null && want != null)
            {
                active = Open('A', want, ts, now);
                lastActiveApp = want;
                if (RunningAllowed && AppResolver.CountsAsRunning(want)) EnsureRunning(want, ts, now);
            }

            if (!RunningAllowed) CloseRunning(ts);
        }

        void Toggle(ref Segment s, bool allowed, char kind, long ts, DateTime now)
        {
            if (allowed && s == null) s = Open(kind, "-", ts, now);
            else if (!allowed && s != null) { Close(s, ts); s = null; }
        }

        void EnsureRunning(string id, long ts, DateTime now)
        {
            if (!running.ContainsKey(id)) running[id] = Open('R', id, ts, now);
        }

        void CloseRunning(long ts)
        {
            foreach (var s in running.Values) Close(s, ts);
            running.Clear();
        }

        void CloseAll(long ts)
        {
            if (active != null) { Close(active, ts); active = null; }
            if (used != null) { Close(used, ts); used = null; }
            if (on != null) { Close(on, ts); on = null; }
            CloseRunning(ts);
        }

        Segment Open(char kind, string appId, long ts, DateTime now)
        {
            // now는 ts와 같은 순간이어야 한다. 되돌린 ts로 열 때는 호출 측이 맞춰서 넘긴다.
            return new Segment { Kind = kind, AppId = appId, Start = now, StartTs = ts };
        }

        void Close(Segment s, long endTs)
        {
            writer.AddClosed(s, Clock.EndOf(s, endTs));
        }

        // ---------- 저장 ----------

        public void Flush()
        {
            var open = new List<Segment>();
            if (on != null) open.Add(on);
            if (used != null) open.Add(used);
            if (active != null) open.Add(active);
            open.AddRange(running.Values);
            writer.Flush(DateTime.Now, open);
            writer.AppendAppInfo(apps.NewApps);
        }

        // ---------- 창 조사 ----------

        HashSet<string> VisibleApps(Dictionary<uint, string> pathCache)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                string id = apps.RunningApp(pid, h, pathCache);
                if (id != null) set.Add(id);
                return true;
            }, IntPtr.Zero);
            return set;
        }

        static bool IsFullscreen(IntPtr hwnd)
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
