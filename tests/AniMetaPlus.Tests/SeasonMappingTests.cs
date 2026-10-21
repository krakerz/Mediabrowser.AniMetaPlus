using System;
using System.Collections.Generic;
using System.Net.Http;
using AniMetaPlus.Data;
using AniMetaPlus.Providers.AniDB.Metadata;
using AniMetaPlus.Sources;
using Xunit;

namespace AniMetaPlus.Tests;

/// <summary>
/// Tests for laying AniDB entries over a library's seasons when the library is missing episodes,
/// for telling a stored season id from one set by hand, and for delegated timeouts.
/// </summary>
public class SeasonMappingTests
{
    /// <summary>
    /// A season missing its first episodes still starts at 1; it is not read as numbered straight through.
    /// </summary>
    [Fact]
    public void ASeasonMissingItsFirstEpisodesStartsAtOne()
    {
        var seasons = AniDbSeasonLayout.ToSeasons(new Dictionary<int, (int, int)> { [1] = (3, 12), [2] = (2, 12) });

        Assert.Equal(new AniDbLibrarySeason(1, 1, 12), seasons[0]);
        Assert.Equal(new AniDbLibrarySeason(2, 1, 12), seasons[1]);
    }

    /// <summary>
    /// Numbers that run on past the season before are absolute numbering and are kept.
    /// </summary>
    [Fact]
    public void NumbersRunningOnFromTheSeasonBeforeAreKept()
    {
        var seasons = AniDbSeasonLayout.ToSeasons(new Dictionary<int, (int, int)> { [1] = (1, 12), [2] = (13, 24), [3] = (27, 36) });

        Assert.Equal(new AniDbLibrarySeason(2, 13, 12), seasons[1]);
        Assert.Equal(new AniDbLibrarySeason(3, 27, 10), seasons[2]);
    }

    /// <summary>
    /// A later season with no season before it in the library keeps its numbers.
    /// </summary>
    [Fact]
    public void ALoneLaterSeasonKeepsItsNumbers()
    {
        var seasons = AniDbSeasonLayout.ToSeasons(new Dictionary<int, (int, int)> { [2] = (13, 24) });

        Assert.Equal(new AniDbLibrarySeason(2, 13, 12), seasons[0]);
    }

    /// <summary>
    /// A few episodes left of an entry don't start the next season; a long entry's rest does.
    /// </summary>
    [Fact]
    public void OnlyARestThatFillsTheNextSeasonCarriesOver()
    {
        // 12-episode entry, season holds 10: two left, next season 12 long.
        Assert.False(AniDbSeasonResolver.CarriesOver(2, 12));

        // 24-episode entry split 12/12.
        Assert.True(AniDbSeasonResolver.CarriesOver(12, 12));

        // Same, with the second season missing its last episodes.
        Assert.True(AniDbSeasonResolver.CarriesOver(12, 9));

        // Dragon Ball Z: one 291-episode entry over nine seasons.
        Assert.True(AniDbSeasonResolver.CarriesOver(252, 35));

        // Within the split allowance: one extra file in the next season.
        Assert.True(AniDbSeasonResolver.CarriesOver(12, 14));
        Assert.False(AniDbSeasonResolver.CarriesOver(12, 15));
    }

    /// <summary>
    /// A stored id matching what the provider recorded is the provider's; one differing from it is the owner's.
    /// </summary>
    [Fact]
    public void AStoredIdIsTheProvidersOnlyWhileItMatchesWhatItRecorded()
    {
        Assert.False(AniDbSeasonResolver.IsSetByHand(null, null, "100", 2));
        Assert.False(AniDbSeasonResolver.IsSetByHand("200", "200", "100", 2));
        Assert.True(AniDbSeasonResolver.IsSetByHand("300", "200", "100", 2));
    }

    /// <summary>
    /// Ids stored before anything was recorded: the series entry on season 0 or 1 is the provider's, anything else stays the owner's.
    /// </summary>
    [Fact]
    public void OlderStoredIdsKeepTheirReading()
    {
        Assert.False(AniDbSeasonResolver.IsSetByHand("100", null, "100", 1));
        Assert.False(AniDbSeasonResolver.IsSetByHand("100", null, "100", 0));
        Assert.True(AniDbSeasonResolver.IsSetByHand("200", null, "100", 1));
        Assert.True(AniDbSeasonResolver.IsSetByHand("200", null, "100", 2));
    }

    /// <summary>
    /// A delegated provider timing out blocks only the item; other sources and other failures keep their scope.
    /// </summary>
    [Fact]
    public void ADelegatedTimeoutBlocksOnlyTheItem()
    {
        var timedOut = new OperationCanceledException();
        var failure = BlockStore.Classify(timedOut);

        Assert.Equal(BlockScope.Source, failure.Scope);
        Assert.Equal(BlockScope.Item, SourceGate.TimeoutScope(failure, timedOut, delegated: true).Scope);
        Assert.Equal(BlockScope.Source, SourceGate.TimeoutScope(failure, timedOut, delegated: false).Scope);

        var down = new HttpRequestException("refused");
        Assert.Equal(BlockScope.Source, SourceGate.TimeoutScope(BlockStore.Classify(down), down, delegated: true).Scope);
    }
}
