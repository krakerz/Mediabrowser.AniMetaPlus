using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Api;
using AniMetaPlus.Data;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Core;

/// <summary>
/// The previous posters the AniDB poster task keeps, one per item, until the owner reverts or
/// acknowledges the replacement. Shared by the task and the settings page's endpoints.
/// </summary>
public sealed class PosterBackups
{
    private readonly AniMetaDb _db;
    private readonly string _folder;

    /// <summary>
    /// Initializes a new instance of the <see cref="PosterBackups"/> class.
    /// </summary>
    /// <param name="db">The database.</param>
    /// <param name="folder">The folder the backup files are kept in.</param>
    public PosterBackups(AniMetaDb db, string folder)
    {
        _db = db;
        _folder = folder;
    }

    /// <summary>
    /// Gets the backups of the running plugin.
    /// </summary>
    public static PosterBackups Current => new(AniMetaDb.Current, FolderOf(Plugin.Instance.AppPaths));

    /// <summary>
    /// Gets the folder for the backup files: the data folder rather than the cache folder,
    /// which can be reset and would take the only copy of a poster with it.
    /// </summary>
    /// <param name="paths">The server paths.</param>
    /// <returns>The folder.</returns>
    public static string FolderOf(IApplicationPaths paths) => Path.Combine(PluginPaths.DataFolder(paths), "image-backups");

