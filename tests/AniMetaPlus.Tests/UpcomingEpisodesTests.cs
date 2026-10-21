using System;
using System.Collections.Generic;
using System.Linq;
using AniMetaPlus.Providers.AniDB.Mapping;
using AniMetaPlus.Providers.AniDB.Metadata;
using MediaBrowser.Model.Providers;
using Xunit;

namespace AniMetaPlus.Tests;

/// <summary>
/// Tests for the upcoming-episodes helpers: renumbering TVDB's list to the library's seasons,
/// reading cached AniDB episodes, and keeping answers for a while.
/// </summary>
public class UpcomingEpisodesTests
{
    private static readonly DateTime Aired = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static RemoteSearchResult Ep(int season, int number)
    {
        var result = new RemoteSearchResult { ParentIndexNumber = season, IndexNumber = number, Name = $"S{season}E{number}", PremiereDate = Aired };
        result.ProviderIds["Tvdb"] = $"{season}{number}";

        return result;
    }

    private static List<RemoteSearchResult> Show() => [Ep(0, 1), Ep(1, 1), Ep(1, 12), Ep(2, 1), Ep(2, 13), Ep(2, 14)];

    private static string[] Numbers(IEnumerable<RemoteSearchResult> results)
        => results.Select(r => $"{r.ParentIndexNumber}x{r.IndexNumber}").ToArray();

    /// <summary>
    /// The base show keeps TVDB's seasons and its specials.
    /// </summary>
    [Fact]
    public void BaseShowPassesThroughWithSpecials()
    {
        var result = UpcomingEpisodes.Renumber(Show(), new MappedRole(1, 0, false, false));

        Assert.NotNull(result);
        Assert.Equal(["0x1", "1x1", "1x12", "2x1", "2x13", "2x14"], Numbers(result));
        Assert.All(result, r => Assert.Equal("AniMeta+", r.SearchProviderName));
        Assert.Equal("01", result[0].ProviderIds["Tvdb"]);
    }

    /// <summary>
    /// A sequel kept as its own show takes the later part of its TVDB season, counted from 1.
    /// </summary>
    [Fact]
    public void SequelSeasonIsRenumberedFromItsOffset()
    {
        var result = UpcomingEpisodes.Renumber(Show(), new MappedRole(2, 12, false, false));

        Assert.NotNull(result);
        Assert.Equal(["1x1", "1x2"], Numbers(result));
        Assert.Equal("Tvdb", result[0].ProviderIds.Keys.Single());
    }

    /// <summary>
    /// A later season with no offset keeps all of that season's episodes.
    /// </summary>
    [Fact]
    public void LaterSeasonWithoutOffsetBecomesSeasonOne()
    {
        var result = UpcomingEpisodes.Renumber(Show(), new MappedRole(2, 0, false, false));

        Assert.Equal(["1x1", "1x13", "1x14"], Numbers(result!));
    }

    /// <summary>
    /// A season 1 entry that starts part-way through the season is cut and renumbered too.
    /// </summary>
    [Fact]
    public void OffsetOnlySeasonOneIsRenumbered()
    {
        var result = UpcomingEpisodes.Renumber(Show(), new MappedRole(1, 6, false, false));

        Assert.Equal(["1x6"], Numbers(result!));
    }

    /// <summary>
    /// Absolute, movie, specials-only and unknown roles have no way to be renumbered.
    /// </summary>
    [Fact]
    public void UnrenumberableRolesAreSkipped()
    {
        Assert.Null(UpcomingEpisodes.Renumber(Show(), new MappedRole(1, 0, true, false)));
        Assert.Null(UpcomingEpisodes.Renumber(Show(), new MappedRole(1, 0, false, true)));
        Assert.Null(UpcomingEpisodes.Renumber(Show(), new MappedRole(null, 0, false, false)));
        Assert.Null(UpcomingEpisodes.Renumber(Show(), new MappedRole(0, 0, false, false)));
        Assert.Null(UpcomingEpisodes.Renumber(Show(), null));
    }

    /// <summary>
    /// Regular episodes with an air date become season 1 results; the rest are left out.
    /// </summary>
    [Fact]
    public void AniDbEpisodesKeepRegularOnesWithDates()
    {
        var cached = new List<UpcomingEpisodes.CachedEpisode>
        {
            new("2", "502", "Two", Aired),
            new("1", "501", "One", Aired),
            new("3", "503", "Undated", null),
            new("S1", "601", "Special", Aired),
            new("C1", "701", "Credits", Aired),
            new(null, null, "No number", Aired),
        };

        var result = UpcomingEpisodes.FromAniDb(cached);

        Assert.Equal(["1x1", "1x2"], Numbers(result));
        Assert.Equal("One", result[0].Name);
        Assert.Equal(Aired, result[0].PremiereDate);
        Assert.Equal("501", result[0].ProviderIds["AniDB"]);
        Assert.Equal("AniMeta+", result[0].SearchProviderName);
    }

    /// <summary>
    /// An answer is served until it expires, then dropped.
    /// </summary>
    [Fact]
    public void CacheServesUntilExpiry()
    {
        var cache = new UpcomingEpisodes.Cache();
        var now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(cache.TryGet("a", now, out _));

        cache.Set("a", [Ep(1, 1)], now, UpcomingEpisodes.AnswerLifetime);
        cache.Set("b", [], now, UpcomingEpisodes.FailureLifetime);

        Assert.True(cache.TryGet("a", now.AddHours(5), out var kept));
        Assert.Single(kept);
        Assert.True(cache.TryGet("b", now.AddMinutes(29), out _));
        Assert.False(cache.TryGet("b", now.AddMinutes(31), out _));
        Assert.False(cache.TryGet("a", now.AddHours(7), out _));
    }
}
