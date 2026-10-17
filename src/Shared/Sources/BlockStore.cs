using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AniMetaPlus.Data;
using AniMetaPlus.Providers.AniDB.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMetaPlus.Sources;

/// <summary>
/// A failed request, sorted into what it means for the next one.
/// </summary>
/// <param name="Scope">Whether the whole source or only this item is refused.</param>
/// <param name="Reason">Why.</param>
/// <param name="Message">What the source said.</param>
/// <param name="HttpStatus">The HTTP status, when there was one.</param>
/// <param name="RetryAfter">How long the source asked to be left alone, when it said.</param>
public sealed record SourceFailure(BlockScope Scope, BlockReason Reason, string Message, int? HttpStatus = null, TimeSpan? RetryAfter = null);

/// <summary>
/// Thrown when a source may not be asked: it is blocked, or its request cap is spent.
/// </summary>
public class SourceBlockedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SourceBlockedException"/> class.
    /// </summary>
    public SourceBlockedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceBlockedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public SourceBlockedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceBlockedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SourceBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets the id of the source that may not be asked.
    /// </summary>
    public string SourceId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the block in force, when it is one rather than a spent cap.
    /// </summary>
    public BlockRecord? Block { get; init; }
}

/// <summary>
/// Sorts failures and remembers them, so that a request that cannot succeed is not sent again
/// until it might.
/// </summary>
public static class BlockStore
{
    private static readonly TimeSpan SameFailureWindow = TimeSpan.FromSeconds(30);

    private static ILogger _logger = NullLogger.Instance;

    /// <summary>
    /// Raised after a block is lifted by hand, with the block that was lifted.
    /// </summary>
    public static event Action<BlockRecord>? Lifted;

    /// <summary>
    /// Sets the logger.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public static void SetLogger(ILogger logger) => _logger = logger;

    /// <summary>
    /// Sorts an exception thrown while asking a source.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>What it means.</returns>
    public static SourceFailure Classify(Exception exception)
    {
        switch (exception)
        {
            case SourceHttpException http:
                return Classify(http.StatusCode, http.Message, http.RetryAfter);

            case HttpRequestException request when request.StatusCode is { } status:
                return Classify(status, request.Message, null);

            case HttpRequestException request:
                return new SourceFailure(BlockScope.Source, BlockReason.Unavailable, request.Message);

            case OperationCanceledException or TimeoutException:
                return new SourceFailure(BlockScope.Source, BlockReason.Unavailable, "The source did not answer in time.");

            case System.Xml.XmlException or System.Text.Json.JsonException or FormatException:
                return new SourceFailure(BlockScope.Item, BlockReason.ParseFailed, exception.Message);

            case KeyNotFoundException:
                return new SourceFailure(BlockScope.Item, BlockReason.NotFound, exception.Message);

            default:
                return new SourceFailure(BlockScope.Item, BlockReason.Other, exception.Message);
        }
    }

    /// <summary>
    /// Whether a failure only means the server is shutting down or restarting, which takes the
    /// services a request needs away from under it: Jellyfin restarts inside its own process.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <returns><c>true</c> when it says nothing about the source.</returns>
    public static bool IsShutdown(Exception exception)
        => exception is ObjectDisposedException
            || exception.InnerException is ObjectDisposedException
            || exception.Message.Contains("Cannot access a disposed object", StringComparison.Ordinal);

    /// <summary>
    /// Removes blocks earlier versions recorded for a failure that only meant the server was
    /// restarting. Run at startup.
    /// </summary>
    /// <returns>How many were removed.</returns>
    public static int RemoveShutdownBlocks()
    {
        var db = Db;
        if (db is null)
        {
            return 0;
        }

        var stale = db.Blocks.FindAll().Where(b => b.Message?.Contains("Cannot access a disposed object", StringComparison.Ordinal) == true).Select(b => b.Id).ToList();
        foreach (var id in stale)
        {
            db.Blocks.Delete(id);
        }

        return stale.Count;
    }

