using System;
using System.Net.Http;
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
                "MB_LyricScroll/1.2 (https://github.com/JulioJulioso/MusicBee-lyricScroll)");
            return http;
        }

        public async Task<LyricsResult> GetLyricsAsync(
            string title,
            string artist,
            string album,
            int durationMs,
            bool preferSynced = true)
        {
            string localRaw = SafeGetLocal()?.Trim() ?? string.Empty;

            if (IsInstrumentalMarker(localRaw))
                return LyricsResult.Instrumental;

            bool hasLocal = !string.IsNullOrWhiteSpace(localRaw) && !IsNoLyricsStub(localRaw);
            LyricsResult local = hasLocal ? LrcParser.TryParseResult(localRaw) : LyricsResult.Empty;

            // Always ask LRCLIB: OST tags often contain wrong scraped lyrics while LRCLIB
            // correctly marks the track instrumental (e.g. Interstellar — S.T.A.Y.).
            LyricsResult lrclib = await FetchFromLrclibAsync(title, artist, album, durationMs)
                .ConfigureAwait(false);

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

                LyricsResult result = await LrclibGetAsync(cleanTitle, cleanArtist, cleanAlbum, durationSeconds)
                    .ConfigureAwait(false);
                if (!result.IsEmpty)
                    return result;

                result = await LrclibGetAsync(cleanTitle, cleanArtist, album: null, durationSeconds)
                    .ConfigureAwait(false);
                if (!result.IsEmpty)
                    return result;

                return await LrclibSearchAsync(cleanTitle, cleanArtist, durationSeconds).ConfigureAwait(false);
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

        private async Task<LyricsResult> LrclibSearchAsync(string title, string artist, int durationSeconds)
        {
            string url = "https://lrclib.net/api/search" +
                         "?track_name=" + Uri.EscapeDataString(title) +
                         "&artist_name=" + Uri.EscapeDataString(artist);

            HttpResponseMessage response = await _http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return LyricsResult.Empty;

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            JArray arr = JArray.Parse(json);
            if (arr.Count == 0)
                return LyricsResult.Empty;

            JObject best = null;
            int bestDelta = int.MaxValue;
            bool bestHasSync = false;

            foreach (JToken token in arr)
            {
                if (!(token is JObject obj))
                    continue;

                if (!ArtistMatches(artist, obj.Value<string>("artistName")))
                    continue;

                int dur = obj.Value<int?>("duration") ?? 0;
                int delta = durationSeconds > 0 && dur > 0
                    ? Math.Abs(dur - durationSeconds)
                    : 9999;

                bool hasSync = !string.IsNullOrWhiteSpace(obj.Value<string>("syncedLyrics"));
                bool hasText = !string.IsNullOrWhiteSpace(obj.Value<string>("plainLyrics"))
                               || hasSync
                               || obj.Value<bool?>("instrumental") == true;

                if (!hasText)
                    continue;

                // Prefer closer duration; on a tie, prefer a hit that has synced lyrics.
                bool better = best == null
                              || delta < bestDelta
                              || (delta == bestDelta && hasSync && !bestHasSync);

                if (better)
                {
                    best = obj;
                    bestDelta = delta;
                    bestHasSync = hasSync;
                }
            }

            return best == null ? LyricsResult.Empty : ParseLrclibJson(best.ToString());
        }

        /// <summary>
        /// Strip trailing (Rare) / [Live] style suffixes so online lookup can match.
        /// </summary>
        internal static string CleanForSearch(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string s = value.Trim();
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
                LyricsResult timed = LrcParser.TryParseResult(synced);
                if (timed.HasSync)
                    return timed;
            }

            string plain = obj.Value<string>("plainLyrics");
            if (!string.IsNullOrWhiteSpace(plain))
                return LyricsResult.FromPlain(plain);

            // Synced present but unparseable — strip tags for plain fallback.
            if (!string.IsNullOrWhiteSpace(synced))
                return LyricsResult.FromPlain(LrcParser.TryParseResult(synced).PlainText);

            return LyricsResult.Empty;
        }

#if DEBUG
        static LyricsService()
        {
            System.Diagnostics.Debug.Assert(CleanForSearch("Bob Dylan (Rare)") == "Bob Dylan");
            System.Diagnostics.Debug.Assert(CleanForSearch("I Want You") == "I Want You");
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
            System.Diagnostics.Debug.Assert(LooksLikeScoreOrOst("S.T.A.Y.", "Interstellar (OST)"));
            System.Diagnostics.Debug.Assert(!LooksLikeScoreOrOst("I Want You", "Blonde on Blonde"));
        }
#endif
    }
}
