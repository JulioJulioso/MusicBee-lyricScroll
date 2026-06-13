using System;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        // ── MusicBee API connection ───────────────────────────────────────────
        private MusicBeeApiInterface _mbApi;
        private PluginInfo _about = new PluginInfo();

        // ─────────────────────────────────────────────────────────────────────
        //  INITIALISE — called by MusicBee immediately after loading the plugin
        // ─────────────────────────────────────────────────────────────────────
        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            _mbApi = new MusicBeeApiInterface();
            _mbApi.Initialise(apiInterfacePtr);

            _about.PluginInfoVersion        = PluginInfoVersion;
            _about.Name                     = "LyricScroll";
            _about.Description              = "Auto-scrolling lyrics panel for MusicBee";
            _about.Author                   = "JulioJulioso";
            _about.TargetApplication        = "";
            _about.Type                     = PluginType.General;
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
        //  RECEIVE NOTIFICATION — MusicBee calls this when player events occur
        // ─────────────────────────────────────────────────────────────────────
        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.PluginStartup:
                    // Plugin loaded successfully — remove this MessageBox before release
                    MessageBox.Show("LyricScroll loaded successfully!", "LyricScroll");
                    break;

                case NotificationType.TrackChanged:
                    // Track changed — lyrics logic will go here
                    string title  = _mbApi.NowPlaying_GetFileTag(MetaDataType.TrackTitle);
                    string artist = _mbApi.NowPlaying_GetFileTag(MetaDataType.Artist);
                    MessageBox.Show($"Now playing: {artist} - {title}", "LyricScroll");
                    break;
            }
        }

        // ── Required MusicBee plugin methods ─────────────────────────────────
        public bool Configure(IntPtr panelHandle) { return false; }
        public void SaveSettings() { }
        public void Close(PluginCloseReason reason) { }
        public void Uninstall() { }
    }
}