    /// <summary>
    /// Describes a poster file, so that a later change to it can be told from the file the task
    /// wrote.
    /// </summary>
    /// <param name="path">The poster file, if the item has a local one.</param>
    /// <returns>The path, length and last write time, or empty when there is no such file.</returns>
    public static string Stamp(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var file = new FileInfo(path);

        return file.Exists
            ? string.Join('|', path, file.Length.ToString(CultureInfo.InvariantCulture), file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture))
            : string.Empty;
    }

    /// <summary>
    /// Finds the record of an item.
    /// </summary>
    /// <param name="id">The server's item id.</param>
    /// <returns>The record, or <c>null</c>.</returns>
    public ImageBackupRecord? Find(string id) => _db.ImageBackups.FindById(id);

    /// <summary>
    /// Whether the item already carries the poster at this address, untouched since the task
    /// replaced it.
    /// </summary>
    /// <param name="record">The item's record, if any.</param>
    /// <param name="url">The AniDB picture address.</param>
    /// <param name="currentStamp">The stamp of the item's poster now.</param>
    /// <returns><c>true</c> when downloading again would change nothing.</returns>
    public static bool IsApplied(ImageBackupRecord? record, string url, string currentStamp)
        => record != null && currentStamp.Length > 0 && record.AppliedUrl == url && record.AppliedStamp == currentStamp;

    /// <summary>
    /// Keeps the poster an item has now, unless an earlier run already holds the original.
    /// Run before the replacement: the server deletes the previous file when it saves.
    /// </summary>
    /// <param name="record">The item's record, if any.</param>
    /// <param name="id">The server's item id.</param>
    /// <param name="posterPath">The item's poster file now, if it has a local one.</param>
    /// <returns>The backup's file name and media type, or empty ones when there is nothing to keep.</returns>
    public (string File, string MimeType) Hold(ImageBackupRecord? record, string id, string? posterPath)
    {
        if (record is { BackupFile.Length: > 0 } && File.Exists(PathOf(record)))
        {
            return (record.BackupFile, record.BackupMimeType);
        }

        if (string.IsNullOrEmpty(posterPath) || !File.Exists(posterPath))
        {
            return (string.Empty, string.Empty);
        }

        Directory.CreateDirectory(_folder);
        DeleteFiles(id);

        var name = id + Path.GetExtension(posterPath).ToLowerInvariant();
        File.Copy(posterPath, Path.Combine(_folder, name), true);

        return (name, MimeTypeOf(posterPath));
    }

    /// <summary>
    /// Records a replacement that was made.
    /// </summary>
    /// <param name="id">The server's item id.</param>
    /// <param name="itemName">The item's name.</param>
    /// <param name="seriesName">The series name of a season, otherwise empty.</param>
    /// <param name="kind">Series, Season or Movie.</param>
    /// <param name="url">The AniDB picture address applied.</param>
    /// <param name="stamp">The stamp of the poster file after the replacement.</param>
    /// <param name="held">The backup held, from <see cref="Hold"/>.</param>
    public void Applied(string id, string itemName, string seriesName, string kind, string url, string stamp, (string File, string MimeType) held)
        => _db.ImageBackups.Upsert(new ImageBackupRecord
        {
            Id = id,
            ItemName = itemName,
            SeriesName = seriesName,
            Kind = kind,
            BackupFile = held.File,
            BackupMimeType = held.MimeType,
            AppliedUrl = url,
            AppliedStamp = stamp,

            // With nothing to go back to there is nothing to decide.
            State = held.File.Length > 0 ? ImageBackupState.Pending : ImageBackupState.Acknowledged,
            ReplacedUtc = DateTime.UtcNow
        });

    /// <summary>
    /// Lists the replacements waiting for a decision.
    /// </summary>
    /// <returns>The records, newest first.</returns>
    public IReadOnlyList<ImageBackupRecord> ListPending()
        => _db.ImageBackups.FindAll().Where(r => r.State == ImageBackupState.Pending).OrderByDescending(r => r.ReplacedUtc).ToList();

    /// <summary>
    /// Counts the items the owner reverted.
    /// </summary>
    /// <returns>The count.</returns>
    public int CountReverted() => _db.ImageBackups.FindAll().Count(r => r.State == ImageBackupState.Reverted);

    /// <summary>
    /// Reads a backup as a data address, which the settings page can show without a login header.
    /// </summary>
    /// <param name="id">The server's item id.</param>
    /// <returns>The data address, or <c>null</c> when no backup is held.</returns>
    public string? ReadDataUrl(string id)
    {
        var record = Find(id);
        if (record is not { BackupFile.Length: > 0 } || !File.Exists(PathOf(record)))
        {
            return null;
        }

        return "data:" + record.BackupMimeType + ";base64," + Convert.ToBase64String(File.ReadAllBytes(PathOf(record)));
    }

    /// <summary>
    /// Puts the previous poster back on each item and keeps the record so that the task leaves
    /// the item alone. An item the server no longer has is forgotten.
    /// </summary>
    /// <param name="ids">The item ids; ignored when <paramref name="all"/> is set.</param>
    /// <param name="all">Whether to take every item waiting for a decision.</param>
    /// <param name="images">The server's poster restorer.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="onItem">Told after each item whether it was handled (<c>true</c>) or failed.</param>
    /// <returns>How many were handled and how many failed.</returns>
    public async Task<(int Done, int Failed)> Revert(IEnumerable<string>? ids, bool all, IHostImages? images, ILogger logger, CancellationToken cancellationToken, Action<bool>? onItem = null)
    {
        var done = 0;
        var failed = 0;

        foreach (var record in Selected(ids, all))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (images == null || !File.Exists(PathOf(record)))
                {
                    failed++;
                    onItem?.Invoke(false);
                    continue;
                }

                if (!await images.RestorePoster(record.Id, PathOf(record), record.BackupMimeType, cancellationToken).ConfigureAwait(false))
                {
                    DeleteFiles(record.Id);
                    _db.ImageBackups.Delete(record.Id);
                    done++;
                    onItem?.Invoke(true);
                    continue;
                }

                DeleteFiles(record.Id);
                record.BackupFile = string.Empty;
                record.State = ImageBackupState.Reverted;
                _db.ImageBackups.Update(record);
                done++;
                onItem?.Invoke(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One item that cannot be restored must not stop the rest.
                failed++;
                onItem?.Invoke(false);
                logger.LogWarning(ex, "AniMeta+ could not put back the previous poster of {Name} ({Id})", record.ItemName, record.Id);
            }
        }

        return (done, failed);
    }

    /// <summary>
    /// Accepts AniDB's poster: the previous one is deleted.
    /// </summary>
    /// <param name="ids">The item ids; ignored when <paramref name="all"/> is set.</param>
    /// <param name="all">Whether to take every item waiting for a decision.</param>
    /// <returns>How many were handled.</returns>
    public int Acknowledge(IEnumerable<string>? ids, bool all)
    {
        var done = 0;

        foreach (var record in Selected(ids, all))
        {
            DeleteFiles(record.Id);
            record.BackupFile = string.Empty;
            record.State = ImageBackupState.Acknowledged;
            _db.ImageBackups.Update(record);
            done++;
        }

        return done;
    }

    /// <summary>
    /// Forgets the items the owner reverted, so that the next task run replaces their posters
    /// again.
    /// </summary>
    /// <returns>How many were forgotten.</returns>
    public int ForgetReverted()
    {
        var reverted = _db.ImageBackups.FindAll().Where(r => r.State == ImageBackupState.Reverted).ToList();
        foreach (var record in reverted)
        {
            _db.ImageBackups.Delete(record.Id);
        }

        return reverted.Count;
    }

    private static string MimeTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/jpeg"
    };

    /// <summary>
    /// The ids of the items a selection takes, among those waiting for a decision.
    /// </summary>
    /// <param name="ids">The item ids; ignored when <paramref name="all"/> is set.</param>
    /// <param name="all">Whether to take every item waiting for a decision.</param>
    /// <returns>The ids.</returns>
    public IReadOnlyList<string> SelectedIds(IEnumerable<string>? ids, bool all) => Selected(ids, all).Select(r => r.Id).ToList();

    private List<ImageBackupRecord> Selected(IEnumerable<string>? ids, bool all)
    {
        if (all)
        {
            return ListPending().ToList();
        }

        return (ids ?? []).Distinct(StringComparer.Ordinal)
            .Select(Find)
            .OfType<ImageBackupRecord>()
            .Where(r => r.State == ImageBackupState.Pending)
            .ToList();
    }

    // The file name comes from the database, but only its name part is trusted to stay in the folder.
    private string PathOf(ImageBackupRecord record) => Path.Combine(_folder, Path.GetFileName(record.BackupFile));

    private void DeleteFiles(string id)
    {
        if (!Directory.Exists(_folder) || id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return;
        }

        foreach (var file in Directory.GetFiles(_folder, id + ".*"))
        {
            File.Delete(file);
        }
    }
}

