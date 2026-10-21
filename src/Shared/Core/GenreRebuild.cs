using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Api;
using AniMetaPlus.Configuration;
using AniMetaPlus.Data;
using AniMetaPlus.Host;
using AniMetaPlus.Providers;
using AniMetaPlus.Providers.AniDB;
using AniMetaPlus.Providers.AniDB.Metadata;
using AniMetaPlus.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Core;

/// <summary>
/// What is cached for one item, from which its genres and tags are built.
/// </summary>
/// <param name="AniDb">AniDB's genres and tags as a refresh leaves them on the item, or <c>null</c> without a cached document.</param>
/// <param name="AniList">AniList's cached answer.</param>
/// <param name="Mal">MyAnimeList's cached answer.</param>
internal sealed record GenreInputs(Series? AniDb, AnimeRecord? AniList, AnimeRecord? Mal);

/// <summary>
/// What the rebuild does with one item.
/// </summary>
internal enum GenreVerdict
{
    /// <summary>The item already has what the cache gives.</summary>
    Unchanged,

    /// <summary>The item's lists change.</summary>
    Update,

    /// <summary>The owner locked it, or no genres or tags are imported.</summary>
    Skipped,

    /// <summary>Nothing cached, or less than the item was built from.</summary>
    Missing
}

/// <summary>
/// The rebuild's verdict on one item.
/// </summary>
/// <param name="Verdict">What to do.</param>
/// <param name="Genres">The genres to write, or <c>null</c> to keep them.</param>
/// <param name="Tags">The tags to write, or <c>null</c> to keep them.</param>
/// <param name="Sources">The sources the lists were built from, in order.</param>
internal sealed record GenreDecision(GenreVerdict Verdict, string[]? Genres = null, string[]? Tags = null, IReadOnlyList<string>? Sources = null);

/// <summary>
/// Rebuilds every anime's genres and tags from the answers already cached on disk - AniDB's
/// series.xml, AniList's and MyAnimeList's records - the way a refresh would, without asking
/// AniDB for anything. Shows lost AniDB tags while AniDB was blocked, and a replace-all refresh
/// per show costs a request each.
/// </summary>
public static class GenreRebuild
{
    private static readonly Lock _lock = new();
    private static GenreRebuildStatus _status = new();

