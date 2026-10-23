using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using AniMetaPlus.Providers.AniDB.Metadata;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Providers.AniDB.Mapping;

/// <summary>
/// The AniBridge mappings, as read from one downloaded copy of them.
/// </summary>
internal sealed class AniBridgeIndex
{
    /// <summary>
    /// The schema this understands. The download URL pins the same major version, so a mapping
    /// set that has moved on is a set this would read wrongly rather than not at all, and is
    /// worth saying so about.
    /// </summary>
    private const string SchemaMajorVersion = "3";

    /// <summary>
    /// Where AniDB's other episodes begin in the single numbering the specials scope uses.
    /// </summary>
    private const int OtherEpisodeBand = 400;

    private readonly IReadOnlyDictionary<string, AniBridgeEntry> _byAnimeId;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<AniBridgeEntry>> _bySeries;
    private readonly IReadOnlyDictionary<string, string> _firstSeasonByTmdb;
    private readonly IReadOnlyDictionary<string, AniDbAnimeListEpisode> _movies;
    private readonly IReadOnlyDictionary<string, string> _tmdbByAnimeId;
    private readonly IReadOnlyDictionary<AniDbAnimeListEpisode, IReadOnlyList<string>> _movieKeys;
    private readonly IReadOnlyDictionary<string, AniDbAnimeListEpisode> _soleMovieByAnimeId;
    private IReadOnlyDictionary<string, string> _animeByLink = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> _linkByAnime = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> _tvdbByLink = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _linksByTvdb = new Dictionary<string, IReadOnlyList<string>>();

    private AniBridgeIndex(
        IReadOnlyDictionary<string, AniBridgeEntry> byAnimeId,
        IReadOnlyDictionary<string, IReadOnlyList<AniBridgeEntry>> bySeries,
        IReadOnlyDictionary<string, string> firstSeasonByTmdb,
        IReadOnlyDictionary<string, AniDbAnimeListEpisode> movies,
        IReadOnlyDictionary<string, string> tmdbByAnimeId,
        IReadOnlyDictionary<AniDbAnimeListEpisode, IReadOnlyList<string>> movieKeys,
        IReadOnlyDictionary<string, AniDbAnimeListEpisode> soleMovieByAnimeId)
    {
        _byAnimeId = byAnimeId;
        _bySeries = bySeries;
        _firstSeasonByTmdb = firstSeasonByTmdb;
        _movies = movies;
        _tmdbByAnimeId = tmdbByAnimeId;
        _movieKeys = movieKeys;
        _soleMovieByAnimeId = soleMovieByAnimeId;
    }

    /// <summary>
    /// Gets the placement worked out for every season already asked about, keyed by AniDB
    /// series id and season number, holding an empty list for a season the mappings do not
    /// place. Each of a season's episodes asks the same question, and the answer changes only
    /// when the mappings are read again, which replaces this along with them.
    /// </summary>
    public ConcurrentDictionary<string, IReadOnlyList<AniDbSeasonSegment>> Placements { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets how many AniDB entries the mappings place against a TVDB series.
    /// </summary>
    public int EntryCount => _byAnimeId.Count;

    /// <summary>
    /// Gets how many shows the mappings place those entries in. Fewer than the entries wherever
    /// a show is filed under several of them, which is the ordinary case: a season, its specials
    /// and the movie released alongside them are three entries of one show.
    /// </summary>
    public int ShowCount => _bySeries.Count;

    /// <summary>
    /// Gets how many movies the mappings identify. Counted by the AniDB episode each names, not
    /// by the keys they are filed under: one movie is filed under a TMDB, an IMDb and a TVDB id,
    /// and is one movie.
    /// </summary>
    public int MovieCount => _movies.Values.Distinct().Count();

    /// <summary>
    /// Reads a downloaded copy of the mappings.
    /// </summary>
    /// <param name="path">Where the copy is cached.</param>
    /// <param name="logger">The logger of whichever provider is asking.</param>
    /// <param name="cachedAtUtc">When the copy was written.</param>
    /// <param name="description">How the file is named in log messages, as a noun phrase. The schema is read by more than one file: the downloaded mappings, and the overrides written by whoever runs the server.</param>
    /// <returns>The mappings.</returns>
    public static AniBridgeIndex Parse(string path, ILogger logger, DateTime cachedAtUtc, string description)
    {
        // Read whole rather than streamed. The file is a single object of some seventy thousand
        // keys, all but the AniDB ones skipped, so a reader over the bytes costs one buffer the
        // size of the file and no bookkeeping, where a document object model over it would cost
        // several times the file and hold every key this throws away.
        var bytes = File.ReadAllBytes(path);
        // Utf8JsonReader rejects a byte order mark, which Notepad writes into a hand-edited overrides file.
        var content = bytes.AsSpan(bytes.AsSpan().StartsWith("\uFEFF"u8) ? 3 : 0);
        var reader = new Utf8JsonReader(content, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

        var spans = new Dictionary<string, List<AniBridgeSpan>>(StringComparer.Ordinal);
        var seriesKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        var tmdbCandidates = new Dictionary<string, List<SeasonClaim>>(StringComparer.Ordinal);
        var movies = new Dictionary<string, List<AniDbAnimeListEpisode>>(StringComparer.Ordinal);
        var animeByLink = new Dictionary<string, string>(StringComparer.Ordinal);
        var linkClaims = new List<LinkClaim>();
        string? schemaVersion = null;

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("The AniBridge mappings do not begin with an object");
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueSpan.SequenceEqual("$meta"u8))
            {
                reader.Read();
                schemaVersion = ReadSchemaVersion(ref reader);

                continue;
            }

            // The mappings keyed by a MyAnimeList or AniList entry are read for the TVDB show
            // they file it under, and nothing else: the AniDB side holds the rest of what they say.
            if (reader.ValueSpan.StartsWith("mal:"u8) || reader.ValueSpan.StartsWith("anilist:"u8))
            {
                var link = reader.GetString()!;

                reader.Read();

                if (link[(link.IndexOf(':') + 1)..] is { Length: > 0 } linked && linked.All(char.IsAsciiDigit))
                {
                    ReadLinkShows(ref reader, link, linkClaims);
                }
                else
                {
                    reader.Skip();
                }

                continue;
            }

            // Otherwise only the AniDB side of the mappings is read. Every mapping is written in
            // both directions - all 8,633 AniDB-to-TVDB placements appear as TVDB-to-AniDB ones
            // too - so the shows keyed the other way are the same shows, and skipping them here
            // costs nothing but saves reading some sixty thousand keys.
            if (!reader.ValueSpan.StartsWith("anidb:"u8))
            {
                reader.Skip();

                continue;
            }

            var descriptor = reader.GetString()!;

            reader.Read();

            if (TryReadScope(descriptor, out var animeId, out var kind))
            {
                ReadTargets(ref reader, animeId, kind, spans, seriesKeys, tmdbCandidates, movies, animeByLink);
            }
            else
            {
                reader.Skip();
            }
        }

        if (schemaVersion != null && !schemaVersion.StartsWith(SchemaMajorVersion + ".", StringComparison.Ordinal))
        {
            logger.LogWarning(
                "{Source} are written to schema {SchemaVersion}, and this reads schema {SupportedVersion}. Seasons may be placed wrongly until the plugin is updated",
                description,
                schemaVersion,
                SchemaMajorVersion);
        }

        var index = Build(spans, seriesKeys, tmdbCandidates, movies, schemaVersion, cachedAtUtc, description, logger);
        index._animeByLink = animeByLink;
        index._linkByAnime = animeByLink
            .GroupBy(pair => pair.Value + ":" + pair.Key[..pair.Key.IndexOf(':')], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Key[(group.First().Key.IndexOf(':') + 1)..], StringComparer.Ordinal);
        (index._tvdbByLink, index._linksByTvdb) = BuildTvdbLinks(linkClaims);

        return index;
    }

