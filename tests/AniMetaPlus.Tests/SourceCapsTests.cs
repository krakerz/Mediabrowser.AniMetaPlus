using System;
using AniMetaPlus.Configuration;
using AniMetaPlus.Sources;
using Xunit;

namespace AniMetaPlus.Tests;

public class SourceCapsTests
{
    private static readonly DateTime _start = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    // A library refresh with no AniDB limits sent 256 requests and was banned.
    [Fact]
    public void AniDbWithoutLimitsGetsTheDefaults()
    {
        var limits = SourceDefaults.Limits(new SourceSetting { Id = SourceIds.AniDb });

        Assert.Equal(0, limits.PerHour);
        Assert.Equal(SourceDefaults.AniDbDefaultPerDay, limits.PerDay);
        Assert.Equal(SourceDefaults.AniDbDefaultBurstSize, limits.BurstSize);
        Assert.Equal(TimeSpan.FromMinutes(SourceDefaults.AniDbDefaultRestMinutes), limits.Rest);
    }

    [Fact]
    public void AniDbKeepsTheLimitsSet()
        => Assert.Equal(
            new RequestLimits(5, 40, 10, TimeSpan.FromMinutes(30)),
            SourceDefaults.Limits(new SourceSetting { Id = SourceIds.AniDb, MaxPerHour = 5, MaxPerDay = 40, BurstSize = 10, RestMinutes = 30 }));

    [Fact]
    public void OtherSourcesStayUnlimitedUnlessSet()
    {
        var limits = SourceDefaults.Limits(new SourceSetting { Id = SourceIds.AniList });

        Assert.Equal((0, 0, 0), (limits.PerHour, limits.PerDay, limits.BurstSize));
    }

    [Fact]
    public void RestsAfterABurstAndResumesAfterTheRest()
    {
        var limits = new RequestLimits(0, 0, 3, TimeSpan.FromMinutes(90));
        var pacer = new RequestPacer();

        for (var i = 0; i < 3; i++)
        {
            Assert.Null(pacer.Refusal(_start.AddSeconds(i * 5), limits));
            pacer.Record(_start.AddSeconds(i * 5), limits);
        }

        var refused = pacer.Refusal(_start.AddMinutes(10), limits);
        Assert.NotNull(refused);
        Assert.Equal(TimeSpan.FromMinutes(90) + TimeSpan.FromSeconds(10) - TimeSpan.FromMinutes(10), refused.Value.Wait);

        var after = _start.AddSeconds(10).AddMinutes(90);
        Assert.Null(pacer.Refusal(after, limits));
        pacer.Record(after, limits);
        Assert.Equal(1, pacer.BurstSent);
    }

    [Fact]
    public void TheDailyCapBindsBeforeTheBursts()
    {
        var limits = new RequestLimits(0, 4, 3, TimeSpan.FromMinutes(1));
        var pacer = new RequestPacer();
        var at = _start;

        for (var i = 0; i < 4; i++)
        {
            Assert.Null(pacer.Refusal(at, limits));
            pacer.Record(at, limits);
            at = at.AddMinutes(2);
        }

        var refused = pacer.Refusal(at, limits);
        Assert.NotNull(refused);
        Assert.Contains("daily cap", refused.Value.Reason, StringComparison.Ordinal);
        Assert.Null(pacer.Refusal(_start.AddDays(1).AddSeconds(1), limits));
    }

    [Fact]
    public void ARestSurvivesARestart()
    {
        var limits = new RequestLimits(0, 0, 2, TimeSpan.FromMinutes(90));
        var before = new RequestPacer();
        before.Record(_start, limits);
        before.Record(_start.AddSeconds(5), limits);

        var after = new RequestPacer();
        after.Load(before.Save(), _start.AddMinutes(1));

        Assert.NotNull(after.Refusal(_start.AddMinutes(30), limits));
        Assert.Equal((2, 2), after.Totals(_start.AddMinutes(30)));
    }

    [Fact]
    public void ReadsTheTimesEarlierVersionsSaved()
    {
        var pacer = new RequestPacer();
        pacer.Load(_start.Ticks + "," + _start.AddMinutes(1).Ticks, _start.AddMinutes(2));

        Assert.Equal((2, 2), pacer.Totals(_start.AddMinutes(2)));
    }

    [Fact]
    public void AQuietSpellAsLongAsARestStartsAFreshBurst()
    {
        var limits = new RequestLimits(0, 0, 3, TimeSpan.FromMinutes(90));
        var pacer = new RequestPacer();
        pacer.Record(_start, limits);
        pacer.Record(_start.AddMinutes(1), limits);

        pacer.Record(_start.AddMinutes(200), limits);

        Assert.Equal(1, pacer.BurstSent);
        Assert.Null(pacer.RestUntilUtc);
    }
}
