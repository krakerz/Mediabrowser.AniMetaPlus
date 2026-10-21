using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Configuration;
using AniMetaPlus.Core;
using AniMetaPlus.Data;
using AniMetaPlus.Host;
using AniMetaPlus.Providers;
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

    /// <summary>Gets or sets the hourly cap in force; zero means none.</summary>
    public int CapPerHour { get; set; }

    /// <summary>Gets or sets the daily cap in force; zero means none.</summary>
    public int CapPerDay { get; set; }

    /// <summary>Gets or sets how many requests go out in one burst; zero means no bursts.</summary>
    public int BurstSize { get; set; }

    /// <summary>Gets or sets how many the current burst has sent.</summary>
    public int BurstSent { get; set; }

    /// <summary>Gets or sets when the rest after the last burst ends, while resting.</summary>
    public DateTime? RestUntilUtc { get; set; }

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

    /// <summary>Gets or sets the default source order of every kind of data.</summary>
    public List<KindPriority> DefaultPriorities { get; set; } = [];

    /// <summary>Gets or sets the version of the plugin the server is running, which an update installed since only replaces after a restart.</summary>
    public string PluginVersion { get; set; } = string.Empty;
}

/// <summary>
/// The result of checking a client name or key.
/// </summary>
/// <param name="Result">valid, invalid, banned, unreachable, throttled or unconfigured.</param>
/// <param name="Message">What was found.</param>
public sealed record ClientTestResult(string Result, string Message);

/// <summary>
/// A stand-in value, with what holds back the source it stands in for.
/// </summary>
public class StandInItem : ProvenanceRecord
{
    /// <summary>Gets or sets why the preferred source has not replaced it, when something is known to hold it back.</summary>
    public string HeldBy { get; set; } = string.Empty;

    /// <summary>Gets or sets when that hold ends, when it does.</summary>
    public DateTime? HeldUntilUtc { get; set; }
}

/// <summary>
/// The answer to mapping an item by hand.
/// </summary>
public class MapResult
{
    /// <summary>Gets or sets the id the item was mapped to, with its source prefix when it is not AniDB.</summary>
    public string AnimeId { get; set; } = string.Empty;
}

