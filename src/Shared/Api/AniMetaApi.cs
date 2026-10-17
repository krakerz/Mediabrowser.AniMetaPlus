using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Configuration;
using AniMetaPlus.Core;
using AniMetaPlus.Data;
using AniMetaPlus.Providers.AniDB;
using AniMetaPlus.Providers.AniDB.Mapping;
using AniMetaPlus.Providers.AniDB.Metadata;
using AniMetaPlus.Sources;
using AniMetaPlus.Titles;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Api;

/// <summary>
/// How one source stands, for the settings page.
/// </summary>
public class SourceStatus
{
    /// <summary>Gets or sets the source id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether it is turned on.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether it can be asked: has its key or client, or is installed.</summary>
    public bool Available { get; set; }

    /// <summary>Gets or sets why it cannot be asked, when it cannot.</summary>
    public string? Unavailable { get; set; }

    /// <summary>Gets or sets the fallback mode.</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>Gets or sets the source-wide block in force, if any.</summary>
    public BlockRecord? Block { get; set; }

    /// <summary>Gets or sets the requests sent in the last hour.</summary>
    public int LastHour { get; set; }

    /// <summary>Gets or sets the requests sent in the last day.</summary>
    public int LastDay { get; set; }

    /// <summary>Gets or sets the last check of its client or key.</summary>
    public ClientTestRecord? LastTest { get; set; }
}

/// <summary>
/// Everything the settings page shows at a glance.
/// </summary>
public class OverviewStatus
{
    /// <summary>Gets or sets AniDB's request queue and ban.</summary>
    public AniDbStatus AniDb { get; set; } = new();

    /// <summary>Gets or sets every source.</summary>
    public List<SourceStatus> Sources { get; set; } = [];

    /// <summary>Gets or sets the titles dump.</summary>
    public TitlesDumpStatus? Titles { get; set; }

    /// <summary>Gets or sets how many items wait for a paused source.</summary>
    public int Waiting { get; set; }

    /// <summary>Gets or sets how many replacements wait for review.</summary>
    public int PendingReviews { get; set; }

    /// <summary>Gets or sets how many items nothing identified.</summary>
    public int Unmatched { get; set; }

    /// <summary>Gets or sets how many fields a stand-in supplied.</summary>
    public int Provisional { get; set; }

    /// <summary>Gets or sets how many blocks are in force.</summary>
    public int ActiveBlocks { get; set; }

    /// <summary>Gets or sets every source's settings, defaults filled in.</summary>
    public List<SourceSetting> Settings { get; set; } = [];

    /// <summary>Gets or sets the source order of every kind of data, defaults filled in.</summary>
    public List<KindPriority> Priorities { get; set; } = [];
}

/// <summary>
/// The result of checking a client name or key.
/// </summary>
/// <param name="Result">valid, invalid, banned, unreachable, throttled or unconfigured.</param>
/// <param name="Message">What was found.</param>
public sealed record ClientTestResult(string Result, string Message);

/// <summary>
/// The answer to mapping an item by hand.
/// </summary>
public class MapResult
{
    /// <summary>Gets or sets the id the item was mapped to, with its source prefix when it is not AniDB.</summary>
    public string AnimeId { get; set; } = string.Empty;
}

/// <summary>
/// One replaced poster waiting for the owner's decision.
/// </summary>
public class ImageBackupItem
{
    /// <summary>Gets or sets the server's item id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's name.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets the series name of a season, otherwise empty.</summary>
    public string SeriesName { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind: Series, Season or Movie.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets when the poster was replaced.</summary>
    public DateTime ReplacedUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether the previous poster is held.</summary>
    public bool HasBackup { get; set; }
}

/// <summary>
/// One page of the replaced posters waiting for the owner's decision.
/// </summary>
public class ImageBackupPage
{
    /// <summary>Gets or sets how many are waiting in all.</summary>
    public int TotalCount { get; set; }

    /// <summary>Gets or sets how many items the owner reverted, which the task skips.</summary>
    public int RevertedCount { get; set; }

