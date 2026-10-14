using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using UltraBlur.Plugin.Configuration;

namespace UltraBlur.Plugin
{
    /// <summary>
    /// UltraBlur plugin for Jellyfin.
    /// Extracts Plex-identical 4-corner ambient colors from item artwork via the
    /// bit-exact in-process port of PMS UltraBlur (Algorithm/) and serves them to web themes.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Plugin"/> class.
        /// </summary>
        /// <param name="applicationPaths">Application paths.</param>
        /// <param name="xmlSerializer">XML serializer.</param>
        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;

            DataDir = Path.Combine(applicationPaths.DataPath, "plugins", "UltraBlur");
            Directory.CreateDirectory(DataDir);
        }

        /// <summary>
        /// Gets the plugin instance.
        /// </summary>
        public static Plugin? Instance { get; private set; }

        /// <summary>
        /// Gets the plugin data directory ({DataPath}/plugins/UltraBlur).
        /// </summary>
        public string DataDir { get; }

        /// <inheritdoc />
        public override string Name => "UltraBlur";

        /// <inheritdoc />
        public override Guid Id => Guid.Parse("b6f0f0d4-51c3-4c9a-8f42-3f2a9be0c6d1");

        /// <inheritdoc />
        public override string Description =>
            "Serves Plex-UltraBlur ambient corner colors extracted from item artwork.";

        /// <summary>
        /// Builds the client script tag injected into jellyfin-web's index.html.
        /// Relative src ("../UltraBlur/...") stays correct under a base-url prefix;
        /// the version query busts client caches across plugin updates.
        /// </summary>
        /// <returns>The script tag HTML.</returns>
        internal string BuildScriptTag()
        {
            return $"<script plugin=\"{Name}\" version=\"{Version}\" src=\"../UltraBlur/ClientScript?v={Version}\" defer></script>";
        }

        /// <inheritdoc />
        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = Name,
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                },
            };
        }
    }
}
