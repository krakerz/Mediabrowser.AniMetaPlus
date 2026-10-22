using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using AniMetaPlus.Data;
using AniMetaPlus.Logging;
using AniMetaPlus.Providers.AniDB.Metadata;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Api;

#pragma warning disable SA1402 // Emby routes are one small request class each, kept together.

/// <summary>Gets everything the settings page shows at a glance.</summary>
[Route("/AniMetaPlus/Overview", "GET")]
public class GetOverview : IReturn<OverviewStatus>
{
}

/// <summary>Gets the newest AniMeta+ release published on GitHub.</summary>
[Route("/AniMetaPlus/LatestRelease", "GET")]
public class GetLatestRelease : IReturn<LatestRelease>
{
}

/// <summary>Gets AniDB's queue and ban, in the original status page's shape.</summary>
[Route("/AniMetaPlus/AniDbStatus", "GET")]
public class GetAniDbStatus : IReturn<AniDbStatus>
{
}

/// <summary>Checks the downloaded mapping sources for newer copies.</summary>
[Route("/AniMetaPlus/MappingSources/Check", "POST")]
public class CheckMappingSources : IReturn<AniDbSourceCheck>
{
}

/// <summary>Lists blocks.</summary>
[Route("/AniMetaPlus/Blocks", "GET")]
public class GetBlocks : IReturn<BlockRecord[]>
{
    /// <summary>Gets or sets a value indicating whether to include lapsed blocks.</summary>
    public bool IncludeExpired { get; set; }
}

/// <summary>Lifts a block.</summary>
[Route("/AniMetaPlus/Blocks/Lift", "POST")]
public class LiftBlock : IReturn<BlockRecord>
{
    /// <summary>Gets or sets the block id.</summary>
    public string Id { get; set; } = string.Empty;
}

/// <summary>Lifts several blocks without refreshing their items.</summary>
[Route("/AniMetaPlus/Blocks/LiftMany", "POST")]
public class LiftBlocks : IReturn<int>
{
    /// <summary>Gets or sets the block ids.</summary>
    public List<string> Ids { get; set; } = [];
}

/// <summary>Pins a block.</summary>
[Route("/AniMetaPlus/Blocks/Pin", "POST")]
public class PinBlock : IReturn<BlockRecord>
{
    /// <summary>Gets or sets the block id.</summary>
    public string Id { get; set; } = string.Empty;
}

/// <summary>Blocks a source by hand.</summary>
[Route("/AniMetaPlus/Blocks", "POST")]
public class AddBlock : IReturn<BlockRecord>
{
    /// <summary>Gets or sets the source id.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the item key, or empty for the whole source.</summary>
    public string ItemKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's name.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of data, or empty for every kind.</summary>
    public string Kind { get; set; } = string.Empty;
}

/// <summary>Lists reviews.</summary>
[Route("/AniMetaPlus/Reviews", "GET")]
public class GetReviews : IReturn<ReviewRecord[]>
{
    /// <summary>Gets or sets a value indicating whether to list only the undecided.</summary>
    public bool PendingOnly { get; set; } = true;
}

/// <summary>Decides a review.</summary>
[Route("/AniMetaPlus/Reviews/Decide", "POST")]
public class DecideReview : IReturn<ReviewRecord>
{
    /// <summary>Gets or sets the review id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the decision: Accepted, Kept or Locked.</summary>
    public ReviewStatus Decision { get; set; }
}

/// <summary>Decides several reviews without refreshing their items.</summary>
[Route("/AniMetaPlus/Reviews/DecideMany", "POST")]
public class DecideReviews : IReturn<int>
{
    /// <summary>Gets or sets the review ids.</summary>
    public List<string> Ids { get; set; } = [];

    /// <summary>Gets or sets the decision: Accepted, Kept or Locked.</summary>
    public ReviewStatus Decision { get; set; }
}

/// <summary>Lists the items nothing identified.</summary>
[Route("/AniMetaPlus/Unmatched", "GET")]
public class GetUnmatched : IReturn<UnmatchedRecord[]>
{
    /// <summary>Gets or sets a value indicating whether to include those set aside.</summary>
    public bool IncludeIgnored { get; set; }
}

/// <summary>Maps an unmatched item by hand.</summary>
[Route("/AniMetaPlus/Unmatched/Map", "POST")]
public class MapUnmatched : IReturn<MapResult>
{
    /// <summary>Gets or sets the item key.</summary>
    public string ItemKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the AniDB id or a link to the anime on AniDB.</summary>
    public string AnimeId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether to refresh the item now.</summary>
    public bool ApplyNow { get; set; }
}

