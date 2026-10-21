using AniMetaPlus.Sources;
using Xunit;

namespace AniMetaPlus.Tests;

public class CloudflareGuardTests
{
    // Anime News Network's answer to the test proxy: 403, cf-mitigated: challenge, server: cloudflare, an HTML page.
    [Theory]
    [InlineData(403, "challenge", "cloudflare", "text/html", "a Cloudflare security check")]
    [InlineData(503, null, "cloudflare", "text/html", "a refusal from Cloudflare")]
    [InlineData(403, null, "cloudflare", "text/html", "a refusal from Cloudflare")]
    [InlineData(403, null, "cloudflare", "application/json", null)]
    [InlineData(429, null, "cloudflare", "text/html", null)]
    [InlineData(500, null, "nginx", "text/html", null)]
    [InlineData(404, null, null, null, null)]
    public void TellsACloudflareCheckFromAnyOtherFailure(int status, string? mitigated, string? server, string? mediaType, string? expected)
        => Assert.Equal(expected, CloudflareGuard.Describe(status, mitigated, server, mediaType));
}
