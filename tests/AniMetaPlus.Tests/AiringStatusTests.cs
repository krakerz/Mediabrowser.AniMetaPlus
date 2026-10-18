using System;
using AniMetaPlus.Providers.AniDB.Metadata;
using MediaBrowser.Model.Entities;
using Xunit;

namespace AniMetaPlus.Tests;

public class AiringStatusTests
{
    private static readonly DateTime _now = new(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EndedOnceTheEndDateHasPassed()
        => Assert.Equal(SeriesStatus.Ended, AniDbSeriesProvider.AiringStatus(_now.AddYears(-1), _now.AddDays(-1), _now));

    [Fact]
    public void ContinuingWhileItHasStartedAndNotEnded()
    {
        Assert.Equal(SeriesStatus.Continuing, AniDbSeriesProvider.AiringStatus(_now.AddDays(-14), null, _now));
        Assert.Equal(SeriesStatus.Continuing, AniDbSeriesProvider.AiringStatus(_now.AddDays(-14), _now.AddDays(60), _now));
    }

    [Fact]
    public void NothingForAShowNotYetAiringOrWithoutDates()
    {
        Assert.Null(AniDbSeriesProvider.AiringStatus(_now.AddDays(30), null, _now));
        Assert.Null(AniDbSeriesProvider.AiringStatus(null, null, _now));
    }
}
