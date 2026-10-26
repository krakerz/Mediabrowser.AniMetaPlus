// Derived from jellyfin-plugin-anidb (https://github.com/jellyfin/jellyfin-plugin-anidb),
// Copyright (C) the Jellyfin contributors, licensed GPL-2.0-only.
// Modified for AniMeta+ since 2026-10-14; the changes are in this repository's git history.

using System.Threading;
using System.Threading.Tasks;

namespace AniMetaPlus.Providers.AniDB.Identity;

/// <summary>
/// The <see cref="IAniDbTitleDownloader"/> interface defines a type which can download anime titles and their AniDB IDs.
/// </summary>
public interface IAniDbTitleDownloader
{
    /// <summary>
    /// Gets the path to the titles.xml file.
    /// </summary>
    string TitlesFilePath { get; }

    /// <summary>
    /// Downloads titles and stores them in an XML file at TitlesFilePath.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    Task Load(CancellationToken cancellationToken);
}
