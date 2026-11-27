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
