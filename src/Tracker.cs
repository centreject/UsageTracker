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
    /// 바깥 세계는 IEnv로만 본다 (자체 시험에서 가짜로 바꿔 끼움).
    /// </summary>
    sealed class Tracker
    {
        readonly IEnv env;
        readonly SessionWriter writer;

        Segment active, used, on;
        readonly Dictionary<string, Segment> running = new Dictionary<string, Segment>(StringComparer.OrdinalIgnoreCase);

        IntPtr fgHwnd;
        string fgApp;
        string lastActiveApp;
        bool locked, suspended, idle, paused, started;
        DateTime pauseUntil;          // DateTime.MaxValue = 다시 켤 때까지
        long lastTickTs;
        long lastExceptionTs = long.MinValue;   // 소리·전체 화면 예외가 마지막으로 확인된 시점
        string watchApp;              // 입력은 없지만 다른 모니터에서 보고 있는 앱 (소리 + 보이는 창). 있으면 활성 시간을 이 앱에
        HashSet<string> excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 기록하지 않을 앱 ID
        DateTime usedDay; double usedTodayClosed;   // 트레이 툴팁용: 오늘 닫힌 PC 사용(U) 시간 합계

        public int IdleThresholdMs = 5 * 60 * 1000;
        public int TickIntervalMs = 60 * 1000;

        public Tracker(IEnv env, SessionWriter writer)
        {
            this.env = env;
            this.writer = writer;
        }

        public bool IsIdle { get { return idle; } }
        public bool IsPaused { get { return paused; } }
        public DateTime PauseUntil { get { return pauseUntil; } }
        public bool IsLocked { get { return locked; } }
        public bool HasActiveApp { get { return fgApp != null; } }
        /// <summary>지금 맨 앞 창이 전체 화면인지 (게임·영상 중에는 업데이트 안내를 띄우지 않으려고).</summary>
        public bool ForegroundFullscreen { get { return env.IsFullscreen(env.Foreground()); } }
        /// <summary>자리 비움 판정을 짧게 다시 봐야 하는 상태 (자리 비움, 또는 다른 모니터 시청 중).</summary>
        public bool NeedsIdlePoll { get { return idle || watchApp != null; } }

        /// <summary>기록하지 않을 앱. 바꾸면 열린 구간을 바로 닫는다.</summary>
        public void SetExcluded(IEnumerable<string> ids)
        {
            excluded = new HashSet<string>(ids ?? new string[0], StringComparer.OrdinalIgnoreCase);
            if (watchApp != null && excluded.Contains(watchApp)) watchApp = null;
            if (!started || suspended) return;
            RefreshForeground();
            Reconcile();
            RefreshRunning();
        }

        string Resolve(IntPtr hwnd)
        {
            string id = env.AppOf(hwnd);
            return id != null && excluded.Contains(id) ? null : id;
        }

        /// <summary>오늘 PC 사용(U) 시간(초). 트레이 툴팁용.</summary>
        public double TodayUsedSeconds()
        {
            DateTime today = env.Now().Date;
            if (usedDay != today) { usedDay = today; usedTodayClosed = 0; }
            double open = 0;
            if (used != null)
            {
                var from = used.Start < today ? today : used.Start;
                open = Math.Max(0, (env.Now() - from).TotalSeconds);
            }
            return usedTodayClosed + open;
        }

        /// <summary>시작할 때 이미 기록된 오늘 사용 시간을 넣어 둔다.</summary>
        public void SeedTodayUsed(DateTime day, double seconds)
        {
            usedDay = day.Date;
            usedTodayClosed = seconds;
        }

        public string StatusText()
        {
            if (suspended) return "절전";
            if (locked) return "잠금";
            if (paused) return pauseUntil == DateTime.MaxValue ? "일시정지 중" : "일시정지 중 (" + pauseUntil.ToString("HH:mm") + "까지)";
            if (idle) return "자리 비움";
            // 트레이를 누르면 맨 앞 창이 작업 표시줄로 바뀌므로, 마지막으로 기록한 앱을 보여 준다
            string id = active != null ? active.AppId : lastActiveApp;
            return id != null ? "기록 중: " + env.NameOf(id) : "기록 중";
        }

        bool ActiveAllowed { get { return !locked && !suspended && !paused && !idle; } }
        bool RunningAllowed { get { return !locked && !suspended && !paused; } }
        bool UsedAllowed { get { return !locked && !suspended && !idle; } }
        bool OnAllowed { get { return !suspended; } }

        // ---------- 시작·종료 ----------

        public void Start()
        {
            int recovered = writer.Recover(env.Now());
            if (recovered > 0) ErrorLog.Write("recover", new Exception("비정상 종료로 열린 구간 " + recovered + "개를 마지막 저장 시점에서 닫음"));
            lastTickTs = env.Ts();
            started = true;
            OnForeground(env.Foreground());
            RefreshRunning();
        }

        public void Shutdown()
        {
            CloseAll(env.Ts());
            Flush();
        }

        // ---------- 이벤트 ----------

        public void OnForeground(IntPtr hwnd)
        {
            fgHwnd = hwnd;
            fgApp = Resolve(hwnd);
            Reconcile();
        }

        public void SetLocked(bool value)
        {
            if (locked == value) return;
            locked = value;
            idle = false;
            lastExceptionTs = long.MinValue; watchApp = null;
            if (!value) RefreshForeground();
            Reconcile();
            if (!value) RefreshRunning();
        }

        /// <summary>절전 상태 전환. 실제로 상태가 바뀌었을 때만 true.</summary>
        public bool SetSuspended(bool value)
        {
            if (suspended == value) return false;
            suspended = value;
            idle = false;
            lastExceptionTs = long.MinValue; watchApp = null;
            if (value)
            {
                Reconcile();
                Flush();
            }
            else
            {
                lastTickTs = env.Ts();
                RefreshForeground();
                Reconcile();
                RefreshRunning();
            }
            return true;
        }

        /// <summary>minutes &gt; 0: 그만큼 일시정지, minutes &lt; 0: 다시 켤 때까지, 0: 재개.</summary>
        public void SetPause(int minutes)
        {
            paused = minutes != 0;
            pauseUntil = minutes > 0 ? env.Now().AddMinutes(minutes) : DateTime.MaxValue;
            if (!paused) RefreshForeground();
            Reconcile();
            if (!paused) RefreshRunning();
        }

        /// <summary>시스템 시각이 바뀌면 열린 구간을 모두 닫고 새 시각으로 다시 연다.</summary>
        public void OnTimeChanged()
        {
            CloseAll(env.Ts());
            lastTickTs = env.Ts();
            Reconcile();
            RefreshRunning();
        }

        void RefreshForeground()
        {
            fgHwnd = env.Foreground();
            fgApp = Resolve(fgHwnd);
        }

        // ---------- 주기 작업 (기본 1분) ----------

        public void Tick()
        {
            long ts = env.Ts();
            DateTime now = env.Now();

            // 타이머가 오래 멈췄다면(절전 알림 누락 등) 그 사이는 기록하지 않는다
            long gapMs = (ts - lastTickTs) * 1000 / Stopwatch.Frequency;
            if (gapMs > TickIntervalMs * 3L)
            {
                long cut = lastTickTs + Clock.MsToTs(TickIntervalMs);
                CloseAll(cut);
                RefreshForeground();
                Reconcile();
            }
            lastTickTs = ts;

            SplitAtMidnight(now);

            if (suspended) return;

            // UWP 창이 막 뜰 때 안쪽 앱이 아직 없어 fgApp이 비어 있었을 수 있으므로 다시 확인한다
            if (fgApp == null && ActiveAllowed && fgHwnd != IntPtr.Zero && env.Foreground() == fgHwnd)
            {
                string resolved = Resolve(fgHwnd);
                if (resolved != null) { fgApp = resolved; Reconcile(); }
            }

            var audioApps = env.AudioApps();

            // 자리 비움 판정: 입력이 없어도 맨 앞 앱이 소리를 내거나 전체 화면이면 사용 중으로 본다
            if (!idle && !locked)
            {
                uint idleMs = env.IdleMs();
                bool exception = fgApp != null && (audioApps.Contains(fgApp) || env.IsFullscreen(fgHwnd));
                if (exception) { lastExceptionTs = ts; SetWatch(null, ts, now); }
                else if (idleMs >= IdleThresholdMs)
                {
                    // 입력이 없어도 다른 모니터에 보이는 창으로 소리를 내는 앱이 있으면 그 앱을 보고 있는 것으로 본다
                    string watched = audioApps.Count == 0 ? null : env.WatchedApp(fgHwnd, audioApps);
                    if (watched != null && excluded.Contains(watched)) watched = null;
                    if (watched != null)
                    {
                        lastExceptionTs = ts;
                        // 시청은 마지막 입력 때부터로 본다 (그 뒤로는 맨 앞 앱이 아니라 영상을 보고 있었다)
                        long at = ts - Clock.MsToTs(idleMs);
                        if (active != null && at < active.StartTs) at = active.StartTs;
                        if (watchApp == null) SetWatch(watched, at, now.AddSeconds(-(double)(ts - at) / Stopwatch.Frequency));
                        else SetWatch(watched, ts, now);
                    }
                    else { SetWatch(null, ts, now, false); EnterIdle(ts, idleMs); }
                }
                else SetWatch(null, ts, now);
            }

            SampleRunning(audioApps, ts, now);
        }

        /// <summary>자리 비움 중 짧은 간격으로 호출. 입력이 돌아오면 true.</summary>
        public bool IdlePoll()
        {
            if (!idle && watchApp == null) return true;
            uint idleMs = env.IdleMs();
            uint resumeLimit = IdleThresholdMs > 0 ? (uint)Math.Min(5000, IdleThresholdMs - 1) : 5000;
            if (idleMs > resumeLimit) return false;
            if (!idle)
            {
                // 다른 모니터 시청 중 입력이 돌아옴: 입력 시점부터 다시 맨 앞 앱으로
                watchApp = null;
                RefreshForeground();
                ReconcileAt(env.Ts() - Clock.MsToTs(idleMs), env.Now().AddMilliseconds(-idleMs));
                return true;
            }
            idle = false;
            lastExceptionTs = long.MinValue; watchApp = null;
            RefreshForeground();
            long ts = env.Ts() - Clock.MsToTs(idleMs);
            ReconcileAt(ts, env.Now().AddMilliseconds(-idleMs));
            return true;
        }

        void EnterIdle(long ts, uint idleMs)
        {
            idle = true;
            long at = ts - Clock.MsToTs(idleMs);   // 마지막 입력 시점까지만 집계
            // 영상 시청(소리·전체 화면 예외) 뒤 재생이 끝나 자리 비움이 됐을 때, 시청 시간까지 되감아 지우지 않는다
            if (lastExceptionTs > at) at = Math.Min(lastExceptionTs, ts);
            lastExceptionTs = long.MinValue; watchApp = null;
            if (active != null) { Close(active, Math.Max(at, active.StartTs)); active = null; }
            if (used != null) { Close(used, Math.Max(at, used.StartTs)); used = null; }
        }

        /// <summary>잠금 해제·절전 복귀·일시정지 해제·시각 변경 직후 실행 시간을 바로 다시 연다 (다음 주기까지 기다리지 않음).</summary>
        void RefreshRunning()
        {
            if (!RunningAllowed) return;
            SampleRunning(env.AudioApps(), env.Ts(), env.Now());
        }

        /// <summary>실행 시간: 보이는 창이 있거나 소리를 내는 앱 + 지금 활성 앱.</summary>
        void SampleRunning(HashSet<string> audioApps, long ts, DateTime now)
        {
            if (!RunningAllowed) return;
            var set = env.VisibleApps();
            set.UnionWith(audioApps);
            set.RemoveWhere(id => !AppResolver.CountsAsRunning(id) || excluded.Contains(id));
            if (active != null && AppResolver.CountsAsRunning(active.AppId)) set.Add(active.AppId);

            var toClose = new List<string>();
            foreach (var kv in running) if (!set.Contains(kv.Key)) toClose.Add(kv.Key);
            foreach (var id in toClose) { Close(running[id], ts); running.Remove(id); }
            foreach (var id in set) EnsureRunning(id, ts, now);
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
            ReconcileAt(env.Ts(), env.Now());
        }

        void ReconcileAt(long ts, DateTime now)
        {
            Toggle(ref on, OnAllowed, 'P', ts, now);
            Toggle(ref used, UsedAllowed, 'U', ts, now);

            string want = ActiveAllowed ? (watchApp ?? fgApp) : null;
            if (active != null && !string.Equals(active.AppId, want, StringComparison.OrdinalIgnoreCase))
            {
                Close(active, ts);
                active = null;
            }
            if (active == null && want != null)
            {
                active = new Segment { Kind = 'A', AppId = want, Start = now, StartTs = ts };
                lastActiveApp = want;
                if (RunningAllowed && AppResolver.CountsAsRunning(want)) EnsureRunning(want, ts, now);
            }

            if (!RunningAllowed) CloseRunning(ts);
        }

        void SetWatch(string app, long ts, DateTime now, bool reconcile = true)
        {
            if (string.Equals(watchApp, app, StringComparison.OrdinalIgnoreCase)) return;
            watchApp = app;
            if (reconcile) ReconcileAt(ts, now);
        }

        void Toggle(ref Segment s, bool allowed, char kind, long ts, DateTime now)
        {
            if (allowed && s == null) s = new Segment { Kind = kind, AppId = "-", Start = now, StartTs = ts };
            else if (!allowed && s != null) { Close(s, ts); s = null; }
        }

        void EnsureRunning(string id, long ts, DateTime now)
        {
            if (!running.ContainsKey(id)) running[id] = new Segment { Kind = 'R', AppId = id, Start = now, StartTs = ts };
        }

        void CloseRunning(long ts)
        {
            foreach (var s in running.Values) Close(s, ts);
            running.Clear();
        }

        void CloseAll(long ts)
        {
            lastExceptionTs = long.MinValue; watchApp = null;
            if (active != null) { Close(active, ts); active = null; }
            if (used != null) { Close(used, ts); used = null; }
            if (on != null) { Close(on, ts); on = null; }
            CloseRunning(ts);
        }

        void Close(Segment s, long endTs)
        {
            DateTime end = Clock.EndOf(s, endTs);
            writer.AddClosed(s, end);
            if (s.Kind == 'U')
            {
                DateTime today = env.Now().Date;
                if (usedDay != today) { usedDay = today; usedTodayClosed = 0; }
                var from = s.Start < today ? today : s.Start;
                if (end > from) usedTodayClosed += (end - from).TotalSeconds;
            }
        }

        // ---------- 삭제·초기화 (화면 모드 요청) ----------

        /// <summary>해당 앱들의 기록을 지운다. 진행 중인 구간은 지금부터 새로 시작한다.</summary>
        public void DeleteApps(ICollection<string> ids)
        {
            Flush();   // 버퍼에 있던 줄까지 파일에 쓴 뒤 지운다
            DataMaintenance.DeleteApps(writer.Dir, ids);
            var set = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            long ts = env.Ts(); DateTime now = env.Now();
            if (active != null && set.Contains(active.AppId)) active = Restart(active, ts, now);
            foreach (var k in new List<string>(running.Keys))
                if (set.Contains(k)) running[k] = Restart(running[k], ts, now);
        }

        /// <summary>모든 기록을 지우고 지금부터 다시 기록한다.</summary>
        public void ResetAll()
        {
            Flush();
            DataMaintenance.ResetAll(writer.Dir);
            env.ResetApps();
            lastExceptionTs = long.MinValue; watchApp = null;
            long ts = env.Ts(); DateTime now = env.Now();
            usedDay = now.Date; usedTodayClosed = 0;
            if (on != null) on = Restart(on, ts, now);
            if (used != null) used = Restart(used, ts, now);
            if (active != null) { active = null; }
            CloseRunningSilently();
            RefreshForeground();
            ReconcileAt(ts, now);
            RefreshRunning();
        }

        static Segment Restart(Segment s, long ts, DateTime now)
        {
            return new Segment { Kind = s.Kind, AppId = s.AppId, Start = now, StartTs = ts };
        }

        void CloseRunningSilently() { running.Clear(); }

        // ---------- 저장 ----------

        public void Flush()
        {
            var open = new List<Segment>();
            if (on != null) open.Add(on);
            if (used != null) open.Add(used);
            if (active != null) open.Add(active);
            open.AddRange(running.Values);
            writer.Flush(env.Now(), open);
            writer.AppendAppInfo(env.NewApps);
        }
    }
}
