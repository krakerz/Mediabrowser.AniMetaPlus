using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Data;
using AniMetaPlus.Providers;
using AniMetaPlus.Providers.AniDB.Mapping;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Core;

/// <summary>
/// An item of an anime library, as the server describes it to the scan.
/// </summary>
/// <param name="Path">The folder of a series, or the file of a movie.</param>
/// <param name="Name">The name.</param>
/// <param name="Year">The year, when known.</param>
/// <param name="Kind">"Series" or "Movie".</param>
/// <param name="ProviderIds">The ids the item carries.</param>
/// <param name="AddedUtc">When it was added to the library.</param>
public sealed record ScanItem(string Path, string Name, int? Year, string Kind, IReadOnlyDictionary<string, string> ProviderIds, DateTime AddedUtc);

/// <summary>
/// What one run of the library scan did.
/// </summary>
/// <param name="Identified">Items identified without asking AniDB, and queued for a refresh.</param>
/// <param name="Unmatched">Items nothing identified, now listed for mapping by hand.</param>
/// <param name="Remaining">Items left for later runs.</param>
public sealed record ScanResult(int Identified, int Unmatched, int Remaining);

/// <summary>
/// Goes through an anime library and identifies what it can from local data alone - the
/// mapping sources and the titles dump - before anything is asked of AniDB. What it identifies
/// is refreshed a batch at a time, newest first, so that a large library reaches AniDB slowly
/// over many runs rather than all at once; what it cannot is listed for mapping by hand.
/// </summary>
public static class LibraryScan
{
    /// <summary>
    /// How many items one run sends for a refresh.
    /// </summary>
    public const int BatchSize = 25;

    /// <summary>
    /// Runs the scan once.
    /// </summary>
    /// <param name="items">The library's series and movies.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="progress">Progress, from 0 to 100.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was done.</returns>
    public static async Task<ScanResult> Run(IReadOnlyList<ScanItem> items, ILogger logger, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        var db = AniMetaDb.Current;
        var refresher = HostServices.Refresher;
        var paths = Plugin.Instance.AppPaths;

        // Items that already carry an AniDB id were identified by an earlier refresh, and items
        // already listed as unmatched wait for the owner, so neither is looked at again.
        var pending = items
            .Where(i => !i.ProviderIds.TryGetValue(ProviderNames.AniDb, out var id) || string.IsNullOrEmpty(id))
            .Where(i => db.Unmatched.FindById(ItemKeys.ForUnidentified(i.Kind, i.Path, i.Name)) is not { } u || u.PendingRefresh)
            .OrderByDescending(i => i.AddedUtc)
            .ToList();

        var identified = 0;
        var unmatched = 0;
        var processed = 0;

        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (identified >= BatchSize)
            {
                break;
            }

            processed++;
            progress?.Report(100d * processed / Math.Max(pending.Count, 1));

            var key = ItemKeys.ForUnidentified(item.Kind, item.Path, item.Name);
            var animeId = Queues.MappedAnimeId(key)
                ?? await FromMappings(item, logger, cancellationToken).ConfigureAwait(false)
                ?? await FromDump(item, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(animeId))
            {
                await Queues.RecordUnmatched(key, item.Kind, item.Name, item.Year, item.Path, cancellationToken).ConfigureAwait(false);
                unmatched++;

                continue;
            }

            // The refresh identifies it again, the same way plus the steps that ask AniDB, and
            // then reads it. Queued here only to pace it.
            if (refresher?.Refresh(item.Path, false) == true)
            {
                identified++;
            }
        }

        var remaining = pending.Count - processed;

        logger.LogInformation(
            "AniMeta+ library scan: {Identified} items identified from local data and queued, {Unmatched} listed for mapping by hand, {Remaining} left for later runs",
            identified,
            unmatched,
            remaining);

        db.Checkpoint();
        progress?.Report(100);

        return new ScanResult(identified, unmatched, remaining);
    }

    private static async Task<string?> FromMappings(ScanItem item, ILogger logger, CancellationToken cancellationToken)
    {
        item.ProviderIds.TryGetValue("Tvdb", out var tvdb);
        item.ProviderIds.TryGetValue("Tmdb", out var tmdb);

        if (string.IsNullOrEmpty(tvdb) && string.IsNullOrEmpty(tmdb))
        {
            return null;
        }

        var mapped = await AniDbMappings.ResolveSeriesId(Plugin.Instance.AppPaths, tvdb, tmdb, logger, cancellationToken).ConfigureAwait(false);

        return mapped?.AnimeId;
    }

    private static async Task<string?> FromDump(ScanItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Name))
        {
            return null;
        }

        var found = await Equals_check.XmlFindId(item.Name, item.Year, cancellationToken).ConfigureAwait(false);

        return string.IsNullOrEmpty(found) ? null : found;
    }
}
