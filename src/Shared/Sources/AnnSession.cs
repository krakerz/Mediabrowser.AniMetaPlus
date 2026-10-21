using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Sources;

/// <summary>
/// What FlareSolverr got past ANN's Cloudflare check: the cookies and the exact user agent they
/// were issued to. The cookie values are secrets and are never logged.
/// </summary>
public sealed class AnnClearance
{
    /// <summary>
    /// Gets or sets the user agent the cookies were issued to; Cloudflare checks it matches.
    /// </summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cookies for animenewsnetwork.com.
    /// </summary>
    public List<AnnCookie> Cookies { get; set; } = [];

    /// <summary>
    /// Gets or sets when FlareSolverr solved the check.
    /// </summary>
    public DateTime SolvedUtc { get; set; }

    /// <summary>
    /// Gets or sets when the cf_clearance cookie expires, or <c>null</c> when it did not say.
    /// </summary>
    public DateTime? ExpiresUtc { get; set; }
}

/// <summary>
/// One cookie of an <see cref="AnnClearance"/>.
/// </summary>
public sealed class AnnCookie
{
    /// <summary>
    /// Gets or sets the cookie's name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cookie's value.
    /// </summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// The FlareSolverr settings, read when a solve is wanted so that a change applies at once.
/// </summary>
/// <param name="Url">The FlareSolverr address, empty when none is set.</param>
/// <param name="TimeoutSeconds">The longest FlareSolverr may take, in seconds.</param>
internal readonly record struct AnnSolverSettings(string Url, int TimeoutSeconds);

/// <summary>
/// Asks ANN for an anime and gets past Cloudflare's security check where the owner has a
/// FlareSolverr: a request carries the stored clearance, a challenge sends FlareSolverr to
/// solve it once, and the request is repeated with what it got. Everything it needs from the
/// plugin is passed in, so that it runs against a stub in tests.
/// </summary>
internal sealed class AnnSession
{
    /// <summary>
    /// The state key the clearance is kept under.
    /// </summary>
    internal const string StateKey = "ann-clearance";

    /// <summary>
    /// How long FlareSolverr is left alone after a solve, so that an address Cloudflare keeps
    /// challenging costs one solve rather than one per show.
    /// </summary>
    internal static readonly TimeSpan SolveRest = TimeSpan.FromMinutes(10);

    private const string Endpoint = "https://cdn.animenewsnetwork.com/encyclopedia/api.xml?anime=";
    private const string Domain = "animenewsnetwork.com";

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    private readonly HttpClient _http;
    private readonly HttpClient _solver;
    private readonly Func<AnnSolverSettings> _settings;
    private readonly Func<string?> _read;
    private readonly Action<string> _write;
    private readonly Func<DateTime> _now;
    private readonly string? _userAgent;

    private AnnClearance? _clearance;
    private bool _loaded;
    private DateTime _lastSolveUtc = DateTime.MinValue;
    private bool _noSolverLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnnSession"/> class.
    /// </summary>
    /// <param name="http">The client for ANN, which must not keep cookies of its own.</param>
    /// <param name="solver">The client for FlareSolverr, which must not time out by itself.</param>
    /// <param name="settings">Gives the FlareSolverr settings.</param>
    /// <param name="read">Reads the kept clearance.</param>
    /// <param name="write">Keeps the clearance; an empty text forgets it.</param>
    /// <param name="now">Gives the time now.</param>
    /// <param name="userAgent">The user agent sent while there is no clearance.</param>
    internal AnnSession(HttpClient http, HttpClient solver, Func<AnnSolverSettings> settings, Func<string?> read, Action<string> write, Func<DateTime> now, string? userAgent)
    {
        _http = http;
        _solver = solver;
        _settings = settings;
        _read = read;
        _write = write;
        _now = now;
        _userAgent = userAgent;
    }

