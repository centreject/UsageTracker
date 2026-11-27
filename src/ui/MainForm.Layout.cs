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
    /// <summary>통계 창: 컨트롤 배치와 이벤트 연결.</summary>
    partial class MainForm
    {

        void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12, 8, 12, 8), BackColor = theme.Back };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // 창 폭을 넘지 않게
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 200));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            // 1. 기간 탭과 이동
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 6) };
            var filterBar = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0), Dock = DockStyle.Fill };
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
                    r.FlatAppearance.MouseOverBackColor = r.Checked ? theme.Accent : theme.Track;
                    if (r.Checked) { period = (Period)r.Tag; RefreshData(); }
                };
                rb.ForeColor = rb.Checked ? Color.White : theme.Text;
                tabs[i] = rb;
                tabBar.Controls.Add(rb);
            }
            showHiddenBox.Text = "숨긴 항목 보기";
            showHiddenBox.AutoSize = true;
            showHiddenBox.Margin = new Padding(14, 7, 0, 0);
            showHiddenBox.CheckedChanged += (s, e) => { showHidden = showHiddenBox.Checked; RefreshData(); };
            filterBar.Controls.Add(showHiddenBox);
            favOnlyBox.Text = "★ 즐겨찾기만";
            favOnlyBox.AutoSize = true;
            favOnlyBox.Margin = new Padding(12, 7, 0, 0);
            favOnlyBox.CheckedChanged += (s, e) => { favOnly = favOnlyBox.Checked; Populate(SelectedKeys()); OnSelection(); };
            filterBar.Controls.Add(favOnlyBox);
            searchBox.Width = 200;
            searchBox.Margin = new Padding(0, 4, 0, 0);
            searchBox.TextChanged += (s, e) => { search = searchBox.Text.Trim(); Populate(SelectedKeys()); OnSelection(); };
            searchBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { searchBox.Clear(); e.Handled = e.SuppressKeyPress = true; } };
            searchBox.HandleCreated += (s, e) => SendMessage(searchBox.Handle, 0x1501 /*EM_SETCUEBANNER*/, (IntPtr)1, "앱 검색 (Ctrl+F)");
            filterBar.Controls.Add(searchBox);
            filterBar.Controls.SetChildIndex(searchBox, 0);   // 검색칸을 맨 앞에
            top.Controls.Add(tabBar, 0, 0);
            // 둘째 줄: 검색·필터 (첫 줄에 두면 좁은 창에서 기간 이동 버튼이 밀려난다)
            top.Controls.Add(filterBar, 0, 1);
            top.SetColumnSpan(filterBar, 2);

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
            periodLabel.AutoSize = false;   // 너비는 RefreshData에서 기간 종류별 고정값으로
            periodLabel.TextAlign = ContentAlignment.MiddleCenter;
            periodLabel.Font = boldFont;
            periodLabel.Margin = new Padding(4, 3, 4, 3);
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
            hourly.BarClicked += h => { if (period == Period.Day) timeline.ShowRange(anchor.Date.AddHours(h), anchor.Date.AddHours(h + 1)); };
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
                var pt = list.PointToClient(Cursor.Position);
                var hit = list.HitTest(pt);
                if (hit.Item != null && IsOnStar(hit.Item, pt.X)) return;   // 별 자리를 빠르게 두 번 눌러도 이름 바꾸기 창을 띄우지 않는다
                var k = SelectedKeys();
                if (k.Count == 1 && k[0] == HiddenKey) showHiddenBox.Checked = true;   // 숨긴 앱 펼치기
                else Rename();
            };
            list.ContextMenuStrip = menu;
            list.MouseClick += (s, e) =>
            {
                // 이름 칸의 별 자리를 누르면 즐겨찾기 켜고 끄기
                var hit = list.HitTest(e.Location);
                if (hit.Item == null || e.Button != MouseButtons.Left) return;
                if (IsOnStar(hit.Item, e.X)) ToggleFavorite(new List<string> { ((Row)hit.Item.Tag).Key });
            };
            list.SmallImageList = images;
            list.BackColor = theme.Back;
            list.ForeColor = theme.Text;
            // 열 너비는 픽셀이라 배율에 맞춰 키운다 (정렬 화살표가 굵은 글씨로 붙어도 잘리지 않게)
            double colK = ScreenDpi() / 96.0;
            list.Columns.Add("이름", (int)(300 * colK));
            list.Columns.Add("활성 시간", (int)(110 * colK), HorizontalAlignment.Right);
            list.Columns.Add("실행 시간", (int)(110 * colK), HorizontalAlignment.Right);
            list.Columns.Add("비율", (int)(78 * colK), HorizontalAlignment.Right);
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

        int StarWidth { get { return images.ImageSize.Width; } }

        bool IsOnStar(ListViewItem item, int x)
        {
            var row = (Row)item.Tag;
            if (row.Key == HiddenKey) return false;
            var b = item.GetBounds(ItemBoundsPortion.Entire);
            int x0 = b.X + 4 + images.ImageSize.Width + 4;
            return x >= x0 && x <= x0 + StarWidth;
        }

        /// <summary>검색: 표시 이름이나 원래 이름에 검색어가 들어 있으면.</summary>
        bool Matches(Row r)
        {
            var ci = CultureInfo.CurrentCulture.CompareInfo;
            if (ci.IndexOf(r.Name, search, CompareOptions.IgnoreCase) >= 0) return true;
            if (r.Key != HiddenKey && ci.IndexOf(catalog.OriginalName(r.Key), search, CompareOptions.IgnoreCase) >= 0) return true;
            return false;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, string l);

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
                // 아이콘 옆 별 자리: 즐겨찾기면 노란 별 (자리는 항상 비워 둬서 이름 줄이 맞는다)
                var star = new Rectangle(r.X + sz + 8, r.Y, StarWidth, r.Height);
                if (row.Favorite)
                    TextRenderer.DrawText(e.Graphics, "★", list.Font, star, theme.Dark ? Color.FromArgb(0xc9, 0x85, 0x00) : Color.FromArgb(0xed, 0xa1, 0x00),
                        TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                var tr = new Rectangle(star.Right + 2, r.Y, r.Right - star.Right - 4, r.Height);
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
    }
}
