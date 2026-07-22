using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MusicBeePlugin
{
    /// <summary>
    /// Lyrics waterfall (preferSynced=true, default):
    /// 1. LRCLIB instrumental overrides bad local tags (OST)
    /// 2. LRCLIB synced LRC (best timing)
    /// 3. Local LRC if parseable
    /// 4. Local plain / MusicBee lyrics
    /// 5. LRCLIB plain
    /// 6. OST heuristic → Instrumental
    /// </summary>
    public class LyricsService
    {
        public const string InstrumentalMessage = "Instrumental";

        private readonly Func<string> _getLocalLyrics;
        private static readonly HttpClient _http = CreateHttpClient();
        // "Bob Dylan (Rare)", "Title [Live]", "Song (Remastered 2015)"
        private static readonly Regex _trailingBracket = new Regex(
            @"\s*[\(\[][^\)\]]*[\)\]]\s*$",
            RegexOptions.Compiled);
        // "02. Title", "2) Title", "02 - Title" (requires space after separator)
        private static readonly Regex _leadingTrackNum = new Regex(
            @"^\d{1,3}\s*[\.\)\-]\s+",
            RegexOptions.Compiled);

        public LyricsService(Func<string> getLocalLyrics)
        {
            _getLocalLyrics = getLocalLyrics ?? throw new ArgumentNullException(nameof(getLocalLyrics));
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient();
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "MB_LyricScroll/1.2 (https://github.com/JulioJulioso/MusicBee-lyricScroll)");
            return http;
        }

        public async Task<LyricsResult> GetLyricsAsync(
            string title,
            string artist,
            string album,
            int durationMs,
            bool preferSynced = true,
            LyricsFetchMode mode = LyricsFetchMode.Auto)
        {
            string localRaw = SafeGetLocal()?.Trim() ?? string.Empty;
            string scrapeFallback = string.Empty;

            if (IsInstrumentalMarker(localRaw))
            {
                if (mode == LyricsFetchMode.LrclibOnly)
                {
                    // still allow LRCLIB to override a wrong local instrumental tag
                }
                else
                    return LyricsResult.Instrumental;
            }

            // MusicBee sometimes stores a full Letras.com page scrape in the lyrics tag.
            // Never prefer that over LRCLIB — keep a trimmed prefix only as last resort.
            if (LooksLikeScrapedWebPage(localRaw))
            {
                if (TryExtractLyricsBeforeWebChrome(localRaw, out string cut))
                    scrapeFallback = cut;
                localRaw = string.Empty;
            }

            bool hasLocal = !string.IsNullOrWhiteSpace(localRaw) && !IsNoLyricsStub(localRaw)
                            && !IsInstrumentalMarker(localRaw);
            LyricsResult local = hasLocal
                ? LrcParser.TryParseResult(localRaw, LyricsSource.Local)
                : LyricsResult.Empty;

            if (mode == LyricsFetchMode.LocalOnly)
            {
                if (!local.IsEmpty)
                    return local;
                if (!string.IsNullOrEmpty(scrapeFallback))
                    return LyricsResult.FromPlain(scrapeFallback, LyricsSource.ScrapedFallback);
                return LyricsResult.Empty;
            }

            LyricsResult lrclib = await FetchFromLrclibAsync(title, artist, album, durationMs)
                .ConfigureAwait(false);

            if (mode == LyricsFetchMode.LrclibOnly)
            {
                if (lrclib.IsInstrumental)
                    return LyricsResult.Instrumental;
                return lrclib.IsEmpty ? LyricsResult.Empty : lrclib;
            }

            // Auto waterfall
            if (lrclib.IsInstrumental)
                return LyricsResult.Instrumental;

            if (preferSynced)
            {
                if (lrclib.HasSync)
                    return lrclib;
                if (local.HasSync)
                    return local;
            }

            if (hasLocal && !local.IsEmpty)
                return local;

            if (!lrclib.IsEmpty)
                return lrclib;

            if (!string.IsNullOrEmpty(scrapeFallback))
                return LyricsResult.FromPlain(scrapeFallback, LyricsSource.ScrapedFallback);

            if (LooksLikeScoreOrOst(title, album))
                return LyricsResult.Instrumental;

            return LyricsResult.Empty;
        }

        private string SafeGetLocal()
        {
            try
            {
                return _getLocalLyrics() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private async Task<LyricsResult> FetchFromLrclibAsync(string title, string artist, string album, int durationMs)
        {
            try
            {
                string cleanTitle = CleanForSearch(title);
                string cleanArtist = CleanForSearch(artist);
                string cleanAlbum = CleanForSearch(album);

                if (string.IsNullOrWhiteSpace(cleanTitle) || string.IsNullOrWhiteSpace(cleanArtist))
                    return LyricsResult.Empty;

                int durationSeconds = Math.Max(durationMs / 1000, 0);

                // /api/get needs duration within ±2s — vinyl/rips often miss that.
                LyricsResult result = await LrclibGetAsync(cleanTitle, cleanArtist, cleanAlbum, durationSeconds)
                    .ConfigureAwait(false);
                if (!result.IsEmpty)
                    return result;

                result = await LrclibGetAsync(cleanTitle, cleanArtist, album: null, durationSeconds)
                    .ConfigureAwait(false);
                if (!result.IsEmpty)
                    return result;

                result = await LrclibSearchAsync(cleanTitle, cleanArtist, durationSeconds).ConfigureAwait(false);
                if (!result.IsEmpty)
                    return result;

                // Same query shape as the website: /search?q=Artist+Title
                return await LrclibSearchQueryAsync(cleanArtist + " " + cleanTitle, cleanTitle, cleanArtist, durationSeconds)
                    .ConfigureAwait(false);
            }
            catch
            {
                return LyricsResult.Empty;
            }
        }

        private async Task<LyricsResult> LrclibGetAsync(string title, string artist, string album, int durationSeconds)
        {
            string url = "https://lrclib.net/api/get" +
                         "?track_name=" + Uri.EscapeDataString(title) +
                         "&artist_name=" + Uri.EscapeDataString(artist) +
                         "&duration=" + durationSeconds;

            if (!string.IsNullOrWhiteSpace(album))
                url += "&album_name=" + Uri.EscapeDataString(album);

            HttpResponseMessage response = await _http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return LyricsResult.Empty;

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseLrclibJson(json);
        }

        private Task<LyricsResult> LrclibSearchAsync(string title, string artist, int durationSeconds)
        {
            string url = "https://lrclib.net/api/search" +
                         "?track_name=" + Uri.EscapeDataString(title) +
                         "&artist_name=" + Uri.EscapeDataString(artist);
            return LrclibSearchUrlAsync(url, title, artist, durationSeconds);
        }

        private Task<LyricsResult> LrclibSearchQueryAsync(
            string query,
            string title,
            string artist,
            int durationSeconds)
        {
            string url = "https://lrclib.net/api/search?q=" + Uri.EscapeDataString(query);
            return LrclibSearchUrlAsync(url, title, artist, durationSeconds);
        }

        private async Task<LyricsResult> LrclibSearchUrlAsync(
            string url,
            string title,
            string artist,
            int durationSeconds)
        {
            HttpResponseMessage response = await _http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return LyricsResult.Empty;

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            JArray arr = JArray.Parse(json);
            if (arr.Count == 0)
                return LyricsResult.Empty;

            JObject best = null;
            int bestDelta = int.MaxValue;
            int bestTier = 99;
            bool bestHasSync = false;

            foreach (JToken token in arr)
            {
                if (!(token is JObject obj))
                    continue;

                if (!ArtistMatches(artist, obj.Value<string>("artistName") ?? string.Empty))
                    continue;
                if (!TitleMatches(title, obj.Value<string>("trackName") ?? string.Empty))
                    continue;

                int dur = ReadDurationSeconds(obj);
                int delta = durationSeconds > 0 && dur > 0
                    ? Math.Abs(dur - durationSeconds)
                    : 5000;

                // Tier 0: near ±2 (±8). Tier 1: vinyl/remaster (±90). Tier 2: any title+artist hit.
                int tier = delta <= 8 ? 0 : delta <= 90 ? 1 : 2;

                bool hasSync = !string.IsNullOrWhiteSpace(obj.Value<string>("syncedLyrics"));
                bool hasText = !string.IsNullOrWhiteSpace(obj.Value<string>("plainLyrics"))
                               || hasSync
                               || obj.Value<bool?>("instrumental") == true;

                if (!hasText)
                    continue;

                bool better = best == null
                              || tier < bestTier
                              || (tier == bestTier && delta < bestDelta)
                              || (tier == bestTier && delta == bestDelta && hasSync && !bestHasSync);

                if (better)
                {
                    best = obj;
                    bestDelta = delta;
                    bestTier = tier;
                    bestHasSync = hasSync;
                }
            }

            return best == null ? LyricsResult.Empty : ParseLrclibJson(best.ToString());
        }

        internal static int ReadDurationSeconds(JObject obj)
        {
            if (obj == null)
                return 0;

            JToken token = obj["duration"];
            if (token == null || token.Type == JTokenType.Null)
                return 0;

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                return (int)Math.Round(token.Value<double>(), MidpointRounding.AwayFromZero);

            if (double.TryParse(
                    token.ToString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double seconds))
                return (int)Math.Round(seconds, MidpointRounding.AwayFromZero);

            return 0;
        }

        /// <summary>
        /// Strip track-number prefixes and trailing (Rare)/[Live]/Producido por…] suffixes
        /// so online lookup can match LRCLIB titles.
        /// </summary>
        internal static string CleanForSearch(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string s = value.Trim();

            // "02. Escribo rap…" → "Escribo rap…"
            s = _leadingTrackNum.Replace(s, "").Trim();

            // Peel stacked suffixes: "Title [Producido por X] (Live)"
            for (int i = 0; i < 3; i++)
            {
                string next = _trailingBracket.Replace(s, "").Trim();
                if (next == s)
                    break;
                s = next;
            }

            // Track num again if it was after a peeled prefix (rare).
            s = _leadingTrackNum.Replace(s, "").Trim();
            return s;
        }

        internal static bool ArtistMatches(string expected, string fromApi)
        {
            string e = NormalizeMatchToken(expected);
            string a = NormalizeMatchToken(fromApi);
            if (string.IsNullOrEmpty(e) || string.IsNullOrEmpty(a))
                return false;
            return a.Contains(e) || e.Contains(a);
        }

        internal static bool TitleMatches(string expected, string fromApi)
        {
            string e = NormalizeMatchToken(expected);
            string a = NormalizeMatchToken(fromApi);
            if (string.IsNullOrEmpty(e) || string.IsNullOrEmpty(a))
                return false;
            return a.Contains(e) || e.Contains(a);
        }

        /// <summary>
        /// Lowercase, drop accents/punctuation so "Seru Giran" ≈ "Serú Girán".
        /// </summary>
        internal static string NormalizeMatchToken(string value)
        {
            string s = RemoveDiacritics(CleanForSearch(value ?? "")).ToLowerInvariant();
            if (s.Length == 0)
                return string.Empty;

            var chars = new char[s.Length];
            int n = 0;
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
                    chars[n++] = c;
            }
            return Regex.Replace(new string(chars, 0, n), @"\s+", " ").Trim();
        }

        /// <summary>
        /// Fold accents: ú→u, ñ stays (letter), etc. FormD + strip NonSpacingMark.
        /// </summary>
        internal static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            string normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        internal static bool LooksLikeScoreOrOst(string title, string album)
        {
            string blob = ((album ?? "") + " " + (title ?? "")).ToLowerInvariant();
            if (blob.Contains("soundtrack") || blob.Contains("motion picture")
                || Regex.IsMatch(blob, @"\bost\b") || blob.Contains("score"))
                return true;

            string t = (title ?? "").Trim();
            return Regex.IsMatch(t, @"^[A-Za-z]([.\u2024\u00B7][A-Za-z]){1,}[.\u2024\u00B7]?$");
        }

        internal static bool IsInstrumentalMarker(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string t = text.Trim();
            if (t.Equals("instrumental", StringComparison.OrdinalIgnoreCase))
                return true;
            if (t.Equals("[instrumental]", StringComparison.OrdinalIgnoreCase))
                return true;
            if (t.Equals("(instrumental)", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        internal static bool IsNoLyricsStub(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return true;

            string t = text.Trim();
            if (t.Length > 40)
                return false;

            string lower = t.ToLowerInvariant();
            return lower == "no lyrics"
                   || lower == "no lyrics found"
                   || lower == "no lyrics found."
                   || lower == "not available"
                   || lower == "n/a"
                   || lower == "none"
                   || lower == "instrumental";
        }

        // Letras.com / letras.mus.br page chrome that MusicBee scrapers sometimes embed.
        private static readonly string[] _webChromePhrases =
        {
            "agregar a favoritos",
            "agregar a playlist",
            "desplazamiento automático",
            "desplazamiento automatico",
            "tamaño de la fuente",
            "anotaciones habilitadas",
            "letras academy",
            "hecho con amor desde brasil",
            "millones de canciones",
            "protección de datos",
            "proteccion de datos",
            "más buscadas",
            "mas buscadas",
            "window.promise",
            "copiar vínculo",
            "copiar vinculo",
            "¿los datos estan equivocados",
            "los datos están equivocados",
        };

        /// <summary>
        /// True when local "lyrics" look like a scraped lyrics-website page, not a song.
        /// </summary>
        internal static bool LooksLikeScrapedWebPage(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length < 120)
                return false;

            string lower = text.ToLowerInvariant();

            int hits = 0;
            for (int i = 0; i < _webChromePhrases.Length; i++)
            {
                if (lower.Contains(_webChromePhrases[i]))
                {
                    hits++;
                    if (hits >= 2)
                        return true;
                }
            }

            // Strong single markers unique to Letras scrapes.
            if (lower.Contains("hecho con amor desde brasil")
                || lower.Contains("letras academy")
                || lower.Contains("window.promise")
                || lower.Contains("millones de canciones"))
                return true;

            // Giant wall of text with almost no line breaks + UI words.
            int newlines = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                    newlines++;
            }

            if (text.Length > 1200 && newlines < 10
                && (lower.Contains("favoritos") || lower.Contains("playlist") || lower.Contains("copyright")))
                return true;

            return false;
        }

        /// <summary>
        /// If real lyric text appears before site chrome, keep that prefix.
        /// </summary>
        internal static bool TryExtractLyricsBeforeWebChrome(string text, out string cleaned)
        {
            cleaned = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string lower = text.ToLowerInvariant();
            int cut = -1;
            for (int i = 0; i < _webChromePhrases.Length; i++)
            {
                int idx = lower.IndexOf(_webChromePhrases[i], StringComparison.Ordinal);
                if (idx >= 0 && (cut < 0 || idx < cut))
                    cut = idx;
            }

            if (cut < 40)
                return false;

            cleaned = text.Substring(0, cut).Trim();
            if (cleaned.Length < 20 || LooksLikeScrapedWebPage(cleaned))
            {
                cleaned = string.Empty;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Prefer syncedLyrics when present; fall back to plainLyrics.
        /// </summary>
        internal static LyricsResult ParseLrclibJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return LyricsResult.Empty;

            JObject obj = JObject.Parse(json);

            if (obj.Value<bool?>("instrumental") == true)
                return LyricsResult.Instrumental;

            string synced = obj.Value<string>("syncedLyrics");
            if (!string.IsNullOrWhiteSpace(synced))
            {
                LyricsResult timed = LrcParser.TryParseResult(synced, LyricsSource.Lrclib);
                if (timed.HasSync)
                    return timed;
            }

            string plain = obj.Value<string>("plainLyrics");
            if (!string.IsNullOrWhiteSpace(plain))
                return LyricsResult.FromPlain(plain, LyricsSource.Lrclib);

            if (!string.IsNullOrWhiteSpace(synced))
                return LyricsResult.FromPlain(
                    LrcParser.TryParseResult(synced, LyricsSource.Lrclib).PlainText,
                    LyricsSource.Lrclib);

            return LyricsResult.Empty;
        }

#if DEBUG
        static LyricsService()
        {
            System.Diagnostics.Debug.Assert(CleanForSearch("Bob Dylan (Rare)") == "Bob Dylan");
            System.Diagnostics.Debug.Assert(CleanForSearch("I Want You") == "I Want You");
            System.Diagnostics.Debug.Assert(
                CleanForSearch("02. Escribo rap con R de revolución [Producido por Portavoz]")
                == "Escribo rap con R de revolución");
            System.Diagnostics.Debug.Assert(
                CleanForSearch("12 - Some Song (Remastered 2015)") == "Some Song");
            System.Diagnostics.Debug.Assert(
                ParseLrclibJson("{\"instrumental\":true,\"plainLyrics\":null}").IsInstrumental);
            System.Diagnostics.Debug.Assert(
                ParseLrclibJson("{\"plainLyrics\":\"Hello\\nWorld\"}").PlainText == "Hello\nWorld");
            System.Diagnostics.Debug.Assert(
                ParseLrclibJson(
                    "{\"syncedLyrics\":\"[00:01.00]Hello\\n[00:05.00]World\",\"plainLyrics\":\"Hello\\nWorld\"}")
                    .HasSync);
            System.Diagnostics.Debug.Assert(IsInstrumentalMarker("Instrumental"));
            System.Diagnostics.Debug.Assert(IsNoLyricsStub("No lyrics found."));
            System.Diagnostics.Debug.Assert(ArtistMatches("Hans Zimmer", "Hans Zimmer"));
            System.Diagnostics.Debug.Assert(ArtistMatches("Seru Giran", "Serú Girán"));
            System.Diagnostics.Debug.Assert(TitleMatches("Llorando En El Espejo", "Llorando en el espejo"));
            System.Diagnostics.Debug.Assert(TitleMatches("Pastor de Elefantes", "Pastor de Elefantes"));
            System.Diagnostics.Debug.Assert(!TitleMatches("Pastor de Elefantes", "Elefantes Rosados"));
            System.Diagnostics.Debug.Assert(CleanForSearch("Peperina (LP)") == "Peperina");
            System.Diagnostics.Debug.Assert(LooksLikeScoreOrOst("S.T.A.Y.", "Interstellar (OST)"));
            System.Diagnostics.Debug.Assert(!LooksLikeScoreOrOst("I Want You", "Blonde on Blonde"));

            const string scrape =
                "Y esperar que una nube me lleve hasta el mar Agregar a favoritos Agregar a playlist "
                + "Desplazamiento automático Letras Academy Hecho con amor desde Brasil if (!window.Promise ||";
            System.Diagnostics.Debug.Assert(LooksLikeScrapedWebPage(scrape));
            System.Diagnostics.Debug.Assert(TryExtractLyricsBeforeWebChrome(scrape, out string cut));
            System.Diagnostics.Debug.Assert(cut.Contains("nube me lleve"));
            System.Diagnostics.Debug.Assert(!LooksLikeScrapedWebPage("Verse one\nVerse two\nChorus"));
        }
#endif
    }
}
