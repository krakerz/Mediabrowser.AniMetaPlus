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
/// The MyAnimeList id of a series or movie, which AniMeta+ writes onto the items it fills.
/// </summary>
public class MyAnimeListExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "MyAnimeList";

    /// <inheritdoc />
    public string Key => SourceIds.Mal;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => null;

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Series || item is Movie;
}

/// <summary>
/// The MyAnimeList link of a series or movie.
/// </summary>
public class MyAnimeListExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc />
    public string Name => "MyAnimeList";

    /// <inheritdoc />
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item is Series or Movie && item.TryGetProviderId(SourceIds.Mal, out var id))
        {
            yield return $"https://myanimelist.net/anime/{id}";
        }
    }
}
