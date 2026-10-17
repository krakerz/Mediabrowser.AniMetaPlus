using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using AniMetaPlus.Data;
using AniMetaPlus.Titles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Api;

/// <summary>
/// A block to set by hand.
/// </summary>
public class BlockRequest
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

/// <summary>
/// A mapping to make by hand.
/// </summary>
public class MapRequest
{
    /// <summary>Gets or sets the item key.</summary>
    public string ItemKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the AniDB id or a link to the anime on AniDB.</summary>
    public string AnimeId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether to refresh the item now.</summary>
    public bool ApplyNow { get; set; }
}

/// <summary>
/// The replaced posters to revert or acknowledge.
/// </summary>
public class ImageSelectionRequest
{
    /// <summary>Gets or sets the item ids.</summary>
    public List<string> Ids { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether to take every item waiting.</summary>
    public bool All { get; set; }
}

/// <summary>
/// The settings page's endpoints on Jellyfin. Each passes straight through to
/// <see cref="AniMetaApi"/>, which Emby's endpoints use too.
/// </summary>
/// <param name="logger">The logger.</param>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("AniMetaPlus")]
[Produces(MediaTypeNames.Application.Json)]
public class AniMetaPlusController(ILogger<AniMetaPlusController> logger) : ControllerBase
{
    /// <summary>Gets everything the settings page shows at a glance.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status.</returns>
    [HttpGet("Overview")]
    public Task<OverviewStatus> GetOverview(CancellationToken cancellationToken) => AniMetaApi.GetOverview(logger, cancellationToken);

    /// <summary>Lists blocks.</summary>
    /// <param name="includeExpired">Whether to include lapsed blocks.</param>
    /// <returns>The blocks.</returns>
    [HttpGet("Blocks")]
    public IReadOnlyList<BlockRecord> GetBlocks([FromQuery] bool includeExpired = false) => AniMetaApi.GetBlocks(includeExpired);

    /// <summary>Lifts a block.</summary>
    /// <param name="id">The block id.</param>
    /// <returns>The lifted block.</returns>
    [HttpPost("Blocks/Lift")]
    public ActionResult<BlockRecord> LiftBlock([FromQuery] string id) => AniMetaApi.LiftBlock(id) is { } block ? block : NotFound();

    /// <summary>Pins a block.</summary>
    /// <param name="id">The block id.</param>
    /// <returns>The pinned block.</returns>
    [HttpPost("Blocks/Pin")]
    public ActionResult<BlockRecord> PinBlock([FromQuery] string id) => AniMetaApi.PinBlock(id) is { } block ? block : NotFound();

    /// <summary>Blocks a source by hand.</summary>
    /// <param name="request">The block.</param>
    /// <returns>The block.</returns>
    [HttpPost("Blocks")]
    public ActionResult<BlockRecord> AddBlock([FromBody] BlockRequest request)
        => AniMetaApi.AddBlock(request.Source, request.ItemKey, request.ItemName, request.Kind) is { } block ? block : Problem("The database is not available.");

    /// <summary>Lists reviews.</summary>
    /// <param name="pendingOnly">Whether to list only the undecided.</param>
    /// <returns>The reviews.</returns>
    [HttpGet("Reviews")]
    public IReadOnlyList<ReviewRecord> GetReviews([FromQuery] bool pendingOnly = true) => AniMetaApi.GetReviews(pendingOnly);

    /// <summary>Decides a review.</summary>
    /// <param name="id">The review id.</param>
    /// <param name="decision">Accepted, Kept or Locked.</param>
    /// <returns>The review.</returns>
    [HttpPost("Reviews/Decide")]
    public ActionResult<ReviewRecord> DecideReview([FromQuery] string id, [FromQuery] ReviewStatus decision)
        => AniMetaApi.DecideReview(id, decision) is { } review ? review : NotFound();

    /// <summary>Lists the items nothing identified.</summary>
    /// <param name="includeIgnored">Whether to include those set aside.</param>
    /// <returns>The items.</returns>
    [HttpGet("Unmatched")]
    public IReadOnlyList<UnmatchedRecord> GetUnmatched([FromQuery] bool includeIgnored = false) => AniMetaApi.GetUnmatched(includeIgnored);

