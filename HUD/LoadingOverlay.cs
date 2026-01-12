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

    // ======= Loading overlay =======
    internal sealed class LoadingOverlay : Control
    {
        public event Action? Completed;

        private Image? _logo;
        private readonly System.Windows.Forms.Timer _tick;
        private DateTime _start;
        private double _progress01;
        private int _durationMs = 2200;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int DurationMs { get => _durationMs; set => _durationMs = Math.Max(500, value); }

        public LoadingOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);

            BackColor = Color.Transparent;

            var p = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (File.Exists(p)) _logo = Image.FromFile(p);

            _tick = new System.Windows.Forms.Timer { Interval = 16 };
            _tick.Tick += (_, __) =>
            {
                var t = (DateTime.UtcNow - _start).TotalMilliseconds;
                _progress01 = Math.Clamp(EaseOutCubic(t / _durationMs), 0, 1);
                Invalidate();
                if (_progress01 >= 1.0) { _tick.Stop(); Completed?.Invoke(); }
            };
        }

        public void Start()
        {
            _progress01 = 0;
            _start = DateTime.UtcNow;
            _tick.Start();
            Invalidate();
        }

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var key = this.FindForm()?.TransparencyKey ?? Color.Black;
            e.Graphics.Clear(key);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;

            if (_logo != null)
            {
                double maxW = Width * 0.52;
                double maxH = Height * 0.42;
                double scale = Math.Min(maxW / _logo.Width, maxH / _logo.Height);
                int w = Math.Max(1, (int)Math.Round(_logo.Width * scale));
                int h = Math.Max(1, (int)Math.Round(_logo.Height * scale));
                int x = (Width - w) / 2;
                int y = (Height - h) / 2 - 20;

                using (var glow = new SolidBrush(Color.FromArgb(46, 0, 0, 0)))
                    g.FillEllipse(glow, x - w * 0.08f, y - h * 0.08f, w * 1.16f, h * 1.16f);

                g.DrawImage(_logo, new Rectangle(x, y, w, h));
            }

            var barW = (int)Math.Round(Math.Min(Width * 0.84, 760));
            var barH = 14;
            var barX = (Width - barW) / 2;
            var barY = (int)Math.Round(Height * 0.62);
            var barRect = new Rectangle(barX, barY, barW, barH);
            int rr = barH / 2;

            int fillW = (int)Math.Round(barRect.Width * _progress01);
            if (fillW > 0)
            {
                var fillRect = new Rectangle(barRect.X, barRect.Y, Math.Max(1, fillW), barRect.Height);
                using (var glow = new SolidBrush(Color.FromArgb(35, 64, 200, 255)))
                    g.FillRoundedRectangle(glow, new Rectangle(fillRect.X - 2, fillRect.Y - 2, fillRect.Width + 4, fillRect.Height + 4), new Size(rr + 2, rr + 2));

                using (var lg = new LinearGradientBrush(fillRect, Color.Cyan, Color.Magenta, 0f))
                {
                    var cb = new ColorBlend
                    {
                        Colors = new[] { Color.FromArgb(255, 32, 216, 255), Color.FromArgb(255, 64, 160, 255), Color.FromArgb(255, 255, 60, 168) },
                        Positions = new[] { 0f, 0.55f, 1f }
                    };
                    lg.InterpolationColors = cb;
                    g.FillRoundedRectangle(lg, fillRect, new Size(rr, rr));
                }
            }

            var s = "Powered by madVR";
            using var f = new Font("Segoe UI", 9.25f, FontStyle.Bold);
            var sz = g.MeasureString(s, f);
            var px = Width - (int)sz.Width - 16;
            var py = Height - (int)sz.Height - 10;

            using (var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                g.DrawString(s, f, shadow, px + 1, py + 1);
            using (var white = new SolidBrush(Color.FromArgb(230, 255, 255, 255)))
                g.DrawString(s, f, white, px, py);
        }

        private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _logo?.Dispose(); _tick?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
