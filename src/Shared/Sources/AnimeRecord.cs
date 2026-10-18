using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;

namespace AniMetaPlus.Sources;

/// <summary>
/// A name with how strongly the source attaches it, from 0 to 1.
/// </summary>
public sealed class WeightedName
{
    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the weight, from 0 to 1.</summary>
    public double Weight { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether the source marks it adult.</summary>
    public bool IsAdult { get; set; }
}

/// <summary>
/// A credit, as any source gives it.
/// </summary>
public sealed class PersonRecord
{
    /// <summary>Gets or sets the person's name, given name first.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the role, or for an actor the character.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of credit, a <c>PersonKind</c> name.</summary>
    public string Kind { get; set; } = "Actor";

    /// <summary>Gets or sets the picture.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Gets or sets the ids the source gives the person, keyed by provider.</summary>
    public Dictionary<string, string> Ids { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// An image a source offers.
/// </summary>
public sealed class ImageRecord
{
    /// <summary>Gets or sets the kind of image: Primary, Backdrop, Banner, Logo.</summary>
    public string Type { get; set; } = "Primary";

    /// <summary>Gets or sets the url.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the width, when known.</summary>
    public int? Width { get; set; }

    /// <summary>Gets or sets the height, when known.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets the language, when it has text.</summary>
    public string? Language { get; set; }
}

/// <summary>
/// What one source knows about one anime, in the same shape whichever source it came from, so
/// that the sources can be merged.
/// </summary>
public sealed class AnimeRecord
{
    /// <summary>Gets or sets the source id.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the source's id for the anime.</summary>
    public string SourceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the title in romaji.</summary>
    public string? RomajiTitle { get; set; }

    /// <summary>Gets or sets the English title.</summary>
    public string? EnglishTitle { get; set; }

    /// <summary>Gets or sets the title in Japanese script.</summary>
    public string? NativeTitle { get; set; }

    /// <summary>Gets or sets the other names the source knows the anime by.</summary>
    public List<string> Synonyms { get; set; } = [];

    /// <summary>Gets or sets the synopsis, as plain text.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the first air date.</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>Gets or sets the last air date.</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>Gets or sets the genres and tags.</summary>
    public List<WeightedName> Genres { get; set; } = [];

    /// <summary>Gets or sets the studios.</summary>
    public List<string> Studios { get; set; } = [];

    /// <summary>Gets or sets the score, from 0 to 10.</summary>
    public double? Rating { get; set; }

    /// <summary>Gets or sets how many votes the score is from.</summary>
    public int? Votes { get; set; }

    /// <summary>Gets or sets the cast and staff.</summary>
    public List<PersonRecord> People { get; set; } = [];

    /// <summary>Gets or sets the images.</summary>
    public List<ImageRecord> Images { get; set; } = [];

    /// <summary>Gets or sets the ids of the anime on other sites, keyed by provider.</summary>
    public Dictionary<string, string> Ids { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets a value indicating whether the source marks the anime adult.</summary>
    public bool? IsAdult { get; set; }

    /// <summary>Gets or sets the episode count.</summary>
    public int? Episodes { get; set; }

    /// <summary>Gets or sets when it was fetched.</summary>
    public DateTime FetchedUtc { get; set; }

    /// <summary>
    /// Gets the title a preference picks: <c>JapaneseRomaji</c>, <c>Japanese</c>, or the English
    /// one for anything else, each falling back to the others.
    /// </summary>
    /// <param name="preference">The title preference name.</param>
    /// <returns>The title, or <c>null</c> when the source has none.</returns>
    public string? Title(string preference) => preference switch
    {
        "JapaneseRomaji" => RomajiTitle ?? EnglishTitle ?? NativeTitle,
        "Japanese" => NativeTitle ?? RomajiTitle ?? EnglishTitle,
        _ => EnglishTitle ?? RomajiTitle ?? NativeTitle
    };
}

/// <summary>
/// Keeps what other sources answered on disk, for as long as AniDB's own documents are kept,
/// so that a refresh does not ask them again.
/// </summary>
public static class SourceCache
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    /// <summary>
    /// Reads a cached record.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="key">The source's id for the anime.</param>
    /// <param name="maxAge">How old it may be.</param>
    /// <returns>The record, or <c>null</c> when none is cached or it is too old.</returns>
    public static AnimeRecord? Read(string source, string key, TimeSpan maxAge)
    {
        var path = PathOf(source, key);
        var info = new FileInfo(path);

        if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > maxAge)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AnimeRecord>(File.ReadAllText(path), _json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Caches a record.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="key">The source's id for the anime.</param>
    public static void Write(AnimeRecord record, string key)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            var path = PathOf(record.Source, key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".partial", JsonSerializer.Serialize(record, _json), new UTF8Encoding(false));
            File.Move(path + ".partial", path, true);
        }
        catch (IOException)
        {
            // Not cached means asked again next time, which is all that is lost.
        }
    }

    private static string PathOf(string source, string key)
    {
        var safe = new StringBuilder();
        foreach (var c in key)
        {
            safe.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        }

        return Path.Combine(
            PluginPaths.CacheFolder(Plugin.Instance.AppPaths),
            "sources",
            source.ToLowerInvariant(),
            safe.ToString().ToLower(CultureInfo.InvariantCulture) + ".json");
    }
}
