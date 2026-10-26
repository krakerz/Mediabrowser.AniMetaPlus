// Derived from jellyfin-plugin-anidb (https://github.com/jellyfin/jellyfin-plugin-anidb),
// Copyright (C) the Jellyfin contributors, licensed GPL-2.0-only.
// Modified for AniMeta+ since 2026-10-14; the changes are in this repository's git history.

namespace AniMetaPlus.Providers.AniDB.Mapping;

/// <summary>
/// A show identified from an id another provider had already settled on, and what identified it.
/// </summary>
/// <param name="AnimeId">The AniDB id the show begins at.</param>
/// <param name="Source">Which mapping source answered, as a noun phrase for a log message.</param>
/// <param name="Provider">Which provider's id it was found from.</param>
/// <param name="ProviderId">That id.</param>
internal sealed record MappedSeries(string AnimeId, string Source, string Provider, string ProviderId);
