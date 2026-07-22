using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace MusicBeePlugin
{
    public enum TextEffectKind
    {
        None = 0,
        Shadow = 1,
        Outline = 2
    }

    /// <summary>
    /// Persisted LyricScroll options (delay + appearance).
    /// </summary>
    public sealed class PluginSettings
    {
        public int StartDelayMs { get; set; }

        /// <summary>
        /// When true, prefer LRCLIB (or local) synced LRC over plain MusicBee lyrics.
        /// </summary>
        public bool PreferSyncedLines { get; set; } = true;

        public string BackColorHex { get; set; } = "#121212";
        public string TextColorHex { get; set; } = "#E8E6E3";
        public string FontFamily { get; set; } = "Segoe UI";
        public float FontSizePt { get; set; } = 12f;
        public bool FontBold { get; set; }

        /// <summary>Legacy single padding; migrated into Left/Top on load.</summary>
        public int PaddingPx { get; set; } = 14;

        public int PaddingLeftPx { get; set; } = 14;
        public int PaddingTopPx { get; set; } = 14;

        /// <summary>Extra pixels between lyric lines (synced and plain newline splits).</summary>
        public int LineSpacingPx { get; set; } = 6;

        public TextEffectKind TextEffect { get; set; } = TextEffectKind.None;

        /// <summary>
        /// WinForms ColorDialog custom palette (COLORREF ints). Persisted across opens.
        /// </summary>
        public int[] CustomColors { get; set; }

        public Color BackColor => ParseHex(BackColorHex, Color.FromArgb(0x12, 0x12, 0x12));
        public Color TextColor => ParseHex(TextColorHex, Color.FromArgb(0xE8, 0xE6, 0xE3));

        public static string ToHex(Color c) =>
            "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

        public static Color ParseHex(string hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return fallback;

            string s = hex.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal))
                s = s.Substring(1);

            if (s.Length == 6
                && int.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int r)
                && int.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g)
                && int.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b))
            {
                return Color.FromArgb(r, g, b);
            }

            return fallback;
        }

        public Font CreateFont()
        {
            float size = FontSizePt;
            if (size < 8f) size = 8f;
            if (size > 48f) size = 48f;

            string family = string.IsNullOrWhiteSpace(FontFamily) ? "Segoe UI" : FontFamily.Trim();
            FontStyle style = FontBold ? FontStyle.Bold : FontStyle.Regular;

            try
            {
                return new Font(family, size, style, GraphicsUnit.Point);
            }
            catch
            {
                return new Font("Segoe UI", size, style, GraphicsUnit.Point);
            }
        }

        public static PluginSettings Load(string directory)
        {
            var settings = new PluginSettings();
            if (string.IsNullOrEmpty(directory))
                return settings;

            try
            {
                string jsonPath = Path.Combine(directory, "LyricScroll.settings.json");
                if (File.Exists(jsonPath))
                {
                    string json = File.ReadAllText(jsonPath);
                    var loaded = JsonConvert.DeserializeObject<PluginSettings>(json);
                    if (loaded != null)
                    {
                        settings = loaded;
                        // Old files only had PaddingPx — copy into Left/Top once.
                        if (json.IndexOf("PaddingLeftPx", StringComparison.Ordinal) < 0
                            || json.IndexOf("PaddingTopPx", StringComparison.Ordinal) < 0)
                        {
                            int p = ClampPad(settings.PaddingPx);
                            if (json.IndexOf("PaddingLeftPx", StringComparison.Ordinal) < 0)
                                settings.PaddingLeftPx = p;
                            if (json.IndexOf("PaddingTopPx", StringComparison.Ordinal) < 0)
                                settings.PaddingTopPx = p;
                        }
                    }
                }
                else
                {
                    string legacy = Path.Combine(directory, "LyricScroll_startDelayMs.txt");
                    if (File.Exists(legacy)
                        && int.TryParse(File.ReadAllText(legacy).Trim(), out int ms)
                        && ms >= 0)
                    {
                        settings.StartDelayMs = ms;
                    }
                }
            }
            catch
            {
                // keep defaults
            }

            settings.Normalize();
            return settings;
        }

        public void Normalize()
        {
            if (StartDelayMs < 0)
                StartDelayMs = 0;

            PaddingLeftPx = ClampPad(PaddingLeftPx);
            PaddingTopPx = ClampPad(PaddingTopPx);
            PaddingPx = PaddingLeftPx; // keep legacy field in sync for older readers

            if (LineSpacingPx < 0)
                LineSpacingPx = 0;
            if (LineSpacingPx > 32)
                LineSpacingPx = 32;

            if (FontSizePt < 8f)
                FontSizePt = 8f;
            if (FontSizePt > 48f)
                FontSizePt = 48f;

            if (TextEffect < TextEffectKind.None || TextEffect > TextEffectKind.Outline)
                TextEffect = TextEffectKind.None;

            CustomColors = NormalizeCustomColors(CustomColors);
        }

        /// <summary>ColorDialog expects up to 16 COLORREF values.</summary>
        public static int[] NormalizeCustomColors(int[] colors)
        {
            var result = new int[16];
            for (int i = 0; i < 16; i++)
                result[i] = unchecked((int)0x00FFFFFF); // white empty slots

            if (colors == null)
                return result;

            int n = Math.Min(16, colors.Length);
            for (int i = 0; i < n; i++)
                result[i] = colors[i];
            return result;
        }

        private static int ClampPad(int value)
        {
            if (value < 0) return 0;
            if (value > 64) return 64;
            return value;
        }

        public void Save(string directory)
        {
            if (string.IsNullOrEmpty(directory))
                return;

            try
            {
                Normalize();
                string jsonPath = Path.Combine(directory, "LyricScroll.settings.json");
                File.WriteAllText(jsonPath, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch
            {
                // ignore
            }
        }
    }
}
