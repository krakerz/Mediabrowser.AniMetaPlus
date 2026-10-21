using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using AniMetaPlus.Providers.AniDB.Metadata;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Sources;

/// <summary>
/// Notices when a source starts answering with Cloudflare's security check, as Anime News
/// Network does for some addresses. Without it the failure reads as any other refusal, and the
/// only fix - solving the check, as is done for ANN - would not be thought of.
/// </summary>
public static class CloudflareGuard
{
    // Sources already reported since the server started, so the log says it once.
    private static readonly ConcurrentDictionary<string, bool> _reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What a response says about Cloudflare: a security check, a refusal by Cloudflare that may
    /// be one, or nothing.
    /// </summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="mitigated">The <c>cf-mitigated</c> header, if any.</param>
    /// <param name="server">The <c>Server</c> header, if any.</param>
    /// <param name="mediaType">The response's media type, if any.</param>
    /// <returns>"a Cloudflare security check", "a refusal from Cloudflare", or <c>null</c>.</returns>
    public static string? Describe(int status, string? mitigated, string? server, string? mediaType)
    {
        if (!string.IsNullOrEmpty(mitigated) && mitigated.Contains("challenge", StringComparison.OrdinalIgnoreCase))
        {
            return "a Cloudflare security check";
        }

        // A Cloudflare page in place of the API's own answer: a block, or a check the header
        // above did not name.
        if (status is 403 or 503
            && server?.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) == true
            && mediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "a refusal from Cloudflare";
        }

        return null;
    }

    /// <summary>
    /// Checks a failed response for Cloudflare, logs it once per source, and gives the words to
    /// add to the failure, so the block on the Blocks tab says it too.
    /// </summary>
    /// <param name="source">The source asked.</param>
    /// <param name="response">The response.</param>
    /// <returns>Text to append to the failure message, empty when Cloudflare had no part in it.</returns>
    public static string Note(string source, HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var what = Describe(
            (int)response.StatusCode,
            response.Headers.TryGetValues("cf-mitigated", out var values) ? values.FirstOrDefault() : null,
            response.Headers.Server?.ToString(),
            response.Content?.Headers.ContentType?.MediaType);

        if (what == null)
        {
            return string.Empty;
        }

        if (_reported.TryAdd(source, true))
        {
            AniDbSeriesProvider.Logger?.LogWarning(
                "{Source} answered with {What} (HTTP {Status}). Requests to it fail from this address until that is dealt with, as AniMeta+ does for Anime News Network through FlareSolverr; worth reporting",
                source,
                what,
                (int)response.StatusCode);
        }

        return " (" + what + ")";
    }
}
