using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Api;
using AniMetaPlus.Core;
using AniMetaPlus.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AniMetaPlus.Tests;

// One collection with the database tests: HostServices.Refresher is shared state, and LiteDB maps types through a shared mapper.
[Collection("AniMetaDb")]
public sealed class MappingTabTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "animetaplus-mapping-" + Guid.NewGuid().ToString("N"));

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

    private static Dictionary<string, string> Ids(params (string Provider, string Id)[] ids) => ids.ToDictionary(i => i.Provider, i => i.Id);

    private static ScanItem Show(string path, string name, Dictionary<string, string>? ids = null)
        => new(path, name, null, "Series", ids ?? new Dictionary<string, string>(), DateTime.UtcNow);

    [Fact]
    public void ExportsOnlyTheIdsTheOwnerDecidedAndRoundTripsThem()
    {
        DecisionsExport export;

        using (var db = AniMetaDb.Open(_folder, NullLogger.Instance))
        {
            db.IdRecords.Upsert(new IdRecord { Id = "/m/a", Status = IdRecord.Manual, NewIds = Ids(("AniDB", "1"), ("MyAnimeList", "10")) });
            db.IdRecords.Upsert(new IdRecord { Id = "/m/b", Status = IdRecord.Verified, NewIds = Ids(("AniDB", "2")) });
            db.IdRecords.Upsert(new IdRecord { Id = "/m/c", Status = IdRecord.Corrected });
            db.IdRecords.Upsert(new IdRecord { Id = "/m/d", Status = IdRecord.Suspect });

            export = db.Export();
        }

        Assert.Equal(["/m/a", "/m/b"], export.IdRecords.Select(r => r.Id).Order());

        var other = Path.Combine(_folder, "other");
        using var restored = AniMetaDb.Open(other, NullLogger.Instance);
        Assert.Equal(2, restored.Import(JsonSerializer.Deserialize<DecisionsExport>(JsonSerializer.Serialize(export))!));

        var manual = restored.IdRecords.FindById("/m/a");
        Assert.Equal(IdRecord.Manual, manual.Status);
        Assert.Equal("10", manual.NewIds["MyAnimeList"]);
        Assert.Equal(IdRecord.Verified, restored.IdRecords.FindById("/m/b").Status);
        Assert.Null(restored.IdRecords.FindById("/m/c"));
        Assert.Equal(2, JsonSerializer.Deserialize<DecisionsExport>(File.ReadAllText(Path.Combine(other, AniMetaDb.DecisionsFileName)))!.IdRecords.Count);
    }

    [Fact]
    public void ImportTakesAFileWithoutIdRecords()
    {
        using var db = AniMetaDb.Open(_folder, NullLogger.Instance);

        Assert.Equal(0, db.Import(JsonSerializer.Deserialize<DecisionsExport>("""{"Version":1,"IdRecords":null}""", AniMetaDb.ReadOptions)!));
    }

    [Fact]
    public async Task PutsImportedManualIdsOnTheShowsThatCarryOthers()
    {
        var host = new FakeRefresher(
            Show("/m/differs", "Differs", Ids(("AniDB", "5"), ("MyAnimeList", "50"))),
            Show("/m/same", "Same", Ids(("AniDB", "1"))),
            Show("/m/verified", "Verified", Ids(("AniDB", "9"))));

        var records = new[]
        {
            new IdRecord { Id = "/m/differs", Status = IdRecord.Manual, NewIds = Ids(("AniDB", "7"), ("AniList", "70")) },
            new IdRecord { Id = "/m/same", Status = IdRecord.Manual, NewIds = Ids(("AniDB", "1")) },
            new IdRecord { Id = "/m/verified", Status = IdRecord.Verified, NewIds = Ids(("AniDB", "1")) },
            new IdRecord { Id = "/m/gone", Status = IdRecord.Manual, NewIds = Ids(("AniDB", "3")) }
        };

        var previous = HostServices.Refresher;
        HostServices.Refresher = host;
        try
        {
            Assert.Equal(1, await AniMetaApi.ApplyImportedIds(records));
        }
        finally
        {
            HostServices.Refresher = previous;
        }

        var (path, written) = Assert.Single(host.Saved);
        Assert.Equal("/m/differs", path);
        Assert.Equal("7", written["AniDB"]);
        Assert.Equal("70", written["AniList"]);

        // What the record does not name is taken off the show.
        Assert.Equal(string.Empty, written["MyAnimeList"]);
        Assert.Equal(("/m/differs", true, true), Assert.Single(host.Refreshed));
    }

    [Fact]
    public void ListsHandMappingsWithTheirShowsAndAsRowsOfTheirOwn()
    {
        var shows = new[]
        {
            Show("/m/Frieren", "Frieren", Ids(("AniDB", "17617"))),
            Show("/m/Other", "Other")
        };

        var mappings = new[]
        {
            new MappingRecord { Id = ItemKeys.ForUnidentified("Series", "/m/Frieren", "Frieren"), AnimeId = "17617", ItemName = "Frieren" },
            new MappingRecord { Id = "movie:kimi no na wa", Kind = "Movie", AnimeId = "9437", ItemName = "Kimi no Na wa" },
            new MappingRecord { Id = "series:not listed", AnimeId = "anilist:99", ItemName = "Not listed" },
            new MappingRecord { Id = "series:via mal", AnimeId = "mal:5", ItemName = string.Empty }
        };

        var rows = AniMetaApi.JoinIdRows(shows, new Dictionary<string, IdRecord>(), mappings);

        var frieren = rows.Single(r => r.Path == "/m/Frieren");
        Assert.Equal("17617", frieren.HandMapping);
        Assert.Equal("series:frieren", frieren.MappingKey);
        Assert.Equal("17617", frieren.Ids["AniDB"]);

        var other = rows.Single(r => r.Path == "/m/Other");
        Assert.Equal(string.Empty, other.HandMapping);
        Assert.Equal(string.Empty, other.MappingKey);

        var orphans = rows.Where(r => r.Path.Length == 0).ToList();
        Assert.Equal(3, orphans.Count);

        var movie = orphans.Single(r => r.MappingKey == "movie:kimi no na wa");
        Assert.Equal("Kimi no Na wa", movie.Name);
        Assert.Equal("9437", movie.Ids["AniDB"]);
        Assert.Equal(string.Empty, movie.Status);

        Assert.Equal("99", orphans.Single(r => r.HandMapping == "anilist:99").Ids["AniList"]);

        var viaMal = orphans.Single(r => r.HandMapping == "mal:5");
        Assert.Equal("5", viaMal.Ids["MyAnimeList"]);
        Assert.Equal("series:via mal", viaMal.Name);
    }

    private sealed class FakeRefresher(params ScanItem[] shows) : IHostRefresher
    {
        public List<(string Path, Dictionary<string, string> Ids)> Saved { get; } = [];

        public List<(string Path, bool ReplaceAll, bool ReplaceImages)> Refreshed { get; } = [];

        public bool Refresh(string itemPath, bool replaceAll) => Refresh(itemPath, replaceAll, false);

        public bool Refresh(string itemPath, bool replaceAll, bool replaceImages)
        {
            Refreshed.Add((itemPath, replaceAll, replaceImages));

            return true;
        }

        public IReadOnlyList<string> CorrectIds(string itemPath, IReadOnlyDictionary<string, string> ids) => [];

        public Task<bool> SaveIds(string itemPath, IReadOnlyDictionary<string, string> ids, CancellationToken cancellationToken)
        {
            Saved.Add((itemPath, new Dictionary<string, string>(ids)));

            return Task.FromResult(true);
        }

        public IReadOnlyList<ScanItem> ListAnimeItems() => shows;
    }
}
