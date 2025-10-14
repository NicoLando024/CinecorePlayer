// CinecorePlayer2025 - Unified DirectShow engine con HDR auto, EVR/MPCVR/VMR9/madVR, HUD autohide, splash centrale, info overlay orizzontale
// Target: .NET 9.0, Windows 11
// NuGet: DirectShowLib (>= 2.1.0), FFmpeg.AutoGen (>= 6.x)
// Abilita /unsafe nel progetto.
#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using DirectShowLib;
using FFmpeg.AutoGen;

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

    // ======= DEBUG CORE (file + ring buffer + helper) =======
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

        public static event Action? OnNewLines;

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
            try { OnNewLines?.Invoke(); } catch { }
        }

        static string Stamp(string msg) => $"{DateTime.Now:HH:mm:ss.fff} | {msg}";
        public static void Log(string msg, LogLevel lvl = LogLevel.Info) { if (lvl > Level) return; Enqueue(Stamp(msg)); }
        public static void Warn(string msg) => Log("WARN: " + msg, LogLevel.Warn);
        public static void Error(string msg) => Log("ERROR: " + msg, LogLevel.Error);

        public static string[] Snapshot() { lock (_lock) return _ring.ToArray(); }

        public static string Hex(Guid g) => g.ToString("B").ToUpperInvariant();
        public static string Safe(object? o) => o == null ? "null" : o.ToString() ?? "null";
    }

    // ======= ENUM & MODALITÀ =======
    public enum HdrMode { Auto, Off }                   // Off = forza SDR (tone-map con madVR/MPCVR)
    public enum Stereo3DMode { None, SBS, TAB }

    // Aggiunta MADVR
    public enum VideoRendererChoice { MADVR, MPCVR, EVR, VMR9 }

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

                        // layout leggibile (FFmpeg >= 5)
                        try
                        {
                            var buf = stackalloc sbyte[128];
                            ffmpeg.av_channel_layout_describe(&par->ch_layout, (byte*)buf, 128);
                            r.AudioLayoutText = Marshal.PtrToStringAnsi((IntPtr)buf) ?? "";
                        }
                        catch { r.AudioLayoutText = ""; }

                        // Heuristica "object-based (Atmos?)"
                        if (par->codec_id == AVCodecID.AV_CODEC_ID_TRUEHD || par->codec_id == AVCodecID.AV_CODEC_ID_EAC3)
                        {
                            // E-AC-3 JOC e TrueHD+Atmos spesso espongono solo il "bed" 5.1/7.1: segnalo come object-based
                            r.AudioLooksObjectBased = r.AudioChannels >= 6;
                        }
                    }
                }

                // capitoli
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
                bool bt2020 = prim == AVColorPrimaries.AVCOL_PRI_BT2020; // compat
                return pq || hlg || (bt2020 && bits >= 10);
            }
        }

        public static bool IsPassthroughCandidate(AVCodecID id) =>
            id == AVCodecID.AV_CODEC_ID_TRUEHD || id == AVCodecID.AV_CODEC_ID_EAC3 ||
            id == AVCodecID.AV_CODEC_ID_AC3 || id == AVCodecID.AV_CODEC_ID_DTS;
    }

    // ======= Thumbnailer (FFmpeg)
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
        bool HasDisplayControl(); // EVR/VMR9/IVideoWindow

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
        private readonly FFmpeg.AutoGen.AVCodecID _srcAudioCodec;

        private IGraphBuilder? _graph;
        private IMediaControl? _control;
        private IMediaSeeking? _seek;
        private IBasicAudio? _basicAudio;

        private IBaseFilter? _lavSource, _lavVideo, _lavAudio, _videoRenderer, _audioRenderer;

        private IMFVideoDisplayControl? _mfDisplay; // EVR
        private IVMRWindowlessControl9? _vmrWC;     // VMR9
        private IVideoWindow? _videoWindow;         // MPCVR/madVR/windowed

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
        public bool HasDisplayControl() => _mfDisplay != null || _vmrWC != null || _videoWindow != null;

        public DirectShowUnifiedEngine(bool preferBitstream, string? preferredRendererName, VideoRendererChoice choice, bool fileIsHdr, FFmpeg.AutoGen.AVCodecID srcAudioCodec)
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
                _videoRenderer = CreateVideoRendererByChoice(_choice);
                if (_videoRenderer == null) throw new ApplicationException("Renderer video non disponibile");
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

                // Primo tentativo (pre-connessione)
                AttachDisplayInterfaces(initial: true);
            }

            var fileSrc = (IFileSourceFilter)_lavSource;
            hr = fileSrc.Load(mediaPath, null); DsError.ThrowExceptionForHR(hr);
            Dbg.Log("File sorgente caricato");

            ConnectAudioPath();
            if (_hasVideo) ConnectVideoPath(); // (post-connessione) e fallback se non c'è display control

            StartTimer();
            var msg = $"Grafo pronto ({(_preferBitstream ? "Bitstream-first" : "PCM")}{(_hasVideo ? ", video" : ", solo audio")}).";
            Dbg.Log(msg);
            OnStatus?.Invoke(msg);
        }

        private void HdrTrace(string text)
        {
            if (_fileIsHdr)
                Dbg.Log("[HDR] " + text, Dbg.LogLevel.Info);
        }

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
                    int hr = _graph!.Connect(srcA, aIn); DsError.ThrowExceptionForHR(hr); Dbg.Log("Audio: LAV Splitter → LAV Audio", Dbg.LogLevel.Verbose);
                    hr = _graph!.Connect(aOut, rIn); DsError.ThrowExceptionForHR(hr); Dbg.Log("Audio: LAV Audio → Renderer", Dbg.LogLevel.Verbose);
                }
                else if (rIn != null)
                {
                    int hr = _graph!.Connect(srcA, rIn); DsError.ThrowExceptionForHR(hr); Dbg.Log("Audio: LAV Splitter → Renderer (direct)");
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

                // LAV Video → Renderer: prova ConnectDirect per preservare P010 con MPCVR/madVR
                var vOut = FindPin(_lavVideo!, PinDirection.Output, null);
                var rIn = FindPin(_videoRenderer!, PinDirection.Input, null);
                if (vOut == null || rIn == null) throw new ApplicationException("Pin video non trovati");

                int hrDirect = _graph!.ConnectDirect(vOut, rIn, null);
                if (hrDirect == 0)
                    Dbg.Log("Video: LAV Video → Renderer (ConnectDirect) OK", Dbg.LogLevel.Verbose);
                else
                {
                    DsError.ThrowExceptionForHR(_graph!.Connect(vOut, rIn));
                    Dbg.Log("Video: LAV Video → Renderer (Connect) OK", Dbg.LogLevel.Verbose);
                }

                // EVITA di toccare le interfacce se siamo su renderer windowed ed abbiamo già IVideoWindow
                bool keepPreWindowed = (_choice == VideoRendererChoice.MPCVR || _choice == VideoRendererChoice.MADVR) && (_videoWindow != null);
                if (!keepPreWindowed)
                    AttachDisplayInterfaces(initial: false);

                // Se comunque non abbiamo alcun display control → fallback
                if (_hasVideo && _mfDisplay == null && _vmrWC == null && _videoWindow == null)
                {
                    string rn = FilterFriendlyName(_videoRenderer!);
                    Dbg.Warn($"Nessun display control disponibile con renderer '{rn}'. Forzo fallback.");
                    throw new ApplicationException("No display control from renderer");
                }

                // Dump negoziazione (best-effort)
                TryDumpNegotiatedVideoMT();

                // Log HDR size (solo EVR)
                if (_fileIsHdr)
                {
                    if (_mfDisplay != null)
                    {
                        try
                        {
                            _mfDisplay.GetNativeVideoSize(out var nat, out var ar);
                            HdrTrace($"NativeVideoSize={nat.Width}x{nat.Height}  ARVideo={ar.Width}x{ar.Height}");
                        }
                        catch (Exception ex) { HdrTrace("GetNativeVideoSize EX: " + ex.Message); }
                    }
                    else HdrTrace("IMFVideoDisplayControl non disponibile (ok con madVR/MPCVR).");
                }
            }
            catch (Exception ex)
            {
                Dbg.Error("ConnectVideoPath EX: " + ex);
                throw;
            }
        }

        public void UpdateVideoWindow(IntPtr ownerHwnd, Rectangle ownerClient)
        {
            if (!_hasVideo) return;

            if (_mfDisplay == null && _vmrWC == null && _videoWindow == null)
            {
                AttachDisplayInterfaces(initial: false);
                if (_mfDisplay == null && _vmrWC == null && _videoWindow == null)
                {
                    Dbg.Warn("UpdateVideoWindow: nessun display control (EVR/VMR9/IVideoWindow).");
                    return;
                }
            }

            try
            {
                if (_mfDisplay != null)
                {
                    _mfDisplay.SetVideoWindow(ownerHwnd);

                    CalcSizesForStereo(out MFVideoNormalizedRect? src, out MFRect dest, ownerClient);

                    IntPtr pSrc = IntPtr.Zero;
                    try
                    {
                        if (src.HasValue)
                        {
                            pSrc = Marshal.AllocHGlobal(Marshal.SizeOf<MFVideoNormalizedRect>());
                            Marshal.StructureToPtr(src.Value, pSrc, false);
                        }

                        _mfDisplay.SetVideoPosition(pSrc, ref dest);
                        _mfDisplay.RepaintVideo(); // forza dipinto
                        if (_fileIsHdr) HdrTrace($"SetVideoPosition dest=({dest.left},{dest.top},{dest.right},{dest.bottom})");
                    }
                    finally
                    {
                        if (pSrc != IntPtr.Zero) { try { Marshal.FreeHGlobal(pSrc); } catch { } }
                    }
                }
                else if (_vmrWC != null)
                {
                    _vmrWC.SetVideoClippingWindow(ownerHwnd);
                    _vmrWC.GetNativeVideoSize(out int cx, out int cy, out _, out _);
                    if (cx <= 0 || cy <= 0) { cx = 1280; cy = 720; }

                    var s = StereoSourceRectPixels(cx, cy);
                    DsRect src = new DsRect(s.Left, s.Top, s.Right, s.Bottom);
                    DsRect dst = new DsRect(ownerClient.Left, ownerClient.Top, ownerClient.Right, ownerClient.Bottom);

                    _vmrWC.SetVideoPosition(src, dst);
                }
                else if (_videoWindow != null)
                {
                    // Sequenza Owner → Style → Pos → Foreground → Visible
                    _videoWindow.put_Owner(ownerHwnd);
                    _videoWindow.put_MessageDrain(ownerHwnd);
                    _videoWindow.put_WindowStyle(WindowStyle.Child | WindowStyle.ClipChildren | WindowStyle.ClipSiblings);
                    _videoWindow.SetWindowPosition(ownerClient.Left, ownerClient.Top, ownerClient.Width, ownerClient.Height);
                    try { _videoWindow.SetWindowForeground(OABool.True); } catch { }
                    _videoWindow.put_AutoShow(OABool.True);
                    _videoWindow.put_Visible(OABool.True);

                    // ripeti il posizionamento dopo il primo Visible (alcuni renderer lo richiedono)
                    _videoWindow.SetWindowPosition(ownerClient.Left, ownerClient.Top, ownerClient.Width, ownerClient.Height);
                    _videoWindow.put_Visible(OABool.True);
                }
            }
            catch (Exception ex)
            {
                OnStatus?.Invoke("UpdateVideoWindow: " + ex.Message);
                if (_fileIsHdr) HdrTrace("UpdateVideoWindow EX: " + ex);
                else Dbg.Warn("UpdateVideoWindow EX: " + ex.Message);
            }
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
                if (_videoWindow != null) { _videoWindow.put_Visible(OABool.True); }
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
            ReleaseCom(ref _vmrWC);
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
            // In post, NON toccare un IVideoWindow già valido se il renderer è windowed (MPCVR/madVR)
            bool preserveWindowedRenderer = (!initial) && (_choice == VideoRendererChoice.MPCVR || _choice == VideoRendererChoice.MADVR) && (_videoWindow != null);

            if (!initial)
            {
                ReleaseCom(ref _mfDisplay);
                ReleaseCom(ref _vmrWC);
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
            catch (Exception ex)
            {
                Dbg.Warn("AttachDisplayInterfaces (EVR/MPCVR) EX: " + ex.Message);
            }

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
            catch (Exception ex)
            {
                Dbg.Warn("AttachDisplayInterfaces (IVideoWindow/Renderer) EX: " + ex.Message);
            }

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
            catch (Exception ex)
            {
                Dbg.Warn("AttachDisplayInterfaces (IVideoWindow/Graph) EX: " + ex.Message);
            }

            // 4) VMR9 Windowless
            try
            {
                if (_videoRenderer is VideoMixingRenderer9)
                {
                    _vmrWC = (IVMRWindowlessControl9)_videoRenderer!;
                    _vmrWC.SetAspectRatioMode(VMR9AspectRatioMode.LetterBox);
                    Dbg.Log("VMR9 Windowless: IVMRWindowlessControl9 acquisito.");
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("AttachDisplayInterfaces (VMR9) EX: " + ex.Message);
            }
        }

        private MFVideoNormalizedRect StereoSourceRectNormalized()
        {
            return _stereo switch
            {
                Stereo3DMode.SBS => new MFVideoNormalizedRect(0f, 0f, 0.5f, 1f),
                Stereo3DMode.TAB => new MFVideoNormalizedRect(0f, 0f, 1f, 0.5f),
                _ => new MFVideoNormalizedRect(0f, 0f, 1f, 1f),
            };
        }

        private (int Left, int Top, int Right, int Bottom) StereoSourceRectPixels(int cx, int cy)
        {
            return _stereo switch
            {
                Stereo3DMode.SBS => (0, 0, Math.Max(1, cx / 2), cy),
                Stereo3DMode.TAB => (0, 0, cx, Math.Max(1, cy / 2)),
                _ => (0, 0, cx, cy),
            };
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
            if (_stereo == Stereo3DMode.SBS)
            {
                cropW = Math.Max(1, natW / 2);
                src = new MFVideoNormalizedRect(0f, 0f, 0.5f, 1f);
            }
            else if (_stereo == Stereo3DMode.TAB)
            {
                cropH = Math.Max(1, natH / 2);
                src = new MFVideoNormalizedRect(0f, 0f, 1f, 0.5f);
            }

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

        // === Selezione renderer audio ===
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

        private IBaseFilter? CreateFilterByName(string friendlyName)
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
                switch (c)
                {
                    case VideoRendererChoice.MADVR:
                        {
                            var f = CreateFilterByName("madVR Renderer") ?? CreateFilterByName("madVR");
                            if (f == null)
                                throw new ApplicationException("madVR non trovato. Installa eseguendo 'install.bat' come Amministratore nella cartella di madVR.");
                            return f;
                        }
                    case VideoRendererChoice.MPCVR:
                        return CreateFilterByName("MPC Video Renderer");
                    case VideoRendererChoice.EVR:
                        return CreateFilterByName("Enhanced Video Renderer");
                    case VideoRendererChoice.VMR9:
                        {
                            var vmr = (IBaseFilter)new VideoMixingRenderer9();
                            var cfg = (IVMRFilterConfig9)vmr;
                            cfg.SetNumberOfStreams(1);
                            cfg.SetRenderingMode(VMR9Mode.Windowless);
                            Dbg.Log("VMR9 creato e configurato Windowless prima dell'aggiunta.", Dbg.LogLevel.Verbose);
                            return vmr;
                        }
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("CreateVideoRendererByChoice EX: " + ex.Message);
                throw;
            }
            return null;
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
        private interface IPersist
        {
            [PreserveSig] int GetClassID(out Guid pClassID);
        }

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

                    // Heuristica: renderer generici + sorgente passthrough candidate
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
                finally
                {
                    if (pDib != IntPtr.Zero) try { Marshal.FreeCoTaskMem(pDib); } catch { }
                }
            }

            if (_vmrWC != null)
            {
                IntPtr pDib = IntPtr.Zero;
                try
                {
                    _vmrWC.GetCurrentImage(out pDib);
                    if (pDib != IntPtr.Zero)
                    {
                        int cb = Marshal.ReadInt32(pDib, 20);
                        if (cb <= 0) cb = 1;
                        byteCount = cb;
                        _lastSnapshotBytes = cb; _lastSnapshotAt = DateTime.Now;
                        return true;
                    }
                }
                catch { }
                finally
                {
                    if (pDib != IntPtr.Zero) try { Marshal.FreeCoTaskMem(pDib); } catch { }
                }
            }

            return false; // con madVR/MPCVR windowed non c'è una API standard per snapshot
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
        public MFVideoNormalizedRect(float l, float t, float r, float b) { left = l; top = t; right = r; bottom = b; }
    }

    // ======= Core Audio session (bitstream volume safe) =======
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

    // ======= UI =======
    public sealed class PlayerForm : Form
    {
        private Panel _stack = null!;
        private Panel _videoHost = null!;
        private HudOverlay _hud = null!;
        private DebugOverlay _debugOverlay = null!;
        private InfoOverlay _infoOverlay = null!;
        private SplashOverlay _splash = null!;
        private Label _lblStatus = null!;
        private Label _lblTime = null!;
        private Panel _audioOnlyBanner = null!;

        private ContextMenuStrip _menu = null!;

        private IPlaybackEngine? _engine;
        private string? _currentPath;
        private MediaProbe.Result? _info;

        private HdrMode _hdr = HdrMode.Auto;
        private string? _selectedAudioRendererName;
        private bool _selectedRendererLooksHdmi;
        private Stereo3DMode _stereo = Stereo3DMode.None;

        private double _duration;
        private bool _paused;

        private Thumbnailer _thumb = new();
        private CancellationTokenSource? _thumbCts;
        private volatile bool _previewBusy;

        private FormWindowState _prevState; private FormBorderStyle _prevBorder;

        private static readonly VideoRendererChoice[] ORDER_HDR = { VideoRendererChoice.MADVR, VideoRendererChoice.MPCVR };
        private static readonly VideoRendererChoice[] ORDER_SDR = { VideoRendererChoice.EVR, VideoRendererChoice.VMR9 };

        private readonly System.Windows.Forms.Timer _debugPump;

        private ToolStripMenuItem _mAudioLang = null!;
        private ToolStripMenuItem _mSubtitles = null!;

        private VideoRendererChoice? _manualRendererChoice = null;

        public PlayerForm()
        {
            Text = "Cinecore Player 2025";
            MinimumSize = new Size(1040, 600);
            BackColor = Color.FromArgb(18, 18, 18);
            DoubleBuffered = true;
            KeyPreview = true;
            KeyDown += PlayerForm_KeyDown;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

            _stack = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            _videoHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            _hud = new HudOverlay { Dock = DockStyle.Fill, AutoHide = false };
            _debugOverlay = new DebugOverlay { Dock = DockStyle.Fill, Visible = false };
            _infoOverlay = new InfoOverlay { Dock = DockStyle.Top, Height = 120, Visible = false };  // info “orizzontale”
            _splash = new SplashOverlay { Dock = DockStyle.Fill, Visible = true };
            _splash.Click += (_, __) => OpenFile();
            _splash.OpenRequested += () => OpenFile();

            _audioOnlyBanner = BuildAudioOnlyBanner();

            // Ordine: video in fondo, poi splash, poi banner audio-only, poi HUD, info, debug
            _stack.Controls.Add(_videoHost);
            _stack.Controls.Add(_splash);
            _stack.Controls.Add(_audioOnlyBanner);
            _stack.Controls.Add(_hud);
            _stack.Controls.Add(_infoOverlay);
            _stack.Controls.Add(_debugOverlay);
            _hud.BringToFront();
            _debugOverlay.BringToFront();

            layout.Controls.Add(_stack, 0, 0);

            var status = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 28, 28) };
            _lblTime = new Label { AutoSize = true, ForeColor = Color.Gainsboro, Dock = DockStyle.Left, Margin = new Padding(8, 5, 8, 5), Text = "00:00 / 00:00" };
            _lblStatus = new Label { AutoSize = true, ForeColor = Color.FromArgb(255, 180, 90), Dock = DockStyle.Right, Margin = new Padding(8, 5, 8, 5), Text = "Pronto" };
            status.Controls.Add(_lblStatus); status.Controls.Add(_lblTime);
            layout.Controls.Add(status, 0, 1);

            Controls.Add(layout);

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
                _hud.BringToFront();
                UpdateDebugPanel();
            };

            BuildMenu();
            ContextMenuStrip = _menu;
            _stack.ContextMenuStrip = _menu;
            _hud.ContextMenuStrip = _menu;
            _videoHost.ContextMenuStrip = _menu;
            _splash.ContextMenuStrip = _menu;

            _hud.SetExternalVolume(1f);

            Dbg.Level = Dbg.LogLevel.Info;
            Dbg.OnNewLines += () => BeginInvoke(new Action(UpdateDebugPanel));

            _debugPump = new System.Windows.Forms.Timer { Interval = 500 };
            _debugPump.Tick += (_, __) => UpdateDebugPanel();
            _debugPump.Start();
        }

        private void PlayerForm_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { TogglePlayPause(); e.Handled = true; }
            else if (e.KeyCode == Keys.F) { ToggleFullscreen(); e.Handled = true; }
            else if (e.KeyCode == Keys.Left) { SeekRelative(-10); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { SeekRelative(10); e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { _hud?.PerformVolumeDelta(+0.05f, v => ApplyVolume(v)); e.Handled = true; _hud.ShowOnce(800); }
            else if (e.KeyCode == Keys.Down) { _hud?.PerformVolumeDelta(-0.05f, v => ApplyVolume(v)); e.Handled = true; _hud.ShowOnce(800); }
            else if (e.KeyCode == Keys.PageUp) { SeekChapter(+1); e.Handled = true; }
            else if (e.KeyCode == Keys.PageDown) { SeekChapter(-1); e.Handled = true; }
            else if (e.KeyCode == Keys.O) { OpenFile(); e.Handled = true; }
            else if (e.KeyCode == Keys.S) { StopAll(); e.Handled = true; }
            else if (e.KeyCode == Keys.D) { _debugOverlay.Visible = !_debugOverlay.Visible; UpdateDebugPanel(); e.Handled = true; }
            else if (e.KeyCode == Keys.I) { _infoOverlay.Visible = !_infoOverlay.Visible; }
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

        private Panel BuildAudioOnlyBanner()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Visible = false };
            Control art;
            var asset = Path.Combine(AppContext.BaseDirectory, "Assets", "audio-only.png");
            if (File.Exists(asset))
            {
                var img = Image.FromFile(asset);
                art = new PictureBox { Image = img, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill };
            }
            else
            {
                art = new Label
                {
                    Text = "🎵",
                    Font = new Font("Segoe UI Emoji", 72, FontStyle.Regular),
                    ForeColor = Color.White,
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Dock = DockStyle.Fill
                };
            }
            var caption = new Label
            {
                Text = "Audio Only",
                Font = new Font("Segoe UI", 16, FontStyle.Bold),
                ForeColor = Color.Gainsboro,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Bottom,
                Height = 48
            };
            p.Controls.Add(art);
            p.Controls.Add(caption);
            return p;
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
            mHdr.DropDownOpening += (_, __) =>
            {
                hAuto.Checked = _hdr == HdrMode.Auto;
                hOff.Checked = _hdr == HdrMode.Off;
            };

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

                var mRefresh = new ToolStripMenuItem("Aggiorna elenco"); mRefresh.Click += (_, __2) => { };
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

            var mShowInfo = new ToolStripMenuItem("Info overlay ON/OFF", null, (_, __) =>
            {
                _infoOverlay.Visible = !_infoOverlay.Visible;
            });

            var mDebug = new ToolStripMenuItem("Debug overlay ON/OFF", null, (_, __) =>
            {
                _debugOverlay.Visible = !_debugOverlay.Visible;
                UpdateDebugPanel();
            });

            var mRenderer = new ToolStripMenuItem("Renderer video");
            void SetRenderer(VideoRendererChoice? c)
            {
                _manualRendererChoice = c;
                _lblStatus.Text = c.HasValue ? $"Renderer video: {c}" : "Renderer video: Auto";
                ReopenSame();
            }
            var miMadvr = new ToolStripMenuItem("madVR", null, (_, __) => SetRenderer(VideoRendererChoice.MADVR));
            var miMpcvr = new ToolStripMenuItem("MPCVR", null, (_, __) => SetRenderer(VideoRendererChoice.MPCVR));
            var miEvr = new ToolStripMenuItem("EVR", null, (_, __) => SetRenderer(VideoRendererChoice.EVR));
            var miVmr9 = new ToolStripMenuItem("VMR9", null, (_, __) => SetRenderer(VideoRendererChoice.VMR9));
            var miAuto = new ToolStripMenuItem("Auto (ordine preferito)", null, (_, __) => SetRenderer(null));
            mRenderer.DropDownItems.AddRange(new ToolStripItem[] { miMadvr, miMpcvr, miEvr, miVmr9, new ToolStripSeparator(), miAuto });
            mRenderer.DropDownOpening += (_, __) =>
            {
                miMadvr.Checked = _manualRendererChoice == VideoRendererChoice.MADVR;
                miMpcvr.Checked = _manualRendererChoice == VideoRendererChoice.MPCVR;
                miEvr.Checked = _manualRendererChoice == VideoRendererChoice.EVR;
                miVmr9.Checked = _manualRendererChoice == VideoRendererChoice.VMR9;
                miAuto.Checked = _manualRendererChoice == null;
            };

            _menu.Items.AddRange(new ToolStripItem[]
            {
                mOpen, new ToolStripSeparator(), mPlay, mStop, mFull, new ToolStripSeparator(),
                mHdr, m3D, _mAudioLang, _mSubtitles, mRenderer, mDev, new ToolStripSeparator(),
                mChapters, mShowInfo, mDebug
            });
        }

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
                    UpdateDebugPanel();
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
                {
                    _lblStatus.Text = "Sottotitoli: disattivati";
                }
                else
                {
                    _lblStatus.Text = "Sottotitoli: nessuna traccia OFF esplicita";
                }
                _hud.ShowOnce(1200);
                UpdateDebugPanel();
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
                    UpdateDebugPanel();
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
                        }));
                    });

                    _engine.Open(path, hasVideo);

                    _duration = _engine.DurationSeconds > 0 ? _engine.DurationSeconds : (_info?.Duration ?? 0);
                    _lblTime.Text = $"00:00 / {Fmt(_duration)}";

                    _audioOnlyBanner.Visible = !hasVideo;
                    _splash.Visible = false;
                    if (_audioOnlyBanner.Visible) _audioOnlyBanner.BringToFront();
                    else _audioOnlyBanner.SendToBack();

                    try { if (hasVideo) _thumb.Open(path); } catch { }

                    if (resume > 0 && _duration > 0)
                        _engine.PositionSeconds = Math.Min(resume, _duration - 0.25);

                    _engine.SetStereo3D(_stereo);
                    _engine.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);

                    // Info overlay: aggiorna contenuti
                    UpdateInfoOverlay(choice, fileHdr);

                    // MOSTRA HUD all'avvio
                    _hud.ShowOnce(2000);

                    _paused = startPaused;
                    try { if (!startPaused) _engine.Play(); else _engine.Pause(); } catch { }

                    ApplyVolume(1f);

                    // "Kick" post-avvio (renderer windowed)
                    var t = new System.Windows.Forms.Timer { Interval = 300 };
                    t.Tick += (_, __) =>
                    {
                        try
                        {
                            _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);
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
                        _lblStatus.Text = hasVideo
                            ? $"Riproduzione ({choice} • {tag})"
                            : "Riproduzione (solo audio)";
                        UpdateDebugPanel();
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
            UpdateDebugPanel();
        }

        private void UpdateInfoOverlay(VideoRendererChoice renderer, bool fileHdr)
        {
            if (_info == null || _engine == null) return;

            var (w, h, sub) = _engine.GetNegotiatedVideoFormat();

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

                AudioIn = $"{CodecName(_info.AudioCodec)} • {(_info.AudioRate > 0 ? _info.AudioRate / 1000 + " kHz" : "n/d")} • {AudioChText(_info)}",
                AudioOut = _engine.IsBitstreamActive() ? "Bitstream (pass-through)" : "PCM",
                AudioBitrateNow = "n/d",
                AudioBitrateAvg = "n/d",

                Renderer = renderer.ToString(),
                HdrMode = fileHdr ? (_hdr == HdrMode.Auto ? "HDR (auto)" : "SDR (tone-map)") : "SDR",
                Upscaling = false,
                RtxHdr = false
            };
            _infoOverlay.SetStats(s);

            static string CodecName(AVCodecID id)
            {
                return id switch
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
            }
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
            _paused = !_paused; if (_paused) _engine.Pause(); else _engine.Play();
            _hud.ShowOnce(1200);
        }

        private void ToggleFullscreen()
        {
            if (FormBorderStyle != FormBorderStyle.None)
            {
                _prevBorder = FormBorderStyle; _prevState = WindowState;
                FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Maximized; _hud.AutoHide = true;
            }
            else
            {
                FormBorderStyle = _prevBorder; WindowState = _prevState; _hud.AutoHide = false;
            }
            _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);
            _hud.BringToFront();
            _hud.ShowOnce(1500);
            UpdateDebugPanel();
        }

        private void UpdateTime(double cur) => _lblTime.Text = _duration > 0 ? $"{Fmt(cur)} / {Fmt(_duration)}" : Fmt(cur);
        private static string Fmt(double s) { if (double.IsNaN(s) || s < 0) s = 0; var ts = TimeSpan.FromSeconds(s); return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss"); }

        private void StopAll()
        {
            try { _engine?.Stop(); } catch { }
            try { _engine?.Dispose(); } catch { }
            _engine = null;
            _duration = 0; _paused = false; _lblTime.Text = "00:00 / 00:00"; _lblStatus.Text = "Pronto";
            _thumbCts?.Cancel(); _thumbCts = null; try { _thumb.Close(); } catch { }
            _audioOnlyBanner.Visible = false; _currentPath = null;
            _splash.Visible = true;  // torna splash
            _hud.SetPreview(null, 0);
            UpdateDebugPanel();
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

        private void UpdateDebugPanel()
        {
            if (_engine == null) { _debugOverlay.SetLines(new[] { "NO ENGINE" }); return; }

            var lines = new List<string>();
            lines.Add($"Wnd: 0x{_videoHost.Handle.ToInt64():X} rect=({_videoHost.ClientRectangle.X},{_videoHost.ClientRectangle.Y},{_videoHost.ClientRectangle.Width}x{_videoHost.ClientRectangle.Height})");
            lines.Add($"HDR={_hdr}  FileHDR={_info?.IsHdr}  3D={_stereo}  Paused={_paused}");

            var (w, h, sub) = _engine.GetNegotiatedVideoFormat();
            var (mtDump, mtAt) = _engine.GetLastVideoMTDump();
            var (snapB, snapAt) = _engine.GetLastSnapshotInfo();

            lines.Add($"Negotiated: {w}x{h} • {sub}");
            lines.Add($"Last snapshot: {snapB} bytes @ {(snapAt == DateTime.MinValue ? "-" : snapAt.ToString("HH:mm:ss.fff"))}");
            if (!string.IsNullOrEmpty(mtDump))
            {
                lines.Add($"Last VMT dump @ {mtAt:HH:mm:ss.fff}");
                foreach (var l in mtDump.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                    lines.Add("  " + l.Trim());
            }

            var tail = Dbg.Snapshot();
            var tailCount = Math.Min(10, tail.Length);
            if (tailCount > 0)
            {
                lines.Add("----- LOG TAIL -----");
                for (int i = tail.Length - tailCount; i < tail.Length; i++)
                    lines.Add(tail[i]);
            }

            _debugOverlay.SetLines(lines);
        }
    }

    // ======= Debug overlay =======
    internal sealed class DebugOverlay : Control
    {
        private string[] _lines = Array.Empty<string>();

        public DebugOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public void SetLines(IEnumerable<string> lines)
        {
            _lines = lines?.ToArray() ?? Array.Empty<string>();
            Invalidate();
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } // WS_EX_TRANSPARENT
        }

        protected override void OnPaintBackground(PaintEventArgs e) { /* trasparente */ }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;

            using (var pen = new Pen(Color.FromArgb(180, 255, 255, 0), 2))
            {
                g.DrawRectangle(pen, new Rectangle(1, 1, Width - 2, Height - 2));
                g.DrawLine(pen, Width / 2, 0, Width / 2, Height);
                g.DrawLine(pen, 0, Height / 2, Width, Height / 2);
            }

            var text = string.Join("\r\n", _lines);
            var boxW = Math.Min(Width - 20, 940);
            var font = new Font("Consolas", 9f, FontStyle.Regular);
            var sz = g.MeasureString(text, font, boxW);
            var rect = new RectangleF(10, 10, boxW, sz.Height + 4);

            using (var bg = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                g.FillRectangle(bg, rect);
            using (var br = new SolidBrush(Color.Lime))
                g.DrawString(text, font, br, new RectangleF(12, 12, boxW - 4, sz.Height + 2));
        }
    }

    // ======= Splash overlay (pulsante centrale per aprire) =======
    internal sealed class SplashOverlay : Control
    {
        public event Action? OpenRequested;
        private Image? _img;

        public SplashOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;

            var p = Path.Combine(AppContext.BaseDirectory, "Assets", "splash.png");
            if (File.Exists(p))
                _img = Image.FromFile(p);

            Cursor = Cursors.Hand;
            Click += (_, __) => OpenRequested?.Invoke();
        }

        protected override CreateParams CreateParams
        { get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            int boxW = Math.Min(480, (int)(Width * 0.6));
            int boxH = Math.Min(340, (int)(Height * 0.5));
            var rect = new Rectangle((Width - boxW) / 2, (Height - boxH) / 2, boxW, boxH);

            using var bg = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
            using var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 2);
            g.FillRoundedRectangle(bg, rect, 18);
            g.DrawRoundedRectangle(pen, rect, 18);

            if (_img != null)
            {
                int ih = (int)(boxH * 0.6);
                var rcImg = new Rectangle(rect.X + (rect.Width - ih) / 2, rect.Y + 20, ih, ih);
                g.DrawImage(_img, rcImg);
            }
            else
            {
                // Fallback: icona "+"
                int s = Math.Min(rect.Width, rect.Height) / 3;
                int cx = rect.X + rect.Width / 2;
                int cy = rect.Y + rect.Height / 2 - 20;
                using var w = new Pen(Color.White, 6);
                g.DrawLine(w, cx - s / 2, cy, cx + s / 2, cy);
                g.DrawLine(w, cx, cy - s / 2, cx, cy + s / 2);
            }

            string t1 = "Clicca o premi O per aprire un file";
            using var f1 = new Font("Segoe UI", 12f, FontStyle.Bold);
            var sz1 = g.MeasureString(t1, f1);
            using var br1 = new SolidBrush(Color.White);
            g.DrawString(t1, f1, br1, rect.X + (rect.Width - sz1.Width) / 2, rect.Bottom - 48);
        }
    }

    // Helpers per disegno arrotondato
    internal static class GfxEx
    {
        public static void FillRoundedRectangle(this Graphics g, Brush brush, Rectangle bounds, int radius)
        { using var path = RoundedRect(bounds, radius); g.FillPath(brush, path); }
        public static void DrawRoundedRectangle(this Graphics g, Pen pen, Rectangle bounds, int radius)
        { using var path = RoundedRect(bounds, radius); g.DrawPath(pen, path); }
        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // ======= Info overlay (orizzontale) =======
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

        public void SetStats(Stats s) { _s = s; Invalidate(); }

        public InfoOverlay()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor,
                true);

            BackColor = Color.Transparent;
        }

        protected override CreateParams CreateParams
        { get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var rect = new Rectangle(12, 6, Width - 24, Height - 12);
            using var bg = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
            using var pen = new Pen(Color.FromArgb(220, 255, 255, 255), 1);
            g.FillRoundedRectangle(bg, rect, 10);
            g.DrawRoundedRectangle(pen, rect, 10);

            using var fTitle = new Font("Segoe UI Semibold", 12f);
            using var fKey = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            using var fVal = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            using var br = new SolidBrush(Color.White);
            using var dim = new SolidBrush(Color.Gainsboro);

            int x = rect.X + 12; int y = rect.Y + 8;

            g.DrawString(_s.Title ?? "—", fTitle, br, x, y);
            y += 28;

            // layout orizzontale: 3 colonne
            int colW = rect.Width / 3 - 10;

            void Col(int colX, (string k, string v)[] items)
            {
                int yy = y;
                foreach (var (k, v) in items)
                {
                    g.DrawString(k, fKey, dim, colX, yy);
                    var keyW = (int)g.MeasureString(k, fKey).Width + 6;
                    var rcVal = new RectangleF(colX + keyW, yy, colW - keyW, 18);
                    g.DrawString(v ?? "—", fVal, br, rcVal);
                    yy += 18;
                }
            }

            Col(x, new[]
            {
                ("Video IN:", _s.VideoIn),
                ("Video OUT:", _s.VideoOut),
                ("Codec:", _s.VideoCodec),
                ("Colore:", $"{_s.VideoPrimaries} / {_s.VideoTransfer}"),
                ("Bitrate (att/med):", $"{_s.VideoBitrateNow} / {_s.VideoBitrateAvg}"),
            });

            Col(x + colW + 10, new[]
            {
                ("Audio IN:", _s.AudioIn),
                ("Audio OUT:", _s.AudioOut),
                ("Bitrate (att/med):", $"{_s.AudioBitrateNow} / {_s.AudioBitrateAvg}"),
                ("Renderer:", _s.Renderer),
            });

            Col(x + (colW + 10) * 2, new[]
            {
                ("HDR Mode:", _s.HdrMode),
                ("Upscaling:", _s.Upscaling ? "ON" : "OFF"),
                ("RTX HDR:", _s.RtxHdr ? "ON" : "OFF"),
            });
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

        private float _vol = 1.0f;
        private bool _overActiveZone, _drag;
        private bool _dragVol;
        private DateTime _lastMove = DateTime.UtcNow;
        private DateTime _lastPreviewAt = DateTime.MinValue;
        private readonly System.Windows.Forms.Timer _fade;
        private float _opacity = 1f;
        private Bitmap? _preview; private double _previewSec; private int _lastMouseX;
        private DateTime _forceShowUntil = DateTime.MinValue;

        public HudOverlay()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;

            _fade = new System.Windows.Forms.Timer { Interval = 180 };
            _fade.Tick += (_, __) =>
            {
                if (DateTime.UtcNow < _forceShowUntil)
                { if (_opacity != 1f) { _opacity = 1f; Invalidate(); } return; }

                if (!AutoHide) { if (_opacity != 1f) { _opacity = 1f; Invalidate(); } return; }
                if (_overActiveZone || _drag) { if (_opacity != 1f) { _opacity = 1f; Invalidate(); } return; }

                var idleMs = (DateTime.UtcNow - _lastMove).TotalMilliseconds;
                if (idleMs > 1200)
                {
                    float target = 0f;
                    float step = 0.08f;
                    if (_opacity > target) { _opacity = Math.Max(target, _opacity - step); Invalidate(); }
                }
            };
            _fade.Start();

            MouseMove += (_, e) =>
            {
                _lastMove = DateTime.UtcNow; _opacity = 1f; _lastMouseX = e.X; Invalidate();

                _overActiveZone = ActiveZone.Contains(e.Location);

                if (_dragVol)
                {
                    float v = (e.X - VolX) / (float)VolWidth; v = Math.Clamp(v, 0f, 1f);
                    _vol = v; VolumeChanged?.Invoke(v); Invalidate(); return;
                }

                if (TimelineRect.Contains(e.Location) && GetTime != null)
                {
                    var now = DateTime.UtcNow;
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

            MouseLeave += (_, __) =>
            {
                _overActiveZone = false; _drag = false; _dragVol = false;
                _lastMove = DateTime.UtcNow;
                if (_preview != null) SetPreview(null, _previewSec);
            };

            MouseDown += OnMouseDown;
            MouseUp += (_, __) => { _drag = false; _dragVol = false; };
        }

        private Rectangle ActiveZone => new Rectangle(0, Height - 120, Width, 120);

        public void ShowOnce(int milliseconds = 2000)
        {
            _forceShowUntil = DateTime.UtcNow.AddMilliseconds(Math.Max(250, milliseconds));
            _opacity = 1f;
            Invalidate();
        }

        public void PerformVolumeDelta(float delta, Action<float> apply)
        {
            _vol = Math.Clamp(_vol + delta, 0f, 1f);
            apply(_vol);
            Invalidate();
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } // WS_EX_TRANSPARENT
        }

        public void SetPreview(Bitmap? bmp, double seconds) { _preview?.Dispose(); _preview = bmp; _previewSec = seconds; Invalidate(); }
        public void SetExternalVolume(float v) { _vol = Math.Clamp(v, 0, 1); Invalidate(); }

        private Rectangle _btnOpen => new(16 + 36, Height - 44, 28, 28);
        private Rectangle _btnRemove => new(16, Height - 44, 28, 28);
        private Rectangle _btnFull => new(Width - 44, Height - 44, 28, 28);

        private int CenterY => Height - 44;
        private int BtnSize => 28;
        private int Gap => 36;

        private Rectangle _btnPlay
        {
            get { int cx = Width / 2 - BtnSize / 2; return new Rectangle(cx, CenterY, BtnSize, BtnSize); }
        }
        private Rectangle _btnBack10 => new(_btnPlay.X - Gap, CenterY, BtnSize, BtnSize);
        private Rectangle _btnFwd10 => new(_btnPlay.Right + Gap - BtnSize, CenterY, BtnSize, BtnSize);
        private Rectangle _btnPrevChap => new(_btnBack10.X - Gap, CenterY, BtnSize, BtnSize);
        private Rectangle _btnNextChap => new(_btnFwd10.Right + (Gap - BtnSize), CenterY, BtnSize, BtnSize);

        private int VolWidth => 180;
        private int VolX => _btnFull.X - 16 - VolWidth;
        private int VolY => Height - 30;

        private Rectangle TimelineRect => new(16, Height - 56, Width - 32, 6);

        private void OnMouseDown(object? sender, MouseEventArgs e)
        {
            if (_btnRemove.Contains(e.Location)) { StopClicked?.Invoke(); return; }
            if (_btnOpen.Contains(e.Location)) { OpenClicked?.Invoke(); return; }
            if (_btnPlay.Contains(e.Location)) { PlayPauseClicked?.Invoke(); return; }
            if (_btnBack10.Contains(e.Location)) { SkipBack10Clicked?.Invoke(); return; }
            if (_btnFwd10.Contains(e.Location)) { SkipForward10Clicked?.Invoke(); return; }
            if (_btnPrevChap.Contains(e.Location)) { PrevChapterClicked?.Invoke(); return; }
            if (_btnNextChap.Contains(e.Location)) { NextChapterClicked?.Invoke(); return; }
            if (_btnFull.Contains(e.Location)) { FullscreenClicked?.Invoke(); return; }

            var vtrack = new Rectangle(VolX, VolY - 6, VolWidth, 12);
            if (vtrack.Contains(e.Location))
            {
                _dragVol = true;
                float v = (e.X - VolX) / (float)VolWidth; v = Math.Clamp(v, 0f, 1f);
                _vol = v; VolumeChanged?.Invoke(v); Invalidate(); return;
            }

            if (TimelineRect.Contains(e.Location) && GetTime != null)
            {
                _drag = true; var (_, dur) = GetTime();
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
            var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            using var bg = new SolidBrush(Color.FromArgb((int)(170 * _opacity), 0, 0, 0));
            g.FillRectangle(bg, new Rectangle(0, Height - 96, Width, 96));

            var info = GetInfoLine?.Invoke() ?? "";
            using var fInfo = new Font("Segoe UI", 9f);
            using var brInfo = new SolidBrush(Color.FromArgb((int)(220 * _opacity), 230, 230, 230));
            g.DrawString(info, fInfo, brInfo, 16, Height - 88);

            if (GetTime != null)
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
                    g.DrawImage(_preview, new Rectangle(px, py, pw, ph));
                    string pt = Fmt(_previewSec); var ptsz = g.MeasureString(pt, fInfo);
                    using var bb = new SolidBrush(Color.FromArgb(220, 0, 0, 0));
                    using var wb = new SolidBrush(Color.White);
                    int boxW = Math.Max((int)(ptsz.Width + 10), pw);
                    g.FillRectangle(bb, px, py - ptsz.Height - 6, boxW, ptsz.Height + 6);
                    g.DrawString(pt, fInfo, wb, px + 5, py - ptsz.Height - 3);
                }
            }

            DrawBtn(g, _btnRemove, "×");
            DrawBtn(g, _btnOpen, "↥");
            DrawBtn(g, _btnPlay, "⏯");
            DrawBtn(g, _btnBack10, "⏪");
            DrawBtn(g, _btnFwd10, "⏩");
            DrawBtn(g, _btnPrevChap, "⏮");
            DrawBtn(g, _btnNextChap, "⏭");
            DrawBtn(g, _btnFull, "⛶");

            using var trk = new Pen(Color.FromArgb((int)(220 * _opacity), 180, 180, 180), 2);
            g.DrawLine(trk, VolX, VolY, VolX + VolWidth, VolY);
            int knob = VolX + (int)(_vol * VolWidth);
            using var kn = new SolidBrush(Color.FromArgb((int)(255 * _opacity), 255, 255, 255));
            g.FillEllipse(kn, knob - 6, VolY - 6, 12, 12);

            static string Fmt(double s) { if (double.IsNaN(s) || s < 0) s = 0; var ts = TimeSpan.FromSeconds(s); return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss"); }
            static void DrawBtn(Graphics gg, Rectangle r, string txt)
            {
                using var b = new SolidBrush(Color.FromArgb(110, 255, 255, 255));
                gg.FillEllipse(b, r);
                using var f = new Font("Segoe UI", 11f, FontStyle.Bold);
                var sz = gg.MeasureString(txt, f);
                gg.DrawString(txt, f, Brushes.Black, r.X + (r.Width - sz.Width) / 2f, r.Y + (r.Height - sz.Height) / 2f);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs pevent) { }
    }
}
