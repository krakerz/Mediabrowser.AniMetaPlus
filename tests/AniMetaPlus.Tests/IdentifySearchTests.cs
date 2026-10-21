using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Providers.AniDB.Mapping;
using AniMetaPlus.Providers.AniDB.Metadata;
using AniMetaPlus.Sources;
using MediaBrowser.Model.Providers;
using Xunit;

namespace AniMetaPlus.Tests;

public class IdentifySearchTests
{
    private static IdentifyResult Local(string animeId, string name)
        => new() { AnimeId = animeId, Name = name };

    private static RemoteCandidate Hit(string source, string id, string title, int? year = 2020, string? animeId = null, string? image = "https://cdn.example/" + "p.jpg")
        => new(source, id, title, year, null, animeId, image);

    private static string? Titles(string id) => id == "9" ? "Dump Title" : null;

    private static IReadOnlyList<IdentifyResult> Merge(IReadOnlyList<IdentifyResult> local, IReadOnlyList<RemoteCandidate> remote, bool posters = true, bool unlinked = true, int cap = IdentifySearch.MaxResults)
        => IdentifySearch.Merge(local, remote, posters, unlinked, Titles, cap);

    [Fact]
    public void ALinkedHitEnrichesTheLocalResult()
    {
        var results = Merge([Local("1", "One")], [Hit(SourceIds.AniList, "100", "One", 2019, "1")]);

        var one = Assert.Single(results);
        Assert.Equal("One", one.Name);
        Assert.Equal(2019, one.Year);
        Assert.Equal("https://cdn.example/p.jpg", one.ImageUrl);
        Assert.Equal("100", one.Ids[SourceIds.AniList]);
    }

    [Fact]
    public void TheFirstPosterWinsAndTheNextHitStillAddsItsId()
    {
        var results = Merge(
            [Local("1", "One")],
            [Hit(SourceIds.AniList, "100", "One", animeId: "1", image: "https://a/1.jpg"), Hit(SourceIds.Tmdb, "5", "One", animeId: "1", image: "https://b/2.jpg")]);

        Assert.Equal("https://a/1.jpg", results[0].ImageUrl);
        Assert.Equal("5", results[0].Ids["Tmdb"]);
    }

    [Fact]
    public void ALinkedHitNotInTheListAddsAnAniDbResultNamedFromTheTitlesDump()
    {
        var results = Merge([Local("1", "One")], [Hit(SourceIds.Mal, "7", "Seven", animeId: "9"), Hit(SourceIds.AniList, "8", "Eight", animeId: "10")]);

        Assert.Equal(["1", "9", "10"], results.Select(r => r.AnimeId));
        Assert.Equal("Dump Title", results[1].Name);
        Assert.Equal("Eight", results[2].Name);
        Assert.Equal("7", results[1].Ids[SourceIds.Mal]);
    }

    [Fact]
    public void AnUnlinkedHitBecomesItsOwnMarkedResultCarryingOnlyItsId()
    {
        var results = Merge([], [Hit(SourceIds.AniList, "100", "Fresh", 2026), Hit(SourceIds.Mal, "200", "Other", 2025)]);

        Assert.Equal(["Fresh [AniList, not on AniDB]", "Other [MyAnimeList, not on AniDB]"], results.Select(r => r.Name));
        Assert.All(results, r => Assert.Null(r.AnimeId));
        Assert.Equal(2026, results[0].Year);
        Assert.Equal([new KeyValuePair<string, string>(SourceIds.AniList, "100")], results[0].Ids);
        Assert.Equal([new KeyValuePair<string, string>(SourceIds.Mal, "200")], results[1].Ids);
    }

