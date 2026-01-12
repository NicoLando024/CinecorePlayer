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

    // ======= Info overlay =======
    internal sealed class InfoOverlay : Control
    {
        public struct Stats
        {
            public string Title;
            public string VideoIn, VideoOut, VideoCodec, VideoPrimaries, VideoTransfer, VideoBitrateNow, VideoBitrateAvg;
            public string AudioIn, AudioOut, AudioBitrateNow, AudioBitrateAvg;
            public string Renderer, HdrMode;
            public bool Upscaling, Bitstream, RtxHdr;
        }

        private Stats _s;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool AutoHeight { get; set; } = true;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int MinCardHeight { get; set; } = 140;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int MaxCardHeight { get; set; } = 460;

        private readonly Color _cardBg = Color.FromArgb(46, 18, 18, 20);
        private readonly Color _cardBrd = Color.FromArgb(70, 255, 255, 255);
        private readonly Color _txt = Color.FromArgb(234, 234, 234);
        private readonly Color _txtDim = Color.FromArgb(170, 170, 170);

        const int PAD = 12;
        const int BAR_W = 6;
        const int ROW_H = 24;

        public InfoOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = MinCardHeight;
        }

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var key = this.FindForm()?.TransparencyKey ?? Color.Black;
            e.Graphics.Clear(key);
        }

        public void SetStats(Stats s)
        {
            _s = s;
            if (AutoHeight) AdjustHeightToContent(Width);
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (AutoHeight) AdjustHeightToContent(Width);
        }

        public void AdjustHeightToContent(int availableWidth)
        {
            if (!AutoHeight || availableWidth <= 0) return;
            using var g = CreateGraphics();
            Height = Math.Max(MinCardHeight, Math.Min(MaxCardHeight, CalcPreferredHeight(g, availableWidth)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var card = new Rectangle(8, 4, Width - 16, Height - 8);
            using (var bg = new SolidBrush(_cardBg)) g.FillRoundedRectangle(bg, card, new Size(10, 10));
            using (var pen = new Pen(_cardBrd, 1)) g.DrawRoundedRectangle(pen, card, new Size(10, 10));

            var bar = new Rectangle(card.X + PAD - 2, card.Y + PAD, BAR_W, Math.Max(1, card.Height - PAD * 2));
            using (var lg = new LinearGradientBrush(bar, Color.Cyan, Color.Magenta, 90f))
            {
                var cb = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(255, 32, 216, 255), Color.FromArgb(255, 64, 160, 255), Color.FromArgb(255, 255, 60, 168) },
                    Positions = new[] { 0f, 0.5f, 1f }
                };
                lg.InterpolationColors = cb;
                g.FillRectangle(lg, bar);
            }

            int x = card.X + PAD + BAR_W + 10;
            int y = card.Y + PAD;
            int w = card.Right - PAD - x;

            using var fTitle = new Font("Segoe UI Semibold", 12.25f);
            using var fHdr = new Font("Segoe UI", 9.25f, FontStyle.Bold);
            using var fKey = new Font("Segoe UI", 9.2f, FontStyle.Bold);
            using var fVal = new Font("Segoe UI", 9.2f, FontStyle.Regular);

            var rcTitle = new Rectangle(x, y, w, fTitle.Height + 2);
            TextRenderer.DrawText(g, string.IsNullOrWhiteSpace(_s.Title) ? "—" : _s.Title,
                fTitle, rcTitle, _txt, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            y = rcTitle.Bottom + 6;

            // RIGA 1: IN/OUT Video (sinistra) + IN/OUT Audio (destra)
            int gap = 28;
            int colW = (w - gap) / 2;
            int col1X = x;
            int col2X = x + colW + gap;

            DrawInOut(g, fKey, fVal, "IN", _s.VideoIn, "OUT", _s.VideoOut, col1X, y, colW);
            DrawInOut(g, fKey, fVal, "IN", _s.AudioIn, "OUT", _s.AudioOut, col2X, y, colW);

            y += ROW_H + 8;

            // RIGA 2: Bitrate (sinistra Video, destra Audio) – "ora" e "medio" in colonnine
            DrawBitrateMini(g, fKey, fVal, "VIDEO", _s.VideoBitrateNow, _s.VideoBitrateAvg, col1X, y, colW);
            DrawBitrateMini(g, fKey, fVal, "AUDIO", _s.AudioBitrateNow, _s.AudioBitrateAvg, col2X, y, colW);

            y += ROW_H + 8;

            using (var p2 = new Pen(Color.FromArgb(38, 255, 255, 255), 1))
                g.DrawLine(p2, x, y, x + w, y);
            y += 8;

            // SISTEMA (tag)
            TextRenderer.DrawText(g, "SISTEMA", fHdr, new Rectangle(x, y, w, fHdr.Height + 2), _txt, TextFormatFlags.NoPadding);
            y += fHdr.Height + 6;

            int left = x;
            int right = x + w;
            int xx = x;
            xx = DrawTag(g, $"Renderer: {_s.Renderer}", xx, ref y, left, right);
            xx = DrawTag(g, $"HDR: {_s.HdrMode}", xx, ref y, left, right);
            xx = DrawTag(g, $"Upscaling: {(_s.Upscaling ? "ON" : "OFF")}", xx, ref y, left, right);
            xx = DrawTag(g, $"Bitstream: {(_s.Bitstream ? "ON" : "OFF")}", xx, ref y, left, right);
            xx = DrawTag(g, $"RTX HDR: {(_s.RtxHdr ? "ON" : "OFF")}", xx, ref y, left, right);
        }

        private void DrawInOut(Graphics g, Font fKey, Font fVal,
                               string k1, string v1, string k2, string v2,
                               int x, int y, int width)
        {
            int half = (width - 12) / 2;

            int chipW1 = DrawKeyChip(g, fKey, k1, x, y);
            var v1rc = new Rectangle(x + chipW1 + 6, y, half - chipW1 - 6, ROW_H);
            TextRenderer.DrawText(g, string.IsNullOrWhiteSpace(v1) ? "n/d" : v1, fVal, v1rc, _txt,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            int x2 = x + half + 12;
            int chipW2 = DrawKeyChip(g, fKey, k2, x2, y);
            var v2rc = new Rectangle(x2 + chipW2 + 6, y, half - chipW2 - 6, ROW_H);
            TextRenderer.DrawText(g, string.IsNullOrWhiteSpace(v2) ? "n/d" : v2, fVal, v2rc, _txt,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private void DrawBitrateMini(Graphics g, Font fKey, Font fVal, string label, string now, string avg, int x, int y, int width)
        {
            int half = (width - 12) / 2;

            int chipW = DrawKeyChip(g, fKey, label, x, y);
            var rcNow = new Rectangle(x + chipW + 6, y, half - chipW - 6, ROW_H);
            var rcAvg = new Rectangle(x + half + 12, y, half - 6, ROW_H);

            TextRenderer.DrawText(g, $"ora: {(string.IsNullOrWhiteSpace(now) ? "n/d" : now)}", fVal, rcNow, _txt,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, $"medio: {(string.IsNullOrWhiteSpace(avg) ? "n/d" : avg)}", fVal, rcAvg, _txt,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        private int DrawKeyChip(Graphics g, Font fKey, string key, int x, int y)
        {
            string t = key.ToUpperInvariant();
            int padX = 8;
            int h = ROW_H;
            var sz = TextRenderer.MeasureText(g, t, fKey, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            int w = sz.Width + padX * 2;

            var rc = new Rectangle(x, y, w, h);
            using (var b = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
                g.FillRoundedRectangle(b, rc, new Size(h / 2, h / 2));
            using (var p = new Pen(Color.FromArgb(80, 255, 255, 255)))
                g.DrawRoundedRectangle(p, rc, new Size(h / 2, h / 2));

            var txRc = new Rectangle(rc.X + padX, rc.Y - 1 + (h - sz.Height) / 2, rc.Width - padX * 2, sz.Height);
            TextRenderer.DrawText(g, t, fKey, txRc, _txtDim, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            return w;
        }

        private int DrawTag(Graphics g, string text, int x, ref int y, int left, int right)
        {
            using var f = new Font("Segoe UI", 9f);
            int padX = 10, padY = 4;

            var sz = TextRenderer.MeasureText(g, text, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            int w = sz.Width + padX * 2;
            int h = sz.Height + padY;

            if (x + w > right)
            {
                x = left;
                y += h + 6;
            }

            var rc = new Rectangle(x, y, w, h);
            using (var b = new SolidBrush(Color.FromArgb(28, 255, 255, 255)))
                g.FillRoundedRectangle(b, rc, new Size(h / 2, h / 2));
            using (var p = new Pen(Color.FromArgb(60, 255, 255, 255)))
                g.DrawRoundedRectangle(p, rc, new Size(h / 2, h / 2));

            TextRenderer.DrawText(g, text, f, new Rectangle(rc.X + padX, rc.Y + padY / 2, rc.Width - padX * 2, rc.Height), _txt,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            return rc.Right + 8;
        }

        private int CalcPreferredHeight(Graphics g, int availableWidth)
        {
            int x = 8 + PAD + BAR_W + 10;
            int w = availableWidth - (x + PAD + 8);

            using var fTitle = new Font("Segoe UI Semibold", 12.25f);
            using var fHdr = new Font("Segoe UI", 9.25f, FontStyle.Bold);
            using var fTag = new Font("Segoe UI", 9f);

            int y = PAD;
            y += fTitle.Height + 6;        // Title
            y += ROW_H + 8;                // IN/OUT Video+Audio
            y += ROW_H + 8;                // Bitrate mini (Video/AUDIO)
            y += 8 + fHdr.Height + 6;      // divider + header SISTEMA
            y += fTag.Height + 10;         // tag (prime righe)
            y += PAD;
            return y + 4;
        }
    }
}
