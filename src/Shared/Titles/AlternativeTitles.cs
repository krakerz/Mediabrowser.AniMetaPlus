using System;
using System.Collections.Generic;
using System.Linq;
using AniMetaPlus.Providers;
using AniMetaPlus.Sources;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace AniMetaPlus.Titles;

/// <summary>
/// Puts an anime's other titles into its original title. Emby and Jellyfin search only the name
/// and the original title, and match any word in it, so this is where "danmachi" has to be for
/// "Dungeon ni Deai wo Motomeru no wa Machigatteiru Darou ka" to be found by it.
/// </summary>
public static class AlternativeTitles
{
    /// <summary>
    /// The most titles put together.
    /// </summary>
    public const int MaxTitles = 6;

    /// <summary>
    /// The longest the joined original title gets, in characters. Both servers show it on the
    /// item's page.
    /// </summary>
    public const int MaxLength = 250;

    private const string Separator = " / ";

    /// <summary>
    /// Joins an anime's titles into one original title: the original one first, then the short
    /// titles people search by, the official English title and the synonyms. Repeats and the
    /// displayed name are left out.
    /// </summary>
    /// <param name="original">The original title already set, usually the romaji main title.</param>
    /// <param name="displayName">The name the item is shown under, which search already finds.</param>
    /// <param name="titles">The anime's titles from the dump.</param>
    /// <returns>The joined title, or <paramref name="original"/> when there is nothing to add.</returns>
    public static string? Join(string? original, string? displayName, IEnumerable<DumpTitle> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var picked = new List<string>();

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            seen.Add(Key(displayName));
        }

        void Add(string? title)
        {
            if (string.IsNullOrWhiteSpace(title) || picked.Count >= MaxTitles)
            {
                return;
            }

            title = Clean(title);
            var length = picked.Sum(t => t.Length + Separator.Length) + title.Length;
            if (length > MaxLength || !seen.Add(Key(title)))
            {
                return;
            }

            picked.Add(title);
        }

        // The original title keeps its place even when it is also the displayed name: the
        // field reads as the original title first.
        var first = !string.IsNullOrWhiteSpace(original) ? original : titles.FirstOrDefault(t => t.Type == "main")?.Title;
        if (!string.IsNullOrWhiteSpace(first))
        {
            first = Clean(first);
            seen.Add(Key(first));
            picked.Add(first);
        }

        foreach (var type in new[] { "short", "official", "main", "syn" })
        {
            foreach (var title in titles.Where(t => t.Type == type && (type != "official" || t.Language == "en")))
            {
                Add(title.Title);
            }
        }

        return picked.Count > 1 ? string.Join(Separator, picked) : original;
    }

    // AniDB writes an apostrophe as a backtick; the other sources use straight or curly quotes.
    private static string Clean(string title) => title.Trim().Replace('`', '\'').Replace('\u2019', '\'');

    // Titles that differ only in case, quotes or spacing are the same title.
    private static string Key(string title)
        => string.Join(' ', Clean(title).ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The original title an item should have: its other titles joined in when the owner asked
    /// for searchable titles, or only the first of them when not. Based on the first title of
    /// what it has, so running it again changes nothing.
    /// </summary>
    /// <param name="current">The original title the item has.</param>
    /// <param name="displayName">The name it is shown under.</param>
    /// <param name="titles">The anime's titles from the dump.</param>
    /// <param name="searchable">Whether the other titles are wanted.</param>
    /// <returns>The original title it should have.</returns>
    public static string? Wanted(string? current, string? displayName, IReadOnlyList<DumpTitle> titles, bool searchable)
    {
        ArgumentNullException.ThrowIfNull(titles);

        var first = string.IsNullOrWhiteSpace(current)
            ? titles.FirstOrDefault(t => t.Type == "main")?.Title
            : current.Split(Separator)[0].Trim();

        return searchable && titles.Count > 0 ? Join(first, displayName, titles) : first;
    }

    /// <summary>
    /// Collects an anime's titles without asking anyone: AniDB's from the local dump, then
    /// AniList's and MyAnimeList's from their cached answers, however old. The cached answers
    /// cover what AniDB does not have, a show with no AniDB entry among them.
    /// </summary>
    /// <param name="paths">The server paths.</param>
    /// <param name="ids">The item's provider ids.</param>
    /// <returns>The titles, AniDB's first.</returns>
    public static IReadOnlyList<DumpTitle> Gather(IApplicationPaths paths, IReadOnlyDictionary<string, string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var titles = new List<DumpTitle>();

        if (ids.TryGetValue(ProviderNames.AniDb, out var animeId) && !string.IsNullOrEmpty(animeId))
        {
            titles.AddRange(TitlesDump.SearchTitles(paths, animeId));
        }

        ids.TryGetValue(SourceIds.AniList, out var aniListId);
        ids.TryGetValue(SourceIds.Mal, out var malId);

        var aniList = !string.IsNullOrEmpty(aniListId) ? SourceCache.Read(SourceIds.AniList, "id-" + aniListId, TimeSpan.MaxValue)
            : !string.IsNullOrEmpty(malId) ? SourceCache.Read(SourceIds.AniList, "mal-" + malId, TimeSpan.MaxValue)
            : null;
        var mal = !string.IsNullOrEmpty(malId) ? SourceCache.Read(SourceIds.Mal, "id-" + malId, TimeSpan.MaxValue) : null;

        foreach (var record in new[] { aniList, mal })
        {
            if (record == null)
            {
                continue;
            }

            titles.AddRange(FromRecord(record));
        }

        return titles;
    }

    /// <summary>
    /// A cached answer's titles in the dump's terms: its romaji title as the main one, its
    /// English title as the official one, and its synonyms.
    /// </summary>
    /// <param name="record">The cached answer.</param>
    /// <returns>The titles.</returns>
    public static IEnumerable<DumpTitle> FromRecord(AnimeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!string.IsNullOrWhiteSpace(record.RomajiTitle))
        {
            yield return new DumpTitle("main", "x-jat", record.RomajiTitle);
        }

        if (!string.IsNullOrWhiteSpace(record.EnglishTitle))
        {
            yield return new DumpTitle("official", "en", record.EnglishTitle);
        }

        foreach (var synonym in record.Synonyms ?? [])
        {
            yield return new DumpTitle("syn", "en", synonym);
        }
    }

    /// <summary>
    /// Sets the joined original title on an item, when the owner asked for searchable titles.
    /// Reads only the local dump and cached answers.
    /// </summary>
    /// <param name="item">The item the provider is about to return.</param>
    /// <param name="paths">The server paths.</param>
    public static void Apply(BaseItem? item, IApplicationPaths paths)
    {
        if (item == null || !Plugin.Instance.Configuration.SearchableAlternativeTitles)
        {
            return;
        }

        var titles = Gather(paths, item.ProviderIds);
        if (titles.Count > 0)
        {
            item.OriginalTitle = Join(item.OriginalTitle, item.Name, titles);
        }
    }
}
