#nullable enable
using DirectShowLib;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Text.Json;

namespace CinecorePlayer2025
{
    // ======= DirectShow unified engine – API =======
    public interface IPlaybackEngine : IDisposable
    {
        void Open(string mediaPath, bool hasVideo);
        void Play(); void Pause(); void Stop();
        double DurationSeconds { get; }
        double PositionSeconds { get; set; }
        void SetVolume(float volume);
        void UpdateVideoWindow(IntPtr ownerHwnd, Rectangle ownerClient);
        Rectangle GetLastDestRectAsClient(Rectangle ownerClient);
        void SetStereo3D(Stereo3DMode mode);
        void SetUpscaling(bool enable);
        void BindUpdateCallback(Action cb);
        bool IsBitstreamActive();
        bool HasDisplayControl();

        (string text, DateTime when) GetLastVideoMTDump();
        (int width, int height, string subtype) GetNegotiatedVideoFormat();
        (int bytes, DateTime when) GetLastSnapshotInfo();

        event Action<double>? OnProgressSeconds;
        event Action<string>? OnStatus;
        event Action<bool>? OnBitstreamChanged;
        List<DsStreamItem> EnumerateStreams();

        bool EnableByGlobalIndex(int globalIndex);
        bool DisableSubtitlesIfPossible();
        bool TrySnapshot(out int byteCount);

        // ===== anteprima overlay (thumbnail FFmpeg) =====
        Bitmap? GetPreviewFrame(double seconds, int maxW = 360);

        // ===== madVR hotkey bridge =====
        void SetMadVrChroma(MadVrCategoryPreset preset);
        void SetMadVrImageUpscale(MadVrCategoryPreset preset);
        void SetMadVrImageDownscale(MadVrCategoryPreset preset);
        void SetMadVrRefinement(MadVrCategoryPreset preset);
        void SetMadVrFps(MadVrFpsChoice choice);
        void SetMadVrHdrMode(MadVrHdrMode mode);
    }

    // ======= Store JSON per punti di ripresa =======
    internal static class PlaybackResumeStore
    {
        private sealed class Envelope
        {
            public Dictionary<string, Entry> Items { get; set; } = new();
        }

        public sealed class Entry
        {
            public string MediaPath { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public double PositionSeconds { get; set; }
            public int PositionMinutes { get; set; }
            public double DurationSeconds { get; set; }
            public DateTime SavedAt { get; set; }
        }

        private static string ResumeFilePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CinecorePlayer2025",
                "resume.json");

        private const double MinSecondsForResume = 30.0; 
        private const int MaxEntries = 30;                

        private static Envelope LoadEnvelope()
        {
            try
            {
                if (File.Exists(ResumeFilePath))
                {
                    var json = File.ReadAllText(ResumeFilePath);
                    return JsonSerializer.Deserialize<Envelope>(json) ?? new Envelope();
                }
            }
            catch { }
            return new Envelope();
        }

