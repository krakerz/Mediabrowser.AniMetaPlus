// Derived from jellyfin-plugin-anidb (https://github.com/jellyfin/jellyfin-plugin-anidb),
// Copyright (C) the Jellyfin contributors, licensed GPL-2.0-only.
// Modified for AniMeta+ since 2026-10-14; the changes are in this repository's git history.

namespace AniMetaPlus.Providers.AniDB.Identity;

/// <summary>
/// The type of an AniDB title.
/// </summary>
public enum TitleType
{
    /// <summary>
    /// The main title.
    /// </summary>
    Main = 0,

    /// <summary>
    /// An official title.
    /// </summary>
    Official = 1,

    /// <summary>
    /// A short title.
    /// </summary>
    Short = 2,

    /// <summary>
    /// A synonym.
    /// </summary>
    Synonym = 3
}
