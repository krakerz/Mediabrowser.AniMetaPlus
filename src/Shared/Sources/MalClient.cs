using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AniMetaPlus.Sources;

/// <summary>
/// Reads anime from MyAnimeList, through its official API when a client id is set and through
/// Jikan otherwise, where the owner allowed it.
/// </summary>
public static class MalClient
{
    private const string OfficialEndpoint = "https://api.myanimelist.net/v2/anime/";
    private const string JikanEndpoint = "https://api.jikan.moe/v4/anime/";

    private const string OfficialFields = "id,title,main_picture,alternative_titles,start_date,end_date,synopsis,mean,num_scoring_users,nsfw,genres,num_episodes,studios";

    /// <summary>
    /// Gets a value indicating whether MyAnimeList can be asked at all with the settings as they are.
    /// </summary>
    public static bool IsConfigured
    {
        get
        {
            var configuration = Plugin.Instance.Configuration;

            return !string.IsNullOrWhiteSpace(configuration.MalClientId) || configuration.MalUseJikan;
        }
    }

    /// <summary>
    /// Reads an anime by its MyAnimeList id.
    /// </summary>
    /// <param name="httpClient">The client.</param>
    /// <param name="malId">The id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The record.</returns>
    public static Task<AnimeRecord> Get(HttpClient httpClient, string malId, CancellationToken cancellationToken)
    {
        var clientId = Plugin.Instance.Configuration.MalClientId;

        return string.IsNullOrWhiteSpace(clientId)
            ? GetJikan(httpClient, malId, cancellationToken)
            : GetOfficial(httpClient, clientId.Trim(), malId, cancellationToken);
    }

    /// <summary>
    /// Finds an anime's MyAnimeList id by title, preferring the one that aired in the given year.
    /// </summary>
    /// <param name="httpClient">The client.</param>
    /// <param name="title">The title.</param>
    /// <param name="year">The year, when known.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The id.</returns>
    /// <exception cref="KeyNotFoundException">Nothing matched.</exception>
    public static async Task<string> Search(HttpClient httpClient, string title, int? year, CancellationToken cancellationToken)
    {
        var clientId = Plugin.Instance.Configuration.MalClientId;
        var official = !string.IsNullOrWhiteSpace(clientId);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            official
                ? "https://api.myanimelist.net/v2/anime?limit=10&fields=start_date&q=" + Uri.EscapeDataString(Truncate(title))
                : "https://api.jikan.moe/v4/anime?limit=10&q=" + Uri.EscapeDataString(title));

        if (official)
        {
            request.Headers.Add("X-MAL-CLIENT-ID", clientId.Trim());
        }

        request.Headers.Accept.ParseAdd("application/json");

        using var document = await Send(httpClient, request, official ? "MyAnimeList" : "Jikan", cancellationToken).ConfigureAwait(false);
        var candidates = new List<(string Id, int? Year)>();

        foreach (var entry in Array(document.RootElement, "data"))
        {
            // The official API wraps each result in a node; Jikan does not.
            var node = entry.TryGetProperty("node", out var inner) ? inner : entry;
            var id = Int(node, "id") ?? Int(node, "mal_id");
            if (id is null)
            {
                continue;
            }

            var start = Str(node, "start_date") ?? (node.TryGetProperty("aired", out var aired) ? Str(aired, "from") : null);
            candidates.Add((id.Value.ToString(CultureInfo.InvariantCulture), ParseDate(start)?.Year));
        }

        if (candidates.Count == 0)
        {
            throw new KeyNotFoundException("MyAnimeList has no anime called " + title + ".");
        }

        // The search ranks by relevance; the year only breaks a tie between remakes and sequels.
        var match = year is { } y ? candidates.FirstOrDefault(c => c.Year == y) : default;