    /// <summary>Gets or sets the page.</summary>
    public List<ImageBackupItem> Items { get; set; } = [];
}

/// <summary>
/// A held poster, as a data address the settings page can show.
/// </summary>
public class ImageBackupPreview
{
    /// <summary>Gets or sets the data address, or empty when no backup is held.</summary>
    public string DataUrl { get; set; } = string.Empty;
}

/// <summary>
/// The answer to reverting posters.
/// </summary>
public class ImageJobStatus
{
    /// <summary>Gets or sets a value indicating whether a run is going.</summary>
    public bool Running { get; set; }

    /// <summary>Gets or sets a value indicating whether this request started the run, rather than finding one going.</summary>
    public bool Started { get; set; }

    /// <summary>Gets or sets how many items the run takes.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets how many were handled.</summary>
    public int Done { get; set; }

    /// <summary>Gets or sets how many could not be restored.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets when the run started.</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>Gets or sets when the run ended.</summary>
    public DateTime? FinishedUtc { get; set; }

    /// <summary>Copies the status.</summary>
    /// <returns>The copy.</returns>
    public ImageJobStatus Copy() => (ImageJobStatus)MemberwiseClone();
}

/// <summary>
/// The answer to acknowledging or forgetting posters.
/// </summary>
public class ImageDoneResult
{
    /// <summary>Gets or sets how many were handled.</summary>
    public int Done { get; set; }
}

/// <summary>
/// What the settings page can ask of the plugin, the same on both servers. Each server's own
/// endpoints only pass requests through to this.
/// </summary>
public static class AniMetaApi
{
    private static readonly TimeSpan _testGap = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private static AniMetaDb Db => AniMetaDb.Current;

    /// <summary>
    /// Gets AniDB's status in the shape the original status page read.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status.</returns>
    public static async Task<AniDbStatus> GetAniDbStatus(ILogger logger, CancellationToken cancellationToken)
    {
        var paths = Plugin.Instance.AppPaths;
        var (banRemaining, queued, untilNext, sent, lastSentUtc) = AniDbSeriesProvider.GetRequestStatus();
        var (cachedAtUtc, checkedAtUtc, entryCount, maxAgeInDays) = AniDbAnimeList.GetStatus(paths);
        var (bridgeCachedAtUtc, bridgeCheckedAtUtc, bridgeEntryCount, bridgeMaxAgeInDays) = AniBridgeMappings.GetStatus(paths);
        var (overridesPath, overridesWrittenAtUtc, overridesEntryCount, overridesShowCount, overridesMovieCount, overridesError) =
            await AniDbMappingOverrides.GetStatus(paths, logger, cancellationToken).ConfigureAwait(false);

        return new AniDbStatus
        {
            IsBanned = banRemaining > TimeSpan.Zero,
            BanRemainingSeconds = banRemaining.TotalSeconds,
            QueuedRequests = queued,
            NextRequestInSeconds = untilNext.TotalSeconds,
            RequestsSent = sent,
            LastRequestUtc = lastSentUtc,
            RequestIntervalMs = AniDbSeriesProvider.GetRequestIntervalMs(),
            AnimeListCachedAtUtc = cachedAtUtc,
            AnimeListCheckedAtUtc = checkedAtUtc,
            AnimeListEntryCount = entryCount,
            AnimeListMaxAgeDays = maxAgeInDays,
            AniBridgeCachedAtUtc = bridgeCachedAtUtc,
            AniBridgeCheckedAtUtc = bridgeCheckedAtUtc,
            AniBridgeEntryCount = bridgeEntryCount,
            AniBridgeMaxAgeDays = bridgeMaxAgeInDays,
            AniBridgeEnabled = Plugin.Instance.Configuration.UseAniBridgeMappings,
            OverridesPath = overridesPath,
            OverridesWrittenAtUtc = overridesWrittenAtUtc,
            OverridesEntryCount = overridesEntryCount,
            OverridesShowCount = overridesShowCount,
            OverridesMovieCount = overridesMovieCount,
            OverridesError = overridesError
        };
    }

