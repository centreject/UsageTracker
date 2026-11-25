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
    /// <summary>
    /// 화면 모드 메인 창. 위에서부터: 기간 탭·이동 / 타임라인(일 탭만) / 목록 + 상세 / 시간대별 그래프 / 요약.
    /// </summary>
    sealed class MainForm : Form
    {
        public const string WindowTitle = "사용 시간 기록";

        enum Period { Day, Week, Month, Year }

        sealed class Row
        {
            public string Key, Name;
            public double Active, Running, Ratio;
            public bool Hidden;
        }

        readonly string dataDir;
        Theme theme = Theme.FromSystem();
        bool showHidden;
        public bool DevMode;                       // --data로 실행 (자동 시작을 건드리지 않음)
        readonly CheckBox showHiddenBox = new CheckBox();
        readonly Button settingsBtn = new Button();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripMenuItem miRename, miHide, miMerge, miUnmerge, miDelete;
        AppCatalog catalog;
        readonly IconCache icons;
        readonly ImageList images = new ImageList { ColorDepth = ColorDepth.Depth32Bit };
        Period period = Period.Day;
        DateTime anchor = DateTime.Today;
        UsageSummary sum, sumRaw;
        List<Row> rows = new List<Row>();
        int sortCol = 1;
        bool sortAsc;
        bool recorderAlive;

        // 컨트롤
        readonly RadioButton[] tabs = new RadioButton[4];
        readonly Button prevBtn = new Button(), nextBtn = new Button(), todayBtn = new Button();
        readonly Label periodLabel = new Label();
        readonly TimelineControl timeline = new TimelineControl();
        readonly ListView list = new ListView();
        readonly SplitContainer split = new SplitContainer();
        readonly TableLayoutPanel detail = new TableLayoutPanel();
        readonly HourlyChart hourly = new HourlyChart();
        readonly HourlyChart trend = new HourlyChart();      // 주·월·년의 날짜별(월별) 추이
        readonly TableLayoutPanel charts = new TableLayoutPanel();
        readonly Label summary = new Label();
        readonly Label status = new Label();
        readonly LinkLabel startLink = new LinkLabel();
        readonly LinkLabel errorLink = new LinkLabel();
        DateTime todaySeen = DateTime.Today;
        readonly Timer refreshTimer = new Timer { Interval = 60000 };
        readonly Font titleFont, boldFont;

        /// <summary>처음 보여 줄 기간(0=일 1=주 2=월 3=년)과 선택할 앱. 화면 확인용 (--view=, --select=).</summary>
        public int InitialView;
        public string InitialSelect;

        public MainForm(string dataDir)
        {
            this.dataDir = dataDir;
            Text = WindowTitle;
            Font = SystemFonts.MessageBoxFont;
            titleFont = new Font(Font.FontFamily, Font.Size * 1.5f, FontStyle.Bold);
            boldFont = new Font(Font, FontStyle.Bold);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1080, 760);
            MinimumSize = new Size(780, 560);
            BackColor = theme.Back;
            ForeColor = theme.Text;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            int iconSize = (int)Math.Round(16 * ScreenDpi() / 96.0);
            icons = new IconCache(iconSize);
            images.ImageSize = new Size(iconSize, iconSize);

            BuildLayout();
            BuildMenu();
            Themer.Apply(this, theme);
            refreshTimer.Tick += (s, e) =>
            {
                // 연 채로 자정이 지나면: "오늘"을 보고 있었다면 새 오늘로 넘어간다
                if (DateTime.Today != todaySeen)
                {
                    if (anchor == todaySeen) anchor = DateTime.Today;
                    todaySeen = DateTime.Today;
                    RefreshData();
                    return;
                }
                if (IsCurrentPeriod()) RefreshData();
            };
            refreshTimer.Start();
            // Windows 라이트·다크 설정이 바뀌면 따라간다
            Microsoft.Win32.UserPreferenceChangedEventHandler onPref = (s, e) =>
            {
                if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
                if (Theme.SystemUsesDark() == theme.Dark) return;
                BeginInvoke((Action)(() => { theme = Theme.FromSystem(); Themer.Apply(this, theme); Themer.TitleBar(this, theme); RefreshData(); }));
            };
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += onPref;
            FormClosed += (s, e) => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= onPref;
            Load += (s, e) =>
            {
                Themer.TitleBar(this, theme);
                split.SplitterDistance = Math.Max(300, split.Width - 330);
                if (InitialView > 0 && InitialView < 4) tabs[InitialView].Checked = true;   // RefreshData 호출됨
                else RefreshData();
                if (InitialSelect != null) SelectKey(InitialSelect);
            };
        }

        int ScreenDpi()
        {
            using (var g = CreateGraphics()) return (int)g.DpiX;
        }

        // ---------- 배치 ----------

        void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12, 8, 12, 8), BackColor = theme.Back };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // 1. 기간 탭과 이동
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 6) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var tabBar = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            string[] names = { "일", "주", "월", "년" };
            for (int i = 0; i < 4; i++)
            {
                var rb = new RadioButton
                {
                    Text = names[i], Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter,
                    Size = new Size(52, 30), Margin = new Padding(0, 0, 4, 0), Tag = (Period)i, Checked = i == 0, Cursor = Cursors.Hand
                };
                rb.FlatAppearance.BorderColor = theme.Border;
                rb.FlatAppearance.CheckedBackColor = theme.Accent;
                rb.CheckedChanged += (s, e) =>
                {
                    var r = (RadioButton)s;
                    r.ForeColor = r.Checked ? Color.White : theme.Text;
                    if (r.Checked) { period = (Period)r.Tag; RefreshData(); }
                };
                rb.ForeColor = rb.Checked ? Color.White : theme.Text;
                tabs[i] = rb;
                tabBar.Controls.Add(rb);
            }
            showHiddenBox.Text = "숨긴 항목 보기";
            showHiddenBox.AutoSize = true;
            showHiddenBox.Margin = new Padding(16, 7, 0, 0);
            showHiddenBox.CheckedChanged += (s, e) => { showHidden = showHiddenBox.Checked; RefreshData(); };
            tabBar.Controls.Add(showHiddenBox);
            top.Controls.Add(tabBar, 0, 0);

            var nav = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right, Margin = Padding.Empty };
            settingsBtn.Text = "설정";
            settingsBtn.Width = 56;
            settingsBtn.Margin = new Padding(8, 3, 0, 3);
            settingsBtn.Click += (s, e) => OpenSettings();
            foreach (var b in new[] { prevBtn, nextBtn, todayBtn, settingsBtn })
            {
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = theme.Border;
                b.Height = 30;
                b.Cursor = Cursors.Hand;
            }
            prevBtn.Text = "◀"; prevBtn.Width = 36;
            nextBtn.Text = "▶"; nextBtn.Width = 36;
            todayBtn.Text = "오늘"; todayBtn.Width = 56; todayBtn.Margin = new Padding(8, 3, 0, 3);
            periodLabel.AutoSize = true;
            periodLabel.Font = boldFont;
            periodLabel.Margin = new Padding(8, 9, 8, 0);
            prevBtn.Click += (s, e) => MovePeriod(-1);
            nextBtn.Click += (s, e) => MovePeriod(1);
            todayBtn.Click += (s, e) => { anchor = DateTime.Today; RefreshData(); };
            nav.Controls.AddRange(new Control[] { prevBtn, periodLabel, nextBtn, todayBtn, settingsBtn });
            top.Controls.Add(nav, 1, 0);
            root.Controls.Add(top, 0, 0);

            // 2. 타임라인
            timeline.Dock = DockStyle.Fill;
            timeline.Theme = theme;
            timeline.Margin = new Padding(0, 0, 0, 6);
            timeline.SegmentClicked += SelectKey;
            timeline.ColorOf = ColorOf;
            root.Controls.Add(timeline, 0, 1);

            // 3. 목록 + 상세
            split.Dock = DockStyle.Fill;
            split.FixedPanel = FixedPanel.Panel2;
            split.BackColor = theme.Border;
            split.Panel1.BackColor = theme.Back;
            split.Panel2.BackColor = theme.Panel;
            split.Panel2.Tag = "panel";
            split.SplitterWidth = 1;
            split.Margin = new Padding(0, 0, 0, 6);

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = true;   // 합치기용
            list.BorderStyle = BorderStyle.None;
            list.OwnerDraw = true;     // 머리글을 테마 색으로 그린다 (항목은 기본 그리기)
            list.DrawColumnHeader += DrawHeader;
            list.DrawItem += (s, e) => { };          // 칸별로 그린다 (DrawSubItem)
            list.DrawSubItem += DrawCell;
            list.GotFocus += (s, e) => list.Invalidate();
            list.LostFocus += (s, e) => list.Invalidate();
            list.DoubleClick += (s, e) =>
            {
                var k = SelectedKeys();
                if (k.Count == 1 && k[0] == HiddenKey) showHiddenBox.Checked = true;   // 숨긴 앱 펼치기
                else Rename();
            };
            list.ContextMenuStrip = menu;
            list.SmallImageList = images;
            list.BackColor = theme.Back;
            list.ForeColor = theme.Text;
            list.Columns.Add("이름", 300);
            list.Columns.Add("활성 시간", 110, HorizontalAlignment.Right);
            list.Columns.Add("실행 시간", 110, HorizontalAlignment.Right);
            list.Columns.Add("비율", 70, HorizontalAlignment.Right);
            list.ColumnClick += (s, e) => SortBy(e.Column);
            list.SelectedIndexChanged += (s, e) => OnSelection();
            list.Resize += (s, e) => FitNameColumn();
            split.Panel1.Controls.Add(list);

            detail.Dock = DockStyle.Fill;
            detail.ColumnCount = 2;
            detail.Padding = new Padding(16, 14, 12, 12);
            detail.AutoScroll = true;
            detail.BackColor = theme.Panel;
            detail.Tag = "panel";
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            split.Panel2.Controls.Add(detail);
            root.Controls.Add(split, 0, 2);

            // 4. 시간대별 그래프
            charts.Dock = DockStyle.Fill;
            charts.ColumnCount = 2;
            charts.RowCount = 1;
            charts.Margin = new Padding(0, 0, 0, 4);
            charts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
            charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var c in new[] { trend, hourly })
            {
                c.Dock = DockStyle.Fill;
                c.Theme = theme;
                c.Margin = Padding.Empty;
            }
            charts.Controls.Add(trend, 0, 0);
            charts.Controls.Add(hourly, 1, 0);
            root.Controls.Add(charts, 0, 3);

            // 5. 요약
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, Margin = Padding.Empty };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            // 오늘 생긴 오류(기록 모드·화면 모드 모두)가 있으면 표시, 누르면 로그를 연다
            errorLink.AutoSize = true;
            errorLink.Visible = false;
            errorLink.Tag = "danger";
            errorLink.Margin = new Padding(8, 4, 0, 0);
            errorLink.LinkClicked += (s, e) =>
            {
                try { Process.Start("notepad.exe", "\"" + Path.Combine(dataDir, "error.log") + "\""); } catch { }
            };
            bottom.Controls.Add(errorLink, 3, 0);
            summary.AutoSize = true;
            summary.Margin = new Padding(0, 4, 0, 0);
            status.AutoSize = true;
            status.Tag = "subtle";
            status.Margin = new Padding(0, 4, 0, 0);
            // 시작 메뉴에서 열었는데 기록 프로그램이 꺼져 있으면 여기서 켤 수 있게
            startLink.Text = "기록 시작";
            startLink.AutoSize = true;
            startLink.Visible = false;
            startLink.Margin = new Padding(8, 4, 0, 0);
            startLink.LinkClicked += (s, e) => StartRecorder();
            bottom.Controls.Add(summary, 0, 0);
            bottom.Controls.Add(status, 1, 0);
            bottom.Controls.Add(startLink, 2, 0);
            root.Controls.Add(bottom, 0, 4);
        }

        void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var b = new SolidBrush(theme.Back)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(theme.Border)) e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                        (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            string text = e.Header.Text + (e.ColumnIndex == sortCol ? (sortAsc ? " ▲" : " ▼") : "");
            TextRenderer.DrawText(e.Graphics, text, e.ColumnIndex == sortCol ? boldFont : Font, r, theme.Subtle, flags);
        }

        /// <summary>목록 칸 그리기: 테마 색 배경·선택 색, 첫 칸은 아이콘 + 이름.</summary>
        void DrawCell(object sender, DrawListViewSubItemEventArgs e)
        {
            var row = (Row)e.Item.Tag;
            Color back = theme.Back;
            if (e.Item.Selected)
                back = theme.Dark ? Color.FromArgb(list.Focused ? 0x1f : 0x2c, list.Focused ? 0x40 : 0x2c, list.Focused ? 0x68 : 0x2c)
                                  : (list.Focused ? Color.FromArgb(0xdd, 0xea, 0xfb) : Color.FromArgb(0xec, 0xeb, 0xe6));
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);

            var r = e.Bounds;
            Color fore = row.Hidden ? theme.Subtle : theme.Text;
            if (e.ColumnIndex == 0)
            {
                int sz = images.ImageSize.Width;
                int iconIndex = images.Images.IndexOfKey(row.Key);
                if (iconIndex >= 0) e.Graphics.DrawImage(images.Images[iconIndex], r.X + 4, r.Y + (r.Height - sz) / 2, sz, sz);
                var tr = new Rectangle(r.X + sz + 10, r.Y, r.Width - sz - 12, r.Height);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, list.Font, tr, fore,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else
            {
                var tr = new Rectangle(r.X + 4, r.Y, r.Width - 10, r.Height);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, list.Font, tr, fore,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        // ---------- 목록 오른쪽 클릭 메뉴: 이름 바꾸기·숨기기·합치기·해제·기록 삭제 ----------

        void BuildMenu()
        {
            miRename = new ToolStripMenuItem("이름 바꾸기", null, (s, e) => Rename()) { ShortcutKeyDisplayString = "F2" };
            miHide = new ToolStripMenuItem("숨기기", null, (s, e) => ToggleHidden());
            miMerge = new ToolStripMenuItem("합치기...", null, (s, e) => Merge());
            miUnmerge = new ToolStripMenuItem("합치기 해제", null, (s, e) => Unmerge());
            miDelete = new ToolStripMenuItem("기록 삭제...", null, (s, e) => DeleteRecords()) { Tag = "danger" };
            menu.Items.AddRange(new ToolStripItem[] { miRename, miHide, new ToolStripSeparator(), miMerge, miUnmerge, new ToolStripSeparator(), miDelete });
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
                if (f.Uninstalled) { Close(); return; }
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

        // ---------- 기간 ----------

        void PeriodRange(out DateTime from, out DateTime to)
        {
            switch (period)
            {
                case Period.Week:
                    int diff = ((int)anchor.DayOfWeek + 6) % 7;   // 월요일 시작
                    from = anchor.Date.AddDays(-diff); to = from.AddDays(7); break;
                case Period.Month:
                    from = new DateTime(anchor.Year, anchor.Month, 1); to = from.AddMonths(1); break;
                case Period.Year:
                    from = new DateTime(anchor.Year, 1, 1); to = from.AddYears(1); break;
                default:
                    from = anchor.Date; to = from.AddDays(1); break;
            }
        }

        bool IsCurrentPeriod()
        {
            DateTime from, to;
            PeriodRange(out from, out to);
            return DateTime.Now >= from && DateTime.Now < to;
        }

        void MovePeriod(int dir)
        {
            switch (period)
            {
                case Period.Week: anchor = anchor.AddDays(7 * dir); break;
                case Period.Month: anchor = anchor.AddMonths(dir); break;
                case Period.Year: anchor = anchor.AddYears(dir); break;
                default: anchor = anchor.AddDays(dir); break;
            }
            if (anchor > DateTime.Today) anchor = DateTime.Today;
            RefreshData();
        }

        string PeriodText(DateTime from, DateTime to)
        {
            var ko = CultureInfo.GetCultureInfo("ko-KR");
            switch (period)
            {
                case Period.Week:
                    var last = to.AddDays(-1);
                    string end = last.Month == from.Month ? last.Day + "일" : last.Month + "월 " + last.Day + "일";
                    return from.ToString("yyyy년 M월 d일", ko) + " – " + end;
                case Period.Month: return from.ToString("yyyy년 M월", ko);
                case Period.Year: return from.ToString("yyyy년", ko);
                default:
                    return from.ToString("yyyy년 M월 d일 (ddd)", ko) + (from == DateTime.Today ? " · 오늘" : "");
            }
        }

        // ---------- 데이터 ----------

        /// <summary>숨긴 앱을 모은 한 줄의 키.</summary>
        const string HiddenKey = "::hidden";
        UsageSummary sumKeys;                         // 숨긴 앱을 모으기 전 (앱·묶음 단위)
        List<string> hiddenInPeriod = new List<string>();
        int appCount;

        string ViewKey(string appId)
        {
            string k = catalog.KeyOf(appId);
            return !showHidden && catalog.IsHidden(k) ? HiddenKey : k;
        }

        string NameOf(string key)
        {
            return key == HiddenKey ? "숨긴 앱 (" + hiddenInPeriod.Count + "개)" : catalog.DisplayName(key);
        }

        Color ColorOf(string key)
        {
            return key == HiddenKey ? theme.Subtle : theme.SlotColor(catalog.SlotOf(key));
        }

        /// <summary>선택에 "숨긴 앱" 줄이 있으면 그 안의 숨긴 항목들로 펼친다.</summary>
        List<string> Expand(List<string> keys)
        {
            var l = new List<string>();
            foreach (var k in keys) { if (k == HiddenKey) l.AddRange(hiddenInPeriod); else l.Add(k); }
            return l.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        void RefreshData()
        {
            DateTime from, to;
            PeriodRange(out from, out to);
            periodLabel.Text = PeriodText(from, to);
            nextBtn.Enabled = to <= DateTime.Today;
            todayBtn.Enabled = !IsCurrentPeriod();

            recorderAlive = Recorder.RequestFlush();
            var now = DateTime.Now;
            catalog = new AppCatalog(dataDir);
            List<RawSeg> segs;
            try { segs = UsageReader.Read(dataDir, from, to, now, recorderAlive); }
            catch (Exception ex) { segs = new List<RawSeg>(); status.Text = "기록을 읽지 못했어요: " + ex.Message; }
            // 숨긴 항목은 "숨긴 앱" 한 줄로 모은다 ("숨긴 항목 보기"를 켜면 하나씩 흐리게).
            // 전체 합계·비율·그래프에는 숨긴 항목도 포함한다.
            sumKeys = UsageReader.Summarize(segs, catalog.KeyOf, from, to);
            sum = showHidden ? sumKeys : UsageReader.Summarize(segs, ViewKey, from, to);
            sumRaw = UsageReader.Summarize(segs, id => id, from, to);
            hiddenInPeriod = sumKeys.Active.Keys.Concat(sumKeys.Running.Keys)
                .Where(k => catalog.IsHidden(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var keys = new HashSet<string>(sum.Active.Keys, StringComparer.OrdinalIgnoreCase);
            keys.UnionWith(sum.Running.Keys);
            double total = 0;
            rows = new List<Row>();
            foreach (var k in keys)
            {
                double a, r;
                sum.Active.TryGetValue(k, out a);
                sum.Running.TryGetValue(k, out r);
                bool hidden = k == HiddenKey || catalog.IsHidden(k);
                rows.Add(new Row { Key = k, Name = NameOf(k), Active = a, Running = r, Hidden = hidden });
                total += a;
            }
            foreach (var row in rows) row.Ratio = total > 0 ? row.Active / total : 0;

            Populate(SelectedKeys());

            bool isDay = period == Period.Day;
            timeline.Visible = isDay;
            if (isDay) timeline.SetData(from, sum.ActiveSegments, NameOf);

            appCount = keys.Count - (keys.Contains(HiddenKey) ? 1 : 0) - (showHidden ? hiddenInPeriod.Count : 0);   // 숨기지 않은 앱 수
            summary.Text = "PC 켜짐 " + Fmt.Short(sum.PcOn) + "   ·   실제 사용 " + Fmt.Short(sum.PcUsed)
                + "   ·   앱 활성 합계 " + Fmt.Short(total) + "   ·   앱 " + appCount + "개"
                + (hiddenInPeriod.Count > 0 ? "   ·   숨김 " + hiddenInPeriod.Count + "개" : "");
            if (!status.Text.StartsWith("기록을"))
                status.Text = recorderAlive ? "기록 중 · " + now.ToString("HH:mm") + " 기준" : "기록 프로그램이 꺼져 있어요";
            startLink.Visible = !recorderAlive;
            int errors = TodayErrorCount();
            errorLink.Visible = errors > 0;
            errorLink.Text = "오류 " + errors + "건";
            OnSelection();
        }

        void Populate(List<string> keep)
        {
            var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            Comparison<Row> cmp;
            switch (sortCol)
            {
                case 0: cmp = (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); break;
                case 2: cmp = (a, b) => a.Running.CompareTo(b.Running); break;
                case 3: cmp = (a, b) => a.Ratio.CompareTo(b.Ratio); break;
                default: cmp = (a, b) => a.Active.CompareTo(b.Active); break;
            }
            rows.Sort((a, b) =>
            {
                int c = sortAsc ? cmp(a, b) : cmp(b, a);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });

            list.BeginUpdate();
            list.Items.Clear();
            foreach (var r in rows)
            {
                if (!images.Images.ContainsKey(r.Key))
                    images.Images.Add(r.Key, icons.Get(r.Key, PathOf(r.Key)));
                var item = new ListViewItem(r.Hidden && r.Key != HiddenKey ? r.Name + " (숨김)" : r.Name, r.Key) { Tag = r };
                if (r.Hidden) item.ForeColor = theme.Subtle;
                item.SubItems.Add(r.Active > 0 ? Fmt.Short(r.Active) : "–");
                item.SubItems.Add(r.Running > 0 ? Fmt.Short(r.Running) : "–");
                item.SubItems.Add(r.Active > 0 ? Fmt.Pct(r.Ratio) : "–");
                list.Items.Add(item);
                if (keepSet.Contains(r.Key)) item.Selected = true;
            }
            list.EndUpdate();
            SortArrow.RepaintHeader(list);
            FitNameColumn();
        }

        void SortBy(int col)
        {
            if (col == sortCol) sortAsc = !sortAsc;
            else { sortCol = col; sortAsc = col == 0; }   // 이름은 가나다순, 시간은 큰 것부터
            Populate(SelectedKeys());
        }

        string SelectedKey()
        {
            return list.SelectedItems.Count > 0 ? ((Row)list.SelectedItems[0].Tag).Key : null;
        }

        void SelectKey(string key)
        {
            foreach (ListViewItem item in list.Items)
            {
                bool match = string.Equals(((Row)item.Tag).Key, key, StringComparison.OrdinalIgnoreCase);
                item.Selected = match;
                if (match) { item.EnsureVisible(); item.Focused = true; }
            }
            list.Focus();
        }

        string PathOf(string key)
        {
            if (AppCatalog.IsGroup(key))
            {
                var m = catalog.Members(key);
                return m.Count > 0 ? PathOf(m[0]) : null;
            }
            AppInfo a;
            return catalog.Info.TryGetValue(key, out a) ? a.Path : null;
        }

        // ---------- 선택 → 상세·그래프·타임라인 ----------

        void OnSelection()
        {
            if (sum == null) return;
            string key = SelectedKey();
            timeline.Selected = key;
            timeline.Invalidate();

            string who = key == null ? "전체" : NameOf(key);
            Color color = key == null ? Color.Empty : ColorOf(key);
            double[] h = null;
            if (key == null) h = sum.HourlyAll; else sum.HourlyActive.TryGetValue(key, out h);
            hourly.SetData(h, "시간대별 · " + who + (period == Period.Day ? "" : " (기간 합계)"), color);

            // 주·월·년: 날짜별(년은 월별) 추이
            bool showTrend = period != Period.Day;
            trend.Visible = showTrend;
            charts.ColumnStyles[0] = showTrend ? new ColumnStyle(SizeType.Percent, 58) : new ColumnStyle(SizeType.Absolute, 0);
            charts.ColumnStyles[1] = new ColumnStyle(SizeType.Percent, showTrend ? 42 : 100);
            if (showTrend)
            {
                Dictionary<DateTime, double> daily = null;
                if (key == null) daily = sum.DailyAll; else sum.DailyActive.TryGetValue(key, out daily);
                SetTrend(daily ?? new Dictionary<DateTime, double>(), who, color);
            }
            ShowDetail(key);
        }

        void SetTrend(Dictionary<DateTime, double> daily, string who, Color color)
        {
            var ko = CultureInfo.GetCultureInfo("ko-KR");
            DateTime from, to;
            PeriodRange(out from, out to);
            double[] vals; string[] axis, tips;
            if (period == Period.Year)
            {
                vals = new double[12]; axis = new string[12]; tips = new string[12];
                foreach (var kv in daily) vals[kv.Key.Month - 1] += kv.Value;
                for (int m = 0; m < 12; m++) { axis[m] = (m + 1) + "월"; tips[m] = from.Year + "년 " + (m + 1) + "월"; }
                trend.SetBars(vals, axis, tips, "월별 · " + who, color);
                return;
            }
            int n = (int)(to - from).TotalDays;
            vals = new double[n]; axis = new string[n]; tips = new string[n];
            for (int i = 0; i < n; i++)
            {
                var d = from.AddDays(i);
                double v; daily.TryGetValue(d, out v);
                vals[i] = v;
                axis[i] = period == Period.Week ? d.ToString("ddd", ko)
                        : (d.Day == 1 || d.Day % 5 == 0) ? d.Day.ToString() : null;
                tips[i] = d.ToString("M월 d일 (ddd)", ko);
            }
            trend.SetBars(vals, axis, tips, "날짜별 · " + who, color);
        }

        void ShowDetail(string key)
        {
            detail.SuspendLayout();
            foreach (Control c in detail.Controls) c.Dispose();
            detail.Controls.Clear();
            detail.RowStyles.Clear();
            detail.RowCount = 0;

            if (key == null)
            {
                AddTitle(null, "기간 요약");
                AddField("앱", appCount + "개" + (hiddenInPeriod.Count > 0 ? " (숨김 " + hiddenInPeriod.Count + "개 별도)" : ""));
                AddField("앱 활성 합계", Fmt.Long(rows.Sum(r => r.Active)));
                AddField("PC 켜짐", Fmt.Long(sum.PcOn));
                AddField("실제 사용", Fmt.Long(sum.PcUsed));
                AddNote("목록에서 앱을 고르면 자세한 정보와 시간대별 사용이 보여요.");
            }
            else if (key == HiddenKey)
            {
                var row = rows.FirstOrDefault(r => r.Key == HiddenKey);
                AddTitle(null, "숨긴 앱");
                if (row != null)
                {
                    AddField("이 기간 활성", Fmt.Long(row.Active));
                    AddField("이 기간 실행", row.Running > 0 ? Fmt.Long(row.Running) : "–");
                    AddField("비율", row.Active > 0 ? Fmt.Pct(row.Ratio) : "–");
                }
                AddHeader("숨긴 항목 " + hiddenInPeriod.Count + "개");
                foreach (var k in hiddenInPeriod.OrderByDescending(k => { double v; sumKeys.Active.TryGetValue(k, out v); return v; }))
                {
                    double a; sumKeys.Active.TryGetValue(k, out a);
                    AddField(catalog.DisplayName(k), Fmt.Long(a));
                }
                AddNote("두 번 누르거나 '숨긴 항목 보기'를 켜면 하나씩 볼 수 있어요. 오른쪽 클릭으로 모두 숨김 해제하거나 기록을 지울 수 있어요.");
            }
            else
            {
                var row = rows.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
                bool group = AppCatalog.IsGroup(key);
                var ids = group ? catalog.Members(key) : new List<string> { key };

                AddTitle(icons.Get(key, PathOf(key)), catalog.DisplayName(key));
                AddField("원래 이름", group ? "묶음 (" + ids.Count + "개 앱)" : catalog.OriginalName(key));
                if (row != null)
                {
                    AddField("이 기간 활성", Fmt.Long(row.Active));
                    AddField("이 기간 실행", row.Running > 0 ? Fmt.Long(row.Running) : "–");
                    AddField("비율", row.Active > 0 ? Fmt.Pct(row.Ratio) : "–");
                }

                DateTime first = DateTime.MaxValue;
                foreach (var id in ids) { AppInfo a; if (catalog.Info.TryGetValue(id, out a) && a.FirstSeen > DateTime.MinValue && a.FirstSeen < first) first = a.FirstSeen; }
                if (first < DateTime.MaxValue) AddField("처음 기록", first.ToString("yyyy-MM-dd HH:mm"));
                var last = UsageReader.LastUsed(dataDir, ids, DateTime.Now);
                AddField("마지막 사용", last == null ? "–" : (last.Value >= DateTime.Now.AddMinutes(-1) ? "지금 사용 중" : last.Value.ToString("yyyy-MM-dd HH:mm")));

                if (group)
                {
                    AddHeader("묶인 앱");
                    foreach (var id in ids)
                    {
                        double a; sumRaw.Active.TryGetValue(id, out a);
                        AddField(catalog.OriginalName(id), Fmt.Long(a));
                    }
                }
                else
                {
                    string path = PathOf(key);
                    if (!string.IsNullOrEmpty(path))
                    {
                        AddField("실행 파일", path);
                        if (File.Exists(path))
                        {
                            var link = new LinkLabel { Text = "파일 위치 열기", AutoSize = true, Margin = new Padding(0, 2, 0, 8), LinkColor = theme.Accent };
                            link.LinkClicked += (s, e) => { try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { } };
                            AddRow(new Label(), link);
                        }
                    }
                    else if (key == AppResolver.DesktopId) AddNote("바탕화면이 맨 앞에 있던 시간이에요.");
                }
            }
            detail.ResumeLayout();
        }

        void AddRow(Control left, Control right)
        {
            detail.RowCount++;
            detail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            detail.Controls.Add(left, 0, detail.RowCount - 1);
            if (right != null) detail.Controls.Add(right, 1, detail.RowCount - 1);
            else detail.SetColumnSpan(left, 2);
        }

        void AddTitle(Image icon, string text)
        {
            var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
            if (icon != null) panel.Controls.Add(new PictureBox { Image = icon, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(24, 24), Margin = new Padding(0, 4, 8, 0) });
            panel.Controls.Add(new Label { Text = text, Font = titleFont, AutoSize = true, ForeColor = theme.Text, MaximumSize = new Size(260, 0) });
            AddRow(panel, null);
        }

        void AddField(string name, string value)
        {
            var l = new Label { Text = name, AutoSize = true, ForeColor = theme.Subtle, Margin = new Padding(0, 3, 12, 3) };
            var v = new Label { Text = value, AutoSize = true, ForeColor = theme.Text, Margin = new Padding(0, 3, 0, 3), MaximumSize = new Size(200, 0) };
            AddRow(l, v);
        }

        void AddHeader(string text)
        {
            AddRow(new Label { Text = text, AutoSize = true, Font = boldFont, ForeColor = theme.Text, Margin = new Padding(0, 12, 0, 4) }, null);
        }

        void AddNote(string text)
        {
            AddRow(new Label { Text = text, AutoSize = true, ForeColor = theme.Subtle, Margin = new Padding(0, 12, 0, 0), MaximumSize = new Size(280, 0) }, null);
        }
    }

    /// <summary>기록 모드(같은 exe의 상주 프로세스)와의 연락.</summary>
    static class Recorder
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

        /// <summary>기록 모드에 지금까지의 기록을 저장하라고 요청한다. 기록 모드가 실행 중이면 true.</summary>
        public static bool RequestFlush()
        {
            return Send(Tray.CmdFlush);
        }

        /// <summary>기록 모드에 메뉴 명령을 보낸다. 기록 모드가 실행 중이면 true.</summary>
        public static bool Send(int cmd)
        {
            IntPtr h = FindWindow("UsageTracker.Recorder", null);
            if (h == IntPtr.Zero) return false;
            IntPtr r;
            SendMessageTimeout(h, 0x0111 /*WM_COMMAND*/, (IntPtr)cmd, IntPtr.Zero, 0x2 /*SMTO_ABORTIFHUNG*/, 2000, out r);
            return true;
        }

        /// <summary>기록 모드를 정상 종료(저장 후 끝)시키고 끝날 때까지 기다린다.</summary>
        public static void CloseAndWait(int timeoutMs)
        {
            IntPtr h = FindWindow("UsageTracker.Recorder", null);
            if (h == IntPtr.Zero) return;
            IntPtr r;
            SendMessageTimeout(h, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero, 0x2, 2000, out r);
            var until = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < until && FindWindow("UsageTracker.Recorder", null) != IntPtr.Zero)
                System.Threading.Thread.Sleep(100);
        }
    }
}
