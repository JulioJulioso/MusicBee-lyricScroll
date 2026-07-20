using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MusicBeePlugin
{
    /// <summary>
    /// Lyrics waterfall:
    /// 1. Local MusicBee sources (skip empty / "no lyrics" stubs; honor instrumental markers)
    /// 2. LRCLIB with cleaned artist/title (e.g. "Bob Dylan (Rare)" → "Bob Dylan")
    /// </summary>
    public class LyricsService
    {
        public const string InstrumentalMessage = "Instrumental";

        private readonly Func<string> _getLocalLyrics;
        private static readonly HttpClient _http = CreateHttpClient();
        private static readonly Regex _lrcTags = new Regex(@"\[[0-9:.]+\]", RegexOptions.Compiled);
        // "Bob Dylan (Rare)", "Title [Live]", "Song (Remastered 2015)"
        private static readonly Regex _trailingBracket = new Regex(
            @"\s*[\(\[][^\)\]]*[\)\]]\s*$",
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
                "MB_LyricScroll/1.0 (https://github.com/JulioJulioso/MusicBee-lyricScroll)");
            return http;
        }

        public async Task<string> GetLyricsAsync(string title, string artist, string album, int durationMs)
        {
            string local = SafeGetLocal()?.Trim() ?? string.Empty;

            if (IsInstrumentalMarker(local))
                return InstrumentalMessage;

            bool hasLocal = !string.IsNullOrWhiteSpace(local) && !IsNoLyricsStub(local);

            // Always ask LRCLIB: OST tags often contain wrong scraped lyrics while LRCLIB
            // correctly marks the track instrumental (e.g. Interstellar — S.T.A.Y.).
            string lrclib = await FetchFromLrclibAsync(title, artist, album, durationMs).ConfigureAwait(false);

            if (string.Equals(lrclib, InstrumentalMessage, StringComparison.Ordinal))
                return InstrumentalMessage;

            if (hasLocal)
                return local;

            if (!string.IsNullOrWhiteSpace(lrclib))
                return lrclib.Trim();

            // Soundtrack / score cues with no online lyrics → don't keep guessing.
            if (LooksLikeScoreOrOst(title, album))
                return InstrumentalMessage;

            return string.Empty;
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

        private async Task<string> FetchFromLrclibAsync(string title, string artist, string album, int durationMs)
        {
            try
            {
                string cleanTitle = CleanForSearch(title);
                string cleanArtist = CleanForSearch(artist);
                string cleanAlbum = CleanForSearch(album);

                if (string.IsNullOrWhiteSpace(cleanTitle) || string.IsNullOrWhiteSpace(cleanArtist))
                    return string.Empty;

                int durationSeconds = Math.Max(durationMs / 1000, 0);

                // 1) Exact get with album
                string result = await LrclibGetAsync(cleanTitle, cleanArtist, cleanAlbum, durationSeconds)
                    .ConfigureAwait(false);
                if (!string.IsNullOrEmpty(result))
                    return result;

                // 2) Get without album (live/bootleg album names often break the match)
                result = await LrclibGetAsync(cleanTitle, cleanArtist, album: null, durationSeconds)
                    .ConfigureAwait(false);
                if (!string.IsNullOrEmpty(result))
                    return result;

                // 3) Search fallback (more tolerant than /get)
                return await LrclibSearchAsync(cleanTitle, cleanArtist, durationSeconds).ConfigureAwait(false);
            }
            catch
            {
                return string.Empty;
            }
        }

        private async Task<string> LrclibGetAsync(string title, string artist, string album, int durationSeconds)
        {
            string url = "https://lrclib.net/api/get" +
                         "?track_name=" + Uri.EscapeDataString(title) +
                         "&artist_name=" + Uri.EscapeDataString(artist) +
                         "&duration=" + durationSeconds;

            if (!string.IsNullOrWhiteSpace(album))
                url += "&album_name=" + Uri.EscapeDataString(album);

            HttpResponseMessage response = await _http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return string.Empty;

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseLrclibJson(json);
        }

        private async Task<string> LrclibSearchAsync(string title, string artist, int durationSeconds)
        {
            string url = "https://lrclib.net/api/search" +
                         "?track_name=" + Uri.EscapeDataString(title) +
                         "&artist_name=" + Uri.EscapeDataString(artist);

            HttpResponseMessage response = await _http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return string.Empty;

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            JArray arr = JArray.Parse(json);
            if (arr.Count == 0)
                return string.Empty;

            // Prefer closest duration, then first hit with usable lyrics.
            JObject best = null;
            int bestDelta = int.MaxValue;

            foreach (JToken token in arr)
            {
                if (!(token is JObject obj))
                    continue;

                // Reject wrong-song hits (search is loose on short titles like "S.T.A.Y.").
                if (!ArtistMatches(artist, obj.Value<string>("artistName")))
                    continue;

                int dur = obj.Value<int?>("duration") ?? 0;
                int delta = durationSeconds > 0 && dur > 0
                    ? Math.Abs(dur - durationSeconds)
                    : 9999;

                bool hasText = !string.IsNullOrWhiteSpace(obj.Value<string>("plainLyrics"))
                               || !string.IsNullOrWhiteSpace(obj.Value<string>("syncedLyrics"))
                               || obj.Value<bool?>("instrumental") == true;

                if (!hasText)
                    continue;

                if (best == null || delta < bestDelta)
                {
                    best = obj;
                    bestDelta = delta;
                }
            }

            return best == null ? string.Empty : ParseLrclibJson(best.ToString());
        }

        /// <summary>
        /// Strip trailing (Rare) / [Live] style suffixes so online lookup can match.
        /// </summary>
        internal static string CleanForSearch(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string s = value.Trim();
            // Peel a few stacked suffixes: "Artist (Live) (Bootleg)"
            for (int i = 0; i < 3; i++)
            {
                string next = _trailingBracket.Replace(s, "").Trim();
                if (next == s)
                    break;
                s = next;
            }
            return s;
        }

        internal static bool ArtistMatches(string expected, string fromApi)
        {
            string e = CleanForSearch(expected).ToLowerInvariant();
            string a = CleanForSearch(fromApi ?? "").ToLowerInvariant();
            if (string.IsNullOrEmpty(e) || string.IsNullOrEmpty(a))
                return false;
            return a.Contains(e) || e.Contains(a);
        }

        internal static bool LooksLikeScoreOrOst(string title, string album)
        {
            string blob = ((album ?? "") + " " + (title ?? "")).ToLowerInvariant();
            if (blob.Contains("soundtrack") || blob.Contains("motion picture")
                || Regex.IsMatch(blob, @"\bost\b") || blob.Contains("score"))
                return true;

            // Titles like S.T.A.Y. / D.M.T. on scores are almost never sung lyrics.
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
                   || lower == "instrumental"; // also treated as stub if we want LRCLIB — but instrumental marker runs first
        }

        internal static string ParseLrclibJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return string.Empty;

            JObject obj = JObject.Parse(json);

            if (obj.Value<bool?>("instrumental") == true)
                return InstrumentalMessage;

            string plain = obj.Value<string>("plainLyrics");
            if (!string.IsNullOrWhiteSpace(plain))
                return plain;

            string synced = obj.Value<string>("syncedLyrics");
            if (string.IsNullOrWhiteSpace(synced))
                return string.Empty;

            return _lrcTags.Replace(synced, "").Trim();
        }

#if DEBUG
        static LyricsService()
        {
            System.Diagnostics.Debug.Assert(CleanForSearch("Bob Dylan (Rare)") == "Bob Dylan");
            System.Diagnostics.Debug.Assert(CleanForSearch("I Want You") == "I Want You");
            System.Diagnostics.Debug.Assert(
                ParseLrclibJson("{\"instrumental\":true,\"plainLyrics\":null}") == InstrumentalMessage);
            System.Diagnostics.Debug.Assert(
                ParseLrclibJson("{\"plainLyrics\":\"Hello\\nWorld\"}") == "Hello\nWorld");
            System.Diagnostics.Debug.Assert(IsInstrumentalMarker("Instrumental"));
            System.Diagnostics.Debug.Assert(IsNoLyricsStub("No lyrics found."));
            System.Diagnostics.Debug.Assert(ArtistMatches("Hans Zimmer", "Hans Zimmer"));
            System.Diagnostics.Debug.Assert(LooksLikeScoreOrOst("S.T.A.Y.", "Interstellar (OST)"));
            System.Diagnostics.Debug.Assert(!LooksLikeScoreOrOst("I Want You", "Blonde on Blonde"));
        }
#endif
    }
}