/// <summary>Sets an unmatched item aside, or brings it back.</summary>
[Route("/AniMetaPlus/Unmatched/Ignore", "POST")]
public class IgnoreUnmatched : IReturn<UnmatchedRecord>
{
    /// <summary>Gets or sets the item key.</summary>
    public string ItemKey { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether to set it aside.</summary>
    public bool Ignore { get; set; } = true;
}

/// <summary>Lists every show of the anime libraries with its ids and what was decided about them.</summary>
[Route("/AniMetaPlus/IdRecords", "GET")]
public class GetIdRecords : IReturn<IdRow[]>
{
}

/// <summary>Marks a show's ids as confirmed.</summary>
[Route("/AniMetaPlus/IdRecords/Verify", "POST")]
public class VerifyIdRecord : IReturn<IdRow>
{
    /// <summary>Gets or sets the show's folder.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>Puts back the ids a show had before the id check corrected them.</summary>
[Route("/AniMetaPlus/IdRecords/Revert", "POST")]
public class RevertIdRecord : IReturn<IdRow>
{
    /// <summary>Gets or sets the show's folder.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>Sets a show's ids by hand.</summary>
[Route("/AniMetaPlus/IdRecords/Manual", "POST")]
public class SetIdRecord : IReturn<IdRow>
{
    /// <summary>Gets or sets the show's folder.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the AniDB id, or empty for none.</summary>
    public string AniDb { get; set; } = string.Empty;

    /// <summary>Gets or sets the MyAnimeList id, or empty for none.</summary>
    public string Mal { get; set; } = string.Empty;

    /// <summary>Gets or sets the AniList id, or empty for none.</summary>
    public string AniList { get; set; } = string.Empty;
}

/// <summary>Takes the owner's mark off a show.</summary>
[Route("/AniMetaPlus/IdRecords/Lift", "POST")]
public class LiftIdRecord : IReturn<IdRow>
{
    /// <summary>Gets or sets the show's folder.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>Lists the mappings made by hand.</summary>
[Route("/AniMetaPlus/Mappings", "GET")]
public class GetMappings : IReturn<MappingRecord[]>
{
}

/// <summary>Removes a mapping made by hand.</summary>
[Route("/AniMetaPlus/Mappings", "DELETE")]
public class DeleteMapping : IReturnVoid
{
    /// <summary>Gets or sets the item key.</summary>
    public string ItemKey { get; set; } = string.Empty;
}

/// <summary>Searches the local titles dump.</summary>
[Route("/AniMetaPlus/Titles/Search", "GET")]
public class SearchTitles : IReturn<CandidateRecord[]>
{
    /// <summary>Gets or sets the title.</summary>
    public string Query { get; set; } = string.Empty;
}

/// <summary>Searches AniList and MyAnimeList by title.</summary>
[Route("/AniMetaPlus/Titles/SearchOnline", "GET")]
public class SearchOnline : IReturn<Sources.RemoteCandidate[]>
{
    /// <summary>Gets or sets the title.</summary>
    public string Query { get; set; } = string.Empty;
}

/// <summary>Downloads a fresh titles dump, within AniDB's once-a-day allowance.</summary>
[Route("/AniMetaPlus/Titles/Refresh", "POST")]
public class RefreshTitles : IReturn<Titles.TitlesRefreshResult>
{
}

/// <summary>Imports the titles dump on disk again.</summary>
[Route("/AniMetaPlus/Titles/Reimport", "POST")]
public class ReimportTitles : IReturn<Titles.TitlesDumpStatus>
{
}

/// <summary>Lists the items waiting for a paused source.</summary>
[Route("/AniMetaPlus/Waiting", "GET")]
public class GetWaiting : IReturn<WaitingRecord[]>
{
}

/// <summary>Gets which source supplied each field of an item.</summary>
[Route("/AniMetaPlus/Provenance", "GET")]
public class GetProvenance : IReturn<ProvenanceRecord[]>
{
    /// <summary>Gets or sets the item key.</summary>
    public string ItemKey { get; set; } = string.Empty;
}

/// <summary>Keeps stand-in values, so they stop waiting for the preferred source.</summary>
[Route("/AniMetaPlus/Provisional/Keep", "POST")]
public class KeepStandIns : IReturn<ImageDoneResult>
{
    /// <summary>Gets or sets the provenance ids.</summary>
    public List<string> Ids { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether to keep every stand-in.</summary>
    public bool All { get; set; }
}

/// <summary>Refreshes a stand-in's item now.</summary>
[Route("/AniMetaPlus/Provisional/Retry", "POST")]
public class RetryStandIn : IReturn<ImageDoneResult>
{
    /// <summary>Gets or sets the provenance id.</summary>
    public string Id { get; set; } = string.Empty;
}

/// <summary>Lists the fields a stand-in supplied.</summary>
[Route("/AniMetaPlus/Provisional", "GET")]
public class GetProvisional : IReturn<StandInItem[]>
{
}

/// <summary>Lists the most recent requests.</summary>
[Route("/AniMetaPlus/Requests", "GET")]
public class GetRequests : IReturn<RequestLogRecord[]>
{
    /// <summary>Gets or sets how many.</summary>
    public int Count { get; set; } = 100;
}

/// <summary>Describes what each source has cached.</summary>
[Route("/AniMetaPlus/Cache", "GET")]
public class GetCache : IReturn<CacheSourceStatus[]>
{
}

/// <summary>Marks every cached answer of a source as expired.</summary>
[Route("/AniMetaPlus/Cache/Expire", "POST")]
public class ExpireCache : IReturn<CacheExpireResult>
{
    /// <summary>Gets or sets the source id.</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>Checks a source's client name or key.</summary>
[Route("/AniMetaPlus/Clients/Test", "POST")]
public class TestClient : IReturn<ClientTestResult>
{
    /// <summary>Gets or sets the source id.</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>Runs the queues now.</summary>
[Route("/AniMetaPlus/Queues/Run", "POST")]
public class RunQueues : IReturn<Core.QueueRunResult>
{
}

/// <summary>Gets the owner's decisions. Holds no keys.</summary>
[Route("/AniMetaPlus/Export", "GET")]
public class ExportDecisions : IReturn<DecisionsExport>
{
}

/// <summary>Merges an export in; the body is the export's JSON.</summary>
[Route("/AniMetaPlus/Import", "POST")]
public class ImportDecisions : IRequiresRequestStream, IReturn<int>
{
    /// <summary>Gets or sets the body.</summary>
    public Stream RequestStream { get; set; } = Stream.Null;
}

/// <summary>Lists the replaced posters waiting for a decision, newest first.</summary>
[Route("/AniMetaPlus/Images/Backups", "GET")]
public class GetImageBackups : IReturn<ImageBackupPage>
{
    /// <summary>Gets or sets the first item to return.</summary>
    public int Start { get; set; }

