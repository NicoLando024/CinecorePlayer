#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using DirectShowLib;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HDRMode = global::CinecorePlayer2025.Utilities.HdrMode;
using VRChoice = global::CinecorePlayer2025.Utilities.VideoRendererChoice;

namespace CinecorePlayer2025
{
    // ======= Helpers disegno (rounded rectangles) =======
    internal static class GraphicsExtensions
    {
        public static void FillRoundedRectangle(this Graphics g, Brush brush, Rectangle rect, Size radius)
        {
            using var gp = Rounded(rect, radius);
            g.FillPath(brush, gp);
        }
        public static void DrawRoundedRectangle(this Graphics g, Pen pen, Rectangle rect, Size radius)
        {
            using var gp = Rounded(rect, radius);
            g.DrawPath(pen, gp);
        }
        private static GraphicsPath Rounded(Rectangle r, Size rad)
        {
            int rx = Math.Max(0, rad.Width);
            int ry = Math.Max(0, rad.Height);
            var gp = new GraphicsPath();
            if (rx == 0 || ry == 0) { gp.AddRectangle(r); return gp; }
            gp.AddArc(r.X, r.Y, rx, ry, 180, 90);
            gp.AddArc(r.Right - rx, r.Y, rx, ry, 270, 90);
            gp.AddArc(r.Right - rx, r.Bottom - ry, rx, ry, 0, 90);
            gp.AddArc(r.X, r.Bottom - ry, rx, ry, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        // NEW: extension usato dal FocusAdorner
        public static Rectangle InflateBy(this Rectangle r, int v) => Rectangle.Inflate(r, v, v);
    }
}
