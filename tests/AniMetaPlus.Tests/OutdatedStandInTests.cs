using System;
using System.IO;
using AniMetaPlus.Configuration;
using AniMetaPlus.Data;
using AniMetaPlus.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AniMetaPlus.Tests;

[Collection("AniMetaDb")]
public sealed class OutdatedStandInTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "animetaplus-standin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Theory]
    [InlineData("AniDB", "MyAnimeList", "AniDB,MyAnimeList,AniList", true)]
    [InlineData("AniDB", "MyAnimeList", "MyAnimeList,AniList,AniDB", false)]
    [InlineData("AniDB", "MyAnimeList", "MyAnimeList,AniList", false)]
    [InlineData("AniDB", "AniList,MyAnimeList", "AniList,AniDB,MyAnimeList", false)]
    [InlineData("AniDB", "AniList,MyAnimeList", "AniDB,AniList,MyAnimeList", true)]
    [InlineData("AniDB", "", "MyAnimeList,AniDB", true)]
    public void WaitsOnlyWhileTheSourceStillComesFirst(string waitedFor, string current, string order, bool waits)
        => Assert.Equal(waits, SeriesMerge.StillWaitsFor(waitedFor, current, order.Split(',')));

    // Stand-ins recorded during a ban, under an order with AniDB first, spent a whole AniDB burst
    // on reviews against the owner's new order with MyAnimeList first.
    [Fact]
    public void SettlesStandInsAndDropsReviewsTheNewOrderNoLongerBacks()
    {
        var configuration = new PluginConfiguration();
        configuration.Priorities.Add(new KindPriority { Kind = "Titles", Sources = "MyAnimeList,AniList,AniDB" });
        configuration.Priorities.Add(new KindPriority { Kind = "Overview", Sources = "AniDB,MyAnimeList" });

        using var db = AniMetaDb.Open(_folder, NullLogger.Instance);
        db.Provenance.Upsert(new ProvenanceRecord { Id = "anidb:1|Titles", ItemKey = "anidb:1", Field = "Titles", Source = "MyAnimeList", PreferredSource = "AniDB", PinnedSource = "MyAnimeList", Provisional = true });
        db.Provenance.Upsert(new ProvenanceRecord { Id = "anidb:2|Titles", ItemKey = "anidb:2", Field = "Titles", Source = "MyAnimeList", PreferredSource = "AniDB", PinnedSource = "MyAnimeList", Provisional = true });
        db.Reviews.Upsert(new ReviewRecord { Id = "anidb:2|Titles", ItemKey = "anidb:2", Field = "Titles", CurrentSource = "MyAnimeList", ProposedSource = "AniDB", Status = ReviewStatus.Pending });
        db.Provenance.Upsert(new ProvenanceRecord { Id = "anidb:3|Overview", ItemKey = "anidb:3", Field = "Overview", Source = "MyAnimeList", PreferredSource = "AniDB", PinnedSource = "MyAnimeList", Provisional = true });

        var (settled, dropped) = SeriesMerge.SettleOutdated(db, configuration);

        Assert.Equal((2, 1), (settled, dropped));
        Assert.False(db.Provenance.FindById("anidb:1|Titles").Provisional);
        Assert.False(db.Provenance.FindById("anidb:2|Titles").Provisional);
        Assert.Null(db.Reviews.FindById("anidb:2|Titles"));

        // AniDB still comes first for the overview: that stand-in keeps waiting.
        Assert.True(db.Provenance.FindById("anidb:3|Overview").Provisional);
    }
}
