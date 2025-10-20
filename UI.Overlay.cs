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
using DirectShowLib;
using FFmpeg.AutoGen;
using VRChoice = global::CinecorePlayer2025.VideoRendererChoice;
using HDRMode = global::CinecorePlayer2025.HdrMode;
using Stereo3DMode = global::CinecorePlayer2025.Stereo3DMode;

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

            // Chiave cromatica NERA (colorkey)
            BackColor = Color.Black;
            TransparencyKey = Color.Black;

            Controls.Add(Surface);
            Surface.BackColor = this.TransparencyKey;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryApplyLayeredColorKey(this.TransparencyKey);
        }

        private void TryApplyLayeredWindowPos()
        {
            try
            {
                Win32.SetWindowPos(this.Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOZORDER | Win32.SWP_NOSIZE | Win32.SWP_NOMOVE | Win32.SWP_FRAMECHANGED);
            }
            catch { }
        }

        private void TryApplyLayeredColorKey(Color key)
        {
            if (!IsHandleCreated) return;
            uint rgb = (uint)(key.R | (key.G << 8) | (key.B << 16));
            try { SetLayeredWindowAttributes(this.Handle, rgb, 255, LWA_COLORKEY); } catch { }
            TryApplyLayeredWindowPos();
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
            // Pulisce col colorkey
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

    // ======= UI (PlayerForm: overlay, HUD, splash, info) =======
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
        private Stereo3DMode _stereo = Stereo3DMode.None;
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

        // === Hot-zone e rettangolo video reale ===
        private Rectangle _lastVideoDestInForm = Rectangle.Empty;
        private const int HUD_HOTZONE_PX = 120;

        // === Impostazioni utente (UI) ===
        private bool _enableUpscaling = false;
        private int _targetFps = 0;
        private bool _preferBitstreamUi = true;
        private readonly DisplayModeSwitcher _refresh = new();

        // Modali overlay
        private SettingsModal _settingsModal = null!;
        private CreditsModal _creditsModal = null!;

        private static readonly VRChoice[] ORDER_HDR = { VRChoice.MADVR, VRChoice.MPCVR };
        private static readonly VRChoice[] ORDER_SDR = { VRChoice.EVR };

        private ToolStripMenuItem _mAudioLang = null!;
        private ToolStripMenuItem _mSubtitles = null!;

        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        private const int WM_SETICON = 0x0080; private const int ICON_SMALL = 0, ICON_BIG = 1, ICON_SMALL2 = 2;

        private Icon? _iconBig; private Icon? _iconSmall;

        // Delegati per unsubscribe sicuro
        private Action<string>? _engineStatusHandler;
        private Action<double>? _engineProgressHandler;
        private Action? _engineUpdateHandler;

        private AudioOnlyOverlay BuildAudioOnlyBanner() => new()
        {
            Dock = DockStyle.Fill,
            Visible = false,
            ImagePath = Path.Combine(AppContext.BaseDirectory, "Assets", "audioOnly.jpg"),
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

            // Mantieni focus in fullscreen (per Space / S)
            Deactivate += (_, __) =>
            {
                if (FormBorderStyle == FormBorderStyle.None)
                    BeginInvoke(new Action(() => { try { Activate(); } catch { } }));
            };

            _rootLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1 };
            _rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _rootLayout.BackColor = Color.Black;
            _rootLayout.CellBorderStyle = TableLayoutPanelCellBorderStyle.None;
            _rootLayout.Padding = Padding.Empty;
            _rootLayout.Margin = Padding.Empty;

            _stack = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            _videoHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };

            // HUD (riquadro nero RIMOSSO → niente host/backdrop)
            _hud = new HudOverlay { Dock = DockStyle.Fill, AutoHide = true, Visible = false };
            _hud.TimelineVisible = false;

            _infoOverlay = new InfoOverlay { Dock = DockStyle.Top, Visible = false, AutoHeight = true, MaxCardHeight = 420 };

            _overlayHost = new OverlayHostForm();
            AddOwnedForm(_overlayHost);
            _overlayHost.Visible = false;

            // overlay figli trasparenti
            _hud.BackColor = Color.Transparent;
            _infoOverlay.BackColor = Color.Transparent;

            _splash = new SplashOverlay { Dock = DockStyle.Fill, Visible = true };
            _splash.OpenRequested += OpenFile;
            _splash.SettingsRequested += ShowSettingsModal;
            _splash.CreditsRequested += ShowCreditsModal;

            _loading = new LoadingOverlay { Dock = DockStyle.Fill, Visible = true };
            _loading.Completed += OnInitialLoadingCompleted;

            _audioOnlyBanner = BuildAudioOnlyBanner();

            _stack.Controls.Add(_videoHost);
            _stack.Controls.Add(_loading);
            _stack.Controls.Add(_splash);

            // Overlay nella finestra host
            _overlayHost.Surface.Controls.Add(_audioOnlyBanner);
            _overlayHost.Surface.Controls.Add(_infoOverlay);
            _overlayHost.Surface.Controls.Add(_hud);
            _hud.BringToFront();

            // HUD hot-zone
            void TryWakeHud()
            {
                if (_engine == null) return;
                var scr = Control.MousePosition;
                bool overHud = IsMouseOverHud();
                if (overHud || IsInHudHotzone(scr))
                {
                    _hud.Visible = true;
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

            _splash.Visible = false;     // parte il loading, poi comparirà lo splash se non apri nulla
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
            _hud.StopClicked += () => SafeStop();
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
                SyncOverlayToVideoRect();
                _infoOverlay.AdjustHeightToContent(_stack.ClientSize.Width);
                BringOverlaysToFront();
            };
            LocationChanged += (_, __) => { SyncOverlayToVideoRect(); };
            SizeChanged += (_, __) => { SyncOverlayToVideoRect(); };

            BuildMenu();
            ContextMenuStrip = _menu;
            _stack.ContextMenuStrip = _menu;
            _hud.ContextMenuStrip = _menu;
            _videoHost.ContextMenuStrip = _menu;
            _splash.ContextMenuStrip = _menu;

            // Blocca menu durante il loading iniziale
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

            // “Apri con …” da Esplora: esegui dopo che il form è mostrato (Handle pronto)
            this.Shown += (_, __) => TryOpenFromCommandLine();
        }

        private void OnInitialLoadingCompleted()
        {
            _loading.Visible = false;
            _splash.Visible = true;
            _hud.Visible = false;
            _hud.TimelineVisible = false;
            BringOverlaysToFront();
        }

        // Intercetta Space e S affidabilmente + blocco input se loading visibile
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_loading.Visible) return true; // blocca TUTTI i tasti finché non finisce il loading
            if (keyData == Keys.Space) { TogglePlayPause(); return true; }
            if (keyData == Keys.S) { SafeStop(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Blocca tasto destro (menù) durante il loading iniziale
        protected override void WndProc(ref Message m)
        {
            const int WM_CONTEXTMENU = 0x007B;
            if (_loading.Visible && m.Msg == WM_CONTEXTMENU) { m.Result = IntPtr.Zero; return; }
            base.WndProc(ref m);
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

        private void TryOpenFromCommandLine()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1)
                {
                    // prendi il primo argomento file valido
                    var p = args.Skip(1).FirstOrDefault(File.Exists);
                    if (!string.IsNullOrEmpty(p))
                    {
                        OpenPath(p!);
                    }
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
            // Manteniamo SEMPRE l'host layered
            enable = false;
            _overlayInlineMode = enable;

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

            if (!_overlayHost.Visible)
            {
                SafeShowOverlayHost();
            }
            SyncOverlayToVideoRect();
            try { _overlayHost.BringToFront(); } catch { }

            BringOverlaysToFront();
            _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);
        }

        // === FIX: mostrare l'overlay host quando l'Handle del form esiste ===
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
                bool showHud = _engine != null && !_splash.Visible;
                _hud.Visible = showHud;
                if (showHud) _hud.BringToFront();
            }

            if (_infoOverlay.Visible) _infoOverlay.BringToFront();
            if (_audioOnlyBanner.Visible) _audioOnlyBanner.BringToFront();
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

        // Overlay "Audio Only"
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
                if (disposing) _png?.Dispose();
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
            var mUpscale = new ToolStripMenuItem("Upscaling (consenti oltre nativo)")
            {
                CheckOnClick = true,
                Checked = _enableUpscaling
            };
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
            mChapters.DropDownOpening += (_, __) => mChapters.Enabled = _info?.Chapters.Count > 0;

            var mShowInfo = new ToolStripMenuItem("Info overlay ON/OFF", null, (_, __) => { _infoOverlay.Visible = !_infoOverlay.Visible; BringOverlaysToFront(); });

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
                mHdr, mRefresh, mUpscale, m3D, _mAudioLang, _mSubtitles, mRenderer, new ToolStripSeparator(),
                mChapters, mShowInfo
            });
        }

        // madVR di default
        private VRChoice? _manualRendererChoice = VRChoice.MADVR;

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

        private volatile bool _stopping;

        private void SkipLoadingIfActive()
        {
            if (_loading?.Visible == true)
            {
                // Stoppa definitivamente il loading iniziale, evita che il suo "Completed"
                // rimostri la splash dopo qualche secondo.
                _loading.Cancel();
                _loading.Visible = false;
                _splash.Visible = false;
                _loading?.Invalidate();
            }
        }

        private void OpenPath(string path, double resume = 0, bool startPaused = false)
        {
            // Stop precedente e pulizia (bug: reopen dopo Remove -> non andava)
            SafeStop();

            // se stiamo aprendo da "Apri con" o subito: salta loading/splash e non farle riapparire
            SkipLoadingIfActive();

            _currentPath = path;

            try { _info = MediaProbe.Probe(path); }
            catch (Exception ex) { _lblStatus.Text = "Probe fallito: " + ex.Message; _info = null; }

            bool hasVideo = _info?.HasVideo == true || LooksLikeVideoByExt(path);
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
                    _stopping = false;
                    _engine = new DirectShowUnifiedEngine(
                        preferBitstream: wantBitstream,
                        preferredRendererName: _selectedAudioRendererName,
                        choice: choice,
                        fileIsHdr: fileHdr,
                        srcAudioCodec: _info?.AudioCodec ?? FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_NONE);

                    _engineStatusHandler = s =>
                    {
                        if (_stopping) return;
                        BeginInvoke(new Action(() => _lblStatus.Text = s));
                    };
                    _engineProgressHandler = s =>
                    {
                        if (_stopping) return;
                        BeginInvoke(new Action(() => UpdateTime(s)));
                    };
                    _engineUpdateHandler = () =>
                    {
                        if (_stopping) return;
                        if (IsHandleCreated) BeginInvoke(new Action(() =>
                        {
                            _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);
                            SyncOverlayToVideoRect();
                            BringOverlaysToFront();
                        }));
                    };

                    _engine.OnStatus += _engineStatusHandler;
                    _engine.OnProgressSeconds += _engineProgressHandler;
                    _engine.BindUpdateCallback(_engineUpdateHandler);

                    UseOverlayInline(false); // SEMPRE host layered

                    _engine.Open(path, hasVideo);

                    bool hasDisplay = _engine.HasDisplayControl();
                    _audioOnlyBanner.Visible = !hasDisplay;
                    _audioOnlyBanner.Invalidate();
                    if (_audioOnlyBanner.Visible) _audioOnlyBanner.BringToFront();

                    _duration = _engine.DurationSeconds > 0 ? _engine.DurationSeconds : (_info?.Duration ?? 0);

                    _splash.Visible = false;
                    BringOverlaysToFront();

                    try { _thumb.Open(path); } catch { }

                    _engine.SetStereo3D(_stereo);
                    _engine.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);

                    SafeShowOverlayHost();
                    SyncOverlayToVideoRect();
                    BringOverlaysToFront();

                    // Auto-selezione stream (no proprietà Language: euristica sul Name)
                    AutoSelectDefaultStreams();

                    UpdateInfoOverlay(choice, fileHdr);
                    // NON aprire automaticamente l'overlay info
                    // _infoOverlay.Visible = true;

                    _hud.TimelineVisible = true;
                    _hud.Visible = true;
                    _hud.ShowOnce(2000);

                    _paused = startPaused;
                    try
                    {
                        if (!startPaused) _engine.Play();
                        else _engine.Pause();
                    }
                    catch { }

                    ApplyVolume(1f);

                    // Entra in fullscreen appena parte (comportamento attuale)
                    if (FormBorderStyle != FormBorderStyle.None) ToggleFullscreen();

                    var t = new System.Windows.Forms.Timer { Interval = 300 };
                    t.Tick += (_, __) =>
                    {
                        try
                        {
                            _engine?.UpdateVideoWindow(_videoHost.Handle, _videoHost.ClientRectangle);
                            SyncOverlayToVideoRect();
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

        private static bool NameLooksItalian(string? name)
        {
            var n = (name ?? "").ToUpperInvariant();
            return n.Contains("ITA") || n.Contains("ITALIAN");
        }
        private static bool NameLooksEnglish(string? name)
        {
            var n = (name ?? "").ToUpperInvariant();
            return n.Contains("ENG") || n.Contains("ENGLISH");
        }

        private void AutoSelectDefaultStreams()
        {
            if (_engine == null) return;
            try
            {
                var streams = _engine.EnumerateStreams().ToList();

                // audio: lascia quello che l'engine ha marcato Selected, altrimenti il primo
                var selAudio = streams.FirstOrDefault(s => s.IsAudio && s.Selected) ??
                               streams.FirstOrDefault(s => s.IsAudio);
                if (selAudio != null) _engine.EnableByGlobalIndex(selAudio.GlobalIndex);

                // sottotitoli: se nessuno selezionato, prova ITA→ENG→primo (euristica sul Name)
                var selSub = streams.FirstOrDefault(s => s.IsSubtitle && s.Selected);
                if (selSub == null)
                {
                    selSub = streams.FirstOrDefault(s => s.IsSubtitle && NameLooksItalian(s.Name))
                             ?? streams.FirstOrDefault(s => s.IsSubtitle && NameLooksEnglish(s.Name))
                             ?? streams.FirstOrDefault(s => s.IsSubtitle);
                    if (selSub != null) _engine.EnableByGlobalIndex(selSub.GlobalIndex);
                }
            }
            catch (Exception ex)
            {
                Dbg.Warn("AutoSelectDefaultStreams: " + ex.Message);
            }
        }

        private void UpdateInfoOverlay(VRChoice renderer, bool fileHdr)
        {
            if (_info == null || _engine == null) return;

            var (w, h, sub) = _engine.GetNegotiatedVideoFormat();

            int vw = _videoHost.ClientSize.Width;
            int vh = _videoHost.ClientSize.Height;
            string outStr = (w > 0 ? $"{w}x{h}" : "n/d") + $" • {sub}";
            if (vw > 0 && vh > 0) outStr += $"  (viewport {vw}x{vh})";

            var selAudio = _engine.EnumerateStreams().FirstOrDefault(s => s.IsAudio && s.Selected);
            string audioPretty = PrettyAudioName(_info, selAudio?.Name);
            bool upscalerActive = _enableUpscaling && renderer == VRChoice.MADVR;

            var s = new InfoOverlay.Stats
            {
                Title = Path.GetFileName(_currentPath) ?? "—",
                VideoIn = $"{_info.Width}x{_info.Height} • {CodecName(_info.VideoCodec)} • {(_info.VideoBits > 0 ? _info.VideoBits + "-bit" : "8-bit?")}",
                VideoOut = outStr,
                VideoCodec = CodecName(_info.VideoCodec),
                VideoPrimaries = PrimName(_info.Primaries),
                VideoTransfer = TrcName(_info.Transfer),
                VideoBitrateNow = "n/d",
                VideoBitrateAvg = "n/d",
                AudioIn = $"{audioPretty} • {(_info.AudioRate > 0 ? _info.AudioRate / 1000 + " kHz" : "n/d")} • {AudioChText(_info)}",
                AudioOut = _engine.IsBitstreamActive() ? "Bitstream (pass-through)" : "PCM",
                AudioBitrateNow = "n/d",
                AudioBitrateAvg = "n/d",
                Renderer = renderer.ToString() + (upscalerActive ? " (madVR upscaler)" : ""),
                HdrMode = fileHdr ? (_hdr == HDRMode.Auto ? "HDR (auto)" : "SDR (tone-map)") : "SDR",
                Upscaling = upscalerActive,
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
            else { _engine.Play(); _hud.TimelineVisible = true; }
            _hud.Visible = true;
            _hud.ShowOnce(1200);
            EnsureActive();
        }

        // Ferma in modo “sicuro”
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
            _engine = null;

            _duration = 0; _paused = false;
            _thumbCts?.Cancel(); _thumbCts = null; try { _thumb.Close(); } catch { }
            _audioOnlyBanner.Visible = false;
            _infoOverlay.Visible = false;
            _hud.Visible = false;
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
            _hud.ShowOnce(1500);
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

        private bool IsInHudHotzone(Point screenPos)
        {
            var clientPos = this.PointToClient(screenPos);
            var v = _lastVideoDestInForm;
            if (v.Width <= 0 || v.Height <= 0) v = new Rectangle(0, 0, this.ClientSize.Width, this.ClientSize.Height);

            if (!v.Contains(clientPos)) return false;
            return clientPos.Y >= (v.Bottom - HUD_HOTZONE_PX);
        }

        private bool IsMouseOverHud()
        {
            var scr = Control.MousePosition;
            var pt = _hud.PointToClient(scr);
            return _hud.ClientRectangle.Contains(pt);
        }

        private void UpdateTime(double cur) { _hud?.Invalidate(); }
        private static string Fmt(double s) { if (double.IsNaN(s) || s < 0) s = 0; var ts = TimeSpan.FromSeconds(s); return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss"); }

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
            if (string.IsNullOrEmpty(_currentPath) || _info == null || !_info.HasVideo)
            {
                _hud.SetPreview(null, seconds);
                return;
            }
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
        private bool _cancelled;

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
                if (_progress01 >= 1.0)
                {
                    _tick.Stop();
                    if (!_cancelled) Completed?.Invoke();
                }
            };
        }

        public void Start()
        {
            _cancelled = false;
            _progress01 = 0;
            _start = DateTime.UtcNow;
            _tick.Start();
            Invalidate();
        }

        // FIX: possibilità di annullare il loading per “Apri con …” immediato
        public void Cancel()
        {
            _cancelled = true;
            if (_tick.Enabled) _tick.Stop();
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

    // ======= Info overlay =======
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

            int gap = 28;
            int colW = (w - gap) / 2;
            int col1X = x;
            int col2X = x + colW + gap;

            DrawInOut(g, fKey, fVal, "IN", _s.VideoIn, "OUT", _s.VideoOut, col1X, y, colW);
            DrawInOut(g, fKey, fVal, "IN", _s.AudioIn, "OUT", _s.AudioOut, col2X, y, colW);

            y += ROW_H + 6;

            using (var p = new Pen(Color.FromArgb(38, 255, 255, 255), 1))
                g.DrawLine(p, x, y, x + w, y);
            y += 8;

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
}
