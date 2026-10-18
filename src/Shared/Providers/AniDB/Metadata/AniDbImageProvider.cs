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
using AniMetaPlus.Configuration;
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

        return await AniDbSeriesProvider.GetAniDbImage(url, cancellationToken).ConfigureAwait(false);
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

        if (item is Season)
        {
            return await GetSeasonImages(item, cancellationToken).ConfigureAwait(false);
        }

        var seriesId = item.GetProviderId(ProviderNames.AniDb);
        var aniDb = (await GetImages(seriesId, cancellationToken).ConfigureAwait(false)).ToList();

        foreach (var image in aniDb)
        {
            image.Type = ImageType.Primary;
        }

        return await ImageMerge.GetImages(item, aniDb, AniDbSeriesProvider.Logger ?? (ILogger)NullLogger.Instance, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a season's images. The season's poster is its own AniDB entry's, and the other
    /// sources have nothing per season. With the season's details delegated they are the server's
    /// own TVDB and TMDB season images instead, rated by the poster order. In the overview order
    /// they are the season's entry's AniDB poster, AniList or MyAnimeList picture and the server's
    /// season images, all rated by the poster order. With the details left plain there are none.
    /// </summary>
    private async Task<IEnumerable<RemoteImageInfo>> GetSeasonImages(BaseItem item, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance.Configuration;

        switch (configuration.SeasonDetailsMode)
        {
            case SeasonDetailsMode.Plain:
                return [];

            case SeasonDetailsMode.Delegated:
                return await GetSeasonImagesInOrder(item, false, cancellationToken).ConfigureAwait(false);

            case SeasonDetailsMode.Order:
                return await GetSeasonImagesInOrder(item, true, cancellationToken).ConfigureAwait(false);

            default:
                var aniDb = (await GetImages(item.GetProviderId(ProviderNames.AniDb), cancellationToken).ConfigureAwait(false)).ToList();
                foreach (var image in aniDb)
                {
                    image.Type = ImageType.Primary;
                }

                return aniDb;
        }
    }

    /// <summary>
    /// Asks the sources in the poster order for the season's primary images, rated by place in
    /// the order so that the first source's wins.
    /// </summary>
    /// <param name="item">The season.</param>
    /// <param name="withEntry">Whether the season's own AniDB entry is asked too: AniDB's poster and the picture AniList or MyAnimeList has for it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private async Task<IEnumerable<RemoteImageInfo>> GetSeasonImagesInOrder(BaseItem item, bool withEntry, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance.Configuration;
        var images = new List<RemoteImageInfo>();
        var order = configuration.GetPriority(DataKind.PrimaryImage);
        var seriesId = (item as Season)?.Series?.GetProviderId(ProviderNames.AniDb);
        var seasonId = item.GetProviderId(ProviderNames.AniDb);
        var itemKey = (string.IsNullOrEmpty(seriesId) ? ItemKeys.ForUnidentified("Series", null, item.Name) : ItemKeys.ForAnime(seriesId))
            + FormattableString.Invariant($":s{item.IndexNumber}");

        for (var rank = 0; rank < order.Count; rank++)
        {
            var source = order[rank];
            if (!configuration.GetSource(source).Enabled)
            {
                continue;
            }

            IEnumerable<RemoteImageInfo> found;

            if (SourceIds.IsDelegated(source))
            {
                try
                {
                    found = (await DelegatedProviders.GetImages(source, item, itemKey, cancellationToken).ConfigureAwait(false)).Where(i => i.Type == ImageType.Primary);
                }
                catch (SourceBlockedException ex)
                {
                    AniDbSeriesProvider.Logger?.LogDebug("{Source} offered no images for {Item}: {Message}", source, item.Name, ex.Message);

                    continue;
                }
            }
            else if (!withEntry || string.IsNullOrEmpty(seasonId))
            {
                continue;
            }
            else if (string.Equals(source, SourceIds.AniDb, StringComparison.OrdinalIgnoreCase))
            {
                found = await GetImages(seasonId, cancellationToken).ConfigureAwait(false);
            }
            else if (!AniDbSeasonProvider.AsksEntryOf(source, item.IndexNumber))
            {
                continue;
            }
            else if (string.Equals(source, SourceIds.AniList, StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, SourceIds.Mal, StringComparison.OrdinalIgnoreCase))
            {
                var context = new FetchContext
                {
                    ItemKey = ItemKeys.ForAnime(seasonId),
                    ItemName = item.Name ?? string.Empty,
                    AnimeId = seasonId,
                    Year = item.ProductionYear
                };

                foreach (var (provider, id) in item.ProviderIds)
                {
                    context.Ids[provider] = id;
                }

                await AniDbSeasonProvider.AddLinkedIds(_appPaths, context, seasonId, cancellationToken).ConfigureAwait(false);

                var outcome = await SourceFetcher.Fetch(source, context, cancellationToken).ConfigureAwait(false);
                found = (outcome.Record?.Images ?? [])
                    .Where(i => string.Equals(i.Type, nameof(ImageType.Primary), StringComparison.OrdinalIgnoreCase))
                    .Select(i => new RemoteImageInfo
                    {
                        Url = i.Url,
                        Width = i.Width,
                        Height = i.Height,
                        Language = i.Language
                    });
            }
            else
            {
                continue;
            }

            foreach (var image in found)
            {
                image.Type = ImageType.Primary;
                image.ProviderName = ProviderNames.ForImage(source);
                image.CommunityRating = 10 - rank;
                image.VoteCount = 1000 - rank;
                images.Add(image);
            }
        }

        return images;
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
