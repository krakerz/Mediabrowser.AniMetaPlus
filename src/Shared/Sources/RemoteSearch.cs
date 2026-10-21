using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Providers.AniDB.Mapping;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Sources;

/// <summary>
/// An anime AniList or MyAnimeList lists under a searched title.
/// </summary>
/// <param name="Source">The source id.</param>
/// <param name="Id">The anime's id there.</param>
/// <param name="Title">Its title there.</param>
/// <param name="Year">The year it started, when known.</param>
/// <param name="Format">TV, movie, OVA and the like, as the source names it.</param>
/// <param name="AniDbId">The AniDB entry the mappings link it to, when they do.</param>
/// <param name="ImageUrl">The poster the source shows for it, when it gave one.</param>
public sealed record RemoteCandidate(string Source, string Id, string Title, int? Year, string? Format, string? AniDbId, string? ImageUrl = null);

/// <summary>
/// Searches AniList and MyAnimeList by title, for an item the local data could not identify:
/// often one too new for AniDB, or one AniDB lists under a different name.
/// </summary>
internal static class RemoteSearch
{
    /// <summary>
    /// Searches every enabled source that can be asked.
    /// </summary>
    /// <param name="query">The title.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matches, AniList's first, each with the AniDB entry it maps to.</returns>
    public static async Task<IReadOnlyList<RemoteCandidate>> Search(string query, ILogger logger, CancellationToken cancellationToken)
    {
        var found = new List<RemoteCandidate>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return found;
        }

        foreach (var source in new[] { SourceIds.AniList, SourceIds.Mal })
        {
            found.AddRange(await SearchSource(source, query, logger, cancellationToken).ConfigureAwait(false));
        }

        return found;
    }

    /// <summary>
    /// Searches one source, if it is enabled and can be asked.
    /// </summary>
    /// <param name="source">AniList or MyAnimeList.</param>
    /// <param name="query">The title.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matches, each with the AniDB entry it maps to; none when the source is off or blocked.</returns>
    public static async Task<IReadOnlyList<RemoteCandidate>> SearchSource(string source, string query, ILogger logger, CancellationToken cancellationToken)
    {
        var found = new List<RemoteCandidate>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return found;
        }

        var configuration = Plugin.Instance.Configuration;
        var http = Plugin.Instance.GetHttpClient();
        var title = query.Trim();
        var prefix = source == SourceIds.AniList ? "anilist" : "mal";

        if (!configuration.GetSource(source).Enabled || (source == SourceIds.Mal && !MalClient.IsConfigured))
        {
            return found;
        }

        IReadOnlyList<RemoteCandidate> results;
        try
        {
            // Through the gate, so that it is paced and counted like any other request; keyed
            // by the query, so that one failed search blocks only itself.
            results = await SourceGate.Run(
                source,
                "search:" + title.ToLowerInvariant(),
                title,
                string.Empty,
                "search " + title,
                ct => source == SourceIds.AniList ? AniListClient.SearchMany(http, title, ct) : MalClient.SearchMany(http, title, ct),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SourceBlockedException ex)
        {
            logger.LogDebug("{Source} could not be searched for {Title}: {Message}", source, title, ex.Message);

            return found;
        }

        foreach (var candidate in results)
        {
            var aniDbId = await AniBridgeMappings.ResolveSeriesIdByLink(Plugin.Instance.AppPaths, prefix, candidate.Id, logger, cancellationToken).ConfigureAwait(false);
            found.Add(candidate with { AniDbId = aniDbId });
        }

        return found;
    }
}
