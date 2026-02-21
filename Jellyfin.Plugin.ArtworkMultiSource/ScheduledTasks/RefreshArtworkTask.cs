using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArtworkMultiSource.ScheduledTasks;

/// <summary>
/// Scheduled task that refreshes artwork (images) for supported items using the normal Jellyfin refresh pipeline,
/// which includes this plugin's image provider.
/// </summary>
public sealed class RefreshArtworkTask : IScheduledTask
{
    private const int PageSize = 200;

    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<RefreshArtworkTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshArtworkTask"/> class.
    /// </summary>
    public RefreshArtworkTask(ILibraryManager libraryManager, IFileSystem fileSystem, ILogger<RefreshArtworkTask> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Refresh artwork (Artwork Multi Source Provider)";

    /// <inheritdoc />
    public string Key => "ArtworkMultiSource_RefreshArtwork";

    /// <inheritdoc />
    public string Description => "Refreshes images (posters, logos) for Movies, Series and Seasons using the Artwork Multi Source.";

    /// <inheritdoc />
    public string Category => "Artwork Multi Source";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => Array.Empty<TaskTriggerInfo>();

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);

        // We only want to refresh images. Metadata should follow the default rules.
        // ImageRefreshMode is set to FullRefresh to force providers to run and to look for new images.
        // MetadataRefreshOptions expects an IDirectoryService instance (not IFileSystem).
        // DirectoryService is the standard Jellyfin implementation backed by IFileSystem.
        var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.Default,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllImages = true,
            EnableRemoteContentProbe = false,
            IsAutomated = true,
            ForceSave = false
        };

        var includeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season };

        // Get a total count (best effort) for progress reporting.
        int totalCount = 0;
        try
        {
            var countQuery = new InternalItemsQuery
            {
                IncludeItemTypes = includeItemTypes,
                Recursive = true,
                Limit = 1,
                StartIndex = 0,
                EnableTotalRecordCount = true
            };

            var countResult = _libraryManager.GetItemsResult(countQuery);
            totalCount = countResult?.TotalRecordCount ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to determine total item count for artwork refresh task.");
        }

        var processed = 0;
        var refreshed = 0;
        var skipped = 0;

        for (var startIndex = 0; ; startIndex += PageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = includeItemTypes,
                Recursive = true,
                StartIndex = startIndex,
                Limit = PageSize,
                IsVirtualItem = false
            };

            var items = _libraryManager.GetItemList(query).ToList();
            if (items.Count == 0)
            {
                break;
            }

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;

                try
                {
                    if (!HasAnyRelevantProviderId(item))
                    {
                        skipped++;
                        ReportProgress(progress, processed, totalCount);
                        continue;
                    }

                    _logger.LogInformation("[ArtworkMultiSource] Refreshing images for {Name} ({Id})", item.Name, item.Id);
                    await item.RefreshMetadata(refreshOptions, cancellationToken).ConfigureAwait(false);
                    refreshed++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ArtworkMultiSource] Failed to refresh images for {Name} ({Id})", item.Name, item.Id);
                }

                ReportProgress(progress, processed, totalCount);
            }

            if (items.Count < PageSize)
            {
                break;
            }
        }

        _logger.LogInformation("[ArtworkMultiSource] Artwork refresh task completed. Processed={Processed}, Refreshed={Refreshed}, Skipped={Skipped}", processed, refreshed, skipped);
        progress.Report(100);
    }

    private static bool HasAnyRelevantProviderId(BaseItem item)
    {
        // Movies/Series: require at least one of the main IDs.
        // Seasons: allow series IDs too (season-level IDs are not always present).

        if (item is Season season)
        {
            var series = season.Series;
            if (series == null)
            {
                return false;
            }

            var seriesTmdb = series.GetProviderId(MetadataProvider.Tmdb);
            var seriesTvdb = series.GetProviderId(MetadataProvider.Tvdb);
            var seasonTmdb = season.GetProviderId(MetadataProvider.Tmdb);
            var seasonTvdb = season.GetProviderId(MetadataProvider.Tvdb);

            return !string.IsNullOrWhiteSpace(seriesTmdb)
                   || !string.IsNullOrWhiteSpace(seriesTvdb)
                   || !string.IsNullOrWhiteSpace(seasonTmdb)
                   || !string.IsNullOrWhiteSpace(seasonTvdb);
        }

        var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
        var tvdb = item.GetProviderId(MetadataProvider.Tvdb);
        return !string.IsNullOrWhiteSpace(tmdb) || !string.IsNullOrWhiteSpace(tvdb);
    }

    private static void ReportProgress(IProgress<double> progress, int processed, int total)
    {
        if (total > 0)
        {
            var pct = (double)processed / total * 100.0;
            if (pct > 100) pct = 100;
            progress.Report(pct);
        }
        else
        {
            // Best-effort fallback: keep UI responsive without knowing the total.
            // This is intentionally capped < 100; the task reports 100 when finishing.
            var pct = Math.Min(99.0, processed / 10.0);
            progress.Report(pct);
        }
    }
}
