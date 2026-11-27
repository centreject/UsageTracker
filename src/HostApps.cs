using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace UsageTracker
{
    /// <summary>
    /// java·python·node처럼 여러 프로그램을 실행하는 "실행기" exe는 실행 대상(스크립트·jar·메인 클래스)별로 나눈다.
    /// 창 제목은 쓰지 않고 명령줄의 실행 대상 파일명만 쓴다.
    /// </summary>
    static class HostApps
    {
        static readonly HashSet<string> Hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "java.exe", "javaw.exe", "python.exe", "pythonw.exe", "py.exe", "pyw.exe",
            "node.exe", "electron.exe", "wscript.exe", "cscript.exe", "mmc.exe"
        };

        public static bool IsHost(string exeName) { return Hosts.Contains(exeName); }

        /// <summary>
        /// 실행기 exe와 명령줄로 실행 대상을 찾는다. 못 찾으면 null.
        /// 결과의 Item1 = 실행 대상 이름(파일명 또는 메인 클래스·모듈), Item2 = 실행 대상 경로(없으면 null).
        /// </summary>
        public static Tuple<string, string> Target(string exeName, string commandLine)
        {
            var args = Split(commandLine);
            if (args.Count < 2) return null;
            string exe = exeName.ToLowerInvariant();

            if (exe == "java.exe" || exe == "javaw.exe")
            {
                for (int i = 1; i < args.Count; i++)
                {
                    string a = args[i];
                    if (a == "-jar" && i + 1 < args.Count) return File(args[i + 1]);
                    if (a == "-cp" || a == "-classpath" || a == "--class-path" || a == "-p" || a == "--module-path") { i++; continue; }
                    if (a == "-m" || a == "--module") return i + 1 < args.Count ? Named(args[i + 1].Split('/')[0]) : null;
                    if (a.StartsWith("-")) continue;
                    return Named(a);   // 메인 클래스
                }
                return null;
            }
            if (exe.StartsWith("py"))
            {
                for (int i = 1; i < args.Count; i++)
                {
                    string a = args[i];
                    if (a == "-m" && i + 1 < args.Count) return Named(args[i + 1]);
                    if (a == "-c") return null;                       // 한 줄 코드는 구분하지 않음
                    if (a == "-W" || a == "-X") { i++; continue; }
                    if (a.StartsWith("-")) continue;
                    return File(a);
                }
                return null;
            }
            if (exe == "mmc.exe")
            {
                foreach (var a in args) if (a.EndsWith(".msc", StringComparison.OrdinalIgnoreCase)) return File(a);
                return null;
            }
            // node, electron, wscript, cscript: 옵션이 아닌 첫 인자
            bool isNode = exe == "node.exe";
            for (int i = 1; i < args.Count; i++)
            {
                string a = args[i];
                if (isNode)
                {
                    if (a == "-e" || a == "--eval" || a == "-p" || a == "--print") return null;   // 한 줄 코드는 구분하지 않음
                    if (a == "-r" || a == "--require" || a == "--import" || a == "--loader" || a == "--env-file" || a == "--env-file-if-exists" || a == "-C" || a == "--conditions")
                    { i++; continue; }
                }
                if (a.StartsWith("-") || a.StartsWith("//")) continue;
                var f = File(a);
                // npm으로 설치한 CLI는 ...\node_modules\@scope\pkg\cli.js 처럼 파일명이 흔하므로 패키지 이름을 쓴다
                string pkg = f == null ? null : CleanToken(NodePackage(a));
                return pkg != null ? Tuple.Create(pkg, f.Item2) : f;
            }
            return null;
        }

        /// <summary>"...\node_modules\@anthropic-ai\claude-code\cli.js" → "claude-code". node_modules 밖이면 null.</summary>
        public static string NodePackage(string path)
        {
            string p = path.Replace('/', '\\');
            int i = p.LastIndexOf("\\node_modules\\", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            var parts = p.Substring(i + "\\node_modules\\".Length).Split('\\');
            if (parts.Length == 0 || parts[0].Length == 0) return null;
            if (parts[0].StartsWith("@")) return parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
            return parts[0];
        }

        static readonly char[] InvalidPathChars = Path.GetInvalidPathChars();

        static string CleanToken(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s.Trim())
                if (c >= ' ') sb.Append(c);
            string r = sb.ToString().Trim();
            return r.Length > 0 ? r : null;
        }

        static Tuple<string, string> Named(string token)
        {
            string clean = CleanToken(token);
            return clean != null ? Tuple.Create(clean, (string)null) : null;
        }

        static Tuple<string, string> File(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(InvalidPathChars) >= 0) return null;
            try
            {
                string trimmed = path.TrimEnd('\\', '/');
                string name = CleanToken(Path.GetFileName(trimmed));
                if (name == null) return null;
                return Tuple.Create(name, Path.IsPathRooted(trimmed) ? trimmed : null);
            }
            catch (ArgumentException) { return null; }
        }

        /// <summary>Windows 명령줄 규칙(따옴표·역슬래시)대로 인자를 나눈다.</summary>
        public static List<string> Split(string cmd)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(cmd)) return list;
            var cur = new System.Text.StringBuilder();
            bool quoted = false, any = false;
            for (int i = 0; i < cmd.Length; i++)
            {
                char c = cmd[i];
                if (c == '\\')
                {
                    int n = 0;
                    while (i < cmd.Length && cmd[i] == '\\') { n++; i++; }
                    if (i < cmd.Length && cmd[i] == '"') { cur.Append('\\', n / 2); if (n % 2 == 1) cur.Append('"'); else quoted = !quoted; }
                    else { cur.Append('\\', n); i--; }
                    any = true;
                }
                else if (c == '"') { quoted = !quoted; any = true; }
                else if (char.IsWhiteSpace(c) && !quoted) { if (any) { list.Add(cur.ToString()); cur.Clear(); any = false; } }
                else { cur.Append(c); any = true; }
            }
            if (any) list.Add(cur.ToString());
            return list;
        }

        // ---------- 다른 프로세스의 명령줄 (Windows 8.1+: ProcessCommandLineInformation) ----------

        [DllImport("ntdll.dll")]
        static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int retLen);

        public static string CommandLine(uint pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            IntPtr buf = IntPtr.Zero;
            try
            {
                int len = 2048, ret;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    buf = Marshal.AllocHGlobal(len);
                    int status = NtQueryInformationProcess(h, 60 /*ProcessCommandLineInformation*/, buf, len, out ret);
                    if (status == 0)
                    {
                        int bytes = Marshal.ReadInt16(buf) & 0xFFFF;            // UNICODE_STRING.Length
                        IntPtr str = Marshal.ReadIntPtr(buf, IntPtr.Size);       // UNICODE_STRING.Buffer
                        return bytes > 0 ? Marshal.PtrToStringUni(str, bytes / 2) : null;
                    }
                    Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                    if (ret <= len) return null;
                    len = ret;
                }
                return null;
            }
            catch { return null; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                Native.CloseHandle(h);
            }
        }
    }
}
