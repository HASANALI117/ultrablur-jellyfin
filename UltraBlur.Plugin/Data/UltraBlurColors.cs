namespace UltraBlur.Plugin.Data
{
    /// <summary>
    /// Stored UltraBlur corner colors for a media item image.
    /// Hex strings are lowercase 6-char RGB without a leading '#'.
    /// </summary>
    public class UltraBlurColors
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UltraBlurColors"/> class.
        /// </summary>
        /// <param name="itemId">Jellyfin item ID.</param>
        /// <param name="imageType">Image type the colors were extracted from (e.g. Backdrop).</param>
        /// <param name="imageTag">Cache tag of the source image; changes when the image changes.</param>
        /// <param name="topLeft">Top-left corner color.</param>
        /// <param name="topRight">Top-right corner color.</param>
        /// <param name="bottomRight">Bottom-right corner color.</param>
        /// <param name="bottomLeft">Bottom-left corner color.</param>
        public UltraBlurColors(
            Guid itemId,
            string imageType,
            string imageTag,
            string topLeft,
            string topRight,
            string bottomRight,
            string bottomLeft)
        {
            ItemId = itemId;
            ImageType = imageType;
            ImageTag = imageTag;
            TopLeft = topLeft;
            TopRight = topRight;
            BottomRight = bottomRight;
            BottomLeft = bottomLeft;
            ComputedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Gets the Jellyfin item ID.
        /// </summary>
        public Guid ItemId { get; }

        /// <summary>
        /// Gets the image type the colors were extracted from.
        /// </summary>
        public string ImageType { get; }

        /// <summary>
        /// Gets the cache tag of the source image.
        /// </summary>
        public string ImageTag { get; }

        /// <summary>
        /// Gets the top-left corner color ("rrggbb").
        /// </summary>
        public string TopLeft { get; }

        /// <summary>
        /// Gets the top-right corner color ("rrggbb").
        /// </summary>
        public string TopRight { get; }

        /// <summary>
        /// Gets the bottom-right corner color ("rrggbb").
        /// </summary>
        public string BottomRight { get; }

        /// <summary>
        /// Gets the bottom-left corner color ("rrggbb").
        /// </summary>
        public string BottomLeft { get; }

        /// <summary>
        /// Gets the UTC timestamp when the colors were computed.
        /// </summary>
        public DateTime ComputedAt { get; }
    }
}
