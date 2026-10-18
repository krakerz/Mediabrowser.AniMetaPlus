using System;
using MediaBrowser.Model.Plugins;

namespace AniMetaPlus.Configuration;

public enum TitlePreferenceType
{
    /// <summary>
    /// Use titles in the local metadata language.
    /// </summary>
    Localized,

    /// <summary>
    /// Use titles in Japanese.
    /// </summary>
    Japanese,

    /// <summary>
    /// Use titles in Japanese romaji.
    /// </summary>
    JapaneseRomaji
}

/// <summary>
/// Where a season's details come from.
/// </summary>
public enum SeasonDetailsMode
{
    /// <summary>
    /// From the AniDB entry the season resolves to.
    /// </summary>
    AniDb,

    /// <summary>
    /// From the server's own TheTVDB and TheMovieDb season providers, never from AniDB's entry.
    /// </summary>
    Delegated,

    /// <summary>
    /// Nothing: the season keeps the server's own name and has no details or images.
    /// </summary>
    Plain,

    /// <summary>
    /// From the first source in the overview order that has an overview for the season, each
    /// asked per season: its own AniDB entry, AniList or MyAnimeList for that entry, or the
    /// server's TheTVDB and TheMovieDb season providers.
    /// </summary>
    Order
}

/// <summary>
/// Where a season's name comes from.
/// </summary>
public enum SeasonNamesMode
{
    /// <summary>
    /// From the source the season's details came from, when it has a name of its own.
    /// </summary>
    Source,

    /// <summary>
    /// Always "Season N" or "Specials", whatever the sources call the season.
    /// </summary>
    Plain
}

public enum AnimeDefaultGenreType
{
    None,
    Anime,
    Animation
}