    [Fact]
    public void AnUnlinkedMalHitRepeatingAnAniListOneIsDroppedOnlyWhenTitleAndYearMatch()
    {
        var results = Merge(
            [],
            [
                Hit(SourceIds.AniList, "100", "Fresh", 2026),
                Hit(SourceIds.Mal, "200", "FRESH", 2026, image: "https://m/x.jpg"),
                Hit(SourceIds.Mal, "201", "Fresh", 2024),
                Hit(SourceIds.Mal, "202", "Different", 2026)
            ]);

        Assert.Equal(["Fresh [AniList, not on AniDB]", "Fresh [MyAnimeList, not on AniDB]", "Different [MyAnimeList, not on AniDB]"], results.Select(r => r.Name));
        Assert.Equal("201", results[1].Ids[SourceIds.Mal]);
    }

    [Fact]
    public void ARepeatOfTheSameIdIsDropped()
    {
        var results = Merge([], [Hit(SourceIds.AniList, "100", "AniList 100", null, image: null), Hit(SourceIds.AniList, "100", "Fresh")]);

        Assert.Single(results);
    }

    [Fact]
    public void UnlinkedTvdbAndTmdbHitsAreNeverOffered()
    {
        Assert.Empty(Merge([], [Hit(SourceIds.Tvdb, "1", "A"), Hit(SourceIds.Tmdb, "2", "B")]));
    }

    [Fact]
    public void UnlinkedHitsCanBeLeftOutAltogether()
    {
        var results = Merge([Local("1", "One")], [Hit(SourceIds.AniList, "100", "Fresh"), Hit(SourceIds.AniList, "101", "One", animeId: "1")], unlinked: false);

        Assert.Single(results);
        Assert.Equal("101", results[0].Ids[SourceIds.AniList]);
    }

    [Fact]
    public void PostersAreLeftOutWhenTurnedOff()
    {
        var results = Merge([Local("1", "One")], [Hit(SourceIds.AniList, "100", "One", animeId: "1"), Hit(SourceIds.Mal, "200", "Fresh")], posters: false);

        Assert.All(results, r => Assert.Null(r.ImageUrl));
    }

    [Fact]
    public void ALocalPosterIsNotReplaced()
    {
        var one = Local("1", "One");
        one.ImageUrl = "https://kept/p.jpg";

        Merge([one], [Hit(SourceIds.AniList, "100", "One", animeId: "1")]);

        Assert.Equal("https://kept/p.jpg", one.ImageUrl);
    }

    [Fact]
    public void TheListIsOrderedAndCapped()
    {
        var local = Enumerable.Range(1, 3).Select(i => Local("L" + i, "Local " + i)).ToList();
        var remote = new List<RemoteCandidate>
        {
            Hit(SourceIds.AniList, "1", "Fresh"),
            Hit(SourceIds.AniList, "2", "Added", animeId: "A1"),
            Hit(SourceIds.AniList, "3", "Local 1", animeId: "L1")
        };

        var results = Merge(local, remote);

        Assert.Equal(["L1", "L2", "L3", "A1", null], results.Select(r => r.AnimeId));

        var many = Enumerable.Range(1, 40).Select(i => Hit(SourceIds.AniList, i.ToString(), "Show " + i)).ToList();
        Assert.Equal(IdentifySearch.MaxResults, Merge(local, many).Count);
        Assert.Equal(["L1", "L2"], Merge(local, many, cap: 2).Select(r => r.AnimeId));
    }

    [Theory]
    [InlineData("Show [AniList, not on AniDB]", "Show")]
    [InlineData("Show [MyAnimeList, not on AniDB]", "Show")]
    [InlineData("Show [BD]", null)]
    [InlineData("Show", null)]
    [InlineData(null, null)]
    public void TheMarkerIsTakenOffAName(string? name, string? expected)
        => Assert.Equal(expected, IdentifySearch.WithoutMarker(name));

    [Theory]
    [InlineData("https://cdn.anidb.net/images/main/1.jpg", true)]
    [InlineData("http://img7.anidb.net/pics/anime/1.jpg", true)]
    [InlineData("https://s4.anilist.co/file/1.jpg", false)]
    [InlineData("https://notanidb.net/1.jpg", false)]
    [InlineData("data:image/png;base64,AAAA", false)]
    public void AniDbAddressesAreToldFromTheRest(string url, bool expected)
        => Assert.Equal(expected, IdentifySearch.IsAniDbUrl(url));

