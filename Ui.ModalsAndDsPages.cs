// FILE: CinecorePlayer2025.UI.cs — FULL PATCHED (layout fix nav, spacing box, 2 colonne General, titolo, bottoni footer)
// Stile = come negli screenshot che mi hai mandato:
//  - overlay scuro + card centrale bordata 1px Theme.Border
//  - header in alto con scritta a destra ("IMPOSTAZIONI", "CREDITI")
//  - colonna sinistra nav con logo + elenco sezioni (Generali, madVR, LAV VIDEO, ...), bottoni rettangolari con bordo 1px
//  - in basso nella nav: CHIUDI / APPLICA con uno spazio fra i due
//  - lato destro: bordo 1px, riga chiara sopra, contenuto scrollabile
//  - pagina GENERALI: DUE COLONNE come nello screenshot: sinistra larga (Algoritmi madVR / HDR / Frequenza monitor), destra stretta (Audio / Video(player))
//      * ogni riquadro è largo tutta la sua colonna e distanziato 12px dagli altri
//  - niente tronchi grafici sul titolo "IMPOSTAZIONI"
//  - la lista nav mostra TUTTE le voci, non solo "Generali"
//  - CreditsModal è più basso ma mantiene stile e bottone CHIUDI con bordo
//
// Metti questo file dentro il progetto (namespace CinecorePlayer2025) e rimuovi le vecchie versioni duplicate.
// Se qualcosa collide con roba tua precedente, tieni QUESTO come sorgente autorevole.
//
// NOTE IMPORTANTI:
// - madVR page si auto-apre (EnsureStarted) quando entri nella voce madVR
// - DsPropPageHost ha lo scroll e padding in alto per non tagliare LAV/madVR/MPC
// - rimane HudOverlay ecc. come già implementato


#nullable enable
using DirectShowLib;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DSFilterCategory = DirectShowLib.FilterCategory;

namespace CinecorePlayer2025
{
    // ===================== THEME =====================
    internal static class Theme
    {
        public static readonly Color BackdropDim = Color.FromArgb(185, 0, 0, 0);     // overlay dietro modali
        public static readonly Color Panel = Color.FromArgb(24, 24, 28);       // sfondo principale / card
        public static readonly Color Card = Color.FromArgb(24, 24, 28);
        public static readonly Color Nav = Color.FromArgb(26, 26, 30);       // colonna sinistra nav
        public static readonly Color PanelAlt = Color.FromArgb(34, 34, 40);       // highlight nav voce selezionata
        public static readonly Color Border = Color.FromArgb(76, 76, 82);       // linee 1px / bordini
        public static readonly Color Text = Color.White;
        public static readonly Color SubtleText = Color.FromArgb(208, 208, 214);
        public static readonly Color Muted = Color.FromArgb(170, 170, 178);
        public static readonly Color Accent = Color.FromArgb(40, 120, 255);
        public static readonly Color AccentSoft = Color.FromArgb(26, 90, 210);
        public static readonly Color Danger = Color.FromArgb(230, 80, 80);
    }

    // ===================== GRAFICA BASE =====================
    internal static class DrawHelpers
    {
        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var gp = new GraphicsPath();
            gp.AddArc(r.Left, r.Top, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }
    }

    // pannello con bordo 1px Theme.Border
    internal sealed class OutlinePanel : Panel
    {
        public OutlinePanel()
        {
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var p = new Pen(Theme.Border, 1f);
            e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    // pannello "card" centrale della modale, con bordo 1px Theme.Border
    internal sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            DoubleBuffered = true;
            BackColor = Theme.Panel;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var p = new Pen(Theme.Border, 1f);
            e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    // ===================== HUD OVERLAY (player OSD) =====================
    internal sealed class HudOverlay : Control
    {
        private float _externalVolume = 1f;
        public event Action? OpenClicked;
        public event Action? PlayPauseClicked;
        public event Action? StopClicked;
        public event Action? FullscreenClicked;
        public event Action? SkipBack10Clicked;
        public event Action? SkipForward10Clicked;
        public event Action? PrevChapterClicked;
        public event Action? NextChapterClicked;

        public event Action? TopSettingsClicked;
        public event Action? TopInfoClicked;

        public event Action<float>? VolumeChanged;
        public event Action<double>? SeekRequested;
        public event Action<double, Point>? PreviewRequested;

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<string>? GetInfoLine { get; set; }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<(double pos, double dur)>? GetTime { get; set; }

        [DefaultValue("")] public string NowPlayingTitle { get; private set; } = string.Empty;
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<string>? GetTitle { get; set; }

        public void UpdateTitle(string? t)
        {
            NowPlayingTitle = t ?? string.Empty;
            ShowOnce(1500);
            Invalidate();
        }
        public void UpdateTitleFromPath(string filePath, string? preferredTitle = null)
        {
            NowPlayingTitle = string.IsNullOrWhiteSpace(preferredTitle)
                ? Path.GetFileNameWithoutExtension(filePath) ?? string.Empty
                : preferredTitle!;
            ShowOnce(1500);
            Invalidate();
        }

        [DefaultValue(false)] public bool AutoHide { get; set; }
        [DefaultValue(2000)] public int IdleHideDelayMs { get; set; } = 2000;
        [DefaultValue(900)] public int HideGraceMs { get; set; } = 900;
        [DefaultValue(150)] public int FadeOutMs { get; set; } = 150;
        [DefaultValue(false)] public bool TimelineVisible { get; set; } = false;

        public Image? IconInfo { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Image? IconSettings { get; set; }

        private const int TopBarHeight = 60;
        private const int BottomBackdropHeight = 100;
        private const int BtnSize = 28;
        private const int GapDesired = 36;
        private const int ExtraBtnVsVolPad = 2;
        private const int TimelineHeight = 6;
        private const int TimelineYFromBottom = 56;
        private const int InfoYFromBottom = 88;
        private const int ControlYFromBottom = 44;
        private const int VolYFromBottom = 30;
        private const int VolKnobRadius = 6;

        private readonly Font _fInfo = new("Segoe UI", 9f);
        private readonly Font _fTime = new("Segoe UI", 9f, FontStyle.Bold);
        private readonly Font _fTopTitle = new("Segoe UI Semibold", 12.5f);
        private readonly Font _fSymbol = new("Segoe UI", 11f, FontStyle.Bold);

        private readonly System.Windows.Forms.Timer _fade;
        private float _opacity = 1f;
        private DateTime _fadeStartAt = DateTime.MinValue;
        private DateTime _lastMove = DateTime.UtcNow;
        private DateTime _forceShowUntil = DateTime.MinValue;

        private float _vol = 1.0f;
        private bool _drag, _dragVol;
        private double _dragPosSec;
        private DateTime _lastPreviewAt = DateTime.MinValue;
        private Bitmap? _preview; private double _previewSec;
        private int _lastMouseX;

        private Rectangle _rcTopBar, _rcBottomBar;
        private Rectangle _rcTimeline, _rcTimelineHit;
        private Rectangle _rcBtnRemove, _rcBtnOpen, _rcBtnPlay, _rcBtnBack, _rcBtnFwd, _rcBtnPrev, _rcBtnNext, _rcBtnFull;
        private Rectangle _rcVolTrack, _rcVolHit;
        private Rectangle _rcTopInfo, _rcTopSettings;
        private int _volCenterY;
        private bool _showPrevNext = true, _showBackFwd = true;

        public enum ButtonId
        {
            None,
            Remove,
            Open,
            PlayPause,
            Back10,
            Fwd10,
            PrevChapter,
            NextChapter,
            Fullscreen,
            TopSettings,
            TopInfo
        }
        private ButtonId _pulseBtn = ButtonId.None;
        private DateTime _pulseUntil = DateTime.MinValue;
        public void Pulse(ButtonId btn, int ms = 180)
        {
            _pulseBtn = btn;
            _pulseUntil = DateTime.UtcNow.AddMilliseconds(Math.Max(60, ms));
            Invalidate();
        }
        private bool IsPulsing(ButtonId btn) => _pulseBtn == btn && DateTime.UtcNow < _pulseUntil;

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var host = FindForm();
            if (host != null && host.TransparencyKey != Color.Empty)
            {
                e.Graphics.Clear(host.TransparencyKey);
                return;
            }
            base.OnPaintBackground(e);
        }

        public HudOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.UserPaint
                   | ControlStyles.ResizeRedraw
                   | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;

            _fade = new System.Windows.Forms.Timer { Interval = 30 };
            _fade.Tick += (_, __) =>
            {
                var now = DateTime.UtcNow;
                if (now < _forceShowUntil || !AutoHide || _drag || _dragVol)
                {
                    _fadeStartAt = DateTime.MinValue;
                    if (_opacity != 1f) { _opacity = 1f; Invalidate(); }
                    return;
                }

                var idleMs = (now - _lastMove).TotalMilliseconds;
                if (idleMs < HideGraceMs)
                {
                    _fadeStartAt = DateTime.MinValue;
                    if (_opacity != 1f) { _opacity = 1f; Invalidate(); }
                    return;
                }

                if (_fadeStartAt == DateTime.MinValue) _fadeStartAt = now;
                double t = (now - _fadeStartAt).TotalMilliseconds / Math.Max(1, FadeOutMs);
                float target = (float)(1.0 - Math.Clamp(t, 0, 1));
                if (Math.Abs(_opacity - target) > 0.01f) { _opacity = target; Invalidate(); }
                else if (t >= 1.0 && _opacity != 0f) { _opacity = 0f; Invalidate(); }
            };
            _fade.Start();

            MouseMove += (_, e) =>
            {
                RecalcLayout();
                var now = DateTime.UtcNow;
                _lastMouseX = e.X;

                if ((_drag || _dragVol) && Control.MouseButtons == MouseButtons.None) { StopDragging(); return; }

                if (IsHudInteractive(e.Location) || _drag || _dragVol)
                {
                    _lastMove = now;
                    if (_opacity != 1f) { _opacity = 1f; Invalidate(); }
                }

                if (_dragVol)
                {
                    float v = (e.X - _rcVolTrack.X) / (float)_rcVolTrack.Width;
                    v = Math.Clamp(v, 0f, 1f);
                    _vol = v;
                    VolumeChanged?.Invoke(v);
                    Invalidate();
                    return;
                }

                if (_drag && TimelineVisible && GetTime != null)
                {
                    var (_, dur) = GetTime();
                    if (dur > 0)
                    {
                        double ratio = (e.X - _rcTimeline.X) / (double)_rcTimeline.Width;
                        ratio = Math.Clamp(ratio, 0, 1);
                        _dragPosSec = ratio * dur;
                        Invalidate();

                        if ((now - _lastPreviewAt).TotalMilliseconds >= 90)
                        {
                            _lastPreviewAt = now;
                            PreviewRequested?.Invoke(_dragPosSec,
                                PointToScreen(new Point(e.X, _rcTimeline.Y)));
                        }
                    }
                }
                else
                {
                    if (_preview != null) { SetPreview(null, _previewSec); }
                }
            };

            VisibleChanged += (_, __) =>
            {
                if (Visible)
                {
                    ShowOnce(1800);
                    _opacity = 1f;
                    Invalidate();
                }
            };
        }

        public void ShowOnce(int ms = 2000)
        {
            _forceShowUntil = DateTime.UtcNow.AddMilliseconds(Math.Max(250, ms));
            _opacity = 1f;
            Invalidate();
        }

        public void SetPreview(Bitmap? bmp, double seconds)
        {
            _preview?.Dispose();
            _preview = bmp;
            _previewSec = seconds;
            Invalidate();
        }

        public void SetExternalVolume(float v)
        {
            var clamped = Math.Clamp(v, 0f, 1f);
            _vol = clamped;
            _externalVolume = clamped;
            Invalidate();
        }

        public float GetExternalVolume()
        {
            return _externalVolume;
        }

        public void PerformVolumeDelta(float delta, Action<float> apply)
        {
            _vol = Math.Clamp(_vol + delta, 0f, 1f);
            apply(_vol);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!Capture) StopDragging();
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) StopDragging();
        }

