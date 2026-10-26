// Derived from jellyfin-plugin-anidb (https://github.com/jellyfin/jellyfin-plugin-anidb),
// Copyright (C) the Jellyfin contributors, licensed GPL-2.0-only.
// Modified for AniMeta+ since 2026-10-14; the changes are in this repository's git history.

using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace AniMetaPlus.Providers.AniDB;

/// <summary>
/// External url provider for AniDB.
/// </summary>
public class AniDbExternalUrlProvider : IExternalUrlProvider
{
    /// <inheritdoc />
    public string Name => "AniDB";

    /// <inheritdoc />
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item.TryGetProviderId(ProviderNames.AniDb, out var externalId))
        {
            switch (item)
            {
                case Series:
                case Season:
                case Movie:
                    yield return $"https://anidb.net/anime/{externalId}";
                    break;
                case Episode:
                    yield return $"https://anidb.net/episode/{externalId}";
                    break;
                case Person:
                    yield return $"https://anidb.net/creator/{externalId}";
                    break;
            }
        }
    }
}
