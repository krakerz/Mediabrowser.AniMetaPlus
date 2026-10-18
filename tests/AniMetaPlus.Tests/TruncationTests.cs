using AniMetaPlus.Providers.AniDB.Metadata;
using Xunit;

namespace AniMetaPlus.Tests;

public class TruncationTests
{
    // Emby named the "Eroriman 2" folder "Eroriman", and the name then identified the first show.
    [Theory]
    [InlineData("Eroriman", "Eroriman 2", true)]
    [InlineData("DanMachi", "DanMachi III", true)]
    [InlineData("86", "86 Eighty-Six", true)]
    [InlineData("Eroriman", "Eroriman", false)]
    [InlineData("Eroriman", "Eroriman Gaiden", false)]
    [InlineData("Show", "Show (2020)", false)]
    [InlineData("Eroriman 2", "Eroriman 2", false)]
    public void SeesWhereTheServerCutTheFolderNameShort(string name, string folder, bool truncated)
        => Assert.Equal(truncated, AniDbSeriesProvider.IsTruncation(name, folder));

    // A name cut off between words lets the folder's title be tried before it.
    [Theory]
    [InlineData("The Beginning", "The Beginning After the End", true)]
    [InlineData("The Beginning After the End", "The Beginning After the End", false)]
    [InlineData("The Begin", "The Beginning After the End", false)]
    [InlineData("Re:Zero", "Re:Zero - Starting Life", true)]
    [InlineData("", "Show", false)]
    public void SeesANameCutOffBetweenWords(string name, string folder, bool cut)
        => Assert.Equal(cut, AniDbSeriesProvider.IsCutShort(name, folder));

    // A specials season's entry is the show's own, so AniList and MyAnimeList would describe the show.
    [Theory]
    [InlineData("AniList", 0, false)]
    [InlineData("MyAnimeList", 0, false)]
    [InlineData("AniDB", 0, true)]
    [InlineData("AniList", 2, true)]
    [InlineData("MyAnimeList", null, true)]
    public void SpecialsAreNotAskedOfAniListOrMyAnimeList(string source, int? season, bool asked)
        => Assert.Equal(asked, AniDbSeasonProvider.AsksEntryOf(source, season));
}
