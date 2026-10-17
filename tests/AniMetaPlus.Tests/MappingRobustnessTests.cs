using System;
using System.IO;
using System.Text;
using AniMetaPlus.Providers.AniDB.Mapping;
using AniMetaPlus.Providers.AniDB.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AniMetaPlus.Tests;

/// <summary>
/// Tests for reading hand-written mapping files and for picking an entry within a season.
/// </summary>
public class MappingRobustnessTests
{
    private const string Mappings = """
        {
          "$meta": { "schema_version": "3.0.0" },
          "anidb:11021:R": { "mal:29575": { "1-4": "1-4" } },
          "anidb:11023:R": { "mal:40000": { "1-4": "1-4" } }
        }
        """;

    /// <summary>
    /// A file saved with a byte order mark, as Notepad does, reads like any other.
    /// </summary>
    [Fact]
    public void AFileWithAByteOrderMarkIsRead()
    {
        var index = ParseBytes([.. new UTF8Encoding(true).GetPreamble(), .. Encoding.UTF8.GetBytes(Mappings)]);

        Assert.Equal("11021", index.AnimeIdByLink("mal", "29575"));
    }

    /// <summary>
    /// A value that is not an object is stepped over, so that the entries after it are still read.
    /// </summary>
    [Fact]
    public void AValueOfTheWrongShapeDoesNotEndTheRead()
    {
        var index = ParseBytes(Encoding.UTF8.GetBytes("""
            {
              "$meta": { "schema_version": "3.0.0" },
              "anidb:11020:R": { "tvdb_show:1:s1": [ 1, 2 ], "tmdb_show:1:s1": [ 1 ], "tmdb_movie:5": [ 1 ] },
              "anidb:11021:R": [ "mal:29575" ],
              "anidb:11023:R": { "mal:40000": { "1-4": "1-4" } }
            }
            """));

        Assert.Equal("11023", index.AnimeIdByLink("mal", "40000"));
    }

    /// <summary>
    /// An episode in the gap between two ranges of a season belongs to neither.
    /// </summary>
    [Fact]
    public void AnEpisodeInAGapBetweenSegmentsHasNoSegment()
    {
        var segments = new[]
        {
            new AniDbSeasonSegment("1", 1, 7),
            new AniDbSeasonSegment("2", 9, 9),
        };

        Assert.Equal("1", AniDbSeasonResolver.PickSegment(segments, 7)?.AnimeId);
        Assert.Null(AniDbSeasonResolver.PickSegment(segments, 8));
        Assert.Equal("2", AniDbSeasonResolver.PickSegment(segments, 9)?.AnimeId);
        Assert.Equal("2", AniDbSeasonResolver.PickSegment(segments, 99)?.AnimeId);
    }

    private static AniBridgeIndex ParseBytes(byte[] bytes)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, bytes);

            return AniBridgeIndex.Parse(path, NullLogger.Instance, DateTime.UtcNow, "test");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
