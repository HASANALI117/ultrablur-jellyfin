using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using UltraBlur.Plugin.Algorithm;
using UltraBlur.Plugin.Data;

namespace UltraBlur.Plugin.Services
{
    /// <summary>
    /// Resolves an item's artwork, runs the managed bit-exact UltraBlur port
    /// (<see cref="UltraBlurCore"/>) on it, and caches the resulting 4-corner
    /// colors via <see cref="ColorStore"/>.
    /// </summary>
    public class UltraBlurService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ColorStore _store;
        private readonly ILogger<UltraBlurService> _logger;
        private readonly ConcurrentDictionary<(Guid ItemId, string ImageType), SemaphoreSlim> _itemLocks = new();
        private SemaphoreSlim? _globalLimit;
        private int _globalLimitSize;

        /// <summary>
        /// Initializes a new instance of the <see cref="UltraBlurService"/> class.
        /// </summary>
        /// <param name="libraryManager">Jellyfin library manager.</param>
        /// <param name="store">Color cache store.</param>
        /// <param name="httpClientFactory">HTTP client factory, for TMDB image downloads.</param>
        /// <param name="logger">Logger instance.</param>
        public UltraBlurService(
            ILibraryManager libraryManager,
            ColorStore store,
            IHttpClientFactory httpClientFactory,
            ILogger<UltraBlurService> logger)
        {
            _libraryManager = libraryManager;
            _httpClientFactory = httpClientFactory;
            _store = store;
            _logger = logger;
        }

        /// <summary>
        /// Failure categories for a color lookup.
        /// </summary>
        public enum LookupError
        {
            /// <summary>No error.</summary>
            None,

            /// <summary>Item not found in the library.</summary>
            ItemNotFound,

            /// <summary>Item has no usable image (JPEG/PNG/WebP).</summary>
            NoUsableImage,

            /// <summary>Decoding or extraction failed.</summary>
            ExtractionFailed,
        }

        /// <summary>
        /// Result of a color lookup.
        /// </summary>
        /// <param name="Colors">The colors, or null when unavailable.</param>
        /// <param name="Cached">True when served from cache without extracting.</param>
        /// <param name="Error">Failure category when Colors is null.</param>
        public record LookupResult(UltraBlurColors? Colors, bool Cached, LookupError Error);

