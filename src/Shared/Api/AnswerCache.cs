using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AniMetaPlus.Core;
using AniMetaPlus.Providers.AniDB.Metadata;
using AniMetaPlus.Sources;

namespace AniMetaPlus.Api;

/// <summary>
/// What one source has cached.
/// </summary>
/// <param name="Source">The source id.</param>
/// <param name="Answers">How many answers are cached.</param>
/// <param name="OldestUtc">When the oldest answer scans still reuse was fetched.</param>
/// <param name="NewestUtc">When the newest was fetched.</param>
/// <param name="Expired">How many scans and tasks no longer reuse.</param>
/// <param name="ScanReuseDays">How many days scans and tasks reuse an answer.</param>
/// <param name="ManualReuseMinutes">How many minutes a refresh by hand reuses an answer.</param>
public sealed record CacheSourceStatus(
    string Source,
    int Answers,
    DateTime? OldestUtc,
    DateTime? NewestUtc,
    int Expired,
    double ScanReuseDays,
    double ManualReuseMinutes);

/// <summary>
/// What resetting a source's cache did.
/// </summary>
/// <param name="Source">The source id.</param>
/// <param name="Expired">How many answers were marked as expired.</param>
/// <param name="Message">What happened, for the settings page.</param>
public sealed record CacheExpireResult(string Source, int Expired, string Message);

/// <summary>
/// The answers AniDB, AniList and MyAnimeList gave, as cached on disk. Each answer's age is its
/// file's write time, so resetting one backdates the file: the next lookup asks again, while
/// the old copy stays on disk for AniDB to fall back on during a ban.
/// </summary>
internal static class AnswerCache
{
    private static readonly string[] _sources = [SourceIds.AniDb, SourceIds.AniList, SourceIds.Mal];

    /// <summary>
    /// Describes what each source has cached.
    /// </summary>
    /// <returns>One entry per source.</returns>
    public static IReadOnlyList<CacheSourceStatus> Status()
    {
        var scanAge = RefreshScope.ConfiguredMaxAge;
        var now = DateTime.UtcNow;

        return _sources.Select(source =>
        {
            var written = Files(source).Select(f => f.LastWriteTimeUtc).ToList();

            // A reset answer's write time is a made-up past one, so only current answers date the cache.
            var current = written.Where(w => now - w <= scanAge).ToList();
            var manual = source == SourceIds.AniDb ? RefreshScope.ManualAniDbMaxAge : RefreshScope.ManualSourceMaxAge;

            return new CacheSourceStatus(
                source,
                written.Count,
                current.Count == 0 ? null : current.Min(),
                current.Count == 0 ? null : current.Max(),
                written.Count - current.Count,
                scanAge.TotalDays,
                manual.TotalMinutes);
        }).ToList();
    }

    /// <summary>
    /// Marks every cached answer of a source as expired, so the next lookup asks again.
    /// </summary>
    /// <param name="source">The source id.</param>
    /// <returns>What happened.</returns>
    public static CacheExpireResult Expire(string source)
    {
        var id = _sources.FirstOrDefault(s => string.Equals(s, source, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Not a cached source: " + source, nameof(source));

        // A day past the scan window, so that neither a scan nor a refresh by hand reuses it.
        var past = DateTime.UtcNow - RefreshScope.ConfiguredMaxAge - TimeSpan.FromDays(1);
        var expired = 0;

        foreach (var file in Files(id))
        {
            try
            {
                file.LastWriteTimeUtc = past;
                expired++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One file that cannot be touched is asked again only when it lapses by itself.
            }
        }

        var next = id == SourceIds.AniDb
            ? "The next refresh or scan asks AniDB again for each of them, one request every " + (Plugin.Instance.Configuration.RequestIntervalMs / 1000d) + " s; the old copies stay in use while AniDB is banned."
            : "The next refresh or scan asks " + id + " again for each of them.";

        return new CacheExpireResult(id, expired, expired + " cached " + id + " answers reset. " + next);
    }

    private static IEnumerable<FileInfo> Files(string source)
    {
        var paths = Plugin.Instance.AppPaths;

        if (source == SourceIds.AniDb)
        {
            // An anime's age is its series document's: the episode documents beside it are
            // written with it and read only once it is current.
            var series = new DirectoryInfo(Path.GetDirectoryName(AniDbSeriesProvider.GetSeriesDataPath(paths, "0"))!);

            return series.Exists
                ? series.EnumerateDirectories().Select(d => new FileInfo(Path.Combine(d.FullName, "series.xml"))).Where(f => f.Exists)
                : [];
        }

        var folder = new DirectoryInfo(Path.Combine(PluginPaths.CacheFolder(paths), "sources", source.ToLowerInvariant()));

        return folder.Exists ? folder.EnumerateFiles("*.json") : [];
    }
}
