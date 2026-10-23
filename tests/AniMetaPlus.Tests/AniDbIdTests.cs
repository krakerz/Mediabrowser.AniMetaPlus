using AniMetaPlus.Providers.AniDB.Metadata;
using Xunit;

namespace AniMetaPlus.Tests;

public class AniDbIdTests
{
    [Theory]
    [InlineData("123", true)]
    [InlineData("anilist:21718", false)]
    [InlineData("mal:5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("12a", false)]
    public void OnlyDigitsAreAnAniDbId(string? id, bool expected)
        => Assert.Equal(expected, AniDbSeriesProvider.IsAniDbId(id));
}
