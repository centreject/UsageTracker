using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UsageTracker
{
    /// <summary>데이터 폴더 파일 읽기·쓰기 (기록 모드와 화면 모드가 같은 파일을 동시에 다룬다).</summary>
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
            return new List<string>(ReadLinesShared(path));
        }

        /// <summary>ReadAllLinesShared와 같지만 한 줄씩 읽는다 (큰 기록 파일용).</summary>
        public static IEnumerable<string> ReadLinesShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null) yield return line;
            }
        }
    }

    /// <summary>key=value 형식 파일 (settings.ini, ui.ini, update.ini).</summary>
    static class IniFile
    {
        /// <summary>
        /// 파일 순서대로 (키, 값) 쌍을 돌려준다. '='가 없거나 맨 앞인 줄은 건너뛰고, 키와 값의 앞뒤 공백은 지운다.
        /// 같은 키가 여러 번 나오면 모두 돌려준다 (어느 것을 쓸지는 부르는 쪽이 정한다).
        /// </summary>
        public static List<KeyValuePair<string, string>> Read(string path)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (var line in AtomicFile.ReadAllLinesShared(path))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) list.Add(new KeyValuePair<string, string>(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim()));
            }
            return list;
        }

        /// <summary>Read 결과를 사전으로 (키 대소문자 무시, 같은 키는 뒤의 값).</summary>
        public static Dictionary<string, string> ReadDictionary(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Read(path)) d[kv.Key] = kv.Value;
            return d;
        }
    }
}
