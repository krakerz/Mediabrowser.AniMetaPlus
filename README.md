# AniMeta+

Anime metadata for Emby and Jellyfin from AniDB, AniList and MyAnimeList, with the server's own TVDB and TMDB providers as fallbacks.

## Description

AniDB bans an IP address when it is asked too often, and every install of the old Jellyfin AniDB plugin shared one borrowed client name, so one busy library could get the whole user base banned. Native TVDB and TMDB metadata is often wrong for anime.

AniMeta+ identifies anime from a local AniDB titles dump and mapping files first, and only asks AniDB with the user's own registered client name. It remembers every failed request so it does not repeat it, and fills each field from other sources in an order the user sets. One codebase builds two plugins, one for Emby and one for Jellyfin.

Licensed under GPL-2.0, see [LICENSE](LICENSE). The bundled Lucide icons are under the ISC license, see `src/Shared/Web/LICENSE-lucide.txt`.

## Features

- Works on Emby Server 4.10+ and Jellyfin 10.11 (separate DLL for each); releases before 2.0.0 were for Jellyfin 12
- Sources: AniDB (needs your own client name; off until set), AniList (no key), MyAnimeList (official API with a client id, or Jikan), plus the server's installed TheTVDB and TheMovieDb providers, asked by AniMeta+ itself even when unticked in the library
- Source order per kind of data: titles, overview, dates & studios, genres & tags, rating, cast & staff, episodes, poster, fanart, banner, logo
- Genres/tags, rating (vote-weighted) and cast are combined across sources; the rest comes from the first source that has it
- Identification without network: local titles dump (anime-titles.dat.gz or .xml.gz), AniBridge and Anime-Lists mappings, manual mappings; folder notes like "(Fall)" or "[BD]" are ignored
- Every failed request is remembered as a block, either for the whole source (ban, rate limit, server down, refused key) or for one item (not found, unreadable), with growing backoff; blocks can be lifted or pinned by hand
- Per source, when it is blocked: Pause (item keeps its data and waits), Switch (next source fills in, replaced automatically later), or Switch + review (replacement waits for Accept / Keep / Lock)
- Unmatched list with suggestions from the titles dump, title search, AniList and MyAnimeList search, and mapping by AniDB, AniList or MyAnimeList id or link; a show AniDB has no entry for is filled from AniList, MyAnimeList, TVDB and TMDB
- Identify by title, AniDB id, MyAnimeList id or AniList id (each id only while that source is set up and its key accepted; Emby shows the id fields); results come from local data with a "Local only" poster, and the real data and images arrive on the refresh after a pick
- Images from every source in the image order, labelled by source in the image picker, such as "AniMeta+ (AniList)"
- AniDB, MyAnimeList and AniList links on shows and movies
- Titles dump downloaded by a scheduled task or by hand, never more than once every 3 days
- Cached AniDB, AniList and MyAnimeList answers listed with the date scans reuse them until, and reset per source
- Request gaps per source can be raised but never set below their defaults
- Library scan task that identifies the whole library from local data each run; only shows that need a new AniDB answer are paced, a set number per run (25 by default)
- Settings page with Status, Sources, Clients & keys, Titles dump, Unmatched, Review, Blocks, General and Import/Export tabs; recent request log
- Export/Import of mappings, review decisions and pinned blocks (never keys), usable across Emby and Jellyfin
- Adult content is always included; AniDB's restricted flag still sets the parental rating
- Takes over settings and any active ban from the earlier AniDB plugin on first start; existing AniDB ids on items are kept

## Installation

### Emby

1. Download the Emby zip for your server from the GitHub release and copy `AniMetaPlus.dll` into Emby's plugins folder:
   - Linux package: `/var/lib/emby/plugins`
   - Docker: `/config/plugins`
   - Windows: `%AppData%\Emby-Server\programdata\plugins`
2. Restart Emby.
3. Open Settings > AniMeta+ and go to **Clients & keys**. Enter your own AniDB client name (register one on anidb.net, under your profile, "Client registration") and press **Test**. AniDB is not asked anything until a client name is set.
4. In the anime library's settings, tick AniMeta+ as metadata and image downloader for Series, Season and Episode, and untick TheTVDB and TheMovieDb.
5. Optional: put AniDB's `anime-titles.dat.gz` in `<plugins>/configurations/AniMetaPlus/` so anime can be identified at once. Otherwise the "Refresh AniDB titles dump" task downloads it, at most once every 3 days.

### Jellyfin

From the plugin repository (updates show up in Jellyfin by themselves):

1. Open Dashboard > Plugins > Manage Repositories, add a repository named `AniMeta+` with the URL `https://raw.githubusercontent.com/krakerz/Mediabrowser.AniMetaPlus/manifest/manifest.json`, and save.
2. Find AniMeta+ in the plugin catalog under Anime, install it, and restart Jellyfin.

If you installed it by hand before, delete that `AniMetaPlus_*` plugin folder first, so that Jellyfin does not load two copies.

By hand instead:

1. Download the Jellyfin zip for your server from the GitHub release. Create the folder `<jellyfin config>/plugins/AniMetaPlus_2.0.0.0/` and copy `AniMetaPlus.dll` into it (when updating, replace the DLL in the `AniMetaPlus_*` folder you already have instead of adding a second one):
   - Linux package: `/var/lib/jellyfin/plugins`
   - Docker: `/config/plugins`
   - Windows: `%ProgramData%\Jellyfin\Server\plugins`
