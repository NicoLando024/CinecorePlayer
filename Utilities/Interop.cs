#nullable enable
using CinecorePlayer2025;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CinecorePlayer2025.Utilities
{
    // --------- Minimal MF interop ----------
    [ComImport, Guid("FA993888-4383-415A-A930-DD472A8CF6F7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFGetService
    {
        [PreserveSig]
        int GetService([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidService,
                       [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
                       [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
    }

    [ComImport, Guid("A490B1E4-AB84-4d31-A1B2-181E03B1077A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFVideoDisplayControl
    {
        void GetNativeVideoSize(out System.Drawing.Size pszVideo, out System.Drawing.Size pszARVideo);
        void GetIdealVideoSize(out System.Drawing.Size pszMin, out System.Drawing.Size pszMax);
        void SetVideoPosition(nint pnrcSource, [In] ref MFRect pnrcDest);
        void GetVideoPosition(out MFVideoNormalizedRect pnrcSource, out MFRect pnrcDest);
        void SetAspectRatioMode(int dwAspectRatioMode);
        void GetAspectRatioMode(out int pdwAspectRatioMode);
        void SetVideoWindow(nint hwndVideo);
        void GetVideoWindow(out nint phwndVideo);
        void RepaintVideo();
        void GetCurrentImage(out nint pDib, out int pcbDib, out long pTimeStamp);
        void SetBorderColor(int Clr);
        void GetBorderColor(out int pClr);
        void SetRenderingPrefs(int dwRenderFlags);
        void GetRenderingPrefs(out int pdwRenderFlags);
        void SetFullscreen(bool fFullscreen);
        void GetFullscreen(out bool pfFullscreen);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MFRect { public int left, top, right, bottom; public MFRect(int l, int t, int r, int b) { left = l; top = t; right = r; bottom = b; } }

    [StructLayout(LayoutKind.Sequential)]
    struct MFVideoNormalizedRect
    {
        public float left, top, right, bottom;
        public MFVideoNormalizedRect(float l, float t, float r, float b) { left = l; top = t; right = r; bottom = b; }
    }

    // ======= Core Audio session (volume sessione processo) =======
    internal static class CoreAudioSessionVolume
    {
        private static ISimpleAudioVolume? _simple;

        public static void Set(float vol01)
        {
            try { Ensure(); _simple?.SetMasterVolume(Math.Clamp(vol01, 0f, 1f), Guid.Empty); }
            catch { }
        }

        private static void Ensure()
        {
            if (_simple != null) return;

            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            var iid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iid, 0, nint.Zero, out var obj);
            var mgr = (IAudioSessionManager2)obj;
            mgr.GetSessionEnumerator(out var en);
            en.GetCount(out int count);
            int pid = Process.GetCurrentProcess().Id;

            for (int i = 0; i < count; i++)
            {
                en.GetSession(i, out var ctl);
                var ctl2 = (IAudioSessionControl2)ctl;
                ctl2.GetProcessId(out uint sessionPid);
                if (sessionPid == pid) { _simple = (ISimpleAudioVolume)ctl; break; }
                Marshal.ReleaseComObject(ctl);
            }
            Marshal.ReleaseComObject(en);
            Marshal.ReleaseComObject(mgr);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }

        #region COM interop
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }
        private enum EDataFlow { eRender, eCapture, eAll }
        private enum ERole { eConsole, eMultimedia, eCommunications }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        private interface IMMDeviceEnumerator
        {
            int NotImpl1();
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, nint pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
        private interface IAudioSessionManager2
        {
            int NotImpl1();
            int NotImpl2();
            int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
        private interface IAudioSessionEnumerator
        {
            int GetCount(out int SessionCount);
            int GetSession(int SessionCount, out IAudioSessionControl Session);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
        private interface IAudioSessionControl { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
        private interface IAudioSessionControl2
        {
            int NotImpl0(); int NotImpl1(); int NotImpl2(); int NotImpl3(); int NotImpl4(); int NotImpl5(); int NotImpl6(); int NotImpl7();
            int NotImpl8(); int NotImpl9(); int NotImpl10();
            int GetProcessId(out uint pRetVal);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
        private interface ISimpleAudioVolume
        {
            int SetMasterVolume(float fLevel, Guid EventContext);
            int GetMasterVolume(out float pfLevel);
            int SetMute(bool bMute, Guid EventContext);
            int GetMute(out bool pbMute);
        }
        #endregion
    }
}
