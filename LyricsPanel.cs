using System;
using System.Drawing;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    /// <summary>
    /// Panel that draws lyrics with TextRenderer (not GDI+ DrawString — that crashes on long text).
    /// Scroll follows MusicBee playback position (gentle autoscroll, not karaoke).
    /// </summary>
    public class LyricsPanel : UserControl
    {
        // GDI+/TextRenderer layout sizes above ~32k often throw ExternalException.
        private const int MaxLayoutHeight = 16000;

        private string _lyrics = string.Empty;
        private float _scrollY = 0f;
        private bool _hasLyrics = false;
        private int _durationMs = 0;
        private int _startDelayMs = 0;
        private float _totalTextHeight = 0f;

        private readonly Font _font = new Font("Segoe UI", 11f, FontStyle.Regular);
        private readonly int _padding = 10;
        private readonly Func<int> _getPositionMs;
        private readonly TextFormatFlags _textFlags =
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding |
            TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.NoPrefix;

        private Timer _scrollTimer;
        private bool _measurePending;

        public LyricsPanel(Func<int> getPositionMs)
        {
            _getPositionMs = getPositionMs ?? throw new ArgumentNullException(nameof(getPositionMs));

            BackColor = Color.Black;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            _scrollTimer = new Timer();
            _scrollTimer.Interval = 50;
            _scrollTimer.Tick += OnScrollTick;
        }

        public void SetStartDelayMs(int startDelayMs)
        {
            _startDelayMs = Math.Max(0, startDelayMs);
            SyncScrollFromPosition();
        }

        public void SetLyrics(string lyrics, int durationMs)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => SetLyrics(lyrics, durationMs)));
                return;
            }

            _scrollY = 0f;
            _durationMs = durationMs;
            _totalTextHeight = 0f;

            if (string.IsNullOrWhiteSpace(lyrics))
            {
                _lyrics = "No lyrics found.";
                _hasLyrics = false;
                _scrollTimer.Stop();
                Invalidate();
                return;
            }

            _lyrics = NormalizeLyrics(lyrics);
            _hasLyrics = true;
            MeasureTextHeight();
            SyncScrollFromPosition();
            Invalidate();
            _scrollTimer.Start();
        }

        public void SetPlayState(bool isPlaying)
        {
            if (_hasLyrics)
                _scrollTimer.Start();
            SyncScrollFromPosition();
        }

        private static string NormalizeLyrics(string lyrics)
        {
            // Null chars and odd line endings can make GDI paint blow up.
            return lyrics.Replace("\0", "")
                         .Replace("\r\n", "\n")
                         .Replace('\r', '\n');
        }

        private void MeasureTextHeight()
        {
            if (!IsHandleCreated || Width <= 0 || string.IsNullOrEmpty(_lyrics))
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

        private void OnScrollTick(object sender, EventArgs e)
        {
            if (_hasLyrics && _totalTextHeight <= 0f && Width > 0)
                MeasureTextHeight();

            SyncScrollFromPosition();
        }

        private void SyncScrollFromPosition()
        {
            if (!_hasLyrics)
                return;

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

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (string.IsNullOrEmpty(_lyrics) || Width <= 0 || Height <= 0)
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
                // TextRenderer matches MeasureText and avoids GDI+ DrawString crashes on long lyrics.
                TextRenderer.DrawText(e.Graphics, _lyrics, _font, textRect, Color.White, _textFlags);
            }
            catch (Exception)
            {
                // Swallow paint failures — an unhandled paint exception paints the red-X death panel.
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
                    MeasureTextHeight();
                    SyncScrollFromPosition();
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
