using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using AniMetaPlus.Data;
using AniMetaPlus.Sources;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Providers.AniDB.Mapping;

/// <summary>
/// Whether TheTVDB has a series.
/// </summary>
internal enum TvdbExistence
{
    /// <summary>TheTVDB could not be asked, or did not answer.</summary>
    Unknown,

    /// <summary>TheTVDB has the series.</summary>
    Exists,

    /// <summary>TheTVDB has no such series.</summary>
    Missing
}

/// <summary>
/// What was done about two TVDB ids in dispute.
/// </summary>
internal enum TvdbOutcome
{
    /// <summary>The AniDB-keyed id stays.</summary>
    Kept,

    /// <summary>The id the MyAnimeList or AniList entry is filed under replaces it.</summary>
    Switched,

    /// <summary>TheTVDB could not be asked, so the AniDB-keyed id stays for now.</summary>
    Unknown
}

/// <summary>
/// The TVDB id picked for a show whose sources disagree.
/// </summary>
/// <param name="Chosen">The id the show should carry.</param>
/// <param name="Candidates">What was in dispute.</param>
/// <param name="Alternative">The other id the choice was made against.</param>
/// <param name="Outcome">What was done.</param>
/// <param name="Reason">A phrase for the Mapping tab.</param>
internal sealed record TvdbChoice(string Chosen, TvdbCandidates Candidates, LinkedTvdb? Alternative, TvdbOutcome Outcome, string Reason);

/// <summary>
/// Settles which of two TVDB ids a show is given by asking TheTVDB whether it still has them.
/// </summary>
/// <remarks>
/// The AniDB-keyed mappings can name a series TheTVDB has since deleted or merged, while the
/// MyAnimeList and AniList entries of the same anime have been corrected. The id is switched
/// only where the AniDB-keyed one is gone and exactly one other exists; in any other case the
/// AniDB-keyed id is kept, because the mappings are the plugin's own and a check that could not
/// tell is no reason to move away from them. The question is only asked of the server's TVDB
/// provider, which answers from its own cache for a series it knows.
/// </remarks>
internal static class TvdbPick
{
    /// <summary>
    /// How many days a recorded choice stands before TheTVDB is asked again.
    /// </summary>
    internal const int ReuseDays = 30;

    /// <summary>
    /// Picks the TVDB id for a show whose sources disagree.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="candidates">The ids in dispute.</param>
    /// <param name="existing">The show's record, which a choice made recently is read from instead of asking again.</param>
    /// <param name="itemName">The show's name, for the request log.</param>
    /// <param name="language">The metadata language, or <c>null</c> for English.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The choice, or <c>null</c> where the sources do not disagree.</returns>
    internal static async Task<TvdbChoice?> Choose(
        IApplicationPaths appPaths,
        TvdbCandidates candidates,
        IdRecord? existing,
        string itemName,
        string? language,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        ArgumentNullException.ThrowIfNull(candidates);

        if (!candidates.Disagree)
        {
            return null;
        }

        if (existing != null && Reusable(existing, candidates, DateTime.UtcNow))
        {
            return FromRecord(existing, candidates);
        }

        // The AniDB-keyed id is asked first, and the others only if it is gone: a series TheTVDB
        // has is the answer whatever the other entries say, and it saves their requests.
        var primary = await Exists(candidates.Primary!, itemName, language, cancellationToken).ConfigureAwait(false);
        var alternatives = new List<TvdbExistence>(candidates.Alternatives.Count);

        if (primary == TvdbExistence.Missing)
        {
            foreach (var alternative in candidates.Alternatives)
            {
                alternatives.Add(await Exists(alternative.TvdbId, itemName, language, cancellationToken).ConfigureAwait(false));
            }
        }

        var choice = Decide(candidates, primary, alternatives);

        logger.LogDebug("{ItemName}: {Reason}", itemName, choice.Reason);

        return choice;
    }

    /// <summary>
    /// Decides from what TheTVDB said.
    /// </summary>
    /// <param name="candidates">The ids in dispute.</param>
    /// <param name="primary">Whether TheTVDB has the AniDB-keyed id.</param>
    /// <param name="alternatives">Whether it has each of the others, in order; empty where they were not asked.</param>
    /// <returns>The choice.</returns>
    internal static TvdbChoice Decide(TvdbCandidates candidates, TvdbExistence primary, IReadOnlyList<TvdbExistence> alternatives)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(alternatives);

        var first = candidates.Alternatives[0];
        var kept = candidates.Primary!;

        if (primary == TvdbExistence.Exists)
        {
            return new TvdbChoice(
                kept,
                candidates,
                first,
                TvdbOutcome.Kept,
                string.Format(CultureInfo.InvariantCulture, "TheTVDB has {0}, the AniDB mapping, which is kept; {1} says {2}", kept, LinkLabel(first.Link), first.TvdbId));
        }

        if (primary == TvdbExistence.Unknown)
        {
            return Unknown(candidates);
        }

        var existing = Enumerable.Range(0, candidates.Alternatives.Count)
            .Where(i => i < alternatives.Count && alternatives[i] == TvdbExistence.Exists)
            .Select(i => candidates.Alternatives[i])
            .ToList();

