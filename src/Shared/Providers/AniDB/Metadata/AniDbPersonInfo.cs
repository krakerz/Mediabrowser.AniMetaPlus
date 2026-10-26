// Derived from jellyfin-plugin-anidb (https://github.com/jellyfin/jellyfin-plugin-anidb),
// Copyright (C) the Jellyfin contributors, licensed GPL-2.0-only.
// Modified for AniMeta+ since 2026-10-14; the changes are in this repository's git history.

namespace AniMetaPlus.Providers.AniDB.Metadata;

/// <summary>
/// Information about a person as provided by AniDB.
/// </summary>
public class AniDbPersonInfo
{
    /// <summary>
    /// Gets or sets the name of the person.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the AniDB id of the person.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the image url of the person.
    /// </summary>
    public string? Image { get; set; }
}