    /// <summary>
    /// Gets how the current or last run stands.
    /// </summary>
    public static GenreRebuildStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status.Copy();
            }
        }
    }

    /// <summary>
    /// Starts a run in the background, unless one is already going, and returns at once.
    /// </summary>
    /// <param name="library">The server's library.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The run's status.</returns>
    public static GenreRebuildStatus Start(HostLibrary library, ILogger logger)
    {
        if (!TryBegin(out var status))
        {
            return status;
        }

        _ = Task.Run(() => Execute(library, logger, null, CancellationToken.None));
        status.Started = true;

        return status;
    }

    /// <summary>
    /// Runs the rebuild and waits for it, unless a run is already going.
    /// </summary>
    /// <param name="library">The server's library.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="progress">Progress, from 0 to 100.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The run's status.</returns>
    public static async Task<GenreRebuildStatus> RunAsync(HostLibrary library, ILogger logger, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!TryBegin(out var status))
        {
            return status;
        }

        await Execute(library, logger, progress, cancellationToken).ConfigureAwait(false);

        return Status;
    }

    /// <summary>
    /// Works out what an item's genres and tags should be from what is cached, and whether to
    /// leave it alone.
    /// </summary>
    /// <param name="item">The item, with the genres and tags it has now.</param>
    /// <param name="provenance">Where its genres came from last time, if recorded.</param>
    /// <param name="inputs">What is cached.</param>
    /// <param name="configuration">The settings.</param>
    /// <returns>The verdict.</returns>
    internal static GenreDecision Decide(BaseItem item, ProvenanceRecord? provenance, GenreInputs inputs, PluginConfiguration configuration)
    {
        if ((!configuration.ImportGenres && !configuration.ImportTags) || provenance is { Locked: true } || HostLibrary.GenresLocked(item))
        {
            return new GenreDecision(GenreVerdict.Skipped);
        }

        var listed = (provenance?.Source ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var contributors = new List<AnimeRecord>();
        var sources = new List<string>();
        AnimeRecord? aniDbRecord = null;

        foreach (var source in Order(configuration, provenance))
        {
            AnimeRecord? record = null;
            var name = source;

            if (string.Equals(source, SourceIds.AniDb, StringComparison.OrdinalIgnoreCase))
            {
                name = SourceIds.AniDb;
                record = aniDbRecord = inputs.AniDb is { Genres.Length: > 0 } aniDb ? AniDbRecord(item, aniDb) : null;
            }
            else if (string.Equals(source, SourceIds.AniList, StringComparison.OrdinalIgnoreCase))
            {
                name = SourceIds.AniList;
                record = inputs.AniList;
            }
            else if (string.Equals(source, SourceIds.Mal, StringComparison.OrdinalIgnoreCase))
            {
                name = SourceIds.Mal;
                record = inputs.Mal;
            }
            else if (listed.Contains(source, StringComparer.OrdinalIgnoreCase))
            {
                // TVDB's and TMDB's genres come in too weak to count, so they change nothing; the
                // record keeps naming them as the refresh that listed them did.
                sources.Add(source);
                continue;
            }

            if (record != null && SeriesMerge.Has(DataKind.Genres, record))
            {
                contributors.Add(record);
                sources.Add(name);
            }
        }

        // Never a thinner list than the item was built from. Without a record of that, an AniDB
        // anime whose document is gone from the cache may still carry AniDB's tags from a refresh.
        if (listed.Any(s => IsReadable(s) && !sources.Contains(s, StringComparer.OrdinalIgnoreCase))
            || (provenance == null && inputs.AniDb == null && !string.IsNullOrEmpty(item.GetProviderId(ProviderNames.AniDb))
                && configuration.GetSource(SourceIds.AniDb).Enabled && Order(configuration, null).Contains(SourceIds.AniDb, StringComparer.OrdinalIgnoreCase)))
        {
            return new GenreDecision(GenreVerdict.Missing);
        }

        string[] genres;
        string[] tags;

        if (contributors.Count > 0)
        {
            var combined = new MetadataResult<Series>
            {
                Item = new Series { Genres = inputs.AniDb?.Genres ?? [], Tags = inputs.AniDb?.Tags ?? [] }
            };

            SeriesMerge.ApplyCombined(DataKind.Genres, combined, contributors, aniDbRecord, configuration);
            genres = combined.Item.Genres ?? [];
            tags = combined.Item.Tags ?? [];
        }
        else if (inputs.AniDb is { } only && (only.Genres.Length > 0 || only.Tags.Length > 0))
        {
            // What a refresh leaves on the item when no source has genres to merge: AniDB's own lists.
            genres = only.Genres;
            tags = only.Tags;
            sources.Clear();
        }
        else
        {
            return new GenreDecision(GenreVerdict.Missing);
        }

        var wantGenres = configuration.ImportGenres ? genres : null;
        var wantTags = configuration.ImportTags ? tags : null;
        var same = (wantGenres is null || (item.Genres ?? []).SequenceEqual(wantGenres, StringComparer.Ordinal))
            && (wantTags is null || new HashSet<string>(item.Tags ?? [], StringComparer.Ordinal).SetEquals(wantTags));

        return new GenreDecision(same ? GenreVerdict.Unchanged : GenreVerdict.Update, wantGenres, wantTags, sources);
    }

    /// <summary>
    /// Works out which lists of a season follow the series' change: a list equal to what the
    /// series had. An empty one stays empty, as seasons in plain mode carry no data.
    /// </summary>
    /// <param name="season">The season.</param>
    /// <param name="oldGenres">The series' genres before.</param>
    /// <param name="oldTags">The series' tags before.</param>
    /// <param name="newGenres">The genres written to the series, or <c>null</c> when not written.</param>
    /// <param name="newTags">The tags written to the series, or <c>null</c> when not written.</param>
    /// <returns>What to write to the season; <c>null</c> keeps a list.</returns>
    internal static (string[]? Genres, string[]? Tags) SeasonLists(BaseItem season, string[] oldGenres, string[] oldTags, string[]? newGenres, string[]? newTags)
    {
        var genres = season.Genres ?? [];
        var tags = season.Tags ?? [];

        return (
            newGenres != null && genres.SequenceEqual(oldGenres, StringComparer.Ordinal) ? newGenres : null,
            newTags != null && new HashSet<string>(tags, StringComparer.Ordinal).SetEquals(oldTags) ? newTags : null);
    }

    /// <summary>
    /// Gets the key an item's fields are recorded under: the AniDB key, or the link the series
    /// provider fills a show without an AniDB entry under.
    /// </summary>
    /// <param name="ids">The item's provider ids.</param>
    /// <returns>The key, or <c>null</c> when the item has none of the ids.</returns>
    internal static string? ItemKeyOf(IReadOnlyDictionary<string, string> ids)
        => ids.GetValueOrDefault(ProviderNames.AniDb) is { Length: > 0 } animeId
            ? ItemKeys.ForAnime(animeId)
            : AniDbSeriesProvider.LinksFromIds(ids).FirstOrDefault();

    /// <summary>
    /// The sources in the order the owner set for genres, the one the field is held to first.
    /// </summary>
    internal static List<string> Order(PluginConfiguration configuration, ProvenanceRecord? provenance)
    {
        var order = configuration.GetPriority(DataKind.Genres).Where(s => SourceIds.All.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();

        if (provenance is { PinnedSource.Length: > 0 } && order.Remove(provenance.PinnedSource))
        {
            order.Insert(0, provenance.PinnedSource);
        }

        return order;
    }

    // The sources whose answers are on disk to be read.
    private static bool IsReadable(string source)
        => string.Equals(source, SourceIds.AniDb, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, SourceIds.AniList, StringComparison.OrdinalIgnoreCase)
            || string.Equals(source, SourceIds.Mal, StringComparison.OrdinalIgnoreCase);

    private static AnimeRecord AniDbRecord(BaseItem item, Series aniDb)
    {
        var record = new AnimeRecord { Source = SourceIds.AniDb, SourceId = item.GetProviderId(ProviderNames.AniDb) ?? string.Empty };
        record.Genres.AddRange(aniDb.Genres.Select(g => new WeightedName { Name = g }));

        return record;
    }

    private static bool TryBegin(out GenreRebuildStatus status)
    {
        lock (_lock)
        {
            if (_status.Running)
            {
                status = _status.Copy();

                return false;
            }

            _status = new GenreRebuildStatus { Running = true, StartedUtc = DateTime.UtcNow };
            status = _status.Copy();

            return true;
        }
    }

    private static void Update(Action<GenreRebuildStatus> change)
    {
        lock (_lock)
        {
            change(_status);
        }
    }

    private static async Task Execute(HostLibrary library, ILogger logger, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var items = library.ListTitledItems();
            Update(s => s.Total = items.Count);

            for (var i = 0; i < items.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(100.0 * i / Math.Max(items.Count, 1));

                try
                {
                    await Process(items[i], library, logger, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One item's trouble does not stop the others.
                    logger.LogWarning(ex, "AniMeta+ could not rebuild the genres and tags of {Item}", items[i].Name);
                    Update(s => s.LastError = ex.Message);
                }
            }

            progress?.Report(100);
        }
        catch (OperationCanceledException)
        {
            Update(s => s.LastError = "Cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AniMeta+ stopped rebuilding genres and tags");
            Update(s => s.LastError = ex.Message);
        }
        finally
        {
            Update(s =>
            {
                s.Running = false;
                s.FinishedUtc = DateTime.UtcNow;
            });

            var done = Status;
            logger.LogInformation(
                "AniMeta+ genres and tags: {Updated} of {Checked} rebuilt, {Unchanged} unchanged, {Skipped} locked or off, {Missing} without cached data, {Requests} requests",
                done.Updated,
                done.Checked,
                done.Unchanged,
                done.Skipped,
                done.Missing,
                done.Requests);
        }
    }

    private static async Task Process(BaseItem item, HostLibrary library, ILogger logger, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance.Configuration;
        var db = AniMetaDb.Current;
        var key = ItemKeyOf(item.ProviderIds);
        var provenance = key == null ? null : db.Provenance.FindById(ProvenanceRecord.MakeId(key, nameof(DataKind.Genres)));

        Update(s => s.Checked++);

        // Before anything is asked: a locked item is left alone, and costs no request.
        if (Decide(item, provenance, new GenreInputs(null, null, null), configuration).Verdict == GenreVerdict.Skipped)
        {
            Update(s => s.Skipped++);

            return;
        }

        var inputs = await Gather(item, key, provenance, configuration, cancellationToken).ConfigureAwait(false);
        var decision = Decide(item, provenance, inputs, configuration);

        if (decision.Verdict is GenreVerdict.Skipped or GenreVerdict.Missing)
        {
            Update(s =>
            {
                if (decision.Verdict == GenreVerdict.Skipped)
                {
                    s.Skipped++;
                }
                else
                {
                    s.Missing++;
                }
            });

            return;
        }

        var sources = decision.Sources ?? [];

        if (decision.Verdict == GenreVerdict.Update)
        {
            var oldGenres = item.Genres ?? [];
            var oldTags = item.Tags ?? [];

            await library.SaveGenresAndTags(item, decision.Genres, decision.Tags, cancellationToken).ConfigureAwait(false);

            if (item is Series)
            {
                foreach (var season in library.SeasonsOf(item).Where(s => !HostLibrary.GenresLocked(s)))
                {
                    var (genres, tags) = SeasonLists(season, oldGenres, oldTags, decision.Genres, decision.Tags);
                    if (genres != null || tags != null)
                    {
                        await library.SaveGenresAndTags(season, genres, tags, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            Update(s => s.Updated++);
        }
        else
        {
            Update(s => s.Unchanged++);
        }

        if (key != null && sources.Count > 0)
        {
            Remember(db, key, item, provenance, sources, decision.Verdict == GenreVerdict.Update);
        }

        logger.LogDebug("AniMeta+ genres and tags of {Item}: {Verdict} from {Sources}", item.Name, decision.Verdict, string.Join(",", sources));
    }

    // Records what the lists were built from, settling a field that waited for a source which has
    // now answered. Left alone when nothing about it changed, so a daily run writes nothing.
    internal static void Remember(AniMetaDb db, string key, BaseItem item, ProvenanceRecord? provenance, IReadOnlyList<string> sources, bool written)
    {
        var joined = string.Join(",", sources);
        var waiting = provenance is { Provisional: true, PreferredSource.Length: > 0 } && !sources.Contains(provenance.PreferredSource, StringComparer.OrdinalIgnoreCase)
            ? provenance.PreferredSource
            : null;

        if (!written && provenance != null && string.Equals(provenance.Source, joined, StringComparison.Ordinal) && !(provenance.Provisional && waiting == null))
        {
            return;
        }

        var context = new FetchContext { ItemKey = key, ItemName = item.Name ?? string.Empty, ItemPath = item.Path ?? string.Empty };

        SeriesMerge.Record(db, ProvenanceRecord.MakeId(key, nameof(DataKind.Genres)), context, DataKind.Genres, joined, waiting, keepPin: provenance);
        db.Wrote();
    }

    // Reads what is cached. AniDB's answer is the cached series.xml, however old, and is never
    // asked for. AniList and MyAnimeList are asked for only where nothing at all is cached and
    // the item has their id, through the same gate as any lookup; never by name.
    private static async Task<GenreInputs> Gather(BaseItem item, string? key, ProvenanceRecord? provenance, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        var paths = Plugin.Instance.AppPaths;
        var ids = item.ProviderIds;
        var order = Order(configuration, provenance);

        Series? aniDb = null;
        if (ids.GetValueOrDefault(ProviderNames.AniDb) is { Length: > 0 } animeId && configuration.GetSource(SourceIds.AniDb).Enabled)
        {
            aniDb = await AniDbSeriesProvider.ReadCachedGenres(AniDbSeriesProvider.CachedSeriesDataPath(paths, animeId)).ConfigureAwait(false);
        }

        var aniListId = ids.GetValueOrDefault(SourceIds.AniList) ?? string.Empty;
        var malId = ids.GetValueOrDefault(SourceIds.Mal) ?? string.Empty;
        var ask = configuration.GenreRebuildAskOthers;

        FetchContext? context = null;

        FetchContext Context()
        {
            if (context == null)
            {
                context = new FetchContext
                {
                    ItemKey = key ?? string.Empty,
                    ItemName = item.Name ?? string.Empty,
                    ItemPath = item.Path ?? string.Empty,
                    AnimeId = ids.GetValueOrDefault(ProviderNames.AniDb),
                    Year = item.ProductionYear,
                    IsMovie = item is Movie
                };

                foreach (var (provider, id) in ids)
                {
                    context.Ids[provider] = id;
                }
            }

            return context;
        }

        async Task<AnimeRecord?> Ask(string source)
        {
            var before = Sent(source);
            var outcome = await SourceFetcher.Fetch(source, Context(), cancellationToken).ConfigureAwait(false);
            Update(s => s.Requests += Math.Max(Sent(source) - before, 0));

            return outcome is { State: SourceState.Ok, Record: { } record } ? record : null;
        }

        AnimeRecord? aniList = null;
        if (UsesCache(order, configuration, SourceIds.AniList))
        {
            var number = IsNumber(aniListId) ? aniListId : null;
            var byMal = IsNumber(malId) ? malId : null;

            aniList = number != null ? SourceCache.Read(SourceIds.AniList, "id-" + number, TimeSpan.MaxValue)
                : byMal != null ? SourceCache.Read(SourceIds.AniList, "mal-" + byMal, TimeSpan.MaxValue)
                : null;

            if (aniList == null && ask && (number != null || byMal != null))
            {
                aniList = await Ask(SourceIds.AniList).ConfigureAwait(false);
            }
        }

        // What AniList says the MyAnimeList id is stands in for an item that lacks it, as in a refresh.
        if (!IsNumber(malId) && aniList?.Ids.GetValueOrDefault(SourceIds.Mal) is { } learned && IsNumber(learned))
        {
            malId = learned;
        }

        AnimeRecord? mal = null;
        if (UsesCache(order, configuration, SourceIds.Mal) && IsNumber(malId))
        {
            mal = SourceCache.Read(SourceIds.Mal, "id-" + malId, TimeSpan.MaxValue);

            if (mal == null && ask && MalClient.IsConfigured)
            {
                Context().Ids[SourceIds.Mal] = malId;
                mal = await Ask(SourceIds.Mal).ConfigureAwait(false);
            }
        }

        return new GenreInputs(aniDb, aniList, mal);
    }

    // Whether the source takes part in the genres: in the order, and turned on.
    private static bool UsesCache(List<string> order, PluginConfiguration configuration, string source)
        => order.Contains(source, StringComparer.OrdinalIgnoreCase) && configuration.GetSource(source).Enabled;

    private static int Sent(string source)
        => SourceGate.Counts().TryGetValue(source, out var counts) ? counts.LastDay : 0;

    private static bool IsNumber(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);
}
