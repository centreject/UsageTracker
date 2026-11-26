using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace UsageTracker
{
    /// <summary>
    /// 기록 삭제·초기화. 파일에 쓰는 쪽이 하나뿐이도록, 기록 모드가 실행 중이면 기록 모드가 직접 한다
    /// (화면 모드는 WM_COPYDATA로 요청만 보낸다). 기록 모드가 꺼져 있으면 화면 모드가 직접 한다.
    /// </summary>
    static class DataMaintenance
    {
        // 다른 프로그램의 우연한 WM_COPYDATA(dwData=1, 2)와 충돌하지 않도록 고유 서명("UT")을 붙인다.
        public const int OpDelete = 0x55540001, OpReset = 0x55540002;

        /// <summary>해당 앱들의 활성·실행 구간(열림 표시 포함)을 모든 월 파일에서 지운다. 지운 줄 수를 돌려준다.</summary>
        public static int DeleteApps(string dir, ICollection<string> ids)
        {
            var set = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            foreach (var f in Directory.GetFiles(dir, "sessions-*.tsv"))
            {
                var lines = File.ReadAllLines(f, Encoding.UTF8);
                var sb = new StringBuilder();
                int before = removed;
                foreach (var line in lines)
                {
                    var p = line.Split('\t');
                    bool drop = p.Length == 4 && set.Contains(p[3]) &&
                                (p[0] == "A" || p[0] == "R" || (p[0] == "O" && (p[1] == "A" || p[1] == "R")));
                    if (drop) removed++;
                    else sb.Append(line).Append("\r\n");
                }
                if (removed != before) AtomicFile.WriteAllText(f, sb.ToString());
            }
            return removed;
        }

        /// <summary>모든 기록과 앱 정보·이름·묶음을 지운다. 설정(settings.ini)은 남긴다.</summary>
        public static void ResetAll(string dir)
        {
            foreach (var f in Directory.GetFiles(dir, "sessions-*.tsv")) File.Delete(f);
            foreach (var name in new[] { "appinfo.tsv", "apps.tsv", "groups.tsv" })
            {
                string f = Path.Combine(dir, name);
                if (File.Exists(f)) File.Delete(f);
            }
        }

        // ---------- 화면 모드 → 기록 모드 요청 (WM_COPYDATA) ----------

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, ref COPYDATASTRUCT l, uint flags, uint timeout, out IntPtr result);

        /// <summary>
        /// 기록 모드에 삭제·초기화를 요청한다. 기록 모드가 없으면 직접 처리한다.
        /// </summary>
        /// <summary>
        /// 기록 모드 창 제목에 데이터 폴더를 넣어, 화면 모드가 같은 데이터 폴더의 기록 모드에만 연락하게 한다
        /// (시험용 데이터 폴더로 연 화면 모드가 실제 기록 모드에 요청을 보내지 않도록).
        /// </summary>
        public static string RecorderTitle(string dataDir)
        {
            return "UsageTracker.Recorder|" + dataDir.TrimEnd('\\').ToLowerInvariant();
        }

        public static IntPtr FindRecorder(string dataDir)
        {
            return FindWindow("UsageTracker.Recorder", RecorderTitle(dataDir));
        }

        public static void Request(string dir, int op, ICollection<string> ids)
        {
            IntPtr h = FindRecorder(dir);
            if (h != IntPtr.Zero)
            {
                string payload = ids == null ? "" : string.Join("\n", ids);
                IntPtr mem = Marshal.StringToHGlobalUni(payload);
                try
                {
                    var cds = new COPYDATASTRUCT { dwData = (IntPtr)op, cbData = (payload.Length + 1) * 2, lpData = mem };
                    IntPtr r;
                    IntPtr ok = SendMessageTimeout(h, 0x004A /*WM_COPYDATA*/, IntPtr.Zero, ref cds, 0x2 /*SMTO_ABORTIFHUNG*/, 10000, out r);
                    if (ok != IntPtr.Zero && r == (IntPtr)1) return;   // 기록 모드가 처리함
                }
                finally { Marshal.FreeHGlobal(mem); }
            }
            if (op == OpDelete) DeleteApps(dir, ids);
            else if (op == OpReset) ResetAll(dir);
        }

        /// <summary>기록 모드 쪽: WM_COPYDATA 내용을 읽는다.</summary>
        public static bool Parse(IntPtr lParam, out int op, out string[] ids)
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
