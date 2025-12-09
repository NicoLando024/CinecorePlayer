using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace CinecorePlayer2025
{
    /// <summary>
    /// Servizio centralizzato per:
    ///  - ricavare un titolo "decente" e un anno dal path del file
    ///  - parlare con TMDb per recuperare poster
    ///  - mantenere una cache persistente (posterIndex.json) per velocizzare tutto
    /// </summary>
    internal static class MovieMetadataService
    {
        // La tua API key TMDb.
        private const string TmdbApiKey = "daf98548f41dd2a9aa6eca965798a463";
        public static event Action? PostersChanged;
        private static readonly HttpClient _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        private static readonly PosterIndexStore _posterIndex = new();

        // --------------------------------------------------------------------
        // API pubblica
        // --------------------------------------------------------------------

        /// <summary>
        /// Versione "vecchia": non passa la durata.
        /// </summary>
        public static (string? normalizedTitle, int? year, string? localPosterPath) ResolveTitleAndPoster(
            string filePath,
            CancellationToken ct)
            => ResolveTitleAndPoster(filePath, null, ct);

        /// <summary>
        /// Versione estesa:
        ///  - filePath: path del file
        ///  - durationSeconds: durata stimata in secondi (puoi leggerla da durationIndex.json)
        /// </summary>
        public static (string? normalizedTitle, int? year, string? localPosterPath) ResolveTitleAndPoster(
            string filePath,
            double? durationSeconds,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return (null, null, null);

            // 1) cache (posterIndex.json) — usiamo solo anno + poster, NON il vecchio titolo
            var cached = _posterIndex.TryGet(filePath);
            int? cachedYear = cached?.year;
            string? cachedPoster = cached?.localPosterPath;

            // 2) normalizza sempre il nome del file
            var (titleFromPath, yearFromPath) = ExtractMovieTitleAndYearFromPath(filePath);

            string? title = titleFromPath;
            int? year = cachedYear ?? yearFromPath;

            // aggiorna comunque l'index con quello che sappiamo
            if (!string.IsNullOrWhiteSpace(title) || year.HasValue || !string.IsNullOrWhiteSpace(cachedPoster))
            {
                _posterIndex.Update(filePath, title, year, cachedPoster);
            }

            // 3) se abbiamo già un poster locale valido, basta così
            if (!string.IsNullOrWhiteSpace(cachedPoster) && File.Exists(cachedPoster))
            {
                return (title ?? titleFromPath, year ?? yearFromPath, cachedPoster);
            }

            // 4) ci assicuriamo di avere almeno il titolo dal path
            if (string.IsNullOrWhiteSpace(title))
            {
                title = titleFromPath;
                year = yearFromPath;
            }

            if (string.IsNullOrWhiteSpace(title))
                return (titleFromPath, yearFromPath, null);

            // 5) best-effort: prova a scaricare un poster da TMDb
            string? posterPath = TryDownloadPoster(title, year, durationSeconds, ct, out var tmdbTitle, out var tmdbYear);
            if (!string.IsNullOrWhiteSpace(posterPath) && File.Exists(posterPath))
            {
                // se TMDb ci dà un titolo/anno, usiamo quelli (sono “ufficiali”)
                if (!string.IsNullOrWhiteSpace(tmdbTitle))
                    title = tmdbTitle;
                if (tmdbYear.HasValue)
                    year = tmdbYear;

                _posterIndex.Update(filePath, title, year, posterPath);
                return (title, year, posterPath);
            }

            // niente poster trovato, ma il titolo normalizzato lo abbiamo comunque
            return (title, year, null);
        }

        // --------------------------------------------------------------------
        // Normalizzazione nome file → (titolo, anno)
        // --------------------------------------------------------------------

        /// <summary>
        /// Normalizza "soft" il nome del film a partire dal path:
        /// - sostituisce . _ + con spazi
        /// - rimuove blocchi tra [] () {}
        /// - prova a estrarre un anno (1999, 2014, 2022...)
        /// - tronca non appena incontra roba da release (1080p, HDR, x265, Ita, Eng, 3D, HSBS ecc.)
        /// </summary>
        public static (string normalizedTitle, int? year) ExtractMovieTitleAndYearFromPath(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                return (name, (int?)null);

            // --------------------------------------------------------------------
            // 1) Trova tutti i possibili anni nel filename, ma:
            //    - solo tra 1900 e (anno corrente + 1)
            //    - se ce ne sono più di uno, prova a scegliere quello "giusto"
            // --------------------------------------------------------------------
            int? year = null;
            int currentYear = DateTime.Now.Year;

            var yearMatches = Regex.Matches(name, @"\b(19[0-9]{2}|20[0-9]{2})\b");
            var yearCandidates = new List<int>();

            foreach (Match m in yearMatches)
            {
                if (int.TryParse(m.Value, out var yy))
                {
                    // es.: 2049 viene scartato perché troppo nel futuro
                    if (yy >= 1900 && yy <= currentYear + 1)
                        yearCandidates.Add(yy);
                }
            }

            if (yearCandidates.Count == 1)
            {
                year = yearCandidates[0];
            }
            else if (yearCandidates.Count > 1)
            {
                // Heuristica:
                // - se la distanza tra min e max è grande (>=10 anni),
                //   prendi il più vecchio (tipico caso: 2001 vs 1968, 1999 vs 2019)
                // - se sono vicini, prendi l'ultimo (es. 2019 vs 2020)
                int min = int.MaxValue;
                int max = int.MinValue;

                foreach (var yy in yearCandidates)
                {
                    if (yy < min) min = yy;
                    if (yy > max) max = yy;
                }

                if (Math.Abs(max - min) >= 10)
                {
                    year = min;
                }
                else
                {
                    year = yearCandidates[yearCandidates.Count - 1];
                }
            }

            // --------------------------------------------------------------------
            // 2) Pulizia "soft" del nome
            // --------------------------------------------------------------------
            string s = name;

            // normalizza trattini "tipografici"
            s = s.Replace('–', '-').Replace('—', '-');

            // rimuovi [stuff] (stuff) {stuff}
            s = Regex.Replace(s, @"\[[^\]]*\]", " ");
            s = Regex.Replace(s, @"\([^\)]*\)", " ");
            s = Regex.Replace(s, @"\{[^\}]*\}", " ");

            // . _ + → spazi (il trattino NON lo tocchiamo, niente più taglio su " - ")
            s = s.Replace('.', ' ')
                 .Replace('_', ' ')
                 .Replace('+', ' ');

            // pulizia spazi
            s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '.', '_');

            if (s.Length == 0)
                return (name, year);

            var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var titleTokens = new List<string>();

            // parole chiaramente "tecniche"
            var noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "1080p", "720p", "2160p", "480p",
                "4k", "uhd", "uhdr", "hdr",
                "remux", "bdremux", "bdrmux",
                "bluray", "bdrip", "brrip", "webrip", "webdl", "web-dl",
                "hdtv", "dvdrip", "hdrip", "microhd",
                "x264", "x265", "h264", "h265", "hevc", "xvid",
                "ac3", "dts", "dtsx", "truehd", "atmos",
                "multi", "ita", "eng", "dual",
                "sub", "subs", "subita", "subeng", "sub-eng", "sub-ita",
                "uncut", "extended", "proper", "repack", "internal",
                "hsbs", "sbs", "fullsbs", "full-sbs", "3d"
            };

            for (int i = 0; i < tokens.Length; i++)
            {
                string raw = tokens[i];
                string t = raw.Trim(' ', '-', '.', '_');
                if (t.Length == 0)
                    continue;

                string lower = t.ToLowerInvariant();

                // --- anno scelto: lo usiamo SOLO se corrisponde a quello deciso sopra ---
                // (altri numeri a 4 cifre, tipo "2049" in "Blade Runner 2049", restano nel titolo)
                if (lower.Length == 4 &&
                    year.HasValue &&
                    int.TryParse(lower, out var yyTok) &&
                    yyTok == year.Value)
                {
                    // non aggiungo l'anno al titolo e tronco qui
                    break;
                }

                // qualsiasi "900p" generico → stop (720p, 1080p, 2160p ecc.)
                if (lower.EndsWith("p", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(lower.AsSpan(0, lower.Length - 1), out _))
                {
                    break;
                }

                // pattern tecnici infilati nello stesso token, tipo "x264-GROUP", "1080p.BluRay", "FULLSBS"
                if (Regex.IsMatch(lower,
                        @"(720p|1080p|2160p|480p|" +
                        @"bluray|b[dr]rip|brrip|webrip|web[-]?dl|hdtv|dvdrip|hdrip|microhd|" +
                        @"x264|x265|h264|h265|hevc|xvid|" +
                        @"ac3|dts|truehd|atmos|" +
                        @"sbs|hsbs|fullsbs|3d|)" +
                        @"upscaled|Upscaled",
                        RegexOptions.IgnoreCase))
                {
                    break;
                }

                // parole chiaramente di "release"
                if (noise.Contains(lower))
                    break;

                titleTokens.Add(t);
            }

            // fallback: se per qualche motivo non abbiamo preso nulla, usa tutta la stringa ripulita
            if (titleTokens.Count == 0)
                titleTokens.AddRange(tokens);

            string title = string.Join(" ", titleTokens);
            title = Regex.Replace(title, @"\s+", " ").Trim();

            // estetica: TitleCase
            try
            {
                var ti = CultureInfo.CurrentCulture.TextInfo;
                title = ti.ToTitleCase(title.ToLower());
            }
            catch
            {
                // ignora, tieni il titolo com'è
            }

            return (title, year);
        }

        // --------------------------------------------------------------------
        //                    IMPLEMENTAZIONE TMDb + CACHE
        // --------------------------------------------------------------------

        /// <summary>
        /// Usa TMDb con vari fallback (it/en, con/senza anno) e,
        /// se durationSeconds è valorizzata, confronta anche il runtime TMDb
        /// entro una tolleranza per evitare match sbagliati.
        /// </summary>
        private static string? TryDownloadPoster(
            string searchTitle,
            int? searchYear,
            double? durationSeconds,
            CancellationToken ct,
            out string? tmdbTitle,
            out int? tmdbYear)
        {
            tmdbTitle = null;
            tmdbYear = null;

            try
            {
                if (string.IsNullOrWhiteSpace(searchTitle))
                    return null;

                // se non hai impostato l'API key non facciamo nulla
                if (string.IsNullOrWhiteSpace(TmdbApiKey) ||
                    TmdbApiKey.StartsWith("INSERISCI_", StringComparison.OrdinalIgnoreCase))
                    return null;

                string? localPosterPath;

                // originalYear = anno ricavato dal file
                var originalYear = searchYear;
                var expectedDurationSeconds = durationSeconds;

                // 1) it-IT con anno
                if (searchYear.HasValue)
                {
                    string? tmpTitle = null;
                    int? tmpYear = null;

                    if (TryOneTmdbCall(searchTitle, searchYear, expectedDurationSeconds, "it-IT", ct,
                            ref tmpTitle, ref tmpYear, out localPosterPath))
                    {
                        tmdbTitle = tmpTitle;
                        tmdbYear = tmpYear;
                        return localPosterPath;
                    }
                }

                // 2) it-IT senza anno
                {
                    string? tmpTitle = null;
                    int? tmpYear = null;

                    if (TryOneTmdbCall(searchTitle, null, expectedDurationSeconds, "it-IT", ct,
                            ref tmpTitle, ref tmpYear, out localPosterPath))
                    {
                        tmdbTitle = tmpTitle;
                        tmdbYear = tmpYear;
                        return localPosterPath;
                    }
                }

                // 3) en-US con anno
                if (searchYear.HasValue)
                {
                    string? tmpTitle = null;
                    int? tmpYear = null;

                    if (TryOneTmdbCall(searchTitle, searchYear, expectedDurationSeconds, "en-US", ct,
                            ref tmpTitle, ref tmpYear, out localPosterPath))
                    {
                        tmdbTitle = tmpTitle;
                        tmdbYear = tmpYear;
                        return localPosterPath;
                    }
                }

                // 4) en-US senza anno
                {
                    string? tmpTitle = null;
                    int? tmpYear = null;

                    if (TryOneTmdbCall(searchTitle, null, expectedDurationSeconds, "en-US", ct,
                            ref tmpTitle, ref tmpYear, out localPosterPath))
                    {
                        tmdbTitle = tmpTitle;
                        tmdbYear = tmpYear;
                        return localPosterPath;
                    }
                }

                return null;
            }
            catch
            {
                tmdbTitle = null;
                tmdbYear = null;
                return null;
            }
        }

        /// <summary>
        /// Singola chiamata a search/movie TMDb in una lingua specifica
        /// (eventuale filtro per anno). Scorriamo alcuni risultati e
        /// accettiamo il primo che passa i controlli di anno/durata.
        /// </summary>
        private static bool TryOneTmdbCall(
            string searchTitle,
            int? searchYear,
            double? expectedDurationSeconds,
            string language,
            CancellationToken ct,
            ref string? tmdbTitle,
            ref int? tmdbYear,
            out string? localPosterPath)
        {
            localPosterPath = null;

            string query = Uri.EscapeDataString(searchTitle);

            string url = searchYear.HasValue
                ? $"https://api.themoviedb.org/3/search/movie?api_key={TmdbApiKey}&language={language}&query={query}&year={searchYear.Value}"
                : $"https://api.themoviedb.org/3/search/movie?api_key={TmdbApiKey}&language={language}&query={query}";

            using var resp = _http.GetAsync(url, ct).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
                return false;

            string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
                return false;

            int count = 0;

            foreach (var result in results.EnumerateArray())
            {
                if (count++ >= 8) // non analizziamo più di 8 risultati
                    break;

                // titolo candidato
                string? candidateTitle = null;
                if (result.TryGetProperty("title", out var titleProp))
                    candidateTitle = titleProp.GetString();
                if (string.IsNullOrWhiteSpace(candidateTitle) &&
                    result.TryGetProperty("original_title", out var origProp))
                    candidateTitle = origProp.GetString();

                // anno candidato da release_date
                int? candidateYear = null;
                if (result.TryGetProperty("release_date", out var rdProp))
                {
                    var rd = rdProp.GetString();
                    if (!string.IsNullOrWhiteSpace(rd) && rd!.Length >= 4 &&
                        int.TryParse(rd.Substring(0, 4), out var y))
                    {
                        candidateYear = y;
                    }
                }

                // runtime candidato (se abbiamo la durata locale e un id)
                int? candidateRuntimeMinutes = null;
                if (expectedDurationSeconds.HasValue &&
                    result.TryGetProperty("id", out var idProp) &&
                    idProp.ValueKind == JsonValueKind.Number)
                {
                    int movieId = idProp.GetInt32();
                    candidateRuntimeMinutes = GetMovieRuntimeMinutes(movieId, language, ct);
                }

                if (!IsAcceptableMatch(
                        searchTitle,
                        searchYear,
                        expectedDurationSeconds,
                        candidateTitle,
                        candidateYear,
                        candidateRuntimeMinutes))
                {
                    continue; // passa al prossimo risultato
                }

                // qui il match è "buono": scarichiamo il poster
                if (!result.TryGetProperty("poster_path", out var posterProp))
                    return false;

                var posterPathTmdb = posterProp.GetString();
                if (string.IsNullOrWhiteSpace(posterPathTmdb))
                    return false;

                string imageUrl = "https://image.tmdb.org/t/p/w500" + posterPathTmdb;

                var bytes = _http.GetByteArrayAsync(imageUrl, ct).GetAwaiter().GetResult();

                string hashSrc = (candidateTitle ?? searchTitle) + "|" +
                                 (candidateYear?.ToString() ?? "") + "|" + posterPathTmdb;
                string fileName = ComputeSha1(hashSrc) + ".jpg";

                string folder = GetPosterFolder();
                string fullPath = Path.Combine(folder, fileName);

                File.WriteAllBytes(fullPath, bytes);

                tmdbTitle = candidateTitle ?? searchTitle;
                tmdbYear = candidateYear;
                localPosterPath = fullPath;
                return true;
            }

            // nessun risultato "accettabile"
            return false;
        }

        /// <summary>
        /// Controlla se il risultato TMDb è "credibile" rispetto a:
        ///  - anno ricavato dal filename
        ///  - durata locale (expectedDurationSeconds) vs runtime TMDb
        /// Soprattutto per titoli molto corti (Flow, Her, Up...) siamo severi.
        /// </summary>
        private static bool IsAcceptableMatch(
            string searchTitle,
            int? originalYear,
            double? expectedDurationSeconds,
            string? candidateTitle,
            int? candidateYear,
            int? candidateRuntimeMinutes)
        {
            if (string.IsNullOrWhiteSpace(candidateTitle))
                return false;

            // per capire se è un titolo "corto" (Flow, Her, Up...)
            var tokenCount = searchTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            bool shortTitle = tokenCount <= 2 && searchTitle.Length <= 8;

            // 1) confronto sulla durata (se disponibile)
            if (expectedDurationSeconds.HasValue && candidateRuntimeMinutes.HasValue)
            {
                double expectedMinutes = expectedDurationSeconds.Value / 60.0;
                double diffMinutes = Math.Abs(candidateRuntimeMinutes.Value - expectedMinutes);

                double maxDiff = shortTitle ? 3.0 : 7.0; // tolleranza più stretta per titoli corti

                if (diffMinutes > maxDiff)
                    return false;
            }

            // 2) confronto sull'anno (se disponibile)
            if (originalYear.HasValue && candidateYear.HasValue)
            {
                int diffYear = Math.Abs(candidateYear.Value - originalYear.Value);

                int maxDiffYear = shortTitle ? 2 : 5;

                if (diffYear > maxDiffYear)
                    return false;
            }

            // se non abbiamo né runtime né anno, rischiamo,
            // ma almeno rientriamo nel caso "meglio di niente".
            return true;
        }

        /// <summary>
        /// Recupera il runtime (in minuti) da TMDb per un dato movieId.
        /// </summary>
        private static int? GetMovieRuntimeMinutes(int movieId, string language, CancellationToken ct)
        {
            try
            {
                string url = $"https://api.themoviedb.org/3/movie/{movieId}?api_key={TmdbApiKey}&language={language}";

                using var resp = _http.GetAsync(url, ct).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                    return null;

                string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("runtime", out var rtProp) &&
                    rtProp.ValueKind == JsonValueKind.Number)
                {
                    return rtProp.GetInt32();
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string GetPosterFolder()
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CinecorePlayer2025",
                "posters");

            Directory.CreateDirectory(folder);
            return folder;
        }

        private static string ComputeSha1(string input)
        {
            using var sha = SHA1.Create();
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = sha.ComputeHash(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // --------------------------------------------------------------------
        //                       POSTER INDEX (JSON)
        // --------------------------------------------------------------------

        private sealed class PosterIndexStore
        {
            private sealed class PosterEntry
            {
                public string? NormalizedTitle { get; set; }
                public int? Year { get; set; }
                public string? LocalPosterPath { get; set; }
            }

            private sealed class Model
            {
                public Dictionary<string, PosterEntry> Items { get; set; } =
                    new(StringComparer.OrdinalIgnoreCase);
            }

            private readonly string _file;
            private readonly object _lock = new();
            private Model _data;

            public PosterIndexStore()
            {
                var folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CinecorePlayer2025");
                Directory.CreateDirectory(folder);

                _file = Path.Combine(folder, "posterIndex.json");
                _data = Load();
            }

            /// <summary>
            /// Ritorna (titolo normalizzato, anno, path poster) se esiste.
            /// </summary>
            public (string? title, int? year, string? localPosterPath)? TryGet(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return null;

                lock (_lock)
                {
                    if (_data.Items.TryGetValue(path, out var e))
                        return (e.NormalizedTitle, e.Year, e.LocalPosterPath);
                }

                return null;
            }

            /// <summary>
            /// Aggiorna o crea l'entry relativa a quel path.
            /// </summary>
            public void Update(string path, string? title, int? year, string? localPosterPath)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                bool changed = false; // <--- aggiungi

                lock (_lock)
                {
                    if (!_data.Items.TryGetValue(path, out var e))
                    {
                        e = new PosterEntry();
                        _data.Items[path] = e;
                        changed = true; // <--- nuova entry
                    }

                    if (!string.IsNullOrWhiteSpace(title) &&
                        !string.Equals(e.NormalizedTitle, title, StringComparison.Ordinal))
                    {
                        e.NormalizedTitle = title;
                        changed = true;
                    }

                    if (year.HasValue && e.Year != year)
                    {
                        e.Year = year;
                        changed = true;
                    }

                    if (!string.IsNullOrWhiteSpace(localPosterPath) &&
                        !string.Equals(e.LocalPosterPath, localPosterPath, StringComparison.OrdinalIgnoreCase))
                    {
                        e.LocalPosterPath = localPosterPath;
                        changed = true;
                    }

                    if (changed)
                    {
                        SaveNoLock();
                    }
                }

                // <--- QUI, fuori dal lock
                if (changed)
                {
                    MovieMetadataService.PostersChanged?.Invoke();
                }
            }

            private Model Load()
            {
                try
                {
                    if (File.Exists(_file))
                    {
                        var json = File.ReadAllText(_file, Encoding.UTF8);
                        var m = JsonSerializer.Deserialize<Model>(json);
                        if (m != null) return m;
                    }
                }
                catch
                {
                    // se fallisce, partiamo da pulito
                }

                return new Model();
            }

            private void SaveNoLock()
            {
                try
                {
                    var json = JsonSerializer.Serialize(
                        _data,
                        new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_file, json, Encoding.UTF8);
                }
                catch
                {
                    // best-effort
                }
            }
        }
    }
}
