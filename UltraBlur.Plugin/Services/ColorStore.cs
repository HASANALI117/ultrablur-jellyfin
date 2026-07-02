using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using UltraBlur.Plugin.Data;

namespace UltraBlur.Plugin.Services
{
    /// <summary>
    /// SQLite-backed store for extracted UltraBlur colors with an in-memory
    /// read-through cache. Keyed by (ItemId, ImageType); the stored ImageTag
    /// invalidates rows when the source image changes.
    /// </summary>
    public class ColorStore
    {
        private readonly ILogger<ColorStore> _logger;
        private readonly ConcurrentDictionary<(Guid ItemId, string ImageType), UltraBlurColors> _cache = new();
        private readonly object _dbLock = new();
        private string? _dbPath;
        private bool _loaded;

        /// <summary>
        /// Initializes a new instance of the <see cref="ColorStore"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        public ColorStore(ILogger<ColorStore> logger)
        {
            _logger = logger;
        }

        private string DbPath
        {
            get
            {
                if (_dbPath == null)
                {
                    var plugin = Plugin.Instance
                        ?? throw new InvalidOperationException("UltraBlur plugin instance not initialized.");
                    _dbPath = Path.Combine(plugin.DataDir, "colors.db");
                }

                return _dbPath;
            }
        }

        /// <summary>
        /// Gets cached colors for an item image, or null when absent or stale.
        /// </summary>
        /// <param name="itemId">Jellyfin item ID.</param>
        /// <param name="imageType">Image type the colors were extracted from.</param>
        /// <param name="imageTag">Current image cache tag; a mismatch is treated as stale.</param>
        /// <returns>Stored colors, or null.</returns>
        public UltraBlurColors? Get(Guid itemId, string imageType, string imageTag)
        {
            EnsureLoaded();
            if (_cache.TryGetValue((itemId, imageType), out var colors)
                && string.Equals(colors.ImageTag, imageTag, StringComparison.Ordinal))
            {
                return colors;
            }

            return null;
        }

        /// <summary>
        /// Checks whether current colors exist for an item image.
        /// </summary>
        /// <param name="itemId">Jellyfin item ID.</param>
        /// <param name="imageType">Image type.</param>
        /// <param name="imageTag">Current image cache tag.</param>
        /// <returns>True when a fresh row exists.</returns>
        public bool Has(Guid itemId, string imageType, string imageTag)
        {
            return Get(itemId, imageType, imageTag) != null;
        }

        /// <summary>
        /// Inserts or replaces colors for an item image.
        /// </summary>
        /// <param name="colors">Colors to store.</param>
        public void Upsert(UltraBlurColors colors)
        {
            EnsureLoaded();
            _cache[(colors.ItemId, colors.ImageType)] = colors;

            lock (_dbLock)
            {
                try
                {
                    using var conn = new SqliteConnection($"Data Source={DbPath}");
                    conn.Open();

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        INSERT OR REPLACE INTO Colors (ItemId, ImageType, ImageTag, TopLeft, TopRight, BottomRight, BottomLeft, ComputedAt)
                        VALUES (@ItemId, @ImageType, @ImageTag, @TopLeft, @TopRight, @BottomRight, @BottomLeft, @ComputedAt)
                        """;
                    cmd.Parameters.AddWithValue("@ItemId", colors.ItemId.ToString());
                    cmd.Parameters.AddWithValue("@ImageType", colors.ImageType);
                    cmd.Parameters.AddWithValue("@ImageTag", colors.ImageTag);
                    cmd.Parameters.AddWithValue("@TopLeft", colors.TopLeft);
                    cmd.Parameters.AddWithValue("@TopRight", colors.TopRight);
                    cmd.Parameters.AddWithValue("@BottomRight", colors.BottomRight);
                    cmd.Parameters.AddWithValue("@BottomLeft", colors.BottomLeft);
                    cmd.Parameters.AddWithValue("@ComputedAt", colors.ComputedAt.ToString("O"));
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to persist colors for {ItemId}", colors.ItemId);
                }
            }
        }

        private void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            lock (_dbLock)
            {
                if (_loaded)
                {
                    return;
                }

                InitDatabase();
                LoadFromDatabase();
                _loaded = true;
            }
        }

        private void InitDatabase()
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={DbPath}");
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS Colors (
                        ItemId TEXT NOT NULL,
                        ImageType TEXT NOT NULL,
                        ImageTag TEXT NOT NULL,
                        TopLeft TEXT NOT NULL,
                        TopRight TEXT NOT NULL,
                        BottomRight TEXT NOT NULL,
                        BottomLeft TEXT NOT NULL,
                        ComputedAt TEXT NOT NULL,
                        PRIMARY KEY (ItemId, ImageType)
                    )
                    """;
                cmd.ExecuteNonQuery();

                _logger.LogInformation("Initialized UltraBlur color database at {Path}", DbPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize SQLite database");
            }
        }

        private void LoadFromDatabase()
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={DbPath}");
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT ItemId, ImageType, ImageTag, TopLeft, TopRight, BottomRight, BottomLeft FROM Colors";
                using var reader = cmd.ExecuteReader();

                var count = 0;
                while (reader.Read())
                {
                    var itemId = Guid.Parse(reader.GetString(0));
                    var colors = new UltraBlurColors(
                        itemId,
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6));
                    _cache[(itemId, colors.ImageType)] = colors;
                    count++;
                }

                _logger.LogInformation("Loaded {Count} UltraBlur color rows from database", count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load colors from database");
            }
        }
    }
}
