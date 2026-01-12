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

            // layout verticale, contenuto “nudo” su sfondo nero
            _grid.FlowDirection = FlowDirection.TopDown;
            _grid.WrapContents = false;

            _grid.SuspendLayout();
            _grid.Controls.Clear();

            // area utile (senza i padding della grid)
            int maxContentWidth = _grid.ClientSize.Width - _grid.Padding.Left - _grid.Padding.Right;
            if (maxContentWidth < 520) maxContentWidth = 520;

            // blocco contenuto centrato, largo ma non a piena pagina
            int blockWidth = Math.Min(980, maxContentWidth);
            int leftMargin = Math.Max(0, (maxContentWidth - blockWidth) / 2);

            // ---- HERO IMAGE (sopra al testo) ----
            var hero = new PictureBox
            {
                Width = blockWidth,
                Height = (int)Math.Round(blockWidth * 9.0 / 16.0), // 16:9
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black,
                Margin = new Padding(leftMargin, 64, 0, 18)
            };
            try
            {
                using var bmp = GetEmptyStateImage(category, blockWidth);
                // clone per evitare file lock
                hero.Image = new Bitmap(bmp);
            }
            catch { /* se fallisce resta nero */ }
            _grid.Controls.Add(hero);

            // testo largo
            string lower = category.ToLowerInvariant();
            string nice = char.ToUpper(lower[0]) + lower.Substring(1);

            var title = new Label
            {
                Text = $"Nessuna cartella configurata per {nice}.",
                AutoSize = true,
                MaximumSize = new Size(blockWidth, 0),
                ForeColor = Color.White,
                BackColor = Color.Black,
                Font = new Font("Segoe UI Semibold", 18f),
                Margin = new Padding(leftMargin, 6, 0, 8)
            };
            _grid.Controls.Add(title);

            var subtitle = new Label
            {
                Text = $"Aggiungi una o più cartelle o dischi (es. \"D:\\\") da cui caricare i tuoi {lower}. " +
                       "Puoi modificarle in qualsiasi momento con il pulsante «Cartelle…» in alto.",
                AutoSize = true,
                MaximumSize = new Size(blockWidth, 0),
                ForeColor = Theme.SubtleText,
                BackColor = Color.Black,
                Font = new Font("Segoe UI", 11.5f),
                Margin = new Padding(leftMargin, 0, 0, 18)
            };
            _grid.Controls.Add(subtitle);

            // riga bottoni
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Black,
                Margin = new Padding(leftMargin, 8, 0, 56),
                Padding = new Padding(0)
            };
            _grid.Controls.Add(row);

            var btnAdd = new FlatButton("Aggiungi cartella…", FlatButton.Variant.Primary)
            {
                Width = 220,
                Height = 34,
                Margin = new Padding(0, 0, 12, 0)
            };
            btnAdd.Click += (_, __) =>
            {
                // qui vogliamo SOLO aggiungere i percorsi, senza far partire subito la scansione
                AddFolderForCurrentCategory(refreshAfterAdd: false);
            };
            row.Controls.Add(btnAdd);

            // nuovo bottone "Fine": solo quando l'utente ha finito di aggiungere cartelle
            var btnDone = new FlatButton("Fine", FlatButton.Variant.Secondary)
            {
                Width = 140,
                Height = 34,
                Margin = new Padding(0, 0, 0, 0)
            };
            btnDone.Click += (_, __) =>
            {
                SetCategory(_selCat);
            };
            row.Controls.Add(btnDone);

            _grid.ResumeLayout(true);
            _grid.Visible = true;              // rimetti la griglia visibile
            _grid.UpdateThemedScrollbar();
            HideMask();
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
        private static bool IsMusicFilePath(string path)
        {
            var ext = (Path.GetExtension(path) ?? string.Empty);
            return ExtsForCategory("Musica")
                .Contains(ext, StringComparer.OrdinalIgnoreCase);
        }


    }
}
