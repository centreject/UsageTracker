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
    /// <summary>통계 창: 기간 계산과 기록 읽기·목록 채우기.</summary>
    partial class MainForm
    {

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

        readonly Dictionary<Period, int> periodWidth = new Dictionary<Period, int>();

        /// <summary>그 기간 종류에서 나올 수 있는 가장 긴 글자의 너비 (배율이 바뀌면 OnDpiRescale에서 비운다).</summary>
        int PeriodLabelWidth()
        {
            int w;
            if (periodWidth.TryGetValue(period, out w)) return w;
            string[] samples;
            switch (period)
            {
                case Period.Week: samples = new[] { "8888년 88월 88일 – 88월 88일" }; break;
                case Period.Month: samples = new[] { "8888년 88월" }; break;
                case Period.Year: samples = new[] { "8888년" }; break;
                default: samples = "월화수목금토일".Select(d => "8888년 88월 88일 (" + d + ") · 오늘").ToArray(); break;
            }
            foreach (var s in samples) w = Math.Max(w, TextRenderer.MeasureText(s, periodLabel.Font).Width);
            w += 4;
            periodWidth[period] = w;
            return w;
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
            // 기간 종류마다 가장 긴 글자에 맞춘 고정 너비: 앞뒤로 넘길 때 ◀ ▶ 버튼이 움직이지 않게
            periodLabel.Size = new Size(Math.Max(PeriodLabelWidth(), TextRenderer.MeasureText(periodLabel.Text, periodLabel.Font).Width + 4), prevBtn.Height);
            nextBtn.Enabled = to <= DateTime.Today;
            todayBtn.Enabled = !IsCurrentPeriod();

            recorderAlive = Recorder.RequestFlush();
            var now = DateTime.Now;
            catalog = new AppCatalog(dataDir);
            excludedIds = new HashSet<string>(new Settings(dataDir).Exclude, StringComparer.OrdinalIgnoreCase);
            List<RawSeg> segs;
            bool readFailed = false;
            try { segs = UsageReader.Read(dataDir, from, to, now, recorderAlive); }
            catch (Exception ex) { segs = new List<RawSeg>(); readFailed = true; status.Text = "기록을 읽지 못했습니다: " + ex.Message; }
            // 숨긴 항목은 "숨긴 앱" 한 줄로 모은다 ("숨긴 항목 보기"를 켜면 하나씩 흐리게).
            // 전체 합계·비율·그래프에는 숨긴 항목도 포함한다.
            sumKeys = UsageReader.Summarize(segs, catalog.KeyOf, from, to);
            hiddenInPeriod = sumKeys.Active.Keys.Concat(sumKeys.Running.Keys)
                .Where(k => catalog.IsHidden(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            sum = (showHidden || hiddenInPeriod.Count == 0) ? sumKeys : UsageReader.Summarize(segs, ViewKey, from, to);
            rawActive = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in segs)
            {
                if (s.Kind != 'A') continue;
                double v; rawActive.TryGetValue(s.AppId, out v); rawActive[s.AppId] = v + s.Seconds;
            }

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
                rows.Add(new Row { Key = k, Name = NameOf(k), Active = a, Running = r, Hidden = hidden, Favorite = k != HiddenKey && catalog.IsFavorite(k) });
                total += a;
            }
            foreach (var row in rows) row.Ratio = total > 0 ? row.Active / total : 0;

            Populate(SelectedKeys());

            bool isDay = period == Period.Day;
            timeline.Visible = isDay;
            hourly.BarsClickable = isDay;   // 일 보기: 시간대 막대를 누르면 위 타임라인을 그 1시간으로 확대
            if (isDay) timeline.SetData(from, sum.ActiveSegments, NameOf);

            appCount = keys.Count - (keys.Contains(HiddenKey) ? 1 : 0) - (showHidden ? hiddenInPeriod.Count : 0);   // 숨기지 않은 앱 수
            summary.Text = "PC 켜짐 " + Fmt.Short(sum.PcOn) + "   ·   실제 사용 " + Fmt.Short(sum.PcUsed)
                + "   ·   앱 활성 합계 " + Fmt.Short(total) + "   ·   앱 " + appCount + "개"
                + (hiddenInPeriod.Count > 0 ? "   ·   숨김 " + hiddenInPeriod.Count + "개" : "");
            if (!readFailed)
                status.Text = recorderAlive ? "기록 중 · " + now.ToString("HH:mm") + " 기준" : "기록 프로그램이 꺼져 있습니다";
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
                if (favOnly && !r.Favorite) continue;   // 보기만 거른다 (합계·비율은 그대로)
                if (search.Length > 0 && !Matches(r)) continue;
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
    }
}
