#nullable enable
using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace CinecorePlayer2025
{
    /// <summary>
    /// Loopback + metriche:
    /// - RMS/Peak/PeakHold
    /// - Spettro dBFS (mid=(L+R)/2) con Hann e normalizzazione coerente:
    ///   amp_rms(k) = (|FFT[k]| * N) / den(k) / √2,
    ///   den(k)=0.5*ΣHann per 1..N/2-1, den(0)=den(N/2)=ΣHann
    /// - Correlazione Pearson DC-free [-1..+1]
    /// - Balance [-1..+1]
    /// - Crest (dB) = 20log10(peak/rms)
    /// - Oscilloscopio L/R (ring buffer)
    /// </summary>
    internal sealed class LoopbackSampler : IDisposable
    {
        // === Config ===
        private const int FFT_N = 1024;                 // log2=10
        private const int FFT_M = 10;
        private const int SCOPE_N = 1024;
        private const double PEAKHOLD_DECAY_DBPS = 6.0; // dB/s
        private const double SILENCE_DBFS = -65.0;
        private const double EPS = 1e-12;

        private WasapiLoopbackCapture? _cap;
        private volatile bool _running;

        // FFT: mid = (L+R)/2
        private readonly float[] _fftMid = new float[FFT_N];
        private int _fftPos;

        // Hann e normalizzazione per-bin
        private readonly float[] _win = BuildHann(FFT_N);
        private readonly double _sumWin;   // Σ Hann
        private readonly double _denMain;  // 0.5 * Σ Hann  (bin 1..N/2-1)
        private readonly double _denEdge;  // Σ Hann        (bin 0 e N/2)

        // Peak hold
        private float _peakHoldL, _peakHoldR;
        private DateTime _lastPeakUpdate = DateTime.MinValue;

        // Oscilloscopio
        private readonly float[] _scopeL = new float[SCOPE_N];
        private readonly float[] _scopeR = new float[SCOPE_N];
        private int _scopeW;

        // ===== Payload =====
        public sealed class AudioMetrics
        {
            public float RmsL, RmsR;
            public float PeakL, PeakR;
            public float PeakHoldL, PeakHoldR;
            public double[] SpectrumDb = Array.Empty<double>(); // lunghezza = N/2+1 (DC..Nyquist)
            public double Correlation;
            public double Balance;
            public double CrestL_dB, CrestR_dB;
            public float[] ScopeL = Array.Empty<float>();
            public float[] ScopeR = Array.Empty<float>();
            public int SampleRate;
            public bool IsSilent;
            public int FftLength;
        }

        public event Action<AudioMetrics>? OnMetrics;
        public event Action<float, float, float, float, double[]>? OnLevels;

        public LoopbackSampler()
        {
            double s = 0;
            for (int i = 0; i < FFT_N; i++) s += _win[i];
            _sumWin = s;
            _denMain = 0.5 * s;
            _denEdge = s;
        }

        public bool Start()
        {
            if (_running) return true;
            try
            {
                _cap = new WasapiLoopbackCapture();
                _cap.DataAvailable += OnData;
                _cap.StartRecording();
                _running = true;
                return true;
            }
            catch
            {
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            _running = false;
            try { if (_cap != null) _cap.DataAvailable -= OnData; } catch { }
            try { _cap?.StopRecording(); } catch { }
            try { _cap?.Dispose(); } catch { }
            _cap = null;
        }

        public void Dispose() => Stop();

        private void OnData(object? sender, WaveInEventArgs e)
        {
            if (!_running || e.BytesRecorded <= 0 || _cap == null) return;
            var wf = _cap.WaveFormat;
            int ch = Math.Max(1, wf.Channels);
            int bps = Math.Max(1, wf.BitsPerSample / 8);
            bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat;
            int frames = e.BytesRecorded / (bps * ch);
            if (frames <= 0) return;

            double sumSqL = 0, sumSqR = 0;
            double sumLL = 0, sumRR = 0, sumLR = 0;
            double sumL = 0, sumR = 0;
            float samplePeakL = 0f, samplePeakR = 0f;

            int step = Math.Max(1, frames / Math.Min(SCOPE_N, frames));

            for (int i = 0; i < frames; i++)
            {
                int idx = i * bps * ch;
                float l = ReadSample(e.Buffer, idx + 0 * bps, bps, isFloat);
                float r = ch > 1 ? ReadSample(e.Buffer, idx + 1 * bps, bps, isFloat) : l;
                l = Clamp1(l);
                r = Clamp1(r);

                sumSqL += l * l; sumSqR += r * r;
                float al = Math.Abs(l), ar = Math.Abs(r);
                if (al > samplePeakL) samplePeakL = al;
                if (ar > samplePeakR) samplePeakR = ar;

                sumLL += l * l; sumRR += r * r; sumLR += l * r;
                sumL += l; sumR += r;

                if (_fftPos < FFT_N)
                {
                    float mid = 0.5f * (l + r);
                    _fftMid[_fftPos] = mid * _win[_fftPos];
                    _fftPos++;
                }

                if ((i % step) == 0)
                {
                    _scopeL[_scopeW] = l;
                    _scopeR[_scopeW] = r;
                    _scopeW = (_scopeW + 1) % SCOPE_N;
                }
            }

            float rmsL = (float)Math.Sqrt(sumSqL / Math.Max(1, frames));
            float rmsR = (float)Math.Sqrt(sumSqR / Math.Max(1, frames));
            bool isSilent = (20.0 * Math.Log10(Math.Max(rmsL, rmsR) + EPS)) < SILENCE_DBFS;

            // Peak-hold
            var now = DateTime.UtcNow;
            if (_lastPeakUpdate == DateTime.MinValue) _lastPeakUpdate = now;
            double dt = (now - _lastPeakUpdate).TotalSeconds;
            _lastPeakUpdate = now;
            float decay = (float)Math.Pow(10, -PEAKHOLD_DECAY_DBPS * dt / 20.0);
            _peakHoldL = Math.Max(samplePeakL, _peakHoldL * decay);
            _peakHoldR = Math.Max(samplePeakR, _peakHoldR * decay);

            // Correlazione Pearson DC-free
            double corr;
            {
                double n = Math.Max(1, frames);
                double meanL = sumL / n, meanR = sumR / n;
                double sLL = sumLL - n * meanL * meanL;
                double sRR = sumRR - n * meanR * meanR;
                double sLR = sumLR - n * meanL * meanR;
                double denom = Math.Sqrt(Math.Max(1e-18, sLL * sRR));
                corr = denom > 0 ? Clamp(sLR / denom, -1.0, +1.0) : 0.0;
            }

            // Balance [-1..+1]
            double balance = 0.0, sum = rmsL + rmsR;
            if (sum > 1e-9) balance = Clamp((rmsR - rmsL) / sum, -1.0, +1.0);

            // Crest (dB)
            double crestL = ToDb(samplePeakL / Math.Max(1e-9f, rmsL));
            double crestR = ToDb(samplePeakR / Math.Max(1e-9f, rmsR));
            crestL = Clamp(crestL, 0.0, 24.0);
            crestR = Clamp(crestR, 0.0, 24.0);

            // Spettro (DC..Nyquist inclusi)
            double[] spectrumDb = Array.Empty<double>();

            if (_fftPos >= FFT_N)
            {
                var buf = new Complex[FFT_N];
                for (int i = 0; i < FFT_N; i++) { buf[i].X = _fftMid[i]; buf[i].Y = 0f; }

                FastFourierTransform.FFT(true, FFT_M, buf);

                int nyquist = FFT_N / 2;
                int bins = nyquist + 1;           // includi Nyquist
                spectrumDb = new double[bins];

                const double SQRT2 = 1.4142135623730951;

                for (int k = 0; k <= nyquist; k++)
                {
                    double re = buf[k].X;
                    double im = buf[k].Y;
                    double mag = Math.Sqrt(re * re + im * im);

                    // In alcune build il forward è 1/N → risali moltiplicando per N
                    mag *= FFT_N;

                    double den = (k == 0 || k == nyquist) ? _denEdge : _denMain;

                    // ampiezza RMS relativa al full-scale
                    double amp_rms = (mag / Math.Max(den, EPS)) / SQRT2;

                    double dbfs = 20.0 * Math.Log10(Math.Max(amp_rms, 1e-12));
                    spectrumDb[k] = dbfs < -120.0 ? -120.0 : (dbfs > 0.0 ? 0.0 : dbfs);
                }

                _fftPos = 0;
                Array.Clear(_fftMid, 0, _fftMid.Length);
            }

            // Copia scope
            var scopeL = new float[SCOPE_N];
            var scopeR = new float[SCOPE_N];
            int w = _scopeW;
            for (int i = 0; i < SCOPE_N; i++)
            {
                int j = (w + i) % SCOPE_N;
                scopeL[i] = _scopeL[j];
                scopeR[i] = _scopeR[j];
            }

            var metrics = new AudioMetrics
            {
                RmsL = rmsL,
                RmsR = rmsR,
                PeakL = samplePeakL,
                PeakR = samplePeakR,
                PeakHoldL = _peakHoldL,
                PeakHoldR = _peakHoldR,
                SpectrumDb = spectrumDb,           // lunghezza = N/2+1
                Correlation = corr,
                Balance = balance,
                CrestL_dB = crestL,
                CrestR_dB = crestR,
                ScopeL = scopeL,
                ScopeR = scopeR,
                SampleRate = wf.SampleRate,
                IsSilent = isSilent,
                FftLength = FFT_N
            };

            OnMetrics?.Invoke(metrics);
            OnLevels?.Invoke(rmsL, rmsR, _peakHoldL, _peakHoldR, spectrumDb);
        }

        // ===== Helpers =====

        private static float ReadSample(byte[] buf, int index, int bps, bool isFloat)
        {
            if (isFloat && bps == 4) return BitConverter.ToSingle(buf, index);
            return bps switch
            {
                2 => BitConverter.ToInt16(buf, index) / 32768f,
                3 => Read24(buf, index) / 8388608f,
                4 => BitConverter.ToInt32(buf, index) / 2147483648f,
                _ => 0f
            };
        }

        private static int Read24(byte[] b, int i)
        {
            int v = b[i] | (b[i + 1] << 8) | (b[i + 2] << 16);
            if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
            return v;
        }

        private static float Clamp1(float x) => x < -1f ? -1f : (x > 1f ? 1f : x);

        private static double Clamp(double v, double lo, double hi)
            => v < lo ? lo : (v > hi ? hi : v);

        private static double ToDb(double x) => 20.0 * Math.Log10(Math.Max(x, EPS));

        private static float[] BuildHann(int N)
        {
            var w = new float[N];
            for (int n = 0; n < N; n++)
                w[n] = 0.5f * (1f - (float)Math.Cos(2 * Math.PI * n / (N - 1)));
            return w;
        }
    }
}
