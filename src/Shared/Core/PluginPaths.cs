using System;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

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
    /// Gets the folder for the answers the sources gave and the mapping files: what could be
    /// fetched again, but only at the cost of requests. It sits in the data folder rather than
    /// the server's cache folder, which the server cleans out and people clear by hand; losing
    /// it would cost a request per anime, and the stale copies AniDB falls back on during a ban.
    /// </summary>
    /// <param name="paths">The server paths.</param>
    /// <returns>The folder, created when missing.</returns>
    public static string CacheFolder(IApplicationPaths paths)
    {
        var folder = Path.Combine(DataFolder(paths), "cache");
        Directory.CreateDirectory(folder);

        return folder;
    }

    /// <summary>
    /// Moves what earlier versions, and the AniDB plugin before them, kept in the server's
    /// cache folder into <see cref="CacheFolder"/>. Run at startup, before anything reads it.
    /// </summary>
    /// <param name="paths">The server paths.</param>
    /// <param name="logger">The logger.</param>
    public static void MoveLegacyCache(IApplicationPaths paths, ILogger logger)
    {
        var moved = MoveTree(Path.Combine(paths.CachePath, "anidb"), CacheFolder(paths))
            + MoveTree(Path.Combine(paths.CachePath, "anidb-people"), Path.Combine(CacheFolder(paths), "people"));

        if (moved > 0)
        {
            logger.LogInformation("AniMeta+ moved {Count} cached answers out of the server's cache folder into {Folder}", moved, CacheFolder(paths));
        }
    }

    private static int MoveTree(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            return 0;
        }

        var moved = 0;

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList())
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));

            try
            {
                // An answer's age is its file's write time, and a move between volumes is a copy
                // that would make every answer look fresh, so the time is carried over.
                var written = File.GetLastWriteTimeUtc(file);

                if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= written)
                {
                    File.Delete(file);

                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target, true);
                File.SetLastWriteTimeUtc(target, written);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left where it is, and read from there no more: it is fetched again when needed.
            }
        }

        try
        {
            if (!Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Any())
            {
                Directory.Delete(from, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty folder left behind costs nothing.
        }

        return moved;
    }
}
