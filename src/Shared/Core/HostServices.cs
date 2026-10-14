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
/// What each server hands the shared code at startup.
/// </summary>
public static class HostServices
{
    /// <summary>
    /// Gets or sets the refresher of the running server.
    /// </summary>
    public static IHostRefresher? Refresher { get; set; }

    /// <summary>
    /// Gets or sets the server's file system, which the server's own providers are called with
    /// on Emby.
    /// </summary>
    public static MediaBrowser.Model.IO.IFileSystem? FileSystem { get; set; }
}
