using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Threading.Tasks;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        private MusicBeeApiInterface _mbApi;
        private PluginInfo _about = new PluginInfo();

        private LyricsService _lyricsService;
        private LyricsPanel _lyricsPanel;
        private Control _hostPanel;
        private int _startDelayMs;

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            _mbApi = new MusicBeeApiInterface();
            _mbApi.Initialise(apiInterfacePtr);

            _lyricsService = new LyricsService(GetLocalLyrics);

            _startDelayMs = LoadStartDelayMs();

            _about.PluginInfoVersion        = PluginInfoVersion;
            _about.Name                     = "LyricScroll";
            _about.Description              = "Auto-scrolling lyrics panel for MusicBee";
            _about.Author                   = "JulioJulioso";
            _about.TargetApplication        = "LyricScroll";
            _about.Type                     = PluginType.PanelView;
            _about.VersionMajor             = 1;
            _about.VersionMinor             = 0;
            _about.Revision                 = 5;
            _about.MinInterfaceVersion      = MinInterfaceVersion;
            _about.MinApiRevision           = MinApiRevision;
            _about.ReceiveNotifications     = ReceiveNotificationFlags.PlayerEvents;
            _about.ConfigurationPanelHeight = 50;

            return _about;
        }

        public int OnDockablePanelCreated(Control panel)
        {
            if (_hostPanel != null)
                _hostPanel.VisibleChanged -= HostPanel_VisibleChanged;

            _hostPanel = panel;
            panel.VisibleChanged += HostPanel_VisibleChanged;

            // MB 3.5+ calls this on the GUI thread. Build UI synchronously — panel.BeginInvoke
            // on cold start often never runs, leaving a gray empty host until the plugin is toggled.
            panel.MinimumSize = Size.Empty;
            panel.MaximumSize = Size.Empty;
            BuildPanelUi(panel);
            RefreshLyrics();

            // Layout may finish after this returns; poke again via the main MusicBee window.
            ScheduleColdStartRepairs();

            // 0 = resizable (MusicBee keeps the dock splitter height the user sets).
            // Positive = fixed height and drag-resize snaps back. If resize still fails after
            // updating the DLL, remove LyricScroll from the layout and add it again.
            return 0;
        }

        private void HostPanel_VisibleChanged(object sender, EventArgs e)
        {
            if (_hostPanel == null || !_hostPanel.Visible || _hostPanel.IsDisposed)
                return;

            InvokeOnMbUi(() =>
            {
                if (_hostPanel == null || _hostPanel.IsDisposed)
                    return;
                EnsurePanelUi(_hostPanel);
                RefreshLyrics();
            });
        }

        /// <summary>
        /// Attach lyrics UI if missing; do not recreate a healthy panel (avoids flicker on repairs).
        /// </summary>
        private void EnsurePanelUi(Control panel)
        {
            if (panel == null || panel.IsDisposed)
                return;

            if (_lyricsPanel != null && !_lyricsPanel.IsDisposed && _lyricsPanel.Parent == panel)
            {
                _lyricsPanel.Visible = true;
                _lyricsPanel.BringToFront();
                return;
            }

            BuildPanelUi(panel);
        }

        /// <summary>
        /// Recreate the lyrics control on the host MusicBee gives us.
        /// </summary>
        private void BuildPanelUi(Control panel)
        {
            if (panel == null || panel.IsDisposed)
                return;

            if (_lyricsPanel != null)
            {
                try
                {
                    if (!_lyricsPanel.IsDisposed)
                    {
                        _lyricsPanel.Parent?.Controls.Remove(_lyricsPanel);
                        _lyricsPanel.Dispose();
                    }
                }
                catch
                {
                    // ignore
                }
                _lyricsPanel = null;
            }

            _lyricsPanel = new LyricsPanel(() => _mbApi.Player_GetPosition());
            _lyricsPanel.Dock = DockStyle.Fill;
            _lyricsPanel.SetStartDelayMs(_startDelayMs);
            panel.Controls.Add(_lyricsPanel);
            _lyricsPanel.BringToFront();
            panel.PerformLayout();
        }

        private void RefreshLyrics()
        {
            if (_lyricsPanel == null || _lyricsPanel.IsDisposed)
                return;

            _lyricsPanel.SetPlayState(_mbApi.Player_GetPlayState() == PlayState.Playing);
            OnTrackChanged();
        }

        /// <summary>
        /// Run on MusicBee's main UI thread (notifications are often background threads).
        /// </summary>
        private void InvokeOnMbUi(Action action)
        {
            if (action == null)
                return;

            try
            {
                IntPtr handle = _mbApi.MB_GetWindowHandle();
                Control mbForm = handle != IntPtr.Zero ? Control.FromHandle(handle) : null;

                if (mbForm != null && !mbForm.IsDisposed)
                {
                    if (mbForm.InvokeRequired)
                        mbForm.BeginInvoke(action);
                    else
                        action();
                    return;
                }
            }
            catch
            {
                // fall through
            }

            // Fallback: host panel or lyrics panel
            try
            {
                Control c = _hostPanel ?? (Control)_lyricsPanel;
                if (c != null && !c.IsDisposed && c.IsHandleCreated)
                {
                    if (c.InvokeRequired)
                        c.BeginInvoke(action);
                    else
                        action();
                    return;
                }
            }
            catch
            {
                // ignore
            }

            try { action(); }
            catch { /* ignore */ }
        }

        private void ScheduleColdStartRepairs()
        {
            // ponytail: Task.Delay + MB Invoke is more reliable than a WinForms Timer during startup.
            int[] delaysMs = { 300, 1000, 2500, 5000 };
            foreach (int delay in delaysMs)
            {
                int captured = delay;
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(captured).ConfigureAwait(false);
                        InvokeOnMbUi(() =>
                        {
                            if (_hostPanel == null || _hostPanel.IsDisposed)
                                return;

                            // Only re-attach if the child is missing — avoid layout fights
                            // while the user is resizing the dock splitter.
                            if (_lyricsPanel == null || _lyricsPanel.IsDisposed || _lyricsPanel.Parent != _hostPanel)
                                EnsurePanelUi(_hostPanel);

                            RefreshLyrics();
                        });
                    }
                    catch
                    {
                        // ignore
                    }
                });
            }
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.PluginStartup:
                    InvokeOnMbUi(() =>
                    {
                        if (_hostPanel != null && !_hostPanel.IsDisposed)
                        {
                            EnsurePanelUi(_hostPanel);
                            RefreshLyrics();
                        }
                    });
                    ScheduleColdStartRepairs();
                    break;

                case NotificationType.TrackChanged:
                    InvokeOnMbUi(() =>
                    {
                        if (_lyricsPanel != null && !_lyricsPanel.IsDisposed)
                            _lyricsPanel.SetPlayState(_mbApi.Player_GetPlayState() == PlayState.Playing);
                    });
                    OnTrackChanged();
                    break;

                case NotificationType.PlayStateChanged:
                    InvokeOnMbUi(() =>
                    {
                        bool isPlaying = _mbApi.Player_GetPlayState() == PlayState.Playing;
                        _lyricsPanel?.SetPlayState(isPlaying);
                    });
                    break;

                case NotificationType.NowPlayingLyricsReady:
                    // MusicBee finished downloading/resolving lyrics for the current track.
                    OnTrackChanged();
                    break;
            }
        }

        /// <summary>
        /// Prefer MusicBee's resolved lyrics, then downloaded, then the raw Lyrics tag.
        /// </summary>
        private string GetLocalLyrics()
        {
            string text = TryInvoke(_mbApi.NowPlaying_GetLyrics);
            if (!string.IsNullOrWhiteSpace(text))
                return text;

            text = TryInvoke(_mbApi.NowPlaying_GetDownloadedLyrics);
            if (!string.IsNullOrWhiteSpace(text))
                return text;

            try
            {
                return _mbApi.NowPlaying_GetFileTag(MetaDataType.Lyrics) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string TryInvoke(NowPlaying_GetLyricsDelegate getter)
        {
            try
            {
                if (getter == null)
                    return string.Empty;
                return getter() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void OnTrackChanged()
        {
            string title    = _mbApi.NowPlaying_GetFileTag(MetaDataType.TrackTitle);
            string artist   = _mbApi.NowPlaying_GetFileTag(MetaDataType.Artist);
            string album    = _mbApi.NowPlaying_GetFileTag(MetaDataType.Album);
            int    duration = _mbApi.NowPlaying_GetDuration();

            Task.Run(async () =>
            {
                string lyrics = await _lyricsService.GetLyricsAsync(title, artist, album, duration);
                InvokeOnMbUi(() => _lyricsPanel?.SetLyrics(lyrics, duration));
            });
        }

        public bool Configure(IntPtr panelHandle)
        {
            if (panelHandle == IntPtr.Zero)
                return false;

            Panel panel = (Panel)Control.FromHandle(panelHandle);
            panel.Controls.Clear();

            Label label = new Label
            {
                Text = "Start delay (seconds):",
                AutoSize = true,
                Left = 8,
                Top = 14
            };

            NumericUpDown delayUpDown = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 600,
                Value = Math.Min(600, Math.Max(0, _startDelayMs / 1000)),
                Left = 170,
                Top = 10,
                Width = 80
            };

            delayUpDown.ValueChanged += (s, e) =>
            {
                _startDelayMs = (int)delayUpDown.Value * 1000;
                _lyricsPanel?.SetStartDelayMs(_startDelayMs);
            };

            panel.Controls.Add(label);
            panel.Controls.Add(delayUpDown);
            return false;
        }

        public void SaveSettings()
        {
            SaveStartDelayMs(_startDelayMs);
            _lyricsPanel?.SetStartDelayMs(_startDelayMs);
        }

        public void Close(PluginCloseReason reason)
        {
            if (_hostPanel != null)
            {
                _hostPanel.VisibleChanged -= HostPanel_VisibleChanged;
                _hostPanel = null;
            }

            if (_lyricsPanel != null && !_lyricsPanel.IsDisposed)
            {
                try { _lyricsPanel.Dispose(); }
                catch { /* ignore */ }
            }
            _lyricsPanel = null;
        }

        public void Uninstall()
        {
            try
            {
                string path = DelaySettingsPath();
                if (path != null && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // ignore
            }
        }

        private string DelaySettingsPath()
        {
            try
            {
                string dir = _mbApi.Setting_GetPersistentStoragePath();
                if (string.IsNullOrEmpty(dir))
                    return null;
                return Path.Combine(dir, "LyricScroll_startDelayMs.txt");
            }
            catch
            {
                return null;
            }
        }

        private int LoadStartDelayMs()
        {
            try
            {
                string path = DelaySettingsPath();
                if (path == null || !File.Exists(path))
                    return 0;
                if (int.TryParse(File.ReadAllText(path).Trim(), out int ms) && ms >= 0)
                    return ms;
            }
            catch
            {
                // ignore
            }
            return 0;
        }

        private void SaveStartDelayMs(int ms)
        {
            try
            {
                string path = DelaySettingsPath();
                if (path == null)
                    return;
                File.WriteAllText(path, Math.Max(0, ms).ToString());
            }
            catch
            {
                // ignore
            }
        }
    }
}
