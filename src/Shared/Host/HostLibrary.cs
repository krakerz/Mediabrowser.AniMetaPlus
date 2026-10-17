using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using AniMetaPlus.Providers;
using AniMetaPlus.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
#if JELLYFIN
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
#else
using MediaBrowser.Controller.Dto;
#endif

namespace AniMetaPlus.Host;

/// <summary>
/// Refreshes items through the server's own queue, and lists the anime libraries' items.
/// </summary>
public sealed class HostLibrary : IHostRefresher
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="HostLibrary"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="fileSystem">The file system.</param>
    public HostLibrary(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        HostServices.FileSystem = fileSystem;
    }

    /// <inheritdoc />
    public bool Refresh(string itemPath, bool replaceAll)
    {
        if (string.IsNullOrEmpty(itemPath))
        {
            return false;
        }

        var item = _libraryManager.FindByPath(itemPath, null);
        if (item == null)
        {
            return false;
        }

        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllMetadata = replaceAll
        };

#if EMBY
        _providerManager.QueueRefresh(item.InternalId, options, RefreshPriority.Normal);
#else
        _providerManager.QueueRefresh(item.Id, options, RefreshPriority.Normal);
#endif

        return true;
    }

    /// <summary>
    /// Lists the series and movies of the libraries where this plugin is turned on as a
    /// metadata provider.
    /// </summary>
    /// <returns>The items.</returns>
    public IReadOnlyList<ScanItem> ListAnimeItems()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
#if EMBY
            IncludeItemTypes = ["Series", "Movie"],
#else
            IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie],
#endif
            Recursive = true,

            // Every field, provider ids included: Jellyfin leaves them out of a bare query, and
            // the scan would then take every identified show for an unidentified one.
            DtoOptions = new DtoOptions(true)
        });

        var result = new List<ScanItem>();

        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Path) || !IsAnimeLibraryItem(item))
            {
                continue;
            }

            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (provider, id) in item.ProviderIds)
            {
                ids[provider] = id;
            }

            result.Add(new ScanItem(
                item.Path,
                item.Name ?? string.Empty,
                item.ProductionYear,
                item is Movie ? "Movie" : "Series",
                ids,
                item.DateCreated.UtcDateTime()));
        }

        return result;
    }

    /// <summary>
    /// Lists the anime library series, seasons and movies that carry an AniDB id.
    /// </summary>
    /// <returns>Each item with its AniDB id.</returns>
    public IReadOnlyList<(BaseItem Item, string AnimeId)> ListAniDbItems()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
#if EMBY
            IncludeItemTypes = ["Series", "Season", "Movie"],
#else
            IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Movie],
#endif
            Recursive = true,
            DtoOptions = new DtoOptions(true)
        });

        var result = new List<(BaseItem, string)>();

        foreach (var item in items)
        {
            var animeId = item.GetProviderId(ProviderNames.AniDb);
            if (!string.IsNullOrEmpty(animeId) && IsAnimeLibraryItem(item))
            {
                result.Add((item, animeId));
            }
        }

        return result;
    }

    /// <summary>
    /// Whether an item has a poster.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns><c>true</c> when it has one.</returns>
    public static bool HasPoster(BaseItem item) => item.HasImage(ImageType.Primary, 0);

    /// <summary>
    /// Queues an images-only refresh that fills the image types an item is missing; images
    /// already in place are kept.
    /// </summary>
    /// <param name="item">The item.</param>
    public void QueueMissingImagesRefresh(BaseItem item)
    {
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh
        };

#if EMBY
        _providerManager.QueueRefresh(item.InternalId, options, RefreshPriority.Low);
#else
        _providerManager.QueueRefresh(item.Id, options, RefreshPriority.Low);
#endif
    }

    /// <summary>
    /// Replaces an item's poster with the image given, through the server's own image saving.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="image">The image.</param>
    /// <param name="mimeType">Its media type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task SavePoster(BaseItem item, Stream image, string mimeType, CancellationToken cancellationToken)
    {
#if EMBY
        await _providerManager.SaveImage(item, _libraryManager.GetLibraryOptions(item), image, mimeType.AsMemory(), ImageType.Primary, null, [], new DirectoryService(_fileSystem), true, cancellationToken).ConfigureAwait(false);
        item.UpdateToRepository(ItemUpdateType.ImageUpdate);
#else
        await _providerManager.SaveImage(item, image, mimeType, ImageType.Primary, null, cancellationToken).ConfigureAwait(false);
        await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
#endif
    }

    /// <summary>
    /// Whether the library an item is in has this plugin turned on for the item's kind.
    /// </summary>
    private bool IsAnimeLibraryItem(BaseItem item)
    {
        var options = _libraryManager.GetLibraryOptions(item);
        var type = options?.GetTypeOptions(item is Movie ? "Movie" : "Series");

        return type?.MetadataFetchers?.Contains(ProviderNames.Display, StringComparer.OrdinalIgnoreCase) == true;
    }
}
