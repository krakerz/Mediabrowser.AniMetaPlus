// Derived from jellyfin-plugin-anidb (https://github.com/jellyfin/jellyfin-plugin-anidb),
// Copyright (C) the Jellyfin contributors, licensed GPL-2.0-only.
// Modified for AniMeta+ since 2026-10-14; the changes are in this repository's git history.

using System.Threading;
using System.Threading.Tasks;

namespace AniMetaPlus.Providers.AniDB.Identity;

/// <summary>
/// The <see cref="IAniDbTitleMatcher"/> interface defines a type which can match series titles to AniDB IDs.
/// </summary>
public interface IAniDbTitleMatcher
{
    /// <summary>
    /// Finds the AniDB for the series with the given title.
    /// </summary>
    /// <param name="title">The title of the series to search for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The AniDB ID of the series is found; else <c>null</c>.</returns>
    Task<string?> FindSeries(string title, CancellationToken cancellationToken);
}
