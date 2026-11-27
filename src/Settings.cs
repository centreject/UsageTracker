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
            foreach (var line in AtomicFile.ReadAllLinesShared(path))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
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
            var ex = Exclude;
            if (ex.Count > 0) sb.Append("exclude=").Append(string.Join("|", ex.ToArray())).Append("\r\n");
            AtomicFile.WriteAllText(path, sb.ToString());
            IsNew = false;
        }
    }

    static class AtomicFile
    {
        /// <summary>임시 파일에 쓴 뒤 바꿔치기해서, 도중에 꺼져도 파일이 깨지지 않게 한다.</summary>
        public static void WriteAllText(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        /// <summary>다른 프로세스가 덧붙여 쓰는 중에도 충돌 없이 읽는다.</summary>
        public static List<string> ReadAllLinesShared(string path)
        {
            var lines = new List<string>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null) lines.Add(line);
            }
            return lines;
        }
    }
}
