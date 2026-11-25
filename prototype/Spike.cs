// 1단계 실측 프로토타입: 기록 모드의 최소 기능만 담는다.
// - 활성 창 감지 (SetWinEventHook, 이벤트 방식)
// - 트레이 아이콘 (Shell_NotifyIcon 직접 호출, WinForms 미사용)
// - 1분 주기 작업: 자리 비움(GetLastInputInfo), 소리 재생(WASAPI 세션), 전체 화면, 보이는 창 목록
// - 주기마다 EmptyWorkingSet
// 실행: Spike.exe [--interval=초]   (기본 60초)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

static class Spike
{
    // ---------- 상태 ----------
    static IntPtr hwndMain;
    static WndProc wndProcDelegate;          // GC 방지용 보관
    static WinEventProc winEventDelegate;    // GC 방지용 보관
    static IntPtr winEventHook;
    static uint ownPid;
    static uint taskbarCreatedMsg;
    static int intervalSec = 60;

    static string currentApp;
    static long currentStart;                // Stopwatch 타임스탬프
    static readonly Dictionary<string, long> activeMs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> nameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static int foregroundEvents;
    static string logPath;

    const uint WM_APP_TRAY = 0x8000 + 1;
    const uint TRAY_ID = 1;

    [STAThread]
    static int Main(string[] args)
    {
        foreach (var a in args)
            if (a.StartsWith("--interval=")) intervalSec = Math.Max(1, int.Parse(a.Substring(11)));

        // 계정당 하나만 실행
        bool created;
        var mutex = new System.Threading.Mutex(true, "Local\\UsageTrackerSpike", out created);
        if (!created) return 1;

        ownPid = (uint)GetCurrentProcessId();
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageTracker");
        Directory.CreateDirectory(dir);
        logPath = Path.Combine(dir, "spike.log");

        CreateHiddenWindow();
        taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
        AddTrayIcon();

        winEventDelegate = OnWinEvent;
        winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        SwitchTo(GetForegroundWindow());       // 시작 시 이미 떠 있는 창부터 기록
        SetTimer(hwndMain, (IntPtr)1, (uint)(intervalSec * 1000), IntPtr.Zero);
        Log("start interval=" + intervalSec + "s");
        TrimMemory();

        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        SwitchTo(IntPtr.Zero);
        var sb = new StringBuilder("totals:");
        foreach (var kv in activeMs) sb.Append(" [").Append(kv.Key).Append(' ').Append(kv.Value / 1000).Append("s]");
        Log(sb.ToString());
        UnhookWinEvent(winEventHook);
        RemoveTrayIcon();
        GC.KeepAlive(mutex);
        return 0;
    }