    /// <summary>
    /// Every entry the mappings file under the same show as the given one.
    /// </summary>
    /// <param name="animeId">The AniDB id of an entry of the show.</param>
    /// <returns>The entries, or <c>null</c> where the mappings do not place that one.</returns>
    public IReadOnlyList<AniBridgeEntry>? Siblings(string animeId)
    {
        if (!_byAnimeId.TryGetValue(animeId, out var self))
        {
            return null;
        }

        return _bySeries.TryGetValue(self.SeriesKey, out var siblings) ? siblings : null;
    }

    /// <summary>
    /// Every entry filed under the given show.
    /// </summary>
    /// <param name="seriesKey">The TVDB id the show's seasons are numbered against.</param>
    /// <returns>The entries, or <c>null</c> where nothing is filed under that show.</returns>
    public IReadOnlyList<AniBridgeEntry>? EntriesFor(string seriesKey)
        => _bySeries.GetValueOrDefault(seriesKey);

    /// <summary>
    /// The show an entry is filed under, as the TVDB id its seasons are numbered against.
    /// </summary>
    /// <remarks>
    /// What a sparsely written set of mappings is reached through. Such a set names the one
    /// entry it has something to say about, which is rarely the entry a show is identified as,
    /// so a lookup by that entry finds nothing; the show it belongs to is what the two sets
    /// have in common.
    /// </remarks>
    /// <param name="animeId">The AniDB id of an entry of the show.</param>
    /// <returns>The TVDB id, or <c>null</c> where the mappings do not place that entry.</returns>
    public string? SeriesKeyOf(string animeId)
        => _byAnimeId.TryGetValue(animeId, out var entry) ? entry.SeriesKey : null;

    /// <summary>
    /// What the mappings say an entry is of its show: the earliest ordinary season it fills and
    /// the episode of it the entry starts at, the specials where it fills nothing else, or a movie.
    /// </summary>
    /// <param name="animeId">The AniDB id of the entry.</param>
    /// <returns>The role, or <c>null</c> where the mappings say nothing about the entry.</returns>
    public MappedRole? RoleOf(string animeId)
    {
        var movie = MovieKeysOf(animeId, null).Count > 0;
        int? season = null;
        var offset = 0;

        if (_byAnimeId.TryGetValue(animeId, out var entry))
        {
            if (EarliestClaim(entry) is { } claim)
            {
                season = claim.Season;
                offset = Math.Max(claim.Start - 1, 0);
            }
            else if (entry.Spans.Count > 0)
            {
                season = 0;
            }
        }

        return season == null && !movie ? null : new MappedRole(season, offset, false, movie);
    }

    /// <summary>
    /// The TMDB show an entry is placed against, where the mappings place it against one. Not
    /// every show they place has a TMDB id; the TVDB id is what they are keyed by.
    /// </summary>
    /// <param name="animeId">The AniDB id of an entry of the show.</param>
    /// <returns>The TMDB show id, or <c>null</c> where the mappings name none for that entry.</returns>
    public string? TmdbShowOf(string animeId)
        => _tmdbByAnimeId.GetValueOrDefault(animeId);

