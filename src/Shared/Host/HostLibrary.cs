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
using MediaBrowser.Controller.Collections;
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
public sealed class HostLibrary : IHostRefresher, IHostImages, IHostCollections
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ICollectionManager? _collectionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="HostLibrary"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="collectionManager">The collection manager, for the tasks that make collections.</param>
    public HostLibrary(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ICollectionManager? collectionManager = null)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _collectionManager = collectionManager;
        HostServices.FileSystem = fileSystem;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> CorrectIds(string itemPath, IReadOnlyDictionary<string, string> ids)
    {
        var changed = new List<string>();
        if (string.IsNullOrEmpty(itemPath) || _libraryManager.FindByPath(itemPath, null) is not { } found)
        {
            return changed;
        }

        // By id rather than by path: that is the library's cached copy, the one a refresh fills.
#if EMBY
        var item = _libraryManager.GetItemById(found.InternalId) ?? found;
#else
        var item = _libraryManager.GetItemById(found.Id) ?? found;
#endif

        foreach (var (provider, id) in ids)
        {
            var current = item.GetProviderId(provider);
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(current) && !string.Equals(current, id, StringComparison.Ordinal))
            {
                item.SetProviderId(provider, id);
                changed.Add(provider + " " + current + " -> " + id);
            }
        }

        return changed;
    }

    /// <inheritdoc />
    public async Task<bool> SaveIds(string itemPath, IReadOnlyDictionary<string, string> ids, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(itemPath) || _libraryManager.FindByPath(itemPath, null) is not { } found)
        {
            return false;
        }

#if EMBY
        var item = _libraryManager.GetItemById(found.InternalId) ?? found;
#else
        var item = _libraryManager.GetItemById(found.Id) ?? found;
#endif

        foreach (var (provider, id) in ids)
        {
            if (string.IsNullOrEmpty(id))
            {
                item.ProviderIds.Remove(provider);
            }
            else
            {
                item.SetProviderId(provider, id);
            }
        }

#if EMBY
        item.UpdateToRepository(ItemUpdateType.MetadataEdit);
        await Task.CompletedTask.ConfigureAwait(false);
#else
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
#endif

        return true;
    }

    /// <inheritdoc />
    public bool Refresh(string itemPath, bool replaceAll) => Refresh(itemPath, replaceAll, false);

    /// <inheritdoc />
    public bool Refresh(string itemPath, bool replaceAll, bool replaceImages)
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
            ReplaceAllMetadata = replaceAll,
            ReplaceAllImages = replaceImages
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
    /// Lists the anime library series and movies that carry an AniDB id.
    /// </summary>
    /// <returns>Each item with its AniDB id.</returns>
    public IReadOnlyList<(BaseItem Item, string AnimeId)> ListAniDbSeriesAndMovies()
        => ListAniDbItems().Where(x => x.Item is not Season).ToList();

    /// <summary>
    /// Lists the anime library series and movies that carry an AniDB, AniList or MyAnimeList id:
    /// the ones whose titles can be found without asking anyone.
    /// </summary>
    /// <returns>The items.</returns>
    public IReadOnlyList<BaseItem> ListTitledItems()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
#if EMBY
            IncludeItemTypes = ["Series", "Movie"],
#else
            IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie],
#endif
            Recursive = true,
            DtoOptions = new DtoOptions(true)
        });

        return items
            .Where(item => new[] { ProviderNames.AniDb, SourceIds.AniList, SourceIds.Mal }.Any(id => !string.IsNullOrEmpty(item.GetProviderId(id))) && IsAnimeLibraryItem(item))
            .ToList();
    }

    /// <summary>
    /// Gets when an item premiered.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The date, or <c>null</c> when it has none.</returns>
    public static DateTime? Premiere(BaseItem item)
#if EMBY
        => item.PremiereDate?.UtcDateTime;
#else
        => item.PremiereDate;
