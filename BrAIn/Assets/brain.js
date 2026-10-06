/* BrAIn in-page automation library.
 * Injected into every document (via CDP addScriptToEvaluateOnNewDocument on
 * Windows, or on-demand eval on macOS). Exposes window.__brain.
 * Element refs are data-brain-ref attributes assigned during snapshots. */
(function () {
    if (window.__brain && window.__brain.v === 1) return;

    var REF = 'data-brain-ref';
    var refCounter = 0;

    // ---------------- element refs ----------------

    function assignRef(el) {
        var r = el.getAttribute(REF);
        if (!r) { r = 'e' + (++refCounter); el.setAttribute(REF, r); }
        return r;
    }

    // ---------------- visibility / role / name ----------------

    function isVisible(el) {
        if (!(el instanceof Element)) return false;
        var s = getComputedStyle(el);
        if (s.display === 'none' || s.visibility === 'hidden' || s.visibility === 'collapse') return false;
        if (el.hasAttribute('hidden')) return false;
        if (el.getAttribute('aria-hidden') === 'true') return false;
        var r = el.getBoundingClientRect();
        if (r.width === 0 && r.height === 0 && !el.querySelector('*')) return false;
        return true;
    }

    var ROLE_BY_TAG = {
        a: 'link', button: 'button', select: 'combobox', textarea: 'textbox',
        summary: 'button', nav: 'navigation', main: 'main', header: 'banner',
        footer: 'contentinfo', aside: 'complementary', form: 'form',
        table: 'table', ul: 'list', ol: 'list', li: 'listitem',
        dialog: 'dialog', img: 'img', video: 'video', audio: 'audio',
        iframe: 'iframe', details: 'group', fieldset: 'group', legend: null,
        article: 'article', section: 'region', meter: 'meter', progress: 'progressbar',
        h1: 'heading', h2: 'heading', h3: 'heading', h4: 'heading', h5: 'heading', h6: 'heading',
    };

    function roleOf(el) {
        var explicit = el.getAttribute('role');
        if (explicit) return explicit.split(' ')[0];
        var tag = el.tagName.toLowerCase();
        if (tag === 'input') {
            var t = (el.type || 'text').toLowerCase();
            return {
                button: 'button', submit: 'button', reset: 'button', image: 'button',
                checkbox: 'checkbox', radio: 'radio', range: 'slider',
                search: 'searchbox', email: 'textbox', url: 'textbox', tel: 'textbox',
                password: 'textbox', text: 'textbox', number: 'spinbutton',
                file: 'button', color: 'button', date: 'textbox', 'datetime-local': 'textbox',
            }[t] || 'textbox';
        }
        if (tag === 'a' && !el.hasAttribute('href')) return null;
        return ROLE_BY_TAG[tag] !== undefined ? ROLE_BY_TAG[tag] : (tag.match(/^h[1-6]$/) ? 'heading' : null);
    }

    function accName(el) {
        var al = el.getAttribute('aria-label');
        if (al) return al.trim();
        var lb = el.getAttribute('aria-labelledby');
        if (lb) {
            var parts = lb.trim().split(/\s+/).map(function (id) {
                var l = document.getElementById(id);
                return l ? l.innerText.trim() : '';
            }).filter(Boolean);
            if (parts.length) return parts.join(' ');
        }
        var tag = el.tagName.toLowerCase();
        if (tag === 'img') return (el.alt || '').trim();
        if (tag === 'input' || tag === 'textarea' || tag === 'select') {
            if (el.labels && el.labels.length) {
                var lt = el.labels[0].innerText.trim();
                if (lt) return lt;
            }
            if (el.placeholder) return el.placeholder;
            var t = (el.type || '').toLowerCase();
            if (t === 'submit' || t === 'button' || t === 'reset') return el.value || t;
            if (el.name) return el.name;
        }
        if (tag === 'button' || tag === 'a' || tag === 'summary') {
            var txt = (el.innerText || '').replace(/\s+/g, ' ').trim();
            if (txt) return txt.slice(0, 80);
            var img = el.querySelector('img[alt]');
            if (img) return img.alt;
        }
        var title = el.getAttribute('title');
        if (title) return title.trim();
        var txt2 = (el.innerText || '').replace(/\s+/g, ' ').trim();
        return txt2.slice(0, 80);
    }

    var INTERACTIVE_ROLES = {
        button: 1, link: 1, checkbox: 1, radio: 1, textbox: 1, searchbox: 1,
        combobox: 1, listbox: 1, slider: 1, spinbutton: 1, switch: 1, tab: 1,
        menuitem: 1, option: 1, treeitem: 1, 'menuitemcheckbox': 1, 'menuitemradio': 1,
    };
    var SEMANTIC_ROLES = {
        heading: 1, img: 1, navigation: 1, main: 1, banner: 1, contentinfo: 1,
        complementary: 1, form: 1, table: 1, list: 1, listitem: 1, dialog: 1,
        article: 1, region: 1, alert: 1, status: 1, progressbar: 1, meter: 1,
        video: 1, audio: 1, iframe: 1, cell: 1, rowheader: 1, columnheader: 1,
    };

    function isInteractive(el, role) {
        if (role && INTERACTIVE_ROLES[role]) return true;
        var tag = el.tagName.toLowerCase();
        if (tag === 'a' && el.hasAttribute('href')) return true;
        if (/^(button|input|select|textarea|summary|option)$/.test(tag)) return true;
        if (el.onclick || el.hasAttribute('onclick')) return true;
        if (el.hasAttribute('tabindex') && el.getAttribute('tabindex') !== '-1') return true;
        if (el.isContentEditable) return true;
        if (el.getAttribute('draggable') === 'true') return true;
        return false;
    }

    // ---------------- snapshot ----------------

    var MAX_SNAPSHOT_NODES = 500;

    function snapshot(opts) {
        opts = opts || {};
        var lines = [];
        var count = 0;

        function walk(node, depth) {
            if (count >= MAX_SNAPSHOT_NODES) return;
            if (!(node instanceof Element)) return;
            if (node.tagName === 'SCRIPT' || node.tagName === 'STYLE' || node.tagName === 'NOSCRIPT' ||
                node.tagName === 'META' || node.tagName === 'LINK' || node.tagName === 'TEMPLATE' ||
                node.id === '__brain-toolbar-root') return;
            if (!isVisible(node)) return;

            var role = roleOf(node);
            var interactive = isInteractive(node, role);
            var semantic = role && SEMANTIC_ROLES[role];
            var interesting = interactive || semantic;

            if (interesting) {
                count++;
                var name = accName(node);
                var line = '- ';
                if (interactive) line += '[ref=' + assignRef(node) + '] ';
                line += role || 'element';
                if (name) line += ' "' + name.replace(/"/g, "'") + '"';
                if (node.disabled || node.getAttribute('aria-disabled') === 'true') line += ' (disabled)';
                if (role === 'checkbox' || role === 'radio')
                    line += ' ' + (node.checked || node.getAttribute('aria-checked') === 'true' ? '[checked]' : '[unchecked]');
                if (role === 'combobox' && node.value) line += ' value="' + String(node.value).slice(0, 60) + '"';
                if ((tag(node) === 'input' || tag(node) === 'textarea') && node.value)
                    line += ' value="' + String(node.value).slice(0, 60) + '"';
                if (node.getAttribute('aria-expanded') === 'true') line += ' [expanded]';
                if (node.getAttribute('aria-expanded') === 'false') line += ' [collapsed]';
                if (role === 'heading') line += ' [level=' + (tag(node).slice(1) || '?') + ']';
                if (tag(node) === 'a' && node.href && opts.links !== false)
                    line += ' → ' + node.href.slice(0, 120);
                lines.push(new Array(depth + 1).join('  ') + line);
            }

            var nextDepth = interesting ? depth + 1 : depth;
            var children = node.children;
            for (var i = 0; i < children.length; i++)
                walk(children[i], nextDepth);

            // Shadow DOM
            if (node.shadowRoot && opts.shadow !== false) {
                var sc = node.shadowRoot.children;
                for (var j = 0; j < sc.length; j++) walk(sc[j], nextDepth);
            }
        }

        walk(document.documentElement, 0);
        return {
            url: location.href,
            title: document.title,
            nodeCount: count,
            truncated: count >= MAX_SNAPSHOT_NODES,
            tree: lines.join('\n'),
        };
    }

    function tag(el) { return el.tagName.toLowerCase(); }

    // ---------------- selector resolution ----------------

    function resolve(sel) {
        if (!sel) return null;
        sel = String(sel).trim();

        // ref: e5 | ref=e5 | @e5
        var m = sel.match(/^(?:@|ref[:=])?e(\d+)$/i);
        if (m) return document.querySelector('[' + REF + '="e' + m[1] + '"]');
        var mr = sel.match(/^ref[:=](.+)$/i);
        if (mr) return document.querySelector('[' + REF + '="' + mr[1] + '"]');

        if (sel.startsWith('css:')) return document.querySelector(sel.slice(4));

        if (sel.startsWith('xpath:')) {
            var xp = document.evaluate(sel.slice(6), document, null,
                XPathResult.FIRST_ORDERED_NODE_TYPE, null);
            return xp.singleNodeValue;
        }

        if (sel.startsWith('text:')) {
            return findByText(sel.slice(5));
        }

        if (sel.startsWith('role:')) {
            var parts = sel.slice(5).split(/:(.+)/);
            return findByRole(parts[0], parts[1]);
        }

        // bare ref like "e5", else CSS
        if (/^e\d+$/.test(sel)) return document.querySelector('[' + REF + '="' + sel + '"]');
        try { return document.querySelector(sel); } catch (e) { return null; }
    }

    function findByText(text) {
        var want = text.toLowerCase();
        var best = null;
        var all = document.querySelectorAll('a,button,input,select,textarea,[role],[onclick],label,option,h1,h2,h3,h4,h5,h6,summary,li,td,th,span,p');
        for (var i = 0; i < all.length; i++) {
            var el = all[i];
            if (!isVisible(el)) continue;
            var name = (accName(el) || '').toLowerCase();
            if (name === want) return el;
            if (!best && name.indexOf(want) >= 0) best = el;
            var it = (el.innerText || '').trim().toLowerCase();
            if (it === want) return el;
            if (!best && it.indexOf(want) >= 0 && it.length < 200) best = el;
        }
        return best;
    }

    function findByRole(role, name) {
        var all = document.querySelectorAll('*');
        for (var i = 0; i < all.length; i++) {
            var el = all[i];
            if (roleOf(el) !== role || !isVisible(el)) continue;
            if (!name) return el;
            var n = (accName(el) || '').toLowerCase();
            if (n.indexOf(name.toLowerCase()) >= 0) return el;
        }
        return null;
    }

    function queryAll(sel) {
        var out = [];
        var els;
        if (sel.startsWith('ref=') || /^e\d+$/.test(sel)) {
            var one = resolve(sel);
            els = one ? [one] : [];
        } else {
            try { els = document.querySelectorAll(sel.replace(/^css:/, '')); }
            catch (e) { return []; }
        }
        for (var i = 0; i < els.length && out.length < 50; i++) {
            var el = els[i];
            if (!(el instanceof Element) || !isVisible(el)) continue;
            var r = el.getBoundingClientRect();
            out.push({
                ref: assignRef(el),
                tag: tag(el),
                role: roleOf(el),
                name: accName(el),
                text: (el.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 100),
                rect: { x: r.x, y: r.y, w: r.width, h: r.height },
            });
        }
        return out;
    }

    // ---------------- events ----------------

    function fireMouse(el, type, x, y, btn) {
        var r = el.getBoundingClientRect();
        x = x !== undefined ? x : r.left + r.width / 2;
        y = y !== undefined ? y : r.top + r.height / 2;
        var e = new MouseEvent(type, {
            bubbles: true, cancelable: true, view: window,
            clientX: x, clientY: y, button: btn || 0, buttons: type === 'mouseup' ? 0 : 1,
        });
        el.dispatchEvent(e);
    }

    function clickAt(x, y, button, count) {
        var btnNum = button === 'right' ? 2 : button === 'middle' ? 1 : 0;
        var el = document.elementFromPoint(x, y) || document.body;
        var n = count || 1;
        fireMouse(el, 'mouseover', x, y, btnNum);
        fireMouse(el, 'mousemove', x, y, btnNum);
        for (var i = 0; i < n; i++) {
            fireMouse(el, 'mousedown', x, y, btnNum);
            fireMouse(el, 'mouseup', x, y, btnNum);
            fireMouse(el, 'click', x, y, btnNum);
            if (n === 2) fireMouse(el, 'dblclick', x, y, btnNum);
        }
        if (button !== 'right') try { el.focus && el.focus(); } catch (e) { }
        return describe(el);
    }

    function click(sel) {
        var el = resolve(sel);
        if (!el) return { ok: false, error: 'element not found: ' + sel };
        el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
        var r = el.getBoundingClientRect();
        return clickAt(r.left + r.width / 2, r.top + r.height / 2, 'left', 1);
    }

    function dblclick(sel) {
        var el = resolve(sel);
        if (!el) return { ok: false, error: 'element not found: ' + sel };
        el.scrollIntoView({ block: 'center', behavior: 'instant' });
        var r = el.getBoundingClientRect();
        return clickAt(r.left + r.width / 2, r.top + r.height / 2, 'left', 2);
    }

    function hover(selOrX, y) {
        var el = (typeof selOrX === 'number') ? document.elementFromPoint(selOrX, y) : resolve(selOrX);
        if (!el) return { ok: false, error: 'element not found' };
        el.scrollIntoView({ block: 'center', behavior: 'instant' });
        var r = el.getBoundingClientRect();
        fireMouse(el, 'mouseover', r.left + r.width / 2, r.top + r.height / 2, 0);
        fireMouse(el, 'mousemove', r.left + r.width / 2, r.top + r.height / 2, 0);
        return { ok: true };
    }

    // Native value setters so React/Vue see a real change
    function setNativeValue(el, value) {
        var proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype
            : el instanceof HTMLSelectElement ? HTMLSelectElement.prototype
            : HTMLInputElement.prototype;
        var setter = Object.getOwnPropertyDescriptor(proto, 'value');
        if (setter && setter.set) setter.set.call(el, value);
        else el.value = value;
    }

    function fill(sel, value) {
        var el = resolve(sel);
        if (!el) return { ok: false, error: 'element not found: ' + sel };
        el.scrollIntoView({ block: 'center', behavior: 'instant' });
        el.focus();
        var tg = tag(el);
        if (tg === 'select') return selectOptionRaw(el, [].concat(value));
        if (el.isContentEditable) {
            el.innerText = value;
        } else {
            setNativeValue(el, value);
        }
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return { ok: true, ref: assignRef(el) };
    }

    function insertText(text) {
        var el = document.activeElement;
        if (!el || el === document.body) return { ok: false, error: 'no focused element' };
        var ok = false;
        try { ok = document.execCommand('insertText', false, text); } catch (e) { }
        if (!ok) {
            if (el.isContentEditable) {
                el.innerText += text;
            } else if ('value' in el) {
                var s = el.selectionStart || 0, e2 = el.selectionEnd || 0;
                setNativeValue(el, el.value.slice(0, s) + text + el.value.slice(e2));
                el.selectionStart = el.selectionEnd = s + text.length;
            } else return { ok: false, error: 'element is not editable' };
            el.dispatchEvent(new Event('input', { bubbles: true }));
        }
        return { ok: true };
    }

    var KEYMAP = {
        Enter: { code: 'Enter', keyCode: 13 },
        Tab: { code: 'Tab', keyCode: 9 },
        Escape: { code: 'Escape', keyCode: 27 },
        Backspace: { code: 'Backspace', keyCode: 8 },
        Delete: { code: 'Delete', keyCode: 46 },
        ArrowUp: { code: 'ArrowUp', keyCode: 38 },
        ArrowDown: { code: 'ArrowDown', keyCode: 40 },
        ArrowLeft: { code: 'ArrowLeft', keyCode: 37 },
        ArrowRight: { code: 'ArrowRight', keyCode: 39 },
        Home: { code: 'Home', keyCode: 36 },
        End: { code: 'End', keyCode: 35 },
        PageUp: { code: 'PageUp', keyCode: 33 },
        PageDown: { code: 'PageDown', keyCode: 34 },
        ' ': { code: 'Space', keyCode: 32 },
    };

    function pressKey(key, modMask) {
        var el = document.activeElement || document.body;
        var info = KEYMAP[key] || { code: 'Key' + key.toUpperCase(), keyCode: key.toUpperCase().charCodeAt(0) };
        var mods = modMask || 0;
        var opts = {
            key: key, code: info.code, keyCode: info.keyCode, which: info.keyCode,
            bubbles: true, cancelable: true,
            altKey: !!(mods & 1), ctrlKey: !!(mods & 2), metaKey: !!(mods & 4), shiftKey: !!(mods & 8),
        };
        el.dispatchEvent(new KeyboardEvent('keydown', opts));
        el.dispatchEvent(new KeyboardEvent('keypress', opts));
        el.dispatchEvent(new KeyboardEvent('keyup', opts));
        // Synthetic events don't trigger browser default actions; emulate the
        // important ones for automation.
        if (key === 'Enter') {
            var form = el.closest && el.closest('form');
            if (form && form.requestSubmit) form.requestSubmit();
            else if (tag(el) === 'a' && el.href) el.click();
        }
        if (key === 'Tab') moveFocus(mods & 8 ? -1 : 1);
        return { ok: true };
    }

    function moveFocus(dir) {
        var focusables = document.querySelectorAll('a[href],button,input,select,textarea,[tabindex]:not([tabindex="-1"]),[contenteditable]');
        var list = [].filter.call(focusables, isVisible);
        var i = list.indexOf(document.activeElement);
        var next = list[i + dir] || (dir > 0 ? list[0] : list[list.length - 1]);
        if (next) next.focus();
    }

    function setChecked(sel, checked) {
        var el = resolve(sel);
        if (!el) return { ok: false, error: 'element not found: ' + sel };
        if (el.checked !== checked) {
            el.checked = checked;
            el.dispatchEvent(new Event('click', { bubbles: true }));
            el.dispatchEvent(new Event('input', { bubbles: true }));
            el.dispatchEvent(new Event('change', { bubbles: true }));
        }
        return { ok: true, checked: el.checked };
    }

    function selectOptionRaw(el, values) {
        var want = values.map(String);
        var matched = [];
        for (var i = 0; i < el.options.length; i++) {
            var o = el.options[i];
            var hit = want.indexOf(o.value) >= 0 || want.indexOf(o.text) >= 0 || want.indexOf(o.label) >= 0;
            o.selected = hit;
            if (hit) matched.push(o.value);
        }
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return { ok: matched.length > 0, selected: matched };
    }

    function selectOption(sel, values) {
        var el = resolve(sel);
        if (!el) return { ok: false, error: 'element not found: ' + sel };
        if (tag(el) !== 'select') return { ok: false, error: 'not a <select>' };
        el.focus();
        return selectOptionRaw(el, [].concat(values));
    }

    // ---------------- scrolling ----------------

    function wheel(dx, dy) {
        window.scrollBy({ left: dx || 0, top: dy || 0, behavior: 'instant' });
        var el = document.elementFromPoint(innerWidth / 2, innerHeight / 2);
        if (el) el.dispatchEvent(new WheelEvent('wheel', { deltaY: dy || 0, deltaX: dx || 0, bubbles: true }));
        return { ok: true, scrollY: scrollY, scrollX: scrollX };
    }

    function scrollTo(sel) {
        var el = resolve(sel);
        if (!el) return { ok: false, error: 'element not found: ' + sel };
        el.scrollIntoView({ block: 'center', behavior: 'instant' });
        var r = el.getBoundingClientRect();
        return { ok: true, rect: { x: r.x, y: r.y, w: r.width, h: r.height } };
    }

    function scrollPage(direction, amount) {
        amount = amount || 600;
        var map = {
            up: [0, -amount], down: [0, amount], left: [-amount, 0], right: [amount, 0],
            top: [0, -document.body.scrollHeight], bottom: [0, document.body.scrollHeight],
        };
        var d = map[direction] || map.down;
        window.scrollBy({ left: d[0], top: d[1], behavior: 'instant' });
        return { ok: true, scrollY: scrollY, scrollX: scrollX, maxY: document.body.scrollHeight - innerHeight };
    }

    // ---------------- extraction ----------------

    function text(sel) {
        if (!sel) return (document.body ? document.body.innerText : '') || '';
        var el = resolve(sel);
        return el ? (el.innerText || el.value || '') : null;
    }

    function html(sel) {
        if (!sel) return document.documentElement.outerHTML;
        var el = resolve(sel);
        return el ? el.outerHTML : null;
    }

    function describe(el) {
        if (!el) return null;
        var r = el.getBoundingClientRect();
        return {
            ref: assignRef(el), tag: tag(el), role: roleOf(el), name: accName(el),
            text: (el.innerText || el.value || '').toString().slice(0, 120),
            rect: { x: r.x, y: r.y, w: r.width, h: r.height },
            visible: isVisible(el),
        };
    }

    function describeSel(sel) { return describe(resolve(sel)); }

    function rect(sel) {
        var el = resolve(sel);
        if (!el) return null;
        el.scrollIntoView({ block: 'center', behavior: 'instant' });
        var r = el.getBoundingClientRect();
        return { x: r.x, y: r.y, w: r.width, h: r.height, dpr: devicePixelRatio };
    }

    // ---------------- cookies ----------------

    function getCookies() {
        return document.cookie.split(';').filter(function (s) { return s.trim(); })
            .map(function (s) {
                var i = s.indexOf('=');
                return { name: s.slice(0, i).trim(), value: decodeURIComponent(s.slice(i + 1).trim()) };
            });
    }

    function setCookie(name, value, path, domain) {
        var c = encodeURIComponent(name) + '=' + encodeURIComponent(value) +
            '; path=' + (path || '/');
        if (domain) c += '; domain=' + domain;
        document.cookie = c;
        return document.cookie.indexOf(encodeURIComponent(name) + '=') >= 0;
    }

    function deleteCookie(name) {
        if (!name) return false;
        document.cookie = encodeURIComponent(name) + '=; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT';
        return true;
    }

    // ---------------- wait primitives (polled by host) ----------------

    function exists(sel) { return !!resolve(sel); }
    function textIncludes(t) {
        var want = String(t).toLowerCase();
        return (document.body && document.body.innerText || '').toLowerCase().indexOf(want) >= 0;
    }
    function urlMatches(p) {
        try { return new RegExp(p).test(location.href); } catch (e) { return location.href.indexOf(p) >= 0; }
    }

    // ---------------- console hook (macOS log capture) ----------------

    function installConsoleHook() {
        if (window.__brainConsoleHooked) return;
        window.__brainConsoleHooked = true;
        ['log', 'info', 'warn', 'error', 'debug'].forEach(function (level) {
            var orig = console[level];
            console[level] = function () {
                try {
                    var text = [].map.call(arguments, function (a) {
                        try { return typeof a === 'object' ? JSON.stringify(a) : String(a); }
                        catch (e) { return String(a); }
                    }).join(' ');
                    window.external && window.external.sendMessage(JSON.stringify({ b: 2, level: level, text: text.slice(0, 2000) }));
                } catch (e) { }
                return orig && orig.apply(console, arguments);
            };
        });
        window.addEventListener('error', function (e) {
            try {
                window.external && window.external.sendMessage(JSON.stringify({
                    b: 2, level: 'error', text: String(e.message || e.error || 'page error').slice(0, 2000),
                }));
            } catch (e2) { }
        });
    }

    // ---------------- network summary (performance entries) ----------------

    var _netSeen = 0;
    function networkLog(sinceIndex) {
        var entries = performance.getEntriesByType('resource');
        var start = sinceIndex !== undefined ? sinceIndex : _netSeen;
        _netSeen = entries.length;
        return entries.slice(start).map(function (e) {
            return {
                name: e.name.slice(0, 300), type: e.initiatorType,
                duration: Math.round(e.duration), size: e.transferSize || 0,
                start: Math.round(e.startTime),
            };
        });
    }

    // ---------------- misc ----------------

    function info() {
        return {
            url: location.href, title: document.title, readyState: document.readyState,
            viewport: { w: innerWidth, h: innerHeight, dpr: devicePixelRatio },
            scroll: { x: scrollX, y: scrollY, maxY: document.body ? document.body.scrollHeight - innerHeight : 0 },
        };
    }

    function blurAll() { if (document.activeElement) document.activeElement.blur(); }

    window.__brain = {
        v: 1,
        snapshot: snapshot, resolve: resolve, queryAll: queryAll,
        click: click, clickAt: clickAt, dblclick: dblclick, hover: hover,
        fill: fill, insertText: insertText, pressKey: pressKey,
        setChecked: setChecked, selectOption: selectOption,
        wheel: wheel, scrollTo: scrollTo, scrollPage: scrollPage,
        text: text, html: html, describe: describeSel, rect: rect,
        getCookies: getCookies, setCookie: setCookie, deleteCookie: deleteCookie,
        exists: exists, textIncludes: textIncludes, urlMatches: urlMatches,
        installConsoleHook: installConsoleHook, networkLog: networkLog,
        info: info, blurAll: blurAll,
        // toolbar() is attached by toolbar.js
    };

    try { installConsoleHook(); } catch (e) { }
})();
