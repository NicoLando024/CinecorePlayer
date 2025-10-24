#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;

namespace CinecorePlayer2025
{
    /// <summary>
    /// UI WinForms con grafici LiveCharts (via reflection) per:
    /// - VU (headroom positivi 0..40 dB) + Peak-Hold con gate di silenzio
    /// - Spettro (dBFS) con scala Y dinamica smussata
    /// - Oscilloscopio (amp) con autoscale ±amp smussato
    /// - Crest factor (dB) corretto e non “floorato”
    /// - Balance (%) con scala ±10%
    /// - Correlazione (storico −1..+1)
    /// </summary>
    internal sealed class AudioMetersLiveCharts : UserControl
    {
        private const bool SHOW_VU_HEADROOM = true; // VU in dB di headroom (0..40). False = dBFS negativi.

        // Charts & series
        private object? _vuChart, _specChart, _scopeChart, _crestChart, _balanceChart, _corrChart;
        private object? _vuRmsSeries, _vuPkSeries;        // Column + Scatter
        private object? _specSeries;                      // Line (dB)
        private object? _scopeLSeries, _scopeRSeries;     // Line (time)
        private object? _crestSeries;                     // Column L/R (dB)
        private object? _balanceSeries;                   // Column unico (percento)
        private object? _corrSeries;                      // Line (storico)

        private object? _xAxisVu, _yAxisVu, _xAxisSp, _yAxisSp, _xAxisSc, _yAxisSc, _xAxisCr, _yAxisCr, _xAxisBal, _yAxisBal, _xAxisCorr, _yAxisCorr;
        private bool _ok;

        private readonly TableLayoutPanel _layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Color.Black
        };
        private readonly Label _msg = new()
        {
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Gainsboro,
            BackColor = Color.Black,
            Visible = false
        };

        // Storici / stati per scale dinamiche
        private readonly double[] _corrHist = new double[180];
        private int _corrW;

        // Stato per etichette spettro
        private int _lastSpectrumSr;
        private int _lastSpectrumBins;
        private int _lastFftLen;

        private double _spYMin = -60;   // min Y spettro (smoothed)
        private double _scVisAmp = 0.5; // metà ampiezza oscillo (smoothed)

        // ===== Assembly bootstrap =====
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

