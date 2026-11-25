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
        bool locked, suspended, idle, paused;
        DateTime pauseUntil;          // DateTime.MaxValue = 다시 켤 때까지
        long lastTickTs;

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
            fgApp = env.AppOf(hwnd);
            Reconcile();
        }

        public void SetLocked(bool value)
        {
            if (locked == value) return;
            locked = value;
            idle = false;
            if (!value) RefreshForeground();
            Reconcile();
            if (!value) RefreshRunning();
        }

        public void SetSuspended(bool value)
        {
            if (suspended == value) return;
            suspended = value;
            idle = false;
            if (value)
            {
                Reconcile();
                CloseRunning(env.Ts());
                Flush();
            }
            else
            {
                lastTickTs = env.Ts();
                RefreshForeground();
                Reconcile();
                RefreshRunning();
            }
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
            fgApp = env.AppOf(fgHwnd);
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

            var audioApps = env.AudioApps();

            // 자리 비움 판정: 입력이 없어도 맨 앞 앱이 소리를 내거나 전체 화면이면 사용 중으로 본다
            if (!idle && !locked)
            {
                uint idleMs = env.IdleMs();
                bool exception = fgApp != null && (audioApps.Contains(fgApp) || env.IsFullscreen(fgHwnd));
                if (idleMs >= IdleThresholdMs && !exception) EnterIdle(ts, idleMs);
            }

            SampleRunning(audioApps, ts, now);
        }

        /// <summary>자리 비움 중 짧은 간격으로 호출. 입력이 돌아오면 true.</summary>
        public bool IdlePoll()
        {
            if (!idle) return true;
            uint idleMs = env.IdleMs();
            if (idleMs > 5000) return false;
            idle = false;
            RefreshForeground();
            long ts = env.Ts() - Clock.MsToTs(idleMs);
            ReconcileAt(ts, env.Now().AddMilliseconds(-idleMs));
            return true;
        }

        void EnterIdle(long ts, uint idleMs)
        {
            idle = true;
            long at = ts - Clock.MsToTs(idleMs);   // 마지막 입력 시점까지만 집계
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
            set.RemoveWhere(id => !AppResolver.CountsAsRunning(id));
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

            string want = ActiveAllowed ? fgApp : null;
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
            if (active != null) { Close(active, ts); active = null; }
            if (used != null) { Close(used, ts); used = null; }
            if (on != null) { Close(on, ts); on = null; }
            CloseRunning(ts);
        }

        void Close(Segment s, long endTs)
        {
            writer.AddClosed(s, Clock.EndOf(s, endTs));
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
            long ts = env.Ts(); DateTime now = env.Now();
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
