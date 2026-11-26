using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UsageTracker.Ui
{
    /// <summary>
    /// 하루 활성 앱 타임라인 띠. 앱마다 고정 색, 선택한 앱 외에는 흐리게.
    /// 기본은 그날 기록이 있는 시간대만 보여 주고(정시 단위), 휠로 확대·축소, 끌어서 옮기기, 두 번 클릭으로 원래대로.
    /// 마우스를 올리면 앱·시각, 누르면 그 앱을 선택한다. 아래에 상위 앱 범례.
    /// </summary>
    sealed class TimelineControl : Control
    {
        List<RawSeg> segs = new List<RawSeg>();
        DateTime day;
        DateTime viewStart, viewEnd;   // 보이는 범위
        bool userZoomed;               // 사용자가 확대·이동했으면 자동 갱신 때 범위를 유지
        Func<string, string> nameOf = k => k;
        readonly ToolTip tip = new ToolTip { InitialDelay = 0, ReshowDelay = 0, AutoPopDelay = 10000 };
        string lastTip;
        public Theme Theme = Theme.Light();
        public Func<string, Color> ColorOf = k => Color.Gray;
        public string Selected;
        public event Action<string> SegmentClicked;

        readonly float k;   // 화면 배율
        readonly int PadX, BarTop, BarH, AxisH, LegendH;
        const double MinSpanMin = 10;

        Point downAt; bool dragging; DateTime dragStartView;

        public TimelineControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) k = g.DpiY / 96f;
            PadX = S(12); BarTop = S(6); BarH = S(34); AxisH = S(18); LegendH = S(20);
            Height = BarTop + BarH + AxisH + LegendH + S(6);
            viewStart = DateTime.Today; viewEnd = viewStart.AddDays(1);
        }

        int S(int v) { return (int)Math.Round(v * k); }

        public void SetData(DateTime day, List<RawSeg> active, Func<string, string> nameOf)
        {
            bool sameDay = this.day == day.Date;
            this.day = day.Date;
            segs = active;
            this.nameOf = nameOf;
            if (!sameDay || !userZoomed) FitToData();
            Invalidate();
        }

        /// <summary>기록이 있는 첫 정시부터 마지막 정시까지. 기록이 없으면 하루 전체.</summary>
        void FitToData()
        {
            userZoomed = false;
            if (segs.Count == 0) { viewStart = day; viewEnd = day.AddDays(1); return; }
            DateTime a = DateTime.MaxValue, b = DateTime.MinValue;
            foreach (var s in segs) { if (s.Start < a) a = s.Start; if (s.End > b) b = s.End; }
            if (a < day) a = day;
            if (b > day.AddDays(1)) b = day.AddDays(1);
            var from = new DateTime(a.Year, a.Month, a.Day, a.Hour, 0, 0);
            var to = new DateTime(b.Year, b.Month, b.Day, b.Hour, 0, 0);
            if (to < b) to = to.AddHours(1);
            if ((to - from).TotalHours < 2) to = from.AddHours(2);
            if (to > day.AddDays(1)) { to = day.AddDays(1); from = to.AddHours(-2); }
            viewStart = from; viewEnd = to;
        }

        float BarLeft { get { return PadX; } }
        float Span { get { return Math.Max(1, Width - PadX * 2); } }

        float X(DateTime t)
        {
            double frac = (t - viewStart).TotalSeconds / (viewEnd - viewStart).TotalSeconds;
            return BarLeft + (float)(frac * Span);
        }

        DateTime TimeAt(float x)
        {
            double frac = (x - BarLeft) / Span;
            return viewStart.AddSeconds(frac * (viewEnd - viewStart).TotalSeconds);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new RectangleF(PadX, BarTop, Span, BarH);
            using (var b = new SolidBrush(Theme.Track)) FillRounded(g, b, track, S(4));

            // 눈금: 글자가 겹치지 않는 가장 촘촘한 간격
            int[] steps = { 5, 10, 15, 30, 60, 120, 180, 360 };
            double spanMin = (viewEnd - viewStart).TotalMinutes;
            int step = 360;
            foreach (int st in steps) if (Span * st / spanMin >= S(44)) { step = st; break; }
            using (var pen = new Pen(Theme.Grid))
            using (var tb = new SolidBrush(Theme.Subtle))
            {
                var t0 = day.AddMinutes(Math.Ceiling((viewStart - day).TotalMinutes / step) * step);
                for (var t = t0; t <= viewEnd; t = t.AddMinutes(step))
                {
                    float x = X(t);
                    g.DrawLine(pen, x, BarTop + BarH, x, BarTop + BarH + S(3));
                    int h = (int)(t - day).TotalHours;
                    string s = step >= 60 ? h.ToString("00") : h.ToString("00") + ":" + t.Minute.ToString("00");
                    var sz = g.MeasureString(s, Font);
                    g.DrawString(s, Font, tb, Math.Max(0, Math.Min(Width - sz.Width, x - sz.Width / 2)), BarTop + BarH + S(2));
                }
            }

            g.SmoothingMode = SmoothingMode.None;
            var clip = g.Clip;
            g.SetClip(track);
            foreach (var s in segs)
            {
                if (s.End <= viewStart || s.Start >= viewEnd) continue;
                float x1 = X(s.Start), x2 = Math.Max(X(s.End), x1 + 1);
                var c = ColorOf(s.AppId);
                if (Selected != null && !string.Equals(Selected, s.AppId, StringComparison.OrdinalIgnoreCase))
                    c = Color.FromArgb(55, c);
                using (var b = new SolidBrush(c)) g.FillRectangle(b, x1, BarTop + S(2), x2 - x1, BarH - S(4));
            }
            g.Clip = clip;

            // 범례: 이 날 활성 시간 상위 6개 앱. 오른쪽에 조작 안내.
            var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in segs) { double v; totals.TryGetValue(s.AppId, out v); totals[s.AppId] = v + s.Seconds; }
            var top = new List<KeyValuePair<string, double>>(totals);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            float lx = PadX, ly = BarTop + BarH + AxisH + S(2);
            using (var tb = new SolidBrush(Theme.Subtle))
            {
                string hint = top.Count == 0 ? "" : userZoomed ? "두 번 클릭하면 원래대로" : "휠로 확대 · 끌어서 이동 · 아래 막대를 눌러도 돼요";
                float hintW = hint.Length == 0 ? 0 : g.MeasureString(hint, Font).Width + S(12);
                for (int i = 0; i < top.Count && i < 6; i++)
                {
                    string label = nameOf(top[i].Key);
                    var sz = g.MeasureString(label, Font);
                    if (lx + S(14) + sz.Width > Width - PadX - hintW) break;
                    using (var b = new SolidBrush(ColorOf(top[i].Key))) g.FillRectangle(b, lx, ly + S(4), S(10), S(10));
                    g.DrawString(label, Font, tb, lx + S(13), ly + S(1));
                    lx += S(13) + sz.Width + S(12);
                }
                if (hint.Length > 0) g.DrawString(hint, Font, tb, Width - PadX - hintW + S(12), ly + S(1));
                if (top.Count == 0) g.DrawString("이 날은 기록이 없어요", Font, tb, PadX, ly + S(1));
            }
        }

        RawSeg Hit(Point p)
        {
            if (p.Y < BarTop - 4 || p.Y > BarTop + BarH + 4) return null;
            RawSeg best = null;
            foreach (var s in segs)
            {
                if (s.End <= viewStart || s.Start >= viewEnd) continue;
                float x1 = X(s.Start) - 2, x2 = Math.Max(X(s.End), x1 + 3) + 2;   // 얇은 구간도 잡히게 여유
                if (p.X >= x1 && p.X <= x2) { best = s; if (p.X >= x1 + 2 && p.X <= x2 - 2) break; }
            }
            return best;
        }

        /// <summary>바깥(시간대별 그래프)에서 범위를 정해 확대.</summary>
        public void ShowRange(DateTime from, DateTime to) { SetView(from, to); }

        void SetView(DateTime from, DateTime to)
        {
            double span = (to - from).TotalMinutes;
            if (span < MinSpanMin) { var mid = from.AddMinutes(span / 2); from = mid.AddMinutes(-MinSpanMin / 2); to = mid.AddMinutes(MinSpanMin / 2); }
            if ((to - from).TotalDays >= 1) { from = day; to = day.AddDays(1); }
            if (from < day) { to = to + (day - from); from = day; }
            if (to > day.AddDays(1)) { from = from - (to - day.AddDays(1)); to = day.AddDays(1); }
            viewStart = from; viewEnd = to;
            userZoomed = true;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var at = TimeAt(e.X);
            double f = e.Delta > 0 ? 0.7 : 1 / 0.7;
            SetView(at.AddSeconds((viewStart - at).TotalSeconds * f), at.AddSeconds((viewEnd - at).TotalSeconds * f));
            lastTip = null; tip.Hide(this);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();   // 휠 입력을 받도록
            if (e.Button == MouseButtons.Left) { downAt = e.Location; dragging = false; dragStartView = viewStart; }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && (dragging || Math.Abs(e.X - downAt.X) > 4))
            {
                dragging = true;
                double secPerPx = (viewEnd - viewStart).TotalSeconds / Span;
                var span = viewEnd - viewStart;
                var from = dragStartView.AddSeconds(-(e.X - downAt.X) * secPerPx);
                SetView(from, from + span);
                Cursor = Cursors.SizeWE;
                lastTip = null; tip.Hide(this);
                return;
            }
            var s = Hit(e.Location);
            string text = s == null ? null
                : nameOf(s.AppId) + "\n" + Fmt.Clock(s.Start) + "–" + Fmt.Clock(s.End) + " (" + Fmt.Long(s.Seconds) + ")";
            if (text != lastTip)
            {
                lastTip = text;
                if (text == null) tip.Hide(this); else tip.Show(text, this, e.X + 12, e.Y + 16);
            }
            Cursor = s == null ? Cursors.Default : Cursors.Hand;
        }

        protected override void OnMouseLeave(EventArgs e) { lastTip = null; tip.Hide(this); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (dragging) { dragging = false; return; }
            var s = Hit(e.Location);
            if (s != null && SegmentClicked != null) SegmentClicked(s.AppId);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            FitToData();
            Invalidate();
        }

        public static void FillRounded(Graphics g, Brush b, RectangleF r, float radius)
        {
            using (var path = new GraphicsPath())
            {
                float d = radius * 2;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(b, path);
            }
        }
    }

    /// <summary>
    /// 막대그래프 한 계열(전체 또는 선택한 앱). 기본은 시간대별(0–23시), SetBars로 날짜별·월별 추이에도 쓴다.
    /// </summary>
    sealed class HourlyChart : Control
    {
        double[] values = new double[24];
        readonly ToolTip tip = new ToolTip { InitialDelay = 0, ReshowDelay = 0, AutoPopDelay = 10000 };
        int lastHour = -1;
        public Theme Theme = Theme.Light();
        public Color BarColor = Color.Empty;
        public string Caption = "";

        const int PadR = 12, PadT = 26, PadB = 20;
        int PadL = 52;   // 세로축 글자 폭에 맞춰 그릴 때마다 다시 정한다

        public HourlyChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        string[] axisLabels, tipLabels;

        /// <summary>시간대별(0–23시).</summary>
        public void SetData(double[] hourly, string caption, Color color)
        {
            var axis = new string[24]; var tips = new string[24];
            for (int h = 0; h < 24; h++) { axis[h] = h % 3 == 0 ? h + "시" : null; tips[h] = h + "시–" + (h + 1) + "시"; }
            SetBars(hourly ?? new double[24], axis, tips, caption, color);
        }

        /// <summary>임의 막대: 값, 아래 축 이름(null이면 생략), 툴팁 제목.</summary>
        public void SetBars(double[] vals, string[] axis, string[] tips, string caption, Color color)
        {
            values = vals; axisLabels = axis; tipLabels = tips;
            Caption = caption;
            BarColor = color;
            lastHour = -1;
            Invalidate();
        }

        /// <summary>눈금 간격: 최대값을 넘는 보기 좋은 값 (분 단위 또는 시간 단위).</summary>
        static double NiceMax(double max, out double step)
        {
            double[] steps = { 60, 300, 600, 900, 1800, 3600, 7200, 3 * 3600, 6 * 3600, 12 * 3600, 24 * 3600, 48 * 3600, 96 * 3600, 240 * 3600 };
            foreach (var s in steps)
                if (max <= s * 4) { step = s; return Math.Max(s, Math.Ceiling(max / s) * s); }
            step = Math.Ceiling(max / 4 / 3600) * 3600;
            return step * 4;
        }

        static string AxisLabel(double sec)
        {
            if (sec == 0) return "0";
            if (sec % 3600 == 0) return (sec / 3600) + "시간";
            return (sec / 60) + "분";
        }

        RectangleF Plot { get { return new RectangleF(PadL, PadT, Width - PadL - PadR, Height - PadT - PadB); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Back);
            double max = 0;
            foreach (var v in values) max = Math.Max(max, v);
            double step;
            double top = NiceMax(Math.Max(max, 1), out step);
            float widest = 0;
            for (double v = 0; v <= top + 0.1; v += step) widest = Math.Max(widest, g.MeasureString(AxisLabel(v), Font).Width);
            PadL = (int)Math.Ceiling(widest) + 10;
            var plot = Plot;

            using (var tb = new SolidBrush(Theme.Text))
            using (var bold = new Font(Font, FontStyle.Bold))
                g.DrawString(Caption, bold, tb, PadL - 4, 4);

            // 옅은 가로 눈금선
            using (var grid = new Pen(Theme.Grid))
            using (var mb = new SolidBrush(Theme.Subtle))
            {
                for (double v = 0; v <= top + 0.1; v += step)
                {
                    float y = plot.Bottom - (float)(v / top * plot.Height);
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    string s = AxisLabel(v);
                    var sz = g.MeasureString(s, Font);
                    g.DrawString(s, Font, mb, plot.Left - sz.Width - 4, y - sz.Height / 2);
                }
                int n = values.Length;
                float bw = plot.Width / n;
                for (int h = 0; h < n; h++)
                {
                    string s = axisLabels != null && h < axisLabels.Length ? axisLabels[h] : null;
                    if (s == null) continue;
                    var sz = g.MeasureString(s, Font);
                    g.DrawString(s, Font, mb, plot.Left + bw * h + bw / 2 - sz.Width / 2, plot.Bottom + 2);
                }
            }

            // 막대: 2px 간격, 위쪽만 둥글게
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int count = values.Length;
            float w = plot.Width / count;
            float gap = w > 6 ? 1 : 0;   // 막대 사이 2px 간격 (아주 좁으면 생략)
            using (var b = new SolidBrush(BarColor.IsEmpty ? Theme.Accent : BarColor))
            {
                for (int h = 0; h < count; h++)
                {
                    if (values[h] <= 0) continue;
                    float bh = Math.Max(2, (float)(values[h] / top * plot.Height));
                    float barW = Math.Min(w - 2 * gap, 40);   // 막대가 몇 개 안 될 때 너무 넓어지지 않게
                    var r = new RectangleF(plot.Left + w * h + (w - barW) / 2, plot.Bottom - bh, barW, bh);
                    FillTopRounded(g, b, r, Math.Min(4, Math.Min(r.Width / 2, r.Height)));
                    if (h == lastHour)
                        using (var hl = new SolidBrush(Color.FromArgb(40, Theme.Text))) g.FillRectangle(hl, r);
                }
            }
            if (max <= 0)
                using (var mb = new SolidBrush(Theme.Subtle))
                    g.DrawString("이 기간에는 활성 시간이 없어요", Font, mb, plot.Left + 8, plot.Top + plot.Height / 2 - 8);
        }

        static void FillTopRounded(Graphics g, Brush b, RectangleF r, float radius)
        {
            if (radius < 1) { g.FillRectangle(b, r); return; }
            using (var path = new GraphicsPath())
            {
                float d = radius * 2;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddLine(r.Right, r.Y + radius, r.Right, r.Bottom);
                path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
                path.CloseFigure();
                g.FillPath(b, path);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var plot = Plot;
            int h = -1;
            if (e.X >= plot.Left && e.X < plot.Right && e.Y >= plot.Top - 4 && e.Y <= plot.Bottom)
                h = Math.Min(values.Length - 1, (int)((e.X - plot.Left) / (plot.Width / values.Length)));
            if (h != lastHour)
            {
                lastHour = h;
                if (h < 0) tip.Hide(this);
                else tip.Show((tipLabels != null && h < tipLabels.Length ? tipLabels[h] : "") + "\n" + Fmt.Long(values[h]), this, e.X + 12, e.Y + 16);
                Invalidate();
            }
            Cursor = BarsClickable && h >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { lastHour = -1; tip.Hide(this); Invalidate(); }

        /// <summary>막대를 누르면 그 칸 번호(시간대별이면 시). 처리하는 쪽이 없으면 아무 일도 없다.</summary>
        public event Action<int> BarClicked;
        public bool BarsClickable;

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (BarsClickable && lastHour >= 0 && BarClicked != null) BarClicked(lastHour);
        }
    }
}
