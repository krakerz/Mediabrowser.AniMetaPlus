using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using AniMetaPlus.Data;

namespace AniMetaPlus.Core;

/// <summary>
/// What the grouping needs to know about a library anime.
/// </summary>
/// <param name="Name">The item's name.</param>
/// <param name="Premiere">The item's premiere date, when it has one.</param>
public sealed record FranchiseNode(string Name, DateTime? Premiere);

/// <summary>
/// A franchise found among the library's anime.
/// </summary>
/// <param name="Key">The lowest AniDB id in the component, which names the franchise between runs.</param>
/// <param name="Name">The name of the member that premiered first.</param>
/// <param name="AnimeIds">The AniDB ids of the library's anime in it, lowest first.</param>
public sealed record Franchise(string Key, string Name, IReadOnlyList<string> AnimeIds);

/// <summary>
/// A franchise as the server's items: what the collection should hold.
/// </summary>
/// <param name="Key">The franchise key.</param>
/// <param name="Name">The collection's name.</param>
/// <param name="ItemIds">The server ids of the items it should hold.</param>
public sealed record FranchiseTarget(string Key, string Name, IReadOnlyList<string> ItemIds);

/// <summary>
/// What a franchise sync did.
/// </summary>
public sealed class FranchiseSyncResult
{
    /// <summary>Gets the records to keep, in full.</summary>
    public List<FranchiseCollectionRecord> Records { get; } = [];

    /// <summary>Gets or sets how many collections were made.</summary>
    public int Created { get; set; }

    /// <summary>Gets or sets how many existing collections changed.</summary>
    public int Updated { get; set; }

    /// <summary>Gets or sets how many items were added.</summary>
    public int Added { get; set; }

    /// <summary>Gets or sets how many items were removed.</summary>
    public int Removed { get; set; }
}

