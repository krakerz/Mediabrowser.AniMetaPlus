using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using AniMetaPlus.Data;
using AniMetaPlus.Providers.AniDB.Mapping;
using AniMetaPlus.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AniMetaPlus.Tests;

/// <summary>
/// Tests for the TVDB ids the MyAnimeList and AniList entries are filed under, and for settling a show whose AniDB-keyed id disagrees.
/// </summary>
public class TvdbConflictTests
{
    // The shape upstream has for Dark Machine: AniDB 18777 is filed under TVDB 452665, which
    // TheTVDB has deleted, while its MyAnimeList and AniList entries are filed under 480909.
    private const string Mappings = """
        {
          "$meta": { "schema_version": "3.0.0" },
          "anidb:18777:R": {
            "tvdb_show:452665:s1": { "1-12": "1-12" },
            "mal:59415": { "1-12": "1-12" },
            "anilist:179876": { "1-12": "1-12" }
          },
          "mal:59415": { "tvdb_show:480909:s1": { "1-12": "1-12" }, "anidb:18777:R": { "1-12": "1-12" } },
          "anilist:179876": { "tvdb_show:480909:s1": { "1-12": "1-12" } },
          "mal:5": { "tvdb_show:76885:s0": { "1-2": "1-2" } },
          "mal:6": { "tvdb_show:300:s2": { "1-12": "1-12" }, "tvdb_show:200:s1": { "1-12": "1-12" } },
          "mal:7": [1, 2],
          "anilist:8": null,
          "mal:abc": { "tvdb_show:8000:s1": { "1-12": "1-12" } },
          "mal:9": { "tvdb_show:9000:s1": { "1-12": "1-12" } },
          "anidb:100:R": {
            "tvdb_show:777:s1": { "1-12": "1-12" },
            "mal:200": { "1-12": "1-12" }
          },
          "mal:200": { "tvdb_show:777:s1": { "1-12": "1-12" } },
          "anidb:300:R": { "tvdb_show:900:s1": { "1-12": "1-12" } },
          "anilist:400": { "tvdb_show:901:s1": { "1-12": "1-12" } },
          "anidb:500:R": {
            "tvdb_show:910:s1": { "1-12": "1-12" },
            "mal:600": { "1-12": "1-12" }
          },
          "mal:600": { "tvdb_show:911:s1": { "1-12": "1-12" } },
          "anidb:700:R": {
            "tvdb_show:950:s1": { "1-12": "1-12" },
            "mal:701": { "1-12": "1-12" },
            "anilist:702": { "1-12": "1-12" }
          },
          "mal:701": { "tvdb_show:950:s1": { "1-12": "1-12" } },
          "anilist:702": { "tvdb_show:951:s1": { "1-12": "1-12" } }
        }
        """;

    private static readonly TvdbCandidates DarkMachine = new("452665", false, [new LinkedTvdb("480909", "mal:59415")]);

    private static readonly TvdbCandidates TwoOthers = new("452665", false, [new LinkedTvdb("480909", "mal:59415"), new LinkedTvdb("555", "anilist:1")]);

    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The AniDB side of the file is read as before.
    /// </summary>
    [Fact]
    public void TheAniDbKeyedShowIsUnchanged()
    {
        var index = ParseBytes(Encoding.UTF8.GetBytes(Mappings));

        Assert.Equal("452665", index.SeriesKeyOf("18777"));
        Assert.Equal("18777", index.AnimeIdByLink("mal", "59415"));
        Assert.Null(index.FirstSeasonByTvdb("480909"));
    }

    /// <summary>
    /// A MyAnimeList or AniList entry is filed under the show its own key names.
    /// </summary>
    [Fact]
    public void ALinkKeyNamesItsOwnShow()
    {
        var index = ParseBytes(Encoding.UTF8.GetBytes(Mappings));

        Assert.Equal("480909", index.TvdbShowByLink("mal", "59415"));
        Assert.Equal("480909", index.TvdbShowByLink("anilist", "179876"));
        Assert.Null(index.TvdbShowByLink("mal", "1"));
        Assert.Equal(["anilist:179876", "mal:59415"], index.FirstSeasonLinksByTvdb("480909"));
        Assert.Empty(index.FirstSeasonLinksByTvdb("1"));
    }

