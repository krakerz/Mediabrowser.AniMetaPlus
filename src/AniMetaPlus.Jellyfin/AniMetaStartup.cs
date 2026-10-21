using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using AniMetaPlus.Host;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;

namespace AniMetaPlus;

/// <summary>
/// Hands the shared code the server's refresh queue once the server is up.
/// </summary>
/// <param name="libraryManager">The library manager.</param>
/// <param name="providerManager">The provider manager.</param>
/// <param name="fileSystem">The file system.</param>
/// <param name="configurationManager">The server's configuration.</param>
public class AniMetaStartup(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, IServerConfigurationManager configurationManager) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var library = new HostLibrary(libraryManager, providerManager, fileSystem);
        HostServices.Refresher = library;
        HostServices.Images = library;
        HostServices.BasePath = () => configurationManager.GetNetworkConfiguration().BaseUrl;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
