using System;
using System.Threading;
using MediaBrowser.Controller.Providers;

namespace AniMetaPlus.Core;

/// <summary>
/// Whether the refresh in progress was started by a person rather than by a scan or a task.
/// A person refreshing an item expects what the sources say now, so cached answers are kept for
/// a shorter time during that refresh; scans and tasks keep the configured cache age, which is
/// what keeps a whole library's refresh from turning into a flood of requests.
/// </summary>
internal static class RefreshScope
{
    /// <summary>
    /// How old a cached AniDB answer a refresh by hand still reuses. AniDB asks clients not to
    /// fetch the same anime more than once a day.
    /// </summary>
    public static readonly TimeSpan ManualAniDbMaxAge = TimeSpan.FromDays(1);

    /// <summary>
    /// How old a cached AniList or MyAnimeList answer a refresh by hand still reuses: long enough
    /// that the several lookups of one refresh share a single request.
    /// </summary>
    public static readonly TimeSpan ManualSourceMaxAge = TimeSpan.FromMinutes(15);

    private static readonly AsyncLocal<bool> _manual = new();

    /// <summary>
    /// Gets a value indicating whether the refresh in progress was started by a person.
    /// </summary>
    public static bool IsManual => _manual.Value;

    /// <summary>
    /// Gets how old a cached AniDB answer may be.
    /// </summary>
    public static TimeSpan AniDbMaxAge => IsManual ? ManualAniDbMaxAge : ConfiguredMaxAge;

    /// <summary>
    /// Gets how old a cached AniList or MyAnimeList answer may be.
    /// </summary>
    public static TimeSpan SourceMaxAge => IsManual ? ManualSourceMaxAge : ConfiguredMaxAge;

    /// <summary>
    /// Gets how old a cached answer scans and tasks still reuse.
    /// </summary>
    public static TimeSpan ConfiguredMaxAge => TimeSpan.FromDays(Plugin.Instance.Configuration.MaxCacheAge);

    /// <summary>
    /// Marks the rest of the current lookup as started by a person or not. Called first thing in
    /// a provider's lookup; the flag flows into everything that lookup awaits.
    /// </summary>
    /// <param name="info">The lookup info the server passed in.</param>
    public static void Enter(ItemLookupInfo info) => _manual.Value = info is { IsAutomated: false };
}
