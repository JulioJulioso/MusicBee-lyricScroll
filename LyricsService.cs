using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MusicBeePlugin
{
    /// <summary>
    /// Lyrics waterfall:
    /// 1. Local MusicBee sources (NowPlaying lyrics / downloaded / tag)
    /// 2. LRCLIB (free, no API key)
    /// </summary>
    public class LyricsService
    {
        private readonly Func<string> _getLocalLyrics;
        private static readonly HttpClient _http = CreateHttpClient();
        private static readonly Regex _lrcTags = new Regex(@"\[[0-9:.]+\]", RegexOptions.Compiled);

        public LyricsService(Func<string> getLocalLyrics)
        {
            _getLocalLyrics = getLocalLyrics ?? throw new ArgumentNullException(nameof(getLocalLyrics));
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient();
            // LRCLIB asks for a descriptive User-Agent.
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "MB_LyricScroll/1.0 (https://github.com/JulioJulioso/MusicBee-lyricScroll)");
            return http;
        }

        public async Task<string> GetLyricsAsync(string title, string artist, string album, int durationMs)
        {
            string local = SafeGetLocal();
            if (!string.IsNullOrWhiteSpace(local))
                return local.Trim();

            string lrclib = await FetchFromLrclibAsync(title, artist, album, durationMs).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(lrclib))
                return lrclib.Trim();

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
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
                    return string.Empty;

                int durationSeconds = Math.Max(durationMs / 1000, 0);

                string url = "https://lrclib.net/api/get" +
                             "?track_name=" + Uri.EscapeDataString(title ?? "") +
                             "&artist_name=" + Uri.EscapeDataString(artist ?? "") +
                             "&album_name=" + Uri.EscapeDataString(album ?? "") +
                             "&duration=" + durationSeconds;

                HttpResponseMessage response = await _http.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return string.Empty;

                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseLrclibJson(json);
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static string ParseLrclibJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return string.Empty;

            JObject obj = JObject.Parse(json);
            string plain = obj.Value<string>("plainLyrics");
            if (!string.IsNullOrWhiteSpace(plain))
                return plain;

            // Fallback: strip simple LRC timestamps from synced lyrics.
            string synced = obj.Value<string>("syncedLyrics");
            if (string.IsNullOrWhiteSpace(synced))
                return string.Empty;

            return _lrcTags.Replace(synced, "").Trim();
        }

#if DEBUG
        static LyricsService()
        {
            System.Diagnostics.Debug.Assert(
                ParseLrclibJson("{\"plainLyrics\":\"Hello\\nWorld\"}") == "Hello\nWorld");
            System.Diagnostics.Debug.Assert(
                ParseLrclibJson("{\"syncedLyrics\":\"[00:12.00]Hi there\"}").Contains("Hi there"));
        }
#endif
    }
}
