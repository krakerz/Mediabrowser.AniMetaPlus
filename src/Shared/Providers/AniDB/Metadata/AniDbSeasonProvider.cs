using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Configuration;
using AniMetaPlus.Core;
using AniMetaPlus.Logging;
using AniMetaPlus.Providers.AniDB.Mapping;
using AniMetaPlus.Sources;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Providers.AniDB.Metadata;

/// <summary>
/// The AniDB metadata provider for seasons.
/// </summary>
/// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
/// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
/// <param name="logger">Instance of the <see cref="ILogger{AniDbSeasonProvider}"/> interface.</param>
public class AniDbSeasonProvider(IApplicationPaths appPaths, ILibraryManager libraryManager, PluginLog<AniDbSeasonProvider> logger) : IRemoteMetadataProvider<Season, SeasonInfo>
{
    private readonly AniDbSeriesProvider _seriesProvider = new AniDbSeriesProvider(appPaths);
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly ILogger<AniDbSeasonProvider> _logger = logger;

    /// <inheritdoc />
    public string Name => ProviderNames.Display;

    /// <inheritdoc />
    public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
    {
        var result = await Fill(info, cancellationToken).ConfigureAwait(false);

        // Whatever the details mode took a name from, plain names are the setting's say.
        if (Plugin.Instance.Configuration.SeasonNamesMode == SeasonNamesMode.Plain)
        {
            result.Item.Name = PlainName(info.IndexNumber, info.Name);
        }

        // The season's own entry and what AniList and MyAnimeList are asked by, on one line.
        var seasonId = result.Item.ProviderIds.GetValueOrDefault(ProviderNames.AniDb) ?? info.ProviderIds.GetValueOrDefault(ProviderNames.AniDb);
        if (!string.IsNullOrEmpty(seasonId))
        {
            var ids = new Dictionary<string, string>(StringComparer.Ordinal) { [ProviderNames.AniDb] = seasonId };
            foreach (var (sourceId, prefix) in new[] { (SourceIds.Mal, "mal"), (SourceIds.AniList, "anilist") })
            {
                if (await AniBridgeMappings.ResolveLink(appPaths, seasonId, prefix, _logger, cancellationToken).ConfigureAwait(false) is { } linked)
                {
                    ids[sourceId] = linked;
                }
            }

            _logger.LogInformation(
                "Season {SeasonNumber} of AniDB series {SeriesId} ({Path}) has {Ids}",
                info.IndexNumber,
                info.SeriesProviderIds.GetValueOrDefault(ProviderNames.AniDb),
                info.Path,
                AniDbSeriesProvider.IdSummary(ids, new Dictionary<string, string>()));
        }

        return result;
    }

