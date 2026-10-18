using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Configuration;
using AniMetaPlus.Core;
using AniMetaPlus.Logging;
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
                if (Plugin.Instance.Configuration.GetSource(SourceIds.AniDb).Mode == FallbackMode.Pause
                    && !(ex is AniDbUnavailableException { ClientRejected: false } && !AniDbSeriesProvider.IsClientUsable()))
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
                && !string.Equals(seasonId, seriesId, StringComparison.Ordinal))
            {
                result.Item.Name = seriesResult.Item.Name;
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
        var named = !string.IsNullOrWhiteSpace(found.Name)
            && !string.Equals(found.Name, serverName, StringComparison.OrdinalIgnoreCase);

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
