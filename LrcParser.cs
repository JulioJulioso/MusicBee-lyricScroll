using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MusicBeePlugin
{
    /// <summary>
    /// Parses standard LRC / enhanced-LRC line timestamps.
    /// ponytail: line-level only — word-level karaoke needs Musixmatch rich sync later.
    /// </summary>
    public static class LrcParser
    {
        private static readonly Regex _timeTag = new Regex(
            @"\[(\d{1,3}):(\d{1,2})(?:\.(\d{1,3}))?\]",
            RegexOptions.Compiled);

        private static readonly Regex _offsetTag = new Regex(
            @"\[offset:\s*([+-]?\d+)\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool LooksLikeLrc(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return _timeTag.IsMatch(text);
        }

        public static IReadOnlyList<LrcLine> Parse(string lrc)
        {
            if (string.IsNullOrWhiteSpace(lrc))
                return Array.Empty<LrcLine>();

            int offsetMs = 0;
            Match offsetMatch = _offsetTag.Match(lrc);
            if (offsetMatch.Success
                && int.TryParse(offsetMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int off))
            {
                offsetMs = off;
            }

            var lines = new List<LrcLine>();
            string[] rawLines = lrc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            foreach (string raw in rawLines)
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;

                // Skip metadata tags without lyric text: [ar:], [ti:], [al:], [by:], [offset:]
                if (Regex.IsMatch(line, @"^\[[a-zA-Z]+:.*\]$"))
                    continue;

                MatchCollection tags = _timeTag.Matches(line);
                if (tags.Count == 0)
                    continue;

                string text = _timeTag.Replace(line, "").Trim();
                // Keep blank timed lines (instrumental gaps) as empty strings so timing stays correct.

                foreach (Match tag in tags)
                {
                    int timeMs = ParseTimeMs(tag) + offsetMs;
                    if (timeMs < 0)
                        timeMs = 0;
                    lines.Add(new LrcLine(timeMs, text));
                }
            }

            lines.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
            return lines;
        }

        public static LyricsResult TryParseResult(string text, LyricsSource source = LyricsSource.Local)
        {
            if (!LooksLikeLrc(text))
                return LyricsResult.FromPlain(text, source);

            IReadOnlyList<LrcLine> lines = Parse(text);
            if (lines.Count < 2)
                return LyricsResult.FromPlain(_timeTag.Replace(text ?? "", "").Trim(), source);

            return LyricsResult.FromSynced(lines, plainFallback: null, source);
        }

        /// <summary>
        /// Index of the last line whose timestamp is &lt;= positionMs, or -1 before the first line.
        /// </summary>
        public static int ActiveIndex(IReadOnlyList<LrcLine> lines, int positionMs)
        {
            if (lines == null || lines.Count == 0)
                return -1;

            int lo = 0;
            int hi = lines.Count - 1;
            int ans = -1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) / 2);
                if (lines[mid].TimeMs <= positionMs)
                {
                    ans = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return ans;
        }

        private static int ParseTimeMs(Match tag)
        {
            int minutes = int.Parse(tag.Groups[1].Value, CultureInfo.InvariantCulture);
            int seconds = int.Parse(tag.Groups[2].Value, CultureInfo.InvariantCulture);
            int frac = 0;
            if (tag.Groups[3].Success)
            {
                string f = tag.Groups[3].Value;
                // .5 → 500ms, .50 → 500ms, .500 → 500ms, .05 → 50ms
                if (f.Length == 1) frac = int.Parse(f, CultureInfo.InvariantCulture) * 100;
                else if (f.Length == 2) frac = int.Parse(f, CultureInfo.InvariantCulture) * 10;
                else frac = int.Parse(f.Substring(0, Math.Min(3, f.Length)), CultureInfo.InvariantCulture);
            }
            return (minutes * 60 + seconds) * 1000 + frac;
        }

#if DEBUG
        static LrcParser()
        {
            var lines = Parse("[00:01.00]Hello\n[00:05.50]World");
            Debug.Assert(lines.Count == 2);
            Debug.Assert(lines[0].TimeMs == 1000);
            Debug.Assert(lines[0].Text == "Hello");
            Debug.Assert(lines[1].TimeMs == 5500);
            Debug.Assert(ActiveIndex(lines, 0) == -1);
            Debug.Assert(ActiveIndex(lines, 1000) == 0);
            Debug.Assert(ActiveIndex(lines, 5500) == 1);
            Debug.Assert(ActiveIndex(lines, 9000) == 1);

            var multi = Parse("[00:01.00][00:10.00]Twice");
            Debug.Assert(multi.Count == 2);
            Debug.Assert(multi[0].Text == "Twice" && multi[1].Text == "Twice");
        }
#endif
    }
}
