# Changelog

All notable changes to AniMeta+ are documented here. The format follows Keep a Changelog, and the project uses semantic versioning.

## [Unreleased]

## [2.14.1] — 2026-10-19

### Fixed
- Review tab: the reviews are one table (item, field, now, proposed, actions), so the columns line up instead of each card's values sitting at a different width
- Unmatched tab: the items to map are one table too (item and path, tries, guess / search / id, actions)

## [2.14.0] — 2026-10-18

### Added
- Blocks tab: click a column header to sort (newest first by default), filter by source, kind, reason or text, and lift every shown block at once. Pinned blocks are left alone, and the lifted items are not refreshed straight away, so lifting hundreds doesn't set off hundreds of requests

### Changed
- The "Most genres per show" setting says that 0 means no limit
- In the overview order, a season's AniList and MyAnimeList entries are taken from the AniBridge mappings of its AniDB entry. Their title search only runs when the mappings link none

### Fixed
- A show is matched to the AniDB entry that has its name exactly (the item's name, the folder name, or the folder name without notes like "(Spring)") before any fuzzy search. "The Beginning After the End (Spring)" was matched to its second season, and a name cut short to "The Beginning" was matched to a Tiger & Bunny film, which also put the show into that franchise's collection
- Specials seasons no longer ask AniList or MyAnimeList for an overview or poster: those describe the whole show, not the specials
- Stand-in values and reviews from before the source order changed are settled once their source no longer comes first, instead of spending AniDB requests and creating reviews against the current order

## [2.13.0] — 2026-10-18

### Added
- Franchise collections take the poster and backdrop of their earliest entry, replacing the collage Emby draws for a new collection; art set by hand later is kept

## [2.12.0] — 2026-10-18

### Added
- Option to make alternative titles searchable: short names such as "DanMachi", the English title and synonyms go into the original title, which Emby and Jellyfin search, while the shown title stays as it is
- Alternative titles come from the local titles dump, then from cached AniList and MyAnimeList answers, so shows without an AniDB entry get them too
- Daily task "Apply alternative titles to original titles" that writes them into existing shows, or takes them out when the option is off, without refreshing or sending any request
- AniList and MyAnimeList synonyms are kept with their cached answers
- Series get a Continuing or Ended status from their air dates
- Option and daily task to group franchises into collections: sequels, prequels, side stories and summaries in the library, from cached AniDB answers; collections made by hand are left alone

### Fixed
- A "Show 2" folder that Emby names "Show" is identified as the sequel, not the first show

## [2.11.0] — 2026-10-18

### Added
- Season details can come from each season's own entry in the Overview and Poster source order, so MyAnimeList or AniList give every season its own synopsis
- Season names setting: from the source the details came from, or always plain "Season 1", "Season 2", "Specials"

## [2.10.0] — 2026-10-18

### Changed
- AniDB's request gap is set only on the Sources tab; a larger gap set on the General tab moves there at startup
- Each scan or queue run sends AniDB only what is left of its current burst, hour and day, replacing the "Shows sent to AniDB per run" setting
- The source status keeps each source on one line: caps follow the counts, the burst shows on hover, and a rest shows in the State column

### Fixed
- Lowering the burst size below what the current burst already sent starts the rest at once
- The mapping lists show "read on first use" after a restart instead of 0 entries

## [2.9.0] — 2026-10-18

### Added
- AniDB requests go out in bursts followed by a rest (20, then 120 minutes, by default), set per source on the Sources tab next to the hourly and daily caps
- The Sources tab estimates how many AniDB requests a day the settings allow, and warns when that, or the day cap, is past the 256 a day AniDB is believed to ban for
- A ban that comes after 256 AniDB requests in 24 hours pauses AniDB for a full day at once
- Season details setting: from the AniDB entry, from TheTVDB / TheMovieDb, or plain "Season 1, Season 2" with nothing fetched

### Changed
- AniDB's default hourly cap is replaced by the burst-and-rest pacing, and its default daily cap is 250 instead of 150
- While AniDB is banned, resting or capped, the library scan and the queue hold back shows AniDB has never answered for, where AniDB comes first for titles, overview or dates, instead of filling them with stand-in values
- The status line shows AniDB's burst progress and when a rest ends

## [2.8.0] — 2026-10-17

### Changed
- AniDB is held to 20 requests an hour and 150 a day unless other caps are set on the Sources tab; leaving them empty no longer means no cap
- AniDB's request counts survive a server restart, so a restart no longer resets its caps
- The library scan and the queue wait while AniDB's cap is used up instead of filling shows with stand-in values
- The Sources status shows each source's caps next to its request counts

## [2.7.1] — 2026-10-17

### Fixed
- Downloading the titles dump no longer fails with "The downloaded dump holds only 0 titles"

## [2.7.0] — 2026-10-17

### Added
- Episode title language setting, so a show can keep its romaji title while episode titles from every source follow another language

### Changed
- Source order is set with draggable source chips, arrows and a Default button instead of comma-separated text
- Episodes follow the Episodes source order: sources placed before AniDB are asked first, and AniDB taken out of it is not asked
- MyAnimeList comes before AniList in the default source order, so AniList is found by its MyAnimeList id rather than by title

### Fixed
- An AniList or MyAnimeList title search that found nothing no longer blocks the show's lookup by id; such blocks from earlier versions are removed once at startup

## [2.6.0] — 2026-10-17

### Added
- Keep, Retry now and Keep all for stand-in values, and a column showing what holds each one back
- AniDB blocks on a single anime show the anime's title on the Blocks tab

### Fixed
- A show with a stand-in value held for review no longer fails every refresh, which kept the stand-in and the rest of its AniMeta+ data from ever updating
- Saving a review, ignore or unmap decision no longer fails with an error after it was stored
- Stand-in values the preferred source has nothing for are no longer refreshed on every queue run; they are retried at most every 6 hours, and not at all while that source blocks the item

## [2.5.0] — 2026-10-17

### Added
- Status shows the running AniMeta+ version and whether the server needs a restart to load an installed update
- Refresh and "Select this page" buttons on the Image overrides tab

## [2.4.0] — 2026-10-17

### Added
- Image overrides tab to compare, revert or acknowledge posters the AniDB poster task replaced; reverting runs in the background
- Downloads from AniDB's image server show in Recent requests as "AniDB image"

### Fixed
- The poster replace task no longer stops on a season without its own folder when artwork is saved with the media
- One poster that cannot be saved no longer ends the poster replace task for the rest

## [2.3.0] — 2026-10-17

### Added
- Manual task "Fill missing posters from cached AniDB answers" that restores posters without asking the AniDB API
- Manual task "Replace posters with AniDB's from cached answers" that sets every anime poster to AniDB's without asking the AniDB API
- Setting to look up AniDB images from the cached answer, so image refreshes and Edit Images don't ask the AniDB API (on by default)

### Changed
- Shows sent to AniDB per scan or queue run default to 20 instead of 25 (saved settings keep their value)
- Shows mapped by hand count against the per-run AniDB limit
- A 401 or 403 from AniList, Jikan, TVDB or TMDB blocks that source for hours instead of until lifted by hand; such old blocks are removed at startup

### Fixed
- Turning AniDB off on the Sources tab now stops all AniDB requests
- The cached AniDB answer is used when AniDB is blocked, capped, unreachable or refuses the anime, instead of no AniDB data
- A cancelled refresh or a non-XML answer no longer corrupts the cached AniDB answer
- A cast name with unusual characters no longer marks the anime as refused by AniDB
- Short AniDB or other source outages no longer escalate into blocks of several hours
- A source's block is no longer lifted by an answer to a request sent before it
- Concurrent requests during an AniDB ban no longer double the ban pause
- Titles with "&" or quotes now match the titles dump
- The titles index loads again after a failed or cancelled first load
- The titles dump is not downloaded twice when the task and the Refresh button run together
- Reimport titles now re-imports, also when the dump path points to an older file
- Items waiting for review are no longer refreshed on every queue run when there is nothing to review
- A failed refresh no longer counts as done in the queue
- Episodes in a gap between mapped ranges no longer get a neighbouring episode's metadata
- A missing season in the library no longer shifts later seasons onto the wrong AniDB entry
- "Opening 1" no longer matches "Opening 10" and later
- Mapping override files saved with a BOM, or with a misplaced list, are read in full
- An unreadable mapping file no longer fails every refresh
- Emby exports can be imported again, and exports keep Keep and Lock decisions
- Mapping an item to AniList or MyAnimeList on Emby no longer shows "Request failed"
- Saving a decision no longer fails after it was stored
- Images for unidentified movies use the movie's own block and lookup

## [2.2.0] — 2026-10-16

### Changed
- The first pause after an AniDB ban is 45 minutes instead of 30, still doubling up to 24 hours

## [2.1.1] — 2026-10-15

### Fixed
- A server restart no longer leaves shows blocked from AniDB or other sources; such blocks from earlier versions are removed at startup
- A show AniDB refused still gets images from AniList, MyAnimeList, TVDB and TMDB

## [2.1.0] — 2026-10-15

### Changed
- Status, Unmatched and General are split into sub-tabs, with the Status buttons at the top

### Fixed
- Mapping or setting aside an unmatched item no longer reloads the list and loses the other items' search results

## [2.0.0] — 2026-10-15

### Changed
- The Jellyfin build is for Jellyfin 10.11 instead of Jellyfin 12; Jellyfin 12 servers should stay on 1.6.0
- The plugin repository lists each release with the Jellyfin version it was built for

### Removed
- Similar items on Jellyfin, which needs Jellyfin 12

## [1.6.0] — 2026-10-14

### Added
- Unmatched tab searches the titles dump, AniList and MyAnimeList together, for one item or for all listed at once

### Changed
- Shows mapped by hand are no longer listed as unmatched; they appear under Mapped by hand

### Fixed
- The Status page could fail to load while counting unmatched items

## [1.5.0] — 2026-10-14

### Added
- Unmatched tab searches AniList and MyAnimeList, showing which results AniDB lists
- Shows can be mapped to an AniList or MyAnimeList entry and are filled from those when AniDB has no entry

### Fixed
- Unmatched entries for shows that already have an AniDB id are cleared by the next scan run
- AniDB requests show in the Status counts and the request log
- AniDB's gap and caps on the Sources tab are applied; the larger of the two AniDB gaps is used

## [1.4.0] — 2026-10-14

### Added
- Setting for how many shows one scan or queue run sends to AniDB, 25 by default

### Changed
- The scan task identifies the whole library each run; only shows needing a new AniDB answer count toward the per-run limit
- Catch-up after an AniDB ban follows the same per-run limit
- How long scans reuse a fetched answer can be set down to 3 days (7 by default)
- Fetched answers are kept in the plugin's data folder instead of the server's cache folder, so cache cleanup no longer deletes them; existing ones are moved on first start

### Fixed
- On Jellyfin the scan task took every show for unidentified and kept refreshing already matched shows

## [1.3.0] — 2026-10-14

### Added
- General tab shows how many AniDB, AniList and MyAnimeList answers are cached and until when, with a Reset button per source

### Fixed
- A refresh started by hand asks AniList and MyAnimeList again, and AniDB once its answer is a day old, instead of reusing week-old answers

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
