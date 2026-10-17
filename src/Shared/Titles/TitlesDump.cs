using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using AniMetaPlus.Core;
using AniMetaPlus.Providers.AniDB.Identity;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Titles;

/// <summary>
/// The outcome of asking for a fresh titles dump.
/// </summary>
/// <param name="Downloaded">Whether a new dump was downloaded and is now in use.</param>
/// <param name="Message">What happened, for the settings page and the task log.</param>
public sealed record TitlesRefreshResult(bool Downloaded, string Message);

/// <summary>
/// What is known about the titles dump in use, for the settings page.
/// </summary>
/// <param name="SourcePath">The dump file the plugin reads.</param>
/// <param name="SourceExists">Whether that file exists.</param>
/// <param name="SourceWrittenUtc">When that file was last written.</param>
/// <param name="AnimeCount">How many anime the imported copy holds, or zero before an import.</param>
/// <param name="NextRefreshUtc">When the scheduled task will next download one, or <c>null</c> when it does not.</param>
public sealed record TitlesDumpStatus(string SourcePath, bool SourceExists, DateTime? SourceWrittenUtc, int AnimeCount, DateTime? NextRefreshUtc);

/// <summary>
/// The AniDB titles dump the plugin identifies anime from. It is read from a local file, and the
/// only thing that ever downloads one is the scheduled task: nothing on a lookup path does.
/// AniDB bans a client that fetches the dump more than once a day; downloads are kept at
/// least <see cref="Configuration.PluginConfiguration.MinimumTitlesRefreshDays"/> days apart.
/// </summary>
public static class TitlesDump
{
    /// <summary>
    /// The file name the dump is kept under in the plugin's data folder.
    /// </summary>
    public const string DefaultFileName = "anime-titles.dat.gz";

    private const string DatUrl = "https://anidb.net/api/anime-titles.dat.gz";
    private const string XmlUrl = "https://anidb.net/api/anime-titles.xml.gz";

    /// <summary>
    /// A real dump holds tens of thousands of titles. Anything far short of that is an error
    /// page or a truncated download, and must not replace a dump that works.
    /// </summary>
    private const int MinimumTitleCount = 10000;

    private static readonly TimeSpan _minimumDownloadGap = TimeSpan.FromDays(Configuration.PluginConfiguration.MinimumTitlesRefreshDays);
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly SemaphoreSlim _refreshGate = new(1, 1);
    private static int _animeCount;
    private static Dictionary<string, string>? _mainTitles;

    /// <summary>
    /// Gets the path of the dump the plugin reads.
    /// </summary>
    /// <param name="paths">The server paths.</param>
    /// <returns>The configured path, or the default file in the plugin's data folder.</returns>
    public static string SourcePath(IApplicationPaths paths)
    {
        var configured = Plugin.Instance?.Configuration.TitlesDumpPath;

        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(PluginPaths.DataFolder(paths), DefaultFileName)
            : configured.Trim();
    }

    /// <summary>
    /// Gets the path of the imported copy every lookup reads, in the layout of AniDB's XML dump.
    /// </summary>
    /// <param name="paths">The server paths.</param>
    /// <returns>The path.</returns>
    public static string ImportedPath(IApplicationPaths paths)
        => Path.Combine(PluginPaths.CacheFolder(paths), "titles.xml");

