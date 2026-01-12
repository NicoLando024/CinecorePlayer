using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Net.Http;
using System.Net.Sockets;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Linq;
using System;

#nullable enable

namespace CinecorePlayer2025
{
    internal sealed partial class MediaLibraryPage : UserControl
    {
        // events esterni
        public event Action<string>? OpenRequested;
        public event Action<string, double?>? OpenWithResumeRequested;
        public event Action? CloseRequested;

        // URL pane
        private UrlPane? _urlPane;
        // YouTube pane
        private YouTubePane? _ytPane;
        // DLNA state
        private DlnaDevice? _dlnaSel;
        private readonly Stack<string> _dlnaStack = new(); // breadcrumb containerId
        private CancellationTokenSource? _dlnaCts;

        // HttpClient condiviso (keep-alive)
        private static readonly HttpClient _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        // layout shared state
        private int _contentSidePad = 104;      // padding sinistro dinamico allineato al carosello
        private readonly int _gridRightPad = 24; // padding destro fisso per non stringere le card

        // pannelli principali
        private readonly Panel _left = new()
        {
            Dock = DockStyle.Left,
            Width = 260,
            BackColor = Theme.Nav
        };

        private readonly Panel _leftFooter = new()
        {
            Dock = DockStyle.Bottom,
            Height = 64,
            BackColor = Theme.Nav,
            Padding = new Padding(12, 10, 12, 12)
        };

        private readonly Panel _leftBody = new()
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Nav
        };

        private readonly RightHostPanel _right = new();

        // header in alto a destra
        private readonly HeaderBar _header = new()
        {
            Dock = DockStyle.Top,
            Height = 70,
            Padding = new Padding(16, 8, 16, 8)
        };

        // section headers
        private readonly SectionHeader _secRecenti = new("Riprendi");
        private readonly SectionHeader _secAll = new("Tutti i file");

        // carosello Recenti
        private readonly Panel _carouselHost = new()
        {
            Dock = DockStyle.Top,
            Height = 260,
            BackColor = Color.Black,
            Padding = new Padding(0, 8, 0, 4),
            Visible = true
        };

        private readonly CarouselViewport _carouselViewport = new()
        {
            BackColor = Color.Black
        };

        // carosello frecce
        private IconButton _carPrev = null!;
        private IconButton _carNext = null!;

        // messaggio quando non c'è nulla da riprendere
        private Label _resumeEmptyLabel = null!;