    /// <summary>
    /// Removes the never-expiring 401/403 blocks earlier versions recorded for sources without an
    /// owner-set credential, which nothing would ever have lifted. Run at startup.
    /// </summary>
    /// <returns>How many were removed.</returns>
    public static int RemoveStuckAuthBlocks()
    {
        var db = Db;
        if (db is null)
        {
            return 0;
        }

        var stuck = db.Blocks.FindAll()
            .Where(b => b.Reason == BlockReason.AuthFailed && b.ExpiresUtc == null && !b.Pinned && b.SetBy != "user" && !HasOwnerCredential(b.Source))
            .Select(b => b.Id)
            .ToList();
        foreach (var id in stuck)
        {
            db.Blocks.Delete(id);
        }

        return stuck.Count;
    }

    /// <summary>
    /// Sorts an HTTP status.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <param name="message">What the source said.</param>
    /// <param name="retryAfter">How long the source asked to be left alone.</param>
    /// <returns>What it means.</returns>
    public static SourceFailure Classify(HttpStatusCode status, string message, TimeSpan? retryAfter)
    {
        var code = (int)status;

        return code switch
        {
            429 => new SourceFailure(BlockScope.Source, BlockReason.RateLimited, message, code, retryAfter),
            503 => new SourceFailure(BlockScope.Source, BlockReason.RateLimited, message, code, retryAfter),
            401 or 403 => new SourceFailure(BlockScope.Source, BlockReason.AuthFailed, message, code),
            404 or 410 => new SourceFailure(BlockScope.Item, BlockReason.NotFound, message, code),
            >= 500 => new SourceFailure(BlockScope.Source, BlockReason.ServerError, message, code),
            >= 400 => new SourceFailure(BlockScope.Item, BlockReason.Other, message, code),
            _ => new SourceFailure(BlockScope.Item, BlockReason.Other, message, code)
        };
    }

    /// <summary>
    /// Gets the block that stops the whole source, if one is in force.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <returns>The block, or <c>null</c>.</returns>
    public static BlockRecord? SourceBlock(string source)
    {
        var block = Db?.Blocks.FindById(BlockRecord.MakeId(source, BlockScope.Source, string.Empty, string.Empty));

        return block != null && block.IsActive(DateTime.UtcNow) ? block : null;
    }

    /// <summary>
    /// Gets the block that stops a source for an item, if one is in force: one for the given
    /// kind of data, or one for every kind.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="itemKey">The item key.</param>
    /// <param name="kind">The kind of data, or empty.</param>
    /// <returns>The block, or <c>null</c>.</returns>
    public static BlockRecord? ItemBlock(string source, string itemKey, string kind = "")
    {
        var db = Db;
        if (db == null || string.IsNullOrEmpty(itemKey))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var any = db.Blocks.FindById(BlockRecord.MakeId(source, BlockScope.Item, itemKey, string.Empty));
        if (any != null && any.IsActive(now))
        {
            return any;
        }

        if (string.IsNullOrEmpty(kind))
        {
            return null;
        }

        var specific = db.Blocks.FindById(BlockRecord.MakeId(source, BlockScope.Item, itemKey, kind));

        return specific != null && specific.IsActive(now) ? specific : null;
    }

    /// <summary>
    /// Gets a value indicating whether a source may be asked about an item.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="itemKey">The item key, or empty.</param>
    /// <param name="kind">The kind of data, or empty.</param>
    /// <returns><c>true</c> when nothing stops it.</returns>
    public static bool IsAllowed(string source, string itemKey = "", string kind = "")
        => SourceBlock(source) == null && ItemBlock(source, itemKey, kind) == null;

