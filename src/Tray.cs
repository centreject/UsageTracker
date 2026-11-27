using System;
using System.Runtime.InteropServices;

namespace UsageTracker
{
    /// <summary>Shell_NotifyIcon을 직접 호출하는 트레이 아이콘 (WinForms 미사용).</summary>
    sealed class Tray
    {
        public const uint CallbackMessage = Native.WM_APP + 1;
        const uint Id = 1;

        // 메뉴 명령
        public const int CmdOpen = 10;
        public const int CmdPause15 = 20, CmdPause60 = 21, CmdPauseForever = 22, CmdResume = 23;
        public const int CmdAutoStart = 30;
        public const int CmdErrors = 40;
        public const int CmdFlush = 50;   // 화면 모드가 열 때 보내는 저장 요청 (메뉴에는 없음)
        public const int CmdSettings = 35;
        public const int CmdUpdateCheck = 36;
        public const int CmdReloadSettings = 60;   // 화면 모드가 설정을 저장한 뒤 보냄 (메뉴에는 없음)
        public const int CmdExit = 99;

        readonly IntPtr hwnd;
        readonly IntPtr icon;

        public Tray(IntPtr hwnd)
        {
            this.hwnd = hwnd;
            icon = Native.LoadIcon(Native.GetModuleHandle(null), (IntPtr)32512);   // exe에 넣은 아이콘
            if (icon == IntPtr.Zero) icon = Native.LoadIcon(IntPtr.Zero, (IntPtr)32512 /*IDI_APPLICATION*/);
        }

        Native.NOTIFYICONDATA Data(string tip)
        {
            return new Native.NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf(typeof(Native.NOTIFYICONDATA)),
                hWnd = hwnd,
                uID = Id,
                uFlags = 0x1 | 0x2 | 0x4, // NIF_MESSAGE | NIF_ICON | NIF_TIP
                uCallbackMessage = CallbackMessage,
                hIcon = icon,
                szTip = Trim(tip)
            };
        }

        static string Trim(string s)
        {
            if (s == null) return "";
            return s.Length > 127 ? s.Substring(0, 127) : s;
        }

        public void Add(string tip) { var d = Data(tip); Native.Shell_NotifyIcon(0 /*NIM_ADD*/, ref d); }
        public void Update(string tip) { var d = Data(tip); Native.Shell_NotifyIcon(1 /*NIM_MODIFY*/, ref d); }
        public void Remove() { var d = Data(""); Native.Shell_NotifyIcon(2 /*NIM_DELETE*/, ref d); }

        /// <summary>오른쪽 클릭 메뉴를 띄우고 고른 명령을 돌려준다 (0 = 취소).</summary>
        public int ShowMenu(string status, bool paused, bool autoStart, bool autoStartAvailable, int errorCount)
        {
            IntPtr menu = Native.CreatePopupMenu();
            IntPtr pauseMenu = Native.CreatePopupMenu();
            try
            {
                Native.AppendMenu(menu, Native.MF_STRING | Native.MF_GRAYED, UIntPtr.Zero, status);
                Native.AppendMenu(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
                Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)CmdOpen, "열기");

                if (paused)
                {
                    Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)CmdResume, "기록 다시 시작");
                }
                else
                {
                    Native.AppendMenu(pauseMenu, Native.MF_STRING, (UIntPtr)CmdPause15, "15분");
                    Native.AppendMenu(pauseMenu, Native.MF_STRING, (UIntPtr)CmdPause60, "1시간");
                    Native.AppendMenu(pauseMenu, Native.MF_STRING, (UIntPtr)CmdPauseForever, "다시 켤 때까지");
                    Native.AppendMenu(menu, Native.MF_POPUP, (UIntPtr)(ulong)pauseMenu.ToInt64(), "기록 일시정지");
                    pauseMenu = IntPtr.Zero;   // 상위 메뉴가 함께 해제한다
                }

                Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)CmdSettings, "설정...");
                Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)CmdUpdateCheck, "업데이트 확인...");
                uint autoFlags = Native.MF_STRING | (autoStart ? Native.MF_CHECKED : 0) | (autoStartAvailable ? 0 : Native.MF_GRAYED);
                Native.AppendMenu(menu, autoFlags, (UIntPtr)CmdAutoStart, "Windows 시작 시 실행");
                if (errorCount > 0)
                    Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)CmdErrors, "오류 " + errorCount + "건 (로그 열기)");
                Native.AppendMenu(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
                Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)CmdExit, "종료");

                Native.POINT pt;
                Native.GetCursorPos(out pt);
                Native.SetForegroundWindow(hwnd);
                uint cmd = Native.TrackPopupMenu(menu, Native.TPM_RETURNCMD | Native.TPM_BOTTOMALIGN | Native.TPM_RIGHTALIGN,
                    pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
                Native.PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);   // 메뉴가 바로 닫히도록 (MS 권장)
                return (int)cmd;
            }
            finally
            {
                Native.DestroyMenu(menu);
                if (pauseMenu != IntPtr.Zero) Native.DestroyMenu(pauseMenu);
            }
        }
    }
}
