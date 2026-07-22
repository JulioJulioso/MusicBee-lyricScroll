using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    /// <summary>
    /// Draws lyrics with TextRenderer.
    /// Synced LRC: highlight the active line and keep it near the vertical center.
    /// Plain text: gentle autoscroll by song position (+ optional start delay).
    /// </summary>
    public class LyricsPanel : UserControl
    {
        private const int MaxLayoutHeight = 16000;
        private const int SyncedLineGap = 6;

        private string _lyrics = string.Empty;
        private IReadOnlyList<LrcLine> _syncedLines = Array.Empty<LrcLine>();
        private int[] _lineTops = Array.Empty<int>();
        private int[] _lineHeights = Array.Empty<int>();
        private bool _syncedMode;
        private int _activeIndex = -1;

        private float _scrollY = 0f;
        private bool _hasLyrics = false;
        private int _durationMs = 0;
        private int _startDelayMs = 0;
        private float _totalTextHeight = 0f;

        private Font _font;
        private Color _textColor = Color.FromArgb(0xE8, 0xE6, 0xE3);
        private Color _dimTextColor = Color.FromArgb(0x7A, 0x78, 0x75);
        private int _padding = 14;
        private readonly Func<int> _getPositionMs;
        private readonly TextFormatFlags _textFlags =
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding |
            TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix;

        private Timer _scrollTimer;
        private bool _measurePending;

        public LyricsPanel(Func<int> getPositionMs)
        {
            _getPositionMs = getPositionMs ?? throw new ArgumentNullException(nameof(getPositionMs));
            _font = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Point);

            BackColor = Color.FromArgb(0x12, 0x12, 0x12);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            _scrollTimer = new Timer();
            _scrollTimer.Interval = 50;
            _scrollTimer.Tick += OnScrollTick;
        }

        public void ApplyAppearance(PluginSettings settings)
        {
            if (settings == null)
                return;

            if (InvokeRequired)
            {
                Invoke(new Action(() => ApplyAppearance(settings)));
                return;
            }

            BackColor = settings.BackColor;
            _textColor = settings.TextColor;
            _dimTextColor = BlendToward(settings.TextColor, settings.BackColor, 0.55f);
            _padding = Math.Max(0, Math.Min(48, settings.PaddingPx));
            _startDelayMs = Math.Max(0, settings.StartDelayMs);

            Font old = _font;
            _font = settings.CreateFont();
            if (old != null && !ReferenceEquals(old, _font))
                old.Dispose();

            MeasureLayout();
            SyncFromPosition();
            Invalidate();
        }

        public void SetStartDelayMs(int startDelayMs)
        {
            _startDelayMs = Math.Max(0, startDelayMs);
            SyncFromPosition();
        }

        public void SetLyrics(LyricsResult result, int durationMs)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => SetLyrics(result, durationMs)));
                return;
            }

            _scrollY = 0f;
            _durationMs = durationMs;
            _totalTextHeight = 0f;
            _activeIndex = -1;
            _syncedLines = Array.Empty<LrcLine>();
            _lineTops = Array.Empty<int>();
            _lineHeights = Array.Empty<int>();
            _syncedMode = false;

            if (result == null || result.IsEmpty)
            {
                _lyrics = "No lyrics found.";
                _hasLyrics = false;
                _scrollTimer.Stop();
                Invalidate();
                return;
            }

            if (result.IsInstrumental)
            {
                _lyrics = LyricsService.InstrumentalMessage;
                _hasLyrics = false;
                _scrollTimer.Stop();
                Invalidate();
                return;
            }

            if (result.HasSync)
            {
                _syncedMode = true;
                _syncedLines = result.SyncedLines;
                _lyrics = result.PlainText;
                _hasLyrics = true;
                MeasureLayout();
                SyncFromPosition();
                Invalidate();
                _scrollTimer.Start();
                return;
            }

            _lyrics = NormalizeLyrics(result.PlainText);
            _hasLyrics = true;
            MeasureLayout();
            SyncFromPosition();
            Invalidate();
            _scrollTimer.Start();
        }

        public void SetPlayState(bool isPlaying)
        {
            if (_hasLyrics)
                _scrollTimer.Start();
            SyncFromPosition();
        }

        private static string NormalizeLyrics(string lyrics)
        {
            return lyrics.Replace("\0", "")
                         .Replace("\r\n", "\n")
                         .Replace('\r', '\n');
        }

        private static Color BlendToward(Color from, Color toward, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            int r = (int)(from.R + (toward.R - from.R) * amount);
            int g = (int)(from.G + (toward.G - from.G) * amount);
            int b = (int)(from.B + (toward.B - from.B) * amount);
            return Color.FromArgb(r, g, b);
        }

        private void MeasureLayout()
        {
            if (_syncedMode)
                MeasureSyncedLayout();
            else
                MeasurePlainHeight();
        }

        private void MeasurePlainHeight()
        {
            if (!IsHandleCreated || Width <= 0 || string.IsNullOrEmpty(_lyrics) || _font == null)
                return;

            int textWidth = Math.Max(Width - (_padding * 2), 1);

            try
            {
                Size size = TextRenderer.MeasureText(
                    _lyrics,
                    _font,
                    new Size(textWidth, MaxLayoutHeight),
                    _textFlags);
                _totalTextHeight = Math.Min(size.Height, MaxLayoutHeight);
            }
            catch (Exception)
            {
                _totalTextHeight = Height;
            }
        }

        private void MeasureSyncedLayout()
        {
            if (!IsHandleCreated || Width <= 0 || _font == null || _syncedLines.Count == 0)
                return;

            int textWidth = Math.Max(Width - (_padding * 2), 1);
            int count = _syncedLines.Count;
            _lineTops = new int[count];
            _lineHeights = new int[count];

            int y = 0;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    string text = _syncedLines[i].Text;
                    if (string.IsNullOrEmpty(text))
                        text = " ";

                    Size size = TextRenderer.MeasureText(
                        text,
                        _font,
                        new Size(textWidth, MaxLayoutHeight),
                        _textFlags);

                    int h = Math.Max(size.Height, _font.Height);
                    _lineTops[i] = y;
                    _lineHeights[i] = h;
                    y += h + SyncedLineGap;

                    if (y > MaxLayoutHeight)
                    {
                        // Truncate layout if absurdly long — still keep measured lines so far.
                        break;
                    }
                }
            }
            catch (Exception)
            {
                y = Height;
            }

            _totalTextHeight = y > 0 ? y - SyncedLineGap : 0;
        }

        private void OnScrollTick(object sender, EventArgs e)
        {
            if (_hasLyrics && _totalTextHeight <= 0f && Width > 0)
                MeasureLayout();

            SyncFromPosition();
        }

        private void SyncFromPosition()
        {
            if (!_hasLyrics)
                return;

            if (_syncedMode)
                SyncSyncedFromPosition();
            else
                SyncPlainFromPosition();
        }

        private void SyncPlainFromPosition()
        {
            float maxScroll = Math.Max(_totalTextHeight - Height + _padding * 2, 0f);
            float next = ScrollMath.ScrollY(_getPositionMs(), _durationMs, _startDelayMs, maxScroll);

            if ((int)next == (int)_scrollY)
            {
                _scrollY = next;
                return;
            }

            _scrollY = next;
            Invalidate();
        }

        private void SyncSyncedFromPosition()
        {
            // Timestamps are absolute — start delay does not apply in synced mode.
            int positionMs = _getPositionMs();
            int nextActive = LrcParser.ActiveIndex(_syncedLines, positionMs);

            float maxScroll = Math.Max(_totalTextHeight - Height + _padding * 2, 0f);
            float nextScroll = 0f;

            if (nextActive >= 0 && nextActive < _lineTops.Length)
            {
                float lineCenter = _lineTops[nextActive] + (_lineHeights[nextActive] / 2f);
                nextScroll = lineCenter - (Height / 2f) + _padding;
                if (nextScroll < 0f) nextScroll = 0f;
                if (nextScroll > maxScroll) nextScroll = maxScroll;
            }

            bool changed = nextActive != _activeIndex || (int)nextScroll != (int)_scrollY;
            _activeIndex = nextActive;
            _scrollY = nextScroll;

            if (changed)
                Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (Width <= 0 || Height <= 0 || _font == null)
                return;

            if (_syncedMode && _hasLyrics)
            {
                PaintSynced(e.Graphics);
                return;
            }

            if (string.IsNullOrEmpty(_lyrics))
                return;

            int textWidth = Width - (_padding * 2);
            if (textWidth < 1)
                return;

            int layoutHeight = (int)Math.Min(
                Math.Max(_totalTextHeight + _padding, Height),
                MaxLayoutHeight);
            if (layoutHeight < 1)
                layoutHeight = Height;

            Rectangle textRect = new Rectangle(
                _padding,
                _padding - (int)_scrollY,
                textWidth,
                layoutHeight);

            try
            {
                e.Graphics.SetClip(ClientRectangle);
                TextRenderer.DrawText(e.Graphics, _lyrics, _font, textRect, _textColor, _textFlags);
            }
            catch (Exception)
            {
                // Swallow paint failures — an unhandled paint exception paints the red-X death panel.
            }
        }

        private void PaintSynced(Graphics g)
        {
            int textWidth = Width - (_padding * 2);
            if (textWidth < 1 || _lineTops.Length == 0)
                return;

            try
            {
                g.SetClip(ClientRectangle);

                for (int i = 0; i < _syncedLines.Count && i < _lineTops.Length; i++)
                {
                    string text = _syncedLines[i].Text;
                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    int top = _padding + _lineTops[i] - (int)_scrollY;
                    int h = _lineHeights[i];

                    // Skip lines fully outside the viewport.
                    if (top + h < 0 || top > Height)
                        continue;

                    Rectangle rect = new Rectangle(_padding, top, textWidth, h);
                    Color color = i == _activeIndex ? _textColor : _dimTextColor;
                    TextRenderer.DrawText(g, text, _font, rect, color, _textFlags);
                }
            }
            catch (Exception)
            {
                // ignore paint failures
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!_hasLyrics || Width <= 0 || _measurePending || !IsHandleCreated)
                return;

            _measurePending = true;
            BeginInvoke(new Action(() =>
            {
                _measurePending = false;
                if (_hasLyrics && !IsDisposed)
                {
                    MeasureLayout();
                    SyncFromPosition();
                    Invalidate();
                }
            }));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _scrollTimer?.Stop();
                _scrollTimer?.Dispose();
                _font?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
