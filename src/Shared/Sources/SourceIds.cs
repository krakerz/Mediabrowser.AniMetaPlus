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
    /// How many AniDB requests go out in one burst when none is set. AniDB bans a client for
    /// volume well short of what the 2.5 s gap alone allows: a library refresh sent 256 requests
    /// in a few hours and was banned, and how long a window AniDB counts over is not known.
    /// </summary>
    public const int AniDbDefaultBurstSize = 20;

    /// <summary>
    /// How long, in minutes, AniDB is left alone after a burst when none is set.
    /// </summary>
    public const int AniDbDefaultRestMinutes = 120;

    /// <summary>
    /// AniDB's daily cap when none is set, a backstop just short of <see cref="AniDbDailyLimitGuess"/>.
    /// The default bursts alone fit 12 of 20 in any 24 hours, 240 requests.
    /// </summary>
    public const int AniDbDefaultPerDay = 250;

    /// <summary>
    /// The number of AniDB requests a day believed to earn a ban. A guess, not a published limit:
    /// a client was banned on 2026-10-17 at exactly 256 logins that day, and requests refused
    /// afterwards were not counted. Whether spreading requests out changes it is not known.
    /// </summary>
    public const int AniDbDailyLimitGuess = 256;

    /// <summary>
    /// Gets the limits a source's requests are held to: the ones set, or for AniDB its defaults
    /// where none is set, since AniDB is never safe to ask without them.
    /// </summary>
    /// <param name="setting">The source's settings.</param>
    /// <returns>The limits; a zero means none of that kind.</returns>
    public static RequestLimits Limits(SourceSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);

        if (!string.Equals(setting.Id, SourceIds.AniDb, StringComparison.OrdinalIgnoreCase))
        {
            return new RequestLimits(setting.MaxPerHour, setting.MaxPerDay, setting.BurstSize, TimeSpan.FromMinutes(setting.RestMinutes > 0 ? setting.RestMinutes : 60));
        }

        return new RequestLimits(
            setting.MaxPerHour,
            setting.MaxPerDay > 0 ? setting.MaxPerDay : AniDbDefaultPerDay,
            setting.BurstSize > 0 ? setting.BurstSize : AniDbDefaultBurstSize,
            TimeSpan.FromMinutes(setting.RestMinutes > 0 ? setting.RestMinutes : AniDbDefaultRestMinutes));
    }

    /// <summary>
    /// Gets the default settings of a source.
    /// </summary>
    /// <param name="id">The source id.</param>
    /// <returns>A new settings object.</returns>
    public static SourceSetting Setting(string id)
    {
        var setting = new SourceSetting { Id = id, RequestIntervalMs = MinimumRequestIntervalMs(id) };

        if (string.Equals(id, SourceIds.AniDb, StringComparison.OrdinalIgnoreCase))
        {
            // AniDB's own data is the most accurate there is, so a stand-in for it is shown
            // to the owner before it is kept.
            setting.Mode = FallbackMode.SwitchReview;
        }

        return setting;
    }

    /// <summary>
    /// Gets the shortest gap a source's requests may be set to, in milliseconds. Hard floors,
    /// so that no setting can send a source more than it tolerates.
    /// </summary>
    /// <param name="id">The source id.</param>
    /// <returns>The floor.</returns>
    public static int MinimumRequestIntervalMs(string? id)
    {
        if (string.Equals(id, SourceIds.AniDb, StringComparison.OrdinalIgnoreCase))
        {
            return PluginConfiguration.MinimumRequestIntervalMs;
        }

        if (string.Equals(id, SourceIds.AniList, StringComparison.OrdinalIgnoreCase))
        {
            // AniList allows 90 a minute, and has run at a degraded 30 for long stretches.
            return 2100;
        }

        if (string.Equals(id, SourceIds.Mal, StringComparison.OrdinalIgnoreCase))
        {
            // Jikan, the slower of the two ways in, allows 60 a minute and 3 a second.
            return 1100;
        }

        // The server's own TVDB and TMDB providers.
        return 250;
    }

    /// <summary>
    /// Gets the default source order of a kind of data.
    /// </summary>
    /// <param name="kind">The kind of data.</param>
    /// <returns>The source ids, first asked first.</returns>
    public static IReadOnlyList<string> Priority(DataKind kind) => kind switch
    {
        DataKind.Titles => [SourceIds.AniDb, SourceIds.Mal, SourceIds.AniList],
        DataKind.Overview => [SourceIds.AniDb, SourceIds.Mal, SourceIds.AniList, SourceIds.Tvdb],
        DataKind.Genres => [SourceIds.AniDb, SourceIds.Mal, SourceIds.AniList],
        DataKind.Rating => [SourceIds.AniDb, SourceIds.Mal, SourceIds.AniList],
        DataKind.People => [SourceIds.AniDb, SourceIds.Mal, SourceIds.AniList],
        DataKind.Details => [SourceIds.AniDb, SourceIds.Mal, SourceIds.AniList, SourceIds.Tvdb],
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
