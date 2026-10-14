/* AniMeta+ settings page, shared by the Emby and Jellyfin builds. Each server wraps this
   in its own module format and passes the host functions in. */
function aniMetaPlusPage(host) {
    'use strict';
    var pluginId = 'd9211a9c-1ad7-4203-bf8e-9078deb65469';
    var sourceIds = ['AniDB', 'AniList', 'MyAnimeList', 'TheTVDB', 'TheMovieDb'];
    var modes = ['Pause', 'SwitchUpgrade', 'SwitchReview'];
    var modeNames = { Pause: 'Pause', SwitchUpgrade: 'Switch', SwitchReview: 'Switch + review' };
    var kindNames = {
        Titles: 'Titles', Overview: 'Overview', Genres: 'Genres & tags', Rating: 'Rating', People: 'Cast & staff',
        Details: 'Dates & studios', Episodes: 'Episodes', PrimaryImage: 'Poster', BackdropImage: 'Fanart',
        BannerImage: 'Banner', LogoImage: 'Logo'
    };
    var titlePreferences = ['Localized', 'Japanese', 'JapaneseRomaji'];
    var reviewDecisions = { Accepted: 'Accept', Kept: 'Keep', Locked: 'Lock' };

    // Lucide "eye" and "eye-off" (ISC licence, lucide.dev).
    var eyeIcon = '<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0"/><circle cx="12" cy="12" r="3"/></svg>';
    var eyeOffIcon = '<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M10.733 5.076a10.744 10.744 0 0 1 11.205 6.575 1 1 0 0 1 0 .696 10.747 10.747 0 0 1-1.444 2.49"/><path d="M14.084 14.158a3 3 0 0 1-4.242-4.242"/><path d="M17.479 17.499a10.75 10.75 0 0 1-15.417-5.151 1 1 0 0 1 0-.696 10.75 10.75 0 0 1 4.446-5.143"/><path d="m2 2 20 20"/></svg>';

    function esc(value) {
        return String(value === null || value === undefined ? '' : value)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function when(value) {
        if (!value) {
            return '';
        }

        var date = new Date(value);
        return isNaN(date) || date.getFullYear() < 2000 ? '' : date.toLocaleString();
    }

    function enumName(value, names) {
        return typeof value === 'number' ? names[value] : value;
    }

    function notify(text) {
        host.toast(text);
    }

    function get(path, query) {
        return ApiClient.getJSON(ApiClient.getUrl('AniMetaPlus/' + path, query || {}));
    }

    function post(path, query, body) {
        var request = { type: 'POST', url: ApiClient.getUrl('AniMetaPlus/' + path, query || {}), dataType: 'json' };
        if (body !== undefined) {
            request.data = typeof body === 'string' ? body : JSON.stringify(body);
            request.contentType = 'application/json';
        }

        return ApiClient.ajax(request);
    }

    function fail(error) {
        host.hideLoading();
        notify('Request failed' + (error && error.status ? ' (' + error.status + ')' : ''));
    }

    function badge(text, kind) {
        return '<span class="amp-badge ' + (kind || '') + '">' + esc(text) + '</span>';
    }

    function blockText(block) {
        if (!block) {
            return '';
        }

        var reason = enumName(block.Reason, ['Banned', 'RateLimited', 'Unavailable', 'ServerError', 'AuthFailed', 'ClientRejected', 'NotFound', 'ParseFailed', 'Empty', 'Manual', 'Other']);
        return reason + (block.ExpiresUtc ? ' until ' + when(block.ExpiresUtc) : ' (until changed)');
    }

    function withConfig(view, change) {
        host.showLoading();
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            change(config);
            return ApiClient.updatePluginConfiguration(pluginId, config);
        }).then(function (result) {
            host.hideLoading();
            host.configSaved(result);
            return loadAll(view);
        }, fail);
    }

    // Status

    function renderStatus(view, overview, waiting, provisional, requests) {
        var cards = [
            ['Unmatched', overview.Unmatched, 'unmatched'],
            ['Waiting for review', overview.PendingReviews, 'review'],
            ['Blocks in force', overview.ActiveBlocks, 'blocks'],
            ['Waiting for a source', overview.Waiting, null],
            ['Stand-in values', overview.Provisional, null],
            ['Anime in titles dump', overview.Titles ? overview.Titles.AnimeCount : 0, 'titles']
        ];
        view.querySelector('#ampCards').innerHTML = cards.map(function (card) {
            return '<div class="amp-card"' + (card[2] ? ' data-goto="' + card[2] + '" style="cursor:pointer"' : '') + '><b>' + esc(card[1] || 0) + '</b>' + esc(card[0]) + '</div>';
        }).join('');

        var rows = '<tr><th>Source</th><th>State</th><th>Mode</th><th>Last hour / day</th><th>Last test</th></tr>';
        overview.Sources.forEach(function (source) {
            var state = !source.Enabled ? badge('Off')
                : source.Block ? badge('Blocked: ' + blockText(source.Block), 'amp-bad')
                : !source.Available ? badge(source.Unavailable || 'Unavailable', 'amp-warn')
                : badge('Ready', 'amp-ok');
            var test = source.LastTest ? esc(source.LastTest.Result) + ' <span class="amp-small">' + when(source.LastTest.AtUtc) + '</span>' : '';
            rows += '<tr><td>' + esc(source.Id) + '</td><td>' + state + '</td><td>' + esc(modeNames[enumName(source.Mode, modes)] || source.Mode) + '</td><td>' + source.LastHour + ' / ' + source.LastDay + '</td><td>' + test + '</td></tr>';
        });
        view.querySelector('#ampSourceStatus').innerHTML = rows;

        var a = overview.AniDb;
        view.querySelector('#ampAniDbQueue').innerHTML = a.IsBanned
            ? badge('Paused: AniDB is refusing requests for another ' + Math.ceil(a.BanRemainingSeconds / 60) + ' min', 'amp-bad')
            : 'One request every ' + (a.RequestIntervalMs / 1000) + ' s. ' + a.QueuedRequests + ' queued, ' + a.RequestsSent + ' sent since start' + (a.LastRequestUtc ? ', last ' + when(a.LastRequestUtc) : '') + '.';
        view.querySelector('#ampAniDbQueue').innerHTML += '<br/>Mapping overrides: ' + esc(a.OverridesPath) + ' - '
            + (a.OverridesError ? badge(a.OverridesError, 'amp-bad')
                : a.OverridesWrittenAtUtc ? (a.OverridesShowCount || 0) + ' shows, ' + (a.OverridesMovieCount || 0) + ' movies, written ' + when(a.OverridesWrittenAtUtc)
                : 'not present')
            + '<br/>AniBridge: ' + (a.AniBridgeEntryCount || 0) + ' entries' + (a.AniBridgeCachedAtUtc ? ', cached ' + when(a.AniBridgeCachedAtUtc) : '')
            + '. Anime-Lists: ' + (a.AnimeListEntryCount || 0) + ' entries' + (a.AnimeListCachedAtUtc ? ', cached ' + when(a.AnimeListCachedAtUtc) : '') + '.';

        view.querySelector('#ampWaiting').innerHTML = waiting.length
            ? '<tr><th>Item</th><th>Waits for</th><th>Since</th></tr>' + waiting.map(function (w) {
                return '<tr><td>' + esc(w.ItemName) + '</td><td>' + esc(w.Source) + ' (' + esc(w.Reason) + ')</td><td>' + when(w.AddedUtc) + '</td></tr>';
            }).join('')
            : '<tr><td class="amp-muted">Nothing is waiting.</td></tr>';

        view.querySelector('#ampProvisional').innerHTML = provisional.length
            ? '<tr><th>Item</th><th>Field</th><th>Supplied by</th><th>Instead of</th></tr>' + provisional.map(function (p) {
                return '<tr><td>' + esc(p.ItemName) + '</td><td>' + esc(kindNames[p.Field] || p.Field) + '</td><td>' + esc(p.Source) + '</td><td>' + esc(p.PreferredSource) + '</td></tr>';
            }).join('')
            : '<tr><td class="amp-muted">None.</td></tr>';

        view.querySelector('#ampRequests').innerHTML = requests.length
            ? '<tr><th>When</th><th>Source</th><th>What</th><th>Result</th><th>ms</th></tr>' + requests.map(function (r) {
                var ok = r.Outcome === 'ok';
                return '<tr><td>' + when(r.AtUtc) + '</td><td>' + esc(r.Source) + '</td><td>' + esc(r.Target) + '</td><td>' + badge(r.Outcome + (r.HttpStatus ? ' ' + r.HttpStatus : ''), ok ? 'amp-ok' : 'amp-warn') + '</td><td>' + r.DurationMs + '</td></tr>';
            }).join('')
            : '<tr><td class="amp-muted">No requests yet.</td></tr>';
    }

    // Sources

    function renderSources(view, overview) {
        var rows = '<tr><th>Source</th><th>On</th><th>When blocked</th><th>Gap (ms)</th><th>Max / hour</th><th>Max / day</th><th>In rating</th><th>Titles in</th></tr>';
        overview.Settings.forEach(function (s) {
            var mode = enumName(s.Mode, modes);
            rows += '<tr data-source="' + esc(s.Id) + '"><td>' + esc(s.Id) + '</td>'
                + '<td><input type="checkbox" data-field="Enabled"' + (s.Enabled ? ' checked' : '') + ' /></td>'
                + '<td><select data-field="Mode">' + modes.map(function (m) {
                    return '<option value="' + m + '"' + (m === mode ? ' selected' : '') + '>' + modeNames[m] + '</option>';
                }).join('') + '</select></td>'
                + '<td><input type="number" min="' + esc(s.MinimumRequestIntervalMs || 0) + '" step="50" data-field="RequestIntervalMs" title="At least ' + esc(s.MinimumRequestIntervalMs || 0) + ' ms" value="' + esc(s.RequestIntervalMs) + '" /></td>'
                + '<td><input type="number" min="0" data-field="MaxPerHour" value="' + esc(s.MaxPerHour) + '" /></td>'
                + '<td><input type="number" min="0" data-field="MaxPerDay" value="' + esc(s.MaxPerDay) + '" /></td>'
                + '<td><input type="checkbox" data-field="IncludeInRating"' + (s.IncludeInRating ? ' checked' : '') + ' /></td>'
                + '<td><select data-field="TitlePreference"><option value="">Global</option>' + titlePreferences.map(function (t) {
                    return '<option value="' + t + '"' + (t === s.TitlePreference ? ' selected' : '') + '>' + t + '</option>';
                }).join('') + '</select></td></tr>';
        });
        view.querySelector('#ampSources').innerHTML = rows;

        view.querySelector('#ampPriorities').innerHTML = overview.Priorities.map(function (p) {
            return '<div class="inputContainer"><label class="inputLabel">' + esc(kindNames[p.Kind] || p.Kind) + '</label>'
                + '<input is="emby-input" type="text" data-kind="' + esc(p.Kind) + '" value="' + esc(p.Sources) + '" /></div>';
        }).join('');
    }

    function saveSources(view) {
        var unknown = [];
        withConfig(view, function (config) {
            config.Sources = Array.prototype.map.call(view.querySelectorAll('#ampSources tr[data-source]'), function (row) {
                function field(name) {
                    return row.querySelector('[data-field="' + name + '"]');
                }

                return {
                    Id: row.getAttribute('data-source'),
                    Enabled: field('Enabled').checked,
                    Mode: field('Mode').value,
                    RequestIntervalMs: Math.max(parseInt(field('RequestIntervalMs').min, 10) || 0, parseInt(field('RequestIntervalMs').value, 10) || 0),
                    MaxPerHour: parseInt(field('MaxPerHour').value, 10) || 0,
                    MaxPerDay: parseInt(field('MaxPerDay').value, 10) || 0,
                    IncludeInRating: field('IncludeInRating').checked,
                    TitlePreference: field('TitlePreference').value
                };
            });
            config.Priorities = Array.prototype.map.call(view.querySelectorAll('#ampPriorities input[data-kind]'), function (input) {
                var ids = input.value.split(',').map(function (s) { return s.trim(); }).filter(function (s) { return s; });
                ids.forEach(function (id) {
                    if (sourceIds.indexOf(id) < 0) {
                        unknown.push(id);
                    }
                });
                return { Kind: input.getAttribute('data-kind'), Sources: ids.join(',') };
            });
        }).then(function () {
            if (unknown.length) {
                notify('Unknown source ids are ignored: ' + unknown.join(', '));
            }
        });
    }

    // Clients

    function renderClients(view, config) {
        view.querySelector('#ampClientName').value = config.AniDbClientName || '';
        view.querySelector('#ampClientVersion').value = config.AniDbClientVersion || 1;
        view.querySelector('#ampMalClientId').value = config.MalClientId || '';
        view.querySelector('#ampUseJikan').checked = !!config.MalUseJikan;
    }

    function test(view, source) {
        var target = view.querySelector('[data-result="' + source + '"]');
        target.innerHTML = 'Checking...';
        post('Clients/Test', { Source: source }).then(function (result) {
            var kind = result.Result === 'valid' ? 'amp-ok' : (result.Result === 'throttled' ? 'amp-warn' : 'amp-bad');
            target.innerHTML = badge(result.Result, kind) + ' ' + esc(result.Message);
        }, function (error) {
            target.innerHTML = badge('error', 'amp-bad') + ' ' + esc(error && error.status);
        });
    }

    // Titles

    function renderTitles(view, config, overview) {
        var t = overview.Titles;
        view.querySelector('#ampTitlesStatus').innerHTML = t
            ? (t.SourceExists ? badge('In use', 'amp-ok') : badge('Missing', 'amp-bad')) + ' ' + esc(t.SourcePath)
                + '<br/><span class="amp-small">Written ' + when(t.SourceWrittenUtc) + (t.AnimeCount ? ', ' + t.AnimeCount + ' anime' : '') + (t.NextRefreshUtc ? '. Next download due ' + when(t.NextRefreshUtc) : '') + '.</span>'
            : '';
        view.querySelector('#ampTitlesPath').value = config.TitlesDumpPath || '';
        view.querySelector('#ampTitlesAuto').checked = !!config.TitlesAutoRefresh;
        view.querySelector('#ampTitlesDays').value = config.TitlesRefreshDays || 7;
    }

    // Unmatched

    function renderUnmatched(view, items, mappings) {
        view.querySelector('#ampUnmatched').innerHTML = items.length ? items.map(function (u) {
            var guesses = (u.Candidates || []).map(function (c) {
                return '<option value="' + esc(c.AnimeId) + '">' + esc(c.Title) + ' (' + esc(c.AnimeId) + ', ' + c.Score + '%)</option>';
            }).join('');
            return '<div class="amp-card" data-key="' + esc(u.Id) + '" style="margin-bottom:1em">'
                + '<div><b style="font-size:1.1em;display:inline">' + esc(u.Name) + '</b> ' + (u.Year ? '(' + u.Year + ') ' : '') + (u.Ignored ? badge('set aside') : '') + '</div>'
                + '<div class="amp-small">' + esc(u.Path) + ' - tried ' + u.Attempts + 'x, last ' + when(u.LastAttemptUtc) + '</div>'
                + '<div class="amp-row" style="margin-top:.5em">'
                + '<div class="inputContainer"><select class="amp-guess"><option value="">Guesses from the titles dump...</option>' + guesses + '</select></div>'
                + '<div class="inputContainer"><input type="text" class="amp-search" placeholder="Search titles" /></div>'
                + '<div class="inputContainer"><input type="text" class="amp-id" placeholder="AniDB id or link" /></div>'
                + '</div>'
                + '<div class="amp-actions"><button type="button" is="emby-button" class="raised" data-act="map">Map</button>'
                + '<button type="button" is="emby-button" class="raised button-submit" data-act="mapnow">Map now</button>'
                + '<button type="button" is="emby-button" class="raised" data-act="ignore">' + (u.Ignored ? 'Bring back' : 'Set aside') + '</button></div>'
                + '</div>';
        }).join('') : '<p class="amp-muted">Everything is identified.</p>';

        view.querySelector('#ampMappings').innerHTML = mappings.length
            ? '<tr><th>Item</th><th>AniDB</th><th>Mapped</th><th></th></tr>' + mappings.map(function (m) {
                return '<tr><td>' + esc(m.ItemName || m.Id) + '</td><td>' + esc(m.AnimeId) + '</td><td>' + when(m.CreatedUtc) + '</td><td><button type="button" is="emby-button" class="raised" data-unmap="' + esc(m.Id) + '">Remove</button></td></tr>';
            }).join('')
            : '<tr><td class="amp-muted">None.</td></tr>';
    }

    function onUnmatchedClick(view, e) {
        var button = e.target.closest('button[data-act]');
        if (!button) {
            return;
        }

        var card = button.closest('[data-key]');
        var key = card.getAttribute('data-key');
        var act = button.getAttribute('data-act');

        if (act === 'ignore') {
            post('Unmatched/Ignore', { ItemKey: key, Ignore: button.textContent !== 'Bring back' }).then(function () { loadAll(view); }, fail);
            return;
        }

        var id = card.querySelector('.amp-id').value.trim() || card.querySelector('.amp-guess').value;
        if (!id) {
            notify('Pick a guess or enter an AniDB id first');
            return;
        }

        post('Unmatched/Map', null, { ItemKey: key, AnimeId: id, ApplyNow: act === 'mapnow' }).then(function () {
            notify('Mapped to AniDB ' + id + (act === 'mapnow' ? '; refreshing' : '; applies on the next queue run'));
            loadAll(view);
        }, fail);
    }

    function onUnmatchedSearch(e) {
        var input = e.target;
        if (!input.classList.contains('amp-search') || e.key !== 'Enter') {
            return;
        }

        e.preventDefault();
        var select = input.closest('[data-key]').querySelector('.amp-guess');
        get('Titles/Search', { Query: input.value }).then(function (found) {
            select.innerHTML = '<option value="">' + found.length + ' found...</option>' + found.map(function (c) {
                return '<option value="' + esc(c.AnimeId) + '">' + esc(c.Title) + ' (' + esc(c.AnimeId) + ', ' + c.Score + '%)</option>';
            }).join('');
        }, fail);
    }

    // Review

    function renderReviews(view, reviews) {
        view.querySelector('#ampReviews').innerHTML = reviews.length ? reviews.map(function (r) {
            return '<div class="amp-card" data-review="' + esc(r.Id) + '" style="margin-bottom:1em">'
                + '<div><b style="font-size:1.1em;display:inline">' + esc(r.ItemName) + '</b> - ' + esc(kindNames[r.Field] || r.Field) + '</div>'
                + '<table class="amp-table"><tr><th>Now, from ' + esc(r.CurrentSource) + '</th><th>Proposed, from ' + esc(r.ProposedSource) + '</th></tr>'
                + '<tr><td>' + esc(r.CurrentValue) + '</td><td>' + esc(r.ProposedValue) + '</td></tr></table>'
                + '<div class="amp-actions">' + Object.keys(reviewDecisions).map(function (d) {
                    return '<button type="button" is="emby-button" class="raised' + (d === 'Accepted' ? ' button-submit' : '') + '" data-decide="' + d + '">' + reviewDecisions[d] + '</button>';
                }).join('') + '</div></div>';
        }).join('') : '<p class="amp-muted">Nothing to review.</p>';
    }

    // Blocks

    function renderBlocks(view, blocks) {
        view.querySelector('#ampBlocks').innerHTML = blocks.length
            ? '<tr><th>Source</th><th>Item</th><th>Kind</th><th>Why</th><th>Seen</th><th></th></tr>' + blocks.map(function (b) {
                var scope = enumName(b.Scope, ['Source', 'Item']);
                return '<tr><td>' + esc(b.Source) + '</td><td>' + (scope === 'Source' ? badge('whole source', 'amp-bad') : esc(b.ItemName || b.ItemKey)) + '</td><td>' + esc(b.Kind || 'all') + '</td>'
                    + '<td>' + esc(blockText(b)) + '<div class="amp-small">' + esc(b.Message) + (b.HttpStatus ? ' (HTTP ' + b.HttpStatus + ')' : '') + '</div></td>'
                    + '<td>' + b.Count + 'x, last ' + when(b.LastSeenUtc) + '</td>'
                    + '<td class="amp-actions"><button type="button" is="emby-button" class="raised" data-lift="' + esc(b.Id) + '">Lift</button>'
                    + (b.Pinned ? badge('pinned') : '<button type="button" is="emby-button" class="raised" data-pin="' + esc(b.Id) + '">Pin</button>') + '</td></tr>';
            }).join('')
            : '<tr><td class="amp-muted">No blocks.</td></tr>';

        view.querySelector('#ampBlockSource').innerHTML = sourceIds.map(function (s) {
            return '<option value="' + s + '">' + s + '</option>';
        }).join('');
    }

    // General

    function span(minutes) {
        return minutes >= 1440 ? (minutes / 1440) + (minutes === 1440 ? ' day' : ' days') : minutes + ' min';
    }

    function later(value, days) {
        return value ? when(new Date(new Date(value).getTime() + days * 86400000)) : '';
    }

    function renderCache(view, sources) {
        view.querySelector('#ampCache').innerHTML = '<tr><th>Source</th><th>Cached</th><th>Fetched</th><th>Scans reuse it until</th><th>A refresh by hand</th><th></th></tr>'
            + sources.map(function (s) {
                var fresh = s.Answers - s.Expired;
                var until = !s.Answers ? '<span class="amp-muted">nothing cached</span>'
                    : !fresh ? badge('all expired', 'amp-warn') + ' the next refresh or scan asks again'
                    : (fresh === 1 ? esc(later(s.NewestUtc, s.ScanReuseDays))
                        : 'newest until ' + esc(later(s.NewestUtc, s.ScanReuseDays)) + '<br/>oldest until ' + esc(later(s.OldestUtc, s.ScanReuseDays)))
                        + (s.Expired ? '<br/>' + s.Expired + ' already expired' : '');
                return '<tr><td>' + esc(s.Source) + '</td>'
                    + '<td>' + s.Answers + (s.Answers === 1 ? ' answer' : ' answers') + '</td>'
                    + '<td>' + (fresh ? esc(when(s.OldestUtc)) + (fresh > 1 ? ' to<br/>' + esc(when(s.NewestUtc)) : '') : '') + '</td>'
                    + '<td>' + until + '</td>'
                    + '<td class="amp-small">asks again once an answer is ' + span(s.ManualReuseMinutes) + ' old</td>'
                    + '<td>' + (s.Answers ? '<button type="button" is="emby-button" class="raised" data-expire="' + esc(s.Source) + '">Reset</button>' : '') + '</td></tr>';
            }).join('');
    }

    function renderGeneral(view, config) {
        view.querySelector('#ampTitlePreference').value = enumName(config.TitlePreference, titlePreferences) || 'Localized';
        view.querySelector('#ampMaxGenres').value = config.MaxGenres;
        view.querySelector('#ampMinTagWeight').value = config.MinimumTagWeight;
        view.querySelector('#ampTagBlacklist').value = config.TagBlacklist || '';
        view.querySelector('#ampRequestInterval').value = config.RequestIntervalMs;
        view.querySelector('#ampMaxCacheAge').value = config.MaxCacheAge;
        view.querySelector('#ampImportGenres').checked = !!config.ImportGenres;
        view.querySelector('#ampImportTags').checked = !!config.ImportTags;
        view.querySelector('#ampImportRating').checked = !!config.ImportCommunityRating;
        view.querySelector('#ampCastCharacters').checked = !!config.CastShowsCharacters;
        view.querySelector('#ampPublishIds').checked = !!config.PublishMappedIds;
        view.querySelector('#ampUseAniBridge').checked = !!config.UseAniBridgeMappings;
    }

    function loadAll(view) {
        host.showLoading();
        return Promise.all([
            ApiClient.getPluginConfiguration(pluginId),
            get('Overview'),
            get('Waiting'),
            get('Provisional'),
            get('Requests', { Count: 50 }),
            get('Unmatched', { IncludeIgnored: view.querySelector('#ampShowIgnored').checked }),
            get('Mappings'),
            get('Reviews', { PendingOnly: true }),
            get('Blocks', { IncludeExpired: view.querySelector('#ampShowExpired').checked }),
            get('Cache')
        ]).then(function (r) {
            var config = r[0];
            var overview = r[1];
            renderStatus(view, overview, r[2], r[3], r[4]);
            renderSources(view, overview);
            renderClients(view, config);
            renderTitles(view, config, overview);
            renderUnmatched(view, r[5], r[6]);
            renderReviews(view, r[7]);
            renderBlocks(view, r[8]);
            renderGeneral(view, config);
            renderCache(view, r[9]);
            host.hideLoading();
        }, fail);
    }

    function showTab(view, name) {
        Array.prototype.forEach.call(view.querySelectorAll('[data-tab]'), function (el) {
            el.classList.toggle('active', el.getAttribute('data-tab') === name);
        });
    }

    return function (view) {
        var malVisible = false;
        view.querySelector('#ampToggleMal').innerHTML = eyeIcon;

        view.addEventListener('click', function (e) {
            var tab = e.target.closest('.amp-tabs button[data-tab], [data-goto]');
            if (tab) {
                showTab(view, tab.getAttribute('data-tab') || tab.getAttribute('data-goto'));
                return;
            }

            var testButton = e.target.closest('[data-test]');
            if (testButton) {
                test(view, testButton.getAttribute('data-test'));
                return;
            }

            var lift = e.target.closest('[data-lift]');
            if (lift) {
                post('Blocks/Lift', { Id: lift.getAttribute('data-lift') }).then(function () { notify('Lifted'); loadAll(view); }, fail);
                return;
            }

            var pin = e.target.closest('[data-pin]');
            if (pin) {
                post('Blocks/Pin', { Id: pin.getAttribute('data-pin') }).then(function () { loadAll(view); }, fail);
                return;
            }

            var decide = e.target.closest('[data-decide]');
            if (decide) {
                var id = decide.closest('[data-review]').getAttribute('data-review');
                post('Reviews/Decide', { Id: id, Decision: decide.getAttribute('data-decide') }).then(function () { loadAll(view); }, fail);
                return;
            }

            var unmap = e.target.closest('[data-unmap]');
            if (unmap) {
                ApiClient.ajax({ type: 'DELETE', url: ApiClient.getUrl('AniMetaPlus/Mappings', { ItemKey: unmap.getAttribute('data-unmap') }) }).then(function () { loadAll(view); }, fail);
            }
        });

        view.querySelector('#ampUnmatched').addEventListener('click', function (e) { onUnmatchedClick(view, e); });
        view.querySelector('#ampUnmatched').addEventListener('keydown', onUnmatchedSearch);
        view.querySelector('#ampShowIgnored').addEventListener('change', function () { loadAll(view); });
        view.querySelector('#ampShowExpired').addEventListener('change', function () { loadAll(view); });
        view.querySelector('#ampRefreshStatus').addEventListener('click', function () { loadAll(view); });

        view.querySelector('#ampRunQueues').addEventListener('click', function () {
            post('Queues/Run').then(function (r) {
                view.querySelector('#ampQueueResult').innerHTML = 'Queued ' + r.Resumed + ' resumed, ' + r.Upgraded + ' to upgrade, ' + r.Mapped + ' mapped; removed ' + r.Pruned + ' lapsed blocks.';
                loadAll(view);
            }, fail);
        });

        view.querySelector('#ampSaveSources').addEventListener('click', function () { saveSources(view); });

        view.querySelector('#ampSaveAniDb').addEventListener('click', function () {
            withConfig(view, function (config) {
                config.AniDbClientName = view.querySelector('#ampClientName').value.trim();
                config.AniDbClientVersion = parseInt(view.querySelector('#ampClientVersion').value, 10) || 1;
            });
        });

        view.querySelector('#ampToggleMal').addEventListener('click', function () {
            malVisible = !malVisible;
            view.querySelector('#ampMalClientId').type = malVisible ? 'text' : 'password';
            this.innerHTML = malVisible ? eyeOffIcon : eyeIcon;
        });

        view.querySelector('#ampSaveMal').addEventListener('click', function () {
            withConfig(view, function (config) {
                config.MalClientId = view.querySelector('#ampMalClientId').value.trim();
                config.MalUseJikan = view.querySelector('#ampUseJikan').checked;
            });
        });

        view.querySelector('#ampClearMal').addEventListener('click', function () {
            view.querySelector('#ampMalClientId').value = '';
            withConfig(view, function (config) { config.MalClientId = ''; });
        });

        view.querySelector('#ampSaveTitles').addEventListener('click', function () {
            withConfig(view, function (config) {
                config.TitlesDumpPath = view.querySelector('#ampTitlesPath').value.trim();
                config.TitlesAutoRefresh = view.querySelector('#ampTitlesAuto').checked;
                config.TitlesRefreshDays = Math.max(3, parseInt(view.querySelector('#ampTitlesDays').value, 10) || 7);
            });
        });

        view.querySelector('#ampTitlesRefresh').addEventListener('click', function () {
            view.querySelector('#ampTitlesResult').innerHTML = 'Working...';
            post('Titles/Refresh').then(function (r) {
                view.querySelector('#ampTitlesResult').innerHTML = badge(r.Downloaded ? 'downloaded' : 'not downloaded', r.Downloaded ? 'amp-ok' : 'amp-warn') + ' ' + esc(r.Message);
                loadAll(view);
            }, fail);
        });

        view.querySelector('#ampTitlesReimport').addEventListener('click', function () {
            post('Titles/Reimport').then(function () { loadAll(view); }, fail);
        });

        view.querySelector('#ampAddBlock').addEventListener('click', function () {
            post('Blocks', null, {
                Source: view.querySelector('#ampBlockSource').value,
                ItemKey: view.querySelector('#ampBlockItem').value.trim(),
                ItemName: view.querySelector('#ampBlockItem').value.trim(),
                Kind: view.querySelector('#ampBlockKind').value.trim()
            }).then(function () { loadAll(view); }, fail);
        });

        view.querySelector('#ampSaveGeneral').addEventListener('click', function () {
            withConfig(view, function (config) {
                config.TitlePreference = view.querySelector('#ampTitlePreference').value;
                config.MaxGenres = parseInt(view.querySelector('#ampMaxGenres').value, 10) || 0;
                config.MinimumTagWeight = parseInt(view.querySelector('#ampMinTagWeight').value, 10) || 0;
                config.TagBlacklist = view.querySelector('#ampTagBlacklist').value;
                config.RequestIntervalMs = Math.max(2500, parseInt(view.querySelector('#ampRequestInterval').value, 10) || 2500);
                config.MaxCacheAge = Math.max(7, parseInt(view.querySelector('#ampMaxCacheAge').value, 10) || 7);
                config.ImportGenres = view.querySelector('#ampImportGenres').checked;
                config.ImportTags = view.querySelector('#ampImportTags').checked;
                config.ImportCommunityRating = view.querySelector('#ampImportRating').checked;
                config.CastShowsCharacters = view.querySelector('#ampCastCharacters').checked;
                config.PublishMappedIds = view.querySelector('#ampPublishIds').checked;
                config.UseAniBridgeMappings = view.querySelector('#ampUseAniBridge').checked;
            });
        });

        view.querySelector('#ampCache').addEventListener('click', function (e) {
            var button = e.target.closest('[data-expire]');
            if (!button) {
                return;
            }

            var source = button.getAttribute('data-expire');

            // Every cached AniDB answer becomes a request again, paced but many for a large library.
            if (source === 'AniDB' && !window.confirm('Reset every cached AniDB answer? The next refresh or scan asks AniDB again for each anime, one request at a time, which can take hours for a large library.')) {
                return;
            }

            post('Cache/Expire', { Source: source }).then(function (r) {
                view.querySelector('#ampCacheResult').innerHTML = badge('reset', 'amp-ok') + ' ' + esc(r.Message);
                loadAll(view);
            }, fail);
        });

        view.querySelector('#ampExport').addEventListener('click', function () {
            get('Export').then(function (data) {
                var blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
                var link = document.createElement('a');
                link.href = URL.createObjectURL(blob);
                link.download = 'animetaplus-decisions.json';
                link.click();
                URL.revokeObjectURL(link.href);
            }, fail);
        });

        view.querySelector('#ampImportFile').addEventListener('change', function () {
            var file = this.files && this.files[0];
            if (!file) {
                return;
            }

            file.text().then(function (text) {
                return post('Import', null, text);
            }).then(function (count) {
                view.querySelector('#ampImportResult').innerHTML = badge('imported', 'amp-ok') + ' ' + count + ' records.';
                loadAll(view);
            }, fail);
        });

        view.addEventListener('viewshow', function () {
            loadAll(view);
        });
    };
}