/// <summary>
/// One show of the anime libraries with its ids and what was decided about them.
/// </summary>
public class IdRow
{
    /// <summary>Gets or sets the show's folder.</summary>
    public string Path { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the show's name.</summary>
    public string Name { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the ids the show has now, by provider; missing ones are left out.</summary>
    public Dictionary<string, string> Ids { get; set => field = value ?? new(); } = new();

    /// <summary>Gets or sets the status: Corrected, Suspect, Verified, Manual, or empty when nothing was decided.</summary>
    public string Status { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the ids the show had before.</summary>
    public Dictionary<string, string> OldIds { get; set => field = value ?? new(); } = new();

    /// <summary>Gets or sets the ids it has since, or for a suspect the candidates.</summary>
    public Dictionary<string, string> NewIds { get; set => field = value ?? new(); } = new();

    /// <summary>Gets or sets why.</summary>
    public string Reason { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets when it was last decided, if it was.</summary>
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>Gets or sets what the owner mapped the show to by hand, as stored: AniDB digits, "anilist:" or "mal:" and an id; empty when not mapped.</summary>
    public string HandMapping { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the key of the hand mapping, to remove it by; empty when not mapped.</summary>
    public string MappingKey { get; set => field = value ?? string.Empty; } = string.Empty;
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
/// How the genre and tag rebuild from cached answers stands.
/// </summary>
public class GenreRebuildStatus
{
    /// <summary>Gets or sets a value indicating whether a run is going.</summary>
    public bool Running { get; set; }

    /// <summary>Gets or sets a value indicating whether this request started the run, rather than finding one going.</summary>
    public bool Started { get; set; }

    /// <summary>Gets or sets how many items the run takes.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets how many items were looked at.</summary>
    public int Checked { get; set; }

    /// <summary>Gets or sets how many got new genres or tags.</summary>
    public int Updated { get; set; }

    /// <summary>Gets or sets how many already had what the cache gives.</summary>
    public int Unchanged { get; set; }

    /// <summary>Gets or sets how many were left alone because the owner locked them or imports no genres or tags.</summary>
    public int Skipped { get; set; }

    /// <summary>Gets or sets how many have no cached data, or less than they were built from.</summary>
    public int Missing { get; set; }

    /// <summary>Gets or sets how many requests went to MyAnimeList and AniList.</summary>
    public int Requests { get; set; }

    /// <summary>Gets or sets when the run started.</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>Gets or sets when the run ended.</summary>
    public DateTime? FinishedUtc { get; set; }

    /// <summary>Gets or sets the last thing that went wrong, or empty.</summary>
    public string LastError { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Copies the status.</summary>
    /// <returns>The copy.</returns>
    public GenreRebuildStatus Copy() => (GenreRebuildStatus)MemberwiseClone();
}

/// <summary>
/// What the settings page can ask of the plugin, the same on both servers. Each server's own
/// endpoints only pass requests through to this.
/// </summary>
public static class AniMetaApi
{
    private static readonly TimeSpan _testGap = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private static readonly string[] _handIdProviders = [ProviderNames.AniDb, SourceIds.Mal, SourceIds.AniList];

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
            ActiveBlocks = BlockStore.List().Count,
            PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty
        };

        foreach (var kind in Enum.GetValues<DataKind>())
        {
            status.Priorities.Add(new KindPriority { Kind = kind.ToString(), Sources = string.Join(",", configuration.GetPriority(kind)) });
            status.DefaultPriorities.Add(new KindPriority { Kind = kind.ToString(), Sources = string.Join(",", SourceDefaults.Priority(kind)) });
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
                CapPerHour = SourceDefaults.Limits(setting).PerHour,
                CapPerDay = SourceDefaults.Limits(setting).PerDay,
                BurstSize = SourceDefaults.Limits(setting).BurstSize,
                BurstSent = SourceGate.BurstState(id).BurstSent,
                RestUntilUtc = SourceGate.BurstState(id).RestUntilUtc,
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
    public static IReadOnlyList<BlockRecord> GetBlocks(bool includeExpired)
    {
        var blocks = BlockStore.List(includeExpired);

        // A block on one AniDB anime is recorded where only its id is known; the titles dump names it.
        foreach (var block in blocks.Where(b => string.IsNullOrEmpty(b.ItemName) && b.ItemKey.StartsWith("anidb:", StringComparison.Ordinal)))
        {
            block.ItemName = TitlesDump.MainTitle(Plugin.Instance.AppPaths, block.ItemKey["anidb:".Length..]) ?? string.Empty;
        }

        return blocks;
    }

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
    /// Lifts several blocks at once, without refreshing their items: a few hundred refreshes
    /// at once would each ask the sources again, AniDB among them. The items are asked again
    /// at their next refresh.
    /// </summary>
    /// <param name="ids">The block ids.</param>
    /// <returns>How many were lifted.</returns>
    public static int LiftBlocks(IEnumerable<string> ids)
        => (ids ?? []).Distinct(StringComparer.Ordinal).Count(id => BlockStore.Lift(id) != null);

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
            provenance.DecidedSource = review.ProposedSource;
            provenance.Provisional = false;
            provenance.PreferredSource = string.Empty;
            provenance.DeclinedValue = string.Empty;
            provenance.Locked = false;
        }
        else
        {
            provenance.PinnedSource = review.CurrentSource;
            provenance.DecidedSource = review.ProposedSource;
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
    /// those are listed on the Mapping tab until the refresh that applies them clears them.
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
    /// Lists every show of the anime libraries with its ids and what was decided about them.
    /// </summary>
    /// <returns>The shows, by name.</returns>
    public static IReadOnlyList<IdRow> GetIdRecords()
        => JoinIdRows(ListShows(), Db.IdRecords.FindAll().ToDictionary(r => r.Id, StringComparer.Ordinal), Db.Mappings.FindAll());

    /// <summary>
    /// Makes the rows of the shows, each with what was decided about its ids and the hand mapping
    /// made for it. A hand mapping that no listed show matches - a movie, or a show the library
    /// does not list yet - becomes a row of its own without a folder.
    /// </summary>
    /// <param name="shows">The shows of the libraries.</param>
    /// <param name="records">What was decided about their ids, by folder.</param>
    /// <param name="mappings">The hand mappings.</param>
    /// <returns>The rows, by name.</returns>
    internal static IReadOnlyList<IdRow> JoinIdRows(IEnumerable<ScanItem> shows, IReadOnlyDictionary<string, IdRecord> records, IEnumerable<MappingRecord> mappings)
    {
        var byKey = mappings.GroupBy(m => m.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<IdRow>();

        foreach (var show in shows)
        {
            var row = ToRow(show, records.GetValueOrDefault(show.Path));

            if (byKey.TryGetValue(ItemKeys.ForUnidentified("Series", show.Path, show.Name), out var mapping))
            {
                row.HandMapping = mapping.AnimeId;
                row.MappingKey = mapping.Id;
                matched.Add(mapping.Id);
            }

            rows.Add(row);
        }

        foreach (var mapping in byKey.Values.Where(m => !matched.Contains(m.Id)))
        {
            rows.Add(new IdRow
            {
                Name = string.IsNullOrEmpty(mapping.ItemName) ? mapping.Id : mapping.ItemName,
                Ids = HandMappedIds(mapping.AnimeId),
                HandMapping = mapping.AnimeId,
                MappingKey = mapping.Id
            });
        }

        return rows.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // The ids a hand mapping names, by provider: AniDB digits, or an AniList or MyAnimeList link.
    private static Dictionary<string, string> HandMappedIds(string animeId)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);

        if (animeId.StartsWith("anilist:", StringComparison.OrdinalIgnoreCase))
        {
            ids[SourceIds.AniList] = animeId[8..];
        }
        else if (animeId.StartsWith("mal:", StringComparison.OrdinalIgnoreCase))
        {
            ids[SourceIds.Mal] = animeId[4..];
        }
        else if (animeId.Length > 0)
        {
            ids[ProviderNames.AniDb] = animeId;
        }

        return ids;
    }

    /// <summary>
    /// Marks a show's ids as confirmed by the owner. The ids it has now are kept with the mark,
    /// so that the check takes the show up again if they change.
    /// </summary>
    /// <param name="path">The show's folder.</param>
    /// <returns>The show, or <c>null</c> when it is not in the libraries.</returns>
    public static IdRow? VerifyIds(string path)
    {
        if (FindShow(path) is not { } show)
        {
            return null;
        }

        var existing = Db.IdRecords.FindById(path);

        // Ids set by hand stay so until the owner lifts that.
        if (existing is { Status: IdRecord.Manual })
        {
            return ToRow(show, existing);
        }

        var ids = IdCheck.RecordedIds(show.ProviderIds);
        return ToRow(show, Save(show, existing, IdRecord.Verified, existing?.OldIds ?? ids, ids, "Confirmed by the owner"));
    }

    /// <summary>
    /// Puts back the ids a show had before the id check corrected them, and marks them as set by
    /// hand. The show is refreshed with its metadata replaced.
    /// </summary>
    /// <param name="path">The show's folder.</param>
    /// <returns>The show, or <c>null</c> when it is not in the libraries.</returns>
    /// <exception cref="ArgumentException">The show was not corrected.</exception>
    public static async Task<IdRow?> RevertIds(string path)
    {
        if (FindShow(path) is not { } show)
        {
            return null;
        }

        var existing = Db.IdRecords.FindById(path);
        if (existing is not { Status: IdRecord.Corrected })
        {
            throw new ArgumentException("Only a corrected show can be reverted.", nameof(path));
        }

        // The TVDB and TMDB ids go back too: a correction may have changed what they point at.
        var restored = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in _handIdProviders.Concat(["Tvdb", "Tmdb"]))
        {
            restored[provider] = existing.OldIds.GetValueOrDefault(provider) ?? string.Empty;
        }

        return await SetByHand(show, existing, restored, "Reverted by the owner").ConfigureAwait(false);
    }

    /// <summary>
    /// Sets a show's AniDB, MyAnimeList and AniList ids by hand, marks them as the owner's, and
    /// refreshes the show with its metadata replaced.
    /// </summary>
    /// <remarks>
    /// Where the owner gives no AniDB id but a MyAnimeList or AniList one, the AniDB entry the
    /// mappings file under that id is put on the show with them, and the show is filled from
    /// AniDB like any other. Where the mappings file none, the show is filled from MyAnimeList
    /// and AniList alone.
    /// </remarks>
    /// <param name="path">The show's folder.</param>
    /// <param name="aniDb">The AniDB id, digits only, or empty for none.</param>
    /// <param name="mal">The MyAnimeList id, digits only, or empty for none.</param>
    /// <param name="aniList">The AniList id, digits only, or empty for none.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The show, or <c>null</c> when it is not in the libraries.</returns>
    /// <exception cref="ArgumentException">An id is not digits, or no id is given at all.</exception>
    public static async Task<IdRow?> SetIds(string path, string? aniDb, string? mal, string? aniList, ILogger logger)
    {
        var ids = ParseHandIds(aniDb, mal, aniList);

        if (FindShow(path) is not { } show)
        {
            return null;
        }

        // The owner named the show by its MyAnimeList or AniList id, so the AniDB entry the
        // mappings file under that id is put on the show with them.
        var given = ids[ProviderNames.AniDb].Length > 0;
        var animeId = await HandAniDbId(
            ids,
            links => AniDbMappings.ResolveSeriesIdByLink(Plugin.Instance.AppPaths, links, logger, CancellationToken.None)).ConfigureAwait(false);

        if (!given && !string.IsNullOrEmpty(animeId))
        {
            logger.LogInformation(
                "AniMeta+ linked {Show} to AniDB {AnimeId}, which the mappings file under the id the owner gave",
                show.Name,
                animeId);
        }
        else if (!given)
        {
            logger.LogInformation(
                "AniMeta+ left {Show} without an AniDB id: the mappings file no entry under the id the owner gave, so it is filled from MyAnimeList and AniList alone",
                show.Name);
        }

        // The TVDB and TMDB ids the show carries came from whatever identified it before, and
        // Charlotte kept another show's that way. They are taken from the mappings of the AniDB
        // entry given, or cleared where the mappings have none, so the server's own providers
        // and the artwork keyed by them follow the show the owner named.
        // The IMDb id came from the same wrong show; it is cleared for the server's TVDB and TMDB
        // providers to fill from the right one.
        var shown = string.IsNullOrEmpty(ids[ProviderNames.AniDb])
            ? MappedIds.None
            : await AniDbMappings.ResolveShowIds(Plugin.Instance.AppPaths, ids[ProviderNames.AniDb], logger, CancellationToken.None).ConfigureAwait(false);
        ids["Tvdb"] = shown.Tvdb ?? string.Empty;
        ids["Tmdb"] = shown.Tmdb ?? string.Empty;
        ids["Imdb"] = string.Empty;

        return await SetByHand(show, Db.IdRecords.FindById(path), ids, "Set by the owner").ConfigureAwait(false);
    }

    /// <summary>
    /// The AniDB id a show is set to by hand: the one the owner gave, or the one the mappings
    /// file under the MyAnimeList or AniList id the owner gave, where they gave no AniDB id of
    /// their own. The id is put among the ids where the mappings name one, so that the show is
    /// filled from AniDB like any other.
    /// </summary>
    /// <param name="ids">The ids the owner gave, by provider.</param>
    /// <param name="resolve">What the mappings file under a link, as the AniDB id.</param>
    /// <returns>The AniDB id the show is set to, or <c>null</c> where it has none.</returns>
    internal static async Task<string?> HandAniDbId(
        Dictionary<string, string> ids,
        Func<IReadOnlyList<string>, Task<string?>> resolve)
    {
        if (ids[ProviderNames.AniDb].Length > 0)
        {
            return ids[ProviderNames.AniDb];
        }

        var animeId = await resolve(AniDbSeriesProvider.LinksFromIds(ids)).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(animeId))
        {
            ids[ProviderNames.AniDb] = animeId;
        }

        return animeId;
    }

    /// <summary>
    /// Takes the owner's mark off a show: its ids stay, and the id check may correct them again.
    /// </summary>
    /// <param name="path">The show's folder.</param>
    /// <returns>The show, or <c>null</c> when it is not in the libraries.</returns>
    public static IdRow? LiftIds(string path)
    {
        if (FindShow(path) is not { } show)
        {
            return null;
        }

        var existing = Db.IdRecords.FindById(path);
        if (existing is { Status: IdRecord.Manual })
        {
            Db.IdRecords.Delete(path);
            Db.DecisionChanged();
            existing = null;
        }

        return ToRow(show, existing);
    }

    /// <summary>
    /// Reads the ids the owner types in by hand.
    /// </summary>
    /// <param name="aniDb">The AniDB id, digits only, or empty for none.</param>
    /// <param name="mal">The MyAnimeList id, digits only, or empty for none.</param>
    /// <param name="aniList">The AniList id, digits only, or empty for none.</param>
    /// <returns>The ids by provider; an empty one removes that id from the show.</returns>
    /// <exception cref="ArgumentException">An id is not digits, or no id is given at all.</exception>
    public static Dictionary<string, string> ParseHandIds(string? aniDb, string? mal, string? aniList)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProviderNames.AniDb] = (aniDb ?? string.Empty).Trim(),
            [SourceIds.Mal] = (mal ?? string.Empty).Trim(),
            [SourceIds.AniList] = (aniList ?? string.Empty).Trim()
        };

        if (ids[ProviderNames.AniDb].Length == 0 && ids[SourceIds.Mal].Length == 0 && ids[SourceIds.AniList].Length == 0)
        {
            throw new ArgumentException("At least one id is required.", nameof(aniDb));
        }

        if (ids[ProviderNames.AniDb].Length > 0 && !ids[ProviderNames.AniDb].All(char.IsAsciiDigit))
        {
            throw new ArgumentException("The AniDB id is digits only.", nameof(aniDb));
        }

        if (!ids[SourceIds.Mal].All(char.IsAsciiDigit))
        {
            throw new ArgumentException("The MyAnimeList id is digits only.", nameof(mal));
        }

        if (!ids[SourceIds.AniList].All(char.IsAsciiDigit))
        {
            throw new ArgumentException("The AniList id is digits only.", nameof(aniList));
        }

        return ids;
    }

    private static IReadOnlyList<ScanItem> ListShows()
        => (HostServices.Refresher?.ListAnimeItems() ?? []).Where(i => i.Kind == "Series").ToList();

    private static ScanItem? FindShow(string? path)
        => string.IsNullOrEmpty(path) ? null : ListShows().FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.Ordinal));