    /// <summary>
    /// Every key the mappings file a movie under, given the AniDB episode it is. Answers for an
    /// entry whose episode is not known only where the entry holds a single movie.
    /// </summary>
    /// <param name="animeId">The AniDB id of the entry holding the movie.</param>
    /// <param name="episode">Which of its episodes the movie is, where that is known.</param>
    /// <returns>The keys, in no particular order, which is empty where the movie is not identified.</returns>
    public IReadOnlyList<string> MovieKeysOf(string animeId, AniDbAnimeListEpisode? episode)
    {
        var wanted = episode ?? _soleMovieByAnimeId.GetValueOrDefault(animeId);

        return wanted == null || !string.Equals(wanted.AnimeId, animeId, StringComparison.Ordinal)
            ? []
            : _movieKeys.GetValueOrDefault(wanted) ?? [];
    }

    /// <summary>
    /// Works out which of a show's entries fill the given season, and which of their episodes
    /// each one contributes.
    /// </summary>
    /// <param name="siblings">Every entry the mappings file under the same show.</param>
    /// <param name="seasonNumber">The season number.</param>
    /// <returns>The segments, in the order the season's episodes run through them.</returns>
    public static IReadOnlyList<AniDbSeasonSegment> Place(IReadOnlyList<AniBridgeEntry> siblings, int seasonNumber)
    {
        // Kept apart because one entry can describe the same season under two of its own
        // numberings: anime 13473 maps a single ordinary episode onto season 2 and all twelve of
        // its other episodes onto the same twelve, the season being three movies that the season
        // numbering breaks into television episodes. Mixing the two would leave the season
        // claimed twice over, so the fuller numbering is taken whole and the other dropped.
        var claims = new Dictionary<AniDbEpisodeKind, List<AniDbSeasonSegment>>();

        foreach (var entry in siblings)
        {
            foreach (var span in entry.Spans)
            {
                // A season can be filled from any of an entry's numberings, the specials
                // included: K-On!'s first season ends with two of them, and the segment says so
                // rather than the episode being left unidentified.
                if (span.Season != seasonNumber)
                {
                    continue;
                }

                SeasonSegments.Add(claims, new AniDbSeasonSegment(entry.AnimeId, span.InSeason.Start, CountOf(span), span.InEntry.Start, span.Kind));
            }
        }

        return SeasonSegments.Resolve(claims);
    }

    /// <summary>
    /// The entry a show begins in, found from the TVDB id another provider has already settled
    /// on.
    /// </summary>
    /// <param name="tvdbId">The TVDB series id.</param>
    /// <returns>The AniDB id, or <c>null</c> where the mappings place nothing against that id.</returns>
    public string? FirstSeasonByTvdb(string tvdbId)
        => _bySeries.TryGetValue(tvdbId, out var siblings) ? PickFirstSeason(siblings) : null;

    /// <summary>
    /// The entry a show begins in, found from the TMDB id another provider has already settled
    /// on. The anime list cannot answer this: it carries TMDB ids for movies only.
    /// </summary>
    /// <param name="tmdbId">The TMDB show id.</param>
    /// <returns>The AniDB id, or <c>null</c> where the mappings place nothing against that id.</returns>
    public string? FirstSeasonByTmdb(string tmdbId)
        => _firstSeasonByTmdb.GetValueOrDefault(tmdbId);

    /// <summary>
    /// The AniDB entry a MyAnimeList or AniList entry is. All three keep one entry per season
    /// or cour, so they line up one to one far more often than any does with TVDB.
    /// </summary>
    /// <param name="prefix">The mappings' name for the database, "mal" or "anilist".</param>
    /// <param name="id">The id in that database.</param>
    /// <returns>The AniDB id, or <c>null</c> where the mappings link nothing to that id.</returns>
    public string? AnimeIdByLink(string prefix, string id)
        => _animeByLink.GetValueOrDefault(prefix + ":" + id);

    /// <summary>
    /// The MyAnimeList or AniList entry an AniDB entry is, the other way round from
    /// <see cref="AnimeIdByLink"/>. Where the mappings link several, the first is taken.
    /// </summary>
    /// <param name="animeId">The AniDB id.</param>
    /// <param name="prefix">The mappings' name for the database, "mal" or "anilist".</param>
    /// <returns>The id in that database, or <c>null</c> where the mappings link none.</returns>
    public string? LinkOf(string animeId, string prefix)
        => _linkByAnime.GetValueOrDefault(animeId + ":" + prefix);

    /// <summary>
    /// The TVDB show the mappings file a MyAnimeList or AniList entry under, read from the
    /// entry's own key rather than from the AniDB entry it is linked to. The two can name
    /// different shows where one of them has been corrected and the other has not.
    /// </summary>
    /// <param name="prefix">The mappings' name for the database, "mal" or "anilist".</param>
    /// <param name="id">The id in that database.</param>
    /// <returns>The TVDB show id, or <c>null</c> where the mappings file the entry under no ordinary season of a show.</returns>
    public string? TvdbShowByLink(string prefix, string id)
        => _tvdbByLink.GetValueOrDefault(prefix + ":" + id);

