using System;
using AniMetaPlus.Providers.AniDB.Metadata;
using Xunit;

namespace AniMetaPlus.Tests;

public class IdentifyPosterLinkTests
{
    private const string Forbidden = "'()\" \\<>";

    private static readonly byte[] _key = Convert.FromHexString("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");

    private static readonly PosterLabel _label = new("https://s4.anilist.co/file/a (1)'x.jpg?w=1&h=2", "title match 92%", "base show", "AniDB · AniList · MAL");

    [Fact]
    public void ASignatureVerifiesOnlyForItsPayloadAndKey()
    {
        var payload = IdentifyLabel.Query(_label);
        var signature = IdentifySigner.Sign(payload, _key);

        Assert.True(IdentifySigner.Verify(payload, signature, _key));
        Assert.False(IdentifySigner.Verify(payload + "x", signature, _key));
        Assert.False(IdentifySigner.Verify(payload, signature[..^1] + (signature[^1] == 'A' ? 'B' : 'A'), _key));
        Assert.False(IdentifySigner.Verify(payload, signature, new byte[32]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!!")]
    [InlineData("AAAA")]
    public void AMalformedSignatureFailsWithoutThrowing(string signature)
        => Assert.False(IdentifySigner.Verify(IdentifyLabel.Query(_label), signature, _key));

    [Fact]
    public void TheSignatureIsBase64UrlWithoutPadding()
    {
        var signature = IdentifySigner.Sign("p=a", _key);

        Assert.Equal(43, signature.Length);
        Assert.DoesNotContain('+', signature);
        Assert.DoesNotContain('/', signature);
        Assert.DoesNotContain('=', signature);
    }

    [Fact]
    public void TheQueryRoundTripsAndHoldsNoCharacterACssUrlOrAnAttributeEndsOn()
    {
        var query = IdentifyLabel.Query(_label);

        Assert.Equal(_label, IdentifyLabel.ParseQuery(query));

        foreach (var forbidden in Forbidden)
        {
            Assert.DoesNotContain(forbidden, query);
        }
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("jellyfin", "/jellyfin")]
    [InlineData("/jellyfin/", "/jellyfin")]
    [InlineData(" /a//b/ ", "/a/b")]
    [InlineData("/my media", "/my%20media")]
    public void ABasePathComesOutEmptyOrWithALeadingSlashAndNoTrailingOne(string? configured, string expected)
        => Assert.Equal(expected, IdentifyLabel.NormalizeBasePath(configured));

    [Theory]
    [InlineData(null, "/AniMetaPlus/Identify/Poster?l=")]
    [InlineData("/jellyfin/", "/jellyfin/AniMetaPlus/Identify/Poster?l=")]
    public void TheAddressIsRootRelativeAndCarriesTheLabelAndItsSignature(string? basePath, string start)
    {
        var url = IdentifyLabel.PosterUrl(_label, basePath, _key);

        Assert.StartsWith(start, url, StringComparison.Ordinal);

        var split = url.IndexOf("&sig=", StringComparison.Ordinal);
        var payload = Uri.UnescapeDataString(url[start.Length..split]);

        Assert.Equal(IdentifyLabel.Query(_label), payload);
        Assert.True(IdentifySigner.Verify(payload, url[(split + 5)..], _key));

        foreach (var forbidden in Forbidden)
        {
            Assert.DoesNotContain(forbidden, url);
        }
    }

    [Fact]
    public void OnEmbyTheAddressIsUnderItsEmbyPrefix()
    {
        var url = IdentifyLabel.PosterUrl(_label, "/emby", _key);

        Assert.StartsWith("/emby/AniMetaPlus/Identify/Poster?l=", url, StringComparison.Ordinal);
        Assert.Contains("&sig=", url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("/emby", "")]
    [InlineData("/emby", "&maxWidth=300")]
    [InlineData("/jellyfin", "&maxWidth=300&quality=90")]
    [InlineData("", "#top")]
    public void AnAddressReadsBackWhateverTheClientAppendedAndStillVerifies(string basePath, string appended)
    {
        var relative = IdentifyLabel.PosterUrl(_label, basePath, _key) + appended;

        foreach (var url in new[] { relative, "http://emby.lan:8096" + relative, "https://media.example.com" + relative })
        {
            Assert.True(IdentifyLabel.TryParseAddress(url, out var payload, out var signature), url);
            Assert.Equal(_label, IdentifyLabel.ParseQuery(payload));
            Assert.True(IdentifySigner.Verify(payload, signature, _key));
        }
    }

    [Fact]
    public void ATamperedAddressIsReadButDoesNotVerify()
    {
        var url = IdentifyLabel.PosterUrl(_label, "/emby", _key);
        var tampered = url[..^1] + (url[^1] == 'A' ? 'B' : 'A');

        Assert.True(IdentifyLabel.TryParseAddress(tampered, out var payload, out var signature));
        Assert.False(IdentifySigner.Verify(payload, signature, _key));

        var other = IdentifyLabel.PosterUrl(_label with { Match = "title match 100%" }, "/emby", _key);
        var swapped = url[..url.IndexOf("&sig=", StringComparison.Ordinal)] + other[other.IndexOf("&sig=", StringComparison.Ordinal)..];

        Assert.True(IdentifyLabel.TryParseAddress(swapped, out payload, out signature));
        Assert.False(IdentifySigner.Verify(payload, signature, _key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/emby/AniMetaPlus/Identify/Poster")]
    [InlineData("/emby/AniMetaPlus/Identify/Poster?l=p%3Da")]
    [InlineData("/emby/AniMetaPlus/Identify/Poster?sig=abc")]
    [InlineData("/emby/AniMetaPlus/Other?l=p%3Da&sig=abc")]
    [InlineData("https://s4.anilist.co/file/a.jpg?l=p%3Da&sig=abc")]
    [InlineData("animetaplus-label:?p=a&m=b&r=c&s=d&v=2")]
    public void AUrlThatIsNotAPosterAddressIsNotRead(string url)
        => Assert.False(IdentifyLabel.TryParseAddress(url, out _, out _));

    [Fact]
    public void EmbyIsToldTheSvgIsImageSvgSoItsCachedFileEndsInSvg()
    {
        Assert.Equal("image/svg", IdentifyLabel.EmbyContentType);
        Assert.Equal("image/svg+xml", IdentifyLabel.SvgContentType);
    }
}
