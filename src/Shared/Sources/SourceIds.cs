using System;
using System.Collections.Generic;
using AniMetaPlus.Configuration;

namespace AniMetaPlus.Sources;

/// <summary>
/// The ids of the metadata sources. The delegated ones are the names the server's own
/// providers go by, which is how they are found.
/// </summary>
public static class SourceIds
{
    /// <summary>AniDB's HTTP API.</summary>
    public const string AniDb = "AniDB";

    /// <summary>AniList's GraphQL API.</summary>
    public const string AniList = "AniList";

    /// <summary>MyAnimeList, through its official API or through Jikan.</summary>
    public const string Mal = "MyAnimeList";

    /// <summary>The server's installed TheTVDB provider.</summary>
    public const string Tvdb = "TheTVDB";

    /// <summary>The server's installed TheMovieDb provider.</summary>
    public const string Tmdb = "TheMovieDb";

    /// <summary>
    /// Gets every source, in the order the settings page lists them.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [AniDb, AniList, Mal, Tvdb, Tmdb];

    /// <summary>
    /// Gets a value indicating whether a source is one of the server's own providers.
    /// </summary>
    /// <param name="id">The source id.</param>
    /// <returns><c>true</c> for a delegated source.</returns>
    public static bool IsDelegated(string id)
        => string.Equals(id, Tvdb, StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, Tmdb, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The kinds of data a source can be asked for, each with its own source order.
/// </summary>
public enum DataKind
{
    /// <summary>Titles.</summary>
    Titles,

    /// <summary>The synopsis.</summary>
    Overview,

    /// <summary>Genres and tags.</summary>
    Genres,

    /// <summary>The community rating.</summary>
    Rating,

    /// <summary>Cast and staff.</summary>
    People,

    /// <summary>Air dates and studios.</summary>
    Details,

    /// <summary>Episode titles, synopses and air dates.</summary>
    Episodes,

    /// <summary>The poster.</summary>
    PrimaryImage,

    /// <summary>Fanart.</summary>
    BackdropImage,

    /// <summary>The banner.</summary>
    BannerImage,

    /// <summary>The logo.</summary>
    LogoImage
}

/// <summary>
/// The defaults of every source and every source order.
/// </summary>
public static class SourceDefaults
{
    /// <summary>
    /// Gets the default settings of a source.
    /// </summary>
    /// <param name="id">The source id.</param>
    /// <returns>A new settings object.</returns>
    public static SourceSetting Setting(string id)
    {
        var setting = new SourceSetting { Id = id };

        if (string.Equals(id, SourceIds.AniDb, StringComparison.OrdinalIgnoreCase))
        {
            // AniDB's own data is the most accurate there is, so a stand-in for it is shown
            // to the owner before it is kept.
            setting.Mode = FallbackMode.SwitchReview;
            setting.RequestIntervalMs = PluginConfiguration.MinimumRequestIntervalMs;
        }
        else if (string.Equals(id, SourceIds.AniList, StringComparison.OrdinalIgnoreCase))
        {
            // AniList allows 90 a minute, and has run at a degraded 30 for long stretches.
            setting.RequestIntervalMs = 2100;
        }
        else if (string.Equals(id, SourceIds.Mal, StringComparison.OrdinalIgnoreCase))
        {
            // Jikan, the slower of the two ways in, allows 60 a minute and 3 a second.
            setting.RequestIntervalMs = 1100;
        }
        else
        {
            setting.RequestIntervalMs = 250;
        }

        return setting;
    }

    /// <summary>
    /// Gets the default source order of a kind of data.
    /// </summary>
    /// <param name="kind">The kind of data.</param>
    /// <returns>The source ids, first asked first.</returns>
    public static IReadOnlyList<string> Priority(DataKind kind) => kind switch
    {
        DataKind.Titles => [SourceIds.AniDb, SourceIds.AniList, SourceIds.Mal],
        DataKind.Overview => [SourceIds.AniDb, SourceIds.AniList, SourceIds.Mal, SourceIds.Tvdb],
        DataKind.Genres => [SourceIds.AniDb, SourceIds.AniList, SourceIds.Mal],
        DataKind.Rating => [SourceIds.AniDb, SourceIds.AniList, SourceIds.Mal],
        DataKind.People => [SourceIds.AniDb, SourceIds.AniList, SourceIds.Mal],
        DataKind.Details => [SourceIds.AniDb, SourceIds.AniList, SourceIds.Mal, SourceIds.Tvdb],
        DataKind.Episodes => [SourceIds.AniDb, SourceIds.Tvdb, SourceIds.Tmdb],
        DataKind.PrimaryImage => [SourceIds.AniDb, SourceIds.AniList, SourceIds.Tmdb, SourceIds.Tvdb],
        DataKind.BackdropImage => [SourceIds.Tmdb, SourceIds.Tvdb],
        DataKind.BannerImage => [SourceIds.Tvdb, SourceIds.AniList],
        DataKind.LogoImage => [SourceIds.Tmdb, SourceIds.Tvdb],
        _ => [SourceIds.AniDb]
    };

    /// <summary>
    /// Gets a value indicating whether the kinds of data are combined across sources rather
    /// than taken from the first that has them.
    /// </summary>
    /// <param name="kind">The kind of data.</param>
    /// <returns><c>true</c> when every source in the order contributes.</returns>
    public static bool IsCombined(DataKind kind)
        => kind is DataKind.Genres or DataKind.Rating or DataKind.People;
}
