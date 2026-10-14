using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace AniMetaPlus.Sources;

/// <summary>
/// The server's own TVDB and TMDB providers, called directly. The owner turns them off in the
/// anime library so that the server does not run them itself, and turning them off only hides
/// them from the server's list: they stay installed, and answer when called.
/// </summary>
/// <remarks>
/// They are never reached through the server's refresh, which would run this plugin again for
/// the same item. Instances come from the server's export list, created for this plugin alone.
/// </remarks>
public static class DelegatedProviders
{
    private static readonly object _lock = new();
    private static List<IMetadataProvider>? _metadata;
    private static List<IImageProvider>? _images;

    /// <summary>
    /// Gets a value indicating whether the server has a provider of that name.
    /// </summary>
    /// <param name="name">The provider name, such as <see cref="SourceIds.Tvdb"/>.</param>
    /// <returns><c>true</c> when it is installed.</returns>
    public static bool IsInstalled(string name)
        => Metadata().Any(p => Matches(p.Name, name)) || Images().Any(p => Matches(p.Name, name));

    /// <summary>
    /// Finds the server's metadata provider of a name for a kind of item.
    /// </summary>
    /// <typeparam name="TItem">The kind of item.</typeparam>
    /// <typeparam name="TInfo">The lookup info of that kind.</typeparam>
    /// <param name="name">The provider name.</param>
    /// <returns>The provider, or <c>null</c> when none is installed.</returns>
    public static IRemoteMetadataProvider<TItem, TInfo>? Find<TItem, TInfo>(string name)
        where TItem : BaseItem, IHasLookupInfo<TInfo>, new()
        where TInfo : ItemLookupInfo, new()
        => Metadata().OfType<IRemoteMetadataProvider<TItem, TInfo>>().FirstOrDefault(p => Matches(p.Name, name));

    /// <summary>
    /// Asks the server's metadata provider of a name about an item.
    /// </summary>
    /// <typeparam name="TItem">The kind of item.</typeparam>
    /// <typeparam name="TInfo">The lookup info of that kind.</typeparam>
    /// <param name="name">The provider name.</param>
    /// <param name="info">The lookup info, carrying the ids that provider is keyed by.</param>
    /// <param name="itemKey">The item key, for blocks and the request log.</param>
    /// <param name="itemName">The item's name.</param>
    /// <param name="kind">The kind of data.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result, or <c>null</c> when the provider is not installed or had nothing.</returns>
    public static async Task<MetadataResult<TItem>?> GetMetadata<TItem, TInfo>(
        string name,
        TInfo info,
        string itemKey,
        string itemName,
        string kind,
        CancellationToken cancellationToken)
        where TItem : BaseItem, IHasLookupInfo<TInfo>, new()
        where TInfo : ItemLookupInfo, new()
    {
        var provider = Find<TItem, TInfo>(name);
        if (provider == null)
        {
            return null;
        }

        var result = await SourceGate.Run(
            name,
            itemKey,
            itemName,
            kind,
            typeof(TItem).Name + " " + itemName,
            ct => Call(provider, info, ct),
            cancellationToken).ConfigureAwait(false);

        return result is { HasMetadata: true } ? result : null;
    }

    /// <summary>
    /// Asks the server's image providers of a name for an item's images.
    /// </summary>
    /// <param name="name">The provider name.</param>
    /// <param name="item">The item, carrying the ids those providers are keyed by.</param>
    /// <param name="itemKey">The item key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The images, empty when none is installed or had any.</returns>
    public static async Task<IReadOnlyList<RemoteImageInfo>> GetImages(string name, BaseItem item, string itemKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var images = new List<RemoteImageInfo>();

        foreach (var provider in Images().OfType<IRemoteImageProvider>().Where(p => Matches(p.Name, name) && p.Supports(item)))
        {
            var found = await SourceGate.Run(
                name,
                itemKey,
                item.Name,
                "Images",
                "images " + item.Name,
#if EMBY
                ct => provider is IRemoteImageProviderWithOptions withOptions && HostServices.FileSystem is { } fileSystem
                    ? withOptions.GetImages(
                        new RemoteImageFetchOptions
                        {
                            Item = item,
                            LibraryOptions = new MediaBrowser.Model.Configuration.LibraryOptions(),
                            DirectoryService = new DirectoryService(fileSystem),
                            MetadataLanguages = []
                        },
                        ct)
                    : provider.GetImages(item, new MediaBrowser.Model.Configuration.LibraryOptions(), ct),
#else
                ct => provider.GetImages(item, ct),
#endif
                cancellationToken).ConfigureAwait(false);

            images.AddRange(found);
        }

        return images;
    }

    /// <summary>
    /// Calls a provider the way the server itself does. Emby's own providers only answer through
    /// the overload that takes fetch options, and throw from the plain one.
    /// </summary>
    private static Task<MetadataResult<TItem>> Call<TItem, TInfo>(IRemoteMetadataProvider<TItem, TInfo> provider, TInfo info, CancellationToken cancellationToken)
        where TItem : BaseItem, IHasLookupInfo<TInfo>, new()
        where TInfo : ItemLookupInfo, new()
    {
#if EMBY
        if (provider is IRemoteMetadataProviderWithOptions<TItem, TInfo> withOptions && HostServices.FileSystem is { } fileSystem)
        {
            return withOptions.GetMetadata(
                new RemoteMetadataFetchOptions<TInfo> { SearchInfo = info, DirectoryService = new DirectoryService(fileSystem) },
                cancellationToken);
        }
#endif

        return provider.GetMetadata(info, cancellationToken);
    }

    private static bool Matches(string providerName, string name)
        => string.Equals(providerName, name, StringComparison.OrdinalIgnoreCase);

    private static List<IMetadataProvider> Metadata()
    {
        if (_metadata != null)
        {
            return _metadata;
        }

        lock (_lock)
        {
            return _metadata ??= Plugin.Instance.AppHost
                .GetExports<IMetadataProvider>(false)
                .Where(p => !IsOwn(p.GetType()))
                .ToList();
        }
    }

    private static List<IImageProvider> Images()
    {
        if (_images != null)
        {
            return _images;
        }

        lock (_lock)
        {
            return _images ??= Plugin.Instance.AppHost
                .GetExports<IImageProvider>(false)
                .Where(p => !IsOwn(p.GetType()))
                .ToList();
        }
    }

    private static bool IsOwn(Type type) => type.Assembly == typeof(DelegatedProviders).Assembly;
}
