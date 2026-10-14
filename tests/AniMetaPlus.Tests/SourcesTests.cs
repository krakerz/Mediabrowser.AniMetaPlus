using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Serialization;
using AniMetaPlus.Api;
using AniMetaPlus.Configuration;
using AniMetaPlus.Core;
using AniMetaPlus.Data;
using AniMetaPlus.Sources;
using Xunit;

namespace AniMetaPlus.Tests;

public class SourcesTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, BlockScope.Source, BlockReason.RateLimited)]
    [InlineData(HttpStatusCode.ServiceUnavailable, BlockScope.Source, BlockReason.RateLimited)]
    [InlineData(HttpStatusCode.Unauthorized, BlockScope.Source, BlockReason.AuthFailed)]
    [InlineData(HttpStatusCode.Forbidden, BlockScope.Source, BlockReason.AuthFailed)]
    [InlineData(HttpStatusCode.NotFound, BlockScope.Item, BlockReason.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, BlockScope.Source, BlockReason.ServerError)]
    [InlineData(HttpStatusCode.BadRequest, BlockScope.Item, BlockReason.Other)]
    public void ClassifiesStatusCodes(HttpStatusCode status, BlockScope scope, BlockReason reason)
    {
        var failure = BlockStore.Classify(new SourceHttpException("x", status, null));

        Assert.Equal(scope, failure.Scope);
        Assert.Equal(reason, failure.Reason);
        Assert.Equal((int)status, failure.HttpStatus);
    }

    [Fact]
    public void ClassifiesOtherFailures()
    {
        Assert.Equal(BlockReason.Unavailable, BlockStore.Classify(new HttpRequestException("down")).Reason);
        Assert.Equal(BlockScope.Source, BlockStore.Classify(new TimeoutException()).Scope);
        Assert.Equal(BlockReason.NotFound, BlockStore.Classify(new KeyNotFoundException()).Reason);
        Assert.Equal(BlockReason.ParseFailed, BlockStore.Classify(new JsonException()).Reason);
        Assert.Equal(BlockScope.Item, BlockStore.Classify(new JsonException()).Scope);
    }

    [Fact]
    public void KeepsTheRetryAfterTheSourceAsksFor()
    {
        var failure = BlockStore.Classify(new SourceHttpException("slow down", HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(7)));

        Assert.Equal(TimeSpan.FromMinutes(7), failure.RetryAfter);
    }

    [Fact]
    public void BackoffsGrowWithSeverity()
    {
        Assert.True(BlockStore.Backoff(BlockReason.Banned, BlockScope.Source).Initial > BlockStore.Backoff(BlockReason.RateLimited, BlockScope.Source).Initial);
        Assert.Equal(TimeSpan.FromHours(24), BlockStore.Backoff(BlockReason.Banned, BlockScope.Source).Maximum);
        Assert.Equal(TimeSpan.FromDays(7), BlockStore.Backoff(BlockReason.NotFound, BlockScope.Item).Initial);
    }

    [Fact]
    public void BlockIdsIgnoreCase()
        => Assert.Equal(
            BlockRecord.MakeId("AniDB", BlockScope.Item, "anidb:23", "Overview"),
            BlockRecord.MakeId("anidb", BlockScope.Item, "ANIDB:23", "overview"));

    [Fact]
    public void ReadsAniListMedia()
    {
        const string json = """
            {
              "id": 1, "idMal": 1, "isAdult": false, "episodes": 26,
              "title": { "romaji": "Cowboy Bebop", "english": "Cowboy Bebop", "native": "カウボーイビバップ" },
              "description": "Enter a world.<br><br>Spike &amp; Jet.<br>(Source: Sunrise)",
              "startDate": { "year": 1998, "month": 4, "day": 3 },
              "endDate": { "year": 1999, "month": 4, "day": 24 },
              "genres": ["Action", "Sci-Fi"],
              "tags": [ { "name": "Space", "rank": 94, "isAdult": false, "isMediaSpoiler": false }, { "name": "Twist", "rank": 80, "isAdult": false, "isMediaSpoiler": true } ],
              "averageScore": 86,
              "stats": { "scoreDistribution": [ { "score": 90, "amount": 1000 }, { "score": 80, "amount": 500 } ] },
              "studios": { "nodes": [ { "name": "Sunrise" } ] },
              "coverImage": { "extraLarge": "https://img/cover.jpg", "large": null },
              "bannerImage": "https://img/banner.jpg",
              "characters": { "edges": [ { "role": "MAIN", "node": { "name": { "full": "Spike Spiegel" }, "image": { "large": null } }, "voiceActors": [ { "id": 95011, "name": { "full": "Kouichi Yamadera" }, "image": { "large": "https://img/va.jpg" } } ] } ] },
              "staff": { "edges": [ { "role": "Director", "node": { "id": 95, "name": { "full": "Shinichirou Watanabe" }, "image": { "large": null } } } ] }
            }
            """;

        using var document = JsonDocument.Parse(json);
        var record = AniListClient.Read(document.RootElement);

        Assert.Equal(SourceIds.AniList, record.Source);
        Assert.Equal("1", record.Ids[SourceIds.Mal]);
        Assert.Equal("Cowboy Bebop", record.RomajiTitle);
        Assert.Equal("Enter a world.\n\nSpike & Jet.", record.Overview);
        Assert.Equal(new DateTime(1998, 4, 3, 0, 0, 0, DateTimeKind.Utc), record.StartDate);
        Assert.Equal(8.6, record.Rating);
        Assert.Equal(1500, record.Votes);
        Assert.Contains(record.Genres, g => g.Name == "Sci-Fi" && g.Weight == 1);
        Assert.Contains(record.Genres, g => g.Name == "Space" && g.Weight < 1);
        Assert.DoesNotContain(record.Genres, g => g.Name == "Twist");
        Assert.Equal(["Sunrise"], record.Studios);
        Assert.Contains(record.Images, i => i.Type == "Primary" && i.Url == "https://img/cover.jpg");
        Assert.Contains(record.Images, i => i.Type == "Banner");
        Assert.Contains(record.People, p => p.Name == "Kouichi Yamadera" && p.Role == "Spike Spiegel" && p.Kind == "Actor");
        Assert.Contains(record.People, p => p.Name == "Shinichirou Watanabe" && p.Kind == "Director");
    }

    [Theory]
    [InlineData("Director", "Director")]
    [InlineData("Original Creator", "Writer")]
    [InlineData("Music", "Composer")]
    [InlineData("Character Design", "Artist")]
    [InlineData("Theme Song Performance", "Unknown")]
    public void SortsAniListStaffRoles(string role, string kind) => Assert.Equal(kind, AniListClient.StaffKind(role));

    [Theory]
    [InlineData("Watanabe Shinichirou", "Shinichirou Watanabe")]
    [InlineData("Yamadera Kouichi", "Kouichi Yamadera")]
    [InlineData("Kana Hanazawa", "kana hanazawa")]
    public void MatchesNamesWrittenEitherWayRound(string a, string b) => Assert.Equal(SeriesMerge.NameKey(a), SeriesMerge.NameKey(b));

    [Theory]
    [InlineData("Sci-Fi", "Science Fiction")]
    [InlineData("Mahou Shoujo", "Magical Girl")]
    [InlineData("space opera", "Space Opera")]
    public void NormalizesGenres(string name, string expected) => Assert.Equal(expected, GenreAliases.Normalize(name, true));

    [Theory]
    [InlineData("23", "23")]
    [InlineData(" 4242 ", "4242")]
    [InlineData("https://anidb.net/anime/17617", "17617")]
    [InlineData("https://anidb.net/anime/17617/relation/graph", "17617")]
    [InlineData("http://anidb.net/perl-bin/animedb.pl?show=anime&aid=69", "69")]
    [InlineData("frieren", null)]
    [InlineData("", null)]
    public void ParsesAnimeIdsAndLinks(string value, string? expected) => Assert.Equal(expected, AniMetaApi.ParseAnimeId(value));

    [Fact]
    public void ScoresCloseTitlesHigher()
    {
        Assert.Equal(100, Queues.Similarity("Cowboy Bebop", "cowboy bebop!"));
        Assert.True(Queues.Similarity("Shingeki no Kyojin", "Shingeki no Kyojin (2017)") > Queues.Similarity("Shingeki no Kyojin", "Cowboy Bebop"));
    }

    [Fact]
    public void BuildsStableItemKeys()
    {
        Assert.Equal("anidb:23", ItemKeys.ForAnime("23"));
        Assert.Equal("23", ItemKeys.AnimeId("anidb:23"));
        Assert.Null(ItemKeys.AnimeId("series:bebop"));
        Assert.Equal("series:cowboy bebop", ItemKeys.ForUnidentified("Series", "/anime/Cowboy Bebop/", null));
        Assert.Equal("movie:akira (1988)", ItemKeys.ForUnidentified("Movie", "/films/Akira (1988).mkv", null));
        Assert.Equal("series:frieren", ItemKeys.ForUnidentified("Series", null, "Frieren"));
    }

    [Fact]
    public void ReadsAniDbExtras()
    {
        var path = Path.Combine(Path.GetTempPath(), "animetaplus-extras-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, """
            <anime id="1" restricted="false">
              <type>TV Series</type>
              <ratings><permanent count="4321">8.13</permanent></ratings>
              <resources>
                <resource type="2"><externalentity><identifier>1</identifier></externalentity><externalentity><identifier>0</identifier></externalentity></resource>
                <resource type="1"><externalentity><identifier>13</identifier></externalentity></resource>
              </resources>
            </anime>
            """);

        try
        {
            var extras = AniDbExtras.Read(path);

            Assert.Equal(["0", "1"], extras.MalIds);
            Assert.Equal(["13"], extras.AnnIds);
            Assert.Equal(4321, extras.RatingVotes);
            Assert.Equal("TV Series", extras.Type);
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Same(AniDbExtras.None, AniDbExtras.Read(path));
    }

    [Fact]
    public void FallsBackToDefaultSourceSettingsAndOrders()
    {
        var configuration = new PluginConfiguration();

        Assert.Equal(FallbackMode.SwitchReview, configuration.GetSource(SourceIds.AniDb).Mode);
        Assert.Equal(FallbackMode.SwitchUpgrade, configuration.GetSource(SourceIds.AniList).Mode);
        Assert.Equal(SourceIds.AniDb, configuration.GetPriority(DataKind.Overview)[0]);

        configuration.Priorities.Add(new KindPriority { Kind = "Overview", Sources = "AniList, AniDB,AniList,," });
        Assert.Equal([SourceIds.AniList, SourceIds.AniDb], configuration.GetPriority(DataKind.Overview));
    }

    [Fact]
    public void SerializesSourceListsWithoutDoublingThem()
    {
        var configuration = new PluginConfiguration { AniDbClientName = "  myclient  ", TitlesRefreshDays = 0 };
        configuration.Sources.Add(new SourceSetting { Id = SourceIds.AniList, Mode = FallbackMode.Pause, MaxPerHour = 30 });

        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, configuration);

        PluginConfiguration read;
        using (var reader = new StringReader(writer.ToString()))
        {
            read = (PluginConfiguration)serializer.Deserialize(reader)!;
        }

        Assert.Equal("myclient", read.AniDbClientName);
        Assert.True(read.AniDbConfigured);
        Assert.Equal(PluginConfiguration.MinimumTitlesRefreshDays, read.TitlesRefreshDays);
        var source = Assert.Single(read.Sources);
        Assert.Equal(FallbackMode.Pause, source.Mode);
        Assert.Equal(30, source.MaxPerHour);
        Assert.True(read.IncludeAdultTags);
        Assert.True(read.PublishMappedIds);
    }

    [Fact]
    public void PicksTitlesByPreference()
    {
        var record = new AnimeRecord { RomajiTitle = "Sousou no Frieren", EnglishTitle = "Frieren", NativeTitle = "葬送のフリーレン" };

        Assert.Equal("Sousou no Frieren", record.Title("JapaneseRomaji"));
        Assert.Equal("葬送のフリーレン", record.Title("Japanese"));
        Assert.Equal("Frieren", record.Title("Localized"));
        Assert.Equal("Sousou no Frieren", new AnimeRecord { RomajiTitle = "Sousou no Frieren" }.Title("Localized"));
    }

    [Fact]
    public void KnowsWhichKindsCombine()
    {
        Assert.True(SourceDefaults.IsCombined(DataKind.Genres));
        Assert.True(SourceDefaults.IsCombined(DataKind.Rating));
        Assert.True(SourceDefaults.IsCombined(DataKind.People));
        Assert.False(SourceDefaults.IsCombined(DataKind.Overview));
        Assert.All(Enum.GetValues<DataKind>(), kind => Assert.NotEmpty(SourceDefaults.Priority(kind)));
        Assert.True(SourceIds.IsDelegated("thetvdb"));
        Assert.False(SourceIds.IsDelegated(SourceIds.AniList));
    }

    [Theory]
    [InlineData("Nyanbo! (Fall)", "Nyanbo!")]
    [InlineData("Show [BD 1080p] (Season 2)", "Show")]
    [InlineData("Ranma ½ (1989)", "Ranma ½ (1989)")]
    [InlineData("Plain", "Plain")]
    public void StripsFolderNotesButKeepsYears(string name, string expected) => Assert.Equal(expected, Queues.StripNotes(name));
}
