// Derived from jellyfin-plugin-anidb (https://github.com/jellyfin/jellyfin-plugin-anidb),
// Copyright (C) the Jellyfin contributors, licensed GPL-2.0-only.
// Modified for AniMeta+ since 2026-10-14; the changes are in this repository's git history.

namespace AniMetaPlus.Providers.AniDB.Metadata;

/// <summary>
/// A single title as provided by AniDB.
/// </summary>
public class Title
{
    /// <summary>
    /// Gets or sets the language of the title.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets the type of the title.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets the title itself.
    /// </summary>
    public string? Name { get; set; }
}
