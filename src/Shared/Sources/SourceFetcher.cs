using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using AniMetaPlus.Data;
using AniMetaPlus.Titles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace AniMetaPlus.Sources;

/// <summary>
/// How a source stands for one item.
/// </summary>
public enum SourceState
{
    /// <summary>The source answered.</summary>
    Ok,

    /// <summary>The source has nothing for the item, or is not asked about it for now.</summary>
    Missing,

    /// <summary>The source as a whole is turned away: banned, rate limited, down, key refused.</summary>
    Blocked,

    /// <summary>The source is off, has no key, or is not installed.</summary>
    Unconfigured
}

/// <summary>
/// How a source answered for one item.
/// </summary>
/// <param name="State">How it stands.</param>
/// <param name="Record">What it answered, when it did.</param>
public sealed record SourceOutcome(SourceState State, AnimeRecord? Record)
{
    /// <summary>Gets an outcome for a source that was not asked.</summary>
    public static SourceOutcome Unconfigured { get; } = new(SourceState.Unconfigured, null);

    /// <summary>Gets an outcome for a source with nothing.</summary>
    public static SourceOutcome Missing { get; } = new(SourceState.Missing, null);

    /// <summary>Gets an outcome for a source turned away.</summary>
    public static SourceOutcome Blocked { get; } = new(SourceState.Blocked, null);
}

/// <summary>
/// What is known about the item being looked up, which every source is asked with.
/// </summary>
public sealed class FetchContext
{
    /// <summary>Gets or sets the item key.</summary>
    public string ItemKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's name.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's path.</summary>
    public string ItemPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the AniDB id, when known.</summary>
    public string? AnimeId { get; set; }

    /// <summary>Gets or sets the year, when known.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is a movie rather than a show.</summary>
    public bool IsMovie { get; set; }

