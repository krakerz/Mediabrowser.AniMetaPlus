using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Data;
using AniMetaPlus.Providers;
using AniMetaPlus.Providers.AniDB.Mapping;
using AniMetaPlus.Providers.AniDB.Metadata;
using AniMetaPlus.Sources;
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
/// <param name="Remaining">Items identified whose refresh would ask AniDB, left for later runs.</param>
/// <param name="AskingAniDb">Of the queued items, how many will ask AniDB.</param>
public sealed record ScanResult(int Identified, int Unmatched, int Remaining, int AskingAniDb);

/// <summary>
/// Goes through an anime library and identifies what it can from local data alone - the
/// mapping sources and the titles dump - before anything is asked of AniDB, and lists what it
/// cannot for mapping by hand. Everything identified is refreshed at once, except refreshes
/// that would ask AniDB for a new answer: those go a few per run, newest first, so that a large
/// library reaches AniDB slowly over many runs rather than all at once.
/// </summary>
public static class LibraryScan
{
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
        // An item that carries an AniDB id is identified, whatever the unmatched list says: a
        // listing left by an earlier run - one that took identified shows for unidentified ones
        // - is cleared here rather than waiting for that item's next refresh.
        foreach (var item in items.Where(i => i.ProviderIds.TryGetValue(ProviderNames.AniDb, out var id) && !string.IsNullOrEmpty(id)))
        {
            Queues.ClearUnmatched(ItemKeys.ForUnidentified(item.Kind, item.Path, item.Name));
        }

        var pending = items
            .Where(i => !i.ProviderIds.TryGetValue(ProviderNames.AniDb, out var id) || string.IsNullOrEmpty(id))
            .Where(i => db.Unmatched.FindById(ItemKeys.ForUnidentified(i.Kind, i.Path, i.Name)) is not { } u || u.PendingRefresh)
            .OrderByDescending(i => i.AddedUtc)
            .ToList();

        // As many as AniDB's burst, hour and day still allow; the rest wait for a later run.
        var aniDbLimit = SourceGate.RequestsLeft(SourceIds.AniDb);

        // The identified shows are checked first, from local data, and share the same limit.
        aniDbLimit = Math.Max(0, aniDbLimit - await IdCheck.Run(items, aniDbLimit, logger, cancellationToken).ConfigureAwait(false));
        var identified = 0;
        var askingAniDb = 0;
        var deferred = 0;
        var unmatched = 0;
        var processed = 0;

        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            processed++;
            progress?.Report(100d * processed / Math.Max(pending.Count, 1));

            var key = ItemKeys.ForUnidentified(item.Kind, item.Path, item.Name);
            var mapped = Queues.MappedAnimeId(key);
            string? animeId;

            if (AniDbSeriesProvider.LinkParts(mapped) is { } link)
            {
                // Mapped by hand to AniList or MyAnimeList: its own refresh fills it from there,
                // and it is sent on only once the mappings tie that entry to AniDB.
                animeId = await AniBridgeMappings.ResolveSeriesIdByLink(paths, link.Prefix, link.Id, logger, cancellationToken).ConfigureAwait(false);
                if (animeId is null)
                {
                    continue;
                }
            }
            else
            {
                animeId = mapped
                    ?? await FromMappings(item, logger, cancellationToken).ConfigureAwait(false)
                    ?? await FromDump(item, cancellationToken).ConfigureAwait(false);
            }

            // An item tied to AniList or MyAnimeList by id is filled from those by its refresh,
            // so it is not listed for mapping by hand.
            if (string.IsNullOrEmpty(animeId) && AniDbSeriesProvider.LinkFromIds(item.ProviderIds) is not null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(animeId))
            {
                await Queues.RecordUnmatched(key, item.Kind, item.Name, item.Year, item.Path, cancellationToken).ConfigureAwait(false);
                unmatched++;

                continue;
            }

            // A refresh that only reads cached or local data costs AniDB nothing, and is queued
            // whatever the limit. One that would ask AniDB waits for a later run once the limit is
            // reached; it is identified again then, which costs nothing either.
            var asks = AniDbSeriesProvider.WouldAskAniDb(paths, animeId);

            // While AniDB is banned, resting or capped, a show it has never answered for waits too,
            // rather than being filled with stand-in values that need replacing later.
            if ((asks && askingAniDb >= aniDbLimit) || AniDbSeriesProvider.ShouldWaitForAniDb(paths, animeId))
            {
                deferred++;

                continue;
            }

            // The refresh identifies it again, the same way plus the steps that ask AniDB, and
            // then reads it.
            if (refresher?.Refresh(item.Path, false) == true)
            {
                identified++;

                if (asks)
                {
                    askingAniDb++;
                }
            }
        }

        logger.LogInformation(
            "AniMeta+ library scan: {Identified} items identified from local data and queued for a refresh ({Cached} need nothing new from AniDB, {AskingAniDb} ask AniDB), {Unmatched} listed for mapping by hand, {Deferred} wait for AniDB in later runs (at most {Limit} per run)",
            identified,
            identified - askingAniDb,
            askingAniDb,
            unmatched,
            deferred,
            aniDbLimit);

        db.Checkpoint();
        progress?.Report(100);

        return new ScanResult(identified, unmatched, deferred, askingAniDb);
    }

    private static async Task<string?> FromMappings(ScanItem item, ILogger logger, CancellationToken cancellationToken)
    {
        item.ProviderIds.TryGetValue("Tvdb", out var tvdb);
        item.ProviderIds.TryGetValue("Tmdb", out var tmdb);

        if (!string.IsNullOrEmpty(tvdb) || !string.IsNullOrEmpty(tmdb))
        {
            var mapped = await AniDbMappings.ResolveSeriesId(Plugin.Instance.AppPaths, tvdb, tmdb, logger, cancellationToken).ConfigureAwait(false);
            if (mapped?.AnimeId is { } byShow)
            {
                return byShow;
            }
        }

        // An AniList or MyAnimeList id another plugin or an earlier refresh left on the item.
        return AniDbSeriesProvider.LinkParts(AniDbSeriesProvider.LinkFromIds(item.ProviderIds)) is { } link
            ? await AniBridgeMappings.ResolveSeriesIdByLink(Plugin.Instance.AppPaths, link.Prefix, link.Id, logger, cancellationToken).ConfigureAwait(false)
            : null;
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