        private void StopDragging()
        {
            if (_drag || _dragVol)
            {
                _drag = false;
                _dragVol = false;
                Capture = false;
                Invalidate();
            }

            if (_preview != null)
            {
                SetPreview(null, _dragPosSec);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            RecalcLayout();

            if (!IsHudInteractive(e.Location))
            {
                ForwardMouseToUnderlying(WinMsg.WM_LBUTTONDOWN, e.Location, IntPtr.Zero);
                return;
            }

            if (_rcTopSettings.Contains(e.Location)) { TopSettingsClicked?.Invoke(); Pulse(ButtonId.TopSettings); return; }
            if (_rcTopInfo.Contains(e.Location)) { TopInfoClicked?.Invoke(); Pulse(ButtonId.TopInfo); return; }

            if (_rcBtnRemove.Contains(e.Location)) { StopClicked?.Invoke(); Pulse(ButtonId.Remove); return; }
            if (_rcBtnOpen.Contains(e.Location)) { OpenClicked?.Invoke(); Pulse(ButtonId.Open); return; }
            if (_rcBtnPlay.Contains(e.Location)) { PlayPauseClicked?.Invoke(); Pulse(ButtonId.PlayPause); return; }
            if (_showBackFwd && _rcBtnBack.Contains(e.Location)) { SkipBack10Clicked?.Invoke(); Pulse(ButtonId.Back10); return; }
            if (_showBackFwd && _rcBtnFwd.Contains(e.Location)) { SkipForward10Clicked?.Invoke(); Pulse(ButtonId.Fwd10); return; }
            if (_showPrevNext && _rcBtnPrev.Contains(e.Location)) { PrevChapterClicked?.Invoke(); Pulse(ButtonId.PrevChapter); return; }
            if (_showPrevNext && _rcBtnNext.Contains(e.Location)) { NextChapterClicked?.Invoke(); Pulse(ButtonId.NextChapter); return; }
            if (_rcBtnFull.Contains(e.Location)) { FullscreenClicked?.Invoke(); Pulse(ButtonId.Fullscreen); return; }

            if (_rcVolHit.Contains(e.Location))
            {
                _dragVol = true;
                Capture = true;
                float v = (e.X - _rcVolTrack.X) / (float)_rcVolTrack.Width;
                v = Math.Clamp(v, 0f, 1f);
                _vol = v;
                VolumeChanged?.Invoke(v);
                Invalidate();
                return;
            }

            if (TimelineVisible && _rcTimelineHit.Contains(e.Location) && GetTime != null)
            {
                _drag = true;
                Capture = true;
                var (_, dur) = GetTime();
                double r = (e.X - _rcTimeline.X) / (double)_rcTimeline.Width;
                r = Math.Clamp(r, 0, 1);
                _dragPosSec = r * Math.Max(0, dur);
                PreviewRequested?.Invoke(_dragPosSec, PointToScreen(new Point(e.X, _rcTimeline.Y)));
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                RecalcLayout();

                if (_drag && TimelineVisible && GetTime != null)
                {
                    var (_, dur) = GetTime();
                    double r = (e.X - _rcTimeline.X) / (double)_rcTimeline.Width;
                    r = Math.Clamp(r, 0, 1);
                    _dragPosSec = r * Math.Max(0, dur);
                    SeekRequested?.Invoke(_dragPosSec);
                }

                if (!IsHudInteractive(e.Location))
                    ForwardMouseToUnderlying(WinMsg.WM_LBUTTONUP, e.Location, IntPtr.Zero);

                StopDragging();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (!IsHudInteractive(e.Location))
                ForwardMouseToUnderlying(WinMsg.WM_LBUTTONDBLCLK, e.Location, IntPtr.Zero);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            RecalcLayout();

            if (!IsHudInteractive(e.Location))
            {
                int wParam = (short)e.Delta << 16;
                ForwardMouseToUnderlying(WinMsg.WM_MOUSEWHEEL, e.Location, (IntPtr)wParam);
                return;
            }

            float step = e.Delta > 0 ? 0.05f : -0.05f;
            _vol = Math.Clamp(_vol + step, 0f, 1f);
            VolumeChanged?.Invoke(_vol);
            Invalidate();
            ShowOnce(800);
        }

        private Rectangle ActiveZoneTop => new Rectangle(0, 0, Width, TopBarHeight + 10);
        private Rectangle ActiveZoneBottom => new Rectangle(0, Height - 120, Width, 120);

        private bool IsHudInteractive(Point p)
        {
            if (_opacity <= 0.05f) return false;
            if (ActiveZoneTop.Contains(p)) return true;
            if (ActiveZoneBottom.Contains(p)) return true;
            if (TimelineVisible && (_rcTimelineHit.Contains(p) || _rcTimeline.Contains(p))) return true;
            if (_rcVolHit.Contains(p)) return true;
            if (_rcTopSettings.Contains(p) || _rcTopInfo.Contains(p)) return true;
            return false;
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            if (m.Msg == WM_NCHITTEST)
            {
                if (_opacity <= 0.05f)
                {
                    m.Result = (IntPtr)(-1);
                    return;
                }

                int x = (short)((uint)m.LParam & 0xFFFF);
                int y = (short)(((uint)m.LParam >> 16) & 0xFFFF);
                Point client = PointToClient(new Point(x, y));

                if (!IsHudInteractive(client))
                {
                    m.Result = (IntPtr)(-1);
                    return;
                }
            }
            base.WndProc(ref m);
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
        private enum WinMsg : uint { WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_MOUSEWHEEL = 0x020A }
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point p);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private static IntPtr MakeLParam(short low, short high) => (IntPtr)((high << 16) | (low & 0xFFFF));
        private void ForwardMouseToUnderlying(WinMsg msg, Point clientPt, IntPtr wParam)
        {
            Point screen = PointToScreen(clientPt);
            IntPtr hTarget = WindowFromPoint(screen);
            if (hTarget == IntPtr.Zero || hTarget == Handle) return;
            var pt = new POINT { X = screen.X, Y = screen.Y };
            if (!ScreenToClient(hTarget, ref pt)) return;
            IntPtr lParam = MakeLParam((short)pt.X, (short)pt.Y);
            SendMessage(hTarget, (uint)msg, wParam, lParam);
        }

        private void RecalcLayout()
        {
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;

            _rcTopBar = new Rectangle(0, 0, w, TopBarHeight);
            _rcBottomBar = new Rectangle(0, Math.Max(0, h - BottomBackdropHeight), w, BottomBackdropHeight);

            int timelineY = h - TimelineYFromBottom;
            _rcTimeline = new Rectangle(16, timelineY, Math.Max(40, w - 32), TimelineHeight);
            _rcTimelineHit = Rectangle.Inflate(_rcTimeline, 0, 12);

            int btnTop = h - ControlYFromBottom;
            _rcBtnFull = new Rectangle(w - 16 - BtnSize, btnTop, BtnSize, BtnSize);

            int dynVolW = Math.Clamp(w / 5, 120, 220);
            _volCenterY = h - VolYFromBottom;
            _rcVolTrack = new Rectangle(_rcBtnFull.X - 16 - dynVolW, _volCenterY - 1, dynVolW, 2);
            _rcVolHit = Rectangle.Inflate(new Rectangle(_rcVolTrack.X, _volCenterY - 6, _rcVolTrack.Width, 12), 0, 8);

            _rcBtnRemove = new Rectangle(16, btnTop, BtnSize, BtnSize);
            _rcBtnOpen = new Rectangle(_rcBtnRemove.Right + 8, btnTop, BtnSize, BtnSize);

            int leftBound = _rcBtnOpen.Right + 24;
            int rightBound = _rcVolTrack.X - 16 - ExtraBtnVsVolPad;
            int usable = Math.Max(0, rightBound - leftBound);
            int gap = Math.Clamp(GapDesired, 22, Math.Max(22, usable / 10));

            int playX = leftBound + (usable - BtnSize) / 2;
            playX = Math.Max(leftBound, Math.Min(playX, rightBound - BtnSize));
            _rcBtnPlay = new Rectangle(playX, btnTop, BtnSize, BtnSize);
            _rcBtnBack = new Rectangle(_rcBtnPlay.X - gap, btnTop, BtnSize, BtnSize);
            _rcBtnFwd = new Rectangle(_rcBtnPlay.Right + gap - BtnSize, btnTop, BtnSize, BtnSize);
            _rcBtnPrev = new Rectangle(_rcBtnBack.X - gap, btnTop, BtnSize, BtnSize);
            _rcBtnNext = new Rectangle(_rcBtnFwd.Right + (gap - BtnSize), btnTop, BtnSize, BtnSize);

            _showBackFwd = _rcBtnBack.X >= leftBound && _rcBtnFwd.Right <= rightBound;
            _showPrevNext = _rcBtnPrev.X >= leftBound && _rcBtnNext.Right <= rightBound;

            int topBtnTop = (_rcTopBar.Height - BtnSize) / 2;
            _rcTopSettings = new Rectangle(Math.Max(16, w - 16 - BtnSize), topBtnTop, BtnSize, BtnSize);
            _rcTopInfo = new Rectangle(Math.Max(16, _rcTopSettings.X - 16 - BtnSize), topBtnTop, BtnSize, BtnSize);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_opacity <= 0.01f) return;

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RecalcLayout();

            using (var bbTop = new SolidBrush(Color.FromArgb((int)(110 * _opacity), 0, 0, 0)))
                g.FillRectangle(bbTop, _rcTopBar);
            using (var bbBottom = new SolidBrush(Color.FromArgb((int)(110 * _opacity), 0, 0, 0)))
                g.FillRectangle(bbBottom, _rcBottomBar);

            DrawTopBar(g);

            string info = GetInfoLine?.Invoke() ?? "";
            if (!string.IsNullOrEmpty(info))
            {
                using var brInfo = new SolidBrush(Color.FromArgb((int)(220 * _opacity), 230, 230, 230));
                g.DrawString(info, _fInfo, brInfo, 16, Height - InfoYFromBottom);
            }

            if (TimelineVisible && GetTime != null)
            {
                var (pos, dur) = GetTime();

                using var tlBg = new SolidBrush(Color.FromArgb((int)(120 * _opacity), 200, 200, 200));
                using var tlFg = new SolidBrush(Color.FromArgb((int)(255 * _opacity), 255, 255, 255));
                g.FillRectangle(tlBg, _rcTimeline);

                if (dur > 0)
                {
                    int wProg = (int)(_rcTimeline.Width * (pos / Math.Max(0.0001, dur)));
                    if (wProg > 0)
                        g.FillRectangle(tlFg, new Rectangle(_rcTimeline.X, _rcTimeline.Y,
                            Math.Min(wProg, _rcTimeline.Width),
                            _rcTimeline.Height));
                }

                if (_drag && dur > 0)
                {
                    double clamped = Math.Clamp(_dragPosSec, 0, dur);
                    int ghostW = (int)(_rcTimeline.Width * (clamped / dur));
                    using var ghost = new SolidBrush(Color.FromArgb((int)(255 * _opacity), 255, 255, 255));
                    g.FillRectangle(ghost, new Rectangle(_rcTimeline.X, _rcTimeline.Y,
                        Math.Min(ghostW, _rcTimeline.Width),
                        _rcTimeline.Height));
                }

                {
                    double knobSec = _drag ? _dragPosSec : pos;
                    knobSec = dur > 0 ? Math.Clamp(knobSec, 0, dur) : 0;
                    int knobX = _rcTimeline.X + (dur > 0 ? (int)(_rcTimeline.Width * (knobSec / dur)) : 0);
                    int d = _drag ? 14 : 12;
                    using var kn = new SolidBrush(Color.FromArgb((int)(255 * _opacity), 255, 255, 255));
                    g.FillEllipse(kn,
                        knobX - d / 2,
                        _rcTimeline.Y + _rcTimeline.Height / 2 - d / 2,
                        d, d);

                    if (_drag && _preview != null)
                    {
                        int pw = _preview.Width, ph = _preview.Height;
                        int px = Math.Clamp(knobX - pw / 2, _rcTimeline.Left, _rcTimeline.Right - pw);
                        int py = _rcTimeline.Y - ph - 18;
                        var dest = new Rectangle(px, py, pw, ph);
                        if (_opacity < 1f)
                        {
                            var cm = new ColorMatrix { Matrix33 = Math.Clamp(_opacity, 0f, 1f) };
                            using var ia = new ImageAttributes();
                            ia.SetColorMatrix(cm);
                            g.DrawImage(_preview, dest, 0, 0, _preview.Width, _preview.Height,
                                GraphicsUnit.Pixel, ia);
                        }
                        else g.DrawImage(_preview, dest);

                        string pt = Fmt(_dragPosSec);
                        var ptsz = g.MeasureString(pt, _fInfo);
                        using var bb2 = new SolidBrush(Color.FromArgb((int)(220 * _opacity), 0, 0, 0));
                        using var wb = new SolidBrush(Color.FromArgb((int)(255 * _opacity), 255, 255, 255));
                        int boxW = Math.Max((int)(ptsz.Width + 10), pw);
                        g.FillRectangle(bb2, px, py - (int)ptsz.Height - 6, boxW, (int)ptsz.Height + 6);
                        g.DrawString(pt, _fInfo, wb, px + 5, py - ptsz.Height - 3);
                    }
                }

                {
                    var (pos2, dur2) = GetTime();
                    string tStr = dur2 > 0 ? $"{Fmt(pos2)} / {Fmt(dur2)}" : Fmt(pos2);
                    var tSz = g.MeasureString(tStr, _fTime);
                    using var brTime = new SolidBrush(Color.FromArgb((int)(230 * _opacity), 255, 255, 255));
                    float tx = _rcTimeline.Right - tSz.Width;
                    float ty = _rcTimeline.Y - tSz.Height - 6;
                    g.DrawString(tStr, _fTime, brTime, tx, ty);
                }
            }

            DrawRoundButton(g, _rcBtnRemove, "×", IsPulsing(ButtonId.Remove));
            DrawRoundButton(g, _rcBtnOpen, "↥", IsPulsing(ButtonId.Open));
            DrawRoundButton(g, _rcBtnPlay, "⏯", IsPulsing(ButtonId.PlayPause));
            if (_showBackFwd)
            {
                DrawRoundButton(g, _rcBtnBack, "⏪", IsPulsing(ButtonId.Back10));
                DrawRoundButton(g, _rcBtnFwd, "⏩", IsPulsing(ButtonId.Fwd10));
            }
            if (_showPrevNext)
            {
                DrawRoundButton(g, _rcBtnPrev, "⏮", IsPulsing(ButtonId.PrevChapter));
                DrawRoundButton(g, _rcBtnNext, "⏭", IsPulsing(ButtonId.NextChapter));
            }
            DrawRoundButton(g, _rcBtnFull, "⛶", IsPulsing(ButtonId.Fullscreen));

            using (var trk = new Pen(Color.FromArgb((int)(220 * _opacity), 180, 180, 180), 2))
                g.DrawLine(trk, _rcVolTrack.X, _volCenterY, _rcVolTrack.Right, _volCenterY);

            int vKnobCenter = _rcVolTrack.X + (int)Math.Round(_vol * _rcVolTrack.Width);
            vKnobCenter = Math.Clamp(vKnobCenter,
                _rcVolTrack.X + VolKnobRadius,
                _rcVolTrack.Right - VolKnobRadius);
            using (var knb = new SolidBrush(Color.FromArgb((int)(255 * _opacity), 255, 255, 255)))
                g.FillEllipse(knb,
                    vKnobCenter - VolKnobRadius,
                    _volCenterY - VolKnobRadius,
                    VolKnobRadius * 2,
                    VolKnobRadius * 2);

            static string Fmt(double s)
            {
                if (double.IsNaN(s) || s < 0) s = 0;
                var ts = TimeSpan.FromSeconds(s);
                return ts.TotalHours >= 1
                    ? ts.ToString(@"hh\:mm\:ss")
                    : ts.ToString(@"mm\:ss");
            }
        }

        private void DrawTopBar(Graphics g)
        {
            int barH = 18;
            using (var barBr = new SolidBrush(Color.FromArgb((int)(255 * _opacity), Theme.Accent)))
                g.FillRectangle(barBr,
                    new Rectangle(16, (_rcTopBar.Height - barH) / 2, 4, barH));

            string title = !string.IsNullOrWhiteSpace(NowPlayingTitle)
                ? NowPlayingTitle
                : (GetTitle?.Invoke() ?? string.Empty);

            int textLeft = 16 + 4 + 8;
            int textRight = _rcTopInfo.X - 12;
            if (!string.IsNullOrWhiteSpace(title) && textRight - textLeft > 10)
            {
                int h = Math.Max(_fTopTitle.Height + 2, 20);
                var rcTitle = new Rectangle(
                    textLeft,
                    (_rcTopBar.Height - h) / 2,
                    Math.Max(20, textRight - textLeft),
                    h);

                TextRenderer.DrawText(
                    g,
                    title,
                    _fTopTitle,
                    rcTitle,
                    Color.FromArgb((int)(240 * _opacity), 255, 255, 255),
                    TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPadding |
                    TextFormatFlags.VerticalCenter
                );
            }

            DrawTopRoundButton(g, _rcTopInfo, IconInfo, "i", IsPulsing(ButtonId.TopInfo));
            DrawTopRoundButton(g, _rcTopSettings, IconSettings, "⚙", IsPulsing(ButtonId.TopSettings));
        }

        private void DrawTopRoundButton(Graphics g, Rectangle r, Image? icon, string fallback, bool pulse)
        {
            int aFill = (int)(((pulse ? 170 : 110)) * Math.Clamp(_opacity, 0f, 1f));
            using (var b = new SolidBrush(Color.FromArgb(aFill, 255, 255, 255)))
                g.FillEllipse(b, r);

            if (pulse)
            {
                using var glow = new Pen(Color.FromArgb((int)(220 * Math.Clamp(_opacity, 0f, 1f)), 255, 255, 255), 3f);
                g.DrawEllipse(glow, r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4);
            }

            if (icon != null)
            {
                int s = Math.Max(18, Math.Min(r.Width, r.Height) - 6);
                int x = r.X + (r.Width - s) / 2;
                int y = r.Y + (r.Height - s) / 2;
                var dest = new Rectangle(x, y, s, s);
                if (_opacity < 1f)
                {
                    var cm = new ColorMatrix { Matrix33 = Math.Clamp(_opacity, 0f, 1f) };
                    using var ia = new ImageAttributes();
                    ia.SetColorMatrix(cm);
                    g.DrawImage(icon, dest, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, ia);
                }
                else g.DrawImage(icon, dest);
            }
            else
            {
                var sz = g.MeasureString(fallback, _fSymbol);
                using var tb = new SolidBrush(Color.FromArgb((int)(255 * Math.Clamp(_opacity, 0f, 1f)), 0, 0, 0));
                g.DrawString(fallback, _fSymbol, tb,
                    r.X + (r.Width - sz.Width) / 2f,
                    r.Y + (r.Height - sz.Height) / 2f);
            }
        }

        private void DrawRoundButton(Graphics gg, Rectangle r, string txt, bool pulse = false)
        {
            int aFill = (int)(((pulse ? 170 : 110)) * Math.Clamp(_opacity, 0f, 1f));
            using (var b = new SolidBrush(Color.FromArgb(aFill, 255, 255, 255)))
                gg.FillEllipse(b, r);

            if (pulse)
            {
                using var glow = new Pen(Color.FromArgb((int)(220 * Math.Clamp(_opacity, 0f, 1f)), 255, 255, 255), 3f);
                gg.DrawEllipse(glow, r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4);
            }

            var sz = gg.MeasureString(txt, _fSymbol);
            using var tb = new SolidBrush(Color.FromArgb((int)(255 * Math.Clamp(_opacity, 0f, 1f)), 0, 0, 0));
            gg.DrawString(txt, _fSymbol, tb,
                r.X + (r.Width - sz.Width) / 2f,
                r.Y + (r.Height - sz.Height) / 2f);
        }
    }