    /// <summary>Maps an unmatched item by hand.</summary>
    /// <param name="request">The mapping.</param>
    /// <returns>The id mapped to.</returns>
    [HttpPost("Unmatched/Map")]
    public ActionResult<MapResult> Map([FromBody] MapRequest request)
    {
        try
        {
            return new MapResult { AnimeId = AniMetaApi.Map(request.ItemKey, request.AnimeId, request.ApplyNow) };
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Sets an unmatched item aside, or brings it back.</summary>
    /// <param name="itemKey">The item key.</param>
    /// <param name="ignore">Whether to set it aside.</param>
    /// <returns>The item.</returns>
    [HttpPost("Unmatched/Ignore")]
    public ActionResult<UnmatchedRecord> Ignore([FromQuery] string itemKey, [FromQuery] bool ignore = true)
        => AniMetaApi.Ignore(itemKey, ignore) is { } record ? record : NotFound();

    /// <summary>Lists the mappings made by hand.</summary>
    /// <returns>The mappings.</returns>
    [HttpGet("Mappings")]
    public IReadOnlyList<MappingRecord> GetMappings() => AniMetaApi.GetMappings();

    /// <summary>Removes a mapping made by hand.</summary>
    /// <param name="itemKey">The item key.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Mappings")]
    public ActionResult Unmap([FromQuery] string itemKey) => AniMetaApi.Unmap(itemKey) ? NoContent() : NotFound();

    /// <summary>Searches the local titles dump.</summary>
    /// <param name="query">The title.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The closest anime.</returns>
    [HttpGet("Titles/Search")]
    public Task<List<CandidateRecord>> SearchTitles([FromQuery] string query, CancellationToken cancellationToken) => AniMetaApi.SearchTitles(query, cancellationToken);

    /// <summary>Searches AniList and MyAnimeList by title.</summary>
    /// <param name="query">The title.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matches, each with the AniDB entry it maps to, when it does.</returns>
    [HttpGet("Titles/SearchOnline")]
    public Task<IReadOnlyList<Sources.RemoteCandidate>> SearchOnline([FromQuery] string query, CancellationToken cancellationToken) => AniMetaApi.SearchOnline(query, logger, cancellationToken);

    /// <summary>Downloads a fresh titles dump, within AniDB's once-a-day allowance.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What happened.</returns>
    [HttpPost("Titles/Refresh")]
    public Task<TitlesRefreshResult> RefreshTitles(CancellationToken cancellationToken) => AniMetaApi.RefreshTitles(logger, cancellationToken);

    /// <summary>Imports the titles dump on disk again.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The dump's status.</returns>
    [HttpPost("Titles/Reimport")]
    public Task<TitlesDumpStatus> ReimportTitles(CancellationToken cancellationToken) => AniMetaApi.ReimportTitles(logger, cancellationToken);

    /// <summary>Lists the replaced posters waiting for a decision, newest first.</summary>
    /// <param name="start">The first item to return.</param>
    /// <param name="limit">How many to return.</param>
    /// <returns>The page.</returns>
    [HttpGet("Images/Backups")]
    public ImageBackupPage GetImageBackups([FromQuery] int start = 0, [FromQuery] int limit = 24) => AniMetaApi.GetImageBackups(start, limit);

    /// <summary>Gets the poster held for an item.</summary>
    /// <param name="id">The item id.</param>
    /// <returns>The poster as a data address.</returns>
    [HttpGet("Images/Backup")]
    public ActionResult<ImageBackupPreview> GetImageBackup([FromQuery] string id)
        => AniMetaApi.GetImageBackup(id) is { } dataUrl ? new ImageBackupPreview { DataUrl = dataUrl } : NotFound();

    /// <summary>Starts putting the previous posters back in the background.</summary>
    /// <param name="request">The items.</param>
    /// <returns>The run's status.</returns>
    [HttpPost("Images/Revert")]
    public ImageJobStatus RevertImages([FromBody] ImageSelectionRequest request)
        => AniMetaApi.RevertImages(request.Ids, request.All, logger);

    /// <summary>Reports how the current or last revert run stands.</summary>
    /// <returns>The status.</returns>
    [HttpGet("Images/RevertStatus")]
    public ImageJobStatus GetRevertStatus() => AniMetaApi.GetRevertStatus();

    /// <summary>Accepts AniDB's posters and deletes the previous ones.</summary>
    /// <param name="request">The items.</param>
    /// <returns>How many were handled.</returns>
    [HttpPost("Images/Acknowledge")]
    public ImageDoneResult AcknowledgeImages([FromBody] ImageSelectionRequest request) => AniMetaApi.AcknowledgeImages(request.Ids, request.All);

    /// <summary>Forgets the reverted items, so that the next task run replaces their posters again.</summary>
    /// <returns>How many were forgotten.</returns>
    [HttpPost("Images/ForgetReverted")]
    public ImageDoneResult ForgetRevertedImages() => AniMetaApi.ForgetRevertedImages();

    /// <summary>Lists the items waiting for a paused source.</summary>
    /// <returns>The items.</returns>
    [HttpGet("Waiting")]
    public IReadOnlyList<WaitingRecord> GetWaiting() => AniMetaApi.GetWaiting();

    /// <summary>Gets which source supplied each field of an item.</summary>
    /// <param name="itemKey">The item key.</param>
    /// <returns>The fields.</returns>
    [HttpGet("Provenance")]
    public IReadOnlyList<ProvenanceRecord> GetProvenance([FromQuery] string itemKey) => AniMetaApi.GetProvenance(itemKey);

    /// <summary>Lists the fields a stand-in supplied.</summary>
    /// <returns>The fields.</returns>
    [HttpGet("Provisional")]
    public IReadOnlyList<ProvenanceRecord> GetProvisional() => AniMetaApi.GetProvisional();

    /// <summary>Lists the most recent requests.</summary>
    /// <param name="count">How many.</param>
    /// <returns>The requests.</returns>
    [HttpGet("Requests")]
    public IReadOnlyList<RequestLogRecord> GetRequests([FromQuery] int count = 100) => AniMetaApi.GetRequests(count);

    /// <summary>Describes what each source has cached.</summary>
    /// <returns>One entry per source.</returns>
    [HttpGet("Cache")]
    public IReadOnlyList<CacheSourceStatus> GetCache() => AnswerCache.Status();

    /// <summary>Marks every cached answer of a source as expired.</summary>
    /// <param name="source">The source id.</param>
    /// <returns>What happened.</returns>
    [HttpPost("Cache/Expire")]
    public ActionResult<CacheExpireResult> ExpireCache([FromQuery] string source)
    {
        try
        {
            return AnswerCache.Expire(source);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Checks a source's client name or key.</summary>
    /// <param name="source">The source id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was found.</returns>
    [HttpPost("Clients/Test")]
    public Task<ClientTestResult> TestClient([FromQuery] string source, CancellationToken cancellationToken) => AniMetaApi.TestClient(source, cancellationToken);

    /// <summary>Runs the queues now.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was done.</returns>
    [HttpPost("Queues/Run")]
    public Task<QueueRunResult> RunQueues(CancellationToken cancellationToken) => AniMetaApi.RunQueues(logger, cancellationToken);

    /// <summary>Downloads the owner's decisions as JSON. Holds no keys.</summary>
    /// <returns>The file.</returns>
    [HttpGet("Export")]
    public FileContentResult Export()
        => File(Encoding.UTF8.GetBytes(AniMetaApi.Export()), MediaTypeNames.Application.Json, "animetaplus-decisions.json");

    /// <summary>Merges an export in.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many records were taken.</returns>
    [HttpPost("Import")]
    [Consumes(MediaTypeNames.Application.Json, "text/plain")]
    public async Task<ActionResult<int>> Import(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return AniMetaApi.Import(json);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException)
        {
            return BadRequest(ex.Message);
        }
    }
}