    /// <summary>
    /// Makes sure the imported copy reflects the dump on disk, importing it when it is missing,
    /// older than the dump, or made from another file. Never downloads anything.
    /// </summary>
    /// <param name="paths">The server paths.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="force">Whether to import even when the imported copy looks current.</param>
    /// <returns><c>true</c> when an imported copy is available to read.</returns>
    public static async Task<bool> EnsureImported(IApplicationPaths paths, ILogger logger, CancellationToken cancellationToken, bool force = false)
    {
        var source = new FileInfo(SourcePath(paths));
        var imported = new FileInfo(ImportedPath(paths));

        if (!source.Exists)
        {
            if (!imported.Exists)
            {
                logger.LogWarning(
                    "There is no AniDB titles dump at {Path}, so anime can only be identified from the mapping sources and by hand. Put anime-titles.dat.gz there, or turn on its refresh in the settings",
                    source.FullName);
            }

            // A copy left by an earlier version still identifies anime.
            return imported.Exists;
        }

        if (!force && IsCurrent(source, imported))
        {
            return true;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            imported.Refresh();
            if (!force && IsCurrent(source, imported))
            {
                return true;
            }

            var count = await Import(source.FullName, imported.FullName, cancellationToken).ConfigureAwait(false);
            File.WriteAllText(StampPath(imported.FullName), Stamp(source));
            _animeCount = count;
            _mainTitles = null;
            AniDbTitleMatcher.Invalidate();

            logger.LogInformation("Imported the AniDB titles dump {Path}: {Count} anime", source.FullName, count);

            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or XmlException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "The AniDB titles dump {Path} could not be read", source.FullName);

            return imported.Exists;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string StampPath(string importedPath) => importedPath + ".source";

    // Which file, and how big, the imported copy was made from: its time alone cannot tell that
    // the path was pointed at an older file.
    private static string Stamp(FileInfo source) => source.FullName + "|" + source.Length.ToString(CultureInfo.InvariantCulture);

    private static bool IsCurrent(FileInfo source, FileInfo imported)
    {
        if (!imported.Exists || imported.Length == 0 || imported.LastWriteTimeUtc < source.LastWriteTimeUtc)
        {
            return false;
        }

        try
        {
            return File.ReadAllText(StampPath(imported.FullName)) == Stamp(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads a fresh dump when one is due, or when asked to. Refuses whenever the dump on
    /// disk is less than a day old, however it is asked.
    /// </summary>
    /// <param name="paths">The server paths.</param>
    /// <param name="httpClient">The client to download with.</param>
    /// <param name="force">Whether to download before the configured interval has passed.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What happened.</returns>
    public static async Task<TitlesRefreshResult> Refresh(IApplicationPaths paths, HttpClient httpClient, bool force, ILogger logger, CancellationToken cancellationToken)
    {
        // One at a time, so that the task and the button cannot both pass the gap check and download.
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RefreshCore(paths, httpClient, force, logger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static async Task<TitlesRefreshResult> RefreshCore(IApplicationPaths paths, HttpClient httpClient, bool force, ILogger logger, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance.Configuration;
        var target = new FileInfo(SourcePath(paths));

        // Counted from the last attempt as well as the file: a failed download leaves the file
        // as it was, and a deleted one leaves nothing to count from.
        var lastAttempt = LastDownloadAttempt(paths);
        var last = target.Exists && target.LastWriteTimeUtc > (lastAttempt ?? DateTime.MinValue) ? target.LastWriteTimeUtc : lastAttempt;

        if (last is { } lastUtc)
        {
            var since = DateTime.UtcNow - lastUtc;

            if (since < _minimumDownloadGap)
            {
                return new TitlesRefreshResult(false, string.Format(
                    CultureInfo.InvariantCulture,
                    "The titles dump was last downloaded {0:0.#} hours ago. AniMeta+ downloads it at most every {1} days, so the next download is allowed in {2:0.#} hours.",
                    since.TotalHours,
                    _minimumDownloadGap.TotalDays,
                    (_minimumDownloadGap - since).TotalHours));
            }
        }

        if (target.Exists && !force && DateTime.UtcNow - target.LastWriteTimeUtc < TimeSpan.FromDays(configuration.TitlesRefreshDays))
        {
            return new TitlesRefreshResult(false, "The titles dump is not due for a refresh yet.");
        }

        if (!force && !configuration.TitlesAutoRefresh)
        {
            return new TitlesRefreshResult(false, "Automatic refresh of the titles dump is turned off.");
        }

        var isXml = target.Name.EndsWith(".xml.gz", StringComparison.OrdinalIgnoreCase) || target.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
        var compressed = target.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        var temporary = DownloadPath(target.FullName);

        try
        {
            var directory = target.DirectoryName;
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Written before asking, and nothing is asked when it cannot be written.
            File.WriteAllText(AttemptPath(paths), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

            using (var response = await httpClient.GetAsync(isXml ? XmlUrl : DatUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var file = File.Create(temporary);

                if (compressed)
                {
                    await body.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    using var unzipped = new GZipStream(body, CompressionMode.Decompress);
                    await unzipped.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
            }

            // Counted before it replaces anything: an error page or a cut-off download must not
            // take the place of a dump that works.
            var titles = CountTitles(temporary);
            if (titles < MinimumTitleCount)
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "The downloaded dump holds only {0} titles.", titles));
            }

            File.Move(temporary, target.FullName, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            logger.LogWarning(ex, "Downloading the AniDB titles dump failed. The dump already on disk stays in use");

            return new TitlesRefreshResult(false, "The download failed: " + ex.Message);
        }

        await EnsureImported(paths, logger, cancellationToken).ConfigureAwait(false);

        return new TitlesRefreshResult(true, "A fresh titles dump was downloaded and imported.");
    }

    private static string AttemptPath(IApplicationPaths paths)
        => Path.Combine(PluginPaths.DataFolder(paths), "titles-download.txt");

    private static DateTime? LastDownloadAttempt(IApplicationPaths paths)
    {
        try
        {
            return DateTime.TryParse(File.ReadAllText(AttemptPath(paths)), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when) ? when.ToUniversalTime() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets what is known about the dump in use.
    /// </summary>
    /// <param name="paths">The server paths.</param>
    /// <returns>The status.</returns>
    public static TitlesDumpStatus GetStatus(IApplicationPaths paths)
    {
        var configuration = Plugin.Instance.Configuration;
        var source = new FileInfo(SourcePath(paths));
        DateTime? written = source.Exists ? source.LastWriteTimeUtc : null;
        var allowed = LastDownloadAttempt(paths) + _minimumDownloadGap;
        DateTime? next = configuration.TitlesAutoRefresh
            ? (written ?? DateTime.UtcNow) + TimeSpan.FromDays(configuration.TitlesRefreshDays)
            : null;
        if (next < allowed)
        {
            next = allowed;
        }

        // After a restart the imported copy is reused rather than imported again, so the count
        // is taken from it: one main title per anime.
        if (_animeCount == 0 && File.Exists(ImportedPath(paths)))
        {
            MainTitle(paths, string.Empty);
            _animeCount = _mainTitles?.Count ?? 0;
        }

        return new TitlesDumpStatus(source.FullName, source.Exists, written, _animeCount, next);
    }

    /// <summary>
    /// Gets the main title of an anime from the imported dump, without asking AniDB.
    /// </summary>
    /// <param name="paths">The server paths.</param>
    /// <param name="animeId">The AniDB id.</param>
    /// <returns>The romaji main title, or <c>null</c> when the dump does not have the anime.</returns>
    public static string? MainTitle(IApplicationPaths paths, string animeId)
    {
        var titles = _mainTitles;

        if (titles == null)
        {
            titles = new Dictionary<string, string>(StringComparer.Ordinal);
            var path = ImportedPath(paths);

            if (File.Exists(path))
            {
                try
                {
                    using var reader = XmlReader.Create(path, new XmlReaderSettings { IgnoreComments = true, CheckCharacters = false });
                    string? aid = null;

                    var more = reader.Read();

                    while (more)
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            if (reader.Name == "anime")
                            {
                                aid = reader.GetAttribute("aid");
                            }
                            else if (reader.Name == "title" && aid != null && reader.GetAttribute("type") == "main")
                            {
                                // Already on the node after the title: reading on would skip it.
                                titles[aid] = reader.ReadElementContentAsString();
                                more = !reader.EOF;
                                continue;
                            }
                        }

                        more = reader.Read();
                    }
                }
                catch (Exception ex) when (ex is XmlException or IOException)
                {
                    // An unreadable dump has no titles to give.
                }
            }

            _mainTitles = titles;
        }

        return titles.TryGetValue(animeId, out var title) ? title : null;
    }

    /// <summary>
    /// Writes the dump at <paramref name="sourcePath"/> out in the layout of AniDB's XML dump.
    /// </summary>
    /// <param name="sourcePath">The dump, as <c>.dat</c> or <c>.xml</c>, gzipped or not.</param>
    /// <param name="importedPath">Where the XML copy goes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many anime the dump holds.</returns>
    internal static async Task<int> Import(string sourcePath, string importedPath, CancellationToken cancellationToken)
    {
        var temporary = importedPath + ".partial";

        try
        {
            int count;

            if (IsXml(sourcePath))
            {
                using (var input = OpenText(sourcePath))
                using (var output = new StreamWriter(temporary, false, new UTF8Encoding(false)))
                {
                    await output.WriteAsync(await input.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                }

                count = CountAnimeInXml(temporary);
            }
            else
            {
                using var input = OpenText(sourcePath);
                using var output = new StreamWriter(temporary, false, new UTF8Encoding(false));
                count = await ConvertDat(input, output, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, importedPath, true);

            return count;
        }
        catch
        {
            TryDelete(temporary);

            throw;
        }
    }

    /// <summary>
    /// Converts the pipe separated dump into AniDB's XML layout, grouping each anime's titles.
    /// </summary>
    /// <param name="input">The <c>.dat</c> text.</param>
    /// <param name="output">Where the XML goes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many anime were written.</returns>
    internal static async Task<int> ConvertDat(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        // Grouped first: the lines of one anime are not guaranteed to be next to each other.
        var anime = new Dictionary<string, List<(string Type, string Language, string Title)>>(StringComparer.Ordinal);
        var order = new List<string>();

        string? line;
        while ((line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
        {
            if (!TryParseDatLine(line, out var aid, out var type, out var language, out var title))
            {
                continue;
            }

            if (!anime.TryGetValue(aid, out var titles))
            {
                titles = [];
                anime[aid] = titles;
                order.Add(aid);
            }

            titles.Add((type, language, title));
        }

        await output.WriteLineAsync("<?xml version=\"1.0\" encoding=\"UTF-8\"?>").ConfigureAwait(false);
        await output.WriteLineAsync("<animetitles>").ConfigureAwait(false);

        foreach (var aid in order)
        {
            await output.WriteLineAsync("<anime aid=\"" + aid + "\">").ConfigureAwait(false);

            foreach (var (type, language, title) in anime[aid])
            {
                await output.WriteLineAsync(string.Concat(
                    "<title xml:lang=\"",
                    SecurityElement.Escape(language),
                    "\" type=\"",
                    type,
                    "\">",
                    SecurityElement.Escape(title),
                    "</title>")).ConfigureAwait(false);
            }

            await output.WriteLineAsync("</anime>").ConfigureAwait(false);
        }

        await output.WriteLineAsync("</animetitles>").ConfigureAwait(false);

        return order.Count;
    }

    /// <summary>
    /// Reads one line of the <c>.dat</c> dump: <c>aid|type|language|title</c>.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="aid">The anime id.</param>
    /// <param name="type">The title type, in the words AniDB's XML dump uses.</param>
    /// <param name="language">The language code.</param>
    /// <param name="title">The title.</param>
    /// <returns><c>false</c> for a comment or a malformed line.</returns>
    internal static bool TryParseDatLine(string line, out string aid, out string type, out string language, out string title)
    {
        aid = type = language = title = string.Empty;

        if (string.IsNullOrWhiteSpace(line) || line[0] == '#')
        {
            return false;
        }

        // Split on the first three only: a few titles have a pipe in them.
        var parts = line.Split('|', 4);
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || string.IsNullOrWhiteSpace(parts[3]))
        {
            return false;
        }

        aid = parts[0];
        language = parts[2];
        title = parts[3];
        type = parts[1] switch
        {
            "1" => "main",
            "2" => "syn",
            "3" => "short",
            "4" => "official",
            "5" => "kana",
            "6" => "card",
            _ => "syn"
        };

        return true;
    }

    private static bool IsXml(string path)
        => path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xml.gz", StringComparison.OrdinalIgnoreCase);

    private static StreamReader OpenText(string path)
    {
        Stream stream = File.OpenRead(path);

        // Unpacked by what the file holds, not by its name: an HTTP client that already
        // decompressed the download leaves plain text under a .gz name.
        var first = stream.ReadByte();
        var second = stream.ReadByte();
        stream.Position = 0;

        if (first == 0x1f && second == 0x8b)
        {
            stream = new GZipStream(stream, CompressionMode.Decompress);
        }

        return new StreamReader(stream, Encoding.UTF8);
    }

    /// <summary>
    /// Where a download is held until it is checked. It keeps the dump's own extension, which is
    /// what tells the reader whether to unpack it and how to parse it; an added ".download" suffix
    /// made every download read as 0 titles.
    /// </summary>
    /// <param name="targetPath">The dump's path.</param>
    /// <returns>The download's path, beside the dump.</returns>
    internal static string DownloadPath(string targetPath)
        => Path.Combine(Path.GetDirectoryName(targetPath) ?? string.Empty, ".download-" + Path.GetFileName(targetPath));

    /// <summary>
    /// Counts the titles in a dump, unpacking and parsing it as its extension says.
    /// </summary>
    /// <param name="path">The dump's path.</param>
    /// <returns>How many titles it holds.</returns>
    internal static int CountTitles(string path)
    {
        using var reader = OpenText(path);

        if (IsXml(path))
        {
            var text = reader.ReadToEnd();
            var count = 0;

            for (var index = text.IndexOf("<title", StringComparison.Ordinal); index >= 0; index = text.IndexOf("<title", index + 6, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        var lines = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (TryParseDatLine(line, out _, out _, out _, out _))
            {
                lines++;
            }
        }

        return lines;
    }

    private static int CountAnimeInXml(string path)
    {
        var text = File.ReadAllText(path);
        var count = 0;

        for (var index = text.IndexOf("<anime ", StringComparison.Ordinal); index >= 0; index = text.IndexOf("<anime ", index + 7, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover partial file is replaced by the next attempt.
        }
    }
}
