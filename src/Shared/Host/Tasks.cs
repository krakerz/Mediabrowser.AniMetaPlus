using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using AniMetaPlus.Data;
using AniMetaPlus.Logging;
using AniMetaPlus.Providers.AniDB.Metadata;
using AniMetaPlus.Titles;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Host;

/// <summary>
/// What the plugin's scheduled tasks share: the category, the trigger shape, and the server's
/// two ways of running a task.
/// </summary>
public abstract class AniMetaTask : IScheduledTask
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Key { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public string Category => "AniMeta+";

    /// <inheritdoc />
    public abstract IEnumerable<TaskTriggerInfo> GetDefaultTriggers();

#if EMBY
    /// <inheritdoc />
    public Task Execute(CancellationToken cancellationToken, IProgress<double> progress) => Run(progress, cancellationToken);
#else
    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) => Run(progress, cancellationToken);
#endif

    /// <summary>
    /// Runs the task.
    /// </summary>
    /// <param name="progress">Progress, from 0 to 100.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    protected abstract Task Run(IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>
    /// Builds a trigger that runs every so often.
    /// </summary>
    /// <param name="interval">How often.</param>
    /// <returns>The trigger.</returns>
    protected static TaskTriggerInfo Every(TimeSpan interval) => new()
    {
#if EMBY
        Type = TaskTriggerInfo.TriggerInterval,
#else
        Type = TaskTriggerInfoType.IntervalTrigger,
#endif
        IntervalTicks = interval.Ticks
    };
}

/// <summary>
/// Downloads a fresh AniDB titles dump when one is due. The only thing that ever downloads it.
/// </summary>
public class TitlesRefreshTask(PluginLog<TitlesRefreshTask> logger) : AniMetaTask
{
    /// <inheritdoc />
    public override string Name => "Refresh AniDB titles dump";

    /// <inheritdoc />
    public override string Key => "AniMetaPlusTitlesRefresh";

    /// <inheritdoc />
    public override string Description => "Downloads a new AniDB titles dump when yours is older than the set interval (at most every 3 days).";

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [Every(TimeSpan.FromHours(12))];

    /// <inheritdoc />
    protected override async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var result = await TitlesDump.Refresh(Plugin.Instance.AppPaths, Plugin.Instance.GetHttpClient(), force: false, logger, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("AniMeta+ titles dump: {Message}", result.Message);

        // Imported either way, so that a dump put in place by hand is picked up.
        await TitlesDump.EnsureImported(Plugin.Instance.AppPaths, logger, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}

/// <summary>
/// Works through the queues: items waiting for a paused source, stand-in values to replace, and
/// items mapped by hand.
/// </summary>
public class QueueTask(PluginLog<QueueTask> logger) : AniMetaTask
{
    /// <inheritdoc />
    public override string Name => "Process AniMeta+ queues";

    /// <inheritdoc />
    public override string Key => "AniMetaPlusQueues";

    /// <inheritdoc />
    public override string Description => "Refreshes items waiting for a source, items holding stand-in values, and items mapped by hand.";

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [Every(TimeSpan.FromMinutes(30))];

    /// <inheritdoc />
    protected override Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        => QueueProcessor.Run(logger, progress, cancellationToken);
}

/// <summary>
/// Identifies the anime libraries from local data first, and paces the refreshes that reach
/// AniDB.
/// </summary>
public class LibraryScanTask(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, PluginLog<LibraryScanTask> logger) : AniMetaTask
{
    /// <inheritdoc />
    public override string Name => "Identify anime libraries";

    /// <inheritdoc />
    public override string Key => "AniMetaPlusLibraryScan";

    /// <inheritdoc />
    public override string Description => "Identifies new anime from local data, lists the rest for mapping by hand, and fixes wrong ids on shows already identified. Stays within the AniDB limits.";

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [Every(TimeSpan.FromHours(1))];

    /// <inheritdoc />
    protected override Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var library = new HostLibrary(libraryManager, providerManager, fileSystem);
        HostServices.Refresher ??= library;
        HostServices.Images ??= library;

        return LibraryScan.Run(library.ListAnimeItems(), logger, progress, cancellationToken);
    }
}

/// <summary>
/// Fills missing posters from the AniDB answers already cached, without asking the AniDB API.
/// Manual only.
/// </summary>
public class CachedPosterTask(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, PluginLog<CachedPosterTask> logger) : AniMetaTask
{
    // Long enough for the server's queue to reach every item: posters download one per AniDB
    // request interval.
    private static readonly TimeSpan _cacheOnlyWindow = TimeSpan.FromHours(6);