            // Layout
            _layout.RowStyles.Clear();
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 18)); // VU
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 32)); // Spettro
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 22)); // Oscilloscopio
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 14)); // Crest
            _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 14)); // Balance + Corr
            Controls.Add(_layout);
            Controls.Add(_msg); _msg.BringToFront();

            _ok = InitCharts();
            if (!_ok)
            {
                _msg.Text = "LiveCharts non trovate: copia LiveChartsCore*, SkiaSharp* e HarfBuzzSharp vicino all'eseguibile.";
                _msg.Visible = true;
            }
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

            // --- Spettro ---
            // --- Spettro ---
            if (m.SpectrumDb.Length > 0)
            {
                // 1) Opzione: nascondi il bin di Nyquist (ultimo punto)
                var binsAll = m.SpectrumDb.Length;          // = N/2 + 1 (DC..Nyquist)
                var binsDraw = Math.Max(1, binsAll - 1);    // disegna 0..N/2-1
                var specDraw = new double[binsDraw];
                Array.Copy(m.SpectrumDb, specDraw, binsDraw);
                SetProp(_specSeries!, "Values", specDraw);

                // 2) Etichette X su dominio indice (0..binsDraw-1), mapping k→Hz = k*fs/FFT_N
                if (m.SampleRate > 0)
                {
                    int fftN = (m.FftLength > 0) ? m.FftLength : (binsAll - 1) * 2; // fallback
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

                // 3) Scala Y dinamica (come prima)
                double minDb = specDraw.Min();
                double targetMin = Math.Max(-120, Math.Min(-10, Math.Floor(minDb / 5.0) * 5.0));
                _spYMin = 0.85 * _spYMin + 0.15 * targetMin;
                TrySet(_yAxisSp!, "MinLimit", _spYMin);
                TrySet(_yAxisSp!, "MaxLimit", 0d);
            }

            // --- Oscilloscopio ---
            if (m.ScopeL.Length > 0)
            {
                SetProp(_scopeLSeries!, "Values", m.ScopeL.Select(v => (double)v).ToArray());
                SetProp(_scopeRSeries!, "Values", m.ScopeR.Select(v => (double)v).ToArray());

                // Autoscale Y (±ampiezza*1.2), smussato
                double maxAbs = Math.Max(
                    m.ScopeL.Length > 0 ? m.ScopeL.Select(v => Math.Abs((double)v)).Max() : 0.0,
                    m.ScopeR.Length > 0 ? m.ScopeR.Select(v => Math.Abs((double)v)).Max() : 0.0);
                double targetHalf = Math.Clamp(maxAbs * 1.2, 0.2, 1.0);
                _scVisAmp = 0.85 * _scVisAmp + 0.15 * targetHalf;
                TrySet(_yAxisSc!, "MinLimit", -_scVisAmp);
                TrySet(_yAxisSc!, "MaxLimit", +_scVisAmp);
            }

            // --- Crest factor (dB) ---
            double crestLvis = m.IsSilent ? 0.0 : Math.Clamp(m.CrestL_dB, 0.0, 24.0);
            double crestRvis = m.IsSilent ? 0.0 : Math.Clamp(m.CrestR_dB, 0.0, 24.0);
            SetProp(_crestSeries!, "Values", new double[] { crestLvis, crestRvis });

            // --- Balance in % ---
            double balPct = Math.Clamp(m.Balance * 100.0, -10.0, 10.0);
            SetProp(_balanceSeries!, "Values", new double[] { balPct });

            // --- Correlazione (storico) ---
            _corrHist[_corrW] = m.Correlation;
            _corrW = (_corrW + 1) % _corrHist.Length;
            var corrVals = new double[_corrHist.Length];
            for (int i = 0; i < corrVals.Length; i++)
                corrVals[i] = _corrHist[(_corrW + i) % _corrHist.Length];
            SetProp(_corrSeries!, "Values", corrVals);

            // Messaggio info (silenzio)
            if (m.IsSilent) SetInfoMessage("Silenzio / floor");
            else SetInfoMessage(null);

            // Redraw
            CallMethod(_vuChart!, "Update");
            CallMethod(_specChart!, "Update");
            CallMethod(_scopeChart!, "Update");
            CallMethod(_crestChart!, "Update");
            CallMethod(_balanceChart!, "Update");
            CallMethod(_corrChart!, "Update");
        }

        /// <summary>
        /// Compat: aggiorna da firma legacy (RMS/PeakHold + spettro).
        /// </summary>
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
            _msg.Text = string.IsNullOrWhiteSpace(msg) ? "" : msg;
            _msg.Visible = !string.IsNullOrWhiteSpace(msg);
            if (_msg.Visible) _msg.BringToFront();
        }

        // ===== Charts setup =====
        private bool InitCharts()
        {
            try
            {
                var chartT = Resolve("LiveChartsCore.SkiaSharpView.WinForms.CartesianChart, LiveChartsCore.SkiaSharpView.WinForms")!;
                var axisT = Resolve("LiveChartsCore.SkiaSharpView.Axis, LiveChartsCore.SkiaSharpView")!;
                var colSeriesT = Resolve("LiveChartsCore.SkiaSharpView.ColumnSeries`1[[System.Double, System.Private.CoreLib]], LiveChartsCore.SkiaSharpView")!;
                var lineSeriesT = Resolve("LiveChartsCore.SkiaSharpView.LineSeries`1[[System.Double, System.Private.CoreLib]], LiveChartsCore.SkiaSharpView")!;
                var scatSeriesT = Resolve("LiveChartsCore.SkiaSharpView.ScatterSeries`1[[System.Double, System.Private.CoreLib]], LiveChartsCore.SkiaSharpView")!;
                var solidPaintT = Resolve("LiveChartsCore.SkiaSharpView.Painting.SolidColorPaint, LiveChartsCore.SkiaSharpView")!;
                var iSeriesT = Resolve("LiveChartsCore.ISeries, LiveChartsCore")!;
                var legendPosT = Resolve("LiveChartsCore.Measure.LegendPosition, LiveChartsCore")!;
                var tipPosT = Resolve("LiveChartsCore.Measure.TooltipPosition, LiveChartsCore")!;

                // Paints
                var grid = MakePaint(solidPaintT, 255, 255, 255, 28, 1.0);
                var axisTxt = MakePaint(solidPaintT, 220, 220, 220, 200);
                var vuFill = MakePaint(solidPaintT, 15, 160, 255, 220);
                var pkFill = MakePaint(solidPaintT, 255, 220, 0, 220);
                var spLine = MakePaint(solidPaintT, 0, 180, 255, 220, 2.0);
                var spFill = MakePaint(solidPaintT, 0, 180, 255, 18);
                var scLLine = MakePaint(solidPaintT, 0, 200, 255, 220, 1.6);
                var scRLine = MakePaint(solidPaintT, 255, 120, 60, 220, 1.6);
                var crestFi = MakePaint(solidPaintT, 40, 220, 140, 220);
                var balFill = MakePaint(solidPaintT, 200, 120, 255, 220);
                var corrLin = MakePaint(solidPaintT, 0, 220, 120, 220, 2.0);

                // === VU ===
                _vuChart = Activator.CreateInstance(chartT)!;
                var vu = (Control)_vuChart; vu.Dock = DockStyle.Fill; vu.BackColor = Color.Black;
                TrySetEnum(_vuChart, "LegendPosition", legendPosT, "Hidden");
                TrySetEnum(_vuChart, "TooltipPosition", tipPosT, "Top");

                _vuRmsSeries = Activator.CreateInstance(colSeriesT)!;
                SetProp(_vuRmsSeries, "Name", SHOW_VU_HEADROOM ? "Headroom (dB)" : "RMS (dBFS)");
                SetProp(_vuRmsSeries, "Values", new double[] { 0, 0 });
                TrySet(_vuRmsSeries, "Fill", vuFill);
                TrySet(_vuRmsSeries, "Stroke", null);
                TrySet(_vuRmsSeries, "Rx", 6d); TrySet(_vuRmsSeries, "Ry", 6d);
                TrySet(_vuRmsSeries, "MaxBarWidth", 140d);
                TrySet(_vuRmsSeries, "Padding", 0.25d);
                TrySet(_vuRmsSeries, "DataLabelsPaint", null);
                TrySet(_vuRmsSeries, "DataLabelsSize", 0d);

                _vuPkSeries = Activator.CreateInstance(scatSeriesT)!;
                SetProp(_vuPkSeries, "Name", SHOW_VU_HEADROOM ? "Peak-hold (dB)" : "Peak-hold (dBFS)");
                SetProp(_vuPkSeries, "Values", new double[] { 0, 0 });
                TrySet(_vuPkSeries, "GeometrySize", 8d);
                TrySet(_vuPkSeries, "Fill", pkFill);
                TrySet(_vuPkSeries, "Stroke", null);

                _xAxisVu = Activator.CreateInstance(axisT)!;
                SetProp(_xAxisVu, "Labels", new[] { "L", "R" });
                TrySet(_xAxisVu, "LabelsPaint", axisTxt);
                TrySet(_xAxisVu, "SeparatorsPaint", null);
                TrySet(_xAxisVu, "TicksPaint", null);

                _yAxisVu = Activator.CreateInstance(axisT)!;
                if (SHOW_VU_HEADROOM)
                {
                    SetProp(_yAxisVu, "MinLimit", 0d); SetProp(_yAxisVu, "MaxLimit", 40d);
                    TrySetLabeler(_yAxisVu, v => v.ToString("0"));
                }
                else
                {
                    SetProp(_yAxisVu, "MinLimit", -50d); SetProp(_yAxisVu, "MaxLimit", 0d);
                }
                TrySet(_yAxisVu, "LabelsPaint", axisTxt);
                TrySet(_yAxisVu, "SeparatorsPaint", grid);

                SetProp(_vuChart, "Series", CreateArray(iSeriesT, _vuRmsSeries, _vuPkSeries));
                SetProp(_vuChart, "XAxes", CreateArray(axisT, _xAxisVu));
                SetProp(_vuChart, "YAxes", CreateArray(axisT, _yAxisVu));

                // === Spettro ===
                _specChart = Activator.CreateInstance(chartT)!;
                var sp = (Control)_specChart; sp.Dock = DockStyle.Fill; sp.BackColor = Color.Black;
                TrySetEnum(_specChart, "LegendPosition", legendPosT, "Hidden");
                TrySetEnum(_specChart, "TooltipPosition", tipPosT, "Top");

                _specSeries = Activator.CreateInstance(lineSeriesT)!;
                SetProp(_specSeries, "Name", "Spettro (dBFS)");
                SetProp(_specSeries, "Values", new double[] { -55, -55, -55 });
                TrySet(_specSeries, "GeometrySize", 0d);
                TrySet(_specSeries, "LineSmoothness", 0d);
                TrySet(_specSeries, "Stroke", spLine);
                TrySet(_specSeries, "Fill", spFill);

                _xAxisSp = Activator.CreateInstance(axisT)!;
                TrySet(_xAxisSp, "LabelsPaint", axisTxt);
                TrySet(_xAxisSp, "SeparatorsPaint", null);
                TrySet(_xAxisSp, "TicksPaint", null);

                _yAxisSp = Activator.CreateInstance(axisT)!;
                SetProp(_yAxisSp, "MinLimit", -55d); SetProp(_yAxisSp, "MaxLimit", 0d); // default; poi dinamico
                TrySet(_yAxisSp, "LabelsPaint", axisTxt);
                TrySet(_yAxisSp, "SeparatorsPaint", grid);
                TrySet(_yAxisSp, "TicksPaint", null);

                SetProp(_specChart, "Series", CreateArray(iSeriesT, _specSeries));
                SetProp(_specChart, "XAxes", CreateArray(axisT, _xAxisSp));
                SetProp(_specChart, "YAxes", CreateArray(axisT, _yAxisSp));

                // === Oscilloscopio ===
                _scopeChart = Activator.CreateInstance(chartT)!;
                var sc = (Control)_scopeChart; sc.Dock = DockStyle.Fill; sc.BackColor = Color.Black;
                TrySetEnum(_scopeChart, "LegendPosition", legendPosT, "Hidden");
                TrySetEnum(_scopeChart, "TooltipPosition", tipPosT, "Top");

                _scopeLSeries = Activator.CreateInstance(lineSeriesT)!;
                _scopeRSeries = Activator.CreateInstance(lineSeriesT)!;
                SetProp(_scopeLSeries, "Name", "L");
                SetProp(_scopeRSeries, "Name", "R");
                TrySet(_scopeLSeries, "GeometrySize", 0d);
                TrySet(_scopeRSeries, "GeometrySize", 0d);
                TrySet(_scopeLSeries, "LineSmoothness", 0d);
                TrySet(_scopeRSeries, "LineSmoothness", 0d);
                TrySet(_scopeLSeries, "Stroke", MakePaint(solidPaintT, 0, 200, 255, 220, 1.6));
                TrySet(_scopeRSeries, "Stroke", MakePaint(solidPaintT, 255, 120, 60, 220, 1.6));
                TrySet(_scopeLSeries, "Fill", null);
                TrySet(_scopeRSeries, "Fill", null);

                _xAxisSc = Activator.CreateInstance(axisT)!;
                TrySet(_xAxisSc, "LabelsPaint", null);
                TrySet(_xAxisSc, "SeparatorsPaint", null);
                TrySet(_xAxisSc, "TicksPaint", null);

                _yAxisSc = Activator.CreateInstance(axisT)!;
                SetProp(_yAxisSc, "MinLimit", -0.5d); SetProp(_yAxisSc, "MaxLimit", +0.5d); // default, poi dinamico
                TrySet(_yAxisSc, "LabelsPaint", axisTxt);
                TrySet(_yAxisSc, "SeparatorsPaint", grid);
                TrySet(_yAxisSc, "TicksPaint", null);

                SetProp(_scopeChart, "Series", CreateArray(iSeriesT, _scopeLSeries, _scopeRSeries));
                SetProp(_scopeChart, "XAxes", CreateArray(axisT, _xAxisSc));
                SetProp(_scopeChart, "YAxes", CreateArray(axisT, _yAxisSc));

                // === Crest factor ===
                _crestChart = Activator.CreateInstance(chartT)!;
                var cr = (Control)_crestChart; cr.Dock = DockStyle.Fill; cr.BackColor = Color.Black;
                TrySetEnum(_crestChart, "LegendPosition", legendPosT, "Hidden");
                TrySetEnum(_crestChart, "TooltipPosition", tipPosT, "Top");

                _crestSeries = Activator.CreateInstance(colSeriesT)!;
                SetProp(_crestSeries, "Name", "Crest (dB)");
                SetProp(_crestSeries, "Values", new double[] { 0, 0 });
                TrySet(_crestSeries, "Fill", MakePaint(solidPaintT, 40, 220, 140, 220));
                TrySet(_crestSeries, "Stroke", null);
                TrySet(_crestSeries, "Rx", 6d); TrySet(_crestSeries, "Ry", 6d);
                TrySet(_crestSeries, "MaxBarWidth", 120d);
                TrySet(_crestSeries, "Padding", 0.25d);

                _xAxisCr = Activator.CreateInstance(axisT)!;
                SetProp(_xAxisCr, "Labels", new[] { "L", "R" });
                TrySet(_xAxisCr, "LabelsPaint", axisTxt);
                TrySet(_xAxisCr, "SeparatorsPaint", null);
                TrySet(_xAxisCr, "TicksPaint", null);

                _yAxisCr = Activator.CreateInstance(axisT)!;
                SetProp(_yAxisCr, "MinLimit", 0d); SetProp(_yAxisCr, "MaxLimit", 24d);
                TrySet(_yAxisCr, "LabelsPaint", axisTxt);
                TrySet(_yAxisCr, "SeparatorsPaint", grid);
                TrySet(_yAxisCr, "TicksPaint", null);

                SetProp(_crestChart, "Series", CreateArray(Resolve("LiveChartsCore.ISeries, LiveChartsCore")!, _crestSeries));
                SetProp(_crestChart, "XAxes", CreateArray(axisT, _xAxisCr));
                SetProp(_crestChart, "YAxes", CreateArray(axisT, _yAxisCr));

                // === Balance (%) ===
                _balanceChart = Activator.CreateInstance(chartT)!;
                var ba = (Control)_balanceChart; ba.Dock = DockStyle.Fill; ba.BackColor = Color.Black;
                TrySetEnum(_balanceChart, "LegendPosition", legendPosT, "Hidden");
                TrySetEnum(_balanceChart, "TooltipPosition", tipPosT, "Top");

                _balanceSeries = Activator.CreateInstance(colSeriesT)!;
                SetProp(_balanceSeries, "Name", "Balance (%)");
                SetProp(_balanceSeries, "Values", new double[] { 0.0 });
                TrySet(_balanceSeries, "Fill", MakePaint(solidPaintT, 200, 120, 255, 220));
                TrySet(_balanceSeries, "Stroke", null);
                TrySet(_balanceSeries, "Rx", 6d); TrySet(_balanceSeries, "Ry", 6d);
                TrySet(_balanceSeries, "MaxBarWidth", 220d);
                TrySet(_balanceSeries, "Padding", 0.2d);

                _xAxisBal = Activator.CreateInstance(axisT)!;
                SetProp(_xAxisBal, "Labels", new[] { "L ← Balance → R" });
                TrySet(_xAxisBal, "LabelsPaint", axisTxt);
                TrySet(_xAxisBal, "SeparatorsPaint", null);
                TrySet(_xAxisBal, "TicksPaint", null);

                _yAxisBal = Activator.CreateInstance(axisT)!;
                SetProp(_yAxisBal, "MinLimit", -10d); SetProp(_yAxisBal, "MaxLimit", +10d);
                TrySet(_yAxisBal, "LabelsPaint", axisTxt);
                TrySet(_yAxisBal, "SeparatorsPaint", grid);
                TrySetLabeler(_yAxisBal, v => v.ToString("+0;-0;0") + "%");

                SetProp(_balanceChart, "Series", CreateArray(Resolve("LiveChartsCore.ISeries, LiveChartsCore")!, _balanceSeries));
                SetProp(_balanceChart, "XAxes", CreateArray(axisT, _xAxisBal));
                SetProp(_balanceChart, "YAxes", CreateArray(axisT, _yAxisBal));

                // === Correlazione ===
                _corrChart = Activator.CreateInstance(chartT)!;
                var co = (Control)_corrChart; co.Dock = DockStyle.Fill; co.BackColor = Color.Black;
                TrySetEnum(_corrChart, "LegendPosition", legendPosT, "Hidden");
                TrySetEnum(_corrChart, "TooltipPosition", tipPosT, "Top");

                _corrSeries = Activator.CreateInstance(lineSeriesT)!;
                SetProp(_corrSeries, "Name", "ρ (corr)");
                SetProp(_corrSeries, "Values", Enumerable.Repeat(0.0, _corrHist.Length).ToArray());
                TrySet(_corrSeries, "GeometrySize", 0d);
                TrySet(_corrSeries, "LineSmoothness", 0d);
                TrySet(_corrSeries, "Stroke", MakePaint(solidPaintT, 0, 220, 120, 220, 2.0));
                TrySet(_corrSeries, "Fill", null);

                _xAxisCorr = Activator.CreateInstance(axisT)!;
                TrySet(_xAxisCorr, "LabelsPaint", null);
                TrySet(_xAxisCorr, "SeparatorsPaint", null);
                TrySet(_xAxisCorr, "TicksPaint", null);

                _yAxisCorr = Activator.CreateInstance(axisT)!;
                SetProp(_yAxisCorr, "MinLimit", -1d); SetProp(_yAxisCorr, "MaxLimit", +1d);
                TrySet(_yAxisCorr, "LabelsPaint", MakePaint(solidPaintT, 220, 220, 220, 200));
                TrySet(_yAxisCorr, "SeparatorsPaint", grid);
                TrySetLabeler(_yAxisCorr, v => v.ToString("0.0"));
                TrySet(_yAxisCorr, "TicksPaint", null);

                // Layout con titoli
                _layout.Controls.Add(MakeTitledPanel("VU L/R — Headroom & Peak-Hold", (Control)_vuChart), 0, 0);
                _layout.Controls.Add(MakeTitledPanel("Spettro — mid (L+R)/2 in dBFS", (Control)_specChart), 0, 1);
                _layout.Controls.Add(MakeTitledPanel("Oscilloscopio — L/R (amp)", (Control)_scopeChart), 0, 2);
                _layout.Controls.Add(MakeTitledPanel("Crest factor — L/R (dB)", (Control)_crestChart), 0, 3);

                var pair = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Black, Padding = new Padding(0) };
                pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                pair.Controls.Add(MakeTitledPanel("Balance — L↔R (%)", (Control)_balanceChart), 0, 0);
                pair.Controls.Add(MakeTitledPanel("Correlazione — storico (−1…+1)", (Control)_corrChart), 1, 0);
                _layout.Controls.Add(pair, 0, 4);

                return true;
            }
            catch { return false; }
        }

        // ===== Helpers =====
        private static double ToDb(double v) => Math.Clamp(20.0 * Math.Log10(Math.Max(v, 1e-12)), -120.0, 0.0);

        private static void CallMethod(object target, string methodName)
        {
            try { target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)?.Invoke(target, null); }
            catch { }
        }

        private static void TrySetEnum(object target, string prop, Type enumType, string member)
        {
            try { SetProp(target, prop, Enum.Parse(enumType, member)); } catch { }
        }

        private static void TrySetLabeler(object axis, Func<double, string> f)
        {
            try { axis.GetType().GetProperty("Labeler", BindingFlags.Instance | BindingFlags.Public)?.SetValue(axis, f); }
            catch { }
        }

        private static Control MakeTitledPanel(string title, Control inner)
        {
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Padding = new Padding(8, 22, 8, 8) };
            var lbl = new Label
            {
                Text = title,
                Dock = DockStyle.Top,
                Height = 18,
                ForeColor = Color.Gainsboro,
                BackColor = Color.Black,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 2, 0, 0)
            };
            host.Controls.Add(inner);
            host.Controls.Add(lbl);
            inner.Dock = DockStyle.Fill;
            return host;
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

        private static object MakePaint(Type solidPaintT, byte r, byte g, byte b, byte a, double strokeThickness = 0)
        {
            var skColorT = Resolve("SkiaSharp.SKColor, SkiaSharp")!;
            var color = Activator.CreateInstance(skColorT, new object[] { r, g, b, a })!;
            var paint = Activator.CreateInstance(solidPaintT, new object[] { color })!;
            TrySet(paint, "StrokeThickness", strokeThickness);
            return paint;
        }

        private static Type? Resolve(string aqn)
        {
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
    }
}
