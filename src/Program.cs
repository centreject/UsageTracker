using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace UsageTracker
{
    /// <summary>
    /// 진입점. 기본은 기록 모드(트레이 상주). 옵션:
    ///   --ui              화면 모드 (통계 창). 트레이 더블클릭·"열기"가 이것으로 실행한다
    ///   --data=폴더       데이터 폴더 지정 (개발·시험용. 지정하면 자동 시작을 건드리지 않는다)
    ///   --tick=초 --flush=초 --idle=초   주기·자리 비움 기준을 바꾼다 (시험용)
    /// </summary>
    static class Program
    {
        static readonly IntPtr TimerTickId = (IntPtr)1, TimerIdleId = (IntPtr)2, TimerFlushId = (IntPtr)3, TimerPauseId = (IntPtr)4;
        const uint IdlePollMs = 2000;

        static IntPtr hwnd;
        static Native.WndProc wndProc;           // GC 방지
        static Native.WinEventProc winEvent;     // GC 방지
        static IntPtr hook;
        static uint taskbarCreated;
        static Tracker tracker;
        static Tray tray;
        static Settings settings;
        static bool manageAutoStart;
        static string exePath;
        static uint tickMs = 60000, flushMs = 300000;
        static bool idleTimerOn;
        static int idleSecOverride = -1;

        [STAThread]
        static int Main(string[] args)
        {
            string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsageTracker");
            manageAutoStart = true;
            idleSecOverride = -1;
            bool ui = false;
            foreach (var a in args)
            {
                if (a == "--ui") ui = true;
                else if (a.StartsWith("--probe="))
                {
                    // 진단: 소리 세션 상태와 지금 소리를 내는 pid, 실행 중 프로세스의 앱 ID(실행기 구분 포함)
                    var lines = new System.Collections.Generic.List<string>(Audio.Describe());
                    lines.Add("audible pids: " + string.Join(",", Audio.ActivePids()));
                    var resolver = new AppResolver(new System.Collections.Generic.Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase));
                    var cache = new System.Collections.Generic.Dictionary<uint, string>();
                    var env = new RealEnv(resolver);
                    foreach (var id in env.VisibleApps()) lines.Add("visible app: " + id + " = " + resolver.NameOf(id));
                    lines.Add("foreground app: " + resolver.ForegroundApp(Native.GetForegroundWindow()));
                    File.WriteAllLines(a.Substring(8), lines.ToArray(), new System.Text.UTF8Encoding(true));
                    return 0;
                }
                else if (a.StartsWith("--bench="))
                {
                    SelfTest.Bench(a.Substring(8));
                    return 0;
                }
                else if (a.StartsWith("--selftest"))
                {
                    string report = a.StartsWith("--selftest=") ? a.Substring(11)
                        : Path.Combine(Path.GetTempPath(), "UsageTracker-selftest.txt");
                    return SelfTest.Run(report);
                }
                if (a.StartsWith("--data=")) { dataDir = Path.GetFullPath(a.Substring(7)); manageAutoStart = false; }
                else if (a.StartsWith("--tick=")) tickMs = (uint)Math.Max(1, int.Parse(a.Substring(7))) * 1000;
                else if (a.StartsWith("--flush=")) flushMs = (uint)Math.Max(1, int.Parse(a.Substring(8))) * 1000;
                else if (a.StartsWith("--idle=")) idleSecOverride = Math.Max(0, int.Parse(a.Substring(7)));   // 0 = 자리 비움 판정 끔 (시험용)
            }
            Program.dataDir = dataDir;

            if (ui)
            {
                // 화면 모드: WinForms는 이 경로에서만 불러온다 (기록 모드 메모리에 영향 없음)
                Directory.CreateDirectory(dataDir);
                ErrorLog.Init(dataDir);
                return RunUi(dataDir, args);
            }

            bool created;
            using (var mutex = new Mutex(true, "Local\\UsageTracker.Recorder", out created))
            {
                if (!created) return 1;   // 이미 실행 중
                Directory.CreateDirectory(dataDir);
                ErrorLog.Init(dataDir);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => ErrorLog.Write("unhandled", e.ExceptionObject as Exception ?? new Exception("unknown"));

                exePath = Assembly.GetExecutingAssembly().Location;
                settings = new Settings(dataDir);
                if (settings.IsNew) settings.Save();
                if (manageAutoStart)
                {
                    AutoStart.Apply(settings.AutoStart, exePath);
                    StartMenu.Ensure(exePath);   // 시작 메뉴에서 통계 창을 열 수 있게
                }

                var writer = new SessionWriter(dataDir);
                var resolver = new AppResolver(writer.LoadAppInfo());
                tracker = new Tracker(new RealEnv(resolver), writer);
                tracker.IdleThresholdMs = idleSecOverride == 0 ? int.MaxValue
                    : (idleSecOverride > 0 ? idleSecOverride : settings.IdleMinutes * 60) * 1000;
                tracker.TickIntervalMs = (int)tickMs;

                CreateWindow();
                taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
                tray = new Tray(hwnd);
                tray.Add("사용 시간 기록");
                Native.WTSRegisterSessionNotification(hwnd, 0 /*NOTIFY_FOR_THIS_SESSION*/);

                winEvent = OnWinEvent;
                hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                    winEvent, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

                Safe("start", tracker.Start);
                UpdateTip();
                Native.SetTimer(hwnd, TimerTickId, tickMs, IntPtr.Zero);
                Native.SetTimer(hwnd, TimerFlushId, flushMs, IntPtr.Zero);
                Native.TrimMemory();

                Native.MSG msg;
                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }

                Safe("shutdown", tracker.Shutdown);
                Native.UnhookWinEvent(hook);
                Native.WTSUnRegisterSessionNotification(hwnd);
                tray.Remove();
            }
            return 0;
        }

        static string dataDir;

        // 별도 메서드로 둬서, 기록 모드에서는 화면 모드 코드(WinForms)를 JIT하지도 불러오지도 않게 한다
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int RunUi(string dir, string[] args)
        {
            return Ui.UiApp.Run(dir, args);
        }

        /// <summary>트레이에서 화면 모드를 연다 (같은 exe를 --ui로 실행).</summary>
        static void LaunchUi(string extra = "")
        {
            string args = "--ui" + extra + (manageAutoStart ? "" : " \"--data=" + dataDir + "\"");
            Native.ShellExecute(IntPtr.Zero, "open", exePath, args, null, 1);
        }

        static void CreateWindow()
        {
            wndProc = WindowProc;
            var wc = new Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf(typeof(Native.WNDCLASSEX)),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
                hInstance = Native.GetModuleHandle(null),
                lpszClassName = "UsageTracker.Recorder"
            };
            Native.RegisterClassEx(ref wc);
            // 보이지 않는 일반 창 (TaskbarCreated 방송을 받으려면 메시지 전용 창이면 안 된다)
            hwnd = Native.CreateWindowEx(0, wc.lpszClassName, DataMaintenance.RecorderTitle(dataDir), 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        }

        static void OnWinEvent(IntPtr h, uint evt, IntPtr window, int idObject, int idChild, uint thread, uint time)
        {
            Safe("foreground", () => { tracker.OnForeground(window); UpdateTip(); });
        }

        static string lastTip;

        /// <summary>상태 문구가 바뀌었을 때만 트레이 툴팁을 고친다.</summary>
        static void UpdateTip()
        {
            string tip = "사용 시간 기록\n" + tracker.StatusText();
            if (tip == lastTip) return;
            lastTip = tip;
            tray.Update(tip);
        }

        static IntPtr WindowProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case Native.WM_TIMER:
                        OnTimer(wParam);
                        UpdateTip();
                        return IntPtr.Zero;

                    case Tray.CallbackMessage:
                        int ev = lParam.ToInt32() & 0xFFFF;
                        if (ev == Native.WM_RBUTTONUP) { OnMenu(); UpdateTip(); }
                        else if (ev == Native.WM_LBUTTONDBLCLK) LaunchUi();
                        return IntPtr.Zero;

                    case Native.WM_WTSSESSION_CHANGE:
                        int code = wParam.ToInt32();
                        if (code == Native.WTS_SESSION_LOCK || code == Native.WTS_CONSOLE_DISCONNECT || code == Native.WTS_REMOTE_DISCONNECT)
                            tracker.SetLocked(true);
                        else if (code == Native.WTS_SESSION_UNLOCK || code == Native.WTS_CONSOLE_CONNECT || code == Native.WTS_REMOTE_CONNECT)
                            tracker.SetLocked(false);
                        UpdateIdleTimer();
                        UpdateTip();
                        return IntPtr.Zero;

                    case Native.WM_POWERBROADCAST:
                        int pbt = wParam.ToInt32();
                        if (pbt == Native.PBT_APMSUSPEND) tracker.SetSuspended(true);
                        else if (pbt == Native.PBT_APMRESUMEAUTOMATIC || pbt == Native.PBT_APMRESUMESUSPEND) tracker.SetSuspended(false);
                        UpdateIdleTimer();
                        UpdateTip();
                        return (IntPtr)1;

                    case Native.WM_TIMECHANGE:
                        tracker.OnTimeChanged();
                        return IntPtr.Zero;

                    case Native.WM_QUERYENDSESSION:
                        return (IntPtr)1;

                    case 0x0010: // WM_CLOSE: 외부에서 정상 종료를 요청할 때 (설치·업데이트 스크립트, 시험)
                        Native.PostQuitMessage(0);
                        return IntPtr.Zero;

                    case 0x0111: // WM_COMMAND: 메뉴와 같은 명령 (CmdErrors처럼 창을 여는 명령은 제외)
                        int c = wParam.ToInt32() & 0xFFFF;
                        if (c != Tray.CmdErrors) { Execute(c); UpdateIdleTimer(); UpdateTip(); }
                        return IntPtr.Zero;

                    case 0x004A: // WM_COPYDATA: 화면 모드의 기록 삭제·초기화 요청
                        int op; string[] ids;
                        if (!DataMaintenance.Parse(lParam, out op, out ids)) return IntPtr.Zero;
                        if (op == DataMaintenance.OpDelete) tracker.DeleteApps(ids);
                        else tracker.ResetAll();
                        UpdateTip();
                        return (IntPtr)1;

                    case Native.WM_ENDSESSION:
                        if (wParam != IntPtr.Zero) { tracker.Shutdown(); tray.Remove(); }
                        return IntPtr.Zero;
                }
                if (msg == taskbarCreated && taskbarCreated != 0)
                {
                    lastTip = null;
                    tray.Add("사용 시간 기록");   // 탐색기가 재시작되면 아이콘을 다시 등록
                    UpdateTip();
                    return IntPtr.Zero;
                }
            }
            catch (Exception ex) { ErrorLog.Write("wndproc", ex); }
            return Native.DefWindowProc(h, msg, wParam, lParam);
        }

        static void OnTimer(IntPtr id)
        {
            if (id == TimerTickId)
            {
                tracker.Tick();
                UpdateIdleTimer();
                Native.TrimMemory();
            }
            else if (id == TimerIdleId)
            {
                if (tracker.IdlePoll()) UpdateIdleTimer();
            }
            else if (id == TimerFlushId)
            {
                tracker.Flush();
                Native.TrimMemory();
            }
            else if (id == TimerPauseId)
            {
                Native.KillTimer(hwnd, TimerPauseId);
                if (tracker.IsPaused && tracker.PauseUntil != DateTime.MaxValue) tracker.SetPause(0);
            }
        }

        static void UpdateIdleTimer()
        {
            bool want = tracker.IsIdle;
            if (want == idleTimerOn) return;
            if (want) Native.SetTimer(hwnd, TimerIdleId, IdlePollMs, IntPtr.Zero);
            else Native.KillTimer(hwnd, TimerIdleId);
            idleTimerOn = want;
        }

        static void OnMenu()
        {
            int cmd = tray.ShowMenu(tracker.StatusText(), tracker.IsPaused, settings.AutoStart, manageAutoStart, ErrorLog.Count);
            Execute(cmd);
        }

        /// <summary>트레이 메뉴 명령 실행. WM_COMMAND로도 받는다 (화면 모드·시험 스크립트용).</summary>
        static void Execute(int cmd)
        {
            switch (cmd)
            {
                case Tray.CmdPause15: Pause(15); break;
                case Tray.CmdPause60: Pause(60); break;
                case Tray.CmdPauseForever: Pause(-1); break;
                case Tray.CmdResume:
                    Native.KillTimer(hwnd, TimerPauseId);
                    tracker.SetPause(0);
                    break;
                case Tray.CmdAutoStart:
                    if (!manageAutoStart) break;
                    settings.AutoStart = !settings.AutoStart;
                    settings.Save();
                    AutoStart.Apply(settings.AutoStart, exePath);
                    break;
                case Tray.CmdErrors:
                    if (ErrorLog.FilePath != null && File.Exists(ErrorLog.FilePath))
                        Native.ShellExecute(IntPtr.Zero, "open", "notepad.exe", "\"" + ErrorLog.FilePath + "\"", null, 1);
                    break;
                case Tray.CmdOpen:
                    LaunchUi();
                    break;
                case Tray.CmdFlush:
                    tracker.Flush();
                    break;
                case Tray.CmdSettings:
                    LaunchUi(" --settings");
                    break;
                case Tray.CmdReloadSettings:
                    settings = new Settings(dataDir);
                    if (idleSecOverride < 0) tracker.IdleThresholdMs = settings.IdleMinutes * 60 * 1000;
                    if (manageAutoStart)
                {
                    AutoStart.Apply(settings.AutoStart, exePath);
                    StartMenu.Ensure(exePath);   // 시작 메뉴에서 통계 창을 열 수 있게
                }
                    break;
                case Tray.CmdExit:
                    Native.PostQuitMessage(0);
                    break;
            }
        }

        static void Pause(int minutes)
        {
            tracker.SetPause(minutes);
            if (minutes > 0) Native.SetTimer(hwnd, TimerPauseId, (uint)minutes * 60000, IntPtr.Zero);
        }

        static void Safe(string context, Action a)
        {
            try { a(); }
            catch (Exception ex) { ErrorLog.Write(context, ex); }
        }
    }
}
