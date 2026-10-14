# Changelog

All notable changes to AniMeta+ are documented here. The format follows Keep a Changelog, and the project uses semantic versioning.

## [Unreleased]

## [1.2.1] — 2026-10-14

### Fixed
- Shows AniDB never answered for are identified and filled from local data and the other sources while AniDB is banned
- Episodes AniDB never answered for are filled from TVDB or TMDB while AniDB is banned

## [1.2.0] — 2026-10-14

### Added
- Jellyfin plugin repository, so AniMeta+ installs and updates from Jellyfin's plugin catalog
- Plugin banner in Emby's and Jellyfin's plugin lists

## [1.1.0] — 2026-10-14

### Added
- Identify by AniDB, MyAnimeList or AniList id, each offered while that source is set up and its key accepted; Emby shows the fields in its Identify dialog
- MyAnimeList and AniList links and id fields on shows and movies

### Changed
- License corrected to GPL-2.0 to match the upstream Jellyfin AniDB plugin
- Identify searches the local titles dump only and shows a "Local only" poster; the refresh after a pick fetches the real data
- AniDB titles dump is downloaded at most every 3 days, counted from the last attempt, including "Download now"
- Per-source request gaps can no longer be set below their defaults
- Images in the server's image picker are labelled by source, such as "AniMeta+ (AniList)"
- A throttled client check says how long until the next one is allowed

### Fixed
- AniDB titles failed to load for title matching
- A client check that found no key no longer holds up the next check
- Episodes named without a season on Jellyfin go into season 1 instead of "Season Unknown"
- Settings page fields, dropdowns, the show/hide button and lone buttons line up on Emby and Jellyfin

## [1.0.0] — 2026-10-14

### Added
- Emby 4.10 support alongside Jellyfin, built from one shared codebase
- AniList and MyAnimeList as metadata sources, plus the server's installed TVDB and TMDB providers as fallbacks
- Per-kind source order for titles, overview, details, genres, rating, cast, episodes and each image type
- Genres, rating and cast combined across sources
- Local AniDB titles dump as the primary title source, refreshed only by a scheduled task and never more than once a day
- AniDB client name and version settings, with AniDB turned off until a client name is set
- Client and key test endpoint for AniDB, AniList and MyAnimeList
- Remembered blocks for every failed request, per source or per item, with lift and pin
- Pause, switch, and switch-with-review modes for a blocked source
- Review list for replacing values a stand-in source supplied
- Unmatched list with title search over the local dump and mapping by hand
- Library scan that identifies from local data first and refreshes a batch at a time, newest first
- Export and import of mappings, reviews and pinned blocks
- Recent request log
- Settings page on Emby and Jellyfin with status, sources, clients, titles dump, unmatched, review, blocks and backup tabs
- Movies and seasons filled from the other sources where AniDB has nothing
- MyAnimeList looked up by title when no id is known
- Library scan, queue and titles dump scheduled tasks
- Plugin ships as a single DLL per server

### Changed
- Renamed to AniMeta+ with a new plugin id; existing AniDB ids on items are kept
- Settings and any active AniDB ban are taken over from the earlier AniDB plugin on first start
- Adult tags are included by default
- Mapped TVDB and TMDB ids are written onto shows by default

### Fixed
- AniDB server errors and timeouts no longer get retried immediately by every episode
- AniDB titles dump title types are read correctly
- Folder names with notes such as "(Fall)" or "[BD]" are identified
- A source that does not answer no longer holds up a refresh for more than 20 seconds