#endif

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
    /// Writes an item's original title straight to the library, without a refresh.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="title">The original title.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task SaveOriginalTitle(BaseItem item, string? title, CancellationToken cancellationToken)
    {
        item.OriginalTitle = title;
#if EMBY
        item.UpdateToRepository(ItemUpdateType.MetadataEdit);
        await Task.CompletedTask.ConfigureAwait(false);
#else
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
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
    public Task SavePoster(BaseItem item, Stream image, string mimeType, CancellationToken cancellationToken)
        => SaveImage(item, ImageType.Primary, image, mimeType, cancellationToken);

    private async Task SaveImage(BaseItem item, ImageType type, Stream image, string mimeType, CancellationToken cancellationToken)
    {
#if EMBY
        await _providerManager.SaveImage(item, _libraryManager.GetLibraryOptions(item), image, mimeType.AsMemory(), type, null, [], new DirectoryService(_fileSystem), true, cancellationToken).ConfigureAwait(false);
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

                await _providerManager.SaveImage(item, temporary, mimeType, type, null, false, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        else
        {
            await _providerManager.SaveImage(item, image, mimeType, type, null, cancellationToken).ConfigureAwait(false);
        }

        await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
#endif
    }

    /// <inheritdoc />
    public async Task<int> CopyArt(string collectionId, string sourceItemId, bool replace, CancellationToken cancellationToken)
    {
        if (FindItem(collectionId) is not BoxSet box || FindItem(sourceItemId) is not { } source)
        {
            return 0;
        }

        var copied = 0;
        var linked = false;

        // Once given its art, only what the collection lacks: art the owner set later stays.
        foreach (var type in new[] { ImageType.Primary, ImageType.Backdrop })
        {
            if ((!replace && box.HasImage(type, 0)) || source.GetImagePath(type, 0) is not { Length: > 0 } path)
            {
                continue;
            }

            if (Uri.TryCreate(path, UriKind.Absolute, out var link) && (link.Scheme == Uri.UriSchemeHttp || link.Scheme == Uri.UriSchemeHttps))
            {
                // A server that does not download artwork keeps the source's link. Saved from it as
                // a file: a collection holding only a link gets Emby's collage drawn back over it.
#if EMBY
                await _providerManager.SaveImage(box, _libraryManager.GetLibraryOptions(box), path, type, null, [], new DirectoryService(_fileSystem), true, cancellationToken).ConfigureAwait(false);
#else
                await _providerManager.SaveImage(box, path, type, null, cancellationToken).ConfigureAwait(false);
#endif
                linked = true;
                copied++;

                continue;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            await using var image = File.OpenRead(path);
            await SaveImage(box, type, image, MimeTypeOf(path), cancellationToken).ConfigureAwait(false);
            copied++;
        }

        if (linked)
        {
#if EMBY
            box.UpdateToRepository(ItemUpdateType.ImageUpdate);
#else
            await box.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
#endif
        }

        return copied;
    }

    private static string MimeTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg"
    };

    /// <inheritdoc />
    public HostCollection? FindCollection(string collectionId)
    {
        if (FindItem(collectionId) is not BoxSet box)
        {
            return null;
        }

#if EMBY
        var members = _libraryManager.GetItemList(new InternalItemsQuery { CollectionIds = [box.InternalId], Recursive = true });
#else
        var members = box.GetLinkedChildren();
#endif

        return new HostCollection(box.Name ?? string.Empty, members.Select(ItemId).ToList());
    }

    /// <inheritdoc />
    public async Task<string> CreateCollection(string name, IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken)
    {
        var manager = _collectionManager ?? throw new InvalidOperationException("No collection manager.");

        // Locked, so that a metadata fetcher does not rename it.
#if EMBY
        var box = await manager.CreateCollection(new CollectionCreationOptions { Name = name, ItemIdList = itemIds.Select(id => long.Parse(id, CultureInfo.InvariantCulture)).ToArray(), IsLocked = true }).ConfigureAwait(false);
        return ItemId(box);
#else
        var box = await manager.CreateCollectionAsync(new CollectionCreationOptions { Name = name, ItemIdList = itemIds.ToList(), IsLocked = true }).ConfigureAwait(false);
        return ItemId(box);
#endif
    }

    /// <inheritdoc />
    public async Task AddToCollection(string collectionId, IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken)
    {
        var manager = _collectionManager ?? throw new InvalidOperationException("No collection manager.");
#if EMBY
        await manager.AddToCollection(long.Parse(collectionId, CultureInfo.InvariantCulture), itemIds.Select(id => long.Parse(id, CultureInfo.InvariantCulture)).ToArray()).ConfigureAwait(false);
#else
        await manager.AddToCollectionAsync(Guid.ParseExact(collectionId, "N"), itemIds.Select(id => Guid.ParseExact(id, "N")).ToList()).ConfigureAwait(false);
#endif
    }

    /// <inheritdoc />
    public async Task RemoveFromCollection(string collectionId, IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken)
    {
        var manager = _collectionManager ?? throw new InvalidOperationException("No collection manager.");
#if EMBY
        if (FindItem(collectionId) is BoxSet box)
        {
            manager.RemoveFromCollection(box, itemIds.Select(id => long.Parse(id, CultureInfo.InvariantCulture)).ToArray());
        }
#else
        await manager.RemoveFromCollectionAsync(Guid.ParseExact(collectionId, "N"), itemIds.Select(id => Guid.ParseExact(id, "N")).ToList()).ConfigureAwait(false);
#endif
    }

    /// <inheritdoc />
    public Task DeleteCollection(string collectionId, CancellationToken cancellationToken)
    {
        if (FindItem(collectionId) is BoxSet box)
        {
            _libraryManager.DeleteItem(box, new DeleteOptions { DeleteFileLocation = true });
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RenameCollection(string collectionId, string name, CancellationToken cancellationToken)
    {
        var box = FindItem(collectionId);
        if (box == null)
        {
            return;
        }

        box.Name = name;
#if EMBY
        box.UpdateToRepository(ItemUpdateType.MetadataEdit);
        await Task.CompletedTask.ConfigureAwait(false);
#else
        await box.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
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