    private static RemoteSearchResult SearchHit(string name, string? tvdb, string? tmdb, int? year, string? image)
    {
        var hit = new RemoteSearchResult { Name = name, ProductionYear = year, ImageUrl = image };
        if (tvdb != null)
        {
            hit.ProviderIds["Tvdb"] = tvdb;
        }

        if (tmdb != null)
        {
            hit.ProviderIds["Tmdb"] = tmdb;
        }

        return hit;
    }

    [Fact]
    public async Task OnlyDelegatedHitsThatMapToAniDbAreKept()
    {
        var hits = new[]
        {
            SearchHit("Mapped", "10", null, 2001, "https://t/10.jpg"),
            SearchHit("Unmapped", "11", null, 2002, null),
            SearchHit("No id", null, "5", 2003, null)
        };

        var kept = await DelegatedProviders.LinkedCandidates(
            SourceIds.Tvdb,
            hits,
            (id, _) => Task.FromResult<string?>(id == "10" ? "777" : null),
            CancellationToken.None);

        var one = Assert.Single(kept);
        Assert.Equal(new RemoteCandidate(SourceIds.Tvdb, "10", "Mapped", 2001, null, "777", "https://t/10.jpg"), one);
    }

    [Fact]
    public async Task ATmdbSearchReadsTheTmdbId()
    {
        var kept = await DelegatedProviders.LinkedCandidates(
            SourceIds.Tmdb,
            [SearchHit("Film", "3", "30", 2010, null)],
            (id, _) => Task.FromResult<string?>("A" + id),
            CancellationToken.None);

        Assert.Equal("A30", Assert.Single(kept).AniDbId);
    }

    [Theory]
    [InlineData(1, 0, false, false, null, "base show")]
    [InlineData(1, 12, false, false, null, "season 1, from ep 13")]
    [InlineData(2, 0, false, false, null, "season 2")]
    [InlineData(3, 24, false, false, null, "season 3, from ep 25")]
    [InlineData(0, 0, false, false, null, "special")]
    [InlineData(null, 0, true, false, null, "base show (absolute)")]
    [InlineData(null, 0, false, true, null, "movie")]
    [InlineData(0, 0, false, true, null, "movie")]
    public void TheRoleComesFromTheMappings(int? season, int offset, bool absolute, bool movie, string? format, string? expected)
        => Assert.Equal(expected, IdentifySearch.RoleLabel(new MappedRole(season, offset, absolute, movie), format));

    [Fact]
    public void WithoutMappingsTheRoleFallsBackToTheFormat()
    {
        Assert.Equal("TV", IdentifySearch.RoleLabel(null, "TV"));
        Assert.Equal("OVA", IdentifySearch.RoleLabel(new MappedRole(null, 0, false, false), "OVA"));
        Assert.Null(IdentifySearch.RoleLabel(null, null));
    }

    [Theory]
    [InlineData("TV", "TV")]
    [InlineData("MOVIE", "Movie")]
    [InlineData("TV_SHORT", "TV short")]
    [InlineData("ova", "OVA")]
    [InlineData("SPECIAL", "Special")]
    [InlineData(" ", null)]
    [InlineData(null, null)]
    public void FormatsAreWrittenAsLabels(string? format, string? expected)
        => Assert.Equal(expected, IdentifySearch.FormatLabel(format));

    [Fact]
    public void AHitsFormatBecomesTheFallbackForItsResult()
    {
        var results = Merge(
            [Local("1", "One")],
            [new RemoteCandidate(SourceIds.AniList, "100", "One", 2019, "MOVIE", "1"), new RemoteCandidate(SourceIds.Tvdb, "5", "One", 2019, "Series", "1"), new RemoteCandidate(SourceIds.Mal, "7", "Fresh", 2026, "ona", null)]);

        Assert.Equal("Movie", results[0].Format);
        Assert.Equal("ONA", results[1].Format);
    }

