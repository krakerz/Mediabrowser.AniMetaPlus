using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace AniMetaPlus.Providers.AniDB;

/// <summary>
/// The AniDB external id for seasons.
/// </summary>
public class AniDbExternalSeasonId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName
        => "AniDB";

    /// <inheritdoc />
    public string Key
        => ProviderNames.AniDb;

    /// <inheritdoc />
    public ExternalIdMediaType? Type
        => ExternalIdMediaType.Season;

    /// <inheritdoc />
    public string? UrlFormatString => "https://anidb.net/anime/{0}";

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item)
        => item is Season;
}
