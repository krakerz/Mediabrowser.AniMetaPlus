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
}
