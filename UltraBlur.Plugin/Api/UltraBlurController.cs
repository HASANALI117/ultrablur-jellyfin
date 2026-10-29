using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using UltraBlur.Plugin.Services;

namespace UltraBlur.Plugin.Api
{
    /// <summary>
    /// REST endpoints serving UltraBlur corner colors to any Jellyfin client
    /// (web, Flutter/Plezy, Android, custom apps) plus the injected web client
    /// script. Color endpoints require standard Jellyfin auth; the script
    /// endpoint is anonymous because the &lt;script&gt; tag loads before login.
    /// </summary>
    [ApiController]
    [Route("UltraBlur")]
    [Produces("application/json")]
    public class UltraBlurController : ControllerBase
    {
        private readonly UltraBlurService _service;
        private readonly IProviderManager _providerManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="UltraBlurController"/> class.
        /// </summary>
        /// <param name="service">UltraBlur extraction service.</param>
        /// <param name="providerManager">The server's metadata/image provider manager.</param>
        public UltraBlurController(UltraBlurService service, IProviderManager providerManager)
        {
            _service = service;
            _providerManager = providerManager;
        }

        /// <summary>
        /// Gets the four UltraBlur corner colors for an item's artwork,
        /// computing and caching them on first request.
        /// </summary>
        /// <param name="itemId">The item ID.</param>
        /// <param name="imageType">Optional image type override (default: Backdrop, then Primary).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <response code="200">Colors returned.</response>
        /// <response code="404">Item not found or has no usable JPEG image.</response>
        /// <response code="422">Extraction failed.</response>
        /// <returns>The corner colors.</returns>
        [HttpGet("Colors/{itemId}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
        public async Task<ActionResult<UltraBlurColorsDto>> GetColors(
            [FromRoute] Guid itemId,
            [FromQuery] ImageType? imageType,
            CancellationToken cancellationToken)
        {
            var result = await _service.GetOrComputeAsync(itemId, imageType, cancellationToken)
                .ConfigureAwait(false);

            if (result.Colors == null)
            {
                return result.Error switch
                {
                    UltraBlurService.LookupError.ItemNotFound => NotFound("Item not found."),
                    UltraBlurService.LookupError.NoUsableImage => NotFound("Item has no usable JPEG image."),
                    _ => UnprocessableEntity("Color extraction failed."),
                };
            }

            var c = result.Colors;
            return Ok(new UltraBlurColorsDto
            {
                ItemId = c.ItemId,
                ImageType = c.ImageType,
                ImageTag = c.ImageTag,
                TopLeft = "#" + c.TopLeft,
                TopRight = "#" + c.TopRight,
                BottomRight = "#" + c.BottomRight,
                BottomLeft = "#" + c.BottomLeft,
                Cached = result.Cached,
            });
        }

        /// <summary>
        /// Gets the four UltraBlur corner colors for a TMDB image that is not in
        /// the library (e.g. a Seerr title's backdrop), computing and caching
        /// them on first request. Only https://image.tmdb.org/t/p/... is fetched.
        /// </summary>
        /// <param name="url">The TMDB image URL.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <response code="200">Colors returned.</response>
        /// <response code="400">URL is not a TMDB image URL.</response>
        /// <response code="404">Image could not be fetched or is not JPEG/PNG/WebP.</response>
        /// <response code="422">Extraction failed.</response>
        /// <returns>The corner colors.</returns>
        [HttpGet("Colors/Url")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
        public async Task<ActionResult<UltraBlurColorsDto>> GetColorsForUrl(
            [FromQuery] string url,
            CancellationToken cancellationToken)
        {
            if (!UltraBlurService.IsAllowedImageUrl(url, out var uri))
            {
                return BadRequest("Only https://image.tmdb.org/t/p/ image URLs are supported.");
            }

            var result = await _service.GetOrComputeForUrlAsync(uri, cancellationToken).ConfigureAwait(false);
            if (result.Colors == null)
            {
                return result.Error == UltraBlurService.LookupError.ExtractionFailed
                    ? UnprocessableEntity("Color extraction failed.")
                    : NotFound("Image could not be fetched or decoded.");
            }

            var c = result.Colors;
            return Ok(new UltraBlurColorsDto
            {
                ItemId = c.ItemId,
                ImageType = c.ImageType,
                ImageTag = c.ImageTag,
                TopLeft = "#" + c.TopLeft,
                TopRight = "#" + c.TopRight,
                BottomRight = "#" + c.BottomRight,
                BottomLeft = "#" + c.BottomLeft,
                Cached = result.Cached,
            });
        }

        /// <summary>
        /// Batch variant for native clients: returns colors for up to 100 items in
        /// one call (e.g. a Flutter/Android library view priming its detail pages).
        /// Items that fail resolve to null rather than failing the whole request.
        /// </summary>
        /// <param name="itemIds">Item IDs to fetch colors for (max 100).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <response code="200">Map of itemId to colors (null for unresolvable items).</response>
        /// <returns>Per-item colors keyed by item ID.</returns>
        [HttpPost("Colors")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<Dictionary<Guid, UltraBlurColorsDto?>>> GetColorsBatch(
            [FromBody] Guid[] itemIds,
            CancellationToken cancellationToken)
        {
            if (itemIds.Length > 100)
            {
                return BadRequest("At most 100 item IDs per request.");
            }

            var results = new Dictionary<Guid, UltraBlurColorsDto?>(itemIds.Length);
            foreach (var itemId in itemIds.Distinct())
            {
                var result = await _service.GetOrComputeAsync(itemId, null, cancellationToken)
                    .ConfigureAwait(false);
                results[itemId] = result.Colors == null ? null : new UltraBlurColorsDto
                {
                    ItemId = result.Colors.ItemId,
                    ImageType = result.Colors.ImageType,
                    ImageTag = result.Colors.ImageTag,
                    TopLeft = "#" + result.Colors.TopLeft,
                    TopRight = "#" + result.Colors.TopRight,
                    BottomRight = "#" + result.Colors.BottomRight,
                    BottomLeft = "#" + result.Colors.BottomLeft,
                    Cached = result.Cached,
                };
            }

            return Ok(results);
        }

        /// <summary>
        /// Gets a clear-logo URL for a title that need not be in the library,
        /// from the server's installed image providers (TMDB built in, Fanart
        /// when installed), by provider id. Nothing is stored.
        /// </summary>
        /// <param name="type">Movie or Series.</param>
        /// <param name="tmdb">TMDB id.</param>
        /// <param name="tvdb">TVDB id.</param>
        /// <param name="imdb">IMDb id.</param>
        /// <param name="language">Preferred language (ISO 639-1), default en.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <response code="200">Logo URL returned.</response>
        /// <response code="400">No usable id or unknown type.</response>
        /// <response code="404">No provider has a logo.</response>
        /// <returns>The logo URL.</returns>
        [HttpGet("Logo")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<UltraBlurLogoDto>> GetLogo(
            [FromQuery] string type,
            [FromQuery] string? tmdb,
            [FromQuery] string? tvdb,
            [FromQuery] string? imdb,
            [FromQuery] string? language,
            CancellationToken cancellationToken)
        {
            BaseItem? item = type switch
            {
                "Movie" => new Movie(),
                "Series" => new Series(),
                _ => null,
            };
            if (item == null)
            {
                return BadRequest("type must be Movie or Series.");
            }

            if (!string.IsNullOrWhiteSpace(tmdb))
            {
                item.SetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb, tmdb);
            }

            if (!string.IsNullOrWhiteSpace(tvdb))
            {
                item.SetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tvdb, tvdb);
            }

            if (!string.IsNullOrWhiteSpace(imdb))
            {
                item.SetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb, imdb);
            }

            if (item.ProviderIds.Count == 0)
            {
                return BadRequest("Pass tmdb, tvdb or imdb.");
            }

            var query = new RemoteImageQuery(string.Empty)
            {
                ImageType = ImageType.Logo,
                IncludeAllLanguages = true,
                IncludeDisabledProviders = true,
            };
            var images = await _providerManager.GetAvailableRemoteImages(item, query, cancellationToken)
                .ConfigureAwait(false);
            var lang = string.IsNullOrWhiteSpace(language) ? "en" : language;
            var best = images
                .Where(i => !string.IsNullOrEmpty(i.Url))
                .OrderBy(i => string.Equals(i.Language, lang, StringComparison.OrdinalIgnoreCase) ? 0 : string.IsNullOrEmpty(i.Language) ? 1 : 2)
                .ThenByDescending(i => i.CommunityRating ?? 0)
                .ThenByDescending(i => i.VoteCount ?? 0)
                .FirstOrDefault();
            if (best == null)
            {
                return NotFound("No provider has a logo.");
            }

            Response.Headers.CacheControl = "private, max-age=3600";
            return Ok(new UltraBlurLogoDto { Url = best.Url!, ProviderName = best.ProviderName ?? string.Empty });
        }

        /// <summary>
        /// Serves the embedded UltraBlur web client script. Anonymous: the injected
        /// &lt;script&gt; tag loads before login, and the script contains no secrets.
        /// </summary>
        /// <response code="200">The client script.</response>
        /// <returns>The JavaScript file.</returns>
        [HttpGet("ClientScript")]
        [Produces("application/javascript")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult GetClientScript()
        {
            var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("UltraBlur.Plugin.js.ultrablur.js");
            if (stream == null)
            {
                return NotFound();
            }

            return File(stream, "application/javascript");
        }
    }

    /// <summary>
    /// UltraBlur colors response payload.
    /// </summary>
    public class UltraBlurColorsDto
    {
        /// <summary>
        /// Gets or sets the item ID.
        /// </summary>
        public Guid ItemId { get; set; }

        /// <summary>
        /// Gets or sets the image type the colors were extracted from.
        /// </summary>
        public string ImageType { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the source image cache tag.
        /// </summary>
        public string ImageTag { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the top-left color ("#rrggbb").
        /// </summary>
        public string TopLeft { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the top-right color ("#rrggbb").
        /// </summary>
        public string TopRight { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the bottom-right color ("#rrggbb").
        /// </summary>
        public string BottomRight { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the bottom-left color ("#rrggbb").
        /// </summary>
        public string BottomLeft { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether the response was served from cache.
        /// </summary>
        public bool Cached { get; set; }
    }

    /// <summary>
    /// Clear-logo lookup response payload.
    /// </summary>
    public class UltraBlurLogoDto
    {
        /// <summary>
        /// Gets or sets the logo image URL (the provider's own CDN).
        /// </summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the image provider that supplied it.
        /// </summary>
        public string ProviderName { get; set; } = string.Empty;
    }
}