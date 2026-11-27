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
    /// <summary>새 버전 안내 팝업: 지금 업데이트 / 나중에, "이 버전은 다시 알리지 않기".</summary>
    sealed class UpdateDialog : Form
    {
        readonly CheckBox skip = new CheckBox { Text = "이 버전은 다시 알리지 않기 (더 새 버전이 나오면 다시 알립니다)", AutoSize = true };
        public bool SkipThisVersion { get { return skip.Checked; } }

        public UpdateDialog(Theme theme, Version current, Version latest, string notes, string url)
        {
            Text = "업데이트 - 사용 시간 기록";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;   // 다른 창 뒤에 숨어 안 보이는 일이 없게 (자동 확인은 사용자가 띄운 창이 아니라서)
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(20, 16, 20, 16) };
            root.Controls.Add(new Label { Text = "새 버전이 있습니다", Font = new Font(Font.FontFamily, Font.Size * 1.3f, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 8) });
            root.Controls.Add(new Label { Text = "v" + latest.ToString(3) + "  (현재 v" + current.ToString(3) + ")", AutoSize = true });
            string body = (notes ?? "").Trim();
            if (body.Length > 800) body = body.Substring(0, 800) + " …";
            if (body.Length > 0)
                root.Controls.Add(new TextBox { Text = body.Replace("\r\n", "\n").Replace("\n", "\r\n"), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 420, Height = 120, Margin = new Padding(0, 10, 0, 4), BackColor = theme.Dark ? theme.Panel : Color.White, ForeColor = theme.Text });
            if (!string.IsNullOrEmpty(url))
            {
                var link = new LinkLabel { Text = "릴리스 페이지 열기", AutoSize = true, Margin = new Padding(0, 2, 0, 6) };
                link.LinkClicked += (s, e) => { try { Process.Start(url); } catch { } };
                root.Controls.Add(link);
            }
            root.Controls.Add(new Label { Text = "업데이트하면 기록 프로그램을 잠시 종료했다가 다시 실행합니다. 기록은 그대로 유지됩니다.", AutoSize = true, Tag = "subtle", MaximumSize = new Size(420, 0), Margin = new Padding(0, 6, 0, 6) });
            root.Controls.Add(skip);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
            var later = new Button { Text = "나중에", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 30) };
            var now = new Button { Text = "지금 업데이트", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(110, 30) };
            buttons.Controls.Add(later);
            buttons.Controls.Add(now);
            root.Controls.Add(buttons);
            Controls.Add(root);
            AcceptButton = now;
            CancelButton = later;
            Shown += (s, e) => now.Select();   // 변경 내용 글상자가 통째로 선택돼 보이지 않게
            Themer.Apply(this, theme);
            Load += (s, e) => Themer.TitleBar(this, theme);
        }
    }
}
