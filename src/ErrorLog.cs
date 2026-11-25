using System;
using System.IO;
using System.Text;

namespace UsageTracker
{
    /// <summary>
    /// 오류만 기록한다. 평소에는 아무것도 쓰지 않는다.
    /// 파일이 1MB를 넘으면 지우고 새로 시작한다.
    /// </summary>
    static class ErrorLog
    {
        const long MaxBytes = 1024 * 1024;
        static string path;

        /// <summary>이번 실행에서 생긴 오류 수 (트레이 메뉴 표시용).</summary>
        public static int Count { get; private set; }
        public static string FilePath { get { return path; } }

        public static void Init(string dataDir)
        {
            path = Path.Combine(dataDir, "error.log");
        }

        public static void Write(string context, Exception ex)
        {
            Count++;
            try
            {
                if (path == null) return;
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxBytes) fi.Delete();
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + context + "] " + ex + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
