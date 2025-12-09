using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class WebMediaResolver
{
    public struct Result
    {
        public string Url;          // URL principale (video o progressive)
        public string? AudioUrl;    // opzionale: audio separato (DASH) per YouTube
        public bool? ForceHasVideo; // lascialo quasi sempre null: fa decidere al Probe
    }

    public static int MaxYouTubeHeight { get; set; } = 0;

    public static Result? Resolve(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return null;

        // Decodifica eventuali &quot; e simili
        var decoded = WebUtility.HtmlDecode(rawUrl).Trim();

        // 0) Se nel testo ci sono già URL .m3u8/.mp4/.mp3 embedded → prova subito da lì
        var blobCandidate = TryExtractFromText(decoded, null);
        if (blobCandidate != null)
            return blobCandidate;

        // 1) Prova a interpretarlo come URI assoluto
        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri))
        {
            // Non è neanche un URI valido → abbiamo già provato come blob sopra, quindi basta
            return null;
        }

        // 2) Se è già un media “diretto” (.mp4, .m3u8, ecc.)
        if (LooksDirectMediaUrl(decoded))
        {
            return new Result
            {
                Url = decoded,
                AudioUrl = null,
                ForceHasVideo = GuessForceHasVideoFromExtension(decoded)
            };
        }

        var host = uri.Host.ToLowerInvariant();
        bool isYouTube =
            host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);

        // 3) YouTube → usa yt-dlp (STREAMING, niente file locali)
        if (isYouTube)
        {
            var yt = TryResolveViaYtDlp(decoded);
            if (yt != null)
                return yt;
            // se fallisce, andremo comunque nel resolver HTTP generico sotto
        }

        // 4) Resolver HTTP generico per tutti i siti NON YouTube (e fallback per YT se yt-dlp non c’è)
        var httpResult = TryResolveViaHttp(uri);
        if (httpResult != null)
            return httpResult;

        // 5) Ultimo tentativo: considera la stringa come blob di testo e cerca URL media dentro
        return TryExtractFromText(decoded, uri);
    }

    // ===================== YT-DLP SOLO PER YOUTUBE (STREAMING) =====================

    private static Result? TryResolveViaYtDlp(string url)
    {
        var ytdlp = TryFindExe("yt-dlp.exe") ?? TryFindExe("yt-dlp");
        if (ytdlp == null)
            return null;

        string formatSelector = BuildYtFormatSelector();
        var args =
            "--no-playlist " +
            "-f \"" + formatSelector + "\" " +
            "--get-url " +
            "\"" + url + "\"";

        var psi = new ProcessStartInfo
        {
            FileName = ytdlp,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        try
        {
            using var p = Process.Start(psi)!;

            // --get-url scrive SOLO URL su stdout (uno o più)
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);

            var lines = stdout
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (lines.Length == 0)
                return null;

            // Primo URL → video
            var videoUrl = lines[0];
            if (string.IsNullOrWhiteSpace(videoUrl))
                return null;

            // Secondo URL (se c’è) → audio separato
            string? audioUrl = null;
            if (lines.Length >= 2)
            {
                audioUrl = lines[1];
                if (string.IsNullOrWhiteSpace(audioUrl))
                    audioUrl = null;
            }

            return new Result
            {
                Url = videoUrl,
                AudioUrl = audioUrl,
                // è sicuramente un flusso video (DASH o progressive)
                ForceHasVideo = true
            };
        }
        catch
        {
            return null;
        }
    }

    // Costruisce il selettore -f per yt-dlp in base a MaxYouTubeHeight
    private static string BuildYtFormatSelector()
    {
        int max = MaxYouTubeHeight;

        // 0 o >=2160 → profilo originale: punta al 4K (e oltre) con fallback
        if (max <= 0 || max >= 2160)
        {
            return
                "bv*[dynamic_range^=HDR][height>=2160]+ba/" +
                "bv*[height>=2160]+ba/" +
                "bv*[height>=1440]+ba/" +
                "bv*[height>=1080]+ba/" +
                "b[height>=1080]/b";
        }

        // Limite 1440p (accetta ≤1440, preferisce 1440, poi ≥1080, poi qualsiasi ≤1440)
        if (max >= 1440)
        {
            return
                "bv*[dynamic_range^=HDR][height<=1440][height>=1440]+ba/" +
                "bv*[height<=1440][height>=1440]+ba/" +
                "bv*[height<=1440][height>=1080]+ba/" +
                "b[height<=1440][height>=1080]/" +
                "b[height<=1440]";
        }

        // Limite 1080p (o meno): preferisci 1080, poi >=720, poi qualsiasi <=1080
        return
            "bv*[height<=1080][height>=1080]+ba/" +
            "bv*[height<=1080][height>=720]+ba/" +
            "b[height<=1080][height>=720]/" +
            "b[height<=1080]";
    }

    // ===================== HTTP GENERICO (non YouTube) =====================

    private static Result? TryResolveViaHttp(Uri uri)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true
            };
            using var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/129.0 Safari/537.36");

            // 1) HEAD: content-type e redirect
            try
            {
                using var headReq = new HttpRequestMessage(HttpMethod.Head, uri);
                using var headRes = http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead)
                                        .GetAwaiter().GetResult();

                var finalUri = headRes.RequestMessage?.RequestUri ?? uri;
                var finalUrl = finalUri.ToString();
                var ct = headRes.Content.Headers.ContentType?.MediaType ?? string.Empty;

                bool isHlsHead =
                    IsHlsContentType(ct) ||
                    finalUrl.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0;

                // Per HLS non facciamo early-return qui: vogliamo scaricare il master
                if (!isHlsHead)
                {
                    if (IsMediaContentType(ct) || LooksDirectMediaUrl(finalUrl))
                    {
                        return new Result
                        {
                            Url = finalUrl,
                            AudioUrl = null,
                            ForceHasVideo = DetermineForceHasVideo(ct, finalUrl)
                        };
                    }
                }

                uri = finalUri; // continua con il GET dal URI finale
            }
            catch
            {
                // alcuni server non supportano HEAD, va bene ignorare
            }

            // 2) GET per avere gli header (e, se serve, il contenuto)
            using var getReq = new HttpRequestMessage(HttpMethod.Get, uri);
            using var getRes = http.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead)
                                   .GetAwaiter().GetResult();

            var finalUri2 = getRes.RequestMessage?.RequestUri ?? uri;
            var finalUrl2 = finalUri2.ToString();
            var ct2 = getRes.Content.Headers.ContentType?.MediaType ?? string.Empty;

            bool isHls =
                IsHlsContentType(ct2) ||
                finalUrl2.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0;

            // Caso HLS: prova a leggere il master e scegliere la migliore variante (track.m3u8)
            if (isHls)
            {
                var playlistText = getRes.Content.ReadAsStringAsync()
                                                 .GetAwaiter().GetResult();

                if (!string.IsNullOrWhiteSpace(playlistText))
                {
                    var bestVariant = TrySelectBestHlsVariant(playlistText, finalUri2);
                    if (!string.IsNullOrEmpty(bestVariant))
                    {
                        return new Result
                        {
                            Url = bestVariant,
                            AudioUrl = null,
                            ForceHasVideo = true // HLS video
                        };
                    }
                }

                // Fallback: usa comunque l’URL del master
                return new Result
                {
                    Url = finalUrl2,
                    AudioUrl = null,
                    ForceHasVideo = true
                };
            }

            // Non HLS ma file media diretto
            if (IsMediaContentType(ct2) || LooksDirectMediaUrl(finalUrl2))
            {
                return new Result
                {
                    Url = finalUrl2,
                    AudioUrl = null,
                    ForceHasVideo = DetermineForceHasVideo(ct2, finalUrl2)
                };
            }

            // Se non è HTML (o simili), ci fermiamo
            if (!IsHtmlLikeContentType(ct2))
                return null;

            // 3) HTML: estraiamo URL media, iframe, ecc.
            var rawHtml = getRes.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(rawHtml))
                return null;

            var html = WebUtility.HtmlDecode(rawHtml);

            var nestedPages = new List<string>();
            var candidates = ExtractMediaUrlsFromHtmlLikeText(html, finalUri2, nestedPages);
            var best = PickBestCandidate(candidates);
            if (best != null)
                return best;

            // Nessun media diretto nella pagina: prova a seguire le pagine annidate (iframe/embed)
            foreach (var nested in nestedPages)
            {
                if (string.IsNullOrWhiteSpace(nested))
                    continue;

                Uri? nestedUri = null;
                if (Uri.TryCreate(nested, UriKind.Absolute, out var abs))
                {
                    nestedUri = abs;
                }
                else if (finalUri2 != null && Uri.TryCreate(finalUri2, nested, out var rel))
                {
                    nestedUri = rel;
                }

                if (nestedUri == null)
                    continue;

                if (nestedUri.Host.Equals(finalUri2.Host, StringComparison.OrdinalIgnoreCase) &&
                    nestedUri.AbsolutePath.Equals(finalUri2.AbsolutePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var nestedResult = TryResolveViaHttp(nestedUri);
                if (nestedResult != null)
                    return nestedResult;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    // ===================== PARSING TESTO / HTML =====================

    private sealed class Candidate
    {
        public Candidate(string url, bool? forceHasVideo)
        {
            Url = url;
            ForceHasVideo = forceHasVideo;
        }
        public string Url { get; }
        public bool? ForceHasVideo { get; }
    }

    private static Result? TryExtractFromText(string text, Uri? baseUri)
    {
        var candidates = ExtractMediaUrlsFromHtmlLikeText(text, baseUri);
        return PickBestCandidate(candidates);
    }

    private static Result? PickBestCandidate(List<Candidate> candidates)
    {
        if (candidates == null || candidates.Count == 0)
            return null;

        // 0) Se ci sono URL HLS da ok.ru / okcdn li preferiamo (HLS → streaming a segmenti)
        var hlsOk = candidates.FirstOrDefault(c =>
        {
            if (c.Url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            try
            {
                var u = new Uri(c.Url);
                return u.Host.Contains("ok.ru", StringComparison.OrdinalIgnoreCase) ||
                       u.Host.Contains("okcdn.ru", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });

        if (hlsOk != null)
        {
            return new Result
            {
                Url = hlsOk.Url,
                AudioUrl = null,
                ForceHasVideo = hlsOk.ForceHasVideo ?? true
            };
        }

        // 1) Preferisci stream progressivi:
        //    - estensioni video classiche
        //    - oppure URL okcdn senza .m3u8/.mpd (videos[0].url di ok.ru)
        var progressive = candidates.FirstOrDefault(c =>
            HasVideoLikeExtension(c.Url) || IsOkCdnProgressiveUrl(c.Url));
        if (progressive != null)
        {
            return new Result
            {
                Url = progressive.Url,
                AudioUrl = null,
                ForceHasVideo = progressive.ForceHasVideo ?? true
            };
        }

        // 2) Poi HLS (.m3u8) generico
        var hls = candidates.FirstOrDefault(c =>
            c.Url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0);
        if (hls != null)
        {
            return new Result
            {
                Url = hls.Url,
                AudioUrl = null,
                ForceHasVideo = hls.ForceHasVideo ?? true
            };
        }

        // 3) Altrimenti prendi il primo
        var first = candidates[0];
        return new Result
        {
            Url = first.Url,
            AudioUrl = null,
            ForceHasVideo = first.ForceHasVideo
        };
    }

    private static List<Candidate> ExtractMediaUrlsFromHtmlLikeText(string text, Uri? baseUri)
    {
        return ExtractMediaUrlsFromHtmlLikeText(text, baseUri, null);
    }

    private static List<Candidate> ExtractMediaUrlsFromHtmlLikeText(
        string text,
        Uri? baseUri,
        List<string>? nestedPageUrls)
    {
        var list = new List<Candidate>();
        if (string.IsNullOrEmpty(text))
            return list;

        void AddMedia(string? rawUrl, bool? forceHasVideo)
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
                return;

            rawUrl = rawUrl.Trim().Trim('\'', '"');
            rawUrl = DecodeJsonUrl(rawUrl.Trim());

            // Se è un blob con dentro una m3u8 (ok.ru, ondemandHls, ecc.)
            if (rawUrl.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (rawUrl.Contains("{") || rawUrl.Contains("}") ||
                 rawUrl.IndexOf("ondemandHls", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var m = Regex.Match(rawUrl,
                    @"https?://[^\s""'<>]+\.m3u8[^\s""'<>]*",
                    RegexOptions.IgnoreCase);
                if (m.Success)
                    rawUrl = m.Value;
            }

            // Se è HLS, taglia eventuale roba dopo ".m3u8" (lascia eventuale '/')
            if (rawUrl.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                rawUrl = NormalizeHlsUrl(rawUrl);
            }

            var normalized = NormalizeUrl(baseUri, rawUrl);
            if (normalized == null)
                return;

            if (list.Any(c => string.Equals(c.Url, normalized, StringComparison.OrdinalIgnoreCase)))
                return;

            bool? finalForce = forceHasVideo;

            // HLS → forziamo video
            if (finalForce == null &&
                normalized.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                finalForce = true;
            }

            // okcdn progressive → consideralo video
            if (finalForce == null && IsOkCdnProgressiveUrl(normalized))
            {
                finalForce = true;
            }

            list.Add(new Candidate(normalized, finalForce));
        }

        void AddNestedPage(string? rawUrl)
        {
            if (nestedPageUrls == null)
                return;

            if (string.IsNullOrWhiteSpace(rawUrl))
                return;

            rawUrl = rawUrl.Trim().Trim('\'', '"');
            rawUrl = DecodeJsonUrl(rawUrl.Trim());
            var normalized = NormalizeUrl(baseUri, rawUrl);
            if (normalized == null)
                return;

            if (nestedPageUrls.Any(u => string.Equals(u, normalized, StringComparison.OrdinalIgnoreCase)))
                return;

            nestedPageUrls.Add(normalized);
        }

        // <video src="..."> / <audio src="...">
        var videoTagRegex = new Regex(
            @"<(video|audio)[^>]*\s+src\s*=\s*[""'](?<url>[^""'#>]+)[""'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match m in videoTagRegex.Matches(text))
        {
            var url = m.Groups["url"].Value;
            var isAudio = m.Groups[1].Value.Equals("audio", StringComparison.OrdinalIgnoreCase);
            AddMedia(url, isAudio ? false : (bool?)null);
        }

        // <source src="...">
        var sourceRegex = new Regex(
            @"<source[^>]*\s+src\s*=\s*[""'](?<url>[^""'#>]+)[""'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match m in sourceRegex.Matches(text))
        {
            var url = m.Groups["url"].Value;
            bool? forceHasVideo = null;

            var tag = m.Value;
            if (tag.IndexOf("audio/", StringComparison.OrdinalIgnoreCase) >= 0)
                forceHasVideo = false;

            AddMedia(url, forceHasVideo);
        }

        // <iframe src="..."> → pagina annidata (ok.ru/videoembed/...)
        var iframeRegex = new Regex(
            @"<iframe[^>]*\s+src\s*=\s*[""'](?<url>[^""'#>]+)[""'][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match m in iframeRegex.Matches(text))
        {
            var url = m.Groups["url"].Value;

            if (url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0 ||
                LooksDirectMediaUrl(url))
            {
                AddMedia(url, GuessForceHasVideoFromExtension(url));
            }

            AddNestedPage(url);
        }

        // src="..." generico che sembra media
        var genericSrcRegex = new Regex(
            @"\bsrc\s*=\s*[""'](?<url>[^""'#>]+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match m in genericSrcRegex.Matches(text))
        {
            var url = m.Groups["url"].Value;
            if (string.IsNullOrWhiteSpace(url))
                continue;

            if (url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0 ||
                LooksDirectMediaUrl(url))
            {
                AddMedia(url, GuessForceHasVideoFromExtension(url));
            }
        }

        // <meta property="og:video" ...>
        var ogVideoRegex = new Regex(
            @"<meta[^>]+property\s*=\s*[""']og:video[^""']*[""'][^>]*content\s*=\s*[""'](?<url>[^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match m in ogVideoRegex.Matches(text))
        {
            var url = m.Groups["url"].Value;
            AddMedia(url, null);
        }

        // Qualsiasi .m3u8 nel testo
        var m3u8Regex = new Regex(
            @"[""'](?<url>[^""']+\.m3u8[^""']*)[""']",
            RegexOptions.IgnoreCase);

        foreach (Match m in m3u8Regex.Matches(text))
        {
            var url = m.Groups["url"].Value;
            AddMedia(url, null);
        }

        // URL diretti https://... con estensione media / okcdn
        var directRegex = new Regex(
            @"https?://[^\s""'<>]+",
            RegexOptions.IgnoreCase);

        foreach (Match m in directRegex.Matches(text))
        {
            var url = m.Value;
            if (LooksDirectMediaUrl(url))
            {
                AddMedia(url, GuessForceHasVideoFromExtension(url));
            }
        }

        return list;
    }

    private static string? NormalizeUrl(Uri? baseUri, string raw)
    {
        try
        {
            if (Uri.TryCreate(raw, UriKind.Absolute, out var abs))
                return abs.ToString();

            if (baseUri != null && Uri.TryCreate(baseUri, raw, out var rel))
                return rel.ToString();
        }
        catch
        {
        }
        return null;
    }

    private static string NormalizeHlsUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return url;

        var idx = url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            int end = idx + ".m3u8".Length;
            // se subito dopo c'è '/', teniamolo (alcuni CDN usano "file.m3u8/" come directory)
            if (url.Length > end && url[end] == '/')
                end++;
            return url.Substring(0, end);
        }

        return url;
    }

    // ===================== Riconoscimento URL ok.ru / okcdn =====================

    private static bool IsOkCdnProgressiveUrl(string url)
    {
        try
        {
            var u = new Uri(url);
            if (!u.Host.Contains("okcdn.ru", StringComparison.OrdinalIgnoreCase))
                return false;

            var ext = Path.GetExtension(u.AbsolutePath).ToLowerInvariant();
            if (ext == ".m3u8" || ext == ".mpd")
                return false;

            var q = u.Query ?? "";
            // i link "full" tipici: ?expires=...&id=...
            if (q.IndexOf("expires=", StringComparison.OrdinalIgnoreCase) >= 0 &&
                q.IndexOf("id=", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        catch
        {
        }
        return false;
    }

    // ===================== Content-Type / estensioni =====================

    private static bool IsHlsContentType(string ct)
    {
        if (string.IsNullOrEmpty(ct))
            return false;

        if (ct.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase))
            return true;
        if (ct.Equals("application/x-mpegURL", StringComparison.OrdinalIgnoreCase))
            return true;
        if (ct.Equals("application/x-mpegurl", StringComparison.OrdinalIgnoreCase))
            return true;
        if (ct.Equals("audio/mpegurl", StringComparison.OrdinalIgnoreCase))
            return true;
        if (ct.Equals("audio/x-mpegurl", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsMediaContentType(string ct)
    {
        if (string.IsNullOrEmpty(ct))
            return false;

        if (ct.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ct.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IsHlsContentType(ct))
            return true;

        return false;
    }

    private static bool IsHtmlLikeContentType(string ct)
    {
        if (string.IsNullOrEmpty(ct))
            return true; // alcuni server non mandano il Content-Type, assumiamo HTML

        if (ct.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ct.IndexOf("application/xhtml", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        return false;
    }

    private static bool? DetermineForceHasVideo(string contentType, string url)
    {
        if (!string.IsNullOrEmpty(contentType))
        {
            if (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                return false;

            if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                return null;

            // HLS: consideralo video
            if (IsHlsContentType(contentType))
                return true;
        }

        return GuessForceHasVideoFromExtension(url);
    }

    private static bool LooksDirectMediaUrl(string url) =>
        HasMediaLikePath(url) ||
        url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool HasMediaLikePath(string url)
    {
        try
        {
            // URL progressivi okcdn senza estensione
            if (IsOkCdnProgressiveUrl(url))
                return true;

            var u = new Uri(url);
            var ext = Path.GetExtension(u.AbsolutePath).ToLowerInvariant();

            string[] mediaExts =
            {
                ".mp4", ".m4v", ".webm", ".mp3", ".m4a", ".flac",
                ".wav", ".ogg", ".opus", ".ts", ".m2ts", ".mov",
                ".avi", ".wmv", ".mkv", ".mka"
            };

            return mediaExts.Contains(ext);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasVideoLikeExtension(string url)
    {
        try
        {
            var u = new Uri(url);
            var ext = Path.GetExtension(u.AbsolutePath).ToLowerInvariant();

            string[] videoExts =
            {
                ".mp4", ".m4v", ".webm", ".mov", ".mkv",
                ".avi", ".wmv", ".ts", ".m2ts"
            };

            return videoExts.Contains(ext);
        }
        catch
        {
            return false;
        }
    }

    private static bool? GuessForceHasVideoFromExtension(string url)
    {
        try
        {
            var u = new Uri(url);
            var ext = Path.GetExtension(u.AbsolutePath).ToLowerInvariant();
            var host = u.Host;

            // HLS → video
            if (ext == ".m3u8")
                return true;

            // okcdn progressive senza estensione → video
            if (host.Contains("okcdn.ru", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(ext) || ext == "/"))
            {
                return true;
            }

            string[] audioExts =
            {
                ".mp3", ".m4a", ".flac", ".wav", ".ogg", ".opus", ".mka"
            };

            if (audioExts.Contains(ext))
                return false; // sicuramente solo audio
        }
        catch
        {
        }

        return null; // lascia decidere al Probe
    }

    // ===================== Parsing master HLS (.m3u8) =====================

    private static string? TrySelectBestHlsVariant(string playlistText, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(playlistText))
            return null;

        var lines = playlistText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var variants = new List<(int Bandwidth, string Url)>();

        for (int i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                continue;

            var uriLine = lines[i + 1].Trim();
            if (string.IsNullOrEmpty(uriLine) || uriLine.StartsWith("#"))
                continue;

            int bandwidth = 0;
            var m = Regex.Match(line, @"BANDWIDTH=(\d+)", RegexOptions.IgnoreCase);
            if (m.Success)
                int.TryParse(m.Groups[1].Value, out bandwidth);

            var fullUrl = NormalizeUrl(baseUri, uriLine);
            if (fullUrl == null)
                continue;

            variants.Add((bandwidth, fullUrl));
        }

        if (variants.Count == 0)
            return null;

        // scegli la variante con BANDWIDTH maggiore (in caso di 0, l’OrderByDescending si arrangia)
        var best = variants
            .OrderByDescending(v => v.Bandwidth)
            .First();

        return best.Url;
    }

    // ===================== Trova yt-dlp =====================

    private static string? TryFindExe(string exeName)
    {
        try
        {
            var here = Path.Combine(AppContext.BaseDirectory, exeName);
            if (File.Exists(here)) return here;

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                try
                {
                    var cand = Path.Combine(dir, exeName);
                    if (File.Exists(cand)) return cand;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // ===================== JSON URL decoding =====================
    private static string DecodeJsonUrl(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;

        // 1) Decodifica entità HTML (&amp;, &quot;, ecc.)
        raw = WebUtility.HtmlDecode(raw);

        // 2) Prova a far decodificare le escape al parser JSON
        //    (gestisce \u0026, \/, ecc. se la stringa è ben formata)
        try
        {
            var json = "\"" + raw.Replace("\"", "\\\"") + "\"";
            raw = JsonSerializer.Deserialize<string>(json) ?? raw;
        }
        catch
        {
            // Se non è una stringa JSON valida, continuiamo con raw così com'è.
        }

        // 3) Fix manuali per sequenze rimaste/doppie
        raw = raw
            // \/ -> /
            .Replace("\\/", "/")

            // \u0026 / \\u0026 -> &
            .Replace("\\\\u0026", "&")
            .Replace("\\u0026", "&")

            // \u003d / \u003D / doppi -> =
            .Replace("\\\\u003d", "=")
            .Replace("\\\\u003D", "=")
            .Replace("\\u003d", "=")
            .Replace("\\u003D", "=")

            // \u003f / \u003F / doppi -> ?
            .Replace("\\\\u003f", "?")
            .Replace("\\\\u003F", "?")
            .Replace("\\u003f", "?")
            .Replace("\\u003F", "?")

            // eventuali \& rimasti da robe strane -> &
            .Replace("\\&", "&");

        // 4) Togli spazi ai bordi
        raw = raw.Trim();

        // 5) Ripulisci finali tipo ...url\' o ...url\"
        if (raw.EndsWith("\\'") || raw.EndsWith("\\\""))
        {
            raw = raw.Substring(0, raw.Length - 2).TrimEnd();
        }
        else if (raw.EndsWith("\\"))
        {
            raw = raw.Substring(0, raw.Length - 1).TrimEnd();
        }

        // 6) Togli eventuali apici/doppi apici di contorno 'url' / "url"
        raw = raw.Trim('\'', '"');

        return raw;
    }
}
