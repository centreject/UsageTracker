using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace UsageTracker
{
    // 자체 시험: 기록 상태 기계(Tracker)와 저장·복구
    static partial class SelfTest
    {
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
            True("월 파일이 하루 단위로 기록됨", File.Exists(SessionFormat.FileFor(dir, env.Base)));
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
            TicksTo(env, t, 450);                 // 420초 주기까지 전체 화면 유지
            True("전체 화면이면 자리 비움 아님", !t.IsIdle);
            env.Full = false;
            TicksTo(env, t, 500);                 // 480초 주기에서 판정 → 전체 화면 시청 시간(420초)은 보존
            True("전체 화면이 끝나면 자리 비움", t.IsIdle);
            t.Shutdown();
            var s = Sum(dir, env.Base);
            Near("활성은 마지막 전체 화면 확인 시점까지 보존", 420, Get(s.Active, "a.exe"));
            Near("PC 사용도 함께 보존", 420, s.PcUsed);
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

        static void T31_IdleAfterAudio()
        {
            var env = NewEnv(); string dir = NewDir();
            env.Audio.Add("a.exe");
            var t = NewTracker(env, dir);
            TicksTo(env, t, 100); env.At(100);    // 재생 클릭 후 손 뗌
            TicksTo(env, t, 700);                 // 660초 주기까지 소리 재생으로 활성 유지
            True("소리 재생 중 활성 유지", !t.IsIdle);
            env.Audio.Clear();                    // 영상 종료
            TicksTo(env, t, 750);                 // 720초 주기에서 자리 비움 진입
            True("소리 끝난 뒤 자리 비움", t.IsIdle);
            t.Shutdown();
            var s = Sum(dir, env.Base);
            Near("영상 시청 시간(660초) 보존", 660, Get(s.Active, "a.exe"));
            Near("PC 사용 시간 보존", 660, s.PcUsed);
        }

        static void T32_MidnightNoZeroLine()
        {
            var env = new FakeEnv { Base = new DateTime(2026, 3, 31, 23, 59, 0) };
            string dir = NewDir();
            env.Visible.Add("a.exe");
            var t = NewTracker(env, dir);
            env.At(90); t.Tick();                 // 4월 1일 00:00:30 → 자정·월말 분할
            env.At(120); t.Shutdown();
            int zeroLines = 0;
            foreach (var f in Directory.GetFiles(dir, "sessions-*.tsv"))
                foreach (var line in File.ReadAllLines(f, Encoding.UTF8))
                {
                    var p = line.Split('\t');
                    if (p.Length == 4 && "ARPU".IndexOf(p[0][0]) >= 0 && p[1] == p[2]) zeroLines++;
                }
            True("자정·월말 분할에서 0초 줄 없음 (실제 " + zeroLines + "개)", zeroLines == 0);
            Near("3월 31일 활성 60초", 60, Get(Sum(dir, env.Base).Active, "a.exe"));
            Near("4월 1일 활성 60초", 60, Get(Sum(dir, env.Base.AddDays(1)).Active, "a.exe"));
        }

        static void T34_LastUsedAndLongGapRecover()
        {
            // 1) 기록 모드가 꺼진 상태의 LastUsed는 now가 아니라 마지막 저장 시점이어야 한다
            var env = NewEnv(); string dir = NewDir();
            var t = NewTracker(env, dir);
            env.At(30); t.Flush();
            var now = env.Base.AddSeconds(120);
            var alive = UsageReader.LastUsed(dir, new[] { "a.exe" }, now, 'A', true);
            var dead = UsageReader.LastUsed(dir, new[] { "a.exe" }, now, 'A', false);
            True("살아 있으면 now", alive == now);
            True("꺼져 있으면 마지막 저장 시점(30초)", dead != null && Math.Abs((dead.Value - env.Base.AddSeconds(30)).TotalSeconds) <= 1);

            // 2) 같은 초에 시작한 묶음 CLI 앱 중 하나가 닫혀도 나머지가 열려 있으면 실행 중으로 판정
            string dir2 = NewDir();
            var t0 = new DateTime(2026, 3, 10, 0, 0, 0);
            File.WriteAllText(SessionFormat.FileFor(dir2, t0),
                "V\t1\r\n" +
                "O\tR\t2026-03-10 00:00:00\tcli:x.exe\r\n" +
                "O\tR\t2026-03-10 00:00:00\tcli:y.exe\r\n" +
                "R\t2026-03-10 00:00:00\t2026-03-10 00:05:00\tcli:x.exe\r\n" +
                "F\t2026-03-10 00:06:00\r\n", new UTF8Encoding(false));
            var now2 = t0.AddMinutes(10);
            True("같은 초 시작 그룹에서 열린 멤버 유지", UsageReader.LastUsed(dir2, new[] { "cli:x.exe", "cli:y.exe" }, now2, 'R', true) == now2);

            // 3) 2달 전 비정상 종료 후 복구 및 과거 달 조회 시 now까지 유령 시간 확장 방지
            string dir3 = NewDir();
            var jan = new DateTime(2026, 1, 15, 10, 0, 0);
            var env3 = new FakeEnv { Base = jan };
            var t3 = NewTracker(env3, dir3);
            env3.At(60); t3.Flush();   // 1월 15일 10:01 저장 후 비정상 종료
            var mar = new DateTime(2026, 3, 20, 10, 0, 0);
            var janSegs = UsageReader.Read(dir3, new DateTime(2026, 1, 1), new DateTime(2026, 2, 1), mar, true);
            var janSum = UsageReader.Summarize(janSegs, id => id, new DateTime(2026, 1, 1), new DateTime(2026, 2, 1));
            Near("미복구 과거 달 조회가 현재까지 번지지 않음", 60, Get(janSum.Active, "a.exe"));
            int rec = new SessionWriter(dir3).Recover(mar);
            True("2달 전 미복구 O 구간 복구됨", rec > 0);
        }

        static void T35_UwpRetryAndFullscreen()
        {
            // UWP 창 초기 fgApp=null 상태에서 다음 주기에 지연 인식
            var env = NewEnv(); env.Fg = null;
            string dir = NewDir();
            var t = NewTracker(env, dir);
            True("초기엔 활성 앱 없음", !t.HasActiveApp);
            env.At(10); env.Fg = "calc.exe";
            env.At(60); t.Tick();
            True("주기에 안쪽 UWP 앱 인식", t.HasActiveApp);
            env.At(120); t.Shutdown();
            Near("지연 인식 뒤 활성 60초 기록", 60, Get(Sum(dir, env.Base).Active, "calc.exe"));

            // 전체 화면 판정: 보조 모니터(작업 표시줄 없음)의 일반 최대화 창은 제외
            var mon = new Native.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            var maxRect = new Native.RECT { Left = -8, Top = -8, Right = 1928, Bottom = 1088 };
            var fullRect = new Native.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            var partRect = new Native.RECT { Left = 0, Top = 0, Right = 1280, Bottom = 720 };
            True("일반 최대화 창(제목+테두리)은 전체 화면 아님", !RealEnv.IsFullscreenRect(maxRect, mon, true, Native.WS_CAPTION | Native.WS_THICKFRAME));
            True("테두리 없는 전체 화면(F11 등)은 전체 화면", RealEnv.IsFullscreenRect(fullRect, mon, false, 0));
            True("테두리 없는 최대화 창은 전체 화면", RealEnv.IsFullscreenRect(fullRect, mon, true, 0));
            True("부분 창은 전체 화면 아님", !RealEnv.IsFullscreenRect(partRect, mon, false, 0));
        }

        static void T36_IpcAndSuspend()
        {
            int op; string[] ids;
            True("lParam=0 거부", !RecorderLink.ParseData(IntPtr.Zero, out op, out ids));

            // 기존 단순 정수(1, 2)는 우연한 브로드캐스트 방지를 위해 거부해야 한다
            IntPtr cdsPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf(typeof(RecorderLink.COPYDATASTRUCT)));
            IntPtr strPtr = System.Runtime.InteropServices.Marshal.StringToHGlobalUni("a.exe\nb.exe");
            try
            {
                var oldCds = new RecorderLink.COPYDATASTRUCT { dwData = (IntPtr)2, cbData = 0, lpData = IntPtr.Zero };
                System.Runtime.InteropServices.Marshal.StructureToPtr(oldCds, cdsPtr, false);
                True("매직 없는 dwData=2 거부", !RecorderLink.ParseData(cdsPtr, out op, out ids));

                var delCds = new RecorderLink.COPYDATASTRUCT { dwData = (IntPtr)DataMaintenance.OpDelete, cbData = ("a.exe\nb.exe".Length + 1) * 2, lpData = strPtr };
                System.Runtime.InteropServices.Marshal.StructureToPtr(delCds, cdsPtr, false);
                True("유효한 OpDelete 파싱", RecorderLink.ParseData(cdsPtr, out op, out ids) && op == DataMaintenance.OpDelete && ids.Length == 2 && ids[0] == "a.exe" && ids[1] == "b.exe");

                var rstCds = new RecorderLink.COPYDATASTRUCT { dwData = (IntPtr)DataMaintenance.OpReset, cbData = 0, lpData = IntPtr.Zero };
                System.Runtime.InteropServices.Marshal.StructureToPtr(rstCds, cdsPtr, false);
                True("유효한 OpReset 파싱", RecorderLink.ParseData(cdsPtr, out op, out ids) && op == DataMaintenance.OpReset && ids.Length == 0);
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(strPtr);
                System.Runtime.InteropServices.Marshal.FreeHGlobal(cdsPtr);
            }

            // SetSuspended 중복 호출 시 상태가 실제로 바뀐 첫 호출만 true
            var env = NewEnv(); string dir = NewDir();
            var t = NewTracker(env, dir);
            True("첫 절전 전환만 true", t.SetSuspended(true) && !t.SetSuspended(true));
            True("첫 복귀 전환만 true", t.SetSuspended(false) && !t.SetSuspended(false));
            t.Shutdown();
        }

        static void T37_FlushPartialFailure()
        {
            // 3월·4월 파일에 쓸 게 있는데 4월 파일 자리가 폴더라 쓰기가 실패하는 상황
            string dir = NewDir();
            var w = new SessionWriter(dir);
            var mar = new DateTime(2026, 3, 31, 23, 0, 0);
            var apr = new DateTime(2026, 4, 1, 0, 10, 0);
            w.AddClosed(new Segment { Kind = 'A', AppId = "a.exe", Start = mar }, mar.AddMinutes(10));
            w.AddClosed(new Segment { Kind = 'A', AppId = "b.exe", Start = apr }, apr.AddMinutes(10));
            string aprFile = SessionFormat.FileFor(dir, apr);
            Directory.CreateDirectory(aprFile);
            bool threw = false;
            try { w.Flush(apr.AddMinutes(10), new Segment[0]); } catch { threw = true; }
            True("4월 파일 쓰기 실패", threw);

            Directory.Delete(aprFile);
            w.Flush(apr.AddMinutes(15), new Segment[0]);   // 재시도
            Near("3월 활성 600초 (중복 없음)", 600, Get(Sum(dir, mar).Active, "a.exe"));
            Near("4월 활성 600초", 600, Get(Sum(dir, apr).Active, "b.exe"));
            int headers = 0;
            foreach (var line in File.ReadAllLines(aprFile, Encoding.UTF8)) if (line.StartsWith("V\t")) headers++;
            True("4월 파일 V 줄 1개 (실제 " + headers + "개)", headers == 1);
        }

        static void T38_Exclude()
        {
            var env = NewEnv(); string dir = NewDir();
            env.Fg = "game.exe"; env.Visible.Add("game.exe"); env.Visible.Add("b.exe");
            var t = new Tracker(env, new SessionWriter(dir)) { IdleThresholdMs = 300 * 1000, TickIntervalMs = 60 * 1000 };
            t.SetExcluded(new[] { "GAME.exe" });  // 시작 전 지정 (대소문자 무시)
            t.Start();
            env.At(60); t.Tick(); env.At(120); t.Tick();
            env.Fg = "a.exe"; t.OnForeground((IntPtr)1);   // 다른 앱으로
            env.At(180); t.Tick();
            t.SetExcluded(new[] { "game.exe", "a.exe" });  // 기록 중에 a.exe도 제외
            env.At(240); t.Tick();
            t.SetExcluded(new string[0]);                   // 제외 해제
            env.At(300); t.Tick();
            t.Shutdown();
            var s = Sum(dir, env.Base);
            Near("제외 앱 활성 0", 0, Get(s.Active, "game.exe"));
            Near("제외 앱 실행: 제외 해제 뒤 60초만", 60, Get(s.Running, "game.exe"));
            Near("다른 앱 실행은 그대로", 300, Get(s.Running, "b.exe"));
            Near("기록 중 제외: 앞 60초 + 해제 뒤 60초", 120, Get(s.Active, "a.exe"));
            Near("제외해도 PC 사용 시간은 그대로", 300, s.PcUsed);
        }

        static void T39_WatchOtherMonitor()
        {
            var env = NewEnv(); string dir = NewDir();
            env.Fg = "a.exe"; env.Audio.Add("v.exe"); env.Watched = "v.exe";
            var t = NewTracker(env, dir);
            env.At(100);                          // 마지막 입력, 이후 보조 모니터 영상 시청
            TicksTo(env, t, 700);
            True("보조 모니터 시청 중 자리 비움 아님", !t.IsIdle && t.NeedsIdlePoll);
            env.At(710); True("입력 돌아옴", t.IdlePoll());
            True("시청 끝나면 짧은 확인 중지", !t.NeedsIdlePoll);
            env.At(760); t.Tick();
            t.Shutdown();
            var s = Sum(dir, env.Base);
            Near("영상 앱 활성: 마지막 입력(100)부터 입력 복귀(710)까지", 610, Get(s.Active, "v.exe"));
            Near("맨 앞 앱 활성: 0–100 + 710–760", 150, Get(s.Active, "a.exe"));
            Near("PC 사용 시간 이어짐", 760, s.PcUsed);

            // 시청 중 소리가 끝나면 자리 비움 (마지막으로 소리를 확인한 때까지 인정)
            var env2 = NewEnv(); string dir2 = NewDir();
            env2.Fg = "a.exe"; env2.Audio.Add("v.exe"); env2.Watched = "v.exe";
            var t2 = NewTracker(env2, dir2);
            env2.At(100);
            TicksTo(env2, t2, 600);
            env2.Audio.Clear();
            TicksTo(env2, t2, 700);
            True("소리 끝나면 자리 비움", t2.IsIdle);
            t2.Shutdown();
            Near("영상 앱 활성: 100–600", 500, Get(Sum(dir2, env2.Base).Active, "v.exe"));

            // 가려지거나 같은 모니터라 시청이 아니면(Watched 없음) 기존대로 자리 비움
            var env3 = NewEnv(); string dir3 = NewDir();
            env3.Fg = "a.exe"; env3.Audio.Add("music.exe");
            var t3 = NewTracker(env3, dir3);
            env3.At(100);
            TicksTo(env3, t3, 500);
            True("배경음악만 있으면 자리 비움", t3.IsIdle);
            t3.Shutdown();
            Near("배경음악 앱 활성 없음", 0, Get(Sum(dir3, env3.Base).Active, "music.exe"));
        }

        static void T40_TodayUsed()
        {
            var env = NewEnv(23, 58); string dir = NewDir();
            env.Visible.Add("a.exe");
            var t = NewTracker(env, dir);
            env.At(60); t.Tick();
            Near("열린 사용 시간 포함", 60, t.TodayUsedSeconds());
            env.At(180); t.Tick();                // 자정(120초) 지나 날짜 바뀜
            Near("자정 뒤에는 오늘 것만", 60, t.TodayUsedSeconds());
            t.Shutdown();
            Near("파일에서 읽은 오늘 사용 시간", 60, SessionWriter.TodayUsedSeconds(dir, env.Now()));
            var t2 = new Tracker(env, new SessionWriter(dir));
            t2.SeedTodayUsed(env.Now(), SessionWriter.TodayUsedSeconds(dir, env.Now()));
            Near("다시 시작해도 이어짐", 60, t2.TodayUsedSeconds());
        }
    }
}
