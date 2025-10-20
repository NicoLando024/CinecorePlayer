#nullable enable
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace CinecorePlayer2025
{
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

        static MediaProbe()
        {
            try { ffmpeg.avformat_network_init(); } catch { }
        }

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

        private int _lastW, _lastH;
        private AVPixelFormat _lastFmt;

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

            _lastW = 0; _lastH = 0; _lastFmt = (AVPixelFormat)(-1);
        }

        public Bitmap? Get(double seconds, int maxW = 360)
        {
            if (_fmt == null || _vindex < 0 || _dec == null) return null;
            var st = _fmt->streams[_vindex];

            // clamp in base a duration stream (più preciso di DurationSeconds del player)
            double tb = ffmpeg.av_q2d(st->time_base);
            double maxSeconds = Math.Max(0.0, st->duration > 0 ? st->duration * tb : seconds);
            if (maxSeconds > 0) seconds = Math.Max(0, Math.Min(seconds, Math.Max(0, maxSeconds - 0.05)));

            long ts = (long)(seconds / tb); if (ts < 0) ts = 0;

            // seek robusto
            int sk = ffmpeg.av_seek_frame(_fmt, _vindex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD | ffmpeg.AVSEEK_FLAG_ANY);
            if (sk < 0) sk = ffmpeg.av_seek_frame(_fmt, _vindex, ts, ffmpeg.AVSEEK_FLAG_ANY);
            ffmpeg.avcodec_flush_buffers(_dec);

            AVPacket* pkt = ffmpeg.av_packet_alloc(); AVFrame* frame = ffmpeg.av_frame_alloc(); Bitmap? bmp = null;
            try
            {
                int packetsRead = 0;
                while (ffmpeg.av_read_frame(_fmt, pkt) >= 0 && packetsRead < 200) // hard limit per evitare loop lunghi
                {
                    packetsRead++;
                    if (pkt->stream_index != _vindex) { ffmpeg.av_packet_unref(pkt); continue; }
                    if (ffmpeg.avcodec_send_packet(_dec, pkt) < 0) { ffmpeg.av_packet_unref(pkt); continue; }
                    ffmpeg.av_packet_unref(pkt);
                    while (ffmpeg.avcodec_receive_frame(_dec, frame) >= 0)
                    {
                        bmp = ToBitmap(frame, maxW);
                        ffmpeg.av_frame_unref(frame);
                        return bmp;
                    }
                }
            }
            finally { ffmpeg.av_frame_free(&frame); ffmpeg.av_packet_free(&pkt); }
            return bmp;
        }

        private Bitmap ToBitmap(AVFrame* src, int maxW)
        {
            int srcW = Math.Max(1, src->width);
            int srcH = Math.Max(1, src->height);
            int dstW = Math.Min(maxW, srcW);
            int dstH = (int)Math.Round(dstW * (srcH / (double)srcW));

            // ricrea _sws se cambia qualcosa
            var curFmt = (AVPixelFormat)src->format;
            if (_sws == null || _lastW != srcW || _lastH != srcH || _lastFmt != curFmt)
            {
                if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
                _sws = ffmpeg.sws_getContext(
                    srcW, srcH, curFmt,
                    dstW, dstH, AVPixelFormat.AV_PIX_FMT_BGRA,
                    ffmpeg.SWS_BICUBIC, null, null, null);
                _lastW = srcW; _lastH = srcH; _lastFmt = curFmt;
            }

            byte_ptrArray4 dst = new(); int_array4 dstLinesize = new();
            int bufSize = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_BGRA, dstW, dstH, 1);
            byte* buffer = (byte*)ffmpeg.av_malloc((ulong)bufSize);
            ffmpeg.av_image_fill_arrays(ref dst, ref dstLinesize, buffer, AVPixelFormat.AV_PIX_FMT_BGRA, dstW, dstH, 1);
            ffmpeg.sws_scale(_sws, src->data, src->linesize, 0, srcH, dst, dstLinesize);

            var bmp = new Bitmap(dstW, dstH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, dstW, dstH);
            var lockd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                int srcStride = Math.Abs(dstLinesize[0]);
                int dstStride = lockd.Stride;
                int bytesToCopy = Math.Min(srcStride, dstStride);
                for (int y = 0; y < dstH; y++)
                {
                    IntPtr srcLine = (IntPtr)(dst[0] + y * dstLinesize[0]);
                    IntPtr dstLine = lockd.Scan0 + y * dstStride;
                    unsafe { Buffer.MemoryCopy((void*)srcLine, (void*)dstLine, bytesToCopy, bytesToCopy); }
                }
            }
            finally
            {
                bmp.UnlockBits(lockd);
                ffmpeg.av_free(buffer);
            }
            return bmp;
        }

        public void Close()
        {
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_dec != null) { var d = _dec; ffmpeg.avcodec_free_context(&d); _dec = null; }
            if (_fmt != null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
            _vindex = -1;
            _lastW = _lastH = 0; _lastFmt = (AVPixelFormat)(-1);
        }

        public void Dispose() => Close();
    }
}