    /// <summary>
    /// The MyAnimeList and AniList entries the mappings file at the start of a TVDB show, AniList
    /// first, each in the form the mappings key them by.
    /// </summary>
    /// <param name="tvdbId">The TVDB show id.</param>
    /// <returns>The links, or an empty list where none is filed under that show.</returns>
    public IReadOnlyList<string> FirstSeasonLinksByTvdb(string tvdbId)
        => _linksByTvdb.GetValueOrDefault(tvdbId) ?? [];

    /// <summary>
    /// The AniDB entry a movie is, and which of its episodes holds it.
    /// </summary>
    /// <param name="key">The movie's key, from <see cref="MovieKey"/>.</param>
    /// <returns>The episode, or <c>null</c> where the mappings identify no movie under that id.</returns>
    public AniDbAnimeListEpisode? ResolveMovie(string key)
        => _movies.GetValueOrDefault(key);

    /// <summary>
    /// Whether the mappings file the given entry in an ordinary season of a show, which makes
    /// their silence about walking it back a statement rather than a gap.
    /// </summary>
    /// <param name="animeId">The AniDB id of the entry.</param>
    /// <returns><c>true</c> where the mappings place the entry in a season of its own.</returns>
    public bool FilesInOrdinarySeason(string animeId)
        => _byAnimeId.TryGetValue(animeId, out var entry) && SeasonOf(entry) >= 1;

    /// <summary>
    /// The entry a show begins in, given an entry of it the mappings file as a later season.
    /// </summary>
    /// <param name="animeId">The AniDB id the name match produced.</param>
    /// <returns>The AniDB id the show begins at, or <c>null</c> where the mappings do not place the entry or already place it at the show's first season.</returns>
    public string? WalkBackToFirstSeason(string animeId)
    {
        // Only an entry the mappings file as a second season or later is walked back. An entry
        // already at season 1 is the show's own start, and moving it could only hand the show
        // to whatever else shares its TVDB id.
        if (!_byAnimeId.TryGetValue(animeId, out var self) || SeasonOf(self) <= 1)
        {
            return null;
        }

        var siblings = Siblings(animeId);
        var first = siblings == null ? null : PickFirstSeason(siblings);

        return string.Equals(first, animeId, StringComparison.Ordinal) ? null : first;
    }

    /// <summary>
    /// Where the given episode of the specials season is read from.
    /// </summary>
    /// <param name="siblings">Every entry the mappings file under the same show.</param>
    /// <param name="episodeNumber">The episode number within the specials season.</param>
    /// <returns>The episode, or <c>null</c> where the mappings do not place it.</returns>
    public static AniDbAnimeListEpisode? PlaceSpecial(IReadOnlyList<AniBridgeEntry> siblings, int episodeNumber)
    {
        AniBridgeEntry? holder = null;
        AniBridgeSpan? narrowest = null;

        foreach (var entry in siblings)
        {
            foreach (var span in entry.Spans)
            {
                if (span.Season != 0 || episodeNumber < span.InSeason.Start || episodeNumber > (span.InSeason.End ?? int.MaxValue))
                {
                    continue;
                }

                var number = span.InEntry.Start + (episodeNumber - span.InSeason.Start);

                // The two sides of a span disagree in length here and there, the mappings
                // reporting some nine thousand such spans themselves. Where they do, an
                // episode past the end of the entry's own run is not placed by this span.
                if (number > (span.InEntry.End ?? int.MaxValue))
                {
                    continue;
                }

                // A span naming this episode outright beats one that merely covers it, which
                // is what places a movie that the season numbering files among the specials.
                if (narrowest == null || (span.InSeason.Length ?? int.MaxValue) < (narrowest.InSeason.Length ?? int.MaxValue))
                {
                    holder = entry;
                    narrowest = span;
                }
            }
        }

        if (holder == null || narrowest == null)
        {
            return null;
        }

        return new AniDbAnimeListEpisode(
            holder.AnimeId,
            narrowest.InEntry.Start + (episodeNumber - narrowest.InSeason.Start),
            narrowest.Kind);
    }

    /// <summary>
    /// Which of the entries filed under one show the show begins in.
    /// </summary>
    /// <param name="siblings">Every entry the mappings file under the same show.</param>
    /// <returns>The AniDB id of the earliest entry, or <c>null</c> where none of them fills a season.</returns>
    public static string? PickFirstSeason(IReadOnlyList<AniBridgeEntry> siblings)
    {
        // The show begins in the entry filling its earliest season, and where that season was
        // released in parts, in the part starting at its first episode. Where a season is
        // filled by several entries starting together - a show and the recap or alternate
        // version filed beside it - the oldest of them is the show itself, AniDB having
        // registered it before whatever was made from it.
        return siblings
            .Select(entry => (Entry: entry, Claim: EarliestClaim(entry)))
            .Where(candidate => candidate.Claim != null)
            .OrderBy(candidate => candidate.Claim!.Season)
            .ThenBy(candidate => candidate.Claim!.Start)
            .ThenBy(candidate => NumericId(candidate.Entry.AnimeId))
            .Select(candidate => candidate.Entry.AnimeId)
            .FirstOrDefault();
    }

    /// <summary>
    /// The season an entry fills, which is the earliest where it fills more than one.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The season number, or -1 where the entry fills no ordinary season.</returns>
    public static int SeasonOf(AniBridgeEntry entry) => EarliestClaim(entry)?.Season ?? -1;

