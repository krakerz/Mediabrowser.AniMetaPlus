// Derived from jellyfin-plugin-anidb (https://github.com/jellyfin/jellyfin-plugin-anidb),
// Copyright (C) the Jellyfin contributors, licensed GPL-2.0-only.
// Modified for AniMeta+ since 2026-10-14; the changes are in this repository's git history.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Configuration;
using AniMetaPlus.Providers.AniDB.Identity;
using AniMetaPlus.Providers.AniDB.Metadata;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus;

/// <summary>
/// Class Plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The name of the HTTP client <see cref="PluginServiceRegistrator"/> registers with the
    /// plugin's user agent, and that every AniDB request is sent with.
    /// </summary>
    public const string HttpClientName = "AniDB";

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="applicationHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="matcherLogger">Instance of the <see cref="ILogger{AniDbTitleMatcher}"/> interface.</param>
    /// <param name="downloaderLogger">Instance of the <see cref="ILogger{AniDbTitleDownloader}"/> interface.</param>
    /// <param name="seriesLogger">Instance of the <see cref="ILogger{AniDbSeriesProvider}"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IServerApplicationHost applicationHost,
        ILogger<AniDbTitleMatcher> matcherLogger,
        ILogger<AniDbTitleDownloader> downloaderLogger,
        ILogger<AniDbSeriesProvider> seriesLogger,
        IHttpClientFactory httpClientFactory)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        AppPaths = applicationPaths;
        AppHost = applicationHost;
        _httpClientFactory = httpClientFactory;

        // Ban state is global to the plugin, so its logger has to be too, and has to be set
        // before the ban is restored or the warning is lost.
        AniDbSeriesProvider.Logger = seriesLogger;
        AniDbSeriesProvider.RestoreBanState(Configuration);

        AniDbTitleMatcher.DefaultInstance = new AniDbTitleMatcher(
            matcherLogger,
            new AniDbTitleDownloader(downloaderLogger, applicationPaths));

        Core.PluginCore.Initialize(this, applicationPaths, seriesLogger);
    }

    /// <summary>
    /// Gets the instance.
    /// </summary>
    /// <value>The instance.</value>
    public static Plugin Instance { get; private set; } = null!;

    /// <summary>
    /// Gets the server paths, which the base class keeps to itself on Jellyfin.
    /// </summary>
    internal IApplicationPaths AppPaths { get; }

    /// <summary>
    /// Gets the server, which hands out the providers other plugins and the server registered.
    /// </summary>
    internal IServerApplicationHost AppHost { get; }

    /// <inheritdoc />
    public override string Name => "AniMeta+";

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
    /// Creates an <see cref="HttpClient"/> configured with the plugin user agent.
    /// </summary>
    /// <returns>The configured <see cref="HttpClient"/>.</returns>
    public HttpClient GetHttpClient()
        => _httpClientFactory.CreateClient(HttpClientName);

    /// <summary>
    /// Downloads an image for the server, sent with the plugin user agent.
    /// </summary>
    /// <param name="url">The image url.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The response.</returns>
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => GetHttpClient().GetAsync(url, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "AniMetaPlus",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.animetaplus.html",
            EnableInMainMenu = true,
            DisplayName = "AniMeta+"
        },
        new PluginPageInfo
        {
            Name = "AniMetaPlusJs",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.animetaplus.js"
        }
    ];
}