    /// <summary>
    /// Checks both downloaded mapping sources for a newer copy.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What each check came to.</returns>
    public static async Task<AniDbSourceCheck> CheckMappingSources(ILogger logger, CancellationToken cancellationToken)
    {
        var paths = Plugin.Instance.AppPaths;
        var outcomes = await Task.WhenAll(
            AniBridgeMappings.CheckNow(paths, logger, cancellationToken),
            AniDbAnimeList.CheckNow(paths, logger, cancellationToken)).ConfigureAwait(false);

        static string Describe(MappingSourceCheck check) => check switch
        {
            MappingSourceCheck.Updated => "a newer copy was downloaded",
            MappingSourceCheck.Unchanged => "already the current copy",
            MappingSourceCheck.NotUsed => "not used",
            MappingSourceCheck.NotDownloaded => "not downloaded",
            _ => "could not be checked - see the server log"
        };

        return new AniDbSourceCheck { AniBridge = Describe(outcomes[0]), AnimeList = Describe(outcomes[1]) };
    }

    /// <summary>
    /// Gets everything the settings page shows at a glance.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status.</returns>
    public static async Task<OverviewStatus> GetOverview(ILogger logger, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance.Configuration;
        var counts = SourceGate.Counts();
        var db = Db;
        var now = DateTime.UtcNow;

        var status = new OverviewStatus
        {
            AniDb = await GetAniDbStatus(logger, cancellationToken).ConfigureAwait(false),
            Titles = TitlesDump.GetStatus(Plugin.Instance.AppPaths),
            Waiting = db.Waiting.Count(),
            PendingReviews = db.Reviews.Count(r => r.Status == ReviewStatus.Pending),
            Unmatched = OpenUnmatched(db).Count(u => !u.Ignored),
            Provisional = db.Provenance.Count(p => p.Provisional),
            ActiveBlocks = BlockStore.List().Count
        };

        foreach (var kind in Enum.GetValues<DataKind>())
        {
            status.Priorities.Add(new KindPriority { Kind = kind.ToString(), Sources = string.Join(",", configuration.GetPriority(kind)) });
        }

        foreach (var id in SourceIds.All)
        {
            var setting = configuration.GetSource(id);
            status.Settings.Add(setting);
            var (available, why) = Availability(id);
            counts.TryGetValue(id, out var count);

            status.Sources.Add(new SourceStatus
            {
                Id = id,
                Enabled = setting.Enabled,
                Available = available,
                Unavailable = why,
                Mode = setting.Mode.ToString(),
                Block = BlockStore.SourceBlock(id),
                LastHour = count.LastHour,
                LastDay = count.LastDay,
                LastTest = db.ClientTests.FindById(id)
            });
        }

        return status;
    }

    /// <summary>
    /// Lists blocks.
    /// </summary>
    /// <param name="includeExpired">Whether to include lapsed blocks.</param>
    /// <returns>The blocks, newest first.</returns>
    public static IReadOnlyList<BlockRecord> GetBlocks(bool includeExpired) => BlockStore.List(includeExpired);

    /// <summary>
    /// Lifts a block. An AniDB ban lifted by hand clears the plugin's own record of it too.
    /// </summary>
    /// <param name="id">The block id.</param>
    /// <returns>The lifted block, or <c>null</c>.</returns>
    public static BlockRecord? LiftBlock(string id)
    {
        var block = BlockStore.Lift(id);

        // An item blocked for a source is refreshed, so that the lift shows at once.
        if (block is { Scope: BlockScope.Item })
        {
            var path = Db.Provenance.Find(p => p.ItemKey == block.ItemKey).Select(p => p.ItemPath).FirstOrDefault(p => !string.IsNullOrEmpty(p));
            if (path != null)
            {
                HostServices.Refresher?.Refresh(path, false);
            }
        }

        return block;
    }

    /// <summary>
    /// Pins a block.
    /// </summary>
    /// <param name="id">The block id.</param>
    /// <returns>The pinned block, or <c>null</c>.</returns>
    public static BlockRecord? PinBlock(string id) => BlockStore.Pin(id);