    /// <summary>
    /// A show only the specials are filed under says nothing about where an entry begins, and an earlier season beats a later one.
    /// </summary>
    [Fact]
    public void OnlyOrdinarySeasonsCountAndTheEarliestWins()
    {
        var index = ParseBytes(Encoding.UTF8.GetBytes(Mappings));

        Assert.Null(index.TvdbShowByLink("mal", "5"));
        Assert.Equal("200", index.TvdbShowByLink("mal", "6"));
        Assert.Equal(["mal:6"], index.FirstSeasonLinksByTvdb("300"));
    }

    /// <summary>
    /// A value of another shape, or an id that is not digits, does not stop the read.
    /// </summary>
    [Fact]
    public void LinkKeysOfOtherShapesAreSteppedOver()
    {
        var index = ParseBytes(Encoding.UTF8.GetBytes(Mappings));

        Assert.Null(index.TvdbShowByLink("mal", "7"));
        Assert.Null(index.TvdbShowByLink("anilist", "8"));
        Assert.Null(index.TvdbShowByLink("mal", "abc"));
        Assert.Equal("9000", index.TvdbShowByLink("mal", "9"));
        Assert.Equal("777", index.SeriesKeyOf("100"));
    }

    /// <summary>
    /// Where the AniDB entry's own links name another show, that one is the alternative.
    /// </summary>
    [Fact]
    public void ADisagreementNamesTheOtherShape()
    {
        var index = ParseBytes(Encoding.UTF8.GetBytes(Mappings));

        var candidates = AniDbMappings.CandidatesFrom(index, "18777", "452665", false, new Dictionary<string, string>());

        Assert.Equal("452665", candidates.Primary);
        Assert.True(candidates.Disagree);
        Assert.Equal([new LinkedTvdb("480909", "mal:59415")], candidates.Alternatives);
    }

    /// <summary>
    /// A link filed under the same show as the AniDB entry means they agree.
    /// </summary>
    [Fact]
    public void ALinkThatAgreesLeavesNoAlternative()
    {
        var index = ParseBytes(Encoding.UTF8.GetBytes(Mappings));

        var candidates = AniDbMappings.CandidatesFrom(index, "100", "777", false, new Dictionary<string, string>());

        Assert.False(candidates.Disagree);
        Assert.Empty(candidates.Alternatives);
    }

    /// <summary>
    /// One link agreeing does not outweigh another naming a different show (Dark Machine's mappings
    /// of 2026-10-20: MyAnimeList on the dropped show, AniList on the new one).
    /// </summary>
    [Fact]
    public void OneAgreeingLinkDoesNotHideAnother()
    {
        var index = ParseBytes(Encoding.UTF8.GetBytes(Mappings));

        var candidates = AniDbMappings.CandidatesFrom(index, "700", "950", false, new Dictionary<string, string>());

        Assert.True(candidates.Disagree);
        Assert.Equal([new LinkedTvdb("951", "anilist:702")], candidates.Alternatives);
    }

    /// <summary>
    /// An id the item carries is read too, unless the mappings link it to another AniDB entry.
    /// </summary>
    [Fact]
    public void ItemIdsAreReadUnlessTheyBelongToAnotherEntry()
    {
        var index = ParseBytes(Encoding.UTF8.GetBytes(Mappings));

        var byAniList = AniDbMappings.CandidatesFrom(index, "300", "900", false, new Dictionary<string, string> { ["AniList"] = "400" });
        Assert.Equal([new LinkedTvdb("901", "anilist:400")], byAniList.Alternatives);

        var byMal = AniDbMappings.CandidatesFrom(index, "300", "900", false, new Dictionary<string, string> { ["MyAnimeList"] = "600" });
        Assert.False(byMal.Disagree);
    }