    /// <inheritdoc />
    public override string Name => "Fill missing posters from cached AniDB answers";

    /// <inheritdoc />
    public override string Key => "AniMetaPlusCachedPosters";

    /// <inheritdoc />
    public override string Description => "Gives anime without a poster AniDB's poster from the cached answer. No AniDB API requests. Run by hand.";

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    /// <inheritdoc />
    protected override Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var library = new HostLibrary(libraryManager, providerManager, fileSystem);
        HostServices.Refresher ??= library;
        HostServices.Images ??= library;

        var paths = Plugin.Instance.AppPaths;
        var queued = 0;
        var skipped = 0;

        foreach (var (item, animeId) in library.ListAniDbItems())
        {
            if (HostLibrary.HasPoster(item))
            {
                continue;
            }

            if (!AniDbSeriesProvider.HasCachedSeriesData(paths, animeId))
            {
                skipped++;
                continue;
            }

            AniDbSeriesProvider.ReadCacheOnly(animeId, _cacheOnlyWindow);
            library.QueueMissingImagesRefresh(item);
            queued++;
        }

        logger.LogInformation("AniMeta+ cached posters: {Queued} items queued for an image refresh, {Skipped} without a cached AniDB answer skipped", queued, skipped);
        progress.Report(100);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Replaces every anime poster with AniDB's, named by the cached answer, without asking the
/// AniDB API. Manual only.
/// </summary>
public class AniDbPosterOverrideTask(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, PluginLog<AniDbPosterOverrideTask> logger) : AniMetaTask
{
    /// <inheritdoc />
    public override string Name => "Replace posters with AniDB's from cached answers";

    /// <inheritdoc />
    public override string Key => "AniMetaPlusOverridePosters";

    /// <inheritdoc />
    public override string Description => "Replaces every anime's poster with AniDB's from the cached answer, keeping the old one to revert on the Image overrides tab. No AniDB API requests. Run by hand.";

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    /// <inheritdoc />
    protected override async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var library = new HostLibrary(libraryManager, providerManager, fileSystem);
        HostServices.Refresher ??= library;
        HostServices.Images ??= library;

        var paths = Plugin.Instance.AppPaths;
        var backups = PosterBackups.Current;
        var items = library.ListAniDbItems();
        var replaced = 0;
        var skipped = 0;
        var unchanged = 0;
        var kept = 0;
        var failed = 0;

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100.0 * i / Math.Max(items.Count, 1));

            var (item, animeId) = items[i];
            var url = AniDbSeriesProvider.HasCachedSeriesData(paths, animeId)
                ? await ReadUrl(AniDbSeriesProvider.CachedSeriesDataPath(paths, animeId)).ConfigureAwait(false)
                : null;

            if (string.IsNullOrEmpty(url))
            {
                skipped++;
                continue;
            }

            var id = HostLibrary.ItemId(item);
            var record = backups.Find(id);

            // The owner chose the previous poster.
            if (record?.State == ImageBackupState.Reverted)
            {
                kept++;
                continue;
            }

            // Already AniDB's and untouched since: no request to the image server.
            if (PosterBackups.IsApplied(record, url, PosterBackups.Stamp(HostLibrary.PosterPath(item))))
            {
                unchanged++;
                continue;
            }

