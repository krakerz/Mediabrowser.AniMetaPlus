using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Configuration;
using AniMetaPlus.Data;
#if JELLYFIN
using Jellyfin.Data.Enums;
#endif
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Sources;

/// <summary>
/// How a merge ended.
/// </summary>
/// <param name="HasData">Whether anything was written onto the item.</param>
/// <param name="PausedFor">The source the item waits for, when a paused source kept it empty.</param>
public sealed record MergeOutcome(bool HasData, string? PausedFor);

/// <summary>
/// Fills a show from every source, one kind of data at a time, in the order the owner set for
/// that kind: the first source that has a value supplies it, except for genres, rating and cast,
/// where every source in the order contributes. Records which source supplied what, and acts on
/// the fallback mode of a source that is turned away.
/// </summary>
public static class SeriesMerge
{
    private static readonly DataKind[] _kinds =
    [
        DataKind.Titles,
        DataKind.Overview,
        DataKind.Details,
        DataKind.Genres,
        DataKind.Rating,
        DataKind.People
    ];

    private static readonly HashSet<string> _seriesSources = new(SourceIds.All, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Merges the sources into the result.
    /// </summary>
    /// <param name="result">The result, already filled by AniDB when it answered.</param>
    /// <param name="aniDb">How AniDB answered.</param>
    /// <param name="context">The item.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How it ended.</returns>
    public static async Task<MergeOutcome> Apply<TItem>(
        MetadataResult<TItem> result,
        SourceOutcome aniDb,
        FetchContext context,
        ILogger logger,
        CancellationToken cancellationToken)
        where TItem : BaseItem, new()
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(aniDb);
        ArgumentNullException.ThrowIfNull(context);

        result.Item ??= new TItem();

        var configuration = Plugin.Instance.Configuration;
        var db = AniMetaDb.Current;
        var outcomes = new Dictionary<string, SourceOutcome>(StringComparer.OrdinalIgnoreCase) { [SourceIds.AniDb] = aniDb };
        var hasData = aniDb.State == SourceState.Ok;
        string? pausedFor = null;

        async Task<SourceOutcome> Get(string source)
        {
            if (!outcomes.TryGetValue(source, out var outcome))
            {
                outcome = await SourceFetcher.Fetch(source, context, cancellationToken).ConfigureAwait(false);
                outcomes[source] = outcome;
            }

            return outcome;
        }

        foreach (var kind in _kinds)
        {
            var preferredOrder = configuration.GetPriority(kind).Where(_seriesSources.Contains).ToList();
            var provenanceId = ProvenanceRecord.MakeId(context.ItemKey, kind.ToString());
            var provenance = db.Provenance.FindById(provenanceId);
            var order = preferredOrder.ToList();

            // A field held to a source by a review, or by the owner's decision on one, asks that
            // source first.
            if (provenance is { PinnedSource.Length: > 0 } && order.Remove(provenance.PinnedSource))
            {
                order.Insert(0, provenance.PinnedSource);
            }

            if (SourceDefaults.IsCombined(kind))
            {
                var contributors = new List<AnimeRecord>();
                string? blocked = null;

                foreach (var source in order)
                {
                    var outcome = await Get(source).ConfigureAwait(false);

                    if (outcome is { State: SourceState.Ok, Record: { } record } && Has(kind, record))
                    {
                        contributors.Add(record);
                    }
                    else if (outcome.State == SourceState.Blocked)
                    {
                        blocked ??= source;
                    }
                }

                if (contributors.Count > 0)
                {
                    ApplyCombined(kind, result, contributors, aniDb.Record);
                    hasData = true;
                }

                var standIn = blocked != null && configuration.GetSource(blocked).Mode != FallbackMode.Pause;
                Record(db, provenanceId, context, kind, string.Join(",", contributors.Select(c => c.Source)), standIn ? blocked : null, keepPin: provenance);

                continue;
            }

            string? chosen = null;
            string? turnedAway = null;
            var paused = false;

            foreach (var source in order)
            {
                var outcome = await Get(source).ConfigureAwait(false);

                if (outcome is { State: SourceState.Ok, Record: { } record } && Has(kind, record))
                {
                    chosen = source;
                    ApplySingle(kind, result, record, aniDb.Record, context);
                    break;
                }

                if (outcome.State == SourceState.Blocked && turnedAway == null)
                {
                    turnedAway = source;

                    // Pause keeps what the item has. A field it never got has nothing to keep, so
                    // the next source fills it, as with Switch, until the paused one answers: an
                    // anime AniDB never answered for would otherwise get no data at all while
                    // AniDB is banned, not even from the local dump and the other sources.
                    if (configuration.GetSource(source).Mode == FallbackMode.Pause && provenance is not null)
                    {
                        paused = true;
                        break;
                    }
                }
            }

            if (paused)
            {
                // Titles are what the item is shown under. Without them and without AniDB there
                // is nothing worth writing, so the item keeps what it has and waits.
                if (kind == DataKind.Titles && aniDb.State != SourceState.Ok)
                {
                    pausedFor = turnedAway;
                }

                continue;
            }

            if (chosen == null)
            {
                continue;
            }

            hasData = true;

            var reviewing = turnedAway != null && configuration.GetSource(turnedAway).Mode == FallbackMode.SwitchReview;
            Record(db, provenanceId, context, kind, chosen, turnedAway, keepPin: provenance, pinTo: reviewing ? chosen : null);

            await ProposeReview(db, provenance, kind, chosen, preferredOrder, outcomes, Get, result, context, logger).ConfigureAwait(false);
        }

        // The ids the sources gave the anime, so that the server and later lookups can use them.
        foreach (var provider in new[] { SourceIds.Mal, SourceIds.AniList })
        {
            if (context.Ids.TryGetValue(provider, out var id) && !string.IsNullOrEmpty(id) && string.IsNullOrEmpty(result.Item.GetProviderId(provider)))
            {
                result.Item.SetProviderId(provider, id);
            }
        }

        db.Wrote();

        return new MergeOutcome(hasData && pausedFor == null, pausedFor);
    }

