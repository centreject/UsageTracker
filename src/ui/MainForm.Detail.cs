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
    /// <summary>통계 창: 선택한 앱의 상세 패널·그래프·타임라인.</summary>
    partial class MainForm
    {

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
                AddNote("목록에서 앱을 고르면 자세한 정보와 시간대별 사용이 표시됩니다.");
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
                AddNote("두 번 누르거나 '숨긴 항목 보기'를 켜면 하나씩 볼 수 있습니다. 오른쪽 클릭으로 모두 숨김 해제하거나 기록을 지울 수 있습니다.");
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
                // CLI는 활성 시간이 없으므로(터미널 창이 가져감) 실행 기록으로 찾는다
                bool cli = ids.Count > 0 && ids.TrueForAll(id => id.StartsWith("cli:", StringComparison.OrdinalIgnoreCase));
                var last = UsageReader.LastUsed(dataDir, ids, DateTime.Now, cli ? 'R' : 'A', recorderAlive);
                AddField(cli ? "마지막 실행" : "마지막 사용", last == null ? "–"
                    : (last.Value >= DateTime.Now.AddMinutes(-1) && recorderAlive ? (cli ? "지금 실행 중" : "지금 사용 중") : last.Value.ToString("yyyy-MM-dd HH:mm")));
                if (ids.Count > 0 && ids.TrueForAll(id => excludedIds.Contains(id)))
                    AddNote("앞으로 기록하지 않는 앱입니다. 설정 → 기록 제외에서 다시 기록하게 할 수 있습니다.");
                if (cli) AddNote("터미널 안에서 실행된 프로그램입니다. 사용(활성) 시간은 터미널 창에 쌓이고, 여기에는 실행 시간만 기록됩니다.");

                if (group)
                {
                    AddHeader("묶인 앱");
                    foreach (var id in ids)
                    {
                        double a; rawActive.TryGetValue(id, out a);
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
                    else if (key == AppResolver.DesktopId) AddNote("바탕화면이 맨 앞에 있던 시간입니다.");
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
            int icoSz = ScalePx(24);
            if (icon != null) panel.Controls.Add(new PictureBox { Image = icon, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(icoSz, icoSz), Margin = new Padding(0, 4, 8, 0) });
            panel.Controls.Add(new Label { Text = text, Font = titleFont, AutoSize = true, ForeColor = theme.Text, MaximumSize = new Size(ScalePx(260), 0) });
            AddRow(panel, null);
        }

        void AddField(string name, string value)
        {
            var l = new Label { Text = name, AutoSize = true, ForeColor = theme.Subtle, Margin = new Padding(0, 3, 12, 3) };
            var v = new Label { Text = value, AutoSize = true, ForeColor = theme.Text, Margin = new Padding(0, 3, 0, 3), MaximumSize = new Size(ScalePx(200), 0) };
            AddRow(l, v);
        }

        void AddHeader(string text)
        {
            AddRow(new Label { Text = text, AutoSize = true, Font = boldFont, ForeColor = theme.Text, Margin = new Padding(0, 12, 0, 4) }, null);
        }

        void AddNote(string text)
        {
            AddRow(new Label { Text = text, AutoSize = true, ForeColor = theme.Subtle, Margin = new Padding(0, 12, 0, 0), MaximumSize = new Size(ScalePx(280), 0) }, null);
        }
    }
}