        /// <summary>
        /// Gets cached colors for an item, computing them on miss.
        /// </summary>
        /// <param name="itemId">Jellyfin item ID.</param>
        /// <param name="preferredType">Preferred image type, or null for the default fallback chain.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The lookup result.</returns>
        public async Task<LookupResult> GetOrComputeAsync(
            Guid itemId,
            ImageType? preferredType,
            CancellationToken cancellationToken)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item == null)
            {
                return new LookupResult(null, false, LookupError.ItemNotFound);
            }

            var image = ResolveImage(item, preferredType);
            if (image == null)
            {
                return new LookupResult(null, false, LookupError.NoUsableImage);
            }

            var (path, imageType, imageTag) = image.Value;
            var typeName = imageType.ToString();

            var cached = _store.Get(itemId, typeName, imageTag);
            if (cached != null)
            {
                return new LookupResult(cached, true, LookupError.None);
            }

            var itemLock = _itemLocks.GetOrAdd((itemId, typeName), _ => new SemaphoreSlim(1, 1));
            await itemLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-check under the lock: a concurrent request may have computed it.
                cached = _store.Get(itemId, typeName, imageTag);
                if (cached != null)
                {
                    return new LookupResult(cached, true, LookupError.None);
                }

                var hex = await ExtractAsync(path, cancellationToken).ConfigureAwait(false);
                if (hex == null)
                {
                    return new LookupResult(null, false, LookupError.ExtractionFailed);
                }

                var colors = new UltraBlurColors(
                    itemId,
                    typeName,
                    imageTag,
                    hex.Value.TopLeft,
                    hex.Value.TopRight,
                    hex.Value.BottomRight,
                    hex.Value.BottomLeft);
                _store.Upsert(colors);
                return new LookupResult(colors, false, LookupError.None);
            }
            finally
            {
                itemLock.Release();
            }
        }

        /// <summary>
        /// Gets cached colors for a remote TMDB image (e.g. a Seerr title that is
        /// not in the library), downloading and computing them on miss. Only
        /// <see cref="IsAllowedImageUrl"/> URLs are accepted; the caller checks.
        /// Rows live in the item table under a URL-derived key; TMDB image paths
        /// are content-addressed, so the URL itself is the cache tag.
        /// </summary>
        /// <param name="url">An allowed TMDB image URL.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The lookup result.</returns>
        public async Task<LookupResult> GetOrComputeForUrlAsync(Uri url, CancellationToken cancellationToken)
        {
            var tag = url.AbsoluteUri;
            var key = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(tag)).AsSpan(0, 16));
            var cached = _store.Get(key, UrlImageType, tag);
            if (cached != null)
            {
                return new LookupResult(cached, true, LookupError.None);
            }

            var itemLock = _itemLocks.GetOrAdd((key, UrlImageType), _ => new SemaphoreSlim(1, 1));
            await itemLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            var tempPath = Path.Combine(Path.GetTempPath(), "ultrablur-" + key.ToString("N"));
            try
            {
                cached = _store.Get(key, UrlImageType, tag);
                if (cached != null)
                {
                    return new LookupResult(cached, true, LookupError.None);
                }

                if (!await DownloadAsync(url, tempPath, cancellationToken).ConfigureAwait(false)
                    || !IsSupportedImage(tempPath))
                {
                    return new LookupResult(null, false, LookupError.NoUsableImage);
                }

                var hex = await ExtractAsync(tempPath, cancellationToken).ConfigureAwait(false);
                if (hex == null)
                {
                    return new LookupResult(null, false, LookupError.ExtractionFailed);
                }

                var colors = new UltraBlurColors(
                    key,
                    UrlImageType,
                    tag,
                    hex.Value.TopLeft,
                    hex.Value.TopRight,
                    hex.Value.BottomRight,
                    hex.Value.BottomLeft);
                _store.Upsert(colors);
                return new LookupResult(colors, false, LookupError.None);
            }
            finally
            {
                File.Delete(tempPath);
                itemLock.Release();
            }
        }

        /// <summary>
        /// Whether a URL may be fetched by <see cref="GetOrComputeForUrlAsync"/>:
        /// https TMDB image CDN paths only (/t/p/&lt;size&gt;/&lt;file&gt;), so the
        /// endpoint cannot be used to make the server fetch arbitrary hosts.
        /// </summary>
        /// <param name="raw">The URL as passed by the client.</param>
        /// <param name="url">The parsed URL when allowed.</param>
        /// <returns>True when allowed.</returns>
        public static bool IsAllowedImageUrl(string? raw, out Uri url)
        {
            url = null!;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed)
                || parsed.Scheme != Uri.UriSchemeHttps
                || !parsed.IsDefaultPort
                || !string.Equals(parsed.Host, "image.tmdb.org", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(parsed.Query)
                || !TmdbPath.IsMatch(parsed.AbsolutePath))
            {
                return false;
            }

            url = parsed;
            return true;
        }

        private static readonly System.Text.RegularExpressions.Regex TmdbPath =
            new("^/t/p/[a-z0-9_]+/[A-Za-z0-9_-]+\\.(jpg|jpeg|png|webp)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>Cache row image type for URL-sourced colors.</summary>
        internal const string UrlImageType = "Url";

        /// <summary>Largest remote image accepted (a TMDB original backdrop is a few MB).</summary>
        private const long MaxDownloadBytes = 25 * 1024 * 1024;

        private async Task<bool> DownloadAsync(Uri url, string path, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxDownloadBytes)
                {
                    return false;
                }

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = File.Create(path);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes)
                    {
                        return false;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                return true;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "UltraBlur could not fetch {Url}", url);
                return false;
            }
        }

        /// <summary>
        /// Resolves the image to sample for an item: preferred type if given,
        /// else a Plex-faithful fallback chain that prefers inherited
        /// backdrops for episodes, seasons, and music before item posters or
        /// stills. Usable formats are JPEG, PNG, and WebP (the decoders verified
        /// byte-identical to Plex's OpenCV build).
        /// </summary>
        /// <param name="item">Library item.</param>
        /// <param name="preferredType">Preferred image type, or null.</param>
        /// <returns>Path, type, and cache tag of the chosen image; null when none usable.</returns>
        internal static (string Path, ImageType Type, string Tag)? ResolveImage(BaseItem item, ImageType? preferredType)
        {
            IEnumerable<(BaseItem Owner, ImageType Type)> candidates = preferredType.HasValue
                ? new[] { (Owner: item, Type: preferredType.Value) }
                : BuildFallbackChain(item);

            foreach (var (owner, type) in candidates)
            {
                var info = owner.GetImageInfo(type, 0);
                if (info == null || string.IsNullOrEmpty(info.Path) || !File.Exists(info.Path))
                {
                    continue;
                }

                if (!IsSupportedImage(info.Path))
                {
                    continue;
                }

                var tag = ComputeTag(info.Path, info.DateModified);
                return (info.Path, type, tag);
            }

            return null;
        }

        private static IEnumerable<(BaseItem Owner, ImageType Type)> BuildFallbackChain(BaseItem item)
        {
            if (item is Episode episode)
            {
                yield return (episode, ImageType.Backdrop);

                var season = episode.FindParent<Season>();
                if (season != null)
                {
                    yield return (season, ImageType.Backdrop);
                }

                if (episode.Series != null)
                {
                    yield return (episode.Series, ImageType.Backdrop);
                }

                yield return (episode, ImageType.Primary);

                if (episode.Series != null)
                {
                    yield return (episode.Series, ImageType.Primary);
                }

                yield break;
            }

            if (item is Season seasonItem)
            {
                yield return (seasonItem, ImageType.Backdrop);

                if (seasonItem.Series != null)
                {
                    yield return (seasonItem.Series, ImageType.Backdrop);
                }

                yield return (seasonItem, ImageType.Primary);

                if (seasonItem.Series != null)
                {
                    yield return (seasonItem.Series, ImageType.Primary);
                }

                yield break;
            }

            if (item is Audio audio)
            {
                var artist = audio.FindParent<MusicArtist>();
                if (artist != null)
                {
                    yield return (artist, ImageType.Backdrop);
                }

                var album = audio.FindParent<MusicAlbum>();
                if (album != null)
                {
                    yield return (album, ImageType.Primary);
                }

                yield return (audio, ImageType.Primary);
                yield break;
            }

            if (item is MusicAlbum musicAlbum)
            {
                var artist = musicAlbum.FindParent<MusicArtist>();
                if (artist != null)
                {
                    yield return (artist, ImageType.Backdrop);
                }

                yield return (musicAlbum, ImageType.Primary);
                yield break;
            }

            yield return (item, ImageType.Backdrop);
            yield return (item, ImageType.Primary);
        }

        private static bool IsSupportedImage(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                Span<byte> magic = stackalloc byte[12];
                var read = fs.ReadAtLeast(magic, 12, throwOnEndOfStream: false);

                // JPEG: FF D8
                if (read >= 2 && magic[0] == 0xFF && magic[1] == 0xD8)
                {
                    return true;
                }

                // PNG: 89 'P' 'N' 'G'
                if (read >= 4 && magic[0] == 0x89 && magic[1] == 0x50 && magic[2] == 0x4E && magic[3] == 0x47)
                {
                    return true;
                }

                // WebP: 'R' 'I' 'F' 'F' <size> 'W' 'E' 'B' 'P'
                if (read >= 12
                    && magic[0] == 0x52 && magic[1] == 0x49 && magic[2] == 0x46 && magic[3] == 0x46
                    && magic[8] == 0x57 && magic[9] == 0x45 && magic[10] == 0x42 && magic[11] == 0x50)
                {
                    return true;
                }

                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static string ComputeTag(string path, DateTime dateModified)
        {
            var input = path + ":" + dateModified.Ticks.ToString(CultureInfo.InvariantCulture);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexStringLower(hash);
        }

        private async Task<(string TopLeft, string TopRight, string BottomRight, string BottomLeft)?> ExtractAsync(
            string imagePath,
            CancellationToken cancellationToken)
        {
            var limit = GetGlobalLimit(Plugin.Instance?.Configuration.MaxConcurrentExtractions ?? 4);
            await limit.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // CPU-bound (decode + median-cut), ~50 ms for a 4K backdrop.
                return await Task.Run(() => Extract(imagePath), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                limit.Release();
            }
        }

        private (string TopLeft, string TopRight, string BottomRight, string BottomLeft)? Extract(string imagePath)
        {
            try
            {
                var image = ImageLoader.Load(imagePath);
                var c = image == null ? null : UltraBlurCore.Extract(image);
                if (c == null)
                {
                    _logger.LogWarning("UltraBlur extraction failed on {Path} (undecodable or no usable pixels)", imagePath);
                    return null;
                }

                return (Hex(c.Value.Tl), Hex(c.Value.Tr), Hex(c.Value.Br), Hex(c.Value.Bl));
            }
#pragma warning disable CA1031 // Native decode of untrusted artwork: any failure = no colors, never a 500.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "UltraBlur extraction threw on {Path}", imagePath);
                return null;
            }
        }

        private static string Hex(uint rgb) => rgb.ToString("x6", CultureInfo.InvariantCulture);

        private SemaphoreSlim GetGlobalLimit(int configured)
        {
            var size = Math.Max(1, configured);
            var current = _globalLimit;
            if (current == null || _globalLimitSize != size)
            {
                // Config changed (or first use): swap in a new semaphore. In-flight
                // waiters keep the old instance; new requests use the new limit.
                current = new SemaphoreSlim(size, size);
                _globalLimit = current;
                _globalLimitSize = size;
            }

            return current;
        }
    }
}
