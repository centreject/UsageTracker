using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace UsageTracker
{
    /// <summary>
    /// 시작 메뉴 바로가기 "사용 시간 기록" (통계 창을 연다). 실제 모드에서 시작할 때 없거나 경로가 다르면 만든다.
    /// 제거 준비에서 지운다.
    /// </summary>
    static class StartMenu
    {
        public const string Name = "사용 시간 기록";

        static string LinkPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk"); }
        }

        /// <summary>바로가기를 만들거나 고친다. 새로 만들었으면 true. lnkPath는 시험용 (기본: 시작 메뉴).</summary>
        public static bool Ensure(string exePath, string lnkPath = null)
        {
            try
            {
                string lnk = lnkPath ?? LinkPath;
                if (File.Exists(lnk) && string.Equals(Target(lnk), exePath, StringComparison.OrdinalIgnoreCase)) return false;
                var link = (IShellLinkW)new ShellLink();
                try
                {
                    link.SetPath(exePath);
                    link.SetArguments("--ui");
                    link.SetWorkingDirectory(Path.GetDirectoryName(exePath));
                    link.SetDescription("프로그램별 사용 시간 보기");
                    link.SetIconLocation(exePath, 0);
                    ((IPersistFile)link).Save(lnk, true);
                }
                finally { Marshal.ReleaseComObject(link); }
                return true;
            }
            catch (Exception ex) { ErrorLog.Write("startmenu", ex); return false; }
        }

        public static void Remove()
        {
            try { if (File.Exists(LinkPath)) File.Delete(LinkPath); }
            catch (Exception ex) { ErrorLog.Write("startmenu", ex); }
        }

        public static string Target(string lnk)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(lnk, 0);
                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                return sb.ToString();
            }
            catch { return null; }
            finally { Marshal.ReleaseComObject(link); }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, int flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int cmd);
            void SetShowCmd(int cmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
            void Resolve(IntPtr hwnd, int flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }
    }
}