    /// <summary>
    /// Gets the clearance in use, for tests.
    /// </summary>
    internal AnnClearance? Clearance => _clearance;

    /// <summary>
    /// Whether an answer is Cloudflare's security check rather than an ANN error.
    /// </summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="cfMitigated">The <c>cf-mitigated</c> header, if any.</param>
    /// <param name="body">The body, if read.</param>
    /// <returns><c>true</c> when it is the check.</returns>
    internal static bool IsChallenge(int status, string? cfMitigated, string? body)
    {
        if (status != 403)
        {
            return false;
        }

        return string.Equals(cfMitigated?.Trim(), "challenge", StringComparison.OrdinalIgnoreCase)
            || (body != null
                && (body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase)
                    || body.Contains("Security check - Anime News Network", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// How long FlareSolverr is given, as the seconds to put in the request.
    /// </summary>
    /// <param name="seconds">The setting.</param>
    /// <returns>The setting kept between 10 and 600.</returns>
    internal static int ClampSeconds(int seconds) => Math.Clamp(seconds, 10, 600);

    /// <summary>
    /// How long to wait for FlareSolverr's answer: what it is given and 30 seconds more.
    /// </summary>
    /// <param name="seconds">The setting.</param>
    /// <returns>The wait.</returns>
    internal static TimeSpan SolverWait(int seconds) => TimeSpan.FromSeconds(ClampSeconds(seconds) + 30);

    /// <summary>
    /// The address FlareSolverr's API is asked at.
    /// </summary>
    /// <param name="address">The address the owner gave.</param>
    /// <returns>The <c>/v1</c> address, or <c>null</c> when it is not an http(s) address.</returns>
    internal static Uri? SolverUri(string? address)
    {
        var text = address?.Trim().TrimEnd('/');

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!text.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            text += "/v1";
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri : null;
    }

    /// <summary>
    /// The body asking FlareSolverr to fetch a page.
    /// </summary>
    /// <param name="url">The page.</param>
    /// <param name="timeoutSeconds">The setting; clamped.</param>
    /// <returns>The JSON.</returns>
    internal static string SolverRequestBody(string url, int timeoutSeconds)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["cmd"] = "request.get",
            ["url"] = url,
            ["maxTimeout"] = ClampSeconds(timeoutSeconds) * 1000
        }, _json);

    /// <summary>
    /// Reads FlareSolverr's answer. Only the cookies and the user agent are taken: the page it
    /// returns is Chrome's rendering of the XML, not the XML.
    /// </summary>
    /// <param name="json">The answer.</param>
    /// <param name="nowUtc">The time now.</param>
    /// <returns>The clearance, or what went wrong where there is none.</returns>
    internal static (AnnClearance? Clearance, string? Error) ParseSolverAnswer(string json, DateTime nowUtc)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, "its answer is not what it should be");
            }

            var status = Str(root, "status");
            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                var message = Str(root, "message");

