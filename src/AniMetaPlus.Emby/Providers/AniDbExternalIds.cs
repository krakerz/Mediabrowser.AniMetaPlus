using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace AniMetaPlus.Providers.AniDB;

/// <summary>
/// The AniDB id of a series or movie.
/// </summary>
public class AniDbExternalId : IExternalId
{
    /// <inheritdoc />
    public string Name => "AniDB";

    /// <inheritdoc />
    public string Key => ProviderNames.AniDb;

    /// <inheritdoc />
    public string UrlFormatString => "https://anidb.net/anime/{0}";

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Series || item is Movie;
}

/// <summary>
/// The AniDB id of the entry a season is read from.
/// </summary>
public class AniDbExternalSeasonId : IExternalId
{
    /// <inheritdoc />
    public string Name => "AniDB";

    /// <inheritdoc />
    public string Key => ProviderNames.AniDb;

    /// <inheritdoc />
    public string UrlFormatString => "https://anidb.net/anime/{0}";

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Season;
}

/// <summary>
/// The AniDB id of an episode.
/// </summary>
public class AniDbExternalEpisodeId : IExternalId
{
    /// <inheritdoc />
    public string Name => "AniDB";

    /// <inheritdoc />
    public string Key => ProviderNames.AniDb;

    /// <inheritdoc />
    public string UrlFormatString => "https://anidb.net/episode/{0}";

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Episode;
}

/// <summary>
/// The AniDB id of a creator.
/// </summary>
public class AniDbExternalPersonId : IExternalId
{
    /// <inheritdoc />
    public string Name => "AniDB";

    /// <inheritdoc />
    public string Key => ProviderNames.AniDb;

    /// <inheritdoc />
    public string UrlFormatString => "https://anidb.net/creator/{0}";

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Person;
}