    /// <summary>
    /// Remembers a failure, doubling the backoff of a block that keeps being hit.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="failure">The failure.</param>
    /// <param name="itemKey">The item key, used for an item block.</param>
    /// <param name="itemName">The item's name, for the settings page.</param>
    /// <param name="kind">The kind of data, or empty for every kind.</param>
    /// <returns>The block now in force.</returns>
    public static BlockRecord? Record(string source, SourceFailure failure, string itemKey = "", string itemName = "", string kind = "")
    {
        var db = Db;
        if (db == null)
        {
            return null;
        }

        var scope = failure.Scope == BlockScope.Item && string.IsNullOrEmpty(itemKey) ? BlockScope.Source : failure.Scope;
        var key = scope == BlockScope.Source ? string.Empty : itemKey;
        var blockKind = scope == BlockScope.Source ? string.Empty : kind;
        var id = BlockRecord.MakeId(source, scope, key, blockKind);
        var now = DateTime.UtcNow;

        var block = db.Blocks.FindById(id) ?? new BlockRecord
        {
            Id = id,
            Source = source,
            Scope = scope,
            ItemKey = key,
            Kind = blockKind,
            FirstSeenUtc = now
        };

        // A pinned block is the owner's; a failure does not shorten or replace it.
        if (block.Pinned)
        {
            return block;
        }

        // Requests in flight together fail together: one stall is one failure, not a doubling per request.
        if (block.LastSeenUtc > now - SameFailureWindow && block.IsActive(now))
        {
            return block;
        }

        var (initial, maximum) = Backoff(failure.Reason, scope);
        var previous = block.ExpiresUtc is { } expires && expires > now - maximum && block.BackoffMinutes > 0
            ? TimeSpan.FromMinutes(block.BackoffMinutes)
            : TimeSpan.Zero;
        var backoff = previous == TimeSpan.Zero ? initial : TimeSpan.FromTicks(Math.Min(previous.Ticks * 2, maximum.Ticks));

        if (failure.RetryAfter is { } retryAfter && retryAfter > backoff && retryAfter < TimeSpan.FromDays(7))
        {
            backoff = retryAfter;
        }

        block.Reason = failure.Reason;
        block.Message = failure.Message.Length > 500 ? failure.Message[..500] : failure.Message;
        block.HttpStatus = failure.HttpStatus;
        block.LastSeenUtc = now;
        block.Count++;
        block.BackoffMinutes = (int)Math.Ceiling(backoff.TotalMinutes);
        block.SetBy = "auto";

        // A refused key or client is refused until it is changed, however long that takes. A 401 or
        // 403 from a source with no owner-set credential (a Cloudflare page, say) is only backed off.
        var permanent = failure.Reason == BlockReason.ClientRejected
            || (failure.Reason == BlockReason.AuthFailed && HasOwnerCredential(source));
        block.ExpiresUtc = permanent ? null : now + backoff;

        if (!string.IsNullOrEmpty(itemName))
        {
            block.ItemName = itemName;
        }

        db.Blocks.Upsert(block);
        db.Wrote();

        if (scope == BlockScope.Source)
        {
            _logger.LogWarning(
                "{Source} is blocked ({Reason}) {Until}: {Message}",
                source,
                failure.Reason,
                block.ExpiresUtc is null ? "until its key or client is changed" : "for " + backoff,
                block.Message);
        }
        else
        {
            _logger.LogInformation(
                "{Source} is not asked about {Item} again for {Backoff} ({Reason}): {Message}",
                source,
                string.IsNullOrEmpty(itemName) ? itemKey : itemName,
                backoff,
                failure.Reason,
                block.Message);
        }

        return block;
    }

    /// <summary>
    /// Sets a source-wide block with an expiry worked out elsewhere, as AniDB's ban is.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="reason">Why.</param>
    /// <param name="message">What happened.</param>
    /// <param name="expiresUtc">When it lapses.</param>
    /// <param name="backoffMinutes">The backoff it represents.</param>
    public static void Set(string source, BlockReason reason, string message, DateTime expiresUtc, int backoffMinutes)
    {
        var db = Db;
        if (db == null)
        {
            return;
        }

        var id = BlockRecord.MakeId(source, BlockScope.Source, string.Empty, string.Empty);
        var now = DateTime.UtcNow;
        var block = db.Blocks.FindById(id) ?? new BlockRecord { Id = id, Source = source, Scope = BlockScope.Source, FirstSeenUtc = now };

        if (block.Pinned)
        {
            return;
        }

        block.Reason = reason;
        block.Message = message;
        block.LastSeenUtc = now;
        block.Count++;
        block.ExpiresUtc = expiresUtc;
        block.BackoffMinutes = backoffMinutes;
        block.SetBy = "auto";

        db.Blocks.Upsert(block);
        db.Wrote();
    }

