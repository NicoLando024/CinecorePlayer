using System;
using System.Drawing;
using System.Windows.Forms;

#nullable enable

namespace CinecorePlayer2025
{
    /// <summary>
    /// Overlay modale full-screen in stile Cinecore.
    /// Da usare come base per tutte le finestre modali in-app.
    /// </summary>
    internal abstract class ModalBase : UserControl
    {
        // Card centrale (rettangolo pieno con bordo 1px Theme.Border)
        private readonly ModalCard _card;

        // Header base
        private readonly Label _lblTitle;
        private readonly Label _lblSubtitle;

        // Host per contenuto custom + bottoni
        protected readonly Panel ContentHost;
        protected readonly FlowLayoutPanel ButtonsHost;

        // bottone "predefinito" (invocato con INVIO)
        private ModalButton? _primaryButton;

        public event Action? Closed;

        /// <summary>Colore overlay dietro al modal.</summary>
        public Color OverlayColor { get; set; } = Theme.BackdropDim;

        /// <summary>Chiudi il modal cliccando sullo sfondo</summary>
        public bool CloseOnBackdropClick { get; set; } = true;

        /// <summary>Chiudi con ESC</summary>
        public bool CloseOnEscape { get; set; } = true;

        /// <summary>Rimuove e Dispose automaticamente il controllo dal parent quando si chiude.</summary>
        public bool AutoDisposeOnClose { get; set; } = true;

        public string TitleText
        {
            get => _lblTitle.Text;
            set => _lblTitle.Text = value;
        }

        public string SubtitleText
        {
            get => _lblSubtitle.Text;
            set
            {
                _lblSubtitle.Text = value ?? string.Empty;
                _lblSubtitle.Visible = !string.IsNullOrWhiteSpace(value);
            }
        }

        /// <summary>
        /// Crea un modal base con titolo e sottotitolo opzionali.
        /// </summary>
        protected ModalBase(string title = "", string? subtitle = null)
        {
            DoubleBuffered = true;
            Dock = DockStyle.Fill;
            BackColor = Color.Transparent;
            TabStop = true;

            // Gestione tasti
            SetStyle(ControlStyles.Selectable, true);
            KeyDown += ModalBase_KeyDown;

            // click sul background
            MouseDown += ModalBase_MouseDown;

            // card centrale (rettangolo pieno)
            _card = new ModalCard
            {
                BackColor = Theme.Panel,
                MinimumSize = new Size(900, 580),
                Padding = new Padding(0)
            };
            Controls.Add(_card);

            // ================== HEADER ==================
            var headerOuter = new Panel
            {
                Dock = DockStyle.Top,
                Height = 56,
                BackColor = Theme.Panel,
                Padding = new Padding(16, 10, 16, 0)
            };

            _lblTitle = new Label
            {
                Text = title,
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 26,
                ForeColor = Theme.Text,
                Font = new Font("Segoe UI Semibold", 13f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0)
            };

            _lblSubtitle = new Label
            {
                Text = subtitle ?? string.Empty,
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 20,
                ForeColor = Theme.SubtleText,
                Font = new Font("Segoe UI", 9.5f),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0),
                Visible = !string.IsNullOrWhiteSpace(subtitle)
            };

            headerOuter.Controls.Add(_lblSubtitle);
            headerOuter.Controls.Add(_lblTitle);

            // ================== CONTENT ==================
            ContentHost = new Panel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Margin = new Padding(0),
                BackColor = Theme.Panel,
                Padding = new Padding(0)
            };