        private static void SaveEnvelope(Envelope env)
        {
            try
            {
                var dir = Path.GetDirectoryName(ResumeFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(env, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ResumeFilePath, json);
            }
            catch { /* non facciamo crashare il player per problemi IO */ }
        }
        private static void TrimToMaxEntries(Envelope env)
        {
            try
            {
                while (env.Items.Count > MaxEntries)
                {
                    // trova l'entry più vecchia per SavedAt
                    var oldest = env.Items
                        .OrderBy(kvp => kvp.Value.SavedAt)
                        .First();

                    Dbg.Log($"Resume: removed oldest entry '{oldest.Value.DisplayName}' ({oldest.Value.SavedAt}).");
                    env.Items.Remove(oldest.Key);
                }
            }
            catch
            {
                // best-effort, non facciamo crashare nulla se qui fallisce
            }
        }

        /// <summary>
        /// Se la posizione è sensata (non all'inizio e non a fine file) salva;
        /// altrimenti rimuove l'eventuale entry.
        /// </summary>
        public static void SaveOrClear(string mediaPath, double positionSeconds, double durationSeconds)
        {
            if (string.IsNullOrWhiteSpace(mediaPath))
                return;

            var env = LoadEnvelope();

            var remaining = durationSeconds - positionSeconds;

            Dbg.Log($"Resume: SaveOrClear mediaPath='{mediaPath}', pos={positionSeconds:0.0}s, dur={durationSeconds:0.0}s, remaining={remaining:0.0}s");

            // Salva solo se siamo oltre una soglia minima dall'inizio (es. 30s)
            bool shouldClear =
                durationSeconds <= 0 ||
                positionSeconds <= 0 ||
                positionSeconds < MinSecondsForResume;

            if (shouldClear)
            {
                if (env.Items.Remove(mediaPath))
                    Dbg.Log($"Resume: cleared entry for '{mediaPath}'");
                SaveEnvelope(env);
                return;
            }

            var entry = new Entry
            {
                MediaPath = mediaPath,
                DisplayName = Path.GetFileName(mediaPath),
                PositionSeconds = positionSeconds,
                PositionMinutes = (int)Math.Round(positionSeconds / 60.0),
                DurationSeconds = durationSeconds,
                SavedAt = DateTime.Now
            };

            env.Items[mediaPath] = entry;

            TrimToMaxEntries(env);

            Dbg.Log($"Resume: saved '{entry.DisplayName}' at {entry.PositionSeconds:0.0}s ({entry.PositionMinutes}m).");
            SaveEnvelope(env);
        }

        public static Entry? Load(string mediaPath)
        {
            if (string.IsNullOrWhiteSpace(mediaPath))
                return null;

            var env = LoadEnvelope();
            env.Items.TryGetValue(mediaPath, out var entry);
            return entry;
        }

        public static IReadOnlyCollection<Entry> LoadAll()
        {
            var env = LoadEnvelope();
            return env.Items.Values.ToList();
        }
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

    // ======= DirectShow unified engine =======
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

        private IMFVideoDisplayControl? _mfDisplay; // EVR/MPCVR (windowless)
        private IVideoWindow? _videoWindow;         // MPCVR/madVR (windowed)
        private MFRect _lastDest;

        private System.Windows.Forms.Timer? _timer;
        private bool _hasVideo;
        private Stereo3DMode _stereo = Stereo3DMode.None;

        private volatile bool _bitstreamActive;
        private bool _allowUpscaling = true;
        private bool IsWindowedRenderer => _videoWindow != null && _mfDisplay == null;
        private string _audioRendererName = "?";

        // === Hotkey bridge: finestra owner da mettere in foreground e drenare messaggi ===
        private IntPtr _lastOwnerHwnd = IntPtr.Zero;

        // Debug state
        private string _lastVmtDump = "";
        private DateTime _lastVmtAt = DateTime.MinValue;
        private int _lastSnapshotBytes;
        private DateTime _lastSnapshotAt;

        // === Thumbnailer per anteprime overlay ===
        private Thumbnailer? _thumb;
        private string? _currentMediaPath;

        // === cache informativa robusta (wrapper immutabile per uso con 'volatile') ===
        private sealed class CachedFmt
        {
            public readonly int W;
            public readonly int H;
            public readonly string Sub;
            public CachedFmt(int w, int h, string sub) { W = w; H = h; Sub = sub; }
        }
        private volatile CachedFmt _cachedFmt = new CachedFmt(0, 0, "?");
        private CancellationTokenSource? _mtPollCts;

        // === DEBUG / INTROSPECTION HOOKS (per InfoOverlay / LAV Audio) ===
        public DirectShowLib.IFilterGraph2? FilterGraph
        {
            get { try { return _graph as DirectShowLib.IFilterGraph2; } catch { return null; } }
        }
        public DirectShowLib.IFilterGraph2? GetGraph() => _graph as DirectShowLib.IFilterGraph2;
        public DirectShowLib.IBaseFilter? LavAudioFilter => _lavAudio;

        public event Action<double>? OnProgressSeconds;
        public event Action<string>? OnStatus;
        // NOTIFICA cambi PCM/Bitstream
        public event Action<bool>? OnBitstreamChanged;

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

        public double DurationSeconds
        {
            get
            {
                try
                {
                    var s = _seek;
                    if (s == null) return 0;
                    s.GetDuration(out long d);
                    return d / 10_000_000.0;
                }
                catch (InvalidComObjectException)
                {
                    _seek = null;
                    return 0;
                }
                catch (COMException)
                {
                    return 0;
                }
            }
        }

        public double PositionSeconds
        {
            get
            {
                try
                {
                    var s = _seek;
                    if (s == null) return 0;
                    s.GetCurrentPosition(out long p);
                    return p / 10_000_000.0;
                }
                catch (InvalidComObjectException)
                {
                    _seek = null;
                    return 0;
                }
                catch (COMException)
                {
                    return 0;
                }
            }
            set
            {
                try
                {
                    var s = _seek;
                    if (s == null) return;
                    long t = (long)(value * 10_000_000.0);
                    s.SetPositions(t, AMSeekingSeekingFlags.AbsolutePositioning, t, AMSeekingSeekingFlags.NoPositioning);
                }
                catch (InvalidComObjectException)
                {
                    _seek = null;
                }
                catch (COMException)
                {

                }
            }
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

            bool usingLavSource = false;

            // Prova LAV Splitter Source (source filter)
            try
            {
                _lavSource = CreateFilterByName("LAV Splitter Source");
                if (_lavSource != null)
                {
                    DsError.ThrowExceptionForHR(_graph!.AddFilter(_lavSource, "LAV Splitter Source"));
                    var ifs = (IFileSourceFilter)_lavSource;
                    DsError.ThrowExceptionForHR(ifs.Load(mediaPath, null));
                    usingLavSource = true;
                }
            }
            catch
            {
                // pulizia best-effort
                try { if (_lavSource != null) _graph!.RemoveFilter(_lavSource); } catch { }
                try { if (_lavSource != null) Marshal.ReleaseComObject(_lavSource); } catch { }
                _lavSource = null;
            }

            // Fallback: File Source (URL) → LAV Splitter (NON-source)
            if (!usingLavSource)
            {
                var urlSrc = CreateFilterByName("File Source (URL)")
                             ?? throw new ApplicationException("File Source (URL) non trovato");
                DsError.ThrowExceptionForHR(_graph!.AddFilter(urlSrc, "File Source (URL)"));
                var ifs2 = (IFileSourceFilter)urlSrc;
                DsError.ThrowExceptionForHR(ifs2.Load(mediaPath, null));

                _lavSource = CreateFilterByName("LAV Splitter")
                             ?? throw new ApplicationException("LAV Splitter non trovato");
                DsError.ThrowExceptionForHR(_graph.AddFilter(_lavSource, "LAV Splitter"));

                var outPin = FindPin(urlSrc, PinDirection.Output, null)
                             ?? throw new ApplicationException("Pin OUT URL mancante");
                var inPin = FindPin(_lavSource, PinDirection.Input, null)
                             ?? throw new ApplicationException("Pin IN LAV mancante");
                DsError.ThrowExceptionForHR(_graph.Connect(outPin, inPin));
            }

            // ↓↓↓ ATTENZIONE: NON ri-aggiungere _lavSource e NON rifare Load qui
            _lavAudio = CreateFilterByName("LAV Audio Decoder")
                        ?? throw new ApplicationException("LAV Audio Decoder non trovato");
            DsError.ThrowExceptionForHR(_graph.AddFilter(_lavAudio, "LAV Audio"));

            // === Thumbnailer per anteprima (indipendente dal renderer) ===
            _currentMediaPath = mediaPath;
            try
            {
                _thumb?.Dispose();
                _thumb = null;
                if (hasVideo)
                {
                    _thumb = new Thumbnailer();
                    _thumb.Open(mediaPath);
                    Dbg.Log("Thumbnailer aperto per anteprime.");
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("Thumbnailer.Open EX: " + ex.Message);
            }

            if (_hasVideo)
            {
                if (_lavVideo == null)
                    _lavVideo = CreateFilterByName("LAV Video Decoder")
                                ?? throw new ApplicationException("LAV Video Decoder non trovato");
                if (_videoRenderer == null)
                    _videoRenderer = CreateVideoRendererByChoice(_choice)
                                     ?? throw new ApplicationException("Renderer video non disponibile");

                DsError.ThrowExceptionForHR(_graph.AddFilter(_lavVideo, "LAV Video"));
                DsError.ThrowExceptionForHR(_graph.AddFilter(_videoRenderer, "Video Renderer"));

                AttachDisplayInterfaces(initial: true);
            }

            ConnectAudioPath();
            if (_hasVideo) ConnectVideoPath();

            StartTimer();
            _cachedFmt = new CachedFmt(0, 0, "?");
            TryDetectBitstream();
            try { OnBitstreamChanged?.Invoke(_bitstreamActive); } catch { }

            var audioMode = _bitstreamActive ? "Bitstream" : "PCM";
            var msg = $"Grafo pronto ({audioMode}{(_hasVideo ? ", video" : ", solo audio")}).";
            Dbg.Log(msg);
            SafeStatus(msg);
        }

        private void HdrTrace(string text) { if (_fileIsHdr) Dbg.Log("[HDR] " + text, Dbg.LogLevel.Info); }

        private void ConnectAudioPath()
        {
            try
            {
                if (_audioRenderer == null)
                {
                    _audioRenderer = PickAudioRenderer(_preferredAudioRendererName);
                    if (_audioRenderer != null)
                    {
                        DsError.ThrowExceptionForHR(_graph!.AddFilter(_audioRenderer, "Audio Renderer"));
                        _audioRendererName = FilterFriendlyName(_audioRenderer);
                    }
                }

                var srcA = FindPin(_lavSource!, PinDirection.Output, DirectShowLib.MediaType.Audio)
                           ?? FindPin(_lavSource!, PinDirection.Output, null);

                if (srcA == null) { Dbg.Error("Audio: nessun pin AUDIO dallo splitter."); return; }
                if (_audioRenderer == null) { Dbg.Error("Audio: nessun Audio Renderer disponibile."); return; }

                var rIn = FindPin(_audioRenderer, PinDirection.Input, null);
                var aIn = FindPin(_lavAudio!, PinDirection.Input, DirectShowLib.MediaType.Audio)
                          ?? FindPin(_lavAudio!, PinDirection.Input, null);
                var aOut = FindPin(_lavAudio!, PinDirection.Output, null);

                int hr;
                if (aIn != null && aOut != null && rIn != null)
                {
                    hr = _graph!.Connect(srcA, aIn); DsError.ThrowExceptionForHR(hr);
                    Dbg.Log("Audio: LAV Splitter → LAV Audio", Dbg.LogLevel.Verbose);

                    hr = _graph.Connect(aOut, rIn); DsError.ThrowExceptionForHR(hr);
                    Dbg.Log("Audio: LAV Audio → Renderer", Dbg.LogLevel.Verbose);
                }
                else if (rIn != null)
                {
                    hr = _graph!.Connect(srcA, rIn); DsError.ThrowExceptionForHR(hr);
                    Dbg.Log("Audio: LAV Splitter → Renderer (direct)");
                }
                else
                {
                    Dbg.Error("Audio: pin di input del renderer non trovato.");
                    return;
                }

                TryDetectBitstream();
                Dbg.Log("Audio path negoziato: " + (_bitstreamActive ? "Bitstream (IEC61937)" : "PCM/Decode"));
                _updateCb?.Invoke();
            }
            catch (Exception ex) { Dbg.Error("ConnectAudioPath EX: " + ex); }
        }

        private void ConnectVideoPath()
        {
            try
            {
                ConnectByType(_lavSource!, _lavVideo!, DirectShowLib.MediaType.Video);
                Dbg.Log("Video: LAV Splitter → LAV Video", Dbg.LogLevel.Verbose);

                var vOut = FindPin(_lavVideo!, PinDirection.Output, null) ?? throw new ApplicationException("Pin out LAV Video non trovato");
                var rIn = FindPin(_videoRenderer!, PinDirection.Input, null) ?? throw new ApplicationException("Pin in renderer non trovato");

                if (_choice == VideoRendererChoice.MPCVR)
                {
                    DsError.ThrowExceptionForHR(_graph!.Connect(vOut, rIn));
                    Dbg.Log("Video: LAV Video → Renderer (Connect) OK", Dbg.LogLevel.Verbose);
                }
                else
                {
                    int hr = _graph!.ConnectDirect(vOut, rIn, null);
                    if (hr == 0)
                    {
                        Dbg.Log("Video: LAV Video → Renderer (ConnectDirect) OK", Dbg.LogLevel.Verbose);
                    }
                    else
                    {
                        Dbg.Warn($"ConnectDirect fallito (hr=0x{hr:X8}). Se 0x80040217, abilita NV12/P010 in LAV Video → Output Formats. Provo Connect()…");
                        DsError.ThrowExceptionForHR(_graph.Connect(vOut, rIn));
                        Dbg.Log("Video: LAV Video → Renderer (Connect) OK", Dbg.LogLevel.Verbose);
                    }
                }

                bool keepPreWindowed = (_videoWindow != null) && (_choice == VideoRendererChoice.MADVR);
                if (!keepPreWindowed) AttachDisplayInterfaces(initial: false);
                try { _mfDisplay?.SetAspectRatioMode((int)MFVideoARMode.PreservePicture); } catch { }

                if (_hasVideo && _mfDisplay == null && _videoWindow == null)
                    Dbg.Warn("Renderer connesso ma nessun display control – ritento dopo Run().");

                // snapshot MT già qui, ma alcuni renderer negoziano solo dopo Run()
                TryDumpNegotiatedVideoMT();
                return;
            }
            catch (Exception firstEx)
            {
                Dbg.Warn("ConnectVideoPath primo tentativo EX: " + firstEx.Message);

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
                    try { _mfDisplay?.SetAspectRatioMode((int)MFVideoARMode.PreservePicture); } catch { }
                    TryDumpNegotiatedVideoMT();
                    return;
                }
                catch (Exception cscEx) { Dbg.Error("ConnectVideoPath fallback CSC EX: " + cscEx.Message); throw; }
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
        public void SetUpscaling(bool enable) { _allowUpscaling = enable; Dbg.Log("Upscaling " + (enable ? "ON" : "OFF"), Dbg.LogLevel.Info); _updateCb?.Invoke(); }

        private Action? _updateCb;
        public void BindUpdateCallback(Action cb) => _updateCb = cb;

        public void Play()
        {
            int hr = _control?.Run() ?? 0; DsError.ThrowExceptionForHR(hr);
            try { if (_mfDisplay == null && _videoWindow == null) AttachDisplayInterfaces(initial: false); } catch { }

            try
            {
                _mfDisplay?.RepaintVideo();
                _videoWindow?.put_Visible(OABool.True);
            }
            catch { }

            // Poll robusto per ottenere MediaType negoziato anche con renderer capricciosi
            StartMediaTypePolling();

            SafeStatus("Riproduzione.");
            _updateCb?.Invoke();
            if (_fileIsHdr) HdrTrace("Play() → Run + RepaintVideo");
            // Dopo la Run, i formati audio possono cambiare: ricontrolla e notifica (UI thread)
            var t = new System.Windows.Forms.Timer { Interval = 150 };
            t.Tick += (_, __) =>
            {
                try
                {
                    t.Stop(); t.Dispose();
                    TryDetectBitstream(); // ← gira sull’UI thread (STA)
                    SafeStatus("Audio: " + (_bitstreamActive ? "Bitstream" : "PCM"));
                }
                catch { /* best-effort */ }
            };
            t.Start();
        }
        public void Pause() { int hr = _control?.Pause() ?? 0; DsError.ThrowExceptionForHR(hr); SafeStatus("Pausa."); if (_fileIsHdr) HdrTrace("Pause()"); }
        public void Stop() { try { _control?.Stop(); } catch { } SafeStatus("Stop."); if (_fileIsHdr) HdrTrace("Stop()"); }
        public void SetVolume(float v)
        {
            if (!_bitstreamActive && _basicAudio != null)
            {
                try
                {
                    int ds = (v <= 0.0001f) ? -10000
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
                    // true se non Audio/Video
                    if (mt.majorType != DirectShowLib.MediaType.Audio &&
                        mt.majorType != DirectShowLib.MediaType.Video &&
                        mt.majorType != Guid.Empty)
                        return true;

                    // fallback per subtype testuale/PGS/ASS/SSA/VobSub ecc.
                    var st = mt.subType.ToString().ToUpperInvariant();
                    if (st.Contains("HDMV") || st.Contains("PGS") || st.Contains("SUBPICTURE") ||
                        st.Contains("SSA") || st.Contains("ASS") || st.Contains("S_TEXT") ||
                        st.Contains("DVB") || st.Contains("VOBSUB"))
                        return true;

                    return false;
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
            catch (Exception ex) { SafeStatus("IAMStreamSelect: " + ex.Message); Dbg.Warn("EnableByGlobalIndex EX: " + ex); }
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

        public void Dispose()
        {
            StopTimer();
            _mtPollCts?.Cancel();
            DisposeGraph();
        }
        private void SaveResumePointIfNeeded()
        {
            try
            {
                if (string.IsNullOrEmpty(_currentMediaPath))
                    return;

                double dur = DurationSeconds;
                double pos = PositionSeconds;

                PlaybackResumeStore.SaveOrClear(_currentMediaPath, pos, dur);
            }
            catch (Exception ex)
            {
                Dbg.Warn("SaveResumePointIfNeeded EX: " + ex.Message);
            }
        }
        private void DisposeGraph()
        {
            Dbg.Log("DisposeGraph()", Dbg.LogLevel.Verbose);

            // NEW: salva eventuale punto di ripresa prima di smontare il grafo
            SaveResumePointIfNeeded();

            try { _control?.Stop(); } catch { }
            ReleaseCom(ref _mfDisplay);
            ReleaseCom(ref _videoWindow);
            ReleaseCom(ref _lavSource); ReleaseCom(ref _lavVideo); ReleaseCom(ref _lavAudio); ReleaseCom(ref _videoRenderer); ReleaseCom(ref _audioRenderer);
            ReleaseCom(ref _seek); ReleaseCom(ref _basicAudio); ReleaseCom(ref _control); ReleaseCom(ref _graph);
            try { GC.Collect(); GC.WaitForPendingFinalizers(); } catch { }
            _lastOwnerHwnd = IntPtr.Zero;

            try { _thumb?.Dispose(); } catch { }
            _thumb = null;
            _currentMediaPath = null;
        }

        private static void ReleaseCom<T>(ref T? obj) where T : class
        { if (obj == null) return; try { if (Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj); } catch { } finally { obj = null; } }

        private static readonly Guid CLSID_MPCVR = new("71F080AA-8661-4093-B15E-4F6903E77D0A");
        private static readonly Guid MR_VIDEO_RENDER_SERVICE = new("1092A86C-AB1A-459A-A336-831FBC4D11FF");

        private void AttachDisplayInterfaces(bool initial)
        {
            bool preserveWindowedRenderer =
                (!initial) &&
                (_choice == VideoRendererChoice.MADVR) &&
                (_videoWindow != null);

            if (!initial)
            {
                ReleaseCom(ref _mfDisplay);
                if (!preserveWindowedRenderer) ReleaseCom(ref _videoWindow);
            }

            if (!_hasVideo)
            {
                Dbg.Log("AttachDisplayInterfaces: skip (no video).", Dbg.LogLevel.Verbose);
                return;
            }

            // 1) pin input (MPCVR spesso espone qui IMFVideoDisplayControl)
            try
            {
                var inPin = _videoRenderer != null ? FindPin(_videoRenderer, PinDirection.Input, null) : null;
                if (inPin is IMFGetService gsPin)
                {
                    int hrPin = gsPin.GetService(MR_VIDEO_RENDER_SERVICE, typeof(IMFVideoDisplayControl).GUID, out object obj2);
                    if (hrPin == 0 && obj2 is IMFVideoDisplayControl d2)
                    {
                        _mfDisplay = d2;
                        _mfDisplay.SetAspectRatioMode((int)MFVideoARMode.PreservePicture);
                        try { _mfDisplay.SetFullscreen(false); } catch { }
                        Dbg.Log($"{(initial ? "(pre)" : "(post)")} IMFVideoDisplayControl ottenuto dall'input pin.");
                        return;
                    }
                    else Dbg.Warn($"{(initial ? "(pre)" : "(post)")} IMFGetService(pin) hr=0x{hrPin:X8}");
                }
            }
            catch (Exception ex) { Dbg.Warn("AttachDisplayInterfaces (pin) EX: " + ex.Message); }

            // 2) dal filtro (EVR classico)
            try
            {
                if (_videoRenderer is IMFGetService gs1)
                {
                    int hr1 = gs1.GetService(MR_VIDEO_RENDER_SERVICE, typeof(IMFVideoDisplayControl).GUID, out object obj);
                    if (hr1 == 0 && obj is IMFVideoDisplayControl d1)
                    {
                        _mfDisplay = d1;
                        _mfDisplay.SetAspectRatioMode((int)MFVideoARMode.PreservePicture);
                        try { _mfDisplay.SetFullscreen(false); } catch { }
                        Dbg.Log($"{(initial ? "(pre)" : "(post)")} IMFVideoDisplayControl ottenuto dal filtro.");
                        return;
                    }
                    else Dbg.Warn($"{(initial ? "(pre)" : "(post)")} IMFGetService(filter) hr=0x{hr1:X8}");
                }
            }
            catch (Exception ex) { Dbg.Warn("AttachDisplayInterfaces (filter) EX: " + ex.Message); }

            // 3) windowed (MPCVR/madVR) → IVideoWindow
            try
            {
                if (_videoRenderer != null && !preserveWindowedRenderer)
                {
                    _videoWindow = (IVideoWindow)_videoRenderer;
                    Dbg.Log($"{(initial ? "(pre)" : "(post)")} IVideoWindow acquisito dal renderer.");
                    return;
                }
                if (preserveWindowedRenderer && _videoWindow != null)
                {
                    Dbg.Log($"{(initial ? "(pre)" : "(post)")} IVideoWindow windowed renderer preservato.");
                    return;
                }
            }
            catch (Exception ex) { Dbg.Warn("AttachDisplayInterfaces (IVideoWindow/Renderer) EX: " + ex.Message); }

            // 4) fallback dal graph
            try
            {
                if (_graph != null && _videoWindow == null)
                {
                    _videoWindow = (IVideoWindow)_graph;
                    Dbg.Log($"{(initial ? "(pre)" : "(post)")} IVideoWindow acquisito dal FilterGraph.");
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
            switch (_stereo)
            {
                case Stereo3DMode.SBS:
                    src = new MFVideoNormalizedRect(0f, 0f, 0.5f, 1f);
                    cropW = natW / 2;
                    break;
                case Stereo3DMode.TAB:
                    src = new MFVideoNormalizedRect(0f, 0f, 1f, 0.5f);
                    cropH = natH / 2;
                    break;
            }

            double ar = cropW / (double)cropH;

            int dstW, dstH;
            if (_allowUpscaling)
            {
                dstW = ownerClient.Width;
                dstH = (int)Math.Round(dstW / ar);
                if (dstH > ownerClient.Height)
                {
                    dstH = ownerClient.Height;
                    dstW = (int)Math.Round(dstH * ar);
                }
            }
            else
            {
                dstW = Math.Min(ownerClient.Width, cropW);
                dstH = (int)Math.Round(dstW / ar);
                if (dstH > ownerClient.Height || dstH > cropH)
                {
                    dstH = Math.Min(ownerClient.Height, cropH);
                    dstW = (int)Math.Round(dstH * ar);
                }
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

            var dsByName = CreateFilterByName("Default DirectSound Device");
            if (dsByName != null) return dsByName;

            var dsByClsid = CreateFilterByClsid(new Guid("79376820-07D0-11CF-A24D-0020AFD79767"), "Default DirectSound Device (CLSID)");
            if (dsByClsid != null) return dsByClsid;

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
                    VideoRendererChoice.MADVR => CreateFilterByName("madVR Renderer")
                                              ?? CreateFilterByName("madVR")
                                              ?? throw new ApplicationException("madVR non trovato. Esegui 'install.bat' come Amministratore nella cartella di madVR."),
                    VideoRendererChoice.MPCVR => CreateFilterByClsid(CLSID_MPCVR, "MPC Video Renderer (CLSID)")
                                              ?? CreateFilterByName("MPC Video Renderer")
                                              ?? CreateFilterByName("MPCVR"),
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
                if (f is IPersist p) { p.GetClassID(out var cls); return cls.ToString(); }
            }
            catch { }

            return f.GetType().Name;
        }

        [ComImport, Guid("0000010c-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersist { [PreserveSig] int GetClassID(out Guid pClassID); }

        private void TryDetectBitstream()
        {
            bool old = _bitstreamActive;
            bool now = old; // ← fallback conservativo: in caso di errore NON cambiare stato

            if (_lavAudio == null || _graph == null)
            {
                _bitstreamActive = false;  // grafo smontato: ok portare a false
                Dbg.Log("Audio bitstreamActive=" + _bitstreamActive);
                if (_bitstreamActive != old) { try { OnBitstreamChanged?.Invoke(_bitstreamActive); } catch { } try { _updateCb?.Invoke(); } catch { } }
                return;
            }

            try
            {
                var aOut = FindPin(_lavAudio, PinDirection.Output, null);
                if (aOut == null) { Dbg.Warn("TryDetectBitstream: LAV Audio out pin null"); _bitstreamActive = false; return; }

                aOut.ConnectedTo(out var rIn);
                if (rIn == null) { Dbg.Warn("TryDetectBitstream: downstream audio pin null"); _bitstreamActive = false; return; }

                var mt = new AMMediaType();
                int hr = rIn.ConnectionMediaType(mt);
                if (hr != 0) { Marshal.ReleaseComObject(rIn); _bitstreamActive = false; return; }

                try
                {
                    if (mt.formatType == FormatType.WaveEx && mt.formatPtr != IntPtr.Zero)
                    {
                        var wfx = Marshal.PtrToStructure<WaveFormatEx>(mt.formatPtr);

                        if (wfx.wFormatTag == 1 || wfx.wFormatTag == 3)
                            now = false; // PCM / FLOAT
                        else if (wfx.wFormatTag == 0x0092)
                            now = true;  // AC-3 IEC61937
                        else if (wfx.wFormatTag == 0xFFFE && wfx.cbSize >= 22)
                        {
                            var ext = Marshal.PtrToStructure<WaveFormatExtensible>(mt.formatPtr);
                            now = !(ext.SubFormat == MediaSubType.PCM || ext.SubFormat == MediaSubType.IEEE_FLOAT);
                        }
                        else
                            now = true;  // altri tag → considera bitstream
                    }
                    else
                    {
                        now = !(mt.subType == MediaSubType.PCM || mt.subType == MediaSubType.IEEE_FLOAT);
                    }
                }
                finally { DsUtils.FreeAMMediaType(mt); Marshal.ReleaseComObject(rIn); }
            }
            catch (Exception ex)
            {
                Dbg.Warn("TryDetectBitstream EX: " + ex.Message);
                // esci lasciando il valore invariato
                return;
            }

            _bitstreamActive = now;
            Dbg.Log("Audio bitstreamActive=" + _bitstreamActive);
            if (_bitstreamActive != old) { try { OnBitstreamChanged?.Invoke(_bitstreamActive); } catch { } try { _updateCb?.Invoke(); } catch { } }
        }

        private void TryDumpNegotiatedVideoMT()
        {
            try
            {
                if (_graph == null || _videoRenderer == null) return;

                // ⛔ Con madVR/MPCVR windowed NON tentare di enumerare pin/MT
                if (IsWindowedRenderer)
                {
                    Dbg.Log("TryDumpNegotiatedVideoMT: windowed renderer → skip", Dbg.LogLevel.Verbose);
                    return;
                }

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
                }
                _lastVmtDump = sb.ToString();
                _lastVmtAt = DateTime.Now;
                Dbg.Log(_lastVmtDump.Replace("\r\n", " | "), Dbg.LogLevel.Verbose);
                if (mt != null) DsUtils.FreeAMMediaType(mt);
            }
            catch (Exception ex) { Dbg.Warn("TryDumpNegotiatedVideoMT EX: " + ex.Message); }
        }

        private void StartMediaTypePolling()
        {
            _mtPollCts?.Cancel();
            var cts = new CancellationTokenSource();
            _mtPollCts = cts;

            Task.Run(async () =>
            {
                for (int i = 0; i < 10 && !cts.IsCancellationRequested; i++)
                {
                    TryDumpNegotiatedVideoMT();
                    var cf = _cachedFmt;
                    if (cf.W > 0 && cf.H > 0) { _updateCb?.Invoke(); break; }
                    await Task.Delay(200, cts.Token).ConfigureAwait(false);
                }
            }, cts.Token);
        }

        public (string text, DateTime when) GetLastVideoMTDump() => (_lastVmtDump, _lastVmtAt);
        public (int width, int height, string subtype) GetNegotiatedVideoFormat()
        {
            // Evita di toccare i pin con madVR/MPCVR windowed
            if (IsWindowedRenderer)
            {
                // Prova a dare almeno qualcosa di sensato: col MF niente, con madVR nulla → “n/d”
                return (0, 0, "madVR/MPCVR (windowed)");
            }

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
            catch (Exception ex) { Dbg.Warn("GetNegotiatedVideoFormat EX: " + ex.Message); }
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
            return false;
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
            if (ownerClient.Width < 2 || ownerClient.Height < 2) return; // evita SetWindowPosition(0,0)

            // 1) EVR/MPCVR (windowless)
            try
            {
                if (_mfDisplay != null)
                {
                    _mfDisplay.SetVideoWindow(ownerHwnd);
                    _lastOwnerHwnd = ownerHwnd; // per hotkeys
                    try { _mfDisplay.SetFullscreen(false); } catch { }
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
                    _lastDest = dest;
                    return;
                }
            }
            catch (Exception ex) { Dbg.Warn("UpdateVideoWindow (EVR) EX: " + ex.Message); }

            // 2) madVR/MPCVR (windowed) via IVideoWindow
            try
            {
                if (_videoWindow != null)
                {
                    _videoWindow.put_Owner(ownerHwnd);
                    _videoWindow.put_MessageDrain(ownerHwnd);
                    _lastOwnerHwnd = ownerHwnd; // per hotkeys

                    const int WS_CHILD = 0x40000000;
                    const int WS_CLIPSIBLINGS = 0x04000000;
                    const int WS_CLIPCHILDREN = 0x02000000;
                    _videoWindow.put_WindowStyle((WindowStyle)(WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN));

                    try { _videoWindow.put_FullScreenMode(OABool.False); _videoWindow.put_AutoShow(OABool.False); } catch { }

                    _videoWindow.SetWindowPosition(ownerClient.Left, ownerClient.Top, ownerClient.Width, ownerClient.Height);
                    _videoWindow.put_Visible(OABool.True);

                    _lastDest = new MFRect(ownerClient.Left, ownerClient.Top, ownerClient.Right, ownerClient.Bottom);
                    return;
                }
            }
            catch (Exception ex) { Dbg.Warn("UpdateVideoWindow (IVideoWindow) EX: " + ex.Message); }
        }

        public Rectangle GetLastDestRectAsClient(Rectangle ownerClient)
        {
            try
            {
                int l = _lastDest.left, t = _lastDest.top;
                int w = Math.Max(0, _lastDest.right - _lastDest.left);
                int h = Math.Max(0, _lastDest.bottom - _lastDest.top);
                if (w <= 0 || h <= 0) return ownerClient;
                return new Rectangle(l, t, w, h);
            }
            catch { return ownerClient; }
        }

        private void SafeStatus(string s)
        {
            try { OnStatus?.Invoke(s); }
            catch (Exception ex) { Dbg.Warn("OnStatus handler EX: " + ex.Message); }
        }

        // ======== Anteprima overlay via Thumbnailer ========
        public Bitmap? GetPreviewFrame(double seconds, int maxW = 360)
        {
            try
            {
                if (!_hasVideo)
                    return null;

                // lazy-reopen nel caso _thumb sia stato rilasciato/chiuso
                if (_thumb == null && !string.IsNullOrEmpty(_currentMediaPath))
                {
                    try
                    {
                        _thumb = new Thumbnailer();
                        _thumb.Open(_currentMediaPath!);
                        Dbg.Log("Thumbnailer riaperto lazy.");
                    }
                    catch (Exception ex)
                    {
                        Dbg.Warn("GetPreviewFrame lazy Open EX: " + ex.Message);
                        return null;
                    }
                }

                if (_thumb == null) return null;

                double dur = DurationSeconds;
                if (dur > 0) seconds = Math.Max(0, Math.Min(seconds, Math.Max(0, dur - 0.05)));

                var bmp = _thumb.Get(seconds, maxW);
                if (bmp != null) return bmp;

                // fallback robusti: prova qualche offset (GOP lunghi / no keyframe)
                double[] offsets = { -2, +2, -5, +5, 0 };
                foreach (var off in offsets)
                {
                    double s2 = Math.Max(0, seconds + off);
                    bmp = _thumb.Get(s2, maxW);
                    if (bmp != null) return bmp;
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("GetPreviewFrame EX: " + ex.Message);
            }
            return null;
        }

        // ======== HOTKEY BRIDGE (SendInput) ========
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion U; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
        }
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); // SW_RESTORE=9
                                                                                                    // --- madVR hotkeys forward ---
        private IntPtr _madvrHwnd = IntPtr.Zero;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_CHAR = 0x0102;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private void SendKey(ushort vk, bool down)
        {
            var inp = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = down ? 0u : KEYEVENTF_KEYUP, time = 0, dwExtraInfo = IntPtr.Zero }
                }
            };
            SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
        }
        private void SendChord(bool ctrl, bool alt, bool shift, Keys key)
        {
            if (_lastOwnerHwnd != IntPtr.Zero)
            {
                if (IsIconic(_lastOwnerHwnd)) ShowWindow(_lastOwnerHwnd, 9);
                SetForegroundWindow(_lastOwnerHwnd);
            }
            if (ctrl) SendKey((ushort)Keys.ControlKey, true);
            if (alt) SendKey((ushort)Keys.Menu, true);
            if (shift) SendKey((ushort)Keys.ShiftKey, true);

            SendKey((ushort)key, true);
            SendKey((ushort)key, false);

            if (shift) SendKey((ushort)Keys.ShiftKey, false);
            if (alt) SendKey((ushort)Keys.Menu, false);
            if (ctrl) SendKey((ushort)Keys.ControlKey, false);
        }

        // ======== API pubbliche: mapping → scorciatoie madVR ========
        public void SetMadVrChroma(MadVrCategoryPreset preset)
        {
            if (_choice != VideoRendererChoice.MADVR) { Dbg.Log("SetMadVrChroma: non madVR, skip.", Dbg.LogLevel.Verbose); return; }
            switch (preset)
            {
                case MadVrCategoryPreset.RendererDefault: SendChord(true, true, false, Keys.C); break;
                case MadVrCategoryPreset.Profile1: SendChord(true, true, false, Keys.F1); break;
                case MadVrCategoryPreset.Profile2: SendChord(true, true, false, Keys.F2); break;
                case MadVrCategoryPreset.Profile3: SendChord(true, true, false, Keys.F3); break;
                case MadVrCategoryPreset.Profile4: SendChord(true, true, false, Keys.F4); break;
                case MadVrCategoryPreset.Profile5: SendChord(true, true, false, Keys.F5); break;
                case MadVrCategoryPreset.Profile6: SendChord(true, true, false, Keys.F6); break;
            }
            Dbg.Log($"madVR Chroma preset → {preset}");
        }

        public void SetMadVrImageUpscale(MadVrCategoryPreset preset)
        {
            if (_choice != VideoRendererChoice.MADVR) { Dbg.Log("SetMadVrImageUpscale: non madVR, skip.", Dbg.LogLevel.Verbose); return; }
            switch (preset)
            {
                case MadVrCategoryPreset.RendererDefault: SendChord(true, true, false, Keys.U); break;
                case MadVrCategoryPreset.Profile1: SendChord(true, true, false, Keys.F7); break;
                case MadVrCategoryPreset.Profile2: SendChord(true, true, false, Keys.F8); break;
                case MadVrCategoryPreset.Profile3: SendChord(true, true, false, Keys.F9); break;
                case MadVrCategoryPreset.Profile4: SendChord(true, true, false, Keys.F10); break;
                case MadVrCategoryPreset.Profile5: SendChord(true, true, false, Keys.F11); break;
                case MadVrCategoryPreset.Profile6: SendChord(true, true, false, Keys.F12); break;
            }
            Dbg.Log($"madVR ImageUpscale preset → {preset}");
        }

        public void SetMadVrImageDownscale(MadVrCategoryPreset preset)
        {
            if (_choice != VideoRendererChoice.MADVR) { Dbg.Log("SetMadVrImageDownscale: non madVR, skip.", Dbg.LogLevel.Verbose); return; }
            switch (preset)
            {
                case MadVrCategoryPreset.RendererDefault: SendChord(true, true, false, Keys.D); break;
                case MadVrCategoryPreset.Profile1: SendChord(true, true, false, Keys.D1); break;
                case MadVrCategoryPreset.Profile2: SendChord(true, true, false, Keys.D2); break;
                case MadVrCategoryPreset.Profile3: SendChord(true, true, false, Keys.D3); break;
                case MadVrCategoryPreset.Profile4: SendChord(true, true, false, Keys.D4); break;
                case MadVrCategoryPreset.Profile5: SendChord(true, true, false, Keys.D5); break;
                case MadVrCategoryPreset.Profile6: SendChord(true, true, false, Keys.D6); break;
            }
            Dbg.Log($"madVR Downscale preset → {preset}");
        }

        public void SetMadVrRefinement(MadVrCategoryPreset preset)
        {
            if (_choice != VideoRendererChoice.MADVR) { Dbg.Log("SetMadVrRefinement: non madVR, skip.", Dbg.LogLevel.Verbose); return; }
            switch (preset)
            {
                case MadVrCategoryPreset.RendererDefault: SendChord(true, true, false, Keys.R); break;
                case MadVrCategoryPreset.Profile1: SendChord(true, true, true, Keys.D1); break; // Ctrl+Alt+Shift+1
                case MadVrCategoryPreset.Profile2: SendChord(true, true, true, Keys.D2); break;
                case MadVrCategoryPreset.Profile3: SendChord(true, true, true, Keys.D3); break;
                case MadVrCategoryPreset.Profile4: SendChord(true, true, true, Keys.D4); break;
                case MadVrCategoryPreset.Profile5: SendChord(true, true, true, Keys.D5); break;
                case MadVrCategoryPreset.Profile6: SendChord(true, true, true, Keys.D6); break;
            }
            Dbg.Log($"madVR Refinement preset → {preset}");
        }

        public void SetMadVrFps(MadVrFpsChoice choice)
        {
            if (_choice != VideoRendererChoice.MADVR) { Dbg.Log("SetMadVrFps: non madVR, skip.", Dbg.LogLevel.Verbose); return; }
            switch (choice)
            {
                // assegna in madVR questi tre comandi ai relativi hotkey
                case MadVrFpsChoice.Adapt: SendChord(true, true, false, Keys.NumPad7); break;
                case MadVrFpsChoice.Force60: SendChord(true, true, false, Keys.NumPad8); break;
                case MadVrFpsChoice.Force24: SendChord(true, true, false, Keys.NumPad9); break;
            }
            Dbg.Log($"madVR FPS choice → {choice}");
        }

        public void SetMadVrHdrMode(MadVrHdrMode mode)
        {
            if (_choice != VideoRendererChoice.MADVR) { Dbg.Log("SetMadVrHdrMode: non madVR, skip.", Dbg.LogLevel.Verbose); return; }
            switch (mode)
            {
                // mappa questi hotkey a profili HDR nei settings di madVR (vedi tabella sotto)
                case MadVrHdrMode.Auto: SendChord(true, true, false, Keys.H); break;         // Auto
                case MadVrHdrMode.PassthroughHdr: SendChord(true, true, true, Keys.P); break;         // Passthrough HDR a display
                case MadVrHdrMode.ToneMapHdrToSdr: SendChord(true, true, false, Keys.S); break;         // Tone-map HDR→SDR (pixel shaders)
                case MadVrHdrMode.LutHdrToSdr: SendChord(true, true, false, Keys.L); break;         // HDR→SDR via 3DLUT
            }
            Dbg.Log($"madVR HDR mode → {mode}");
        }
    }
}
