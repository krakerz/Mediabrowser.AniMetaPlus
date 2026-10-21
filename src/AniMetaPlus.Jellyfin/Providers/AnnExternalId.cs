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
/// The Anime News Network encyclopedia entry AniDB links a series or movie to. Its link is the
/// credit ANN's terms ask for wherever data from it is shown.
/// </summary>
public class AnnExternalId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName => "Anime News Network";

    /// <inheritdoc />
    public string Key => SourceIds.Ann;

    /// <inheritdoc />
    public ExternalIdMediaType? Type => null;

    /// <inheritdoc />
    public string? UrlFormatString => "https://www.animenewsnetwork.com/encyclopedia/anime.php?id={0}";

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item) => item is Series || item is Movie;
}

/// <summary>
/// The Anime News Network link of a series or movie.
/// </summary>
public class AnnExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc />
    public string Name => "Anime News Network";

    /// <inheritdoc />
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item is Series or Movie && item.TryGetProviderId(SourceIds.Ann, out var id))
        {
            yield return $"https://www.animenewsnetwork.com/encyclopedia/anime.php?id={id}";
        }
    }
}
