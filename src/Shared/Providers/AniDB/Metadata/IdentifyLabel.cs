using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using MediaBrowser.Common.Configuration;
#if EMBY
using System.Net;
#endif

namespace AniMetaPlus.Providers.AniDB.Metadata;

/// <summary>
/// What an identify result's poster says about it: how it matched, what it is of its show, and
/// the sites it has ids for.
/// </summary>
/// <param name="Poster">The poster's address, or empty for none.</param>
/// <param name="Match">How the result got into the list.</param>
/// <param name="Role">What the result is of its show.</param>
/// <param name="Sites">The sites it has ids for.</param>
internal sealed record PosterLabel(string Poster, string Match, string Role, string Sites);

/// <summary>
/// Draws the label band on identify posters. The label travels in a url, and the picture is drawn
/// when it is asked for: by the server's own address for it, which a browser loads as it is, or
/// through the provider by the url, which is how an older result carried it (a scheme of our own).
/// </summary>
internal static partial class IdentifyLabel
{
    private const string Scheme = "animetaplus-label:";

    /// <summary>
    /// The path of the picture on the server, which a browser asks without a login.
    /// </summary>
    internal const string PosterPath = "/AniMetaPlus/Identify/Poster";

    /// <summary>
    /// What Emby is told the picture is. Emby saves a picture it fetched under a name ending in
    /// the part of the type after the slash, and serves it by that ending: "image/svg+xml"
    /// would be saved as ".svg+xml", which it does not know and serves as a plain download.
    /// </summary>
    internal const string EmbyContentType = "image/svg";

    /// <summary>
    /// What the picture is, for a browser.
    /// </summary>
    internal const string SvgContentType = "image/svg+xml";

    /// <summary>
    /// The query flag on an address that the server's own image proxy fetches, which is answered
    /// as <see cref="EmbyContentType"/>.
    /// </summary>
    internal const string ProxyFlag = "t";

    private const string ProxyPath = "/Images/Remote";
    private const int MaxLine = 28;
    private const int MaxPosterBytes = 5 * 1024 * 1024;
    private const double LineWidth = 368;

    /// <summary>
    /// Makes the url a result carries as its image.
    /// </summary>
    /// <param name="label">What the poster says.</param>
    /// <returns>A url with only url-safe characters, such as "animetaplus-label:?p=...&amp;m=...&amp;r=...&amp;s=...".</returns>
    internal static string Url(PosterLabel label)
        => Scheme + "?" + Query(label);

    /// <summary>
    /// Makes the url a result carries as its image, in the form the running server can show.
    /// </summary>
    /// <param name="label">What the poster says.</param>
    /// <param name="paths">The server paths, which the signing key is kept beside.</param>
    /// <returns>A url the server's page can load.</returns>
    internal static string ResultUrl(PosterLabel label, IApplicationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

#if EMBY
        // Emby serves its api under "/emby". Older web clients load this url in the browser;
        // newer ones ask the server for it, which <see cref="ProxyUrl"/> is for.
        return PosterUrl(label, "/emby", IdentifySigner.Key(paths));
#else
        // Jellyfin's page puts the url straight into a card, so it has to be an address of the server's own.
        return PosterUrl(label, HostServices.BasePath?.Invoke(), IdentifySigner.Key(paths));
#endif
    }

