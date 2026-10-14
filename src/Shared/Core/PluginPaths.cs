using System.IO;
using MediaBrowser.Common.Configuration;

namespace AniMetaPlus.Core;

/// <summary>
/// Where the plugin keeps its files.
/// </summary>
public static class PluginPaths
{
    /// <summary>
    /// The name of the folder the plugin's own data lives in.
    /// </summary>
    public const string FolderName = "AniMetaPlus";

    /// <summary>
    /// Gets the folder for what the owner decided and must survive: the database and the
    /// titles dump. It sits beside the plugin's settings so that a backup of the server's
    /// configuration takes it along.
    /// </summary>
    /// <param name="paths">The server paths.</param>
    /// <returns>The folder, created when missing.</returns>
    public static string DataFolder(IApplicationPaths paths)
    {
        var folder = Path.Combine(paths.PluginConfigurationsPath, FolderName);
        Directory.CreateDirectory(folder);

        return folder;
    }

    /// <summary>
    /// Gets the folder for what can be fetched or rebuilt again, which backups may leave out.
    /// </summary>
    /// <param name="paths">The server paths.</param>
    /// <returns>The folder, created when missing.</returns>
    public static string CacheFolder(IApplicationPaths paths)
    {
        var folder = Path.Combine(paths.CachePath, "anidb");
        Directory.CreateDirectory(folder);

        return folder;
    }
}
