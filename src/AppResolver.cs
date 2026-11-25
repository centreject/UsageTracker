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
        readonly Dictionary<string, AppInfo> known;                       // ID → 정보 (appinfo.tsv)
        readonly Dictionary<string, AppInfo> byPath = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
        readonly List<AppInfo> newApps = new List<AppInfo>();

        public AppResolver(Dictionary<string, AppInfo> knownApps)
        {
            known = knownApps;
        }

        /// <summary>아직 appinfo.tsv에 저장하지 않은 새 앱. 저장 후 비운다.</summary>
        public List<AppInfo> NewApps { get { return newApps; } }

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
            if (path == null) return null;
            string exe = System.IO.Path.GetFileName(path);
            if (ShellExes.Contains(exe)) return null;
            if (exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && ExplorerShellClasses.Contains(cls)) return null;
            return Register(path);
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
            if (path == null) return null;
            string exe = System.IO.Path.GetFileName(path);
            if (ShellExes.Contains(exe) || exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) return null;
            return Register(path);
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

        string Register(string path)
        {
            AppInfo a;
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

        static string ReadName(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var vi = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrWhiteSpace(vi.FileDescription)) return vi.FileDescription.Trim();
                    if (!string.IsNullOrWhiteSpace(vi.ProductName)) return vi.ProductName.Trim();
                }
            }
            catch { }
            return System.IO.Path.GetFileName(path);
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
