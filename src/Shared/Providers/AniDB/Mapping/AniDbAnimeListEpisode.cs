// Derived from jellyfin-plugin-anidb (https://github.com/jellyfin/jellyfin-plugin-anidb),
// Copyright (C) the Jellyfin contributors, licensed GPL-2.0-only.
// Modified for AniMeta+ since 2026-10-14; the changes are in this repository's git history.

using AniMetaPlus.Providers.AniDB.Metadata;

namespace AniMetaPlus.Providers.AniDB.Mapping;

/// <summary>
/// Where a single episode is read from, once a mapping source has placed it.
/// </summary>
/// <param name="AnimeId">The AniDB id of the entry holding it.</param>
/// <param name="Number">Its number within that entry.</param>
/// <param name="Kind">Which of the entry's episode numberings that number belongs to.</param>
internal sealed record AniDbAnimeListEpisode(string AnimeId, int Number, AniDbEpisodeKind Kind);
