using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Providers;
using AniMetaPlus.Providers.AniDB.Mapping;
using AniMetaPlus.Providers.AniDB.Metadata;
using AniMetaPlus.Sources;
using AniMetaPlus.Titles;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Core;

/// <summary>
/// What the check makes of a show's AniDB id.
/// </summary>
/// <param name="Fix">The AniDB id the show should have instead, or <c>null</c>.</param>
/// <param name="Suspect">Whether the id looks wrong without enough evidence to change it.</param>
public readonly record struct AniDbVerdict(string? Fix, bool Suspect);

/// <summary>
/// Checks the ids of the shows already identified, from local data only: the mapping sources
/// and the titles dump. A wrong AniDB id is corrected where two independent pieces of evidence
/// agree on another, and only logged where one does. AniList and MyAnimeList ids are corrected
/// to the ones the AniBridge mappings link to the AniDB entry. A corrected show is refreshed
/// with its metadata replaced, since the servers keep a title and overview an item already has.
/// </summary>
public static class IdCheck
{
    // A show checked under these ids is not checked again until one of them changes or the
    // server restarts: the evidence is local and does not change between runs.
    private static readonly ConcurrentDictionary<string, bool> _checked = new(StringComparer.Ordinal);

    /// <summary>
    /// Judges a show's AniDB id.
    /// </summary>
    /// <param name="current">The AniDB id the show has.</param>
    /// <param name="currentShow">The TVDB show the mappings file that entry under, if any.</param>
    /// <param name="itemTvdb">The TVDB id on the show, if any.</param>
    /// <param name="byShow">The AniDB entry the show's TVDB or TMDB id maps to, if any.</param>
    /// <param name="byFolder">The AniDB entry the folder name is exactly the title of, if any.</param>
    /// <returns>The verdict.</returns>
    public static AniDbVerdict Judge(string current, string? currentShow, string? itemTvdb, string? byShow, string? byFolder)
    {
        if (string.Equals(current, byShow, StringComparison.Ordinal) || string.Equals(current, byFolder, StringComparison.Ordinal))
        {
            return default;
        }

        // An entry of the same TVDB show: a later season kept in a folder of its own.
        if (!string.IsNullOrEmpty(currentShow) && string.Equals(currentShow, itemTvdb, StringComparison.Ordinal))
        {
            return default;
        }

        if (!string.IsNullOrEmpty(byShow) && string.Equals(byShow, byFolder, StringComparison.Ordinal))
        {
            return new AniDbVerdict(byShow, false);
        }

        return new AniDbVerdict(null, !string.IsNullOrEmpty(byShow) || !string.IsNullOrEmpty(byFolder));
    }