    /// <summary>
    /// An id from the overrides is final.
    /// </summary>
    [Fact]
    public void AnOverriddenIdIsNotInDispute()
    {
        var index = ParseBytes(Encoding.UTF8.GetBytes(Mappings));

        var candidates = AniDbMappings.CandidatesFrom(index, "18777", "452665", true, new Dictionary<string, string>());

        Assert.True(candidates.Overridden);
        Assert.False(candidates.Disagree);
    }

    /// <summary>
    /// No mappings, or no id from them, is no dispute.
    /// </summary>
    [Fact]
    public void NothingToCompareIsNoDispute()
    {
        Assert.False(AniDbMappings.CandidatesFrom(null, "18777", "452665", false, new Dictionary<string, string>()).Disagree);
        Assert.False(AniDbMappings.CandidatesFrom(ParseBytes(Encoding.UTF8.GetBytes(Mappings)), "18777", null, false, new Dictionary<string, string>()).Disagree);
    }

    /// <summary>
    /// The id TheTVDB has is kept, and the others are not asked about.
    /// </summary>
    [Fact]
    public void AnExistingAniDbIdIsKept()
    {
        var choice = TvdbPick.Decide(DarkMachine, TvdbExistence.Exists, []);

        Assert.Equal(TvdbOutcome.Kept, choice.Outcome);
        Assert.Equal("452665", choice.Chosen);
        Assert.Equal("TheTVDB has 452665, the AniDB mapping, which is kept; MyAnimeList 59415 says 480909", choice.Reason);
    }

    /// <summary>
    /// An answer that could not be had keeps the AniDB id and says so.
    /// </summary>
    [Fact]
    public void AnUnknownAnswerKeepsTheAniDbId()
    {
        var choice = TvdbPick.Decide(DarkMachine, TvdbExistence.Unknown, []);

        Assert.Equal(TvdbOutcome.Unknown, choice.Outcome);
        Assert.Equal("452665", choice.Chosen);
        Assert.Equal("TheTVDB could not be asked; the AniDB mapping 452665 is kept, MyAnimeList 59415 says 480909", choice.Reason);

        Assert.Equal(TvdbOutcome.Unknown, TvdbPick.Decide(DarkMachine, TvdbExistence.Missing, [TvdbExistence.Unknown]).Outcome);
    }

    /// <summary>
    /// A missing AniDB id and exactly one other that exists switches to it.
    /// </summary>
    [Fact]
    public void AMissingAniDbIdSwitchesToTheOneThatExists()
    {
        var choice = TvdbPick.Decide(DarkMachine, TvdbExistence.Missing, [TvdbExistence.Exists]);

        Assert.Equal(TvdbOutcome.Switched, choice.Outcome);
        Assert.Equal("480909", choice.Chosen);
        Assert.Equal("TheTVDB no longer has 452665, the AniDB mapping; 480909 from MyAnimeList 59415 is used", choice.Reason);

        var second = TvdbPick.Decide(TwoOthers, TvdbExistence.Missing, [TvdbExistence.Missing, TvdbExistence.Exists]);

        Assert.Equal(TvdbOutcome.Switched, second.Outcome);
        Assert.Equal("555", second.Chosen);
        Assert.Equal("anilist:1", second.Alternative?.Link);
    }

    /// <summary>
    /// With none of them there, or several, the AniDB id stays.
    /// </summary>
    [Fact]
    public void WithNoSingleAnswerTheAniDbIdStays()
    {
        var neither = TvdbPick.Decide(DarkMachine, TvdbExistence.Missing, [TvdbExistence.Missing]);

        Assert.Equal(TvdbOutcome.Kept, neither.Outcome);
        Assert.Equal("452665", neither.Chosen);
        Assert.Equal("TheTVDB has neither 452665 nor 480909; the AniDB mapping is kept", neither.Reason);

        var several = TvdbPick.Decide(TwoOthers, TvdbExistence.Missing, [TvdbExistence.Exists, TvdbExistence.Exists]);

        Assert.Equal(TvdbOutcome.Kept, several.Outcome);
        Assert.Equal("452665", several.Chosen);
        Assert.Equal("TheTVDB has several of the other ids; the AniDB mapping 452665 is kept", several.Reason);
    }

