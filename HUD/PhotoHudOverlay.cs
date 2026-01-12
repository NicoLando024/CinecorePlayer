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

    // ===== HUD FOTO (solo frecce) =====
    internal sealed class PhotoHudOverlay : Control
    {
        public event Action? PrevRequested;
        public event Action? NextRequested;

        private Rectangle _rcPrev, _rcNext;
        private bool _hoverPrev, _hoverNext;

        public PhotoHudOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);

            BackColor = Color.Transparent;
            Cursor = Cursors.Default;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x20; // WS_EX_TRANSPARENT (come gli altri overlay)
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var key = FindForm()?.TransparencyKey ?? Color.Black;
            e.Graphics.Clear(key);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingQuality = CompositingQuality.HighQuality;

            // dimensioni più sobrie
            int size = Math.Max(40, Math.Min(64, Height / 12));
            int margin = Math.Max(32, Width / 30);
            int cy = (Height - size) / 2;

            _rcPrev = new Rectangle(margin, cy, size, size);
            _rcNext = new Rectangle(Width - margin - size, cy, size, size);

            DrawButton(g, _rcPrev, isRight: false, hovered: _hoverPrev);
            DrawButton(g, _rcNext, isRight: true, hovered: _hoverNext);
        }

        private void DrawButton(Graphics g, Rectangle r, bool isRight, bool hovered)
        {
            // shadow morbida
            var shadow = r;
            shadow.Inflate(4, 4);
            using (var sb = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                g.FillEllipse(sb, shadow);

            // cerchio principale
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(r);

                using (var fill = new SolidBrush(Color.FromArgb(hovered ? 140 : 110, 15, 15, 15)))
                    g.FillPath(fill, path);

                using (var pen = new Pen(
                    Color.FromArgb(hovered ? 230 : 190, 255, 255, 255),
                    hovered ? 2.0f : 1.5f))
                {
                    g.DrawPath(pen, path);
                }
            }

            // freccia
            Point c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            int w = (int)(r.Width * 0.28);

            Point p1, p2, p3;
            if (isRight)
            {
                p1 = new Point(c.X - w, c.Y - w);
                p2 = new Point(c.X - w, c.Y + w);
                p3 = new Point(c.X + w, c.Y);
            }
            else
            {
                p1 = new Point(c.X + w, c.Y - w);
                p2 = new Point(c.X + w, c.Y + w);
                p3 = new Point(c.X - w, c.Y);
            }

            using (var gp = new GraphicsPath())
            {
                gp.AddPolygon(new[] { p1, p2, p3 });
                using (var arrow = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
                    g.FillPath(arrow, gp);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            if (_rcPrev.Contains(e.Location))
                PrevRequested?.Invoke();
            else if (_rcNext.Contains(e.Location))
                NextRequested?.Invoke();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            bool hp = _rcPrev.Contains(e.Location);
            bool hn = _rcNext.Contains(e.Location);

            if (hp != _hoverPrev || hn != _hoverNext)
            {
                _hoverPrev = hp;
                _hoverNext = hn;
                Invalidate();
            }

            Cursor = (hp || hn) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverPrev || _hoverNext)
            {
                _hoverPrev = _hoverNext = false;
                Invalidate();
            }
            Cursor = Cursors.Default;
        }
    }
}