/// <summary>
/// Groups anime into franchises by the relations in the cached AniDB answers, and keeps one
/// collection per franchise.
/// </summary>
public static class Franchises
{
    private static readonly HashSet<string> _joiningRelations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sequel", "Prequel", "Side Story", "Parent Story", "Summary", "Full Story"
    };

    /// <summary>
    /// Whether a relation type puts two anime in the same franchise.
    /// </summary>
    /// <param name="type">The AniDB relation type.</param>
    /// <returns><c>true</c> for sequels, prequels, side and parent stories, summaries and full stories.</returns>
    public static bool Joins(string type) => _joiningRelations.Contains(type);

    /// <summary>
    /// Reads the relations of a cached AniDB answer, without asking anyone.
    /// </summary>
    /// <param name="seriesDataPath">The cached series.xml.</param>
    /// <returns>Each related anime's id and relation type, or <c>null</c> when the file cannot be read.</returns>
    public static IReadOnlyList<(string Id, string Type)>? ReadRelations(string seriesDataPath)
    {
        var settings = new XmlReaderSettings
        {
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            ValidationType = ValidationType.None
        };

        try
        {
            var result = new List<(string, string)>();
            using var stream = new StreamReader(seriesDataPath, System.Text.Encoding.UTF8);
            using var reader = XmlReader.Create(stream, settings);
            reader.MoveToContent();

            var inRelated = false;

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "relatedanime")
                {
                    break;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.Name == "relatedanime")
                {
                    inRelated = !reader.IsEmptyElement;
                    if (!inRelated)
                    {
                        break;
                    }
                }
                else if (inRelated && reader.Name == "anime")
                {
                    var id = reader.GetAttribute("id");
                    var type = reader.GetAttribute("type");
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(type))
                    {
                        result.Add((id, type));
                    }
                }
                else if (reader.Name is "characters" or "episodes")
                {
                    // The bulk of the document, after the relations.
                    break;
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the franchises: the connected components, through the relations that join, that
    /// hold two or more library anime. Relations are followed through anime outside the library
    /// as long as they have a cached answer.
    /// </summary>
    /// <param name="library">The library's anime by AniDB id.</param>
    /// <param name="relations">An anime's relations, or <c>null</c> when it has no cached answer.</param>
    /// <returns>The franchises, lowest key first.</returns>
    public static IReadOnlyList<Franchise> Group(
        IReadOnlyDictionary<string, FranchiseNode> library,
        Func<string, IReadOnlyList<(string Id, string Type)>?> relations)
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        string Find(string id)
        {
            while (parent[id] != id)
            {
                parent[id] = parent[parent[id]];
                id = parent[id];
            }

            return id;
        }

        void Visit(string id)
        {
            if (parent.TryAdd(id, id))
            {
                queue.Enqueue(id);
            }
        }

        foreach (var id in library.Keys)
        {
            Visit(id);
        }

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var (related, type) in relations(id) ?? [])
            {
                if (!Joins(type) || related == id)
                {
                    continue;
                }

                Visit(related);
                parent[Find(id)] = Find(related);
            }
        }

        var result = new List<Franchise>();

        foreach (var component in parent.Keys.GroupBy(Find))
        {
            var members = component.Where(library.ContainsKey).OrderBy(id => id, IdComparer.Instance).ToList();
            if (members.Count < 2)
            {
                continue;
            }

            var key = component.OrderBy(id => id, IdComparer.Instance).First();
            result.Add(new Franchise(key, NameOf(members, library), members));
        }

        return result.OrderBy(f => f.Key, IdComparer.Instance).ToList();
    }

    /// <summary>
    /// Makes the collections match the franchises: makes missing ones, adds and removes the
    /// items it put there itself, and leaves every collection it did not make alone.
    /// </summary>
    /// <param name="host">The server's collections.</param>
    /// <param name="targets">What each franchise's collection should hold.</param>
    /// <param name="records">The collections made on earlier runs.</param>
    /// <param name="saved">Called with each record as soon as its collection is done, so that a failed run does not forget a collection it made.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was done, with the records to keep.</returns>
    public static async Task<FranchiseSyncResult> Sync(
        IHostCollections host,
        IReadOnlyList<FranchiseTarget> targets,
        IReadOnlyList<FranchiseCollectionRecord> records,
        Action<FranchiseCollectionRecord>? saved,
        CancellationToken cancellationToken)
    {
        var result = new FranchiseSyncResult();
        var unclaimed = records.ToList();

        void Keep(FranchiseCollectionRecord record)
        {
            result.Records.Add(record);
            saved?.Invoke(record);
        }

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // By key, else by the most items shared: the key moves when an anime joins or leaves.
            var record = unclaimed.FirstOrDefault(r => r.Id == target.Key)
                ?? unclaimed
                    .Select(r => (Record: r, Shared: r.MemberIds.Intersect(target.ItemIds).Count()))
                    .Where(x => x.Shared > 0)
                    .OrderByDescending(x => x.Shared)
                    .Select(x => x.Record)
                    .FirstOrDefault();

            if (record != null)
            {
                unclaimed.Remove(record);
            }

            var state = record == null ? null : host.FindCollection(record.CollectionId);

            if (record == null || state == null)
            {
                // New, or deleted by the owner and still a franchise.
                var id = await host.CreateCollection(target.Name, target.ItemIds, cancellationToken).ConfigureAwait(false);
                result.Created++;
                result.Added += target.ItemIds.Count;
                Keep(new FranchiseCollectionRecord { Id = target.Key, CollectionId = id, Name = target.Name, MemberIds = [.. target.ItemIds] });
                continue;
            }

            var changed = false;
            var toAdd = target.ItemIds.Except(state.MemberIds).ToList();

            // Only what the task put there: an item the owner added is theirs.
            var toRemove = record.MemberIds.Except(target.ItemIds).Intersect(state.MemberIds).ToList();
            var name = record.Name;

            if (toAdd.Count > 0)
            {
                await host.AddToCollection(record.CollectionId, toAdd, cancellationToken).ConfigureAwait(false);
                result.Added += toAdd.Count;
                changed = true;
            }

            if (toRemove.Count > 0)
            {
                await host.RemoveFromCollection(record.CollectionId, toRemove, cancellationToken).ConfigureAwait(false);
                result.Removed += toRemove.Count;
                changed = true;
            }

            // A name that is no longer the one the task set was changed by the owner.
            if (state.Name == record.Name && record.Name != target.Name)
            {
                await host.RenameCollection(record.CollectionId, target.Name, cancellationToken).ConfigureAwait(false);
                name = target.Name;
                changed = true;
            }

            if (changed)
            {
                result.Updated++;
            }

            Keep(new FranchiseCollectionRecord { Id = target.Key, CollectionId = record.CollectionId, Name = name, MemberIds = [.. target.ItemIds] });
        }

        // Franchises that no longer exist keep their record, and their collection.
        result.Records.AddRange(unclaimed);

        return result;
    }

    private static string NameOf(List<string> members, IReadOnlyDictionary<string, FranchiseNode> library)
    {
        var first = members
            .Where(id => library[id].Premiere != null)
            .OrderBy(id => library[id].Premiere)
            .ThenBy(id => id, IdComparer.Instance)
            .FirstOrDefault() ?? members[0];

        return library[first].Name;
    }

    /// <summary>
    /// Orders AniDB ids by number, so that 99 comes before 100.
    /// </summary>
    private sealed class IdComparer : IComparer<string>
    {
        public static readonly IdComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var parsed = long.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var a);
            if (parsed && long.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var b))
            {
                return a.CompareTo(b);
            }

            return string.CompareOrdinal(x, y);
        }
    }
}
