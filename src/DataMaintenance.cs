using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UsageTracker
{
    /// <summary>
    /// 기록 삭제·초기화. 파일에 쓰는 쪽이 하나뿐이도록, 기록 모드가 실행 중이면 기록 모드가 직접 한다
    /// (화면 모드는 RecorderLink로 요청만 보낸다). 기록 모드가 꺼져 있으면 화면 모드가 직접 한다.
    /// </summary>
    static class DataMaintenance
    {
        public const int OpDelete = RecorderLink.OpDelete, OpReset = RecorderLink.OpReset;

        /// <summary>해당 앱들의 활성·실행 구간(열림 표시 포함)을 모든 월 파일에서 지운다. 지운 줄 수를 돌려준다.</summary>
        public static int DeleteApps(string dir, ICollection<string> ids)
        {
            var set = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            foreach (var f in Directory.GetFiles(dir, SessionFormat.FilePattern))
            {
                var lines = File.ReadAllLines(f, Encoding.UTF8);
                var sb = new StringBuilder();
                int before = removed;
                foreach (var line in lines)
                {
                    var p = line.Split('\t');
                    bool drop = (SessionFormat.IsClosed(p) && IsAppKind(p[0][0]) && set.Contains(p[3]))
                             || (SessionFormat.IsOpen(p) && IsAppKind(p[1][0]) && set.Contains(p[3]));
                    if (drop) removed++;
                    else sb.Append(line).Append("\r\n");
                }
                if (removed != before) AtomicFile.WriteAllText(f, sb.ToString());
            }
            return removed;
        }

        /// <summary>앱별 구간(활성·실행)인지. PC 켜짐·사용 구간은 앱과 무관해서 지우지 않는다.</summary>
        static bool IsAppKind(char kind) { return kind == 'A' || kind == 'R'; }

        /// <summary>모든 기록과 앱 정보·이름·묶음을 지운다. 설정(settings.ini)은 남긴다.</summary>
        public static void ResetAll(string dir)
        {
            foreach (var f in Directory.GetFiles(dir, SessionFormat.FilePattern)) File.Delete(f);
            foreach (var name in new[] { "appinfo.tsv", "apps.tsv", "groups.tsv" })
            {
                string f = Path.Combine(dir, name);
                if (File.Exists(f)) File.Delete(f);
            }
        }

        /// <summary>기록 모드에 삭제·초기화를 요청한다. 기록 모드가 없거나 응답하지 않으면 직접 처리한다.</summary>
        public static void Request(string dir, int op, ICollection<string> ids)
        {
            if (RecorderLink.SendData(dir, op, ids)) return;   // 기록 모드가 처리함
            if (op == OpDelete) DeleteApps(dir, ids);
            else if (op == OpReset) ResetAll(dir);
        }
    }
}
