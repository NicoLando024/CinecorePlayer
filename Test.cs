// AudioMetersOverlay.cs
#nullable enable
using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CinecorePlayer2025
{
    /// <summary>
    /// Overlay SOLO AUDIO: VU (RMS/Peak con peak-hold), Correlation/Phase, Spectrum FFT, LUFS (M/S approx).
    /// Sicuro ai gradient con rettangoli nulli, e non blocca il mouse (click-through).
    /// API:
    ///   - PushPcm(float[] interleaved, int channels, int sampleRate)
    ///   - SetBitstreamMode(bool on)
    ///   - Clear()
    /// </summary>
    public sealed class AudioMetersOverlay : Control
    {
        // ===== Opzioni UI =====
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool ShowVu { get; set; } = true;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool ShowCorrelation { get; set; } = true;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool ShowSpectrum { get; set; } = true;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool ShowLoudness { get; set; } = true;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool ClickThrough { get; set; } = true;

        // Tema
        private readonly Color _txt = Color.FromArgb(234, 234, 234);
        private readonly Color _txtDim = Color.FromArgb(168, 168, 168);
        private readonly Color _cardBg = Color.FromArgb(42, 18, 18, 20);
        private readonly Color _cardBrd = Color.FromArgb(70, 255, 255, 255);

        // ===== Stato audio =====
        private volatile int _sampleRate = 48000;
        private volatile int _channels = 2;
        private volatile bool _bitstreamActive;
        private DateTime _lastPushUtc = DateTime.MinValue;

        // Buffer circolare L/R (max ~4s @48k)
        private const int MaxSeconds = 4;
        private float[] _bufL = new float[48000 * MaxSeconds];
        private float[] _bufR = new float[48000 * MaxSeconds];
        private int _wr, _len;
        private readonly object _lock = new();

        // Peak/RMS + hold
        private double _rmsL, _rmsR;         // lin (smoothed)
        private double _peakL, _peakR;       // lin
        private double _holdL = double.NaN;  // dBFS
        private double _holdR = double.NaN;  // dBFS
        private DateTime _holdSinceL, _holdSinceR;

        // Correlation (−1..+1)
        private double _corr = 1.0;

        // LUFS approx (K-weight: HP 38 Hz + HS +4 dB @1.5 kHz)
        private double _lufsM = double.NaN, _lufsS = double.NaN;
        private Biquad _kHpL = new(), _kHpR = new(), _kHsL = new(), _kHsR = new();
        private int _kFs;

        // FFT
        private readonly object _fftLock = new();
        private float[] _fftIn = new float[4096];
        private double[] _fftDb = new double[2048];
        private int _fftSize = 4096;

        // UI timer
        private readonly Timer _tick;

        public AudioMetersOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;

            _tick = new Timer { Interval = 33 }; // ~30 fps
            _tick.Tick += (_, __) => { TryUpdateMeters(); Invalidate(); };
            _tick.Start();
        }

        // ===== API =====
        public void PushPcm(float[] interleaved, int channels, int sampleRate)
        {
            if (interleaved == null || interleaved.Length == 0) return;
            channels = Math.Clamp(channels, 1, 8);
            sampleRate = Math.Clamp(sampleRate, 8000, 384000);

            int cap = sampleRate * MaxSeconds;
            lock (_lock)
            {
                if (_bufL.Length != cap)
                {
                    _bufL = new float[cap];
                    _bufR = new float[cap];
                    _wr = 0; _len = 0;
                }

                _sampleRate = sampleRate;
                _channels = channels;

                int frames = interleaved.Length / channels;
                for (int i = 0; i < frames; i++)
                {
                    float l = interleaved[i * channels + 0];
                    float r = channels > 1 ? interleaved[i * channels + 1] : l;
                    _bufL[_wr] = l;
                    _bufR[_wr] = r;
                    _wr = (_wr + 1) % cap;
                    if (_len < cap) _len++;
                }
                _lastPushUtc = DateTime.UtcNow;
            }
        }

        public void SetBitstreamMode(bool on) => _bitstreamActive = on;

        public void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_bufL, 0, _bufL.Length);
                Array.Clear(_bufR, 0, _bufR.Length);
                _wr = 0; _len = 0;
            }
            _rmsL = _rmsR = 0; _peakL = _peakR = 0; _holdL = _holdR = double.NaN; _corr = 1.0;
            _lufsM = _lufsS = double.NaN;
            _lastPushUtc = DateTime.MinValue;
        }

        // ===== Rendering =====
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var key = this.FindForm()?.TransparencyKey ?? Color.Black;
            e.Graphics.Clear(key);
        }
        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84; const int HTTRANSPARENT = -1;
            if (ClickThrough && m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            int pad = 12;
            var card = new Rectangle(pad, pad, Math.Max(60, Width - pad * 2), Math.Max(60, Height - pad * 2));
            using (var bg = new SolidBrush(_cardBg)) g.FillRoundedRectangle(bg, card, new Size(12, 12));
            using (var p = new Pen(_cardBrd, 1)) g.DrawRoundedRectangle(p, card, new Size(12, 12));

            // Layout 2x2: [VU | Correlation+LUFS] on top, [Spectrum] bottom full width
            int gap = 10;
            int topH = Math.Max(90, (int)(card.Height * 0.45));
            var rcTop = new Rectangle(card.X + gap, card.Y + gap, card.Width - gap * 2, topH - gap);
            var rcBot = new Rectangle(card.X + gap, rcTop.Bottom + gap, card.Width - gap * 2, card.Bottom - gap - (rcTop.Bottom + gap));

            int colW = (rcTop.Width - gap) / 2;
            var rcVu = new Rectangle(rcTop.X, rcTop.Y, colW, rcTop.Height);
            var rcRight = new Rectangle(rcVu.Right + gap, rcTop.Y, rcTop.Right - (rcVu.Right + gap), rcTop.Height);

            // Stato input
            bool noPcm = !_bitstreamActive && (DateTime.UtcNow - _lastPushUtc).TotalMilliseconds > 800;
            if (_bitstreamActive)
            {
                DrawBadge(g, rcTop, "Bitstream attivo", "Nessun PCM disponibile per i meter");
            }
            else if (noPcm)
            {
                DrawBadge(g, rcTop, "In attesa di PCM…", "Collega la tap dal decoder/renderer");
            }

            if (ShowVu) DrawVu(g, rcVu);
            if (ShowCorrelation) DrawCorrelationAndGoniometer(g, rcRight);
            if (ShowLoudness) DrawLoudnessBadges(g, rcRight);

            if (ShowSpectrum) DrawSpectrum(g, rcBot);
        }

        private void DrawBadge(Graphics g, Rectangle rc, string title, string? sub)
        {
            using var f1 = new Font("Segoe UI Semibold", 12f);
            using var f2 = new Font("Segoe UI", 9.25f);
            var y = rc.Y + 4;
            TextRenderer.DrawText(g, title, f1, new Point(rc.X + 4, y), _txt);
            if (!string.IsNullOrWhiteSpace(sub))
                TextRenderer.DrawText(g, sub!, f2, new Point(rc.X + 4, y + f1.Height + 2), _txtDim);
        }

        // ===== VU (RMS/Peak con hold) =====
        private void DrawVu(Graphics g, Rectangle rc)
        {
            using var f = new Font("Segoe UI", 9.25f, FontStyle.Bold);
            TextRenderer.DrawText(g, "VU / Peak", f, new Point(rc.X, rc.Y - 2), _txt);

            int top = rc.Y + 14;
            int h = Math.Max(24, rc.Height - 20);
            int bw = Math.Max(20, Math.Min(28, rc.Width / 8));
            int gap = Math.Max(8, bw / 2);

            var rcL = new Rectangle(rc.X, top, bw, h);
            var rcR = new Rectangle(rcL.Right + gap, top, bw, h);

            DrawVuBar(g, rcL, (float)_rmsL, (float)_peakL, (float)_holdL, "L");
            DrawVuBar(g, rcR, (float)_rmsR, (float)_peakR, (float)_holdR, "R");
        }

        private void DrawVuBar(Graphics g, Rectangle rc, float rmsLin, float peakLin, float peakHoldDb, string label)
        {
            // Background e bordo
            using (var p = new Pen(Color.FromArgb(80, 255, 255, 255))) g.DrawRectangle(p, rc);

            float ToY(float lin)
            {
                if (lin <= 0) return rc.Bottom;
                double db = 20.0 * Math.Log10(Math.Max(1e-6, lin)); // dBFS
                db = Math.Clamp(db, -60, 0);
                double t = (db + 60.0) / 60.0;
                return rc.Bottom - (float)(t * rc.Height);
            }

            // RMS fill (safe)
            int y = Math.Min(rc.Bottom - 1, (int)Math.Round(ToY(rmsLin)));
            int h = Math.Max(0, rc.Bottom - y);
            if (h > 0 && rc.Width > 1)
            {
                var fill = new Rectangle(rc.X + 1, y, rc.Width - 2, h);
                using var lg = SafeGradient(fill, Color.Cyan, Color.Magenta, 90f);
                if (lg != null) g.FillRectangle(lg, fill);
            }

            // Peak line
            float yPk = ToY(peakLin);
            using (var pen = new Pen(Color.White, 2)) g.DrawLine(pen, rc.X, yPk, rc.Right, yPk);

            // Peak-hold marker
            double dBH = double.IsNaN(peakHoldDb) ? -60 : Math.Clamp(peakHoldDb, -60, 0);
            float yH = ToY((float)Math.Pow(10.0, dBH / 20.0));
            var tri = new[] { new PointF(rc.Right + 3, yH), new PointF(rc.Right + 11, yH - 5), new PointF(rc.Right + 11, yH + 5) };
            using (var b = new SolidBrush(Color.FromArgb(220, 255, 255, 255))) g.FillPolygon(b, tri);

            // Label + dB
            using var fLbl = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            TextRenderer.DrawText(g, label, fLbl, new Point(rc.X, rc.Bottom + 2), _txtDim);

            using var fDb = new Font("Segoe UI", 8f);
            string dbTxt = $"{ToDb(peakLin):0.0} dBFS";
            var sz = TextRenderer.MeasureText(dbTxt, fDb);
            TextRenderer.DrawText(g, dbTxt, fDb, new Point(rc.Right - sz.Width, rc.Y - 2), _txtDim, TextFormatFlags.NoPadding);
        }

        // ===== Correlation + Goniometer =====
        private void DrawCorrelationAndGoniometer(Graphics g, Rectangle rc)
        {
            using var f = new Font("Segoe UI", 9.25f, FontStyle.Bold);
            TextRenderer.DrawText(g, "Correlation / Phase", f, new Point(rc.X, rc.Y - 2), _txt);

            int top = rc.Y + 14;
            int meterH = Math.Min(36, Math.Max(24, rc.Height / 5));
            var mRc = new Rectangle(rc.X, top, rc.Width, meterH);

            using (var b = new SolidBrush(Color.FromArgb(28, 255, 255, 255))) g.FillRectangle(b, mRc);
            using (var p = new Pen(Color.FromArgb(80, 255, 255, 255))) g.DrawRectangle(p, mRc);

            int cx = mRc.X + mRc.Width / 2;
            using (var p0 = new Pen(Color.FromArgb(120, 255, 255, 255))) g.DrawLine(p0, cx, mRc.Top, cx, mRc.Bottom);

            double t = Math.Clamp((_corr + 1.0) / 2.0, 0, 1);
            int w = (int)Math.Round(t * mRc.Width);
            var bar = new Rectangle(mRc.X, mRc.Y, Math.Max(1, w), mRc.Height);
            using var lg = SafeGradient(bar, Color.Magenta, Color.Cyan, 0f);
            if (lg != null) g.FillRectangle(lg, bar);

            using var fVal = new Font("Segoe UI", 9f);
            string s = _corr.ToString("+0.00;-0.00");
            var ssz = TextRenderer.MeasureText(s, fVal);
            TextRenderer.DrawText(g, s, fVal, new Point(mRc.Right - ssz.Width - 4, mRc.Bottom + 2), _txtDim);

            // Goniometro
            int gy = mRc.Bottom + 10;
            var gon = new Rectangle(rc.X, gy, rc.Width, Math.Max(40, rc.Bottom - gy));
            using (var p = new Pen(Color.FromArgb(80, 255, 255, 255))) g.DrawRectangle(p, gon);

            Span<PointF> pts = stackalloc PointF[256];
            int n = ReadLatestXY(pts);
            if (n > 1)
            {
                using var pen = new Pen(Color.FromArgb(160, 255, 255, 255), 1f);
                for (int i = 1; i < n; i++)
                {
                    float x = (pts[i].X * 0.5f + 0.5f);
                    float y = (pts[i].Y * -0.5f + 0.5f);
                    float px = (pts[i - 1].X * 0.5f + 0.5f);
                    float py = (pts[i - 1].Y * -0.5f + 0.5f);
                    g.DrawLine(pen,
                        gon.X + px * gon.Width, gon.Y + py * gon.Height,
                        gon.X + x * gon.Width, gon.Y + y * gon.Height);
                }
            }
        }

        // ===== LUFS badges =====
        private void DrawLoudnessBadges(Graphics g, Rectangle rcRight)
        {
            // Area a destra in alto
            int w = 92, h = 22, gap = 6;
            int x = rcRight.Right - w;
            int y = rcRight.Y + 2;

            DrawTag(g, new Rectangle(x, y, w, h), "LUFS-M", double.IsNaN(_lufsM) ? "n/d" : $"{_lufsM:0.0}");
            y += h + gap;
            DrawTag(g, new Rectangle(x, y, w, h), "LUFS-S", double.IsNaN(_lufsS) ? "n/d" : $"{_lufsS:0.0}");
        }

        private void DrawTag(Graphics g, Rectangle rc, string key, string val)
        {
            int rr = rc.Height / 2;
            using (var b = new SolidBrush(Color.FromArgb(30, 255, 255, 255))) g.FillRoundedRectangle(b, rc, new Size(rr, rr));
            using (var p = new Pen(Color.FromArgb(80, 255, 255, 255))) g.DrawRoundedRectangle(p, rc, new Size(rr, rr));
            using var fk = new Font("Segoe UI", 8.75f, FontStyle.Bold);
            using var fv = new Font("Segoe UI", 8.75f);
            TextRenderer.DrawText(g, key, fk, new Rectangle(rc.X + 8, rc.Y + 2, rc.Width / 2, rc.Height), _txt, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            var vsz = TextRenderer.MeasureText(val, fv);
            TextRenderer.DrawText(g, val, fv, new Point(rc.Right - vsz.Width - 8, rc.Y + 2), _txtDim, TextFormatFlags.NoPadding);
        }

        // ===== Spectrum =====
        private void DrawSpectrum(Graphics g, Rectangle rc)
        {
            using var f = new Font("Segoe UI", 9.25f, FontStyle.Bold);
            TextRenderer.DrawText(g, "Spectrum", f, new Point(rc.X, rc.Y - 2), _txt);

            var plot = new Rectangle(rc.X, rc.Y + 12, rc.Width, rc.Height - 14);
            if (plot.Width <= 4 || plot.Height <= 2) return;

            using (var b = new SolidBrush(Color.FromArgb(20, 255, 255, 255))) g.FillRectangle(b, plot);
            using (var p = new Pen(Color.FromArgb(80, 255, 255, 255))) g.DrawRectangle(p, plot);

            double[] mag;
            lock (_fftLock) mag = (double[])_fftDb.Clone();
            int bins = mag.Length;
            if (bins <= 8) return;

            int bars = Math.Max(20, Math.Min(160, plot.Width / 4));
            int barW = Math.Max(2, plot.Width / bars);
            double fMin = 20, fMax = Math.Min(_sampleRate / 2.0, 22000);

            for (int i = 0; i < bars; i++)
            {
                double frac = i / (double)(bars - 1);
                double fLo = Math.Exp(Math.Log(fMin) + frac * (Math.Log(fMax) - Math.Log(fMin)));
                double fHi = Math.Exp(Math.Log(fMin) + (i + 1.0) / bars * (Math.Log(fMax) - Math.Log(fMin)));

                int iLo = (int)Math.Clamp(Math.Floor(fLo / (_sampleRate / (double)_fftSize)), 1, bins - 1);
                int iHi = (int)Math.Clamp(Math.Ceiling(fHi / (_sampleRate / (double)_fftSize)), iLo + 1, bins);

                double m = -120;
                for (int k = iLo; k < iHi; k++) m = Math.Max(m, mag[k]);

                double t = Math.Clamp((m + 96) / 96.0, 0, 1); // −96..0 dB → 0..1
                int h = (int)Math.Round(t * Math.Max(0, plot.Height - 2));
                if (h <= 0) continue;

                var r = new Rectangle(plot.X + i * barW + 1, plot.Bottom - h - 1, Math.Max(1, barW - 2), h);
                using var lg = SafeGradient(r, Color.Cyan, Color.Magenta, 90f);
                if (lg != null) g.FillRectangle(lg, r);
            }
        }

        // ===== Update misure =====
        private void TryUpdateMeters()
        {
            int sr, frames;
            float[] l, r;
            lock (_lock)
            {
                if (_len <= 0) return;
                sr = _sampleRate;
                frames = Math.Min(_len, Math.Min(_bufL.Length, sr)); // ~1s di finestra
                l = new float[frames]; r = new float[frames];
                int cap = _bufL.Length;
                int start = (_wr - frames + cap) % cap;
                int head = Math.Min(frames, cap - start);
                Array.Copy(_bufL, start, l, 0, head);
                Array.Copy(_bufR, start, r, 0, head);
                if (head < frames)
                {
                    Array.Copy(_bufL, 0, l, head, frames - head);
                    Array.Copy(_bufR, 0, r, head, frames - head);
                }
            }

            // VU: RMS su ~300ms, peak + hold
            int w300 = Math.Max(1, (int)(sr * 0.30));
            double sumL = 0, sumR = 0, pkL = 0, pkR = 0;
            for (int i = Math.Max(0, l.Length - w300); i < l.Length; i++)
            {
                float L = l[i], R = r[i];
                sumL += L * L; sumR += R * R;
                pkL = Math.Max(pkL, Math.Abs(L));
                pkR = Math.Max(pkR, Math.Abs(R));
            }
            double rmsL = Math.Sqrt(sumL / Math.Max(1, Math.Min(w300, l.Length)));
            double rmsR = Math.Sqrt(sumR / Math.Max(1, Math.Min(w300, r.Length)));
            _rmsL = Ballistics(_rmsL, rmsL, 50, 350, sr);
            _rmsR = Ballistics(_rmsR, rmsR, 50, 350, sr);

            _peakL = Math.Max(pkL, _peakL * 0.95);
            _peakR = Math.Max(pkR, _peakR * 0.95);

            double dBL = ToDb(_peakL); if (double.IsNegativeInfinity(dBL)) dBL = -120;
            double dBR = ToDb(_peakR); if (double.IsNegativeInfinity(dBR)) dBR = -120;
            if (double.IsNaN(_holdL) || dBL >= _holdL - 0.1) { _holdL = dBL; _holdSinceL = DateTime.UtcNow; }
            if (double.IsNaN(_holdR) || dBR >= _holdR - 0.1) { _holdR = dBR; _holdSinceR = DateTime.UtcNow; }
            if (_holdSinceL != DateTime.MinValue && (DateTime.UtcNow - _holdSinceL).TotalSeconds > 2) _holdL -= 0.7;
            if (_holdSinceR != DateTime.MinValue && (DateTime.UtcNow - _holdSinceR).TotalSeconds > 2) _holdR -= 0.7;

            // Correlation su ~25ms
            int w25 = Math.Max(16, (int)(sr * 0.025));
            double sLL = 0, sRR = 0, sLR = 0;
            for (int i = Math.Max(0, l.Length - w25); i < l.Length; i++)
            { double L = l[i], R = r[i]; sLL += L * L; sRR += R * R; sLR += L * R; }
            double denom = Math.Sqrt(sLL * sRR);
            _corr = denom > 1e-9 ? Math.Clamp(sLR / denom, -1.0, 1.0) : 0.0;

            // K-weight + LUFS (M=400ms, S=3s)
            EnsureKWeight(sr);
            int mWin = Math.Max(1, (int)(sr * 0.400));
            int sWin = Math.Max(mWin + 1, (int)(sr * 3.0));
            double km = 0, ks = 0; int mc = 0, sc = 0;
            for (int i = Math.Max(0, l.Length - sWin); i < l.Length; i++)
            {
                double xL = _kHsL.Process(_kHpL.Process(l[i]));
                double xR = _kHsR.Process(_kHpR.Process(r[i]));
                double x = 0.5 * (xL + xR);
                double x2 = x * x;
                ks += x2; sc++;
                if (i >= l.Length - mWin) { km += x2; mc++; }
            }
            if (mc > 0) _lufsM = -0.691 + 10.0 * Math.Log10(km / mc + 1e-12);
            if (sc > 0) _lufsS = -0.691 + 10.0 * Math.Log10(ks / sc + 1e-12);

            // FFT (mono, Hann)
            int nFft = Math.Min(_fftSize, l.Length & ~1);
            if (nFft >= 512)
            {
                lock (_fftLock)
                {
                    if (_fftIn.Length != nFft) _fftIn = new float[nFft];
                    int start = l.Length - nFft;
                    for (int i = 0; i < nFft; i++)
                    {
                        float w = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (nFft - 1)));
                        _fftIn[i] = ((l[start + i] + r[start + i]) * 0.5f) * w;
                    }
                    var spec = Fft.PowerDb(_fftIn);
                    if (_fftDb.Length != spec.Length) _fftDb = new double[spec.Length];
                    Array.Copy(spec, _fftDb, spec.Length);
                }
            }
        }

        // ===== Helpers =====
        private static double Ballistics(double prev, double next, int attackMs, int releaseMs, int fs)
        {
            double aAtk = Math.Exp(-1.0 / (attackMs * 0.001 * fs));
            double aRel = Math.Exp(-1.0 / (releaseMs * 0.001 * fs));
            return next > prev ? (aAtk * prev + (1 - aAtk) * next)
                               : (aRel * prev + (1 - aRel) * next);
        }
        private static double ToDb(double lin) => 20.0 * Math.Log10(Math.Max(1e-12, lin));

        private int ReadLatestXY(Span<PointF> dst)
        {
            int sr, n;
            lock (_lock) { sr = _sampleRate; n = Math.Min(_len, Math.Max(256, (int)(sr * 0.025))); }
            if (n <= 2) return 0;

            float[] l = new float[n], r = new float[n];
            lock (_lock)
            {
                int cap = _bufL.Length, start = (_wr - n + cap) % cap, head = Math.Min(n, cap - start);
                Array.Copy(_bufL, start, l, 0, head); Array.Copy(_bufR, start, r, 0, head);
                if (head < n) { Array.Copy(_bufL, 0, l, head, n - head); Array.Copy(_bufR, 0, r, head, n - head); }
            }
            int step = Math.Max(1, n / dst.Length);
            int idx = 0;
            for (int i = 0; i < n && idx < dst.Length; i += step)
                dst[idx++] = new PointF(Math.Clamp(l[i], -1f, 1f), Math.Clamp(r[i], -1f, 1f));
            return idx;
        }

        private void EnsureKWeight(int fs)
        {
            if (fs == _kFs) return;
            _kFs = fs;
            _kHpL.SetHighPass1st(fs, 38.0);
            _kHpR.SetHighPass1st(fs, 38.0);
            _kHsL.SetHighShelf(fs, 1500.0, +4.0);
            _kHsR.SetHighShelf(fs, 1500.0, +4.0);
        }

        private static LinearGradientBrush? SafeGradient(Rectangle rect, Color c1, Color c2, float angle)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return null;
            return new LinearGradientBrush(rect, c1, c2, angle);
        }

        // ==== DSP mini ====
        private sealed class Biquad
        {
            double b0, b1, b2, a1, a2, z1, z2;
            public double Process(double x)
            {
                double y = b0 * x + z1;
                z1 = b1 * x - a1 * y + z2;
                z2 = b2 * x - a2 * y;
                return y;
            }
            public void Reset() { z1 = z2 = 0; }
            public void SetHighPass1st(int fs, double fc)
            {
                // 1st-order via bilinear; Q≈0.707 damping
                double w = 2 * Math.PI * fc / fs;
                double c = Math.Cos(w), s = Math.Sin(w);
                double alpha = s / Math.Sqrt(2.0);
                double b0n = (1 + c) / 2.0;
                double b1n = -(1 + c);
                double b2n = (1 + c) / 2.0;
                double a0 = 1 + alpha, a1n = -2 * c, a2n = 1 - alpha;
                b0 = b0n / a0; b1 = b1n / a0; b2 = b2n / a0; a1 = a1n / a0; a2 = a2n / a0;
                Reset();
            }
            public void SetHighShelf(int fs, double fc, double gainDb)
            {
                double A = Math.Pow(10, gainDb / 40.0);
                double w = 2 * Math.PI * fc / fs, c = Math.Cos(w), s = Math.Sin(w);
                double alpha = s / Math.Sqrt(2.0);
                double beta = Math.Sqrt(A);
                double b0n = A * ((A + 1) + (A - 1) * c + beta * s);
                double b1n = -2 * A * ((A - 1) + (A + 1) * c);
                double b2n = A * ((A + 1) + (A - 1) * c - beta * s);
                double a0 = (A + 1) - (A - 1) * c + beta * s;
                double a1n = 2 * ((A - 1) - (A + 1) * c);
                double a2n = (A + 1) - (A - 1) * c - beta * s;
                b0 = b0n / a0; b1 = b1n / a0; b2 = b2n / a0; a1 = a1n / a0; a2 = a2n / a0;
                Reset();
            }
        }

        private static class Fft
        {
            public static double[] PowerDb(float[] time)
            {
                int n = time.Length;
                int m = HighestPow2LE(n);
                Span<double> re = stackalloc double[m];
                Span<double> im = stackalloc double[m];
                for (int i = 0; i < m; i++) re[i] = time[i];
                FFT(re, im);
                int bins = m / 2;
                var db = new double[bins];
                double scale = 2.0 / m;
                for (int k = 1; k < bins; k++)
                {
                    double r = re[k] * scale, ii = im[k] * scale;
                    double mag = Math.Sqrt(r * r + ii * ii) + 1e-12;
                    double v = 20.0 * Math.Log10(mag);
                    if (v < -120) v = -120; if (v > 0) v = 0;
                    db[k] = v;
                }
                return db;
            }

            private static int HighestPow2LE(int n) { int p = 1; while ((p << 1) <= n && p < (1 << 14)) p <<= 1; return p; }

            private static void FFT(Span<double> re, Span<double> im)
            {
                int n = re.Length, j = 0;
                for (int i = 0; i < n; i++)
                {
                    if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
                    int m = n >> 1; while (j >= m && m >= 2) { j -= m; m >>= 1; }
                    j += m;
                }
                for (int len = 2; len <= n; len <<= 1)
                {
                    double ang = -2 * Math.PI / len;
                    double wlenRe = Math.Cos(ang), wlenIm = Math.Sin(ang);
                    for (int i = 0; i < n; i += len)
                    {
                        double wRe = 1, wIm = 0;
                        for (int k = 0; k < len / 2; k++)
                        {
                            int u = i + k, v = i + k + len / 2;
                            double vr = re[v] * wRe - im[v] * wIm;
                            double vi = re[v] * wIm + im[v] * wRe;
                            double ur = re[u], ui = im[u];
                            re[v] = ur - vr; im[v] = ui - vi;
                            re[u] = ur + vr; im[u] = ui + vi;
                            double nwRe = wRe * wlenRe - wIm * wlenIm;
                            double nwIm = wRe * wlenIm + wIm * wlenRe;
                            wRe = nwRe; wIm = nwIm;
                        }
                    }
                }
            }
        }
    }
}
