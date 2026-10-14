# Changelog

All notable changes to AniMeta+ are documented here. The format follows Keep a Changelog, and the project uses semantic versioning.

## [Unreleased]

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
