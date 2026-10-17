using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using AniMetaPlus.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AniMetaPlus.Tests;

[Collection("AniMetaDb")]
public sealed class PosterBackupsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "animetaplus-posters-" + Guid.NewGuid().ToString("N"));
    private readonly AniMetaDb _db;
    private readonly PosterBackups _backups;

    public PosterBackupsTests()
    {
        _db = AniMetaDb.Open(_folder, NullLogger.Instance);
        _backups = new PosterBackups(_db, Path.Combine(_folder, "image-backups"));
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_folder, true);
    }

    [Fact]
    public void HoldsTheCurrentPosterOnceAndKeepsTheOriginal()
    {
        var poster = Poster("poster.png", "original");

        var held = _backups.Hold(null, "42", poster);
        Assert.Equal("42.png", held.File);
        Assert.Equal("image/png", held.MimeType);
        _backups.Applied("42", "Frieren", string.Empty, "Series", "http://x/a.jpg", PosterBackups.Stamp(poster), held);

        // A second run finds AniDB's poster in place: the original stays.
        File.WriteAllText(poster, "anidb");
        var again = _backups.Hold(_backups.Find("42"), "42", poster);

        Assert.Equal(held, again);
        Assert.Equal("original", File.ReadAllText(Path.Combine(_folder, "image-backups", "42.png")));
    }

    [Fact]
    public void NoBackupWithoutALocalPosterNeedsNoDecision()
    {
        var held = _backups.Hold(null, "7", null);
        Assert.Equal(string.Empty, held.File);

        _backups.Applied("7", "Movie", string.Empty, "Movie", "u", "stamp", held);

        Assert.Equal(ImageBackupState.Acknowledged, _backups.Find("7")!.State);
        Assert.Empty(_backups.ListPending());
    }

    [Fact]
    public void SkipsOnlyWhileUrlAndPosterFileAreUnchanged()
    {
        var poster = Poster("p.jpg", "a");
        var stamp = PosterBackups.Stamp(poster);
        _backups.Applied("1", "A", string.Empty, "Series", "u1", stamp, (string.Empty, string.Empty));
        var record = _backups.Find("1");

        Assert.True(PosterBackups.IsApplied(record, "u1", stamp));
        Assert.False(PosterBackups.IsApplied(record, "u2", stamp));
        Assert.False(PosterBackups.IsApplied(null, "u1", stamp));
        Assert.False(PosterBackups.IsApplied(record, "u1", string.Empty));

        File.WriteAllText(poster, "changed by the owner");
        Assert.False(PosterBackups.IsApplied(record, "u1", PosterBackups.Stamp(poster)));
    }

    [Fact]
    public void ListsPendingNewestFirst()
    {
        var first = _backups.Hold(null, "1", Poster("a.jpg", "a"));
        _backups.Applied("1", "A", string.Empty, "Series", "u", "s", first);
        var older = _backups.Find("1")!;
        older.ReplacedUtc = DateTime.UtcNow.AddHours(-1);
        _db.ImageBackups.Update(older);

        var second = _backups.Hold(null, "2", Poster("b.jpg", "b"));
        _backups.Applied("2", "B", "Show", "Season", "u", "s", second);

        Assert.Equal(["2", "1"], _backups.ListPending().Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task RevertRestoresKeepsTheRecordAndDeletesTheFile()
    {
        var held = _backups.Hold(null, "5", Poster("c.jpg", "previous"));
        _backups.Applied("5", "C", string.Empty, "Series", "u", "s", held);
        var images = new FakeImages();

        var (done, failed) = await _backups.Revert(["5"], false, images, NullLogger.Instance, CancellationToken.None);

        Assert.Equal((1, 0), (done, failed));
        Assert.Equal("previous", images.Restored["5"]);
        var record = _backups.Find("5")!;
        Assert.Equal(ImageBackupState.Reverted, record.State);
        Assert.True(string.IsNullOrEmpty(record.BackupFile));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "image-backups")));
        Assert.Equal(1, _backups.CountReverted());

        Assert.Equal(1, _backups.ForgetReverted());
        Assert.Null(_backups.Find("5"));
    }

    [Fact]
    public async Task RevertForgetsAnItemTheServerNoLongerHas()
    {
        var held = _backups.Hold(null, "9", Poster("d.jpg", "x"));
        _backups.Applied("9", "D", string.Empty, "Series", "u", "s", held);

        var (done, failed) = await _backups.Revert(null, true, new FakeImages { Missing = true }, NullLogger.Instance, CancellationToken.None);

        Assert.Equal((1, 0), (done, failed));
        Assert.Null(_backups.Find("9"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "image-backups")));
    }

    [Fact]
    public async Task RevertCountsAFailureAndLeavesTheBackup()
    {
        var held = _backups.Hold(null, "3", Poster("e.jpg", "x"));
        _backups.Applied("3", "E", string.Empty, "Series", "u", "s", held);

        var (done, failed) = await _backups.Revert(["3"], false, new FakeImages { Throws = true }, NullLogger.Instance, CancellationToken.None);

        Assert.Equal((0, 1), (done, failed));
        Assert.Equal(ImageBackupState.Pending, _backups.Find("3")!.State);
        Assert.NotNull(_backups.ReadDataUrl("3"));
    }

    [Fact]
    public void AcknowledgeDeletesTheBackupsAndIgnoresDecidedItems()
    {
        var held = _backups.Hold(null, "11", Poster("f.png", "x"));
        _backups.Applied("11", "F", string.Empty, "Series", "u", "s", held);

        Assert.StartsWith("data:image/png;base64,", _backups.ReadDataUrl("11"));
        Assert.Equal(1, _backups.Acknowledge(null, true));
        Assert.Equal(0, _backups.Acknowledge(["11"], false));

        var record = _backups.Find("11")!;
        Assert.Equal(ImageBackupState.Acknowledged, record.State);
        Assert.True(string.IsNullOrEmpty(record.BackupFile));
        Assert.Null(_backups.ReadDataUrl("11"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_folder, "image-backups")));
    }

    private string Poster(string name, string content)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, content);

        return path;
    }

    private sealed class FakeImages : IHostImages
    {
        public Dictionary<string, string> Restored { get; } = [];

        public bool Missing { get; set; }

        public bool Throws { get; set; }

        public Task<bool> RestorePoster(string itemId, string file, string mimeType, CancellationToken cancellationToken)
        {
            if (Throws)
            {
                throw new IOException("disk");
            }

            if (Missing)
            {
                return Task.FromResult(false);
            }

            Restored[itemId] = File.ReadAllText(file);

            return Task.FromResult(true);
        }
    }
}
