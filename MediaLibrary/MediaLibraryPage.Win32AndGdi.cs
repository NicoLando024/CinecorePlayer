using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Net.Sockets;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;
using System;

#nullable enable

namespace CinecorePlayer2025
{
    internal sealed partial class MediaLibraryPage
    {
        // ------------ Lettura durata media da proprietà shell (System.Media.Duration) ------------
        private static class ShellDurationUtil
        {
            private enum HRESULT : int
            {
                S_OK = 0,
                S_FALSE = 1
            }

            [Flags]
            private enum GETPROPERTYSTOREFLAGS
            {
                GPS_DEFAULT = 0,
                GPS_HANDLERPROPERTIESONLY = 0x1,
                GPS_READWRITE = 0x2,
                GPS_TEMPORARY = 0x4,
                GPS_FASTPROPERTIESONLY = 0x8,
                GPS_OPENSLOWITEM = 0x10,
                GPS_DELAYCREATION = 0x20,
                GPS_BESTEFFORT = 0x40,
                GPS_NO_OPLOCK = 0x80,
                GPS_PREFERQUERYPROPERTIES = 0x100,
                GPS_EXTRINSICPROPERTIES = 0x200,
                GPS_EXTRINSICPROPERTIESONLY = 0x400,
                GPS_VOLATILEPROPERTIES = 0x800,
                GPS_VOLATILEPROPERTIESONLY = 0x1000,
                GPS_MASK_VALID = 0x1FFF
            }

            [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IPropertyStore
            {
                HRESULT GetCount(out uint propertyCount);
                HRESULT GetAt(uint propertyIndex, out PROPERTYKEY key);
                HRESULT GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
                HRESULT SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
                HRESULT Commit();
            }

            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            private struct PROPERTYKEY
            {
                private readonly Guid _fmtid;
                private readonly uint _pid;

                public PROPERTYKEY(Guid fmtid, uint pid)
                {
                    _fmtid = fmtid;
                    _pid = pid;
                }
            }

            [StructLayout(LayoutKind.Sequential, Pack = 0)]
            private struct PROPARRAY
            {
                public uint cElems;
                public IntPtr pElems;
            }

            [StructLayout(LayoutKind.Explicit, Pack = 1)]
            private struct PROPVARIANT
            {
                [FieldOffset(0)]
                public ushort varType;
                [FieldOffset(2)]
                public ushort wReserved1;
                [FieldOffset(4)]
                public ushort wReserved2;
                [FieldOffset(6)]
                public ushort wReserved3;

                [FieldOffset(8)]
                public byte bVal;
                [FieldOffset(8)]
                public sbyte cVal;
                [FieldOffset(8)]
                public ushort uiVal;
                [FieldOffset(8)]
                public short iVal;
                [FieldOffset(8)]
                public uint uintVal;
                [FieldOffset(8)]
                public int intVal;
                [FieldOffset(8)]
                public ulong ulVal;
                [FieldOffset(8)]
                public long lVal;
                [FieldOffset(8)]
                public float fltVal;
                [FieldOffset(8)]
                public double dblVal;
                [FieldOffset(8)]
                public short boolVal;
                [FieldOffset(8)]
                public IntPtr pclsidVal;
                [FieldOffset(8)]
                public IntPtr pszVal;
                [FieldOffset(8)]
                public IntPtr pwszVal;
                [FieldOffset(8)]
                public IntPtr punkVal;
                [FieldOffset(8)]
                public PROPARRAY ca;
                [FieldOffset(8)]
                public System.Runtime.InteropServices.ComTypes.FILETIME filetime;
            }

            private enum VARENUM
            {
                VT_EMPTY = 0,
                VT_NULL = 1,
                VT_I2 = 2,
                VT_I4 = 3,
                VT_R4 = 4,
                VT_R8 = 5,
                VT_CY = 6,
                VT_DATE = 7,
                VT_BSTR = 8,
                VT_DISPATCH = 9,
                VT_ERROR = 10,
                VT_BOOL = 11,
                VT_VARIANT = 12,
                VT_UNKNOWN = 13,
                VT_DECIMAL = 14,
                VT_I1 = 16,
                VT_UI1 = 17,
                VT_UI2 = 18,
                VT_UI4 = 19,
                VT_I8 = 20,
                VT_UI8 = 21,
                VT_INT = 22,
                VT_UINT = 23,
                VT_FILETIME = 64
            }

            [DllImport("Shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern HRESULT SHGetPropertyStoreFromParsingName(
                string pszPath,
                IntPtr pbc,
                GETPROPERTYSTOREFLAGS flags,
                ref Guid iid,
                [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

            [DllImport("ole32.dll")]
            private static extern int PropVariantClear(ref PROPVARIANT pvar);

            // System.Media.Duration (UInt64, unità da 100ns)
            private static readonly PROPERTYKEY PKEY_Media_Duration =
                new PROPERTYKEY(new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), 3);

            public static double? TryGetDurationMinutes(string path)
            {
                IPropertyStore? store = null;
                PROPVARIANT pv = default;

                try
                {
                    Guid iid = typeof(IPropertyStore).GUID;

                    var hr = SHGetPropertyStoreFromParsingName(
                        path,
                        IntPtr.Zero,
                        GETPROPERTYSTOREFLAGS.GPS_BESTEFFORT,
                        ref iid,
                        out store);

                    if (hr != HRESULT.S_OK || store == null)
                        return null;

                    var key = PKEY_Media_Duration;
                    hr = store.GetValue(ref key, out pv);
                    if (hr != HRESULT.S_OK)
                        return null;

                    if (pv.varType != (ushort)VARENUM.VT_UI8)
                        return null;

                    ulong value = pv.ulVal; // 100ns unità
                    if (value == 0)
                        return null;

                    double seconds = value / 10000000.0;
                    return seconds / 60.0;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    try { PropVariantClear(ref pv); } catch { }

                    if (store != null)
                    {
                        try { Marshal.ReleaseComObject(store); } catch { }
                    }
                }
            }
        }

        // per sganciare gli handler statici quando la pagina viene distrutta
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    MovieMetadataService.PostersChanged -= OnPostersChanged;
                }
                catch
                {
                    // best effort
                }
            }

