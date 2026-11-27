using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UsageTracker
{
    /// <summary>settings.ini (key=value) 읽기·쓰기.</summary>
    sealed class Settings
    {
        readonly string path;
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool IsNew { get; private set; }

        public Settings(string dataDir)
        {
            path = Path.Combine(dataDir, "settings.ini");
            IsNew = !File.Exists(path);
            if (IsNew) return;
            foreach (var kv in IniFile.Read(path)) values[kv.Key] = kv.Value;
        }

        public int IdleMinutes
        {
            get { return GetInt("idleMinutes", 5, 1, 120); }
            set { values["idleMinutes"] = value.ToString(); }
        }

        public bool AutoStart
        {
            get { return GetInt("autoStart", 1, 0, 1) == 1; }
            set { values["autoStart"] = value ? "1" : "0"; }
        }

        /// <summary>새 버전 알림 (일주일에 한 번 GitHub에서 버전만 확인).</summary>
        public bool UpdateCheck
        {
            get { return GetInt("updateCheck", 1, 0, 1) == 1; }
            set { values["updateCheck"] = value ? "1" : "0"; }
        }

        /// <summary>기록하지 않을 앱 ID (소문자, 예: game.exe, cli:node.exe). 앞으로만 적용된다.</summary>
        public List<string> Exclude
        {
            get
            {
                string s;
                var list = new List<string>();
                if (values.TryGetValue("exclude", out s))
                    foreach (var part in s.Split('|'))
                    {
                        string id = part.Trim().ToLowerInvariant();
                        if (id.Length > 0 && !list.Contains(id)) list.Add(id);
                    }
                return list;
            }
            set
            {
                var clean = new List<string>();
                if (value != null)
                    foreach (var v in value)
                    {
                        string id = (v ?? "").Trim().ToLowerInvariant().Replace("|", "");
                        if (id.Length > 0 && !clean.Contains(id)) clean.Add(id);
                    }
                values["exclude"] = string.Join("|", clean.ToArray());
            }
        }

        int GetInt(string key, int def, int min, int max)
        {
            string s; int v;
            if (values.TryGetValue(key, out s) && int.TryParse(s, out v)) return Math.Max(min, Math.Min(max, v));
            return def;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.Append("idleMinutes=").Append(IdleMinutes).Append("\r\n");
            sb.Append("autoStart=").Append(AutoStart ? 1 : 0).Append("\r\n");
            sb.Append("updateCheck=").Append(UpdateCheck ? 1 : 0).Append("\r\n");
            var ex = Exclude;
            if (ex.Count > 0) sb.Append("exclude=").Append(string.Join("|", ex.ToArray())).Append("\r\n");
            AtomicFile.WriteAllText(path, sb.ToString());
            IsNew = false;
        }
    }
}
