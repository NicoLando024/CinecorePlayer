#nullable enable

using CinecorePlayer2025;
using DirectShowLib;
using FFmpeg.AutoGen;
using System;
using System.Collections.Concurrent;
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
using VRChoice = global::CinecorePlayer2025.VideoRendererChoice;
using HDRMode = global::CinecorePlayer2025.HdrMode;
using S3DMode = global::CinecorePlayer2025.Stereo3DMode;

namespace CinecorePlayer2025
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "win-x64");
            if (Directory.Exists(local)) ffmpeg.RootPath = local;

            ApplicationConfiguration.Initialize();
            Application.Run(new PlayerForm());
        }
    }

    // ======= Helpers disegno (rounded rectangles) =======
    internal static class GraphicsExtensions
    {
        public static void FillRoundedRectangle(this Graphics g, Brush brush, Rectangle rect, Size radius)
        {
            using var gp = Rounded(rect, radius);
            g.FillPath(brush, gp);
        }
        public static void DrawRoundedRectangle(this Graphics g, Pen pen, Rectangle rect, Size radius)
        {
            using var gp = Rounded(rect, radius);
            g.DrawPath(pen, gp);
        }
        private static GraphicsPath Rounded(Rectangle r, Size rad)
        {
            int rx = Math.Max(0, rad.Width);
            int ry = Math.Max(0, rad.Height);
            var gp = new GraphicsPath();
            if (rx == 0 || ry == 0) { gp.AddRectangle(r); return gp; }
            gp.AddArc(r.X, r.Y, rx, ry, 180, 90);
            gp.AddArc(r.Right - rx, r.Y, rx, ry, 270, 90);
            gp.AddArc(r.Right - rx, r.Bottom - ry, rx, ry, 0, 90);
            gp.AddArc(r.X, r.Bottom - ry, rx, ry, 90, 90);
            gp.CloseFigure();
            return gp;
        }
    }

    // ======= Pannello overlay trasparente inline (stesso HWND del video) =======
    internal sealed class InlineOverlayPanel : Panel
    {
        public InlineOverlayPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } // WS_EX_TRANSPARENT
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
    }

    // ======= DEBUG CORE (file + ring buffer + batch writer) =======
    static class Dbg
    {
        public enum LogLevel { Error = 0, Warn = 1, Info = 2, Verbose = 3 }
        public static LogLevel Level = LogLevel.Info;

        static readonly object _lock = new();
        static readonly Queue<string> _ring = new();
        static readonly int _maxLines = 1500;

        static readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "cinecore_debug.log");
        static readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>());
        static readonly Thread _writer;

        static Dbg()
        {
            try { File.AppendAllText(_logPath, $"\r\n==== RUN {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\r\n"); } catch { }
            _writer = new Thread(WriterProc) { IsBackground = true, Name = "DbgWriter" };
            _writer.Start();
        }

        static void WriterProc()
        {
            var batch = new List<string>(128);
            while (true)
            {
                try
                {
                    batch.Clear();
                    if (_queue.TryTake(out var first, 200))
                    {
                        batch.Add(first);
                        while (_queue.TryTake(out var line))
                        {
                            batch.Add(line);
                            if (batch.Count >= 256) break;
                        }
                    }
                    if (batch.Count > 0) File.AppendAllLines(_logPath, batch);
                }
                catch { Thread.Sleep(300); }
            }
        }

        static void Enqueue(string line)
        {
            lock (_lock)
            {
                _ring.Enqueue(line);
                while (_ring.Count > _maxLines) _ring.Dequeue();
            }
            try { Debug.WriteLine(line); } catch { }
            try { _queue.Add(line); } catch { }
        }

        static string Stamp(string msg) => $"{DateTime.Now:HH:mm:ss.fff} | {msg}";
        public static void Log(string msg, LogLevel lvl = LogLevel.Info) { if (lvl > Level) return; Enqueue(Stamp(msg)); }
        public static void Warn(string msg) => Log("WARN: " + msg, LogLevel.Warn);
        public static void Error(string msg) => Log("ERROR: " + msg, LogLevel.Error);
        public static string[] Snapshot() { lock (_lock) return _ring.ToArray(); }

        public static string Hex(Guid g) => g.ToString("B").ToUpperInvariant();
    }

    // ======= ENUM & MODALITÀ =======
    public enum HdrMode { Auto, Off }                   // Off = forza SDR (tone-map con madVR/MPCVR)
    public enum Stereo3DMode { None, SBS, TAB }
    public enum VideoRendererChoice { MADVR, MPCVR, EVR }

    // ======= MediaProbe (FFmpeg) =======
    public static unsafe class MediaProbe
    {
        public sealed class Result
        {
            public double Duration;
            public bool HasVideo;
            public int Width, Height, VideoBits;
            public AVCodecID VideoCodec;
            public AVPixelFormat PixFmt;
            public AVColorPrimaries Primaries;
            public AVColorTransferCharacteristic Transfer;
            public AVCodecID AudioCodec;
            public int AudioRate, AudioChannels, AudioBits;
            public string AudioLayoutText = "";
            public bool AudioLooksObjectBased;
            public bool IsHdr;
            public List<(string title, double start)> Chapters = new();
        }

        static MediaProbe() { try { ffmpeg.avformat_network_init(); } catch { } }

        public static Result Probe(string path)
        {
            AVFormatContext* fmt = null;
            if (ffmpeg.avformat_open_input(&fmt, path, null, null) != 0)
                throw new ApplicationException("Impossibile aprire il file.");
            try
            {
                if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                    throw new ApplicationException("Stream info non trovate.");

                var r = new Result { Duration = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0 };

                for (int i = 0; i < fmt->nb_streams; i++)
                {
                    var st = fmt->streams[i];
                    var par = st->codecpar;

                    if (par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        r.HasVideo = true;
                        r.Width = par->width; r.Height = par->height;
                        r.VideoCodec = par->codec_id;
                        r.PixFmt = (AVPixelFormat)par->format;
                        r.VideoBits = GuessVideoBits(r.PixFmt, par->bits_per_raw_sample);
                        r.Primaries = par->color_primaries; r.Transfer = par->color_trc;
                    }
                    else if (par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                    {
                        r.AudioCodec = par->codec_id;
                        r.AudioRate = par->sample_rate;
                        int ch = (int)par->ch_layout.nb_channels; if (ch <= 0) ch = 2;
                        r.AudioChannels = ch;
                        r.AudioBits = GuessAudioBits(par->codec_id, par->format, par->bits_per_coded_sample, par->bits_per_raw_sample);

                        try
                        {
                            var buf = stackalloc sbyte[128];
                            ffmpeg.av_channel_layout_describe(&par->ch_layout, (byte*)buf, 128);
                            r.AudioLayoutText = Marshal.PtrToStringAnsi((IntPtr)buf) ?? "";
                        }
                        catch { r.AudioLayoutText = ""; }

                        // Heuristica: Atmos/E-AC-3 JOC o TrueHD object-based
                        if (par->codec_id == AVCodecID.AV_CODEC_ID_TRUEHD || par->codec_id == AVCodecID.AV_CODEC_ID_EAC3)
                        {
                            r.AudioLooksObjectBased = r.AudioChannels >= 6;
                        }
                    }
                }

                // Capitoli
                for (int i = 0; i < fmt->nb_chapters; i++)
                {
                    var ch = fmt->chapters[i];
                    double tb = ffmpeg.av_q2d(ch->time_base);
                    double start = ch->start * tb;
                    string title = "Capitolo " + (i + 1);
                    var tag = ffmpeg.av_dict_get(ch->metadata, "title", null, 0);
                    if (tag != null) title = Marshal.PtrToStringAnsi((IntPtr)tag->value) ?? title;
                    r.Chapters.Add((title, Math.Max(0, start)));
                }

                // HDR heuristic compatibile
                r.IsHdr = IsHdrLike(r.Transfer, r.Primaries, r.VideoBits);
                return r;
            }
            finally { if (fmt != null) { var l = fmt; ffmpeg.avformat_close_input(&l); } }

            static int GuessVideoBits(AVPixelFormat fmt, int bprs)
            { if (bprs > 0) return bprs; var d = ffmpeg.av_pix_fmt_desc_get(fmt); return d != null ? d->comp[0].depth : 8; }

            static int GuessAudioBits(AVCodecID id, int parFmt, int coded, int raw)
            {
                if (raw > 0) return raw; if (coded > 0) return coded;
                int fromCodec = ffmpeg.av_get_bits_per_sample(id);
                if (fromCodec > 0) return fromCodec;
                if (id == AVCodecID.AV_CODEC_ID_PCM_S16LE || id == AVCodecID.AV_CODEC_ID_PCM_S16BE) return 16;
                if (id == AVCodecID.AV_CODEC_ID_PCM_S24LE || id == AVCodecID.AV_CODEC_ID_PCM_S24BE) return 24;
                if (id == AVCodecID.AV_CODEC_ID_PCM_F32LE || id == AVCodecID.AV_CODEC_ID_PCM_F32BE) return 32;
                return 0;
            }

            static bool IsHdrLike(AVColorTransferCharacteristic trc, AVColorPrimaries prim, int bits)
            {
                bool pq = trc == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084;
                bool hlg = trc == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;
                bool bt2020 = prim == AVColorPrimaries.AVCOL_PRI_BT2020;
                return pq || hlg || (bt2020 && bits >= 10);
            }
        }

        public static bool IsPassthroughCandidate(AVCodecID id) =>
            id == AVCodecID.AV_CODEC_ID_TRUEHD || id == AVCodecID.AV_CODEC_ID_EAC3 ||
            id == AVCodecID.AV_CODEC_ID_AC3 || id == AVCodecID.AV_CODEC_ID_DTS;
    }

    // ======= Thumbnailer (FFmpeg) =======
    internal sealed unsafe class Thumbnailer : IDisposable
    {
        private AVFormatContext* _fmt;
        private int _vindex = -1;
        private AVCodecContext* _dec;
        private SwsContext* _sws;

        public void Open(string path)
        {
            Close();
            AVFormatContext* f = null;
            if (ffmpeg.avformat_open_input(&f, path, null, null) != 0) throw new ApplicationException("Thumb open failed");
            if (ffmpeg.avformat_find_stream_info(f, null) < 0) { ffmpeg.avformat_close_input(&f); throw new ApplicationException("Thumb si failed"); }
            for (int i = 0; i < f->nb_streams; i++)
                if (f->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO) { _vindex = i; break; }
            if (_vindex < 0) { ffmpeg.avformat_close_input(&f); throw new ApplicationException("No video"); }
            var par = f->streams[_vindex]->codecpar;
            AVCodec* codec = ffmpeg.avcodec_find_decoder(par->codec_id);
            if (codec == null) { ffmpeg.avformat_close_input(&f); throw new ApplicationException("No decoder"); }
            AVCodecContext* dec = ffmpeg.avcodec_alloc_context3(codec);
            if (ffmpeg.avcodec_parameters_to_context(dec, par) < 0) { ffmpeg.avformat_close_input(&f); throw new ApplicationException("Ctx copy fail"); }
            if (ffmpeg.avcodec_open2(dec, codec, null) < 0) { ffmpeg.avformat_close_input(&f); ffmpeg.avcodec_free_context(&dec); throw new ApplicationException("Open dec fail"); }
            _fmt = f; _dec = dec;
        }

        public Bitmap? Get(double seconds, int maxW = 360)
        {
            if (_fmt == null || _vindex < 0 || _dec == null) return null;
            var st = _fmt->streams[_vindex];
            long ts = (long)(seconds / ffmpeg.av_q2d(st->time_base)); if (ts < 0) ts = 0;
            ffmpeg.av_seek_frame(_fmt, _vindex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
            ffmpeg.avcodec_flush_buffers(_dec);
            AVPacket* pkt = ffmpeg.av_packet_alloc(); AVFrame* frame = ffmpeg.av_frame_alloc(); Bitmap? bmp = null;
            try
            {
                while (ffmpeg.av_read_frame(_fmt, pkt) >= 0)
                {
                    if (pkt->stream_index != _vindex) { ffmpeg.av_packet_unref(pkt); continue; }
                    if (ffmpeg.avcodec_send_packet(_dec, pkt) < 0) { ffmpeg.av_packet_unref(pkt); continue; }
                    ffmpeg.av_packet_unref(pkt);
                    while (ffmpeg.avcodec_receive_frame(_dec, frame) >= 0)
                    { bmp = ToBitmap(frame, maxW); ffmpeg.av_frame_unref(frame); return bmp; }
                }
            }
            finally { ffmpeg.av_frame_free(&frame); ffmpeg.av_packet_free(&pkt); }
            return bmp;
        }

        private Bitmap ToBitmap(AVFrame* src, int maxW)
        {
            int dstW = Math.Min(maxW, src->width);
            int dstH = (int)Math.Round(dstW * (src->height / (double)src->width));

            if (_sws == null)
            {
                _sws = ffmpeg.sws_getContext(
                    src->width, src->height, (AVPixelFormat)src->format,
                    dstW, dstH, AVPixelFormat.AV_PIX_FMT_BGRA,
                    ffmpeg.SWS_BICUBIC, null, null, null);
            }

            byte_ptrArray4 dst = new(); int_array4 dstLinesize = new();
            int bufSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, dstW, dstH, 1);
            byte* buffer = (byte*)ffmpeg.av_malloc((ulong)bufSize);
            ffmpeg.av_image_fill_arrays(ref dst, ref dstLinesize, buffer, AVPixelFormat.AV_PIX_FMT_BGRA, dstW, dstH, 1);
            ffmpeg.sws_scale(_sws, src->data, src->linesize, 0, src->height, dst, dstLinesize);

            var bmp = new Bitmap(dstW, dstH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, dstW, dstH);
            var lockd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                int stride = Math.Abs(dstLinesize[0]);
                for (int y = 0; y < dstH; y++)
                {
                    IntPtr srcLine = (IntPtr)(dst[0] + y * dstLinesize[0]);
                    IntPtr dstLine = lockd.Scan0 + y * lockd.Stride;
                    unsafe { Buffer.MemoryCopy((void*)srcLine, (void*)dstLine, lockd.Stride, stride); }
                }
            }
            finally { bmp.UnlockBits(lockd); ffmpeg.av_free(buffer); }
            return bmp;
        }

        public void Close()
        {
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_dec != null) { var d = _dec; ffmpeg.avcodec_free_context(&d); _dec = null; }
            if (_fmt != null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
            _vindex = -1;
        }

        public void Dispose() => Close();
    }

    // ======= DirectShow unified engine =======
    public interface IPlaybackEngine : IDisposable
    {
        void Open(string mediaPath, bool hasVideo);
        void Play(); void Pause(); void Stop();
        double DurationSeconds { get; }
        double PositionSeconds { get; set; }
        void SetVolume(float volume);
        void UpdateVideoWindow(IntPtr ownerHwnd, Rectangle ownerClient);
        void SetStereo3D(Stereo3DMode mode);
        void BindUpdateCallback(Action cb);
        bool IsBitstreamActive();
        bool HasDisplayControl();

        (string text, DateTime when) GetLastVideoMTDump();
        (int width, int height, string subtype) GetNegotiatedVideoFormat();
        (int bytes, DateTime when) GetLastSnapshotInfo();

        event Action<double>? OnProgressSeconds;
        event Action<string>? OnStatus;

        List<DsStreamItem> EnumerateStreams();
        bool EnableByGlobalIndex(int globalIndex);
        bool DisableSubtitlesIfPossible();
        bool TrySnapshot(out int byteCount);
    }

    public sealed class DsStreamItem
    {
        public int GlobalIndex;
        public int Group;
        public bool IsAudio;
        public bool IsSubtitle;
        public string Name = "";
        public bool Selected;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveFormatExtensible
    {
        public WaveFormatEx Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    public sealed class DirectShowUnifiedEngine : IPlaybackEngine
    {
        private readonly bool _preferBitstream;
        private readonly string? _preferredAudioRendererName;
        private readonly VideoRendererChoice _choice;
        private readonly bool _fileIsHdr;
        private readonly AVCodecID _srcAudioCodec;

        private IGraphBuilder? _graph;
        private IMediaControl? _control;
        private IMediaSeeking? _seek;
        private IBasicAudio? _basicAudio;

        private IBaseFilter? _lavSource, _lavVideo, _lavAudio, _videoRenderer, _audioRenderer;

        private IMFVideoDisplayControl? _mfDisplay; // EVR/MPCVR
        private IVideoWindow? _videoWindow;         // MPCVR/madVR (windowed)

        private System.Windows.Forms.Timer? _timer;
        private bool _hasVideo;
        private Stereo3DMode _stereo = Stereo3DMode.None;

        private bool _bitstreamActive;
        private string _audioRendererName = "?";

        // Debug state
        private string _lastVmtDump = "";
        private DateTime _lastVmtAt = DateTime.MinValue;
        private int _lastSnapshotBytes;
        private DateTime _lastSnapshotAt;

        public event Action<double>? OnProgressSeconds;
        public event Action<string>? OnStatus;

        public bool IsBitstreamActive() => _bitstreamActive;
        public bool HasDisplayControl() => _mfDisplay != null || _videoWindow != null;

        public DirectShowUnifiedEngine(bool preferBitstream, string? preferredRendererName, VideoRendererChoice choice, bool fileIsHdr, AVCodecID srcAudioCodec)
        {
            _preferBitstream = preferBitstream;
            _preferredAudioRendererName = preferredRendererName;
            _choice = choice;
            _fileIsHdr = fileIsHdr;
            _srcAudioCodec = srcAudioCodec;
            Dbg.Log($"Engine ctor: preferBitstream={preferBitstream}, preferredAR={(preferredRendererName ?? "null")}, choice={choice}");
        }

        public double DurationSeconds { get { if (_seek == null) return 0; _seek.GetDuration(out long d); return d / 10_000_000.0; } }
        public double PositionSeconds
        {
            get { if (_seek == null) return 0; _seek.GetCurrentPosition(out long p); return p / 10_000_000.0; }
            set { if (_seek == null) return; long t = (long)(value * 10_000_000.0); _seek.SetPositions(t, AMSeekingSeekingFlags.AbsolutePositioning, t, AMSeekingSeekingFlags.NoPositioning); }
        }

        public void Open(string mediaPath, bool hasVideo)
        {
            _hasVideo = hasVideo;
            Dbg.Log($"Open(mediaPath='{mediaPath}', hasVideo={hasVideo})");
            DisposeGraph();

            _graph = (IGraphBuilder)new FilterGraph();
            _control = (IMediaControl)_graph;
            _seek = (IMediaSeeking)_graph;
            _basicAudio = (IBasicAudio)_graph;

            _lavSource = CreateFilterByName("LAV Splitter Source") ?? throw new ApplicationException("LAV Splitter Source non trovato");
            _lavAudio = CreateFilterByName("LAV Audio Decoder") ?? throw new ApplicationException("LAV Audio Decoder non trovato");
            Dbg.Log("LAV Splitter/Audio creati");

            if (_hasVideo)
            {
                _lavVideo = CreateFilterByName("LAV Video Decoder") ?? throw new ApplicationException("LAV Video Decoder non trovato");
                _videoRenderer = CreateVideoRendererByChoice(_choice) ?? throw new ApplicationException("Renderer video non disponibile");
                Dbg.Log($"Video renderer scelto: {FilterFriendlyName(_videoRenderer)} ({_choice})");
            }
            _audioRenderer = PickAudioRenderer(_preferredAudioRendererName);
            _audioRendererName = _audioRenderer != null ? FilterFriendlyName(_audioRenderer) : "null";
            Dbg.Log($"Audio renderer: {_audioRendererName}");

            int hr;
            hr = _graph.AddFilter(_lavSource, "LAV Source"); DsError.ThrowExceptionForHR(hr);
            hr = _graph.AddFilter(_lavAudio, "LAV Audio"); DsError.ThrowExceptionForHR(hr);
            if (_audioRenderer != null) { hr = _graph.AddFilter(_audioRenderer, "Audio Renderer"); DsError.ThrowExceptionForHR(hr); }
            if (_hasVideo)
            {
                hr = _graph.AddFilter(_lavVideo!, "LAV Video"); DsError.ThrowExceptionForHR(hr);
                hr = _graph.AddFilter(_videoRenderer!, "Video Renderer"); DsError.ThrowExceptionForHR(hr);
                AttachDisplayInterfaces(initial: true);
            }

            var fileSrc = (IFileSourceFilter)_lavSource;
            hr = fileSrc.Load(mediaPath, null); DsError.ThrowExceptionForHR(hr);
            Dbg.Log("File sorgente caricato");

            ConnectAudioPath();
            if (_hasVideo) ConnectVideoPath();

            StartTimer();
            var msg = $"Grafo pronto ({(_preferBitstream ? "Bitstream-first" : "PCM")}{(_hasVideo ? ", video" : ", solo audio")}).";
            Dbg.Log(msg);
            OnStatus?.Invoke(msg);
        }

        private void HdrTrace(string text) { if (_fileIsHdr) Dbg.Log("[HDR] " + text, Dbg.LogLevel.Info); }

        private void ConnectAudioPath()
        {
            try
            {
                var srcA = FindPin(_lavSource!, PinDirection.Output, DirectShowLib.MediaType.Audio) ?? FindPin(_lavSource!, PinDirection.Output, null);
                if (srcA == null || _audioRenderer == null) { Dbg.Warn("Audio: srcA o renderer null, skip"); return; }

                var aIn = FindPin(_lavAudio!, PinDirection.Input, DirectShowLib.MediaType.Audio) ?? FindPin(_lavAudio!, PinDirection.Input, null);
                var aOut = FindPin(_lavAudio!, PinDirection.Output, null);
                var rIn = FindPin(_audioRenderer!, PinDirection.Input, null);

                if (aIn != null && aOut != null && rIn != null)
                {
                    int hr = _graph!.Connect(srcA, aIn); DsError.ThrowExceptionForHR(hr);
                    Dbg.Log("Audio: LAV Splitter → LAV Audio", Dbg.LogLevel.Verbose);
                    hr = _graph!.Connect(aOut, rIn); DsError.ThrowExceptionForHR(hr);
                    Dbg.Log("Audio: LAV Audio → Renderer", Dbg.LogLevel.Verbose);
                }
                else if (rIn != null)
                {
                    int hr = _graph!.Connect(srcA, rIn); DsError.ThrowExceptionForHR(hr);
                    Dbg.Log("Audio: LAV Splitter → Renderer (direct)");
                }

                Dbg.Log(_preferBitstream ? "Preferenza Bitstream attiva (se LAV lo consente)" : "PCM (fallback)");
                TryDetectBitstream();
            }
            catch (Exception ex)
            {
                Dbg.Error("ConnectAudioPath EX: " + ex);
                throw;
            }
        }

        private void ConnectVideoPath()
        {
            try
            {
                // LAV Splitter → LAV Video
                ConnectByType(_lavSource!, _lavVideo!, DirectShowLib.MediaType.Video);
                Dbg.Log("Video: LAV Splitter → LAV Video", Dbg.LogLevel.Verbose);

                // LAV Video → Renderer
                var vOut = FindPin(_lavVideo!, PinDirection.Output, null) ?? throw new ApplicationException("Pin out LAV Video non trovato");
                var rIn = FindPin(_videoRenderer!, PinDirection.Input, null) ?? throw new ApplicationException("Pin in renderer non trovato");

                int hr = _graph!.ConnectDirect(vOut, rIn, null);
                if (hr == 0)
                {
                    Dbg.Log("Video: LAV Video → Renderer (ConnectDirect) OK", Dbg.LogLevel.Verbose);
                }
                else
                {
                    Dbg.Warn($"ConnectDirect fallito (hr=0x{hr:X8}), provo Connect() standard…");
                    DsError.ThrowExceptionForHR(_graph.Connect(vOut, rIn));
                    Dbg.Log("Video: LAV Video → Renderer (Connect) OK", Dbg.LogLevel.Verbose);
                }

                // Reattach interfacce display
                bool keepPreWindowed = (_choice == VideoRendererChoice.MPCVR || _choice == VideoRendererChoice.MADVR) && (_videoWindow != null);
                if (!keepPreWindowed) AttachDisplayInterfaces(initial: false);
                if (_hasVideo && _mfDisplay == null && _videoWindow == null)
                    throw new ApplicationException("No display control from renderer");

                TryDumpNegotiatedVideoMT();
                if (_fileIsHdr && _mfDisplay != null)
                {
                    try
                    {
                        _mfDisplay.GetNativeVideoSize(out var nat, out var ar);
                        HdrTrace($"NativeVideoSize={nat.Width}x{nat.Height}  ARVideo={ar.Width}x{ar.Height}");
                    }
                    catch (Exception ex) { HdrTrace("GetNativeVideoSize EX: " + ex.Message); }
                }
                return;
            }
            catch (Exception firstEx)
            {
                Dbg.Warn("ConnectVideoPath primo tentativo EX: " + firstEx.Message);

                // Fallback con Color Space Converter
                try
                {
                    var csc = CreateFilterByClsid(new Guid("1643E180-90F5-11CE-97D5-00AA0055595A"), "Color Space Converter")
                              ?? throw new ApplicationException("Color Space Converter non disponibile");
                    int hrAdd = _graph!.AddFilter(csc, "Color Space Converter");
                    DsError.ThrowExceptionForHR(hrAdd);

                    var vOut = FindPin(_lavVideo!, PinDirection.Output, null) ?? throw new ApplicationException("Pin out LAV Video non trovato");
                    var cIn = FindPin(csc, PinDirection.Input, DirectShowLib.MediaType.Video) ?? FindPin(csc, PinDirection.Input, null) ?? throw new ApplicationException("Pin in CSC non trovato");
                    var cOut = FindPin(csc, PinDirection.Output, DirectShowLib.MediaType.Video) ?? FindPin(csc, PinDirection.Output, null) ?? throw new ApplicationException("Pin out CSC non trovato");
                    var rIn = FindPin(_videoRenderer!, PinDirection.Input, null) ?? throw new ApplicationException("Pin in renderer non trovato");

                    DsError.ThrowExceptionForHR(_graph.Connect(vOut, cIn));
                    Dbg.Log("Video: LAV Video → Color Space Converter", Dbg.LogLevel.Verbose);

                    int hr2 = _graph.ConnectDirect(cOut, rIn, null);
                    if (hr2 != 0)
                    {
                        Dbg.Warn($"CSC → Renderer ConnectDirect fallito (hr=0x{hr2:X8}), riprovo Connect()…");
                        DsError.ThrowExceptionForHR(_graph.Connect(cOut, rIn));
                    }
                    Dbg.Log("Video: Color Space Converter → Renderer OK", Dbg.LogLevel.Verbose);

                    AttachDisplayInterfaces(initial: false);
                    if (_hasVideo && _mfDisplay == null && _videoWindow == null)
                        throw new ApplicationException("No display control after CSC fallback");

                    TryDumpNegotiatedVideoMT();
                    return;
                }
                catch (Exception cscEx)
                {
                    Dbg.Error("ConnectVideoPath fallback CSC EX: " + cscEx.Message);
                    throw;
                }
            }
        }

        private IBaseFilter? CreateFilterByClsid(Guid clsid, string friendlyForLog)
        {
            try
            {
                var type = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
                var obj = Activator.CreateInstance(type);
                if (obj is IBaseFilter f)
                {
                    Dbg.Log($"CreateFilterByClsid: {friendlyForLog} → OK", Dbg.LogLevel.Verbose);
                    return f;
                }
            }
            catch (Exception ex) { Dbg.Warn($"CreateFilterByClsid: {friendlyForLog} EX: {ex.Message}"); }
            return null;
        }

        public void SetStereo3D(Stereo3DMode mode) { _stereo = mode; _updateCb?.Invoke(); Dbg.Log("Stereo3D set to " + mode, Dbg.LogLevel.Verbose); }

        private Action? _updateCb;
        public void BindUpdateCallback(Action cb) => _updateCb = cb;

        public void Play()
        {
            int hr = _control?.Run() ?? 0; DsError.ThrowExceptionForHR(hr);
            try
            {
                _mfDisplay?.RepaintVideo();
                _videoWindow?.put_Visible(OABool.True);
            }
            catch { }
            OnStatus?.Invoke("Riproduzione.");
            _updateCb?.Invoke();
            if (_fileIsHdr) HdrTrace("Play() → Run + RepaintVideo");
        }
        public void Pause() { int hr = _control?.Pause() ?? 0; DsError.ThrowExceptionForHR(hr); OnStatus?.Invoke("Pausa."); if (_fileIsHdr) HdrTrace("Pause()"); }
        public void Stop() { try { _control?.Stop(); } catch { } OnStatus?.Invoke("Stop."); if (_fileIsHdr) HdrTrace("Stop()"); }

        public void SetVolume(float v)
        {
            if (_basicAudio != null)
            {
                try
                {
                    int ds = (v <= 0.0001f)
                        ? -10000
                        : (int)Math.Round(Math.Clamp(20.0 * Math.Log10(v) * 100.0, -10000.0, 0.0));
                    _basicAudio.put_Volume(ds);
                }
                catch { }
            }
            try { CoreAudioSessionVolume.Set(v); } catch { }
        }

        public List<DsStreamItem> EnumerateStreams()
        {
            var list = new List<DsStreamItem>();
            try
            {
                if (_lavSource is IAMStreamSelect sel)
                {
                    sel.Count(out int count);
                    for (int i = 0; i < count; i++)
                    {
                        sel.Info(i, out AMMediaType mt, out AMStreamSelectInfoFlags flags, out int _, out int group, out string name, out object a, out object b);
                        bool isAudio = (mt.majorType == DirectShowLib.MediaType.Audio);
                        bool isSub = (mt.majorType == DirectShowLib.MediaType.Texts) || LooksLikeSubtitleName(name) || LooksLikeSubtitleSubtype(mt);
                        list.Add(new DsStreamItem
                        {
                            GlobalIndex = i,
                            Group = group,
                            IsAudio = isAudio,
                            IsSubtitle = isSub && !isAudio,
                            Name = name ?? "",
                            Selected = (flags & AMStreamSelectInfoFlags.Enabled) != 0
                        });
                        if (mt != null) DsUtils.FreeAMMediaType(mt);
                        if (a != null && Marshal.IsComObject(a)) Marshal.ReleaseComObject(a);
                        if (b != null && Marshal.IsComObject(b)) Marshal.ReleaseComObject(b);
                    }
                }
            }
            catch (Exception ex) { Dbg.Warn("EnumerateStreams EX: " + ex.Message); }
            return list;

            static bool LooksLikeSubtitleName(string? n)
            {
                if (string.IsNullOrEmpty(n)) return false;
                n = n.ToLowerInvariant();
                string[] keys = { "sub", "subtitle", "srt", "ass", "ssa", "pgs", "vobsub", "hdmv", "dvb", "idx", "forced", "ita", "eng", "spa", "fra", "ger", "deu" };
                return keys.Any(k => n.Contains(k));
            }
            static bool LooksLikeSubtitleSubtype(AMMediaType mt)
            {
                try
                {
                    return mt.majorType != DirectShowLib.MediaType.Audio &&
                           mt.majorType != DirectShowLib.MediaType.Video &&
                           mt.majorType != Guid.Empty;
                }
                catch { return false; }
            }
        }

        public bool EnableByGlobalIndex(int globalIndex)
        {
            try
            {
                if (_lavSource is IAMStreamSelect sel)
                {
                    sel.Enable(globalIndex, AMStreamSelectEnableFlags.Enable);
                    _updateCb?.Invoke();
                    Dbg.Log("Stream abilitato idx=" + globalIndex, Dbg.LogLevel.Verbose);
                    return true;
                }
            }
            catch (Exception ex) { OnStatus?.Invoke("IAMStreamSelect: " + ex.Message); Dbg.Warn("EnableByGlobalIndex EX: " + ex); }
            return false;
        }

        public bool DisableSubtitlesIfPossible()
        {
            try
            {
                if (!(_lavSource is IAMStreamSelect sel)) return false;
                sel.Count(out int count);
                for (int i = 0; i < count; i++)
                {
                    sel.Info(i, out AMMediaType mt, out AMStreamSelectInfoFlags _, out int _, out int _, out string name, out object a, out object b);
                    bool isAudio = (mt.majorType == DirectShowLib.MediaType.Audio);
                    bool looksSub = !isAudio || (name?.IndexOf("sub", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (looksSub && name != null &&
                        (name.Contains("off", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("none", StringComparison.OrdinalIgnoreCase)))
                    {
                        sel.Enable(i, AMStreamSelectEnableFlags.Enable);
                        if (mt != null) DsUtils.FreeAMMediaType(mt);
                        if (a != null && Marshal.IsComObject(a)) Marshal.ReleaseComObject(a);
                        if (b != null && Marshal.IsComObject(b)) Marshal.ReleaseComObject(b);
                        _updateCb?.Invoke();
                        Dbg.Log("Subs disabilitati via IAMStreamSelect");
                        return true;
                    }
                    if (mt != null) DsUtils.FreeAMMediaType(mt);
                    if (a != null && Marshal.IsComObject(a)) Marshal.ReleaseComObject(a);
                    if (b != null && Marshal.IsComObject(b)) Marshal.ReleaseComObject(b);
                }
            }
            catch (Exception ex) { Dbg.Warn("DisableSubtitlesIfPossible EX: " + ex.Message); }
            return false;
        }

        private void StartTimer()
        {
            StopTimer();
            _timer = new System.Windows.Forms.Timer { Interval = 250 };
            _timer.Tick += (_, __) =>
            {
                try
                {
                    if (_seek == null || OnProgressSeconds == null) return;
                    _seek.GetCurrentPosition(out long pos);
                    OnProgressSeconds(pos / 10_000_000.0);
                }
                catch { }
            };
            _timer.Start();
        }
        private void StopTimer()
        {
            if (_timer == null) return;
            _timer.Stop(); _timer.Dispose(); _timer = null;
        }

        public void Dispose() { StopTimer(); DisposeGraph(); }

        private void DisposeGraph()
        {
            Dbg.Log("DisposeGraph()", Dbg.LogLevel.Verbose);
            try { _control?.Stop(); } catch { }
            ReleaseCom(ref _mfDisplay);
            ReleaseCom(ref _videoWindow);
            ReleaseCom(ref _lavSource); ReleaseCom(ref _lavVideo); ReleaseCom(ref _lavAudio); ReleaseCom(ref _videoRenderer); ReleaseCom(ref _audioRenderer);
            ReleaseCom(ref _seek); ReleaseCom(ref _basicAudio); ReleaseCom(ref _control); ReleaseCom(ref _graph);
            try { GC.Collect(); GC.WaitForPendingFinalizers(); } catch { }
        }

        private static void ReleaseCom<T>(ref T? obj) where T : class
        { if (obj == null) return; try { if (Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); } catch { } finally { obj = null; } }

        private static readonly Guid MR_VIDEO_RENDER_SERVICE = new("1092A86C-AB1A-459A-A336-831FBC4D11FF");

        private void AttachDisplayInterfaces(bool initial)
        {
            bool preserveWindowedRenderer = (!initial) && (_choice == VideoRendererChoice.MPCVR || _choice == VideoRendererChoice.MADVR) && (_videoWindow != null);

            if (!initial)
            {
                ReleaseCom(ref _mfDisplay);
                if (!preserveWindowedRenderer) ReleaseCom(ref _videoWindow);
            }

            // 1) EVR/MPCVR: IMFVideoDisplayControl via IMFGetService
            try
            {
                if (_videoRenderer is IMFGetService gs1)
                {
                    int hr1 = gs1.GetService(MR_VIDEO_RENDER_SERVICE, typeof(IMFVideoDisplayControl).GUID, out object obj);
                    if (hr1 == 0 && obj is IMFVideoDisplayControl d1)
                    {
                        _mfDisplay = d1;
                        _mfDisplay.SetAspectRatioMode((int)MFVideoARMode.PreservePicture);
                        Dbg.Log($"{(initial ? "(pre)" : "(post)")} IMFVideoDisplayControl ottenuto dal filtro.");
                        return;
                    }
                    else Dbg.Warn($"{(initial ? "(pre)" : "(post)")} IMFGetService(filter) hr=0x{hr1:X8}");
                }

                var inPin = _videoRenderer != null ? FindPin(_videoRenderer, PinDirection.Input, null) : null;
                if (inPin is IMFGetService gs2)
                {
                    int hr2 = gs2.GetService(MR_VIDEO_RENDER_SERVICE, typeof(IMFVideoDisplayControl).GUID, out object obj2);
                    if (hr2 == 0 && obj2 is IMFVideoDisplayControl d2)
                    {
                        _mfDisplay = d2;
                        _mfDisplay.SetAspectRatioMode((int)MFVideoARMode.PreservePicture);
                        Dbg.Log($"{(initial ? "(pre)" : "(post)")} IMFVideoDisplayControl ottenuto dall'input pin.");
                        return;
                    }
                    else Dbg.Warn($"{(initial ? "(pre)" : "(post)")} IMFGetService(pin) hr=0x{hr2:X8}");
                }
            }
            catch (Exception ex) { Dbg.Warn("AttachDisplayInterfaces (EVR/MPCVR) EX: " + ex.Message); }

            // 2) Renderer windowed (MPCVR/madVR): prova direttamente IVideoWindow
            try
            {
                if (_videoRenderer != null && !preserveWindowedRenderer)
                {
                    _videoWindow = (IVideoWindow)_videoRenderer;
                    Dbg.Log("IVideoWindow acquisito dal renderer.");
                    return;
                }
                if (preserveWindowedRenderer)
                {
                    Dbg.Log("(post) IVideoWindow windowed renderer preservato.");
                    return;
                }
            }
            catch (Exception ex) { Dbg.Warn("AttachDisplayInterfaces (IVideoWindow/Renderer) EX: " + ex.Message); }

            // 3) Fallback: IVideoWindow dal FilterGraph
            try
            {
                if (_graph != null && _videoWindow == null)
                {
                    _videoWindow = (IVideoWindow)_graph;
                    Dbg.Log("IVideoWindow acquisito dal FilterGraph.");
                    return;
                }
            }
            catch (Exception ex) { Dbg.Warn("AttachDisplayInterfaces (IVideoWindow/Graph) EX: " + ex.Message); }
        }

        private void CalcSizesForStereo(out MFVideoNormalizedRect? src, out MFRect dest, Rectangle ownerClient)
        {
            src = null;

            int natW = 0, natH = 0;
            try
            {
                if (_mfDisplay != null)
                {
                    _mfDisplay.GetNativeVideoSize(out var nat, out _);
                    natW = Math.Max(1, nat.Width);
                    natH = Math.Max(1, nat.Height);
                }
            }
            catch { }

            if (natW <= 0 || natH <= 0) { natW = 1920; natH = 1080; }

            int cropW = natW, cropH = natH;
            if (_stereo == Stereo3DMode.SBS) { cropW = Math.Max(1, natW / 2); src = new MFVideoNormalizedRect(0f, 0f, 0.5f, 1f); }
            else if (_stereo == Stereo3DMode.TAB) { cropH = Math.Max(1, natH / 2); src = new MFVideoNormalizedRect(0f, 0f, 1f, 0.5f); }

            double ar = cropW / (double)cropH;
            int dstW = ownerClient.Width;
            int dstH = (int)Math.Round(dstW / ar);
            if (dstH > ownerClient.Height)
            {
                dstH = ownerClient.Height;
                dstW = (int)Math.Round(dstH * ar);
            }
            int dx = ownerClient.Left + (ownerClient.Width - dstW) / 2;
            int dy = ownerClient.Top + (ownerClient.Height - dstH) / 2;
            dest = new MFRect(dx, dy, dx + dstW, dy + dstH);
        }

        private IBaseFilter? PickAudioRenderer(string? preferredName)
        {
            if (!string.IsNullOrWhiteSpace(preferredName))
            {
                var ex = CreateFilterByName(preferredName);
                if (ex != null) return ex;
            }
            var mpcAr = CreateFilterByName("MPC Audio Renderer");
            if (mpcAr != null) return mpcAr;

            var ds = CreateFilterByName("Default DirectSound Device");
            if (ds != null) return ds;

            var any = DsDevice.GetDevicesOfCat(FilterCategory.AudioRendererCategory).FirstOrDefault();
            return any != null ? CreateFilterByName(any.Name) : null;
        }

        private IBaseFilter? CreateFilterByName(string? friendlyName)
        {
            if (string.IsNullOrWhiteSpace(friendlyName)) return null;
            Guid[] cats =
            {
        FilterCategory.LegacyAmFilterCategory,
        FilterCategory.AudioRendererCategory,
        FilterCategory.VideoCompressorCategory,
        FilterCategory.AudioCompressorCategory,
        FilterCategory.VideoInputDevice,
        FilterCategory.AudioInputDevice
    };
            foreach (var cat in cats)
            {
                foreach (var d in DsDevice.GetDevicesOfCat(cat))
                {
                    if (d.Name.Equals(friendlyName, StringComparison.OrdinalIgnoreCase) ||
                        d.Name.StartsWith(friendlyName, StringComparison.OrdinalIgnoreCase))
                    {
                        var iid = typeof(IBaseFilter).GUID;
                        d.Mon.BindToObject(null, null, ref iid, out object obj);
                        Dbg.Log($"CreateFilterByName: '{friendlyName}' → OK", Dbg.LogLevel.Verbose);
                        return (IBaseFilter)obj;
                    }
                }
            }
            Dbg.Log($"CreateFilterByName: '{friendlyName}' → NOT FOUND", Dbg.LogLevel.Verbose);
            return null;
        }

        private IBaseFilter? CreateVideoRendererByChoice(VideoRendererChoice c)
        {
            try
            {
                return c switch
                {
                    VideoRendererChoice.MADVR => CreateFilterByName("madVR Renderer") ?? CreateFilterByName("madVR") ?? throw new ApplicationException("madVR non trovato. Esegui 'install.bat' come Amministratore nella cartella di madVR."),
                    VideoRendererChoice.MPCVR => CreateFilterByName("MPC Video Renderer"),
                    VideoRendererChoice.EVR => CreateFilterByName("Enhanced Video Renderer"),
                    _ => null
                };
            }
            catch (Exception ex) { Dbg.Warn("CreateVideoRendererByChoice EX: " + ex.Message); throw; }
        }

        private void ConnectByType(IBaseFilter src, IBaseFilter dst, Guid? majorType)
        {
            if (_graph == null) throw new ObjectDisposedException(nameof(DirectShowUnifiedEngine));
            var outPin = FindPin(src, PinDirection.Output, majorType) ?? FindPin(src, PinDirection.Output, null);
            var inPin = FindPin(dst, PinDirection.Input, majorType) ?? FindPin(dst, PinDirection.Input, null);
            if (outPin == null || inPin == null) throw new ApplicationException("Pin non trovati per la connessione.");
            int hr = _graph.Connect(outPin, inPin); DsError.ThrowExceptionForHR(hr);
            Dbg.Log($"ConnectByType: {FilterFriendlyName(src)} → {FilterFriendlyName(dst)} major={majorType}", Dbg.LogLevel.Verbose);
        }

        private static IPin? FindPin(IBaseFilter f, PinDirection dir, Guid? major)
        {
            f.EnumPins(out var e);
            try
            {
                var arr = new IPin[1]; IPin? fallback = null;
                while (e.Next(1, arr, IntPtr.Zero) == 0)
                {
                    arr[0].QueryDirection(out var d);
                    if (d == dir)
                    {
                        if (major == null) return arr[0];
                        if (PinHasType(arr[0], major.Value)) { return arr[0]; }
                        if (fallback == null) fallback = arr[0]; else Marshal.ReleaseComObject(arr[0]);
                    }
                    else Marshal.ReleaseComObject(arr[0]);
                }
                return fallback;
            }
            finally { Marshal.ReleaseComObject(e); }
        }

        private static bool PinHasType(IPin p, Guid major)
        {
            p.EnumMediaTypes(out var e);
            try
            {
                var arr = new AMMediaType[1];
                while (e.Next(1, arr, IntPtr.Zero) == 0)
                {
                    bool ok = (arr[0].majorType == major);
                    DsUtils.FreeAMMediaType(arr[0]);
                    if (ok) return true;
                }
                return false;
            }
            finally { Marshal.ReleaseComObject(e); }
        }

        private enum MFVideoARMode { None = 0, PreservePicture = 1, PreservePixel = 2, NonLinearStretch = 4, Mask = 0x7 }

        private static string FilterFriendlyName(IBaseFilter f)
        {
            try
            {
                f.QueryFilterInfo(out var fi);
                var n = fi.achName;
                if (fi.pGraph != null) Marshal.ReleaseComObject(fi.pGraph);
                if (!string.IsNullOrWhiteSpace(n)) return n;
            }
            catch { }

            try
            {
                if (f is IPersist p)
                {
                    p.GetClassID(out var cls);
                    return cls.ToString();
                }
            }
            catch { }

            return f.GetType().Name;
        }

        [ComImport, Guid("0000010c-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersist { [PreserveSig] int GetClassID(out Guid pClassID); }

        private void TryDetectBitstream()
        {
            _bitstreamActive = false;
            if (_audioRenderer == null || _graph == null) return;
            try
            {
                var rIn = FindPin(_audioRenderer, PinDirection.Input, null);
                if (rIn == null) return;
                var mt = new AMMediaType();
                rIn.ConnectionMediaType(mt);
                try
                {
                    bool heuristic = false;

                    if (mt.subType == MediaSubType.PCM || mt.subType == MediaSubType.IEEE_FLOAT)
                    {
                        _bitstreamActive = false;
                    }
                    else if (mt.formatType == FormatType.WaveEx && mt.formatPtr != IntPtr.Zero)
                    {
                        var wfx = Marshal.PtrToStructure<WaveFormatEx>(mt.formatPtr);
                        if (wfx.wFormatTag == 1 /*PCM*/ || wfx.wFormatTag == 3 /*IEEE_FLOAT*/)
                        {
                            _bitstreamActive = false;
                        }
                        else if (wfx.wFormatTag == 0xFFFE /*Extensible*/ && wfx.cbSize >= 22)
                        {
                            var ext = Marshal.PtrToStructure<WaveFormatExtensible>(mt.formatPtr);
                            var sub = ext.SubFormat;
                            _bitstreamActive = !(sub == MediaSubType.PCM || sub == MediaSubType.IEEE_FLOAT);
                        }
                        else
                        {
                            _bitstreamActive = true; // AC3/DTS/IEC61937 ecc.
                        }
                    }
                    else
                    {
                        _bitstreamActive = !(mt.subType == MediaSubType.PCM || mt.subType == MediaSubType.IEEE_FLOAT);
                    }

                    if (!_bitstreamActive && MediaProbe.IsPassthroughCandidate(_srcAudioCodec))
                    {
                        _bitstreamActive = true;
                        heuristic = true;
                    }

                    Dbg.Log("Audio bitstreamActive=" + _bitstreamActive + (heuristic ? " (heuristic)" : ""));
                }
                finally { if (mt != null) DsUtils.FreeAMMediaType(mt); }
            }
            catch (Exception ex) { Dbg.Warn("TryDetectBitstream EX: " + ex.Message); }
        }

        private void TryDumpNegotiatedVideoMT()
        {
            try
            {
                if (_graph == null || _videoRenderer == null) return;
                var inPin = FindPin(_videoRenderer, PinDirection.Input, null);
                if (inPin == null) { Dbg.Warn("TryDumpNegotiatedVideoMT: inPin null"); return; }
                var mt = new AMMediaType();
                inPin.ConnectionMediaType(mt);
                var sb = new StringBuilder();
                sb.AppendLine("=== NEGOTIATED VIDEO MT ===");
                sb.AppendLine($"majorType: {mt.majorType}");
                sb.AppendLine($"subType:   {Dbg.Hex(mt.subType)}");
                sb.AppendLine($"formatType:{mt.formatType}");
                if (mt.formatType == FormatType.VideoInfo2 && mt.formatPtr != IntPtr.Zero)
                {
                    var vih = (VideoInfoHeader2)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader2))!;
                    sb.AppendLine($"  size: {vih.BmiHeader.Width}x{Math.Abs(vih.BmiHeader.Height)}  bitCount: {vih.BmiHeader.BitCount}");
                    if (_fileIsHdr) HdrTrace($"Negotiated subtype={Dbg.Hex(mt.subType)} size={vih.BmiHeader.Width}x{Math.Abs(vih.BmiHeader.Height)} bitCount={vih.BmiHeader.BitCount}");
                }
                _lastVmtDump = sb.ToString();
                _lastVmtAt = DateTime.Now;
                Dbg.Log(_lastVmtDump.Replace("\r\n", " | "), _fileIsHdr ? Dbg.LogLevel.Info : Dbg.LogLevel.Verbose);
                if (mt != null) DsUtils.FreeAMMediaType(mt);
            }
            catch (Exception ex) { Dbg.Warn("TryDumpNegotiatedVideoMT EX: " + ex.Message); }
        }

        public (string text, DateTime when) GetLastVideoMTDump() => (_lastVmtDump, _lastVmtAt);

        public (int width, int height, string subtype) GetNegotiatedVideoFormat()
        {
            int w = 0, h = 0; string sub = "?";
            try
            {
                if (_graph == null || _videoRenderer == null) return (0, 0, "?");
                var inPin = FindPin(_videoRenderer, PinDirection.Input, null);
                if (inPin == null) return (0, 0, "?");
                var mt = new AMMediaType();
                inPin.ConnectionMediaType(mt);
                try
                {
                    sub = GuidToCodecName(mt.subType);
                    if (mt.formatType == FormatType.VideoInfo2 && mt.formatPtr != IntPtr.Zero)
                    {
                        var vih = (VideoInfoHeader2)Marshal.PtrToStructure(mt.formatPtr, typeof(VideoInfoHeader2))!;
                        w = vih.BmiHeader.Width;
                        h = Math.Abs(vih.BmiHeader.Height);
                    }
                }
                finally { if (mt != null) DsUtils.FreeAMMediaType(mt); }
            }
            catch { }
            return (w, h, sub);
        }

        public bool TrySnapshot(out int byteCount)
        {
            byteCount = 0;
            if (_mfDisplay != null)
            {
                IntPtr pDib = IntPtr.Zero;
                try
                {
                    _mfDisplay.GetCurrentImage(out pDib, out int cb, out long _);
                    if (pDib != IntPtr.Zero && cb > 0)
                    {
                        byteCount = cb;
                        _lastSnapshotBytes = cb; _lastSnapshotAt = DateTime.Now;
                        return true;
                    }
                }
                catch { }
                finally { if (pDib != IntPtr.Zero) try { Marshal.FreeCoTaskMem(pDib); } catch { } }
            }
            return false; // con madVR/MPCVR windowed non c'è API standard
        }

        public (int bytes, DateTime when) GetLastSnapshotInfo() => (_lastSnapshotBytes, _lastSnapshotAt);

        private static string GuidToCodecName(Guid sub)
        {
            if (sub == MediaSubType.PCM) return "PCM";
            if (sub == MediaSubType.IEEE_FLOAT) return "PCM Float";
            if (sub == MediaSubType.DolbyAC3) return "Dolby Digital";
            if (sub == MediaSubType.H264) return "H.264";
            if (sub == MediaSubType.NV12) return "NV12";
            var s = sub.ToString().ToUpperInvariant();
            if (s.Contains("30313050")) return "P010";
            if (s.Contains("50313130") || s.Contains("P016")) return "P016";
            return sub.ToString();
        }

        public void UpdateVideoWindow(IntPtr ownerHwnd, Rectangle ownerClient)
        {
            if (!_hasVideo) return;

            // 1) EVR/MPCVR via IMFVideoDisplayControl
            try
            {
                if (_mfDisplay != null)
                {
                    _mfDisplay.SetVideoWindow(ownerHwnd);
                    CalcSizesForStereo(out var src, out var dest, ownerClient);
                    if (src.HasValue)
                    {
                        unsafe { var s = src.Value; _mfDisplay.SetVideoPosition((IntPtr)(&s), ref dest); }
                    }
                    else
                    {
                        _mfDisplay.SetVideoPosition(IntPtr.Zero, ref dest);
                    }
                    _mfDisplay.RepaintVideo();
                    return;
                }
            }
            catch (Exception ex) { Dbg.Warn("UpdateVideoWindow (EVR) EX: " + ex.Message); }

            // 2) Renderers windowed (madVR/MPCVR) via IVideoWindow
            try
            {
                if (_videoWindow != null)
                {
                    _videoWindow.put_Owner(ownerHwnd);
                    _videoWindow.put_MessageDrain(ownerHwnd);
                    const int WS_CHILD = 0x40000000;
                    const int WS_CLIPSIBLINGS = 0x04000000;
                    const int WS_CLIPCHILDREN = 0x02000000;
                    _videoWindow.put_WindowStyle((WindowStyle)(WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN));
                    _videoWindow.SetWindowPosition(ownerClient.Left, ownerClient.Top, ownerClient.Width, ownerClient.Height);
                    _videoWindow.put_Visible(OABool.True);
                    return;
                }
            }
            catch (Exception ex) { Dbg.Warn("UpdateVideoWindow (IVideoWindow) EX: " + ex.Message); }
        }
    }

    // --------- Minimal MF interop ----------
    [ComImport, Guid("FA993888-4383-415A-A930-DD472A8CF6F7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFGetService
    {
        [PreserveSig]
        int GetService([In, MarshalAs(UnmanagedType.LPStruct)] Guid guidService, [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
    }

    [ComImport, Guid("A490B1E4-AB84-4d31-A1B2-181E03B1077A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFVideoDisplayControl
    {
        void GetNativeVideoSize(out Size pszVideo, out Size pszARVideo);
        void GetIdealVideoSize(out Size pszMin, out Size pszMax);
        void SetVideoPosition(IntPtr pnrcSource, [In] ref MFRect pnrcDest);
        void GetVideoPosition(out MFVideoNormalizedRect pnrcSource, out MFRect pnrcDest);
        void SetAspectRatioMode(int dwAspectRatioMode);
        void GetAspectRatioMode(out int pdwAspectRatioMode);
        void SetVideoWindow(IntPtr hwndVideo);
        void GetVideoWindow(out IntPtr phwndVideo);
        void RepaintVideo();
        void GetCurrentImage(out IntPtr pDib, out int pcbDib, out long pTimeStamp);
        void SetBorderColor(int Clr);
        void GetBorderColor(out int pClr);
        void SetRenderingPrefs(int dwRenderFlags);
        void GetRenderingPrefs(out int pdwRenderFlags);
        void SetFullscreen(bool fFullscreen);
        void GetFullscreen(out bool pfFullscreen);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MFRect { public int left, top, right, bottom; public MFRect(int l, int t, int r, int b) { left = l; top = t; right = r; bottom = b; } }

    [StructLayout(LayoutKind.Sequential)]
    struct MFVideoNormalizedRect
    {
        public float left, top, right, bottom;
        public MFVideoNormalizedRect(float l, float t, float r, float b) { left = l; top = t; right = b; bottom = b; }
    }

    // ======= Core Audio session (volume sessione processo) =======
    internal static class CoreAudioSessionVolume
    {
        private static ISimpleAudioVolume? _simple;

        public static void Set(float vol01)
        {
            try { Ensure(); _simple?.SetMasterVolume(Math.Clamp(vol01, 0f, 1f), Guid.Empty); }
            catch { }
        }

        private static void Ensure()
        {
            if (_simple != null) return;

            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            var iid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iid, 0, IntPtr.Zero, out var obj);
            var mgr = (IAudioSessionManager2)obj;
            mgr.GetSessionEnumerator(out var en);
            en.GetCount(out int count);
            int pid = Process.GetCurrentProcess().Id;

            for (int i = 0; i < count; i++)
            {
                en.GetSession(i, out var ctl);
                var ctl2 = (IAudioSessionControl2)ctl;
                ctl2.GetProcessId(out uint sessionPid);
                if (sessionPid == pid) { _simple = (ISimpleAudioVolume)ctl; break; }
                Marshal.ReleaseComObject(ctl);
            }
            Marshal.ReleaseComObject(en);
            Marshal.ReleaseComObject(mgr);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }

        #region COM interop
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }
        private enum EDataFlow { eRender, eCapture, eAll }
        private enum ERole { eConsole, eMultimedia, eCommunications }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        private interface IMMDeviceEnumerator
        {
            int NotImpl1();
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
        private interface IAudioSessionManager2
        {
            int NotImpl1();
            int NotImpl2();
            int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
        private interface IAudioSessionEnumerator
        {
            int GetCount(out int SessionCount);
            int GetSession(int SessionCount, out IAudioSessionControl Session);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
        private interface IAudioSessionControl { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
        private interface IAudioSessionControl2
        {
            int NotImpl0(); int NotImpl1(); int NotImpl2(); int NotImpl3(); int NotImpl4(); int NotImpl5(); int NotImpl6(); int NotImpl7();
            int NotImpl8(); int NotImpl9(); int NotImpl10();
            int GetProcessId(out uint pRetVal);
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
        private interface ISimpleAudioVolume
        {
            int SetMasterVolume(float fLevel, Guid EventContext);
            int GetMasterVolume(out float pfLevel);
            int SetMute(bool bMute, Guid EventContext);
            int GetMute(out bool pbMute);
        }
        #endregion
    }

    // ======= OverlayHostForm – top-level davvero trasparente =======
    internal sealed class OverlayHostForm : Form
    {
        public Panel Surface { get; } = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

        public OverlayHostForm()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;

            BackColor = Color.Lime;
            TransparencyKey = Color.Lime;
            Controls.Add(Surface);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        public void SyncTo(Form owner)
        {
            if (!owner.Visible) return;
            var rc = owner.RectangleToScreen(owner.ClientRectangle);
            Bounds = rc;
            if (Visible) { try { BringToFront(); } catch { } }
        }

        // Win32 helpers
        [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;

        /// <summary>Se true rimuove NOACTIVATE così i pulsanti (X) ricevono click & focus.</summary>
        public void SetInteractive(bool on)
        {
            if (!IsHandleCreated) return;
            int ex = GetWindowLong(this.Handle, GWL_EXSTYLE);
            if (on) ex &= ~WS_EX_NOACTIVATE;
            else ex |= WS_EX_NOACTIVATE;
            SetWindowLong(this.Handle, GWL_EXSTYLE, ex);
            Win32.SetWindowPos(this.Handle, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_FRAMECHANGED | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER);
        }
    }

    // ======= UI =======
    public sealed class PlayerForm : Form
    {
        private Panel _stack = null!;
        private Panel _videoHost = null!;
        private HudOverlay _hud = null!;
        private InfoOverlay _infoOverlay = null!;
        private SplashOverlay _splash = null!;
        private Label _lblStatus = null!;
        private AudioOnlyOverlay _audioOnlyBanner = null!;
        private LoadingOverlay _loading = null!;
        private ContextMenuStrip _menu = null!;
        private TableLayoutPanel _rootLayout = null!;
        private IPlaybackEngine? _engine;
        private string? _currentPath;
        private MediaProbe.Result? _info;

        private string? _selectedAudioRendererName;
        private bool _selectedRendererLooksHdmi;
        private S3DMode _stereo = S3DMode.None;
        private HDRMode _hdr = HDRMode.Auto;

        private double _duration;
        private bool _paused;

        private Thumbnailer _thumb = new();
        private CancellationTokenSource? _thumbCts;
        private volatile bool _previewBusy;

        private FormWindowState _prevState; private FormBorderStyle _prevBorder;
        private readonly OverlayHostForm _overlayHost;
        private InlineOverlayPanel? _overlayInlineHost;
        private bool _overlayInlineMode;

        private static readonly VRChoice[] ORDER_HDR = { VRChoice.MADVR, VRChoice.MPCVR };
        private static readonly VRChoice[] ORDER_SDR = { VRChoice.EVR };

        private ToolStripMenuItem _mAudioLang = null!;
        private ToolStripMenuItem _mSubtitles = null!;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        private const int ICON_SMALL2 = 2;

        private Icon? _iconBig;
        private Icon? _iconSmall;

        private AudioOnlyOverlay BuildAudioOnlyBanner() => new()
        {
            Dock = DockStyle.Fill,
            Visible = false,
            ImagePath = Path.Combine(AppContext.BaseDirectory, "Assets", "audio-only.png"),
            Caption = "Audio Only"
        };

        public PlayerForm()
        {
            Text = "Cinecore Player 2025";
            MinimumSize = new Size(1040, 600);
            BackColor = Color.FromArgb(18, 18, 18);
            DoubleBuffered = true;
            KeyPreview = true;
            KeyDown += PlayerForm_KeyDown;

            _rootLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
            _rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _rootLayout.BackColor = Color.Black;
            _rootLayout.CellBorderStyle = TableLayoutPanelCellBorderStyle.None;
            _rootLayout.Padding = Padding.Empty;
            _rootLayout.Margin = Padding.Empty;

            _stack = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            _videoHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            _hud = new HudOverlay { Dock = DockStyle.Fill, AutoHide = true, Visible = false };
            _hud.TimelineVisible = false;
            _infoOverlay = new InfoOverlay { Dock = DockStyle.Top, Visible = false, AutoHeight = true, MaxCardHeight = 420 };
            _overlayHost = new OverlayHostForm();
            AddOwnedForm(_overlayHost);
            _overlayHost.Visible = false;

            _splash = new SplashOverlay { Dock = DockStyle.Fill, Visible = true };
            _splash.OpenRequested += () => OpenFile();

            _loading = new LoadingOverlay { Dock = DockStyle.Fill, Visible = true };
            _loading.Completed += () =>
            {
                _loading.Visible = false;
                _splash.Visible = true;
                _hud.Visible = false;
                _hud.TimelineVisible = false;
                BringOverlaysToFront();
            };

            _audioOnlyBanner = BuildAudioOnlyBanner();

            _stack.Controls.Add(_videoHost);
            _stack.Controls.Add(_loading);
            _stack.Controls.Add(_splash);

            _overlayHost.Surface.Controls.Add(_audioOnlyBanner);
            _overlayHost.Surface.Controls.Add(_hud);
            _overlayHost.Surface.Controls.Add(_infoOverlay);

            _hud.BringToFront();
            BringOverlaysToFront();

            _splash.Visible = false;     // parte il loading, poi comparirà lo splash
            _hud.Visible = false;
            _infoOverlay.Visible = false;
            _loading.Start();

            _rootLayout.Controls.Add(_stack, 0, 0);
            _lblStatus = new Label { Text = "Pronto" };

            Controls.Add(_rootLayout);
            BringOverlaysToFront();

            _hud.GetTime = () => (_engine?.PositionSeconds ?? 0, _duration);
            _hud.GetInfoLine = () => _lblStatus.Text;
            _hud.OpenClicked += () => OpenFile();
            _hud.PlayPauseClicked += () => TogglePlayPause();
            _hud.StopClicked += () => StopAll();
            _hud.FullscreenClicked += () => ToggleFullscreen();
            _hud.VolumeChanged += v => ApplyVolume(v);
            _hud.SeekRequested += s => { if (_engine != null && _duration > 0) _engine.PositionSeconds = Math.Clamp(s, 0, Math.Max(0.01, _duration)); _hud.ShowOnce(1200); };
            _hud.PreviewRequested += OnPreviewRequested;
            _hud.SkipBack10Clicked += () => { SeekRelative(-10); _hud.ShowOnce(1200); };
            _hud.SkipForward10Clicked += () => { SeekRelative(10); _hud.ShowOnce(1200); };
            _hud.PrevChapterClicked += () => { SeekChapter(-1); _hud.ShowOnce(1200); };
            _hud.NextChapterClicked += () => { SeekChapter(1); _hud.ShowOnce(1200); };

            _videoHost.Resize += (_, __) =>
            {
                _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);
                _infoOverlay.AdjustHeightToContent(_stack.ClientSize.Width);
                _hud.BringToFront();
                BringOverlaysToFront();
            };

            BuildMenu();
            ContextMenuStrip = _menu;
            _stack.ContextMenuStrip = _menu;
            _hud.ContextMenuStrip = _menu;
            _videoHost.ContextMenuStrip = _menu;
            _splash.ContextMenuStrip = _menu;

            _hud.SetExternalVolume(1f);
            Dbg.Level = Dbg.LogLevel.Info;

            try
            {
                var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
                var bigPath = Path.Combine(assets, "cinecore_icon_512.ico");
                var smallPath = Path.Combine(assets, "cinecore_icon.ico");

                if (File.Exists(bigPath)) _iconBig = new Icon(bigPath);
                if (File.Exists(smallPath)) _iconSmall = new Icon(smallPath);

                // qualcosa anche per il designer/title:
                if (_iconBig != null) this.Icon = _iconBig;
                else if (_iconSmall != null) this.Icon = _iconSmall;
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                if (_iconBig != null) SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_BIG, _iconBig.Handle);   // taskbar/Alt-Tab
                if (_iconSmall != null)
                {
                    SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_SMALL, _iconSmall.Handle); // title bar
                    SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_SMALL2, _iconSmall.Handle); // alcuni temi usano SMALL2
                }
            }
            catch { }
        }

        private string PrettyAudioName(MediaProbe.Result info, string? selectedStreamName)
        {
            string name = info.AudioCodec switch
            {
                AVCodecID.AV_CODEC_ID_TRUEHD => "Dolby TrueHD",
                AVCodecID.AV_CODEC_ID_EAC3 => "Dolby Digital Plus",
                AVCodecID.AV_CODEC_ID_AC3 => "Dolby Digital",
                AVCodecID.AV_CODEC_ID_DTS => "DTS",
                AVCodecID.AV_CODEC_ID_AAC => "AAC",
                _ => info.AudioCodec.ToString().Replace("AV_CODEC_ID_", "")
            };

            string n = selectedStreamName?.ToUpperInvariant() ?? string.Empty;

            if ((info.AudioCodec == AVCodecID.AV_CODEC_ID_TRUEHD || info.AudioCodec == AVCodecID.AV_CODEC_ID_EAC3)
                && (info.AudioLooksObjectBased || n.Contains("ATMOS") || n.Contains("JOC")))
            {
                name += " (Atmos)";
            }

            if (info.AudioCodec == AVCodecID.AV_CODEC_ID_DTS)
            {
                if (n.Contains("DTS:X") || n.Contains("DTS X")) return "DTS:X";
                if (n.Contains("DTS-HD MA") || n.Contains("DTS HD MA") || n.Contains("MASTER AUDIO")) return "DTS-HD MA";
                if (n.Contains("DTS-HD HRA") || n.Contains("HIGH RES")) return "DTS-HD HRA";
            }

            return name;
        }

        private void UseOverlayInline(bool enable)
        {
            _overlayInlineMode = enable;

            if (_overlayInlineHost == null)
            {
                _overlayInlineHost = new InlineOverlayPanel { Dock = DockStyle.Fill };
                _stack.Controls.Add(_overlayInlineHost);
                _overlayInlineHost.BringToFront();
            }

            Control target = enable ? (Control)_overlayInlineHost : (Control)_overlayHost.Surface;

            if (_audioOnlyBanner.Parent != target) _audioOnlyBanner.Parent = target;
            if (_hud.Parent != target) _hud.Parent = target;
            if (_infoOverlay.Parent != target) _infoOverlay.Parent = target;

            _overlayInlineHost.Visible = enable;

            if (enable)
            {
                if (_overlayHost.Visible) _overlayHost.Hide();
            }
            else
            {
                if (!_overlayHost.Visible)
                {
                    try { if (_overlayHost.Owner != this) AddOwnedForm(_overlayHost); } catch { }
                    _overlayHost.Show(this);
                }
                _overlayHost.SyncTo(this);
                try { _overlayHost.BringToFront(); } catch { }
            }

            BringOverlaysToFront();
            _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);
        }

        private void BringOverlaysToFront()
        {
            _videoHost.SendToBack();
            if (_splash.Visible) _splash.BringToFront();

            if (_overlayInlineMode) _overlayInlineHost?.BringToFront();
            else { _overlayHost?.SyncTo(this); if (_overlayHost?.Visible == true) { try { _overlayHost.BringToFront(); } catch { } } }

            _infoOverlay.BringToFront();

            _hud.Visible = _engine != null && !_splash.Visible;
            if (_hud.Visible) _hud.BringToFront();

            if (_audioOnlyBanner.Visible) _audioOnlyBanner.BringToFront();
        }

        private void PlayerForm_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { TogglePlayPause(); _hud.Pulse(HudOverlay.ButtonId.PlayPause); e.Handled = true; }
            else if (e.KeyCode == Keys.F) { ToggleFullscreen(); _hud.Pulse(HudOverlay.ButtonId.Fullscreen); e.Handled = true; }
            else if (e.KeyCode == Keys.Left) { SeekRelative(-10); _hud.Pulse(HudOverlay.ButtonId.Back10); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { SeekRelative(10); _hud.Pulse(HudOverlay.ButtonId.Fwd10); e.Handled = true; }
            else if (e.KeyCode == Keys.PageUp) { SeekChapter(+1); _hud.Pulse(HudOverlay.ButtonId.NextChapter); e.Handled = true; }
            else if (e.KeyCode == Keys.PageDown) { SeekChapter(-1); _hud.Pulse(HudOverlay.ButtonId.PrevChapter); e.Handled = true; }
            else if (e.KeyCode == Keys.O) { OpenFile(); _hud.Pulse(HudOverlay.ButtonId.Open); e.Handled = true; }
            else if (e.KeyCode == Keys.S) { StopAll(); _hud.Pulse(HudOverlay.ButtonId.Remove); e.Handled = true; }
        }

        private void SeekRelative(double delta)
        {
            if (_engine == null || _duration <= 0) return;
            double t = Math.Clamp(_engine.PositionSeconds + delta, 0, Math.Max(0.01, _duration));
            _engine.PositionSeconds = t;
        }

        private void SeekChapter(int dir)
        {
            if (_engine == null || _info == null || _info.Chapters.Count == 0) return;
            double cur = _engine.PositionSeconds;
            if (dir > 0)
            {
                var next = _info.Chapters.Select(c => c.start).FirstOrDefault(s => s > cur + 0.5);
                if (next > 0) _engine.PositionSeconds = Math.Min(next, Math.Max(0.01, _duration));
            }
            else
            {
                var prev = _info.Chapters.Select(c => c.start).Where(s => s < cur - 0.5).DefaultIfEmpty(0).Max();
                _engine.PositionSeconds = Math.Max(0, prev);
            }
        }

        // Overlay "Audio Only" pass-through ai click (non blocca l’HUD)
        internal sealed class AudioOnlyOverlay : Control
        {
            private Image? _png;
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string? ImagePath { get; set; }
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string Caption { get; set; } = "Audio Only";

            public AudioOnlyOverlay()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint |
                         ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            protected override CreateParams CreateParams
            { get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } }

            protected override void OnCreateControl()
            {
                base.OnCreateControl();
                var candidates = new[]
                {
                    ImagePath,
                    Path.Combine(AppContext.BaseDirectory, "Assets", "AudioOnly.png"),
                }.Where(p => !string.IsNullOrWhiteSpace(p));

                string? found = candidates.FirstOrDefault(File.Exists);
                if (found != null)
                {
                    try
                    {
                        using var fs = new FileStream(found, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var bmp = Image.FromStream(fs);
                        _png = new Bitmap(bmp);
                        Dbg.Log("AudioOnlyOverlay: caricato PNG da " + found, Dbg.LogLevel.Info);
                    }
                    catch (Exception ex) { Dbg.Warn("AudioOnlyOverlay: errore caricando '" + found + "': " + ex.Message); }
                }
                else
                {
                    Dbg.Warn("AudioOnlyOverlay: PNG non trovato. Cercati: " + string.Join(" | ", candidates));
                }
            }

            protected override void OnPaintBackground(PaintEventArgs e) { }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                if (_png != null)
                {
                    var maxW = (int)(Width * 0.4);
                    var maxH = (int)(Height * 0.4);
                    double s = Math.Min(maxW / (double)_png.Width, maxH / (double)_png.Height);
                    int w = Math.Max(1, (int)Math.Round(_png.Width * s));
                    int h = Math.Max(1, (int)Math.Round(_png.Height * s));
                    int x = (Width - w) / 2;
                    int y = (Height - h) / 2 - 24;

                    using (var glow = new SolidBrush(Color.FromArgb(36, 0, 0, 0)))
                        g.FillEllipse(glow, x - w * 0.08f, y - h * 0.08f, w * 1.16f, h * 1.16f);

                    g.DrawImage(_png, new Rectangle(x, y, w, h));
                }

                using var f = new Font("Segoe UI", 16, FontStyle.Bold);
                var sz = g.MeasureString(Caption, f);
                using var sh = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
                using var fg = new SolidBrush(Color.FromArgb(230, 230, 230));
                float cx = (Width - sz.Width) / 2f;
                float cy = Height * 0.65f;
                g.DrawString(Caption, f, sh, cx + 1, cy + 1);
                g.DrawString(Caption, f, fg, cx, cy);
            }

            protected override void WndProc(ref Message m)
            {
                const int WM_NCHITTEST = 0x84;
                const int HTTRANSPARENT = -1;
                if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
                base.WndProc(ref m);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _png?.Dispose();
                base.Dispose(disposing);
            }
        }

        private void BuildMenu()
        {
            _menu = new ContextMenuStrip();

            var mOpen = new ToolStripMenuItem("Apri…", null, (_, __) => OpenFile());
            var mPlay = new ToolStripMenuItem("Play/Pausa", null, (_, __) => TogglePlayPause());
            var mStop = new ToolStripMenuItem("Rimuovi", null, (_, __) => StopAll());
            var mFull = new ToolStripMenuItem("Schermo intero", null, (_, __) => ToggleFullscreen());

            var mHdr = new ToolStripMenuItem("Immagine (HDR)");
            var hAuto = new ToolStripMenuItem("Auto (usa madVR/MPCVR su file HDR)", null, (_, __) => { _hdr = HdrMode.Auto; _lblStatus.Text = "HDR: Auto"; ReopenSame(); }) { Checked = true };
            var hOff = new ToolStripMenuItem("Forza SDR (tone-map HDR→SDR con madVR/MPCVR)", null, (_, __) => { _hdr = HdrMode.Off; _lblStatus.Text = "HDR: Forza SDR"; ReopenSame(); });
            mHdr.DropDownItems.AddRange(new[] { hAuto, hOff });
            mHdr.DropDownOpening += (_, __) => { hAuto.Checked = _hdr == HdrMode.Auto; hOff.Checked = _hdr == HdrMode.Off; };

            var m3D = new ToolStripMenuItem("3D");
            var m3Off = new ToolStripMenuItem("Off", null, (_, __) => { _stereo = Stereo3DMode.None; _engine?.SetStereo3D(_stereo); _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle); }) { Checked = true };
            var m3SBS = new ToolStripMenuItem("SBS → 2D (metà sinistra)", null, (_, __) => { _stereo = Stereo3DMode.SBS; _engine?.SetStereo3D(_stereo); _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle); });
            var m3TAB = new ToolStripMenuItem("TAB → 2D (metà superiore)", null, (_, __) => { _stereo = Stereo3DMode.TAB; _engine?.SetStereo3D(_stereo); _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle); });
            m3D.DropDownItems.AddRange(new[] { m3Off, m3SBS, m3TAB });
            m3D.DropDownOpening += (_, __) =>
            {
                bool enable = _info?.HasVideo == true;
                m3D.Enabled = enable;
                m3Off.Checked = _stereo == Stereo3DMode.None;
                m3SBS.Checked = _stereo == Stereo3DMode.SBS;
                m3TAB.Checked = _stereo == Stereo3DMode.TAB;
            };

            var mDev = new ToolStripMenuItem("Dispositivo audio (renderer DS)");
            mDev.DropDownOpening += (_, __) =>
            {
                mDev.DropDownItems.Clear();
                var mRefresh = new ToolStripMenuItem("Aggiorna elenco");
                mRefresh.Click += (_, __2) => { }; // noop: l'elenco viene ricreato ad ogni apertura
                var mWin = new ToolStripMenuItem("Impostazioni audio di Windows…");
                mWin.Click += (_, __2) => { try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); } catch { } };
                mDev.DropDownItems.Add(mRefresh);
                mDev.DropDownItems.Add(mWin);
                mDev.DropDownItems.Add(new ToolStripSeparator());

                var all = DsDevice.GetDevicesOfCat(FilterCategory.AudioRendererCategory);
                var groups = new Dictionary<string, List<DsDevice>>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in all)
                {
                    var baseName = d.Name.StartsWith("DirectSound: ", StringComparison.OrdinalIgnoreCase) ? d.Name.Substring(13) : d.Name;
                    if (!groups.TryGetValue(baseName, out var list)) groups[baseName] = list = new List<DsDevice>();
                    list.Add(d);
                }

                foreach (var kv in groups.OrderBy(k => k.Key))
                {
                    var baseItem = new ToolStripMenuItem(kv.Key + (LooksHdmi(kv.Key) ? "  (HDMI?)" : ""));
                    foreach (var dev in kv.Value)
                    {
                        var sub = new ToolStripMenuItem(dev.Name)
                        {
                            Checked = string.Equals(dev.Name, _selectedAudioRendererName, StringComparison.OrdinalIgnoreCase)
                        };
                        sub.Click += (_, __2) =>
                        {
                            _selectedAudioRendererName = dev.Name;
                            _selectedRendererLooksHdmi = LooksHdmi(dev.Name);
                            _lblStatus.Text = $"Renderer: {dev.Name}" + (_selectedRendererLooksHdmi ? " (HDMI?)" : "");
                            ReopenSame();
                        };
                        baseItem.DropDownItems.Add(sub);
                    }
                    mDev.DropDownItems.Add(baseItem);
                }
                if (mDev.DropDownItems.Count == 3) mDev.Enabled = false;
            };

            _mAudioLang = new ToolStripMenuItem("Lingua audio");
            _mAudioLang.DropDownOpening += (_, __) => PopulateAudioLangMenu();

            _mSubtitles = new ToolStripMenuItem("Sottotitoli");
            _mSubtitles.DropDownOpening += (_, __) => PopulateSubtitlesMenu();

            var mChapters = new ToolStripMenuItem("Capitoli…", null, (_, __) => ShowChaptersMenu());
            mChapters.DropDownOpening += (_, __) => mChapters.Enabled = _info?.Chapters.Count > 0;

            var mShowInfo = new ToolStripMenuItem("Info overlay ON/OFF", null, (_, __) => { _infoOverlay.Visible = !_infoOverlay.Visible; });

            var mRenderer = new ToolStripMenuItem("Renderer video");
            void SetRenderer(VRChoice? c)
            {
                _manualRendererChoice = c;
                _lblStatus.Text = c.HasValue ? $"Renderer video: {c}" : "Renderer video: Auto";
                ReopenSame();
            }
            var miMadvr = new ToolStripMenuItem("madVR", null, (_, __) => SetRenderer(VRChoice.MADVR));
            var miMpcvr = new ToolStripMenuItem("MPCVR", null, (_, __) => SetRenderer(VRChoice.MPCVR));
            var miEvr = new ToolStripMenuItem("EVR", null, (_, __) => SetRenderer(VRChoice.EVR));
            var miAuto = new ToolStripMenuItem("Auto (ordine preferito)", null, (_, __) => SetRenderer(null));
            mRenderer.DropDownItems.AddRange(new ToolStripItem[] { miMadvr, miMpcvr, miEvr, new ToolStripSeparator(), miAuto });
            mRenderer.DropDownOpening += (_, __) =>
            {
                miMadvr.Checked = _manualRendererChoice == VideoRendererChoice.MADVR;
                miMpcvr.Checked = _manualRendererChoice == VideoRendererChoice.MPCVR;
                miEvr.Checked = _manualRendererChoice == VideoRendererChoice.EVR;
                miAuto.Checked = _manualRendererChoice == null;
            };

            _menu.Items.AddRange(new ToolStripItem[]
            {
                mOpen, new ToolStripSeparator(), mPlay, mStop, mFull, new ToolStripSeparator(),
                mHdr, m3D, _mAudioLang, _mSubtitles, mRenderer, mDev, new ToolStripSeparator(),
                mChapters, mShowInfo
            });
        }

        private VRChoice? _manualRendererChoice = null;

        private void PopulateAudioLangMenu()
        {
            _mAudioLang.DropDownItems.Clear();
            if (_engine == null) { _mAudioLang.Enabled = false; return; }
            var streams = _engine.EnumerateStreams().Where(s => s.IsAudio).ToList();
            _mAudioLang.Enabled = streams.Count > 0;
            foreach (var s in streams)
            {
                var name = string.IsNullOrWhiteSpace(s.Name) ? $"Audio {s.Group}:{s.GlobalIndex}" : s.Name;
                var it = new ToolStripMenuItem(name) { Checked = s.Selected };
                int idx = s.GlobalIndex;
                it.Click += (_, __) =>
                {
                    _engine?.EnableByGlobalIndex(idx);
                    _lblStatus.Text = $"Audio: {name}";
                    _hud.ShowOnce(1200);
                };
                _mAudioLang.DropDownItems.Add(it);
            }
        }

        private void PopulateSubtitlesMenu()
        {
            _mSubtitles.DropDownItems.Clear();
            if (_engine == null) { _mSubtitles.Enabled = false; return; }
            var streams = _engine.EnumerateStreams().Where(s => s.IsSubtitle).ToList();
            _mSubtitles.Enabled = streams.Count > 0;
            if (streams.Count == 0) return;

            var off = new ToolStripMenuItem("Disattiva (se disponibile)");
            off.Click += (_, __) =>
            {
                if (_engine!.DisableSubtitlesIfPossible())
                    _lblStatus.Text = "Sottotitoli: disattivati";
                else
                    _lblStatus.Text = "Sottotitoli: nessuna traccia OFF esplicita";
                _hud.ShowOnce(1200);
            };
            _mSubtitles.DropDownItems.Add(off);
            _mSubtitles.DropDownItems.Add(new ToolStripSeparator());

            foreach (var s in streams)
            {
                var name = string.IsNullOrWhiteSpace(s.Name) ? $"Sub {s.Group}:{s.GlobalIndex}" : s.Name;
                var it = new ToolStripMenuItem(name) { Checked = s.Selected };
                int idx = s.GlobalIndex;
                it.Click += (_, __) =>
                {
                    _engine?.EnableByGlobalIndex(idx);
                    _lblStatus.Text = $"Sottotitoli: {name}";
                    _hud.ShowOnce(1200);
                };
                _mSubtitles.DropDownItems.Add(it);
            }
        }

        private static bool LooksHdmi(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            string[] hdmi = { "hdmi", "display audio", "avr", "denon", "marantz", "onkyo", "yamaha", "nvidia high definition audio", "intel(r) display audio", "amd high definition audio" };
            return hdmi.Any(n.Contains);
        }

        private void OpenFile()
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "Media|*.mkv;*.mp4;*.m2ts;*.ts;*.mov;*.avi;*.wmv;*.webm;*.flac;*.mp3;*.mka;*.aac;*.ogg;*.wav;*.wma;*.m4a;*.opus|Tutti i file|*.*"
            };
            if (ofd.ShowDialog() != DialogResult.OK) return;
            OpenPath(ofd.FileName);
        }

        private void OpenPath(string path, double resume = 0, bool startPaused = false)
        {
            StopAll();
            _currentPath = path;

            try { _info = MediaProbe.Probe(path); }
            catch (Exception ex) { _lblStatus.Text = "Probe fallito: " + ex.Message; _info = null; }

            bool hasVideo = _info?.HasVideo == true;
            bool fileHdr = _info?.IsHdr == true;
            bool hdmi = _selectedRendererLooksHdmi;
            bool passCandidate = _info != null && MediaProbe.IsPassthroughCandidate(_info.AudioCodec);
            bool wantBitstream = (hdmi && passCandidate);

            var order =
                _manualRendererChoice.HasValue
                ? new[] { _manualRendererChoice.Value }
                : (fileHdr ? ORDER_HDR : ORDER_SDR);

            Dbg.Log($"OpenPath '{path}', HDR_File={fileHdr}, UI_HDR={_hdr}, hasVideo={hasVideo}, wantBitstream={wantBitstream}, order=[{string.Join(",", order)}]");

            foreach (var choice in order)
            {
                try
                {
                    _engine = new DirectShowUnifiedEngine(
                        preferBitstream: wantBitstream,
                        preferredRendererName: _selectedAudioRendererName,
                        choice: choice,
                        fileIsHdr: fileHdr,
                        srcAudioCodec: _info?.AudioCodec ?? AVCodecID.AV_CODEC_ID_NONE);

                    _engine.OnStatus += s => BeginInvoke(new Action(() => _lblStatus.Text = s));
                    _engine.OnProgressSeconds += s => BeginInvoke(new Action(() => UpdateTime(s)));
                    _engine.BindUpdateCallback(() =>
                    {
                        if (IsHandleCreated) BeginInvoke(new Action(() =>
                        {
                            _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);
                            _hud.BringToFront();
                            BringOverlaysToFront();
                        }));
                    });

                    UseOverlayInline(true);
                    _engine.Open(path, hasVideo);

                    _duration = _engine.DurationSeconds > 0 ? _engine.DurationSeconds : (_info?.Duration ?? 0);

                    _splash.Visible = false;
                    BringOverlaysToFront();

                    _audioOnlyBanner.Visible = !hasVideo;
                    BringOverlaysToFront();
                    try { if (hasVideo) _thumb.Open(path); } catch { }

                    if (resume > 0 && _duration > 0) _engine.PositionSeconds = Math.Min(resume, _duration - 0.25);

                    _engine.SetStereo3D(_stereo);
                    _engine.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);
                    if (!_overlayInlineMode)
                    {
                        _overlayHost.Show(this);
                        _overlayHost.SyncTo(this);
                    }
                    BringOverlaysToFront();

                    UpdateInfoOverlay(choice, fileHdr);
                    _hud.ShowOnce(2000);

                    _paused = startPaused;
                    try
                    {
                        if (!startPaused)
                        {
                            _engine.Play();
                            _hud.TimelineVisible = true;
                        }
                        else
                        {
                            _engine.Pause();
                            _hud.TimelineVisible = false;
                        }
                    }
                    catch { }

                    ApplyVolume(1f);

                    var t = new System.Windows.Forms.Timer { Interval = 300 };
                    t.Tick += (_, __) =>
                    {
                        try
                        {
                            _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);
                            BringOverlaysToFront();
                            _hud.ShowOnce(1000);
                        }
                        catch { }
                        finally { t.Stop(); t.Dispose(); }
                    };
                    t.Start();

                    bool okDisplay = _engine.HasDisplayControl();
                    if (!hasVideo || okDisplay)
                    {
                        string tag = fileHdr ? "HDR" : "SDR";
                        _lblStatus.Text = hasVideo ? $"Riproduzione ({choice} • {tag})" : "Riproduzione (solo audio)";
                        return;
                    }

                    throw new Exception("Renderer non pronto (nessun display control) → fallback");
                }
                catch (Exception ex)
                {
                    Dbg.Warn($"OpenPath: renderer {choice} EX: " + ex.Message);
                    try { _engine?.Dispose(); } catch { }
                    _engine = null;

                    if (_manualRendererChoice == VideoRendererChoice.MADVR && (ex.Message?.IndexOf("madVR non trovato", StringComparison.OrdinalIgnoreCase) >= 0))
                        _lblStatus.Text = "madVR non installato. Esegui 'install.bat' come Amministratore nella cartella di madVR, poi riprova.";
                }
            }

            _lblStatus.Text = "Impossibile presentare il video con i renderer selezionati";
        }

        private void UpdateInfoOverlay(VRChoice renderer, bool fileHdr)
        {
            if (_info == null || _engine == null) return;

            var (w, h, sub) = _engine.GetNegotiatedVideoFormat();
            var selAudio = _engine.EnumerateStreams().FirstOrDefault(s => s.IsAudio && s.Selected);
            string audioPretty = PrettyAudioName(_info, selAudio?.Name);

            var s = new InfoOverlay.Stats
            {
                Title = Path.GetFileName(_currentPath) ?? "—",
                VideoIn = $"{_info.Width}x{_info.Height} • {CodecName(_info.VideoCodec)} • {(_info.VideoBits > 0 ? _info.VideoBits + "-bit" : "8-bit?")}",
                VideoOut = $"{(w > 0 ? $"{w}x{h}" : "n/d")} • {sub}",
                VideoCodec = CodecName(_info.VideoCodec),
                VideoPrimaries = PrimName(_info.Primaries),
                VideoTransfer = TrcName(_info.Transfer),
                VideoBitrateNow = "n/d",
                VideoBitrateAvg = "n/d",
                AudioIn = $"{audioPretty} • {(_info.AudioRate > 0 ? _info.AudioRate / 1000 + " kHz" : "n/d")} • {AudioChText(_info)}",
                AudioOut = _engine.IsBitstreamActive() ? "Bitstream (pass-through)" : "PCM",
                AudioBitrateNow = "n/d",
                AudioBitrateAvg = "n/d",
                Renderer = renderer.ToString(),
                HdrMode = fileHdr ? (_hdr == HdrMode.Auto ? "HDR (auto)" : "SDR (tone-map)") : "SDR",
                Upscaling = false,
                RtxHdr = false
            };

            _infoOverlay.SetStats(s);

            static string CodecName(AVCodecID id) => id switch
            {
                AVCodecID.AV_CODEC_ID_HEVC => "HEVC",
                AVCodecID.AV_CODEC_ID_H264 => "H.264",
                AVCodecID.AV_CODEC_ID_VP9 => "VP9",
                AVCodecID.AV_CODEC_ID_AV1 => "AV1",
                AVCodecID.AV_CODEC_ID_TRUEHD => "Dolby TrueHD",
                AVCodecID.AV_CODEC_ID_EAC3 => "Dolby Digital Plus",
                AVCodecID.AV_CODEC_ID_AC3 => "Dolby Digital",
                AVCodecID.AV_CODEC_ID_DTS => "DTS",
                _ => id.ToString().Replace("AV_CODEC_ID_", "")
            };

            static string PrimName(AVColorPrimaries p) =>
                p == AVColorPrimaries.AVCOL_PRI_BT2020 ? "BT.2020" :
                p == AVColorPrimaries.AVCOL_PRI_BT709 ? "BT.709" :
                p == AVColorPrimaries.AVCOL_PRI_SMPTE170M ? "SMPTE 170M" :
                p.ToString().Replace("AVCOL_PRI_", "");

            static string TrcName(AVColorTransferCharacteristic t) =>
                t == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 ? "PQ" :
                t == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 ? "HLG" :
                t == AVColorTransferCharacteristic.AVCOL_TRC_BT709 ? "BT.709" :
                t.ToString().Replace("AVCOL_TRC_", "");

            static string AudioChText(MediaProbe.Result r)
            {
                if (!string.IsNullOrWhiteSpace(r.AudioLayoutText)) return $"{r.AudioChannels}ch ({r.AudioLayoutText})";
                if (r.AudioLooksObjectBased) return $"{r.AudioChannels}ch (object-based/Atmos?)";
                return $"{r.AudioChannels}ch";
            }
        }

        private void ReopenSame()
        {
            if (string.IsNullOrEmpty(_currentPath)) return;
            double pos = _engine?.PositionSeconds ?? 0; bool paused = _paused;
            OpenPath(_currentPath!, resume: pos, startPaused: paused);
        }
        private void TogglePlayPause()
        {
            if (_engine == null) return;
            _paused = !_paused;
            if (_paused) { _engine.Pause(); }
            else { _engine.Play(); _hud.TimelineVisible = true; } // [ADD]
            _hud.ShowOnce(1200);
        }

        private void ToggleFullscreen()
        {
            var screen = Screen.FromControl(this);
            if (FormBorderStyle != FormBorderStyle.None)
            {
                _prevBorder = FormBorderStyle; _prevState = WindowState;
                FormBorderStyle = FormBorderStyle.None;
                TopMost = true;
                WindowState = FormWindowState.Normal;
                Bounds = screen.Bounds;
                Win32.SetWindowPos(this.Handle, Win32.HWND_TOPMOST,
                    Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height,
                    Win32.SWP_SHOWWINDOW | Win32.SWP_FRAMECHANGED);
                _hud.AutoHide = true;
            }
            else
            {
                TopMost = false;
                FormBorderStyle = _prevBorder;
                WindowState = _prevState;
                Win32.SetWindowPos(this.Handle, Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_FRAMECHANGED);
                _hud.AutoHide = false;
            }

            _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);
            _overlayHost?.SyncTo(this);
            _hud.BringToFront();
            _hud.ShowOnce(1500);
        }

        private void UpdateTime(double cur) { _hud?.Invalidate(); }

        private static string Fmt(double s) { if (double.IsNaN(s) || s < 0) s = 0; var ts = TimeSpan.FromSeconds(s); return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss"); }

        private void StopAll()
        {
            try { _engine?.Stop(); } catch { }
            try { _engine?.Dispose(); } catch { }
            _engine = null;

            // ✅ torna all’overlay “finestra” per evitare il velo nero
            UseOverlayInline(false);

            _duration = 0; _paused = false;
            _thumbCts?.Cancel(); _thumbCts = null; try { _thumb.Close(); } catch { }
            _audioOnlyBanner.Visible = false;
            _currentPath = null;
            _hud.TimelineVisible = false;
            _infoOverlay.Visible = false;
            _hud.Visible = false;
            _audioOnlyBanner.Visible = false;
            _splash.Visible = true;

            _overlayHost?.SyncTo(this);
            _overlayHost?.Hide();

            _hud.SetPreview(null, 0);
            BringOverlaysToFront();
        }

        private void ShowChaptersMenu()
        {
            if (_info == null || _info.Chapters.Count == 0) { _lblStatus.Text = "Nessun capitolo rilevato"; return; }
            var menu = new ContextMenuStrip();
            foreach (var (title, start) in _info.Chapters)
            {
                var it = new ToolStripMenuItem($"{Fmt(start)}  {title}"); double s = start;
                it.Click += (_, __) => { if (_engine != null) _engine.PositionSeconds = s; _hud.ShowOnce(1200); };
                menu.Items.Add(it);
            }
            menu.Show(Cursor.Position);
        }

        private void OnPreviewRequested(double seconds, Point _)
        {
            if (_thumbCts != null && !_thumbCts.IsCancellationRequested && _previewBusy) return;
            if (string.IsNullOrEmpty(_currentPath) || _info == null || !_info.HasVideo) { _hud.SetPreview(null, seconds); return; }
            _thumbCts?.Cancel(); _thumbCts = new CancellationTokenSource(); var tk = _thumbCts.Token;
            Task.Run(() =>
            {
                _previewBusy = true;
                try
                {
                    var bmp = _thumb.Get(seconds);
                    if (tk.IsCancellationRequested) { bmp?.Dispose(); return; }
                    BeginInvoke(new Action(() => _hud.SetPreview(bmp, seconds)));
                }
                catch { BeginInvoke(new Action(() => _hud.SetPreview(null, seconds))); }
                finally { _previewBusy = false; }
            }, tk);
        }

        private void ApplyVolume(float v)
        {
            try { _engine?.SetVolume(v); } catch { }
            try { CoreAudioSessionVolume.Set(v); } catch { }
        }
    }

    // ======= Splash overlay =======
    internal sealed class SplashOverlay : Control
    {
        public event Action? OpenRequested;
        // ✅ gli eventi devono stare a livello di classe, non dentro al costruttore
        public event Action? SettingsRequested;
        public event Action? CreditsRequested;

        private Image? _img;

        // Icone per i tre pulsanti (opzionali, fallback a glifi testuali)
        private Image? _icoOpen, _icoSettings, _icoCredits;

        // hitbox per click runtime
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

            int size = Math.Max(44, Math.Min(60, (int)Math.Round(Height * 0.055)));
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
            g.Clear(Color.Black);
            if (_img == null) return;

            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            int maxW = (int)(Width * 0.60);
            int maxH = (int)(Height * 0.60);
            double s = Math.Min(maxW / (double)_img.Width, maxH / (double)_img.Height);
            int w = Math.Max(1, (int)Math.Round(_img.Width * s));
            int h = Math.Max(1, (int)Math.Round(_img.Height * s));
            int x = (Width - w) / 2;
            int y = (Height - h) / 2;

            g.DrawImage(_img, x, y, w, h);

            // -- Tre bottoni circolari (responsive) --
            int size = Math.Max(44, Math.Min(60, (int)Math.Round(Height * 0.055))); // 44..60 px in base all'altezza
            int gap = Math.Max(14, Math.Min(28, (int)Math.Round(size * 0.35)));    // spazio orizzontale tra i cerchi

            // t = 0 a ~800px di altezza, t = 1 a ~1400px+
            double t = Math.Clamp((Height - 800) / 600.0, 0, 1);

            // più vicino al logo anche in fullscreen: da -40px (finestre basse) a -14px (schermi alti)
            int gapBelowLogo = (int)Math.Round(-40 + (-150 - (-40)) * t); // == -40 + 26*t

            // base: subito sotto al logo + offset adattivo
            int cy = y + h + gapBelowLogo;

            // evita che finiscano troppo vicino al bordo basso
            int bottomMargin = Math.Max(16, size / 2);
            cy = Math.Min(cy, Height - bottomMargin - size);

            // posizionamento orizzontale
            Rectangle rcOpen = new Rectangle(Width / 2 - size / 2, cy, size, size);
            Rectangle rcSettings = new Rectangle(rcOpen.X - size - gap, cy, size, size);
            Rectangle rcCredits = new Rectangle(rcOpen.Right + gap, cy, size, size);

            // disegno cerchi + icone PNG (senza fallback testuale)
            void DrawCircle(Graphics gg, Rectangle r)
            {
                using var bg = new SolidBrush(Color.FromArgb(38, 255, 255, 255));
                using var pen = new Pen(Color.FromArgb(80, 255, 255, 255), 1f);
                gg.FillEllipse(bg, r);
                gg.DrawEllipse(pen, r);
            }
            void DrawPng(Graphics gg, Rectangle r, Image? ico)
            {
                if (ico == null) return;
                int pad = Math.Max(10, (int)Math.Round(size * 0.22));
                gg.DrawImage(ico, new Rectangle(r.X + pad, r.Y + pad, r.Width - pad * 2, r.Height - pad * 2));
            }

            DrawCircle(g, rcSettings);
            DrawCircle(g, rcOpen);
            DrawCircle(g, rcCredits);

            DrawPng(g, rcSettings, _icoSettings);
            DrawPng(g, rcOpen, _icoOpen);
            DrawPng(g, rcCredits, _icoCredits);

            RecomputeButtonHitboxes();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_lastRcOpen.Contains(e.Location)) { OpenRequested?.Invoke(); return; }
            if (_lastRcSettings.Contains(e.Location)) { SettingsRequested?.Invoke(); return; }
            if (_lastRcCredits.Contains(e.Location)) { CreditsRequested?.Invoke(); return; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _img?.Dispose();
            base.Dispose(disposing);
        }
    }

    // ======= Loading overlay =======
    internal sealed class LoadingOverlay : Control
    {
        public event Action? Completed;

        private Image? _logo;
        private readonly System.Windows.Forms.Timer _tick;
        private DateTime _start;
        private double _progress01;
        private int _durationMs = 2200;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int DurationMs { get => _durationMs; set => _durationMs = Math.Max(500, value); }

        public LoadingOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);

            BackColor = Color.Transparent;

            var p = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (File.Exists(p)) _logo = Image.FromFile(p);

            _tick = new System.Windows.Forms.Timer { Interval = 16 };
            _tick.Tick += (_, __) =>
            {
                var t = (DateTime.UtcNow - _start).TotalMilliseconds;
                _progress01 = Math.Clamp(EaseOutCubic(t / _durationMs), 0, 1);
                Invalidate();
                if (_progress01 >= 1.0) { _tick.Stop(); Completed?.Invoke(); }
            };
        }

        public void Start()
        {
            _progress01 = 0;
            _start = DateTime.UtcNow;
            _tick.Start();
            Invalidate();
        }

        protected override CreateParams CreateParams
        { get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;

            if (_logo != null)
            {
                double maxW = Width * 0.52;
                double maxH = Height * 0.42;
                double scale = Math.Min(maxW / _logo.Width, maxH / _logo.Height);
                int w = Math.Max(1, (int)Math.Round(_logo.Width * scale));
                int h = Math.Max(1, (int)Math.Round(_logo.Height * scale));
                int x = (Width - w) / 2;
                int y = (Height - h) / 2 - 20;

                using (var glow = new SolidBrush(Color.FromArgb(46, 0, 0, 0)))
                    g.FillEllipse(glow, x - w * 0.08f, y - h * 0.08f, w * 1.16f, h * 1.16f);

                g.DrawImage(_logo, new Rectangle(x, y, w, h));
            }

            var barW = (int)Math.Round(Math.Min(Width * 0.84, 760));
            var barH = 14;
            var barX = (Width - barW) / 2;
            var barY = (int)Math.Round(Height * 0.62);
            var barRect = new Rectangle(barX, barY, barW, barH);
            int rr = barH / 2;

            int fillW = (int)Math.Round(barRect.Width * _progress01);
            if (fillW > 0)
            {
                var fillRect = new Rectangle(barRect.X, barRect.Y, Math.Max(1, fillW), barRect.Height);
                using (var glow = new SolidBrush(Color.FromArgb(35, 64, 200, 255)))
                    g.FillRoundedRectangle(glow, new Rectangle(fillRect.X - 2, fillRect.Y - 2, fillRect.Width + 4, fillRect.Height + 4), new Size(rr + 2, rr + 2));

                using (var lg = new LinearGradientBrush(fillRect, Color.Cyan, Color.Magenta, 0f))
                {
                    var cb = new ColorBlend
                    {
                        Colors = new[]
                        {
                            Color.FromArgb(255, 32, 216, 255),
                            Color.FromArgb(255, 64, 160, 255),
                            Color.FromArgb(255, 255, 60, 168)
                        },
                        Positions = new[] { 0f, 0.55f, 1f }
                    };
                    lg.InterpolationColors = cb;
                    g.FillRoundedRectangle(lg, fillRect, new Size(rr, rr));
                }
                using var hi = new SolidBrush(Color.FromArgb(140, 255, 255, 255));
                g.FillRectangle(hi, fillRect.X + 4, fillRect.Y + 1, Math.Max(0, fillRect.Width - 8), 2);
            }

            var s = "Powered by madVR";
            using var f = new Font("Segoe UI", 9.25f, FontStyle.Bold);
            var sz = g.MeasureString(s, f);
            var px = Width - (int)sz.Width - 16;
            var py = Height - (int)sz.Height - 10;

            using (var shadow = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                g.DrawString(s, f, shadow, px + 1, py + 1);
            using (var white = new SolidBrush(Color.FromArgb(230, 255, 255, 255)))
                g.DrawString(s, f, white, px, py);
        }

        private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _logo?.Dispose(); _tick?.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // ======= Info overlay – 2 colonne (VIDEO/AUDIO) + Sistema =======
    internal sealed class InfoOverlay : Control
    {
        public struct Stats
        {
            public string Title;
            public string VideoIn, VideoOut, VideoCodec, VideoPrimaries, VideoTransfer, VideoBitrateNow, VideoBitrateAvg;
            public string AudioIn, AudioOut, AudioBitrateNow, AudioBitrateAvg;
            public string Renderer, HdrMode;
            public bool Upscaling, RtxHdr;
        }

        private Stats _s;

        public bool AutoHeight { get; set; } = true;
        public int MinCardHeight { get; set; } = 120;
        public int MaxCardHeight { get; set; } = 420;

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
        protected override void OnPaintBackground(PaintEventArgs e) { }

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

            // Titolo
            var rcTitle = new Rectangle(x, y, w, fTitle.Height + 2);
            TextRenderer.DrawText(g, string.IsNullOrWhiteSpace(_s.Title) ? "—" : _s.Title,
                fTitle, rcTitle, _txt, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            y = rcTitle.Bottom + 6;

            // DUE COLONNE: VIDEO | AUDIO
            int gap = 28;
            int colW = (w - gap) / 2;
            int col1X = x;
            int col2X = x + colW + gap;

            TextRenderer.DrawText(g, "VIDEO", fHdr, new Rectangle(col1X, y, colW, fHdr.Height + 2), _txt, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "AUDIO", fHdr, new Rectangle(col2X, y, colW, fHdr.Height + 2), _txt, TextFormatFlags.NoPadding);
            y += fHdr.Height + 6;

            DrawInOut(g, fKey, fVal, "IN", _s.VideoIn, "OUT", _s.VideoOut, col1X, y, colW);
            DrawInOut(g, fKey, fVal, "IN", _s.AudioIn, "OUT", _s.AudioOut, col2X, y, colW);

            y += ROW_H + 6;

            using (var p = new Pen(Color.FromArgb(38, 255, 255, 255), 1))
                g.DrawLine(p, x, y, x + w, y);
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

            var txRc = new Rectangle(rc.X + padX, rc.Y + (h - sz.Height) / 2, rc.Width - padX * 2, sz.Height);
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

            // wrap su nuova riga se serve
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
            y += fTitle.Height + 6;
            y += fHdr.Height + 6;
            y += ROW_H + 6;
            y += 8;
            y += fHdr.Height + 6;
            y += fTag.Height + 10;

            y += PAD;
            return y + 4;
        }
    }

    // ======= HUD overlay =======
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

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<string>? GetInfoLine { get; set; }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<(double pos, double dur)>? GetTime { get; set; }

        [DefaultValue(false)]
        public bool AutoHide { get; set; }

        [DefaultValue(2000)]
        public int IdleHideDelayMs { get; set; } = 2000;

        [DefaultValue(900)]
        public int HideGraceMs { get; set; } = 900;

        [DefaultValue(120)]
        public int FadeOutMs { get; set; } = 120;

        [DefaultValue(false)]
        public bool TimelineVisible { get; set; } = false;

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
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) StopDragging(); }
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

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } } // WS_EX_TRANSPARENT

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
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float step = e.Delta > 0 ? 0.05f : -0.05f;
            _vol = Math.Clamp(_vol + step, 0f, 1f);
            VolumeChanged?.Invoke(_vol);
            Invalidate();
            ShowOnce(800);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;

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
                    using var bb = new SolidBrush(Color.FromArgb((int)(220 * _opacity), 0, 0, 0));
                    using var wb = new SolidBrush(Color.FromArgb((int)(255 * _opacity), 255, 255, 255));
                    int boxW = Math.Max((int)(ptsz.Width + 10), pw);
                    g.FillRectangle(bb, px, py - ptsz.Height - 6, boxW, ptsz.Height + 6);
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
        protected override void OnPaintBackground(PaintEventArgs pevent) { }
    }

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
