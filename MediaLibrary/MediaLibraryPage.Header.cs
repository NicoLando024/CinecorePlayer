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
                _btnBrowse, _btnRefresh, _btnManageFolders, _btnAddFolder, _chipSort, _chipExt
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

                // browse | cartelle | aggiungi cartella | refresh | sort | ext a destra
                _btnBrowse.Location = new Point(
                    right - _btnBrowse.Width,
                    (_header.Height - _btnBrowse.Height) / 2);
                right -= _btnBrowse.Width + 10;

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

                if (_btnRefresh.Visible)
                {
                    _btnRefresh.Location = new Point(
                        right - _btnRefresh.Width,
                        (_header.Height - _btnRefresh.Height) / 2);
                    right -= _btnRefresh.Width + 10;
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

                if (_btnRefresh.Visible)
                {
                    _btnRefresh.Location = new Point(x, y2);
                    x += _btnRefresh.Width + 8;
                }

                if (_btnManageFolders.Visible)
                {
                    _btnManageFolders.Location = new Point(x, y2);
                    x += _btnManageFolders.Width + 8;
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

        public List<string> transport_folder_name = new List<string>();

        private bool AddFolderForCurrentCategory(bool refreshAfterAdd)
        {
            if (IsDisposed) return false;

            var cat = _selCat;

            // solo Film / Video / Foto / Musica
            if (!IsLocalLibraryCategory(cat))
                return false;

            // solo sorgente "Il mio computer"
            if (!string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase))
                return false;

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
                return false;

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
                return false;
            }
            transport_folder_name.Add(path);
            // salva il nuovo root (cartella o disco intero)
            _roots.Add(cat, path);

            // svuota l'indice così la categoria viene reindicizzata da zero
            _libraryIndex.ReplacePaths(cat, Array.Empty<string>());

            if (refreshAfterAdd)
            {
                // parte subito la scansione con i nuovi percorsi
                RefreshContent();
            }

            return true;
        }

        // wrapper per il comportamento "classico" (aggiungi + scansiona subito)
        private void AddFolderForCurrentCategory()
        {
            _ = AddFolderForCurrentCategory(refreshAfterAdd: true);
        }
        private void ManageFoldersForCurrentCategory()
        {
            // solo per Film/Video/Foto/Musica sulla sorgente "Il mio computer"
            if (!IsLocalLibraryCategory(_selCat) ||
                !string.Equals(_selSrc, "Il mio computer", StringComparison.OrdinalIgnoreCase))
                return;

            ShowRootsOverlay();
        }

    }
}
