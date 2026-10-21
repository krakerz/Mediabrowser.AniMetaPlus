using System;
using System.IO;
using System.Linq;
using System.Xml;
using AniMetaPlus.Configuration;
using AniMetaPlus.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Xunit;

namespace AniMetaPlus.Tests;

public class AnnExtrasTests
{
    private static readonly DateTime _now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    // Shaped like ANN's answer for an anime; the text carries the entities ANN leaves in it.
    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <ann>
          <anime id="4658" gid="1" type="TV" name="Sample Show" precision="TV" generated-on="2026-10-21T00:00:00Z">
            <info type="Main title" lang="JA">Sample &quot;Show&quot;</info>
            <info type="Number of episodes">12</info>
            <ratings nb_votes="435" weighted_score="6.0263" bayesian_score="6.06502"/>
            <episode num="1"><title gid="1" lang="JA">Hajimari</title><title gid="2" lang="EN">It&#039;s a Start</title></episode>
            <episode num="1.5"><title lang="EN">Recap</title></episode>
            <episode num="S1"><title lang="EN">Special</title></episode>
            <episode num="2"><title lang="JA">Tsugi</title></episode>
            <episode num="3"><title lang="EN">Third &amp;amp; Last</title></episode>
            <staff gid="1"><task>Director</task><person id="3610">Masahiko Murata</person></staff>
            <staff gid="2"><task>Series Composition</task><person id="1">Jun Maekawa</person></staff>
            <staff gid="3"><task>Music</task><person id="2">Yuki Kajiura</person></staff>
            <staff gid="4"><task>Key Animation</task><person id="3">Some Animator</person></staff>
            <cast gid="5" lang="EN"><role>Shizuka Tsuzaki</role><person id="9">Monica Rial</person></cast>
            <cast gid="6" lang="JA"><role>Shizuka Tsuzaki</role><person id="8">Rie Kugimiya</person></cast>
            <credit gid="7"><task>Animation Production</task><company id="1">Studio X</company></credit>
            <credit gid="8"><task>Production</task><company id="2">Producer Co.</company></credit>
          </anime>
        </ann>
        """;

    private static AnnRecord Record() => AnnClient.Parse(Sample, "4658", _now);

    [Fact]
    public void ParsesTheAnswer()
    {
        var record = Record();

        Assert.True(record.Found);
        Assert.Equal("Sample \"Show\"", record.Title);
        Assert.Equal(6.06502, record.Rating);
        Assert.Equal(435, record.Votes);
        Assert.Equal(5, record.Episodes.Count);
        Assert.Equal(4, record.Staff.Count);
        Assert.Equal(2, record.Cast.Count);
        Assert.Equal(2, record.Companies.Count);
        Assert.Equal(_now, record.FetchedUtc);
    }

    [Fact]
    public void DecodesEntitiesEvenWhenEscapedTwice()
    {
        var record = Record();

        Assert.Equal("It's a Start", record.Episodes[0].EnglishTitle);
        Assert.Equal("Third & Last", record.Episodes[4].EnglishTitle);
    }

    [Fact]
    public void ReadsAnAnswerWithEveryPartMissing()
    {
        var record = AnnClient.Parse("<ann><anime id=\"1\" name=\"Bare\" /></ann>", "1", _now);

        Assert.True(record.Found);
        Assert.Null(record.Title);
        Assert.Null(record.Rating);
        Assert.Null(record.Votes);
        Assert.Empty(record.Episodes);
        Assert.Empty(record.Staff);
        Assert.Empty(record.Cast);
        Assert.Empty(record.Companies);
    }

    [Fact]
    public void ReadsAWarningAsNotFound()
    {
        var record = AnnClient.Parse("<ann><warning>no result for anime=999999</warning></ann>", "999999", _now);

        Assert.False(record.Found);
        Assert.Equal("999999", record.Id);
    }

    [Fact]
    public void RejectsAnAnswerThatIsNotXml()
        => Assert.ThrowsAny<XmlException>(() => AnnClient.Parse("<html>503", "1", _now));

    [Fact]
    public void MapsStaffToKindsOfCredit()
    {
        var staff = AnnExtras.Staff(Record());

        Assert.Equal(["Masahiko Murata", "Jun Maekawa", "Yuki Kajiura"], staff.Select(p => p.Name));
        Assert.Equal(["Director", "Writer", "Composer"], staff.Select(p => p.Kind));
        Assert.Equal("Series Composition", staff[1].Role);
    }

    [Theory]
    [InlineData("Director", "Director")]
    [InlineData("Series Director", "Director")]
    [InlineData("Script", "Writer")]
    [InlineData("Screenplay", "Writer")]
    [InlineData("Original creator", "Writer")]
    [InlineData("Original Work", "Writer")]
    [InlineData("Music", "Composer")]
    [InlineData("Producer", "Producer")]
    [InlineData("Script (eps 1-3)", "Writer")]
    [InlineData("series director", "Director")]
    public void MapsEachTask(string task, string kind)
    {
        var staff = AnnExtras.Staff(new AnnRecord { Staff = [new AnnStaff { Task = task, Name = "A Person" }] });

        Assert.Equal(kind, Assert.Single(staff).Kind);
    }

    [Theory]
    [InlineData("Key Animation")]
    [InlineData("Episode Director")]
    [InlineData("")]
    public void LeavesOtherTasksOut(string task)
        => Assert.Empty(AnnExtras.Staff(new AnnRecord { Staff = [new AnnStaff { Task = task, Name = "A Person" }] }));

    [Fact]
    public void ListsEachStaffNameOnce()
    {
        var staff = AnnExtras.Staff(new AnnRecord
        {
            Staff = [new AnnStaff { Task = "Director", Name = "Jun Maekawa" }, new AnnStaff { Task = "Script", Name = "Maekawa Jun" }]
        });

        Assert.Equal("Director", Assert.Single(staff).Role);
    }

    [Fact]
    public void PicksTheAnimationStudiosAndFallsBackToProduction()
    {
        Assert.Equal(["Studio X"], AnnExtras.Studios(Record()));

        var production = new AnnRecord { Companies = [new AnnCompany { Task = "Production", Name = "Producer Co." }, new AnnCompany { Task = "Distribution", Name = "Shop" }] };
        Assert.Equal(["Producer Co."], AnnExtras.Studios(production));
        Assert.Empty(AnnExtras.Studios(new AnnRecord()));
    }

    [Fact]
    public void PicksTheEnglishCastWithTheirCharacters()
    {
        var cast = AnnExtras.EnglishCast(Record());

        var actor = Assert.Single(cast);
        Assert.Equal("Monica Rial", actor.Name);
        Assert.Equal("Shizuka Tsuzaki", actor.Role);
        Assert.Equal("Actor", actor.Kind);
    }

    [Fact]
    public void AddsToTheResultWithoutRepeatingNames()
    {
        var result = new MetadataResult<Series> { Item = new Series { Studios = ["studio x"] } };
        result.AddPerson(new PersonInfo { Name = "Maekawa Jun", Role = "Writer" });
        var configuration = new PluginConfiguration { AnnEnglishCast = true };

        AnnExtras.Apply(result, Record(), configuration);

        // The family-name-first spelling AniDB uses is the same person; the studio is already there.
        Assert.Equal(["Maekawa Jun", "Masahiko Murata", "Yuki Kajiura", "Monica Rial"], result.People.Select(p => p.Name));
        Assert.Equal(["studio x"], result.Item.Studios);
    }

    [Fact]
    public void AddsAStudioTheItemLacks()
    {
        var result = new MetadataResult<Series> { Item = new Series { Studios = ["Other"] } };

        AnnExtras.Apply(result, Record(), new PluginConfiguration());

        Assert.Equal(["Other", "Studio X"], result.Item.Studios);
    }

    [Fact]
    public void LeavesTheEnglishCastOutUnlessAsked()
    {
        var result = new MetadataResult<Series> { Item = new Series() };

        AnnExtras.Apply(result, Record(), new PluginConfiguration());

        Assert.DoesNotContain(result.People, p => p.Name == "Monica Rial");
        Assert.Equal(3, result.People.Count);
    }

    [Fact]
    public void LeavesTheEnglishCastOutWhereCastListsCharacters()
    {
        var result = new MetadataResult<Series> { Item = new Series() };

        AnnExtras.Apply(result, Record(), new PluginConfiguration { AnnEnglishCast = true, CastShowsCharacters = true });

        Assert.DoesNotContain(result.People, p => p.Name == "Monica Rial");
    }

    [Fact]
    public void ScoresCountFromTenVotes()
    {
        var enough = AnnExtras.RatingRecord(new AnnRecord { Id = "1", Rating = 7.5, Votes = 10 });

        Assert.NotNull(enough);
        Assert.Equal(SourceIds.Ann, enough.Source);
        Assert.Equal(7.5, enough.Rating);
        Assert.Equal(10, enough.Votes);

        Assert.Null(AnnExtras.RatingRecord(new AnnRecord { Rating = 7.5, Votes = 9 }));
        Assert.Null(AnnExtras.RatingRecord(new AnnRecord { Rating = 7.5 }));
        Assert.Null(AnnExtras.RatingRecord(new AnnRecord { Votes = 500 }));
    }

    [Fact]
    public void RatingIsAnotherContributorWeighedByVotes()
    {
        var aniDb = new AnimeRecord { Source = SourceIds.AniDb, Rating = 8, Votes = 100 };
        var ann = AnnExtras.RatingRecord(new AnnRecord { Rating = 6, Votes = 300 })!;

        var average = SeriesMerge.AverageRating([aniDb, ann], _ => true);

        // (8 * 100 + 6 * 300) / 400
        Assert.Equal(6.5f, average);
        Assert.Equal(8f, SeriesMerge.AverageRating([aniDb], _ => true));
        Assert.Equal(6f, SeriesMerge.AverageRating([aniDb, ann], source => source == SourceIds.Ann));
        Assert.Null(SeriesMerge.AverageRating([aniDb], _ => false));
    }

    [Fact]
    public void FindsAnEpisodeTitleByPlainNumber()
    {
        var record = Record();

        Assert.Equal("It's a Start", AnnExtras.EpisodeTitle(record, 1));
        Assert.Equal("Third & Last", AnnExtras.EpisodeTitle(record, 3));
        Assert.Null(AnnExtras.EpisodeTitle(record, 2));
        Assert.Null(AnnExtras.EpisodeTitle(record, 5));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("Episode 5", true)]
    [InlineData("episode 12", true)]
    [InlineData("Ep. 3", true)]
    [InlineData("Episode 5: The Fight", false)]
    [InlineData("The Fight", false)]
    public void RecognisesAPlaceholderTitle(string? title, bool placeholder)
        => Assert.Equal(placeholder, AnnExtras.IsPlaceholderTitle(title));

    [Theory]
    [InlineData(TitlePreferenceType.Localized, "en", true)]
    [InlineData(TitlePreferenceType.Localized, "en-US", true)]
    [InlineData(TitlePreferenceType.Localized, "", true)]
    [InlineData(TitlePreferenceType.Localized, null, true)]
    [InlineData(TitlePreferenceType.Localized, "de", false)]
    [InlineData(TitlePreferenceType.Japanese, "en", false)]
    [InlineData(TitlePreferenceType.JapaneseRomaji, "en", false)]
    public void WantsEnglishTitlesOnlyInAnEnglishLibrary(TitlePreferenceType preference, string? language, bool wanted)
        => Assert.Equal(wanted, AnnExtras.WantsEnglishTitles(preference, language));

    [Theory]
    [InlineData("episode-5.xml", 5)]
    [InlineData("episode-125.xml", 125)]
    [InlineData("episode-S1.xml", null)]
    [InlineData("episode-C2.xml", null)]
    [InlineData("series.xml", null)]
    public void NumbersOnlyOrdinaryEpisodes(string fileName, int? number)
        => Assert.Equal(number, AnnExtras.RegularEpisodeNumber(fileName));

    [Fact]
    public void ReadsTheAnnIdFromACachedAniDbDocument()
    {
        var folder = Path.Combine(Path.GetTempPath(), "amp-ann-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            var path = Path.Combine(folder, "series.xml");
            File.WriteAllText(path, "<anime id=\"1\"><titles /><resources><resource type=\"1\"><externalentity><identifier>4658</identifier></externalentity></resource></resources></anime>");

            Assert.Equal("4658", AniMetaPlus.Providers.AniDB.Metadata.AniDbSeriesProvider.ReadAnnId(path));
            Assert.Null(AniMetaPlus.Providers.AniDB.Metadata.AniDbSeriesProvider.ReadAnnId(Path.Combine(folder, "missing.xml")));

            File.WriteAllText(path, "<anime id=\"1\"><titles /></anime>");
            Assert.Null(AniMetaPlus.Providers.AniDB.Metadata.AniDbSeriesProvider.ReadAnnId(path));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void CachesARecordAndReadsItBack()
    {
        var folder = Path.Combine(Path.GetTempPath(), "amp-ann-" + Guid.NewGuid().ToString("N"), "ann");

        try
        {
            Assert.Null(AnnClient.ReadCache(folder, "4658"));

            AnnClient.WriteCache(folder, Record());
            var read = AnnClient.ReadCache(folder, "4658");

            Assert.NotNull(read);
            Assert.True(File.Exists(Path.Combine(folder, "4658.json")));
            Assert.Equal(_now, read.FetchedUtc);
            Assert.Equal(Record().Episodes.Count, read.Episodes.Count);
            Assert.Equal("Monica Rial", read.Cast[0].Name);
            Assert.Equal(6.06502, read.Rating);
        }
        finally
        {
            Directory.Delete(Directory.GetParent(folder)!.FullName, true);
        }
    }

    [Fact]
    public void ReusesAnAnswerForAWeek()
    {
        var record = new AnnRecord { FetchedUtc = _now };

        Assert.True(AnnClient.IsFresh(record, _now.AddDays(6).AddHours(23)));
        Assert.False(AnnClient.IsFresh(record, _now.AddDays(7)));
    }

    [Fact]
    public void KeepsAboveASecondBetweenRequests()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(600), AnnClient.WaitBeforeNext(_now, _now.AddMilliseconds(500)));
        Assert.Equal(TimeSpan.Zero, AnnClient.WaitBeforeNext(_now, _now.AddSeconds(2)));
        Assert.Equal(TimeSpan.Zero, AnnClient.WaitBeforeNext(DateTime.MinValue, _now));
    }

    [Theory]
    [InlineData("4658", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("12a", false)]
    [InlineData("../1", false)]
    public void AcceptsOnlyDigitsAsAnId(string? id, bool valid)
        => Assert.Equal(valid, AnnClient.IsId(id));

    [Fact]
    public void DefaultsToExtrasOnAndEnglishCastOff()
    {
        var configuration = new PluginConfiguration();

        Assert.True(configuration.UseAnnExtras);
        Assert.False(configuration.AnnEnglishCast);
    }
}