    // ===================== DirectShow / COM interop =====================
    [ComImport, Guid("B196B28B-BAB4-101A-B69C-00AA00341D07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpecifyPropertyPages { [PreserveSig] int GetPages(out CAUUID pPages); }

    [StructLayout(LayoutKind.Sequential)]
    struct CAUUID
    {
        public int cElems;
        public IntPtr pElems;
    }

    [ComImport, Guid("B196B28D-BAB4-101A-B69C-00AA00341D07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyPage
    {
        void SetPageSite(IPropertyPageSite pPageSite);
        void Activate(IntPtr hWndParent, ref RECT pRect, int bModal);
        void Deactivate();
        void GetPageInfo(out PROPPAGEINFO pPageInfo);
        void SetObjects(uint cObjects, [MarshalAs(UnmanagedType.IUnknown)] ref object ppUnk);
        void Show(int nCmdShow);
        void Move(ref RECT pRect);
        [PreserveSig] int IsPageDirty();
        void Apply();
        void Help(string pszHelpDir);
        void TranslateAccelerator(ref MSG pMsg);
    }

    [ComImport, Guid("B196B28C-BAB4-101A-B69C-00AA00341D07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyPageSite
    {
        [PreserveSig] int OnStatusChange(int dwFlags);
        [PreserveSig] int GetLocaleID(out int pLocaleID);
        [PreserveSig] int GetPageContainer([MarshalAs(UnmanagedType.IUnknown)] out object ppUnk);
        [PreserveSig] int TranslateAccelerator(ref MSG pMsg);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPPAGEINFO
    {
        public int cb;
        public IntPtr pszTitle;
        public Size size;
        public IntPtr pszDocString;
        public IntPtr pszHelpFile;
        public int dwHelpContext;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hWnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public Point pt; }

    // ===================== Helpers DirectShow =====================
    internal static class DsHelpers
    {
        public static IBaseFilter? CreateFilterByFriendlyName(string? friendlyName)
        {
            if (string.IsNullOrWhiteSpace(friendlyName)) return null;
            Guid[] cats = {
                DSFilterCategory.LegacyAmFilterCategory,
                DSFilterCategory.AudioRendererCategory,
                DSFilterCategory.AudioCompressorCategory,
                DSFilterCategory.VideoCompressorCategory,
                DSFilterCategory.VideoInputDevice,
                DSFilterCategory.AudioInputDevice
            };
            foreach (var cat in cats)
            {
                foreach (var d in DsDevice.GetDevicesOfCat(cat))
                {
                    bool match =
                        d.Name.Equals(friendlyName, StringComparison.OrdinalIgnoreCase) ||
                        d.Name.Contains(friendlyName, StringComparison.OrdinalIgnoreCase) ||
                        friendlyName.Contains(d.Name, StringComparison.OrdinalIgnoreCase);
                    if (match)
                    {
                        var iid = typeof(IBaseFilter).GUID;
                        d.Mon.BindToObject(null, null, ref iid, out object obj);
                        return (IBaseFilter)obj;
                    }
                }
            }
            return null;
        }

        public static IBaseFilter? CreateFilterByClsid(Guid clsid)
        {
            try
            {
                var t = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
                var obj = Activator.CreateInstance(t);
                return obj as IBaseFilter;
            }
            catch { return null; }
        }
    }

    // ===================== Host property pages di DirectShow =====================
    internal sealed class DsPropPageHost : Panel, IPropertyPageSite, IDisposable
    {
        private readonly Panel _toolbar = new()
        {
            Height = 44,
            BackColor = Theme.PanelAlt,
            Dock = DockStyle.Top,
            Padding = new Padding(10, 8, 10, 8),
            Visible = false
        };
        private readonly ComboBox _pages = new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.System,
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        private readonly Panel _viewport = new()
        {
            BackColor = Theme.Panel,
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 12, 12, 12),
            AutoScroll = true
        };

        private object? _filter;
        private IPropertyPage[] _pp = Array.Empty<IPropertyPage>();
        private int _active = -1;
        private bool _suspendNav;

        private IFilterGraph2? _tempGraph;
        private bool _addedToGraph;

        // offset top grosso per non tagliare la prima riga (LAV/madVR/MPC)
        public int ExtraTopPaddingDpiLogical = 64;

        public DsPropPageHost()
        {
            DoubleBuffered = true;
            BackColor = Theme.Panel;
            Padding = new Padding(0);

            Controls.Add(_viewport);
            Controls.Add(_toolbar);

            _toolbar.Controls.Add(_pages);

            _pages.Left = 10; _pages.Top = 8;
            _pages.Width = Math.Max(220, _toolbar.Width - 20);
            _toolbar.Resize += (_, __) =>
            {
                _pages.Width = Math.Max(220, _toolbar.Width - 20);
            };
            _pages.SelectedIndexChanged += (_, __) =>
            {
                if (!_suspendNav) ActivateIndex(_pages.SelectedIndex);
            };

            Resize += (_, __) => RefitActivePage();
        }

        public void LoadFromFriendlyName(string friendlyName)
        {
            Clear();
            var f = DsHelpers.CreateFilterByFriendlyName(friendlyName);
            if (f == null) throw new ApplicationException($"Filtro \"{friendlyName}\" non trovato.");
            _filter = f;
            AttachToTempGraphIfNeeded();
            BuildPages();
        }
        public void LoadFromClsid(Guid clsid)
        {
            Clear();
            var f = DsHelpers.CreateFilterByClsid(clsid);
            if (f == null) throw new ApplicationException($"CLSID {clsid} non trovato/instanziabile.");
            _filter = f;
            AttachToTempGraphIfNeeded();
            BuildPages();
        }
        public void LoadFromFilter(IBaseFilter filter)
        {
            Clear();
            _filter = filter;
            AttachToTempGraphIfNeeded();
            BuildPages();
        }

        private void AttachToTempGraphIfNeeded()
        {
            try
            {
                if (_filter is not IBaseFilter bf) return;
                _tempGraph = (IFilterGraph2)new FilterGraph();
                int hr = _tempGraph.AddFilter(bf, "CfgTarget");
                _addedToGraph = hr >= 0;
            }
            catch
            {
                _addedToGraph = false;
                try { if (_tempGraph != null) Marshal.ReleaseComObject(_tempGraph); } catch { }
                _tempGraph = null;
            }
        }

        public void Apply()
        {
            foreach (var p in _pp)
            {
                try
                {
                    int hr = p.IsPageDirty();
                    if (hr != 0) // != S_OK
                        continue;

                    p.Apply();
                }
                catch
                {
                }
            }
        }

        public void Clear()
        {
            try
            {
                for (int i = 0; i < _pp.Length; i++)
                {
                    try { _pp[i].Show(0); } catch { }
                    try { _pp[i].Deactivate(); } catch { }
                    try { Marshal.ReleaseComObject(_pp[i]); } catch { }
                }
            }
            catch { }

            _pp = Array.Empty<IPropertyPage>();
            _pages.Items.Clear();
            _viewport.Controls.Clear();
            _active = -1;

            try
            {
                if (_addedToGraph && _tempGraph != null && _filter is IBaseFilter bf)
                {
                    try { _tempGraph.RemoveFilter(bf); } catch { }
                }
            }
            catch { }

            if (_filter != null && Marshal.IsComObject(_filter))
                try { Marshal.ReleaseComObject(_filter); } catch { }
            _filter = null;

            if (_tempGraph != null)
            {
                try { Marshal.ReleaseComObject(_tempGraph); } catch { }
                _tempGraph = null;
            }
            _addedToGraph = false;
        }

        private void BuildPages()
        {
            if (_filter == null) return;

            _suspendNav = true;
            _pages.Items.Clear();
            _pp = Array.Empty<IPropertyPage>();

            try
            {
                var spp = _filter as ISpecifyPropertyPages
                    ?? throw new ApplicationException("Il filtro non espone pagine di proprietà.");

                spp.GetPages(out var cauuid);
                try
                {
                    var okPages = new List<IPropertyPage>();
                    var okTitles = new List<string>();

                    if (cauuid.cElems > 0 && cauuid.pElems != IntPtr.Zero)
                    {
                        for (int i = 0; i < cauuid.cElems; i++)
                        {
                            Guid clsid = Marshal.PtrToStructure<Guid>(
                                IntPtr.Add(cauuid.pElems, i * Marshal.SizeOf<Guid>()));

                            try
                            {
                                var type = Type.GetTypeFromCLSID(clsid, true)!;
                                if (Activator.CreateInstance(type) is not IPropertyPage page)
                                    continue;

                                object unk = _filter!;
                                page.SetObjects(1, ref unk);
                                page.SetPageSite(this);
                                page.GetPageInfo(out var info);

                                string title = info.pszTitle != IntPtr.Zero
                                    ? (Marshal.PtrToStringUni(info.pszTitle) ?? $"Pagina {okPages.Count + 1}")
                                    : $"Pagina {okPages.Count + 1}";

                                okPages.Add(page);
                                okTitles.Add(title);
                            }
                            catch
                            {
                            }
                        }
                    }

                    if (okPages.Count == 0)
                    {
                        _pp = Array.Empty<IPropertyPage>();
                        _pages.Items.Add("(Nessuna property page disponibile)");
                        _pages.SelectedIndex = 0;
                        ShowPlaceholder("(Nessuna property page disponibile)");
                    }
                    else
                    {
                        _pp = okPages.ToArray();
                        foreach (var t in okTitles) _pages.Items.Add(t);
                        _pages.SelectedIndex = 0;
                    }
                }
                finally
                {
                    if (cauuid.pElems != IntPtr.Zero) Marshal.FreeCoTaskMem(cauuid.pElems);
                }
            }
            catch (Exception ex)
            {
                _pp = Array.Empty<IPropertyPage>();
                _pages.Items.Add("Errore: " + ex.Message);
                _pages.SelectedIndex = 0;
                ShowPlaceholder("Errore: " + ex.Message);
            }
            finally
            {
                _suspendNav = false;
            }

            _toolbar.Visible = _pp.Length > 1;
            EnsureFirstPageActivated();
        }

        private void EnsureFirstPageActivated()
        {
            if (_pp.Length > 0)
                ActivateIndex(0);
        }

        private void ShowPlaceholder(string text)
        {
            _viewport.Controls.Clear();
            var lbl = new Label
            {
                Text = text,
                ForeColor = Theme.Muted,
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _viewport.Controls.Add(lbl);
        }

        private int TopEmbedOffset
        {
            get
            {
                float scale = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;
                int logical = ExtraTopPaddingDpiLogical;
                int px = (int)Math.Round(logical * scale);
                return Math.Max(32, px); // minimo 32px
            }
        }

        private RECT CalcRectToFit()
        {
            // diamo alla property page una rect virtuale MOLTO più alta.
            // così lei crea tutto il contenuto e noi scrolliamo.
            var view = _viewport.ClientRectangle;
            if (view.Width < 1 || view.Height < 1) view = new Rectangle(0, 0, 1, 1);

            int virtualHeight = view.Height + 1600;
            if (virtualHeight < 1000) virtualHeight = 1000;

            return new RECT
            {
                left = 0,
                top = TopEmbedOffset,
                right = view.Width - 1,
                bottom = virtualHeight
            };
        }

        private void ActivateIndex(int index)
        {
            if (_pp.Length == 0)
            {
                ShowPlaceholder("(Nessuna property page disponibile)");
                _active = -1;
                return;
            }
            if (index < 0 || index >= _pp.Length)
            {
                ShowPlaceholder("(Indice pagina non valido)");
                _active = -1;
                return;
            }

            var next = _pp[index];
            if (next == null)
            {
                ShowPlaceholder("(Pagina non disponibile)");
                _active = -1;
                return;
            }

            if (_active >= 0 && _active < _pp.Length && _pp[_active] != null)
            {
                try { _pp[_active].Show(0); } catch { }
                try { _pp[_active].Deactivate(); } catch { }
            }

            _viewport.Controls.Clear();
            _active = index;

            var rc = CalcRectToFit();
            try
            {
                next.Activate(_viewport.Handle, ref rc, 0);
                next.Show(5);
            }
            catch (Exception ex)
            {
                ShowPlaceholder("Errore nell'attivazione della pagina:\r\n" + ex.Message);
            }
        }

        private void RefitActivePage()
        {
            if (_active < 0 || _active >= _pp.Length) return;
            var rc = CalcRectToFit();
            try { _pp[_active].Move(ref rc); } catch { }
        }

        // IPropertyPageSite
        int IPropertyPageSite.OnStatusChange(int dwFlags) => 0;
        int IPropertyPageSite.GetLocaleID(out int pLocaleID) { pLocaleID = 0x0400; return 0; }
        int IPropertyPageSite.GetPageContainer(out object ppUnk) { ppUnk = this; return 0; }
        int IPropertyPageSite.TranslateAccelerator(ref MSG pMsg) => 1;

        protected override void Dispose(bool disposing)
        {
            if (disposing) Clear();
            base.Dispose(disposing);
        }
    }

    // ===================== madVR settings embedder =====================
    internal sealed class MadVrSettingsEmbedder : Panel
    {
        private Process? _proc;
        private IntPtr _wnd = IntPtr.Zero;

        private readonly System.Windows.Forms.Timer _tick;
        private bool _embedded;
        private bool _started;

        private IntPtr _oldParent = IntPtr.Zero;
        private int _oldStyle = 0;
        private bool _savedOld = false;

        private uint _ctrlPid;

        private readonly Panel _placeholder = new() { Dock = DockStyle.Fill, BackColor = Theme.Panel };
        private readonly Button _btnReopen = new() { Text = "Apri impostazioni madVR", Width = 240, Height = 28, FlatStyle = FlatStyle.System };

        private bool _sanitizedButtons;
        private bool _closedButtonRemoved;

        private static readonly Guid CLSID_madVR = new("E1A8B82A-32CE-4B0D-BE0D-AA68C772E423");

        public MadVrSettingsEmbedder()
        {
            DoubleBuffered = true;
            BackColor = Theme.Panel;

            var lblInfo = new Label
            {
                Text = "Le impostazioni madVR non sono aperte.\r\nPremi il bottone per avviarle.",
                ForeColor = Theme.Muted,
                Dock = DockStyle.Top,
                Padding = new Padding(0, 0, 0, 10),
                Height = 68,
                TextAlign = ContentAlignment.MiddleCenter
            };

            _placeholder.Controls.Add(_btnReopen);
            _placeholder.Controls.Add(lblInfo);
            Controls.Add(_placeholder);

            Layout += (_, __) =>
            {
                _btnReopen.Left = (Width - _btnReopen.Width) / 2;
                _btnReopen.Top = (Height - _btnReopen.Height) / 2;
            };

            _btnReopen.Click += (_, __) => { EnsureStarted(); };

            _tick = new System.Windows.Forms.Timer { Interval = 250 };
            _tick.Tick += (_, __) =>
            {
                if (!_started) return;

                if (!_embedded)
                {
                    TryFindAndEmbed();
                }
                else
                {
                    if (_wnd == IntPtr.Zero || !IsWindow(_wnd) || GetParent(_wnd) != Handle)
                    {
                        OnChildClosedOrLost();
                    }
                    else
                    {
                        MoveWindow(_wnd, 0, 0, ClientSize.Width, ClientSize.Height, true);
                        HardenCloseAndSanitizeButtons();
                    }
                }

                if (!_embedded && (_wnd == IntPtr.Zero || !IsWindow(_wnd)))
                {
                    KickOpenSettingsViaPropPage();
                }
            };

            HandleDestroyed += (_, __) => Cleanup(true);
            Resize += (_, __) =>
            {
                if (_embedded && _wnd != IntPtr.Zero && IsWindow(_wnd))
                    MoveWindow(_wnd, 0, 0, ClientSize.Width, ClientSize.Height, true);
            };
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsStarted => _started;

        public void EnsureStarted()
        {
            if (!_started)
            {
                _started = true;
                Start();
            }
            else
            {
                if (_wnd == IntPtr.Zero || !IsWindow(_wnd) || !_embedded)
                {
                    KickOpenSettingsViaPropPage();
                    TryFindAndEmbed();
                }
                if (!_tick.Enabled) _tick.Start();
            }
        }

        private void Start()
        {
            string? folder = GetMadVrFolder();
            if (folder == null)
            {
                ShowPlaceholder("madVR non installato/registrato.");
                return;
            }

            string exe = Path.Combine(folder, "madHcCtrl.exe");
            if (!File.Exists(exe))
            {
                ShowPlaceholder($"madHcCtrl.exe non trovato in:\r\n{folder}");
                return;
            }

            try
            {
                var already = Process.GetProcessesByName("madHcCtrl").FirstOrDefault();
                if (already == null)
                {
                    _proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = false
                    });
                    _proc?.WaitForInputIdle(1000);
                    _ctrlPid = (uint)(_proc?.Id ?? 0);
                }
                else
                {
                    _ctrlPid = (uint)already.Id;
                }

                KickOpenSettingsViaPropPage();
                _tick.Start();
            }
            catch (Exception ex)
            {
                ShowPlaceholder("Errore avvio madVR: " + ex.Message);
            }
        }

        // madVR si apre da solo quando la tab madVR diventa visibile.
        // SettingsModal chiama host.EnsureStarted() quando entri in quella voce.

        private void TryFindAndEmbed()
        {
            if (_embedded) return;

            IntPtr w = FindSettingsWindow();
            if (w == IntPtr.Zero) return;

            _wnd = w;

            if (!_savedOld)
            {
                _oldParent = GetParent(_wnd);
                _oldStyle = GetWindowLong(_wnd, GWL_STYLE);
                _savedOld = true;
            }

            int style = _oldStyle;
            style = (style | WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS) &
                    ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX |
                      WS_MAXIMIZEBOX | WS_SYSMENU | WS_POPUP);
            SetWindowLong(_wnd, GWL_STYLE, style);
            SetParent(_wnd, Handle);
            MoveWindow(_wnd, 0, 0, ClientSize.Width, ClientSize.Height, true);
            ShowWindow(_wnd, SW_SHOW);

            _embedded = true;
            _placeholder.Visible = false;

            HardenCloseAndSanitizeButtons();
        }

        private void HardenCloseAndSanitizeButtons()
        {
            if (_wnd == IntPtr.Zero || !IsWindow(_wnd)) return;

            if (!_closedButtonRemoved)
            {
                IntPtr hMenu = GetSystemMenu(_wnd, false);
                if (hMenu != IntPtr.Zero)
                {
                    RemoveMenu(hMenu, SC_CLOSE, MF_BYCOMMAND);
                    DrawMenuBar(_wnd);
                    _closedButtonRemoved = true;
                }
            }

            if (!_sanitizedButtons)
            {
                EnumChildWindows(_wnd, (h, l) =>
                {
                    if (!IsWindow(h)) return true;
                    string cls = GetClass(h).ToLowerInvariant();
                    if (cls != "button") return true;

                    int id = GetDlgCtrlID(h);
                    string txt = GetText(h).Trim().ToLowerInvariant();

                    // Disabilita "OK"
                    if (id == 1 || txt == "ok")
                    {
                        EnableWindow(h, false);
                        SetWindowText(h, "OK (disabilitato)");
                        return true;
                    }

                    // "Apply" -> stile push di default
                    if (txt.Contains("apply") || txt.Contains("applica"))
                    {
                        SendMessage(h, BM_SETSTYLE, (IntPtr)BS_DEFPUSHBUTTON, (IntPtr)1);
                        return true;
                    }

                    return true;
                }, IntPtr.Zero);

                _sanitizedButtons = true;
            }
        }

        private void OnChildClosedOrLost()
        {
            _embedded = false;
            _wnd = IntPtr.Zero;
            _sanitizedButtons = false;
            _closedButtonRemoved = false;
            _placeholder.Visible = true;
        }

        private void KickOpenSettingsViaPropPage()
        {
            Panel? host = null;
            try
            {
                var filter = DsHelpers.CreateFilterByClsid(CLSID_madVR);
                if (filter == null) return;

                if (filter is not ISpecifyPropertyPages spp) { Release(filter); return; }

                spp.GetPages(out var cauuid);
                try
                {
                    if (cauuid.cElems <= 0 || cauuid.pElems == IntPtr.Zero) return;

                    Guid pageClsid = Marshal.PtrToStructure<Guid>(cauuid.pElems);
                    var type = Type.GetTypeFromCLSID(pageClsid, true)!;
                    if (Activator.CreateInstance(type) is not IPropertyPage page) return;

                    object unk = filter;
                    page.SetObjects(1, ref unk);
                    page.SetPageSite(new DummySite());

                    host = new Panel
                    {
                        Visible = false,
                        Width = 5,
                        Height = 5,
                        Left = -10000,
                        Top = -10000
                    };
                    Controls.Add(host);
                    host.CreateControl();

                    var rc = new RECT { left = 0, top = 0, right = 320, bottom = 200 };
                    page.Activate(host.Handle, ref rc, 0);
                    page.Show(5);

                    IntPtr btn = IntPtr.Zero;
                    EnumChildWindows(host.Handle, (h, l) =>
                    {
                        if (!string.Equals(GetClass(h), "BUTTON", StringComparison.OrdinalIgnoreCase)) return true;
                        string txt = GetText(h).ToLowerInvariant();
                        if (txt.Contains("setting") || txt.Contains("impostaz"))
                        { btn = h; return false; }
                        return true;
                    }, IntPtr.Zero);

                    if (btn != IntPtr.Zero) SendMessage(btn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);

                    page.Show(0);
                    page.Deactivate();
                    Release(page);
                }
                finally
                {
                    if (cauuid.pElems != IntPtr.Zero) Marshal.FreeCoTaskMem(cauuid.pElems);
                    Release(filter);
                }
            }
            catch
            {
            }
            finally
            {
                if (host != null)
                {
                    try { Controls.Remove(host); } catch { }
                    try { host.Dispose(); } catch { }
                }
            }
        }

        private static void Release(object o)
        {
            try
            {
                if (Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
            }
            catch { }
        }

        private IntPtr FindSettingsWindow()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                var title = GetText(h).ToLowerInvariant();
                if (title.Length == 0) return true;

                bool looksLike = title.Contains("madvr") &&
                                 (title.Contains("setting") || title.Contains("impostaz"));
                bool sameProc = (_ctrlPid != 0 && pid == _ctrlPid && title.Contains("setting"));

                if (looksLike || sameProc)
                {
                    found = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return found;
        }

        private void ShowPlaceholder(string text)
        {
            var lbl = _placeholder.Controls.OfType<Label>().FirstOrDefault();
            if (lbl != null)
                lbl.Text = text + "\r\nPremi il bottone per avviare.";
            _placeholder.Visible = true;
        }

        public void CloseSettingsWindow()
        {
            try
            {
                Cleanup(true);
                IntPtr w = FindSettingsWindow();
                if (w != IntPtr.Zero && IsWindow(w))
                {
                    PostMessage(w, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Cleanup(true);
            base.Dispose(disposing);
        }

        private void Cleanup(bool disposingControl)
        {
            try { _tick.Stop(); } catch { }

            try
            {
                if (_wnd != IntPtr.Zero && IsWindow(_wnd))
                {
                    if (GetParent(_wnd) == Handle)
                    {
                        PostMessage(_wnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }

                    if (_savedOld)
                    {
                        try { SetParent(_wnd, _oldParent); } catch { }
                        try { SetWindowLong(_wnd, GWL_STYLE, _oldStyle); } catch { }
                    }
                }
            }
            catch
            {
            }
            finally
            {
                _wnd = IntPtr.Zero;
                _embedded = false;
                _savedOld = false;
                _oldParent = IntPtr.Zero;
                _oldStyle = 0;
                _started = false;
                _sanitizedButtons = false;
                _closedButtonRemoved = false;
            }
        }

        private sealed class DummySite : IPropertyPageSite
        {
            public int OnStatusChange(int dwFlags) => 0;
            public int GetLocaleID(out int pLocaleID) { pLocaleID = 0x0400; return 0; }
            public int GetPageContainer(out object ppUnk) { ppUnk = this; return 0; }
            public int TranslateAccelerator(ref MSG pMsg) => 1;
        }

        // P/Invoke
        private const int GWL_STYLE = -16;
        private const int WS_CHILD = 0x40000000;
        private const int WS_CLIPSIBLINGS = 0x04000000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int SW_SHOW = 5;
        private const int BM_CLICK = 0x00F5;
        private const int BM_SETSTYLE = 0x00F4;
        private const int BS_DEFPUSHBUTTON = 0x0001;
        private const int WM_CLOSE = 0x0010;

        private const int SC_CLOSE = 0xF060;
        private const int MF_BYCOMMAND = 0x0000;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetDlgCtrlID(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr hWnd, string lpString);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveMenu(IntPtr hMenu, int uPosition, int uFlags);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool DrawMenuBar(IntPtr hWnd);

        private static string GetText(IntPtr hWnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
        private static string GetClass(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
        private static string? GetMadVrFolder()
        {
            static string? ReadRegDefault(RegistryView view)
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
                using var key = baseKey.OpenSubKey(@"CLSID\{E1A8B82A-32CE-4B0D-BE0D-AA68C772E423}\InprocServer32");
                return key?.GetValue(null) as string;
            }
            var axPath = ReadRegDefault(RegistryView.Registry64) ?? ReadRegDefault(RegistryView.Registry32);
            if (string.IsNullOrWhiteSpace(axPath)) return null;
            try { return Path.GetDirectoryName(axPath); } catch { return null; }
        }
    }

    // ===================== CONTESTO VIDEO SETTINGS / ENUM =====================
    public enum MadVrHdrMode { Auto = 0, PassthroughHdr, ToneMapHdrToSdr, LutHdrToSdr }
    public enum MadVrCategoryPreset { RendererDefault = 0, Profile1, Profile2, Profile3, Profile4, Profile5, Profile6 }
    public enum MadVrFpsChoice { Adapt = 0, Force60 = 60, Force24 = 24 }

    public sealed class VideoSettings
    {
        public int TargetFps { get; set; }
        public bool AllowUpscaling { get; set; }
        public bool PreferBitstream { get; set; }

        public MadVrHdrMode HdrMode { get; set; } = MadVrHdrMode.Auto;
        public MadVrCategoryPreset ChromaPreset { get; set; } = MadVrCategoryPreset.RendererDefault;
        public MadVrCategoryPreset ImageUpscalePreset { get; set; } = MadVrCategoryPreset.RendererDefault;
        public MadVrCategoryPreset ImageDownscalePreset { get; set; } = MadVrCategoryPreset.RendererDefault;
        public MadVrCategoryPreset RefinementPreset { get; set; } = MadVrCategoryPreset.RendererDefault;
        public MadVrFpsChoice FpsChoice { get; set; } = MadVrFpsChoice.Adapt;
    }

    // ===================== UI KIT (bottoni stile screenshot) =====================
    internal static class UiKit
    {
        // bottone stile outline rettangolare 1px chiaro (sidebar e footer)
        public static Button MakeOutlineButton(string text, bool leftAlign = false, bool useNavBg = false)
        {
            var b = new Button
            {
                Text = text.ToUpperInvariant(),
                FlatStyle = FlatStyle.Flat,
                BackColor = useNavBg ? Theme.Nav : Theme.Panel,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                Height = 32,
                Dock = DockStyle.Top,
                TextAlign = leftAlign ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleCenter,
                Padding = leftAlign ? new Padding(8, 0, 8, 0) : new Padding(0),
                Margin = new Padding(0, 0, 0, 8),
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Theme.Border;
            b.FlatAppearance.MouseOverBackColor = useNavBg ? Theme.Nav : Theme.Panel;
            b.FlatAppearance.MouseDownBackColor = useNavBg ? Theme.Nav : Theme.Panel;
            return b;
        }

        // label titolo gruppo
        public static Label MakeGroupHeader(string text)
        {
            return new Label
            {
                Text = text,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2)
            };
        }

        // label descrizione gruppo
        public static Label MakeGroupSub(string text)
        {
            return new Label
            {
                Text = text,
                ForeColor = Theme.SubtleText,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8),
                MaximumSize = new Size(1000, 0)
            };
        }

        public static RadioButton MakeRadio(string txt)
        {
            return new RadioButton
            {
                Text = txt,
                AutoSize = true,
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                Margin = new Padding(0, 2, 0, 2)
            };
        }

        public static CheckBox MakeCheck(string txt)
        {
            return new CheckBox
            {
                Text = txt,
                AutoSize = true,
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                Margin = new Padding(0, 2, 0, 2)
            };
        }

        public static ComboBox MakePresetCombo()
        {
            var cb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.System,
                Font = new Font("Segoe UI", 9f),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                MinimumSize = new Size(200, 0),
                Margin = new Padding(0, 2, 0, 8)
            };
            cb.Items.Add("Default (renderer)");
            cb.Items.Add("Profile 1");
            cb.Items.Add("Profile 2");
            cb.Items.Add("Profile 3");
            cb.Items.Add("Profile 4");
            cb.Items.Add("Profile 5");
            cb.Items.Add("Profile 6");
            cb.SelectedIndex = 0;
            return cb;
        }

        public static MadVrCategoryPreset ComboToPreset(ComboBox cb)
        {
            return cb.SelectedIndex switch
            {
                0 => MadVrCategoryPreset.RendererDefault,
                1 => MadVrCategoryPreset.Profile1,
                2 => MadVrCategoryPreset.Profile2,
                3 => MadVrCategoryPreset.Profile3,
                4 => MadVrCategoryPreset.Profile4,
                5 => MadVrCategoryPreset.Profile5,
                6 => MadVrCategoryPreset.Profile6,
                _ => MadVrCategoryPreset.RendererDefault
            };
        }
    }

        internal sealed class SettingsModal : ModalBase
        {
            public event Action<int, bool, bool>? ApplyClicked;
            public event Action<VideoSettings>? ApplyDetailed;

            // NAV SINISTRA
            private Panel _leftNav = null!;
            private Panel _navButtonsHost = null!;
            private Panel _bottomButtonsHost = null!;
            private PictureBox _logoBox = null!;
            private Label _logoText = null!;
            private readonly List<Button> _navButtons = new();
            private Button _btnApply = null!;
            private Button _btnClose = null!;

            // DESTRO
            private Panel _rightOuter = null!;
            private OutlinePanel _rightBorder = null!;
            private Panel _pageHost = null!;

            // PAGINE
            private Control? _pgGeneral;
            private Control? _pgMadvr;
            private Control? _pgLavVideo;
            private Control? _pgLavAudio;
            private Control? _pgMpcVr;
            private Control? _pgMpcAr;

            // campi pagina "Generali"
            private RadioButton _fpsAuto = null!;
            private RadioButton _fps60 = null!;
            private RadioButton _fps24 = null!;
            private RadioButton _hdrAuto = null!;
            private RadioButton _hdrPass = null!;
            private RadioButton _hdrTone = null!;
            private RadioButton _hdrLut = null!;
            private CheckBox _upscale = null!;
            private CheckBox _preferBitstream = null!;
            private ComboBox _cbChroma = null!;
            private ComboBox _cbUp = null!;
            private ComboBox _cbDown = null!;
            private ComboBox _cbRefine = null!;

            private int _selNav = 0;

            private static readonly Guid CLSID_MPCVR = new("71F080AA-8661-4093-B15E-4F6903E77D0A");

            public SettingsModal() : base("IMPOSTAZIONI")
            {
                OverlayColor = Theme.BackdropDim;
                CloseOnBackdropClick = true;
                CloseOnEscape = true;
                AutoDisposeOnClose = true;

                // vogliamo tutta la card piena
                ContentHost.AutoSize = false;
                ContentHost.Dock = DockStyle.Fill;
                ContentHost.Padding = new Padding(16);
                ContentHost.BackColor = Theme.Panel;

                BuildLayout();
                HookEvents();

                EnsurePageGeneral();
                ShowPage(0);
                SetNavSelected(0);
            }

            // Precarica tutte le pagine (se vuoi chiamarlo subito dopo aver creato il modal)
            public void EnsureHostsLoaded()
            {
                EnsurePageGeneral();
                EnsurePageMadvr();
                EnsurePageLavVideo();
                EnsurePageLavAudio();
                EnsurePageMpcVr();
                EnsurePageMpcAr();

                try
                {
                    if (_pgMadvr != null)
                    {
                        var host = _pgMadvr
                            .Controls.OfType<Panel>()
                            .SelectMany(p => p.Controls.OfType<MadVrSettingsEmbedder>())
                            .FirstOrDefault();
                        host?.EnsureStarted();
                    }
                }
                catch { }
            }

        // ---------- layout fisico ----------
        private void BuildLayout()
        {
            ContentHost.Controls.Clear();

            var hostSplit = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Theme.Panel,
            };
            hostSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));    // nav sinistra
            hostSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));     // contenuto destro
            ContentHost.Controls.Add(hostSplit);

            // ================== NAV SINISTRA ==================
            _leftNav = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Nav,
                Padding = new Padding(8, 8, 8, 8)
            };
            hostSplit.Controls.Add(_leftNav, 0, 0);

            // ----- header logo -----
            var logoWrap = new Panel
            {
                Dock = DockStyle.Top,
                Height = 64,
                Padding = new Padding(8, 6, 8, 6),
                BackColor = Theme.Nav
            };
            _leftNav.Controls.Add(logoWrap);

            _logoBox = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom,
                Width = 36,
                Height = 36,
                Left = 8,
                Top = 6,
                BackColor = Color.Transparent
            };
            logoWrap.Controls.Add(_logoBox);

            _logoText = new Label
            {
                Text = "CINECOREPLAYER2025",
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                AutoSize = true,
                Left = 8 + 36 + 6,
                Top = 14
            };
            logoWrap.Controls.Add(_logoText);

            var logoLine = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 1,
                BackColor = Theme.Border
            };
            logoWrap.Controls.Add(logoLine);
            logoLine.BringToFront();

            // ----- FOOTER CHIUDI / APPLICA (affiancati) -----
            _bottomButtonsHost = new Panel
            {
                Dock = DockStyle.Bottom,
                BackColor = Theme.Nav,
                Padding = new Padding(8, 8, 8, 8),
                Height = 64
            };
            _leftNav.Controls.Add(_bottomButtonsHost);

            var bottomFlow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            _bottomButtonsHost.Controls.Add(bottomFlow);

            _btnClose = UiKit.MakeOutlineButton("Chiudi", leftAlign: false, useNavBg: true);
            _btnClose.Width = 110;
            _btnClose.Height = 30;
            _btnClose.Margin = new Padding(0, 0, 8, 0);

            _btnApply = UiKit.MakeOutlineButton("Applica", leftAlign: false, useNavBg: true);
            _btnApply.Width = 110;
            _btnApply.Height = 30;
            _btnApply.Margin = new Padding(0);

            bottomFlow.Controls.Add(_btnClose);
            bottomFlow.Controls.Add(_btnApply);

            _bottomButtonsHost.Resize += (_, __) =>
            {
                bottomFlow.Left = (_bottomButtonsHost.ClientSize.Width - bottomFlow.Width) / 2;
                bottomFlow.Top = _bottomButtonsHost.ClientSize.Height - bottomFlow.Height - 4;
            };

            // ----- NAV VOCI (panel + Dock=Top) -----
            _navButtonsHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Nav,
                AutoScroll = true,
                Padding = new Padding(8, 8, 8, 8),
                Margin = new Padding(0)
            };
            _leftNav.Controls.Add(_navButtonsHost);
            _navButtonsHost.BringToFront();

            _navButtons.Clear();
            _navButtonsHost.Controls.Clear();

            string[] navItems =
            {
                "Generali",
                "madVR",
                "LAV VIDEO",
                "LAV AUDIO",
                "MPC VIDEO RENDERER",
                "MPC AUDIO RENDERER"
            };

            // con Dock=Top bisogna aggiungerli al contrario
            for (int i = navItems.Length - 1; i >= 0; i--)
            {
                var b = MkNavButton(navItems[i], i);
                _navButtons.Add(b);
                _navButtonsHost.Controls.Add(b);
            }

            // ================== DESTRA ==================
            _rightOuter = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(8, 8, 8, 8)
            };
            hostSplit.Controls.Add(_rightOuter, 1, 0);

            _rightBorder = new OutlinePanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(12, 12, 12, 12)
            };
            _rightOuter.Controls.Add(_rightBorder);

