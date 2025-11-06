#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace CinecorePlayer2025
{
    /// <summary>
    /// UI WinForms + LiveCharts (reflection) con 4 pagine:
    /// 1) Levels: VU, Scope, Crest, Balance + Correlazione
    /// 2) Spectrum: spettro grande + info
    /// 3) Loudness: LUFS M/S/I, LRA, dBTP L/R, PSR/PLR
    /// 4) Stereo/Diag: Width, Corr storico, DC/Noise/SNR/ENOB
    /// </summary>
    internal sealed class AudioMetersLiveCharts : UserControl
    {
        private const bool SHOW_VU_HEADROOM = true;

        // === Reflection types ===
        private Type? _chartT, _axisT, _colSeriesT, _lineSeriesT, _scatSeriesT, _solidPaintT, _iSeriesT, _legendPosT, _tipPosT;

        // ====== Charts/axes (Levels) ======
        private object? _vuChart, _scopeChart, _crestChart, _balanceChart, _corrChart1;
        private object? _vuRmsSeries, _vuPkSeries;
        private object? _scopeLSeries, _scopeRSeries;
        private object? _crestSeries;
        private object? _balanceSeries;
        private object? _corrSeries1;
        private object? _xAxisVu, _yAxisVu, _xAxisSc, _yAxisSc, _xAxisCr, _yAxisCr, _xAxisBal, _yAxisBal, _xAxisCorr1, _yAxisCorr1;

        // ====== Charts/axes (Spectrum) ======
        private object? _specChart;
        private object? _specSeries;
        private object? _xAxisSp, _yAxisSp;

        // ====== Charts/axes (Loudness) ======
        private object? _loudChart, _lraChart, _tpChart, _dynChart;
        private object? _loudSeries, _lraSeries, _tpSeries, _dynSeries;
        private object? _xAxisLoud, _yAxisLoud, _xAxisLra, _yAxisLra, _xAxisTp, _yAxisTp, _xAxisDyn, _yAxisDyn;

        // ====== Charts/axes (Stereo/Diag) ======
        private object? _widthChart, _corrChart2;
        private object? _widthSeries, _corrSeries2;
        private object? _xAxisWidth, _yAxisWidth, _xAxisCorr2, _yAxisCorr2;

        // ====== Labels / badges (NO FLICKER) ======
        private readonly BufferedLabel _msg = new()
        {
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Gainsboro,
            BackColor = Color.Black,
            Visible = false
        };

        private readonly BufferedLabel _badgesSpectrum = MakeBadge();
        private readonly BufferedLabel _badgesLoud = MakeBadge();
        private readonly BufferedLabel _badgesPeaks = MakeBadge();
        private readonly BufferedLabel _badgesDiag = MakeBadge();
        private readonly BufferedLabel _badgesStereo = MakeBadge();

        // ====== Navigation (custom, niente TabControl) ======
        private readonly BufferedPanel _navBar = new() { Dock = DockStyle.Top, Height = 56, BackColor = Color.Black, Padding = new Padding(12, 10, 12, 8) };
        private readonly BufferedTable _navGrid = new()
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(0)
        };
        private readonly NavButton _btnLevels = new("Levels");
        private readonly NavButton _btnSpectrum = new("Spectrum");
        private readonly NavButton _btnLoud = new("Loudness");
        private readonly NavButton _btnStereo = new("Stereo/Diag");

        private readonly BufferedPanel _content = new() { Dock = DockStyle.Fill, BackColor = Color.Black };

        // ====== Pages ======
        private readonly TableLayoutPanel _pageLevels = NewPage(rows: 4);
        private readonly TableLayoutPanel _pageSpectrum = NewPage(rows: 2);
        private readonly TableLayoutPanel _pageLoud = NewPage(rows: 3);
        private readonly TableLayoutPanel _pageStereo = NewPage(rows: 3);

        // Storici / stati
        private readonly double[] _corrHist = new double[240];
        private int _corrW;
        private int _lastSpectrumSr, _lastSpectrumBins, _lastFftLen;
        private double _spYMin = -60;   // spettro (smoothed)
        private double _scVisAmp = 0.5; // scope (smoothed)
        private double _widthVis = 10;  // width half-range (smoothed)
        private bool _ok;

        // ===== Riduce flicker del container =====
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x02000000; return cp; } // WS_EX_COMPOSITED
        }

        // ===== Bootstrap =====
        static AudioMetersLiveCharts()
        {
            try
            {
                AddNativeSearchPaths();
                AppDomain.CurrentDomain.AssemblyResolve += (_, a) =>
                {
                    var n = new AssemblyName(a.Name).Name + ".dll";
                    var b = AppContext.BaseDirectory;
                    string[] c = {
                        Path.Combine(b, n),
                        Path.Combine(b, "runtimes","win-x64","lib", n),
                        Path.Combine(b, "runtimes","win","lib", n)
                    };
                    foreach (var p in c) if (File.Exists(p)) { try { return Assembly.LoadFrom(p); } catch { } }
                    return null;
                };
                TryLoadManaged("LiveChartsCore");
                TryLoadManaged("LiveChartsCore.SkiaSharpView");
                TryLoadManaged("LiveChartsCore.SkiaSharpView.WinForms");
                TryLoadManaged("SkiaSharp");
                TryLoadManaged("SkiaSharp.Views.WindowsForms");
            }
            catch { }
        }

        public AudioMetersLiveCharts()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;

            var ff = new FontFamily("Segoe UI");
            Font = new Font(ff, 9f, FontStyle.Regular);
            _msg.Font = new Font(ff, 9f, FontStyle.Bold);

            // reflection types
            _chartT = Resolve("LiveChartsCore.SkiaSharpView.WinForms.CartesianChart, LiveChartsCore.SkiaSharpView.WinForms");
            _axisT = Resolve("LiveChartsCore.SkiaSharpView.Axis, LiveChartsCore.SkiaSharpView");
            _colSeriesT = Resolve("LiveChartsCore.SkiaSharpView.ColumnSeries`1[[System.Double, System.Private.CoreLib]], LiveChartsCore.SkiaSharpView");
            _lineSeriesT = Resolve("LiveChartsCore.SkiaSharpView.LineSeries`1[[System.Double, System.Private.CoreLib]], LiveChartsCore.SkiaSharpView");
            _scatSeriesT = Resolve("LiveChartsCore.SkiaSharpView.ScatterSeries`1[[System.Double, System.Private.CoreLib]], LiveChartsCore.SkiaSharpView");
            _solidPaintT = Resolve("LiveChartsCore.SkiaSharpView.Painting.SolidColorPaint, LiveChartsCore.SkiaSharpView");
            _iSeriesT = Resolve("LiveChartsCore.ISeries, LiveChartsCore");
            _legendPosT = Resolve("LiveChartsCore.Measure.LegendPosition, LiveChartsCore");
            _tipPosT = Resolve("LiveChartsCore.Measure.TooltipPosition, LiveChartsCore");

            // === NAV BAR (tasti visibili, no riquadro grigio) ===
            _navGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            _navGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            _navGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            _navGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            _navGrid.Controls.Add(_btnLevels, 0, 0);
            _navGrid.Controls.Add(_btnSpectrum, 1, 0);
            _navGrid.Controls.Add(_btnLoud, 2, 0);
            _navGrid.Controls.Add(_btnStereo, 3, 0);
            _navBar.Controls.Add(_navGrid);

            _btnLevels.Click += (_, __) => SetPage(0);
            _btnSpectrum.Click += (_, __) => SetPage(1);
            _btnLoud.Click += (_, __) => SetPage(2);
            _btnStereo.Click += (_, __) => SetPage(3);

            Controls.Add(_content);
            Controls.Add(_navBar);
            Controls.Add(_msg); _msg.BringToFront();

            // setup charts e pagine
            _ok = InitCharts();
            if (!_ok)
            {
                SetLabelTextNoFlicker(_msg, "LiveCharts non trovate: copia LiveChartsCore*, SkiaSharp* e HarfBuzzSharp vicino all'eseguibile.");
                _msg.Visible = true;
            }

            // pagina iniziale
            SetPage(0);
        }

        // ======= PAGE SWITCH =======
        private void SetPage(int index)
        {
            _btnLevels.Selected = (index == 0);
            _btnSpectrum.Selected = (index == 1);
            _btnLoud.Selected = (index == 2);
            _btnStereo.Selected = (index == 3);

            _content.SuspendLayout();
            _content.Controls.Clear();
            switch (index)
            {
                case 0: _content.Controls.Add(_pageLevels); break;
                case 1: _content.Controls.Add(_pageSpectrum); break;
                case 2: _content.Controls.Add(_pageLoud); break;
                case 3: _content.Controls.Add(_pageStereo); break;
            }
            _content.ResumeLayout();
            _content.Invalidate();
        }

        // ===== Public API =====
        public void Update(LoopbackSampler.AudioMetrics m)
        {
            if (!_ok) return;

            // --- VU ---
            double dBL = ToDb(m.RmsL), dBR = ToDb(m.RmsR);
            double pHL = ToDb(m.PeakHoldL), pHR = ToDb(m.PeakHoldR);
            bool sil = m.IsSilent;

            if (SHOW_VU_HEADROOM)
            {
                double hL = sil ? 0 : Math.Clamp(-dBL, 0, 40);
                double hR = sil ? 0 : Math.Clamp(-dBR, 0, 40);
                double hpL = sil ? 0 : Math.Clamp(-pHL, 0, 40);
                double hpR = sil ? 0 : Math.Clamp(-pHR, 0, 40);
                SetProp(_vuRmsSeries!, "Values", new double[] { hL, hR });
                SetProp(_vuPkSeries!, "Values", new double[] { hpL, hpR });
            }
            else
            {
                SetProp(_vuRmsSeries!, "Values", new double[] { sil ? -120 : dBL, sil ? -120 : dBR });
                SetProp(_vuPkSeries!, "Values", new double[] { sil ? -120 : pHL, sil ? -120 : pHR });
            }

            // --- Scope ---
            if (m.ScopeL.Length > 0)
            {
                SetProp(_scopeLSeries!, "Values", m.ScopeL.Select(v => (double)v).ToArray());
                SetProp(_scopeRSeries!, "Values", m.ScopeR.Select(v => (double)v).ToArray());

                double maxAbs = Math.Max(
                    m.ScopeL.Length > 0 ? m.ScopeL.Select(v => Math.Abs((double)v)).Max() : 0.0,
                    m.ScopeR.Length > 0 ? m.ScopeR.Select(v => Math.Abs((double)v)).Max() : 0.0);
                double targetHalf = Math.Clamp(maxAbs * 1.2, 0.2, 1.0);
                _scVisAmp = 0.85 * _scVisAmp + 0.15 * targetHalf;
                TrySet(_yAxisSc!, "MinLimit", -_scVisAmp);
                TrySet(_yAxisSc!, "MaxLimit", +_scVisAmp);
            }

            // --- Crest ---
            double crestLvis = m.IsSilent ? 0.0 : Math.Clamp(m.CrestL_dB, 0.0, 24.0);
            double crestRvis = m.IsSilent ? 0.0 : Math.Clamp(m.CrestR_dB, 0.0, 24.0);
            SetProp(_crestSeries!, "Values", new double[] { crestLvis, crestRvis });

            // --- Balance ---
            double balPct = Math.Clamp(m.Balance * 100.0, -10.0, 10.0);
            SetProp(_balanceSeries!, "Values", new double[] { balPct });

            // --- Correlazione (storico) su 2 pagine ---
            _corrHist[_corrW] = m.Correlation;
            _corrW = (_corrW + 1) % _corrHist.Length;
            var corrVals = new double[_corrHist.Length];
            for (int i = 0; i < corrVals.Length; i++) corrVals[i] = _corrHist[(_corrW + i) % _corrHist.Length];
            SetProp(_corrSeries1!, "Values", corrVals);
            SetProp(_corrSeries2!, "Values", corrVals);

            // --- Spectrum ---
            if (m.SpectrumDb.Length > 0)
            {
                var binsAll = m.SpectrumDb.Length;
                var binsDraw = Math.Max(1, binsAll - 1);
                var specDraw = new double[binsDraw];
                Array.Copy(m.SpectrumDb, specDraw, binsDraw);
                SetProp(_specSeries!, "Values", specDraw);

                if (m.SampleRate > 0)
                {
                    int fftN = (m.FftLength > 0) ? m.FftLength : (binsAll - 1) * 2;
                    if (_lastSpectrumSr != m.SampleRate || _lastSpectrumBins != binsDraw || _lastFftLen != fftN)
                    {
                        _lastSpectrumSr = m.SampleRate;
                        _lastSpectrumBins = binsDraw;
                        _lastFftLen = fftN;

                        TrySet(_xAxisSp!, "MinLimit", 0d);
                        TrySet(_xAxisSp!, "MaxLimit", (double)(binsDraw - 1));
                        double hzStep = m.SampleRate / (double)fftN;

                        TrySetLabeler(_xAxisSp!, x =>
                        {
                            double k = Math.Max(0, x);
                            double hz = k * hzStep;
                            return hz >= 1000 ? (hz / 1000.0).ToString("0.#") + " kHz" : hz.ToString("0") + " Hz";
                        });
                    }
                }

                double minDb = specDraw.Min();
                double targetMin = Math.Max(-120, Math.Min(-10, Math.Floor(minDb / 5.0) * 5.0));
                _spYMin = 0.85 * _spYMin + 0.15 * targetMin;
                TrySet(_yAxisSp!, "MinLimit", _spYMin);
                TrySet(_yAxisSp!, "MaxLimit", 0d);
            }

            // --- Loudness & Peaks ---
            {
                double mL = SafeFinite(m.LufsM, double.NegativeInfinity);
                double sL = SafeFinite(m.LufsS, double.NegativeInfinity);
                double iL = SafeFinite(m.LufsI, double.NegativeInfinity);

                double[] loudVals = new[]
                {
                    double.IsNegativeInfinity(mL) ? -120 : Math.Max(-120, Math.Min(0, mL)),
                    double.IsNegativeInfinity(sL) ? -120 : Math.Max(-120, Math.Min(0, sL)),
                    double.IsNegativeInfinity(iL) ? -120 : Math.Max(-120, Math.Min(0, iL))
                };
                SetProp(_loudSeries!, "Values", loudVals);

                double lra = Math.Max(0, Math.Min(30, SafeFinite(m.Lra, 0)));
                SetProp(_lraSeries!, "Values", new double[] { lra });

                double tpL = SafeFinite(m.DbTpL, -120);
                double tpR = SafeFinite(m.DbTpR, -120);
                double minTp = Math.Min(tpL, tpR);
                double axisMin = (minTp < -6) ? Math.Floor(minTp / 3.0) * 3.0 : -6.0;
                TrySet(_yAxisTp!, "MinLimit", axisMin);
                TrySet(_yAxisTp!, "MaxLimit", 0d);
                SetProp(_tpSeries!, "Values", new double[]
                {
                    Math.Max(axisMin, Math.Min(0, tpL)),
                    Math.Max(axisMin, Math.Min(0, tpR))
                });

                double psr = Math.Max(0, Math.Min(30, SafeFinite(m.PsrDb, 0)));
                double plr = Math.Max(0, Math.Min(30, SafeFinite(m.PlrDb, 0)));
                SetProp(_dynSeries!, "Values", new double[] { psr, plr });

                SetLabelTextNoFlicker(_badgesLoud, $"M: {FmtLufs(m.LufsM)}   S: {FmtLufs(m.LufsS)}   I: {FmtLufs(m.LufsI)}");
                SetLabelTextNoFlicker(_badgesPeaks, $"LRA: {m.Lra:0.0} LU   dBTP L/R: {SafeFinite(m.DbTpL, double.NaN):0.0} / {SafeFinite(m.DbTpR, double.NaN):0.0}   " +
                                                    $"Clips TP: {m.ClipEvents}   Clips SP: {m.ClipSampleEvents}   " +
                                                    $"PSR: {SafeFinite(m.PsrDb, double.NaN):0.0} dB   PLR: {SafeFinite(m.PlrDb, double.NaN):0.0} dB");
            }

            // --- Stereo/Diag badges (no flicker) ---
            SetLabelTextNoFlicker(_badgesStereo, $"ρ: {m.Correlation:0.00}   Width: {SafeFinite(m.WidthDb, double.NaN):0.0} dB");
            SetLabelTextNoFlicker(_badgesDiag,
                $"DC L/R: {SafeFinite(m.DcL, double.NaN):+0.000;-0.000;0.000} / {SafeFinite(m.DcR, double.NaN):+0.000;-0.000;0.000}   " +
                $"Dominant: {SafeFinite(m.DominantHz, 0):0.#} Hz   Centroid: {SafeFinite(m.SpectralCentroidHz, 0):0.#} Hz   " +
                $"Roll-off95: {SafeFinite(m.SpectralRollOffHz, 0):0.#} Hz   Noise floor: {SafeFinite(m.NoiseFloorDb, double.NaN):0.0} dBFS   " +
                $"SNR: {SafeFinite(m.SnrDb, double.NaN):0.0} dB   ENOB: {SafeFinite(m.EnobBits, double.NaN):0.0} bit");

            // --- Width (asse adattivo) ---
            {
                double widthDb = SafeFinite(m.WidthDb, 0);
                widthDb = Math.Max(-30, Math.Min(+30, widthDb));
                SetProp(_widthSeries!, "Values", new double[] { widthDb });

                double target = Math.Clamp(Math.Abs(widthDb) * 1.2, 6.0, 20.0); // half-range
                _widthVis = 0.9 * _widthVis + 0.1 * target;
                TrySet(_yAxisWidth!, "MinLimit", -_widthVis);
                TrySet(_yAxisWidth!, "MaxLimit", +_widthVis);
            }

            // --- Spectrum badges (no flicker) ---
            SetLabelTextNoFlicker(_badgesSpectrum,
                $"DC off L/R: {SafeFinite(m.DcL, double.NaN):+0.000;-0.000;0.000} / {SafeFinite(m.DcR, double.NaN):+0.000;-0.000;0.000}   " +
                $"Dominant: {SafeFinite(m.DominantHz, 0):0.#} Hz   Centroid: {SafeFinite(m.SpectralCentroidHz, 0):0.#} Hz   " +
                $"Roll-off95: {SafeFinite(m.SpectralRollOffHz, 0):0.#} Hz");

            // Messaggio info (no flicker)
            SetLabelTextNoFlicker(_msg, m.IsSilent ? "Silenzio / floor" : "");
            _msg.Visible = m.IsSilent;

            // Redraw
            CallMethod(_vuChart!, "Update");
            CallMethod(_scopeChart!, "Update");
            CallMethod(_crestChart!, "Update");
            CallMethod(_balanceChart!, "Update");
            CallMethod(_corrChart1!, "Update");

            CallMethod(_specChart!, "Update");

            CallMethod(_loudChart!, "Update");
            CallMethod(_lraChart!, "Update");
            CallMethod(_tpChart!, "Update");
            CallMethod(_dynChart!, "Update");

            CallMethod(_widthChart!, "Update");
            CallMethod(_corrChart2!, "Update");
        }

        public void UpdateLevels(float rmsL, float rmsR, float peakHoldL, float peakHoldR, double[]? spectrumDb)
        {
            var m = new LoopbackSampler.AudioMetrics
            {
                RmsL = rmsL,
                RmsR = rmsR,
                PeakHoldL = peakHoldL,
                PeakHoldR = peakHoldR,
                PeakL = peakHoldL,
                PeakR = peakHoldR,
                SpectrumDb = spectrumDb ?? Array.Empty<double>(),
                Balance = (rmsL + rmsR) > 1e-9 ? (rmsR - rmsL) / (rmsL + rmsR) : 0.0,
                Correlation = 0.0,
                CrestL_dB = 0.0,
                CrestR_dB = 0.0,
                ScopeL = Array.Empty<float>(),
                ScopeR = Array.Empty<float>(),
                SampleRate = 0,
                FftLength = 0,
                IsSilent = (20.0 * Math.Log10(Math.Max(rmsL, rmsR) + 1e-12)) < -65.0
            };
            Update(m);
        }

        public void SetInfoMessage(string? msg)
        {
            SetLabelTextNoFlicker(_msg, string.IsNullOrWhiteSpace(msg) ? "" : msg);
            _msg.Visible = !string.IsNullOrWhiteSpace(msg);
            if (_msg.Visible) _msg.BringToFront();
        }

        // ===== Setup grafici & layout =====
        private bool InitCharts()
        {
            try
            {
                // Palette scura
                var grid = MakePaint(_solidPaintT!, 255, 255, 255, 28, 1.0);
                var axisTxt = MakePaint(_solidPaintT!, 220, 220, 220, 200);
                var vuFill = MakePaint(_solidPaintT!, 15, 160, 255, 220);
                var pkFill = MakePaint(_solidPaintT!, 255, 220, 0, 220);
                var spLine = MakePaint(_solidPaintT!, 0, 180, 255, 220, 2.0);
                var spFill = MakePaint(_solidPaintT!, 0, 180, 255, 18);
                var scLLine = MakePaint(_solidPaintT!, 0, 200, 255, 220, 1.6);
                var scRLine = MakePaint(_solidPaintT!, 255, 120, 60, 220, 1.6);
                var crestFi = MakePaint(_solidPaintT!, 40, 220, 140, 220);
                var balFill = MakePaint(_solidPaintT!, 200, 120, 255, 220);
                var corrLin = MakePaint(_solidPaintT!, 0, 220, 120, 220, 2.0);
                var loudFi = MakePaint(_solidPaintT!, 0, 200, 200, 220);
                var lraFi = MakePaint(_solidPaintT!, 120, 200, 120, 220);
                var tpFi = MakePaint(_solidPaintT!, 255, 160, 80, 220);
                var dynFi = MakePaint(_solidPaintT!, 100, 180, 255, 220);
                var widthFi = MakePaint(_solidPaintT!, 120, 220, 180, 220);

                Control MakeChartHost(object chart, string title) => MakeTitledPanel(title, (Control)chart);

                void PrepChart(object chartControl)
                {
                    var c = (Control)chartControl;
                    c.BackColor = Color.Black;
                    c.Margin = new Padding(0);
                    c.Padding = new Padding(0);
                    c.Dock = DockStyle.Fill;
                    TrySet(chartControl, "DrawMarginFrame", null);
                    TrySet(chartControl, "Background", null);
                    c.GetType().GetProperty("BorderStyle")?.SetValue(c, BorderStyle.None);
                }

                // ===== PAGE 1: LEVELS =====
                _vuChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_vuChart);
                TrySetEnum(_vuChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_vuChart, "TooltipPosition", _tipPosT!, "Top");
                _vuRmsSeries = Activator.CreateInstance(_colSeriesT!)!;
                SetProp(_vuRmsSeries, "Name", SHOW_VU_HEADROOM ? "Headroom (dB)" : "RMS (dBFS)");
                SetProp(_vuRmsSeries, "Values", new double[] { 0, 0 });
                TrySet(_vuRmsSeries, "Fill", vuFill);
                TrySet(_vuRmsSeries, "Stroke", null);
                TrySet(_vuRmsSeries, "Rx", 6d); TrySet(_vuRmsSeries, "Ry", 6d);
                TrySet(_vuRmsSeries, "MaxBarWidth", 140d);
                TrySet(_vuRmsSeries, "Padding", 0.25d);
                _vuPkSeries = Activator.CreateInstance(_scatSeriesT!)!;
                SetProp(_vuPkSeries, "Name", SHOW_VU_HEADROOM ? "Peak-hold (dB)" : "Peak-hold (dBFS)");
                SetProp(_vuPkSeries, "Values", new double[] { 0, 0 });
                TrySet(_vuPkSeries, "GeometrySize", 8d);
                TrySet(_vuPkSeries, "Fill", pkFill);
                TrySet(_vuPkSeries, "Stroke", null);
                _xAxisVu = Activator.CreateInstance(_axisT!)!;
                SetProp(_xAxisVu, "Labels", new[] { "L", "R" });
                TrySet(_xAxisVu, "LabelsPaint", axisTxt);
                TrySet(_xAxisVu, "SeparatorsPaint", null);
                TrySet(_xAxisVu, "TicksPaint", null);
                _yAxisVu = Activator.CreateInstance(_axisT!)!;
                if (SHOW_VU_HEADROOM) { SetProp(_yAxisVu, "MinLimit", 0d); SetProp(_yAxisVu, "MaxLimit", 40d); TrySetLabeler(_yAxisVu, v => v.ToString("0")); }
                else { SetProp(_yAxisVu, "MinLimit", -50d); SetProp(_yAxisVu, "MaxLimit", 0d); }
                TrySet(_yAxisVu, "LabelsPaint", axisTxt);
                TrySet(_yAxisVu, "SeparatorsPaint", grid);
                SetProp(_vuChart, "Series", CreateArray(_iSeriesT!, _vuRmsSeries, _vuPkSeries));
                SetProp(_vuChart, "XAxes", CreateArray(_axisT!, _xAxisVu));
                SetProp(_vuChart, "YAxes", CreateArray(_axisT!, _yAxisVu));

                _scopeChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_scopeChart);
                TrySetEnum(_scopeChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_scopeChart, "TooltipPosition", _tipPosT!, "Top");
                _scopeLSeries = Activator.CreateInstance(_lineSeriesT!)!;
                _scopeRSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_scopeLSeries, "Name", "L");
                SetProp(_scopeRSeries, "Name", "R");
                TrySet(_scopeLSeries, "GeometrySize", 0d);
                TrySet(_scopeRSeries, "GeometrySize", 0d);
                TrySet(_scopeLSeries, "LineSmoothness", 0d);
                TrySet(_scopeRSeries, "LineSmoothness", 0d);
                TrySet(_scopeLSeries, "Stroke", scLLine);
                TrySet(_scopeRSeries, "Stroke", scRLine);
                TrySet(_scopeLSeries, "Fill", null);
                TrySet(_scopeRSeries, "Fill", null);
                _xAxisSc = Activator.CreateInstance(_axisT!)!;
                TrySet(_xAxisSc, "LabelsPaint", null);
                TrySet(_xAxisSc, "SeparatorsPaint", null);
                TrySet(_xAxisSc, "TicksPaint", null);
                _yAxisSc = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisSc, "MinLimit", -0.5d); SetProp(_yAxisSc, "MaxLimit", +0.5d);
                TrySet(_yAxisSc, "LabelsPaint", axisTxt);
                TrySet(_yAxisSc, "SeparatorsPaint", grid);
                TrySet(_yAxisSc, "TicksPaint", null);
                SetProp(_scopeChart, "Series", CreateArray(_iSeriesT!, _scopeLSeries, _scopeRSeries));
                SetProp(_scopeChart, "XAxes", CreateArray(_axisT!, _xAxisSc));
                SetProp(_scopeChart, "YAxes", CreateArray(_axisT!, _yAxisSc));

                _crestChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_crestChart);
                TrySetEnum(_crestChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_crestChart, "TooltipPosition", _tipPosT!, "Top");
                _crestSeries = Activator.CreateInstance(_colSeriesT!)!;
                SetProp(_crestSeries, "Name", "Crest (dB)");
                SetProp(_crestSeries, "Values", new double[] { 0, 0 });
                TrySet(_crestSeries, "Fill", crestFi);
                TrySet(_crestSeries, "Stroke", null);
                TrySet(_crestSeries, "Rx", 6d); TrySet(_crestSeries, "Ry", 6d);
                TrySet(_crestSeries, "MaxBarWidth", 120d);
                TrySet(_crestSeries, "Padding", 0.25d);
                _xAxisCr = Activator.CreateInstance(_axisT!)!;
                SetProp(_xAxisCr, "Labels", new[] { "L", "R" });
                TrySet(_xAxisCr, "LabelsPaint", axisTxt);
                TrySet(_xAxisCr, "SeparatorsPaint", null);
                TrySet(_xAxisCr, "TicksPaint", null);
                _yAxisCr = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisCr, "MinLimit", 0d); SetProp(_yAxisCr, "MaxLimit", 24d);
                TrySet(_yAxisCr, "LabelsPaint", axisTxt);
                TrySet(_yAxisCr, "SeparatorsPaint", grid);
                TrySet(_yAxisCr, "TicksPaint", null);
                SetProp(_crestChart, "Series", CreateArray(_iSeriesT!, _crestSeries));
                SetProp(_crestChart, "XAxes", CreateArray(_axisT!, _xAxisCr));
                SetProp(_crestChart, "YAxes", CreateArray(_axisT!, _yAxisCr));

                _balanceChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_balanceChart);
                TrySetEnum(_balanceChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_balanceChart, "TooltipPosition", _tipPosT!, "Top");
                _balanceSeries = Activator.CreateInstance(_colSeriesT!)!;
                SetProp(_balanceSeries, "Name", "Balance (%)");
                SetProp(_balanceSeries, "Values", new double[] { 0.0 });
                TrySet(_balanceSeries, "Fill", balFill);
                TrySet(_balanceSeries, "Stroke", null);
                TrySet(_balanceSeries, "Rx", 6d); TrySet(_balanceSeries, "Ry", 6d);
                TrySet(_balanceSeries, "MaxBarWidth", 220d);
                TrySet(_balanceSeries, "Padding", 0.2d);
                _xAxisBal = Activator.CreateInstance(_axisT!)!;
                SetProp(_xAxisBal, "Labels", new[] { "L ← Balance → R" });
                TrySet(_xAxisBal, "LabelsPaint", axisTxt);
                TrySet(_xAxisBal, "SeparatorsPaint", null);
                TrySet(_xAxisBal, "TicksPaint", null);
                _yAxisBal = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisBal, "MinLimit", -10d); SetProp(_yAxisBal, "MaxLimit", +10d);
                TrySet(_yAxisBal, "LabelsPaint", axisTxt);
                TrySet(_yAxisBal, "SeparatorsPaint", grid);
                TrySetLabeler(_yAxisBal, v => v.ToString("+0;-0;0") + "%");
                SetProp(_balanceChart, "Series", CreateArray(_iSeriesT!, _balanceSeries));
                SetProp(_balanceChart, "XAxes", CreateArray(_axisT!, _xAxisBal));
                SetProp(_balanceChart, "YAxes", CreateArray(_axisT!, _yAxisBal));

                _corrChart1 = Activator.CreateInstance(_chartT!)!;
                PrepChart(_corrChart1);
                TrySetEnum(_corrChart1, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_corrChart1, "TooltipPosition", _tipPosT!, "Top");
                _corrSeries1 = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_corrSeries1, "Name", "ρ (corr)");
                SetProp(_corrSeries1, "Values", Enumerable.Repeat(0.0, _corrHist.Length).ToArray());
                TrySet(_corrSeries1, "GeometrySize", 0d);
                TrySet(_corrSeries1, "LineSmoothness", 0d);
                TrySet(_corrSeries1, "Stroke", corrLin);
                TrySet(_corrSeries1, "Fill", null);
                _xAxisCorr1 = Activator.CreateInstance(_axisT!)!;
                TrySet(_xAxisCorr1, "LabelsPaint", null);
                TrySet(_xAxisCorr1, "SeparatorsPaint", null);
                TrySet(_xAxisCorr1, "TicksPaint", null);
                _yAxisCorr1 = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisCorr1, "MinLimit", -1d); SetProp(_yAxisCorr1, "MaxLimit", +1d);
                TrySet(_yAxisCorr1, "LabelsPaint", axisTxt);
                TrySet(_yAxisCorr1, "SeparatorsPaint", grid);
                TrySetLabeler(_yAxisCorr1, v => v.ToString("0.0"));
                TrySet(_yAxisCorr1, "TicksPaint", null);
                SetProp(_corrChart1, "Series", CreateArray(_iSeriesT!, _corrSeries1));
                SetProp(_corrChart1, "XAxes", CreateArray(_axisT!, _xAxisCorr1));
                SetProp(_corrChart1, "YAxes", CreateArray(_axisT!, _yAxisCorr1));

                // Layout Levels
                _pageLevels.RowStyles.Clear();
                _pageLevels.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
                _pageLevels.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
                _pageLevels.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
                _pageLevels.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
                _pageLevels.Controls.Add(MakeChartHost(_vuChart, "VU L/R — Headroom & Peak-Hold"), 0, 0);
                _pageLevels.Controls.Add(MakeChartHost(_scopeChart, "Oscilloscopio — L/R (amp)"), 0, 1);
                _pageLevels.Controls.Add(MakeChartHost(_crestChart, "Crest factor — L/R (dB)"), 0, 2);
                var lvPair = NewPair(50, 50);
                lvPair.Controls.Add(MakeChartHost(_balanceChart, "Balance — L↔R (%)"), 0, 0);
                lvPair.Controls.Add(MakeChartHost(_corrChart1, "Correlazione — storico (−1…+1)"), 1, 0);
                _pageLevels.Controls.Add(lvPair, 0, 3);

                // ===== PAGE 2: SPECTRUM =====
                _specChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_specChart);
                TrySetEnum(_specChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_specChart, "TooltipPosition", _tipPosT!, "Top");
                _specSeries = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_specSeries, "Name", "Spettro (dBFS)");
                SetProp(_specSeries, "Values", new double[] { -60, -60, -60 });
                TrySet(_specSeries, "GeometrySize", 0d);
                TrySet(_specSeries, "LineSmoothness", 0d);
                TrySet(_specSeries, "Stroke", spLine);
                TrySet(_specSeries, "Fill", spFill);
                _xAxisSp = Activator.CreateInstance(_axisT!)!;
                TrySet(_xAxisSp, "LabelsPaint", axisTxt);
                TrySet(_xAxisSp, "SeparatorsPaint", null);
                TrySet(_xAxisSp, "TicksPaint", null);
                _yAxisSp = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisSp, "MinLimit", -60d); SetProp(_yAxisSp, "MaxLimit", 0d);
                TrySet(_yAxisSp, "LabelsPaint", axisTxt);
                TrySet(_yAxisSp, "SeparatorsPaint", grid);
                TrySet(_yAxisSp, "TicksPaint", null);
                SetProp(_specChart, "Series", CreateArray(_iSeriesT!, _specSeries));
                SetProp(_specChart, "XAxes", CreateArray(_axisT!, _xAxisSp));
                SetProp(_specChart, "YAxes", CreateArray(_axisT!, _yAxisSp));

                _pageSpectrum.RowStyles.Clear();
                _pageSpectrum.RowStyles.Add(new RowStyle(SizeType.Percent, 78));
                _pageSpectrum.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
                _pageSpectrum.Controls.Add(MakeChartHost(_specChart, "Spettro — mid (L+R)/2 in dBFS"), 0, 0);
                _pageSpectrum.Controls.Add(MakeBadgePanel("Spectrum — info", _badgesSpectrum, MakeBadgeEmpty()), 0, 1);

                // ===== PAGE 3: LOUDNESS =====
                _loudChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_loudChart);
                TrySetEnum(_loudChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_loudChart, "TooltipPosition", _tipPosT!, "Top");
                _loudSeries = Activator.CreateInstance(_colSeriesT!)!;
                SetProp(_loudSeries, "Name", "LUFS (M, S, I)");
                SetProp(_loudSeries, "Values", new double[] { -120, -120, -120 });
                TrySet(_loudSeries, "Fill", loudFi);
                TrySet(_loudSeries, "Stroke", null);
                TrySet(_loudSeries, "Rx", 6d); TrySet(_loudSeries, "Ry", 6d);
                TrySet(_loudSeries, "MaxBarWidth", 140d);
                TrySet(_loudSeries, "Padding", 0.25d);
                _xAxisLoud = Activator.CreateInstance(_axisT!)!;
                SetProp(_xAxisLoud, "Labels", new[] { "Momentary", "Short-Term", "Integrated" });
                TrySet(_xAxisLoud, "LabelsPaint", axisTxt);
                _yAxisLoud = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisLoud, "MinLimit", -60d); SetProp(_yAxisLoud, "MaxLimit", 0d);
                TrySetLabeler(_yAxisLoud, v => $"{v:0} LUFS");
                TrySet(_yAxisLoud, "LabelsPaint", axisTxt);
                TrySet(_yAxisLoud, "SeparatorsPaint", grid);
                SetProp(_loudChart, "Series", CreateArray(_iSeriesT!, _loudSeries));
                SetProp(_loudChart, "XAxes", CreateArray(_axisT!, _xAxisLoud));
                SetProp(_loudChart, "YAxes", CreateArray(_axisT!, _yAxisLoud));

                _lraChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_lraChart);
                TrySetEnum(_lraChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_lraChart, "TooltipPosition", _tipPosT!, "Top");
                _lraSeries = Activator.CreateInstance(_colSeriesT!)!;
                SetProp(_lraSeries, "Name", "LRA");
                SetProp(_lraSeries, "Values", new double[] { 0 });
                TrySet(_lraSeries, "Fill", lraFi);
                TrySet(_lraSeries, "Stroke", null);
                TrySet(_lraSeries, "Rx", 6d); TrySet(_lraSeries, "Ry", 6d);
                _xAxisLra = Activator.CreateInstance(_axisT!)!;
                SetProp(_xAxisLra, "Labels", new[] { "LRA" });
                TrySet(_xAxisLra, "LabelsPaint", axisTxt);
                _yAxisLra = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisLra, "MinLimit", 0d); SetProp(_yAxisLra, "MaxLimit", 30d);
                TrySet(_yAxisLra, "LabelsPaint", axisTxt);
                TrySet(_yAxisLra, "SeparatorsPaint", grid);
                SetProp(_lraChart, "Series", CreateArray(_iSeriesT!, _lraSeries));
                SetProp(_lraChart, "XAxes", CreateArray(_axisT!, _xAxisLra));
                SetProp(_lraChart, "YAxes", CreateArray(_axisT!, _yAxisLra));

                _tpChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_tpChart);
                TrySetEnum(_tpChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_tpChart, "TooltipPosition", _tipPosT!, "Top");
                _tpSeries = Activator.CreateInstance(_colSeriesT!)!;
                SetProp(_tpSeries, "Name", "dBTP (L/R)");
                SetProp(_tpSeries, "Values", new double[] { -6, -6 });
                TrySet(_tpSeries, "Fill", tpFi);
                TrySet(_tpSeries, "Stroke", null);
                TrySet(_tpSeries, "Rx", 6d); TrySet(_tpSeries, "Ry", 6d);
                _xAxisTp = Activator.CreateInstance(_axisT!)!;
                SetProp(_xAxisTp, "Labels", new[] { "L", "R" });
                TrySet(_xAxisTp, "LabelsPaint", axisTxt);
                _yAxisTp = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisTp, "MinLimit", -6d); SetProp(_yAxisTp, "MaxLimit", 0d);
                TrySet(_yAxisTp, "LabelsPaint", axisTxt);
                TrySetLabeler(_yAxisTp, v => $"{v:0} dBTP");
                TrySet(_yAxisTp, "SeparatorsPaint", grid);
                SetProp(_tpChart, "Series", CreateArray(_iSeriesT!, _tpSeries));
                SetProp(_tpChart, "XAxes", CreateArray(_axisT!, _xAxisTp));
                SetProp(_tpChart, "YAxes", CreateArray(_axisT!, _yAxisTp));

                _dynChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_dynChart);
                TrySetEnum(_dynChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_dynChart, "TooltipPosition", _tipPosT!, "Top");
                _dynSeries = Activator.CreateInstance(_colSeriesT!)!;
                SetProp(_dynSeries, "Name", "PSR / PLR (dB)");
                SetProp(_dynSeries, "Values", new double[] { 0, 0 });
                TrySet(_dynSeries, "Fill", dynFi);
                TrySet(_dynSeries, "Stroke", null);
                TrySet(_dynSeries, "Rx", 6d); TrySet(_dynSeries, "Ry", 6d);
                _xAxisDyn = Activator.CreateInstance(_axisT!)!;
                SetProp(_xAxisDyn, "Labels", new[] { "PSR", "PLR" });
                TrySet(_xAxisDyn, "LabelsPaint", axisTxt);
                _yAxisDyn = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisDyn, "MinLimit", 0d); SetProp(_yAxisDyn, "MaxLimit", 30d);
                TrySet(_yAxisDyn, "LabelsPaint", axisTxt);
                TrySet(_yAxisDyn, "SeparatorsPaint", grid);
                SetProp(_dynChart, "Series", CreateArray(_iSeriesT!, _dynSeries));
                SetProp(_dynChart, "XAxes", CreateArray(_axisT!, _xAxisDyn));
                SetProp(_dynChart, "YAxes", CreateArray(_axisT!, _yAxisDyn));

                // Layout Loudness
                _pageLoud.RowStyles.Clear();
                _pageLoud.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
                _pageLoud.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
                _pageLoud.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
                var ldTop = NewPair(60, 40);
                ldTop.Controls.Add(MakeChartHost(_loudChart, "Loudness — LUFS (M / S / I)"), 0, 0);
                ldTop.Controls.Add(MakeChartHost(_tpChart, "True-Peak — dBTP L/R"), 1, 0);
                var ldMid = NewPair(40, 60);
                ldMid.Controls.Add(MakeChartHost(_lraChart, "LRA (Loudness Range)"), 0, 0);
                ldMid.Controls.Add(MakeChartHost(_dynChart, "Dinamica — PSR / PLR (dB)"), 1, 0);
                _pageLoud.Controls.Add(ldTop, 0, 0);
                _pageLoud.Controls.Add(ldMid, 0, 1);
                _pageLoud.Controls.Add(MakeBadgePanel("Loudness & Peaks — valori", _badgesLoud, _badgesPeaks), 0, 2);

                // ===== PAGE 4: STEREO/DIAG =====
                _widthChart = Activator.CreateInstance(_chartT!)!;
                PrepChart(_widthChart);
                TrySetEnum(_widthChart, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_widthChart, "TooltipPosition", _tipPosT!, "Top");
                _widthSeries = Activator.CreateInstance(_colSeriesT!)!;
                SetProp(_widthSeries, "Name", "Width (Mid/Side, dB)");
                SetProp(_widthSeries, "Values", new double[] { 0 });
                TrySet(_widthSeries, "Fill", widthFi);
                TrySet(_widthSeries, "Stroke", null);
                TrySet(_widthSeries, "Rx", 6d); TrySet(_widthSeries, "Ry", 6d);
                _xAxisWidth = Activator.CreateInstance(_axisT!)!;
                SetProp(_xAxisWidth, "Labels", new[] { "Width dB" });
                TrySet(_xAxisWidth, "LabelsPaint", axisTxt);
                _yAxisWidth = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisWidth, "MinLimit", -20d); SetProp(_yAxisWidth, "MaxLimit", +20d);
                TrySet(_yAxisWidth, "LabelsPaint", axisTxt);
                TrySetLabeler(_yAxisWidth, v => $"{v:0} dB");
                TrySet(_yAxisWidth, "SeparatorsPaint", grid);
                SetProp(_widthChart, "Series", CreateArray(_iSeriesT!, _widthSeries));
                SetProp(_widthChart, "XAxes", CreateArray(_axisT!, _xAxisWidth));
                SetProp(_widthChart, "YAxes", CreateArray(_axisT!, _yAxisWidth));

                _corrChart2 = Activator.CreateInstance(_chartT!)!;
                PrepChart(_corrChart2);
                TrySetEnum(_corrChart2, "LegendPosition", _legendPosT!, "Hidden");
                TrySetEnum(_corrChart2, "TooltipPosition", _tipPosT!, "Top");
                _corrSeries2 = Activator.CreateInstance(_lineSeriesT!)!;
                SetProp(_corrSeries2, "Name", "ρ (corr)");
                SetProp(_corrSeries2, "Values", Enumerable.Repeat(0.0, _corrHist.Length).ToArray());
                TrySet(_corrSeries2, "GeometrySize", 0d);
                TrySet(_corrSeries2, "LineSmoothness", 0d);
                TrySet(_corrSeries2, "Stroke", corrLin);
                TrySet(_corrSeries2, "Fill", null);
                _xAxisCorr2 = Activator.CreateInstance(_axisT!)!;
                TrySet(_xAxisCorr2, "LabelsPaint", null);
                TrySet(_xAxisCorr2, "SeparatorsPaint", null);
                TrySet(_xAxisCorr2, "TicksPaint", null);
                _yAxisCorr2 = Activator.CreateInstance(_axisT!)!;
                SetProp(_yAxisCorr2, "MinLimit", -1d); SetProp(_yAxisCorr2, "MaxLimit", +1d);
                TrySet(_yAxisCorr2, "LabelsPaint", axisTxt);
                TrySet(_yAxisCorr2, "SeparatorsPaint", grid);
                TrySetLabeler(_yAxisCorr2, v => v.ToString("0.0"));
                TrySet(_yAxisCorr2, "TicksPaint", null);
                SetProp(_corrChart2, "Series", CreateArray(_iSeriesT!, _corrSeries2));
                SetProp(_corrChart2, "XAxes", CreateArray(_axisT!, _xAxisCorr2));
                SetProp(_corrChart2, "YAxes", CreateArray(_axisT!, _yAxisCorr2));

                var stPair = NewPair(55, 45);
                stPair.Controls.Add(MakeChartHost(_widthChart, "Width — Mid/Side (dB)"), 0, 0);
                stPair.Controls.Add(MakeChartHost(_corrChart2, "Correlazione — storico (−1…+1)"), 1, 0);

                _pageStereo.RowStyles.Clear();
                _pageStereo.RowStyles.Add(new RowStyle(SizeType.Percent, 68));
                _pageStereo.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
                _pageStereo.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
                _pageStereo.Controls.Add(stPair, 0, 0);
                _pageStereo.Controls.Add(MakeSpacer(8), 0, 1);
                _pageStereo.Controls.Add(MakeBadgePanel("Stereo & Diagnostics", _badgesStereo, _badgesDiag), 0, 2);

                return true;
            }
            catch { return false; }
        }

        // ===== Helpers =====
        private static TableLayoutPanel NewPage(int rows)
        {
            var p = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                ColumnCount = 1,
                RowCount = rows,
                Padding = new Padding(12)
            };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return p;
        }

        private static TableLayoutPanel NewPair(int leftPct, int rightPct)
        {
            var pair = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Black, Padding = new Padding(0) };
            pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, leftPct));
            pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, rightPct));
            return pair;
        }

        private static Panel MakeSpacer(int h) => new Panel { Dock = DockStyle.Top, Height = h, BackColor = Color.Black };

        private static double ToDb(double v) => Math.Clamp(20.0 * Math.Log10(Math.Max(v, 1e-12)), -120.0, 0.0);
        private static double SafeFinite(double v, double fallback) => (double.IsFinite(v) ? v : fallback);

        private static string FmtLufs(double v)
        {
            if (double.IsNegativeInfinity(v)) return "−∞";
            if (!double.IsFinite(v)) return "n/a";
            return $"{v:0.0}";
        }

        private static BufferedLabel MakeBadge()
        {
            return new BufferedLabel
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.Gainsboro,
                BackColor = Color.FromArgb(20, 20, 24),
                Padding = new Padding(10, 6, 10, 6),
                Font = new Font(new FontFamily("Segoe UI"), 9f, FontStyle.Regular)
            };
        }

        private static BufferedLabel MakeBadgeEmpty()
        {
            var l = MakeBadge();
            l.Text = "";
            return l;
        }

        private static Control MakeBadgePanel(string title, BufferedLabel row1, BufferedLabel row2)
        {
            var host = new BufferedPanel { Dock = DockStyle.Fill, BackColor = Color.Black, Padding = new Padding(8, 22, 8, 8) };
            var lbl = new BufferedLabel
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 18,
                ForeColor = Color.Gainsboro,
                BackColor = Color.Black,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 2, 0, 0),
                Font = new Font(new FontFamily("Segoe UI"), 9f, FontStyle.Bold)
            };
            var grid = new BufferedTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Black, Padding = new Padding(0) };
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.Controls.Add(row1, 0, 0);
            grid.Controls.Add(row2, 0, 1);
            host.Controls.Add(grid);
            host.Controls.Add(lbl);
            return host;
        }

        private static Control MakeTitledPanel(string title, Control inner)
        {
            var host = new BufferedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Black,
                Padding = new Padding(10, 22, 10, 10)
            };
            var lbl = new BufferedLabel
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 18,
                ForeColor = Color.Gainsboro,
                BackColor = Color.Black,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 2, 0, 0),
                Font = new Font(new FontFamily("Segoe UI"), 10f, FontStyle.Bold)
            };
            inner.Dock = DockStyle.Fill;
            if (inner is Control c) { c.Margin = new Padding(0); c.Padding = new Padding(0); }
            host.Controls.Add(inner);
            host.Controls.Add(lbl);
            return host;
        }

        private static void SetLabelTextNoFlicker(BufferedLabel lbl, string text)
        {
            if (!string.Equals(lbl.Text, text, StringComparison.Ordinal))
                lbl.Text = text;
        }

        private static void CallMethod(object target, string methodName)
        {
            try { target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)?.Invoke(target, null); }
            catch { }
        }

        private static void TrySetEnum(object? target, string prop, Type enumType, string member)
        {
            try { target?.GetType().GetProperty(prop)?.SetValue(target, Enum.Parse(enumType, member)); } catch { }
        }

        private static void TrySetLabeler(object axis, Func<double, string> f)
        {
            try { axis.GetType().GetProperty("Labeler", BindingFlags.Instance | BindingFlags.Public)?.SetValue(axis, f); }
            catch { }
        }

        private static void TrySet(object target, string prop, object? value)
        {
            try { SetProp(target, prop, value); } catch { }
        }

        private static void SetProp(object target, string propName, object? value)
        {
            var pi = target.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
            if (pi == null || !pi.CanWrite) return;

            var destT = pi.PropertyType; var v = value;
            if (v != null && !destT.IsInstanceOfType(v))
            {
                if (destT.IsArray && v is Array src)
                {
                    var elemT = destT.GetElementType()!;
                    var arr = Array.CreateInstance(elemT, src.Length);
                    for (int i = 0; i < src.Length; i++) arr.SetValue(src.GetValue(i), i);
                    v = arr;
                }
            }
            pi.SetValue(target, v);
        }

        private static Array CreateArray(Type elementType, params object?[] items)
        {
            var arr = Array.CreateInstance(elementType, items.Length);
            for (int i = 0; i < items.Length; i++) arr.SetValue(items[i], i);
            return arr;
        }

        private object MakePaint(Type solidPaintT, byte r, byte g, byte b, byte a, double strokeThickness = 0)
        {
            var skColorT = Resolve("SkiaSharp.SKColor, SkiaSharp")!;
            var color = Activator.CreateInstance(skColorT, new object[] { r, g, b, a })!;
            var paint = Activator.CreateInstance(solidPaintT, new object[] { color })!;
            TrySet(paint, "StrokeThickness", strokeThickness);
            return paint;
        }

        private static Type? Resolve(string? aqn)
        {
            if (aqn == null) return null;
            var t = Type.GetType(aqn, false);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = asm.GetType(aqn.Split(',')[0], false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static void TryLoadManaged(string simple)
        {
            try
            {
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                {
                    try { return string.Equals(a.GetName().Name, simple, StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                })) return;

                var b = AppContext.BaseDirectory;
                foreach (var p in new[]
                {
                    Path.Combine(b, simple + ".dll"),
                    Path.Combine(b, "runtimes","win-x64","lib", simple + ".dll"),
                    Path.Combine(b, "runtimes","win","lib", simple + ".dll")
                })
                {
                    if (File.Exists(p)) { Assembly.LoadFrom(p); break; }
                }
            }
            catch { }
        }

        private static void AddNativeSearchPaths()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string[] nativeDirs =
                {
                    baseDir,
                    Path.Combine(baseDir, "runtimes","win-x64","native"),
                    Path.Combine(baseDir, "runtimes","win","native")
                };
                SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
                foreach (var d in nativeDirs.Distinct().Where(Directory.Exists))
                    AddDllDirectory(d);
            }
            catch { }
        }

        private const int LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetDefaultDllDirectories(int flags);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr AddDllDirectory(string path);

        // ================== NAV BUTTON (nuovo stile) ==================
        private sealed class NavButton : Control
        {
            private bool _selected;
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public bool Selected
            {
                get => _selected;
                set { _selected = value; Invalidate(); }
            }

            private bool _hover;
            private readonly StringFormat _fmt = new()
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            };
            private readonly Font _bold = new(new FontFamily("Segoe UI"), 10f, FontStyle.Bold);
            private readonly Font _reg = new(new FontFamily("Segoe UI"), 10f, FontStyle.Regular);

            public NavButton(string text)
            {
                Text = text;
                Dock = DockStyle.Fill;
                Margin = new Padding(8, 0, 8, 0);
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
                BackColor = Color.Black;   // non usato (non dipingiamo background), solo per sicurezza
                ForeColor = Color.Gainsboro;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); OnClick(EventArgs.Empty); }

            // niente riempimento dietro i bottoni => niente "riquadro grigio"
            protected override void OnPaintBackground(PaintEventArgs pevent) { /* lasciamo vedere il nero del parent */ }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                var rect = ClientRectangle;
                var pillRect = Rectangle.Inflate(rect, -6, -10);

                // Outline solo in hover (molto leggero), nessun fill.
                if (_hover && !_selected)
                {
                    using var path = RoundedRect(pillRect, 10);
                    using var pen = new Pen(Color.FromArgb(90, 120, 130, 140), 1.25f);
                    e.Graphics.DrawPath(pen, path);
                }

                // Testo: più chiaro se selezionato.
                using var tb = new SolidBrush(_selected ? Color.WhiteSmoke : Color.Gainsboro);
                e.Graphics.DrawString(Text, _selected ? _bold : _reg, tb, rect, _fmt);

                // Indicatore selezione: barra sottile neon, niente glow.
                if (_selected)
                {
                    var under = new Rectangle(pillRect.Left + 16, pillRect.Bottom - 2, pillRect.Width - 32, 3);
                    using var acc = new SolidBrush(Color.FromArgb(255, 64, 160, 255));
                    e.Graphics.FillRectangle(acc, under);
                }
            }
        }

        // ===== Controls anti-flicker =====
        private sealed class BufferedPanel : Panel
        {
            public BufferedPanel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                using var b = new SolidBrush(BackColor);
                e.Graphics.FillRectangle(b, ClientRectangle);
            }
        }

        private sealed class BufferedTable : TableLayoutPanel
        {
            public BufferedTable()
            {
                DoubleBuffered = true;
                BackColor = Color.Black;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                using var b = new SolidBrush(BackColor);
                e.Graphics.FillRectangle(b, ClientRectangle);
            }
        }

        private sealed class BufferedLabel : Label
        {
            public BufferedLabel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                using var b = new SolidBrush(BackColor);
                e.Graphics.FillRectangle(b, ClientRectangle);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        // ===== Utils grafici =====
        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
