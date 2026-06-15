using System;
using System.Windows.Forms;
using System.Threading.Tasks;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        // ── MusicBee API connection ───────────────────────────────────────────
        private MusicBeeApiInterface _mbApi;
        private PluginInfo _about = new PluginInfo();

        // ── Plugin modules ────────────────────────────────────────────────────
        private LyricsService _lyricsService;
        private LyricsPanel _lyricsPanel;

        // ─────────────────────────────────────────────────────────────────────
        //  INITIALISE — called by MusicBee immediately after loading the plugin
        // ─────────────────────────────────────────────────────────────────────
        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            _mbApi = new MusicBeeApiInterface();
            _mbApi.Initialise(apiInterfacePtr);

            // Pass a function that gets embedded lyrics — avoids nested class issue
            _lyricsService = new LyricsService(
                () => _mbApi.NowPlaying_GetFileTag(MetaDataType.Lyrics)
            );

            _about.PluginInfoVersion        = PluginInfoVersion;
            _about.Name                     = "LyricScroll";
            _about.Description              = "Auto-scrolling lyrics panel for MusicBee";
            _about.Author                   = "JulioJulioso";
            _about.TargetApplication        = "";
            _about.Type                     = PluginType.PanelView;
            _about.VersionMajor             = 1;
            _about.VersionMinor             = 0;
            _about.Revision                 = 1;
            _about.MinInterfaceVersion      = MinInterfaceVersion;
            _about.MinApiRevision           = MinApiRevision;
            _about.ReceiveNotifications     = ReceiveNotificationFlags.PlayerEvents;
            _about.ConfigurationPanelHeight = 0;

            return _about;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  PANEL — MusicBee calls this to embed our UI inside its interface
        // ─────────────────────────────────────────────────────────────────────
        public int OnDockablePanelCreated(Control panel)
        {
            _lyricsPanel = new LyricsPanel();
            _lyricsPanel.Dock = DockStyle.Fill;
            panel.Controls.Add(_lyricsPanel);
            return -1;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  RECEIVE NOTIFICATION — MusicBee calls this when player events occur
        // ─────────────────────────────────────────────────────────────────────
        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.PluginStartup:
                    break;

                case NotificationType.TrackChanged:
                    OnTrackChanged();
                    break;

                case NotificationType.PlayStateChanged:
                    bool isPlaying = _mbApi.Player_GetPlayState() == PlayState.Playing;
                    _lyricsPanel?.SetPlayState(isPlaying);
                    break;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  TRACK CHANGED — fetch lyrics and send to panel
        // ─────────────────────────────────────────────────────────────────────
        private void OnTrackChanged()
        {
            string title    = _mbApi.NowPlaying_GetFileTag(MetaDataType.TrackTitle);
            string artist   = _mbApi.NowPlaying_GetFileTag(MetaDataType.Artist);
            string album    = _mbApi.NowPlaying_GetFileTag(MetaDataType.Album);
            int    duration = _mbApi.NowPlaying_GetDuration();

            // Fetch lyrics asynchronously — does not block MusicBee's UI thread
            Task.Run(async () =>
            {
                string lyrics = await _lyricsService.GetLyricsAsync(title, artist, album, duration);
                _lyricsPanel?.SetLyrics(lyrics, duration);
            });
        }

        // ── Required MusicBee plugin methods ─────────────────────────────────
        public bool Configure(IntPtr panelHandle) { return false; }
        public void SaveSettings() { }
        public void Close(PluginCloseReason reason) { }
        public void Uninstall() { }
    }
}