            // solo contenuto scrollabile
            _pageHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(4),
                AutoScroll = true
            };
            _rightBorder.Controls.Add(_pageHost);

            TryLoadLogo();
        }

        private void TryLoadLogo()
            {
                try
                {
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string[] candidates =
                    {
                    System.IO.Path.Combine(baseDir, "assets", "logo.png"),
                    System.IO.Path.Combine(baseDir, "assets", "logo_orizzontale.png"),
                    System.IO.Path.Combine(baseDir, "logo.png"),
                };
                    foreach (var p in candidates)
                    {
                        if (System.IO.File.Exists(p))
                        {
                            using var temp = Image.FromFile(p);
                            _logoBox.Image = new Bitmap(temp);
                            break;
                        }
                    }
                }
                catch { }
            }

            private Button MkNavButton(string text, int index)
            {
                var b = UiKit.MakeOutlineButton(text, leftAlign: true, useNavBg: true);
                b.Tag = index;
                b.Height = 32;
                b.Dock = DockStyle.Top;
                return b;
            }

            // ---------- eventi ----------
            private void HookEvents()
            {
                _btnApply.Click += (_, __) =>
                {
                    ApplyAllPropertyPagesInCurrentPage();
                    var vs = CollectVideoSettings();
                    ApplyDetailed?.Invoke(vs);
                    ApplyClicked?.Invoke(vs.TargetFps, vs.AllowUpscaling, vs.PreferBitstream);
                    CloseModal();
                };

                _btnClose.Click += (_, __) =>
                {
                    try
                    {
                        (_pgMadvr?.Controls
                            .OfType<Panel>()
                            .SelectMany(p => p.Controls.OfType<MadVrSettingsEmbedder>())
                            .FirstOrDefault())?.CloseSettingsWindow();
                    }
                    catch { }
                    CloseModal();
                };

                foreach (var btn in _navButtons)
                {
                    btn.Click += (_, __) =>
                    {
                        if (btn.Tag is int idx)
                            SetNavSelected(idx);
                    };
                }
            }

            private void ApplyAllPropertyPagesInCurrentPage()
            {
                var current = GetCurrentPage();
                if (current == null) return;

                void Walk(Control c)
                {
                    if (c is DsPropPageHost host)
                    {
                        try { host.Apply(); } catch { }
                    }
                    foreach (Control child in c.Controls) Walk(child);
                }

                Walk(current);
            }

            private Control? GetCurrentPage()
            {
                foreach (Control c in _pageHost.Controls)
                    if (c.Visible) return c;
                return null;
            }

            // ---------- NAV ----------
            private void SetNavSelected(int index)
            {
                _selNav = index;
                foreach (var btn in _navButtons)
                {
                    bool sel = (int)btn.Tag == _selNav;
                    btn.BackColor = sel ? Theme.PanelAlt : Theme.Nav;
                }
                ShowPage(index);

                if (_selNav == 1 && _pgMadvr != null)
                {
                    var host = _pgMadvr.Controls
                        .OfType<Panel>()
                        .SelectMany(p => p.Controls.OfType<MadVrSettingsEmbedder>())
                        .FirstOrDefault();
                    host?.EnsureStarted();
                }
            }

            private void HideAllPages()
            {
                foreach (Control c in _pageHost.Controls)
                    c.Visible = false;
            }

            private void AddPage(Control pg, ref Control? field)
            {
                pg.Visible = false;
                pg.Dock = DockStyle.Fill;
                _pageHost.Controls.Add(pg);
                field = pg;
            }

            private void ShowPage(int index)
            {
                HideAllPages();
                Control? toShow = null;
                switch (index)
                {
                    case 0:
                        EnsurePageGeneral();
                        toShow = _pgGeneral;
                        break;
                    case 1:
                        EnsurePageMadvr();
                        toShow = _pgMadvr;
                        break;
                    case 2:
                        EnsurePageLavVideo();
                        toShow = _pgLavVideo;
                        break;
                    case 3:
                        EnsurePageLavAudio();
                        toShow = _pgLavAudio;
                        break;
                    case 4:
                        EnsurePageMpcVr();
                        toShow = _pgMpcVr;
                        break;
                    case 5:
                        EnsurePageMpcAr();
                        toShow = _pgMpcAr;
                        break;
                }
                if (toShow != null)
                {
                    toShow.Visible = true;
                    toShow.BringToFront();
                }
            }

        // ---------- PAGINA "GENERALI" ----------
        private void EnsurePageGeneral()
        {
            if (_pgGeneral != null) return;

            var page = new Panel
            {
                BackColor = Theme.Panel,
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(12, 12, 12, 12)
            };

            // GRID 2 COLONNE 50/50
            var grid2 = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Theme.Panel,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            grid2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f)); // VIDEO
            grid2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f)); // AUDIO
            page.Controls.Add(grid2);

            // colonna sinistra (VIDEO)
            var leftCol = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                BackColor = Theme.Panel,
                Padding = new Padding(0),
                Margin = new Padding(0, 0, 8, 0)
            };
            leftCol.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            grid2.Controls.Add(leftCol, 0, 0);

            // colonna destra (AUDIO)
            var rightCol = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                BackColor = Theme.Panel,
                Padding = new Padding(0),
                Margin = new Padding(8, 0, 0, 0)
            };
            rightCol.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            grid2.Controls.Add(rightCol, 1, 0);

            // box "pieni" (no bordi, solo colore)
            Panel MakeBox(string title, string subtitle, out Panel body)
            {
                var box = new Panel
                {
                    BackColor = Theme.PanelAlt,              // grigio più chiaro
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Padding = new Padding(12, 10, 12, 10),
                    Margin = new Padding(0, 0, 0, 12),
                    Dock = DockStyle.Top
                };

                var header = UiKit.MakeGroupHeader(title);
                header.Location = new Point(0, 0);
                box.Controls.Add(header);

                var sub = UiKit.MakeGroupSub(subtitle);
                sub.Location = new Point(0, header.Bottom);
                box.Controls.Add(sub);

                body = new Panel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = Color.Transparent,
                    Location = new Point(0, sub.Bottom + 4)
                };
                box.Controls.Add(body);

                return box;
            }

            void AddBox(TableLayoutPanel col, Panel box)
            {
                int row = col.RowCount;
                col.RowCount++;
                col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                col.Controls.Add(box, 0, row);
            }

            // ---------- COLONNA SINISTRA (VIDEO) ----------
            // Algoritmi madVR
            Panel bodyAlgo;
            var boxAlgo = MakeBox(
                "Algoritmi madVR",
                "Profili madVR per upscaling / downscaling / refinement. Scegli quale profilo usare per ciascuna categoria.",
                out bodyAlgo);

            var gridAlgo = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 4,
                AutoSize = true,
                BackColor = Color.Transparent,
                Padding = new Padding(0),
                Margin = new Padding(0),
                Dock = DockStyle.Top
            };
            gridAlgo.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            gridAlgo.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            Label L(string t) => new Label
            {
                Text = t,
                ForeColor = Theme.SubtleText,
                Font = new Font("Segoe UI", 9f),
                AutoSize = true,
                Margin = new Padding(0, 4, 12, 4)
            };

            _cbChroma = UiKit.MakePresetCombo();
            _cbUp = UiKit.MakePresetCombo();
            _cbDown = UiKit.MakePresetCombo();
            _cbRefine = UiKit.MakePresetCombo();

            _cbChroma.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _cbUp.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _cbDown.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _cbRefine.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            gridAlgo.Controls.Add(L("Chroma upscaling:"), 0, 0);
            gridAlgo.Controls.Add(_cbChroma, 1, 0);

            gridAlgo.Controls.Add(L("Image upscaling:"), 0, 1);
            gridAlgo.Controls.Add(_cbUp, 1, 1);

            gridAlgo.Controls.Add(L("Image downscaling:"), 0, 2);
            gridAlgo.Controls.Add(_cbDown, 1, 2);

            gridAlgo.Controls.Add(L("Upscaling refinement:"), 0, 3);
            gridAlgo.Controls.Add(_cbRefine, 1, 3);

            bodyAlgo.Controls.Add(gridAlgo);
            AddBox(leftCol, boxAlgo);

            // HDR
            Panel bodyHdr;
            var boxHdr = MakeBox("madVR — HDR", "Come gestire i contenuti HDR.", out bodyHdr);

            _hdrAuto = UiKit.MakeRadio("Auto");
            _hdrAuto.Checked = true;
            _hdrPass = UiKit.MakeRadio("Passthrough HDR al display");
            _hdrTone = UiKit.MakeRadio("Converti HDR → SDR (tone mapping)");
            _hdrLut = UiKit.MakeRadio("HDR → SDR usando LUT 3D esterna");

            int y = 0;
            foreach (var rb in new[] { _hdrAuto, _hdrPass, _hdrTone, _hdrLut })
            {
                rb.Location = new Point(0, y);
                bodyHdr.Controls.Add(rb);
                y = rb.Bottom + 2;
            }
            AddBox(leftCol, boxHdr);

            // Frequenza monitor
            Panel bodyFps;
            var boxFps = MakeBox(
                "Frequenza monitor",
                "Cambia il refresh del display durante la riproduzione (film vs desktop/sport).",
                out bodyFps);

            _fpsAuto = UiKit.MakeRadio("Non cambiare (usa frequenza attuale)");
            _fpsAuto.Checked = true;
            _fps60 = UiKit.MakeRadio("59/60p (desktop / sport)");
            _fps24 = UiKit.MakeRadio("23/24p (film)");

            y = 0;
            foreach (var rb in new[] { _fpsAuto, _fps60, _fps24 })
            {
                rb.Location = new Point(0, y);
                bodyFps.Controls.Add(rb);
                y = rb.Bottom + 2;
            }
            AddBox(leftCol, boxFps);

            // ---------- COLONNA DESTRA (AUDIO) ----------
            Panel bodyAud;
            var boxAud = MakeBox("Audio", "Uscita audio e passthrough bitstream.", out bodyAud);

            _preferBitstream = UiKit.MakeCheck("Preferisci inviare bitstream al dispositivo (se supporta passthrough)");
            _preferBitstream.Location = new Point(0, 0);
            bodyAud.Controls.Add(_preferBitstream);
            AddBox(rightCol, boxAud);

            Panel bodyVid;
            var boxVid = MakeBox(
                "Video (player)",
                "Opzioni interne del player quando NON sto usando madVR.",
                out bodyVid);

            _upscale = UiKit.MakeCheck("Abilita upscaling lato player");
            _upscale.Location = new Point(0, 0);
            bodyVid.Controls.Add(_upscale);
            AddBox(rightCol, boxVid);

            AddPage(page, ref _pgGeneral);
        }

        // ----- MADVR -----
        private void EnsurePageMadvr()
            {
                if (_pgMadvr != null) return;

                var wrap = new Panel
                {
                    BackColor = Theme.Panel,
                    Dock = DockStyle.Fill,
                    Padding = new Padding(16),
                    AutoScroll = true
                };
                var host = new MadVrSettingsEmbedder
                {
                    Dock = DockStyle.Fill,
                    BackColor = Theme.Panel
                };
                wrap.Controls.Add(host);

                wrap.VisibleChanged += (_, __) =>
                {
                    if (wrap.Visible) host.EnsureStarted();
                };
                host.HandleCreated += (_, __) =>
                {
                    if (wrap.Visible) host.EnsureStarted();
                };

                AddPage(wrap, ref _pgMadvr);
            }

            // ----- LAV VIDEO -----
            private void EnsurePageLavVideo()
            {
                if (_pgLavVideo != null) return;

                var host = new DsPropPageHost
                {
                    Dock = DockStyle.Fill,
                    BackColor = Theme.Panel
                };

                bool ok = true;
                try
                {
                    host.LoadFromFriendlyName("LAV Video Decoder");
                }
                catch (Exception ex)
                {
                    ok = false;
                    host.Dispose();
                    _pgLavVideo = MakeFallbackPage("LAV Video Decoder non trovato.\r\nDettagli: " + ex.Message);
                    AddPage(_pgLavVideo, ref _pgLavVideo);
                    return;
                }

                var wrap = new Panel
                {
                    BackColor = Theme.Panel,
                    Dock = DockStyle.Fill,
                    Padding = new Padding(16),
                    AutoScroll = true
                };
                wrap.Controls.Add(host);

                if (ok) AddPage(wrap, ref _pgLavVideo);
            }

            // ----- LAV AUDIO -----
            private void EnsurePageLavAudio()
            {
                if (_pgLavAudio != null) return;

                var host = new DsPropPageHost
                {
                    Dock = DockStyle.Fill,
                    BackColor = Theme.Panel
                };

                bool ok = true;
                try
                {
                    host.LoadFromFriendlyName("LAV Audio Decoder");
                }
                catch (Exception ex)
                {
                    ok = false;
                    host.Dispose();
                    _pgLavAudio = MakeFallbackPage("LAV Audio Decoder non trovato.\r\nDettagli: " + ex.Message);
                    AddPage(_pgLavAudio, ref _pgLavAudio);
                    return;
                }

                var wrap = new Panel
                {
                    BackColor = Theme.Panel,
                    Dock = DockStyle.Fill,
                    Padding = new Padding(16),
                    AutoScroll = true
                };
                wrap.Controls.Add(host);

                if (ok) AddPage(wrap, ref _pgLavAudio);
            }

            // ----- MPC VIDEO RENDERER -----
            private void EnsurePageMpcVr()
            {
                if (_pgMpcVr != null) return;

                var host = new DsPropPageHost
                {
                    Dock = DockStyle.Fill,
                    BackColor = Theme.Panel
                };

                bool ok = false;
                try
                {
                    host.LoadFromClsid(CLSID_MPCVR);
                    ok = true;
                }
                catch { }
                if (!ok)
                {
                    try
                    {
                        host.LoadFromFriendlyName("MPC Video Renderer");
                        ok = true;
                    }
                    catch { }
                }

                if (!ok)
                {
                    host.Dispose();
                    _pgMpcVr = MakeFallbackPage("MPC Video Renderer non trovato.");
                    AddPage(_pgMpcVr, ref _pgMpcVr);
                    return;
                }

                var wrap = new Panel
                {
                    BackColor = Theme.Panel,
                    Dock = DockStyle.Fill,
                    Padding = new Padding(16),
                    AutoScroll = true
                };
                wrap.Controls.Add(host);

                AddPage(wrap, ref _pgMpcVr);
            }

            // ----- MPC AUDIO RENDERER -----
            private void EnsurePageMpcAr()
            {
                if (_pgMpcAr != null) return;

                var host = new DsPropPageHost
                {
                    Dock = DockStyle.Fill,
                    BackColor = Theme.Panel
                };

                bool ok = true;
                try
                {
                    host.LoadFromFriendlyName("MPC Audio Renderer");
                }
                catch (Exception ex)
                {
                    ok = false;
                    host.Dispose();
                    _pgMpcAr = MakeFallbackPage("MPC Audio Renderer non trovato.\r\nDettagli: " + ex.Message);
                    AddPage(_pgMpcAr, ref _pgMpcAr);
                    return;
                }

                var wrap = new Panel
                {
                    BackColor = Theme.Panel,
                    Dock = DockStyle.Fill,
                    Padding = new Padding(16),
                    AutoScroll = true
                };
                wrap.Controls.Add(host);

                if (ok) AddPage(wrap, ref _pgMpcAr);
            }

            private Control MakeFallbackPage(string msg)
            {
                var panel = new Panel
                {
                    BackColor = Theme.Panel,
                    Dock = DockStyle.Fill,
                    Padding = new Padding(16),
                    AutoScroll = true
                };
                var lbl = new Label
                {
                    Text = msg,
                    ForeColor = Theme.Danger,
                    Font = new Font("Segoe UI", 9f),
                    AutoSize = false,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter
                };
                panel.Controls.Add(lbl);
                return panel;
            }

            // ---------- RACCOLTA/SYNC DELLE OPZIONI ----------
            private VideoSettings CollectVideoSettings()
            {
                var vs = new VideoSettings();

                if (_fpsAuto.Checked) { vs.TargetFps = 0; vs.FpsChoice = MadVrFpsChoice.Adapt; }
                else if (_fps60.Checked) { vs.TargetFps = 60; vs.FpsChoice = MadVrFpsChoice.Force60; }
                else { vs.TargetFps = 24; vs.FpsChoice = MadVrFpsChoice.Force24; }

                vs.AllowUpscaling = _upscale.Checked;
                vs.PreferBitstream = _preferBitstream.Checked;

                if (_hdrPass.Checked) vs.HdrMode = MadVrHdrMode.PassthroughHdr;
                else if (_hdrTone.Checked) vs.HdrMode = MadVrHdrMode.ToneMapHdrToSdr;
                else if (_hdrLut.Checked) vs.HdrMode = MadVrHdrMode.LutHdrToSdr;
                else vs.HdrMode = MadVrHdrMode.Auto;

                vs.ChromaPreset = UiKit.ComboToPreset(_cbChroma);
                vs.ImageUpscalePreset = UiKit.ComboToPreset(_cbUp);
                vs.ImageDownscalePreset = UiKit.ComboToPreset(_cbDown);
                vs.RefinementPreset = UiKit.ComboToPreset(_cbRefine);

                return vs;
            }

            public void SyncFromState(int targetFps, bool upscale, bool preferBitstream)
            {
                EnsurePageGeneral();

                _fpsAuto.Checked = (targetFps == 0);
                _fps60.Checked = (targetFps == 60);
                _fps24.Checked = (targetFps == 24 || targetFps == 23);

                _upscale.Checked = upscale;
                _preferBitstream.Checked = preferBitstream;
            }

            public void FocusApply()
            {
                try { _btnApply.Focus(); } catch { }
            }
        }

    // ===================== MODALE CREDITI =====================
    internal sealed class CreditsModal : ModalBase
    {
        private Button _btnClose = null!;
        private Panel _bottomBar = null!;
        private Panel _scrollHost = null!;
        private FlowLayoutPanel _stack = null!;

        public CreditsModal() : base("CREDITI")
        {
            OverlayColor = Theme.BackdropDim;
            CloseOnBackdropClick = true;
            CloseOnEscape = true;
            AutoDisposeOnClose = true;

            ContentHost.AutoSize = false;
            ContentHost.Dock = DockStyle.Fill;
            ContentHost.Padding = new Padding(16);
            ContentHost.BackColor = Theme.Panel;

            BuildLayout();
            BuildContent();
        }

        private void BuildLayout()
        {
            ContentHost.Controls.Clear();

            var outline = new OutlinePanel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(0)
            };
            ContentHost.Controls.Add(outline);

            // riga sopra
            var topLine = new Panel
            {
                Dock = DockStyle.Top,
                Height = 1,
                BackColor = Theme.Border
            };
            outline.Controls.Add(topLine);

            _scrollHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(16, 12, 16, 12),
                AutoScroll = true
            };
            outline.Controls.Add(_scrollHost);
            _scrollHost.BringToFront();

            // barra bottom con bottone CHIUDI in basso a destra
            _bottomBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                BackColor = Theme.Panel,
                Padding = new Padding(0, 8, 16, 8)
            };
            outline.Controls.Add(_bottomBar);

            _bottomBar.Paint += (_, pe) =>
            {
                using var p = new Pen(Theme.Border, 1f);
                pe.Graphics.DrawLine(p, 0, 0, _bottomBar.Width, 0);
            };

            _btnClose = UiKit.MakeOutlineButton("Chiudi", leftAlign: false);
            _btnClose.Dock = DockStyle.None;
            _btnClose.Height = 28;
            _btnClose.Width = 90;
            _bottomBar.Controls.Add(_btnClose);
            _bottomBar.Resize += (_, __) =>
            {
                _btnClose.Left = _bottomBar.Width - _btnClose.Width - 16;
                _btnClose.Top = (_bottomBar.Height - _btnClose.Height) / 2;
            };

            _btnClose.Click += (_, __) => CloseModal();

            _stack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                BackColor = Theme.Panel,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            _scrollHost.Controls.Add(_stack);
        }

        private void BuildContent()
        {
            _stack.SuspendLayout();
            _stack.Controls.Clear();

            void AddCard(string title, string subtitle, string[] bodyLines, (string text, string url)[]? links = null)
            {
                var box = new OutlinePanel
                {
                    BackColor = Theme.Panel,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Padding = new Padding(12, 10, 12, 10),
                    Margin = new Padding(0, 0, 0, 12)
                };

                var hdr = UiKit.MakeGroupHeader(title);
                hdr.Location = new Point(0, 0);
                box.Controls.Add(hdr);

                var sub = UiKit.MakeGroupSub(subtitle);
                sub.Location = new Point(0, hdr.Bottom + 0);
                box.Controls.Add(sub);

                int curY = sub.Bottom + 6;

                foreach (var line in bodyLines)
                {
                    var lbl = new Label
                    {
                        Text = line,
                        ForeColor = Theme.SubtleText,
                        Font = new Font("Segoe UI", 9f),
                        AutoSize = true,
                        Margin = new Padding(0),
                        Location = new Point(0, curY),
                        MaximumSize = new Size(1000, 0)
                    };
                    box.Controls.Add(lbl);
                    curY = lbl.Bottom + 4;
                }

                if (links != null)
                {
                    foreach (var (text, url) in links)
                    {
                        var lnk = new LinkLabel
                        {
                            Text = text,
                            Font = new Font("Segoe UI", 9f),
                            AutoSize = true,
                            LinkColor = Theme.Accent,
                            ActiveLinkColor = Theme.Accent,
                            Location = new Point(0, curY),
                            Margin = new Padding(0),
                        };
                        lnk.Links.Add(0, text.Length, url);
                        lnk.LinkClicked += (_, e) =>
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = e.Link.LinkData?.ToString(),
                                    UseShellExecute = true
                                });
                            }
                            catch { }
                        };
                        box.Controls.Add(lnk);
                        curY = lnk.Bottom + 4;
                    }
                }

                _stack.Controls.Add(box);
            }

            AddCard(
                "Cinecore Player 2025",
                "Progetto non commerciale",
                new[]
                {
                    "© 2025 — Niccolò Landolfi.",
                    "Video: DirectShow + LAV + madVR / MPCVR / EVR",
                    "Audio: LAV + MPC Audio Renderer / renderer di sistema"
                }
            );

            AddCard(
                "Componenti principali",
                "",
                new[]
                {
                    "• LAV Filters",
                    "• madVR (video renderer)",
                    "• MPC Video Renderer / MPC Audio Renderer",
                    "• DirectShowLib, FFmpeg.AutoGen"
                }
            );

            AddCard(
                "Ringraziamenti",
                "",
                new[]
                {
                    "• LAV Filters, MPC-HC team",
                    "• madshi (madVR)"
                }
            );

            AddCard(
                "Link utili",
                "",
                Array.Empty<string>(),
                new (string text, string url)[]
                {
                    ("Sito Cinecore", "https://cinecore.it"),
                    ("Repository (se pubblico)", "https://github.com/")
                }
            );

            _stack.ResumeLayout();
        }
    }

    // ===================== SWITCH FREQUENZA MONITOR =====================
    internal sealed class DisplayModeSwitcher
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            private const int CCHDEVICENAME = 32;
            private const int CCHFORMNAME = 32;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int EDS_RAWMODE = 0x00000002;
        private const int CDS_FULLSCREEN = 0x00000004;
        private const int CDS_UPDATEREGISTRY = 0x00000001;

        private string? _device;
        private DEVMODE? _original;

        public bool SwitchToNearest(Screen screen, int desiredFps)
        {
            try
            {
                _device = screen.DeviceName;
                var cur = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (!EnumDisplaySettingsEx(_device, ENUM_CURRENT_SETTINGS, ref cur, 0))
                    return false;
                _original = cur;

                var best = cur;
                int target = (desiredFps <= 25) ? 24 : 60;
                int alt = (target == 24) ? 23 : 59;
                int bestHz = cur.dmDisplayFrequency;
                int bestDelta = int.MaxValue;

                DEVMODE mode = new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                for (int i = 0; EnumDisplaySettingsEx(_device, i, ref mode, EDS_RAWMODE); i++)
                {
                    if (mode.dmPelsWidth != cur.dmPelsWidth || mode.dmPelsHeight != cur.dmPelsHeight)
                        continue;
                    int hz = mode.dmDisplayFrequency;
                    if (hz <= 0) continue;
                    int delta = Math.Min(Math.Abs(hz - target), Math.Abs(hz - alt));
                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        best = mode;
                        bestHz = hz;
                    }
                }

                if (bestHz == cur.dmDisplayFrequency) return true;
                int r = ChangeDisplaySettingsEx(_device, ref best, IntPtr.Zero,
                    CDS_FULLSCREEN | CDS_UPDATEREGISTRY, IntPtr.Zero);
                return r == 0;
            }
            catch { return false; }
        }

        public void RestoreIfChanged()
        {
            try
            {
                if (_device == null || _original == null) return;
                var orig = _original.Value;
                ChangeDisplaySettingsEx(_device, ref orig, IntPtr.Zero,
                    CDS_FULLSCREEN | CDS_UPDATEREGISTRY, IntPtr.Zero);
            }
            catch { }
            finally
            {
                _original = null;
                _device = null;
            }
        }
    }

    // ===================== WIN32 helper =====================
    internal static class Win32
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy,
            uint uFlags);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_SHOWWINDOW = 0x0040;
    }
}