2. Restart Jellyfin.

Then, either way:

3. Open Dashboard > Plugins > AniMeta+ and go to **Clients & keys**. Enter your own AniDB client name and press **Test**. AniDB is not asked anything until a client name is set.
4. In the anime library's settings, tick AniMeta+ as metadata and image fetcher for Series, Season and Episode, and untick TheTVDB and TheMovieDb. AniMeta+ still uses them itself where its source order lists them, so the TheTVDB plugin must stay installed for TVDB data.
5. Optional: put `anime-titles.dat.gz` in `<plugins>/configurations/AniMetaPlus/` as for Emby.

## Building from source

Prerequisites: the .NET SDK 10. The .NET 8 (Emby) and .NET 9 (Jellyfin) targeting packs are restored automatically.

```
dotnet build AniMetaPlus.slnx -c Release
```

Outputs (LiteDB is merged into each DLL):

- `src/AniMetaPlus.Emby/bin/Release/net8.0/merged/AniMetaPlus.dll`
- `src/AniMetaPlus.Jellyfin/bin/Release/net9.0/merged/AniMetaPlus.dll`

Tests (need the ASP.NET Core 10 runtime):

```
dotnet tests/AniMetaPlus.Tests/bin/Release/net10.0/AniMetaPlus.Tests.dll
```

Layout:

- `src/Shared`: all logic, compiled into both plugins
- `src/AniMetaPlus.Emby`: Emby host
- `src/AniMetaPlus.Jellyfin`: Jellyfin host
- `tests/`: unit tests

## Usage

### Quick start

1. Register an AniDB client on anidb.net and enter the name under **Clients & keys**, then press **Test**.
2. Create an anime library with only AniMeta+ ticked as metadata provider (untick TheTVDB and TheMovieDb).
3. Scan the library.

### Settings

- **Status**: ban and block state, request queue, and how fresh each mapping source is
- **Sources**: enable sources, set the order per kind of data, and choose the mode for a blocked source
- **Clients & keys**: AniDB client name, MyAnimeList client id, Jikan, and the Test buttons
- **Titles dump**: location and state of the AniDB titles dump
- **Unmatched**: items AniMeta+ could not identify, with suggestions and manual mapping
- **Review**: replacement values waiting for Accept / Keep / Lock
- **Blocks**: remembered failures, with lift and pin
- **General**: general plugin options
- **Import/Export**: mappings, review decisions and pinned blocks (never keys)

### Source modes

When a source is blocked, the mode decides what happens to the items it would fill:

- **Pause**: the item keeps its data and waits for the source to recover.
- **Switch**: the next source in the order fills in, and is replaced automatically later.
- **Switch + review**: the replacement waits in the Review tab for Accept / Keep / Lock.

### Scheduled tasks

- **Refresh AniDB titles dump**: every 12 hours; downloads only when due.
- **Process AniMeta+ queues**: every 30 minutes.
- **Identify anime libraries**: hourly; identifies the whole library from local data and sends at most the set number of shows (25 by default) to AniDB per run.

### Season mapping and overrides

- Identification uses a TMDB, IMDb or TVDB id another provider has already settled on, looked up in the mapping sources. With no id, the name is matched against the titles dump.
- Season placement comes from the [AniBridge mappings](https://github.com/anibridge/anibridge-mappings), then the [Anime-Lists](https://github.com/Anime-Lists/anime-lists) anime list. Both are cached and refetched only when the publisher says the file has changed. Where neither places a season, AniDB's sequel and prequel relations are used.
- A mapping override file you write is the last word on the entries it names. It is described in [docs/mapping-overrides.md](docs/mapping-overrides.md), which also covers ranges, ratios and movies.

### Data locations

- `<plugins>/configurations/AniMetaPlus/`: database `animetaplus.db`, `decisions.json` (a copy of your review decisions) and the titles dump.
- `<plugins>/configurations/AniMetaPlus/cache/`: what AniDB, AniList and MyAnimeList answered (one entry per anime), cast details and the mapping files. Kept out of the server's cache folder so that the server's cache cleanup does not delete them; reset them per source on the General tab.

## FAQ

**Why is AniDB not asked anything?**
No client name is set, or AniDB rejected it. The Status tab shows which.

**Will a different client name lift an AniDB ban?**
No. Bans are per IP address.

**Why untick TheTVDB/TheMovieDb but keep them installed?**
AniMeta+ calls them itself, only for what its source order says. If they were ticked in the library, the server would also run them for everything, and providers ticked below AniMeta+ would fill any fields it left empty with their own data.

**An anime was not identified.**
Open the Unmatched tab and pick a suggestion, search by title, search AniList and MyAnimeList, or paste an AniDB, AniList or MyAnimeList id or link. Map now refreshes it. Anime too new for AniDB are mapped to their AniList or MyAnimeList entry and move to AniDB by themselves once the mappings list them there.

**What happens when a source is down?**
It is blocked and retried with backoff. Depending on the mode, the item either pauses or falls back to the next source.

**A refresh does not change anything.**
Scans and scheduled tasks reuse fetched answers for the days set under General. A refresh you start by hand asks AniList and MyAnimeList again, and AniDB once its answer is a day old: AniDB asks clients not to fetch the same anime more often.

**Can I move my mappings between Emby and Jellyfin?**
Yes, using Import/Export.

---

### Notes

Built and maintained with the help of AI.
