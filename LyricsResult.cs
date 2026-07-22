using System;
using System.Collections.Generic;

namespace MusicBeePlugin
{
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
        public static LyricsResult Empty { get; } = new LyricsResult(string.Empty, null, instrumental: false);
        public static LyricsResult Instrumental { get; } = new LyricsResult(LyricsService.InstrumentalMessage, null, instrumental: true);

        private LyricsResult(string plainText, IReadOnlyList<LrcLine> syncedLines, bool instrumental)
        {
            PlainText = plainText ?? string.Empty;
            SyncedLines = syncedLines ?? Array.Empty<LrcLine>();
            IsInstrumental = instrumental;
        }

        public string PlainText { get; }
        public IReadOnlyList<LrcLine> SyncedLines { get; }
        public bool IsInstrumental { get; }
        public bool IsEmpty => !IsInstrumental && string.IsNullOrWhiteSpace(PlainText) && SyncedLines.Count == 0;
        public bool HasSync => SyncedLines.Count >= 2;

        public static LyricsResult FromPlain(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Empty;
            if (LyricsService.IsInstrumentalMarker(text))
                return Instrumental;
            return new LyricsResult(text.Trim(), null, instrumental: false);
        }

        public static LyricsResult FromSynced(IReadOnlyList<LrcLine> lines, string plainFallback)
        {
            if (lines == null || lines.Count < 2)
                return FromPlain(plainFallback);

            string plain = plainFallback;
            if (string.IsNullOrWhiteSpace(plain))
            {
                var parts = new string[lines.Count];
                for (int i = 0; i < lines.Count; i++)
                    parts[i] = lines[i].Text;
                plain = string.Join("\n", parts);
            }

            return new LyricsResult(plain.Trim(), lines, instrumental: false);
        }
    }
}
