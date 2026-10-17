using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using AniMetaPlus.Providers;
using AniMetaPlus.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
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
public sealed class HostLibrary : IHostRefresher, IHostImages
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
    /// Gets the id the server knows an item by: the internal number on Emby, the guid on Jellyfin.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The id.</returns>
    public static string ItemId(BaseItem item)
#if EMBY
        => item.InternalId.ToString(CultureInfo.InvariantCulture);
#else
        => item.Id.ToString("N");
#endif

    /// <summary>
    /// Gets the file of an item's poster.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The path, or <c>null</c> when it has no poster.</returns>
    public static string? PosterPath(BaseItem item) => item.GetImagePath(ImageType.Primary, 0);

    /// <summary>
    /// Names the series a season belongs to, and the kind of item, for the poster backup list.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The kind, and the series name for a season.</returns>
    public static (string Kind, string SeriesName) DescribeItem(BaseItem item) => item switch
    {
        Season season => ("Season", season.SeriesName ?? string.Empty),
        Movie => ("Movie", string.Empty),
        _ => ("Series", string.Empty)
    };

    /// <summary>
    /// Finds an item by the id <see cref="ItemId"/> gives.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The item, or <c>null</c> when the server no longer has it.</returns>
    public BaseItem? FindItem(string id)
    {
#if EMBY
        return long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var internalId) ? _libraryManager.GetItemById(internalId) : null;
#else
        return Guid.TryParseExact(id, "N", out var guid) ? _libraryManager.GetItemById(guid) : null;
#endif
    }

    /// <inheritdoc />
    public async Task<bool> RestorePoster(string itemId, string file, string mimeType, CancellationToken cancellationToken)
    {
        var item = FindItem(itemId);
        if (item == null)
        {
            return false;
        }

        await using var stream = new MemoryStream(await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false));
        await SavePoster(item, stream, mimeType, cancellationToken).ConfigureAwait(false);

        return true;
    }

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
        if (string.IsNullOrEmpty(item.ContainingFolderPath))
        {
            // An item without a folder of its own (a season made from files in the series folder)
            // has nowhere beside the media to save to, which fails the save when the library keeps
            // artwork with the media. Only the file overload can send it to the internal metadata
            // folder instead.
            var temporary = Path.Combine(Path.GetTempPath(), "animetaplus-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var file = File.Create(temporary))
                {
                    await image.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }

                await _providerManager.SaveImage(item, temporary, mimeType, ImageType.Primary, null, false, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        else
        {
            await _providerManager.SaveImage(item, image, mimeType, ImageType.Primary, null, cancellationToken).ConfigureAwait(false);
        }

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
