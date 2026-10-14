namespace UltraBlur.Plugin.Configuration
{
    /// <summary>
    /// Configurable options for the UltraBlur plugin.
    /// </summary>
    public class PluginConfiguration : MediaBrowser.Model.Plugins.BasePluginConfiguration
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
        /// </summary>
        public PluginConfiguration()
        {
            MaxConcurrentExtractions = 4;
            EnableWarmupTask = true;
            WarmupBatchSize = 500;
            EnableClientScript = true;
        }

        /// <summary>
        /// Gets or sets the maximum number of concurrent extractions.
        /// </summary>
        public int MaxConcurrentExtractions { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the scheduled warm-up task
        /// precomputes colors for library items.
        /// </summary>
        public bool EnableWarmupTask { get; set; }

        /// <summary>
        /// Gets or sets the number of items processed per warm-up task run.
        /// </summary>
        public int WarmupBatchSize { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the UltraBlur client script is
        /// injected into jellyfin-web (paints the ambient gradient on detail pages
        /// without any theme edits).
        /// </summary>
        public bool EnableClientScript { get; set; }
    }
}
