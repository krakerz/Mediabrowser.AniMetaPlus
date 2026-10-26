// Derived from jellyfin-plugin-anidb (https://github.com/jellyfin/jellyfin-plugin-anidb),
// Copyright (C) the Jellyfin contributors, licensed GPL-2.0-only.
// Modified for AniMeta+ since 2026-10-14; the changes are in this repository's git history.

namespace AniMetaPlus.Providers.AniDB.Similarity;

/// <summary>
/// An anime offered as being like the one asked about, and how strongly. The score is comparable
/// only against others from the same ranking: it exists to order them, not to be shown.
/// </summary>
/// <param name="AnimeId">The AniDB id of the anime.</param>
/// <param name="Score">How strongly the anime is held to be alike, from 0 to 1.</param>
internal sealed record AniDbRankedAnime(string AnimeId, double Score);
