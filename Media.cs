#nullable enable
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace CinecorePlayer2025
{
    // ======= Bootstrap FFmpeg: RootPath + avformat_network_init una volta =======
    internal static class FFmpegBootstrap
    {
        private static readonly object _lock = new();
        private static bool _initialized;

        public static void Ensure()
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;
                try
                {
                    var baseDir = AppContext.BaseDirectory;
                    var arch = Environment.Is64BitProcess ? "win-x64" : "win-x86";
                    var candidate1 = Path.Combine(baseDir, "ffmpeg", arch);
                    var candidate2 = Path.Combine(baseDir, "runtimes", arch, "native");

                    if (Directory.Exists(candidate1)) ffmpeg.RootPath = candidate1;
                    else if (Directory.Exists(candidate2)) ffmpeg.RootPath = candidate2;

                    string ver = ffmpeg.av_version_info() ?? "?";

                    _initialized = true;
                }
                catch (Exception)
                {
                }
            }
        }
    }

    // ======= Helper unsafe comune =======
    internal static unsafe class FF
    {
        public static int OpenInputUtf8(string path, AVFormatContext** pFmt)
            => ffmpeg.avformat_open_input(pFmt, path, null, null);
    }

    // ======= MediaProbe (FFmpeg) =======
    public static unsafe class MediaProbe
    {
        public static string? LastProbedPath { get; private set; }

        public sealed class Result
        {
            public double Duration;
            public bool HasVideo;
            public int Width, Height, VideoBits;
            public AVCodecID VideoCodec;
            public AVPixelFormat PixFmt;
            public AVColorPrimaries Primaries;
            public AVColorTransferCharacteristic Transfer;
            public double VideoFps;                // <-- FPS nominali

            public AVCodecID AudioCodec;
            public int AudioRate, AudioChannels, AudioBits;
            public string AudioLayoutText = "";
            public bool AudioLooksObjectBased;
            public bool IsHdr;
            public List<(string title, double start)> Chapters = new();
        }

        static MediaProbe() { FFmpegBootstrap.Ensure(); }

        public static Result Probe(string path)
        {
            LastProbedPath = path;
            AVFormatContext* fmt = null;

            int openRc = FF.OpenInputUtf8(path, &fmt);
            if (openRc != 0)
                throw new ApplicationException($"Impossibile aprire il file (rc={openRc}).");

            try
            {
                if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                    throw new ApplicationException("Stream info non trovate.");

                var r = new Result
                {
                    Duration = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0
                };

                for (int i = 0; i < fmt->nb_streams; i++)
                {
                    var st = fmt->streams[i];
                    var par = st->codecpar;

                    if (par->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        r.HasVideo = true;
                        r.Width = par->width;
                        r.Height = par->height;
                        r.VideoCodec = par->codec_id;
                        r.PixFmt = (AVPixelFormat)par->format;
                        r.VideoBits = GuessVideoBits(r.PixFmt, par->bits_per_raw_sample);
                        r.Primaries = par->color_primaries;
                        r.Transfer = par->color_trc;
                        r.VideoFps = GuessFps(st);
                    }
                    else if (par->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                    {
                        r.AudioCodec = par->codec_id;
                        r.AudioRate = par->sample_rate;

                        int ch = (int)par->ch_layout.nb_channels;
                        if (ch <= 0) ch = 2;
                        r.AudioChannels = ch;

                        r.AudioBits = GuessAudioBits(par->codec_id, par->format,
                                                     par->bits_per_coded_sample,
                                                     par->bits_per_raw_sample);

                        try
                        {
                            var buf = stackalloc sbyte[128];
                            ffmpeg.av_channel_layout_describe(&par->ch_layout, (byte*)buf, 128);
                            r.AudioLayoutText = Marshal.PtrToStringAnsi((IntPtr)buf) ?? "";
                        }
                        catch
                        {
                            r.AudioLayoutText = "";
                        }

                        if (par->codec_id == AVCodecID.AV_CODEC_ID_TRUEHD ||
                            par->codec_id == AVCodecID.AV_CODEC_ID_EAC3)
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

                r.IsHdr = IsHdrLike(r.Transfer, r.Primaries, r.VideoBits);

                return r;
            }
            finally
            {
                if (fmt != null)
                {
                    var l = fmt;
                    ffmpeg.avformat_close_input(&l);
                }
            }

            static int GuessVideoBits(AVPixelFormat fmt, int bprs)
            {
                if (bprs > 0) return bprs;
                var d = ffmpeg.av_pix_fmt_desc_get(fmt);
                return d != null ? d->comp[0].depth : 8;
            }

            static int GuessAudioBits(AVCodecID id, int parFmt, int coded, int raw)
            {
                if (raw > 0) return raw;
                if (coded > 0) return coded;

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

            static double GuessFps(AVStream* st)
            {
                AVRational r = st->avg_frame_rate;
                double fps = 0;

                if (r.num > 0 && r.den > 0)
                {
                    fps = r.num / (double)r.den;
                }
                else
                {
                    r = st->r_frame_rate;
                    if (r.num > 0 && r.den > 0)
                        fps = r.num / (double)r.den;
                    else
                        fps = st->time_base.den != 0
                            ? 1.0 / ffmpeg.av_q2d(st->time_base)
                            : 0;
                }

                if (fps < 0.01 || fps > 500) return 0;
                return fps;
            }
        }

        public static bool IsPassthroughCandidate(AVCodecID id) =>
            id == AVCodecID.AV_CODEC_ID_TRUEHD || id == AVCodecID.AV_CODEC_ID_EAC3 ||
            id == AVCodecID.AV_CODEC_ID_AC3 || id == AVCodecID.AV_CODEC_ID_DTS;
    }

    // ======= Thumbnailer (FFmpeg) — thread-safe e idempotente =======
    internal sealed unsafe class Thumbnailer : IDisposable
    {
        private readonly object _lock = new();
        private AVFormatContext* _fmt;
        private int _vindex = -1;
        private AVCodecContext* _dec;
        private SwsContext* _sws;

        private int _lastSrcW, _lastSrcH;
        private AVPixelFormat _lastSrcFmt;
        private int _lastOutW, _lastOutH;
        private string? _srcPath;
        private bool _opened;
        private bool _disposed;

        public string? SourcePath => _srcPath;
        public event Action<string>? SourceOpened;

        public void Open(string path)
        {
            lock (_lock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Thumbnailer));
                FFmpegBootstrap.Ensure();

                if (_opened && string.Equals(_srcPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Close_NoLock();

                AVFormatContext* f = null;
                int rcOpen = FF.OpenInputUtf8(path, &f);
                if (rcOpen != 0) throw new ApplicationException("Thumb open failed (rc=" + rcOpen + ")");

                if (ffmpeg.avformat_find_stream_info(f, null) < 0)
                {
                    ffmpeg.avformat_close_input(&f);
                    throw new ApplicationException("Thumb si failed");
                }

                for (int i = 0; i < f->nb_streams; i++)
                    if (f->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        _vindex = i;
                        break;
                    }

                if (_vindex < 0)
                {
                    ffmpeg.avformat_close_input(&f);
                    throw new ApplicationException("No video");
                }

                var par = f->streams[_vindex]->codecpar;
                AVCodec* codec = ffmpeg.avcodec_find_decoder(par->codec_id);
                if (codec == null)
                {
                    ffmpeg.avformat_close_input(&f);
                    throw new ApplicationException("No decoder");
                }

                AVCodecContext* dec = ffmpeg.avcodec_alloc_context3(codec);
                if (ffmpeg.avcodec_parameters_to_context(dec, par) < 0)
                {
                    ffmpeg.avformat_close_input(&f);
                    throw new ApplicationException("Ctx copy fail");
                }

                if (ffmpeg.avcodec_open2(dec, codec, null) < 0)
                {
                    ffmpeg.avformat_close_input(&f);
                    ffmpeg.avcodec_free_context(&dec);
                    throw new ApplicationException("Open dec fail");
                }

                _fmt = f;
                _dec = dec;
                _srcPath = path;
                _opened = true;
                SourceOpened?.Invoke(path);
                _lastSrcW = _lastSrcH = 0;
                _lastSrcFmt = (AVPixelFormat)(-1);
                _lastOutW = _lastOutH = 0;
            }
        }

        public Bitmap? Get(double seconds, int maxW = 360)
        {
            lock (_lock)
            {
                if (_disposed || !_opened || _fmt == null || _dec == null || _vindex < 0)
                    return null;

                try
                {
                    var st = _fmt->streams[_vindex];
                    double tb = Math.Max(ffmpeg.av_q2d(st->time_base), 1e-12);

                    // clamp entro durata video (per sicurezza togliamo un pelo al fondo)
                    double maxSeconds = Math.Max(0.0, st->duration > 0 ? st->duration * tb : seconds);
                    if (maxSeconds > 0)
                        seconds = Math.Max(0, Math.Min(seconds, Math.Max(0, maxSeconds - 0.05)));

                    long ts = (long)(seconds / tb);
                    if (ts < 0) ts = 0;

                    // SEEK sullo stream video, all'indietro verso il keyframe precedente
                    int sk = ffmpeg.avformat_seek_file(_fmt, _vindex, long.MinValue, ts, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                    if (sk < 0)
                        sk = ffmpeg.av_seek_frame(_fmt, _vindex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);

                    ffmpeg.avcodec_flush_buffers(_dec);

                    AVPacket* pkt = ffmpeg.av_packet_alloc();
                    AVFrame* frame = ffmpeg.av_frame_alloc();
                    AVFrame* bestFrame = ffmpeg.av_frame_alloc();

                    try
                    {
                        const int MAX_PACKETS = 2000; // limite di sicurezza
                        const int MAX_FRAMES = 256;   // limita il lavoro dopo il seek

                        bool hasBest = false;
                        double bestDelta = double.MaxValue;
                        int packetsRead = 0;
                        int framesRead = 0;

                        while (ffmpeg.av_read_frame(_fmt, pkt) >= 0 && packetsRead < MAX_PACKETS)
                        {
                            packetsRead++;
                            if (pkt->stream_index != _vindex)
                            {
                                ffmpeg.av_packet_unref(pkt);
                                continue;
                            }

                            if (ffmpeg.avcodec_send_packet(_dec, pkt) < 0)
                            {
                                ffmpeg.av_packet_unref(pkt);
                                continue;
                            }

                            ffmpeg.av_packet_unref(pkt);

                            while (ffmpeg.avcodec_receive_frame(_dec, frame) >= 0)
                            {
                                framesRead++;
                                if (frame->width <= 0 || frame->height <= 0 || frame->format < 0 || frame->data[0] == null)
                                {
                                    ffmpeg.av_frame_unref(frame);
                                    continue;
                                }

                                long tsFrame = frame->best_effort_timestamp;
                                if (tsFrame == ffmpeg.AV_NOPTS_VALUE)
                                    tsFrame = frame->pts;
                                if (tsFrame == ffmpeg.AV_NOPTS_VALUE)
                                {
                                    ffmpeg.av_frame_unref(frame);
                                    continue;
                                }

                                double frameSec = tsFrame * tb;
                                double delta = Math.Abs(frameSec - seconds);

                                if (delta < bestDelta)
                                {
                                    bestDelta = delta;
                                    hasBest = true;
                                    ffmpeg.av_frame_unref(bestFrame);
                                    if (ffmpeg.av_frame_ref(bestFrame, frame) < 0)
                                    {
                                        hasBest = false;
                                    }
                                }

                                // Se siamo oltre il target e abbiamo già un frame a meno di 300ms, stop.
                                if (frameSec >= seconds && bestDelta < 0.3)
                                {
                                    ffmpeg.av_frame_unref(frame);
                                    goto DoneDecoding;
                                }

                                if (framesRead >= MAX_FRAMES)
                                {
                                    ffmpeg.av_frame_unref(frame);
                                    goto DoneDecoding;
                                }

                                ffmpeg.av_frame_unref(frame);
                            }
                        }

                    DoneDecoding:
                        if (!hasBest)
                            return null;

                        var bmp = ToBitmap(bestFrame, maxW);
                        return bmp;
                    }
                    finally
                    {
                        ffmpeg.av_frame_free(&bestFrame);
                        ffmpeg.av_frame_free(&frame);
                        ffmpeg.av_packet_free(&pkt);
                    }
                }
                catch
                {
                    return null;
                }
            }
        }

        private Bitmap ToBitmap(AVFrame* src, int maxW)
        {
            int srcW = Math.Max(1, src->width);
            int srcH = Math.Max(1, src->height);
            int dstW = Math.Min(Math.Max(1, maxW), srcW);
            int dstH = (int)Math.Round((double)srcH * dstW / srcW);

            var curFmt = (AVPixelFormat)src->format;

            // Re-init SWS se cambia sorgente OPPURE la dimensione di uscita
            if (_sws == null ||
                _lastSrcW != srcW || _lastSrcH != srcH || _lastSrcFmt != curFmt ||
                _lastOutW != dstW || _lastOutH != dstH)
            {
                if (_sws != null)
                {
                    ffmpeg.sws_freeContext(_sws);
                    _sws = null;
                }

                _sws = ffmpeg.sws_getContext(
                    srcW, srcH, curFmt,
                    dstW, dstH, AVPixelFormat.AV_PIX_FMT_BGRA,
                    ffmpeg.SWS_BILINEAR, null, null, null);

                _lastSrcW = srcW; _lastSrcH = srcH; _lastSrcFmt = curFmt;
                _lastOutW = dstW; _lastOutH = dstH;
            }

            // Conversione diretta nel buffer del Bitmap
            var bmp = new Bitmap(dstW, dstH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, dstW, dstH);
            var lockd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);

            try
            {
                byte_ptrArray4 dstPlanes = new();
                int_array4 dstLines = new();

                int stride = lockd.Stride;
                byte* basePtr = (byte*)lockd.Scan0;
                if (stride < 0)
                {
                    basePtr = (byte*)lockd.Scan0 + (long)(-stride) * (dstH - 1);
                    stride = -stride;
                }

                dstPlanes[0] = basePtr;
                dstLines[0] = stride;
                dstPlanes[1] = null; dstPlanes[2] = null; dstPlanes[3] = null;
                dstLines[1] = 0; dstLines[2] = 0; dstLines[3] = 0;

                ffmpeg.sws_scale(_sws, src->data, src->linesize, 0, srcH, dstPlanes, dstLines);
            }
            finally
            {
                bmp.UnlockBits(lockd);
            }

            return bmp;
        }

        public void Close()
        {
            lock (_lock) Close_NoLock();
        }

        private void Close_NoLock()
        {
            if (_sws != null)
            {
                ffmpeg.sws_freeContext(_sws);
                _sws = null;
            }
            if (_dec != null)
            {
                var d = _dec;
                ffmpeg.avcodec_free_context(&d);
                _dec = null;
            }
            if (_fmt != null)
            {
                var f = _fmt;
                ffmpeg.avformat_close_input(&f);
                _fmt = null;
            }

            _vindex = -1;
            _opened = false;
            _srcPath = null;
            _lastSrcW = _lastSrcH = 0;
            _lastSrcFmt = (AVPixelFormat)(-1);
            _lastOutW = _lastOutH = 0;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                Close_NoLock();
                _disposed = true;
            }
        }
    }

    // ======= PacketRateSampler (FFmpeg) — misura kbps reali in finestra breve =======
    internal sealed unsafe class PacketRateSampler : IDisposable
    {
        private readonly object _lock = new();
        private AVFormatContext* _fmt = null;
        private int _aIdx = -1, _vIdx = -1;
        private bool _opened;

        public bool Open(string path)
        {
            Close();
            FFmpegBootstrap.Ensure();

            AVFormatContext* f = null;
            int rc = FF.OpenInputUtf8(path, &f);
            if (rc != 0) return false;
            if (ffmpeg.avformat_find_stream_info(f, null) < 0)
            {
                ffmpeg.avformat_close_input(&f);
                return false;
            }

            int ai = -1, vi = -1;
            for (int i = 0; i < (int)f->nb_streams; i++)
            {
                var st = f->streams[i];
                if (st->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && ai < 0) ai = i;
                if (st->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && vi < 0) vi = i;
            }

            lock (_lock)
            {
                _fmt = f; _aIdx = ai; _vIdx = vi; _opened = true;
            }

            return true;
        }

        /// <summary>
        /// Campiona byte audio/video in una finestra ~windowSec attorno a tSec.
        /// Ritorna (audioKbps, videoKbps). Zero se non calcolabile.
        /// </summary>
        public (int aKbps, int vKbps) Sample(double tSec, double windowSec = 0.8)
        {
            lock (_lock)
            {
                if (!_opened || _fmt == null) return (0, 0);

                long ts = (long)(tSec * ffmpeg.AV_TIME_BASE);
                // seek "globale" più robusto
                int skl = ffmpeg.av_seek_frame(_fmt, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                if (skl < 0)
                    skl = ffmpeg.avformat_seek_file(_fmt, -1, long.MinValue, ts, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);

                long aBytes = 0, vBytes = 0;

                // Durata per-stream: evita che l'audio resti 0 quando la finestra si chiude "sui video"
                double t0A = -1, t1A = -1, t0V = -1, t1V = -1;

                AVPacket* pkt = ffmpeg.av_packet_alloc();
                try
                {
                    int guardPkts = 0;
                    while (ffmpeg.av_read_frame(_fmt, pkt) >= 0)
                    {
                        guardPkts++;
                        int si = pkt->stream_index;
                        var st = _fmt->streams[si];

                        double tb = ffmpeg.av_q2d(st->time_base);
                        double pts = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts * tb
                                   : (pkt->dts != ffmpeg.AV_NOPTS_VALUE ? pkt->dts * tb : -1);
                        double pdur = pkt->duration > 0 ? pkt->duration * tb : 0;

                        if (si == _aIdx)
                        {
                            aBytes += pkt->size;
                            if (pts >= 0)
                            {
                                if (t0A < 0) t0A = pts;
                                t1A = Math.Max(t1A, pts + Math.Max(pdur, 0));
                            }
                        }
                        else if (si == _vIdx)
                        {
                            vBytes += pkt->size;
                            if (pts >= 0)
                            {
                                if (t0V < 0) t0V = pts;
                                t1V = Math.Max(t1V, pts + Math.Max(pdur, 0));
                            }
                        }

                        ffmpeg.av_packet_unref(pkt);

                        // Condizione di stop: quando entrambe le durate per-stream hanno coperto ~windowSec
                        double aDur = (t0A >= 0 && t1A > t0A) ? (t1A - t0A) : 0;
                        double vDur = (t0V >= 0 && t1V > t0V) ? (t1V - t0V) : 0;
                        if ((aDur >= windowSec * 0.9 && vDur >= windowSec * 0.9) || guardPkts > 8000)
                            break;
                    }
                }
                finally
                {
                    ffmpeg.av_packet_free(&pkt);
                }

                // Se una delle due durate non è emersa, usa comunque la finestra nominale
                double aW = (t0A >= 0 && t1A > t0A) ? (t1A - t0A) : windowSec;
                double vW = (t0V >= 0 && t1V > t0V) ? (t1V - t0V) : windowSec;

                int aK = aBytes > 0 ? (int)Math.Round((aBytes * 8.0 / 1000.0) / Math.Max(aW, 1e-3)) : 0;
                int vK = vBytes > 0 ? (int)Math.Round((vBytes * 8.0 / 1000.0) / Math.Max(vW, 1e-3)) : 0;

                return (aK, vK);
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                if (_fmt != null)
                {
                    var f = _fmt;
                    ffmpeg.avformat_close_input(&f);
                }
                _fmt = null; _aIdx = _vIdx = -1; _opened = false;
            }
        }

        public void Dispose() => Close();
    }
}
