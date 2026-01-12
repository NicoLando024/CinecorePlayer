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

    // ======= Splash overlay (home) =======
    internal sealed class SplashOverlay : Control
    {
        public event Action? OpenRequested;
        public event Action? SettingsRequested;
        public event Action? CreditsRequested;

        private Image? _img;
        private Image? _icoOpen, _icoSettings, _icoCredits;
        private Rectangle _lastRcOpen, _lastRcSettings, _lastRcCredits;

        public SplashOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);

            Dock = DockStyle.Fill;
            BackColor = Color.Black;

            var p = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (File.Exists(p))
            {
                using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var tmp = Image.FromStream(fs);
                _img = new Bitmap(tmp);
            }
        }

        protected override void OnCreateControl()
        {
            base.OnCreateControl();
            _icoOpen = TryLoadPng("icon-open-files-64.png");
            _icoSettings = TryLoadPng("icon-settings-64.png");
            _icoCredits = TryLoadPng("icon-info-64.png");
        }

        private void RecomputeButtonHitboxes()
        {
            if (_img == null) { _lastRcOpen = _lastRcSettings = _lastRcCredits = Rectangle.Empty; return; }

            int maxW = (int)(Width * 0.60);
            int maxH = (int)(Height * 0.60);
            double s = Math.Min(maxW / (double)_img.Width, maxH / (double)_img.Height);
            int w = Math.Max(1, (int)Math.Round(_img.Width * s));
            int h = Math.Max(1, (int)Math.Round(_img.Height * s));
            int x = (Width - w) / 2;
            int y = (Height - h) / 2;

            int size = Math.Max(44, Math.Min(64, (int)Math.Round(Height * 0.058)));
            int gap = Math.Max(14, Math.Min(28, (int)Math.Round(size * 0.35)));
            double t = Math.Clamp((Height - 800) / 600.0, 0, 1);
            int gapBelowLogo = (int)Math.Round(-40 + (-150 - (-40)) * t);
            int cy = y + h + gapBelowLogo;

            int bottomMargin = Math.Max(16, size / 2);
            cy = Math.Min(cy, Height - bottomMargin - size);

            _lastRcOpen = new Rectangle(Width / 2 - size / 2, cy, size, size);
            _lastRcSettings = new Rectangle(_lastRcOpen.X - size - gap, cy, size, size);
            _lastRcCredits = new Rectangle(_lastRcOpen.Right + gap, cy, size, size);
        }

        private Image? TryLoadPng(string name)
        {
            try
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Assets", name);
                if (File.Exists(p))
                {
                    using var fs = File.OpenRead(p);
                    using var tmp = Image.FromStream(fs);
                    return new Bitmap(tmp);
                }
            }
            catch { }
            return null;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            g.Clear(Color.Black);
            if (_img == null) return;

            int maxW = (int)(Width * 0.60);
            int maxH = (int)(Height * 0.60);
            double s = Math.Min(maxW / (double)_img.Width, maxH / (double)_img.Height);
            int w = Math.Max(1, (int)Math.Round(_img.Width * s));
            int h = Math.Max(1, (int)Math.Round(_img.Height * s));
            int x = (Width - w) / 2;
            int y = (Height - h) / 2;

            g.DrawImage(_img, x, y, w, h);

            int size = Math.Max(44, Math.Min(64, (int)Math.Round(Height * 0.058)));
            int gap = Math.Max(14, Math.Min(28, (int)Math.Round(size * 0.35)));
            double t = Math.Clamp((Height - 800) / 600.0, 0, 1);
            int gapBelowLogo = (int)Math.Round(-40 + (-150 - (-40)) * t);
            int cy = y + h + gapBelowLogo;
            int bottomMargin = Math.Max(16, size / 2);
            cy = Math.Min(cy, Height - bottomMargin - size);

            Rectangle rcOpen = new Rectangle(Width / 2 - size / 2, cy, size, size);
            Rectangle rcSettings = new Rectangle(rcOpen.X - size - gap, cy, size, size);
            Rectangle rcCredits = new Rectangle(rcOpen.Right + gap, cy, size, size);

            static void DrawCircleSoft(Graphics gg, Rectangle r)
            {
                using var path = new GraphicsPath();
                path.AddEllipse(r);
                using var fill = new SolidBrush(Color.FromArgb(46, 255, 255, 255));
                gg.FillPath(fill, path);
            }

            DrawCircleSoft(g, rcSettings);
            DrawCircleSoft(g, rcOpen);
            DrawCircleSoft(g, rcCredits);

            void DrawIcon(Graphics gg, Rectangle r, Image? ico)
            {
                if (ico == null) return;
                int pad = Math.Max(10, (int)Math.Round(size * 0.22));
                gg.DrawImage(ico, new Rectangle(r.X + pad, r.Y + pad, r.Width - pad * 2, r.Height - pad * 2));
            }

            DrawIcon(g, rcSettings, _icoSettings);
            DrawIcon(g, rcOpen, _icoOpen);
            DrawIcon(g, rcCredits, _icoCredits);

            RecomputeButtonHitboxes();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            RecomputeButtonHitboxes();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            RecomputeButtonHitboxes();
            if (_lastRcOpen.Contains(e.Location)) { OpenRequested?.Invoke(); return; }
            if (_lastRcSettings.Contains(e.Location)) { SettingsRequested?.Invoke(); return; }
            if (_lastRcCredits.Contains(e.Location)) { CreditsRequested?.Invoke(); return; }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            RecomputeButtonHitboxes();
            bool over = _lastRcOpen.Contains(e.Location) ||
                        _lastRcSettings.Contains(e.Location) ||
                        _lastRcCredits.Contains(e.Location);
            Cursor = over ? Cursors.Hand : Cursors.Default;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _img?.Dispose();
            base.Dispose(disposing);
        }
    }
}
