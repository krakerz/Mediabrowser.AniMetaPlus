using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Configuration;
using AniMetaPlus.Data;
using AniMetaPlus.Providers.AniDB.Metadata;
using AniMetaPlus.Sources;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Core;

/// <summary>
/// What one run of the queue processor did.
/// </summary>
/// <param name="Resumed">Items refreshed because the source they waited for answers again.</param>
/// <param name="Upgraded">Items refreshed so that a stand-in's values are replaced.</param>
/// <param name="Mapped">Items refreshed after being mapped by hand.</param>
/// <param name="Pruned">Lapsed blocks removed.</param>
public sealed record QueueRunResult(int Resumed, int Upgraded, int Mapped, int Pruned);

/// <summary>
/// Works through the queues: items waiting for a paused source, values a stand-in supplied that
/// the preferred source can now replace, and items the owner mapped by hand. Each is handed to
/// the server as a refresh; the refresh itself does the work.
/// </summary>
public static class QueueProcessor
{
    /// <summary>
    /// How many refreshes one run queues at most, so that a source coming back does not have a
    /// whole library sent at it at once.
    /// </summary>
    public const int MaxRefreshesPerRun = 100;

    // A stand-in whose source still has nothing for it would otherwise be refreshed every run,
    // forever; once tried, it waits this long before the next try.
    private static readonly TimeSpan _upgradeRetryGap = TimeSpan.FromHours(6);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _upgradeTried = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the queues once.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="progress">Progress, from 0 to 100.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was done.</returns>
    public static Task<QueueRunResult> Run(ILogger logger, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var refresher = HostServices.Refresher;
        var db = AniMetaDb.Current;
        var budget = MaxRefreshesPerRun;

        // Items going back to AniDB once it answers again are paced like the library scan: the
        // catch-up after a ban is exactly the burst that earns the next one.
        var aniDbBudget = SourceGate.RequestsLeft(SourceIds.AniDb);
        var refreshed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Refresh(string path, bool replaceAll, bool toAniDb = false)
        {
            if (budget <= 0 || (toAniDb && (aniDbBudget <= 0 || (AniDbSeriesProvider.IsHeldBack() && AniDbSeriesProvider.LeadsASingleSourceField()))) || refresher == null || string.IsNullOrEmpty(path))
            {
                return false;
            }

            // An item with several queued fields is refreshed once.
            if (refreshed.Contains(path))
            {
                return true;
            }

            if (refresher.Refresh(path, replaceAll))
            {
                refreshed.Add(path);
                budget--;
                if (toAniDb)
                {
                    aniDbBudget--;
                }

                return true;
            }

            return false;
        }

        // Waiting for a paused source.
        var resumed = 0;
        foreach (var waiting in db.Waiting.FindAll().OrderBy(w => w.AddedUtc).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (BlockStore.SourceBlock(waiting.Source) != null)
            {
                continue;
            }

            if (Refresh(waiting.ItemPath, false, waiting.Source == SourceIds.AniDb))
            {
                db.Waiting.Delete(waiting.Id);
                resumed++;
            }
        }

        progress?.Report(33);

        // Provisional values whose preferred source answers again. A source that replaces its
        // stand-in only after review is left to the review: the refresh that meets the source
        // again proposes it.
        var upgraded = 0;
        var configuration = Plugin.Instance.Configuration;
        // Stand-ins and reviews the owner's current source order no longer backs are settled first,
        // so none of them is refreshed, or sent to AniDB, for a value ranked lower than what it has.
        var (settled, dropped) = SeriesMerge.SettleOutdated(db, configuration);
        if (settled + dropped > 0)
        {
            logger.LogInformation("AniMeta+ settled {Settled} stand-in values and dropped {Dropped} reviews that the current source order no longer backs", settled, dropped);
        }

        foreach (var provenance in db.Provenance.Find(p => p.Provisional).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(provenance.PreferredSource)
                || BlockStore.SourceBlock(provenance.PreferredSource) != null
                || BlockStore.ItemBlock(provenance.PreferredSource, provenance.ItemKey) != null
                || (provenance.PreferredSource == SourceIds.AniDb && (AniDbSeriesProvider.GetRemainingBanTime() > TimeSpan.Zero || !AniDbSeriesProvider.IsClientUsable()))
                || (_upgradeTried.TryGetValue(provenance.ItemPath, out var tried) && DateTime.UtcNow - tried < _upgradeRetryGap))
            {
                continue;
            }

            // A paused source stood in for only where the item had nothing, and replaces that
            // stand-in like a switched one.
            var mode = configuration.GetSource(provenance.PreferredSource).Mode;
            if (mode is FallbackMode.SwitchUpgrade or FallbackMode.Pause || (mode == FallbackMode.SwitchReview && !db.Reviews.Exists(r => r.Id == provenance.Id)))
            {
                if (Refresh(provenance.ItemPath, false, provenance.PreferredSource == SourceIds.AniDb))
                {
                    _upgradeTried[provenance.ItemPath] = DateTime.UtcNow;
                    upgraded++;
                }
            }
        }

        progress?.Report(66);

        // Mapped by hand and waiting for the refresh that applies it.
        var mapped = 0;
        foreach (var unmatched in db.Unmatched.Find(u => u.PendingRefresh).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A newly mapped AniDB id sends the refresh to AniDB, so it is paced like the other AniDB refreshes.
            var mappedId = Queues.MappedAnimeId(unmatched.Id);
            var toAniDb = mappedId != null
                && AniDbSeriesProvider.LinkParts(mappedId) is null
                && AniDbSeriesProvider.NeedsAniDbAnswer(Plugin.Instance.AppPaths, mappedId);

            if (Refresh(unmatched.Path, true, toAniDb))
            {
                unmatched.PendingRefresh = false;
                db.Unmatched.Update(unmatched);
                mapped++;
            }
        }

        var pruned = BlockStore.Prune();
        db.Checkpoint();
        progress?.Report(100);

        if (resumed + upgraded + mapped > 0)
        {
            logger.LogInformation(
                "AniMeta+ queued {Resumed} items whose source answers again, {Upgraded} with values to replace, and {Mapped} mapped by hand",
                resumed,
                upgraded,
                mapped);
        }

        return Task.FromResult(new QueueRunResult(resumed, upgraded, mapped, pruned));
    }
}
