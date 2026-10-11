/*
 * MeTube for Jellyfin: client script, injected into Jellyfin Web's index.html.
 *
 * Adds a "YouTube" entry (side menu + header icon) for users the server says are
 * allowed, and opens a full-screen overlay to search YouTube and add videos.
 * Everything goes through this plugin's own /MeTube endpoints with the user's
 * own Jellyfin token; the script holds no configuration or secrets.
 *
 * Plain ES5 and conservative CSS on purpose: jellyfin-web also runs in older TV
 * browsers (webOS 4-6 is Chromium 53-79), so no arrow functions, no `inset`, no
 * flex `gap`.
 */
(function () {
    'use strict';

    if (window.MeTubeForJellyfin) {
        return;
    }

    var VERSION = '0.1.2';
    var ENTRY_ATTR = 'data-metube-entry';
    var ICON = 'ondemand_video';
    var POLL_ACTIVE_MS = 4000;
    var POLL_IDLE_MS = 20000;
    var OPEN_STATES = { Queued: true, Downloading: true, Finished: true };

    var state = {
        me: null,
        meUser: null,
        checking: false,
        retryAt: 0,
        overlay: null,
        historyPushed: false,
        pendingNav: null,
        pollTimer: null,
        query: '',
        continuation: null,
        searching: false,
        items: [],
        jobs: [],
        jobsSig: '',
        jobsByVideo: {},
        otherJobs: {},
        addState: {},
        tab: 'search',
        source: null
    };

    // ---------------------------------------------------------------- i18n

    var lang = (navigator.language || 'en').toLowerCase().indexOf('nl') === 0 ? 'nl' : 'en';
    var STRINGS = {
        en: {
            search: 'Search', downloads: 'Downloads', close: 'Close',
            placeholder: 'Search YouTube or paste a link',
            add: 'Add', adding: 'Adding…', queued: 'Queued', downloading: 'Downloading {0}%',
            processing: 'Processing…', play: 'Play', inLibrary: 'In library',
            live: 'Live stream', upcoming: 'Not yet published', tooLong: 'Too long',
            failed: 'Failed', retry: 'Try again', missing: 'MeTube lost this download',
            removed: 'Removed from the library',
            downloaded: 'Downloaded; not visible in Jellyfin yet',
            loadMore: 'Load more', noResults: 'No videos found.', searching: 'Searching…',
            notConfigured: 'An administrator still has to finish the MeTube plugin settings.',
            noLibraryAccess: 'You have no access to the YouTube library, so you will not see videos you add. Ask an administrator.',
            linkVideo: 'YouTube video {0}', emptyDownloads: 'Nothing added yet.',
            fallback: 'Showing simplified results: YouTube search is not available right now.',
            by: 'by {0}', error: 'Something went wrong.'
        },
        nl: {
            search: 'Zoeken', downloads: 'Downloads', close: 'Sluiten',
            placeholder: 'Zoek op YouTube of plak een link',
            add: 'Toevoegen', adding: 'Toevoegen…', queued: 'In wachtrij', downloading: 'Downloaden {0}%',
            processing: 'Verwerken…', play: 'Afspelen', inLibrary: 'In bibliotheek',
            live: 'Livestream', upcoming: 'Nog niet verschenen', tooLong: 'Te lang',
            failed: 'Mislukt', retry: 'Opnieuw', missing: 'MeTube is deze download kwijt',
            removed: 'Uit de bibliotheek verwijderd',
            downloaded: 'Gedownload; nog niet zichtbaar in Jellyfin',
            loadMore: 'Meer laden', noResults: 'Geen video\'s gevonden.', searching: 'Zoeken…',
            notConfigured: 'Een beheerder moet de instellingen van de MeTube-plugin nog afronden.',
            noLibraryAccess: 'Je hebt geen toegang tot de YouTube-bibliotheek, dus je ziet toegevoegde video\'s niet. Vraag het een beheerder.',
            linkVideo: 'YouTube-video {0}', emptyDownloads: 'Nog niets toegevoegd.',
            fallback: 'Vereenvoudigde resultaten: zoeken via YouTube lukt nu niet.',
            by: 'door {0}', error: 'Er ging iets mis.'
        }
    };

    function t(key) {
        var text = (STRINGS[lang] && STRINGS[lang][key]) || STRINGS.en[key] || key;
        for (var i = 1; i < arguments.length; i++) {
            text = text.replace('{' + (i - 1) + '}', String(arguments[i]));
        }
        return text;
    }

    // ---------------------------------------------------------------- helpers

    function pick(obj, name) {
        if (!obj) {
            return undefined;
        }
        if (obj[name] !== undefined) {
            return obj[name];
        }
        return obj[name.charAt(0).toLowerCase() + name.slice(1)];
    }

    function el(tag, attrs, children) {
        var node = document.createElement(tag);
        if (attrs) {
            for (var key in attrs) {
                if (!Object.prototype.hasOwnProperty.call(attrs, key) || attrs[key] === null || attrs[key] === undefined) {
                    continue;
                }
                if (key === 'text') {
                    node.textContent = attrs[key];
                } else if (key === 'className') {
                    node.className = attrs[key];
                } else if (key.indexOf('on') === 0) {
                    node.addEventListener(key.substring(2), attrs[key]);
                } else {
                    node.setAttribute(key, attrs[key]);
                }
            }
        }
        if (children) {
            for (var i = 0; i < children.length; i++) {
                if (children[i]) {
                    node.appendChild(typeof children[i] === 'string' ? document.createTextNode(children[i]) : children[i]);
                }
            }
        }
        return node;
    }

    function icon(name) {
        return el('span', { className: 'material-icons', 'aria-hidden': 'true', text: name });
    }

    function ready() {
        return !!window.ApiClient && !!document.body;
    }

    function token() {
        try {
            return window.ApiClient && ApiClient.accessToken ? ApiClient.accessToken() : null;
        } catch (e) {
            return null;
        }
    }

    function currentUserId() {
        try {
            return window.ApiClient && ApiClient.getCurrentUserId ? ApiClient.getCurrentUserId() : null;
        } catch (e) {
            return null;
        }
    }

    // Native fetch with the user's token rather than ApiClient.ajax: a 403 or 429
    // from this plugin must stay a plain error message, not trigger jellyfin-web's
    // global request-failure handling.
    function request(method, path, body, query) {
        var headers = {
            'Accept': 'application/json',
            'Authorization': 'MediaBrowser Token="' + token() + '"'
        };
        var options = { method: method, headers: headers };
        if (body !== undefined) {
            headers['Content-Type'] = 'application/json';
            options.body = JSON.stringify(body);
        }
        return fetch(ApiClient.getUrl(path, query), options).then(function (response) {
            return response.text().then(function (text) {
                var data = null;
                try {
                    data = text ? JSON.parse(text) : null;
                } catch (e) {
                    data = null;
                }
                if (!response.ok) {
                    var error = new Error(pick(data, 'Error') || t('error'));
                    error.status = response.status;
                    throw error;
                }
                return data;
            });
        });
    }

    function extractVideoId(text) {
        var match = /(?:youtube\.com\/(?:watch\?(?:[^#]*&)?v=|shorts\/|live\/|embed\/)|youtu\.be\/)([A-Za-z0-9_-]{11})(?![A-Za-z0-9_-])/.exec(text || '');
        return match ? match[1] : null;
    }

    function isOpenJob(job) {
        return !!job && OPEN_STATES[pick(job, 'State')] === true;
    }

    // ---------------------------------------------------------------- access

    function allowed() {
        return !!state.me && pick(state.me, 'Allowed') === true && state.meUser === currentUserId();
    }

    function checkMe() {
        var userId = currentUserId();
        if (!userId || !token()) {
            state.me = null;
            state.meUser = null;
            return;
        }
        if (state.checking || (state.meUser === userId && state.me) || Date.now() < state.retryAt) {
            return;
        }
        state.checking = true;
        request('GET', 'MeTube/Me').then(function (me) {
            state.checking = false;
            state.me = me;
            state.meUser = userId;
            scheduleScan();
        }).catch(function (error) {
            state.checking = false;
            state.me = null;
            state.meUser = null;
            // 401: not signed in yet; anything else: plugin unavailable. Back off
            // instead of asking on every DOM change.
            state.retryAt = Date.now() + (error && error.status === 401 ? 3000 : 60000);
        });
    }

    // Another user signed in on this tab (jellyfin-web logs out without a reload):
    // forget everything the previous user saw.
    function resetForNewUser() {
        close(true);
        state.me = null;
        state.meUser = null;
        state.retryAt = 0;
        state.query = '';
        state.items = [];
        state.continuation = null;
        state.source = null;
        state.jobs = [];
        state.jobsSig = '';
        state.jobsByVideo = {};
        state.otherJobs = {};
        state.addState = {};
        state.tab = 'search';
    }

    // ---------------------------------------------------------------- entries

    function removeEntries() {
        var nodes = document.querySelectorAll('[' + ENTRY_ATTR + ']');
        for (var i = 0; i < nodes.length; i++) {
            nodes[i].parentNode.removeChild(nodes[i]);
        }
    }

    // No stopPropagation: letting the click bubble is what closes the drawer the
    // entry sits in (the modern drawer's own onClick, legacy libraryMenu's handler).
    function onEntryClick(event) {
        event.preventDefault();
        open();
    }

    // Modern layout (default since 12.0): clone the Favorites row of the side menu.
    function injectModernDrawer() {
        var favorites = document.querySelector('a[href="#/home?tab=1"], a[href$="/home?tab=1"]');
        var row = favorites && favorites.closest ? favorites.closest('li') : null;
        if (!row || !row.parentNode) {
            return;
        }
        if (row.parentNode.querySelector('[' + ENTRY_ATTR + '="drawer"]')) {
            return;
        }
        var clone = row.cloneNode(true);
        clone.setAttribute(ENTRY_ATTR, 'drawer');
        var link = clone.querySelector('a');
        if (!link) {
            return;
        }
        link.removeAttribute('href');
        link.setAttribute('role', 'button');
        link.setAttribute('tabindex', '0');
        link.classList.remove('Mui-selected');
        link.removeAttribute('aria-current');
        var iconHost = link.querySelector('.MuiListItemIcon-root');
        if (iconHost) {
            iconHost.textContent = '';
            iconHost.appendChild(el('span', { className: 'material-icons MuiIcon-root', 'aria-hidden': 'true', text: ICON }));
        }
        var label = link.querySelector('.MuiListItemText-primary') || link.querySelector('.MuiTypography-root');
        if (label) {
            label.textContent = 'YouTube';
        }
        link.addEventListener('click', onEntryClick);
        link.addEventListener('keydown', function (event) {
            if (event.key === 'Enter' || event.key === ' ') {
                onEntryClick(event);
            }
        });
        row.parentNode.insertBefore(clone, row.nextSibling);
    }

    // Modern layout header: an icon button styled like the Search button next to it.
    function injectModernHeader() {
        var search = document.querySelector('.MuiToolbar-root a[href*="/search"]');
        if (!search || !search.parentNode || search.parentNode.querySelector('[' + ENTRY_ATTR + '="header"]')) {
            return;
        }
        var button = el('button', {
            type: 'button',
            className: search.className.replace(/\bMui-disabled\b/g, ''),
            title: 'YouTube',
            'aria-label': 'YouTube',
            onclick: onEntryClick
        }, [el('span', { className: 'material-icons MuiIcon-root', 'aria-hidden': 'true', text: ICON })]);
        button.setAttribute(ENTRY_ATTR, 'header');
        search.parentNode.insertBefore(button, search);
    }

    // Legacy layouts (desktop-legacy, tv): header button and side-menu link.
    function injectLegacy() {
        var searchButton = document.querySelector('.headerRight .headerSearchButton');
        if (searchButton && searchButton.parentNode && !searchButton.parentNode.querySelector('[' + ENTRY_ATTR + '="legacy-header"]')) {
            var button = el('button', {
                type: 'button',
                className: 'headerButton headerButtonRight paper-icon-button-light',
                title: 'YouTube',
                'aria-label': 'YouTube',
                onclick: onEntryClick
            }, [icon(ICON)]);
            button.setAttribute(ENTRY_ATTR, 'legacy-header');
            searchButton.parentNode.insertBefore(button, searchButton);
        }

        var home = document.querySelector('.mainDrawer a.navMenuOption[href="#/home"]');
        if (home && home.parentNode && !home.parentNode.querySelector('[' + ENTRY_ATTR + '="legacy-drawer"]')) {
            var link = el('a', { className: 'navMenuOption lnkMediaFolder', href: '#', onclick: onEntryClick }, [
                el('span', { className: 'material-icons navMenuOptionIcon', 'aria-hidden': 'true', text: ICON }),
                el('span', { className: 'navMenuOptionText', text: 'YouTube' })
            ]);
            link.setAttribute(ENTRY_ATTR, 'legacy-drawer');
            home.parentNode.insertBefore(link, home.nextSibling);
        }
    }

    var scanQueued = false;

    function scheduleScan() {
        if (scanQueued) {
            return;
        }
        scanQueued = true;
        // A short timer rather than requestAnimationFrame, which never fires while
        // the page is hidden (a background tab, an app WebView not on screen).
        window.setTimeout(function () {
            scanQueued = false;
            try {
                scan();
            } catch (e) {
                // Best effort only; never break the web client.
            }
        }, 50);
    }

    function scan() {
        if (!ready()) {
            return;
        }
        var userId = currentUserId();
        if (state.meUser && state.meUser !== userId) {
            resetForNewUser();
        }
        if (!userId || state.meUser !== userId) {
            removeEntries();
            checkMe();
            return;
        }
        // Admins can switch the menu entries off; the script may still be loaded by
        // the settings page's "Open YouTube search" fallback.
        if (!allowed() || pick(state.me, 'InjectionEnabled') === false) {
            removeEntries();
            return;
        }
        injectModernDrawer();
        injectModernHeader();
        injectLegacy();
    }

    // ---------------------------------------------------------------- styles

    function ensureStyles() {
        if (document.getElementById('mtj-style')) {
            return;
        }
        var css = [
            '.mtj-overlay{position:fixed;top:0;right:0;bottom:0;left:0;z-index:2000;display:flex;flex-direction:column;background:#101010;color:#fff;font-family:inherit;}',
            '.mtj-overlay [hidden]{display:none !important;}',
            '.mtj-bar{display:flex;align-items:center;padding:8px 12px;background:#1c1c1c;box-shadow:0 2px 6px rgba(0,0,0,.4);flex-wrap:wrap;}',
            '.mtj-bar>*+*{margin-left:8px;}',
            '.mtj-bar .mtj-title{font-size:1.2em;font-weight:600;margin-right:12px;}',
            '.mtj-tabs{display:flex;flex:1;}',
            '.mtj-tabs>*+*{margin-left:4px;}',
            '.mtj-tab{background:none;border:0;color:#bbb;padding:8px 12px;font:inherit;cursor:pointer;border-bottom:2px solid transparent;}',
            '.mtj-tab.mtj-active{color:#fff;border-bottom-color:#00a4dc;}',
            '.mtj-tab:focus{outline:2px solid #00a4dc;}',
            '.mtj-iconbtn{background:none;border:0;color:#fff;cursor:pointer;padding:6px;border-radius:50%;line-height:0;}',
            '.mtj-iconbtn:hover,.mtj-iconbtn:focus{background:rgba(255,255,255,.12);outline:none;}',
            '.mtj-body{flex:1;overflow-y:auto;-webkit-overflow-scrolling:touch;padding:16px;}',
            '.mtj-form{display:flex;max-width:900px;margin:0 auto 16px;}',
            '.mtj-form>*+*{margin-left:8px;}',
            '.mtj-input{flex:1;min-width:0;padding:10px 12px;font:inherit;font-size:1em;color:#fff;background:#262626;border:1px solid #3a3a3a;border-radius:6px;}',
            '.mtj-input:focus{outline:none;border-color:#00a4dc;}',
            '.mtj-btn{padding:8px 14px;font:inherit;color:#fff;background:#00a4dc;border:0;border-radius:6px;cursor:pointer;white-space:nowrap;}',
            '.mtj-btn:hover,.mtj-btn:focus{filter:brightness(1.12);outline:2px solid #fff;outline-offset:1px;}',
            '.mtj-btn[disabled]{background:#333;color:#999;cursor:default;filter:none;outline:none;}',
            '.mtj-btn.mtj-secondary{background:#333;}',
            '.mtj-msg{max-width:900px;margin:0 auto 12px;color:#bbb;}',
            '.mtj-msg.mtj-err{color:#ff8a80;}',
            '.mtj-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));grid-gap:16px;gap:16px;}',
            '.mtj-card{background:#1c1c1c;border-radius:8px;overflow:hidden;display:flex;flex-direction:column;margin-bottom:0;}',
            '.mtj-thumb{position:relative;padding-top:56.25%;background:#000;}',
            '.mtj-thumb img{position:absolute;top:0;left:0;width:100%;height:100%;object-fit:cover;}',
            '.mtj-dur{position:absolute;right:6px;bottom:6px;padding:1px 5px;font-size:.8em;background:rgba(0,0,0,.8);border-radius:3px;}',
            '.mtj-info{padding:10px 10px 4px;flex:1;}',
            '.mtj-name{font-weight:600;line-height:1.3;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden;}',
            '.mtj-meta{margin-top:4px;font-size:.85em;color:#aaa;}',
            '.mtj-actions{display:flex;align-items:center;padding:8px 10px 10px;flex-wrap:wrap;}',
            '.mtj-actions>*+*{margin-left:8px;}',
            '.mtj-note{font-size:.85em;color:#aaa;}',
            '.mtj-note.mtj-err{color:#ff8a80;}',
            '.mtj-more{display:block;margin:16px auto 0;}',
            '.mtj-row{display:flex;align-items:center;background:#1c1c1c;border-radius:8px;padding:8px;margin:0 auto 8px;max-width:900px;}',
            '.mtj-row>*+*{margin-left:12px;}',
            '.mtj-row img{width:120px;height:68px;object-fit:cover;border-radius:4px;flex:none;background:#000;}',
            '.mtj-row .mtj-info{padding:0;min-width:0;}',
            '.mtj-row .mtj-actions{padding:0;margin-left:auto;}',
            '@media (max-width:600px){.mtj-body{padding:12px;}.mtj-grid{grid-template-columns:1fr;}.mtj-row img{width:96px;height:54px;}}'
        ].join('\n');
        document.head.appendChild(el('style', { id: 'mtj-style', text: css }));
    }

    // ---------------------------------------------------------------- overlay

    var BACK_KEYS = { Escape: true, Esc: true, GoBack: true, BrowserBack: true };
    var BACK_KEY_CODES = { 27: true, 461: true, 10009: true };

    // Registered on window in the capture phase while the overlay is open, so it runs
    // before jellyfin-web's own keyboard handling; preventDefault makes that handling
    // skip the key, so one Escape is one Back.
    function onOverlayKey(event) {
        if (!state.overlay) {
            return;
        }
        if (BACK_KEYS[event.key] === true || BACK_KEY_CODES[event.keyCode] === true) {
            event.preventDefault();
            event.stopPropagation();
            close();
        }
    }

    function open() {
        if (!ready()) {
            return;
        }
        if (!allowed()) {
            // Settings-page fallback: ask the server first, then open.
            state.retryAt = 0;
            request('GET', 'MeTube/Me').then(function (me) {
                state.me = me;
                state.meUser = currentUserId();
                if (allowed()) {
                    open();
                }
            }).catch(function () { /* not allowed or unavailable */ });
            return;
        }
        if (state.overlay) {
            return;
        }
        ensureStyles();
        state.overlay = buildOverlay();
        document.body.appendChild(state.overlay);
        document.documentElement.style.overflow = 'hidden';
        window.addEventListener('keydown', onOverlayKey, true);
        try {
            history.pushState({ metubeOverlay: true }, '', location.href);
            state.historyPushed = true;
            // jellyfin-web's back logic (Android app back button, TV remotes) exits
            // the app on Home unless it sees an open dialog. This marker makes it go
            // back one history entry instead, which closes the overlay.
            state.overlay.appendChild(el('div', { className: 'dialogContainer', style: 'display:none', 'aria-hidden': 'true' }));
        } catch (e) {
            state.historyPushed = false;
        }
        showTab(state.tab);
        renderNotices();
        renderResults();
        refreshJobs();
        var input = state.overlay.querySelector('.mtj-input');
        if (input) {
            input.value = state.query;
        }
        // After the drawer that was clicked has closed and handed focus back.
        window.setTimeout(function () {
            var field = state.overlay && state.overlay.querySelector('.mtj-input');
            if (field) {
                field.focus();
            }
        }, 60);
    }

    function close(fromHistory) {
        if (!state.overlay) {
            return;
        }
        window.clearTimeout(state.pollTimer);
        state.pollTimer = null;
        window.removeEventListener('keydown', onOverlayKey, true);
        state.overlay.parentNode.removeChild(state.overlay);
        state.overlay = null;
        document.documentElement.style.overflow = '';
        var pushed = state.historyPushed;
        state.historyPushed = false;
        if (pushed && !fromHistory) {
            try {
                history.back();
            } catch (e) {
                // ignore
            }
        }
    }

    function buildOverlay() {
        var tabs = el('div', { className: 'mtj-tabs' }, [
            el('button', { type: 'button', className: 'mtj-tab', 'data-tab': 'search', text: t('search'), onclick: function () { showTab('search'); } }),
            el('button', { type: 'button', className: 'mtj-tab', 'data-tab': 'downloads', text: t('downloads'), onclick: function () { showTab('downloads'); refreshJobs(); } })
        ]);
        var bar = el('div', { className: 'mtj-bar' }, [
            icon(ICON),
            el('span', { className: 'mtj-title', text: 'YouTube' }),
            tabs,
            el('button', { type: 'button', className: 'mtj-iconbtn', title: t('close'), 'aria-label': t('close'), onclick: function () { close(); } }, [icon('close')])
        ]);

        var input = el('input', { type: 'search', className: 'mtj-input', placeholder: t('placeholder'), maxlength: '300', 'aria-label': t('placeholder'), enterkeyhint: 'search' });
        var form = el('form', { className: 'mtj-form', onsubmit: function (event) { event.preventDefault(); startSearch(input.value); } }, [
            input,
            el('button', { type: 'submit', className: 'mtj-btn', text: t('search') })
        ]);
        var searchPanel = el('section', { 'data-panel': 'search' }, [
            form,
            el('div', { className: 'mtj-msg', 'data-role': 'notice' }),
            el('div', { className: 'mtj-msg', 'data-role': 'search-msg' }),
            el('div', { className: 'mtj-grid', 'data-role': 'grid' }),
            el('button', { type: 'button', className: 'mtj-btn mtj-secondary mtj-more', text: t('loadMore'), hidden: 'hidden', onclick: loadMore })
        ]);
        var downloadsPanel = el('section', { 'data-panel': 'downloads', hidden: 'hidden' }, [
            el('div', { className: 'mtj-msg', 'data-role': 'downloads-msg' }),
            el('div', { 'data-role': 'list' })
        ]);
        // "focuscontainer" keeps TV D-pad navigation inside the overlay instead of
        // wandering to the (covered) page behind it.
        return el('div', { className: 'mtj-overlay focuscontainer', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'YouTube' }, [
            bar,
            el('div', { className: 'mtj-body' }, [searchPanel, downloadsPanel])
        ]);
    }

    function part(role) {
        return state.overlay ? state.overlay.querySelector('[data-role="' + role + '"]') : null;
    }

    function showTab(tab) {
        state.tab = tab;
        if (!state.overlay) {
            return;
        }
        var tabs = state.overlay.querySelectorAll('.mtj-tab');
        for (var i = 0; i < tabs.length; i++) {
            if (tabs[i].getAttribute('data-tab') === tab) {
                tabs[i].classList.add('mtj-active');
            } else {
                tabs[i].classList.remove('mtj-active');
            }
        }
        var panels = state.overlay.querySelectorAll('[data-panel]');
        for (var j = 0; j < panels.length; j++) {
            panels[j].hidden = panels[j].getAttribute('data-panel') !== tab;
        }
    }

    function renderNotices() {
        var notice = part('notice');
        if (!notice) {
            return;
        }
        var lines = [];
        if (pick(state.me, 'Configured') === false) {
            lines.push(t('notConfigured'));
        } else if (pick(state.me, 'CanPlay') === false) {
            lines.push(t('noLibraryAccess'));
        }
        if (state.source === 'metube') {
            lines.push(t('fallback'));
        }
        notice.textContent = lines.join(' ');
        notice.hidden = lines.length === 0;
    }

    function setMessage(role, text, isError) {
        var node = part(role);
        if (node) {
            node.textContent = text || '';
            if (isError) {
                node.classList.add('mtj-err');
            } else {
                node.classList.remove('mtj-err');
            }
            node.hidden = !text;
        }
    }

    // ---------------------------------------------------------------- search

    function startSearch(text) {
        var value = (text || '').trim();
        if (!value || state.searching) {
            return;
        }
        state.query = value;
        state.items = [];
        state.continuation = null;
        state.source = null;

        var linked = extractVideoId(value);
        if (linked) {
            state.items = [{ VideoId: linked, Title: t('linkVideo', linked), ThumbnailUrl: 'https://i.ytimg.com/vi/' + linked + '/mqdefault.jpg', Availability: 'Addable', Pasted: true }];
            setMessage('search-msg', '');
            renderNotices();
            renderResults();
            return;
        }

        runSearch({ q: value }, false);
    }

    function loadMore() {
        if (state.continuation) {
            runSearch({ continuation: state.continuation }, true);
        }
    }

    function runSearch(query, append) {
        state.searching = true;
        setMessage('search-msg', t('searching'));
        var more = state.overlay && state.overlay.querySelector('.mtj-more');
        if (more) {
            more.disabled = true;
        }
        request('GET', 'MeTube/Search', undefined, query).then(function (data) {
            state.searching = false;
            var items = pick(data, 'Items') || [];
            state.items = append ? state.items.concat(items) : items;
            state.continuation = pick(data, 'Continuation') || null;
            state.source = pick(data, 'Source') || null;
            setMessage('search-msg', state.items.length ? '' : t('noResults'));
            renderNotices();
            renderResults();
            refreshOtherJobs();
        }).catch(function (error) {
            state.searching = false;
            setMessage('search-msg', error.message, true);
            renderResults();
        });
    }

    function renderResults() {
        var grid = part('grid');
        if (!grid) {
            return;
        }
        grid.textContent = '';
        for (var i = 0; i < state.items.length; i++) {
            grid.appendChild(buildCard(state.items[i]));
        }
        var more = state.overlay.querySelector('.mtj-more');
        if (more) {
            more.hidden = !state.continuation;
            more.disabled = state.searching;
        }
    }

    function buildCard(item) {
        var videoId = pick(item, 'VideoId');
        var meta = [];
        if (pick(item, 'Channel')) {
            meta.push(pick(item, 'Channel'));
        }
        if (pick(item, 'ViewsText')) {
            meta.push(pick(item, 'ViewsText'));
        }
        if (pick(item, 'PublishedText')) {
            meta.push(pick(item, 'PublishedText'));
        }
        var thumb = el('div', { className: 'mtj-thumb' }, [
            el('img', { src: pick(item, 'ThumbnailUrl'), alt: '', loading: 'lazy', referrerpolicy: 'no-referrer' }),
            pick(item, 'DurationText') ? el('span', { className: 'mtj-dur', text: pick(item, 'DurationText') }) : null
        ]);
        var card = el('div', { className: 'mtj-card', 'data-video-id': videoId }, [
            thumb,
            el('div', { className: 'mtj-info' }, [
                el('div', { className: 'mtj-name', title: pick(item, 'Title'), text: pick(item, 'Title') }),
                meta.length ? el('div', { className: 'mtj-meta', text: meta.join(' · ') }) : null
            ]),
            el('div', { className: 'mtj-actions' })
        ]);
        renderCardActions(card, item);
        return card;
    }

    // Decides what a card (or a Downloads row) shows. The job wins while it is open.
    // Once closed, the server's current answer (search or add) decides, so a late
    // arrival in the library still gets its Play button.
    function decide(item) {
        var videoId = pick(item, 'VideoId');
        var job = item.OwnJob ? pick(item, 'Job') : (state.jobsByVideo[videoId] || state.otherJobs[videoId] || pick(item, 'Job'));
        var availability = pick(item, 'Availability');
        var view = { kind: 'add', itemId: pick(item, 'ItemId'), note: null, noteIsError: false, job: null };
        var jobState = job ? pick(job, 'State') : null;

        if (job && jobState === 'InLibrary' && pick(job, 'ItemId')) {
            view.kind = 'play';
            view.itemId = pick(job, 'ItemId');
            return view;
        }
        if (job && isOpenJob(job)) {
            view.kind = 'job';
            view.job = job;
            return view;
        }
        if (job && !availability) {
            // Downloads rows: only the job is known.
            if (jobState === 'Downloaded') {
                view.kind = 'downloaded';
                return view;
            }
            view.kind = 'retry';
            view.note = jobState === 'Missing' ? t('missing') : jobState === 'Removed' ? t('removed') : (pick(job, 'Error') || t('failed'));
            view.noteIsError = jobState !== 'Removed';
            return view;
        }
        if (job && availability === 'Addable' && (jobState === 'Failed' || jobState === 'Missing')) {
            view.note = jobState === 'Missing' ? t('missing') : (pick(job, 'Error') || t('failed'));
            view.noteIsError = true;
        }
        if (availability === 'InLibrary') {
            view.kind = view.itemId ? 'play' : 'inlibrary';
        } else if (availability === 'NotAddable') {
            var reason = pick(item, 'Reason');
            view.kind = 'blocked';
            view.note = reason === 'Live' ? t('live') : reason === 'Upcoming' ? t('upcoming') : t('tooLong');
        } else if (availability === 'InProgress') {
            // The search said someone is downloading it, but that job is not known
            // here (yet): show it as queued until the status poll catches up.
            view.kind = 'job';
            view.job = pick(item, 'Job') || { State: 'Queued' };
        }
        return view;
    }

    function signature(item) {
        var videoId = pick(item, 'VideoId');
        var view = decide(item);
        var add = state.addState[videoId] || {};
        var job = view.job;
        return [view.kind, view.itemId || '', view.note || '', job ? pick(job, 'State') : '', job ? Math.round(pick(job, 'Percent') || 0) : '', add.adding ? 1 : 0, add.error || ''].join('|');
    }

    function renderCardActions(card, item) {
        var actions = card.querySelector('.mtj-actions');
        var sig = signature(item);
        if (card.getAttribute('data-sig') === sig) {
            return;
        }
        card.setAttribute('data-sig', sig);
        var hadFocus = actions.contains(document.activeElement);
        actions.textContent = '';
        fillActions(actions, item);
        if (hadFocus) {
            // Keep D-pad/keyboard focus inside the overlay when a poll re-renders.
            var target = actions.querySelector('button:not([disabled])') || (state.overlay && state.overlay.querySelector('.mtj-input'));
            if (target) {
                target.focus();
            }
        }
    }

    function fillActions(actions, item) {
        var videoId = pick(item, 'VideoId');
        var add = state.addState[videoId] || {};
        if (add.adding) {
            actions.appendChild(disabledButton(t('adding')));
            return;
        }
        var view = decide(item);
        var note = add.error || view.note;
        var noteIsError = add.error ? true : view.noteIsError;

        if (view.kind === 'play') {
            actions.appendChild(el('button', { type: 'button', className: 'mtj-btn', onclick: function () { play(view.itemId); } }, [t('play')]));
            return;
        }
        if (view.kind === 'job') {
            var jobState = pick(view.job, 'State');
            if (jobState === 'Downloading') {
                actions.appendChild(disabledButton(t('downloading', Math.round(pick(view.job, 'Percent') || 0))));
            } else if (jobState === 'Finished') {
                actions.appendChild(disabledButton(t('processing')));
            } else {
                actions.appendChild(disabledButton(t('queued')));
            }
            return;
        }
        if (view.kind === 'downloaded') {
            actions.appendChild(disabledButton(t('inLibrary')));
            actions.appendChild(el('span', { className: 'mtj-note', text: t('downloaded') }));
            return;
        }
        if (view.kind === 'inlibrary') {
            actions.appendChild(disabledButton(t('inLibrary')));
            return;
        }
        if (view.kind === 'blocked') {
            actions.appendChild(disabledButton(view.note));
            return;
        }
        actions.appendChild(addButton(videoId, view.kind === 'retry' ? t('retry') : t('add')));
        if (note) {
            actions.appendChild(el('span', { className: noteIsError ? 'mtj-note mtj-err' : 'mtj-note', text: note }));
        }
    }

    function disabledButton(text) {
        return el('button', { type: 'button', className: 'mtj-btn', disabled: 'disabled', text: text });
    }

    function addButton(videoId, label) {
        return el('button', { type: 'button', className: 'mtj-btn', text: label, onclick: function () { add(videoId); } });
    }

    function rerender() {
        if (!state.overlay) {
            return;
        }
        for (var i = 0; i < state.items.length; i++) {
            var card = state.overlay.querySelector('.mtj-card[data-video-id="' + pick(state.items[i], 'VideoId') + '"]');
            if (card) {
                renderCardActions(card, state.items[i]);
            }
        }
        renderDownloads(false);
    }

    function setAvailability(videoId, availability, itemId) {
        for (var i = 0; i < state.items.length; i++) {
            if (pick(state.items[i], 'VideoId') === videoId) {
                state.items[i].Availability = availability;
                state.items[i].ItemId = itemId || null;
            }
        }
    }

    function add(videoId) {
        if (state.addState[videoId] && state.addState[videoId].adding) {
            return;
        }
        state.addState[videoId] = { adding: true };
        rerender();
        // Always the id: the server rebuilds the URL itself, and a pasted text with
        // extra words around the link would not parse as a URL anyway.
        request('POST', 'MeTube/Add', { VideoId: videoId }).then(function (result) {
            delete state.addState[videoId];
            if (pick(result, 'AlreadyInLibrary')) {
                setAvailability(videoId, 'InLibrary', pick(result, 'ItemId'));
                delete state.jobsByVideo[videoId];
                delete state.otherJobs[videoId];
            }
            var job = pick(result, 'Job');
            if (job) {
                state.jobsByVideo[videoId] = job;
                for (var i = 0; i < state.items.length; i++) {
                    if (state.items[i].Pasted && pick(state.items[i], 'VideoId') === videoId && pick(job, 'Title')) {
                        state.items[i].Title = pick(job, 'Title');
                        renderResults();
                    }
                }
            }
            rerender();
            schedulePoll(1500);
        }).catch(function (error) {
            state.addState[videoId] = { error: error.message };
            rerender();
        });
    }

    function play(itemId) {
        var target = '#/details?id=' + encodeURIComponent(itemId);
        if (state.historyPushed) {
            // Navigate only after close()'s history.back() has landed, otherwise the
            // back traversal can undo the navigation on a slow device.
            state.pendingNav = target;
            close();
            window.setTimeout(flushPendingNav, 1000);
        } else {
            close();
            location.hash = target;
        }
    }

    function flushPendingNav() {
        if (state.pendingNav && !state.overlay) {
            var target = state.pendingNav;
            state.pendingNav = null;
            location.hash = target;
        }
    }

    // ---------------------------------------------------------------- downloads

    function refreshJobs() {
        if (!state.overlay) {
            return;
        }
        request('GET', 'MeTube/Status').then(function (jobs) {
            state.jobs = jobs || [];
            state.jobsByVideo = {};
            // Newest first from the server; keep the newest job per video.
            for (var i = state.jobs.length - 1; i >= 0; i--) {
                state.jobsByVideo[pick(state.jobs[i], 'VideoId')] = state.jobs[i];
            }
            return refreshOtherJobs();
        }).then(function () {
            rerender();
            schedulePoll(anyOpenJobs() ? POLL_ACTIVE_MS : POLL_IDLE_MS);
        }).catch(function (error) {
            setMessage('downloads-msg', error.message, true);
            schedulePoll(POLL_IDLE_MS);
        });
    }

    // Search cards can show downloads another user started; those are not in this
    // user's own list, so ask for them by video id.
    function refreshOtherJobs() {
        var ids = [];
        for (var i = 0; i < state.items.length; i++) {
            var videoId = pick(state.items[i], 'VideoId');
            if (pick(state.items[i], 'Availability') === 'InProgress' && !state.jobsByVideo[videoId]) {
                ids.push(videoId);
            }
        }
        if (!ids.length) {
            state.otherJobs = {};
            return Promise.resolve();
        }
        return request('GET', 'MeTube/Status', undefined, { videoIds: ids.join(',') }).then(function (jobs) {
            state.otherJobs = {};
            for (var j = 0; j < (jobs || []).length; j++) {
                state.otherJobs[pick(jobs[j], 'VideoId')] = jobs[j];
            }
            rerender();
        }).catch(function () { /* keep the snapshot from the search */ });
    }

    // Poll fast while anything shown is still on its way: own jobs, other users'
    // jobs, and search cards that only have the snapshot from the search answer.
    function anyOpenJobs() {
        var key;
        for (var i = 0; i < state.jobs.length; i++) {
            if (isOpenJob(state.jobs[i])) {
                return true;
            }
        }
        for (key in state.otherJobs) {
            if (Object.prototype.hasOwnProperty.call(state.otherJobs, key) && isOpenJob(state.otherJobs[key])) {
                return true;
            }
        }
        for (var j = 0; j < state.items.length; j++) {
            if (decide(state.items[j]).kind === 'job') {
                return true;
            }
        }
        return false;
    }

    function schedulePoll(delay) {
        window.clearTimeout(state.pollTimer);
        if (state.overlay) {
            state.pollTimer = window.setTimeout(refreshJobs, delay);
        }
    }

    function renderDownloads(force) {
        var list = part('list');
        if (!list) {
            return;
        }
        var sig = JSON.stringify(state.jobs) + JSON.stringify(state.addState);
        if (!force && sig === state.jobsSig && list.childNodes.length) {
            return;
        }
        state.jobsSig = sig;

        var rows = list.querySelectorAll('.mtj-row');
        var focusedIndex = -1;
        for (var r = 0; r < rows.length; r++) {
            if (rows[r].contains(document.activeElement)) {
                focusedIndex = r;
            }
        }

        list.textContent = '';
        setMessage('downloads-msg', state.jobs.length ? '' : t('emptyDownloads'));
        var showUser = pick(state.me, 'IsAdmin') === true;
        for (var i = 0; i < state.jobs.length; i++) {
            var job = state.jobs[i];
            var meta = [];
            if (pick(job, 'Channel')) {
                meta.push(pick(job, 'Channel'));
            }
            if (showUser && pick(job, 'UserName')) {
                meta.push(t('by', pick(job, 'UserName')));
            }
            var created = pick(job, 'CreatedUtc');
            if (created) {
                var date = new Date(created);
                if (!isNaN(date.getTime())) {
                    meta.push(date.toLocaleString());
                }
            }
            var row = el('div', { className: 'mtj-row', 'data-video-id': pick(job, 'VideoId') }, [
                el('img', { src: pick(job, 'ThumbnailUrl'), alt: '', loading: 'lazy', referrerpolicy: 'no-referrer' }),
                el('div', { className: 'mtj-info' }, [
                    el('div', { className: 'mtj-name', title: pick(job, 'Title'), text: pick(job, 'Title') }),
                    el('div', { className: 'mtj-meta', text: meta.join(' · ') })
                ]),
                el('div', { className: 'mtj-actions' })
            ]);
            fillActions(row.querySelector('.mtj-actions'), { VideoId: pick(job, 'VideoId'), Job: job, OwnJob: true });
            list.appendChild(row);
        }

        if (focusedIndex >= 0) {
            var newRows = list.querySelectorAll('.mtj-row');
            var row2 = newRows[Math.min(focusedIndex, newRows.length - 1)];
            var target = (row2 && row2.querySelector('button:not([disabled])')) || state.overlay.querySelector('.mtj-tab.mtj-active');
            if (target) {
                target.focus();
            }
        }
    }

    // ---------------------------------------------------------------- boot

    function installHooks() {
        var observer = new MutationObserver(scheduleScan);
        observer.observe(document.body, { childList: true, subtree: true });
        window.addEventListener('hashchange', scheduleScan, true);
        window.addEventListener('popstate', function () {
            if (state.overlay && state.historyPushed) {
                // Back button (browser, Android app, TV remote) closes the overlay
                // instead of leaving the page.
                state.historyPushed = false;
                close(true);
            }
            if (state.pendingNav && !state.overlay) {
                window.setTimeout(flushPendingNav, 0);
            }
            scheduleScan();
        }, true);
        window.setInterval(scheduleScan, 3000);
    }

    function start() {
        if (!ready()) {
            window.setTimeout(start, 300);
            return;
        }
        installHooks();
        scheduleScan();
    }

    window.MeTubeForJellyfin = { version: VERSION, open: open, close: function () { close(); } };
    start();
})();
