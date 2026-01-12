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
        // ------------ OVERLAY GESTIONE CARTELLE (sopra al pannello destro) ------------
        private void BuildRootsOverlay()
        {
            _rootsOverlay = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(180, 10, 10, 14), // vetro scuro
                Visible = false
            };

            var content = new Panel
            {
                BackColor = Theme.PanelAlt,
                Size = new Size(720, 420),
                Padding = new Padding(20),
                // un minimo di "card" visiva
                BorderStyle = BorderStyle.FixedSingle
            };

            // funzione locale per centrare il contenuto in mezzo al pannello
            void CenterContent()
            {
                if (_rootsOverlay == null) return;
                if (_rootsOverlay.ClientSize.Width <= 0 || _rootsOverlay.ClientSize.Height <= 0)
                    return;

                content.Left = (_rootsOverlay.ClientSize.Width - content.Width) / 2;
                content.Top = (_rootsOverlay.ClientSize.Height - content.Height) / 2;
            }

            _rootsOverlay.Resize += (_, __) => CenterContent();
            _rootsOverlay.Controls.Add(content);

            // --- HEADER (titolo + sottotitolo) ---
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 72,
                BackColor = Theme.PanelAlt
            };

            var lblTitle = new Label
            {
                Text = "Cartelle per la categoria corrente",
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 26,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 12f),
                Padding = new Padding(4, 0, 4, 0)
            };
            header.Controls.Add(lblTitle);

            var lblSub = new Label
            {
                Text = "Qui vedi tutte le cartelle configurate per questa categoria.\r\n" +
                       "Rimuovere una cartella la toglie solo dalla libreria, NON cancella i file.",
                AutoSize = false,
                Dock = DockStyle.Fill,
                ForeColor = Theme.SubtleText,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.5f),
                Padding = new Padding(4, 4, 4, 0)
            };
            header.Controls.Add(lblSub);

            // --- LISTA CARTELLE (scrollabile) ---
            _rootsOverlayList = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Theme.Panel
            };

            // --- BARRA BOTTONI IN BASSO ---
            var bottomBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                BackColor = Theme.PanelAlt
            };

            var btnCloseOnly = new FlatButton("Chiudi", FlatButton.Variant.Secondary)
            {
                Width = 110,
                Height = 32,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };
            bottomBar.Controls.Add(btnCloseOnly);

            var btnCloseAndScan = new FlatButton("Chiudi e aggiorna", FlatButton.Variant.Primary)
            {
                Width = 160,
                Height = 32,
                Anchor = AnchorStyles.Right | AnchorStyles.Top
            };
            bottomBar.Controls.Add(btnCloseAndScan);

            bottomBar.Resize += (_, __) =>
            {
                // pulsanti allineati a destra
                btnCloseAndScan.Left = bottomBar.Width - btnCloseAndScan.Width - 10;
                btnCloseAndScan.Top = 9;

                btnCloseOnly.Left = btnCloseAndScan.Left - btnCloseOnly.Width - 8;
                btnCloseOnly.Top = 9;
            };

            // Chiudi SOLO overlay, NON rifare scansione
            btnCloseOnly.Click += (_, __) =>
            {
                _rootsOverlay.Visible = false;
            };

            // Chiudi + rifai indicizzazione categoria corrente
            btnCloseAndScan.Click += (_, __) =>
            {
                _rootsOverlay.Visible = false;
                ForceRescanCurrentCategory();
            };

            // AGGIUNGI I CONTROLLI IN QUESTO ORDINE
            content.Controls.Add(_rootsOverlayList); // Fill per primo
            content.Controls.Add(bottomBar);         // poi Bottom
            content.Controls.Add(header);            // poi Top

            CenterContent();
        }

        private void ShowRootsOverlay()
        {
            RefreshRootsOverlayList();
            _rootsOverlay.Visible = true;
            _rootsOverlay.BringToFront();
        }

        private void RefreshRootsOverlayList()
        {
            if (_rootsOverlayList == null) return;

            _rootsOverlayList.SuspendLayout();
            _rootsOverlayList.Controls.Clear();

            // usiamo le cartelle salvate per la categoria corrente
            var rootsForCat = _roots.Get(_selCat);

            if (rootsForCat.Count == 0)
            {
                var empty = new Label
                {
                    Text = "Nessuna cartella configurata per questa categoria.",
                    AutoSize = true,
                    ForeColor = Theme.SubtleText,
                    BackColor = Color.Transparent,
                    Margin = new Padding(8, 8, 8, 8)
                };
                _rootsOverlayList.Controls.Add(empty);
                _rootsOverlayList.ResumeLayout();
                return;
            }

            int width = _rootsOverlayList.ClientSize.Width;
            if (width <= 0) width = 680;

            foreach (var folder in rootsForCat)
            {
                var row = new Panel
                {
                    Height = 40,
                    Width = width - 24,
                    BackColor = Theme.Card,
                    Margin = new Padding(8, 6, 8, 0),
                    Padding = new Padding(8, 8, 8, 8)
                };

                var lbl = new Label
                {
                    Text = folder,
                    Dock = DockStyle.Fill,
                    AutoEllipsis = true,
                    ForeColor = Theme.Text,
                    BackColor = Color.Transparent
                };

                var btnDelete = new FlatButton("Rimuovi", FlatButton.Variant.Secondary)
                {
                    Width = 100,
                    Height = 24,
                    Dock = DockStyle.Right
                };

                btnDelete.Click += (_, __) =>
                {
                    // togli la cartella solo dalla lista radici salvate
                    _roots.Remove(_selCat, folder);
                    // l’indice vero lo ricalcoliamo quando chiudi con "Chiudi e aggiorna"
                    RefreshRootsOverlayList();
                };

                row.Controls.Add(btnDelete);
                row.Controls.Add(lbl);
                _rootsOverlayList.Controls.Add(row);
            }

            _rootsOverlayList.ResumeLayout();
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


        // ------------ FileCard (griglia e carosello) ------------
        private sealed class FileCard : Control
        {
            private readonly string _path;
            private string _displayName;
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
                _displayName = Path.GetFileName(path);
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
            public void SetDisplayName(string name)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                _displayName = name;
                Invalidate();
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

                string fileName = _displayName;
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

            // NUOVO: quante card ci sono nel carosello
            public int ItemsCount => _flow.Controls.Count;

            // NUOVO: c'è overflow orizzontale?
            public bool HasHorizontalOverflow()
            {
                int contentW = TotalContentWidth();
                int viewW = ClientSize.Width;
                if (viewW <= 0) viewW = Width;
                return contentW > viewW && contentW > 0;
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

        // chiamato quando MovieMetadataService segnala che l'indice poster è stato aggiornato
        private void OnPostersChanged()
        {
            if (IsDisposed) return;

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action(OnPostersChanged));
                }
                catch
                {
                    // la pagina potrebbe essere già stata distrutta
                }
                return;
            }

            // non aggiorniamo subito: accendiamo solo il debounce del carosello
            _carouselPosterRefresh.Stop();
            _carouselPosterRefresh.Start();
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
            private string _text;
            public int LeftMargin { get; set; } = 104;

            public string Title
            {
                get => _text;
                set
                {
                    _text = value;
                    Invalidate();
                }
            }

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


    }
}
