using System;
using System.Collections.Generic;

namespace AniMetaPlus.Data;

/// <summary>
/// How much a block covers.
/// </summary>
public enum BlockScope
{
    /// <summary>The source as a whole: nothing is asked of it.</summary>
    Source,

    /// <summary>One item, and optionally one kind of data, from the source.</summary>
    Item
}

/// <summary>
/// Why a source or an item was blocked.
/// </summary>
public enum BlockReason
{
    /// <summary>The source banned the client or the IP.</summary>
    Banned,

    /// <summary>The source said too many requests were sent.</summary>
    RateLimited,

    /// <summary>The source was down or unreachable.</summary>
    Unavailable,

    /// <summary>The source failed with server errors repeatedly.</summary>
    ServerError,

    /// <summary>The key or client id was refused.</summary>
    AuthFailed,

    /// <summary>The client name or version was refused.</summary>
    ClientRejected,

    /// <summary>The source has no such item.</summary>
    NotFound,

    /// <summary>The source answered with something that could not be read.</summary>
    ParseFailed,

    /// <summary>The source had nothing for the item.</summary>
    Empty,

    /// <summary>The owner blocked it by hand.</summary>
    Manual,

    /// <summary>Anything else.</summary>
    Other
}

/// <summary>
/// A remembered refusal, so that the same request is not sent again until it can succeed.
/// </summary>
public class BlockRecord
{
    /// <summary>Gets or sets the key, built from source, scope, item and kind.</summary>
    public string Id { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the source id.</summary>
    public string Source { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets what the block covers.</summary>
    public BlockScope Scope { get; set; }

    /// <summary>Gets or sets the item key, empty for a source-wide block.</summary>
    public string ItemKey { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the item's name as it was when blocked, for the settings page.</summary>
    public string ItemName { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the kind of data, empty for every kind.</summary>
    public string Kind { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets why.</summary>
    public BlockReason Reason { get; set; }

    /// <summary>Gets or sets what the source said.</summary>
    public string Message { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the HTTP status, when there was one.</summary>
    public int? HttpStatus { get; set; }

    /// <summary>Gets or sets when it was first seen.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>Gets or sets when it was last seen.</summary>
    public DateTime LastSeenUtc { get; set; }

    /// <summary>Gets or sets how many times it was seen.</summary>
    public int Count { get; set; }

    /// <summary>Gets or sets when it lapses. <c>null</c> for a pinned block, which never does.</summary>
    public DateTime? ExpiresUtc { get; set; }

    /// <summary>Gets or sets the backoff applied last time, which the next one doubles.</summary>
    public int BackoffMinutes { get; set; }

    /// <summary>Gets or sets a value indicating whether the owner pinned it.</summary>
    public bool Pinned { get; set; }

    /// <summary>Gets or sets who set it: <c>auto</c> or <c>user</c>.</summary>
    public string SetBy { get; set; } = "auto";

    /// <summary>
    /// Gets a value indicating whether the block is in force at a moment.
    /// </summary>
    /// <param name="nowUtc">The moment.</param>
    /// <returns><c>true</c> while it holds.</returns>
    public bool IsActive(DateTime nowUtc) => Pinned || ExpiresUtc is null || ExpiresUtc > nowUtc;

    /// <summary>
    /// Builds the key a block is stored under.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <param name="scope">The scope.</param>
    /// <param name="itemKey">The item key.</param>
    /// <param name="kind">The kind of data.</param>
    /// <returns>The key.</returns>
    public static string MakeId(string source, BlockScope scope, string itemKey, string kind)
        => string.Join("|", source.ToLowerInvariant(), scope.ToString(), itemKey.ToLowerInvariant(), kind.ToLowerInvariant());
}

/// <summary>
/// Which source supplied a field of an item.
/// </summary>
public class ProvenanceRecord
{
    /// <summary>Gets or sets the key, <c>itemKey|field</c>.</summary>
    public string Id { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the item key.</summary>
    public string ItemKey { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the field, a kind of data.</summary>
    public string Field { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the source that supplied it.</summary>
    public string Source { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the source it should have come from, when that one was blocked.</summary>
    public string PreferredSource { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a stand-in supplied it, to be replaced.</summary>
    public bool Provisional { get; set; }

    /// <summary>
    /// Gets or sets the source this field is held to, ahead of the usual order: the stand-in while
    /// a review of its replacement is open, or whichever source the owner chose in a review.
    /// </summary>
    public string PinnedSource { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the owner locked the field to its source.</summary>
    public bool Locked { get; set; }

    /// <summary>Gets or sets the value the owner kept the field over, so it is not proposed again.</summary>
    public string DeclinedValue { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the item's path, so that it can be found and refreshed again.</summary>
    public string ItemPath { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the item's name, for the settings page.</summary>
    public string ItemName { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets when it was written.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>
    /// Builds the key a field is stored under.
    /// </summary>
    /// <param name="itemKey">The item key.</param>
    /// <param name="field">The field.</param>
    /// <returns>The key.</returns>
    public static string MakeId(string itemKey, string field) => itemKey + "|" + field;
}

/// <summary>
/// An item the owner identified by hand.
/// </summary>
public class MappingRecord
{
    /// <summary>Gets or sets the item key.</summary>
    public string Id { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets whether it is a series or a movie.</summary>
    public string Kind { get; set; } = "Series";

    /// <summary>Gets or sets the AniDB id it is.</summary>
    public string AnimeId { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the item's name when it was mapped.</summary>
    public string ItemName { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets a note from the owner.</summary>
    public string Note { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets when it was mapped.</summary>
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// A guess at what an unmatched item is.
/// </summary>
public class CandidateRecord
{
    /// <summary>Gets or sets the AniDB id.</summary>
    public string AnimeId { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the title that matched.</summary>
    public string Title { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets how close it is, 0 to 100.</summary>
    public int Score { get; set; }
}

/// <summary>
/// An item nothing could identify.
/// </summary>
public class UnmatchedRecord
{
    /// <summary>Gets or sets the item key.</summary>
    public string Id { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets whether it is a series or a movie.</summary>
    public string Kind { get; set; } = "Series";

    /// <summary>Gets or sets its name.</summary>
    public string Name { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets its year, when known.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets its path.</summary>
    public string Path { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the owner mapped it and it waits for a refresh.</summary>
    public bool PendingRefresh { get; set; }

    /// <summary>Gets or sets how often it was tried.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets when it was first seen.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>Gets or sets when it was last tried.</summary>
    public DateTime LastAttemptUtc { get; set; }

    /// <summary>Gets or sets the best guesses from the local titles dump.</summary>
    public List<CandidateRecord> Candidates { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the owner chose to leave it alone.</summary>
    public bool Ignored { get; set; }
}

/// <summary>
/// An item waiting for a paused source to answer again.
/// </summary>
public class WaitingRecord
{
    /// <summary>Gets or sets the item key.</summary>
    public string Id { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the item's path, so that it can be found and refreshed.</summary>
    public string ItemPath { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the item's name.</summary>
    public string ItemName { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the source it waits for.</summary>
    public string Source { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets why it waits.</summary>
    public string Reason { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets when it started waiting.</summary>
    public DateTime AddedUtc { get; set; }
}

/// <summary>
/// The owner's decision on a replacement.
/// </summary>
public enum ReviewStatus
{
    /// <summary>Not decided yet.</summary>
    Pending,

    /// <summary>The proposed value replaces the current one.</summary>
    Accepted,

    /// <summary>The current value stays.</summary>
    Kept,

    /// <summary>The current value stays and is not proposed for replacing again.</summary>
    Locked
}

/// <summary>
/// A value the preferred source has again, waiting for the owner to accept it in place of what
/// a stand-in supplied.
/// </summary>
public class ReviewRecord
{
    /// <summary>Gets or sets the key, <c>itemKey|field</c>.</summary>
    public string Id { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the item key.</summary>
    public string ItemKey { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the item's path, so that it can be found and refreshed.</summary>
    public string ItemPath { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the item's name.</summary>
    public string ItemName { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the field, a kind of data.</summary>
    public string Field { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the value the item has.</summary>
    public string CurrentValue { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the source of the value the item has.</summary>
    public string CurrentSource { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the value proposed in its place.</summary>
    public string ProposedValue { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the source of the proposed value.</summary>
    public string ProposedSource { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the decision.</summary>
    public ReviewStatus Status { get; set; }

    /// <summary>Gets or sets when it was proposed.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Gets or sets when it was decided.</summary>
    public DateTime? DecidedUtc { get; set; }
}

/// <summary>
/// The last check of a source's client name or key.
/// </summary>
public class ClientTestRecord
{
    /// <summary>Gets or sets the source id.</summary>
    public string Id { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the result: <c>valid</c>, <c>invalid</c>, <c>banned</c> or <c>unreachable</c>.</summary>
    public string Result { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets what was found.</summary>
    public string Message { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the client or key that was checked, masked.</summary>
    public string Subject { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets when it was checked.</summary>
    public DateTime AtUtc { get; set; }
}

/// <summary>
/// One request sent to a source, for the settings page.
/// </summary>
public class RequestLogRecord
{
    /// <summary>Gets or sets the id.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the source id.</summary>
    public string Source { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets what was asked for, without keys.</summary>
    public string Target { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the item key.</summary>
    public string ItemKey { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the outcome.</summary>
    public string Outcome { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the HTTP status.</summary>
    public int? HttpStatus { get; set; }

    /// <summary>Gets or sets how long it took.</summary>
    public int DurationMs { get; set; }

    /// <summary>Gets or sets when it was sent.</summary>
    public DateTime AtUtc { get; set; }
}

/// <summary>
/// A named value the plugin keeps between runs.
/// </summary>
public class StateRecord
{
    /// <summary>Gets or sets the name.</summary>
    public string Id { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the value.</summary>
    public string Value { get; set => field = value ?? string.Empty; } = string.Empty;
}

/// <summary>
/// Where a poster the AniDB poster task replaced stands.
/// </summary>
public enum ImageBackupState
{
    /// <summary>The previous poster is held, waiting for the owner to revert or acknowledge.</summary>
    Pending,

    /// <summary>The owner accepted AniDB's poster, or there was no previous poster to hold.</summary>
    Acknowledged,

    /// <summary>The owner went back to the previous poster; the task leaves the item alone.</summary>
    Reverted
}

/// <summary>
/// A poster replaced by the AniDB poster task, and the previous one kept beside it.
/// </summary>
public class ImageBackupRecord
{
    /// <summary>Gets or sets the server's item id.</summary>
    public string Id { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the item's name.</summary>
    public string ItemName { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the series name of a season, otherwise empty.</summary>
    public string SeriesName { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the kind: Series, Season or Movie.</summary>
    public string Kind { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the backup's file name in the backup folder; empty when none is held.</summary>
    public string BackupFile { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the backup's media type.</summary>
    public string BackupMimeType { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the AniDB picture address that was applied.</summary>
    public string AppliedUrl { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the poster file's path, length and write time right after the replace.</summary>
    public string AppliedStamp { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets where the owner's decision stands.</summary>
    public ImageBackupState State { get; set; }

    /// <summary>Gets or sets when the poster was replaced.</summary>
    public DateTime ReplacedUtc { get; set; }
}

/// <summary>
/// A collection the franchise task made, remembered so that a rerun updates it rather than
/// making another, and so that collections the owner made are never touched.
/// </summary>
public class FranchiseCollectionRecord
{
    /// <summary>Gets or sets the franchise key: the lowest AniDB id in its component.</summary>
    public string Id { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the server's id of the collection.</summary>
    public string CollectionId { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the name the task last gave the collection.</summary>
    public string Name { get; set => field = value ?? string.Empty; } = string.Empty;

    /// <summary>Gets or sets the server ids of the items the task last put in it.</summary>
    public List<string> MemberIds { get; set => field = value ?? []; } = [];

    /// <summary>Gets or sets a value indicating whether the collection was given its art, after which only missing images are filled.</summary>
    public bool ArtSet { get; set; }
}
