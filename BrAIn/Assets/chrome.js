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
        mag: '<svg viewBox="0 0 16 16"><circle cx="7" cy="7" r="4.4" fill="none" stroke="currentColor" stroke-width="1.6"/><path d="m10.4 10.4 3 3" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>'
    };

    function send(cmd) {
        try { window.external && window.external.sendMessage('brain:' + cmd); } catch (e) { }
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

    function render(sh) {
        var strip = sh.getElementById('tabs');
        if (!strip) return;
        // Signature check: state pushes arrive on every load event, and an
        // innerHTML rebuild mid-click eats the click + replays the tab-in
        // animation. Only rebuild when something actually changed.
        var sig = (state.loading ? 'L' : '.');
        for (var i = 0; i < state.tabs.length; i++) {
            var t0 = state.tabs[i];
            sig += t0.id + ',' + (t0.active ? 1 : 0) + ',' + (t0.title || '') + ',' + (t0.icon || '') + ';';
        }
        if (sig === lastSig) { updateNav(sh); updateProg(sh); return; }
        lastSig = sig;
        var html = '';
        for (var i = 0; i < state.tabs.length; i++) {
            var t = state.tabs[i];
            var h = hostOf(t.url);
            var busy = t.active && state.loading;
            var face = busy
                ? '<span class="dot spin"></span>'
                : '<span class="dot" style="background:' + tabHue(t.id) + '">' +
                  esc((t.title || h || '?').trim().charAt(0).toUpperCase()) +
                  (t.icon ? '<img class="fav" src="' + esc(t.icon) + '" alt="" onerror="this.remove()">' : '') +
                  '</span>';
            html += '<div class="tab' + (t.active ? ' active' : '') + '" data-id="' + esc(t.id) + '" title="' + esc(t.title) + '\n' + esc(t.url) + '">' +
                face +
                '<span class="t">' + esc(t.title || h) + '</span>' +
                '<span class="x" data-x="' + esc(t.id) + '" title="Close tab">' + SVG.close + '</span>' +
                '</div>';
        }
        strip.innerHTML = html;

        strip.querySelectorAll('.tab').forEach(function (el) {
            el.draggable = true;
            el.addEventListener('click', function (e) {
                var x = e.target.closest && e.target.closest('.x');
                if (x) { send('closetab:' + x.getAttribute('data-x')); e.stopPropagation(); return; }
                send('switchtab:' + el.getAttribute('data-id'));
            });
            el.addEventListener('auxclick', function (e) {
                if (e.button === 1) { // middle-click closes the tab
                    e.preventDefault();
                    send('closetab:' + el.getAttribute('data-id'));
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
                dragId = null;
            });
        });

        updateNav(sh);
        updateProg(sh);
    }

    // ---------------- tab context menu ----------------

    function showTabMenu(sh, x, y, id) {
        var m = sh.getElementById('tmenu');
        var items = [
            ['reloadtab', 'Reload'],
            ['dupetab', 'Duplicate'],
            ['closeothers', 'Close other tabs'],
            ['closeright', 'Close tabs to the right'],
            ['closetab', 'Close'],
        ];
        var html = '';
        for (var i = 0; i < items.length; i++)
            html += '<div class="mi" data-c="' + items[i][0] + '">' + items[i][1] + '</div>';
        m.innerHTML = html;
        m.style.display = 'block';
        m.style.left = Math.min(x, window.innerWidth - 190) + 'px';
        m.style.top = Math.min(y, window.innerHeight - 200) + 'px';
        m.querySelectorAll('.mi').forEach(function (el) {
            el.addEventListener('mousedown', function (e) {
                e.preventDefault(); e.stopPropagation();
                send(el.getAttribute('data-c') + ':' + id);
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

    function wireStripDnd(sh) {
        var strip = sh.getElementById('tabs');
        strip.addEventListener('dragover', function (e) {
            if (dragId) e.preventDefault();
        });
        strip.addEventListener('drop', function (e) {
            e.preventDefault();
            if (!dragId) return;
            var tabs = Array.prototype.slice.call(strip.querySelectorAll('.tab'));
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
            rel.innerHTML = state.loading ? SVG.stop : SVG.reload;
            rel.setAttribute('data-cmd', state.loading ? 'stop' : 'reload');
            rel.title = state.loading ? 'Stop loading (Esc)' : 'Reload (Ctrl+R)';
        }
    }

    function updateProg(sh) {
        var p = sh.getElementById('prog');
        if (p) p.classList.toggle('on', !!state.loading);
    }

    function updateAddr(sh) {
        var addr = sh.getElementById('addr');
        var lock = sh.getElementById('lock');
        if (!addr || editing) return;
        var url = location.href;
        if (url === 'about:blank') {
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
        return [
            ['newtab', 'New tab', 'Ctrl+T'],
            ['reopen', 'Reopen closed tab', 'Ctrl+Shift+T'],
            null,
            ['find', 'Find in page', 'Ctrl+F'],
            ['bm', (state.bm ? 'Remove bookmark' : 'Bookmark this page'), 'Ctrl+D'],
            null,
            ['zoomout', 'Zoom out', 'Ctrl+−'],
            ['zoomreset', 'Reset zoom — ' + state.zoom + '%', 'Ctrl+0'],
            ['zoomin', 'Zoom in', 'Ctrl++'],
            null,
            ['print', 'Print', 'Ctrl+P'],
            ['viewsource', 'View source', 'Ctrl+U'],
            null,
            ['fullscreen', 'Fullscreen', 'F11'],
            ['topmost', 'Always on top', state.topmost ? '✓' : ''],
        ];
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
        if (c === 'find') { openFind(sh); return; }
        if (c === 'print') { try { window.print(); } catch (e) { } return; }
        if (c === 'viewsource') { send('newtab:view-source:' + location.href); return; }
        send(c);
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
        var http = /^https?:/.test(location.href);
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
            } else if (mod && (k === 't' || k === 'T')) {
                e.preventDefault(); e.stopPropagation();
                send('newtab');
            } else if (mod && (k === 'w' || k === 'W')) {
                e.preventDefault(); e.stopPropagation();
                var a = activeTab(); if (a) send('closetab:' + a.id);
            } else if (mod && (k === 'r' || k === 'R')) {
                e.preventDefault(); e.stopPropagation();
                send('reload');
            } else if (mod && (k === 'd' || k === 'D')) {
                e.preventDefault(); e.stopPropagation();
                send('bm');
            } else if (mod && (k === 'f' || k === 'F')) {
                e.preventDefault(); e.stopPropagation();
                openFind(sh);
            } else if (mod && (k === 'p' || k === 'P')) {
                e.preventDefault(); e.stopPropagation();
                try { window.print(); } catch (err) { }
            } else if (mod && (k === 'u' || k === 'U')) {
                e.preventDefault(); e.stopPropagation();
                send('newtab:view-source:' + location.href);
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
                if (tt) send('switchtab:' + tt.id);
            } else if (e.key === 'F5') {
                e.preventDefault(); e.stopPropagation();
                send('reload');
            } else if (e.key === 'F6') {
                e.preventDefault(); e.stopPropagation();
                startEditing(sh);
            } else if (e.key === 'F11') {
                e.preventDefault(); e.stopPropagation();
                send('fullscreen');
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
                if (findOpen) closeFind(sh);
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
        if (next) send('switchtab:' + next.id);
    }

    // ---------------- mount ----------------

    function mount() {
        if (document.getElementById(HOST_ID)) return;
        if (!document.documentElement) { setTimeout(mount, 120); return; }

        var host = document.createElement('div');
        host.id = HOST_ID;
        host.style.cssText = 'all:initial;position:fixed;top:0;left:0;right:0;height:46px;z-index:2147483647;';
        var sh = host.attachShadow({ mode: 'open' });
        sh.innerHTML =
            '<style>' +
            ':host{all:initial}* {box-sizing:border-box;margin:0;padding:0}' +
            '.bar{position:fixed;top:0;left:0;right:0;height:46px;display:flex;align-items:center;gap:10px;' +
            'padding:0 12px;' +
            'background:linear-gradient(180deg,rgba(23,23,36,.94),rgba(15,15,25,.92));' +
            'backdrop-filter:blur(22px) saturate(1.6);-webkit-backdrop-filter:blur(22px) saturate(1.6);' +
            'border-bottom:1px solid rgba(255,255,255,.07);' +
            'box-shadow:inset 0 1px 0 rgba(255,255,255,.05),0 8px 28px rgba(0,0,0,.4);' +
            'font:12px/1.45 "Segoe UI Variable Text",system-ui,-apple-system,"Segoe UI",sans-serif;color:#c9c9dc;' +
            'user-select:none;-webkit-user-select:none;' +
            'animation:slidein .28s cubic-bezier(.2,.9,.3,1)}' +
            '@keyframes slidein{from{transform:translateY(-10px);opacity:0}to{transform:none;opacity:1}}' +
            '#prog{position:absolute;left:0;right:0;bottom:-1px;height:2px;overflow:hidden;opacity:0;transition:opacity .3s}' +
            '#prog.on{opacity:1}' +
            '#prog i{position:absolute;top:0;bottom:0;width:38%;border-radius:2px;' +
            'background:linear-gradient(90deg,#7c7cff,#4ec9b0);' +
            'box-shadow:0 0 10px rgba(124,124,255,.6);animation:sweep 1.15s ease-in-out infinite}' +
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
            'transition:background .15s,color .15s,border-color .15s,box-shadow .15s;' +
            'animation:tabin .18s cubic-bezier(.2,.9,.3,1)}' +
            '@keyframes tabin{from{transform:scale(.8) translateY(-2px);opacity:0}to{transform:scale(1) translateY(0);opacity:1}}' +
            '.tab:hover{background:rgba(255,255,255,.06);color:#d8d8ec}' +
            '.tab.active{background:linear-gradient(180deg,rgba(124,124,255,.22),rgba(124,124,255,.1));' +
            'color:#ececff;border-color:rgba(140,140,255,.4);' +
            'box-shadow:0 2px 10px rgba(0,0,0,.3),inset 0 1px 0 rgba(255,255,255,.08),0 0 20px rgba(124,124,255,.14)}' +
            '.tab .dot{width:21px;height:21px;border-radius:7px;flex:none;display:grid;place-items:center;position:relative;' +
            'font-size:11px;font-weight:700;color:#fff;overflow:hidden;box-shadow:inset 0 1px 0 rgba(255,255,255,.15)}' +
            '.tab .dot .fav{position:absolute;inset:2.5px;width:16px;height:16px;border-radius:5px}' +
            '.tab .dot.spin{background:transparent;box-shadow:none}' +
            '.tab .dot.spin::before{content:"";width:13px;height:13px;border-radius:50%;' +
            'border:2px solid rgba(124,124,255,.2);border-top-color:#9c9cff;animation:rot .65s linear infinite}' +
            '@keyframes rot{to{transform:rotate(360deg)}}' +
            '.tab .t{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:1;min-width:0;font-weight:500}' +
            '.tab .x{width:18px;height:18px;border-radius:999px;display:none;place-items:center;color:#8a8aa4;flex:none;transition:background .12s,color .12s}' +
            '.tab .x svg{width:10px;height:10px}' +
            '.tab:hover .x{display:grid}' +
            '.tab .x:hover{background:rgba(255,255,255,.16);color:#fff}' +
            '.tab.dragging{opacity:.4}' +
            '#tmenu{position:fixed;min-width:190px;display:none;z-index:30;' +
            'background:rgba(22,22,34,.96);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(255,255,255,.1);border-radius:13px;padding:5px;' +
            'box-shadow:0 12px 36px rgba(0,0,0,.55),inset 0 1px 0 rgba(255,255,255,.06);font:12px/1.4 system-ui,sans-serif}' +
            '.mi{padding:7px 12px;border-radius:8px;color:#c9c9dc;cursor:pointer;transition:background .1s,color .1s}' +
            '.mi:hover{background:rgba(124,124,255,.18);color:#fff}' +
            '.plus{width:30px;height:34px;flex:none;display:grid;place-items:center;border:0;border-radius:10px;' +
            'background:transparent;color:#8a8aa4;cursor:pointer;transition:background .14s,color .14s,transform .1s}' +
            '.plus:hover{background:rgba(124,124,255,.18);color:#c9c9ff}' +
            '.plus:active{transform:scale(.9)}' +
            '.plus svg{width:14px;height:14px}' +
            '.omni{flex:1;min-width:120px;display:flex;align-items:center;gap:8px;height:34px;padding:0 14px;' +
            'border-radius:999px;background:rgba(255,255,255,.055);border:1px solid rgba(255,255,255,.08);' +
            'box-shadow:inset 0 1px 2px rgba(0,0,0,.25);cursor:text;position:relative;' +
            'transition:background .15s,border-color .15s,box-shadow .2s}' +
            '.omni:hover{background:rgba(255,255,255,.08)}' +
            '.omni:focus-within{background:rgba(255,255,255,.1);border-color:rgba(124,124,255,.55);' +
            'box-shadow:0 0 0 4px rgba(124,124,255,.14),inset 0 1px 2px rgba(0,0,0,.2)}' +
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
            'box-shadow:0 12px 36px rgba(0,0,0,.55),inset 0 1px 0 rgba(255,255,255,.05);padding:5px}' +
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
            'box-shadow:0 6px 20px rgba(0,0,0,.45);transition:opacity .15s}' +
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
            'box-shadow:0 12px 36px rgba(0,0,0,.55),inset 0 1px 0 rgba(255,255,255,.06);font:12px/1.4 system-ui,sans-serif}' +
            '#menu.on{display:block}' +
            '#menu .mi{display:flex;justify-content:space-between;align-items:center;gap:22px}' +
            '#menu .sc{color:#63637e;font-size:11px;flex:none}' +
            '.msep{height:1px;margin:5px 10px;background:rgba(255,255,255,.07)}' +
            '#fbar{position:fixed;top:52px;right:12px;display:none;align-items:center;gap:7px;z-index:29;' +
            'padding:6px 8px 6px 12px;border-radius:12px;' +
            'background:rgba(22,22,34,.97);backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);' +
            'border:1px solid rgba(255,255,255,.1);' +
            'box-shadow:0 12px 36px rgba(0,0,0,.55),inset 0 1px 0 rgba(255,255,255,.06);' +
            'color:#a5a5c2;font:12px/1.4 system-ui,sans-serif}' +
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
            'font-weight:800;font-size:12.5px;letter-spacing:.5px;' +
            'background:linear-gradient(115deg,#9c9cff 20%,#4ec9b0 90%);' +
            '-webkit-background-clip:text;background-clip:text;color:transparent}' +
            '.brand svg{width:14px;height:14px;color:#9c9cff}' +
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
            '<div id="prog"><i></i></div>' +
            '</div>' +
            '<div id="status"></div>' +
            '<div id="tmenu"></div>' +
            '<div id="menu"></div>' +
            '<div id="fbar">' + SVG.mag +
            '<input id="fin" spellcheck="false" placeholder="Find in page">' +
            '<span id="fcnt"></span>' +
            '<button class="fb" data-f="-1" title="Previous (Shift+Enter)">' + SVG.up + '</button>' +
            '<button class="fb" data-f="1" title="Next (Enter)">' + SVG.dn + '</button>' +
            '<button class="fb" id="fclose" title="Close (Esc)">' + SVG.close + '</button></div>';

        sh.querySelector('.nav').addEventListener('click', function (e) {
            var b = e.target.closest('button');
            if (b && b.getAttribute('data-cmd')) send(b.getAttribute('data-cmd'));
        });
        sh.getElementById('newtab').addEventListener('click', function () { send('newtab'); });
        sh.getElementById('mbtn').addEventListener('click', function (e) {
            e.stopPropagation();
            showMenu(sh);
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
            wireScrollReport();
            // Keep the omnibox in sync with in-page (SPA) navigation.
            setInterval(function () { var s = sh0(); if (s) updateAddr(s); }, 1000);
        }
        wireStripDnd(sh); // strip node is recreated each mount
        lastSig = '';     // force a fresh strip render for the new shadow
        editing = false; dirty = false; dropIdx = -1; // remount kills the old omnibox

        // window.external bridge is only injected on real documents; when the
        // bar mounts inside about:blank (start page) the shim still exists.
        try {
            window.external && window.external.receiveMessage(function (m) {
                try {
                    var j = JSON.parse(m);
                    if (j && j.b === 9 && Array.isArray(j.tabs)) {
                        state.tabs = j.tabs;
                        state.loading = !!j.loading;
                        state.canBack = j.canBack !== false;
                        state.canFwd = !!j.canFwd;
                        state.hist = Array.isArray(j.hist) ? j.hist : [];
                        state.bmarks = Array.isArray(j.bmarks) ? j.bmarks : [];
                        state.bm = !!j.bm;
                        state.zoom = j.zoom || 100;
                        state.topmost = !!j.topmost;
                        window.__brainState = state;
                        render(sh);
                        updateStar(sh);
                        if (editing) renderDrops(sh);
                        try {
                            document.dispatchEvent(new CustomEvent('brain-state', { detail: state }));
                        } catch (e2) { }
                    }
                } catch (e) { }
            });
        } catch (e) { }

        render(sh);
        updateAddr(sh);
        send('tabs');
    }

    window.__brainChrome = {
        mount: mount,
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
