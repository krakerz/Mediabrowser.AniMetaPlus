using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using JsonSerializer = System.Text.Json.JsonSerializer;
using System.Threading;
using AniMetaPlus.Core;
using LiteDB;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMetaPlus.Data;

/// <summary>
/// What the owner decided, written out on its own so that it survives a database that can no
/// longer be opened, and moves between servers.
/// </summary>
public class DecisionsExport
{
    /// <summary>Gets or sets the format version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets when it was written.</summary>
    public DateTime ExportedUtc { get; set; }

    /// <summary>Gets or sets the items identified by hand.</summary>
    public List<MappingRecord> Mappings { get; set; } = [];

    /// <summary>Gets or sets the blocks the owner pinned or set.</summary>
    public List<BlockRecord> Blocks { get; set; } = [];

    /// <summary>Gets or sets the decided reviews.</summary>
    public List<ReviewRecord> Reviews { get; set; } = [];

    /// <summary>Gets or sets the items the owner chose to leave unmatched.</summary>
    public List<UnmatchedRecord> Ignored { get; set; } = [];

    /// <summary>Gets or sets the fields the owner pinned, locked or kept; absent in older files.</summary>
    public List<ProvenanceRecord> Provenance { get; set; } = [];
}

/// <summary>
/// The plugin's database: blocks, where each field came from, the queues and what the owner
/// decided. One file, opened once per process.
/// </summary>
public sealed class AniMetaDb : IDisposable
{
    /// <summary>
    /// The database file name.
    /// </summary>
    public const string FileName = "animetaplus.db";

    /// <summary>
    /// The file what the owner decided is mirrored to after every change.
    /// </summary>
    public const string DecisionsFileName = "decisions.json";

    private const int RequestLogSize = 1000;
    private const int WritesPerCheckpoint = 50;

    private static readonly object _openLock = new();
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private static AniMetaDb? _instance;

    private readonly LiteDatabase _db;
    private readonly string _folder;
    private readonly ILogger _logger;
    private int _writes;

    private AniMetaDb(LiteDatabase db, string folder, ILogger logger)
    {
        _db = db;
        _folder = folder;
        _logger = logger;

        Blocks.EnsureIndex(b => b.Source);
        Blocks.EnsureIndex(b => b.ItemKey);
        Provenance.EnsureIndex(p => p.ItemKey);
        Provenance.EnsureIndex(p => p.Provisional);
        Reviews.EnsureIndex(r => r.Status);
        Waiting.EnsureIndex(w => w.Source);
        RequestLog.EnsureIndex(r => r.AtUtc);
    }

    /// <summary>
    /// Gets the blocks.
    /// </summary>
    public ILiteCollection<BlockRecord> Blocks => _db.GetCollection<BlockRecord>("blocks");

    /// <summary>
    /// Gets where each field came from.
    /// </summary>
    public ILiteCollection<ProvenanceRecord> Provenance => _db.GetCollection<ProvenanceRecord>("provenance");

    /// <summary>
    /// Gets the items identified by hand.
    /// </summary>
    public ILiteCollection<MappingRecord> Mappings => _db.GetCollection<MappingRecord>("mappings");

    /// <summary>
    /// Gets the items nothing identified.
    /// </summary>
    public ILiteCollection<UnmatchedRecord> Unmatched => _db.GetCollection<UnmatchedRecord>("unmatched");

    /// <summary>
    /// Gets the items waiting for a paused source.
    /// </summary>
    public ILiteCollection<WaitingRecord> Waiting => _db.GetCollection<WaitingRecord>("waiting");

    /// <summary>
    /// Gets the replacements waiting for review.
    /// </summary>
    public ILiteCollection<ReviewRecord> Reviews => _db.GetCollection<ReviewRecord>("reviews");

    /// <summary>
    /// Gets the last client and key checks.
    /// </summary>
    public ILiteCollection<ClientTestRecord> ClientTests => _db.GetCollection<ClientTestRecord>("clienttests");

    /// <summary>
    /// Gets the recent requests.
    /// </summary>
    public ILiteCollection<RequestLogRecord> RequestLog => _db.GetCollection<RequestLogRecord>("requests");

    /// <summary>
    /// Gets the posters the AniDB poster task replaced.
    /// </summary>
    public ILiteCollection<ImageBackupRecord> ImageBackups => _db.GetCollection<ImageBackupRecord>("imagebackups");

