// Derived from jellyfin-plugin-anidb (https://github.com/jellyfin/jellyfin-plugin-anidb),
// Copyright (C) the Jellyfin contributors, licensed GPL-2.0-only.
// Modified for AniMeta+ since 2026-10-14; the changes are in this repository's git history.

using AniMetaPlus.Providers.AniDB.Mapping;
using Xunit;

namespace AniMetaPlus.Tests;

public class Smoke
{
    [Fact]
    public void InternalsAreVisible() => Assert.Equal("tmdb:1", MovieKey.Tmdb("1"));

    [Fact]
    public void SkipWorks()
    {
        Assert.Skip("dynamic skip is available");
    }
}
