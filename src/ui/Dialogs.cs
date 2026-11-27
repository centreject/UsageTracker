using System;
using System.Drawing;
using System.Windows.Forms;

namespace UsageTracker.Ui
{
    /// <summary>이름 입력 창 (이름 바꾸기·합치기). "원래 이름으로"를 누르면 빈 문자열을 돌려준다.</summary>
    sealed class NameDialog : Form
    {
        readonly TextBox box = new TextBox();
        public string Value { get { return box.Text; } }

        public NameDialog(Theme theme, string title, string prompt, string initial, string originalName)
        {
            Text = title;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(16), Dock = DockStyle.Fill };
            root.Controls.Add(new Label { Text = prompt, AutoSize = true, Margin = new Padding(0, 0, 0, 6) });
            box.Text = initial ?? "";
            box.Width = 320;
            box.Margin = new Padding(0, 0, 0, 6);
            root.Controls.Add(box);
            if (originalName != null)
                root.Controls.Add(new Label { Text = "원래 이름: " + originalName, AutoSize = true, Tag = "subtle", Margin = new Padding(0, 0, 0, 10) });

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = Padding.Empty };
            var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 30) };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 30) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            if (originalName != null)
            {
                var reset = new Button { Text = "원래 이름으로", AutoSize = true, MinimumSize = new Size(100, 30) };
                reset.Click += (s, e) => { box.Text = ""; DialogResult = DialogResult.OK; };
                buttons.Controls.Add(reset);
            }
            root.Controls.Add(buttons);
            Controls.Add(root);
            AcceptButton = ok;
            CancelButton = cancel;
            Themer.Apply(this, theme);
            Load += (s, e) => { Themer.TitleBar(this, theme); box.SelectAll(); box.Focus(); };
        }
    }
}
