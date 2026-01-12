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

            // 1) cache in RAM (valida solo per questa sessione)
            lock (_durationLock)
            {
                if (_durationCache.TryGetValue(path, out var cached))
                    return cached;
            }

            // 2) cache persistente su disco (JSON)
            var persisted = _durationIndex.TryGet(path);
            if (persisted.HasValue)
            {
                // mettiamo comunque anche in RAM per le chiamate successive
                lock (_durationLock)
                {
                    _durationCache[path] = persisted;
                }
                return persisted;
            }

            // 3) se non c'è né in RAM né su disco, chiediamo alla Shell
            double? duration = null;
            try
            {
                duration = ShellDurationUtil.TryGetDurationMinutes(path);
            }
            catch
            {
                // best-effort: se fallisce lasciamo duration = null
            }

            // 4) salviamo sia in RAM che nel JSON persistente
            lock (_durationLock)
            {
                _durationCache[path] = duration;
            }

            // se riesce a leggere qualcosa, salviamo su disco
            _durationIndex.Set(path, duration);

            return duration;
        }


    }
}