/// <summary>
/// Puts previous posters back in the background, one run at a time, so that reverting thousands
/// of items does not hold a page request open until it times out.
/// </summary>
public static class PosterRevertJob
{
    private static readonly Lock _lock = new();
    private static ImageJobStatus _status = new();

    /// <summary>
    /// Gets how the current or last run stands.
    /// </summary>
    public static ImageJobStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status.Copy();
            }
        }
    }

    /// <summary>
    /// Starts reverting the selected items, unless a run is already going, and returns at once.
    /// </summary>
    /// <param name="ids">The item ids; ignored when <paramref name="all"/> is set.</param>
    /// <param name="all">Whether to take every item waiting for a decision.</param>
    /// <param name="images">The server's poster restorer.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The run's status.</returns>
    public static ImageJobStatus Start(IEnumerable<string>? ids, bool all, IHostImages? images, ILogger logger)
    {
        lock (_lock)
        {
            if (_status.Running)
            {
                return _status.Copy();
            }

            var backups = PosterBackups.Current;
            var selected = backups.SelectedIds(ids, all);
            _status = new ImageJobStatus { Running = selected.Count > 0, Total = selected.Count, StartedUtc = DateTime.UtcNow };

            if (selected.Count == 0)
            {
                _status.FinishedUtc = DateTime.UtcNow;
                return _status.Copy();
            }

            _ = Task.Run(() => Run(backups, selected, images, logger));

            var started = _status.Copy();
            started.Started = true;

            return started;
        }
    }

    private static async Task Run(PosterBackups backups, IReadOnlyList<string> ids, IHostImages? images, ILogger logger)
    {
        try
        {
            await backups.Revert(ids, false, images, logger, CancellationToken.None, ok =>
            {
                lock (_lock)
                {
                    if (ok)
                    {
                        _status.Done++;
                    }
                    else
                    {
                        _status.Failed++;
                    }
                }
            }).ConfigureAwait(false);

            logger.LogInformation("AniMeta+ put the previous poster back on {Done} items, {Failed} failed", _status.Done, _status.Failed);
        }
        catch (Exception ex)
        {
            // Whatever is left stays waiting in the list and can be reverted again.
            logger.LogError(ex, "AniMeta+ stopped putting previous posters back");
        }
        finally
        {
            lock (_lock)
            {
                _status.Running = false;
                _status.FinishedUtc = DateTime.UtcNow;
            }
        }
    }
}