    /// <summary>
    /// Blocks a source by hand, for one item or as a whole.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="itemKey">The item key, or empty for the whole source.</param>
    /// <param name="itemName">The item's name.</param>
    /// <param name="kind">The kind of data, or empty for every kind.</param>
    /// <returns>The block.</returns>
    public static BlockRecord? AddBlock(string source, string itemKey, string itemName, string kind)
        => BlockStore.Add(source, itemKey ?? string.Empty, itemName ?? string.Empty, kind ?? string.Empty);

    /// <summary>
    /// Lists reviews.
    /// </summary>
    /// <param name="pendingOnly">Whether to list only the undecided.</param>
    /// <returns>The reviews, newest first.</returns>
    public static IReadOnlyList<ReviewRecord> GetReviews(bool pendingOnly)
        => Db.Reviews.FindAll()
            .Where(r => !pendingOnly || r.Status == ReviewStatus.Pending)
            .OrderByDescending(r => r.CreatedUtc)
            .ToList();

    /// <summary>
    /// Decides a review.
    /// </summary>
    /// <param name="id">The review id.</param>
    /// <param name="decision">Accepted, Kept or Locked.</param>
    /// <returns>The decided review, or <c>null</c> when there is none.</returns>
    public static ReviewRecord? DecideReview(string id, ReviewStatus decision)
    {
        var db = Db;
        var review = db.Reviews.FindById(id);
        if (review == null || decision == ReviewStatus.Pending)
        {
            return review;
        }

        var provenance = db.Provenance.FindById(id) ?? new ProvenanceRecord { Id = id, ItemKey = review.ItemKey, Field = review.Field };

        if (decision == ReviewStatus.Accepted)
        {
            provenance.PinnedSource = review.ProposedSource;
            provenance.Provisional = false;
            provenance.PreferredSource = string.Empty;
            provenance.DeclinedValue = string.Empty;
            provenance.Locked = false;
        }
        else
        {
            provenance.PinnedSource = review.CurrentSource;
            provenance.Provisional = false;
            provenance.PreferredSource = string.Empty;
            provenance.DeclinedValue = review.ProposedValue;
            provenance.Locked = decision == ReviewStatus.Locked;
        }

        provenance.UpdatedUtc = DateTime.UtcNow;
        db.Provenance.Upsert(provenance);

        review.Status = decision;
        review.DecidedUtc = DateTime.UtcNow;
        db.Reviews.Update(review);
        db.DecisionChanged();

        if (decision == ReviewStatus.Accepted && !string.IsNullOrEmpty(review.ItemPath))
        {
            HostServices.Refresher?.Refresh(review.ItemPath, false);
        }

        return review;
    }

    /// <summary>
    /// Lists the items nothing identified, leaving out the ones the owner has mapped by hand:
    /// those are listed with the mappings until the refresh that applies them clears them.
    /// </summary>
    /// <param name="includeIgnored">Whether to include the ones the owner set aside.</param>
    /// <returns>The items, most recently tried first.</returns>
    public static IReadOnlyList<UnmatchedRecord> GetUnmatched(bool includeIgnored)
        => OpenUnmatched(Db)
            .Where(u => includeIgnored || !u.Ignored)
            .OrderByDescending(u => u.LastAttemptUtc)
            .ToList();

    // Filtered in memory: LiteDB cannot always translate a lambda over these records into a
    // query, and failed on the Ignored flag once, which took the whole status page down.
    private static IEnumerable<UnmatchedRecord> OpenUnmatched(AniMetaDb db)
    {
        var mapped = db.Mappings.FindAll().Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

        return db.Unmatched.FindAll().Where(u => !mapped.Contains(u.Id));
    }

