/* BrAIn top chrome — injected into every page (opt-out via --no-toolbar).
 * Shadow-DOM isolated. Commands -> window.external.sendMessage('brain:...'),
 * state pushes arrive via window.external.receiveMessage({b:9,...}).
 * Latest push is mirrored to window.__brainState and a 'brain-state'
 * CustomEvent is dispatched on document for page-side widgets (newtab tiles).
 * Exposes window.__brainChrome { mount, hide, show } — hide/show is used to
 * keep the bar out of captured screenshots. */
(function () {
    if (window.__brainChrome) return;

    // ---- trust boundary ---------------------------------------------------
    // This script runs inside every web page, so anything it exposes (globals, DOM events)
    // can be read by that page's own scripts. Only BrAIn's own pages (about: documents the
    // host builds: new tab, history, passwords, flows...) may see host state.
    var INTERNAL_DOC = false;
    try { INTERNAL_DOC = /^(about|data|app):$/i.test(location.protocol) && window.top === window && !window.opener; } catch (e) { }
    var haveState = false, bootHeld = null;
    // The host can bake the boot state (tabs, history, bookmarks) in before this script runs:
    // take it out of the page's reach straight away.
    try { if (window.__brainBoot && !INTERNAL_DOC) { bootHeld = window.__brainBoot; window.__brainBoot = null; } } catch (e) { }
    function exposeState() { haveState = true; if (INTERNAL_DOC) { try { window.__brainState = state; } catch (e) { } } }

    // BrAIn's own page-level listeners bypass brain.js's listener registry
    // (they're not the page's) — rawOn is the un-hooked addEventListener.
    var rawOn = (window.__brain && window.__brain.rawOn) ||
        function (t, type, fn, o) { t.addEventListener(type, fn, o); };

    var HOST_ID = '__brain-chrome-host';
    // Split-toolbar mode (macOS --split): the bar lives in its own native web
    // view above the page. ROLE 'bar' = that view (toolbar + its menus);
    // ROLE 'page' = the page view (page-level features: DevTools, find, page
    // context menu, password prompts, new-tab overlay). Anything one view
    // can't do itself is relayed through the host (pageui:/barui: verbs).
    var ROLE = window.__BRAIN_ROLE === 'bar' ? 'bar' : 'page';
    var SPLIT = ROLE === 'bar' || window.__BRAIN_SPLIT === true;
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
    state.rw = false; state.rwa = false; state.rwk = 30; state.rwm = 128; state.rws = [];
    state.ag = { p: false, n: 0, t: '', s: false, k: 0 }; // agent: paused / busy count / tool / ever seen / blocked count
    state.se = 'google'; state.sel = []; // search engine id + [{i,n}] list
    state.ov = false;      // active tab is a blank new tab painted as an in-doc overlay
    state.wc = false;      // chromeless window — show our own min/max/close
    state.mtl = false;     // macOS inline titlebar — leave room for traffic lights
    state.w = false;       // window currently maximized
    var ovPending = 0;     // optimistic overlay hold (ms deadline) — see holdOverlay()
    var lastPushOv = false;
    var ntpHistSig = '';

    var SVG = {
        bot: '<svg viewBox="0 0 16 16"><rect x="3" y="5.2" width="10" height="7.6" rx="2.4" fill="none" stroke="currentColor" stroke-width="1.4"/><path d="M8 5.2V3M6.9 3h2.2" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/><circle cx="6" cy="9" r="1" fill="currentColor"/><circle cx="10" cy="9" r="1" fill="currentColor"/></svg>',
        pause: '<svg viewBox="0 0 16 16"><path d="M5.6 3.6v8.8M10.4 3.6v8.8" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"/></svg>',
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
        key: '<svg viewBox="0 0 16 16"><circle cx="5.4" cy="10.2" r="3" fill="none" stroke="currentColor" stroke-width="1.4"/><path d="M7.7 7.9 13.2 2.4M10.4 5.2l2.2 2.2M12.2 3.4l2.2 2.2" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>',
        rw: '<svg viewBox="0 0 16 16"><path d="M3.1 6.8A5.3 5.3 0 1 1 2.8 9" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/><path d="M2.8 4.5v4h4" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/><path d="M8.1 5.3V8l1.8 1.4" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/></svg>',
        gear: '<svg viewBox="0 0 16 16"><circle cx="8" cy="8" r="1.9" fill="none" stroke="currentColor" stroke-width="1.4"/><path d="M8 1.7v1.7M8 12.6v1.7M1.7 8h1.7M12.6 8h1.7M3.5 3.5l1.2 1.2M11.3 11.3l1.2 1.2M12.5 3.5l-1.2 1.2M4.7 11.3l-1.2 1.2" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>'
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
    var RES_BASE = 46, RW_H = 44; // toolbar + rewind bar heights
    function resPx() { return (SPLIT ? 0 : RES_BASE) + (rw && rw.open ? RW_H : 0); }
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
            var v = 'calc(56px + ' + resPx() + 'px)';
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
            var px = resPx();
            if (on === false || px === 0) {
                ytVars(false);
                try { var sg = document.getElementById('brain-yt-guide'); if (sg) sg.remove(); } catch (e) { }
                if (ytFilled) { try { var mcr = document.getElementById('masthead-container'); if (mcr) mcr.style.removeProperty('background-color'); } catch (e) { } ytFilled = false; }
                de.style.marginTop = '';
                for (var i = 0; i < fixedShifted.length; i++) {
                    var it = fixedShifted[i];
                    if (it.orig) it.el.style.setProperty('top', it.orig);
                    else it.el.style.removeProperty('top');
                    if (it.hs) { if (it.oh) it.el.style.setProperty('height', it.oh); else it.el.style.removeProperty('height'); }
                }
                fixedShifted = [];
                fixedSeen = new WeakSet();
            } else {
                de.style.setProperty('margin-top', px + 'px', 'important');
                ytVars(true);
                adjustFixed();
            }
        } catch (e) { }
    }
    function adjustFixed() {
        if (resPx() === 0) return;
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
                target.style.setProperty('top', 'calc(' + t + ' + ' + resPx() + 'px)', 'important');
                break; // one header band per column sample
            }
        }
        adjustSticky(vw, vh);
        ytMastheadFill();
    }

    // YouTube: once the masthead is shifted down, its own #background layer
    // (z-index:-1, collapses to 0x0 here) no longer paints, so the feed shows
    // through the header while scrolling. Paint the masthead container with the
    // same colour that layer would have used.
    var ytFilled = false;
    function ytMastheadFill() {
        try {
            if (SPLIT || !/(^|\.)youtube\.com$/.test(location.hostname)) return;
            var mc = document.getElementById('masthead-container');
            if (!mc) return;
            var clear = function (c) { return !c || c === 'transparent' || /^rgba\(\s*\d+,\s*\d+,\s*\d+,\s*0\s*\)$/.test(c); };
            var col = '';
            var bgEl = document.querySelector('ytd-masthead #background');
            if (bgEl) { var bc = getComputedStyle(bgEl).backgroundColor; if (!clear(bc)) col = bc; }
            for (var el = mc.parentElement; !col && el; el = el.parentElement) {
                var c2 = getComputedStyle(el).backgroundColor;
                if (!clear(c2)) col = c2;
            }
            if (!col) return;
            mc.style.setProperty('background-color', col, 'important');
            ytFilled = true;
        } catch (e) { }
    }

    // YouTube: shift its fixed layout pieces by NAME instead of relying on
    // point sampling (the masthead renders late on a fresh load, and the guide
    // drawer sits ~9 ancestors above the element under the sample point).
    // Re-applied on a timer because YouTube re-renders/toggles them on SPA nav.
    var IS_YT = /(^|\.)youtube\.com$/.test(location.hostname);
    function ytDirect() {
        try {
            if (!IS_YT || !document.documentElement.style.marginTop) return;
            var vh = window.innerHeight, edge = 56 + resPx();
            for (var bi = fixedShifted.length - 1; bi >= 0; bi--) {
                var be = fixedShifted[bi];
                if (!be.band) continue;
                var bp = getComputedStyle(be.el).position;
                if (bp === 'fixed' || bp === 'sticky') continue;
                if (be.orig) be.el.style.setProperty('top', be.orig); else be.el.style.removeProperty('top');
                if (be.hs) { if (be.oh) be.el.style.setProperty('height', be.oh); else be.el.style.removeProperty('height'); }
                fixedSeen.delete(be.el);
                fixedShifted.splice(bi, 1);
            }
            var mc = document.getElementById('masthead-container');
            if (mc && !fixedSeen.has(mc)) {
                var mcs = getComputedStyle(mc);
                if (mcs.position === 'fixed' || mcs.position === 'sticky') {
                    var mt = parseFloat(mcs.top) || 0;
                    if (mt < resPx() - 1) {
                        fixedSeen.add(mc);
                        fixedShifted.push({ el: mc, orig: mc.style.getPropertyValue('top') || null });
                        mc.style.setProperty('top', 'calc(' + mt + 'px + ' + resPx() + 'px)', 'important');
                    }
                }
            }
            var list = document.querySelectorAll('tp-yt-app-drawer#guide, ytd-mini-guide-renderer, #chips-wrapper');
            for (var i = 0; i < list.length; i++) {
                var el = list[i];
                if (fixedSeen.has(el)) continue;
                var cs = getComputedStyle(el);
                if (cs.position !== 'fixed') continue;
                var t = parseFloat(cs.top);
                if (!(t >= 0) || t >= edge - 1) continue;
                var rec = { el: el, orig: el.style.getPropertyValue('top') || null, band: true };
                fixedSeen.add(el);
                el.style.setProperty('top', edge + 'px', 'important');
                var r = el.getBoundingClientRect();
                if (el.tagName === 'TP-YT-APP-DRAWER' && r.bottom >= vh - 4) {
                    rec.oh = el.style.getPropertyValue('height') || null; rec.hs = true;
                    el.style.setProperty('height', Math.max(120, vh - edge) + 'px', 'important');
                }
                fixedShifted.push(rec);
            }
            if (!document.getElementById('brain-yt-guide')) {
                var st = document.createElement('style');
                st.id = 'brain-yt-guide';
                var H = 'calc(100vh - ' + edge + 'px)';
                st.textContent =
                    'tp-yt-app-drawer#guide{height:' + H + ' !important;max-height:' + H + ' !important}' +
                    'tp-yt-app-drawer#guide #contentContainer{height:100% !important;max-height:100% !important}' +
                    'tp-yt-app-drawer#guide #guide-inner-content{height:100% !important;max-height:100% !important;overflow-y:auto !important}' +
                    'ytd-mini-guide-renderer{max-height:' + H + ' !important;overflow-y:auto !important}';
                (document.head || document.documentElement).appendChild(st);
            }
            ytMastheadFill();
        } catch (e) { }
    }
    if (IS_YT) setInterval(ytDirect, 400);

    // Second pass: wide sticky/fixed bands (YouTube's #chips-wrapper is
    // position:fixed; top:56px) that a site pins at a hard-coded offset BELOW its header. We moved
    // the header down by RESERVED but such a band keeps its own offset, so it
    // slides under the shifted header and overlaps it. Push any sticky band
    // whose pin point is above the shifted header's bottom edge down to it.
    function adjustSticky(vw, vh) {
        if (!fixedShifted.length) return;
        // Pages flip such a band between position:fixed (scrolled) and
        // relative (at the top). Our inline top would push a RELATIVE band
        // 100px down — release it until it pins again.
        for (var bi = fixedShifted.length - 1; bi >= 0; bi--) {
            var be = fixedShifted[bi];
            if (!be.band) continue;
            var bp;
            try { bp = getComputedStyle(be.el).position; } catch (e) { continue; }
            if (bp === 'fixed' || bp === 'sticky') continue;
            if (be.orig) be.el.style.setProperty('top', be.orig); else be.el.style.removeProperty('top');
            if (be.hs) { if (be.oh) be.el.style.setProperty('height', be.oh); else be.el.style.removeProperty('height'); }
            fixedSeen.delete(be.el);
            fixedShifted.splice(bi, 1);
        }
        var bottom = 0;
        for (var i = 0; i < fixedShifted.length; i++) {
            var hr;
            if (fixedShifted[i].band) continue; // only headers define the edge
            try { hr = fixedShifted[i].el.getBoundingClientRect(); } catch (e) { continue; }
            if (hr.height > 0 && hr.top < 120 && hr.bottom > bottom && hr.bottom < 200) bottom = hr.bottom;
        }
        if (!bottom) return;
        var cols = Math.max(4, Math.min(10, Math.ceil(vw / 160)));
        var ys = [Math.round(bottom - 40), Math.round(bottom - 6), Math.round(bottom + 14), Math.round(bottom + 40)];
        for (var c = 0; c < cols; c++) {
            var x = Math.round(vw * (c + 0.5) / cols);
            for (var yi = 0; yi < ys.length; yi++) {
                var stack;
                try { stack = document.elementsFromPoint(x, ys[yi]); } catch (e) { continue; }
                for (var k = 0; k < stack.length; k++) {
                    for (var el = stack[k], d = 0; el && d < 14 && el !== document.body && el !== document.documentElement; el = el.parentElement, d++) {
                        if (el.id === HOST_ID || fixedSeen.has(el)) continue;
                        var cs;
                        try { cs = getComputedStyle(el); } catch (e) { continue; }
                        if (cs.position !== 'sticky' && cs.position !== 'fixed') continue;
                        var t = parseFloat(cs.top);
                        if (!(t >= 0) || t >= bottom - 1 || !/px$/.test(cs.top)) continue;
                        var r = el.getBoundingClientRect();
                        var isBand = r.width >= vw * 0.3 && r.height >= 16 && r.height <= 140;
                        // tall side panel glued to a viewport edge (YouTube's guide drawer)
                        var isDrawer = cs.position === 'fixed' && r.width > 20 && r.width <= vw * 0.4 &&
                            r.height >= vh * 0.5 && (r.left <= 4 || r.right >= vw - 4);
                        if (!isBand && !isDrawer) continue;
                        fixedSeen.add(el);
                        var rec = { el: el, orig: el.style.getPropertyValue('top') || null, band: true };
                        el.style.setProperty('top', Math.ceil(bottom) + 'px', 'important');
                        if (isDrawer && r.bottom >= vh - 4) {
                            // keep it ending at the viewport bottom instead of overflowing by the shift
                            rec.oh = el.style.getPropertyValue('height') || null; rec.hs = true;
                            el.style.setProperty('height', Math.max(120, vh - Math.ceil(bottom)) + 'px', 'important');
                        }
                        fixedShifted.push(rec);
                    }
                }
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
    // URL of the page being browsed. The bar view's own document is a stub, so
    // it takes the active tab's URL from host state instead.
    function curUrl() {
        if (ROLE !== 'bar') return location.href;
        var a = activeTab();
        return (a && a.url) || 'about:blank';
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
        setTimeout(function () { rawOn(document, 'mousedown', off, true); }, 0);
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
            var stoppable = !!(state.loading && loadSlow);
            if (rel._stop !== stoppable) {
                rel._stop = stoppable;
                rel.innerHTML = stoppable ? SVG.stop : SVG.reload;
                rel.setAttribute('data-cmd', stoppable ? 'stop' : 'reload');
                rel.classList.toggle('stopping', stoppable);
                if (stoppable) setTip(rel, 'Stop loading', 'Esc'); else setTip(rel, 'Reload', 'Ctrl+R');
            }
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
        var url = curUrl();
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
        if (SPLIT && ROLE === 'page') { send('barui:addr'); return; }
        var addr = sh.getElementById('addr'), inp = sh.getElementById('addrIn');
        if (editing || !addr || !inp) return;
        editing = true;
        dirty = false;
        ghostAt = -1; ghostBlock = null;
        addr.style.display = 'none';
        inp.style.display = 'block';
        inp.value = curUrl() === 'about:blank' ? '' : curUrl();
        inp.focus();
        inp.select();
        dropIdx = -1;
        renderDrops(sh);
    }

    function finishEditing(sh) {
        var addr = sh.getElementById('addr'), inp = sh.getElementById('addrIn');
        editing = false;
        if (ROLE === 'bar') send('focuspage');
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
        if (hasSel) { items.push(['copy', 'Copy']); items.push(['mdsel', 'Copy as Markdown']); items.push(null); }
        items.push(['back', 'Back']);
        items.push(['forward', 'Forward']);
        items.push(['reload', 'Reload']);
        items.push(null);
        items.push(['newprivtab', 'New private tab']);
        items.push(null);
        items.push(['markdown', 'Copy page as Markdown']);
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
                else if (c === 'mdsel') copyMd(sh, 'sel');
                else if (c === 'markdown') copyMd(sh, 'page');
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
        setTimeout(function () { rawOn(document, 'mousedown', off, true); }, 0);
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
        setTimeout(function () { rawOn(document, 'mousedown', off, true); }, 0);
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
        rawOn(document, 'focusin', function (e) {
            var sh = sh0(); if (!sh) return;
            var t = e.target;
            if (dtPicking || !t || !t.matches || !t.matches(PW_SEL)) { hidePwEye(sh); return; }
            pwEyeField = t;
            var b = sh.getElementById('pweye');
            b.classList.toggle('on', t.type !== 'password');
            b.title = t.type !== 'password' ? 'Hide password' : 'Show password';
            positionPwEye(sh);
        }, true);
        rawOn(document, 'focusout', function () {
            setTimeout(function () {
                if (document.activeElement !== pwEyeField) hidePwEye(sh0());
            }, 130);
        }, true);
        rawOn(document, 'scroll', function () {
            var sh = sh0(); if (sh && pwEyeField) positionPwEye(sh);
        }, true);
        rawOn(window, 'resize', function () {
            var sh = sh0(); if (sh && pwEyeField) positionPwEye(sh);
        });
        rawOn(document, 'mousedown', function (e) {
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
        b.innerHTML = '<span class="top"><span class="k">' + SVG.key + '</span>' +
            '<span class="tx">Save password for <b>' + esc(host) + '</b>' +
            (u ? ' as <b>' + esc(u) + '</b>' : '') + '?</span>' +
            '<button class="xx">' + SVG.close + '</button></span>' +
            '<span class="btns"><button class="nv">Never</button><button class="sv">Save</button></span>';
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

    // ---------------- seatbelt card ----------------
    // An AI agent asked to do something that looks like paying, deleting or sending.
    // The id that authorises it stays in this closure (never in the DOM, never relayed
    // to page scripts) and only a real click answers: a page cannot read or fake it.
    var beltAskId = '', beltAsked = false;
    var BELT_SVG = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3l8 3v6c0 4.5-3.2 8.3-8 9-4.8-.7-8-4.5-8-9V6z"/><path d="M12 8v5"/><circle cx="12" cy="16" r=".6"/></svg>';
    function beltLabel() { return 'Agent asks before risky actions: ' + ((state.belt && state.belt.mode === 'auto') ? 'off' : 'on'); }
    function toggleBelt() { send('beltmode:' + ((state.belt && state.belt.mode === 'auto') ? 'ask' : 'auto')); }
    function onBelt(sh, belt) {
        if (!belt) return;
        state.belt = belt;
        var b = sh.getElementById('askcard'); if (!b) return;
        var ask = belt.ask || [];
        if (!ask.length) { b.style.display = 'none'; b.innerHTML = ''; beltAskId = ''; return; }
        var a = ask[0];
        beltAskId = a.id;
        var tool = String(a.tool || '').replace(/^browser_/, '').replace(/_/g, ' ');
        b.innerHTML = '<span class="top"><span class="k">' + BELT_SVG + '</span><span class="qt">An AI agent wants to <b>' + esc(tool) + '</b>' +
            (a.label ? ' “<b>' + esc(a.label) + '</b>”' : '') + (a.host ? ' on <b>' + esc(a.host) + '</b>' : '') +
            '<br><i>' + esc(a.why || '') + '</i></span></span>' +
            (ask.length > 1 ? '<span class="more">+' + (ask.length - 1) + ' more waiting</span>' : '') +
            '<span class="btns"><button class="dn">Deny</button><button class="st">Allow this site 10 min</button><button class="ok">Allow once</button></span>';
        var wire = function (cls, choice) {
            b.querySelector(cls).addEventListener('click', function (e) {
                if (!e.isTrusted || !beltAskId) return; // script-made clicks never count
                send('askok:' + beltAskId + ':' + choice);
            });
        };
        wire('.dn', 'deny'); wire('.st', 'site'); wire('.ok', 'once');
        b.style.display = 'flex';
    }

    // ---------------- handoff card ("your turn") ----------------
    // The AI agent hit a step only the user can do (login, CAPTCHA, 2FA, payment) and is
    // waiting. Same rules as the seatbelt card: the id stays in this closure and only a
    // real click answers.
    var hoId = '', hoAsked = false;
    var HO_SVG = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"><path d="M18 11V6a2 2 0 0 0-4 0M14 10V4a2 2 0 0 0-4 0v2M10 10.5V6a2 2 0 0 0-4 0v8"/><path d="M18 8a2 2 0 0 1 4 0v6a8 8 0 0 1-8 8h-2c-2.8 0-4.5-.9-5.9-2.4L3.4 16a2 2 0 0 1 3.2-2.4L8 15"/></svg>';
    function onHandoff(sh, ho) {
        if (!ho) return;
        var b = sh.getElementById('hocard'); if (!b) return;
        var ask = ho.ask || [];
        if (!ask.length) { b.style.display = 'none'; b.innerHTML = ''; hoId = ''; return; }
        var a = ask[0];
        hoId = a.id;
        b.innerHTML = '<span class="top"><span class="k">' + HO_SVG + '</span><span class="qt"><b>Your AI agent needs you</b>' + (a.host ? ' on <b>' + esc(a.host) + '</b>' : '') +
            '<br>' + esc(a.message || '') + '<br><i>Do it in the page, then press Done.</i></span></span>' +
            (ask.length > 1 ? '<span class="more">+' + (ask.length - 1) + ' more waiting</span>' : '') +
            '<span class="btns"><button class="st">I can\u2019t do this</button><button class="ok">Done</button></span>';
        var wire = function (cls, choice) {
            b.querySelector(cls).addEventListener('click', function (e) {
                if (!e.isTrusted || !hoId) return; // script-made clicks never count
                send('handoff:' + hoId + ':' + choice);
            });
        };
        wire('.st', 'cant'); wire('.ok', 'done');
        b.style.display = 'flex';
    }

    // ---------------- recorder UI ("show it once") ----------------
    var recAsked = false;
    function recLabel() { return (state.rec && state.rec.on) ? 'Stop recording • ' + state.rec.n + (state.rec.n === 1 ? ' step' : ' steps') : 'Record a flow…'; }
    function toggleRec() {
        if (state.rec && state.rec.on) { if (ROLE === 'bar') send('pageui:reccard'); else openRecCard(sh0()); }
        else send('recstart');
    }
    function onRec(sh, rec, done) {
        if (rec) state.rec = rec;
        if (done) {
            mdToast(done.ok ? 'Saved “' + done.name + '” as a pending flow — approve it in Flows to make it a tool' : (done.err || 'Could not save the flow'), !done.ok);
            closeRecCard(sh);
        }
        paintRec(sh);
        if (!(state.rec && state.rec.on)) closeRecCard(sh);
    }
    function paintRec(sh) {
        var p = sh && sh.getElementById('recpill'); if (!p) return;
        var r = state.rec;
        if (!(r && r.on)) { p.style.display = 'none'; p.innerHTML = ''; return; }
        p.innerHTML = '<i class="rd"></i><span>Recording · ' + r.n + (r.n === 1 ? ' step' : ' steps') + '</span><button type="button">Stop</button>';
        p.querySelector('button').addEventListener('click', function (e) { if (e.isTrusted) openRecCard(sh); });
        p.style.display = 'flex';
    }
    function closeRecCard(sh) {
        var c = sh && sh.getElementById('reccard');
        if (c) { c.style.display = 'none'; c.innerHTML = ''; }
    }
    function recArgGuess(label) {
        return String(label || '').replace(/^Type into\s*/i, '').toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '').slice(0, 24) || 'value';
    }
    function openRecCard(sh) {
        var c = sh && sh.getElementById('reccard'), r = state.rec;
        if (!c || !r || !r.on) return;
        var defName = String(r.title || '').toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '').slice(0, 30);
        var rows = '';
        (r.steps || []).forEach(function (s, i) {
            var extra = '';
            if (s.kind === 'type' && !s.masked) extra = '<label class="pm"><input type="checkbox" data-i="' + i + '"> argument <input class="pn" data-i="' + i + '" value="' + esc(recArgGuess(s.label)) + '"></label>';
            else if (s.masked) extra = '<span class="sec">secret — you give it each run</span>';
            rows += '<div class="st"><span class="n">' + (i + 1) + '</span><span class="rl">' + esc(s.label) +
                (s.value && s.kind !== 'select' ? ' <b>' + esc(String(s.value).slice(0, 40)) + '</b>' : '') + '</span>' + extra + '</div>';
        });
        c.innerHTML = '<div class="hd">Save this flow</div>' +
            '<input class="nm" placeholder="name, e.g. get_invoice" value="' + esc(defName) + '">' +
            '<input class="ds" placeholder="what it does (the agent reads this)">' +
            '<div class="sl">' + (rows || '<div class="st"><span class="rl">Nothing recorded yet — click around the page first.</span></div>') + '</div>' +
            '<div class="hint">Tick “argument” on a typed value to let callers change it. Passwords and card numbers are never stored.</div>' +
            '<span class="btns"><button type="button" class="dn">Discard</button><button type="button" class="kp">Keep recording</button><button type="button" class="ok">Save flow</button></span>';
        c.querySelector('.dn').addEventListener('click', function (e) { if (e.isTrusted) { send('recdiscard'); closeRecCard(sh); } });
        c.querySelector('.kp').addEventListener('click', function (e) { if (e.isTrusted) closeRecCard(sh); });
        c.querySelector('.ok').addEventListener('click', function (e) {
            if (!e.isTrusted) return;
            var nm = c.querySelector('.nm');
            if (!nm.value.trim()) { nm.focus(); nm.style.borderColor = '#ff6b7a'; return; }
            var params = [], seen = {};
            c.querySelectorAll('.pm input[type=checkbox]').forEach(function (cb) {
                if (!cb.checked) return;
                var i = +cb.getAttribute('data-i'), pn = c.querySelector('.pn[data-i="' + i + '"]');
                var nme = String(pn && pn.value || '').toLowerCase().replace(/[^a-z0-9_]+/g, '_').replace(/^_+|_+$/g, '') || 'value';
                while (seen[nme]) nme += '_' + i;
                seen[nme] = 1;
                params.push({ name: nme, example: r.steps[i].value, description: 'Value for: ' + r.steps[i].label });
            });
            send('recstop:' + JSON.stringify({ name: nm.value.trim(), description: c.querySelector('.ds').value.trim(), params: params }));
        });
        c.style.display = 'flex';
        var nmEl = c.querySelector('.nm'); try { nmEl.focus(); nmEl.select(); } catch (e0) { }
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

        rawOn(document, 'focusin', function (e) {
            var sh = sh0(); if (!sh) return;
            var t = e.target;
            if (!t || !t.matches) { hidePwDrop(sh); return; }
            var isPw = t.matches(PW_SEL);
            if (isPw) maybePwFetch();
            if ((isPw || t.matches(PW_USER_SEL)) && pwCreds && pwCreds.length && pwPartnerFor(t))
                showPwDrop(sh, t);
            else hidePwDrop(sh);
        }, true);
        rawOn(document, 'focusout', function () {
            setTimeout(function () {
                var ae = document.activeElement;
                if (ae && ae.matches && (ae.matches(PW_SEL) || ae.matches(PW_USER_SEL))) return;
                var sh = sh0(); if (sh) hidePwDrop(sh);
            }, 130);
        }, true);
        rawOn(document, 'scroll', function () {
            var sh = sh0(); if (sh && pwDropFor) positionPwDrop(sh);
        }, true);
        rawOn(window, 'resize', function () {
            var sh = sh0(); if (sh && pwDropFor) positionPwDrop(sh);
        });
        rawOn(document, 'keydown', function (e) {
            if (e.key === 'Escape') { var sh = sh0(); if (sh) hidePwDrop(sh); }
        }, true);
        rawOn(document, 'mousedown', function (e) {
            var t = e.target;
            if (t && (t.id === HOST_ID || t === pwDropFor)) return;
            var sh = sh0(); if (sh) hidePwDrop(sh);
        }, true);

        // Form submits → save candidate (host decides whether to prompt).
        rawOn(document, 'submit', function (e) {
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
        rawOn(window, 'pagehide', function () {
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
        else { dtTab = 'ins'; syncDtTabs(sh, true); send('dtstate:ins'); }
        ensureIns(sh);
        insSelect(sh, el);
    }

    function wirePageMenu() {
        rawOn(document, 'contextmenu', function (e) {
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
        rawOn(document, 'mouseover', function (e) {
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
        rawOn(document, 'scroll', function () {
            if (statusEl) statusEl.classList.remove('on');
            lastLink = null;
        }, true);
        // Mouse leaving the window hides the bubble.
        rawOn(document, 'mouseleave', function () {
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
        rawOn(document, 'auxclick', function (e) {
            if (e.button !== 1) return;
            var href = linkTarget(e);
            if (!href) return;
            e.preventDefault();
            send('newtab:' + href);
        }, true);
        // Ctrl/Cmd+click — the classic open-in-new-tab gesture.
        rawOn(document, 'click', function (e) {
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
        if (ROLE === 'bar') { send('pageui:find'); return; }
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

    // ⋮ menu layout: quick tiles, a zoom stepper, then grouped sections.
    // Rarely used entries live behind "More tools ›" so the main panel stays short.
    function menuTiles() {
        return [
            ['newtab', SVG.plus, 'New tab', 'Ctrl+T'],
            ['newpriv', SVG.mask, 'Private', 'Ctrl+Shift+N'],
            ['bm', state.bm ? SVG.starF : SVG.star, state.bm ? 'Bookmarked' : 'Bookmark', 'Ctrl+D'],
            ['find', SVG.mag, 'Find', 'Ctrl+F'],
        ];
    }
    function menuSections() {
        var at = activeTab();
        var secs = [
            ['Tabs', [
                ['dupe', 'Duplicate tab', 'Ctrl+Shift+D'],
                ['pin', at && at.pin ? 'Unpin tab' : 'Pin tab', 'Ctrl+Alt+P'],
                ['mute', docTabMuted() ? 'Unmute tab' : 'Mute tab', 'Ctrl+Alt+M'],
                ['reopen', 'Reopen closed tab', 'Ctrl+Shift+T'],
                ['closeothers', 'Close other tabs', 'Ctrl+Alt+W'],
            ]],
            ['This page', [
                ['copyurl', 'Copy link address', 'Ctrl+Shift+C'],
                ['markdown', 'Copy page as Markdown', 'Ctrl+Shift+M'],
                ['shot', 'Screenshot (full page)', 'Ctrl+Shift+S'],
                ['rewind', 'Rewind — page time travel', 'Ctrl+Shift+Y'],
                ['pip', pipActive() ? 'Exit picture-in-picture' : 'Picture-in-picture', 'Ctrl+Shift+P'],
                ['print', 'Print', 'Ctrl+P'],
            ]],
            ['AI agent', [
                ['agent', (state.ag && state.ag.p) ? 'Resume agent' : 'Pause agent', 'Ctrl+Shift+.'],
                ['rec', recLabel(), 'Ctrl+Alt+R'],
                ['flows', 'Flows & activity', 'Ctrl+Shift+F'],
                ['belt', beltLabel(), ''],
            ]],
            ['Library', [
                ['history', 'History', 'Ctrl+H'],
                ['dlp', 'Downloads', 'Ctrl+J'],
                ['pw', 'Passwords', 'Ctrl+Shift+O'],
            ]],
            ['Settings', [
                ['se', 'Search engine', seName() + ' ›'],
                ['import', 'Import browser data', ''],
            ]],
        ];
        return secs;
    }
    function menuMore() {
        var items = [];
        if (state.sess > 0) items.push(['restore', 'Restore last session (' + state.sess + ')', '']);
        items.push(['pastego', 'Paste and go', 'Ctrl+Shift+V']);
        items.push(['hardreload', 'Hard reload (ignore cache)', 'Ctrl+Shift+R']);
        items.push(['shotview', 'Screenshot (visible area)', '']);
        items.push(['viewsource', 'View source', 'Ctrl+U']);
        items.push(['devtools', 'Developer tools', 'F12']);
        items.push(['shortcuts', 'Keyboard shortcuts', 'Ctrl+/']);
        items.push(['fullscreen', 'Fullscreen', 'F11 · Ctrl+Alt+F']);
        items.push(['topmost', 'Always on top', state.topmost ? '✓' : 'Ctrl+Alt+T']);
        return items;
    }

    function showMenu(sh) {
        var m = sh.getElementById('menu');
        if (m.classList.contains('on')) { m.classList.remove('on'); return; }
        var html = '', mi = 0;
        html += '<div class="mtiles">';
        menuTiles().forEach(function (t) {
            html += '<div class="mt" style="--i:' + (mi++) + '" data-c="' + t[0] + '" title="' + t[2] + ' · ' + t[3] + '">' + t[1] + '<span>' + t[2] + '</span></div>';
        });
        html += '</div>';
        html += '<div class="mzoom" style="--i:' + (mi++) + '"><span class="mzl">Zoom</span>' +
            '<div class="mzc"><b data-z="zoomout" title="Zoom out · Ctrl+−">−</b>' +
            '<em data-z="zoomreset" title="Reset · Ctrl+0">' + state.zoom + '%</em>' +
            '<b data-z="zoomin" title="Zoom in · Ctrl++">+</b></div>' +
            '<div class="mzf" data-c="fullscreen" title="Fullscreen · F11">⛶</div></div>';
        menuSections().forEach(function (sec) {
            html += '<div class="mlabel">' + sec[0] + '</div>';
            sec[1].forEach(function (it) {
                html += '<div class="mi" style="--i:' + (mi++) + '" data-c="' + it[0] + '"><span>' + it[1] + '</span><span class="sc">' + it[2] + '</span></div>';
            });
        });
        html += '<div class="msep"></div>';
        html += '<div class="mi" style="--i:' + (mi++) + '" data-c="more"><span>More tools</span><span class="sc">›</span></div>';
        html += '<div class="mfoot" style="--i:' + (mi++) + '"><span data-c="palette">Command palette <i>' + (IS_MAC ? '⌘K' : 'Ctrl+K') + '</i></span><span data-c="help">How to use <i>F1</i></span></div>';
        m.innerHTML = html;
        m.classList.add('on');
        m.querySelectorAll('[data-c]').forEach(function (el) {
            el.addEventListener('mousedown', function (e) {
                e.preventDefault(); e.stopPropagation();
                var c = el.getAttribute('data-c');
                if (c === 'se') { showSeFly(sh, m, el); return; }
                if (c === 'more') { showMoreFly(sh, m, el); return; }
                m.classList.remove('on');
                var sm0 = sh.getElementById('smenu'); if (sm0) sm0.classList.remove('on');
                menuCmd(sh, c);
            });
        });
        // zoom buttons keep the menu open and refresh the percentage
        m.querySelectorAll('[data-z]').forEach(function (el) {
            el.addEventListener('mousedown', function (e) {
                e.preventDefault(); e.stopPropagation();
                m.classList.add('on');
                send(el.getAttribute('data-z'));
                setTimeout(function () {
                    var z = m.querySelector('[data-z="zoomreset"]');
                    if (z) z.textContent = state.zoom + '%';
                }, 160);
            });
        });
        var off = function (e) {
            var t = e && e.target;
            if (t && t.closest && t.closest('#smenu')) return;
            m.classList.remove('on');
            var sm = sh.getElementById('smenu'); if (sm) sm.classList.remove('on');
            document.removeEventListener('mousedown', off, true);
        };
        setTimeout(function () { rawOn(document, 'mousedown', off, true); }, 0);
    }

    // "More tools" flyout — same panel as the search-engine flyout.
    function showMoreFly(sh, m, row) {
        var sm = sh.getElementById('smenu');
        if (!sm) return;
        m.classList.add('on');
        var mr = m.getBoundingClientRect(), rr = row.getBoundingClientRect();
        var html = '';
        menuMore().forEach(function (it, i) {
            html += '<div class="smi" style="--i:' + i + '" data-c="' + it[0] + '"><span style="flex:1">' + it[1] + '</span><span class="sc">' + it[2] + '</span></div>';
        });
        sm.innerHTML = html;
        sm.style.top = Math.max(40, Math.min(rr.top - 5, innerHeight - 20 - 36 * menuMore().length)) + 'px';
        sm.style.right = (innerWidth - mr.left + 4) + 'px';
        sm.classList.add('on');
        sm.querySelectorAll('.smi').forEach(function (el) {
            el.addEventListener('mousedown', function (e) {
                e.preventDefault(); e.stopPropagation();
                sm.classList.remove('on'); m.classList.remove('on');
                menuCmd(sh, el.getAttribute('data-c'));
            });
        });
        var off = function (e) {
            var t = e.target;
            if (t.closest && (t.closest('#smenu') || t.closest('#menu'))) return;
            sm.classList.remove('on'); m.classList.remove('on');
            document.removeEventListener('mousedown', off, true);
        };
        setTimeout(function () { rawOn(document, 'mousedown', off, true); }, 0);
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
        setTimeout(function () { rawOn(document, 'mousedown', off, true); }, 0);
    }

    function menuCmd(sh, c) {
        // On the start-page overlay, page-scoped actions would hit the hidden document.
        if (state.ov && (c === 'find' || c === 'print' || c === 'viewsource' || c === 'copyurl' || c === 'markdown' || c === 'mdsel' || c === 'mdsmart' || c === 'bm' || c === 'rewind')) return;
        if (c === 'palette') { openPal(sh); return; }
        if (c === 'find') { openFind(sh); return; }
        if (c === 'pip') { togglePip(); return; }
        if (c === 'mute') { var at = activeTab(); if (at) send('mute:' + at.id); return; }
        if (c === 'pin') { var atPin = activeTab(); if (atPin) send('pin:' + atPin.id); return; }
        if (c === 'dupe') { var atDupe = activeTab(); if (atDupe) send('dupetab:' + atDupe.id); return; }
        if (c === 'closeothers') { var atCO = activeTab(); if (atCO) send('closeothers:' + atCO.id); return; }
        if (c === 'history') { send('navigate:about:history'); return; }
        if (c === 'pw') { send('navigate:about:passwords'); return; }
        if (c === 'flows') { send('navigate:about:flows'); return; }
        if (c === 'agent') { toggleAgent(); return; }
        if (c === 'belt') { toggleBelt(); return; }
        if (c === 'rec') { toggleRec(); return; }
        if (c === 'import') { send('navigate:about:import'); return; }
        if (c === 'devtools') { toggleDt(sh); return; }
        if (c === 'dlp') { toggleDl(sh); return; }
        if (c === 'rewind') { if (ROLE === 'bar') send('pageui:rewind'); else toggleRw(sh); return; }
        if (c === 'print') { if (ROLE === 'bar') { send('pageui:print'); return; } try { window.print(); } catch (e) { } return; }
        if (c === 'viewsource') { send('newtab:view-source:' + curUrl()); return; }
        if (c === 'help') { send('newtab:about:help'); return; }
        if (c === 'shortcuts') { send('newtab:about:shortcuts'); return; }
        if (c === 'newpriv') { openNewTab(true); return; }
        if (c === 'newtab') { openNewTab(false); return; }
        if (c === 'copyurl') { copyText(curUrl()); return; }
        if (c === 'markdown' || c === 'mdsel' || c === 'mdsmart') {
            // the page's DOM lives in the page view — relay there when we are the toolbar view
            if (ROLE === 'bar') send('pageui:' + c); else copyMd(sh, c === 'markdown' ? 'page' : c === 'mdsel' ? 'sel' : 'smart');
            return;
        }
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
            ['dupe', 'Duplicate tab', 'copy clone same tab', IS_MAC ? '\u21e7\u2318D' : 'Ctrl+Shift+D'],
            ['pin', a && a.pin ? 'Unpin tab' : 'Pin tab', 'pin keep sticky tab', IS_MAC ? '\u2303\u2325P' : 'Ctrl+Alt+P'],
            ['closeothers', 'Close other tabs', 'close all tabs except this one', IS_MAC ? '\u2303\u2325W' : 'Ctrl+Alt+W'],
            ['history', 'Open history', 'browsing history visited', IS_MAC ? '\u2318H' : 'Ctrl+H'],
            ['import', 'Import browser data', 'bookmarks passwords chrome safari edge firefox', ''],
            ['pw', 'Passwords', 'saved logins credentials vault autofill signin', IS_MAC ? '\u21e7\u2318O' : 'Ctrl+Shift+O'],
            ['flows', 'Flows & activity', 'flows macros automation agent mcp tool calls log history record replay', IS_MAC ? '\u21e7\u2318F' : 'Ctrl+Shift+F'],
            ['agent', (state.ag && state.ag.p) ? 'Resume agent' : 'Pause agent', 'ai agent mcp stop halt pause resume continue', IS_MAC ? '\u21e7\u2318.' : 'Ctrl+Shift+.'],
            ['rec', recLabel(), 'record flow macro teach show once automation demonstrate steps learn', IS_MAC ? '\u2303\u2325R' : 'Ctrl+Alt+R'],
            ['belt', beltLabel(), 'ai agent mcp safety approve ask confirm risky pay delete send seatbelt permission', ''],
            ['rewind', 'Rewind — scrub back through this page', 'rewind time travel history snapshot previous state undo', IS_MAC ? '\u21e7\u2318Y' : 'Ctrl+Shift+Y'],
            ['find', 'Find in page', 'search text', IS_MAC ? '\u2318F' : 'Ctrl+F'],
            ['bm', state.bm ? 'Remove bookmark' : 'Bookmark this page', 'star favorite save', IS_MAC ? '\u2318D' : 'Ctrl+D'],
            ['copyurl', 'Copy page link', 'url address clipboard', ''],
            ['pastego', 'Paste and go', 'clipboard open url search paste', IS_MAC ? '⇧⌘V' : 'Ctrl+Shift+V'],
            ['hardreload', 'Hard reload — ignore cache', 'refresh reload cache bust force', IS_MAC ? '⇧⌘R' : 'Ctrl+Shift+R'],
            ['markdown', 'Copy page as Markdown', 'markdown md copy text article readable ai llm clipboard', IS_MAC ? '\u21e7\u2318M' : 'Ctrl+Shift+M'],
            ['mdsel', 'Copy selection as Markdown', 'markdown md selection copy text clipboard', ''],
            ['pip', pipActive() ? 'Exit picture-in-picture' : 'Picture-in-picture', 'pip video float', ''],
            ['mute', a && a.mute ? 'Unmute tab' : 'Mute tab', 'sound audio volume silence', IS_MAC ? '⌃⌥M' : 'Ctrl+Alt+M'],
            ['zoomin', 'Zoom in', 'bigger larger magnify', ''],
            ['zoomout', 'Zoom out', 'smaller', ''],
            ['zoomreset', 'Reset zoom', '100', ''],
            ['print', 'Print', 'pdf paper', ''],
            ['shot', 'Screenshot (full page)', 'capture scrolling long page image png save', IS_MAC ? '\u21e7\u2318S' : 'Ctrl+Shift+S'],
            ['shotview', 'Screenshot (visible area)', 'capture screen image png save', ''],
            ['viewsource', 'View source', 'html code', ''],
            ['devtools', 'Developer tools', 'console network inspect cookies', 'F12'],
            ['dlp', 'Downloads', 'downloaded files save shelf', IS_MAC ? '⇧⌘J' : 'Ctrl+J'],
            ['fullscreen', 'Toggle fullscreen', 'full screen', IS_MAC ? 'F11 · ⌃⌘F' : 'F11 · Ctrl+Alt+F'],
            ['topmost', 'Toggle always on top', 'pin window float', IS_MAC ? '⌃⌥T' : 'Ctrl+Alt+T'],
            ['shortcuts', 'Keyboard shortcuts', 'hotkeys keys bindings cheat sheet', IS_MAC ? '⌘/' : 'Ctrl+/'],
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
        if (SPLIT && ROLE === 'page') { send('barui:pal'); return; }
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

    // ---------------- copy page / selection as Markdown ----------------
    // The conversion lives in brain.js (window.__brain.markdownInfo) so the
    // browser_markdown MCP tool returns exactly what the user would paste.
    var mdToastT = 0;
    function mdToast(msg, bad) {
        var sh = sh0(); if (!sh) return;
        var t = sh.getElementById('mdtoast');
        if (!t) {
            t = document.createElement('div'); t.id = 'mdtoast';
            t.style.cssText = 'position:fixed;left:50%;bottom:34px;transform:translate(-50%,12px);max-width:min(580px,88vw);padding:11px 18px;' +
                'border-radius:14px;background:rgba(16,16,30,.96);color:#ececf8;border:1px solid rgba(255,255,255,.14);' +
                'box-shadow:0 14px 40px rgba(0,0,0,.5);font:600 13px/1.4 "Segoe UI Variable Text",system-ui,-apple-system,sans-serif;' +
                'z-index:2147483647;opacity:0;pointer-events:none;transition:opacity .2s,transform .2s;text-align:center;';
            sh.appendChild(t);
        }
        t.textContent = msg;
        t.style.borderColor = bad ? 'rgba(255,107,122,.55)' : 'rgba(111,224,198,.5)';
        t.style.opacity = '1'; t.style.transform = 'translate(-50%,0)';
        clearTimeout(mdToastT);
        mdToastT = setTimeout(function () { t.style.opacity = '0'; t.style.transform = 'translate(-50%,12px)'; }, bad ? 4200 : 2600);
    }
    function copyTextCb(text, cb) {
        var done = false;
        function fin(ok) { if (!done) { done = true; cb(ok); } }
        function legacy() {
            try {
                var ta = document.createElement('textarea');
                ta.value = text; ta.style.cssText = 'position:fixed;opacity:0;top:0;left:0';
                document.body.appendChild(ta); ta.select();
                var ok = document.execCommand('copy'); ta.remove(); fin(!!ok);
            } catch (e) { fin(false); }
        }
        try { navigator.clipboard.writeText(text).then(function () { fin(true); }, legacy); }
        catch (e) { legacy(); }
    }
    function mdFallback(text) { // clipboard blocked: show the text, pre-selected
        var sh = sh0(); if (!sh) return;
        var old = sh.getElementById('mdfb'); if (old) old.remove();
        var w = document.createElement('div'); w.id = 'mdfb';
        w.style.cssText = 'position:fixed;inset:0;z-index:2147483647;background:rgba(5,5,12,.62);display:grid;place-items:center;font:13px system-ui,-apple-system,sans-serif;';
        w.innerHTML = '<div style="width:min(660px,90vw);background:#12121f;color:#ececf8;border:1px solid rgba(255,255,255,.14);border-radius:16px;padding:16px;box-shadow:0 30px 80px rgba(0,0,0,.6)">' +
            '<div style="font-weight:700;font-size:14px;margin-bottom:6px">Copy this Markdown</div>' +
            '<div style="color:#8a8aa6;margin-bottom:10px">Automatic copy was blocked here. The text is selected — press ' + (IS_MAC ? '⌘C' : 'Ctrl+C') + '.</div>' +
            '<textarea readonly spellcheck="false" style="width:100%;height:280px;resize:vertical;background:#0b0b14;color:#d8d8f0;border:1px solid rgba(255,255,255,.1);border-radius:10px;padding:10px;font:12px/1.5 ui-monospace,Menlo,monospace"></textarea>' +
            '<div style="text-align:right;margin-top:10px"><button style="font:600 13px system-ui;color:#fff;background:#4b46c9;border:0;border-radius:9px;padding:8px 18px;cursor:pointer">Close</button></div></div>';
        sh.appendChild(w);
        var ta = w.querySelector('textarea'); ta.value = text;
        var close = function () { w.remove(); };
        w.querySelector('button').addEventListener('click', close);
        w.addEventListener('mousedown', function (e) { if (e.target === w) close(); });
        ['keydown', 'keyup', 'keypress'].forEach(function (ev) {
            ta.addEventListener(ev, function (e) { e.stopPropagation(); if (ev === 'keydown' && e.key === 'Escape') close(); });
        });
        ta.focus(); ta.select();
    }
    function copyMd(sh, mode) { // mode: 'page' | 'sel' | 'smart' (selection if there is one, else page)
        if (state.ov) return;
        var B = window.__brain;
        if (!B || !B.markdownInfo) { mdToast('Page tools aren’t loaded on this page — reload it and try again.', true); return; }
        var s = null; try { s = window.getSelection(); } catch (e) { }
        var has = !!(s && !s.isCollapsed && String(s).trim());
        var useSel = mode === 'sel' || (mode === 'smart' && has);
        if (useSel && !has) { mdToast('Select some text first.', true); return; }
        var r;
        try { r = B.markdownInfo(null, { selection: useSel }); }
        catch (e) { mdToast('Couldn’t convert this page: ' + ((e && e.message) || e), true); return; }
        if (!r || r.empty || !r.md) { mdToast((r && r.empty) || 'Nothing to copy on this page.', true); return; }
        var tok = Math.max(1, Math.round(r.chars / 4));
        var msg = 'Copied ' + (useSel ? 'selection' : 'page') + ' as Markdown · ' + r.chars.toLocaleString() + ' characters (~' +
            (tok >= 1000 ? (tok / 1000).toFixed(1) + 'k' : tok) + ' tokens)' + (r.truncated ? ' · truncated' : '');
        copyTextCb(r.md, function (ok) { if (ok) mdToast(msg); else mdFallback(r.md); });
    }

    // ---------------- scroll position reporting ----------------
    // Logical tabs reload on switch — the host restores our saved position
    // after load, like a real browser preserves scroll on reload.

    function wireScrollReport() {
        var lastFixedCheck = 0, bandT = 0;
        rawOn(window, 'scroll', function (e) {
            if (e.target !== document) return; // window scroll only, not nested scrollers
            var n = Date.now();
            // Bands that pin mid-scroll (YouTube's filter chips): cheap pass,
            // at most every ~50ms with a trailing run so the last position counts.
            if (!bandT) bandT = setTimeout(function () {
                bandT = 0;
                try { adjustSticky(window.innerWidth, window.innerHeight); ytMastheadFill(); } catch (e2) { }
            }, 50);
            // Sticky headers pin to the viewport while scrolling — re-scan the
            // covered strip so newly-pinned ones shift under the bar too.
            if (n - lastFixedCheck > 350) { lastFixedCheck = n; adjustFixed(); }
            if (n - lastScrollReport < 300) return;
            lastScrollReport = n;
            send('scroll:' + location.href + '|' + Math.round(window.scrollX || 0) + ',' + Math.round(window.scrollY || 0));
        }, { passive: true, capture: true });
        rawOn(window, 'resize', adjustFixed, { passive: true });
    }

    // ---------------- tool capsule: highlight + tooltips ----------------

    function fmtSc(k) {
        if (!k || !IS_MAC) return k || '';
        if (/^Alt\+/.test(k)) return '\u2325' + k.slice(4);
        if (!/Ctrl\+/.test(k)) return k;
        var shift = /Shift\+/.test(k);
        return (shift ? '\u21e7' : '') + '\u2318' + k.replace(/Ctrl\+|Shift\+/g, '');
    }
    function setTip(el, text, sc) {
        el.setAttribute('data-tip', text);
        if (sc) el.setAttribute('data-sc', sc); else el.removeAttribute('data-sc');
        el.removeAttribute('title'); // ours replaces the slow native tooltip
    }

    function wireTools(sh) {
        wireHoverGroup(sh, 'tools', 'hl', '.tb');
        wireHoverGroup(sh, 'nav', 'nhl', '.nav-b');
    }

    // One glass pill with a highlight that glides between its buttons and a
    // tooltip carrying the shortcut — shared by the left (nav) and right (tools) capsules.
    function wireHoverGroup(sh, boxId, hlId, btnSel) {
        var box = sh.getElementById(boxId), hl = sh.getElementById(hlId), tip = sh.getElementById('ttip');
        if (!box || !hl || !tip) return;
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
            var b = e.target.closest && e.target.closest(btnSel);
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

    // ---------------- agent indicator + pause ----------------
    // The host pushes {b:40, ag:{p,n,t,s,k}} around every MCP tool call.
    // "Working" stays lit ~2s after the last call ends so quick bursts read
    // as one continuous run instead of flickering.
    var agRunUntil = 0, agTimer = 0, agKey = '';
    function agLabel(t) { return String(t || '').replace(/^browser_/, '').replace(/^flow_/, 'flow: ').replace(/_/g, ' '); }
    function setAgent(sh, ag) {
        var o = state.ag || {};
        ag = ag || { p: false, n: 0, t: '', s: false, k: 0 };
        state.ag = ag;
        if (ag.n > 0) agRunUntil = Date.now() + 2200;
        else if (o.n > 0) agRunUntil = Date.now() + 2000;
        paintAgent(sh);
        if (ag.p && ag.k > (o.k || 0)) {
            var b = sh && sh.getElementById('agbtn');
            if (b) { b.classList.remove('ag-shake'); void b.offsetWidth; b.classList.add('ag-shake'); }
        }
        clearTimeout(agTimer);
        if (!ag.n && agRunUntil > Date.now()) agTimer = setTimeout(function () { paintAgent(sh0()); }, agRunUntil - Date.now() + 40);
    }
    function paintAgent(sh) {
        if (!sh) return;
        var b = sh.getElementById('agbtn'); if (!b) return;
        var ag = state.ag || {};
        var show = !!(ag.s || ag.p);
        var run = !ag.p && (ag.n > 0 || agRunUntil > Date.now());
        b.classList.toggle('ag-on', show);
        b.classList.toggle('ag-run', run);
        b.classList.toggle('ag-pause', !!ag.p);
        var bar = sh.querySelector('.bar');
        if (bar) { bar.classList.toggle('agrun', run); bar.classList.toggle('agpause', !!ag.p); }
        var key = (ag.p ? 'p' + (ag.k || 0) : 'a');
        if (key !== agKey) {
            agKey = key;
            b.innerHTML = (ag.p ? SVG.pause : SVG.bot) + (ag.p ? (ag.k ? '<i class="agk">' + Math.min(ag.k, 99) + '</i>' : '') : '<i class="agd"></i>');
        }
        var tip = ag.p ? 'Agent paused \u2014 click to resume' + (ag.k ? ' (' + ag.k + ' blocked)' : '')
            : run ? 'Agent working' + (ag.t ? ': ' + agLabel(ag.t) : '') + ' \u2014 click to pause'
            : 'Agent connected \u2014 click to pause';
        setTip(b, tip, 'Ctrl+Shift+.');
        // refresh a tooltip that is already showing for this button
        var tt = sh.getElementById('ttip');
        if (tt && tt.classList.contains('on') && b.matches(':hover')) { var sp = tt.querySelector('span'); if (sp) sp.textContent = tip; }
    }
    function toggleAgent() { send('agtoggle'); }

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
        if (ROLE === 'bar') { send('pageui:pip'); return; }
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
            rawOn(document, ev, function (e) {
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
        if (SPLIT && ROLE === 'page') { send('barui:dl'); return; }
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
            setTimeout(function () { rawOn(document, 'mousedown', off, true); }, 0);
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

    // Editable key bindings. KEYS = default gesture→action rows; ACT = the
    // action bodies. A gesture is "mod+alt+shift+<key>" (mod = Ctrl on
    // Windows/Linux, Cmd on macOS; key = e.key.toLowerCase(), '+' and ' '
    // encoded as "plus"/"space"). state.keys[id] — user overrides pushed in
    // the b:9 payload — replaces ALL of an action's defaults with one
    // gesture; "" marks it unbound. Reset restores the defaults. Row flags:
    //   f — never fires inside editable fields (AltGr / paste-plain safety)
    //   o — eaten but ignored while the start-page overlay is up
    //   m/w — macOS-only / Windows-only default
    //   v — forks on modifiers: binds every modifier combo, run() reads them
    // Synced with the GROUPS table in shortcuts.html — same action ids.
    var KEYS = [
        { id: 'pal',      g: 'mod+k' },
        { id: 'pal',      g: 'mod+shift+a' },
        { id: 'omnibox',  g: 'mod+l' },
        { id: 'omnibox',  g: 'mod+e' },
        { id: 'omnibox',  g: 'f6' },
        { id: 'omnibox',  g: 'alt+d', f: 1 },
        { id: 'altf',     g: 'mod+alt+f' },
        { id: 'menu',     g: 'alt+f', f: 1 },
        // tabs & windows
        { id: 'newtab',   g: 'mod+t' },
        { id: 'privtab',  g: 'mod+shift+n' },
        { id: 'closetab', g: 'mod+w' },
        { id: 'reopen',   g: 'mod+shift+t' },
        { id: 'dupe',     g: 'mod+shift+d' },
        { id: 'pin',      g: 'mod+alt+p', f: 1 },
        { id: 'mute',     g: 'mod+alt+m', f: 1 },
        { id: 'closeothers', g: 'mod+alt+w', f: 1 },
        { id: 'wclose',   g: 'mod+shift+w' },
        { id: 'wclose',   g: 'mod+shift+q' },
        { id: 'nexttab',  g: 'mod+tab' },
        { id: 'nexttab',  g: 'mod+pagedown' },
        { id: 'nexttab',  g: 'mod+alt+arrowright', m: 1 },
        { id: 'prevtab',  g: 'mod+shift+tab' },
        { id: 'prevtab',  g: 'mod+pageup' },
        { id: 'prevtab',  g: 'mod+alt+arrowleft', m: 1 },
        { id: 'movetabR', g: 'mod+shift+pagedown' },
        { id: 'movetabL', g: 'mod+shift+pageup' },
        // navigation
        { id: 'back',     g: 'alt+arrowleft' },
        { id: 'back',     g: 'mod+[' },
        { id: 'forward',  g: 'alt+arrowright' },
        { id: 'forward',  g: 'mod+]' },
        { id: 'reload',   g: 'mod+r' },
        { id: 'reload',   g: 'f5', v: 1 },
        { id: 'hardreload', g: 'mod+shift+r' },
        { id: 'home',     g: 'alt+home' },
        { id: 'history',  g: 'mod+h' },
        { id: 'viewsource', g: 'mod+u', o: 1 },
        // clipboard & page
        { id: 'pastego',  g: 'mod+shift+v', f: 1 },
        { id: 'copylink', g: 'mod+shift+c', o: 1 },
        { id: 'markdown', g: 'mod+shift+m' },
        { id: 'print',    g: 'mod+p', o: 1 },
        // find
        { id: 'find',     g: 'mod+f', o: 1 },
        { id: 'findnext', g: 'mod+g', o: 1 },
        { id: 'findprev', g: 'mod+shift+g', o: 1 },
        { id: 'findnav',  g: 'f3', v: 1 },
        // tools & panels
        { id: 'downloads', g: 'mod+j' },
        { id: 'shiftj',   g: 'mod+shift+j' },
        { id: 'dtconj',   g: 'mod+alt+j', f: 1, m: 1 },
        { id: 'passwords', g: 'mod+shift+o' },
        { id: 'bookmark', g: 'mod+d' },
        { id: 'pip',      g: 'mod+shift+p' },
        { id: 'shot',     g: 'mod+shift+s' },
        { id: 'rewind',   g: 'mod+shift+y' },
        { id: 'zoomin',   g: 'mod+=' },
        { id: 'zoomin',   g: 'mod+shift+plus' },
        { id: 'zoomin',   g: 'mod+plus' },
        { id: 'zoomout',  g: 'mod+-' },
        { id: 'zoomreset', g: 'mod+0' },
        // ai agent
        { id: 'agentpause', g: 'mod+shift+.' },
        { id: 'agentpause', g: 'mod+shift+>' },
        { id: 'recflow',  g: 'mod+alt+r', f: 1 },
        { id: 'flows',    g: 'mod+shift+f' },
        // devtools & window
        { id: 'devtools', g: 'f12' },
        { id: 'devtools', g: 'mod+shift+i' },
        { id: 'fullscreen', g: 'f11' },
        { id: 'topmost',  g: 'mod+alt+t', f: 1 },
        { id: 'help',     g: 'f1' },
        { id: 'shortcutspage', g: 'mod+/' },
        { id: 'shortcutspage', g: 'mod+shift+?' },
    ];

    var ACT = {
        pal:       function (e, sh) { openPal(sh); },
        omnibox:   function (e, sh) { startEditing(sh); },
        altf:      function (e, sh, f) { if (IS_MAC) startEditing(sh); else if (!f) send('fullscreen'); },
        menu:      function (e, sh) { if (SPLIT && ROLE === 'page') send('barui:menu'); else showMenu(sh); },
        newtab:    function () { openNewTab(false); },
        privtab:   function () { openNewTab(true); },
        closetab:  function () { var a = activeTab(); if (a) closeTabLocal(a.id); },
        reopen:    function () { send('reopen'); },
        dupe:      function () { var a = activeTab(); if (a) send('dupetab:' + a.id); },
        pin:       function () { var a = activeTab(); if (a) send('pin:' + a.id); },
        mute:      function () { var a = activeTab(); if (a) send('mute:' + a.id); },
        closeothers: function () { var a = activeTab(); if (a) send('closeothers:' + a.id); },
        wclose:    function () { send('wclose'); },
        nexttab:   function () { cycleTab(1); },
        prevtab:   function () { cycleTab(-1); },
        movetabR:  function () { moveActiveTab(1); },
        movetabL:  function () { moveActiveTab(-1); },
        back:      function () { send('back'); },
        forward:   function () { send('forward'); },
        reload:    function (e) { send((e.ctrlKey || e.metaKey || e.shiftKey) ? 'hardreload' : 'reload'); },
        hardreload: function () { send('hardreload'); },
        home:      function () { send('home'); },
        history:   function () { send('navigate:about:history'); },
        viewsource: function () { send('newtab:view-source:' + curUrl()); },
        pastego:   function () { send('pastego'); },
        copylink:  function () { copyText(curUrl()); },
        markdown:  function (e, sh) { menuCmd(sh, 'mdsmart'); },
        print:     function () { if (SPLIT && ROLE === 'bar') send('pageui:print'); else { try { window.print(); } catch (err) { } } },
        find:      function (e, sh) { openFind(sh); },
        findnext:  function (e, sh) { if (findOpen) findStep(sh, false); else openFind(sh); },
        findprev:  function (e, sh) { if (findOpen) findStep(sh, true); else openFind(sh); },
        findnav:   function (e, sh) { if (findOpen) findStep(sh, e.shiftKey); else openFind(sh); },
        downloads: function (e, sh) { toggleDl(sh); },
        shiftj:    function (e, sh) { if (IS_MAC) toggleDl(sh); else toggleDt(sh, 'con'); },
        dtconj:    function (e, sh) { toggleDt(sh, 'con'); },
        passwords: function () { send('navigate:about:passwords'); },
        bookmark:  function () { send('bm'); },
        pip:       function () { if (pipVideo()) togglePip(); },
        shot:      function () { send('shot'); },
        rewind:    function (e, sh) { menuCmd(sh, 'rewind'); },
        zoomin:    function () { send('zoomin'); },
        zoomout:   function () { send('zoomout'); },
        zoomreset: function () { send('zoomreset'); },
        agentpause: function () { toggleAgent(); },
        recflow:   function () { toggleRec(); },
        flows:     function () { send('navigate:about:flows'); },
        devtools:  function (e, sh) { toggleDt(sh); },
        fullscreen: function () { send('fullscreen'); },
        topmost:   function () { send('topmost'); },
        help:      function () { send('newtab:about:help'); },
        shortcutspage: function () { send('newtab:about:shortcuts'); },
    };

    // ---- gesture machinery ----
    var BINDS = Object.create(null);
    var VARY_ALL = ['', 'mod+', 'alt+', 'shift+', 'mod+alt+', 'mod+shift+', 'alt+shift+', 'mod+alt+shift+'];
    var MOD_RE = /^(mod|alt|shift|control|meta)$/i;

    function gestureOf(e) {
        var k = e.key;
        if (!k || MOD_RE.test(k)) return null;
        if (k === ' ') k = 'space'; else if (k === '+') k = 'plus'; else k = k.toLowerCase();
        return (e.ctrlKey || e.metaKey ? 'mod+' : '') + (e.altKey ? 'alt+' : '') + (e.shiftKey ? 'shift+' : '') + k;
    }

    function bindRow(d, g) {
        if (d.m && !IS_MAC || d.w && IS_MAC) return;
        if (!BINDS[g]) BINDS[g] = { id: d.id, f: !!d.f, o: !!d.o };
    }
    function bindDef(d) {
        bindRow(d, d.g);
        if (d.v) { // modifier-fork defaults (F5/F3) match every combo, like before
            var k = d.g.split('+').pop();
            VARY_ALL.forEach(function (p) { bindRow(d, p + k); });
        }
    }
    // shift+mod variants for a rebound fork action — keeps the
    // "plain=soft, shifted/modified=hard" feel on the new key.
    function bindVarySm(d) {
        bindRow(d, d.g);
        var parts = d.g.split('+'), k = parts.pop();
        var has = function (x) { return parts.indexOf(x) >= 0; };
        var m = has('mod'), a = has('alt'), s = has('shift');
        var B = function (mm, aa, ss) { return (mm ? 'mod+' : '') + (aa ? 'alt+' : '') + (ss ? 'shift+' : '') + k; };
        if (!s) bindRow(d, B(m, a, true));
        if (!m) { bindRow(d, B(true, a, s)); if (!s) bindRow(d, B(true, a, true)); }
    }
    function rebuildBinds() {
        BINDS = Object.create(null);
        var over = state.keys || {}, has = {};
        for (var id in over) has[id] = 1;
        KEYS.forEach(function (d) { if (!has[d.id]) bindDef(d); });
        for (var id2 in over) {
            var g = over[id2]; if (!g) continue; // "" = explicitly unbound
            var proto = null, vary = false;
            for (var i = 0; i < KEYS.length; i++) {
                var d = KEYS[i];
                if (d.id !== id2 || (d.m && !IS_MAC || d.w && IS_MAC)) continue;
                if (!proto) proto = d;
                if (d.v) vary = true; // any forked default keeps its fork on rebind
            }
            if (!proto) continue;
            var row = { id: id2, g: g, f: proto.f, o: proto.o, m: proto.m, w: proto.w };
            if (vary) bindVarySm(row); else bindRow(row, g);
        }
    }

    function dispatchKey(e, sh, inField) {
        var g = gestureOf(e);
        if (!g) return false;
        var b = BINDS[g];
        if (!b) return false;
        if (b.f && inField) return true;      // editable field: leave the key alone
        e.preventDefault(); e.stopPropagation();
        if (b.o && state.ov) return true;      // overlay up: swallowed, like before
        var a = ACT[b.id];
        if (a) a(e, sh, inField);
        return true;
    }

    function wireShortcuts() {
        rebuildBinds();
        rawOn(window, 'keydown', function (e) {
            var sh = sh0(); // resolve live — a remount swaps the shadow root
            if (!sh) return;
            // about:shortcuts' rebind UI is recording this press — don't fire it.
            try { if (window.__brainKeyCapture) return; } catch (e0) { }
            var mod = e.ctrlKey || e.metaKey;
            var k = e.key;
            // Typing in a field: letter combos stay out of the way (and Windows
            // AltGr arrives as ctrl+alt — the character must survive).
            var inField = e.target && e.target.closest && e.target.closest('input,textarea,select,[contenteditable]');
            if (dispatchKey(e, sh, inField)) return;
            // Digit tab jumps are a pattern (1..9), not a bindable action —
            // a user binding can still steal a digit since the table runs first.
            if (mod && k >= '1' && k <= '9' && !e.altKey) {
                e.preventDefault(); e.stopPropagation();
                var ti = k === '9' ? state.tabs.length - 1 : (+k) - 1;
                var tt = state.tabs[ti];
                if (tt) switchTabLocal(tt.id);
            } else if (e.key === 'Escape') {
                if (palOpen) { e.preventDefault(); e.stopPropagation(); closePal(sh); }
                else if (rw.open) { e.preventDefault(); e.stopPropagation(); closeRw(sh); }
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

    // Ctrl+Shift+PgUp/PgDn — reorder the active tab. Pinned tabs stay inside
    // the pinned block at the strip's left edge (same rule as Chrome).
    function moveActiveTab(dir) {
        var n = state.tabs.length, idx = -1, pins = 0;
        for (var i = 0; i < n; i++) {
            if (state.tabs[i].active) idx = i;
            if (state.tabs[i].pin) pins++;
        }
        if (idx < 0) return;
        var pinned = !!state.tabs[idx].pin;
        var to = idx + dir;
        if (to < (pinned ? 0 : pins) || to > (pinned ? pins - 1 : n - 1)) return;
        send('movetab:' + state.tabs[idx].id + ':' + to);
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
        if (SPLIT) { if (ROLE === 'page') t = false; else if (h <= 60) b = false; }
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
        if (ROLE === 'bar') { if (!quiet) send('pageui:devtools'); return; }
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

    var DT_ORDER = ['con', 'net', 'cookies', 'sess', 'ins'], dtPrevTab = null;
    function syncDtTabs(sh, animate) {
        var activeBtn = null;
        sh.querySelectorAll('.dtab').forEach(function (b) {
            var on = b.getAttribute('data-t') === dtTab;
            b.classList.toggle('on', on);
            if (on) activeBtn = b;
        });
        var dir = DT_ORDER.indexOf(dtTab) >= DT_ORDER.indexOf(dtPrevTab) ? 'in-r' : 'in-l';
        var changed = dtPrevTab !== null && dtPrevTab !== dtTab;
        DT_ORDER.forEach(function (t) {
            var p = sh.getElementById('dtp' + t);
            if (!p) return;
            p.classList.remove('in-r', 'in-l');
            p.classList.toggle('on', t === dtTab);
            if (animate && changed && t === dtTab) { void p.offsetWidth; p.classList.add(dir); }
        });
        dtPrevTab = dtTab;
        // sliding indicator — skip while the panel is hidden (no layout yet)
        var ind = sh.getElementById('dtind');
        if (ind && activeBtn && activeBtn.offsetWidth) {
            var first = !ind.classList.contains('on');
            if (first || !animate) ind.classList.add('snap');
            ind.style.width = activeBtn.offsetWidth + 'px';
            ind.style.height = activeBtn.offsetHeight + 'px';
            ind.style.transform = 'translate(' + activeBtn.offsetLeft + 'px,' + activeBtn.offsetTop + 'px)';
            ind.classList.add('on');
            if (ind.classList.contains('snap')) { void ind.offsetWidth; ind.classList.remove('snap'); }
        }
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
        renderInsListeners(sh, el);
    }
    // ---- Event listeners (from brain.js's registry) ----
    var insLAnc = true, insLRem = false, insLData = null;
    function renderInsListeners(sh, el) {
        var box = sh.getElementById('inslist'); if (!box) return;
        var cnt = sh.getElementById('inslc');
        insLData = null;
        if (!el || el.nodeType !== 1 || !window.__brain || !window.__brain.listeners) {
            box.innerHTML = '<div class="sempty">—</div>'; if (cnt) cnt.textContent = ''; return;
        }
        var r;
        try { r = window.__brain.listeners(el, { ancestors: insLAnc, removed: insLRem, refs: false, src: true }); }
        catch (e) { r = { ok: false, error: String(e) }; }
        if (!r || !r.ok) { box.innerHTML = '<div class="sempty">' + esc((r && r.error) || 'unavailable') + '</div>'; if (cnt) cnt.textContent = ''; return; }
        insLData = r;
        if (cnt) cnt.textContent = r.total ? '· ' + r.total : '';
        var h = r.late ? '<div class="lwarn">Registry started after this page\'s scripts ran — listeners added earlier are missing. Reload for a complete picture.</div>' : '';
        if (!r.total) h += '<div class="sempty">' + (insLAnc ? 'no listeners on this element or its ancestors' : 'no listeners on this element') + '</div>';
        r.groups.forEach(function (g, gi) {
            if (!g.listeners.length) return;
            h += '<div class="lg"><div class="lgh" data-gi="' + gi + '" title="' + (g.node && g.node.nodeType === 1 ? 'Select this node' : '') + '">' +
                '<b>' + esc(g.target) + '</b><span class="lgr">' + (g.rel === 'target' ? 'this' : 'ancestor') + '</span></div>';
            g.listeners.forEach(function (l, li) {
                var fname = l.fn.name || (l.fn.attr ? 'inline attribute' : 'anonymous');
                h += '<div class="lr' + (l.removed ? ' gone' : '') + '" data-gi="' + gi + '" data-li="' + li + '">' +
                    '<div class="lrl"><span class="lt">' + esc(l.type) + '</span>' +
                    (l.phase === 'capture' ? '<span class="lb cap">capture</span>' : '') +
                    (l.once ? '<span class="lb">once</span>' : '') +
                    (l.passive === true ? '<span class="lb">passive</span>' : '') +
                    (l.how !== 'listener' ? '<span class="lb">' + (l.how === 'attribute' ? 'attr' : 'on' + esc(l.type)) + '</span>' : '') +
                    (l.removed ? '<span class="lb rm">' + esc(l.removed) + '</span>' : '') +
                    '<span class="ls" title="' + esc(l.site) + '">' + esc(fname) + (l.site ? ' · ' + esc(l.site) : '') + '</span>' +
                    '<span class="dtsp"></span>' +
                    (l.removed ? '' : '<button class="lx" title="Remove this listener from the page">✕</button>') + '</div>' +
                    '<div class="lrd"><pre>' + esc(l.fn.src || (l.fn.native ? '[native code]' : '(source unavailable)')) + '</pre>' +
                    (l.stack && l.stack.length
                        ? '<div class="lf">registered' + (l.at !== undefined ? ' at +' + l.at + 'ms' : '') + ' by:' +
                          l.stack.map(function (f) { return '<br>&nbsp;&nbsp;' + esc(f.fn || '(anonymous)') + ' <span style="color:#55556f">' + esc(f.at) + '</span>'; }).join('') + '</div>'
                        : '<div class="lf">registration site unknown' + (l.how === 'attribute' ? ' (inline HTML attribute)' : '') + '</div>') +
                    (l.removedAt ? '<div class="lf">removed by ' + esc(l.removedAt) + '</div>' : '') + '</div></div>';
            });
            h += '</div>';
        });
        if (r.omitted) h += '<div class="sempty">' + r.omitted + ' more listeners not shown</div>';
        box.innerHTML = h;
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
        setTimeout(function () { rawOn(document, 'mousedown', off, true); }, 0);
    }

    function applyBoot() {
        var b = null;
        try { b = window.__brainBoot || bootHeld; } catch (e) { }
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
        bootHeld = null;
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
        if (b.ag) { state.ag = b.ag; if (b.ag.n > 0) agRunUntil = Date.now() + 2200; }
        state.privActive = !!b.privActive;
        if (b.se) state.se = b.se;
        if (Array.isArray(b.sel)) state.sel = b.sel;
        state.keys = b.keys || {};
        rebuildBinds();
        state.ov = lastPushOv = !!b.ov;
        exposeState();
        var s = sh0();
        if (s) {
            render(s);
            syncWin(s);
            syncNtp(s);
            paintAgent(s);
            updateStar(s);
            updateAddr(s);
            rwApply(b, s);
            var om = s.getElementById('omni');
            if (om) om.classList.toggle('priv', !!state.privActive);
            // DevTools survives doc swaps — reopen on the tab the host remembers.
            if (b.dt && !dtOpen) toggleDt(s, b.dt, true);
        }
        // Pages (newtab tiles) render from this event — fire after listeners
        // have had a chance to attach during parse.
        setTimeout(function () {
            if (INTERNAL_DOC) try { document.dispatchEvent(new CustomEvent('brain-state', { detail: state })); } catch (e) { }
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
            // split mode: the page view must own keyboard focus for typing to land
            if (SPLIT && ROLE === 'page') send('focuspage');
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

    // ---------------- Rewind (opt-in page time travel) ----------------
    // OFF by default. Nothing in this section runs — no observer, no timer,
    // no snapshot — unless the host reports state.rw (the user turned it on).
    // Recorder (page view only): a MutationObserver just raises a dirty flag;
    // a throttled timer then clones the DOM, strips scripts/secrets, gzips it
    // off-thread and posts it to the host, which keeps it in RAM (see the
    // Rewind region of BrowserController.cs for budgets). The throttle adapts
    // to how long a capture takes, so heavy pages are sampled less often.
    // Viewer: a sandboxed, script-less iframe over the page, driven by a
    // scrubber whose right end is "Live".
    var rw = {
        rec: false, mo: null, tm: 0, dirty: false, lastT: 0, lastMut: 0, lastHash: -1, lastDt: 0,
        gap: 4500, busy: false, pend: null, sheets: null, wired: false,
        open: false, items: [], info: null, selId: 0, shownId: 0, want: 0,
        cache: {}, order: [], hits: null, gear: false, tick: 0, reqT: 0, toastT: 0
    };
    var RW_SENS = /pass(word|wd)?|pwd|secret|token|cvv|cvc|card|otp|ssn|pin\b|2fa|mfa|iban/i;
    var RW_MAX_NODES = 60000, RW_MAX_CHARS = 6000000, RW_DATA_MAX = 65536;

    function rwSkipped() {
        var h = location.hostname.toLowerCase(), l = state.rws || [];
        for (var i = 0; i < l.length; i++) {
            var r = String(l[i]).toLowerCase();
            if (h === r || h.slice(-(r.length + 1)) === '.' + r) return true;
        }
        return false;
    }
    // Why this document can't be recorded right now ('' = it can).
    function rwReason() {
        if (state.privActive) return 'Rewind never records private tabs.';
        if (state.ov) return 'Nothing to rewind on the new-tab page.';
        if (!/^https?:$/.test(location.protocol)) return 'Rewind records regular web pages only.';
        if (rwSkipped()) return 'You excluded this site from Rewind.';
        if (ROLE === 'page' && document.querySelector('input[type=password]')) return 'Login pages are never recorded.';
        return '';
    }
    function rwSensitive(el) {
        try {
            if ((el.type || '').toLowerCase() === 'password') return true;
            var ac = (el.getAttribute('autocomplete') || '').toLowerCase();
            if (/^cc-|one-time-code|current-password|new-password/.test(ac)) return true;
            return RW_SENS.test((el.name || '') + ' ' + (el.id || ''));
        } catch (e) { return false; }
    }

    // ---- recorder ----
    function rwSync() {
        if (ROLE !== 'page') return;
        if (state.rw && !rw.rec) rwStart();
        else if (!state.rw && rw.rec) rwStop();
    }
    function rwWire() {
        if (rw.wired) return; rw.wired = true;
        rawOn(document, 'visibilitychange', function () {
            if (!rw.rec) return;
            if (document.visibilityState === 'hidden') { if (rw.dirty) rwCapture(null, true); } // leaving: keep the tab's last state
            else if (rw.dirty) rwSchedule(600);
        });
        // Last state before the document goes away — only when captures are cheap,
        // and uncompressed (an async gzip would not finish before unload).
        rawOn(window, 'pagehide', function () {
            if (rw.rec && rw.dirty && rw.lastDt < 120) rwCapture(null, true, true);
        });
    }
    function rwStart() {
        if (!window.MutationObserver) return;
        rwWire();
        rw.rec = true; rw.dirty = true; rw.lastHash = -1; rw.gap = 4500;
        try {
            rw.mo = new MutationObserver(function () {
                rw.dirty = true; rw.lastMut = Date.now();
                if (!rw.tm) rwSchedule();
            });
            rw.mo.observe(document, { subtree: true, childList: true, attributes: true, characterData: true });
        } catch (e) { rw.rec = false; return; }
        rwSchedule(1800); // first snapshot once the page has had a moment to settle
    }
    function rwStop() {
        rw.rec = false;
        if (rw.mo) { try { rw.mo.disconnect(); } catch (e) { } rw.mo = null; }
        if (rw.tm) { clearTimeout(rw.tm); rw.tm = 0; }
        rw.dirty = false; rw.sheets = null; rw.pend = null;
        rw.cache = {}; rw.order = []; rw.items = []; rw.hits = null;
        if (rw.open) closeRw(sh0());
    }
    function rwSchedule(d) {
        if (rw.tm || !rw.rec) return;
        var wait = d !== undefined ? d : Math.max(rw.gap - (Date.now() - rw.lastT), 700);
        rw.tm = setTimeout(function () { rw.tm = 0; rwTick(); }, wait);
    }
    function rwTick() {
        if (!rw.rec || !rw.dirty) return; // quiet: the observer re-arms us on the next mutation
        if (document.visibilityState !== 'visible') return; // visibilitychange re-arms us
        // Let a burst of mutations settle (bounded: never starve the timeline).
        if (Date.now() - rw.lastMut < 350 && Date.now() - rw.lastT < rw.gap * 3) { rwSchedule(350); return; }
        rwCapture(null, false);
    }
    function rwCapture(tag, force, sync) {
        if (!rw.rec) return;
        if (rw.busy) { if (tag) rw.pend = tag; return; }
        if (!force && document.visibilityState !== 'visible') return;
        if (rwReason()) { rw.dirty = false; return; }
        rw.busy = true;
        var t0 = performance.now(), snap = null;
        try { snap = rwSerialize(); } catch (e) { snap = null; }
        rw.lastDt = performance.now() - t0;
        rw.lastT = Date.now(); rw.dirty = false;
        // Adaptive duty cycle: spend at most ~2.5% of a core. A 100ms capture
        // means one per 4.5s; a 1s capture on a monster page means one per 40s.
        rw.gap = Math.min(60000, Math.max(4500, Math.round(rw.lastDt * 40)));
        if (!snap || (snap.hs === rw.lastHash && !tag)) { rw.busy = false; return; }
        rw.lastHash = snap.hs;
        try {
            rwSend(snap, tag, sync, function () {
                rw.busy = false;
                if (rw.open) send('rwlist');
                if (rw.pend) { var tg = rw.pend; rw.pend = null; rwCapture(tg, true); }
            });
        } catch (e) { rw.busy = false; }
    }
    function rwDoctype() {
        try { if (document.doctype) return new XMLSerializer().serializeToString(document.doctype); } catch (e) { }
        return document.compatMode === 'CSS1Compat' ? '<!DOCTYPE html>' : '';
    }
    function rwHash(s) {
        var h = 2166136261;
        for (var i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 16777619); }
        return h >>> 0;
    }
    function rwSheetText(sheet) {
        try {
            var n = sheet.cssRules.length, c = rw.sheets && rw.sheets.get(sheet);
            if (c && c.n === n) return c.t;
            var t = '';
            for (var k = 0; k < n; k++) t += sheet.cssRules[k].cssText + '\n';
            if (!rw.sheets) rw.sheets = new WeakMap();
            rw.sheets.set(sheet, { n: n, t: t });
            return t;
        } catch (e) { return ''; } // cross-origin sheet
    }
    // The page as static HTML: live form state baked in, scripts/frames/media and
    // anything secret-looking stripped, CSSOM-only styles inlined, URLs pinned
    // to the document's base. Returns null when this page should not be recorded.
    function rwSerialize() {
        var de = document.documentElement;
        if (!de || !document.body) return null;
        if (document.getElementsByTagName('*').length > RW_MAX_NODES) return null;
        var qs = function (root, sel) { return root.querySelectorAll(sel); };
        var lIn = qs(de, 'input,textarea,select'), lSt = qs(de, 'style'), lIf = qs(de, 'iframe'), lIm = qs(de, 'img');
        var cl = de.cloneNode(true);
        var cIn = qs(cl, 'input,textarea,select'), cSt = qs(cl, 'style'), cIf = qs(cl, 'iframe'), cIm = qs(cl, 'img');
        var i, j, a, b;

        // Live values -> attributes (serialised HTML only knows the initial ones).
        for (i = 0; i < lIn.length && i < cIn.length; i++) {
            a = lIn[i]; b = cIn[i];
            if (b.tagName === 'INPUT') {
                var ty = (a.type || 'text').toLowerCase();
                if (rwSensitive(a) || ty === 'hidden' || ty === 'file') { b.removeAttribute('value'); continue; }
                if (ty === 'checkbox' || ty === 'radio') { if (a.checked) b.setAttribute('checked', ''); else b.removeAttribute('checked'); }
                else if (ty !== 'button' && ty !== 'submit' && ty !== 'reset' && ty !== 'image') b.setAttribute('value', a.value);
            } else if (b.tagName === 'TEXTAREA') {
                b.textContent = rwSensitive(a) ? '' : a.value;
            } else if (b.tagName === 'SELECT') {
                for (j = 0; j < a.options.length && j < b.options.length; j++) {
                    if (a.options[j].selected) b.options[j].setAttribute('selected', ''); else b.options[j].removeAttribute('selected');
                }
            }
        }
        // Style rules that only exist in the CSSOM (CSS-in-JS) + constructed sheets.
        for (i = 0; i < lSt.length && i < cSt.length; i++)
            if (!cSt[i].textContent && lSt[i].sheet) cSt[i].textContent = rwSheetText(lSt[i].sheet);
        var head = cl.querySelector('head');
        if (!head) { head = document.createElement('head'); cl.insertBefore(head, cl.firstChild); }
        try {
            var ad = document.adoptedStyleSheets;
            if (ad && ad.length) {
                var as = document.createElement('style'), at = '';
                for (i = 0; i < ad.length; i++) at += rwSheetText(ad[i]);
                as.textContent = at; head.appendChild(as);
            }
        } catch (e) { }
        // Frames become sized placeholders (nothing from other origins may load or run).
        for (i = 0; i < lIf.length && i < cIf.length; i++) {
            var r = lIf[i].getBoundingClientRect(), ph = document.createElement('div');
            ph.setAttribute('style', 'display:inline-block;box-sizing:border-box;width:' + Math.round(r.width) + 'px;height:' +
                Math.round(r.height) + 'px;background:rgba(128,128,128,.12);border:1px dashed rgba(128,128,128,.4)');
            cIf[i].replaceWith(ph);
        }
        // Pin each image to the source the browser actually chose; drop giant data: URIs.
        for (i = 0; i < lIm.length && i < cIm.length; i++) {
            var cs = lIm[i].currentSrc, src = cIm[i].getAttribute('src') || '';
            if (cs && cs.indexOf('data:') !== 0) { cIm[i].setAttribute('src', cs); cIm[i].removeAttribute('srcset'); cIm[i].removeAttribute('sizes'); }
            else if (src.indexOf('data:') === 0 && src.length > RW_DATA_MAX) cIm[i].setAttribute('src', 'data:image/gif;base64,R0lGODlhAQABAAAAACw=');
        }
        var drop = qs(cl, 'script,noscript,template,object,embed,applet,base,picture source,video source,audio source,track,' +
            'link[rel=preload],link[rel=prefetch],link[rel=modulepreload],link[rel=dns-prefetch],link[rel=preconnect],' +
            'meta[http-equiv],meta[name=csrf-token],meta[name=csrf-param],#' + HOST_ID + ',#brain-yt-guide');
        for (i = 0; i < drop.length; i++) drop[i].remove();
        var media = qs(cl, 'video,audio');
        for (i = 0; i < media.length; i++) { media[i].removeAttribute('src'); media[i].removeAttribute('autoplay'); media[i].setAttribute('preload', 'none'); }
        // The in-page toolbar's reserved strip is chrome, not content.
        try {
            if (cl.style.getPropertyValue('margin-top') === resPx() + 'px') {
                cl.style.removeProperty('margin-top');
                if (!cl.getAttribute('style')) cl.removeAttribute('style');
            }
        } catch (e) { }
        var base = document.createElement('base'); base.setAttribute('href', document.baseURI);
        var csp = document.createElement('meta');
        csp.setAttribute('http-equiv', 'Content-Security-Policy');
        csp.setAttribute('content', "script-src 'none'; object-src 'none'; frame-src 'none'; media-src 'none'");
        head.insertBefore(csp, head.firstChild); head.insertBefore(base, head.firstChild);

        var html = rwDoctype() + cl.outerHTML;
        if (html.length > RW_MAX_CHARS) return null;
        var text = '';
        try { text = (document.body.innerText || '').replace(/[ \t]+\n/g, '\n').replace(/\n{3,}/g, '\n\n').slice(0, 40000); } catch (e) { }
        return { html: html, text: text, hs: rwHash(html), sx: Math.round(window.scrollX), sy: Math.round(window.scrollY), vw: window.innerWidth, vh: window.innerHeight };
    }
    function rwB64(buf) {
        var u = new Uint8Array(buf), s = '';
        for (var i = 0; i < u.length; i += 0x8000) s += String.fromCharCode.apply(null, u.subarray(i, i + 0x8000));
        return btoa(s);
    }
    function rwGz(str) { return new Response(new Blob([str]).stream().pipeThrough(new CompressionStream('gzip'))).arrayBuffer(); }
    function rwUnz(b64) {
        var bin = atob(b64), u = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) u[i] = bin.charCodeAt(i);
        return new Response(new Blob([u]).stream().pipeThrough(new DecompressionStream('gzip'))).text();
    }
    function rwSend(snap, tag, sync, done) {
        var m = { u: location.href, ti: (document.title || '').slice(0, 200), sx: snap.sx, sy: snap.sy, vw: snap.vw, vh: snap.vh, hs: snap.hs };
        if (tag) m.tag = tag;
        var post = function () { try { send('rwsnap:' + JSON.stringify(m)); } catch (e) { } };
        var raw = function () { m.d = snap.html; m.x = snap.text; post(); }; // host compresses
        var p = null;
        try {
            if (!sync && window.CompressionStream && window.Response && window.Blob && Blob.prototype.stream)
                p = Promise.all([rwGz(snap.html), rwGz(snap.text)]);
        } catch (e) { p = null; }
        if (!p) { raw(); done(); return; }
        p.then(function (r) { m.z = 1; m.d = rwB64(r[0]); m.x = rwB64(r[1]); post(); })
            .catch(raw).then(done, done);
    }

    // ---- viewer ----
    function rwAgo(ms) {
        var s = Math.max(0, Math.round(ms / 1000));
        return s < 60 ? s + 's' : s < 3600 ? Math.floor(s / 60) + 'm ' + (s % 60) + 's' : Math.floor(s / 3600) + 'h ' + Math.floor(s % 3600 / 60) + 'm';
    }
    function rwClock(t) {
        var d = new Date(t), p = function (n) { return ('0' + n).slice(-2); };
        return p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
    }
    function rwTop() { return SPLIT ? 0 : 46; }
    // The bar overlays content like the toolbar does: opening/closing it
    // changes the reserved strip, so restore every shifted header and let the
    // existing machinery rescan at the new total.
    function rwSpace() {
        var de = document.documentElement;
        if (!de) return;
        for (var i = fixedShifted.length - 1; i >= 0; i--) {
            var it = fixedShifted[i];
            try {
                if (it.orig) it.el.style.setProperty('top', it.orig); else it.el.style.removeProperty('top');
                if (it.hs) { if (it.oh) it.el.style.setProperty('height', it.oh); else it.el.style.removeProperty('height'); }
            } catch (e) { }
        }
        fixedShifted = []; fixedSeen = new WeakSet();
        var px = resPx();
        if (px > 0) {
            de.style.setProperty('margin-top', px + 'px', 'important');
            ytVars(true);
            adjustFixed();
            if (IS_YT) ytDirect();
        } else {
            de.style.marginTop = '';
            ytVars(false);
            try { var sg = document.getElementById('brain-yt-guide'); if (sg) sg.remove(); } catch (e) { }
            if (ytFilled) {
                ytFilled = false;
                try { var mcr = document.getElementById('masthead-container'); if (mcr) mcr.style.removeProperty('background-color'); } catch (e) { }
            }
        }
    }
    function rwIdx() {
        for (var i = 0; i < rw.items.length; i++) if (rw.items[i].i === rw.selId) return i;
        return rw.items.length; // not found / live
    }
    function toggleRw(sh) { if (rw.open) closeRw(sh); else openRw(sh); }
    function openRw(sh) {
        if (!sh || state.ov || rw.open) return;
        rw.open = true; rw.selId = 0; rw.shownId = 0;
        rwSpace();
        sh.getElementById('rwbar').style.top = rwTop() + 'px';
        sh.getElementById('rwview').style.top = (rwTop() + 44) + 'px';
        sh.getElementById('rwbar').classList.add('on');
        if (state.rw) send('rwlist');
        rwRender(sh);
        rw.tick = setInterval(function () { if (rw.open) rwLabel(sh0()); }, 15000);
        setTimeout(function () { var s = sh0(), sl = s && s.getElementById('rwsl'); if (sl && state.rw) sl.focus(); }, 60);
    }
    function closeRw(sh) {
        rw.open = false; rw.selId = 0; rw.shownId = 0; rw.hits = null; rw.gear = false;
        rwSpace();
        clearInterval(rw.tick); clearTimeout(rw.reqT);
        rw.cache = {}; rw.order = [];
        if (!sh) return;
        ['rwbar', 'rwview', 'rwmenu', 'rwres'].forEach(function (id) { var e = sh.getElementById(id); if (e) e.classList.remove('on'); });
        var f = sh.getElementById('rwif'); if (f) { f.removeAttribute('srcdoc'); f.style.display = 'none'; }
    }
    function rwRender(sh) {
        if (!sh || !rw.open) return;
        var bar = sh.getElementById('rwbar'), view = sh.getElementById('rwview'), card = sh.getElementById('rwcard');
        var sl = sh.getElementById('rwsl');
        bar.classList.toggle('off', !state.rw);
        if (!state.rw) { // consent card: off by default, explain before turning on
            view.classList.add('on', 'card'); card.style.display = 'flex';
            sh.getElementById('rwif').style.display = 'none';
            rwLabel(sh); return;
        }
        card.style.display = 'none'; view.classList.remove('card');
        var n = rw.items.length, idx = rwIdx();
        if (!sl) return;
        sl.disabled = n === 0; sl.max = String(n); sl.value = String(idx);
        if (idx >= n) { // Live
            rw.selId = 0; rw.shownId = 0;
            view.classList.remove('on');
            sh.getElementById('rwif').style.display = 'none';
        } else if (rw.items[idx].i !== rw.shownId) rwShow(sh, idx);
        rwLabel(sh);
        if (rw.gear) rwFillGear(sh);
    }
    function rwLabel(sh) {
        if (!sh) return;
        var lb = sh.getElementById('rwlbl'), bar = sh.getElementById('rwbar'); if (!lb || !bar) return;
        var n = rw.items.length, idx = rwIdx(), why = rwReason();
        bar.classList.toggle('past', state.rw && idx < n);
        if (!state.rw) { lb.textContent = 'Off — turn it on to start recording'; return; }
        if (idx < n) {
            var it = rw.items[idx];
            lb.innerHTML = '<b>' + rwAgo(Date.now() - it.t) + ' ago</b> · ' + rwClock(it.t) + ' · ' + esc(it.ti || hostOf(it.u)) +
                (it.tag ? ' <i class="rwtag">' + esc(it.tag) + '</i>' : '');
            lb.title = it.u;
        } else if (n === 0) lb.textContent = why || 'Recording — the first snapshot lands a few seconds after the page settles';
        else lb.innerHTML = '<b>Live</b> · ' + n + ' snapshot' + (n === 1 ? '' : 's') + ' · last ' + rwAgo(Date.now() - rw.items[n - 1].t) + ' ago' +
            (why ? ' · <span class="rwwarn">' + esc(why) + '</span>' : '');
    }
    function rwShow(sh, idx) {
        var it = rw.items[idx]; if (!it) return;
        sh.getElementById('rwview').classList.add('on');
        var hit = rw.cache[it.i];
        rw.selId = it.i;
        if (hit) { rwPaint(sh, it, hit); return; }
        rw.want = it.i;
        clearTimeout(rw.reqT);
        rw.reqT = setTimeout(function () { send('rwget:' + it.i); }, 60); // coalesce while dragging
    }
    function rwPaint(sh, it, m) {
        var f = sh.getElementById('rwif'); if (!f) return;
        rw.shownId = it.i;
        f.style.display = 'block';
        f.onload = function () {
            try {
                var d = f.contentDocument, w = f.contentWindow; if (!d || !w) return;
                w.scrollTo(m.sx, m.sy);
                d.addEventListener('click', function (e) {
                    var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
                    if (a) { e.preventDefault(); rwToast(sh0(), 'Links are disabled in a snapshot — ' + a.href); }
                }, true);
                d.addEventListener('submit', function (e) { e.preventDefault(); }, true);
            } catch (e) { }
        };
        f.srcdoc = m.html;
    }
    function rwToast(sh, msg) {
        var t = sh && sh.getElementById('rwmsg'); if (!t) return;
        t.textContent = msg; t.classList.add('on');
        clearTimeout(rw.toastT); rw.toastT = setTimeout(function () { t.classList.remove('on'); }, 2600);
    }
    function onRwList(sh, j) {
        rw.info = j; rw.items = Array.isArray(j.items) ? j.items : [];
        if (rw.open) rwRender(sh);
    }
    function onRwSnap(sh, j) {
        if (!j.ok) { if (rw.open && rw.want === j.i) rwToast(sh, 'That snapshot has aged out of memory.'); send('rwlist'); return; }
        var dec = null;
        try { dec = rwUnz(j.d); } catch (e) { }
        if (!dec) { rwToast(sh0(), 'This build cannot decompress snapshots.'); return; }
        dec.then(function (html) {
            rw.cache[j.i] = { html: html, sx: j.sx || 0, sy: j.sy || 0 };
            rw.order.push(j.i);
            while (rw.order.length > 6) delete rw.cache[rw.order.shift()];
            var s = sh0(), idx = rwIdx();
            if (rw.open && s && rw.selId === j.i && rw.items[idx]) rwPaint(s, rw.items[idx], rw.cache[j.i]);
        }).catch(function () { rwToast(sh0(), 'Could not open that snapshot.'); });
    }
    function onRwSearch(sh, j) {
        rw.hits = Array.isArray(j.hits) ? j.hits : [];
        rwFillResults(sh);
    }
    function rwFillResults(sh) {
        var box = sh.getElementById('rwres'); if (!box) return;
        if (!rw.hits) { box.classList.remove('on'); return; }
        var h = '';
        rw.hits.forEach(function (x) {
            h += '<div class="rwr" data-i="' + x.i + '"><b>' + rwClock(x.t) + '</b><span>' + esc(x.sn) + '</span></div>';
        });
        box.innerHTML = h || '<div class="rwr none">No snapshot of this tab contains that text.</div>';
        box.style.top = (rwTop() + 46) + 'px';
        box.classList.add('on');
    }
    function rwFillGear(sh) {
        var m = sh.getElementById('rwmenu'); if (!m) return;
        var skipped = rwSkipped(), used = rw.info ? (rw.info.bytes / 1048576).toFixed(1) : '0.0';
        var chips = function (cur, opts, kind, fmt) {
            return opts.map(function (v) { return '<i class="rwc' + (v === cur ? ' on' : '') + '" data-k="' + kind + '" data-v="' + v + '">' + fmt(v) + '</i>'; }).join('');
        };
        m.style.top = (rwTop() + 46) + 'px';
        m.innerHTML =
            '<div class="rwm-h">Keep history for</div><div class="rwm-r">' +
            chips(state.rwk, [15, 30, 60, 240], 'keep', function (v) { return v < 60 ? v + ' min' : (v / 60) + ' h'; }) + '</div>' +
            '<div class="rwm-h">Memory limit</div><div class="rwm-r">' +
            chips(state.rwm, [64, 128, 256, 512], 'mb', function (v) { return v + ' MB'; }) + '</div>' +
            '<div class="rwm-i" data-a="skip"><span>Don\u2019t record ' + esc(hostOf(location.href) || 'this site') + '</span><em>' + (skipped ? SVG.chk : '') + '</em></div>' +
            '<div class="rwm-i" data-a="agent"><span>Let the AI agent read this history</span><em>' + (state.rwa ? SVG.chk : '') + '</em></div>' +
            '<div class="rwm-i" data-a="clear"><span>Clear this tab\u2019s history</span></div>' +
            '<div class="rwm-i" data-a="clearall"><span>Clear everything</span></div>' +
            '<div class="rwm-i danger" data-a="off"><span>Turn Rewind off</span></div>' +
            '<div class="rwm-f">In memory only · ' + used + ' MB used · never private tabs or login pages</div>';
    }
    function rwGear(sh) {
        var m = sh.getElementById('rwmenu');
        rw.gear = !rw.gear;
        if (rw.gear) { rwFillGear(sh); m.classList.add('on'); } else m.classList.remove('on');
    }
    // State arrived from the host (b:9 push or boot payload).
    function rwApply(j, sh) {
        state.rw = !!j.rw; state.rwa = !!j.rwa;
        state.rwk = j.rwk || 30; state.rwm = j.rwm || 128;
        state.rws = Array.isArray(j.rws) ? j.rws : [];
        paintRwBtn(sh);
        rwSync();
        if (rw.open) { if (state.rw) send('rwlist'); rwRender(sh); }
    }
    function paintRwBtn(sh) {
        var b = sh && sh.getElementById('rwb'); if (!b) return;
        b.classList.toggle('on', !!state.rw);
        b.classList.toggle('rec', !!state.rw && !state.ov && !state.privActive);
    }
    function wireRewind(sh) {
        var $ = function (id) { return sh.getElementById(id); };
        var rwB = $('rwb');
        ['mousedown', 'click'].forEach(function (ev) {
            rwB.addEventListener(ev, function (e) { e.stopPropagation(); e.preventDefault(); });
        });
        rwB.addEventListener('click', function () { menuCmd(sh, 'rewind'); });
        var bar = $('rwbar');
        ['mousedown', 'click', 'contextmenu'].forEach(function (ev) { bar.addEventListener(ev, function (e) { e.stopPropagation(); }); });
        $('rwx').addEventListener('click', function () { closeRw(sh); });
        $('rwlive').addEventListener('click', function () { rw.selId = 0; rwRender(sh); });
        $('rwprev').addEventListener('click', function () { rwStep(sh, -1); });
        $('rwnext').addEventListener('click', function () { rwStep(sh, 1); });
        $('rwgear').addEventListener('click', function () { rwGear(sh); });
        $('rwsl').addEventListener('input', function (e) {
            var v = +e.target.value, n = rw.items.length;
            rw.selId = v >= n ? 0 : rw.items[v].i;
            rwRender(sh);
        });
        var q = $('rwq');
        q.addEventListener('keydown', function (e) {
            e.stopPropagation();
            if (e.key === 'Enter') { var v = q.value.trim(); if (v) send('rwsearch:' + v); else { rw.hits = null; rwFillResults(sh); } }
            else if (e.key === 'Escape') { q.value = ''; rw.hits = null; rwFillResults(sh); q.blur(); }
        });
        ['keyup', 'keypress'].forEach(function (ev) { q.addEventListener(ev, function (e) { e.stopPropagation(); }); });
        $('rwres').addEventListener('click', function (e) {
            var r = e.target.closest('.rwr[data-i]'); if (!r) return;
            rw.selId = +r.getAttribute('data-i'); rw.hits = null; rwFillResults(sh); rwRender(sh);
        });
        $('rwmenu').addEventListener('click', function (e) {
            var c = e.target.closest('[data-k]'), a = e.target.closest('[data-a]');
            if (c) { send('rwcfg:' + c.getAttribute('data-k') + ':' + c.getAttribute('data-v')); return; }
            if (!a) return;
            var act = a.getAttribute('data-a');
            if (act === 'skip') send('rwskip');
            else if (act === 'agent') send('rwcfg:agent:' + (state.rwa ? '0' : '1'));
            else if (act === 'clear') { send('rwclear:tab'); rw.selId = 0; }
            else if (act === 'clearall') { send('rwclear:all'); rw.selId = 0; }
            else if (act === 'off') { send('rwtoggle:0'); closeRw(sh); }
        });
        $('rwon').addEventListener('click', function () { send('rwtoggle:1'); });
        $('rwnot').addEventListener('click', function () { closeRw(sh); });
        if (!rw.docWired) { // document listeners attach once — remounts must not duplicate them
            rw.docWired = true;
            rawOn(document, 'mousedown', function (e) {
                if (!rw.gear) return;
                var t = e.composedPath ? e.composedPath() : [];
                for (var i = 0; i < t.length; i++) if (t[i] && (t[i].id === 'rwmenu' || t[i].id === 'rwgear')) return;
                rw.gear = false;
                var s2 = sh0(), m = s2 && s2.getElementById('rwmenu'); if (m) m.classList.remove('on');
            }, true);
        }
    }
    function rwStep(sh, d) {
        var n = rw.items.length; if (!n) return;
        var i = Math.max(0, Math.min(n, rwIdx() + d));
        rw.selId = i >= n ? 0 : rw.items[i].i;
        rwRender(sh);
    }

    // ---------------- mount ----------------

    // ---------------- split-toolbar plumbing (macOS --split) ----------------
    // The host sizes the native bar view: 46pt normally, taller while one of
    // the bar's popovers is open (they hang below the bar), full height for
    // the palette and menus whose CSS depends on the viewport size.
    function setupSplit(host, sh) {
        var css = document.createElement('style');
        css.id = 'splitcss';
        if (ROLE === 'bar') {
            // page-level features belong to the page view
            css.textContent = '#ntp,#dt,#dthl,#dttip,#dtpin,#fbar,#status,#pweye,#pwfill,#pwsave,#recpill,#reccard,#pmenu,#rwbar,#rwview,#rwmenu,#rwres,#rwmsg{display:none!important}' +
                // the page is a different view now: there is nothing behind us to blur
                '*{backdrop-filter:none!important;-webkit-backdrop-filter:none!important}';
        } else {
            host.style.setProperty('height', '0', 'important');
            host.style.setProperty('pointer-events', 'none', 'important');
            css.textContent = '.bar,#drops,#tmenu,#menu,#smenu,#dlp,#pal,#ttip,#agline,#prog,#askcard,#hocard{display:none!important}' +
                '#ntp{top:0!important}#fbar{top:6px!important}#pwsave,#askcard,#hocard{top:4px!important}#recpill,#reccard{top:8px!important}*{pointer-events:auto}';
        }
        sh.appendChild(css);
        if (ROLE !== 'bar') return;

        var last = '';
        var raf = 0, lowTimer = 0;
        var ids = ['drops', 'tmenu', 'menu', 'smenu', 'dlp', 'pal', 'ttip', 'askcard', 'hocard'];
        var check = function () {
            raf = 0;
            var s = sh0(); if (!s) return;
            var full = false, drops = false, tip = false, ask = false;
            for (var i = 0; i < ids.length; i++) {
                var el = s.getElementById(ids[i]);
                if (!el) continue;
                var cs = getComputedStyle(el);
                // Opacity is deliberately NOT checked: popovers fade in from 0, and
                // waiting for them to become visible delays the resize by a frame
                // or more. Closed ones are display:none / visibility:hidden.
                if (cs.display === 'none' || cs.visibility === 'hidden') continue;
                if (ids[i] === 'ttip') { if (el.classList.contains('on')) tip = true; continue; }
                if (ids[i] === 'askcard' || ids[i] === 'hocard') { var ra = el.getBoundingClientRect(); if (ra.width > 0 && ra.height > 0) ask = true; continue; }
                var r = el.getBoundingClientRect();
                if (!(r.width > 0 && r.height > 0)) continue;
                if (ids[i] === 'drops') drops = true;
                else full = true;
            }
            // Fixed sizes per kind: resizing a WKWebView on every keystroke
            // while the suggestion list changes height is what made typing laggy.
            var want = full ? 'full' : drops ? '440' : ask ? '240' : tip ? '120' : 'bar';
            if (want === last) { if (lowTimer) { clearTimeout(lowTimer); lowTimer = 0; } return; }
            if (want === 'bar' && last !== '') {
                // collapse a beat late so open/close flicker never ping-pongs the view size
                if (!lowTimer) lowTimer = setTimeout(function () { lowTimer = 0; last = 'bar'; send('barh:bar'); }, 140);
                return;
            }
            if (lowTimer) { clearTimeout(lowTimer); lowTimer = 0; }
            last = want; send('barh:' + want);
        };
        // Run in a microtask right after the DOM change (not next frame): the host
        // must start growing the view before the popover's first paint.
        var sched = function () { if (!raf) { raf = 1; Promise.resolve().then(check); } };
        // Watch just the popovers (not the whole bar, which animates constantly).
        try {
            var mo = new MutationObserver(sched);
            ids.forEach(function (id) {
                var el = sh.getElementById(id);
                if (el) mo.observe(el, { attributes: true, attributeFilter: ['style', 'class'], childList: id === 'drops' });
            });
        } catch (e) { }
        ['mousedown', 'keydown', 'input'].forEach(function (ev) { sh.addEventListener(ev, sched, true); });
        setInterval(sched, 600);
        // Host → bar commands (shortcuts pressed while the page view has focus)
        window.__brainBarCmd = function (c) {
            var s = sh0(); if (!s) return;
            if (c === 'pal') openPal(s);
            else if (c === 'addr') startEditing(s);
            else if (c === 'dl') toggleDl(s);
            else if (c === 'menu') showMenu(s);
        };
        send('barready');
    }
    // Page-level commands the bar view (or a menu there) asked for.
    function runPageCmd(c) {
        var s = sh0(); if (!s) return;
        if (c === 'find') openFind(s);
        else if (c === 'devtools') toggleDt(s);
        else if (c === 'pip') togglePip();
        else if (c === 'print') { try { window.print(); } catch (e) { } }
        else if (c === 'rewind') toggleRw(s);
        else if (c === 'markdown') copyMd(s, 'page');
        else if (c === 'mdsel') copyMd(s, 'sel');
        else if (c === 'mdsmart') copyMd(s, 'smart');
        else if (c === 'reccard') openRecCard(s);
    }

    function mount() {
        if (document.getElementById(HOST_ID)) return;
        if (!document.documentElement) { setTimeout(mount, 120); return; }
        // With boot state the bar existed before the doc swap — sliding it in
        // again reads as flicker, so skip the entrance animation.
        var hadBoot = false;
        try { hadBoot = !!(window.__brainBoot || bootHeld); } catch (e) { }

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
            '.nav{position:relative;display:flex;gap:1px;flex:none;padding:3px;border-radius:12px;' +
            'background:rgba(255,255,255,.04);border:1px solid rgba(255,255,255,.05)}' +
            'button.nav-b{position:relative;z-index:1;width:28px;height:28px;display:grid;place-items:center;border:0;border-radius:9px;' +
            'background:transparent;color:#a5a5c2;cursor:pointer;transition:color .15s,transform .14s cubic-bezier(.3,1.5,.5,1)}' +
            // the sliding .hl pill supplies the hover background
            'button.nav-b:hover{color:#fff}' +
            'button.nav-b:active{transform:scale(.84)}' +
            'button.nav-b svg{width:15px;height:15px;display:block;transition:transform .45s cubic-bezier(.16,1,.3,1),filter .2s}' +
            '.nav.hov .hl{opacity:1}' +
            'button.nav-b:hover svg{filter:drop-shadow(0 0 5px hsl(var(--h) 100% 72% / .55))}' +
            'button.nav-b[data-cmd=back]:hover svg{transform:translateX(-2.5px)}' +
            'button.nav-b[data-cmd=forward]:hover svg{transform:translateX(2.5px)}' +
            'button.nav-b[data-cmd=reload]:hover svg{transform:rotate(180deg)}' +
            'button.nav-b[data-cmd=home]:hover svg{transform:translateY(-2px) scale(1.14)}' +
            'button.nav-b.stopping:hover svg{transform:scale(1.15)}' +
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
            '#recpill{position:fixed;top:52px;left:50%;transform:translateX(-50%);display:none;z-index:33;align-items:center;gap:10px;' +
            'background:rgba(30,18,24,.96);border:1px solid rgba(255,107,122,.5);border-radius:99px;padding:6px 8px 6px 14px;' +
            'box-shadow:0 10px 30px rgba(0,0,0,.5);font:600 12.5px system-ui,sans-serif;color:#f2dfe3}' +
            '#recpill .rd{width:9px;height:9px;border-radius:50%;background:#ff4d63;animation:recblink 1.1s ease-in-out infinite}' +
            '@keyframes recblink{50%{opacity:.25}}' +
            '#recpill button{border:0;cursor:pointer;border-radius:99px;padding:5px 12px;background:#ff4d63;color:#fff;font:700 12px system-ui,sans-serif}' +
            '#reccard{position:fixed;top:52px;right:14px;display:none;z-index:34;flex-direction:column;gap:9px;width:390px;max-width:calc(100vw - 28px);max-height:calc(100vh - 80px);' +
            'background:rgba(22,22,34,.97);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);border:1px solid rgba(255,255,255,.12);border-radius:14px;padding:14px;' +
            'box-shadow:0 14px 40px rgba(0,0,0,.6);font:12.5px/1.4 system-ui,sans-serif;color:#c9c9dc;animation:pop .18s cubic-bezier(.16,1,.3,1)}' +
            '#reccard .hd{font:700 14px system-ui,sans-serif;color:#f2f2fc}' +
            '#reccard input.nm,#reccard input.ds{background:rgba(255,255,255,.06);border:1px solid rgba(255,255,255,.12);border-radius:9px;padding:7px 10px;color:#f2f2fc;font:13px system-ui,sans-serif;outline:0}' +
            '#reccard input.nm:focus,#reccard input.ds:focus{border-color:hsl(var(--h) 100% 70%)}' +
            '#reccard .sl{overflow:auto;max-height:240px;display:flex;flex-direction:column;gap:4px;padding-right:2px}' +
            '#reccard .st{display:flex;align-items:center;gap:8px;flex-wrap:wrap;padding:5px 8px;border-radius:8px;background:rgba(255,255,255,.04)}' +
            '#reccard .st .n{color:#6b6b88;font-size:11px;min-width:16px}#reccard .st .rl{flex:1;min-width:120px;color:#d8d8ea}#reccard .st .rl b{color:#fff;font-weight:600}' +
            '#reccard .pm{display:flex;align-items:center;gap:6px;color:#9a9ab4;font-size:11.5px}' +
            '#reccard .pn{width:90px;background:rgba(255,255,255,.06);border:1px solid rgba(255,255,255,.12);border-radius:6px;padding:2px 6px;color:#f2f2fc;font:11.5px system-ui,sans-serif;outline:0}' +
            '#reccard .sec{color:#ffb067;font-size:11.5px}#reccard .hint{color:#7d7d98;font-size:11.5px}' +
            '#reccard .btns{display:flex;justify-content:flex-end;gap:8px}' +
            '#reccard button{border:0;cursor:pointer;border-radius:9px;padding:7px 12px;font:600 12px system-ui,sans-serif}' +
            '#reccard .dn{background:rgba(255,107,122,.16);color:#ff8b98}#reccard .kp{background:rgba(255,255,255,.07);color:#d4d4e6}' +
            '#reccard .ok{background:hsl(var(--h) 100% 70%);color:#0b0b14}' +
            '#askcard{position:fixed;top:48px;right:14px;display:none;z-index:32;flex-direction:column;gap:10px;' +
            'background:rgba(30,22,26,.97);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(255,170,90,.45);border-radius:14px;padding:12px 14px;width:350px;max-width:calc(100vw - 40px);' +
            'box-shadow:0 14px 40px rgba(0,0,0,.6),inset 0 1px 0 rgba(255,255,255,.06);font:12.5px/1.45 system-ui,sans-serif;' +
            'transform-origin:top right;animation:pop .18s cubic-bezier(.16,1,.3,1)}' +
            '#askcard .top{display:flex;align-items:flex-start;gap:11px}' +
            '#askcard .k{width:26px;height:26px;border-radius:8px;display:grid;place-items:center;flex:none;background:rgba(255,170,90,.16);color:#ffb067}' +
            '#askcard .k svg{width:15px;height:15px}' +
            '#askcard .qt{color:#d4cdd0;min-width:0;flex:1;word-break:break-word}' +
            '#askcard .qt b{color:#fff;font-weight:600}' +
            '#askcard .qt i{color:#ffb067;font-style:normal;font-size:12px}' +
            '#askcard .more{color:#8a8aa4;font-size:11.5px}' +
            '#askcard .btns{display:flex;justify-content:flex-end;gap:8px;flex-wrap:wrap}' +
            '#askcard button{border:0;cursor:pointer;border-radius:9px;padding:7px 12px;font:600 12px system-ui,sans-serif;transition:all .12s}' +
            '#askcard .dn{background:rgba(255,107,122,.16);color:#ff8b98}#askcard .dn:hover{background:rgba(255,107,122,.28)}' +
            '#askcard .st{background:rgba(255,255,255,.07);color:#d4d4e6}#askcard .st:hover{background:rgba(255,255,255,.13)}' +
            '#askcard .ok{background:hsl(var(--h) 100% 70%);color:#0b0b14}#askcard .ok:hover{background:hsl(var(--h) 100% 78%)}' +
            '#hocard{position:fixed;top:48px;right:14px;display:none;z-index:33;flex-direction:column;gap:10px;' +
            'background:rgba(20,28,26,.97);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(110,220,170,.45);border-radius:14px;padding:12px 14px;width:350px;max-width:calc(100vw - 40px);' +
            'box-shadow:0 14px 40px rgba(0,0,0,.6),inset 0 1px 0 rgba(255,255,255,.06);font:12.5px/1.45 system-ui,sans-serif;' +
            'transform-origin:top right;animation:pop .18s cubic-bezier(.16,1,.3,1)}' +
            '#hocard .top{display:flex;align-items:flex-start;gap:11px}' +
            '#hocard .k{width:26px;height:26px;border-radius:8px;display:grid;place-items:center;flex:none;background:rgba(110,220,170,.16);color:#6edcaa}' +
            '#hocard .k svg{width:16px;height:16px}' +
            '#hocard .qt{color:#d4d8d6;min-width:0;flex:1;word-break:break-word}' +
            '#hocard .qt b{color:#fff;font-weight:600}' +
            '#hocard .qt i{color:#6edcaa;font-style:normal;font-size:12px}' +
            '#hocard .more{color:#8a8aa4;font-size:11.5px}' +
            '#hocard .btns{display:flex;justify-content:flex-end;gap:8px;flex-wrap:wrap}' +
            '#hocard button{border:0;cursor:pointer;border-radius:9px;padding:7px 12px;font:600 12px system-ui,sans-serif;transition:all .12s}' +
            '#hocard .st{background:rgba(255,255,255,.07);color:#d4d4e6}#hocard .st:hover{background:rgba(255,255,255,.13)}' +
            '#hocard .ok{background:#6edcaa;color:#0b0b14}#hocard .ok:hover{background:#8ae8bc}' +
            '#pwsave{position:fixed;top:48px;right:14px;display:none;z-index:31;flex-direction:column;gap:10px;' +
            'background:rgba(22,22,34,.96);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(255,255,255,.1);border-radius:14px;padding:12px 14px;width:330px;max-width:calc(100vw - 40px);' +
            'box-shadow:0 14px 40px rgba(0,0,0,.55),inset 0 1px 0 rgba(255,255,255,.06);font:12.5px/1.4 system-ui,sans-serif;' +
            'transform-origin:top right;animation:pop .18s cubic-bezier(.16,1,.3,1)}' +
            '#pwsave .top{display:flex;align-items:center;gap:11px}' +
            '#pwsave .btns{display:flex;justify-content:flex-end;gap:8px}' +
            '#pwsave .k{width:26px;height:26px;border-radius:8px;display:grid;place-items:center;flex:none;' +
            'background:hsl(var(--h) 100% 70% / .18);color:hsl(var(--h) 100% 84%)}' +
            '#pwsave .k svg{width:14px;height:14px}' +
            '#pwsave .tx{color:#c9c9dc;min-width:0;flex:1}' +
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
            '#menu{position:fixed;top:44px;right:8px;width:288px;max-height:calc(100vh - 56px);overflow-y:auto;z-index:31;' +
            'background:rgba(22,22,34,.97);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(255,255,255,.1);border-radius:13px;padding:5px;' +
            'transform-origin:top right;' +
            'box-shadow:0 12px 36px rgba(0,0,0,.55),inset 0 1px 0 rgba(255,255,255,.06);font:12px/1.4 system-ui,sans-serif;' +
            // closed state stays rendered (not display:none) so it can animate out
            'display:block;visibility:hidden;opacity:0;pointer-events:none;transform:translateY(-8px) scale(.95);' +
            'transition:opacity .16s ease,transform .18s cubic-bezier(.4,0,1,1),visibility 0s linear .18s}' +
            '#menu.on{visibility:visible;opacity:1;pointer-events:auto;transform:none;' +
            'transition:opacity .16s ease,transform .22s cubic-bezier(.16,1,.3,1),visibility 0s}' +
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
            // ---- agent indicator + pause ----
            '#agbtn{display:none}#agbtn.ag-on{display:grid}' +
            '#agbtn svg{width:15px;height:15px}' +
            '#agbtn .agd{position:absolute;top:5px;right:6px;width:6px;height:6px;border-radius:50%;background:#5f5f7c;transition:background .2s}' +
            '#agbtn.ag-run{color:hsl(var(--h) 100% 82%)}' +
            '#agbtn.ag-run .agd{background:hsl(var(--h) 100% 72%);animation:agpulse 1.15s ease-out infinite}' +
            '#agbtn.ag-run svg{animation:agbob 1.15s ease-in-out infinite}' +
            '#agbtn.ag-pause{color:#f2c14e}#agbtn.ag-pause .agd{display:none}' +
            '#agbtn .agk{position:absolute;top:1px;right:0;min-width:13px;height:13px;padding:0 3px;border-radius:7px;background:#f2c14e;color:#1a1400;font:700 9px/13px system-ui,sans-serif;text-align:center}' +
            '#agbtn.ag-shake{animation:agshake .38s}' +
            '@keyframes agpulse{0%{box-shadow:0 0 0 0 hsl(var(--h) 100% 72% / .65)}100%{box-shadow:0 0 0 7px hsl(var(--h) 100% 72% / 0)}}' +
            '@keyframes agbob{0%,100%{transform:translateY(0)}50%{transform:translateY(-1.5px)}}' +
            '@keyframes agshake{0%,100%{transform:none}25%{transform:translateX(-3px)}55%{transform:translateX(3px)}80%{transform:translateX(-1.5px)}}' +
            '#agline{position:absolute;left:0;right:0;bottom:-1px;height:2px;opacity:0;pointer-events:none;transition:opacity .3s;' +
            'background:linear-gradient(90deg,transparent,hsl(var(--h) 100% 76%),transparent) no-repeat;background-size:36% 100%}' +
            '.bar.agrun #agline{opacity:.95;animation:agsweep 1.3s linear infinite}' +
            '.bar.agpause #agline{opacity:.85;background:#f2c14e;animation:none}' +
            '@keyframes agsweep{from{background-position:-40% 0}to{background-position:140% 0}}' +
            '.mtiles{display:grid;grid-template-columns:repeat(4,1fr);gap:4px;padding:3px 3px 6px}' +
            '.mt{display:flex;flex-direction:column;align-items:center;gap:5px;padding:9px 2px 7px;border-radius:10px;color:#c9c9dc;cursor:pointer;background:rgba(255,255,255,.035);transition:background .12s,color .12s,transform .12s}' +
            '.mt:hover{background:hsl(var(--h) 100% 74% / .2);color:#fff;transform:translateY(-1px)}' +
            '.mt svg{width:17px;height:17px}' +
            '.mt span{font-size:10.5px;font-weight:600;white-space:nowrap}' +
            '#menu.on .mt,#menu.on .mzoom,#menu.on .mfoot{animation:rowin .22s cubic-bezier(.16,1,.3,1) both;animation-delay:calc(var(--i,0) * 14ms)}' +
            '.mzoom{display:flex;align-items:center;gap:8px;padding:4px 6px 4px 9px;margin:0 0 2px;border-radius:9px;background:rgba(255,255,255,.035)}' +
            '.mzl{flex:1;color:#c9c9dc}' +
            '.mzc{display:flex;align-items:center;gap:2px}' +
            '.mzc b,.mzf{width:26px;height:24px;display:grid;place-items:center;border-radius:7px;color:#c9c9dc;cursor:pointer;font:600 14px/1 system-ui,sans-serif;transition:background .1s,color .1s}' +
            '.mzc b:hover,.mzf:hover{background:hsl(var(--h) 100% 74% / .22);color:#fff}' +
            '.mzc em{font-style:normal;min-width:44px;text-align:center;color:#ececfa;font-weight:600;cursor:pointer;padding:3px 0;border-radius:6px}' +
            '.mzc em:hover{background:rgba(255,255,255,.07)}' +
            '.mzf{margin-left:2px;border-left:1px solid rgba(255,255,255,.08);border-radius:0 7px 7px 0;width:30px}' +
            '.mlabel{padding:8px 11px 3px;font-size:10px;font-weight:700;letter-spacing:.7px;text-transform:uppercase;color:#5f5f7c}' +
            '.mfoot{display:flex;justify-content:space-between;gap:8px;margin-top:2px;padding:6px 10px 3px;border-top:1px solid rgba(255,255,255,.07);font-size:11px;color:#7c7c98}' +
            '.mfoot span{cursor:pointer;padding:3px 6px;border-radius:6px;transition:background .1s,color .1s}' +
            '.mfoot span:hover{background:hsl(var(--h) 100% 74% / .18);color:#fff}' +
            '.mfoot i{font-style:normal;color:#55556f;margin-left:4px}' +
            '.smi .sc{color:#63637e;font-size:11px;margin-left:18px}' +
            // ---- search-engine flyout (⋮ → Search engine) ----
            '#smenu{position:fixed;min-width:196px;display:block;visibility:hidden;opacity:0;pointer-events:none;' +
            'transform:translateX(8px) scale(.96);transition:opacity .15s ease,transform .18s cubic-bezier(.4,0,1,1),visibility 0s linear .18s;z-index:32;padding:5px;' +
            'background:rgba(22,22,34,.97);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(255,255,255,.1);border-radius:13px;transform-origin:top right;' +
            'box-shadow:0 12px 36px rgba(0,0,0,.55),inset 0 1px 0 rgba(255,255,255,.06);font:12px/1.4 system-ui,sans-serif;' +
            '}' +
            '#smenu.on{visibility:visible;opacity:1;pointer-events:auto;transform:none;' +
            'transition:opacity .15s ease,transform .22s cubic-bezier(.16,1,.3,1),visibility 0s}' +
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
            '.dtab.on{color:#ececff}' +
            '.dtab{position:relative;z-index:1;transition:color .18s}' +
            // sliding pill that glides between tabs (sized/positioned from JS)
            '.dthead{position:relative}' +
            '.dtind{position:absolute;left:0;top:0;width:0;height:0;z-index:0;pointer-events:none;opacity:0;' +
            'border-radius:8px 8px 0 0;background:hsl(var(--h) 100% 74% / .13);box-shadow:inset 0 -2px 0 hsl(var(--h) 100% 74%);' +
            'transition:transform .32s cubic-bezier(.34,1.18,.5,1),width .32s cubic-bezier(.4,0,.2,1),opacity .15s}' +
            '.dtind.on{opacity:1}.dtind.snap{transition:none}' +
            // incoming pane: short fade + slide from the side the tab lies on
            '.dtpane.in-r{animation:dtpr .26s cubic-bezier(.16,1,.3,1)}' +
            '.dtpane.in-l{animation:dtpl .26s cubic-bezier(.16,1,.3,1)}' +
            '@keyframes dtpr{from{opacity:0;transform:translateX(18px)}to{opacity:1;transform:none}}' +
            '@keyframes dtpl{from{opacity:0;transform:translateX(-18px)}to{opacity:1;transform:none}}' +
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
            '.lhd{display:flex;align-items:center;gap:9px}' +
            '.lchk{display:inline-flex;align-items:center;gap:4px;color:#7a7a9c;font-weight:400;letter-spacing:0;' +
            'text-transform:none;font-size:10.5px;cursor:pointer;user-select:none}' +
            '.lchk input{margin:0;width:11px;height:11px;accent-color:hsl(var(--h) 85% 62%);cursor:pointer}' +
            '.lg{margin:2px 0 8px}' +
            '.lgh{display:flex;align-items:center;gap:7px;padding:2px 0;font-size:11px;cursor:pointer;min-width:0}' +
            '.lgh b{color:hsl(var(--h) 100% 79%);font-weight:600;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
            '.lgh:hover b{text-decoration:underline}' +
            '.lgr{flex:none;font-size:9px;color:#66667f;text-transform:uppercase;letter-spacing:.6px}' +
            '.lr{border-bottom:1px solid rgba(255,255,255,.03)}' +
            '.lrl{display:flex;align-items:center;gap:6px;padding:2px 0 2px 8px;cursor:pointer;min-width:0}' +
            '.lrl:hover{background:rgba(255,255,255,.04)}' +
            '.lt{flex:none;color:#8fe0cd;font-weight:600}' +
            '.lb{flex:none;padding:0 5px;border-radius:4px;background:rgba(255,255,255,.07);color:#9a9aba;font-size:9px;line-height:14px}' +
            '.lb.cap{background:hsl(var(--h) 100% 74% / .16);color:hsl(var(--h) 100% 86%)}' +
            '.lb.rm{background:rgba(232,17,35,.16);color:#ffb4b9}' +
            '.ls{min-width:0;color:#7a7a9c;font-size:10px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
            '.lx{flex:none;width:16px;height:16px;padding:0;display:grid;place-items:center;border:0;border-radius:5px;' +
            'background:transparent;color:#66667f;cursor:pointer;font-size:9.5px;opacity:0;transition:opacity .1s}' +
            '.lr:hover .lx{opacity:1}' +
            '.lx:hover{background:rgba(232,17,35,.2);color:#ffb4b9}' +
            '.lrd{display:none;padding:3px 0 7px 14px}' +
            '.lr.open .lrd{display:block}' +
            '.lrd pre{margin:2px 0 4px;padding:6px 8px;border-radius:6px;background:rgba(255,255,255,.04);color:#c9c9de;' +
            'font:10.5px/1.5 Consolas,"Cascadia Mono",monospace;white-space:pre-wrap;word-break:break-all;user-select:text;' +
            'max-height:150px;overflow:auto}' +
            '.lf{color:#8282a6;font-size:10px;line-height:1.55;user-select:text}' +
            '.lr.gone{opacity:.5}' +
            '.lr.gone .lt{text-decoration:line-through}' +
            '.lwarn{margin:3px 0 5px;padding:5px 8px;border-radius:6px;background:rgba(255,193,7,.1);color:#ffd88a;font-size:10.5px;line-height:1.45}' +
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
            '#rwb{position:relative}#rwb .rdot{position:absolute;top:5px;right:5px;width:6px;height:6px;border-radius:50%;background:#ff5f57;box-shadow:0 0 6px rgba(255,95,87,.9);display:none}' +
            '#rwb.rec .rdot{display:block;animation:rwpulse 1.6s ease-in-out infinite}' +
            '@keyframes rwpulse{50%{opacity:.3}}' +
            '#rwbar{position:fixed;left:0;right:0;top:46px;height:44px;z-index:28;display:none;align-items:center;gap:8px;padding:0 14px;' +
            'background:linear-gradient(180deg,rgba(30,24,52,.98),rgba(18,15,34,.98));border-bottom:1px solid rgba(255,255,255,.07);' +
            'box-shadow:0 12px 30px rgba(0,0,0,.4);font:12.5px var(--font);color:#cfcfe4;backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px)}' +
            '#rwbar.on{display:flex}' +
            '#rwbar .rwbrand{display:flex;align-items:center;gap:7px;color:#b9a8ff;flex:none;font-style:normal}' +
            '#rwbar .rwbrand svg{width:15px;height:15px}' +
            '#rwbar .rwbrand b{font-size:12.5px;letter-spacing:.03em;font-weight:650}' +
            '#rwbar .rwdot{width:6px;height:6px;border-radius:50%;background:#ff5f57;display:none;box-shadow:0 0 6px rgba(255,95,87,.9)}' +
            '#rwbar:not(.off) .rwdot{display:block;animation:rwpulse 1.6s ease-in-out infinite}' +
            '#rwbar .rwbtn{flex:none;width:28px;height:26px;border:0;border-radius:8px;display:grid;place-items:center;background:transparent;color:#8f8fb2;cursor:pointer}' +
            '#rwbar .rwbtn:hover{background:rgba(255,255,255,.09);color:#ececff}' +
            '#rwbar .rwbtn svg{width:13px;height:13px}' +
            '#rwbar #rwsl{flex:0 1 340px;min-width:90px;accent-color:#a78bfa;height:4px;cursor:pointer}' +
            '#rwbar #rwsl:disabled{opacity:.3;cursor:default}' +
            '#rwbar #rwlbl{flex:0 1 auto;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#9d9dbf}' +
            '#rwbar.past #rwlbl{color:#ffd479}' +
            '#rwbar #rwlbl b{color:#ececff;font-weight:600}' +
            '#rwbar .rwwarn{color:#8a8aa6}' +
            '#rwbar .rwtag{font-style:normal;color:#b9a8ff;background:rgba(167,139,250,.14);border-radius:5px;padding:1px 6px;margin-left:4px}' +
            '#rwbar .rwsp{flex:1}' +
            '#rwbar #rwq{flex:0 1 190px;min-width:80px;height:27px;padding:0 10px;border-radius:8px;border:1px solid rgba(255,255,255,.1);background:rgba(255,255,255,.05);color:#ececff;font:12px var(--font);outline:none}' +
            '#rwbar #rwq:focus{border-color:rgba(167,139,250,.6)}' +
            '#rwbar #rwlive{flex:none;height:26px;padding:0 12px;border:0;border-radius:8px;background:rgba(167,139,250,.18);color:#cbb8ff;font:600 11.5px var(--font);cursor:pointer}' +
            '#rwbar.past #rwlive{background:#a78bfa;color:#141021}' +
            '#rwbar #rwlive:hover{background:rgba(167,139,250,.34);color:#fff}' +
            '#rwbar.past #rwlive:hover{background:#b79ffb;color:#141021}' +
            '#rwview{position:fixed;left:0;right:0;top:90px;bottom:0;z-index:27;display:none;background:#0c0c14}' +
            '#rwview.on{display:block}' +
            '#rwview #rwif{position:absolute;inset:0;width:100%;height:100%;border:0;background:#fff;display:none}' +
            '#rwview.card{background:rgba(9,9,16,.72);backdrop-filter:blur(6px);-webkit-backdrop-filter:blur(6px)}' +
            '#rwcard{display:none;position:absolute;inset:0;overflow:auto}' +
            '#rwcard .rwcb{max-width:432px;margin:auto;padding:30px 34px;border-radius:18px;background:linear-gradient(180deg,rgba(32,27,54,.97),rgba(20,17,34,.97));border:1px solid rgba(255,255,255,.1);box-shadow:0 30px 80px rgba(0,0,0,.5);color:#d5d5ea;font:13px/1.55 var(--font)}' +
            '#rwcard .rwc-ic{color:#b9a8ff}#rwcard .rwc-ic svg{width:26px;height:26px}' +
            '#rwcard h2{font-size:16.5px;color:#fff;margin:12px 0 8px;font-weight:650}' +
            '#rwcard p{color:#9d9dbf;margin-bottom:12px}' +
            '#rwcard ul{margin:0 0 18px 18px;color:#9d9dbf}#rwcard li{margin:5px 0}' +
            '#rwcard .rwc-a{display:flex;gap:10px}' +
            '#rwcard #rwon{height:32px;padding:0 18px;border:0;border-radius:10px;background:linear-gradient(135deg,#a78bfa,#7c5cf0);color:#fff;font:600 12.5px var(--font);cursor:pointer}' +
            '#rwcard #rwnot{height:32px;padding:0 14px;border:1px solid rgba(255,255,255,.14);border-radius:10px;background:transparent;color:#9d9dbf;font:500 12.5px var(--font);cursor:pointer}' +
            '#rwcard #rwnot:hover{color:#ececff}' +
            '#rwmenu{position:fixed;right:14px;top:90px;width:272px;z-index:30;display:none;padding:12px;border-radius:14px;background:linear-gradient(180deg,rgba(30,26,50,.98),rgba(19,16,32,.98));border:1px solid rgba(255,255,255,.1);box-shadow:0 18px 50px rgba(0,0,0,.5);font:12.5px var(--font);color:#cfcfe4}' +
            '#rwmenu.on{display:block}' +
            '#rwmenu .rwm-h{font-size:10.5px;text-transform:uppercase;letter-spacing:.08em;color:#66667f;margin:10px 0 6px}' +
            '#rwmenu .rwm-h:first-child{margin-top:0}' +
            '#rwmenu .rwm-r{display:flex;gap:6px;flex-wrap:wrap}' +
            '#rwmenu .rwc{font-style:normal;padding:4px 10px;border-radius:8px;border:1px solid rgba(255,255,255,.1);color:#9d9dbf;cursor:pointer;font-size:11.5px}' +
            '#rwmenu .rwc:hover{color:#ececff;border-color:rgba(255,255,255,.25)}' +
            '#rwmenu .rwc.on{background:rgba(167,139,250,.2);border-color:rgba(167,139,250,.55);color:#cbb8ff}' +
            '#rwmenu .rwm-i{display:flex;align-items:center;justify-content:space-between;gap:10px;padding:8px;border-radius:9px;cursor:pointer;margin-top:2px}' +
            '#rwmenu .rwm-i:hover{background:rgba(255,255,255,.06)}' +
            '#rwmenu .rwm-i em{font-style:normal;width:16px;color:#a78bfa;display:grid;place-items:center}#rwmenu .rwm-i em svg{width:13px;height:13px}' +
            '#rwmenu .rwm-i.danger span{color:#ff8f8f}' +
            '#rwmenu .rwm-f{margin-top:10px;padding-top:9px;border-top:1px solid rgba(255,255,255,.07);color:#66667f;font-size:11px;line-height:1.45}' +
            '#rwres{position:fixed;right:14px;top:90px;width:340px;max-height:50vh;z-index:30;display:none;overflow:auto;padding:6px;border-radius:12px;background:rgba(19,16,32,.98);border:1px solid rgba(255,255,255,.1);box-shadow:0 18px 50px rgba(0,0,0,.5);font:12px var(--font)}' +
            '#rwres.on{display:block}' +
            '#rwres .rwr{display:flex;gap:10px;padding:8px 10px;border-radius:8px;cursor:pointer;color:#9d9dbf;align-items:baseline}' +
            '#rwres .rwr:hover{background:rgba(255,255,255,.07);color:#ececff}' +
            '#rwres .rwr b{color:#b9a8ff;flex:none;font-weight:600}' +
            '#rwres .rwr span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
            '#rwres .rwr.none{cursor:default;color:#66667f}' +
            '#rwmsg{position:fixed;top:96px;left:50%;transform:translateX(-50%);z-index:31;display:none;padding:7px 14px;border-radius:9px;background:rgba(19,16,32,.97);border:1px solid rgba(255,255,255,.12);color:#cfcfe4;font:12px var(--font);box-shadow:0 10px 30px rgba(0,0,0,.45)}' +
            '#rwmsg.on{display:block}' +
            '</style>' +
            '<div class="bar">' +
            '<div class="nav" id="nav"><i class="hl" id="nhl"></i>' +
            '<button class="nav-b" data-cmd="back" data-tip="Back" data-sc="Alt+←">' + SVG.back + '</button>' +
            '<button class="nav-b" data-cmd="forward" data-tip="Forward" data-sc="Alt+→">' + SVG.forward + '</button>' +
            '<button class="nav-b" id="rel" data-cmd="reload" data-tip="Reload" data-sc="Ctrl+R">' + SVG.reload + '</button>' +
            '<button class="nav-b" data-cmd="home" data-tip="New tab page">' + SVG.home + '</button>' +
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
            '<button class="tb" id="agbtn" data-tip="Agent" data-sc="Ctrl+Shift+."></button>' +
            '<button class="tb" id="pip" data-tip="Picture-in-picture" data-sc="Ctrl+Shift+P">' + SVG.pip + '</button>' +
            '<button class="tb" id="star" data-tip="Bookmark this page" data-sc="Ctrl+D">' + SVG.star + '</button>' +
            '<button class="tb" id="rwb" data-tip="Rewind — revisit this page as it was" data-sc="Ctrl+Shift+Y">' + SVG.rw + '<i class="rdot"></i></button>' +
            '<button class="tb" id="dlbtn" data-tip="Downloads" data-sc="Ctrl+J"><svg class="drg" viewBox="0 0 20 20"><circle cx="10" cy="10" r="8"/></svg>' + SVG.dl + '<i class="ddot"></i></button>' +
            '<button class="tb" id="mbtn" data-tip="Menu">' + SVG.dots + '</button></div>' +
            '<div class="wctrl" id="wctrl">' +
            '<button class="wb" id="wmin" title="Minimize">' + SVG.wmin + '</button>' +
            '<button class="wb" id="wmaxb" title="Maximize">' + SVG.wmax + '</button>' +
            '<button class="wb wcx" id="wclose" title="Close">' + SVG.close + '</button></div>' +
            '<div id="agline"></div>' +
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
            '<div id="askcard"></div>' +
            '<div id="hocard"></div>' +
            '<div id="recpill"></div><div id="reccard"></div>' +
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
            // Rewind: scrubber + frozen snapshot viewer + first-run consent card.
            '<div id="rwbar"><i class="rwbrand">' + SVG.rw + '<b>Rewind</b><span class="rwdot"></span></i>' +
            '<button class="rwbtn" id="rwprev" title="Older snapshot">' + SVG.back + '</button>' +
            '<input id="rwsl" type="range" min="0" max="0" value="0" step="1">' +
            '<button class="rwbtn" id="rwnext" title="Newer snapshot">' + SVG.forward + '</button>' +
            '<span id="rwlbl"></span><span class="rwsp"></span>' +
            '<input id="rwq" placeholder="Search snapshots\u2026" spellcheck="false">' +
            '<button id="rwlive" title="Back to the live page">Live</button>' +
            '<button class="rwbtn" id="rwgear" title="Rewind settings">' + SVG.gear + '</button>' +
            '<button class="rwbtn" id="rwx" title="Close Rewind (Esc)">' + SVG.close + '</button></div>' +
            '<div id="rwview"><iframe id="rwif" sandbox="allow-same-origin"></iframe>' +
            '<div id="rwcard"><div class="rwcb"><div class="rwc-ic">' + SVG.rw + '</div>' +
            '<h2>Rewind — time travel for this page</h2>' +
            '<p>Drag a scrubber to see this page exactly as it was minutes ago — the text, the layout, ' +
            'where you had scrolled to. Snapshots are compressed and kept only in memory.</p>' +
            '<ul><li>Records a snapshot a few seconds after the page changes</li>' +
            '<li>Never records private tabs, login pages, or sites you exclude</li>' +
            '<li>Nothing is written to disk or sent anywhere</li>' +
            '<li>Off by default — it costs some extra CPU and memory</li></ul>' +
            '<div class="rwc-a"><button id="rwon">Turn on Rewind</button>' +
            '<button id="rwnot">Not now</button></div></div></div></div>' +
            '<div id="rwmenu"></div><div id="rwres"></div><div id="rwmsg"></div>' +
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
            '<span class="qc" data-go="about:help">How to use</span>' +
            '<span class="qc" data-go="about:shortcuts">Shortcuts <kbd>' + (IS_MAC ? '⌘/' : 'Ctrl+/') + '</kbd></span></div>' +
            '<div class="rc" id="ntprc"><h2>Recent</h2><div class="tl" id="ntptl"></div></div>' +
            '<div class="hn"><i></i><span>MCP server ready &middot; connect any client over <code>stdio</code></span></div></div>' +
            // devtools panel — Console / Network / Inspect over the page
            '<div id="dt"><div id="dtgrip"></div>' +
            '<div class="dthead"><i class="dtind" id="dtind"></i>' +
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
            '<div class="dtsec lhd">Event listeners <span class="cnt" id="inslc"></span><span class="dtsp"></span>' +
            '<label class="lchk" title="Include listeners on ancestors (delegated handlers)"><input type="checkbox" id="inslanc" checked>ancestors</label>' +
            '<label class="lchk" title="Also show recently removed listeners"><input type="checkbox" id="inslrem">removed</label></div>' +
            '<div id="inslist"></div>' +
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
                syncDtTabs(sh, true);
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
        // Event listeners: toggles, row expand, jump-to-node, remove (all delegated).
        sh.getElementById('inslanc').addEventListener('change', function (e) { insLAnc = e.target.checked; renderInsListeners(sh, insSel); });
        sh.getElementById('inslrem').addEventListener('change', function (e) { insLRem = e.target.checked; renderInsListeners(sh, insSel); });
        sh.getElementById('inslist').addEventListener('click', function (e) {
            if (!insLData) return;
            var rm = e.target.closest('.lx');
            var row = e.target.closest('.lr');
            if (rm && row) {
                var l = insLData.groups[+row.getAttribute('data-gi')].listeners[+row.getAttribute('data-li')];
                try { l.remove(); } catch (x) { }
                renderInsDetails(sh, insSel);
                return;
            }
            if (row) { row.classList.toggle('open'); return; }
            var gh = e.target.closest('.lgh');
            if (gh) {
                var n = insLData.groups[+gh.getAttribute('data-gi')].node;
                if (n && n.nodeType === 1 && n !== insSel) insSelect(sh, n, true);
            }
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
            rawOn(document, 'mousemove', mv, true);
            rawOn(document, 'mouseup', up, true);
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

        var agB = sh.getElementById('agbtn');
        ['mousedown', 'click'].forEach(function (ev) {
            agB.addEventListener(ev, function (e) { e.stopPropagation(); e.preventDefault(); });
        });
        agB.addEventListener('click', toggleAgent);
        paintAgent(sh);

        var dlB = sh.getElementById('dlbtn');
        ['mousedown', 'click'].forEach(function (ev) {
            dlB.addEventListener(ev, function (e) { e.stopPropagation(); e.preventDefault(); });
        });
        dlB.addEventListener('click', function () { toggleDl(sh); });
        // Popover clicks must not reach the document off-listener.
        sh.getElementById('dlp').addEventListener('mousedown', function (e) { e.stopPropagation(); });

        wireRewind(sh);
        paintRwBtn(sh);

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
                if (v) send(e.altKey ? 'newtab:' + v : 'navigate:' + v); // Alt+Enter opens it in a new tab
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
        if (SPLIT) setupSplit(host, sh);
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
            rawOn(document, 'mousemove', function (e) {
                if (!state.wc || state.w) { if (curEl) setEdgeCursor(0); return; }
                setEdgeCursor(edgeCode(e.clientX, e.clientY) ? edgeCursor(edgeCode(e.clientX, e.clientY)) : 0);
            }, true);
            rawOn(document, 'mousedown', function (e) {
                if (!state.wc || state.w || dtPicking || e.button !== 0) return;
                var c = edgeCode(e.clientX, e.clientY);
                if (!c) return;
                e.preventDefault();
                setEdgeCursor(0);
                send('wresize:' + c);
            }, true);
            // Inspect-tab element picker — highlight on hover, pin on click.
            rawOn(document, 'mousemove', function (e) {
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
            rawOn(document, 'mousedown', function (e) {
                if (!dtPicking) return;
                e.preventDefault(); e.stopPropagation();
                var s = sh0();
                var el = document.elementFromPoint(e.clientX, e.clientY);
                if (s && el && el.id !== HOST_ID) insSelect(s, el);
                setPick(s, false);
            }, true);
            // Keep the pinned selection outline glued to its element.
            rawOn(document, 'scroll', function () {
                var s = sh0(); if (s && insSel) pinHighlight(s, insSel);
            }, true);
            rawOn(window, 'resize', function () {
                var s = sh0(); if (s && insSel) pinHighlight(s, insSel);
            });
            // Embedded __brainBoot (internal pages) lands during head parse —
            // retry at DOMContentLoaded in case the 0ms timer beat it.
            rawOn(document, 'DOMContentLoaded', applyBoot);
            // Keep the omnibox in sync with in-page (SPA) navigation; tick the
            // start-page clock while the overlay is up.
            setInterval(function () {
                var s = sh0(); if (!s) return;
                // A mount-time 'tabs' request can race driver init on the
                // host and get dropped — re-ask until the first push lands.
                if (!haveState && !window.__brainBoot && !bootHeld) send('tabs');
                if (!beltAsked) { beltAsked = true; send('beltget'); }
                if (!recAsked) { recAsked = true; send('recget'); }
                if (!(SPLIT && ROLE === 'page')) updateAddr(s);
                if (state.ov && !(SPLIT && ROLE === 'bar')) ntpTick(s);
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
                    // {b:47} carries approval ids: it must never reach page scripts.
                    if (INTERNAL_DOC && !(j && (j.b === 47 || j.b === 48 || j.b === 49))) try { document.dispatchEvent(new CustomEvent('brain-msg', { detail: j })); } catch (e0) { }
                    if (j && j.b === 60) { if (ROLE === 'page') runPageCmd(j.c); return; }
                    if (j && j.split && !SPLIT) SPLIT = true;
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
                        if (j.ag) { state.ag = j.ag; if (j.ag.n > 0) agRunUntil = Math.max(agRunUntil, Date.now() + 2200); }
                        if (j.se) state.se = j.se;
                        if (Array.isArray(j.sel)) state.sel = j.sel;
                        state.keys = j.keys || {};
                        rebuildBinds();
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
                        exposeState();
                        // Split mode: each view only does its own half — the page
                        // view never draws the (hidden) bar, the bar view never
                        // draws the start-page overlay.
                        var drawBar = !(SPLIT && ROLE === 'page'), drawNtp = !(SPLIT && ROLE === 'bar');
                        if (drawBar) { render(sh); syncWin(sh); }
                        if (drawNtp) syncNtp(sh);
                        if (drawBar) {
                            paintAgent(sh);
                            updateAddr(sh);
                            updateStar(sh);
                            var om = sh.getElementById('omni');
                            if (om) om.classList.toggle('priv', !!state.privActive);
                        }
                        rwApply(j, sh);
                        if (j.dt && !dtOpen) toggleDt(sh, j.dt, true);
                        if (editing) renderDrops(sh);
                        if (INTERNAL_DOC) try {
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
                    } else if (j && j.b === 48) {
                        onRec(sh, j.rec, j.done);
                    } else if (j && j.b === 49) {
                        onHandoff(sh, j.ho);
                    } else if (j && j.b === 47) {
                        onBelt(sh, j.belt);
                    } else if (j && j.b === 40) {
                        setAgent(sh, j.ag);
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
                    } else if (j && j.b === 71) {
                        onRwList(sh, j);
                    } else if (j && j.b === 72) {
                        onRwSnap(sh, j);
                    } else if (j && j.b === 73) {
                        onRwSearch(sh, j);
                    } else if (j && j.b === 74) {
                        rwCapture(j.tag || 'agent', true); // host asks for a (usually agent-tagged) snapshot
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
        rwSync(); // no-op unless Rewind is enabled in the boot/pushed state
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
        else {
            var want = resPx();
            if (document.documentElement && document.documentElement.style.marginTop !== (want ? want + 'px' : '')) reserveSpace();
        }
    }, 3000);
})();
