using AniMetaPlus.Core;
using AniMetaPlus.Host;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace AniMetaPlus;

/// <summary>
/// Hands the shared code the server's refresh queue once the server is up.
/// </summary>
public class EntryPoint : IServerEntryPoint
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="EntryPoint"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="fileSystem">The file system.</param>
    public EntryPoint(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public void Run()
    {
        var plugin = Plugin.Instance;

        AniMetaPlus.Providers.AniDB.Metadata.AniDbSeriesProvider.RestoreBanState(plugin.Configuration);
        PluginCore.Initialize(plugin, plugin.AppPaths, new AniMetaPlus.Logging.PluginLog<Plugin>(plugin.LogManager));
        var library = new HostLibrary(_libraryManager, _providerManager, _fileSystem);
        HostServices.Refresher = library;
        HostServices.Images = library;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        System.GC.SuppressFinalize(this);
    }
}
