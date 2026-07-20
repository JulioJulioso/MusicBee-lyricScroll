using System;
using System.Drawing;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    /// <summary>
    /// Panel that draws lyrics directly using GDI+.
    /// Scroll position follows MusicBee playback position (gentle autoscroll, not karaoke).
    /// </summary>
    public class LyricsPanel : UserControl
    {
        private string _lyrics = string.Empty;
        private float _scrollY = 0f;
        private bool _hasLyrics = false;
        private int _durationMs = 0;
        private int _startDelayMs = 0;
        private float _totalTextHeight = 0f;

        private readonly Font _font = new Font("Segoe UI", 11f, FontStyle.Regular);
        private readonly Brush _textBrush = new SolidBrush(Color.White);
        private readonly int _padding = 10;
        private readonly Func<int> _getPositionMs;

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

        /// <summary>
        /// Global hold at the top before scroll starts (does not shrink duration in the rate math).
        /// </summary>
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

            _lyrics = lyrics;
            _hasLyrics = true;
            MeasureTextHeight();
            SyncScrollFromPosition();
            Invalidate();
            // Always poll while lyrics are shown. Pause/stop freeze via unchanged Player_GetPosition;
            // Loading must not stop the timer (seek / track change briefly leave Playing).
            _scrollTimer.Start();
        }

        /// <summary>
        /// Kept for Plugin notifications. Does not stop the timer — Loading≠Playing used to
        /// freeze scroll after seek / next track.
        /// </summary>
        public void SetPlayState(bool isPlaying)
        {
            if (_hasLyrics)
                _scrollTimer.Start();
            SyncScrollFromPosition();
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
                    new Size(textWidth, 100000),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding);
                _totalTextHeight = size.Height;
            }
            catch (Exception)
            {
                _totalTextHeight = Height;
            }
        }

        private void OnScrollTick(object sender, EventArgs e)
        {
            // Remeasure if SetLyrics ran before the panel had a real width.
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

            // Only skip redraw when the pixel row did not change (0.05f was too coarse for long tracks).
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

            if (string.IsNullOrEmpty(_lyrics))
                return;

            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.SetClip(ClientRectangle);

            RectangleF textRect = new RectangleF(
                _padding,
                _padding - _scrollY,
                Width - (_padding * 2),
                Math.Max(_totalTextHeight, Height) + _padding
            );

            g.DrawString(_lyrics, _font, _textBrush, textRect);
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
                _textBrush?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
