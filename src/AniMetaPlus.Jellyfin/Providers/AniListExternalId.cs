using System.Collections.Generic;
using AniMetaPlus.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace AniMetaPlus.Providers;

/// <summary>
/// The AniList id of a series or movie, which AniMeta+ writes onto the items it fills.
/// </summary>
public class AniListExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "AniList";

    /// <inheritdoc />
    public string Key => SourceIds.AniList;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => null;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Series || item is Movie;
}

/// <summary>
/// The AniList link of a series or movie.
/// </summary>
public class AniListExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc />
    public string Name => "AniList";

    /// <inheritdoc />
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item is Series or Movie && item.TryGetProviderId(SourceIds.AniList, out var id))
        {
            yield return $"https://anilist.co/anime/{id}";
        }
    }
}
