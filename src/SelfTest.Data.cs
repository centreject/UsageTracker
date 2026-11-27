using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace UsageTracker
{
    // 자체 시험: 기록 읽기·집계, 삭제·초기화, 이름·묶음·즐겨찾기, 화면 상태
    static partial class SelfTest
    {
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
    }
}
