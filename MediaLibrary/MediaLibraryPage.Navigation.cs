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

            // NEW: titolo sezione diversi per Musica vs resto
            if (string.Equals(_selCat, "Musica", StringComparison.OrdinalIgnoreCase))
                _secRecenti.Title = "Recenti";
            else
                _secRecenti.Title = "Riprendi";

            if (_rootsOverlay != null)
                _rootsOverlay.Visible = false;

            // se siamo su DLNA → niente carosello, niente RefreshContent
            if (string.Equals(_selSrc, "Rete domestica", StringComparison.OrdinalIgnoreCase))
            {
                _secRecenti.Visible = false;
                _carouselHost.Visible = false;

                RefreshNavPaint();
                AlignCarouselViewport();
                return;
            }

            _recents.PruneToCategory(_selCat, ExtsForCategory(_selCat));

            BuildHeaderFilters();

            bool isPlaylist = string.Equals(_selCat, "Playlist", StringComparison.OrdinalIgnoreCase);
            bool isPreferiti = string.Equals(_selCat, "Preferiti", StringComparison.OrdinalIgnoreCase);
            bool isFoto = string.Equals(_selCat, "Foto", StringComparison.OrdinalIgnoreCase);
            bool isUrlSrc = string.Equals(_selSrc, "URL", StringComparison.OrdinalIgnoreCase);
            bool isYtSrc = string.Equals(_selSrc, "YouTube", StringComparison.OrdinalIgnoreCase);

            bool showCarousel = !(isPlaylist || isPreferiti || isFoto || isUrlSrc || isYtSrc);

            _secRecenti.Visible = showCarousel;
            _carouselHost.Visible = showCarousel;

            if (showCarousel)
                ShowMask("Caricamento elementi recenti…");

            LoadRecentsCarouselImmediate();

            RefreshContent();

            RefreshNavPaint();
            AlignCarouselViewport();
        }

        // ⬇️ RIMETTI / LASCIA COSÌ QUESTO
        private void SetSource(string s)
        {
            _selSrc = s;

            if (_rootsOverlay != null)
                _rootsOverlay.Visible = false;

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


    }
}
