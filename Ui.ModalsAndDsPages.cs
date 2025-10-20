#nullable enable
using DirectShowLib;
using DSFilterCategory = DirectShowLib.FilterCategory;
using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Text;
using System.Collections.Generic;

namespace CinecorePlayer2025
{
    // ===================== TEMA (squadrato) =====================
    internal static class Theme
    {
        public static readonly Color BackdropDim = Color.FromArgb(185, 0, 0, 0);
        public static readonly Color Card = Color.FromArgb(30, 30, 34);
        public static readonly Color Panel = Color.FromArgb(24, 24, 28);
        public static readonly Color PanelAlt = Color.FromArgb(34, 34, 40);
        public static readonly Color Border = Color.FromArgb(64, 64, 70);
        public static readonly Color Nav = Color.FromArgb(26, 26, 30);
        public static readonly Color Accent = Color.FromArgb(40, 120, 255);
        public static readonly Color AccentSoft = Color.FromArgb(26, 90, 210);
        public static readonly Color Text = Color.White;
        public static readonly Color SubtleText = Color.FromArgb(208, 208, 214);
    }

    // ===================== HUD OVERLAY (logica invariata; fix minimi) =====================
    internal sealed class HudOverlay : Control
    {
        public event Action? OpenClicked;
        public event Action? PlayPauseClicked;
        public event Action? StopClicked;
        public event Action? FullscreenClicked;
        public event Action? SkipBack10Clicked;
        public event Action? SkipForward10Clicked;
        public event Action? PrevChapterClicked;
        public event Action? NextChapterClicked;
        public event Action<float>? VolumeChanged;
        public event Action<double>? SeekRequested;
        public event Action<double, Point>? PreviewRequested;

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } }

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

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<string>? GetInfoLine { get; set; }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<(double pos, double dur)>? GetTime { get; set; }

        [DefaultValue(false)] public bool AutoHide { get; set; }
        [DefaultValue(2000)] public int IdleHideDelayMs { get; set; } = 2000;
        [DefaultValue(900)] public int HideGraceMs { get; set; } = 900;
        [DefaultValue(120)] public int FadeOutMs { get; set; } = 120;
        [DefaultValue(false)] public bool TimelineVisible { get; set; } = false;

        private DateTime _fadeStartAt = DateTime.MinValue;
        private float _vol = 1.0f;
        private bool _overActiveZone, _drag, _dragVol;
        private DateTime _lastMove = DateTime.UtcNow;
        private DateTime _lastPreviewAt = DateTime.MinValue;
        private readonly System.Windows.Forms.Timer _fade;
        private float _opacity = 1f;
        private Bitmap? _preview; private double _previewSec; private int _lastMouseX;
        private DateTime _forceShowUntil = DateTime.MinValue;

        public enum ButtonId { None, Remove, Open, PlayPause, Back10, Fwd10, PrevChapter, NextChapter, Fullscreen }
        private ButtonId _pulseBtn = ButtonId.None;
        private DateTime _pulseUntil = DateTime.MinValue;
        public void Pulse(ButtonId btn, int ms = 180) { _pulseBtn = btn; _pulseUntil = DateTime.UtcNow.AddMilliseconds(Math.Max(60, ms)); Invalidate(); }
        private bool IsPulsing(ButtonId btn) => _pulseBtn == btn && DateTime.UtcNow < _pulseUntil;

        public HudOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
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
                var now = DateTime.UtcNow;
                _lastMouseX = e.X;
                if ((_drag || _dragVol) && Control.MouseButtons == MouseButtons.None) { StopDragging(); return; }
                _overActiveZone = ActiveZone.Contains(e.Location);
                if (_overActiveZone || _drag || _dragVol) { _lastMove = now; if (_opacity != 1f) { _opacity = 1f; Invalidate(); } }

                if (_dragVol)
                {
                    float v = (e.X - VolX) / (float)VolWidth; v = Math.Clamp(v, 0f, 1f);
                    _vol = v; VolumeChanged?.Invoke(v); Invalidate();
                    return;
                }

                if (TimelineVisible && TimelineRect.Contains(e.Location) && GetTime != null)
                {
                    if ((now - _lastPreviewAt).TotalMilliseconds >= 90)
                    {
                        _lastPreviewAt = now;
                        var (_, dur) = GetTime();
                        if (dur > 0)
                        {
                            double ratio = (e.X - TimelineRect.X) / (double)TimelineRect.Width;
                            ratio = Math.Clamp(ratio, 0, 1);
                            double s = ratio * dur;
                            PreviewRequested?.Invoke(s, PointToScreen(new Point(e.X, TimelineRect.Y)));
                        }
                    }
                }
                else
                {
                    if (_preview != null) { SetPreview(null, _previewSec); }
                }
            };
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!Capture) StopDragging(); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) StopDragging(); }

        private Rectangle ActiveZone => new Rectangle(0, Height - 120, Width, 120);
        public void ShowOnce(int milliseconds = 2000) { _forceShowUntil = DateTime.UtcNow.AddMilliseconds(Math.Max(250, milliseconds)); _opacity = 1f; Invalidate(); }
        public void SetPreview(Bitmap? bmp, double seconds) { _preview?.Dispose(); _preview = bmp; _previewSec = seconds; Invalidate(); }
        public void SetExternalVolume(float v) { _vol = Math.Clamp(v, 0, 1); Invalidate(); }
        public void PerformVolumeDelta(float delta, Action<float> apply) { _vol = Math.Clamp(_vol + delta, 0f, 1f); apply(_vol); Invalidate(); }

        private void StopDragging()
        {
            if (_drag || _dragVol) { _drag = false; _dragVol = false; Capture = false; Invalidate(); }
        }

        private Rectangle _btnOpen => new(16 + 36, Height - 44, 28, 28);
        private Rectangle _btnRemove => new(16, Height - 44, 28, 28);
        private Rectangle _btnFull => new(Width - 44, Height - 44, 28, 28);
        private int CenterY => Height - 44;
        private int BtnSize => 28;
        private int Gap => 36;
        private Rectangle _btnPlay => new(Width / 2 - BtnSize / 2, CenterY, BtnSize, BtnSize);
        private Rectangle _btnBack10 => new(_btnPlay.X - Gap, CenterY, BtnSize, BtnSize);
        private Rectangle _btnFwd10 => new(_btnPlay.Right + Gap - BtnSize, CenterY, BtnSize, BtnSize);
        private Rectangle _btnPrevChap => new(_btnBack10.X - Gap, CenterY, BtnSize, BtnSize);
        private Rectangle _btnNextChap => new(_btnFwd10.Right + (Gap - BtnSize), CenterY, BtnSize, BtnSize);
        private int VolWidth => 180;
        private int VolX => _btnFull.X - 16 - VolWidth;
        private int VolY => Height - 30;
        private Rectangle TimelineRect => new(16, Height - 56, Width - 32, 6);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            if (!IsHudInteractive(e.Location))
            {
                ForwardMouseToUnderlying(WinMsg.WM_LBUTTONDOWN, e.Location, IntPtr.Zero);
                return;
            }

            if (_btnRemove.Contains(e.Location)) { StopClicked?.Invoke(); Pulse(ButtonId.Remove); return; }
            if (_btnOpen.Contains(e.Location)) { OpenClicked?.Invoke(); Pulse(ButtonId.Open); return; }
            if (_btnPlay.Contains(e.Location)) { PlayPauseClicked?.Invoke(); Pulse(ButtonId.PlayPause); return; }
            if (_btnBack10.Contains(e.Location)) { SkipBack10Clicked?.Invoke(); Pulse(ButtonId.Back10); return; }
            if (_btnFwd10.Contains(e.Location)) { SkipForward10Clicked?.Invoke(); Pulse(ButtonId.Fwd10); return; }
            if (_btnPrevChap.Contains(e.Location)) { PrevChapterClicked?.Invoke(); Pulse(ButtonId.PrevChapter); return; }
            if (_btnNextChap.Contains(e.Location)) { NextChapterClicked?.Invoke(); Pulse(ButtonId.NextChapter); return; }
            if (_btnFull.Contains(e.Location)) { FullscreenClicked?.Invoke(); Pulse(ButtonId.Fullscreen); return; }

            var vtrack = new Rectangle(VolX, VolY - 6, VolWidth, 12);
            if (vtrack.Contains(e.Location))
            {
                _dragVol = true;
                Capture = true;
                float v = (e.X - VolX) / (float)VolWidth; v = Math.Clamp(v, 0f, 1f);
                _vol = v; VolumeChanged?.Invoke(v); Invalidate(); return;
            }

            if (TimelineVisible && TimelineRect.Contains(e.Location) && GetTime != null)
            {
                _drag = true;
                Capture = true;
                var (_, dur) = GetTime();
                double r = (e.X - TimelineRect.X) / (double)TimelineRect.Width; r = Math.Clamp(r, 0, 1);
                SeekRequested?.Invoke(r * dur); Invalidate();
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (!IsHudInteractive(e.Location))
                ForwardMouseToUnderlying(WinMsg.WM_LBUTTONDBLCLK, e.Location, IntPtr.Zero);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                if (!IsHudInteractive(e.Location))
                    ForwardMouseToUnderlying(WinMsg.WM_LBUTTONUP, e.Location, IntPtr.Zero);
                StopDragging();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
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

        private Rectangle VolTrack => new Rectangle(VolX, VolY - 6, VolWidth, 12);

        private bool IsHudInteractive(Point p)
        {
            if (ActiveZone.Contains(p)) return true;
            if (TimelineVisible && (TimelineRect.Contains(p))) return true;
            if (VolTrack.Contains(p)) return true;
            return false;
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            if (m.Msg == WM_NCHITTEST)
            {
                int x = (short)((uint)m.LParam & 0xFFFF);
                int y = (short)(((uint)m.LParam >> 16) & 0xFFFF);
                Point client = PointToClient(new Point(x, y));

                if (!IsHudInteractive(client))
                {
                    m.Result = (IntPtr)(-1); // HTTRANSPARENT
                    return;
                }
            }
            base.WndProc(ref m);
        }

        private enum WinMsg : uint
        {
            WM_MOUSEMOVE = 0x0200,
            WM_LBUTTONDOWN = 0x0201,
            WM_LBUTTONUP = 0x0202,
            WM_LBUTTONDBLCLK = 0x0203,
            WM_MOUSEWHEEL = 0x020A
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
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

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var bb = new SolidBrush(Color.FromArgb((int)(110 * _opacity), 0, 0, 0)))
                g.FillRectangle(bb, new Rectangle(0, Height - 100, Width, 100));

            var info = GetInfoLine?.Invoke() ?? "";
            using var fInfo = new Font("Segoe UI", 9f);
            using var brInfo = new SolidBrush(Color.FromArgb((int)(220 * _opacity), 230, 230, 230));
            g.DrawString(info, fInfo, brInfo, 16, Height - 88);

            if (TimelineVisible && GetTime != null)
            {
                var (pos, dur) = GetTime();
                using var tlBg = new SolidBrush(Color.FromArgb((int)(120 * _opacity), 200, 200, 200));
                using var tlFg = new SolidBrush(Color.FromArgb((int)(255 * _opacity), 255, 255, 255));
                g.FillRectangle(tlBg, TimelineRect);
                if (dur > 0)
                {
                    int w = (int)(TimelineRect.Width * (pos / Math.Max(0.0001, dur)));
                    if (w > 0) g.FillRectangle(tlFg, new Rectangle(TimelineRect.X, TimelineRect.Y, Math.Min(w, TimelineRect.Width), TimelineRect.Height));
                }

                if (_preview != null && TimelineRect.Contains(new Point(_lastMouseX, TimelineRect.Y + 2)))
                {
                    int pw = _preview.Width, ph = _preview.Height;
                    int px = Math.Clamp(_lastMouseX - pw / 2, TimelineRect.Left, TimelineRect.Right - pw);
                    int py = TimelineRect.Y - ph - 18;
                    var dest = new Rectangle(px, py, pw, ph);
                    if (_opacity < 1f)
                    {
                        var cm = new ColorMatrix { Matrix33 = Math.Clamp(_opacity, 0f, 1f) };
                        using var ia = new ImageAttributes();
                        ia.SetColorMatrix(cm);
                        g.DrawImage(_preview, dest, 0, 0, _preview.Width, _preview.Height, GraphicsUnit.Pixel, ia);
                    }
                    else
                    {
                        g.DrawImage(_preview, dest);
                    }
                    string pt = Fmt(_previewSec); var ptsz = g.MeasureString(pt, fInfo);
                    using var bb2 = new SolidBrush(Color.FromArgb((int)(220 * _opacity), 0, 0, 0));
                    using var wb = new SolidBrush(Color.FromArgb((int)(255 * _opacity), 255, 255, 255));
                    int boxW = Math.Max((int)(ptsz.Width + 10), pw);
                    g.FillRectangle(bb2, px, py - ptsz.Height - 6, boxW, ptsz.Height + 6);
                    g.DrawString(pt, fInfo, wb, px + 5, py - ptsz.Height - 3);
                }

                {
                    var (pos2, dur2) = GetTime();
                    string tStr = dur2 > 0 ? $"{Fmt(pos2)} / {Fmt(dur2)}" : Fmt(pos2);
                    using var fTime = new Font("Segoe UI", 9f, FontStyle.Bold);
                    var tSz = g.MeasureString(tStr, fTime);
                    using var brTime = new SolidBrush(Color.FromArgb((int)(230 * _opacity), 255, 255, 255));
                    float tx = TimelineRect.Right - tSz.Width;
                    float ty = TimelineRect.Y - tSz.Height - 6;
                    g.DrawString(tStr, fTime, brTime, tx, ty);
                }
            }

            DrawBtn(g, _btnRemove, "×", _opacity, IsPulsing(ButtonId.Remove));
            DrawBtn(g, _btnOpen, "↥", _opacity, IsPulsing(ButtonId.Open));
            DrawBtn(g, _btnPlay, "⏯", _opacity, IsPulsing(ButtonId.PlayPause));
            DrawBtn(g, _btnBack10, "⏪", _opacity, IsPulsing(ButtonId.Back10));
            DrawBtn(g, _btnFwd10, "⏩", _opacity, IsPulsing(ButtonId.Fwd10));
            DrawBtn(g, _btnPrevChap, "⏮", _opacity, IsPulsing(ButtonId.PrevChapter));
            DrawBtn(g, _btnNextChap, "⏭", _opacity, IsPulsing(ButtonId.NextChapter));
            DrawBtn(g, _btnFull, "⛶", _opacity, IsPulsing(ButtonId.Fullscreen));

            using var trk = new Pen(Color.FromArgb((int)(220 * _opacity), 180, 180, 180), 2);
            g.DrawLine(trk, VolX, VolY, VolX + VolWidth, VolY);
            int knob = VolX + (int)(_vol * VolWidth);
            using var kn = new SolidBrush(Color.FromArgb((int)(255 * _opacity), 255, 255, 255));
            g.FillEllipse(kn, knob - 6, VolY - 6, 12, 12);

            static string Fmt(double s) { if (double.IsNaN(s) || s < 0) s = 0; var ts = TimeSpan.FromSeconds(s); return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss"); }
            static void DrawBtn(Graphics gg, Rectangle r, string txt, float opacity, bool pulse = false)
            {
                int aFill = (int)(((pulse ? 170 : 110)) * Math.Clamp(opacity, 0f, 1f));
                using (var b = new SolidBrush(Color.FromArgb(aFill, 255, 255, 255)))
                    gg.FillEllipse(b, r);
                if (pulse)
                {
                    using var glow = new Pen(Color.FromArgb((int)(220 * Math.Clamp(opacity, 0f, 1f)), 255, 255, 255), 3f);
                    gg.DrawEllipse(glow, r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4);
                }
                using var f = new Font("Segoe UI", 11f, FontStyle.Bold);
                var sz = gg.MeasureString(txt, f);
                using var tb = new SolidBrush(Color.FromArgb((int)(255 * Math.Clamp(opacity, 0f, 1f)), 0, 0, 0));
                gg.DrawString(txt, f, tb, r.X + (r.Width - sz.Width) / 2f, r.Y + (r.Height - sz.Height) / 2f);
            }
        }
    }

    // ===================== BASE MODALI V2 (squadrato, spacing ampio, zero sovrapposizioni) =====================
    internal abstract class ModalBaseV2 : UserControl
    {
        private Panel _card = null!;
        private Label _title = null!;
        protected Panel BodyPanel = null!;

        public event Action? Closed;

        [Category("Appearance")]
        [DefaultValue("")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string Title
        {
            get => _title?.Text ?? "";
            set { if (_title != null) _title.Text = value; Invalidate(); }
        }

        protected ModalBaseV2()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.SupportsTransparentBackColor, true);

            BackColor = Color.Transparent;
            Dock = DockStyle.Fill;

            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;

            _card = new Panel
            {
                BackColor = Theme.Card,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_card);

            _title = new Label
            {
                AutoSize = false,
                Height = 64,
                Dock = DockStyle.Top,
                Padding = new Padding(24, 18, 24, 8),
                Font = new Font("Segoe UI Semibold", 18f),
                ForeColor = Theme.Text,
                Text = ""
            };
            _card.Controls.Add(_title);

            BodyPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Panel,
                Padding = new Padding(18)
            };
            _card.Controls.Add(BodyPanel);

            Resize += (_, __) => LayoutCard();
            VisibleChanged += (_, __) => LayoutCard();
        }

        protected void RaiseClose()
        {
            Visible = false;
            Closed?.Invoke();
        }

        private float DpiScale => DeviceDpi > 0 ? DeviceDpi / 96f : 1f;

        private void LayoutCard()
        {
            int w = Width, h = Height;
            if (w <= 0 || h <= 0) return;

            // Usa più spazio e centra, senza comprimere
            int margin = (int)(24 * DpiScale);
            int minW = (int)(840 * DpiScale);
            int minH = (int)(560 * DpiScale);
            int maxW = Math.Min((int)(w * 0.96), (int)(1400 * DpiScale));
            int cardW = Math.Max(minW, Math.Min(maxW, w - margin * 2));
            int cardH = Math.Max(minH, h - margin * 2);

            int x = (w - cardW) / 2;
            int y = (h - cardH) / 2;

            _card.Bounds = new Rectangle(x, y, cardW, cardH);
            Invalidate(); // soft shadow in OnPaintBackground
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Color.Transparent);

            // backdrop
            using (var dim = new SolidBrush(Theme.BackdropDim))
                g.FillRectangle(dim, ClientRectangle);

            // soft shadow squadrata (anelli rettangolari)
            var rc = _card.Bounds;
            if (rc.Width > 0 && rc.Height > 0)
            {
                for (int i = 20; i >= 3; i -= 3)
                {
                    int a = 6 * i; // intensità crescente verso l’esterno
                    using var pen = new Pen(Color.FromArgb(Math.Min(90, a), 0, 0, 0), 1);
                    var ring = Rectangle.Inflate(rc, i, i);
                    g.DrawRectangle(pen, ring);
                }
            }
        }
    }

    // ===================== DirectShow property pages COM =====================
    [ComImport, Guid("B196B28B-BAB4-101A-B69C-00AA00341D07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpecifyPropertyPages { [PreserveSig] int GetPages(out CAUUID pPages); }
    [StructLayout(LayoutKind.Sequential)] struct CAUUID { public int cElems; public IntPtr pElems; }
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
        public int cb; public IntPtr pszTitle; public Size size; public IntPtr pszDocString; public IntPtr pszHelpFile; public int dwHelpContext;
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

    // ===================== Host property pages (Dock=Fill, padding ampio) =====================
    internal sealed class DsPropPageHost : Panel, IPropertyPageSite, IDisposable
    {
        private readonly Panel _toolbar = new() { Height = 48, BackColor = Theme.Panel, Dock = DockStyle.Top, Padding = new Padding(12, 10, 12, 10) };
        private readonly ComboBox _pages = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.System, Width = 520 };
        private readonly Panel _viewport = new() { BackColor = Theme.Panel, Dock = DockStyle.Fill, Padding = new Padding(12) };

        private object? _filter;
        private IPropertyPage[] _pp = Array.Empty<IPropertyPage>();
        private int _active = -1;
        private bool _suspendNav;

        private IFilterGraph2? _tempGraph;
        private bool _addedToGraph;

        public DsPropPageHost()
        {
            BackColor = Theme.Panel;
            DoubleBuffered = true;

            Controls.Add(_viewport);
            Controls.Add(_toolbar);
            _toolbar.Controls.Add(_pages);

            _pages.Left = 12; _pages.Top = 10;

            _pages.SelectedIndexChanged += (_, __) =>
            {
                if (_suspendNav) return;
                ActivateIndex(_pages.SelectedIndex);
            };

            Resize += (_, __) => RefitActivePage();
            Paint += (_, e) =>
            {
                using var p = new Pen(Theme.Border);
                var rc = ClientRectangle; rc.Inflate(-1, -1);
                e.Graphics.DrawRectangle(p, rc);
            };
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
                try { if (p.IsPageDirty() == 0) continue; p.Apply(); } catch { }
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
                                IntPtr.Add(cauuid.pElems, i * Marshal.SizeOf<Guid>())
                            );

                            try
                            {
                                var type = Type.GetTypeFromCLSID(clsid, true)!;
                                if (Activator.CreateInstance(type) is not IPropertyPage page) continue;

                                object unk = _filter!;
                                page.SetObjects(1, ref unk);
                                page.SetPageSite(this);

                                page.GetPageInfo(out var info);
                                string title = info.pszTitle != IntPtr.Zero
                                    ? (Marshal.PtrToStringUni(info.pszTitle) ?? $"Page {okPages.Count + 1}")
                                    : $"Page {okPages.Count + 1}";

                                okPages.Add(page);
                                okTitles.Add(title);
                            }
                            catch { }
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
            if (_pp.Length > 0) ActivateIndex(0);
        }

        private void ShowPlaceholder(string text)
        {
            _viewport.Controls.Clear();
            var lbl = new Label
            {
                Text = text,
                ForeColor = Color.Gainsboro,
                Dock = DockStyle.Fill,
                Padding = new Padding(16)
            };
            _viewport.Controls.Add(lbl);
        }

        private void ActivateIndex(int index)
        {
            if (_pp.Length == 0) { ShowPlaceholder("(Nessuna property page disponibile)"); _active = -1; return; }
            if (index < 0 || index >= _pp.Length) { ShowPlaceholder("(Indice pagina non valido)"); _active = -1; return; }

            var next = _pp[index];
            if (next == null) { ShowPlaceholder("(Pagina non disponibile)"); _active = -1; return; }

            if (_active >= 0 && _active < _pp.Length && _pp[_active] != null)
            {
                try { _pp[_active].Show(0); } catch { }
                try { _pp[_active].Deactivate(); } catch { }
            }

            _viewport.Controls.Clear();
            _active = index;

            var rc = CalcRectToFit();
            try { next.Activate(_viewport.Handle, ref rc, 0); next.Show(5); }
            catch (Exception ex) { ShowPlaceholder("Errore nell'attivazione della pagina:\r\n" + ex.Message); }
        }

        private void RefitActivePage()
        {
            if (_active < 0 || _active >= _pp.Length) return;
            var rc = CalcRectToFit();
            try { _pp[_active].Move(ref rc); } catch { }
        }

        private RECT CalcRectToFit()
        {
            var view = _viewport.ClientRectangle;
            if (view.Width < 1 || view.Height < 1) view = new Rectangle(0, 0, 1, 1);
            return new RECT { left = view.Left, top = view.Top, right = view.Right, bottom = view.Bottom };
        }

        // IPropertyPageSite
        int IPropertyPageSite.OnStatusChange(int dwFlags) => 0;
        int IPropertyPageSite.GetLocaleID(out int pLocaleID) { pLocaleID = 0x0400; return 0; }
        int IPropertyPageSite.GetPageContainer(out object ppUnk) { ppUnk = this; return 0; }
        int IPropertyPageSite.TranslateAccelerator(ref MSG pMsg) => 1;

        protected override void Dispose(bool disposing) { if (disposing) Clear(); base.Dispose(disposing); }
    }

    // ===================== madVR settings embedder (anti-close su OK/Apply) =====================
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

        private readonly Panel _empty = new() { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(18) };
        private readonly Button _btnReopen = new() { Text = "Apri impostazioni madVR", Width = 240, Height = 38, FlatStyle = FlatStyle.System };

        private bool _sanitizedButtons;
        private bool _closedButtonRemoved;

        private static readonly Guid CLSID_madVR = new("E1A8B82A-32CE-4B0D-BE0D-AA68C772E423");

        public MadVrSettingsEmbedder()
        {
            BackColor = Theme.Panel;

            _btnReopen.Click += (_, __) => { EnsureStarted(); };

            _empty.Controls.Add(new Label
            {
                Text = "Le impostazioni madVR non sono aperte.\r\nPremi il bottone per avviarle.",
                ForeColor = Color.Gainsboro,
                Dock = DockStyle.Top,
                Padding = new Padding(0, 0, 0, 10),
                Height = 68
            });
            _empty.Controls.Add(_btnReopen);
            Controls.Add(_empty);
            Layout += (_, __) =>
            {
                _btnReopen.Left = (Width - _btnReopen.Width) / 2;
                _btnReopen.Top = (Height - _btnReopen.Height) / 2;
            };

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

                // Se non troviamo la finestra per un po', riprova ad aprirla
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

        public void EnsureStarted()
        {
            if (!_started)
            {
                _started = true;
                Start();
            }
            else
            {
                // FIX: se la finestra è stata chiusa/perduta, riapri e ri-embedda
                if (_wnd == IntPtr.Zero || !IsWindow(_wnd) || !_embedded)
                {
                    KickOpenSettingsViaPropPage();
                    TryFindAndEmbed();
                }
                if (!_tick.Enabled) _tick.Start();
            }
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsStarted => _started;

        private void Start()
        {
            string? folder = GetMadVrFolder();
            if (folder == null) { ShowEmpty("madVR non installato/registrato."); return; }

            string exe = Path.Combine(folder, "madHcCtrl.exe");
            if (!File.Exists(exe)) { ShowEmpty($"madHcCtrl.exe non trovato in:\r\n{folder}"); return; }

            try
            {
                var already = Process.GetProcessesByName("madHcCtrl").FirstOrDefault();
                if (already == null)
                {
                    _proc = Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = false });
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
            catch (Exception ex) { ShowEmpty("Errore avvio madVR: " + ex.Message); }
        }

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
                    ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU | WS_POPUP);
            SetWindowLong(_wnd, GWL_STYLE, style);
            SetParent(_wnd, Handle);
            MoveWindow(_wnd, 0, 0, ClientSize.Width, ClientSize.Height, true);
            ShowWindow(_wnd, SW_SHOW);

            _embedded = true;
            _empty.Visible = false;

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

                    if (id == 1 || txt == "ok")
                    {
                        EnableWindow(h, false);
                        SetWindowText(h, "OK (disabilitato)");
                        return true;
                    }

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
            _empty.Visible = true;
        }

        private void KickOpenSettingsViaPropPage()
        {
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

                    using var host = new Panel { Visible = false, Width = 1, Height = 1 };
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
                    Controls.Remove(host);
                    host.Dispose();
                }
                finally
                {
                    if (cauuid.pElems != IntPtr.Zero) Marshal.FreeCoTaskMem(cauuid.pElems);
                    Release(filter);
                }
            }
            catch { /* best effort */ }
        }

        private static void Release(object o) { try { if (Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { } }

        private IntPtr FindSettingsWindow()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                var title = GetText(h).ToLowerInvariant();
                if (title.Length == 0) return true;

                bool looksLike = title.Contains("madvr") && (title.Contains("setting") || title.Contains("impostaz"));
                bool sameProc = (_ctrlPid != 0 && pid == _ctrlPid && title.Contains("setting"));
                if (looksLike || sameProc) { found = h; return false; }
                return true;
            }, IntPtr.Zero);

            return found;
        }

        private void ShowEmpty(string text)
        {
            var lbl = _empty.Controls.OfType<Label>().FirstOrDefault();
            if (lbl != null) lbl.Text = text + "\r\nPremi il bottone per avviare.";
            _empty.Visible = true;
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
            catch { }
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

        private static string GetText(IntPtr hWnd) { var sb = new StringBuilder(512); GetWindowText(hWnd, sb, sb.Capacity); return sb.ToString(); }
        private static string GetClass(IntPtr hWnd) { var sb = new StringBuilder(256); GetClassName(hWnd, sb, sb.Capacity); return sb.ToString(); }

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

    // ===================== Contratti impostazioni video/madVR =====================
    public enum MadVrHdrMode { Auto = 0, PassthroughHdr, ToneMapHdrToSdr, LutHdrToSdr }
    public enum MadVrCategoryPreset { RendererDefault = 0, Profile1, Profile2, Profile3, Profile4, Profile5, Profile6 }
    public enum MadVrFpsChoice { Adapt = 0, Force60 = 60, Force24 = 24 }

    public sealed class VideoSettings
    {
        public int TargetFps { get; set; }               // 0=auto, 24, 60
        public bool AllowUpscaling { get; set; }          // upscaling lato player (non madVR)
        public bool PreferBitstream { get; set; }
        public MadVrHdrMode HdrMode { get; set; } = MadVrHdrMode.Auto;
        public MadVrCategoryPreset ChromaPreset { get; set; } = MadVrCategoryPreset.RendererDefault;
        public MadVrCategoryPreset ImageUpscalePreset { get; set; } = MadVrCategoryPreset.RendererDefault;
        public MadVrCategoryPreset ImageDownscalePreset { get; set; } = MadVrCategoryPreset.RendererDefault;
        public MadVrCategoryPreset RefinementPreset { get; set; } = MadVrCategoryPreset.RendererDefault;
        public MadVrFpsChoice FpsChoice { get; set; } = MadVrFpsChoice.Adapt;
    }

    // ===================== Helpers UI (card squadrate) =====================
    internal static class UiKit
    {
        public static Button MakePrimary(string text)
        {
            var b = new Button
            {
                Text = text,
                Width = 140,
                Height = 42,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                BackColor = Theme.Accent,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 10.5f)
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Theme.AccentSoft;
            b.FlatAppearance.MouseDownBackColor = Theme.AccentSoft;
            return b;
        }

        public static Button MakeGhost(string text)
        {
            var b = new Button
            {
                Text = text,
                Width = 120,
                Height = 42,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                BackColor = Theme.PanelAlt,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI", 10.5f)
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Theme.Border;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(38, 38, 44);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(44, 44, 50);
            return b;
        }

        public sealed class CardPanel : Panel
        {
            public CardPanel()
            {
                DoubleBuffered = true;
                BackColor = Theme.Card;
                Padding = new Padding(18);
                Margin = new Padding(0, 8, 0, 16);
                BorderStyle = BorderStyle.FixedSingle;
            }
        }
    }

    // ===================== SETTINGS MODAL (squadrata, spazio generoso, padding corretti) =====================
    internal sealed class SettingsModal : ModalBaseV2
    {
        public event Action<int, bool, bool>? ApplyClicked;
        public event Action<VideoSettings>? ApplyDetailed;

        private FlowLayoutPanel _nav = null!;
        private Panel _content = null!;
        private Button _btnApply = null!;
        private Button _btnClose = null!;
        private Panel _bottom = null!;

        private readonly string[] _navItems = new[]
        {
            "Generali",
            "madVR",
            "LAV Audio",
            "LAV Video",
            "MPC Video Renderer",
            "MPC Audio Renderer"
        };
        private readonly List<Button> _navButtons = new();
        private int _selNav = 0;

        private static readonly Guid CLSID_MPCVR = new("71F080AA-8661-4093-B15E-4F6903E77D0A");

        private Panel? _pgGeneral;
        private Control? _pgMadvr;
        private DsPropPageHost? _pgLavAudio, _pgLavVideo, _pgMpcVr, _pgMpcAr;

        private RadioButton _fpsAuto = null!;
        private RadioButton _fps24 = null!;
        private RadioButton _fps60 = null!;
        private CheckBox _upscale = null!;
        private CheckBox _preferBitstream = null!;

        private RadioButton _hdrAuto = null!;
        private RadioButton _hdrPass = null!;
        private RadioButton _hdrTone = null!;
        private RadioButton _hdrLut = null!;

        private ComboBox _cbChroma = null!;
        private ComboBox _cbUp = null!;
        private ComboBox _cbDown = null!;
        private ComboBox _cbRefine = null!;

        private bool _loaded;

        public SettingsModal()
        {
            Title = "Impostazioni";

            var main = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                BackColor = Theme.Panel,
                Padding = new Padding(18),
            };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300)); // nav largo
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            BodyPanel.Controls.Add(main);

            _nav = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Theme.Nav,
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                Margin = new Padding(0)
            };
            foreach (var (text, idx) in _navItems.Select((t, i) => (t, i)))
            {
                var b = MkNavButton(text, idx);
                _navButtons.Add(b);
                _nav.Controls.Add(b);
            }

            _content = new Panel
            {
                BackColor = Theme.Panel,
                Dock = DockStyle.Fill,
                Padding = new Padding(18)
            };
            _content.Paint += (_, e) =>
            {
                using var pen = new Pen(Theme.Border);
                var rc = _content.ClientRectangle; rc.Inflate(-1, -1);
                e.Graphics.DrawRectangle(pen, rc);
            };

            _bottom = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel, Height = 72, Padding = new Padding(18, 14, 18, 14) };
            _btnApply = UiKit.MakePrimary("Applica");
            _btnClose = UiKit.MakeGhost("Chiudi");
            _btnApply.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            _btnClose.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            _bottom.Controls.Add(_btnApply);
            _bottom.Controls.Add(_btnClose);
            _bottom.Resize += (_, __) =>
            {
                _btnApply.Location = new Point(_bottom.Width - _btnApply.Width - 18, (_bottom.Height - _btnApply.Height) / 2);
                _btnClose.Location = new Point(_btnApply.Left - _btnClose.Width - 10, (_bottom.Height - _btnClose.Height) / 2);
            };

            main.Controls.Add(_nav, 0, 0);
            main.Controls.Add(_content, 1, 0);
            main.Controls.Add(_bottom, 0, 1);
            main.SetColumnSpan(_bottom, 2);

            _btnApply.Click += (_, __) =>
            {
                // "Applica" esiste SOLO in Generali: applichiamo le nostre impostazioni e chiudiamo la modale.
                var vs = CollectVideoSettings();
                ApplyDetailed?.Invoke(vs);
                ApplyClicked?.Invoke(vs.TargetFps, vs.AllowUpscaling, vs.PreferBitstream);
                RaiseClose();
            };
            _btnClose.Click += (_, __) =>
            {
                // Chiude la modale e (per pulizia) chiude la finestra impostazioni madVR se è aperta/embeddeda
                try { (_pgMadvr as MadVrSettingsEmbedder)?.CloseSettingsWindow(); } catch { }
                RaiseClose();
            };

            VisibleChanged += (_, __) =>
            {
                if (Visible && !_loaded)
                {
                    _loaded = true;
                    EnsureGeneralPage();
                    SetNavSelected(0);
                }
                else if (!Visible)
                {
                    try { (_pgMadvr as MadVrSettingsEmbedder)?.CloseSettingsWindow(); } catch { }
                }
            };

            BodyPanel.Resize += (_, __) => UpdateResponsive(main);
            UpdateResponsive(main);
        }

        private void UpdateResponsive(TableLayoutPanel main)
        {
            int w = BodyPanel.Width;
            bool narrow = w < 980;

            main.SuspendLayout();
            if (narrow)
            {
                if (main.ColumnCount != 1)
                {
                    main.Controls.Remove(_nav);
                    main.Controls.Remove(_content);
                    main.Controls.Remove(_bottom);

                    main.ColumnCount = 1;
                    main.RowCount = 3;
                    main.ColumnStyles.Clear();
                    main.RowStyles.Clear();
                    main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    main.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));     // nav top
                    main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));     // content
                    main.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));     // bottom

                    _nav.Height = 84;
                    foreach (var b in _navButtons) { b.Width = 220; b.Height = 48; }
                    _nav.WrapContents = true; _nav.FlowDirection = FlowDirection.LeftToRight;

                    main.Controls.Add(_nav, 0, 0);
                    main.Controls.Add(_content, 0, 1);
                    main.Controls.Add(_bottom, 0, 2);
                }
            }
            else
            {
                if (main.ColumnCount != 2)
                {
                    main.Controls.Remove(_nav);
                    main.Controls.Remove(_content);
                    main.Controls.Remove(_bottom);

                    main.ColumnCount = 2;
                    main.RowCount = 2;
                    main.ColumnStyles.Clear();
                    main.RowStyles.Clear();
                    main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
                    main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                    main.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));

                    foreach (var b in _navButtons) { b.Width = 260; b.Height = 48; }
                    _nav.WrapContents = false; _nav.FlowDirection = FlowDirection.TopDown;

                    main.Controls.Add(_nav, 0, 0);
                    main.Controls.Add(_content, 1, 0);
                    main.Controls.Add(_bottom, 0, 1);
                    main.SetColumnSpan(_bottom, 2);
                }
            }
            main.ResumeLayout();
        }

        private Button MkNavButton(string text, int index)
        {
            var b = new Button
            {
                Text = text,
                Tag = index,
                Height = 48,
                Width = 260,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(16, 0, 12, 0),
                BackColor = Theme.Nav,
                ForeColor = Theme.Text,
                TabStop = true,
                Margin = new Padding(0, 8, 0, 0),
                UseVisualStyleBackColor = false
            };
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Theme.Border;
            b.Click += (_, __) => SetNavSelected(index);
            b.Paint += (s, e) =>
            {
                bool sel = (int)b.Tag == _selNav;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.None;

                var rect = new Rectangle(0, 0, b.Width - 1, b.Height - 1);
                using var bg = new SolidBrush(sel ? Theme.PanelAlt : Theme.Nav);
                using var pen = new Pen(Theme.Border);
                g.FillRectangle(bg, rect);
                g.DrawRectangle(pen, rect);

                var font = sel ? new Font("Segoe UI Semibold", 11f) : new Font("Segoe UI", 10.5f);
                TextRenderer.DrawText(g, b.Text, font, new Rectangle(16, 0, b.Width - 32, b.Height),
                    Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };
            return b;
        }

        private static RadioButton MkRadio(string text) =>
            new() { Text = text, AutoSize = true, ForeColor = Theme.Text, BackColor = Color.Transparent, Font = new Font("Segoe UI", 11f) };
        private static CheckBox MkCheck(string text) =>
            new() { Text = text, AutoSize = true, ForeColor = Theme.Text, BackColor = Color.Transparent, Font = new Font("Segoe UI", 11f) };
        private static ComboBox MkPresetCombo() =>
            new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.System, Width = 320, Font = new Font("Segoe UI", 10.5f) };

        private static void FillPresetCombo(ComboBox cb)
        {
            cb.Items.Clear();
            cb.Items.Add("Default (renderer)");
            cb.Items.Add("Profile 1");
            cb.Items.Add("Profile 2");
            cb.Items.Add("Profile 3");
            cb.Items.Add("Profile 4");
            cb.Items.Add("Profile 5");
            cb.Items.Add("Profile 6");
            cb.SelectedIndex = 0;
        }

        private static MadVrCategoryPreset GetPresetFromCombo(ComboBox cb) =>
            cb.SelectedIndex switch
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

        private void EnsureGeneralPage()
        {
            if (_pgGeneral != null) return;

            var page = new Panel { BackColor = Theme.Panel, Dock = DockStyle.Fill, Padding = new Padding(6) };
            var stack = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Theme.Panel,
                Padding = new Padding(6)
            };

            UiKit.CardPanel Card(string title)
            {
                var card = new UiKit.CardPanel { Width = _content.Width - 48, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
                var header = new Label
                {
                    Text = title,
                    Font = new Font("Segoe UI Semibold", 13.5f),
                    ForeColor = Theme.Text,
                    AutoSize = true,
                    Margin = new Padding(0, 0, 0, 12)
                };
                card.Controls.Add(header);
                return card;
            }

            // Frequenza monitor
            var cardFps = Card("Frequenza monitor");
            var pnlFps = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Top, BackColor = Color.Transparent, Padding = new Padding(2) };
            _fpsAuto = MkRadio("Non cambiare (usa frequenza attuale)"); _fpsAuto.Checked = true;
            _fps60 = MkRadio("59/60p (desktop/sport)");
            _fps24 = MkRadio("23/24p (film)");
            pnlFps.Controls.AddRange(new Control[] { _fpsAuto, _fps60, _fps24 });
            cardFps.Controls.Add(pnlFps);

            // HDR
            var cardHdr = Card("madVR — HDR");
            var pnlHdr = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Top, BackColor = Color.Transparent, Padding = new Padding(2) };
            _hdrAuto = MkRadio("Auto"); _hdrAuto.Checked = true;
            _hdrPass = MkRadio("Passthrough HDR al display");
            _hdrTone = MkRadio("Forza conversione HDR → SDR (tone mapping)");
            _hdrLut = MkRadio("Forza HDR → SDR via 3D LUT esterna");
            pnlHdr.Controls.AddRange(new Control[] { _hdrAuto, _hdrPass, _hdrTone, _hdrLut });
            cardHdr.Controls.Add(pnlHdr);

            // Algoritmi
            var cardAlgo = Card("Algoritmi madVR");
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                AutoSize = true,
                BackColor = Color.Transparent,
                Padding = new Padding(2)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Label L(string t) => new()
            {
                Text = t,
                ForeColor = Theme.SubtleText,
                Font = new Font("Segoe UI", 11f),
                AutoSize = true,
                Margin = new Padding(0, 8, 16, 8)
            };

            _cbChroma = MkPresetCombo(); FillPresetCombo(_cbChroma);
            _cbUp = MkPresetCombo(); FillPresetCombo(_cbUp);
            _cbDown = MkPresetCombo(); FillPresetCombo(_cbDown);
            _cbRefine = MkPresetCombo(); FillPresetCombo(_cbRefine);

            grid.Controls.Add(L("Chroma upscaling:"), 0, 0); grid.Controls.Add(_cbChroma, 1, 0);
            grid.Controls.Add(L("Image upscaling:"), 0, 1); grid.Controls.Add(_cbUp, 1, 1);
            grid.Controls.Add(L("Image downscaling:"), 0, 2); grid.Controls.Add(_cbDown, 1, 2);
            grid.Controls.Add(L("Upscaling refinement:"), 0, 3); grid.Controls.Add(_cbRefine, 1, 3);
            cardAlgo.Controls.Add(grid);

            // Video (player)
            var cardVideo = Card("Video (player)");
            _upscale = MkCheck("Abilita upscaling lato player (non madVR)");
            _upscale.Margin = new Padding(2, 4, 2, 2);
            cardVideo.Controls.Add(_upscale);

            // Audio
            var cardAudio = Card("Audio");
            _preferBitstream = MkCheck("Preferisci bitstream se il dispositivo supporta passthrough");
            _preferBitstream.Margin = new Padding(2, 4, 2, 2);
            cardAudio.Controls.Add(_preferBitstream);

            stack.Controls.Add(cardFps);
            stack.Controls.Add(cardHdr);
            stack.Controls.Add(cardAlgo);
            stack.Controls.Add(cardVideo);
            stack.Controls.Add(cardAudio);

            page.Controls.Add(stack);
            _pgGeneral = page;
            AddPageToContent(page);
        }

        private void EnsureMadvrPage()
        {
            if (_pgMadvr != null) return;
            var host = new MadVrSettingsEmbedder { Dock = DockStyle.Fill, BackColor = Theme.Panel, Padding = new Padding(12) };
            _pgMadvr = host;
            AddPageToContent(host);
        }

        private void EnsureLavAudioPage()
        {
            if (_pgLavAudio != null) return;
            _pgLavAudio = NewHost("LAV Audio Decoder", "LAV Audio non trovato.");
            AddPageToContent(_pgLavAudio);
        }

        private void EnsureLavVideoPage()
        {
            if (_pgLavVideo != null) return;
            _pgLavVideo = NewHost("LAV Video Decoder", "LAV Video non trovato.");
            AddPageToContent(_pgLavVideo);
        }

        private void EnsureMpcVrPage()
        {
            if (_pgMpcVr != null) return;

            var host = new DsPropPageHost { Padding = new Padding(12) };
            bool loaded = false;
            try { host.LoadFromClsid(CLSID_MPCVR); loaded = true; } catch { }
            if (!loaded) { try { host.LoadFromFriendlyName("MPC Video Renderer"); loaded = true; } catch { } }
            if (!loaded)
            {
                var p = new Panel { BackColor = Theme.Panel, Dock = DockStyle.Fill, Padding = new Padding(16) };
                p.Controls.Add(new Label
                {
                    Text = "MPC Video Renderer non trovato.",
                    ForeColor = Color.FromArgb(230, 80, 80),
                    Dock = DockStyle.Fill
                });
                host.Controls.Add(p);
            }
            _pgMpcVr = host;
            AddPageToContent(_pgMpcVr);
        }

        private void EnsureMpcArPage()
        {
            if (_pgMpcAr != null) return;
            _pgMpcAr = NewHost("MPC Audio Renderer", "MPC Audio Renderer non trovato.");
            AddPageToContent(_pgMpcAr);
        }

        private DsPropPageHost NewHost(string friendly, string fallback)
        {
            var host = new DsPropPageHost { Dock = DockStyle.Fill, Padding = new Padding(12) };
            try { host.LoadFromFriendlyName(friendly); }
            catch (Exception ex)
            {
                var p = new Panel { BackColor = Theme.Panel, Dock = DockStyle.Fill, Padding = new Padding(16) };
                var lbl = new Label
                {
                    Text = $"{fallback}\r\nDettagli: {ex.Message}",
                    ForeColor = Color.FromArgb(230, 80, 80),
                    Dock = DockStyle.Fill
                };
                p.Controls.Add(lbl);
                host.Controls.Add(p);
            }
            return host;
        }

        private void AddPageToContent(Control page)
        {
            page.Visible = false;
            page.Dock = DockStyle.Fill;
            _content.Controls.Add(page);
        }

        private void SetNavSelected(int index)
        {
            if (index < 0 || index >= _navItems.Length) index = 0;
            _selNav = index;
            foreach (var btn in _navButtons) btn.Invalidate();
            ShowPage(index);
            UpdateApplyVisibility(); // <<< SOLO Generali mostra "Applica"
        }

        private void ShowPage(int index)
        {
            foreach (Control c in _content.Controls) c.Visible = false;
            Control? toShow = null;
            switch (index)
            {
                case 0: EnsureGeneralPage(); toShow = _pgGeneral!; break;
                case 1:
                    EnsureMadvrPage();
                    toShow = _pgMadvr!;
                    // Sempre "poke": se la finestra madVR è stata chiusa o persa, riapri
                    if (_pgMadvr is MadVrSettingsEmbedder m) m.EnsureStarted();
                    break;
                case 2: EnsureLavAudioPage(); toShow = _pgLavAudio!; break;
                case 3: EnsureLavVideoPage(); toShow = _pgLavVideo!; break;
                case 4: EnsureMpcVrPage(); toShow = _pgMpcVr!; break;
                case 5: EnsureMpcArPage(); toShow = _pgMpcAr!; break;
                default: EnsureGeneralPage(); toShow = _pgGeneral!; break;
            }
            if (toShow != null) toShow.Visible = true;
            _content.Invalidate();
        }

        private void UpdateApplyVisibility()
        {
            // "Applica" solo in Generali
            bool showApply = (_selNav == 0);
            _btnApply.Visible = showApply;
        }

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

            vs.ChromaPreset = GetPresetFromCombo(_cbChroma);
            vs.ImageUpscalePreset = GetPresetFromCombo(_cbUp);
            vs.ImageDownscalePreset = GetPresetFromCombo(_cbDown);
            vs.RefinementPreset = GetPresetFromCombo(_cbRefine);
            return vs;
        }

        public void SyncFromState(int targetFps, bool upscale, bool preferBitstream)
        {
            EnsureGeneralPage();
            _fpsAuto.Checked = (targetFps == 0);
            _fps60.Checked = (targetFps == 60);
            _fps24.Checked = (targetFps == 24 || targetFps == 23);
            _upscale.Checked = upscale;
            _preferBitstream.Checked = preferBitstream;
        }

        public void EnsureHostsLoaded()
        {
            EnsureGeneralPage();
            EnsureLavAudioPage();
            EnsureLavVideoPage();
            EnsureMpcVrPage();
            EnsureMpcArPage();
        }

        public void FocusApply() { try { _btnApply.Focus(); } catch { } }
    }

    // ===================== CREDITS MODAL (squadrata, tipografia ampia, layout a card) =====================
    internal sealed class CreditsModal : ModalBaseV2
    {
        private Button _close;
        private Panel _scroll;
        private FlowLayoutPanel _stack;

        public CreditsModal()
        {
            Title = "Crediti";

            _close = UiKit.MakeGhost("Chiudi");
            _close.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            _close.Click += (_, __) => RaiseClose();

            _scroll = new Panel { AutoScroll = true, BackColor = Theme.Panel, Dock = DockStyle.Fill, Padding = new Padding(18) };
            BodyPanel.Controls.Add(_scroll);

            _stack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                BackColor = Theme.Panel,
                Padding = new Padding(0)
            };
            _scroll.Controls.Add(_stack);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 72, BackColor = Theme.Panel, Padding = new Padding(18, 14, 18, 14) };
            bottom.Controls.Add(_close);
            bottom.Resize += (_, __) => _close.Location = new Point(bottom.Width - _close.Width - 18, (bottom.Height - _close.Height) / 2);
            BodyPanel.Controls.Add(bottom);

            BuildContent();
            _scroll.SizeChanged += (_, __) => UpdateCardWidths();
        }

        private void BuildContent()
        {
            _stack.SuspendLayout();
            _stack.Controls.Clear();

            UiKit.CardPanel Card(string title, string? subtitle = null)
            {
                var card = new UiKit.CardPanel { Width = _scroll.ClientSize.Width - 48, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
                var header = new Label
                {
                    Text = title,
                    Font = new Font("Segoe UI Semibold", 14.5f),
                    ForeColor = Theme.Text,
                    AutoSize = true
                };
                card.Controls.Add(header);
                if (!string.IsNullOrWhiteSpace(subtitle))
                {
                    var sub = new Label
                    {
                        Text = subtitle!,
                        Font = new Font("Segoe UI", 11f),
                        ForeColor = Theme.SubtleText,
                        AutoSize = true,
                        Margin = new Padding(0, 4, 0, 12)
                    };
                    card.Controls.Add(sub);
                }
                return card;
            }

            void AddParagraph(Control parent, string text)
            {
                var lbl = new Label
                {
                    Text = text,
                    Font = new Font("Segoe UI", 11.5f),
                    ForeColor = Theme.SubtleText,
                    AutoSize = true,
                    MaximumSize = new Size(_scroll.ClientSize.Width - 120, 0),
                    Margin = new Padding(0, 6, 0, 0)
                };
                parent.Controls.Add(lbl);
            }

            Control AddLink(Control parent, string text, string url)
            {
                var l = new LinkLabel
                {
                    Text = text,
                    Font = new Font("Segoe UI", 11.5f),
                    AutoSize = true,
                    LinkColor = Theme.Accent,
                    ActiveLinkColor = Theme.Accent,
                    Margin = new Padding(0, 6, 0, 0)
                };
                l.Links.Add(0, text.Length, url);
                l.LinkClicked += (_, e) =>
                {
                    try { Process.Start(new ProcessStartInfo { FileName = e.Link.LinkData?.ToString(), UseShellExecute = true }); }
                    catch { }
                };
                parent.Controls.Add(l);
                return l;
            }

            // Card: Progetto
            var cProj = Card("Cinecore Player 2025", "Progetto non commerciale");
            AddParagraph(cProj, "© 2025 — Niccolò Landolfi.");
            AddParagraph(cProj, "Video: DirectShow + LAV + madVR / MPCVR / EVR");
            AddParagraph(cProj, "Audio: LAV + renderer di sistema / MPC Audio Renderer");
            _stack.Controls.Add(cProj);

            // Card: Componenti
            var cComp = Card("Componenti principali");
            AddParagraph(cComp, "• LAV Filters");
            AddParagraph(cComp, "• madVR (video renderer)");
            AddParagraph(cComp, "• MPC Video Renderer");
            AddParagraph(cComp, "• MPC Audio Renderer");
            AddParagraph(cComp, "• DirectShowLib, FFmpeg.AutoGen");
            _stack.Controls.Add(cComp);

            // Card: Ringraziamenti
            var cThanks = Card("Ringraziamenti");
            AddParagraph(cThanks, "• LAV Filters, MPC-HC team");
            AddParagraph(cThanks, "• madshi (madVR)");
            _stack.Controls.Add(cThanks);

            // Card: Link utili
            var cLinks = Card("Link utili");
            AddLink(cLinks, "Sito Cinecore", "https://cinecore.it");
            AddLink(cLinks, "Repository (se pubblico)", "https://github.com/");
            _stack.Controls.Add(cLinks);

            _stack.ResumeLayout();
            UpdateCardWidths();
        }

        private void UpdateCardWidths()
        {
            int w = Math.Max(280, _scroll.ClientSize.Width - 48);
            foreach (Control c in _stack.Controls)
            {
                if (c is UiKit.CardPanel cp)
                    cp.Width = w;
            }
        }
    }

    // ===================== DisplayModeSwitcher (invariato) =====================
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, int dwFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);

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
                if (!EnumDisplaySettingsEx(_device, ENUM_CURRENT_SETTINGS, ref cur, 0)) return false;
                _original = cur;

                var best = cur;
                int target = (desiredFps <= 25) ? 24 : 60;
                int alt = (target == 24) ? 23 : 59;
                int bestHz = cur.dmDisplayFrequency;
                int bestDelta = int.MaxValue;

                DEVMODE mode = new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                for (int i = 0; EnumDisplaySettingsEx(_device, i, ref mode, EDS_RAWMODE); i++)
                {
                    if (mode.dmPelsWidth != cur.dmPelsWidth || mode.dmPelsHeight != cur.dmPelsHeight) continue;
                    int hz = mode.dmDisplayFrequency;
                    if (hz <= 0) continue;
                    int delta = Math.Min(Math.Abs(hz - target), Math.Abs(hz - alt));
                    if (delta < bestDelta) { bestDelta = delta; best = mode; bestHz = hz; }
                }

                if (bestHz == cur.dmDisplayFrequency) return true;
                int r = ChangeDisplaySettingsEx(_device, ref best, IntPtr.Zero, CDS_FULLSCREEN | CDS_UPDATEREGISTRY, IntPtr.Zero);
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
                ChangeDisplaySettingsEx(_device, ref orig, IntPtr.Zero, CDS_FULLSCREEN | CDS_UPDATEREGISTRY, IntPtr.Zero);
            }
            catch { }
            finally { _original = null; _device = null; }
        }
    }

    // ===================== Win32 helpers =====================
    internal static class Win32
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_SHOWWINDOW = 0x0040;
    }
}
