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
    internal sealed partial class MediaLibraryPage
    {
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
            ShowMask("Caricamento contenuti…");
            // nascondi subito la griglia per evitare che il vecchio contenuto “si deformi”
            _grid.Visible = false;

            _grid.SuspendLayout();
            _grid.Controls.Clear();
            _grid.ResumeLayout();

            // ----- sorgente URL: solo il pannellino link -----
            if (_selSrc == "URL")
            {
                // per la sorgente URL niente carosello "Riprendi"
                _secRecenti.Visible = false;
                _carouselHost.Visible = false;

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
                _grid.Visible = true;              // ri-mostra la griglia con il pannello URL
                _grid.UpdateThemedScrollbar();
                return;
            }
            // ----- sorgente URL: solo il pannellino link -----
            if (_selSrc == "URL")
            {
                // per la sorgente URL niente carosello "Riprendi"
                _secRecenti.Visible = false;
                _carouselHost.Visible = false;

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
                _grid.Visible = true;              // ri-mostra la griglia con il pannello URL
                _grid.UpdateThemedScrollbar();
                return;
            }
            else if (_selSrc == "YouTube")
            {
                // sorgente YouTube: UI dedicata, niente carosello e niente scansione dischi
                _secRecenti.Visible = false;
                _carouselHost.Visible = false;

                HideMask();
                _grid.Controls.Clear();

                _ytPane ??= new YouTubePane(url => SafeOpen(url));
                _ytPane.Dock = DockStyle.Top;

                _grid.Controls.Add(_ytPane);
                _grid.Visible = true;
                _grid.UpdateThemedScrollbar();
                return;
            }
            // ----- sorgente DLNA -----
            else if (_selSrc == "Rete domestica")
            {
                // NEW: mai carosello in DLNA
                _secRecenti.Visible = false;
                _carouselHost.Visible = false;

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

            // reset visibilità sezioni / carosello per lo stato “normale”
            bool isPlaylist = string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase);
            bool isPreferiti = string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase);
            bool isFoto = string.Equals(_selCat, "Foto", StringComparison.OrdinalIgnoreCase);
            bool isUrlSrc = string.Equals(_selSrc, "URL", StringComparison.OrdinalIgnoreCase);
            bool isYtSrc = string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase);

            // niente carosello per Playlist / Preferiti / Foto e per le sorgenti URL / YouTube
            bool showCarousel = !(isPlaylist || isPreferiti || isFoto || isUrlSrc || isYtSrc);

            _secAll.Visible = true;
            _secRecenti.Visible = showCarousel;
            _carouselHost.Visible = showCarousel;

            // se siamo su "Il mio computer" e non ci sono cartelle configurate
            // per Film/Video/Foto/Musica → schermata vuota full-page
            if (string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase)
                && IsLocalLibraryCategory(_selCat)
                && rootsList.Count == 0)
            {
                HideMask();

                // qui vogliamo SOLO l’empty-state centrale
                _secAll.Visible = false;
                _secRecenti.Visible = false;
                _carouselHost.Visible = false;

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
                    // NON facciamo File.Exists qui: creiamo solo i FileInfo
                    foreach (var p in stored)
                    {
                        try
                        {
                            initial.Add(new FileInfo(p));
                        }
                        catch { }
                    }

                    // pulizia dei path inesistenti in background, per non bloccare l'UI
                    Task.Run(() =>
                    {
                        try { _libraryIndex.RemoveMissing(catNow); }
                        catch { }
                    }, ct);
                }
            }

            bool hadIndexInitial = initial.Count > 0;

            if (hadIndexInitial)
            {
                lock (_cacheLock)
                    _cache = initial;

                ApplyFilterAndRender();
            }
            else
            {
                if (useIndex)
                {
                    // per la prima indicizzazione locale mostriamo la mask con lo spinner
                    ShowMask("Caricamento libreria in corso…");

                    _grid.Controls.Clear();
                    _grid.Controls.Add(new InfoRow("Indicizzazione della libreria in corso…"));
                    _grid.UpdateThemedScrollbar();
                }
                else
                {
                    // per le altre sorgenti (es. DLNA) la mask ha già senso
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

                        // Se avevamo già mostrato roba dall'indice JSON, NON rifacciamo la griglia:
                        // aggiorniamo solo indice + recents → niente "lampeggio" improvviso.
                        bool shouldRerender = !(useIndex && hadIndexInitial);

                        if (shouldRerender)
                        {
                            // ApplyFilterAndRender si occuperà di togliere la mask quando i dati sono pronti
                            ApplyFilterAndRender();

                            _grid.UpdateThemedScrollbar();
                            _header.Invalidate(true); _header.Update();
                            _grid.Invalidate(true); _grid.Update();
                        }
                        else
                        {
                            // qui davvero non rifacciamo nulla, quindi la mask si può togliere
                            HideMask();
                        }
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
            // snapshot
            List<FileInfo> cacheSnapshot;
            lock (_cacheLock) cacheSnapshot = _cache.ToList();

            var category = _selCat;
            var srcSel = _selSrc;
            var selExt = _selExt;
            var sortIndex = _sortIndex;
            var queryLower = (_search.Text ?? string.Empty)
                .Trim()
                .ToLowerInvariant();

            _filterCts?.Cancel();
            _filterCts = new CancellationTokenSource();
            var ct = _filterCts.Token;

            Task.Run(() =>
            {
                var filtered = BuildFilteredListCore(
                    cacheSnapshot,
                    category,
                    selExt,
                    sortIndex,
                    queryLower,
                    ct);

                if (ct.IsCancellationRequested || IsDisposed)
                    return;

                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || ct.IsCancellationRequested)
                            return;

                        // se nel frattempo l'utente è andato altrove, ignora
                        if (!string.Equals(_selCat, category, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(_selSrc, srcSel, StringComparison.OrdinalIgnoreCase))
                            return;

                        // <<< QUI: dati pronti, togliamo la mask e partiamo col render >>>
                        HideMask();
                        StartProgressiveRender(filtered);
                    }));
                }
                catch
                {
                    // form già distrutta, ignora
                }
            });
        }

        private List<FileInfo> BuildFilteredListCore(
        List<FileInfo> src,
        string category,
        string selExt,
        int sortIndex,
        string queryLower,
        CancellationToken ct)
        {
            // categoria Preferiti → rileggo direttamente dai preferiti
            if (string.Equals(category, "Preferiti", StringComparison.OrdinalIgnoreCase))
            {
                var favs = _favs.All()
                    .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    .Select(p =>
                    {
                        if (ct.IsCancellationRequested) return null;
                        try { return new FileInfo(p); }
                        catch { return null; }
                    })
                    .Where(fi => fi != null)
                    .Cast<FileInfo>()
                    .ToList();

                src = favs;
            }

            if (ct.IsCancellationRequested)
                return new List<FileInfo>();

            // filtro testo (nome + percorso)
            if (!string.IsNullOrEmpty(queryLower))
            {
                var tokens = queryLower
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (tokens.Length > 0)
                {
                    src = src.Where(fi =>
                    {
                        if (ct.IsCancellationRequested) return false;

                        string name = fi.Name.ToLowerInvariant();
                        string path = (fi.DirectoryName ?? "").ToLowerInvariant();

                        foreach (var t in tokens)
                            if (!(name.Contains(t) || path.Contains(t)))
                                return false;
                        return true;
                    }).ToList();
                }
            }

            string catLower = category.ToLowerInvariant();
            bool isFilm = catLower == "film";
            bool isVideo = catLower == "video";

            if (isFilm || isVideo)
            {
                // Film/Video: split basato sulla durata
                src = src.Where(fi =>
                {
                    if (ct.IsCancellationRequested) return false;

                    var ext = (Path.GetExtension(fi.FullName) ?? "").ToLowerInvariant();
                    bool isMovieContainer = ext == ".mkv" || ext == ".mp4";

                    if (isFilm)
                    {
                        // FILM: solo mkv/mp4 con durata >= 40 minuti
                        if (!isMovieContainer)
                            return false;

                        var dur = GetDurationMinutesCached(fi.FullName);
                        if (!dur.HasValue)
                            return false;

                        return dur.Value >= 40.0;
                    }

                    // VIDEO: tutti i video, ma:
                    // - mkv/mp4 qui solo se < 40 min o durata ignota
                    // - altri container video sempre qui
                    if (!IsAnyVideoExtension(ext))
                        return false;

                    if (!isMovieContainer)
                        return true;

                    var d = GetDurationMinutesCached(fi.FullName);
                    if (!d.HasValue)
                        return true;

                    return d.Value < 40.0;
                }).ToList();
            }
            else
            {
                // altre categorie: filtro per estensione
                var allowed = new HashSet<string>(ExtsForCategory(category), StringComparer.OrdinalIgnoreCase);
                src = src.Where(fi =>
                {
                    if (ct.IsCancellationRequested) return false;
                    return allowed.Contains(Path.GetExtension(fi.FullName));
                }).ToList();
            }

            // filtro chip estensione
            if (!string.Equals(selExt, "Tutte", StringComparison.OrdinalIgnoreCase))
            {
                src = src.Where(fi =>
                {
                    if (ct.IsCancellationRequested) return false;
                    return string.Equals(
                        Path.GetExtension(fi.FullName),
                        selExt,
                        StringComparison.OrdinalIgnoreCase);
                }).ToList();
            }

            if (ct.IsCancellationRequested)
                return new List<FileInfo>();

            // ordinamento
            src = sortIndex switch
            {
                1 => src.OrderBy(fi => fi.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                2 => src.OrderByDescending(fi => fi.Length).ToList(),
                _ => src.OrderByDescending(fi => fi.LastWriteTimeUtc).ToList()
            };

            if (ct.IsCancellationRequested)
                return new List<FileInfo>();

            return src.ToList();
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
                _grid.Visible = true;                 // torna visibile con il messaggio
                _grid.UpdateThemedScrollbar();
                return;
            }

            _grid.Visible = true;                     // torna visibile prima che parta il render progressivo
            _progressiveTimer.Start();
        }

        private void ProgressiveTick()
        {
            if (_progressivePos >= _progressiveList.Count)
            {
                _progressiveTimer.Stop();
                _grid.UpdateThemedScrollbar();
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

                // thumb async (con poster online per Film)
                BeginThumbLoadForCard(card, path, _progressiveThumbToken);
            }

            // Aggiorniamo solo lo stato della scrollbar, la UI ridisegna il necessario da sola.
            _grid.UpdateThemedScrollbar();
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

        // Usa la larghezza del carosello (non della griglia) per stimare quante card stanno in riga
        private int EstimateCardsPerRowForCarousel()
        {
            int hostW = _carouselHost.ClientSize.Width;
            if (hostW <= 0)
                hostW = _carouselHost.Width;
            if (hostW <= 0 && _right != null)
                hostW = _right.ClientSize.Width - (_gridRightPad * 2);

            if (hostW <= 0)
                return 1;

            int itemOuter = _carouselViewport.GetItemOuterWidthEstimate();
            if (itemOuter <= 0)
                itemOuter = 320; // 300 card + 10+10 margini

            // piccolo margine per non appiccicare le card ai bordi
            int usable = Math.Max(0, hostW - 16);

            int cards = usable / itemOuter;
            if (cards < 1) cards = 1;
            if (cards > 6) cards = 6;

            return cards;
        }


    }
}
