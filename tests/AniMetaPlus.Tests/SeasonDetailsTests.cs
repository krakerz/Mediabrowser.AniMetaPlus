using System;
using AniMetaPlus.Configuration;
using AniMetaPlus.Providers.AniDB.Metadata;
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
}
