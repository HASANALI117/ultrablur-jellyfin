using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using UltraBlur.Plugin.Data;

namespace UltraBlur.Plugin.Services
{
    /// <summary>
    /// Resolves an item's artwork, runs ultrablur_cli on it, and caches the
    /// resulting 4-corner colors via <see cref="ColorStore"/>.
    /// </summary>
    public partial class UltraBlurService
    {
        private readonly ILibraryManager _libraryManager;
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
        /// <param name="logger">Logger instance.</param>
        public UltraBlurService(
            ILibraryManager libraryManager,
            ColorStore store,
            ILogger<UltraBlurService> logger)
        {
            _libraryManager = libraryManager;
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

            /// <summary>Item has no usable JPEG image.</summary>
            NoUsableImage,

            /// <summary>ultrablur_cli failed or timed out.</summary>
            ExtractionFailed,
        }

        /// <summary>
        /// Result of a color lookup.
        /// </summary>
        /// <param name="Colors">The colors, or null when unavailable.</param>
        /// <param name="Cached">True when served from cache without running the CLI.</param>
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

                var hex = await RunCliAsync(path, cancellationToken).ConfigureAwait(false);
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
        /// Resolves the image to sample for an item: preferred type if given,
        /// else Backdrop, then Primary, then the parent series' Backdrop for
        /// episodes. Only JPEG files are usable (the CLI's decoder is
        /// JPEG-only by design, for bit-exactness with Plex).
        /// </summary>
        /// <param name="item">Library item.</param>
        /// <param name="preferredType">Preferred image type, or null.</param>
        /// <returns>Path, type, and cache tag of the chosen image; null when none usable.</returns>
        internal static (string Path, ImageType Type, string Tag)? ResolveImage(BaseItem item, ImageType? preferredType)
        {
            var candidates = preferredType.HasValue
                ? new[] { (item, preferredType.Value) }
                : BuildFallbackChain(item);

            foreach (var (owner, type) in candidates)
            {
                var info = owner.GetImageInfo(type, 0);
                if (info == null || string.IsNullOrEmpty(info.Path) || !File.Exists(info.Path))
                {
                    continue;
                }

                if (!IsJpeg(info.Path))
                {
                    continue;
                }

                var tag = ComputeTag(info.Path, info.DateModified);
                return (info.Path, type, tag);
            }

            return null;
        }

        private static (BaseItem Owner, ImageType Type)[] BuildFallbackChain(BaseItem item)
        {
            if (item is Episode episode && episode.Series != null)
            {
                return new[]
                {
                    (item, ImageType.Backdrop),
                    ((BaseItem)episode.Series, ImageType.Backdrop),
                    (item, ImageType.Primary),
                    ((BaseItem)episode.Series, ImageType.Primary),
                };
            }

            return new[]
            {
                (item, ImageType.Backdrop),
                (item, ImageType.Primary),
            };
        }

        private static bool IsJpeg(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                Span<byte> magic = stackalloc byte[2];
                return fs.Read(magic) == 2 && magic[0] == 0xFF && magic[1] == 0xD8;
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

        [GeneratedRegex("topLeft=([0-9a-f]{6}) topRight=([0-9a-f]{6}) bottomRight=([0-9a-f]{6}) bottomLeft=([0-9a-f]{6})")]
        private static partial Regex CliOutputRegex();

        private async Task<(string TopLeft, string TopRight, string BottomRight, string BottomLeft)?> RunCliAsync(
            string imagePath,
            CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogError("Plugin instance not available");
                return null;
            }

            if (!File.Exists(config.CliPath))
            {
                _logger.LogError("ultrablur_cli not found at {CliPath}. Configure CliPath in plugin settings.", config.CliPath);
                return null;
            }

            var limit = GetGlobalLimit(config.MaxConcurrentExtractions);
            await limit.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = config.CliPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add(imagePath);

                using var process = new Process { StartInfo = psi };
                process.Start();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.TimeoutSeconds)));

                string stdout;
                string stderr;
                try
                {
                    var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                    var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    stdout = await stdoutTask.ConfigureAwait(false);
                    stderr = await stderrTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // Process already exited.
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    _logger.LogWarning("ultrablur_cli timed out on {Path}", imagePath);
                    return null;
                }

                if (process.ExitCode != 0)
                {
                    _logger.LogWarning(
                        "ultrablur_cli exited with code {ExitCode} on {Path}. Stderr: {Stderr}",
                        process.ExitCode,
                        imagePath,
                        stderr.Trim());
                    return null;
                }

                var match = CliOutputRegex().Match(stdout);
                if (!match.Success)
                {
                    _logger.LogWarning("Unexpected ultrablur_cli output on {Path}: {Output}", imagePath, stdout.Trim());
                    return null;
                }

                return (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value);
            }
            finally
            {
                limit.Release();
            }
        }

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
