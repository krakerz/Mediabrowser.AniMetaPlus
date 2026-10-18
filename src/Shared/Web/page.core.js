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

        view.querySelector('#ampWaiting').innerHTML = waiting.length
            ? '<tr><th>Item</th><th>Waits for</th><th>Since</th></tr>' + waiting.map(function (w) {
                return '<tr><td>' + esc(w.ItemName) + '</td><td>' + esc(w.Source) + ' (' + esc(w.Reason) + ')</td><td>' + when(w.AddedUtc) + '</td></tr>';
            }).join('')
            : '<tr><td class="amp-muted">Nothing is waiting.</td></tr>';

        view.querySelector('#ampProvisional').innerHTML = provisional.length
            ? '<tr><th>Item</th><th>Field</th><th>Supplied by</th><th>Instead of</th><th>Held by</th><th></th></tr>' + provisional.map(function (p) {
                var held = p.HeldBy ? esc(p.HeldBy) + (p.HeldUntilUtc ? ' (until ' + when(p.HeldUntilUtc) + ')' : '') : '<span class="amp-muted">Nothing known; retried on the next queue run</span>';
                return '<tr><td>' + esc(p.ItemName) + '</td><td>' + esc(kindNames[p.Field] || p.Field) + '</td><td>' + esc(p.Source) + '</td><td>' + esc(p.PreferredSource) + '</td><td class="amp-small">' + held + '</td>'
                    + '<td><span class="amp-actions"><button is="emby-button" type="button" class="raised" data-standin-keep="' + esc(p.Id) + '"><span>Keep</span></button>'
                    + '<button is="emby-button" type="button" class="raised" data-standin-retry="' + esc(p.Id) + '"><span>Retry now</span></button></span></td></tr>';
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

    function renderUnmatched(view, items, mappings) {
        view.querySelector('#ampUnmatched').innerHTML = items.length ? items.map(function (u) {
            var guesses = (u.Candidates || []).map(function (c) {
                return '<option value="' + esc(c.AnimeId) + '">' + esc(c.Title) + ' (' + esc(c.AnimeId) + ', ' + c.Score + '%)</option>';
            }).join('');
            var options = searchedGuesses[u.Id] || '<option value="">Guesses from the titles dump...</option>' + guesses;
            return '<div class="amp-card" data-key="' + esc(u.Id) + '" style="margin-bottom:1em">'
                + '<div><b style="font-size:1.1em;display:inline">' + esc(u.Name) + '</b> ' + (u.Year ? '(' + u.Year + ') ' : '') + (u.Ignored ? badge('set aside') : '') + '</div>'
                + '<div class="amp-small">' + esc(u.Path) + ' - tried ' + u.Attempts + 'x, last ' + when(u.LastAttemptUtc) + '</div>'
                + '<div class="amp-row" style="margin-top:.5em">'
                + '<div class="inputContainer"><select class="amp-guess">' + options + '</select></div>'
                + '<div class="inputContainer"><input type="text" class="amp-search" placeholder="Search the titles dump" value="' + esc(u.Name) + '" /></div>'
                + '<div class="inputContainer"><input type="text" class="amp-id" placeholder="AniDB, AniList or MyAnimeList id or link" /></div>'
                + '</div>'
                + '<div class="amp-actions"><button type="button" is="emby-button" class="raised" data-act="online">Search titles dump, AniList and MyAnimeList</button>'
                + '<button type="button" is="emby-button" class="raised" data-act="map">Map</button>'
                + '<button type="button" is="emby-button" class="raised button-submit" data-act="mapnow">Map now</button>'
                + '<button type="button" is="emby-button" class="raised" data-act="ignore">' + (u.Ignored ? 'Bring back' : 'Set aside') + '</button></div>'
                + '</div>';
        }).join('') : '<p class="amp-muted">Everything is identified.</p>';

        view.querySelector('#ampCountUnmatched').textContent = '(' + items.length + ')';
        renderMappings(view, mappings);
    }

    function renderMappings(view, mappings) {
        view.querySelector('#ampCountMapped').textContent = '(' + mappings.length + ')';
        view.querySelector('#ampMappings').innerHTML = mappings.length
            ? '<tr><th>Item</th><th>Mapped to</th><th>When</th><th></th></tr>' + mappings.map(function (m) {
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

            // A mapped item leaves the list for the mappings below; the other cards and what their
            // searches found stay as they are.
            removeCard(view, card);
            get('Mappings').then(function (mappings) { renderMappings(view, mappings); });
        }, fail);
    }

    function removeCard(view, card) {
        delete searchedGuesses[card.getAttribute('data-key')];
        card.remove();
        view.querySelector('#ampCountUnmatched').textContent = '(' + view.querySelectorAll('#ampUnmatched [data-key]').length + ')';

        if (!view.querySelector('#ampUnmatched [data-key]')) {
            view.querySelector('#ampUnmatched').innerHTML = '<p class="amp-muted">Everything is identified.</p>';
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
            status.innerHTML = 'Nothing listed to search.';
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
            status.innerHTML = 'Searched ' + cards.length + ' items; ' + withResults + ' have results to pick from.';
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
        view.querySelector('#ampEpisodeTitlePreference').value = config.EpisodeTitlePreference || '';
        view.querySelector('#ampSeasonDetails').value = config.SeasonDetails || 'AniDB';
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
    }

    // Image overrides

    var imageSize = 24;
    var imageState = { page: 0, total: 0, reverted: 0, selected: {}, token: 0, watching: false, running: false };

    function selectedImages() {
        return Object.keys(imageState.selected);
    }

    function renderImageToolbar(view) {
        var count = selectedImages().length;
        view.querySelector('#ampImgSelected').textContent = count + ' selected of ' + imageState.total;
        view.querySelector('#ampImgForget').style.display = imageState.reverted > 0 ? '' : 'none';
        view.querySelector('#ampImgForgetText').textContent = 'Forget ' + imageState.reverted + ' reverted';
    }

    function renderImages(view, page) {
        imageState.total = page.TotalCount;
        imageState.reverted = page.RevertedCount || 0;

        var pages = Math.max(1, Math.ceil(page.TotalCount / imageSize));
        view.querySelector('#ampImgPage').textContent = 'Page ' + (imageState.page + 1) + ' of ' + pages;
        view.querySelector('#ampImgPrev').disabled = imageState.page <= 0;
        view.querySelector('#ampImgNext').disabled = imageState.page + 1 >= pages;

        // One tag per load keeps a replaced poster from showing from the browser's cache.
        var tag = String(Date.now());
        view.querySelector('#ampImages').innerHTML = page.Items.length ? page.Items.map(function (item) {
            var now = ApiClient.getImageUrl(item.Id, { type: 'Primary', maxHeight: 240, tag: tag });
            var full = ApiClient.getImageUrl(item.Id, { type: 'Primary', tag: tag });
            return '<div class="amp-imgcard" data-img="' + esc(item.Id) + '">'
                + '<label><input type="checkbox" class="amp-imgcheck"' + (imageState.selected[item.Id] ? ' checked' : '') + ' /><span class="amp-imgname">' + esc(item.SeriesName || item.ItemName) + '</span></label>'
                + '<div class="amp-small">' + esc(item.Kind) + (item.SeriesName ? ' - ' + esc(item.ItemName) : '') + ' - replaced ' + when(item.ReplacedUtc) + '</div>'
                + '<div class="amp-imgpair">'
                + '<figure><div class="amp-imgbox" data-old="1">' + (item.HasBackup ? 'Loading...' : 'None') + '</div><figcaption>Previous</figcaption></figure>'
                + '<figure><img src="' + esc(now) + '" data-full="' + esc(full) + '" alt="" /><figcaption>Now (AniDB)</figcaption></figure>'
                + '</div>'
                + '<div class="amp-actions"><button type="button" is="emby-button" class="raised" data-img-act="Revert">Revert</button>'
                + '<button type="button" is="emby-button" class="raised" data-img-act="Acknowledge">Acknowledge</button></div>'
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
        return get('Images/Backups', { Start: imageState.page * imageSize, Limit: imageSize }).then(function (page) {
            // The last page can empty out once its items are decided.
            if (imageState.page > 0 && !page.Items.length && page.TotalCount > 0) {
                imageState.page = Math.ceil(page.TotalCount / imageSize) - 1;
                return loadImages(view);
            }

            if (!page.TotalCount) {
                imageState.page = 0;
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
        view.querySelectorAll('#ampImgRevert, #ampImgAck, [data-img-act]').forEach(function (button) {
            button.disabled = busy;
        });
    }

    // Reverting runs on the server in the background; this follows it until it ends.
    function watchRevert(view) {
        imageState.watching = true;
        get('Images/RevertStatus').then(function (s) {
            var line = view.querySelector('#ampImgJob');
            var wasRunning = imageState.running;
            imageState.running = !!s.Running;
            setImagesBusy(view, imageState.running);

            if (s.Running) {
                line.textContent = 'Putting previous posters back: ' + (s.Done + s.Failed) + ' of ' + s.Total + (s.Failed ? ' (' + s.Failed + ' failed)' : '') + '. You can leave this page; it carries on.';
                if (document.body.contains(view)) {
                    setTimeout(function () { watchRevert(view); }, 1500);
                    return;
                }
            } else if (wasRunning) {
                line.textContent = 'Put the previous poster back on ' + s.Done + ' items' + (s.Failed ? ', ' + s.Failed + ' failed' : '') + '.';
                imageState.watching = false;
                loadImages(view);
                return;
            }

            imageState.watching = false;
        }, function () {
            imageState.watching = false;
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

        host.showLoading();
        post('Images/' + kind, null, { Ids: ids, All: false }).then(function (r) {
            host.hideLoading();
            ids.forEach(function (id) { delete imageState.selected[id]; });

            if (kind === 'Revert') {
                // Started in the background; the status line follows it.
                notify(r.Started ? 'Putting back ' + r.Total + ' posters' : r.Running ? 'A revert is already running' : 'Nothing to put back');
                imageState.running = !!r.Running;
                if (!imageState.watching) {
                    watchRevert(view);
                }

                return;
            }

            notify('Acknowledged ' + r.Done);
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

        view.querySelector('#ampImgPrev').addEventListener('click', function () {
            imageState.page = Math.max(0, imageState.page - 1);
            loadImages(view);
        });

        view.querySelector('#ampImgNext').addEventListener('click', function () {
            imageState.page++;
            loadImages(view);
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

    // "status:waiting" opens the Status tab at its Waiting section.
    function showTab(view, name) {
        var parts = name.split(':');
        Array.prototype.forEach.call(view.querySelectorAll('[data-tab]'), function (el) {
            el.classList.toggle('active', el.getAttribute('data-tab') === parts[0]);
        });

        if (parts[1]) {
            showSub(view.querySelector('.amp-tab[data-tab="' + parts[0] + '"]'), parts[1]);
        }

        if (parts[0] === 'images') {
            loadImages(view);
        }
    }

    function showSub(tab, name) {
        Array.prototype.forEach.call(tab.querySelectorAll('[data-sub]'), function (el) {
            el.classList.toggle('active', el.getAttribute('data-sub') === name);
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

            var sub = e.target.closest('.amp-subtabs button[data-sub]');
            if (sub) {
                showSub(sub.closest('.amp-tab'), sub.getAttribute('data-sub'));
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
        view.querySelector('#ampSearchAllUnmatched').addEventListener('click', function () { searchAllUnmatched(view); });
        view.querySelector('#ampShowIgnored').addEventListener('change', function () { loadAll(view); });
        view.querySelector('#ampShowExpired').addEventListener('change', function () { loadAll(view); });
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

        view.querySelector('#ampSaveGeneral').addEventListener('click', function () {
            withConfig(view, function (config) {
                config.TitlePreference = view.querySelector('#ampTitlePreference').value;
                config.EpisodeTitlePreference = view.querySelector('#ampEpisodeTitlePreference').value;
                config.SeasonDetails = view.querySelector('#ampSeasonDetails').value;
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

        view.addEventListener('viewshow', function () {
            loadAll(view);

            if (view.querySelector('.amp-tab[data-tab="images"]').classList.contains('active')) {
                loadImages(view);
            }
        });
    };
}
