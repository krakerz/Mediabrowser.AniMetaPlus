using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Data;
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
/// <param name="Reason">A short phrase for why, or <c>null</c> where the id fits.</param>
public readonly record struct AniDbVerdict(string? Fix, bool Suspect, string? Reason = null);

/// <summary>
/// Checks the ids of the shows already identified, from local data only: the mapping sources
/// and the titles dump. A wrong AniDB id is corrected where two independent pieces of evidence
/// agree on another, and only logged where one does. AniList and MyAnimeList ids are corrected
/// to the ones the AniBridge mappings link to the AniDB entry. A corrected show is refreshed
/// with its metadata replaced, since the servers keep a title and overview an item already has.
/// </summary>
public static class IdCheck
{
    /// <summary>
    /// The ids of a show the mapping page lists and records: AniDB, MyAnimeList, AniList, TVDB and TMDB.
    /// </summary>
    public static readonly string[] RecordedProviders = [ProviderNames.AniDb, SourceIds.Mal, SourceIds.AniList, "Tvdb", "Tmdb"];

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
    /// <param name="folderLikesShow">How alike the folder name and the closest title of <paramref name="byShow"/> are, 0 to 100.</param>
    /// <param name="folderLikesCurrent">How alike the folder name and the closest title of <paramref name="current"/> are, 0 to 100.</param>
    /// <param name="holdsSeasonOne">Whether the show's folder has a Season 01 folder in it.</param>
    /// <returns>The verdict.</returns>
    public static AniDbVerdict Judge(string current, string? currentShow, string? itemTvdb, string? byShow, string? byFolder, int folderLikesShow = 0, int folderLikesCurrent = 100, bool holdsSeasonOne = false)
    {
        if (string.Equals(current, byShow, StringComparison.Ordinal) || string.Equals(current, byFolder, StringComparison.Ordinal))
        {
            return default;
        }

        // Both say another entry. Ahead of the same-show rule below: a folder holding a whole
        // franchise, "Natsume Yuujinchou", identified as one of its specials is the same TVDB
        // show, and is still wrong.
        if (!string.IsNullOrEmpty(byShow) && string.Equals(byShow, byFolder, StringComparison.Ordinal))
        {
            return new AniDbVerdict(byShow, false, "TVDB id and folder name agree");
        }

        // An entry of the same TVDB show: a later season kept in a folder of its own - unless the
        // folder holds the show's Season 01, which only the whole show does. "Monogatari", with
        // Season 01 to 05, held as Monogatari Series: Second Season.
        if (!string.IsNullOrEmpty(currentShow) && string.Equals(currentShow, itemTvdb, StringComparison.Ordinal))
        {
            return holdsSeasonOne && !string.IsNullOrEmpty(byShow) ? new AniDbVerdict(byShow, false, "folder holds Season 01") : default;
        }

        // The TVDB id says another entry, and the folder name reads as that entry's title and not
        // as the current one's: "Nakanohito Genome" held as Utawarerumono, where TVDB says
        // "Naka no Hito Genome [Jikkyouchuu]".
        if (!string.IsNullOrEmpty(byShow) && string.IsNullOrEmpty(byFolder)
            && folderLikesShow >= LikeEnough && folderLikesCurrent <= folderLikesShow - LikeMargin)
        {
            return new AniDbVerdict(byShow, false, "folder name reads as the TVDB entry");
        }

        var suspect = !string.IsNullOrEmpty(byShow) || !string.IsNullOrEmpty(byFolder);
        return new AniDbVerdict(null, suspect, suspect ? "TVDB id and folder name disagree with it" : null);
    }

