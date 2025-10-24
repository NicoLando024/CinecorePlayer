#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Drawing;
using System.Diagnostics;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Collections.Generic;
using DirectShowLib;
using FFmpeg.AutoGen;
using VRChoice = global::CinecorePlayer2025.VideoRendererChoice;
using HDRMode = global::CinecorePlayer2025.HdrMode;
using Stereo3DMode = global::CinecorePlayer2025.Stereo3DMode;
using System.Text.RegularExpressions;

namespace CinecorePlayer2025
{
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
        protected override void OnPaintBackground(PaintEventArgs e) { }
    }

    // ======= OverlayHostForm – top-level davvero trasparente =======
    internal sealed class OverlayHostForm : Form
    {
        public Panel Surface { get; } = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };

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

            BackColor = Color.Black;           // colorkey
            TransparencyKey = Color.Black;

            Controls.Add(Surface);
            Surface.BackColor = this.TransparencyKey;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryApplyLayeredColorKey(this.TransparencyKey);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_ERASEBKGND = 0x0014;
            if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }
            base.WndProc(ref m);
        }

        private void TryApplyLayeredColorKey(Color key)
        {
            if (!IsHandleCreated) return;
            uint rgb = (uint)(key.R | (key.G << 8) | (key.B << 16));
            try { SetLayeredWindowAttributes(this.Handle, rgb, 255, LWA_COLORKEY); } catch { }
            try
            {
                Win32.SetWindowPos(this.Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOZORDER | Win32.SWP_NOSIZE | Win32.SWP_NOMOVE | Win32.SWP_FRAMECHANGED);
            }
            catch { }
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00080000;  // WS_EX_LAYERED
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(this.TransparencyKey);
        }

        public void SyncTo(Form owner)
        {
            if (!owner.Visible) return;
            var rc = owner.RectangleToScreen(owner.ClientRectangle);
            Bounds = rc;
            if (Visible) { try { BringToFront(); } catch { } }
        }

        private const int SWP_NOACTIVATE = 0x0010;

        public void SyncToScreen(Rectangle screenRect)
        {
            if (screenRect.Width <= 0 || screenRect.Height <= 0) return;
            try
            {
                Win32.SetWindowPos(this.Handle, IntPtr.Zero,
                    screenRect.X, screenRect.Y, screenRect.Width, screenRect.Height,
                    Win32.SWP_SHOWWINDOW | Win32.SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch
            {
                Bounds = screenRect; // fallback
            }
            if (Visible) { try { BringToFront(); } catch { } }
        }

        public void SetClickThrough(bool passThrough)
        {
            if (!IsHandleCreated) return;
            int ex = GetWindowLong(this.Handle, GWL_EXSTYLE);
            if (passThrough) ex |= WS_EX_TRANSPARENT; else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLong(this.Handle, GWL_EXSTYLE, ex);
            Win32.SetWindowPos(this.Handle, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_FRAMECHANGED | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER);
        }

        [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const uint LWA_COLORKEY = 0x00000001;

        public void SetInteractive(bool on)
        {
            if (!IsHandleCreated) return;
            int ex = GetWindowLong(this.Handle, GWL_EXSTYLE);
            if (on) ex &= ~WS_EX_NOACTIVATE; else ex |= WS_EX_NOACTIVATE;
            SetWindowLong(this.Handle, GWL_EXSTYLE, ex);
            Win32.SetWindowPos(this.Handle, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_FRAMECHANGED | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER);
        }
    }

    // ======= WaveFormatExtensible helper =======
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormatExtensibleLocal
    {
        public WaveFormatEx Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    // ======= UI principale =======
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
        // === Audio-only meters (LiveCharts) ===
        private AudioMetersLiveCharts? _audioMeters;
        private LoopbackSampler? _audioSampler;

        private ContextMenuStrip _menu = null!;
        private TableLayoutPanel _rootLayout = null!;
        private IPlaybackEngine? _engine;
        private string? _currentPath;
        private MediaProbe.Result? _info;

        private string? _selectedAudioRendererName;
        private bool _selectedRendererLooksHdmi;
        private Stereo3DMode _stereo = Stereo3DMode.None;
        private HDRMode _hdr = HDRMode.Auto;
        private bool _scrubActive = false;
        private double _scrubPending = -1;

        private double _duration;
        private bool _paused;

        private readonly Thumbnailer _thumb = new();
        private CancellationTokenSource? _thumbCts;
        private volatile bool _previewBusy;

        private FormWindowState _prevState; private FormBorderStyle _prevBorder;
        private readonly OverlayHostForm _overlayHost;
        private InlineOverlayPanel? _overlayInlineHost;

        private Rectangle _lastVideoDestInForm = Rectangle.Empty;

        private bool _enableUpscaling = false;
        private int _targetFps = 0;
        private bool _preferBitstreamUi = true;
        private readonly DisplayModeSwitcher _refresh = new();

        private SettingsModal _settingsModal = null!;
        private CreditsModal _creditsModal = null!;

        private static readonly VRChoice[] ORDER_HDR = { VRChoice.MADVR, VRChoice.MPCVR };
        private static readonly VRChoice[] ORDER_SDR = { VRChoice.EVR };

        private ToolStripMenuItem _mAudioLang = null!;
        private ToolStripMenuItem _mSubtitles = null!;
        private ToolStripMenuItem _mAudioOut = null!;

        private long _vPrevBytes = 0;
        private DateTime _vPrevWhen = DateTime.MinValue;
        private int _videoBitrateNowKbps = 0;

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private const int WM_SETICON = 0x0080; private const int ICON_SMALL = 0, ICON_BIG = 1, ICON_SMALL2 = 2;
        // ===== Process I/O (per bitrate container NOW) =====
        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS counters);

        private Icon? _iconBig; private Icon? _iconSmall;

        private Action<string>? _engineStatusHandler;
        private Action<double>? _engineProgressHandler;
        private Action? _engineUpdateHandler;

        private long _ioPrevBytes = 0;
        private DateTime _ioPrevWhen = DateTime.MinValue;
        private int _containerBitrateNowKbps = 0;
        private int _audioBitrateNowKbps = 0;
        private volatile bool _bitstreamNow = false;
        // Debug: ultimo stato loggato di IPlaybackEngine.IsBitstreamActive()
        private bool _lastIsBsLogged = false;
        // Usa SOLO l’engine per sapere se siamo in bitstream
        private bool IsBitstream() => _engine?.IsBitstreamActive() ?? false;

        // --- Packet-level bitrate sampler (FFmpeg) ---
        private readonly PacketRateSampler _pktRate = new();
        private DateTime _lastPktSample = DateTime.MinValue;
        // Timestamp ultimo campione valido “ora” (per non sovrascrivere col fallback)
        private DateTime _aNowTs = DateTime.MinValue, _vNowTs = DateTime.MinValue;

        // === RUNNING AVERAGES (media live) ===
        // Pubblica ogni 10s; media cumulativa dall’avvio (pesata per il tempo)
        private const int AVG_PUBLISH_SEC = 10;

        private DateTime _avgLastPublish = DateTime.MinValue;
        private DateTime _avgLastTs = DateTime.MinValue;   // ultimo timestamp campione
        private double _avgAudioBitSec = 0;               // somma (kbps * secondi)
        private double _avgVideoBitSec = 0;               // somma (kbps * secondi)
        private double _avgDurSec = 0;                    // somma dei Δt

        private double _audioAvgLiveKbps = 0;
        private double _videoAvgLiveKbps = 0;
        private DateTime _bitstreamLastTrue = DateTime.MinValue;

        // === TIMER per aggiornare le statistiche dell'overlay a cadenza fissa ===
        private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 250 };

        private AudioOnlyOverlay BuildAudioOnlyBanner() => new()
        {
            Dock = DockStyle.Fill,
            Visible = false,
            ImagePath = Path.Combine(AppContext.BaseDirectory, "Assets", "audioOnly.png"),
            Caption = "Audio Only"
        };

        private void RedrawHome()
        {
            if (_splash == null) return;
            _splash.Invalidate(true);
            _splash.Refresh();
            _stack?.Invalidate(true);
            _stack?.Update();
        }

        public PlayerForm()
        {
            Text = "Cinecore Player 2025";
            MinimumSize = new Size(1040, 600);
            BackColor = Color.FromArgb(18, 18, 18);
            DoubleBuffered = true;
            KeyPreview = true;
            KeyDown += PlayerForm_KeyDown;

            Deactivate += (_, __) =>
            {
                if (FormBorderStyle == FormBorderStyle.None)
                    BeginInvoke(new Action(() => { try { Activate(); } catch { } }));
            };

            _rootLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
            _rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _rootLayout.BackColor = Color.Black;

            _stack = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            _videoHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };

            // HUD
            _hud = new HudOverlay { Dock = DockStyle.Fill, AutoHide = true, Visible = false };
            _hud.TimelineVisible = false;

            _infoOverlay = new InfoOverlay { Dock = DockStyle.Top, Visible = false, AutoHeight = true, MaxCardHeight = 420 };

            _overlayHost = new OverlayHostForm();
            AddOwnedForm(_overlayHost);
            _overlayHost.Visible = false;

            _hud.BackColor = Color.Transparent;
            _infoOverlay.BackColor = Color.Transparent;

            _splash = new SplashOverlay { Dock = DockStyle.Fill, Visible = true };
            _splash.OpenRequested += OpenFile;
            _splash.SettingsRequested += ShowSettingsModal;
            _splash.CreditsRequested += ShowCreditsModal;

            _loading = new LoadingOverlay { Dock = DockStyle.Fill, Visible = true };
            _loading.Completed += () =>
            {
                _loading.Visible = false;
                _splash.Visible = (_currentPath == null);
                _hud.Visible = false;
                _hud.TimelineVisible = false;
                BringOverlaysToFront();
            };

            _audioOnlyBanner = BuildAudioOnlyBanner();

            _stack.Controls.Add(_videoHost);
            _stack.Controls.Add(_loading);
            _stack.Controls.Add(_splash);

            _overlayHost.Surface.Controls.Add(_audioOnlyBanner);
            _overlayHost.Surface.Controls.Add(_infoOverlay);
            _overlayHost.Surface.Controls.Add(_hud);
            _hud.BringToFront();
            // Overlay “Audio Meters” (solo audio, LiveCharts)
            _audioMeters = new AudioMetersLiveCharts
            {
                Dock = DockStyle.Fill,
                Visible = false,
                BackColor = _overlayHost.TransparencyKey // nero come colorkey
            };
            _overlayHost.Surface.Controls.Add(_audioMeters);

            _audioOnlyBanner.BackColor = _overlayHost.TransparencyKey;

            // ===== HUD wake: SOLO sopra la fascia HUD (non tutta la hot-zone larga) =====
            void TryWakeHud()
            {
                if (_engine == null || !_engine.HasDisplayControl()) return;

                // Se non conosciamo ancora la dest rect del video, NON svegliare l’HUD.
                if (_lastVideoDestInForm.Width <= 0 || _lastVideoDestInForm.Height <= 0)
                    return;

                var scr = Control.MousePosition;
                var client = this.PointToClient(scr);

                var v = _lastVideoDestInForm;
                int hudH = Math.Min(Math.Max(_hud?.Height ?? 64, 48), 96);
                var hudZone = new Rectangle(v.X, Math.Max(v.Bottom - hudH, v.Y), v.Width, Math.Min(hudH, v.Height));

                bool overHudZone = hudZone.Contains(client);
                bool overHud = IsMouseOverHud();

                if (overHud || overHudZone)
                {
                    if (!_hud.Visible) _hud.Visible = true;
                    _hud.AutoHide = !overHud;
                    _hud.ShowOnce(1400);
                }
                else
                {
                    _hud.AutoHide = true;
                }
            }

            _overlayHost.Surface.MouseMove += (_, __) => TryWakeHud();
            _videoHost.MouseMove += (_, __) => TryWakeHud();
            _overlayHost.Surface.MouseLeave += (_, __) => { if (_engine != null) { _hud.AutoHide = true; _hud.ShowOnce(800); } };
            _videoHost.MouseLeave += (_, __) => { if (_engine != null) { _hud.AutoHide = true; _hud.ShowOnce(800); } };

            _hud.MouseEnter += (_, __) => { _hud.AutoHide = false; };
            _hud.MouseLeave += (_, __) => { _hud.AutoHide = true; _hud.ShowOnce(800); };

            _settingsModal = new SettingsModal { Visible = false, Dock = DockStyle.Fill };
            _settingsModal.ApplyClicked += (fps, upscale, preferBitstream) =>
            {
                _targetFps = fps;
                _enableUpscaling = upscale;
                _preferBitstreamUi = preferBitstream;

                if (_targetFps == 0) { try { _refresh.RestoreIfChanged(); } catch { } }
                if (_enableUpscaling && _manualRendererChoice != VRChoice.MADVR) _manualRendererChoice = VRChoice.MADVR;

                try { _engine?.SetUpscaling(_enableUpscaling); } catch { }

                if (_info != null && _engine != null)
                    UpdateInfoOverlay(_manualRendererChoice ?? (_info.IsHdr ? ORDER_HDR.First() : ORDER_SDR.First()), _info.IsHdr);

                ReopenSame();
            };
            _settingsModal.Closed += () =>
            {
                _settingsModal.Visible = false;
                if (_settingsModal.Tag is bool wasInline && !wasInline) UseOverlayInline(false);
                if (_splash.Visible) RedrawHome();
                BringOverlaysToFront();
            };

            _creditsModal = new CreditsModal { Visible = false, Dock = DockStyle.Fill };
            _creditsModal.Closed += () =>
            {
                _creditsModal.Visible = false;
                _overlayHost.SetInteractive(false);
                if (_creditsModal.Tag is bool wasInline && wasInline) UseOverlayInline(true);
                if (_overlayInlineHost != null) _overlayInlineHost.Visible = false;
                if (_splash.Visible) RedrawHome();
                BringOverlaysToFront();
            };

            _overlayHost.Surface.Controls.Add(_settingsModal);
            _overlayHost.Surface.Controls.Add(_creditsModal);

            BringOverlaysToFront();

            _splash.Visible = false;
            _hud.Visible = false;
            _infoOverlay.Visible = false;
            _loading.Start();

            _rootLayout.Controls.Add(_stack, 0, 0);
            _lblStatus = new Label { Text = "Pronto" };

            Controls.Add(_rootLayout);
            BringOverlaysToFront();

            _hud.GetTime = () => (_engine?.PositionSeconds ?? 0, _duration);
            _hud.GetInfoLine = () => _lblStatus.Text;
            _hud.GetTitle = () =>
            {
                var p = !string.IsNullOrWhiteSpace(_currentPath) ? _currentPath
                        : (!string.IsNullOrWhiteSpace(_thumb?.SourcePath) ? _thumb.SourcePath
                        : MediaProbe.LastProbedPath);
                return string.IsNullOrWhiteSpace(p) ? "" : Path.GetFileNameWithoutExtension(p);
            };
            _hud.OpenClicked += () => OpenFile();
            _hud.PlayPauseClicked += () => TogglePlayPause();
            _hud.StopClicked += () => SafeStop();
            _hud.FullscreenClicked += () => ToggleFullscreen();
            _hud.VolumeChanged += v => ApplyVolume(v);

            _hud.SeekRequested += s =>
            {
                if (_engine == null || _duration <= 0) return;
                _scrubPending = Math.Clamp(s, 0, Math.Max(0.01, _duration));
                try { _engine.PositionSeconds = _scrubPending; } catch { }
                _scrubActive = false;
                _hud.ShowOnce(1200);
            };

            _hud.PreviewRequested += (sec, pt) =>
            {
                _scrubActive = true;
                OnPreviewRequested(sec, pt);
            };

            _hud.SkipBack10Clicked += () => { SeekRelative(-10); _hud.ShowOnce(1200); };
            _hud.SkipForward10Clicked += () => { SeekRelative(10); _hud.ShowOnce(1200); };
            _hud.PrevChapterClicked += () => { SeekChapter(-1); _hud.ShowOnce(1200); };
            _hud.NextChapterClicked += () => { SeekChapter(+1); _hud.ShowOnce(1200); };

            _videoHost.Resize += (_, __) =>
            {
                _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);
                SyncOverlayToVideoRect();
                _infoOverlay.AdjustHeightToContent(_stack.ClientSize.Width);
                BringOverlaysToFront();
                if (_info != null && _engine != null)
                {
                    var r = _manualRendererChoice ?? (_info.IsHdr ? ORDER_HDR.First() : ORDER_SDR.First());
                    UpdateInfoOverlay(r, _info.IsHdr);
                }
            };
            LocationChanged += (_, __) => { SyncOverlayToVideoRect(); };
            SizeChanged += (_, __) => { SyncOverlayToVideoRect(); };

            BuildMenu();
            ContextMenuStrip = _menu;
            _stack.ContextMenuStrip = _menu;
            _hud.ContextMenuStrip = _menu;
            _videoHost.ContextMenuStrip = _menu;
            _splash.ContextMenuStrip = _menu;

            _menu.Opening += (_, e) => { if (_loading.Visible) e.Cancel = true; };

            _hud.SetExternalVolume(1f);
            Dbg.Level = Dbg.LogLevel.Info;

            try
            {
                var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
                var bigPath = Path.Combine(assets, "cinecore_icon_512.ico");
                var smallPath = Path.Combine(assets, "cinecore_icon.ico");
                if (File.Exists(bigPath)) _iconBig = new Icon(bigPath);
                if (File.Exists(smallPath)) _iconSmall = new Icon(smallPath);
                if (_iconBig != null) this.Icon = _iconBig;
                else if (_iconSmall != null) this.Icon = _iconSmall;
            }
            catch { }
        }

        private int ProbeAudioAvgKbps()
        {
            try
            {
                if (_info == null) return 0;
                var t = _info.GetType();

                var pKbps = t.GetProperty("AudioBitrateKbps");
                if (pKbps != null)
                {
                    var v = pKbps.GetValue(_info);
                    if (v is int ik && ik > 0) return ik;
                    if (v is long lk && lk > 0) return (int)lk;
                }

                var pBps = t.GetProperty("AudioBitrate"); // in bps
                if (pBps != null)
                {
                    var v = pBps.GetValue(_info);
                    if (v is long lbps && lbps > 0) return (int)Math.Round(lbps / 1000.0);
                    if (v is int ibps && ibps > 0) return (int)Math.Round(ibps / 1000.0);
                    if (v is double dbps && dbps > 0) return (int)Math.Round(dbps / 1000.0);
                }
            }
            catch { }
            return 0;
        }

        // Blocca shortcut durante il loader
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_loading.Visible) return true;
            if (keyData == Keys.Space) { TogglePlayPause(); return true; }
            if (keyData == Keys.S) { SafeStop(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Blocca tasto destro + ascolta hot-plug audio per ri-verifica PCM/Bitstream
        protected override void WndProc(ref Message m)
        {
            const int WM_CONTEXTMENU = 0x007B;
            const int WM_DEVICECHANGE = 0x0219;
            const int DBT_DEVNODES_CHANGED = 0x0007;
            const int DBT_DEVICEARRIVAL = 0x8000;
            const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

            if (_loading.Visible && m.Msg == WM_CONTEXTMENU) { m.Result = IntPtr.Zero; return; }

            if (m.Msg == WM_DEVICECHANGE)
            {
                int ev = m.WParam.ToInt32();
                if (ev == DBT_DEVNODES_CHANGED || ev == DBT_DEVICEARRIVAL || ev == DBT_DEVICEREMOVECOMPLETE)
                {
                    // Re-check PCM/bitstream e refresh overlay dopo il cambio dispositivo
                    BeginInvoke(new Action(() =>
                    {
                        try { RecheckAudioNow(); } catch { }
                    }));
                }
            }

            base.WndProc(ref m);
        }
        private void RecheckAudioNow()
        {
            if (_engine == null) return;

            // Media container avg per fallback
            int avgContainerKbpsLocal = 0;
            try
            {
                if (!string.IsNullOrEmpty(_currentPath) && File.Exists(_currentPath) && _duration > 1)
                {
                    var fi = new FileInfo(_currentPath);
                    avgContainerKbpsLocal = (int)Math.Round((fi.Length * 8.0 / 1000.0) / _duration);
                }
            }
            catch { }

            var sel = _engine.EnumerateStreams().FirstOrDefault(s => s.IsAudio && s.Selected);
            var lav = GetLavAudioIODetails(sel?.Name);
            _bitstreamNow = IsBitstream();

            int kbps = 0;
            if (lav.AudioNowKbps > 0) kbps = lav.AudioNowKbps;
            if (kbps <= 0 && sel != null) kbps = ParseKbpsFromName(sel.Name);
            if (kbps <= 0 && avgContainerKbpsLocal > 0) kbps = (int)(avgContainerKbpsLocal * 0.30);
            _audioBitrateNowKbps = kbps;

            // refresh immediato dell’overlay info se presente
            if (_info != null)
            {
                var chosen = _manualRendererChoice ?? (_info.IsHdr ? ORDER_HDR.First() : ORDER_SDR.First());
                UpdateInfoOverlay(chosen, _info.IsHdr);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                if (_iconBig != null) SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_BIG, _iconBig.Handle);
                if (_iconSmall != null)
                {
                    SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_SMALL, _iconSmall.Handle);
                    SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_SMALL2, _iconSmall.Handle);
                }
            }
            catch { }
        }

        // “Apri con …” da Esplora
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            TryOpenFromCommandLineSafe();
        }

        private void TryOpenFromCommandLineSafe()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1)
                {
                    var p = args.Skip(1).FirstOrDefault(File.Exists);
                    if (!string.IsNullOrEmpty(p))
                    {
                        SkipLoadingIfActive();
                        OpenPath(p!);
                    }
                }
            }
            catch { }
        }

        private void SkipLoadingIfActive()
        {
            if (_loading?.Visible == true)
            {
                _loading.Visible = false;
                _splash.Visible = false;
                _loading?.Invalidate();
            }
        }

        private void UseOverlayInline(bool enable)
        {
            // Manteniamo SEMPRE l'host layered
            enable = false;

            if (_overlayInlineHost == null)
            {
                _overlayInlineHost = new InlineOverlayPanel { Dock = DockStyle.Fill };
                _stack.Controls.Add(_overlayInlineHost);
                _overlayInlineHost.Visible = false;
            }

            Control target = _overlayHost.Surface;

            if (_audioOnlyBanner.Parent != target) _audioOnlyBanner.Parent = target;
            if (_hud.Parent != target) _hud.Parent = target;
            if (_infoOverlay.Parent != target) _infoOverlay.Parent = target;
            if (_settingsModal.Parent != target) _settingsModal.Parent = target;
            if (_creditsModal.Parent != target) _creditsModal.Parent = target;

            if (!_overlayHost.Visible) SafeShowOverlayHost();
            SyncOverlayToVideoRect();
            try { _overlayHost.BringToFront(); } catch { }

            BringOverlaysToFront();
            _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);
        }

        private bool _pendingShowOverlayOnHandleCreated;
        private void SafeShowOverlayHost()
        {
            if (_overlayHost == null || _overlayHost.IsDisposed) return;
            if (_overlayHost.Visible) return;

            if (!IsHandleCreated)
            {
                if (_pendingShowOverlayOnHandleCreated) return;
                _pendingShowOverlayOnHandleCreated = true;

                void Handler(object? s, EventArgs e)
                {
                    try { this.HandleCreated -= Handler; }
                    catch { }
                    finally
                    {
                        _pendingShowOverlayOnHandleCreated = false;
                        try
                        {
                            BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (_overlayHost == null || _overlayHost.IsDisposed || _overlayHost.Visible) return;
                                    _overlayHost.Show();
                                    try { _overlayHost.BringToFront(); } catch { }
                                }
                                catch
                                {
                                    try { if (_overlayHost != null && !_overlayHost.IsDisposed) { _overlayHost.Hide(); _overlayHost.Show(); } } catch { }
                                }
                            }));
                        }
                        catch
                        {
                            try { if (_overlayHost != null && !_overlayHost.IsDisposed && !_overlayHost.Visible) _overlayHost.Show(); } catch { }
                        }
                    }
                }

                this.HandleCreated += Handler;
                return;
            }

            try
            {
                BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (_overlayHost == null || _overlayHost.IsDisposed || _overlayHost.Visible) return;
                        _overlayHost.Show();
                        try { _overlayHost.BringToFront(); } catch { }
                    }
                    catch (InvalidOperationException)
                    {
                        try { _overlayHost.Hide(); _overlayHost.Show(); } catch { }
                    }
                    catch { /* best-effort */ }
                }));
            }
            catch (InvalidOperationException)
            {
                SafeShowOverlayHost();
            }

            // Aggiornamento periodico delle statistiche (anche se l'engine non notifica)
            _statsTimer.Tick += (_, __) =>
            {
                try
                {
                    if (_engine != null)
                    {
                        UpdateTime(_engine.PositionSeconds);

                        // >>> AGGIUNGI DA QUI
                        bool isBsNow = _engine.IsBitstreamActive(); // solo questa API
                        if (isBsNow != _lastIsBsLogged)
                        {
                            _lastIsBsLogged = isBsNow;
                            Debug.WriteLine($"[Cinecore] IsBitstreamActive → {(isBsNow ? "Bitstream" : "PCM")} @ {DateTime.Now:HH:mm:ss.fff}");
                        }
                        // <<< A QUI
                    }
                }
                catch { /* best-effort */ }
            };
        }

        private void BringOverlaysToFront()
        {
            _videoHost.SendToBack();

            if (_splash.Visible) _splash.BringToFront();
            if (_overlayHost != null)
            {
                if (!_overlayHost.Visible) SafeShowOverlayHost();
                _overlayHost.BringToFront();
            }

            bool modalVisible = (_settingsModal?.Visible ?? false) || (_creditsModal?.Visible ?? false);

            _infoOverlay.BringToFront();

            if (modalVisible)
            {
                _hud.Visible = false;
                if (_settingsModal?.Visible == true) _settingsModal.BringToFront();
                if (_creditsModal?.Visible == true) _creditsModal.BringToFront();
            }
            else
            {
                if (_settingsModal != null) _settingsModal.SendToBack();
                if (_creditsModal != null) _creditsModal.SendToBack();

                // HUD solo quando esiste un renderer video (display control)
                bool showHud = _engine != null && !_splash.Visible && _engine.HasDisplayControl();
                _hud.Visible = showHud;
                if (showHud) _hud.BringToFront();
            }

            // In solo-audio: se i meters sono attivi, portali davanti; altrimenti il banner PNG
            if (_audioMeters?.Visible == true)
            {
                _hud.Visible = false;
                _audioMeters.BringToFront();
            }
            else if (_audioOnlyBanner.Visible)
            {
                _hud.Visible = false;
                _audioOnlyBanner.BringToFront();
            }
        }

        private void ShowSettingsModal()
        {
            UseOverlayInline(false);
            _settingsModal.Tag = false;

            _settingsModal.SyncFromState(_targetFps, _enableUpscaling, _preferBitstreamUi);
            _settingsModal.Visible = true;
            _settingsModal.BringToFront();

            _settingsModal.EnsureHostsLoaded();
            BeginInvoke(new Action(() => _settingsModal.FocusApply()));
            BringOverlaysToFront();
        }

        private void ShowCreditsModal()
        {
            UseOverlayInline(false);
            _creditsModal.Tag = false;

            _creditsModal.Visible = true;
            _creditsModal.BringToFront();
            BringOverlaysToFront();
        }

        private void PlayerForm_KeyDown(object? sender, KeyEventArgs e)
        {
            if (_loading.Visible) { e.Handled = true; return; }
            if (e.KeyCode == Keys.F) { ToggleFullscreen(); _hud.Pulse(HudOverlay.ButtonId.Fullscreen); e.Handled = true; }
            else if (e.KeyCode == Keys.Left) { SeekRelative(-10); _hud.Pulse(HudOverlay.ButtonId.Back10); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { SeekRelative(10); _hud.Pulse(HudOverlay.ButtonId.Fwd10); e.Handled = true; }
            else if (e.KeyCode == Keys.PageUp) { SeekChapter(+1); _hud.Pulse(HudOverlay.ButtonId.NextChapter); e.Handled = true; }
            else if (e.KeyCode == Keys.PageDown) { SeekChapter(-1); _hud.Pulse(HudOverlay.ButtonId.PrevChapter); e.Handled = true; }
            else if (e.KeyCode == Keys.O) { OpenFile(); _hud.Pulse(HudOverlay.ButtonId.Open); e.Handled = true; }
        }

        private void EnsureActive()
        {
            try { if (!Focused) Activate(); } catch { }
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

        // ===== Overlay "Audio Only" =====
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

            protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var key = this.FindForm()?.TransparencyKey ?? Color.Black;
                e.Graphics.Clear(key);
            }

            protected override void OnCreateControl()
            {
                base.OnCreateControl();
                var candidates = new[]
                {
                    ImagePath,
                    Path.Combine(AppContext.BaseDirectory, "Assets", "AudioOnly.png"),
                    Path.Combine(AppContext.BaseDirectory, "Assets", "audioOnly.jpg"),
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
                    Dbg.Warn("AudioOnlyOverlay: PNG non trovato.");
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;

                if (_png != null)
                {
                    var maxW = (int)(Width * 0.4);
                    var maxH = (int)(Height * 0.4);
                    double s = Math.Min(maxW / (double)_png.Width, maxH / (double)_png.Height);
                    int w = Math.Max(1, (int)Math.Round(_png.Width * s));
                    int h = Math.Max(1, (int)Math.Round(_png.Height * s));
                    int x = (Width - w) / 2;
                    int y = (Height - h) / 2 - 24;

                    using (var glow = new SolidBrush(Color.FromArgb(46, 0, 0, 0)))
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
                if (disposing) { _png?.Dispose(); }
                base.Dispose(disposing);
            }
        }

        private void BuildMenu()
        {
            _menu = new ContextMenuStrip();

            var mOpen = new ToolStripMenuItem("Apri…", null, (_, __) => OpenFile());
            var mPlay = new ToolStripMenuItem("Play/Pausa", null, (_, __) => TogglePlayPause());
            var mStop = new ToolStripMenuItem("Rimuovi", null, (_, __) => SafeStop());
            var mFull = new ToolStripMenuItem("Schermo intero", null, (_, __) => ToggleFullscreen());

            var mHdr = new ToolStripMenuItem("Immagine (HDR)");
            var hAuto = new ToolStripMenuItem("Auto (usa madVR/MPCVR su file HDR)", null, (_, __) => { _hdr = HDRMode.Auto; _lblStatus.Text = "HDR: Auto"; ReopenSame(); }) { Checked = true };
            var hOff = new ToolStripMenuItem("Forza SDR (tone-map HDR→SDR con madVR/MPCVR)", null, (_, __) => { _hdr = HDRMode.Off; _lblStatus.Text = "HDR: Forza SDR"; ReopenSame(); });
            mHdr.DropDownItems.AddRange(new[] { hAuto, hOff });
            mHdr.DropDownOpening += (_, __) => { hAuto.Checked = _hdr == HDRMode.Auto; hOff.Checked = _hdr == HDRMode.Off; };

            var m3D = new ToolStripMenuItem("3D");
            var m3Off = new ToolStripMenuItem("Off", null, (_, __) => { _stereo = Stereo3DMode.None; _engine?.SetStereo3D(_stereo); _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle); }) { Checked = true };
            var m3SBS = new ToolStripMenuItem("SBS → 2D (metà sinistra)", null, (_, __) => { _stereo = Stereo3DMode.SBS; _engine?.SetStereo3D(_stereo); _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle); });
            var m3TAB = new ToolStripMenuItem("TAB → 2D (metà superiore)", null, (_, __) => { _stereo = Stereo3DMode.TAB; _engine?.SetStereo3D(_stereo); _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle); });
            m3D.DropDownItems.AddRange(new[] { m3Off, m3SBS, m3TAB });

            var mUpscale = new ToolStripMenuItem("Upscaling (consenti oltre nativo)") { CheckOnClick = true, Checked = _enableUpscaling };
            var mRefresh = new ToolStripMenuItem("Frequenza monitor");

            var rAuto = new ToolStripMenuItem("Adatta allo schermo (non cambiare)", null, (_, __) =>
            {
                _targetFps = 0;
                _lblStatus.Text = "Refresh: Adatta allo schermo";
                try { _refresh.RestoreIfChanged(); } catch { }
                ReopenSame();
            });

            var r24 = new ToolStripMenuItem("Forza 23/24p", null, (_, __) =>
            {
                _targetFps = 24;
                _lblStatus.Text = "Refresh: 23/24p";
                ReopenSame();
            });

            var r60 = new ToolStripMenuItem("Forza 59/60p", null, (_, __) =>
            {
                _targetFps = 60;
                _lblStatus.Text = "Refresh: 59/60p";
                ReopenSame();
            });

            mRefresh.DropDownItems.AddRange(new[] { rAuto, r24, r60 });
            mRefresh.DropDownOpening += (_, __) =>
            {
                rAuto.Checked = _targetFps == 0;
                r24.Checked = _targetFps == 24;
                r60.Checked = _targetFps == 60;
            };

            mUpscale.Click += (_, __) =>
            {
                _enableUpscaling = mUpscale.Checked;

                try { _engine?.SetUpscaling(_enableUpscaling); } catch { }
                _lblStatus.Text = "Upscaling: " + (_enableUpscaling ? "ON" : "OFF");

                if (_enableUpscaling && _manualRendererChoice != VRChoice.MADVR)
                {
                    _manualRendererChoice = VRChoice.MADVR;
                    _lblStatus.Text += " • Renderer → madVR";
                    ReopenSame();
                }
                else
                {
                    _hud.ShowOnce(1200);
                }
            };

            _menu.Opening += (_, __) => { mUpscale.Checked = _enableUpscaling; };

            _mAudioLang = new ToolStripMenuItem("Lingua audio");
            _mAudioLang.DropDownOpening += (_, __) => PopulateAudioLangMenu();

            _mSubtitles = new ToolStripMenuItem("Sottotitoli");
            _mSubtitles.DropDownOpening += (_, __) => PopulateSubtitlesMenu();

            var mChapters = new ToolStripMenuItem("Capitoli…", null, (_, __) => ShowChaptersMenu());

            var mShowInfo = new ToolStripMenuItem("Info overlay ON/OFF", null, (_, __) => { _infoOverlay.Visible = !_infoOverlay.Visible; });

            _mAudioOut = new ToolStripMenuItem("Uscita audio");
            _mAudioOut.DropDownOpening += (_, __) => PopulateAudioOutputMenu(_mAudioOut);
            PopulateAudioOutputMenu(_mAudioOut);

            var mRenderer = new ToolStripMenuItem("Renderer video");
            void SetRenderer(VRChoice? c)
            {
                _manualRendererChoice = c;
                _lblStatus.Text = c.HasValue ? $"Renderer video: {c}" : "Renderer video: Auto";

                if (c.HasValue && c.Value != VRChoice.MADVR)
                {
                    _enableUpscaling = false;
                    try { _engine?.SetUpscaling(false); } catch { }
                    mUpscale.Checked = false;
                }

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
            };

            _menu.Items.AddRange(new ToolStripItem[]
            {
                mOpen, new ToolStripSeparator(), mPlay, mStop, mFull, new ToolStripSeparator(),
                mHdr, mRefresh, mUpscale, m3D, _mAudioLang, _mSubtitles, _mAudioOut, mRenderer, new ToolStripSeparator(),
                mChapters, mShowInfo
            });
        }

        // ======= Uscita audio raggruppata =======
        private void PopulateAudioOutputMenu(ToolStripMenuItem root)
        {
            root.DropDownItems.Clear();

            List<DsDevice> all;
            try { all = DsDevice.GetDevicesOfCat(FilterCategory.AudioRendererCategory).ToList(); }
            catch { all = new List<DsDevice>(); }

            var grpDefault = new ToolStripMenuItem("Predefinito di sistema");
            var grpWasapi = new ToolStripMenuItem("WASAPI");
            var grpDs = new ToolStripMenuItem("DirectSound");
            var grpMpc = new ToolStripMenuItem("MPC Audio Renderer");

            foreach (var dev in all.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new ToolStripMenuItem(dev.Name)
                {
                    Checked = string.Equals(dev.Name, _selectedAudioRendererName, StringComparison.OrdinalIgnoreCase)
                };
                string captured = dev.Name;
                item.Click += (_, __) =>
                {
                    _selectedAudioRendererName = captured;
                    _selectedRendererLooksHdmi = LooksHdmi(captured);
                    _lblStatus.Text = "Uscita audio: " + captured;
                    ReopenSame();
                };

                var nl = dev.Name.ToLowerInvariant();
                if (nl.Contains("mpc audio renderer")) grpMpc.DropDownItems.Add(item);
                else if (nl.Contains("wasapi")) grpWasapi.DropDownItems.Add(item);
                else if (nl.Contains("directsound")) grpDs.DropDownItems.Add(item);
                else grpDefault.DropDownItems.Add(item);
            }

            void sortItems(ToolStripMenuItem m)
            {
                var list = m.DropDownItems.OfType<ToolStripMenuItem>()
                    .OrderBy(i => i.Text, StringComparer.CurrentCultureIgnoreCase).ToList();
                m.DropDownItems.Clear();
                foreach (var it in list) m.DropDownItems.Add(it);
            }
            sortItems(grpDefault);
            sortItems(grpWasapi);
            sortItems(grpDs);
            sortItems(grpMpc);

            if (grpDefault.DropDownItems.Count == 0)
            {
                var miDefault = new ToolStripMenuItem("Usa dispositivo predefinito") { Checked = string.IsNullOrWhiteSpace(_selectedAudioRendererName) };
                miDefault.Click += (_, __) =>
                {
                    _selectedAudioRendererName = null;
                    _selectedRendererLooksHdmi = false;
                    _lblStatus.Text = "Uscita audio: predefinito di sistema";
                    ReopenSame();
                };
                grpDefault.DropDownItems.Add(miDefault);
            }

            if (grpDefault.DropDownItems.Count > 0) root.DropDownItems.Add(grpDefault);
            if (grpWasapi.DropDownItems.Count > 0) root.DropDownItems.Add(grpWasapi);
            if (grpDs.DropDownItems.Count > 0) root.DropDownItems.Add(grpDs);
            if (grpMpc.DropDownItems.Count > 0) root.DropDownItems.Add(grpMpc);
        }

        private static bool LooksHdmi(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            string[] hdmi = { "hdmi", "display audio", "avr", "denon", "marantz", "onkyo", "yamaha", "nvidia high definition audio", "intel(r) display audio", "amd high definition audio" };
            return hdmi.Any(n.Contains);
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
                    if (_info != null)
                    {
                        var r = _manualRendererChoice ?? (_info.IsHdr ? ORDER_HDR.First() : ORDER_SDR.First());
                        UpdateInfoOverlay(r, _info.IsHdr);
                    }
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

        private void OpenFile()
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "Media|*.mkv;*.mp4;*.m2ts;*.ts;*.mov;*.avi;*.wmv;*.webm;*.flac;*.mp3;*.mka;*.aac;*.ogg;*.wav;*.wma;*.m4a;*.opus|Tutti i file|*.*"
            };
            if (ofd.ShowDialog() != DialogResult.OK) return;
            OpenPath(ofd.FileName);
        }

        private volatile bool _stopping;
        private VRChoice? _manualRendererChoice = VRChoice.MADVR;

        private void OpenPath(string path, double resume = 0, bool startPaused = false)
        {
            SafeStop();
            SkipLoadingIfActive();

            _currentPath = path;
            _bitstreamNow = false;
            _bitstreamLastTrue = DateTime.MinValue;

            try { _info = MediaProbe.Probe(path); }
            catch (Exception ex) { _lblStatus.Text = "Probe fallito: " + ex.Message; _info = null; }

            bool hasVideo = _info?.HasVideo == true || LooksLikeVideoByExt(path);
            bool fileHdr = _info?.IsHdr == true;
            bool hdmi = _selectedRendererLooksHdmi;
            bool passCandidate = _info != null && MediaProbe.IsPassthroughCandidate(_info.AudioCodec);

            bool wantBitstream = (_preferBitstreamUi && hdmi && passCandidate);

            var order =
                _manualRendererChoice.HasValue
                ? new[] { _manualRendererChoice.Value }
                : (fileHdr ? ORDER_HDR : ORDER_SDR);

            Dbg.Log($"OpenPath '{path}', HDR_File={fileHdr}, UI_HDR={_hdr}, hasVideo={hasVideo}, wantBitstream={wantBitstream}, order=[{string.Join(",", order)}]");

            foreach (var choice in order)
            {
                try
                {
                    _stopping = false;
                    _engine = new DirectShowUnifiedEngine(
                        preferBitstream: wantBitstream,
                        preferredRendererName: _selectedAudioRendererName,
                        choice: choice,
                        fileIsHdr: fileHdr,
                        srcAudioCodec: _info?.AudioCodec ?? AVCodecID.AV_CODEC_ID_NONE);

                    _engineStatusHandler = s => { if (_stopping) return; BeginInvoke(new Action(() => _lblStatus.Text = s)); };
                    _engineProgressHandler = s => { if (_stopping) return; BeginInvoke(new Action(() => UpdateTime(s))); };
                    _engineUpdateHandler = () =>
                    {
                        if (_stopping) return;
                        if (IsHandleCreated) BeginInvoke(new Action(() =>
                        {
                            _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);
                            SyncOverlayToVideoRect();
                            if (_info != null && _engine != null)
                            {
                                var chosen = _manualRendererChoice ?? (fileHdr ? ORDER_HDR.First() : ORDER_SDR.First());
                                UpdateInfoOverlay(chosen, fileHdr);
                            }
                            BringOverlaysToFront();
                        }));
                    };

                    _engine.OnStatus += _engineStatusHandler;
                    _engine.OnProgressSeconds += _engineProgressHandler;
                    _engine.BindUpdateCallback(_engineUpdateHandler);

                    _engine.OnBitstreamChanged += b =>
                    {
                        _bitstreamNow = b;
                        BeginInvoke(new Action(() =>
                        {
                            // refresh rapido overlay
                            if (_info != null)
                            {
                                var chosen = _manualRendererChoice ?? (_info.IsHdr ? ORDER_HDR.First() : ORDER_SDR.First());
                                UpdateInfoOverlay(chosen, _info.IsHdr);
                            }

                            // se sei in audio-only, abilita/disabilita meters live
                            if (!(_engine?.HasDisplayControl() ?? false))
                            {
                                if (b) { StopAudioMeters(); _audioOnlyBanner.Visible = true; }
                                else { StartAudioMetersIfPossible(); }
                            }

                            // volume slider: in bitstream resta al 100%
                            if (b) { try { _engine?.SetVolume(1f); } catch { } try { _hud?.SetExternalVolume(1f); } catch { } }
                        }));
                    };

                    UseOverlayInline(false); // SEMPRE host layered

                    _engine.Open(path, hasVideo);
                    try
                    {
                        bool isBsInit = _engine.IsBitstreamActive();
                        _lastIsBsLogged = isBsInit; // baseline per evitare doppio log al primo Tick
                        Debug.WriteLine($"[Cinecore] (init) IsBitstreamActive = {(isBsInit ? "Bitstream" : "PCM")} @ {DateTime.Now:HH:mm:ss.fff}");
                    }
                    catch { /* best-effort */ }
                    _vPrevBytes = 0;
                    _vPrevWhen = DateTime.MinValue;
                    _videoBitrateNowKbps = 0;

                    _avgLastPublish = DateTime.MinValue;
                    _avgLastTs = DateTime.MinValue;
                    _avgAudioBitSec = 0;
                    _avgVideoBitSec = 0;
                    _avgDurSec = 0;
                    _audioAvgLiveKbps = 0;
                    _videoAvgLiveKbps = 0;

                    _duration = _engine.DurationSeconds > 0 ? _engine.DurationSeconds : (_info?.Duration ?? 0);

                    if (resume > 0 && _duration > 0)
                    {
                        try { _engine.PositionSeconds = Math.Min(resume, Math.Max(0.01, _duration)); } catch { }
                    }

                    bool hasDisplay = _engine.HasDisplayControl();
                    if (!hasDisplay)
                    {
                        StartAudioMetersIfPossible();
                    }
                    else
                    {
                        StopAudioMeters();
                        _audioOnlyBanner.Visible = false;
                    }

                    _duration = _engine.DurationSeconds > 0 ? _engine.DurationSeconds : (_info?.Duration ?? 0);

                    _splash.Visible = false;
                    BringOverlaysToFront();

                    try { _thumb.Open(path); } catch { }
                    try
                    {
                        // Sampler FFmpeg: solo file locali (evita doppio traffico su HTTP/SMB)
                        bool isLocal = true;
                        if (Uri.TryCreate(path, UriKind.Absolute, out var u)) isLocal = u.IsFile;
                        if (isLocal)
                        {
                            _pktRate.Open(path);
                            _lastPktSample = DateTime.MinValue;
                        }
                    }
                    catch { /* best-effort */ }

                    _engine.SetStereo3D(_stereo);
                    _engine.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);

                    SafeShowOverlayHost();
                    SyncOverlayToVideoRect();
                    BringOverlaysToFront();

                    AutoSelectDefaultStreams();

                    UpdateInfoOverlay(choice, fileHdr);

                    _hud.TimelineVisible = hasDisplay; // niente barra in solo-audio
                    _hud.Visible = hasDisplay;
                    if (hasDisplay) _hud.ShowOnce(2000);

                    _paused = startPaused;
                    try { if (!startPaused) _engine.Play(); else _engine.Pause(); } catch { }

                    ApplyVolume(1f);

                    if (FormBorderStyle != FormBorderStyle.None) ToggleFullscreen();

                    var t = new System.Windows.Forms.Timer { Interval = 300 };
                    t.Tick += (_, __) =>
                    {
                        try
                        {
                            _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);
                            SyncOverlayToVideoRect();
                            BringOverlaysToFront();
                            if (hasDisplay) _hud.ShowOnce(1000);
                        }
                        catch { }
                        finally { t.Stop(); t.Dispose(); }
                    };
                    t.Start();

                    bool okDisplay = _engine.HasDisplayControl();
                    if (!hasVideo || okDisplay)
                    {
                        string tag = fileHdr ? "HDR" : "SDR";
                        _lblStatus.Text = (!hasVideo)
                            ? "Riproduzione (solo audio)"
                            : $"Riproduzione ({choice} • {tag})";
                        return;
                    }

                    throw new Exception("Renderer non pronto (nessun display control) → fallback");
                }
                catch (Exception ex)
                {
                    Dbg.Warn($"OpenPath: renderer {choice} EX: " + ex.Message);
                    try { _engine?.Dispose(); } catch { }
                    _engine = null;

                    if (_manualRendererChoice == VideoRendererChoice.MADVR &&
                        (ex.Message?.IndexOf("madVR non trovato", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        _lblStatus.Text = "madVR non installato. Esegui 'install.bat' come Amministratore nella cartella di madVR, poi riprova.";
                    }
                }
            }

            _lblStatus.Text = "Impossibile presentare il video con i renderer selezionati";
        }

        private static bool LooksLikeVideoByExt(string path)
        {
            var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return new[] { ".mkv", ".mp4", ".m2ts", ".ts", ".mov", ".avi", ".wmv", ".webm" }.Contains(ext);
        }

        private void AutoSelectDefaultStreams()
        {
            if (_engine == null) return;
            try
            {
                var streams = _engine.EnumerateStreams().ToList();

                var selAudio = streams.FirstOrDefault(s => s.IsAudio && s.Selected) ??
                               streams.FirstOrDefault(s => s.IsAudio);
                if (selAudio != null) _engine.EnableByGlobalIndex(selAudio.GlobalIndex);

                var selSub = streams.FirstOrDefault(s => s.IsSubtitle && s.Selected);
                if (selSub == null)
                {
                    static bool Match(string? name, params string[] keys)
                    {
                        var n = (name ?? "").ToLowerInvariant();
                        return keys.Any(k => n.Contains(k));
                    }
                    selSub =
                        streams.FirstOrDefault(s => s.IsSubtitle && Match(s.Name, "ita", "ital", "italian")) ??
                        streams.FirstOrDefault(s => s.IsSubtitle && Match(s.Name, "eng", "english", "en ")) ??
                        streams.FirstOrDefault(s => s.IsSubtitle);
                    if (selSub != null) _engine.EnableByGlobalIndex(selSub.GlobalIndex);
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("AutoSelectDefaultStreams: " + ex.Message);
            }
        }

        private static (int W, int H, string Label) NormalizeViewport(int w, int h)
        {
            if (w <= 0 || h <= 0) return (w, h, $"{w}x{h}");
            var cand = new (int W, int H, string Label)[]
            {
                (3840,2160,"3840x2160"), (4096,2160,"4096x2160"), (2560,1440,"2560x1440"),
                (1920,1080,"1920x1080"), (1600,900,"1600x900"), (1280,720,"1280x720"),
            };
            foreach (var c in cand)
            {
                double dw = Math.Abs(w - c.W) / (double)c.W;
                double dh = Math.Abs(h - c.H) / (double)c.H;
                if (dw <= 0.02 && dh <= 0.02) return c;
            }
            return (w, h, $"{w}x{h}");
        }

        private static string FmtKbps(int kbps) => kbps > 0 ? $"{kbps:n0} kbps" : "n/d";

        // ======= INFO OVERLAY =======
        private void UpdateInfoOverlay(VRChoice renderer, bool fileHdr)
        {
            if (_engine == null) return;

            // Video OUT (negoziato)
            var (w, h, sub) = _engine.GetNegotiatedVideoFormat();
            int vw = _videoHost.ClientSize.Width;
            int vh = _videoHost.ClientSize.Height;
            var vnorm = NormalizeViewport(vw, vh);
            string outStr = (w > 0 ? $"{w}x{h}" : "n/d") + $" • {sub}";
            if (vw > 0 && vh > 0) outStr += $"  (viewport {vnorm.Label})";

            // Stima bitrate medio dal container (fallback)
            int avgContainerKbps = 0;
            try
            {
                if (!string.IsNullOrEmpty(_currentPath) && File.Exists(_currentPath) && _duration > 1)
                {
                    var fi = new FileInfo(_currentPath);
                    avgContainerKbps = (int)Math.Round((fi.Length * 8.0 / 1000.0) / _duration);
                }
            }
            catch { }

            // ===== Audio da LAV Audio (IN/OUT + bitstream + kbpsNow) =====
            var selAudio = _engine.EnumerateStreams().FirstOrDefault(s => s.IsAudio && s.Selected);
            string selName = selAudio?.Name ?? "";
            var lav = GetLavAudioIODetails(selName);
            bool bitstream = IsBitstream();

            // "ora" audio: SOLO misura live (niente fallback su probe per evitare numeri statici)
            int audioNowKbps = _audioBitrateNowKbps > 0
                ? _audioBitrateNowKbps
                : ParseKbpsFromName(selName);

            // === MEDIE: usa la media live pubblicata ogni 10s; se 0, fallback ai metadata ===
            int audioAvgKbps = (_audioAvgLiveKbps > 0) ? (int)Math.Round(_audioAvgLiveKbps) : 0;
            int videoAvgKbps = (_videoAvgLiveKbps > 0) ? (int)Math.Round(_videoAvgLiveKbps) : 0;

            if (audioAvgKbps <= 0)
            {
                if (lav.AudioNowKbps > 0) audioAvgKbps = lav.AudioNowKbps;
                if (audioAvgKbps <= 0 && !string.IsNullOrWhiteSpace(selName))
                    audioAvgKbps = ParseKbpsFromName(selName);
                if (audioAvgKbps <= 0)
                    audioAvgKbps = ProbeAudioAvgKbps();
                if (audioAvgKbps <= 0 && avgContainerKbps > 0)
                    audioAvgKbps = (int)(avgContainerKbps * 0.30);
            }

            if (videoAvgKbps <= 0)
            {
                if (avgContainerKbps > 0 && audioAvgKbps > 0)
                    videoAvgKbps = Math.Max(0, avgContainerKbps - audioAvgKbps);
                else if (avgContainerKbps > 0)
                    videoAvgKbps = (int)(avgContainerKbps * 0.70);
            }

            // Se non abbiamo ancora campioni live, usa i fallback esistenti
            if (audioAvgKbps <= 0)
            {
                // 1) dal graph corrente (PCM calcolato o payload stimato da LAV)
                if (lav.AudioNowKbps > 0) audioAvgKbps = lav.AudioNowKbps;

                // 2) dal nome traccia ("xxx kb/s")
                if (audioAvgKbps <= 0 && !string.IsNullOrWhiteSpace(selName))
                    audioAvgKbps = ParseKbpsFromName(selName);

                // 3) dal probe (se disponibile)
                if (audioAvgKbps <= 0)
                    audioAvgKbps = ProbeAudioAvgKbps();

                // 4) heuristico dal container
                if (audioAvgKbps <= 0 && avgContainerKbps > 0)
                    audioAvgKbps = (int)(avgContainerKbps * 0.30);
            }

            if (videoAvgKbps <= 0)
            {
                if (avgContainerKbps > 0 && audioAvgKbps > 0)
                    videoAvgKbps = Math.Max(0, avgContainerKbps - audioAvgKbps);
                else if (avgContainerKbps > 0)
                    videoAvgKbps = (int)(avgContainerKbps * 0.70);
            }

            // Video "ora": calcolato altrove come residuo container-now – audio-now
            int videoNowKbps = _videoBitrateNowKbps > 0 ? _videoBitrateNowKbps : 0;

            // Audio IN/OUT prettificato
            string audioIn = !string.IsNullOrWhiteSpace(lav.InDetail) && lav.InDetail != "n/d"
                ? lav.InDetail
                : PrettyAudioInFromProbe(_info);

            var s = new InfoOverlay.Stats
            {
                Title = Path.GetFileName(_currentPath ?? "") ?? "—",

                VideoIn = _info != null
                          ? $"{_info.Width}x{_info.Height} • {CodecName(_info.VideoCodec)} • {(_info.VideoBits > 0 ? _info.VideoBits + "-bit" : "8-bit?")}"
                          : "n/d",
                VideoOut = outStr,
                VideoCodec = _info != null ? CodecName(_info.VideoCodec) : "n/d",
                VideoPrimaries = _info != null ? PrimName(_info.Primaries) : "n/d",
                VideoTransfer = _info != null ? TrcName(_info.Transfer) : "n/d",

                VideoBitrateNow = videoNowKbps > 0 ? FmtKbps(videoNowKbps) : "n/d",
                VideoBitrateAvg = videoAvgKbps > 0 ? FmtKbps(videoAvgKbps) : "n/d",

                AudioIn = string.IsNullOrWhiteSpace(audioIn) ? "n/d" : audioIn,
                AudioOut = lav.OutDetail,
                AudioBitrateNow = audioNowKbps > 0
                ? (FmtKbps(audioNowKbps) + (bitstream ? " (payload)" : " (PCM)"))
                : "n/d",
                AudioBitrateAvg = audioAvgKbps > 0 ? FmtKbps(audioAvgKbps) : "n/d",

                Renderer = renderer.ToString() + ((_enableUpscaling && renderer == VRChoice.MADVR) ? " (madVR upscaler)" : ""),
                HdrMode = fileHdr ? (_hdr == HDRMode.Auto ? "HDR (auto)" : "SDR (tone-map)") : "SDR",
                Upscaling = _enableUpscaling && renderer == VRChoice.MADVR,
                Bitstream = bitstream,
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
        }

        // --- helper locali ---
        private static string PrettyChannels(int ch)
        {
            return ch switch
            {
                1 => "1.0",
                2 => "2.0",
                3 => "2.1",
                4 => "4.0",
                5 => "4.1",
                6 => "5.1",
                7 => "6.1",
                8 => "7.1",
                _ => $"{ch}ch"
            };
        }

        private static string LocalCodecName(AVCodecID id) => id switch
        {
            AVCodecID.AV_CODEC_ID_TRUEHD => "Dolby TrueHD",
            AVCodecID.AV_CODEC_ID_EAC3 => "Dolby Digital Plus",
            AVCodecID.AV_CODEC_ID_AC3 => "Dolby Digital",
            AVCodecID.AV_CODEC_ID_DTS => "DTS",
            AVCodecID.AV_CODEC_ID_FLAC => "FLAC",
            AVCodecID.AV_CODEC_ID_AAC => "AAC",
            _ => id.ToString().Replace("AV_CODEC_ID_", "")
        };

        private string PrettyAudioInFromProbe(MediaProbe.Result? r)
        {
            if (r == null || r.AudioCodec == 0) return "n/d";
            string c = LocalCodecName(r.AudioCodec);               // ← non dipende da CodecName(...) locale
            string ch = r.AudioChannels > 0 ? " • " + PrettyChannels(r.AudioChannels) : "";
            string sr = r.AudioRate > 0 ? $" • {r.AudioRate / 1000.0:0.#} kHz" : ""; // ← AudioRate, non AudioSampleRate
            return c + ch + sr;
        }
        private bool TryGetLavInAvgBytesPerSec(out int avgBps)
        {
            avgBps = 0;
            try
            {
                if (!TryGetFilterGraph(out var fg) || fg == null) return false;
                if (!TryFindFilter(fg, "LAV Audio", out var lav) || lav == null) return false;

                if (lav.EnumPins(out IEnumPins? ep) != 0 || ep == null) return false;
                var pins = new IPin[1];

                while (ep.Next(1, pins, IntPtr.Zero) == 0)
                {
                    var p = pins[0];
                    p.QueryPinInfo(out var pi);
                    try
                    {
                        if (pi.dir == PinDirection.Input)
                        {
                            var mt = new AMMediaType();
                            if (p.ConnectionMediaType(mt) == 0)
                            {
                                try
                                {
                                    if (mt.formatType == FormatType.WaveEx && mt.formatPtr != IntPtr.Zero)
                                    {
                                        var wfx = Marshal.PtrToStructure<WaveFormatEx>(mt.formatPtr);
                                        if (wfx.nAvgBytesPerSec > 0)
                                        {
                                            avgBps = (int)wfx.nAvgBytesPerSec;
                                            return true;
                                        }
                                    }
                                }
                                finally { DsUtils.FreeAMMediaType(mt); }
                            }
                        }
                    }
                    finally
                    {
                        if (pi.filter != null) Marshal.ReleaseComObject(pi.filter);
                        Marshal.ReleaseComObject(p);
                    }
                }
            }
            catch { }
            return false;
        }

        // ======= LAV Audio I/O Details (unica fonte per overlay audio) =======
        private (string InDetail, string OutDetail, bool Bitstream, int AudioNowKbps) GetLavAudioIODetails(string? selectedStreamName)
        {
            string inStr = "n/d";
            string outStr = "n/d";
            bool bitstream = IsBitstream(); // unica fonte di verità
            int kbpsNow = 0;

            try
            {
                // 1) prova a ottenere direttamente il filtro LAV Audio dall’engine
                IBaseFilter? lavAudio = null;
                try
                {
                    var t = _engine?.GetType();
                    if (t != null)
                    {
                        var pLav = t.GetProperty("LavAudioFilter",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        if (pLav?.GetValue(_engine) is IBaseFilter lav) lavAudio = lav;
                    }
                }
                catch { /* best-effort */ }

                // 2) fallback: cerca "LAV Audio" nel graph
                IFilterGraph2? fg = null;
                if (lavAudio == null)
                {
                    if (!TryGetFilterGraph(out fg) || fg == null) return (inStr, outStr, bitstream, kbpsNow);
                    if (!TryFindFilter(fg, "LAV Audio", out lavAudio) || lavAudio == null) return (inStr, outStr, bitstream, kbpsNow);
                }

                // 3) pin in/out connessi
                if (!TryGetLavPinsConnected(lavAudio, out var pinIn, out var pinOut))
                    return (inStr, outStr, bitstream, kbpsNow);

                AMMediaType mtIn = new AMMediaType();
                AMMediaType mtOut = new AMMediaType();
                AMMediaType mtDown = new AMMediaType();

                try
                {
                    // IN (a LAV)
                    if (pinIn != null && pinIn.ConnectionMediaType(mtIn) == 0)
                        inStr = PrettyFromIn(mtIn, selectedStreamName);

                    // OUT (da LAV) e DOWNSTREAM (ingresso renderer)
                    string detail = "n/d";
                    AMMediaType? mtChosen = null;

                    // OUT immediato
                    bool haveOut = (pinOut != null && pinOut.ConnectionMediaType(mtOut) == 0);
                    if (haveOut)
                    {
                        (_, string detailOut) = PrettyOutFromLav(mtOut, selectedStreamName);
                        detail = detailOut;
                        mtChosen = mtOut;
                    }

                    // IN del renderer (downstream), preferisci quello più specifico
                    if (pinOut != null && pinOut.ConnectedTo(out IPin? rIn) == 0 && rIn != null)
                    {
                        try
                        {
                            if (rIn.ConnectionMediaType(mtDown) == 0)
                            {
                                (_, string detailDown) = PrettyOutFromLav(mtDown, selectedStreamName);

                                // scegli il più specifico tra detail e detailDown
                                if (string.Equals(detail, "n/d", StringComparison.OrdinalIgnoreCase) ||
                                    PreferMoreSpecific(detailDown, detail) == detailDown)
                                {
                                    detail = detailDown;
                                    mtChosen = mtDown;
                                }
                            }
                        }
                        finally { Marshal.ReleaseComObject(rIn); }
                    }

                    // Componi OutDetail coerente col flag dell’engine
                    if (bitstream)
                    {
                        string pretty = detail;
                        if (string.IsNullOrWhiteSpace(pretty) || pretty.Equals("n/d", StringComparison.OrdinalIgnoreCase))
                            pretty = "IEC61937";
                        if (!pretty.StartsWith("Bitstream", StringComparison.OrdinalIgnoreCase))
                            outStr = $"Bitstream ({pretty})";
                        else
                            outStr = pretty;
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(detail) &&
                            detail.StartsWith("PCM", StringComparison.OrdinalIgnoreCase))
                        {
                            outStr = detail;
                        }
                        else if (mtChosen != null)
                        {
                            // costruisci stringa PCM da wave format
                            var (_, rate, ch, bps, vbits) = ReadWave(mtChosen);
                            int usedBits = vbits > 0 ? vbits : bps;
                            string rateStr = rate > 0 ? (rate / 1000.0).ToString("0.0") + " kHz" : "n/d";
                            string chStr = ch > 0 ? $"{ch}ch" : "n/d";
                            string bitStr = usedBits > 0 ? $"{usedBits}-bit" : "n/d";
                            outStr = $"PCM {rateStr} • {bitStr} • {chStr}";
                        }
                        else
                        {
                            outStr = "PCM";
                        }
                    }

                    // ===== Bitrate "ora" =====
                    if (!bitstream && mtChosen != null)
                    {
                        // PCM/Float: throughput = rate * validBits * channels (kbps)
                        var (tag, rate, ch, bps, vbits) = ReadWave(mtChosen);
                        int usedBits = vbits > 0 ? vbits : bps;
                        if (tag == 3 && usedBits <= 0) usedBits = 32; // IEEE_FLOAT → 32-bit
                        if (rate > 0 && ch > 0 && usedBits > 0)
                            kbpsNow = (int)Math.Round(rate * usedBits * ch / 1000.0);
                    }
                    else
                    {
                        // BITSTREAM: stima il payload (non il trasporto IEC61937)
                        kbpsNow = ProbeAudioAvgKbps();

                        if (kbpsNow <= 0)
                        {
                            // se è AC-3/E-AC3/DTS core prova nAvgBytesPerSec dell’IN di LAV
                            bool likelyCore =
                                (inStr.IndexOf("Dolby Digital Plus", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                (inStr.IndexOf("Dolby Digital", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                (inStr.IndexOf("DTS-HD", StringComparison.OrdinalIgnoreCase) < 0 &&
                                 inStr.IndexOf("DTS", StringComparison.OrdinalIgnoreCase) >= 0);

                            if (likelyCore && TryGetLavInAvgBytesPerSec(out int avgBps) && avgBps > 0)
                                kbpsNow = (int)Math.Round(avgBps * 8 / 1000.0);
                        }

                        if (kbpsNow <= 0)
                            kbpsNow = ParseKbpsFromName(selectedStreamName);
                    }
                }
                finally
                {
                    try { DsUtils.FreeAMMediaType(mtIn); } catch { }
                    try { DsUtils.FreeAMMediaType(mtOut); } catch { }
                    try { DsUtils.FreeAMMediaType(mtDown); } catch { }
                    try { if (pinIn != null) Marshal.ReleaseComObject(pinIn); } catch { }
                    try { if (pinOut != null) Marshal.ReleaseComObject(pinOut); } catch { }
                }
            }
            catch { /* lascia n/d */ }

            return (inStr, outStr, bitstream, kbpsNow);

            // ----------------- Helpers locali -----------------

            static bool TryGetLavPinsConnected(IBaseFilter lav, out IPin? pinIn, out IPin? pinOut)
            {
                pinIn = null; pinOut = null;
                if (lav.EnumPins(out IEnumPins? ep) != 0 || ep == null) return false;
                var pins = new IPin[1];
                while (ep.Next(1, pins, IntPtr.Zero) == 0)
                {
                    var p = pins[0];
                    p.QueryPinInfo(out var pi);
                    try
                    {
                        if (p.ConnectedTo(out IPin? other) == 0 && other != null)
                        {
                            if (pi.dir == PinDirection.Input && pinIn == null) pinIn = p;
                            if (pi.dir == PinDirection.Output && pinOut == null) pinOut = p;
                            Marshal.ReleaseComObject(other);
                            if (pinIn != null && pinOut != null) return true;
                        }
                        else
                        {
                            Marshal.ReleaseComObject(p);
                        }
                    }
                    finally
                    {
                        if (pi.filter != null) Marshal.ReleaseComObject(pi.filter);
                    }
                }
                return pinIn != null || pinOut != null;
            }

            static string PrettyFromIn(AMMediaType mtIn, string? selectedName)
            {
                string? pretty = PrettyFromWaveOrSubtype(mtIn);
                if (string.IsNullOrEmpty(pretty))
                    pretty = PrettyFromName(selectedName);

                var (tag, rate, ch, _, _) = ReadWave(mtIn);
                string rateStr = rate > 0 ? (rate / 1000.0).ToString("0.0") + " kHz" : "";
                string chStr = ch > 0 ? $"{ch}ch" : "";
                string extra = string.Join(" • ", new[] { rateStr, chStr }.Where(s => !string.IsNullOrEmpty(s)));

                return string.IsNullOrEmpty(extra) ? (pretty ?? "n/d") : $"{(pretty ?? "n/d")} • {extra}";
            }

            // Ritorna (isPcmStimato, dettaglioHuman); il flag PCM qui è solo “descrittivo”.
            static (bool isPcm, string detail) PrettyOutFromLav(AMMediaType mtOut, string? selectedName)
            {
                (ushort tag, int rate, int ch, int bps, int vbits) = ReadWave(mtOut);

                // 1) priorità a WaveEx/WaveExtensible
                var waveClass = ClassifyByWave(mtOut);
                if (waveClass.HasValue)
                {
                    bool isPcmWave = waveClass.Value.isPcm;
                    string prettyWave = waveClass.Value.pretty
                                        ?? PrettyFromWaveOrSubtype(mtOut)
                                        ?? PrettyFromName(selectedName)
                                        ?? "IEC61937";

                    if (isPcmWave)
                    {
                        int validBitsW = vbits > 0 ? vbits : bps;
                        string rateStrW = rate > 0 ? (rate / 1000.0).ToString("0.0") + " kHz" : "n/d";
                        string chStrW = ch > 0 ? $"{ch}ch" : "n/d";
                        string bitStrW = validBitsW > 0 ? $"{validBitsW}-bit" : "n/d";
                        return (true, $"PCM {rateStrW} • {bitStrW} • {chStrW}");
                    }

                    return (false, $"Bitstream ({prettyWave})");
                }

                // 2) fallback: subType/tag
                bool isPcmBySubtype = (mtOut.subType == MediaSubType.PCM || mtOut.subType == MediaSubType.IEEE_FLOAT);
                bool isPcmByTag = (tag == 1 /*PCM*/ || tag == 3 /*IEEE_FLOAT*/);

                string rateStr = rate > 0 ? (rate / 1000.0).ToString("0.0") + " kHz" : "n/d";
                string chStr = ch > 0 ? $"{ch}ch" : "n/d";
                int validBits = vbits > 0 ? vbits : bps;
                string bitStr = validBits > 0 ? $"{validBits}-bit" : "n/d";

                if (isPcmBySubtype || isPcmByTag)
                    return (true, $"PCM {rateStr} • {bitStr} • {chStr}");

                string pretty = PrettyFromWaveOrSubtype(mtOut) ?? PrettyFromName(selectedName) ?? "IEC61937";
                return (false, $"Bitstream ({pretty})");
            }

            static (bool isPcm, string? pretty)? ClassifyByWave(AMMediaType mt)
            {
                try
                {
                    if (mt.formatType == FormatType.WaveEx && mt.formatPtr != IntPtr.Zero)
                    {
                        var wfex = Marshal.PtrToStructure<WaveFormatEx>(mt.formatPtr);

                        if (wfex.wFormatTag == 1 || wfex.wFormatTag == 3)
                            return (true, "PCM");

                        if (wfex.wFormatTag == 0x0092) // WAVE_FORMAT_DOLBY_AC3_SPDIF
                            return (false, "Dolby Digital");

                        if (wfex.wFormatTag == 0xFFFE && wfex.cbSize >= 22)
                        {
                            var ext = Marshal.PtrToStructure<WaveFormatExtensibleLocal>(mt.formatPtr);

                            var subStr = ext.SubFormat.ToString().ToUpperInvariant();
                            if (subStr.Contains("61937") || subStr.Contains("SPDIF"))
                                return (false, "IEC61937");

                            var s = ext.SubFormat.ToString().ToUpperInvariant();
                            string? pretty =
                                s.Contains("TRUEHD") || s.Contains("MLP") ? "Dolby TrueHD" :
                                s.Contains("EAC3") || s.Contains("DDPLUS") || s.Contains("DD+") ? "Dolby Digital Plus" :
                                s.Contains("AC3") || s.Contains("DOLBY_AC3") ? "Dolby Digital" :
                                (s.Contains("DTS_HD") && s.Contains("MA")) ? "DTS-HD MA" :
                                (s.Contains("DTS_HD") && (s.Contains("HRA") || s.Contains("HIGH"))) ? "DTS-HD HRA" :
                                s.Contains("DTS") ? "DTS" : null;

                            return (false, pretty);
                        }

                        return (false, null);
                    }
                }
                catch { }
                return null;
            }

            static (ushort wFormatTag, int nSamplesPerSec, int nChannels, int wBitsPerSample, int validBitsPerSample) ReadWave(AMMediaType mt)
            {
                ushort tag = 0; int rate = 0; int ch = 0; int bps = 0; int vbits = 0;
                try
                {
                    if (mt.formatType == FormatType.WaveEx && mt.formatPtr != IntPtr.Zero)
                    {
                        var wfex = Marshal.PtrToStructure<WaveFormatEx>(mt.formatPtr);
                        tag = wfex.wFormatTag;
                        rate = unchecked((int)wfex.nSamplesPerSec);
                        ch = unchecked((int)wfex.nChannels);
                        bps = wfex.wBitsPerSample;

                        if (tag == 0xFFFE /*WAVE_FORMAT_EXTENSIBLE*/ && wfex.cbSize >= 22)
                        {
                            var ext = Marshal.PtrToStructure<WaveFormatExtensibleLocal>(mt.formatPtr);
                            if (ext.wValidBitsPerSample != 0) vbits = ext.wValidBitsPerSample;
                        }
                    }
                }
                catch { }
                return (tag, rate, ch, bps, vbits);
            }

            static string? PrettyFromWaveOrSubtype(AMMediaType mt)
            {
                try
                {
                    var sub = mt.subType;
                    string g = sub.ToString().ToUpperInvariant();
                    if (g.Contains("AC3") || g.Contains("DOLBY_AC3")) return "Dolby Digital";
                    if (g.Contains("EAC3") || g.Contains("DDPLUS") || g.Contains("DD+")) return "Dolby Digital Plus";
                    if (g.Contains("TRUEHD") || g.Contains("MLP")) return "Dolby TrueHD";
                    if (g.Contains("DTS_HD") && g.Contains("MA")) return "DTS-HD MA";
                    if (g.Contains("DTS_HD") && (g.Contains("HRA") || g.Contains("HIGH"))) return "DTS-HD HRA";
                    if (g.Contains("DTS")) return "DTS";
                    if (g.Contains("AAC")) return "AAC";
                    if (g.Contains("OPUS")) return "Opus";
                    if (g.Contains("FLAC")) return "FLAC";
                    if (g.Contains("PCM")) return "PCM";
                    if (g.Contains("IEEE_FLOAT")) return "PCM (float)";

                    if (mt.formatType == FormatType.WaveEx && mt.formatPtr != IntPtr.Zero)
                    {
                        var wfex = Marshal.PtrToStructure<WaveFormatEx>(mt.formatPtr);
                        ushort tag = wfex.wFormatTag;
                        if (tag == 1) return "PCM";
                        if (tag == 3) return "PCM (float)";
                        if (tag == 0x0092) return "IEC61937";
                        if (tag == 0x2000) return "Dolby/DTS (compresso)";
                    }

                    var sg = mt.subType.ToString().ToUpperInvariant();
                    if (sg.Contains("61937") || sg.Contains("SPDIF"))
                        return "IEC61937";
                }
                catch { }
                return null;
            }

            static string? PrettyFromName(string? name)
            {
                if (string.IsNullOrWhiteSpace(name)) return null;
                string n = name.ToUpperInvariant();
                if (n.Contains("TRUEHD")) return n.Contains("ATMOS") || n.Contains("JOC") ? "Dolby TrueHD (Atmos)" : "Dolby TrueHD";
                if (n.Contains("E-AC3") || n.Contains("EAC3") || n.Contains("DDP") || n.Contains("DD+"))
                    return n.Contains("ATMOS") || n.Contains("JOC") ? "Dolby Digital Plus (Atmos)" : "Dolby Digital Plus";
                if (n.Contains("AC3") || n.Contains("DOLBY DIGITAL")) return "Dolby Digital";
                if (n.Contains("DTS:X") || n.Contains("DTS X")) return "DTS:X";
                if (n.Contains("DTS-HD MA") || n.Contains("DTS HD MA") || n.Contains("MASTER AUDIO")) return "DTS-HD MA";
                if (n.Contains("DTS-HD HRA") || n.Contains("HIGH RES")) return "DTS-HD HRA";
                if (n.Contains("DTS")) return "DTS";
                if (n.Contains("AAC")) return "AAC";
                if (n.Contains("OPUS")) return "Opus";
                if (n.Contains("FLAC")) return "FLAC";
                return null;
            }

            // preferisci stringhe non generiche (es. "DTS-HD MA" batte "IEC61937")
            static string PreferMoreSpecific(string a, string b)
            {
                bool AIsGeneric = a.IndexOf("IEC61937", StringComparison.OrdinalIgnoreCase) >= 0;
                bool BIsGeneric = b.IndexOf("IEC61937", StringComparison.OrdinalIgnoreCase) >= 0;
                if (AIsGeneric && !BIsGeneric) return b;
                if (BIsGeneric && !AIsGeneric) return a;
                return a.Length >= b.Length ? a : b;
            }
        }


        // Graph helpers
        private bool TryGetFilterGraph(out IFilterGraph2? fg)
        {
            fg = null;
            if (_engine == null) return false;
            try
            {
                if (_engine is IFilterGraph2 direct) { fg = direct; return true; }

                var t = _engine.GetType();
                var p1 = t.GetProperty("Graph", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (p1 != null && p1.GetValue(_engine) is IFilterGraph2 g1) { fg = g1; return true; }

                var p2 = t.GetProperty("FilterGraph", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (p2 != null && p2.GetValue(_engine) is IFilterGraph2 g2) { fg = g2; return true; }

                var m1 = t.GetMethod("GetGraph", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (m1 != null && (m1.Invoke(_engine, null) is IFilterGraph2 g3)) { fg = g3; return true; }
            }
            catch { }
            return false;
        }
        private static bool TryFindFilter(IFilterGraph2 fg, string nameContains, out IBaseFilter? filter)
        {
            filter = null;
            if (fg.EnumFilters(out IEnumFilters? enumF) != 0 || enumF == null) return false;

            var arr = new IBaseFilter[1];
            while (enumF.Next(1, arr, IntPtr.Zero) == 0)
            {
                var f = arr[0];
                f.QueryFilterInfo(out var info);
                try
                {
                    if (!string.IsNullOrWhiteSpace(info.achName) &&
                        info.achName.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                    { filter = f; return true; }
                }
                finally
                {
                    if (info.pGraph != null) Marshal.ReleaseComObject(info.pGraph);
                }
                Marshal.ReleaseComObject(f);
            }
            return false;
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

            if (_paused)
            {
                // stoppa qualsiasi integrazione e campionamento
                _ioPrevWhen = DateTime.MinValue;
                _lastPktSample = DateTime.MinValue;
                _aNowTs = _vNowTs = DateTime.MinValue;

                // opzionale: decadi dolcemente verso 0 per evitare salti
                _containerBitrateNowKbps = (int)(_containerBitrateNowKbps * 0.5);
                _audioBitrateNowKbps = (int)(_audioBitrateNowKbps * 0.5);
                _videoBitrateNowKbps = (int)(_videoBitrateNowKbps * 0.5);

                // non accumulare nelle medie finché sei fermo
                _avgLastTs = DateTime.MinValue;
            }

            if (_paused) { _engine.Pause(); }
            else { _engine.Play(); _hud.TimelineVisible = _engine.HasDisplayControl(); }

            _hud.Visible = _engine.HasDisplayControl();
            _hud.ShowOnce(1200);
            EnsureActive();
        }

        private void SafeStop()
        {
            _stopping = true;

            if (_engine != null)
            {
                try { _engine.BindUpdateCallback(null); } catch { }
                try { if (_engineStatusHandler != null) _engine.OnStatus -= _engineStatusHandler; } catch { }
                try { if (_engineProgressHandler != null) _engine.OnProgressSeconds -= _engineProgressHandler; } catch { }
            }
            _engineStatusHandler = null;
            _engineProgressHandler = null;
            _engineUpdateHandler = null;

            try { _engine?.Stop(); } catch { }
            try { _engine?.Dispose(); } catch { }
            try { _refresh.RestoreIfChanged(); } catch { }
            try { _pktRate.Dispose(); } catch { }
            _engine = null;

            _duration = 0; _paused = false;
            _thumbCts?.Cancel(); _thumbCts = null; try { _thumb.Close(); } catch { }
            _audioOnlyBanner.Visible = false;
            _infoOverlay.Visible = false;
            _hud.Visible = false;
            StopAudioMeters();
            _currentPath = null;

            _splash.Visible = true;
            _overlayHost?.SyncTo(this);
            BringOverlaysToFront();
            EnsureActive();
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
            SyncOverlayToVideoRect();
            BringOverlaysToFront();
            if (_engine?.HasDisplayControl() == true) _hud.ShowOnce(1500);
            EnsureActive();
        }

        private void SyncOverlayToVideoRect()
        {
            if (_overlayHost == null) return;

            Rectangle formClientScreen = this.RectangleToScreen(this.ClientRectangle);
            _overlayHost.SyncToScreen(formClientScreen);
            _overlayHost.SetClickThrough(false);

            Rectangle destClient = _videoHost.ClientRectangle;
            try
            {
                if (_engine != null)
                    destClient = _engine.GetLastDestRectAsClient(_videoHost.ClientRectangle);
            }
            catch { destClient = _videoHost.ClientRectangle; }

            destClient.Offset(_videoHost.Left, _videoHost.Top);
            _lastVideoDestInForm = destClient;
        }

        private bool IsMouseOverHud()
        {
            var scr = Control.MousePosition;
            var pt = _hud.PointToClient(scr);
            return _hud.ClientRectangle.Contains(pt);
        }
        private void UpdateTime(double cur)
        {
            _hud?.Invalidate();

            try
            {
                // --- SE PAUSA: non campionare nulla, congela i "now" e non aggiornare le medie ---
                if (_paused)
                {
                    // niente sampler FFmpeg e niente I/O processo
                    _ioPrevWhen = DateTime.MinValue;
                    _lastPktSample = DateTime.MinValue;

                    // porta lentamente a 0 per avere overlay stabile
                    _containerBitrateNowKbps = (int)(_containerBitrateNowKbps * 0.85);
                    _audioBitrateNowKbps = (int)(_audioBitrateNowKbps * 0.85);
                    _videoBitrateNowKbps = (int)(_videoBitrateNowKbps * 0.85);

                    // non accumulare nelle medie finché fermo
                    _avgLastTs = DateTime.MinValue;

                    // aggiorna solo l’overlay (userà i valori già presenti/decaduti)
                    if (_infoOverlay.Visible && _info != null && _engine != null)
                    {
                        var chosen = _manualRendererChoice ?? (_info.IsHdr ? ORDER_HDR.First() : ORDER_SDR.First());
                        UpdateInfoOverlay(chosen, _info.IsHdr);
                    }
                    return;
                }
                if (_engine != null)
                {
                    // MEDIA container (fallback costante)
                    int avgContainerKbpsLocal = 0;
                    try
                    {
                        if (!string.IsNullOrEmpty(_currentPath) && File.Exists(_currentPath) && _duration > 1)
                        {
                            var fi = new FileInfo(_currentPath);
                            avgContainerKbpsLocal = (int)Math.Round((fi.Length * 8.0 / 1000.0) / _duration);
                        }
                    }
                    catch { }

                    // 1) Container NOW: vero I/O del processo (bytes letti) via GetProcessIoCounters
                    var now = DateTime.UtcNow;

                    // === Campionamento reale FFmpeg ogni ~800ms (finestra 0.8s) ===
                    try
                    {
                        if ((DateTime.UtcNow - _lastPktSample).TotalMilliseconds >= 800 && !_stopping)
                        {
                            _lastPktSample = DateTime.UtcNow;
                            double pos = _engine.PositionSeconds;

                            Task.Run(() =>
                            {
                                var (ak, vk) = _pktRate.Sample(pos, 0.8);
                                if (ak > 0 || vk > 0)
                                {
                                    BeginInvoke(new Action(() =>
                                    {
                                        if (ak > 0)
                                        {
                                            _audioBitrateNowKbps = (_audioBitrateNowKbps <= 0)
                                                ? ak : (int)(_audioBitrateNowKbps * 0.6 + ak * 0.4);
                                            _aNowTs = DateTime.UtcNow;
                                        }
                                        if (vk > 0)
                                        {
                                            _videoBitrateNowKbps = (_videoBitrateNowKbps <= 0)
                                                ? vk : (int)(_videoBitrateNowKbps * 0.75 + vk * 0.25);
                                            _vNowTs = DateTime.UtcNow;
                                        }
                                    }));
                                }
                            });
                        }
                    }
                    catch { /* best-effort */ }

                    // I/O processo (container) per calcolare residui e clamp
                    try
                    {
                        if (GetProcessIoCounters(Process.GetCurrentProcess().Handle, out var io))
                        {
                            long curRead = (long)io.ReadTransferCount;

                            if (_ioPrevWhen != DateTime.MinValue)
                            {
                                double dt = (now - _ioPrevWhen).TotalSeconds;
                                if (dt >= 0.50)
                                {
                                    long dbytes = curRead - _ioPrevBytes;
                                    if (dbytes < 0) dbytes = 0;

                                    int inst = (int)Math.Round((dbytes * 8.0 / 1000.0) / Math.Max(dt, 0.001));

                                    if (avgContainerKbpsLocal > 0)
                                    {
                                        int capHi = (int)Math.Round(avgContainerKbpsLocal * 1.80);
                                        int capLo = (int)Math.Round(avgContainerKbpsLocal * 0.25); // 0.22–0.25
                                        inst = Math.Clamp(inst, capLo, capHi);
                                    }

                                    if (inst <= 0 && avgContainerKbpsLocal > 0)
                                        inst = avgContainerKbpsLocal; // fallback: usa media

                                    _containerBitrateNowKbps = _containerBitrateNowKbps <= 0
                                        ? inst
                                        : (int)(_containerBitrateNowKbps * 0.5 + inst * 0.5);
                                }
                            }

                            _ioPrevBytes = curRead;
                            _ioPrevWhen = now;
                        }
                    }
                    catch { /* best-effort */ }

                    // 2) Audio IN/OUT + flag bitstream
                    var sel = _engine.EnumerateStreams().FirstOrDefault(s => s.IsAudio && s.Selected);
                    var lav = GetLavAudioIODetails(sel?.Name);
                    // Fonte unica: OUT reale di LAV (evita falsi positivi)
                    _bitstreamNow = IsBitstream();

                    // 3) Video/Audio NOW (dinamico) + gestione solo-audio
                    bool hasVideo = _engine.HasDisplayControl() || (_info?.HasVideo ?? false);

                    // Se abbiamo un campione “fresco” (<1.5s), non toccare quel valore
                    bool recentAudio = (DateTime.UtcNow - _aNowTs).TotalSeconds <= 1.5;
                    bool recentVideo = (DateTime.UtcNow - _vNowTs).TotalSeconds <= 1.5;

                    if (!hasVideo)
                    {
                        _videoBitrateNowKbps = 0;

                        if (!recentAudio)
                        {
                            int kbps = 0;

                            // 1) LAV: PCM calcolato oppure payload stimato
                            if (lav.AudioNowKbps > 0) kbps = lav.AudioNowKbps;

                            // 2) dal nome traccia
                            if (kbps <= 0 && sel != null) kbps = ParseKbpsFromName(sel.Name);

                            // 3) dal container NOW / media
                            if (kbps <= 0)
                                kbps = (_containerBitrateNowKbps > 0) ? _containerBitrateNowKbps
                                      : (avgContainerKbpsLocal > 0 ? avgContainerKbpsLocal : 0);

                            if (kbps > 0)
                                _audioBitrateNowKbps = (_audioBitrateNowKbps <= 0)
                                    ? kbps
                                    : (int)(_audioBitrateNowKbps * 0.6 + kbps * 0.4);
                        }
                    }
                    else
                    {
                        // AUDIO fallback (se il sampler non ha aggiornato di recente)
                        if (!recentAudio)
                        {
                            if (_paused) { /* già gestito sopra, non ricalcolare da LAV */ }
                            else
                            {
                                if (lav.AudioNowKbps > 0)
                                    _audioBitrateNowKbps = lav.AudioNowKbps;
                                else if (_audioBitrateNowKbps <= 0 && sel != null)
                                    _audioBitrateNowKbps = ParseKbpsFromName(sel.Name);

                                if (_audioBitrateNowKbps <= 0 && avgContainerKbpsLocal > 0)
                                    _audioBitrateNowKbps = (int)(avgContainerKbpsLocal * 0.30);
                            }
                        }

                        // VIDEO fallback come residuo (se il sampler non ha aggiornato di recente)
                        if (!recentVideo)
                        {
                            int residual = 0;
                            if (_containerBitrateNowKbps > 0)
                                residual = Math.Max(0, _containerBitrateNowKbps - Math.Max(0, _audioBitrateNowKbps));
                            else if (avgContainerKbpsLocal > 0)
                                residual = (int)(avgContainerKbpsLocal * 0.70);

                            if (residual > 0)
                            {
                                _videoBitrateNowKbps = _videoBitrateNowKbps <= 0
                                    ? residual
                                    : (int)(_videoBitrateNowKbps * 0.75 + residual * 0.25);
                            }
                        }

                        // In bitstream usa il payload LAV SOLO come fallback (se il sampler non ha dato un campione fresco)
                        if (!recentAudio && _audioBitrateNowKbps <= 0 && _bitstreamNow && lav.AudioNowKbps > 0)
                            _audioBitrateNowKbps = lav.AudioNowKbps;
                    }

                    // 3b) Clamp “minimi”/“massimi” ragionevoli in funzione del container (UHD remux tuning)
                    if (avgContainerKbpsLocal > 0)
                    {
                        // AUDIO: evita valori “impossibili” >50% del container (come prima)
                        if (_audioBitrateNowKbps > 0)
                        {
                            int aMax = (int)Math.Round(avgContainerKbpsLocal * 0.40);
                            _audioBitrateNowKbps = Math.Min(_audioBitrateNowKbps, aMax);
                        }

                        // VIDEO: floor combinato su container e audio medio
                        int audioAvgForFloor =
                            (_audioAvgLiveKbps > 0) ? (int)Math.Round(_audioAvgLiveKbps) :
                            (_audioBitrateNowKbps > 0) ? _audioBitrateNowKbps :
                            (int)Math.Round(avgContainerKbpsLocal * 0.30); // fallback prudente

                        if (_videoBitrateNowKbps > 0)
                        {
                            int vMinContainer = (int)Math.Round(avgContainerKbpsLocal * 0.08);   // 8% media container
                            int vMinAudio = (int)Math.Round(audioAvgForFloor * 1.30);        // 1.3× audio medio
                            int vMin = Math.Min(Math.Max(vMinContainer, vMinAudio), (int)Math.Round(avgContainerKbpsLocal * 0.40)); // non superare il 40% come floor
                            int vMax = (int)Math.Round(avgContainerKbpsLocal * 1.60);   // cap più stretto
                            _videoBitrateNowKbps = Math.Clamp(_videoBitrateNowKbps, vMin, vMax);
                        }
                    }

                    // AUDIO: piccolo floor assoluto (evita numeri ridicoli ma lascia 0 se sconosciuto)
                    if (_audioBitrateNowKbps > 0)
                        _audioBitrateNowKbps = Math.Max(_audioBitrateNowKbps, 24);

                    // 4) MEDIE LIVE — accumulo time-weighted + publish ogni 10s
                    var nowTs = now;

                    if (_avgLastTs != DateTime.MinValue)
                    {
                        double dt = (nowTs - _avgLastTs).TotalSeconds;
                        if (dt > 0 && dt < 5) // ignora outlier/jitter grossi
                        {
                            _avgAudioBitSec += Math.Max(0, _audioBitrateNowKbps) * dt;
                            _avgVideoBitSec += Math.Max(0, _videoBitrateNowKbps) * dt;
                            _avgDurSec += dt;
                        }
                    }
                    _avgLastTs = nowTs;

                    if (_avgLastPublish == DateTime.MinValue || (nowTs - _avgLastPublish).TotalSeconds >= AVG_PUBLISH_SEC)
                    {
                        if (_avgDurSec > 0)
                        {
                            _audioAvgLiveKbps = _avgAudioBitSec / _avgDurSec;
                            _videoAvgLiveKbps = _avgVideoBitSec / _avgDurSec;
                        }
                        _avgLastPublish = nowTs;
                    }
                }
            }
            catch { }

            if (_infoOverlay.Visible && _info != null && _engine != null)
            {
                var chosen = _manualRendererChoice ?? (_info.IsHdr ? ORDER_HDR.First() : ORDER_SDR.First());
                UpdateInfoOverlay(chosen, _info.IsHdr);
            }
        }

        private static int ParseKbpsFromName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            var m = Regex.Match(name, @"(\d{2,5})\s*(kb/s|kbps)", RegexOptions.IgnoreCase);
            return (m.Success && int.TryParse(m.Groups[1].Value, out int v)) ? v : 0;
        }

        private static string Fmt(double s)
        {
            if (double.IsNaN(s) || s < 0) s = 0;
            var ts = TimeSpan.FromSeconds(s);
            return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
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
            if (!_scrubActive)
            {
                _hud.SetPreview(null, seconds);
                return;
            }

            _hud.Visible = true;
            _hud.BringToFront();
            _overlayHost?.BringToFront();

            if (_thumbCts != null && !_thumbCts.IsCancellationRequested && _previewBusy) return;

            if (string.IsNullOrEmpty(_currentPath) || _info == null || !_info.HasVideo)
            {
                _hud.SetPreview(null, seconds);
                return;
            }

            _thumbCts?.Cancel();
            _thumbCts = new CancellationTokenSource();
            var tk = _thumbCts.Token;

            Task.Run(() =>
            {
                _previewBusy = true;
                try
                {
                    System.Drawing.Bitmap? bmp = null;
                    try { bmp = _thumb.Get(seconds); } catch { bmp = null; }

                    if (bmp == null)
                    {
                        try { bmp = _engine?.GetPreviewFrame(seconds, 360); } catch { bmp = null; }
                    }

                    if (tk.IsCancellationRequested) { bmp?.Dispose(); return; }
                    BeginInvoke(new Action(() => _hud.SetPreview(bmp!, seconds)));
                }
                catch
                {
                    BeginInvoke(new Action(() => _hud.SetPreview(null, seconds)));
                }
                finally { _previewBusy = false; }
            }, tk);
        }

        private void StartAudioMetersIfPossible()
        {
            try
            {
                bool bit = IsBitstream();

                if (bit)
                {
                    _audioMeters?.SetInfoMessage("Bitstream attivo: misure disabilitate");
                    _audioMeters!.Visible = false;
                    _audioOnlyBanner.Visible = true;
                    _audioOnlyBanner.BringToFront();
                    return;
                }

                _audioMeters?.SetInfoMessage(null);

                _audioSampler ??= new LoopbackSampler();
                if (_audioSampler.Start())
                {
                    // Subscrive una sola volta (payload completo)
                    _audioSampler.OnMetrics -= OnSamplerMetrics;
                    _audioSampler.OnMetrics += OnSamplerMetrics;

                    _audioOnlyBanner.Visible = false;
                    _audioMeters!.Visible = true;
                    _audioMeters.BringToFront();
                }
                else
                {
                    // Fallback PNG
                    _audioMeters!.Visible = false;
                    _audioOnlyBanner.Visible = true;
                    _audioOnlyBanner.BringToFront();
                }
            }
            catch
            {
                _audioMeters!.Visible = false;
                _audioOnlyBanner.Visible = true;
                _audioOnlyBanner.BringToFront();
            }
        }
        private void OnSamplerMetrics(LoopbackSampler.AudioMetrics m)
        {
            if (_audioMeters == null) return;
            try
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (_audioMeters.Visible) _audioMeters.Update(m);
                    }));
                }
            }
            catch { }
        }

        private void StopAudioMeters()
        {
            try
            {
                try { if (_audioSampler != null) _audioSampler.OnMetrics -= OnSamplerMetrics; } catch { }
                try { if (_audioSampler != null) _audioSampler.OnLevels -= OnSamplerLevels; } catch { }
                _audioSampler?.Stop();
            }
            catch { }
            _audioMeters?.SetInfoMessage(null);
            if (_audioMeters != null) _audioMeters.Visible = false;
        }

        private void OnSamplerLevels(float rmsL, float rmsR, float peakHoldL, float peakHoldR, double[] spectrumDb)
        {
            if (_audioMeters == null) return;

            try
            {
                // Esegui sul thread UI
                if (IsHandleCreated)
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (!_audioMeters.Visible) return;
                        _audioMeters.UpdateLevels(rmsL, rmsR, peakHoldL, peakHoldR,
                            (spectrumDb != null && spectrumDb.Length > 0) ? spectrumDb : null);
                    }));
                }
            }
            catch { /* best-effort */ }
        }


        private void ApplyVolume(float v)
        {
            bool isBt = IsBitstream();

            if (isBt)
            {
                // In bitstream il volume è fisso: forziamo 100% e disabilitiamo lo slider
                try { _engine?.SetVolume(1f); } catch { }
                try { CoreAudioSessionVolume.Set(1f); } catch { }
                try { _hud?.SetExternalVolume(1f); } catch { }
                return;
            }

            try { _engine?.SetVolume(v); } catch { }
            try { CoreAudioSessionVolume.Set(v); } catch { }
        }
    }

    // ======= Splash overlay (home) =======
    internal sealed class SplashOverlay : Control
    {
        public event Action? OpenRequested;
        public event Action? SettingsRequested;
        public event Action? CreditsRequested;

        private Image? _img;
        private Image? _icoOpen, _icoSettings, _icoCredits;
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

            int size = Math.Max(44, Math.Min(64, (int)Math.Round(Height * 0.058)));
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

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            g.Clear(Color.Black);
            if (_img == null) return;

            int maxW = (int)(Width * 0.60);
            int maxH = (int)(Height * 0.60);
            double s = Math.Min(maxW / (double)_img.Width, maxH / (double)_img.Height);
            int w = Math.Max(1, (int)Math.Round(_img.Width * s));
            int h = Math.Max(1, (int)Math.Round(_img.Height * s));
            int x = (Width - w) / 2;
            int y = (Height - h) / 2;

            g.DrawImage(_img, x, y, w, h);

            int size = Math.Max(44, Math.Min(64, (int)Math.Round(Height * 0.058)));
            int gap = Math.Max(14, Math.Min(28, (int)Math.Round(size * 0.35)));
            double t = Math.Clamp((Height - 800) / 600.0, 0, 1);
            int gapBelowLogo = (int)Math.Round(-40 + (-150 - (-40)) * t);
            int cy = y + h + gapBelowLogo;
            int bottomMargin = Math.Max(16, size / 2);
            cy = Math.Min(cy, Height - bottomMargin - size);

            Rectangle rcOpen = new Rectangle(Width / 2 - size / 2, cy, size, size);
            Rectangle rcSettings = new Rectangle(rcOpen.X - size - gap, cy, size, size);
            Rectangle rcCredits = new Rectangle(rcOpen.Right + gap, cy, size, size);

            static void DrawCircleSoft(Graphics gg, Rectangle r)
            {
                using var path = new GraphicsPath();
                path.AddEllipse(r);
                using var fill = new SolidBrush(Color.FromArgb(46, 255, 255, 255));
                gg.FillPath(fill, path);
            }

            DrawCircleSoft(g, rcSettings);
            DrawCircleSoft(g, rcOpen);
            DrawCircleSoft(g, rcCredits);

            void DrawIcon(Graphics gg, Rectangle r, Image? ico)
            {
                if (ico == null) return;
                int pad = Math.Max(10, (int)Math.Round(size * 0.22));
                gg.DrawImage(ico, new Rectangle(r.X + pad, r.Y + pad, r.Width - pad * 2, r.Height - pad * 2));
            }

            DrawIcon(g, rcSettings, _icoSettings);
            DrawIcon(g, rcOpen, _icoOpen);
            DrawIcon(g, rcCredits, _icoCredits);

            RecomputeButtonHitboxes();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            RecomputeButtonHitboxes();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            RecomputeButtonHitboxes();
            if (_lastRcOpen.Contains(e.Location)) { OpenRequested?.Invoke(); return; }
            if (_lastRcSettings.Contains(e.Location)) { SettingsRequested?.Invoke(); return; }
            if (_lastRcCredits.Contains(e.Location)) { CreditsRequested?.Invoke(); return; }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            RecomputeButtonHitboxes();
            bool over = _lastRcOpen.Contains(e.Location) ||
                        _lastRcSettings.Contains(e.Location) ||
                        _lastRcCredits.Contains(e.Location);
            Cursor = over ? Cursors.Hand : Cursors.Default;
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

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x20; return cp; } }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var key = this.FindForm()?.TransparencyKey ?? Color.Black;
            e.Graphics.Clear(key);
        }

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
                        Colors = new[] { Color.FromArgb(255, 32, 216, 255), Color.FromArgb(255, 64, 160, 255), Color.FromArgb(255, 255, 60, 168) },
                        Positions = new[] { 0f, 0.55f, 1f }
                    };
                    lg.InterpolationColors = cb;
                    g.FillRoundedRectangle(lg, fillRect, new Size(rr, rr));
                }
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

    // ======= Info overlay =======
    internal sealed class InfoOverlay : Control
    {
        public struct Stats
        {
            public string Title;
            public string VideoIn, VideoOut, VideoCodec, VideoPrimaries, VideoTransfer, VideoBitrateNow, VideoBitrateAvg;
            public string AudioIn, AudioOut, AudioBitrateNow, AudioBitrateAvg;
            public string Renderer, HdrMode;
            public bool Upscaling, Bitstream, RtxHdr;
        }

        private Stats _s;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool AutoHeight { get; set; } = true;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int MinCardHeight { get; set; } = 140;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int MaxCardHeight { get; set; } = 460;

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
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var key = this.FindForm()?.TransparencyKey ?? Color.Black;
            e.Graphics.Clear(key);
        }

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

            var rcTitle = new Rectangle(x, y, w, fTitle.Height + 2);
            TextRenderer.DrawText(g, string.IsNullOrWhiteSpace(_s.Title) ? "—" : _s.Title,
                fTitle, rcTitle, _txt, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            y = rcTitle.Bottom + 6;

            // RIGA 1: IN/OUT Video (sinistra) + IN/OUT Audio (destra)
            int gap = 28;
            int colW = (w - gap) / 2;
            int col1X = x;
            int col2X = x + colW + gap;

            DrawInOut(g, fKey, fVal, "IN", _s.VideoIn, "OUT", _s.VideoOut, col1X, y, colW);
            DrawInOut(g, fKey, fVal, "IN", _s.AudioIn, "OUT", _s.AudioOut, col2X, y, colW);

            y += ROW_H + 8;

            // RIGA 2: Bitrate (sinistra Video, destra Audio) – "ora" e "medio" in colonnine
            DrawBitrateMini(g, fKey, fVal, "VIDEO", _s.VideoBitrateNow, _s.VideoBitrateAvg, col1X, y, colW);
            DrawBitrateMini(g, fKey, fVal, "AUDIO", _s.AudioBitrateNow, _s.AudioBitrateAvg, col2X, y, colW);

            y += ROW_H + 8;

            using (var p2 = new Pen(Color.FromArgb(38, 255, 255, 255), 1))
                g.DrawLine(p2, x, y, x + w, y);
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
            xx = DrawTag(g, $"Bitstream: {(_s.Bitstream ? "ON" : "OFF")}", xx, ref y, left, right);
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

        private void DrawBitrateMini(Graphics g, Font fKey, Font fVal, string label, string now, string avg, int x, int y, int width)
        {
            int half = (width - 12) / 2;

            int chipW = DrawKeyChip(g, fKey, label, x, y);
            var rcNow = new Rectangle(x + chipW + 6, y, half - chipW - 6, ROW_H);
            var rcAvg = new Rectangle(x + half + 12, y, half - 6, ROW_H);

            TextRenderer.DrawText(g, $"ora: {(string.IsNullOrWhiteSpace(now) ? "n/d" : now)}", fVal, rcNow, _txt,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, $"medio: {(string.IsNullOrWhiteSpace(avg) ? "n/d" : avg)}", fVal, rcAvg, _txt,
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

            var txRc = new Rectangle(rc.X + padX, rc.Y - 1 + (h - sz.Height) / 2, rc.Width - padX * 2, sz.Height);
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
            y += fTitle.Height + 6;        // Title
            y += ROW_H + 8;                // IN/OUT Video+Audio
            y += ROW_H + 8;                // Bitrate mini (Video/AUDIO)
            y += 8 + fHdr.Height + 6;      // divider + header SISTEMA
            y += fTag.Height + 10;         // tag (prime righe)
            y += PAD;
            return y + 4;
        }
    }
}