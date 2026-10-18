using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AniMetaPlus.Configuration;
using AniMetaPlus.Providers.AniDB.Metadata;
using AniMetaPlus.Sources;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace AniMetaPlus.Tests;

public class SeasonDetailsTests
{
    [Fact]
    public void DefaultsToAniDb()
    {
        Assert.Equal("AniDB", new PluginConfiguration().SeasonDetails);
        Assert.Equal(SeasonDetailsMode.AniDb, new PluginConfiguration().SeasonDetailsMode);
    }

    [Theory]
    [InlineData("AniDB", SeasonDetailsMode.AniDb)]
    [InlineData("Delegated", SeasonDetailsMode.Delegated)]
    [InlineData("Plain", SeasonDetailsMode.Plain)]
    [InlineData("Order", SeasonDetailsMode.Order)]
    [InlineData("order", SeasonDetailsMode.Order)]
    [InlineData("delegated", SeasonDetailsMode.Delegated)]
    [InlineData(" PLAIN ", SeasonDetailsMode.Plain)]
    [InlineData("", SeasonDetailsMode.AniDb)]
    [InlineData(null, SeasonDetailsMode.AniDb)]
    [InlineData("Klingon", SeasonDetailsMode.AniDb)]
    [InlineData("7", SeasonDetailsMode.AniDb)]
    public void ParsesTheMode(string? value, SeasonDetailsMode expected)
    {
        var configuration = new PluginConfiguration { SeasonDetails = value! };

        Assert.Equal(expected, configuration.SeasonDetailsMode);
    }

    [Fact]
    public void DelegatedAnswerWithAnOverviewIsTaken()
    {
        var target = new Season { Name = "Season 2" };
        var found = new Season
        {
            Name = "Season 2",
            Overview = "The story goes on.",
            PremiereDate = new DateTime(2020, 1, 5),
            EndDate = new DateTime(2020, 3, 29),
            ProductionYear = 2020
        };

        Assert.True(AniDbSeasonProvider.ApplyDelegated(target, found, "Season 2"));
        Assert.Equal("Season 2", target.Name);
        Assert.Equal("The story goes on.", target.Overview);
        Assert.Equal(found.PremiereDate, target.PremiereDate);
        Assert.Equal(found.EndDate, target.EndDate);
        Assert.Equal(2020, target.ProductionYear);
    }

    [Fact]
    public void DelegatedNameOfItsOwnIsTaken()
    {
        var target = new Season { Name = "Season 2" };
        var found = new Season { Name = "The Final Arc" };

        Assert.True(AniDbSeasonProvider.ApplyDelegated(target, found, "Season 2"));
        Assert.Equal("The Final Arc", target.Name);
    }

    [Fact]
    public void DelegatedAnswerWithOnlyTheDefaultNameIsNotTaken()
    {
        var target = new Season { Name = "Season 2" };
        var found = new Season { Name = "season 2", PremiereDate = new DateTime(2020, 1, 5) };

        Assert.False(AniDbSeasonProvider.ApplyDelegated(target, found, "Season 2"));
        Assert.Equal("Season 2", target.Name);
        Assert.Null(target.PremiereDate);
    }

    [Fact]
    public void EmptyDelegatedAnswerIsNotTaken()
    {
        var target = new Season { Name = "Specials" };

        Assert.False(AniDbSeasonProvider.ApplyDelegated(target, new Season { Name = " " }, "Specials"));
        Assert.Equal("Specials", target.Name);
    }

    [Theory]
    [InlineData(1, "Romelia Senki 2nd Season", "Season 1")]
    [InlineData(2, null, "Season 2")]
    [InlineData(0, "Extras", "Specials")]
    [InlineData(null, "Unnumbered", "Unnumbered")]
    public void PlainNamesIgnoreEarlierNames(int? number, string? current, string? expected)
        => Assert.Equal(expected, AniMetaPlus.Providers.AniDB.Metadata.AniDbSeasonProvider.PlainName(number, current));

    [Fact]
    public void NamesDefaultToTheSource()
    {
        Assert.Equal("Source", new PluginConfiguration().SeasonNames);
        Assert.Equal(SeasonNamesMode.Source, new PluginConfiguration().SeasonNamesMode);
    }

    [Theory]
    [InlineData("Source", SeasonNamesMode.Source)]
    [InlineData("Plain", SeasonNamesMode.Plain)]
    [InlineData(" plain ", SeasonNamesMode.Plain)]
    [InlineData("SOURCE", SeasonNamesMode.Source)]
    [InlineData("", SeasonNamesMode.Source)]
    [InlineData(null, SeasonNamesMode.Source)]
    [InlineData("Klingon", SeasonNamesMode.Source)]
    [InlineData("9", SeasonNamesMode.Source)]
    public void ParsesTheNamesMode(string? value, SeasonNamesMode expected)
        => Assert.Equal(expected, new PluginConfiguration { SeasonNames = value! }.SeasonNamesMode);

