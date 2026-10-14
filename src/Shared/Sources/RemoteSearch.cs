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
public sealed record RemoteCandidate(string Source, string Id, string Title, int? Year, string? Format, string? AniDbId);

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

        var configuration = Plugin.Instance.Configuration;
        var http = Plugin.Instance.GetHttpClient();
        var title = query.Trim();

        foreach (var (source, prefix) in new[] { (SourceIds.AniList, "anilist"), (SourceIds.Mal, "mal") })
        {
            if (!configuration.GetSource(source).Enabled || (source == SourceIds.Mal && !MalClient.IsConfigured))
            {
                continue;
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

                continue;
            }

            foreach (var candidate in results)
            {
                var aniDbId = await AniBridgeMappings.ResolveSeriesIdByLink(Plugin.Instance.AppPaths, prefix, candidate.Id, logger, cancellationToken).ConfigureAwait(false);
                found.Add(candidate with { AniDbId = aniDbId });
            }
        }

        return found;
    }
}