    /// <summary>Gets or sets how many to return.</summary>
    public int Limit { get; set; } = 24;
}

/// <summary>Gets the poster held for an item.</summary>
[Route("/AniMetaPlus/Images/Backup", "GET")]
public class GetImageBackup : IReturn<ImageBackupPreview>
{
    /// <summary>Gets or sets the item id.</summary>
    public string Id { get; set; } = string.Empty;
}

/// <summary>Reports how the current or last revert run stands.</summary>
[Route("/AniMetaPlus/Images/RevertStatus", "GET")]
public class GetImageRevertStatus : IReturn<ImageJobStatus>
{
}

/// <summary>Starts putting the previous posters back in the background.</summary>
[Route("/AniMetaPlus/Images/Revert", "POST")]
public class RevertImages : IReturn<ImageJobStatus>
{
    /// <summary>Gets or sets the item ids.</summary>
    public List<string> Ids { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether to take every item waiting.</summary>
    public bool All { get; set; }
}

/// <summary>Reports how the genre and tag rebuild stands.</summary>
[Route("/AniMetaPlus/GenreRebuild", "GET")]
public class GetGenreRebuild : IReturn<GenreRebuildStatus>
{
}

/// <summary>Starts rebuilding genres and tags from the cached answers in the background.</summary>
[Route("/AniMetaPlus/GenreRebuild", "POST")]
public class StartGenreRebuild : IReturn<GenreRebuildStatus>
{
}

/// <summary>Accepts AniDB's posters and deletes the previous ones.</summary>
[Route("/AniMetaPlus/Images/Acknowledge", "POST")]
public class AcknowledgeImages : IReturn<ImageDoneResult>
{
    /// <summary>Gets or sets the item ids.</summary>
    public List<string> Ids { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether to take every item waiting.</summary>
    public bool All { get; set; }
}

/// <summary>Forgets the reverted items, so that the next task run replaces their posters again.</summary>
[Route("/AniMetaPlus/Images/ForgetReverted", "POST")]
public class ForgetRevertedImages : IReturn<ImageDoneResult>
{
}

/// <summary>Forgets the posters kept for items the library no longer has.</summary>
[Route("/AniMetaPlus/Images/ForgetMissing", "POST")]
public class ForgetMissingImages : IReturn<int>
{
}

/// <summary>Gets an identify result's poster with its label band.</summary>
[Route("/AniMetaPlus/Identify/Poster", "GET")]
[Unauthenticated]
public class GetIdentifyPoster : IReturn<byte[]>
{
    /// <summary>Gets or sets the label, as the address carried it.</summary>
    public string? L { get; set; }

