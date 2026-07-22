using System;
using System.Collections.Generic;

namespace MusicBeePlugin
{
    public enum LyricsSource
    {
        None = 0,
        Local = 1,
        Lrclib = 2,
        ScrapedFallback = 3
    }

    /// <summary>
    /// How to resolve lyrics for the current track (right-click overrides Auto).
    /// </summary>
    public enum LyricsFetchMode
    {
        Auto = 0,
        LocalOnly = 1,
        LrclibOnly = 2
    }

    public readonly struct LrcLine
    {
        public LrcLine(int timeMs, string text)
        {
            TimeMs = timeMs;
            Text = text ?? string.Empty;
        }

        public int TimeMs { get; }
        public string Text { get; }
    }

    /// <summary>
    /// Lyrics payload for the panel: instrumental, timed lines, or plain text.
    /// </summary>
    public sealed class LyricsResult
    {
        public static LyricsResult Empty { get; } =
            new LyricsResult(string.Empty, null, instrumental: false, LyricsSource.None);

        public static LyricsResult Instrumental { get; } =
            new LyricsResult(LyricsService.InstrumentalMessage, null, instrumental: true, LyricsSource.None);

        private LyricsResult(
            string plainText,
            IReadOnlyList<LrcLine> syncedLines,
            bool instrumental,
            LyricsSource source)
        {
            PlainText = plainText ?? string.Empty;
            SyncedLines = syncedLines ?? Array.Empty<LrcLine>();
            IsInstrumental = instrumental;
            Source = source;
        }

        public string PlainText { get; }
        public IReadOnlyList<LrcLine> SyncedLines { get; }
        public bool IsInstrumental { get; }
        public LyricsSource Source { get; }
        public bool IsEmpty => !IsInstrumental && string.IsNullOrWhiteSpace(PlainText) && SyncedLines.Count == 0;
        public bool HasSync => SyncedLines.Count >= 2;

        public LyricsResult WithSource(LyricsSource source) =>
            new LyricsResult(PlainText, SyncedLines.Count > 0 ? SyncedLines : null, IsInstrumental, source);

        public string SourceLabel
        {
            get
            {
                if (IsInstrumental)
                    return "Instrumental";
                if (IsEmpty)
                    return "None";
                switch (Source)
                {
                    case LyricsSource.Lrclib:
                        return HasSync ? "LRCLIB (synced)" : "LRCLIB";
                    case LyricsSource.Local:
                        return HasSync ? "MusicBee (synced)" : "MusicBee";
                    case LyricsSource.ScrapedFallback:
                        return "Local (trimmed scrape)";
                    default:
                        return "Unknown";
                }
            }
        }

        public static LyricsResult FromPlain(string text, LyricsSource source = LyricsSource.Local)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Empty;
            if (LyricsService.IsInstrumentalMarker(text))
                return Instrumental;
            return new LyricsResult(text.Trim(), null, instrumental: false, source);
        }

        public static LyricsResult FromSynced(
            IReadOnlyList<LrcLine> lines,
            string plainFallback,
            LyricsSource source = LyricsSource.Local)
        {
            if (lines == null || lines.Count < 2)
                return FromPlain(plainFallback, source);

            string plain = plainFallback;
            if (string.IsNullOrWhiteSpace(plain))
            {
                var parts = new string[lines.Count];
                for (int i = 0; i < lines.Count; i++)
                    parts[i] = lines[i].Text;
                plain = string.Join("\n", parts);
            }

            return new LyricsResult(plain.Trim(), lines, instrumental: false, source);
        }
    }
}