public partial class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// The shortest cache window AniDB metadata may be reused for. AniDB asks clients not to
    /// fetch the same anime more than once a day; three days keeps a library's scans well
    /// clear of that, and AniDB's limits on the Sources tab pace scans besides.
    /// </summary>
    public const int MinimumCacheAgeDays = 3;

    /// <summary>
    /// The shortest gap between two AniDB requests. AniDB bans a client that sends them
    /// closer together than roughly two seconds, so no lower value is accepted.
    /// </summary>
    public const int MinimumRequestIntervalMs = 2500;

    /// <summary>
    /// The tag weight AniDB itself treats as the line between a tag that describes the show
    /// and one a single user attached to it.
    /// </summary>
    public const int DefaultMinimumTagWeight = 400;

    private int _maxCacheAge = 7;

    private int _requestIntervalMs = MinimumRequestIntervalMs;

    private int _aniDbShowsPerRun = 20;

    private string _tagBlacklist = string.Empty;

    public PluginConfiguration()
    {
        TitlePreference = TitlePreferenceType.Localized;
        IgnoreSeason = false;
        TitleSimilarityThreshold = 50;
        MaxGenres = 5;
        TidyGenreList = true;
        TitleCaseGenres = true;
        AnimeDefaultGenre = AnimeDefaultGenreType.Anime;
        MaxCacheAge = 7;
        AniDbReplaceGraves = true;
        IncludeAdultTags = true;
        ImportGenres = true;
        ImportTags = true;
        UseAniDbSeasonNames = true;
        RequestIntervalMs = MinimumRequestIntervalMs;
        MinimumTagWeight = DefaultMinimumTagWeight;
        InfoboxTagsOnly = false;
        TagBlacklist = string.Empty;
        UseAniBridgeMappings = true;
        ImportCommunityRating = true;
        PublishMappedIds = true;
        CastShowsCharacters = false;
        EnableSimilarItems = true;
        AniDbImagesFromCache = true;
    }

    /// <summary>
    /// Gets or sets the language the displayed title is taken from. The original title is not
    /// affected: it is always the romaji title, that being the Japanese title of the anime in
    /// the alphabet the rest of the world reads it in.
    /// </summary>
    public TitlePreferenceType TitlePreference { get; set; }

    /// <summary>
    /// Gets or sets the language AniDB episode titles are taken in: a <see cref="TitlePreferenceType"/>
    /// name, or empty to follow <see cref="TitlePreference"/>. Lets a show keep its romaji title
    /// while its episodes read in the library's language.
    /// </summary>
    public string EpisodeTitlePreference { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether an anime's other titles - short names such as
    /// "DanMachi", the English title, synonyms - go into its original title, read from the local
    /// titles dump. The servers search the name and the original title only, so this is what
    /// makes them findable. Off by default: the servers show the original title on the item's page.
    /// </summary>
    public bool SearchableAlternativeTitles { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the franchise task groups anime into collections,
    /// one per franchise, from the relations in the cached AniDB answers. Off by default; when
    /// off the task does nothing and never removes a collection.
    /// </summary>
    public bool FranchiseCollections { get; set; }

    /// <summary>
    /// Gets the language AniDB episode titles are taken in, after <see cref="EpisodeTitlePreference"/>
    /// falls back to the show titles' language.
    /// </summary>
    [System.Xml.Serialization.XmlIgnore]
    public TitlePreferenceType EpisodeTitles
        => Enum.TryParse<TitlePreferenceType>(EpisodeTitlePreference, true, out var preference) ? preference : TitlePreference;

    public bool IgnoreSeason { get; set; }

    public int TitleSimilarityThreshold { get; set; }

    public int MaxGenres { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether AniDB's tags are shown as genres at all. With
    /// this off the genre list is left to whichever other provider fills it.
    /// </summary>
    public bool ImportGenres { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether AniDB's tags are kept as tags. Independent of
    /// <see cref="ImportGenres"/>.
    /// </summary>
    public bool ImportTags { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether AniDB's rating is kept. Jellyfin holds one rating
    /// per item rather than one per provider, so a library taking ratings from somewhere else, or
    /// wanting none, has to turn this off rather than choose between them.
    /// </summary>
    public bool ImportCommunityRating { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the TVDB and TMDB ids the mapping sources file a
    /// show under are written onto it. They are what the image providers of those sites, and
    /// fanart, are keyed by, so filling them in is what lets those fetch artwork for a show this
    /// plugin identified. On by default, though the id also arms the metadata providers of those sites
    /// where they are enabled. An id already set is never replaced.
    /// </summary>
    public bool PublishMappedIds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the cast lists the characters rather than the
    /// actors voicing them, each named after the character and credited with its actor. AniDB
    /// records both, and Jellyfin has no kind of its own for a character, so it is one or the
    /// other.
    /// </summary>
    public bool CastShowsCharacters { get; set; }

    public bool TidyGenreList { get; set; }

    public bool TitleCaseGenres { get; set; }

    public AnimeDefaultGenreType AnimeDefaultGenre { get; set; }

    /// <summary>
    /// Gets or sets the number of days cached series metadata is reused before AniDB is
    /// queried again. Clamped to at least <see cref="MinimumCacheAgeDays"/> days.
    /// </summary>
    public int MaxCacheAge
    {
        get => Math.Max(_maxCacheAge, MinimumCacheAgeDays);
        set => _maxCacheAge = value;
    }

    public bool AniDbReplaceGraves { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a season is named after the AniDB entry it was
    /// filled from. With this off the season keeps the name Jellyfin gave it. Applies only
    /// when <see cref="SeasonDetails"/> is <c>AniDB</c>.
    /// </summary>
    public bool UseAniDbSeasonNames { get; set; }

    /// <summary>
    /// Gets or sets where a season's details come from: <c>AniDB</c> (the entry the season
    /// resolves to), <c>Delegated</c> (the server's TheTVDB and TheMovieDb season providers) or
    /// <c>Plain</c> (nothing, the season keeps the server's own name) or <c>Order</c> (the first
    /// source in the overview order that has one for the season). Anything else is <c>AniDB</c>.
    /// </summary>
    public string SeasonDetails { get; set; } = "AniDB";

    /// <summary>
    /// Gets <see cref="SeasonDetails"/> as a <see cref="SeasonDetailsMode"/>.
    /// </summary>
    [System.Xml.Serialization.XmlIgnore]
    public SeasonDetailsMode SeasonDetailsMode
        => Enum.TryParse<SeasonDetailsMode>(SeasonDetails?.Trim(), true, out var mode) && Enum.IsDefined(mode) ? mode : SeasonDetailsMode.AniDb;

    /// <summary>
    /// Gets or sets where a season's name comes from: <c>Source</c> (the source its details came
    /// from, when it has a name for the season) or <c>Plain</c> ("Season N" or "Specials",
    /// whatever the details mode). Anything else is <c>Source</c>.
    /// </summary>
    public string SeasonNames { get; set; } = "Source";

    /// <summary>
    /// Gets <see cref="SeasonNames"/> as a <see cref="SeasonNamesMode"/>.
    /// </summary>
    [System.Xml.Serialization.XmlIgnore]
    public SeasonNamesMode SeasonNamesMode
        => Enum.TryParse<SeasonNamesMode>(SeasonNames?.Trim(), true, out var mode) && Enum.IsDefined(mode) ? mode : SeasonNamesMode.Source;

    /// <summary>
    /// Gets or sets a value indicating whether the AniBridge mappings are consulted before the
    /// anime list. They place half again as many AniDB entries, state each placement's episode
    /// ranges outright, and are the only source here that carries TMDB show ids. Turning them
    /// off leaves the anime list to place everything, as it did before they were added, which is
    /// worth doing only where they place a show wrongly.
    /// </summary>
    public bool UseAniBridgeMappings { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the anime AniDB's users hold to be alike are
    /// offered as similar items. The votes come with the metadata already downloaded, so this
    /// costs no AniDB request of its own.
    /// </summary>
    public bool EnableSimilarItems { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether AniDB images are looked up from the cached answer,
    /// however old, whenever one exists, so an image refresh or the Edit Images dialog never asks
    /// the AniDB API for it. An anime with nothing cached is still asked. The picture itself comes
    /// from AniDB's image server either way, which the API ban does not cover.
    /// </summary>
    public bool AniDbImagesFromCache { get; set; }

    /// <summary>
    /// Gets or sets the AniDB request gap the General tab used to set, in milliseconds. Read once
    /// at startup and moved to AniDB's gap on the Sources tab, which is the only setting now;
    /// kept so older configuration files still load.
    /// </summary>
    public int RequestIntervalMs
    {
        get => Math.Max(_requestIntervalMs, MinimumRequestIntervalMs);
        set => _requestIntervalMs = value;
    }

    /// <summary>
    /// Gets or sets how many shows one scan or queue run used to send to AniDB. No longer read:
    /// a run now sends what is left of AniDB's burst, hour and day on the Sources tab. Kept so
    /// older configuration files still load.
    /// </summary>
    public int AniDbShowsPerRun
    {
        get => Math.Max(_aniDbShowsPerRun, 1);
        set => _aniDbShowsPerRun = value;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the tags AniDB flags as 18+ content are kept.
    /// They are kept by default. Anime AniDB flags as adult are rated as such either way,
    /// which is what parental controls act on.
    /// </summary>
    public bool IncludeAdultTags { get; set; }

    /// <summary>
    /// Gets or sets the weight a tag needs before it is imported. AniDB weights a tag by how
    /// much of the show it describes, so raising this keeps only the tags that characterise
    /// it.
    /// </summary>
    public int MinimumTagWeight { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether only the tags AniDB shows in the infobox on the
    /// anime's page are imported. Those are the ones AniDB considers to describe the show
    /// rather than its setting or its cast.
    /// </summary>
    public bool InfoboxTagsOnly { get; set; }

    /// <summary>
    /// Gets or sets the tag names never to import, one per line or separated by commas.
    /// Matched whole and without regard to case.
    /// </summary>
    public string TagBlacklist
    {
        get => _tagBlacklist;
        set => _tagBlacklist = value ?? string.Empty;
    }

    /// <summary>
    /// Gets or sets the UTC time at which an AniDB ban is assumed to lapse. Persisted so that
    /// restarting the server does not hand a banned client a fresh allowance. Runtime state
    /// rather than a user setting.
    /// </summary>
    public DateTime AniDbBannedUntilUtc { get; set; }

    /// <summary>
    /// Gets or sets the backoff, in minutes, applied to the next detected AniDB ban. Runtime
    /// state rather than a user setting.
    /// </summary>
    public int AniDbBanBackoffMinutes { get; set; }
}
