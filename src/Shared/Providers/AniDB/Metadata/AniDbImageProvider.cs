using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AniMetaPlus.Core;
using AniMetaPlus.Sources;

namespace AniMetaPlus.Providers.AniDB.Metadata;

/// <summary>
/// The AniDB image provider for series, seasons and movies.
/// </summary>
/// <param name="appPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
public class AniDbImageProvider(IApplicationPaths appPaths) : IRemoteImageProvider
{
    private readonly IApplicationPaths _appPaths = appPaths;

    /// <inheritdoc />
    public string Name => ProviderNames.Display;

    /// <inheritdoc />
    public async Task<HostImageResponse> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        // Emby fetches a search result's picture through whichever provider shares the
        // result's provider name, which is this one rather than the series provider.
        if (LocalOnlyImage.Is(url))
        {
            return LocalOnlyImage.Response();
        }

        await AniDbSeriesProvider.WaitForImageSlot(cancellationToken).ConfigureAwait(false);
        return await Plugin.Instance.GetImageResponse(url, cancellationToken).ConfigureAwait(false);
    }

#if EMBY
    /// <inheritdoc />
    public Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, MediaBrowser.Model.Configuration.LibraryOptions libraryOptions, CancellationToken cancellationToken)
        => GetImages(item, cancellationToken);

#endif
    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var seriesId = item.GetProviderId(ProviderNames.AniDb);
        var aniDb = (await GetImages(seriesId, cancellationToken).ConfigureAwait(false)).ToList();

        foreach (var image in aniDb)
        {
            image.Type = ImageType.Primary;
        }

        // A season's image is its own entry's poster; the other sources have nothing per season.
        if (item is Season)
        {
            return aniDb;
        }

        return await ImageMerge.GetImages(item, aniDb, AniDbSeriesProvider.Logger ?? (ILogger)NullLogger.Instance, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the available images for the given AniDB id.
    /// </summary>
    /// <param name="aniDbId">The AniDB id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The available images.</returns>
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(string? aniDbId, CancellationToken cancellationToken)
    {
        var list = new List<RemoteImageInfo>();

        if (!string.IsNullOrEmpty(aniDbId))
        {
            string? imageUrl;

            try
            {
                // The picture name rarely changes, so a cached answer of any age will do when asked to.
                var seriesDataPath = Plugin.Instance.Configuration.AniDbImagesFromCache && AniDbSeriesProvider.HasCachedSeriesData(_appPaths, aniDbId)
                    ? AniDbSeriesProvider.CachedSeriesDataPath(_appPaths, aniDbId)
                    : await AniDbSeriesProvider.GetSeriesData(_appPaths, aniDbId, cancellationToken).ConfigureAwait(false);
                imageUrl = await FindImageUrl(seriesDataPath).ConfigureAwait(false);
            }
            catch (AniDbBannedException ex)
            {
                // AniDB not being asked is expected, and the other sources supply images.
                AniDbSeriesProvider.Logger?.LogDebug("No AniDB image for anime {AnimeId}: {Message}", aniDbId, ex.Message);

                return list;
            }
            catch (InvalidOperationException ex) when (!BlockStore.IsShutdown(ex))
            {
                // AniDB refused this one anime. Only its own image is missing; letting the refusal
                // out would fail the whole lookup and take every other source's images with it.
                AniDbSeriesProvider.Logger?.LogDebug("No AniDB image for anime {AnimeId}: {Message}", aniDbId, ex.Message);

                return list;
            }
            catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
            {
                // Nothing to offer rather than a failed refresh. The document is fetched again
                // on the next one, and until then the item keeps whatever image it has.
                AniDbSeriesProvider.Logger?.LogWarning(
                    ex,
                    "The cached document of AniDB anime {AnimeId} could not be read, so no image is offered for it",
                    aniDbId);

                return list;
            }

            if (!string.IsNullOrEmpty(imageUrl))
            {
                list.Add(new RemoteImageInfo
                {
                    ProviderName = ProviderNames.ForImage(SourceIds.AniDb),
                    Url = imageUrl
                });
            }
        }

        return list;
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        return item is Season ? [ImageType.Primary] : ImageMerge.Types;
    }

    /// <inheritdoc />
    public bool Supports(BaseItem item)
    {
        return item is Series || item is Season || item is Movie;
    }

    internal static async Task<string?> FindImageUrl(string seriesDataPath)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            CheckCharacters = false,
            IgnoreProcessingInstructions = true,
            IgnoreComments = true,
            ValidationType = ValidationType.None
        };

        using var streamReader = new StreamReader(seriesDataPath, Encoding.UTF8);
        using XmlReader reader = XmlReader.Create(streamReader, settings);
        await reader.MoveToContentAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "picture")
            {
                return "https://cdn.anidb.net/images/main/" + await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
            }
        }

        return null;
    }
}
