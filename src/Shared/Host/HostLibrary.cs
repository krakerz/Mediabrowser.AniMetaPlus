using System;
using System.Collections.Generic;
using System.Linq;
using AniMetaPlus.Core;
using AniMetaPlus.Providers;
using AniMetaPlus.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
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
            DtoOptions = new DtoOptions(false)
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
    /// Whether the library an item is in has this plugin turned on for the item's kind.
    /// </summary>
    private bool IsAnimeLibraryItem(BaseItem item)
    {
        var options = _libraryManager.GetLibraryOptions(item);
        var type = options?.GetTypeOptions(item is Movie ? "Movie" : "Series");

        return type?.MetadataFetchers?.Contains(ProviderNames.Display, StringComparer.OrdinalIgnoreCase) == true;
    }
}
