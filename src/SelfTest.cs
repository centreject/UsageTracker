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
    static partial class SelfTest
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
            public string Watched;           // 다른 모니터에 보이는 창으로 소리를 내는 앱 (시청 시험)
            public string WatchedApp(IntPtr fg, HashSet<string> audio) { return Watched != null && audio.Contains(Watched) ? Watched : null; }
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
                { "T31 자리 비움 예외 종료 후 시청 시간 보존", T31_IdleAfterAudio },
                { "T32 자정·월말 분할 시 0초 구간 미기록", T32_MidnightNoZeroLine },
                { "T33 실행기 특수문자·옵션·TSV 정제", T33_HostEdgeAndClean },
                { "T34 마지막 사용 정확도·장기 미실행 복구", T34_LastUsedAndLongGapRecover },
                { "T35 UWP 지연 인식·보조 모니터 최대화 판정", T35_UwpRetryAndFullscreen },
                { "T36 IPC 서명 검증·절전 중복 방지", T36_IpcAndSuspend },
                { "T37 저장 중간 실패 후 재시도 시 중복 없음", T37_FlushPartialFailure },
                { "T38 기록 제외 앱", T38_Exclude },
                { "T39 보조 모니터 시청", T39_WatchOtherMonitor },
                { "T40 오늘 사용 시간 (트레이 툴팁)", T40_TodayUsed },
                { "T41 업데이트 알림 판단", T41_UpdateDecision },
                { "T42 업데이트 URL·버전 검증 및 정식 우선", T42_UpdateSafety },
                { "T43 예외 뒤 보조 모니터 시청·일시정지 해제", T43_WatchAfterExceptionAndPause },
                { "T44 게임패드 순환 만료·툴팁 시간 형식", T44_GamepadAndTooltip },
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
    }
}
