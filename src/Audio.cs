using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace UsageTracker
{
    /// <summary>지금 소리를 내고 있는 프로세스(WASAPI 세션 상태가 Active)를 찾는다. 1분에 한 번만 호출한다.</summary>
    static class Audio
    {
        static readonly Guid MMDeviceEnumeratorClsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

        public static HashSet<uint> ActivePids()
        {
            var result = new HashSet<uint>();
            object enumObj = null; IMMDeviceCollection devices = null;
            try
            {
                enumObj = Activator.CreateInstance(Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid));
                ((IMMDeviceEnumerator)enumObj).EnumAudioEndpoints(0 /*eRender*/, 1 /*DEVICE_STATE_ACTIVE*/, out devices);
                if (devices == null) return result;
                uint count; devices.GetCount(out count);
                var iid = typeof(IAudioSessionManager2).GUID;
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice dev = null; object mgrObj = null; IAudioSessionEnumerator sessions = null;
                    try
                    {
                        devices.Item(i, out dev);
                        if (dev == null) continue;
                        dev.Activate(ref iid, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out mgrObj);
                        if (mgrObj == null) continue;
                        ((IAudioSessionManager2)mgrObj).GetSessionEnumerator(out sessions);
                        if (sessions == null) continue;
                        int n; sessions.GetCount(out n);
                        for (int s = 0; s < n; s++)
                        {
                            object ctlObj = null;
                            try
                            {
                                sessions.GetSession(s, out ctlObj);
                                var ctl = ctlObj as IAudioSessionControl2;
                                if (ctl == null) continue;
                                int state; ctl.GetState(out state);
                                if (state != 1 /*AudioSessionStateActive*/) continue;
                                uint pid; ctl.GetProcessId(out pid);
                                if (pid != 0) result.Add(pid);
                            }
                            finally { Release(ctlObj); }
                        }
                    }
                    finally { Release(sessions); Release(mgrObj); Release(dev); }
                }
            }
            catch (Exception ex) { ErrorLog.Write("audio", ex); }
            finally { Release(devices); Release(enumObj); }
            return result;
        }

        static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
        }
    }

    // ---------- WASAPI COM 인터페이스 (vtable 순서 그대로, 필요한 앞부분만) ----------
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr ctx);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr ctx);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(IntPtr param, IntPtr ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }
}
