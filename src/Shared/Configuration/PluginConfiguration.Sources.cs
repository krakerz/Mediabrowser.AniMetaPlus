using System;
using System.Collections.Generic;
using System.Linq;
using AniMetaPlus.Sources;

namespace AniMetaPlus.Configuration;

/// <summary>
/// What happens to a request whose source has been turned away as a whole.
/// </summary>
public enum FallbackMode
{
    /// <summary>
    /// The item keeps what it has and waits until the source answers again.
    /// </summary>
    Pause,

    /// <summary>
    /// The next source is asked, and its answer is replaced once the source recovers.
    /// </summary>
    SwitchUpgrade,

    /// <summary>
    /// The next source is asked, and replacing its answer later is left to a review.
    /// </summary>
    SwitchReview
}

/// <summary>
/// The settings of one metadata source.
/// </summary>
public class SourceSetting
{
    /// <summary>
    /// Gets or sets the source id, one of <see cref="SourceIds"/>.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the source is asked at all.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets what happens while the source is turned away as a whole.
    /// </summary>
    public FallbackMode Mode { get; set; } = FallbackMode.SwitchUpgrade;

    private int _requestIntervalMs;

    /// <summary>
    /// Gets or sets the shortest gap between two requests to the source, in milliseconds. Never
    /// below the source's floor, <see cref="SourceDefaults.MinimumRequestIntervalMs"/>.
    /// </summary>
    public int RequestIntervalMs
    {
        get => Math.Max(_requestIntervalMs, SourceDefaults.MinimumRequestIntervalMs(Id));
        set => _requestIntervalMs = value;
    }

    /// <summary>
    /// Gets the floor of <see cref="RequestIntervalMs"/>, for the settings page. Read-only, so
    /// it is not written into the configuration file.
    /// </summary>
    public int MinimumRequestIntervalMs => SourceDefaults.MinimumRequestIntervalMs(Id);

    /// <summary>
    /// Gets or sets how many requests the source may be sent per hour. Zero means no cap.
    /// </summary>
    public int MaxPerHour { get; set; }

    /// <summary>
    /// Gets or sets how many requests the source may be sent per day. Zero means no cap.
    /// </summary>
    public int MaxPerDay { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the source's score counts towards the rating.
    /// </summary>
    public bool IncludeInRating { get; set; } = true;

    /// <summary>
    /// Gets or sets the title preference this source uses, or empty to follow the global one.
    /// </summary>
    public string TitlePreference { get; set; } = string.Empty;
}

/// <summary>
/// The order sources are asked in for one kind of data.
/// </summary>
public class KindPriority
{
    /// <summary>
    /// Gets or sets the kind of data, a <see cref="DataKind"/> name.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source ids in the order they are asked, comma separated.
    /// </summary>
    public string Sources { get; set; } = string.Empty;
}

/// <content>
/// The multi-source settings.
/// </content>
public partial class PluginConfiguration
{
    /// <summary>
    /// The shortest interval the titles dump may be refreshed at, by hand or on schedule.
    /// AniDB bans a client that downloads it more than once a day; three days leaves a wide margin.
    /// </summary>
    public const int MinimumTitlesRefreshDays = 3;

    private int _titlesRefreshDays = 7;
    private string _aniDbClientName = string.Empty;

    /// <summary>
    /// Gets or sets the client name registered on AniDB. AniDB is not asked anything while this
    /// is empty: every program has to register its own, and a borrowed one gets every user of
    /// it banned together.
    /// </summary>
    public string AniDbClientName
    {
        get => _aniDbClientName;
        set => _aniDbClientName = (value ?? string.Empty).Trim();
    }

    /// <summary>
    /// Gets or sets the client version registered on AniDB alongside the name.
    /// </summary>
    public int AniDbClientVersion { get; set; } = 1;

    /// <summary>
    /// Gets or sets the client AniDB last rejected, as <c>name/version</c>. AniDB is not asked
    /// while the configured client matches it.
    /// </summary>
    public string AniDbRejectedClient { get; set; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether AniDB's API may be asked anything.
    /// </summary>
    public bool AniDbConfigured => !string.IsNullOrWhiteSpace(AniDbClientName);

    /// <summary>
    /// Gets or sets the MyAnimeList client id. The official API answers nothing without one.
    /// </summary>
    public string MalClientId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether MyAnimeList is read through Jikan, which needs no
    /// client id but is not run by MyAnimeList.
    /// </summary>
    public bool MalUseJikan { get; set; }

    /// <summary>
    /// Gets or sets the path of the local AniDB titles dump, a <c>.dat.gz</c> or <c>.xml.gz</c>
    /// file. Empty means the file in the plugin's data folder.
    /// </summary>
    public string TitlesDumpPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the scheduled task downloads a fresh titles dump.
    /// </summary>
    public bool TitlesAutoRefresh { get; set; } = true;

    /// <summary>
    /// Gets or sets how many days a titles dump is kept before the scheduled task replaces it.
    /// </summary>
    public int TitlesRefreshDays
    {
        get => Math.Max(_titlesRefreshDays, MinimumTitlesRefreshDays);
        set => _titlesRefreshDays = value;
    }

    /// <summary>
    /// Gets or sets the per-source settings. Sources missing from the list use their defaults.
    /// </summary>
    /// <remarks>
    /// Left empty by the constructor: the XML serializer adds what it reads to a list that
    /// already has entries, which would double them on every load.
    /// </remarks>
    public List<SourceSetting> Sources { get; set; } = [];

    /// <summary>
    /// Gets or sets the order sources are asked in, per kind of data. Kinds missing from the
    /// list use their defaults.
    /// </summary>
    public List<KindPriority> Priorities { get; set; } = [];

    /// <summary>
    /// Gets the settings of a source, falling back to its defaults.
    /// </summary>
    /// <param name="id">The source id.</param>
    /// <returns>The settings.</returns>
    public SourceSetting GetSource(string id)
        => Sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? SourceDefaults.Setting(id);

    /// <summary>
    /// Gets the order sources are asked in for a kind of data, falling back to its default.
    /// </summary>
    /// <param name="kind">The kind of data.</param>
    /// <returns>The source ids, first asked first.</returns>
    public IReadOnlyList<string> GetPriority(DataKind kind)
    {
        var configured = Priorities.FirstOrDefault(p => string.Equals(p.Kind, kind.ToString(), StringComparison.OrdinalIgnoreCase));

        if (configured is null || string.IsNullOrWhiteSpace(configured.Sources))
        {
            return SourceDefaults.Priority(kind);
        }

        return configured.Sources
            .Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