    /// <summary>
    /// Clears the automatic source-wide block of a source after it answered.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="requestStartedUtc">When the request that answered was sent; a block recorded after that is newer than the answer and stays.</param>
    public static void RecordSuccess(string source, DateTime? requestStartedUtc = null)
    {
        var db = Db;
        var id = BlockRecord.MakeId(source, BlockScope.Source, string.Empty, string.Empty);
        var block = db?.Blocks.FindById(id);

        if (block != null && !block.Pinned && block.SetBy != "user" && !(requestStartedUtc is { } started && block.LastSeenUtc > started))
        {
            db!.Blocks.Delete(id);
            db.Wrote();
            _logger.LogInformation("{Source} answered again, so it is no longer blocked", source);
        }
    }

    /// <summary>
    /// Clears the blocks a changed key or client name makes stale.
    /// </summary>
    /// <param name="source">The source id.</param>
    public static void ClearCredentialBlocks(string source)
    {
        var db = Db;
        if (db == null)
        {
            return;
        }

        var deleted = db.Blocks.DeleteMany(b => b.Source == source && !b.Pinned
            && (b.Reason == BlockReason.AuthFailed || b.Reason == BlockReason.ClientRejected));

        if (deleted > 0)
        {
            db.Wrote();
        }
    }

    /// <summary>
    /// Lifts a block by hand.
    /// </summary>
    /// <param name="id">The block id.</param>
    /// <returns>The lifted block, or <c>null</c> when there was none.</returns>
    public static BlockRecord? Lift(string id)
    {
        var db = Db;
        var block = db?.Blocks.FindById(id);
        if (block == null)
        {
            return null;
        }

        db!.Blocks.Delete(id);
        db.DecisionChanged();
        Lifted?.Invoke(block);

        return block;
    }

    /// <summary>
    /// Pins a block, so that it never lapses.
    /// </summary>
    /// <param name="id">The block id.</param>
    /// <returns>The pinned block, or <c>null</c> when there was none.</returns>
    public static BlockRecord? Pin(string id)
    {
        var db = Db;
        var block = db?.Blocks.FindById(id);
        if (block == null)
        {
            return null;
        }

        block.Pinned = true;
        block.SetBy = "user";
        block.ExpiresUtc = null;
        db!.Blocks.Update(block);
        db.DecisionChanged();

        return block;
    }

    /// <summary>
    /// Blocks a source for an item by hand, permanently.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="itemKey">The item key.</param>
    /// <param name="itemName">The item's name.</param>
    /// <param name="kind">The kind of data, or empty for every kind.</param>
    /// <returns>The block.</returns>
    public static BlockRecord? Add(string source, string itemKey, string itemName, string kind)
    {
        var db = Db;
        if (db == null)
        {
            return null;
        }

        var scope = string.IsNullOrEmpty(itemKey) ? BlockScope.Source : BlockScope.Item;
        var now = DateTime.UtcNow;
        var block = new BlockRecord
        {
            Id = BlockRecord.MakeId(source, scope, itemKey, kind),
            Source = source,
            Scope = scope,
            ItemKey = itemKey,
            ItemName = itemName,
            Kind = kind,
            Reason = BlockReason.Manual,
            Message = "Blocked by hand.",
            FirstSeenUtc = now,
            LastSeenUtc = now,
            Count = 1,
            Pinned = true,
            SetBy = "user"
        };

        db.Blocks.Upsert(block);
        db.DecisionChanged();

        return block;
    }