    /// <summary>
    /// Searches the local titles dump.
    /// </summary>
    /// <param name="query">The title to search for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The closest anime.</returns>
    public static Task<List<CandidateRecord>> SearchTitles(string query, CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(query) ? Task.FromResult(new List<CandidateRecord>()) : Queues.Candidates(query, cancellationToken);

    /// <summary>
    /// Maps an unmatched item to an AniDB id.
    /// </summary>
    /// <param name="itemKey">The item key.</param>
    /// <param name="animeIdOrUrl">The AniDB id, or a link to the anime on AniDB.</param>
    /// <param name="applyNow">Whether to refresh it now rather than on the next queue run.</param>
    /// <returns>The mapped id.</returns>
    public static string Map(string itemKey, string animeIdOrUrl, bool applyNow)
    {
        var animeId = ParseMapping(animeIdOrUrl) ?? throw new ArgumentException("Not an AniDB, AniList or MyAnimeList id or link: " + animeIdOrUrl, nameof(animeIdOrUrl));
        var unmatched = Queues.Map(itemKey, animeId);

        if (applyNow && unmatched is { Path.Length: > 0 } && HostServices.Refresher?.Refresh(unmatched.Path, true) == true)
        {
            unmatched.PendingRefresh = false;
            Db.Unmatched.Update(unmatched);
        }

        return animeId;
    }

    /// <summary>
    /// Sets an unmatched item aside, or brings it back.
    /// </summary>
    /// <param name="itemKey">The item key.</param>
    /// <param name="ignore">Whether to set it aside.</param>
    /// <returns>The item, or <c>null</c>.</returns>
    public static UnmatchedRecord? Ignore(string itemKey, bool ignore)
    {
        var db = Db;
        var record = db.Unmatched.FindById(itemKey);
        if (record == null)
        {
            return null;
        }

        record.Ignored = ignore;
        db.Unmatched.Update(record);
        db.DecisionChanged();

        return record;
    }

    /// <summary>
    /// Removes a mapping made by hand.
    /// </summary>
    /// <param name="itemKey">The item key.</param>
    /// <returns><c>true</c> when there was one.</returns>
    public static bool Unmap(string itemKey)
    {
        var removed = Db.Mappings.Delete(itemKey);
        if (removed)
        {
            Db.DecisionChanged();
        }

        return removed;
    }

    /// <summary>
    /// Lists the mappings made by hand.
    /// </summary>
    /// <returns>The mappings.</returns>
    public static IReadOnlyList<MappingRecord> GetMappings() => Db.Mappings.FindAll().OrderByDescending(m => m.CreatedUtc).ToList();

    /// <summary>
    /// Lists the replaced posters waiting for a decision, newest first.
    /// </summary>
    /// <param name="start">The first item to return.</param>
    /// <param name="limit">How many to return; 24 when not positive.</param>
    /// <returns>The page.</returns>
    public static ImageBackupPage GetImageBackups(int start, int limit)
    {
        var backups = PosterBackups.Current;
        var pending = backups.ListPending();

        return new ImageBackupPage
        {
            TotalCount = pending.Count,
            RevertedCount = backups.CountReverted(),
            Items = pending.Skip(Math.Max(start, 0)).Take(limit > 0 ? limit : 24).Select(r => new ImageBackupItem
            {
                Id = r.Id,
                ItemName = r.ItemName ?? string.Empty,

                // LiteDB reads an empty string back as null.
                SeriesName = r.SeriesName ?? string.Empty,
                Kind = r.Kind ?? string.Empty,
                ReplacedUtc = r.ReplacedUtc,
                HasBackup = !string.IsNullOrEmpty(r.BackupFile)
            }).ToList()
        };
    }

    /// <summary>
    /// Reads the poster held for an item.
    /// </summary>
    /// <param name="id">The server's item id.</param>
    /// <returns>The data address, or <c>null</c> when none is held.</returns>
    public static string? GetImageBackup(string id) => PosterBackups.Current.ReadDataUrl(id);

    /// <summary>
    /// Starts putting the previous posters back in the background.
    /// </summary>
    /// <param name="ids">The item ids.</param>
    /// <param name="all">Whether to take every item waiting.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The run's status; a run already going is reported rather than a new one started.</returns>
    public static ImageJobStatus RevertImages(IEnumerable<string>? ids, bool all, ILogger logger)
        => PosterRevertJob.Start(ids, all, HostServices.Images, logger);

    /// <summary>
    /// Reports how the current or last revert run stands.
    /// </summary>
    /// <returns>The status.</returns>
    public static ImageJobStatus GetRevertStatus() => PosterRevertJob.Status;

    /// <summary>
    /// Accepts AniDB's posters and deletes the previous ones.
    /// </summary>
    /// <param name="ids">The item ids.</param>
    /// <param name="all">Whether to take every item waiting.</param>
    /// <returns>How many were handled.</returns>
    public static ImageDoneResult AcknowledgeImages(IEnumerable<string>? ids, bool all) => new() { Done = PosterBackups.Current.Acknowledge(ids, all) };

    /// <summary>
    /// Forgets the reverted items, so that the next task run replaces their posters again.
    /// </summary>
    /// <returns>How many were forgotten.</returns>
    public static ImageDoneResult ForgetRevertedImages() => new() { Done = PosterBackups.Current.ForgetReverted() };

    /// <summary>
    /// Lists the items waiting for a paused source.
    /// </summary>
    /// <returns>The items.</returns>
    public static IReadOnlyList<WaitingRecord> GetWaiting() => Db.Waiting.FindAll().OrderBy(w => w.AddedUtc).ToList();

    /// <summary>
    /// Gets which source supplied each field of an item.
    /// </summary>
    /// <param name="itemKey">The item key, such as <c>anidb:23</c>.</param>
    /// <returns>The fields.</returns>
    public static IReadOnlyList<ProvenanceRecord> GetProvenance(string itemKey)
        => Db.Provenance.Find(p => p.ItemKey == itemKey).OrderBy(p => p.Field).ToList();

    /// <summary>
    /// Lists the fields a stand-in supplied.
    /// </summary>
    /// <returns>The fields.</returns>
    public static IReadOnlyList<ProvenanceRecord> GetProvisional()
        => Db.Provenance.Find(p => p.Provisional).OrderByDescending(p => p.UpdatedUtc).ToList();

    /// <summary>
    /// Lists the most recent requests.
    /// </summary>
    /// <param name="count">How many.</param>
    /// <returns>The requests, newest first.</returns>
    public static IReadOnlyList<RequestLogRecord> GetRequests(int count)
        => Db.RequestLog.Query().OrderByDescending(r => r.Id).Limit(Math.Clamp(count, 1, 500)).ToList();

    /// <summary>
    /// Checks a source's client name or key with one request.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was found.</returns>
    public static async Task<ClientTestResult> TestClient(string source, CancellationToken cancellationToken)
    {
        var db = Db;
        var configuration = Plugin.Instance.Configuration;

        // Each check is a real request, so they are spaced like any other. An unconfigured
        // result sent nothing, so it does not hold up the check after the key is entered.
        if (db.ClientTests.FindById(source) is { Result: not "unconfigured" } last && DateTime.UtcNow - last.AtUtc < _testGap)
        {
            var since = DateTime.UtcNow - last.AtUtc;
            return new ClientTestResult("throttled", "Checked " + Duration(since) + " ago: " + last.Result + ". Checks are kept " + Duration(_testGap) + " apart, so the next one is allowed in " + Duration(_testGap - since) + ".");
        }

        ClientTestResult result;
        string subject;

        if (string.Equals(source, SourceIds.AniDb, StringComparison.OrdinalIgnoreCase))
        {
            subject = configuration.AniDbClientName + "/" + configuration.AniDbClientVersion;
            BlockStore.ClearCredentialBlocks(SourceIds.AniDb);
            result = await TestAniDb(cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(source, SourceIds.Mal, StringComparison.OrdinalIgnoreCase))
        {
            subject = Mask(configuration.MalClientId);
            BlockStore.ClearCredentialBlocks(SourceIds.Mal);
            result = await TestHttp(source, ct => string.IsNullOrWhiteSpace(configuration.MalClientId)
                ? throw new InvalidOperationException("No MyAnimeList client id is set.")
                : MalClient.Test(Plugin.Instance.GetHttpClient(), configuration.MalClientId, ct), cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(source, SourceIds.AniList, StringComparison.OrdinalIgnoreCase))
        {
            subject = "public API";
            result = await TestHttp(source, ct => AniListClient.GetById(Plugin.Instance.GetHttpClient(), "1", ct), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            subject = source;
            result = DelegatedProviders.IsInstalled(source)
                ? new ClientTestResult("valid", source + " is installed and is asked through the server.")
                : new ClientTestResult("unconfigured", source + " is not installed on this server.");
        }

        db.ClientTests.Upsert(new ClientTestRecord { Id = source, Result = result.Result, Message = result.Message, Subject = subject, AtUtc = DateTime.UtcNow });
        db.Wrote();

        return result;
    }

    /// <summary>
    /// Downloads a fresh titles dump, within AniDB's once-a-day allowance.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What happened.</returns>
    public static Task<TitlesRefreshResult> RefreshTitles(ILogger logger, CancellationToken cancellationToken)
        => TitlesDump.Refresh(Plugin.Instance.AppPaths, Plugin.Instance.GetHttpClient(), force: true, logger, cancellationToken);

    /// <summary>
    /// Imports the titles dump on disk again, after it was replaced by hand.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The dump's status.</returns>
    public static async Task<TitlesDumpStatus> ReimportTitles(ILogger logger, CancellationToken cancellationToken)
    {
        await TitlesDump.EnsureImported(Plugin.Instance.AppPaths, logger, cancellationToken, force: true).ConfigureAwait(false);

        return TitlesDump.GetStatus(Plugin.Instance.AppPaths);
    }

    /// <summary>
    /// Writes the owner's decisions out as JSON. Holds no keys or client names.
    /// </summary>
    /// <returns>The JSON.</returns>
    public static string Export() => JsonSerializer.Serialize(Db.Export(), _json);

    /// <summary>
    /// Merges exported decisions in.
    /// </summary>
    /// <param name="json">The JSON an export wrote.</param>
    /// <returns>How many records were taken.</returns>
    public static int Import(string json)
    {
        var decisions = JsonSerializer.Deserialize<DecisionsExport>(json, AniMetaDb.ReadOptions) ?? throw new ArgumentException("Not an AniMeta+ export.", nameof(json));

        return Db.Import(decisions);
    }

    /// <summary>
    /// Runs the queues now.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was done.</returns>
    public static Task<QueueRunResult> RunQueues(ILogger logger, CancellationToken cancellationToken)
        => QueueProcessor.Run(logger, null, cancellationToken);

    /// <summary>
    /// Searches AniList and MyAnimeList by title.
    /// </summary>
    /// <param name="query">The title.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matches, each with the AniDB entry it maps to, when it does.</returns>
    public static Task<IReadOnlyList<RemoteCandidate>> SearchOnline(string query, ILogger logger, CancellationToken cancellationToken)
        => RemoteSearch.Search(query, logger, cancellationToken);

    /// <summary>
    /// Reads what an item is mapped to: an AniDB id, or "anilist:" or "mal:" and an id, from an
    /// id, one of those, or a link to the anime on AniDB, AniList or MyAnimeList.
    /// </summary>
    /// <param name="value">What the owner entered.</param>
    /// <returns>The mapping, or <c>null</c>.</returns>
    internal static string? ParseMapping(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        // Checked before AniDB's links, whose "/anime/" AniList's and MyAnimeList's share.
        foreach (var (marker, prefix) in new[] { ("anilist.co/anime/", "anilist"), ("myanimelist.net/anime/", "mal"), ("anilist:", "anilist"), ("mal:", "mal") })
        {
            var at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var digits = new string(text[(at + marker.Length)..].TakeWhile(char.IsDigit).ToArray());

                return digits.Length > 0 ? prefix + ":" + digits : null;
            }
        }

        return ParseAnimeId(text);
    }

    /// <summary>
    /// Reads an AniDB id from an id or a link to an anime on AniDB.
    /// </summary>
    /// <param name="value">The id or link.</param>
    /// <returns>The id, or <c>null</c>.</returns>
    internal static string? ParseAnimeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.All(char.IsDigit))
        {
            return text;
        }

        // anidb.net/anime/123, or the old animedb.pl?show=anime&aid=123.
        foreach (var marker in new[] { "/anime/", "aid=" })
        {
            var at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                var digits = new string(text[(at + marker.Length)..].TakeWhile(char.IsDigit).ToArray());
                if (digits.Length > 0)
                {
                    return digits;
                }
            }
        }

        return null;
    }

