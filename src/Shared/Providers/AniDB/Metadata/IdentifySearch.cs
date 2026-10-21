using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using AniMetaPlus.Providers.AniDB.Mapping;
using AniMetaPlus.Sources;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Providers.AniDB.Metadata;

/// <summary>
/// One line of an identify's result list, before it becomes the server's search result.
/// </summary>
internal sealed class IdentifyResult
{
    /// <summary>
    /// Gets or sets the AniDB id, or <c>null</c> for an anime AniDB does not list.
    /// </summary>
    public string? AnimeId { get; set; }

    /// <summary>
    /// Gets or sets the name shown.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the year it started, when a source gave one.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets the poster a source gave, or <c>null</c> for the "Local only" one.
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// Gets the ids the result carries besides AniDB's, by provider id key.
    /// </summary>
    public Dictionary<string, string> Ids { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets how the result got into the list, such as "title match 92%". Kept when a
    /// later online hit enriches it.
    /// </summary>
    public string? Match { get; set; }

    /// <summary>
    /// Gets or sets the format an AniList or MyAnimeList hit gave, such as "TV" or "Movie".
    /// </summary>
    public string? Format { get; set; }

    /// <summary>
    /// Gets or sets what the result is of its show, such as "base show" or "season 2".
    /// </summary>
    public string? Role { get; set; }
}

/// <summary>
/// Builds an identify's result list: the local AniDB title matches, enriched and extended with
/// what AniList, MyAnimeList, TheTVDB and TheMovieDb find under the same name. AniDB itself is
/// never asked.
/// </summary>
internal static partial class IdentifySearch
{
    /// <summary>
    /// The most results an identify offers.
    /// </summary>
    internal const int MaxResults = 25;

    /// <summary>
    /// The longest the online searches may take together; whatever finished by then is used.
    /// </summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan _posterTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets the provider id key an online source's ids are kept under.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <returns>The key.</returns>
    internal static string IdKey(string source) => source switch
    {
        SourceIds.Tvdb => nameof(MetadataProvider.Tvdb),
        SourceIds.Tmdb => nameof(MetadataProvider.Tmdb),
        _ => source
    };

    /// <summary>
    /// The text that marks a result AniDB does not list, which is what tells a pick of it from
    /// a pick of an AniDB entry once the server has copied the result's name onto the item.
    /// </summary>
    /// <param name="source">The source the anime was found at.</param>
    /// <returns>The marker, such as " [AniList, not on AniDB]".</returns>
    internal static string NotOnAniDbMarker(string source) => " [" + source + ", not on AniDB]";

    /// <summary>
    /// Takes the marker off a name.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The name without the marker, or <c>null</c> when it carried none.</returns>
    internal static string? WithoutMarker(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var match = MarkerRegex().Match(name);

        return match.Success ? name[..match.Index].TrimEnd() : null;
    }

    /// <summary>
    /// Says what a source's format is, as a label.
    /// </summary>
    /// <param name="format">The format as the source names it, such as "TV_SHORT" or "movie".</param>
    /// <returns>The label, such as "TV short", or <c>null</c> when the source gave none.</returns>
    internal static string? FormatLabel(string? format)
    {
        var words = format?.Replace('_', ' ').Trim();

        if (string.IsNullOrEmpty(words))
        {
            return null;
        }

        return words.ToUpperInvariant() switch
        {
            "TV" or "OVA" or "ONA" or "CM" or "PV" => words.ToUpperInvariant(),
            "TV SHORT" => "TV short",
            "TV SPECIAL" => "TV special",
            _ => char.ToUpperInvariant(words[0]) + words[1..].ToLowerInvariant()
        };
    }

    /// <summary>
    /// Says what an AniDB entry is of its show, from what the mappings hold about it.
    /// </summary>
    /// <param name="mapped">What the mappings say, or <c>null</c> when they say nothing.</param>
    /// <param name="format">The format an AniList or MyAnimeList hit gave, as <see cref="FormatLabel"/> writes it.</param>
    /// <returns>The label, or <c>null</c> when neither source knows.</returns>
    internal static string? RoleLabel(MappedRole? mapped, string? format)
    {
        if (mapped is not null)
        {
            if (mapped.Absolute)
            {
                return "base show (absolute)";
            }

            switch (mapped.Season)
            {
                case >= 1 when mapped.EpisodeOffset > 0:
                    return string.Create(CultureInfo.InvariantCulture, $"season {mapped.Season}, from ep {mapped.EpisodeOffset + 1}");
                case 1:
                    return "base show";
                case >= 2:
                    return string.Create(CultureInfo.InvariantCulture, $"season {mapped.Season}");
            }

            if (mapped.Movie)
            {
                return "movie";
            }

            if (mapped.Season == 0)
            {
                return "special";
            }
        }

        return format;
    }