    /// <summary>
    /// A recorded choice is used again only while it is about the same ids, was answered, and is recent.
    /// </summary>
    [Fact]
    public void ARecordedChoiceIsReusedOnlyWhileItStands()
    {
        var record = RecordOf(TvdbOutcome.Switched, Now.AddDays(-3));

        Assert.True(TvdbPick.Reusable(record, DarkMachine, Now));

        record.Status = IdRecord.Suspect;
        Assert.False(TvdbPick.Reusable(record, DarkMachine, Now));

        Assert.False(TvdbPick.Reusable(null, DarkMachine, Now));
        Assert.False(TvdbPick.Reusable(RecordOf(TvdbOutcome.Switched, Now.AddDays(-3)), new TvdbCandidates("1", false, DarkMachine.Alternatives), Now));
        Assert.False(TvdbPick.Reusable(RecordOf(TvdbOutcome.Switched, Now.AddDays(-3)), new TvdbCandidates("452665", false, [new LinkedTvdb("2", "mal:2")]), Now));
        Assert.False(TvdbPick.Reusable(RecordOf(TvdbOutcome.Unknown, Now.AddDays(-3)), DarkMachine, Now));
        Assert.False(TvdbPick.Reusable(RecordOf(TvdbOutcome.Kept, Now.AddDays(-TvdbPick.ReuseDays - 1)), DarkMachine, Now));
        Assert.True(TvdbPick.Reusable(RecordOf(TvdbOutcome.Kept, Now.AddDays(-TvdbPick.ReuseDays + 1)), DarkMachine, Now));
    }

    /// <summary>
    /// A reused record gives back the choice it was made from.
    /// </summary>
    [Fact]
    public void ARecordGivesBackItsChoice()
    {
        var switched = TvdbPick.FromRecord(RecordOf(TvdbOutcome.Switched, Now), DarkMachine);

        Assert.Equal("480909", switched.Chosen);
        Assert.Equal(TvdbOutcome.Switched, switched.Outcome);
        Assert.Equal("mal:59415", switched.Alternative?.Link);

        var kept = TvdbPick.FromRecord(RecordOf(TvdbOutcome.Kept, Now), DarkMachine);

        Assert.Equal("452665", kept.Chosen);
    }

    /// <summary>
    /// The id replaces an empty one or the AniDB one, and nothing else.
    /// </summary>
    [Fact]
    public void ReplacementTouchesOnlyAnEmptyOrAniDbId()
    {
        var switched = TvdbPick.Decide(DarkMachine, TvdbExistence.Missing, [TvdbExistence.Exists]);

        Assert.Equal("480909", TvdbPick.Replacement(null, switched));
        Assert.Equal("480909", TvdbPick.Replacement(string.Empty, switched));
        Assert.Equal("480909", TvdbPick.Replacement("452665", switched));
        Assert.Null(TvdbPick.Replacement("480909", switched));
        Assert.Null(TvdbPick.Replacement("12345", switched));

        var kept = TvdbPick.Decide(DarkMachine, TvdbExistence.Exists, []);

        Assert.Equal("452665", TvdbPick.Replacement(null, kept));
        Assert.Null(TvdbPick.Replacement("452665", kept));
    }

    /// <summary>
    /// The record holds the ids in dispute and never an empty value.
    /// </summary>
    [Fact]
    public void ARecordHoldsTheDisputeAndNoEmptyValues()
    {
        var before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AniDB"] = "18777",
            ["MyAnimeList"] = "59415",
            ["AniList"] = "179876",
            ["Tvdb"] = "452665",
            ["Tmdb"] = string.Empty,
            ["Imdb"] = "tt1"
        };
        var created = Now.AddDays(-9);
        var existing = new IdRecord { Id = "/media/Dark Machine", CreatedUtc = created };
        var choice = TvdbPick.Decide(DarkMachine, TvdbExistence.Missing, [TvdbExistence.Exists]);

        var record = TvdbPick.ToRecord("/media/Dark Machine", "Dark Machine (Fall)", before, choice, existing, Now);

