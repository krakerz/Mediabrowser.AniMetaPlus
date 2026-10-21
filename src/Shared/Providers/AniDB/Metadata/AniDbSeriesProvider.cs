using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
#if JELLYFIN
using Jellyfin.Data.Enums;
#endif
using AniMetaPlus.Configuration;
using AniMetaPlus.Data;
using AniMetaPlus.Core;
using AniMetaPlus.Sources;
using AniMetaPlus.Providers.AniDB.Identity;
using AniMetaPlus.Providers.AniDB.Mapping;
using AniMetaPlus.Titles;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMetaPlus.Providers.AniDB.Metadata;

/// <summary>
/// The AniDB metadata provider for series.
/// </summary>
public partial class AniDbSeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IHasOrder
#if EMBY
    , IHasSupportedExternalIdentifiers
#endif
{
    /// <summary>
    /// The rating given to anime AniDB flags as adult, so that Jellyfin's parental controls
    /// have something to act on.
    /// </summary>
    private const string AdultOfficialRating = "XXX";

    /// <summary>
    /// Where AniDB serves the pictures it names in a picture attribute.
    /// </summary>
    private const string PersonImageBaseUrl = "https://cdn.anidb.net/images/main/";

    /// <summary>
    /// The request log's name for downloads from AniDB's image server.
    /// </summary>
    internal const string ImageLogSource = "AniDB image";

    // AniDB bans a client that sends requests closer together than 2500ms, which is the
    // floor the configured interval is clamped to. A bucket limiter cannot express this: it
    // replenishes independently of when tokens are consumed, so an idle plugin holding a full
    // bucket can fire two requests back to back. Gate on the time since the previous request
    // instead, measured with the monotonic clock so a system time change cannot shorten it.
    private static readonly TimeSpan _minimumDelay = TimeSpan.FromMilliseconds(1);
    private static readonly SemaphoreSlim _requestGate = new(1, 1);

    /// <summary>
    /// How long an id AniDB has refused is left alone before it is asked for again. Long
    /// enough that a scan does not keep paying for the same refusal, short enough that an
    /// entry restored on AniDB's side is picked up the same day.
    /// </summary>
    private static readonly TimeSpan _retryAfterRefusal = TimeSpan.FromHours(6);

    /// <summary>
    /// One gate per anime, so that the episodes of a season refreshing together send one
    /// request for the entry they share rather than one apiece.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _downloadGates = new(StringComparer.Ordinal);

    /// <summary>
    /// The ids AniDB has refused, and what it said. Kept because a refusal caches nothing, so
    /// without it every episode of the show asks again and pays for the same answer.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (DateTime At, string Message)> _refusedIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Anime whose cached document is read whatever its age until the time given, so that the
    /// cached-poster task's refreshes never reach the AniDB API.
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTime> _cacheOnlyUntil = new(StringComparer.Ordinal);

    // A ban is temporary, but its remaining time cannot be queried and runs from 15 minutes
    // to 24 hours. Probing a banned server extends the ban, so double the backoff after each
    // consecutive ban and reset it once a request succeeds: 45 minutes, 1.5, 3, 6 and 12 hours,
    // then 24 hours at most.
    internal static readonly TimeSpan InitialBanBackoff = TimeSpan.FromMinutes(45);
    internal static readonly TimeSpan MaximumBanBackoff = TimeSpan.FromHours(24);
    private static readonly TimeSpan _initialBanBackoff = InitialBanBackoff;
    private static readonly TimeSpan _maximumBanBackoff = MaximumBanBackoff;
    private static readonly Lock _banLock = new();

    /// <summary>
    /// AniDB tags that are dropped before anything else looks at them, matched on the tag's own
    /// id and on its parent id, so listing a branch also removes the tags directly under it.
    /// </summary>
    private static readonly int[] IgnoredTagIds =
    [
        // Maintenance tags. AniDB housekeeping that says nothing about the show.
        3955,   // DESCRIPTION MISSING (1610)
        5939,   // CAST MISSING (2042)
        6850,   // STAFF MISSING (335)

        // Origin. On more than 14000 anime between them, so they distinguish nothing.
        6173,   // origin
        6152,   // Chinese production (1471)
        6166,   // South Korean production (288)
        7885,   // Japanese production (14413)
    ];

    /// <summary>
    /// The tags AniDB flags as 18+ content, dropped unless
    /// <see cref="PluginConfiguration.IncludeAdultTags"/> says otherwise. Matched on the tag's
    /// own id and on its parent id, so listing a branch also removes the tags under it. Kept
    /// apart from <see cref="IgnoredTagIds"/> because whether these belong in a library is the
    /// owner's call. Yuri and pornography are flagged by AniDB but left in, because
    /// <see cref="GenreHelper"/> reads both as genres. The counts are the anime carrying each
    /// tag when the list was taken.
    /// </summary>
    private static readonly int[] AdultTagIds =
    [
        301,    // lactation (169)
        724,    // shotacon (0)
        1172,   // sex slave (0)
        1521,   // anal tail (0)
        2016,   // inverted nipples (0)
        2142,   // pubic hair (0)
        2429,   // futanari (107)
        2693,   // handjob (405)
        2694,   // glory hole (6)
        2696,   // threesome (497)
        2698,   // pantyjob (101)
        2699,   // sixty-nine (358)
        2700,   // window fuck (94)
        2701,   // pussy sandwich (83)
        2702,   // boobjob (602)
        2703,   // wakamezake (6)
        2704,   // cum play (208)
        2705,   // creampie (967)
        2706,   // masturbation (715)
        2707,   // internal shots (551)
        2708,   // uncensored version available (594)
        2709,   // netorare (203)
        2710,   // squirting (253)
        2711,   // urination (458)
        2714,   // point of view (38)
        2715,   // rape (835)
        2717,   // anal (710)
        2719,   // gang bang (363)
        2720,   // bestiality (64)
        2721,   // public sex (578)
        2722,   // doggy style (840)
        2724,   // double penetration (412)
        2725,   // ahegao (242)
        2726,   // prostitution (233)
        2727,   // stomach stretch (100)
        2728,   // fisting (36)
        2730,   // exhibitionism (169)
        2731,   // sex while on the phone (61)
        2732,   // female rapes female (145)
        2733,   // enjoyable rape (337)
        2734,   // scissoring (67)
        2736,   // mother-daughter incest (34)
        2737,   // father-daughter incest (47)
        2740,   // mother-son incest (57)
        2742,   // futa x futa (16)
        2743,   // futa x male (22)
        2744,   // futa x female (88)
        2747,   // hidden vibrator (101)
        2828,   // borderline porn (188)
        2842,   // incest (441)
        2896,   // deflowering (697)
        2899,   // strap-on dildo (113)
        2900,   // dildos - vibrators (465)
        2902,   // facesitting (90)
        2903,   // scat (63)
        2911,   // footjob (122)
        2912,   // shimaidon (86)
        2913,   // cum swapping (18)
        2914,   // bukkake (128)
        2918,   // cunnilingus (808)
        2920,   // rimming (186)
        2921,   // throat fucking (362)
        2935,   // stomach bulge (97)
        2995,   // nipple penetration (21)
        3299,   // double fellatio (158)
        3443,   // orgy (150)
        3800,   // oyakodon (45)
        4020,   // pegging (13)
        4046,   // foursome (177)
        4049,   // outdoor sex (405)
        4207,   // doujin (22)
        4492,   // gang rape (243)
        4594,   // acidic breast milk (0)
        4741,   // anal pissing (10)
        5053,   // vagina dentata (0)
        5327,   // urophagia (32)
        5451,   // soapland (11)
        5715,   // eye penetration (4)
        5750,   // triple penetration (118)
        6164,   // onahole (11)
        6218,   // gokkun (252)
        6221,   // cybersex (13)
        6268,   // fellatio (1165)
        6355,   // water sex (120)
        6389,   // cervix penetration (60)
        6443,   // wooden horse (34)
        6455,   // double-sided dildo (59)
        6507,   // pillory (8)
        6519,   // autofellatio (6)
        6520,   // foreskin sex (2)
        6538,   // large areolae (0)
        6572,   // FFM threesome (342)
        6573,   // MMF threesome (120)
        6731,   // wax play (38)
        6829,   // sleeping sex (30)
        6843,   // MMM threesome (8)
        7004,   // prostate massage (56)
        7148,   // impregnation with larvae (32)
        7151,   // pregnant with larvae (0)
        7229,   // fingering (410)
        7247,   // sumata (83)
        7251,   // cockring (18)
        7399,   // orgasm denial (24)
        7403,   // anal fingering (107)
        7420,   // spitroast (77)
        7422,   // reverse spitroast (63)
        7560,   // suspension bondage (65)
        7600,   // macrophilia (27)
        7614,   // double assjob (3)
        7615,   // assjob (13)
        7633,   // petplay (17)
        7829,   // FFF threesome (36)
        7868,   // microphilia (4)
        7886,   // wormhole sex (3)
        7957,   // group sex (0)
        7958,   // stripper (0)
        8013,   // vaginal pissing (2)
        8028,   // pasties (0)
        8038,   // nipple stimulation (260)
        8163,   // internal breast shots (3)
        8266,   // double vaginal penetration (2)
        8267,   // double anal penetration (0)
        8268,   // biphallic (3)

        // Branches AniDB leaves unflagged that explicit tags are filed under, matched as
        // parent ids to clear those out.
        1773,   // tentacle (335)
        1894,   // shota (136)
        2566,   // loli (394)
        2608,   // fetishes (0)
        2697,   // voyeurism (285)
        2712,   // sex toys (276)
        2729,   // BDSM (652)
        2816,   // sexual fantasies (336)
        2891,   // breasts (0)
        2901,   // bondage (524)
    ];

    private static readonly CompositeFormat _seriesQueryUrlFormat = CompositeFormat.Parse("http://api.anidb.net:9001/httpapi?request=anime&client={0}&clientver={1}&protover=1&aid={2}");

    /// <summary>
    /// The monotonic timestamp before which no further AniDB request may be issued.
    /// Guarded by <see cref="_requestGate"/>.
    /// </summary>
    private static long _nextRequestTimestamp = Stopwatch.GetTimestamp();

    /// <summary>
    /// The UTC time at which the current ban is assumed to have lapsed. Wall clock rather than
    /// the monotonic clock used for request spacing, because a ban outlives the process.
    /// Guarded by <see cref="_banLock"/>.
    /// </summary>
    private static DateTime _banUntilUtc = DateTime.MinValue;

    /// <summary>
    /// When the latest ban was registered. Guarded by <see cref="_banLock"/>.
    /// </summary>
    private static DateTime _banRegisteredUtc = DateTime.MinValue;

    /// <summary>
    /// How long the next detected ban will be waited out for. Guarded by <see cref="_banLock"/>.
    /// </summary>
    private static TimeSpan _currentBanBackoff = _initialBanBackoff;

    /// <summary>
    /// Whether a ban has been reported and no request has succeeded since. Guarded by
    /// <see cref="_banLock"/>, and read outside it only as a logging fast path.
    /// </summary>
    private static bool _banActive;

    /// <summary>
    /// Whether the "resuming requests" message has already been logged for the current ban.
    /// Guarded by <see cref="_banLock"/>.
    /// </summary>
    private static bool _resumeLogged;

    /// <summary>
    /// How many callers are waiting for a request slot, how many requests have gone out since
    /// the server started, and when the last one did. Reported by <see cref="GetRequestStatus"/>
    /// and touched only through <see cref="Interlocked"/>.
    /// </summary>
    private static int _queuedRequests;
    private static long _requestsSent;
    private static long _lastRequestTicks;

    private readonly IApplicationPaths _appPaths;

    /// <summary>
    /// What AniDB's creator types mean to Jellyfin. The role is what the cast list shows
    /// under the name, so it keeps AniDB's own wording where Jellyfin has no kind for it.
    /// </summary>
    private static readonly Dictionary<string, (PersonKind Kind, string Role)> _creatorTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Direction", (PersonKind.Director, "Director") },
        { "Chief Direction", (PersonKind.Director, "Chief Director") },
        { "Series Direction", (PersonKind.Director, "Series Director") },
        { "Animation Direction", (PersonKind.Director, "Animation Director") },
        { "Chief Animation Direction", (PersonKind.Director, "Chief Animation Director") },
        { "Music", (PersonKind.Composer, "Music") },
        { "Original Work", (PersonKind.Writer, "Original Creator") },
        { "Story Composition", (PersonKind.Writer, "Story Composition") },
        { "Series Composition", (PersonKind.Writer, "Series Composition") },
        { "Screenplay", (PersonKind.Writer, "Screenplay") },
        { "Original Plan", (PersonKind.Writer, "Original Plan") },
        { "Character Design", (PersonKind.Artist, "Character Design") },
        { "Main Character Design", (PersonKind.Artist, "Main Character Design") },
        { "Animation Character Design", (PersonKind.Artist, "Animation Character Design") }
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="AniDbSeriesProvider"/> class.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    public AniDbSeriesProvider(IApplicationPaths appPaths)
    {
        _appPaths = appPaths;
        TitleMatcher = AniDbTitleMatcher.DefaultInstance;
    }

    /// <inheritdoc />
    public int Order => -1;

    /// <inheritdoc />
    public string Name => ProviderNames.Display;

    /// <summary>
    /// Gets or sets the logger used to report the plugin-wide AniDB ban state.
    /// </summary>
    internal static ILogger<AniDbSeriesProvider>? Logger { get; set; }

    private IAniDbTitleMatcher TitleMatcher { get; set; }

    /// <inheritdoc />
    public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
    {
        var result = await GetSeriesMetadata(info, cancellationToken).ConfigureAwait(false);
        AlternativeTitles.Apply(result.Item, _appPaths);

        if (result.Item is { } series && series.Status == null && AiringStatus(series.PremiereDate, series.EndDate, DateTime.UtcNow) is { } status)
        {
            series.Status = status;
        }

        // Every id the show ends up with on one line, so a wrong one is easy to find. Only for
        // a show in the library: the season provider asks this for a season's own entry too.
        if (result.Item is { } item && !string.IsNullOrEmpty(info.Path))
        {
            Logger?.LogInformation("{SeriesName} ({Path}) has {Ids}", item.Name ?? info.Name, info.Path, IdSummary(info.ProviderIds, item.ProviderIds));
        }

        return result;
    }

    /// <summary>
    /// An item's ids for the log, AniDB's first: the ids it carried, with those the refresh
    /// gave it taking their place. The servers keep an id an item already has, except AniList's
    /// and MyAnimeList's, which a refresh corrects; this reads them the same way.
    /// </summary>
    /// <param name="before">The ids the item carried.</param>
    /// <param name="found">The ids the refresh gave it.</param>
    /// <returns>The ids, such as "AniDB 8692, MyAnimeList 11757, AniList 11757, TVDB 259640".</returns>
    internal static string IdSummary(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> found)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, id) in before)
        {
            if (!string.IsNullOrEmpty(id))
            {
                ids[provider] = id;
            }
        }

        foreach (var (provider, id) in found)
        {
            var corrected = provider is SourceIds.Mal or SourceIds.AniList or ProviderNames.AniDb;
            if (!string.IsNullOrEmpty(id) && (corrected || !ids.ContainsKey(provider)))
            {
                ids[provider] = id;
            }
        }

        string[] order = [ProviderNames.AniDb, SourceIds.Mal, SourceIds.AniList, "Tvdb", "Tmdb", "Imdb"];
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Tvdb"] = "TVDB", ["Tmdb"] = "TMDB", ["Imdb"] = "IMDb" };

        var listed = ids
            .OrderBy(pair => Array.FindIndex(order, o => string.Equals(o, pair.Key, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : order.Length)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (names.GetValueOrDefault(pair.Key) ?? pair.Key) + " " + pair.Value)
            .ToList();

        return listed.Count > 0 ? string.Join(", ", listed) : "no ids";
    }

    /// <summary>
    /// Whether a show is still airing, from its dates: ended once its end date has passed,
    /// continuing once it has started and has not ended.
    /// </summary>
    /// <param name="premiere">The first air date.</param>
    /// <param name="end">The last air date.</param>
    /// <param name="now">The time, in UTC.</param>
    /// <returns>The status, or <c>null</c> when the dates do not say.</returns>
    internal static SeriesStatus? AiringStatus(DateTimeOffset? premiere, DateTimeOffset? end, DateTime now)
    {
        if (end is { } last && last <= now)
        {
            return SeriesStatus.Ended;
        }

        return premiere is { } first && first <= now ? SeriesStatus.Continuing : null;
    }

    private async Task<MetadataResult<Series>> GetSeriesMetadata(SeriesInfo info, CancellationToken cancellationToken)
    {
        RefreshScope.Enter(info);
        var animeId = info.ProviderIds.GetValueOrDefault(ProviderNames.AniDb);
        var unidentifiedKey = ItemKeys.ForUnidentified("Series", info.Path, info.Name);

        // What the owner mapped by hand settles the question before anything is guessed.
        string? link = null;
        if (string.IsNullOrEmpty(animeId))
        {
            animeId = Queues.MappedAnimeId(unidentifiedKey);

            // Mapped to an AniList or MyAnimeList entry: AniDB had none to map to.
            if (animeId is { } mapped && LinkParts(mapped) is not null)
            {
                link = mapped;
                animeId = await AniDbForLink(mapped, cancellationToken).ConfigureAwait(false);
            }
        }

        // The owner set the show's ids by hand: a MyAnimeList or AniList id names the show
        // outright, and is looked up before the name is guessed at. A show AniDB has no entry
        // for is filled from those sites, rather than matched to whatever its name comes
        // nearest.
        if (string.IsNullOrEmpty(animeId) && link is null && IdCheck.IsManual(info.Path))
        {
            var links = LinksFromIds(info.ProviderIds);

            animeId = await AniDbMappings.ResolveSeriesIdByLink(_appPaths, links, Logger ?? (ILogger)NullLogger.Instance, cancellationToken).ConfigureAwait(false);
            link = links.FirstOrDefault();
        }

        if (string.IsNullOrEmpty(animeId) && link is null)
        {
            animeId = await Identify(info, cancellationToken).ConfigureAwait(false);

            // An item another plugin or an earlier mapping tied to AniList or MyAnimeList, which
            // nothing here could tie to AniDB, is still filled from those.
            if (string.IsNullOrEmpty(animeId))
            {
                link = LinkFromIds(info.ProviderIds);
                animeId = link is null ? null : await AniDbForLink(link, cancellationToken).ConfigureAwait(false);
            }
        }

        if (string.IsNullOrEmpty(animeId) && link is not null)
        {
            Queues.ClearUnmatched(unidentifiedKey);

            return await GetLinkedMetadata(link, info, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(animeId))
        {
            // Nothing is returned, so a provider after this one may still fill the show, and the
            // owner can map it by hand from the settings page.
            await Queues.RecordUnmatched(unidentifiedKey, "Series", info.Name, GetLookupYear(info), info.Path, cancellationToken).ConfigureAwait(false);

            return new MetadataResult<Series>();
        }

        Queues.ClearUnmatched(unidentifiedKey);

        return await GetMergedMetadata(animeId, info, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the show from AniDB and every other source, merged in the order the owner set.
    /// </summary>
    /// <param name="animeId">The AniDB id.</param>
    /// <param name="info">The lookup info.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result; empty when the item waits for a paused source.</returns>
    private async Task<MetadataResult<Series>> GetMergedMetadata(string animeId, SeriesInfo info, CancellationToken cancellationToken)
    {
        MetadataResult<Series> result;
        SourceOutcome aniDb;

        try
        {
            result = await GetMetadataForId(animeId, info, cancellationToken).ConfigureAwait(false);

            var extras = AniDbExtras.Read(Path.Combine(GetSeriesDataPath(_appPaths, animeId), "series.xml"));
            aniDb = new SourceOutcome(SourceState.Ok, ToRecord(animeId, result.Item, result.People, extras));
        }
        catch (Exception ex) when (ex is AniDbBannedException or InvalidOperationException)
        {
            aniDb = ex switch
            {
                AniDbUnavailableException { ClientRejected: false } when !IsClientUsable() => SourceOutcome.Unconfigured,
                AniDbBannedException => SourceOutcome.Blocked,
                _ => SourceOutcome.Missing
            };

            result = new MetadataResult<Series> { Item = new Series() };
            result.Item.ProviderIds[ProviderNames.AniDb] = animeId;
            await AddMappedShowIds(result.Item, info, animeId, cancellationToken).ConfigureAwait(false);

            Logger?.LogDebug("AniDB has no answer for anime {AnimeId} ({State}): {Message}", animeId, aniDb.State, ex.Message);
        }

        var context = new FetchContext
        {
            ItemKey = ItemKeys.ForAnime(animeId),
            ItemName = result.Item.Name ?? info.Name ?? string.Empty,
            ItemPath = info.Path ?? string.Empty,
            AnimeId = animeId,
            Year = result.Item.ProductionYear ?? GetLookupYear(info),
            Language = info.MetadataLanguage ?? "en"
        };

        foreach (var (provider, id) in info.ProviderIds)
        {
            context.Ids[provider] = id;
        }

        foreach (var (provider, id) in result.Item.ProviderIds)
        {
            context.Ids[provider] = id;
        }

        // A show whose ids the owner set keeps them: the mappings do not override its MyAnimeList
        // and AniList ids, nor replace the ones it carries.
        var manual = IdCheck.IsManual(info.Path);
        var linked = manual
            ? new Dictionary<string, string>()
            : await AniDbSeasonProvider.AddLinkedIds(_appPaths, context, animeId, cancellationToken).ConfigureAwait(false);
        if (linked.Count > 0 && HostServices.Refresher?.CorrectIds(info.Path ?? string.Empty, linked) is { Count: > 0 } corrected)
        {
            Logger?.LogInformation(
                "{SeriesName} carried ids of another anime than AniDB {AnimeId}, which the AniBridge mappings link to; replaced {Corrected}",
                result.Item.Name ?? info.Name,
                animeId,
                string.Join(", ", corrected));
        }

        if (aniDb.Record is { } record)
        {
            foreach (var (provider, id) in record.Ids)
            {
                context.Ids.TryAdd(provider, id);
            }
        }

        var merged = await SeriesMerge.Apply(result, aniDb, context, Logger ?? (ILogger)NullLogger.Instance, cancellationToken).ConfigureAwait(false);

        if (merged.PausedFor is { } source)
        {
            Queues.Wait(context.ItemKey, context.ItemName, context.ItemPath, source);
            Logger?.LogInformation("{SeriesName} waits for {Source}, which is paused, and keeps what it has until then", info.Name, source);

            return new MetadataResult<Series>();
        }

        result.HasMetadata = merged.HasData;

        return result;
    }

    /// <summary>
    /// Splits a mapping to AniList or MyAnimeList, "anilist:123" or "mal:123".
    /// </summary>
    /// <param name="mapping">The mapping.</param>
    /// <returns>The source and the id, or <c>null</c> for an AniDB id or anything else.</returns>
    internal static (string Source, string Prefix, string Id)? LinkParts(string? mapping)
    {
        foreach (var (source, prefix) in new[] { (SourceIds.AniList, "anilist"), (SourceIds.Mal, "mal") })
        {
            if (mapping is not null && mapping.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase))
            {
                var id = mapping[(prefix.Length + 1)..];

                return id.Length > 0 && id.All(char.IsAsciiDigit) ? (source, prefix, id) : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the AniList or MyAnimeList mapping an item's own ids make, AniList's first.
    /// </summary>
    /// <param name="ids">The item's ids.</param>
    /// <returns>"anilist:123", "mal:123", or <c>null</c>.</returns>
    internal static string? LinkFromIds(IReadOnlyDictionary<string, string> ids)
        => LinksFromIds(ids).FirstOrDefault();

    /// <summary>
    /// Gets every AniList and MyAnimeList link an item's own ids make, AniList's first.
    /// </summary>
    /// <remarks>
    /// AniList is read first because it is the id the other sources know a show by, and because
    /// the AniBridge mappings link it to the right entry more often than they link a
    /// MyAnimeList id: now and then they link one to another season's entry.
    /// </remarks>
    /// <param name="ids">The item's ids.</param>
    /// <returns>The links, such as "anilist:123" and "mal:456".</returns>
    internal static IReadOnlyList<string> LinksFromIds(IReadOnlyDictionary<string, string> ids)
    {
        var links = new List<string>(2);

        foreach (var (source, prefix) in new[] { (SourceIds.AniList, "anilist"), (SourceIds.Mal, "mal") })
        {
            if (ids.GetValueOrDefault(source) is { Length: > 0 } id && id.All(char.IsAsciiDigit))
            {
                links.Add(prefix + ":" + id);
            }
        }

        return links;
    }

    /// <summary>
    /// Gets the AniDB entry the mappings link an AniList or MyAnimeList entry to, so that an
    /// item mapped to one moves to AniDB once AniDB lists it and the mappings catch up.
    /// </summary>
    private async Task<string?> AniDbForLink(string link, CancellationToken cancellationToken)
        => LinkParts(link) is { } parts
            ? await AniBridgeMappings.ResolveSeriesIdByLink(_appPaths, parts.Prefix, parts.Id, Logger ?? (ILogger)NullLogger.Instance, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>
    /// Fills a show AniDB has no entry for from the AniList or MyAnimeList entry it is mapped
    /// to, and from every other source in the order the owner set.
    /// </summary>
    /// <param name="link">"anilist:123" or "mal:123".</param>
    /// <param name="info">The lookup info.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result; empty when the item waits for a paused source.</returns>
    private async Task<MetadataResult<Series>> GetLinkedMetadata(string link, SeriesInfo info, CancellationToken cancellationToken)
    {
        var (source, _, id) = LinkParts(link)!.Value;
        var result = new MetadataResult<Series> { Item = new Series() };
        result.Item.SetProviderId(source, id);

        var context = new FetchContext
        {
            ItemKey = link,
            ItemName = info.Name ?? string.Empty,
            ItemPath = info.Path ?? string.Empty,
            Year = GetLookupYear(info),
            Language = info.MetadataLanguage ?? "en"
        };

        foreach (var (provider, value) in info.ProviderIds)
        {
            context.Ids[provider] = value;
        }

        context.Ids[source] = id;

        var merged = await SeriesMerge.Apply(result, SourceOutcome.Missing, context, Logger ?? (ILogger)NullLogger.Instance, cancellationToken).ConfigureAwait(false);

        if (merged.PausedFor is { } paused)
        {
            Queues.Wait(context.ItemKey, context.ItemName, context.ItemPath, paused);
            Logger?.LogInformation("{SeriesName} waits for {Source}, which is paused, and keeps what it has until then", info.Name, paused);

            return new MetadataResult<Series>();
        }

        Logger?.LogInformation("{SeriesName} has no AniDB entry and is filled from {Source} {Id} and the other sources", info.Name, source, id);
        result.HasMetadata = merged.HasData;

        return result;
    }

    /// <summary>
    /// Reads AniDB's answer back into the shape the other sources are merged in.
    /// </summary>
    internal static AnimeRecord ToRecord(string animeId, BaseItem series, IEnumerable<PersonInfo>? people, AniDbExtras extras)
    {
        var record = new AnimeRecord
        {
            Source = SourceIds.AniDb,
            SourceId = animeId,
            EnglishTitle = series.Name,
            RomajiTitle = series.OriginalTitle,
            Overview = series.Overview,
            StartDate = series.PremiereDate?.UtcDateTime(),
            EndDate = series.EndDate?.UtcDateTime(),
            Rating = series.CommunityRating,
            Votes = extras.RatingVotes,
            FetchedUtc = DateTime.UtcNow
        };

        record.Genres.AddRange((series.Genres ?? []).Select(g => new WeightedName { Name = g }));
        record.Studios.AddRange(series.Studios ?? []);
        record.People.AddRange((people ?? []).Select(p => new PersonRecord { Name = p.Name ?? string.Empty, Role = p.Role ?? string.Empty }));
        record.Ids[SourceIds.AniDb] = animeId;

        if (extras.MalIds.Count > 0)
        {
            record.Ids[SourceIds.Mal] = extras.MalIds[0];
        }

        return record;
    }

    /// <summary>
    /// Works out which AniDB entry a show is, from whatever the library already knows of it.
    /// </summary>
    /// <param name="info">The series lookup info.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB id, or <c>null</c> when the show cannot be identified.</returns>
    private async Task<string?> Identify(SeriesInfo info, CancellationToken cancellationToken)
    {
        // A TVDB or TMDB id a provider ahead of this one has already settled on names the show
        // outright, and the mapping sources record which AniDB entry that id is. It is tried
        // first because a name is the weaker evidence of the two: AniDB spells a great many
        // names differently from TVDB, and where two shows do share a name the id is the only
        // thing that tells them apart. A folder naming a season is left to the name match
        // below, because the id names the whole show and would answer with its first season.
        if (!AniDbSeasonResolver.NamesASeason(info.Name))
        {
            var mapped = await AniDbMappings.ResolveSeriesId(
                _appPaths,
                info.ProviderIds.GetValueOrDefault(nameof(MetadataProvider.Tvdb)),
                info.ProviderIds.GetValueOrDefault(nameof(MetadataProvider.Tmdb)),
                Logger ?? (ILogger)NullLogger.Instance,
                cancellationToken).ConfigureAwait(false);

            if (mapped != null)
            {
                Logger?.LogInformation(
                    "{SeriesName} is AniDB anime {AnimeId}, which {Source} files under {Provider} series {ProviderId}",
                    info.Name,
                    mapped.AnimeId,
                    mapped.Source,
                    mapped.Provider,
                    mapped.ProviderId);

                return mapped.AnimeId;
            }
        }

        // The folder is what the user named, and it still says which show this is where the name
        // on the item no longer does.
        var folderName = Path.GetFileName(info.Path?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var truncated = IsTruncation(info.Name, folderName);

        if (truncated)
        {
            Logger?.LogInformation(
                "{SeriesName} is what is left of the folder name {FolderName} once it was cut short, so the folder is searched instead. A name with no letters in it matches almost any anime, this search being a fuzzy one, and matching the wrong one would settle the show for good",
                info.Name,
                folderName);
        }

        // Folders carry notes no title has - "(Fall)", "[BD]" - and the fuzzy search reads them
        // as part of the name.
        var withoutNotes = Queues.StripNotes(folderName ?? info.Name ?? string.Empty);

        // Every spelling is tried whole before any is searched fuzzily. The fuzzy search of one
        // spelling settles on whatever starts alike: "The Beginning After the End (Spring)"
        // found its own second season, and a name cut short to "The Beginning" found a Tiger &
        // Bunny film, where the folder without its note is a title AniDB has exactly.
        var matched = await ExactMatch(info, folderName, withoutNotes, truncated, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(matched) && !string.IsNullOrEmpty(info.Name) && !truncated)
        {
            matched = await Equals_check.XmlFindId(info.Name, GetLookupYear(info), cancellationToken).ConfigureAwait(false);
        }

        // The name searched above is the item's, which is whatever provider reached it first,
        // and a provider that matched the wrong show has already renamed it to that show. The
        // folder gets an attempt of its own, under the year written into it rather than the
        // wrong show's.
        if (string.IsNullOrEmpty(matched)
            && !string.IsNullOrEmpty(folderName)
            && !string.Equals(folderName, info.Name, StringComparison.OrdinalIgnoreCase))
        {
            matched = await Equals_check.XmlFindId(folderName, YearInName(folderName) ?? GetLookupYear(info), cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(matched))
            {
                Logger?.LogInformation(
                    "{SeriesName} could not be matched under that name, but its folder {FolderName} is AniDB anime {AnimeId}. The name on the item is another provider's, and that provider matched a different show",
                    info.Name,
                    folderName,
                    matched);
            }
        }

        // Tried last, so a note that is part of a real title still matches that title first.
        if (string.IsNullOrEmpty(matched)
            && !string.IsNullOrEmpty(withoutNotes)
            && !string.Equals(withoutNotes, folderName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(withoutNotes, info.Name, StringComparison.OrdinalIgnoreCase))
        {
            matched = await Equals_check.XmlFindId(withoutNotes, YearInName(folderName ?? string.Empty) ?? GetLookupYear(info), cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(matched))
            {
                Logger?.LogInformation("{SeriesName} is AniDB anime {AnimeId} once the notes in its folder name are left out ({Name})", info.Name, matched, withoutNotes);
            }
        }

        if (string.IsNullOrEmpty(matched))
        {
            Logger?.LogInformation(
                "No AniDB entry could be identified for {SeriesName} (folder {FolderName}). Where two shows share a name, the year in the folder name is what tells them apart, and a TVDB id on the show lets the anime list answer instead",
                info.Name,
                folderName);

            return null;
        }

        // Only a name match is walked back. An id set by hand names the entry to use, the
        // TVDB route already answers with the show's first entry, and the season provider asks
        // for a season's own entry by id.
        var searchedName = info.Name ?? folderName;

        // The list is asked before AniDB's own relations are walked. A name match lands on a
        // later season more often than it looks: AniDB disambiguates a second season by
        // appending its year, exactly as it does a remake, so a show whose seasons all aired in
        // one year matches its own sequel. The list records which season every entry fills, so
        // it settles this for nothing, where each hop of the relation walk costs a request.
        if (!AniDbSeasonResolver.NamesASeason(searchedName))
        {
            var listedFirst = await AniDbMappings.ResolveFirstSeason(
                _appPaths,
                matched,
                Logger ?? (ILogger)NullLogger.Instance,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(listedFirst))
            {
                Logger?.LogInformation(
                    "{SeriesName} matched AniDB anime {MatchedId}, which the mapping sources file as a later season. The show begins at anime {AnimeId}, which is used instead",
                    searchedName,
                    matched,
                    listedFirst);

                return listedFirst;
            }
        }

        try
        {
            return await AniDbSeasonResolver.ResolveFirstSeasonId(_appPaths, matched, searchedName, Logger, cancellationToken).ConfigureAwait(false);
        }
        catch (AniDbBannedException ex)
        {
            // Walking back to the first season asks AniDB about each entry on the way. When it
            // cannot be asked, the name match stands.
            Logger?.LogDebug("The first season of anime {AnimeId} could not be looked up ({Message}); the name match is kept", matched, ex.Message);

            return matched;
        }
    }

    /// <summary>
    /// Gets the metadata for the given AniDB id.
    /// </summary>
    /// <param name="animeId">The AniDB id.</param>
    /// <param name="info">The series lookup info.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="publishShowIds">Whether the ids the mapping sources file the show under may be written onto the result. Off for a movie read out of a show's entry, which is not that show and must not carry the ids of its seasons.</param>
    /// <returns>The metadata result.</returns>
    public async Task<MetadataResult<Series>> GetMetadataForId(
        string animeId,
        SeriesInfo info,
        CancellationToken cancellationToken,
        bool publishShowIds = true)
    {
        var result = new MetadataResult<Series>
        {
            Item = new Series(),
            HasMetadata = true
        };

        result.Item.ProviderIds.Add(ProviderNames.AniDb, animeId);

        if (publishShowIds)
        {
            await AddMappedShowIds(result.Item, info, animeId, cancellationToken).ConfigureAwait(false);
        }

        var seriesDataPath = await GetSeriesData(_appPaths, animeId, cancellationToken).ConfigureAwait(false);
        await FetchSeriesInfo(result, seriesDataPath, info.MetadataLanguage ?? "en").ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Writes the TVDB and TMDB ids the mapping sources file a show under onto it, where that is
    /// turned on. Those sites' image providers, and fanart, are keyed by them, so this is what
    /// lets them fetch artwork for a show AniDB identified.
    /// </summary>
    /// <param name="series">The show being filled in.</param>
    /// <param name="info">The lookup info, holding whatever ids the item already carries.</param>
    /// <param name="animeId">The AniDB id of the show.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    private async Task AddMappedShowIds(Series series, SeriesInfo info, string animeId, CancellationToken cancellationToken)
    {
        if (!Plugin.Instance.Configuration.PublishMappedIds)
        {
            return;
        }

        var ids = await AniDbMappings.ResolveShowIds(
            _appPaths,
            animeId,
            Logger ?? (ILogger)NullLogger.Instance,
            cancellationToken).ConfigureAwait(false);

        if (!ids.Any)
        {
            return;
        }

        AddMappedId(series, info.ProviderIds, nameof(MetadataProvider.Tvdb), ids.Tvdb);
        AddMappedId(series, info.ProviderIds, nameof(MetadataProvider.Tmdb), ids.Tmdb);

        Logger?.LogDebug(
            "AniDB anime {AnimeId} is filed under TVDB {TvdbId} and TMDB {TmdbId}, which are written onto the show so that whatever is keyed by them can fetch its artwork",
            animeId,
            ids.Tvdb,
            ids.Tmdb);
    }

    /// <summary>
    /// Writes one mapped id onto an item, leaving alone an id the item already carries: that one
    /// was either entered by hand or settled by the provider it belongs to, and either is better
    /// evidence about the item than a mapping is.
    /// </summary>
    /// <param name="item">The item being filled in.</param>
    /// <param name="known">The ids the item already carries.</param>
    /// <param name="provider">The provider whose id this is.</param>
    /// <param name="id">The id, where a source named one.</param>
    internal static void AddMappedId(IHasProviderIds item, IReadOnlyDictionary<string, string> known, string provider, string? id)
    {
        if (!string.IsNullOrEmpty(id) && string.IsNullOrEmpty(known.GetValueOrDefault(provider)))
        {
            item.ProviderIds[provider] = id;
        }
    }

#if EMBY
    /// <summary>
    /// Makes Emby's identify dialog offer the id fields <see cref="IdentifyIds"/> allows.
    /// </summary>
    /// <returns>The external id keys an identify can be given.</returns>
    public string[] GetSupportedExternalIdentifiers() => IdentifyIds();

#endif
    /// <summary>
    /// The ids an identify can be given: AniDB's while a client name is set and not rejected,
    /// MyAnimeList's while that source is on, has a client id or Jikan, and its key was not
    /// refused, AniList's (which needs no key) while that source is on. An id is useless
    /// otherwise, as the refresh after a pick could not use it.
    /// </summary>
    /// <returns>The id keys.</returns>
    internal static string[] IdentifyIds()
    {
        var ids = new List<string>(3);

        if (IsClientUsable())
        {
            ids.Add(ProviderNames.AniDb);
        }

        if (Plugin.Instance?.Configuration is { } configuration)
        {
            if (configuration.GetSource(SourceIds.Mal).Enabled
                && MalClient.IsConfigured
                && BlockStore.SourceBlock(SourceIds.Mal) is not { Reason: BlockReason.AuthFailed })
            {
                ids.Add(SourceIds.Mal);
            }

            if (configuration.GetSource(SourceIds.AniList).Enabled)
            {
                ids.Add(SourceIds.AniList);
            }
        }

        return [.. ids];
    }

    /// <inheritdoc />
    /// <remarks>
    /// Answers from the titles dump and mappings alone, with a "Local only" poster: an identify that asked
    /// AniDB for every guess would spend one paced request each, enough to earn a ban. The
    /// refresh after a pick fetches the real metadata and images.
    /// </remarks>
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
    {
        var results = new List<RemoteSearchResult>();
        var offered = new HashSet<string>(StringComparer.Ordinal);

        void Offer(string? id, string? title = null)
        {
            if (string.IsNullOrEmpty(id) || !offered.Add(id))
            {
                return;
            }

            var result = new RemoteSearchResult
            {
                Name = title ?? TitlesDump.MainTitle(_appPaths, id) ?? "AniDB " + id,
                ImageUrl = LocalOnlyImage.Url,
                SearchProviderName = ProviderNames.Display
            };
            result.SetProviderId(ProviderNames.AniDb, id);
            results.Add(result);
        }

        var allowed = IdentifyIds();

        if (allowed.Contains(ProviderNames.AniDb))
        {
            Offer(searchInfo.ProviderIds.GetValueOrDefault(ProviderNames.AniDb));
        }

        // MyAnimeList and AniList ids are looked up in the mappings, never asked of the
        // databases themselves.
        foreach (var (key, prefix) in new[] { (SourceIds.Mal, "mal"), (SourceIds.AniList, "anilist") })
        {
            if (allowed.Contains(key))
            {
                Offer(await AniBridgeMappings.ResolveSeriesIdByLink(
                    _appPaths,
                    prefix,
                    searchInfo.ProviderIds.GetValueOrDefault(key),
                    Logger ?? (ILogger)NullLogger.Instance,
                    cancellationToken).ConfigureAwait(false));
            }
        }

        // The mapping sources are the ones here that answer with a certainty rather than a
        // guess, so their answer goes first. They also settle the question the name cannot: they
        // hold anime only, so an id they do not carry belongs to something that is not an
        // anime - a live action adaptation sharing the show's name, most often - and an id they
        // do carry names the AniDB entry outright.
        var mapped = await AniDbMappings.ResolveSeriesId(
            _appPaths,
            searchInfo.ProviderIds.GetValueOrDefault(nameof(MetadataProvider.Tvdb)),
            searchInfo.ProviderIds.GetValueOrDefault(nameof(MetadataProvider.Tmdb)),
            Logger ?? (ILogger)NullLogger.Instance,
            cancellationToken).ConfigureAwait(false);

        Offer(mapped?.AnimeId);

        if (!string.IsNullOrEmpty(searchInfo.Name))
        {
            foreach (var candidate in await Queues.Candidates(searchInfo.Name, cancellationToken).ConfigureAwait(false))
            {
                Offer(candidate.AnimeId, candidate.Title);
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<HostImageResponse> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        if (LocalOnlyImage.Is(url))
        {
            return LocalOnlyImage.Response();
        }

        return await GetAniDbImage(url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads an image from AniDB's image server in its turn, and writes it to the request log.
    /// </summary>
    /// <param name="url">The image url.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The server's image response.</returns>
    internal static async Task<HostImageResponse> GetAniDbImage(string url, CancellationToken cancellationToken)
    {
        await WaitForImageSlot(cancellationToken).ConfigureAwait(false);

        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await Plugin.Instance.GetImageResponse(url, cancellationToken).ConfigureAwait(false);
            LogImageRequest(url, (int)response.StatusCode, started);

            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LogImageRequest(url, null, started, ex is TaskCanceledException ? "Timeout" : "Unreachable");
            throw;
        }
    }

    /// <summary>
    /// Writes an image download to the request log, under its own name so that it never counts
    /// as an AniDB API request.
    /// </summary>
    /// <param name="url">The image url.</param>
    /// <param name="status">The HTTP status, when there was one.</param>
    /// <param name="started">When it was sent, as a <see cref="Stopwatch"/> timestamp.</param>
    /// <param name="failure">How it failed, when it got no status.</param>
    internal static void LogImageRequest(string url, int? status, long started, string? failure = null)
        => SourceGate.LogOwnRequest(
            ImageLogSource,
            "image " + url[(url.LastIndexOf('/') + 1)..],
            string.Empty,
            failure ?? (status is >= 200 and < 300 ? "ok" : "HttpError"),
            status,
            started);

    /// <summary>
    /// Gets the path to the cached series data, downloading it when needed.
    /// </summary>
    /// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="seriesId">The AniDB series id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The path to the series data file.</returns>
    public static async Task<string> GetSeriesData(IApplicationPaths appPaths, string seriesId, CancellationToken cancellationToken)
    {
        var seriesDataPath = Path.Combine(GetSeriesDataPath(appPaths, seriesId), "series.xml");

        if (!NeedsDownload(seriesDataPath) || (IsCacheOnly(seriesId) && HasCopy(seriesDataPath)))
        {
            return seriesDataPath;
        }

        // One download per anime at a time. Every episode of a season asks for the entry it is
        // read from, and a scan refreshes them together, so without this they all find nothing
        // cached and send the same request at once - one per episode, for one anime.
        var gate = _downloadGates.GetOrAdd(seriesId, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Whoever held the gate has very likely just downloaded it.
            if (!NeedsDownload(seriesDataPath))
            {
                return seriesDataPath;
            }

            var fileInfo = new FileInfo(seriesDataPath);
            var hasCopy = fileInfo.Exists && fileInfo.Length > 0;

            // While banned, or while AniDB may not be asked at all, read the stale copy rather
            // than spend a request that would only be refused.
            if (hasCopy && (GetRemainingBanTime() > TimeSpan.Zero || !IsClientUsable()))
            {
                return seriesDataPath;
            }

            // AniDB refuses some ids outright - one it has deleted or merged away, or one set
            // by hand that was never right - and answers with an error there is nothing to
            // cache. Nothing being cached is what makes the next episode ask again, so one bad
            // id costs a request for every file in the show, every scan. The refusal is
            // remembered instead, and raised again without asking.
            if (_refusedIds.TryGetValue(seriesId, out var refusal) && DateTime.UtcNow - refusal.At < _retryAfterRefusal)
            {
                return hasCopy ? seriesDataPath : throw new InvalidOperationException(refusal.Message);
            }

            // Also kept in the database, so that a refusal outlives a restart and shows on the
            // settings page, where it can be lifted by hand.
            if (BlockStore.ItemBlock(SourceIds.AniDb, ItemKeys.ForAnime(seriesId)) is { } stored)
            {
                return hasCopy ? seriesDataPath : throw new InvalidOperationException(stored.Message);
            }

            try
            {
                await DownloadSeriesData(seriesId, seriesDataPath, PluginPaths.CacheFolder(appPaths), cancellationToken).ConfigureAwait(false);

                _refusedIds.TryRemove(seriesId, out _);
            }
            catch (AniDbBannedException ex) when (hasCopy)
            {
                // Blocked, capped or unreachable: an old answer beats none.
                Logger?.LogDebug("AniDB could not be asked for anime {AnimeId}, so its cached answer is used: {Message}", seriesId, ex.Message);
            }
            catch (AniDbRefusedException ex)
            {
                _refusedIds[seriesId] = (DateTime.UtcNow, ex.Message);

                // Held for days whatever the error: retrying sooner only spends AniDB requests.
                var block = BlockStore.Record(SourceIds.AniDb, new SourceFailure(BlockScope.Item, BlockReason.NotFound, ex.Message), ItemKeys.ForAnime(seriesId), string.Empty);

                Logger?.LogWarning(
                    "AniDB refused anime {AnimeId}: {Error}. Nothing more will be asked of that id for {RetryAfter}, so the rest of the show does not spend a request each on the same refusal",
                    seriesId,
                    ex.Message,
                    block?.ExpiresUtc is { } until ? until - DateTime.UtcNow : _retryAfterRefusal);

                if (!hasCopy)
                {
                    throw;
                }
            }
        }
        finally
        {
            gate.Release();
        }

        return seriesDataPath;
    }

    /// <summary>
    /// Whether refreshing an anime now would send AniDB a request: not while AniDB cannot be
    /// asked - banned, blocked, turned off or without a client - and not while its answer is
    /// cached and current. Used to pace scans by what they cost AniDB, not by item count.
    /// </summary>
    /// <param name="appPaths">The server paths.</param>
    /// <param name="animeId">The AniDB id.</param>
    /// <returns><c>true</c> when the refresh would ask AniDB.</returns>
    internal static bool WouldAskAniDb(IApplicationPaths appPaths, string animeId)
        => Plugin.Instance.Configuration.GetSource(SourceIds.AniDb).Enabled
            && IsClientUsable()
            && GetRemainingBanTime() <= TimeSpan.Zero
            && BlockStore.SourceBlock(SourceIds.AniDb) == null
            && NeedsDownload(Path.Combine(GetSeriesDataPath(appPaths, animeId), "series.xml"))
            && !(IsCacheOnly(animeId) && HasCachedSeriesData(appPaths, animeId));

    /// <summary>
    /// Whether an AniDB answer for the anime is on disk, however old.
    /// </summary>
    /// <param name="appPaths">The server paths.</param>
    /// <param name="animeId">The AniDB id.</param>
    /// <returns><c>true</c> when a cached document exists.</returns>
    internal static bool HasCachedSeriesData(IApplicationPaths appPaths, string animeId)
        => HasCopy(CachedSeriesDataPath(appPaths, animeId));

    /// <summary>
    /// Where the cached AniDB answer for an anime is kept.
    /// </summary>
    /// <param name="appPaths">The server paths.</param>
    /// <param name="animeId">The AniDB id.</param>
    /// <returns>The path of its series.xml.</returns>
    internal static string CachedSeriesDataPath(IApplicationPaths appPaths, string animeId)
        => Path.Combine(GetSeriesDataPath(appPaths, animeId), "series.xml");

    /// <summary>
    /// Makes lookups of the anime read its cached document, however old, rather than ask AniDB,
    /// for the time given.
    /// </summary>
    /// <param name="animeId">The AniDB id.</param>
    /// <param name="window">How long.</param>
    internal static void ReadCacheOnly(string animeId, TimeSpan window)
        => _cacheOnlyUntil[animeId] = DateTime.UtcNow + window;

    private static bool IsCacheOnly(string animeId)
    {
        if (!_cacheOnlyUntil.TryGetValue(animeId, out var until))
        {
            return false;
        }

        if (until > DateTime.UtcNow)
        {
            return true;
        }

        _cacheOnlyUntil.TryRemove(animeId, out _);
        return false;
    }

    private static bool HasCopy(string seriesDataPath)
    {
        var fileInfo = new FileInfo(seriesDataPath);

        return fileInfo.Exists && fileInfo.Length > 0;
    }

    /// <summary>
    /// Whether a refresh of the anime needs a new answer from AniDB - nothing usable cached - and
    /// AniDB is in use, whether or not it can be asked right now.
    /// </summary>
    /// <param name="appPaths">The server paths.</param>
    /// <param name="animeId">The AniDB id.</param>
    /// <returns><c>true</c> when the refresh depends on AniDB answering.</returns>
    internal static bool NeedsAniDbAnswer(IApplicationPaths appPaths, string animeId)
        => Plugin.Instance.Configuration.GetSource(SourceIds.AniDb).Enabled
            && IsClientUsable()
            && NeedsDownload(CachedSeriesDataPath(appPaths, animeId))
            && !(IsCacheOnly(animeId) && HasCachedSeriesData(appPaths, animeId));

    /// <summary>
    /// Whether AniDB is held back for now: banned, blocked, or resting or capped on the Sources
    /// tab. Work that needs it waits rather than filling shows with stand-in values.
    /// </summary>
    /// <returns><c>true</c> when AniDB may not be asked now.</returns>
    internal static bool IsHeldBack()
        => GetRemainingBanTime() > TimeSpan.Zero
            || BlockStore.SourceBlock(SourceIds.AniDb) != null
            || SourceGate.IsCapped(SourceIds.AniDb);

    /// <summary>
    /// Whether a refresh while AniDB is held back would leave stand-in values: AniDB is first
    /// for a field taken from one source. Where it only adds to combined fields - genres,
    /// rating, cast - or comes after other sources, missing it costs no stand-ins.
    /// </summary>
    /// <returns><c>true</c> when AniDB leads such a field.</returns>
    internal static bool LeadsASingleSourceField()
    {
        var configuration = Plugin.Instance.Configuration;

        return new[] { DataKind.Titles, DataKind.Overview, DataKind.Details }
            .Any(kind => string.Equals(configuration.GetPriority(kind).FirstOrDefault(), SourceIds.AniDb, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a refresh of the anime should wait for AniDB: it needs a new answer, AniDB is held
    /// back, and going ahead would leave stand-in values.
    /// </summary>
    /// <param name="appPaths">The server paths.</param>
    /// <param name="animeId">The AniDB id.</param>
    /// <returns><c>true</c> when the refresh should wait.</returns>
    internal static bool ShouldWaitForAniDb(IApplicationPaths appPaths, string animeId)
        => IsHeldBack() && LeadsASingleSourceField() && NeedsAniDbAnswer(appPaths, animeId);

    /// <summary>
    /// Whether the cached document of an anime has to be downloaded before it can be read.
    /// </summary>
    /// <param name="seriesDataPath">The path of the cached document.</param>
    /// <returns><c>true</c> when there is no usable copy on disk.</returns>
    private static bool NeedsDownload(string seriesDataPath)
    {
        var fileInfo = new FileInfo(seriesDataPath);

        return !fileInfo.Exists
            || fileInfo.Length == 0
            || DateTime.UtcNow - fileInfo.LastWriteTimeUtc > RefreshScope.AniDbMaxAge;
    }

    /// <summary>
    /// Waits until the AniDB rate limit allows another request to be made.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static Task WaitForRequestSlot(CancellationToken cancellationToken)
        => WaitForSlot(true, cancellationToken);

    /// <summary>
    /// Waits for the slot an image download takes. Images come from AniDB's image server rather
    /// than from its API, which counts and bans separately, so a download waits its turn like
    /// anything else but is not held back by a ban on the API. Otherwise a poster the API has
    /// already described is lost for as long as the ban lasts, which is what leaves a show
    /// identified during one without its artwork until someone picks it by hand.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static Task WaitForImageSlot(CancellationToken cancellationToken)
        => WaitForSlot(false, cancellationToken);

    private static async Task WaitForSlot(bool countsAgainstTheApi, CancellationToken cancellationToken)
    {
        if (countsAgainstTheApi)
        {
            ThrowIfBanned();
        }

        Interlocked.Increment(ref _queuedRequests);

        try
        {
            // The gate is held across the wait so that concurrent callers queue behind each
            // other rather than all sleeping in parallel and then firing at the same moment.
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Task.Delay may fire fractionally early, so re-check the interval.
                for (var remaining = GetRemainingInterval(); remaining > TimeSpan.Zero; remaining = GetRemainingInterval())
                {
                    await Task.Delay(remaining < _minimumDelay ? _minimumDelay : remaining, cancellationToken).ConfigureAwait(false);
                }

                // A caller ahead in the queue may have been banned while this one waited.
                if (countsAgainstTheApi)
                {
                    ThrowIfBanned();

                    try
                    {
                        SourceGate.CountOwnRequest(SourceIds.AniDb);
                    }
                    catch (SourceBlockedException ex)
                    {
                        // The caps and bursts on the Sources tab hold AniDB back like a short block.
                        throw new AniDbUnavailableException(ex.Message, ex) { RetryAfter = ex.RetryAfter ?? TimeSpan.FromHours(1) };
                    }
                }

                _nextRequestTimestamp = Stopwatch.GetTimestamp() + GetRequestIntervalTicks();

                Interlocked.Increment(ref _requestsSent);
                Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks);
            }
            finally
            {
                _requestGate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _queuedRequests);
        }
    }

    /// <summary>
    /// How the plugin currently stands with AniDB, for the status the configuration page
    /// shows. A scan that looks stalled is almost always one queued behind the rate limit or
    /// waiting out a ban, and neither is visible from the library screen.
    /// </summary>
    /// <returns>What is left of any ban, how many requests are waiting for a slot, how long until the next may be sent, how many have been sent since the server started, and when the last one went out.</returns>
    internal static (TimeSpan BanRemaining, int Queued, TimeSpan UntilNextRequest, long Sent, DateTime? LastSentUtc) GetRequestStatus()
    {
        var lastTicks = Interlocked.Read(ref _lastRequestTicks);
        var untilNext = GetRemainingInterval();

        return (
            GetRemainingBanTime(),
            Volatile.Read(ref _queuedRequests),
            untilNext > TimeSpan.Zero ? untilNext : TimeSpan.Zero,
            Interlocked.Read(ref _requestsSent),
            lastTicks == 0 ? null : new DateTime(lastTicks, DateTimeKind.Utc));
    }

    /// <summary>
    /// Gets the time remaining on the current AniDB ban, or <see cref="TimeSpan.Zero"/>
    /// when the plugin is not currently banned.
    /// </summary>
    /// <returns>The remaining ban time.</returns>
    internal static TimeSpan GetRemainingBanTime()
    {
        lock (_banLock)
        {
            var remaining = _banUntilUtc - DateTime.UtcNow;

            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Restores the ban recorded by a previous session, so that restarting the server does
    /// not hand a banned client a fresh allowance of requests.
    /// </summary>
    /// <param name="configuration">The plugin configuration holding the persisted state.</param>
    internal static void RestoreBanState(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        lock (_banLock)
        {
            _currentBanBackoff = configuration.AniDbBanBackoffMinutes > 0
                ? TimeSpan.FromMinutes(Math.Min(configuration.AniDbBanBackoffMinutes, _maximumBanBackoff.TotalMinutes))
                : _initialBanBackoff;

            var remaining = configuration.AniDbBannedUntilUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                _banUntilUtc = DateTime.MinValue;
                _banActive = false;

                return;
            }

            // Guard against a persisted time that a clock change has pushed implausibly far
            // out; never wait longer than a ban could actually last.
            if (remaining > _maximumBanBackoff)
            {
                remaining = _maximumBanBackoff;
            }

            _banUntilUtc = DateTime.UtcNow + remaining;
            _banActive = true;
            _resumeLogged = false;

            Logger?.LogWarning(
                "An AniDB ban recorded before the last restart is still in force. AniDB requests stay paused for {RetryAfter}",
                remaining);
        }
    }

    /// <summary>
    /// Records that AniDB reported a ban and returns how long it will be waited out for.
    /// </summary>
    /// <returns>The backoff applied for this ban.</returns>
    private static TimeSpan RegisterBan()
    {
        lock (_banLock)
        {
            // Requests already in flight when the ban began are refused too; they are the same
            // ban, not a new one to double the backoff for.
            if (_banActive && _banUntilUtc > DateTime.UtcNow && DateTime.UtcNow - _banRegisteredUtc < TimeSpan.FromMinutes(1))
            {
                return _banUntilUtc - DateTime.UtcNow;
            }

            _banRegisteredUtc = DateTime.UtcNow;
            var backoff = _currentBanBackoff;

            // A ban that comes once the day's requests reach the daily limit is that limit, which
            // retrying sooner cannot lift and may only extend: wait the whole day at once.
            var sentToday = SourceGate.Counts().TryGetValue(SourceIds.AniDb, out var counts) ? counts.LastDay : 0;
            if (sentToday >= SourceDefaults.AniDbDailyLimitGuess)
            {
                backoff = _maximumBanBackoff;
                Logger?.LogWarning(
                    "AniDB banned this client after {Count} requests in the last 24 hours, which is past its daily limit of about {Limit}. Waiting a full {RetryAfter} before asking again",
                    sentToday,
                    SourceDefaults.AniDbDailyLimitGuess,
                    backoff);
            }

            _banUntilUtc = DateTime.UtcNow + backoff;

            var escalated = backoff + backoff;
            _currentBanBackoff = escalated > _maximumBanBackoff ? _maximumBanBackoff : escalated;

            if (_banActive)
            {
                Logger?.LogWarning(
                    "AniDB is still refusing requests. All AniDB requests remain paused, now for a further {RetryAfter}",
                    backoff);
            }
            else
            {
                Logger?.LogWarning(
                    "AniDB has banned this client. Pausing all AniDB requests for {RetryAfter}. Cached metadata will continue to be used in the meantime",
                    backoff);
            }

            _banActive = true;
            _resumeLogged = false;
            PersistBanState();
            BlockStore.Set(SourceIds.AniDb, BlockReason.Banned, "AniDB banned this client.", _banUntilUtc, (int)backoff.TotalMinutes);

            return backoff;
        }
    }

    /// <summary>
    /// Clears the ban state after AniDB served a valid response.
    /// </summary>
    private static void RegisterSuccess(DateTime requestedAtUtc)
    {
        lock (_banLock)
        {
            // A request sent before the latest ban says nothing about whether it has lifted.
            if (requestedAtUtc < _banRegisteredUtc)
            {
                return;
            }

            BlockStore.RecordSuccess(SourceIds.AniDb);

            // Every successful download lands here, so do nothing unless a ban was in
            // effect. Otherwise the configuration would be rewritten once per series.
            if (!_banActive && _banUntilUtc == DateTime.MinValue && _currentBanBackoff == _initialBanBackoff)
            {
                return;
            }

            if (_banActive)
            {
                Logger?.LogInformation("AniDB accepted a request again, so the ban has lifted. Resuming normal metadata fetching");
            }

            _banUntilUtc = DateTime.MinValue;
            _currentBanBackoff = _initialBanBackoff;
            _banActive = false;
            _resumeLogged = false;
            PersistBanState();
        }
    }

    /// <summary>
    /// Forgets the ban and every refusal kept in memory, after the owner lifted them by hand.
    /// </summary>
    /// <param name="animeId">The anime whose refusal was lifted, or <c>null</c> for the ban.</param>
    internal static void Lift(string? animeId)
    {
        if (!string.IsNullOrEmpty(animeId))
        {
            _refusedIds.TryRemove(animeId, out _);

            return;
        }

        lock (_banLock)
        {
            _banUntilUtc = DateTime.MinValue;
            _currentBanBackoff = _initialBanBackoff;
            _banActive = false;
            _resumeLogged = false;
            PersistBanState();
        }

        Logger?.LogInformation("The AniDB block was lifted by hand. AniDB requests resume");
    }

    private static void ThrowIfBanned()
    {
        GetUsableClient();

        // Other refusals than a ban - a server down, a rate limit - stop the queue as well.
        if (BlockStore.SourceBlock(SourceIds.AniDb) is { Reason: not BlockReason.Banned } block)
        {
            throw new AniDbUnavailableException("AniDB is blocked (" + block.Reason + "): " + block.Message)
            {
                RetryAfter = block.ExpiresUtc is { } until ? until - DateTime.UtcNow : TimeSpan.MaxValue,
                ClientRejected = block.Reason == BlockReason.ClientRejected
            };
        }

        var remaining = GetRemainingBanTime();
        if (remaining <= TimeSpan.Zero)
        {
            // The backoff has elapsed. Whether the ban has actually lifted is only known
            // once a request succeeds, so announce the retry once rather than per request.
            if (Volatile.Read(ref _banActive))
            {
                lock (_banLock)
                {
                    if (_banActive && !_resumeLogged)
                    {
                        _resumeLogged = true;
                        Logger?.LogInformation("The AniDB ban backoff has elapsed. Retrying AniDB requests");
                    }
                }
            }

            return;
        }

        throw new AniDbBannedException(
            string.Format(CultureInfo.InvariantCulture, "AniDB has banned this client; no request will be sent for another {0}.", remaining))
        {
            RetryAfter = remaining
        };
    }

    /// <summary>
    /// Writes the current ban state to the plugin configuration. Must be called under
    /// <see cref="_banLock"/>.
    /// </summary>
    private static void PersistBanState()
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        try
        {
            plugin.Configuration.AniDbBannedUntilUtc = _banUntilUtc;
            plugin.Configuration.AniDbBanBackoffMinutes = (int)_currentBanBackoff.TotalMinutes;
            plugin.SaveConfiguration();
        }
        catch (Exception ex)
        {
            // Losing the record only costs the ban its ability to outlive a restart, which
            // must not fail the request that discovered it.
            Logger?.LogWarning(ex, "Could not persist the AniDB ban state, so it will not survive a restart");
        }
    }

    /// <summary>
    /// Gets the client name and version AniDB is asked with, or throws when AniDB may not be
    /// asked: no name is set, or AniDB has already turned the one that is away.
    /// </summary>
    /// <returns>The client name and version.</returns>
    internal static (string Name, int Version) GetUsableClient()
    {
        var configuration = Plugin.Instance?.Configuration;

        if (configuration is null || !configuration.AniDbConfigured)
        {
            throw new AniDbUnavailableException("No AniDB client name is set, so AniDB is not asked anything. Register a client on AniDB and enter its name in the AniMeta+ settings.")
            {
                RetryAfter = TimeSpan.MaxValue
            };
        }

        if (!configuration.GetSource(SourceIds.AniDb).Enabled)
        {
            throw new AniDbUnavailableException("AniDB is turned off on the Sources tab, so it is not asked anything.")
            {
                RetryAfter = TimeSpan.MaxValue
            };
        }

        var client = ClientKey(configuration.AniDbClientName, configuration.AniDbClientVersion);

        if (string.Equals(configuration.AniDbRejectedClient, client, StringComparison.OrdinalIgnoreCase))
        {
            throw new AniDbUnavailableException("AniDB rejected the client name " + configuration.AniDbClientName + " version " + configuration.AniDbClientVersion.ToString(CultureInfo.InvariantCulture) + ". Check its registration, or enter another one.")
            {
                RetryAfter = TimeSpan.MaxValue,
                ClientRejected = true
            };
        }

        return (configuration.AniDbClientName, configuration.AniDbClientVersion);
    }

    /// <summary>
    /// Gets a value indicating whether AniDB may be asked at all, without throwing.
    /// </summary>
    /// <returns><c>true</c> when a client name is set and AniDB has not rejected it.</returns>
    internal static bool IsClientUsable()
    {
        var configuration = Plugin.Instance?.Configuration;

        return configuration is not null
            && configuration.AniDbConfigured
            && configuration.GetSource(SourceIds.AniDb).Enabled
            && !string.Equals(configuration.AniDbRejectedClient, ClientKey(configuration.AniDbClientName, configuration.AniDbClientVersion), StringComparison.OrdinalIgnoreCase);
    }

    private static string ClientKey(string name, int version)
        => name + "/" + version.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Remembers that AniDB turned a client away, until the name or version is changed.
    /// </summary>
    /// <param name="name">The client name.</param>
    /// <param name="version">The client version.</param>
    /// <param name="error">What AniDB said.</param>
    private static void RegisterClientRejected(string name, int version, string error)
    {
        Logger?.LogError(
            "AniDB rejected the client name {ClientName} version {ClientVersion}: {Error}. AniDB is not asked anything more until another name or version is entered",
            name,
            version,
            error);

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return;
        }

        try
        {
            plugin.Configuration.AniDbRejectedClient = ClientKey(name, version);
            plugin.SaveConfiguration();
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "Could not persist that AniDB rejected the client name");
        }
    }

    /// <summary>
    /// Sends one request for a small, well known anime with the configured client, so that the
    /// settings page can tell whether the name works. Goes through the same queue and ban
    /// handling as every other request.
    /// </summary>
    /// <param name="appPaths">The server paths.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal static async Task TestClient(IApplicationPaths appPaths, CancellationToken cancellationToken)
    {
        // A new name or version gets a fresh chance, even one rejected before.
        var configuration = Plugin.Instance.Configuration;
        if (!string.IsNullOrEmpty(configuration.AniDbRejectedClient)
            && !string.Equals(configuration.AniDbRejectedClient, ClientKey(configuration.AniDbClientName, configuration.AniDbClientVersion), StringComparison.OrdinalIgnoreCase))
        {
            configuration.AniDbRejectedClient = string.Empty;
            Plugin.Instance.SaveConfiguration();
        }

        const string TestAnimeId = "1";
        var seriesDataPath = Path.Combine(GetSeriesDataPath(appPaths, TestAnimeId), "series.xml");

        await DownloadSeriesData(TestAnimeId, seriesDataPath, PluginPaths.CacheFolder(appPaths), cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan GetRemainingInterval()
        => Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), _nextRequestTimestamp);

    /// <summary>
    /// Gets the configured gap between two AniDB requests, in monotonic clock ticks. Read per
    /// request so that changing the setting takes effect without a restart.
    /// </summary>
    /// <returns>The request interval in <see cref="Stopwatch"/> ticks.</returns>
    private static long GetRequestIntervalTicks()
        => (long)(Stopwatch.Frequency * (GetRequestIntervalMs() / 1000d));

    /// <summary>
    /// Gets the gap kept between two AniDB requests: AniDB's gap on the Sources tab, or the
    /// General tab value an older configuration still holds, whichever is larger.
    /// </summary>
    /// <returns>The gap in milliseconds.</returns>
    internal static int GetRequestIntervalMs()
    {
        var configuration = Plugin.Instance?.Configuration;

        return configuration is null
            ? PluginConfiguration.MinimumRequestIntervalMs
            : Math.Max(configuration.RequestIntervalMs, configuration.GetSource(SourceIds.AniDb).RequestIntervalMs);
    }

    private async Task FetchSeriesInfo(MetadataResult<Series> result, string seriesDataPath, string preferredMetadataLangauge)
    {
        var series = result.Item;
        var settings = new XmlReaderSettings
        {
            Async = true,
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            ValidationType = ValidationType.None
        };

        using (var streamReader = File.Open(seriesDataPath, FileMode.Open, FileAccess.Read))
        using (var reader = XmlReader.Create(streamReader, settings))
        {
            await reader.MoveToContentAsync().ConfigureAwait(false);

            if (string.Equals(reader.GetAttribute("restricted"), "true", StringComparison.OrdinalIgnoreCase))
            {
                series.OfficialRating = AdultOfficialRating;
            }

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    switch (reader.Name)
                    {
                        case "startdate":
                            var val = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                            if (!string.IsNullOrWhiteSpace(val))
                            {
                                // AniDB reports a calendar date with no time and no zone.
                                // AssumeUniversal keeps it as written rather than reading it
                                // as the server's local time and shifting it by that offset.
                                if (DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime date))
                                {
                                    series.PremiereDate = date;
                                    series.ProductionYear = date.Year;
                                }
                            }

                            break;


                        case "enddate":
                            var endDate = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                            if (!string.IsNullOrWhiteSpace(endDate))
                            {
                                // AniDB reports a calendar date with no time and no zone.
                                // AssumeUniversal keeps it as written rather than reading it
                                // as the server's local time and shifting it by that offset.
                                if (DateTime.TryParse(endDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime date))
                                {
                                    series.EndDate = date;
                                }
                            }

                            break;

                        case "titles":
                            using (var subtree = reader.ReadSubtree())
                            {
                                var (title, originalTitle) = await ParseTitle(subtree, preferredMetadataLangauge).ConfigureAwait(false);
                                if (!string.IsNullOrEmpty(title))
                                {
                                    series.Name = Plugin.Instance.Configuration.AniDbReplaceGraves
                                        ? title.Replace('`', '\'')
                                        : title;
                                }

                                if (!string.IsNullOrEmpty(originalTitle))
                                {
                                    series.OriginalTitle = Plugin.Instance.Configuration.AniDbReplaceGraves
                                        ? originalTitle.Replace('`', '\'')
                                        : originalTitle;
                                }
                            }

                            break;

                        case "creators":
                            using (var subtree = reader.ReadSubtree())
                            {
                                await ParseCreators(result, subtree).ConfigureAwait(false);
                            }

                            break;

                        case "description":
                            var description = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                            series.Overview = AniDbDescription.Clean(description.TrimStart('*').Trim());

                            break;

                        case "ratings":
                            using (var subtree = reader.ReadSubtree())
                            {
                                ParseRatings(series, subtree);
                            }

                            break;

                        case "characters":
                            using (var subtree = reader.ReadSubtree())
                            {
                                await ParseActors(result, subtree).ConfigureAwait(false);
                            }

                            break;

                        case "tags":
                            using (var subtree = reader.ReadSubtree())
                            {
                                await ParseTags(series, subtree).ConfigureAwait(false);
                            }

                            break;

                        case "resources":
                            using (var subtree = reader.ReadSubtree())
                            {
                                if (ParseAnnId(subtree) is { } annId)
                                {
                                    series.SetProviderId(SourceIds.Ann, annId);
                                }
                            }

                            break;
                    }
                }
            }
        }

        GenreHelper.CleanupGenres(series);
    }

    private static bool IsIgnoredTag(int tagId)
    {
        if (IgnoredTagIds.Contains(tagId))
        {
            return true;
        }

        return !Plugin.Instance.Configuration.IncludeAdultTags && AdultTagIds.Contains(tagId);
    }

    private static async Task ParseTags(Series series, XmlReader reader)
    {
        var configuration = Plugin.Instance.Configuration;
        var blacklist = GetTagBlacklist(configuration.TagBlacklist);
        var genres = new List<GenreInfo>();

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "tag")
            {
                if (!int.TryParse(reader.GetAttribute("weight"), CultureInfo.InvariantCulture, out int weight))
                {
                    weight = 0;
                }

                // AniDB marks the tags it shows in the infobox on the anime's page. Read
                // before the subtree, which moves off the element the attributes are on.
                var infobox = string.Equals(reader.GetAttribute("infobox"), "true", StringComparison.OrdinalIgnoreCase);

                if (int.TryParse(reader.GetAttribute("id"), CultureInfo.InvariantCulture, out int id) && IsIgnoredTag(id))
                {
                    continue;
                }

                if (int.TryParse(reader.GetAttribute("parentid"), CultureInfo.InvariantCulture, out int parentId)
                    && IsIgnoredTag(parentId))
                {
                    continue;
                }

                using var tagSubtree = reader.ReadSubtree();
                while (await tagSubtree.ReadAsync().ConfigureAwait(false))
                {
                    if (tagSubtree.NodeType == XmlNodeType.Element && tagSubtree.Name == "name")
                    {
                        var name = await tagSubtree.ReadElementContentAsStringAsync().ConfigureAwait(false);

                        // Decided before any filter: the rating is what parental controls act
                        // on, and it must not depend on how the tag list was narrowed.
                        if (string.Equals(name, "18 restricted", StringComparison.OrdinalIgnoreCase))
                        {
                            series.OfficialRating = AdultOfficialRating;
                        }

                        if (weight >= configuration.MinimumTagWeight
                            && (infobox || !configuration.InfoboxTagsOnly)
                            && !blacklist.Contains(name))
                        {
                            genres.Add(new GenreInfo { Name = name, Weight = weight });
                        }
                    }
                }
            }
        }

        // Descending: the list is later trimmed to the first MaxGenres entries, which must
        // be the ones AniDB weighted highest.
        var ordered = genres.OrderByDescending(g => g.Weight).Select(g => g.Name).ToArray();

        series.Genres = ordered;

        // Every tag that got this far is kept as a tag, whether or not it also names a
        // genre. Which of them become genres is GenreHelper's business, and a tag list
        // assembled there would vanish whenever genres were turned off.
        series.Tags = [.. series.Tags.Concat(ordered).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static HashSet<string> GetTagBlacklist(string value)
    {
        var blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in value.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            blacklist.Add(name);
        }

        return blacklist;
    }

    private static async Task ParseActors(MetadataResult<Series> series, XmlReader reader)
    {
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "character")
                {
                    using var subtree = reader.ReadSubtree();
                    await ParseActor(series, subtree).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task ParseActor(MetadataResult<Series> series, XmlReader reader)
    {
        string? actor = null;
        string? actorPicture = null;
        string? actorId = null;
        string? character = null;
        string? characterPicture = null;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.Name)
                {
                    case "name":
                        character = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;

                    case "picture":
                        characterPicture = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;

                    case "seiyuu":
                        // Read before the content, which moves off the element these are on.
                        actorPicture = reader.GetAttribute("picture");
                        actorId = reader.GetAttribute("id");
                        actor = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;
                }
            }
        }

        // Jellyfin holds one person per credit and has no kind of its own for a character, so the
        // two swap places rather than both being listed: whichever is named takes the credit and
        // the other becomes the role it is credited with.
        var showCharacters = Plugin.Instance.Configuration.CastShowsCharacters;
        var name = showCharacters ? character : actor;
        var role = showCharacters ? actor : character;

        // A credit needs someone to name. The usual listing names the actor and so needs both,
        // an actor being worth listing only against the character they play; a character with no
        // actor recorded is still a character, and is kept where those are what is listed.
        if (string.IsNullOrEmpty(name) || (!showCharacters && string.IsNullOrEmpty(role)))
        {
            return;
        }

        // A character's AniDB id is not a creator's, and a person's id is written out as a link
        // to a creator, so a character is listed without an id rather than with one pointing at
        // whoever happens to hold that creator id.
        series.AddPerson(CreatePerson(
            ReplaceGraves(name),
            PersonKind.Actor,
            ReplaceGraves(role),
            GetPersonImageUrl(showCharacters ? characterPicture : actorPicture),
            showCharacters ? null : actorId));
    }

    /// <summary>
    /// Replaces the grave accents AniDB romanises a name with, where that is turned on.
    /// </summary>
    /// <param name="value">The name, where there is one.</param>
    /// <returns>The name as it is listed.</returns>
    [return: NotNullIfNotNull(nameof(value))]
    private static string? ReplaceGraves(string? value)
        => Plugin.Instance.Configuration.AniDbReplaceGraves ? value?.Replace('`', '\'') : value;

    private static void ParseRatings(Series series, XmlReader reader)
    {
        if (!Plugin.Instance.Configuration.ImportCommunityRating)
        {
            return;
        }

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "permanent")
                {
                    if (float.TryParse(
                        reader.ReadElementContentAsString(),
                        NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out float rating))
                    {
                        series.CommunityRating = (float)Math.Round(rating, 1);
                    }
                }
            }
        }
    }

    private static async Task<(string? Title, string? OriginalTitle)> ParseTitle(XmlReader reader, string preferredMetadataLangauge)
    {
        var titles = new List<Title>();

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "title")
            {
                var language = reader.GetAttribute("xml:lang");
                var type = reader.GetAttribute("type");
                var name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                titles.Add(new Title
                {
                    Language = language,
                    Type = type,
                    Name = name
                });
            }
        }

        string? title = titles.Localize(Plugin.Instance.Configuration.TitlePreference, preferredMetadataLangauge)?.Name;

        // The original title is the Japanese one, that being the language the anime was made
        // in, written in romaji rather than in kanji: it is the name the show is catalogued
        // under outside Japan, and it reads to anyone, which the kanji does not. It does not
        // follow the displayed title's language.
        string? originalTitle = titles.Localize(TitlePreferenceType.JapaneseRomaji, preferredMetadataLangauge)?.Name;

        return (title, originalTitle);
    }

    private static async Task ParseCreators(MetadataResult<Series> series, XmlReader reader)
    {
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "name")
            {
                var type = reader.GetAttribute("type");
                var personId = reader.GetAttribute("id");
                var name = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);

                if (type == "Animation Work")
                {
                    series.Item.AddStudio(name);
                }
                else
                {
                    var (kind, role) = GetCreatorRole(type);

                    series.AddPerson(CreatePerson(
                       ReplaceGraves(name),
                       kind,
                       role,
                       null,
                       personId));
                }
            }
        }
    }

    private static (PersonKind Kind, string? Role) GetCreatorRole(string? type)
    {
        if (string.IsNullOrEmpty(type))
        {
            return (PersonKind.Unknown, null);
        }

        if (_creatorTypes.TryGetValue(type, out var mapped))
        {
            return mapped;
        }

        // Several AniDB types are already the name of a Jellyfin kind.
        return Enum.TryParse<PersonKind>(type, true, out var parsed)
            ? (parsed, type)
            : (PersonKind.Unknown, type);
    }

    private static string? GetPersonImageUrl(string? picture)
        => string.IsNullOrEmpty(picture) ? null : PersonImageBaseUrl + picture;

    private static PersonInfo CreatePerson(string name, PersonKind kind, string? role = null, string? imageUrl = null, string? personId = null)
    {
        // todo find nationality of person and conditionally reverse name order
        var person = new PersonInfo
        {
            Name = ReverseNameOrder(name),
            Type = HostPeople.ToHost(kind),
            Role = role,
            ImageUrl = imageUrl
        };

        if (!string.IsNullOrEmpty(personId))
        {
            person.ProviderIds[ProviderNames.AniDb] = personId;
        }

        return person;
    }

    /// <summary>
    /// The year a lookup should be pinned to, taken from whichever of the item's own year and
    /// its air date is set.
    /// </summary>
    /// <param name="info">The lookup info.</param>
    /// <returns>The year, or <c>null</c> when nothing gives one.</returns>
    private static int? GetLookupYear(ItemLookupInfo info)
        => info.Year ?? info.PremiereDate?.Year;

    /// <summary>
    /// The AniDB entry one of an item's spellings is exactly the title of: its name, its folder
    /// name, and the folder name without its notes. The name goes first unless it is the start
    /// of the folder name cut off between two words, which is a name that has lost the words
    /// telling the show apart.
    /// </summary>
    /// <param name="info">The item.</param>
    /// <param name="folderName">The name of its folder.</param>
    /// <param name="withoutNotes">The folder name without its notes.</param>
    /// <param name="truncated">Whether the name is the folder name cut short.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB id, or an empty string.</returns>
    private async Task<string> ExactMatch(SeriesInfo info, string? folderName, string withoutNotes, bool truncated, CancellationToken cancellationToken)
    {
        var folderYear = YearInName(folderName ?? string.Empty) ?? GetLookupYear(info);
        var folders = new[] { folderName, withoutNotes }
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => (Name: n!, Year: folderYear));

        var spellings = new List<(string Name, int? Year)>();
        if (!string.IsNullOrWhiteSpace(info.Name) && !truncated)
        {
            spellings.Add((info.Name, GetLookupYear(info)));
        }

        spellings = IsCutShort(info.Name, withoutNotes) ? [.. folders, .. spellings] : [.. spellings, .. folders];

        foreach (var (name, year) in spellings.DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var matched = await Equals_check.XmlFindExactId(name, year, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(matched))
            {
                if (!string.Equals(name, info.Name, StringComparison.OrdinalIgnoreCase))
                {
                    Logger?.LogInformation("{SeriesName} (folder {FolderName}) is AniDB anime {AnimeId}, which has the title {Name} exactly", info.Name, folderName, matched, name);
                }

                return matched;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Whether a name is the start of a longer one, ending where a word does.
    /// </summary>
    /// <param name="name">The name on the item.</param>
    /// <param name="longer">The folder name without its notes.</param>
    /// <returns><c>true</c> where the name is the longer one cut short between words.</returns>
    internal static bool IsCutShort(string? name, string? longer)
        => !string.IsNullOrWhiteSpace(name) && !string.IsNullOrEmpty(longer)
            && longer.Length > name.Trim().Length
            && longer.StartsWith(name.Trim(), StringComparison.OrdinalIgnoreCase)
            && !char.IsLetterOrDigit(longer[name.Trim().Length]);

    /// <summary>
    /// The Anime News Network entry AniDB links an anime to: the first identifier of its
    /// resource of type 1, which is how AniDB lists ANN among the other sites.
    /// </summary>
    /// <param name="reader">A reader over the anime's resources element.</param>
    /// <returns>The ANN id, or <c>null</c> when AniDB links none.</returns>
    internal static string? ParseAnnId(XmlReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var inAnn = false;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.Name == "resource")
            {
                inAnn = reader.GetAttribute("type") == "1";
            }
            else if (inAnn && reader.Name == "identifier")
            {
                var id = reader.ReadElementContentAsString().Trim();
                if (id.Length > 0 && id.All(char.IsAsciiDigit))
                {
                    return id;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The Anime News Network entry an anime's cached document links, read from disk with no
    /// request. The same entry <see cref="ParseAnnId"/> puts on the show.
    /// </summary>
    /// <param name="seriesXmlPath">The path of the cached <c>series.xml</c>.</param>
    /// <returns>The ANN id, or <c>null</c> when nothing is cached or AniDB links none.</returns>
    internal static string? ReadAnnId(string seriesXmlPath)
    {
        if (!File.Exists(seriesXmlPath))
        {
            return null;
        }

        try
        {
            using var reader = XmlReader.Create(seriesXmlPath, new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "resources")
                {
                    using var subtree = reader.ReadSubtree();

                    return ParseAnnId(subtree);
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            // An unreadable document is one with no link.
        }

        return null;
    }

    /// <summary>
    /// Whether the name on the item is the folder name cut short rather than a name of its own.
    /// </summary>
    /// <remarks>
    /// A show whose name begins with a number and a dot arrives here named with just that
    /// number: "2.43: Seiin High School Boys Volleyball Team" in a folder of that name reaches
    /// this as "2". Whatever cut it is not this plugin - nothing here reads a name apart at a
    /// dot - but searching what is left would match almost any anime, the search being fuzzy and
    /// a single digit appearing in thousands of titles, and a match however wrong would keep the
    /// folder from being tried at all. A name with no letter anywhere in it is the mark of such
    /// a cut: a real title of that shape - "009-1", "001", "663114" - is its folder's name
    /// whole rather than the start of it.
    /// </remarks>
    /// <param name="name">The name on the item.</param>
    /// <param name="folderName">The name of the folder holding it.</param>
    /// <returns><c>true</c> where the name is the folder name cut short.</returns>
    internal static bool IsTruncation(string? name, string? folderName)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(folderName)
            || folderName.Length <= name.Length
            || !folderName.StartsWith(name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Emby names a "Show 2" folder "Show", and the name then finds the first show, not its sequel.
        return !name.Any(char.IsLetter) || SequelNumberRegex().IsMatch(folderName[name.Length..]);
    }

    /// <summary>
    /// The year a folder name carries, as "Ranma &#189; (1989)" does. It is what tells two
    /// shows of one name apart, and it belongs to the folder rather than to whatever the item
    /// has since been named.
    /// </summary>
    /// <param name="name">The folder name.</param>
    /// <returns>The year, or <c>null</c> when the name carries none.</returns>
    private static int? YearInName(string name)
    {
        var match = TrailingYearRegex().Match(name);

        return match.Success && int.TryParse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }

    /// <summary>
    /// Reverses the order of the parts of a name.
    /// </summary>
    /// <param name="name">The name to reverse.</param>
    /// <returns>The reversed name.</returns>
    public static string ReverseNameOrder(string name)
    {
        return Enumerable.Reverse(name.Split(' ')).Aggregate(string.Empty, (n, part) => n + " " + part).Trim();
    }

    private static async Task DownloadSeriesData(string aid, string seriesDataPath, string cachePath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(seriesDataPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The series data path does not contain a directory.", nameof(seriesDataPath));
        }

        var (clientName, clientVersion) = GetUsableClient();
        var httpClient = Plugin.Instance.GetHttpClient();
        var url = string.Format(CultureInfo.InvariantCulture, _seriesQueryUrlFormat, clientName, clientVersion, aid);

        await WaitForRequestSlot(cancellationToken).ConfigureAwait(false);

        var requestedAtUtc = DateTime.UtcNow;
        var started = Stopwatch.GetTimestamp();
        var target = "anime " + aid;
        string text;
        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                SourceGate.LogOwnRequest(SourceIds.AniDb, target, ItemKeys.ForAnime(aid), "HttpError", (int)response.StatusCode, started);

                throw new SourceHttpException(
                    "AniDB answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase + CloudflareGuard.Note(SourceIds.AniDb, response),
                    response.StatusCode,
                    response.Headers.RetryAfter?.Delta);
            }

            text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            if (ex is not SourceHttpException)
            {
                SourceGate.LogOwnRequest(SourceIds.AniDb, target, ItemKeys.ForAnime(aid), ex is TaskCanceledException ? "Timeout" : "Unreachable", null, started);
            }

            // A failure that is not a ban was once forgotten as soon as it happened, so the next
            // episode asked again straight away. Whatever it means for the source as a whole is
            // remembered now, and stops the queue behind it.
            var failure = BlockStore.Classify(ex);
            var block = failure.Scope == BlockScope.Source
                ? BlockStore.Record(SourceIds.AniDb, failure)
                : BlockStore.Record(SourceIds.AniDb, failure, ItemKeys.ForAnime(aid), string.Empty);

            throw new AniDbUnavailableException("AniDB could not be asked: " + failure.Message, ex)
            {
                RetryAfter = block?.ExpiresUtc is { } until ? until - DateTime.UtcNow : TimeSpan.FromMinutes(5)
            };
        }

        text = text.Replace("&#x0;", string.Empty, StringComparison.Ordinal);

        var answered = ErrorRegex().Match(text);
        SourceGate.LogOwnRequest(
            SourceIds.AniDb,
            target,
            ItemKeys.ForAnime(aid),
            !answered.Success ? (string.IsNullOrWhiteSpace(text) ? "Banned" : "ok")
                : BannedRegex().IsMatch(answered.Value) ? "Banned"
                : ClientErrorRegex().IsMatch(answered.Value) ? "ClientRejected"
                : "Error",
            200,
            started);

        // Validate before touching the cache. AniDB answers a ban with an <error> document,
        // and overwriting good cached data with one would force another request on the next
        // scan, exactly when none must be made.
        var errorRegexMatch = ErrorRegex().Match(text);
        if (errorRegexMatch.Success)
        {
            // A client AniDB does not know, or one sent with the wrong version, is turned away
            // on every request until the name changes, so asking again only spends requests.
            if (ClientErrorRegex().IsMatch(errorRegexMatch.Value))
            {
                RegisterClientRejected(clientName, clientVersion, errorRegexMatch.Value);
                BlockStore.Record(SourceIds.AniDb, new SourceFailure(BlockScope.Source, BlockReason.ClientRejected, errorRegexMatch.Value));

                throw new AniDbUnavailableException("AniDB rejected the client name " + clientName + ": " + errorRegexMatch.Value)
                {
                    RetryAfter = TimeSpan.MaxValue,
                    ClientRejected = true
                };
            }

            // A ban is reported as either <error>Banned</error> or <error code="500">banned</error>.
            if (BannedRegex().IsMatch(errorRegexMatch.Value))
            {
                var retryAfter = RegisterBan();

                throw new AniDbBannedException(
                    string.Format(CultureInfo.InvariantCulture, "AniDB has banned this client; pausing all AniDB requests for {0}.", retryAfter))
                {
                    RetryAfter = retryAfter
                };
            }

            throw new AniDbRefusedException("AniDB API error " + errorRegexMatch.Value);
        }

        // An empty body is how the API turns a request away once it has stopped answering, so
        // it counts as a ban. Treating it as a one-off failure would let a scan carry on
        // asking, which is what turns a short refusal into a ban measured in days.
        if (string.IsNullOrWhiteSpace(text))
        {
            var retryAfter = RegisterBan();

            throw new AniDbBannedException(
                string.Format(CultureInfo.InvariantCulture, "AniDB returned an empty response for anime {0}; pausing all AniDB requests for {1}.", aid, retryAfter))
            {
                RetryAfter = retryAfter
            };
        }

        // A 200 that is not an anime document (a proxy or maintenance page) must not replace
        // the cache or count as AniDB answering.
        if (!IsAnimeDocument(text))
        {
            var failure = new SourceFailure(BlockScope.Source, BlockReason.Unavailable, "AniDB answered with something other than an anime document");
            var block = BlockStore.Record(SourceIds.AniDb, failure);

            throw new AniDbUnavailableException(failure.Message)
            {
                RetryAfter = block?.ExpiresUtc is { } until ? until - DateTime.UtcNow : TimeSpan.FromMinutes(5)
            };
        }

        RegisterSuccess(requestedAtUtc);

        // The payload is known good, so the previous cache may now be replaced. Written aside
        // and moved in, without the token, so a cancelled refresh never leaves half a file.
        Directory.CreateDirectory(directory);
        var partialPath = seriesDataPath + ".partial";
        await File.WriteAllTextAsync(partialPath, text, new UTF8Encoding(false), CancellationToken.None).ConfigureAwait(false);
        DeleteXmlFiles(directory);
        File.Move(partialPath, seriesDataPath, true);

        await ExtractEpisodes(directory, seriesDataPath).ConfigureAwait(false);
        await ExtractCast(cachePath, seriesDataPath).ConfigureAwait(false);
    }

    private static bool IsAnimeDocument(string text)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Ignore });

            return reader.MoveToContent() == XmlNodeType.Element && reader.Name == "anime";
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static void DeleteXmlFiles(string path)
    {
        try
        {
            foreach (var file in new DirectoryInfo(path)
                .EnumerateFiles("*.xml", SearchOption.AllDirectories))
            {
                file.Delete();
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing cached to remove.
        }
    }

    private static async Task ExtractEpisodes(string seriesDataDirectory, string seriesDataPath)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            ValidationType = ValidationType.None
        };

        using var streamReader = new StreamReader(seriesDataPath, Encoding.UTF8);
        using var reader = XmlReader.Create(streamReader, settings);
        await reader.MoveToContentAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "episode")
                {
                    var outerXml = await reader.ReadOuterXmlAsync().ConfigureAwait(false);
                    await SaveEpsiodeXml(seriesDataDirectory, outerXml).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task ExtractCast(string cachePath, string seriesDataPath)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            ValidationType = ValidationType.None
        };

        var cast = new List<AniDbPersonInfo>();

        using (var streamReader = new StreamReader(seriesDataPath, Encoding.UTF8))
        {
            using var reader = XmlReader.Create(streamReader, settings);
            await reader.MoveToContentAsync().ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Name == "characters")
                {
                    var outerXml = await reader.ReadOuterXmlAsync().ConfigureAwait(false);
                    cast.AddRange(ParseCharacterList(outerXml));
                }

                if (reader.NodeType == XmlNodeType.Element && reader.Name == "creators")
                {
                    var outerXml = await reader.ReadOuterXmlAsync().ConfigureAwait(false);
                    cast.AddRange(ParseCreatorsList(outerXml));
                }
            }
        }

        var serializer = new XmlSerializer(typeof(AniDbPersonInfo));
        foreach (var person in cast)
        {
            if (string.IsNullOrEmpty(person.Name))
            {
                continue;
            }

            var path = GetCastPath(person.Name, cachePath);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(path) || person.Image != null)
            {
                try
                {
                    var partialPath = path + ".partial";

                    using (var stream = File.Open(partialPath, FileMode.Create))
                    {
                        serializer.Serialize(stream, person);
                    }

                    File.Move(partialPath, path, true);
                }
                catch (IOException)
                {
                    // Another refresh is writing the same person; either copy will do.
                }
                catch (InvalidOperationException ex)
                {
                    // A name with characters XML cannot hold; skip that person, not the anime.
                    Logger?.LogDebug("The cast entry {Name} could not be cached: {Message}", person.Name, ex.Message);
                }
            }
        }
    }

    /// <summary>
    /// Gets the cached information about a person.
    /// </summary>
    /// <param name="cachePath">The cache path.</param>
    /// <param name="name">The name of the person.</param>
    /// <returns>The cached person info, or <c>null</c> when it is not cached.</returns>
    public static AniDbPersonInfo? GetPersonInfo(string cachePath, string name)
    {
        // The cache files a person under the first character of their name, so a blank one
        // has nowhere to be looked up.
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var path = GetCastPath(name, cachePath);
        var serializer = new XmlSerializer(typeof(AniDbPersonInfo));

        try
        {
            if (File.Exists(path))
            {
                var readerSettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };

                using var stream = File.OpenRead(path);
                using var reader = XmlReader.Create(stream, readerSettings);
                return serializer.Deserialize(reader) as AniDbPersonInfo;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidOperationException ex)
        {
            // What XmlSerializer throws for a document it cannot read. The file is only
            // rewritten for a person who turns up again with an image, so leaving it would
            // fail this person on every refresh. Removing it means simply not cached.
            Logger?.LogWarning(ex, "Discarding the unreadable cached person file {Path}", path);

            try
            {
                File.Delete(path);
            }
            catch (Exception deleteException) when (deleteException is IOException or UnauthorizedAccessException)
            {
                // The next refresh tries again.
            }

            return null;
        }

        return null;
    }

    private static string GetCastPath(string name, string cachePath)
    {
        name = name.ToLowerInvariant();
        return Path.Combine(cachePath, "people", name[0].ToString(), name + ".xml");
    }

    private static IEnumerable<AniDbPersonInfo> ParseCharacterList(string xml)
    {
        var doc = XDocument.Parse(xml);
        var people = new List<AniDbPersonInfo>();

        var characters = doc.Element("characters");
        if (characters != null)
        {
            foreach (var character in characters.Descendants("character"))
            {
                var seiyuu = character.Element("seiyuu");
                if (seiyuu != null)
                {
                    var person = new AniDbPersonInfo
                    {
                        Name = ReverseNameOrder(seiyuu.Value)
                    };

                    var picture = seiyuu.Attribute("picture");
                    if (picture != null && !string.IsNullOrEmpty(picture.Value))
                    {
                        person.Image = PersonImageBaseUrl + picture.Value;
                    }

                    var id = seiyuu.Attribute("id");
                    if (id != null && !string.IsNullOrEmpty(id.Value))
                    {
                        person.Id = id.Value;
                    }

                    people.Add(person);
                }
            }
        }

        return people;
    }

    private static IEnumerable<AniDbPersonInfo> ParseCreatorsList(string xml)
    {
        var doc = XDocument.Parse(xml);
        var people = new List<AniDbPersonInfo>();

        var creators = doc.Element("creators");
        if (creators != null)
        {
            foreach (var creator in creators.Descendants("name"))
            {
                var type = creator.Attribute("type");
                if (type != null && type.Value == "Animation Work")
                {
                    continue;
                }

                var person = new AniDbPersonInfo
                {
                    Name = ReverseNameOrder(creator.Value)
                };

                var id = creator.Attribute("id");
                if (id != null && !string.IsNullOrEmpty(id.Value))
                {
                    person.Id = id.Value;
                }

                people.Add(person);
            }
        }

        return people;
    }

    private static async Task SaveXml(string xml, string filename)
    {
        var writerSettings = new XmlWriterSettings
        {
            Encoding = Encoding.UTF8,
            Async = true
        };

        // Written beside the real file and moved onto it once complete. A crash or a full disk
        // part way through a direct write would leave a truncated document, which is worse than
        // none: it does not look stale, so every read of it fails until the cache window lapses.
        var partialFilename = filename + ".partial";

        using (var writer = XmlWriter.Create(partialFilename, writerSettings))
        {
            await writer.WriteRawAsync(xml).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        File.Move(partialFilename, filename, true);
    }

    private static async Task SaveEpsiodeXml(string seriesDataDirectory, string xml)
    {
        var episodeNumber = await ParseEpisodeNumber(xml).ConfigureAwait(false);

        if (episodeNumber != null)
        {
            var file = Path.Combine(seriesDataDirectory, FormattableString.Invariant($"episode-{episodeNumber}.xml"));
            await SaveXml(xml, file).ConfigureAwait(false);
        }
    }

    private static async Task<string?> ParseEpisodeNumber(string xml)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            ValidationType = ValidationType.None
        };

        using var streamReader = new StringReader(xml);
        using var reader = XmlReader.Create(streamReader, settings);
        await reader.MoveToContentAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "epno")
                {
                    var val = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        return val;
                    }
                }
                else
                {
                    await reader.SkipAsync().ConfigureAwait(false);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the series data path.
    /// </summary>
    /// <param name="appPaths">The app paths.</param>
    /// <param name="seriesId">The series id.</param>
    /// <returns>System.String.</returns>
    public static string GetSeriesDataPath(IApplicationPaths appPaths, string seriesId)
    {
        return Path.Combine(PluginPaths.CacheFolder(appPaths), "series", seriesId);
    }

    [GeneratedRegex(@"<error[^>]*>.*?</error>", RegexOptions.Singleline)]
    private static partial Regex ErrorRegex();

    [GeneratedRegex("banned", RegexOptions.IgnoreCase)]
    private static partial Regex BannedRegex();

    [GeneratedRegex("client", RegexOptions.IgnoreCase)]
    private static partial Regex ClientErrorRegex();

    [GeneratedRegex(@"^\s*(?:[0-9]{1,2}|[IVX]{1,4})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SequelNumberRegex();

    [GeneratedRegex(@"\(([0-9]{4})\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYearRegex();

    private struct GenreInfo
    {
        public string Name;
        public int Weight;
    }
}
