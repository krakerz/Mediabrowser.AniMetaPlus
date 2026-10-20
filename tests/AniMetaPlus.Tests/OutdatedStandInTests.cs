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

    // Accepting AniDB's value under an order with MyAnimeList first raised a new review proposing
    // MyAnimeList on the next refresh: a decided field is reviewed against the source decided about.
    [Theory]
    [InlineData(false, "", "AniDB", "AniDB")]
    [InlineData(false, "", "", "MyAnimeList")]
    [InlineData(true, "AniList", "AniDB", "AniList")]
    [InlineData(true, "", "AniDB", "AniDB")]
    public void ReviewsADecidedFieldAgainstTheSourceDecidedAbout(bool provisional, string waitedFor, string decided, string expected)
    {
        var provenance = new ProvenanceRecord { Provisional = provisional, PreferredSource = waitedFor, DecidedSource = decided, PinnedSource = "AniDB" };

        Assert.Equal(expected, SeriesMerge.ReviewedSource(provenance, ["MyAnimeList", "AniList", "AniDB"]));
    }

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

    // Genres and tags filled from AniList and MyAnimeList while AniDB was blocked lost AniDB's tags
    // for good: a combined field waits for every source in its order, wherever it stands.
    [Fact]
    public void KeepsACombinedFieldWaitingWhereverItsSourceStands()
    {
        var configuration = new PluginConfiguration();
        configuration.Priorities.Add(new KindPriority { Kind = "Genres", Sources = "AniList,AniDB,MyAnimeList" });

        using var db = AniMetaDb.Open(_folder, NullLogger.Instance);
        db.Provenance.Upsert(new ProvenanceRecord { Id = "anidb:1|Genres", ItemKey = "anidb:1", Field = "Genres", Source = "AniList,MyAnimeList", PreferredSource = "AniDB", Provisional = true });

        Assert.Equal((0, 0), SeriesMerge.SettleOutdated(db, configuration));
        Assert.True(db.Provenance.FindById("anidb:1|Genres").Provisional);

        configuration.Priorities[0].Sources = "AniList,MyAnimeList";
        Assert.Equal((1, 0), SeriesMerge.SettleOutdated(db, configuration));
    }

    [Fact]
    public void MarksCombinedFieldsFilledWithoutAniDbAsWaitingForIt()
    {
        var configuration = new PluginConfiguration();
        configuration.Priorities.Add(new KindPriority { Kind = "Genres", Sources = "AniList,AniDB,MyAnimeList" });
        configuration.Priorities.Add(new KindPriority { Kind = "Titles", Sources = "MyAnimeList,AniDB" });
        configuration.Priorities.Add(new KindPriority { Kind = "People", Sources = "AniList,MyAnimeList" });

        using var db = AniMetaDb.Open(_folder, NullLogger.Instance);
        db.Provenance.Upsert(new ProvenanceRecord { Id = "a|Genres", ItemKey = "a", Field = "Genres", Source = "AniList,MyAnimeList" });
        db.Provenance.Upsert(new ProvenanceRecord { Id = "b|Genres", ItemKey = "b", Field = "Genres", Source = "AniList,AniDB,MyAnimeList" });
        db.Provenance.Upsert(new ProvenanceRecord { Id = "c|Titles", ItemKey = "c", Field = "Titles", Source = "MyAnimeList" });
        db.Provenance.Upsert(new ProvenanceRecord { Id = "d|People", ItemKey = "d", Field = "People", Source = "AniList" });
        db.Provenance.Upsert(new ProvenanceRecord { Id = "e|Genres", ItemKey = "e", Field = "Genres", Source = "AniList", Locked = true });

        Assert.Equal(1, SeriesMerge.WaitForMissingContributors(db, configuration, "AniDB"));
        Assert.Equal("AniDB", db.Provenance.FindById("a|Genres").PreferredSource);
        Assert.True(db.Provenance.FindById("a|Genres").Provisional);
        Assert.False(db.Provenance.FindById("b|Genres").Provisional);
        Assert.False(db.Provenance.FindById("c|Titles").Provisional);
        Assert.False(db.Provenance.FindById("d|People").Provisional);
        Assert.False(db.Provenance.FindById("e|Genres").Provisional);
    }

    // Seasons reading their own AniDB entry left stand-ins with no show behind them, which nothing could refresh.
    [Fact]
    public void RemovesStandInsAndReviewsWithNoShowBehindThem()
    {
        using var db = AniMetaDb.Open(_folder, NullLogger.Instance);
        db.Provenance.Upsert(new ProvenanceRecord { Id = "anidb:10376|Genres", ItemKey = "anidb:10376", Field = "Genres", Source = "TheTVDB", PreferredSource = "AniDB", Provisional = true });
        db.Provenance.Upsert(new ProvenanceRecord { Id = "anidb:8692|Genres", ItemKey = "anidb:8692", Field = "Genres", Source = "AniList", PreferredSource = "AniDB", Provisional = true, ItemPath = "/media/anime/Sword Art Online" });
        db.Reviews.Upsert(new ReviewRecord { Id = "anidb:10376|Titles", ItemKey = "anidb:10376", Field = "Titles", Status = ReviewStatus.Pending });

        Assert.Equal(2, SeriesMerge.RemoveOrphans(db));
        Assert.Null(db.Provenance.FindById("anidb:10376|Genres"));
        Assert.NotNull(db.Provenance.FindById("anidb:8692|Genres"));
        Assert.Null(db.Reviews.FindById("anidb:10376|Titles"));
    }

    // Sengoku Youko was identified again from 18543 to 18156; its stand-ins under 18543 stayed and the queue refreshed the show for them every run.
    [Fact]
    public void DropsRecordsKeptForAnAnimeTheShowNoLongerIs()
    {
        using var db = AniMetaDb.Open(_folder, NullLogger.Instance);
        db.Provenance.Upsert(new ProvenanceRecord { Id = "anidb:18543|Genres", ItemKey = "anidb:18543", Field = "Genres", PreferredSource = "AniDB", Provisional = true, ItemPath = "/a/Sengoku Youko (Winter)" });
        db.Reviews.Upsert(new ReviewRecord { Id = "anidb:18543|Genres", ItemKey = "anidb:18543", Field = "Genres", Status = ReviewStatus.Pending, ItemPath = "/a/Sengoku Youko (Winter)" });
        db.Provenance.Upsert(new ProvenanceRecord { Id = "anidb:18156|Genres", ItemKey = "anidb:18156", Field = "Genres", PreferredSource = "AniDB", Provisional = true, ItemPath = "/a/Sengoku Youko (Winter)" });
        db.Provenance.Upsert(new ProvenanceRecord { Id = "anidb:1|Titles", ItemKey = "anidb:1", Field = "Titles", ItemPath = "/a/Gone" });
        db.Provenance.Upsert(new ProvenanceRecord { Id = "mal:5|Titles", ItemKey = "mal:5", Field = "Titles", ItemPath = "/a/Mapped to MyAnimeList" });

        var library = new System.Collections.Generic.Dictionary<string, string> { ["/a/Sengoku Youko (Winter)"] = "18156" };

        Assert.Equal(2, SeriesMerge.RemoveStale(db, library));
        Assert.Null(db.Provenance.FindById("anidb:18543|Genres"));
        Assert.Null(db.Reviews.FindById("anidb:18543|Genres"));
        Assert.NotNull(db.Provenance.FindById("anidb:18156|Genres"));
        Assert.Null(db.Provenance.FindById("anidb:1|Titles"));
        Assert.NotNull(db.Provenance.FindById("mal:5|Titles"));

        // An empty listing - the library could not be read - removes nothing.
        Assert.Equal(0, SeriesMerge.RemoveStale(db, new System.Collections.Generic.Dictionary<string, string>()));
    }
}
