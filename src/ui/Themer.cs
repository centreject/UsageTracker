using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UsageTracker.Ui
{
    /// <summary>컨트롤 트리에 라이트·다크 색을 입힌다. 제목 표시줄과 스크롤바도 다크로 바꾼다.</summary>
    static class Themer
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        public static void TitleBar(Form f, Theme t)
        {
            int v = t.Dark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(f.Handle, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref v, 4) != 0)
                    DwmSetWindowAttribute(f.Handle, 19, ref v, 4);   // 이전 Windows 10 빌드
            }
            catch { }
        }

        public static void Apply(Control c, Theme t)
        {
            if (c is Form || c is Panel || c is SplitterPanel || c is TableLayoutPanel || c is FlowLayoutPanel || c is UserControl)
            {
                c.BackColor = c.Tag as string == "panel" ? t.Panel : t.Back;
                c.ForeColor = t.Text;
            }
            else if (c is SplitContainer)
            {
                c.BackColor = t.Border;   // 가르는 선
            }
            else if (c is LinkLabel)
            {
                var l = (LinkLabel)c;
                l.LinkColor = t.Accent; l.ActiveLinkColor = t.Accent; l.VisitedLinkColor = t.Accent;
            }
            else if (c is Label)
            {
                c.ForeColor = c.Tag as string == "subtle" ? t.Subtle : (c.Tag as string == "danger" ? Danger(t) : t.Text);
            }
            else if (c is RadioButton && ((RadioButton)c).Appearance == Appearance.Button)
            {
                var r = (RadioButton)c;
                r.FlatAppearance.BorderColor = t.Border;
                r.FlatAppearance.CheckedBackColor = t.Accent;
                r.FlatAppearance.MouseOverBackColor = r.Checked ? t.Accent : t.Track;
                r.BackColor = t.Back;
                r.ForeColor = r.Checked ? Color.White : t.Text;
            }
            else if (c is ButtonBase)
            {
                var b = (ButtonBase)c;
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = t.Border;
                b.FlatAppearance.MouseOverBackColor = t.Track;
                b.BackColor = t.Back;
                b.ForeColor = c.Tag as string == "danger" ? Danger(t) : t.Text;
            }
            else if (c is TextBox || c is NumericUpDown)
            {
                c.BackColor = t.Dark ? t.Panel : Color.White;
                c.ForeColor = t.Text;
            }
            else if (c is ListView)
            {
                c.BackColor = t.Back;
                c.ForeColor = t.Text;
                if (c.IsHandleCreated) SetWindowTheme(c.Handle, t.Dark ? "DarkMode_ItemsView" : "ItemsView", null);
                else c.HandleCreated += (s, e) => SetWindowTheme(((Control)s).Handle, t.Dark ? "DarkMode_ItemsView" : "ItemsView", null);
            }
            else if (c is TimelineControl) { ((TimelineControl)c).Theme = t; c.Invalidate(); }
            else if (c is HourlyChart) { ((HourlyChart)c).Theme = t; c.Invalidate(); }

            if (c.ContextMenuStrip != null) Menu(c.ContextMenuStrip, t);
            foreach (Control child in c.Controls) Apply(child, t);
        }

        public static Color Danger(Theme t) { return t.Dark ? Color.FromArgb(0xe6, 0x67, 0x67) : Color.FromArgb(0xd0, 0x3b, 0x3b); }

        public static void Menu(ToolStrip m, Theme t)
        {
            m.Renderer = new ToolStripProfessionalRenderer(new MenuColors(t));
            m.BackColor = t.Panel;
            m.ForeColor = t.Text;
            foreach (ToolStripItem i in m.Items) i.ForeColor = i.Tag as string == "danger" ? Danger(t) : t.Text;
        }

        sealed class MenuColors : ProfessionalColorTable
        {
            readonly Theme t;
            public MenuColors(Theme t) { this.t = t; UseSystemColors = false; }
            public override Color ToolStripDropDownBackground { get { return t.Panel; } }
            public override Color ImageMarginGradientBegin { get { return t.Panel; } }
            public override Color ImageMarginGradientMiddle { get { return t.Panel; } }
            public override Color ImageMarginGradientEnd { get { return t.Panel; } }
            public override Color MenuBorder { get { return t.Border; } }
            public override Color MenuItemBorder { get { return t.Track; } }
            public override Color MenuItemSelected { get { return t.Track; } }
            public override Color SeparatorDark { get { return t.Border; } }
            public override Color SeparatorLight { get { return t.Panel; } }
        }
    }
}
