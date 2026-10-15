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
    [InlineData(SourceIds.AniDb, 2500)]
    [InlineData(SourceIds.AniList, 2100)]
    [InlineData(SourceIds.Mal, 1100)]
    [InlineData("thetvdb", 250)]
    public void RequestGapNeverGoesBelowTheSourcesFloor(string id, int floor)
    {
        var setting = new SourceSetting { Id = id, RequestIntervalMs = 0 };
        Assert.Equal(floor, setting.RequestIntervalMs);

        setting.RequestIntervalMs = floor + 500;
        Assert.Equal(floor + 500, setting.RequestIntervalMs);
        Assert.Equal(floor, SourceDefaults.Setting(id).RequestIntervalMs);
    }

    [Theory]
    [InlineData(0.2, "1 s")]
    [InlineData(40, "40 s")]
    [InlineData(120, "2 min")]
    [InlineData(208.3, "3 min 29 s")]
    public void WritesWaitsTheWayAPersonSaysThem(double seconds, string expected)
        => Assert.Equal(expected, AniMetaApi.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void MapsMyAnimeListAndAniListIdsToTheirAniDbEntry()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                {
                  "$meta": { "schema_version": "3.0.0" },
                  "anidb:11021:R": { "anilist:21222": { "1-4": "1-4" }, "mal:29575": { "1-4": "1-4" } },
                  "anidb:11022:S": { "mal:40000": { "1": "1" } },
                  "mal:29575": { "anidb:11021:R": { "1-4": "1-4" } }
                }
                """);

            var index = AniMetaPlus.Providers.AniDB.Mapping.AniBridgeIndex.Parse(path, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, DateTime.UtcNow, "test");

            Assert.Equal("11021", index.AnimeIdByLink("mal", "29575"));
            Assert.Equal("11021", index.AnimeIdByLink("anilist", "21222"));
            Assert.Null(index.AnimeIdByLink("mal", "40000"));
            Assert.Null(index.AnimeIdByLink("mal", "21222"));
            Assert.Null(index.AnimeIdByLink("mal", "1"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("11021", "11021")]
    [InlineData("https://anidb.net/anime/11021", "11021")]
    [InlineData("anilist:21222", "anilist:21222")]
    [InlineData("https://anilist.co/anime/21222/Mankitsu-Happening/", "anilist:21222")]
    [InlineData("mal:29575", "mal:29575")]
    [InlineData("https://myanimelist.net/anime/29575/Mankitsu_Happening", "mal:29575")]
    [InlineData("anilist:", null)]
    [InlineData("not an id", null)]
    public void ReadsWhatAnItemIsMappedTo(string input, string? expected)
        => Assert.Equal(expected, AniMetaApi.ParseMapping(input));

    [Fact]
    public void AShutdownIsNotTheSourcesFault()
    {
        Assert.True(BlockStore.IsShutdown(new ObjectDisposedException("IServiceProvider")));
        Assert.True(BlockStore.IsShutdown(new InvalidOperationException("Cannot access a disposed object.\nObject name: 'IServiceProvider'.")));
        Assert.True(BlockStore.IsShutdown(new HttpRequestException("failed", new ObjectDisposedException("x"))));
        Assert.False(BlockStore.IsShutdown(new InvalidOperationException("AniDB API error <error>No such anime</error>")));
        Assert.False(BlockStore.IsShutdown(new HttpRequestException("Connection refused")));
    }

    [Fact]
    public void TitlesRefreshIsAtLeastThreeDays()
    {
        Assert.Equal(3, new PluginConfiguration { TitlesRefreshDays = 1 }.TitlesRefreshDays);
        Assert.Equal(10, new PluginConfiguration { TitlesRefreshDays = 10 }.TitlesRefreshDays);
    }

    [Fact]
    public void FloorStaysOutOfTheConfigurationFile()
    {
        var xml = new StringWriter();
        new XmlSerializer(typeof(SourceSetting)).Serialize(xml, new SourceSetting { Id = SourceIds.AniList });
        Assert.DoesNotContain(nameof(SourceSetting.MinimumRequestIntervalMs), xml.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Nyanbo! (Fall)", "Nyanbo!")]
    [InlineData("Show [BD 1080p] (Season 2)", "Show")]
    [InlineData("Ranma ½ (1989)", "Ranma ½ (1989)")]
    [InlineData("Plain", "Plain")]
    public void StripsFolderNotesButKeepsYears(string name, string expected) => Assert.Equal(expected, Queues.StripNotes(name));
}
