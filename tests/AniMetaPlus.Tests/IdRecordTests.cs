using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AniMetaPlus.Api;
using AniMetaPlus.Core;
using AniMetaPlus.Data;
using AniMetaPlus.Providers.AniDB.Metadata;
using Xunit;

namespace AniMetaPlus.Tests;

public class IdRecordTests
{
    private static Dictionary<string, string> Ids(string aniDb, string mal = "", string aniList = "")
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["AniDB"] = aniDb };
        if (mal.Length > 0)
        {
            ids["MyAnimeList"] = mal;
        }

        if (aniList.Length > 0)
        {
            ids["AniList"] = aniList;
        }

        return ids;
    }

    [Fact]
    public void NamesTheEvidenceOfACorrection()
    {
        Assert.Equal("TVDB id and folder name agree", IdCheck.Judge("8805", "246381", "455684", "18935", "18935").Reason);
        Assert.Equal("TVDB id and folder name agree", IdCheck.Judge("9904", "81891", "81891", "5787", "5787").Reason);
        Assert.Equal("folder holds Season 01", IdCheck.Judge("9183", "102261", "102261", "6327", null, holdsSeasonOne: true).Reason);
        Assert.Equal("folder name reads as the TVDB entry", IdCheck.Judge("1", "100", "200", "2", null, 95, 10).Reason);
    }

    [Fact]
    public void NamesNoReasonWhereTheIdFits()
        => Assert.Null(IdCheck.Judge("8692", "259640", "259640", "8692", null).Reason);

    [Fact]
    public void NamesAReasonForASuspect()
    {
        var verdict = IdCheck.Judge("8805", "246381", "455684", "18935", null);

        Assert.True(verdict.Suspect);
        Assert.False(string.IsNullOrEmpty(verdict.Reason));
    }

    [Fact]
    public void SkipsAShowSetByHandWhateverItsIds()
    {
        var record = new IdRecord { Status = IdRecord.Manual, NewIds = Ids("1") };

        Assert.True(IdCheck.SkipsCheck(record, Ids("1")));
        Assert.True(IdCheck.SkipsCheck(record, Ids("2")));
    }

    [Fact]
    public void SkipsAConfirmedShowWhileItsIdsAreTheConfirmedOnes()
    {
        var record = new IdRecord { Status = IdRecord.Verified, NewIds = Ids("1", "10", "100") };

        Assert.True(IdCheck.SkipsCheck(record, Ids("1", "10", "100")));

        // Ids it carries beyond those three are not what was confirmed.
        var more = Ids("1", "10", "100");
        more["Tvdb"] = "5";
        Assert.True(IdCheck.SkipsCheck(record, more));
    }

    [Theory]
    [InlineData("2", "10", "100")]
    [InlineData("1", "11", "100")]
    [InlineData("1", "", "100")]
    [InlineData("1", "10", "")]
    public void ChecksAConfirmedShowAgainOnceItsIdsChange(string aniDb, string mal, string aniList)
        => Assert.False(IdCheck.SkipsCheck(new IdRecord { Status = IdRecord.Verified, NewIds = Ids("1", "10", "100") }, Ids(aniDb, mal, aniList)));

    [Theory]
    [InlineData(IdRecord.Corrected)]
    [InlineData(IdRecord.Suspect)]
    public void ChecksCorrectedAndSuspectShows(string status)
        => Assert.False(IdCheck.SkipsCheck(new IdRecord { Status = status, NewIds = Ids("1") }, Ids("1")));

    [Fact]
    public void ChecksAShowWithoutARecord()
        => Assert.False(IdCheck.SkipsCheck(null, Ids("1")));

    [Fact]
    public void ReadsCorrectedIdsWithTheRemovedOnesGone()
    {
        var old = Ids("1", "10", "100");
        old["Tvdb"] = "5";

        var corrected = IdCheck.CorrectedIds(old, new Dictionary<string, string> { ["AniDB"] = "2", ["MyAnimeList"] = string.Empty });

        Assert.Equal("2", corrected["AniDB"]);
        Assert.False(corrected.ContainsKey("MyAnimeList"));
        Assert.Equal("100", corrected["AniList"]);
        Assert.Equal("5", corrected["Tvdb"]);
    }

    [Fact]
    public void ReadsTheHandIdsTrimmed()
    {
        var ids = AniMetaApi.ParseHandIds(" 123 ", "", null);

        Assert.Equal("123", ids["AniDB"]);
        Assert.Equal(string.Empty, ids["MyAnimeList"]);
        Assert.Equal(string.Empty, ids["AniList"]);
        Assert.Equal("55", AniMetaApi.ParseHandIds("1", "55", "66")["MyAnimeList"]);
    }

    [Fact]
    public void AcceptsHandIdsWithNoAniDb()
    {
        var malOnly = AniMetaApi.ParseHandIds("", "100", "");
        Assert.Equal(string.Empty, malOnly["AniDB"]);
        Assert.Equal("100", malOnly["MyAnimeList"]);

        var aniListOnly = AniMetaApi.ParseHandIds(null, "", "200");
        Assert.Equal(string.Empty, aniListOnly["AniDB"]);
        Assert.Equal("200", aniListOnly["AniList"]);
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData(null, "", "")]
    [InlineData(null, null, null)]
    [InlineData("12a", "", "")]
    [InlineData("https://anidb.net/anime/12", "", "")]
    [InlineData("-1", "", "")]
    [InlineData("1", "x", "")]
    [InlineData("1", "", "1.5")]
    [InlineData("1", "١٢", "")]
    public void RefusesHandIdsThatAreNotDigits(string? aniDb, string? mal, string? aniList)
        => Assert.Throws<ArgumentException>(() => AniMetaApi.ParseHandIds(aniDb, mal, aniList));

    [Fact]
    public void ReadsAniListBeforeMyAnimeList()
    {
        var links = AniDbSeriesProvider.LinksFromIds(new Dictionary<string, string> { ["MyAnimeList"] = "100", ["AniList"] = "200" });

        Assert.Equal(new[] { "anilist:200", "mal:100" }, links);
    }

    [Fact]
    public void ReadsNoLinkFromAnIdThatIsNotDigits()
    {
        var links = AniDbSeriesProvider.LinksFromIds(new Dictionary<string, string> { ["AniList"] = "x", ["MyAnimeList"] = "100" });

        Assert.Equal(new[] { "mal:100" }, links);
    }

    [Fact]
    public void ReadsNoLinkWhereThereIsNoId()
        => Assert.Empty(AniDbSeriesProvider.LinksFromIds(new Dictionary<string, string> { ["AniDB"] = "1" }));

    [Fact]
    public async Task KeepsTheAniDbIdTheOwnerGave()
    {
        var ids = AniMetaApi.ParseHandIds("123", "", "");

        var animeId = await AniMetaApi.HandAniDbId(ids, _ => Task.FromResult<string?>("999"));

        Assert.Equal("123", animeId);
        Assert.Equal("123", ids["AniDB"]);
    }

    [Fact]
    public async Task TakesTheAniDbIdTheMappingsNameUnderTheOtherIds()
    {
        var ids = AniMetaApi.ParseHandIds("", "100", "");
        IReadOnlyList<string>? asked = null;

        var animeId = await AniMetaApi.HandAniDbId(ids, links =>
        {
            asked = links;

            return Task.FromResult<string?>("456");
        });

        Assert.Equal("456", animeId);
        Assert.Equal("456", ids["AniDB"]);
        Assert.Equal(new[] { "mal:100" }, asked);
    }

    [Fact]
    public async Task LeavesTheShowWithoutAnAniDbIdWhereTheMappingsNameNone()
    {
        var ids = AniMetaApi.ParseHandIds("", "", "200");

        var animeId = await AniMetaApi.HandAniDbId(ids, _ => Task.FromResult<string?>(null));

        Assert.Null(animeId);
        Assert.Equal(string.Empty, ids["AniDB"]);
    }

    [Fact]
    public void ReadsTheTvdbAndTmdbHandIds()
    {
        var ids = AniMetaApi.ParseHandIds("", "", "", "480909", "");

        Assert.Equal("480909", ids["Tvdb"]);
        Assert.Equal(string.Empty, ids["Tmdb"]);
        Assert.Equal(string.Empty, ids["AniDB"]);
        Assert.Equal("77", AniMetaApi.ParseHandIds("", "", "", "", " 77 ")["Tmdb"]);
    }

    [Theory]
    [InlineData("", "", "", "12a", "")]
    [InlineData("", "", "", "", "x")]
    [InlineData("", "", "", "", "")]
    public void RefusesTvdbAndTmdbHandIdsThatAreNotDigitsOrAllEmpty(string aniDb, string mal, string aniList, string tvdb, string tmdb)
        => Assert.Throws<ArgumentException>(() => AniMetaApi.ParseHandIds(aniDb, mal, aniList, tvdb, tmdb));

    [Fact]
    public async Task AsksForTheAniDbIdUnderTheTvdbIdWhereNoLinkResolves()
    {
        var ids = AniMetaApi.ParseHandIds("", "", "", "480909", "");
        string? askedTvdb = null;
        string? askedTmdb = null;

        var animeId = await AniMetaApi.HandAniDbId(
            ids,
            _ => Task.FromResult<string?>(null),
            (tvdb, tmdb) =>
            {
                askedTvdb = tvdb;
                askedTmdb = tmdb;

                return Task.FromResult<string?>("18777");
            });

        Assert.Equal("18777", animeId);
        Assert.Equal("18777", ids["AniDB"]);
        Assert.Equal("480909", askedTvdb);
        Assert.Equal(string.Empty, askedTmdb);
    }

    [Fact]
    public async Task DoesNotAskUnderTheTvdbIdWhereTheLinksResolve()
    {
        var ids = AniMetaApi.ParseHandIds("", "100", "", "480909", "");
        var asked = false;

        var animeId = await AniMetaApi.HandAniDbId(
            ids,
            _ => Task.FromResult<string?>("456"),
            (_, _) =>
            {
                asked = true;

                return Task.FromResult<string?>("1");
            });

        Assert.Equal("456", animeId);
        Assert.False(asked);
    }

    [Theory]
    [InlineData(IdRecord.Corrected, "", "9", true)]
    [InlineData(IdRecord.TvdbConflict, "452665", "480909", true)]
    [InlineData(IdRecord.TvdbConflict, "452665", "452665", false)]
    [InlineData(IdRecord.TvdbConflict, "", "480909", false)]
    [InlineData(IdRecord.Suspect, "452665", "480909", false)]
    [InlineData(IdRecord.Manual, "452665", "480909", false)]
    public void RevertsOnlyACorrectionOrAReplacedTvdbId(string status, string oldTvdb, string currentTvdb, bool expected)
    {
        var record = new IdRecord { Status = status };
        if (oldTvdb.Length > 0)
        {
            record.OldIds["Tvdb"] = oldTvdb;
        }

        Assert.Equal(expected, AniMetaApi.CanRevert(record, new Dictionary<string, string> { ["Tvdb"] = currentTvdb }));
        Assert.False(AniMetaApi.CanRevert(null, new Dictionary<string, string>()));
    }
}
