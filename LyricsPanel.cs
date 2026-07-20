using System;
using System.Drawing;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    /// <summary>
    /// Panel that draws lyrics directly using GDI+.
    /// This avoids Label overflow issues and gives full control over clipping and scroll.
    /// </summary>
    public class LyricsPanel : UserControl
    {
        // ── State ─────────────────────────────────────────────────────────────
        private string _lyrics = string.Empty;
        private float _scrollY = 0f;
        private float _scrollPixelsPerTick = 0f;
        private float _scrollAccumulator = 0f;
        private bool _isPaused = false;
        private bool _hasLyrics = false;
        private int _durationMs = 0;
        private float _totalTextHeight = 0f;

        // ── Drawing ───────────────────────────────────────────────────────────
        private readonly Font _font = new Font("Segoe UI", 11f, FontStyle.Regular);
        private readonly Brush _textBrush = new SolidBrush(Color.White);
        private readonly int _padding = 10;

        // ── Timer ─────────────────────────────────────────────────────────────
        private Timer _scrollTimer;
        private bool _measurePending;

        public LyricsPanel()
        {
            this.BackColor = Color.Black;
            this.DoubleBuffered = true;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            _scrollTimer = new Timer();
            _scrollTimer.Interval = 50; // 20 times per second
            _scrollTimer.Tick += OnScrollTick;
        }

        /// <summary>
        /// Receives lyrics and duration from mb, measures text height, starts scroll.
        /// </summary>
        public void SetLyrics(string lyrics, int durationMs)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => SetLyrics(lyrics, durationMs)));
                return;
            }

            _scrollTimer.Stop();
            _scrollY = 0f;
            _scrollAccumulator = 0f;
            _durationMs = durationMs;

            if (string.IsNullOrWhiteSpace(lyrics))
            {
                _lyrics = "No lyrics found.";
                _hasLyrics = false;
                _totalTextHeight = 0f;
                this.Invalidate();
                return;
            }

            _lyrics = lyrics;
            _hasLyrics = true;

            // Measure total text height using current panel width
            MeasureAndStartScroll();
        }

        private void MeasureAndStartScroll()
        {
            if (!IsHandleCreated || Width <= 0 || string.IsNullOrEmpty(_lyrics))
                return;

            int textWidth = Math.Max(Width - (_padding * 2), 1);

            // CreateGraphics + MeasureString during OnResize often throws GDI+ ExternalException.
            // TextRenderer is safer; still guard — huge lyrics can exceed GDI limits.
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
                return;
            }

            Invalidate();

            float totalPixels = Math.Max(_totalTextHeight - Height + _padding, 0f);
            if (totalPixels <= 0f)
                return;

            int totalTicks = Math.Max(_durationMs / 50, 1);
            _scrollPixelsPerTick = Math.Max(totalPixels / totalTicks, 0.1f);

            if (!_isPaused)
                _scrollTimer.Start();
        }

        /// <summary>
        /// Called when play/pause state changes.
        /// </summary>
        public void SetPlayState(bool isPlaying)
        {
            _isPaused = !isPlaying;

            if (_isPaused)
                _scrollTimer.Stop();
            else if (_hasLyrics)
                _scrollTimer.Start();
        }

        private void OnScrollTick(object sender, EventArgs e)
        {
            _scrollAccumulator += _scrollPixelsPerTick;
            float pixels = (int)_scrollAccumulator;

            if (pixels < 1f)
                return;

            _scrollAccumulator -= pixels;

            float maxScroll = Math.Max(_totalTextHeight - this.Height + _padding * 2, 0f);

            if (_scrollY >= maxScroll)
            {
                _scrollTimer.Stop();
                return;
            }

            _scrollY = Math.Min(_scrollY + pixels, maxScroll);
          
            // Redraw panel with new scroll position
            this.Invalidate();
        }

        /// <summary>
        /// Draws lyrics text directly — clipping is automatic since we draw inside the control bounds.
        /// </summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (string.IsNullOrEmpty(_lyrics))
                return;

            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Clip strictly to our bounds — nothing draws outside
            g.SetClip(this.ClientRectangle);

            RectangleF textRect = new RectangleF(
                _padding,
                _padding - _scrollY,
                this.Width - (_padding * 2),
                _totalTextHeight + _padding
            );

            g.DrawString(_lyrics, _font, _textBrush, textRect);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // Defer measure — calling GDI measure inside UpdateBounds crashes MusicBee.
            if (!_hasLyrics || Width <= 0 || _measurePending || !IsHandleCreated)
                return;

            _measurePending = true;
            BeginInvoke(new Action(() =>
            {
                _measurePending = false;
                if (_hasLyrics && !IsDisposed)
                    MeasureAndStartScroll();
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