        if (existing.Count == 1)
        {
            var chosen = existing[0];

            return new TvdbChoice(
                chosen.TvdbId,
                candidates,
                chosen,
                TvdbOutcome.Switched,
                string.Format(CultureInfo.InvariantCulture, "TheTVDB no longer has {0}, the AniDB mapping; {1} from {2} is used", kept, chosen.TvdbId, LinkLabel(chosen.Link)));
        }

        if (existing.Count > 1)
        {
            return new TvdbChoice(
                kept,
                candidates,
                first,
                TvdbOutcome.Kept,
                string.Format(CultureInfo.InvariantCulture, "TheTVDB has several of the other ids; the AniDB mapping {0} is kept", kept));
        }

        // None of the others exists. An answer that could not be had for one of them leaves it
        // open, where a clear no from every one is a reason to stay with the mapping.
        if (candidates.Alternatives.Where((_, i) => i >= alternatives.Count || alternatives[i] == TvdbExistence.Unknown).Any())
        {
            return Unknown(candidates);
        }

        return new TvdbChoice(
            kept,
            candidates,
            first,
            TvdbOutcome.Kept,
            string.Format(CultureInfo.InvariantCulture, "TheTVDB has neither {0} nor {1}; the AniDB mapping is kept", kept, first.TvdbId));
    }

    /// <summary>
    /// Whether a recorded choice still stands: the same ids in dispute, an answer TheTVDB gave,
    /// and recent enough that a series it dropped is not likely to have come back.
    /// </summary>
    /// <param name="existing">The show's record.</param>
    /// <param name="candidates">The ids in dispute now.</param>
    /// <param name="nowUtc">The time now.</param>
    /// <returns><c>true</c> where the record can be used without asking.</returns>
    internal static bool Reusable(IdRecord? existing, TvdbCandidates candidates, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (existing == null || !string.Equals(existing.Status, IdRecord.TvdbConflict, StringComparison.Ordinal))
        {
            return false;
        }

        var byLink = existing.NewIds.GetValueOrDefault("TvdbByLink");

        return !string.IsNullOrEmpty(candidates.Primary)
            && string.Equals(existing.NewIds.GetValueOrDefault("TvdbByAniDb"), candidates.Primary, StringComparison.Ordinal)
            && candidates.Alternatives.Any(alternative => string.Equals(alternative.TvdbId, byLink, StringComparison.Ordinal))
            && Enum.TryParse<TvdbOutcome>(existing.NewIds.GetValueOrDefault("TvdbOutcome"), out var outcome)
            && outcome != TvdbOutcome.Unknown
            && existing.UpdatedUtc > nowUtc - TimeSpan.FromDays(ReuseDays);
    }

    /// <summary>
    /// Reads the choice back from a record that <see cref="Reusable"/> passed.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="candidates">The ids in dispute.</param>
    /// <returns>The choice.</returns>
    internal static TvdbChoice FromRecord(IdRecord record, TvdbCandidates candidates)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(candidates);

        var byLink = record.NewIds.GetValueOrDefault("TvdbByLink");
        var alternative = candidates.Alternatives.FirstOrDefault(a => string.Equals(a.TvdbId, byLink, StringComparison.Ordinal)) ?? candidates.Alternatives.FirstOrDefault();
        var outcome = Enum.TryParse<TvdbOutcome>(record.NewIds.GetValueOrDefault("TvdbOutcome"), out var parsed) ? parsed : TvdbOutcome.Kept;

        return new TvdbChoice(
            outcome == TvdbOutcome.Switched && alternative != null ? alternative.TvdbId : candidates.Primary!,
            candidates,
            alternative,
            outcome,
            record.Reason);
    }

    /// <summary>
    /// The id to put on a show given what it carries, or <c>null</c> to leave it.
    /// </summary>
    /// <remarks>
    /// An id that is neither candidate is not the plugin's to touch: the owner or the server's
    /// own provider put it there.
    /// </remarks>
    /// <param name="stored">The TVDB id the show carries, if any.</param>
    /// <param name="choice">The choice.</param>
    /// <returns>The id to write, or <c>null</c>.</returns>
    internal static string? Replacement(string? stored, TvdbChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);

        if (string.IsNullOrEmpty(stored))
        {
            return choice.Chosen;
        }

        return string.Equals(stored, choice.Candidates.Primary, StringComparison.Ordinal)
            && !string.Equals(choice.Chosen, choice.Candidates.Primary, StringComparison.Ordinal)
                ? choice.Chosen
                : null;
    }

    /// <summary>
    /// The record of a choice, for the Mapping tab.
    /// </summary>
    /// <param name="path">The show's folder.</param>
    /// <param name="itemName">The show's name.</param>
    /// <param name="idsBefore">The ids the show carried before the choice was applied.</param>
    /// <param name="choice">The choice.</param>
    /// <param name="existing">The record the show already had.</param>
    /// <param name="nowUtc">The time now.</param>
    /// <returns>The record.</returns>
    internal static IdRecord ToRecord(
        string path,
        string itemName,
        IReadOnlyDictionary<string, string> idsBefore,
        TvdbChoice choice,
        IdRecord? existing,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(idsBefore);
        ArgumentNullException.ThrowIfNull(choice);

        var alternative = choice.Alternative ?? choice.Candidates.Alternatives.FirstOrDefault();
        var newIds = IdCheck.RecordedIds(idsBefore);

        // An empty value is stored as null by the database and read back as no key, so none is written.
        void Put(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                newIds[key] = value;
            }
        }

        Put("Tvdb", choice.Chosen);
        Put("TvdbByAniDb", choice.Candidates.Primary);
        Put("TvdbByLink", alternative?.TvdbId);
        Put("TvdbLink", alternative?.Link);
        Put("TvdbOutcome", choice.Outcome.ToString());

        return new IdRecord
        {
            Id = path,
            ItemName = itemName,
            Status = IdRecord.TvdbConflict,
            OldIds = IdCheck.RecordedIds(idsBefore),
            NewIds = newIds,
            Reason = choice.Reason,
            CreatedUtc = existing?.CreatedUtc ?? nowUtc,
            UpdatedUtc = nowUtc
        };
    }

    /// <summary>
    /// Whether a failure asking a source says the thing asked about is not there.
    /// </summary>
    /// <remarks>
    /// Walks the failure and what caused it, since the request gate wraps what the provider threw
    /// and the servers' providers throw their own types: Emby's HTTP exception is neither
    /// <see cref="HttpRequestException"/> nor ours, so its status is read by name.
    /// </remarks>
    /// <param name="ex">The failure.</param>
    /// <returns><c>true</c> where the answer was a 404 or 410.</returns>
    internal static bool IsNotFound(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is SourceBlockedException { Block: { } block } && (block.Reason == BlockReason.NotFound || block.HttpStatus is 404 or 410))
            {
                return true;
            }

            if (current is SourceHttpException http && http.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return true;
            }

            if (current is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone })
            {
                return true;
            }

            if (StatusByName(current) is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The label of a link for a phrase: "MyAnimeList 59415" or "AniList 179876".
    /// </summary>
    /// <param name="link">The link as the mappings key it.</param>
    /// <returns>The label.</returns>
    internal static string LinkLabel(string link)
    {
        var split = link.IndexOf(':', StringComparison.Ordinal);

        if (split < 0)
        {
            return link;
        }

        return (link[..split] switch { "mal" => "MyAnimeList", "anilist" => "AniList", var other => other }) + " " + link[(split + 1)..];
    }

    private static TvdbChoice Unknown(TvdbCandidates candidates)
    {
        var first = candidates.Alternatives[0];

        return new TvdbChoice(
            candidates.Primary!,
            candidates,
            first,
            TvdbOutcome.Unknown,
            string.Format(CultureInfo.InvariantCulture, "TheTVDB could not be asked; the AniDB mapping {0} is kept, {1} says {2}", candidates.Primary, LinkLabel(first.Link), first.TvdbId));
    }

    private static HttpStatusCode? StatusByName(Exception ex)
    {
        try
        {
            var property = ex.GetType().GetProperties().FirstOrDefault(p => p.Name == "StatusCode" && p.GetIndexParameters().Length == 0);

            return property?.GetValue(ex) switch
            {
                HttpStatusCode status => status,
                int number => (HttpStatusCode)number,
                _ => null
            };
        }
        catch (Exception reflection) when (reflection is System.Reflection.TargetInvocationException or InvalidCastException or MethodAccessException)
        {
            return null;
        }
    }

    private static async Task<TvdbExistence> Exists(string tvdbId, string itemName, string? language, CancellationToken cancellationToken)
    {
        // The delegated call answers null for a provider that is not there and for one with
        // nothing to say, so a provider that cannot be asked is told apart first.
        if (!Plugin.Instance.Configuration.GetSource(SourceIds.Tvdb).Enabled
            || DelegatedProviders.Find<Series, SeriesInfo>(SourceIds.Tvdb) == null)
        {
            return TvdbExistence.Unknown;
        }

        // No name: a lookup with one falls back to a search by name where the id finds nothing,
        // and a search hit would read as the series existing.
        var lookup = new SeriesInfo { MetadataLanguage = language ?? "en" };
        lookup.ProviderIds["Tvdb"] = tvdbId;

        try
        {
            // Its own key, never the show's: a 404 blocks the key it was asked under for days,
            // and the show itself is fine.
            var result = await DelegatedProviders.GetMetadata<Series, SeriesInfo>(
                SourceIds.Tvdb,
                lookup,
                "tvdb-series:" + tvdbId,
                itemName + " (TVDB " + tvdbId + ")",
                "Exists",
                cancellationToken).ConfigureAwait(false);

            // Jellyfin's TheTVDB plugin answers an error with metadata and no name.
            return string.IsNullOrEmpty(result?.Item?.Name) ? TvdbExistence.Missing : TvdbExistence.Exists;
        }
        catch (SourceBlockedException ex)
        {
            return IsNotFound(ex) ? TvdbExistence.Missing : TvdbExistence.Unknown;
        }
    }
}