    private static (bool Available, string? Why) Availability(string id)
    {
        var configuration = Plugin.Instance.Configuration;

        if (string.Equals(id, SourceIds.AniDb, StringComparison.OrdinalIgnoreCase))
        {
            if (!configuration.AniDbConfigured)
            {
                return (false, "No AniDB client name is set.");
            }

            return AniDbSeriesProvider.IsClientUsable() ? (true, null) : (false, "AniDB rejected the client name.");
        }

        if (string.Equals(id, SourceIds.Mal, StringComparison.OrdinalIgnoreCase))
        {
            return MalClient.IsConfigured ? (true, null) : (false, "No MyAnimeList client id is set, and Jikan is off.");
        }

        if (SourceIds.IsDelegated(id))
        {
            return DelegatedProviders.IsInstalled(id) ? (true, null) : (false, id + " is not installed on this server.");
        }

        return (true, null);
    }

    private static async Task<ClientTestResult> TestAniDb(CancellationToken cancellationToken)
    {
        if (!Plugin.Instance.Configuration.AniDbConfigured)
        {
            return new ClientTestResult("unconfigured", "Enter the client name registered on AniDB first.");
        }

        try
        {
            await AniDbSeriesProvider.TestClient(Plugin.Instance.AppPaths, cancellationToken).ConfigureAwait(false);

            return new ClientTestResult("valid", "AniDB accepted the client.");
        }
        catch (AniDbUnavailableException ex) when (ex.ClientRejected)
        {
            return new ClientTestResult("invalid", ex.Message);
        }
        catch (AniDbBannedException ex)
        {
            return new ClientTestResult("banned", ex.Message + " Bans are per IP address: a different client name does not lift one.");
        }
        catch (InvalidOperationException ex)
        {
            return new ClientTestResult("invalid", ex.Message);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            return new ClientTestResult("unreachable", ex.Message);
        }
    }