    [Theory]
    [InlineData(SeasonNamesMode.Source, 2, "Season 2", "The Final Arc", "The Final Arc")]
    [InlineData(SeasonNamesMode.Source, 2, "Season 2", null, "Season 2")]
    [InlineData(SeasonNamesMode.Source, 2, "Old name", " ", "Season 2")]
    [InlineData(SeasonNamesMode.Source, 0, "Extras", null, "Specials")]
    [InlineData(SeasonNamesMode.Plain, 2, "Season 2", "The Final Arc", "Season 2")]
    [InlineData(SeasonNamesMode.Plain, 0, "Extras", "Bonus", "Specials")]
    public void NameFollowsTheNamesSetting(SeasonNamesMode names, int number, string? serverName, string? sourceName, string expected)
        => Assert.Equal(expected, AniDbSeasonProvider.ChooseName(names, number, serverName, sourceName));

    [Theory]
    [InlineData("200", "100", "Second Season", "Second Season")]
    [InlineData("100", "100", "Show", null)]
    [InlineData("200", "100", null, null)]
    [InlineData("200", "100", " ", null)]
    public void EntryTitleNamesOnlyALaterSeason(string seasonId, string seriesId, string? title, string? expected)
        => Assert.Equal(expected, AniDbSeasonProvider.EntryName(seasonId, seriesId, title));

    [Fact]
    public void DelegatedNameIsOneOfItsOwn()
    {
        Assert.Equal("The Final Arc", AniDbSeasonProvider.DelegatedName(new Season { Name = "The Final Arc" }, "Season 2"));
        Assert.Null(AniDbSeasonProvider.DelegatedName(new Season { Name = "season 2" }, "Season 2"));
        Assert.Null(AniDbSeasonProvider.DelegatedName(new Season(), "Season 2"));
    }

    [Theory]
    [InlineData(SourceIds.AniDb, true, true, true)]
    [InlineData(SourceIds.AniList, true, true, true)]
    [InlineData(SourceIds.Mal, true, true, true)]
    [InlineData(SourceIds.Tvdb, true, true, true)]
    [InlineData(SourceIds.AniDb, true, false, false)]
    [InlineData(SourceIds.AniList, true, false, false)]
    [InlineData(SourceIds.Mal, true, false, false)]
    [InlineData(SourceIds.Tvdb, true, false, true)]
    [InlineData(SourceIds.Tmdb, true, false, true)]
    [InlineData(SourceIds.Tvdb, false, false, false)]
    [InlineData(SourceIds.AniDb, false, true, false)]
    public void PartialEntriesAndSpecialsAskOnlyTheServersProviders(string source, bool enabled, bool ownEntry, bool expected)
        => Assert.Equal(expected, AniDbSeasonProvider.AsksSource(source, enabled, ownEntry));

    private static AniDbSeasonProvider.OrderAnswer Answer(string source, string? overview, string? name = null)
        => new(source, name, overview, new DateTime(2020, 1, 5), null, 2020);

    [Fact]
    public async Task FirstSourceWithAnOverviewWinsAndTheRestAreNotAsked()
    {
        var asked = new List<string>();

        var answer = await AniDbSeasonProvider.FirstAnswer(
            [SourceIds.AniDb, SourceIds.AniList, SourceIds.Tvdb],
            _ => true,
            source =>
            {
                asked.Add(source);

                return Task.FromResult<AniDbSeasonProvider.OrderAnswer?>(Answer(source, source == SourceIds.AniDb ? " " : "Text from " + source));
            });

        Assert.Equal(SourceIds.AniList, answer?.Source);
        Assert.Equal("Text from AniList", answer?.Overview);
        Assert.Equal([SourceIds.AniDb, SourceIds.AniList], asked);
    }

    [Fact]
    public async Task SourcesThatAreNotAskedOrAnswerNothingAreSkipped()
    {
        var asked = new List<string>();

        var answer = await AniDbSeasonProvider.FirstAnswer(
            [SourceIds.AniDb, SourceIds.Mal, SourceIds.Tvdb, SourceIds.Tmdb],
            source => source != SourceIds.AniDb,
            source =>
            {
                asked.Add(source);

                return Task.FromResult(source == SourceIds.Tvdb ? null : Answer(source, "Text from " + source, "Arc"));
            });

        Assert.Equal(SourceIds.Mal, answer?.Source);
        Assert.Equal("Arc", answer?.Name);
        Assert.Equal([SourceIds.Mal], asked);
    }

    [Fact]
    public async Task AnUnansweringSourceFallsThroughToTheNext()
    {
        var asked = new List<string>();

        var answer = await AniDbSeasonProvider.FirstAnswer(
            [SourceIds.Mal, SourceIds.Tvdb, SourceIds.Tmdb],
            _ => true,
            source =>
            {
                asked.Add(source);

                return Task.FromResult(source == SourceIds.Tmdb ? Answer(source, "Text") : null);
            });

        Assert.Equal(SourceIds.Tmdb, answer?.Source);
        Assert.Equal([SourceIds.Mal, SourceIds.Tvdb, SourceIds.Tmdb], asked);
    }

    [Fact]
    public async Task NoAnswerWithAnOverviewIsNull()
    {
        var answer = await AniDbSeasonProvider.FirstAnswer(
            [SourceIds.AniDb, SourceIds.Tvdb],
            _ => true,
            source => Task.FromResult<AniDbSeasonProvider.OrderAnswer?>(Answer(source, null)));

        Assert.Null(answer);
    }
}
