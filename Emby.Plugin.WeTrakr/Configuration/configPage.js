define(['loading', 'emby-input', 'emby-button', 'emby-checkbox'], function (loading) {
    'use strict';

    function api(method, path, body) {
        var opts = { type: method, url: ApiClient.getUrl(path), dataType: 'json' };
        if (body) {
            opts.data = JSON.stringify(body);
            opts.contentType = 'application/json';
        }
        return ApiClient.ajax(opts);
    }

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function when(d) {
        if (!d) return null;
        var t = new Date(d);
        return isNaN(t.getTime()) ? null : t.toLocaleString();
    }

    return function (view) {
        var pollTimers = {};

        function setStatus(text) {
            view.querySelector('#wtStatus').textContent = text || '';
        }

        function renderUsers(users) {
            var html = users.map(function (u) {
                var lines = [];
                if (u.Linked && !u.Invalid) {
                    lines.push('<div class="wt-ok">Linked to WeTrakr as <b>' + esc(u.WeTrakrUsername || '?') + '</b></div>');
                } else if (u.Invalid) {
                    lines.push('<div class="wt-warn">WeTrakr no longer accepts this link (it was linked again elsewhere or disconnected). Link it again.</div>');
                } else {
                    lines.push('<div class="wt-muted">Not linked</div>');
                }
                if (u.LastSyncAt) lines.push('<div class="wt-muted">Last sync: ' + esc(when(u.LastSyncAt)) + (u.LastSyncResult ? ' · ' + esc(u.LastSyncResult) : '') + '</div>');
                if (u.Linked && !u.Invalid && u.NextCheckAt) lines.push('<div class="wt-muted">Next check for changes: ' + esc(when(u.NextCheckAt)) + '</div>');
                if (u.LastScrobbleAt) lines.push('<div class="wt-muted">Last scrobble: ' + esc(when(u.LastScrobbleAt)) + '</div>');
                if (u.PendingCode) {
                    lines.push('<div>Go to <a href="' + esc(u.PendingUrl) + '" target="_blank" rel="noopener">' + esc(u.PendingUrl) + '</a> and enter:</div>' +
                        '<div class="wt-code">' + esc(u.PendingCode) + '</div><div class="wt-muted">Waiting for confirmation…</div>');
                }

                var buttons = [];
                if (u.PendingCode) {
                    buttons.push('<button is="emby-button" type="button" class="raised" data-action="cancel" data-user="' + esc(u.UserId) + '"><span>Cancel</span></button>');
                } else {
                    buttons.push('<button is="emby-button" type="button" class="raised button-submit" data-action="link" data-user="' + esc(u.UserId) + '"><span>' + (u.Linked ? 'Link again' : 'Link WeTrakr') + '</span></button>');
                }
                if (u.Linked) {
                    buttons.push('<button is="emby-button" type="button" class="raised" data-action="resync" data-user="' + esc(u.UserId) + '"><span>Full resync</span></button>');
                    buttons.push('<button is="emby-button" type="button" class="raised" data-action="disconnect" data-user="' + esc(u.UserId) + '"><span>Disconnect</span></button>');
                }

                return '<div class="wt-card"><div class="wt-row"><div><div class="wt-name">' + esc(u.UserName) + '</div>' + lines.join('') + '</div>' +
                    '<div class="wt-actions">' + buttons.join('') + '</div></div></div>';
            }).join('');
            view.querySelector('#wtUsers').innerHTML = html || '<p class="wt-muted">No Emby users.</p>';

            users.forEach(function (u) {
                if (u.PendingCode && !pollTimers[u.UserId]) startPolling(u.UserId);
            });
        }

        function renderLibraries(libs) {
            var html = libs.map(function (l) {
                var type = l.CollectionType ? ' <span class="wt-muted">(' + esc(l.CollectionType) + ')</span>' : '';
                return '<div class="checkboxContainer"><label><input is="emby-checkbox" type="checkbox" class="wtLibrary" data-id="' + esc(l.Id) + '"' +
                    (l.Excluded ? ' checked' : '') + ' /><span>' + esc(l.Name) + type + '</span></label></div>';
            }).join('');
            view.querySelector('#wtLibraries').innerHTML = html || '<p class="wt-muted">No movie or TV libraries found.</p>';
        }

        function excludedLibraryIds() {
            return Array.prototype.map.call(view.querySelectorAll('.wtLibrary:checked'), function (cb) {
                return cb.getAttribute('data-id');
            });
        }

        function load() {
            loading.show();
            return api('GET', 'WeTrakr/Status').then(function (s) {
                view.querySelector('#wtScrobblePlaying').checked = !!s.ScrobblePlaying;
                view.querySelector('#wtScrobbleWatched').checked = !!s.ScrobbleWatched;
                view.querySelector('#wtScrobbleUnwatched').checked = !!s.ScrobbleUnwatched;
                view.querySelector('#wtScrobbleRatings').checked = !!s.ScrobbleRatings;
                view.querySelector('#wtSyncToWeTrakr').checked = !!s.SyncToWeTrakr;
                view.querySelector('#wtSyncFromWeTrakr').checked = !!s.SyncFromWeTrakr;
                view.querySelector('#wtWatchlistPlaylist').checked = !!s.WatchlistPlaylist;
                renderLibraries(s.Libraries || []);
                view.querySelector('#wtPlaylistName').value = s.WatchlistPlaylistName || 'WeTrakr Watchlist';
                view.querySelector('#wtVersion').textContent = 'Plugin version ' + (s.Version || '');
                renderUsers(s.Users || []);
                loading.hide();
            }, function () {
                loading.hide();
                setStatus('Could not load the WeTrakr settings.');
            });
        }

        function startPolling(userId) {
            stopPolling(userId);
            pollTimers[userId] = setInterval(function () {
                api('POST', 'WeTrakr/Connect/Poll', { UserId: userId }).then(function (r) {
                    if (!r || r.Status === 'pending') return;
                    stopPolling(userId);
                    if (r.Status === 'linked') {
                        var extra = (r.StaleLinksRemoved && r.StaleLinksRemoved.length) ? ' (removed the old link of ' + r.StaleLinksRemoved.join(', ') + ')' : '';
                        setStatus('Linked to ' + r.Username + '. The first sync has started' + extra + '.');
                    } else if (r.Status === 'expired') {
                        setStatus('The code expired. Try again.');
                    } else {
                        setStatus(r.Error || 'Linking was cancelled.');
                    }
                    load();
                });
            }, 5000);
        }

        function stopPolling(userId) {
            if (pollTimers[userId]) {
                clearInterval(pollTimers[userId]);
                delete pollTimers[userId];
            }
        }

        view.querySelector('#wtUsers').addEventListener('click', function (e) {
            var btn = e.target.closest('button[data-action]');
            if (!btn) return;
            var userId = btn.getAttribute('data-user');
            var action = btn.getAttribute('data-action');
            setStatus('');

            if (action === 'link') {
                api('POST', 'WeTrakr/Connect/Start', { UserId: userId }).then(function (r) {
                    if (!r || !r.Ok) { setStatus((r && r.Error) || 'Could not start linking.'); return; }
                    load().then(function () { startPolling(userId); });
                });
            } else if (action === 'cancel') {
                stopPolling(userId);
                api('POST', 'WeTrakr/Connect/Cancel', { UserId: userId }).then(load);
            } else if (action === 'disconnect') {
                api('POST', 'WeTrakr/Disconnect', { UserId: userId }).then(function () {
                    setStatus('Disconnected. To stop the account in WeTrakr too, disconnect Emby in WeTrakr Settings.');
                    load();
                });
            } else if (action === 'resync') {
                api('POST', 'WeTrakr/Sync/Reset', { UserId: userId }).then(function () {
                    setStatus('Full resync started. It can take a few minutes on big libraries.');
                });
            }
        });

        view.querySelector('#wtSave').addEventListener('click', function () {
            api('POST', 'WeTrakr/Settings', {
                ScrobblePlaying: view.querySelector('#wtScrobblePlaying').checked,
                ScrobbleWatched: view.querySelector('#wtScrobbleWatched').checked,
                ScrobbleUnwatched: view.querySelector('#wtScrobbleUnwatched').checked,
                ScrobbleRatings: view.querySelector('#wtScrobbleRatings').checked,
                SyncToWeTrakr: view.querySelector('#wtSyncToWeTrakr').checked,
                SyncFromWeTrakr: view.querySelector('#wtSyncFromWeTrakr').checked,
                WatchlistPlaylist: view.querySelector('#wtWatchlistPlaylist').checked,
                WatchlistPlaylistName: view.querySelector('#wtPlaylistName').value,
                ExcludedLibraryIds: excludedLibraryIds()
            }).then(function () { setStatus('Settings saved.'); });
        });

        view.querySelector('#wtSyncNow').addEventListener('click', function () {
            api('POST', 'WeTrakr/Sync/Run').then(function () { setStatus('Sync started. Check back in a minute.'); });
        });

        view.addEventListener('viewshow', load);
        view.addEventListener('viewhide', function () {
            Object.keys(pollTimers).forEach(stopPolling);
        });
    };
});
