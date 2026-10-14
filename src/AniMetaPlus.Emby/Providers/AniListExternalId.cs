using AniMetaPlus.Sources;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace AniMetaPlus.Providers;

/// <summary>
/// The AniList id of a series or movie, which AniMeta+ writes onto the items it fills.
/// </summary>
public class AniListExternalId : IExternalId
{
    /// <inheritdoc />
    public string Name => "AniList";

    /// <inheritdoc />
    public string Key => SourceIds.AniList;

    /// <inheritdoc />
    public string UrlFormatString => "https://anilist.co/anime/{0}";

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Series || item is Movie;
}
