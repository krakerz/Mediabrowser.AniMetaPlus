using AniMetaPlus.Sources;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace AniMetaPlus.Providers;

/// <summary>
/// The MyAnimeList id of a series or movie, which AniMeta+ writes onto the items it fills.
/// </summary>
public class MyAnimeListExternalId : IExternalId
{
    /// <inheritdoc />
    public string Name => "MyAnimeList";

    /// <inheritdoc />
    public string Key => SourceIds.Mal;

    /// <inheritdoc />
    public string UrlFormatString => "https://myanimelist.net/anime/{0}";

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Series || item is Movie;
}
