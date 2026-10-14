using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Data;
using AniMetaPlus.Providers;
using AniMetaPlus.Sources;
using AniMetaPlus.Titles;

namespace AniMetaPlus.Core;

/// <summary>
/// The items identified by hand, the ones nothing identified, and the ones waiting for a source.
/// </summary>
public static class Queues
{
    private const int CandidateCount = 8;

    /// <summary>
    /// Gets the AniDB id the owner mapped an unidentified item to.
    /// </summary>
    /// <param name="itemKey">The item's key.</param>
    /// <returns>The AniDB id, or <c>null</c>.</returns>
    public static string? MappedAnimeId(string itemKey)
    {
        var mapping = Db?.Mappings.FindById(itemKey);

        return string.IsNullOrEmpty(mapping?.AnimeId) ? null : mapping.AnimeId;
    }

    /// <summary>
    /// Records an item nothing identified, with the closest titles in the local dump as guesses.
    /// </summary>
    /// <param name="itemKey">The item's key.</param>
    /// <param name="kind">"Series" or "Movie".</param>
    /// <param name="name">The item's name.</param>
    /// <param name="year">The item's year.</param>
    /// <param name="path">The item's path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public static async Task RecordUnmatched(string itemKey, string kind, string? name, int? year, string? path, CancellationToken cancellationToken)
    {
        var db = Db;
        if (db == null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var record = db.Unmatched.FindById(itemKey) ?? new UnmatchedRecord { Id = itemKey, Kind = kind, FirstSeenUtc = now };

        record.Name = name ?? string.Empty;
        record.Year = year;
        record.Path = path ?? string.Empty;
        record.Attempts++;
        record.LastAttemptUtc = now;
        record.PendingRefresh = false;

        if (!string.IsNullOrWhiteSpace(name) && record.Candidates.Count == 0)
        {
            record.Candidates = await Candidates(name, cancellationToken).ConfigureAwait(false);
        }

        db.Unmatched.Upsert(record);
        db.Wrote();
    }

    /// <summary>
    /// Forgets an item that has since been identified.
    /// </summary>
    /// <param name="itemKey">The item's key.</param>
    public static void ClearUnmatched(string itemKey)
    {
        var db = Db;
        if (db != null && db.Unmatched.FindById(itemKey) is { Ignored: false })
        {
            db.Unmatched.Delete(itemKey);
            db.Wrote();
        }
    }

    /// <summary>
    /// Searches the local dump for anime a name could be.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The guesses, closest first.</returns>
    public static async Task<List<CandidateRecord>> Candidates(string name, CancellationToken cancellationToken)
    {
        var paths = Plugin.Instance.AppPaths;
        var ids = new List<string>();

        // The name as it is, then without the notes folders carry in brackets - "(Fall)",
        // "[BD 1080p]" - which no title in the dump has.
        foreach (var query in new[] { name, StripNotes(name) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(query))
            {
                ids.AddRange(await Equals_check.XmlSearch(query, CandidateCount * 3, cancellationToken).ConfigureAwait(false));
            }
        }

        return ids
            .Distinct(StringComparer.Ordinal)
            .Select(id =>
            {
                var title = TitlesDump.MainTitle(paths, id) ?? id;

                return new CandidateRecord { AnimeId = id, Title = title, Score = Math.Max(Similarity(name, title), Similarity(StripNotes(name), title)) };
            })
            .OrderByDescending(c => c.Score)
            .Take(CandidateCount)
            .ToList();
    }

    /// <summary>
    /// Maps an item to an AniDB id by hand.
    /// </summary>
    /// <param name="itemKey">The item's key.</param>
    /// <param name="animeId">The AniDB id.</param>
    /// <param name="note">A note.</param>
    /// <returns>The unmatched record that was waiting, when there was one.</returns>
    public static UnmatchedRecord? Map(string itemKey, string animeId, string note = "")
    {
        var db = Db ?? throw new InvalidOperationException("The database is not available.");
        var unmatched = db.Unmatched.FindById(itemKey);

        db.Mappings.Upsert(new MappingRecord
        {
            Id = itemKey,
            Kind = unmatched?.Kind ?? (itemKey.StartsWith("movie:", StringComparison.Ordinal) ? "Movie" : "Series"),
            AnimeId = animeId,
            ItemName = unmatched?.Name ?? string.Empty,
            Note = note,
            CreatedUtc = DateTime.UtcNow
        });

        if (unmatched != null)
        {
            unmatched.PendingRefresh = true;
            unmatched.Ignored = false;
            db.Unmatched.Upsert(unmatched);
        }

        db.DecisionChanged();

        return unmatched;
    }

    /// <summary>
    /// Puts an item on the list of those waiting for a paused source.
    /// </summary>
    /// <param name="itemKey">The item's key.</param>
    /// <param name="itemName">The item's name.</param>
    /// <param name="itemPath">The item's path.</param>
    /// <param name="source">The source it waits for.</param>
    public static void Wait(string itemKey, string? itemName, string? itemPath, string source)
    {
        var db = Db;
        if (db == null || string.IsNullOrEmpty(itemPath))
        {
            return;
        }

        db.Waiting.Upsert(new WaitingRecord
        {
            Id = itemKey,
            ItemName = itemName ?? string.Empty,
            ItemPath = itemPath,
            Source = source,
            Reason = BlockStore.SourceBlock(source)?.Reason.ToString() ?? "Paused",
            AddedUtc = DateTime.UtcNow
        });
        db.Wrote();
    }

    /// <summary>
    /// Removes what a name carries in brackets, keeping a bracketed year, which tells apart two
    /// shows of one name.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The name without its notes.</returns>
    public static string StripNotes(string name)
    {
        var stripped = System.Text.RegularExpressions.Regex.Replace(name, @"\s*[\(\[](?![12][0-9]{3}[\)\]])[^\)\]]*[\)\]]", string.Empty);

        return System.Text.RegularExpressions.Regex.Replace(stripped, @"\s{2,}", " ").Trim();
    }

    /// <summary>
    /// A rough likeness of two titles, from 0 to 100.
    /// </summary>
    /// <param name="a">One title.</param>
    /// <param name="b">The other.</param>
    /// <returns>The likeness.</returns>
    internal static int Similarity(string a, string b)
    {
        var x = Compact(a);
        var y = Compact(b);

        if (x.Length == 0 || y.Length == 0)
        {
            return 0;
        }

        var distance = Levenshtein(x, y);

        return (int)Math.Round(100d * (1 - ((double)distance / Math.Max(x.Length, y.Length))));
    }

    private static string Compact(string value)
        => new string(value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static AniMetaDb? Db
    {
        get
        {
            try
            {
                return Plugin.Instance is null ? null : AniMetaDb.Current;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null;
            }
        }
    }
}