    /// <summary>Gets or sets the signature the address carried.</summary>
    public string? Sig { get; set; }

    /// <summary>Gets or sets the flag the server's own image proxy's address carries.</summary>
    public string? T { get; set; }
}

#pragma warning restore SA1402

/// <summary>
/// The settings page's endpoints on Emby. Each passes straight through to
/// <see cref="AniMetaApi"/>, which Jellyfin's endpoints use too.
/// </summary>
[Authenticated(Roles = "Admin")]
public class AniMetaPlusService : IService
{
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AniMetaPlusService"/> class.
    /// </summary>
    /// <param name="logManager">The server's log manager.</param>
    public AniMetaPlusService(ILogManager logManager)
    {
        _logger = new PluginLog<AniMetaPlusService>(logManager);
    }

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public async Task<object> Get(GetOverview request) => await AniMetaApi.GetOverview(_logger, CancellationToken.None).ConfigureAwait(false);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public async Task<object> Get(GetLatestRelease request) => await ReleaseCheck.Get(_logger, CancellationToken.None).ConfigureAwait(false);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public async Task<object> Get(GetAniDbStatus request) => await AniMetaApi.GetAniDbStatus(_logger, CancellationToken.None).ConfigureAwait(false);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public async Task<object> Post(CheckMappingSources request) => await AniMetaApi.CheckMappingSources(_logger, CancellationToken.None).ConfigureAwait(false);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Get(GetBlocks request) => AniMetaApi.GetBlocks(request.IncludeExpired);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object? Post(LiftBlock request) => AniMetaApi.LiftBlock(request.Id);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Post(LiftBlocks request) => new { Lifted = AniMetaApi.LiftBlocks(request.Ids) };

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object? Post(PinBlock request) => AniMetaApi.PinBlock(request.Id);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object? Post(AddBlock request) => AniMetaApi.AddBlock(request.Source, request.ItemKey, request.ItemName, request.Kind);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Get(GetReviews request) => AniMetaApi.GetReviews(request.PendingOnly);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object? Post(DecideReview request) => AniMetaApi.DecideReview(request.Id, request.Decision);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Post(DecideReviews request) => new { Decided = AniMetaApi.DecideReviews(request.Ids, request.Decision) };

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Get(GetUnmatched request) => AniMetaApi.GetUnmatched(request.IncludeIgnored);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Post(MapUnmatched request) => new MapResult { AnimeId = AniMetaApi.Map(request.ItemKey, request.AnimeId, request.ApplyNow) };

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object? Post(IgnoreUnmatched request) => AniMetaApi.Ignore(request.ItemKey, request.Ignore);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Get(GetMappings request) => AniMetaApi.GetMappings();

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Get(GetIdRecords request) => AniMetaApi.GetIdRecords();

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Post(VerifyIdRecord request) => AniMetaApi.VerifyIds(request.Path) ?? throw ShowNotFound(request.Path);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public async Task<object> Post(RevertIdRecord request) => await AniMetaApi.RevertIds(request.Path).ConfigureAwait(false) ?? throw ShowNotFound(request.Path);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public async Task<object> Post(SetIdRecord request) => await AniMetaApi.SetIds(request.Path, request.AniDb, request.Mal, request.AniList, _logger).ConfigureAwait(false) ?? throw ShowNotFound(request.Path);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Post(LiftIdRecord request) => AniMetaApi.LiftIds(request.Path) ?? throw ShowNotFound(request.Path);

