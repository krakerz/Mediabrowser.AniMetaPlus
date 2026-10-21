#if EMBY
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using AniMetaPlus.Providers.AniDB.Mapping;
using AniMetaPlus.Sources;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMetaPlus.Providers.AniDB.Metadata;

/// <summary>
/// Emby's upcoming and missing episode lists, which it asks the library's first series fetcher
/// that can list a show's episodes.
/// </summary>
public partial class AniDbSeriesProvider : ISeriesMetadataProvider
{
    // Emby asks for every show that has not ended each time the page loads.
    private static readonly UpcomingEpisodes.Cache _upcoming = new();
    private static readonly TimeSpan _upcomingBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Lists the episodes of a show, aired or not, numbered the way the library numbers them.
    /// </summary>
    /// <param name="seriesInfo">The series lookup info.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The episodes; none when nothing is known of the show.</returns>
    public async Task<RemoteSearchResult[]> GetAllEpisodes(SeriesInfo seriesInfo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seriesInfo);

        var key = seriesInfo.ProviderIds.GetValueOrDefault(ProviderNames.AniDb)
            ?? ItemKeys.ForUnidentified("Series", seriesInfo.Path, seriesInfo.Name);

        if (_upcoming.TryGet(key, DateTime.UtcNow, out var kept))
        {
            return kept;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_upcomingBudget);

        RemoteSearchResult[] episodes;
        var lifetime = UpcomingEpisodes.AnswerLifetime;

        try
        {
            episodes = await ListEpisodes(seriesInfo, key, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger?.LogDebug(ex, "Could not list the episodes of {SeriesName}; trying again in {Minutes} minutes", seriesInfo.Name, UpcomingEpisodes.FailureLifetime.TotalMinutes);
            episodes = [];
            lifetime = UpcomingEpisodes.FailureLifetime;
        }

        _upcoming.Set(key, episodes, DateTime.UtcNow, lifetime);

        return episodes;
    }

    private async Task<RemoteSearchResult[]> ListEpisodes(SeriesInfo info, string key, CancellationToken cancellationToken)
    {
        var logger = Logger ?? (ILogger)NullLogger.Instance;
        var animeId = await ResolveAnimeIdOffline(info, key, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(animeId))
        {
            logger.LogDebug("{SeriesName} has no AniDB id yet, so no upcoming episodes are listed", info.Name);

            return [];
        }

        var fromTvdb = await FromTvdb(info, animeId, key, logger, cancellationToken).ConfigureAwait(false);
        if (fromTvdb is { Count: > 0 })
        {
            logger.LogDebug("Upcoming episodes of {SeriesName} (AniDB {AnimeId}): {Count} from TheTVDB", info.Name, animeId, fromTvdb.Count);

            return [.. fromTvdb];
        }

        var fromAniDb = await FromCachedAniDb(info, animeId).ConfigureAwait(false);
        logger.LogDebug("Upcoming episodes of {SeriesName} (AniDB {AnimeId}): {Count} from cached AniDB documents", info.Name, animeId, fromAniDb.Count);

        return [.. fromAniDb];
    }

    /// <summary>
    /// The AniDB id of the show from what is already known of it; nothing is asked of AniDB.
    /// </summary>
    private async Task<string?> ResolveAnimeIdOffline(SeriesInfo info, string key, CancellationToken cancellationToken)
    {
        var animeId = info.ProviderIds.GetValueOrDefault(ProviderNames.AniDb);

        if (string.IsNullOrEmpty(animeId))
        {
            animeId = Queues.MappedAnimeId(key);

            // Mapped to an AniList or MyAnimeList entry: no AniDB documents to list from.
            if (animeId is { } mapped && LinkParts(mapped) is not null)
            {
                animeId = null;
            }
        }

        if (string.IsNullOrEmpty(animeId))
        {
            var tvdb = info.ProviderIds.GetValueOrDefault(nameof(MetadataProvider.Tvdb));
            var tmdb = info.ProviderIds.GetValueOrDefault(nameof(MetadataProvider.Tmdb));

            if (!string.IsNullOrEmpty(tvdb) || !string.IsNullOrEmpty(tmdb))
            {
                var mapped = await AniDbMappings.ResolveSeriesId(_appPaths, tvdb, tmdb, Logger ?? (ILogger)NullLogger.Instance, cancellationToken).ConfigureAwait(false);
                animeId = mapped?.AnimeId;
            }
        }

        return animeId;
    }

    private async Task<IReadOnlyList<RemoteSearchResult>?> FromTvdb(SeriesInfo info, string animeId, string key, ILogger logger, CancellationToken cancellationToken)
    {
        if (!Plugin.Instance.Configuration.GetSource(SourceIds.Tvdb).Enabled)
        {
            return null;
        }

        var role = await AniDbMappings.ResolveRole(_appPaths, animeId, logger, cancellationToken).ConfigureAwait(false);

        // Absolute, movie and unknown roles have no numbering to carry TVDB's over to.
        if (role is not { Absolute: false, Movie: false, Season: >= 1 })
        {
            return null;
        }

        var tvdbId = info.ProviderIds.GetValueOrDefault(nameof(MetadataProvider.Tvdb));
        if (string.IsNullOrEmpty(tvdbId))
        {
            tvdbId = (await AniDbMappings.ResolveShowIds(_appPaths, animeId, logger, cancellationToken).ConfigureAwait(false)).Tvdb;
        }

        if (string.IsNullOrEmpty(tvdbId))
        {
            return null;
        }

        var lookup = new SeriesInfo
        {
            Name = info.Name,
            Year = info.Year,
            MetadataLanguage = info.MetadataLanguage,
            MetadataCountryCode = info.MetadataCountryCode
        };
        lookup.ProviderIds[nameof(MetadataProvider.Tvdb)] = tvdbId;

        try
        {
            var listed = await DelegatedProviders.GetAllEpisodes(SourceIds.Tvdb, lookup, key, info.Name ?? string.Empty, cancellationToken).ConfigureAwait(false);

            return listed is null ? null : UpcomingEpisodes.Renumber(listed, role);
        }
        catch (SourceBlockedException ex)
        {
            logger.LogDebug("TheTVDB could not list the episodes of {SeriesName}: {Message}", info.Name, ex.Message);

            return null;
        }
    }

    private async Task<IReadOnlyList<RemoteSearchResult>> FromCachedAniDb(SeriesInfo info, string animeId)
    {
        var directory = GetSeriesDataPath(_appPaths, animeId);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var cached = new List<UpcomingEpisodes.CachedEpisode>();

        foreach (var path in Directory.EnumerateFiles(directory, "episode-*.xml"))
        {
            // Only plain digits are regular episodes; the others carry a letter for their kind.
            var match = RegularEpisodeFile().Match(Path.GetFileName(path));
            if (!match.Success)
            {
                continue;
            }

            var episode = new Episode();
            await AniDbEpisodeProvider.ParseEpisodeXml(new FileInfo(path), episode, info.MetadataLanguage).ConfigureAwait(false);

            cached.Add(new UpcomingEpisodes.CachedEpisode(
                match.Groups[1].Value,
                episode.ProviderIds.GetValueOrDefault(ProviderNames.AniDb),
                episode.Name,
                episode.PremiereDate?.UtcDateTime));
        }

        return UpcomingEpisodes.FromAniDb(cached);
    }

    [GeneratedRegex(@"^episode-(\d+)\.xml$")]
    private static partial Regex RegularEpisodeFile();
}
#endif