    [Fact]
    public void AMatchIsRecordedWhenAResultIsAddedAndKeptWhenItIsEnriched()
    {
        var one = Local("1", "One");
        one.Match = "title match 92%";

        var results = Merge(
            [one],
            [
                Hit(SourceIds.AniList, "100", "One", animeId: "1"),
                Hit(SourceIds.Mal, "7", "Seven", animeId: "9"),
                Hit(SourceIds.Tvdb, "8", "Eight", animeId: "10"),
                Hit(SourceIds.Tmdb, "9", "Nine", animeId: "11"),
                Hit(SourceIds.AniList, "200", "Fresh")
            ]);

        Assert.Equal(["title match 92%", "via MyAnimeList", "via TheTVDB", "via TheMovieDb", "not on AniDB"], results.Select(r => r.Match));
    }

    [Fact]
    public void TheSitesAreListedInOrder()
    {
        var all = Local("1", "One");
        all.Ids["Tmdb"] = "1";
        all.Ids["Tvdb"] = "2";
        all.Ids[SourceIds.Mal] = "3";
        all.Ids[SourceIds.AniList] = "4";

        var unlinked = new IdentifyResult();
        unlinked.Ids[SourceIds.Mal] = "3";

        Assert.Equal("AniDB \u00b7 AniList \u00b7 MAL \u00b7 TVDB \u00b7 TMDB", IdentifySearch.SitesLine(all));
        Assert.Equal("MAL", IdentifySearch.SitesLine(unlinked));
        Assert.Equal("AniDB", IdentifySearch.SitesLine(Local("2", "Two")));
    }

    [Fact]
    public void ALabelUrlSurvivesBeingBuiltAndParsed()
    {
        var label = new PosterLabel("https://s4.anilist.co/file/a b&c.jpg?x=1", "title match 92%", "season 2, from ep 13", "AniDB \u00b7 AniList");

        var url = IdentifyLabel.Url(label);

        Assert.True(IdentifyLabel.Is(url));
        Assert.Matches("^animetaplus-label:\\?p=[A-Za-z0-9%._~-]*&m=[A-Za-z0-9%._~-]*&r=[A-Za-z0-9%._~-]*&s=[A-Za-z0-9%._~-]*&v=[0-9]+$", url);
        Assert.True(IdentifyLabel.TryParse(url, out var parsed));
        Assert.Equal(label, parsed);
    }

    [Theory]
    [InlineData("base show", "Base Show")]
    [InlineData("season 4, from ep 13", "Season 4, From Ep 13")]
    [InlineData("via AniList", "Via AniList")]
    [InlineData("not on AniDB", "Not On AniDB")]
    [InlineData("TV special", "TV Special")]
    [InlineData("title match 100%", "Title Match 100%")]
    [InlineData("base show (absolute)", "Base Show (Absolute)")]
    public void LabelLinesAreTitleCased(string text, string expected)
        => Assert.Equal(expected, IdentifyLabel.TitleCase(text));

    [Fact]
    public void AnEmptyPosterAndOtherUrlsAreHandled()
    {
        Assert.True(IdentifyLabel.TryParse(IdentifyLabel.Url(new PosterLabel(string.Empty, "m", "r", "s")), out var parsed));
        Assert.Equal(string.Empty, parsed.Poster);
        Assert.False(IdentifyLabel.Is("https://s4.anilist.co/file/1.jpg"));
        Assert.False(IdentifyLabel.TryParse("data:image/png;base64,AAAA", out _));
    }

    [Fact]
    public void TheSvgEscapesItsText()
    {
        var svg = IdentifyLabel.Svg(new PosterLabel(string.Empty, "a <b> & \"c\" 'd'", "x&y", "<s>"), null);

        Assert.Contains("A &lt;B&gt; &amp; &quot;C&quot; &apos;D&apos;", svg);
        Assert.Contains("X&amp;Y", svg);
        Assert.Contains("&lt;s&gt;", svg);
        Assert.DoesNotContain("<b>", svg);
        System.Xml.Linq.XDocument.Parse(svg);
    }