    /// <summary>
    /// How many episodes a span contributes, or zero where it runs to the end of the season.
    /// </summary>
    /// <param name="span">The span.</param>
    /// <returns>The episode count.</returns>
    private static int CountOf(AniBridgeSpan span)
    {
        // Where the two sides of a span disagree in length, the shorter is used: reading past
        // the end of the entry would look for episodes it does not have, and reading past the
        // end of the season would take episodes belonging to whatever comes next.
        return (span.InSeason.Length, span.InEntry.Length) switch
        {
            ({ } inSeason, { } inEntry) => Math.Min(inSeason, inEntry),
            ({ } inSeason, null) => inSeason,
            (null, { } inEntry) => inEntry,
            _ => 0,
        };
    }

    /// <summary>
    /// The earliest ordinary season an entry fills, and where in it the entry starts.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The claim, or <c>null</c> where the entry fills no ordinary season.</returns>
    private static SeasonClaim? EarliestClaim(AniBridgeEntry entry)
    {
        SeasonClaim? earliest = null;

        foreach (var span in entry.Spans)
        {
            if (span.Season < 1 || span.Kind == AniDbEpisodeKind.Special)
            {
                continue;
            }

            if (earliest == null
                || span.Season < earliest.Season
                || (span.Season == earliest.Season && span.InSeason.Start < earliest.Start))
            {
                earliest = new SeasonClaim(span.Season, span.InSeason.Start, entry.AnimeId);
            }
        }

        return earliest;
    }

    /// <summary>
    /// Which claim to keep where more than one names the same movie.
    /// </summary>
    /// <remarks>
    /// An entry claims one movie twice wherever its ordinary numbering and its specials scope
    /// both cover it - nineteen movies in the set at the time of writing - and the ordinary
    /// episode is the better of the two: it is the entry AniDB registered for that movie, where
    /// the special is the same movie listed again inside whatever it was released alongside.
    /// </remarks>
    /// <param name="claims">The claims read for one key.</param>
    /// <returns>The claim to answer with.</returns>
    private static AniDbAnimeListEpisode PickMovie(List<AniDbAnimeListEpisode> claims)
        => claims
            .OrderBy(claim => claim.Kind == AniDbEpisodeKind.Regular ? 0 : 1)
            .ThenBy(claim => claim.Number)
            .ThenBy(claim => NumericId(claim.AnimeId))
            .First();

    private static int NumericId(string animeId)
        => int.TryParse(animeId, CultureInfo.InvariantCulture, out var parsed) ? parsed : int.MaxValue;

    private static AniBridgeIndex Build(
        Dictionary<string, List<AniBridgeSpan>> spans,
        Dictionary<string, string> seriesKeys,
        Dictionary<string, List<SeasonClaim>> tmdbCandidates,
        Dictionary<string, List<AniDbAnimeListEpisode>> movies,
        string? schemaVersion,
        DateTime cachedAtUtc,
        string description,
        ILogger logger)
    {
        var byAnimeId = new Dictionary<string, AniBridgeEntry>(StringComparer.Ordinal);
        var bySeries = new Dictionary<string, List<AniBridgeEntry>>(StringComparer.Ordinal);

        foreach (var (animeId, seriesKey) in seriesKeys)
        {
            var entry = new AniBridgeEntry(animeId, seriesKey, spans.GetValueOrDefault(animeId) ?? []);

            byAnimeId[animeId] = entry;

            if (!bySeries.TryGetValue(seriesKey, out var siblings))
            {
                siblings = [];
                bySeries[seriesKey] = siblings;
            }

            siblings.Add(entry);
        }

        var identifiedMovies = movies.ToDictionary(
            pair => pair.Key,
            pair => PickMovie(pair.Value),
            StringComparer.Ordinal);

        var firstSeasonByTmdb = new Dictionary<string, string>(StringComparer.Ordinal);
        var tmdbByAnimeId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (tmdbId, claims) in tmdbCandidates)
        {
            var first = claims
                .OrderBy(claim => claim.Season)
                .ThenBy(claim => claim.Start)
                .ThenBy(claim => NumericId(claim.AnimeId))
                .First();

            firstSeasonByTmdb[tmdbId] = first.AnimeId;

            foreach (var claim in claims)
            {
                // An entry placed against two TMDB shows keeps the lower of the two ids, so that
                // what is written onto a show does not depend on the order the file was read in.
                if (!tmdbByAnimeId.TryGetValue(claim.AnimeId, out var held) || NumericId(tmdbId) < NumericId(held))
                {
                    tmdbByAnimeId[claim.AnimeId] = tmdbId;
                }
            }
        }

        var movieKeys = new Dictionary<AniDbAnimeListEpisode, List<string>>();
        var moviesByAnimeId = new Dictionary<string, HashSet<AniDbAnimeListEpisode>>(StringComparer.Ordinal);

        foreach (var (key, episode) in identifiedMovies)
        {
            if (!movieKeys.TryGetValue(episode, out var keys))
            {
                keys = [];
                movieKeys[episode] = keys;
            }

            keys.Add(key);

            if (!moviesByAnimeId.TryGetValue(episode.AnimeId, out var held))
            {
                held = [];
                moviesByAnimeId[episode.AnimeId] = held;
            }

            held.Add(episode);
        }

        // Only the entries holding exactly one movie. An entry holding a trilogy is one AniDB id
        // and three TMDB ids, and nothing but the episode says which of them a film is.
        var soleMovieByAnimeId = moviesByAnimeId
            .Where(pair => pair.Value.Count == 1)
            .ToDictionary(pair => pair.Key, pair => pair.Value.First(), StringComparer.Ordinal);