            base.Dispose(disposing);
        }


        // ------------ helper GDI comuni ------------
        private static class GraphicsUtil
        {
            public static GraphicsPath RoundRect(Rectangle r, int rad)
            {
                var gp = new GraphicsPath();
                gp.AddArc(r.Left, r.Top, rad, rad, 180, 90);
                gp.AddArc(r.Right - rad, r.Top, rad, rad, 270, 90);
                gp.AddArc(r.Right - rad, r.Bottom - rad, rad, rad, 0, 90);
                gp.AddArc(r.Left, r.Bottom - rad, rad, rad, 90, 90);
                gp.CloseFigure();
                return gp;
            }

            public static void DrawStar(Graphics g, RectangleF r, Color color, bool fill)
            {
                var pts = new List<PointF>();
                var cx = r.Left + r.Width / 2f;
                var cy = r.Top + r.Height / 2f;

                for (int i = 0; i < 10; i++)
                {
                    var ang = -Math.PI / 2 + i * Math.PI / 5;
                    var rad = (i % 2 == 0) ? r.Width / 2f : r.Width / 4.2f;
                    pts.Add(new PointF(
                        cx + (float)(rad * Math.Cos(ang)),
                        cy + (float)(rad * Math.Sin(ang))));
                }

                using var path = new GraphicsPath();
                path.AddPolygon(pts.ToArray());
                if (fill)
                {
                    using var br = new SolidBrush(color);
                    g.FillPath(br, path);
                }
                using var pen = new Pen(color, 1.8f);
                g.DrawPath(pen, path);
            }
        }


        // ------------ Win32 scrollbar hiding ------------
        private static class Win32
        {
            public const int SB_HORZ = 0;
            public const int SB_VERT = 1;
            [DllImport("user32.dll")]
            public static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        }

    }
}
