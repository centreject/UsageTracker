using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UsageTracker.Ui
{
    /// <summary>시간 표시 형식.</summary>
    static class Fmt
    {
        /// <summary>목록·요약용: "3시간 25분", 1분 미만은 "1분 미만".</summary>
        public static string Short(double sec)
        {
            if (sec <= 0) return "0분";
            if (sec < 60) return "1분 미만";
            long m = (long)(sec / 60), h = m / 60;
            m %= 60;
            if (h == 0) return m + "분";
            return m == 0 ? h + "시간" : h + "시간 " + m + "분";
        }

        /// <summary>상세·툴팁용: "3시간 25분 12초".</summary>
        public static string Long(double sec)
        {
            long s = (long)Math.Round(Math.Max(0, sec));
            long h = s / 3600, m = s % 3600 / 60, ss = s % 60;
            if (h > 0) return h + "시간 " + m + "분 " + ss + "초";
            if (m > 0) return m + "분 " + ss + "초";
            return ss + "초";
        }

        public static string Pct(double ratio) { return (ratio * 100).ToString("0.0") + "%"; }

        public static string Clock(DateTime t) { return t.ToString("HH:mm"); }
    }

    /// <summary>화면 색. 5단계에서 다크 모드 색을 추가한다.</summary>
    sealed class Theme
    {
        public Color Back, Panel, Text, Subtle, Border, Grid, Accent, Track;
        public bool Dark;

        /// <summary>Windows 설정(앱 모드: 라이트/다크)을 따른다.</summary>
        /// <summary>화면 확인용 강제 지정 (--theme=dark|light). null이면 시스템 설정.</summary>
        public static string Forced;

        public static Theme FromSystem()
        {
            if (Forced == "dark") return DarkTheme();
            if (Forced == "light") return Light();
            return SystemUsesDark() ? DarkTheme() : Light();
        }

        public static bool SystemUsesDark()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        public static Theme DarkTheme()
        {
            return new Theme
            {
                Dark = true,
                Back = Hex(0x1a1a19), Panel = Hex(0x232322), Text = Hex(0xf0efec),
                Subtle = Hex(0xc3c2b7), Border = Hex(0x383835),
                Grid = Hex(0x2c2c2a), Accent = Hex(0x3987e5), Track = Hex(0x2c2c2a)
            };
        }

        public static Theme Light()
        {
            return new Theme
            {
                Back = Hex(0xfcfcfb), Panel = Hex(0xf9f9f7), Text = Hex(0x0b0b0b),
                Subtle = Hex(0x52514e), Border = Hex(0xe1e0d9),
                Grid = Hex(0xe1e0d9), Accent = Hex(0x2a78d6), Track = Hex(0xf0efec)
            };
        }

        // 검증된 8색 범주 팔레트 (색각 이상 구분 확인된 순서). 앱마다 처음 기록된 순서로 고정 배정한다
        // (색은 순위가 아니라 앱을 따라간다). 9번째부터는 다시 돌므로 범례·툴팁이 항상 이름을 함께 보여 준다.
        static readonly Color[] LightPalette =
        {
            Hex(0x2a78d6), Hex(0xeb6834), Hex(0x1baf7a), Hex(0xeda100), Hex(0xe87ba4), Hex(0x008300), Hex(0x6250d6), Hex(0xe34948)
        };
        static readonly Color[] DarkPalette =
        {
            Hex(0x3987e5), Hex(0xd95926), Hex(0x199e70), Hex(0xc98500), Hex(0xd55181), Hex(0x0ca30c), Hex(0x9085e9), Hex(0xe66767)
        };

        static Color Hex(int rgb) { return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        /// <summary>색 슬롯 번호(처음 기록된 순서)로 색을 고른다.</summary>
        public Color SlotColor(int slot)
        {
            var p = Dark ? DarkPalette : LightPalette;
            return p[((slot % p.Length) + p.Length) % p.Length];
        }
    }

    /// <summary>앱 아이콘. exe에서 꺼내고, 못 꺼내면 기본 아이콘.</summary>
    sealed class IconCache
    {
        readonly Dictionary<string, Image> cache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        readonly int size;
        readonly Image fallback;

        public IconCache(int size)
        {
            this.size = size;
            fallback = Scale(SystemIcons.Application.ToBitmap());
        }

        public Image Get(string key, string path)
        {
            Image img;
            if (cache.TryGetValue(key, out img)) return img;
            img = null;
            try
            {
                if (key == AppResolver.DesktopId) img = Scale(ShellIcon(34));              // shell32 34 = 바탕화면
                else if (key == "::hidden") img = Scale(ShellIcon(4));                     // shell32 4 = 폴더 (숨긴 앱 모음)
                else if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    using (var ico = Icon.ExtractAssociatedIcon(path)) img = Scale(ico.ToBitmap());
                }
            }
            catch { }
            if (img == null) img = fallback;
            cache[key] = img;
            return img;
        }

        Image Scale(Image src)
        {
            if (src == null) return null;
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, size, size);
            }
            src.Dispose();
            return bmp;
        }

        static Image ShellIcon(int index)
        {
            IntPtr large, small;
            if (ExtractIconEx(Path.Combine(Environment.SystemDirectory, "shell32.dll"), index, out large, out small, 1) == 0) return null;
            try { return Icon.FromHandle(large).ToBitmap(); }
            finally { DestroyIcon(large); DestroyIcon(small); }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    }

    /// <summary>ListView 머리글에 정렬 화살표를 표시한다.</summary>
    static class SortArrow
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct HDITEM
        {
            public uint mask; public int cxy; public IntPtr pszText; public IntPtr hbm; public int cchTextMax;
            public int fmt; public IntPtr lParam; public int iImage; public int iOrder; public uint type; public IntPtr pvFilter; public uint state;
        }
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "SendMessage")] static extern IntPtr SendHd(IntPtr h, uint msg, IntPtr w, ref HDITEM l);

        [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr h, IntPtr rect, bool erase);

        /// <summary>머리글을 직접 그릴 때(OwnerDraw) 정렬이 바뀌면 다시 그리게 한다.</summary>
        public static void RepaintHeader(ListView lv)
        {
            IntPtr header = SendMessage(lv.Handle, 0x101F /*LVM_GETHEADER*/, IntPtr.Zero, IntPtr.Zero);
            if (header != IntPtr.Zero) InvalidateRect(header, IntPtr.Zero, true);
        }

        public static void Set(ListView lv, int column, bool ascending)
        {
            IntPtr header = SendMessage(lv.Handle, 0x101F /*LVM_GETHEADER*/, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < lv.Columns.Count; i++)
            {
                var item = new HDITEM { mask = 0x4 /*HDI_FORMAT*/ };
                SendHd(header, 0x120B /*HDM_GETITEMW*/, (IntPtr)i, ref item);
                item.fmt &= ~(0x400 | 0x200);
                if (i == column) item.fmt |= ascending ? 0x400 /*HDF_SORTUP*/ : 0x200 /*HDF_SORTDOWN*/;
                SendHd(header, 0x120C /*HDM_SETITEMW*/, (IntPtr)i, ref item);
            }
        }
    }
}