    /// <summary>
    /// Whether the check leaves a show alone because of what was decided about it: always for
    /// a show with its ids set by hand, and for one the owner confirmed while its ids are still
    /// the confirmed ones.
    /// </summary>
    /// <param name="record">The show's record, or <c>null</c>.</param>
    /// <param name="currentIds">The ids the show has now, by provider.</param>
    /// <returns><c>true</c> when the check skips the show.</returns>
    public static bool SkipsCheck(IdRecord? record, IReadOnlyDictionary<string, string> currentIds)
    {
        ArgumentNullException.ThrowIfNull(currentIds);

        if (record == null)
        {
            return false;
        }

        if (string.Equals(record.Status, IdRecord.Manual, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(record.Status, IdRecord.Verified, StringComparison.Ordinal)
            && new[] { ProviderNames.AniDb, SourceIds.Mal, SourceIds.AniList }.All(key =>
                string.Equals(IdOf(record.NewIds, key), IdOf(currentIds, key), StringComparison.Ordinal));
    }

    /// <summary>
    /// Picks the recorded providers' ids out of an item's ids, leaving out missing ones.
    /// </summary>
    /// <param name="ids">The item's ids.</param>
    /// <returns>The ids, by provider.</returns>
    public static Dictionary<string, string> RecordedIds(IReadOnlyDictionary<string, string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in RecordedProviders)
        {
            var id = IdOf(ids, key);
            if (id.Length > 0)
            {
                result[key] = id;
            }
        }

        return result;
    }

    // An id by provider, ignoring case in the key as the items' own id lists do.
    private static string IdOf(IEnumerable<KeyValuePair<string, string>> ids, string key)
        => ids.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase)).Value ?? string.Empty;

    /// <summary>
    /// How alike a folder name has to be to an entry's title to count as naming it.
    /// </summary>
    public const int LikeEnough = 85;

    /// <summary>
    /// How much more alike the folder name has to be to that entry than to the current one.
    /// </summary>
    public const int LikeMargin = 30;

    /// <summary>
    /// How alike a folder name is to the closest of an entry's titles, 0 to 100. A name the
    /// title begins with or holds whole counts as 90: folders are often named by the start of a
    /// long title ("Compass 2.0" for "#Compass 2.0 Animation Project") or its last part.
    /// </summary>
    /// <param name="folder">The folder name, without notes.</param>
    /// <param name="titles">The entry's titles.</param>
    /// <returns>The likeness.</returns>
    public static int Likeness(string folder, IEnumerable<string> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);

        var name = Compact(folder);
        var best = 0;

        foreach (var title in titles)
        {
            var other = Compact(title);
            if (name.Length == 0 || other.Length == 0)
            {
                continue;
            }

            var likeness = name == other ? 100
                : Math.Min(name.Length, other.Length) >= 6 && (other.Contains(name, StringComparison.Ordinal) || name.Contains(other, StringComparison.Ordinal)) ? 90
                : Queues.Similarity(folder, title);

            best = Math.Max(best, likeness);
        }

        return best;
    }

    private static string Compact(string value)
        => new(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>
    /// The year a show's folder name says, as "Ranma (1989)". Never the show's own year: that is
    /// the entry it has now, possibly the wrong one, and AniDB titles a later season by its year -
    /// "Kusuriya no Hitorigoto (2025)" - so the show's year found the wrong entry and took it for
    /// right. Nor a parent folder named by a year, which is how a library happens to be sorted.
    /// </summary>
    /// <param name="path">The show's folder.</param>
    /// <returns>The year, or <c>null</c>.</returns>
    public static int? FolderYear(string path)
    {
        var trimmed = (path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(trimmed), @"\(((?:19|20)[0-9]{2})\)");
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Checks the identified shows and corrects what it can.
    /// </summary>
    /// <param name="items">The anime libraries' items.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many of the refreshes queued will ask AniDB.</returns>
    public static async Task<int> Run(IReadOnlyList<ScanItem> items, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        var refresher = HostServices.Refresher;
        if (refresher == null)
        {
            return 0;
        }

        var paths = Plugin.Instance.AppPaths;
        var db = AniMetaDb.Current;
        var records = db.IdRecords.FindAll().ToDictionary(r => r.Id, StringComparer.Ordinal);
        var shows = items
            .Where(i => i.Kind == "Series" && i.ProviderIds.TryGetValue(ProviderNames.AniDb, out var id) && !string.IsNullOrEmpty(id))

            // What the owner set or confirmed is not the check's to decide.
            .Where(i => !SkipsCheck(records.GetValueOrDefault(i.Path), i.ProviderIds))
            .Where(i => !_checked.ContainsKey(CheckKey(i)))
            .Where(i => Queues.MappedAnimeId(ItemKeys.ForUnidentified(i.Kind, i.Path, i.Name)) == null)
            .ToList();

        if (shows.Count == 0)
        {
            return 0;
        }

        var folders = shows.Select(s => (Show: s, Name: Queues.StripNotes(FolderName(s)))).ToList();
        var byFolderName = await Equals_check.XmlFindExactIds(folders.Select(f => (f.Name, FolderYear(f.Show.Path))), cancellationToken).ConfigureAwait(false);

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
            var byFolder = byFolderName.GetValueOrDefault(Equals_check.NameKey(folder, FolderYear(show.Path)));
            var currentShow = await AniBridgeMappings.ResolveSeriesKey(paths, current, logger, cancellationToken).ConfigureAwait(false);

            // Only needed where the TVDB id alone disagrees.
            var likesShow = 0;
            var likesCurrent = 100;
            if (!string.IsNullOrEmpty(byShow) && byShow != current && string.IsNullOrEmpty(byFolder))
            {
                likesShow = Likeness(folder, TitlesDump.SearchTitles(paths, byShow).Select(t => t.Title));
                likesCurrent = Likeness(folder, TitlesDump.SearchTitles(paths, current).Select(t => t.Title));
            }

            var verdict = Judge(current, currentShow, tvdb, byShow, byFolder, likesShow, likesCurrent, HoldsSeasonOne(show.Path));
            var animeId = verdict.Fix ?? current;

            if (verdict.Suspect)
            {
                suspects++;
                RecordSuspect(db, records.GetValueOrDefault(show.Path), show, byShow, byFolder, verdict.Reason);
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

            var links = await LinkedIds(paths, animeId, logger, cancellationToken).ConfigureAwait(false);
            foreach (var sourceId in new[] { SourceIds.Mal, SourceIds.AniList })
            {
                var linked = links.GetValueOrDefault(sourceId);
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
                // A suspect noted earlier that the show no longer is, since its ids were fixed by other means.
                if (!verdict.Suspect && records.GetValueOrDefault(show.Path) is { Status: IdRecord.Suspect })
                {
                    db.IdRecords.Delete(show.Path);
                }

                _checked[CheckKey(show)] = true;
                continue;
            }

            // Not held back for AniDB's limits: the refresh fills what the other sources have now,
            // and a field only AniDB can fill takes a stand-in that the queue replaces once AniDB
            // answers, paced like any other request. Only where AniDB leads the title, overview or
            // dates and is held back does the show wait, rather than show a stand-in title.
            var asks = AniDbSeriesProvider.WouldAskAniDb(paths, animeId);
            if (AniDbSeriesProvider.ShouldWaitForAniDb(paths, animeId))
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

            RecordCorrection(db, records.GetValueOrDefault(show.Path), show, wanted, verdict.Fix != null ? verdict.Reason : "AniBridge link");

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

    /// <summary>
    /// Whether the owner set a show's ids by hand.
    /// </summary>
    /// <param name="path">The show's folder.</param>
    /// <returns><c>true</c> when the show has a manual record.</returns>
    public static bool IsManual(string? path)
        => !string.IsNullOrEmpty(path) && AniMetaDb.Current.IdRecords.FindById(path) is { Status: IdRecord.Manual };

    /// <summary>
    /// The ids a show has once the corrections are put on it: those it had, replaced by the
    /// corrections, and without those the corrections remove.
    /// </summary>
    /// <param name="old">The ids it had.</param>
    /// <param name="wanted">The corrections, by provider; an empty one removes that id.</param>
    /// <returns>The ids, by provider.</returns>
    public static Dictionary<string, string> CorrectedIds(IReadOnlyDictionary<string, string> old, IReadOnlyDictionary<string, string> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);

        var result = RecordedIds(old);
        foreach (var (provider, id) in wanted)
        {
            if (string.IsNullOrEmpty(id))
            {
                result.Remove(provider);
            }
            else
            {
                result[provider] = id;
            }
        }

        return result;
    }

    private static void RecordCorrection(AniMetaDb db, IdRecord? existing, ScanItem show, IReadOnlyDictionary<string, string> wanted, string? reason)
    {
        var now = DateTime.UtcNow;
        db.IdRecords.Upsert(new IdRecord
        {
            Id = show.Path,
            ItemName = show.Name,
            Status = IdRecord.Corrected,
            OldIds = RecordedIds(show.ProviderIds),
            NewIds = CorrectedIds(show.ProviderIds, wanted),
            Reason = reason ?? string.Empty,
            CreatedUtc = existing?.CreatedUtc ?? now,
            UpdatedUtc = now
        });
    }

    private static void RecordSuspect(AniMetaDb db, IdRecord? existing, ScanItem show, string? byShow, string? byFolder, string? reason)
    {
        // What the owner confirmed or set stands, whatever the check thinks of it.
        if (existing is { Status: IdRecord.Verified or IdRecord.Manual })
        {
            return;
        }

        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(byShow))
        {
            candidates["ByTvdb"] = byShow;
        }

        if (!string.IsNullOrEmpty(byFolder))
        {
            candidates["ByFolder"] = byFolder;
        }

        var now = DateTime.UtcNow;
        db.IdRecords.Upsert(new IdRecord
        {
            Id = show.Path,
            ItemName = show.Name,
            Status = IdRecord.Suspect,
            OldIds = RecordedIds(show.ProviderIds),
            NewIds = candidates,
            Reason = reason ?? string.Empty,
            CreatedUtc = existing?.CreatedUtc ?? now,
            UpdatedUtc = now
        });
    }

    /// <summary>
    /// Whether a show's folder has a folder for season 1 in it, "Season 01" or "Season 1".
    /// </summary>
    /// <param name="path">The show's folder.</param>
    /// <returns><c>true</c> when it has.</returns>
    internal static bool HoldsSeasonOne(string path)
    {
        try
        {
            return Directory.Exists(path) && Directory.EnumerateDirectories(path)
                .Select(Path.GetFileName)
                .Any(name => System.Text.RegularExpressions.Regex.IsMatch(name ?? string.Empty, @"^season\s*0*1$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The AniList and MyAnimeList ids an AniDB entry is, from the AniBridge mappings. Their
    /// MyAnimeList link is now and then another season's - Bungou Stray Dogs' first season linked
    /// to MyAnimeList's 2nd Season - so where AniList's own cached answer for the linked entry
    /// names a MyAnimeList id, that one is taken. Local and cached data only.
    /// </summary>
    /// <param name="paths">The server paths.</param>
    /// <param name="animeId">The AniDB id.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ids, by provider.</returns>
    public static async Task<IReadOnlyDictionary<string, string>> LinkedIds(MediaBrowser.Common.Configuration.IApplicationPaths paths, string? animeId, ILogger logger, CancellationToken cancellationToken)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(animeId))
        {
            return ids;
        }

        var aniList = await AniBridgeMappings.ResolveLink(paths, animeId, "anilist", logger, cancellationToken).ConfigureAwait(false);
        var mal = await AniBridgeMappings.ResolveLink(paths, animeId, "mal", logger, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(aniList))
        {
            ids[SourceIds.AniList] = aniList;

            var named = SourceCache.Read(SourceIds.AniList, "id-" + aniList, TimeSpan.MaxValue)?.Ids.GetValueOrDefault(SourceIds.Mal);
            if (!string.IsNullOrEmpty(named))
            {
                mal = named;
            }
        }

        if (!string.IsNullOrEmpty(mal))
        {
            ids[SourceIds.Mal] = mal;
        }

        return ids;
    }

    private static string FolderName(ScanItem item)
        => Path.GetFileName(item.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static string CheckKey(ScanItem item)
        => string.Join("|", item.Path, item.ProviderIds.GetValueOrDefault(ProviderNames.AniDb), item.ProviderIds.GetValueOrDefault("Tvdb"), item.ProviderIds.GetValueOrDefault(SourceIds.Mal), item.ProviderIds.GetValueOrDefault(SourceIds.AniList));
}