    /// <summary>
    /// Lists the sites a result has an id for, in the order AniDB, AniList, MyAnimeList, TheTVDB, TheMovieDb.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <returns>The short names joined by a dot, or an empty string when it has none.</returns>
    internal static string SitesLine(IdentifyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sites = new List<string>();

        if (!string.IsNullOrEmpty(result.AnimeId))
        {
            sites.Add("AniDB");
        }

        foreach (var (key, name) in new[] { (SourceIds.AniList, "AniList"), (SourceIds.Mal, "MAL"), (IdKey(SourceIds.Tvdb), "TVDB"), (IdKey(SourceIds.Tmdb), "TMDB") })
        {
            if (result.Ids.TryGetValue(key, out var id) && !string.IsNullOrEmpty(id))
            {
                sites.Add(name);
            }
        }

        return string.Join(" · ", sites);
    }

    /// <summary>
    /// Merges the online hits into the local results, keyed by AniDB id.
    /// </summary>
    /// <remarks>
    /// A hit linked to an AniDB entry already listed enriches it (year, ids, and the poster when
    /// posters are wanted and it has none); one linked to an entry not listed adds that entry,
    /// named as the titles dump names it. An AniList or MyAnimeList hit with no AniDB entry
    /// becomes a result of its own and carries only its own id; a MyAnimeList hit repeating an
    /// AniList one (same title, same year) is dropped. TheTVDB and TheMovieDb hits with no AniDB
    /// entry are dropped. Order: the local results as they came, the entries added, then the
    /// ones AniDB does not list.
    /// </remarks>
    /// <param name="local">The local results, in order; enriched in place.</param>
    /// <param name="remote">The online hits.</param>
    /// <param name="remotePosters">Whether the posters the hits carry are used.</param>
    /// <param name="includeUnlinked">Whether hits with no AniDB entry are offered at all.</param>
    /// <param name="mainTitle">Gives an AniDB entry's main title from the titles dump.</param>
    /// <param name="cap">The most results returned.</param>
    /// <returns>The results.</returns>
    internal static IReadOnlyList<IdentifyResult> Merge(
        IReadOnlyList<IdentifyResult> local,
        IReadOnlyList<RemoteCandidate> remote,
        bool remotePosters,
        bool includeUnlinked,
        Func<string, string?> mainTitle,
        int cap = MaxResults)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(mainTitle);

        var byAnimeId = new Dictionary<string, IdentifyResult>(StringComparer.Ordinal);
        foreach (var result in local)
        {
            if (result.AnimeId is { Length: > 0 } id)
            {
                byAnimeId.TryAdd(id, result);
            }
        }

        var added = new List<IdentifyResult>();
        var unlinked = new List<(string Source, string Id, IdentifyResult Result)>();

        foreach (var hit in remote)
        {
            if (!string.IsNullOrEmpty(hit.AniDbId))
            {
                if (!byAnimeId.TryGetValue(hit.AniDbId, out var target))
                {
                    target = new IdentifyResult { AnimeId = hit.AniDbId, Name = mainTitle(hit.AniDbId) ?? hit.Title, Match = "via " + hit.Source };
                    byAnimeId[hit.AniDbId] = target;
                    added.Add(target);
                }

                Enrich(target, hit, remotePosters);

                continue;
            }

            // Only AniList and MyAnimeList entries can be identified without AniDB; the other two
            // sites are searched for the AniDB entries they file under their ids.
            if (!includeUnlinked || hit.Source is not (SourceIds.AniList or SourceIds.Mal))
            {
                continue;
            }

            var twin = unlinked.FirstOrDefault(u => (u.Source == hit.Source && u.Id == hit.Id)
                || (u.Source != hit.Source
                    && u.Result.Year == hit.Year
                    && string.Equals(WithoutMarker(u.Result.Name), hit.Title, StringComparison.OrdinalIgnoreCase)));

            if (twin.Result is { } existing)
            {
                if (remotePosters && existing.ImageUrl is null && !string.IsNullOrEmpty(hit.ImageUrl))
                {
                    existing.ImageUrl = hit.ImageUrl;
                }

                existing.Format ??= FormatLabel(hit.Format);

                continue;
            }

            var offered = new IdentifyResult
            {
                Name = hit.Title + NotOnAniDbMarker(hit.Source),
                Year = hit.Year,
                ImageUrl = remotePosters && !string.IsNullOrEmpty(hit.ImageUrl) ? hit.ImageUrl : null,
                Match = "not on AniDB",
                Format = FormatLabel(hit.Format)
            };
            offered.Ids[IdKey(hit.Source)] = hit.Id;
            unlinked.Add((hit.Source, hit.Id, offered));
        }

        return [.. local.Concat(added).Concat(unlinked.Select(u => u.Result)).Take(cap)];
    }

