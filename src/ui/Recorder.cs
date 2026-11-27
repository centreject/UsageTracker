using System;

namespace UsageTracker.Ui
{
    /// <summary>화면 모드에서 기록 모드(같은 데이터 폴더의 상주 프로세스)에 연락한다. 규칙은 RecorderLink.</summary>
    static class Recorder
    {
        /// <summary>이 화면 모드의 데이터 폴더. 같은 폴더를 쓰는 기록 모드에만 연락한다.</summary>
        public static string DataDir;

        /// <summary>기록 모드에 지금까지의 기록을 저장하라고 요청한다. 기록 모드가 실행 중이면 true.</summary>
        public static bool RequestFlush()
        {
            return Send(Tray.CmdFlush);
        }

        /// <summary>기록 모드에 메뉴 명령을 보낸다. 기록 모드가 실행 중이고 응답하면 true.</summary>
        public static bool Send(int cmd)
        {
            return RecorderLink.SendCommand(DataDir, cmd);
        }

        /// <summary>기록 모드를 정상 종료(저장 후 끝)시키고 끝날 때까지 기다린다.</summary>
        public static void CloseAndWait(int timeoutMs)
        {
            if (!RecorderLink.RequestClose(DataDir)) return;
            var until = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < until && RecorderLink.Find(DataDir) != IntPtr.Zero)
                System.Threading.Thread.Sleep(100);
        }
    }
}
