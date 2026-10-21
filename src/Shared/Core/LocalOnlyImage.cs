using System;
using System.IO;
#if EMBY
using System.Net;
#else
using System.Net.Http;
using System.Net.Http.Headers;
#endif

namespace AniMetaPlus.Core;

/// <summary>
/// The "Local only" poster on identify results, which come from local data and carry no
/// picture of their own.
/// </summary>
internal static class LocalOnlyImage
{
    private static readonly Lazy<byte[]> _bytes = new(() =>
    {
        using var stream = typeof(LocalOnlyImage).Assembly.GetManifestResourceStream("AniMetaPlus.Images.local-only.png")
            ?? throw new InvalidOperationException("The local-only poster is not embedded.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    });

    private static readonly Lazy<string> _dataUrl = new(() => "data:image/png;base64," + Convert.ToBase64String(_bytes.Value));

    /// <summary>
    /// Gets the poster's url. A data url, because both servers' result lists put a search
    /// picture's url straight into the page; Emby's confirm step instead fetches it through
    /// the provider, by this url in a query string, so the picture is kept small.
    /// </summary>
    public static string Url => _dataUrl.Value;

    /// <summary>
    /// Whether a url is the poster's.
    /// </summary>
    /// <param name="url">The url.</param>
    /// <returns><c>true</c> for the poster.</returns>
    public static bool Is(string url) => string.Equals(url, Url, StringComparison.Ordinal);

    /// <summary>
    /// Answers a request for the poster.
    /// </summary>
    /// <returns>The poster.</returns>
    public static HostImageResponse Response() => Response(_bytes.Value, "image/png", Url);

    /// <summary>
    /// Wraps picture bytes in the server's image response.
    /// </summary>
    /// <param name="bytes">The picture.</param>
    /// <param name="contentType">Its media type.</param>
    /// <param name="url">The url it was asked for by.</param>
    /// <returns>The response.</returns>
    public static HostImageResponse Response(byte[] bytes, string contentType, string url)
    {
#if EMBY
        var content = new MemoryStream(bytes, false);
        return new HostImageResponse([content])
        {
            Content = content,
            ContentType = contentType,
            ContentLength = bytes.Length,
            StatusCode = HttpStatusCode.OK,
            ResponseUrl = url
        };
#else
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HostImageResponse(System.Net.HttpStatusCode.OK) { Content = content };
#endif
    }
}
