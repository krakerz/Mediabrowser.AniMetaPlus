using System.Collections.Generic;
using AniMetaPlus.Titles;
using Xunit;

namespace AniMetaPlus.Tests;

public class AlternativeTitlesTests
{
    // AniDB 10894's romaji and English titles, as the local dump has them.
    private static readonly List<DumpTitle> _danMachi =
    [
        new("short", "x-jat", "danmachi"),
        new("main", "x-jat", "Dungeon ni Deai o Motomeru no wa Machigatte Iru Darouka: Familia Myth"),
        new("syn", "x-jat", "Dungeon ni Deai wo Motomeru no wa Machigatte Iru Darouka: Familia Myth"),
        new("official", "en", "Is It Wrong to Try to Pick Up Girls in a Dungeon?"),
    ];

    [Fact]
    public void PutsTheShortTitleAndTheEnglishTitleAfterTheOriginal()
    {
        var joined = AlternativeTitles.Join("Dungeon ni Deai o Motomeru no wa Machigatte Iru Darouka: Familia Myth", "Is It Wrong to Try to Pick Up Girls in a Dungeon?", _danMachi);

        Assert.Equal("Dungeon ni Deai o Motomeru no wa Machigatte Iru Darouka: Familia Myth / danmachi / Dungeon ni Deai wo Motomeru no wa Machigatte Iru Darouka: Familia Myth", joined);
    }

    [Fact]
    public void LeavesOutTheDisplayedNameAndRepeats()
    {
        var joined = AlternativeTitles.Join("Dungeon ni Deai o Motomeru no wa Machigatte Iru Darouka: Familia Myth", "Dungeon ni Deai o Motomeru no wa Machigatte Iru Darouka: Familia Myth", _danMachi)!;

        Assert.StartsWith("Dungeon ni Deai o Motomeru no wa Machigatte Iru Darouka: Familia Myth / danmachi", joined, System.StringComparison.Ordinal);
        Assert.Contains("Is It Wrong to Try to Pick Up Girls in a Dungeon?", joined, System.StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(joined, "Dungeon ni Deai o "));
    }

    [Fact]
    public void StaysWithinTheLengthLimit()
    {
        var many = new List<DumpTitle>();
        for (var i = 0; i < 30; i++)
        {
            many.Add(new DumpTitle("syn", "en", "A rather long alternative title number " + i));
        }

        var joined = AlternativeTitles.Join("Original", "Shown", many)!;

        Assert.True(joined.Length <= AlternativeTitles.MaxLength);
        Assert.True(joined.Split(" / ").Length <= AlternativeTitles.MaxTitles);
    }

    [Fact]
    public void KeepsTheOriginalWhenThereIsNothingToAdd()
        => Assert.Equal("Only", AlternativeTitles.Join("Only", "Only", [new DumpTitle("main", "x-jat", "Only")]));

    [Fact]
    public void WantedJoinsAndRunningAgainChangesNothing()
    {
        const string Romaji = "Dungeon ni Deai o Motomeru no wa Machigatte Iru Darouka: Familia Myth";
        var once = AlternativeTitles.Wanted(Romaji, "Is It Wrong to Try to Pick Up Girls in a Dungeon?", _danMachi, true);

        Assert.Contains(" / danmachi", once, System.StringComparison.Ordinal);
        Assert.Equal(once, AlternativeTitles.Wanted(once, "Is It Wrong to Try to Pick Up Girls in a Dungeon?", _danMachi, true));
    }

    [Fact]
    public void WantedStripsBackWhenTurnedOff()
    {
        const string Romaji = "Dungeon ni Deai o Motomeru no wa Machigatte Iru Darouka: Familia Myth";

        Assert.Equal(Romaji, AlternativeTitles.Wanted(Romaji + " / danmachi / Is It Wrong to Try to Pick Up Girls in a Dungeon?", "x", _danMachi, false));
    }

    [Fact]
    public void WantedFallsBackToTheMainTitleWhenNoneIsSet()
        => Assert.StartsWith("Dungeon ni Deai o Motomeru", AlternativeTitles.Wanted(null, "Shown", _danMachi, false), System.StringComparison.Ordinal);

    // A show AniDB has no entry for: its titles come from the cached AniList or MyAnimeList answer.
    [Fact]
    public void CachedAnswersGiveTitlesWithoutAniDb()
    {
        var record = new AniMetaPlus.Sources.AnimeRecord { RomajiTitle = "Chimimouryou", EnglishTitle = "Spirits of the Mountain", Synonyms = ["Chimi Mouryou", "Mononoke Tale"] };
        var titles = new List<DumpTitle>(AlternativeTitles.FromRecord(record));

        Assert.Equal("Chimimouryou / Spirits of the Mountain / Chimi Mouryou / Mononoke Tale", AlternativeTitles.Wanted(null, "Chimimouryou", titles, true));
    }

    // AniDB, AniList and MyAnimeList write the same apostrophe three ways.
    [Fact]
    public void TitlesDifferingOnlyInQuotesAreOneTitle()
    {
        var titles = new List<DumpTitle> { new("official", "en", "Frieren: Beyond Journey`s End"), new("official", "en", "Frieren: Beyond Journey\u2019s End"), new("syn", "en", "Frieren: Beyond Journey's End") };

        Assert.Equal("Sousou no Frieren / Frieren: Beyond Journey's End", AlternativeTitles.Join("Sousou no Frieren", "Sousou no Frieren", titles));
    }
}