        return match.Id ?? candidates[0].Id;
    }

    // The official search rejects queries over 64 characters.
    private static string Truncate(string title) => title.Length > 64 ? title[..64] : title;

    /// <summary>
    /// Checks a client id by reading one well known anime with it.
    /// </summary>
    /// <param name="httpClient">The client.</param>
    /// <param name="clientId">The client id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static Task Test(HttpClient httpClient, string clientId, CancellationToken cancellationToken)
        => GetOfficial(httpClient, clientId.Trim(), "1", cancellationToken);

    private static async Task<AnimeRecord> GetOfficial(HttpClient httpClient, string clientId, string malId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OfficialEndpoint + Uri.EscapeDataString(malId) + "?fields=" + OfficialFields);
        request.Headers.Add("X-MAL-CLIENT-ID", clientId);
        request.Headers.Accept.ParseAdd("application/json");

        using var document = await Send(httpClient, request, "MyAnimeList", cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        var record = new AnimeRecord
        {
            Source = SourceIds.Mal,
            SourceId = malId,
            RomajiTitle = Str(root, "title"),
            Overview = Clean(Str(root, "synopsis")),
            StartDate = ParseDate(Str(root, "start_date")),
            EndDate = ParseDate(Str(root, "end_date")),
            Rating = Double(root, "mean"),
            Votes = Int(root, "num_scoring_users"),
            Episodes = Int(root, "num_episodes"),
            IsAdult = Str(root, "nsfw") is "black",
            FetchedUtc = DateTime.UtcNow
        };

        record.Ids[SourceIds.Mal] = malId;

        if (root.TryGetProperty("alternative_titles", out var alternatives) && alternatives.ValueKind == JsonValueKind.Object)
        {
            record.EnglishTitle = NullIfEmpty(Str(alternatives, "en"));
            record.NativeTitle = NullIfEmpty(Str(alternatives, "ja"));
        }

        foreach (var genre in Array(root, "genres"))
        {
            if (Str(genre, "name") is { Length: > 0 } name)
            {
                record.Genres.Add(new WeightedName { Name = name, Weight = 1, IsAdult = name is "Hentai" or "Erotica" });
            }
        }

        foreach (var studio in Array(root, "studios"))
        {
            if (Str(studio, "name") is { Length: > 0 } name)
            {
                record.Studios.Add(name);
            }
        }

        if (root.TryGetProperty("main_picture", out var picture) && (Str(picture, "large") ?? Str(picture, "medium")) is { Length: > 0 } url)
        {
            record.Images.Add(new ImageRecord { Type = "Primary", Url = url });
        }

        return record;
    }

    private static async Task<AnimeRecord> GetJikan(HttpClient httpClient, string malId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, JikanEndpoint + Uri.EscapeDataString(malId) + "/full");
        request.Headers.Accept.ParseAdd("application/json");

        using var document = await Send(httpClient, request, "Jikan", cancellationToken).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new KeyNotFoundException("MyAnimeList has no such anime.");
        }

        var record = new AnimeRecord
        {
            Source = SourceIds.Mal,
            SourceId = malId,
            RomajiTitle = Str(data, "title"),
            EnglishTitle = NullIfEmpty(Str(data, "title_english")),
            NativeTitle = NullIfEmpty(Str(data, "title_japanese")),
            Overview = Clean(Str(data, "synopsis")),
            Rating = Double(data, "score"),
            Votes = Int(data, "scored_by"),
            Episodes = Int(data, "episodes"),
            FetchedUtc = DateTime.UtcNow
        };

        record.Ids[SourceIds.Mal] = malId;

        if (data.TryGetProperty("aired", out var aired) && aired.ValueKind == JsonValueKind.Object)
        {
            record.StartDate = ParseDate(Str(aired, "from"));
            record.EndDate = ParseDate(Str(aired, "to"));
        }

        foreach (var list in new[] { "genres", "themes", "demographics", "explicit_genres" })
        {
            foreach (var genre in Array(data, list))
            {
                if (Str(genre, "name") is { Length: > 0 } name)
                {
                    record.Genres.Add(new WeightedName { Name = name, Weight = list == "genres" ? 1 : 0.8, IsAdult = list == "explicit_genres" });
                }
            }
        }

        foreach (var studio in Array(data, "studios"))
        {
            if (Str(studio, "name") is { Length: > 0 } name)
            {
                record.Studios.Add(name);
            }
        }

        if (data.TryGetProperty("images", out var images)
            && images.TryGetProperty("jpg", out var jpg)
            && (Str(jpg, "large_image_url") ?? Str(jpg, "image_url")) is { Length: > 0 } url)
        {
            record.Images.Add(new ImageRecord { Type = "Primary", Url = url });
        }

        record.IsAdult = Str(data, "rating")?.StartsWith("Rx", StringComparison.OrdinalIgnoreCase) == true;

        return record;
    }

    private static async Task<JsonDocument> Send(HttpClient httpClient, HttpRequestMessage request, string name, CancellationToken cancellationToken)
    {
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new KeyNotFoundException(name + " has no such anime.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SourceHttpException(
                name + " answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
                response.StatusCode,
                response.Headers.RetryAfter?.Delta ?? (response.StatusCode == HttpStatusCode.TooManyRequests ? TimeSpan.FromMinutes(1) : null));
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return JsonDocument.Parse(text);
    }

    private static string? Clean(string? synopsis)
    {
        if (string.IsNullOrWhiteSpace(synopsis))
        {
            return null;
        }

        // MyAnimeList closes with a credit line in square brackets.
        var text = synopsis.Trim();
        var credit = text.LastIndexOf("[Written by", StringComparison.OrdinalIgnoreCase);

        return (credit > 0 ? text[..credit] : text).Trim();
    }

    private static DateTime? ParseDate(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) ? date : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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

    private static double? Double(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
