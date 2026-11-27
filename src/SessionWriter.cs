using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace UsageTracker
{
    /// <summary>진행 중이거나 끝난 사용 구간 하나.</summary>
    sealed class Segment
    {
        public char Kind;        // A=활성, R=실행, P=PC 켜짐, U=PC 사용
        public string AppId;     // A·R만 사용. P·U는 "-"
        public DateTime Start;   // 벽시계 시각
        public long StartTs;     // Stopwatch 타임스탬프 (시계 변경에 영향받지 않는 기준)
        public bool Logged;      // 'O'(열림) 줄을 이미 파일에 썼는지

        /// <summary>지금 막 열린 구간 (아직 파일에 쓰지 않음).</summary>
        public static Segment Open(char kind, string appId, DateTime start, long startTs)
        {
            return new Segment { Kind = kind, AppId = appId, Start = start, StartTs = startTs };
        }
    }

    static class Clock
    {
        public static long Ts() { return Stopwatch.GetTimestamp(); }

        public static long MsToTs(long ms) { return ms * Stopwatch.Frequency / 1000; }

        /// <summary>구간의 끝 시각 = 시작 시각 + 단조 시계로 잰 경과 시간.</summary>
        public static DateTime EndOf(Segment s, long endTs)
        {
            double sec = (double)(endTs - s.StartTs) / Stopwatch.Frequency;
            if (sec < 0) sec = 0;
            return s.Start.AddSeconds(sec);
        }

        public static long TsAt(Segment s, DateTime t)
        {
            return s.StartTs + (long)((t - s.Start).TotalSeconds * Stopwatch.Frequency);
        }
    }

    /// <summary>
    /// 사용 구간을 월별 파일(sessions-YYYY-MM.tsv)에 덧붙여 쓴다. 파일 형식은 SessionFormat.
    /// 비정상 종료 후 시작하면, 짝이 없는 O 구간을 마지막 F 시각에서 닫는다.
    /// </summary>
    sealed class SessionWriter
    {
        readonly string dir;
        readonly SortedDictionary<string, StringBuilder> pending = new SortedDictionary<string, StringBuilder>(StringComparer.Ordinal);

        public SessionWriter(string dataDir)
        {
            dir = dataDir;
        }

        public string Dir { get { return dir; } }

        static string Fmt(DateTime t) { return SessionFormat.FormatTime(t); }

        StringBuilder Buf(DateTime t)
        {
            string f = SessionFormat.FileFor(dir, t);
            StringBuilder sb;
            if (!pending.TryGetValue(f, out sb)) { sb = new StringBuilder(); pending[f] = sb; }
            return sb;
        }

        /// <summary>끝난 구간을 버퍼에 넣는다. 자정을 넘으면 날짜별로 나눈다.</summary>
        public void AddClosed(Segment s, DateTime end)
        {
            if (end < s.Start) end = s.Start;
            // 1초 미만 구간은 버린다. 단, 이미 열림(O) 표시를 썼다면 짝을 맞추기 위해 남긴다.
            if ((end - s.Start).TotalSeconds < 1 && !s.Logged) return;

            DateTime from = s.Start;
            while (from.Date < end.Date)
            {
                DateTime midnight = from.Date.AddDays(1);
                Line(s.Kind, from, midnight, s.AppId);
                from = midnight;
            }
            // 자정에서 정확히 끝난 구간(SplitAtMidnight 등)은 0초짜리 다음 날 줄을 쓰지 않는다.
            if (from < end || from == s.Start) Line(s.Kind, from, end, s.AppId);
        }

        void Line(char kind, DateTime start, DateTime end, string appId)
        {
            Buf(start).Append(kind).Append('\t').Append(Fmt(start)).Append('\t').Append(Fmt(end))
                .Append('\t').Append(Clean(appId ?? "-")).Append("\r\n");
        }

        /// <summary>버퍼와 새로 열린 구간 표시, 저장 시점 표시를 파일에 덧붙인다.</summary>
        public void Flush(DateTime now, IEnumerable<Segment> open)
        {
            foreach (var s in open)
            {
                if (s.Logged) continue;
                Buf(s.Start).Append(SessionFormat.OpenTag).Append('\t').Append(s.Kind).Append('\t').Append(Fmt(s.Start))
                    .Append('\t').Append(Clean(s.AppId ?? "-")).Append("\r\n");
                s.Logged = true;
            }
            Buf(now).Append(SessionFormat.FlushTag).Append('\t').Append(Fmt(now)).Append("\r\n");

            // 파일마다 쓴 즉시 버퍼에서 뺀다. 중간 파일에서 실패해도 이미 쓴 파일을 다음 저장 때 다시 쓰지 않게
            foreach (var f in new List<string>(pending.Keys))
            {
                string text = pending[f].ToString();
                if (text.Length > 0)
                {
                    if (!File.Exists(f)) text = SessionFormat.VersionTag + "\t" + SessionFormat.Version + "\r\n" + text;
                    File.AppendAllText(f, text, new UTF8Encoding(false));
                }
                pending.Remove(f);
            }
        }

        /// <summary>비정상 종료로 닫히지 않은 구간을 마지막 저장 시점에서 닫는다.</summary>
        public int Recover(DateTime now)
        {
            var files = new List<string>();
            // 한 달 넘게 PC를 꺼 둔 뒤 켜도 마지막 월 파일의 열린 구간을 복구할 수 있게 최근 파일 2개를 본다.
            if (Directory.Exists(dir))
            {
                var all = Directory.GetFiles(dir, SessionFormat.FilePattern);
                Array.Sort(all, StringComparer.OrdinalIgnoreCase);
                for (int i = Math.Max(0, all.Length - 2); i < all.Length; i++) files.Add(all[i]);
            }
            if (files.Count == 0) return 0;

            var openKeys = new Dictionary<string, Segment>(StringComparer.Ordinal);
            DateTime lastFlush = DateTime.MinValue;
            foreach (var f in files)
            {
                foreach (var line in AtomicFile.ReadAllLinesShared(f))
                {
                    var p = line.Split('\t');
                    DateTime t;
                    if (SessionFormat.IsOpen(p) && SessionFormat.TryParseTime(p[2], out t))
                        openKeys[p[1] + "|" + p[2] + "|" + p[3]] = new Segment { Kind = p[1][0], Start = t, AppId = p[3], Logged = true };
                    else if (SessionFormat.IsClosed(p))
                        openKeys.Remove(p[0] + "|" + p[1] + "|" + p[3]);
                    else if (SessionFormat.IsFlush(p) && SessionFormat.TryParseTime(p[1], out t) && t > lastFlush)
                        lastFlush = t;
                }
            }
            if (openKeys.Count == 0) return 0;

            foreach (var s in openKeys.Values)
                AddClosed(s, lastFlush > s.Start ? lastFlush : s.Start);
            Flush(now, new Segment[0]);
            return openKeys.Count;
        }

        // ---------- 앱 정보 (appinfo.tsv: 기록 모드만 쓴다) ----------

        /// <summary>오늘 이미 기록된(닫힌) PC 사용(U) 시간(초). 기록 모드 시작 때 트레이 툴팁용으로 한 번 읽는다.</summary>
        public static double TodayUsedSeconds(string dir, DateTime now)
        {
            string f = SessionFormat.FileFor(dir, now);
            if (!File.Exists(f)) return 0;
            DateTime today = now.Date, tomorrow = today.AddDays(1);
            double sum = 0;
            foreach (var line in AtomicFile.ReadAllLinesShared(f))
            {
                if (line.Length < 2 || line[0] != 'U' || line[1] != '\t') continue;
                var p = line.Split('\t');
                DateTime a, b;
                if (p.Length < 3 || !SessionFormat.TryParseTime(p[1], out a) || !SessionFormat.TryParseTime(p[2], out b)) continue;
                if (a < today) a = today;
                if (b > tomorrow) b = tomorrow;
                if (b > a) sum += (b - a).TotalSeconds;
            }
            return sum;
        }

        public Dictionary<string, AppInfo> LoadAppInfo()
        {
            var d = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
            string f = Path.Combine(dir, "appinfo.tsv");
            if (!File.Exists(f)) return d;
            foreach (var line in AtomicFile.ReadAllLinesShared(f))
            {
                var p = line.Split('\t');
                if (p.Length < 4 || p[0] == "V") continue;
                DateTime t; SessionFormat.TryParseTime(p[3], out t);
                d[p[0]] = new AppInfo { Id = p[0], Name = p[1], Path = p[2], FirstSeen = t };
            }
            return d;
        }

        public void AppendAppInfo(List<AppInfo> apps)
        {
            if (apps.Count == 0) return;
            string f = Path.Combine(dir, "appinfo.tsv");
            var sb = new StringBuilder();
            if (!File.Exists(f)) sb.Append(SessionFormat.VersionTag).Append('\t').Append(SessionFormat.Version).Append("\r\n");
            foreach (var a in apps)
                sb.Append(Clean(a.Id)).Append('\t').Append(Clean(a.Name)).Append('\t').Append(Clean(a.Path))
                  .Append('\t').Append(Fmt(a.FirstSeen)).Append("\r\n");
            File.AppendAllText(f, sb.ToString(), new UTF8Encoding(false));
            apps.Clear();
        }

        static string Clean(string s)
        {
            return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