                return (null, string.IsNullOrWhiteSpace(message) ? "it answered with status " + (status ?? "none") : message!);
            }

            if (!root.TryGetProperty("solution", out var solution) || solution.ValueKind != JsonValueKind.Object)
            {
                return (null, "its answer holds no solution");
            }

            var userAgent = Str(solution, "userAgent");
            if (string.IsNullOrWhiteSpace(userAgent))
            {
                return (null, "its answer holds no user agent");
            }

            var clearance = new AnnClearance { UserAgent = userAgent!, SolvedUtc = nowUtc };
            var hasClearance = false;

            if (solution.TryGetProperty("cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Array)
            {
                foreach (var cookie in cookies.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object))
                {
                    var name = Str(cookie, "name");
                    var value = Str(cookie, "value");
                    var domain = Str(cookie, "domain")?.Trim().TrimStart('.');

                    if (string.IsNullOrEmpty(name) || value == null || !IsHeaderSafe(name) || !IsHeaderSafe(value))
                    {
                        continue;
                    }

                    // Cookies of other sites FlareSolverr met on the way are not ours to send.
                    if (!string.IsNullOrEmpty(domain)
                        && !string.Equals(domain, Domain, StringComparison.OrdinalIgnoreCase)
                        && !domain.EndsWith("." + Domain, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    clearance.Cookies.Add(new AnnCookie { Name = name, Value = value });

                    if (name == "cf_clearance")
                    {
                        hasClearance = true;
                        clearance.ExpiresUtc = ExpiryOf(cookie);
                    }
                }
            }

            return hasClearance ? (clearance, null) : (null, "its answer holds no cf_clearance cookie");
        }
        catch (JsonException)
        {
            return (null, "its answer is not JSON");
        }
    }

    /// <summary>
    /// Writes a clearance as JSON, for keeping.
    /// </summary>
    /// <param name="clearance">The clearance.</param>
    /// <returns>The JSON.</returns>
    internal static string Serialize(AnnClearance clearance) => JsonSerializer.Serialize(clearance, _json);

    /// <summary>
    /// Reads a kept clearance.
    /// </summary>
    /// <param name="json">The kept text.</param>
    /// <returns>The clearance, or <c>null</c> when there is none or it cannot be read.</returns>
    internal static AnnClearance? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var clearance = JsonSerializer.Deserialize<AnnClearance>(json, _json);

            return clearance == null || string.IsNullOrWhiteSpace(clearance.UserAgent) || clearance.Cookies == null || clearance.Cookies.Count == 0 ? null : clearance;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a clearance has run out.
    /// </summary>
    /// <param name="clearance">The clearance.</param>
    /// <param name="nowUtc">The time now.</param>
    /// <returns><c>true</c> when its cf_clearance cookie has expired.</returns>
    internal static bool IsExpired(AnnClearance clearance, DateTime nowUtc)
        => clearance.ExpiresUtc is { } expires && expires <= nowUtc;

    /// <summary>
    /// The <c>Cookie</c> header for a clearance.
    /// </summary>
    /// <param name="clearance">The clearance.</param>
    /// <returns>The header value, <c>name=value; name=value</c>.</returns>
    internal static string CookieHeader(AnnClearance clearance)
        => string.Join("; ", clearance.Cookies.Select(c => c.Name + "=" + c.Value));

    /// <summary>
    /// Gets an ANN page's XML.
    /// </summary>
    /// <param name="annId">The ANN id, digits only.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The XML.</returns>
    /// <exception cref="HttpRequestException">ANN did not answer with the XML.</exception>
    internal async Task<string> Fetch(string annId, ILogger? logger, CancellationToken cancellationToken)
    {
        LoadClearance();

        var reply = await Send(annId, cancellationToken).ConfigureAwait(false);

        if (!reply.Challenged)
        {
            return Accept(reply);
        }

        // A clearance that is challenged anyway was revoked or is for another address.
        Forget();

        var settings = _settings();

        if (string.IsNullOrWhiteSpace(settings.Url))
        {
            if (!_noSolverLogged)
            {
                _noSolverLogged = true;
                logger?.LogInformation("Anime News Network is behind a Cloudflare security check from this address. Set a FlareSolverr address in General to get past it.");
            }

            throw new HttpRequestException("Anime News Network answered with a Cloudflare security check");
        }

        if (_now() - _lastSolveUtc < SolveRest)
        {
            throw new HttpRequestException("Anime News Network answered with a Cloudflare security check and FlareSolverr was asked a short while ago");
        }

        _lastSolveUtc = _now();

        var clearance = await Solve(annId, settings, logger, cancellationToken).ConfigureAwait(false);
        Keep(clearance);

        logger?.LogInformation("FlareSolverr solved Anime News Network's security check.");

        reply = await Send(annId, cancellationToken).ConfigureAwait(false);

        if (reply.Challenged)
        {
            Forget();
            logger?.LogWarning("Anime News Network still sends its security check with the cookie FlareSolverr got. FlareSolverr has to reach the internet from the same public IP as this server.");

            throw new HttpRequestException("Anime News Network answered with a Cloudflare security check after FlareSolverr solved it");
        }

        return Accept(reply);
    }

    private static string Accept(AnnReply reply)
    {
        if (reply.Status < 200 || reply.Status > 299)
        {
            throw new HttpRequestException("Anime News Network answered " + reply.Status.ToString(CultureInfo.InvariantCulture));
        }

        return reply.Body;
    }

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTime? ExpiryOf(JsonElement cookie)
    {
        // A session cookie has an expiry of -1.
        if (cookie.TryGetProperty("expiry", out var expiry) && expiry.ValueKind == JsonValueKind.Number && expiry.TryGetDouble(out var seconds) && seconds > 0 && seconds < 253402300799)
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)seconds).UtcDateTime;
        }

        return null;
    }

    // A cookie holding a line break or a semicolon would end up as more headers or more cookies.
    private static bool IsHeaderSafe(string text) => text.All(c => c >= ' ' && c != 127 && c != ';');

    private void LoadClearance()
    {
        if (_loaded)
        {
            if (_clearance != null && IsExpired(_clearance, _now()))
            {
                Forget();
            }

            return;
        }

        _loaded = true;
        _clearance = Deserialize(_read());

        if (_clearance != null && IsExpired(_clearance, _now()))
        {
            Forget();
        }
    }

    private void Keep(AnnClearance clearance)
    {
        _clearance = clearance;
        _loaded = true;
        _write(Serialize(clearance));
    }

    private void Forget()
    {
        if (_clearance == null)
        {
            return;
        }

        _clearance = null;
        _write(string.Empty);
    }

    private async Task<AnnReply> Send(string annId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + annId);
        request.Headers.Accept.ParseAdd("application/xml");

        var clearance = _clearance;

        if (clearance != null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", clearance.UserAgent);
            request.Headers.TryAddWithoutValidation("Cookie", CookieHeader(clearance));
        }
        else if (!string.IsNullOrEmpty(_userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var mitigated = response.Headers.TryGetValues("cf-mitigated", out var values) ? values.FirstOrDefault() : null;

        return new AnnReply(status, body, IsChallenge(status, mitigated, body));
    }

    private async Task<AnnClearance> Solve(string annId, AnnSolverSettings settings, ILogger? logger, CancellationToken cancellationToken)
    {
        var uri = SolverUri(settings.Url);

        if (uri == null)
        {
            logger?.LogWarning("The FlareSolverr address is not an http or https address, so Anime News Network's security check is not solved.");

            throw new HttpRequestException("The FlareSolverr address is not an http or https address");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SolverWait(settings.TimeoutSeconds));

        string body;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(SolverRequestBody(Endpoint + annId, settings.TimeoutSeconds), Encoding.UTF8, "application/json")
            };

            using var response = await _solver.SendAsync(request, timeout.Token).ConfigureAwait(false);

            // FlareSolverr answers a failed solve with an error status and a JSON message, so the status is not checked here.
            body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning("FlareSolverr did not answer within {Seconds} seconds, so Anime News Network's security check is not solved.", SolverWait(settings.TimeoutSeconds).TotalSeconds);

            throw new HttpRequestException("FlareSolverr did not answer in time");
        }
        catch (HttpRequestException ex)
        {
            logger?.LogWarning("FlareSolverr could not be reached: {Message}", ex.Message);

            throw;
        }

        var (clearance, error) = ParseSolverAnswer(body, _now());

        if (clearance == null)
        {
            logger?.LogWarning("FlareSolverr could not solve Anime News Network's security check: {Message}", error);

            throw new HttpRequestException("FlareSolverr could not solve the security check");
        }

        return clearance;
    }

    private readonly record struct AnnReply(int Status, string Body, bool Challenged);
}
