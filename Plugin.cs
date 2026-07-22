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
        private LyricsFetchMode _fetchMode = LyricsFetchMode.Auto;

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            _mbApi = new MusicBeeApiInterface();
            _mbApi.Initialise(apiInterfacePtr);

            _lyricsService = new LyricsService(GetLocalLyrics);
            _settings = PluginSettings.Load(PersistentDir());

            _about.PluginInfoVersion        = PluginInfoVersion;
            _about.Name                     = "LyricScroll";
            _about.Description              = "Auto-scrolling lyrics panel for MusicBee";
            _about.Author                   = "Julio Rodríguez";
            _about.TargetApplication        = "LyricScroll";
            _about.Type                     = PluginType.PanelView;
            _about.VersionMajor             = 1;
            _about.VersionMinor             = 4;
            _about.Revision                 = 1;
            _about.MinInterfaceVersion      = MinInterfaceVersion;
            _about.MinApiRevision           = MinApiRevision;
            _about.ReceiveNotifications     = ReceiveNotificationFlags.PlayerEvents;
            _about.ConfigurationPanelHeight = 280;

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
            _lyricsPanel.ContextMenuStrip = BuildLyricsContextMenu();
            panel.Controls.Add(_lyricsPanel);
            _lyricsPanel.BringToFront();
            panel.PerformLayout();
        }

        private ContextMenuStrip BuildLyricsContextMenu()
        {
            var menu = new ContextMenuStrip();

            var sourceInfo = new ToolStripMenuItem("Source: …") { Enabled = false };
            menu.Items.Add(sourceInfo);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Reload (auto)", null, (s, e) => FetchLyrics(LyricsFetchMode.Auto));
            menu.Items.Add("Use MusicBee lyrics", null, (s, e) => FetchLyrics(LyricsFetchMode.LocalOnly));
            menu.Items.Add("Use LRCLIB", null, (s, e) => FetchLyrics(LyricsFetchMode.LrclibOnly));
            menu.Items.Add("Open LRCLIB in browser", null, (s, e) => OpenLrclibInBrowser());
            menu.Items.Add("Copy lyrics", null, (s, e) =>
            {
                try
                {
                    string text = _lyricsPanel?.CopyText ?? string.Empty;
                    if (!string.IsNullOrEmpty(text))
                        Clipboard.SetText(text);
                }
                catch { /* ignore */ }
            });
            menu.Items.Add(new ToolStripSeparator());

            var preferSynced = new ToolStripMenuItem("Prefer synced lines")
            {
                CheckOnClick = true,
                Checked = _settings.PreferSyncedLines
            };
            EventHandler preferHandler = (s, e) =>
            {
                _settings.PreferSyncedLines = preferSynced.Checked;
                _settings.Save(PersistentDir());
                FetchLyrics(_fetchMode);
            };
            preferSynced.CheckedChanged += preferHandler;
            menu.Items.Add(preferSynced);
            menu.Items.Add("Settings…", null, (s, e) => ShowSettingsDialog());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("About LyricScroll…", null, (s, e) => ShowAboutDialog());

            menu.Opening += (s, e) =>
            {
                sourceInfo.Text = "Source: " + (string.IsNullOrEmpty(_lyricsPanel?.SourceLabel)
                    ? "—"
                    : _lyricsPanel.SourceLabel);
                preferSynced.CheckedChanged -= preferHandler;
                preferSynced.Checked = _settings.PreferSyncedLines;
                preferSynced.CheckedChanged += preferHandler;
            };

            return menu;
        }

        private void FetchLyrics(LyricsFetchMode mode)
        {
            _fetchMode = mode;
            OnTrackChanged();
        }

        private void OpenLrclibInBrowser()
        {
            try
            {
                string title = _mbApi.NowPlaying_GetFileTag(MetaDataType.TrackTitle) ?? string.Empty;
                string artist = _mbApi.NowPlaying_GetFileTag(MetaDataType.Artist) ?? string.Empty;
                string q = (artist + " " + title).Trim();
                if (string.IsNullOrEmpty(q))
                    q = title;
                string url = "https://lrclib.net/search?q=" + Uri.EscapeDataString(q);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch { /* ignore */ }
        }

        private void OpenGitHub()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/JulioJulioso",
                    UseShellExecute = true
                });
            }
            catch { /* ignore */ }
        }

        private string VersionString() =>
            _about.VersionMajor + "." + _about.VersionMinor + "." + _about.Revision;

        private void ShowAboutDialog()
        {
            using (var form = new Form())
            {
                form.Text = "About LyricScroll";
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.StartPosition = FormStartPosition.CenterParent;
                form.ClientSize = new Size(340, 168);
                form.ShowInTaskbar = false;

                var titleFont = new Font(form.Font.FontFamily, 12f, FontStyle.Bold);
                var title = new Label
                {
                    Text = "LyricScroll",
                    Font = titleFont,
                    AutoSize = true,
                    Left = 16,
                    Top = 16
                };
                form.FormClosed += (s, e) => titleFont.Dispose();
                var version = new Label
                {
                    Text = "Version " + VersionString(),
                    AutoSize = true,
                    Left = 16,
                    Top = 44
                };
                var author = new Label
                {
                    Text = "Author: Julio Rodríguez",
                    AutoSize = true,
                    Left = 16,
                    Top = 68
                };
                var github = new LinkLabel
                {
                    Text = "GitHub: JulioJulioso",
                    AutoSize = true,
                    Left = 16,
                    Top = 92
                };
                github.LinkClicked += (s, e) => OpenGitHub();

                var ok = new Button
                {
                    Text = "OK",
                    Left = 240,
                    Top = 126,
                    Width = 80,
                    Height = 26,
                    DialogResult = DialogResult.OK
                };

                form.Controls.Add(title);
                form.Controls.Add(version);
                form.Controls.Add(author);
                form.Controls.Add(github);
                form.Controls.Add(ok);
                form.AcceptButton = ok;
                ShowOwnedDialog(form);
            }
        }

        private void ShowSettingsDialog()
        {
            using (var form = new Form())
            {
                form.Text = "LyricScroll — Settings";
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.StartPosition = FormStartPosition.CenterParent;
                form.ClientSize = new Size(340, 360);
                form.ShowInTaskbar = false;

                int y = 12;

                Label delayLabel = new Label
                {
                    Text = "Start delay (seconds):",
                    AutoSize = true,
                    Left = 12,
                    Top = y + 3
                };
                NumericUpDown delayUpDown = new NumericUpDown
                {
                    Minimum = 0,
                    Maximum = 600,
                    Value = Math.Min(600, Math.Max(0, _settings.StartDelayMs / 1000)),
                    Left = 190,
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
                    Left = 12,
                    Top = y,
                    Checked = _settings.PreferSyncedLines
                };
                syncedCheck.CheckedChanged += (s, e) =>
                {
                    _settings.PreferSyncedLines = syncedCheck.Checked;
                    FetchLyrics(_fetchMode);
                };
                y += 28;

                Label padLeftLabel = new Label { Text = "Padding left (px):", AutoSize = true, Left = 12, Top = y + 3 };
                NumericUpDown padLeftUp = MakePadUpDown(_settings.PaddingLeftPx, 190, y);
                padLeftUp.ValueChanged += (s, e) =>
                {
                    _settings.PaddingLeftPx = (int)padLeftUp.Value;
                    ApplySettingsToPanel();
                };
                y += 28;

                Label padTopLabel = new Label { Text = "Padding top (px):", AutoSize = true, Left = 12, Top = y + 3 };
                NumericUpDown padTopUp = MakePadUpDown(_settings.PaddingTopPx, 190, y);
                padTopUp.ValueChanged += (s, e) =>
                {
                    _settings.PaddingTopPx = (int)padTopUp.Value;
                    ApplySettingsToPanel();
                };
                y += 28;

                Label gapLabel = new Label { Text = "Line spacing (px):", AutoSize = true, Left = 12, Top = y + 3 };
                NumericUpDown gapUp = new NumericUpDown
                {
                    Minimum = 0,
                    Maximum = 32,
                    Value = Math.Min(32, Math.Max(0, _settings.LineSpacingPx)),
                    Left = 190,
                    Top = y,
                    Width = 70
                };
                gapUp.ValueChanged += (s, e) =>
                {
                    _settings.LineSpacingPx = (int)gapUp.Value;
                    ApplySettingsToPanel();
                };
                y += 28;

                Label effectLabel = new Label { Text = "Text effect:", AutoSize = true, Left = 12, Top = y + 3 };
                ComboBox effectCombo = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Left = 120,
                    Top = y,
                    Width = 140
                };
                effectCombo.Items.AddRange(new object[] { "None", "Shadow", "Outline" });
                effectCombo.SelectedIndex = Math.Min(2, Math.Max(0, (int)_settings.TextEffect));
                effectCombo.SelectedIndexChanged += (s, e) =>
                {
                    _settings.TextEffect = (TextEffectKind)effectCombo.SelectedIndex;
                    ApplySettingsToPanel();
                };
                y += 34;

                Button backBtn = MakeColorButton("Background…", _settings.BackColor, 12, y);
                backBtn.Click += (s, e) =>
                {
                    if (!TryPickColor(form, _settings.BackColor, out Color picked))
                        return;
                    _settings.BackColorHex = PluginSettings.ToHex(picked);
                    backBtn.BackColor = picked;
                    backBtn.ForeColor = ContrastText(picked);
                    ApplySettingsToPanel();
                };

                Button textBtn = MakeColorButton("Text…", _settings.TextColor, 160, y);
                textBtn.Click += (s, e) =>
                {
                    if (!TryPickColor(form, _settings.TextColor, out Color picked))
                        return;
                    _settings.TextColorHex = PluginSettings.ToHex(picked);
                    textBtn.BackColor = picked;
                    textBtn.ForeColor = ContrastText(picked);
                    ApplySettingsToPanel();
                };
                y += 36;

                Button fontBtn = new Button
                {
                    Text = "Font: " + _settings.FontFamily + " " + _settings.FontSizePt.ToString("0.#") + "pt",
                    Left = 12,
                    Top = y,
                    Width = 300,
                    Height = 28
                };
                fontBtn.Click += (s, e) =>
                {
                    using (Font current = _settings.CreateFont())
                    using (var dlg = new FontDialog { Font = current, MinSize = 8, MaxSize = 48 })
                    {
                        if (dlg.ShowDialog(form) != DialogResult.OK)
                            return;
                        _settings.FontFamily = dlg.Font.FontFamily.Name;
                        _settings.FontSizePt = dlg.Font.SizeInPoints;
                        _settings.FontBold = dlg.Font.Bold;
                        fontBtn.Text = "Font: " + _settings.FontFamily + " " + _settings.FontSizePt.ToString("0.#") + "pt";
                        ApplySettingsToPanel();
                    }
                };
                y += 40;

                Button resetBtn = new Button { Text = "Reset look", Left = 12, Top = y, Width = 100, Height = 26 };
                resetBtn.Click += (s, e) =>
                {
                    int delay = _settings.StartDelayMs;
                    bool preferSynced = _settings.PreferSyncedLines;
                    _settings = new PluginSettings
                    {
                        StartDelayMs = delay,
                        PreferSyncedLines = preferSynced
                    };
                    padLeftUp.Value = _settings.PaddingLeftPx;
                    padTopUp.Value = _settings.PaddingTopPx;
                    gapUp.Value = _settings.LineSpacingPx;
                    effectCombo.SelectedIndex = (int)_settings.TextEffect;
                    backBtn.BackColor = _settings.BackColor;
                    backBtn.ForeColor = ContrastText(_settings.BackColor);
                    textBtn.BackColor = _settings.TextColor;
                    textBtn.ForeColor = ContrastText(_settings.TextColor);
                    fontBtn.Text = "Font: " + _settings.FontFamily + " " + _settings.FontSizePt.ToString("0.#") + "pt";
                    ApplySettingsToPanel();
                };

                Button okBtn = new Button
                {
                    Text = "OK",
                    Left = 230,
                    Top = y,
                    Width = 80,
                    Height = 26,
                    DialogResult = DialogResult.OK
                };

                form.Controls.Add(delayLabel);
                form.Controls.Add(delayUpDown);
                form.Controls.Add(syncedCheck);
                form.Controls.Add(padLeftLabel);
                form.Controls.Add(padLeftUp);
                form.Controls.Add(padTopLabel);
                form.Controls.Add(padTopUp);
                form.Controls.Add(gapLabel);
                form.Controls.Add(gapUp);
                form.Controls.Add(effectLabel);
                form.Controls.Add(effectCombo);
                form.Controls.Add(backBtn);
                form.Controls.Add(textBtn);
                form.Controls.Add(fontBtn);
                form.Controls.Add(resetBtn);
                form.Controls.Add(okBtn);
                form.AcceptButton = okBtn;

                form.FormClosed += (s, e) =>
                {
                    _settings.Save(PersistentDir());
                    ApplySettingsToPanel();
                };

                ShowOwnedDialog(form);
            }
        }

        private static NumericUpDown MakePadUpDown(int value, int left, int top)
        {
            return new NumericUpDown
            {
                Minimum = 0,
                Maximum = 64,
                Value = Math.Min(64, Math.Max(0, value)),
                Left = left,
                Top = top,
                Width = 70
            };
        }

        private bool TryPickColor(IWin32Window owner, Color current, out Color picked)
        {
            picked = current;
            using (var dlg = new ColorDialog
            {
                Color = current,
                FullOpen = true,
                AnyColor = true,
                SolidColorOnly = false,
                CustomColors = PluginSettings.NormalizeCustomColors(_settings.CustomColors)
            })
            {
                DialogResult result = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
                if (result != DialogResult.OK)
                    return false;

                picked = dlg.Color;
                _settings.CustomColors = PluginSettings.NormalizeCustomColors(dlg.CustomColors);
                _settings.Save(PersistentDir());
                return true;
            }
        }

        private void ShowOwnedDialog(Form form)
        {
            try
            {
                Control owner = _hostPanel ?? (Control)_lyricsPanel;
                if (owner != null && !owner.IsDisposed)
                    form.ShowDialog(owner.FindForm() ?? owner);
                else
                    form.ShowDialog();
            }
            catch
            {
                form.ShowDialog();
            }
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
                    _fetchMode = LyricsFetchMode.Auto;
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
                    title, artist, album, duration,
                    _settings.PreferSyncedLines,
                    _fetchMode);
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

            Label padLeftLabel = new Label
            {
                Text = "Padding left (px):",
                AutoSize = true,
                Left = 8,
                Top = y + 3
            };
            NumericUpDown padLeftUp = MakePadUpDown(_settings.PaddingLeftPx, 170, y);
            padLeftUp.ValueChanged += (s, e) =>
            {
                _settings.PaddingLeftPx = (int)padLeftUp.Value;
                ApplySettingsToPanel();
            };
            y += 28;

            Label padTopLabel = new Label
            {
                Text = "Padding top (px):",
                AutoSize = true,
                Left = 8,
                Top = y + 3
            };
            NumericUpDown padTopUp = MakePadUpDown(_settings.PaddingTopPx, 170, y);
            padTopUp.ValueChanged += (s, e) =>
            {
                _settings.PaddingTopPx = (int)padTopUp.Value;
                ApplySettingsToPanel();
            };
            y += 28;

            Label gapLabel = new Label
            {
                Text = "Line spacing (px):",
                AutoSize = true,
                Left = 8,
                Top = y + 3
            };
            NumericUpDown gapUp = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 32,
                Value = Math.Min(32, Math.Max(0, _settings.LineSpacingPx)),
                Left = 170,
                Top = y,
                Width = 70
            };
            gapUp.ValueChanged += (s, e) =>
            {
                _settings.LineSpacingPx = (int)gapUp.Value;
                ApplySettingsToPanel();
            };
            y += 28;

            Label effectLabel = new Label
            {
                Text = "Text effect:",
                AutoSize = true,
                Left = 8,
                Top = y + 3
            };
            ComboBox effectCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = 110,
                Top = y,
                Width = 130
            };
            effectCombo.Items.AddRange(new object[] { "None", "Shadow", "Outline" });
            effectCombo.SelectedIndex = Math.Min(2, Math.Max(0, (int)_settings.TextEffect));
            effectCombo.SelectedIndexChanged += (s, e) =>
            {
                _settings.TextEffect = (TextEffectKind)effectCombo.SelectedIndex;
                ApplySettingsToPanel();
            };
            y += 32;

            Button backBtn = MakeColorButton("Background…", _settings.BackColor, 8, y);
            backBtn.Click += (s, e) =>
            {
                if (!TryPickColor(null, _settings.BackColor, out Color picked))
                    return;
                _settings.BackColorHex = PluginSettings.ToHex(picked);
                backBtn.BackColor = picked;
                backBtn.ForeColor = ContrastText(picked);
                ApplySettingsToPanel();
            };

            Button textBtn = MakeColorButton("Text…", _settings.TextColor, 150, y);
            textBtn.Click += (s, e) =>
            {
                if (!TryPickColor(null, _settings.TextColor, out Color picked))
                    return;
                _settings.TextColorHex = PluginSettings.ToHex(picked);
                textBtn.BackColor = picked;
                textBtn.ForeColor = ContrastText(picked);
                ApplySettingsToPanel();
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
                padLeftUp.Value = _settings.PaddingLeftPx;
                padTopUp.Value = _settings.PaddingTopPx;
                gapUp.Value = _settings.LineSpacingPx;
                effectCombo.SelectedIndex = (int)_settings.TextEffect;
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
            panel.Controls.Add(padLeftLabel);
            panel.Controls.Add(padLeftUp);
            panel.Controls.Add(padTopLabel);
            panel.Controls.Add(padTopUp);
            panel.Controls.Add(gapLabel);
            panel.Controls.Add(gapUp);
            panel.Controls.Add(effectLabel);
            panel.Controls.Add(effectCombo);
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
