using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace UsageTracker
{
    /// <summary>
    /// --selftest: 가짜 환경(FakeEnv)으로 Tracker를 돌리고, 파일에 쓴 결과를 UsageReader로 다시 읽어 확인한다.
    /// 실제 창·입력·소리는 쓰지 않는다. 실패 수를 종료 코드로 돌려준다.
    /// </summary>
    static class SelfTest
    {
        sealed class FakeEnv : IEnv
        {
            public DateTime Base;
            public double T;                 // Base 이후 경과 초 (단조 시계)
            public double WallOffset;        // 벽시계만 바꿀 때 (시각 변경 시험)
            public double LastInputT;
            public string Fg = "a.exe";
            public bool Full;
            public HashSet<string> Audio = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            readonly List<AppInfo> none = new List<AppInfo>();

            public long Ts() { return (long)(T * Stopwatch.Frequency); }
            public DateTime Now() { return Base.AddSeconds(T + WallOffset); }
            public uint IdleMs() { return (uint)Math.Max(0, (T - LastInputT) * 1000); }
            public IntPtr Foreground() { return (IntPtr)1; }
            public string AppOf(IntPtr hwnd) { return Fg; }
            public bool IsFullscreen(IntPtr hwnd) { return Full; }
            public HashSet<string> AudioApps() { return new HashSet<string>(Audio, StringComparer.OrdinalIgnoreCase); }
            public HashSet<string> VisibleApps() { return new HashSet<string>(Visible, StringComparer.OrdinalIgnoreCase); }
            public string NameOf(string id) { return id; }
            public List<AppInfo> NewApps { get { return none; } }

            public void At(double t) { T = t; LastInputT = t; }   // 그 시각에 입력이 있었다
        }

        static readonly StringBuilder report = new StringBuilder();
        static int failures, checks;
        static string root;

        public static int Run(string reportPath)
        {
            root = Path.Combine(Path.GetTempPath(), "UsageTracker-selftest-" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(root);
            var tests = new Dictionary<string, Action>
            {
                { "T01 기본 전환: 활성·사용·켜짐, 실행 ≥ 활성", T01_Basic },
                { "T02 자정 넘김 분할", T02_Midnight },
                { "T03 자리 비움: 마지막 입력 시점까지, 복귀", T03_Idle },
                { "T04 자리 비움 예외: 소리 재생", T04_IdleAudio },
                { "T05 자리 비움 예외: 전체 화면", T05_IdleFullscreen },
                { "T06 일시정지", T06_Pause },
                { "T07 잠금", T07_Lock },
                { "T08 절전", T08_Suspend },
                { "T09 시스템 시각 변경", T09_TimeChange },
                { "T10 비정상 종료 복구", T10_Recover },
                { "T11 타이머 공백 감지", T11_Gap },
                { "T12 열린 구간 읽기 (기록 중)", T12_OpenWhileAlive },
                { "T13 묶음 실행 시간 합집합, 시간대별 분할", T13_UnionHourly },
                { "T14 이름·숨김·합치기·해제 저장", T14_Catalog },
                { "T15 실행 시간에서 탐색기·바탕화면 제외", T15_ExcludeRunning },
            };
            foreach (var kv in tests)
            {
                int before = failures;
                try { kv.Value(); }
                catch (Exception ex) { Fail("예외: " + ex); }
                report.Insert(report.Length, (failures == before ? "[통과] " : "[실패] ") + kv.Key + "\r\n");
                FlushPending();
            }
            report.Append("\r\n").Append(checks).Append("개 확인, 실패 ").Append(failures).Append("개\r\n");
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(true));
            try { Directory.Delete(root, true); } catch { }
            return failures;
        }

        /// <summary>
        /// --bench: 1년치 가상 기록(하루 활성 전환 400회, 실행 앱 12개)을 만들어 기간별 읽기·집계 시간을 잰다.
        /// </summary>
        public static void Bench(string reportPath)
        {
            string dir = Path.Combine(Path.GetTempPath(), "UsageTracker-bench-" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            try
            {
                var rnd = new Random(1);
                var w = new SessionWriter(dir);
                var end = new DateTime(2026, 12, 31);
                var start = end.AddDays(-365);
                long lines = 0;
                for (var day = start; day < end; day = day.AddDays(1))
                {
                    var t = day.AddHours(9);
                    var dayEnd = day.AddHours(23);
                    w.AddClosed(new Segment { Kind = 'P', AppId = "-", Start = t }, dayEnd);
                    w.AddClosed(new Segment { Kind = 'U', AppId = "-", Start = t }, dayEnd);
                    for (int r = 0; r < 12; r++)
                        w.AddClosed(new Segment { Kind = 'R', AppId = "app" + r + ".exe", Start = t.AddMinutes(r) }, dayEnd);
                    lines += 14;
                    for (int i = 0; i < 400 && t < dayEnd; i++)
                    {
                        var e = t.AddSeconds(30 + rnd.Next(170));
                        w.AddClosed(new Segment { Kind = 'A', AppId = "app" + rnd.Next(20) + ".exe", Start = t }, e);
                        t = e; lines++;
                    }
                    if (day.Day == 1 || day.AddDays(1) == end) w.Flush(day, new Segment[0]);
                }
                w.Flush(end, new Segment[0]);
                long bytes = 0;
                foreach (var f in Directory.GetFiles(dir, "sessions-*.tsv")) bytes += new FileInfo(f).Length;
                sb.AppendFormat("가상 기록: {0:N0}줄, {1:N1}MB\r\n", lines, bytes / 1048576.0);

                var ranges = new[] { Tuple.Create("일", end.AddDays(-1)), Tuple.Create("주", end.AddDays(-7)),
                                     Tuple.Create("월", end.AddMonths(-1)), Tuple.Create("년", end.AddDays(-365)) };
                foreach (var r in ranges)
                {
                    var sw = Stopwatch.StartNew();
                    var segs = UsageReader.Read(dir, r.Item2, end, end.AddDays(1), false);
                    var sum = UsageReader.Summarize(segs, id => id, r.Item2, end);
                    sw.Stop();
                    sb.AppendFormat("{0}: {1:N0}ms (구간 {2:N0}개, 앱 {3}개)\r\n", r.Item1, sw.ElapsedMilliseconds, segs.Count, sum.Active.Count);
                }
                sb.AppendFormat("최대 메모리(GC 힙): {0:N1}MB\r\n", GC.GetTotalMemory(false) / 1048576.0);
            }
            finally
            {
                File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(true));
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // 실패 메시지는 시험 이름 아래에 붙이도록 모아 둔다
        static readonly StringBuilder pending = new StringBuilder();
        static void FlushPending() { report.Append(pending); pending.Clear(); }
        static void Fail(string msg) { failures++; pending.Append("    - ").Append(msg).Append("\r\n"); }

        static void Near(string what, double expected, double actual, double tol = 1.0)
        {
            checks++;
            if (Math.Abs(expected - actual) > tol) Fail(what + ": 기대 " + expected + ", 실제 " + actual.ToString("0.##"));
        }

        static void True(string what, bool cond)
        {
            checks++;
            if (!cond) Fail(what);
        }

        // ---------- 도우미 ----------

        static string NewDir()
        {
            string d = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        static Tracker NewTracker(FakeEnv env, string dir)
        {
            var t = new Tracker(env, new SessionWriter(dir));
            t.IdleThresholdMs = 300 * 1000;
            t.TickIntervalMs = 60 * 1000;
            t.Start();
            return t;
        }

        static FakeEnv NewEnv(int hour = 10, int minute = 0)
        {
            return new FakeEnv { Base = new DateTime(2026, 3, 10, hour, minute, 0) };
        }

        static UsageSummary Sum(string dir, DateTime day, int days = 1)
        {
            var segs = UsageReader.Read(dir, day.Date, day.Date.AddDays(days), day.Date.AddDays(days + 1), false);
            return UsageReader.Summarize(segs, id => id, day.Date, day.Date.AddDays(days));
        }

        static double Get(Dictionary<string, double> d, string k) { double v; return d.TryGetValue(k, out v) ? v : 0; }

        /// <summary>실제처럼 60초마다 주기 작업을 돌리며 until까지 시간을 보낸다 (입력 없음).</summary>
        static void TicksTo(FakeEnv env, Tracker t, double until)
        {
            double next = Math.Floor(env.T / 60) * 60 + 60;
            while (next <= until) { env.T = next; t.Tick(); next += 60; }
            env.T = until;
        }

        // ---------- 시험 ----------

        static void T01_Basic()
        {
            var env = NewEnv(); string dir = NewDir();
            var t = NewTracker(env, dir);
            env.At(30); env.Fg = "b.exe"; t.OnForeground((IntPtr)1);
            env.At(90); t.Shutdown();
            var s = Sum(dir, env.Base);
            Near("활성 a", 30, Get(s.Active, "a.exe"));
            Near("활성 b", 60, Get(s.Active, "b.exe"));
            Near("PC 사용", 90, s.PcUsed);
            Near("PC 켜짐", 90, s.PcOn);
            True("실행 a ≥ 활성 a", Get(s.Running, "a.exe") >= Get(s.Active, "a.exe") - 0.5);
            True("실행 b ≥ 활성 b", Get(s.Running, "b.exe") >= Get(s.Active, "b.exe") - 0.5);
        }

        static void T02_Midnight()
        {
            var env = NewEnv(23, 59); string dir = NewDir();
            env.Visible.Add("a.exe");
            var t = NewTracker(env, dir);
            env.At(90); t.Tick();                 // 자정 이후 주기 작업 → 열린 구간 분할
            env.At(120); t.Shutdown();
            var d1 = Sum(dir, env.Base);
            var d2 = Sum(dir, env.Base.AddDays(1));
            Near("첫날 활성", 60, Get(d1.Active, "a.exe"));
            Near("다음날 활성", 60, Get(d2.Active, "a.exe"));
            Near("첫날 실행", 60, Get(d1.Running, "a.exe"));
            Near("다음날 켜짐", 60, d2.PcOn);
            True("월 파일이 하루 단위로 기록됨", File.Exists(SessionWriter.FileFor(dir, env.Base)));
        }

        static void T03_Idle()
        {
            var env = NewEnv(); string dir = NewDir();
            env.Visible.Add("a.exe");
            var t = NewTracker(env, dir);
            TicksTo(env, t, 100); env.At(100);    // 마지막 입력
            TicksTo(env, t, 360);
            True("300초 전에는 자리 비움 아님", !t.IsIdle);
            TicksTo(env, t, 480);                 // 420초 주기에서 320초 무입력 → 자리 비움
            True("자리 비움 상태", t.IsIdle);
            env.At(500); t.IdlePoll();            // 입력 복귀
            True("복귀", !t.IsIdle);
            TicksTo(env, t, 560); t.Shutdown();
            var s = Sum(dir, env.Base);
            Near("활성 = 100 + 60", 160, Get(s.Active, "a.exe"));
            Near("PC 사용 = 100 + 60", 160, s.PcUsed);
            Near("실행은 자리 비움 중에도 계속", 560, Get(s.Running, "a.exe"));
            Near("PC 켜짐", 560, s.PcOn);
        }

        static void T04_IdleAudio()
        {
            var env = NewEnv(); string dir = NewDir();
            env.Audio.Add("a.exe");
            var t = NewTracker(env, dir);
            TicksTo(env, t, 100); env.At(100);
            TicksTo(env, t, 560);
            True("소리 재생 중이면 자리 비움 아님", !t.IsIdle);
            t.Shutdown();
            Near("활성 계속", 560, Get(Sum(dir, env.Base).Active, "a.exe"));
        }

        static void T05_IdleFullscreen()
        {
            var env = NewEnv(); string dir = NewDir();
            env.Full = true;
            var t = NewTracker(env, dir);
            TicksTo(env, t, 100); env.At(100);
            TicksTo(env, t, 450);
            True("전체 화면이면 자리 비움 아님", !t.IsIdle);
            env.Full = false;
            TicksTo(env, t, 500);                 // 480초 주기에서 판정
            True("전체 화면이 끝나면 자리 비움", t.IsIdle);
            t.Shutdown();
            Near("활성은 마지막 입력 시점까지", 100, Get(Sum(dir, env.Base).Active, "a.exe"));
        }

        static void T06_Pause()
        {
            var env = NewEnv(); string dir = NewDir();
            env.Visible.Add("a.exe");
            var t = NewTracker(env, dir);
            env.At(60); t.SetPause(15);
            env.At(120); t.SetPause(0);
            env.At(180); t.Shutdown();
            var s = Sum(dir, env.Base);
            Near("활성은 일시정지 동안 멈춤", 120, Get(s.Active, "a.exe"));
            Near("실행도 멈춤", 120, Get(s.Running, "a.exe"));
            Near("PC 사용은 계속", 180, s.PcUsed);
        }

        static void T07_Lock()
        {
            var env = NewEnv(); string dir = NewDir();
            env.Visible.Add("a.exe");
            var t = NewTracker(env, dir);
            env.At(60); t.SetLocked(true);
            env.At(120); t.SetLocked(false);
            env.At(180); t.Shutdown();
            var s = Sum(dir, env.Base);
            Near("활성", 120, Get(s.Active, "a.exe"));
            Near("실행", 120, Get(s.Running, "a.exe"));
            Near("PC 사용", 120, s.PcUsed);
            Near("PC 켜짐은 잠금 포함", 180, s.PcOn);
        }

        static void T08_Suspend()
        {
            var env = NewEnv(); string dir = NewDir();
            var t = NewTracker(env, dir);
            env.At(60); t.SetSuspended(true);
            env.At(120); t.SetSuspended(false);
            env.At(180); t.Shutdown();
            var s = Sum(dir, env.Base);
            Near("PC 켜짐은 절전 제외", 120, s.PcOn);
            Near("활성", 120, Get(s.Active, "a.exe"));
        }

        static void T09_TimeChange()
        {
            var env = NewEnv(); string dir = NewDir();
            var t = NewTracker(env, dir);
            env.At(60); env.WallOffset = 3600; t.OnTimeChanged();   // 시계가 1시간 앞으로
            env.At(120); t.Shutdown();
            var s = Sum(dir, env.Base);
            Near("활성 합계는 실제 경과만", 120, Get(s.Active, "a.exe"));
            True("두 구간으로 나뉨", s.ActiveSegments.Count == 2);
            if (s.ActiveSegments.Count == 2)
                True("두 번째 구간은 새 시각에서 시작", s.ActiveSegments[1].Start == env.Base.AddSeconds(3660));
        }

        static void T10_Recover()
        {
            var env = NewEnv(); string dir = NewDir();
            var t = NewTracker(env, dir);
            env.At(30); t.Flush();                // 저장 후
            env.At(45);                           // 저장하지 못하고 비정상 종료
            env.At(100);
            var t2 = NewTracker(env, dir);        // 다시 시작 → 복구
            env.At(110); t2.Shutdown();
            var s = Sum(dir, env.Base);
            Near("복구분(마지막 저장까지 30초) + 재시작 후 10초", 40, Get(s.Active, "a.exe"));
            Near("PC 켜짐", 40, s.PcOn);
        }

        static void T11_Gap()
        {
            var env = NewEnv(); string dir = NewDir();
            var t = NewTracker(env, dir);
            env.At(60); t.Tick();
            env.At(400); t.Tick();                // 340초 공백 (절전 알림 누락 등)
            env.At(460); t.Shutdown();
            var s = Sum(dir, env.Base);
            Near("공백은 마지막 주기 + 1주기까지만 인정", 180, Get(s.Active, "a.exe"));
        }

        static void T12_OpenWhileAlive()
        {
            var env = NewEnv(); string dir = NewDir();
            var t = NewTracker(env, dir);
            env.At(30); t.Flush();
            var segs = UsageReader.Read(dir, env.Base.Date, env.Base.Date.AddDays(1), env.Base.AddSeconds(50), true);
            var s = UsageReader.Summarize(segs, id => id, env.Base.Date, env.Base.Date.AddDays(1));
            Near("열린 활성 구간은 지금까지", 50, Get(s.Active, "a.exe"));
            segs = UsageReader.Read(dir, env.Base.Date, env.Base.Date.AddDays(1), env.Base.AddSeconds(50), false);
            s = UsageReader.Summarize(segs, id => id, env.Base.Date, env.Base.Date.AddDays(1));
            Near("기록 모드가 꺼져 있으면 마지막 저장까지", 30, Get(s.Active, "a.exe"));
        }

        static void T13_UnionHourly()
        {
            var b = new DateTime(2026, 3, 10, 10, 0, 0);
            var segs = new List<RawSeg>
            {
                new RawSeg { Kind = 'R', AppId = "x.exe", Start = b, End = b.AddMinutes(10) },
                new RawSeg { Kind = 'R', AppId = "y.exe", Start = b.AddMinutes(5), End = b.AddMinutes(15) },
                new RawSeg { Kind = 'A', AppId = "x.exe", Start = b.AddMinutes(50), End = b.AddMinutes(70) },
            };
            var s = UsageReader.Summarize(segs, id => "group:g", b.Date, b.Date.AddDays(1));
            Near("묶음 실행 = 합집합 15분", 900, Get(s.Running, "group:g"));
            Near("10시대 활성", 600, s.HourlyAll[10]);
            Near("11시대 활성", 600, s.HourlyAll[11]);
            Near("시간대별 합 = 활성 합", Get(s.Active, "group:g"), s.HourlyAll[10] + s.HourlyAll[11]);
        }

        static void T14_Catalog()
        {
            string dir = NewDir();
            var c = new AppCatalog(dir);
            c.Rename("a.exe", "에이");
            c.SetHidden("c.exe", true);
            string g = c.Merge(new[] { "a.exe", "b.exe" }, "묶음");
            c.Save();
            var c2 = new AppCatalog(dir);
            True("합치기: 같은 키", c2.KeyOf("a.exe") == g && c2.KeyOf("b.exe") == g);
            True("묶음 이름", c2.DisplayName(g) == "묶음");
            True("사용자 이름 유지", c2.DisplayName("a.exe") == "에이");
            True("숨김 유지", c2.IsHidden("c.exe"));
            c2.Unmerge(g); c2.Save();
            var c3 = new AppCatalog(dir);
            True("해제: 원래 키로", c3.KeyOf("a.exe") == "a.exe" && c3.KeyOf("b.exe") == "b.exe");
            c3.Rename("a.exe", "  ");
            True("빈 이름이면 원래 이름", c3.DisplayName("a.exe") == "a.exe");
        }

        static void T15_ExcludeRunning()
        {
            var env = NewEnv(); string dir = NewDir();
            env.Fg = "explorer.exe";
            env.Visible.Add("explorer.exe");
            var t = NewTracker(env, dir);
            env.At(30); env.Fg = AppResolver.DesktopId; t.OnForeground((IntPtr)1);
            env.At(60); t.Tick();
            env.At(90); t.Shutdown();
            var s = Sum(dir, env.Base);
            Near("탐색기 활성은 기록", 30, Get(s.Active, "explorer.exe"));
            Near("바탕화면 활성은 기록", 60, Get(s.Active, AppResolver.DesktopId));
            True("탐색기 실행 시간 없음", Get(s.Running, "explorer.exe") == 0);
            True("바탕화면 실행 시간 없음", Get(s.Running, AppResolver.DesktopId) == 0);
        }
    }
}
