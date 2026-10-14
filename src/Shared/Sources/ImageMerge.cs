using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AniMetaPlus.Core;
using AniMetaPlus.Providers;
using AniMetaPlus.Providers.AniDB.Metadata;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Sources;

/// <summary>
/// Offers an item's images from every source, each kind of image in the order the owner set for
/// it. The server takes the best rated image of a kind, so each source's images are rated by
/// its place in that order: the first source in the order is the one whose image is used.
/// </summary>
public static class ImageMerge
{
    private static readonly (ImageType Type, DataKind Kind)[] _kinds =
    [
        (ImageType.Primary, DataKind.PrimaryImage),
        (ImageType.Backdrop, DataKind.BackdropImage),
        (ImageType.Banner, DataKind.BannerImage),
        (ImageType.Logo, DataKind.LogoImage)
    ];

    /// <summary>
    /// Gets the kinds of image the merge can offer.
    /// </summary>
    public static IReadOnlyList<ImageType> Types { get; } = _kinds.Select(k => k.Type).ToList();

    /// <summary>
    /// Collects an item's images.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="aniDbImages">The images AniDB has for it.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The images, rated by source order.</returns>
    public static async Task<IReadOnlyList<RemoteImageInfo>> GetImages(
        BaseItem item,
        IEnumerable<RemoteImageInfo> aniDbImages,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var configuration = Plugin.Instance.Configuration;
        var animeId = item.GetProviderId(ProviderNames.AniDb);

        // An item AniDB has no entry for may still be tied to AniList or MyAnimeList by id.
        var link = string.IsNullOrEmpty(animeId) ? AniDbSeriesProvider.LinkFromIds(item.ProviderIds) : null;
        var context = new FetchContext
        {
            ItemKey = !string.IsNullOrEmpty(animeId) ? ItemKeys.ForAnime(animeId) : link ?? ItemKeys.ForUnidentified("Series", item.Path, item.Name),
            ItemName = item.Name ?? string.Empty,
            ItemPath = item.Path ?? string.Empty,
            AnimeId = animeId,
            Year = item.ProductionYear
        };

        foreach (var (provider, id) in item.ProviderIds)
        {
            context.Ids[provider] = id;
        }

        var byType = new Dictionary<ImageType, List<RemoteImageInfo>>();
        var delegated = new Dictionary<string, IReadOnlyList<RemoteImageInfo>>(StringComparer.OrdinalIgnoreCase);
        var records = new Dictionary<string, AnimeRecord?>(StringComparer.OrdinalIgnoreCase);
        var aniDb = aniDbImages.ToList();

        foreach (var (type, kind) in _kinds)
        {
            var order = configuration.GetPriority(kind);
            var images = new List<RemoteImageInfo>();

            for (var rank = 0; rank < order.Count; rank++)
            {
                var source = order[rank];
                if (!configuration.GetSource(source).Enabled)
                {
                    continue;
                }

                IEnumerable<RemoteImageInfo> found;

                if (string.Equals(source, SourceIds.AniDb, StringComparison.OrdinalIgnoreCase))
                {
                    found = aniDb.Where(i => i.Type == type);
                }
                else if (SourceIds.IsDelegated(source))
                {
                    if (!delegated.TryGetValue(source, out var list))
                    {
                        list = await Delegated(source, item, context.ItemKey, logger, cancellationToken).ConfigureAwait(false);
                        delegated[source] = list;
                    }

                    found = list.Where(i => i.Type == type);
                }
                else if (string.IsNullOrEmpty(animeId) && link is null)
                {
                    // An item nothing identified is not looked up by name: a name search that
                    // matches the wrong anime would put its poster on the item.
                    continue;
                }
                else
                {
                    if (!records.TryGetValue(source, out var record))
                    {
                        var outcome = await SourceFetcher.Fetch(source, context, cancellationToken).ConfigureAwait(false);
                        record = outcome.Record;
                        records[source] = record;
                    }

                    found = (record?.Images ?? [])
                        .Where(i => string.Equals(i.Type, type.ToString(), StringComparison.OrdinalIgnoreCase))
                        .Select(i => new RemoteImageInfo
                        {
                            ProviderName = ProviderNames.Display,
                            Url = i.Url,
                            Type = type,
                            Width = i.Width,
                            Height = i.Height,
                            Language = i.Language
                        });
                }

                // Rated by place in the order, so the server picks the first source's image and
                // offers the rest further down when an image is chosen by hand.
                foreach (var image in found)
                {
                    image.ProviderName = ProviderNames.ForImage(source);
                    image.CommunityRating = 10 - rank;
                    image.VoteCount = 1000 - rank;
                    images.Add(image);
                }
            }

            byType[type] = images;
        }

        return byType.Values.SelectMany(v => v).ToList();
    }

    private static async Task<IReadOnlyList<RemoteImageInfo>> Delegated(string source, BaseItem item, string itemKey, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            return await DelegatedProviders.GetImages(source, item, itemKey, cancellationToken).ConfigureAwait(false);
        }
        catch (SourceBlockedException ex)
        {
            logger.LogDebug("{Source} offered no images for {Item}: {Message}", source, item.Name, ex.Message);

            return [];
        }
    }
}
