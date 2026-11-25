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
            public void ResetApps() { }

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
                { "T16 앱 기록 삭제 (기록 중)", T16_DeleteApps },
                { "T17 전체 기록 초기화 (기록 중)", T17_ResetAll },
                { "T18 기록 모드 없이 삭제", T18_DeleteOffline },
                { "T19 실행기 exe 실행 대상 구분 (java·python·node·mmc)", T19_HostTargets },
                { "T20 다른 프로세스 명령줄 읽기", T20_CommandLine },
                { "T21 소리 판정 기억 (잠깐의 정적)", T21_AudioMemory },
                { "T22 날짜별 활성 시간", T22_Daily },
                { "T23 시작 메뉴 바로가기 만들기·유지", T23_Shortcut },
                { "T24 읽기 캐시: 파일이 바뀌면 다시 읽음", T24_ReaderCache },
                { "T25 즐겨찾기 저장·이전 형식 읽기", T25_Favorites },
                { "T26 터미널 안 CLI 도구 고르기", T26_CliPick },
                { "T27 npm CLI 패키지 이름", T27_NodePackage },
                { "T28 창 있는 앱은 CLI로 또 세지 않음", T28_CliExcludeWindowed },
                { "T29 CLI 마지막 실행 시각", T29_LastRun },
                { "T30 통계 창 상태 저장·불러오기", T30_UiPrefs },
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
                    var sw2 = Stopwatch.StartNew();   // 두 번째: 해석 캐시 사용 (화면 갱신 상황)
                    segs = UsageReader.Read(dir, r.Item2, end, end.AddDays(1), false);
                    UsageReader.Summarize(segs, id => id, r.Item2, end);
                    sw2.Stop();
                    sb.AppendFormat("{0}: 처음 {1:N0}ms, 다시 {2:N0}ms (구간 {3:N0}개, 앱 {4}개)\r\n",
                        r.Item1, sw.ElapsedMilliseconds, sw2.ElapsedMilliseconds, segs.Count, sum.Active.Count);
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

        static void T16_DeleteApps()
        {
            var env = NewEnv(); string dir = NewDir();
            env.Visible.Add("a.exe"); env.Visible.Add("b.exe");
            var t = NewTracker(env, dir);
            env.At(60); env.Fg = "b.exe"; t.OnForeground((IntPtr)1);
            env.At(120); env.Fg = "a.exe"; t.OnForeground((IntPtr)1);
            env.At(150); t.Flush();
            env.At(180); t.DeleteApps(new[] { "a.exe" });   // a 활성 중에 삭제
            env.At(240); t.Shutdown();
            var s = Sum(dir, env.Base);
            Near("a 활성은 삭제 뒤 60초만 남음", 60, Get(s.Active, "a.exe"));
            Near("a 실행도 삭제 뒤 60초만", 60, Get(s.Running, "a.exe"));
            Near("b 활성은 그대로", 60, Get(s.Active, "b.exe"));
            Near("b 실행은 그대로", 240, Get(s.Running, "b.exe"));
            Near("PC 켜짐은 그대로", 240, s.PcOn);
        }

        static void T17_ResetAll()
        {
            var env = NewEnv(); string dir = NewDir();
            env.Visible.Add("a.exe");
            var t = NewTracker(env, dir);
            env.At(100); t.Flush();
            env.At(120); t.ResetAll();
            env.At(180); t.Shutdown();
            var s = Sum(dir, env.Base);
            Near("초기화 뒤 활성 60초만", 60, Get(s.Active, "a.exe"));
            Near("초기화 뒤 PC 켜짐 60초만", 60, s.PcOn);
            Near("초기화 뒤 실행 60초만", 60, Get(s.Running, "a.exe"));
        }

        static void T18_DeleteOffline()
        {
            var env = NewEnv(); string dir = NewDir();
            var t = NewTracker(env, dir);
            env.At(60); env.Fg = "b.exe"; t.OnForeground((IntPtr)1);
            env.At(120); t.Shutdown();
            int removed = DataMaintenance.DeleteApps(dir, new[] { "b.exe" });
            var s = Sum(dir, env.Base);
            True("지운 줄이 있음", removed > 0);
            Near("b 기록 없음", 0, Get(s.Active, "b.exe") + Get(s.Running, "b.exe"));
            Near("a 기록은 그대로", 60, Get(s.Active, "a.exe"));
        }

        static void T19_HostTargets()
        {
            Func<string, string, string> name = (exe, cmd) => { var r = HostApps.Target(exe, cmd); return r == null ? null : r.Item1; };
            True("java -jar", name("javaw.exe", "\"C:\\Java\\bin\\javaw.exe\" -Xmx2G -jar \"D:\\Games\\My Launcher.jar\" --nogui") == "My Launcher.jar");
            True("java 메인 클래스", name("java.exe", "java -cp lib\\a.jar;b.jar com.example.App arg") == "com.example.App");
            True("java -m 모듈", name("java.exe", "java -p mods -m org.foo/org.foo.Main") == "org.foo");
            True("python 스크립트", name("python.exe", "C:\\Python\\python.exe -u C:\\bots\\bot.py --x 1") == "bot.py");
            True("python -m", name("pythonw.exe", "pythonw.exe -m idlelib") == "idlelib");
            True("python -c는 구분 안 함", name("python.exe", "python -c \"print(1)\"") == null);
            True("node 스크립트", name("node.exe", "node --inspect server.js") == "server.js");
            True("mmc 콘솔", name("mmc.exe", "\"C:\\WINDOWS\\system32\\mmc.exe\" \"C:\\WINDOWS\\system32\\devmgmt.msc\"") == "devmgmt.msc");
            True("인자 없음", name("python.exe", "python.exe") == null);
            var full = HostApps.Target("python.exe", "python C:\\bots\\bot.py");
            True("실행 대상 경로", full != null && full.Item2 == "C:\\bots\\bot.py");
            True("따옴표·역슬래시 분리", HostApps.Split("a \"b c\" d\\\\\"e f\"").Count == 3);
        }

        static void T20_CommandLine()
        {
            string cmd = HostApps.CommandLine((uint)Process.GetCurrentProcess().Id);
            True("자기 명령줄을 읽음 (--selftest 포함)", cmd != null && cmd.Contains("--selftest"));
        }

        static void T21_AudioMemory()
        {
            long sec = Stopwatch.Frequency;
            var m = new AudioMemory(150 * sec);
            True("들리면 소리 냄", m.Update(new[] { "a.exe" }, 0).Contains("a.exe"));
            True("60초 뒤 잠깐 조용해도 유지", m.Update(new string[0], 60 * sec).Contains("a.exe"));
            True("120초 뒤에도 유지", m.Update(new string[0], 120 * sec).Contains("a.exe"));
            True("180초 동안 조용하면 해제", !m.Update(new string[0], 180 * sec).Contains("a.exe"));
        }

        static void T22_Daily()
        {
            var b = new DateTime(2026, 3, 10, 23, 30, 0);
            var segs = new List<RawSeg>
            {
                new RawSeg { Kind = 'A', AppId = "x.exe", Start = b, End = b.AddMinutes(30) },                  // 3/10 30분
                new RawSeg { Kind = 'A', AppId = "x.exe", Start = b.AddMinutes(30), End = b.AddMinutes(50) },   // 3/11 20분
                new RawSeg { Kind = 'A', AppId = "y.exe", Start = b.AddMinutes(50), End = b.AddMinutes(60) },   // 3/11 10분
            };
            var s = UsageReader.Summarize(segs, id => id, b.Date, b.Date.AddDays(2));
            Near("3/10 전체", 1800, s.DailyAll[b.Date]);
            Near("3/11 전체", 1800, s.DailyAll[b.Date.AddDays(1)]);
            Near("3/11 x", 1200, s.DailyActive["x.exe"][b.Date.AddDays(1)]);
        }

        static void T23_Shortcut()
        {
            string dir = NewDir();
            string lnk = Path.Combine(dir, "test.lnk");
            string exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            True("처음엔 만든다", StartMenu.Ensure(exe, lnk));
            True("대상 경로가 맞다", string.Equals(StartMenu.Target(lnk), exe, StringComparison.OrdinalIgnoreCase));
            True("같으면 다시 만들지 않는다", !StartMenu.Ensure(exe, lnk));
            string other = Path.Combine(Environment.SystemDirectory, "calc.exe");
            True("exe를 옮기면 고친다", StartMenu.Ensure(other, lnk) && string.Equals(StartMenu.Target(lnk), other, StringComparison.OrdinalIgnoreCase));
        }

        static void T24_ReaderCache()
        {
            var env = NewEnv(); string dir = NewDir();
            var t = NewTracker(env, dir);
            env.At(60); t.Flush();
            var s1 = Sum(dir, env.Base);
            var s2 = Sum(dir, env.Base);   // 캐시에서
            Near("같은 파일은 같은 결과", Get(s1.Active, "a.exe"), Get(s2.Active, "a.exe"));
            System.Threading.Thread.Sleep(20);
            env.At(120); env.Fg = "b.exe"; t.OnForeground((IntPtr)1);
            env.At(180); t.Shutdown();     // 파일에 덧붙임 → 크기가 바뀌므로 다시 읽어야 한다
            var s3 = Sum(dir, env.Base);
            Near("덧붙인 뒤 a", 120, Get(s3.Active, "a.exe"));
            Near("덧붙인 뒤 b", 60, Get(s3.Active, "b.exe"));
            DataMaintenance.DeleteApps(dir, new[] { "b.exe" });   // 다시 쓰기 → 다시 읽어야 한다
            Near("삭제 뒤 b 없음", 0, Get(Sum(dir, env.Base).Active, "b.exe"));
        }

        static void T25_Favorites()
        {
            string dir = NewDir();
            // v0.7 이하 형식(즐겨찾기 열 없음)도 읽혀야 한다
            File.WriteAllText(Path.Combine(dir, "apps.tsv"), "V\t1\r\na.exe\t에이\t\t0\r\n", new UTF8Encoding(false));
            var c = new AppCatalog(dir);
            True("이전 형식: 이름 유지", c.DisplayName("a.exe") == "에이");
            True("이전 형식: 즐겨찾기 아님", !c.IsFavorite("a.exe"));
            c.SetFavorite("a.exe", true);
            c.SetFavorite("b.exe", true);
            string g = c.Merge(new[] { "c.exe", "d.exe" }, "묶음");
            c.SetFavorite(g, true);
            c.Save();
            var c2 = new AppCatalog(dir);
            True("앱 즐겨찾기 저장", c2.IsFavorite("a.exe") && c2.IsFavorite("b.exe"));
            True("묶음 즐겨찾기 저장", c2.IsFavorite(g));
            True("이름은 그대로", c2.DisplayName("a.exe") == "에이");
            c2.SetFavorite("a.exe", false); c2.Save();
            True("해제 저장", !new AppCatalog(dir).IsFavorite("a.exe"));
        }

        static void T26_CliPick()
        {
            Func<uint, uint, string, CliApps.Proc> P = (pid, ppid, exe) => new CliApps.Proc { Pid = pid, ParentPid = ppid, Exe = exe };
            var procs = new List<CliApps.Proc>
            {
                P(1, 0, "explorer.exe"),
                P(10, 1, "WindowsTerminal.exe"),
                P(11, 10, "cmd.exe"),          // 터미널 탭의 셸
                P(12, 11, "claude.exe"),       // ← CLI 도구 (실측 구조: 도구 ← cmd ← WindowsTerminal)
                P(13, 12, "node.exe"),         // 도구의 자식은 세지 않음
                P(14, 11, "conhost.exe"),      // 콘솔 호스트는 무시
                P(20, 10, "pwsh.exe"),
                P(21, 20, "cmd.exe"),          // 셸 안의 셸
                P(22, 21, "python.exe"),       // ← CLI 도구
                P(30, 1, "cmd.exe"),           // Win+R로 연 cmd (부모 탐색기)
                P(31, 30, "ping.exe"),         // ← CLI 도구
                P(40, 1, "Claude.exe"),        // 다른 앱
                P(41, 40, "cmd.exe"),          // 앱이 뒤에서 띄운 셸
                P(42, 41, "powershell.exe"),
                P(43, 42, "git.exe"),          // 세지 않음
                P(50, 99, "cmd.exe"),          // 부모가 이미 끝난 셸
                P(51, 50, "node.exe"),         // 세지 않음
            };
            var picked = CliApps.Pick(procs);
            picked.Sort();
            True("터미널·탐색기에서 연 셸의 자식만: 12, 22, 31 (실제 " + string.Join(",", picked) + ")",
                picked.Count == 3 && picked[0] == 12 && picked[1] == 22 && picked[2] == 31);
        }

        static void T27_NodePackage()
        {
            True("범위 있는 패키지", HostApps.NodePackage(@"C:\Users\u\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js") == "claude-code");
            True("일반 패키지", HostApps.NodePackage(@"C:\x\node_modules\typescript\bin\tsc") == "typescript");
            True("node_modules 밖", HostApps.NodePackage(@"C:\proj\server.js") == null);
            var t = HostApps.Target("node.exe", "\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\Users\\u\\AppData\\Roaming\\npm\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" --resume");
            True("node + npm CLI → 패키지 이름", t != null && t.Item1 == "claude-code");
        }

        static void T28_CliExcludeWindowed()
        {
            Func<uint, uint, string, CliApps.Proc> P = (pid, ppid, exe) => new CliApps.Proc { Pid = pid, ParentPid = ppid, Exe = exe };
            var procs = new List<CliApps.Proc>
            {
                P(10, 1, "WindowsTerminal.exe"),
                P(11, 10, "cmd.exe"),
                P(12, 11, "claude.exe"),     // 창 없음 → CLI
                P(13, 11, "notepad.exe"),    // cmd에서 띄운 메모장: 창 있음 → CLI 아님
            };
            var picked = CliApps.Pick(procs, new HashSet<uint> { 13 });
            True("창 있는 notepad는 빠지고 claude만 (실제 " + string.Join(",", picked) + ")", picked.Count == 1 && picked[0] == 12);
        }

        static void T29_LastRun()
        {
            var env = NewEnv(); string dir = NewDir();
            env.Visible.Add("cli:claude.exe");   // 실행 중인 CLI (활성 기록은 없음)
            var t = NewTracker(env, dir);
            env.At(60); env.Visible.Clear(); t.Tick();   // CLI 종료
            env.At(120); t.Shutdown();
            var now = env.Base.AddSeconds(200);
            var lastActive = UsageReader.LastUsed(dir, new[] { "cli:claude.exe" }, now, 'A');
            var lastRun = UsageReader.LastUsed(dir, new[] { "cli:claude.exe" }, now, 'R');
            True("활성 기록은 없음", lastActive == null);
            True("마지막 실행 = 종료 시각", lastRun != null && Math.Abs((lastRun.Value - env.Base.AddSeconds(60)).TotalSeconds) <= 1);
        }

        static void T30_UiPrefs()
        {
            string dir = NewDir();
            var p = Ui.UiPrefs.Load(dir);
            True("처음엔 위치 없음", !p.HasBounds && p.SortCol == 1);
            p.X = 100; p.Y = 50; p.W = 900; p.H = 600; p.Maximized = true; p.Split = 420; p.Period = 2;
            p.SortCol = 0; p.SortAsc = true; p.ShowHidden = true; p.FavOnly = true;
            p.Save(dir);
            var q = Ui.UiPrefs.Load(dir);
            True("저장한 값 그대로", q.HasBounds && q.X == 100 && q.Y == 50 && q.W == 900 && q.H == 600 && q.Maximized
                && q.Split == 420 && q.Period == 2 && q.SortCol == 0 && q.SortAsc && q.ShowHidden && q.FavOnly);
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