    /// <summary>Gets or sets the metadata language.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Gets the ids the item is known by, keyed by provider.</summary>
    public Dictionary<string, string> Ids { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Asks one source about one anime, from its cache where it can.
/// </summary>
public static class SourceFetcher
{
    /// <summary>
    /// Asks a source about the item in the context.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="context">The item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How it answered.</returns>
    public static async Task<SourceOutcome> Fetch(string source, FetchContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var setting = Plugin.Instance.Configuration.GetSource(source);
        if (!setting.Enabled)
        {
            return SourceOutcome.Unconfigured;
        }

        if (BlockStore.SourceBlock(source) != null)
        {
            return SourceOutcome.Blocked;
        }

        if (BlockStore.ItemBlock(source, context.ItemKey) != null)
        {
            return SourceOutcome.Missing;
        }

        try
        {
            if (string.Equals(source, SourceIds.AniList, StringComparison.OrdinalIgnoreCase))
            {
                return await FetchAniList(context, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(source, SourceIds.Mal, StringComparison.OrdinalIgnoreCase))
            {
                return await FetchMal(context, cancellationToken).ConfigureAwait(false);
            }

            if (SourceIds.IsDelegated(source))
            {
                return await FetchDelegated(source, context, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SourceBlockedException ex)
        {
            return ex.Block is { Scope: BlockScope.Item } ? SourceOutcome.Missing : SourceOutcome.Blocked;
        }

        return SourceOutcome.Unconfigured;
    }

    private static TimeSpan MaxAge => RefreshScope.SourceMaxAge;

    private static async Task<SourceOutcome> FetchAniList(FetchContext context, CancellationToken cancellationToken)
    {
        var http = Plugin.Instance.GetHttpClient();
        string key;
        Func<CancellationToken, Task<AnimeRecord>> request;

        if (context.Ids.TryGetValue(SourceIds.AniList, out var aniListId) && IsNumber(aniListId))
        {
            key = "id-" + aniListId;
            request = ct => AniListClient.GetById(http, aniListId, ct);
        }
        else if (context.Ids.TryGetValue(SourceIds.Mal, out var malId) && IsNumber(malId))
        {
            key = "mal-" + malId;
            request = ct => AniListClient.GetByMalId(http, malId, ct);
        }
        else
        {
            // No id ties the anime to AniList, so it is looked up by the name AniDB gives it,
            // read from the local dump rather than asked for.
            var title = context.AnimeId is { } aid ? TitlesDump.MainTitle(Plugin.Instance.AppPaths, aid) : null;
            title ??= context.ItemName;

            if (string.IsNullOrWhiteSpace(title))
            {
                return SourceOutcome.Missing;
            }

            key = "search-" + title + "-" + context.Year?.ToString(CultureInfo.InvariantCulture);
            request = ct => AniListClient.Search(http, title, context.Year, ct);
        }

        var record = SourceCache.Read(SourceIds.AniList, key, MaxAge)
            ?? await Run(SourceIds.AniList, context, key, request, cancellationToken).ConfigureAwait(false);

        Learn(context, record);

        return new SourceOutcome(SourceState.Ok, record);
    }

    private static async Task<SourceOutcome> FetchMal(FetchContext context, CancellationToken cancellationToken)
    {
        if (!MalClient.IsConfigured)
        {
            return SourceOutcome.Unconfigured;
        }

        var http = Plugin.Instance.GetHttpClient();

        if (!context.Ids.TryGetValue(SourceIds.Mal, out var malId) || !IsNumber(malId))
        {
            // Looked up by the name AniDB gives the anime, read from the local dump. Only for an
            // identified anime: a name search for anything else risks the wrong show.
            var title = context.AnimeId is { } aid ? TitlesDump.MainTitle(Plugin.Instance.AppPaths, aid) : null;
            if (string.IsNullOrWhiteSpace(title))
            {
                return SourceOutcome.Missing;
            }

            var searchKey = "search-" + title + "-" + context.Year?.ToString(CultureInfo.InvariantCulture);
            var cachedSearch = SourceCache.Read(SourceIds.Mal, searchKey, MaxAge);

            if (cachedSearch != null)
            {
                malId = cachedSearch.SourceId;
            }
            else
            {
                malId = await SourceGate.Run(SourceIds.Mal, context.ItemKey, context.ItemName, string.Empty, searchKey, ct => MalClient.Search(http, title, context.Year, ct), cancellationToken).ConfigureAwait(false);
                SourceCache.Write(new AnimeRecord { Source = SourceIds.Mal, SourceId = malId, FetchedUtc = DateTime.UtcNow }, searchKey);
            }

            context.Ids[SourceIds.Mal] = malId;
        }

        var key = "id-" + malId;
        var record = SourceCache.Read(SourceIds.Mal, key, MaxAge)
            ?? await Run(SourceIds.Mal, context, key, ct => MalClient.Get(http, malId, ct), cancellationToken).ConfigureAwait(false);

        Learn(context, record);

        return new SourceOutcome(SourceState.Ok, record);
    }

    private static async Task<SourceOutcome> FetchDelegated(string source, FetchContext context, CancellationToken cancellationToken)
    {
        var idKey = string.Equals(source, SourceIds.Tvdb, StringComparison.OrdinalIgnoreCase) ? "Tvdb" : "Tmdb";

        if (!DelegatedProviders.IsInstalled(source))
        {
            return SourceOutcome.Unconfigured;
        }

        if (!context.Ids.TryGetValue(idKey, out var id) || string.IsNullOrEmpty(id))
        {
            return SourceOutcome.Missing;
        }

        var found = context.IsMovie
            ? await Delegated<Movie, MovieInfo>(source, idKey, id, context, cancellationToken).ConfigureAwait(false)
            : await Delegated<Series, SeriesInfo>(source, idKey, id, context, cancellationToken).ConfigureAwait(false);

        if (found is not { } pair)
        {
            return SourceOutcome.Missing;
        }

        var (series, people) = pair;

        var record = new AnimeRecord
        {
            Source = source,
            SourceId = id,
            EnglishTitle = series.Name,
            RomajiTitle = series.OriginalTitle,
            Overview = series.Overview,
            StartDate = series.PremiereDate?.UtcDateTime(),
            EndDate = series.EndDate?.UtcDateTime(),
            Rating = series.CommunityRating,
            FetchedUtc = DateTime.UtcNow
        };

        record.Studios.AddRange(series.Studios ?? []);
        record.Genres.AddRange((series.Genres ?? []).Select(g => new WeightedName { Name = g, Weight = 0.5 }));

        foreach (var person in people)
        {
            record.People.Add(new PersonRecord
            {
                Name = person.Name ?? string.Empty,
                Role = person.Role ?? string.Empty,
                Kind = person.Type.ToString(),
                ImageUrl = person.ImageUrl
            });
        }

        return new SourceOutcome(SourceState.Ok, record);
    }

    private static async Task<(BaseItem Item, IReadOnlyList<PersonInfo> People)?> Delegated<TItem, TInfo>(string source, string idKey, string id, FetchContext context, CancellationToken cancellationToken)
        where TItem : BaseItem, IHasLookupInfo<TInfo>, new()
        where TInfo : ItemLookupInfo, new()
    {
        var info = new TInfo
        {
            Name = context.ItemName,
            Year = context.Year,
            MetadataLanguage = context.Language
        };
        info.SetProviderId(idKey, id);

        var result = await DelegatedProviders.GetMetadata<TItem, TInfo>(source, info, context.ItemKey, context.ItemName, string.Empty, cancellationToken).ConfigureAwait(false);

        return result?.Item is { } item ? (item, (IReadOnlyList<PersonInfo>)(result.People?.ToList() ?? [])) : null;
    }

    private static async Task<AnimeRecord> Run(string source, FetchContext context, string key, Func<CancellationToken, Task<AnimeRecord>> request, CancellationToken cancellationToken)
    {
        var record = await SourceGate.Run(source, context.ItemKey, context.ItemName, string.Empty, key, request, cancellationToken).ConfigureAwait(false);
        SourceCache.Write(record, key);

        return record;
    }

    /// <summary>
    /// Takes the ids a source gave the anime, so that the sources asked after it can be looked
    /// up by id rather than by name.
    /// </summary>
    private static void Learn(FetchContext context, AnimeRecord record)
    {
        foreach (var (provider, id) in record.Ids)
        {
            if (!string.IsNullOrEmpty(id) && !context.Ids.ContainsKey(provider))
            {
                context.Ids[provider] = id;
            }
        }
    }

    private static bool IsNumber(string value) => value.Length > 0 && value.All(char.IsDigit);
}

/// <summary>
/// Reads the server's date values the same way on both servers.
/// </summary>
internal static class HostDates
{
#if EMBY
    public static DateTime UtcDateTime(this DateTimeOffset value) => value.UtcDateTime;
#else
    public static DateTime UtcDateTime(this DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
#endif
}
