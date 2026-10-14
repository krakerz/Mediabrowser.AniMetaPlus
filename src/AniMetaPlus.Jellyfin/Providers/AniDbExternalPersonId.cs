using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace AniMetaPlus.Providers.AniDB;

/// <summary>
/// The AniDB external id for people.
/// </summary>
public class AniDbExternalPersonId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName
        => "AniDB";

    /// <inheritdoc />
    public string Key
        => ProviderNames.AniDb;

    /// <inheritdoc />
    public ExternalIdMediaType? Type
        => ExternalIdMediaType.Person;

    /// <inheritdoc />
    public string? UrlFormatString => "https://anidb.net/creator/{0}";

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item)
        => item is Person;
}
