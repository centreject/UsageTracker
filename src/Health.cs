using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace UsageTracker
{
    /// <summary>
    /// 기록 모드가 자기 자원 사용을 health.tsv에 남긴다: 시작·종료·절전·복귀 때와 1시간마다 한 줄 (디스크 쓰기 시간당 1회).
    /// 외부 측정 스크립트 없이도 재부팅·절전을 넘어 장기간 자원 사용을 확인할 수 있다 (tools/health-report.ps1).
    /// 열: 시각  사건  버전  pid  워킹셋MB  커밋MB  CPU초  쓰기횟수  읽기횟수  핸들수
    /// 워킹셋은 작업 관리자 "메모리"(private working set)보다 크거나 같다 (상한으로 본다).
    /// </summary>
    static class Health
    {
        static string path;
        static string version;

        public static void Init(string dataDir)
        {
            path = Path.Combine(dataDir, "health.tsv");
            version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        }

        public static void Log(string ev)
        {
            if (path == null) return;
            try
            {
                IntPtr me = Native.GetCurrentProcess();
                var mem = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX)) };
                GetProcessMemoryInfo(me, ref mem, mem.cb);
                long create, exit, kernel, user;
                GetProcessTimes(me, out create, out exit, out kernel, out user);
                IO_COUNTERS io;
                GetProcessIoCounters(me, out io);
                uint handles;
                GetProcessHandleCount(me, out handles);

                var sb = new StringBuilder();
                if (!File.Exists(path)) sb.Append("time\tevent\tversion\tpid\twsMB\tcommitMB\tcpuSec\twriteOps\treadOps\thandles\r\n");
                var ci = CultureInfo.InvariantCulture;
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", ci)).Append('\t').Append(ev).Append('\t').Append(version)
                  .Append('\t').Append(Native.GetCurrentProcessId())
                  .Append('\t').Append((mem.WorkingSetSize.ToUInt64() / 1048576.0).ToString("0.000", ci))
                  .Append('\t').Append((mem.PrivateUsage.ToUInt64() / 1048576.0).ToString("0.00", ci))
                  .Append('\t').Append(((kernel + user) / 1e7).ToString("0.000", ci))
                  .Append('\t').Append(io.WriteOperationCount).Append('\t').Append(io.ReadOperationCount)
                  .Append('\t').Append(handles).Append("\r\n");
                File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { ErrorLog.Write("health", ex); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [DllImport("psapi.dll")] static extern bool GetProcessMemoryInfo(IntPtr h, ref PROCESS_MEMORY_COUNTERS_EX c, uint size);
        [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS io);
        [DllImport("kernel32.dll")] static extern bool GetProcessHandleCount(IntPtr h, out uint count);
    }
}