        // griglia contenuti
        private readonly SkinnedFlow _grid = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.Black,
            WrapContents = true,
            Padding = new Padding(0, 8, 0, 8), // verrà rimesso con ApplyContentSidePad
            Margin = new Padding(0),
            UseThemedVScroll = false
        };

        // header widgets
        private SearchBox _search = null!;
        private Chip _chipExt = null!;
        private Chip _chipSort = null!;
        private HeaderActionButton _btnBrowse = null!;
        private HeaderActionButton _btnAddFolder = null!;
        private HeaderActionButton _btnManageFolders = null!;
        private HeaderActionButton _btnRefresh = null!;

        // loading mask overlay
        private readonly LoadingMask _mask = new() { Dock = DockStyle.Fill, Visible = false };

        // overlay gestione cartelle (sopra al pannello destro)
        private Panel _rootsOverlay = null!;
        private FlowLayoutPanel _rootsOverlayList = null!;

        // nav model
        private readonly string[] _catOrder = { "Film", "Video", "Foto", "Musica", "Playlist", "Preferiti" };
        private readonly string[] _srcOrder = { "Il mio computer", "Rete domestica", "YouTube", "URL" };
        private readonly List<NavButton> _catButtons = new();
        private readonly List<NavButton> _srcButtons = new();

        // stato nav selezionato
        private string _selCat = "Film";
        private string _selSrc = "Il mio computer";

        // filtro / sort
        private string _selExt = "Tutte";
        private int _sortIndex = 0; // 0: Recenti, 1: Nome A–Z, 2: Dimensione
        private ContextMenuStrip? _menuSort;
        private ContextMenuStrip? _menuExt;
        private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 220 };

        // scan / thumb infra
        private CancellationTokenSource? _scanCts;
        private CancellationTokenSource? _thumbCts;
        private CancellationTokenSource? _filterCts;
        private List<FileInfo> _cache = new();
        private readonly object _cacheLock = new();
        // cache durata media (in minuti) letta dalle proprietà shell di Windows
        private readonly Dictionary<string, double?> _durationCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _durationLock = new();
        // cache persistente delle durate (su disco, JSON)
        private readonly DurationIndexStore _durationIndex = new();

        // progressive render griglia
        private readonly System.Windows.Forms.Timer _progressiveTimer = new() { Interval = 30 };
        private List<FileInfo> _progressiveList = new();
        private int _progressivePos = 0;
        private CancellationToken _progressiveThumbToken;
        // debounce per ricaricare il carosello quando arrivano nuove copertine
        private readonly System.Windows.Forms.Timer _carouselPosterRefresh = new() { Interval = 500 };

        // NEW: reset totale del render progressivo
        private void ResetProgressiveRender()
        {
            _progressiveTimer.Stop();
            _progressiveList = new List<FileInfo>();
            _progressivePos = 0;
        }

        // persistenza recenti / preferiti / radici / indice libreria
        private readonly RecentsStore _recents = new();
        private readonly FavoritesStore _favs = new();
        private readonly RootsStore _roots = new();
        private readonly LibraryIndexStore _libraryIndex = new();
        private readonly MusicRecentsStore _musicRecents = new();

        public MediaLibraryPage()
        {
            DoubleBuffered = true;
            Dock = DockStyle.Fill;
            BackColor = Color.Black;

            Controls.Add(_right);
            Controls.Add(_left);

            // left column
            _left.Controls.Add(_leftBody);
            _left.Controls.Add(_leftFooter);
            BuildLeftBody();
            BuildLeftFooter();

            // right column
            BuildHeader();

            _carouselHost.Controls.Add(_carouselViewport);

            _right.Controls.Add(_grid);          // Fill
            _right.Controls.Add(_secAll);        // Top
            _right.Controls.Add(_carouselHost);  // Top
            _right.Controls.Add(_secRecenti);    // Top
            _right.Controls.Add(_header);        // Top
            _right.Controls.Add(_mask);          // overlay caricamento

            // overlay gestione cartelle
            BuildRootsOverlay();
            _right.Controls.Add(_rootsOverlay);
            _rootsOverlay.BringToFront();

            _carouselHost.VisibleChanged += (_, __) => AlignCarouselViewport();
            BuildCarouselChrome();

            // padding iniziale
            ApplyContentSidePad();

            // categoria iniziale
            if (IsHandleCreated)
            {
                SetCategory("Film");

                // sposta il focus iniziale sul primo pulsante di catalogo (niente caret nel search)
                var firstCat = _catButtons.FirstOrDefault();
                if (firstCat != null)
                    BeginInvoke(new Action(() => firstCat.Focus()));
            }
            else HandleCreated += (_, __) =>
            {
                if (IsDisposed) return;

                SetCategory("Film");

                // stesso discorso, ma quando l'handle viene creato dopo
                var firstCat = _catButtons.FirstOrDefault();
                if (firstCat != null)
                    BeginInvoke(new Action(() => firstCat.Focus()));
            };

            // debounce search
            _searchDebounce.Tick += (_, __) =>
            {
                _searchDebounce.Stop();
                ApplyFilterAndRender();
            };

            _header.Resize += (_, __) => LayoutHeader();

            // scrollbar custom + sync carosello
            _grid.ScrollStateChanged += (_, __) => { _grid.UpdateThemedScrollbar(); };

            _grid.SizeChanged += (_, __) =>
            {
                _grid.UpdateThemedScrollbar();
                AlignCarouselViewport();
            };
            _grid.Layout += (_, __) =>
            {
                _grid.UpdateThemedScrollbar();
                AlignCarouselViewport();
            };
            _grid.Resize += (_, __) =>
            {
                _grid.UpdateThemedScrollbar();
                AlignCarouselViewport();
            };

            _carouselHost.Resize += (_, __) => AlignCarouselViewport();
            _carouselViewport.Resize += (_, __) => LayoutCarouselArrows();

            _progressiveTimer.Tick += (_, __) => ProgressiveTick();

            // debounce per aggiornare il carosello quando il servizio poster salva nuove copertine
            _carouselPosterRefresh.Tick += (_, __) =>
            {
                _carouselPosterRefresh.Stop();

                if (IsDisposed || !IsHandleCreated)
                    return;

                // aggiorna solo se siamo in Film + "Il mio computer" e il carosello è visibile
                if (!string.Equals(_selCat, "Film", StringComparison.OrdinalIgnoreCase))
                    return;
                if (!string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase))
                    return;
                if (!_carouselHost.Visible)
                    return;

                ForceCarouselRefresh();
            };

            // ascolta le notifiche del servizio metadata film
            MovieMetadataService.PostersChanged += OnPostersChanged;

            HideMask();

            Load += (_, __) =>
            {
                if (IsDisposed) return;

                // lo facciamo in BeginInvoke per essere sicuri che la Size sia quella finale
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;

                    // rifai il render della categoria corrente (di solito "Film")
                    RefreshContent();
                }));
            };
        }


        // ------------ open file/url ------------
        // overload base: senza ripresa
        private void SafeOpen(string pathOrUrl)
            => SafeOpen(pathOrUrl, resumeSeconds: null);

        // overload con posizione di ripresa (per il carosello)
        private void SafeOpen(string pathOrUrl, double? resumeSeconds)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pathOrUrl)) return;

                bool isUrl = Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var u) &&
                             (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

                if (!isUrl && !File.Exists(pathOrUrl))
                {
                    HandleMissingMediaPath(pathOrUrl);
                    return;
                }

                // NEW: se è un file "musica" (locale o URL con estensione audio), salvalo nei recenti musica
                if (IsMusicFilePath(pathOrUrl))
                {
                    _musicRecents.RegisterPlay(pathOrUrl);
                }

                try { _thumbCts?.Cancel(); } catch { }

                if (resumeSeconds.HasValue && OpenWithResumeRequested != null)
                {
                    OpenWithResumeRequested(pathOrUrl, resumeSeconds.Value);
                }
                else
                {
                    OpenRequested?.Invoke(pathOrUrl);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    MessageBox.Show(this,
                        $"Impossibile aprire la sorgente:\n{ex.Message}",
                        "Errore riproduzione",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                catch { }
            }
        }

        // gestisce il caso in cui un file locale indicizzato non esiste più
        private void HandleMissingMediaPath(string path)
        {
            try
            {
                MessageBox.Show(this,
                    "Il file non esiste più.\nLo rimuovo dalla libreria.",
                    "File non trovato",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch { }

            // ci interessa solo per la libreria locale ("Tutti i file")
            bool isLocalGridContext =
                string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase) &&
                IsLocalLibraryCategory(_selCat);

            if (!isLocalGridContext)
                return;

            // 1) aggiorna la cache in memoria
            List<FileInfo> newCache;
            lock (_cacheLock)
            {
                newCache = _cache
                    .Where(fi => !string.Equals(fi.FullName, path, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                _cache = newCache;
            }

            // 2) aggiorna l'indice JSON su disco per la categoria corrente
            _libraryIndex.ReplacePaths(_selCat, newCache.Select(fi => fi.FullName));

            // 3) togli dai preferiti (se presente)
            _favs.Set(path, fav: false);

            // 4) rimuovi la card dalla griglia "Tutti i file"
            var toRemove = _grid.Controls
                .OfType<FileCard>()
                .FirstOrDefault(c => string.Equals(c.FilePath, path, StringComparison.OrdinalIgnoreCase));

            if (toRemove != null)
            {
                _grid.Controls.Remove(toRemove);
                toRemove.Dispose();
                _grid.UpdateThemedScrollbar();
                _grid.Invalidate(true);
                _grid.Update();
            }
        }


    }
}