    private async Task<MetadataResult<Season>> Fill(SeasonInfo info, CancellationToken cancellationToken)
    {
        RefreshScope.Enter(info);
        var mode = Plugin.Instance.Configuration.SeasonDetailsMode;
        var result = new MetadataResult<Season>
        {
            HasMetadata = true,
            Item = new Season
            {
                // A name an earlier AniDB fill gave the season is not kept outside AniDB mode.
                Name = mode == SeasonDetailsMode.AniDb ? info.Name : PlainName(info.IndexNumber, info.Name),
                IndexNumber = info.IndexNumber
            }
        };

        var seriesId = info.SeriesProviderIds.GetValueOrDefault(ProviderNames.AniDb);
        var seasonId = info.ProviderIds.GetValueOrDefault(ProviderNames.AniDb);

        // Whether the season is the whole of the entry it comes from, or a run of episodes part
        // way into one. A long-running show is often a single AniDB entry that the season
        // numbering breaks into several seasons, and those seasons share none of its dates.
        var wholeEntry = true;

        if (string.IsNullOrEmpty(seasonId) && !string.IsNullOrEmpty(seriesId))
        {
            try
            {
                var segment = await AniDbSeasonResolver.ResolveSeason(appPaths, _libraryManager, seriesId, info.IndexNumber, _logger, cancellationToken).ConfigureAwait(false);

                seasonId = segment?.AnimeId;
                wholeEntry = segment?.FirstEpisodeInEntry <= 1;
            }
            catch (AniDbBannedException ex)
            {
                _logger.LogDebug(
                    "Season {SeasonNumber} of AniDB series {SeriesId} could not be identified because AniDB cannot be asked, for another {RetryAfter}",
                    info.IndexNumber,
                    seriesId,
                    ex.RetryAfter);

                // A paused AniDB keeps the season as it is until AniDB answers again.
                if (PausesFor(ex))
                {
                    throw;
                }
            }
        }

        if (string.IsNullOrEmpty(seasonId))
        {
            if (mode == SeasonDetailsMode.Delegated)
            {
                await FillFromDelegatedSources(result, info, seriesId, cancellationToken).ConfigureAwait(false);
            }
            else if (mode == SeasonDetailsMode.AniDb)
            {
                await FillFromDelegates(result, info, seriesId, cancellationToken).ConfigureAwait(false);
            }
            else if (mode == SeasonDetailsMode.Order)
            {
                await FillInOrder(result, info, seriesId, null, false, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        // Recorded so the episode and image providers see the season's own entry instead of
        // resolving it again.
        result.Item.ProviderIds[ProviderNames.AniDb] = seasonId;

        // The id is all the episodes need. Nothing is fetched for the season's own entry, which
        // would cost an AniDB request per season.
        if (mode == SeasonDetailsMode.Plain)
        {
            return result;
        }

        if (mode == SeasonDetailsMode.Delegated)
        {
            await FillFromDelegatedSources(result, info, seriesId, cancellationToken).ConfigureAwait(false);

            return result;
        }

        if (mode == SeasonDetailsMode.Order)
        {
            await FillInOrder(result, info, seriesId, seasonId, info.IndexNumber is not <= 0 && wholeEntry, cancellationToken).ConfigureAwait(false);

            return result;
        }

        _logger.LogDebug(
            "Season {SeasonNumber} of AniDB series {SeriesId} filled from anime {SeasonId}",
            info.IndexNumber,
            seriesId,
            seasonId);

        // Specials have no entry of their own, which is why the id above is the series'. The
        // episodes need it, but the season must not be filled from it: the specials did not
        // air on the series' dates, and are not named or rated as the series is. A season that
        // is one run of episodes out of a longer entry is the same case.
        if (info.IndexNumber <= 0 || !wholeEntry)
        {
            return result;
        }

        var seriesInfo = new SeriesInfo
        {
            MetadataLanguage = info.MetadataLanguage,
            MetadataCountryCode = info.MetadataCountryCode
        };

        seriesInfo.ProviderIds.Add(ProviderNames.AniDb, seasonId);

        var seriesResult = await _seriesProvider.GetMetadata(seriesInfo, cancellationToken).ConfigureAwait(false);
        if (seriesResult.HasMetadata)
        {
            // The first season is the series entry, so taking its title would name the season
            // after the show it sits under. A later season has a title of its own.
            if (Plugin.Instance.Configuration.UseAniDbSeasonNames
                && EntryName(seasonId, seriesId, seriesResult.Item.Name) is { } entryName)
            {
                result.Item.Name = entryName;
            }

            result.Item.Overview = seriesResult.Item.Overview;
            result.Item.PremiereDate = seriesResult.Item.PremiereDate;
            result.Item.ProductionYear = seriesResult.Item.ProductionYear;
            result.Item.EndDate = seriesResult.Item.EndDate;
            result.Item.CommunityRating = seriesResult.Item.CommunityRating;
            result.Item.Studios = seriesResult.Item.Studios;
            result.Item.Genres = seriesResult.Item.Genres;
            result.Item.Tags = seriesResult.Item.Tags;
        }

        if (string.IsNullOrEmpty(result.Item.Overview))
        {
            await FillFromDelegates(result, info, seriesId, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Fills the season's overview and dates from the first source in the overview order that
    /// has an overview for it, and names it after that source when the names setting allows.
    /// </summary>
    /// <param name="result">The season being filled.</param>
    /// <param name="info">The lookup info.</param>
    /// <param name="seriesId">The series' AniDB id.</param>
    /// <param name="seasonId">The season's own AniDB entry id, when it resolved.</param>
    /// <param name="ownEntry">Whether the season is the whole of that entry, so that the entry's own synopsis describes it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task FillInOrder(MetadataResult<Season> result, SeasonInfo info, string? seriesId, string? seasonId, bool ownEntry, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance.Configuration;
        var entryAsked = ownEntry && !string.IsNullOrEmpty(seasonId);

        var answer = await FirstAnswer(
            configuration.GetPriority(DataKind.Overview),
            source => AsksSource(source, configuration.GetSource(source).Enabled, entryAsked) && AsksEntryOf(source, info.IndexNumber),
            source => AskSource(source, info, seriesId, seasonId, cancellationToken)).ConfigureAwait(false);

        if (answer is null)
        {
            return;
        }

        _logger.LogDebug("Season {SeasonNumber} of AniDB series {SeriesId} filled from {Source}", info.IndexNumber, seriesId, answer.Source);

        result.Item.Overview = answer.Overview;
        result.Item.PremiereDate = answer.PremiereDate;
        result.Item.EndDate = answer.EndDate;
        result.Item.ProductionYear = answer.ProductionYear;
        result.Item.Name = ChooseName(configuration.SeasonNamesMode, info.IndexNumber, info.Name, answer.Name);
    }

    /// <summary>
    /// Asks one source for the season's overview, dates and name. A source that cannot answer
    /// is skipped, except an AniDB that is paused.
    /// </summary>
    private async Task<OrderAnswer?> AskSource(string source, SeasonInfo info, string? seriesId, string? seasonId, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance.Configuration;

        try
        {
            if (string.Equals(source, SourceIds.AniDb, StringComparison.OrdinalIgnoreCase))
            {
                var seriesInfo = new SeriesInfo
                {
                    MetadataLanguage = info.MetadataLanguage,
                    MetadataCountryCode = info.MetadataCountryCode
                };

                seriesInfo.ProviderIds.Add(ProviderNames.AniDb, seasonId!);

                var found = await _seriesProvider.GetMetadata(seriesInfo, cancellationToken).ConfigureAwait(false);
                if (!found.HasMetadata)
                {
                    return null;
                }

                return new OrderAnswer(
                    source,
                    configuration.UseAniDbSeasonNames ? EntryName(seasonId, seriesId, found.Item.Name) : null,
                    found.Item.Overview,
                    found.Item.PremiereDate?.UtcDateTime(),
                    found.Item.EndDate?.UtcDateTime(),
                    found.Item.ProductionYear);
            }

            if (string.Equals(source, SourceIds.AniList, StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, SourceIds.Mal, StringComparison.OrdinalIgnoreCase))
            {
                var context = new FetchContext
                {
                    ItemKey = ItemKeys.ForAnime(seasonId!),
                    ItemName = info.Name ?? string.Empty,
                    AnimeId = seasonId,
                    Year = info.Year,
                    Language = info.MetadataLanguage ?? "en"
                };

                foreach (var (provider, id) in info.ProviderIds)
                {
                    context.Ids[provider] = id;
                }

                await AddLinkedIds(appPaths, context, seasonId, cancellationToken).ConfigureAwait(false);

                var outcome = await SourceFetcher.Fetch(source, context, cancellationToken).ConfigureAwait(false);
                if (outcome.Record is not { } record)
                {
                    return null;
                }

                return new OrderAnswer(
                    source,
                    EntryName(seasonId, seriesId, record.Title(configuration.TitlePreference.ToString())),
                    record.Overview,
                    record.StartDate,
                    record.EndDate,
                    record.StartDate?.Year);
            }

            if (SourceIds.IsDelegated(source))
            {
                var found = await DelegatedProviders.GetMetadata<Season, SeasonInfo>(source, info, SeasonKey(info, seriesId), info.Name ?? SeasonKey(info, seriesId), "Season", cancellationToken).ConfigureAwait(false);
                if (found?.Item is not { } season)
                {
                    return null;
                }

                return new OrderAnswer(source, DelegatedName(season, info.Name), season.Overview, season.PremiereDate?.UtcDateTime(), season.EndDate?.UtcDateTime(), season.ProductionYear);
            }
        }
        catch (AniDbBannedException ex)
        {
            _logger.LogDebug("Season {SeasonNumber} could not be read from {Source}: {Message}", info.IndexNumber, source, ex.Message);

            if (PausesFor(ex))
            {
                throw;
            }
        }
        catch (SourceBlockedException ex)
        {
            _logger.LogDebug("Season {SeasonNumber} could not be read from {Source}: {Message}", info.IndexNumber, source, ex.Message);
        }
        catch (InvalidOperationException ex) when (!BlockStore.IsShutdown(ex))
        {
            // AniDB refused this one anime. Only its answer is missing, and the next source is asked.
            _logger.LogDebug("Season {SeasonNumber} could not be read from {Source}: {Message}", info.IndexNumber, source, ex.Message);
        }

        return null;
    }

    private static bool PausesFor(AniDbBannedException ex)
        => Plugin.Instance.Configuration.GetSource(SourceIds.AniDb).Mode == FallbackMode.Pause
            && !(ex is AniDbUnavailableException { ClientRejected: false } && !AniDbSeriesProvider.IsClientUsable());

    /// <summary>
    /// What a source answered for a season in the overview order.
    /// </summary>
    /// <param name="Source">The source id.</param>
    /// <param name="Name">The name the source has for the season, or <c>null</c> when it has none to offer.</param>
    /// <param name="Overview">The overview.</param>
    /// <param name="PremiereDate">The premiere date.</param>
    /// <param name="EndDate">The end date.</param>
    /// <param name="ProductionYear">The production year.</param>
    internal sealed record OrderAnswer(string Source, string? Name, string? Overview, DateTime? PremiereDate, DateTime? EndDate, int? ProductionYear);

    /// <summary>
    /// Asks the sources in order and stops at the first answer that has an overview.
    /// </summary>
    /// <param name="order">The source ids, best first.</param>
    /// <param name="asked">Whether a source is asked at all.</param>
    /// <param name="ask">Asks one source.</param>
    /// <returns>The first answer with an overview, or <c>null</c> when there is none.</returns>
    internal static async Task<OrderAnswer?> FirstAnswer(IEnumerable<string> order, Func<string, bool> asked, Func<string, Task<OrderAnswer?>> ask)
    {
        foreach (var source in order)
        {
            if (!asked(source))
            {
                continue;
            }

            if (await ask(source).ConfigureAwait(false) is { } answer && !string.IsNullOrWhiteSpace(answer.Overview))
            {
                return answer;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a source is asked about a season. An entry's own synopsis describes the season
    /// only when the season is the whole entry; anything else asks the server's providers alone.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="enabled">Whether the source is on.</param>
    /// <param name="ownEntry">Whether the season is the whole of its own AniDB entry.</param>
    /// <returns><c>true</c> when the source is asked.</returns>
    internal static bool AsksSource(string source, bool enabled, bool ownEntry)
        => enabled && (ownEntry || SourceIds.IsDelegated(source));

    /// <summary>
    /// Whether AniList or MyAnimeList is asked about a season's AniDB entry. Not for the
    /// specials: their entry is the show's own, and what those two have for it is the show's
    /// synopsis and poster, not the specials'.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <returns><c>true</c> when the source may be asked.</returns>
    internal static bool AsksEntryOf(string source, int? seasonNumber)
        => seasonNumber != 0
            || !(string.Equals(source, SourceIds.AniList, StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, SourceIds.Mal, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Puts the AniList and MyAnimeList ids the mappings link an AniDB entry to into a fetch,
    /// for a season or a whole show. That saves searching those two by AniDB's spelling of the
    /// title, a search that misses often enough to have filled the blocks list, and that can
    /// land on a sequel. Taken over any id already on the item: a
    /// season may carry the whole show's, and a show the id an earlier wrong search gave it.
    /// Where the mappings link none, the search still runs.
    /// </summary>
    /// <param name="paths">The server paths.</param>
    /// <param name="context">The fetch.</param>
    /// <param name="seasonId">The AniDB entry.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ids the mappings supplied, by provider.</returns>
    internal static async Task<IReadOnlyDictionary<string, string>> AddLinkedIds(IApplicationPaths paths, FetchContext context, string? seasonId, CancellationToken cancellationToken)
    {
        var logger = (ILogger?)AniDbSeriesProvider.Logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var supplied = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (sourceId, prefix) in new[] { (SourceIds.AniList, "anilist"), (SourceIds.Mal, "mal") })
        {
            var linked = await AniBridgeMappings.ResolveLink(paths, seasonId, prefix, logger, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(linked))
            {
                context.Ids[sourceId] = linked;
                supplied[sourceId] = linked;
            }
        }

        return supplied;
    }

    /// <summary>
    /// The name a season takes under the names setting.
    /// </summary>
    /// <param name="names">The names setting.</param>
    /// <param name="number">The season number.</param>
    /// <param name="serverName">The name the server gave the season.</param>
    /// <param name="sourceName">The name the source has for the season, if any.</param>
    /// <returns>The name.</returns>
    internal static string? ChooseName(SeasonNamesMode names, int? number, string? serverName, string? sourceName)
        => names == SeasonNamesMode.Source && !string.IsNullOrWhiteSpace(sourceName) ? sourceName : PlainName(number, serverName);

    /// <summary>
    /// The title of a season's entry as the season's name. The first season is the series entry,
    /// so its title would name the season after the show it sits under.
    /// </summary>
    /// <param name="seasonId">The season's AniDB id.</param>
    /// <param name="seriesId">The series' AniDB id.</param>
    /// <param name="title">The entry's title.</param>
    /// <returns>The name, or <c>null</c> when the entry is the series' own or has no title.</returns>
    internal static string? EntryName(string? seasonId, string? seriesId, string? title)
        => string.IsNullOrWhiteSpace(title) || string.Equals(seasonId, seriesId, StringComparison.Ordinal) ? null : title;

    /// <summary>
    /// A delegated provider's name for a season, when it is one of its own.
    /// </summary>
    /// <param name="found">The provider's season.</param>
    /// <param name="serverName">The name the server gave the season, which is not a name of its own.</param>
    /// <returns>The name, or <c>null</c>.</returns>
    internal static string? DelegatedName(Season found, string? serverName)
        => !string.IsNullOrWhiteSpace(found.Name) && !string.Equals(found.Name, serverName, StringComparison.OrdinalIgnoreCase) ? found.Name : null;

    /// <summary>
    /// Fills what AniDB left empty from the server's own TVDB or TMDB season provider, in the
    /// overview's source order. The library is numbered the way TVDB numbers it, so they are
    /// asked with the season as it stands.
    /// </summary>
    private async Task FillFromDelegates(MetadataResult<Season> result, SeasonInfo info, string? seriesId, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance.Configuration;
        var itemKey = SeasonKey(info, seriesId);

        foreach (var source in configuration.GetPriority(DataKind.Overview))
        {
            if (!SourceIds.IsDelegated(source) || !configuration.GetSource(source).Enabled)
            {
                continue;
            }

            try
            {
                var found = await DelegatedProviders.GetMetadata<Season, SeasonInfo>(source, info, itemKey, info.Name ?? itemKey, "Season", cancellationToken).ConfigureAwait(false);
                if (found?.Item is not { } season)
                {
                    continue;
                }

                result.Item.Overview ??= season.Overview;
                result.Item.PremiereDate ??= season.PremiereDate;
                result.Item.ProductionYear ??= season.ProductionYear;
                result.Item.EndDate ??= season.EndDate;

                if (!string.IsNullOrEmpty(result.Item.Overview))
                {
                    return;
                }
            }
            catch (SourceBlockedException ex)
            {
                _logger.LogDebug("Season {SeasonNumber} could not be read from {Source}: {Message}", info.IndexNumber, source, ex.Message);
            }
        }
    }

    /// <summary>
    /// Fills the season from the server's own TVDB or TMDB season provider, in the episodes'
    /// source order, taking everything from the first that answers with a name or an overview.
    /// </summary>
    private async Task FillFromDelegatedSources(MetadataResult<Season> result, SeasonInfo info, string? seriesId, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance.Configuration;
        var itemKey = SeasonKey(info, seriesId);

        foreach (var source in configuration.GetPriority(DataKind.Episodes))
        {
            if (!SourceIds.IsDelegated(source) || !configuration.GetSource(source).Enabled)
            {
                continue;
            }

            try
            {
                var found = await DelegatedProviders.GetMetadata<Season, SeasonInfo>(source, info, itemKey, info.Name ?? itemKey, "Season", cancellationToken).ConfigureAwait(false);
                if (found?.Item is { } season && ApplyDelegated(result.Item, season, info.Name))
                {
                    return;
                }
            }
            catch (SourceBlockedException ex)
            {
                _logger.LogDebug("Season {SeasonNumber} could not be read from {Source}: {Message}", info.IndexNumber, source, ex.Message);
            }
        }
    }

    /// <summary>
    /// Copies a delegated provider's answer onto the season when it has an overview or a name
    /// of its own.
    /// </summary>
    /// <param name="target">The season being filled.</param>
    /// <param name="found">The provider's season.</param>
    /// <param name="serverName">The name the server gave the season, which is not a name of its own.</param>
    /// <returns><c>true</c> when the answer was taken.</returns>
    internal static bool ApplyDelegated(Season target, Season found, string? serverName)
    {
        var named = DelegatedName(found, serverName) is not null;

        if (!named && string.IsNullOrEmpty(found.Overview))
        {
            return false;
        }

        if (named)
        {
            target.Name = found.Name;
        }

        target.Overview = found.Overview;
        target.PremiereDate = found.PremiereDate;
        target.EndDate = found.EndDate;
        target.ProductionYear = found.ProductionYear;

        return true;
    }

    /// <summary>
    /// The plain name of a season: "Specials" for season 0, "Season N" otherwise.
    /// </summary>
    /// <param name="number">The season number.</param>
    /// <param name="fallback">The name to keep when the season has no number.</param>
    /// <returns>The name.</returns>
    internal static string? PlainName(int? number, string? fallback) => number switch
    {
        null => fallback,
        0 => "Specials",
        _ => FormattableString.Invariant($"Season {number}")
    };

    private static string SeasonKey(SeasonInfo info, string? seriesId)
        => (string.IsNullOrEmpty(seriesId) ? ItemKeys.ForUnidentified("Series", null, info.Name) : ItemKeys.ForAnime(seriesId))
            + FormattableString.Invariant($":s{info.IndexNumber}");

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo searchInfo, CancellationToken cancellationToken)
    {
        var metadata = await GetMetadata(searchInfo, cancellationToken).ConfigureAwait(false);

        var list = new List<RemoteSearchResult>();

        if (metadata.HasMetadata)
        {
            var res = new RemoteSearchResult
            {
                Name = metadata.Item.Name,
                PremiereDate = metadata.Item.PremiereDate,
                ProductionYear = metadata.Item.ProductionYear,
                ProviderIds = metadata.Item.ProviderIds,
                SearchProviderName = Name
            };

            list.Add(res);
        }

        return list;
    }

    /// <inheritdoc />
    public Task<HostImageResponse> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
