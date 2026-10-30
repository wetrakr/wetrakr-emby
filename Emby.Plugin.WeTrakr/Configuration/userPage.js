define(['loading', 'emby-button'], function (loading) {
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
        var pollTimer = null;

        function setStatus(text) {
            view.querySelector('#wtStatus').textContent = text || '';
        }

        function render(u) {
            var lines = [];
            if (u.Linked && !u.Invalid) {
                lines.push('<div class="wt-ok">Linked to WeTrakr as <b>' + esc(u.WeTrakrUsername || '?') + '</b></div>');
            } else if (u.Invalid) {
                lines.push('<div class="wt-warn">WeTrakr no longer accepts this link (this server was unlinked in WeTrakr Settings &gt; Emby). Link it again.</div>');
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
                buttons.push('<button is="emby-button" type="button" class="raised" data-action="cancel"><span>Cancel</span></button>');
            } else {
                buttons.push('<button is="emby-button" type="button" class="raised button-submit" data-action="link"><span>' + (u.Linked ? 'Link again' : 'Link WeTrakr') + '</span></button>');
            }
            if (u.Linked) {
                buttons.push('<button is="emby-button" type="button" class="raised" data-action="sync"><span>Sync now</span></button>');
                buttons.push('<button is="emby-button" type="button" class="raised" data-action="disconnect"><span>Disconnect</span></button>');
            }

            view.querySelector('#wtMe').innerHTML = '<div class="wt-card"><div class="wt-name">' + esc(u.UserName) + '</div>' + lines.join('') +
                '<div class="wt-actions">' + buttons.join('') + '</div></div>';

            if (u.PendingCode && !pollTimer) startPolling();
        }

        function load() {
            loading.show();
            return api('GET', 'WeTrakr/Me').then(function (s) {
                loading.hide();
                if (!s || !s.Ok) { setStatus((s && s.Error) || 'Could not load.'); return; }
                view.querySelector('#wtVersion').textContent = 'Plugin version ' + (s.Version || '');
                render(s.User || {});
            }, function () {
                loading.hide();
                setStatus('Could not load the WeTrakr settings.');
            });
        }

        function startPolling() {
            stopPolling();
            pollTimer = setInterval(function () {
                api('POST', 'WeTrakr/Me/Connect/Poll').then(function (r) {
                    if (!r || r.Status === 'pending') return;
                    stopPolling();
                    if (r.Status === 'linked') {
                        setStatus('Linked to ' + r.Username + '. The first sync has started.');
                    } else if (r.Status === 'expired') {
                        setStatus('The code expired. Try again.');
                    } else {
                        setStatus(r.Error || 'Linking was cancelled.');
                    }
                    load();
                });
            }, 5000);
        }

        function stopPolling() {
            if (pollTimer) {
                clearInterval(pollTimer);
                pollTimer = null;
            }
        }

        view.querySelector('#wtMe').addEventListener('click', function (e) {
            var btn = e.target.closest('button[data-action]');
            if (!btn) return;
            var action = btn.getAttribute('data-action');
            setStatus('');

            if (action === 'link') {
                api('POST', 'WeTrakr/Me/Connect/Start').then(function (r) {
                    if (!r || !r.Ok) { setStatus((r && r.Error) || 'Could not start linking.'); return; }
                    load().then(startPolling);
                });
            } else if (action === 'cancel') {
                stopPolling();
                api('POST', 'WeTrakr/Me/Connect/Cancel').then(load);
            } else if (action === 'disconnect') {
                api('POST', 'WeTrakr/Me/Disconnect').then(function () {
                    setStatus('Disconnected. To stop the account in WeTrakr too, disconnect Emby in WeTrakr Settings.');
                    load();
                });
            } else if (action === 'sync') {
                api('POST', 'WeTrakr/Me/Sync/Run').then(function () { setStatus('Sync started. Check back in a minute.'); });
            }
        });

        view.addEventListener('viewshow', load);
        view.addEventListener('viewhide', stopPolling);
    };
});