    /// <summary>
    /// Proposes the preferred source's value for review, where a stand-in supplied the field
    /// while it was turned away and it now answers again.
    /// </summary>
    private static async Task ProposeReview<TItem>(
        AniMetaDb db,
        ProvenanceRecord? provenance,
        DataKind kind,
        string chosen,
        IReadOnlyList<string> preferredOrder,
        Dictionary<string, SourceOutcome> outcomes,
        Func<string, Task<SourceOutcome>> get,
        MetadataResult<TItem> result,
        FetchContext context,
        ILogger logger)
        where TItem : BaseItem
    {
        if (provenance == null || provenance.Locked || string.IsNullOrEmpty(provenance.PinnedSource) || preferredOrder.Count == 0)
        {
            return;
        }

        // The source the field would come from without the pin.
        var preferred = provenance.Provisional && !string.IsNullOrEmpty(provenance.PreferredSource)
            ? provenance.PreferredSource
            : preferredOrder[0];

        if (string.Equals(preferred, chosen, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var outcome = await get(preferred).ConfigureAwait(false);
        if (outcome is not { State: SourceState.Ok, Record: { } record } || !Has(kind, record))
        {
            return;
        }

        var proposed = Describe(kind, record, context);
        var current = Describe(kind, outcomes.TryGetValue(chosen, out var c) && c.Record != null ? c.Record : null, context, result.Item);

        if (string.Equals(proposed, current, StringComparison.Ordinal)
            || string.Equals(proposed, provenance.DeclinedValue, StringComparison.Ordinal))
        {
            return;
        }

        var id = ProvenanceRecord.MakeId(context.ItemKey, kind.ToString());
        var existing = db.Reviews.FindById(id);
        if (existing is { Status: ReviewStatus.Pending } && existing.ProposedValue == proposed)
        {
            return;
        }

        db.Reviews.Upsert(new ReviewRecord
        {
            Id = id,
            ItemKey = context.ItemKey,
            ItemPath = context.ItemPath,
            ItemName = context.ItemName,
            Field = kind.ToString(),
            CurrentValue = current,
            CurrentSource = chosen,
            ProposedValue = proposed,
            ProposedSource = preferred,
            Status = ReviewStatus.Pending,
            CreatedUtc = DateTime.UtcNow
        });

        logger.LogInformation(
            "{Source} answers again for {Item}; its {Kind} waits for review in place of what {StandIn} supplied",
            preferred,
            context.ItemName,
            kind,
            chosen);
    }

    private static void Record(
        AniMetaDb db,
        string id,
        FetchContext context,
        DataKind kind,
        string source,
        string? preferredSource,
        ProvenanceRecord? keepPin,
        string? pinTo = null)
    {
        var record = keepPin ?? new ProvenanceRecord { Id = id, ItemKey = context.ItemKey, Field = kind.ToString() };

        record.Source = source;
        record.ItemPath = context.ItemPath;
        record.ItemName = context.ItemName;
        record.UpdatedUtc = DateTime.UtcNow;

        if (preferredSource != null)
        {
            record.Provisional = true;
            record.PreferredSource = preferredSource;
        }
        else if (record.Provisional && string.Equals(source, record.PreferredSource, StringComparison.OrdinalIgnoreCase))
        {
            // The source it was waiting for supplied it after all, the stand-in having nothing.
            record.Provisional = false;
            record.PreferredSource = string.Empty;
            record.PinnedSource = string.Empty;
        }
        else if (string.IsNullOrEmpty(record.PinnedSource) || string.Equals(record.PinnedSource, source, StringComparison.OrdinalIgnoreCase))
        {
            // Stays provisional only while held to a stand-in pending review.
            record.Provisional = record.Provisional && !string.IsNullOrEmpty(record.PinnedSource) && record.DeclinedValue.Length == 0 && !record.Locked
                && !string.Equals(record.PinnedSource, record.PreferredSource, StringComparison.OrdinalIgnoreCase);

            if (!record.Provisional)
            {
                record.PreferredSource = string.Empty;
            }
        }

        if (pinTo != null)
        {
            record.PinnedSource = pinTo;
        }

        db.Provenance.Upsert(record);
    }

    /// <summary>
    /// Whether a source has a value for a kind of data.
    /// </summary>
    internal static bool Has(DataKind kind, AnimeRecord record) => kind switch
    {
        DataKind.Titles => !string.IsNullOrWhiteSpace(record.RomajiTitle ?? record.EnglishTitle ?? record.NativeTitle),
        DataKind.Overview => !string.IsNullOrWhiteSpace(record.Overview),
        DataKind.Details => record.StartDate != null,
        DataKind.Genres => record.Genres.Count > 0,
        DataKind.Rating => record.Rating is > 0,
        DataKind.People => record.People.Count > 0,
        _ => false
    };

    private static void ApplySingle<TItem>(DataKind kind, MetadataResult<TItem> result, AnimeRecord record, AnimeRecord? aniDb, FetchContext context)
        where TItem : BaseItem
    {
        var series = result.Item;

        // AniDB's own answer is already on the result, written the way its provider writes it.
        if (ReferenceEquals(record, aniDb))
        {
            return;
        }

        switch (kind)
        {
            case DataKind.Titles:
                var setting = Plugin.Instance.Configuration.GetSource(record.Source);
                var preference = string.IsNullOrEmpty(setting.TitlePreference)
                    ? Plugin.Instance.Configuration.TitlePreference.ToString()
                    : setting.TitlePreference;
                series.Name = record.Title(preference) ?? series.Name;
                series.OriginalTitle = record.RomajiTitle ?? record.NativeTitle ?? series.OriginalTitle;
                break;

            case DataKind.Overview:
                series.Overview = record.Overview;
                break;

            case DataKind.Details:
                series.PremiereDate = record.StartDate;
                series.ProductionYear = record.StartDate?.Year;
                series.EndDate = record.EndDate;

                if (record.Studios.Count > 0)
                {
                    series.Studios = record.Studios.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                }

                break;
        }
    }

    private static void ApplyCombined<TItem>(DataKind kind, MetadataResult<TItem> result, List<AnimeRecord> contributors, AnimeRecord? aniDb)
        where TItem : BaseItem
    {
        var configuration = Plugin.Instance.Configuration;
        var series = result.Item;

        switch (kind)
        {
            case DataKind.Genres:
                if (!configuration.ImportGenres && !configuration.ImportTags)
                {
                    return;
                }

                var genres = new List<string>();
                var tags = new List<string>();

                foreach (var record in contributors)
                {
                    if (ReferenceEquals(record, aniDb))
                    {
                        // AniDB's tags have been through the genre pipeline already.
                        genres.AddRange(series.Genres ?? []);
                        tags.AddRange(series.Tags ?? []);
                        continue;
                    }

                    foreach (var name in record.Genres)
                    {
                        var normal = GenreAliases.Normalize(name.Name, configuration.TitleCaseGenres);

                        // A source's genres are genres; its tags are only worth keeping where
                        // the source says they describe the show well.
                        if (name.Weight >= 1)
                        {
                            genres.Add(normal);
                        }
                        else if (name.Weight >= 0.6)
                        {
                            tags.Add(normal);
                        }
                    }
                }

                var distinctGenres = genres.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (configuration.MaxGenres > 0 && distinctGenres.Count > configuration.MaxGenres)
                {
                    distinctGenres = distinctGenres.Take(configuration.MaxGenres).ToList();
                }

                if (configuration.ImportGenres)
                {
                    series.Genres = distinctGenres.ToArray();
                }

                if (configuration.ImportTags)
                {
                    series.Tags = tags.Concat(genres).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                }

                break;

            case DataKind.Rating:
                if (!configuration.ImportCommunityRating)
                {
                    return;
                }

                double total = 0;
                double weight = 0;

                foreach (var record in contributors)
                {
                    if (!configuration.GetSource(record.Source).IncludeInRating || record.Rating is not { } rating)
                    {
                        continue;
                    }

                    // Weighted by votes, so that a score from a handful of voters does not pull one
                    // from a hundred thousand. A source that gives no count weighs as one hundred.
                    var votes = Math.Max(record.Votes ?? 100, 1);
                    total += rating * votes;
                    weight += votes;
                }

                if (weight > 0)
                {
                    series.CommunityRating = (float)Math.Round(total / weight, 1);
                }

                break;

            case DataKind.People:
                var known = new HashSet<string>(StringComparer.Ordinal);

                foreach (var person in result.People ?? [])
                {
                    known.Add(NameKey(person.Name));
                }

                foreach (var record in contributors)
                {
                    if (ReferenceEquals(record, aniDb))
                    {
                        continue;
                    }

                    // Where cast is listed as characters, the other sources' actors would mix two
                    // ways of listing in one list.
                    if (configuration.CastShowsCharacters)
                    {
                        break;
                    }

                    foreach (var person in record.People)
                    {
                        if (string.IsNullOrWhiteSpace(person.Name) || !known.Add(NameKey(person.Name)))
                        {
                            continue;
                        }

                        var info = new PersonInfo
                        {
                            Name = person.Name,
                            Role = person.Role,
                            Type = HostPeople.ToHost(Enum.TryParse<PersonKind>(person.Kind, true, out var parsed) ? parsed : PersonKind.Unknown),
                            ImageUrl = person.ImageUrl
                        };

                        foreach (var (provider, id) in person.Ids)
                        {
                            info.SetProviderId(provider, id);
                        }

                        result.AddPerson(info);
                    }
                }

                break;
        }
    }

    /// <summary>
    /// The text a field holds, for a review.
    /// </summary>
    internal static string Describe(DataKind kind, AnimeRecord? record, FetchContext context, BaseItem? series = null)
    {

        return kind switch
        {
            DataKind.Titles => record?.Title(Plugin.Instance.Configuration.TitlePreference.ToString()) ?? series?.Name ?? string.Empty,
            DataKind.Overview => record?.Overview ?? series?.Overview ?? string.Empty,
            DataKind.Details => (record?.StartDate ?? (DateTime?)series?.PremiereDate?.UtcDateTime())?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
            _ => string.Empty
        };
    }

    /// <summary>
    /// A person's name reduced to what tells two spellings of it apart: AniDB writes family name
    /// first and reverses it, AniList writes given name first.
    /// </summary>
    internal static string NameKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var parts = name
            .Normalize(NormalizationForm.FormKD)
            .ToLowerInvariant()
            .Split([' ', '-', '.', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => new string(p.Where(char.IsLetterOrDigit).ToArray()))
            .Where(p => p.Length > 0)
            .OrderBy(p => p, StringComparer.Ordinal);

        return string.Join(" ", parts);
    }
}

/// <summary>
/// Spells the genres of every source the same way.
/// </summary>
public static class GenreAliases
{
    private static readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sci-Fi"] = "Science Fiction",
        ["SciFi"] = "Science Fiction",
        ["Slice of Life"] = "Slice of Life",
        ["Mahou Shoujo"] = "Magical Girl",
        ["Mahou Shoujo (Magical Girls)"] = "Magical Girl",
        ["Magical Girls"] = "Magical Girl",
        ["Shounen"] = "Shounen",
        ["Shonen"] = "Shounen",
        ["Shojo"] = "Shoujo",
        ["Rom-Com"] = "Romantic Comedy",
        ["Mecha"] = "Mecha",
        ["Martial Arts"] = "Martial Arts",
        ["Psychological"] = "Psychological",
        ["Suspense"] = "Thriller",
        ["Boys Love"] = "Boys Love",
        ["Shounen Ai"] = "Boys Love",
        ["Girls Love"] = "Girls Love",
        ["Shoujo Ai"] = "Girls Love"
    };

    /// <summary>
    /// Normalizes a genre name.
    /// </summary>
    /// <param name="name">The name as the source spells it.</param>
    /// <param name="titleCase">Whether to title case it.</param>
    /// <returns>The name.</returns>
    public static string Normalize(string name, bool titleCase)
    {
        var trimmed = name.Trim();

        if (_aliases.TryGetValue(trimmed, out var alias))
        {
            return alias;
        }

        return titleCase ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(trimmed) : trimmed;
    }
}
