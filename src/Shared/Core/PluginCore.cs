using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AniMetaPlus.Configuration;
using AniMetaPlus.Data;
using AniMetaPlus.Providers.AniDB.Metadata;
using AniMetaPlus.Sources;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace AniMetaPlus.Core;

/// <summary>
/// What both servers run when the plugin starts.
/// </summary>
public static class PluginCore
{
    /// <summary>
    /// The settings files of the AniDB plugins this one replaces, newest first.
    /// </summary>
    private static readonly string[] _predecessorConfigurations =
    [
        "Jellyfin.Plugin.AniDB.xml",
        "MediaBrowser.Plugins.Anime.xml"
    ];

    /// <summary>
    /// The settings carried over from a predecessor: the ones both plugins have and mean the
    /// same by.
    /// </summary>
    private static readonly string[] _carriedSettings =
    [
        nameof(PluginConfiguration.TitlePreference),
        nameof(PluginConfiguration.IgnoreSeason),
        nameof(PluginConfiguration.TitleSimilarityThreshold),
        nameof(PluginConfiguration.MaxGenres),
        nameof(PluginConfiguration.TidyGenreList),
        nameof(PluginConfiguration.TitleCaseGenres),
        nameof(PluginConfiguration.AnimeDefaultGenre),
        nameof(PluginConfiguration.MaxCacheAge),
        nameof(PluginConfiguration.AniDbReplaceGraves),
        nameof(PluginConfiguration.ImportGenres),
        nameof(PluginConfiguration.ImportTags),
        nameof(PluginConfiguration.UseAniDbSeasonNames),
        nameof(PluginConfiguration.RequestIntervalMs),
        nameof(PluginConfiguration.MinimumTagWeight),
        nameof(PluginConfiguration.InfoboxTagsOnly),
        nameof(PluginConfiguration.TagBlacklist),
        nameof(PluginConfiguration.UseAniBridgeMappings),
        nameof(PluginConfiguration.ImportCommunityRating),
        nameof(PluginConfiguration.CastShowsCharacters),
        nameof(PluginConfiguration.EnableSimilarItems)
    ];

    /// <summary>
    /// Starts the shared parts. Called from each server's plugin constructor, after the ban
    /// state was restored.
    /// </summary>
    /// <param name="plugin">The plugin.</param>
    /// <param name="paths">The server paths.</param>
    /// <param name="logger">The logger.</param>
    public static void Initialize(Plugin plugin, IApplicationPaths paths, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        BlockStore.SetLogger(logger);
        BlockStore.Lifted -= OnLifted;
        BlockStore.Lifted += OnLifted;

        try
        {
            PluginPaths.MoveLegacyCache(paths, logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "AniMeta+ could not move its cached answers out of the server's cache folder; they are fetched again as needed");
        }

        try
        {
            var db = AniMetaDb.Get(paths, logger);

            var shutdownBlocks = BlockStore.RemoveShutdownBlocks();
            if (shutdownBlocks > 0)
            {
                logger.LogInformation("AniMeta+ removed {Count} blocks recorded only because the server was restarting", shutdownBlocks);
            }

            var authBlocks = BlockStore.RemoveStuckAuthBlocks();
            if (authBlocks > 0)
            {
                logger.LogInformation("AniMeta+ removed {Count} permanent blocks of sources that take no key", authBlocks);
            }

            if (db.GetState("migrated") == null)
            {
                MigrateFromPredecessor(plugin, paths, logger);
                db.SetState("migrated", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                db.Checkpoint();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "AniMeta+ could not open its database. Blocks, reviews and mappings made by hand are unavailable until it can");
        }
    }

    private static void OnLifted(BlockRecord block)
    {
        if (!string.Equals(block.Source, SourceIds.AniDb, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AniDbSeriesProvider.Lift(block.Scope == BlockScope.Source ? null : ItemKeys.AnimeId(block.ItemKey));
    }

    /// <summary>
    /// Takes over the settings and the ban of the AniDB plugin this one replaces, once. The ban
    /// matters most: a fresh plugin on a banned server would otherwise start asking at once.
    /// </summary>
    private static void MigrateFromPredecessor(Plugin plugin, IApplicationPaths paths, ILogger logger)
    {
        foreach (var name in _predecessorConfigurations)
        {
            var path = Path.Combine(paths.PluginConfigurationsPath, name);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var root = XDocument.Load(path).Root;
                if (root == null)
                {
                    continue;
                }

                var configuration = plugin.Configuration;
                var type = typeof(PluginConfiguration);
                var carried = 0;

                foreach (var setting in _carriedSettings)
                {
                    var value = root.Element(setting)?.Value;
                    var property = type.GetProperty(setting);

                    if (value == null || property == null || !property.CanWrite)
                    {
                        continue;
                    }

                    object? converted = property.PropertyType.IsEnum
                        ? (Enum.TryParse(property.PropertyType, value, true, out var parsed) ? parsed : null)
                        : Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture);

                    if (converted != null)
                    {
                        property.SetValue(configuration, converted);
                        carried++;
                    }
                }

                if (DateTime.TryParse(root.Element(nameof(PluginConfiguration.AniDbBannedUntilUtc))?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var bannedUntil)
                    && bannedUntil > DateTime.UtcNow)
                {
                    configuration.AniDbBannedUntilUtc = bannedUntil;

                    if (int.TryParse(root.Element(nameof(PluginConfiguration.AniDbBanBackoffMinutes))?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var backoff))
                    {
                        configuration.AniDbBanBackoffMinutes = backoff;
                    }

                    AniDbSeriesProvider.RestoreBanState(configuration);
                    BlockStore.Set(SourceIds.AniDb, BlockReason.Banned, "Carried over from " + name + ".", bannedUntil, configuration.AniDbBanBackoffMinutes);
                }

                plugin.SaveConfiguration();
                logger.LogInformation("AniMeta+ took over {Count} settings from {File}", carried, name);

                return;
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException or FormatException or InvalidCastException or OverflowException)
            {
                logger.LogWarning(ex, "The settings of the earlier plugin in {File} could not be read, so AniMeta+ starts from its defaults", name);
            }
        }
    }
}
