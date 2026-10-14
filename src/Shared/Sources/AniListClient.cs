using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AniMetaPlus.Sources;

/// <summary>
/// Reads anime from AniList's public GraphQL API, which needs no key.
/// </summary>
public static partial class AniListClient
{
    private const string Endpoint = "https://graphql.anilist.co";

    private const string MediaFields = """
        id idMal isAdult episodes format
        title { romaji english native }
        description(asHtml: false)
        startDate { year month day }
        endDate { year month day }
        genres
        tags { name rank isAdult isMediaSpoiler }
        averageScore
        stats { scoreDistribution { score amount } }
        studios(isMain: true) { nodes { name } }
        coverImage { extraLarge large }
        bannerImage
        characters(sort: [ROLE, RELEVANCE], perPage: 25) {
          edges { role node { name { full } image { large } } voiceActors(language: JAPANESE, sort: RELEVANCE) { id name { full } image { large } } }
        }
        staff(sort: RELEVANCE, perPage: 25) { edges { role node { id name { full } image { large } } } }
        """;

    /// <summary>
    /// Reads an anime by its MyAnimeList id, the one id AniList is looked up by that the other
    /// sources know.
    /// </summary>
    /// <param name="httpClient">The client.</param>
    /// <param name="malId">The MyAnimeList id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The record.</returns>
    /// <exception cref="KeyNotFoundException">AniList has no anime with that id.</exception>
    public static Task<AnimeRecord> GetByMalId(HttpClient httpClient, string malId, CancellationToken cancellationToken)
        => Query(
            httpClient,
            "query ($id: Int) { Media(idMal: $id, type: ANIME) { " + MediaFields + " } }",
            new Dictionary<string, object> { ["id"] = int.Parse(malId, CultureInfo.InvariantCulture) },
            cancellationToken);

    /// <summary>
    /// Reads an anime by AniList's own id.
    /// </summary>
    /// <param name="httpClient">The client.</param>
    /// <param name="aniListId">The AniList id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The record.</returns>
    public static Task<AnimeRecord> GetById(HttpClient httpClient, string aniListId, CancellationToken cancellationToken)
        => Query(
            httpClient,
            "query ($id: Int) { Media(id: $id, type: ANIME) { " + MediaFields + " } }",
            new Dictionary<string, object> { ["id"] = int.Parse(aniListId, CultureInfo.InvariantCulture) },
            cancellationToken);

    /// <summary>
    /// Finds an anime by title, preferring the one that aired in the given year.
    /// </summary>
    /// <param name="httpClient">The client.</param>
    /// <param name="title">The title.</param>
    /// <param name="year">The year, when known.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The record.</returns>
    public static Task<AnimeRecord> Search(HttpClient httpClient, string title, int? year, CancellationToken cancellationToken)
    {
        var variables = new Dictionary<string, object> { ["search"] = title };
        var yearFilter = string.Empty;

        if (year is { } y)
        {
            variables["year"] = y * 10000;
            variables["yearEnd"] = (y + 1) * 10000;
            yearFilter = ", startDate_greater: $year, startDate_lesser: $yearEnd";
        }

        var declarations = year is null ? "$search: String" : "$search: String, $year: FuzzyDateInt, $yearEnd: FuzzyDateInt";

        return Query(
            httpClient,
            "query (" + declarations + ") { Media(search: $search, type: ANIME" + yearFilter + ") { " + MediaFields + " } }",
            variables,
            cancellationToken);
    }

    /// <summary>
    /// Lists the anime whose titles match, for the owner to pick from.
    /// </summary>
    /// <param name="httpClient">The client.</param>
    /// <param name="title">The title.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matches, most relevant first.</returns>
    public static async Task<IReadOnlyList<RemoteCandidate>> SearchMany(HttpClient httpClient, string title, CancellationToken cancellationToken)
    {
        using var document = await Post(
            httpClient,
            "query ($search: String) { Page(perPage: 10) { media(search: $search, type: ANIME) { id title { romaji english } startDate { year } format } } }",
            new Dictionary<string, object> { ["search"] = title },
            cancellationToken).ConfigureAwait(false);

        var found = new List<RemoteCandidate>();

        if (document.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("Page", out var page)
            && page.TryGetProperty("media", out var media)
            && media.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in media.EnumerateArray())
            {
                if (Int(entry, "id") is not { } id)
                {
                    continue;
                }

                var titles = entry.TryGetProperty("title", out var t) ? t : default;
                var name = (titles.ValueKind == JsonValueKind.Object ? Str(titles, "romaji") ?? Str(titles, "english") : null) ?? id.ToString(CultureInfo.InvariantCulture);
                var year = entry.TryGetProperty("startDate", out var start) && start.ValueKind == JsonValueKind.Object ? Int(start, "year") : null;

                found.Add(new RemoteCandidate(SourceIds.AniList, id.ToString(CultureInfo.InvariantCulture), name, year, Str(entry, "format"), null));
            }
        }

