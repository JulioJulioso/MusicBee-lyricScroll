using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace MusicBeePlugin
{
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
        public int PaddingPx { get; set; } = 14;

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
                        settings = loaded;
                }
                else
                {
                    // Migrate older single-file delay setting.
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

            if (settings.StartDelayMs < 0)
                settings.StartDelayMs = 0;
            if (settings.PaddingPx < 0)
                settings.PaddingPx = 0;
            if (settings.PaddingPx > 48)
                settings.PaddingPx = 48;
            if (settings.FontSizePt < 8f)
                settings.FontSizePt = 8f;
            if (settings.FontSizePt > 48f)
                settings.FontSizePt = 48f;

            return settings;
        }

        public void Save(string directory)
        {
            if (string.IsNullOrEmpty(directory))
                return;

            try
            {
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
