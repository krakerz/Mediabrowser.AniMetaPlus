using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AniMetaPlus.Core;

/// <summary>
/// Refreshes items through the server, which the shared code cannot reach itself.
/// </summary>
public interface IHostRefresher
{
    /// <summary>
    /// Queues a metadata refresh of the item at a path.
    /// </summary>
    /// <param name="itemPath">The folder of a series, or the file of a movie or episode.</param>
    /// <param name="replaceAll">Whether existing metadata is replaced rather than filled in.</param>
    /// <returns><c>true</c> when the item was found and queued.</returns>
    bool Refresh(string itemPath, bool replaceAll);
}

/// <summary>
/// Puts posters back through the server, which the shared code cannot reach itself.
/// </summary>
public interface IHostImages
{
    /// <summary>
    /// Replaces an item's poster with an image file.
    /// </summary>
    /// <param name="itemId">The server's item id.</param>
    /// <param name="file">The image file.</param>
    /// <param name="mimeType">The image's media type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>false</c> when the item no longer exists.</returns>
    Task<bool> RestorePoster(string itemId, string file, string mimeType, CancellationToken cancellationToken);
}

/// <summary>
/// A collection as the server holds it.
/// </summary>
/// <param name="Name">The collection's name.</param>
/// <param name="MemberIds">The server ids of the items in it.</param>
public sealed record HostCollection(string Name, IReadOnlyCollection<string> MemberIds);

/// <summary>
/// Makes and changes collections through the server, which the shared code cannot reach itself.
/// </summary>
public interface IHostCollections
{
    /// <summary>
    /// Finds a collection.
    /// </summary>
    /// <param name="collectionId">The server's id of the collection.</param>
    /// <returns>The collection, or <c>null</c> when it no longer exists.</returns>
    HostCollection? FindCollection(string collectionId);

    /// <summary>
    /// Makes a collection holding the items given.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="itemIds">The server ids of the items.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The server's id of the new collection.</returns>
    Task<string> CreateCollection(string name, IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken);

    /// <summary>
    /// Adds items to a collection.
    /// </summary>
    /// <param name="collectionId">The server's id of the collection.</param>
    /// <param name="itemIds">The server ids of the items.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task AddToCollection(string collectionId, IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken);

    /// <summary>
    /// Takes items out of a collection, without deleting them.
    /// </summary>
    /// <param name="collectionId">The server's id of the collection.</param>
    /// <param name="itemIds">The server ids of the items.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RemoveFromCollection(string collectionId, IReadOnlyCollection<string> itemIds, CancellationToken cancellationToken);

    /// <summary>
    /// Renames a collection.
    /// </summary>
    /// <param name="collectionId">The server's id of the collection.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task RenameCollection(string collectionId, string name, CancellationToken cancellationToken);

    /// <summary>
    /// Copies an item's poster and first backdrop onto a collection, from the files the server
    /// already holds.
    /// </summary>
    /// <param name="collectionId">The server's id of the collection.</param>
    /// <param name="sourceItemId">The server's id of the item whose art is copied.</param>
    /// <param name="replace">Whether to replace images the collection has - the collage Emby makes for a new one - rather than only fill missing ones.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many images were copied.</returns>
    Task<int> CopyArt(string collectionId, string sourceItemId, bool replace, CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>
/// What each server hands the shared code at startup.
/// </summary>
public static class HostServices
{
    /// <summary>
    /// Gets or sets the refresher of the running server.
    /// </summary>
    public static IHostRefresher? Refresher { get; set; }

    /// <summary>
    /// Gets or sets the poster restorer of the running server.
    /// </summary>
    public static IHostImages? Images { get; set; }

    /// <summary>
    /// Gets or sets the server's file system, which the server's own providers are called with
    /// on Emby.
    /// </summary>
    public static MediaBrowser.Model.IO.IFileSystem? FileSystem { get; set; }
}
