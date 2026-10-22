/*
 * MeTube for Jellyfin: client script, injected into Jellyfin Web's index.html.
 *
 * Adds a "YouTube" entry (header icon, side menu) for users the server says are
 * allowed, and a YouTube page at #/youtube that lives inside Jellyfin's own layout:
 * the header and navigation stay, Back and the Android back button work, and the
 * page survives a reload (search, sort and filters are in the URL). Administrators
 * also get a Folders tab to merge channel folders, reachable from Jellyfin's own
 * selection menu too ("Merge folders" when two or more channel folders are selected).
 *
 * Jellyfin has no route for #/youtube, so its router shows a "page not found" view
 * there; this script hides that view with CSS and shows its own page in the same
 * page container instead. Everything goes through this plugin's /MeTube endpoints
 * with the user's own Jellyfin token; the script holds no configuration or secrets.
 *
 * Plain ES5 and conservative CSS on purpose: jellyfin-web also runs in older TV
 * browsers (webOS 4-6 is Chromium 53-79).
 */
(function () {
    'use strict';

    if (window.MeTubeForJellyfin) {
        return;
    }

    var VERSION = '0.5.2';
    var ROUTE_RE = /^#!?\/youtube(?:[\/?]|$)/i;
    var ENTRY_ATTR = 'data-metube-entry';
    // Material "smart_display": Jellyfin itself uses ondemand_video for mixed-content
    // libraries, so the YouTube entry must not. ICON_CODE is the same glyph's code point
    // in Jellyfin's icon font, for the CSS that gives the YouTube library this icon too.
    var ICON = 'smart_display';
    var ICON_CODE = '\\f06a';
    var POLL_ACTIVE_MS = 4000;
    var POLL_IDLE_MS = 20000;
    var OPEN_STATES = { Queued: true, Downloading: true, Finished: true };
    var SERVER_SORTS = { relevance: true, popularity: true };
    var LOCAL_ORDERS = { newest: true, shortest: true, longest: true };

    var state = {
        me: null,
        meUser: null,
        checking: false,
        retryAt: 0,
        page: null,
        visible: false,
        scrollY: 0,
        routeKey: null,
        query: '',
        filters: { sort: 'relevance', duration: 'any', date: 'any' },
        order: 'newest',
        continuation: null,
        searching: false,
        searchSeq: 0,
        items: [],
        jobs: [],
        jobsSig: '',
        jobsByVideo: {},
        otherJobs: {},
        addState: {},
        formatSaving: null,
        tab: 'search',
        source: null,
        pollTimer: null,
        folders: null,
        aliases: [],
        foldersLoading: false,
        folderSel: {},
        folderNotice: '',
        merge: null,
        mergeBusy: false,
        focusVideoId: null,
        pendingSelect: null,
        fromLibrary: false,
        dismissing: {}
    };

    // ---------------------------------------------------------------- i18n

    var lang = (navigator.language || 'en').toLowerCase().indexOf('nl') === 0 ? 'nl' : 'en';
    var STRINGS = {
        en: {
            search: 'Search', added: 'Added videos',
            placeholder: 'Search YouTube or paste a link',
            sortBy: 'Sort', length: 'Length', uploaded: 'Uploaded',
            relevance: 'Relevance', popularity: 'Popularity', newest: 'Newest first', shortest: 'Shortest first', longest: 'Longest first',
            anyLength: 'Any length', short: 'Under 3 minutes', medium: '3 to 20 minutes', long: 'Over 20 minutes',
            anyTime: 'Any time', today: 'Today', week: 'This week', month: 'This month', year: 'This year',
            localOrderNote: 'Sorted within the results loaded so far.',
            add: 'Add', adding: 'Adding…', queued: 'Queued', downloading: 'Downloading {0}%',
            processing: 'Processing…', play: 'Play', inLibrary: 'In library',
            live: 'Live stream', upcoming: 'Not yet published', tooLong: 'Too long',
            failed: 'Failed', retry: 'Try again', missing: 'MeTube lost this download',
            removed: 'Removed from the library', deleted: 'Deleted',
            downloaded: 'Downloaded; not visible in Jellyfin yet',
            del: 'Delete', deleting: 'Deleting…', deleteTitle: 'Delete video',
            deleteConfirm: 'Delete "{0}" from the library and from disk?',
            loadMore: 'Load more', noResults: 'No videos found.', searching: 'Searching…',
            notConfigured: 'An administrator still has to finish the MeTube plugin settings.',
            noLibraryAccess: 'You have no access to the YouTube library, so you will not see videos you add. Ask an administrator.',
            notAllowed: 'YouTube search is not enabled for your account.',
            loading: 'Loading…',
            linkVideo: 'YouTube video {0}', emptyAdded: 'Nothing added yet.',
            fallback: 'Showing simplified results: YouTube search is not available right now.',
            by: 'by {0}', error: 'Something went wrong.',
            folders: 'Folders',
            foldersIntro: 'Channel folders in the YouTube library. Select two or more and choose Merge to put all their videos in one folder.',
            videos: '{0} videos', video: '1 video', noFolders: 'No channel folders yet.', selected: '{0} selected',
            merge: 'Merge', merging: 'Merging…', cancel: 'Cancel',
            mergeKeep: 'Which folder do you want to keep? All videos go into it, and the other folders are removed.',
            mergeNote: 'New downloads from the merged channels will go into the kept folder too. Moved videos lose their watched status in Jellyfin.',
            mergeDone: 'Moved {0} video(s) into "{1}".',
            mergeSkipped: '{0} file(s) stayed where they were because "{1}" already has a file with the same name.',
            rescanNote: 'Jellyfin is updating the library; this can take a minute.',
            alsoReceives: 'Also gets new videos from: {0}',
            mergedChannels: 'Merged channels', stopRedirect: 'Stop redirecting',
            redirectNote: 'New downloads from the channel on the left go into the folder on the right.',
            mergeFolders: 'Merge folders',
            downloadAs: 'Download as', formatDefault: 'Server default', formatCompatible: 'Chromecast compatible (up to 1080p)', formatBest: 'Best quality (4K)',
            dismiss: 'Remove from list', cleanList: 'Clean up list', cleaning: 'Cleaning up…',
            selectionGone: 'The folders selected in the library were not found. They may have been merged or removed already.',
            backToLibrary: 'Back to the library'
        },
        nl: {
            search: 'Zoeken', added: 'Toegevoegde video\'s',
            placeholder: 'Zoek op YouTube of plak een link',
            sortBy: 'Sorteren', length: 'Lengte', uploaded: 'Geüpload',
            relevance: 'Relevantie', popularity: 'Populariteit', newest: 'Nieuwste eerst', shortest: 'Kortste eerst', longest: 'Langste eerst',
            anyLength: 'Elke lengte', short: 'Korter dan 3 minuten', medium: '3 tot 20 minuten', long: 'Langer dan 20 minuten',
            anyTime: 'Altijd', today: 'Vandaag', week: 'Deze week', month: 'Deze maand', year: 'Dit jaar',
            localOrderNote: 'Gesorteerd binnen de tot nu toe geladen resultaten.',
            add: 'Toevoegen', adding: 'Toevoegen…', queued: 'In wachtrij', downloading: 'Downloaden {0}%',
            processing: 'Verwerken…', play: 'Afspelen', inLibrary: 'In bibliotheek',
            live: 'Livestream', upcoming: 'Nog niet verschenen', tooLong: 'Te lang',
            failed: 'Mislukt', retry: 'Opnieuw', missing: 'MeTube is deze download kwijt',
            removed: 'Uit de bibliotheek verwijderd', deleted: 'Verwijderd',
            downloaded: 'Gedownload; nog niet zichtbaar in Jellyfin',
            del: 'Verwijderen', deleting: 'Verwijderen…', deleteTitle: 'Video verwijderen',
            deleteConfirm: '"{0}" uit de bibliotheek en van de schijf verwijderen?',
            loadMore: 'Meer laden', noResults: 'Geen video\'s gevonden.', searching: 'Zoeken…',
            notConfigured: 'Een beheerder moet de instellingen van de MeTube-plugin nog afronden.',
            noLibraryAccess: 'Je hebt geen toegang tot de YouTube-bibliotheek, dus je ziet toegevoegde video\'s niet. Vraag het een beheerder.',
            notAllowed: 'YouTube zoeken is niet ingeschakeld voor jouw account.',
            loading: 'Laden…',
            linkVideo: 'YouTube-video {0}', emptyAdded: 'Nog niets toegevoegd.',
            fallback: 'Vereenvoudigde resultaten: zoeken via YouTube lukt nu niet.',
            by: 'door {0}', error: 'Er ging iets mis.',
            folders: 'Mappen',
            foldersIntro: 'Kanaalmappen in de YouTube-bibliotheek. Selecteer er twee of meer en kies Samenvoegen om al hun video\'s in één map te zetten.',
            videos: '{0} video\'s', video: '1 video', noFolders: 'Nog geen kanaalmappen.', selected: '{0} geselecteerd',
            merge: 'Samenvoegen', merging: 'Samenvoegen…', cancel: 'Annuleren',
            mergeKeep: 'Welke map wil je houden? Alle video\'s gaan daarin en de andere mappen verdwijnen.',
            mergeNote: 'Nieuwe downloads van de samengevoegde kanalen komen voortaan ook in de behouden map. Verplaatste video\'s verliezen hun bekeken-status in Jellyfin.',
            mergeDone: '{0} video(\'s) verplaatst naar "{1}".',
            mergeSkipped: '{0} bestand(en) bleven staan omdat "{1}" al een bestand met dezelfde naam heeft.',
            rescanNote: 'Jellyfin werkt de bibliotheek bij; dat kan een minuut duren.',
            alsoReceives: 'Krijgt ook nieuwe video\'s van: {0}',
            mergedChannels: 'Samengevoegde kanalen', stopRedirect: 'Niet meer omleiden',
            redirectNote: 'Nieuwe downloads van het kanaal links komen in de map rechts.',
            mergeFolders: 'Mappen samenvoegen',
            downloadAs: 'Downloaden als', formatDefault: 'Standaard van de server', formatCompatible: 'Chromecast-compatibel (tot 1080p)', formatBest: 'Beste kwaliteit (4K)',
            dismiss: 'Uit lijst verwijderen', cleanList: 'Lijst opschonen', cleaning: 'Opschonen…',
            selectionGone: 'De mappen die in de bibliotheek waren geselecteerd zijn niet gevonden. Misschien zijn ze al samengevoegd of verwijderd.',
            backToLibrary: 'Terug naar de bibliotheek'
        }
    };

    function t(key) {
        var text = (STRINGS[lang] && STRINGS[lang][key]) || STRINGS.en[key] || key;
        for (var i = 1; i < arguments.length; i++) {
            text = text.replace('{' + (i - 1) + '}', literal(String(arguments[i])));
        }
        return text;
    }

    // A replacement function, so "$&" or "$'" in a video title is not a pattern.
    function literal(value) {
        return function () {
            return value;
        };
    }

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text === null || text === undefined ? '' : String(text);
        return div.innerHTML;
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
                if (!Object.prototype.hasOwnProperty.call(attrs, key) || attrs[key] === null || attrs[key] === undefined || attrs[key] === false) {
                    continue;
                }
                if (key === 'text') {
                    node.textContent = attrs[key];
                } else if (key === 'className') {
                    node.className = attrs[key];
                } else if (key.indexOf('on') === 0) {
                    node.addEventListener(key.substring(2), attrs[key]);
                } else {
                    node.setAttribute(key, attrs[key] === true ? key : attrs[key]);
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

    function icon(name, extra) {
        return el('span', { className: 'material-icons' + (extra ? ' ' + extra : ''), 'aria-hidden': 'true', text: name });
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

    // ---------------------------------------------------------------- route

    function isRoute() {
        return ROUTE_RE.test(location.hash);
    }

    function routeParams() {
        var result = {};
        var hash = location.hash;
        var q = hash.indexOf('?');
        if (q < 0) {
            return result;
        }
        var parts = hash.substring(q + 1).split('&');
        for (var i = 0; i < parts.length; i++) {
            var eq = parts[i].indexOf('=');
            if (eq > 0) {
                try {
                    result[decodeURIComponent(parts[i].substring(0, eq))] = decodeURIComponent(parts[i].substring(eq + 1).replace(/\+/g, ' '));
                } catch (e) {
                    // Ignore a malformed parameter.
                }
            }
        }
        return result;
    }

    function routeQuery() {
        var parts = [];
        function addPart(name, value, fallback) {
            if (value && value !== fallback) {
                parts.push(name + '=' + encodeURIComponent(value));
            }
        }
        addPart('q', state.query, '');
        addPart('sort', state.filters.sort, 'relevance');
        addPart('duration', state.filters.duration, 'any');
        addPart('date', state.filters.date, 'any');
        addPart('order', state.order, impliedOrder(state.filters.sort));
        addPart('tab', state.tab, 'search');
        return parts.join('&');
    }

    // The order an address without "order=" means: newest first for a relevance search
    // (the default), YouTube's own order when a popularity sort was asked for.
    function impliedOrder(sort) {
        return sort === 'relevance' ? 'newest' : 'none';
    }

    // Records search, sort, filters and tab in the URL without a new history entry,
    // so reload and Back restore the same view. history.state is passed on unchanged:
    // it carries React Router's own bookkeeping.
    //
    // React Router only reads the address on its own navigations and on popstate, so a
    // synthetic popstate tells it about the new URL. Without that, its copy goes stale,
    // and every Jellyfin dialog (the Delete confirmation, for one) would push that stale
    // URL. Our own popstate handler ignores it: routeKey already matches.
    function writeRoute() {
        if (!isRoute()) {
            return;
        }
        var qs = routeQuery();
        var hash = '#/youtube' + (qs ? '?' + qs : '');
        if (location.hash !== hash) {
            try {
                history.replaceState(history.state, '', location.pathname + location.search + hash);
                state.routeKey = location.hash;
                window.dispatchEvent(new PopStateEvent('popstate', { state: history.state }));
            } catch (e) {
                // Old engines: the dialog check in applyRoute still covers dialogs.
            }
        }
        state.routeKey = location.hash;
    }

    function navigateToPage() {
        if (isRoute()) {
            showTab('search');
            focusSearch();
            return;
        }
        var qs = routeQuery();
        var path = '/youtube' + (qs ? '?' + qs : '');
        if (document.getElementById('fallbackPage')) {
            // From another "not found" view, Jellyfin's router would keep the same view
            // and wait forever for it to announce itself, blocking Play, Delete and Back.
            location.hash = '#' + path;
            return;
        }
        try {
            if (window.Emby && window.Emby.Page && typeof window.Emby.Page.show === 'function') {
                window.Emby.Page.show(path);
                return;
            }
        } catch (e) {
            // Fall through to a plain hash change.
        }
        location.hash = '#' + path;
    }

    function setRootClass(on) {
        var root = document.documentElement;
        if (on) {
            root.classList.add('mtj-yt');
        } else {
            root.classList.remove('mtj-yt');
        }
    }

    function sync() {
        var on = isRoute();
        setRootClass(on);
        if (on) {
            // After jellyfin-web has finished showing its own (hidden) not-found view.
            window.setTimeout(showPage, 0);
        } else {
            hidePage();
        }
        scheduleScan();
    }

    // Applies the URL (deep link, reload, Back) to the page state. Our own URL updates
    // set routeKey, so they are not applied a second time. Returns true when it did
    // something.
    function applyRoute() {
        var hs = history.state;
        if (state.routeKey !== null && hs && hs.usr && hs.usr.dialogs && hs.usr.dialogs.length && document.querySelector('.dialogContainer')) {
            // A Jellyfin dialog's own history entry, not a navigation.
            return false;
        }
        if (location.hash === state.routeKey) {
            return false;
        }
        state.routeKey = location.hash;
        var p = routeParams();
        var filters = {
            sort: SERVER_SORTS[p.sort] ? p.sort : 'relevance',
            duration: /^(any|short|medium|long)$/.test(p.duration || '') ? p.duration : 'any',
            date: /^(any|today|week|month|year)$/.test(p.date || '') ? p.date : 'any'
        };
        var changed = filters.sort !== state.filters.sort || filters.duration !== state.filters.duration || filters.date !== state.filters.date;
        state.filters = filters;
        state.order = LOCAL_ORDERS[p.order] || p.order === 'none' ? p.order : impliedOrder(filters.sort);
        state.tab = p.tab === 'added' || p.tab === 'folders' ? p.tab : 'search';
        // From Jellyfin's selection menu: the folders to merge, by item id. Used once;
        // the next address update drops it.
        var picked = parseIdList(p.select);
        if (picked.length >= 2) {
            state.tab = 'folders';
            state.pendingSelect = picked;
            state.fromLibrary = true;
            state.folderNotice = '';
            state.merge = null;
        }
        var q = (p.q || '').trim();
        if (q && (q !== state.query || changed || !state.items.length)) {
            state.query = q;
            startSearch(q, true);
        } else if (!q && state.query) {
            state.query = '';
            state.items = [];
            state.continuation = null;
        }
        return true;
    }

    // ---------------------------------------------------------------- access

    function allowed() {
        return !!state.me && pick(state.me, 'Allowed') === true && state.meUser === currentUserId();
    }

    function isAdmin() {
        return allowed() && pick(state.me, 'IsAdmin') === true;
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
            if (state.visible) {
                render();
                refreshJobs();
            }
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
        state.me = null;
        state.meUser = null;
        state.retryAt = 0;
        state.searchSeq++; // Drops search answers still on their way.
        state.searching = false;
        window.clearTimeout(state.pollTimer);
        state.pollTimer = null;
        state.filters = { sort: 'relevance', duration: 'any', date: 'any' };
        state.order = 'newest';
        state.scrollY = 0;
        state.focusVideoId = null;
        state.query = '';
        state.items = [];
        state.continuation = null;
        state.source = null;
        state.jobs = [];
        state.jobsSig = '';
        state.jobsByVideo = {};
        state.otherJobs = {};
        state.addState = {};
        state.formatSaving = null;
        state.tab = 'search';
        state.folders = null;
        state.aliases = [];
        state.folderSel = {};
        state.folderNotice = '';
        state.merge = null;
        state.pendingSelect = null;
        state.fromLibrary = false;
        state.dismissing = {};
        folderIds.map = null;
        state.routeKey = null;
        if (state.page && state.page.parentNode) {
            state.page.parentNode.removeChild(state.page);
        }
        state.page = null;
        state.visible = false;
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
        if (event.ctrlKey || event.metaKey || event.shiftKey || event.button === 1) {
            return; // Let the browser open #/youtube in a new tab.
        }
        event.preventDefault();
        navigateToPage();
    }

    function markEntries() {
        var on = state.visible;
        var nodes = document.querySelectorAll('[' + ENTRY_ATTR + ']');
        for (var i = 0; i < nodes.length; i++) {
            if (on) {
                nodes[i].setAttribute('aria-current', 'page');
            } else {
                nodes[i].removeAttribute('aria-current');
            }
        }
    }

    // Modern layout, phones: clone the Favorites row of the side menu.
    function injectModernDrawer() {
        var favorites = document.querySelector('.MuiDrawer-root a[href="#/home?tab=1"], .MuiDrawer-root a[href$="/home?tab=1"]');
        var row = favorites && favorites.closest ? favorites.closest('li') : null;
        if (!row || !row.parentNode || row.parentNode.querySelector('[' + ENTRY_ATTR + '="drawer"]')) {
            return;
        }
        var clone = row.cloneNode(true);
        var link = clone.querySelector('a');
        if (!link) {
            return;
        }
        link.setAttribute(ENTRY_ATTR, 'drawer');
        link.setAttribute('href', '#/youtube');
        link.classList.remove('Mui-selected');
        link.removeAttribute('aria-current');
        var iconHost = link.querySelector('.MuiListItemIcon-root');
        if (iconHost) {
            iconHost.textContent = '';
            iconHost.appendChild(icon(ICON, 'MuiIcon-root'));
        }
        var label = link.querySelector('.MuiListItemText-primary') || link.querySelector('.MuiTypography-root');
        if (label) {
            label.textContent = 'YouTube';
        }
        link.addEventListener('click', onEntryClick);
        clone.setAttribute('data-metube-row', 'drawer');
        row.parentNode.insertBefore(clone, row.nextSibling);
    }

    // Modern layout header: an icon link styled like the Search button next to it.
    function injectModernHeader() {
        var search = document.querySelector('header.MuiAppBar-root .MuiToolbar-root a[href*="/search"]') || document.querySelector('.MuiToolbar-root a[href*="/search"]');
        if (!search || !search.parentNode || search.parentNode.querySelector('[' + ENTRY_ATTR + '="header"]')) {
            return;
        }
        var link = el('a', {
            href: '#/youtube',
            className: search.className.replace(/\bMui-disabled\b/g, ''),
            title: 'YouTube',
            'aria-label': 'YouTube',
            onclick: onEntryClick
        }, [icon(ICON, 'MuiIcon-root')]);
        link.setAttribute(ENTRY_ATTR, 'header');
        search.parentNode.insertBefore(link, search);
    }

    // Legacy layouts (desktop-legacy, tv): header button and side-menu link.
    function injectLegacy() {
        var searchButton = document.querySelector('.headerRight .headerSearchButton');
        if (searchButton && searchButton.parentNode && !searchButton.parentNode.querySelector('[' + ENTRY_ATTR + '="legacy-header"]')) {
            var button = el('a', {
                href: '#/youtube',
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
            var link = el('a', { className: 'navMenuOption lnkMediaFolder', href: '#/youtube', 'data-itemid': 'metube', onclick: onEntryClick }, [
                el('span', { className: 'material-icons navMenuOptionIcon', 'aria-hidden': 'true', text: ICON }),
                el('span', { className: 'navMenuOptionText', text: 'YouTube' })
            ]);
            link.setAttribute(ENTRY_ATTR, 'legacy-drawer');
            home.parentNode.insertBefore(link, home.nextSibling);
        }
    }

    function removeDrawerRows() {
        var rows = document.querySelectorAll('[data-metube-row]');
        for (var i = 0; i < rows.length; i++) {
            rows[i].parentNode.removeChild(rows[i]);
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
            removeDrawerRows();
            checkMe();
            return;
        }
        checkSelectionMenu();
        applyLibraryIcon();
        // Admins can switch the menu entries off; the page itself keeps working.
        if (!allowed() || pick(state.me, 'InjectionEnabled') === false) {
            removeEntries();
            removeDrawerRows();
            return;
        }
        injectModernDrawer();
        injectModernHeader();
        injectLegacy();
        markEntries();
    }

    // Jellyfin picks a library's icon from its content type, so the YouTube library
    // (Home Videos) gets a photo. CSS only, for the links to that library: the header,
    // the side menu, the "More" menu and the home screen's library buttons. Jellyfin's
    // own elements are hidden or decorated, never replaced, so React keeps working.
    var libraryIconFor = null;

    function applyLibraryIcon() {
        var id = state.me ? String(pick(state.me, 'LibraryId') || '').replace(/-/g, '').toLowerCase() : '';
        if (!/^[0-9a-f]{32}$/.test(id)) {
            id = '';
        }
        if (id === libraryIconFor) {
            return;
        }
        libraryIconFor = id;
        var node = document.getElementById('mtj-libicon');
        if (!id) {
            if (node) {
                node.parentNode.removeChild(node);
            }
            return;
        }
        var link = 'a[href*="topParentId=' + id + '"]';
        var glyph = 'content:"' + ICON_CODE + '";font-family:"Material Icons";font-weight:normal;font-style:normal;line-height:1;letter-spacing:normal;' +
            'text-transform:none;white-space:nowrap;display:inline-block;-webkit-font-smoothing:antialiased;';
        var css = [
            link + ' .MuiButton-startIcon>svg,' + link + ' .MuiListItemIcon-root>svg{display:none !important;}',
            link + ' .MuiButton-startIcon::before{' + glyph + 'font-size:20px;}',
            link + ' .MuiListItemIcon-root::before{' + glyph + 'font-size:24px;}',
            link + ' .navMenuOptionIcon::before,' + link + ' .homeLibraryIcon::before{content:"' + ICON_CODE + '" !important;}'
        ].join('\n');
        if (!node) {
            node = el('style', { id: 'mtj-libicon' });
            (document.head || document.documentElement).appendChild(node);
        }
        node.textContent = css;
    }

    // ---------------------------------------------------------------- styles

    // Colours come from Jellyfin's theme variables, so the page follows the selected
    // theme (dark, light, purple haze...). No z-index: the page sits in Jellyfin's own
    // page container, under the app bar, menus and dialogs.
    function ensureStyles() {
        if (document.getElementById('mtj-style')) {
            return;
        }
        var primary = 'var(--jf-palette-primary-main,#00a4dc)';
        var paper = 'var(--jf-palette-background-paper,#202020)';
        var secondary = 'var(--jf-palette-text-secondary,rgba(255,255,255,.7))';
        var divider = 'var(--jf-palette-divider,rgba(255,255,255,.12))';
        var css = [
            'html.mtj-yt #fallbackPage{display:none !important;}',
            'html.mtj-yt .mainAnimatedPages>.page:not(#mtjPage){display:none !important;}',
            '[data-metube-entry][aria-current="page"]{color:' + primary + ' !important;}',
            '#mtjPage [hidden]{display:none !important;}',
            '#mtjPage .mtj-wrap{padding-top:1em;}',
            '#mtjPage .mtj-head{display:flex;align-items:center;flex-wrap:wrap;margin-bottom:1em;}',
            '#mtjPage .mtj-head>*+*{margin-left:1em;}',
            '#mtjPage .mtj-h1{margin:0;font-size:1.6em;font-weight:500;}',
            '#mtjPage .mtj-tabs{display:flex;}',
            '#mtjPage .mtj-tab{background:none;border:0;color:' + secondary + ';padding:.5em .9em;font:inherit;cursor:pointer;border-bottom:2px solid transparent;}',
            '#mtjPage .mtj-tab.mtj-active{color:inherit;border-bottom-color:' + primary + ';}',
            '#mtjPage .mtj-tab:focus{outline:2px solid ' + primary + ';}',
            '#mtjPage .mtj-form{display:flex;max-width:900px;}',
            '#mtjPage .mtj-form>*+*{margin-left:.5em;}',
            '#mtjPage .mtj-input,#mtjPage .mtj-select{font:inherit;color:inherit;background:' + paper + ';border:1px solid ' + divider + ';border-radius:.3em;}',
            '#mtjPage .mtj-input{flex:1;min-width:0;padding:.6em .8em;font-size:1.05em;}',
            '#mtjPage .mtj-select{padding:.4em .5em;max-width:100%;}',
            '#mtjPage .mtj-select option{background:' + paper + ';color:inherit;}',
            '#mtjPage .mtj-input:focus,#mtjPage .mtj-select:focus{outline:none;border-color:' + primary + ';}',
            '#mtjPage .mtj-filters{display:flex;flex-wrap:wrap;align-items:center;margin:.8em 0 1em;}',
            '#mtjPage .mtj-filters label{display:flex;align-items:center;margin:0 1.2em .4em 0;color:' + secondary + ';}',
            '#mtjPage .mtj-filters label>*+*{margin-left:.5em;}',
            '#mtjPage .mtj-btn{padding:.5em 1em;font:inherit;color:#fff;background:' + primary + ';border:0;border-radius:.3em;cursor:pointer;white-space:nowrap;}',
            '#mtjPage .mtj-btn:hover,#mtjPage .mtj-btn:focus{filter:brightness(1.12);outline:2px solid currentColor;outline-offset:1px;}',
            '#mtjPage .mtj-btn[disabled]{background:' + divider + ';color:' + secondary + ';cursor:default;filter:none;outline:none;}',
            '#mtjPage .mtj-btn.mtj-secondary{background:' + paper + ';color:inherit;border:1px solid ' + divider + ';}',
            '#mtjPage .mtj-btn.mtj-danger{background:transparent;color:var(--jf-palette-error-main,#f44336);border:1px solid currentColor;padding:.4em .6em;line-height:1;}',
            '#mtjPage .mtj-btn.mtj-danger .material-icons{font-size:1.2em;vertical-align:middle;}',
            '#mtjPage .mtj-msg{margin:0 0 .8em;color:' + secondary + ';}',
            '#mtjPage .mtj-msg.mtj-err{color:var(--jf-palette-error-main,#ff8a80);}',
            '#mtjPage .mtj-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));grid-gap:1em;gap:1em;}',
            '#mtjPage .mtj-card{background:' + paper + ';border-radius:var(--jf-card-borderRadius,.3em);overflow:hidden;display:flex;flex-direction:column;}',
            '#mtjPage .mtj-thumb{position:relative;padding-top:56.25%;background:#000;}',
            '#mtjPage .mtj-thumb img{position:absolute;top:0;left:0;width:100%;height:100%;object-fit:cover;}',
            '#mtjPage .mtj-dur{position:absolute;right:6px;bottom:6px;padding:1px 5px;font-size:.8em;color:#fff;background:rgba(0,0,0,.8);border-radius:3px;}',
            '#mtjPage .mtj-info{padding:.7em .7em .2em;flex:1;}',
            '#mtjPage .mtj-name{font-weight:600;line-height:1.3;display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical;overflow:hidden;}',
            '#mtjPage .mtj-meta{margin-top:.3em;font-size:.85em;color:' + secondary + ';}',
            '#mtjPage .mtj-actions{display:flex;align-items:center;padding:.5em .7em .7em;flex-wrap:wrap;}',
            '#mtjPage .mtj-actions>*+*{margin-left:.5em;}',
            '#mtjPage .mtj-note{font-size:.85em;color:' + secondary + ';}',
            '#mtjPage .mtj-note.mtj-err{color:var(--jf-palette-error-main,#ff8a80);}',
            '#mtjPage .mtj-more{display:block;margin:1.2em auto 0;}',
            '#mtjPage .mtj-row{display:flex;flex-wrap:wrap;align-items:center;background:' + paper + ';border-radius:var(--jf-card-borderRadius,.3em);padding:.5em;margin:0 0 .5em;max-width:1100px;}',
            '#mtjPage .mtj-row>*+*{margin-left:.8em;}',
            '#mtjPage .mtj-row img{width:120px;height:68px;object-fit:cover;border-radius:4px;flex:none;background:#000;}',
            '#mtjPage .mtj-row .mtj-info{padding:0;min-width:0;flex:1 1 10em;}',
            '#mtjPage .mtj-row .mtj-actions{padding:0;margin-left:auto;justify-content:flex-end;}',
            '#mtjPage .mtj-btn.mtj-icon{background:transparent;color:inherit;border:1px solid ' + divider + ';padding:.4em .6em;line-height:1;}',
            '#mtjPage .mtj-btn.mtj-icon .material-icons{font-size:1.2em;vertical-align:middle;}',
            '#mtjPage .mtj-toolbar{display:flex;align-items:center;flex-wrap:wrap;margin:0 0 .8em;}',
            '#mtjPage .mtj-toolbar>*+*{margin-left:.8em;}',
            '#mtjPage .mtj-folder{cursor:pointer;}',
            '#mtjPage .mtj-folder.mtj-selected{box-shadow:inset 0 0 0 2px ' + primary + ';}',
            '#mtjPage .mtj-check{width:1.2em;height:1.2em;flex:none;margin:0 .2em;}',
            '#mtjPage .mtj-noimg{width:120px;height:68px;flex:none;display:flex;align-items:center;justify-content:center;background:rgba(0,0,0,.3);border-radius:4px;color:' + secondary + ';}',
            '#mtjPage .mtj-merge{background:' + paper + ';border:1px solid ' + primary + ';border-radius:.3em;padding:.8em 1em;margin:0 0 1em;max-width:1100px;}',
            '#mtjPage .mtj-radios{margin:.6em 0;}',
            '#mtjPage .mtj-radio{display:flex;align-items:center;padding:.3em 0;cursor:pointer;}',
            '#mtjPage .mtj-radio>*+*{margin-left:.6em;}',
            '#mtjPage .mtj-merge .mtj-actions{padding:.4em 0 0;}',
            '#mtjPage .mtj-h2{font-size:1.15em;font-weight:500;margin:1.6em 0 .4em;}',
            '#mtjPage .mtj-alias .mtj-name{font-weight:400;}',
            '@media (max-width:600px){#mtjPage .mtj-grid{grid-template-columns:1fr;}#mtjPage .mtj-row img,#mtjPage .mtj-noimg{width:96px;height:54px;}#mtjPage .mtj-filters label{margin-right:.8em;}' +
                '#mtjPage .mtj-row .mtj-actions{flex:1 1 100%;margin:.5em 0 0;}}'
        ].join('\n');
        (document.head || document.documentElement).appendChild(el('style', { id: 'mtj-style', text: css }));
    }

    // ---------------------------------------------------------------- page

    function pageHost() {
        return document.querySelector('main .mainAnimatedPages') || document.querySelector('.mainAnimatedPages');
    }

    function ensurePage(host) {
        if (state.page && document.contains(state.page)) {
            return state.page;
        }
        // jellyfin-web empties its page container on logout and layout switches.
        state.page = el('div', {
            id: 'mtjPage',
            'data-role': 'page',
            className: 'page libraryPage noSecondaryNavPage mainAnimatedPage mtj-page hide',
            'data-title': 'YouTube'
        }, [el('div', { className: 'mtj-wrap padded-left padded-right padded-bottom-page' })]);
        host.appendChild(state.page);
        render();
        return state.page;
    }

    var showRetries = 0;

    function showPage() {
        if (!isRoute()) {
            return;
        }
        var host = ready() ? pageHost() : null;
        if (!host) {
            if (showRetries++ < 100) {
                window.setTimeout(showPage, 200);
            }
            return;
        }
        showRetries = 0;
        ensureStyles();
        ensurePage(host);
        checkMe();
        var changed = applyRoute();
        var wasVisible = state.visible;
        state.page.classList.remove('hide');
        state.visible = true;
        markEntries();
        if (wasVisible) {
            if (changed) {
                render(); // The address was edited by hand.
            }
            return;
        }
        render();
        var restored = state.scrollY > 0;
        try {
            // Lets jellyfin-web set the document title, the legacy header's back
            // button and the now-playing bar, as for its own pages.
            state.page.dispatchEvent(new CustomEvent('pageshow', {
                bubbles: true,
                detail: { isRestored: restored, options: { enableMediaControl: true, supportsThemeMedia: false }, params: {} }
            }));
        } catch (e) {
            document.title = 'YouTube';
        }
        if (restored) {
            window.scrollTo(0, state.scrollY);
        }
        refreshJobs();
        if (!restoreFocus(restored)) {
            if (!state.query) {
                focusSearch();
            } else if (document.documentElement.classList.contains('layout-tv')) {
                focusFirstCard();
            }
        }
    }

    // Back from a video: put focus on the card that was used, without scrolling, so a
    // TV remote carries on where it was.
    function restoreFocus(restored) {
        var videoId = state.focusVideoId;
        state.focusVideoId = null;
        if (!restored || !videoId || !state.page) {
            return false;
        }
        var card = state.page.querySelector('[data-panel="' + state.tab + '"] [data-video-id="' + videoId + '"]');
        if (!card) {
            return false;
        }
        focusQuietly(card.querySelector('.mtj-actions button:not([disabled])') || card);
        return true;
    }

    function focusFirstCard() {
        window.setTimeout(function () {
            var button = state.page && state.page.querySelector('.mtj-card .mtj-actions button:not([disabled])');
            if (button && state.visible) {
                focusQuietly(button);
            } else {
                focusSearch();
            }
        }, 60);
    }

    function focusQuietly(node) {
        try {
            node.focus({ preventScroll: true });
        } catch (e) {
            node.focus();
        }
    }

    function hidePage() {
        if (!state.visible) {
            return;
        }
        var active = document.activeElement;
        var card = active && active.closest ? active.closest('[data-video-id]') : null;
        state.focusVideoId = card && state.page && state.page.contains(card) ? card.getAttribute('data-video-id') : null;
        state.scrollY = window.pageYOffset || 0;
        state.visible = false;
        state.fromLibrary = false;
        if (state.page) {
            state.page.classList.add('hide');
        }
        window.clearTimeout(state.pollTimer);
        state.pollTimer = null;
        markEntries();
    }

    function focusSearch() {
        window.setTimeout(function () {
            var input = state.page && state.page.querySelector('.mtj-input');
            if (input && state.visible && state.tab === 'search') {
                input.focus();
            }
        }, 60);
    }

    function part(role) {
        return state.page ? state.page.querySelector('[data-role="' + role + '"]') : null;
    }

    function render() {
        if (!state.page) {
            return;
        }
        var wrap = state.page.querySelector('.mtj-wrap');
        wrap.textContent = '';

        if (!allowed()) {
            wrap.appendChild(el('h1', { className: 'sectionTitle mtj-h1', text: 'YouTube' }));
            wrap.appendChild(el('p', { className: 'mtj-msg', text: state.me ? t('notAllowed') : t('loading') }));
            return;
        }

        var tabs = el('div', { className: 'mtj-tabs', role: 'tablist' }, [
            el('button', { type: 'button', role: 'tab', className: 'mtj-tab', 'data-tab': 'search', text: t('search'), onclick: function () { showTab('search'); } }),
            el('button', { type: 'button', role: 'tab', className: 'mtj-tab', 'data-tab': 'added', text: t('added'), onclick: function () { showTab('added'); refreshJobs(); } }),
            isAdmin() ? el('button', { type: 'button', role: 'tab', className: 'mtj-tab', 'data-tab': 'folders', text: t('folders'), onclick: function () { showTab('folders'); loadFolders(); } }) : null
        ]);
        wrap.appendChild(el('div', { className: 'mtj-head' }, [
            el('h1', { className: 'sectionTitle mtj-h1', text: 'YouTube' }),
            tabs
        ]));

        var input = el('input', { type: 'search', className: 'mtj-input', placeholder: t('placeholder'), maxlength: '300', 'aria-label': t('placeholder'), enterkeyhint: 'search' });
        input.value = state.query;
        var form = el('form', { className: 'mtj-form', onsubmit: function (event) { event.preventDefault(); startSearch(input.value, false); } }, [
            input,
            el('button', { type: 'submit', className: 'mtj-btn', text: t('search') })
        ]);

        var searchPanel = el('section', { 'data-panel': 'search' }, [
            form,
            buildFilters(),
            el('div', { className: 'mtj-msg', 'data-role': 'notice' }),
            el('div', { className: 'mtj-msg', 'data-role': 'search-msg' }),
            el('div', { className: 'mtj-grid', 'data-role': 'grid' }),
            el('button', { type: 'button', className: 'mtj-btn mtj-secondary mtj-more', text: t('loadMore'), hidden: true, onclick: loadMore })
        ]);
        var addedPanel = el('section', { 'data-panel': 'added', hidden: true }, [
            el('div', { className: 'mtj-toolbar', 'data-role': 'added-bar' }),
            el('div', { className: 'mtj-msg', 'data-role': 'added-msg' }),
            el('div', { 'data-role': 'list' })
        ]);
        wrap.appendChild(searchPanel);
        wrap.appendChild(addedPanel);
        if (isAdmin()) {
            wrap.appendChild(el('section', { 'data-panel': 'folders', hidden: true }, [
                el('p', { className: 'mtj-msg', text: t('foldersIntro') }),
                el('div', { className: 'mtj-msg', 'data-role': 'folders-msg', hidden: true }),
                el('div', { className: 'mtj-toolbar', 'data-role': 'folders-bar' }),
                el('div', { 'data-role': 'merge' }),
                el('div', { 'data-role': 'folders' }),
                el('div', { 'data-role': 'aliases' })
            ]));
        }

        showTab(state.tab, true);
        renderNotices();
        renderResults();
        state.jobsSig = '';
        renderAdded();
        renderFolders();
        if (state.searching) {
            setMessage('search-msg', t('searching'));
        }
        if (state.tab === 'folders') {
            loadFolders();
        }
    }

    function select(name, label, options, value, onChange) {
        var node = el('select', { className: 'mtj-select', 'data-filter': name, onchange: function () { onChange(node.value); } });
        for (var i = 0; i < options.length; i++) {
            var option = el('option', { value: options[i][0], text: options[i][1] });
            if (options[i][0] === value) {
                option.selected = true;
            }
            node.appendChild(option);
        }
        return el('label', null, [el('span', { text: label }), node]);
    }

    // The format the next Add downloads in. Stored on the server per user, so it
    // follows them to every device. While a change is still on its way to the
    // server, an Add sends it along so it can't get the previous format; otherwise
    // the Add leaves it out, because the stored choice may be newer (set elsewhere).
    function setDownloadFormat(value) {
        var previous = pick(state.me, 'DownloadFormat') || 'default';
        var saving = { value: value };
        state.me.DownloadFormat = value;
        state.formatSaving = saving;
        request('POST', 'MeTube/Preferences', { DownloadFormat: value }).then(function () {
            if (state.formatSaving === saving) {
                state.formatSaving = null;
            }
        }, function (error) {
            // Only the latest change rolls back: a newer one, or another user signing
            // in (which clears formatSaving), has replaced this one.
            if (state.formatSaving !== saving) {
                return;
            }
            state.formatSaving = null;
            if (!state.me) {
                return; // Signed out meanwhile.
            }
            state.me.DownloadFormat = previous;
            var node = state.page && state.page.querySelector('select[data-filter="download"]');
            if (node) {
                node.value = previous;
            }
            setMessage('search-msg', error.message, true);
        });
    }

    // "Sort" mixes what YouTube can do (relevance, popularity: a new search) with what
    // only the page can do (newest, shortest, longest: reorder what is loaded).
    function buildFilters() {
        var sortValue = state.order !== 'none' ? state.order : state.filters.sort;
        return el('div', { className: 'mtj-filters' }, [
            select('sort', t('sortBy'), [
                ['relevance', t('relevance')], ['popularity', t('popularity')],
                ['newest', t('newest')], ['shortest', t('shortest')], ['longest', t('longest')]
            ], sortValue, function (value) {
                if (SERVER_SORTS[value]) {
                    var changed = state.filters.sort !== value;
                    state.filters.sort = value;
                    state.order = 'none';
                    if (changed) {
                        rerunSearch();
                    } else {
                        renderResults();
                        writeRoute();
                    }
                } else {
                    state.order = value;
                    renderResults();
                    writeRoute();
                }
            }),
            select('duration', t('length'), [
                ['any', t('anyLength')], ['short', t('short')], ['medium', t('medium')], ['long', t('long')]
            ], state.filters.duration, function (value) {
                state.filters.duration = value;
                rerunSearch();
            }),
            select('date', t('uploaded'), [
                ['any', t('anyTime')], ['today', t('today')], ['week', t('week')], ['month', t('month')], ['year', t('year')]
            ], state.filters.date, function (value) {
                state.filters.date = value;
                rerunSearch();
            }),
            select('download', t('downloadAs'), [
                ['default', t('formatDefault')], ['compatible', t('formatCompatible')], ['best', t('formatBest')]
            ], pick(state.me, 'DownloadFormat') || 'default', setDownloadFormat)
        ]);
    }

    function showTab(tab, silent) {
        if (tab === 'folders' && !isAdmin()) {
            tab = 'search';
        }
        state.tab = tab;
        if (state.page) {
            var tabs = state.page.querySelectorAll('.mtj-tab');
            for (var i = 0; i < tabs.length; i++) {
                var active = tabs[i].getAttribute('data-tab') === tab;
                if (active) {
                    tabs[i].classList.add('mtj-active');
                } else {
                    tabs[i].classList.remove('mtj-active');
                }
                tabs[i].setAttribute('aria-selected', active ? 'true' : 'false');
            }
            var panels = state.page.querySelectorAll('[data-panel]');
            for (var j = 0; j < panels.length; j++) {
                panels[j].hidden = panels[j].getAttribute('data-panel') !== tab;
            }
        }
        if (!silent) {
            writeRoute();
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
        if (state.order !== 'none' && state.items.length) {
            lines.push(t('localOrderNote'));
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

    function startSearch(text, fromRoute) {
        var value = (text || '').trim();
        if (!value) {
            return;
        }
        state.query = value;
        state.items = [];
        state.continuation = null;
        state.source = null;
        if (!fromRoute) {
            writeRoute();
        }

        var linked = extractVideoId(value);
        if (linked) {
            state.searchSeq++;
            state.searching = false;
            state.items = [{ VideoId: linked, Title: t('linkVideo', linked), ThumbnailUrl: 'https://i.ytimg.com/vi/' + linked + '/mqdefault.jpg', Availability: 'Addable', Pasted: true }];
            setMessage('search-msg', '');
            renderNotices();
            renderResults();
            return;
        }

        runSearch({ q: value, sort: state.filters.sort, duration: state.filters.duration, date: state.filters.date }, false);
    }

    function rerunSearch() {
        writeRoute();
        if (state.query && !extractVideoId(state.query)) {
            startSearch(state.query, true);
        }
    }

    function loadMore() {
        if (state.continuation && !state.searching) {
            runSearch({ continuation: state.continuation }, true);
        }
    }

    function runSearch(query, append) {
        var seq = ++state.searchSeq;
        state.searching = true;
        setMessage('search-msg', t('searching'));
        var more = state.page && state.page.querySelector('.mtj-more');
        if (more) {
            more.disabled = true;
        }
        request('GET', 'MeTube/Search', undefined, query).then(function (data) {
            if (seq !== state.searchSeq) {
                return; // A newer search replaced this one.
            }
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
            if (seq !== state.searchSeq) {
                return;
            }
            state.searching = false;
            setMessage('search-msg', error.message, true);
            renderResults();
        });
    }

    // Stable sort (older engines' Array.sort is not), missing values last.
    function orderedItems() {
        var items = state.items.slice();
        if (state.order === 'none') {
            return items;
        }
        var field = state.order === 'newest' ? 'AgeSeconds' : 'DurationSeconds';
        var direction = state.order === 'longest' ? -1 : 1;
        var indexed = [];
        for (var i = 0; i < items.length; i++) {
            indexed.push({ item: items[i], index: i, value: pick(items[i], field) });
        }
        indexed.sort(function (a, b) {
            var av = typeof a.value === 'number' ? a.value : null;
            var bv = typeof b.value === 'number' ? b.value : null;
            if (av === null && bv === null) {
                return a.index - b.index;
            }
            if (av === null) {
                return 1;
            }
            if (bv === null) {
                return -1;
            }
            return av === bv ? a.index - b.index : (av - bv) * direction;
        });
        var result = [];
        for (var j = 0; j < indexed.length; j++) {
            result.push(indexed[j].item);
        }
        return result;
    }

    function renderResults() {
        var grid = part('grid');
        if (!grid) {
            return;
        }
        grid.textContent = '';
        var items = orderedItems();
        for (var i = 0; i < items.length; i++) {
            grid.appendChild(buildCard(items[i]));
        }
        var more = state.page.querySelector('.mtj-more');
        if (more) {
            more.hidden = !state.continuation;
            more.disabled = state.searching;
        }
        renderNotices();
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
        var card = el('div', { className: 'mtj-card', 'data-video-id': videoId, tabindex: '-1' }, [
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

    // Decides what a card (or an "Added videos" row) shows. The job wins while it is
    // open. Once closed, the server's current answer (search or add) decides, so a late
    // arrival in the library still gets its Play button.
    function decide(item) {
        var videoId = pick(item, 'VideoId');
        var job = item.OwnJob ? pick(item, 'Job') : (state.jobsByVideo[videoId] || state.otherJobs[videoId] || pick(item, 'Job'));
        var availability = pick(item, 'Availability');
        var view = { kind: 'add', itemId: pick(item, 'ItemId'), note: null, noteIsError: false, job: null, canDelete: pick(item, 'CanDelete') === true };
        var jobState = job ? pick(job, 'State') : null;
        if (job && pick(job, 'CanDelete') === true) {
            view.canDelete = true;
        }

        if (job && jobState === 'InLibrary' && pick(job, 'ItemId')) {
            view.kind = 'play';
            view.itemId = pick(job, 'ItemId');
            return view;
        }
        if (job && isOpenJob(job)) {
            view.kind = 'job';
            view.job = job;
            view.canDelete = false;
            return view;
        }
        if (job && !availability) {
            // "Added videos" rows: only the job is known.
            if (jobState === 'Downloaded') {
                view.kind = 'downloaded';
                return view;
            }
            view.kind = 'retry';
            view.canDelete = false;
            view.note = jobState === 'Missing' ? t('missing') : jobState === 'Removed' ? t('removed') : jobState === 'Deleted' ? t('deleted') : (pick(job, 'Error') || t('failed'));
            view.noteIsError = jobState !== 'Removed' && jobState !== 'Deleted';
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
            view.canDelete = false;
            view.note = reason === 'Live' ? t('live') : reason === 'Upcoming' ? t('upcoming') : t('tooLong');
        } else if (availability === 'InProgress') {
            // The search said someone is downloading it, but that job is not known
            // here (yet): show it as queued until the status poll catches up.
            view.kind = 'job';
            view.canDelete = false;
            view.job = pick(item, 'Job') || { State: 'Queued' };
        } else {
            view.canDelete = false;
        }
        return view;
    }

    function signature(item) {
        var videoId = pick(item, 'VideoId');
        var view = decide(item);
        var pending = state.addState[videoId] || {};
        var job = view.job;
        return [view.kind, view.itemId || '', view.note || '', view.canDelete ? 1 : 0, job ? pick(job, 'State') : '', job ? Math.round(pick(job, 'Percent') || 0) : '', pending.busy || '', pending.error || ''].join('|');
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
            // Keep keyboard/remote focus on the card when a poll re-renders it. Never on
            // the search box: that sits at the top and would scroll the page there.
            focusQuietly(actions.querySelector('button:not([disabled])') || card);
        }
    }

    function fillActions(actions, item) {
        var videoId = pick(item, 'VideoId');
        // Never give a local variable a function's name here: a local "add" once hid
        // the add function from the Add button's click handler, so the button did nothing.
        var pending = state.addState[videoId] || {};
        if (pending.busy) {
            actions.appendChild(disabledButton(pending.busy === 'delete' ? t('deleting') : t('adding')));
            return;
        }
        var view = decide(item);
        var note = pending.error || view.note;
        var noteIsError = pending.error ? true : view.noteIsError;
        var title = pick(item, 'Title') || pick(view.job, 'Title') || videoId;

        if (view.kind === 'play') {
            actions.appendChild(el('button', { type: 'button', className: 'mtj-btn', onclick: function () { play(view.itemId); } }, [t('play')]));
        } else if (view.kind === 'job') {
            var jobState = pick(view.job, 'State');
            if (jobState === 'Downloading') {
                actions.appendChild(disabledButton(t('downloading', Math.round(pick(view.job, 'Percent') || 0))));
            } else if (jobState === 'Finished') {
                actions.appendChild(disabledButton(t('processing')));
            } else {
                actions.appendChild(disabledButton(t('queued')));
            }
        } else if (view.kind === 'downloaded') {
            actions.appendChild(disabledButton(t('inLibrary')));
            note = note || t('downloaded');
        } else if (view.kind === 'inlibrary') {
            actions.appendChild(disabledButton(t('inLibrary')));
        } else if (view.kind === 'blocked') {
            actions.appendChild(disabledButton(view.note));
            note = pending.error || null;
        } else {
            actions.appendChild(el('button', { type: 'button', className: 'mtj-btn', text: view.kind === 'retry' ? t('retry') : t('add'), onclick: function () { addVideo(videoId); } }));
        }

        if (view.canDelete && (view.kind === 'play' || view.kind === 'inlibrary' || view.kind === 'downloaded')) {
            actions.appendChild(el('button', {
                type: 'button',
                className: 'mtj-btn mtj-danger',
                title: t('del'),
                'aria-label': t('del'),
                onclick: function () { confirmDelete(videoId, title); }
            }, [icon('delete')]));
        }

        if (note) {
            actions.appendChild(el('span', { className: noteIsError ? 'mtj-note mtj-err' : 'mtj-note', text: note }));
        }
    }

    function disabledButton(text) {
        return el('button', { type: 'button', className: 'mtj-btn', disabled: true, text: text });
    }

    function rerender() {
        if (!state.page) {
            return;
        }
        for (var i = 0; i < state.items.length; i++) {
            var card = state.page.querySelector('.mtj-card[data-video-id="' + pick(state.items[i], 'VideoId') + '"]');
            if (card) {
                renderCardActions(card, state.items[i]);
            }
        }
        renderAdded();
    }

    function setAvailability(videoId, availability, itemId) {
        for (var i = 0; i < state.items.length; i++) {
            if (pick(state.items[i], 'VideoId') === videoId) {
                state.items[i].Availability = availability;
                state.items[i].ItemId = itemId || null;
                if (availability !== 'InLibrary') {
                    state.items[i].CanDelete = false;
                }
            }
        }
    }

    function addVideo(videoId) {
        if (state.addState[videoId] && state.addState[videoId].busy) {
            return;
        }
        state.addState[videoId] = { busy: 'add' };
        rerender();
        // Always the id: the server rebuilds the URL itself. The format only while a
        // change is still being saved (see setDownloadFormat).
        var body = { VideoId: videoId };
        if (state.formatSaving) {
            body.DownloadFormat = state.formatSaving.value;
        }
        request('POST', 'MeTube/Add', body).then(function (result) {
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

    function confirmDelete(videoId, title) {
        var text = t('deleteConfirm', title);
        var go = function () { remove(videoId); };
        try {
            if (window.Dashboard && typeof window.Dashboard.confirm === 'function') {
                // Jellyfin's own confirmation dialog, with a red Delete button. Its text
                // is HTML, so the title is escaped ("<3" in a title is common).
                window.Dashboard.confirm({ text: t('deleteConfirm', escapeHtml(title)), title: t('deleteTitle'), confirmText: t('del'), primary: 'delete' }, '', function (ok) {
                    if (ok) {
                        go();
                    }
                });
                return;
            }
        } catch (e) {
            // Fall back to the browser's dialog.
        }
        if (window.confirm(text)) {
            go();
        }
    }

    function remove(videoId) {
        state.addState[videoId] = { busy: 'delete' };
        rerender();
        request('POST', 'MeTube/Delete', { VideoId: videoId }).then(function () {
            delete state.addState[videoId];
            setAvailability(videoId, 'Addable', null);
            delete state.jobsByVideo[videoId];
            delete state.otherJobs[videoId];
            rerender();
            refreshJobs();
        }).catch(function (error) {
            if (error && error.status === 404) {
                // Already gone: show it as addable again.
                setAvailability(videoId, 'Addable', null);
                delete state.jobsByVideo[videoId];
            }
            state.addState[videoId] = { error: error.message };
            rerender();
        });
    }

    function play(itemId) {
        var path = '/details?id=' + encodeURIComponent(itemId);
        try {
            if (window.ApiClient && ApiClient.serverId) {
                path += '&serverId=' + encodeURIComponent(ApiClient.serverId());
            }
            if (window.Emby && window.Emby.Page && typeof window.Emby.Page.show === 'function') {
                window.Emby.Page.show(path);
                return;
            }
        } catch (e) {
            // Fall through to a plain hash change.
        }
        location.hash = '#' + path;
    }

    // ---------------------------------------------------------------- added videos

    function refreshJobs() {
        if (!state.visible || !allowed()) {
            return;
        }
        var uid = currentUserId();
        request('GET', 'MeTube/Status').then(function (jobs) {
            if (uid !== currentUserId() || !allowed()) {
                throw { stale: true };
            }
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
            if (error && error.stale) {
                return; // Another user signed in meanwhile.
            }
            setMessage('added-msg', error.message, true);
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
        var uid = currentUserId();
        return request('GET', 'MeTube/Status', undefined, { videoIds: ids.join(',') }).then(function (jobs) {
            if (uid !== currentUserId()) {
                return;
            }
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
        state.pollTimer = null;
        if (state.visible) {
            state.pollTimer = window.setTimeout(refreshJobs, delay);
        }
    }

    function renderAdded() {
        var list = part('list');
        if (!list) {
            return;
        }
        var sig = JSON.stringify(state.jobs) + JSON.stringify(state.addState) + JSON.stringify(state.dismissing);
        if (sig === state.jobsSig && list.childNodes.length) {
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
        setMessage('added-msg', state.jobs.length ? '' : t('emptyAdded'));
        renderAddedBar();
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
            var row = el('div', { className: 'mtj-row', 'data-video-id': pick(job, 'VideoId'), tabindex: '-1' }, [
                el('img', { src: pick(job, 'ThumbnailUrl'), alt: '', loading: 'lazy', referrerpolicy: 'no-referrer' }),
                el('div', { className: 'mtj-info' }, [
                    el('div', { className: 'mtj-name', title: pick(job, 'Title'), text: pick(job, 'Title') }),
                    el('div', { className: 'mtj-meta', text: meta.join(' · ') })
                ]),
                el('div', { className: 'mtj-actions' })
            ]);
            var rowActions = row.querySelector('.mtj-actions');
            fillActions(rowActions, { VideoId: pick(job, 'VideoId'), Title: pick(job, 'Title'), Job: job, OwnJob: true });
            if (pick(job, 'CanDismiss') === true) {
                rowActions.appendChild(dismissButton(pick(job, 'Id')));
            }
            list.appendChild(row);
        }

        if (focusedIndex >= 0) {
            var newRows = list.querySelectorAll('.mtj-row');
            var row2 = newRows[Math.min(focusedIndex, newRows.length - 1)];
            var target = (row2 && (row2.querySelector('button:not([disabled])') || row2)) || state.page.querySelector('.mtj-tab.mtj-active');
            if (target) {
                focusQuietly(target);
            }
        }
    }

    function renderAddedBar() {
        var bar = part('added-bar');
        if (!bar) {
            return;
        }
        bar.textContent = '';
        var any = false;
        for (var i = 0; i < state.jobs.length; i++) {
            if (pick(state.jobs[i], 'CanDismiss') === true) {
                any = true;
            }
        }
        bar.hidden = !any;
        if (any) {
            bar.appendChild(el('button', {
                type: 'button',
                className: 'mtj-btn mtj-secondary',
                disabled: !!state.dismissing.all,
                text: state.dismissing.all ? t('cleaning') : t('cleanList'),
                onclick: function () { dismiss(null); }
            }));
        }
    }

    function dismissButton(jobId) {
        return el('button', {
            type: 'button',
            className: 'mtj-btn mtj-icon',
            title: t('dismiss'),
            'aria-label': t('dismiss'),
            disabled: !!(state.dismissing[jobId] || state.dismissing.all),
            onclick: function () { dismiss(jobId); }
        }, [icon('close')]);
    }

    // Removes one entry (or, with no id, every failed or removed entry) from the list.
    function dismiss(jobId) {
        var key = jobId || 'all';
        if (state.dismissing[key]) {
            return;
        }
        state.dismissing[key] = true;
        renderAdded();
        var body = jobId ? { JobIds: [jobId] } : { All: true };
        request('POST', 'MeTube/Jobs/Dismiss', body).then(function () {
            delete state.dismissing[key];
            var kept = [];
            for (var i = 0; i < state.jobs.length; i++) {
                var gone = jobId ? pick(state.jobs[i], 'Id') === jobId : pick(state.jobs[i], 'CanDismiss') === true;
                if (!gone) {
                    kept.push(state.jobs[i]);
                }
            }
            state.jobs = kept;
            renderAdded();
            refreshJobs();
        }).catch(function (error) {
            delete state.dismissing[key];
            setMessage('added-msg', error.message, true);
            renderAdded();
        });
    }

    // ---------------------------------------------------------------- folders (admins)

    function loadFolders() {
        if (!isAdmin() || state.foldersLoading) {
            return;
        }
        state.foldersLoading = true;
        if (!state.folders) {
            setMessage('folders-msg', t('loading'));
        }
        var uid = currentUserId();
        request('GET', 'MeTube/Admin/Folders').then(function (data) {
            state.foldersLoading = false;
            if (uid !== currentUserId() || !isAdmin()) {
                return;
            }
            state.folders = pick(data, 'Folders') || [];
            state.aliases = pick(data, 'Aliases') || [];
            rememberFolderIds(state.folders, uid);
            var openAfter = applyPendingSelect();
            var names = {};
            for (var i = 0; i < state.folders.length; i++) {
                names[pick(state.folders[i], 'Name')] = true;
            }
            for (var key in state.folderSel) {
                if (Object.prototype.hasOwnProperty.call(state.folderSel, key) && !names[key]) {
                    delete state.folderSel[key];
                }
            }
            if (selectedFolders().length < 2) {
                state.merge = null;
            }
            setMessage('folders-msg', state.folderNotice);
            renderFolders();
            if (openAfter) {
                openMerge();
            }
        }).catch(function (error) {
            state.foldersLoading = false;
            setMessage('folders-msg', error.message, true);
        });
    }

    // Selects the folders picked in Jellyfin's library view. Returns true when there are
    // two or more, so the merge box opens right away.
    function applyPendingSelect() {
        var wanted = state.pendingSelect;
        if (!wanted) {
            return false;
        }
        state.pendingSelect = null;
        var lookup = {};
        for (var i = 0; i < wanted.length; i++) {
            lookup[wanted[i]] = true;
        }
        state.folderSel = {};
        state.merge = null;
        var found = 0;
        for (var j = 0; j < state.folders.length; j++) {
            if (lookup[normalizeId(pick(state.folders[j], 'ItemId'))]) {
                state.folderSel[pick(state.folders[j], 'Name')] = true;
                found++;
            }
        }
        state.folderNotice = found >= 2 ? '' : t('selectionGone');
        writeRoute();
        return found >= 2;
    }

    // Selected folder names, in list order.
    function selectedFolders() {
        var result = [];
        var folders = state.folders || [];
        for (var i = 0; i < folders.length; i++) {
            if (state.folderSel[pick(folders[i], 'Name')]) {
                result.push(pick(folders[i], 'Name'));
            }
        }
        return result;
    }

    function folderByName(name) {
        var folders = state.folders || [];
        for (var i = 0; i < folders.length; i++) {
            if (pick(folders[i], 'Name') === name) {
                return folders[i];
            }
        }
        return null;
    }

    function videoCount(folder) {
        var count = pick(folder, 'VideoCount') || 0;
        return count === 1 ? t('video') : t('videos', count);
    }

    function renderFolders() {
        var list = part('folders');
        if (!list) {
            return;
        }
        var selected = selectedFolders();

        var bar = part('folders-bar');
        bar.textContent = '';
        bar.appendChild(el('button', { type: 'button', className: 'mtj-btn', disabled: selected.length < 2 || state.mergeBusy || !!state.merge, text: t('merge'), onclick: openMerge }));
        if (state.fromLibrary && !state.merge) {
            bar.appendChild(el('button', { type: 'button', className: 'mtj-btn mtj-secondary', disabled: state.mergeBusy, text: t('backToLibrary'), onclick: backToLibrary }));
        }
        if (selected.length) {
            bar.appendChild(el('span', { className: 'mtj-note', text: t('selected', selected.length) }));
        }

        var box = part('merge');
        box.textContent = '';
        if (state.merge && selected.length >= 2) {
            box.appendChild(buildMergeBox(selected));
        }

        var hadFocus = list.contains(document.activeElement) ? document.activeElement.getAttribute('data-name') : null;
        list.textContent = '';
        var folders = state.folders || [];
        if (state.folders && !folders.length) {
            list.appendChild(el('p', { className: 'mtj-msg', text: t('noFolders') }));
        }
        var receives = {};
        for (var a = 0; a < state.aliases.length; a++) {
            var target = pick(state.aliases[a], 'Target');
            (receives[target] = receives[target] || []).push(pick(state.aliases[a], 'Source'));
        }
        for (var i = 0; i < folders.length; i++) {
            list.appendChild(buildFolderRow(folders[i], i, receives[pick(folders[i], 'Name')]));
        }
        if (hadFocus) {
            var boxes = list.querySelectorAll('.mtj-check');
            for (var b = 0; b < boxes.length; b++) {
                if (boxes[b].getAttribute('data-name') === hadFocus) {
                    boxes[b].focus();
                }
            }
        }

        renderAliases();
    }

    function buildFolderRow(folder, index, sources) {
        var name = pick(folder, 'Name');
        var id = 'mtj-folder-' + index;
        var itemId = pick(folder, 'SampleItemId');
        var meta = [videoCount(folder)];
        if (sources && sources.length) {
            meta.push(t('alsoReceives', sources.join(', ')));
        }
        var check = el('input', {
            type: 'checkbox',
            id: id,
            className: 'mtj-check',
            'data-name': name,
            checked: !!state.folderSel[name],
            disabled: state.mergeBusy,
            onchange: function () {
                if (check.checked) {
                    state.folderSel[name] = true;
                } else {
                    delete state.folderSel[name];
                }
                if (selectedFolders().length < 2) {
                    state.merge = null;
                }
                renderFolders();
            }
        });
        var image = itemId
            ? el('img', { src: ApiClient.getUrl('Items/' + itemId + '/Images/Primary', { maxWidth: 240, quality: 80 }), alt: '', loading: 'lazy' })
            : el('div', { className: 'mtj-noimg' }, [icon('folder')]);
        return el('label', { className: 'mtj-row mtj-folder' + (state.folderSel[name] ? ' mtj-selected' : ''), 'for': id }, [
            check,
            image,
            el('div', { className: 'mtj-info' }, [
                el('div', { className: 'mtj-name', title: name, text: name }),
                el('div', { className: 'mtj-meta', text: meta.join(' · ') })
            ])
        ]);
    }

    function openMerge() {
        var selected = selectedFolders();
        if (selected.length < 2) {
            return;
        }
        // Suggest keeping the folder with the most videos.
        var best = selected[0];
        for (var i = 1; i < selected.length; i++) {
            if ((pick(folderByName(selected[i]), 'VideoCount') || 0) > (pick(folderByName(best), 'VideoCount') || 0)) {
                best = selected[i];
            }
        }
        state.merge = { target: best };
        state.folderNotice = '';
        setMessage('folders-msg', '');
        renderFolders();
        var radio = part('merge') && part('merge').querySelector('input:checked');
        if (radio) {
            radio.focus();
        }
    }

    function buildMergeBox(selected) {
        if (selected.indexOf(state.merge.target) < 0) {
            state.merge.target = selected[0];
        }
        var radios = [];
        for (var i = 0; i < selected.length; i++) {
            radios.push(buildKeepOption(selected[i], i));
        }
        return el('div', { className: 'mtj-merge', role: 'group', 'aria-label': t('merge') }, [
            el('div', { className: 'mtj-name', text: t('mergeKeep') }),
            el('div', { className: 'mtj-radios' }, radios),
            el('p', { className: 'mtj-note', text: t('mergeNote') }),
            el('div', { className: 'mtj-actions' }, [
                el('button', { type: 'button', className: 'mtj-btn', disabled: state.mergeBusy, text: state.mergeBusy ? t('merging') : t('merge'), onclick: doMerge }),
                el('button', { type: 'button', className: 'mtj-btn mtj-secondary', disabled: state.mergeBusy, text: t('cancel'), onclick: function () { state.merge = null; renderFolders(); } })
            ])
        ]);
    }

    function buildKeepOption(name, index) {
        var radio = el('input', {
            type: 'radio',
            name: 'mtj-keep',
            id: 'mtj-keep-' + index,
            value: name,
            checked: state.merge.target === name,
            disabled: state.mergeBusy,
            onchange: function () {
                if (radio.checked) {
                    state.merge.target = name;
                }
            }
        });
        return el('label', { className: 'mtj-radio', 'for': 'mtj-keep-' + index }, [
            radio,
            el('span', { text: name + ' (' + videoCount(folderByName(name)) + ')' })
        ]);
    }

    function doMerge() {
        var selected = selectedFolders();
        if (!state.merge || selected.length < 2 || state.mergeBusy) {
            return;
        }
        var target = state.merge.target;
        state.mergeBusy = true;
        renderFolders();
        request('POST', 'MeTube/Admin/Merge', { Folders: selected, Target: target }).then(function (result) {
            state.mergeBusy = false;
            state.merge = null;
            state.folderSel = {};
            var lines = [t('mergeDone', pick(result, 'MovedVideos') || 0, target)];
            var skipped = pick(result, 'Skipped') || [];
            if (skipped.length) {
                lines.push(t('mergeSkipped', skipped.length, target));
            }
            lines.push(t('rescanNote'));
            state.folderNotice = lines.join(' ');
            setMessage('folders-msg', state.folderNotice);
            renderFolders();
            loadFolders();
            refreshJobs();
            // Item ids for the thumbnails exist once Jellyfin has scanned the moved files.
            window.setTimeout(function () {
                if (state.visible && state.tab === 'folders') {
                    loadFolders();
                }
            }, 15000);
        }).catch(function (error) {
            state.mergeBusy = false;
            setMessage('folders-msg', error.message, true);
            renderFolders();
            loadFolders();
        });
    }

    function renderAliases() {
        var host = part('aliases');
        if (!host) {
            return;
        }
        host.textContent = '';
        if (!state.aliases.length) {
            return;
        }
        host.appendChild(el('h2', { className: 'mtj-h2', text: t('mergedChannels') }));
        host.appendChild(el('p', { className: 'mtj-msg', text: t('redirectNote') }));
        for (var i = 0; i < state.aliases.length; i++) {
            host.appendChild(buildAliasRow(state.aliases[i]));
        }
    }

    function buildAliasRow(alias) {
        var source = pick(alias, 'Source');
        return el('div', { className: 'mtj-row mtj-alias' }, [
            el('div', { className: 'mtj-info' }, [
                el('div', { className: 'mtj-name', text: source + '  →  ' + pick(alias, 'Target') })
            ]),
            el('div', { className: 'mtj-actions' }, [
                el('button', { type: 'button', className: 'mtj-btn mtj-secondary', text: t('stopRedirect'), onclick: function () { removeAlias(source); } })
            ])
        ]);
    }

    function removeAlias(source) {
        request('POST', 'MeTube/Admin/Aliases/Remove', { Source: source }).then(function () {
            loadFolders();
        }).catch(function (error) {
            setMessage('folders-msg', error.message, true);
            loadFolders();
        });
    }

    function backToLibrary() {
        state.fromLibrary = false;
        try {
            if (window.Emby && window.Emby.Page && typeof window.Emby.Page.back === 'function') {
                window.Emby.Page.back();
                return;
            }
        } catch (e) {
            // Fall through to the browser's own Back.
        }
        history.back();
    }

    // ---------------------------------------------------------------- Jellyfin's selection menu (admins)

    // Jellyfin's library views have a multi-select mode (long-press or right-click a
    // card); its "..." button opens an action sheet. When the selection is two or more
    // channel folders of the YouTube library, an administrator gets "Merge folders"
    // there, which opens the Folders tab with those folders selected. Jellyfin's menu
    // ignores ids it does not know, so the extra entry only does what this script does.

    var MERGE_ITEM_ID = 'mtj-merge';
    var folderIds = { map: null, user: null, at: 0, pending: null, panel: null };

    function normalizeId(id) {
        return String(id || '').replace(/-/g, '').toLowerCase();
    }

    function parseIdList(text) {
        var result = [];
        var parts = String(text || '').split(',');
        for (var i = 0; i < parts.length && result.length < 200; i++) {
            var id = normalizeId(parts[i]);
            if (/^[0-9a-f]{32}$/.test(id) && result.indexOf(id) < 0) {
                result.push(id);
            }
        }
        return result;
    }

    function rememberFolderIds(folders, uid) {
        var map = {};
        for (var i = 0; i < folders.length; i++) {
            var id = normalizeId(pick(folders[i], 'ItemId'));
            if (id) {
                map[id] = pick(folders[i], 'Name');
            }
        }
        folderIds.map = map;
        folderIds.user = uid;
        folderIds.at = Date.now();
    }

    // The channel folders' item ids, at most maxAge old.
    function loadFolderIds(maxAge) {
        var uid = currentUserId();
        if (folderIds.map && folderIds.user === uid && Date.now() - folderIds.at <= maxAge) {
            return Promise.resolve(folderIds.map);
        }
        if (!folderIds.pending) {
            folderIds.pending = request('GET', 'MeTube/Admin/Folders').then(function (data) {
                folderIds.pending = null;
                if (uid !== currentUserId()) {
                    return null;
                }
                rememberFolderIds(pick(data, 'Folders') || [], uid);
                return folderIds.map;
            }, function () {
                folderIds.pending = null;
                return null;
            });
        }
        return folderIds.pending;
    }

    // The cards ticked in Jellyfin's selection mode.
    function selectedCards() {
        var boxes = document.querySelectorAll('.itemSelectionPanel .chkItemSelect');
        var cards = [];
        for (var i = 0; i < boxes.length; i++) {
            var card = boxes[i].checked && boxes[i].closest ? boxes[i].closest('.card[data-id]') : null;
            if (card && cards.indexOf(card) < 0) {
                cards.push(card);
            }
        }
        return cards;
    }

    function checkSelectionMenu() {
        if (!isAdmin()) {
            return;
        }
        var panel = document.querySelector('.selectionCommandsPanel');
        if (!panel) {
            folderIds.panel = null;
            return;
        }
        if (folderIds.panel !== panel) {
            // Selecting just started: fetch the folder list now, so it is ready by the
            // time the menu opens. Fresh each time: downloads add folders.
            folderIds.panel = panel;
            loadFolderIds(0);
        }
        var sheet = document.querySelector('.actionSheet');
        if (!sheet || sheet.getAttribute('data-mtj-seen') || !sheet.querySelector('.actionSheetMenuItem[data-id="selectall"]')) {
            return;
        }
        sheet.setAttribute('data-mtj-seen', '1');
        var cards = selectedCards();
        if (cards.length < 2) {
            return;
        }
        var ids = [];
        for (var i = 0; i < cards.length; i++) {
            if (cards[i].getAttribute('data-isfolder') !== 'true') {
                return;
            }
            ids.push(normalizeId(cards[i].getAttribute('data-id')));
        }
        loadFolderIds(60000).then(function (map) {
            if (!map || !document.body.contains(sheet)) {
                return;
            }
            for (var j = 0; j < ids.length; j++) {
                if (!map[ids[j]]) {
                    return; // Not (all) channel folders of the YouTube library.
                }
            }
            addMergeMenuItem(sheet, ids);
        });
    }

    function addMergeMenuItem(sheet, ids) {
        if (sheet.querySelector('[data-id="' + MERGE_ITEM_ID + '"]')) {
            return;
        }
        var template = sheet.querySelector('.actionSheetMenuItem[data-id="markplayed"]') || sheet.querySelector('.actionSheetMenuItem');
        if (!template) {
            return;
        }
        // A copy of one of Jellyfin's own entries, so it looks and behaves the same in
        // every layout (phone, desktop, TV).
        var item = template.cloneNode(true);
        item.setAttribute('data-id', MERGE_ITEM_ID);
        item.removeAttribute('autofocus');
        var iconNode = item.querySelector('.actionsheetMenuItemIcon');
        if (iconNode) {
            iconNode.className = 'actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons call_merge';
            iconNode.style.visibility = '';
        }
        var text = item.querySelector('.actionSheetItemText');
        if (text) {
            text.textContent = t('mergeFolders');
        }
        var extras = item.querySelectorAll('.listItemBodyText.secondary, .actionSheetItemAsideText');
        for (var i = 0; i < extras.length; i++) {
            extras[i].parentNode.removeChild(extras[i]);
        }
        var after = sheet.querySelector('.actionSheetMenuItem[data-id="delete"]') || sheet.querySelector('.actionSheetMenuItem[data-id="playlist"]');
        if (after) {
            after.parentNode.insertBefore(item, after.nextSibling);
        } else {
            template.parentNode.insertBefore(item, template);
        }
        item.addEventListener('click', function () {
            // Jellyfin closes the menu itself (its own click handler sees the entry) and
            // then steps back through history; navigate only once that is done.
            var started = false;
            var go = function () {
                if (started) {
                    return;
                }
                started = true;
                window.setTimeout(function () { openMergeFromSelection(ids); }, 0);
            };
            sheet.addEventListener('close', go);
            window.setTimeout(go, 2000);
        });
        keepOnScreen(sheet);
    }

    // The menu was positioned before the extra entry made it taller.
    function keepOnScreen(sheet) {
        if (sheet.style.position !== 'fixed') {
            return; // Full-screen (TV) or laid out by CSS.
        }
        var rect = sheet.getBoundingClientRect();
        var overflow = rect.bottom - ((window.innerHeight || document.documentElement.clientHeight) - 10);
        if (overflow > 0) {
            sheet.style.top = Math.max(10, rect.top - overflow) + 'px';
        }
    }

    function openMergeFromSelection(ids) {
        var close = document.querySelector('.selectionCommandsPanel .btnCloseSelectionPanel');
        if (close) {
            close.click();
        }
        var qs = routeQuery().replace(/(^|&)tab=[^&]*/, '');
        qs = (qs ? qs.replace(/^&/, '') + '&' : '') + 'tab=folders&select=' + ids.join(',');
        var path = '/youtube?' + qs;
        try {
            if (window.Emby && window.Emby.Page && typeof window.Emby.Page.show === 'function') {
                window.Emby.Page.show(path);
                return;
            }
        } catch (e) {
            // Fall through to a plain hash change.
        }
        location.hash = '#' + path;
    }

    // ---------------------------------------------------------------- boot

    // Leaving via a React <Link> is a pushState, which fires no event; wrap both
    // history methods (chaining whatever another plugin installed) to notice it.
    function wrapHistory(name) {
        var original = history[name];
        if (typeof original !== 'function' || original.mtjWrapped) {
            return;
        }
        var wrapped = function () {
            var result = original.apply(this, arguments);
            window.setTimeout(sync, 0);
            return result;
        };
        wrapped.mtjWrapped = true;
        history[name] = wrapped;
    }

    function installRouteHooks() {
        // Jellyfin's hidden "not found" view announces itself after (or before) this
        // page does; its pageshow would set the title to "Page not found" and scroll to
        // the top. Its viewshow, which Jellyfin's router waits for, is left alone.
        window.addEventListener('pageshow', function (event) {
            var target = event.target;
            if (target && target.id === 'fallbackPage' && isRoute()) {
                event.stopImmediatePropagation();
            }
        }, true);
        window.addEventListener('popstate', sync, true);
        window.addEventListener('hashchange', sync, true);
        document.addEventListener('viewshow', function (event) {
            if (event.target !== state.page) {
                sync();
            }
        }, true);
        wrapHistory('pushState');
        wrapHistory('replaceState');
    }

    function installDomHooks() {
        var observer = new MutationObserver(scheduleScan);
        observer.observe(document.body, { childList: true, subtree: true });
        window.setInterval(scheduleScan, 3000);
    }

    function start() {
        if (!ready()) {
            window.setTimeout(start, 300);
            return;
        }
        installDomHooks();
        scheduleScan();
        sync();
    }

    window.MeTubeForJellyfin = { version: VERSION, open: navigateToPage };

    // Styles and route handling go in right away, before jellyfin-web has signed in,
    // so a deep link to #/youtube never flashes Jellyfin's "page not found".
    ensureStyles();
    installRouteHooks();
    setRootClass(isRoute());
    start();
})();
