using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Data;
using AniMetaPlus.Logging;
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

/// <summary>Lists the items nothing identified.</summary>
[Route("/AniMetaPlus/Unmatched", "GET")]
public class GetUnmatched : IReturn<UnmatchedRecord[]>
{
    /// <summary>Gets or sets a value indicating whether to include those set aside.</summary>
    public bool IncludeIgnored { get; set; }
}

/// <summary>Maps an unmatched item by hand.</summary>
[Route("/AniMetaPlus/Unmatched/Map", "POST")]
public class MapUnmatched : IReturn<string>
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

/// <summary>Lists the fields a stand-in supplied.</summary>
[Route("/AniMetaPlus/Provisional", "GET")]
public class GetProvisional : IReturn<ProvenanceRecord[]>
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
    public object Get(GetUnmatched request) => AniMetaApi.GetUnmatched(request.IncludeIgnored);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public object Post(MapUnmatched request) => AniMetaApi.Map(request.ItemKey, request.AnimeId, request.ApplyNow);

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
    public void Delete(DeleteMapping request) => AniMetaApi.Unmap(request.ItemKey);

    /// <summary>Handles the request.</summary>
    /// <param name="request">The request.</param>
    /// <returns>The response.</returns>
    public async Task<object> Get(SearchTitles request) => await AniMetaApi.SearchTitles(request.Query, CancellationToken.None).ConfigureAwait(false);

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

        return AniMetaApi.Import(json);
    }
}
