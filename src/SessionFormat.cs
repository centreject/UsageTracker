using System;
using System.Globalization;
using System.IO;

namespace UsageTracker
{
    /// <summary>
    /// 기록 파일(sessions-YYYY-MM.tsv)의 형식. 쓰는 쪽(SessionWriter)과 읽는 쪽(UsageReader, 복구, 삭제)이
    /// 같은 규칙을 쓰도록 한곳에 둔다. 줄 형식(탭 구분):
    ///   V  1                               형식 버전 (파일 첫 줄)
    ///   A|R|P|U  시작  끝  앱ID             끝난 구간 (자정에서 나눠 씀). P·U의 앱ID는 "-"
    ///   O  종류  시작  앱ID                 진행 중인 구간이 열렸다는 표시 (비정상 종료 복구용)
    ///   F  시각                             저장 시점 표시
    /// 줄 판별(IsClosed 등)은 칸 수와 종류 문자만 본다. 시각을 해석할지는 쓰는 곳마다 다르다.
    /// </summary>
    static class SessionFormat
    {
        public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
        public const int Version = 1;
        public const string FilePattern = "sessions-*.tsv";

        /// <summary>끝난 구간 줄의 종류 문자: 활성·실행·PC 켜짐·PC 사용.</summary>
        public const string ClosedKinds = "ARPU";
        public const string VersionTag = "V", OpenTag = "O", FlushTag = "F";

        public static string FileFor(string dataDir, DateTime t)
        {
            return Path.Combine(dataDir, "sessions-" + t.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".tsv");
        }

        public static string FormatTime(DateTime t) { return t.ToString(TimeFormat, CultureInfo.InvariantCulture); }

        public static bool TryParseTime(string s, out DateTime t)
        {
            return DateTime.TryParseExact(s, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out t);
        }

        /// <summary>끝난 구간 줄: 종류(A·R·P·U), 시작, 끝, 앱ID.</summary>
        public static bool IsClosed(string[] p) { return p.Length == 4 && p[0].Length == 1 && ClosedKinds.IndexOf(p[0][0]) >= 0; }

        /// <summary>열림 표시 줄: O, 종류, 시작, 앱ID.</summary>
        public static bool IsOpen(string[] p) { return p.Length == 4 && p[0] == OpenTag && p[1].Length == 1; }

        /// <summary>저장 시점 줄: F, 시각.</summary>
        public static bool IsFlush(string[] p) { return p.Length == 2 && p[0] == FlushTag; }
    }
}
