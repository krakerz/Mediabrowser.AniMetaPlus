using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using AniMetaPlus.Host;
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
public class AniMetaStartup(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        HostServices.Refresher = new HostLibrary(libraryManager, providerManager, fileSystem);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
