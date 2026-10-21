using System;
using System.IO;
using System.Linq;
using AniMetaPlus.Api;
using AniMetaPlus.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AniMetaPlus.Tests;

[Collection("AniMetaDb")]
public sealed class ReviewDecisionsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "animetaplus-reviews-" + Guid.NewGuid().ToString("N"));
    private readonly AniMetaDb _db;

    public ReviewDecisionsTests()
    {
        _db = AniMetaDb.Open(_folder, NullLogger.Instance);
        _db.Reviews.Upsert(new ReviewRecord { Id = "a", CurrentSource = "AniDB", ProposedSource = "AniList", ProposedValue = "x" });
        _db.Reviews.Upsert(new ReviewRecord { Id = "b", CurrentSource = "AniDB", ProposedSource = "AniList", ProposedValue = "y" });
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_folder, true);
    }

    [Fact]
    public void DecidesSeveralSkippingUnknownAndRepeatedIds()
    {
        var decided = AniMetaApi.DecideReviews(_db, ["a", "b", "a", "nope"], ReviewStatus.Locked);

        Assert.Equal(2, decided);
        Assert.All(_db.Reviews.FindAll(), r => Assert.Equal(ReviewStatus.Locked, r.Status));
        Assert.True(_db.Provenance.FindById("a").Locked);
        Assert.Equal("AniDB", _db.Provenance.FindById("b").PinnedSource);
    }

    [Fact]
    public void AcceptedPinsTheProposedSource()
    {
        Assert.Equal(1, AniMetaApi.DecideReviews(_db, ["a"], ReviewStatus.Accepted));

        Assert.Equal("AniList", _db.Provenance.FindById("a").PinnedSource);
        Assert.Equal(ReviewStatus.Pending, _db.Reviews.FindById("b").Status);
    }

    [Fact]
    public void PendingIsNoDecision()
    {
        Assert.Equal(0, AniMetaApi.DecideReviews(_db, ["a", "b"], ReviewStatus.Pending));
        Assert.All(_db.Reviews.FindAll(), r => Assert.Equal(ReviewStatus.Pending, r.Status));
        Assert.Equal(0, AniMetaApi.DecideReviews(_db, null!, ReviewStatus.Kept));
    }
}
