using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace UsageTracker
{
    // 자체 시험: 앱 인식(실행기·CLI·소리), 시작 메뉴, 업데이트 판단
    static partial class SelfTest
    {
        static void T19_HostTargets()
        {
            Func<string, string, string> name = (exe, cmd) => { var r = HostApps.Target(exe, cmd); return r == null ? null : r.Item1; };
            True("java -jar", name("javaw.exe", "\"C:\\Java\\bin\\javaw.exe\" -Xmx2G -jar \"D:\\Games\\My Launcher.jar\" --nogui") == "My Launcher.jar");
            True("java 메인 클래스", name("java.exe", "java -cp lib\\a.jar;b.jar com.example.App arg") == "com.example.App");
            True("java -m 모듈", name("java.exe", "java -p mods -m org.foo/org.foo.Main") == "org.foo");
            True("python 스크립트", name("python.exe", "C:\\Python\\python.exe -u C:\\bots\\bot.py --x 1") == "bot.py");
            True("python -m", name("pythonw.exe", "pythonw.exe -m idlelib") == "idlelib");
            True("python -c는 구분 안 함", name("python.exe", "python -c \"print(1)\"") == null);
            True("node 스크립트", name("node.exe", "node --inspect server.js") == "server.js");
            True("mmc 콘솔", name("mmc.exe", "\"C:\\WINDOWS\\system32\\mmc.exe\" \"C:\\WINDOWS\\system32\\devmgmt.msc\"") == "devmgmt.msc");
            True("인자 없음", name("python.exe", "python.exe") == null);
            var full = HostApps.Target("python.exe", "python C:\\bots\\bot.py");
            True("실행 대상 경로", full != null && full.Item2 == "C:\\bots\\bot.py");
            True("따옴표·역슬래시 분리", HostApps.Split("a \"b c\" d\\\\\"e f\"").Count == 3);
        }

        static void T20_CommandLine()
        {
            string cmd = HostApps.CommandLine((uint)Process.GetCurrentProcess().Id);
            True("자기 명령줄을 읽음 (--selftest 포함)", cmd != null && cmd.Contains("--selftest"));
        }

        static void T21_AudioMemory()
        {
            long sec = Stopwatch.Frequency;
            var m = new AudioMemory(150 * sec);
            True("들리면 소리 냄", m.Update(new[] { "a.exe" }, 0).Contains("a.exe"));
            True("60초 뒤 잠깐 조용해도 유지", m.Update(new string[0], 60 * sec).Contains("a.exe"));
            True("120초 뒤에도 유지", m.Update(new string[0], 120 * sec).Contains("a.exe"));
            True("180초 동안 조용하면 해제", !m.Update(new string[0], 180 * sec).Contains("a.exe"));
        }

        static void T23_Shortcut()
        {
            string dir = NewDir();
            string lnk = Path.Combine(dir, "test.lnk");
            string exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            True("처음엔 만든다", StartMenu.Ensure(exe, lnk));
            True("대상 경로가 맞다", string.Equals(StartMenu.Target(lnk), exe, StringComparison.OrdinalIgnoreCase));
            True("같으면 다시 만들지 않는다", !StartMenu.Ensure(exe, lnk));
            string other = Path.Combine(Environment.SystemDirectory, "calc.exe");
            True("exe를 옮기면 고친다", StartMenu.Ensure(other, lnk) && string.Equals(StartMenu.Target(lnk), other, StringComparison.OrdinalIgnoreCase));
        }

        static void T26_CliPick()
        {
            Func<uint, uint, string, CliApps.Proc> P = (pid, ppid, exe) => new CliApps.Proc { Pid = pid, ParentPid = ppid, Exe = exe };
            var procs = new List<CliApps.Proc>
            {
                P(1, 0, "explorer.exe"),
                P(10, 1, "WindowsTerminal.exe"),
                P(11, 10, "cmd.exe"),          // 터미널 탭의 셸
                P(12, 11, "claude.exe"),       // ← CLI 도구 (실측 구조: 도구 ← cmd ← WindowsTerminal)
                P(13, 12, "node.exe"),         // 도구의 자식은 세지 않음
                P(14, 11, "conhost.exe"),      // 콘솔 호스트는 무시
                P(20, 10, "pwsh.exe"),
                P(21, 20, "cmd.exe"),          // 셸 안의 셸
                P(22, 21, "python.exe"),       // ← CLI 도구
                P(30, 1, "cmd.exe"),           // Win+R로 연 cmd (부모 탐색기)
                P(31, 30, "ping.exe"),         // ← CLI 도구
                P(40, 1, "Claude.exe"),        // 다른 앱
                P(41, 40, "cmd.exe"),          // 앱이 뒤에서 띄운 셸
                P(42, 41, "powershell.exe"),
                P(43, 42, "git.exe"),          // 세지 않음
                P(50, 99, "cmd.exe"),          // 부모가 이미 끝난 셸
                P(51, 50, "node.exe"),         // 세지 않음
            };
            var picked = CliApps.Pick(procs);
            picked.Sort();
            True("터미널·탐색기에서 연 셸의 자식만: 12, 22, 31 (실제 " + string.Join(",", picked) + ")",
                picked.Count == 3 && picked[0] == 12 && picked[1] == 22 && picked[2] == 31);
        }

        static void T27_NodePackage()
        {
            True("범위 있는 패키지", HostApps.NodePackage(@"C:\Users\u\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js") == "claude-code");
            True("일반 패키지", HostApps.NodePackage(@"C:\x\node_modules\typescript\bin\tsc") == "typescript");
            True("node_modules 밖", HostApps.NodePackage(@"C:\proj\server.js") == null);
            var t = HostApps.Target("node.exe", "\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\Users\\u\\AppData\\Roaming\\npm\\node_modules\\@anthropic-ai\\claude-code\\cli.js\" --resume");
            True("node + npm CLI → 패키지 이름", t != null && t.Item1 == "claude-code");
        }

        static void T28_CliExcludeWindowed()
        {
            Func<uint, uint, string, CliApps.Proc> P = (pid, ppid, exe) => new CliApps.Proc { Pid = pid, ParentPid = ppid, Exe = exe };
            var procs = new List<CliApps.Proc>
            {
                P(10, 1, "WindowsTerminal.exe"),
                P(11, 10, "cmd.exe"),
                P(12, 11, "claude.exe"),     // 창 없음 → CLI
                P(13, 11, "notepad.exe"),    // cmd에서 띄운 메모장: 창 있음 → CLI 아님
            };
            var picked = CliApps.Pick(procs, new HashSet<uint> { 13 });
            True("창 있는 notepad는 빠지고 claude만 (실제 " + string.Join(",", picked) + ")", picked.Count == 1 && picked[0] == 12);
        }

        static void T29_LastRun()
        {
            var env = NewEnv(); string dir = NewDir();
            env.Visible.Add("cli:claude.exe");   // 실행 중인 CLI (활성 기록은 없음)
            var t = NewTracker(env, dir);
            env.At(60); env.Visible.Clear(); t.Tick();   // CLI 종료
            env.At(120); t.Shutdown();
            var now = env.Base.AddSeconds(200);
            var lastActive = UsageReader.LastUsed(dir, new[] { "cli:claude.exe" }, now, 'A');
            var lastRun = UsageReader.LastUsed(dir, new[] { "cli:claude.exe" }, now, 'R');
            True("활성 기록은 없음", lastActive == null);
            True("마지막 실행 = 종료 시각", lastRun != null && Math.Abs((lastRun.Value - env.Base.AddSeconds(60)).TotalSeconds) <= 1);
        }

        static void T33_HostEdgeAndClean()
        {
            Func<string, string, string> name = (exe, cmd) => { var r = HostApps.Target(exe, cmd); return r == null ? null : r.Item1; };
            True("node -e 특수문자 예외 없음", name("node.exe", "node -e \"console.log('a|b<c>')\"") == null);
            True("node --eval 무시", name("node.exe", "node --eval \"1+1\"") == null);
            True("node -p 무시", name("node.exe", "node -p \"process.version\"") == null);
            True("node -r·--env-file 뒤 스크립트 인식", name("node.exe", "node -r dotenv/config --env-file .env server.js") == "server.js");
            True("python 금지 문자 예외 없음", name("python.exe", "python \"bad|script.py\"") == null);
            string cleaned = name("java.exe", "java \"com.app\tbad\nname\"");
            True("제어문자 제거", cleaned != null && cleaned.IndexOf('\t') < 0 && cleaned.IndexOf('\n') < 0);

            string dir = NewDir();
            var w = new SessionWriter(dir);
            var start = new DateTime(2026, 3, 10, 10, 0, 0);
            w.AddClosed(new Segment { Kind = 'A', AppId = "bad\tid\n.exe", Start = start }, start.AddSeconds(10));
            w.Flush(start.AddSeconds(10), new Segment[0]);
            bool allFourCols = true;
            foreach (var line in File.ReadAllLines(SessionFormat.FileFor(dir, start), Encoding.UTF8))
            {
                var p = line.Split('\t');
                if (p[0] == "A" && p.Length != 4) allFourCols = false;
            }
            True("AppId 탭·줄바꿈이 TSV 열을 깨지 않음", allFourCols);
        }

        static void T41_UpdateDecision()
        {
            Func<string, Version> V = s => new Version(s);
            True("새 버전이면 알림", UpdateState.ShouldNotify(V("0.8.5"), V("0.9.0"), "", false));
            True("같은 버전이면 알림 없음", !UpdateState.ShouldNotify(V("0.9.0"), V("0.9.0"), "", false));
            True("다시 알리지 않기로 고른 버전은 알림 없음", !UpdateState.ShouldNotify(V("0.8.5"), V("0.9.0"), "0.9.0", false));
            True("다시 알리지 않기 뒤 더 새 버전(0.9.1)은 알림", UpdateState.ShouldNotify(V("0.8.5"), V("0.9.1"), "0.9.0", false));
            True("직접 확인은 건너뛴 버전도 보여 줌", UpdateState.ShouldNotify(V("0.8.5"), V("0.9.0"), "0.9.0", true));
            True("태그 v0.9.1 읽기", UpdateState.ParseTag("v0.9.1") == V("0.9.1"));
            True("태그 1.0.0-rc1 읽기", UpdateState.ParseTag("1.0.0-rc1") == V("1.0.0"));
            True("이상한 태그는 무시", UpdateState.ParseTag("latest") == null);

            string json = "[" +
                "{\"tag_name\":\"v1.1.0\",\"draft\":true,\"prerelease\":false,\"assets\":[]}," +
                "{\"tag_name\":\"v1.0.1-beta\",\"draft\":false,\"prerelease\":true,\"assets\":[]}," +
                "{\"tag_name\":\"v1.0.0\",\"draft\":false,\"prerelease\":false,\"body\":\"첫 정식\",\"assets\":[" +
                    "{\"name\":\"UsageTracker-v1.0.0.zip\",\"browser_download_url\":\"https://x/z.zip\"}," +
                    "{\"name\":\"UsageTracker-v1.0.0.zip.sha256\",\"browser_download_url\":\"https://x/z.zip.sha256\"}]}," +
                "{\"tag_name\":\"v0.9.0\",\"draft\":false,\"prerelease\":true,\"assets\":[]}]";
            True("1.0 전: 초안은 빼고 가장 높은 버전(사전 릴리스 포함)", Ui.Updater.PickVersionForTest(json, "0.8.5") == "1.0.1");
            True("1.0부터: 정식 릴리스만, 업데이트 파일 확인", Ui.Updater.PickVersionForTest(json, "1.0.0") == "1.0.0+files");

            var dir = NewDir();
            var st = UpdateState.Load(dir);
            True("처음엔 바로 확인 대상", st.NextCheck == DateTime.MinValue);
            st.LastCheck = new DateTime(2026, 11, 27, 10, 0, 0); st.SkipVersion = "0.9.0"; st.Save();
            var st2 = UpdateState.Load(dir);
            True("다음 확인은 일주일 뒤", st2.NextCheck == new DateTime(2026, 12, 4, 10, 0, 0));
            True("건너뛴 버전 저장", st2.SkipVersion == "0.9.0");
        }
    }
}
