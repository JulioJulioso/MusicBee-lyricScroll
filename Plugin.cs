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
        private PluginSettings _settings = new PluginSettings();

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            _mbApi = new MusicBeeApiInterface();
            _mbApi.Initialise(apiInterfacePtr);

            _lyricsService = new LyricsService(GetLocalLyrics);
            _settings = PluginSettings.Load(PersistentDir());

            _about.PluginInfoVersion        = PluginInfoVersion;
            _about.Name                     = "LyricScroll";
            _about.Description              = "Auto-scrolling lyrics panel for MusicBee";
            _about.Author                   = "JulioJulioso";
            _about.TargetApplication        = "LyricScroll";
            _about.Type                     = PluginType.PanelView;
            _about.VersionMajor             = 1;
            _about.VersionMinor             = 2;
            _about.Revision                 = 0;
            _about.MinInterfaceVersion      = MinInterfaceVersion;
            _about.MinApiRevision           = MinApiRevision;
            _about.ReceiveNotifications     = ReceiveNotificationFlags.PlayerEvents;
            _about.ConfigurationPanelHeight = 200;

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
            _lyricsPanel.ApplyAppearance(_settings);
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
                LyricsResult lyrics = await _lyricsService.GetLyricsAsync(
                    title, artist, album, duration, _settings.PreferSyncedLines);
                InvokeOnMbUi(() => _lyricsPanel?.SetLyrics(lyrics, duration));
            });
        }

        public bool Configure(IntPtr panelHandle)
        {
            if (panelHandle == IntPtr.Zero)
                return false;

            Panel panel = (Panel)Control.FromHandle(panelHandle);
            panel.Controls.Clear();

            int y = 10;

            Label delayLabel = new Label
            {
                Text = "Start delay (seconds):",
                AutoSize = true,
                Left = 8,
                Top = y + 3
            };
            NumericUpDown delayUpDown = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 600,
                Value = Math.Min(600, Math.Max(0, _settings.StartDelayMs / 1000)),
                Left = 170,
                Top = y,
                Width = 70
            };
            delayUpDown.ValueChanged += (s, e) =>
            {
                _settings.StartDelayMs = (int)delayUpDown.Value * 1000;
                ApplySettingsToPanel();
            };
            y += 28;

            CheckBox syncedCheck = new CheckBox
            {
                Text = "Prefer synced lines (LRCLIB LRC)",
                AutoSize = true,
                Left = 8,
                Top = y,
                Checked = _settings.PreferSyncedLines
            };
            syncedCheck.CheckedChanged += (s, e) =>
            {
                _settings.PreferSyncedLines = syncedCheck.Checked;
                OnTrackChanged();
            };
            y += 28;

            Label padLabel = new Label
            {
                Text = "Padding (px):",
                AutoSize = true,
                Left = 8,
                Top = y + 3
            };
            NumericUpDown padUpDown = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 48,
                Value = Math.Min(48, Math.Max(0, _settings.PaddingPx)),
                Left = 170,
                Top = y,
                Width = 70
            };
            padUpDown.ValueChanged += (s, e) =>
            {
                _settings.PaddingPx = (int)padUpDown.Value;
                ApplySettingsToPanel();
            };
            y += 36;

            Button backBtn = MakeColorButton("Background…", _settings.BackColor, 8, y);
            backBtn.Click += (s, e) =>
            {
                using (var dlg = new ColorDialog { Color = _settings.BackColor, FullOpen = true })
                {
                    if (dlg.ShowDialog() != DialogResult.OK)
                        return;
                    _settings.BackColorHex = PluginSettings.ToHex(dlg.Color);
                    backBtn.BackColor = dlg.Color;
                    backBtn.ForeColor = ContrastText(dlg.Color);
                    ApplySettingsToPanel();
                }
            };

            Button textBtn = MakeColorButton("Text…", _settings.TextColor, 150, y);
            textBtn.Click += (s, e) =>
            {
                using (var dlg = new ColorDialog { Color = _settings.TextColor, FullOpen = true })
                {
                    if (dlg.ShowDialog() != DialogResult.OK)
                        return;
                    _settings.TextColorHex = PluginSettings.ToHex(dlg.Color);
                    textBtn.BackColor = dlg.Color;
                    textBtn.ForeColor = ContrastText(dlg.Color);
                    ApplySettingsToPanel();
                }
            };
            y += 36;

            Button fontBtn = new Button
            {
                Text = "Font: " + _settings.FontFamily + " " + _settings.FontSizePt.ToString("0.#") + "pt",
                Left = 8,
                Top = y,
                Width = 280,
                Height = 28
            };
            fontBtn.Click += (s, e) =>
            {
                using (Font current = _settings.CreateFont())
                using (var dlg = new FontDialog { Font = current, MinSize = 8, MaxSize = 48 })
                {
                    if (dlg.ShowDialog() != DialogResult.OK)
                        return;
                    _settings.FontFamily = dlg.Font.FontFamily.Name;
                    _settings.FontSizePt = dlg.Font.SizeInPoints;
                    _settings.FontBold = dlg.Font.Bold;
                    fontBtn.Text = "Font: " + _settings.FontFamily + " " + _settings.FontSizePt.ToString("0.#") + "pt";
                    ApplySettingsToPanel();
                }
            };
            y += 36;

            Button resetBtn = new Button
            {
                Text = "Reset look",
                Left = 8,
                Top = y,
                Width = 100,
                Height = 26
            };
            resetBtn.Click += (s, e) =>
            {
                int delay = _settings.StartDelayMs;
                bool preferSynced = _settings.PreferSyncedLines;
                _settings = new PluginSettings
                {
                    StartDelayMs = delay,
                    PreferSyncedLines = preferSynced
                };
                delayUpDown.Value = Math.Min(600, Math.Max(0, delay / 1000));
                syncedCheck.Checked = preferSynced;
                padUpDown.Value = _settings.PaddingPx;
                backBtn.BackColor = _settings.BackColor;
                backBtn.ForeColor = ContrastText(_settings.BackColor);
                textBtn.BackColor = _settings.TextColor;
                textBtn.ForeColor = ContrastText(_settings.TextColor);
                fontBtn.Text = "Font: " + _settings.FontFamily + " " + _settings.FontSizePt.ToString("0.#") + "pt";
                ApplySettingsToPanel();
            };

            panel.Controls.Add(delayLabel);
            panel.Controls.Add(delayUpDown);
            panel.Controls.Add(syncedCheck);
            panel.Controls.Add(padLabel);
            panel.Controls.Add(padUpDown);
            panel.Controls.Add(backBtn);
            panel.Controls.Add(textBtn);
            panel.Controls.Add(fontBtn);
            panel.Controls.Add(resetBtn);
            return false;
        }

        private static Button MakeColorButton(string text, Color color, int left, int top)
        {
            return new Button
            {
                Text = text,
                Left = left,
                Top = top,
                Width = 130,
                Height = 28,
                BackColor = color,
                ForeColor = ContrastText(color),
                FlatStyle = FlatStyle.Flat
            };
        }

        private static Color ContrastText(Color bg)
        {
            // Relative luminance threshold — pick black or white label text.
            double y = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
            return y > 0.55 ? Color.Black : Color.White;
        }

        private void ApplySettingsToPanel()
        {
            _lyricsPanel?.ApplyAppearance(_settings);
        }

        public void SaveSettings()
        {
            _settings.Save(PersistentDir());
            ApplySettingsToPanel();
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
                string dir = PersistentDir();
                if (string.IsNullOrEmpty(dir))
                    return;

                string json = Path.Combine(dir, "LyricScroll.settings.json");
                if (File.Exists(json))
                    File.Delete(json);

                string legacy = Path.Combine(dir, "LyricScroll_startDelayMs.txt");
                if (File.Exists(legacy))
                    File.Delete(legacy);
            }
            catch
            {
                // ignore
            }
        }

        private string PersistentDir()
        {
            try
            {
                return _mbApi.Setting_GetPersistentStoragePath();
            }
            catch
            {
                return null;
            }
        }
    }
}