    private static IdRow ToRow(ScanItem show, IdRecord? record) => new()
    {
        Path = show.Path,
        Name = show.Name,
        Ids = IdCheck.RecordedIds(show.ProviderIds),
        Status = record?.Status ?? string.Empty,
        OldIds = record?.OldIds ?? new(),
        NewIds = record?.NewIds ?? new(),
        Reason = record?.Reason ?? string.Empty,
        UpdatedUtc = record?.UpdatedUtc
    };

    private static IdRecord Save(ScanItem show, IdRecord? existing, string status, Dictionary<string, string> oldIds, Dictionary<string, string> newIds, string reason)
    {
        var now = DateTime.UtcNow;
        var record = new IdRecord
        {
            Id = show.Path,
            ItemName = show.Name,
            Status = status,
            OldIds = oldIds,
            NewIds = newIds,
            Reason = reason,
            CreatedUtc = existing?.CreatedUtc ?? now,
            UpdatedUtc = now
        };

        Db.IdRecords.Upsert(record);
        Db.DecisionChanged();

        return record;
    }

    // Writes the ids onto the show, marks them the owner's and refreshes the show, which
    // replaces its metadata since the ids behind it changed.
    private static async Task<IdRow?> SetByHand(ScanItem show, IdRecord? existing, Dictionary<string, string> ids, string reason)
    {
        var refresher = HostServices.Refresher;
        if (refresher == null || !await refresher.SaveIds(show.Path, ids, CancellationToken.None).ConfigureAwait(false))
        {
            return null;
        }

        var now = ids.Where(i => i.Value.Length > 0).ToDictionary(i => i.Key, i => i.Value, StringComparer.Ordinal);
        var record = Save(show, existing, IdRecord.Manual, existing is { Status: IdRecord.Manual } ? existing.OldIds : IdCheck.RecordedIds(show.ProviderIds), now, reason);

        // Images too: the poster and backdrop were found under the ids the show had.
        refresher.Refresh(show.Path, true, true);

        var shown = IdCheck.RecordedIds(show.ProviderIds);
        foreach (var (provider, id) in ids)
        {
            if (id.Length > 0)
            {
                shown[provider] = id;
            }
            else
            {
                shown.Remove(provider);
            }
        }

        var row = ToRow(show, record);
        row.Ids = shown;
        return row;
    }