    /// <summary>
    /// Checks the identified shows and corrects what it can.
    /// </summary>
    /// <param name="items">The anime libraries' items.</param>
    /// <param name="aniDbLimit">How many refreshes that ask AniDB may still be queued this run.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many refreshes that ask AniDB were queued.</returns>
    public static async Task<int> Run(IReadOnlyList<ScanItem> items, int aniDbLimit, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        var refresher = HostServices.Refresher;
        if (refresher == null)
        {
            return 0;
        }

        var paths = Plugin.Instance.AppPaths;
        var shows = items
            .Where(i => i.Kind == "Series" && i.ProviderIds.TryGetValue(ProviderNames.AniDb, out var id) && !string.IsNullOrEmpty(id))
            .Where(i => !_checked.ContainsKey(CheckKey(i)))
            .Where(i => Queues.MappedAnimeId(ItemKeys.ForUnidentified(i.Kind, i.Path, i.Name)) == null)
            .ToList();

        if (shows.Count == 0)
        {
            return 0;
        }

        var folders = shows.Select(s => (Show: s, Name: Queues.StripNotes(FolderName(s)))).ToList();
        var byFolderName = await Equals_check.XmlFindExactIds(folders.Select(f => (f.Name, f.Show.Year)), cancellationToken).ConfigureAwait(false);

        int fixedAniDb = 0, fixedLinks = 0, suspects = 0, askingAniDb = 0, deferred = 0;

        foreach (var (show, folder) in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = show.ProviderIds[ProviderNames.AniDb];
            show.ProviderIds.TryGetValue("Tvdb", out var tvdb);
            show.ProviderIds.TryGetValue("Tmdb", out var tmdb);

            var byShow = !string.IsNullOrEmpty(tvdb) || !string.IsNullOrEmpty(tmdb)
                ? (await AniDbMappings.ResolveSeriesId(paths, tvdb, tmdb, logger, cancellationToken).ConfigureAwait(false))?.AnimeId
                : null;
            var byFolder = byFolderName.GetValueOrDefault(Equals_check.NameKey(folder, show.Year));
            var currentShow = await AniBridgeMappings.ResolveSeriesKey(paths, current, logger, cancellationToken).ConfigureAwait(false);

            var verdict = Judge(current, currentShow, tvdb, byShow, byFolder);
            var animeId = verdict.Fix ?? current;

            if (verdict.Suspect)
            {
                suspects++;
                logger.LogWarning(
                    "{Name} ({Path}) may have the wrong AniDB id: it has AniDB {Current} ({CurrentTitle}), its TVDB {Tvdb} / TMDB {Tmdb} maps to AniDB {ByShow} and its folder name is exactly the title of AniDB {ByFolder}. Left as it is; use Identify on the show if it is wrong",
                    show.Name,
                    show.Path,
                    current,
                    TitlesDump.MainTitle(paths, current),
                    tvdb ?? "-",
                    tmdb ?? "-",
                    byShow ?? "-",
                    byFolder ?? "-");
            }

            // The ids the show should carry: AniDB's, and what the mappings link that entry to.
            var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
            if (verdict.Fix != null)
            {
                wanted[ProviderNames.AniDb] = verdict.Fix;
            }

            foreach (var (sourceId, prefix) in new[] { (SourceIds.Mal, "mal"), (SourceIds.AniList, "anilist") })
            {
                var linked = await AniBridgeMappings.ResolveLink(paths, animeId, prefix, logger, cancellationToken).ConfigureAwait(false);
                var has = show.ProviderIds.GetValueOrDefault(sourceId);

                if (!string.IsNullOrEmpty(linked) && !string.Equals(linked, has, StringComparison.Ordinal))
                {
                    wanted[sourceId] = linked;
                }
                else if (string.IsNullOrEmpty(linked) && verdict.Fix != null && !string.IsNullOrEmpty(has))
                {
                    // The old id belongs to the wrong show; the refresh finds the right one.
                    wanted[sourceId] = string.Empty;
                }
            }

            if (wanted.Count == 0)
            {
                _checked[CheckKey(show)] = true;
                continue;
            }

            // The refresh that follows reads AniDB for the entry; one that would ask it waits for
            // a later run once this run's share is used, and is checked again then.
            var asks = AniDbSeriesProvider.WouldAskAniDb(paths, animeId);
            if ((asks && askingAniDb >= aniDbLimit) || AniDbSeriesProvider.ShouldWaitForAniDb(paths, animeId))
            {
                deferred++;
                continue;
            }

            if (!await refresher.SaveIds(show.Path, wanted, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            logger.LogInformation(
                "{Name} ({Path}) had {Old}; corrected to {New} from the mappings and the titles dump, and refreshed with its metadata replaced",
                show.Name,
                show.Path,
                AniDbSeriesProvider.IdSummary(show.ProviderIds, new Dictionary<string, string>()),
                string.Join(", ", wanted.Select(w => w.Key + " " + (w.Value.Length > 0 ? w.Value : "removed"))));

            if (verdict.Fix != null)
            {
                fixedAniDb++;
            }
            else
            {
                fixedLinks++;
            }

            if (refresher.Refresh(show.Path, true) && asks)
            {
                askingAniDb++;
            }
        }

        logger.LogInformation(
            "AniMeta+ id check: {Checked} shows checked, {FixedAniDb} wrong AniDB ids and {FixedLinks} wrong AniList or MyAnimeList ids corrected, {Suspects} AniDB ids that may be wrong logged, {Deferred} wait for AniDB in later runs",
            shows.Count,
            fixedAniDb,
            fixedLinks,
            suspects,
            deferred);

        return askingAniDb;
    }

    private static string FolderName(ScanItem item)
        => Path.GetFileName(item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static string CheckKey(ScanItem item)
        => string.Join("|", item.Path, item.ProviderIds.GetValueOrDefault(ProviderNames.AniDb), item.ProviderIds.GetValueOrDefault("Tvdb"), item.ProviderIds.GetValueOrDefault(SourceIds.Mal), item.ProviderIds.GetValueOrDefault(SourceIds.AniList));
}
