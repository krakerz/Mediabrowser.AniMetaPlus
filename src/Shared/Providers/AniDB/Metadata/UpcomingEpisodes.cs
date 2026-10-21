using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AniMetaPlus.Providers.AniDB.Mapping;
using MediaBrowser.Model.Providers;

namespace AniMetaPlus.Providers.AniDB.Metadata;

/// <summary>
/// The pure part of listing a show's upcoming and missing episodes: putting another source's
/// numbering into the library's, and turning cached AniDB episodes into results.
/// </summary>
internal static class UpcomingEpisodes
{
    /// <summary>
    /// How long an answer is kept. The server asks for every show that has not ended each time
    /// its upcoming page loads.
    /// </summary>
    internal static readonly TimeSpan AnswerLifetime = TimeSpan.FromHours(6);

    /// <summary>
    /// How long a failure is kept, so that a source that is down is not asked on every page load.
    /// </summary>
    internal static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// One regular episode as the cached AniDB document of it reads.
    /// </summary>
    /// <param name="EpNo">The number AniDB gives the episode; only plain digits are regular episodes.</param>
    /// <param name="AniDbId">The episode's AniDB id, when known.</param>
    /// <param name="Name">The title in the configured language.</param>
    /// <param name="AirDate">The air date, or <c>null</c> where AniDB has none.</param>
    internal readonly record struct CachedEpisode(string? EpNo, string? AniDbId, string? Name, DateTime? AirDate);

    /// <summary>
    /// Puts a show's TVDB episodes into the numbering of the library item built on the AniDB
    /// entry, going by the role the mappings give that entry.
    /// </summary>
    /// <param name="episodes">The episodes TVDB lists for the show.</param>
    /// <param name="role">The entry's role.</param>
    /// <returns>The episodes as the library numbers them, or <c>null</c> where the role does not say how to renumber them.</returns>
    internal static IReadOnlyList<RemoteSearchResult>? Renumber(IEnumerable<RemoteSearchResult> episodes, MappedRole? role)
    {
        ArgumentNullException.ThrowIfNull(episodes);

        if (role is not { Absolute: false, Movie: false, Season: >= 1 })
        {
            return null;
        }

        var season = role.Season.Value;

        // The base show: the library's seasons follow TVDB's, specials included.
        if (season == 1 && role.EpisodeOffset == 0)
        {
            return episodes.Select(e => Copy(e, e.ParentIndexNumber, e.IndexNumber)).ToList();
        }

        // The item is that one entry kept as a show of its own: its episodes are the later part
        // of a TVDB season, counted from 1.
        return episodes
            .Where(e => e.ParentIndexNumber == season && e.IndexNumber > role.EpisodeOffset)
            .Select(e => Copy(e, 1, e.IndexNumber - role.EpisodeOffset))
            .ToList();
    }

    /// <summary>
    /// Turns cached AniDB episodes into results: the regular ones that have an air date, as
    /// season 1 of the entry.
    /// </summary>
    /// <param name="episodes">The cached episodes.</param>
    /// <returns>The results, in episode order.</returns>
    internal static IReadOnlyList<RemoteSearchResult> FromAniDb(IEnumerable<CachedEpisode> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);

        var found = new List<RemoteSearchResult>();

        foreach (var episode in episodes)
        {
            if (episode.AirDate is not { } date
                || !int.TryParse(episode.EpNo, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || number < 1)
            {
                continue;
            }

            var result = new RemoteSearchResult
            {
                IndexNumber = number,
                ParentIndexNumber = 1,
                Name = episode.Name,
                PremiereDate = date,
                SearchProviderName = ProviderNames.Display
            };

            if (!string.IsNullOrEmpty(episode.AniDbId))
            {
                result.ProviderIds[ProviderNames.AniDb] = episode.AniDbId;
            }

            found.Add(result);
        }

        return found.OrderBy(e => e.IndexNumber).ToList();
    }

    private static RemoteSearchResult Copy(RemoteSearchResult source, int? season, int? number)
    {
        var copy = new RemoteSearchResult
        {
            IndexNumber = number,
            ParentIndexNumber = season,
            Name = source.Name,
            PremiereDate = source.PremiereDate,
            ProductionYear = source.ProductionYear,
            SearchProviderName = ProviderNames.Display
        };

        if (source.ProviderIds != null)
        {
            foreach (var id in source.ProviderIds)
            {
                copy.ProviderIds[id.Key] = id.Value;
            }
        }

        return copy;
    }

    /// <summary>
    /// Answers kept in memory per show, each for a time.
    /// </summary>
    internal sealed class Cache
    {
        private readonly ConcurrentDictionary<string, (DateTime Until, RemoteSearchResult[] Episodes)> _entries = new();

        /// <summary>
        /// Looks an answer up.
        /// </summary>
        /// <param name="key">The show's key.</param>
        /// <param name="nowUtc">The time now.</param>
        /// <param name="episodes">The answer, when one is kept and has not expired.</param>
        /// <returns><c>true</c> when there is one.</returns>
        internal bool TryGet(string key, DateTime nowUtc, out RemoteSearchResult[] episodes)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.Until > nowUtc)
            {
                episodes = entry.Episodes;
                return true;
            }

            episodes = [];
            return false;
        }

        /// <summary>
        /// Keeps an answer.
        /// </summary>
        /// <param name="key">The show's key.</param>
        /// <param name="episodes">The answer.</param>
        /// <param name="nowUtc">The time now.</param>
        /// <param name="lifetime">How long to keep it.</param>
        internal void Set(string key, RemoteSearchResult[] episodes, DateTime nowUtc, TimeSpan lifetime)
            => _entries[key] = (nowUtc + lifetime, episodes);
    }
}