    /// <summary>
    /// Gets how the genre and tag rebuild stands.
    /// </summary>
    /// <returns>The status of the current or last run.</returns>
    public static GenreRebuildStatus GetGenreRebuild() => GenreRebuild.Status;

    /// <summary>
    /// Starts rebuilding every anime's genres and tags from the answers already cached, unless a
    /// run is going. Asks AniDB for nothing.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <returns>The run's status.</returns>
    public static GenreRebuildStatus StartGenreRebuild(ILogger logger)
        => HostServices.Refresher is HostLibrary library ? GenreRebuild.Start(library, logger) : GenreRebuild.Status;

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
    public static IReadOnlyList<StandInItem> GetProvisional()
    {
        var items = new List<StandInItem>();

        foreach (var p in Db.Provenance.FindAll().Where(p => p.Provisional).OrderByDescending(p => p.UpdatedUtc))
        {
            var item = new StandInItem
            {
                Id = p.Id,
                ItemKey = p.ItemKey,
                Field = p.Field,
                Source = p.Source,
                PreferredSource = p.PreferredSource,
                Provisional = p.Provisional,
                PinnedSource = p.PinnedSource,
                Locked = p.Locked,
                DeclinedValue = p.DeclinedValue,
                ItemPath = p.ItemPath,
                ItemName = p.ItemName,
                UpdatedUtc = p.UpdatedUtc
            };

            var block = string.IsNullOrEmpty(p.PreferredSource)
                ? null
                : BlockStore.SourceBlock(p.PreferredSource) ?? BlockStore.ItemBlock(p.PreferredSource, p.ItemKey);
            if (block != null)
            {
                item.HeldBy = block.Reason + (string.IsNullOrEmpty(block.Message) ? string.Empty : ": " + block.Message);
                item.HeldUntilUtc = block.ExpiresUtc;
            }
            else if (p.PreferredSource == SourceIds.AniDb && !AniDbSeriesProvider.IsClientUsable())
            {
                item.HeldBy = "AniDB cannot be asked (no usable client name, or turned off)";
            }

            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// Keeps stand-in values: they stop waiting for the preferred source and stay pinned to the
    /// one that supplied them. Should the preferred source answer for the field later, its value
    /// is offered for review rather than taken.
    /// </summary>
    /// <param name="ids">The provenance ids; ignored when <paramref name="all"/> is set.</param>
    /// <param name="all">Whether to keep every stand-in.</param>
    /// <returns>How many were kept.</returns>
    public static ImageDoneResult KeepStandIns(IEnumerable<string>? ids, bool all)
    {
        var db = Db;
        var wanted = all ? null : new HashSet<string>(ids ?? [], StringComparer.Ordinal);
        var done = 0;

        foreach (var p in db.Provenance.FindAll().Where(p => p.Provisional && (wanted == null || wanted.Contains(p.Id))).ToList())
        {
            p.Provisional = false;
            p.PinnedSource = p.Source;
            p.DecidedSource = p.PreferredSource;
            p.PreferredSource = string.Empty;
            p.UpdatedUtc = DateTime.UtcNow;
            db.Provenance.Upsert(p);
            done++;
        }

        if (done > 0)
        {
            db.DecisionChanged();
        }

        return new ImageDoneResult { Done = done };
    }

    /// <summary>
    /// Refreshes the item of a stand-in now, whatever the queue's pacing says.
    /// </summary>
    /// <param name="id">The provenance id.</param>
    /// <returns>Whether a refresh was queued.</returns>
    public static ImageDoneResult RetryStandIn(string id)
    {
        var p = Db.Provenance.FindById(id);

        return new ImageDoneResult { Done = p != null && HostServices.Refresher?.Refresh(p.ItemPath, false) == true ? 1 : 0 };
    }

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
    /// Merges exported decisions in, and puts the ids the owner set by hand onto the shows in the
    /// library that carry others.
    /// </summary>
    /// <param name="json">The JSON an export wrote.</param>
    /// <returns>How many records were taken.</returns>
    public static async Task<int> Import(string json)
    {
        var decisions = JsonSerializer.Deserialize<DecisionsExport>(json, AniMetaDb.ReadOptions) ?? throw new ArgumentException("Not an AniMeta+ export.", nameof(json));
        var taken = Db.Import(decisions);

        await ApplyImportedIds(decisions.IdRecords ?? []).ConfigureAwait(false);

        return taken;
    }

    /// <summary>
    /// Writes the ids of imported hand-set records onto their shows where the shows carry
    /// others, and refreshes those shows with their metadata replaced. The records themselves
    /// are already stored.
    /// </summary>
    /// <param name="records">The imported records.</param>
    /// <returns>How many shows were changed.</returns>
    internal static async Task<int> ApplyImportedIds(IEnumerable<IdRecord> records)
    {
        var refresher = HostServices.Refresher;
        if (refresher == null)
        {
            return 0;
        }

        var shows = ListShows().ToDictionary(s => s.Path, StringComparer.Ordinal);
        var applied = 0;

        foreach (var record in records.Where(r => r.Status == IdRecord.Manual))
        {
            if (!shows.TryGetValue(record.Id, out var show))
            {
                continue;
            }

            var wanted = IdCheck.RecordedProviders.ToDictionary(p => p, p => record.NewIds.GetValueOrDefault(p) ?? string.Empty, StringComparer.Ordinal);
            var current = IdCheck.RecordedIds(show.ProviderIds);

            if (wanted.All(w => string.Equals(w.Value, current.GetValueOrDefault(w.Key) ?? string.Empty, StringComparison.Ordinal)))
            {
                continue;
            }

            if (await refresher.SaveIds(show.Path, wanted, CancellationToken.None).ConfigureAwait(false))
            {
                // Images too, as when the owner sets the ids: they were found under the old ids.
                refresher.Refresh(show.Path, true, true);
                applied++;
            }
        }

        return applied;
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
