using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Configuration;
#if JELLYFIN
using Jellyfin.Data.Enums;
#endif
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Sources;

/// <summary>
/// What Anime News Network adds to a show or movie AniDB links an entry for: staff, studios, the
/// English dub cast, a rating and English episode titles. It adds to what the configured sources
/// produced and never replaces it.
/// </summary>
public static partial class AnnExtras
{
    /// <summary>
    /// The fewest votes an ANN score may be from to count. A score from a handful of voters
    /// says little, and ANN's own bayesian score is then close to the site's average anyway.
    /// </summary>
    internal const int MinimumVotes = 10;

    // ANN's tasks, mapped to the kind of credit a person gets. Compared without regard to case.
    private static readonly Dictionary<string, string> _staffKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Director"] = "Director",
        ["Series Director"] = "Director",
        ["Script"] = "Writer",
        ["Series Composition"] = "Writer",
        ["Screenplay"] = "Writer",
        ["Original creator"] = "Writer",
        ["Original Work"] = "Writer",
        ["Music"] = "Composer",
        ["Producer"] = "Producer"
    };

    /// <summary>
    /// Gets the ANN entry the item is linked to, from its ids.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="context">The item's context.</param>
    /// <returns>The id, or <c>null</c>.</returns>
    internal static string? IdOf(BaseItem? item, FetchContext context)
    {
        var id = context.Ids.GetValueOrDefault(SourceIds.Ann);

        if (string.IsNullOrEmpty(id))
        {
            id = item?.GetProviderId(SourceIds.Ann);
        }

        return AnnClient.IsId(id) ? id : null;
    }

    /// <summary>
    /// Asks ANN about the item's entry. No request at all when the extras are turned off or the
    /// item has no ANN id.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="context">The item's context.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The record, or <c>null</c>.</returns>
    public static async Task<AnnRecord?> Load(BaseItem? item, FetchContext context, ILogger logger, CancellationToken cancellationToken)
        => Plugin.Instance.Configuration.UseAnnExtras && IdOf(item, context) is { } id
            ? await AnnClient.Get(id, logger, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>
    /// Puts ANN's staff, studios and, where wanted, English dub cast onto the result, after what
    /// the sources gave it. Names the item already has are skipped.
    /// </summary>
    /// <typeparam name="TItem">The kind of item.</typeparam>
    /// <param name="result">The result, already merged.</param>
    /// <param name="ann">What ANN knows.</param>
    /// <param name="configuration">The settings.</param>
    public static void Apply<TItem>(MetadataResult<TItem> result, AnnRecord ann, PluginConfiguration configuration)
        where TItem : BaseItem
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(ann);
        ArgumentNullException.ThrowIfNull(configuration);

        var known = new HashSet<string>(StringComparer.Ordinal);

        foreach (var person in result.People ?? [])
        {
            known.Add(SeriesMerge.NameKey(person.Name));
        }

        var people = Staff(ann).ToList();

        // Where cast is listed as characters, a voice actor listed under their own name would mix
        // two ways of listing in one list, as the other sources' actors would.
        if (configuration.AnnEnglishCast && !configuration.CastShowsCharacters)
        {
            people.AddRange(EnglishCast(ann));
        }

        foreach (var person in people)
        {
            if (!known.Add(SeriesMerge.NameKey(person.Name)))
            {
                continue;
            }

            result.AddPerson(new PersonInfo
            {
                Name = person.Name,
                Role = person.Role,
                Type = HostPeople.ToHost(Enum.TryParse<PersonKind>(person.Kind, true, out var parsed) ? parsed : PersonKind.Unknown)
            });
        }

        if (result.Item is { } item)
        {
            var studios = Studios(ann).Where(s => !(item.Studios ?? []).Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();

            if (studios.Count > 0)
            {
                item.Studios = [.. item.Studios ?? [], .. studios];
            }
        }
    }

    /// <summary>
    /// ANN's staff as credits, for the tasks that map to a kind of credit the servers know.
    /// </summary>
    /// <param name="ann">What ANN knows.</param>
    /// <returns>The credits, each name once.</returns>
    internal static IReadOnlyList<PersonRecord> Staff(AnnRecord ann)
    {
        var people = new List<PersonRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in ann.Staff)
        {
            var task = ParentheticalRegex().Replace(member.Task ?? string.Empty, string.Empty).Trim();

            if (!_staffKinds.TryGetValue(task, out var kind) || string.IsNullOrWhiteSpace(member.Name) || !seen.Add(SeriesMerge.NameKey(member.Name)))
            {
                continue;
            }

            people.Add(new PersonRecord { Name = member.Name.Trim(), Role = task, Kind = kind });
        }

        return people;
    }

    /// <summary>
    /// The English dub cast as actors, each credited with the character.
    /// </summary>
    /// <param name="ann">What ANN knows.</param>
    /// <returns>The actors, each name once.</returns>
    internal static IReadOnlyList<PersonRecord> EnglishCast(AnnRecord ann)
    {
        var people = new List<PersonRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in ann.Cast)
        {
            if (!string.Equals(member.Language?.Trim(), "EN", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(member.Name)
                || !seen.Add(SeriesMerge.NameKey(member.Name)))
            {
                continue;
            }

            people.Add(new PersonRecord { Name = member.Name.Trim(), Role = member.Role?.Trim() ?? string.Empty, Kind = "Actor" });
        }

        return people;
    }

    /// <summary>
    /// The studios that animated the anime, or, where none is credited for that, those that produced it.
    /// </summary>
    /// <param name="ann">What ANN knows.</param>
    /// <returns>The company names, each once.</returns>
    internal static IReadOnlyList<string> Studios(AnnRecord ann)
    {
        IReadOnlyList<string> Of(string task) => ann.Companies
            .Where(c => string.Equals(c.Task?.Trim(), task, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => c.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var animation = Of("Animation Production");

        return animation.Count > 0 ? animation : Of("Production");
    }

    /// <summary>
    /// ANN's score as one more contributor to the combined rating, weighed by its votes the way
    /// every other source is.
    /// </summary>
    /// <param name="ann">What ANN knows.</param>
    /// <returns>The contributor, or <c>null</c> when ANN has no score or too few votes.</returns>
    internal static AnimeRecord? RatingRecord(AnnRecord ann)
        => ann.Rating is > 0 && ann.Votes is >= MinimumVotes
            ? new AnimeRecord { Source = SourceIds.Ann, SourceId = ann.Id, Rating = ann.Rating, Votes = ann.Votes, FetchedUtc = ann.FetchedUtc }
            : null;

    /// <summary>
    /// ANN's English title for an episode. Only an episode numbered with plain digits matches:
    /// ANN also numbers "1.5" and "S1", which AniDB numbers differently.
    /// </summary>
    /// <param name="ann">What ANN knows.</param>
    /// <param name="number">The episode's number within the entry.</param>
    /// <returns>The title, or <c>null</c>.</returns>
    internal static string? EpisodeTitle(AnnRecord ann, int number)
        => ann.Episodes.FirstOrDefault(e =>
            !string.IsNullOrWhiteSpace(e.EnglishTitle)
            && e.Number.All(char.IsAsciiDigit)
            && int.TryParse(e.Number, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n == number)?.EnglishTitle;

    /// <summary>
    /// Whether episode titles are wanted in English: the library's language, with the episode
    /// titles following it, and that language English.
    /// </summary>
    /// <param name="preference">The episode title preference in force.</param>
    /// <param name="metadataLanguage">The language the library is in.</param>
    /// <returns><c>true</c> when an English title is the one wanted.</returns>
    internal static bool WantsEnglishTitles(TitlePreferenceType preference, string? metadataLanguage)
    {
        if (preference != TitlePreferenceType.Localized)
        {
            return false;
        }

        var language = metadataLanguage?.Trim() ?? string.Empty;

        return language.Length == 0
            || string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("en-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether an episode's title says nothing: empty, or only "Episode 5", which is what AniDB
    /// names an episode it has no title for.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <returns><c>true</c> when ANN's title may take its place.</returns>
    internal static bool IsPlaceholderTitle(string? title)
        => string.IsNullOrWhiteSpace(title) || PlaceholderRegex().IsMatch(title.Trim());

    /// <summary>
    /// The number an episode's cached document is for, where it is an ordinary episode.
    /// Specials, credits, trailers and parodies carry a letter in the number and have none.
    /// </summary>
    /// <param name="fileName">The document's file name, such as <c>episode-5.xml</c>.</param>
    /// <returns>The number within the entry, or <c>null</c>.</returns>
    internal static int? RegularEpisodeNumber(string fileName)
        => RegularFileRegex().Match(fileName) is { Success: true } match
            && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;

    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex ParentheticalRegex();

    [GeneratedRegex(@"^(?:episode|ep\.?)\s*\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^episode-(\d+)\.xml$", RegexOptions.CultureInvariant)]
    private static partial Regex RegularFileRegex();
}
