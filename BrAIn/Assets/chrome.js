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
    var statusEl = null;   // link-hover status bubble node (inside shadow)
    var wired = false;     // document/window listeners attach once — remounts must not duplicate them
    var lastSig = '';      // tab-strip render signature — skip DOM rebuilds when unchanged
    var findOpen = false;  // find-in-page bar visible
    var lastScrollReport = 0;
    state.bmarks = []; state.bm = false; state.zoom = 100; state.topmost = false;
    state.sess = 0; state.privActive = false;
    state.ov = false;      // active tab is a blank new tab painted as an in-doc overlay
    state.wc = false;      // chromeless window — show our own min/max/close
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
        wmin: '<svg viewBox="0 0 16 16"><path d="M3.6 8h8.8" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>',
        wmax: '<svg viewBox="0 0 16 16"><rect x="3.6" y="3.6" width="8.8" height="8.8" rx="1" fill="none" stroke="currentColor" stroke-width="1.5"/></svg>',
        wres: '<svg viewBox="0 0 16 16"><rect x="4.8" y="4.8" width="7.2" height="7.2" rx="1" fill="none" stroke="currentColor" stroke-width="1.4"/><path d="M6.6 4.8v-.6a1 1 0 0 1 1-1h3.6a1 1 0 0 1 1 1v3.6a1 1 0 0 1-1 1h-.6" fill="none" stroke="currentColor" stroke-width="1.4"/></svg>'
    };

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
    // nothing stays permanently covered at scroll position 0. Fixed/sticky
    // page headers still anchor to the viewport (unavoidable with an overlay).
    var RESERVED = '46px';
    function reserveSpace(on) {
        try {
            var de = document.documentElement;
            if (!de) return;
            if (on === false) de.style.marginTop = '';
            else de.style.setProperty('margin-top', RESERVED, 'important');
        } catch (e) { }
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
            '<span class="x" title="Close tab">' + SVG.close + '</span>';
        el.draggable = true;
        el.addEventListener('click', function (e) {
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
                dot.innerHTML = esc(letter) +
                    (icon ? '<img class="fav" src="' + esc(icon) + '" alt="" onerror="this.remove()">' : '');
            }
        }
    }

    function render(sh) {
        var strip = sh.getElementById('tabs');
        if (!strip) return;
        // Signature check: state pushes arrive on every load event; reconcile
        // only when tab data actually changed.
        var sig = ((state.loading && loadSlow) ? 'L' : '.');
        for (var i = 0; i < state.tabs.length; i++) {
            var t0 = state.tabs[i];
            sig += t0.id + ',' + (t0.active ? 1 : 0) + ',' + (t0.title || '') + ',' + (t0.icon || '') + ';';
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
        if (p) p.classList.toggle('on', !!(state.loading && loadSlow));
    }

    function updateAddr(sh) {
        var addr = sh.getElementById('addr');
        var lock = sh.getElementById('lock');
        if (!addr || editing) return;
        var url = location.href;
        if (url === 'about:blank' || state.ov) {
            addr.innerHTML = '<span class="dim">Search or type a URL</span>';
            if (lock) { lock.style.opacity = '0'; lock.title = ''; }
            return;
        }
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

    function filteredHist() {
        var s = sh0();
        if (!s) return [];
        // Before the user types, show everything (like a real omnibox).
        var q = dirty ? (s.getElementById('addr').textContent || '').trim().toLowerCase() : '';
        var seen = {};
        var out = [];
        // Bookmarks pin to the top of the dropdown.
        for (var i = 0; i < state.bmarks.length && out.length < 3; i++) {
            var b = state.bmarks[i];
            var bu = b && b.u;
            if (!bu || seen[bu]) continue;
            if (q && bu.toLowerCase().indexOf(q) < 0 && (b.t || '').toLowerCase().indexOf(q) < 0) continue;
            seen[bu] = 1;
            out.push({ u: bu, i: b.i, bm: 1 });
        }
        for (var j = 0; j < state.hist.length && out.length < 9; j++) {
            var it = state.hist[j];
            var u = typeof it === 'string' ? it : it.u;
            if (!u || seen[u]) continue;
            if (q && u.toLowerCase().indexOf(q) < 0) continue;
            seen[u] = 1;
            out.push({ u: u, i: (typeof it === 'object' ? it.i : null), bm: 0 });
        }
        return out;
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
            var u = it.u;
            var ic = it.i
                ? '<img class="fav" src="' + esc(it.i) + '" alt="" onerror="this.remove()">' : '';
            html += '<div class="drop' + (i === dropIdx ? ' sel' : '') + '" data-i="' + i + '">' +
                (it.bm ? '<span class="bmstar">' + SVG.starF + '</span>' : '') +
                ic + '<span class="du">' + prettyUrl(u) + '</span></div>';
        }
        box.innerHTML = html;
        box.style.display = '';
        box.querySelectorAll('.drop').forEach(function (el) {
            el.addEventListener('mousedown', function (e) {
                e.preventDefault(); e.stopPropagation(); // keep the omnibox focused
                var i = +el.getAttribute('data-i');
                var it = filteredHist()[i];
                if (it) send('navigate:' + it.u);
                finishEditing(sh);
            });
            // The click after mousedown must not bubble to .omni (it would
            // re-enter editing mode right after we picked an item).
            el.addEventListener('click', function (e) { e.stopPropagation(); });
        });
    }

    function sh0() { var h = document.getElementById(HOST_ID); return h ? h.shadowRoot : null; }

    function startEditing(sh) {
        var addr = sh.getElementById('addr');
        if (editing || !addr) return;
        editing = true;
        dirty = false;
        addr.classList.add('edit');
        addr.contentEditable = 'true';
        addr.textContent = location.href === 'about:blank' ? '' : location.href;
        addr.focus();
        var r = document.createRange();
        r.selectNodeContents(addr);
        var s = getSelection();
        s.removeAllRanges(); s.addRange(r);
        dropIdx = -1;
        renderDrops(sh);
    }

    function finishEditing(sh) {
        var addr = sh.getElementById('addr');
        editing = false;
        dropIdx = -1;
        if (addr) {
            addr.classList.remove('edit');
            addr.contentEditable = 'false';
        }
        renderDrops(sh);
        updateAddr(sh);
    }

    // ---------------- page context menu ----------------
    // WebView2 suppresses its own context menu when the page handles
    // contextmenu, so our preventDefault fully replaces it. Editable fields
    // are left alone — the native cut/copy/paste menu belongs there.

    function showPageMenu(sh, x, y, link, hasSel) {
        var m = sh.getElementById('pmenu');
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
        var html = '';
        for (var i = 0; i < items.length; i++)
            html += items[i] === null ? '<div class="msep"></div>' :
                '<div class="mi" data-c="' + items[i][0] + '">' + items[i][1] + '</div>';
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
                else send(c); // back/forward/reload
            });
        });
        var off = function () {
            m.style.display = 'none';
            document.removeEventListener('mousedown', off, true);
        };
        setTimeout(function () { document.addEventListener('mousedown', off, true); }, 0);
    }

    function wirePageMenu() {
        document.addEventListener('contextmenu', function (e) {
            var sh = sh0(); if (!sh) return;
            var t = e.target;
            // events from inside our shadow chrome retarget to the host node —
            // the tab strip/context menus handle those themselves
            if (t && t.id === HOST_ID) return;
            // editable fields keep the native editing menu
            if (t && t.closest && t.closest('input,textarea,[contenteditable="true"],[contenteditable=""]')) return;
            e.preventDefault(); e.stopPropagation();
            var a = t && t.closest ? t.closest('a[href]') : null;
            var href = null;
            if (a) { href = a.getAttribute('href'); try { href = new URL(href, location.href).href; } catch (e2) { href = null; } }
            var sel = '';
            try { sel = (getSelection() || '').toString().trim(); } catch (e3) { }
            showPageMenu(sh, e.clientX, e.clientY, href, !!sel);
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
            null,
            ['find', 'Find in page', 'Ctrl+F'],
            ['bm', (state.bm ? 'Remove bookmark' : 'Bookmark this page'), 'Ctrl+D'],
            ['copyurl', 'Copy link address', 'Ctrl+Shift+C'],
            null,
            ['zoomout', 'Zoom out', 'Ctrl+−'],
            ['zoomreset', 'Reset zoom — ' + state.zoom + '%', 'Ctrl+0'],
            ['zoomin', 'Zoom in', 'Ctrl++'],
            null,
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
        menuItems().forEach(function (it) {
            html += it === null ? '<div class="msep"></div>' :
                '<div class="mi" data-c="' + it[0] + '"><span>' + it[1] + '</span><span class="sc">' + it[2] + '</span></div>';
        });
        m.innerHTML = html;
        m.classList.add('on');
        m.querySelectorAll('.mi').forEach(function (el) {
            el.addEventListener('mousedown', function (e) {
                e.preventDefault(); e.stopPropagation();
                m.classList.remove('on');
                menuCmd(sh, el.getAttribute('data-c'));
            });
        });
        var off = function () {
            m.classList.remove('on');
            document.removeEventListener('mousedown', off, true);
        };
        setTimeout(function () { document.addEventListener('mousedown', off, true); }, 0);
    }

    function menuCmd(sh, c) {
        // On the start-page overlay, page-scoped actions would hit the hidden document.
        if (state.ov && (c === 'find' || c === 'print' || c === 'viewsource' || c === 'copyurl' || c === 'bm')) return;
        if (c === 'find') { openFind(sh); return; }
        if (c === 'devtools') { toggleDt(sh); return; }
        if (c === 'print') { try { window.print(); } catch (e) { } return; }
        if (c === 'viewsource') { send('newtab:view-source:' + location.href); return; }
        if (c === 'help') { send('newtab:about:help'); return; }
        if (c === 'newpriv') { openNewTab(true); return; }
        if (c === 'newtab') { openNewTab(false); return; }
        if (c === 'copyurl') { copyText(location.href); return; }
        if (c === 'restore') { send('restoresession'); return; }
        send(c);
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
        window.addEventListener('scroll', function (e) {
            if (e.target !== document) return; // window scroll only, not nested scrollers
            var n = Date.now();
            if (n - lastScrollReport < 300) return;
            lastScrollReport = n;
            send('scroll:' + location.href + '|' + Math.round(window.scrollX || 0) + ',' + Math.round(window.scrollY || 0));
        }, { passive: true, capture: true });
    }

    // ---------------- bookmark star ----------------

    function updateStar(sh) {
        var s = sh.getElementById('star');
        if (!s) return;
        var http = /^https?:/.test(location.href) && !state.privActive && !state.ov;
        s.style.display = http ? 'grid' : 'none';
        s.innerHTML = state.bm ? SVG.starF : SVG.star;
        s.classList.toggle('yes', !!state.bm);
        s.title = state.bm ? 'Remove bookmark (Ctrl+D)' : 'Bookmark this page (Ctrl+D)';
    }

    // ---------------- keyboard shortcuts ----------------

    function wireShortcuts() {
        window.addEventListener('keydown', function (e) {
            var sh = sh0(); // resolve live — a remount swaps the shadow root
            if (!sh) return;
            var mod = e.ctrlKey || e.metaKey;
            var k = e.key;
            if (mod && (k === 'l' || k === 'L')) {
                e.preventDefault(); e.stopPropagation();
                startEditing(sh);
            } else if (mod && e.shiftKey && (k === 't' || k === 'T')) {
                e.preventDefault(); e.stopPropagation();
                send('reopen'); // Ctrl+Shift+T reopens the last closed tab
            } else if (mod && e.shiftKey && (k === 'n' || k === 'N')) {
                e.preventDefault(); e.stopPropagation();
                openNewTab(true); // Ctrl+Shift+N — incognito-style tab
            } else if (mod && e.shiftKey && (k === 'c' || k === 'C')) {
                e.preventDefault(); e.stopPropagation();
                if (!state.ov) copyText(location.href); // Ctrl+Shift+C copies the page URL
            } else if (mod && e.shiftKey && (k === 'i' || k === 'I')) {
                e.preventDefault(); e.stopPropagation();
                toggleDt(sh); // Ctrl+Shift+I
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
                if (dtPicking) setPick(sh, false);
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
    }

    // ---------------- devtools panel ----------------
    var dtOpen = false, dtTab = 'con', dtH = 340;
    var dtConSig = '', dtNetSig = '', dtPicking = false, dtReplBusy = false;
    var dtScrollPin = true; // console auto-scrolls only when parked at bottom

    function toggleDt(sh, tab) {
        var p = sh.getElementById('dt');
        if (!p) return;
        dtOpen = !dtOpen;
        if (dtOpen && tab) dtTab = tab;
        p.style.display = dtOpen ? 'flex' : 'none';
        if (dtOpen) { p.style.height = dtH + 'px'; syncDtTabs(sh); send('dtget'); }
        if (!dtOpen && dtPicking) setPick(sh, false);
    }

    function syncDtTabs(sh) {
        sh.querySelectorAll('.dtab').forEach(function (b) {
            b.classList.toggle('on', b.getAttribute('data-t') === dtTab);
        });
        ['con', 'net', 'ins'].forEach(function (t) {
            var p = sh.getElementById('dtp' + t);
            if (p) p.classList.toggle('on', t === dtTab);
        });
    }

    function fmtBytes(n) {
        n = +n || 0;
        return n >= 1048576 ? (n / 1048576).toFixed(1) + ' MB' :
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
        var ns = net.length + '|' + (net.length ? net[net.length - 1].u + net[net.length - 1].st : '');
        if (ns !== dtNetSig) {
            dtNetSig = ns;
            var nl = sh.getElementById('dtnet');
            if (nl) {
                var h2 = '';
                for (var k = 0; k < net.length; k++) {
                    var n = net[k];
                    var st = n.er ? 'ERR' : (n.st || '…');
                    var cls = n.er ? 's0' : 's' + String(n.st || 0)[0];
                    h2 += '<div class="dnetr"><span class="' + cls + '">' + st + '</span>' +
                        '<span>' + esc(n.m || '') + '</span><span style="color:#77779a">' + esc(n.ty || '') + '</span>' +
                        '<span style="color:#77779a">' + (n.sz ? fmtBytes(n.sz) : '') + '</span>' +
                        '<span class="u" title="' + esc(n.u || '') + '">' + esc(n.u || '') + '</span></div>';
                }
                nl.innerHTML = h2;
            }
        }
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

    function describeEl(el) {
        if (!el || !el.tagName) return '';
        var s = '<' + el.tagName.toLowerCase();
        if (el.id) s += ' id="' + el.id + '"';
        if (el.className && typeof el.className === 'string')
            s += ' class="' + el.className.trim().slice(0, 120) + '"';
        s += '>';
        var r = el.getBoundingClientRect();
        var meta = s + '  ·  ' + Math.round(r.width) + '×' + Math.round(r.height) +
            ' @ ' + Math.round(r.x) + ',' + Math.round(r.y);
        var attrs = '';
        for (var i = 0; i < el.attributes.length && i < 12; i++)
            attrs += el.attributes[i].name + '="' + el.attributes[i].value.slice(0, 80) + '"  ';
        var html = (el.outerHTML || '').slice(0, 800);
        var txt = (el.innerText || '').trim().slice(0, 200);
        return meta + '\n\nattributes: ' + (attrs.trim() || '—') +
            '\n\ntext: ' + (txt || '—') + '\n\nhtml: ' + html;
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
        state.w = !!b.w;
        state.sess = b.sess || 0;
        state.privActive = !!b.privActive;
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

    function ntpTick(sh) {
        var d = new Date(), hh = d.getHours(), mm = d.getMinutes();
        var ck = sh.getElementById('ntpck'), dt = sh.getElementById('ntpdt');
        if (ck) ck.textContent = (hh < 10 ? '0' : '') + hh + ':' + (mm < 10 ? '0' : '') + mm;
        if (dt) dt.textContent = d.toLocaleDateString(undefined, { weekday: 'long', month: 'long', day: 'numeric' });
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
            html += '<div class="ti" data-u="' + esc(u) + '" title="' + esc(u) + '">' +
                '<span class="ic" style="background:' + tabHue(h) + '">' + esc((h.charAt(0) || '?').toUpperCase()) +
                (icon ? '<img src="' + esc(icon) + '" alt="" onerror="this.remove()">' : '') +
                '</span><span>' + esc(h) + '</span></div>';
        }
        tl.innerHTML = html;
        box.classList.toggle('has', !!html);
    }

    function syncNtp(sh) {
        var n = sh.getElementById('ntp');
        if (!n) return;
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
        sh.getElementById('ntphp').addEventListener('click', function () { send('navigate:about:help'); });
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
        host.style.cssText = 'all:initial;position:fixed;top:0;left:0;right:0;height:46px;z-index:2147483647;';
        var sh = host.attachShadow({ mode: 'open' });
        sh.innerHTML =
            '<style>' +
            ':host{all:initial}* {box-sizing:border-box;margin:0;padding:0}' +
            '.bar{position:fixed;top:0;left:0;right:0;height:46px;display:flex;align-items:center;gap:10px;z-index:10;' +
            'padding:0 12px;' +
            // near-opaque: a backdrop blur here was invisible yet re-rastered
            // on every document's first frame
            'background:linear-gradient(180deg,rgba(23,23,36,.985),rgba(15,15,25,.98));' +
            'border-bottom:1px solid rgba(255,255,255,.07);' +
            'box-shadow:inset 0 1px 0 rgba(255,255,255,.05),0 8px 28px rgba(0,0,0,.4);' +
            'font:12px/1.45 "Segoe UI Variable Text",system-ui,-apple-system,"Segoe UI",sans-serif;color:#c9c9dc;' +
            'user-select:none;-webkit-user-select:none;' +
            'animation:slidein .24s cubic-bezier(.16,1,.3,1)}' +
            '@keyframes slidein{from{transform:translateY(-8px);opacity:0}to{transform:none;opacity:1}}' +
            '@keyframes pop{from{transform:scale(.94) translateY(-4px);opacity:0}to{transform:none;opacity:1}}' +
            '#prog{position:absolute;left:0;right:0;bottom:-1px;height:2px;overflow:hidden;opacity:0;transition:opacity .35s}' +
            '#prog.on{opacity:1}' +
            '#prog i{position:absolute;top:0;bottom:0;width:38%;border-radius:2px;' +
            'background:linear-gradient(90deg,#7c7cff,#4ec9b0);' +
            'box-shadow:0 0 10px rgba(124,124,255,.6);animation:sweep 1.2s cubic-bezier(.45,0,.55,1) infinite}' +
            '@keyframes sweep{0%{left:-40%}55%{left:62%}100%{left:105%}}' +
            '.nav{display:flex;gap:1px;flex:none;padding:3px;border-radius:999px;' +
            'background:rgba(255,255,255,.045);border:1px solid rgba(255,255,255,.05)}' +
            'button.nav-b{width:28px;height:28px;display:grid;place-items:center;border:0;border-radius:999px;' +
            'background:transparent;color:#a5a5c2;cursor:pointer;transition:background .14s,color .14s,transform .1s}' +
            'button.nav-b:hover{background:rgba(255,255,255,.1);color:#fff}' +
            'button.nav-b:active{transform:scale(.9)}' +
            'button.nav-b svg{width:15px;height:15px}' +
            'button.nav-b.dis{opacity:.3;pointer-events:none}' +
            '#tabs{display:flex;align-items:center;gap:5px;flex:0 1 auto;min-width:0;overflow-x:auto;scrollbar-width:none}' +
            '#tabs::-webkit-scrollbar{display:none}' +
            '.tab{display:flex;align-items:center;gap:8px;height:34px;padding:0 10px 0 8px;min-width:46px;max-width:184px;' +
            'border-radius:11px;cursor:pointer;color:#9c9cb6;border:1px solid transparent;' +
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
            'border-radius:2px;background:linear-gradient(180deg,#9c9cff,#4ec9b0)}' +
            '.tab.dropmark::before{left:2px}.tab.dropmark-r::after{right:2px}' +
            '.tab:hover{background:rgba(255,255,255,.06);color:#d8d8ec}' +
            '.tab.active{background:linear-gradient(180deg,rgba(124,124,255,.22),rgba(124,124,255,.1));' +
            'color:#ececff;border-color:rgba(140,140,255,.4);' +
            'box-shadow:0 2px 10px rgba(0,0,0,.3),inset 0 1px 0 rgba(255,255,255,.08),0 0 20px rgba(124,124,255,.14)}' +
            '.tab .dot{width:21px;height:21px;border-radius:7px;flex:none;display:grid;place-items:center;position:relative;' +
            'font-size:11px;font-weight:700;color:#fff;overflow:hidden;box-shadow:inset 0 1px 0 rgba(255,255,255,.15)}' +
            '.tab .dot .fav{position:absolute;inset:2.5px;width:16px;height:16px;border-radius:5px;' +
            'animation:iconin .18s ease both}' +
            '@keyframes iconin{from{opacity:0}to{opacity:1}}' +
            '.tab .dot.spin{background:transparent;box-shadow:none}' +
            '.tab .dot.spin::before{content:"";width:13px;height:13px;border-radius:50%;' +
            'border:2px solid rgba(124,124,255,.2);border-top-color:#9c9cff;animation:rot .65s linear infinite}' +
            '@keyframes rot{to{transform:rotate(360deg)}}' +
            '.tab .t{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:1;min-width:0;font-weight:500}' +
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
            '#tmenu,#pmenu{position:fixed;min-width:190px;display:none;z-index:30;' +
            'background:rgba(22,22,34,.96);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(255,255,255,.1);border-radius:13px;padding:5px;' +
            'transform-origin:top left;' +
            'box-shadow:0 12px 36px rgba(0,0,0,.55),inset 0 1px 0 rgba(255,255,255,.06);font:12px/1.4 system-ui,sans-serif;' +
            'animation:pop .16s cubic-bezier(.16,1,.3,1)}' +
            '.mi{padding:7px 12px;border-radius:8px;color:#c9c9dc;cursor:pointer;transition:background .1s,color .1s}' +
            '.mi:hover{background:rgba(124,124,255,.18);color:#fff}' +
            '.plus{width:30px;height:34px;flex:none;display:grid;place-items:center;border:0;border-radius:10px;' +
            'background:transparent;color:#8a8aa4;cursor:pointer;transition:background .14s,color .14s,transform .1s}' +
            '.plus:hover{background:rgba(124,124,255,.18);color:#c9c9ff}' +
            '.plus:active{transform:scale(.9)}' +
            '.plus svg{width:14px;height:14px;transition:transform .3s cubic-bezier(.16,1,.3,1)}' +
            '.plus:hover svg{transform:rotate(90deg)}' +
            '.omni{flex:1;min-width:120px;display:flex;align-items:center;gap:8px;height:34px;padding:0 14px;' +
            'border-radius:999px;background:rgba(255,255,255,.055);border:1px solid rgba(255,255,255,.08);' +
            'box-shadow:inset 0 1px 2px rgba(0,0,0,.25);cursor:text;position:relative;' +
            'transition:background .15s,border-color .15s,box-shadow .2s}' +
            '.omni:hover{background:rgba(255,255,255,.08)}' +
            '.omni:focus-within{background:rgba(255,255,255,.1);border-color:rgba(124,124,255,.55);' +
            'box-shadow:0 0 0 4px rgba(124,124,255,.14),inset 0 1px 2px rgba(0,0,0,.2)}' +
            '.omni.priv{background:rgba(109,63,160,.14);border-color:rgba(178,132,255,.3)}' +
            '.omni.priv #lock{color:#b8a3ff}' +
            '#lock{width:13px;height:13px;color:#6fd8a8;flex:none}' +
            '#lock.bad{color:#e8b45c}' +
            '#lock svg{width:100%;height:100%}' +
            '#addr{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#b9b9d0;outline:none;font-size:12.5px}' +
            '#addr b{color:#f2f2fc;font-weight:600}' +
            '#addr .dim{color:#6a6a84}' +
            '#addr.edit{color:#f2f2fc;white-space:nowrap;overflow-x:auto;scrollbar-width:none}' +
            '#addr.edit::-webkit-scrollbar{display:none}' +
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
            '.drop b{color:#f2f2fc;font-weight:600}' +
            '.drop.sel,.drop:hover{background:rgba(124,124,255,.18);color:#ececff}' +
            '#status{position:fixed;left:10px;bottom:10px;max-width:55%;padding:6px 14px;' +
            'background:rgba(22,22,34,.94);border:1px solid rgba(255,255,255,.1);border-radius:999px;' +
            'backdrop-filter:blur(14px);-webkit-backdrop-filter:blur(14px);' +
            'color:#9a9ab8;font:11.5px/1.4 system-ui,sans-serif;' +
            'overflow:hidden;text-overflow:ellipsis;white-space:nowrap;display:none;pointer-events:none;' +
            'box-shadow:0 6px 20px rgba(0,0,0,.45);' +
            'animation:statusin .18s cubic-bezier(.16,1,.3,1)}' +
            '@keyframes statusin{from{transform:translateY(8px);opacity:0}to{transform:none;opacity:1}}' +
            '#status.on{display:block}' +
            '#star{width:22px;height:22px;border-radius:999px;display:none;place-items:center;color:#8a8aa4;flex:none;cursor:pointer;transition:background .12s,color .12s}' +
            '#star:hover{background:rgba(255,255,255,.12);color:#d8d8ec}' +
            '#star.yes{color:#f2c14e}' +
            '#star svg{width:13px;height:13px}' +
            '.bmstar{width:12px;height:12px;color:#f2c14e;flex:none}' +
            '.bmstar svg{width:100%;height:100%}' +
            '#menu{position:fixed;top:44px;right:8px;min-width:236px;display:none;z-index:31;' +
            'background:rgba(22,22,34,.97);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(255,255,255,.1);border-radius:13px;padding:5px;' +
            'transform-origin:top right;' +
            'box-shadow:0 12px 36px rgba(0,0,0,.55),inset 0 1px 0 rgba(255,255,255,.06);font:12px/1.4 system-ui,sans-serif;' +
            'animation:pop .16s cubic-bezier(.16,1,.3,1)}' +
            '#menu.on{display:block}' +
            '#menu .mi{display:flex;justify-content:space-between;align-items:center;gap:22px}' +
            '#menu .sc{color:#63637e;font-size:11px;flex:none}' +
            '.msep{height:1px;margin:5px 10px;background:rgba(255,255,255,.07)}' +
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
            '.brand{flex:none;display:flex;align-items:center;gap:6px;padding:0 6px;' +
            'font-weight:800;font-size:12.5px;letter-spacing:.5px;transition:filter .25s;' +
            'background:linear-gradient(115deg,#9c9cff 20%,#4ec9b0 90%);' +
            '-webkit-background-clip:text;background-clip:text;color:transparent}' +
            '.brand:hover{filter:brightness(1.25)}' +
            '.brand svg{width:14px;height:14px;color:#9c9cff;transition:transform .4s cubic-bezier(.16,1,.3,1)}' +
            '.brand:hover svg{transform:rotate(180deg)}' +
            // ---- devtools panel ----
            '#dt{position:fixed;left:0;right:0;bottom:0;height:340px;display:none;flex-direction:column;z-index:20;' +
            'background:linear-gradient(180deg,rgba(18,18,28,.995),rgba(12,12,20,.995));' +
            'border-top:1px solid rgba(255,255,255,.1);box-shadow:0 -14px 44px rgba(0,0,0,.55);' +
            'font:12px/1.45 "Segoe UI Variable Text",system-ui,sans-serif;color:#c9c9dc;' +
            'user-select:text;-webkit-user-select:text;animation:dtin .18s cubic-bezier(.16,1,.3,1)}' +
            '@keyframes dtin{from{transform:translateY(18px);opacity:0}to{transform:none;opacity:1}}' +
            '#dtgrip{height:5px;margin-top:-3px;cursor:ns-resize;flex:none;position:relative;z-index:2}' +
            '.dthead{display:flex;align-items:center;gap:2px;padding:0 10px;height:35px;flex:none;' +
            'border-bottom:1px solid rgba(255,255,255,.06)}' +
            '.dtab{padding:6px 12px;border:0;border-radius:8px 8px 0 0;background:transparent;color:#8f8fb0;' +
            'font:600 11.5px/1 "Segoe UI Variable Text",system-ui,sans-serif;cursor:pointer;transition:color .12s}' +
            '.dtab:hover{color:#d8d8ec}' +
            '.dtab.on{color:#ececff;background:rgba(124,124,255,.13);box-shadow:inset 0 -2px 0 #7c7cff}' +
            '.dtsp{flex:1}' +
            '.dtbtn{padding:4px 10px;border:0;border-radius:7px;background:transparent;color:#8f8fb0;font-size:11px;cursor:pointer}' +
            '.dtbtn:hover{background:rgba(255,255,255,.09);color:#fff}' +
            '.dtbtn.armed{color:#b9b9ff;background:rgba(124,124,255,.18)}' +
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
            '.crow.out .ct{color:#7c7cff}' +
            '.dneth,.dnetr{display:grid;grid-template-columns:52px 62px 78px 74px 1fr;gap:10px;padding:2px 14px}' +
            '.dneth{color:#66667f;font-size:10.5px;letter-spacing:.6px;text-transform:uppercase;border-bottom:1px solid rgba(255,255,255,.08);position:sticky;top:0;background:#101018;flex:none}' +
            '.dnetr{border-bottom:1px solid rgba(255,255,255,.03);font:11.5px/1.55 Consolas,"Cascadia Mono",monospace}' +
            '.dnetr .u{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:#a8a8c8;direction:rtl;text-align:left}' +
            '.dnetr .s2{color:#7ee0a3}.dnetr .s3{color:#7cc4ff}.dnetr .s4{color:#ffb86b}.dnetr .s5,.dnetr .s0{color:#ff8a94}' +
            '.dtrepl{display:flex;align-items:center;gap:9px;border-top:1px solid rgba(255,255,255,.08);padding:0 14px;flex:none;height:32px}' +
            '.dtrepl input{flex:1;border:0;outline:0;background:transparent;color:#e8e8f4;font:12px/1 Consolas,"Cascadia Mono",monospace}' +
            '.dtrepl .pr{color:#7c7cff;font-weight:700}' +
            '.ins-top{display:flex;align-items:center;gap:10px;padding:6px 14px;border-bottom:1px solid rgba(255,255,255,.07);flex:none}' +
            '#dtins{flex:1;overflow:auto;margin:0;padding:9px 14px;white-space:pre-wrap;word-break:break-word;' +
            'font:11.5px/1.6 Consolas,"Cascadia Mono",monospace;color:#b9b9d4}' +
            '#dthl{position:fixed;display:none;pointer-events:none;z-index:24;border:1.5px solid #8c8cff;' +
            'background:rgba(124,124,255,.13);border-radius:3px;transition:all .06s}' +
            '#dttip{position:fixed;display:none;pointer-events:none;z-index:24;padding:2px 8px;border-radius:6px;' +
            'background:#1d1d2e;border:1px solid rgba(140,140,255,.5);color:#d8d8f0;font:11px/1.5 Consolas,monospace;white-space:nowrap}' +
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
            // Same look as newtab.html; top:46 + height:100vh centers content
            // exactly like the real start-page document, so the two are
            // interchangeable without a visible jump.
            '#ntp{position:fixed;left:0;right:0;top:46px;height:100vh;display:none;z-index:5;overflow:hidden;' +
            'flex-direction:column;align-items:center;justify-content:center;background:#0b0b13;color:#e8e8f0;' +
            'font:13px/1.45 "Segoe UI Variable Text",system-ui,-apple-system,"Segoe UI",sans-serif;' +
            'user-select:none;-webkit-user-select:none;cursor:default;animation:ntpin .14s ease-out}' +
            '#ntp.on{display:flex}' +
            '@keyframes ntpin{from{opacity:0}to{opacity:1}}' +
            '#ntp .orb{position:absolute;border-radius:50%;opacity:.5;pointer-events:none;will-change:transform}' +
            '#ntp .oa{width:760px;height:760px;top:-366px;left:-240px;' +
            'background:radial-gradient(circle closest-side,rgba(74,63,184,.85),rgba(74,63,184,.32) 45%,rgba(74,63,184,0));' +
            'animation:drift1 26s ease-in-out infinite alternate}' +
            '#ntp .ob{width:680px;height:680px;bottom:-254px;right:-220px;' +
            'background:radial-gradient(circle closest-side,rgba(31,122,104,.85),rgba(31,122,104,.32) 45%,rgba(31,122,104,0));' +
            'animation:drift2 30s ease-in-out infinite alternate}' +
            '#ntp .oc{width:480px;height:480px;top:calc(40% - 136px);right:calc(22% - 90px);opacity:.28;' +
            'background:radial-gradient(circle closest-side,rgba(109,63,160,.85),rgba(109,63,160,.3) 45%,rgba(109,63,160,0));' +
            'animation:drift1 34s ease-in-out infinite alternate-reverse}' +
            '#ntp.priv .ob{background:radial-gradient(circle closest-side,rgba(124,60,200,.85),rgba(124,60,200,.32) 45%,rgba(124,60,200,0))}' +
            '@keyframes drift1{from{transform:translate(0,0) scale(1)}to{transform:translate(70px,40px) scale(1.12)}}' +
            '@keyframes drift2{from{transform:translate(0,0) scale(1.05)}to{transform:translate(-60px,-50px) scale(.95)}}' +
            '#ntp>*:not(.orb){position:relative}' +
            '#ntp .ck{font-size:64px;font-weight:200;letter-spacing:1px;color:#f2f2fc;font-variant-numeric:tabular-nums;line-height:1;margin-bottom:4px}' +
            '#ntp .dt{font-size:13px;color:#7a7a96;margin-bottom:42px;letter-spacing:.3px}' +
            '#ntp .lg{font-size:52px;font-weight:800;letter-spacing:2px;margin-bottom:8px;line-height:1.45;' +
            'background:linear-gradient(120deg,#9c9cff,#4ec9b0);-webkit-background-clip:text;background-clip:text;color:transparent}' +
            '#ntp .tg{color:#8a8aa0;margin-bottom:38px;font-size:14px}' +
            '#ntp .tg b{color:#4ec9b0;font-weight:600}' +
            '#ntp .pv{display:none;margin:-26px 0 26px;padding:5px 14px;border-radius:999px;font-size:12px;font-weight:600;' +
            'color:#c9b8ff;background:rgba(124,92,255,.14);border:1px solid rgba(140,110,255,.3);align-items:center;gap:7px}' +
            '#ntp .pv svg{width:14px;height:14px}' +
            '#ntp.priv .pv{display:inline-flex}' +
            '#ntp form{display:flex;gap:10px;width:min(640px,86vw);padding:8px;border-radius:999px;' +
            'background:rgba(255,255,255,.05);border:1px solid rgba(255,255,255,.08);' +
            'box-shadow:0 12px 40px rgba(0,0,0,.35),inset 0 1px 0 rgba(255,255,255,.05);transition:border-color .2s,box-shadow .2s}' +
            '#ntp form:focus-within{border-color:rgba(124,124,255,.45);' +
            'box-shadow:0 12px 40px rgba(0,0,0,.35),0 0 0 4px rgba(124,124,255,.12),inset 0 1px 0 rgba(255,255,255,.05)}' +
            '#ntp input{flex:1;padding:13px 18px;border:none;outline:none;background:transparent;color:#eeeefc;' +
            'font:15px "Segoe UI Variable Text",system-ui,sans-serif;user-select:text;-webkit-user-select:text}' +
            '#ntp input::placeholder{color:#5e5e78}' +
            '#ntp button{padding:12px 26px;border-radius:999px;border:none;cursor:pointer;' +
            'background:linear-gradient(120deg,#7c7cff,#6868f0);color:#fff;font:600 14px system-ui,sans-serif;letter-spacing:.2px;' +
            'transition:filter .15s,transform .1s;box-shadow:0 4px 16px rgba(124,124,255,.35)}' +
            '#ntp button:hover{filter:brightness(1.12)}#ntp button:active{transform:scale(.96)}' +
            '#ntp .rc{display:none;margin-top:46px;width:min(700px,88vw)}' +
            '#ntp .rc.has{display:block}' +
            '#ntp .rc h2{font-size:11px;font-weight:600;letter-spacing:1.4px;text-transform:uppercase;color:#5f5f7c;margin-bottom:14px;text-align:center}' +
            '#ntp .tl{display:grid;grid-template-columns:repeat(4,1fr);gap:12px}' +
            '#ntp .ti{display:flex;align-items:center;gap:10px;padding:13px 14px;cursor:pointer;overflow:hidden;white-space:nowrap;' +
            'background:rgba(255,255,255,.04);border:1px solid rgba(255,255,255,.07);border-radius:14px;color:#c5c5dd;' +
            'font-size:12.5px;font-weight:500;transition:transform .15s,border-color .15s,background .15s,color .15s}' +
            '#ntp .ti:hover{transform:translateY(-2px);background:rgba(255,255,255,.07);border-color:rgba(124,124,255,.4);' +
            'color:#f0f0fc;box-shadow:0 8px 24px rgba(0,0,0,.3)}' +
            '#ntp .ic{width:26px;height:26px;flex:none;border-radius:8px;display:grid;place-items:center;font-size:12px;' +
            'font-weight:700;color:#fff;position:relative;box-shadow:inset 0 1px 0 rgba(255,255,255,.18)}' +
            '#ntp .ic img{position:absolute;inset:4px;width:18px;height:18px;border-radius:5px}' +
            '#ntp .ti>span:last-child{overflow:hidden;text-overflow:ellipsis}' +
            '#ntp .hn{position:absolute;bottom:72px;color:#4c4c62;font-size:12.5px;max-width:620px;text-align:center;line-height:1.7;padding:0 20px}' +
            '#ntp .hn code{background:rgba(255,255,255,.06);border:1px solid rgba(255,255,255,.08);border-radius:6px;padding:1px 7px;color:#9a9ab8;font-size:12px}' +
            '#ntp .hp{display:inline-block;margin-top:8px;padding:6px 16px;cursor:pointer;border-radius:999px;' +
            'border:1px solid rgba(255,255,255,.09);background:rgba(255,255,255,.05);color:#8f8fb0;font-size:12px;font-weight:600;' +
            'transition:background .15s,color .15s,border-color .15s}' +
            '#ntp .hp:hover{background:rgba(124,124,255,.12);border-color:rgba(124,124,255,.35);color:#c9c9ff}' +
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
            '<span id="star" title="Bookmark this page (Ctrl+D)">' + SVG.star + '</span>' +
            '<div id="drops"></div></div>' +
            '<div class="brand">' + SVG.sparkle + '<span>BrAIn</span></div>' +
            '<button class="nav-b" id="mbtn" title="Menu">' + SVG.dots + '</button>' +
            '<div class="wctrl" id="wctrl">' +
            '<button class="wb" id="wmin" title="Minimize">' + SVG.wmin + '</button>' +
            '<button class="wb" id="wmaxb" title="Maximize">' + SVG.wmax + '</button>' +
            '<button class="wb wcx" id="wclose" title="Close">' + SVG.close + '</button></div>' +
            '<div id="prog"><i></i></div>' +
            '</div>' +
            '<div id="status"></div>' +
            '<div id="tmenu"></div>' +
            '<div id="pmenu"></div>' +
            '<div id="menu"></div>' +
            '<div id="fbar">' + SVG.mag +
            '<input id="fin" spellcheck="false" placeholder="Find in page">' +
            '<span id="fcnt"></span>' +
            '<button class="fb" data-f="-1" title="Previous (Shift+Enter)">' + SVG.up + '</button>' +
            '<button class="fb" data-f="1" title="Next (Enter)">' + SVG.dn + '</button>' +
            '<button class="fb" id="fclose" title="Close (Esc)">' + SVG.close + '</button></div>' +
            '<div id="ntp"><div class="orb oa"></div><div class="orb ob"></div><div class="orb oc"></div>' +
            '<div class="ck" id="ntpck"></div><div class="dt" id="ntpdt"></div>' +
            '<div class="lg">BrAIn</div>' +
            '<div class="tg">A browser your <b>AI</b> can drive &mdash; MCP server built in</div>' +
            '<div class="pv">' + SVG.mask + 'Private tab &mdash; no history, site data wiped on close</div>' +
            '<form id="ntpf"><input id="ntpq" placeholder="Search Google or type a URL" autocomplete="off" spellcheck="false">' +
            '<button type="submit">Go</button></form>' +
            '<div class="rc" id="ntprc"><h2>Recent</h2><div class="tl" id="ntptl"></div></div>' +
            '<div class="hn">Connect an MCP client to this process over <code>stdio</code> and the agent ' +
            'can navigate, click, type, snapshot and screenshot this browser.<br>' +
            '<span class="hp" id="ntphp">How to use &rarr;</span></div></div>' +
            // devtools panel — Console / Network / Inspect over the page
            '<div id="dt"><div id="dtgrip"></div>' +
            '<div class="dthead">' +
            '<button class="dtab on" data-t="con">Console</button>' +
            '<button class="dtab" data-t="net">Network</button>' +
            '<button class="dtab" data-t="ins">Inspect</button>' +
            '<span class="dtsp"></span>' +
            '<button class="dtbtn" id="dtclear">Clear</button>' +
            '<button class="dtbtn" id="dtx" title="Close (F12)">✕</button></div>' +
            '<div id="dtbody">' +
            '<div class="dtpane on" id="dtpcon">' +
            '<div class="dtscroll" id="dtlist"></div>' +
            '<div class="dtrepl"><span class="pr">›</span><input id="dtin" spellcheck="false" placeholder="Evaluate JavaScript in page context…"></div></div>' +
            '<div class="dtpane" id="dtpnet">' +
            '<div class="dneth"><span>Status</span><span>Method</span><span>Type</span><span>Size</span><span>URL</span></div>' +
            '<div class="dtscroll" id="dtnet"></div></div>' +
            '<div class="dtpane" id="dtpins">' +
            '<div class="ins-top"><button class="dtbtn" id="dtpick">◉ Pick element</button>' +
            '<span id="dtinsst" style="color:#66667f;font-size:11px">click any element on the page</span></div>' +
            '<pre id="dtins"></pre></div>' +
            '</div></div>' +
            '<div id="dthl"></div><div id="dttip"></div>';

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
            return state.wc && e.button === 0 && !edgeCode(e.clientX, e.clientY) &&
                !e.target.closest('button,.tab,.omni,.plus,.nav,#drops,.wctrl,input');
        };
        bar.addEventListener('mousedown', function (e) {
            if (!barDragOk(e)) return;
            e.preventDefault();
            send('wdrag');
        });
        bar.addEventListener('dblclick', function (e) {
            if (!state.wc || !barDragOk(e)) return;
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
            });
        });
        sh.getElementById('dtclear').addEventListener('click', function () {
            if (dtTab === 'con') { send('dtclearc'); dtConSig = ''; }
            else if (dtTab === 'net') { send('dtclearn'); dtNetSig = ''; }
            else { var p = sh.getElementById('dtins'); if (p) p.textContent = ''; }
            setTimeout(function () { send('dtget'); }, 60);
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
        var addr = sh.getElementById('addr');

        omni.addEventListener('click', function (e) {
            // Clicks inside the history dropdown are handled by the rows.
            if (e.target && e.target.closest && e.target.closest('#drops')) return;
            startEditing(sh);
        });
        addr.addEventListener('paste', function (e) {
            e.preventDefault();
            var t = (e.clipboardData || window.clipboardData || {}).getData
                ? e.clipboardData.getData('text/plain') : '';
            document.execCommand('insertText', false, String(t).replace(/\s+/g, ' '));
        });
        addr.addEventListener('input', function () {
            dirty = true;
            dropIdx = -1;
            renderDrops(sh);
        });
        addr.addEventListener('keydown', function (e) {
            e.stopPropagation();
            var items = filteredHist();
            if (e.key === 'Enter') {
                e.preventDefault();
                var it = dropIdx >= 0 && items[dropIdx];
                var v = it ? (typeof it === 'string' ? it : it.u) : addr.textContent.trim();
                if (v) send('navigate:' + v);
                addr.blur();
            } else if (e.key === 'Escape') {
                addr.blur();
            } else if (e.key === 'ArrowDown') {
                e.preventDefault();
                if (items.length) { dropIdx = (dropIdx + 1) % items.length; renderDrops(sh); }
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                if (items.length) { dropIdx = (dropIdx - 1 + items.length) % items.length; renderDrops(sh); }
            }
        });
        addr.addEventListener('blur', function () { finishEditing(sh); });
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
            wireScrollReport();
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
                tip.textContent = el.tagName.toLowerCase() + (el.id ? '#' + el.id : '') +
                    '  ' + Math.round(r.width) + '×' + Math.round(r.height);
                tip.style.left = Math.max(4, Math.min(r.left, window.innerWidth - 160)) + 'px';
                tip.style.top = Math.max(4, r.top - 24) + 'px';
            }, true);
            document.addEventListener('mousedown', function (e) {
                if (!dtPicking) return;
                e.preventDefault(); e.stopPropagation();
                var s = sh0();
                var el = document.elementFromPoint(e.clientX, e.clientY);
                if (s && el && el.id !== HOST_ID) {
                    var ins = s.getElementById('dtins');
                    if (ins) ins.textContent = describeEl(el);
                }
                setPick(s, false);
            }, true);
            // Embedded __brainBoot (internal pages) lands during head parse —
            // retry at DOMContentLoaded in case the 0ms timer beat it.
            document.addEventListener('DOMContentLoaded', applyBoot);
            // Keep the omnibox in sync with in-page (SPA) navigation; tick the
            // start-page clock while the overlay is up.
            setInterval(function () {
                var s = sh0(); if (!s) return;
                updateAddr(s);
                if (state.ov) ntpTick(s);
                if (dtOpen) send('dtget');
            }, 1000);
        }
        wireStripDnd(sh); // strip node is recreated each mount
        lastSig = '';     // force a fresh strip render for the new shadow
        tabEls = {};      // new shadow — the old tab elements are gone
        ntpHistSig = '';  // ...and so are the overlay tiles
        firstRender = true;
        editing = false; dirty = false; dropIdx = -1; // remount kills the old omnibox

        // window.external bridge is only injected on real documents; when the
        // bar mounts inside about:blank (start page) the shim still exists.
        try {
            window.external && window.external.receiveMessage(function (m) {
                try {
                    // Consume any pending boot state BEFORE the push renders —
                    // the push can beat the deferred applyBoot and trigger the
                    // whole-strip cascade (visible as the new-tab flicker).
                    applyBoot();
                    var j = JSON.parse(m);
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
                        state.w = !!j.w;
                        state.sess = j.sess || 0;
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
                        if (editing) renderDrops(sh);
                        try {
                            document.dispatchEvent(new CustomEvent('brain-state', { detail: state }));
                        } catch (e2) { }
                    } else if (j && j.b === 10 && dtOpen) {
                        renderDt(sh, j);
                    } else if (j && j.b === 11 && dtOpen) {
                        dtEvalResult(sh, j);
                    }
                } catch (e) { }
            });
        } catch (e) { }

        // Apply boot synchronously — the strip renders final state in the
        // same task as mount, so an empty strip can never reach a paint.
        applyBoot();
        render(sh);
        syncNtp(sh);
        updateAddr(sh);
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
        else reserveSpace();
    }, 2000);
})();
