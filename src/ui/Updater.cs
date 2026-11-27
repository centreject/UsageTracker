using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace UsageTracker.Ui
{
    /// <summary>
    /// 새 버전 확인·업데이트. 기록 모드는 네트워크를 쓰지 않고, 일주일에 한 번 이 프로세스(--ui --update-check)를 띄우기만 한다.
    /// GitHub 릴리스 목록에서 버전만 받아 오고(사용 기록·PC 정보는 보내지 않음), 새 버전이 있으면 팝업으로 묻는다.
    /// "이 버전은 다시 알리지 않기"는 그 버전까지만: 더 새 버전이 나오면 다시 알린다.
    /// 업데이트: zip과 .sha256을 받아 해시를 확인 → 기록 모드 저장 후 종료 → 실행 파일 바꾸기(실행 중 파일은 이름만 바꿔 둠) → 다시 실행.
    /// </summary>
    static class Updater
    {
        public const string Repo = "centreject/UsageTracker";
        const string UserAgent = "UsageTracker-update-check";

        internal sealed class Release
        {
            public Version Version;
            public string Tag, Body, HtmlUrl, ZipUrl, ShaUrl;
            public bool Prerelease;
        }

        public static Version CurrentVersion()
        {
            var v = typeof(Updater).Assembly.GetName().Version;
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        public static int Run(string dataDir, bool manual, bool devMode, bool testAutoYes = false)
        {
            var theme = Theme.FromSystem();
            var settings = new Settings(dataDir);
            if (!manual && !settings.UpdateCheck) return 0;
            var state = UpdateState.Load(dataDir);
            DateTime now = DateTime.Now;
            if (!manual && state.LastCheck != DateTime.MinValue && now < state.NextCheck) return 0;

            // 실패해도 매번 다시 시도하지 않게 먼저 적어 둔다 (실패하면 하루 뒤 다시)
            state.LastCheck = now - (UpdateState.Interval - UpdateState.RetryAfterFailure);
            state.Save();

            Version current = CurrentVersion();
            List<Release> releases;
            try { releases = Fetch(); }
            catch (Exception ex)
            {
                ErrorLog.Write("update", ex);
                if (manual) Message("업데이트를 확인하지 못했어요.\n" + ex.Message, MessageBoxIcon.Warning);
                return 0;
            }
            state.LastCheck = now;
            state.Save();

            var latest = Pick(releases, current);
            if (latest == null || !UpdateState.ShouldNotify(current, latest.Version, state.SkipVersion, manual))
            {
                if (manual) Message("현재 최신 버전(v" + current.ToString(3) + ")입니다.", MessageBoxIcon.Information);
                return 0;
            }

            if (!testAutoYes)
            using (var d = new UpdateDialog(theme, current, latest.Version, latest.Body, latest.HtmlUrl))
            {
                var r = d.ShowDialog();
                if (d.SkipThisVersion)
                {
                    state.SkipVersion = latest.Version.ToString(3);
                    state.Save();
                }
                if (r != DialogResult.OK) return 0;
            }
            try
            {
                Apply(latest, dataDir, devMode);
                if (testAutoYes) ErrorLog.Write("update-test", new Exception("updated to " + latest.Version.ToString(3)));
                else Message("v" + latest.Version.ToString(3) + "(으)로 업데이트했어요.", MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("update", ex);
                if (!testAutoYes) Message("업데이트하지 못했어요. 기존 버전을 그대로 써요.\n" + ex.Message, MessageBoxIcon.Warning);
                return 1;
            }
        }

        static void Message(string text, MessageBoxIcon icon)
        {
            MessageBox.Show(text, "업데이트 - 사용 시간 기록", MessageBoxButtons.OK, icon);
        }

        // ---------- 확인 ----------

        static string Get(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Accept = "application/vnd.github+json";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        static List<Release> Fetch()
        {
            string json = Get("https://api.github.com/repos/" + Repo + "/releases?per_page=20");
            return ParseReleases(json);
        }

        /// <summary>GitHub 릴리스 목록 JSON에서 필요한 값만 꺼낸다 (자체 시험에서도 쓴다).</summary>
        internal static List<Release> ParseReleases(string json)
        {
            var list = new List<Release>();
            var arr = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.DeserializeObject(json) as object[];
            if (arr == null) return list;
            foreach (var o in arr)
            {
                var d = o as Dictionary<string, object>;
                if (d == null || IsTrue(d, "draft")) continue;
                var r = new Release
                {
                    Tag = Str(d, "tag_name"),
                    Body = Str(d, "body"),
                    HtmlUrl = Str(d, "html_url"),
                    Prerelease = IsTrue(d, "prerelease"),
                };
                r.Version = UpdateState.ParseTag(r.Tag);
                if (r.Version == null) continue;
                var assets = d.ContainsKey("assets") ? d["assets"] as object[] : null;
                if (assets != null)
                    foreach (var ao in assets)
                    {
                        var a = ao as Dictionary<string, object>;
                        if (a == null) continue;
                        string name = Str(a, "name") ?? "", url = Str(a, "browser_download_url");
                        if (name.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase)) r.ShaUrl = url;
                        else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) r.ZipUrl = url;
                    }
                list.Add(r);
            }
            return list;
        }

        /// <summary>가장 높은 버전. 1.0.0 전에는 사전 릴리스도 대상, 1.0.0부터는 정식 릴리스만.</summary>
        static Release Pick(List<Release> list, Version current)
        {
            Release best = null;
            foreach (var r in list)
            {
                if (r.Prerelease && current.Major >= 1) continue;
                if (best == null || r.Version > best.Version) best = r;
            }
            return best;
        }

        internal static string PickVersionForTest(string json, string current)
        {
            var r = Pick(ParseReleases(json), new Version(current));
            return r == null ? null : r.Version.ToString(3) + (r.ZipUrl != null && r.ShaUrl != null ? "+files" : "");
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) ? v as string : null; }
        static bool IsTrue(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is bool && (bool)v; }

        // ---------- 업데이트 ----------

        static void Apply(Release rel, string dataDir, bool devMode)
        {
            if (rel.ZipUrl == null || rel.ShaUrl == null)
            {
                try { Process.Start(rel.HtmlUrl); } catch { }
                throw new Exception("이 릴리스에는 자동 업데이트용 파일이 없어요. 릴리스 페이지에서 직접 받아 주세요.");
            }
            string work = Path.Combine(Path.GetTempPath(), "UsageTracker-update-" + rel.Version.ToString(3));
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);
            string zip = Path.Combine(work, "update.zip");
            using (var wc = new WebClient())
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                wc.DownloadFile(rel.ZipUrl, zip);
                wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                string sha = wc.DownloadString(rel.ShaUrl);
                string expected = sha.Trim().Split(new[] { ' ', '\t', '\r', '\n', '*' }, StringSplitOptions.RemoveEmptyEntries)[0];
                if (!string.Equals(expected, Sha256(zip), StringComparison.OrdinalIgnoreCase))
                    throw new Exception("받은 파일의 확인 값(SHA-256)이 맞지 않아요.");
            }

            string newExe = Path.Combine(work, "UsageTracker.exe"), newCfg = Path.Combine(work, "UsageTracker.exe.config");
            using (var za = ZipFile.OpenRead(zip))
                foreach (var e in za.Entries)
                {
                    if (string.Equals(e.Name, "UsageTracker.exe", StringComparison.OrdinalIgnoreCase)) e.ExtractToFile(newExe, true);
                    else if (string.Equals(e.Name, "UsageTracker.exe.config", StringComparison.OrdinalIgnoreCase)) e.ExtractToFile(newCfg, true);
                }
            if (!File.Exists(newExe)) throw new Exception("받은 파일에 UsageTracker.exe가 없어요.");
            string got = FileVersionInfo.GetVersionInfo(newExe).ProductVersion ?? "";
            if (!got.StartsWith(rel.Version.ToString(3))) throw new Exception("받은 프로그램의 버전(" + got + ")이 릴리스와 달라요.");

            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            string exe = Path.Combine(dir, "UsageTracker.exe");
            Replace(exe, newExe, newCfg, dataDir, devMode);
            try { Directory.Delete(work, true); } catch { }
        }

        /// <summary>기록 모드를 저장 후 끝내고, 실행 파일을 바꾼 뒤 다시 실행한다. 중간에 실패하면 원래대로 되돌린다.</summary>
        internal static void Replace(string exe, string newExe, string newCfg, string dataDir, bool devMode)
        {
            Recorder.CloseAndWait(10000);
            if (RecorderLink.Find(dataDir) != IntPtr.Zero) throw new Exception("기록 프로그램이 끝나지 않았어요.");

            // 실행 중인 exe(이 프로세스, 열려 있는 통계 창)는 덮어쓸 수 없지만 이름은 바꿀 수 있다
            string old = exe + ".old";
            try { if (File.Exists(old)) File.Delete(old); }
            catch { old = exe + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".old"; }
            File.Move(exe, old);
            try
            {
                File.Copy(newExe, exe, false);
                if (newCfg != null && File.Exists(newCfg)) File.Copy(newCfg, exe + ".config", true);
            }
            catch
            {
                try { if (File.Exists(exe)) File.Delete(exe); File.Move(old, exe); } catch { }
                StartRecorder(exe, dataDir, devMode);
                throw;
            }
            StartRecorder(exe, dataDir, devMode);
        }

        static void StartRecorder(string exe, string dataDir, bool devMode)
        {
            Process.Start(new ProcessStartInfo(exe, devMode ? "\"--data=" + dataDir + "\"" : "") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
        }

        static string Sha256(string file)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "");
        }
    }
}
