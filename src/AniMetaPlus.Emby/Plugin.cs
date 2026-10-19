using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Configuration;
using AniMetaPlus.Logging;
using AniMetaPlus.Providers.AniDB.Identity;
using AniMetaPlus.Providers.AniDB.Metadata;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace AniMetaPlus;

/// <summary>
/// The Emby entry point of the plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
{
    // Emby has no client factory for plugins. One client for the process is what the factory
    // would hand out anyway, and creating one per request exhausts sockets.
    private static readonly Lazy<HttpClient> _httpClient = new(CreateHttpClient);

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="applicationHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="logManager">Instance of the <see cref="ILogManager"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IServerApplicationHost applicationHost, ILogManager logManager)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        AppPaths = applicationPaths;
        AppHost = applicationHost;
        LogManager = logManager;

        // Ban state is global to the plugin, so its logger has to be too. The ban itself is
        // restored by the entry point: Emby sets the plugin's file name, which its settings path
        // is built from, only after constructing it, so the settings cannot be read here.
        AniDbSeriesProvider.Logger = new PluginLog<AniDbSeriesProvider>(logManager);

        AniDbTitleMatcher.DefaultInstance = new AniDbTitleMatcher(
            new PluginLog<AniDbTitleMatcher>(logManager),
            new AniDbTitleDownloader(new PluginLog<AniDbTitleDownloader>(logManager), applicationPaths));
    }

    /// <summary>
    /// Gets the instance.
    /// </summary>
    public static Plugin Instance { get; private set; } = null!;

    /// <summary>
    /// Gets the server paths, which the base class keeps to itself on Jellyfin.
    /// </summary>
    internal IApplicationPaths AppPaths { get; }

    /// <summary>
    /// Gets the server, which hands out the providers other plugins and the server registered.
    /// </summary>
    internal IServerApplicationHost AppHost { get; }

    /// <summary>
    /// Gets the server's log manager, for code the container does not build.
    /// </summary>
    internal ILogManager LogManager { get; }

    /// <inheritdoc />
    public override string Name => "AniMeta+";

    /// <inheritdoc />
    public override string Description => "Anime metadata from AniDB, AniList and MyAnimeList, with the server's TVDB and TMDB providers as fallbacks.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("d9211a9c-1ad7-4203-bf8e-9078deb65469");

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);

        // Saved from the settings page; written to the log so a later problem can be matched against them.
        Core.PluginCore.LogSettings(Configuration, AniDbSeriesProvider.Logger, "now runs with");
    }

    /// <summary>
    /// Gets the <see cref="HttpClient"/> every request of the plugin is sent with.
    /// </summary>
    /// <returns>The shared client.</returns>
    public HttpClient GetHttpClient() => _httpClient.Value;

    /// <summary>
    /// Downloads an image for the server, sent with the plugin user agent.
    /// </summary>
    /// <param name="url">The image url.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The response, which the server reads and disposes.</returns>
    public async Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var response = await GetHttpClient().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        return new HttpResponseInfo([content, response])
        {
            Content = content,
            ContentType = response.Content.Headers.ContentType?.MediaType,
            ContentLength = response.Content.Headers.ContentLength,
            StatusCode = response.StatusCode,
            ResponseUrl = response.RequestMessage?.RequestUri?.ToString() ?? url,
            Headers = response.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <inheritdoc />
    public ImageFormat ThumbImageFormat => ImageFormat.Jpg;

    /// <inheritdoc />
    public Stream GetThumbImage() => GetType().Assembly.GetManifestResourceStream("AniMetaPlus.banner.jpg")!;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "AniMetaPlus",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.animetaplus.html",
            IsMainConfigPage = true,
            EnableInMainMenu = true,
            MenuSection = "server",
            DisplayName = "AniMeta+"
        },
        new PluginPageInfo
        {
            Name = "AniMetaPlusJs",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.animetaplus.js"
        }
    ];

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };

        var client = new HttpClient(handler);
        var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Emby-Server", "4"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("animetaplus", version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));

        return client;
    }
}