    /// <summary>
    /// Searches AniList, MyAnimeList, TheTVDB and TheMovieDb at once, for as long as the budget
    /// allows.
    /// </summary>
    /// <param name="name">The name searched for.</param>
    /// <param name="year">The year, when known.</param>
    /// <param name="movie">Whether a movie is searched for.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The hits of the searches that finished: AniList's first, then MyAnimeList's, TheTVDB's and TheMovieDb's.</returns>
    internal static async Task<IReadOnlyList<RemoteCandidate>> SearchOnline(string name, int? year, bool movie, ILogger logger, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        (string Source, Task<IReadOnlyList<RemoteCandidate>> Task)[] searches =
        [
            (SourceIds.AniList, Guarded(SourceIds.AniList, () => RemoteSearch.SearchSource(SourceIds.AniList, name, logger, stop.Token), logger)),
            (SourceIds.Mal, Guarded(SourceIds.Mal, () => RemoteSearch.SearchSource(SourceIds.Mal, name, logger, stop.Token), logger)),
            (SourceIds.Tvdb, Guarded(SourceIds.Tvdb, () => DelegatedProviders.Search(SourceIds.Tvdb, name, year, movie, logger, stop.Token), logger)),
            (SourceIds.Tmdb, Guarded(SourceIds.Tmdb, () => DelegatedProviders.Search(SourceIds.Tmdb, name, year, movie, logger, stop.Token), logger))
        ];

        await Task.WhenAny(Task.WhenAll(searches.Select(s => s.Task)), Task.Delay(Budget, cancellationToken)).ConfigureAwait(false);

        // Stops whatever is still going, which would otherwise keep a gate slot after the answer.
        await stop.CancelAsync().ConfigureAwait(false);

        var found = new List<RemoteCandidate>();
        var cutOff = new List<string>();

        foreach (var (source, task) in searches)
        {
            if (task.IsCompletedSuccessfully)
            {
                found.AddRange(task.Result);
            }
            else
            {
                cutOff.Add(source);
            }
        }

        if (cutOff.Count > 0)
        {
            logger.LogInformation("The identify search for {Name} stopped waiting for {Sources} after {Seconds} seconds and used what the others found", name, string.Join(", ", cutOff), Budget.TotalSeconds);
        }

        return found;
    }

    /// <summary>
    /// Downloads a poster an online source gave, for an identify result. Never used for AniDB's
    /// images, which have their own paced path.
    /// </summary>
    /// <param name="url">The poster's url.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The server's image response.</returns>
    /// <exception cref="ArgumentException">The url is not an http or https address on a source's image host.</exception>
    internal static async Task<HostImageResponse> GetRemotePoster(string url, CancellationToken cancellationToken)
    {
        // Only the sources' own image hosts: the server's image endpoint must not fetch any address it is handed.
        if (!IsPosterUrl(url, out var uri))
        {
            throw new ArgumentException("Not an image address.", nameof(url));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_posterTimeout);

        return await Plugin.Instance.GetImageResponse(uri.AbsoluteUri, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a url is one of AniDB's own, which the AniDB image path serves.
    /// </summary>
    /// <param name="url">The url.</param>
    /// <returns><c>true</c> for an AniDB address.</returns>
    internal static bool IsAniDbUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && IsHostIn(uri.Host, "anidb.net");

    /// <summary>
    /// Whether a url is an http or https address on one of the sources' image hosts.
    /// </summary>
    /// <param name="url">The url.</param>
    /// <param name="uri">The address, when it is one.</param>
    /// <returns><c>true</c> for a poster address that may be fetched.</returns>
    internal static bool IsPosterUrl(string? url, [NotNullWhen(true)] out Uri? uri)
        => Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme is ("http" or "https") && IsPosterHost(uri.Host);

    /// <summary>
    /// Whether a host serves AniList's, MyAnimeList's, TheTVDB's or TheMovieDb's images.
    /// </summary>
    /// <param name="host">The host name.</param>
    /// <returns><c>true</c> for one of those.</returns>
    internal static bool IsPosterHost(string host)
        => IsHostIn(host, "anilist.co") || IsHostIn(host, "anili.st") || IsHostIn(host, "myanimelist.net")
            || IsHostIn(host, "thetvdb.com") || IsHostIn(host, "tmdb.org");

    private static bool IsHostIn(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private static void Enrich(IdentifyResult target, RemoteCandidate hit, bool remotePosters)
    {
        target.Year ??= hit.Year;
        target.Ids.TryAdd(IdKey(hit.Source), hit.Id);

        // Only AniList and MyAnimeList say what kind of release an entry is.
        if (hit.Source is SourceIds.AniList or SourceIds.Mal)
        {
            target.Format ??= FormatLabel(hit.Format);
        }

        if (remotePosters && target.ImageUrl is null && !string.IsNullOrEmpty(hit.ImageUrl))
        {
            target.ImageUrl = hit.ImageUrl;
        }
    }

    // A search that fails, or is stopped, leaves the others to answer.
    private static async Task<IReadOnlyList<RemoteCandidate>> Guarded(string source, Func<Task<IReadOnlyList<RemoteCandidate>>> search, ILogger logger)
    {
        try
        {
            return await search().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogDebug("{Source} could not be searched by an identify: {Message}", source, ex.Message);

            return [];
        }
    }

    [GeneratedRegex(@"\s*\[(?:AniList|MyAnimeList), not on AniDB\]\s*$")]
    private static partial Regex MarkerRegex();
}
