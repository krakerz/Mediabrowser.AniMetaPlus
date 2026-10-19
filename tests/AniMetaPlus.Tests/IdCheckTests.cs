using AniMetaPlus.Core;
using Xunit;

namespace AniMetaPlus.Tests;

public class IdCheckTests
{
    // The verdict without its reason, which the tests below check apart.
    private static (string? Fix, bool Suspect) Of(AniDbVerdict verdict) => (verdict.Fix, verdict.Suspect);

    // The Beginning After the End held Gekijouban Tiger & Bunny's id (8805): its TVDB id and its
    // folder both say 18935, so it is corrected.
    [Fact]
    public void CorrectsAnIdBothTheTvdbIdAndTheFolderContradict()
        => Assert.Equal(("18935", false), Of(IdCheck.Judge("8805", "246381", "455684", "18935", "18935")));

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
        => Assert.Equal((null, true), Of(IdCheck.Judge(current, currentShow, tvdb, byShow, byFolder)));

    // !Collection/Natsume Yuujinchou held one of its specials (9904), an entry of the same TVDB
    // show; its TVDB id and its folder both say the first season.
    [Fact]
    public void CorrectsAFranchiseFolderHeldAsOneOfItsSpecials()
        => Assert.Equal(("5787", false), Of(IdCheck.Judge("9904", "81891", "81891", "5787", "5787")));

    // Folders held as another show the fuzzy search found, where only the TVDB id disagreed.
    [Theory]
    [InlineData("Nakanohito Genome", new[] { "Naka no Hito Genome [Jikkyouchuu]", "nakageno", "The Ones Within" }, new[] { "Utawarerumono" })]
    [InlineData("Compass 2.0", new[] { "#Compass 2.0 Animation Project", "Compass 2.0 - Sentou Setsuri Kaiseki System" }, new[] { "Soushisouai Note The Animation" })]
    [InlineData("Yuusha-kei ni Shosu", new[] { "Sentenced to Be a Hero", "Yuusha-kei ni Shosu: Choubatsu Yuusha 9004 Tai Keimu Kiroku" }, new[] { "Yuusha Keisatsu J-Decker" })]
    [InlineData("Gokushufudo", new[] { "Godou", "Gokushufudou", "The Way of the Househusband" }, new[] { "Gokusen" })]
    [InlineData("Sen no Kiseki - Northern War", new[] { "Eiyuu Densetsu: Sen no Kiseki - Northern War", "The Legend of Heroes: Sen no Kiseki - Northern War" }, new[] { "Eiyuu Densetsu: Sora no Kiseki The Animation" })]
    public void CorrectsWhereTheFolderReadsAsTheTvdbEntry(string folder, string[] mappedTitles, string[] currentTitles)
    {
        var likesShow = IdCheck.Likeness(folder, mappedTitles);
        var likesCurrent = IdCheck.Likeness(folder, currentTitles);

        Assert.Equal(("2", false), Of(IdCheck.Judge("1", "100", "200", "2", null, likesShow, likesCurrent)));
    }

    // Arslan Senki (1991) holds a title the folder is exactly, so the 2015 show TVDB names is no reason to move it.
    [Fact]
    public void LeavesAFolderThatReadsAsTheCurrentEntryToo()
    {
        var likesShow = IdCheck.Likeness("Arslan Senki", ["Arslan Senki (2015)", "The Heroic Legend of Arslan (2015)"]);
        var likesCurrent = IdCheck.Likeness("Arslan Senki", ["Arslan Senki"]);

        Assert.Equal((null, true), Of(IdCheck.Judge("519", "70000", "291630", "10910", null, likesShow, likesCurrent)));
    }

    // A later season in its own folder stays, however its folder reads.
    [Fact]
    public void LeavesALaterSeasonOfTheSameShow()
        => Assert.Equal(default, IdCheck.Judge("10376", "259640", "259640", "8692", null, 90, 40));

    // Only the folder name's year: the show's own year is the entry it has, possibly the wrong one.
    [Theory]
    [InlineData("/mnt/media/Anime/Series/2025/Sorairo Utility (Winter)", null)]
    [InlineData("/mnt/media/Anime/Series/!Collection/Kusuriya no Hitorigoto", null)]
    [InlineData("/media/anime/Ranma (1989)", 1989)]
    public void ReadsTheYearFromTheFolderNameOnly(string path, int? expected)
        => Assert.Equal(expected, IdCheck.FolderYear(path));

    // !Collection/Monogatari holds Season 01 to 05 and was held as its Second Season, an entry of the same TVDB show.
    [Fact]
    public void CorrectsASameShowEntryWhenTheFolderHoldsSeasonOne()
    {
        Assert.Equal(("6327", false), Of(IdCheck.Judge("9183", "102261", "102261", "6327", null, holdsSeasonOne: true)));
        Assert.Equal(default, IdCheck.Judge("9183", "102261", "102261", "6327", null, holdsSeasonOne: false));
    }

    [Fact]
    public void FindsASeasonOneFolder()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "animetaplus-season-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "Season 02"));
            Assert.False(IdCheck.HoldsSeasonOne(root));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, "Season 01"));
            Assert.True(IdCheck.HoldsSeasonOne(root));
            Assert.False(IdCheck.HoldsSeasonOne(System.IO.Path.Combine(root, "missing")));
        }
        finally
        {
            System.IO.Directory.Delete(root, true);
        }
    }
}
