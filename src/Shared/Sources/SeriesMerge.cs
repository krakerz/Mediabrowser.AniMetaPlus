using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Configuration;
using AniMetaPlus.Core;
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

                // A combined field built without a blocked source waits for it whatever its mode: the
                // others' genres and tags are only part of the list, and AniDB's tags went missing
                // for good when a paused AniDB's absence was recorded as the field's final answer.
                Record(db, provenanceId, context, kind, string.Join(",", contributors.Select(c => c.Source)), blocked, keepPin: provenance);

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
        // Written even over an id the item has: the fetch began from that id unless the mappings
        // named a better one, and a wrong one would otherwise stay on the item for good.
        foreach (var provider in new[] { SourceIds.Mal, SourceIds.AniList })
        {
            if (context.Ids.TryGetValue(provider, out var id) && !string.IsNullOrEmpty(id) && !string.Equals(result.Item.GetProviderId(provider), id, StringComparison.Ordinal))
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
        if (provenance == null || provenance.Locked || string.IsNullOrEmpty(provenance.PinnedSource) || preferredOrder.Count == 0 || string.IsNullOrEmpty(context.ItemPath))
        {
            return;
        }

        // A decision made before the decided source was recorded is read from its review, which
        // keeps the same id until a new review replaces it.
        if (!provenance.Provisional && string.IsNullOrEmpty(provenance.DecidedSource)
            && db.Reviews.FindById(provenance.Id) is { Status: not ReviewStatus.Pending } decided
            && !string.IsNullOrEmpty(decided.ProposedSource))
        {
            provenance.DecidedSource = decided.ProposedSource;
            db.Provenance.Upsert(provenance);
        }

        var preferred = ReviewedSource(provenance, preferredOrder);

        if (string.Equals(preferred, chosen, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Reordered since: the stand-in's source now comes after the one that supplied the field.
        if (provenance.Provisional && !StillWaitsFor(preferred, chosen, preferredOrder))
        {
            Settle(db, provenance, kind, context);

            return;
        }

        var outcome = await get(preferred).ConfigureAwait(false);
        if (outcome is not { State: SourceState.Ok, Record: { } record } || !Has(kind, record))
        {
            // It answers but has nothing for the field: nothing will ever be proposed, so the stand-in stands.
            if (outcome.State == SourceState.Ok)
            {
                Settle(db, provenance, kind, context);
            }

            return;
        }

        var proposed = Describe(kind, record, context);
        var current = Describe(kind, outcomes.TryGetValue(chosen, out var c) && c.Record != null ? c.Record : null, context, result.Item);

        if (string.Equals(proposed, current, StringComparison.Ordinal))
        {
            // Nothing to review, so the field is settled rather than left to be refreshed every run.
            Settle(db, provenance, kind, context);

            return;
        }

        if (string.Equals(proposed, provenance.DeclinedValue, StringComparison.Ordinal))
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

    /// <summary>
    /// The source a review of a pinned field would propose: the one a stand-in waits for, the
    /// one the owner already decided about, or else the first in the order. Comparing a decided
    /// field with the first source asked the owner again after every Accept whenever the
    /// accepted source was not first - AniDB accepted, then MyAnimeList proposed.
    /// </summary>
    /// <param name="provenance">The field's record.</param>
    /// <param name="order">The field's source order.</param>
    /// <returns>The source.</returns>
    internal static string ReviewedSource(ProvenanceRecord provenance, IReadOnlyList<string> order)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(order);

        return provenance.Provisional && !string.IsNullOrEmpty(provenance.PreferredSource) ? provenance.PreferredSource
            : !string.IsNullOrEmpty(provenance.DecidedSource) ? provenance.DecidedSource
            : order[0];
    }

    /// <summary>
    /// Whether a stand-in still waits for its source: that source comes before the one that
    /// supplied the field in the field's current order. After the owner reorders the sources, a
    /// stand-in for one that now comes later is simply the field's data.
    /// </summary>
    /// <param name="waitedFor">The source the stand-in waits for, or a review proposes.</param>
    /// <param name="current">The source that supplied the field; several, comma separated, for a combined one.</param>
    /// <param name="order">The field's source order now.</param>
    /// <returns><c>true</c> while the source waited for still comes first.</returns>
    public static bool StillWaitsFor(string? waitedFor, string? current, IReadOnlyList<string> order)
    {
        ArgumentNullException.ThrowIfNull(order);

        int IndexOf(string source) => order.ToList().FindIndex(s => string.Equals(s, source.Trim(), StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(waitedFor) || IndexOf(waitedFor) is var wanted && wanted < 0)
        {
            return false;
        }

        var supplied = (current ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(IndexOf).Where(i => i >= 0).ToList();

        return supplied.Count == 0 || wanted < supplied.Min();
    }

    /// <summary>
    /// Settles the stand-ins, and drops the pending reviews, that the current source order no
    /// longer backs: their source now comes after the one that supplied the field, so asking it
    /// again would only spend requests on a value the owner ranks lower.
    /// </summary>
    /// <param name="db">The database.</param>
    /// <param name="configuration">The configuration, for the source order.</param>
    /// <returns>How many stand-ins were settled and reviews dropped.</returns>
    public static (int Settled, int Dropped) SettleOutdated(AniMetaDb db, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(configuration);

        var settled = 0;
        var dropped = 0;

        foreach (var review in db.Reviews.FindAll().Where(r => r.Status == ReviewStatus.Pending).ToList())
        {
            if (Enum.TryParse<DataKind>(review.Field, out var kind) && !StillWaitsFor(review.ProposedSource, review.CurrentSource, configuration.GetPriority(kind)))
            {
                db.Reviews.Delete(review.Id);
                dropped++;
            }
        }

        foreach (var provenance in db.Provenance.FindAll().Where(p => p.Provisional).ToList())
        {
            // Every source of a combined field contributes, wherever it is in the order; one still
            // in the order is still waited for.
            if (Enum.TryParse<DataKind>(provenance.Field, out var kind)
                && !db.Reviews.Exists(r => r.Id == provenance.Id)
                && (SourceDefaults.IsCombined(kind)
                    ? !configuration.GetPriority(kind).Contains(provenance.PreferredSource, StringComparer.OrdinalIgnoreCase)
                    : !StillWaitsFor(provenance.PreferredSource, provenance.Source, configuration.GetPriority(kind))))
            {
                provenance.Provisional = false;
                provenance.PreferredSource = string.Empty;
                provenance.PinnedSource = string.Empty;
                db.Provenance.Upsert(provenance);
                settled++;
            }
        }

        return (settled, dropped);
    }

    /// <summary>
    /// Marks the combined fields a source took no part in as waiting for it, where it is in the
    /// field's order: genres and tags, rating, cast. The queue refreshes them once the source
    /// answers, and a source that has nothing for the field leaves it settled after that one try.
    /// </summary>
    /// <param name="db">The database.</param>
    /// <param name="configuration">The settings.</param>
    /// <param name="source">The source.</param>
    /// <returns>How many fields were marked.</returns>
    public static int WaitForMissingContributors(AniMetaDb db, PluginConfiguration configuration, string source)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(configuration);

        var marked = 0;
        foreach (var provenance in db.Provenance.FindAll().Where(p => !p.Provisional && !p.Locked).ToList())
        {
            if (!Enum.TryParse<DataKind>(provenance.Field, out var kind)
                || !SourceDefaults.IsCombined(kind)
                || !configuration.GetPriority(kind).Contains(source, StringComparer.OrdinalIgnoreCase)
                || provenance.Source.Split(',').Contains(source, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            provenance.Provisional = true;
            provenance.PreferredSource = source;
            db.Provenance.Upsert(provenance);
            marked++;
        }

        return marked;
    }

    /// <summary>
    /// Deletes the stand-ins and pending reviews that have no item on the library behind them,
    /// left by lookups made for a season's own entry. Nothing could ever refresh them.
    /// </summary>
    /// <param name="db">The database.</param>
    /// <returns>How many were deleted.</returns>
    public static int RemoveOrphans(AniMetaDb db)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.Provenance.DeleteMany(p => p.ItemPath == null || p.ItemPath == string.Empty)
            + db.Reviews.DeleteMany(r => r.Status == ReviewStatus.Pending && (r.ItemPath == null || r.ItemPath == string.Empty));
    }

    /// <summary>
    /// Deletes the records kept for an anime a show no longer is: its stand-ins, reviews and
    /// field records under the AniDB entry it had before it was identified again. A refresh
    /// records the show under its new entry, so nothing would ever replace the old ones, and the
    /// queue kept refreshing the show for them.
    /// </summary>
    /// <param name="db">The database.</param>
    /// <param name="aniDbByPath">The AniDB id of each item in the anime libraries, by its path.</param>
    /// <returns>How many were deleted.</returns>
    public static int RemoveStale(AniMetaDb db, IReadOnlyDictionary<string, string> aniDbByPath)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(aniDbByPath);

        if (aniDbByPath.Count == 0)
        {
            return 0;
        }

        var stale = db.Provenance.FindAll()
            .Where(p => !string.IsNullOrEmpty(p.ItemPath) && p.ItemKey.StartsWith("anidb:", StringComparison.Ordinal))
            .Where(p => !aniDbByPath.TryGetValue(p.ItemPath, out var id) || !string.Equals(ItemKeys.ForAnime(id), p.ItemKey, StringComparison.Ordinal))
            .Select(p => p.Id)
            .ToList();

        foreach (var id in stale)
        {
            db.Provenance.Delete(id);
            db.Reviews.Delete(id);
        }

        return stale.Count;
    }

    private static void Settle(AniMetaDb db, ProvenanceRecord provenance, DataKind kind, FetchContext context)
    {
        // Only a stand-in still waiting for review; a pending review is left to be decided.
        if (!provenance.Provisional || db.Reviews.Exists(r => r.Id == ProvenanceRecord.MakeId(context.ItemKey, kind.ToString())))
        {
            return;
        }

        provenance.Provisional = false;
        provenance.PreferredSource = string.Empty;
        provenance.PinnedSource = string.Empty;
        db.Provenance.Upsert(provenance);
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
        // Nothing on the library to refresh: a lookup made for another reason, such as a season
        // reading its own entry, keeps no record that the queue would wait on for good.
        if (string.IsNullOrEmpty(context.ItemPath))
        {
            return;
        }

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
