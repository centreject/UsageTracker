using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace UsageTracker
{
    /// <summary>
    /// update.ini: 업데이트 확인 상태. 마지막 확인 시각과 "이 버전은 다시 알리지 않기"로 고른 버전.
    /// 쓰는 쪽은 업데이트 확인 프로세스(화면 모드)뿐이고, 기록 모드는 언제 확인을 띄울지 정할 때만 읽는다.
    /// </summary>
    sealed class UpdateState
    {
        public static readonly TimeSpan Interval = TimeSpan.FromDays(7);
        public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromDays(1);

        public DateTime LastCheck = DateTime.MinValue;
        public string SkipVersion = "";
        readonly string path;

        UpdateState(string path) { this.path = path; }

        public static UpdateState Load(string dataDir)
        {
            var s = new UpdateState(Path.Combine(dataDir, "update.ini"));
            try
            {
                if (!File.Exists(s.path)) return s;
                foreach (var kv in IniFile.Read(s.path))
                {
                    string k = kv.Key, v = kv.Value;
                    DateTime t;
                    if (k == "lastCheck" && DateTime.TryParseExact(v, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) s.LastCheck = t;
                    else if (k == "skip") s.SkipVersion = v;
                }
            }
            catch (Exception ex) { ErrorLog.Write("update-state", ex); }
            return s;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.Append("lastCheck=").Append(LastCheck.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("\r\n");
            if (SkipVersion.Length > 0) sb.Append("skip=").Append(SkipVersion).Append("\r\n");
            AtomicFile.WriteAllText(path, sb.ToString());
        }

        /// <summary>다음 자동 확인 시각.</summary>
        public DateTime NextCheck { get { return LastCheck == DateTime.MinValue ? DateTime.MinValue : LastCheck + Interval; } }

        /// <summary>새 버전이 이 사용자에게 알릴 대상인지: 지금보다 높고, "다시 알리지 않기"로 고른 버전보다도 높아야 한다.</summary>
        public static bool ShouldNotify(Version current, Version latest, string skip, bool manual)
        {
            if (latest == null || current == null || latest <= current) return false;
            if (manual) return true;
            Version s;
            if (!string.IsNullOrEmpty(skip) && Version.TryParse(skip, out s) && latest <= s) return false;
            return true;
        }

        /// <summary>"v0.9.1", "0.9.1" → Version(0.9.1). 못 읽으면 null.</summary>
        public static Version ParseTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            string t = tag.Trim().TrimStart('v', 'V');
            int dash = t.IndexOf('-');
            if (dash > 0) t = t.Substring(0, dash);
            Version v;
            if (!Version.TryParse(t, out v)) return null;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }
}
