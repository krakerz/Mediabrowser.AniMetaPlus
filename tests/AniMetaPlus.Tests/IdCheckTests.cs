using AniMetaPlus.Core;
using Xunit;

namespace AniMetaPlus.Tests;

public class IdCheckTests
{
    // The Beginning After the End held Gekijouban Tiger & Bunny's id (8805): its TVDB id and its
    // folder both say 18935, so it is corrected.
    [Fact]
    public void CorrectsAnIdBothTheTvdbIdAndTheFolderContradict()
        => Assert.Equal(new AniDbVerdict("18935", false), IdCheck.Judge("8805", "246381", "455684", "18935", "18935"));

    [Theory]
    // The id agrees with one of them.
    [InlineData("8692", "259640", "259640", "8692", null)]
    [InlineData("8692", null, null, null, "8692")]
    // A later season in a folder of its own: another entry of the same TVDB show.
    [InlineData("10376", "259640", "259640", "8692", null)]
    // Nothing to compare with.
    [InlineData("8692", null, null, null, null)]
    public void LeavesAnIdThatFits(string current, string? currentShow, string? tvdb, string? byShow, string? byFolder)
        => Assert.Equal(default, IdCheck.Judge(current, currentShow, tvdb, byShow, byFolder));

    [Theory]
    // Only one piece of evidence against it, or two that disagree: logged, not changed.
    [InlineData("8805", "246381", "455684", "18935", null)]
    [InlineData("8805", "246381", null, null, "18935")]
    [InlineData("8805", "246381", "455684", "18935", "19381")]
    public void OnlyLogsAnIdWithoutEnoughEvidence(string current, string? currentShow, string? tvdb, string? byShow, string? byFolder)
        => Assert.Equal(new AniDbVerdict(null, true), IdCheck.Judge(current, currentShow, tvdb, byShow, byFolder));
}
