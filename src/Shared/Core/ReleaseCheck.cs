using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Core;

/// <summary>
/// The newest published release on GitHub, so the settings page can say when an update is out.
/// </summary>
/// <param name="Version">The release's version, without a leading "v"; empty when unknown.</param>
/// <param name="Url">The release page.</param>
/// <param name="CheckedUtc">When GitHub last answered; <c>null</c> before the first answer.</param>
public sealed record LatestRelease(string Version, string Url, DateTime? CheckedUtc);

/// <summary>
/// Asks GitHub for the latest release at most every six hours, and only while the settings page is open.
/// </summary>
internal static class ReleaseCheck
{
    private const string LatestUrl = "https://api.github.com/repos/krakerz/Mediabrowser.AniMetaPlus/releases/latest";

    private static readonly TimeSpan _interval = TimeSpan.FromHours(6);

    private static readonly TimeSpan _retryAfterFailure = TimeSpan.FromMinutes(30);

    private static readonly SemaphoreSlim _lock = new(1, 1);

    private static LatestRelease _latest = new(string.Empty, string.Empty, null);

    private static DateTime _nextCheckUtc = DateTime.MinValue;

    /// <summary>
    /// Gets the latest release, asking GitHub first when the last answer is stale.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The latest release; an empty version when GitHub has not answered yet.</returns>
    public static async Task<LatestRelease> Get(ILogger logger, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow < _nextCheckUtc)
            {
                return _latest;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
                request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                using var response = await Plugin.Instance.GetHttpClient().SendAsync(request, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                var tag = json.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                var url = json.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                _latest = new LatestRelease(tag.TrimStart('v', 'V'), url, DateTime.UtcNow);
                _nextCheckUtc = DateTime.UtcNow + _interval;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug(ex, "Checking GitHub for a newer AniMeta+ release failed");
                _nextCheckUtc = DateTime.UtcNow + _retryAfterFailure;
            }

            return _latest;
        }
        finally
        {
            _lock.Release();
        }
    }
}