            try
            {
                await AniDbSeriesProvider.WaitForImageSlot(cancellationToken).ConfigureAwait(false);

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                HttpResponseMessage response;
                try
                {
                    response = await Plugin.Instance.GetHttpClient().GetAsync(url, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    AniDbSeriesProvider.LogImageRequest(url, null, started, ex is TaskCanceledException ? "Timeout" : "Unreachable");
                    throw;
                }

                using var owned = response;
                AniDbSeriesProvider.LogImageRequest(url, (int)response.StatusCode, started);
                response.EnsureSuccessStatusCode();

                // Buffered: the server's image saving may read the stream more than once.
                using var image = new MemoryStream();
                await response.Content.CopyToAsync(image, cancellationToken).ConfigureAwait(false);
                image.Position = 0;

                // Kept before the save, which deletes the previous file; a failed copy leaves the poster alone.
                var held = backups.Hold(record, id, HostLibrary.PosterPath(item));

                await library.SavePoster(item, image, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg", cancellationToken).ConfigureAwait(false);

                var saved = library.FindItem(id) ?? item;
                var (kind, seriesName) = HostLibrary.DescribeItem(saved);
                backups.Applied(id, saved.Name ?? string.Empty, seriesName, kind, url, PosterBackups.Stamp(HostLibrary.PosterPath(saved)), held);
                replaced++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // One item the server cannot save for must not end the run for the rest.
                failed++;
                logger.LogWarning(ex, "AniMeta+ could not replace the poster of {Name} ({Path}) from {Url}", item.Name, item.Path, url);
            }
        }

        logger.LogInformation("AniMeta+ AniDB posters: {Replaced} replaced, {Unchanged} already AniDB's, {Kept} kept by you, {Skipped} without a cached AniDB picture skipped, {Failed} failed", replaced, unchanged, kept, skipped, failed);
        progress.Report(100);
    }

    private static async Task<string?> ReadUrl(string seriesDataPath)
    {
        try
        {
            return await AniDbImageProvider.FindImageUrl(seriesDataPath).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Brings every anime's original title in line with the searchable-titles setting, straight in
/// the library: its other titles from the local dump joined in, or taken out again. Sends no
/// request and refreshes nothing, so it can run daily; a "Replace all metadata" refresh is the
/// only thing that rewrites original titles in between.
/// </summary>
public class AlternativeTitlesTask(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, PluginLog<AlternativeTitlesTask> logger) : AniMetaTask
{
    /// <inheritdoc />
    public override string Name => "Apply alternative titles to original titles";

    /// <inheritdoc />
    public override string Key => "AniMetaPlusAlternativeTitles";

    /// <inheritdoc />
    public override string Description => "Puts each anime's other titles into its original title so search finds them, from local and cached data. No requests.";

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [Every(TimeSpan.FromDays(1))];

    /// <inheritdoc />
    protected override async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var library = new HostLibrary(libraryManager, providerManager, fileSystem);
        HostServices.Refresher ??= library;
        HostServices.Images ??= library;

        var paths = Plugin.Instance.AppPaths;
        var searchable = Plugin.Instance.Configuration.SearchableAlternativeTitles;
        var items = library.ListTitledItems();
        var changed = 0;

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100.0 * i / Math.Max(items.Count, 1));

            var item = items[i];
            var wanted = AlternativeTitles.Wanted(item.OriginalTitle, item.Name, AlternativeTitles.Gather(paths, item.ProviderIds), searchable);
            if (string.Equals(wanted, item.OriginalTitle, StringComparison.Ordinal) || string.IsNullOrEmpty(wanted))
            {
                continue;
            }

            await library.SaveOriginalTitle(item, wanted, cancellationToken).ConfigureAwait(false);
            changed++;
        }

        logger.LogInformation("AniMeta+ alternative titles: {Changed} of {Count} original titles {Action}", changed, items.Count, searchable ? "now carry their alternative titles" : "set back to the plain original title");
        progress.Report(100);
    }
}


/// <summary>
/// Rebuilds every anime's genres and tags from the answers already cached, straight in the
/// library, for shows that lost them while a source was blocked. Never asks AniDB; MyAnimeList
/// and AniList are asked only for a show with their id and no cached answer, when the setting allows.
/// </summary>
public class GenreRebuildTask(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, PluginLog<GenreRebuildTask> logger) : AniMetaTask
{
    /// <inheritdoc />
    public override string Name => "Rebuild genres and tags from cached answers";

    /// <inheritdoc />
    public override string Key => "AniMetaPlusGenreRebuild";

