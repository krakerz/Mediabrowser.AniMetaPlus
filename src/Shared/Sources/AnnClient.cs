using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using AniMetaPlus.Core;
using AniMetaPlus.Data;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Sources;

/// <summary>
/// Reads an anime's page from the Anime News Network encyclopedia. ANN is not a source the owner
/// orders: AniDB names the entry, and what ANN knows only adds to what the sources gave. It asks
/// for one request a second and for answers to be kept about a week, so every answer is cached
/// on disk and every request goes through one gate.
/// </summary>
public static partial class AnnClient
{
    /// <summary>
    /// How long an answer is reused. ANN asks for about a week.
    /// </summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// The gap kept between two requests: ANN allows one a second, with a margin.
    /// </summary>
    internal static readonly TimeSpan RequestGap = TimeSpan.FromMilliseconds(1100);

    /// <summary>
    /// How long no request is made after one failed, so that an ANN outage costs one request
    /// rather than one per show.
    /// </summary>
    internal static readonly TimeSpan FailureRest = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };
    private static readonly Lazy<AnnSession> _session = new(CreateSession);
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static DateTime _lastRequestUtc = DateTime.MinValue;
    private static DateTime _failedAtUtc = DateTime.MinValue;

    /// <summary>
    /// Gets what ANN knows about an anime, from the cache while the answer is under a week old.
    /// Never throws: a failed request gives the stale copy when there is one, and nothing
    /// otherwise.
    /// </summary>
    /// <param name="annId">The ANN id.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The record, or <c>null</c> when ANN is turned off, does not know the id or cannot be asked.</returns>
    public static async Task<AnnRecord?> Get(string? annId, ILogger? logger, CancellationToken cancellationToken)
    {
        if (!IsId(annId) || !Plugin.Instance.Configuration.UseAnnExtras)
        {
            return null;
        }

        var folder = CacheFolder();
        var cached = ReadCache(folder, annId!);

        if (cached != null && IsFresh(cached, DateTime.UtcNow))
        {
            return cached.Found ? cached : null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // The caller before this one may have fetched the same entry while this one waited.
            cached = ReadCache(folder, annId!) ?? cached;

            if (cached != null && IsFresh(cached, DateTime.UtcNow))
            {
                return cached.Found ? cached : null;
            }

            if (DateTime.UtcNow - _failedAtUtc < FailureRest)
            {
                return cached is { Found: true } ? cached : null;
            }

            var wait = WaitBeforeNext(_lastRequestUtc, DateTime.UtcNow);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                _lastRequestUtc = DateTime.UtcNow;

                var record = Parse(await Fetch(annId!, logger, cancellationToken).ConfigureAwait(false), annId!, DateTime.UtcNow);
                WriteCache(folder, record);

                return record.Found ? record : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _failedAtUtc = DateTime.UtcNow;
                logger?.LogDebug("Anime News Network could not be read for entry {AnnId}: {Message}", annId, ex.Message);

                return cached is { Found: true } ? cached : null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Whether text is an ANN id: digits only, which also keeps it safe to put in a url and a file name.
    /// </summary>
    /// <param name="annId">The text.</param>
    /// <returns><c>true</c> when it is an id.</returns>
    internal static bool IsId(string? annId)
        => !string.IsNullOrEmpty(annId) && annId.Length <= 9 && annId.All(char.IsAsciiDigit);

    /// <summary>
    /// How long to wait before the next request may go out, ANN allowing one a second.
    /// </summary>
    /// <param name="lastRequestUtc">When the last request went out.</param>
    /// <param name="nowUtc">The time now.</param>
    /// <returns>The wait, zero when the request may go at once.</returns>
    internal static TimeSpan WaitBeforeNext(DateTime lastRequestUtc, DateTime nowUtc)
    {
        var wait = lastRequestUtc + RequestGap - nowUtc;

        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    /// <summary>
    /// Whether a cached answer is young enough to reuse.
    /// </summary>
    /// <param name="record">The cached record.</param>
    /// <param name="nowUtc">The time now.</param>
    /// <returns><c>true</c> when it is under a week old.</returns>
    internal static bool IsFresh(AnnRecord record, DateTime nowUtc)
        => nowUtc - record.FetchedUtc < MaxAge && record.FetchedUtc <= nowUtc.AddMinutes(5);

    /// <summary>
    /// Reads an ANN answer.
    /// </summary>
    /// <param name="xml">The answer.</param>
    /// <param name="annId">The id asked for.</param>
    /// <param name="fetchedUtc">When it was fetched.</param>
    /// <returns>The record; not found, with nothing in it, where the answer holds no such anime.</returns>
    /// <exception cref="XmlException">The answer is not XML.</exception>
    internal static AnnRecord Parse(string xml, string annId, DateTime fetchedUtc)
    {
        var settings = new XmlReaderSettings
        {
            CheckCharacters = false,
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null
        };

        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var root = XDocument.Load(reader).Root;

        var record = new AnnRecord { Id = annId, FetchedUtc = fetchedUtc };

        // A request for an unknown id is answered with a warning element and no anime.
        var anime = root?.Elements("anime").FirstOrDefault(a => (string?)a.Attribute("id") == annId)
            ?? root?.Elements("anime").FirstOrDefault();

        if (anime == null)
        {
            return record;
        }

        record.Found = true;
        record.Title = Text(anime.Elements("info").FirstOrDefault(i => string.Equals((string?)i.Attribute("type"), "Main title", StringComparison.OrdinalIgnoreCase)));

        if (anime.Element("ratings") is { } ratings)
        {
            record.Rating = Number(ratings, "bayesian_score");
            record.Votes = int.TryParse((string?)ratings.Attribute("nb_votes"), NumberStyles.None, CultureInfo.InvariantCulture, out var votes) ? votes : null;
        }

        foreach (var episode in anime.Elements("episode"))
        {
            var english = episode.Elements("title").FirstOrDefault(t => string.Equals((string?)t.Attribute("lang"), "EN", StringComparison.OrdinalIgnoreCase) && Text(t) != null);

            record.Episodes.Add(new AnnEpisode
            {
                Number = ((string?)episode.Attribute("num"))?.Trim() ?? string.Empty,
                EnglishTitle = Text(english)
            });
        }

        foreach (var staff in anime.Elements("staff"))
        {
            var task = Text(staff.Element("task"));

            foreach (var person in staff.Elements("person"))
            {
                if (task != null && Text(person) is { } name)
                {
                    record.Staff.Add(new AnnStaff { Task = task, Name = name });
                }
            }
        }

        foreach (var cast in anime.Elements("cast"))
        {
            var role = Text(cast.Element("role"));

            foreach (var person in cast.Elements("person"))
            {
                if (Text(person) is { } name)
                {
                    record.Cast.Add(new AnnCast { Language = ((string?)cast.Attribute("lang"))?.Trim() ?? string.Empty, Role = role ?? string.Empty, Name = name });
                }
            }
        }

        foreach (var credit in anime.Elements("credit"))
        {
            var task = Text(credit.Element("task"));

            foreach (var company in credit.Elements("company"))
            {
                if (task != null && Text(company) is { } name)
                {
                    record.Companies.Add(new AnnCompany { Task = task, Name = name });
                }
            }
        }

        return record;
    }

    /// <summary>
    /// Reads a cached answer, of any age.
    /// </summary>
    /// <param name="folder">The cache folder.</param>
    /// <param name="annId">The ANN id.</param>
    /// <returns>The record, or <c>null</c> when none is cached or the file cannot be read.</returns>
    internal static AnnRecord? ReadCache(string folder, string annId)
    {
        var path = PathOf(folder, annId);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AnnRecord>(File.ReadAllText(path), _json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Caches an answer, as <c>ann/&lt;id&gt;.json</c> in the cache folder.
    /// </summary>
    /// <param name="folder">The cache folder.</param>
    /// <param name="record">The record.</param>
    internal static void WriteCache(string folder, AnnRecord record)
    {
        try
        {
            var path = PathOf(folder, record.Id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(path + ".partial", JsonSerializer.Serialize(record, _json), new UTF8Encoding(false));
            File.Move(path + ".partial", path, true);
        }
        catch (IOException)
        {
            // Not cached means asked again next time, which is all that is lost.
        }
    }

    private static string PathOf(string folder, string annId) => Path.Combine(folder, annId + ".json");

    private static string CacheFolder() => Path.Combine(PluginPaths.CacheFolder(Plugin.Instance.AppPaths), "ann");

    private static Task<string> Fetch(string annId, ILogger? logger, CancellationToken cancellationToken)
        => _session.Value.Fetch(annId, logger, cancellationToken);

    // ANN gets its own client rather than the plugin's: the clearance's cookies and user agent
    // are sent by hand, which a client that keeps cookies would mix with its own. The proxy is
    // the default one, so the environment's HTTP(S)_PROXY applies as it does elsewhere.
    private static AnnSession CreateSession()
    {
        var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // A solve takes up to a minute or more, so its wait is set per request, from the setting.
        var solver = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };

        var userAgent = Plugin.Instance.GetHttpClient().DefaultRequestHeaders.UserAgent.ToString();

        return new AnnSession(
            http,
            solver,
            () =>
            {
                var configuration = Plugin.Instance.Configuration;

                return new AnnSolverSettings(configuration.FlareSolverrUrl ?? string.Empty, configuration.FlareSolverrTimeoutSeconds);
            },
            () =>
            {
                try
                {
                    return AniMetaDb.Current.GetState(AnnSession.StateKey);
                }
                catch (Exception ex) when (ex is LiteDB.LiteException or IOException)
                {
                    return null;
                }
            },
            value =>
            {
                try
                {
                    AniMetaDb.Current.SetState(AnnSession.StateKey, value);
                }
                catch (Exception ex) when (ex is LiteDB.LiteException or IOException)
                {
                    // Not kept means FlareSolverr is asked again after a restart, which is all that is lost.
                }
            },
            () => DateTime.UtcNow,
            userAgent);
    }

    private static double? Number(XElement element, string attribute)
        => double.TryParse((string?)element.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// The text of an element, with the HTML entities ANN leaves in it decoded and white space
    /// tidied.
    /// </summary>
    private static string? Text(XElement? element)
    {
        if (element == null)
        {
            return null;
        }

        // Some of ANN's text is escaped twice, so what the XML reader decoded may still hold entities.
        var text = WhiteSpaceRegex().Replace(WebUtility.HtmlDecode(element.Value), " ").Trim();

        return text.Length == 0 ? null : text;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpaceRegex();
}
