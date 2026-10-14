using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace AniMetaPlus.Sources;

/// <summary>
/// What AniDB's cached document says beyond what the series provider writes onto the show:
/// the ids the other sites file the anime under, and how many votes its rating is from.
/// </summary>
/// <param name="MalIds">The MyAnimeList ids, lowest first.</param>
/// <param name="AnnIds">The Anime News Network ids.</param>
/// <param name="RatingVotes">How many votes the permanent rating is from.</param>
/// <param name="Type">The format, such as "TV Series" or "Movie".</param>
public sealed record AniDbExtras(IReadOnlyList<string> MalIds, IReadOnlyList<string> AnnIds, int? RatingVotes, string? Type)
{
    /// <summary>
    /// Gets nothing, for an anime with no cached document.
    /// </summary>
    public static AniDbExtras None { get; } = new([], [], null, null);

    /// <summary>
    /// Reads them from a cached document. Costs no request.
    /// </summary>
    /// <param name="seriesXmlPath">The path of the cached <c>series.xml</c>.</param>
    /// <returns>What was found.</returns>
    public static AniDbExtras Read(string seriesXmlPath)
    {
        if (!File.Exists(seriesXmlPath))
        {
            return None;
        }

        try
        {
            var root = XDocument.Load(seriesXmlPath).Root;
            if (root == null)
            {
                return None;
            }

            // AniDB's resource types: 1 Anime News Network, 2 MyAnimeList.
            List<string> Identifiers(string type) => root
                .Elements("resources")
                .Elements("resource")
                .Where(r => (string?)r.Attribute("type") == type)
                .Elements("externalentity")
                .Elements("identifier")
                .Select(i => i.Value.Trim())
                .Where(v => v.Length > 0 && v.All(char.IsDigit))
                .Distinct()
                .OrderBy(v => long.Parse(v, CultureInfo.InvariantCulture))
                .ToList();

            int? votes = int.TryParse((string?)root.Element("ratings")?.Element("permanent")?.Attribute("count"), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                ? count
                : null;

            return new AniDbExtras(Identifiers("2"), Identifiers("1"), votes, root.Element("type")?.Value);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException)
        {
            return None;
        }
    }
}
