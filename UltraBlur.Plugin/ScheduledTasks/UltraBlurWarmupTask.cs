using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using UltraBlur.Plugin.Services;

namespace UltraBlur.Plugin.ScheduledTasks
{
    /// <summary>
    /// Scheduled task that precomputes UltraBlur colors for library items so
    /// detail pages get instant cache hits.
    /// </summary>
    public class UltraBlurWarmupTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly UltraBlurService _service;
        private readonly ColorStore _store;
        private readonly ILogger<UltraBlurWarmupTask> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="UltraBlurWarmupTask"/> class.
        /// </summary>
        /// <param name="libraryManager">Jellyfin library manager.</param>
        /// <param name="service">UltraBlur extraction service.</param>
        /// <param name="store">Color cache store.</param>
        /// <param name="logger">Logger instance.</param>
        public UltraBlurWarmupTask(
            ILibraryManager libraryManager,
            UltraBlurService service,
            ColorStore store,
            ILogger<UltraBlurWarmupTask> logger)
        {
            _libraryManager = libraryManager;
            _service = service;
            _store = store;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "UltraBlur Color Warm-up";

        /// <inheritdoc />
        public string Key => "UltraBlurWarmupTask";

        /// <inheritdoc />
        public string Description => "Precomputes UltraBlur ambient corner colors for movies, series, seasons, episodes, and music albums.";

        /// <inheritdoc />
        public string Category => "UltraBlur";

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                // Run daily at 3 AM, matching the repo's other plugins.
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.DailyTrigger,
                    TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
                },
            };
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnableWarmupTask)
            {
                _logger.LogInformation("UltraBlur warm-up disabled in plugin settings");
                progress.Report(100.0);
                return;
            }

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.MusicAlbum },
                IsVirtualItem = false,
                Recursive = true,
                OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
                Limit = Math.Max(1, config.WarmupBatchSize),
            });

            // Skip items whose current image already has fresh colors.
            var pending = items.Where(item =>
            {
                var image = UltraBlurService.ResolveImage(item, null);
                return image != null && !_store.Has(item.Id, image.Value.Type.ToString(), image.Value.Tag);
            }).ToList();

            _logger.LogInformation("UltraBlur warm-up: {Count} items need extraction", pending.Count);

            if (pending.Count == 0)
            {
                progress.Report(100.0);
                return;
            }

            var completed = 0;
            var failed = 0;
            foreach (var item in pending)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("UltraBlur warm-up cancelled");
                    break;
                }

                var result = await _service.GetOrComputeAsync(item.Id, null, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Colors == null)
                {
                    failed++;
                }

                completed++;
                progress.Report((double)completed / pending.Count * 100.0);
            }

            _logger.LogInformation(
                "UltraBlur warm-up finished: {Completed} processed, {Failed} failed",
                completed,
                failed);
        }
    }
}
