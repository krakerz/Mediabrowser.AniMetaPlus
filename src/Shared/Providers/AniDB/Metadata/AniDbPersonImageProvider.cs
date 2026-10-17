using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace AniMetaPlus.Providers.AniDB.Metadata;

/// <summary>
/// The AniDB image provider for people.
/// </summary>
/// <param name="paths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
public class AniDbPersonImageProvider(IApplicationPaths paths) : IRemoteImageProvider
{
    private readonly IApplicationPaths _paths = paths;

    /// <inheritdoc />
    public string Name => ProviderNames.Display;

    /// <inheritdoc />
    public bool Supports(BaseItem item)
    {
        return item is Person;
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        yield return ImageType.Primary;
    }

#if EMBY
    /// <inheritdoc />
    public Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, MediaBrowser.Model.Configuration.LibraryOptions libraryOptions, CancellationToken cancellationToken)
        => GetImages(item, cancellationToken);

#endif
    /// <inheritdoc />
    public Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var infos = new List<RemoteImageInfo>();

        var person = AniDbSeriesProvider.GetPersonInfo(Core.PluginPaths.CacheFolder(_paths), item.Name);
        if (person != null && !string.IsNullOrEmpty(person.Image))
        {
            infos.Add(new RemoteImageInfo
            {
                Url = person.Image,
                Type = ImageType.Primary,
                ProviderName = Name
            });
        }

        return Task.FromResult<IEnumerable<RemoteImageInfo>>(infos);
    }

    /// <inheritdoc />
    public async Task<HostImageResponse> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return await AniDbSeriesProvider.GetAniDbImage(url, cancellationToken).ConfigureAwait(false);
    }
}
