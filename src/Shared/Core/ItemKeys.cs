using System;
using System.IO;

namespace AniMetaPlus.Core;

/// <summary>
/// The keys the database files items under. Built from what does not change when a library is
/// rescanned or restored - an AniDB id, or failing that the folder or file name - never from the
/// server's own item ids, which do.
/// </summary>
public static class ItemKeys
{
    /// <summary>
    /// Gets the key of an identified anime.
    /// </summary>
    /// <param name="animeId">The AniDB id.</param>
    /// <returns>The key.</returns>
    public static string ForAnime(string animeId) => "anidb:" + animeId;

    /// <summary>
    /// Gets the key of an item nothing identified, from its path.
    /// </summary>
    /// <param name="kind">"Series" or "Movie".</param>
    /// <param name="path">The folder of a series, or the file of a movie.</param>
    /// <param name="name">The item's name, used when there is no path.</param>
    /// <returns>The key.</returns>
    public static string ForUnidentified(string kind, string? path, string? name)
    {
        var leaf = string.IsNullOrEmpty(path)
            ? name
            : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.Equals(kind, "Movie", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(leaf))
        {
            leaf = Path.GetFileNameWithoutExtension(leaf);
        }

        return kind.ToLowerInvariant() + ":" + (leaf ?? string.Empty).Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Gets the AniDB id a key names, when it names one.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The AniDB id, or <c>null</c>.</returns>
    public static string? AnimeId(string key)
        => key.StartsWith("anidb:", StringComparison.Ordinal) ? key[6..] : null;
}