        logger.LogInformation(
            "{Source}, last written on {WrittenAt} to schema {SchemaVersion}, place {EntryCount} AniDB entries across {SeriesCount} TVDB shows, identify {TmdbCount} TMDB shows and identify {MovieCount} movies",
            description,
            cachedAtUtc,
            schemaVersion ?? "an unstated version",
            byAnimeId.Count,
            bySeries.Count,
            firstSeasonByTmdb.Count,
            identifiedMovies.Values.Distinct().Count());

        return new AniBridgeIndex(
            byAnimeId,
            bySeries.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<AniBridgeEntry>)pair.Value, StringComparer.Ordinal),
            firstSeasonByTmdb,
            identifiedMovies,
            tmdbByAnimeId,
            movieKeys.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value),
            soleMovieByAnimeId);
    }

    private static string? ReadSchemaVersion(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        string? version = null;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueSpan.SequenceEqual("schema_version"u8))
            {
                reader.Read();

                version = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
            else
            {
                reader.Skip();
            }
        }

        return version;
    }

    private static void ReadTargets(
        ref Utf8JsonReader reader,
        string animeId,
        AniDbEpisodeKind kind,
        Dictionary<string, List<AniBridgeSpan>> spans,
        Dictionary<string, string> seriesKeys,
        Dictionary<string, List<SeasonClaim>> tmdbCandidates,
        Dictionary<string, List<AniDbAnimeListEpisode>> movies,
        Dictionary<string, string> animeByLink)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            // A value of another shape is stepped over, or the enclosing loops would end inside it and drop the rest of the file.
            reader.Skip();

            return;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            // Only a regular entry's link is kept: the same id also turns up against the
            // specials scope of the entry, which is not a show of its own.
            if (reader.ValueSpan.StartsWith("mal:"u8) || reader.ValueSpan.StartsWith("anilist:"u8))
            {
                var link = reader.GetString()!;
                if (kind == AniDbEpisodeKind.Regular && link[(link.IndexOf(':') + 1)..] is { Length: > 0 } linked && linked.All(char.IsAsciiDigit))
                {
                    animeByLink.TryAdd(link, animeId);
                }

                reader.Read();
                reader.Skip();

                continue;
            }

            var isTvdb = reader.ValueSpan.StartsWith("tvdb_show:"u8);

            // A movie is keyed by its own id with whichever provider, and by no season: the
            // library holds it as one item, so there is nothing to lay over a numbering.
            var isMovie = !isTvdb
                && (reader.ValueSpan.StartsWith("tmdb_movie:"u8)
                    || reader.ValueSpan.StartsWith("imdb_movie:"u8)
                    || reader.ValueSpan.StartsWith("tvdb_movie:"u8));

            // TMDB is read for identification only, not for placing seasons: the numbering a
            // season is placed against has to be the one Jellyfin numbered the season by, and
            // that is what the show's own provider settled on. Placing TVDB runs against TMDB
            // seasons would move every episode of a show the two databases split differently.
            if (!isTvdb && !isMovie && !reader.ValueSpan.StartsWith("tmdb_show:"u8))
            {
                reader.Skip();

                continue;
            }

            var descriptor = reader.GetString()!;

            reader.Read();

            if (isMovie)
            {
                var key = MovieKey.FromDescriptor(descriptor);

                if (key == null)
                {
                    reader.Skip();
                }
                else
                {
                    ReadMovieClaim(ref reader, animeId, kind, key, movies);
                }

                continue;
            }

            if (!TryReadShow(descriptor, out var showId, out var season))
            {
                reader.Skip();

                continue;
            }

            if (isTvdb)
            {
                ReadRanges(ref reader, animeId, kind, showId, season, spans, seriesKeys);
            }
            else
            {
                ReadTmdbClaim(ref reader, animeId, kind, showId, season, tmdbCandidates);
            }
        }
    }

    private static void ReadRanges(
        ref Utf8JsonReader reader,
        string animeId,
        AniDbEpisodeKind kind,
        string showId,
        int season,
        Dictionary<string, List<AniBridgeSpan>> spans,
        Dictionary<string, string> seriesKeys)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();

            return;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var inEntry = AniBridgeRange.ReadAll(reader.GetString());

            reader.Read();

            // A range the mappings unmap outright is written as null, and one they write in a
            // form this does not read comes back null too.
            var inSeason = reader.TokenType == JsonTokenType.String ? AniBridgeRange.ReadTarget(reader.GetString()) : null;

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }

            if (inEntry == null || inSeason == null)
            {
                continue;
            }

            // A mapping listing several ranges, or weighting one side against the other, is read
            // as the runs it pairs up into, so that a season numbered around an episode AniDB
            // does not list is held as the two runs it really is rather than refused for the
            // comma between them.
            foreach (var (entryRun, seasonRun) in AniBridgeRange.Pair(inEntry, inSeason.Value.Ranges, inSeason.Value.Ratio))
            {
                if (ReadBand(kind, entryRun) is not { } banded)
                {
                    continue;
                }

                // The show is keyed by the id its episodes are placed against, so an entry only
                // counts as placed once a span of it has been read.
                seriesKeys.TryAdd(animeId, showId);

                if (!spans.TryGetValue(animeId, out var list))
                {
                    list = [];
                    spans[animeId] = list;
                }

                var span = new AniBridgeSpan(season, banded.InEntry, seasonRun, banded.Kind);

                // The specials and other scopes of one entry say the same thing twice once the
                // band above is read for what it is.
                if (!list.Contains(span))
                {
                    list.Add(span);
                }
            }
        }
    }

    /// <summary>
    /// Reads which episode of an entry a movie is.
    /// </summary>
    /// <remarks>
    /// A movie's side of the mapping is a single episode, so only the entry's side is read. Most
    /// name the entry's first ordinary episode, which is to say the movie is that AniDB entry;
    /// the rest name a later episode, a special or one of the other episodes, which is to say
    /// AniDB holds the movie inside an entry registered for something else.
    /// </remarks>
    /// <param name="reader">The reader, positioned at the movie's ranges.</param>
    /// <param name="animeId">The AniDB id of the entry.</param>
    /// <param name="kind">Which of the entry's numberings the scope named.</param>
    /// <param name="key">The movie's key, from <see cref="MovieKey"/>.</param>
    /// <param name="movies">Every claim read so far, by key.</param>
    private static void ReadMovieClaim(
        ref Utf8JsonReader reader,
        string animeId,
        AniDbEpisodeKind kind,
        string key,
        Dictionary<string, List<AniDbAnimeListEpisode>> movies)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();

            return;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var inEntry = AniBridgeRange.Read(reader.GetString());

            reader.Read();

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }

            // Only a range the movie is actually mapped onto counts. A null unmaps it outright.
            if (inEntry == null || reader.TokenType == JsonTokenType.Null || ReadBand(kind, inEntry) is not { } banded)
            {
                continue;
            }

            var claim = new AniDbAnimeListEpisode(animeId, banded.InEntry.Start, banded.Kind);

            if (!movies.TryGetValue(key, out var claims))
            {
                claims = [];
                movies[key] = claims;
            }

            if (!claims.Contains(claim))
            {
                claims.Add(claim);
            }
        }
    }

    private static void ReadTmdbClaim(
        ref Utf8JsonReader reader,
        string animeId,
        AniDbEpisodeKind kind,
        string showId,
        int season,
        Dictionary<string, List<SeasonClaim>> tmdbCandidates)
    {
        var earliest = ReadEarliestTargetStart(ref reader);

        if (kind != AniDbEpisodeKind.Regular || season < 1 || earliest == int.MaxValue)
        {
            return;
        }

        if (!tmdbCandidates.TryGetValue(showId, out var claims))
        {
            claims = [];
            tmdbCandidates[showId] = claims;
        }

        claims.Add(new SeasonClaim(season, earliest, animeId));
    }

    /// <summary>
    /// Reads where an entry starts in a show's season, given the ranges it is mapped onto.
    /// </summary>
    /// <param name="reader">The reader, positioned at the ranges of one show descriptor.</param>
    /// <returns>The earliest episode of the season any range starts at, or <see cref="int.MaxValue"/> where none is mapped.</returns>
    private static int ReadEarliestTargetStart(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();

            return int.MaxValue;
        }

        var earliest = int.MaxValue;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            reader.Read();

            var inSeason = reader.TokenType == JsonTokenType.String ? AniBridgeRange.ReadTarget(reader.GetString()) : null;

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }

            // Only where the entry starts is wanted, so a run listing several ranges is worth
            // no more than its first episode, and a ratio over them is worth nothing at all.
            if (inSeason is { } side && side.Ranges[0].Start < earliest)
            {
                earliest = side.Ranges[0].Start;
            }
        }

        return earliest;
    }

    /// <summary>
    /// Reads the TVDB shows a MyAnimeList or AniList entry is filed under in its own key.
    /// </summary>
    /// <param name="reader">The reader, positioned at the value of the entry's key.</param>
    /// <param name="link">The entry, as the mappings key it: "mal:&lt;id&gt;" or "anilist:&lt;id&gt;".</param>
    /// <param name="claims">Every claim read so far.</param>
    private static void ReadLinkShows(ref Utf8JsonReader reader, string link, List<LinkClaim> claims)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();

            return;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (!reader.ValueSpan.StartsWith("tvdb_show:"u8))
            {
                reader.Read();
                reader.Skip();

                continue;
            }

            var descriptor = reader.GetString()!;

            reader.Read();

            // Season 0 is the show's specials, which says nothing about which show the entry begins.
            if (!TryReadShow(descriptor, out var showId, out var season) || season < 1)
            {
                reader.Skip();

                continue;
            }

            var earliest = ReadEarliestTargetStart(ref reader);

            if (earliest != int.MaxValue)
            {
                claims.Add(new LinkClaim(link, showId, season, earliest));
            }
        }
    }

    /// <summary>
    /// Works out, from what the link keys say, the show each entry is filed under and the entries
    /// each show begins with.
    /// </summary>
    /// <param name="claims">Every claim read.</param>
    /// <returns>The show by link, and the first-season links by show.</returns>
    private static (IReadOnlyDictionary<string, string> TvdbByLink, IReadOnlyDictionary<string, IReadOnlyList<string>> LinksByTvdb) BuildTvdbLinks(List<LinkClaim> claims)
    {
        // An entry filed under two shows belongs to the one it enters at the earliest season, the
        // lower id deciding a tie, so that the answer does not depend on the order of the file.
        var tvdbByLink = claims
            .GroupBy(claim => claim.Link, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(claim => claim.Season)
                    .ThenBy(claim => claim.Start)
                    .ThenBy(claim => NumericId(claim.ShowId))
                    .First().ShowId,
                StringComparer.Ordinal);

        var linksByTvdb = claims
            .GroupBy(claim => claim.ShowId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var first = group.OrderBy(claim => claim.Season).ThenBy(claim => claim.Start).First();

                    return (IReadOnlyList<string>)group
                        .Where(claim => claim.Season == first.Season && claim.Start == first.Start)
                        .Select(claim => claim.Link)
                        .OrderBy(link => link.StartsWith("anilist:", StringComparison.Ordinal) ? 0 : 1)
                        .ThenBy(link => NumericId(link[(link.IndexOf(':') + 1)..]))
                        .ToList();
                },
                StringComparer.Ordinal);

        return (tvdbByLink, linksByTvdb);
    }

    /// <summary>
    /// Reads a source descriptor, which is "anidb:&lt;id&gt;:&lt;scope&gt;".
    /// </summary>
    /// <param name="descriptor">The descriptor as written.</param>
    /// <param name="animeId">The AniDB id.</param>
    /// <param name="kind">Which of the entry's numberings the scope counts.</param>
    /// <returns><c>true</c> where the descriptor names a scope this reads.</returns>
    private static bool TryReadScope(string descriptor, out string animeId, out AniDbEpisodeKind kind)
    {
        animeId = string.Empty;
        kind = AniDbEpisodeKind.Regular;

        var parts = descriptor.Split(':');

        if (parts.Length != 3 || parts[1].Length == 0 || !parts[1].All(char.IsAsciiDigit))
        {
            return false;
        }

        // "R" numbers the entry's ordinary episodes, "S" its specials and "O" its other
        // episodes. Anything else is a scope this does not read.
        switch (parts[2])
        {
            case "R":
                break;
            case "S":
                kind = AniDbEpisodeKind.Special;
                break;
            case "O":
                kind = AniDbEpisodeKind.Other;
                break;
            default:
                return false;
        }

        animeId = parts[1];

        return true;
    }

    /// <summary>
    /// Rewrites a span of the specials scope that is really one of the other episodes.
    /// </summary>
    /// <remarks>
    /// The specials scope numbers every episode that is not an ordinary one in a single run,
    /// AniDB's type deciding where in it a number falls: specials from 1, and the other
    /// episodes from 401. Every number in the set is in one band or the other, and where an
    /// entry carries both scopes they say the same thing twice - anime 13473's specials scope
    /// maps 401-412 onto the same season episodes its other scope maps 1-12 onto - so reading
    /// the band as what it is makes the two agree instead of compete, and gives the six entries
    /// that carry only the specials scope an answer that names a document on disk.
    /// </remarks>
    /// <param name="kind">The kind the scope named.</param>
    /// <param name="inEntry">The episodes of the entry the span covers.</param>
    /// <returns>The kind and range to record, or <c>null</c> where the range falls in neither band.</returns>
    private static (AniDbEpisodeKind Kind, AniBridgeRange InEntry)? ReadBand(AniDbEpisodeKind kind, AniBridgeRange inEntry)
    {
        if (kind != AniDbEpisodeKind.Special || inEntry.Start < OtherEpisodeBand)
        {
            // A special numbered into a later band alongside one that is not cannot be read as
            // either, and no such span exists in the set.
            return inEntry.End >= OtherEpisodeBand && kind == AniDbEpisodeKind.Special
                ? null
                : (kind, inEntry);
        }

        return (
            AniDbEpisodeKind.Other,
            new AniBridgeRange(inEntry.Start - OtherEpisodeBand, inEntry.End is { } end ? end - OtherEpisodeBand : null));
    }

    /// <summary>
    /// Reads a show descriptor, which is "&lt;provider&gt;:&lt;id&gt;:s&lt;season&gt;".
    /// </summary>
    /// <param name="descriptor">The descriptor as written.</param>
    /// <param name="showId">The show's id with the provider.</param>
    /// <param name="season">The season number, 0 being the specials.</param>
    /// <returns><c>true</c> where the descriptor names a numbered season.</returns>
    private static bool TryReadShow(string descriptor, out string showId, out int season)
    {
        showId = string.Empty;
        season = -1;

        var parts = descriptor.Split(':');

        if (parts.Length != 3
            || parts[1].Length == 0
            || !parts[1].All(char.IsAsciiDigit)
            || parts[2].Length < 2
            || parts[2][0] != 's'
            || !int.TryParse(parts[2].AsSpan(1), CultureInfo.InvariantCulture, out season))
        {
            return false;
        }

        showId = parts[1];

        return true;
    }

    /// <summary>
    /// One entry's claim on a season, used to work out which entry a show begins in.
    /// </summary>
    /// <param name="Season">The season claimed.</param>
    /// <param name="Start">The episode of that season the entry starts at.</param>
    /// <param name="AnimeId">The AniDB id of the entry.</param>
    private sealed record SeasonClaim(int Season, int Start, string AnimeId);

    /// <summary>
    /// One MyAnimeList or AniList entry's claim on a season of a TVDB show.
    /// </summary>
    /// <param name="Link">The entry, as the mappings key it.</param>
    /// <param name="ShowId">The TVDB show id.</param>
    /// <param name="Season">The season claimed.</param>
    /// <param name="Start">The episode of that season the entry starts at.</param>
    private sealed record LinkClaim(string Link, string ShowId, int Season, int Start);
}
