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
        // ------------ padding condiviso fra carosello, sezioni e griglia ------------
        private void ApplyContentSidePad()
        {
            _grid.Padding = new Padding(_contentSidePad, 8, _gridRightPad, 8);

            _secRecenti.LeftMargin = _contentSidePad;
            _secAll.LeftMargin = _contentSidePad;

            _secRecenti.Invalidate();
            _secAll.Invalidate();
        }


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
            if (IsDisposed)
                return;

            if (!_carouselHost.Visible)
                return;

            int hostW = _carouselHost.ClientSize.Width;
            if (hostW <= 0)
                hostW = _carouselHost.Width;
            if (hostW <= 0 && _right != null)
                hostW = _right.ClientSize.Width - (_gridRightPad * 2);

            if (hostW <= 0)
                return;

            int itemOuter = _carouselViewport.GetItemOuterWidthEstimate();
            if (itemOuter <= 0)
                itemOuter = 320; // 300 + margini

            int cardsPerRow = EstimateCardsPerRowForCarousel();
            int desiredW = cardsPerRow * itemOuter;

            if (desiredW > hostW)
                desiredW = hostW;

            int x = (hostW - desiredW) / 2;
            if (x < 0) x = 0;

            _contentSidePad = x;
            ApplyContentSidePad();

            int desiredH = _carouselViewport.GetPreferredHeightEstimate();
            if (desiredH <= 0)
                desiredH = 236;

            _carouselViewport.Size = new Size(desiredW, desiredH);
            _carouselViewport.Location = new Point(x, 8);

            // PRIMA: controllavi anche l’overflow orizzontale → spesso rimanevano invisibili
            // bool needArrows =
            //     _carouselViewport.ItemsCount > 1 &&
            //     _carouselViewport.HasHorizontalOverflow();

            // ADESSO: se ci sono almeno 2 card, mostrami sempre le frecce
            bool needArrows = _carouselViewport.ItemsCount > 1;

            _carPrev.Visible = needArrows;
            _carNext.Visible = needArrows;

            LayoutCarouselArrows();
            LayoutResumeEmptyLabel();
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
            if (string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase))
            {
                _secRecenti.Visible = false;
                _carouselHost.Visible = false;
                return;
            }

            bool isPlaylist = string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase);
            bool isPreferiti = string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase);
            bool isFoto = string.Equals(_selCat, "Foto", StringComparison.OrdinalIgnoreCase);
            bool isUrlSrc = string.Equals(_selSrc, "URL", StringComparison.OrdinalIgnoreCase);
            bool isYtSrc = string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase);

            bool showCarousel = !(isPlaylist || isPreferiti || isFoto || isUrlSrc || isYtSrc);

            _secRecenti.Visible = showCarousel;
            _carouselHost.Visible = showCarousel;
            if (!showCarousel)
                return;

            // NEW: per la categoria Musica il carosello è "recenti" slegato dai minutaggi
            if (string.Equals(_selCat, "Musica", StringComparison.OrdinalIgnoreCase))
            {
                LoadMusicRecentsCarousel();
                return;
            }

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

                    // thumb async poi (con poster online per Film)
                    BeginThumbLoadForCard(card, path, token);
                });
            AlignCarouselViewport();
        }

        private void LoadMusicRecentsCarousel()
        {
            var all = _musicRecents.All()
                .Where(p => !string.IsNullOrWhiteSpace(p) && IsMusicFilePath(p))
                .Where(p =>
                {
                    // se è URL http/https lo accettiamo sempre,
                    // se è path locale verifichiamo che il file esista ancora
                    if (Uri.TryCreate(p, UriKind.Absolute, out var u) &&
                        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                        return true;

                    return File.Exists(p);
                })
                .Take(30)
                .ToList();

            if (all.Count == 0)
            {
                _carouselViewport.ResetItems(
                    new List<string>(),
                    GetOrNewThumbCts().Token,
                    _ => { },
                    (_, __) => { });

                _resumeEmptyLabel.Text = "Nessun brano riprodotto di recente.";
                _carPrev.Visible = false;
                _carNext.Visible = false;
                _resumeEmptyLabel.Visible = true;
                AlignCarouselViewport();
                return;
            }

            _resumeEmptyLabel.Visible = false;

            var token = GetOrNewThumbCts().Token;
            var paths = all;

            _carouselViewport.ResetItems(
                paths,
                token,
                path => SafeOpen(path),              // NO ripresa, apertura semplice
                (path, card) =>
                {
                    // placeholder categoria musica
                    var phBmp = GetCategoryPlaceholder("musica", 520);
                    card.SetInitialPlaceholder(phBmp);

                    // niente progress bar (non chiamiamo SetProgress)

                    // thumb async
                    BeginThumbLoadForCard(card, path, token);
                });
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


    }
}
