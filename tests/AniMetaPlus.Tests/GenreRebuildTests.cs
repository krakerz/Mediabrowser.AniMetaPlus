using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AniMetaPlus.Configuration;
using AniMetaPlus.Core;
using AniMetaPlus.Data;
using AniMetaPlus.Sources;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AniMetaPlus.Tests;

[Collection("AniMetaDb")]
public sealed class GenreRebuildTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "animetaplus-genres-" + Guid.NewGuid().ToString("N"));

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

    private static PluginConfiguration Configuration(string order = "AniDB,MyAnimeList,AniList")
    {
        var configuration = new PluginConfiguration();
        configuration.Priorities.Add(new KindPriority { Kind = "Genres", Sources = order });

        return configuration;
    }

    // What a refresh leaves on the item from AniDB's cached document, as the genre pipeline wrote it.
    private static Series AniDb() => new() { Genres = ["Action", "Anime", "Fantasy"], Tags = ["Action", "Fantasy", "Swordplay", "Magic"] };

    private static AnimeRecord Record(string source, params (string Name, double Weight)[] genres)
    {
        var record = new AnimeRecord { Source = source };
        record.Genres.AddRange(genres.Select(g => new WeightedName { Name = g.Name, Weight = g.Weight }));

        return record;
    }

    private static AnimeRecord AniList() => Record("AniList", ("Adventure", 1), ("Fantasy", 1), ("Isekai", 0.8), ("Gore", 0.3));

    private static AnimeRecord Mal() => Record("MyAnimeList", ("Comedy", 1), ("Isekai", 1));

    // The merge a refresh runs, fed the same answers the way SeriesMerge.Apply feeds it.
    private static (string[] Genres, string[] Tags) FullRefresh(PluginConfiguration configuration, Series aniDb, params AnimeRecord[] others)
    {
        var result = new MetadataResult<Series> { Item = new Series { Genres = aniDb.Genres, Tags = aniDb.Tags } };
        var record = Record("AniDB");
        record.Genres.AddRange(aniDb.Genres.Select(g => new WeightedName { Name = g }));

        // In the order the configuration lists the sources, AniDB first.
        var contributors = new List<AnimeRecord> { record };
        contributors.AddRange(others);
        SeriesMerge.ApplyCombined(DataKind.Genres, result, contributors, record, configuration);

        return (result.Item.Genres, result.Item.Tags);
    }

    [Fact]
    public void BuildsWhatARefreshWouldFromTheSameAnswers()
    {
        var configuration = Configuration("AniDB,AniList,MyAnimeList");
        var expected = FullRefresh(configuration, AniDb(), AniList(), Mal());

        var decision = GenreRebuild.Decide(new Series(), null, new GenreInputs(AniDb(), AniList(), Mal()), configuration);

        Assert.Equal(GenreVerdict.Update, decision.Verdict);
        Assert.Equal(expected.Genres, decision.Genres);
        Assert.Equal(expected.Tags, decision.Tags);
        Assert.Equal(["AniDB", "AniList", "MyAnimeList"], decision.Sources);
    }

    [Fact]
    public void FollowsTheSourceOrderAndThePinnedSource()
    {
        var configuration = Configuration("MyAnimeList,AniList,AniDB");
        var inputs = new GenreInputs(AniDb(), AniList(), Mal());

        Assert.Equal(["MyAnimeList", "AniList", "AniDB"], GenreRebuild.Decide(new Series(), null, inputs, configuration).Sources);
        Assert.Equal(["AniList", "MyAnimeList", "AniDB"], GenreRebuild.Decide(new Series(), new ProvenanceRecord { PinnedSource = "AniList" }, inputs, configuration).Sources);
    }

    [Fact]
    public void LeavesAnItemThatAlreadyHasTheListsUnchanged()
    {
        var configuration = Configuration();
        var inputs = new GenreInputs(AniDb(), null, null);
        var built = GenreRebuild.Decide(new Series(), null, inputs, configuration);
        var item = new Series { Genres = built.Genres!, Tags = built.Tags!.Reverse().ToArray() };

        Assert.Equal(GenreVerdict.Unchanged, GenreRebuild.Decide(item, null, inputs, configuration).Verdict);

        // Genres are an ordered list: the same genres in another order are a change.
        item.Genres = built.Genres!.Reverse().ToArray();
        Assert.Equal(GenreVerdict.Update, GenreRebuild.Decide(item, null, inputs, configuration).Verdict);
    }

    [Fact]
    public void WritesOnlyTheListsTheSettingsImport()
    {
        var configuration = Configuration();
        configuration.ImportTags = false;

        var decision = GenreRebuild.Decide(new Series(), null, new GenreInputs(AniDb(), null, null), configuration);

        Assert.NotNull(decision.Genres);
        Assert.Null(decision.Tags);
    }

    [Fact]
    public void LeavesWhatAnotherSourceHasNothingToSayAboutTheTagsOfAniDbAlone()
    {
        // Genres off, tags on: AniDB has no genres to merge, and a refresh leaves its own tags on the item.
        var configuration = Configuration();
        configuration.ImportGenres = false;
        var aniDb = new Series { Genres = [], Tags = ["Magic", "Swordplay"] };

        var decision = GenreRebuild.Decide(new Series(), null, new GenreInputs(aniDb, null, null), configuration);

        Assert.Equal(GenreVerdict.Update, decision.Verdict);
        Assert.Equal(["Magic", "Swordplay"], decision.Tags!);
        Assert.Null(decision.Genres);
    }

    [Fact]
    public void SkipsLockedAndUnimportedItems()
    {
        var inputs = new GenreInputs(AniDb(), AniList(), Mal());

        Assert.Equal(GenreVerdict.Skipped, GenreRebuild.Decide(new Series(), new ProvenanceRecord { Locked = true }, inputs, Configuration()).Verdict);
        Assert.Equal(GenreVerdict.Skipped, GenreRebuild.Decide(new Series { LockedFields = [MetadataField.Genres] }, null, inputs, Configuration()).Verdict);
        Assert.Equal(GenreVerdict.Skipped, GenreRebuild.Decide(new Series { LockedFields = [MetadataField.Tags] }, null, inputs, Configuration()).Verdict);
        Assert.Equal(GenreVerdict.Skipped, GenreRebuild.Decide(new Series { IsLocked = true }, null, inputs, Configuration()).Verdict);

        var off = Configuration();
        off.ImportGenres = false;
        off.ImportTags = false;
        Assert.Equal(GenreVerdict.Skipped, GenreRebuild.Decide(new Series(), null, inputs, off).Verdict);

        // Other fields locked are no reason to leave the genres.
        Assert.Equal(GenreVerdict.Update, GenreRebuild.Decide(new Series { LockedFields = [MetadataField.Overview] }, null, inputs, Configuration()).Verdict);
    }

    [Fact]
    public void NeverWritesAThinnerListThanTheItemWasBuiltFrom()
    {
        var configuration = Configuration();
        var built = new ProvenanceRecord { Source = "AniDB,AniList" };

        // AniList's answer is gone: the list it contributed to would shrink.
        Assert.Equal(GenreVerdict.Missing, GenreRebuild.Decide(new Series(), built, new GenreInputs(AniDb(), null, null), configuration).Verdict);
        Assert.Equal(GenreVerdict.Missing, GenreRebuild.Decide(new Series(), built, new GenreInputs(null, AniList(), null), configuration).Verdict);
        Assert.Equal(GenreVerdict.Update, GenreRebuild.Decide(new Series(), built, new GenreInputs(AniDb(), AniList(), null), configuration).Verdict);

        // An answer the item was not built from may be missing, and a delegated source never counts.
        Assert.Equal(GenreVerdict.Update, GenreRebuild.Decide(new Series(), new ProvenanceRecord { Source = "AniDB,TheTVDB" }, new GenreInputs(AniDb(), null, null), configuration).Verdict);

        // Nothing recorded and AniDB's document gone: the item may still carry AniDB's tags.
        var anime = new Series();
        anime.SetProviderId("AniDB", "1");
        Assert.Equal(GenreVerdict.Missing, GenreRebuild.Decide(anime, null, new GenreInputs(null, AniList(), null), configuration).Verdict);
        Assert.Equal(GenreVerdict.Update, GenreRebuild.Decide(new Series(), null, new GenreInputs(null, AniList(), null), configuration).Verdict);
    }

    [Fact]
    public void CountsAnItemWithNothingCachedAsMissing()
        => Assert.Equal(GenreVerdict.Missing, GenreRebuild.Decide(new Series(), null, new GenreInputs(null, null, null), Configuration()).Verdict);

    [Fact]
    public void KeepsTheDelegatedSourcesTheOldRecordNamed()
    {
        var decision = GenreRebuild.Decide(new Series(), new ProvenanceRecord { Source = "AniDB,TheTVDB" }, new GenreInputs(AniDb(), null, null), Configuration("AniDB,TheTVDB,AniList"));

        Assert.Equal(["AniDB", "TheTVDB"], decision.Sources);
    }

    [Theory]
    [InlineData("12", "", "", "anidb:12")]
    [InlineData("", "30", "40", "anilist:30")]
    [InlineData("", "", "40", "mal:40")]
    [InlineData("", "", "", null)]
    public void KeysAnItemByAniDbElseByTheLinkTheSeriesProviderUses(string aniDb, string aniList, string mal, string? key)
    {
        var ids = new Dictionary<string, string>();
        foreach (var (provider, id) in new[] { ("AniDB", aniDb), ("AniList", aniList), ("MyAnimeList", mal) })
        {
            if (id.Length > 0)
            {
                ids[provider] = id;
            }
        }

        Assert.Equal(key, GenreRebuild.ItemKeyOf(ids));
    }

    [Fact]
    public void FollowsASeasonOnlyWhereItHeldTheSeriesOldLists()
    {
        string[] oldGenres = ["Action", "Anime"];
        string[] oldTags = ["Action", "Magic"];
        string[] newGenres = ["Action", "Fantasy"];
        string[] newTags = ["Action", "Fantasy"];

        var following = new Series { Genres = oldGenres, Tags = oldTags.Reverse().ToArray() };
        var empty = new Series();
        var own = new Series { Genres = ["Drama"], Tags = ["Tragedy"] };

        Assert.Equal((newGenres, newTags), GenreRebuild.SeasonLists(following, oldGenres, oldTags, newGenres, newTags));
        Assert.Equal((null, null), GenreRebuild.SeasonLists(empty, oldGenres, oldTags, newGenres, newTags));
        Assert.Equal((null, null), GenreRebuild.SeasonLists(own, oldGenres, oldTags, newGenres, newTags));
        Assert.Equal((null, newTags), GenreRebuild.SeasonLists(following, oldGenres, oldTags, null, newTags));
    }

    [Fact]
    public void SettlesAStandInOnceTheSourceItWaitedForContributed()
    {
        using var db = AniMetaDb.Open(_folder, NullLogger.Instance);
        var item = new Series { Name = "Show", Path = "/media/show" };
        var id = ProvenanceRecord.MakeId("anidb:1", "Genres");
        var waiting = new ProvenanceRecord { Id = id, ItemKey = "anidb:1", Field = "Genres", Source = "AniList", PreferredSource = "AniDB", Provisional = true };
        db.Provenance.Upsert(waiting);

        // AniDB still has no answer: the field keeps waiting for it.
        GenreRebuild.Remember(db, "anidb:1", item, waiting, ["AniList", "MyAnimeList"], true);
        var kept = db.Provenance.FindById(id);
        Assert.True(kept.Provisional);
        Assert.Equal("AniDB", kept.PreferredSource);
        Assert.Equal("AniList,MyAnimeList", kept.Source);

        // Now its cached answer is there.
        GenreRebuild.Remember(db, "anidb:1", item, kept, ["AniDB", "AniList"], true);
        var settled = db.Provenance.FindById(id);
        Assert.False(settled.Provisional);
        Assert.Equal(string.Empty, settled.PreferredSource);
        Assert.Equal("AniDB,AniList", settled.Source);
    }

    [Fact]
    public void RecordsOnlyWhereTheItemHasAPathAndSomethingChanged()
    {
        using var db = AniMetaDb.Open(_folder, NullLogger.Instance);
        var id = ProvenanceRecord.MakeId("anilist:5", "Genres");

        GenreRebuild.Remember(db, "anilist:5", new Series { Name = "No path" }, null, ["AniList"], true);
        Assert.Null(db.Provenance.FindById(id));

        var item = new Series { Name = "Show", Path = "/media/show" };
        GenreRebuild.Remember(db, "anilist:5", item, null, ["AniList"], true);
        var created = db.Provenance.FindById(id);
        Assert.Equal("AniList", created.Source);

        // Nothing changed and nothing waits: the record is left as it is.
        var stamp = created.UpdatedUtc;
        GenreRebuild.Remember(db, "anilist:5", item, created, ["AniList"], false);
        Assert.Equal(stamp, db.Provenance.FindById(id).UpdatedUtc);
    }
}
