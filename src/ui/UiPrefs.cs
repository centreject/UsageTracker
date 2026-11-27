using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace UsageTracker.Ui
{
    /// <summary>
    /// 통계 창이 기억할 것 (ui.ini, 화면 모드만 씀): 창 위치·크기·최대화, 목록/상세 경계, 기간 탭, 정렬, 필터.
    /// 기간 자체(어느 날짜)는 기억하지 않고 항상 오늘을 연다.
    /// </summary>
    sealed class UiPrefs
    {
        public int X = int.MinValue, Y = int.MinValue, W, H, Split, Period, SortCol = 1;
        public bool Maximized, SortAsc, ShowHidden, FavOnly;

        public bool HasBounds { get { return X != int.MinValue && W > 0 && H > 0; } }

        static string PathOf(string dataDir) { return Path.Combine(dataDir, "ui.ini"); }

        public static UiPrefs Load(string dataDir)
        {
            var p = new UiPrefs();
            try
            {
                string f = PathOf(dataDir);
                if (!File.Exists(f)) return p;
                var d = IniFile.ReadDictionary(f);
                p.X = Int(d, "x", int.MinValue); p.Y = Int(d, "y", int.MinValue);
                p.W = Int(d, "w", 0); p.H = Int(d, "h", 0);
                p.Split = Int(d, "split", 0);
                p.Period = Math.Max(0, Math.Min(3, Int(d, "period", 0)));
                p.SortCol = Math.Max(0, Math.Min(3, Int(d, "sortCol", 1)));
                p.Maximized = Int(d, "maximized", 0) == 1;
                p.SortAsc = Int(d, "sortAsc", 0) == 1;
                p.ShowHidden = Int(d, "showHidden", 0) == 1;
                p.FavOnly = Int(d, "favOnly", 0) == 1;
            }
            catch { }
            return p;
        }

        static int Int(Dictionary<string, string> d, string k, int def)
        {
            string s; int v;
            return d.TryGetValue(k, out s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        public void Save(string dataDir)
        {
            try
            {
                var sb = new StringBuilder();
                Action<string, int> add = (k, v) => sb.Append(k).Append('=').Append(v.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                add("x", X); add("y", Y); add("w", W); add("h", H);
                add("maximized", Maximized ? 1 : 0); add("split", Split); add("period", Period);
                add("sortCol", SortCol); add("sortAsc", SortAsc ? 1 : 0);
                add("showHidden", ShowHidden ? 1 : 0); add("favOnly", FavOnly ? 1 : 0);
                AtomicFile.WriteAllText(PathOf(dataDir), sb.ToString());
            }
            catch (Exception ex) { ErrorLog.Write("ui-prefs", ex); }
        }
    }
}