    private static async Task<ClientTestResult> TestHttp(string source, Func<CancellationToken, Task> test, CancellationToken cancellationToken)
    {
        try
        {
            await test(cancellationToken).ConfigureAwait(false);
            BlockStore.RecordSuccess(source);

            return new ClientTestResult("valid", source + " answered.");
        }
        catch (InvalidOperationException ex)
        {
            return new ClientTestResult("unconfigured", ex.Message);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or KeyNotFoundException or JsonException)
        {
            var failure = BlockStore.Classify(ex);

            return failure.Reason switch
            {
                BlockReason.AuthFailed => new ClientTestResult("invalid", source + " refused the key: " + ex.Message),
                BlockReason.RateLimited => new ClientTestResult("banned", source + " is rate limiting: " + ex.Message),
                _ => new ClientTestResult("unreachable", ex.Message)
            };
        }
    }

    /// <summary>
    /// Writes a short span the way a person says it: "2 min 5 s", "40 s".
    /// </summary>
    /// <param name="span">The span.</param>
    /// <returns>The text.</returns>
    internal static string Duration(TimeSpan span)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(span.TotalSeconds));
        var minutes = seconds / 60;
        seconds %= 60;

        return minutes == 0 ? seconds + " s" : seconds == 0 ? minutes + " min" : minutes + " min " + seconds + " s";
    }

    private static string Mask(string? key)
        => string.IsNullOrEmpty(key) ? string.Empty : key.Length <= 4 ? "****" : new string('*', key.Length - 4) + key[^4..];
}
