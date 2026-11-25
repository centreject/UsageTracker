using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace UsageTracker
{
    /// <summary>
    /// 터미널 안에서 실행 중인 CLI 도구를 찾는다 (실행 시간만 기록, 활성 시간은 터미널 창이 가져간다).
    /// 규칙: 셸(cmd·powershell 등)의 바로 아래 자식이면서, 그 셸의 부모를 따라 올라가면
    /// 터미널(Windows 터미널·콘솔 창)이나 탐색기(사용자가 직접 연 셸)에 닿는 프로그램.
    /// 다른 프로그램이 뒤에서 띄운 셸(예: 앱의 백그라운드 작업)의 자식은 세지 않는다. 창 제목은 쓰지 않는다.
    /// </summary>
    static class CliApps
    {
        static readonly HashSet<string> Shells = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cmd.exe", "powershell.exe", "pwsh.exe", "bash.exe", "sh.exe", "zsh.exe", "nu.exe", "fish.exe", "tcc.exe"
        };

        // 사용자가 셸을 여는 곳 (셸의 조상이 이 중 하나면 사용자가 연 셸)
        static readonly HashSet<string> TerminalHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "windowsterminal.exe", "openconsole.exe", "conhost.exe", "explorer.exe", "wt.exe",
            "alacritty.exe", "wezterm-gui.exe", "conemu64.exe", "conemu.exe", "mintty.exe", "tabby.exe", "hyper.exe"
        };

        // 셸 아래에 있어도 도구로 치지 않는 것
        static readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "conhost.exe", "openconsole.exe"
        };

        public sealed class Proc
        {
            public uint Pid, ParentPid;
            public string Exe;
        }

        /// <summary>프로세스 목록에서 CLI 도구의 pid를 고른다 (시험할 수 있게 순수 함수로 둔다).</summary>
        public static List<uint> Pick(List<Proc> procs)
        {
            var byPid = new Dictionary<uint, Proc>();
            foreach (var p in procs) byPid[p.Pid] = p;
            var interactive = new Dictionary<uint, bool>();
            var result = new List<uint>();
            foreach (var p in procs)
            {
                if (Shells.Contains(p.Exe) || Ignored.Contains(p.Exe)) continue;
                Proc parent;
                if (!byPid.TryGetValue(p.ParentPid, out parent) || !Shells.Contains(parent.Exe)) continue;
                if (IsInteractiveShell(parent, byPid, interactive, 0)) result.Add(p.Pid);
            }
            return result;
        }

        static bool IsInteractiveShell(Proc shell, Dictionary<uint, Proc> byPid, Dictionary<uint, bool> memo, int depth)
        {
            bool v;
            if (memo.TryGetValue(shell.Pid, out v)) return v;
            v = false;
            Proc parent;
            if (depth < 8 && shell.ParentPid != shell.Pid && byPid.TryGetValue(shell.ParentPid, out parent))
            {
                if (TerminalHosts.Contains(parent.Exe)) v = true;
                else if (Shells.Contains(parent.Exe)) v = IsInteractiveShell(parent, byPid, memo, depth + 1);   // 셸 안의 셸
            }
            memo[shell.Pid] = v;
            return v;
        }

        /// <summary>지금 실행 중인 프로세스 목록 (Toolhelp 스냅숏, 1분에 한 번).</summary>
        public static List<Proc> Snapshot()
        {
            var list = new List<Proc>();
            IntPtr snap = Native.CreateToolhelp32Snapshot(0x2 /*TH32CS_SNAPPROCESS*/, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
            try
            {
                var e = new Native.PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(Native.PROCESSENTRY32)) };
                if (!Native.Process32First(snap, ref e)) return list;
                do list.Add(new Proc { Pid = e.th32ProcessID, ParentPid = e.th32ParentProcessID, Exe = e.szExeFile });
                while (Native.Process32Next(snap, ref e));
            }
            finally { Native.CloseHandle(snap); }
            return list;
        }
    }
}