    /// <summary>
    /// Makes the address the server itself can fetch a root-relative picture address by: Emby 4.10.2's
    /// web client has the server fetch a result's image, and the server cannot know which host the
    /// browser used, so it is addressed on its own loopback.
    /// </summary>
    /// <param name="resultUrl">A root-relative address, such as <see cref="ResultUrl"/> made.</param>
    /// <param name="port">The plain-http port the server listens on locally.</param>
    /// <returns>An absolute address that answers as <see cref="EmbyContentType"/>, or <c>null</c> where there is no port or the address is not root-relative.</returns>
    internal static string? ProxyUrl(string resultUrl, int? port)
    {
        if (port is not (> 0 and <= 65535) || !resultUrl.StartsWith('/') || resultUrl.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        // The flag is no part of what is signed.
        return "http://127.0.0.1:" + port.Value.ToString(CultureInfo.InvariantCulture) + resultUrl + "&" + ProxyFlag + "=1";
    }

    /// <summary>
    /// Reads the address Emby's image proxy wraps a result's picture address in
    /// ("…/Images/Remote?api_key=…&amp;imageUrl=…").
    /// </summary>
    /// <param name="url">The url.</param>
    /// <param name="inner">The wrapped address, decoded.</param>
    /// <returns><c>true</c> when the url is a wrapper that holds an address.</returns>
    internal static bool TryUnwrapProxy(string url, [NotNullWhen(true)] out string? inner)
    {
        inner = null;

        var fragment = url.IndexOf('#', StringComparison.Ordinal);
        var trimmed = fragment < 0 ? url : url[..fragment];
        var separator = trimmed.IndexOf('?', StringComparison.Ordinal);

        if (separator < 0 || !trimmed[..separator].TrimEnd('/').EndsWith(ProxyPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var part in trimmed[(separator + 1)..].Split('&'))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);

            if (equals > 0 && part[..equals].Equals("imageUrl", StringComparison.OrdinalIgnoreCase))
            {
                var value = Uri.UnescapeDataString(part[(equals + 1)..]);

                if (value.Length > 0)
                {
                    inner = value;

                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Makes the address of a label's picture on the server, signed so that only labels this
    /// server made can be asked for: the address is open to a browser that carries no login.
    /// </summary>
    /// <param name="label">What the poster says.</param>
    /// <param name="basePath">The server's configured base path, such as "/jellyfin", or empty for none.</param>
    /// <param name="key">The signing key.</param>
    /// <returns>A root-relative address with only characters that are safe in a url and in a css <c>url('...')</c>.</returns>
    internal static string PosterUrl(PosterLabel label, string? basePath, byte[] key)
    {
        var payload = Query(label);

        return NormalizeBasePath(basePath) + PosterPath + "?l=" + Encode(payload) + "&sig=" + IdentifySigner.Sign(payload, key);
    }

    /// <summary>
    /// Brings a base path to "" or "/x/y": a leading slash, none at the end.
    /// </summary>
    /// <param name="basePath">The configured base path, as typed.</param>
    /// <returns>The base path.</returns>
    internal static string NormalizeBasePath(string? basePath)
    {
        var segments = (basePath ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments.Length == 0 ? string.Empty : "/" + string.Join('/', Array.ConvertAll(segments, Encode));
    }

    /// <summary>
    /// Writes the label as a query string; also what a signature is made over.
    /// </summary>
    /// <param name="label">What the poster says.</param>
    /// <returns>The query string, without a leading question mark.</returns>
    internal static string Query(PosterLabel label)
    {
        ArgumentNullException.ThrowIfNull(label);

        return "p=" + Encode(label.Poster)
            + "&m=" + Encode(label.Match)
            + "&r=" + Encode(label.Role)
            + "&s=" + Encode(label.Sites)
            + "&v=" + Version;
    }

    // Emby caches a search image under the url's hash for good, typed by what it was first given:
    // a change to the picture or its content type needs a new url. Bump it with such changes.
    private const string Version = "4";

    /// <summary>
    /// Capitalises the first letter of each word and leaves the rest as it is, so names such as
    /// "AniList" or "TV" keep their own casing: "via AniList" becomes "Via AniList".
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The text in title case.</returns>
    internal static string TitleCase(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if ((i == 0 || !char.IsLetterOrDigit(chars[i - 1])) && char.IsLower(chars[i]))
            {
                chars[i] = char.ToUpperInvariant(chars[i]);
            }
        }

        return new string(chars);
    }

    // Percent-encoding that leaves nothing a css url('...') or an attribute would end on.
    private static string Encode(string text)
        => Uri.EscapeDataString(text).Replace("'", "%27", StringComparison.Ordinal).Replace("(", "%28", StringComparison.Ordinal).Replace(")", "%29", StringComparison.Ordinal);

    /// <summary>
    /// Whether a url is one <see cref="Url"/> made.
    /// </summary>
    /// <param name="url">The url.</param>
    /// <returns><c>true</c> for a label url.</returns>
    internal static bool Is(string url)
        => url.StartsWith(Scheme + "?", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a label url.
    /// </summary>
    /// <param name="url">The url.</param>
    /// <param name="label">The label.</param>
    /// <returns><c>true</c> when the url is a label url.</returns>
    internal static bool TryParse(string url, [NotNullWhen(true)] out PosterLabel? label)
    {
        if (!Is(url))
        {
            label = null;

            return false;
        }

        label = ParseQuery(url[(Scheme.Length + 1)..]);

        return true;
    }

    /// <summary>
    /// Reads a label from a query string <see cref="Query"/> wrote.
    /// </summary>
    /// <param name="query">The query string, without a leading question mark.</param>
    /// <returns>The label.</returns>
    internal static PosterLabel ParseQuery(string query)
    {
        string poster = string.Empty, match = string.Empty, role = string.Empty, sites = string.Empty;

        foreach (var part in query.Split('&'))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);

            if (separator < 1)
            {
                continue;
            }

            var value = Uri.UnescapeDataString(part[(separator + 1)..]);

            switch (part[..separator])
            {
                case "p":
                    poster = value;
                    break;
                case "m":
                    match = value;
                    break;
                case "r":
                    role = value;
                    break;
                case "s":
                    sites = value;
                    break;
            }
        }

        return new PosterLabel(poster, match, role, sites);
    }

    /// <summary>
    /// Cuts a line to what fits the band.
    /// </summary>
    /// <param name="text">The line.</param>
    /// <returns>The line, ending in an ellipsis where it was cut.</returns>
    internal static string Fit(string text)
        => text.Length > MaxLine ? text[..(MaxLine - 1)].TrimEnd() + "…" : text;

    /// <summary>
    /// Draws the poster with its label band.
    /// </summary>
    /// <param name="label">What the poster says.</param>
    /// <param name="posterDataUrl">The poster as a data url, or <c>null</c> for the "Local only" background.</param>
    /// <returns>The picture, as svg.</returns>
    internal static string Svg(PosterLabel label, string? posterDataUrl)
    {
        ArgumentNullException.ThrowIfNull(label);

        var svg = new StringBuilder();

        // The poster is embedded: a browser shows an svg used as a picture without loading anything it points to.
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"400\" height=\"600\" viewBox=\"0 0 400 600\">");
        svg.Append("<rect width=\"400\" height=\"600\" fill=\"#1c1f26\"/>");

        if (posterDataUrl is not null && DataUrlRegex().IsMatch(posterDataUrl))
        {
            svg.Append("<image x=\"0\" y=\"0\" width=\"400\" height=\"600\" preserveAspectRatio=\"xMidYMid slice\" xlink:href=\"").Append(posterDataUrl).Append("\"/>");
        }
        else
        {
            svg.Append("<text x=\"200\" y=\"290\" text-anchor=\"middle\" font-family=\"Arial, Helvetica, sans-serif\" font-size=\"44\" fill=\"#8a8f98\">Local only</text>");
        }

        svg.Append("<rect x=\"0\" y=\"404\" width=\"400\" height=\"196\" fill=\"#000000\" fill-opacity=\"0.78\"/>");

        var role = string.IsNullOrWhiteSpace(label.Role) ? "no season info" : label.Role;
        var sites = label.Sites.Length > MaxLine ? label.Sites.Replace(" · ", "·", StringComparison.Ordinal) : label.Sites;

        Line(svg, TitleCase(role), 462, 34, role.StartsWith("base show", StringComparison.OrdinalIgnoreCase) ? "#52b54b" : "#ffffff", true);
        Line(svg, TitleCase(label.Match), 520, 28, "#ffffff", false);
        Line(svg, sites, 573, 28, "#ffffff", false);

        return svg.Append("</svg>").ToString();
    }

    /// <summary>
    /// Answers a request for a label url's picture.
    /// </summary>
    /// <param name="url">The label url.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The server's image response, holding an svg.</returns>
    /// <exception cref="ArgumentException">The url is not a label url.</exception>
    internal static async Task<HostImageResponse> GetImage(string url, CancellationToken cancellationToken)
    {
        if (!TryParse(url, out var label))
        {
            throw new ArgumentException("Not a label address.", nameof(url));
        }

        var bytes = await Render(label, cancellationToken).ConfigureAwait(false);

#if EMBY
        return LocalOnlyImage.Response(bytes, EmbyContentType, url);
#else
        return LocalOnlyImage.Response(bytes, SvgContentType, url);
#endif
    }

    /// <summary>
    /// Answers a request for a label's picture by the address <see cref="PosterUrl"/> made.
    /// </summary>
    /// <param name="payload">The label, as the address carried it.</param>
    /// <param name="signature">The signature the address carried.</param>
    /// <param name="paths">The server paths, which the signing key is kept beside.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The picture as svg, or <c>null</c> when the signature is not one this server made.</returns>
    internal static async Task<byte[]?> GetSignedImage(string? payload, string? signature, IApplicationPaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (string.IsNullOrEmpty(payload) || string.IsNullOrEmpty(signature) || !IdentifySigner.Verify(payload, signature, IdentifySigner.Key(paths)))
        {
            return null;
        }

        return await Render(ParseQuery(payload), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a url of the server's picture address, as <see cref="PosterUrl"/> made it: with or without
    /// a base path, root-relative or absolute, and with whatever else a client appended to the query.
    /// </summary>
    /// <param name="url">The url.</param>
    /// <param name="payload">The label, as the address carried it.</param>
    /// <param name="signature">The signature the address carried.</param>
    /// <returns><c>true</c> when the url is a picture address.</returns>
    internal static bool TryParseAddress(string url, [NotNullWhen(true)] out string? payload, [NotNullWhen(true)] out string? signature)
    {
        payload = null;
        signature = null;

        var fragment = url.IndexOf('#', StringComparison.Ordinal);
        var trimmed = fragment < 0 ? url : url[..fragment];
        var separator = trimmed.IndexOf('?', StringComparison.Ordinal);

        if (separator < 0 || !trimmed[..separator].TrimEnd('/').EndsWith(PosterPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var part in trimmed[(separator + 1)..].Split('&'))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);

            if (equals < 1)
            {
                continue;
            }

            var value = Uri.UnescapeDataString(part[(equals + 1)..]);

            switch (part[..equals])
            {
                case "l":
                    payload = value;
                    break;
                case "sig":
                    signature = value;
                    break;
            }
        }

        return !string.IsNullOrEmpty(payload) && !string.IsNullOrEmpty(signature);
    }

    /// <summary>
    /// Answers a request, through the provider, for a url of the server's picture address.
    /// </summary>
    /// <param name="url">The picture address, as the server's own image proxy was given it.</param>
    /// <param name="paths">The server paths, which the signing key is kept beside.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The server's image response, holding the svg, or the "Local only" poster where the signature is not one this server made.</returns>
    internal static async Task<HostImageResponse> GetAddressImage(string url, IApplicationPaths paths, CancellationToken cancellationToken)
    {
        var svg = TryParseAddress(url, out var payload, out var signature)
            ? await GetSignedImage(payload, signature, paths, cancellationToken).ConfigureAwait(false)
            : null;

        if (svg is null)
        {
            return LocalOnlyImage.Response();
        }

#if EMBY
        return LocalOnlyImage.Response(svg, EmbyContentType, url);
#else
        return LocalOnlyImage.Response(svg, SvgContentType, url);
#endif
    }

    private static async Task<byte[]> Render(PosterLabel label, CancellationToken cancellationToken)
    {
        var poster = string.IsNullOrEmpty(label.Poster) ? null : await FetchPoster(label.Poster, cancellationToken).ConfigureAwait(false);

        return Encoding.UTF8.GetBytes(Svg(label, poster));
    }

    // The poster as a data url, or null where it is not a source's, cannot be fetched or is no picture.
    private static async Task<string?> FetchPoster(string poster, CancellationToken cancellationToken)
    {
        if (!IdentifySearch.IsPosterUrl(poster, out _))
        {
            return null;
        }

        try
        {
            using var response = await IdentifySearch.GetRemotePoster(poster, cancellationToken).ConfigureAwait(false);
            byte[] bytes;
            string? contentType;
#if EMBY
            if (response.StatusCode != HttpStatusCode.OK || response.Content is null)
            {
                return null;
            }

            using var copy = new MemoryStream();
            await response.Content.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            bytes = copy.ToArray();
            contentType = response.ContentType;
#else
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            contentType = response.Content.Headers.ContentType?.MediaType;
#endif
            if (bytes.Length is 0 or > MaxPosterBytes)
            {
                return null;
            }

            // A picture that is not a raster one is left out rather than embedded.
            var type = contentType is { } known && RasterTypeRegex().IsMatch(known) ? known.ToLowerInvariant() : "image/jpeg";

            return "data:" + type + ";base64," + Convert.ToBase64String(bytes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            // A poster that cannot be had leaves the label on the plain background.
            return null;
        }
    }

    private static void Line(StringBuilder svg, string text, int y, int size, string fill, bool bold)
    {
        var line = Fit(text);

        if (line.Length == 0)
        {
            return;
        }

        // Shrunk to the band where the line would run past it, which a long line of wide letters does.
        var fitted = Math.Min(size, Math.Max(20, (int)(LineWidth / (line.Length * (bold ? 0.58 : 0.54)))));

        svg.Append("<text x=\"16\" y=\"").Append(y).Append("\" font-family=\"Arial, Helvetica, sans-serif\" font-size=\"").Append(fitted).Append('"');

        if (bold)
        {
            svg.Append(" font-weight=\"700\"");
        }

        svg.Append(" fill=\"").Append(fill).Append("\">").Append(Escape(line)).Append("</text>");
    }

    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            switch (character)
            {
                case '&':
                    escaped.Append("&amp;");
                    break;
                case '<':
                    escaped.Append("&lt;");
                    break;
                case '>':
                    escaped.Append("&gt;");
                    break;
                case '"':
                    escaped.Append("&quot;");
                    break;
                case '\'':
                    escaped.Append("&apos;");
                    break;
                default:
                    // What xml cannot hold is dropped.
                    if (character >= ' ')
                    {
                        escaped.Append(character);
                    }

                    break;
            }
        }

        return escaped.ToString();
    }

    [GeneratedRegex(@"^data:image/[a-z0-9.+-]+;base64,[A-Za-z0-9+/=]*$")]
    private static partial Regex DataUrlRegex();

    [GeneratedRegex(@"^image/(jpeg|png|webp|gif|avif)$", RegexOptions.IgnoreCase)]
    private static partial Regex RasterTypeRegex();
}
