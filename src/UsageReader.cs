using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UsageTracker
{
    /// <summary>파일에서 읽은 구간 하나.</summary>
    sealed class RawSeg
    {
        public char Kind;
        public string AppId;
        public DateTime Start, End;
        public double Seconds { get { return (End - Start).TotalSeconds; } }
    }

    /// <summary>기간 집계 결과. 키는 앱 ID 또는 묶음(그룹) 키.</summary>
    sealed class UsageSummary
    {
        public DateTime From, To;
        public readonly Dictionary<string, double> Active = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, double> Running = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, double[]> HourlyActive = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        public readonly double[] HourlyAll = new double[24];
        // 날짜별 활성 시간 (주·월·년 추이 그래프)
        public readonly Dictionary<string, Dictionary<DateTime, double>> DailyActive = new Dictionary<string, Dictionary<DateTime, double>>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<DateTime, double> DailyAll = new Dictionary<DateTime, double>();
        public double PcOn, PcUsed;
        public readonly List<RawSeg> ActiveSegments = new List<RawSeg>();   // 타임라인용 (키로 바꾼 AppId)
    }

    /// <summary>sessions-YYYY-MM.tsv를 읽어 기간별로 집계한다. 화면 모드에서만 쓴다.</summary>
    static class UsageReader
    {
        /// <summary>
        /// [from, to) 구간과 겹치는 구간을 읽어 잘라 돌려준다.
        /// 아직 닫히지 않은 구간(O)은 기록 모드가 실행 중이면 지금까지, 아니면 마지막 저장 시점까지로 본다.
        /// </summary>
        public static List<RawSeg> Read(string dir, DateTime from, DateTime to, DateTime now, bool recorderAlive)
        {
            var result = new List<RawSeg>();
            var open = new Dictionary<string, RawSeg>(StringComparer.Ordinal);
            DateTime lastFlush = DateTime.MinValue;

            // 비정상 종료 구간은 다음 달 파일에서 닫힐 수 있으므로 한 달 앞부터 읽는다
            var month = new DateTime(from.Year, from.Month, 1).AddMonths(-1);
            var lastMonth = new DateTime(to.AddTicks(-1).Year, to.AddTicks(-1).Month, 1);
            // 열린 구간(O)의 짝은 보통 같은 달이나 다음 달에 있으므로 한 달 뒤까지만 더 읽는다
            var nowMonth = new DateTime(now.Year, now.Month, 1);
            var nextMonth = lastMonth.AddMonths(1);
            if (nextMonth > nowMonth) nextMonth = nowMonth;
            if (nextMonth > lastMonth) lastMonth = nextMonth;
            var openStarts = new HashSet<long>();
            for (; month <= lastMonth; month = month.AddMonths(1))
            {
                var pf = Parse(SessionWriter.FileFor(dir, month));
                if (pf == null) continue;
                if (pf.LastFlush > lastFlush) lastFlush = pf.LastFlush;
                foreach (var r in pf.Records)
                {
                    if (r.Open)
                    {
                        open[OpenKey(r.Kind, r.Start, r.App)] = new RawSeg { Kind = r.Kind, AppId = r.App, Start = r.Start };
                        openStarts.Add(r.Start.Ticks);
                        continue;
                    }
                    // 열린 구간의 짝(같은 종류·시작·앱의 끝난 구간)이면 열린 목록에서 뺀다
                    if (openStarts.Count > 0 && openStarts.Contains(r.Start.Ticks)) open.Remove(OpenKey(r.Kind, r.Start, r.App));
                    Add(result, r.Kind, r.App, r.Start, r.End, from, to);
                }
            }

            // 과거 달 조회에서 복구되지 않은 오래된 O 줄이 현재(now)까지 이어지지 않게, 이번 달까지 읽었을 때만 now를 쓴다
            bool reachesNow = lastMonth == nowMonth;
            DateTime openEnd = (recorderAlive && reachesNow) ? now : lastFlush;
            foreach (var o in open.Values)
            {
                if (openEnd <= o.Start) continue;
                // 열린 구간은 자정 분할 전이므로 날짜별로 나눈다
                DateTime a = o.Start;
                while (a.Date < openEnd.Date) { Add(result, o.Kind, o.AppId, a, a.Date.AddDays(1), from, to); a = a.Date.AddDays(1); }
                Add(result, o.Kind, o.AppId, a, openEnd, from, to);
            }
            return result;
        }

        static void Add(List<RawSeg> list, char kind, string app, DateTime s, DateTime e, DateTime from, DateTime to)
        {
            if (e <= from || s >= to) return;
            if (s < from) s = from;
            if (e > to) e = to;
            if (e > s) list.Add(new RawSeg { Kind = kind, AppId = app, Start = s, End = e });
        }

        static string OpenKey(char kind, DateTime start, string app)
        {
            return kind.ToString() + start.Ticks + "|" + app;
        }

        // ---------- 월 파일 해석 캐시 (화면 모드 프로세스 안에서만 산다) ----------

        sealed class Rec
        {
            public char Kind;        // A·R·P·U (열린 구간이면 그 종류)
            public bool Open;        // 'O' 줄
            public DateTime Start, End;
            public string App;
        }

        sealed class ParsedFile
        {
            public long Length;
            public DateTime WriteTime;
            public readonly List<Rec> Records = new List<Rec>();
            public DateTime LastFlush = DateTime.MinValue;
        }

        static readonly Dictionary<string, ParsedFile> cache = new Dictionary<string, ParsedFile>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> appIds = new Dictionary<string, string>(StringComparer.Ordinal);   // 같은 앱 ID 문자열 공유

        /// <summary>
        /// 월 파일을 해석한다. 크기·수정 시각이 그대로면 이전 결과를 쓴다 (지난달 파일은 바뀌지 않으므로
        /// 1분마다 갱신해도 이번 달 파일만 다시 읽는다). 파일이 없으면 null.
        /// </summary>
        static ParsedFile Parse(string path)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) { cache.Remove(path); return null; }
            ParsedFile pf;
            if (cache.TryGetValue(path, out pf) && pf.Length == fi.Length && pf.WriteTime == fi.LastWriteTimeUtc) return pf;

            pf = new ParsedFile { Length = fi.Length, WriteTime = fi.LastWriteTimeUtc };
            foreach (var line in ReadLinesShared(path))
            {
                var p = line.Split('\t');
                DateTime s, e;
                if (p.Length == 4 && p[0].Length == 1 && "ARPU".IndexOf(p[0][0]) >= 0
                    && SessionWriter.TryParse(p[1], out s) && SessionWriter.TryParse(p[2], out e))
                    pf.Records.Add(new Rec { Kind = p[0][0], Start = s, End = e, App = Intern(p[3]) });
                else if (p.Length == 4 && p[0] == "O" && p[1].Length == 1 && SessionWriter.TryParse(p[2], out s))
                    pf.Records.Add(new Rec { Kind = p[1][0], Open = true, Start = s, App = Intern(p[3]) });
                else if (p.Length == 2 && p[0] == "F" && SessionWriter.TryParse(p[1], out s) && s > pf.LastFlush)
                    pf.LastFlush = s;
                // 그 밖의 줄(버전, 쓰다 만 줄)은 무시
            }
            cache[path] = pf;
            return pf;
        }

        static string Intern(string s)
        {
            string v;
            if (appIds.TryGetValue(s, out v)) return v;
            appIds[s] = s;
            return s;
        }

        /// <summary>기록 모드가 덧붙여 쓰는 중에도 읽을 수 있게 공유 모드로 연다.</summary>
        static IEnumerable<string> ReadLinesShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null) yield return line;
            }
        }

        /// <summary>
        /// 구간을 키(앱 또는 묶음)별로 집계한다.
        /// 실행 시간과 PC 시간은 겹치는 구간을 합집합으로 계산한다 (묶음 안의 두 exe가 동시에 실행돼도 두 번 세지 않음).
        /// </summary>
        public static UsageSummary Summarize(List<RawSeg> segs, Func<string, string> keyOf, DateTime from, DateTime to)
        {
            var sum = new UsageSummary { From = from, To = to };
            var runningByKey = new Dictionary<string, List<RawSeg>>(StringComparer.OrdinalIgnoreCase);
            var pcOn = new List<RawSeg>();
            var pcUsed = new List<RawSeg>();

            foreach (var s in segs)
            {
                switch (s.Kind)
                {
                    case 'A':
                    {
                        string key = keyOf(s.AppId);
                        double v; sum.Active.TryGetValue(key, out v); sum.Active[key] = v + s.Seconds;
                        double[] h;
                        if (!sum.HourlyActive.TryGetValue(key, out h)) { h = new double[24]; sum.HourlyActive[key] = h; }
                        AddHourly(h, sum.HourlyAll, s.Start, s.End);
                        Dictionary<DateTime, double> dd;
                        if (!sum.DailyActive.TryGetValue(key, out dd)) { dd = new Dictionary<DateTime, double>(); sum.DailyActive[key] = dd; }
                        double dv; dd.TryGetValue(s.Start.Date, out dv); dd[s.Start.Date] = dv + s.Seconds;   // 구간은 자정에서 이미 나뉘어 있다
                        sum.DailyAll.TryGetValue(s.Start.Date, out dv); sum.DailyAll[s.Start.Date] = dv + s.Seconds;
                        sum.ActiveSegments.Add(new RawSeg { Kind = 'A', AppId = key, Start = s.Start, End = s.End });
                        break;
                    }
                    case 'R':
                    {
                        string key = keyOf(s.AppId);
                        List<RawSeg> l;
                        if (!runningByKey.TryGetValue(key, out l)) { l = new List<RawSeg>(); runningByKey[key] = l; }
                        l.Add(s);
                        break;
                    }
                    case 'P': pcOn.Add(s); break;
                    case 'U': pcUsed.Add(s); break;
                }
            }
            foreach (var kv in runningByKey) sum.Running[kv.Key] = UnionSeconds(kv.Value);
            sum.PcOn = UnionSeconds(pcOn);
            sum.PcUsed = UnionSeconds(pcUsed);
            sum.ActiveSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
            return sum;
        }

        static void AddHourly(double[] app, double[] all, DateTime s, DateTime e)
        {
            DateTime t = s;
            while (t < e)
            {
                DateTime next = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0).AddHours(1);
                if (next > e) next = e;
                double sec = (next - t).TotalSeconds;
                app[t.Hour] += sec;
                all[t.Hour] += sec;
                t = next;
            }
        }

        /// <summary>
        /// 앱(들)이 마지막으로 활성(kind='A') 또는 실행(kind='R', CLI용)이었던 시각.
        /// 지금 그 상태이고 기록 모드가 살아 있으면 now. 최근 24개월만 찾는다.
        /// </summary>
        public static DateTime? LastUsed(string dir, ICollection<string> ids, DateTime now, char kind = 'A', bool recorderAlive = true)
        {
            var set = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            var month = new DateTime(now.Year, now.Month, 1);
            // 해석 캐시를 쓰므로, 화면에 보이는 기간의 파일은 다시 읽지 않는다
            for (int i = 0; i < 24; i++, month = month.AddMonths(-1))
            {
                var pf = Parse(SessionWriter.FileFor(dir, month));
                if (pf == null) continue;
                DateTime best = DateTime.MinValue;
                // 그룹 내 여러 앱이 같은 초(예: 자정 분할)에 시작해도 서로 지워지지 않게 앱 ID까지 키에 포함한다
                var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in pf.Records)
                {
                    if (r.Kind != kind || !set.Contains(r.App)) continue;
                    string k = r.App + "|" + r.Start.Ticks;
                    if (r.Open) { open.Add(k); continue; }
                    if (r.End > best) best = r.End;
                    open.Remove(k);
                }
                if (open.Count > 0 && i == 0)
                {
                    if (recorderAlive) return now;
                    if (pf.LastFlush > best) best = pf.LastFlush;
                }
                if (best > DateTime.MinValue) return best;
            }
            return null;
        }

        public static double UnionSeconds(List<RawSeg> list)
        {
            if (list.Count == 0) return 0;
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
            double total = 0;
            DateTime cs = list[0].Start, ce = list[0].End;
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].Start <= ce) { if (list[i].End > ce) ce = list[i].End; }
                else { total += (ce - cs).TotalSeconds; cs = list[i].Start; ce = list[i].End; }
            }
            return total + (ce - cs).TotalSeconds;
        }
    }
}
