using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UsageTracker.Ui
{
    /// <summary>기록 모드(같은 exe의 상주 프로세스)와의 연락.</summary>
    static class Recorder
    {
        /// <summary>이 화면 모드의 데이터 폴더. 같은 폴더를 쓰는 기록 모드에만 연락한다.</summary>
        public static string DataDir;

        static IntPtr Find() { return DataMaintenance.FindRecorder(DataDir); }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

        /// <summary>기록 모드에 지금까지의 기록을 저장하라고 요청한다. 기록 모드가 실행 중이면 true.</summary>
        public static bool RequestFlush()
        {
            return Send(Tray.CmdFlush);
        }

        /// <summary>기록 모드에 메뉴 명령을 보낸다. 기록 모드가 실행 중이고 응답하면 true.</summary>
        public static bool Send(int cmd)
        {
            IntPtr h = Find();
            if (h == IntPtr.Zero) return false;
            IntPtr r;
            return SendMessageTimeout(h, 0x0111 /*WM_COMMAND*/, (IntPtr)cmd, IntPtr.Zero, 0x2 /*SMTO_ABORTIFHUNG*/, 2000, out r) != IntPtr.Zero;
        }

        /// <summary>기록 모드를 정상 종료(저장 후 끝)시키고 끝날 때까지 기다린다.</summary>
        public static void CloseAndWait(int timeoutMs)
        {
            IntPtr h = Find();
            if (h == IntPtr.Zero) return;
            IntPtr r;
            SendMessageTimeout(h, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero, 0x2, 2000, out r);
            var until = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < until && Find() != IntPtr.Zero)
                System.Threading.Thread.Sleep(100);
        }
    }
}