    [Fact]
    public void ALongLineIsCutWithAnEllipsis()
    {
        var long28 = new string('a', 28);
        Assert.Equal(long28, IdentifyLabel.Fit(long28));

        var cut = IdentifyLabel.Fit(new string('a', 29));
        Assert.Equal(28, cut.Length);
        Assert.EndsWith("\u2026", cut);

        Assert.Contains("B" + new string('b', 26) + "\u2026", IdentifyLabel.Svg(new PosterLabel(string.Empty, new string('b', 60), "r", "s"), null));
    }

    [Fact]
    public void ThePosterIsEmbeddedAndNothingIsLoadedFromOutside()
    {
        var svg = IdentifyLabel.Svg(new PosterLabel("https://s4.anilist.co/p.jpg", "m", "base show", "AniDB"), "data:image/jpeg;base64,/9j/4AAQ+=");

        Assert.Contains("xlink:href=\"data:image/jpeg;base64,/9j/4AAQ+=\"", svg);
        Assert.Contains("preserveAspectRatio=\"xMidYMid slice\"", svg);
        Assert.Contains("viewBox=\"0 0 400 600\"", svg);
        Assert.Contains("#52b54b", svg);
        Assert.DoesNotContain("anilist.co", svg);
        Assert.DoesNotMatch("href=\"(?!data:)", svg);
        Assert.DoesNotContain("Local only", svg);
    }

    [Fact]
    public void WithoutAPosterTheBackgroundSaysLocalOnly()
    {
        var svg = IdentifyLabel.Svg(new PosterLabel(string.Empty, "m", "season 2", "AniDB"), null);

        Assert.Contains("Local only", svg);
        Assert.DoesNotContain("<image", svg);
        Assert.DoesNotContain("#52b54b", svg);
        Assert.Contains("No Season Info", IdentifyLabel.Svg(new PosterLabel(string.Empty, "m", string.Empty, "s"), null));
    }

    [Fact]
    public void ADataUrlThatIsNotAnImageIsNotEmbedded()
    {
        var svg = IdentifyLabel.Svg(new PosterLabel(string.Empty, "m", "r", "s"), "data:text/html;base64,AAAA\"/><script>");

        Assert.DoesNotContain("<image", svg);
        Assert.DoesNotContain("<script>", svg);
    }

    [Theory]
    [InlineData("https://s4.anilist.co/file/1.jpg", true)]
    [InlineData("https://cdn.myanimelist.net/images/1.jpg", true)]
    [InlineData("https://artworks.thetvdb.com/a.jpg", true)]
    [InlineData("https://image.tmdb.org/t/p/w500/a.jpg", true)]
    [InlineData("https://evil.example/a.jpg", false)]
    [InlineData("https://anilist.co.evil.example/a.jpg", false)]
    [InlineData("http://127.0.0.1/a.jpg", false)]
    [InlineData("ftp://s4.anilist.co/a.jpg", false)]
    [InlineData("data:image/png;base64,AAAA", false)]
    public void OnlyTheSourcesImageHostsMayBeFetched(string url, bool expected)
        => Assert.Equal(expected, IdentifySearch.IsPosterUrl(url, out _));

    [Fact]
    public async Task ALabelWhosePosterIsOnAnotherHostIsDrawnWithoutIt()
    {
        var url = IdentifyLabel.Url(new PosterLabel("https://evil.example/a.jpg", "title match 90%", "base show", "AniDB"));

        using var response = await IdentifyLabel.GetImage(url, CancellationToken.None);

        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        var svg = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Contains("Local only", svg);
        Assert.Contains("Title Match 90%", svg);
        await Assert.ThrowsAsync<System.ArgumentException>(() => IdentifySearch.GetRemotePoster("https://evil.example/a.jpg", CancellationToken.None));
    }
}
