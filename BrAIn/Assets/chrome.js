/* BrAIn top chrome — injected into every page (opt-out via --no-toolbar).
 * Shadow-DOM isolated. Commands -> window.external.sendMessage('brain:...'),
 * state pushes arrive via window.external.receiveMessage({b:9,...}).
 * Latest push is mirrored to window.__brainState and a 'brain-state'
 * CustomEvent is dispatched on document for page-side widgets (newtab tiles).
 * Exposes window.__brainChrome { mount, hide, show } — hide/show is used to
 * keep the bar out of captured screenshots. */
(function () {
    if (window.__brainChrome) return;

    var HOST_ID = '__brain-chrome-host';
    var state = { tabs: [], loading: false, canBack: true, canFwd: false, hist: [] };
    var editing = false;   // true while the user is typing in the omnibox
    var dirty = false;     // true once the user has actually typed (vs. the pre-filled URL)
    var dropIdx = -1;      // selected row in the history dropdown
    var ghostAt = -1;      // inline-completion text starts at this offset in #addr (-1 = none)
    var ghostBlock = null; // query whose ghost completion the user dismissed (Backspace/Left)
    var statusEl = null;   // link-hover status bubble node (inside shadow)
    var wired = false;     // document/window listeners attach once — remounts must not duplicate them
    var lastSig = '';      // tab-strip render signature — skip DOM rebuilds when unchanged
    var findOpen = false;  // find-in-page bar visible
    var lastScrollReport = 0;
    state.bmarks = []; state.bm = false; state.zoom = 100; state.topmost = false;
    state.sess = 0; state.privActive = false;
    state.se = 'google'; state.sel = []; // search engine id + [{i,n}] list
    state.ov = false;      // active tab is a blank new tab painted as an in-doc overlay
    state.wc = false;      // chromeless window — show our own min/max/close
    state.mtl = false;     // macOS inline titlebar — leave room for traffic lights
    state.w = false;       // window currently maximized
    var ovPending = 0;     // optimistic overlay hold (ms deadline) — see holdOverlay()
    var lastPushOv = false;
    var ntpHistSig = '';

    var SVG = {
        back: '<svg viewBox="0 0 16 16"><path d="M10.5 3.5 6 8l4.5 4.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>',
        forward: '<svg viewBox="0 0 16 16"><path d="M5.5 3.5 10 8l-4.5 4.5" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>',
        reload: '<svg viewBox="0 0 16 16"><path d="M13.2 8a5.2 5.2 0 1 1-1.6-3.7" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/><path d="M13.4 1.6v3h-3" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/></svg>',
        stop: '<svg viewBox="0 0 16 16"><path d="m5 5 6 6m0-6-6 6" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>',
        home: '<svg viewBox="0 0 16 16"><path d="M2.6 6.8 8 2.4l5.4 4.4v5.4a1.2 1.2 0 0 1-1.2 1.2H3.8a1.2 1.2 0 0 1-1.2-1.2z" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"/><path d="M6.2 13.4v-3.4h3.6v3.4" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"/></svg>',
        plus: '<svg viewBox="0 0 16 16"><path d="M8 3.4v9.2M3.4 8h9.2" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>',
        close: '<svg viewBox="0 0 16 16"><path d="m4.6 4.6 6.8 6.8m0-6.8-6.8 6.8" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>',
        lock: '<svg viewBox="0 0 16 16"><rect x="4" y="7" width="8" height="6.4" rx="1.4" fill="none" stroke="currentColor" stroke-width="1.4"/><path d="M6 7V5.6a2 2 0 0 1 4 0V7" fill="none" stroke="currentColor" stroke-width="1.4"/></svg>',
        warn: '<svg viewBox="0 0 16 16"><path d="M8 2.2 14.6 13.4H1.4z" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"/><path d="M8 6.4v3.2" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/><circle cx="8" cy="11.6" r=".9" fill="currentColor"/></svg>',
        sparkle: '<svg viewBox="0 0 16 16"><path d="M8 1.8 9.4 5.4 13 6.8 9.4 8.2 8 11.8 6.6 8.2 3 6.8l3.6-1.4z" fill="currentColor"/><path d="M12.6 10.2l.8 1.8 1.8.8-1.8.8-.8 1.8-.8-1.8-1.8-.8 1.8-.8z" fill="currentColor" opacity=".7"/></svg>',
        snd: '<svg viewBox="0 0 16 16"><path d="M2.5 6v4h2.6l3.4 2.8V3.2L5.1 6z" fill="currentColor"/><path d="M10.6 5.6a3.4 3.4 0 0 1 0 4.8M12.2 3.9a5.8 5.8 0 0 1 0 8.2" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/></svg>',
        mute: '<svg viewBox="0 0 16 16"><path d="M2.5 6v4h2.6l3.4 2.8V3.2L5.1 6z" fill="currentColor"/><path d="M10.8 6.2l3.4 3.6M14.2 6.2l-3.4 3.6" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>',
        pip: '<svg viewBox="0 0 16 16"><rect x="1.8" y="3" width="12.4" height="9.4" rx="1.6" fill="none" stroke="currentColor" stroke-width="1.3"/><rect x="7.6" y="7.2" width="5" height="3.6" rx=".8" fill="currentColor"/></svg>',
        star: '<svg viewBox="0 0 16 16"><path d="M8 2.4l1.8 3.6 4 .6-2.9 2.8.7 4L8 11.6l-3.6 1.8.7-4-2.9-2.8 4-.6z" fill="none" stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/></svg>',
        starF: '<svg viewBox="0 0 16 16"><path d="M8 2.4l1.8 3.6 4 .6-2.9 2.8.7 4L8 11.6l-3.6 1.8.7-4-2.9-2.8 4-.6z" fill="currentColor"/></svg>',
        dots: '<svg viewBox="0 0 16 16"><circle cx="8" cy="3.4" r="1.3" fill="currentColor"/><circle cx="8" cy="8" r="1.3" fill="currentColor"/><circle cx="8" cy="12.6" r="1.3" fill="currentColor"/></svg>',
        up: '<svg viewBox="0 0 16 16"><path d="M3.4 10.6 8 6l4.6 4.6" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>',
        dn: '<svg viewBox="0 0 16 16"><path d="M3.4 5.4 8 10l4.6-4.6" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>',
        mag: '<svg viewBox="0 0 16 16"><circle cx="7" cy="7" r="4.4" fill="none" stroke="currentColor" stroke-width="1.6"/><path d="m10.4 10.4 3 3" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>',
        mask: '<svg viewBox="0 0 16 16"><path d="M8 2.6c-2 0-3.6.7-4.3 1.7L3 7.6h10l-.7-3.3C11.6 3.3 10 2.6 8 2.6z" fill="currentColor"/><path d="M3.4 8.4a4.6 4.6 0 0 0 9.2 0z" fill="currentColor" opacity=".75"/><circle cx="5.7" cy="9.6" r=".85" fill="#0b0b13"/><circle cx="10.3" cy="9.6" r=".85" fill="#0b0b13"/></svg>',
        pin: '<svg viewBox="0 0 16 16"><path d="M9.4 2.2 13.8 6.6l-1.5.2-3.2 3.2-.2 3-1.7 1.7.6-3.9-3-3zM3.6 12.4 6 10" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"/><path d="M2.4 13.6l4-4" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>',
        link: '<svg viewBox="0 0 16 16"><path d="M6.5 9.5a3 3 0 0 0 4.2.4l2-2a3 3 0 1 0-4.2-4.2l-1.1 1" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/><path d="M9.5 6.5a3 3 0 0 0-4.2-.4l-2 2a3 3 0 1 0 4.2 4.2l1.1-1" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>',
        copy: '<svg viewBox="0 0 16 16"><rect x="5.5" y="5.5" width="8" height="8" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.4"/><path d="M10.5 5.5v-1a1.5 1.5 0 0 0-1.5-1.5H4.5A1.5 1.5 0 0 0 3 4.5V9a1.5 1.5 0 0 0 1.5 1.5h1" fill="none" stroke="currentColor" stroke-width="1.4"/></svg>',
        dl: '<svg viewBox="0 0 16 16"><path d="M8 2v6.2M5.4 5.8 8 8.4l2.6-2.6" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/><path d="M3 10v2.4A1.8 1.8 0 0 0 4.8 14h6.4a1.8 1.8 0 0 0 1.8-1.8V10" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>',
        doc: '<svg viewBox="0 0 16 16"><path d="M4.2 1.8h4.6L13 6v8.2H4.2z" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"/><path d="M8.8 1.8V6H13" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"/></svg>',
        folder: '<svg viewBox="0 0 16 16"><path d="M1.8 4.2a1.4 1.4 0 0 1 1.4-1.4h2.6l1.6 1.8h5.4a1.4 1.4 0 0 1 1.4 1.4v5.4a1.4 1.4 0 0 1-1.4 1.4H3.2a1.4 1.4 0 0 1-1.4-1.4z" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"/></svg>',
        ext: '<svg viewBox="0 0 16 16"><path d="M6 3.4h7.2V10.6M13 3.6 5.4 11.2" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/><path d="M10.8 12.6H3.6V5.4" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>',
        wmin: '<svg viewBox="0 0 16 16"><path d="M3.6 8h8.8" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>',
        wmax: '<svg viewBox="0 0 16 16"><rect x="3.6" y="3.6" width="8.8" height="8.8" rx="1" fill="none" stroke="currentColor" stroke-width="1.5"/></svg>',
        wres: '<svg viewBox="0 0 16 16"><rect x="4.8" y="4.8" width="7.2" height="7.2" rx="1" fill="none" stroke="currentColor" stroke-width="1.4"/><path d="M6.6 4.8v-.6a1 1 0 0 1 1-1h3.6a1 1 0 0 1 1 1v3.6a1 1 0 0 1-1 1h-.6" fill="none" stroke="currentColor" stroke-width="1.4"/></svg>',
        chk: '<svg viewBox="0 0 16 16"><path d="m3.2 8.4 3 3 6.6-6.8" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>',
        key: '<svg viewBox="0 0 16 16"><circle cx="5.4" cy="10.2" r="3" fill="none" stroke="currentColor" stroke-width="1.4"/><path d="M7.7 7.9 13.2 2.4M10.4 5.2l2.2 2.2M12.2 3.4l2.2 2.2" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>'
    };

    function seName() {
        var l = state.sel;
        for (var i = 0; i < l.length; i++) if (l[i].i === state.se) return l[i].n;
        return 'Google';
    }

    function send(cmd) {
        // Navigating away from the overlay: keep it up until the new document
        // replaces this one — otherwise the hidden previous page flashes.
        if (state.ov && /^navigate:/.test(cmd)) holdOverlay(4000);
        try { window.external && window.external.sendMessage('brain:' + cmd); } catch (e) { }
    }

    // ---------------- optimistic close / switch ----------------
    // Applied locally in the click's frame (same neighbor rule as the host),
    // then confirmed by the host push. Stale pushes in between must not
    // resurrect a closed tab or flip the active pill back.
    var pendClose = {};   // id -> deadline
    var pendAct = null;   // { id, until }

    function predictActivate(next) {
        for (var i = 0; i < state.tabs.length; i++) state.tabs[i].active = state.tabs[i] === next;
        pendAct = { id: next.id, until: Date.now() + 900 };
        state.privActive = !!next.priv;
        if (next.doc) { ovPending = 0; state.ov = false; }            // its page is right underneath
        else if (next.url === 'about:newtab') { state.ov = true; holdOverlay(700); }
        else if (state.ov) holdOverlay(4000); // will navigate — keep the start page until the new doc lands
        var s = sh0();
        if (!s) return;
        render(s); syncNtp(s); updateAddr(s); updateStar(s);
        var om = s.getElementById('omni');
        if (om) om.classList.toggle('priv', !!state.privActive);
    }

    function findTab(id) {
        for (var i = 0; i < state.tabs.length; i++) if (state.tabs[i].id === id) return i;
        return -1;
    }

    function switchTabLocal(id) {
        var i = findTab(id);
        if (i >= 0 && !state.tabs[i].active) predictActivate(state.tabs[i]);
        send('switchtab:' + id);
    }

    function closeTabLocal(id) {
        var i = findTab(id);
        if (i < 0) { send('closetab:' + id); return; }
        var t = state.tabs[i];
        if (t.pin) return;
        if (state.tabs.length > 1) {
            pendClose[id] = Date.now() + 1500;
            state.tabs = state.tabs.slice(0, i).concat(state.tabs.slice(i + 1));
            if (t.active) predictActivate(state.tabs[Math.min(i, state.tabs.length - 1)]);
            else {
                if (t.doc && state.ov) holdOverlay(4000); // host unloads the covered page
                var s = sh0(); if (s) render(s);
            }
        }
        send('closetab:' + id);
    }

    // Filters a host push through pending local actions. Returns true when
    // the push's active tab was overridden (its ov/priv are then stale too).
    function reconcilePending(tabs) {
        var now = Date.now(), overridden = false;
        for (var id in pendClose) {
            var still = false;
            for (var k = 0; k < tabs.length; k++) if (tabs[k].id === id) still = true;
            if (!still || pendClose[id] < now) delete pendClose[id];
        }
        var out = tabs.filter(function (t) { return !pendClose[t.id]; });
        if (pendAct && pendAct.until > now) {
            var hit = null;
            for (var j = 0; j < out.length; j++) if (out[j].id === pendAct.id) hit = out[j];
            if (!hit) pendAct = null;
            else if (hit.active) pendAct = null; // host confirmed
            else {
                for (var m = 0; m < out.length; m++) out[m].active = out[m] === hit;
                overridden = true;
            }
        } else pendAct = null;
        state.tabs = out;
        return overridden;
    }

    // The bar overlays the top of the viewport; push the document down 46px so
    // nothing stays permanently covered at scroll position 0.
    var RESERVED = '46px';
    // Top-anchored position:fixed/sticky elements (navbars, cookie banners)
    // are viewport-anchored — the html margin can't move them, so they stay
    // under the bar. Detect them by sampling the covered strip with
    // elementsFromPoint (cheap: ~12 point queries, no DOM walk) and shift
    // their 'top' down by 46px. Sticky headers are caught once pinned.
    var fixedSeen = new WeakSet();
    var fixedShifted = []; // {el, orig} for restore on hide()
    function ytVars(on) {
        // youtube.com lays out its guide/page from --ytd-toolbar-height inside
        // a position:absolute root (ignores html margin); keep it in sync with
        // the masthead we shift down by RESERVED.
        var de = document.documentElement;
        if (!/(^|\.)youtube\.com$/.test(location.hostname)) return;
        if (on) {
            var v = 'calc(56px + ' + RESERVED + ')';
            de.style.setProperty('--ytd-toolbar-height', v, 'important');
            de.style.setProperty('--ytd-masthead-height', v, 'important');
        } else {
            de.style.removeProperty('--ytd-toolbar-height');
            de.style.removeProperty('--ytd-masthead-height');
        }
    }
    function reserveSpace(on) {
        try {
            var de = document.documentElement;
            if (!de) return;
            if (on === false) {
                ytVars(false);
                de.style.marginTop = '';
                for (var i = 0; i < fixedShifted.length; i++) {
                    var it = fixedShifted[i];
                    if (it.orig) it.el.style.setProperty('top', it.orig);
                    else it.el.style.removeProperty('top');
                }
                fixedShifted = [];
                fixedSeen = new WeakSet();
            } else {
                de.style.setProperty('margin-top', RESERVED, 'important');
                ytVars(true);
                adjustFixed();
            }
        } catch (e) { }
    }
    function adjustFixed() {
        var vw = window.innerWidth, vh = window.innerHeight;
        if (!vw || !vh) return;
        var n = Math.max(6, Math.min(20, Math.ceil(vw / 90)));
        for (var i = 0; i < n; i++) {
            var x = Math.round(vw * (i + 0.5) / n);
            var stack;
            try { stack = document.elementsFromPoint(x, 10); } catch (e) { continue; }
            for (var k = 0; k < stack.length; k++) {
                var el = stack[k];
                if (el.id === HOST_ID || el === document.documentElement || el === document.body) continue;
                var cs;
                try { cs = getComputedStyle(el); } catch (e) { continue; }
                if (cs.position !== 'fixed' && cs.position !== 'sticky') continue;
                // Shift the OUTERMOST fixed ancestor near the top — a nested
                // fixed child moved alone stays trapped in its parent.
                var target = el;
                for (var a = el.parentElement; a && a !== document.body && a !== document.documentElement; a = a.parentElement) {
                    try {
                        var acs = getComputedStyle(a);
                        if ((acs.position === 'fixed' || acs.position === 'sticky') &&
                            a.getBoundingClientRect().top <= el.getBoundingClientRect().top + 4)
                            target = a;
                    } catch (e) { }
                }
                if (fixedSeen.has(target)) break;
                var r = target.getBoundingClientRect();
                // Skip overlays/full-height drawers and small side widgets —
                // only header-shaped bands should move.
                // Narrow pieces count too when they're glued to the top edge and
                // header-sized — sites like Facebook split their header into
                // separate fixed clusters (logo+search, nav, account icons).
                var edgePiece = r.top <= 4 && r.height >= 30 && r.height <= 90 && r.width >= 40;
                if (r.height > vh * 0.65 || r.height < 6 || (r.width < vw * 0.3 && !edgePiece)) break;
                fixedSeen.add(target);
                fixedShifted.push({ el: target, orig: target.style.getPropertyValue('top') || null });
                var t = cs.position === 'sticky' || target === el ? getComputedStyle(target).top : cs.top;
                if (t === 'auto') t = '0px';
                target.style.setProperty('top', 'calc(' + t + ' + ' + RESERVED + ')', 'important');
                break; // one header band per column sample
            }
        }
    }

    function esc(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function hostOf(url) {
        try { return new URL(url).hostname.replace(/^www\./, ''); }
        catch (e) { return url || 'newtab'; }
    }

    function tabHue(id) {
        var h = 0;
        for (var i = 0; i < id.length; i++) h = (h * 31 + id.charCodeAt(i)) | 0;
        return 'hsl(' + (((h % 360) + 360) % 360) + ' 60% 55%)';
    }

    // Aurora: the whole chrome tints to the current site. A curated hue set
    // (no muddy greens/browns) keyed by hostname, so a site always looks the
    // same; internal pages and the start page keep the brand violet.
    var HUES = [212, 228, 252, 272, 292, 322, 346, 8, 28, 46, 152, 172, 192];
    function siteHue(url) {
        try {
            var u = new URL(url);
            if (!/^https?:$/.test(u.protocol)) return 252;
            var s = u.hostname.replace(/^www\./, ''), h = 7;
            for (var i = 0; i < s.length; i++) h = (h * 33 + s.charCodeAt(i)) | 0;
            return HUES[Math.abs(h) % HUES.length];
        } catch (e) { return 252; }
    }
    function applyAccent() {
        var host = document.getElementById(HOST_ID);
        if (!host) return;
        var a = activeTab();
        var hue = a && !state.ov ? siteHue(a.url) : 252;
        if (host._hue === hue) return;
        var first = host._hue === undefined;
        host._hue = hue;
        host.style.setProperty('--h', hue);
        // glow swell when you land on a different site; two identical
        // animations alternate so a new one restarts with no forced reflow
        var bar = host.shadowRoot && host.shadowRoot.querySelector('.bar');
        if (bar && !first) { var on1 = bar.classList.contains('sw1'); bar.classList.remove('sw1', 'sw2'); bar.classList.add(on1 ? 'sw2' : 'sw1'); }
    }

    function prettyUrl(url) {
        try {
            var u = new URL(url);
            return '<b>' + esc(u.hostname.replace(/^www\./, '')) + '</b>' +
                esc(u.pathname + u.search + u.hash);
        } catch (e) { return esc(url); }
    }

    function activeTab() {
        for (var i = 0; i < state.tabs.length; i++) if (state.tabs[i].active) return state.tabs[i];
        return null;
    }

    // ---------------- rendering ----------------
    // Tab elements persist across state pushes (keyed by id) and are
    // reconciled in place — adding a tab animates only the new pill, closing
    // animates out before detach, favicons never re-fetch on unrelated pushes,
    // and activation cross-fades via CSS transitions.

    var tabEls = {};   // id -> live .tab element (reset on mount)
    var firstRender = true; // tabs cascade in on mount (doc swap on tab switch)
    // Boot state baked into the document by the host (window.__brainBoot) —
    // applied once so the strip renders final state on first paint; only
    // bootFresh (the just-created tab) animates. Without it every tab would
    // flash in ~100ms later when the first push arrives.
    var bootApplied = null, bootFresh = null;
    // Loading indicator appears only once a load outlives ~150ms, like real
    // browsers — instant internal-page swaps must not flash spinner/progress.
    var loadStart = 0, loadSlow = false;
    function markLoading() {
        if (state.loading) {
            if (!loadStart) {
                loadStart = Date.now();
                loadSlow = false;
                setTimeout(function () {
                    if (!state.loading) return;
                    loadSlow = true;
                    var s = sh0();
                    if (s) { render(s); updateNav(s); updateProg(s); }
                }, 160);
            }
        } else { loadStart = 0; loadSlow = false; }
    }

    function makeTabEl(sh, t) {
        var el = document.createElement('div');
        el.className = 'tab';
        el.setAttribute('data-id', t.id);
        el.innerHTML = '<span class="dot"></span><span class="t"></span>' +
            '<span class="snd"></span>' +
            '<span class="x" title="Close tab">' + SVG.close + '</span>';
        el.draggable = true;
        el.addEventListener('click', function (e) {
            var sn = e.target.closest && e.target.closest('.snd');
            if (sn) { send('mute:' + el.getAttribute('data-id')); e.stopPropagation(); return; }
            var x = e.target.closest && e.target.closest('.x');
            if (x) { if (!el._pin) closeTabLocal(el.getAttribute('data-id')); e.stopPropagation(); return; }
            switchTabLocal(el.getAttribute('data-id'));
        });
        el.addEventListener('auxclick', function (e) {
            if (e.button === 1 && !el._pin) { // middle-click closes the tab
                e.preventDefault();
                closeTabLocal(el.getAttribute('data-id'));
            }
        });
        el.addEventListener('contextmenu', function (e) {
            e.preventDefault(); e.stopPropagation();
            showTabMenu(sh, e.clientX, e.clientY, el.getAttribute('data-id'));
        });
        el.addEventListener('dragstart', function (e) {
            dragId = el.getAttribute('data-id');
            el.classList.add('dragging');
            try { e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', dragId); } catch (err) { }
        });
        el.addEventListener('dragend', function () {
            el.classList.remove('dragging');
            clearDropMark(sh);
            dragId = null;
        });
        return el;
    }

    function syncTabEl(el, t) {
        el.classList.toggle('active', !!t.active);
        el.classList.toggle('priv', !!t.priv);
        el.classList.toggle('pin', !!t.pin);
        el._pin = !!t.pin;
        var h = hostOf(t.url);
        var title = t.title || h;
        if (el._title !== title || el._url !== t.url) {
            el._title = title; el._url = t.url;
            el.querySelector('.t').textContent = title;
            el.title = (t.title || '') + '\n' + (t.url || '') +
                (t.priv ? '\nPrivate — no history; site data wiped on close' : '');
        }
        // Speaker badge: crossed-out while muted, plain while sound plays.
        // Click toggles mute (the tab itself isn't switched).
        var sndMode = t.mute ? 'm' : t.aud ? 'a' : '';
        if (el._snd !== sndMode) {
            el._snd = sndMode;
            var sn = el.querySelector('.snd');
            sn.className = 'snd' + (sndMode ? ' on' + (sndMode === 'm' ? ' muted' : '') : '');
            sn.innerHTML = sndMode === 'm' ? SVG.mute : sndMode === 'a' ? SVG.snd : '';
            sn.title = t.mute ? 'Unmute tab' : 'Mute tab';
        }
        var busy = t.active && state.loading && loadSlow;
        var icon = t.icon || '';
        var letter = (title || '?').trim().charAt(0).toUpperCase();
        var faceSig = letter + '|' + icon + '|' + (busy ? 'B' : '') + (t.priv ? 'P' : '') + '|' + t.id;
        if (el._face !== faceSig) {
            el._face = faceSig;
            var dot = el.querySelector('.dot');
            if (busy) {
                dot.className = 'dot spin';
                dot.style.background = '';
                dot.innerHTML = '';
            } else if (t.priv) {
                dot.className = 'dot mask';
                dot.style.background = 'rgba(124,124,255,.16)';
                dot.innerHTML = SVG.mask;
            } else {
                dot.className = 'dot';
                dot.style.background = tabHue(t.id);
                // Favicon replaces the letter — a transparent icon drawn over
                // the glyph reads as the two overlapping each other.
                dot.innerHTML = icon
                    ? '<img class="fav" src="' + esc(icon) + '" alt="" data-l="' + esc(letter) +
                        '" onerror="this.parentNode.textContent=this.dataset.l">'
                    : esc(letter);
            }
        }
    }

    function render(sh) {
        var strip = sh.getElementById('tabs');
        if (!strip) return;
        applyAccent();
        // Signature check: state pushes arrive on every load event; reconcile
        // only when tab data actually changed.
        var sig = ((state.loading && loadSlow) ? 'L' : '.');
        for (var i = 0; i < state.tabs.length; i++) {
            var t0 = state.tabs[i];
            sig += t0.id + ',' + (t0.active ? 1 : 0) + ',' + (t0.title || '') + ',' + (t0.icon || '') +
                ',' + (t0.mute ? 1 : 0) + (t0.aud ? 1 : 0) + ';';
        }
        if (sig === lastSig) { updateNav(sh); updateProg(sh); return; }
        lastSig = sig;

        var seen = {};
        var prev = null;
        for (var i = 0; i < state.tabs.length; i++) {
            var t = state.tabs[i];
            seen[t.id] = 1;
            var el = tabEls[t.id];
            var fresh = !el || el.parentNode !== strip;
            if (fresh) {
                el = makeTabEl(sh, t);
                tabEls[t.id] = el;               // .tab's tabin anim plays on insert
                if (firstRender) {
                    if (bootApplied) {
                        // Boot state: strip appears instantly — existing tabs
                        // get a permanent noanim class (never cleared: doing
                        // so would restart tabin mid-flight); only the freshly
                        // created pill slides in.
                        if (t.id !== bootFresh) el.classList.add('noanim');
                    }
                    else el.style.animationDelay = Math.min(i * 40, 360) + 'ms';
                }
            }
            syncTabEl(el, t);
            var anchor = prev ? prev.nextSibling : strip.firstChild;
            if (el !== anchor) strip.insertBefore(el, anchor); // move keeps the node
            if (fresh) {
                // reveal the new tab at the strip's edge, like real browsers —
                // measured next frame: a sync offsetLeft read here forced a
                // full-document layout (~30ms) before the first paint.
                (function (e2) {
                    requestAnimationFrame(function () {
                        if (!e2.isConnected || strip.scrollWidth <= strip.clientWidth) return;
                        var l = e2.offsetLeft, r = l + e2.offsetWidth;
                        if (r > strip.scrollLeft + strip.clientWidth)
                            strip.scrollTo({ left: r - strip.clientWidth, behavior: 'smooth' });
                        else if (l < strip.scrollLeft)
                            strip.scrollTo({ left: l, behavior: 'smooth' });
                    });
                })(el);
            }
            prev = el;
        }
        // The empty render at mount must not consume the cascade — state
        // arrives via the first push, which is when tabs actually appear.
        if (state.tabs.length) firstRender = false;
        for (var id in tabEls) {
            if (seen[id]) continue;
            var dead = tabEls[id];
            delete tabEls[id];
            if (dead.parentNode === strip) {
                dead.classList.add('closing');
                dead.draggable = false;
                (function (d) { setTimeout(function () { d.remove(); }, 200); })(dead);
            }
        }
        updateNav(sh);
        updateProg(sh);
    }

    // ---------------- tab context menu ----------------

    function showTabMenu(sh, x, y, id) {
        var m = sh.getElementById('tmenu');
        var tb = null;
        for (var i = 0; i < state.tabs.length; i++)
            if (state.tabs[i].id === id) tb = state.tabs[i];
        var items = [
            [tb && tb.pin ? 'unpin' : 'pin', tb && tb.pin ? 'Unpin tab' : 'Pin tab'],
            ['mute', tb && tb.mute ? 'Unmute tab' : 'Mute tab'],
            ['newprivtab', 'New private tab'],
            null,
            ['reloadtab', 'Reload'],
            ['dupetab', 'Duplicate'],
            ['closeothers', 'Close other tabs'],
            ['closeright', 'Close tabs to the right'],
        ];
        if (!(tb && tb.pin)) { items.push(null); items.push(['closetab', 'Close']); }
        var html = '';
        for (var i = 0; i < items.length; i++) {
            var it = items[i];
            html += it === null ? '<div class="msep"></div>' :
                '<div class="mi" data-c="' + it[0] + '">' + it[1] + '</div>';
        }
        m.innerHTML = html;
        m.style.display = 'block';
        m.style.left = Math.min(x, window.innerWidth - 190) + 'px';
        m.style.top = Math.min(y, window.innerHeight - 200) + 'px';
        m.querySelectorAll('.mi').forEach(function (el) {
            el.addEventListener('mousedown', function (e) {
                e.preventDefault(); e.stopPropagation();
                var c = el.getAttribute('data-c');
                // newprivtab takes a URL arg — never the tab id
                if (c === 'newprivtab') openNewTab(true);
                else if (c === 'closetab') closeTabLocal(id);
                else send(c + ':' + id);
                m.style.display = 'none';
            });
        });
        var off = function () {
            m.style.display = 'none';
            document.removeEventListener('mousedown', off, true);
        };
        setTimeout(function () { document.addEventListener('mousedown', off, true); }, 0);
    }

    // ---------------- strip drag-to-reorder ----------------

    var dragId = null;

    function liveTabs(strip) {
        // skip tabs mid close-animation — they're already gone logically
        return Array.prototype.slice.call(strip.querySelectorAll('.tab:not(.closing)'));
    }

    function clearDropMark(sh) {
        var strip = sh.getElementById('tabs');
        if (!strip) return;
        strip.querySelectorAll('.dropmark,.dropmark-r').forEach(function (el) {
            el.classList.remove('dropmark', 'dropmark-r');
        });
    }

    function wireStripDnd(sh) {
        var strip = sh.getElementById('tabs');
        strip.addEventListener('dragover', function (e) {
            if (!dragId) return;
            e.preventDefault();
            var tabs = liveTabs(strip);
            clearDropMark(sh);
            var marked = false;
            for (var i = 0; i < tabs.length; i++) {
                var r = tabs[i].getBoundingClientRect();
                if (e.clientX < r.left + r.width / 2) {
                    tabs[i].classList.add('dropmark');
                    marked = true; break;
                }
            }
            if (!marked && tabs.length) tabs[tabs.length - 1].classList.add('dropmark-r');
        });
        strip.addEventListener('dragleave', function (e) {
            if (!strip.contains(e.relatedTarget)) clearDropMark(sh);
        });
        strip.addEventListener('drop', function (e) {
            e.preventDefault();
            if (!dragId) return;
            clearDropMark(sh);
            var tabs = liveTabs(strip);
            var dragIdx = -1, idx = tabs.length;
            for (var i = 0; i < tabs.length; i++) {
                if (tabs[i].getAttribute('data-id') === dragId) dragIdx = i;
                var r = tabs[i].getBoundingClientRect();
                if (e.clientX < r.left + r.width / 2 && idx === tabs.length) { idx = i; }
            }
            // Host removes the dragged tab before inserting at idx — when
            // moving right, the slot computed against the pre-removal strip
            // is one too far.
            if (dragIdx >= 0 && dragIdx < idx) idx--;
            send('movetab:' + dragId + ':' + idx);
            dragId = null;
        });
        // Horizontal wheel over the strip scrolls it instead of the page.
        strip.addEventListener('wheel', function (e) {
            if (!e.deltaY) return;
            e.preventDefault();
            strip.scrollLeft += e.deltaY;
        }, { passive: false });
    }

    function updateNav(sh) {
        var b = sh.querySelector('[data-cmd="back"]');
        var f = sh.querySelector('[data-cmd="forward"]');
        if (b) b.classList.toggle('dis', !state.canBack);
        if (f) f.classList.toggle('dis', !state.canFwd);
        var rel = sh.getElementById('rel');
        if (rel) {
            var stoppable = state.loading && loadSlow;
            rel.innerHTML = stoppable ? SVG.stop : SVG.reload;
            rel.setAttribute('data-cmd', stoppable ? 'stop' : 'reload');
            rel.title = stoppable ? 'Stop loading (Esc)' : 'Reload (Ctrl+R)';
        }
    }

    function updateProg(sh) {
        var p = sh.getElementById('prog');
        // macOS draws the progress line natively (MacWindow.SetLoadingBar) — WebKit
        // freezes in-page painting during a pending navigation, so this one can't show.
        if (p) p.classList.toggle('on', !!(state.loading && loadSlow) && !state.mtl);
        var bar = sh.querySelector('.bar');
        if (bar) bar.classList.toggle('ld', !!(state.loading && loadSlow));
    }

    var lastAddrUrl = null;
    function updateAddr(sh) {
        var addr = sh.getElementById('addr');
        var lock = sh.getElementById('lock');
        if (!addr || editing) return;
        var url = location.href;
        if (state.ov) {
            if (lastAddrUrl === '__ov__') return;
            lastAddrUrl = '__ov__';
            addr.innerHTML = '<span class="dim">Search or type a URL</span>';
            if (lock) { lock.style.opacity = '0'; lock.title = ''; }
            return;
        }
        if (url === 'about:blank') {
            if (lastAddrUrl === 'about:blank') return;
            lastAddrUrl = 'about:blank';
            addr.innerHTML = '<span class="dim">Search or type a URL</span>';
            if (lock) { lock.style.opacity = '0'; lock.title = ''; }
            return;
        }
        if (url === lastAddrUrl) return;
        lastAddrUrl = url;
        addr.innerHTML = prettyUrl(url);
        updateStar(sh);
        if (lock) {
            var https = url.startsWith('https:');
            lock.style.opacity = url.startsWith('http') ? '1' : '.35';
            lock.innerHTML = https ? SVG.lock : SVG.warn;
            lock.classList.toggle('bad', url.startsWith('http') && !https);
            lock.title = https ? 'Secure connection' : 'Not secure';
        }
    }

    // ---------------- omnibox history dropdown ----------------

    // The query is the text the user typed — minus any inline-completion
    // ghost tail currently selected at the end of the field.
    function readQuery() {
        var s = sh0();
        var inp = s && s.getElementById('addrIn');
        var t = inp ? inp.value || '' : '';
        return ghostAt >= 0 && t.length >= ghostAt ? t.slice(0, ghostAt) : t;
    }

    function filteredHist() {
        var s = sh0();
        if (!s) return [];
        // Before the user types, show everything (like a real omnibox).
        var q = dirty ? readQuery().trim() : '';
        var ql = q.toLowerCase();
        var seen = {};
        var out = [];
        // Top row echoes the typed text — "Go to" for domain-shaped input,
        // a Google search otherwise (the host's NormalizeUrl resolves both).
        if (q) out.push({ s: 1, q: q });
        // Bookmarks pin to the top of the dropdown.
        var bmN = 0;
        for (var i = 0; i < state.bmarks.length && bmN < 3; i++) {
            var b = state.bmarks[i];
            var bu = b && b.u;
            if (!bu || seen[bu]) continue;
            if (ql && bu.toLowerCase().indexOf(ql) < 0 && (b.t || '').toLowerCase().indexOf(ql) < 0) continue;
            seen[bu] = 1; bmN++;
            out.push({ u: bu, t: b.t, i: b.i, bm: 1 });
        }
        var scored = [];
        for (var j = 0; j < state.hist.length; j++) {
            var it = state.hist[j];
            var u = typeof it === 'string' ? it : it.u;
            if (!u || seen[u]) continue;
            var sc = 0;
            if (ql) {
                var ul = u.toLowerCase();
                var bare = ul.replace(/^https?:\/\/(www\.)?/, '');
                if (ul.indexOf(ql) === 0 || bare.indexOf(ql) === 0 || hostOf(u).toLowerCase().indexOf(ql) === 0) sc = 100;
                else if (ul.indexOf(ql) >= 0) sc = 40;
                else if (((it && it.t) || '').toLowerCase().indexOf(ql) >= 0) sc = 25;
                else continue;
                sc += Math.min((it && it.v) || 1, 20); // frequent visits float up
            }
            seen[u] = 1;
            scored.push({ u: u, t: (it && it.t) || null, i: (it && it.i) || null, bm: 0, sc: sc });
        }
        if (ql) scored.sort(function (a, b) { return b.sc - a.sc; });
        for (var k = 0; k < scored.length && out.length < 9; k++) out.push(scored[k]);
        return out;
    }

    // Chrome-style inline autocomplete: extend the typed prefix to the best
    // matching URL and leave the appended tail selected, so typing replaces
    // it and Right/End/Tab accepts it.
    function applyInline(sh) {
        var inp = sh.getElementById('addrIn');
        if (!inp || !dirty) return;
        if (inp.selectionStart !== inp.value.length) return; // caret must be at the end
        var q = inp.value || '';
        if (!q || q === ghostBlock) return;
        var ql = q.toLowerCase();
        var items = filteredHist(), vis = null;
        for (var i = 0; i < items.length; i++) {
            var u = items[i].u;
            if (!u) continue;
            var bare = u.replace(/^https?:\/\/(www\.)?/, '');
            if (bare.toLowerCase().indexOf(ql) === 0 && bare.length > q.length) { vis = bare; break; }
            if (u.toLowerCase().indexOf(ql) === 0 && u.length > q.length) { vis = u; break; }
        }
        if (!vis) return;
        inp.value = q + vis.slice(q.length); // typed case preserved
        ghostAt = q.length;
        inp.setSelectionRange(q.length, inp.value.length);
    }

    function renderDrops(sh) {
        var box = sh.getElementById('drops');
        if (!box) return;
        if (!editing) { box.style.display = 'none'; return; }
        var items = filteredHist();
        if (!items.length) { box.style.display = 'none'; return; }
        var html = '';
        for (var i = 0; i < items.length; i++) {
            var it = items[i];
            var sel = i === dropIdx ? ' sel' : '';
            if (it.s) {
                var go = /^[^\s]+\.[^\s]+$/.test(it.q);
                html += '<div class="drop' + sel + '" data-i="' + i + '"><span class="dic">' + SVG.mag + '</span>' +
                    '<span class="du">' + (go ? 'Go to <b>' + esc(it.q) + '</b>' : 'Search ' + esc(seName()) + ' for <b>' + esc(it.q) + '</b>') + '</span></div>';
                continue;
            }
            var ic = it.i
                ? '<img class="fav" src="' + esc(it.i) + '" alt="" onerror="this.remove()">' : '';
            html += '<div class="drop' + sel + '" data-i="' + i + '">' +
                (it.bm ? '<span class="bmstar">' + SVG.starF + '</span>' : '') +
                ic +
                (it.t ? '<span class="dt2">' + esc(it.t) + '</span>' : '') +
                '<span class="du">' + prettyUrl(it.u) + '</span></div>';
        }
        box.innerHTML = html;
        // '' would fall back to the stylesheet's display:none — must be
        // an explicit value or the dropdown never becomes visible.
        box.style.display = 'block';
        box.querySelectorAll('.drop').forEach(function (el) {
            el.addEventListener('mousedown', function (e) {
                e.preventDefault(); e.stopPropagation(); // keep the omnibox focused
                var i = +el.getAttribute('data-i');
                var it = filteredHist()[i];
                if (it) send('navigate:' + (it.s ? it.q : it.u));
                finishEditing(sh);
            });
            // The click after mousedown must not bubble to .omni (it would
            // re-enter editing mode right after we picked an item).
            el.addEventListener('click', function (e) { e.stopPropagation(); });
        });
    }

    function sh0() { var h = document.getElementById(HOST_ID); return h ? h.shadowRoot : null; }

    function startEditing(sh) {
        var addr = sh.getElementById('addr'), inp = sh.getElementById('addrIn');
        if (editing || !addr || !inp) return;
        editing = true;
        dirty = false;
        ghostAt = -1; ghostBlock = null;
        addr.style.display = 'none';
        inp.style.display = 'block';
        inp.value = location.href === 'about:blank' ? '' : location.href;
        inp.focus();
        inp.select();
        dropIdx = -1;
        renderDrops(sh);
    }

    function finishEditing(sh) {
        var addr = sh.getElementById('addr'), inp = sh.getElementById('addrIn');
        editing = false;
        dropIdx = -1;
        ghostAt = -1; ghostBlock = null;
        if (inp) inp.style.display = 'none';
        if (addr) addr.style.display = '';
        renderDrops(sh);
        updateAddr(sh);
    }

    // ---------------- page context menu ----------------
    // WebView2 suppresses its own context menu when the page handles
    // contextmenu, so our preventDefault fully replaces it. Editable fields
    // are left alone — the native cut/copy/paste menu belongs there.

    function showPageMenu(sh, x, y, link, hasSel, target) {
        var m = sh.getElementById('pmenu');
        pgMenuTarget = target;
        var items = [];
        if (link) {
            items.push(['olink', 'Open link in new tab']);
            items.push(['clink', 'Copy link address']);
            items.push(null);
        }
        if (hasSel) { items.push(['copy', 'Copy']); items.push(null); }
        items.push(['back', 'Back']);
        items.push(['forward', 'Forward']);
        items.push(['reload', 'Reload']);
        items.push(null);
        items.push(['newprivtab', 'New private tab']);
        items.push(null);
        items.push(['vsrc', 'View source']);
        items.push(['help', 'How to use']);
        items.push(null);
        items.push(['inspect', 'Inspect element', 'ac']);
        var html = '';
        for (var i = 0; i < items.length; i++)
            html += items[i] === null ? '<div class="msep"></div>' :
                '<div class="mi' + (items[i][2] ? ' ' + items[i][2] : '') + '" data-c="' + items[i][0] + '">' + items[i][1] + '</div>';
        m.innerHTML = html;
        m.style.display = 'block';
        // clamp against the menu's real size once rendered
        var mw = m.offsetWidth, mh = m.offsetHeight;
        m.style.left = Math.min(x, Math.max(0, window.innerWidth - mw - 8)) + 'px';
        m.style.top = Math.min(y, Math.max(0, window.innerHeight - mh - 8)) + 'px';
        m.querySelectorAll('.mi').forEach(function (el) {
            el.addEventListener('mousedown', function (e) {
                e.preventDefault(); e.stopPropagation();
                m.style.display = 'none';
                var c = el.getAttribute('data-c');
                if (c === 'olink') send('newtab:' + link);
                else if (c === 'clink') copyText(link);
                else if (c === 'copy') copyText((getSelection() || '').toString());
                else if (c === 'vsrc') send('newtab:view-source:' + location.href);
                else if (c === 'help') send('newtab:about:help');
                else if (c === 'newprivtab') openNewTab(true);
                else if (c === 'inspect') inspectEl(sh, pgMenuTarget);
                else send(c); // back/forward/reload
            });
        });
        var off = function () {
            m.style.display = 'none';
            document.removeEventListener('mousedown', off, true);
        };
        setTimeout(function () { document.addEventListener('mousedown', off, true); }, 0);
    }

    var pgMenuTarget = null;

    // Right-click menu for password fields — generated-password fill plus
    // the editing essentials the native menu would have shown.
    function showPwMenu(sh, x, y, el) {
        var m = sh.getElementById('pmenu');
        pgMenuTarget = el;
        var items = [
            ['genpw', 'Insert generated password', 'ac'],
            null,
            ['cut', 'Cut'],
            ['copy', 'Copy'],
            ['paste', 'Paste'],
            null,
            ['pwmgr', 'Manage passwords…'],
            ['inspect', 'Inspect element', 'ac'],
        ];
        var html = '';
        for (var i = 0; i < items.length; i++)
            html += items[i] === null ? '<div class="msep"></div>' :
                '<div class="mi' + (items[i][2] ? ' ' + items[i][2] : '') + '" data-c="' + items[i][0] + '">' + items[i][1] + '</div>';
        m.innerHTML = html;
        m.style.display = 'block';
        var mw = m.offsetWidth, mh = m.offsetHeight;
        m.style.left = Math.min(x, Math.max(0, window.innerWidth - mw - 8)) + 'px';
        m.style.top = Math.min(y, Math.max(0, window.innerHeight - mh - 8)) + 'px';
        m.querySelectorAll('.mi').forEach(function (it) {
            it.addEventListener('mousedown', function (e) {
                e.preventDefault(); e.stopPropagation();
                m.style.display = 'none';
                var c = it.getAttribute('data-c');
                if (c === 'genpw') fillGeneratedPassword(el);
                else if (c === 'cut' || c === 'copy') { el.focus(); try { document.execCommand(c); } catch (e2) { } }
                else if (c === 'paste') { el.focus(); send('clipread'); }
                else if (c === 'pwmgr') send('navigate:about:passwords');
                else if (c === 'inspect') inspectEl(sh, el);
            });
        });
        var off = function () {
            m.style.display = 'none';
            document.removeEventListener('mousedown', off, true);
        };
        setTimeout(function () { document.addEventListener('mousedown', off, true); }, 0);
    }

    function genPassword() {
        // crypto-random, 18 chars, every class covered, no ambiguous glyphs
        var U = 'ABCDEFGHJKLMNPQRSTUVWXYZ', L = 'abcdefghijkmnpqrstuvwxyz',
            D = '23456789', S = '!@#$%&*+-=?', all = U + L + D + S;
        var buf = new Uint32Array(18), out = [];
        crypto.getRandomValues(buf);
        for (var i = 0; i < 18; i++) out.push(all[buf[i] % all.length]);
        out[0] = U[buf[0] % U.length]; out[1] = L[buf[1] % L.length];
        out[2] = D[buf[2] % D.length]; out[3] = S[buf[3] % S.length];
        return out.join('');
    }

    // Setter that controlled inputs (React & co.) actually notice.
    function setFieldValue(el, v) {
        var d = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
        if (d && d.set) d.set.call(el, v); else el.value = v;
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
    }

    function insertFieldText(el, txt) {
        if (!el || txt == null) return;
        el.focus();
        var s = el.selectionStart || 0, e2 = el.selectionEnd || 0;
        setFieldValue(el, (el.value || '').slice(0, s) + txt + (el.value || '').slice(e2));
        try { el.setSelectionRange(s + txt.length, s + txt.length); } catch (e3) { }
    }

    function fillGeneratedPassword(el) {
        if (!el) return;
        var pw = genPassword();
        setFieldValue(el, pw);
        // A single sibling password field is almost always "confirm".
        var f = el.form;
        if (f) {
            var others = [].slice.call(f.querySelectorAll(PW_SEL))
                .filter(function (x) { return x !== el && !x.disabled && !x.readOnly; });
            if (others.length === 1) setFieldValue(others[0], pw);
        }
        el.focus();
        try { el.select(); } catch (e) { }
    }

    // Reveal/hide eye that follows the focused password field. Lives in the
    // shadow root so page CSS can't restyle it; type flips mark the field so
    // our context menu still treats a revealed input as a password field.
    var pwEyeField = null;
    var PW_SEL = 'input[type="password"],input[data-brainpw],' +
        'input[autocomplete~="new-password"],input[autocomplete~="current-password"]';

    function positionPwEye(sh) {
        var b = sh.getElementById('pweye');
        if (!pwEyeField || !pwEyeField.isConnected) { b.style.display = 'none'; pwEyeField = null; return; }
        var r = pwEyeField.getBoundingClientRect();
        if (r.width < 30 || r.bottom < 0 || r.top > window.innerHeight || r.right < 0 || r.left > window.innerWidth) {
            b.style.display = 'none'; return;
        }
        var s = Math.min(16, r.height - 8);
        if (s < 10) { b.style.display = 'none'; return; }
        b.style.width = s + 'px'; b.style.height = s + 'px';
        b.style.left = (r.right - s - 8) + 'px';
        b.style.top = (r.top + (r.height - s) / 2) + 'px';
        b.style.display = 'grid';
    }

    function hidePwEye(sh) {
        var b = sh && sh.getElementById('pweye');
        if (b) b.style.display = 'none';
        pwEyeField = null;
    }

    function wirePwEye() {
        document.addEventListener('focusin', function (e) {
            var sh = sh0(); if (!sh) return;
            var t = e.target;
            if (dtPicking || !t || !t.matches || !t.matches(PW_SEL)) { hidePwEye(sh); return; }
            pwEyeField = t;
            var b = sh.getElementById('pweye');
            b.classList.toggle('on', t.type !== 'password');
            b.title = t.type !== 'password' ? 'Hide password' : 'Show password';
            positionPwEye(sh);
        }, true);
        document.addEventListener('focusout', function () {
            setTimeout(function () {
                if (document.activeElement !== pwEyeField) hidePwEye(sh0());
            }, 130);
        }, true);
        document.addEventListener('scroll', function () {
            var sh = sh0(); if (sh && pwEyeField) positionPwEye(sh);
        }, true);
        window.addEventListener('resize', function () {
            var sh = sh0(); if (sh && pwEyeField) positionPwEye(sh);
        });
        document.addEventListener('mousedown', function (e) {
            // retargeted: clicks on the eye arrive with target = shadow host
            if (!pwEyeField || (e.target && e.target.id === HOST_ID)) return;
            var r = pwEyeField.getBoundingClientRect();
            if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) {
                var sh = sh0(); if (sh) hidePwEye(sh);
            }
        }, true);
    }

    // ---------------- password vault ----------------
    // Capture on submit (plus a pagehide fallback for XHR logins) → the host
    // compares against the vault and pushes b:24 only when saving makes
    // sense. Creds arrive as b:23 — one entry autofills silently, several
    // open the fill dropdown on field focus. Origins are checked host-side.

    var pwCreds = null;    // this origin's saved logins (b:23) — {id,u}, no passwords
    var pwDropFor = null;  // field the fill dropdown is anchored to
    var pwFillField = null; // field awaiting an authenticated b:26 credential
    var pwAsked = false;   // pwlist already requested for this document
    var pwCandSent = false;
    var pwSaveData = null;

    function pwVisible(el) {
        if (!el || el.disabled || el.readOnly) return false;
        var r = el.getBoundingClientRect();
        if (r.width < 4 || r.height < 4) return false;
        var s = getComputedStyle(el);
        return s.visibility !== 'hidden' && s.display !== 'none';
    }

    function pwFields() {
        return [].slice.call(document.querySelectorAll(PW_SEL)).filter(pwVisible);
    }

    var PW_USER_SEL = 'input[type="text"],input[type="email"],input[type="tel"],' +
        'input:not([type]),input[autocomplete~="username"],input[autocomplete~="email"]';

    // Username partner = the closest visible text-ish input before the
    // password field inside its form (or the document when formless).
    function pwUserFor(pw) {
        var scope = pw.form || document;
        var cand = [].slice.call(scope.querySelectorAll(PW_USER_SEL))
            .filter(function (c) { return c !== pw && pwVisible(c); });
        var before = cand.filter(function (c) {
            return !!(pw.compareDocumentPosition(c) & 2); // PRECEDING
        });
        return before.length ? before[before.length - 1] : (cand[0] || null);
    }

    function pwPartnerFor(field) {
        if (field.matches(PW_SEL)) return field;
        var scope = field.form || document;
        return [].slice.call(scope.querySelectorAll(PW_SEL)).filter(pwVisible)[0] || null;
    }

    function maybePwFetch() {
        if (pwAsked || !pwFields().length) return;
        pwAsked = true;
        send('pwlist');
    }

    function onPwCreds(sh, items) {
        pwCreds = items;
        if (!items.length) return;
        var ae = document.activeElement;
        if (ae && ae.matches && pwPartnerFor(ae) &&
            (ae.matches(PW_SEL) || ae.matches(PW_USER_SEL)))
            showPwDrop(sh, ae);
    }

    // b:26 — the authenticated credential for the picked row (or deny).
    function onPwFill(sh, j) {
        if (j.deny) { // auth cancelled — keep the pick list within reach
            if (pwFillField && pwFillField.isConnected) showPwDrop(sh, pwFillField);
            return;
        }
        var anchor = pwFillField;
        pwFillField = null;
        if (!anchor || !anchor.isConnected) return;
        var pw = anchor.matches(PW_SEL) ? anchor : pwPartnerFor(anchor);
        if (!pw) return;
        var uf = pwUserFor(pw);
        if (uf && j.u) setFieldValue(uf, j.u);
        setFieldValue(pw, j.p || '');
        pw.focus();
        try { var n = pw.value.length; pw.setSelectionRange(n, n); } catch (e) { }
    }

    function hidePwDrop(sh) {
        var d = sh && sh.getElementById('pwfill');
        if (d) d.style.display = 'none';
        pwDropFor = null;
    }

    function positionPwDrop(sh) {
        var d = sh.getElementById('pwfill');
        if (!pwDropFor || !pwDropFor.isConnected) { hidePwDrop(sh); return; }
        var r = pwDropFor.getBoundingClientRect();
        if (r.width < 10 || r.bottom < 0 || r.top > innerHeight) { d.style.display = 'none'; return; }
        d.style.left = Math.min(Math.max(4, r.left), Math.max(4, innerWidth - d.offsetWidth - 8)) + 'px';
        d.style.top = (r.bottom + 6) + 'px';
        d.style.display = 'block';
    }

    function showPwDrop(sh, field) {
        var d = sh.getElementById('pwfill');
        if (!pwCreds || !pwCreds.length || !pwPartnerFor(field)) { hidePwDrop(sh); return; }
        pwDropFor = field;
        var html = '';
        for (var i = 0; i < pwCreds.length; i++) {
            var c = pwCreds[i];
            html += '<div class="mi" data-i="' + i + '"><span class="u">' + SVG.key + esc(c.u || '(no username)') + '</span>' +
                '<span class="h">••••••••</span></div>';
        }
        html += '<div class="mi mft" data-mgr="1"><span class="u">Manage passwords…</span></div>';
        d.innerHTML = html;
        d.style.minWidth = Math.min(280, Math.max(210, field.getBoundingClientRect().width)) + 'px';
        positionPwDrop(sh);
        d.querySelectorAll('.mi').forEach(function (it) {
            it.addEventListener('mousedown', function (e) {
                e.preventDefault(); e.stopPropagation();
                var mgr = it.getAttribute('data-mgr');
                var c = mgr ? null : pwCreds[+it.getAttribute('data-i')];
                hidePwDrop(sh);
                if (mgr) { send('navigate:about:passwords'); return; }
                // Pick → host authenticates (Touch ID / PIN) then b:26 fills.
                if (c) { pwFillField = field; send('pwfill:' + c.id); }
            });
        });
    }

    function showPwSave(sh, o, u, p) {
        var b = sh.getElementById('pwsave');
        pwSaveData = { u: u, p: p };
        var host = o;
        try { host = new URL(o).hostname.replace(/^www\./, ''); } catch (e) { }
        b.innerHTML = '<span class="k">' + SVG.key + '</span>' +
            '<span class="tx">Save password for <b>' + esc(host) + '</b>' +
            (u ? ' as <b>' + esc(u) + '</b>' : '') + '?</span>' +
            '<button class="sv">Save</button><button class="nv">Never</button>' +
            '<button class="xx">' + SVG.close + '</button>';
        var hide = function () { b.style.display = 'none'; pwSaveData = null; };
        b.querySelector('.sv').addEventListener('click', function () {
            if (pwSaveData) send('pwsave:' + JSON.stringify(pwSaveData));
            hide();
        });
        b.querySelector('.nv').addEventListener('click', function () { send('pwskip'); hide(); });
        b.querySelector('.xx').addEventListener('click', function () { send('pwdismiss'); hide(); });
        clearTimeout(b._t);
        b._t = setTimeout(hide, 25000);
        b.style.display = 'flex';
    }

    function wirePwMgr() {
        maybePwFetch();
        // SPAs mount login forms late — watch until the first pw field shows
        // up so the cred request still goes out (observer stops after that).
        var pwMo = new MutationObserver(function (ms) {
            if (pwAsked) { pwMo.disconnect(); return; }
            for (var i = 0; i < ms.length; i++)
                for (var n = 0; n < ms[i].addedNodes.length; n++) {
                    var nd = ms[i].addedNodes[n];
                    if (nd.nodeType === 1 && ((nd.matches && nd.matches(PW_SEL)) ||
                        (nd.querySelector && nd.querySelector(PW_SEL)))) {
                        maybePwFetch();
                        return;
                    }
                }
        });
        try { pwMo.observe(document.documentElement, { childList: true, subtree: true }); } catch (e) { }

        document.addEventListener('focusin', function (e) {
            var sh = sh0(); if (!sh) return;
            var t = e.target;
            if (!t || !t.matches) { hidePwDrop(sh); return; }
            var isPw = t.matches(PW_SEL);
            if (isPw) maybePwFetch();
            if ((isPw || t.matches(PW_USER_SEL)) && pwCreds && pwCreds.length && pwPartnerFor(t))
                showPwDrop(sh, t);
            else hidePwDrop(sh);
        }, true);
        document.addEventListener('focusout', function () {
            setTimeout(function () {
                var ae = document.activeElement;
                if (ae && ae.matches && (ae.matches(PW_SEL) || ae.matches(PW_USER_SEL))) return;
                var sh = sh0(); if (sh) hidePwDrop(sh);
            }, 130);
        }, true);
        document.addEventListener('scroll', function () {
            var sh = sh0(); if (sh && pwDropFor) positionPwDrop(sh);
        }, true);
        window.addEventListener('resize', function () {
            var sh = sh0(); if (sh && pwDropFor) positionPwDrop(sh);
        });
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') { var sh = sh0(); if (sh) hidePwDrop(sh); }
        }, true);
        document.addEventListener('mousedown', function (e) {
            var t = e.target;
            if (t && (t.id === HOST_ID || t === pwDropFor)) return;
            var sh = sh0(); if (sh) hidePwDrop(sh);
        }, true);

        // Form submits → save candidate (host decides whether to prompt).
        document.addEventListener('submit', function (e) {
            var f = e.target;
            if (!f || !f.querySelectorAll) return;
            var pw = [].slice.call(f.querySelectorAll(PW_SEL))
                .filter(function (x) { return pwVisible(x) && x.value; })[0];
            if (!pw) return;
            var uf = pwUserFor(pw);
            pwCandSent = true;
            send('pwcand:' + JSON.stringify({ o: location.origin, u: uf ? uf.value : '', p: pw.value }));
        }, true);
        // XHR/SPA logins submit nothing — catch a filled field on the way out.
        window.addEventListener('pagehide', function () {
            if (pwCandSent) return;
            var pw = pwFields().filter(function (x) { return x.value; })[0];
            if (!pw) return;
            var uf = pwUserFor(pw);
            try { send('pwcand:' + JSON.stringify({ o: location.origin, u: uf ? uf.value : '', p: pw.value })); } catch (e) { }
        });
    }

    function inspectEl(sh, el) {
        if (!sh || !el || el.nodeType !== 1 || el.id === HOST_ID) return;
        if (!dtOpen) toggleDt(sh, 'ins');
        else { dtTab = 'ins'; syncDtTabs(sh); send('dtstate:ins'); }
        ensureIns(sh);
        insSelect(sh, el);
    }

    function wirePageMenu() {
        document.addEventListener('contextmenu', function (e) {
            var sh = sh0(); if (!sh) return;
            var t = e.target;
            // events from inside our shadow chrome retarget to the host node —
            // the tab strip/context menus handle those themselves
            if (t && t.id === HOST_ID) return;
            // password fields get our compact menu (generated password +
            // editing) instead of the native editing menu
            var pw = t && t.closest ? t.closest(PW_SEL) : null;
            if (pw) {
                e.preventDefault(); e.stopPropagation();
                showPwMenu(sh, e.clientX, e.clientY, pw);
                return;
            }
            // editable fields keep the native editing menu
            if (t && t.closest && t.closest('input,textarea,[contenteditable="true"],[contenteditable=""]')) return;
            e.preventDefault(); e.stopPropagation();
            var a = t && t.closest ? t.closest('a[href]') : null;
            var href = null;
            if (a) { href = a.getAttribute('href'); try { href = new URL(href, location.href).href; } catch (e2) { href = null; } }
            var sel = '';
            try { sel = (getSelection() || '').toString().trim(); } catch (e3) { }
            showPageMenu(sh, e.clientX, e.clientY, href, !!sel, t);
        }, true);
    }

    // ---------------- link-hover status bubble ----------------

    function wireStatusBubble() {
        var lastLink = null;
        document.addEventListener('mouseover', function (e) {
            if (!statusEl) return;
            var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
            if (a === lastLink) return;
            lastLink = a;
            if (!a) { statusEl.classList.remove('on'); return; }
            var href = a.getAttribute('href');
            try { href = new URL(href, location.href).href; } catch (err) { }
            if (!href || href.length > 300) { statusEl.classList.remove('on'); return; }
            statusEl.textContent = href;
            statusEl.classList.add('on');
        }, true);
        document.addEventListener('scroll', function () {
            if (statusEl) statusEl.classList.remove('on');
            lastLink = null;
        }, true);
        // Mouse leaving the window hides the bubble.
        document.addEventListener('mouseleave', function () {
            if (statusEl) statusEl.classList.remove('on');
        }, true);
    }

    // ------------- open-in-new-tab gestures (middle / ctrl-click) -------------

    function linkTarget(e) {
        var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
        if (!a) return null;
        var href = a.getAttribute('href');
        try { href = new URL(href, location.href).href; } catch (err) { return null; }
        return /^https?:|^about:/.test(href) ? href : null;
    }

    function wireLinkGestures() {
        document.addEventListener('auxclick', function (e) {
            if (e.button !== 1) return;
            var href = linkTarget(e);
            if (!href) return;
            e.preventDefault();
            send('newtab:' + href);
        }, true);
        // Ctrl/Cmd+click — the classic open-in-new-tab gesture.
        document.addEventListener('click', function (e) {
            if (e.button !== 0 || !(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey) return;
            var href = linkTarget(e);
            if (!href) return;
            e.preventDefault(); e.stopPropagation();
            send('newtab:' + href);
        }, true);
    }

    // ---------------- find in page (Ctrl+F) ----------------
    // window.find() does the matching/scroll/highlight natively; we add the
    // UI + match count. Lives client-side — no host round-trip per keystroke.

    function openFind(sh) {
        findOpen = true;
        var b = sh.getElementById('fbar');
        b.classList.add('on');
        var inp = sh.getElementById('fin');
        try {
            var sel = (getSelection().toString() || '').trim().slice(0, 80);
            if (sel) inp.value = sel;
        } catch (e) { }
        inp.focus(); inp.select();
        if (inp.value) findStep(sh, false);
    }

    function closeFind(sh) {
        findOpen = false;
        var b = sh.getElementById('fbar');
        if (b) b.classList.remove('on');
        try { getSelection().removeAllRanges(); } catch (e) { }
    }

    function updateFindCount(sh) {
        var q = sh.getElementById('fin').value;
        var cnt = sh.getElementById('fcnt');
        if (!q) { cnt.textContent = ''; cnt.classList.remove('none'); return; }
        var n = 0;
        try {
            var low = ((document.body && document.body.innerText) || '').toLowerCase();
            var qq = q.toLowerCase(), p = 0;
            while ((p = low.indexOf(qq, p)) >= 0 && n < 999) { n++; p += qq.length; }
        } catch (e) { }
        cnt.textContent = n === 0 ? 'No results' : n + (n >= 999 ? '+' : '');
        cnt.classList.toggle('none', n === 0);
    }

    function findStep(sh, back) {
        var q = sh.getElementById('fin').value;
        if (!q) { updateFindCount(sh); return; }
        try {
            var s = getSelection();
            if (s && s.rangeCount) { back ? s.collapseToStart() : s.collapseToEnd(); }
            window.find(q, false, !!back, true);
        } catch (e) { }
        updateFindCount(sh);
    }

    // ---------------- ⋮ browser menu ----------------

    function menuItems() {
        var items = [
            ['newtab', 'New tab', 'Ctrl+T'],
            ['newpriv', 'New private tab', 'Ctrl+Shift+N'],
            ['reopen', 'Reopen closed tab', 'Ctrl+Shift+T'],
            ['history', 'History', 'Ctrl+H'],
            ['pw', 'Passwords', ''],
            ['import', 'Import browser data', ''],
            ['se', 'Search engine', seName() + ' \u203a'],
            null,
            ['palette', 'Command palette', IS_MAC ? '\u2318K' : 'Ctrl+K'],
            ['find', 'Find in page', 'Ctrl+F'],
            ['bm', (state.bm ? 'Remove bookmark' : 'Bookmark this page'), 'Ctrl+D'],
            ['copyurl', 'Copy link address', 'Ctrl+Shift+C'],
            ['pip', pipActive() ? 'Exit picture-in-picture' : 'Picture-in-picture', 'Ctrl+Shift+P'],
            ['mute', docTabMuted() ? 'Unmute tab' : 'Mute tab', ''],
            null,
            ['zoomout', 'Zoom out', 'Ctrl+−'],
            ['zoomreset', 'Reset zoom — ' + state.zoom + '%', 'Ctrl+0'],
            ['zoomin', 'Zoom in', 'Ctrl++'],
            null,
            ['dlp', 'Downloads', 'Ctrl+J'],
            ['print', 'Print', 'Ctrl+P'],
            ['viewsource', 'View source', 'Ctrl+U'],
            ['devtools', 'Developer tools', 'F12'],
        ];
        if (state.sess > 0) {
            items.push(null);
            items.push(['restore', 'Restore last session (' + state.sess + ' tab' + (state.sess > 1 ? 's' : '') + ')', '']);
        }
        items.push(null);
        items.push(['fullscreen', 'Fullscreen', 'F11']);
        items.push(['topmost', 'Always on top', state.topmost ? '✓' : '']);
        items.push(null);
        items.push(['help', 'How to use', 'F1']);
        return items;
    }

    function showMenu(sh) {
        var m = sh.getElementById('menu');
        if (m.classList.contains('on')) { m.classList.remove('on'); return; }
        var html = '';
        var mi = 0;
        menuItems().forEach(function (it) {
            html += it === null ? '<div class="msep"></div>' :
                '<div class="mi" style="--i:' + (mi++) + '" data-c="' + it[0] + '"><span>' + it[1] + '</span><span class="sc">' + it[2] + '</span></div>';
        });
        m.innerHTML = html;
        m.classList.add('on');
        m.querySelectorAll('.mi').forEach(function (el) {
            el.addEventListener('mousedown', function (e) {
                e.preventDefault(); e.stopPropagation();
                if (el.getAttribute('data-c') === 'se') { showSeFly(sh, m, el); return; }
                m.classList.remove('on');
                menuCmd(sh, el.getAttribute('data-c'));
            });
        });
        var off = function () {
            m.classList.remove('on');
            var sm = sh.getElementById('smenu'); if (sm) sm.classList.remove('on');
            document.removeEventListener('mousedown', off, true);
        };
        setTimeout(function () { document.addEventListener('mousedown', off, true); }, 0);
    }

    // "Search engine" flyout — a second panel slid left of the ⋮ menu, one
    // row per provider, check mark on the active one.
    function showSeFly(sh, m, row) {
        var sm = sh.getElementById('smenu');
        if (!sm || !state.sel.length) return;
        m.classList.add('on'); // the capture-phase outside-closer already hid it — restore
        var mr = m.getBoundingClientRect(), rr = row.getBoundingClientRect();
        var html = '';
        state.sel.forEach(function (e, i) {
            var on = e.i === state.se;
            html += '<div class="smi' + (on ? ' cur' : '') + '" style="--i:' + i + '" data-e="' + esc(e.i) + '">' +
                '<span class="sk">' + (on ? SVG.chk : '') + '</span><span>' + esc(e.n) + '</span></div>';
        });
        sm.innerHTML = html;
        sm.style.top = Math.max(40, rr.top - 5) + 'px';
        sm.style.right = (innerWidth - mr.left + 4) + 'px';
        sm.classList.add('on');
        sm.querySelectorAll('.smi').forEach(function (el) {
            el.addEventListener('mousedown', function (e) {
                e.preventDefault(); e.stopPropagation();
                send('se:' + el.getAttribute('data-e'));
                sm.classList.remove('on'); m.classList.remove('on');
            });
        });
        var off = function (e) {
            var t = e.target;
            if (t.closest && (t.closest('#smenu') || t.closest('#menu'))) return;
            sm.classList.remove('on'); m.classList.remove('on');
            document.removeEventListener('mousedown', off, true);
        };
        setTimeout(function () { document.addEventListener('mousedown', off, true); }, 0);
    }

    function menuCmd(sh, c) {
        // On the start-page overlay, page-scoped actions would hit the hidden document.
        if (state.ov && (c === 'find' || c === 'print' || c === 'viewsource' || c === 'copyurl' || c === 'bm')) return;
        if (c === 'palette') { openPal(sh); return; }
        if (c === 'find') { openFind(sh); return; }
        if (c === 'pip') { togglePip(); return; }
        if (c === 'mute') { var at = activeTab(); if (at) send('mute:' + at.id); return; }
        if (c === 'history') { send('navigate:about:history'); return; }
        if (c === 'pw') { send('navigate:about:passwords'); return; }
        if (c === 'import') { send('navigate:about:import'); return; }
        if (c === 'devtools') { toggleDt(sh); return; }
        if (c === 'dlp') { toggleDl(sh); return; }
        if (c === 'print') { try { window.print(); } catch (e) { } return; }
        if (c === 'viewsource') { send('newtab:view-source:' + location.href); return; }
        if (c === 'help') { send('newtab:about:help'); return; }
        if (c === 'newpriv') { openNewTab(true); return; }
        if (c === 'newtab') { openNewTab(false); return; }
        if (c === 'copyurl') { copyText(location.href); return; }
        if (c === 'restore') { send('restoresession'); return; }
        send(c);
    }

    // ---------------- command palette (Ctrl/Cmd+K) ----------------
    // One keyboard-first box for everything: jump to an open tab, reopen
    // something from history/bookmarks, run a browser action, or just type a
    // URL / search. Built lazily on open — zero cost until used.

    var palOpen = false, palSel = 0, palRows = [];
    var IS_MAC = /Mac/.test(navigator.platform);

    function palActions() {
        var a = activeTab();
        return [
            ['newtab', 'New tab', 'tab open blank', IS_MAC ? '\u2318T' : 'Ctrl+T'],
            ['newpriv', 'New private tab', 'incognito private tab', IS_MAC ? '\u21e7\u2318N' : 'Ctrl+Shift+N'],
            ['reopen', 'Reopen closed tab', 'undo close restore', IS_MAC ? '\u21e7\u2318T' : 'Ctrl+Shift+T'],
            ['history', 'Open history', 'browsing history visited', IS_MAC ? '\u2318H' : 'Ctrl+H'],
            ['import', 'Import browser data', 'bookmarks passwords chrome safari edge firefox', ''],
            ['pw', 'Passwords', 'saved logins credentials vault autofill signin', ''],
            ['find', 'Find in page', 'search text', IS_MAC ? '\u2318F' : 'Ctrl+F'],
            ['bm', state.bm ? 'Remove bookmark' : 'Bookmark this page', 'star favorite save', IS_MAC ? '\u2318D' : 'Ctrl+D'],
            ['copyurl', 'Copy page link', 'url address clipboard', ''],
            ['pip', pipActive() ? 'Exit picture-in-picture' : 'Picture-in-picture', 'pip video float', ''],
            ['mute', a && a.mute ? 'Unmute tab' : 'Mute tab', 'sound audio volume silence', ''],
            ['zoomin', 'Zoom in', 'bigger larger magnify', ''],
            ['zoomout', 'Zoom out', 'smaller', ''],
            ['zoomreset', 'Reset zoom', '100', ''],
            ['print', 'Print', 'pdf paper', ''],
            ['viewsource', 'View source', 'html code', ''],
            ['devtools', 'Developer tools', 'console network inspect cookies', 'F12'],
            ['dlp', 'Downloads', 'downloaded files save shelf', IS_MAC ? '⇧⌘J' : 'Ctrl+J'],
            ['fullscreen', 'Toggle fullscreen', 'full screen', 'F11'],
            ['topmost', 'Toggle always on top', 'pin window float', ''],
            ['help', 'How to use BrAIn', 'help guide mcp docs', 'F1'],
        ].concat(state.sel.map(function (e) {
            return ['se:' + e.i, 'Search engine: ' + e.n,
                'default search provider engine ' + e.n.toLowerCase(), e.i === state.se ? '✓' : ''];
        }));
    }

    // 100 prefix · 80 word start · 60 inside · 30 letters in order · 0 miss
    // Letters-in-order matching only for short titles — on URLs and keyword
    // lists it matches nearly everything.
    function fuzzy(hay, q, loose) {
        hay = hay.toLowerCase();
        var i = hay.indexOf(q);
        if (i === 0) return 100;
        if (i > 0) return /[\s\/.\-_]/.test(hay.charAt(i - 1)) ? 80 : 60;
        if (!loose || hay.length > 32) return 0;
        var p = 0;
        for (var k = 0; k < hay.length && p < q.length; k++) if (hay.charAt(k) === q.charAt(p)) p++;
        return p === q.length && q.length > 1 ? 30 : 0;
    }

    function looksLikeUrl(v) {
        return /^(https?:\/\/|about:|file:)/i.test(v) || /^(localhost|[\w-]+(\.[\w-]+)+)(:\d+)?(\/|$)/i.test(v);
    }

    function palCollect(q) {
        q = q.trim();
        var ql = q.toLowerCase(), out = [], i, it, sc;
        var web = !state.ov;
        function add(kind, t, sub, icon, id, score, run) {
            out.push({ kind: kind, t: t, sub: sub, icon: icon, id: id, score: score, run: run });
        }
        if (q) {
            if (looksLikeUrl(q)) add('Go', 'Go to ' + q, 'Open address', null, 'go', 1000, function () { send('navigate:' + q); });
            else add('Search', 'Search the web for \u201c' + q + '\u201d', seName(), null, 'go', 1000, function () { send('navigate:' + q); });
        }
        state.tabs.forEach(function (t) {
            var title = t.title || hostOf(t.url);
            sc = q ? Math.max(fuzzy(title, ql, 1), fuzzy(t.url || '', ql) * .85) : 90 - (t.active ? 1 : 0);
            if (sc > 0) add(t.active ? 'Current tab' : 'Tab', title, prettyHost(t.url), t.icon || '', t.id, sc + 20, function () { switchTabLocal(t.id); });
        });
        palActions().forEach(function (a) {
            sc = q ? Math.max(fuzzy(a[1], ql, 1), fuzzy(a[2], ql) * .8) : 0;
            if (!q && (a[0] === 'newtab' || a[0] === 'newpriv' || a[0] === 'history' || a[0] === 'find' || a[0] === 'devtools')) sc = 40;
            if (sc > 0) add('Action', a[1], a[3], null, a[0], sc + 8, function () { var sh = sh0(); if (sh) menuCmd(sh, a[0]); });
        });
        (state.bmarks || []).forEach(function (b) {
            sc = q ? Math.max(fuzzy(b.t || '', ql, 1), fuzzy(b.u || '', ql) * .85) : 0;
            if (sc > 0) add('Bookmark', b.t || hostOf(b.u), prettyHost(b.u), b.i || '', 'b' + b.u, sc + 10, function () { send('navigate:' + b.u); });
        });
        var seen = {};
        state.tabs.forEach(function (t) { seen[t.url] = 1; });
        (state.bmarks || []).forEach(function (b) { seen[b.u] = 1; });
        (state.hist || []).forEach(function (h, idx) {
            if (!/^https?:/.test(h.u)) return;
            sc = q ? Math.max(fuzzy(h.t || '', ql, 1), fuzzy(h.u || '', ql) * .85) : (idx < 4 ? 20 - idx : 0);
            if (sc > 0 && !seen[h.u]) { seen[h.u] = 1; add('History', h.t || hostOf(h.u), prettyHost(h.u), h.i || '', 'h' + h.u, sc, function () { send('navigate:' + h.u); }); }
        });
        out.sort(function (a, b) { return b.score - a.score; });
        return out.slice(0, 9);
    }

    function prettyHost(u) { try { var x = new URL(u); return x.hostname.replace(/^www\./, '') + (x.pathname.length > 1 ? x.pathname : ''); } catch (e) { return u || ''; } }

    function palFace(r) {
        if (r.kind === 'Go' || r.kind === 'Search') return '<span class="pi go">' + SVG.mag + '</span>';
        if (r.kind === 'Action') return '<span class="pi act">' + SVG.sparkle + '</span>';
        var letter = ((r.t || '?').trim().charAt(0) || '?').toUpperCase();
        return '<span class="pi" style="background:' + tabHue(r.id) + '">' +
            (r.icon ? '<img src="' + esc(r.icon) + '" alt="" data-l="' + esc(letter) + '" onerror="this.parentNode.textContent=this.dataset.l">' : esc(letter)) + '</span>';
    }

    function palRender(sh) {
        var box = sh.getElementById('pres');
        var q = sh.getElementById('pq').value;
        palRows = palCollect(q);
        if (palSel >= palRows.length) palSel = Math.max(0, palRows.length - 1);
        var html = '';
        for (var i = 0; i < palRows.length; i++) {
            var r = palRows[i];
            html += '<div class="pr' + (i === palSel ? ' sel' : '') + '" style="--i:' + i + '" data-i="' + i + '">' + palFace(r) +
                '<span class="pt">' + esc(r.t) + '</span>' +
                (r.sub ? '<span class="ps">' + esc(r.sub) + '</span>' : '') +
                '<span class="pk">' + esc(r.kind) + '</span></div>';
        }
        box.innerHTML = html || '<div class="pnone">Nothing found</div>';
    }

    function palMove(sh, d) {
        if (!palRows.length) return;
        palSel = (palSel + d + palRows.length) % palRows.length;
        var rows = sh.getElementById('pres').children;
        for (var i = 0; i < rows.length; i++) rows[i].classList.toggle('sel', i === palSel);
        var el = rows[palSel];
        if (el && el.scrollIntoView) el.scrollIntoView({ block: 'nearest' });
    }

    function palRun(sh, i) {
        var r = palRows[i];
        if (!r) return;
        closePal(sh);
        setTimeout(function () { try { r.run(); } catch (e) { } }, 0);
    }

    function openPal(sh) {
        var p = sh.getElementById('pal');
        if (!p) return;
        if (palOpen) { closePal(sh); return; }
        palOpen = true; palSel = 0;
        var m = sh.getElementById('menu'); if (m) m.classList.remove('on');
        p.classList.add('on');
        var q = sh.getElementById('pq');
        q.value = '';
        var rs = sh.getElementById('pres');
        rs.classList.add('fresh'); // rows cascade in once per open, not per keystroke
        setTimeout(function () { rs.classList.remove('fresh'); }, 520);
        palRender(sh);
        q.focus();
    }

    function closePal(sh) {
        palOpen = false;
        var p = sh && sh.getElementById('pal');
        if (p) p.classList.remove('on');
        var q = sh && sh.getElementById('pq');
        if (q) q.blur();
    }

    function wirePalette(sh) {
        var p = sh.getElementById('pal'), q = sh.getElementById('pq'), res = sh.getElementById('pres');
        ['mousedown', 'click', 'contextmenu'].forEach(function (ev) {
            p.addEventListener(ev, function (e) { e.stopPropagation(); });
        });
        p.addEventListener('mousedown', function (e) {
            if (e.target === p) closePal(sh);
        });
        q.addEventListener('input', function () { palSel = 0; palRender(sh); });
        q.addEventListener('keydown', function (e) {
            e.stopPropagation();
            if (e.key === 'ArrowDown' || (e.ctrlKey && (e.key === 'n' || e.key === 'j'))) { e.preventDefault(); palMove(sh, 1); }
            else if (e.key === 'ArrowUp' || (e.ctrlKey && (e.key === 'p' || e.key === 'k'))) { e.preventDefault(); palMove(sh, -1); }
            else if (e.key === 'Enter') { e.preventDefault(); palRun(sh, palSel); }
            else if (e.key === 'Escape') { e.preventDefault(); closePal(sh); }
            else if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K') && !e.shiftKey) { e.preventDefault(); closePal(sh); }
        });
        ['keyup', 'keypress'].forEach(function (ev) { q.addEventListener(ev, function (e) { e.stopPropagation(); }); });
        res.addEventListener('mousemove', function (e) {
            var row = e.target.closest && e.target.closest('.pr');
            if (!row) return;
            var i = +row.getAttribute('data-i');
            if (i !== palSel) { palSel = i; var rows = res.children; for (var k = 0; k < rows.length; k++) rows[k].classList.toggle('sel', k === i); }
        });
        res.addEventListener('mousedown', function (e) {
            e.preventDefault();
            var row = e.target.closest && e.target.closest('.pr');
            if (row) palRun(sh, +row.getAttribute('data-i'));
        });
        var kb = sh.getElementById('orb');
        if (kb) ['mousedown', 'click'].forEach(function (ev) {
            kb.addEventListener(ev, function (e) { e.stopPropagation(); e.preventDefault(); if (ev === 'mousedown') openPal(sh); });
        });
    }

    function copyText(text) {
        try { navigator.clipboard.writeText(text); }
        catch (e) {
            try {
                var ta = document.createElement('textarea');
                ta.value = text; ta.style.cssText = 'position:fixed;opacity:0';
                document.body.appendChild(ta); ta.select();
                document.execCommand('copy'); ta.remove();
            } catch (e2) { }
        }
    }

    // ---------------- scroll position reporting ----------------
    // Logical tabs reload on switch — the host restores our saved position
    // after load, like a real browser preserves scroll on reload.

    function wireScrollReport() {
        var lastFixedCheck = 0;
        window.addEventListener('scroll', function (e) {
            if (e.target !== document) return; // window scroll only, not nested scrollers
            var n = Date.now();
            // Sticky headers pin to the viewport while scrolling — re-scan the
            // covered strip so newly-pinned ones shift under the bar too.
            if (n - lastFixedCheck > 350) { lastFixedCheck = n; adjustFixed(); }
            if (n - lastScrollReport < 300) return;
            lastScrollReport = n;
            send('scroll:' + location.href + '|' + Math.round(window.scrollX || 0) + ',' + Math.round(window.scrollY || 0));
        }, { passive: true, capture: true });
        window.addEventListener('resize', adjustFixed, { passive: true });
    }

    // ---------------- tool capsule: highlight + tooltips ----------------

    function fmtSc(k) {
        if (!k || !IS_MAC) return k || '';
        var shift = /Shift\+/.test(k);
        return (shift ? '\u21e7' : '') + '\u2318' + k.replace(/Ctrl\+|Shift\+/g, '');
    }
    function setTip(el, text, sc) {
        el.setAttribute('data-tip', text);
        if (sc) el.setAttribute('data-sc', sc); else el.removeAttribute('data-sc');
        el.removeAttribute('title'); // ours replaces the slow native tooltip
    }

    function wireTools(sh) {
        var box = sh.getElementById('tools'), hl = sh.getElementById('hl'), tip = sh.getElementById('ttip');
        var timer = 0, shown = false;
        function hideTip() { clearTimeout(timer); shown = false; tip.classList.remove('on'); }
        function showTip(b) {
            var t = b.getAttribute('data-tip'); if (!t) { hideTip(); return; }
            var sc = fmtSc(b.getAttribute('data-sc'));
            tip.innerHTML = '<span>' + esc(t) + '</span>' + (sc ? '<kbd>' + esc(sc) + '</kbd>' : '');
            var r = b.getBoundingClientRect(), w = tip.offsetWidth;
            tip.style.left = Math.max(8, Math.min(window.innerWidth - w - 8, r.left + r.width / 2 - w / 2)) + 'px';
            tip.classList.add('on'); shown = true;
        }
        box.addEventListener('mouseover', function (e) {
            var b = e.target.closest && e.target.closest('.tb');
            if (!b) return;
            if (!box.classList.contains('hov')) {
                // first entry: land on the button instead of sliding in from the left
                hl.style.transition = 'opacity .16s';
                hl.style.width = b.offsetWidth + 'px';
                hl.style.transform = 'translateX(' + b.offsetLeft + 'px)';
                void hl.offsetWidth;
                hl.style.transition = '';
                box.classList.add('hov');
            } else {
                hl.style.width = b.offsetWidth + 'px';
                hl.style.transform = 'translateX(' + b.offsetLeft + 'px)';
            }
            clearTimeout(timer);
            if (shown) showTip(b); else timer = setTimeout(function () { showTip(b); }, 380);
        });
        box.addEventListener('mouseleave', function () { box.classList.remove('hov'); hideTip(); });
        box.addEventListener('mousedown', hideTip, true); // capture: the orb stops propagation
    }

    // ---------------- bookmark star ----------------

    function updateStar(sh) {
        var s = sh.getElementById('star');
        if (!s) return;
        var http = /^https?:/.test(location.href) && !state.privActive && !state.ov;
        s.style.display = http ? 'grid' : 'none';
        // rebuild only on change — the pop animation must not replay on every push
        if (s._bm !== !!state.bm) {
            s._bm = !!state.bm;
            s.innerHTML = state.bm ? SVG.starF : SVG.star;
            s.classList.toggle('yes', !!state.bm);
        }
        setTip(s, state.bm ? 'Remove bookmark' : 'Bookmark this page', 'Ctrl+D');
        updatePip();
    }

    // ---------------- picture-in-picture ----------------
    // Works for YouTube and any HTML5 <video>. Uses the standard API
    // (WebView2/Chromium) and falls back to WebKit's presentation mode
    // (WKWebView). Must run inside a user gesture (button click / shortcut).

    // querySelectorAll stops at shadow boundaries; many custom players
    // (web-component based) keep their <video> inside an open shadow root.
    function allMedia(root, out, depth, sel) {
        out = out || []; depth = depth || 0;
        if (depth > 6) return out;
        var vs = root.querySelectorAll(sel);
        for (var i = 0; i < vs.length; i++) out.push(vs[i]);
        var all = root.querySelectorAll('*');
        for (var j = 0; j < all.length; j++)
            if (all[j].shadowRoot && all[j].id !== HOST_ID) allMedia(all[j].shadowRoot, out, depth + 1, sel);
        return out;
    }

    function pipVideo() {
        var vs = allMedia(document, null, 0, 'video'), best = null, bs = -1;
        for (var i = 0; i < vs.length; i++) {
            var v = vs[i];
            if (v.readyState < 1 && !v.currentSrc && !v.src) continue;
            var r = v.getBoundingClientRect();
            var score = r.width * r.height + (!v.paused && !v.ended ? 1e7 : 0) +
                (isPip(v) ? 1e9 : 0);
            if (score > bs) { bs = score; best = v; }
        }
        return best;
    }

    function isPip(v) {
        return document.pictureInPictureElement === v || v.webkitPresentationMode === 'picture-in-picture';
    }

    function togglePip() {
        var v = pipVideo();
        if (!v) return;
        try {
            v.removeAttribute('disablePictureInPicture'); // YouTube & co. set this
            v.disablePictureInPicture = false;
            if (isPip(v)) {
                if (document.pictureInPictureElement && document.exitPictureInPicture) document.exitPictureInPicture().catch(function () { });
                else if (v.webkitSetPresentationMode) v.webkitSetPresentationMode('inline');
            } else if (v.requestPictureInPicture) {
                v.requestPictureInPicture().catch(function () {
                    if (v.webkitSetPresentationMode) try { v.webkitSetPresentationMode('picture-in-picture'); } catch (e) { }
                });
            } else if (v.webkitSetPresentationMode) {
                v.webkitSetPresentationMode('picture-in-picture');
            }
        } catch (e) { }
        setTimeout(updatePip, 300);
    }

    function pipActive() {
        var v = state.ov ? null : pipVideo();
        return !!(v && isPip(v));
    }

    function docTabMuted() {
        var a = activeTab();
        return !!(a && a.mute);
    }

    // The button is always present on web pages so it can be found; it's
    // dimmed (and explains itself) when the page has no video to float.
    function updatePip() {
        var sh = sh0(); if (!sh) return;
        var p = sh.getElementById('pip'); if (!p) return;
        var web = !state.ov && /^(https?|file|blob):/.test(location.href);
        var v = web ? pipVideo() : null;
        p.style.display = web ? 'grid' : 'none';
        p.classList.toggle('na', web && !v);
        if (v) {
            var on = isPip(v);
            p.classList.toggle('yes', on);
            setTip(p, on ? 'Exit picture-in-picture' : 'Picture-in-picture', 'Ctrl+Shift+P');
        } else {
            p.classList.remove('yes');
            setTip(p, 'No video on this page', '');
        }
    }

    function wirePip() {
        // Media events don't bubble, so capture them at the document. SPA video
        // swaps (YouTube) fire loadeddata/play, so the common case is instant;
        // the slow poll only catches videos inside shadow roots (their media
        // events never cross the boundary) and late-mounted players.
        ['loadeddata', 'play', 'emptied', 'enterpictureinpicture', 'leavepictureinpicture',
            'webkitpresentationmodechanged'].forEach(function (ev) {
            document.addEventListener(ev, function (e) {
                if (e.target && e.target.tagName === 'VIDEO') updatePip();
            }, true);
        });
        setTimeout(updatePip, 800);
        setInterval(function () { if (!document.hidden) updatePip(); }, 2500);
    }

    // ---------------- downloads ----------------
    // The host pushes the full snapshot as {b:20,dls:[{i,n,u,r,t,s,e}]}
    // (s: 0 running · 1 done · 2 failed · 3 canceled). The capsule button
    // draws a progress ring over the icon while anything is in flight and
    // pulses once when a download lands; the popover is the shelf.

    var dlList = [], dlOpen = false, dlFlash = { t: 0 };
    var DL_CIRC = 50.265; // 2π × r=8 ring

    function dlActive() {
        var n = 0, p = 0, k = 0, und = false;
        for (var i = 0; i < dlList.length; i++) {
            var d = dlList[i];
            if (d.s !== 0) continue;
            n++;
            if (d.t > 0) { p += Math.min(1, d.r / d.t); k++; }
            else und = true;
        }
        return { n: n, frac: k ? p / k : -1, und: und && !k };
    }

    function renderDlBtn(sh) {
        var b = sh.getElementById('dlbtn'); if (!b) return;
        var a = dlActive();
        var web = !state.ov && /^(https?|file|blob):/.test(location.href);
        b.style.display = web || dlList.length || dlOpen ? 'grid' : 'none';
        var rg = b.querySelector('.drg'), dot = b.querySelector('.ddot');
        if (a.n) {
            b.classList.add('run');
            rg.style.display = 'block';
            var c = rg.firstChild;
            if (a.und) b.classList.add('und');
            else {
                b.classList.remove('und');
                c.style.strokeDasharray = DL_CIRC;
                c.style.strokeDashoffset = DL_CIRC * (1 - Math.max(.05, a.frac));
            }
        } else {
            b.classList.remove('run', 'und');
            rg.style.display = 'none';
        }
        dot.style.display = !a.n && dlList.length ? 'block' : 'none';
    }

    function dlSub(d) {
        if (d.s === 1) return fmtBytes(d.t > 0 ? d.t : d.r) || 'Saved';
        if (d.s === 2) return 'Failed' + (d.e ? ' — ' + d.e : '');
        if (d.s === 3) return 'Canceled';
        if (d.t > 0)
            return Math.min(100, Math.floor(d.r * 100 / d.t)) + '% · ' +
                fmtBytes(d.r) + ' of ' + fmtBytes(d.t);
        return d.r > 0 ? fmtBytes(d.r) + '…' : 'Starting…';
    }

    // Row DOM persists across progress pushes — rebuilding innerHTML every
    // ~300ms replayed the rowin entrance on every row (=the flicker) and kept
    // the bar's width transition from ever running. Diff in place instead:
    // sync text/bar on existing rows, create only genuinely new ones.
    function dlRowSync(el, d, full) {
        var st = d.s === 0 ? 'run' : d.s === 1 ? 'ok' : d.s === 2 ? 'err' : 'off';
        if (full || el._st !== st) {
            el.className = 'dli ' + st + (el._fresh ? ' new' : '');
            el.innerHTML = '<span class="dic">' + SVG.doc + '</span>' +
                '<span class="db"><b title="' + esc(d.n) + '">' + esc(d.n) + '</b><i></i>' +
                (d.s === 0 ? '<u><e></e></u>' : '') + '</span>' +
                (d.s === 0
                    ? '<span class="dab" data-x="1" title="Cancel">' + SVG.close + '</span>'
                    : '<span class="dab" data-r="1" title="Show in folder">' + SVG.folder + '</span>');
            el._st = st; el._n = d.n;
        }
        if (el._n !== d.n) {
            el._n = d.n;
            var b = el.querySelector('.db b');
            b.textContent = d.n; b.title = d.n;
        }
        el.querySelector('.db i').textContent = dlSub(d);
        var e = el.querySelector('.db u e');
        if (e && d.s === 0) {
            var pc = d.t > 0 ? Math.min(100, d.r * 100 / d.t) : -1;
            if (pc < 0) { e.className = 'ind'; e.style.width = ''; }
            else { e.className = ''; e.style.width = pc.toFixed(1) + '%'; }
        }
    }

    function dlRowEl(d) {
        var el = document.createElement('div');
        el.setAttribute('data-i', d.i);
        el._fresh = true;
        dlRowSync(el, d, true);
        el.addEventListener('animationend', function h(e) {
            if (e.animationName === 'rowin') { el._fresh = false; el.classList.remove('new'); el.removeEventListener('animationend', h); }
        });
        el.addEventListener('mousedown', function (e) {
            e.preventDefault(); e.stopPropagation();
            var act = e.target.closest('.dab');
            var id = el.getAttribute('data-i');
            if (act) { send(act.hasAttribute('data-x') ? 'dlcancel:' + id : 'dlreveal:' + id); return; }
            if (el._st === 'ok') send('dlopen:' + id);
        });
        return el;
    }

    // Collapse-out: shrink the row to nothing so the popover's auto height
    // follows smoothly, then detach.
    function dlRemoveRow(el) {
        if (!el.parentNode) return;
        el.classList.add('out');
        el.style.height = el.offsetHeight + 'px';
        void el.offsetHeight;
        el.style.height = '0px';
        setTimeout(function () { el.remove(); }, 210);
    }

    function renderDl(sh) {
        var p = sh.getElementById('dlp'); if (!p || !dlOpen) return;
        if (!p._rows) {
            p.innerHTML = '<div class="dph"><span class="dpt">Downloads</span><span class="dtsp"></span>' +
                '<button class="dtbtn" id="dlclr">Clear</button></div>' +
                '<div class="dpempty" style="display:none"><span>' + SVG.dl + '</span>No downloads yet</div>';
            p.querySelector('#dlclr').addEventListener('mousedown', function (e) {
                e.preventDefault(); e.stopPropagation(); send('dlclear');
            });
            p._rows = {};
        }
        p.querySelector('#dlclr').disabled = !dlList.some(function (d) { return d.s !== 0; });
        var empty = p.querySelector('.dpempty');
        empty.style.display = dlList.length ? 'none' : 'flex';

        var seen = {}, ref = empty.nextSibling;
        dlList.slice(0, 30).forEach(function (d, i) {
            var el = p._rows[d.i];
            if (el) dlRowSync(el, d, false); else p._rows[d.i] = el = dlRowEl(d);
            seen[d.i] = true;
            el.style.setProperty('--i', Math.min(i, 12));
            if (el !== ref) p.insertBefore(el, ref); else ref = ref.nextSibling;
        });
        Object.keys(p._rows).forEach(function (id) {
            if (!seen[id]) { var el = p._rows[id]; delete p._rows[id]; dlRemoveRow(el); }
        });
    }

    function dlClose(sh) {
        dlOpen = false;
        var p = sh.getElementById('dlp');
        if (p && p.classList.contains('on') && !p.classList.contains('bye')) {
            p.classList.add('bye');
            setTimeout(function () {
                if (!dlOpen) { p.classList.remove('on', 'bye'); p.innerHTML = ''; p._rows = null; }
            }, 170);
        }
    }

    function toggleDl(sh, open) {
        var p = sh.getElementById('dlp'); if (!p) return;
        var want = open === undefined ? !dlOpen : open;
        if (want === dlOpen) return;
        if (want) {
            dlOpen = true;
            p.classList.remove('bye');
            p.classList.add('on');
            send('dllist'); // host pushes fresh state → renderDl via b:20
            renderDl(sh);
            var off = function (e) {
                if (e.target.closest && (e.target.closest('#dlp') || e.target.closest('#dlbtn'))) return;
                document.removeEventListener('mousedown', off, true);
                dlClose(sh);
                renderDlBtn(sh);
            };
            setTimeout(function () { document.addEventListener('mousedown', off, true); }, 0);
        } else {
            dlClose(sh);
        }
        renderDlBtn(sh);
    }

    // Called from the b:20 message branch.
    function onDlPush(sh, j) {
        // Detect a new completion while the popover is closed → one pulse.
        var was = {};
        dlList.forEach(function (d) { was[d.i] = d.s; });
        var just = j.dls.some(function (d) {
            return d.s === 1 && was[d.i] === 0;
        });
        dlList = j.dls;
        renderDlBtn(sh);
        if (dlOpen) renderDl(sh);
        else if (just && dlFlash.t < Date.now() - 1800) {
            dlFlash.t = Date.now();
            var b = sh.getElementById('dlbtn');
            if (b) { b.classList.remove('dldone'); void b.offsetWidth; b.classList.add('dldone'); }
        }
    }

    // ---------------- tab mute (in-page fallback) ----------------
    // WKWebView mutes and reports audio natively (state.nat). Elsewhere the
    // page is the only place to do it: force media elements muted while the
    // document's tab is muted — restoring only the ones we muted — and report
    // whether anything audible is playing. Doesn't reach WebAudio or iframes.

    var audLast = null;
    function audioTick() {
        if (state.nat || state.ov) return;
        var owner = null;
        for (var i = 0; i < state.tabs.length; i++) if (state.tabs[i].doc) owner = state.tabs[i];
        var muteOn = !!(owner && owner.mute), playing = false;
        try {
            var ms = allMedia(document, null, 0, 'video,audio');
            for (var k = 0; k < ms.length; k++) {
                var m = ms[k];
                if (muteOn) { if (!m.muted) { m.muted = true; m._brMuted = true; } }
                else if (m._brMuted) { m.muted = false; m._brMuted = false; }
                if (!m.paused && !m.ended && m.readyState > 2 && m.volume > 0 && (!m.muted || m._brMuted))
                    playing = true;
            }
        } catch (e) { }
        if (playing !== audLast) { audLast = playing; send('audible:' + (playing ? 1 : 0)); }
    }

    function wireAudio() {
        setInterval(function () { if (!document.hidden) audioTick(); }, 700);
    }

    // ---------------- keyboard shortcuts ----------------

    function wireShortcuts() {
        window.addEventListener('keydown', function (e) {
            var sh = sh0(); // resolve live — a remount swaps the shadow root
            if (!sh) return;
            var mod = e.ctrlKey || e.metaKey;
            var k = e.key;
            if (mod && !e.shiftKey && !e.altKey && (k === 'k' || k === 'K')) {
                e.preventDefault(); e.stopPropagation();
                openPal(sh);
            } else if (mod && (k === 'l' || k === 'L')) {
                e.preventDefault(); e.stopPropagation();
                startEditing(sh);
            } else if (mod && e.shiftKey && (k === 't' || k === 'T')) {
                e.preventDefault(); e.stopPropagation();
                send('reopen'); // Ctrl+Shift+T reopens the last closed tab
            } else if (mod && e.shiftKey && (k === 'n' || k === 'N')) {
                e.preventDefault(); e.stopPropagation();
                openNewTab(true); // Ctrl+Shift+N — incognito-style tab
            } else if (mod && e.shiftKey && (k === 'p' || k === 'P')) {
                if (pipVideo()) { e.preventDefault(); e.stopPropagation(); togglePip(); }
            } else if (mod && e.shiftKey && (k === 'c' || k === 'C')) {
                e.preventDefault(); e.stopPropagation();
                if (!state.ov) copyText(location.href); // Ctrl+Shift+C copies the page URL
            } else if (mod && e.shiftKey && (k === 'i' || k === 'I')) {
                e.preventDefault(); e.stopPropagation();
                toggleDt(sh); // Ctrl+Shift+I
            } else if (mod && (k === 'j' || k === 'J')) {
                e.preventDefault(); e.stopPropagation();
                toggleDl(sh); // Ctrl+J — downloads shelf (⌘⇧J is Chrome's)
            } else if (mod && (k === 'h' || k === 'H')) {
                e.preventDefault(); e.stopPropagation();
                send('navigate:about:history');
            } else if (mod && (k === 't' || k === 'T')) {
                e.preventDefault(); e.stopPropagation();
                openNewTab(false);
            } else if (mod && (k === 'w' || k === 'W')) {
                e.preventDefault(); e.stopPropagation();
                var a = activeTab(); if (a) closeTabLocal(a.id);
            } else if (mod && (k === 'r' || k === 'R')) {
                e.preventDefault(); e.stopPropagation();
                send('reload');
            } else if (mod && (k === 'd' || k === 'D')) {
                e.preventDefault(); e.stopPropagation();
                send('bm');
            } else if (mod && (k === 'f' || k === 'F')) {
                e.preventDefault(); e.stopPropagation();
                if (!state.ov) openFind(sh); // nothing to find on the start page
            } else if (mod && (k === 'p' || k === 'P')) {
                e.preventDefault(); e.stopPropagation();
                if (!state.ov) { try { window.print(); } catch (err) { } }
            } else if (mod && (k === 'u' || k === 'U')) {
                e.preventDefault(); e.stopPropagation();
                if (!state.ov) send('newtab:view-source:' + location.href);
            } else if (mod && (k === '=' || k === '+')) {
                e.preventDefault(); e.stopPropagation();
                send('zoomin');
            } else if (mod && k === '-') {
                e.preventDefault(); e.stopPropagation();
                send('zoomout');
            } else if (mod && k === '0') {
                e.preventDefault(); e.stopPropagation();
                send('zoomreset');
            } else if (mod && k >= '1' && k <= '9') {
                e.preventDefault(); e.stopPropagation();
                var ti = k === '9' ? state.tabs.length - 1 : (+k) - 1;
                var tt = state.tabs[ti];
                if (tt) switchTabLocal(tt.id);
            } else if (e.key === 'F5') {
                e.preventDefault(); e.stopPropagation();
                send('reload');
            } else if (e.key === 'F6') {
                e.preventDefault(); e.stopPropagation();
                startEditing(sh);
            } else if (e.key === 'F1') {
                e.preventDefault(); e.stopPropagation();
                send('newtab:about:help');
            } else if (e.key === 'F11') {
                e.preventDefault(); e.stopPropagation();
                send('fullscreen');
            } else if (e.key === 'F12') {
                e.preventDefault(); e.stopPropagation();
                toggleDt(sh);
            } else if (e.key === 'F3') {
                e.preventDefault(); e.stopPropagation();
                if (findOpen) findStep(sh, e.shiftKey); else openFind(sh);
            } else if (mod && e.key === 'Tab') {
                e.preventDefault(); e.stopPropagation();
                cycleTab(e.shiftKey ? -1 : 1);
            } else if (e.altKey && e.key === 'ArrowLeft') {
                e.preventDefault(); e.stopPropagation();
                send('back');
            } else if (e.altKey && e.key === 'ArrowRight') {
                e.preventDefault(); e.stopPropagation();
                send('forward');
            } else if (e.key === 'Escape') {
                if (palOpen) { e.preventDefault(); e.stopPropagation(); closePal(sh); }
                else if (dtPicking) setPick(sh, false);
                else if (dtOpen) toggleDt(sh);
                else if (findOpen) closeFind(sh);
                else if (!editing && state.loading) send('stop');
            }
        }, true);
    }

    function cycleTab(dir) {
        var n = state.tabs.length;
        if (n < 2) return;
        var idx = 0;
        for (var i = 0; i < n; i++) if (state.tabs[i].active) { idx = i; break; }
        var next = state.tabs[(idx + dir + n) % n];
        if (next) switchTabLocal(next.id);
    }

    // Applies window.__brainBoot (baked in by the host before first paint)
    // exactly once — deferred past head-parse so an embedded <script> on
    // internal pages overrides a stale injected payload. Falls back cleanly
    // to the normal push path when absent.

    // ---- window controls (chromeless) ----
    var curEl = null; // temporary *{cursor} style while hovering a resize edge
    function edgeCode(x, y) {
        var w = window.innerWidth, h = window.innerHeight;
        var l = x < 6, r = x > w - 6, t = y < 6, b = y > h - 6;
        if (t) return l ? 13 : r ? 14 : 12;
        if (b) return l ? 16 : r ? 17 : 15;
        return l ? 10 : r ? 11 : 0;
    }
    function edgeCursor(c) {
        return c === 10 || c === 11 ? 'ew-resize' :
            c === 12 || c === 15 ? 'ns-resize' :
            c === 13 || c === 17 ? 'nwse-resize' : 'nesw-resize';
    }
    function setEdgeCursor(c) {
        if (!c) { if (curEl) { curEl.remove(); curEl = null; } return; }
        if (!curEl) { curEl = document.createElement('style'); document.documentElement.appendChild(curEl); }
        curEl.textContent = '*{cursor:' + c + ' !important}';
    }
    // Show/hide the caption buttons and swap maximize ↔ restore glyph.
    function syncWin(s) {
        var w = s.getElementById('wctrl');
        if (w) w.style.display = state.wc ? 'flex' : 'none';
        var mb = s.getElementById('wmaxb');
        if (mb) { mb.innerHTML = state.w ? SVG.wres : SVG.wmax; mb.title = state.w ? 'Restore down' : 'Maximize'; }
        var bar = s.querySelector('.bar');
        if (bar) bar.classList.toggle('mtl', state.mtl);
    }

    // ---------------- devtools panel ----------------
    var dtOpen = false, dtTab = 'con', dtH = 340;
    var dtConSig = '', dtNetSig = '', dtPicking = false, dtReplBusy = false;
    var dtLast = null; // latest b:10 payload — filters re-render from cache
    var dtScrollPin = true; // console auto-scrolls only when parked at bottom

    function toggleDt(sh, tab, quiet) {
        var p = sh.getElementById('dt');
        if (!p) return;
        dtOpen = !dtOpen;
        if (dtOpen && tab) dtTab = tab;
        p.style.display = dtOpen ? 'flex' : 'none';
        if (dtOpen) {
            p.style.height = dtH + 'px'; syncDtTabs(sh); send('dtget');
            if (dtTab === 'cookies') send('dtcookies');
            else if (dtTab === 'sess') send('dtsessions');
            else if (dtTab === 'ins') ensureIns(sh);
        }
        if (!dtOpen) {
            if (dtPicking) setPick(sh, false);
            ['dtpin', 'dthl', 'dttip'].forEach(function (id) {
                var o = sh.getElementById(id); if (o) o.style.display = 'none';
            });
        }
        // Report to the host so the panel reopens after a document swap —
        // quiet restores (from a boot payload) must not echo back.
        if (!quiet) send('dtstate:' + (dtOpen ? dtTab : ''));
    }

    function syncDtTabs(sh) {
        sh.querySelectorAll('.dtab').forEach(function (b) {
            b.classList.toggle('on', b.getAttribute('data-t') === dtTab);
        });
        ['con', 'net', 'cookies', 'sess', 'ins'].forEach(function (t) {
            var p = sh.getElementById('dtp' + t);
            if (p) p.classList.toggle('on', t === dtTab);
        });
        var clr = sh.getElementById('dtclear');
        if (clr) clr.textContent = (dtTab === 'cookies' || dtTab === 'sess') ? 'Refresh' : 'Clear';
    }

    function fmtBytes(n) {
        n = +n || 0;
        return n >= 1073741824 ? (n / 1073741824).toFixed(2) + ' GB' :
            n >= 1048576 ? (n / 1048576).toFixed(1) + ' MB' :
            n >= 1024 ? (n / 1024).toFixed(1) + ' KB' : n + ' B';
    }

    // b:10 payload — rebuild a pane only when (count,last-entry) changes.
    function renderDt(sh, j) {
        var con = j.con || [], net = j.net || [];
        var cs = con.length + '|' + (con.length ? con[con.length - 1].t + con[con.length - 1].x : '');
        if (cs !== dtConSig) {
            dtConSig = cs;
            var list = sh.getElementById('dtlist');
            if (list) {
                var h = '';
                for (var i = 0; i < con.length; i++) {
                    var c = con[i];
                    h += '<div class="crow ' + (c.l === 'error' ? 'err' : c.l === 'warn' ? 'warn' : '') + '">' +
                        '<span class="ct">' + esc(c.t || '') + '</span><span>' + esc(c.x || '') + '</span></div>';
                }
                var pinned = list.scrollTop + list.clientHeight >= list.scrollHeight - 30;
                list.innerHTML = h;
                if (pinned && dtScrollPin) list.scrollTop = list.scrollHeight;
            }
        }
        var ns = dtNetType + '|' + dtNetQ + '|' + dtNetSel + '|' + net.length;
        for (var nx = 0; nx < net.length; nx++) // status/bytes land via merges
            ns += '|' + (net[nx].st || '') + (net[nx].sz || '');
        if (ns !== dtNetSig) {
            dtNetSig = ns;
            var nl = sh.getElementById('dtnet');
            if (nl) {
                var h2 = '';
                for (var k = 0; k < net.length; k++) {
                    var n = net[k];
                    if (dtNetType !== 'all' && netBucket(n.ty) !== dtNetType) continue;
                    if (dtNetQ && (n.u || '').toLowerCase().indexOf(dtNetQ) < 0) continue;
                    var st = n.er ? 'ERR' : (n.st || '…');
                    var cls = n.er ? 's0' : 's' + String(n.st || 0)[0];
                    h2 += '<div class="dnetr' + (dtNetSel === n.id ? ' sel' : '') + '" data-id="' + esc(n.id || '') + '">' +
                        '<span class="' + cls + '">' + st + '</span>' +
                        '<span>' + esc(n.m || '') + '</span><span style="color:#77779a">' + esc(n.ty || '') + '</span>' +
                        '<span style="color:#77779a">' + (n.sz ? fmtBytes(n.sz) : '') + '</span>' +
                        '<span class="u" title="' + esc(n.u || '') + '">' + esc(n.u || '') + '</span></div>';
                }
                nl.innerHTML = h2 || '<div style="padding:12px 14px;color:#55556f">no requests match</div>';
            }
        }
    }

    // Network pane state + views (list / detail / edit-and-resend).
    var dtNetType = 'all', dtNetQ = '', dtNetSel = null, dtNetEntry = null;
    var NETPILLS = [['all', 'All'], ['doc', 'Doc'], ['xhr', 'XHR'], ['js', 'JS'], ['css', 'CSS'],
        ['img', 'Img'], ['media', 'Media'], ['font', 'Font'], ['ws', 'WS'], ['other', 'Other']];
    function netBucket(ty) {
        ty = (ty || '').toLowerCase();
        return ty === 'document' ? 'doc' : (ty === 'xhr' || ty === 'fetch') ? 'xhr' :
            ty === 'script' ? 'js' : ty === 'stylesheet' ? 'css' : ty === 'image' ? 'img' :
            ty === 'font' ? 'font' : ty === 'media' ? 'media' : ty === 'websocket' ? 'ws' : 'other';
    }
    function showNetView(sh, v) { // 'list' | 'detail' | 'edit'
        var pane = sh.getElementById('dtpnet'); if (!pane) return;
        var list = pane.querySelector('.dtscroll'), hdr = pane.querySelector('.dneth'),
            tools = pane.querySelector('.dnettools'),
            det = sh.getElementById('dtdetail'), ed = sh.getElementById('dtedit');
        if (list) list.style.display = v === 'list' ? '' : 'none';
        if (hdr) hdr.style.display = v === 'list' ? '' : 'none';
        if (tools) tools.style.display = v === 'list' ? '' : 'none';
        if (det) det.style.display = v === 'detail' ? 'flex' : 'none';
        if (ed) ed.style.display = v === 'edit' ? 'flex' : 'none';
    }
    function kvRows(map) {
        if (!map) return '<div class="dtkv"><b>—</b><span style="color:#55556f">none captured</span></div>';
        var keys = Object.keys(map).sort(), h = '';
        for (var i = 0; i < keys.length; i++)
            h += '<div class="dtkv"><b title="' + esc(keys[i]) + '">' + esc(keys[i]) + '</b><span>' + esc(map[keys[i]]) + '</span></div>';
        return h || '<div class="dtkv"><b>—</b><span style="color:#55556f">none</span></div>';
    }
    function renderNetDetail(sh) {
        var d = sh.getElementById('dtdetail'), e = dtNetEntry;
        if (!d || !e) return;
        var h = '<div class="dttools"><button class="dtbtn" id="dtback">‹ All requests</button>' +
            '<span class="dtsp"></span><button class="dtbtn" id="dteditb" style="color:#b9b9ff">✎ Edit and resend</button></div>' +
            '<div class="dtbscroll">' +
            '<div class="dtsec">General</div>' +
            '<div class="dtkv"><b>URL</b><span>' + esc(e.u) + '</span></div>' +
            '<div class="dtkv"><b>Method</b><span>' + esc(e.m) + '</span></div>' +
            '<div class="dtkv"><b>Status</b><span>' + (e.er ? '<span class="resp-err">' + esc(e.er) + '</span>' : (e.st || 'pending')) + '</span></div>' +
            '<div class="dtkv"><b>Type</b><span>' + esc(e.ty || '') + '</span></div>' +
            '<div class="dtkv"><b>Size</b><span>' + fmtBytes(e.sz) + '</span></div>' +
            '<div class="dtkv"><b>Time</b><span>' + esc(e.t || '') + '</span></div>';
        if (e.pd) h += '<div class="dtsec">Request body</div><pre style="white-space:pre-wrap;word-break:break-all;color:#b9b9d4;margin:0 0 4px">' + esc(e.pd) + '</pre>';
        h += '<div class="dtsec">Request headers</div>' + kvRows(e.rq) +
            '<div class="dtsec">Response headers</div>' + kvRows(e.rs) + '</div>';
        d.innerHTML = h;
        showNetView(sh, 'detail');
        d.querySelector('#dtback').addEventListener('click', function () {
            dtNetSel = null; dtNetEntry = null; showNetView(sh, 'list');
        });
        d.querySelector('#dteditb').addEventListener('click', function () { renderNetEditor(sh); });
    }
    function addHdrRow(sh, name, val) {
        var box = sh.getElementById('dteh'); if (!box) return;
        var row = document.createElement('div');
        row.className = 'dthr';
        row.innerHTML = '<input class="dtedit-in hn" placeholder="Name" spellcheck="false">' +
            '<input class="dtedit-in hv" placeholder="Value" spellcheck="false">' +
            '<button class="hx" title="Remove">✕</button>';
        row.children[0].value = name || ''; row.children[1].value = val || '';
        row.querySelector('.hx').addEventListener('click', function () { row.remove(); });
        box.appendChild(row);
    }
    function renderNetEditor(sh) {
        var ed = sh.getElementById('dtedit'), e = dtNetEntry;
        if (!ed || !e) return;
        var methods = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'];
        var h = '<div class="dttools"><button class="dtbtn" id="dteback">‹ Detail</button><span class="dtsp"></span>' +
            '<span style="color:#55556f;font-size:10.5px">bypasses page CSP &amp; CORS · profile cookies merged</span></div>' +
            '<div class="dte-row"><select id="dtem" class="dtedit-in">' +
            methods.map(function (m) { return '<option' + (m === e.m ? ' selected' : '') + '>' + m + '</option>'; }).join('') +
            '</select><input id="dteu" class="dtedit-in" spellcheck="false" placeholder="https://…">' +
            '<button class="dtsend" id="dtesend">Send</button></div>' +
            '<div style="flex:1;overflow-y:auto;min-height:0" class="dtbscroll">' +
            '<div class="dtsec">Headers</div><div id="dteh"></div>' +
            '<button class="dtbtn" id="dteadd" style="margin:4px 14px">+ header</button>' +
            '<div class="dtsec">Body</div><textarea id="dteb" class="dtedit-in" spellcheck="false"></textarea>' +
            '<div class="dtsec">Response</div><div id="dteresp"><span style="color:#55556f">not sent yet</span></div>' +
            '</div>';
        ed.innerHTML = h;
        ed.querySelector('#dteu').value = e.u;
        if (e.pd) ed.querySelector('#dteb').value = e.pd;
        showNetView(sh, 'edit');
        var rq = e.rq || {};
        var keys = Object.keys(rq).filter(function (k) { return k[0] !== ':'; }).sort();
        for (var i = 0; i < keys.length; i++) addHdrRow(sh, keys[i], rq[keys[i]]);
        if (!keys.length) addHdrRow(sh, '', '');
        sh.getElementById('dteback').addEventListener('click', function () { showNetView(sh, 'detail'); });
        sh.getElementById('dteadd').addEventListener('click', function () { addHdrRow(sh, '', ''); });
        sh.getElementById('dtesend').addEventListener('click', function () {
            var hd = {};
            sh.querySelectorAll('#dteh .dthr').forEach(function (row) {
                var n = row.children[0].value.trim();
                if (n) hd[n] = row.children[1].value;
            });
            var btn = sh.getElementById('dtesend');
            btn.disabled = true; btn.textContent = 'Sending…';
            sh.getElementById('dteresp').innerHTML = '<span style="color:#66667f">sending…</span>';
            send('dtsend:' + JSON.stringify({
                m: sh.getElementById('dtem').value,
                u: sh.getElementById('dteu').value.trim(),
                h: hd, b: sh.getElementById('dteb').value || null,
            }));
        });
    }
    function renderDtResp(sh, j) {
        var r = sh.getElementById('dteresp'); if (!r) return;
        var btn = sh.getElementById('dtesend');
        if (btn) { btn.disabled = false; btn.textContent = 'Send'; }
        if (!j.ok) { r.innerHTML = '<span class="resp-err">✕ ' + esc(j.e || 'request failed') + '</span>'; return; }
        var cls = j.st >= 200 && j.st < 400 ? 'resp-ok' : 'resp-err';
        var h = '<div><span class="' + cls + '" style="font-size:13px;font-weight:700">' + j.st + '</span>' +
            '<span class="rdet"> · ' + j.ms + ' ms · ' + fmtBytes(j.sz) + '</span></div>';
        if (j.rh) {
            var ks = Object.keys(j.rh).sort();
            h += '<div class="dtsec">Headers</div>';
            for (var i = 0; i < ks.length; i++)
                h += '<div class="dtkv"><b>' + esc(ks[i]) + '</b><span>' + esc(j.rh[ks[i]]) + '</span></div>';
        }
        h += '<div class="dtsec">Body</div>';
        var body = j.bin ? '[binary body, ' + fmtBytes(j.sz) + ']' : (j.body || '');
        try { // pretty-print JSON bodies
            var pj = JSON.parse(body);
            body = JSON.stringify(pj, null, 2);
        } catch (e) { }
        h += '<pre>' + esc(body.slice(0, 20000)) + '</pre>';
        r.innerHTML = h;
    }

    // ---- Cookies pane ----
    var dtCk = [], dtCkView = [], dtCkHost = '', dtCkQ = '', dtCkSite = true, dtCkEdit = null;
    function renderCookies(sh) {
        var l = sh.getElementById('dtclist'); if (!l) return;
        dtCkView = dtCk.filter(function (c) {
            if (dtCkSite && dtCkHost) {
                var d = (c.d || '').replace(/^\./, '');
                // No domain = document.cookie source — inherently this site.
                if (d && !(dtCkHost === d || dtCkHost.endsWith('.' + d) || d.endsWith('.' + dtCkHost))) return false;
            }
            return !dtCkQ || (c.n + ' ' + (c.d || '')).toLowerCase().indexOf(dtCkQ) >= 0;
        });
        var h = '';
        for (var i = 0; i < dtCkView.length; i++) {
            var c = dtCkView[i];
            var flags = (c.h ? '<span class="ckb ho">HttpOnly</span>' : '') +
                (c.s ? '<span class="ckb sc">Secure</span>' : '');
            var exp = c.e > 0 ? new Date(c.e * 1000).toLocaleDateString() + ' ' +
                new Date(c.e * 1000).toLocaleTimeString() : 'Session';
            h += '<div class="ckr" data-i="' + i + '">' +
                '<span title="' + esc(c.n) + '">' + esc(c.n) + '</span>' +
                '<span class="cv" title="' + esc(c.v) + '">' + esc(c.v) + '</span>' +
                '<span>' + esc(c.d || '') + '</span><span>' + esc(c.p || '/') + '</span>' +
                '<span>' + esc(exp) + '</span><span>' + flags + '</span>' +
                '<button class="ckx" title="Delete cookie" data-di="' + i + '">✕</button></div>';
        }
        l.innerHTML = h || '<div class="sempty">' +
            (dtCk.length ? 'no cookies match the filter' : 'no cookies in the profile') + '</div>';
    }
    function renderCkEditor(sh) {
        var ed = sh.getElementById('dtcedit'); if (!ed) return;
        if (!dtCkEdit) { ed.style.display = 'none'; return; }
        var c = dtCkEdit;
        ed.innerHTML =
            '<div class="cer"><input class="dtedit-in" id="cen" placeholder="Name" spellcheck="false" value="' + esc(c.n || '') + '">' +
            '<input class="dtedit-in" id="cev" placeholder="Value" spellcheck="false" value="' + esc(c.v || '') + '"></div>' +
            '<div class="cer"><input class="dtedit-in" id="ced" placeholder="Domain" spellcheck="false" value="' +
            esc(c.d || (dtCkHost ? '.' + dtCkHost : '')) + '">' +
            '<input class="dtedit-in" id="cep" placeholder="Path" spellcheck="false" value="' + esc(c.p || '/') + '"></div>' +
            '<div class="cer cflags">' +
            '<label><input type="checkbox" id="ceh"' + (c.h ? ' checked' : '') + '> HttpOnly</label>' +
            '<label><input type="checkbox" id="ces"' + (c.s ? ' checked' : '') + '> Secure</label>' +
            '<span class="dtsp"></span>' +
            '<button class="dtsend" id="cesave" style="padding:4px 16px">' + (c.add ? 'Add' : 'Save') + '</button>' +
            '<button class="dtbtn" id="cecancel">Cancel</button></div>';
        ed.style.display = 'flex';
        sh.getElementById('cecancel').addEventListener('click', function () {
            dtCkEdit = null; renderCkEditor(sh);
        });
        sh.getElementById('cesave').addEventListener('click', function () {
            var n = sh.getElementById('cen').value.trim(); if (!n) return;
            send('dtcset:' + JSON.stringify({
                n: n, v: sh.getElementById('cev').value,
                d: sh.getElementById('ced').value.trim() || null,
                p: sh.getElementById('cep').value.trim() || '/',
                h: sh.getElementById('ceh').checked, s: sh.getElementById('ces').checked,
            }));
            dtCkEdit = null; renderCkEditor(sh);
        });
    }

    // ---- Sessions pane ----
    function renderSessions(sh, j) {
        var l = sh.getElementById('dtslist'); if (!l) return;
        var open = j.open || [], closed = j.closed || [], saved = j.saved || [];
        var h = '<div class="ssec">Open tabs<span class="cnt">' + open.length + '</span></div>';
        open.forEach(function (t) {
            h += '<div class="srow" data-tab="' + esc(t.id) + '">' +
                (t.i ? '<img src="' + esc(t.i) + '">' : '<span style="width:14px;flex:none"></span>') +
                '<span class="st">' + esc(t.t || t.u || 'New Tab') + '</span>' +
                '<span class="su">' + esc(t.u || '') + '</span>' +
                (t.priv ? '<span class="sbadge priv">private</span>' : '') +
                (t.pin ? '<span class="sbadge pin">pinned</span>' : '') +
                (t.act ? '<span class="sbadge cur">current</span>' : '') + '</div>';
        });
        h += '<div class="ssec">Recently closed<span class="cnt">' + closed.length + '</span></div>';
        if (!closed.length) h += '<div class="sempty">nothing recently closed</div>';
        closed.forEach(function (c) {
            h += '<div class="srow" data-reopen="1" data-u="' + esc(c.u) + '">' +
                (c.i ? '<img src="' + esc(c.i) + '">' : '<span style="width:14px;flex:none"></span>') +
                '<span class="su">' + esc(c.u) + '</span>' +
                '<button class="sbtn">Restore</button></div>';
        });
        h += '<div class="ssec">Previous session<span class="cnt">' + saved.length + '</span>' +
            (saved.length ? '<button class="sbtn" id="sessall">Restore all</button>' : '') + '</div>';
        if (!saved.length) h += '<div class="sempty">no saved session</div>';
        saved.forEach(function (u) {
            h += '<div class="srow" data-newtab="1" data-u="' + esc(u) + '">' +
                '<span style="width:14px;flex:none"></span><span class="su">' + esc(u) + '</span>' +
                '<button class="sbtn">Open</button></div>';
        });
        l.innerHTML = h;
    }

    // Console REPL — echo the input, host evals through the driver
    // (Runtime.evaluate / evaluateJavaScript — not subject to page CSP).
    function dtReplRun(sh, expr) {
        var list = sh.getElementById('dtlist');
        if (list) {
            var d = document.createElement('div');
            d.className = 'crow out';
            d.innerHTML = '<span class="ct">›</span><span style="color:#b9b9ff">' + esc(expr) + '</span>';
            list.appendChild(d);
            list.scrollTop = list.scrollHeight;
        }
        dtReplBusy = true;
        send('dteval:' + expr);
    }

    // b:11 — eval result line.
    function dtEvalResult(sh, j) {
        dtReplBusy = false;
        var list = sh.getElementById('dtlist');
        if (!list) return;
        var d = document.createElement('div');
        d.className = 'crow ' + (j.ok ? 'in' : 'err');
        var v;
        try {
            v = j.ok ? (typeof j.v === 'string' ? j.v : JSON.stringify(j.v) ?? 'undefined')
                     : 'Uncaught ' + (j.e || 'error');
        } catch (e) { v = String(j.v); }
        d.innerHTML = '<span class="ct">←</span><span>' + esc(v) + '</span>';
        list.appendChild(d);
        list.scrollTop = list.scrollHeight;
    }

    // Element picker for the Inspect tab.
    function setPick(sh, on) {
        dtPicking = on;
        var b = sh.getElementById('dtpick');
        if (b) b.classList.toggle('armed', on);
        var st = sh.getElementById('dtinsst');
        if (st) st.textContent = on ? 'hover to inspect — click to pin' : 'click any element on the page';
        var hl = sh.getElementById('dthl'), tip = sh.getElementById('dttip');
        if (!on) { if (hl) hl.style.display = 'none'; if (tip) tip.style.display = 'none'; }
        document.documentElement.style.cursor = on ? 'crosshair' : '';
    }

    // ---- Inspect: DOM tree + element details ----
    var insSel = null, insSelRow = null, insStQ = '';

    function insKidOK(c) {
        return c.nodeType === 1 || (c.nodeType === 3 && (c.nodeValue || '').trim());
    }
    function insKids(n) {
        if (!n) return [];
        if (n.nodeType === 9) return n.documentElement ? [n.documentElement] : [];
        if (n.nodeType === 11) return [].filter.call(n.childNodes, insKidOK);
        if (n.nodeType !== 1) return [];
        var k = [];
        if (n.shadowRoot) k.push(n.shadowRoot); // open shadow roots only
        for (var c = n.firstChild; c; c = c.nextSibling) if (insKidOK(c)) k.push(c);
        return k;
    }
    function insShort(n) {
        if (!n) return '?';
        if (n.nodeType === 11) return 'shadow-root';
        if (n.nodeType === 9) return '#document';
        if (n.nodeType === 3) return '"' + (n.nodeValue || '').trim().slice(0, 20) + '"';
        var s = n.tagName.toLowerCase();
        if (n.id) s += '#' + n.id;
        var cl = (typeof n.className === 'string' ? n.className : '').trim().split(/\s+/);
        for (var i = 0; i < cl.length && i < 3; i++) if (cl[i]) s += '.' + cl[i];
        return s;
    }
    function insLabel(n) {
        if (n.nodeType === 3)
            return '<span class="tq">"' + esc((n.nodeValue || '').trim().slice(0, 140)) + '"</span>';
        if (n.nodeType === 11)
            return '<span class="tl">&lt;</span><b>shadow-root</b><span class="ta"> open</span><span class="tl">&gt;</span>';
        if (n.nodeType === 9) return '<b>#document</b>';
        var h = '<span class="tl">&lt;</span><b>' + esc(n.tagName.toLowerCase()) + '</b>';
        var shown = {};
        if (n.id) { shown.id = 1; h += '<span class="ta"> id</span><span class="tl">=</span><span class="tv">"' + esc(n.id) + '"</span>'; }
        var cl = (typeof n.className === 'string' ? n.className : '').trim();
        if (cl) { shown['class'] = 1; h += '<span class="ta"> class</span><span class="tl">=</span><span class="tv">"' + esc(cl.slice(0, 110)) + '"</span>'; }
        if (!n.id && !cl) {
            for (var i = 0, c = 0; i < n.attributes.length && c < 3; i++) {
                var a = n.attributes[i];
                h += '<span class="ta"> ' + esc(a.name) + '</span><span class="tl">=</span><span class="tv">"' + esc(a.value.slice(0, 70)) + '"</span>';
                c++;
            }
        }
        return h + '<span class="tl">&gt;</span>';
    }
    function insRow(n, depth) {
        depth = depth || 0;
        var row = document.createElement('div');
        row.className = 'tn' + (n.nodeType === 3 ? ' txt' : '');
        row._n = n; row._d = depth;
        var th = document.createElement('div');
        th.className = 'th';
        var tx = document.createElement('span');
        tx.className = 'tx';
        th.appendChild(tx);
        var lb = document.createElement('span');
        lb.innerHTML = insLabel(n);
        lb.style.cssText = 'overflow:hidden;text-overflow:ellipsis;min-width:0';
        th.appendChild(lb);
        var dm = document.createElement('span');
        dm.className = 'tdim';
        th.appendChild(dm);
        row._dim = dm;
        row._lb = lb;
        row.appendChild(th);
        row._tx = tx;
        if (insKids(n).length) {
            tx.textContent = '▸';
            var ch = document.createElement('div');
            ch.className = 'tch';
            // Aurora indent guide — the guide's hue walks with depth.
            ch.style.borderColor = 'hsl(calc(var(--h) + ' + ((depth % 9) * 22) + ') 80% 66% / .3)';
            row.appendChild(ch);
            row._ch = ch;
        }
        return row;
    }
    function insExpand(row, open) {
        if (!row || !row._ch) return;
        var want = open === undefined ? row._ch.style.display !== 'block' : open;
        if (want && !row._ch._built) {
            var k = insKids(row._n);
            for (var i = 0; i < k.length; i++) row._ch.appendChild(insRow(k[i], row._d + 1));
            row._ch._built = true;
        }
        row._ch.style.display = want ? 'block' : 'none';
        row.classList.toggle('open', want);
        row._tx.textContent = want ? '▾' : '▸';
    }
    function insParentOf(n) {
        return n.parentNode || n.host || null; // .host walks out of a shadow root
    }
    function insChain(n) {
        var c = [];
        for (var x = n; x && x.nodeType !== 9; x = insParentOf(x)) c.unshift(x);
        return c;
    }
    function buildInsTree(sh) {
        var t = sh.getElementById('instree'); if (!t) return;
        t.innerHTML = '';
        insSelRow = null;
        var kids = insKids(document);
        for (var i = 0; i < kids.length; i++) {
            var r = insRow(kids[i]);
            t.appendChild(r);
            insExpand(r, true);
        }
    }
    function ensureIns(sh) {
        var t = sh.getElementById('instree'); if (!t) return;
        if (t.children.length) return;
        buildInsTree(sh);
        if (document.body && document.body.isConnected) insSelect(sh, document.body, false);
        else if (document.documentElement) insSelect(sh, document.documentElement, false);
    }
    function insSelect(sh, n, scroll) {
        if (!n || n.nodeType === 9) return;
        var t = sh.getElementById('instree');
        if (!t) return;
        if (!t.children.length) buildInsTree(sh);
        var chain = insChain(n), row = null, kids = [].slice.call(t.children);
        for (var i = 0; i < chain.length; i++) {
            row = null;
            for (var j = 0; j < kids.length; j++)
                if (kids[j]._n === chain[i]) { row = kids[j]; break; }
            if (!row) break;
            if (i < chain.length - 1) {
                insExpand(row, true);
                kids = row._ch ? [].slice.call(row._ch.children) : [];
            }
        }
        if (row) {
            var old = t.querySelector('.tn.sel');
            if (old) old.classList.remove('sel');
            row.classList.add('sel');
            insSelRow = row;
            if (scroll !== false) {
                var th = row.querySelector('.th');
                if (th) th.scrollIntoView({ block: 'nearest' });
            }
        }
        insSel = n;
        renderInsDetails(sh, n);
        renderInsCrumbs(sh, n);
        pinHighlight(sh, n);
    }
    function insSelector(el) {
        if (el.id && /^[A-Za-z_][\w\-:.]*$/.test(el.id)) return '#' + el.id;
        var parts = [];
        for (var n = el; n && n.nodeType === 1 && parts.length < 6; n = n.parentNode) {
            var p = n.tagName.toLowerCase();
            if (n.id) { parts.unshift(p + '#' + n.id); break; }
            if (n.parentNode) {
                var sib = [].filter.call(n.parentNode.children, function (c) { return c.tagName === n.tagName; });
                if (sib.length > 1) p += ':nth-of-type(' + (sib.indexOf(n) + 1) + ')';
            }
            parts.unshift(p);
        }
        return parts.join(' > ') || '?';
    }
    function px4(cs, base) {
        var s = base === 'border' ? 'Width' : '';
        return [parseFloat(cs[base + 'Top' + s]) || 0, parseFloat(cs[base + 'Right' + s]) || 0,
            parseFloat(cs[base + 'Bottom' + s]) || 0, parseFloat(cs[base + 'Left' + s]) || 0];
    }
    function bmVals(v) {
        return '<span class="v t">' + v[0] + '</span><span class="v r">' + v[1] + '</span>' +
            '<span class="v b">' + v[2] + '</span><span class="v l">' + v[3] + '</span>';
    }
    function renderInsDetails(sh, el) {
        var nm = sh.getElementById('insname'); if (!nm) return;
        var ok = el && el.nodeType === 1;
        nm.innerHTML = ok
            ? '<b>&lt;' + esc(el.tagName.toLowerCase()) + '&gt;</b><span class="inssel">' + esc(insShort(el)) + '</span>' +
              '<span class="dtsp"></span>' +
              '<button class="dtbtn" id="incsel" title="Copy unique CSS selector">⧉ sel</button>' +
              '<button class="dtbtn" id="inchtml" title="Copy outerHTML">⧉ html</button>' +
              '<button class="dtbtn" id="inview" title="Scroll into view">⇣ view</button>'
            : '<span style="color:#66667f;font-size:11px">pick or select a node</span>';
        if (ok) {
            nm.querySelector('#incsel').addEventListener('click', function (e) {
                var b = e.target;
                try { navigator.clipboard.writeText(insSelector(el)); } catch (x) { }
                b.textContent = 'copied'; setTimeout(function () { b.textContent = '⧉ sel'; }, 900);
            });
            nm.querySelector('#inchtml').addEventListener('click', function (e) {
                var b = e.target;
                try { navigator.clipboard.writeText((el.outerHTML || '').slice(0, 40000)); } catch (x) { }
                b.textContent = 'copied'; setTimeout(function () { b.textContent = '⧉ html'; }, 900);
            });
            nm.querySelector('#inview').addEventListener('click', function () {
                try { el.scrollIntoView({ block: 'center', behavior: 'instant' }); } catch (x) { }
                pinHighlight(sh, el);
            });
        }
        var b = sh.getElementById('insbox');
        if (b) {
            if (!ok) b.innerHTML = '';
            else {
                var cs = getComputedStyle(el), r = el.getBoundingClientRect();
                b.innerHTML = '<div class="bm"><div class="bmL bmm">' + bmVals(px4(cs, 'margin')) + '<i>margin</i>' +
                    '<div class="bmL bmb">' + bmVals(px4(cs, 'border')) + '<i>border</i>' +
                    '<div class="bmL bmp">' + bmVals(px4(cs, 'padding')) + '<i>padding</i>' +
                    '<div class="bmC">' + Math.round(r.width) + ' × ' + Math.round(r.height) + '</div>' +
                    '</div></div></div></div>';
            }
        }
        renderInsInline(sh, el);
        renderInsStyles(sh, el);
        var at = sh.getElementById('insattrs');
        if (at) {
            var ac = sh.getElementById('insac');
            if (!ok || !el.attributes.length) {
                at.innerHTML = '<div class="sempty">no attributes</div>';
                if (ac) ac.textContent = '';
            }
            else {
                if (ac) ac.textContent = '· ' + el.attributes.length;
                var h = '';
                for (var i = 0; i < el.attributes.length; i++) {
                    var a = el.attributes[i];
                    h += '<div class="stl"><span class="sp" title="' + esc(a.name) + '">' + esc(a.name) +
                        '</span><span class="sv" title="Double-click to edit">' + esc(a.value) + '</span></div>';
                }
                at.innerHTML = h;
            }
        }
    }
    var STORD = ['Layout', 'Box', 'Text', 'Paint', 'Motion', 'Other'];
    var insStClosed = { Other: true };
    function stGroup(k) {
        if (/^(margin|padding|gap|row-gap|column-gap|border-(top|right|bottom|left)-width|border-width)/.test(k)) return 'Box';
        if (/^(font|line-|text-|letter-|word-|white-space|vertical-|direction|unicode-|quotes|hyphens|writing-|tab-size|list-|orphans|widows|caret)/.test(k)) return 'Text';
        if (/^(transition|animation|transform|offset|rotate|scale|translate|view-timeline|scroll-timeline|motion)/.test(k)) return 'Motion';
        if (/^(color$|background|box-shadow|opacity|border.*(color|radius|style|image)|outline|fill$|fill-|stroke|mask|clip|filter|backdrop|mix-blend|object-|accent-color|color-scheme|-webkit-(mask|text-fill|text-stroke|box-reflect|print)|appearance|visibility)/.test(k)) return 'Paint';
        if (/^(display|position|top$|right$|bottom$|left$|inset|z-index|float|clear|flex|grid|columns?|column-|align|justify|order|overflow|width$|height$|min-|max-|box-sizing|contain|aspect|place|table|empty|break-|resize|isolation|scroll|overscroll|pointer-events|touch-action|user-select|-webkit-user|cursor|content$|all$)/.test(k)) return 'Layout';
        return 'Other';
    }
    var SWRE = /(#[0-9a-f]{3,8}\b|rgba?\([^)]*\)|hsla?\([^)]*\)|\b(?:black|white|red|blue|green|gray|grey|orange|purple|pink|yellow|cyan|magenta|transparent)\b)/i;
    function stValHtml(v) {
        var m = v.match(SWRE);
        return (m ? '<i class="sw" style="background:' + esc(m[1]) + '"></i>' : '') + esc(v);
    }
    function renderInsStyles(sh, el) {
        var box = sh.getElementById('insstyles'); if (!box) return;
        if (!el || el.nodeType !== 1) { box.innerHTML = '<div class="sempty">—</div>'; return; }
        var cs = getComputedStyle(el), groups = {}, total = 0;
        for (var i = 0; i < cs.length; i++) {
            var k = cs[i], v = cs.getPropertyValue(k);
            if (insStQ && k.toLowerCase().indexOf(insStQ) < 0 && v.toLowerCase().indexOf(insStQ) < 0) continue;
            var g = stGroup(k);
            (groups[g] = groups[g] || []).push([k, v]);
            total++;
        }
        var h = '';
        for (var gi = 0; gi < STORD.length; gi++) {
            var gn = STORD[gi], rows = groups[gn];
            if (!rows || !rows.length) continue;
            var closed = insStQ ? false : !!insStClosed[gn];
            h += '<div class="stg' + (closed ? ' closed' : '') + '" data-g="' + gn + '">' +
                '<div class="stgh"><span class="gc">▾</span>' + gn + '<span class="cnt">' + rows.length + '</span></div>' +
                '<div class="stgb">';
            for (var r = 0; r < rows.length; r++)
                h += '<div class="stl"><span class="sp" title="' + esc(rows[r][0]) + '">' + esc(rows[r][0]) +
                    '</span><span class="sv">' + stValHtml(rows[r][1]) + '</span></div>';
            h += '</div></div>';
        }
        box.innerHTML = h || '<div class="sempty">no styles match</div>';
        var c = sh.getElementById('insstc'); if (c) c.textContent = total ? '· ' + total : '';
    }
    function renderInsCrumbs(sh, el) {
        var bc = sh.getElementById('insbc'); if (!bc) return;
        var chain = insChain(el), h = '';
        for (var i = 0; i < chain.length; i++) {
            if (i) h += '<span class="csep">›</span>';
            h += '<button class="cr' + (chain[i] === el ? ' on' : '') + '" data-ci="' + i + '">' + esc(insShort(chain[i])) + '</button>';
        }
        bc.innerHTML = h;
        bc._chain = chain;
        var cur = bc.querySelector('.cr.on');
        if (cur) cur.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    }
    function pinHighlight(sh, el) {
        var p = sh.getElementById('dtpin'); if (!p) return;
        if (!el || el.nodeType !== 1 || !el.isConnected) { p.style.display = 'none'; return; }
        var r = el.getBoundingClientRect();
        if (r.width < 1 && r.height < 1) { p.style.display = 'none'; return; }
        p.style.display = 'block';
        p.style.left = r.left + 'px'; p.style.top = r.top + 'px';
        p.style.width = Math.max(r.width, 2) + 'px'; p.style.height = Math.max(r.height, 2) + 'px';
    }

    // ---- Inline style editor (element.style) ----
    var insInlineOff = new WeakMap(); // el -> {prop: savedValue} while checkbox off
    function setStyle(el, k, v) {
        var pri = '';
        var m = /^(.*?)\s*!\s*important\s*$/i.exec(v);
        if (m) { v = m[1]; pri = 'important'; }
        try { el.style.setProperty(k, v, pri); } catch (e) { }
    }
    function silRow(k, v, on) {
        return '<div class="sil' + (on ? '' : ' off') + '" data-k="' + esc(k) + '" data-v="' + esc(v) + '">' +
            '<input type="checkbox" class="sic"' + (on ? ' checked' : '') + '>' +
            '<input class="sin" value="' + esc(k) + '" spellcheck="false">' +
            '<input class="siv" value="' + esc(v) + '" spellcheck="false">' +
            '<button class="hx" title="Remove property">✕</button></div>';
    }
    function renderInsInline(sh, el) {
        var box = sh.getElementById('insinline'); if (!box) return;
        if (!el || el.nodeType !== 1) { box.innerHTML = ''; return; }
        var off = insInlineOff.get(el) || {}, h = '', n = 0;
        for (var i = 0; i < el.style.length; i++) {
            var k = el.style[i];
            var dis = off[k] !== undefined;
            h += silRow(k, dis ? off[k] : el.style.getPropertyValue(k), !dis);
            n++;
        }
        for (var k2 in off) // toggled-off props that left the style list
            if (el.style.getPropertyValue(k2) === '') { h += silRow(k2, off[k2], false); n++; }
        h += '<div class="sil add"><span class="sic"></span>' +
            '<input class="sin" placeholder="property" spellcheck="false">' +
            '<input class="siv" placeholder="value" spellcheck="false">' +
            '<button class="hx" tabindex="-1">✕</button></div>';
        box.innerHTML = h;
        var c = sh.getElementById('insic'); if (c) c.textContent = n ? '· ' + n : '';
    }
    function insSilCommit(sh, row) {
        var el = insSel; if (!el || el.nodeType !== 1) return;
        var cbx = row.querySelector('.sic'), nin = row.querySelector('.sin'), vin = row.querySelector('.siv');
        var ok = row.getAttribute('data-k') || '', nk = nin.value.trim(), nv = vin.value.trim();
        var off = insInlineOff.get(el); if (!off) { off = {}; insInlineOff.set(el, off); }
        if (row.classList.contains('add')) {
            if (nk && nv) setStyle(el, nk, nv);
        } else {
            if (ok && ok !== nk) { el.style.removeProperty(ok); delete off[ok]; }
            if (!nk || !nv) el.style.removeProperty(nk || ok);
            else setStyle(el, nk, nv);
        }
    }
    // ---- Edit as HTML ----
    function openInsHtml(sh, n) {
        var o = sh.getElementById('insh'); if (!o || !n) return;
        var isTxt = n.nodeType === 3;
        if (n.nodeType !== 1 && !isTxt) return;
        var ta = sh.getElementById('inshta');
        ta.value = isTxt ? (n.nodeValue || '') : n.outerHTML;
        var lbl = o.querySelector('.insed-bar span');
        if (lbl) lbl.textContent = isTxt
            ? 'editing text node · ⌘↵ apply · esc cancel'
            : 'editing outerHTML — replaces the element · ⌘↵ apply · esc cancel';
        o.style.display = 'flex';
        o._el = n;
        ta.focus();
    }
    function closeInsHtml(sh) {
        var o = sh.getElementById('insh');
        if (o) { o.style.display = 'none'; o._el = null; }
    }
    function applyInsHtml(sh) {
        var o = sh.getElementById('insh'), n = o && o._el;
        if (!n) { closeInsHtml(sh); return; }
        if (n.nodeType === 3) {
            // Text node survives — just swap its content, no rebuild needed.
            try { n.nodeValue = sh.getElementById('inshta').value; } catch (x) { }
            closeInsHtml(sh);
            if (insSelRow && insSelRow._lb) insSelRow._lb.innerHTML = insLabel(n);
            return;
        }
        var p = insParentOf(n);
        try { n.outerHTML = sh.getElementById('inshta').value; } catch (x) { }
        closeInsHtml(sh);
        insSel = null; insSelRow = null;
        buildInsTree(sh);
        if (p && p.isConnected && p.nodeType === 1) insSelect(sh, p, false);
    }
    // Right-click menu for tree nodes — the shadow root suppresses the
    // native menu, so this is the only context menu inspect gets (ours).
    function showNodeMenu(sh, x, y, n, tn) {
        var m = sh.getElementById('pmenu'); if (!m) return;
        var isEl = n && n.nodeType === 1, items = [];
        if (isEl) {
            items.push(['nsel', 'Inspect element', 'ac']);
            items.push(null);
            items.push(['ehtml', 'Edit as HTML']);
            items.push(['csel', 'Copy selector']);
            items.push(['chtml', 'Copy outerHTML']);
            items.push(['sview', 'Scroll into view']);
            items.push(['expall', 'Expand subtree']);
        } else {
            items.push(['etxt', 'Edit text', 'ac']);
            items.push(['ctxt', 'Copy text']);
        }
        items.push(null);
        items.push(['del', 'Delete node']);
        var html = '';
        for (var i = 0; i < items.length; i++)
            html += items[i] === null ? '<div class="msep"></div>' :
                '<div class="mi' + (items[i][2] ? ' ' + items[i][2] : '') + '" data-c="' + items[i][0] + '">' + items[i][1] + '</div>';
        m.innerHTML = html;
        m.style.display = 'block';
        var mw = m.offsetWidth, mh = m.offsetHeight;
        m.style.left = Math.min(x, Math.max(0, window.innerWidth - mw - 8)) + 'px';
        m.style.top = Math.min(y, Math.max(0, window.innerHeight - mh - 8)) + 'px';
        m.querySelectorAll('.mi').forEach(function (el) {
            el.addEventListener('mousedown', function (e) {
                e.preventDefault(); e.stopPropagation();
                m.style.display = 'none';
                var c = el.getAttribute('data-c');
                if (c === 'nsel') insSelect(sh, n);
                else if (c === 'ehtml' || c === 'etxt') openInsHtml(sh, n);
                else if (c === 'csel') copyText(insSelector(n));
                else if (c === 'chtml') copyText((n.outerHTML || '').slice(0, 40000));
                else if (c === 'ctxt') copyText((n.nodeValue || '').trim());
                else if (c === 'sview') { try { n.scrollIntoView({ block: 'center', behavior: 'instant' }); } catch (x) { } pinHighlight(sh, n); }
                else if (c === 'expall') {
                    var count = 0;
                    (function ex(row) {
                        if (!row || ++count > 1500) return;
                        insExpand(row, true);
                        if (row._ch) for (var i = 0; i < row._ch.children.length; i++) ex(row._ch.children[i]);
                    })(tn);
                }
                else if (c === 'del') {
                    var p = insParentOf(n);
                    try { n.parentNode.removeChild(n); } catch (x) { }
                    if (tn && tn.remove) tn.remove();
                    if (p && p.nodeType === 1) insSelect(sh, p, false);
                }
            });
        });
        var off = function () {
            m.style.display = 'none';
            document.removeEventListener('mousedown', off, true);
        };
        setTimeout(function () { document.addEventListener('mousedown', off, true); }, 0);
    }

    function applyBoot() {
        var b = null;
        try { b = window.__brainBoot; } catch (e) { }
        if (!b || b === bootApplied || b.b !== 9 || !Array.isArray(b.tabs)) return;
        // The injected new-document script also fires on internal pages
        // (NavigateToString) BEFORE the page's embedded payload exists — its
        // copy carries no `fresh` marker and would kill the new-tab slide-in.
        // On about: docs let the embedded payload (src:"emb") win.
        if (b.src === 'inj') {
            try { if (location.protocol === 'about:') return; } catch (e) { }
        }
        bootApplied = b;
        try { window.__brainBoot = null; } catch (e) { }
        bootFresh = b.fresh || null;
        state.tabs = b.tabs;
        state.loading = !!b.loading;
        markLoading();
        state.canBack = b.canBack !== false;
        state.canFwd = !!b.canFwd;
        state.hist = Array.isArray(b.hist) ? b.hist : [];
        state.bmarks = Array.isArray(b.bmarks) ? b.bmarks : [];
        state.bm = !!b.bm;
        state.zoom = b.zoom || 100;
        state.topmost = !!b.topmost;
        state.wc = !!b.wc;
        state.mtl = !!b.mtl;
        state.w = !!b.w;
        state.sess = b.sess || 0;
        state.privActive = !!b.privActive;
        if (b.se) state.se = b.se;
        if (Array.isArray(b.sel)) state.sel = b.sel;
        state.ov = lastPushOv = !!b.ov;
        window.__brainState = state;
        var s = sh0();
        if (s) {
            render(s);
            syncWin(s);
            syncNtp(s);
            updateStar(s);
            updateAddr(s);
            var om = s.getElementById('omni');
            if (om) om.classList.toggle('priv', !!state.privActive);
            // DevTools survives doc swaps — reopen on the tab the host remembers.
            if (b.dt && !dtOpen) toggleDt(s, b.dt, true);
        }
        // Pages (newtab tiles) render from this event — fire after listeners
        // have had a chance to attach during parse.
        setTimeout(function () {
            try { document.dispatchEvent(new CustomEvent('brain-state', { detail: state })); } catch (e) { }
        }, 0);
    }

    // ---------------- in-document new-tab page ----------------
    // Blank new tabs don't load a document (each load cold-starts a renderer
    // process, ~200ms of nothing then a jump). The host marks them virtual
    // (state.ov) and we paint the start page over the current document.

    // Keep the overlay up for `ms` regardless of pushes (optimistic open /
    // pending navigation), then fall back to the host's latest word.
    function holdOverlay(ms) {
        ovPending = Date.now() + ms;
        setTimeout(function () {
            if (ovPending && Date.now() >= ovPending) {
                ovPending = 0;
                state.ov = lastPushOv;
                var s = sh0(); if (s) { syncNtp(s); updateAddr(s); }
            }
        }, ms + 20);
    }

    // Ctrl+T / + / menu: show the start page in the same frame as the
    // keypress; the host's push (pill + ov:true) lands ~10ms later.
    function openNewTab(priv) {
        var s = sh0();
        if (s) {
            if (findOpen) closeFind(s);
            state.ov = true;
            state.privActive = !!priv;
            holdOverlay(700);
            syncNtp(s);
            updateAddr(s);
            updateStar(s);
            var om = s.getElementById('omni');
            if (om) om.classList.toggle('priv', !!priv);
        }
        send(priv ? 'newprivtab' : 'newtab');
    }

    var lastNtpMin = -1;
    function ntpTick(sh) {
        var d = new Date(), mm = d.getMinutes();
        if (mm === lastNtpMin) return;
        lastNtpMin = mm;
        var hh = d.getHours();
        var ck = sh.getElementById('ntpck'), dt = sh.getElementById('ntpdt');
        if (ck) ck.textContent = (hh < 10 ? '0' : '') + hh + ':' + (mm < 10 ? '0' : '') + mm;
        if (dt) dt.textContent = d.toLocaleDateString(undefined, { weekday: 'long', month: 'long', day: 'numeric' });
        var gr = sh.getElementById('ntpgr');
        if (gr) gr.textContent = hh < 5 ? 'Still up' : hh < 12 ? 'Good morning' : hh < 18 ? 'Good afternoon' : 'Good evening';
    }

    function renderNtpTiles(sh) {
        // One tile per site, one row — repeated hosts read as clutter and a
        // second row collided with the bottom hint.
        var picks = [], seenH = {};
        (state.hist || []).forEach(function (it) {
            var u = typeof it === 'string' ? it : it.u, h = hostOf(u);
            if (picks.length < 4 && h && !seenH[h]) { seenH[h] = 1; picks.push({ u: u, i: typeof it === 'object' ? it.i : null, h: h }); }
        });
        var sig = picks.map(function (p) { return p.u + '|' + (p.i || ''); }).join(';');
        if (sig === ntpHistSig) return;
        ntpHistSig = sig;
        var box = sh.getElementById('ntprc'), tl = sh.getElementById('ntptl');
        if (!box || !tl) return;
        var html = '';
        for (var i = 0; i < picks.length; i++) {
            var u = picks[i].u, icon = picks[i].i, h = picks[i].h;
            var lt = (h.charAt(0) || '?').toUpperCase();
            html += '<div class="ti" style="--tc:' + tabHue(h) + '" data-u="' + esc(u) + '" title="' + esc(u) + '">' +
                '<span class="ic">' +
                (icon ? '<img src="' + esc(icon) + '" alt="" data-l="' + esc(lt) +
                    '" onerror="this.parentNode.textContent=this.dataset.l">' : esc(lt)) +
                '</span><span>' + esc(h) + '</span></div>';
        }
        tl.innerHTML = html;
        box.classList.toggle('has', !!html);
    }

    function syncNtp(sh) {
        var n = sh.getElementById('ntp');
        if (!n) return;
        // The real start-page document themes itself from the same flag the
        // overlay uses: private tabs get the magenta aurora and the banner.
        if (window.__brainNewTab) {
            var pn = document.getElementById('ntp');
            if (pn) pn.classList.toggle('priv', !!state.privActive);
            try { document.documentElement.style.setProperty('--h', state.privActive ? 285 : 252); } catch (e) { }
        }
        // A real start-page document already shows this exact UI.
        var on = !!state.ov && !window.__brainNewTab;
        var was = n.classList.contains('on');
        n.classList.toggle('priv', !!state.privActive);
        if (on) renderNtpTiles(sh);
        if (on === was) return;
        n.classList.toggle('on', on);
        // Fixed overlays don't cover the viewport scrollbar — hide the hidden
        // page's (also blocks keyboard-scrolling it). Scroll offset survives.
        try {
            var de = document.documentElement;
            if (on) de.style.setProperty('overflow', 'hidden', 'important');
            else de.style.removeProperty('overflow');
        } catch (e) { }
        var q = sh.getElementById('ntpq');
        if (on) {
            ntpTick(sh);
            if (q) { q.value = ''; try { q.focus({ preventScroll: true }); } catch (e) { } }
        } else if (q) {
            try { q.blur(); } catch (e) { }
        }
    }

    function wireNtp(sh) {
        var n = sh.getElementById('ntp');
        var q = sh.getElementById('ntpq');
        sh.getElementById('ntpf').addEventListener('submit', function (e) {
            e.preventDefault();
            var v = q.value.trim();
            if (v) send('navigate:' + v);
        });
        sh.getElementById('ntpqa').addEventListener('click', function (e) {
            var c = e.target.closest && e.target.closest('.qc');
            if (!c) return;
            if (c.getAttribute('data-k')) openPal(sh);
            else send('navigate:' + c.getAttribute('data-go'));
        });
        sh.getElementById('ntptl').addEventListener('click', function (e) {
            var t = e.target.closest && e.target.closest('.ti');
            if (t) send('navigate:' + t.getAttribute('data-u'));
        });
        // The overlay sits on top of a live page — keystrokes/clicks/wheel
        // must not reach that page's handlers (e.g. YouTube 'k' = pause).
        // Our own shortcuts run in window capture phase, before this.
        ['keydown', 'keyup', 'keypress', 'mousedown', 'mouseup', 'click', 'dblclick',
            'input', 'paste', 'copy', 'cut', 'pointerdown', 'pointerup'].forEach(function (ev) {
            n.addEventListener(ev, function (e) { e.stopPropagation(); });
        });
        n.addEventListener('wheel', function (e) { e.preventDefault(); e.stopPropagation(); }, { passive: false });
        n.addEventListener('contextmenu', function (e) {
            e.stopPropagation();
            if (e.target !== q) e.preventDefault();
        });
    }

    // ---------------- mount ----------------

    function mount() {
        if (document.getElementById(HOST_ID)) return;
        if (!document.documentElement) { setTimeout(mount, 120); return; }
        // With boot state the bar existed before the doc swap — sliding it in
        // again reads as flicker, so skip the entrance animation.
        var hadBoot = false;
        try { hadBoot = !!window.__brainBoot; } catch (e) { }

        var host = document.createElement('div');
        host.id = HOST_ID;
        host.style.cssText = 'all:initial;position:fixed;top:0;left:0;right:0;height:46px;z-index:2147483647;--h:252;';
        var sh = host.attachShadow({ mode: 'open' });
        sh.innerHTML =
            '<style>' +
            ':host{all:initial;--ac:hsl(var(--h) 100% 78%);--ac2:hsl(calc(var(--h) - 72) 56% 62%);--mut:#8a8aa6;--dim:#5c5c78;--font:"Segoe UI Variable Text",system-ui,-apple-system,"Segoe UI",sans-serif}* {box-sizing:border-box;margin:0;padding:0}' +
            '.bar{position:fixed;top:0;left:0;right:0;height:46px;display:flex;align-items:center;gap:10px;z-index:10;' +
            'padding:0 12px;' +
            // near-opaque: a backdrop blur here was invisible yet re-rastered
            // on every document's first frame
            'isolation:isolate;' +
            'background:linear-gradient(180deg,rgba(21,21,33,.985),rgba(12,12,21,.985));' +
            'border-bottom:1px solid rgba(255,255,255,.05);' +
            'box-shadow:inset 0 1px 0 rgba(255,255,255,.05),0 10px 30px rgba(0,0,0,.38);' +
            'font:12px/1.45 "Segoe UI Variable Text",system-ui,-apple-system,"Segoe UI",sans-serif;color:#c9c9dc;' +
            'user-select:none;-webkit-user-select:none;' +
            'animation:slidein .24s cubic-bezier(.16,1,.3,1)}' +
            // macOS inline titlebar: the real traffic lights float at the
            // window's top-left (~8..72px) — pad the bar so they land in
            // empty space instead of on the nav buttons.
            '.bar.mtl{padding-left:78px}' +
            // Aurora: two soft site-tinted glows under the glass plus a
            // one-pixel light line on the lower edge. Static gradients only —
            // no blur, no animation, nothing to re-raster.
            '.bar::before{content:"";position:absolute;inset:0;z-index:-1;pointer-events:none;' +
            'background:radial-gradient(560px 96px at 24% -34px,hsl(var(--h) 100% 74% / .17),transparent 72%),' +
            'radial-gradient(440px 84px at 90% -34px,hsl(calc(var(--h) - 72) 60% 58% / .11),transparent 72%)}' +
            '.bar::after{content:"";position:absolute;left:0;right:0;bottom:-1px;height:1px;z-index:-1;pointer-events:none;' +
            'background:linear-gradient(90deg,transparent,hsl(var(--h) 100% 74% / .5) 26%,hsl(calc(var(--h) - 72) 60% 58% / .42) 74%,transparent)}' +
            '@keyframes slidein{from{transform:translateY(-8px);opacity:0}to{transform:none;opacity:1}}' +
            '@keyframes pop{from{transform:scale(.94) translateY(-4px);opacity:0}to{transform:none;opacity:1}}' +
            '#prog{position:absolute;left:0;right:0;bottom:-1px;height:2px;overflow:hidden;opacity:0;transition:opacity .35s}' +
            '#prog.on{opacity:1}' +
            '#prog i{position:absolute;top:0;bottom:0;width:38%;border-radius:2px;' +
            'background:linear-gradient(90deg,hsl(var(--h) 100% 74%),hsl(calc(var(--h) - 72) 62% 62%));' +
            'box-shadow:0 0 10px hsl(var(--h) 100% 74% / .6);animation:sweep 1.2s cubic-bezier(.45,0,.55,1) infinite}' +
            '@keyframes sweep{0%{left:-40%}55%{left:62%}100%{left:105%}}' +
            '.nav{display:flex;gap:1px;flex:none;padding:3px;border-radius:12px;' +
            'background:rgba(255,255,255,.04);border:1px solid rgba(255,255,255,.05)}' +
            'button.nav-b{width:28px;height:28px;display:grid;place-items:center;border:0;border-radius:9px;' +
            'background:transparent;color:#a5a5c2;cursor:pointer;transition:background .14s,color .14s,transform .1s}' +
            'button.nav-b:hover{background:rgba(255,255,255,.1);color:#fff}' +
            'button.nav-b:active{transform:scale(.9)}' +
            'button.nav-b svg{width:15px;height:15px}' +
            'button.nav-b.dis{opacity:.3;pointer-events:none}' +
            '#tabs{display:flex;align-items:center;gap:5px;flex:0 1 auto;min-width:0;overflow-x:auto;scrollbar-width:none}' +
            '#tabs::-webkit-scrollbar{display:none}' +
            '.tab{display:flex;align-items:center;gap:8px;height:34px;padding:0 8px 0 8px;min-width:46px;max-width:184px;' +
            'border-radius:10px;cursor:pointer;color:#8e8eac;border:1px solid transparent;' +
            'position:relative;overflow:hidden;' +
            'transition:background .2s,color .2s,border-color .2s,box-shadow .2s;' +
            'animation:tabin .22s cubic-bezier(.16,1,.3,1) backwards}' +
            '@keyframes tabin{from{transform:scale(.75);opacity:0;max-width:0;padding-left:0;padding-right:0}' +
            '60%{transform:scale(1.03)}to{transform:scale(1);opacity:1;max-width:184px}}' +
            // Boot-rendered tabs appear with the bar, not "in" — permanent
            // class (clearing inline styles would restart tabin). Closing
            // stays later in the sheet so tabout still wins.
            '.tab.noanim{animation:none}' +
            '.tab.closing{pointer-events:none;animation:tabout .19s cubic-bezier(.4,0,.7,1) forwards}' +
            '@keyframes tabout{to{transform:scale(.7);opacity:0;max-width:0;padding-left:0;padding-right:0;border-width:0}}' +
            '.tab.dropmark::before,.tab.dropmark-r::after{content:"";position:absolute;top:6px;bottom:6px;width:2.5px;' +
            'border-radius:2px;background:linear-gradient(180deg,hsl(var(--h) 100% 81%),#4ec9b0)}' +
            '.tab.dropmark::before{left:2px}.tab.dropmark-r::after{right:2px}' +
            '.tab:hover{background:rgba(255,255,255,.055);color:#d6d6ea}' +
            '.tab.active{background:linear-gradient(180deg,hsl(var(--h) 100% 74% / .15),hsl(var(--h) 100% 74% / .05));' +
            'color:#f2f2ff;border-color:hsl(var(--h) 100% 78% / .2);' +
            'box-shadow:inset 0 1px 0 rgba(255,255,255,.07),0 8px 20px -8px hsl(var(--h) 100% 70% / .5)}' +
            // the signature: a lit underline that carries the site's color
            '.tab.active:not(.dropmark-r)::after{content:"";position:absolute;left:12px;right:12px;bottom:0;height:2px;' +
            'border-radius:2px 2px 0 0;pointer-events:none;' +
            'background:linear-gradient(90deg,hsl(var(--h) 100% 78%),hsl(calc(var(--h) - 72) 62% 62%));' +
            'box-shadow:0 0 12px hsl(var(--h) 100% 72% / .85)}' +
            '.tab .dot{width:21px;height:21px;border-radius:7px;flex:none;display:grid;place-items:center;position:relative;' +
            'font-size:11px;font-weight:700;color:#fff;overflow:hidden;box-shadow:inset 0 1px 0 rgba(255,255,255,.15)}' +
            '.tab .dot .fav{position:absolute;inset:2.5px;width:16px;height:16px;border-radius:5px;' +
            'animation:iconin .18s ease both}' +
            '@keyframes iconin{from{opacity:0}to{opacity:1}}' +
            '.tab .dot.spin{background:transparent;box-shadow:none}' +
            '.tab .dot.spin::before{content:"";width:13px;height:13px;border-radius:50%;' +
            'border:2px solid hsl(var(--h) 100% 74% / .2);border-top-color:hsl(var(--h) 100% 81%);animation:rot .65s linear infinite}' +
            '@keyframes rot{to{transform:rotate(360deg)}}' +
            '.tab .t{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:1;min-width:0;font-weight:500;letter-spacing:.1px}' +
            '.tab .x{width:18px;height:18px;border-radius:999px;display:grid;place-items:center;color:#8a8aa4;flex:none;' +
            'opacity:0;transition:opacity .15s,background .12s,color .12s,transform .15s}' +
            '.tab .x svg{width:10px;height:10px}' +
            '.tab:hover .x{opacity:1}' +
            '.tab .x:hover{background:rgba(255,255,255,.16);color:#fff;transform:scale(1.15)}' +
            '.tab.dragging{opacity:.4}' +
            '.tab.priv{background:rgba(109,63,160,.18);color:#b8a3d9}' +
            '.tab.priv:hover{background:rgba(109,63,160,.28);color:#d9c4f5}' +
            '.tab.priv.active{background:linear-gradient(180deg,rgba(150,80,220,.32),rgba(109,63,160,.18));' +
            'color:#eadcff;border-color:rgba(178,132,255,.45);' +
            'box-shadow:0 2px 10px rgba(0,0,0,.3),inset 0 1px 0 rgba(255,255,255,.08),0 0 20px rgba(150,80,220,.18)}' +
            '.tab .dot.mask{color:#b8a3ff;box-shadow:none}' +
            '.tab .dot.mask svg{width:15px;height:15px}' +
            '.tab.pin{padding:0 9px;min-width:0;gap:0}' +
            '.tab.pin .t,.tab.pin .x{display:none}' +
            // Speaker badge — clicking it toggles mute without switching tabs
            '.tab .snd{display:none;width:17px;height:17px;border-radius:999px;flex:none;place-items:center;' +
            'color:#8a8aa4;transition:background .12s,color .12s}' +
            '.tab .snd.on{display:grid}' +
            '.tab .snd svg{width:12px;height:12px}' +
            '.tab .snd:hover{background:rgba(255,255,255,.16);color:#fff}' +
            '.tab.active .snd{color:hsl(var(--h) 100% 90%)}' +
            '.tab .snd.muted{color:#e8b45c}' +
            '.tab.pin .snd.on{position:absolute;right:1px;bottom:1px;width:13px;height:13px;' +
            'background:rgba(16,16,28,.94);color:#e6e6fa}' +
            '.tab.pin .snd.on.muted{color:#e8b45c}' +
            '.tab.pin .snd svg{width:9px;height:9px}' +
            '#tmenu,#pmenu{position:fixed;min-width:190px;display:none;z-index:30;' +
            'background:rgba(22,22,34,.96);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(255,255,255,.1);border-radius:13px;padding:5px;' +
            'transform-origin:top left;' +
            'box-shadow:0 12px 36px rgba(0,0,0,.55),inset 0 1px 0 rgba(255,255,255,.06);font:12px/1.4 system-ui,sans-serif;' +
            'animation:pop .16s cubic-bezier(.16,1,.3,1)}' +
            '.mi{padding:7px 12px;border-radius:8px;color:#c9c9dc;cursor:pointer;transition:background .1s,color .1s}' +
            '.mi:hover{background:hsl(var(--h) 100% 74% / .18);color:#fff}' +
            '.mi.ac{color:hsl(var(--h) 100% 83%);font-weight:600}' +
            '.mi.ac:hover{background:hsl(var(--h) 100% 70% / .3);color:#fff}' +
            '#pweye{position:fixed;display:none;place-items:center;padding:0;border:0;border-radius:50%;' +
            'background:transparent;color:#8a8fa6;cursor:pointer;z-index:29;' +
            'filter:drop-shadow(0 0 1.6px rgba(255,255,255,.85)) drop-shadow(0 0 4px rgba(255,255,255,.4));' +
            'transition:color .12s,transform .1s}' +
            '#pweye:hover{color:#565b74}' +
            '#pweye:active{transform:scale(.88)}' +
            '#pweye.on{color:hsl(var(--h) 100% 62%)}' +
            '#pweye svg{width:100%;height:100%}' +
            '#pweye .sl{display:none}' +
            '#pweye.on .sl{display:block}' +
            '#pwfill{position:fixed;display:none;z-index:30;min-width:200px;max-width:320px;max-height:260px;overflow:auto;' +
            'background:rgba(22,22,34,.96);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(255,255,255,.1);border-radius:13px;padding:5px;' +
            'transform-origin:top left;scrollbar-width:thin;' +
            'box-shadow:0 12px 36px rgba(0,0,0,.55),inset 0 1px 0 rgba(255,255,255,.06);font:12px/1.4 system-ui,sans-serif;' +
            'animation:pop .16s cubic-bezier(.16,1,.3,1)}' +
            '#pwfill .mi{display:flex;flex-direction:column;gap:1px;align-items:flex-start}' +
            '#pwfill .mi .u{color:#e6e6f8;font-weight:600;font-size:12.5px;display:flex;align-items:center;gap:7px}' +
            '#pwfill .mi .u svg{width:11px;height:11px;color:hsl(var(--h) 100% 78%);flex:none}' +
            '#pwfill .mi .h{color:#6a6a88;font-size:10.5px;font-family:var(--mono,monospace);letter-spacing:.5px}' +
            '#pwfill .mft{border-top:1px solid rgba(255,255,255,.07);margin-top:4px;padding-top:4px}' +
            '#pwfill .mft .u{color:#8a8aa4;font-weight:500}' +
            '#pwsave{position:fixed;top:48px;right:14px;display:none;z-index:31;align-items:center;gap:11px;' +
            'background:rgba(22,22,34,.96);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(255,255,255,.1);border-radius:14px;padding:10px 12px;max-width:400px;' +
            'box-shadow:0 14px 40px rgba(0,0,0,.55),inset 0 1px 0 rgba(255,255,255,.06);font:12.5px/1.4 system-ui,sans-serif;' +
            'transform-origin:top right;animation:pop .18s cubic-bezier(.16,1,.3,1)}' +
            '#pwsave .k{width:26px;height:26px;border-radius:8px;display:grid;place-items:center;flex:none;' +
            'background:hsl(var(--h) 100% 70% / .18);color:hsl(var(--h) 100% 84%)}' +
            '#pwsave .k svg{width:14px;height:14px}' +
            '#pwsave .tx{color:#c9c9dc;min-width:0}' +
            '#pwsave .tx b{color:#f2f2fc;font-weight:600;word-break:break-all}' +
            '#pwsave button{border:0;cursor:pointer;transition:all .12s;flex:none}' +
            '#pwsave .sv{border-radius:9px;padding:6px 13px;font:600 12px system-ui,sans-serif;' +
            'background:hsl(var(--h) 100% 70% / .85);color:#0c0c18}' +
            '#pwsave .sv:hover{background:hsl(var(--h) 100% 78%)}' +
            '#pwsave .nv{border-radius:9px;padding:6px 10px;font:500 12px system-ui,sans-serif;background:transparent;color:#8a8aa4}' +
            '#pwsave .nv:hover{color:#c9c9dc;background:rgba(255,255,255,.06)}' +
            '#pwsave .xx{border-radius:7px;padding:5px;background:transparent;color:#5c5c78;display:grid;place-items:center}' +
            '#pwsave .xx:hover{color:#b9b9d0}' +
            '#pwsave .xx svg{width:11px;height:11px}' +
            '.plus{width:30px;height:34px;flex:none;display:grid;place-items:center;border:0;border-radius:10px;' +
            'background:transparent;color:#8a8aa4;cursor:pointer;transition:background .14s,color .14s,transform .1s}' +
            '.plus:hover{background:hsl(var(--h) 100% 74% / .18);color:hsl(var(--h) 100% 90%)}' +
            '.plus:active{transform:scale(.9)}' +
            '.plus svg{width:14px;height:14px;transition:transform .3s cubic-bezier(.16,1,.3,1)}' +
            '.plus:hover svg{transform:rotate(90deg)}' +
            '.omni{flex:1;min-width:120px;display:flex;align-items:center;gap:8px;height:34px;padding:0 14px;' +
            'border-radius:12px;background:rgba(255,255,255,.05);border:1px solid rgba(255,255,255,.07);' +
            'box-shadow:inset 0 1px 2px rgba(0,0,0,.25);cursor:text;position:relative;' +
            'transition:background .15s,border-color .15s,box-shadow .2s}' +
            '.omni:hover{background:rgba(255,255,255,.08)}' +
            '.omni:focus-within{background:rgba(255,255,255,.09);border-color:hsl(var(--h) 100% 74% / .6);' +
            'box-shadow:0 0 0 3px hsl(var(--h) 100% 74% / .15),0 10px 30px -10px hsl(var(--h) 100% 70% / .55),inset 0 1px 2px rgba(0,0,0,.2)}' +
            '.omni.priv{background:rgba(109,63,160,.14);border-color:rgba(178,132,255,.3)}' +
            '.omni.priv #lock{color:#b8a3ff}' +
            '#lock{width:13px;height:13px;color:#6fd8a8;flex:none}' +
            '#lock.bad{color:#e8b45c}' +
            '#lock svg{width:100%;height:100%}' +
            '#addr{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#b9b9d0;outline:none;font-size:12.5px}' +
            '#addr b{color:#f2f2fc;font-weight:600}' +
            '#addr .dim{color:#6a6a84}' +
            // Editing field is a real <input>: WKWebView can't put a caret or
            // selection into a shadow-root contentEditable (window.getSelection
            // ignores the shadow tree), so typing/focus never worked there.
            '#addrIn{display:none;flex:1;min-width:0;border:0;outline:0;padding:0;background:transparent;' +
            'color:#f2f2fc;font:inherit;font-size:12.5px;caret-color:hsl(var(--h) 100% 81%);user-select:text;-webkit-user-select:text}' +
            '#addrIn::selection{background:hsl(var(--h) 100% 74% / .42)}' +
            '#drops{position:absolute;top:40px;left:6px;right:6px;display:none;z-index:5;' +
            'background:rgba(20,20,32,.97);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(255,255,255,.1);border-radius:13px;overflow:hidden;' +
            'transform-origin:top center;' +
            'box-shadow:0 12px 36px rgba(0,0,0,.55),inset 0 1px 0 rgba(255,255,255,.05);padding:5px;' +
            'animation:pop .16s cubic-bezier(.16,1,.3,1)}' +
            '.drop{display:flex;align-items:center;gap:10px;padding:7px 11px;border-radius:9px;color:#b9b9d0;cursor:pointer;' +
            'overflow:hidden;text-overflow:ellipsis;white-space:nowrap;transition:background .1s,color .1s}' +
            '.drop .fav{width:15px;height:15px;border-radius:4px;flex:none}' +
            '.drop .du{overflow:hidden;text-overflow:ellipsis;min-width:0}' +
            '.drop .dt2{color:#ececff;flex:none;max-width:42%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
            '.drop .dic{width:15px;height:15px;color:#8a8aa4;flex:none}.drop .dic svg{width:100%;height:100%}' +
            '.drop b{color:#f2f2fc;font-weight:600}' +
            '.drop.sel,.drop:hover{background:hsl(var(--h) 100% 74% / .18);color:#ececff}' +
            '#status{position:fixed;left:10px;bottom:10px;max-width:55%;padding:6px 14px;' +
            'background:rgba(22,22,34,.94);border:1px solid rgba(255,255,255,.1);border-radius:999px;' +
            'backdrop-filter:blur(14px);-webkit-backdrop-filter:blur(14px);' +
            'color:#9a9ab8;font:11.5px/1.4 system-ui,sans-serif;' +
            'overflow:hidden;text-overflow:ellipsis;white-space:nowrap;display:none;pointer-events:none;' +
            'box-shadow:0 6px 20px rgba(0,0,0,.45);' +
            'animation:statusin .18s cubic-bezier(.16,1,.3,1)}' +
            '@keyframes statusin{from{transform:translateY(8px);opacity:0}to{transform:none;opacity:1}}' +
            '#status.on{display:block}' +
            // ---- tool capsule (top right) ----
            '.tools{position:relative;flex:none;display:flex;align-items:center;gap:1px;padding:3px;height:36px;border-radius:13px;' +
            'background:rgba(255,255,255,.04);border:1px solid rgba(255,255,255,.06);box-shadow:inset 0 1px 0 rgba(255,255,255,.04)}' +
            '.hl{position:absolute;top:3px;bottom:3px;left:0;width:30px;border-radius:10px;pointer-events:none;opacity:0;' +
            'background:hsl(var(--h) 100% 74% / .15);box-shadow:inset 0 0 0 1px hsl(var(--h) 100% 78% / .24),0 6px 16px -6px hsl(var(--h) 100% 70% / .55);' +
            'transition:transform .26s cubic-bezier(.2,.9,.3,1.12),width .26s cubic-bezier(.2,.9,.3,1),opacity .16s}' +
            '.tools.hov .hl{opacity:1}' +
            '.tb{position:relative;z-index:1;flex:none;width:30px;height:28px;padding:0;border:0;border-radius:10px;display:grid;place-items:center;' +
            'background:transparent;color:#9a9ab8;cursor:pointer;transition:color .15s,transform .14s cubic-bezier(.3,1.5,.5,1)}' +
            '.tb:hover{color:#fff}.tb:active{transform:scale(.84)}' +
            '.tb svg{width:15px;height:15px;display:block}' +
            '#pip,#star{display:none}' +
            '.tsep{width:1px;height:16px;margin:0 3px;background:rgba(255,255,255,.08);flex:none}' +
            '.orb svg{color:hsl(var(--h) 100% 82%);filter:drop-shadow(0 0 5px hsl(var(--h) 100% 70% / .65));' +
            'transition:transform .55s cubic-bezier(.16,1,.3,1)}' +
            '.orb:hover svg{transform:rotate(90deg) scale(1.14)}' +
            '.bar.ld .orb svg{animation:orbspin 1s linear infinite}' +
            '@keyframes orbspin{to{transform:rotate(360deg)}}' +
            '#pip svg{width:16px;height:16px}' +
            '#pip.yes{color:hsl(var(--h) 100% 78%)}' +
            '#pip.yes::after{content:"";position:absolute;inset:2px;border-radius:9px;pointer-events:none;' +
            'box-shadow:0 0 0 0 hsl(var(--h) 100% 74% / .6);animation:ring 1.3s ease-out 2}' +
            '@keyframes ring{to{box-shadow:0 0 0 9px hsl(var(--h) 100% 74% / 0)}}' +
            '#pip.na{opacity:.38;cursor:default}' +
            '#star.yes{color:#f2c14e}' +
            '#star.yes svg{animation:starpop .5s cubic-bezier(.2,1.5,.4,1)}' +
            '@keyframes starpop{0%{transform:scale(.4) rotate(-40deg)}60%{transform:scale(1.35) rotate(8deg)}100%{transform:none}}' +
            // pointer-following tooltip with the shortcut chip
            '#ttip{position:fixed;top:54px;left:0;display:flex;align-items:center;gap:8px;padding:6px 9px 6px 11px;border-radius:10px;z-index:50;' +
            'background:rgba(20,20,32,.98);border:1px solid hsl(var(--h) 100% 78% / .2);pointer-events:none;white-space:nowrap;' +
            'box-shadow:0 12px 30px rgba(0,0,0,.5),inset 0 1px 0 rgba(255,255,255,.06);' +
            'font:600 11.5px/1.2 "Segoe UI Variable Text",system-ui,sans-serif;color:#e4e4f4;' +
            'opacity:0;transform:translateY(-5px) scale(.96);transform-origin:top center;transition:opacity .12s,transform .16s cubic-bezier(.16,1,.3,1)}' +
            '#ttip.on{opacity:1;transform:none}' +
            '#ttip kbd{font:600 10px/16px system-ui,sans-serif;padding:0 6px;border-radius:5px;color:hsl(var(--h) 100% 86%);' +
            'background:hsl(var(--h) 100% 74% / .15);border:1px solid hsl(var(--h) 100% 78% / .22)}' +
            // motion: site-switch glow, underline grow, staggered lists
            '.bar.sw1::before{animation:swa .7s ease-out}.bar.sw2::before{animation:swb .7s ease-out}' +
            '@keyframes swa{0%{opacity:.25;transform:scale(1.06)}100%{opacity:1;transform:none}}' +
            '@keyframes swb{0%{opacity:.25;transform:scale(1.06)}100%{opacity:1;transform:none}}' +
            '.tab.active:not(.dropmark-r)::after{animation:uline .38s cubic-bezier(.16,1,.3,1)}' +
            '@keyframes uline{from{transform:scaleX(.15);opacity:0}to{transform:none;opacity:1}}' +
            '@keyframes rowin{from{opacity:0;transform:translateY(7px)}to{opacity:1;transform:none}}' +
            '#menu.on .mi{animation:rowin .22s cubic-bezier(.16,1,.3,1) both;animation-delay:calc(var(--i,0) * 14ms)}' +
            '#pres.fresh .pr{animation:rowin .24s cubic-bezier(.16,1,.3,1) both;animation-delay:calc(var(--i,0) * 20ms)}' +
            '@media (prefers-reduced-motion:reduce){*,*::before,*::after{animation-duration:.01ms!important;animation-iteration-count:1!important;transition-duration:.01ms!important}}' +
            '.bmstar{width:12px;height:12px;color:#f2c14e;flex:none}' +
            '.bmstar svg{width:100%;height:100%}' +
            '#menu{position:fixed;top:44px;right:8px;min-width:236px;display:none;z-index:31;' +
            'background:rgba(22,22,34,.97);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(255,255,255,.1);border-radius:13px;padding:5px;' +
            'transform-origin:top right;' +
            'box-shadow:0 12px 36px rgba(0,0,0,.55),inset 0 1px 0 rgba(255,255,255,.06);font:12px/1.4 system-ui,sans-serif;' +
            'animation:pop .16s cubic-bezier(.16,1,.3,1)}' +
            '#menu.on{display:block}' +
            // ---- command palette ----
            '#pal{position:fixed;inset:0;display:none;z-index:40;background:rgba(4,4,10,.5)}' +
            '#pal.on{display:block;animation:palfade .12s ease-out}' +
            '@keyframes palfade{from{opacity:0}to{opacity:1}}' +
            '.pbox{position:absolute;top:84px;left:50%;margin-left:max(-320px,-46vw);width:min(640px,92vw);border-radius:18px;overflow:hidden;' +
            'background:linear-gradient(180deg,rgba(24,24,38,.99),rgba(15,15,26,.99));' +
            'border:1px solid hsl(var(--h) 100% 78% / .22);' +
            'box-shadow:0 34px 90px rgba(0,0,0,.65),0 24px 90px -24px hsl(var(--h) 100% 70% / .5),inset 0 1px 0 rgba(255,255,255,.07);' +
            'font:13px/1.4 "Segoe UI Variable Text",system-ui,-apple-system,sans-serif;color:#d6d6ea;' +
            'transform-origin:top center;animation:pop .16s cubic-bezier(.16,1,.3,1)}' +
            '.pin{display:flex;align-items:center;gap:12px;padding:0 18px;height:56px;border-bottom:1px solid rgba(255,255,255,.07);color:hsl(var(--h) 100% 81%)}' +
            '.pin>svg{width:17px;height:17px;flex:none}' +
            '#pq{flex:1;min-width:0;border:0;outline:0;background:transparent;color:#f2f2fc;font:500 15.5px "Segoe UI Variable Text",system-ui,sans-serif;' +
            'caret-color:hsl(var(--h) 100% 78%);user-select:text;-webkit-user-select:text}' +
            '#pq::placeholder{color:#5f5f7c;font-weight:400}' +
            '.pesc{flex:none;padding:1px 7px;border-radius:6px;font:600 10px/17px system-ui,sans-serif;color:#6c6c8a;background:rgba(255,255,255,.05);border:1px solid rgba(255,255,255,.07)}' +
            '#pres{padding:6px;max-height:min(420px,56vh);overflow-y:auto}' +
            '#pres::-webkit-scrollbar{width:8px}#pres::-webkit-scrollbar-thumb{background:rgba(255,255,255,.1);border-radius:4px}' +
            '.pr{display:flex;align-items:center;gap:12px;padding:8px 12px;border-radius:11px;cursor:pointer;position:relative}' +
            '.pr.sel{background:hsl(var(--h) 100% 74% / .14)}' +
            '.pr.sel::before{content:"";position:absolute;left:0;top:9px;bottom:9px;width:3px;border-radius:3px;' +
            'background:linear-gradient(180deg,hsl(var(--h) 100% 80%),hsl(calc(var(--h) - 72) 62% 62%))}' +
            '.pi{width:26px;height:26px;border-radius:8px;flex:none;display:grid;place-items:center;position:relative;overflow:hidden;' +
            'font-size:12px;font-weight:700;color:#fff;box-shadow:inset 0 1px 0 rgba(255,255,255,.16)}' +
            '.pi img{position:absolute;inset:4px;width:18px;height:18px;border-radius:5px}' +
            '.pi.go,.pi.act{background:hsl(var(--h) 100% 74% / .16);color:hsl(var(--h) 100% 84%);box-shadow:none}' +
            '.pi svg{width:14px;height:14px}' +
            '.pt{flex:0 1 auto;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#ececfa;font-weight:500}' +
            '.ps{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#6c6c8a;font-size:12px}' +
            '.pr .pt+.pk{margin-left:auto}' +
            '.pk{flex:none;margin-left:auto;font-size:10.5px;font-weight:600;letter-spacing:.3px;color:#6c6c8a}' +
            '.pr.sel .pk{color:hsl(var(--h) 100% 84%)}' +
            '.pnone{padding:26px;text-align:center;color:#5f5f7c}' +
            '.pft{display:flex;gap:18px;padding:9px 18px;border-top:1px solid rgba(255,255,255,.06);font-size:11px;color:#5f5f7c}' +
            '.pft b{color:#8b8baa;font-weight:600;margin-right:4px}' +
            '#menu .mi{display:flex;justify-content:space-between;align-items:center;gap:22px}' +
            '#menu .sc{color:#63637e;font-size:11px;flex:none}' +
            // ---- search-engine flyout (⋮ → Search engine) ----
            '#smenu{position:fixed;min-width:196px;display:none;z-index:32;padding:5px;' +
            'background:rgba(22,22,34,.97);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(255,255,255,.1);border-radius:13px;transform-origin:top right;' +
            'box-shadow:0 12px 36px rgba(0,0,0,.55),inset 0 1px 0 rgba(255,255,255,.06);font:12px/1.4 system-ui,sans-serif;' +
            'animation:pop .16s cubic-bezier(.16,1,.3,1)}' +
            '#smenu.on{display:block}' +
            '#smenu.on .smi{animation:rowin .22s cubic-bezier(.16,1,.3,1) both;animation-delay:calc(var(--i,0) * 14ms)}' +
            '.smi{display:flex;align-items:center;gap:9px;padding:7px 10px 7px 8px;border-radius:8px;color:#c9c9dc;cursor:pointer;transition:background .1s,color .1s}' +
            '.smi:hover{background:hsl(var(--h) 100% 74% / .18);color:#fff}' +
            '.smi .sk{width:15px;height:15px;flex:none;display:grid;place-items:center;color:hsl(var(--h) 100% 80%)}' +
            '.smi .sk svg{width:13px;height:13px}' +
            '.smi.cur{color:#ececfa;font-weight:600}' +
            '.msep{height:1px;margin:5px 10px;background:rgba(255,255,255,.07)}' +
            // ---- downloads: capsule button ring + shelf popover ----
            '#dlbtn{display:none}' +
            '.drg{position:absolute;inset:0;width:100%!important;height:100%!important;display:none;' +
            'transform:rotate(-90deg);pointer-events:none}' +
            '.drg circle{fill:none;stroke:hsl(var(--h) 100% 76%);stroke-width:2.4;stroke-linecap:round;' +
            'transition:stroke-dashoffset .3s ease}' +
            '#dlbtn.und .drg circle{stroke-dasharray:16 34.3;animation:dlspin 1.1s linear infinite}' +
            '#dlbtn.und .drg{animation:none}' +
            '@keyframes dlspin{to{stroke-dashoffset:-50.265}}' +
            '#dlbtn.run{color:hsl(var(--h) 100% 82%)}' +
            '#dlbtn.dldone{animation:dlring 1.1s ease-out}' +
            '@keyframes dlring{0%{box-shadow:0 0 0 0 hsl(var(--h) 100% 74% / .7)}100%{box-shadow:0 0 0 12px hsl(var(--h) 100% 74% / 0)}}' +
            '.ddot{position:absolute;right:3px;bottom:3px;width:6px;height:6px;border-radius:50%;display:none;' +
            'background:hsl(var(--h) 100% 76%);box-shadow:0 0 6px hsl(var(--h) 100% 70% / .9)}' +
            '#dlp{position:fixed;top:44px;right:8px;width:320px;max-height:400px;display:none;z-index:31;' +
            'background:rgba(22,22,34,.97);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(255,255,255,.09);border-radius:14px;overflow:hidden auto;' +
            'transform-origin:top right;' +
            'box-shadow:0 16px 44px rgba(0,0,0,.55),0 0 0 1px hsl(var(--h) 100% 76% / .06),' +
            '0 -1px 0 hsl(var(--h) 100% 76% / .22),inset 0 1px 0 rgba(255,255,255,.05)}' +
            '#dlp.on{display:block;animation:dlopen .22s cubic-bezier(.16,1,.3,1)}' +
            '@keyframes dlopen{from{opacity:0;transform:scale(.9) translateY(-10px)}to{opacity:1;transform:none}}' +
            '#dlp.bye{pointer-events:none;animation:dlbye .16s cubic-bezier(.5,0,.8,.4) forwards}' +
            '@keyframes dlbye{from{opacity:1;transform:none}to{opacity:0;transform:scale(.93) translateY(-8px)}}' +
            '#dlp::-webkit-scrollbar{width:8px}#dlp::-webkit-scrollbar-thumb{background:rgba(255,255,255,.08);border-radius:4px}' +
            '.dph{display:flex;align-items:center;padding:11px 14px 9px;position:sticky;top:0;z-index:2;' +
            'background:rgba(22,22,34,.97);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border-bottom:1px solid rgba(255,255,255,.06)}' +
            '.dpt{font:600 12px/1.2 system-ui,sans-serif;letter-spacing:.4px;color:#ececff}' +
            '#dlclr[disabled]{opacity:.4;pointer-events:none}' +
            '.dpempty{display:flex;flex-direction:column;align-items:center;gap:10px;padding:30px;color:#5a5a76;' +
            'font:12px/1.4 system-ui,sans-serif}' +
            '.dpempty span{opacity:.5}.dpempty svg{width:22px;height:22px}' +
            '.dli{display:flex;align-items:center;gap:11px;padding:8px 12px;cursor:default;overflow:hidden;' +
            'transition:background .12s,height .2s cubic-bezier(.16,1,.3,1),padding .2s cubic-bezier(.16,1,.3,1),' +
            'margin .2s cubic-bezier(.16,1,.3,1),opacity .16s ease,transform .2s cubic-bezier(.16,1,.3,1);' +
            'border-radius:10px;margin:2px 6px}' +
            // Entrance plays once via .new — keeping it off .dli means state
            // flips (run→ok) and reorders can't retrigger it.
            '.dli.new{animation:rowin .24s cubic-bezier(.16,1,.3,1) both;animation-delay:calc(var(--i,0) * 16ms)}' +
            '.dli.out{pointer-events:none;padding-top:0;padding-bottom:0;margin-top:0;margin-bottom:0;' +
            'opacity:0;transform:translateX(16px)}' +
            '.dli.ok{cursor:pointer}' +
            '.dli:hover{background:hsl(var(--h) 100% 74% / .1)}' +
            '.dic{flex:none;width:30px;height:30px;border-radius:9px;display:grid;place-items:center;color:#9a9ab8;' +
            'background:rgba(255,255,255,.05);border:1px solid rgba(255,255,255,.06)}' +
            '.dli.run .dic{color:hsl(var(--h) 100% 80%);background:hsl(var(--h) 100% 70% / .12);' +
            'border-color:hsl(var(--h) 100% 74% / .24)}' +
            '.dli.ok .dic{color:#7ee0b0}' +
            '.dli.err .dic{color:#ff8f97}' +
            '.dic svg{width:14px;height:14px}' +
            '.db{flex:1;min-width:0;display:flex;flex-direction:column;gap:2px}' +
            '.db b{font:600 12px/1.25 system-ui,sans-serif;color:#ececff;overflow:hidden;text-overflow:ellipsis;' +
            'white-space:nowrap;font-style:normal}' +
            '.db i{font:11px/1.3 system-ui,sans-serif;color:#8a8aa4;font-style:normal;overflow:hidden;' +
            'text-overflow:ellipsis;white-space:nowrap}' +
            '.dli.err .db i{color:#e89aa0}' +
            '.db u{display:block;height:3px;border-radius:2px;background:rgba(255,255,255,.07);margin-top:3px;' +
            'overflow:hidden;position:relative}' +
            '.db u e{display:block;height:100%;border-radius:2px;background:linear-gradient(90deg,' +
            'hsl(var(--h) 100% 66%),hsl(calc(var(--h) + 40) 100% 72%));transition:width .3s ease}' +
            '.db u e.ind{width:34%!important;animation:dlind 1.2s ease-in-out infinite}' +
            '@keyframes dlind{0%{margin-left:-34%}100%{margin-left:100%}}' +
            '.dab{flex:none;width:24px;height:24px;border-radius:7px;display:grid;place-items:center;color:#7a7a98;' +
            'cursor:pointer;opacity:0;transition:opacity .12s,background .12s,color .12s}' +
            '.dli:hover .dab{opacity:1}' +
            '.dab:hover{background:hsl(var(--h) 100% 74% / .2);color:#fff}' +
            '.dab svg{width:12px;height:12px}' +
            '#fbar{position:fixed;top:52px;right:12px;display:none;align-items:center;gap:7px;z-index:29;' +
            'padding:6px 8px 6px 12px;border-radius:12px;' +
            'background:rgba(22,22,34,.97);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(255,255,255,.1);' +
            'box-shadow:0 12px 36px rgba(0,0,0,.55),inset 0 1px 0 rgba(255,255,255,.06);' +
            'color:#a5a5c2;font:12px/1.4 system-ui,sans-serif;' +
            'transform-origin:top right;' +
            'animation:pop .16s cubic-bezier(.16,1,.3,1)}' +
            '#fbar.on{display:flex}' +
            '#fbar>svg{width:13px;height:13px;flex:none}' +
            '#fin{width:190px;background:transparent;border:0;outline:0;color:#ececff;' +
            'font:12.5px/1.4 system-ui,sans-serif;padding:4px 2px}' +
            '#fin::placeholder{color:#63637e}' +
            '#fcnt{font-size:11px;color:#8a8aa4;white-space:nowrap;min-width:38px;text-align:right}' +
            '#fcnt.none{color:#e8736f}' +
            '.fb{width:22px;height:22px;display:grid;place-items:center;border:0;border-radius:6px;' +
            'background:transparent;color:#a5a5c2;cursor:pointer;transition:background .12s,color .12s}' +
            '.fb:hover{background:rgba(255,255,255,.1);color:#fff}' +
            '.fb svg{width:11px;height:11px}' +
            // ---- devtools panel ----
            '#dt{position:fixed;left:0;right:0;bottom:0;height:340px;display:none;flex-direction:column;z-index:20;' +
            'background:linear-gradient(180deg,rgba(18,18,28,.995),rgba(12,12,20,.995));' +
            'border-top:1px solid hsl(var(--h) 100% 70% / .35);' +
            'box-shadow:0 -14px 44px rgba(0,0,0,.55),0 -1px 22px -4px hsl(var(--h) 95% 62% / .3),' +
            'inset 0 14px 26px -22px hsl(var(--h) 100% 70% / .4);' +
            'font:12px/1.45 "Segoe UI Variable Text",system-ui,sans-serif;color:#c9c9dc;' +
            'user-select:text;-webkit-user-select:text;animation:dtin .18s cubic-bezier(.16,1,.3,1)}' +
            '@keyframes dtin{from{transform:translateY(18px);opacity:0}to{transform:none;opacity:1}}' +
            '#dtgrip{height:5px;margin-top:-3px;cursor:ns-resize;flex:none;position:relative;z-index:2}' +
            '.dthead{display:flex;align-items:center;gap:2px;padding:0 10px;height:35px;flex:none;' +
            'border-bottom:1px solid rgba(255,255,255,.06)}' +
            '.dtab{padding:6px 12px;border:0;border-radius:8px 8px 0 0;background:transparent;color:#8f8fb0;' +
            'font:600 11.5px/1 "Segoe UI Variable Text",system-ui,sans-serif;cursor:pointer;transition:color .12s}' +
            '.dtab:hover{color:#d8d8ec}' +
            '.dtab.on{color:#ececff;background:hsl(var(--h) 100% 74% / .13);box-shadow:inset 0 -2px 0 hsl(var(--h) 100% 74%)}' +
            '.dtsp{flex:1}' +
            '.dtbtn{padding:4px 10px;border:0;border-radius:7px;background:transparent;color:#8f8fb0;font-size:11px;cursor:pointer}' +
            '.dtbtn:hover{background:rgba(255,255,255,.09);color:#fff}' +
            '.dtbtn.armed{color:hsl(var(--h) 100% 86%);background:hsl(var(--h) 100% 74% / .18)}' +
            '#dtbody{flex:1;display:flex;min-height:0}' +
            '.dtpane{flex:1;min-width:0;display:none;flex-direction:column;min-height:0}' +
            '.dtpane.on{display:flex}' +
            '.dtscroll{flex:1;overflow-y:auto;font:11.5px/1.55 Consolas,"Cascadia Mono","Courier New",monospace}' +
            '.dtscroll::-webkit-scrollbar{width:9px}' +
            '.dtscroll::-webkit-scrollbar-thumb{background:rgba(255,255,255,.12);border-radius:5px}' +
            '.crow{padding:1.5px 14px;border-bottom:1px solid rgba(255,255,255,.035);white-space:pre-wrap;word-break:break-word;display:flex;gap:10px}' +
            '.crow .ct{color:#55556f;flex:none}' +
            '.crow.err{background:rgba(232,17,35,.09);color:#ffb4b9}' +
            '.crow.warn{color:#ffd08a}' +
            '.crow.in{color:#8fe0cd}.crow.in .ct{color:#3d7a6b}' +
            '.crow.out .ct{color:hsl(var(--h) 100% 74%)}' +
            '.dneth,.dnetr{display:grid;grid-template-columns:52px 62px 78px 74px 1fr;gap:10px;padding:2px 14px}' +
            '.dneth{color:#66667f;font-size:10.5px;letter-spacing:.6px;text-transform:uppercase;border-bottom:1px solid rgba(255,255,255,.08);position:sticky;top:0;background:#101018;flex:none}' +
            '.dnetr{border-bottom:1px solid rgba(255,255,255,.03);font:11.5px/1.55 Consolas,"Cascadia Mono",monospace}' +
            '.dnetr .u{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#a8a8c8;direction:rtl;text-align:left}' +
            '.dnetr .s2{color:#7ee0a3}.dnetr .s3{color:#7cc4ff}.dnetr .s4{color:#ffb86b}.dnetr .s5,.dnetr .s0{color:#ff8a94}' +
            '.dnetr{cursor:pointer}.dnetr:hover{background:rgba(255,255,255,.04)}' +
            '.dnetr.sel{background:hsl(var(--h) 100% 74% / .1)}' +
            '.dnettools{display:flex;align-items:center;gap:8px;padding:5px 10px;flex:none;' +
            'border-bottom:1px solid rgba(255,255,255,.07)}' +
            '.fpills{display:flex;gap:3px;flex:none;flex-wrap:wrap}' +
            '.fpill{padding:3px 9px;border:1px solid rgba(255,255,255,.09);border-radius:999px;' +
            'background:transparent;color:#8f8fb0;font-size:10.5px;cursor:pointer;white-space:nowrap;transition:all .12s}' +
            '.fpill:hover{color:#d8d8ec;border-color:rgba(255,255,255,.22)}' +
            '.fpill.on{background:hsl(var(--h) 100% 74% / .16);border-color:hsl(var(--h) 100% 74% / .45);color:hsl(var(--h) 100% 90%)}' +
            '#dtfq{flex:1;max-width:230px;margin-left:auto;border:1px solid rgba(255,255,255,.1);border-radius:7px;' +
            'background:rgba(255,255,255,.04);color:#e8e8f4;padding:4px 9px;font-size:11.5px;outline:0}' +
            '#dtfq:focus{border-color:hsl(var(--h) 100% 74% / .5)}' +
            '#dtdetail,#dtedit{flex:1;min-height:0;flex-direction:column;overflow:hidden}' +
            '.dttools{display:flex;align-items:center;gap:8px;padding:6px 14px;flex:none;' +
            'border-bottom:1px solid rgba(255,255,255,.07)}' +
            '.dtbscroll{flex:1;overflow-y:auto;padding:4px 14px 12px;font:11.5px/1.6 Consolas,"Cascadia Mono",monospace;min-height:0}' +
            '.dtbscroll::-webkit-scrollbar{width:9px}' +
            '.dtbscroll::-webkit-scrollbar-thumb{background:rgba(255,255,255,.12);border-radius:5px}' +
            '.dtsec{margin:10px 0 2px;color:hsl(var(--h) 100% 74%);font-weight:700;font-size:10.5px;letter-spacing:.7px;text-transform:uppercase}' +
            '.dtkv{display:flex;gap:10px;padding:1.5px 0;border-bottom:1px solid rgba(255,255,255,.03)}' +
            '.dtkv b{color:#7a7a98;font-weight:400;min-width:160px;flex:none;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
            '.dtkv span{color:#c9c9dc;word-break:break-all;min-width:0;user-select:text}' +
            '.dtedit-in{border:1px solid rgba(255,255,255,.12);border-radius:7px;background:rgba(255,255,255,.05);' +
            'color:#e8e8f4;padding:5px 9px;font:11.5px/1.4 Consolas,"Cascadia Mono",monospace;outline:0}' +
            '.dtedit-in:focus{border-color:hsl(var(--h) 100% 74% / .55)}' +
            '.dte-row{display:flex;gap:6px;padding:8px 14px;align-items:center;flex:none}' +
            '#dtem{width:92px;flex:none}' +
            '.dthr{display:flex;gap:6px;padding:2px 14px;align-items:center}' +
            '.dthr input{flex:1;min-width:0}' +
            '.dthr .hx{flex:none;width:20px;height:20px;padding:0;display:grid;place-items:center;border:0;border-radius:5px;' +
            'background:transparent;color:#77779a;cursor:pointer;font-size:12px}' +
            '.dthr .hx:hover{background:rgba(232,17,35,.2);color:#ffb4b9}' +
            '#dteb{margin:2px 14px;min-height:56px;max-height:140px;resize:vertical}' +
            '.dtsend{padding:5px 16px;border:0;border-radius:8px;flex:none;' +
            'background:linear-gradient(135deg,hsl(var(--h) 100% 74%),hsl(var(--h) 60% 58%));color:#fff;font-weight:600;cursor:pointer;transition:filter .12s}' +
            '.dtsend:hover{filter:brightness(1.15)}' +
            '.dtsend:disabled{opacity:.45;cursor:default}' +
            '#dteresp{margin:2px 14px 10px;border-top:1px solid rgba(255,255,255,.08);padding-top:8px;' +
            'font:11.5px/1.55 Consolas,"Cascadia Mono",monospace;overflow:auto;min-height:0}' +
            '#dteresp pre{white-space:pre-wrap;word-break:break-all;margin:4px 0;color:#a8e0c8;user-select:text}' +
            '#dteresp .rdet{color:#8f8fb0}' +
            '.resp-ok{color:#7ee0a3}.resp-err{color:#ff8a94}' +
            '.cklbl{display:flex;align-items:center;gap:5px;color:#8f8fb0;font-size:11px;cursor:pointer;white-space:nowrap}' +
            '.cklbl input{accent-color:hsl(var(--h) 100% 74%);margin:0}' +
            '.ckh,.ckr{display:grid;grid-template-columns:minmax(90px,1.1fr) minmax(120px,1.6fr) ' +
            'minmax(90px,1fr) minmax(50px,.6fr) 130px 74px 24px;gap:8px;align-items:center;padding:3px 12px}' +
            '.ckh{position:sticky;top:0;background:#111320;color:#55556f;font-size:10px;letter-spacing:.6px;' +
            'text-transform:uppercase;border-bottom:1px solid rgba(255,255,255,.07);flex:none;z-index:1}' +
            '.ckr{font:11.5px/1.5 Consolas,"Cascadia Mono",monospace;color:#c9c9dc;cursor:pointer}' +
            '.ckr:hover{background:rgba(255,255,255,.04)}' +
            '.ckr span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;min-width:0}' +
            '.ckr .cv{color:#8f8fb0}' +
            '.ckb{display:inline-block;padding:0 5px;margin-right:3px;border-radius:4px;font-size:9px;' +
            'letter-spacing:.4px;line-height:15px}' +
            '.ckb.ho{background:rgba(255,184,107,.15);color:#ffb86b}' +
            '.ckb.sc{background:rgba(126,224,163,.14);color:#7ee0a3}' +
            '.ckx{width:18px;height:18px;padding:0;display:grid;place-items:center;border:0;border-radius:4px;' +
            'background:transparent;color:#77779a;cursor:pointer;font-size:11px}' +
            '.ckx:hover{background:rgba(232,17,35,.2);color:#ffb4b9}' +
            '#dtcedit{flex:none;border-top:1px solid rgba(255,255,255,.08);padding:8px 14px;' +
            'display:none;flex-direction:column;gap:6px}' +
            '#dtcedit .cer{display:flex;gap:6px;align-items:center}' +
            '#dtcedit .dtedit-in{flex:1;min-width:0}' +
            '#dtcedit .cflags{display:flex;gap:12px;color:#8f8fb0;font-size:11px;align-items:center}' +
            '#dtcedit .cflags label{display:flex;gap:4px;align-items:center;cursor:pointer}' +
            '#dtcedit .cflags input{accent-color:hsl(var(--h) 100% 74%);margin:0}' +
            '.ssec{margin:10px 0 4px;color:hsl(var(--h) 100% 74%);font-weight:700;font-size:10.5px;letter-spacing:.7px;' +
            'text-transform:uppercase;display:flex;align-items:center;gap:8px}' +
            '.ssec .cnt{color:#55556f;font-weight:400;letter-spacing:0}' +
            '.srow{display:flex;align-items:center;gap:9px;padding:5px 12px;border-radius:8px;cursor:pointer;min-width:0}' +
            '.srow:hover{background:rgba(255,255,255,.05)}' +
            '.srow img{width:14px;height:14px;border-radius:3px;flex:none}' +
            '.srow .st{color:#d8d8ec;font-size:11.5px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;flex:none;max-width:38%}' +
            '.srow .su{color:#66667f;font-size:11px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;flex:1;min-width:0}' +
            '.sbadge{flex:none;font-size:9.5px;padding:0 6px;border-radius:4px;line-height:16px;letter-spacing:.4px}' +
            '.sbadge.priv{background:hsl(var(--h) 100% 74% / .16);color:hsl(var(--h) 100% 86%)}' +
            '.sbadge.pin{background:rgba(255,255,255,.08);color:#8f8fb0}' +
            '.sbadge.cur{background:rgba(126,224,163,.14);color:#7ee0a3}' +
            '.sbtn{flex:none;padding:3px 10px;border:1px solid rgba(255,255,255,.12);border-radius:6px;' +
            'background:transparent;color:hsl(var(--h) 100% 86%);font-size:10.5px;cursor:pointer}' +
            '.sbtn:hover{background:hsl(var(--h) 100% 74% / .12);border-color:hsl(var(--h) 100% 74% / .4)}' +
            '.sempty{padding:14px;color:#55556f;font-size:11.5px}' +
            '.dtrepl{display:flex;align-items:center;gap:9px;border-top:1px solid rgba(255,255,255,.08);padding:0 14px;flex:none;height:32px}' +
            '.dtrepl input{flex:1;border:0;outline:0;background:transparent;color:#e8e8f4;font:12px/1 Consolas,"Cascadia Mono",monospace}' +
            '.dtrepl .pr{color:hsl(var(--h) 100% 74%);font-weight:700}' +
            '.ins-top{display:flex;align-items:center;gap:10px;padding:6px 14px;border-bottom:1px solid rgba(255,255,255,.07);flex:none}' +
            '#inssplit{flex:1;display:flex;min-height:0}' +
            '#instree{flex:1.2;min-width:0;overflow:auto;border-right:1px solid rgba(255,255,255,.07);padding:4px 0;outline:0;' +
            'font:11.5px/1.75 Consolas,"Cascadia Mono","Courier New",monospace;user-select:none}' +
            '#instree::-webkit-scrollbar{width:9px;height:9px}' +
            '#instree::-webkit-scrollbar-thumb{background:rgba(255,255,255,.12);border-radius:5px}' +
            '.tn{min-width:0}' +
            '.th{display:flex;align-items:center;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;cursor:default;padding-right:8px;min-height:19px}' +
            '.th:hover{background:rgba(255,255,255,.05)}' +
            '.tn.sel>.th{background:hsl(var(--h) 100% 74% / .16);box-shadow:inset 2px 0 0 hsl(var(--h) 100% 72%);' +
            'text-shadow:0 0 14px hsl(var(--h) 100% 78% / .45)}' +
            '.tn.txt>.th{color:#7f7f9c;font-style:italic}' +
            '.tx{flex:none;width:15px;text-align:center;color:#7f7fa2;cursor:pointer;font-size:8px;user-select:none;' +
            'transition:transform .12s,color .12s}' +
            '.tx:hover{color:#fff;transform:scale(1.3)}' +
            '.tn.open>.th .tx{color:hsl(var(--h) 100% 80%)}' +
            '.tdim{flex:none;margin-left:9px;color:#585870;font-size:9.5px;display:none;letter-spacing:.2px}' +
            '.th:hover .tdim{display:inline}' +
            '.tch{margin-left:11px;padding-left:3px;border-left:1.5px solid rgba(255,255,255,.07);display:none}' +
            '.tl{color:#8282a6}' +
            '.tn b{color:hsl(var(--h) 100% 79%);font-weight:600}' +
            '.ta{color:#9d9dcc}' +
            '.tv{color:#8fe0cd}' +
            '.tq{color:#8a8aa6;font-style:italic}' +
            '#insdet{width:min(400px,42%);flex:none;display:flex;flex-direction:column;min-width:0;min-height:0}' +
            '#insname{display:flex;align-items:center;gap:6px;padding:5px 0 7px;flex-wrap:wrap;flex:none}' +
            '#insname b{color:hsl(var(--h) 100% 79%)}' +
            '.inssel{color:#9a9aba;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;min-width:0}' +
            '#insname .dtbtn{padding:2px 8px;font-size:10px;border:1px solid rgba(255,255,255,.09)}' +
            '#insstf{width:100%;box-sizing:border-box;margin:0 0 5px;padding:4px 9px;border:1px solid rgba(255,255,255,.1);' +
            'border-radius:7px;background:rgba(255,255,255,.04);color:#d8d8ec;font:11px Consolas,monospace;outline:0}' +
            '#insstf:focus{border-color:hsl(var(--h) 100% 72% / .55)}' +
            '.stg{margin:1px 0}' +
            '.stgh{display:flex;align-items:center;gap:7px;padding:4px 2px;cursor:pointer;user-select:none;' +
            'color:#a5a5c8;font-weight:700;font-size:10px;letter-spacing:.8px;text-transform:uppercase}' +
            '.stgh:hover{color:#e8e8f8}' +
            '.stgh .gc{font-size:7.5px;color:hsl(var(--h) 100% 78%);transition:transform .12s;display:inline-block;width:10px}' +
            '.stg.closed .gc{transform:rotate(-90deg)}' +
            '.stgh .cnt{color:#55556f;font-weight:400;letter-spacing:0;text-transform:none}' +
            '.stgb{display:block;padding-left:10px;border-left:1px solid hsl(var(--h) 100% 70% / .16);margin:1px 0 5px 4px}' +
            '.stg.closed .stgb{display:none}' +
            '.stl{display:flex;gap:9px;padding:1px 0;border-bottom:1px solid rgba(255,255,255,.03)}' +
            '.stl .sp{color:#7a7a9c;flex:none;min-width:148px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
            '.stl .sv{color:#c9c9de;word-break:break-all;user-select:text;min-width:0}' +
            '.sw{display:inline-block;width:9px;height:9px;border-radius:2.5px;border:1px solid rgba(255,255,255,.3);' +
            'margin-right:5px;vertical-align:-1px;flex:none}' +
            '.atv-in{width:100%;box-sizing:border-box;background:rgba(255,255,255,.07);' +
            'border:1px solid hsl(var(--h) 100% 72% / .6);border-radius:5px;color:#e8e8f4;' +
            'font:11px Consolas,monospace;padding:1px 6px;outline:0}' +
            '.sil{display:flex;align-items:center;gap:6px;padding:1.5px 0;border-bottom:1px solid rgba(255,255,255,.03)}' +
            '.sic{flex:none;width:12px;height:12px;margin:0;accent-color:hsl(var(--h) 85% 62%);cursor:pointer}' +
            '.sin,.siv{border:0;border-bottom:1px solid transparent;background:transparent;outline:0;' +
            'font:11px/1.5 Consolas,monospace;padding:0 3px;min-width:0}' +
            '.sin{flex:none;width:118px;color:#9d9dcc}' +
            '.siv{flex:1;color:#8fe0cd}' +
            '.sin:focus,.siv:focus{border-bottom-color:hsl(var(--h) 100% 72% / .6)}' +
            '.sil.off{opacity:.42}' +
            '.sil.off .siv{text-decoration:line-through}' +
            '.sil .hx{flex:none;width:18px;height:18px;padding:0;display:grid;place-items:center;border:0;' +
            'border-radius:5px;background:transparent;color:#66667f;cursor:pointer;font-size:10px;opacity:0;transition:opacity .1s}' +
            '.sil:hover .hx{opacity:1}' +
            '.sil .hx:hover{background:rgba(232,17,35,.2);color:#ffb4b9}' +
            '.sil.add .hx{opacity:0;pointer-events:none}' +
            '.sil.add .sic{visibility:hidden}' +
            '.sil.add .sin,.sil.add .siv{color:#66667f;font-style:italic}' +
            '#dtpins{position:relative}' +
            '#insh{position:absolute;inset:33px 0 24px;display:none;flex-direction:column;z-index:4;' +
            'background:rgba(12,12,20,.985);animation:dtin .16s cubic-bezier(.16,1,.3,1)}' +
            '#inshta{flex:1;margin:8px 12px;resize:none;white-space:pre;overflow:auto;' +
            'background:rgba(255,255,255,.035);border:1px solid hsl(var(--h) 100% 70% / .3);border-radius:10px;' +
            'color:#e8e8f4;font:11.5px/1.55 Consolas,"Cascadia Mono",monospace;padding:10px 12px;outline:0}' +
            '#inshta:focus{border-color:hsl(var(--h) 100% 72% / .55)}' +
            '.insed-bar{display:flex;align-items:center;gap:8px;padding:0 14px 10px;color:#66667f;font-size:11px;flex:none}' +
            '.bm{margin:5px auto 3px;max-width:310px;font:9.5px/1 Consolas,monospace}' +
            '.bmL{position:relative;padding:15px 26px;border-radius:7px}' +
            '.bmL i{position:absolute;top:2px;left:6px;font-style:normal;color:rgba(255,255,255,.62);font-size:8px;' +
            'background:rgba(8,8,14,.45);padding:1px 6px;border-radius:4px;letter-spacing:.4px}' +
            '.bmm{background:rgba(246,178,107,.15)}.bmb{background:rgba(255,229,153,.13)}' +
            '.bmp{background:rgba(147,196,125,.16)}' +
            '.bmC{background:rgba(139,180,255,.22);text-align:center;padding:9px 0;color:#d6e4ff;font-size:10px}' +
            '.bmL .v{position:absolute;color:rgba(255,255,255,.72)}' +
            '.bmL .v.t{top:2px;left:50%;transform:translateX(-50%)}.bmL .v.b{bottom:2px;left:50%;transform:translateX(-50%)}' +
            '.bmL .v.l{left:4px;top:50%;transform:translateY(-50%)}.bmL .v.r{right:4px;top:50%;transform:translateY(-50%)}' +
            '#insbc{flex:none;display:flex;align-items:center;gap:1px;padding:4px 10px;border-top:1px solid rgba(255,255,255,.07);overflow-x:auto;white-space:nowrap}' +
            '.cr{border:0;background:rgba(255,255,255,.035);color:#8f8fb0;font:10.5px/1.4 Consolas,monospace;' +
            'padding:2px 7px;border-radius:6px;cursor:pointer;transition:background .1s,color .1s}' +
            '.cr:hover{color:#fff;background:rgba(255,255,255,.09)}' +
            '.cr.on{color:hsl(var(--h) 100% 84%);background:hsl(var(--h) 100% 70% / .16)}' +
            '.csep{color:#4a4a68;font-size:9px;flex:none}' +
            '#dthl{position:fixed;display:none;pointer-events:none;z-index:24;border:1.5px solid hsl(var(--h) 100% 78%);' +
            'background:hsl(var(--h) 100% 74% / .13);border-radius:3px;transition:all .06s}' +
            '#dtpin{position:fixed;display:none;pointer-events:none;z-index:23;border:1.5px dashed hsl(var(--h) 100% 72% / .9);' +
            'background:hsl(var(--h) 100% 74% / .07);border-radius:3px}' +
            '#dttip{position:fixed;display:none;pointer-events:none;z-index:24;padding:3px 9px;border-radius:6px;' +
            'background:#1d1d2e;border:1px solid hsl(var(--h) 100% 78% / .5);color:#d8d8f0;font:11px/1.5 Consolas,monospace;white-space:nowrap}' +
            '#dttip b{color:hsl(var(--h) 100% 83%)}' +
            '#dttip i{display:inline-block;width:8px;height:8px;border-radius:2px;margin:0 1px 0 6px;vertical-align:-1px;border:1px solid rgba(255,255,255,.3)}' +
            // ---- window controls (chromeless title-bar buttons) ----
            // Windows caption-button proportions: 46px wide, flush with the
            // bar's top/right/bottom edges so the close button sits in the
            // exact corner like the native chrome did.
            '.wctrl{display:none;flex:none;align-self:stretch;margin-right:-12px;margin-left:2px}' +
            '.wb{width:46px;display:grid;place-items:center;border:0;align-self:stretch;' +
            'background:transparent;color:#b9b9d4;cursor:pointer;transition:background .12s,color .12s}' +
            '.wb:hover{background:rgba(255,255,255,.1);color:#fff}' +
            '.wb svg{width:14px;height:14px;pointer-events:none}' +
            '.wb.wcx:hover{background:#e81123;color:#fff}' +
            '.wb.wcx{border-top-right-radius:8px}' +  // matches DWM corner rounding
            // ---- in-document new-tab page (virtual blank tabs) ----
            // Same rules as newtab.html (generated from its NTP-CSS block);
            // top:46 + height:100vh centers content exactly like the real
            // start-page document, so the two swap without a visible jump.
            '#ntp{position:fixed;left:0;right:0;top:46px;height:100vh;display:none;z-index:5;overflow:hidden;' +
            'flex-direction:column;align-items:center;justify-content:center;color:#e8e8f0;' +
            'font:13px/1.45 var(--font);user-select:none;-webkit-user-select:none;cursor:default;animation:ntpin .14s ease-out;' +
            'background:radial-gradient(900px 640px at 6% -10%,hsl(var(--h) 78% 56% / .30),transparent 70%),' +
            'radial-gradient(820px 580px at 98% 110%,hsl(calc(var(--h) - 72) 62% 44% / .27),transparent 70%),' +
            'radial-gradient(560px 400px at 72% 26%,hsl(calc(var(--h) + 54) 64% 52% / .09),transparent 70%),#08080f}' +
            '#ntp.on{display:flex}' +
            '#ntp.priv{--h:285}' +
            '@keyframes ntpin{from{opacity:0}to{opacity:1}}' +
            '#ntp code,#ntp kbd{font:11.5px ui-monospace,Menlo,Consolas,monospace;padding:1px 7px;border-radius:6px;' +
            'background:rgba(255,255,255,.07);border:1px solid rgba(255,255,255,.09);color:#a6a6c4}' +
            // <ntp-generated>
            '#ntp .lg { font-size: 13px; font-weight: 800; letter-spacing: 7px; margin: 0 0 38px 7px; background: linear-gradient(115deg, #a9a9ff 10%, #6fe0c6 95%); -webkit-background-clip: text; background-clip: text; color: transparent; }' +
            '#ntp .pv { display: none; margin: 0 0 26px; padding: 5px 14px; border-radius: 999px; font-size: 12px; font-weight: 600; color: #d3c2ff; background: rgba(150,100,255,.14); border: 1px solid rgba(170,130,255,.3); align-items: center; gap: 7px; }' +
            '#ntp .pv svg { width: 14px; height: 14px; }' +
            '#ntp.priv .pv { display: inline-flex; }' +
            '#ntp.priv .lg { margin-bottom: 22px; }' +
            '#ntp .gr { display: inline-flex; align-items: center; gap: 9px; margin-bottom: 14px; font-size: 11.5px; font-weight: 700; letter-spacing: 2px; text-transform: uppercase; color: var(--mut); }' +
            '#ntp .gr::before { content: ""; width: 6px; height: 6px; border-radius: 50%; background: var(--ac); box-shadow: 0 0 12px hsl(var(--h) 100% 70%); }' +
            '#ntp .ck { font-size: 84px; font-weight: 200; letter-spacing: -1.5px; line-height: 1; font-variant-numeric: tabular-nums; background: linear-gradient(180deg, #ffffff 25%, #adadd8); -webkit-background-clip: text; background-clip: text; color: transparent; margin-bottom: 8px; padding: 0 4px; }' +
            '#ntp .dt { font-size: 14px; color: var(--mut); margin-bottom: 44px; letter-spacing: .2px; }' +
            '#ntp form { display: flex; align-items: center; gap: 8px; width: min(680px, 88vw); height: 62px; padding: 0 9px 0 22px; border-radius: 19px; background: linear-gradient(180deg, rgba(255,255,255,.08), rgba(255,255,255,.04)); border: 1px solid rgba(255,255,255,.09); box-shadow: 0 20px 56px rgba(0,0,0,.42), inset 0 1px 0 rgba(255,255,255,.08); transition: border-color .2s, box-shadow .25s; }' +
            '#ntp form:focus-within { border-color: hsl(var(--h) 100% 74% / .6); box-shadow: 0 0 0 4px hsl(var(--h) 100% 74% / .14), 0 24px 64px -14px hsl(var(--h) 100% 66% / .55), inset 0 1px 0 rgba(255,255,255,.08); }' +
            '#ntp form > svg { width: 18px; height: 18px; flex: none; color: var(--dim); }' +
            '#ntp input { flex: 1; min-width: 0; padding: 0 6px; border: 0; outline: 0; background: transparent; color: #f4f4fd; font: 500 16.5px var(--font); user-select: text; -webkit-user-select: text; caret-color: var(--ac); }' +
            '#ntp input::placeholder { color: #5f5f7c; font-weight: 400; }' +
            '#ntp form button { width: 46px; height: 46px; padding: 0; border-radius: 14px; border: 0; cursor: pointer; flex: none; display: grid; place-items: center; color: #fff; background: linear-gradient(130deg, hsl(var(--h) 80% 64%), hsl(calc(var(--h) - 40) 70% 56%)); box-shadow: 0 10px 22px -6px hsl(var(--h) 90% 60% / .7), inset 0 1px 0 rgba(255,255,255,.25); transition: filter .15s, transform .12s; }' +
            '#ntp form button svg { width: 18px; height: 18px; }' +
            '#ntp form button:hover { filter: brightness(1.12); transform: translateX(1px); }' +
            '#ntp form button:active { transform: scale(.94); }' +
            '#ntp .qa { display: flex; flex-wrap: wrap; gap: 10px; justify-content: center; margin-top: 22px; max-width: 88vw; }' +
            '#ntp .qc { display: inline-flex; align-items: center; gap: 8px; padding: 8px 15px; border-radius: 12px; cursor: pointer; font-size: 12.5px; font-weight: 600; color: #a9a9c6; background: rgba(255,255,255,.045); border: 1px solid rgba(255,255,255,.07); transition: color .15s, background .15s, border-color .15s, transform .15s; user-select: none; -webkit-user-select: none; }' +
            '#ntp .qc:hover { color: #fff; background: hsl(var(--h) 100% 74% / .14); border-color: hsl(var(--h) 100% 74% / .4); transform: translateY(-1px); }' +
            '#ntp .qc kbd { font-size: 10.5px; padding: 0 6px; line-height: 17px; border-radius: 6px; color: #8c8cab; }' +
            '#ntp .rc { display: none; margin-top: 46px; width: min(720px, 90vw); }' +
            '#ntp .rc.has { display: block; }' +
            '#ntp .rc h2 { display: block; text-align: center; margin-bottom: 14px; font-size: 11px; font-weight: 700; letter-spacing: 1.6px; text-transform: uppercase; color: var(--dim); }' +
            '#ntp .rc h2::before, #ntp .rc h2::after { display: none; }' +
            '#ntp .tl { display: grid; grid-template-columns: repeat(4, 1fr); gap: 12px; }' +
            '#ntp .ti { display: flex; flex-direction: column; align-items: flex-start; gap: 16px; padding: 15px; min-width: 0; cursor: pointer; border-radius: 17px; overflow: hidden; color: #c4c4de; font-size: 12.5px; font-weight: 600; background: linear-gradient(180deg, rgba(255,255,255,.065), rgba(255,255,255,.03)); border: 1px solid rgba(255,255,255,.07); transition: transform .16s, border-color .16s, box-shadow .22s, color .16s; }' +
            '#ntp .ti:hover { transform: translateY(-3px); color: #fff; border-color: rgba(255,255,255,.17); box-shadow: 0 18px 36px -14px var(--tc, hsl(var(--h) 90% 60%)), 0 10px 24px rgba(0,0,0,.3); }' +
            '#ntp .ti .ic { width: 36px; height: 36px; flex: none; border-radius: 11px; display: grid; place-items: center; position: relative; font-size: 14px; font-weight: 700; color: #fff; background: var(--tc, #555); box-shadow: inset 0 1px 0 rgba(255,255,255,.2); }' +
            '#ntp .ti .ic img { position: absolute; inset: 6px; width: 24px; height: 24px; border-radius: 7px; }' +
            '#ntp .ti > span:last-child { max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }' +
            '#ntp .hn { position: fixed; bottom: 22px; left: 0; right: 0; display: flex; align-items: center; justify-content: center; gap: 9px; color: #55556f; font-size: 12px; padding: 0 20px; text-align: center; pointer-events: none; }' +
            '#ntp .hn i { width: 6px; height: 6px; border-radius: 50%; background: #5fd9a8; box-shadow: 0 0 10px rgba(95,217,168,.8); flex: none; }' +
            // </ntp-generated>
            '</style>' +
            '<div class="bar">' +
            '<div class="nav">' +
            '<button class="nav-b" data-cmd="back" title="Back (Alt+←)">' + SVG.back + '</button>' +
            '<button class="nav-b" data-cmd="forward" title="Forward (Alt+→)">' + SVG.forward + '</button>' +
            '<button class="nav-b" id="rel" data-cmd="reload" title="Reload (Ctrl+R)">' + SVG.reload + '</button>' +
            '<button class="nav-b" data-cmd="home" title="New tab page">' + SVG.home + '</button>' +
            '</div>' +
            '<div id="tabs"></div>' +
            '<button class="plus" id="newtab" title="New tab (Ctrl+T)">' + SVG.plus + '</button>' +
            '<div class="omni" id="omni"><span id="lock">' + SVG.lock + '</span><div id="addr" spellcheck="false"></div>' +
            '<input id="addrIn" spellcheck="false" autocomplete="off">' +
            '<div id="drops"></div></div>' +
            // Tool capsule: one glass pill, one sliding highlight that follows
            // the pointer, tooltips with the shortcut. The orb opens the palette.
            '<div class="tools" id="tools"><i class="hl" id="hl"></i>' +
            '<button class="tb orb" id="orb" data-tip="Command palette" data-sc="Ctrl+K">' + SVG.sparkle + '</button>' +
            '<span class="tsep"></span>' +
            '<button class="tb" id="pip" data-tip="Picture-in-picture" data-sc="Ctrl+Shift+P">' + SVG.pip + '</button>' +
            '<button class="tb" id="star" data-tip="Bookmark this page" data-sc="Ctrl+D">' + SVG.star + '</button>' +
            '<button class="tb" id="dlbtn" data-tip="Downloads" data-sc="Ctrl+J"><svg class="drg" viewBox="0 0 20 20"><circle cx="10" cy="10" r="8"/></svg>' + SVG.dl + '<i class="ddot"></i></button>' +
            '<button class="tb" id="mbtn" data-tip="Menu">' + SVG.dots + '</button></div>' +
            '<div class="wctrl" id="wctrl">' +
            '<button class="wb" id="wmin" title="Minimize">' + SVG.wmin + '</button>' +
            '<button class="wb" id="wmaxb" title="Maximize">' + SVG.wmax + '</button>' +
            '<button class="wb wcx" id="wclose" title="Close">' + SVG.close + '</button></div>' +
            '<div id="prog"><i></i></div>' +
            '</div>' +
            '<div id="status"></div><div id="ttip"></div>' +
            '<div id="tmenu"></div>' +
            '<div id="pmenu"></div>' +
            '<button id="pweye" type="button" title="Show password">' +
            '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round">' +
            '<path d="M2 12c2.7-4.6 6.2-7 10-7s7.3 2.4 10 7c-2.7 4.6-6.2 7-10 7S4.7 16.6 2 12z"/>' +
            '<circle cx="12" cy="12" r="3"/>' +
            '<line class="sl" x1="4.5" y1="4" x2="19.5" y2="20"/></svg></button>' +
            '<div id="pwfill"></div>' +
            '<div id="pwsave"></div>' +
            '<div id="menu"></div>' +
            '<div id="smenu"></div>' +
            '<div id="dlp"></div>' +
            '<div id="pal"><div class="pbox"><div class="pin">' + SVG.mag +
            '<input id="pq" spellcheck="false" autocomplete="off" placeholder="Jump to a tab, search history, run an action, or type a URL">' +
            '<span class="pesc">esc</span></div><div id="pres"></div>' +
            '<div class="pft"><span><b>\u2191\u2193</b> move</span><span><b>\u21b5</b> open</span><span><b>esc</b> close</span></div></div></div>' +
            '<div id="fbar">' + SVG.mag +
            '<input id="fin" spellcheck="false" placeholder="Find in page">' +
            '<span id="fcnt"></span>' +
            '<button class="fb" data-f="-1" title="Previous (Shift+Enter)">' + SVG.up + '</button>' +
            '<button class="fb" data-f="1" title="Next (Enter)">' + SVG.dn + '</button>' +
            '<button class="fb" id="fclose" title="Close (Esc)">' + SVG.close + '</button></div>' +
            '<div id="ntp"><div class="lg">BrAIn</div>' +
            '<div class="pv">' + SVG.mask + 'Private tab &mdash; no history, site data wiped on close</div>' +
            '<div class="gr" id="ntpgr"></div><div class="ck" id="ntpck"></div><div class="dt" id="ntpdt"></div>' +
            '<form id="ntpf">' + SVG.mag +
            '<input id="ntpq" placeholder="Search ' + esc(seName()) + ' or type a URL" autocomplete="off" spellcheck="false">' +
            '<button type="submit" title="Go"><svg viewBox="0 0 16 16"><path d="M3 8h9.4M8.6 4l4 4-4 4" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg></button></form>' +
            '<div class="qa" id="ntpqa">' +
            '<span class="qc" data-k="1">Command palette <kbd>' + (IS_MAC ? '\u2318K' : 'Ctrl K') + '</kbd></span>' +
            '<span class="qc" data-go="about:history">History</span>' +
            '<span class="qc" data-go="about:import">Import data</span>' +
            '<span class="qc" data-go="about:help">How to use</span></div>' +
            '<div class="rc" id="ntprc"><h2>Recent</h2><div class="tl" id="ntptl"></div></div>' +
            '<div class="hn"><i></i><span>MCP server ready &middot; connect any client over <code>stdio</code></span></div></div>' +
            // devtools panel — Console / Network / Inspect over the page
            '<div id="dt"><div id="dtgrip"></div>' +
            '<div class="dthead">' +
            '<button class="dtab on" data-t="con">Console</button>' +
            '<button class="dtab" data-t="net">Network</button>' +
            '<button class="dtab" data-t="cookies">Cookies</button>' +
            '<button class="dtab" data-t="sess">Sessions</button>' +
            '<button class="dtab" data-t="ins">Inspect</button>' +
            '<span class="dtsp"></span>' +
            '<button class="dtbtn" id="dtclear">Clear</button>' +
            '<button class="dtbtn" id="dtx" title="Close (F12)">✕</button></div>' +
            '<div id="dtbody">' +
            '<div class="dtpane on" id="dtpcon">' +
            '<div class="dtscroll" id="dtlist"></div>' +
            '<div class="dtrepl"><span class="pr">›</span><input id="dtin" spellcheck="false" placeholder="Evaluate JavaScript in page context…"></div></div>' +
            '<div class="dtpane" id="dtpnet">' +
            '<div class="dnettools"><div class="fpills" id="dtfp"></div>' +
            '<input id="dtfq" placeholder="Filter" spellcheck="false"></div>' +
            '<div class="dneth"><span>Status</span><span>Method</span><span>Type</span><span>Size</span><span>URL</span></div>' +
            '<div class="dtscroll" id="dtnet"></div>' +
            '<div id="dtdetail" style="display:none"></div>' +
            '<div id="dtedit" style="display:none"></div></div>' +
            '<div class="dtpane" id="dtpcookies">' +
            '<div class="dnettools"><input id="dtcq" placeholder="Filter by name or domain" spellcheck="false">' +
            '<label class="cklbl"><input type="checkbox" id="dtcsite"> this site only</label>' +
            '<span class="dtsp"></span><button class="dtbtn" id="dtcadd">+ Cookie</button></div>' +
            '<div class="ckh"><span>Name</span><span>Value</span><span>Domain</span><span>Path</span>' +
            '<span>Expires</span><span>Flags</span><span></span></div>' +
            '<div class="dtscroll" id="dtclist"></div>' +
            '<div id="dtcedit" style="display:none"></div></div>' +
            '<div class="dtpane" id="dtpsess"><div class="dtscroll" id="dtslist"></div></div>' +
            '<div class="dtpane" id="dtpins">' +
            '<div class="ins-top"><button class="dtbtn" id="dtpick">◉ Pick element</button>' +
            '<span id="dtinsst" style="color:#66667f;font-size:11px">click any element on the page</span>' +
            '<span class="dtsp"></span>' +
            '<button class="dtbtn" id="dtrefr" title="Rebuild the DOM tree" style="font-size:12px">⟳</button></div>' +
            '<div id="inssplit"><div id="instree" tabindex="0"></div>' +
            '<div id="insdet"><div id="insdscroll" class="dtscroll">' +
            '<div id="insname"></div>' +
            '<div class="dtsec">Box model</div><div id="insbox"></div>' +
            '<div class="dtsec">Inline style <span class="cnt" id="insic"></span></div><div id="insinline"></div>' +
            '<div class="dtsec">Styles <span class="cnt" id="insstc"></span></div>' +
            '<input id="insstf" placeholder="Filter computed styles" spellcheck="false">' +
            '<div id="insstyles"></div>' +
            '<div class="dtsec">Attributes <span class="cnt" id="insac"></span></div><div id="insattrs"></div>' +
            '</div></div></div>' +
            '<div id="insh"><textarea id="inshta" spellcheck="false"></textarea>' +
            '<div class="insed-bar"><span>editing outerHTML — replaces the element · ⌘↵ apply · esc cancel</span>' +
            '<span class="dtsp"></span><button class="dtbtn" id="inshesc">Cancel</button>' +
            '<button class="dtsend" id="insapp">Apply</button></div></div>' +
            '<div id="insbc"></div></div>' +
            '</div></div>' +
            '<div id="dthl"></div><div id="dttip"></div><div id="dtpin"></div>';

        sh.querySelector('.nav').addEventListener('click', function (e) {
            var b = e.target.closest('button');
            if (b && b.getAttribute('data-cmd')) send(b.getAttribute('data-cmd'));
        });
        sh.getElementById('newtab').addEventListener('click', function () { openNewTab(false); });
        wireNtp(sh);
        sh.getElementById('mbtn').addEventListener('click', function (e) {
            e.stopPropagation();
            showMenu(sh);
        });

        // ---- window controls + title-bar behavior (chromeless) ----
        // Empty bar space drags the window (host posts WM_NCLBUTTONDOWN
        // HTCAPTION → native move loop incl. Aero Snap); dbl-click toggles
        // maximize; the top 6px stay a resize strip handled at document level.
        sh.getElementById('wmin').addEventListener('click', function () { send('wmin'); });
        sh.getElementById('wmaxb').addEventListener('click', function () { send('wmax'); });
        sh.getElementById('wclose').addEventListener('click', function () { send('wclose'); });
        var bar = sh.querySelector('.bar');
        var barDragOk = function (e) {
            return (state.wc || state.mtl) && e.button === 0 && !edgeCode(e.clientX, e.clientY) &&
                !e.target.closest('button,.tab,.omni,.plus,.nav,.tools,#drops,.wctrl,input');
        };
        bar.addEventListener('mousedown', function (e) {
            if (!barDragOk(e)) return;
            e.preventDefault();
            send('wdrag');
        });
        bar.addEventListener('dblclick', function (e) {
            if (!(state.wc || state.mtl) || !barDragOk(e)) return;
            send('wmax');
        });
        bar.addEventListener('mousemove', function (e) {
            if (!state.wc || state.w || e.clientY >= 6) { bar.style.cursor = ''; return; }
            bar.style.cursor = edgeCursor(edgeCode(e.clientX, e.clientY) || 12);
        });
        bar.addEventListener('mouseleave', function () { bar.style.cursor = ''; });
        syncWin(sh);

        // ---- devtools panel ----
        sh.querySelectorAll('.dtab').forEach(function (b) {
            b.addEventListener('click', function () {
                dtTab = b.getAttribute('data-t');
                syncDtTabs(sh);
                if (dtTab !== 'ins' && dtPicking) setPick(sh, false);
                send('dtget');
                send('dtstate:' + dtTab);
                if (dtTab === 'cookies') send('dtcookies');
                else if (dtTab === 'sess') send('dtsessions');
                else if (dtTab === 'ins') ensureIns(sh);
            });
        });
        sh.getElementById('dtclear').addEventListener('click', function () {
            if (dtTab === 'con') { send('dtclearc'); dtConSig = ''; }
            else if (dtTab === 'net') {
                send('dtclearn'); dtNetSig = '';
                dtNetSel = null; dtNetEntry = null; showNetView(sh, 'list');
            }
            else if (dtTab === 'cookies') send('dtcookies');
            else if (dtTab === 'sess') send('dtsessions');
            else { var p = sh.getElementById('dtins'); if (p) p.textContent = ''; }
            setTimeout(function () { send('dtget'); }, 60);
        });
        // Cookies pane: filter, this-site toggle, add/edit/delete.
        var cq = sh.getElementById('dtcq');
        if (cq) cq.addEventListener('input', function () {
            dtCkQ = cq.value.trim().toLowerCase(); renderCookies(sh);
        });
        var cs = sh.getElementById('dtcsite');
        if (cs) { cs.checked = dtCkSite; cs.addEventListener('change', function () {
            dtCkSite = cs.checked; renderCookies(sh);
        }); }
        var cadd = sh.getElementById('dtcadd');
        if (cadd) cadd.addEventListener('click', function () {
            dtCkEdit = { add: true }; renderCkEditor(sh);
        });
        var cl = sh.getElementById('dtclist');
        if (cl) cl.addEventListener('click', function (ev) {
            var del = ev.target.closest ? ev.target.closest('.ckx') : null;
            if (del) {
                var c = dtCkView[+del.dataset.di]; if (!c) return;
                send('dtcdel:' + JSON.stringify({ n: c.n, d: c.d, p: c.p, s: c.s }));
                return;
            }
            var row = ev.target.closest ? ev.target.closest('.ckr') : null;
            if (row) { dtCkEdit = dtCkView[+row.dataset.i] || null; renderCkEditor(sh); }
        });
        // Sessions pane: switch / reopen / restore.
        var sl = sh.getElementById('dtslist');
        if (sl) sl.addEventListener('click', function (ev) {
            var t = ev.target.closest ? ev.target.closest('.srow') : null;
            if (!t) return;
            if (t.dataset.tab) send('switchtab:' + t.dataset.tab);
            else if (t.dataset.reopen) send('dtunclose:' + t.dataset.u);
            else if (t.dataset.newtab) send('newtab:' + t.dataset.u);
        });
        sl && sl.addEventListener('click', function (ev) {
            if (ev.target.id === 'sessall') send('restoresession');
        });
        // Network filters.
        var fp = sh.getElementById('dtfp');
        if (fp) {
            fp.innerHTML = '';
            NETPILLS.forEach(function (p) {
                var b = document.createElement('button');
                b.className = 'fpill' + (dtNetType === p[0] ? ' on' : '');
                b.textContent = p[1];
                b.addEventListener('click', function () {
                    dtNetType = p[0];
                    fp.querySelectorAll('.fpill').forEach(function (x) { x.classList.toggle('on', x === b); });
                    dtNetSig = 'x'; if (dtLast) renderDt(sh, dtLast);
                });
                fp.appendChild(b);
            });
        }
        var fq = sh.getElementById('dtfq');
        if (fq) fq.addEventListener('input', function () {
            dtNetQ = fq.value.trim().toLowerCase(); dtNetSig = 'x'; if (dtLast) renderDt(sh, dtLast);
        });
        var netList = sh.getElementById('dtnet');
        if (netList) netList.addEventListener('click', function (ev) {
            var row = ev.target.closest ? ev.target.closest('.dnetr') : null;
            if (!row || !row.dataset.id) return;
            dtNetSel = row.dataset.id;
            netList.querySelectorAll('.dnetr').forEach(function (r) {
                r.classList.toggle('sel', r.dataset.id === dtNetSel);
            });
            var dd = sh.getElementById('dtdetail');
            if (dd) dd.innerHTML = '<div class="dtbscroll" style="padding:12px 14px;color:#55556f">loading…</div>';
            showNetView(sh, 'detail');
            send('dtnet:' + dtNetSel);
        });
        // Panel input isolation: filter/editor keystrokes must not reach page
        // handlers or our own window shortcuts (Ctrl+R inside an input, etc.).
        var dtp = sh.getElementById('dt');
        ['keydown', 'keyup', 'keypress'].forEach(function (ev) {
            dtp.addEventListener(ev, function (e) {
                if (e.key === 'Escape') return; // window handler closes the panel
                e.stopPropagation();
            });
        });
        sh.getElementById('dtx').addEventListener('click', function () { toggleDt(sh); });
        var dtin = sh.getElementById('dtin');
        dtin.addEventListener('keydown', function (e) {
            e.stopPropagation();
            if (e.key === 'Enter' && dtin.value.trim()) {
                var x = dtin.value.trim();
                dtin.value = '';
                dtReplRun(sh, x);
            }
            // Escape is handled by the window-level shortcut (closes the panel).
        });
        dtin.addEventListener('mousedown', function (e) { e.stopPropagation(); });
        var dl = sh.getElementById('dtlist');
        dl.addEventListener('scroll', function () {
            dtScrollPin = dl.scrollTop + dl.clientHeight >= dl.scrollHeight - 30;
        });
        sh.getElementById('dtpick').addEventListener('click', function () { setPick(sh, !dtPicking); });
        sh.getElementById('dtrefr').addEventListener('click', function () {
            var keep = insSel;
            buildInsTree(sh);
            if (keep && keep.isConnected) insSelect(sh, keep, false);
        });
        var itree = sh.getElementById('instree');
        itree.addEventListener('click', function (e) {
            var tn = e.target.closest('.tn'); if (!tn) return;
            if (e.target.classList.contains('tx')) { insExpand(tn); return; }
            insSelect(sh, tn._n, false);
        });
        // Double-click a row = edit it (element → HTML, text → content).
        // Expand/collapse stays on the chevron and arrow keys.
        itree.addEventListener('dblclick', function (e) {
            var tn = e.target.closest('.tn'); if (!tn) return;
            var n = tn._n;
            if (n && (n.nodeType === 1 || n.nodeType === 3)) {
                insSelect(sh, n, false);
                openInsHtml(sh, n);
            } else insExpand(tn);
        });
        itree.addEventListener('keydown', function (e) {
            if (!insSelRow) return;
            var rows = [].filter.call(itree.querySelectorAll('.tn'), function (r) { return r.offsetParent; });
            var i = rows.indexOf(insSelRow);
            if (e.key === 'ArrowDown' && rows[i + 1]) insSelect(sh, rows[i + 1]._n, false);
            else if (e.key === 'ArrowUp' && rows[i - 1]) insSelect(sh, rows[i - 1]._n, false);
            else if (e.key === 'ArrowRight' && insSelRow._ch) {
                if (insSelRow._ch.style.display === 'block') { if (rows[i + 1]) insSelect(sh, rows[i + 1]._n, false); }
                else insExpand(insSelRow, true);
            }
            else if (e.key === 'ArrowLeft') {
                if (insSelRow._ch && insSelRow._ch.style.display === 'block') insExpand(insSelRow, false);
                else {
                    var pr = insSelRow.parentNode.closest ? insSelRow.parentNode.closest('.tn') : null;
                    if (pr) insSelect(sh, pr._n, false);
                }
            }
            else return;
            e.preventDefault();
        });
        sh.getElementById('insbc').addEventListener('click', function (e) {
            var b = e.target.closest('.cr'); if (!b) return;
            var chain = sh.getElementById('insbc')._chain || [];
            var n = chain[+b.getAttribute('data-ci')];
            if (n) insSelect(sh, n);
        });
        sh.getElementById('insstf').addEventListener('input', function (e) {
            insStQ = e.target.value.trim().toLowerCase();
            renderInsStyles(sh, insSel);
        });
        // Inline style editor: 'change' commits name/value edits (blur/Enter),
        // checkbox toggles a property off without losing its value.
        var ibox = sh.getElementById('insinline');
        ibox.addEventListener('change', function (e) {
            var row = e.target.closest('.sil'); if (!row || !insSel) return;
            if (e.target.classList.contains('sic')) {
                var off = insInlineOff.get(insSel); if (!off) { off = {}; insInlineOff.set(insSel, off); }
                var k = row.querySelector('.sin').value.trim(); if (!k) return;
                if (e.target.checked) { var pv = off[k]; delete off[k]; setStyle(insSel, k, pv || ''); }
                else { off[k] = insSel.style.getPropertyValue(k); insSel.style.removeProperty(k); }
            } else insSilCommit(sh, row);
            renderInsDetails(sh, insSel);
        });
        ibox.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); e.target.blur(); }
            else if (e.key === 'Escape') {
                var row = e.target.closest('.sil');
                if (row) { row.querySelector('.sin').value = row.getAttribute('data-k') || ''; row.querySelector('.siv').value = row.getAttribute('data-v') || ''; }
                e.target.blur();
            }
            e.stopPropagation();
        });
        ibox.addEventListener('click', function (e) {
            var x = e.target.closest('.hx'); if (!x || !insSel) return;
            var row = x.closest('.sil');
            var k = row.getAttribute('data-k') || row.querySelector('.sin').value.trim();
            if (k) insSel.style.removeProperty(k);
            var off = insInlineOff.get(insSel); if (off) delete off[k];
            renderInsDetails(sh, insSel);
        });
        // Edit-as-HTML overlay buttons + textarea keys.
        sh.getElementById('insapp').addEventListener('click', function () { applyInsHtml(sh); });
        sh.getElementById('inshesc').addEventListener('click', function () { closeInsHtml(sh); });
        sh.getElementById('inshta').addEventListener('keydown', function (e) {
            e.stopPropagation();
            if (e.key === 'Escape') closeInsHtml(sh);
            else if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') applyInsHtml(sh);
        });
        // Style-group collapse toggles + attribute editing + tree-hover
        // highlight all delegate — the list bodies re-render on every pick.
        sh.getElementById('insstyles').addEventListener('click', function (e) {
            var gh = e.target.closest('.stgh'); if (!gh) return;
            var g = gh.parentNode, gn = g.getAttribute('data-g');
            g.classList.toggle('closed');
            insStClosed[gn] = g.classList.contains('closed');
        });
        sh.getElementById('insattrs').addEventListener('dblclick', function (e) {
            var sv = e.target.closest('.sv');
            if (!sv || !insSel || insSel.nodeType !== 1) return;
            var name = sv.parentNode.querySelector('.sp').textContent;
            var inp = document.createElement('input');
            inp.className = 'atv-in';
            inp.value = sv.textContent;
            sv.replaceWith(inp);
            inp.focus(); inp.select();
            var fired = false;
            var done = function (commit) {
                if (fired) return; fired = true;
                try {
                    if (commit) {
                        if (inp.value === '') insSel.removeAttribute(name);
                        else insSel.setAttribute(name, inp.value);
                    }
                } catch (x) { }
                renderInsDetails(sh, insSel);
                if (insSelRow && insSelRow._lb) insSelRow._lb.innerHTML = insLabel(insSel);
            };
            inp.addEventListener('keydown', function (ev) {
                if (ev.key === 'Enter') { ev.preventDefault(); done(true); }
                else if (ev.key === 'Escape') done(false);
            });
            inp.addEventListener('blur', function () { done(true); });
        });
        itree.addEventListener('mousemove', function (e) {
            if (dtPicking) return;
            var tn = e.target.closest('.tn'); if (!tn) return;
            var n = tn._n; if (!n || n.nodeType !== 1) return;
            var r = n.getBoundingClientRect();
            var hl = sh.getElementById('dthl');
            if (r.width < 1 && r.height < 1) { hl.style.display = 'none'; return; }
            hl.style.display = 'block';
            hl.style.left = r.left + 'px'; hl.style.top = r.top + 'px';
            hl.style.width = Math.max(r.width, 1) + 'px'; hl.style.height = Math.max(r.height, 1) + 'px';
            if (tn._dim && !tn._dim.textContent)
                tn._dim.textContent = Math.round(r.width) + '×' + Math.round(r.height);
        });
        itree.addEventListener('mouseleave', function () {
            if (dtPicking) return;
            var hl = sh.getElementById('dthl'); if (hl) hl.style.display = 'none';
        });
        // Suppress the native context menu inside our chrome entirely —
        // tree rows get our node menu, edit fields keep the native one
        // (its only WebKit item, "Inspect Element", is disabled host-side).
        sh.addEventListener('contextmenu', function (e) {
            if (e.target.closest && e.target.closest('input,textarea,[contenteditable]')) return;
            var tn = e.target.closest ? e.target.closest('#instree .tn') : null;
            e.preventDefault(); e.stopPropagation();
            if (tn) {
                insSelect(sh, tn._n, false);
                showNodeMenu(sh, e.clientX, e.clientY, tn._n, tn);
            }
        });
        // Height grip — drag the panel's top edge.
        var grip = sh.getElementById('dtgrip');
        grip.addEventListener('mousedown', function (e) {
            e.preventDefault();
            var y0 = e.clientY, h0 = dtH;
            var mv = function (ev) {
                dtH = Math.min(Math.max(h0 + (y0 - ev.clientY), 140), window.innerHeight - 120);
                grip.parentElement.style.height = dtH + 'px';
            };
            var up = function () {
                document.removeEventListener('mousemove', mv, true);
                document.removeEventListener('mouseup', up, true);
            };
            document.addEventListener('mousemove', mv, true);
            document.addEventListener('mouseup', up, true);
        });

        var star = sh.getElementById('star');
        ['mousedown', 'click'].forEach(function (ev) {
            star.addEventListener(ev, function (e) { e.stopPropagation(); e.preventDefault(); });
        });
        star.addEventListener('click', function () { send('bm:'); });

        var pipB = sh.getElementById('pip');
        ['mousedown', 'click'].forEach(function (ev) {
            pipB.addEventListener(ev, function (e) { e.stopPropagation(); e.preventDefault(); });
        });
        pipB.addEventListener('click', togglePip);

        var dlB = sh.getElementById('dlbtn');
        ['mousedown', 'click'].forEach(function (ev) {
            dlB.addEventListener(ev, function (e) { e.stopPropagation(); e.preventDefault(); });
        });
        dlB.addEventListener('click', function () { toggleDl(sh); });
        // Popover clicks must not reach the document off-listener.
        sh.getElementById('dlp').addEventListener('mousedown', function (e) { e.stopPropagation(); });

        var pwB = sh.getElementById('pweye');
        ['mousedown', 'mouseup', 'click', 'dblclick'].forEach(function (ev) {
            pwB.addEventListener(ev, function (e) { e.preventDefault(); e.stopPropagation(); });
        });
        pwB.addEventListener('click', function () {
            var f = pwEyeField;
            if (!f || !f.isConnected) { hidePwEye(sh); return; }
            var show = f.type === 'password';
            try { f.type = show ? 'text' : 'password'; } catch (e2) { return; }
            if (show) f.setAttribute('data-brainpw', '1'); else f.removeAttribute('data-brainpw');
            pwB.classList.toggle('on', show);
            pwB.title = show ? 'Hide password' : 'Show password';
            f.focus();
            try { var n = f.value.length; f.setSelectionRange(n, n); } catch (e3) { }
        });
        wirePalette(sh);
        wireTools(sh);

        var fin = sh.getElementById('fin');
        fin.addEventListener('input', function () { findStep(sh, false); });
        fin.addEventListener('keydown', function (e) {
            e.stopPropagation();
            if (e.key === 'Enter') { e.preventDefault(); findStep(sh, e.shiftKey); }
            else if (e.key === 'Escape') { e.preventDefault(); closeFind(sh); }
            else if (e.key === 'F3') { e.preventDefault(); findStep(sh, e.shiftKey); }
        });
        // Find-bar keys/pointer must not leak to page handlers or our own
        // window shortcuts.
        ['keyup', 'keypress', 'mousedown', 'mouseup', 'click', 'dblclick'].forEach(function (ev) {
            sh.getElementById('fbar').addEventListener(ev, function (e) { e.stopPropagation(); });
        });
        sh.querySelectorAll('#fbar .fb').forEach(function (b) {
            b.addEventListener('click', function () {
                var f = b.getAttribute('data-f');
                if (f === null) { closeFind(sh); return; }
                findStep(sh, f === '-1');
            });
        });

        var omni = sh.getElementById('omni');
        var inp = sh.getElementById('addrIn');

        omni.addEventListener('click', function (e) {
            // Clicks inside the history dropdown are handled by the rows.
            if (e.target && e.target.closest && e.target.closest('#drops')) return;
            startEditing(sh);
        });
        inp.addEventListener('paste', function (e) {
            e.preventDefault();
            var t = ((e.clipboardData || window.clipboardData || {}).getData
                ? e.clipboardData.getData('text/plain') : '');
            t = String(t).replace(/\s+/g, ' ');
            var s0 = inp.selectionStart || 0, e0 = inp.selectionEnd || 0;
            inp.value = inp.value.slice(0, s0) + t + inp.value.slice(e0);
            inp.setSelectionRange(s0 + t.length, s0 + t.length);
            dirty = true; ghostAt = -1; ghostBlock = null;
            dropIdx = -1;
            renderDrops(sh); applyInline(sh);
        });
        inp.addEventListener('input', function () {
            dirty = true;
            ghostAt = -1; // typing consumed or replaced the ghost tail
            // Editing past a dismissed suggestion re-enables inline completion.
            if (ghostBlock !== null && inp.value !== ghostBlock) ghostBlock = null;
            dropIdx = -1;
            renderDrops(sh);
            applyInline(sh);
        });
        inp.addEventListener('keydown', function (e) {
            e.stopPropagation();
            var items = filteredHist();
            if (e.key === 'Enter') {
                e.preventDefault();
                var it = dropIdx >= 0 && items[dropIdx];
                // A selected ghost tail stays in the value — Enter goes to it.
                var v = it ? (it.s ? it.q : it.u) : inp.value.trim();
                if (v) send('navigate:' + v);
                inp.blur();
            } else if (e.key === 'Escape') {
                inp.blur();
            } else if (e.key === 'ArrowDown') {
                e.preventDefault();
                if (items.length) { dropIdx = (dropIdx + 1) % items.length; renderDrops(sh); }
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                if (items.length) { dropIdx = (dropIdx - 1 + items.length) % items.length; renderDrops(sh); }
            } else if (ghostAt >= 0 && (e.key === 'ArrowRight' || e.key === 'End' || e.key === 'Tab')) {
                e.preventDefault(); // accept the ghost completion
                inp.setSelectionRange(inp.value.length, inp.value.length);
                ghostAt = -1;
            } else if (ghostAt >= 0 && (e.key === 'ArrowLeft' || e.key === 'Backspace' || e.key === 'Delete')) {
                // Drop the suggestion instead of deleting into it; don't
                // re-suggest the same text until it changes (Chrome behaves so).
                e.preventDefault();
                var q2 = readQuery();
                ghostAt = -1;
                ghostBlock = q2;
                inp.value = q2;
                inp.setSelectionRange(q2.length, q2.length);
                renderDrops(sh);
            }
        });
        inp.addEventListener('blur', function () { finishEditing(sh); });
        // Don't let page shortcuts eat omnibox keystrokes/pointer events.
        ['keyup', 'keypress', 'mousedown', 'mouseup', 'click', 'dblclick'].forEach(function (ev) {
            omni.addEventListener(ev, function (e) { e.stopPropagation(); });
        });

        (document.body || document.documentElement).appendChild(host);
        reserveSpace();
        if (hadBoot) {
            var barEl = sh.querySelector('.bar');
            if (barEl) barEl.style.animation = 'none';
        }

        statusEl = sh.getElementById('status');
        // Document/window-level wires attach exactly once per document —
        // remounts after a page rebuild must not stack duplicate listeners
        // (double Ctrl+T = two tabs, etc.). They resolve the live shadow
        // root via sh0() so they keep working after the host is replaced.
        if (!wired) {
            wired = true;
            wireShortcuts();
            wireStatusBubble();
            wireLinkGestures();
            wirePageMenu();
            wirePwEye();
            wirePwMgr();
            wireScrollReport();
            wirePip();
            wireAudio();
            // Chromeless edge resize: thin zones on the window edges start a
            // native modal resize via the host (skipped while maximized —
            // the borders are off-screen then anyway).
            document.addEventListener('mousemove', function (e) {
                if (!state.wc || state.w) { if (curEl) setEdgeCursor(0); return; }
                setEdgeCursor(edgeCode(e.clientX, e.clientY) ? edgeCursor(edgeCode(e.clientX, e.clientY)) : 0);
            }, true);
            document.addEventListener('mousedown', function (e) {
                if (!state.wc || state.w || dtPicking || e.button !== 0) return;
                var c = edgeCode(e.clientX, e.clientY);
                if (!c) return;
                e.preventDefault();
                setEdgeCursor(0);
                send('wresize:' + c);
            }, true);
            // Inspect-tab element picker — highlight on hover, pin on click.
            document.addEventListener('mousemove', function (e) {
                if (!dtPicking) return;
                var s = sh0(); if (!s) return;
                var el = document.elementFromPoint(e.clientX, e.clientY);
                if (!el || el.id === HOST_ID) {
                    s.getElementById('dthl').style.display = 'none';
                    s.getElementById('dttip').style.display = 'none';
                    return;
                }
                var r = el.getBoundingClientRect();
                var hl = s.getElementById('dthl');
                hl.style.display = 'block';
                hl.style.left = r.left + 'px'; hl.style.top = r.top + 'px';
                hl.style.width = Math.max(r.width, 1) + 'px'; hl.style.height = Math.max(r.height, 1) + 'px';
                var tip = s.getElementById('dttip');
                tip.style.display = 'block';
                var ecs = getComputedStyle(el);
                var ecl = (typeof el.className === 'string' ? el.className : '').trim().split(/\s+/)[0];
                tip.innerHTML = '<b>' + esc(el.tagName.toLowerCase()) + '</b>' +
                    (el.id ? '<span style="color:#8fd3ff">#' + esc(el.id) + '</span>' : '') +
                    (ecl ? '<span style="color:#9d9dcc">.' + esc(ecl) + '</span>' : '') +
                    '  ' + Math.round(r.width) + '×' + Math.round(r.height) +
                    '  ' + esc((+parseFloat(ecs.fontSize).toFixed(1)) + 'px') + '/' + esc(ecs.fontWeight) +
                    '<i style="background:' + esc(ecs.color) + '" title="color"></i>' +
                    '<i style="background:' + esc(ecs.backgroundColor) + '" title="background"></i>';
                tip.style.left = Math.max(4, Math.min(r.left, window.innerWidth - 320)) + 'px';
                tip.style.top = Math.max(4, r.top - 26) + 'px';
            }, true);
            document.addEventListener('mousedown', function (e) {
                if (!dtPicking) return;
                e.preventDefault(); e.stopPropagation();
                var s = sh0();
                var el = document.elementFromPoint(e.clientX, e.clientY);
                if (s && el && el.id !== HOST_ID) insSelect(s, el);
                setPick(s, false);
            }, true);
            // Keep the pinned selection outline glued to its element.
            document.addEventListener('scroll', function () {
                var s = sh0(); if (s && insSel) pinHighlight(s, insSel);
            }, true);
            window.addEventListener('resize', function () {
                var s = sh0(); if (s && insSel) pinHighlight(s, insSel);
            });
            // Embedded __brainBoot (internal pages) lands during head parse —
            // retry at DOMContentLoaded in case the 0ms timer beat it.
            document.addEventListener('DOMContentLoaded', applyBoot);
            // Keep the omnibox in sync with in-page (SPA) navigation; tick the
            // start-page clock while the overlay is up.
            setInterval(function () {
                var s = sh0(); if (!s) return;
                // A mount-time 'tabs' request can race driver init on the
                // host and get dropped — re-ask until the first push lands.
                if (!window.__brainState && !window.__brainBoot) send('tabs');
                updateAddr(s);
                if (state.ov) ntpTick(s);
                if (dtOpen) {
                    send('dtget');
                    if (dtTab === 'cookies') send('dtcookies');
                    else if (dtTab === 'sess') send('dtsessions');
                }
            }, 1000);
        }
        wireStripDnd(sh); // strip node is recreated each mount
        lastSig = '';     // force a fresh strip render for the new shadow
        tabEls = {};      // new shadow — the old tab elements are gone
        ntpHistSig = '';  // ...and so are the overlay tiles
        firstRender = true;
        editing = false; dirty = false; dropIdx = -1; // remount kills the old omnibox
        ghostAt = -1; ghostBlock = null;

        // window.external bridge is only injected on real documents; when the
        // bar mounts inside about:blank (start page) the shim still exists.
        // At document-start our user script can beat Photino's bridge — poll
        // briefly rather than permanently losing host pushes.
        var wireBridge = function () {
            if (!(window.external && window.external.receiveMessage)) return false;
            window.external.receiveMessage(function (m) {
                try {
                    // Consume any pending boot state BEFORE the push renders —
                    // the push can beat the deferred applyBoot and trigger the
                    // whole-strip cascade (visible as the new-tab flicker).
                    applyBoot();
                    // macOS Photino evals the message raw, so m arrives as a
                    // JS object; WebView2 delivers it as a JSON string.
                    var j = typeof m === 'string' ? JSON.parse(m) : m;
                    // Internal pages (about:history) subscribe to host pushes
                    // through this relay — receiveMessage has one listener.
                    try { document.dispatchEvent(new CustomEvent('brain-msg', { detail: j })); } catch (e0) { }
                    if (j && j.b === 9 && Array.isArray(j.tabs)) {
                        var stale = reconcilePending(j.tabs); // sets state.tabs
                        state.loading = !!j.loading;
                        markLoading();
                        state.canBack = j.canBack !== false;
                        state.canFwd = !!j.canFwd;
                        state.hist = Array.isArray(j.hist) ? j.hist : [];
                        state.bmarks = Array.isArray(j.bmarks) ? j.bmarks : [];
                        state.bm = !!j.bm;
                        state.zoom = j.zoom || 100;
                        state.topmost = !!j.topmost;
                        state.wc = !!j.wc;
                        state.mtl = !!j.mtl;
                        state.nat = !!j.nat;
                        state.w = !!j.w;
                        state.sess = j.sess || 0;
                        if (j.se) state.se = j.se;
                        if (Array.isArray(j.sel)) state.sel = j.sel;
                        var nq = sh.getElementById('ntpq');
                        if (nq) nq.placeholder = 'Search ' + seName() + ' or type a URL';
                        lastPushOv = !!j.ov;
                        // An optimistic/pending overlay holds until the
                        // host confirms or the hold expires. A push that
                        // predates a local switch carries the old tab's ov.
                        if (lastPushOv && !stale) ovPending = 0;
                        if (!stale && (lastPushOv || !(ovPending > Date.now()))) {
                            state.ov = lastPushOv;
                            state.privActive = !!j.privActive;
                        }
                        window.__brainState = state;
                        render(sh);
                        syncWin(sh);
                        syncNtp(sh);
                        updateAddr(sh);
                        updateStar(sh);
                        var om = sh.getElementById('omni');
                        if (om) om.classList.toggle('priv', !!state.privActive);
                        if (j.dt && !dtOpen) toggleDt(sh, j.dt, true);
                        if (editing) renderDrops(sh);
                        try {
                            document.dispatchEvent(new CustomEvent('brain-state', { detail: state }));
                        } catch (e2) { }
                    } else if (j && j.b === 10 && dtOpen) {
                        dtLast = j;
                        renderDt(sh, j);
                    } else if (j && j.b === 11 && dtOpen) {
                        dtEvalResult(sh, j);
                    } else if (j && j.b === 12 && dtOpen) {
                        dtNetEntry = j;
                        renderNetDetail(sh);
                    } else if (j && j.b === 13 && dtOpen) {
                        renderDtResp(sh, j);
                    } else if (j && j.b === 14 && dtOpen) {
                        dtCk = j.ck || []; dtCkHost = j.host || '';
                        renderCookies(sh);
                    } else if (j && j.b === 15 && dtOpen) {
                        renderSessions(sh, j);
                    } else if (j && j.b === 20) {
                        onDlPush(sh, j);
                    } else if (j && j.b === 22) {
                        insertFieldText(pgMenuTarget, j.t);
                    } else if (j && j.b === 23) {
                        onPwCreds(sh, j.items || []);
                    } else if (j && j.b === 24) {
                        showPwSave(sh, j.o, j.u, j.p);
                    } else if (j && j.b === 26) {
                        onPwFill(sh, j);
                    }
                } catch (e) { }
            });
            return true;
        };
        try {
            if (!wireBridge()) {
                var bpoll = setInterval(function () {
                    try { if (wireBridge()) clearInterval(bpoll); } catch (e0) { }
                }, 60);
                setTimeout(function () { clearInterval(bpoll); }, 15000);
            }
        } catch (e) { }

        // Apply boot synchronously — the strip renders final state in the
        // same task as mount, so an empty strip can never reach a paint.
        applyBoot();
        render(sh);
        syncNtp(sh);
        updateAddr(sh);
        renderDlBtn(sh);
        send('dllist'); // seed the badge before any push arrives
        send('tabs');
        // Retry deferred: covers a late-arriving embedded payload.
        setTimeout(applyBoot, 0);
    }

    window.__brainChrome = {
        mount: mount,
        applyBoot: applyBoot,
        hide: function () {
            var h = document.getElementById(HOST_ID);
            if (h) h.style.display = 'none';
            reserveSpace(false); // screenshots shouldn't show a blank gap either
        },
        show: function () {
            var h = document.getElementById(HOST_ID);
            if (h) h.style.display = '';
            reserveSpace();
        },
    };

    if (document.body || document.documentElement) mount();
    else {
        var obs = new MutationObserver(function () {
            if (document.documentElement) { obs.disconnect(); mount(); }
        });
        obs.observe(document, { childList: true });
    }

    // Re-mount if a page rebuild removed our node or cleared the margin.
    setInterval(function () {
        var h = document.getElementById(HOST_ID);
        if (!h) mount();
        else if (document.documentElement && document.documentElement.style.marginTop !== RESERVED) reserveSpace();
    }, 3000);
})();
