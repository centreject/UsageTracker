using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UsageTracker.Ui
{
    /// <summary>통계 창: 목록 오른쪽 클릭 메뉴 동작(이름 바꾸기·숨기기·합치기·즐겨찾기·삭제)과 창 활성화.</summary>
    partial class MainForm
    {

        void BuildMenu()
        {
            miRename = new ToolStripMenuItem("이름 바꾸기", null, (s, e) => Rename()) { ShortcutKeyDisplayString = "F2" };
            miHide = new ToolStripMenuItem("숨기기", null, (s, e) => ToggleHidden());
            miFavorite = new ToolStripMenuItem("즐겨찾기에 추가", null, (s, e) => ToggleFavorite(SelectedKeys()));
            miMerge = new ToolStripMenuItem("합치기...", null, (s, e) => Merge());
            miUnmerge = new ToolStripMenuItem("합치기 해제", null, (s, e) => Unmerge());
            miExclude = new ToolStripMenuItem("앞으로 기록하지 않기...", null, (s, e) => ExcludeApps());
            miDelete = new ToolStripMenuItem("기록 삭제...", null, (s, e) => DeleteRecords()) { Tag = "danger" };
            menu.Items.AddRange(new ToolStripItem[] { miRename, miFavorite, miHide, new ToolStripSeparator(), miMerge, miUnmerge, new ToolStripSeparator(), miExclude, miDelete });
            menu.Opening += (s, e) =>
            {
                var picked = SelectedKeys();
                if (picked.Count == 0) { e.Cancel = true; return; }
                bool hiddenRow = picked.Contains(HiddenKey);
                var keys = Expand(picked);
                bool one = picked.Count == 1;
                miRename.Enabled = one && !hiddenRow;
                miHide.Text = hiddenRow && one ? "모두 숨김 해제" : (keys.All(k => catalog.IsHidden(k)) ? "숨김 해제" : "숨기기");
                miMerge.Enabled = picked.Count >= 2 && !hiddenRow;
                miFavorite.Enabled = !hiddenRow;
                miFavorite.Text = picked.All(k => catalog.IsFavorite(k)) ? "즐겨찾기 해제" : "즐겨찾기에 추가";
                miUnmerge.Visible = one && AppCatalog.IsGroup(picked[0]);
                miDelete.Text = hiddenRow && one ? "숨긴 앱 " + keys.Count + "개의 기록 모두 삭제..."
                              : one ? "이 앱의 기록 삭제..." : "선택한 " + keys.Count + "개 앱의 기록 삭제...";
            };
        }

        List<string> SelectedKeys()
        {
            var l = new List<string>();
            foreach (ListViewItem i in list.SelectedItems) l.Add(((Row)i.Tag).Key);
            return l;
        }

        void Rename()
        {
            var keys = SelectedKeys();
            if (keys.Count != 1) return;
            string key = keys[0];
            if (key == HiddenKey) return;
            bool group = AppCatalog.IsGroup(key);
            using (var d = new NameDialog(theme, "이름 바꾸기", group ? "묶음 이름" : "표시할 이름 (비우면 원래 이름)",
                       catalog.DisplayName(key), group ? null : catalog.OriginalName(key)))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (group && string.IsNullOrWhiteSpace(d.Value)) return;
                catalog.Rename(key, d.Value);
                catalog.Save();
            }
            RefreshData();
        }

        void ToggleFavorite(List<string> keys)
        {
            keys = keys.Where(k => k != HiddenKey).ToList();
            if (keys.Count == 0) return;
            bool fav = !keys.All(k => catalog.IsFavorite(k));
            foreach (var k in keys) catalog.SetFavorite(k, fav);
            catalog.Save();
            RefreshData();
        }

        void ToggleHidden()
        {
            var keys = Expand(SelectedKeys());
            if (keys.Count == 0) return;
            bool hide = !keys.All(k => catalog.IsHidden(k));
            foreach (var k in keys) catalog.SetHidden(k, hide);
            catalog.Save();
            RefreshData();
        }

        void Merge()
        {
            var keys = SelectedKeys();
            if (keys.Count < 2 || keys.Contains(HiddenKey)) return;
            // 기본 이름: 이 기간 활성 시간이 가장 긴 항목의 이름
            var top = rows.Where(r => keys.Contains(r.Key)).OrderByDescending(r => r.Active).First();
            using (var d = new NameDialog(theme, "합치기", keys.Count + "개 항목을 하나로 묶어요. 묶음 이름:", top.Name, null))
            {
                if (d.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(d.Value)) return;
                string g = catalog.Merge(keys, d.Value.Trim());
                catalog.Save();
                RefreshData();
                SelectKey(g);
            }
        }

        void Unmerge()
        {
            var keys = SelectedKeys();
            if (keys.Count != 1 || !AppCatalog.IsGroup(keys[0])) return;
            catalog.Unmerge(keys[0]);
            catalog.Save();
            RefreshData();
        }

        void DeleteRecords()
        {
            var picked = SelectedKeys();
            var keys = Expand(picked);
            if (keys.Count == 0) return;
            var ids = new List<string>();
            foreach (var k in keys) ids.AddRange(AppCatalog.IsGroup(k) ? catalog.Members(k) : new List<string> { k });
            string what = picked.Count == 1 && picked[0] == HiddenKey ? "숨긴 앱 " + keys.Count + "개"
                        : keys.Count == 1 ? "'" + catalog.DisplayName(keys[0]) + "'" : "선택한 " + keys.Count + "개 항목";
            if (MessageBox.Show(this, what + "의 모든 기간 사용 기록을 지울까요?\n되돌릴 수 없어요. 앱은 앞으로도 계속 기록돼요.",
                    "기록 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try { DataMaintenance.Request(dataDir, DataMaintenance.OpDelete, ids); }
            catch (Exception ex) { MessageBox.Show(this, "기록을 지우지 못했어요: " + ex.Message, "기록 삭제"); }
            RefreshData();
        }

        /// <summary>선택한 앱(묶음은 구성 앱)을 앞으로 기록하지 않는다. 지난 기록은 남긴다.</summary>
        void ExcludeApps()
        {
            var picked = SelectedKeys();
            var keys = Expand(picked);
            var ids = new List<string>();
            foreach (var k in keys) ids.AddRange(AppCatalog.IsGroup(k) ? catalog.Members(k) : new List<string> { k });
            if (ids.Count == 0) return;
            string what = ids.Count == 1 ? "'" + catalog.DisplayName(ids[0]) + "'" : ids.Count + "개 앱";
            if (MessageBox.Show(this, what + "을(를) 앞으로 기록하지 않을까요?\n지난 기록은 그대로 남아요. 설정에서 다시 기록하게 할 수 있어요.",
                    "기록 제외", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            var settings = new Settings(dataDir);
            var list = settings.Exclude;
            list.AddRange(ids);
            settings.Exclude = list;
            settings.Save();
            Recorder.Send(Tray.CmdReloadSettings);
            RefreshData();
        }

        /// <summary>error.log에서 오늘 날짜로 시작하는 항목 수 (복구 안내처럼 오류가 아닌 기록은 뺀다).</summary>
        int TodayErrorCount()
        {
            try
            {
                string f = Path.Combine(dataDir, "error.log");
                if (!File.Exists(f)) return 0;
                string prefix = DateTime.Today.ToString("yyyy-MM-dd ");
                int n = 0;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                {
                    string line;
                    while ((line = sr.ReadLine()) != null)
                        if (line.StartsWith(prefix) && !line.Contains("[recover]")) n++;
                }
                return n;
            }
            catch { return 0; }
        }

        void StartRecorder()
        {
            try
            {
                string args = DevMode ? "\"--data=" + dataDir + "\"" : "";
                Process.Start(Application.ExecutablePath, args);
            }
            catch (Exception ex) { MessageBox.Show(this, "기록 프로그램을 켜지 못했어요: " + ex.Message, WindowTitle); return; }
            var t = new Timer { Interval = 1500 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); RefreshData(); };
            t.Start();
        }

        void OpenSettings()
        {
            using (var f = new SettingsForm(theme, dataDir, DevMode))
            {
                f.StartPosition = FormStartPosition.CenterParent;
                f.ShowDialog(this);
                if (f.Uninstalled) { uninstalled = true; Close(); return; }
                if (f.DataChanged) RefreshData();
            }
        }

        void FitNameColumn()
        {
            if (list.Columns.Count < 4) return;
            int others = list.Columns[1].Width + list.Columns[2].Width + list.Columns[3].Width;
            int w = list.ClientSize.Width - others - 4;
            if (w > 120) list.Columns[0].Width = w;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) { RefreshData(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.F) { searchBox.Focus(); searchBox.SelectAll(); e.Handled = true; }
            else if (e.KeyCode == Keys.F2 && list.Focused) { Rename(); e.Handled = true; }
            else if (e.Alt && e.KeyCode == Keys.Left) { MovePeriod(-1); e.Handled = true; }
            else if (e.Alt && e.KeyCode == Keys.Right) { MovePeriod(1); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (sum != null && IsCurrentPeriod()) RefreshData();   // 다시 앞으로 올 때 최신으로
        }
    }
}
