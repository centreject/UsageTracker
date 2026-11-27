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
    sealed partial class MainForm : Form
    {
        public const string WindowTitle = "사용 시간 기록";

        enum Period { Day, Week, Month, Year }

        sealed class Row
        {
            public string Key, Name;
            public double Active, Running, Ratio;
            public bool Hidden;
            public bool Favorite;
        }

        readonly string dataDir;
        Theme theme = Theme.FromSystem();
        bool showHidden;
        public bool DevMode;                       // --data로 실행 (자동 시작을 건드리지 않음)
        readonly CheckBox showHiddenBox = new CheckBox();
        readonly CheckBox favOnlyBox = new CheckBox();
        readonly TextBox searchBox = new TextBox();
        string search = "";
        bool favOnly;
        readonly Button settingsBtn = new Button();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripMenuItem miRename, miHide, miFavorite, miMerge, miUnmerge, miExclude, miDelete;
        AppCatalog catalog;
        HashSet<string> excludedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IconCache icons;
        readonly ImageList images = new ImageList { ColorDepth = ColorDepth.Depth32Bit };
        Period period = Period.Day;
        DateTime anchor = DateTime.Today;
        UsageSummary sum;
        Dictionary<string, double> rawActive = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        List<Row> rows = new List<Row>();
        int sortCol = 1;
        bool sortAsc;
        bool recorderAlive;
        bool uninstalled;

        // 컨트롤
        TableLayoutPanel rootLayout;
        readonly RadioButton[] tabs = new RadioButton[4];
        readonly Button prevBtn = new Themer.FlatButton(), nextBtn = new Themer.FlatButton(), todayBtn = new Themer.FlatButton();
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
        Font titleFont, boldFont;

        /// <summary>처음 보여 줄 기간(0=일 1=주 2=월 3=년)과 선택할 앱. 화면 확인용 (--view=, --select=).</summary>
        public int InitialView;
        public string InitialSelect;
        public bool InitialFavOnly;   // --favonly (화면 확인용)
        public string InitialSearch;  // --search= (화면 확인용)

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
            DpiChanged += (s, e) => OnDpiRescale(e.DeviceDpiOld, e.DeviceDpiNew);
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
            FormClosed += (s, e) =>
            {
                Microsoft.Win32.SystemEvents.UserPreferenceChanged -= onPref;
                refreshTimer.Dispose();
                if (icons != null) icons.Dispose();
                if (titleFont != null) titleFont.Dispose();
                if (boldFont != null) boldFont.Dispose();
            };
            // 지난번 창 상태 (위치·크기·탭·정렬·필터). 화면 밖이면 무시
            prefs = UiPrefs.Load(dataDir);
            if (prefs.HasBounds)
            {
                var r = new Rectangle(prefs.X, prefs.Y, prefs.W, prefs.H);
                foreach (var scr in Screen.AllScreens)
                    if (scr.WorkingArea.IntersectsWith(new Rectangle(r.X + 40, r.Y + 10, 120, 40)))
                    {
                        StartPosition = FormStartPosition.Manual;
                        Bounds = r;
                        break;
                    }
            }
            if (prefs.Maximized) WindowState = FormWindowState.Maximized;
            sortCol = prefs.SortCol;
            sortAsc = prefs.SortAsc;
            FormClosing += (s, e) => { if (!uninstalled) SavePrefs(); };

            Load += (s, e) =>
            {
                Themer.TitleBar(this, theme);
                // 상세 패널: 창 폭의 30% (240~330px). 저장한 경계가 지금 창에 맞으면 그대로
                int detailW = Math.Max(240, Math.Min(330, split.Width * 3 / 10));
                split.SplitterDistance = prefs.Split >= 300 && prefs.Split <= split.Width - 240 ? prefs.Split : Math.Max(300, split.Width - detailW);
                showHiddenBox.Checked = prefs.ShowHidden;
                favOnlyBox.Checked = InitialFavOnly || prefs.FavOnly;
                int view = InitialView > 0 ? InitialView : prefs.Period;
                if (view > 0 && view < 4) tabs[view].Checked = true;   // RefreshData 호출됨
                else RefreshData();
                if (InitialSearch != null) searchBox.Text = InitialSearch;
                if (InitialSelect != null) SelectKey(InitialSelect);
            };
        }

        readonly UiPrefs prefs;

        void SavePrefs()
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            prefs.X = b.X; prefs.Y = b.Y; prefs.W = b.Width; prefs.H = b.Height;
            prefs.Maximized = WindowState == FormWindowState.Maximized;
            prefs.Split = split.SplitterDistance;
            prefs.Period = (int)period;
            prefs.SortCol = sortCol;
            prefs.SortAsc = sortAsc;
            prefs.ShowHidden = showHidden;
            prefs.FavOnly = favOnly;
            prefs.Save(dataDir);
        }

        /// <summary>이 창이 있는 모니터의 DPI (모니터별 배율 인식).</summary>
        int ScreenDpi() { return DeviceDpi; }

        int ScalePx(int px) { return (int)Math.Round(px * ScreenDpi() / 96.0); }

        /// <summary>배율이 다른 모니터로 옮겼을 때: 글꼴·컨트롤은 WinForms가 맞추고, 직접 정한 픽셀 값(글꼴·목록 열·그래프 높이·아이콘)은 여기서 맞춘다.</summary>
        void OnDpiRescale(int oldDpi, int newDpi)
        {
            if (oldDpi <= 0 || newDpi <= 0 || oldDpi == newDpi) return;
            double r = (double)newDpi / oldDpi;
            var oldTitle = titleFont; var oldBold = boldFont;
            titleFont = new Font(Font.FontFamily, Font.Size * 1.5f, FontStyle.Bold);
            boldFont = new Font(Font, FontStyle.Bold);
            periodLabel.Font = boldFont;
            if (rootLayout != null && rootLayout.RowStyles.Count > 3)
                rootLayout.RowStyles[3].Height = (float)Math.Round(200 * newDpi / 96.0);
            for (int i = 1; i < list.Columns.Count; i++) list.Columns[i].Width = (int)Math.Round(list.Columns[i].Width * r);
            int iconSize = (int)Math.Round(16 * newDpi / 96.0);
            var oldIcons = icons;
            icons = new IconCache(iconSize);
            images.Images.Clear();
            images.ImageSize = new Size(iconSize, iconSize);
            periodWidth.Clear();   // 기간 글자 칸 너비는 글꼴 크기에 따라 다시 잰다
            RefreshData();
            if (oldIcons != null) oldIcons.Dispose();
            if (oldTitle != null) oldTitle.Dispose();
            if (oldBold != null) oldBold.Dispose();
        }

    }
}
