using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace UsageTracker.Ui
{
    /// <summary>설정 창: 자리 비움 기준, 자동 시작, 데이터 폴더, 전체 기록 초기화, 제거 준비.</summary>
    sealed class SettingsForm : Form
    {
        readonly string dataDir;
        readonly bool devMode;
        readonly Theme theme;
        readonly Settings settings;
        readonly NumericUpDown idle = new NumericUpDown { Minimum = 1, Maximum = 120, Width = 70 };
        readonly CheckBox autoStart = new CheckBox { Text = "Windows 시작 시 실행", AutoSize = true };
        bool autoStartInitially;
        readonly CheckBox updateCheck = new CheckBox { Text = "새 버전 알림 (일주일에 한 번 확인)", AutoSize = true };
        List<string> excluded = new List<string>();
        readonly ListBox excludeList = new ListBox { SelectionMode = SelectionMode.MultiExtended, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };

        sealed class ExcludeItem
        {
            public string Id, Name;
            public override string ToString() { return Name; }
        }

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
            root.Controls.Add(Note("소리가 나거나 전체 화면이면, 또는 다른 모니터에서 소리 나는 창을 보고 있으면 입력이 없어도 사용 중으로 봅니다. 게임패드 입력도 사용으로 봅니다."));

            // 실제로 자동 시작되는지 보여 준다: Windows 설정 → 시작 앱에서 꺼 두었으면 체크 해제로 보인다
            bool winOff = !devMode && AutoStart.DisabledInWindows();
            autoStart.Checked = settings.AutoStart && !winOff;
            autoStartInitially = autoStart.Checked;
            // 비활성 체크 상자는 다크 테마에서 글씨가 거의 안 보이므로, 끄지 않고 누를 수만 없게 한다
            if (devMode) { autoStart.AutoCheck = false; autoStart.Tag = "subtle"; autoStart.Cursor = Cursors.Default; }
            autoStart.Margin = new Padding(0, 8, 0, 0);
            root.Controls.Add(autoStart);
            if (devMode) root.Controls.Add(Note("시험용 데이터 폴더로 실행 중이므로 자동 시작은 바꿀 수 없습니다."));
            else if (winOff && settings.AutoStart)
                root.Controls.Add(Note("Windows 설정 → 앱 → 시작 앱에서 꺼 둔 상태입니다. 여기서 켜고 저장하면 다시 켜집니다."));

            root.Controls.Add(Header("업데이트", bold));
            updateCheck.Checked = settings.UpdateCheck;
            root.Controls.Add(updateCheck);
            var checkNow = new Button { Text = "지금 확인", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            checkNow.Click += (s, e) =>
            {
                try
                {
                    Process.Start(Application.ExecutablePath, "--ui --update-check --manual" + (devMode ? " \"--data=" + dataDir + "\"" : ""));
                }
                catch (Exception ex) { MessageBox.Show(this, "업데이트 확인을 시작하지 못했습니다: " + ex.Message, "업데이트"); }
            };
            root.Controls.Add(checkNow);
            var st = UpdateState.Load(dataDir);
            root.Controls.Add(Note("현재 v" + Updater.CurrentVersion().ToString(3)
                + (st.LastCheck == DateTime.MinValue ? "" : " · 마지막 확인 " + st.LastCheck.ToString("yyyy-MM-dd"))
                + ". 확인할 때 GitHub에서 버전 정보만 받습니다. 새 버전이 있으면 설치할지 묻습니다."));

            // 기록 제외: 목록에서 고른 앱을 다시 기록하게 할 수 있다 (저장할 때 적용)
            root.Controls.Add(Header("기록 제외", bold));
            excluded = settings.Exclude;
            if (excluded.Count == 0)
                root.Controls.Add(Note("기록하지 않는 앱이 없습니다. 통계 창 목록에서 앱을 오른쪽 클릭 → '앞으로 기록하지 않기'로 지정합니다."));
            else
            {
                var catalog = new AppCatalog(dataDir);
                excludeList.Width = 300;
                excludeList.Height = Math.Min(6, excluded.Count) * excludeList.ItemHeight + 6;
                foreach (var id in excluded) excludeList.Items.Add(new ExcludeItem { Id = id, Name = catalog.DisplayName(id) });
                excludeList.BackColor = theme.Dark ? theme.Panel : Color.White;
                excludeList.ForeColor = theme.Text;
                excludeList.Margin = new Padding(0, 0, 0, 4);
                root.Controls.Add(excludeList);
                var include = new Button { Text = "선택한 앱 다시 기록", AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
                include.Click += (s, e) =>
                {
                    var sel = new List<object>();
                    foreach (var o in excludeList.SelectedItems) sel.Add(o);
                    foreach (var o in sel) excludeList.Items.Remove(o);
                };
                root.Controls.Add(include);
                root.Controls.Add(Note("지난 기록은 남아 있습니다. 저장하면 바로 적용됩니다."));
            }

            root.Controls.Add(Header("데이터", bold));
            var folder = new LinkLabel { Text = dataDir, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 0, 0, 4) };
            folder.LinkClicked += (s, e) => { try { Process.Start("explorer.exe", "\"" + dataDir + "\""); } catch { } };
            root.Controls.Add(folder);
            root.Controls.Add(Note("기록은 이 PC에만 저장되고, 네트워크로 보내지 않습니다. 창 제목은 저장하지 않습니다. (업데이트 확인 때만 GitHub에서 버전 정보를 받습니다.)"));

            var reset = new Button { Text = "전체 기록 초기화...", AutoSize = true, Tag = "danger", Margin = new Padding(0, 8, 0, 0) };
            reset.Click += (s, e) => ResetAll();
            root.Controls.Add(reset);

            var uninstall = new Button { Text = "제거 준비...", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            uninstall.Click += (s, e) => PrepareUninstall();
            root.Controls.Add(uninstall);
            root.Controls.Add(Note("자동 시작 등록을 지우고 기록 프로그램을 종료합니다. 그다음 프로그램 폴더를 지우면 됩니다."));

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0) };
            var save = new Button { Text = "저장", AutoSize = true, MinimumSize = new Size(80, 30) };
            var close = new Button { Text = "닫기", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 30) };
            save.Click += (s, e) => { Save(); Close(); };
            // 트레이 "설정..."은 이 창을 단독 창(Application.Run)으로 띄운다. 그때는 DialogResult만으로 닫히지 않으므로 직접 닫는다 (Esc도 이 버튼)
            close.Click += (s, e) => Close();
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
            settings.UpdateCheck = updateCheck.Checked;
            if (excluded.Count > 0)
            {
                // 다른 창에서 그사이 더한 제외 앱은 지우지 않도록, 이 창에서 뺀 것만 빼서 저장
                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ExcludeItem it in excludeList.Items) keep.Add(it.Id);
                var latest = new Settings(dataDir).Exclude;
                settings.Exclude = latest.FindAll(id => keep.Contains(id) || !excluded.Contains(id));
            }
            else settings.Exclude = new Settings(dataDir).Exclude;
            settings.Save();
            // 사용자가 이 창에서 직접 켰으면 Windows 쪽 "사용 안 함"도 다시 켠다
            if (!devMode && autoStart.Checked && !autoStartInitially)
                AutoStart.Apply(true, Application.ExecutablePath, true);
            if (!Recorder.Send(Tray.CmdReloadSettings) && !devMode)
                AutoStart.Apply(settings.AutoStart, Application.ExecutablePath);   // 기록 모드가 꺼져 있으면 직접
        }

        void ResetAll()
        {
            if (MessageBox.Show(this, "모든 사용 기록과 바꾼 이름·숨김·묶음을 지우시겠습니까?\n되돌릴 수 없습니다. 설정은 유지됩니다.",
                    "전체 기록 초기화", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            DataMaintenance.Request(dataDir, DataMaintenance.OpReset, null);
            DataChanged = true;
            MessageBox.Show(this, "모든 기록을 지웠습니다. 지금부터 다시 기록합니다.", "전체 기록 초기화", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void PrepareUninstall()
        {
            if (MessageBox.Show(this, "자동 시작 등록과 시작 메뉴 바로가기를 지우고 기록 프로그램을 종료하시겠습니까?\n그 뒤에는 기록되지 않습니다.",
                    "제거 준비", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            bool deleteData = MessageBox.Show(this, "기록 데이터도 지우시겠습니까?\n" + dataDir + "\n\n'아니요'를 고르면 데이터는 남겨 둡니다.",
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
                catch (Exception ex) { MessageBox.Show(this, "데이터 폴더를 지우지 못했습니다: " + ex.Message, "제거 준비"); }
            }
            Uninstalled = true;
            MessageBox.Show(this, "준비가 끝났습니다. 이제 프로그램 폴더를 지우면 됩니다.\n" + Path.GetDirectoryName(Application.ExecutablePath),
                "제거 준비", MessageBoxButtons.OK, MessageBoxIcon.Information);
            try { Process.Start("explorer.exe", "/select,\"" + Application.ExecutablePath + "\""); } catch { }
            Close();
        }
    }
}