    /// <summary>
    /// Lists blocks, newest first.
    /// </summary>
    /// <param name="includeExpired">Whether to include blocks that have lapsed.</param>
    /// <returns>The blocks.</returns>
    public static IReadOnlyList<BlockRecord> List(bool includeExpired = false)
    {
        var db = Db;
        if (db == null)
        {
            return [];
        }

        var now = DateTime.UtcNow;

        return db.Blocks.FindAll()
            .Where(b => includeExpired || b.IsActive(now))
            .OrderByDescending(b => b.LastSeenUtc)
            .ToList();
    }

    /// <summary>
    /// Removes lapsed automatic blocks.
    /// </summary>
    /// <returns>How many were removed.</returns>
    public static int Prune()
    {
        var db = Db;
        if (db == null)
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(30);
        var now = DateTime.UtcNow;

        // Kept a while after lapsing so that the next failure doubles from them.
        var removed = db.Blocks.DeleteMany(b => !b.Pinned && b.SetBy != "user" && b.ExpiresUtc != null && b.ExpiresUtc < now && b.LastSeenUtc < cutoff);
        if (removed > 0)
        {
            db.Checkpoint();
        }

        return removed;
    }

    // Whether the owner supplied a key or client name for the source, which a refusal then means is wrong.
    private static bool HasOwnerCredential(string source)
    {
        var configuration = Plugin.Instance.Configuration;

        return source switch
        {
            SourceIds.AniDb => configuration.AniDbConfigured,
            SourceIds.Mal => !string.IsNullOrWhiteSpace(configuration.MalClientId),
            _ => false
        };
    }

    private static AniMetaDb? Db
    {
        get
        {
            try
            {
                return Plugin.Instance is null ? null : AniMetaDb.Current;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "The AniMeta+ database is not available");
                return null;
            }
        }
    }

    /// <summary>
    /// How long a block of each kind first lasts, and the most it doubles to.
    /// </summary>
    /// <param name="reason">Why.</param>
    /// <param name="scope">What it covers.</param>
    /// <returns>The first and the longest backoff.</returns>
    internal static (TimeSpan Initial, TimeSpan Maximum) Backoff(BlockReason reason, BlockScope scope) => reason switch
    {
        BlockReason.Banned => (AniDbSeriesProvider.InitialBanBackoff, AniDbSeriesProvider.MaximumBanBackoff),
        BlockReason.RateLimited => (TimeSpan.FromMinutes(1), TimeSpan.FromHours(2)),
        BlockReason.AuthFailed => (TimeSpan.FromHours(1), TimeSpan.FromHours(24)),
        BlockReason.Unavailable => (TimeSpan.FromMinutes(5), TimeSpan.FromHours(6)),
        BlockReason.ServerError => (TimeSpan.FromMinutes(5), TimeSpan.FromHours(6)),
        BlockReason.NotFound => (TimeSpan.FromDays(7), TimeSpan.FromDays(30)),
        BlockReason.ParseFailed => (TimeSpan.FromDays(1), TimeSpan.FromDays(7)),
        BlockReason.Empty => (TimeSpan.FromDays(3), TimeSpan.FromDays(14)),
        _ when scope == BlockScope.Source => (TimeSpan.FromMinutes(15), TimeSpan.FromHours(6)),
        _ => (TimeSpan.FromHours(6), TimeSpan.FromDays(3))
    };
}

/// <summary>
/// An HTTP failure from a source, with what it said about retrying.
/// </summary>
public class SourceHttpException : HttpRequestException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SourceHttpException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="statusCode">The status.</param>
    /// <param name="retryAfter">How long the source asked to be left alone.</param>
    public SourceHttpException(string message, HttpStatusCode statusCode, TimeSpan? retryAfter)
        : base(message, null, statusCode)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceHttpException"/> class.
    /// </summary>
    public SourceHttpException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceHttpException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public SourceHttpException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceHttpException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SourceHttpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets how long the source asked to be left alone.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// Gets the status code. Shadows the base property so that it is never empty.
    /// </summary>
    public new HttpStatusCode StatusCode => base.StatusCode ?? HttpStatusCode.InternalServerError;
}
