using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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

    /// <summary>설정 창: 자리 비움 기준, 자동 시작, 데이터 폴더, 전체 기록 초기화, 제거 준비.</summary>
    sealed class SettingsForm : Form
    {
        readonly string dataDir;
        readonly bool devMode;
        readonly Theme theme;
        readonly Settings settings;
        readonly NumericUpDown idle = new NumericUpDown { Minimum = 1, Maximum = 120, Width = 70 };
        readonly CheckBox autoStart = new CheckBox { Text = "Windows 시작 시 실행", AutoSize = true };

        /// <summary>전체 초기화나 제거 준비를 했으면 true (메인 창이 다시 읽거나 닫는다).</summary>
        public bool DataChanged, Uninstalled;

        public SettingsForm(Theme theme, string dataDir, bool devMode)
        {
            this.theme = theme;
            this.dataDir = dataDir;
            this.devMode = devMode;
            settings = new Settings(dataDir);

            Text = "설정 - 사용 시간 기록";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(20, 16, 20, 16) };
            var bold = new Font(Font, FontStyle.Bold);

            root.Controls.Add(Header("기록", bold));
            var idleRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 2) };
            idleRow.Controls.Add(new Label { Text = "자리 비움 기준", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
            idle.Value = settings.IdleMinutes;
            idleRow.Controls.Add(idle);
            idleRow.Controls.Add(new Label { Text = "분 동안 입력이 없으면", AutoSize = true, Margin = new Padding(6, 6, 0, 0) });
            root.Controls.Add(idleRow);
            root.Controls.Add(Note("소리가 나거나 전체 화면이면 입력이 없어도 사용 중으로 봐요."));

            autoStart.Checked = settings.AutoStart;
            autoStart.Enabled = !devMode;
            autoStart.Margin = new Padding(0, 8, 0, 0);
            root.Controls.Add(autoStart);
            if (devMode) root.Controls.Add(Note("시험용 데이터 폴더로 실행 중이라 자동 시작은 바꿀 수 없어요."));

            root.Controls.Add(Header("데이터", bold));
            var folder = new LinkLabel { Text = dataDir, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 0, 0, 4) };
            folder.LinkClicked += (s, e) => { try { Process.Start("explorer.exe", "\"" + dataDir + "\""); } catch { } };
            root.Controls.Add(folder);
            root.Controls.Add(Note("기록은 이 PC에만 저장되고, 네트워크로 보내지 않아요. 창 제목은 저장하지 않아요."));

            var reset = new Button { Text = "전체 기록 초기화...", AutoSize = true, Tag = "danger", Margin = new Padding(0, 8, 0, 0) };
            reset.Click += (s, e) => ResetAll();
            root.Controls.Add(reset);

            var uninstall = new Button { Text = "제거 준비...", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            uninstall.Click += (s, e) => PrepareUninstall();
            root.Controls.Add(uninstall);
            root.Controls.Add(Note("자동 시작 등록을 지우고 기록 프로그램을 끝내요. 그다음 프로그램 폴더를 지우면 돼요."));

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0) };
            var save = new Button { Text = "저장", AutoSize = true, MinimumSize = new Size(80, 30) };
            var close = new Button { Text = "닫기", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 30) };
            save.Click += (s, e) => { Save(); Close(); };
            buttons.Controls.Add(close);
            buttons.Controls.Add(save);
            root.Controls.Add(buttons);

            Controls.Add(root);
            AcceptButton = save;
            CancelButton = close;
            Themer.Apply(this, theme);
            Load += (s, e) => Themer.TitleBar(this, theme);
        }

        static Label Header(string text, Font f)
        {
            return new Label { Text = text, Font = f, AutoSize = true, Margin = new Padding(0, 12, 0, 6) };
        }

        static Label Note(string text)
        {
            return new Label { Text = text, AutoSize = true, Tag = "subtle", MaximumSize = new Size(420, 0), Margin = new Padding(0, 2, 0, 2) };
        }

        void Save()
        {
            settings.IdleMinutes = (int)idle.Value;
            if (!devMode) settings.AutoStart = autoStart.Checked;
            settings.Save();
            if (!Recorder.Send(Tray.CmdReloadSettings) && !devMode)
                AutoStart.Apply(settings.AutoStart, Application.ExecutablePath);   // 기록 모드가 꺼져 있으면 직접
        }

        void ResetAll()
        {
            if (MessageBox.Show(this, "모든 사용 기록과 바꾼 이름·숨김·묶음을 지울까요?\n되돌릴 수 없어요. 설정은 남아요.",
                    "전체 기록 초기화", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            DataMaintenance.Request(dataDir, DataMaintenance.OpReset, null);
            DataChanged = true;
            MessageBox.Show(this, "모든 기록을 지웠어요. 지금부터 다시 기록해요.", "전체 기록 초기화", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void PrepareUninstall()
        {
            if (MessageBox.Show(this, "자동 시작 등록과 시작 메뉴 바로가기를 지우고 기록 프로그램을 끝낼까요?\n그 뒤에는 기록되지 않아요.",
                    "제거 준비", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            bool deleteData = MessageBox.Show(this, "기록 데이터도 지울까요?\n" + dataDir + "\n\n'아니요'를 고르면 데이터는 남겨요.",
                    "제거 준비", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

            if (!devMode)
            {
                settings.AutoStart = false;
                settings.Save();
                AutoStart.Apply(false, Application.ExecutablePath);
                StartMenu.Remove();
            }
            Recorder.CloseAndWait(5000);
            if (deleteData)
            {
                try { Directory.Delete(dataDir, true); }
                catch (Exception ex) { MessageBox.Show(this, "데이터 폴더를 지우지 못했어요: " + ex.Message, "제거 준비"); }
            }
            Uninstalled = true;
            MessageBox.Show(this, "준비가 끝났어요. 이제 프로그램 폴더를 지우면 돼요.\n" + Path.GetDirectoryName(Application.ExecutablePath),
                "제거 준비", MessageBoxButtons.OK, MessageBoxIcon.Information);
            try { Process.Start("explorer.exe", "/select,\"" + Application.ExecutablePath + "\""); } catch { }
            Close();
        }
    }
}
