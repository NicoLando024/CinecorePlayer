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
                _grid.Visible = true;
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

            _grid.Visible = true;
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
                _grid.Visible = true;
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
            _grid.Visible = true;
            _grid.UpdateThemedScrollbar();
        }


    }
}