    /// <summary>
    /// Gets the named values kept between runs.
    /// </summary>
    public ILiteCollection<StateRecord> State => _db.GetCollection<StateRecord>("state");

    /// <summary>
    /// Gets the options to read an export with: Emby writes enums by name and Jellyfin by number.
    /// </summary>
    public static JsonSerializerOptions ReadOptions { get; } = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>
    /// Gets the database, opening it on first use.
    /// </summary>
    /// <param name="paths">The server paths.</param>
    /// <param name="logger">The logger, used the first time only.</param>
    /// <returns>The database.</returns>
    public static AniMetaDb Get(IApplicationPaths paths, ILogger? logger = null)
    {
        var instance = Volatile.Read(ref _instance);
        if (instance != null)
        {
            return instance;
        }

        lock (_openLock)
        {
            return _instance ??= Open(PluginPaths.DataFolder(paths), logger ?? NullLogger.Instance);
        }
    }

    /// <summary>
    /// Gets the database of the running plugin.
    /// </summary>
    public static AniMetaDb Current => Get(Plugin.Instance.AppPaths);

    /// <summary>
    /// Opens the database in a folder, setting a damaged one aside and starting again from the
    /// last copy of the owner's decisions.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The database.</returns>
    internal static AniMetaDb Open(string folder, ILogger logger)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);

        try
        {
            return Create(path, folder, logger);
        }
        // A damaged file fails in whatever way the bytes it holds happen to lead to, not only
        // with LiteException, so anything short of running out of memory counts.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            logger.LogError(ex, "The AniMeta+ database {Path} could not be opened. It is set aside as {Suffix} and a new one started from the last copy of your decisions", path, ".corrupt-" + stamp);

            MoveAside(path, stamp);
            MoveAside(path + "-log", stamp);
            MoveAside(Path.ChangeExtension(path, null) + "-log.db", stamp);

            var db = Create(path, folder, logger);
            var decisions = Path.Combine(folder, DecisionsFileName);

            if (File.Exists(decisions))
            {
                try
                {
                    db.Import(JsonSerializer.Deserialize<DecisionsExport>(File.ReadAllText(decisions), ReadOptions) ?? new DecisionsExport(), writeBackup: false);
                }
                catch (Exception importException) when (importException is JsonException or IOException)
                {
                    logger.LogError(importException, "The copy of your decisions {Path} could not be read either", decisions);
                }
            }

            return db;
        }
    }

    /// <summary>
    /// Counts a write, and folds the log into the database file every so often so that a copy
    /// of the file taken by a backup is complete.
    /// </summary>
    public void Wrote()
    {
        if (Interlocked.Increment(ref _writes) % WritesPerCheckpoint == 0)
        {
            Checkpoint();
        }
    }

    /// <summary>
    /// Folds the log into the database file.
    /// </summary>
    public void Checkpoint()
    {
        try
        {
            _db.Checkpoint();
        }
        catch (LiteException ex)
        {
            _logger.LogWarning(ex, "Could not checkpoint the AniMeta+ database");
        }
    }

    /// <summary>
    /// Records a change to what the owner decided: checkpoints, and mirrors the decisions to
    /// their own file.
    /// </summary>
    public void DecisionChanged()
    {
        Checkpoint();

        try
        {
            var path = Path.Combine(_folder, DecisionsFileName);
            var partial = path + ".partial";
            File.WriteAllText(partial, JsonSerializer.Serialize(Export(), _json));
            File.Move(partial, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LiteException)
        {
            _logger.LogWarning(ex, "Could not write the copy of your AniMeta+ decisions");
        }
    }

    /// <summary>
    /// Collects what the owner decided. Holds no keys or client names.
    /// </summary>
    /// <returns>The decisions.</returns>
    public DecisionsExport Export() => new()
    {
        ExportedUtc = DateTime.UtcNow,
        Mappings = Mappings.FindAll().ToList(),

        // In memory: a lambda query on a record can fail on its mapper member lookup.
        Blocks = Blocks.FindAll().Where(b => b.Pinned || b.SetBy == "user").ToList(),
        Reviews = Reviews.FindAll().Where(r => r.Status != ReviewStatus.Pending).ToList(),
        Ignored = Unmatched.FindAll().Where(u => u.Ignored).ToList(),

        // A pin on a stand-in while a review is open is not a decision, and goes with the review.
        Provenance = Provenance.FindAll().Where(p => p.Locked || p.DeclinedValue.Length > 0 || (p.PinnedSource.Length > 0 && !p.Provisional)).ToList()
    };

    /// <summary>
    /// Merges exported decisions in, replacing records with the same key.
    /// </summary>
    /// <param name="decisions">The decisions.</param>
    /// <param name="writeBackup">Whether to mirror the result to the decisions file.</param>
    /// <returns>How many records were taken.</returns>
    public int Import(DecisionsExport decisions, bool writeBackup = true)
    {
        ArgumentNullException.ThrowIfNull(decisions);

        // Lists a hand-edited or older file leaves out come in as null.
        var count = UpsertAll(Mappings, decisions.Mappings ?? [], m => m.Id)
            + UpsertAll(Blocks, decisions.Blocks ?? [], b => b.Id)
            + UpsertAll(Reviews, decisions.Reviews ?? [], r => r.Id)
            + UpsertAll(Unmatched, decisions.Ignored ?? [], u => u.Id)
            + ImportProvenance(decisions.Provenance ?? []);

        if (writeBackup)
        {
            DecisionChanged();
        }
        else
        {
            Checkpoint();
        }

        return count;
    }

    /// <summary>
    /// Appends a request to the log, dropping the oldest beyond its size.
    /// </summary>
    /// <param name="record">The request.</param>
    public void LogRequest(RequestLogRecord record)
    {
        try
        {
            RequestLog.Insert(record);

            if (record.Id % 100 == 0)
            {
                var cutoff = record.Id - RequestLogSize;
                RequestLog.DeleteMany(r => r.Id <= cutoff);
            }

            Wrote();
        }
        catch (LiteException ex)
        {
            _logger.LogDebug(ex, "Could not log a request");
        }
    }

    /// <summary>
    /// Reads a named value.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The value, or <c>null</c> when none was kept.</returns>
    public string? GetState(string name) => State.FindById(name)?.Value;

    /// <summary>
    /// Keeps a named value.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="value">The value.</param>
    public void SetState(string name, string value)
    {
        State.Upsert(new StateRecord { Id = name, Value = value });
        Wrote();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Checkpoint();
        _db.Dispose();

        lock (_openLock)
        {
            if (ReferenceEquals(_instance, this))
            {
                _instance = null;
            }
        }
    }

    private static LiteDatabase Connect(string path)
    {
        var db = new LiteDatabase(new ConnectionString
        {
            Filename = path,
            Connection = ConnectionType.Direct
        })
        {
            UtcDate = true
        };

        try
        {
            // Touching a collection reads the header and the first pages, which is where a damaged
            // file shows; better to find out here than on the first lookup.
            _ = db.GetCollectionNames().ToList();

            return db;
        }
        catch
        {
            // Left open, it would hold the file and the damaged copy could not be moved aside.
            db.Dispose();

            throw;
        }
    }

    private static AniMetaDb Create(string path, string folder, ILogger logger)
    {
        var db = Connect(path);

        try
        {
            return new AniMetaDb(db, folder, logger);
        }
        catch
        {
            // The indexes are built here, and a damaged file can fail on them.
            db.Dispose();

            throw;
        }
    }

    private int ImportProvenance(IEnumerable<ProvenanceRecord> records)
    {
        var count = 0;

        foreach (var record in records)
        {
            if (string.IsNullOrEmpty(record.Id))
            {
                continue;
            }

            // Only the decision is taken over a row the server already has.
            var existing = Provenance.FindById(record.Id);
            if (existing == null)
            {
                Provenance.Upsert(record);
            }
            else
            {
                existing.PinnedSource = record.PinnedSource;
                existing.Locked = record.Locked;
                existing.DeclinedValue = record.DeclinedValue;
                existing.Provisional = false;
                Provenance.Update(existing);
            }

            count++;
        }

        return count;
    }

    private static int UpsertAll<T>(ILiteCollection<T> collection, IEnumerable<T> records, Func<T, string> key)
    {
        var count = 0;

        foreach (var record in records)
        {
            if (!string.IsNullOrEmpty(key(record)))
            {
                collection.Upsert(record);
                count++;
            }
        }

        return count;
    }

    private static void MoveAside(string path, string stamp)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, path + ".corrupt-" + stamp, true);
            }
        }
        catch (IOException)
        {
            // Left where it is; the new database is opened over it regardless.
        }
    }
}
