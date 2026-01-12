#nullable enable
using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CinecorePlayer2025.Utilities
{
    /// <summary>
    /// Analizzatore HDR per frame video (HDR10 / PQ).
    /// Calcola luminanza, gamut coverage e istogramma nits.
    /// </summary>
    public sealed class HdrAnalyzer
    {
        public double MaxCLL { get; private set; }
        public double AvgCLL { get; private set; }
        public double MinCLL { get; private set; }
        public double Bt709Coverage { get; private set; }
        public double DciP3Coverage { get; private set; }
        public double Bt2020Coverage { get; private set; }

        public int[] LuminanceHistogram { get; } = new int[256]; // 0–10000 nits mappati logaritmicamente
        private readonly object _lock = new();

        // Costanti PQ (ST2084)
        private const double m1 = 2610.0 / 4096.0 / 4.0;
        private const double m2 = 2523.0 / 4096.0 * 128.0;
        private const double c1 = 3424.0 / 4096.0;
        private const double c2 = 2413.0 / 4096.0 * 32.0;
        private const double c3 = 2392.0 / 4096.0 * 32.0;

        /// <summary>
        /// Analizza un frame in PQ (R,G,B ∈ [0,1]).
        /// </summary>
        public void AnalyzeFrame(ReadOnlySpan<Vector3> rgbFrame)
        {
            if (rgbFrame.Length == 0)
            {
                Dbg.Log("[HdrAnalyzer] AnalyzeFrame chiamato con frame vuoto (Length = 0).");
                return;
            }

            double sum = 0, min = double.MaxValue, max = 0;
            int[] hist = new int[LuminanceHistogram.Length];

            int inside709 = 0, insideP3 = 0, inside2020 = 0;
            int total = rgbFrame.Length;

            Dbg.Log($"[HdrAnalyzer] AnalyzeFrame start. pixels={total}");

            int idxPixel = 0;
            foreach (var rgb in rgbFrame)
            {
                // Conversione lineare RGB → PQ luminance (nits)
                double pq = 0.2627 * rgb.X + 0.6780 * rgb.Y + 0.0593 * rgb.Z;
                pq = Math.Clamp(pq, 0.0, 1.0);
                double nits = PqToNits(pq);

                // Piccolo sample di debug sul primo pixel
                if (idxPixel == 0)
                {
                    Dbg.Log($"[HdrAnalyzer] FirstPixel rgb=({rgb.X:F3},{rgb.Y:F3},{rgb.Z:F3}) pq={pq:F4} nits={nits:F2}");
                }
                idxPixel++;

                sum += nits;
                if (nits < min) min = nits;
                if (nits > max) max = nits;

                int idx = (int)(Math.Log10(1 + nits) / Math.Log10(10001) * (hist.Length - 1));
                idx = Math.Clamp(idx, 0, hist.Length - 1);
                hist[idx]++;

                // Gamut coverage (approssimazione)
                var xy = RgbToXy(rgb);
                if (InTriangle(xy, GamutBT709)) inside709++;
                if (InTriangle(xy, GamutDCIP3)) insideP3++;
                if (InTriangle(xy, GamutBT2020)) inside2020++;
            }

            if (total <= 0)
            {
                Dbg.Log("[HdrAnalyzer] Nessun pixel valido nel frame (total=0).");
                return;
            }

            // Safety: se per qualche motivo min non è mai stato aggiornato
            if (min == double.MaxValue)
                min = 0.0;

            lock (_lock)
            {
                AvgCLL = sum / total;
                MaxCLL = max;
                MinCLL = min;
                Array.Copy(hist, LuminanceHistogram, hist.Length);
                Bt709Coverage = inside709 * 100.0 / total;
                DciP3Coverage = insideP3 * 100.0 / total;
                Bt2020Coverage = inside2020 * 100.0 / total;
            }

            Dbg.Log($"[HdrAnalyzer] AnalyzeFrame done. " +
                    $"Min={MinCLL:F4} Avg={AvgCLL:F2} Max={MaxCLL:F1} " +
                    $"BT.709={Bt709Coverage:F2}% P3={DciP3Coverage:F2}% BT.2020={Bt2020Coverage:F2}%");
        }

        public static double PqToNits(double pq)
        {
            pq = Math.Clamp(pq, 0.0, 1.0);
            double v = Math.Pow(pq, 1 / m2);
            double num = Math.Max(v - c1, 0.0);
            double den = c2 - c3 * v;
            if (den <= 0.0)
                return 0.0;

            double L = Math.Pow(num / den, 1 / m1);
            return L * 10000.0;
        }

        // -------------------- Gamut utils --------------------
        private static readonly Vector2[] GamutBT709 = { new(0.640f, 0.330f), new(0.300f, 0.600f), new(0.150f, 0.060f) };
        private static readonly Vector2[] GamutDCIP3 = { new(0.680f, 0.320f), new(0.265f, 0.690f), new(0.150f, 0.060f) };
        private static readonly Vector2[] GamutBT2020 = { new(0.708f, 0.292f), new(0.170f, 0.797f), new(0.131f, 0.046f) };

        private static Vector2 RgbToXy(Vector3 rgb)
        {
            // D65 matrix RGB → XYZ
            double X = 0.4124 * rgb.X + 0.3576 * rgb.Y + 0.1805 * rgb.Z;
            double Y = 0.2126 * rgb.X + 0.7152 * rgb.Y + 0.0722 * rgb.Z;
            double Z = 0.0193 * rgb.X + 0.1192 * rgb.Y + 0.9505 * rgb.Z;
            double sum = X + Y + Z + 1e-9;
            return new Vector2((float)(X / sum), (float)(Y / sum));
        }

        private static bool InTriangle(Vector2 p, Vector2[] tri)
        {
            Vector2 a = tri[0], b = tri[1], c = tri[2];
            float area = Cross(b - a, c - a);
            if (Math.Abs(area) < 1e-12f)
                return false;

            float s = Cross(p - a, c - a) / area;
            float t = Cross(b - a, p - a) / area;
            return s >= 0 && t >= 0 && s + t <= 1;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        // -------------------- Debug utils --------------------
        public string GetReport()
        {
            lock (_lock)
            {
                return $"maxCLL: {MaxCLL:F1} nits\n" +
                       $"avgCLL: {AvgCLL:F2} nits\n" +
                       $"minCLL: {MinCLL:F5} nits\n" +
                       $"BT.709: {Bt709Coverage:F2}%\n" +
                       $"DCI-P3: {DciP3Coverage:F2}%\n" +
                       $"BT.2020: {Bt2020Coverage:F2}%";
            }
        }
    }

    /// <summary>
    /// Overlay waveform HDR (0→10 000 nits) con header metriche dell'HdrAnalyzer
    /// e opzionale mini-istogramma. Nessuna dipendenza esterna (solo System.Drawing).
    /// </summary>
    public sealed class HdrWaveformOverlay : Control
    {
        // --- PQ (ST2084) ---
        const double m1 = 2610.0 / 4096.0 / 4.0;
        const double m2 = 2523.0 / 4096.0 * 128.0;
        const double c1 = 3424.0 / 4096.0;
        const double c2 = 2413.0 / 4096.0 * 32.0;
        const double c3 = 2392.0 / 4096.0 * 32.0;

        static double PqToNits(double pq)
        {
            pq = Math.Clamp(pq, 0.0, 1.0);
            double v = Math.Pow(pq, 1 / m2);
            double num = Math.Max(v - c1, 0.0);
            double den = c2 - c3 * v;
            if (den <= 0.0)
                return 0.0;

            double L = Math.Pow(num / den, 1 / m1);
            return L * 10000.0;
        }

        // DTO per passare i dati dell'Analyzer
        public readonly struct AnalyzerSnapshot
        {
            public readonly double Max, Avg, Min, Bt709, P3, Bt2020;
            public readonly int[] Hist; // 256 bin log

            public AnalyzerSnapshot(double max, double avg, double min, double bt709, double p3, double bt2020, int[] hist)
            {
                Max = max;
                Avg = avg;
                Min = min;
                Bt709 = bt709;
                P3 = p3;
                Bt2020 = bt2020;
                Hist = hist ?? Array.Empty<int>();
            }

            public static AnalyzerSnapshot From(HdrAnalyzer a)
                => new AnalyzerSnapshot(
                    a.MaxCLL,
                    a.AvgCLL,
                    a.MinCLL,
                    a.Bt709Coverage,
                    a.DciP3Coverage,
                    a.Bt2020Coverage,
                    a.LuminanceHistogram.ToArray());
        }

        // --- Config overlay ---
        public bool ClickThrough { get; set; } = true;
        public bool ShowHeader { get; set; } = true;
        public bool ShowHistogram { get; set; } = false;

        public int GridX = 512;     // risoluzione orizzontale densità
        public int GridY = 256;     // risoluzione verticale (nits, log)
        public double TopNits = 10000.0;
        public double Decay = 0.90; // ritenzione
        public double AddGain = 1.0;

        // --- Stato ---
        readonly object _lock = new();
        float[,] _dens;          // [x,y] densità
        double _visMax = 1.0;    // autogain mapping
        readonly Timer _tick;

        // Header
        double _hMax, _hAvg, _hMin, _h709, _hP3, _h2020;
        int[] _hist = Array.Empty<int>();
        double _histMaxVis = 1.0;

        // Tema
        readonly Color _txt = Color.FromArgb(234, 234, 234);
        readonly Color _txtDim = Color.FromArgb(168, 168, 168);

        public HdrWaveformOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;

            _dens = new float[GridX, GridY];

            _tick = new Timer { Interval = 33 }; // ~30 fps
            _tick.Tick += (_, __) => Invalidate();
            _tick.Start();

            Dbg.Log("[HdrWaveformOverlay] Creato overlay HDR waveform.");
        }

        // ===== API: aggiorna header/istogramma da Analyzer =====
        public void SetHeader(AnalyzerSnapshot s)
        {
            lock (_lock)
            {
                _hMax = s.Max;
                _hAvg = s.Avg;
                _hMin = s.Min;
                _h709 = s.Bt709;
                _hP3 = s.P3;
                _h2020 = s.Bt2020;
                _hist = s.Hist ?? Array.Empty<int>();
                int maxBin = _hist.Length > 0 ? _hist.Max() : 1;
                _histMaxVis = 0.85 * _histMaxVis + 0.15 * Math.Max(1, maxBin);
            }

            Dbg.Log($"[HdrWaveformOverlay] SetHeader: Max={s.Max:F1} Avg={s.Avg:F2} Min={s.Min:F4} " +
                    $"BT.709={s.Bt709:F1}% P3={s.P3:F1}% BT.2020={s.Bt2020:F1}%");

            Invalidate();
        }

        // Comodità: one-shot con Analyzer
        public void SetHeaderFrom(HdrAnalyzer analyzer)
        {
            if (analyzer == null)
            {
                Dbg.Log("[HdrWaveformOverlay] SetHeaderFrom chiamato con analyzer null.");
                return;
            }
            SetHeader(AnalyzerSnapshot.From(analyzer));
        }

        // ===== API: passa il frame in RGB PQ (0..1) =====
        public void PushWaveRgbPq(ReadOnlySpan<Vector3> rgb, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                Dbg.Log($"[HdrWaveformOverlay] PushWaveRgbPq: width/height non validi ({width}x{height}).");
                return;
            }

            int needed = width * height;
            if (rgb.Length < needed)
            {
                Dbg.Log($"[HdrWaveformOverlay] PushWaveRgbPq: frame troppo corto. len={rgb.Length}, expected={needed}");
                return;
            }

            int stepX = Math.Max(1, width / GridX);
            int stepY = Math.Max(1, height / GridY);

            // Sample debug sul primo pixel
            if (rgb.Length > 0)
            {
                var px0 = rgb[0];
                double pqY0 = 0.2627 * px0.X + 0.6780 * px0.Y + 0.0593 * px0.Z;
                double nits0 = PqToNits(pqY0);
                Dbg.Log($"[HdrWaveformOverlay] PushWaveRgbPq: firstPixel rgb=({px0.X:F3},{px0.Y:F3},{px0.Z:F3}) " +
                        $"pqY={pqY0:F4} nits={nits0:F2} width={width} height={height}");
            }

            lock (_lock)
            {
                // decadimento
                for (int x = 0; x < GridX; x++)
                    for (int y = 0; y < GridY; y++)
                        _dens[x, y] = (float)(_dens[x, y] * Decay);

                // accumulo
                for (int y = 0; y < height; y += stepY)
                {
                    int by = y * width;
                    for (int x = 0; x < width; x += stepX)
                    {
                        Vector3 px = rgb[by + x];
                        double pqY = 0.2627 * px.X + 0.6780 * px.Y + 0.0593 * px.Z; // luma PQ
                        double nits = PqToNits(pqY);

                        int gx = x * GridX / Math.Max(1, width);
                        int gy = (int)(Math.Log10(1 + nits) / Math.Log10(1 + TopNits) * (GridY - 1));
                        gx = Math.Clamp(gx, 0, GridX - 1);
                        gy = Math.Clamp(gy, 0, GridY - 1);
                        _dens[gx, gy] += (float)AddGain;
                    }
                }

                // autogain
                float localMax = 0f;
                for (int x = 0; x < GridX; x++)
                    for (int y = 0; y < GridY; y++)
                        if (_dens[x, y] > localMax) localMax = _dens[x, y];
                _visMax = 0.85 * _visMax + 0.15 * Math.Max(1.0, localMax);
            }
        }

        // ===== API: oppure passa direttamente luminanza in nits =====
        public void PushWaveNits(ReadOnlySpan<float> nits, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                Dbg.Log($"[HdrWaveformOverlay] PushWaveNits: width/height non validi ({width}x{height}).");
                return;
            }

            int needed = width * height;
            if (nits.Length < needed)
            {
                Dbg.Log($"[HdrWaveformOverlay] PushWaveNits: frame troppo corto. len={nits.Length}, expected={needed}");
                return;
            }

            if (nits.Length > 0)
            {
                Dbg.Log($"[HdrWaveformOverlay] PushWaveNits: firstPixel L={nits[0]:F2} width={width} height={height}");
            }

            int stepX = Math.Max(1, width / GridX);
            int stepY = Math.Max(1, height / GridY);

            lock (_lock)
            {
                for (int x = 0; x < GridX; x++)
                    for (int y = 0; y < GridY; y++)
                        _dens[x, y] = (float)(_dens[x, y] * Decay);

                for (int y = 0; y < height; y += stepY)
                {
                    int by = y * width;
                    for (int x = 0; x < width; x += stepX)
                    {
                        float L = nits[by + x];
                        int gx = x * GridX / Math.Max(1, width);
                        int gy = (int)(Math.Log10(1 + Math.Max(0, L)) / Math.Log10(1 + TopNits) * (GridY - 1));
                        gx = Math.Clamp(gx, 0, GridX - 1);
                        gy = Math.Clamp(gy, 0, GridY - 1);
                        _dens[gx, gy] += (float)AddGain;
                    }
                }

                float localMax = 0f;
                for (int x = 0; x < GridX; x++)
                    for (int y = 0; y < GridY; y++)
                        if (_dens[x, y] > localMax) localMax = _dens[x, y];
                _visMax = 0.85 * _visMax + 0.15 * Math.Max(1.0, localMax);
            }
        }

        // ===== Rendering =====
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x20; // WS_EX_TRANSPARENT
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var key = FindForm()?.TransparencyKey ?? Color.Black;
            e.Graphics.Clear(key);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            const int HTTRANSPARENT = -1;
            if (ClickThrough && m.Msg == WM_NCHITTEST)
            {
                m.Result = HTTRANSPARENT;
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            int pad = 12;
            int headerH = ShowHeader ? 52 : 0;
            int histH = ShowHistogram && (_hist?.Length ?? 0) > 0 ? 80 : 0;

            // Header box
            var rcHeader = new Rectangle(pad, pad, Math.Max(200, Width - pad * 2), headerH);
            // Waveform area
            var rcWave = new Rectangle(
                pad,
                rcHeader.Bottom + (headerH > 0 ? 8 : 0),
                Math.Max(200, Width - pad * 2),
                Math.Max(120, Height - pad * 2 - headerH - histH - (histH > 0 ? 8 : 0) - (headerH > 0 ? 8 : 0)));
            // Histogram area (bottom)
            var rcHist = new Rectangle(
                pad,
                rcWave.Bottom + (histH > 0 ? 8 : 0),
                Math.Max(200, Width - pad * 2),
                histH);

            if (ShowHeader) DrawHeader(g, rcHeader);
            DrawWaveform(g, rcWave);
            if (histH > 0) DrawHistogram(g, rcHist);
        }

        private void DrawHeader(Graphics g, Rectangle rc)
        {
            using var ft = new Font("Segoe UI Semibold", 11.0f);
            using var f = new Font("Segoe UI", 9.0f);
            double max, avg, min, c709, p3, b2020;
            lock (_lock)
            {
                max = _hMax;
                avg = _hAvg;
                min = _hMin;
                c709 = _h709;
                p3 = _hP3;
                b2020 = _h2020;
            }

            TextRenderer.DrawText(g, "HDR Analyzer", ft, new Point(rc.X, rc.Y - 2), _txt);
            TextRenderer.DrawText(
                g,
                $"MaxCLL {max:0.#}   AvgCLL {avg:0.##}   MinCLL {min:0.###} nits",
                f,
                new Point(rc.X, rc.Y + ft.Height + 2),
                _txt);
            TextRenderer.DrawText(
                g,
                $"BT.709 {c709:0.0}%   DCI-P3 {p3:0.0}%   BT.2020 {b2020:0.0}%",
                f,
                new Point(rc.X, rc.Y + ft.Height + 2 + f.Height + 1),
                _txtDim);
        }

        private void DrawWaveform(Graphics g, Rectangle rc)
        {
            // cornice + griglia
            using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1))
                g.DrawRectangle(pen, rc);

            using (var pGrid = new Pen(Color.FromArgb(40, 255, 255, 255), 1))
            using (var brLbl = new SolidBrush(Color.FromArgb(170, 220, 220, 220)))
            using (var f = new Font("Segoe UI", 8.0f))
            {
                foreach (var n in new[] { 0, 100, 400, 1000, 4000, 10000 })
                {
                    double t = Math.Log10(1 + n) / Math.Log10(1 + TopNits);
                    float y = rc.Bottom - (float)(t * rc.Height);
                    g.DrawLine(pGrid, rc.Left, y, rc.Right, y);
                    var label = n >= 1000 ? $"{n / 1000.0:0.#}k" : $"{n}";
                    g.DrawString(label, f, brLbl, rc.Left + 4, y - f.Height - 1);
                }
            }

            float[,] local;
            double visMax;
            lock (_lock)
            {
                local = (float[,])_dens.Clone();
                visMax = _visMax;
            }

            int gx = local.GetLength(0);
            int gy = local.GetLength(1);
            float cellW = rc.Width / (float)gx;
            float cellH = rc.Height / (float)gy;

            for (int x = 0; x < gx; x++)
            {
                for (int y = 0; y < gy; y++)
                {
                    float v = local[x, y];
                    if (v <= 0.001f) continue;

                    double t = Math.Min(1.0, v / Math.Max(1.0, visMax));
                    t = Math.Pow(t, 0.6); // più brillante

                    int a = (int)(t * 220);
                    if (a < 8) continue;
                    var baseCol = Color.FromArgb(a, 255, 255, 255);

                    Color col = baseCol;
                    if (t > 0.6)
                    {
                        double u = (t - 0.6) / 0.4;
                        u = Math.Clamp(u, 0, 1);
                        int R = (int)(255 * u);
                        int G = 255;
                        int B = (int)(255 * (1 - u));
                        col = Color.FromArgb(Math.Min(255, a + 20), R, G, B);
                    }

                    float sx = rc.Left + x * cellW;
                    float sy = rc.Bottom - (y + 1) * cellH;
                    using var br = new SolidBrush(col);
                    g.FillRectangle(br, sx, sy, Math.Max(1f, cellW), Math.Max(1f, cellH));
                }
            }
        }

        private void DrawHistogram(Graphics g, Rectangle rc)
        {
            int[] hist;
            double hmax;
            lock (_lock)
            {
                hist = _hist ?? Array.Empty<int>();
                hmax = _histMaxVis;
            }

            using var f = new Font("Segoe UI", 8.75f);
            TextRenderer.DrawText(g, "Istogramma luminanza (nits, log)", f, new Point(rc.X, rc.Y - 14), _txt);

            using (var pen = new Pen(Color.FromArgb(80, 255, 255, 255), 1))
                g.DrawRectangle(pen, rc);

            if (hist.Length == 0 || hmax <= 0)
            {
                TextRenderer.DrawText(g, "Nessun dato", f, new Point(rc.X + 6, rc.Y + 6), _txtDim);
                return;
            }

            float w = rc.Width / (float)hist.Length;
            var ticks = new[] { 0, 100, 400, 1000, 4000, 10000 };
            using (var pTick = new Pen(Color.FromArgb(40, 255, 255, 255), 1))
            using (var brLbl = new SolidBrush(_txtDim))
            {
                foreach (var n in ticks)
                {
                    double t = Math.Log10(1 + n) / Math.Log10(10001.0);
                    int i = (int)(t * (hist.Length - 1));
                    i = Math.Clamp(i, 0, hist.Length - 1);
                    float x = rc.X + i * w;
                    g.DrawLine(pTick, x, rc.Bottom, x, rc.Top);
                    var s = n >= 1000 ? (n / 1000.0).ToString("0.#") + "k" : n.ToString();
                    g.DrawString(s, f, brLbl, x - 10, rc.Bottom + 2);
                }
            }

            for (int i = 0; i < hist.Length; i++)
            {
                int v = hist[i];
                double h = rc.Height * (v / Math.Max(1.0, hmax));
                if (h <= 0.5) continue;
                float x = rc.X + i * w;
                var bar = new RectangleF(x, (float)(rc.Bottom - h), Math.Max(1f, w - 0.75f), (float)h);
                using var lg = new LinearGradientBrush(bar, Color.Cyan, Color.Magenta, 90f);
                g.FillRectangle(lg, bar);
            }
        }
    }
}
