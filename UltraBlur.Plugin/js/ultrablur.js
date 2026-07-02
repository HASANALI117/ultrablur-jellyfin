/**
 * UltraBlur client script — injected into jellyfin-web by the UltraBlur plugin.
 *
 * On item detail pages, fetches the item's Plex-identical 4-corner colors from
 * GET /UltraBlur/Colors/{itemId} and paints a Plex-style ambient gradient on
 * the page's backdrop layer. Also exposes the colors as CSS custom properties
 * (--ultrablur-tl/tr/br/bl) on <html> so themes can restyle freely.
 */
(function () {
    'use strict';

    var CACHE_PREFIX = 'ultrablur:';
    var currentItemId = null;

    function log(msg) {
        if (window.console && console.debug) {
            console.debug('[UltraBlur] ' + msg);
        }
    }

    function apiClient() {
        return window.ApiClient || null;
    }

    function detailItemId() {
        // jellyfin-web routes: index.html#/details?id=<id>&serverId=...
        // (10.11 also serves #!/details?...); non-detail routes return null.
        var hash = window.location.hash || '';
        var m = /[#!/]details\?([^#]*)/.exec(hash);
        if (!m) {
            return null;
        }
        var q = new URLSearchParams(m[1]);
        return q.get('id');
    }

    function fetchColors(itemId) {
        var cached = null;
        try {
            cached = sessionStorage.getItem(CACHE_PREFIX + itemId);
        } catch (e) { /* storage disabled */ }
        if (cached) {
            return Promise.resolve(JSON.parse(cached));
        }

        var client = apiClient();
        if (!client) {
            return Promise.resolve(null);
        }
        return fetch(client.getUrl('UltraBlur/Colors/' + itemId), {
            headers: {
                Authorization: 'MediaBrowser Token="' + client.accessToken() + '"'
            }
        }).then(function (res) {
            if (!res.ok) {
                return null;
            }
            return res.json().then(function (colors) {
                try {
                    sessionStorage.setItem(CACHE_PREFIX + itemId, JSON.stringify(colors));
                } catch (e) { /* storage full/disabled */ }
                return colors;
            });
        }).catch(function () {
            return null;
        });
    }

    function gradient(colors) {
        return 'radial-gradient(ellipse at top left, ' + colors.topLeft + ', transparent 70%),'
            + 'radial-gradient(ellipse at top right, ' + colors.topRight + ', transparent 70%),'
            + 'radial-gradient(ellipse at bottom right, ' + colors.bottomRight + ', transparent 70%),'
            + 'radial-gradient(ellipse at bottom left, ' + colors.bottomLeft + ', transparent 70%),'
            + '#000';
    }

    function backdropLayer() {
        // jellyfin-web's fixed backdrop layer (behind all content). Fall back to
        // an own fixed div if a theme removed it.
        var el = document.querySelector('.backdropContainer');
        if (el) {
            return el;
        }
        el = document.getElementById('ultrablur-backdrop');
        if (!el) {
            el = document.createElement('div');
            el.id = 'ultrablur-backdrop';
            el.style.cssText = 'position:fixed;inset:0;z-index:-1;pointer-events:none;transition:opacity .4s;';
            document.body.insertBefore(el, document.body.firstChild);
        }
        return el;
    }

    function apply(colors) {
        var root = document.documentElement;
        root.style.setProperty('--ultrablur-tl', colors.topLeft);
        root.style.setProperty('--ultrablur-tr', colors.topRight);
        root.style.setProperty('--ultrablur-br', colors.bottomRight);
        root.style.setProperty('--ultrablur-bl', colors.bottomLeft);
        root.classList.add('ultrablur-active');
        backdropLayer().style.background = gradient(colors);
        log('applied colors for ' + currentItemId);
    }

    function clear() {
        var root = document.documentElement;
        root.classList.remove('ultrablur-active');
        var el = document.querySelector('.backdropContainer');
        if (el) {
            el.style.background = '';
        }
        var own = document.getElementById('ultrablur-backdrop');
        if (own) {
            own.style.background = '';
        }
    }

    function onNavigate() {
        var itemId = detailItemId();
        if (itemId === currentItemId) {
            return;
        }
        currentItemId = itemId;
        if (!itemId) {
            clear();
            return;
        }
        fetchColors(itemId).then(function (colors) {
            // Guard against stale responses after quick navigation.
            if (colors && currentItemId === itemId) {
                apply(colors);
            }
        });
    }

    function start() {
        window.addEventListener('hashchange', onNavigate);
        // jellyfin-web dispatches viewshow on SPA view changes (covers
        // back/forward restores that don't always fire hashchange first).
        document.addEventListener('viewshow', onNavigate, true);
        onNavigate();
        log('client script active');
    }

    // ApiClient appears once the web app boots; poll briefly.
    var tries = 0;
    var boot = setInterval(function () {
        if (apiClient() || ++tries > 100) {
            clearInterval(boot);
            if (apiClient()) {
                start();
            }
        }
    }, 200);
})();