            // ================== BOTTOM / BUTTONS ==================
            var bottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                BackColor = Theme.Panel,
                Padding = new Padding(0, 10, 16, 10)
            };

            ButtonsHost = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Right,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            bottom.Controls.Add(ButtonsHost);

            // ordine: header top, bottom bottom, content fill
            _card.Controls.Add(ContentHost);
            _card.Controls.Add(bottom);
            _card.Controls.Add(headerOuter);

            // centriamo la card e la teniamo bella grossa
            Resize += (_, __) => CenterCard();
            VisibleChanged += (_, __) =>
            {
                if (Visible)
                {
                    CenterCard();
                    Focus();
                }
            };
        }

        // ----------------- API PUBBLICA PER MOSTRARE/CHIUDERE -----------------

        /// <summary>
        /// Aggancia il modal a un container (tipicamente la Form principale).
        /// </summary>
        public void ShowOver(Control host)
        {
            Dock = DockStyle.Fill;
            host.Controls.Add(this);
            host.Controls.SetChildIndex(this, 0);
            BringToFront();
            Focus();
        }

        /// <summary>
        /// Chiudi il modal, sganciandolo dal parent.
        /// </summary>
        protected void CloseModal()
        {
            var parent = Parent;
            if (parent != null)
            {
                parent.Controls.Remove(this);
            }

            Closed?.Invoke();

            if (AutoDisposeOnClose)
                Dispose();
        }

        /// <summary>
        /// Crea un bottone primario (accent color) e lo aggiunge alla barra bottoni.
        /// </summary>
        protected ModalButton AddPrimaryButton(string text, Action onClick)
        {
            var btn = new ModalButton(text, ModalButton.Variant.Primary)
            {
                Margin = new Padding(8, 0, 0, 0)
            };
            btn.Click += (_, __) => onClick();

            ButtonsHost.Controls.Add(btn);

            // primo primario incontrato diventa default ENTER
            _primaryButton ??= btn;
            return btn;
        }

        /// <summary>
        /// Crea un bottone secondario (outline) e lo aggiunge alla barra bottoni.
        /// </summary>
        protected ModalButton AddSecondaryButton(string text, Action onClick)
        {
            var btn = new ModalButton(text, ModalButton.Variant.Secondary)
            {
                Margin = new Padding(8, 0, 0, 0)
            };
            btn.Click += (_, __) => onClick();

            ButtonsHost.Controls.Add(btn);
            return btn;
        }

        // ----------------- INPUT / LAYOUT -----------------

        private void CenterCard()
        {
            if (!IsHandleCreated) return;

            int margin = 32;

            int width = Math.Max(900, ClientSize.Width - margin * 2);
            int height = Math.Max(580, ClientSize.Height - margin * 2);

            // non sforare oltre i bordi
            width = Math.Min(width, ClientSize.Width - 8);
            height = Math.Min(height, ClientSize.Height - 8);

            _card.Size = new Size(width, height);

            int x = (ClientSize.Width - _card.Width) / 2;
            int y = (ClientSize.Height - _card.Height) / 2;

            if (x < margin) x = margin;
            if (y < margin) y = margin;

            _card.Location = new Point(x, y);
            _card.BringToFront();
        }

        private void ModalBase_MouseDown(object? sender, MouseEventArgs e)
        {
            if (!CloseOnBackdropClick)
                return;

            // se clicco fuori dalla card → chiudi
            if (!_card.Bounds.Contains(e.Location))
                CloseModal();
        }

        private void ModalBase_KeyDown(object? sender, KeyEventArgs e)
        {
            if (CloseOnEscape && e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                CloseModal();
                return;
            }

            if (e.KeyCode == Keys.Enter && _primaryButton != null)
            {
                e.Handled = true;
                _primaryButton.TriggerClick();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // overlay scuro dietro
            var g = e.Graphics;
            using var br = new SolidBrush(OverlayColor);
            g.FillRectangle(br, ClientRectangle);
        }

        // ===================== CLASSI INTERNE =====================

        /// <summary>
        /// Card centrale: riempita con Theme.Panel + bordo 1px Theme.Border, spigoli vivi.
        /// </summary>
        private sealed class ModalCard : Panel
        {
            public ModalCard()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

                var rect = new Rectangle(0, 0, Width - 1, Height - 1);

                using (var br = new SolidBrush(Theme.Panel))
                    g.FillRectangle(br, rect);

                using var pen = new Pen(Theme.Border, 1f);
                g.DrawRectangle(pen, rect);
            }
        }

        /// <summary>Bottone stile Cinecore per i modal.</summary>
        protected sealed class ModalButton : Control
        {
            public enum Variant { Primary, Secondary }

            private readonly Variant _variant;
            private bool _hover;
            private bool _down;
            private readonly string _text;

            public ModalButton(string text, Variant variant)
            {
                _text = text;
                _variant = variant;

                Cursor = Cursors.Hand;
                Size = new Size(140, 32);
                TabStop = true;

                SetStyle(ControlStyles.AllPaintingInWmPaint
                       | ControlStyles.OptimizedDoubleBuffer
                       | ControlStyles.UserPaint
                       | ControlStyles.ResizeRedraw
                       | ControlStyles.Selectable, true);

                BackColor = Color.Transparent;

                MouseEnter += (_, __) => { _hover = true; Invalidate(); };
                MouseLeave += (_, __) => { _hover = false; _down = false; Invalidate(); };
                MouseDown += (_, e) =>
                {
                    if (e.Button == MouseButtons.Left)
                    {
                        _down = true;
                        Invalidate();
                    }
                };
                MouseUp += (_, e) =>
                {
                    if (_down && e.Button == MouseButtons.Left)
                    {
                        _down = false;
                        Invalidate();
                        OnClick(EventArgs.Empty);
                    }
                };

                KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
                    {
                        e.Handled = true;
                        OnClick(EventArgs.Empty);
                    }
                };
            }

            internal void TriggerClick()
            {
                OnClick(EventArgs.Empty);
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Color.Transparent);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);

                // rettangolo con angoli leggermente arrotondati visivi
                using var path = new System.Drawing.Drawing2D.GraphicsPath();
                int r = 4;
                int d = r * 2;
                path.AddArc(rect.X, rect.Y, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();

                if (_variant == Variant.Primary)
                {
                    var cTop = Theme.Accent;
                    var cBot = Theme.AccentSoft;

                    if (_down)
                    {
                        cTop = ControlPaint.Dark(cTop);
                        cBot = ControlPaint.Dark(cBot);
                    }
                    else if (_hover)
                    {
                        cTop = ControlPaint.Light(cTop);
                    }

                    using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                               rect, cTop, cBot,
                               System.Drawing.Drawing2D.LinearGradientMode.Vertical))
                    {
                        g.FillPath(lg, path);
                    }
                }
                else
                {
                    var baseCol = Theme.Panel;
                    if (_hover) baseCol = ControlPaint.Light(baseCol);
                    if (_down) baseCol = ControlPaint.Dark(baseCol);

                    using (var br = new SolidBrush(baseCol))
                        g.FillPath(br, path);

                    using var pen = new Pen(Theme.Border);
                    g.DrawPath(pen, path);
                }

                using var f = new Font("Segoe UI Semibold", 10.5f);
                TextRenderer.DrawText(
                    g,
                    _text,
                    f,
                    rect,
                    Color.White,
                    TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis);
            }
        }
    }
}