    /// <inheritdoc />
    public override string Description => "Rebuilds each anime's genres and tags from the answers already cached. Never sends a request to AniDB. Needs the daily run turned on on the Sources tab.";

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [Every(TimeSpan.FromDays(1))];

    /// <inheritdoc />
    protected override async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!Plugin.Instance.Configuration.GenreRebuildDaily)
        {
            progress.Report(100);
            return;
        }

        var library = new HostLibrary(libraryManager, providerManager, fileSystem);
        HostServices.Refresher ??= library;
        HostServices.Images ??= library;

        await GenreRebuild.RunAsync(library, logger, progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}


/// <summary>
/// Groups anime into one collection per franchise, from the relations in the AniDB answers
/// already cached. Sends no request, and only ever changes the collections it made itself.
/// </summary>
public class FranchiseCollectionsTask(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ICollectionManager collectionManager, PluginLog<FranchiseCollectionsTask> logger) : AniMetaTask
{
    /// <inheritdoc />
    public override string Name => "Group anime franchises into collections";

    /// <inheritdoc />
    public override string Key => "AniMetaPlusFranchiseCollections";

    /// <inheritdoc />
    public override string Description => "Groups related anime (sequels, prequels, side stories) into one collection per franchise, from cached AniDB answers. No requests. Needs the franchise collections setting.";

    /// <inheritdoc />
    public override IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [Every(TimeSpan.FromDays(1))];

    /// <inheritdoc />
    protected override async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!Plugin.Instance.Configuration.FranchiseCollections)
        {
            progress.Report(100);
            return;
        }

        var library = new HostLibrary(libraryManager, providerManager, fileSystem, collectionManager);
        HostServices.Refresher ??= library;
        HostServices.Images ??= library;

        var paths = Plugin.Instance.AppPaths;
        var nodes = new Dictionary<string, FranchiseNode>();
        var itemIds = new Dictionary<string, List<string>>();

        foreach (var (item, animeId) in library.ListAniDbSeriesAndMovies())
        {
            var node = new FranchiseNode(item.Name ?? string.Empty, HostLibrary.Premiere(item));

            // Two items with one AniDB id count as one anime, named by the earlier.
            if (!nodes.TryGetValue(animeId, out var known) || (node.Premiere != null && (known.Premiere == null || node.Premiere < known.Premiere)))
            {
                nodes[animeId] = node;
            }

            if (!itemIds.TryGetValue(animeId, out var ids))
            {
                itemIds[animeId] = ids = [];
            }

            ids.Add(HostLibrary.ItemId(item));
        }

        // Cached answers only: nothing here can reach AniDB.
        var franchises = Franchises.Group(nodes, id =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            return AniDbSeriesProvider.HasCachedSeriesData(paths, id)
                ? Franchises.ReadRelations(AniDbSeriesProvider.CachedSeriesDataPath(paths, id))
                : null;
        });

        var targets = franchises
            .Select(f => new FranchiseTarget(f.Key, f.Name, f.AnimeIds.SelectMany(id => itemIds[id]).Distinct().ToList(), itemIds[EarliestOf(f.AnimeIds, nodes)][0]))
            .ToList();

        var db = AniMetaDb.Current;
        var result = await Franchises.Sync(
            library,
            targets,
            db.FranchiseCollections.FindAll().ToList(),
            record =>
            {
                db.FranchiseCollections.DeleteMany(r => r.CollectionId == record.CollectionId);
                db.FranchiseCollections.Upsert(record);
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation("AniMeta+ franchise collections: {Franchises} franchises found, {Created} collections created, {Updated} updated, {Deleted} deleted, {Added} items added, {Removed} removed, {Art} images copied", franchises.Count, result.Created, result.Updated, result.Deleted, result.Added, result.Removed, result.ArtCopied);
        progress.Report(100);
    }

    // The member that premiered first, which names the collection and lends it its art.
    private static string EarliestOf(IReadOnlyList<string> animeIds, IReadOnlyDictionary<string, FranchiseNode> nodes)
        => animeIds
            .OrderBy(id => nodes.TryGetValue(id, out var node) && node.Premiere is { } premiere ? premiere : DateTime.MaxValue)
            .ThenBy(id => long.TryParse(id, out var number) ? number : long.MaxValue)
            .First();
}
