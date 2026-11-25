using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace UsageTracker
{
    sealed class AppInfo
    {
        public string Id;        // exe 파일명 소문자 (예: chrome.exe). 바탕화면은 "::desktop"
        public string Name;      // 원래 이름 (FileDescription → ProductName → exe 파일명)
        public string Path;
        public DateTime FirstSeen;
    }

    /// <summary>창·프로세스를 앱 ID로 바꾼다. 앱 정보(이름)는 ID당 한 번만 읽는다.</summary>
    sealed class AppResolver
    {
        public const string DesktopId = "::desktop";

        // 집계에서 빼는 시스템 화면 프로세스
        static readonly HashSet<string> ShellExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "lockapp.exe", "startmenuexperiencehost.exe", "searchhost.exe", "searchapp.exe",
            "shellexperiencehost.exe", "shellhost.exe", "textinputhost.exe", "logonui.exe",
            "consent.exe", "dwm.exe", "csrss.exe", "winlogon.exe"
        };

        // explorer.exe 창 중 앱으로 치지 않는 것 (작업 표시줄, 작업 전환 등)
        static readonly HashSet<string> ExplorerShellClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
            "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow",
            "MultitaskingViewFrame", "ForegroundStaging", "TaskListThumbnailWnd", "#32768"
        };

        readonly uint ownPid = (uint)Native.GetCurrentProcessId();
        // 화면 모드는 같은 exe의 다른 프로세스이므로 pid가 아니라 exe 경로로 뺀다
        readonly string ownExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
        readonly Dictionary<string, AppInfo> known;                       // ID → 정보 (appinfo.tsv)
        readonly Dictionary<string, AppInfo> byPath = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
        readonly List<AppInfo> newApps = new List<AppInfo>();

        public AppResolver(Dictionary<string, AppInfo> knownApps)
        {
            known = knownApps;
        }

        /// <summary>아직 appinfo.tsv에 저장하지 않은 새 앱. 저장 후 비운다.</summary>
        public List<AppInfo> NewApps { get { return newApps; } }

        public void Reset()
        {
            known.Clear();
            byPath.Clear();
            newApps.Clear();
        }

        public string NameOf(string id)
        {
            AppInfo a;
            return id != null && known.TryGetValue(id, out a) ? a.Name : id;
        }

        /// <summary>맨 앞 창의 앱 ID. 집계 제외 대상이면 null.</summary>
        public string ForegroundApp(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            uint pid = Native.WindowPid(hwnd);
            if (pid == 0 || pid == ownPid) return null;

            string cls = Native.ClassName(hwnd);
            if (cls == "Progman" || cls == "WorkerW") return Desktop();

            string path = PathOf(pid, hwnd, cls);
            if (path == null || IsSelf(path)) return null;
            string exe = System.IO.Path.GetFileName(path);
            if (ShellExes.Contains(exe)) return null;
            if (exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && ExplorerShellClasses.Contains(cls)) return null;
            return Register(path, pid);
        }

        /// <summary>실행 시간 집계용 앱 ID. explorer.exe·시스템 화면·자기 자신은 null.</summary>
        public string RunningApp(uint pid, IntPtr hwnd, Dictionary<uint, string> pathCache)
        {
            if (pid == 0 || pid == ownPid) return null;
            string path;
            if (!pathCache.TryGetValue(pid, out path))
            {
                path = ProcessPath(pid);
                pathCache[pid] = path;
            }
            if (path == null || IsSelf(path)) return null;
            string exe = System.IO.Path.GetFileName(path);
            if (ShellExes.Contains(exe) || exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) return null;
            return Register(path, pid);
        }

        /// <summary>
        /// 터미널에서 실행 중인 CLI 도구의 앱 ID. 창 있는 앱과 섞이지 않게 "cli:" 접두어를 붙인다
        /// (예: Claude CLI의 claude.exe와 Claude 데스크톱 앱의 claude.exe를 구분).
        /// </summary>
        public string CliApp(uint pid)
        {
            if (pid == 0 || pid == ownPid) return null;
            string path = ProcessPath(pid);
            if (path == null || IsSelf(path)) return null;
            string exeName = System.IO.Path.GetFileName(path);
            string id, name;
            if (HostApps.IsHost(exeName))
            {
                var target = HostApps.Target(exeName, HostApps.CommandLine(pid));
                string host = ReadName(path);
                if (target != null)
                {
                    id = "cli:" + exeName.ToLowerInvariant() + ":" + target.Item1.ToLowerInvariant();
                    string shown = System.IO.Path.GetExtension(target.Item1).Length > 0 && target.Item2 != null
                        ? System.IO.Path.GetFileNameWithoutExtension(target.Item1) : target.Item1;
                    name = shown + " (" + host + ")";
                }
                else { id = "cli:" + exeName.ToLowerInvariant(); name = host + " (CLI)"; }
            }
            else
            {
                id = "cli:" + exeName.ToLowerInvariant();
                name = ReadName(path) + " (CLI)";
            }
            AppInfo a;
            if (!known.TryGetValue(id, out a))
            {
                a = new AppInfo { Id = id, Name = name, Path = path, FirstSeen = DateTime.Now };
                known[id] = a;
                newApps.Add(a);
            }
            return a.Id;
        }

        bool IsSelf(string path)
        {
            return string.Equals(path, ownExe, StringComparison.OrdinalIgnoreCase);
        }

        string Desktop()
        {
            if (!known.ContainsKey(DesktopId))
            {
                var a = new AppInfo { Id = DesktopId, Name = "바탕화면", Path = "", FirstSeen = DateTime.Now };
                known[DesktopId] = a;
                newApps.Add(a);
            }
            return DesktopId;
        }

        string Register(string path, uint pid)
        {
            AppInfo a;
            string exeName = System.IO.Path.GetFileName(path);
            if (pid != 0 && HostApps.IsHost(exeName))
            {
                string hostId = RegisterHost(path, exeName, pid);
                if (hostId != null) return hostId;
            }
            if (byPath.TryGetValue(path, out a)) return a.Id;

            string id = System.IO.Path.GetFileName(path).ToLowerInvariant();
            if (!known.TryGetValue(id, out a))
            {
                a = new AppInfo { Id = id, Name = ReadName(path), Path = path, FirstSeen = DateTime.Now };
                known[id] = a;
                newApps.Add(a);
            }
            byPath[path] = a;
            return a.Id;
        }

        /// <summary>실행기 exe(java·python 등)는 실행 대상별 ID: "python.exe:bot.py". 대상을 못 찾으면 null (exe 단위로 기록).</summary>
        string RegisterHost(string hostPath, string exeName, uint pid)
        {
            var target = HostApps.Target(exeName, HostApps.CommandLine(pid));
            if (target == null) return null;
            string id = exeName.ToLowerInvariant() + ":" + target.Item1.ToLowerInvariant();
            AppInfo a;
            if (!known.TryGetValue(id, out a))
            {
                string host = ReadName(hostPath);
                string shown = System.IO.Path.GetExtension(target.Item1).Length > 0 && target.Item2 != null
                    ? System.IO.Path.GetFileNameWithoutExtension(target.Item1) : target.Item1;
                a = new AppInfo { Id = id, Name = shown + " (" + host + ")", Path = target.Item2 ?? hostPath, FirstSeen = DateTime.Now };
                known[id] = a;
                newApps.Add(a);
            }
            return a.Id;
        }

        /// <summary>
        /// 원래 이름: 스토어(패키지) 앱은 매니페스트의 표시 이름(현지화) → FileDescription → ProductName → exe 파일명.
        /// FileDescription이 "Notepad.exe"처럼 파일명 모양이면 건너뛴다.
        /// </summary>
        static string ReadName(string path)
        {
            try
            {
                string pkg = PackagedAppName(path);
                if (!string.IsNullOrWhiteSpace(pkg)) return pkg.Trim();
                if (File.Exists(path))
                {
                    var vi = FileVersionInfo.GetVersionInfo(path);
                    string d = vi.FileDescription;
                    if (!string.IsNullOrWhiteSpace(d) && !d.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return d.Trim();
                    if (!string.IsNullOrWhiteSpace(vi.ProductName)) return vi.ProductName.Trim();
                }
            }
            catch { }
            return System.IO.Path.GetFileName(path);
        }

        /// <summary>WindowsApps 아래 패키지 앱이면 AppxManifest.xml의 DisplayName을 현지화해서 돌려준다.</summary>
        static string PackagedAppName(string path)
        {
            const string marker = @"\WindowsApps\";
            int i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int start = i + marker.Length;
            int end = path.IndexOf('\\', start);
            if (end < 0) return null;
            string fullName = path.Substring(start, end - start);           // 패키지 전체 이름 (폴더명)
            string manifest = System.IO.Path.Combine(path.Substring(0, end), "AppxManifest.xml");
            if (!File.Exists(manifest)) return null;

            string xml = File.ReadAllText(manifest);
            string identity = Attr(xml, "<Identity", "Name");
            string display = Element(xml, "<Properties>", "DisplayName");
            if (string.IsNullOrEmpty(display)) return null;
            if (!display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)) return display;
            if (identity == null) return null;

            string res = display.Substring("ms-resource:".Length);
            string uri = res.StartsWith("//") ? "ms-resource:" + res
                       : res.StartsWith("/") ? "ms-resource://" + identity + res
                       : res.Contains("/") ? "ms-resource://" + identity + "/" + res
                       : "ms-resource://" + identity + "/Resources/" + res;
            var sb = new StringBuilder(512);
            if (SHLoadIndirectString("@{" + fullName + "?" + uri + "}", sb, (uint)sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0)
                return sb.ToString();
            return null;
        }

        static string Attr(string xml, string tag, string attr)
        {
            int t = xml.IndexOf(tag, StringComparison.Ordinal);
            if (t < 0) return null;
            int close = xml.IndexOf('>', t);
            int a = xml.IndexOf(attr + "=\"", t, StringComparison.Ordinal);
            if (a < 0 || a > close) return null;
            a += attr.Length + 2;
            int e = xml.IndexOf('"', a);
            return e > a ? xml.Substring(a, e - a) : null;
        }

        static string Element(string xml, string parentTag, string name)
        {
            int p = xml.IndexOf(parentTag, StringComparison.Ordinal);
            if (p < 0) return null;
            int s = xml.IndexOf("<" + name + ">", p, StringComparison.Ordinal);
            if (s < 0) return null;
            s += name.Length + 2;
            int e = xml.IndexOf("</" + name + ">", s, StringComparison.Ordinal);
            return e > s ? xml.Substring(s, e - s).Trim() : null;
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int SHLoadIndirectString(string source, StringBuilder outBuf, uint cch, IntPtr reserved);

        /// <summary>실행 시간에 넣지 않는 앱 (바탕화면, 탐색기 셸).</summary>
        public static bool CountsAsRunning(string id)
        {
            return id != null && id != DesktopId && !id.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>창의 실제 프로세스 경로. UWP(ApplicationFrameHost)는 안쪽 앱 프로세스를 찾는다.</summary>
        string PathOf(uint pid, IntPtr hwnd, string cls)
        {
            if (cls == "ApplicationFrameWindow")
            {
                uint inner = InnerUwpPid(hwnd, pid);
                if (inner != 0) pid = inner;
            }
            return ProcessPath(pid);
        }

        public static uint InnerUwpPid(IntPtr frame, uint framePid)
        {
            uint found = 0;
            Native.EnumChildWindows(frame, (h, l) =>
            {
                uint p = Native.WindowPid(h);
                if (p != 0 && p != framePid) { found = p; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>프로세스 exe 경로. 열 수 없는 보호 프로세스는 exe 파일명만 돌려준다.</summary>
        public static string ProcessPath(uint pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (Native.QueryFullProcessImageName(h, 0, sb, ref size)) return sb.ToString();
                }
                finally { Native.CloseHandle(h); }
            }
            return ExeNameFromSnapshot(pid);
        }

        static string ExeNameFromSnapshot(uint pid)
        {
            IntPtr snap = Native.CreateToolhelp32Snapshot(0x2 /*TH32CS_SNAPPROCESS*/, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return null;
            try
            {
                var e = new Native.PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(Native.PROCESSENTRY32)) };
                if (!Native.Process32First(snap, ref e)) return null;
                do { if (e.th32ProcessID == pid) return e.szExeFile; }
                while (Native.Process32Next(snap, ref e));
                return null;
            }
            finally { Native.CloseHandle(snap); }
        }
    }
}
