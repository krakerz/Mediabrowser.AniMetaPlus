// Derived from jellyfin-plugin-anidb (https://github.com/jellyfin/jellyfin-plugin-anidb),
// Copyright (C) the Jellyfin contributors, licensed GPL-2.0-only.
// Modified for AniMeta+ since 2026-10-14; the changes are in this repository's git history.

using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace AniMetaPlus.Providers.AniDB;

/// <summary>
/// The AniDB external id for episodes.
/// </summary>
public class AniDbExternalEpisodeId : IExternalId
{
    /// <inheritdoc />
    public string ProviderName
        => "AniDB";

    /// <inheritdoc />
    public string Key
        => ProviderNames.AniDb;

    /// <inheritdoc />
    public ExternalIdMediaType? Type
        => ExternalIdMediaType.Episode;

    /// <inheritdoc />
    public string? UrlFormatString => "https://anidb.net/episode/{0}";

    /// <inheritdoc />
    public bool Supports(IHasProviderIds item)
        => item is Episode;
}
