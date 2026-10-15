/**
 * UltraBlur client script — injected into jellyfin-web by the UltraBlur plugin.
 *
 * On item detail pages, fetches the item's Plex-identical 4-corner colors from
 * GET /UltraBlur/Colors/{itemId} and paints a Plex-style ambient gradient as
 * the page background. The colors are exposed as CSS custom properties
 * (--ultrablur-tl/tr/br/bl) on <html>, with class ultrablur-active, so themes
 * can restyle freely.
 */
(function () {
    'use strict';

    var CACHE_PREFIX = 'ultrablur:';
    var currentItemId = null;

    // jellyfin-web stacks a fixed .backdropContainer (z-index -1, backdrop
    // images) under a fixed, opaque .backgroundContainer (#101010; 0.86
    // opacity while backdrops show), so the gradient goes on the latter.
    // backgroundContainer-transparent = video playback, leave it alone.
    var CSS = ''
        + 'html.ultrablur-active .backgroundContainer:not(.backgroundContainer-transparent){background:'
        + 'radial-gradient(ellipse at top left,var(--ultrablur-tl),transparent 70%),'
        + 'radial-gradient(ellipse at top right,var(--ultrablur-tr),transparent 70%),'
        + 'radial-gradient(ellipse at bottom right,var(--ultrablur-br),transparent 70%),'
        + 'radial-gradient(ellipse at bottom left,var(--ultrablur-bl),transparent 70%),#000}'
        // The title ribbon is 80% grey in the dark theme, and the wrapper is
        // opaque under .noBackdropTransparency; let the gradient through.
        + 'html.ultrablur-active .detailRibbon,html.ultrablur-active .detailPageWrapperContainer{background:transparent}'
        // Fade the detail banner into the gradient instead of a hard edge.
        + 'html.ultrablur-active .itemBackdrop{-webkit-mask-image:linear-gradient(#000 60%,transparent);'
        + 'mask-image:linear-gradient(#000 60%,transparent)}';

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
        return fetch(client.getUrl('UltraBlur/Colors/' + itemId), {
            headers: {
                Authorization: 'MediaBrowser Token="' + client.accessToken() + '"'
            }
        }).then(function (res) {
            if (!res.ok) {
                log('no colors for ' + itemId + ' (HTTP ' + res.status + ')');
                return null;
            }
            return res.json().then(function (colors) {
                try {
                    sessionStorage.setItem(CACHE_PREFIX + itemId, JSON.stringify(colors));
                } catch (e) { /* storage full/disabled */ }
                return colors;
            });
        }).catch(function (e) {
            log('fetch failed for ' + itemId + ': ' + e);
            return null;
        });
    }

    function apply(colors) {
        // Jellyfin serializes PascalCase (TopLeft, ...).
        var root = document.documentElement;
        root.style.setProperty('--ultrablur-tl', colors.TopLeft);
        root.style.setProperty('--ultrablur-tr', colors.TopRight);
        root.style.setProperty('--ultrablur-br', colors.BottomRight);
        root.style.setProperty('--ultrablur-bl', colors.BottomLeft);
        root.classList.add('ultrablur-active');
        log('applied colors for ' + currentItemId);
    }

    function clear() {
        document.documentElement.classList.remove('ultrablur-active');
    }

    function onNavigate() {
        if (!apiClient()) {
            return; // web app still booting
        }
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
            if (currentItemId !== itemId) {
                return;
            }
            if (colors) {
                apply(colors);
            } else {
                clear();
            }
        });
    }

    var style = document.createElement('style');
    style.id = 'ultrablur-style';
    style.textContent = CSS;
    document.head.appendChild(style);

    // jellyfin-web's router navigates with history.pushState, which fires no
    // hashchange; viewshow covers legacy views, the poll covers the rest.
    // ponytail: 250 ms hash poll; hook history.pushState if it ever matters.
    document.addEventListener('viewshow', onNavigate, true);
    setInterval(onNavigate, 250);
    log('client script active');
})();
