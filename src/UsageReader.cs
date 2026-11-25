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
            for (; month <= lastMonth; month = month.AddMonths(1))
            {
                string f = SessionWriter.FileFor(dir, month);
                if (!File.Exists(f)) continue;
                foreach (var line in ReadLinesShared(f))
                {
                    var p = line.Split('\t');
                    DateTime s, e;
                    if (p.Length == 4 && p[0].Length == 1 && "ARPU".IndexOf(p[0][0]) >= 0
                        && SessionWriter.TryParse(p[1], out s) && SessionWriter.TryParse(p[2], out e))
                    {
                        open.Remove(p[0] + "|" + p[1] + "|" + p[3]);
                        Add(result, p[0][0], p[3], s, e, from, to);
                    }
                    else if (p.Length == 4 && p[0] == "O" && p[1].Length == 1 && SessionWriter.TryParse(p[2], out s))
                    {
                        open[p[1] + "|" + p[2] + "|" + p[3]] = new RawSeg { Kind = p[1][0], AppId = p[3], Start = s };
                    }
                    else if (p.Length == 2 && p[0] == "F" && SessionWriter.TryParse(p[1], out s) && s > lastFlush)
                    {
                        lastFlush = s;
                    }
                    // 그 밖의 줄(버전, 쓰다 만 줄)은 무시
                }
            }

            DateTime openEnd = recorderAlive ? now : lastFlush;
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

        /// <summary>앱(들)이 마지막으로 활성이었던 시각. 지금 활성 중이면 now. 최근 24개월만 찾는다.</summary>
        public static DateTime? LastUsed(string dir, ICollection<string> ids, DateTime now)
        {
            var set = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            var month = new DateTime(now.Year, now.Month, 1);
            for (int i = 0; i < 24; i++, month = month.AddMonths(-1))
            {
                string f = SessionWriter.FileFor(dir, month);
                if (!File.Exists(f)) continue;
                DateTime best = DateTime.MinValue;
                var open = new HashSet<string>(StringComparer.Ordinal);
                foreach (var line in ReadLinesShared(f))
                {
                    var p = line.Split('\t');
                    DateTime e;
                    if (p.Length == 4 && p[0] == "A" && set.Contains(p[3]) && SessionWriter.TryParse(p[2], out e))
                    {
                        if (e > best) best = e;
                        open.Remove(p[1] + "|" + p[3]);
                    }
                    else if (p.Length == 4 && p[0] == "O" && p[1] == "A" && set.Contains(p[3]))
                        open.Add(p[2] + "|" + p[3]);
                }
                if (open.Count > 0 && i == 0) return now;
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
