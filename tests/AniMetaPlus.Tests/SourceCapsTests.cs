using AniMetaPlus.Configuration;
using AniMetaPlus.Sources;
using Xunit;

namespace AniMetaPlus.Tests;

public class SourceCapsTests
{
    // A library refresh with no AniDB cap sent 256 requests and was banned.
    [Fact]
    public void AniDbWithoutCapsGetsTheDefaults()
        => Assert.Equal((SourceDefaults.AniDbDefaultPerHour, SourceDefaults.AniDbDefaultPerDay), SourceDefaults.Caps(new SourceSetting { Id = SourceIds.AniDb }));

    [Fact]
    public void AniDbKeepsTheCapsSet()
        => Assert.Equal((5, 40), SourceDefaults.Caps(new SourceSetting { Id = SourceIds.AniDb, MaxPerHour = 5, MaxPerDay = 40 }));

    [Fact]
    public void OtherSourcesStayUncappedUnlessSet()
        => Assert.Equal((0, 0), SourceDefaults.Caps(new SourceSetting { Id = SourceIds.AniList }));
}
