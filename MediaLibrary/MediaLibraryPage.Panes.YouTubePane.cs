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
        // ------------ YouTube Pane per sorgente "YouTube" integrata ------------
        private sealed class YouTubePane : Panel
        {
            private readonly TextBox _tbSearch;
            private readonly Button _btnSearch;
            private readonly TextBox _tbUrl;
            private readonly Button _btnPlay;
            private readonly FlowLayoutPanel _results;
            private readonly Label _status;
            private readonly Action<string> _play;
            private CancellationTokenSource? _cts;

            public YouTubePane(Action<string> onPlay)
            {
                _play = onPlay;

                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;
                BackColor = Color.Black;
                Padding = new Padding(16, 12, 16, 12);

                var lbl = new Label
                {
                    Text = "Cerca un video su YouTube o incolla un link:",
                    Dock = DockStyle.Top,
                    Height = 20,
                    ForeColor = Theme.Text,
                    BackColor = Color.Black
                };
                Controls.Add(lbl);

                // --- riga ricerca ---
                var rowSearch = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Top,
                    BackColor = Color.Black,
                    Margin = new Padding(0, 8, 0, 0),
                    Padding = new Padding(0)
                };

                _tbSearch = new TextBox
                {
                    Width = 380,
                    Height = 30,
                    BorderStyle = BorderStyle.FixedSingle,
                    PlaceholderText = "Cerca titolo o canale…",
                    Font = new Font("Segoe UI", 10.5f)
                };

                _btnSearch = new Button
                {
                    Text = "Cerca",
                    Width = 90,
                    Height = 30,
                    FlatStyle = FlatStyle.Flat
                };
                _btnSearch.FlatAppearance.BorderSize = 0;
                _btnSearch.BackColor = Theme.Accent;
                _btnSearch.ForeColor = Color.White;

                _btnSearch.Click += async (_, __) => await DoSearchAsync();

                _tbSearch.KeyDown += async (_, e) =>
                {
                    if (e.KeyCode == Keys.Enter)
                    {
                        e.SuppressKeyPress = true;
                        await DoSearchAsync();
                    }
                };

                rowSearch.Controls.Add(_tbSearch);
                rowSearch.Controls.Add(_btnSearch);
                Controls.Add(rowSearch);

                // --- riga link diretto ---
                var rowUrl = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = false,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Top,
                    BackColor = Color.Black,
                    Margin = new Padding(0, 8, 0, 0),
                    Padding = new Padding(0)
                };

                _tbUrl = new TextBox
                {
                    Width = 380,
                    Height = 30,
                    BorderStyle = BorderStyle.FixedSingle,
                    PlaceholderText = "https://www.youtube.com/watch?v=…",
                    Font = new Font("Segoe UI", 10.5f)
                };

                _btnPlay = new Button
                {
                    Text = "Riproduci",
                    Width = 90,
                    Height = 30,
                    FlatStyle = FlatStyle.Flat
                };
                _btnPlay.FlatAppearance.BorderSize = 0;
                _btnPlay.BackColor = Theme.Accent;
                _btnPlay.ForeColor = Color.White;

                _btnPlay.Click += (_, __) => PlayDirectUrl();

                _tbUrl.KeyDown += (_, e) =>
                {
                    if (e.KeyCode == Keys.Enter)
                    {
                        e.SuppressKeyPress = true;
                        PlayDirectUrl();
                    }
                };

                rowUrl.Controls.Add(_tbUrl);
                rowUrl.Controls.Add(_btnPlay);
                Controls.Add(rowUrl);

                // --- status + risultati ---
                _status = new Label
                {
                    Text = "Risultati ricerca.",
                    AutoSize = true,
                    ForeColor = Theme.SubtleText,
                    BackColor = Color.Black,
                    Dock = DockStyle.Top,
                    Margin = new Padding(0, 8, 0, 4)
                };
                Controls.Add(_status);

                _results = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = true,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Top,
                    BackColor = Color.Black,
                    Margin = new Padding(0, 4, 0, 0),
                    Padding = new Padding(0)
                };
                Controls.Add(_results);
            }

            private void PlayDirectUrl()
            {
                var s = (_tbUrl.Text ?? "").Trim();
                if (!IsYouTubeUrl(s))
                {
                    SystemSounds.Beep.Play();
                    return;
                }

                _play(s);
            }

            private static bool IsYouTubeUrl(string url)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
                    return false;

                if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
                    return false;

                var host = u.Host.ToLowerInvariant();
                return host.Contains("youtube.com") || host.Contains("youtu.be");
            }

            private async Task DoSearchAsync()
            {
                var q = (_tbSearch.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(q))
                {
                    SystemSounds.Beep.Play();
                    return;
                }

                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;

                _status.Text = "Ricerca YouTube in corso…";
                _results.Controls.Clear();

                try
                {
                    var url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(q);

                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                    using var resp = await _http.SendAsync(
                        req,
                        HttpCompletionOption.ResponseContentRead,
                        ct);

                    resp.EnsureSuccessStatusCode();
                    var html = await resp.Content.ReadAsStringAsync(ct);

                    var results = ParseResults(html).Take(24).ToList();
                    if (ct.IsCancellationRequested) return;

                    if (results.Count == 0)
                    {
                        _status.Text = "Nessun risultato trovato.";
                        return;
                    }

                    foreach (var r in results)
                    {
                        var tile = new RemoteTile(
                            r.Title,
                            r.ChannelTitle ?? r.Url,
                            () => _play(r.Url),
                            w: 360,
                            h: 76);

                        _results.Controls.Add(tile);
                    }

                    _status.Text = $"{results.Count} risultati.";
                }
                catch (OperationCanceledException)
                {
                    // ignorata
                }
                catch
                {
                    _status.Text = "Errore nella ricerca YouTube.";
                }
            }

            private sealed class YouTubeResult
            {
                public string VideoId = "";
                public string Title = "";
                public string? ChannelTitle;
                public string Url = "";
            }

            private static IEnumerable<YouTubeResult> ParseResults(string html)
            {
                var list = new List<YouTubeResult>();
                if (string.IsNullOrEmpty(html)) return list;

                // pattern best-effort: videoId + title dal blob ytInitialData
                var rx = new Regex(
                    "\"videoId\":\"(?<id>[^\"]+)\"[^\"]+\"title\":\\{\"runs\":\\[\\{\"text\":\"(?<title>[^\"]+)\"",
                    RegexOptions.Compiled | RegexOptions.CultureInvariant);

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (Match m in rx.Matches(html))
                {
                    var id = m.Groups["id"].Value;
                    var title = WebUtility.HtmlDecode(m.Groups["title"].Value);

                    if (string.IsNullOrEmpty(id) || !seen.Add(id))
                        continue;

                    list.Add(new YouTubeResult
                    {
                        VideoId = id,
                        Title = title,
                        ChannelTitle = null,
                        Url = "https://www.youtube.com/watch?v=" + id
                    });
                }

                return list;
            }
        }


    }
}