        return found;
    }

    private static async Task<AnimeRecord> Query(HttpClient httpClient, string query, Dictionary<string, object> variables, CancellationToken cancellationToken)
    {
        using var document = await Post(httpClient, query, variables, cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("Media", out var media)
            || media.ValueKind != JsonValueKind.Object)
        {
            throw new KeyNotFoundException("AniList has no such anime.");
        }

        return Read(media);
    }

    private static async Task<JsonDocument> Post(HttpClient httpClient, string query, Dictionary<string, object> variables, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { query, variables });
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // AniList reports a missing anime as a 404 inside a GraphQL error, sometimes with a 200.
        if (response.StatusCode == HttpStatusCode.NotFound || text.Contains("\"status\":404", StringComparison.Ordinal))
        {
            throw new KeyNotFoundException("AniList has no such anime.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SourceHttpException(
                "AniList answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                response.StatusCode,
                response.Headers.RetryAfter?.Delta ?? (response.StatusCode == HttpStatusCode.TooManyRequests ? TimeSpan.FromMinutes(1) : null));
        }

        return JsonDocument.Parse(text);
    }

    /// <summary>
    /// Reads a Media object into a record.
    /// </summary>
    /// <param name="media">The Media object.</param>
    /// <returns>The record.</returns>
    internal static AnimeRecord Read(JsonElement media)
    {
        var record = new AnimeRecord
        {
            Source = SourceIds.AniList,
            SourceId = Int(media, "id")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            IsAdult = Bool(media, "isAdult"),
            Episodes = Int(media, "episodes"),
            FetchedUtc = DateTime.UtcNow
        };

        record.Ids[SourceIds.AniList] = record.SourceId;
        if (Int(media, "idMal") is { } malId)
        {
            record.Ids[SourceIds.Mal] = malId.ToString(CultureInfo.InvariantCulture);
        }

        if (media.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.Object)
        {
            record.RomajiTitle = Str(title, "romaji");
            record.EnglishTitle = Str(title, "english");
            record.NativeTitle = Str(title, "native");
        }

        record.Overview = CleanDescription(Str(media, "description"));
        record.StartDate = Date(media, "startDate");
        record.EndDate = Date(media, "endDate");

        foreach (var genre in Array(media, "genres"))
        {
            if (genre.GetString() is { Length: > 0 } name)
            {
                record.Genres.Add(new WeightedName { Name = name, Weight = 1 });
            }
        }

        foreach (var tag in Array(media, "tags"))
        {
            if (Bool(tag, "isMediaSpoiler") == true || Str(tag, "name") is not { Length: > 0 } name)
            {
                continue;
            }

            record.Genres.Add(new WeightedName
            {
                Name = name,
                Weight = (Int(tag, "rank") ?? 50) / 100d,
                IsAdult = Bool(tag, "isAdult") == true
            });
        }

        if (Int(media, "averageScore") is { } score)
        {
            record.Rating = score / 10d;

            if (media.TryGetProperty("stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
            {
                record.Votes = Array(stats, "scoreDistribution").Sum(d => Int(d, "amount") ?? 0);
            }
        }

        if (media.TryGetProperty("studios", out var studios) && studios.ValueKind == JsonValueKind.Object)
        {
            foreach (var node in Array(studios, "nodes"))
            {
                if (Str(node, "name") is { Length: > 0 } name)
                {
                    record.Studios.Add(name);
                }
            }
        }

        if (media.TryGetProperty("coverImage", out var cover) && cover.ValueKind == JsonValueKind.Object
            && (Str(cover, "extraLarge") ?? Str(cover, "large")) is { Length: > 0 } coverUrl)
        {
            record.Images.Add(new ImageRecord { Type = "Primary", Url = coverUrl });
        }

        if (Str(media, "bannerImage") is { Length: > 0 } banner)
        {
            record.Images.Add(new ImageRecord { Type = "Banner", Url = banner });
        }

        ReadCharacters(media, record);
        ReadStaff(media, record);

        return record;
    }

    private static void ReadCharacters(JsonElement media, AnimeRecord record)
    {
        if (!media.TryGetProperty("characters", out var characters) || characters.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var edge in Array(characters, "edges"))
        {
            var character = edge.TryGetProperty("node", out var node) && node.TryGetProperty("name", out var name)
                ? Str(name, "full")
                : null;

            foreach (var actor in Array(edge, "voiceActors"))
            {
                var actorName = actor.TryGetProperty("name", out var n) ? Str(n, "full") : null;
                if (string.IsNullOrEmpty(actorName))
                {
                    continue;
                }

                var person = new PersonRecord
                {
                    Name = actorName,
                    Role = character ?? string.Empty,
                    Kind = "Actor",
                    ImageUrl = actor.TryGetProperty("image", out var image) ? Str(image, "large") : null
                };

                if (Int(actor, "id") is { } id)
                {
                    person.Ids[SourceIds.AniList] = id.ToString(CultureInfo.InvariantCulture);
                }

                record.People.Add(person);

                // The first voice actor is the main one; the rest are dubs and stand-ins.
                break;
            }
        }
    }

    private static void ReadStaff(JsonElement media, AnimeRecord record)
    {
        if (!media.TryGetProperty("staff", out var staff) || staff.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var edge in Array(staff, "edges"))
        {
            if (!edge.TryGetProperty("node", out var node) || !node.TryGetProperty("name", out var name) || Str(name, "full") is not { Length: > 0 } fullName)
            {
                continue;
            }

            var role = Str(edge, "role") ?? string.Empty;
            var person = new PersonRecord
            {
                Name = fullName,
                Role = role,
                Kind = StaffKind(role),
                ImageUrl = node.TryGetProperty("image", out var image) ? Str(image, "large") : null
            };

            if (Int(node, "id") is { } id)
            {
                person.Ids[SourceIds.AniList] = id.ToString(CultureInfo.InvariantCulture);
            }

            record.People.Add(person);
        }
    }

    /// <summary>
    /// Sorts an AniList staff role into a kind of credit.
    /// </summary>
    /// <param name="role">The role, such as "Director" or "Original Creator".</param>
    /// <returns>The kind, a <c>PersonKind</c> name.</returns>
    internal static string StaffKind(string role)
    {
        if (role.Contains("Director", StringComparison.OrdinalIgnoreCase))
        {
            return "Director";
        }

        if (role.Contains("Music", StringComparison.OrdinalIgnoreCase) || role.Contains("Composer", StringComparison.OrdinalIgnoreCase))
        {
            return "Composer";
        }

        if (role.Contains("Original", StringComparison.OrdinalIgnoreCase)
            || role.Contains("Script", StringComparison.OrdinalIgnoreCase)
            || role.Contains("Composition", StringComparison.OrdinalIgnoreCase)
            || role.Contains("Screenplay", StringComparison.OrdinalIgnoreCase))
        {
            return "Writer";
        }

        if (role.Contains("Design", StringComparison.OrdinalIgnoreCase) || role.Contains("Art", StringComparison.OrdinalIgnoreCase))
        {
            return "Artist";
        }

        if (role.Contains("Producer", StringComparison.OrdinalIgnoreCase))
        {
            return "Producer";
        }

        return "Unknown";
    }

    /// <summary>
    /// Turns AniList's description, which carries a little HTML and a source line, into text.
    /// </summary>
    /// <param name="description">The description.</param>
    /// <returns>The text, or <c>null</c> when there is none.</returns>
    internal static string? CleanDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var text = LineBreakRegex().Replace(description, "\n");
        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = SourceLineRegex().Replace(text, string.Empty);
        text = BlankLinesRegex().Replace(text, "\n\n");

        return text.Trim();
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? Bool(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static DateTime? Date(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var date) || date.ValueKind != JsonValueKind.Object || Int(date, "year") is not { } year)
        {
            return null;
        }

        return new DateTime(year, Int(date, "month") ?? 1, Int(date, "day") ?? 1, 0, 0, 0, DateTimeKind.Utc);
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\(Source:[^)]*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SourceLineRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();
}
