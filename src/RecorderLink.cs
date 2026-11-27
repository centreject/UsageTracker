using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace UsageTracker
{
    /// <summary>
    /// 기록 모드(상주 프로세스)와 화면 모드·업데이트 사이의 연락. 보내는 쪽과 받는 쪽 규칙을 한곳에 둔다.
    ///   창: 클래스 "UsageTracker.Recorder", 제목 "UsageTracker.Recorder|데이터 폴더(소문자)"
    ///       (tools\*.ps1도 이 이름으로 찾는다)
    ///   WM_COMMAND  wParam = 트레이 메뉴 명령 번호(Tray.Cmd*)
    ///   WM_CLOSE    저장하고 종료
    ///   WM_COPYDATA 기록 삭제·초기화 요청 (dwData = OpDelete·OpReset, 내용 = 앱 ID를 줄바꿈으로 이은 UTF-16). 처리하면 1을 돌려준다
    /// </summary>
    static class RecorderLink
    {
        public const string ClassName = "UsageTracker.Recorder";
        public const uint WM_CLOSE = 0x0010, WM_COMMAND = 0x0111, WM_COPYDATA = 0x004A;
        const uint SMTO_ABORTIFHUNG = 0x2;

        // 다른 프로그램의 우연한 WM_COPYDATA(dwData=1, 2)와 충돌하지 않도록 고유 서명("UT")을 붙인다.
        public const int OpDelete = 0x55540001, OpReset = 0x55540002;

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeout")]
        static extern IntPtr SendCopyData(IntPtr h, uint msg, IntPtr w, ref COPYDATASTRUCT l, uint flags, uint timeout, out IntPtr result);

        /// <summary>
        /// 기록 모드 창 제목에 데이터 폴더를 넣어, 화면 모드가 같은 데이터 폴더의 기록 모드에만 연락하게 한다
        /// (시험용 데이터 폴더로 연 화면 모드가 실제 기록 모드에 요청을 보내지 않도록).
        /// </summary>
        public static string Title(string dataDir)
        {
            return ClassName + "|" + dataDir.TrimEnd('\\').ToLowerInvariant();
        }

        public static IntPtr Find(string dataDir)
        {
            return FindWindow(ClassName, Title(dataDir));
        }

        // ---------- 보내는 쪽 (화면 모드·업데이트) ----------

        /// <summary>메뉴 명령을 보낸다. 기록 모드가 실행 중이고 응답하면 true.</summary>
        public static bool SendCommand(string dataDir, int cmd)
        {
            IntPtr h = Find(dataDir);
            if (h == IntPtr.Zero) return false;
            IntPtr r;
            return SendMessageTimeout(h, WM_COMMAND, (IntPtr)cmd, IntPtr.Zero, SMTO_ABORTIFHUNG, 2000, out r) != IntPtr.Zero;
        }

        /// <summary>정상 종료(저장 후 끝)를 요청한다. 끝날 때까지 기다리지는 않는다. 기록 모드가 없으면 false.</summary>
        public static bool RequestClose(string dataDir)
        {
            IntPtr h = Find(dataDir);
            if (h == IntPtr.Zero) return false;
            IntPtr r;
            SendMessageTimeout(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 2000, out r);
            return true;
        }

        /// <summary>삭제·초기화를 요청한다. 기록 모드가 받아서 처리했으면 true (없거나 응답이 없으면 false).</summary>
        public static bool SendData(string dataDir, int op, ICollection<string> ids)
        {
            IntPtr h = Find(dataDir);
            if (h == IntPtr.Zero) return false;
            string payload = ids == null ? "" : string.Join("\n", ids);
            IntPtr mem = Marshal.StringToHGlobalUni(payload);
            try
            {
                var cds = new COPYDATASTRUCT { dwData = (IntPtr)op, cbData = (payload.Length + 1) * 2, lpData = mem };
                IntPtr r;
                IntPtr ok = SendCopyData(h, WM_COPYDATA, IntPtr.Zero, ref cds, SMTO_ABORTIFHUNG, 10000, out r);
                return ok != IntPtr.Zero && r == (IntPtr)1;
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        // ---------- 받는 쪽 (기록 모드) ----------

        /// <summary>WM_COPYDATA 내용을 읽는다. 이 앱의 요청이 아니면 false.</summary>
        public static bool ParseData(IntPtr lParam, out int op, out string[] ids)
        {
            op = 0;
            ids = new string[0];
            if (lParam == IntPtr.Zero) return false;
            var cds = (COPYDATASTRUCT)Marshal.PtrToStructure(lParam, typeof(COPYDATASTRUCT));
            op = cds.dwData.ToInt32();
            if (op != OpDelete && op != OpReset) return false;
            string s = cds.cbData > 0 && cds.lpData != IntPtr.Zero
                ? Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2).TrimEnd('\0')
                : "";
            ids = s.Length == 0 ? new string[0] : s.Split('\n');
            return true;
        }
    }
}
