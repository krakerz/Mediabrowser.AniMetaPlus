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

    // Paging. Every list has a pager in its toolbar (data-pager="name"); the page lives here, so
    // a reload that redraws the list keeps it.

    var pagerSizes = { mapping: 100, blocks: 100, unmatched: 25, review: 50, waiting: 50, provisional: 50, requests: 50, images: 24 };
    var pagers = {};
    var pagerRenders = {};

    function pagerState(name) {
        return pagers[name] || (pagers[name] = { page: 0, size: pagerSizes[name] });
    }

    // Clamps the list's page, draws its arrows and label, and returns the index its page starts at.
    function pageStart(view, name, total) {
        var state = pagerState(name);
        var pages = Math.max(1, Math.ceil(total / state.size));
        state.page = Math.min(Math.max(state.page, 0), pages - 1);

        var pager = view.querySelector('[data-pager="' + name + '"]');
        if (pager) {
            pager.style.display = pages > 1 ? '' : 'none';
            pager.querySelector('[data-page-label]').textContent = (state.page + 1) + ' / ' + pages;
            pager.querySelector('[data-page="-1"]').disabled = state.page <= 0;
            pager.querySelector('[data-page="1"]').disabled = state.page >= pages - 1;
        }

        return state.page * state.size;
    }

    function pageRows(view, name, list) {
        var start = pageStart(view, name, list.length);
        return list.slice(start, start + pagerState(name).size);
    }

    function resetPage(name) {
        pagerState(name).page = 0;
    }

    // Status

    function entries(count) {
        return count < 0 ? 'read on first use since the server started' : (count || 0) + ' entries';
    }

    function versionParts(version) {
        return String(version || '').split('.').map(function (part) { return parseInt(part, 10) || 0; });
    }

    function compareVersions(a, b) {
        var x = versionParts(a);
        var y = versionParts(b);
        for (var i = 0; i < Math.max(x.length, y.length); i++) {
            if ((x[i] || 0) !== (y[i] || 0)) {
                return (x[i] || 0) - (y[i] || 0);
            }
        }

        return 0;
    }

    function shortVersion(version) {
        var parts = String(version || '').split('.');
        while (parts.length > 3 && parts[parts.length - 1] === '0') {
            parts.pop();
        }

        return parts.join('.');
    }

    // What the server has installed can be newer than what it runs: an update only loads after a restart.
    function renderPluginState(view, running) {
        var line = view.querySelector('#ampPluginState');
        Promise.all([
            ApiClient.getInstalledPlugins().catch(function () { return []; }),
            ApiClient.getSystemInfo().catch(function () { return {}; })
        ]).then(function (results) {
            var mine = (results[0] || []).filter(function (p) {
                return String(p.Id || '').replace(/-/g, '').toLowerCase() === pluginId.replace(/-/g, '').toLowerCase();
            });
            var newest = mine.reduce(function (best, p) { return !best || compareVersions(p.Version, best) > 0 ? p.Version : best; }, '');
            var waiting = mine.some(function (p) { return p.Status === 'Restart'; });
            var pending = !!results[1].HasPendingRestart;
            var text = 'AniMeta+ ' + shortVersion(running) + ' running';

            if ((newest && compareVersions(newest, running) > 0) || waiting) {
                line.innerHTML = badge('Restart needed', 'amp-warn') + ' ' + esc(text + '; ' + (newest && compareVersions(newest, running) > 0 ? shortVersion(newest) + ' is installed and' : 'an update') + ' loads after the server restarts.');
            } else if (pending) {
                line.innerHTML = badge('Restart pending', 'amp-warn') + ' ' + esc(text + '; the server is waiting for a restart to finish an update.');
            } else {
                line.innerHTML = badge('Up to date', 'amp-ok') + ' ' + esc(text + '; no restart needed.');
            }
        });
    }

    var waitingList = [];
    var provisionalList = [];
    var requestList = [];

    function renderStatus(view, overview, waiting, provisional, requests) {
        var cards = [
            ['Unmatched', overview.Unmatched, 'unmatched'],
            ['Waiting for review', overview.PendingReviews, 'review'],
            ['Blocks in force', overview.ActiveBlocks, 'blocks'],
            ['Waiting for a source', overview.Waiting, 'status:waiting'],
            ['Stand-in values', overview.Provisional, 'status:provisional'],
            ['Anime in titles dump', overview.Titles ? overview.Titles.AnimeCount : 0, 'titles']
        ];
        renderPluginState(view, overview.PluginVersion);
        view.querySelector('#ampCards').innerHTML = cards.map(function (card) {
            return '<div class="amp-card"' + (card[2] ? ' data-goto="' + card[2] + '" style="cursor:pointer"' : '') + '><b>' + esc(card[1] || 0) + '</b>' + esc(card[0]) + '</div>';
        }).join('');

        var rows = '<tr><th>Source</th><th>State</th><th>Mode</th><th>Last hour / day</th><th>Last test</th></tr>';
        overview.Sources.forEach(function (source) {
            var resting = source.RestUntilUtc && !source.Block;
            var state = !source.Enabled ? badge('Off')
                : source.Block ? badge('Blocked: ' + blockText(source.Block), 'amp-bad')
                : !source.Available ? badge(source.Unavailable || 'Unavailable', 'amp-warn')
                : resting ? badge('Resting until ' + when(source.RestUntilUtc), 'amp-warn')
                : badge('Ready', 'amp-ok');
            var test = source.LastTest ? esc(source.LastTest.Result) + ' <span class="amp-small">' + when(source.LastTest.AtUtc) + '</span>' : '';

            // One line per source: the caps follow the counts, and the burst shows on hover.
            var caps = source.CapPerHour && source.CapPerDay ? ' of ' + source.CapPerHour + ' / ' + source.CapPerDay
                : source.CapPerDay ? ' of ' + source.CapPerDay
                : source.CapPerHour ? ' of ' + source.CapPerHour + ' an hour' : '';
            var burst = source.BurstSize ? 'Burst ' + source.BurstSent + ' of ' + source.BurstSize + (resting ? ', resting until ' + when(source.RestUntilUtc) : '') : '';
            var counts = source.LastHour + ' / ' + source.LastDay + (caps ? ' <span class="amp-muted">' + esc(caps) + '</span>' : '');
            rows += '<tr><td>' + esc(source.Id) + '</td><td>' + state + '</td><td>' + esc(modeNames[enumName(source.Mode, modes)] || source.Mode) + '</td><td>'
                + (burst ? '<span class="amp-hint" title="' + esc(burst) + '">' + counts + '</span>' : counts) + '</td><td>' + test + '</td></tr>';
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
            + '<br/>AniBridge: ' + entries(a.AniBridgeEntryCount) + (a.AniBridgeCachedAtUtc ? ', cached ' + when(a.AniBridgeCachedAtUtc) : '')
            + '. Anime-Lists: ' + entries(a.AnimeListEntryCount) + (a.AnimeListCachedAtUtc ? ', cached ' + when(a.AnimeListCachedAtUtc) : '') + '.';

        view.querySelector('#ampCountWaiting').textContent = '(' + waiting.length + ')';
        view.querySelector('#ampCountProvisional').textContent = '(' + provisional.length + ')';
        view.querySelector('#ampCountRequests').textContent = '(' + requests.length + ')';

        [['Unmatched', overview.Unmatched], ['Review', overview.PendingReviews], ['Blocks', overview.ActiveBlocks]].forEach(function (c) {
            view.querySelector('#ampTabCount' + c[0]).textContent = c[1] > 0 ? c[1] : '';
        });

        waitingList = waiting;
        provisionalList = provisional;
        requestList = requests;
        renderWaiting(view);
        renderProvisional(view);
        renderRequests(view);
    }

    function renderWaiting(view) {
        var rows = pageRows(view, 'waiting', waitingList);
        view.querySelector('#ampWaiting').innerHTML = rows.length
            ? '<tr><th>Item</th><th>Waits for</th><th>Since</th></tr>' + rows.map(function (w) {
                return '<tr><td>' + esc(w.ItemName) + '</td><td>' + esc(w.Source) + ' (' + esc(w.Reason) + ')</td><td>' + when(w.AddedUtc) + '</td></tr>';
            }).join('')
            : '<tr><td class="amp-muted">Nothing is waiting.</td></tr>';
    }

    function renderProvisional(view) {
        var rows = pageRows(view, 'provisional', provisionalList);
        view.querySelector('#ampProvisional').innerHTML = rows.length
            ? '<tr><th>Item</th><th>Field</th><th>Supplied by</th><th>Instead of</th><th>Held by</th><th></th></tr>' + rows.map(function (p) {
                var held = p.HeldBy ? esc(p.HeldBy) + (p.HeldUntilUtc ? ' (until ' + when(p.HeldUntilUtc) + ')' : '') : '<span class="amp-muted">Nothing known; retried on the next queue run</span>';
                return '<tr><td>' + esc(p.ItemName) + '</td><td>' + esc(kindNames[p.Field] || p.Field) + '</td><td>' + esc(p.Source) + '</td><td>' + esc(p.PreferredSource) + '</td><td class="amp-small">' + held + '</td>'
                    + '<td><span class="amp-actions"><button is="emby-button" type="button" class="raised" data-standin-keep="' + esc(p.Id) + '"><span>Keep</span></button>'
                    + '<button is="emby-button" type="button" class="raised" data-standin-retry="' + esc(p.Id) + '"><span>Retry now</span></button></span></td></tr>';
            }).join('')
            : '<tr><td class="amp-muted">None.</td></tr>';
    }

    function renderRequests(view) {
        var rows = pageRows(view, 'requests', requestList);
        view.querySelector('#ampRequests').innerHTML = rows.length
            ? '<tr><th>When</th><th>Source</th><th>What</th><th>Result</th><th>ms</th></tr>' + rows.map(function (r) {
                var ok = r.Outcome === 'ok';
                return '<tr><td>' + when(r.AtUtc) + '</td><td>' + esc(r.Source) + '</td><td>' + esc(r.Target) + '</td><td>' + badge(r.Outcome + (r.HttpStatus ? ' ' + r.HttpStatus : ''), ok ? 'amp-ok' : 'amp-warn') + '</td><td>' + r.DurationMs + '</td></tr>';
            }).join('')
            : '<tr><td class="amp-muted">No requests yet.</td></tr>';
    }

    // Sources

    function renderSources(view, overview) {
        var rows = '<tr><th>Source</th><th>On</th><th>When blocked</th><th>Gap (ms)</th><th>Max / hour</th><th>Max / day</th><th>Burst</th><th>Rest (min)</th><th>In rating</th><th>Titles in</th></tr>';
        overview.Settings.forEach(function (s) {
            var mode = enumName(s.Mode, modes);
            rows += '<tr data-source="' + esc(s.Id) + '" data-limit="' + esc(s.DailyLimitGuess || 0) + '"><td>' + esc(s.Id) + '</td>'
                + '<td><input type="checkbox" data-field="Enabled"' + (s.Enabled ? ' checked' : '') + ' /></td>'
                + '<td><select data-field="Mode">' + modes.map(function (m) {
                    return '<option value="' + m + '"' + (m === mode ? ' selected' : '') + '>' + modeNames[m] + '</option>';
                }).join('') + '</select></td>'
                + '<td><input type="number" min="' + esc(s.MinimumRequestIntervalMs || 0) + '" step="50" data-field="RequestIntervalMs" title="At least ' + esc(s.MinimumRequestIntervalMs || 0) + ' ms" value="' + esc(s.RequestIntervalMs) + '" /></td>'
                + '<td><input type="number" min="0" data-field="MaxPerHour" value="' + esc(s.MaxPerHour || '') + '" placeholder="' + (s.DefaultMaxPerHour || 'none') + '" /></td>'
                + '<td><input type="number" min="0" data-field="MaxPerDay" value="' + esc(s.MaxPerDay || '') + '" placeholder="' + (s.DefaultMaxPerDay || 'none') + '" /></td>'
                + '<td><input type="number" min="0" data-field="BurstSize" value="' + esc(s.BurstSize || '') + '" placeholder="' + (s.DefaultBurstSize || 'none') + '" title="Requests sent in one go before a rest" /></td>'
                + '<td><input type="number" min="0" data-field="RestMinutes" value="' + esc(s.RestMinutes || '') + '" placeholder="' + (s.DefaultRestMinutes || 60) + '" title="Minutes to leave the source alone after a burst" /></td>'
                + '<td><input type="checkbox" data-field="IncludeInRating"' + (s.IncludeInRating ? ' checked' : '') + ' /></td>'
                + '<td><select data-field="TitlePreference"><option value="">Global</option>' + titlePreferences.map(function (t) {
                    return '<option value="' + t + '"' + (t === s.TitlePreference ? ' selected' : '') + '>' + t + '</option>';
                }).join('') + '</select></td></tr>';
        });
        view.querySelector('#ampSources').innerHTML = rows;
        renderEstimates(view);

        var defaults = {};
        (overview.DefaultPriorities || []).forEach(function (p) { defaults[p.Kind] = p.Sources; });

        var container = view.querySelector('#ampPriorities');
        container.innerHTML = overview.Priorities.map(function (p) {
            return '<div class="amp-order" data-kind="' + esc(p.Kind) + '" data-ids="' + esc(p.Sources) + '" data-default="' + esc(defaults[p.Kind] || '') + '">'
                + '<div class="amp-order-head"><b>' + esc(kindNames[p.Kind] || p.Kind) + '</b>'
                + (combinedKinds.indexOf(p.Kind) >= 0 ? badge('combined') : '')
                + (defaults[p.Kind] ? '<button type="button" class="amp-order-reset" data-reset="1" title="Back to the default order">' + icons.reset + 'Default</button>' : '')
                + '</div><div class="amp-order-chips"></div><div class="amp-order-spare"></div></div>';
        }).join('');

        Array.prototype.forEach.call(container.querySelectorAll('.amp-order'), renderOrder);
    }

    // Source order

    var combinedKinds = ['Genres', 'Rating', 'People'];

    function lucide(paths) {
        return '<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' + paths + '</svg>';
    }

    // Lucide chevron-left, chevron-right, x, plus, grip-vertical and rotate-ccw (ISC licence, lucide.dev).
    var icons = {
        left: lucide('<path d="m15 18-6-6 6-6"/>'),
        right: lucide('<path d="m9 18 6-6-6-6"/>'),
        x: lucide('<path d="M18 6 6 18"/><path d="m6 6 12 12"/>'),
        plus: lucide('<path d="M5 12h14"/><path d="M12 5v14"/>'),
        grip: lucide('<circle cx="9" cy="12" r="1"/><circle cx="9" cy="5" r="1"/><circle cx="9" cy="19" r="1"/><circle cx="15" cy="12" r="1"/><circle cx="15" cy="5" r="1"/><circle cx="15" cy="19" r="1"/>'),
        reset: lucide('<path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"/><path d="M3 3v5h5"/>')
    };

    function orderIds(row) {
        return (row.getAttribute('data-ids') || '').split(',').map(function (s) { return s.trim(); }).filter(function (id) {
            return sourceIds.indexOf(id) >= 0;
        }).filter(function (id, i, all) { return all.indexOf(id) === i; });
    }

    function setOrder(row, ids) {
        row.setAttribute('data-ids', ids.join(','));
        renderOrder(row);
    }

    function renderOrder(row) {
        var ids = orderIds(row);
        row.querySelector('.amp-order-chips').innerHTML = ids.map(function (id, i) {
            return '<span class="amp-chip" data-source="' + esc(id) + '">'
                + '<span class="amp-chip-grip">' + icons.grip + '</span><span class="amp-chip-pos">' + (i + 1) + '</span>' + esc(id)
                + '<button type="button" class="amp-chip-btn" data-move="-1" title="Ask earlier"' + (i === 0 ? ' disabled' : '') + '>' + icons.left + '</button>'
                + '<button type="button" class="amp-chip-btn" data-move="1" title="Ask later"' + (i === ids.length - 1 ? ' disabled' : '') + '>' + icons.right + '</button>'
                + '<button type="button" class="amp-chip-btn" data-remove="1" title="Take out"' + (ids.length === 1 ? ' disabled' : '') + '>' + icons.x + '</button></span>';
        }).join('');
        row.querySelector('.amp-order-spare').innerHTML = sourceIds.filter(function (id) { return ids.indexOf(id) < 0; }).map(function (id) {
            return '<button type="button" class="amp-chip-add" data-add="' + esc(id) + '">' + icons.plus + esc(id) + '</button>';
        }).join('');
    }

    function wireOrder(view) {
        var container = view.querySelector('#ampPriorities');
        var dragged = null;

        container.addEventListener('click', function (e) {
            var button = e.target.closest('button');
            var row = button && button.closest('.amp-order');
            if (!row || button.disabled) {
                return;
            }

            var ids = orderIds(row);
            var chip = button.closest('.amp-chip');
            var id = chip ? chip.getAttribute('data-source') : null;
            var at = id ? ids.indexOf(id) : -1;

            if (button.hasAttribute('data-move') && at >= 0) {
                var to = at + parseInt(button.getAttribute('data-move'), 10);
                ids.splice(to, 0, ids.splice(at, 1)[0]);
            } else if (button.hasAttribute('data-remove') && at >= 0 && ids.length > 1) {
                ids.splice(at, 1);
            } else if (button.hasAttribute('data-add')) {
                ids.push(button.getAttribute('data-add'));
            } else if (button.hasAttribute('data-reset')) {
                ids = (row.getAttribute('data-default') || '').split(',');
            } else {
                return;
            }

            setOrder(row, ids);
        });

        function clearMarks() {
            Array.prototype.forEach.call(container.querySelectorAll('.amp-drop-before, .amp-drop-after'), function (c) {
                c.classList.remove('amp-drop-before', 'amp-drop-after');
            });
        }

        // Pointer events rather than HTML drag and drop: the host pages cancel native drags, and
        // these work with touch as well.
        var drag = null;

        function targetAt(state, e) {
            var under = document.elementFromPoint(e.clientX, e.clientY);
            var chip = under && under.closest('.amp-chip');
            return chip && chip !== state.chip && chip.closest('.amp-order') === state.row ? chip : null;
        }

        function after(chip, e) {
            var box = chip.getBoundingClientRect();
            return e.clientX > box.left + box.width / 2;
        }

        container.addEventListener('pointerdown', function (e) {
            var chip = e.target.closest('.amp-chip');
            if (!chip || e.target.closest('button') || e.button > 0) {
                return;
            }

            drag = { chip: chip, row: chip.closest('.amp-order'), x: e.clientX, y: e.clientY, moving: false, pointer: e.pointerId };
        });

        container.addEventListener('pointermove', function (e) {
            if (!drag || e.pointerId !== drag.pointer) {
                return;
            }

            if (!drag.moving) {
                if (Math.abs(e.clientX - drag.x) + Math.abs(e.clientY - drag.y) < 6) {
                    return;
                }

                drag.moving = true;
                drag.chip.classList.add('amp-dragging');
                drag.chip.setPointerCapture(e.pointerId);
            }

            e.preventDefault();
            clearMarks();
            var chip = targetAt(drag, e);
            if (chip) {
                chip.classList.add(after(chip, e) ? 'amp-drop-after' : 'amp-drop-before');
            }
        });

        function endDrag(e, drop) {
            if (!drag || e.pointerId !== drag.pointer) {
                return;
            }

            var done = drag;
            drag = null;
            done.chip.classList.remove('amp-dragging');
            clearMarks();

            var chip = done.moving && drop ? targetAt(done, e) : null;
            if (!chip) {
                return;
            }

            var ids = orderIds(done.row);
            var moving = done.chip.getAttribute('data-source');
            ids.splice(ids.indexOf(moving), 1);
            var target = ids.indexOf(chip.getAttribute('data-source'));
            ids.splice(after(chip, e) ? target + 1 : target, 0, moving);
            setOrder(done.row, ids);
        }

        container.addEventListener('pointerup', function (e) { endDrag(e, true); });
        container.addEventListener('pointercancel', function (e) { endDrag(e, false); });
    }

    // The most requests a day a source's limits let through, for each source with a known
    // daily limit, worked out live as the fields change.
    function renderEstimates(view) {
        var lines = [];
        Array.prototype.forEach.call(view.querySelectorAll('#ampSources tr[data-source]'), function (row) {
            function value(name) {
                var input = row.querySelector('[data-field="' + name + '"]');
                return parseInt(input.value, 10) || parseInt(input.placeholder, 10) || 0;
            }

            var limit = parseInt(row.getAttribute('data-limit'), 10) || 0;
            if (!limit) {
                return;
            }

            var gap = Math.max(value('RequestIntervalMs'), parseInt(row.querySelector('[data-field="RequestIntervalMs"]').min, 10) || 0);
            var burst = value('BurstSize');
            var rest = value('RestMinutes');
            var perDay = value('MaxPerDay');
            var perHour = value('MaxPerHour');

            var most = Math.floor(86400000 / Math.max(gap, 1));
            if (burst > 0) {
                var cycle = burst * gap / 60000 + rest;
                most = Math.min(most, Math.ceil(1440 / cycle) * burst);
            }

            if (perHour > 0) {
                most = Math.min(most, perHour * 24);
            }

            if (perDay > 0) {
                most = Math.min(most, perDay);
            }

            var source = row.getAttribute('data-source');
            var text = source + ': at most about ' + most + ' requests a day with these settings.';
            lines.push(most > limit
                ? badge('Over ' + limit + ' a day', 'amp-bad') + ' ' + esc(text + ' That is past the ' + limit + ' a day ' + source + ' is believed to ban for, so this IP may get banned.')
                : perDay > limit
                    ? badge('Day cap over ' + limit, 'amp-warn') + ' ' + esc(text + ' The bursts keep it there, but the day cap of ' + perDay + ' is past the ' + limit + ' ' + source + ' is believed to ban for, so a shorter rest or a bigger burst could get this IP banned.')
                    : badge('Under ' + limit + ' a day', 'amp-ok') + ' ' + esc(text));
            lines.push('<span class="amp-muted amp-small">' + esc('The ' + limit + ' a day is a guess, not a published limit: a ban came at exactly ' + limit + ' requests in a day (17 Oct 2026). Whether bursts with rests change it is not known. A ban that comes after ' + limit + ' requests in 24 hours pauses ' + source + ' for a full day.') + '</span>');
        });

        view.querySelector('#ampSourceEstimates').innerHTML = lines.join('<br/>');
    }

    function saveSources(view) {
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
                    BurstSize: parseInt(field('BurstSize').value, 10) || 0,
                    RestMinutes: parseInt(field('RestMinutes').value, 10) || 0,
                    IncludeInRating: field('IncludeInRating').checked,
                    TitlePreference: field('TitlePreference').value
                };
            });
            config.Priorities = Array.prototype.map.call(view.querySelectorAll('#ampPriorities .amp-order[data-kind]'), function (row) {
                return { Kind: row.getAttribute('data-kind'), Sources: orderIds(row).join(',') };
            });
            config.GenreRebuildDaily = view.querySelector('#ampGenreRebuildDaily').checked;
            config.GenreRebuildAskOthers = view.querySelector('#ampGenreRebuildAsk').checked;
        });
    }

    // Genres and tags rebuilt from the cache

    var genreRebuild = { watching: false };

    function renderGenreRebuild(view, config) {
        view.querySelector('#ampGenreRebuildDaily').checked = config.GenreRebuildDaily !== false;
        view.querySelector('#ampGenreRebuildAsk').checked = config.GenreRebuildAskOthers !== false;
    }

    function showGenreRebuild(view, s) {
        view.querySelector('#ampGenreRebuild').disabled = !!s.Running;

        var line = view.querySelector('#ampGenreRebuildResult');
        if (!s.Running && !when(s.StartedUtc)) {
            line.textContent = '';
            return;
        }

        line.textContent = (s.Running ? 'Running, ' + s.Checked + ' of ' + s.Total + ' looked at. ' : 'Last run ' + when(s.FinishedUtc || s.StartedUtc) + '. ')
            + 'Updated ' + s.Updated + ' of ' + s.Checked + ', unchanged ' + s.Unchanged + ', skipped ' + s.Skipped + ' (locked), '
            + s.Missing + ' without cached data, ' + s.Requests + ' requests.' + (s.LastError ? ' Last error: ' + s.LastError : '');
    }

    // The rebuild runs on the server in the background; this follows it until it ends.
    function watchGenreRebuild(view) {
        genreRebuild.watching = true;
        get('GenreRebuild').then(function (s) {
            showGenreRebuild(view, s);

            if (s.Running && document.body.contains(view)) {
                setTimeout(function () { watchGenreRebuild(view); }, 2000);
                return;
            }

            genreRebuild.watching = false;
        }, function () {
            genreRebuild.watching = false;
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

    // What a search found for each item, kept across redraws of the list: searching a long list
    // takes a while, and a reload must not throw the results away.
    var searchedGuesses = {};

    var unmatchedList = [];

    function unmatchedRow(u) {
        var guesses = (u.Candidates || []).map(function (c) {
            return '<option value="' + esc(c.AnimeId) + '">' + esc(c.Title) + ' (' + esc(c.AnimeId) + ', ' + c.Score + '%)</option>';
        }).join('');
        var options = searchedGuesses[u.Id] || '<option value="">Guesses from the titles dump...</option>' + guesses;
        return '<tr data-key="' + esc(u.Id) + '">'
            + '<td><b>' + esc(u.Name) + '</b> ' + (u.Year ? '(' + u.Year + ') ' : '') + (u.Ignored ? badge('set aside') : '')
            + '<div class="amp-small amp-path">' + esc(u.Path) + '</div></td>'
            + '<td class="amp-small">' + u.Attempts + 'x<br/>' + when(u.LastAttemptUtc) + '</td>'
            + '<td><select class="amp-guess">' + options + '</select>'
            + '<input type="text" class="amp-search" placeholder="Search the titles dump (Enter)" value="' + esc(u.Name) + '" />'
            + '<input type="text" class="amp-id" placeholder="AniDB, AniList or MyAnimeList id or link" /></td>'
            + '<td><div class="amp-unmatched-acts"><button type="button" is="emby-button" class="raised button-submit" data-act="mapnow">Map now</button>'
            + '<button type="button" is="emby-button" class="raised" data-act="map">Map</button>'
            + '<button type="button" is="emby-button" class="raised" data-act="online" title="Search the titles dump, AniList and MyAnimeList">Search online</button>'
            + '<button type="button" is="emby-button" class="raised" data-act="ignore">' + (u.Ignored ? 'Bring back' : 'Set aside') + '</button></div></td>'
            + '</tr>';
    }

    var unmatchedSort = null;

    var unmatchedColumns = [
        ['item', 'Item', function (u) { return (u.Name || '').toLowerCase(); }],
        ['tried', 'Tried', function (u) { return new Date(u.LastAttemptUtc).getTime() || 0; }]
    ];

    // The items the filter box lets through, in the order the headers say (the server's until one is clicked).
    function shownUnmatched(view) {
        var text = view.querySelector('#ampUnmatchedFilter').value.trim().toLowerCase();
        var shown = unmatchedList.filter(function (u) {
            return !text || [u.Name, u.Path].join(' ').toLowerCase().indexOf(text) >= 0;
        });
        if (!unmatchedSort) {
            return shown;
        }

        var column = unmatchedColumns.filter(function (c) { return c[0] === unmatchedSort.key; })[0];
        return shown.sort(function (a, b) {
            var x = column[2](a), y = column[2](b);
            var order = x < y ? -1 : x > y ? 1 : 0;
            if (!order && unmatchedSort.key === 'tried') {
                order = (a.Attempts || 0) - (b.Attempts || 0);
            }

            return order * unmatchedSort.dir;
        });
    }

    // The clickable header cell of a sortable column, with its arrow.
    function sortHeader(sort, column) {
        var arrow = sort && column[0] === sort.key ? (sort.dir < 0 ? ' &#9660;' : ' &#9650;') : '';
        return '<th data-sort="' + column[0] + '" style="cursor:pointer" title="Sort">' + column[1] + arrow + '</th>';
    }

    function renderUnmatchedCount(view, shown) {
        view.querySelector('#ampUnmatchedCount').textContent = unmatchedList.length ? shown.length + ' of ' + unmatchedList.length : '';
        view.querySelector('#ampCountUnmatched').textContent = '(' + unmatchedList.length + ')';
    }

    function renderUnmatched(view) {
        var shown = shownUnmatched(view);
        var rows = pageRows(view, 'unmatched', shown);
        renderUnmatchedCount(view, shown);

        // One table, so every item's fields and buttons line up with the next one's.
        view.querySelector('#ampUnmatched').innerHTML = rows.length
            ? '<div class="amp-scroll"><table class="amp-table amp-unmatched"><tr>' + unmatchedColumns.map(function (c) { return sortHeader(unmatchedSort, c); }).join('') + '<th>Map to</th><th></th></tr>' + rows.map(unmatchedRow).join('') + '</table></div>'
            : '<p class="amp-muted">' + (unmatchedList.length ? 'No items match the filter.' : 'Everything is identified.') + '</p>';
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
            var ignore = button.textContent !== 'Bring back';
            post('Unmatched/Ignore', { ItemKey: key, Ignore: ignore }).then(function () {
                // Only this card changes; the rest keep what their searches found.
                if (ignore && !view.querySelector('#ampShowIgnored').checked) {
                    removeCard(view, card);
                } else {
                    button.textContent = ignore ? 'Bring back' : 'Set aside';
                }
            }, fail);
            return;
        }

        if (act === 'online') {
            searchCard(card).then(null, fail);
            return;
        }

        var id = card.querySelector('.amp-id').value.trim() || card.querySelector('.amp-guess').value;
        if (!id) {
            notify('Pick a guess or enter an id first');
            return;
        }

        post('Unmatched/Map', null, { ItemKey: key, AnimeId: id, ApplyNow: act === 'mapnow' }).then(function (mapped) {
            var to = String((mapped && mapped.AnimeId) || mapped || id);
            notify('Mapped to ' + (/^anilist:/.test(to) ? 'AniList ' + to.slice(8) : /^mal:/.test(to) ? 'MyAnimeList ' + to.slice(4) : 'AniDB ' + to) + (act === 'mapnow' ? '; refreshing' : '; applies on the next queue run'));

            // A mapped item leaves the list for the Mapping tab; the other cards and what their
            // searches found stay as they are.
            removeCard(view, card);
        }, fail);
    }

    function removeCard(view, card) {
        var key = card.getAttribute('data-key');
        delete searchedGuesses[key];
        unmatchedList = unmatchedList.filter(function (u) { return u.Id !== key; });
        card.remove();

        // The other rows stay as they are, typed text included; only an emptied page is redrawn.
        var cards = view.querySelectorAll('#ampUnmatched [data-key]').length;
        if (!cards) {
            renderUnmatched(view);
            return;
        }

        var shown = shownUnmatched(view);
        var start = pageStart(view, 'unmatched', shown.length);
        renderUnmatchedCount(view, shown);

        // The next item moves up into the gap, so paging on does not skip it.
        var missing = shown.slice(start + cards, start + pagerState('unmatched').size);
        if (missing.length) {
            view.querySelector('#ampUnmatched tr').parentNode.insertAdjacentHTML('beforeend', missing.map(unmatchedRow).join(''));
        }
    }

    // Searches the titles dump, AniList and MyAnimeList for one card and fills its guesses. A
    // result AniList or MyAnimeList ties to AniDB maps to that AniDB entry; one they do not is
    // mapped to itself, and the item is filled from AniList and MyAnimeList.
    function searchCard(card) {
        var select = card.querySelector('.amp-guess');
        var query = card.querySelector('.amp-search').value.trim();
        select.innerHTML = '<option value="">Searching the titles dump, AniList and MyAnimeList...</option>';

        return Promise.all([get('Titles/Search', { Query: query }), get('Titles/SearchOnline', { Query: query })]).then(function (r) {
            var local = r[0].map(function (c) {
                return '<option value="' + esc(c.AnimeId) + '">' + esc('AniDB: ' + c.Title + ' (' + c.AnimeId + ', ' + c.Score + '%)') + '</option>';
            });
            var online = r[1].map(function (c) {
                var value = c.AniDbId || (c.Source === 'AniList' ? 'anilist:' : 'mal:') + c.Id;
                var details = [c.Year, c.Format].filter(function (x) { return x; }).join(', ');
                return '<option value="' + esc(value) + '">' + esc(c.Source + ': ' + c.Title + (details ? ' (' + details + ')' : '')
                    + (c.AniDbId ? ' = AniDB ' + c.AniDbId : ' - not on AniDB')) + '</option>';
            });
            select.innerHTML = '<option value="">' + (local.length + online.length) + ' found (' + local.length + ' in the titles dump, ' + online.length + ' on AniList and MyAnimeList)...</option>'
                + local.join('') + online.join('');
            searchedGuesses[card.getAttribute('data-key')] = select.innerHTML;

            return local.length + online.length;
        });
    }

    // One card after another: AniList and MyAnimeList are asked through their request gaps, and
    // a page firing every search at once would only queue them up behind each other.
    function searchAllUnmatched(view) {
        var cards = Array.prototype.slice.call(view.querySelectorAll('#ampUnmatched [data-key]'));
        var button = view.querySelector('#ampSearchAllUnmatched');
        var status = view.querySelector('#ampSearchAllResult');
        var withResults = 0;

        if (!cards.length) {
            status.innerHTML = 'Nothing on this page to search.';
            return;
        }

        button.disabled = true;

        return cards.reduce(function (chain, card, index) {
            return chain.then(function () {
                status.innerHTML = 'Searching ' + (index + 1) + ' of ' + cards.length + '...';
                return searchCard(card).then(function (count) {
                    withResults += count ? 1 : 0;
                }, function () { /* one failed search does not stop the rest */ });
            });
        }, Promise.resolve()).then(function () {
            button.disabled = false;
            status.innerHTML = 'Searched ' + cards.length + ' items on this page; ' + withResults + ' have results to pick from.';
        });
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
            searchedGuesses[input.closest('[data-key]').getAttribute('data-key')] = select.innerHTML;
        }, fail);
    }

    // Review

    var reviewList = [];
    var reviewSort = null;

    function reviewField(r) {
        return kindNames[r.Field] || r.Field;
    }

    var reviewColumns = [
        ['item', 'Item', function (r) { return (r.ItemName || '').toLowerCase(); }],
        ['field', 'Field', function (r) { return reviewField(r).toLowerCase(); }],
        ['now', 'Now', function (r) { return ((r.CurrentSource || '') + ' ' + (r.CurrentValue || '')).toLowerCase(); }],
        ['proposed', 'Proposed', function (r) { return ((r.ProposedSource || '') + ' ' + (r.ProposedValue || '')).toLowerCase(); }]
    ];

    // The reviews the filters let through, in the order the headers say (the server's until one is clicked).
    function shownReviews(view) {
        var field = view.querySelector('#ampReviewFilterField').value;
        var text = view.querySelector('#ampReviewFilterText').value.trim().toLowerCase();
        var shown = reviewList.filter(function (r) {
            return (!field || reviewField(r) === field)
                && (!text || [r.ItemName, r.CurrentValue, r.ProposedValue].join(' ').toLowerCase().indexOf(text) >= 0);
        });
        if (!reviewSort) {
            return shown;
        }

        var column = reviewColumns.filter(function (c) { return c[0] === reviewSort.key; })[0];
        return shown.sort(function (a, b) {
            var x = column[2](a), y = column[2](b);
            return (x < y ? -1 : x > y ? 1 : 0) * reviewSort.dir;
        });
    }

    function renderReviews(view) {
        fillFilter(view.querySelector('#ampReviewFilterField'), 'Field', distinct(reviewList, reviewField));
        var shown = shownReviews(view);
        var rows = pageRows(view, 'review', shown);
        view.querySelector('#ampReviewCount').textContent = reviewList.length ? shown.length + ' of ' + reviewList.length : '';

        Object.keys(reviewDecisions).forEach(function (d) {
            var button = view.querySelector('[data-decide-all="' + d + '"]');
            button.disabled = !shown.length;
            button.querySelector('span').textContent = reviewDecisions[d] + ' all matching (' + shown.length + ')';
        });

        // One table, so every review's columns line up with the next one's.
        view.querySelector('#ampReviews').innerHTML = rows.length
            ? '<div class="amp-scroll"><table class="amp-table amp-reviews"><tr>' + reviewColumns.map(function (c) { return sortHeader(reviewSort, c); }).join('') + '<th></th></tr>' + rows.map(function (r) {
                return '<tr data-review="' + esc(r.Id) + '"><td><b>' + esc(r.ItemName) + '</b></td><td>' + esc(reviewField(r)) + '</td>'
                    + '<td>' + esc(r.CurrentValue) + '<div class="amp-small">from ' + esc(r.CurrentSource) + '</div></td>'
                    + '<td>' + esc(r.ProposedValue) + '<div class="amp-small">from ' + esc(r.ProposedSource) + '</div></td>'
                    + '<td class="amp-actions">' + Object.keys(reviewDecisions).map(function (d) {
                        return '<button type="button" is="emby-button" class="raised' + (d === 'Accepted' ? ' button-submit' : '') + '" data-decide="' + d + '">' + reviewDecisions[d] + '</button>';
                    }).join('') + '</td></tr>';
            }).join('') + '</table></div>'
            : '<p class="amp-muted">' + (reviewList.length ? 'No reviews match the filters.' : 'Nothing to review.') + '</p>';
    }

    var reviewConfirms = {
        Accepted: 'Accept the {n} reviews matching the filters? Their items are not refreshed now; each takes the accepted value at its next refresh.',
        Kept: 'Keep the current value for the {n} reviews matching the filters?',
        Locked: 'Lock the current value for good for the {n} reviews matching the filters?'
    };

    function decideShown(view, decision) {
        var ids = shownReviews(view).map(function (r) { return r.Id; });
        if (!ids.length || !window.confirm(reviewConfirms[decision].replace('{n}', ids.length))) {
            return;
        }

        post('Reviews/DecideMany', null, { Ids: ids, Decision: decision }).then(function (r) {
            notify('Decided ' + (r && r.Decided !== undefined ? r.Decided : ids.length) + ' reviews');
            loadAll(view);
        }, fail);
    }

    // Blocks

    var blockList = [];
    var blockSort = { key: 'seen', dir: -1 };

    var blockColumns = [
        ['source', 'Source', function (b) { return (b.Source || '').toLowerCase(); }],
        ['item', 'Item', function (b) { return (b.ItemName || b.ItemKey || '').toLowerCase(); }],
        ['kind', 'Kind', function (b) { return (b.Kind || 'all').toLowerCase(); }],
        ['why', 'Why', function (b) { return blockReason(b); }],
        ['seen', 'Seen', function (b) { return new Date(b.LastSeenUtc).getTime() || 0; }]
    ];

    function blockReason(block) {
        return enumName(block.Reason, ['Banned', 'RateLimited', 'Unavailable', 'ServerError', 'AuthFailed', 'ClientRejected', 'NotFound', 'ParseFailed', 'Empty', 'Manual', 'Other']);
    }

    function fillFilter(select, label, values) {
        var current = select.value;
        select.innerHTML = '<option value="">' + label + ': all</option>' + values.map(function (v) {
            return '<option value="' + esc(v) + '">' + esc(v) + '</option>';
        }).join('');
        select.value = values.indexOf(current) >= 0 ? current : '';
    }

    function distinct(blocks, read) {
        var seen = {};
        blocks.forEach(function (b) { seen[read(b)] = true; });
        return Object.keys(seen).sort();
    }

    // The blocks the filters let through, in the order the headers say.
    function shownBlocks(view) {
        var source = view.querySelector('#ampBlockFilterSource').value;
        var kind = view.querySelector('#ampBlockFilterKind').value;
        var reason = view.querySelector('#ampBlockFilterReason').value;
        var text = view.querySelector('#ampBlockFilterText').value.trim().toLowerCase();
        var column = blockColumns.filter(function (c) { return c[0] === blockSort.key; })[0];

        return blockList.filter(function (b) {
            return (!source || b.Source === source)
                && (!kind || (b.Kind || 'all') === kind)
                && (!reason || blockReason(b) === reason)
                && (!text || [b.ItemName, b.ItemKey, b.Message].join(' ').toLowerCase().indexOf(text) >= 0);
        }).sort(function (a, b) {
            var x = column[2](a), y = column[2](b);
            return (x < y ? -1 : x > y ? 1 : 0) * blockSort.dir;
        });
    }

    function renderBlocks(view, blocks) {
        blockList = blocks;
        fillFilter(view.querySelector('#ampBlockFilterSource'), 'Source', distinct(blocks, function (b) { return b.Source; }));
        fillFilter(view.querySelector('#ampBlockFilterKind'), 'Kind', distinct(blocks, function (b) { return b.Kind || 'all'; }));
        fillFilter(view.querySelector('#ampBlockFilterReason'), 'Why', distinct(blocks, blockReason));
        renderBlockRows(view);

        view.querySelector('#ampBlockSource').innerHTML = sourceIds.map(function (s) {
            return '<option value="' + s + '">' + s + '</option>';
        }).join('');
    }

    function renderBlockRows(view) {
        var blocks = shownBlocks(view);
        var rows = pageRows(view, 'blocks', blocks);
        view.querySelector('#ampBlockCount').textContent = blockList.length ? blocks.length + ' of ' + blockList.length : '';
        var liftable = blocks.filter(function (b) { return !b.Pinned; }).length;
        var liftButton = view.querySelector('#ampLiftShown');
        liftButton.disabled = !liftable;
        liftButton.querySelector('span').textContent = 'Lift all matching (' + liftable + ')';
        liftButton.title = liftable < blocks.length ? 'Pinned blocks are left alone; lift those one at a time.' : '';

        view.querySelector('#ampBlocks').innerHTML = rows.length
            ? '<tr>' + blockColumns.map(function (c) { return sortHeader(blockSort, c); }).join('') + '<th></th></tr>' + rows.map(function (b) {
                var scope = enumName(b.Scope, ['Source', 'Item']);
                return '<tr><td>' + esc(b.Source) + '</td><td>' + (scope === 'Source' ? badge('whole source', 'amp-bad') : esc(b.ItemName || b.ItemKey)) + '</td><td>' + esc(b.Kind || 'all') + '</td>'
                    + '<td>' + esc(blockText(b)) + '<div class="amp-small">' + esc(b.Message) + (b.HttpStatus ? ' (HTTP ' + b.HttpStatus + ')' : '') + '</div></td>'
                    + '<td>' + b.Count + 'x, last ' + when(b.LastSeenUtc) + '</td>'
                    + '<td class="amp-actions"><button type="button" is="emby-button" class="raised" data-lift="' + esc(b.Id) + '">Lift</button>'
                    + (b.Pinned ? badge('pinned') : '<button type="button" is="emby-button" class="raised" data-pin="' + esc(b.Id) + '">Pin</button>') + '</td></tr>';
            }).join('')
            : '<tr><td class="amp-muted">' + (blockList.length ? 'No blocks match the filters.' : 'No blocks.') + '</td></tr>';
    }

    function liftShown(view) {
        var blocks = shownBlocks(view);
        var ids = blocks.filter(function (b) { return !b.Pinned; }).map(function (b) { return b.Id; });
        var pinned = blocks.length - ids.length;

        if (!ids.length || !window.confirm('Lift the ' + ids.length + ' blocks matching the filters?' + (pinned ? ' Pinned blocks stay (' + pinned + ').' : '')
            + " Their items are not refreshed now; each is asked again at its next refresh, AniDB included where a block was AniDB's.")) {
            return;
        }

        post('Blocks/LiftMany', null, { Ids: ids }).then(function (r) { notify('Lifted ' + (r && r.Lifted !== undefined ? r.Lifted : ids.length)); loadAll(view); }, fail);
    }

    // Mapping

    var mapList = [];
    var mapSort = { key: 'status', dir: 1 };
    var mapEdit = null;
    var mapStatusOrder = ['Corrected', 'Suspect', 'Manual', 'Verified', ''];

    // Key, header, keys the id may be stored under, site link.
    var mapIds = [
        ['anidb', 'AniDB', ['AniDB'], 'https://anidb.net/anime/'],
        ['mal', 'MyAnimeList', ['MyAnimeList', 'Mal'], 'https://myanimelist.net/anime/'],
        ['anilist', 'AniList', ['AniList'], 'https://anilist.co/anime/'],
        ['tvdb', 'TVDB', ['Tvdb', 'TVDB'], 'https://thetvdb.com/dereferrer/series/'],
        ['tmdb', 'TMDB', ['Tmdb', 'TMDB'], 'https://www.themoviedb.org/tv/']
    ];

    function mapId(ids, keys) {
        for (var i = 0; i < keys.length; i++) {
            if (ids && ids[keys[i]] !== undefined && ids[keys[i]] !== null && ids[keys[i]] !== '') {
                return String(ids[keys[i]]);
            }
        }

        return '';
    }

    function mapStatus(record) {
        return record.Status || '';
    }

    function mapName(record) {
        return (record.Name || record.Path || '').toLowerCase();
    }

    var mapColumns = [['show', 'Show', mapName]].concat(mapIds.map(function (def) {
        return [def[0], def[1], function (r) { return parseInt(mapId(r.Ids, def[2]), 10) || 0; }];
    })).concat([['status', 'Status', function (r) {
        var index = mapStatusOrder.indexOf(mapStatus(r));
        return index < 0 ? mapStatusOrder.length : index;
    }]]);

    // The records the filters let through, in the order the headers say, the name breaking ties.
    function shownMapping(view) {
        var status = view.querySelector('#ampMapFilterStatus').value;
        var text = view.querySelector('#ampMapFilterText').value.trim().toLowerCase();
        var column = mapColumns.filter(function (c) { return c[0] === mapSort.key; })[0];

        return mapList.filter(function (r) {
            var own = mapStatus(r);

            // A hand mapping with no show in the list has no check to be missing.
            if (status === 'attention' ? ['Corrected', 'Suspect'].indexOf(own) < 0
                : status === 'none' ? own !== '' || !r.Path
                : status === 'hand' ? !r.HandMapping
                : status !== 'all' && own !== status) {
                return false;
            }

            return !text || [r.Name, r.Path].concat(mapIds.map(function (def) { return mapId(r.Ids, def[2]); })).join(' ').toLowerCase().indexOf(text) >= 0;
        }).sort(function (a, b) {
            var x = column[2](a), y = column[2](b);
            var order = (x < y ? -1 : x > y ? 1 : 0) * mapSort.dir;
            return order || (mapName(a) < mapName(b) ? -1 : mapName(a) > mapName(b) ? 1 : 0);
        });
    }

    function mapLink(def, id) {
        return '<a href="' + esc(def[3] + encodeURIComponent(id)) + '" target="_blank" rel="noopener">' + esc(id) + '</a>';
    }

    function mapCell(record, def) {
        var now = mapId(record.Ids, def[2]);
        var status = mapStatus(record);
        var html = now ? mapLink(def, now) : '<span class="amp-muted">-</span>';

        if (status === 'Corrected') {
            var old = mapId(record.OldIds, def[2]);
            if (old && old !== now) {
                html += '<div><s class="amp-small">' + esc(old) + '</s></div>';
            }
        }

        if (status === 'Suspect' && def[0] === 'anidb') {
            [['ByTvdb', 'TVDB says'], ['ByFolder', 'folder says']].forEach(function (hint) {
                var suggested = mapId(record.NewIds, [hint[0]]);
                if (suggested) {
                    html += '<div class="amp-small">' + hint[1] + ' ' + mapLink(def, suggested) + '</div>';
                }
            });
        }

        return '<td>' + html + '</td>';
    }

    function mapButton(act, label, submit) {
        return '<button type="button" is="emby-button" class="raised' + (submit ? ' button-submit' : '') + '" data-map-act="' + act + '">' + label + '</button>';
    }

    function mapRow(record) {
        var status = mapStatus(record);
        var kind = status === 'Corrected' ? 'amp-warn' : status === 'Suspect' ? 'amp-bad' : status === 'Verified' ? 'amp-ok' : '';

        // A hand mapping no listed show matches has no folder: it is keyed by its mapping, and
        // only its removal makes sense.
        var actions = record.Path
            ? (status === 'Corrected' || status === 'Suspect' || status === '' ? mapButton('verify', 'Verify', status === 'Suspect') : '')
                + (status === 'Corrected' ? mapButton('revert', 'Revert') : '')
                + mapButton('edit', 'Set manually')
                + (status === 'Manual' ? mapButton('lift', 'Lift manual') : '')
            : '';

        if (record.HandMapping) {
            actions += mapButton('unmap', 'Remove hand mapping');
        }

        return '<tr ' + (record.Path ? 'data-path="' + esc(record.Path) : 'data-key="' + esc(record.MappingKey)) + '"><td><b>' + esc(record.Name || record.Path || record.MappingKey) + '</b>'
            + '<div class="amp-small amp-path">' + (record.Path ? esc(record.Path) : '<span class="amp-muted">Not in the library list</span>') + '</div></td>'
            + mapIds.map(function (def) { return mapCell(record, def); }).join('')
            + '<td>' + (status ? badge(status, kind) : '<span class="amp-muted">-</span>')
            + (record.HandMapping ? ' ' + badge('mapped by hand') : '')
            + (record.Reason && (status === 'Corrected' || status === 'Suspect') ? '<div class="amp-small">' + esc(record.Reason) + '</div>' : '') + '</td>'
            + '<td><div class="amp-map-acts">' + actions + '</div></td></tr>';
    }

    function mapEditRow() {
        function field(name, label) {
            return '<label class="amp-small">' + label + '<input type="text" data-map-field="' + name + '" value="' + esc(mapEdit[name]) + '" /></label>';
        }

        return '<tr class="amp-map-edit" data-path="' + esc(mapEdit.path) + '"><td colspan="' + (mapIds.length + 3) + '"><div class="amp-map-form">'
            + field('AniDb', 'AniDB id') + field('Mal', 'MyAnimeList id') + field('AniList', 'AniList id')
            + mapButton('save', 'Save', true) + mapButton('cancel', 'Cancel')
            + '<span class="amp-small amp-muted">An AniDB id left empty is looked up from the MyAnimeList or AniList id; where there is none, the show is filled from those sites alone.</span>'
            + '</div></td></tr>';
    }

    function renderMapping(view) {
        var shown = shownMapping(view);
        var rows = pageRows(view, 'mapping', shown);

        view.querySelector('#ampMapCount').textContent = mapList.length ? shown.length + ' of ' + mapList.length : '';

        view.querySelector('#ampMapping').innerHTML = rows.length
            ? '<tr>' + mapColumns.map(function (c) {
                var arrow = c[0] === mapSort.key ? (mapSort.dir < 0 ? ' &#9660;' : ' &#9650;') : '';
                return '<th data-sort="' + c[0] + '" style="cursor:pointer" title="Sort">' + c[1] + arrow + '</th>';
            }).join('') + '<th></th></tr>' + rows.map(function (r) {
                return mapRow(r) + (mapEdit && mapEdit.path === r.Path ? mapEditRow() : '');
            }).join('')
            : '<tr><td class="amp-muted">' + (mapList.length ? 'No shows match the filters.' : 'No identified shows.') + '</td></tr>';
    }

    function loadMapping(view) {
        host.showLoading();
        return get('IdRecords').then(function (records) {
            mapList = records || [];
            renderMapping(view);
            host.hideLoading();
        }, function (error) {
            view.querySelector('#ampMapping').innerHTML = '<tr><td class="amp-muted">Could not load.</td></tr>';
            fail(error);
        });
    }

    function mapAct(view, endpoint, body, message) {
        post('IdRecords/' + endpoint, null, body).then(function () {
            notify(message);
            loadMapping(view);
        }, fail);
    }

    function onMappingClick(view, e) {
        var header = e.target.closest('[data-sort]');
        if (header) {
            var key = header.getAttribute('data-sort');
            mapSort = { key: key, dir: mapSort.key === key ? -mapSort.dir : 1 };
            resetPage('mapping');
            renderMapping(view);
            return;
        }

        var button = e.target.closest('button[data-map-act]');
        if (!button) {
            return;
        }

        var row = button.closest('tr[data-path], tr[data-key]');
        var path = row.getAttribute('data-path');
        var act = button.getAttribute('data-map-act');
        var record = path === null ? mapList.filter(function (r) { return !r.Path && r.MappingKey === row.getAttribute('data-key'); })[0]
            : mapList.filter(function (r) { return r.Path === path; })[0];

        if (act === 'unmap' && record) {
            if (window.confirm('Remove the hand mapping of ' + (record.Name || record.MappingKey) + '? The show keeps the ids it has until the plugin identifies it again.')) {
                ApiClient.ajax({ type: 'DELETE', url: ApiClient.getUrl('AniMetaPlus/Mappings', { ItemKey: record.MappingKey }) }).then(function () {
                    notify('Hand mapping removed');
                    loadMapping(view);
                }, fail);
            }
        } else if (path === null) {
            return;
        } else if (act === 'verify') {
            mapAct(view, 'Verify', { Path: path }, 'Verified');
        } else if (act === 'revert') {
            if (window.confirm('Put back the ids this show had before the correction and keep them by hand? The show is refreshed.')) {
                mapAct(view, 'Revert', { Path: path }, 'Reverted; refreshing');
            }
        } else if (act === 'lift') {
            if (window.confirm('Lift the manual mark? The ids stay, but the plugin may correct them again on its next check.')) {
                mapAct(view, 'Lift', { Path: path }, 'Lifted');
            }
        } else if (act === 'edit' && record) {
            mapEdit = {
                path: path,
                AniDb: mapId(record.Ids, mapIds[0][2]),
                Mal: mapId(record.Ids, mapIds[1][2]),
                AniList: mapId(record.Ids, mapIds[2][2])
            };
            renderMapping(view);
        } else if (act === 'cancel') {
            mapEdit = null;
            renderMapping(view);
        } else if (act === 'save' && mapEdit) {
            var aniDb = mapEdit.AniDb.trim();
            var malId = mapEdit.Mal.trim();
            var aniListId = mapEdit.AniList.trim();
            if (!aniDb && !malId && !aniListId) {
                notify('At least one id is required');
                return;
            }

            if (aniDb && !/^[0-9]+$/.test(aniDb)) {
                notify('The AniDB id must be digits');
                return;
            }

            if (malId && !/^[0-9]+$/.test(malId)) {
                notify('The MyAnimeList id must be digits');
                return;
            }

            if (aniListId && !/^[0-9]+$/.test(aniListId)) {
                notify('The AniList id must be digits');
                return;
            }

            var edit = mapEdit;
            mapEdit = null;
            mapAct(view, 'Manual', { Path: path, AniDb: aniDb, Mal: malId, AniList: aniListId }, 'Saved; refreshing');
        }
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

    // A setting with data-needs="<checkbox id>" is greyed out and disabled while that box is off;
    // its value stays and is still saved, since the save reads the inputs directly.
    function applyNeeds(view) {
        Array.prototype.forEach.call(view.querySelectorAll('[data-needs]'), function (box) {
            var parent = view.querySelector('#' + box.getAttribute('data-needs'));
            var off = !!parent && !parent.checked;
            box.classList.toggle('amp-off', off);
            Array.prototype.forEach.call(box.querySelectorAll('input, select'), function (input) {
                input.disabled = off;
            });
        });
    }

    // The save bar must hide what scrolls under it, so it takes the page's own background.
    function tintSaveBar(view) {
        var bar = view.querySelector('#ampSaveBar');
        var color = '';
        for (var el = view; el && el.nodeType === 1; el = el.parentElement) {
            var c = window.getComputedStyle(el).backgroundColor;
            if (c && c !== 'transparent' && c !== 'rgba(0, 0, 0, 0)') {
                color = c;
                break;
            }
        }

        bar.style.backgroundColor = color;
    }

    // position: sticky fails under Jellyfin's scrolling ancestors, so the bar is pinned with fixed
    // positioning while its natural spot (the sentinel) is below the viewport.
    var saveBarObserver = null;

    function pinSaveBar(view) {
        var bar = view.querySelector('#ampSaveBar');
        var spot = view.querySelector('#ampSaveSpot');
        var box = spot.parentElement;
        // Measured each time: the observer only fires on crossing the viewport edge, and a tab
        // drawn while hidden never crosses it when it is shown.
        var pin = spot.offsetParent !== null && spot.getBoundingClientRect().top > (window.innerHeight || document.documentElement.clientHeight);
        if (pin) {
            var r = box.getBoundingClientRect();
            spot.style.height = bar.offsetHeight + 'px';
            bar.style.left = r.left + 'px';
            bar.style.width = r.width + 'px';
        } else {
            spot.style.height = '';
            bar.style.left = '';
            bar.style.width = '';
        }

        bar.classList.toggle('amp-savebar-pinned', pin);
    }

    function unwatchSaveBar(view) {
        if (saveBarObserver) {
            saveBarObserver.disconnect();
            saveBarObserver = null;
        }

        var bar = view.querySelector('#ampSaveBar');
        view.querySelector('#ampSaveSpot').style.height = '';
        bar.style.left = '';
        bar.style.width = '';
        bar.classList.remove('amp-savebar-pinned');
    }

    function watchSaveBar(view) {
        var spot = view.querySelector('#ampSaveSpot');
        if (!saveBarObserver && window.IntersectionObserver) {
            saveBarObserver = new IntersectionObserver(function () { pinSaveBar(view); });
            saveBarObserver.observe(spot);
        }

        pinSaveBar(view);
    }

    function renderGeneral(view, config) {
        view.querySelector('#ampTitlePreference').value = enumName(config.TitlePreference, titlePreferences) || 'Localized';
        view.querySelector('#ampEpisodeTitlePreference').value = config.EpisodeTitlePreference || '';
        view.querySelector('#ampSeasonDetails').value = config.SeasonDetails || 'AniDB';
        view.querySelector('#ampSearchableTitles').checked = !!config.SearchableAlternativeTitles;
        view.querySelector('#ampFranchiseCollections').checked = !!config.FranchiseCollections;
        view.querySelector('#ampSeasonNames').value = config.SeasonNames || 'Source';
        view.querySelector('#ampMaxGenres').value = config.MaxGenres;
        view.querySelector('#ampMinTagWeight').value = config.MinimumTagWeight;
        view.querySelector('#ampTagBlacklist').value = config.TagBlacklist || '';
        view.querySelector('#ampMaxCacheAge').value = config.MaxCacheAge;
        view.querySelector('#ampImagesFromCache').checked = config.AniDbImagesFromCache !== false;
        view.querySelector('#ampImportGenres').checked = !!config.ImportGenres;
        view.querySelector('#ampImportTags').checked = !!config.ImportTags;
        view.querySelector('#ampImportRating').checked = !!config.ImportCommunityRating;
        view.querySelector('#ampCastCharacters').checked = !!config.CastShowsCharacters;
        view.querySelector('#ampPublishIds').checked = !!config.PublishMappedIds;
        view.querySelector('#ampUseAniBridge').checked = !!config.UseAniBridgeMappings;
        view.querySelector('#ampUseAnnExtras').checked = config.UseAnnExtras !== false;
        view.querySelector('#ampIdentifySearchOnline').checked = config.IdentifySearchOnline !== false;
        view.querySelector('#ampIdentifyRemotePosters').checked = config.IdentifyRemotePosters !== false;
        view.querySelector('#ampIdentifyPosterLabels').checked = config.IdentifyPosterLabels !== false;
        view.querySelector('#ampAnnEnglishCast').checked = !!config.AnnEnglishCast;
        view.querySelector('#ampFlareSolverrUrl').value = config.FlareSolverrUrl || '';
        view.querySelector('#ampFlareSolverrTimeout').value = config.FlareSolverrTimeoutSeconds || 90;
        view.querySelector('#ampLogSettings').checked = config.LogSettings !== false;
        applyNeeds(view);
        tintSaveBar(view);
        watchSaveBar(view);
    }

    // Image overrides

    var imageState = { total: 0, reverted: 0, missing: 0, selected: {}, token: 0, watching: false, watchId: 0, running: false };

    function selectedImages() {
        return Object.keys(imageState.selected);
    }

    function renderImageToolbar(view) {
        var count = selectedImages().length;
        view.querySelector('#ampImgSelected').textContent = count + ' selected of ' + imageState.total;
        view.querySelector('#ampImgForget').style.display = imageState.reverted > 0 ? '' : 'none';
        view.querySelector('#ampImgForgetText').textContent = 'Forget ' + imageState.reverted + ' reverted';
        view.querySelector('#ampImgForgetMissing').style.display = imageState.missing > 0 ? '' : 'none';
        view.querySelector('#ampImgForgetMissingText').textContent = 'Forget missing (' + imageState.missing + ')';
    }

    function renderImages(view, page) {
        imageState.total = page.TotalCount;
        imageState.reverted = page.RevertedCount || 0;
        imageState.missing = page.MissingCount || 0;

        pageStart(view, 'images', page.TotalCount);

        // One tag per load keeps a replaced poster from showing from the browser's cache.
        var tag = String(Date.now());
        view.querySelector('#ampImages').innerHTML = page.Items.length ? page.Items.map(function (item) {
            // The server has no image for an item it no longer has, so none is asked for.
            var now = item.Missing ? '' : ApiClient.getImageUrl(item.Id, { type: 'Primary', maxHeight: 240, tag: tag });
            var full = item.Missing ? '' : ApiClient.getImageUrl(item.Id, { type: 'Primary', tag: tag });
            return '<div class="amp-imgcard" data-img="' + esc(item.Id) + '">'
                + '<label><input type="checkbox" class="amp-imgcheck"' + (imageState.selected[item.Id] ? ' checked' : '') + ' /><span class="amp-imgname">' + esc(item.SeriesName || item.ItemName) + '</span></label>'
                + '<div class="amp-small">' + esc(item.Kind) + (item.SeriesName ? ' - ' + esc(item.ItemName) : '') + ' - replaced ' + when(item.ReplacedUtc) + (item.Missing ? ' ' + badge('not in library', 'amp-warn') : '') + '</div>'
                + '<div class="amp-imgpair">'
                + '<figure><div class="amp-imgbox" data-old="1">' + (item.HasBackup ? 'Loading...' : 'None') + '</div><figcaption>Previous</figcaption></figure>'
                + (item.Missing
                    ? '<figure><div class="amp-imgbox">No longer in the library</div><figcaption>Now (AniDB)</figcaption></figure>'
                    : '<figure><img src="' + esc(now) + '" data-full="' + esc(full) + '" alt="" /><figcaption>Now (AniDB)</figcaption></figure>')
                + '</div>'
                + '<div class="amp-actions">' + (item.Missing
                    ? '<button type="button" is="emby-button" class="raised" data-img-act="Forget">Forget</button>'
                    : '<button type="button" is="emby-button" class="raised" data-img-act="Revert">Revert</button>'
                    + '<button type="button" is="emby-button" class="raised" data-img-act="Acknowledge">Acknowledge</button>') + '</div>'
                + '</div>';
        }).join('') : '<p class="amp-muted">No replaced posters are waiting for a decision.</p>';

        renderImageToolbar(view);

        // The previous posters come as data addresses, only for the page on show.
        var token = ++imageState.token;
        page.Items.filter(function (item) { return item.HasBackup; }).forEach(function (item) {
            get('Images/Backup', { Id: item.Id }).then(function (preview) {
                var box = token === imageState.token && view.querySelector('.amp-imgcard[data-img="' + item.Id + '"] .amp-imgbox[data-old]');
                if (box && preview && preview.DataUrl) {
                    var image = document.createElement('img');
                    image.src = preview.DataUrl;
                    image.setAttribute('data-full', preview.DataUrl);
                    image.alt = '';
                    box.replaceWith(image);
                } else if (box) {
                    box.textContent = 'Missing';
                }
            }, function () {
                var box = token === imageState.token && view.querySelector('.amp-imgcard[data-img="' + item.Id + '"] .amp-imgbox[data-old]');
                if (box) {
                    box.textContent = 'Missing';
                }
            });
        });
    }

    function loadImages(view) {
        var state = pagerState('images');
        return get('Images/Backups', { Start: state.page * state.size, Limit: state.size }).then(function (page) {
            // The last page can empty out once its items are decided.
            if (state.page > 0 && !page.Items.length && page.TotalCount > 0) {
                state.page = Math.ceil(page.TotalCount / state.size) - 1;
                return loadImages(view);
            }

            if (!page.TotalCount) {
                state.page = 0;
            }

            renderImages(view, page);
            setImagesBusy(view, imageState.running);

            // A revert started earlier, perhaps from another visit, may still be going.
            if (!imageState.watching) {
                watchRevert(view);
            }
        }, fail);
    }

    function setImagesBusy(view, busy) {
        view.querySelectorAll('#ampImgRevert, #ampImgAck, #ampImgForgetMissing, [data-img-act]').forEach(function (button) {
            button.disabled = busy;
        });
    }

    // Reverting runs on the server in the background; this follows it until it ends.
    function watchRevert(view) {
        // A newer watch replaces an older one, whose answer may predate the run it would report.
        var watchId = ++imageState.watchId;
        imageState.watching = true;
        get('Images/RevertStatus').then(function (s) {
            if (watchId !== imageState.watchId) {
                return;
            }

            var line = view.querySelector('#ampImgJob');
            var wasRunning = imageState.running;
            imageState.running = !!s.Running;
            setImagesBusy(view, imageState.running);

            if (s.Running) {
                line.textContent = 'Putting previous posters back: ' + (s.Done + s.Failed) + ' of ' + s.Total + (s.Failed ? ' (' + s.Failed + ' failed)' : '') + '. You can leave this page; it carries on.';
                if (document.body.contains(view)) {
                    setTimeout(function () {
                        if (watchId === imageState.watchId) {
                            watchRevert(view);
                        }
                    }, 1500);
                    return;
                }
            } else if (wasRunning) {
                line.textContent = 'Put the previous poster back on ' + s.Done + ' items' + (s.Failed ? ', ' + s.Failed + ' failed' : '') + (s.Skipped ? ', ' + s.Skipped + ' skipped as no longer in the library' : '') + '.';
                imageState.watching = false;
                loadImages(view);
                return;
            }

            imageState.watching = false;
        }, function () {
            if (watchId === imageState.watchId) {
                imageState.watching = false;
            }
        });
    }

    function decideImages(view, kind, ids) {
        if (!ids.length) {
            notify('Nothing selected');
            return;
        }

        var text = kind === 'Revert'
            ? 'Put the previous poster back on ' + ids.length + ' items? The task skips them from then on.'
            : "Keep AniDB's poster on " + ids.length + ' items and delete the previous ones?';
        if (ids.length > 1 && !window.confirm(text)) {
            return;
        }

        // Forgetting one missing item is acknowledging it: the backup and the record go.
        var action = kind === 'Forget' ? 'Acknowledge' : kind;
        host.showLoading();
        post('Images/' + action, null, { Ids: ids, All: false }).then(function (r) {
            host.hideLoading();
            ids.forEach(function (id) { delete imageState.selected[id]; });

            if (kind === 'Revert') {
                // Started in the background; the status line follows it.
                notify(r.Started ? 'Putting back ' + r.Total + ' posters' : r.Running ? 'A revert is already running' : r.Skipped ? 'Nothing to put back: ' + r.Skipped + ' no longer in the library' : 'Nothing to put back');
                imageState.running = !!r.Running;

                // The list reloads once the status reports the run done, not before.
                if (r.Running) {
                    watchRevert(view);
                    return;
                }

                return loadImages(view);
            }

            notify((kind === 'Forget' ? 'Forgot ' : 'Acknowledged ') + r.Done);
            return loadImages(view);
        }, fail);
    }

    function showImage(source) {
        var overlay = document.createElement('div');
        overlay.style.cssText = 'position:fixed;top:0;left:0;right:0;bottom:0;z-index:100000;background:rgba(0,0,0,.85);display:flex;align-items:center;justify-content:center;cursor:zoom-out';
        var image = document.createElement('img');
        image.src = source;
        image.style.cssText = 'max-width:95vw;max-height:95vh;object-fit:contain';
        overlay.appendChild(image);

        function close() {
            overlay.remove();
            document.removeEventListener('keydown', onKey);
        }

        function onKey(e) {
            if (e.key === 'Escape') {
                close();
            }
        }

        overlay.addEventListener('click', close);
        document.addEventListener('keydown', onKey);
        document.body.appendChild(overlay);
    }

    function wireImages(view) {
        var grid = view.querySelector('#ampImages');

        grid.addEventListener('change', function (e) {
            var box = e.target.closest('.amp-imgcheck');
            if (box) {
                var id = box.closest('[data-img]').getAttribute('data-img');
                if (box.checked) {
                    imageState.selected[id] = true;
                } else {
                    delete imageState.selected[id];
                }

                renderImageToolbar(view);
            }
        });

        grid.addEventListener('click', function (e) {
            var button = e.target.closest('button[data-img-act]');
            if (button) {
                decideImages(view, button.getAttribute('data-img-act'), [button.closest('[data-img]').getAttribute('data-img')]);
                return;
            }

            var image = e.target.closest('.amp-imgpair img');
            if (image) {
                showImage(image.getAttribute('data-full') || image.src);
            }
        });

        view.querySelector('#ampImgSelectAll').addEventListener('click', function () {
            // Every id, across pages: the list holds no pictures, so it is cheap to fetch whole.
            get('Images/Backups', { Start: 0, Limit: 100000 }).then(function (all) {
                all.Items.forEach(function (item) { imageState.selected[item.Id] = true; });
                Array.prototype.forEach.call(grid.querySelectorAll('.amp-imgcheck'), function (box) { box.checked = true; });
                renderImageToolbar(view);
            }, fail);
        });

        view.querySelector('#ampImgSelectPage').addEventListener('click', function () {
            Array.prototype.forEach.call(grid.querySelectorAll('[data-img]'), function (card) {
                var box = card.querySelector('.amp-imgcheck');
                if (box) {
                    imageState.selected[card.getAttribute('data-img')] = true;
                    box.checked = true;
                }
            });
            renderImageToolbar(view);
        });

        view.querySelector('#ampImgRefresh').addEventListener('click', function () {
            loadImages(view);
        });

        view.querySelector('#ampImgClear').addEventListener('click', function () {
            imageState.selected = {};
            Array.prototype.forEach.call(grid.querySelectorAll('.amp-imgcheck'), function (box) { box.checked = false; });
            renderImageToolbar(view);
        });

        view.querySelector('#ampImgRevert').addEventListener('click', function () { decideImages(view, 'Revert', selectedImages()); });
        view.querySelector('#ampImgAck').addEventListener('click', function () { decideImages(view, 'Acknowledge', selectedImages()); });

        view.querySelector('#ampImgForgetMissing').addEventListener('click', function () {
            if (!window.confirm('Forget the ' + imageState.missing + ' posters kept for shows no longer in the library? Their backup files are deleted.')) {
                return;
            }

            post('Images/ForgetMissing').then(function (r) {
                notify('Forgot ' + r.Forgotten);
                loadImages(view);
            }, fail);
        });

        view.querySelector('#ampImgForget').addEventListener('click', function () {
            if (!window.confirm('Forget the ' + imageState.reverted + " reverted items? The next run of the task replaces their posters with AniDB's again.")) {
                return;
            }

            post('Images/ForgetReverted').then(function (r) {
                notify('Forgot ' + r.Done);
                loadImages(view);
            }, fail);
        });
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
            unmatchedList = r[5];
            renderUnmatched(view);
            reviewList = r[6];
            renderReviews(view);
            renderBlocks(view, r[7]);
            renderGeneral(view, config);
            renderGenreRebuild(view, config);
            renderCache(view, r[8]);
            host.hideLoading();

            if (!genreRebuild.watching) {
                watchGenreRebuild(view);
            }
        }, fail);
    }

    // "status:waiting" opens the Status tab at its Waiting section.
    function showTab(view, name) {
        var parts = name.split(':');
        Array.prototype.forEach.call(view.querySelectorAll('[data-tab]'), function (el) {
            el.classList.toggle('active', el.getAttribute('data-tab') === parts[0]);
        });

        if (parts[1]) {
            showSub(view.querySelector('.amp-tab[data-tab="' + parts[0] + '"]'), parts[1]);
        }

        // Tab switches change the sentinel's visibility without scrolling, so re-check after layout.
        window.setTimeout(function () { pinSaveBar(view); }, 0);

        if (parts[0] === 'images') {
            loadImages(view);
        }

        if (parts[0] === 'mapping') {
            loadMapping(view);
        }

        rememberTab(view);
    }

    // The tab on show, with its sub-tab where it has one, survives a reload of the page.
    function rememberTab(view) {
        var tab = view.querySelector('.amp-tab.active');
        var sub = tab && tab.querySelector('.amp-sub.active');
        try {
            sessionStorage.setItem('animetaplus.tab', tab.getAttribute('data-tab') + (sub ? ':' + sub.getAttribute('data-sub') : ''));
        } catch (e) { /* storage can be blocked; the page then opens on Status */ }
    }

    function restoreTab(view) {
        var saved = '';
        try {
            saved = sessionStorage.getItem('animetaplus.tab') || '';
        } catch (e) { /* see rememberTab */ }

        var parts = saved.split(':');
        // No backslashes anywhere in this file: the build's WriteLinesToFile turns them into slashes on Linux.
        var tab = /^[a-z]+$/.test(parts[0]) && view.querySelector('.amp-tab[data-tab="' + parts[0] + '"]');
        if (!tab || (parts[1] && !(/^[a-z]+$/.test(parts[1]) && tab.querySelector('.amp-sub[data-sub="' + parts[1] + '"]')))) {
            return false;
        }

        showTab(view, saved);
        return true;
    }

    function showSub(tab, name) {
        Array.prototype.forEach.call(tab.querySelectorAll('[data-sub]'), function (el) {
            el.classList.toggle('active', el.getAttribute('data-sub') === name);
        });
    }

    return function (view) {
        var malVisible = false;
        pagerRenders = {
            mapping: renderMapping, blocks: renderBlockRows, unmatched: renderUnmatched, review: renderReviews,
            waiting: renderWaiting, provisional: renderProvisional, requests: renderRequests, images: loadImages
        };
        // Hidden until their list first draws them, so no empty pager flashes up.
        Array.prototype.forEach.call(view.querySelectorAll('[data-pager]'), function (pager) { pager.style.display = 'none'; });
        view.querySelector('#ampToggleMal').innerHTML = eyeIcon;

        view.addEventListener('click', function (e) {
            var tab = e.target.closest('.amp-tabs button[data-tab], [data-goto]');
            if (tab) {
                showTab(view, tab.getAttribute('data-tab') || tab.getAttribute('data-goto'));
                return;
            }

            var sub = e.target.closest('.amp-subtabs button[data-sub]');
            if (sub) {
                showSub(sub.closest('.amp-tab'), sub.getAttribute('data-sub'));
                window.setTimeout(function () { pinSaveBar(view); }, 0);
                rememberTab(view);
                return;
            }

            var pageButton = e.target.closest('[data-pager] [data-page]');
            if (pageButton) {
                var name = pageButton.closest('[data-pager]').getAttribute('data-pager');
                pagerState(name).page += parseInt(pageButton.getAttribute('data-page'), 10);
                pagerRenders[name](view);
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
        });

        view.querySelector('#ampUnmatched').addEventListener('click', function (e) { onUnmatchedClick(view, e); });
        view.querySelector('#ampUnmatched').addEventListener('keydown', onUnmatchedSearch);
        view.querySelector('#ampUnmatchedFilter').addEventListener('input', function () { resetPage('unmatched'); renderUnmatched(view); });
        view.querySelector('#ampUnmatched').addEventListener('click', function (e) {
            var header = e.target.closest('[data-sort]');
            if (header) {
                var key = header.getAttribute('data-sort');
                unmatchedSort = { key: key, dir: unmatchedSort && unmatchedSort.key === key ? -unmatchedSort.dir : 1 };
                resetPage('unmatched');
                renderUnmatched(view);
            }
        });
        view.querySelector('#ampSearchAllUnmatched').addEventListener('click', function () { searchAllUnmatched(view); });
        view.querySelector('#ampShowIgnored').addEventListener('change', function () { loadAll(view); });
        view.querySelector('#ampShowExpired').addEventListener('change', function () { loadAll(view); });
        ['#ampBlockFilterSource', '#ampBlockFilterKind', '#ampBlockFilterReason'].forEach(function (id) {
            view.querySelector(id).addEventListener('change', function () { resetPage('blocks'); renderBlockRows(view); });
        });
        view.querySelector('#ampBlockFilterText').addEventListener('input', function () { resetPage('blocks'); renderBlockRows(view); });
        view.querySelector('#ampLiftShown').addEventListener('click', function () { liftShown(view); });
        view.querySelector('#ampBlocks').addEventListener('click', function (e) {
            var header = e.target.closest('[data-sort]');
            if (header) {
                var key = header.getAttribute('data-sort');
                blockSort = { key: key, dir: blockSort.key === key ? -blockSort.dir : (key === 'seen' ? -1 : 1) };
                renderBlockRows(view);
            }
        });
        view.querySelector('#ampReviewFilterField').addEventListener('change', function () { resetPage('review'); renderReviews(view); });
        view.querySelector('#ampReviewFilterText').addEventListener('input', function () { resetPage('review'); renderReviews(view); });
        Object.keys(reviewDecisions).forEach(function (d) {
            view.querySelector('[data-decide-all="' + d + '"]').addEventListener('click', function () { decideShown(view, d); });
        });
        view.querySelector('#ampReviews').addEventListener('click', function (e) {
            var header = e.target.closest('[data-sort]');
            if (header) {
                var key = header.getAttribute('data-sort');
                reviewSort = { key: key, dir: reviewSort && reviewSort.key === key ? -reviewSort.dir : 1 };
                resetPage('review');
                renderReviews(view);
            }
        });
        view.querySelector('#ampMapping').addEventListener('click', function (e) { onMappingClick(view, e); });
        view.querySelector('#ampMapping').addEventListener('input', function (e) {
            var field = e.target.getAttribute('data-map-field');
            if (field && mapEdit) {
                mapEdit[field] = e.target.value;
            }
        });
        view.querySelector('#ampMapping').addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && e.target.getAttribute('data-map-field')) {
                e.preventDefault();
                var save = view.querySelector('.amp-map-edit [data-map-act="save"]');
                if (save) {
                    save.click();
                }
            }
        });
        view.querySelector('#ampMapFilterStatus').addEventListener('change', function () { resetPage('mapping'); renderMapping(view); });
        view.querySelector('#ampMapFilterText').addEventListener('input', function () { resetPage('mapping'); renderMapping(view); });
        view.querySelector('#ampMapReload').addEventListener('click', function () { loadMapping(view); });
        view.querySelector('#ampRefreshStatus').addEventListener('click', function () { loadAll(view); });

        view.querySelector('#ampProvisional').addEventListener('click', function (e) {
            var keep = e.target.closest('[data-standin-keep]');
            var retry = e.target.closest('[data-standin-retry]');
            if (keep) {
                post('Provisional/Keep', null, { Ids: [keep.getAttribute('data-standin-keep')], All: false }).then(function () {
                    notify('Kept the stand-in value');
                    loadAll(view);
                }, fail);
            } else if (retry) {
                post('Provisional/Retry', { Id: retry.getAttribute('data-standin-retry') }).then(function (r) {
                    notify(r.Done ? 'Refresh queued; reload the page in a moment' : 'The item was not found');
                }, fail);
            }
        });

        view.querySelector('#ampKeepAllStandIns').addEventListener('click', function () {
            if (!window.confirm('Keep every stand-in value and stop waiting for the preferred sources?')) {
                return;
            }

            post('Provisional/Keep', null, { Ids: [], All: true }).then(function (r) {
                notify('Kept ' + r.Done + ' stand-in values');
                loadAll(view);
            }, fail);
        });

        view.querySelector('#ampRunQueues').addEventListener('click', function () {
            post('Queues/Run').then(function (r) {
                view.querySelector('#ampQueueResult').innerHTML = 'Queued ' + r.Resumed + ' resumed, ' + r.Upgraded + ' to upgrade, ' + r.Mapped + ' mapped; removed ' + r.Pruned + ' lapsed blocks.';
                loadAll(view);
            }, fail);
        });

        view.querySelector('#ampSaveSources').addEventListener('click', function () { saveSources(view); });
        view.querySelector('#ampGenreRebuild').addEventListener('click', function () {
            post('GenreRebuild').then(function (s) {
                showGenreRebuild(view, s);
                if (!genreRebuild.watching) {
                    watchGenreRebuild(view);
                }
            }, fail);
        });
        view.querySelector('#ampSources').addEventListener('input', function () { renderEstimates(view); });
        wireOrder(view);

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

        view.querySelector('.amp-sub[data-sub="settings"]').addEventListener('change', function () { applyNeeds(view); });

        view.querySelector('#ampSaveGeneral').addEventListener('click', function () {
            withConfig(view, function (config) {
                config.TitlePreference = view.querySelector('#ampTitlePreference').value;
                config.EpisodeTitlePreference = view.querySelector('#ampEpisodeTitlePreference').value;
                config.SeasonDetails = view.querySelector('#ampSeasonDetails').value;
                config.SearchableAlternativeTitles = view.querySelector('#ampSearchableTitles').checked;
                config.FranchiseCollections = view.querySelector('#ampFranchiseCollections').checked;
                config.SeasonNames = view.querySelector('#ampSeasonNames').value;
                config.MaxGenres = parseInt(view.querySelector('#ampMaxGenres').value, 10) || 0;
                config.MinimumTagWeight = parseInt(view.querySelector('#ampMinTagWeight').value, 10) || 0;
                config.TagBlacklist = view.querySelector('#ampTagBlacklist').value;
                config.MaxCacheAge = Math.max(3, parseInt(view.querySelector('#ampMaxCacheAge').value, 10) || 7);
                config.AniDbImagesFromCache = view.querySelector('#ampImagesFromCache').checked;
                config.ImportGenres = view.querySelector('#ampImportGenres').checked;
                config.ImportTags = view.querySelector('#ampImportTags').checked;
                config.ImportCommunityRating = view.querySelector('#ampImportRating').checked;
                config.CastShowsCharacters = view.querySelector('#ampCastCharacters').checked;
                config.PublishMappedIds = view.querySelector('#ampPublishIds').checked;
                config.UseAniBridgeMappings = view.querySelector('#ampUseAniBridge').checked;
                config.UseAnnExtras = view.querySelector('#ampUseAnnExtras').checked;
                config.IdentifySearchOnline = view.querySelector('#ampIdentifySearchOnline').checked;
                config.IdentifyRemotePosters = view.querySelector('#ampIdentifyRemotePosters').checked;
                config.IdentifyPosterLabels = view.querySelector('#ampIdentifyPosterLabels').checked;
                config.AnnEnglishCast = view.querySelector('#ampAnnEnglishCast').checked;
                config.FlareSolverrUrl = view.querySelector('#ampFlareSolverrUrl').value.trim();
                config.FlareSolverrTimeoutSeconds = Math.min(600, Math.max(10, parseInt(view.querySelector('#ampFlareSolverrTimeout').value, 10) || 90));
                config.LogSettings = view.querySelector('#ampLogSettings').checked;
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

        wireImages(view);

        window.addEventListener('resize', function () { pinSaveBar(view); });
        view.addEventListener('viewbeforehide', function () { unwatchSaveBar(view); });
        view.addEventListener('viewhide', function () { unwatchSaveBar(view); });

        var restored = false;
        view.addEventListener('viewshow', function () {
            watchSaveBar(view);
            loadAll(view);

            // The first show opens the tab last used; showTab starts its own loader.
            if (!restored) {
                restored = true;
                if (restoreTab(view)) {
                    return;
                }
            }

            if (view.querySelector('.amp-tab[data-tab="images"]').classList.contains('active')) {
                loadImages(view);
            }
        });
    };
}
