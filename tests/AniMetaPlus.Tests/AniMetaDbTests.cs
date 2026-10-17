using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AniMetaPlus.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AniMetaPlus.Tests;

// One collection: LiteDB maps a type for the first time through a shared mapper, which two databases opening at once race on.
[Collection("AniMetaDb")]
public sealed class AniMetaDbTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "animetaplus-db-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // A stand-in pinned to its source failed every refresh once its empty DeclinedValue came back null.
    [Fact]
    public void EmptyStringsComeBackEmptyNotNull()
    {
        using (var db = AniMetaDb.Open(_folder, NullLogger.Instance))
        {
            db.Provenance.Upsert(new ProvenanceRecord { Id = "anidb:1|Overview", ItemKey = "anidb:1", Field = "Overview", Source = "AniList", PinnedSource = "AniList", Provisional = true });
        }

        using (var db = AniMetaDb.Open(_folder, NullLogger.Instance))
        {
            var record = db.Provenance.FindById("anidb:1|Overview");

            Assert.Equal(string.Empty, record.DeclinedValue);
            Assert.Equal(string.Empty, record.PreferredSource);
            Assert.Equal("AniList", record.PinnedSource);

            Assert.NotNull(db.Export());
        }

        // Rows earlier versions stored as null are set to null when read; the setter makes them empty.
        Assert.Equal(string.Empty, new ProvenanceRecord { DeclinedValue = null! }.DeclinedValue);
    }

    [Fact]
    public void KeepsUtcTimesAndRoundTripsBlocks()
    {
        var at = new DateTime(2026, 10, 14, 1, 2, 3, DateTimeKind.Utc);

        using (var db = AniMetaDb.Open(_folder, NullLogger.Instance))
        {
            db.Blocks.Upsert(new BlockRecord
            {
                Id = BlockRecord.MakeId("AniDB", BlockScope.Source, string.Empty, string.Empty),
                Source = "AniDB",
                Reason = BlockReason.Banned,
                FirstSeenUtc = at,
                ExpiresUtc = at.AddHours(1)
            });
        }

        using (var db = AniMetaDb.Open(_folder, NullLogger.Instance))
        {
            var block = Assert.Single(db.Blocks.FindAll());
            Assert.Equal(at, block.FirstSeenUtc);
            Assert.Equal(DateTimeKind.Utc, block.FirstSeenUtc.Kind);
            Assert.True(block.IsActive(at));
            Assert.False(block.IsActive(at.AddHours(2)));
        }
    }

    [Fact]
    public void ExportHoldsOnlyDecisionsAndImportRestoresThem()
    {
        DecisionsExport export;

        using (var db = AniMetaDb.Open(_folder, NullLogger.Instance))
        {
            db.Mappings.Upsert(new MappingRecord { Id = "series:frieren", AnimeId = "17617" });
            db.Blocks.Upsert(new BlockRecord { Id = "auto", Source = "AniList", Reason = BlockReason.RateLimited });
            db.Blocks.Upsert(new BlockRecord { Id = "pinned", Source = "AniDB", Pinned = true, SetBy = "user", Reason = BlockReason.Manual });
            db.Reviews.Upsert(new ReviewRecord { Id = "r1", Status = ReviewStatus.Pending });
            db.Reviews.Upsert(new ReviewRecord { Id = "r2", Status = ReviewStatus.Locked });
            db.Unmatched.Upsert(new UnmatchedRecord { Id = "u1", Ignored = true });
            db.Unmatched.Upsert(new UnmatchedRecord { Id = "u2" });

            export = db.Export();
        }

        Assert.Single(export.Mappings);
        Assert.Equal("pinned", Assert.Single(export.Blocks).Id);
        Assert.Equal("r2", Assert.Single(export.Reviews).Id);
        Assert.Equal("u1", Assert.Single(export.Ignored).Id);

        var other = Path.Combine(_folder, "other");
        using var restored = AniMetaDb.Open(other, NullLogger.Instance);
        Assert.Equal(4, restored.Import(JsonSerializer.Deserialize<DecisionsExport>(JsonSerializer.Serialize(export))!));
        Assert.Equal("17617", restored.Mappings.FindById("series:frieren").AnimeId);
        Assert.True(File.Exists(Path.Combine(other, AniMetaDb.DecisionsFileName)));
    }

    [Fact]
    public void ImportTakesEnumNamesNullListsAndProvenanceDecisions()
    {
        const string json = """
            {"Version":1,"Mappings":null,"Blocks":null,"Ignored":null,
             "Reviews":[{"Id":"r1","Status":"Accepted"},{"Id":"r2","Status":2}],
             "Provenance":[{"Id":"k|Overview","ItemKey":"k","Field":"Overview","PinnedSource":"AniDB","Locked":true,"DeclinedValue":"x"}]}
            """;

        using var db = AniMetaDb.Open(_folder, NullLogger.Instance);
        var decisions = JsonSerializer.Deserialize<DecisionsExport>(json, AniMetaDb.ReadOptions)!;

        Assert.Equal(3, db.Import(decisions));
        Assert.Equal(ReviewStatus.Accepted, db.Reviews.FindById("r1").Status);
        Assert.Equal((ReviewStatus)2, db.Reviews.FindById("r2").Status);

        var provenance = db.Provenance.FindById("k|Overview");
        Assert.True(provenance.Locked);
        Assert.Equal("AniDB", provenance.PinnedSource);
        Assert.Equal("k|Overview", Assert.Single(db.Export().Provenance).Id);

        // An older export has no Provenance list at all.
        Assert.Equal(0, db.Import(JsonSerializer.Deserialize<DecisionsExport>("{}", AniMetaDb.ReadOptions)!));
    }

    [Fact]
    public void SetsADamagedFileAsideAndRestoresDecisions()
    {
        using (var db = AniMetaDb.Open(_folder, NullLogger.Instance))
        {
            db.Mappings.Upsert(new MappingRecord { Id = "series:bebop", AnimeId = "23" });
            db.DecisionChanged();
        }

        File.WriteAllBytes(Path.Combine(_folder, AniMetaDb.FileName), Enumerable.Repeat((byte)0x5A, 32768).ToArray());

        using (var db = AniMetaDb.Open(_folder, NullLogger.Instance))
        {
            Assert.Equal("23", db.Mappings.FindById("series:bebop").AnimeId);
        }

        Assert.Contains(Directory.GetFiles(_folder), f => f.Contains(".corrupt-", StringComparison.Ordinal));
    }
}
