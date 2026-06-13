using System;
using System.Drawing;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    /// <summary>
    /// Panel that displays lyrics with automatic vertical scrolling.
    /// Scroll speed is calculated based on track duration.
    /// </summary>
    public class LyricsPanel : UserControl
    {
        // ── UI controls ───────────────────────────────────────────────────────
        private Label _lyricsLabel;
        private Timer _scrollTimer;

        // ── Scroll state ──────────────────────────────────────────────────────
        private int _scrollPixelsPerTick = 1;
        private bool _isPaused = false;
        private bool _hasLyrics = false;

        public LyricsPanel()
        {
            InitializeControls();
        }

        private void InitializeControls()
        {
            // Panel background
            this.BackColor = Color.Black;
            this.DoubleBuffered = true;

            // Label that holds the full lyrics text
            _lyricsLabel = new Label();
            _lyricsLabel.ForeColor = Color.White;
            _lyricsLabel.BackColor = Color.Transparent;
            _lyricsLabel.Font = new Font("Segoe UI", 11f, FontStyle.Regular);
            _lyricsLabel.AutoSize = true;
            _lyricsLabel.Location = new Point(10, 10);
            _lyricsLabel.MaximumSize = new Size(this.Width - 20, 0);
            _lyricsLabel.Padding = new Padding(0, 0, 0, 40);

            this.Controls.Add(_lyricsLabel);

            // Timer that moves the label upward every tick
            _scrollTimer = new Timer();
            _scrollTimer.Interval = 50; // fires every 50ms = 20 times per second
            _scrollTimer.Tick += OnScrollTick;
        }

        /// <summary>
        /// Receives lyrics text and duration, calculates scroll speed and starts scrolling.
        /// </summary>
        public void SetLyrics(string lyrics, int durationMs)
        {
            // Must update UI from the UI thread
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => SetLyrics(lyrics, durationMs)));
                return;
            }

            _scrollTimer.Stop();

            if (string.IsNullOrWhiteSpace(lyrics))
            {
                _lyricsLabel.Text = "No lyrics found.";
                _hasLyrics = false;
                return;
            }

            _lyricsLabel.Text = lyrics;
            _lyricsLabel.Location = new Point(10, 10);
            _hasLyrics = true;

            // Calculate scroll speed:
            // total pixels to scroll = label height - panel height
            // we need to cover that distance in durationMs milliseconds
            // timer fires every 50ms, so total ticks = durationMs / 50
            int totalPixels = Math.Max(_lyricsLabel.Height - this.Height, 0);
            int totalTicks  = Math.Max(durationMs / 50, 1);
            _scrollPixelsPerTick = Math.Max(totalPixels / totalTicks, 1);

            if (!_isPaused)
                _scrollTimer.Start();
        }

        /// <summary>
        /// Called when play state changes — pauses or resumes scroll.
        /// </summary>
        public void SetPlayState(bool isPlaying)
        {
            _isPaused = !isPlaying;

            if (_isPaused)
                _scrollTimer.Stop();
            else if (_hasLyrics)
                _scrollTimer.Start();
        }

        /// <summary>
        /// Moves the lyrics label up by one scroll step.
        /// Stops when the bottom of the label is reached.
        /// </summary>
        private void OnScrollTick(object sender, EventArgs e)
        {
            int newY = _lyricsLabel.Location.Y - _scrollPixelsPerTick;

            // Stop scrolling when last line is visible
            int minY = -((_lyricsLabel.Height - this.Height) + 10);
            if (newY < minY)
            {
                _scrollTimer.Stop();
                return;
            }

            _lyricsLabel.Location = new Point(_lyricsLabel.Location.X, newY);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _scrollTimer?.Stop();
                _scrollTimer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}