using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace MusicBeePlugin
{
    /// <summary>
    /// Fetches lyrics using a waterfall strategy:
    /// 1. Embedded tags in MusicBee
    /// 2. LRCLIB (free, no API key required)
    /// </summary>
    public class LyricsService
    {
        private readonly Func<string> _getEmbeddedLyrics;
        private static readonly HttpClient _http = new HttpClient();

        public LyricsService(Func<string> getEmbeddedLyrics)
        {
            _getEmbeddedLyrics = getEmbeddedLyrics;
            _http.DefaultRequestHeaders.Add("User-Agent", "MB_LyricScroll/1.0");
        }

        public async Task<string> GetLyricsAsync(string title, string artist, string album, int durationMs)
        {
            // Step 1: check if MusicBee already has embedded lyrics
            string embedded = _getEmbeddedLyrics();
            if (!string.IsNullOrWhiteSpace(embedded))
                return embedded.Trim();

            // Step 2: try LRCLIB
            string lrclib = await FetchFromLrclibAsync(title, artist, album, durationMs);
            if (!string.IsNullOrWhiteSpace(lrclib))
                return lrclib.Trim();

            return string.Empty;
        }

        private async Task<string> FetchFromLrclibAsync(string title, string artist, string album, int durationMs)
        {
            try
            {
                int durationSeconds = durationMs / 1000;

                string url = "https://lrclib.net/api/get" +
                             "?track_name="  + Uri.EscapeDataString(title)  +
                             "&artist_name=" + Uri.EscapeDataString(artist) +
                             "&album_name="  + Uri.EscapeDataString(album)  +
                             "&duration="    + durationSeconds;

                HttpResponseMessage response = await _http.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return string.Empty;

                string json = await response.Content.ReadAsStringAsync();
                string plain = ExtractJsonField(json, "plainLyrics");
                return plain;
            }
            catch
            {
                return string.Empty;
            }
        }

        private string ExtractJsonField(string json, string field)
        {
            string search = "\"" + field + "\":\"";
            int start = json.IndexOf(search);
            if (start < 0) return string.Empty;

            start += search.Length;
            int end = json.IndexOf("\"", start);
            if (end < 0) return string.Empty;

            return json.Substring(start, end - start)
                       .Replace("\\n", "\n")
                       .Replace("\\r", "")
                       .Replace("\\\"", "\"");
        }
    }
}