        Assert.Equal(IdRecord.TvdbConflict, record.Status);
        Assert.Equal("/media/Dark Machine", record.Id);
        Assert.Equal("Dark Machine (Fall)", record.ItemName);
        Assert.Equal(created, record.CreatedUtc);
        Assert.Equal(Now, record.UpdatedUtc);
        Assert.Equal(choice.Reason, record.Reason);
        Assert.Equal("452665", record.OldIds["Tvdb"]);
        Assert.False(record.OldIds.ContainsKey("Tmdb"));
        Assert.False(record.OldIds.ContainsKey("Imdb"));
        Assert.Equal("480909", record.NewIds["Tvdb"]);
        Assert.Equal("452665", record.NewIds["TvdbByAniDb"]);
        Assert.Equal("480909", record.NewIds["TvdbByLink"]);
        Assert.Equal("mal:59415", record.NewIds["TvdbLink"]);
        Assert.Equal("Switched", record.NewIds["TvdbOutcome"]);
        Assert.Equal("18777", record.NewIds["AniDB"]);
        Assert.DoesNotContain(record.NewIds.Values, value => value.Length == 0);
        Assert.DoesNotContain(record.OldIds.Values, value => value.Length == 0);

        Assert.Equal(Now, TvdbPick.ToRecord("p", "n", before, choice, null, Now).CreatedUtc);
    }

    /// <summary>
    /// A 404 anywhere in what the request gate wrapped says the series is gone.
    /// </summary>
    [Fact]
    public void ANotFoundInsideTheGateIsRecognised()
    {
        Assert.True(TvdbPick.IsNotFound(new SourceBlockedException("blocked", new HttpRequestException("gone", null, HttpStatusCode.NotFound))));
        Assert.True(TvdbPick.IsNotFound(new SourceBlockedException("blocked") { Block = new BlockRecord { Reason = BlockReason.NotFound } }));
        Assert.True(TvdbPick.IsNotFound(new SourceBlockedException("blocked") { Block = new BlockRecord { Reason = BlockReason.Other, HttpStatus = 404 } }));
        Assert.True(TvdbPick.IsNotFound(new SourceHttpException("gone", HttpStatusCode.NotFound, null)));
        Assert.True(TvdbPick.IsNotFound(new SourceBlockedException("blocked", new StatusException(HttpStatusCode.NotFound))));
        Assert.True(TvdbPick.IsNotFound(new StatusException(HttpStatusCode.NotFound)));
    }

    /// <summary>
    /// Anything else is not an answer about the series.
    /// </summary>
    [Fact]
    public void OtherFailuresAreNotNotFound()
    {
        Assert.False(TvdbPick.IsNotFound(new TimeoutException()));
        Assert.False(TvdbPick.IsNotFound(new SourceBlockedException("blocked", new TimeoutException())));
        Assert.False(TvdbPick.IsNotFound(new SourceBlockedException("blocked") { Block = new BlockRecord { Reason = BlockReason.RateLimited, HttpStatus = 429 } }));
        Assert.False(TvdbPick.IsNotFound(new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable)));
        Assert.False(TvdbPick.IsNotFound(new StatusException(HttpStatusCode.InternalServerError)));
        Assert.False(TvdbPick.IsNotFound(new SourceBlockedException("blocked")));
    }

    private static IdRecord RecordOf(TvdbOutcome outcome, DateTime updatedUtc)
        => new()
        {
            Id = "/media/Dark Machine",
            Status = IdRecord.TvdbConflict,
            NewIds = new Dictionary<string, string>
            {
                ["TvdbByAniDb"] = "452665",
                ["TvdbByLink"] = "480909",
                ["TvdbLink"] = "mal:59415",
                ["TvdbOutcome"] = outcome.ToString()
            },
            Reason = "recorded",
            CreatedUtc = updatedUtc,
            UpdatedUtc = updatedUtc
        };

    private static AniBridgeIndex ParseBytes(byte[] bytes)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, bytes);

            return AniBridgeIndex.Parse(path, NullLogger.Instance, DateTime.UtcNow, "test");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Stands in for a server's own HTTP exception, which carries its status by name only.
    private sealed class StatusException(HttpStatusCode statusCode) : Exception("status")
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
    }
}
