using System;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    public partial class Plugin
    {
        // ── Conexión con MusicBee ─────────────────────────────────────────────
        private MusicBeeApiInterface _mbApi;
        private PluginInfo _about = new PluginInfo();

        // ─────────────────────────────────────────────────────────────────────
        //  INITIALISE — MusicBee llama este método al cargar el plugin
        // ─────────────────────────────────────────────────────────────────────
        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            // Conectamos nuestro plugin con la API de MusicBee
            _mbApi = new MusicBeeApiInterface();
            _mbApi.Initialise(apiInterfacePtr);

            // Datos del plugin — esto aparece en Preferences > Plugins de MusicBee
            _about.PluginInfoVersion = PluginInfoVersion;
            _about.Name              = "LyricScroll";
            _about.Description       = "Auto-scrolling lyrics for MusicBee";
            _about.Author            = "JulioJulioso";
            _about.TargetApplication = "";
            _about.Type              = PluginType.General;
            _about.VersionMajor      = 1;
            _about.VersionMinor      = 0;
            _about.Revision          = 1;
            _about.MinInterfaceVersion = MinInterfaceVersion;
            _about.MinApiRevision      = MinApiRevision;
            _about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;
            _about.ConfigurationPanelHeight = 0;

            return _about;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  RECEIVE NOTIFICATION — MusicBee avisa cuando algo cambia
        // ─────────────────────────────────────────────────────────────────────
        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            switch (type)
            {
                case NotificationType.PluginStartup:
                    // El plugin cargó correctamente
                    MessageBox.Show("LyricScroll cargado correctamente!", "LyricScroll");
                    break;

                case NotificationType.TrackChanged:
                    // Cambió la canción — aquí después irá la lógica de lyrics
                    string title  = _mbApi.NowPlaying_GetFileTag(MetaDataType.TrackTitle);
                    string artist = _mbApi.NowPlaying_GetFileTag(MetaDataType.Artist);
                    MessageBox.Show($"Sonando: {artist} - {title}", "LyricScroll");
                    break;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Métodos requeridos por MusicBee — por ahora vacíos
        // ─────────────────────────────────────────────────────────────────────
        public bool Configure(IntPtr panelHandle) { return false; }
        public void SaveSettings() { }
        public void Close(PluginCloseReason reason) { }
        public void Uninstall() { }
    }
}