    // ---------- 활성 창 ----------
    static void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        foregroundEvents++;
        SwitchTo(hwnd);
    }

    static void SwitchTo(IntPtr hwnd)
    {
        long now = Stopwatch.GetTimestamp();
        if (currentApp != null)
        {
            long ms = (now - currentStart) * 1000 / Stopwatch.Frequency;
            long prev;
            activeMs.TryGetValue(currentApp, out prev);
            activeMs[currentApp] = prev + ms;
        }
        currentApp = hwnd == IntPtr.Zero ? null : ResolveApp(hwnd);
        currentStart = now;
    }

    static string ResolveApp(IntPtr hwnd)
    {
        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0 || pid == ownPid) return null;
        string path = GetProcessPath(pid);
        if (path == null) return null;

        // UWP: ApplicationFrameHost 안의 실제 앱 프로세스를 찾는다
        if (Path.GetFileName(path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            uint childPid = 0;
            EnumChildWindows(hwnd, (h, l) =>
            {
                uint p; GetWindowThreadProcessId(h, out p);
                if (p != pid) { childPid = p; return false; }
                return true;
            }, IntPtr.Zero);
            if (childPid != 0)
            {
                string cp = GetProcessPath(childPid);
                if (cp != null) path = cp;
            }
        }
        return DisplayName(path);
    }

    static string DisplayName(string path)
    {
        string name;
        if (nameCache.TryGetValue(path, out name)) return name;
        name = null;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            name = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription.Trim()
                 : !string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName.Trim() : null;
        }
        catch { }
        if (name == null) name = Path.GetFileName(path);
        nameCache[path] = name;
        return name;
    }

    static string GetProcessPath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    // ---------- 1분 주기 작업 ----------
    static void OnTick()
    {
        var sw = Stopwatch.StartNew();

        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
        GetLastInputInfo(ref lii);
        uint idleSec = ((uint)Environment.TickCount - lii.dwTime) / 1000;

        var audioPids = AudioActivePids();
        bool fullscreen = IsForegroundFullscreen();
        int visibleApps = CountVisibleAppWindows();

        sw.Stop();
        Log(string.Format("tick idle={0}s audio={1} fullscreen={2} visibleWindowPids={3} fgEvents={4} current={5} tickCost={6:0.00}ms",
            idleSec, audioPids.Count, fullscreen, visibleApps, foregroundEvents, currentApp ?? "-", sw.Elapsed.TotalMilliseconds));
        TrimMemory();
    }

    static bool IsForegroundFullscreen()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
        RECT r;
        if (!GetWindowRect(fg, out r)) return false;
        IntPtr mon = MonitorFromWindow(fg, 2 /*MONITOR_DEFAULTTONEAREST*/);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
               r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    static int CountVisibleAppWindows()
    {
        var pids = new HashSet<uint>();
        EnumWindows((h, l) =>
        {
            if (IsWindowVisible(h) && GetWindow(h, 4 /*GW_OWNER*/) == IntPtr.Zero && GetWindowTextLength(h) > 0)
            {
                uint p; GetWindowThreadProcessId(h, out p);
                pids.Add(p);
            }
            return true;
        }, IntPtr.Zero);
        return pids.Count;
    }

    // ---------- 소리 재생 감지 (WASAPI 세션) ----------
    static HashSet<uint> AudioActivePids()
    {
        var result = new HashSet<uint>();
        object enumObj = null; IMMDeviceCollection devices = null;
        try
        {
            enumObj = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
            var enumerator = (IMMDeviceEnumerator)enumObj;
            enumerator.EnumAudioEndpoints(0 /*eRender*/, 1 /*DEVICE_STATE_ACTIVE*/, out devices);
            uint count; devices.GetCount(out count);
            var iid = typeof(IAudioSessionManager2).GUID;
            for (uint i = 0; i < count; i++)
            {
                IMMDevice dev = null; object mgrObj = null; IAudioSessionEnumerator sessions = null;
                try
                {
                    devices.Item(i, out dev);
                    dev.Activate(ref iid, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out mgrObj);
                    ((IAudioSessionManager2)mgrObj).GetSessionEnumerator(out sessions);
                    int n; sessions.GetCount(out n);
                    for (int s = 0; s < n; s++)
                    {
                        object ctlObj = null;
                        try
                        {
                            sessions.GetSession(s, out ctlObj);
                            var ctl = (IAudioSessionControl2)ctlObj;
                            int state; ctl.GetState(out state);
                            if (state == 1 /*AudioSessionStateActive*/)
                            {
                                uint pid; ctl.GetProcessId(out pid);
                                if (pid != 0) result.Add(pid);
                            }
                        }
                        finally { Release(ctlObj); }
                    }
                }
                catch (Exception ex) { Log("audio device error: " + ex.Message); }
                finally { Release(sessions); Release(mgrObj); Release(dev); }
            }
        }
        catch (Exception ex) { Log("audio error: " + ex.Message); }
        finally { Release(devices); Release(enumObj); }
        return result;
    }

    static void Release(object o) { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }

    // ---------- 트레이 ----------
    static void CreateHiddenWindow()
    {
        wndProcDelegate = WindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX)),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProcDelegate),
            hInstance = GetModuleHandle(null),
            lpszClassName = "UsageTrackerSpikeWnd"
        };
        RegisterClassEx(ref wc);
        hwndMain = CreateWindowEx(0, wc.lpszClassName, "UsageTrackerSpike", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    static NOTIFYICONDATA TrayData()
    {
        return new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = hwndMain,
            uID = TRAY_ID,
            uFlags = 0x1 | 0x2 | 0x4, // NIF_MESSAGE | NIF_ICON | NIF_TIP
            uCallbackMessage = WM_APP_TRAY,
            hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512 /*IDI_APPLICATION*/),
            szTip = "사용 시간 기록 (프로토타입)"
        };
    }

    static void AddTrayIcon() { var d = TrayData(); Shell_NotifyIcon(0 /*NIM_ADD*/, ref d); }
    static void RemoveTrayIcon() { var d = TrayData(); Shell_NotifyIcon(2 /*NIM_DELETE*/, ref d); }

    static void ShowTrayMenu()
    {
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, 0, (UIntPtr)1, "종료");
        POINT pt; GetCursorPos(out pt);
        SetForegroundWindow(hwndMain);
        uint cmd = TrackPopupMenu(menu, 0x0100 /*TPM_RETURNCMD*/ | 0x0020 /*TPM_BOTTOMALIGN*/, pt.X, pt.Y, 0, hwndMain, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd == 1) PostQuitMessage(0);
    }

    static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == 0x0113 /*WM_TIMER*/) { OnTick(); return IntPtr.Zero; }
        if (msg == WM_APP_TRAY)
        {
            int ev = lParam.ToInt32() & 0xFFFF;
            if (ev == 0x0205 /*WM_RBUTTONUP*/) ShowTrayMenu();
            return IntPtr.Zero;
        }
        if (msg == taskbarCreatedMsg && taskbarCreatedMsg != 0) { AddTrayIcon(); return IntPtr.Zero; }
        if (msg == 0x0011 /*WM_QUERYENDSESSION*/) return (IntPtr)1;
        if (msg == 0x0016 /*WM_ENDSESSION*/) { if (wParam != IntPtr.Zero) PostQuitMessage(0); return IntPtr.Zero; }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ---------- 기타 ----------
    static void TrimMemory()
    {
        EmptyWorkingSet(GetCurrentProcess());
    }

    static void Log(string line)
    {
        try { File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + Environment.NewLine); }
        catch { }
    }

    // ---------- Win32 ----------
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO lii);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern IntPtr LoadIcon(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int r, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern int GetCurrentProcessId();
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr process);
}

// ---------- WASAPI COM 인터페이스 (vtable 순서 그대로) ----------
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
}

[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    // IAudioSessionControl
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out IntPtr name);
    [PreserveSig] int SetDisplayName(IntPtr name, IntPtr ctx);
    [PreserveSig] int GetIconPath(out IntPtr path);
    [PreserveSig] int SetIconPath(IntPtr path, IntPtr ctx);
    [PreserveSig] int GetGroupingParam(out Guid param);
    [PreserveSig] int SetGroupingParam(IntPtr param, IntPtr ctx);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    // IAudioSessionControl2
    [PreserveSig] int GetSessionIdentifier(out IntPtr id);
    [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
    [PreserveSig] int GetProcessId(out uint pid);
}