    private static Exception ShowNotFound(string path)
        => new MediaBrowser.Common.Extensions.ResourceNotFoundException("No show at " + path);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    public void Delete(DeleteMapping request) => AniMetaApi.Unmap(request.ItemKey);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public async Task<object> Get(SearchTitles request) => await AniMetaApi.SearchTitles(request.Query, CancellationToken.None).ConfigureAwait(false);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public async Task<object> Get(SearchOnline request) => await AniMetaApi.SearchOnline(request.Query, _logger, CancellationToken.None).ConfigureAwait(false);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public async Task<object> Post(RefreshTitles request) => await AniMetaApi.RefreshTitles(_logger, CancellationToken.None).ConfigureAwait(false);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public async Task<object> Post(ReimportTitles request) => await AniMetaApi.ReimportTitles(_logger, CancellationToken.None).ConfigureAwait(false);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Get(GetImageBackups request) => AniMetaApi.GetImageBackups(request.Start, request.Limit);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Get(GetImageBackup request) => new ImageBackupPreview { DataUrl = AniMetaApi.GetImageBackup(request.Id) ?? string.Empty };

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Post(RevertImages request) => AniMetaApi.RevertImages(request.Ids, request.All, _logger);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Get(GetImageRevertStatus request) => AniMetaApi.GetRevertStatus();

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Get(GetGenreRebuild request) => AniMetaApi.GetGenreRebuild();

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Post(StartGenreRebuild request) => AniMetaApi.StartGenreRebuild(_logger);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Post(AcknowledgeImages request) => AniMetaApi.AcknowledgeImages(request.Ids, request.All);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Post(ForgetRevertedImages request) => AniMetaApi.ForgetRevertedImages();

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Post(ForgetMissingImages request) => new { Forgotten = AniMetaApi.ForgetMissingImages() };

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Get(GetWaiting request) => AniMetaApi.GetWaiting();

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Get(GetProvenance request) => AniMetaApi.GetProvenance(request.ItemKey);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Get(GetProvisional request) => AniMetaApi.GetProvisional();

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Post(KeepStandIns request) => AniMetaApi.KeepStandIns(request.Ids, request.All);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Post(RetryStandIn request) => AniMetaApi.RetryStandIn(request.Id);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Get(GetRequests request) => AniMetaApi.GetRequests(request.Count);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Get(GetCache request) => AnswerCache.Status();

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Post(ExpireCache request) => AnswerCache.Expire(request.Source);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public async Task<object> Post(TestClient request) => await AniMetaApi.TestClient(request.Source, CancellationToken.None).ConfigureAwait(false);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public async Task<object> Post(RunQueues request) => await AniMetaApi.RunQueues(_logger, CancellationToken.None).ConfigureAwait(false);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Get(ExportDecisions request) => AniMetaDb.Current.Export();

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public async Task<object> Post(ImportDecisions request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var reader = new StreamReader(request.RequestStream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync().ConfigureAwait(false);

        return await AniMetaApi.Import(json).ConfigureAwait(false);
    }
}

/// <summary>
/// The identify poster, apart from the settings endpoints because it is open to anyone: the browser
/// loads it for the result list on older web clients, and the server fetches it from itself, through
/// its image proxy, on 4.10.2 and later; neither sends a login. The signature keeps it to labels
/// this server's own identify made, and the poster is only ever fetched from a source's image host.
/// </summary>
public class AniMetaPlusPosterService : IService, IRequiresRequest
{
    private readonly IHttpResultFactory _results;

    /// <summary>
    /// Initializes a new instance of the <see cref="AniMetaPlusPosterService"/> class.
    /// </summary>
    /// <param name="results">The server's result factory.</param>
    public AniMetaPlusPosterService(IHttpResultFactory results)
    {
        _results = results;
    }

    /// <summary>Gets or sets the request, which the server sets before it calls the handler.</summary>
    public IRequest Request { get; set; } = null!;

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The picture as svg.</returns>
    public async Task<object> Get(GetIdentifyPoster request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var svg = await IdentifyLabel.GetSignedImage(request.L, request.Sig, Plugin.Instance.AppPaths, CancellationToken.None).ConfigureAwait(false);

        if (svg is null)
        {
            throw new MediaBrowser.Common.Extensions.ResourceNotFoundException("No such poster");
        }

        // The server's image proxy (flagged) serves what it saved by the type's ending; a browser needs the real type.
        var type = string.IsNullOrEmpty(request.T) ? IdentifyLabel.SvgContentType : IdentifyLabel.EmbyContentType;

        return _results.GetResult(Request, svg, type, new Dictionary<string, string> { ["Cache-Control"] = "public, max-age=86400" });
    }
}
