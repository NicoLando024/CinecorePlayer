using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Xml.Linq;

#nullable enable

namespace CinecorePlayer2025
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.IO;
    using System.Linq;
    using System.Media;
    using System.Security;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Windows.Forms;

    internal sealed class MediaLibraryPage : UserControl
    {
        // events esterni
        public event Action<string>? OpenRequested;
        public event Action<string, double?>? OpenWithResumeRequested;
        public event Action? CloseRequested;

        // URL pane
        private UrlPane? _urlPane;

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
        private List<FileInfo> _cache = new();
        private readonly object _cacheLock = new();
        // cache durata media (in minuti) letta dalle proprietà shell di Windows
        private readonly Dictionary<string, double?> _durationCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _durationLock = new();

        // progressive render griglia
        private readonly System.Windows.Forms.Timer _progressiveTimer = new() { Interval = 30 };
        private List<FileInfo> _progressiveList = new();
        private int _progressivePos = 0;
        private CancellationToken _progressiveThumbToken;

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
            _right.Controls.Add(_mask);          // overlay

            BuildCarouselChrome();

            // padding iniziale
            ApplyContentSidePad();

            // categoria iniziale
            if (IsHandleCreated) SetCategory("Film");
            else HandleCreated += (_, __) =>
            {
                if (!IsDisposed) SetCategory("Film");
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

            HideMask();
        }

        // ------------ padding condiviso fra carosello, sezioni e griglia ------------
        private void ApplyContentSidePad()
        {
            _grid.Padding = new Padding(_contentSidePad, 8, _gridRightPad, 8);

            _secRecenti.LeftMargin = _contentSidePad;
            _secAll.LeftMargin = _contentSidePad;

            _secRecenti.Invalidate();
            _secAll.Invalidate();
        }

        // ------------ mask overlay ------------
        private void ShowMask(string msg)
        {
            _mask.SetMessage(msg);
            _mask.Visible = true;
            _mask.BringToFront();
        }

        private void HideMask()
        {
            _mask.Visible = false;
            _grid.BringToFront();
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

        // ------------ LEFT NAV (logo + categorie/sorgenti + footer Chiudi) ------------
        private void BuildLeftBody()
        {
            var stack = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                BackColor = Theme.Nav,
                Padding = new Padding(10, 10, 10, 6)
            };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _leftBody.Controls.Add(stack);

            stack.Controls.Add(MakeLogoHeader());

            stack.Controls.Add(MkSection("Catalogo").WithMargin(0, 10, 0, 0));
            foreach (var c in _catOrder)
            {
                var b = new NavButton(c) { Dock = DockStyle.Top };
                b.Click += (_, __) =>
                {
                    SetCategory(c);
                    RefreshNavPaint();
                };
                _catButtons.Add(b);
                stack.Controls.Add(b);
            }

            stack.Controls.Add(MkSection("SORGENTE").WithMargin(0, 12, 0, 0));
            foreach (var s in _srcOrder)
            {
                var b = new NavButton(s) { Dock = DockStyle.Top };
                b.Click += (_, __) =>
                {
                    SetSource(s);
                    RefreshNavPaint();
                };
                _srcButtons.Add(b);
                stack.Controls.Add(b);
            }

            // filler
            stack.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Theme.Nav });
        }

        private Control MakeLogoHeader()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var logoPath = Path.Combine(baseDir, "assets", "logo.png"); // logo bianco orizzontale
                if (File.Exists(logoPath))
                {
                    var pic = new PictureBox
                    {
                        Height = 80,
                        Dock = DockStyle.Top,
                        SizeMode = PictureBoxSizeMode.Zoom,
                        BackColor = Theme.Nav,
                        Padding = new Padding(8, 0, 0, 0),
                        Margin = new Padding(0)
                    };
                    using (var bmp = new Bitmap(logoPath))
                    {
                        pic.Image = new Bitmap(bmp); // clone → niente file lock
                    }
                    return pic;
                }
            }
            catch { }

            // fallback vuoto senza scritta cinecore
            return new Panel
            {
                Height = 44,
                Dock = DockStyle.Top,
                BackColor = Theme.Nav,
                Margin = new Padding(0)
            };
        }
        private static string NormalizeRootPath(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            string path = input.Trim();

            // prima uniformiamo gli slash (ma NON tocchiamo l’eventuale prefisso UNC \\server\share)
            path = path.Replace('/', Path.DirectorySeparatorChar);

            // --- pattern “drive letter” puri: D / d / D: / d: / D:\ / d:\ ---
            if (path.Length == 1 && char.IsLetter(path[0]))
            {
                // "D" → "D:\"
                return $"{char.ToUpperInvariant(path[0])}:{Path.DirectorySeparatorChar}";
            }

            if (path.Length == 2 && char.IsLetter(path[0]) && path[1] == ':')
            {
                // "D:" → "D:\"
                return $"{char.ToUpperInvariant(path[0])}:{Path.DirectorySeparatorChar}";
            }

            if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' &&
                (path[2] == '\\' || path[2] == Path.DirectorySeparatorChar))
            {
                // "D:\" o "D:\qualcosa"
                char drive = char.ToUpperInvariant(path[0]);

                if (path.Length == 3)
                {
                    // esattamente "D:\" → normalizza e basta
                    return $"{drive}:{Path.DirectorySeparatorChar}";
                }

                // "D:\qualcosa" → drive + resto ripulito dagli slash doppi
                string rest = path.Substring(2)
                                  .Replace('\\', Path.DirectorySeparatorChar)
                                  .Replace('/', Path.DirectorySeparatorChar);

                // evitiamo roba tipo "D::\"
                if (rest.Length == 0 || rest == Path.DirectorySeparatorChar.ToString())
                    return $"{drive}:{Path.DirectorySeparatorChar}";

                return $"{drive}:{rest}";
            }

            // Se è un path radicato (es. UNC \\server\share o C:\foo\bar) proviamo a canonizzarlo
            try
            {
                if (Path.IsPathRooted(path))
                    path = Path.GetFullPath(path);
            }
            catch
            {
                // se fallisce, lasciamo il path così com'è
            }

            return path;
        }

        private void BuildLeftFooter()
        {
            var btnClose = new FlatButton("Chiudi", FlatButton.Variant.Secondary)
            {
                Dock = DockStyle.Fill,
                Height = 32,
                TabStop = false
            };
            btnClose.Click += (_, __) => CloseRequested?.Invoke();
            _leftFooter.Controls.Add(btnClose);
        }

        private static Label MkSection(string text) => new()
        {
            Text = text.ToUpperInvariant(),
            AutoSize = false,
            Height = 20,
            Dock = DockStyle.Top,
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(6, 0, 0, 0),
            ForeColor = Theme.Muted,
            BackColor = Theme.Nav,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };

        private void RefreshNavPaint()
        {
            foreach (var b in _catButtons)
                b.Selected = string.Equals(b.Text, _selCat, StringComparison.OrdinalIgnoreCase);

            foreach (var b in _srcButtons)
                b.Selected = string.Equals(b.Text, _selSrc, StringComparison.OrdinalIgnoreCase);

            _leftBody.Invalidate(true);
        }
        private void SetCategory(string c)
        {
            _selCat = c;

            // prune recenti non compatibili con l'estensione della categoria
            _recents.PruneToCategory(_selCat, ExtsForCategory(_selCat));

            BuildHeaderFilters();

            bool showCarousel = !(string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase));

            _secRecenti.Visible = showCarousel;
            _carouselHost.Visible = showCarousel;

            // 1) PRIMA il carosello (usa lo stato corrente)
            LoadRecentsCarouselImmediate();

            // 2) POI la griglia / scansioni (che resettano i CTS)
            RefreshContent();

            RefreshNavPaint();
        }

        private void SetSource(string s)
        {
            _selSrc = s;
            RefreshContent();
            RefreshNavPaint();
        }
        public void ForceCarouselRefresh()
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            // Se chiamato da un thread che non è l’UI, rimbalza sull’UI
            if (InvokeRequired)
            {
                BeginInvoke(new Action(ForceCarouselRefresh));
                return;
            }

            LoadRecentsCarouselImmediate();
        }


        // ------------ HEADER (search / chip estensione / chip sort / browse file) ------------
        private void BuildHeader()
        {
            _search = new SearchBox { Width = 360, Height = 32, Margin = new Padding(0) };
            _search.Placeholder = "Cerca nome o percorso…";
            _search.TextChanged += (_, __) =>
            {
                _searchDebounce.Stop();
                _searchDebounce.Start();
            };
            _header.Controls.Add(_search);

            _chipExt = new Chip("Estensione: Tutte") { Height = 32, TabStop = false };
            _chipExt.Click += (_, __) => ShowMenuExt();

            _chipSort = new Chip("Ordina: Recenti") { Height = 32, TabStop = false };
            _chipSort.Click += (_, __) => ShowMenuSort();

            _btnBrowse = new HeaderActionButton("Scegli file")
            {
                Width = 148,
                Height = 32,
                TabStop = false
            };
            _btnBrowse.Click += (_, __) =>
            {
                using var ofd = new OpenFileDialog
                {
                    Filter =
                        "Media|*.mkv;*.m2ts;*.ts;*.iso;*.mp4;*.m4v;*.mov;*.avi;*.wmv;*.webm;*.flv;*.flac;*.mp3;*.mka;*.aac;*.ogg;*.wav;*.wma;*.m4a;*.opus|Tutti i file|*.*"
                };
                if (ofd.ShowDialog() == DialogResult.OK)
                    SafeOpen(ofd.FileName);
            };

            _btnAddFolder = new HeaderActionButton("Aggiungi cartella")
            {
                Width = 148,
                Height = 32,
                TabStop = false
            };
            _btnAddFolder.Click += (_, __) => AddFolderForCurrentCategory();

            _btnManageFolders = new HeaderActionButton("Cartelle…")
            {
                Width = 120,
                Height = 32,
                TabStop = false
            };
            _btnManageFolders.Click += (_, __) => ManageFoldersForCurrentCategory();

            _btnRefresh = new HeaderActionButton("Aggiorna")
            {
                Width = 120,
                Height = 32,
                TabStop = false
            };
            _btnRefresh.Click += (_, __) => ForceRescanCurrentCategory();

            _header.Controls.AddRange(new Control[]
            {
                _btnBrowse, _btnRefresh, _btnAddFolder, _btnManageFolders, _chipSort, _chipExt
            });

            BuildHeaderFilters();
            LayoutHeader();
        }

        private void LayoutHeader()
        {
            bool narrow = _header.Width < 720;
            bool isLocalCat = IsLocalLibraryCategory(_selCat);
            bool isLocalSrc = string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase);

            // i bottoni cartelle hanno senso solo per "Il mio computer" + Film/Video/Foto/Musica
            _btnAddFolder.Visible = isLocalCat && isLocalSrc;
            _btnManageFolders.Visible = isLocalCat && isLocalSrc;
            _btnRefresh.Visible = isLocalCat && isLocalSrc; 

            if (!narrow)
            {
                if (_header.Height != 70)
                    _header.Height = 70;

                int right = _header.Width - 16;

                // browse | refresh | manage-folders | add-folder | sort | ext a destra
                _btnBrowse.Location = new Point(
                    right - _btnBrowse.Width,
                    (_header.Height - _btnBrowse.Height) / 2);
                right -= _btnBrowse.Width + 10;

                if (_btnRefresh.Visible)
                {
                    _btnRefresh.Location = new Point(
                        right - _btnRefresh.Width,
                        (_header.Height - _btnRefresh.Height) / 2);
                    right -= _btnRefresh.Width + 10;
                }

                if (_btnManageFolders.Visible)
                {
                    _btnManageFolders.Location = new Point(
                        right - _btnManageFolders.Width,
                        (_header.Height - _btnManageFolders.Height) / 2);
                    right -= _btnManageFolders.Width + 10;
                }

                if (_btnAddFolder.Visible)
                {
                    _btnAddFolder.Location = new Point(
                        right - _btnAddFolder.Width,
                        (_header.Height - _btnAddFolder.Height) / 2);
                    right -= _btnAddFolder.Width + 10;
                }

                _chipSort.AutoSizeToText();
                _chipSort.Location = new Point(
                    right - _chipSort.Width,
                    (_header.Height - _chipSort.Height) / 2);
                right -= _chipSort.Width + 8;

                _chipExt.AutoSizeToText();
                _chipExt.Location = new Point(
                    right - _chipExt.Width,
                    (_header.Height - _chipExt.Height) / 2);
                right -= _chipExt.Width + 8;

                int left = 16;
                int minWidth = 220;
                int space = Math.Max(minWidth, right - left);
                _search.Width = space;
                _search.Location = new Point(
                    left,
                    (_header.Height - _search.Height) / 2);
            }
            else
            {
                int topPadding = 12;
                int rowGap = 8;
                int lineH = 32;
                int totalH = topPadding + lineH + rowGap + lineH + topPadding;
                if (_header.Height != totalH)
                    _header.Height = totalH;

                int padX = 16;
                _search.Width = Math.Max(220, _header.Width - padX * 2);
                _search.Location = new Point(padX, topPadding);

                int y2 = _search.Bottom + rowGap;
                _chipExt.AutoSizeToText();
                _chipSort.AutoSizeToText();
                int x = padX;

                _chipExt.Location = new Point(x, y2);
                x += _chipExt.Width + 8;

                _chipSort.Location = new Point(x, y2);
                x += _chipSort.Width + 8;

                if (_btnAddFolder.Visible)
                {
                    _btnAddFolder.Location = new Point(x, y2);
                    x += _btnAddFolder.Width + 8;
                }

                if (_btnManageFolders.Visible)
                {
                    _btnManageFolders.Location = new Point(x, y2);
                    x += _btnManageFolders.Width + 8;
                }

                if (_btnRefresh.Visible)
                {
                    _btnRefresh.Location = new Point(x, y2);
                    x += _btnRefresh.Width + 8;
                }

                _btnBrowse.Location = new Point(x, y2);
            }
        }

        private void ForceRescanCurrentCategory()
        {
            // azzera l’indice per la categoria corrente e forza una nuova indicizzazione completa
            _libraryIndex.ReplacePaths(_selCat, Array.Empty<string>());
            RefreshContent();
        }

        private void AddFolderForCurrentCategory()
        {
            if (IsDisposed) return;

            var cat = _selCat;

            // solo Film / Video / Foto / Musica
            if (!IsLocalLibraryCategory(cat))
                return;

            // solo sorgente "Il mio computer"
            if (!string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase))
                return;

            using var fbd = new FolderBrowserDialog
            {
                Description = $"Seleziona una cartella o un intero disco per i tuoi {cat.ToLowerInvariant()}.",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
                RootFolder = Environment.SpecialFolder.MyComputer
            };

            var owner = FindForm();
            var result = owner != null ? fbd.ShowDialog(owner) : fbd.ShowDialog();
            if (result != DialogResult.OK)
                return;

            // normalizza SEMPRE (qui vediamo già cosa entra)
            var raw = fbd.SelectedPath ?? string.Empty;
            var path = NormalizeRootPath(raw);

            // se per qualche motivo siamo ancora messi male, prova a forzare "D:\" sugli 1–2 caratteri
            if (string.IsNullOrWhiteSpace(path))
            {
                path = NormalizeRootPath(raw.Length >= 2 ? raw.Substring(0, 2) : raw);
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                SystemSounds.Beep.Play();
                return;
            }

            // salva il nuovo root (cartella o disco intero)
            _roots.Add(cat, path);

            // svuota l'indice così la categoria viene reindicizzata da zero
            _libraryIndex.ReplacePaths(cat, Array.Empty<string>());

            // parte subito la scansione con i nuovi percorsi
            RefreshContent();
        }

        private void ManageFoldersForCurrentCategory()
        {
            if (!string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase))
                return;

            var cat = _selCat;
            if (!IsLocalLibraryCategory(cat))
                return;

            ShowManageRootsUi(cat, firstRun: false);
        }

        private void BuildHeaderFilters()
        {
            _selExt = "Tutte";
            _chipExt.Text = "Estensione: Tutte";
            _chipExt.AutoSizeToText();

            _menuExt = new ContextMenuStrip
            {
                ShowImageMargin = false,
                RenderMode = ToolStripRenderMode.System,
                BackColor = Color.FromArgb(36, 36, 42),
                ForeColor = Theme.Text
            };

            _menuExt.Items.Add(MakeExtItem("Tutte"));
            foreach (var e in ExtsForCategory(_selCat)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(s => s))
            {
                _menuExt.Items.Add(MakeExtItem(e));
            }

            _menuSort ??= new ContextMenuStrip
            {
                ShowImageMargin = false,
                RenderMode = ToolStripRenderMode.System,
                BackColor = Color.FromArgb(36, 36, 42),
                ForeColor = Theme.Text
            };

            if (_menuSort.Items.Count == 0)
            {
                _menuSort.Items.Add(MakeSortItem("Recenti", 0));
                _menuSort.Items.Add(MakeSortItem("Nome A–Z", 1));
                _menuSort.Items.Add(MakeSortItem("Dimensione", 2));
            }
        }

        private ToolStripMenuItem MakeExtItem(string label)
        {
            var it = new ToolStripMenuItem(label) { ForeColor = Theme.Text };
            it.Click += (_, __) =>
            {
                _selExt = label;
                _chipExt.Text = $"Estensione: {label}";
                _chipExt.AutoSizeToText();
                LayoutHeader();
                ApplyFilterAndRender();
            };
            return it;
        }

        private ToolStripMenuItem MakeSortItem(string label, int idx)
        {
            var it = new ToolStripMenuItem(label) { ForeColor = Theme.Text };
            it.Click += (_, __) =>
            {
                _sortIndex = idx;
                _chipSort.Text = $"Ordina: {label}";
                _chipSort.AutoSizeToText();
                LayoutHeader();
                ApplyFilterAndRender();
            };
            return it;
        }

        private void ShowMenuExt() => _menuExt?.Show(_chipExt, new Point(0, _chipExt.Height));
        private void ShowMenuSort() => _menuSort?.Show(_chipSort, new Point(0, _chipSort.Height));

        // ------------ CAROSELLO "Recenti" ------------
        private void BuildCarouselChrome()
        {
            _carPrev = new IconButton(IconButton.Kind.ChevronLeft);
            _carNext = new IconButton(IconButton.Kind.ChevronRight);

            // scorriamo di UNA card alla volta
            _carPrev.Click += (_, __) => _carouselViewport.StepItems(-1);
            _carNext.Click += (_, __) => _carouselViewport.StepItems(+1);

            _carouselHost.Controls.Add(_carPrev);
            _carouselHost.Controls.Add(_carNext);

            // label "nessun contenuto da riprendere"
            _resumeEmptyLabel = new Label
            {
                Text = "Nessun contenuto da riprendere.",
                AutoSize = true,
                ForeColor = Theme.SubtleText,
                BackColor = Color.Black,
                Visible = false
            };
            _carouselHost.Controls.Add(_resumeEmptyLabel);

            AlignCarouselViewport();
            LayoutCarouselArrows();
            LayoutResumeEmptyLabel();
        }
        private void LayoutResumeEmptyLabel()
        {
            if (_resumeEmptyLabel == null) return;
            if (!_resumeEmptyLabel.Visible) return;

            int x = (_carouselHost.ClientSize.Width - _resumeEmptyLabel.Width) / 2;
            int y = (_carouselHost.ClientSize.Height - _resumeEmptyLabel.Height) / 2;
            if (x < 0) x = 0;
            if (y < 0) y = 0;

            _resumeEmptyLabel.Location = new Point(x, y);
            _resumeEmptyLabel.BringToFront();
        }
        private void AlignCarouselViewport()
        {
            int cardsPerRow = EstimateCardsPerRow();
            if (cardsPerRow < 1) cardsPerRow = 1;

            int itemOuter = _carouselViewport.GetItemOuterWidthEstimate();
            if (itemOuter < 1) itemOuter = 320;

            int desiredW = cardsPerRow * itemOuter;

            int desiredH = _carouselViewport.GetPreferredHeightEstimate();
            if (desiredH < 1) desiredH = 236;

            int hostW = _carouselHost.ClientSize.Width;
            int x = (hostW - desiredW) / 2;
            if (x < 0) x = 0;

            // aggiorna padding comune sinistro
            _contentSidePad = x;
            ApplyContentSidePad();

            _carouselViewport.Size = new Size(desiredW, desiredH);
            _carouselViewport.Location = new Point(x, 8);

            LayoutCarouselArrows();
            LayoutResumeEmptyLabel(); // NEW
        }

        private void LayoutCarouselArrows()
        {
            if (_carPrev == null || _carNext == null) return;

            var vp = _carouselViewport;
            int y = vp.Top + (vp.Height - 42) / 2;
            if (y < 0) y = 0;

            _carPrev.Bounds = new Rectangle(vp.Left - 50, y, 42, 42);
            _carNext.Bounds = new Rectangle(vp.Right + 8, y, 42, 42);

            _carPrev.BringToFront();
            _carNext.BringToFront();
        }
        private void LoadRecentsCarouselImmediate()
        {
            bool isPlaylist = string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase);
            bool isPreferiti = string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase);
            bool isFoto = string.Equals(_selCat, "Foto", StringComparison.OrdinalIgnoreCase);

            bool showCarousel = !(isPlaylist || isPreferiti || isFoto);

            _secRecenti.Visible = showCarousel;
            _carouselHost.Visible = showCarousel;
            if (!showCarousel)
                return;

            // carica tutti i punti di ripresa dal JSON
            var all = PlaybackResumeStore.LoadAll();

            // filtra solo quelli che:
            // - esistono ancora sul disco
            // - sono compatibili con la categoria corrente (film/video/foto/musica)
            var perCat = all
                .Where(e => !string.IsNullOrWhiteSpace(e.MediaPath)
                            && File.Exists(e.MediaPath)
                            && ResumeEntryMatchesCurrentCategory(e))
                .OrderByDescending(e => e.SavedAt)
                .Take(30)
                .ToList();

            if (perCat.Count == 0)
            {
                // niente da riprendere → nascondi frecce, mostra label centrale
                _carouselViewport.ResetItems(
                    new List<string>(),
                    GetOrNewThumbCts().Token,
                    _ => { },
                    (_, __) => { });

                _carPrev.Visible = false;
                _carNext.Visible = false;
                _resumeEmptyLabel.Visible = true;
                AlignCarouselViewport();
                return;
            }

            _resumeEmptyLabel.Visible = false;

            var token = GetOrNewThumbCts().Token;

            // mappa path → entry per progress bar e start position
            var byPath = perCat.ToDictionary(e => e.MediaPath, e => e, StringComparer.OrdinalIgnoreCase);

            var paths = perCat.Select(e => e.MediaPath).ToList();

            _carouselViewport.ResetItems(
                paths,
                token,
                path =>
                {
                    if (byPath.TryGetValue(path, out var entry))
                        SafeOpen(path, entry.PositionSeconds); // RIPARTI DA QUI
                    else
                        SafeOpen(path);
                },
                (path, card) =>
                {
                    // placeholder subito
                    var cat = CategoryFromExt((Path.GetExtension(path) ?? "").ToLowerInvariant());
                    var phBmp = GetCategoryPlaceholder(cat, 520);
                    card.SetInitialPlaceholder(phBmp);

                    // progress bar (pos/dur)
                    if (byPath.TryGetValue(path, out var entry) && entry.DurationSeconds > 0)
                    {
                        double progress = Math.Max(0.0,
                            Math.Min(1.0, entry.PositionSeconds / entry.DurationSeconds));
                        card.SetProgress(progress);
                    }

                    // thumb async poi
                    card.BeginThumbLoad(token);
                });

            // frecce visibili solo se c'è più di una card "a schermo"
            int perRow = EstimateCardsPerRow();
            bool needArrows = paths.Count > perRow;
            _carPrev.Visible = needArrows;
            _carNext.Visible = needArrows;

            AlignCarouselViewport();
        }

        private bool ResumeEntryMatchesCurrentCategory(PlaybackResumeStore.Entry e)
        {
            if (string.IsNullOrWhiteSpace(e.MediaPath))
                return false;

            // niente resume in Playlist/Preferiti
            if (string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase))
                return false;

            var ext = (Path.GetExtension(e.MediaPath) ?? "").ToLowerInvariant();
            string catLower = _selCat.ToLowerInvariant();

            bool isFilmCat = catLower == "film";
            bool isVideoCat = catLower == "video";

            if (isFilmCat || isVideoCat)
            {
                bool isMovieContainer = ext == ".mkv" || ext == ".mp4";
                bool isAnyVideo = IsAnyVideoExtension(ext);
                double mins = e.DurationSeconds > 0 ? e.DurationSeconds / 60.0 : 0;

                if (isFilmCat)
                {
                    // FILM: solo mkv/mp4 con durata >= 40 minuti
                    if (!isMovieContainer) return false;
                    if (mins <= 0) return false;
                    return mins >= 40.0;
                }

                // VIDEO: tutti i video, ma i mkv/mp4 "lunghi" vanno in Film
                if (!isAnyVideo) return false;

                if (!isMovieContainer) return true;

                if (mins <= 0) return true; // durata ignota → consideralo video generico
                return mins < 40.0;
            }

            // Foto / Musica: usa la stessa logica di ExtsForCategory
            var allowed = new HashSet<string>(ExtsForCategory(_selCat), StringComparer.OrdinalIgnoreCase);
            return allowed.Contains(ext);
        }

        private void UpdateRecentsFromScanFor(string category, List<FileInfo> scanned)
        {
            var paths = scanned
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .Select(fi => fi.FullName)
                .Take(200)
                .ToList();

            _recents.Set(category, paths);
        }

        // ------------ SCAN / FILTER / RENDER GRID ------------
        private void RefreshContent()
        {
            try { _thumbCts?.Cancel(); } catch { }
            try { _scanCts?.Cancel(); } catch { }

            ResetProgressiveRender();

            _scanCts = new CancellationTokenSource();
            _thumbCts = new CancellationTokenSource();
            var ct = _scanCts.Token;

            string catNow = _selCat;
            string srcNow = _selSrc;
            var thisScanCts = _scanCts;

            _grid.SuspendLayout();
            _grid.Controls.Clear();
            _grid.ResumeLayout();

            // ----- sorgente URL: solo il pannellino link -----
            if (_selSrc == "URL")
            {
                HideMask();
                _grid.Controls.Clear();

                _urlPane ??= new UrlPane(url => SafeOpen(url));
                _urlPane.Dock = DockStyle.Top;

                var host = new Panel
                {
                    Dock = DockStyle.Top,
                    Height = _urlPane.Height + 40,
                    BackColor = Color.Black,
                    Padding = new Padding(0, 8, 0, 0)
                };
                host.Controls.Add(_urlPane);
                host.Controls.Add(new InfoRow("Supportati link diretti HTTP/HTTPS (anche HLS .m3u8)."));

                _grid.Controls.Add(host);
                _grid.UpdateThemedScrollbar();
                _header.Invalidate(true); _header.Update();
                _grid.Invalidate(true); _grid.Update();
                return;
            }
            // ----- sorgente DLNA -----
            else if (_selSrc == "Rete domestica")
            {
                _grid.Controls.Clear();
                _grid.UpdateThemedScrollbar();

                _dlnaCts?.Cancel();
                _dlnaCts = new CancellationTokenSource();
                var ctDlna = _dlnaCts.Token;

                ShowMask("Ricerca dispositivi DLNA…");
                Task.Run(async () =>
                {
                    List<DlnaDevice> devs;
                    try { devs = await DiscoverDlnaWithRetry(ctDlna); }
                    catch { devs = new List<DlnaDevice>(); }

                    if (IsDisposed || ctDlna.IsCancellationRequested) return;
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || ctDlna.IsCancellationRequested) return;
                        HideMask();
                        RenderDlnaDeviceList(devs);
                    }));
                }, ctDlna);
                return;
            }

            var exts = ExtsForCategory(_selCat);
            var rootsList = AllRootsForCategory(_selCat).ToList();

            // se siamo su "Il mio computer" e non ci sono cartelle configurate
            // per Film/Video/Foto/Musica → mostra un riquadro di call-to-action
            if (string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase)
                && IsLocalLibraryCategory(_selCat)
                && rootsList.Count == 0)
            {
                HideMask();
                ShowInlineRootsCallToAction(_selCat);
                return;
            }

            // ----- 1) prova a caricare SUBITO dall'indice persistente -----
            List<FileInfo> initial = new();
            bool useIndex = string.Equals(srcNow, "Il mio computer", StringComparison.OrdinalIgnoreCase)
                            && IsLocalLibraryCategory(catNow);

            if (useIndex)
            {
                var stored = _libraryIndex.GetPaths(catNow);
                if (stored.Count > 0)
                {
                    var existing = new List<string>();
                    foreach (var p in stored)
                    {
                        try
                        {
                            if (File.Exists(p))
                            {
                                existing.Add(p);
                                initial.Add(new FileInfo(p));
                            }
                        }
                        catch { }
                    }

                    // ripulisci l'indice da roba che non esiste più
                    if (existing.Count != stored.Count)
                        _libraryIndex.ReplacePaths(catNow, existing);
                }
            }

            if (initial.Count > 0)
            {
                lock (_cacheLock)
                    _cache = initial;

                ApplyFilterAndRender();
                HideMask(); // l'utente vede SUBITO qualcosa
            }
            else
            {
                if (useIndex)
                {
                    // niente mask bloccante per la libreria locale:
                    // mostriamo solo una riga "Indicizzazione..." e partiamo in background
                    HideMask();

                    _grid.Controls.Clear();
                    _grid.Controls.Add(new InfoRow("Indicizzazione della libreria in corso…"));
                    _grid.UpdateThemedScrollbar();
                }
                else
                {
                    // per le altre sorgenti (es. DLNA) la mask ha ancora senso
                    ShowMask("Ricerca in corso…");
                }
            }

            var roots = rootsList;

            // ----- 2) in background fai la scansione completa e aggiorna indice + UI -----
            Task.Run(() =>
            {
                var list = new List<FileInfo>();
                try
                {
                    foreach (var root in roots)
                    {
                        if (ct.IsCancellationRequested) break;
                        if (IsSystemPath(root)) continue;

                        try
                        {
                            foreach (var f in EnumerateFilesSafe(root, exts, ct))
                            {
                                if (ct.IsCancellationRequested) break;
                                try
                                {
                                    var fi = new FileInfo(f);
                                    if (fi.Exists)
                                        list.Add(fi);
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                if (ct.IsCancellationRequested) return;

                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed) return;
                        if (!ReferenceEquals(_scanCts, thisScanCts)) return;
                        if (_selCat != catNow || _selSrc != srcNow) return;

                        lock (_cacheLock)
                            _cache = list;

                        if (useIndex)
                            _libraryIndex.ReplacePaths(catNow, list.Select(fi => fi.FullName));

                        UpdateRecentsFromScanFor(catNow, list);

                        ApplyFilterAndRender();
                        HideMask();

                        _grid.UpdateThemedScrollbar();
                        _header.Invalidate(true); _header.Update();
                        _grid.Invalidate(true); _grid.Update();
                    }));
                }
            }, ct);
        }

        private CancellationTokenSource GetOrNewThumbCts()
        {
            if (_thumbCts == null || _thumbCts.IsCancellationRequested)
                _thumbCts = new CancellationTokenSource();
            return _thumbCts;
        }

        private void ApplyFilterAndRender()
        {
            var filtered = BuildFilteredList();
            StartProgressiveRender(filtered);
        }

        private List<FileInfo> BuildFilteredList()
        {
            List<FileInfo> src;
            lock (_cacheLock) src = _cache.ToList();

            // preferiti = usa lista preferiti come sorgente
            if (string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase))
            {
                var favs = _favs.All()
                    .Where(File.Exists)
                    .Select(p =>
                    {
                        try { return new FileInfo(p); }
                        catch { return null; }
                    })
                    .Where(fi => fi != null)
                    .Cast<FileInfo>()
                    .ToList();
                src = favs;
            }

            // filtro testo (nome + percorso)
            string q = (_search.Text ?? "").Trim().ToLowerInvariant();
            if (q.Length > 0)
            {
                var tokens = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                src = src.Where(fi =>
                {
                    string name = fi.Name.ToLowerInvariant();
                    string path = (fi.DirectoryName ?? "").ToLowerInvariant();
                    foreach (var t in tokens)
                        if (!(name.Contains(t) || path.Contains(t)))
                            return false;
                    return true;
                }).ToList();
            }

            string catLower = _selCat.ToLowerInvariant();
            bool isFilm = catLower == "film";
            bool isVideo = catLower == "video";

            if (isFilm || isVideo)
            {
                // split Film/Video IN BASE ALLA DURATA
                src = src.Where(fi =>
                {
                    var ext = (Path.GetExtension(fi.FullName) ?? "").ToLowerInvariant();
                    bool isMovieContainer = ext == ".mkv" || ext == ".mp4";

                    if (isFilm)
                    {
                        // FILM: solo mkv/mp4 con durata >= 40 minuti
                        if (!isMovieContainer)
                            return false;

                        var dur = GetDurationMinutesCached(fi.FullName);
                        if (!dur.HasValue)
                            return false;          // se non so la durata lo tengo fuori da "Film"

                        return dur.Value >= 40.0;
                    }

                    // VIDEO: tutti i video, ma:
                    // - i mkv/mp4 vanno qui solo se < 40 min o senza durata
                    // - gli altri container video sempre qui
                    if (!IsAnyVideoExtension(ext))
                        return false;

                    if (!isMovieContainer)
                        return true;

                    var d = GetDurationMinutesCached(fi.FullName);
                    if (!d.HasValue)
                        return true;               // durata sconosciuta → lo consideriamo "video"

                    return d.Value < 40.0;
                }).ToList();
            }
            else
            {
                // altre categorie: filtro solo per estensione
                var allowed = new HashSet<string>(ExtsForCategory(_selCat), StringComparer.OrdinalIgnoreCase);
                src = src.Where(fi => allowed.Contains(Path.GetExtension(fi.FullName))).ToList();
            }

            // filtro chip estensione singola
            if (!string.Equals(_selExt, "Tutte", StringComparison.OrdinalIgnoreCase))
            {
                src = src.Where(fi =>
                        string.Equals(
                            Path.GetExtension(fi.FullName),
                            _selExt,
                            StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // sort
            src = _sortIndex switch
            {
                1 => src.OrderBy(fi => fi.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                2 => src.OrderByDescending(fi => fi.Length).ToList(),
                _ => src.OrderByDescending(fi => fi.LastWriteTimeUtc).ToList()
            };

            return src.Take(250).ToList();
        }

        private void StartProgressiveRender(List<FileInfo> list)
        {
            if (_grid.FlowDirection != FlowDirection.LeftToRight || !_grid.WrapContents)
            {
                _grid.FlowDirection = FlowDirection.LeftToRight;
                _grid.WrapContents = true;
            }

            _progressiveTimer.Stop();
            _progressiveList = list;
            _progressivePos = 0;
            _progressiveThumbToken = GetOrNewThumbCts().Token;

            _grid.SuspendLayout();
            _grid.Controls.Clear();
            _grid.ResumeLayout();

            if (list.Count == 0)
            {
                _grid.Controls.Add(new InfoRow("Nessun elemento corrisponde ai filtri."));
                _grid.UpdateThemedScrollbar();
                _grid.Invalidate(true);
                _grid.Update();
                return;
            }

            _progressiveTimer.Start();
        }

        private void ProgressiveTick()
        {
            if (_progressivePos >= _progressiveList.Count)
            {
                _progressiveTimer.Stop();
                _grid.UpdateThemedScrollbar();
                _grid.Invalidate(true);
                _grid.Update();
                return;
            }

            int cardsPerRow = EstimateCardsPerRow();
            if (cardsPerRow < 1) cardsPerRow = 1;

            for (int i = 0; i < cardsPerRow && _progressivePos < _progressiveList.Count; i++)
            {
                var fi = _progressiveList[_progressivePos++];
                var path = fi.FullName;

                var card = new FileCard(
                    path,
                    showFavorite: true,
                    favInit: _favs.IsFav(path),
                    onFavToggle: (p, fav) => _favs.Set(p, fav),
                    clickOpen: () => SafeOpen(path),
                    cardWidth: 300,
                    cardHeight: 236,
                    imgHeight: 170
                );

                // placeholder immediata
                var cat = CategoryFromExt((Path.GetExtension(path) ?? "").ToLowerInvariant());
                var phBmp = GetCategoryPlaceholder(cat, 520);
                card.SetInitialPlaceholder(phBmp);

                _grid.Controls.Add(card);

                // thumb async
                card.BeginThumbLoad(_progressiveThumbToken);
            }

            _grid.UpdateThemedScrollbar();
            _grid.Invalidate(true);
            _grid.Update();
        }

        // quante card (300 + margini ~20 => ~320px) entrano in una riga disponibile
        private int EstimateCardsPerRow()
        {
            int w = _grid.ClientSize.Width;
            if (w <= 0) w = _grid.Width;
            if (w <= 0) return 2;

            int usable = w - _grid.Padding.Left - _grid.Padding.Right;
            if (usable <= 0) usable = w;

            int per = usable / 320; // ~320px per card
            if (per < 1) per = 1;

            return per;
        }

        // ------------ FS / categorie / estensioni ------------
        private static bool IsLocalLibraryCategory(string cat)
        {
            return string.Equals(cat, "Film", StringComparison.OrdinalIgnoreCase)
                || string.Equals(cat, "Video", StringComparison.OrdinalIgnoreCase)
                || string.Equals(cat, "Foto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(cat, "Musica", StringComparison.OrdinalIgnoreCase);
        }

        private IEnumerable<string> AllRootsForCategory(string cat)
        {
            // 1) prima prova a usare le cartelle salvate per quella categoria
            var userRoots = _roots.Get(cat);
            if (userRoots.Count > 0)
                return userRoots;

            // 2) se siamo su "Il mio computer" e categoria Film/Video/Foto/Musica
            //    e non ci sono cartelle salvate → niente fallback automatico:
            //    la UI mostrerà la paginetta di configurazione.
            if (string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase)
                && IsLocalLibraryCategory(cat))
            {
                return Array.Empty<string>();
            }

            // 3) per le altre sorgenti/categorie (o se vuoi mantenere compatibilità)
            //    usiamo la vecchia logica automatica
            return DefaultRootsForCategory(cat);
        }
        private void ShowInlineRootsCallToAction(string category)
        {
            try { _thumbCts?.Cancel(); } catch { }
            try { _scanCts?.Cancel(); } catch { }

            ResetProgressiveRender();

            // layout verticale, ma sempre dentro la zona di "Tutti i file"
            _grid.FlowDirection = FlowDirection.TopDown;
            _grid.WrapContents = false;

            _grid.SuspendLayout();
            _grid.Controls.Clear();

            // area utile interna alla grid (tolti i padding)
            int maxContentWidth = _grid.ClientSize.Width - _grid.Padding.Left - _grid.Padding.Right;
            if (maxContentWidth < 480)
                maxContentWidth = 480;

            // card più grande, ma non a tutta larghezza
            int cardWidth = Math.Min(720, maxContentWidth);

            // centrata orizzontalmente rispetto all'area utile
            int leftMargin = Math.Max(0, (maxContentWidth - cardWidth) / 2);

            var host = new Panel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                // margine top un po' generoso per staccarla dal titolo "Tutti i file"
                Margin = new Padding(leftMargin, 60, 0, 40),
                Padding = new Padding(0),
                Width = cardWidth
            };
            _grid.Controls.Add(host);

            var card = new Panel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Padding = new Padding(22, 18, 22, 18),
                Width = cardWidth,
                Margin = new Padding(0)
            };
            card.Paint += (_, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                var rect = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
                using var path = GraphicsUtil.RoundRect(rect, 10);

                using (var bg = new SolidBrush(Theme.PanelAlt))
                    g.FillPath(bg, path);

                using (var pen = new Pen(Theme.Border))
                    g.DrawPath(pen, path);
            };
            host.Controls.Add(card);

            // riga principale: icona a sinistra, testo + bottoni in colonna a destra
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            card.Controls.Add(row);

            // icona rotonda più grande
            var iconHost = new Panel
            {
                Width = 68,
                Height = 68,
                Margin = new Padding(0, 0, 18, 0),
                BackColor = Color.Transparent
            };
            iconHost.Paint += (_, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, iconHost.Width - 1, iconHost.Height - 1);

                using var br = new SolidBrush(Color.FromArgb(40, Theme.Accent));
                using var pen = new Pen(Theme.Accent, 1.5f);
                g.FillEllipse(br, r);
                g.DrawEllipse(pen, r);

                // folder stilizzata
                using var f = new Font("Segoe UI Symbol", 26f, FontStyle.Regular);
                var txt = "📁";
                var sz = g.MeasureString(txt, f);
                g.DrawString(
                    txt,
                    f,
                    Brushes.White,
                    r.Left + (r.Width - sz.Width) / 2f,
                    r.Top + (r.Height - sz.Height) / 2f - 1);
            };
            row.Controls.Add(iconHost);

            // colonna testo + pulsanti
            var col = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Padding = new Padding(0),
                Margin = new Padding(0),
                Width = cardWidth - iconHost.Width - 18 - 22 // icona + gap + padding destro
            };
            row.Controls.Add(col);

            string lower = category.ToLowerInvariant();
            string niceName = char.ToUpper(lower[0]) + lower.Substring(1);

            var lblTitle = new Label
            {
                Text = $"Nessuna cartella configurata per {niceName}.",
                AutoSize = true,
                MaximumSize = new Size(col.Width, 0),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 12.0f),
                Margin = new Padding(0, 0, 0, 3)
            };
            col.Controls.Add(lblTitle);

            var lblSub = new Label
            {
                Text = $"Aggiungi una o più cartelle o dischi (es. \"D:\\\") da cui caricare i tuoi {lower}. " +
                       "Puoi modificarle in qualsiasi momento dal pulsante \"Cartelle…\" in alto.",
                AutoSize = true,
                MaximumSize = new Size(col.Width, 0),
                ForeColor = Theme.SubtleText,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 10.0f),
                Margin = new Padding(0, 0, 0, 10)
            };
            col.Controls.Add(lblSub);

            // riga pulsanti sotto al testo
            var btnRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Padding = new Padding(0),
                Margin = new Padding(0, 4, 0, 0)
            };
            col.Controls.Add(btnRow);

            var btnAdd = new FlatButton("Aggiungi cartella…", FlatButton.Variant.Primary)
            {
                Width = 190,
                Height = 32,
                Margin = new Padding(0, 0, 10, 0)
            };
            btnAdd.Click += (_, __) => AddFolderForCurrentCategory();
            btnRow.Controls.Add(btnAdd);

            var btnManage = new FlatButton("Gestisci cartelle", FlatButton.Variant.Secondary)
            {
                Width = 170,
                Height = 32,
                Margin = new Padding(0, 0, 0, 0)
            };
            btnManage.Click += (_, __) => ManageFoldersForCurrentCategory();
            btnRow.Controls.Add(btnManage);

            _grid.ResumeLayout(true);
            _grid.UpdateThemedScrollbar();
        }

        private void ShowManageRootsUi(string category, bool firstRun)
        {
            try { _thumbCts?.Cancel(); } catch { }
            try { _scanCts?.Cancel(); } catch { }

            ResetProgressiveRender();

            // la pagina "Cartelle..." è una colonna verticale
            _grid.FlowDirection = FlowDirection.TopDown;
            _grid.WrapContents = false;

            _grid.SuspendLayout();
            _grid.Controls.Clear();

            int hostWidth = Math.Max(520, _grid.ClientSize.Width - _grid.Padding.Left - _grid.Padding.Right);
            if (hostWidth > 860) hostWidth = 860;

            // contenitore principale
            var host = new Panel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Black,
                Margin = new Padding(0, 16, 0, 16),
                Padding = new Padding(0),
                Width = hostWidth
            };
            _grid.Controls.Add(host);

            // stack verticale interno per tenere in ordine header, lista, pulsanti
            var stack = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                BackColor = Color.Black,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            host.Controls.Add(stack);

            // --- titolo + sotto-testo ---
            string lower = category.ToLowerInvariant();
            string headerText = firstRun
                ? $"Scegli una o più cartelle o interi dischi dove hai i tuoi {lower}."
                : $"Percorsi per la categoria {category} (sorgente \"Il mio computer\").";

            var lblHeader = new Label
            {
                Text = headerText,
                AutoSize = true,
                MaximumSize = new Size(hostWidth, 0),
                ForeColor = Color.White,
                BackColor = Color.Black,
                Font = new Font("Segoe UI Semibold", 12f),
                Padding = new Padding(4, 0, 4, 4),
                Margin = new Padding(0, 0, 0, 2)
            };
            stack.Controls.Add(lblHeader);

            var lblSub = new Label
            {
                Text = "Puoi indicare sia cartelle singole che dischi interi (es. \"D:\\\"). Le cartelle di sistema vengono ignorate automaticamente durante la scansione.",
                AutoSize = true,
                MaximumSize = new Size(hostWidth, 0),
                ForeColor = Theme.SubtleText,
                BackColor = Color.Black,
                Font = new Font("Segoe UI", 9.5f),
                Padding = new Padding(4, 0, 4, 8),
                Margin = new Padding(0, 0, 0, 6)
            };
            stack.Controls.Add(lblSub);

            // --- lista percorsi configurati ---
            var roots = _roots.Get(category);

            if (roots.Count == 0 && !firstRun)
            {
                var lblEmpty = new Label
                {
                    Text = "Nessun percorso configurato per questa categoria.",
                    AutoSize = true,
                    MaximumSize = new Size(hostWidth, 0),
                    ForeColor = Theme.SubtleText,
                    BackColor = Color.Black,
                    Padding = new Padding(4, 4, 4, 8),
                    Margin = new Padding(0, 0, 0, 4)
                };
                stack.Controls.Add(lblEmpty);
            }

            foreach (var r in roots)
            {
                var row = new Panel
                {
                    Height = 40,
                    Width = hostWidth - 16,
                    Margin = new Padding(0, 2, 0, 2),
                    Padding = new Padding(8, 8, 8, 8),
                    BackColor = Theme.PanelAlt
                };

                var lblPath = new Label
                {
                    Text = r,
                    Dock = DockStyle.Fill,
                    AutoEllipsis = true,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = Theme.Text,
                    BackColor = Color.Transparent
                };

                var btnRemove = new FlatButton("Rimuovi", FlatButton.Variant.Secondary)
                {
                    Dock = DockStyle.Right,
                    Width = 100,
                    Height = 26
                };
                btnRemove.Click += (_, __) =>
                {
                    _roots.Remove(category, r);
                    _libraryIndex.ReplacePaths(category, Array.Empty<string>());
                    // ricarica la pagina cartelle con il percorso in meno
                    ShowManageRootsUi(category, firstRun);
                };

                row.Controls.Add(btnRemove);
                row.Controls.Add(lblPath);
                stack.Controls.Add(row);
            }

            // --- testo per aggiungere nuove cartelle/dischi ---
            var lblAdd = new Label
            {
                Text = "Aggiungi cartella o intero disco:",
                AutoSize = true,
                MaximumSize = new Size(hostWidth, 0),
                ForeColor = Theme.SubtleText,
                BackColor = Color.Black,
                Margin = new Padding(4, 12, 4, 4)
            };
            stack.Controls.Add(lblAdd);

            // bottone "Scegli cartella / disco…"
            var btnPick = new FlatButton("Scegli cartella / disco…", FlatButton.Variant.Primary)
            {
                Width = 220,
                Height = 32,
                Margin = new Padding(4, 4, 4, 0)
            };

            btnPick.Click += (_, __) =>
            {
                using var fbd = new FolderBrowserDialog
                {
                    Description = $"Seleziona una cartella o un disco per la categoria {category.ToLowerInvariant()}.",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = false,
                    RootFolder = Environment.SpecialFolder.MyComputer
                };

                var owner = FindForm();
                var result = owner != null ? fbd.ShowDialog(owner) : fbd.ShowDialog();
                if (result != DialogResult.OK)
                    return;

                var raw = fbd.SelectedPath ?? string.Empty;
                var path = NormalizeRootPath(raw);

                if (string.IsNullOrWhiteSpace(path))
                {
                    // seconda chance in caso di robe tipo "D" / "D:"
                    path = NormalizeRootPath(raw.Length >= 2 ? raw.Substring(0, 2) : raw);
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    SystemSounds.Beep.Play();
                    return;
                }

                _roots.Add(category, path);

                _libraryIndex.ReplacePaths(category, Array.Empty<string>());

                ShowManageRootsUi(category, firstRun: false);
            };

            stack.Controls.Add(btnPick);

            // --- barra azioni in fondo: Inizia/Chiudi ---
            var actions = new Panel
            {
                Height = 48,
                Width = hostWidth,
                Margin = new Padding(0, 16, 0, 0),
                BackColor = Color.Black
            };

            var btnDone = new FlatButton(firstRun ? "Inizia" : "Chiudi", FlatButton.Variant.Secondary)
            {
                Width = 120,
                Height = 32,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(actions.Width - 120, 8)
            };
            btnDone.Click += (_, __) =>
            {
                // azzero l'indice della categoria e faccio ripartire la scansione
                _libraryIndex.ReplacePaths(category, Array.Empty<string>());
                RefreshContent();
            };

            actions.Controls.Add(btnDone);
            stack.Controls.Add(actions);

            _grid.ResumeLayout(true);
            _grid.UpdateThemedScrollbar();
            HideMask();
        }

        private void ShowRootsSetupPage(string category)
        {
            ShowManageRootsUi(category, firstRun: true);
        }

        private static IEnumerable<string> DefaultRootsForCategory(string cat)
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddIf(string? p)
            {
                if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                    roots.Add(p);
            }

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            var pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var downloads = Path.Combine(user, "Downloads");

            switch (cat.ToLowerInvariant())
            {
                case "film":
                case "video":
                    AddIf(videos); AddIf(desktop); AddIf(downloads); AddIf(docs);
                    break;
                case "foto":
                    AddIf(pics); AddIf(desktop); AddIf(downloads);
                    break;
                case "musica":
                    AddIf(music); AddIf(desktop); AddIf(downloads);
                    break;
                case "preferiti":
                    AddIf(videos); AddIf(music); AddIf(pics); AddIf(desktop); AddIf(downloads);
                    break;
                default:
                    AddIf(desktop);
                    break;
            }

            return roots;
        }

        private static bool IsSystemPath(string path)
        {
            string p = path.TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
            return p.EndsWith(@":\windows")
                || p.Contains(@":\program files")
                || p.Contains(@":\program files (x86)")
                || p.Contains(@":\programdata")
                || p.Contains(@":\appdata")
                || p.Contains(@":\users\all users")
                || p.Contains(@"\$recycle.bin")
                || p.Contains(@"\system volume information");
        }

        private static IEnumerable<string> EnumerateFilesSafe(string root, string[] allowedExts, CancellationToken ct)
        {
            // normalizziamo comunque, giusto per sicurezza (D, D:, D\ → D:\)
            var normalizedRoot = NormalizeRootPath(root);
            if (string.IsNullOrWhiteSpace(normalizedRoot))
                yield break;

            var allow = new HashSet<string>(allowedExts.Select(e => e.ToLowerInvariant()));
            var stack = new Stack<string>();
            stack.Push(normalizedRoot);

            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested)
                    yield break;

                var dir = stack.Pop();

                DirectoryInfo? di = null;
                try { di = new DirectoryInfo(dir); } catch { }
                if (di == null) continue;

                // ---- NON saltare la root del disco anche se marcata System/Hidden ----
                bool isDriveRoot = di.Parent == null; // per C:\, D:\ ecc.

                try
                {
                    // salta solo directory di sistema NON root
                    if (!isDriveRoot && (di.Attributes & FileAttributes.System) != 0)
                        continue;
                    // i folder Hidden li lasciamo passare: filtriamo per nome più sotto
                }
                catch
                {
                    // se non riusciamo a leggere gli attributi proviamo comunque a scendere
                }

                // --- sottocartelle ---
                IEnumerable<string> subdirs = Array.Empty<string>();
                try { subdirs = Directory.EnumerateDirectories(dir); } catch { }

                foreach (var sd in subdirs)
                {
                    if (ct.IsCancellationRequested) yield break;

                    var name = Path.GetFileName(sd).ToLowerInvariant();

                    // blacklist di cartelle ultra-sistemiche
                    if (name is "windows"
                             or "program files"
                             or "program files (x86)"
                             or "$recycle.bin"
                             or "system volume information")
                        continue;

                    stack.Push(sd);
                }

                // --- file nella dir corrente ---
                IEnumerable<string> files = Array.Empty<string>();
                try { files = Directory.EnumerateFiles(dir); } catch { }

                foreach (var f in files)
                {
                    if (ct.IsCancellationRequested) yield break;

                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (allow.Contains(ext))
                        yield return f;
                }
            }
        }

        // set di estensioni comuni per Film/Video
        private static readonly string[] FilmMovieExts = { ".mkv", ".mp4" };

        private static readonly string[] OtherVideoExts =
        {
            ".m4v", ".mov", ".avi", ".wmv",
            ".webm", ".flv", ".m2ts", ".ts", ".iso"
        };

        private static readonly HashSet<string> AllVideoExtsSet =
            new HashSet<string>(
                FilmMovieExts.Concat(OtherVideoExts),
                StringComparer.OrdinalIgnoreCase);

        private static bool IsAnyVideoExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext))
                return false;
            return AllVideoExtsSet.Contains(ext);
        }
        private static string[] ExtsForCategory(string cat)
        {
            cat = cat.ToLowerInvariant();

            // FILM = solo container "lunghi": mkv + mp4 (la durata la filtriamo dopo)
            if (cat == "film")
                return FilmMovieExts;

            // VIDEO = tutti i formati video, inclusi mkv/mp4
            if (cat == "video")
                return AllVideoExtsSet.ToArray();

            if (cat == "foto")
                return new[] {
                    ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".webp"
                };

            if (cat == "musica")
                return new[] {
                    ".mp3", ".flac", ".mka", ".aac", ".ogg", ".wav", ".wma",
                    ".m4a", ".opus", ".dts", ".ac3", ".eac3"
                };

            return Array.Empty<string>();
        }

        // ------------ RECENTS STORE ------------
        private sealed class RecentsStore
        {
            private sealed class Model
            {
                public Dictionary<string, List<string>> Items { get; set; } =
                    new(StringComparer.OrdinalIgnoreCase);
            }

            private readonly string _file;
            private Model _data;

            public RecentsStore()
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025");
                Directory.CreateDirectory(folder);

                _file = Path.Combine(folder, "recents.json");
                _data = Load();
            }

            public IEnumerable<string> TryGet(string category)
            {
                if (_data.Items.TryGetValue(category, out var list))
                    return list;
                return Array.Empty<string>();
            }

            public void Set(string category, List<string> paths)
            {
                _data.Items[category] = paths
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Save();
            }

            public void PruneToCategory(string category, string[] allowedExts)
            {
                if (!_data.Items.TryGetValue(category, out var list))
                    return;

                var allow = new HashSet<string>(allowedExts, StringComparer.OrdinalIgnoreCase);

                var filtered = list
                    .Where(p =>
                    {
                        var ext = Path.GetExtension(p) ?? "";
                        return allow.Contains(ext);
                    })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(200)
                    .ToList();

                _data.Items[category] = filtered;
                Save();
            }

            private Model Load()
            {
                try
                {
                    if (File.Exists(_file))
                    {
                        var json = File.ReadAllText(_file, Encoding.UTF8);
                        var m = JsonSerializer.Deserialize<Model>(json);
                        if (m != null) return m;
                    }
                }
                catch { }
                return new Model();
            }

            private void Save()
            {
                try
                {
                    var json = JsonSerializer.Serialize(
                        _data,
                        new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_file, json, Encoding.UTF8);
                }
                catch { }
            }
        }

        // ------------ FAVORITES STORE ------------
        private sealed class FavoritesStore
        {
            private sealed class Model
            {
                public HashSet<string> Paths { get; set; } =
                    new(StringComparer.OrdinalIgnoreCase);
            }

            private readonly string _file;
            private Model _data;

            public FavoritesStore()
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025");
                Directory.CreateDirectory(folder);

                _file = Path.Combine(folder, "favorites.json");
                _data = Load();
            }

            public bool IsFav(string path) => _data.Paths.Contains(path);

            public IEnumerable<string> All() => _data.Paths.ToArray();

            public void Set(string path, bool fav)
            {
                if (fav) _data.Paths.Add(path);
                else _data.Paths.Remove(path);

                Save();
            }

            private Model Load()
            {
                try
                {
                    if (File.Exists(_file))
                    {
                        var json = File.ReadAllText(_file, Encoding.UTF8);
                        var m = JsonSerializer.Deserialize<Model>(json);
                        if (m != null) return m;
                    }
                }
                catch { }
                return new Model();
            }

            private void Save()
            {
                try
                {
                    var json = JsonSerializer.Serialize(
                        _data,
                        new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_file, json, Encoding.UTF8);
                }
                catch { }
            }
        }

        // ------------ ROOTS STORE (cartelle per Film/Video/Foto/Musica) ------------
        private sealed class RootsStore
        {
            private sealed class Model
            {
                public Dictionary<string, List<string>> Roots { get; set; } =
                    new(StringComparer.OrdinalIgnoreCase);
            }

            private readonly string _file;
            private Model _data;

            public RootsStore()
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025");
                Directory.CreateDirectory(folder);

                _file = Path.Combine(folder, "libraryRoots.json");
                _data = Load();
            }

            // SEMPRE: restituisce path normalizzati (D:, D, D\ → D:\) e senza doppioni
            public List<string> Get(string category)
            {
                if (_data.Roots.TryGetValue(category, out var list))
                {
                    return list
                        .Select(p => MediaLibraryPage.NormalizeRootPath(p))
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                return new List<string>();
            }

            public void Set(string category, List<string> roots)
            {
                _data.Roots[category] = roots
                    .Select(MediaLibraryPage.NormalizeRootPath)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                Save();
            }

            public void Add(string category, string root)
            {
                root = MediaLibraryPage.NormalizeRootPath(root);
                if (string.IsNullOrWhiteSpace(root))
                    return;

                if (!_data.Roots.TryGetValue(category, out var list))
                {
                    list = new List<string>();
                    _data.Roots[category] = list;
                }

                // niente doppioni, anche se in json c’era scritto "D:" e ora passi "D:\"
                if (!list.Any(p =>
                        string.Equals(MediaLibraryPage.NormalizeRootPath(p), root, StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(root);
                    Save();
                }
            }

            public void Remove(string category, string root)
            {
                root = MediaLibraryPage.NormalizeRootPath(root);

                if (!_data.Roots.TryGetValue(category, out var list))
                    return;

                list.RemoveAll(p =>
                    string.Equals(MediaLibraryPage.NormalizeRootPath(p), root, StringComparison.OrdinalIgnoreCase));

                Save();
            }

            private Model Load()
            {
                try
                {
                    if (File.Exists(_file))
                    {
                        var json = File.ReadAllText(_file, Encoding.UTF8);
                        var m = JsonSerializer.Deserialize<Model>(json);
                        if (m != null)
                        {
                            var normalized = new Model();
                            foreach (var kv in m.Roots)
                            {
                                var list = kv.Value
                                    .Select(p => MediaLibraryPage.NormalizeRootPath(p))
                                    .Where(p => !string.IsNullOrWhiteSpace(p))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();

                                if (list.Count > 0)
                                    normalized.Roots[kv.Key] = list;
                            }

                            return normalized;
                        }
                    }
                }
                catch { }
                return new Model();
            }

            private void Save()
            {
                try
                {
                    var json = JsonSerializer.Serialize(
                        _data,
                        new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_file, json, Encoding.UTF8);
                }
                catch { }
            }
        }

        // ------------ LIBRARY INDEX STORE (tutto quello che abbiamo indicizzato per categoria) ------------
        private sealed class LibraryIndexStore
        {
            private sealed class CategoryIndex
            {
                public DateTime LastScanUtc { get; set; }
                public List<string> Paths { get; set; } = new();
            }

            private sealed class Model
            {
                public Dictionary<string, CategoryIndex> Categories { get; set; } =
                    new(StringComparer.OrdinalIgnoreCase);
            }

            private readonly string _file;
            private readonly object _lock = new();
            private Model _data;

            public LibraryIndexStore()
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025");
                Directory.CreateDirectory(folder);

                _file = Path.Combine(folder, "libraryIndex.json");
                _data = Load();
            }

            public List<string> GetPaths(string category)
            {
                lock (_lock)
                {
                    if (_data.Categories.TryGetValue(category, out var idx))
                        return idx.Paths.ToList();
                }
                return new List<string>();
            }

            public void ReplacePaths(string category, IEnumerable<string> paths)
            {
                lock (_lock)
                {
                    if (!_data.Categories.TryGetValue(category, out var idx))
                    {
                        idx = new CategoryIndex();
                        _data.Categories[category] = idx;
                    }

                    idx.Paths = paths
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    idx.LastScanUtc = DateTime.UtcNow;
                    SaveNoLock();
                }
            }

            public void RemoveMissing(string category)
            {
                lock (_lock)
                {
                    if (!_data.Categories.TryGetValue(category, out var idx))
                        return;

                    var filtered = idx.Paths
                        .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (filtered.Count != idx.Paths.Count)
                    {
                        idx.Paths = filtered;
                        SaveNoLock();
                    }
                }
            }

            private Model Load()
            {
                try
                {
                    if (File.Exists(_file))
                    {
                        var json = File.ReadAllText(_file, Encoding.UTF8);
                        var m = JsonSerializer.Deserialize<Model>(json);
                        if (m != null) return m;
                    }
                }
                catch { }
                return new Model();
            }

            private void SaveNoLock()
            {
                try
                {
                    var json = JsonSerializer.Serialize(
                        _data,
                        new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_file, json, Encoding.UTF8);
                }
                catch { }
            }
        }

        // ------------ ROW informativa / vuoto / messaggi ------------
        private sealed class InfoRow : Panel
        {
            public InfoRow(string text)
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint, true);

                Height = 40;
                Dock = DockStyle.Top;
                BackColor = Color.Black;

                Controls.Add(new Label
                {
                    Text = text,
                    Dock = DockStyle.Fill,
                    ForeColor = Theme.SubtleText,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 11f),
                    BackColor = Color.Black
                });
            }
        }

        // ------------ HEADER BAR + CHIP + SEARCH + BUTTON ------------

        private sealed class HeaderBar : Panel
        {
            public HeaderBar()
            {
                BackColor = Theme.PanelAlt;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw, true);
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.PanelAlt);
            }
        }

        private sealed class HeaderActionButton : Control
        {
            private bool _hover;
            private bool _down;
            public string BtnText { get; set; }

            public HeaderActionButton(string text)
            {
                BtnText = text;
                Cursor = Cursors.Hand;
                Size = new Size(148, 32);
                TabStop = false;

                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.SupportsTransparentBackColor, true);

                BackColor = Color.Transparent;

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; _down = false; Invalidate(); };
                MouseDown += (_, __) => { _down = true; Invalidate(); };
                MouseUp += (_, __) =>
                {
                    _down = false;
                    Invalidate();
                    OnClick(EventArgs.Empty);
                };
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.PanelAlt);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);

                using var gp = GraphicsUtil.RoundRect(rect, 6);

                Color cTop = Theme.Accent;
                Color cBot = Color.FromArgb(180, Theme.Accent);
                if (_down)
                {
                    cTop = ControlPaint.Dark(cTop);
                    cBot = ControlPaint.Dark(cBot);
                }
                else if (_hover)
                {
                    cTop = ControlPaint.Light(cTop);
                }

                using (var lg = new LinearGradientBrush(rect, cTop, cBot, LinearGradientMode.Vertical))
                    g.FillPath(lg, gp);

                using var f = new Font("Segoe UI Semibold", 10.5f);
                TextRenderer.DrawText(
                    g,
                    BtnText,
                    f,
                    rect,
                    Color.White,
                    TextFormatFlags.HorizontalCenter
                  | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.EndEllipsis);
            }
        }

        private sealed class Chip : Control
        {
            private bool _hover, _down;
            public Chip(string text)
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.UserPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.SupportsTransparentBackColor
                       | ControlStyles.ResizeRedraw, true);

                Font = new Font("Segoe UI", 10.5f);
                ForeColor = Color.White;
                BackColor = Color.Transparent;
                Cursor = Cursors.Hand;
                Text = text;
                Height = 32;
                Width = 160;
                Margin = new Padding(0);

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; _down = false; Invalidate(); };
                MouseDown += (_, __) => { _down = true; Invalidate(); };
                MouseUp += (_, __) =>
                {
                    _down = false;
                    Invalidate();
                    OnClick(EventArgs.Empty);
                };
            }

            public void AutoSizeToText()
            {
                var w = TextRenderer.MeasureText(Text, Font).Width + 28;
                Width = Math.Max(120, w);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.PanelAlt);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rc = new Rectangle(0, 0, Width - 1, Height - 1);

                var fill = _down ? Color.FromArgb(60, Theme.Accent)
                                 : _hover ? Color.FromArgb(40, Theme.Accent)
                                          : Color.FromArgb(28, Theme.Accent);

                using var br = new SolidBrush(fill);
                using var pen = new Pen(Color.FromArgb(120, Theme.Accent));

                using var gp = GraphicsUtil.RoundRect(rc, 6);

                g.FillPath(br, gp);
                g.DrawPath(pen, gp);

                TextRenderer.DrawText(
                    g,
                    Text,
                    Font,
                    rc,
                    Color.White,
                    TextFormatFlags.VerticalCenter
                  | TextFormatFlags.HorizontalCenter
                  | TextFormatFlags.EndEllipsis);
            }
        }

        private sealed class SearchBox : Panel
        {
            public TextBox Inner { get; }

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public new string Text
            {
                get => Inner.Text;
                set => Inner.Text = value;
            }

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public new event EventHandler? TextChanged
            {
                add { Inner.TextChanged += value; }
                remove { Inner.TextChanged -= value; }
            }

            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public string Placeholder
            {
                get => Inner.PlaceholderText;
                set => Inner.PlaceholderText = value;
            }

            public SearchBox()
            {
                DoubleBuffered = true;
                Height = 32;
                BackColor = Theme.Panel;
                Padding = new Padding(12, 6, 12, 6);

                Inner = new TextBox
                {
                    BorderStyle = BorderStyle.None,
                    Font = new Font("Segoe UI", 10.5f),
                    ForeColor = Theme.Text,
                    BackColor = Theme.Panel,
                    Dock = DockStyle.Fill
                };

                Controls.Add(Inner);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Panel);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                using var bg = new SolidBrush(Theme.Panel);
                using var pen = new Pen(Theme.Border);
                g.FillRectangle(bg, ClientRectangle);
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }

        // ------------ NAV BUTTON SINISTRA ------------
        private sealed class NavButton : Control
        {
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public bool Selected { get; set; }

            public NavButton(string text)
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint, true);

                Height = 40;
                Width = 220;
                Cursor = Cursors.Hand;
                Text = text;
                ForeColor = Theme.Text;
                BackColor = Theme.Nav;
                Margin = new Padding(0, 6, 0, 0);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);

                using var bg = new SolidBrush(Selected ? Theme.PanelAlt : Theme.Nav);
                using var bd = new Pen(Theme.Border);
                g.FillRectangle(bg, r);
                g.DrawRectangle(bd, r);

                using var font = Selected
                    ? new Font("Segoe UI Semibold", 10.5f)
                    : new Font("Segoe UI", 10.5f);

                TextRenderer.DrawText(
                    g,
                    Text,
                    font,
                    new Rectangle(12, 0, Width - 24, Height),
                    Theme.Text,
                    TextFormatFlags.Left
                  | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.EndEllipsis);
            }
        }

        // ------------ BOTTONE FOOTER SINISTRO ------------
        private sealed class FlatButton : Control
        {
            public enum Variant { Primary, Secondary }

            private readonly Variant _variant;
            private bool _hover;
            private bool _down;
            private readonly string _text;

            public FlatButton(string text, Variant variant)
            {
                _text = text;
                _variant = variant;

                Cursor = Cursors.Hand;
                Size = new Size(148, 32);
                TabStop = false;

                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.SupportsTransparentBackColor, true);

                BackColor = Color.Transparent;

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; _down = false; Invalidate(); };
                MouseDown += (_, __) => { _down = true; Invalidate(); };
                MouseUp += (_, __) =>
                {
                    _down = false;
                    Invalidate();
                    OnClick(EventArgs.Empty);
                };
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Nav);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);

                using var gp = GraphicsUtil.RoundRect(rect, 6);

                if (_variant == Variant.Primary)
                {
                    var cTop = Theme.Accent;
                    var cBot = Color.FromArgb(180, Theme.Accent);

                    if (_down)
                    {
                        cTop = ControlPaint.Dark(cTop);
                        cBot = ControlPaint.Dark(cBot);
                    }
                    else if (_hover)
                    {
                        cTop = ControlPaint.Light(cTop);
                    }

                    using (var lg = new LinearGradientBrush(rect, cTop, cBot, LinearGradientMode.Vertical))
                        g.FillPath(lg, gp);
                }
                else
                {
                    var baseCol = Theme.PanelAlt;
                    if (_hover) baseCol = ControlPaint.Light(baseCol);
                    if (_down) baseCol = ControlPaint.Dark(baseCol);

                    using (var br = new SolidBrush(baseCol))
                        g.FillPath(br, gp);

                    using var pen = new Pen(Color.FromArgb(90, Theme.Accent));
                    g.DrawPath(pen, gp);
                }

                using var f = new Font("Segoe UI Semibold", 10.5f);
                TextRenderer.DrawText(
                    g,
                    _text,
                    f,
                    rect,
                    Color.White,
                    TextFormatFlags.HorizontalCenter
                  | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.EndEllipsis);
            }
        }

        // ------------ LOADING MASK (overlay caricamento) ------------
        private sealed class LoadingMask : Control
        {
            private readonly System.Windows.Forms.Timer _t = new() { Interval = 90 };
            private int _angle;
            private string _message = "Caricamento…";

            public LoadingMask()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.UserPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.SupportsTransparentBackColor, true);

                BackColor = Color.Transparent;

                _t.Tick += (_, __) =>
                {
                    _angle = (_angle + 30) % 360;
                    Invalidate();
                };
                _t.Start();
            }

            public void SetMessage(string m)
            {
                _message = m;
                Invalidate();
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                // vetro scuro sopra tutta la pagina
                using var br = new SolidBrush(Color.FromArgb(170, 10, 10, 14));
                e.Graphics.FillRectangle(br, ClientRectangle);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                int w = Width;
                int h = Height;
                if (w <= 0 || h <= 0) return;

                // ---- spinner ad arco ----
                int r = 32;
                int cx = w / 2;
                int cy = h / 2 - 10;
                var rect = new Rectangle(cx - r / 2, cy - r / 2, r, r);

                using (var p = new Pen(Theme.Accent, 3)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                })
                {
                    g.DrawArc(p, rect, _angle, 300);
                }

                // ---- testo sotto ----
                using var f = new Font("Segoe UI", 11f);
                var sz = TextRenderer.MeasureText(_message, f);
                var txtRect = new Rectangle(
                    cx - sz.Width / 2,
                    cy + r / 2 + 8,
                    sz.Width,
                    sz.Height);

                TextRenderer.DrawText(
                    g,
                    _message,
                    f,
                    txtRect,
                    Theme.SubtleText,
                    TextFormatFlags.HorizontalCenter
                  | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.EndEllipsis);
            }
        }

        // ------------ PictureBox DB per thumbnails ------------
        private sealed class DBPictureBox : PictureBox
        {
            public DBPictureBox()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw, true);
                DoubleBuffered = true;
                BackColor = Color.Black;
                SizeMode = PictureBoxSizeMode.Zoom;
            }
        }

        // ------------ FileCard (griglia e carosello) ------------
        private sealed class FileCard : Control
        {
            private readonly string _path;
            private readonly DBPictureBox _img;
            private readonly IconButton? _starBtn;
            private bool _fav;
            private readonly Action<string, bool>? _favSetter;
            private readonly Action _openAction;
            private readonly int _imgHeight;
            private bool _hover;
            private double _progress;
            public string FilePath => _path;

            public FileCard(
                string path,
                bool showFavorite,
                bool favInit,
                Action<string, bool>? onFavToggle,
                Action clickOpen,
                int cardWidth,
                int cardHeight,
                int imgHeight)
            {
                _path = path;
                _openAction = clickOpen;
                _imgHeight = imgHeight;
                _fav = favInit;
                _favSetter = onFavToggle;

                Size = new Size(cardWidth, cardHeight);
                Margin = new Padding(10, 6, 10, 6);

                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw, true);

                BackColor = Theme.Card;
                Cursor = Cursors.Hand;

                _img = new DBPictureBox
                {
                    Location = new Point(0, 0),
                    Size = new Size(cardWidth, imgHeight),
                    Cursor = Cursors.Hand,
                    TabStop = false
                };
                _img.Click += (_, __) => _openAction();
                Controls.Add(_img);

                if (showFavorite)
                {
                    _starBtn = new IconButton(favInit ? IconButton.Kind.StarFilled : IconButton.Kind.Star)
                    {
                        Size = new Size(22, 22),
                        BackColor = Color.Transparent
                    };
                    _starBtn.Click += (_, __) =>
                    {
                        _fav = !_fav;
                        _favSetter?.Invoke(_path, _fav);
                        _starBtn.SetKind(_fav ? IconButton.Kind.StarFilled : IconButton.Kind.Star);
                        Invalidate();
                    };
                    Controls.Add(_starBtn);
                }

                Resize += (_, __) => LayoutInternal();

                Click += (_, __) => _openAction();
                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; Invalidate(); };
            }
            public void SetProgress(double progress01)
            {
                _progress = Math.Max(0.0, Math.Min(1.0, progress01));
                Invalidate();
            }

            private void LayoutInternal()
            {
                _img.Size = new Size(Width, _imgHeight);
                _img.Location = new Point(0, 0);

                if (_starBtn != null)
                {
                    int footerY = _imgHeight;
                    int footerH = Height - _imgHeight;
                    _starBtn.Left = Width - _starBtn.Width - 8;
                    _starBtn.Top = footerY + (footerH - _starBtn.Height) / 2;
                }
            }

            public void SetInitialPlaceholder(Bitmap bmp)
            {
                if (_img.IsDisposed) return;
                _img.Image = bmp;
            }

            public void BeginThumbLoad(CancellationToken ct)
            {
                Task.Run(() =>
                {
                    try
                    {
                        if (ct.IsCancellationRequested) return;

                        Bitmap? bmp = TryLoadThumb(_path, Math.Max(520, Width));

                        if (bmp == null)
                        {
                            var cat = CategoryFromExt((Path.GetExtension(_path) ?? "").ToLowerInvariant());
                            bmp = GetCategoryPlaceholder(cat, Math.Max(520, Width));
                        }

                        if (ct.IsCancellationRequested)
                        {
                            bmp?.Dispose();
                            return;
                        }

                        if (bmp != null && _img.IsHandleCreated && !_img.IsDisposed)
                        {
                            _img.BeginInvoke(new Action(() =>
                            {
                                if (!_img.IsDisposed)
                                    _img.Image = bmp;
                            }));
                        }
                    }
                    catch { }
                }, ct);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Black);

                // card bg + border
                using (var bg = new SolidBrush(_hover ? Theme.PanelAlt : Theme.Card))
                    g.FillRectangle(bg, new Rectangle(0, 0, Width - 1, Height - 1));

                using (var penBorder = new Pen(Theme.Border))
                    g.DrawRectangle(penBorder, 0, 0, Width - 1, Height - 1);

                // footer
                int footerY = _imgHeight;
                int footerH = Height - _imgHeight;
                var footerRect = new Rectangle(0, footerY, Width, footerH);

                using (var footerBg = new SolidBrush(_hover
                    ? Theme.PanelAlt
                    : Color.FromArgb(36, 36, 40)))
                {
                    g.FillRectangle(footerBg, footerRect);
                }

                // --- progress bar "riprendi" (se presente) ---
                int barHeight = 4;
                if (_progress > 0.001)
                {
                    double p = Math.Max(0.0, Math.Min(1.0, _progress));

                    var trackRect = new Rectangle(
                        footerRect.Left,
                        footerRect.Top,
                        footerRect.Width,
                        barHeight);

                    using (var trackBg = new SolidBrush(Color.FromArgb(80, Theme.Border)))
                        g.FillRectangle(trackBg, trackRect);

                    int filledW = (int)Math.Round(trackRect.Width * p);
                    if (filledW > 0)
                    {
                        var fillRect = new Rectangle(
                            trackRect.Left,
                            trackRect.Top,
                            filledW,
                            trackRect.Height);

                        using var fillBr = new SolidBrush(Theme.Accent);
                        g.FillRectangle(fillBr, fillRect);
                    }
                }

                string fileName = Path.GetFileName(_path);
                using var fileFont = new Font("Segoe UI Semibold", 10f);
                // un po' di spazio sopra per la barretta
                var textRect = new Rectangle(10, footerY + 6, Width - 10 - 10 - 30, footerH - 12);

                TextRenderer.DrawText(
                    g,
                    fileName,
                    fileFont,
                    textRect,
                    Color.White,
                    TextFormatFlags.Left
                  | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.EndEllipsis);

                // badge estensione in alto a destra della thumb
                var ext = (Path.GetExtension(_path) ?? "")
                    .Trim('.')
                    .ToUpperInvariant();

                if (!string.IsNullOrEmpty(ext))
                {
                    var badge = $" {ext} ";
                    using var badgeFont = new Font("Segoe UI Semibold", 8.5f);

                    var sz = g.MeasureString(badge, badgeFont);

                    int bx = Width - (int)sz.Width - 12;
                    int by = 10;

                    using var brBadgeBg = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
                    using var brBadgeFg = new SolidBrush(Color.White);

                    g.FillRectangle(
                        brBadgeBg,
                        new Rectangle(
                            bx - 4,
                            by - 2,
                            (int)sz.Width + 8,
                            (int)sz.Height + 4));

                    g.DrawString(badge, badgeFont, brBadgeFg, bx, by);
                }
            }
        }

        // ------------ VIEWPORT CAROSELLO (Recenti) ------------
        private sealed class CarouselViewport : Panel
        {
            private readonly FlowLayoutPanel _flow;
            private int _offsetX; // scroll orizzontale manuale

            public CarouselViewport()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw, true);

                BackColor = Color.Black;
                AutoScroll = false; // no scrollbar Windows

                _flow = new FlowLayoutPanel
                {
                    WrapContents = false,
                    FlowDirection = FlowDirection.LeftToRight,
                    Margin = new Padding(0),
                    Padding = new Padding(0),
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    BackColor = Color.Black,
                    Location = new Point(0, 0)
                };

                Controls.Add(_flow);

                Anchor = AnchorStyles.Top;
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                ClampOffset();
                UpdateFlowPosition();
            }

            public void ResetItems(
                List<string> paths,
                CancellationToken token,
                Action<string> openCb,
                Action<string, FileCard> initThumb)
            {
                SuspendLayout();
                _flow.SuspendLayout();

                _flow.Controls.Clear();

                foreach (var p in paths)
                {
                    var card = new FileCard(
                        path: p,
                        showFavorite: false,
                        favInit: false,
                        onFavToggle: null,
                        clickOpen: () => openCb(p),
                        cardWidth: 300,
                        cardHeight: 236,
                        imgHeight: 170
                    );

                    initThumb(p, card);
                    _flow.Controls.Add(card);
                }

                _flow.ResumeLayout(true);

                _offsetX = 0;
                ClampOffset();
                UpdateFlowPosition();

                ResumeLayout(true);
                Invalidate();
            }

            // sposta di UNA card
            public void StepItems(int dir)
            {
                int iw = GetItemOuterWidthEstimate();
                if (iw < 1) return;
                _offsetX += dir * iw;
                ClampOffset();
                UpdateFlowPosition();
            }

            public int GetItemOuterWidthEstimate()
            {
                var c = _flow.Controls.Cast<Control>().FirstOrDefault();
                if (c == null) return 320;
                return c.Width + c.Margin.Left + c.Margin.Right;
            }

            public int GetPreferredHeightEstimate()
            {
                var c = _flow.Controls.Cast<Control>().FirstOrDefault();
                if (c == null) return 236;
                return c.Height + c.Margin.Top + c.Margin.Bottom;
            }

            private void UpdateFlowPosition()
            {
                _flow.Location = new Point(-_offsetX, 0);
            }

            private void ClampOffset()
            {
                int contentW = TotalContentWidth();
                int viewW = ClientSize.Width;
                if (contentW <= viewW)
                {
                    _offsetX = 0;
                }
                else
                {
                    if (_offsetX < 0) _offsetX = 0;
                    int maxOff = contentW - viewW;
                    if (_offsetX > maxOff) _offsetX = maxOff;
                }
            }

            private int TotalContentWidth()
            {
                int tot = 0;
                foreach (Control c in _flow.Controls)
                    tot += c.Width + c.Margin.Left + c.Margin.Right;
                return tot;
            }
        }

        // ------------ ICON BUTTON (frecce carosello / stellina preferiti) ------------
        private sealed class IconButton : Control
        {
            public enum Kind { ChevronLeft, ChevronRight, Star, StarFilled }
            private Kind _kind;
            private bool _hover, _down;

            public IconButton(Kind k)
            {
                _kind = k;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.UserPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.SupportsTransparentBackColor
                       | ControlStyles.ResizeRedraw, true);

                Cursor = Cursors.Hand;
                Size = new Size(42, 42);
                BackColor = Color.Transparent;
                TabStop = false;

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; _down = false; Invalidate(); };
                MouseDown += (_, __) => { _down = true; Invalidate(); };
                MouseUp += (_, __) => { _down = false; Invalidate(); };
            }

            public void SetKind(Kind k) { _kind = k; Invalidate(); }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Color.Transparent);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                if (_kind is Kind.ChevronLeft or Kind.ChevronRight)
                {
                    var bg = _down ? Color.FromArgb(190, Theme.Accent)
                                   : _hover ? Color.FromArgb(160, Theme.Accent)
                                            : Color.FromArgb(120, Theme.Accent);

                    using var b = new SolidBrush(bg);
                    g.FillEllipse(b, 0, 0, Width - 1, Height - 1);

                    using var p = new Pen(Color.White, 3f)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    };
                    var cx = Width / 2f;
                    var cy = Height / 2f;
                    if (_kind == Kind.ChevronLeft)
                    {
                        g.DrawLines(p, new[]
                        {
                            new PointF(cx + 5, cy - 9),
                            new PointF(cx - 5, cy),
                            new PointF(cx + 5, cy + 9)
                        });
                    }
                    else
                    {
                        g.DrawLines(p, new[]
                        {
                            new PointF(cx - 5, cy - 9),
                            new PointF(cx + 5, cy),
                            new PointF(cx - 5, cy + 9)
                        });
                    }
                }
                else
                {
                    // stellina preferiti
                    var color = _kind == Kind.StarFilled ? Color.Gold : Color.White;
                    var r = new RectangleF(
                        (Width - 18) / 2f,
                        (Height - 18) / 2f,
                        18,
                        18);
                    GraphicsUtil.DrawStar(g, r, color, fill: _kind == Kind.StarFilled);
                }
            }
        }

        // ------------ THUMB GENERATION / PLACEHOLDER ------------
        private static Bitmap? TryLoadThumb(string path, int maxW)
        {
            try
            {
                var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();

                // immagini → carica diretta
                if (new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".webp" }.Contains(ext))
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var src = new Bitmap(fs);
                    var bmpImg = ResizeBitmap(src, maxW);

                    // frame quasi nero? scarta
                    if (IsMostlyDark(bmpImg))
                    {
                        bmpImg.Dispose();
                        return null;
                    }

                    return bmpImg;
                }

                // video/audio → estrai frame (Thumbnailer custom)
                try
                {
                    var th = new Thumbnailer(); // tua classe esterna
                    th.Open(path);
                    var frame = th.Get(seconds: 3.0, maxW: maxW)
                             ?? th.Get(1.0, maxW);

                    if (frame != null)
                    {
                        if (IsMostlyDark(frame))
                        {
                            frame.Dispose();
                            frame = null;
                        }

                        if (frame != null)
                            return frame;
                    }
                }
                catch { }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap ResizeBitmap(Bitmap src, int maxW)
        {
            if (src.Width <= maxW)
                return new Bitmap(src);
            var h = (int)Math.Max(1, src.Height * (maxW / (double)src.Width));
            var bmp = new Bitmap(maxW, h);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, maxW, h);
            return bmp;
        }

        private static bool IsMostlyDark(Bitmap bmp)
        {
            // se >90% pixel sotto 20/255 la consideriamo nera
            int darkCount = 0;
            int total = 0;

            int stepX = Math.Max(1, bmp.Width / 8);
            int stepY = Math.Max(1, bmp.Height / 8);

            for (int y = stepY / 2; y < bmp.Height; y += stepY)
            {
                for (int x = stepX / 2; x < bmp.Width; x += stepX)
                {
                    var c = bmp.GetPixel(x, y);
                    int lum = (c.R + c.G + c.B) / 3;
                    total++;
                    if (lum < 20) darkCount++;
                }
            }

            if (total == 0) return false;
            double ratioDark = darkCount / (double)total;
            return ratioDark > 0.9;
        }

        private static string CategoryFromExt(string ext)
        {
            if (new[] { ".mkv" }.Contains(ext)) return "film";
            if (new[] { ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".webm", ".flv", ".m2ts", ".ts", ".iso" }.Contains(ext)) return "video";
            if (new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".webp" }.Contains(ext)) return "foto";
            if (new[]
                { ".mp3", ".flac", ".mka", ".aac", ".ogg", ".wav", ".wma",
                  ".m4a", ".opus", ".dts", ".ac3", ".eac3" }.Contains(ext)) return "musica";
            return "video";
        }

        private static Bitmap GetCategoryPlaceholder(string cat, int maxW)
        {
            try
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025",
                    "placeholders");

                var file = cat switch
                {
                    "film" => Path.Combine(folder, "film.jpg"),
                    "video" => Path.Combine(folder, "video.jpg"),
                    "musica" => Path.Combine(folder, "musica.jpg"),
                    "foto" => Path.Combine(folder, "foto.jpg"),
                    _ => Path.Combine(folder, "video.jpg")
                };

                if (File.Exists(file))
                {
                    using var src = new Bitmap(file);
                    return ResizeBitmap(src, maxW);
                }
            }
            catch { }

            // fallback gradient + emoji
            var w = maxW;
            var h = Math.Max(120, (int)(maxW * 9.0 / 16.0));
            var bmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            (Color c1, Color c2, string icon) tpl = cat switch
            {
                "film" => (Color.FromArgb(80, 30, 180), Color.FromArgb(30, 10, 90), "🎬"),
                "video" => (Color.FromArgb(0, 140, 200), Color.FromArgb(0, 70, 120), "▶"),
                "musica" => (Color.FromArgb(0, 170, 120), Color.FromArgb(0, 90, 70), "♪"),
                "foto" => (Color.FromArgb(190, 120, 0), Color.FromArgb(120, 70, 0), "🖼"),
                _ => (Color.FromArgb(60, 60, 60), Color.FromArgb(30, 30, 30), "■")
            };

            using (var lg = new LinearGradientBrush(
                new Rectangle(0, 0, w, h),
                tpl.c1,
                tpl.c2,
                LinearGradientMode.ForwardDiagonal))
            {
                g.FillRectangle(lg, 0, 0, w, h);
            }

            using var f = new Font("Segoe UI Emoji", Math.Max(28, w / 10), FontStyle.Bold);
            var sz = g.MeasureString(tpl.icon, f);
            g.DrawString(tpl.icon, f, Brushes.White,
                (w - sz.Width) / 2f,
                (h - sz.Height) / 2f);

            return bmp;
        }

        // ------------ Flow / scrollbar custom ------------
        private class BetterFlow : FlowLayoutPanel
        {
            public bool HideHScroll { get; set; }
            public bool HideVScroll { get; set; }

            public BetterFlow()
            {
                // forza DoubleBuffered su FlowLayoutPanel via reflection
                typeof(Panel)
                    .GetProperty("DoubleBuffered",
                        System.Reflection.BindingFlags.Instance
                      | System.Reflection.BindingFlags.NonPublic)
                    ?.SetValue(this, true, null);

                SetStyle(ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw, true);
                UpdateStyles();
            }

            // togli scrollbar native
            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_VSCROLL = 0x00200000;
                    const int WS_HSCROLL = 0x00100000;
                    var cp = base.CreateParams;
                    cp.Style &= ~WS_VSCROLL;
                    cp.Style &= ~WS_HSCROLL;
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                HideBars();
            }
            protected override void OnLayout(LayoutEventArgs levent)
            {
                base.OnLayout(levent);
                HideBars();
            }
            protected override void OnResize(EventArgs eventargs)
            {
                base.OnResize(eventargs);
                HideBars();
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);

                const int WM_VSCROLL = 0x0115;
                const int WM_HSCROLL = 0x0114;
                const int WM_MOUSEWHEEL = 0x020A;
                const int WM_MOUSEHWHEEL = 0x020E;
                const int WM_PAINT = 0x000F;
                const int WM_NCPAINT = 0x0085;
                const int WM_WINDOWPOSCHANGED = 0x0047;

                if (m.Msg == WM_VSCROLL || m.Msg == WM_HSCROLL ||
                    m.Msg == WM_MOUSEWHEEL || m.Msg == WM_MOUSEHWHEEL ||
                    m.Msg == WM_PAINT || m.Msg == WM_NCPAINT ||
                    m.Msg == WM_WINDOWPOSCHANGED)
                {
                    HideBars();
                }
            }

            public void ForceHideScrollbars() => HideBars();

            protected void HideBars()
            {
                if (!IsHandleCreated) return;
                if (HideHScroll) Win32.ShowScrollBar(Handle, Win32.SB_HORZ, false);
                if (HideVScroll) Win32.ShowScrollBar(Handle, Win32.SB_VERT, false);
            }
        }

        private sealed class SkinnedFlow : BetterFlow
        {
            private ThemedVScroll? _skin;
            public bool UseThemedVScroll { get; set; }

            public event EventHandler? ScrollStateChanged;

            public SkinnedFlow()
            {
                HideHScroll = true;
                HideVScroll = true;

                // rotella mouse = scroll verticale custom
                MouseWheel += (_, e) =>
                {
                    var cur = -AutoScrollPosition.Y;
                    var step = SystemInformation.MouseWheelScrollDelta;
                    var target = Math.Max(0, cur - Math.Sign(e.Delta) * step * 2);

                    AutoScrollPosition = new Point(-AutoScrollPosition.X, target);
                    ScrollStateChanged?.Invoke(this, EventArgs.Empty);
                    UpdateThemedScrollbar();

                    Invalidate(true);
                    Update();
                };
            }

            protected override void OnCreateControl()
            {
                base.OnCreateControl();
                if (UseThemedVScroll)
                {
                    _skin = new ThemedVScroll
                    {
                        Dock = DockStyle.Right,
                        Width = 12 // nero + thumb accent 4px
                    };

                    Controls.Add(_skin);
                    _skin.BringToFront();

                    _skin.ScrollTo += v =>
                    {
                        AutoScrollPosition = new Point(-AutoScrollPosition.X, v);
                        ScrollStateChanged?.Invoke(this, EventArgs.Empty);
                        UpdateThemedScrollbar();
                        Invalidate(true);
                        Update();
                    };
                }
            }

            protected override void OnScroll(ScrollEventArgs se)
            {
                base.OnScroll(se);
                ScrollStateChanged?.Invoke(this, EventArgs.Empty);
                UpdateThemedScrollbar();
                Invalidate(true);
                Update();
            }

            public void UpdateThemedScrollbar()
            {
                if (UseThemedVScroll && _skin != null)
                {
                    var total = DisplayRectangle.Height;
                    var viewport = ClientSize.Height;
                    var value = Math.Max(0, -AutoScrollPosition.Y);
                    _skin.SetRange(total, viewport, value);
                }

                ForceHideScrollbars();
            }
        }

        // ------------ Scrollbar verticale custom ------------
        private sealed class ThemedVScroll : Control
        {
            private int _total = 1;
            private int _view = 1;
            private int _value = 0;
            private bool _drag;
            private int _dragOffset;

            public event Action<int>? ScrollTo;

            public ThemedVScroll()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.SupportsTransparentBackColor, true);

                BackColor = Color.Black; // nessuna gutter chiara
                Cursor = Cursors.Hand;
            }

            protected override void OnParentChanged(EventArgs e)
            {
                base.OnParentChanged(e);
                if (Parent != null) BackColor = Parent.BackColor;
            }

            public void SetRange(int total, int viewport, int value)
            {
                _total = Math.Max(1, total);
                _view = Math.Max(1, viewport);
                _value = Math.Max(0,
                    Math.Min(value, Math.Max(0, _total - _view)));

                Visible = _total > _view;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (!Visible) return;
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.None;

                using (var bg = new SolidBrush(Color.Black))
                    g.FillRectangle(bg, ClientRectangle);

                var thRect = GetThumbRect();

                // thumb = barretta Accent da 4px sul bordo destro
                var drawRect = new Rectangle(Width - 4, thRect.Y, 4, thRect.Height);
                using var thumbBr = new SolidBrush(Color.FromArgb(200, Theme.Accent));
                g.FillRectangle(thumbBr, drawRect);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (!Visible) return;
                var th = GetThumbRect();
                if (th.Contains(e.Location))
                {
                    _drag = true;
                    _dragOffset = e.Y - th.Y;
                }
                else
                {
                    JumpTo(e.Y);
                }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (Visible && _drag)
                    DragTo(e.Y - _dragOffset);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                _drag = false;
            }

            private Rectangle GetThumbRect()
            {
                float ratio = _total <= _view ? 1f : (float)_view / _total;
                int th = Math.Max(20, (int)(Height * ratio));
                int maxY = Height - th;
                int y = (_total <= _view)
                    ? 0
                    : (int)(maxY * (_value / (float)(_total - _view)));

                // hitbox larga 8px, disegniamo 4px
                return new Rectangle(Width - 8, y, 8, th);
            }

            private void JumpTo(int y)
            {
                var th = GetThumbRect();
                DragTo(y - th.Height / 2);
            }

            private void DragTo(int y)
            {
                float ratio = _total <= _view ? 1f : (float)_view / _total;
                int th = Math.Max(20, (int)(Height * ratio));
                int maxY = Height - th;
                y = Math.Max(0, Math.Min(y, maxY));

                int newVal = (int)((_total - _view) * (y / (float)maxY));
                ScrollTo?.Invoke(newVal);
                _value = newVal;
                Invalidate();
            }
        }

        // ------------ SectionHeader ("Recenti", "Tutti i file") ------------
        private sealed class SectionHeader : Panel
        {
            private readonly string _text;
            public int LeftMargin { get; set; } = 104;

            public SectionHeader(string text)
            {
                _text = text;
                Height = 38;
                Dock = DockStyle.Top;
                BackColor = Color.Black;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using var font = new Font("Segoe UI Semibold", 11f);
                var textSize = TextRenderer.MeasureText(
                    _text,
                    font,
                    new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding);

                int leftPad = LeftMargin;
                int pillPadX = 8;
                int pillPadY = 4;

                var pillRect = new Rectangle(
                    leftPad,
                    6,
                    textSize.Width + pillPadX * 2,
                    textSize.Height + pillPadY * 2 - 2);

                using (var path = GraphicsUtil.RoundRect(pillRect, 6))
                {
                    using (var br = new SolidBrush(Theme.PanelAlt))
                        g.FillPath(br, path);

                    using (var pn = new Pen(Theme.Border))
                        g.DrawPath(pn, path);
                }

                TextRenderer.DrawText(
                    g,
                    _text,
                    font,
                    new Rectangle(
                        pillRect.Left + pillPadX,
                        pillRect.Top + pillPadY / 2,
                        pillRect.Width - pillPadX * 2,
                        pillRect.Height - pillPadY),
                    Color.White,
                    TextFormatFlags.NoPadding
                  | TextFormatFlags.Left
                  | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.EndEllipsis);

                using var pen = new Pen(Theme.Border);
                g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }
        }

        // ------------ Pannello destro host custom (toglie scrollbar di sistema) ------------
        private sealed class RightHostPanel : Panel
        {
            public RightHostPanel()
            {
                Dock = DockStyle.Fill;
                BackColor = Color.Black;
                AutoScroll = false;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw, true);
            }

            // togli gli style WS_VSCROLL / WS_HSCROLL per evitare gutter bianca
            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_VSCROLL = 0x00200000;
                    const int WS_HSCROLL = 0x00100000;
                    var cp = base.CreateParams;
                    cp.Style &= ~WS_VSCROLL;
                    cp.Style &= ~WS_HSCROLL;
                    return cp;
                }
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                Win32.ShowScrollBar(Handle, Win32.SB_VERT, false);
                Win32.ShowScrollBar(Handle, Win32.SB_HORZ, false);
            }
        }

        // ------------ DLNA DISCOVERY / BROWSE UI ------------
        private sealed class DlnaDevice
        {
            public string FriendlyName = "DLNA";
            public Uri BaseUri = null!;
            public Uri ControlUrl = null!;
        }

        private sealed class DlnaObject
        {
            public string Id = "0";
            public string Title = "";
            public bool IsContainer;
            public string? AlbumArt;
            public string? Resource; // primo <res> utile (http-get)
            public string? Mime;
        }

        private static async Task<List<DlnaDevice>> DiscoverDlnaAsync(CancellationToken ct)
        {
            var list = new List<DlnaDevice>();

            // forza IPv4 (su .NET recenti altrimenti a volte si incasina)
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.EnableBroadcast = true;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            var ep = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

            string req =
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 2\r\n" +
                "ST: urn:schemas-upnp-org:service:ContentDirectory:1\r\n\r\n";

            byte[] data = Encoding.ASCII.GetBytes(req);

            var start = Environment.TickCount;
            int lastSend = start - 3000;
            int sendCount = 0;

            while (!ct.IsCancellationRequested && Environment.TickCount - start < 9000)
            {
                int now = Environment.TickCount;

                // manda M-SEARCH subito e poi ogni ~3s
                if (sendCount == 0 || now - lastSend >= 3000)
                {
                    try
                    {
                        await udp.SendAsync(data, data.Length, ep);
                    }
                    catch
                    {
                        // se il send fallisce non blocchiamo tutto: proviamo comunque a ricevere
                    }
                    lastSend = now;
                    sendCount++;
                }

                try
                {
                    var res = await udp.ReceiveAsync().WaitAsync(
                        TimeSpan.FromMilliseconds(1000), ct);

                    string resp = Encoding.ASCII.GetString(res.Buffer);
                    var headers = ParseHttpHeaders(resp);
                    if (!headers.TryGetValue("LOCATION", out var loc))
                        continue;

                    try
                    {
                        var desc = await _http.GetStringAsync(loc, ct);
                        var (friendly, ctrl) = ParseDeviceDescription(desc, new Uri(loc));
                        if (ctrl != null && friendly != null)
                        {
                            list.Add(new DlnaDevice
                            {
                                FriendlyName = friendly,
                                BaseUri = new Uri(loc),
                                ControlUrl = ctrl
                            });
                        }
                    }
                    catch
                    {
                        // best-effort
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // timeout singolo ok, continuiamo finché non scade il while
                }
            }

            // dedup per ControlUrl
            var unique = new Dictionary<string, DlnaDevice>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in list)
                if (!unique.ContainsKey(d.ControlUrl.ToString()))
                    unique[d.ControlUrl.ToString()] = d;

            return unique.Values.ToList();

            static Dictionary<string, string> ParseHttpHeaders(string raw)
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var l in lines)
                {
                    int c = l.IndexOf(':');
                    if (c > 0)
                        dict[l.Substring(0, c).Trim()] = l.Substring(c + 1).Trim();
                }
                return dict;
            }

            static (string? friendly, Uri? ctrl) ParseDeviceDescription(string xml, Uri loc)
            {
                try
                {
                    var x = XDocument.Parse(xml);

                    // prova con namespace ufficiale, ma con fallback sui LocalName
                    XNamespace ns = "urn:schemas-upnp-org:device-1-0";
                    var dev = x.Root?.Element(ns + "device")
                              ?? x.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
                    if (dev == null) return (null, null);

                    string? name = dev.Elements().FirstOrDefault(e => e.Name.LocalName == "friendlyName")?.Value;

                    var servicesParent = dev.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceList");
                    var services = servicesParent?.Elements().Where(e => e.Name.LocalName == "service")
                                  ?? Enumerable.Empty<XElement>();

                    foreach (var s in services)
                    {
                        var st = s.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value ?? "";
                        if (!st.Contains("ContentDirectory", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var rel = s.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value ?? "";
                        if (string.IsNullOrWhiteSpace(rel))
                            continue;

                        Uri ctrl;
                        if (Uri.TryCreate(rel, UriKind.Absolute, out var abs))
                        {
                            ctrl = abs;
                        }
                        else
                        {
                            ctrl = new Uri(new Uri(loc.GetLeftPart(UriPartial.Authority)), rel);
                        }

                        return (name, ctrl);
                    }
                }
                catch { }

                return (null, null);
            }
        }

        private static async Task<List<DlnaDevice>> DiscoverDlnaWithRetry(CancellationToken ct)
        {
            List<DlnaDevice> devs;
            try { devs = await DiscoverDlnaAsync(ct); }
            catch { devs = new List<DlnaDevice>(); }

            if (ct.IsCancellationRequested || devs.Count > 0)
                return devs;

            // se non ha trovato nulla, aspetta un attimo e riprova
            try { await Task.Delay(1000, ct); } catch { }

            try { devs = await DiscoverDlnaAsync(ct); }
            catch { devs = new List<DlnaDevice>(); }

            return devs;
        }

        private static async Task<(List<DlnaObject> containers, List<DlnaObject> items)> BrowseAsync(DlnaDevice dev, string objectId, CancellationToken ct)
        {
            // SOAP Browse: BrowseDirectChildren
            string soap =
        $@"<?xml version=""1.0"" encoding=""utf-8""?>
<s:Envelope s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"" xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"">
  <s:Body>
    <u:Browse xmlns:u=""urn:schemas-upnp-org:service:ContentDirectory:1"">
      <ObjectID>{SecurityElement.Escape(objectId)}</ObjectID>
      <BrowseFlag>BrowseDirectChildren</BrowseFlag>
      <Filter>*</Filter>
      <StartingIndex>0</StartingIndex>
      <RequestedCount>200</RequestedCount>
      <SortCriteria></SortCriteria>
    </u:Browse>
  </s:Body>
</s:Envelope>";

            using var msg = new HttpRequestMessage(HttpMethod.Post, dev.ControlUrl);
            msg.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"");
            msg.Content = new StringContent(soap, Encoding.UTF8, "text/xml");

            using var httpResp = await _http.SendAsync(msg, ct);
            httpResp.EnsureSuccessStatusCode();
            string resp = await httpResp.Content.ReadAsStringAsync(ct);

            var (cont, items) = ParseDidl(resp, dev.BaseUri);
            return (cont, items);

            static (List<DlnaObject> containers, List<DlnaObject> items) ParseDidl(string soapResp, Uri baseUri)
            {
                var containers = new List<DlnaObject>();
                var items = new List<DlnaObject>();
                try
                {
                    var x = XDocument.Parse(soapResp);
                    var resultStr = x.Descendants().FirstOrDefault(e => e.Name.LocalName == "Result")?.Value;
                    if (string.IsNullOrWhiteSpace(resultStr)) return (containers, items);

                    var didl = XDocument.Parse(resultStr);
                    XNamespace didlns = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
                    XNamespace upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";
                    XNamespace dc = "http://purl.org/dc/elements/1.1/";

                    foreach (var c in didl.Descendants(didlns + "container"))
                    {
                        var id = c.Attribute("id")?.Value ?? "0";
                        var title = c.Element(dc + "title")?.Value ?? "(cartella)";
                        var art = c.Element(upnp + "albumArtURI")?.Value;
                        if (!string.IsNullOrWhiteSpace(art))
                        {
                            try { art = new Uri(baseUri, art).ToString(); } catch { }
                        }
                        containers.Add(new DlnaObject { Id = id, Title = title, IsContainer = true, AlbumArt = art });
                    }

                    foreach (var it in didl.Descendants(didlns + "item"))
                    {
                        var id = it.Attribute("id")?.Value ?? "";
                        var title = it.Element(dc + "title")?.Value ?? "(sorgente)";
                        var art = it.Element(upnp + "albumArtURI")?.Value;
                        if (!string.IsNullOrWhiteSpace(art))
                        {
                            try { art = new Uri(baseUri, art).ToString(); } catch { }
                        }

                        string? res = null, mime = null;
                        foreach (var r in it.Elements(didlns + "res"))
                        {
                            var proto = (r.Attribute("protocolInfo")?.Value ?? "").ToLowerInvariant();
                            var url = r.Value?.Trim();
                            if (string.IsNullOrWhiteSpace(url)) continue;
                            try { url = new Uri(baseUri, url).ToString(); } catch { }
                            // preferiamo risorse http-get
                            if (proto.Contains("http-get"))
                            {
                                res = url; mime = proto.Split(':').ElementAtOrDefault(2);
                                break;
                            }
                        }

                        items.Add(new DlnaObject { Id = id, Title = title, IsContainer = false, AlbumArt = art, Resource = res, Mime = mime });
                    }
                }
                catch { }
                return (containers, items);
            }
        }

        private sealed class RemoteTile : Control
        {
            private readonly string _title;
            private readonly string? _sub;
            private readonly Action _onClick;
            private bool _hover;

            public RemoteTile(string title, string? subtitle, Action onClick, int w = 300, int h = 80)
            {
                _title = title;
                _sub = subtitle;
                _onClick = onClick;

                Size = new Size(w, h);
                Margin = new Padding(10, 6, 10, 6);
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; Invalidate(); };
                Click += (_, __) => _onClick();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rc = new Rectangle(0, 0, Width - 1, Height - 1);

                using var bg = new SolidBrush(_hover ? ControlPaint.Light(Theme.Card) : Theme.Card);
                using var bd = new Pen(Theme.Border);
                g.FillRectangle(bg, rc);
                g.DrawRectangle(bd, rc);

                using var t1 = new Font("Segoe UI Semibold", 10.5f);
                using var t2 = new Font("Segoe UI", 9f);

                var rcTitle = new Rectangle(12, 10, Width - 24, 22);
                var rcSub = new Rectangle(12, rcTitle.Bottom, Width - 24, Height - rcTitle.Bottom - 8);

                TextRenderer.DrawText(g, _title, t1, rcTitle, Color.White, TextFormatFlags.EndEllipsis);
                if (!string.IsNullOrWhiteSpace(_sub))
                    TextRenderer.DrawText(g, _sub, t2, rcSub, Theme.SubtleText, TextFormatFlags.EndEllipsis);
            }
        }

        private void RenderDlnaDeviceList(List<DlnaDevice> devs)
        {
            _grid.Controls.Clear();

            if (devs.Count == 0)
            {
                _grid.Controls.Add(new InfoRow("Nessun server DLNA trovato nella rete domestica."));
                _grid.UpdateThemedScrollbar();
                return;
            }

            _grid.Controls.Add(new InfoRow("Dispositivi DLNA:"));

            foreach (var d in devs.OrderBy(v => v.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
            {
                var tile = new RemoteTile(d.FriendlyName, d.BaseUri.Host, () =>
                {
                    _dlnaSel = d;
                    _dlnaStack.Clear();
                    DlnaEnterContainer("0"); // root
                }, w: 360, h: 76);

                _grid.Controls.Add(tile);
            }

            _grid.UpdateThemedScrollbar();
        }

        private void DlnaEnterContainer(string id)
        {
            if (_dlnaSel == null) return;

            _dlnaStack.Push(id); // current on stack per back
            _dlnaCts?.Cancel();
            _dlnaCts = new CancellationTokenSource();
            var ct = _dlnaCts.Token;

            ShowMask("Caricamento contenuti…");
            Task.Run(async () =>
            {
                List<DlnaObject> folders;
                List<DlnaObject> items;
                try
                {
                    (folders, items) = await BrowseAsync(_dlnaSel, id, ct);
                }
                catch
                {
                    folders = new(); items = new();
                }

                if (IsDisposed || ct.IsCancellationRequested) return;

                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || ct.IsCancellationRequested) return;
                    HideMask();
                    RenderDlnaContainerUi(folders, items);
                }));
            }, ct);
        }

        private void DlnaBack()
        {
            if (_dlnaSel == null) return;

            if (_dlnaStack.Count <= 1)
            {
                // torna alla lista dispositivi
                _dlnaSel = null;
                _dlnaStack.Clear();

                _grid.Controls.Clear();
                _grid.UpdateThemedScrollbar();

                _dlnaCts?.Cancel();
                _dlnaCts = new CancellationTokenSource();
                var ctDlna = _dlnaCts.Token;

                ShowMask("Ricerca dispositivi DLNA…");
                Task.Run(async () =>
                {
                    List<DlnaDevice> devs;
                    try { devs = await DiscoverDlnaWithRetry(ctDlna); }
                    catch { devs = new List<DlnaDevice>(); }

                    if (IsDisposed || ctDlna.IsCancellationRequested) return;

                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || ctDlna.IsCancellationRequested) return;
                        HideMask();
                        RenderDlnaDeviceList(devs);
                    }));
                }, ctDlna);
                return;
            }

            // pop current e vai al parent
            _dlnaStack.Pop();
            var parent = _dlnaStack.Peek();
            _dlnaStack.Pop(); // re-push inside Enter
            DlnaEnterContainer(parent);
        }

        private void RenderDlnaContainerUi(List<DlnaObject> folders, List<DlnaObject> items)
        {
            _grid.Controls.Clear();

            var back = new RemoteTile("← Indietro", _dlnaSel?.FriendlyName, () => DlnaBack(), w: 240, h: 56);
            _grid.Controls.Add(back);

            if (folders.Count == 0 && items.Count == 0)
            {
                _grid.Controls.Add(new InfoRow("Cartella vuota."));
                _grid.UpdateThemedScrollbar();
                return;
            }

            if (folders.Count > 0)
                _grid.Controls.Add(new InfoRow("Cartelle:"));
            foreach (var f in folders)
            {
                var t = new RemoteTile(f.Title, "Cartella", () => DlnaEnterContainer(f.Id), w: 360, h: 76);
                _grid.Controls.Add(t);
            }

            if (items.Count > 0)
                _grid.Controls.Add(new InfoRow("Elementi:"));
            foreach (var it in items)
            {
                string? res = it.Resource;
                var sub = string.IsNullOrWhiteSpace(it.Mime) ? "Sorgente" : it.Mime;
                var t = new RemoteTile(it.Title, sub, () =>
                {
                    if (!string.IsNullOrWhiteSpace(res)) SafeOpen(res!);
                }, w: 360, h: 76);
                _grid.Controls.Add(t);
            }

            _grid.UpdateThemedScrollbar();
        }

        // ------------ URL Pane per "URL" source ------------
        private sealed class UrlPane : Panel
        {
            private readonly TextBox _tb;
            private readonly Button _btn;
            private readonly Action<string> _play;

            public UrlPane(Action<string> onPlay)
            {
                _play = onPlay;
                Height = 120;
                Dock = DockStyle.Top;
                BackColor = Color.Black;
                Padding = new Padding(16, 12, 16, 12);

                var lbl = new Label
                {
                    Text = "Incolla un link http/https (file diretto o HLS .m3u8):",
                    Dock = DockStyle.Top,
                    Height = 20,
                    ForeColor = Theme.Text,
                    BackColor = Color.Black
                };

                _tb = new TextBox
                {
                    Dock = DockStyle.Top,
                    Height = 30,
                    BorderStyle = BorderStyle.FixedSingle,
                    PlaceholderText = "https://…",
                    Font = new Font("Segoe UI", 10.5f)
                };

                _btn = new Button
                {
                    Text = "Riproduci",
                    Dock = DockStyle.Top,
                    Height = 34,
                    FlatStyle = FlatStyle.Flat
                };
                _btn.FlatAppearance.BorderSize = 0;
                _btn.BackColor = Theme.Accent;
                _btn.ForeColor = Color.White;

                _btn.Click += (_, __) => TryPlay();
                _tb.KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Enter)
                    {
                        e.SuppressKeyPress = true;
                        TryPlay();
                    }
                };

                Controls.Add(new Panel { Height = 6, Dock = DockStyle.Top, BackColor = Color.Black });
                Controls.Add(_btn);
                Controls.Add(new Panel { Height = 8, Dock = DockStyle.Top, BackColor = Color.Black });
                Controls.Add(_tb);
                Controls.Add(new Panel { Height = 8, Dock = DockStyle.Top, BackColor = Color.Black });
                Controls.Add(lbl);
            }

            private void TryPlay()
            {
                var s = (_tb.Text ?? "").Trim();
                if (Uri.TryCreate(s, UriKind.Absolute, out var u) &&
                    (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                {
                    _play(s);
                }
                else
                {
                    SystemSounds.Beep.Play();
                }
            }
        }
        private double? GetDurationMinutesCached(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            // 1) prova a leggere dalla cache
            lock (_durationLock)
            {
                if (_durationCache.TryGetValue(path, out var cached))
                    return cached;
            }

            // 2) se non c'è in cache, chiedi a ShellDurationUtil
            double? duration = null;
            try
            {
                duration = ShellDurationUtil.TryGetDurationMinutes(path);
            }
            catch
            {
                // best-effort: se fallisce lasciamo duration = null
            }

            lock (_durationLock)
            {
                _durationCache[path] = duration;
            }

            return duration;
        }

        // ------------ Lettura durata media da proprietà shell (System.Media.Duration) ------------
        private static class ShellDurationUtil
        {
            private enum HRESULT : int
            {
                S_OK = 0,
                S_FALSE = 1
            }

            [Flags]
            private enum GETPROPERTYSTOREFLAGS
            {
                GPS_DEFAULT = 0,
                GPS_HANDLERPROPERTIESONLY = 0x1,
                GPS_READWRITE = 0x2,
                GPS_TEMPORARY = 0x4,
                GPS_FASTPROPERTIESONLY = 0x8,
                GPS_OPENSLOWITEM = 0x10,
                GPS_DELAYCREATION = 0x20,
                GPS_BESTEFFORT = 0x40,
                GPS_NO_OPLOCK = 0x80,
                GPS_PREFERQUERYPROPERTIES = 0x100,
                GPS_EXTRINSICPROPERTIES = 0x200,
                GPS_EXTRINSICPROPERTIESONLY = 0x400,
                GPS_VOLATILEPROPERTIES = 0x800,
                GPS_VOLATILEPROPERTIESONLY = 0x1000,
                GPS_MASK_VALID = 0x1FFF
            }

            [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IPropertyStore
            {
                HRESULT GetCount(out uint propertyCount);
                HRESULT GetAt(uint propertyIndex, out PROPERTYKEY key);
                HRESULT GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
                HRESULT SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
                HRESULT Commit();
            }

            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            private struct PROPERTYKEY
            {
                private readonly Guid _fmtid;
                private readonly uint _pid;

                public PROPERTYKEY(Guid fmtid, uint pid)
                {
                    _fmtid = fmtid;
                    _pid = pid;
                }
            }

            [StructLayout(LayoutKind.Sequential, Pack = 0)]
            private struct PROPARRAY
            {
                public uint cElems;
                public IntPtr pElems;
            }

            [StructLayout(LayoutKind.Explicit, Pack = 1)]
            private struct PROPVARIANT
            {
                [FieldOffset(0)]
                public ushort varType;
                [FieldOffset(2)]
                public ushort wReserved1;
                [FieldOffset(4)]
                public ushort wReserved2;
                [FieldOffset(6)]
                public ushort wReserved3;

                [FieldOffset(8)]
                public byte bVal;
                [FieldOffset(8)]
                public sbyte cVal;
                [FieldOffset(8)]
                public ushort uiVal;
                [FieldOffset(8)]
                public short iVal;
                [FieldOffset(8)]
                public uint uintVal;
                [FieldOffset(8)]
                public int intVal;
                [FieldOffset(8)]
                public ulong ulVal;
                [FieldOffset(8)]
                public long lVal;
                [FieldOffset(8)]
                public float fltVal;
                [FieldOffset(8)]
                public double dblVal;
                [FieldOffset(8)]
                public short boolVal;
                [FieldOffset(8)]
                public IntPtr pclsidVal;
                [FieldOffset(8)]
                public IntPtr pszVal;
                [FieldOffset(8)]
                public IntPtr pwszVal;
                [FieldOffset(8)]
                public IntPtr punkVal;
                [FieldOffset(8)]
                public PROPARRAY ca;
                [FieldOffset(8)]
                public System.Runtime.InteropServices.ComTypes.FILETIME filetime;
            }

            private enum VARENUM
            {
                VT_EMPTY = 0,
                VT_NULL = 1,
                VT_I2 = 2,
                VT_I4 = 3,
                VT_R4 = 4,
                VT_R8 = 5,
                VT_CY = 6,
                VT_DATE = 7,
                VT_BSTR = 8,
                VT_DISPATCH = 9,
                VT_ERROR = 10,
                VT_BOOL = 11,
                VT_VARIANT = 12,
                VT_UNKNOWN = 13,
                VT_DECIMAL = 14,
                VT_I1 = 16,
                VT_UI1 = 17,
                VT_UI2 = 18,
                VT_UI4 = 19,
                VT_I8 = 20,
                VT_UI8 = 21,
                VT_INT = 22,
                VT_UINT = 23,
                VT_FILETIME = 64
            }

            [DllImport("Shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern HRESULT SHGetPropertyStoreFromParsingName(
                string pszPath,
                IntPtr pbc,
                GETPROPERTYSTOREFLAGS flags,
                ref Guid iid,
                [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

            [DllImport("ole32.dll")]
            private static extern int PropVariantClear(ref PROPVARIANT pvar);

            // System.Media.Duration (UInt64, unità da 100ns)
            private static readonly PROPERTYKEY PKEY_Media_Duration =
                new PROPERTYKEY(new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), 3);

            public static double? TryGetDurationMinutes(string path)
            {
                IPropertyStore? store = null;
                PROPVARIANT pv = default;

                try
                {
                    Guid iid = typeof(IPropertyStore).GUID;

                    var hr = SHGetPropertyStoreFromParsingName(
                        path,
                        IntPtr.Zero,
                        GETPROPERTYSTOREFLAGS.GPS_BESTEFFORT,
                        ref iid,
                        out store);

                    if (hr != HRESULT.S_OK || store == null)
                        return null;

                    var key = PKEY_Media_Duration;
                    hr = store.GetValue(ref key, out pv);
                    if (hr != HRESULT.S_OK)
                        return null;

                    if (pv.varType != (ushort)VARENUM.VT_UI8)
                        return null;

                    ulong value = pv.ulVal; // 100ns unità
                    if (value == 0)
                        return null;

                    double seconds = value / 10000000.0;
                    return seconds / 60.0;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    try { PropVariantClear(ref pv); } catch { }

                    if (store != null)
                    {
                        try { Marshal.ReleaseComObject(store); } catch { }
                    }
                }
            }
        }

        // ------------ helper GDI comuni ------------
        private static class GraphicsUtil
        {
            public static GraphicsPath RoundRect(Rectangle r, int rad)
            {
                var gp = new GraphicsPath();
                gp.AddArc(r.Left, r.Top, rad, rad, 180, 90);
                gp.AddArc(r.Right - rad, r.Top, rad, rad, 270, 90);
                gp.AddArc(r.Right - rad, r.Bottom - rad, rad, rad, 0, 90);
                gp.AddArc(r.Left, r.Bottom - rad, rad, rad, 90, 90);
                gp.CloseFigure();
                return gp;
            }

            public static void DrawStar(Graphics g, RectangleF r, Color color, bool fill)
            {
                var pts = new List<PointF>();
                var cx = r.Left + r.Width / 2f;
                var cy = r.Top + r.Height / 2f;

                for (int i = 0; i < 10; i++)
                {
                    var ang = -Math.PI / 2 + i * Math.PI / 5;
                    var rad = (i % 2 == 0) ? r.Width / 2f : r.Width / 4.2f;
                    pts.Add(new PointF(
                        cx + (float)(rad * Math.Cos(ang)),
                        cy + (float)(rad * Math.Sin(ang))));
                }

                using var path = new GraphicsPath();
                path.AddPolygon(pts.ToArray());
                if (fill)
                {
                    using var br = new SolidBrush(color);
                    g.FillPath(br, path);
                }
                using var pen = new Pen(color, 1.8f);
                g.DrawPath(pen, path);
            }
        }

        // ------------ Win32 scrollbar hiding ------------
        private static class Win32
        {
            public const int SB_HORZ = 0;
            public const int SB_VERT = 1;
            [DllImport("user32.dll")]
            public static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);
        }
    }

    internal static class ControlExt
    {
        public static T WithMargin<T>(this T c, int l, int t, int r, int b) where T : Control
        {
            c.Margin = new Padding(l, t, r, b);
            return c;
        }
    }
}
