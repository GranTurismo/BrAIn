/* BrAIn in-page automation library.
 * Injected into every document (via CDP addScriptToEvaluateOnNewDocument on
 * Windows, or on-demand eval on macOS). Exposes window.__brain.
 * Element refs are data-brain-ref attributes assigned during snapshots. */
(function () {
    if (window.__brain && window.__brain.v === 5) return;

    var REF = 'data-brain-ref';
    // Continue after the highest ref already on the page, so re-injecting this script never hands out a ref twice.
    var refCounter = (function () {
        var max = 0;
        try {
            var held = document.querySelectorAll('[data-brain-ref]');
            for (var i = 0; i < held.length; i++) {
                var n = parseInt(String(held[i].getAttribute('data-brain-ref')).slice(1), 10);
                if (n > max) max = n;
            }
        } catch (e) { }
        return max;
    })();

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
        // Clickable divs/spans with no role or tabindex (the React default): the pointer cursor gives them away.
        if (/^(div|span|li|img|svg|p|td|i)$/.test(tag)) {
            try {
                if (getComputedStyle(el).cursor === 'pointer') {
                    var par = el.parentElement;
                    if (!par || getComputedStyle(par).cursor !== 'pointer') return true;
                }
            } catch (e) { }
        }
        return false;
    }

    // ---------------- snapshot ----------------

    var MAX_SNAPSHOT_NODES = 500;

    function snapshot(opts) {
        opts = opts || {};
        var limit = Math.max(1, Math.min(parseInt(opts.maxNodes, 10) || MAX_SNAPSHOT_NODES, 3000));
        var lines = [];
        var count = 0;
        var seen = new Set();

        function walk(node, depth) {
            if (count >= limit) return;
            if (!(node instanceof Element) || seen.has(node)) return;
            if (node.tagName === 'SCRIPT' || node.tagName === 'STYLE' || node.tagName === 'NOSCRIPT' ||
                node.tagName === 'META' || node.tagName === 'LINK' || node.tagName === 'TEMPLATE' ||
                node.id === '__brain-toolbar-root' || node.id === '__brain-chrome-host') return;
            if (!isVisible(node)) return;
            seen.add(node);

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

        // Open dialogs/modals first: sites append them at the very end of <body>, where a size limit would cut them off.
        try {
            var dlgs = document.querySelectorAll('[role=dialog],[role=alertdialog],dialog[open],[aria-modal=true]');
            for (var d = 0; d < dlgs.length; d++) walk(dlgs[d], 0);
        } catch (e) { }
        walk(document.documentElement, 0);
        return {
            url: location.href,
            title: document.title,
            nodeCount: count,
            truncated: count >= limit,
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
        if (/^(ref[=:]|text:|role:|xpath:)/.test(sel) || /^e\d+$/.test(sel)) {
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

    function fireMouse(el, type, x, y, btn, detail) {
        var r = el.getBoundingClientRect();
        x = x !== undefined ? x : r.left + r.width / 2;
        y = y !== undefined ? y : r.top + r.height / 2;
        var e = new MouseEvent(type, {
            bubbles: true, cancelable: true, view: window, detail: detail || 0,
            clientX: x, clientY: y, button: btn || 0, buttons: /^(mouseup|click|dblclick|auxclick|contextmenu|mouseover|mousemove|mouseenter)$/.test(type) ? 0 : (btn === 2 ? 2 : btn === 1 ? 4 : 1),
        });
        el.dispatchEvent(e);
    }

    // Pointer events too: menus and widgets built on them (Radix, Material...) ignore mouse-only input.
    function firePointer(el, type, x, y, btn) {
        if (typeof PointerEvent !== 'function') return;
        el.dispatchEvent(new PointerEvent(type, {
            bubbles: true, cancelable: true, view: window, pointerType: 'mouse', isPrimary: true, pointerId: 1,
            clientX: x, clientY: y, button: btn || 0, buttons: type === 'pointerup' ? 0 : 1,
        }));
    }

    function clickAt(x, y, button, count, target) {
        var btnNum = button === 'right' ? 2 : button === 'middle' ? 1 : 0;
        var el = target || document.elementFromPoint(x, y) || document.body;
        var n = count || 1;
        firePointer(el, 'pointerover', x, y, btnNum);
        fireMouse(el, 'mouseover', x, y, btnNum);
        fireMouse(el, 'mousemove', x, y, btnNum);
        for (var i = 0; i < n; i++) {
            firePointer(el, 'pointerdown', x, y, btnNum);
            fireMouse(el, 'mousedown', x, y, btnNum, i + 1);
            if (button !== 'right') try { el.focus && el.focus(); } catch (e) { }
            firePointer(el, 'pointerup', x, y, btnNum);
            fireMouse(el, 'mouseup', x, y, btnNum, i + 1);
            if (btnNum === 2) fireMouse(el, 'contextmenu', x, y, btnNum, i + 1);
            else if (btnNum === 1) fireMouse(el, 'auxclick', x, y, btnNum, i + 1);
            else fireMouse(el, 'click', x, y, btnNum, i + 1);
        }
        if (n === 2 && btnNum === 0) fireMouse(el, 'dblclick', x, y, btnNum, 2);
        return describe(el);
    }

    // Click the element itself (not whatever is on top of it), and say if something else is covering it.
    function clickElement(el, count) {
        el.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' });
        var r = el.getBoundingClientRect();
        var cx = r.left + r.width / 2, cy = r.top + r.height / 2;
        var top = document.elementFromPoint(cx, cy);
        var out = clickAt(cx, cy, 'left', count, el);
        if (top && !(el.contains(top) || top.contains(el))) out.coveredBy = tag(top) + (accName(top) ? ' "' + accName(top) + '"' : '');
        return out;
    }

    function click(sel) {
        var el = resolve(sel);
        if (!el) return { ok: false, error: 'element not found: ' + sel };
        return clickElement(el, 1);
    }

    function dblclick(sel) {
        var el = resolve(sel);
        if (!el) return { ok: false, error: 'element not found: ' + sel };
        return clickElement(el, 2);
    }

    function hover(selOrX, y) {
        var el = (typeof selOrX === 'number') ? document.elementFromPoint(selOrX, y) : resolve(selOrX);
        if (!el) return { ok: false, error: 'element not found' };
        el.scrollIntoView({ block: 'center', behavior: 'instant' });
        var r = el.getBoundingClientRect();
        var hx = r.left + r.width / 2, hy = r.top + r.height / 2;
        firePointer(el, 'pointerover', hx, hy, 0);
        firePointer(el, 'pointerenter', hx, hy, 0);
        fireMouse(el, 'mouseover', hx, hy, 0);
        fireMouse(el, 'mouseenter', hx, hy, 0);
        fireMouse(el, 'mousemove', hx, hy, 0);
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

    // The thing that takes the typing: a wrapper (div role=textbox, a labelled group...) holds the real editable.
    function editableFor(el) {
        var t = tag(el);
        if (t === 'input' || t === 'textarea' || t === 'select' || el.isContentEditable) return el;
        var inner = el.querySelector('textarea, input:not([type=hidden]):not([type=checkbox]):not([type=radio]):not([type=button]):not([type=submit]), [contenteditable=""], [contenteditable="true"], [contenteditable="plaintext-only"]');
        if (inner) return inner;
        var up = el.closest && el.closest('[contenteditable=""], [contenteditable="true"], [contenteditable="plaintext-only"]');
        return up || null;
    }

    function readValue(el) {
        return el.isContentEditable ? (el.innerText || '') : ('value' in el ? String(el.value || '') : '');
    }

    function sameText(got, want) {
        var n = function (x) { return String(x).replace(/\s+/g, ' ').trim(); };
        got = n(got); want = n(want);
        return got === want || (want.length > 40 && got.indexOf(want.slice(0, 40)) >= 0);
    }

    function fill(sel, value) {
        var el = resolve(sel);
        if (!el) return { ok: false, error: 'element not found: ' + sel };
        el.scrollIntoView({ block: 'center', behavior: 'instant' });
        if (tag(el) === 'select') { el.focus(); return selectOptionRaw(el, [].concat(value)); }
        var target = editableFor(el);
        if (!target) return { ok: false, error: 'not editable: <' + tag(el) + '>. Pick the text field itself (snapshot again), or click it and use browser_type_text.' };
        if (tag(target) === 'select') { target.focus(); return selectOptionRaw(target, [].concat(value)); }
        target.focus();
        var want = String(value), ce = target.isContentEditable;

        // 1) Behave like typing: select what is there, then insert. Editors built on beforeinput/input events
        //    (React inputs, Lexical, Draft.js, ProseMirror) only accept text that arrives this way.
        try {
            if (ce) {
                var r = document.createRange(); r.selectNodeContents(target);
                var sl = getSelection(); sl.removeAllRanges(); sl.addRange(r);
            } else if (target.select) target.select();
            document.execCommand('insertText', false, want);
        } catch (e) { }
        var got = readValue(target);

        // 2) Otherwise set it directly and announce the change.
        if (!sameText(got, want)) {
            try { if (ce) target.textContent = want; else setNativeValue(target, want); } catch (e) { }
            try { target.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: want })); }
            catch (e) { target.dispatchEvent(new Event('input', { bubbles: true })); }
            target.dispatchEvent(new Event('change', { bubbles: true }));
            got = readValue(target);
        }
        var out = { ok: true, ref: assignRef(target), set: sameText(got, want), value: got.slice(0, 120) };
        if (!out.set) out.warning = 'the field does not show the text (it shows "' + got.slice(0, 60) + '"); the page may be rejecting scripted input';
        return out;
    }

    // The focused element, looking through shadow roots.
    function deepActive() {
        var el = document.activeElement;
        while (el && el.shadowRoot && el.shadowRoot.activeElement) el = el.shadowRoot.activeElement;
        return el;
    }

    function focusInfo() {
        var el = deepActive();
        if (!el || el === document.body || el === document.documentElement) return { editable: false, tag: 'body' };
        var t = tag(el);
        var ed = !!el.isContentEditable || t === 'textarea' ||
            (t === 'input' && !/^(button|submit|reset|checkbox|radio|file|image|range|color)$/i.test(el.type || 'text'));
        return { editable: ed, tag: t, name: accName(el) };
    }

    function insertText(text) {
        var el = deepActive();
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
    for (var fk = 1; fk <= 12; fk++) KEYMAP['F' + fk] = { code: 'F' + fk, keyCode: 111 + fk };
    var KEY_ALIAS = { Space: ' ', Esc: 'Escape', Return: 'Enter', Del: 'Delete', Up: 'ArrowUp', Down: 'ArrowDown', Left: 'ArrowLeft', Right: 'ArrowRight' };

    function pressKey(key, modMask) {
        key = KEY_ALIAS[key] || key;
        var el = deepActive() || document.body;
        var info = KEYMAP[key] ||
            (key.length === 1
                ? { code: /[a-z]/i.test(key) ? 'Key' + key.toUpperCase() : /\d/.test(key) ? 'Digit' + key : '', keyCode: key.toUpperCase().charCodeAt(0) }
                : { code: key, keyCode: 0 });
        var mods = modMask || 0;
        var opts = {
            key: key, code: info.code, keyCode: info.keyCode, which: info.keyCode,
            bubbles: true, cancelable: true,
            altKey: !!(mods & 1), ctrlKey: !!(mods & 2), metaKey: !!(mods & 4), shiftKey: !!(mods & 8),
        };
        var proceed = el.dispatchEvent(new KeyboardEvent('keydown', opts));
        if (proceed && (key.length === 1 || key === 'Enter')) el.dispatchEvent(new KeyboardEvent('keypress', opts));
        el.dispatchEvent(new KeyboardEvent('keyup', opts));
        // The page took the key (a chat box that sends on Enter, a shortcut): do not also run the browser default.
        if (!proceed) return { ok: true, handledByPage: true };

        // Synthetic key events do not trigger the browser's own behaviour, so do the important ones by hand.
        var t = el.tagName ? tag(el) : '';
        var editable = !!el.isContentEditable || t === 'textarea' || (t === 'input' && !/^(button|submit|reset|checkbox|radio|file|image|range|color)$/i.test(el.type || 'text'));
        var cmd = (mods & 6) !== 0; // ctrl or meta
        try {
            if (key === 'Enter' && !cmd) {
                if (t === 'textarea') document.execCommand('insertText', false, '\n');
                else if (el.isContentEditable) document.execCommand('insertParagraph');
                else if (t === 'button' || t === 'summary' || t === 'a' || el.getAttribute('role') === 'button') el.click();
                else {
                    var form = el.closest && el.closest('form');
                    if (form && form.requestSubmit) form.requestSubmit();
                }
            } else if (key === ' ' && !cmd && !editable) {
                if (t === 'button' || t === 'summary' || (t === 'input' && /^(checkbox|radio|button|submit)$/i.test(el.type || ''))
                    || /^(button|checkbox|switch|radio|menuitem)$/.test(el.getAttribute('role') || '')) el.click();
            } else if (key === 'Backspace' && editable) document.execCommand('delete');
            else if (key === 'Delete' && editable) document.execCommand('forwardDelete');
            else if (key === 'Tab') moveFocus(mods & 8 ? -1 : 1);
            else if (cmd && (key === 'a' || key === 'A') && editable) document.execCommand('selectAll');
        } catch (e) { }
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
        // A label or wrapper stands for the control inside / behind it.
        var box = el;
        var isBox = function (x) { return x.type === 'checkbox' || x.type === 'radio' || /^(checkbox|switch|radio|menuitemcheckbox|menuitemradio)$/.test(x.getAttribute && x.getAttribute('role') || ''); };
        if (!isBox(box)) {
            var inner = el.control || (el.querySelector && el.querySelector('input[type=checkbox],input[type=radio],[role=checkbox],[role=switch],[role=radio]'));
            if (inner) box = inner;
        }
        var read = function () {
            return (box.type === 'checkbox' || box.type === 'radio') ? !!box.checked : box.getAttribute('aria-checked') === 'true';
        };
        var want = !!checked;
        if (read() !== want) {
            box.scrollIntoView({ block: 'center', behavior: 'instant' });
            box.click(); // a real click toggles the control and fires input/change the way pages expect
            if (read() !== want && 'checked' in box) {
                box.checked = want;
                box.dispatchEvent(new Event('input', { bubbles: true }));
                box.dispatchEvent(new Event('change', { bubbles: true }));
            }
        }
        var now = read();
        return now === want ? { ok: true, checked: now } : { ok: false, checked: now, error: 'could not change it (radio buttons cannot be unchecked; the page may block it)' };
    }

    function selectOptionRaw(el, values) {
        var want = values.map(String);
        var norm = function (x) { return String(x).trim().toLowerCase(); };
        var find = function (eq) {
            var hits = [];
            for (var i = 0; i < el.options.length; i++) {
                var o = el.options[i];
                if (want.some(function (w) { return eq(o.value, w) || eq(o.text, w) || eq(o.label, w); })) hits.push(i);
            }
            return hits;
        };
        var hits = find(function (a, b) { return a === b; });
        if (!hits.length) hits = find(function (a, b) { return norm(a) === norm(b); });
        if (!hits.length) {
            var opts = [];
            for (var k = 0; k < el.options.length && k < 40; k++) opts.push(el.options[k].text.trim());
            return { ok: false, error: 'no option matches ' + JSON.stringify(want), options: opts };
        }
        if (!el.multiple) hits = hits.slice(0, 1);
        var matched = [];
        for (var j = 0; j < el.options.length; j++) {
            var sel = hits.indexOf(j) >= 0;
            el.options[j].selected = sel;
            if (sel) matched.push(el.options[j].value);
        }
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        return { ok: true, selected: matched };
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

    // Single-page apps often scroll an inner panel, not the window: find the one under the middle of the screen.
    function scrollerAtCenter() {
        var el = document.elementFromPoint(innerWidth / 2, innerHeight / 2);
        while (el && el !== document.body && el !== document.documentElement) {
            var st = getComputedStyle(el);
            if (/(auto|scroll)/.test(st.overflowY) && el.scrollHeight > el.clientHeight + 4) return el;
            el = el.parentElement;
        }
        return null;
    }

    function scrollPage(direction, amount) {
        amount = amount || 600;
        var BIG = 10000000;
        var map = {
            up: [0, -amount], down: [0, amount], left: [-amount, 0], right: [amount, 0],
            top: [0, -BIG], bottom: [0, BIG],
        };
        var d = map[direction] || map.down;
        var bx = scrollX, by = scrollY;
        window.scrollBy({ left: d[0], top: d[1], behavior: 'instant' });
        var root = document.scrollingElement || document.documentElement;
        if (scrollX === bx && scrollY === by) {
            var sc = scrollerAtCenter();
            if (sc) {
                sc.scrollBy({ left: d[0], top: d[1], behavior: 'instant' });
                return {
                    ok: true, container: tag(sc) + (sc.id ? '#' + sc.id : ''), scrollTop: Math.round(sc.scrollTop),
                    maxY: Math.round(sc.scrollHeight - sc.clientHeight),
                };
            }
        }
        return { ok: true, scrollY: scrollY, scrollX: scrollX, maxY: Math.max(0, Math.round(root.scrollHeight - innerHeight)) };
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

    // ---------------- click verification (used by browser_click) ----------------
    var clickProbe = null;
    function pageSig() { return location.href + '|' + document.getElementsByTagName('*').length; }
    // Arms a one-shot capture listener for the real OS click that is about to be posted at `sel`.
    function clickWatch(sel) {
        clickWatchOff();
        var el = resolve(sel);
        clickProbe = { sig: pageSig(), h: null, got: null };
        window.__brainClickDoc = clickProbe.token = 'c' + Math.random().toString(36).slice(2);
        clickProbe.h = function (e) {
            var t = e.target;
            clickProbe.got = {
                tag: t && t.tagName,
                name: ((t && t.getAttribute && t.getAttribute('aria-label')) || (t && t.innerText) || '').slice(0, 40),
                hit: !!(el && t && (el === t || el.contains(t) || t.contains(el))),
            };
        };
        window.addEventListener('mousedown', clickProbe.h, true);
        return clickProbe.token;
    }
    function clickWatchOff() {
        if (clickProbe && clickProbe.h) window.removeEventListener('mousedown', clickProbe.h, true);
    }
    // What the click did: null if no mousedown arrived, else {hit, tag, name, changed}.
    function clickResult() {
        var p = clickProbe; clickWatchOff(); clickProbe = null;
        if (!p || !p.got) return null;
        p.got.changed = pageSig() !== p.sig;
        return p.got;
    }

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
        rawOn(window, 'error', function (e) {
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
        // document nav first, then subresources — the combined tail is what
        // incremental calls (no sinceIndex) return.
        var entries = performance.getEntriesByType('navigation')
            .concat(performance.getEntriesByType('resource'));
        var start = sinceIndex !== undefined ? sinceIndex : _netSeen;
        _netSeen = entries.length;
        var origin = performance.timeOrigin || 0;
        return entries.slice(start).map(function (e) {
            return {
                name: e.name.slice(0, 300),
                type: e.initiatorType || (e.entryType === 'navigation' ? 'document' : 'other'),
                duration: Math.round(e.duration), size: e.transferSize || 0,
                start: Math.round(e.startTime),
                t: Math.round(origin + e.startTime), // wall-clock epoch ms
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

    // ---------------- stable locators (activity log / flows) ----------------
    // Refs (e1, e2, ...) are reassigned on every snapshot, so a logged
    // `click e7` cannot be replayed. locator() turns whatever the caller used
    // into selectors that survive a reload, verified to resolve to the SAME
    // element right now, best first.

    function cssEsc(s) {
        return (window.CSS && CSS.escape) ? CSS.escape(String(s)) : String(s).replace(/[^\w-]/g, '\\$&');
    }
    function uniqueCss(css, el) {
        try { var n = document.querySelectorAll(css); return n.length === 1 && n[0] === el; } catch (e) { return false; }
    }
    // Generated ids (react-:r1:, ember123, long hex/digit runs) change per load.
    function looksStable(v) {
        if (!v || v.length > 60) return false;
        if (/\d{4,}/.test(v)) return false;
        if (/[0-9a-f]{8,}/i.test(v)) return false;
        if (/^(:r|ember|react|radix|mui-|rc[-_]|headlessui)/i.test(v)) return false;
        return true;
    }
    function cssPath(el) {
        var parts = [];
        var cur = el;
        while (cur && cur.nodeType === 1 && cur !== document.documentElement) {
            if (cur.id && looksStable(cur.id)) { parts.unshift('#' + cssEsc(cur.id)); break; }
            var t = tag(cur), idx = 1, sib = cur;
            while ((sib = sib.previousElementSibling)) if (tag(sib) === t) idx++;
            parts.unshift(t + ':nth-of-type(' + idx + ')');
            cur = cur.parentElement;
            if (parts.length > 8) break;
        }
        return parts.join(' > ');
    }
    var SENSITIVE_RE = /pass(word|wd)?|pwd|secret|token|cvv|cvc|card|otp|ssn|pin\b|2fa|mfa/i;
    function sensitiveEl(el) {
        if (!el || !el.getAttribute) return false;
        var t = (el.type || '').toLowerCase();
        if (t === 'password') return true;
        var ac = (el.getAttribute('autocomplete') || '').toLowerCase();
        if (/^cc-|one-time-code|current-password|new-password/.test(ac)) return true;
        return SENSITIVE_RE.test((el.name || '') + ' ' + (el.id || ''));
    }
    function locator(sel) { return locatorFor(resolve(sel), String(sel)); }
    // Same, for an element already in hand (the recorder): no caller-authored selector.
    function locatorFor(el, sel) {
        if (!el) return null;
        var out = [];
        var s = String(sel).trim();
        var isRef = /^(?:@|ref[:=])?e\d+$/i.test(s) || /^ref[:=]/i.test(s);
        if (s && !isRef && resolve(s) === el) out.push(s); // caller-authored selector wins
        if (el.id && looksStable(el.id)) {
            var c = '#' + cssEsc(el.id);
            if (uniqueCss(c, el)) out.push('css:' + c);
        }
        ['data-testid', 'data-test', 'data-qa', 'data-cy', 'name', 'aria-label', 'placeholder'].forEach(function (a) {
            var v = el.getAttribute(a);
            if (v && looksStable(v)) {
                var c2 = tag(el) + '[' + a + '="' + v.replace(/"/g, '\\"') + '"]';
                if (uniqueCss(c2, el)) out.push('css:' + c2);
            }
        });
        var role = roleOf(el), name = accName(el);
        if (role && name && name.length <= 80 && findByRole(role, name) === el) out.push('role:' + role + ':' + name);
        if (name && name.length <= 80 && findByText(name) === el) out.push('text:' + name);
        var path = cssPath(el);
        if (path && uniqueCss(path, el)) out.push('css:' + path);
        var seen = {}, uniq = [];
        out.forEach(function (x) { if (!seen[x]) { seen[x] = 1; uniq.push(x); } });
        return {
            sel: uniq[0] || s, alts: uniq.slice(1, 5), tag: tag(el), role: role,
            name: name ? String(name).slice(0, 80) : null, sensitive: sensitiveEl(el),
        };
    }
    // ---------------- recorder: "show it once" ----------------
    // While the user records, their own clicks, typing and choices stream to the host as
    // {b:31}. Secret fields send a mask only; the value never leaves the page.
    var REC = null;
    function recSend(ev) {
        try { window.external && window.external.sendMessage(JSON.stringify({ b: 31, ev: ev })); } catch (e) { }
    }
    function recIgnore(el) { return !el || el.nodeType !== 1 || el.id === '__brain-chrome-host'; }
    function recTextLike(el) {
        if (!el || el.nodeType !== 1) return false;
        var t = tag(el);
        if (t === 'textarea') return true;
        if (t === 'input') return !/^(checkbox|radio|submit|button|image|reset|file|range|color|hidden)$/i.test(el.type || 'text');
        return !!el.isContentEditable && !(el.parentElement && el.parentElement.isContentEditable);
    }
    function recValueOf(el) {
        if (el.isContentEditable) return String(el.innerText || '').slice(0, 4000);
        return String(el.value == null ? '' : el.value).slice(0, 4000);
    }
    function recFlush(el) {
        if (!REC || recIgnore(el) || !recTextLike(el)) return;
        var v = recValueOf(el), prev = REC.vals.has(el) ? REC.vals.get(el) : '';
        if (v === prev) return;
        REC.vals.set(el, v);
        var loc = locatorFor(el, '');
        if (!loc) return;
        var masked = sensitiveEl(el);
        recSend({ k: 'type', loc: loc, value: masked ? '' : v, masked: masked, url: location.href });
    }
    function recStart() {
        if (REC) return true;
        var R = REC = { vals: new WeakMap(), off: [] };
        function on(type, fn) { window.addEventListener(type, fn, true); R.off.push(function () { window.removeEventListener(type, fn, true); }); }
        on('focusin', function (e) {
            var el = e.target;
            if (!recIgnore(el) && recTextLike(el) && !R.vals.has(el)) R.vals.set(el, recValueOf(el));
        });
        on('focusout', function (e) { recFlush(e.target); });
        on('change', function (e) {
            var el = e.target;
            if (!e.isTrusted || recIgnore(el)) return;
            var t = tag(el), ty = String(el.type || '').toLowerCase();
            if (recTextLike(el)) { recFlush(el); return; }
            var loc = locatorFor(el, '');
            if (!loc) return;
            if (t === 'select') {
                var vals = Array.prototype.map.call(el.selectedOptions || [], function (o) { return o.value || o.text; });
                recSend({ k: 'select', loc: loc, values: vals, url: location.href });
            } else if (t === 'input' && (ty === 'checkbox' || ty === 'radio')) {
                if (ty === 'radio' && !el.checked) return;
                recSend({ k: 'check', loc: loc, checked: !!el.checked, url: location.href });
            }
        });
        on('keydown', function (e) {
            var el = e.target;
            if (!e.isTrusted || recIgnore(el) || e.key !== 'Enter' || e.shiftKey || e.isComposing) return;
            if (!recTextLike(el) || tag(el) === 'textarea') return;
            recFlush(el);
            var loc = locatorFor(el, '');
            recSend({ k: 'key', key: 'Enter', loc: loc, url: location.href });
        });
        on('click', function (e) {
            var el = e.target;
            if (!e.isTrusted || e.button > 0 || recIgnore(el)) return;
            var t = el.closest && el.closest('button,a[href],summary,input[type=submit],input[type=button],input[type=image],input[type=reset],[role=button],[role=link],[role=menuitem],[role=menuitemcheckbox],[role=tab],[role=option],[role=switch],[onclick]');
            if (!t) { try { if (getComputedStyle(el).cursor === 'pointer') t = el; } catch (e2) { } }
            if (!t) return;
            var tg = tag(t), ty = String(t.type || '').toLowerCase();
            if (tg === 'label' || tg === 'select' || recTextLike(t) || (tg === 'input' && (ty === 'checkbox' || ty === 'radio'))) return;
            var loc = locatorFor(t, '');
            if (!loc) return;
            recFlush(document.activeElement);
            recSend({ k: 'click', loc: loc, url: location.href });
        });
        return true;
    }
    function recStop() {
        if (!REC) return false;
        REC.off.forEach(function (f) { try { f(); } catch (e) { } });
        REC = null;
        return true;
    }

    // ---------------- seatbelt: what would this click do? ----------------
    function riskFacts(el) {
        if (!el || el.nodeType !== 1) return null;
        var btn = (el.closest && el.closest('button,input,a,summary,[role=button],[role=link],[role=menuitem]')) || el;
        var tg = tag(btn);
        var type = String((btn.getAttribute && btn.getAttribute('type')) || '').toLowerCase();
        var form = btn.form || (btn.closest && btn.closest('form')) || null;
        var submit = !!form && ((tg === 'button' && (type === '' || type === 'submit')) ||
            (tg === 'input' && (type === 'submit' || type === 'image')));
        var card = false;
        if (form && submit) {
            var fs = form.elements || [];
            for (var i = 0; i < fs.length && i < 60; i++) {
                var f = fs[i], ac = String((f.getAttribute && f.getAttribute('autocomplete')) || '').toLowerCase();
                if (/^cc-(number|csc|exp)/.test(ac) || /card.?num|cardnumber|cvv|cvc/i.test((f.name || '') + ' ' + (f.id || ''))) { card = true; break; }
            }
        }
        var name = '';
        try { name = accName(btn) || btn.value || (btn.textContent || '').trim(); } catch (e) { }
        return {
            tag: tg, role: roleOf(btn), type: type, name: String(name || '').replace(/\s+/g, ' ').slice(0, 80),
            submit: submit, post: !!form && String(form.method || '').toLowerCase() === 'post', card: card, host: location.hostname,
        };
    }
    function risk(sel) { var el = null; try { el = resolve(sel); } catch (e) { } return riskFacts(el); }
    function riskAt(x, y) { return riskFacts(document.elementFromPoint(x, y)); }
    function riskKey() {
        var a = document.activeElement;
        if (!a) return null;
        if (/^(button|a|summary)$/i.test(a.tagName) || a.getAttribute('role') === 'button') return riskFacts(a);
        if (!a.form) return null;
        var b = a.form.querySelector('button[type=submit],input[type=submit],button:not([type])');
        return b ? riskFacts(b) : null;
    }

    // First selector (in order) that currently resolves, or null.
    function firstMatch(sels) {
        if (!Array.isArray(sels)) return null;
        for (var i = 0; i < sels.length; i++) {
            try { if (resolve(sels[i])) return sels[i]; } catch (e) { }
        }
        return null;
    }
    function isSensitiveFocus() { return sensitiveEl(document.activeElement); }

    // ---------------- event-listener registry ----------------
    // Pages have no API for "what listeners are bound to this node?". So we
    // wrap EventTarget.prototype.add/removeEventListener and the on* handler
    // setters here — at document-start, before any page script runs — and
    // remember who registered what. Registration stacks are kept as
    // un-formatted Error objects and only parsed when someone asks, so the
    // hot path stays cheap. BrAIn's own window/document listeners go through
    // rawOn() (the original addEventListener) and never enter the registry.

    var LR_HOST = '__brain-chrome-host';
    var LR_LATE = !!(document.body || document.readyState !== 'loading');
    var LR = (function () {
        var ET = window.EventTarget && window.EventTarget.prototype;
        if (!ET || typeof ET.addEventListener !== 'function' || typeof WeakMap !== 'function') return null;
        var rawAdd = ET.addEventListener, rawRemove = ET.removeEventListener;
        var fnToString = Function.prototype.toString;
        var ErrorC = Error;
        var HAS_WR = typeof WeakRef === 'function';
        var HIST = 20;          // removed records kept per (target, type)
        var ERR_CAP = 40000;    // stop capturing stacks after this many registrations
        var store = new WeakMap();   // target -> Map(type -> [record])
        var pstore = new WeakMap();  // target -> { type: {fn, err, t} | null }  (on* assignments)
        var seen = new WeakSet();
        var refs = [], sweepAt = 4000, seq = 0, nErr = 0;
        var hnCache = new Map();

        function nowT() { return Math.round(performance.now()); }

        // Named so frames from the hook itself can be stripped from stacks.
        function __brainMk() {
            if (nErr++ > ERR_CAP) return null;
            var had = typeof ErrorC.stackTraceLimit === 'number', lim = had ? ErrorC.stackTraceLimit : 0;
            try { if (had) ErrorC.stackTraceLimit = 14; } catch (e) { had = false; }
            var err = new ErrorC();
            try { if (had) ErrorC.stackTraceLimit = lim; } catch (e) { }
            return err;
        }

        function track(t) {
            if (!HAS_WR || seen.has(t)) return;
            seen.add(t);
            refs.push(new WeakRef(t));
            if (refs.length > sweepAt) {
                refs = refs.filter(function (r) { return r.deref() !== undefined; });
                sweepAt = Math.max(4000, refs.length * 2);
            }
        }
        function bucket(t, type, create) {
            var m = store.get(t);
            if (!m) {
                if (!create) return null;
                m = new Map(); store.set(t, m); track(t);
            }
            var a = m.get(type);
            if (!a && create) { a = []; m.set(type, a); }
            return a || null;
        }
        function normOpts(o) {
            if (o !== null && typeof o === 'object') {
                // Each member is read exactly once (getters may have side effects);
                // the browser then receives the normalised copy.
                var n = { cap: !!o.capture, once: !!o.once, passive: o.passive, sig: o.signal };
                n.o = { capture: n.cap, once: n.once, passive: n.passive, signal: n.sig };
                return n;
            }
            return { cap: !!o, once: false, passive: undefined, sig: undefined, o: o };
        }

        function retire(rec, why, t, err) {
            if (rec.removed) return;
            rec.removed = why; rec.rt = nowT();
            if (err) rec.rerr = err; // who removed it (captured by the caller so hook frames strip cleanly)
            if (rec.mark && t) { try { rawRemove.call(t, rec.type, rec.mark, rec.cap); } catch (e) { } }
            rec.mark = null;
            var arr = rec.arr;
            arr.nr = (arr.nr || 0) + 1;
            if (arr.nr > HIST * 2) { // keep every live record + the newest HIST removed ones
                var drop = arr.nr - HIST;
                for (var i = 0; i < arr.length && drop > 0; ) {
                    if (arr[i].removed) { arr.splice(i, 1); drop--; arr.nr--; } else i++;
                }
            }
        }
        function hookSignal(rec, sig) {
            // Weak: a long-lived AbortSignal must not pin the target through our closure.
            var wr = HAS_WR ? new WeakRef(rec) : null;
            rawAdd.call(sig, 'abort', function () {
                var r = wr ? wr.deref() : rec;
                if (r) retire(r, 'signal');
            }, { once: true });
        }

        function __brainAdd(type, fn, opts) {
            if (arguments.length < 2) return rawAdd.apply(this, arguments);
            var t = this, n = normOpts(opts), ty = `${type}`, arr = null, dup = false, comp = null, rec = null;
            try {
                if (fn != null && !(n.sig && n.sig.aborted)) {
                    arr = bucket(t, ty, true);
                    for (var i = 0; i < arr.length; i++) {
                        var r = arr[i];
                        if (!r.removed && r.fn === fn && r.cap === n.cap) { dup = true; break; }
                    }
                    if (!dup && n.once) {
                        // once listeners vanish when they fire; a marker registered just before
                        // theirs runs exactly when theirs does, so the record can be retired.
                        comp = function () { if (rec) retire(rec, 'once'); };
                        rawAdd.call(t, ty, comp, { capture: n.cap, once: true, passive: true, signal: n.sig });
                    }
                }
            } catch (e) { arr = null; comp = null; }
            var res;
            try { res = rawAdd.call(t, ty, fn, n.o); }
            catch (e) { if (comp) { try { rawRemove.call(t, ty, comp, n.cap); } catch (e2) { } } throw e; }
            if (arr && !dup) {
                try {
                    rec = { id: ++seq, type: ty, fn: fn, cap: n.cap, once: n.once, passive: n.passive,
                        err: __brainMk(), t: nowT(), removed: '', mark: comp, arr: arr };
                    arr.push(rec);
                    if (n.sig) hookSignal(rec, n.sig);
                } catch (e) { }
            }
            return res;
        }
        function __brainRm(type, fn, opts) {
            var res = rawRemove.apply(this, arguments);
            try {
                if (arguments.length >= 2 && fn != null) {
                    var cap = opts !== null && typeof opts === 'object' ? !!opts.capture : !!opts;
                    var arr = bucket(this, `${type}`, false);
                    if (arr) for (var i = 0; i < arr.length; i++) {
                        var r = arr[i];
                        if (!r.removed && r.fn === fn && r.cap === cap) { retire(r, 'removed', this, __brainMk()); break; }
                    }
                }
            } catch (e) { }
            return res;
        }
        try { Object.defineProperty(__brainAdd, 'length', { value: 2, configurable: true }); } catch (e) { }
        try { Object.defineProperty(__brainRm, 'length', { value: 2, configurable: true }); } catch (e) { }

        // ---- on* handler properties (el.onclick = fn) ----
        function __brainNote(t, type, fn) {
            var m = pstore.get(t);
            if (!m) { m = Object.create(null); pstore.set(t, m); track(t); }
            m[type] = fn ? { fn: fn, err: __brainMk(), t: nowT() } : null;
        }
        function hookProps(obj) {
            var names;
            try { names = Object.getOwnPropertyNames(obj); } catch (e) { return; }
            names.forEach(function (name) {
                if (name.length < 3 || name.charCodeAt(0) !== 111 || name.charCodeAt(1) !== 110) return;
                var d;
                try { d = Object.getOwnPropertyDescriptor(obj, name); } catch (e) { return; }
                if (!d || !d.set || !d.configurable) return;
                var type = name.slice(2), origSet = d.set;
                var setter = function __brainSet(v) {
                    try { __brainNote(this, type, typeof v === 'function' ? v : null); } catch (e) { }
                    return origSet.call(this, v);
                };
                try { Object.defineProperty(obj, name, { get: d.get, set: setter, enumerable: d.enumerable, configurable: true }); } catch (e) { }
            });
        }

        // ---- reading ----
        function isInternal(t) {
            try {
                if (t.id === LR_HOST) return true;
                var r = t.getRootNode ? t.getRootNode() : null;
                return !!(r && r.host && r.host.id === LR_HOST);
            } catch (e) { return false; }
        }
        function label(t) {
            try {
                if (t === window) return 'window';
                if (t === document) return 'document';
                if (t.nodeType === 11) return '#shadow-root';
                if (t.nodeType === 1) {
                    var s = t.tagName.toLowerCase();
                    if (t.id) s += '#' + t.id;
                    var c = (typeof t.className === 'string' ? t.className : '').trim().split(/\s+/).filter(Boolean).slice(0, 3);
                    if (c.length) s += '.' + c.join('.');
                    return s.length > 80 ? s.slice(0, 77) + '…' : s;
                }
                var n = (t.constructor && t.constructor.name) || 'EventTarget';
                if (typeof t.url === 'string') n += ' ' + t.url.slice(0, 60);
                else if (typeof t.media === 'string') n += ' ' + t.media.slice(0, 60);
                return n;
            } catch (e) { return 'EventTarget'; }
        }
        function safeGet(t, k) { try { return t[k]; } catch (e) { return undefined; } }
        function handlerNames(t) {
            var out = [], dup = Object.create(null), p = t, guard = 0;
            while (p && p !== Object.prototype && guard++ < 12) {
                var ns = hnCache.get(p);
                if (!ns) {
                    ns = [];
                    try {
                        Object.getOwnPropertyNames(p).forEach(function (name) {
                            if (!/^on[a-z]/.test(name)) return;
                            var d = Object.getOwnPropertyDescriptor(p, name);
                            if (d && d.set) ns.push(name);
                        });
                    } catch (e) { }
                    // Instances (elements, sockets…) must not be pinned by the cache; only window and prototypes are.
                    if (p === window || p !== t) hnCache.set(p, ns);
                }
                for (var i = 0; i < ns.length; i++) if (!dup[ns[i]]) { dup[ns[i]] = 1; out.push(ns[i]); }
                p = Object.getPrototypeOf(p);
            }
            return out;
        }
        function fnInfo(f, withSrc) {
            var o = {};
            try {
                var h = f;
                if (f !== null && typeof f === 'object' && typeof f.handleEvent === 'function') { h = f.handleEvent; o.handleEvent = true; }
                if (typeof h !== 'function') { o.name = ''; return o; }
                o.name = h.name || ''; o.len = h.length;
                var s = fnToString.call(h);
                o.native = /\{\s*\[native code\]\s*\}\s*$/.test(s);
                o.size = s.length;
                if (withSrc) o.src = s.length > 1200 ? s.slice(0, 1200) + '…' : s;
            } catch (e) { }
            return o;
        }
        function frames(err, max) {
            var out = [];
            if (!err) return out;
            var s;
            try { s = err.stack; } catch (e) { return out; }
            if (typeof s !== 'string') return out;
            var lines = s.split('\n');
            for (var i = 0; i < lines.length && out.length < max; i++) {
                var L = lines[i].trim();
                if (!L || /__brain(Mk|Add|Rm|Set|Note)/.test(L)) continue;
                var m, f, u, ln, col;
                if ((m = /^at (?:(.*?) \()?(.*?):(\d+):(\d+)\)?$/.exec(L))) { f = m[1] || ''; u = m[2]; ln = +m[3]; col = +m[4]; }
                else if ((m = /^(.*?)@(.*?)(?::(\d+):(\d+))?$/.exec(L))) { f = m[1]; u = m[2]; ln = +m[3] || 0; col = +m[4] || 0; }
                else continue;
                if (!u || u === '[native code]' || u === '<anonymous>' || u.indexOf('user-script:') === 0) continue; // BrAIn's own injected lib (WKUserScript)
                out.push({ fn: f, url: u, line: ln, col: col });
            }
            return out;
        }
        function siteStr(fr) {
            var u = fr.url, o = location.origin;
            if (o && o !== 'null' && u.indexOf(o) === 0) u = u.slice(o.length) || '/';
            return u + (fr.line ? ':' + fr.line + (fr.col ? ':' + fr.col : '') : '');
        }

        // One descriptor per live (or, on request, removed) listener on t.
        function collect(t, o) {
            var out = [], types = o.types;
            var m = store.get(t);
            if (m) m.forEach(function (arr, type) {
                if (types && !types[type]) return;
                for (var i = 0; i < arr.length; i++) {
                    var r = arr[i];
                    if (r.removed && !o.removed) continue;
                    out.push({ id: r.id, type: r.type, cap: r.cap, once: r.once, passive: r.passive, how: 'listener',
                        at: r.t, removed: r.removed, rt: r.rt, fn: r.fn, err: r.err, rerr: r.rerr, rec: r, t: t });
                }
            });
            out.sort(function (a, b) { return a.id - b.id; });
            var pm = pstore.get(t), names = handlerNames(t);
            for (var j = 0; j < names.length; j++) {
                var name = names[j], type2 = name.slice(2);
                if (types && !types[type2]) continue;
                // The live handler is ground truth (setAttribute after onX=null re-arms it);
                // recorded assignments / attributes only say where it came from.
                var cur = safeGet(t, name);
                if (typeof cur !== 'function') continue;
                var pr = pm ? pm[type2] : null, d;
                if (pr && pr.fn === cur) d = { fn: cur, err: pr.err, at: pr.t };
                else if (t.nodeType === 1 && t.hasAttribute(name)) d = { fn: null, code: t.getAttribute(name), attr: true };
                else d = { fn: cur };
                d.id = 0; d.type = type2; d.cap = false; d.once = false; d.how = d.attr ? 'attribute' : 'property';
                d.name = name; d.t = t;
                out.push(d);
            }
            return out;
        }
        function shape(d, o) {
            var fr = frames(d.err, 12);
            var out = { id: d.id || undefined, type: d.type, phase: d.cap ? 'capture' : 'bubble', how: d.how };
            if (d.once) out.once = true;
            if (d.passive !== undefined) out.passive = !!d.passive;
            if (d.at !== undefined) out.at = d.at;
            if (d.removed) {
                out.removed = d.removed;
                var rf = frames(d.rerr, 4);
                if (rf.length) out.removedAt = siteStr(rf[0]);
            }
            out.fn = d.fn != null ? fnInfo(d.fn, o.src !== false)
                : { name: '', attr: true, src: String(d.code).slice(0, 1200) };
            out.site = fr.length ? siteStr(fr[0]) : '';
            if (o.stack !== false) out.stack = fr.map(function (x) { return { fn: x.fn, at: siteStr(x) }; });
            // Live handle for in-page consumers (DevTools); not serialised.
            out.remove = function () {
                if (d.how === 'listener') {
                    rawRemove.call(d.t, d.type, d.fn, d.cap);
                    retire(d.rec, 'removed', d.t);
                } else if (d.how === 'attribute') d.t.removeAttribute(d.name);
                else d.t[d.name] = null;
            };
            return out;
        }
        function pathOf(node, ancestors) {
            var chain = [node];
            if (!ancestors) return chain;
            var n = node, guard = 0;
            while (n && guard++ < 300) {
                var p = n.parentNode || n.host || null;
                if (!p) break;
                chain.push(p); n = p;
            }
            var top = chain[chain.length - 1];
            if (node !== window && (top === document || top.nodeType === 9)) chain.push(window);
            return chain;
        }
        function pickTarget(sel) {
            if (sel && typeof sel === 'object' && typeof sel.addEventListener === 'function') return sel;
            var s = String(sel == null ? '' : sel).trim().toLowerCase();
            if (s === 'window') return window;
            if (s === 'document') return document;
            return resolve(sel);
        }

        function query(sel, o) {
            o = o || {};
            var t = pickTarget(sel);
            if (!t) return { ok: false, error: 'element not found: ' + sel };
            var types = null;
            if (o.type) { types = Object.create(null); String(o.type).split(/[\s,]+/).forEach(function (x) { if (x) types[x.replace(/^on/, '')] = 1; }); }
            var anc = o.ancestors !== false;
            var chain = pathOf(t, anc), groups = [], total = 0, cap = o.max || 400, omitted = 0;
            for (var i = 0; i < chain.length; i++) {
                var node = chain[i];
                if (isInternal(node)) continue;
                var ds = collect(node, { types: types, removed: !!o.removed });
                if (!ds.length && node !== t) continue;
                var room = Math.max(0, cap - total);
                if (ds.length && !room) { omitted += ds.length; continue; } // cap hit: don't show an empty-looking group
                var g = { target: label(node), rel: node === t ? 'target' : 'ancestor', listeners: [] };
                if (node.nodeType === 1 && ds.length && o.refs !== false) g.ref = assignRef(node);
                Object.defineProperty(g, 'node', { value: node, enumerable: false });
                for (var k = 0; k < ds.length && k < room; k++) { g.listeners.push(shape(ds[k], o)); total++; }
                if (ds.length > g.listeners.length) { g.more = ds.length - g.listeners.length; omitted += g.more; }
                groups.push(g);
            }
            var res = { ok: true, late: LR_LATE, target: label(t), total: total, groups: groups };
            if (omitted) res.omitted = omitted;
            return res;
        }

        function summary(o) {
            o = o || {};
            var byType = Object.create(null), rows = new Map(), total = 0, removedN = 0;
            function row(t) {
                var r = rows.get(t);
                if (!r) { r = { t: t, n: 0, types: Object.create(null) }; rows.set(t, r); }
                return r;
            }
            function bump(t, type, k) { var r = row(t); r.n += k; r.types[type] = (r.types[type] || 0) + k; byType[type] = (byType[type] || 0) + k; total += k; }
            for (var i = 0; i < refs.length; i++) {
                var t = refs[i].deref();
                if (!t || isInternal(t)) continue;
                var m = store.get(t);
                if (m) m.forEach(function (arr, type) {
                    for (var j = 0; j < arr.length; j++) { if (arr[j].removed) removedN++; else bump(t, type, 1); }
                });
                var pm = pstore.get(t);
                if (pm) for (var ty in pm) if (pm[ty] && typeof safeGet(t, 'on' + ty) === 'function') bump(t, ty, 1);
            }
            if (o.attrs !== false && document.documentElement) {
                var all = document.getElementsByTagName('*');
                for (var a = 0; a < all.length; a++) {
                    var el = all[a], at = el.attributes;
                    if (!at || !at.length || el.id === LR_HOST) continue;
                    for (var b = 0; b < at.length; b++) {
                        var nm = at[b].name;
                        if (nm.length > 2 && nm.charCodeAt(0) === 111 && nm.charCodeAt(1) === 110) {
                            var pm2 = pstore.get(el), pr2 = pm2 && pm2[nm.slice(2)];
                            if (pr2 && safeGet(el, nm) === pr2.fn) continue; // already counted as a property assignment
                            bump(el, nm.slice(2), 1);
                        }
                    }
                }
            }
            var list = [];
            rows.forEach(function (r) { list.push(r); });
            list.sort(function (x, y) { return y.n - x.n; });
            var max = o.max || 40;
            var targets = list.slice(0, max).map(function (r) {
                var g = { target: label(r.t), count: r.n, types: r.types };
                if (r.t.nodeType === 1 && o.refs !== false) g.ref = assignRef(r.t);
                return g;
            });
            var types = Object.keys(byType).map(function (k) { return [k, byType[k]]; })
                .sort(function (x, y) { return y[1] - x[1]; });
            return { ok: true, late: LR_LATE, total: total, removedKept: removedN, targetCount: list.length,
                types: types.slice(0, 60), targets: targets, truncated: list.length > max || undefined };
        }

        // ---- install ----
        ET.addEventListener = __brainAdd;
        ET.removeEventListener = __brainRm;
        ['Element', 'HTMLElement', 'SVGElement', 'MathMLElement', 'HTMLBodyElement', 'HTMLFrameSetElement',
            'Document', 'Window', 'XMLHttpRequest', 'XMLHttpRequestEventTarget', 'WebSocket', 'Worker',
            'SharedWorker', 'MessagePort', 'BroadcastChannel', 'MediaQueryList', 'EventSource', 'FileReader',
            'IDBRequest', 'IDBTransaction', 'IDBDatabase', 'Notification', 'ServiceWorkerContainer',
            'ServiceWorker', 'ServiceWorkerRegistration', 'RTCPeerConnection', 'RTCDataChannel',
            'BaseAudioContext', 'MediaStream', 'MediaStreamTrack', 'MediaRecorder', 'MediaSource',
            'SourceBuffer', 'SourceBufferList', 'TextTrack', 'TextTrackList', 'Animation', 'AbortSignal',
            'VisualViewport', 'ScreenOrientation', 'Performance', 'FontFaceSet', 'OffscreenCanvas'
        ].forEach(function (nm) {
            try { var C = window[nm]; if (typeof C === 'function' && C.prototype) hookProps(C.prototype); } catch (e) { }
        });
        hookProps(window);

        return {
            query: query, summary: summary,
            rawOn: function (t, type, fn, o) { return rawAdd.call(t, type, fn, o); },
        };
    })();

    // The original addEventListener — for BrAIn's own page-level listeners.
    function rawOn(t, type, fn, o) { return LR ? LR.rawOn(t, type, fn, o) : t.addEventListener(type, fn, o); }

    function lrOff() { return { ok: false, error: 'listener registry unavailable (no EventTarget)' }; }
    function listeners(sel, o) { return LR ? LR.query(sel, o) : lrOff(); }
    function listenerSummary(o) { return LR ? LR.summary(o) : lrOff(); }

    // Compact text form for agents: target by target, one line per listener.
    function listenersText(sel, o) {
        o = o || {};
        o.refs = o.refs !== false;
        var r = (sel == null || sel === '') ? listenerSummary(o) : listeners(sel, o);
        if (!r.ok) return r.error || 'failed';
        var out = [];
        if (r.late) out.push('! registry started after the page\'s scripts ran — listeners added earlier are missing (reload the page for a complete picture)');
        if (r.groups) {
            out.push(r.target + ': ' + r.total + ' listener' + (r.total === 1 ? '' : 's') + ' shown' +
                (o.ancestors === false ? '' : ' (target + ancestors)') +
                (r.omitted ? ', ' + r.omitted + ' more not shown (raise max or filter by type)' : ''));
            r.groups.forEach(function (g) {
                out.push('');
                out.push('[' + g.rel + '] ' + g.target + (g.ref ? ' [ref=' + g.ref + ']' : '') + (g.listeners.length ? '' : '  (none)'));
                g.listeners.forEach(function (l) {
                    var flags = [l.phase, l.how];
                    if (l.once) flags.push('once');
                    if (l.passive !== undefined) flags.push(l.passive ? 'passive' : 'non-passive');
                    if (l.removed) flags.push('REMOVED:' + l.removed);
                    out.push('  ' + l.type + '  ' + flags.join(' ') + '  ' + (l.fn.name || (l.fn.attr ? 'inline attr' : 'anonymous')) +
                        (l.site ? '  @ ' + l.site : ''));
                    var src = (l.fn.src || '').replace(/\s+/g, ' ').trim();
                    if (src) out.push('      ' + (src.length > 160 ? src.slice(0, 160) + '…' : src));
                    if (l.removedAt) out.push('      removed @ ' + l.removedAt);
                });
                if (g.more) out.push('  … ' + g.more + ' more on this target');
            });
            return out.join('\n');
        }
        out.push('Page-wide: ' + r.total + ' live listeners on ' + r.targetCount + ' targets' +
            (r.removedKept ? ' (' + r.removedKept + ' removed kept in history)' : ''));
        out.push('By type: ' + r.types.map(function (x) { return x[0] + ' ' + x[1]; }).join(', '));
        out.push('');
        r.targets.forEach(function (g) {
            var tk = Object.keys(g.types).sort(function (a, b) { return g.types[b] - g.types[a]; });
            out.push(g.target + (g.ref ? ' [ref=' + g.ref + ']' : '') + '  ' + g.count + '  ' +
                tk.slice(0, 10).map(function (k) { return k + (g.types[k] > 1 ? '×' + g.types[k] : ''); }).join(' ') +
                (tk.length > 10 ? '  +' + (tk.length - 10) + ' more types' : ''));
        });
        if (r.truncated) out.push('… more targets (raise max)');
        return out.join('\n');
    }

    // ---------------- page -> Markdown ----------------
    // Turns the page (or the user's selection, or one element) into clean
    // Markdown for pasting into an AI chat, notes or a bug report. Works on
    // what is *visible*: scripts, styles, hidden nodes, form controls and
    // (for whole pages) navigation / footers / cookie banners are dropped.
    // markdownInfo() returns { md, title, url, source, chars }; markdown()
    // is the string-only form used by the browser_markdown MCP tool.

    var MD_SKIP = /^(script|style|noscript|template|svg|canvas|iframe|object|embed|head|link|meta|title|select|option|datalist|button|input|textarea|dialog|map|area|audio|video|source|track)$/;
    var MD_BLOCK = /^(address|article|aside|blockquote|body|center|details|dd|div|dl|dt|fieldset|figcaption|figure|footer|form|h[1-6]|header|hgroup|hr|li|main|nav|ol|p|pre|section|summary|table|ul|menu|caption|thead|tbody|tfoot|tr|td|th|legend)$/;
    var MD_NOISE_ROLE = /^(navigation|banner|contentinfo|complementary|dialog|alertdialog|search)$/;
    var MD_CONSENT = /cookie|consent|gdpr|onetrust|didomi|cmp-|privacy-banner/i;

    function mdVisible(el) {
        if (!el.isConnected) return true; // cloned selection fragments carry no layout
        if (el.hasAttribute('hidden') || el.getAttribute('aria-hidden') === 'true') return false;
        var s;
        try { s = getComputedStyle(el); } catch (e) { return true; }
        if (s.display === 'none' || s.visibility === 'hidden' || s.visibility === 'collapse') return false;
        if (s.opacity === '0' && el.children.length === 0) return false;
        // screen-reader-only text ("Skip to content", "opens in new tab") and off-canvas items
        if (el.children.length === 0 || s.overflow !== 'visible') {
            var r = el.getBoundingClientRect();
            if (r.width <= 1 && r.height <= 1 && s.overflow !== 'visible') return false;
            if (r.width > 0 && r.right + (window.scrollX || 0) < -50) return false;
            if (r.height > 0 && r.bottom + (window.scrollY || 0) < -50) return false;
        }
        return true;
    }

    function mdEscText(s) {
        return s.replace(/\\/g, '\\\\').replace(/([*`])/g, '\\$1')
            .replace(/(^|\s)_|_(\s|$)/g, function (m) { return m.replace('_', '\\_'); })
            .replace(/\]\(/g, '\\](').replace(/<(?=[a-zA-Z\/!])/g, '\\<');
    }

    function mdUrl(u) { return u.replace(/ /g, '%20').replace(/\(/g, '%28').replace(/\)/g, '%29'); }

    function mdAbs(u) {
        try { return new URL(u, document.baseURI).href; } catch (e) { return u; }
    }

    function mdLang(pre) {
        var cands = [pre, pre.querySelector('code'), pre.parentElement];
        for (var i = 0; i < cands.length; i++) {
            var c = cands[i]; if (!c || !c.className || typeof c.className !== 'string') continue;
            var m = /(?:^|\s)(?:language|lang|highlight-source|highlight)-([a-z0-9#+_-]+)/i.exec(c.className);
            if (m) return m[1].toLowerCase();
        }
        return '';
    }

    function mdIsNoise(el, tagName, ctx) {
        if (ctx.full) return false;
        var role = el.getAttribute && el.getAttribute('role');
        if (role && MD_NOISE_ROLE.test(role)) return true;
        if (el.getAttribute && el.getAttribute('aria-modal') === 'true') return true;
        if (tagName === 'nav' || tagName === 'aside') return true;
        if (tagName === 'footer' || tagName === 'header') {
            // an article/section's own header or footer is content; the page's is chrome
            return !el.closest('article, main, section, [role=main]');
        }
        var id = (el.id || '') + ' ' + (typeof el.className === 'string' ? el.className : '');
        if (id.length > 3 && MD_CONSENT.test(id)) {
            var len = (el.textContent || '').length;
            if (len < 1800 && !el.querySelector('main, article')) return true;
        }
        return false;
    }

    function mdInlineWrap(mark, inner) {
        var m = /^(\s*)([\s\S]*?)(\s*)$/.exec(inner);
        if (!m[2]) return inner;
        return m[1] + mark + m[2] + mark + m[3];
    }

    // Renders the children of `node` into an array of block strings.
    function mdChildren(node, ctx, only) {
        var parts = [], buf = '';
        function flush() {
            var t = buf.replace(/[ \t\r\f\v]+/g, ' ').replace(/ *\n */g, '\n').trim();
            buf = '';
            if (!t) return;
            if (/^(#{1,6}\s|[>+-]\s|\d+[.)]\s|={3,}|-{3,}$)/.test(t)) t = '\\' + t;
            parts.push(t);
        }
        var kids = only || mdKids(node);
        for (var i = 0; i < kids.length; i++) {
            var c = kids[i];
            if (c.nodeType === 3) {
                var v = c.nodeValue;
                if (ctx.pre) buf += v; else buf += mdEscText(v.replace(/\s+/g, ' '));
                continue;
            }
            if (c.nodeType !== 1) continue;
            var tg = c.tagName.toLowerCase();
            if (MD_SKIP.test(tg)) {
                if ((tg === 'video' || tg === 'audio') && c.getAttribute('src') && !ctx.noImages) {
                    flush(); parts.push('[' + tg + '](' + mdUrl(mdAbs(c.getAttribute('src'))) + ')');
                }
                continue;
            }
            if (!mdVisible(c)) continue;
            if (mdIsNoise(c, tg, ctx)) continue;
            if (tg === 'br') { buf += '\n'; continue; }
            if (MD_BLOCK.test(tg)) {
                flush();
                var b = mdBlock(c, tg, ctx);
                if (b) parts.push(b);
                continue;
            }
            // flex/grid items and display:block spans/links/images stand on their own line
            if (c.isConnected && !ctx.pre) {
                var disp = '';
                try { disp = getComputedStyle(c).display; } catch (e) { }
                if (/^(block|flex|grid|list-item|table|flow-root)/.test(disp)) {
                    flush();
                    if (/^(a|img|code|kbd|samp|tt|strong|b|em|i|cite|del|s|strike|q)$/.test(tg)) {
                        var ai = mdInline(c, tg, ctx);
                        if (ai && ai.blocks) ai.blocks.forEach(function (x) { if (x) parts.push(x); });
                        else if (ai) parts.push(ai);
                    } else {
                        var gb = mdBlock(c, tg, ctx);
                        if (gb) parts.push(gb);
                    }
                    continue;
                }
            }
            // inline-level
            var inl = mdInline(c, tg, ctx);
            if (inl && inl.blocks) { flush(); inl.blocks.forEach(function (x) { if (x) parts.push(x); }); }
            else buf += inl || '';
        }
        flush();
        return parts;
    }

    function mdKids(node) {
        // open shadow roots are part of what the user sees; <slot> shows its assigned nodes
        if (node.shadowRoot) return [].slice.call(node.shadowRoot.childNodes);
        if (node.tagName && node.tagName.toLowerCase() === 'slot') {
            var a = node.assignedNodes ? node.assignedNodes({ flatten: true }) : [];
            return a.length ? a : [].slice.call(node.childNodes);
        }
        return [].slice.call(node.childNodes);
    }

    function mdHasBlock(el) {
        return !!el.querySelector('p,div,ul,ol,li,table,pre,blockquote,h1,h2,h3,h4,h5,h6,section,article,figure,hr');
    }

    function mdInline(el, tg, ctx) {
        if (tg === 'img') {
            if (ctx.noImages) return '';
            var alt = (el.getAttribute('alt') || '').replace(/\s+/g, ' ').trim();
            var src = el.currentSrc || el.getAttribute('src') || '';
            if (!src || /^data:/i.test(src) || /^blob:/i.test(src)) return alt && alt.length > 3 && !ctx.inLink ? mdEscText(alt) : '';
            if (!alt && el.isConnected && ((el.naturalWidth && el.naturalWidth <= 40) || (el.width && el.width <= 40))) return ''; // icons / tracking pixels
            if (el.isConnected && (el.width <= 2 || el.height <= 2)) return '';
            return '![' + alt.replace(/[\[\]]/g, '') + '](' + mdUrl(mdAbs(src)) + ')';
        }
        if (tg === 'a') {
            var href = el.getAttribute('href');
            var hasB = mdHasBlock(el);
            var inner;
            var sub = Object.assign({}, ctx, { inLink: true });
            if (hasB) {
                var bl = mdChildren(el, sub);
                var one = bl.join(' ').replace(/\s+/g, ' ').trim();
                if (!href || /^(javascript:|#)/i.test(href) || ctx.inLink || !one) return { blocks: bl };
                if (bl.length === 1 && bl[0].indexOf('\n') < 0) return { blocks: ['[' + bl[0] + '](' + mdUrl(mdAbs(href)) + ')'] };
                return { blocks: bl.concat(['[link](' + mdUrl(mdAbs(href)) + ')']) };
            }
            inner = mdChildren(el, sub).join(' ').replace(/\s+/g, ' ').trim();
            if (/^(#|¶|§|🔗|\u00b6)$/.test(inner) && ctx.heading) return ''; // heading anchor icons
            if (!inner) inner = (el.getAttribute('aria-label') || el.getAttribute('title') || '').trim();
            if (!inner) return '';
            if (!href || /^(javascript:|#)/i.test(href) || ctx.inLink) return inner;
            return '[' + inner.replace(/\]/g, '\\]') + '](' + mdUrl(mdAbs(href)) + ')';
        }
        if (mdHasBlock(el)) return { blocks: mdChildren(el, ctx) }; // span/label wrapping blocks
        var txt = mdChildren(el, ctx).join(' ');
        if (tg === 'strong' || tg === 'b') return ctx.heading ? txt : mdInlineWrap('**', txt);
        if (tg === 'em' || tg === 'i' || tg === 'cite') return mdInlineWrap('*', txt);
        if (tg === 'del' || tg === 's' || tg === 'strike') return mdInlineWrap('~~', txt);
        if (tg === 'code' || tg === 'kbd' || tg === 'samp' || tg === 'tt') {
            var raw = (el.textContent || '').replace(/\s+/g, ' ');
            if (!raw.trim()) return '';
            var fence = /`/.test(raw) ? '``' : '`';
            return fence + (/^`|`$/.test(raw) ? ' ' + raw + ' ' : raw) + fence;
        }
        if (tg === 'q') return '"' + txt + '"';
        return txt;
    }

    function mdBlock(el, tg, ctx) {
        var kids;
        if (/^h[1-6]$/.test(tg)) {
            var t = mdChildren(el, Object.assign({}, ctx, { heading: true })).join(' ').replace(/\s+/g, ' ').trim();
            return t ? new Array(+tg.charAt(1) + 1).join('#') + ' ' + t : '';
        }
        if (tg === 'p' || tg === 'summary' || tg === 'caption' || tg === 'legend') {
            var p = mdChildren(el, ctx).join('\n\n');
            if (tg === 'summary' && p) return '**' + p.replace(/\n+/g, ' ') + '**';
            if (tg === 'caption' && p) return '*' + p.replace(/\n+/g, ' ') + '*';
            return p;
        }
        if (tg === 'pre') {
            var code = (el.textContent || '').replace(/\r\n?/g, '\n').replace(/^\n+|\s+$/g, '');
            if (!code) return '';
            var fence = /```/.test(code) ? '~~~~' : '```';
            return fence + mdLang(el) + '\n' + code + '\n' + fence;
        }
        if (tg === 'hr') return '---';
        if (tg === 'blockquote') {
            var q = mdChildren(el, ctx).join('\n\n');
            return q ? q.split('\n').map(function (l) { return l ? '> ' + l : '>'; }).join('\n') : '';
        }
        if (tg === 'ul' || tg === 'ol' || tg === 'menu') return mdList(el, tg === 'ol', ctx);
        if (tg === 'li') return mdChildren(el, ctx).join('\n\n'); // stray li outside a list
        if (tg === 'table') return mdTable(el, ctx);
        if (tg === 'figcaption') {
            var fc = mdChildren(el, ctx).join(' ').replace(/\s+/g, ' ').trim();
            return fc ? '*' + fc + '*' : '';
        }
        if (tg === 'dt') {
            var d = mdChildren(el, ctx).join(' ').replace(/\s+/g, ' ').trim();
            return d ? '**' + d + '**' : '';
        }
        if (tg === 'dl') {
            var out = [];
            [].forEach.call(el.children, function (c) {
                if (!mdVisible(c)) return;
                var ct = c.tagName.toLowerCase();
                if (ct === 'dt') { var s = mdBlock(c, 'dt', ctx); if (s) out.push(s); }
                else if (ct === 'dd') { var s2 = mdChildren(c, ctx).join(' ').trim(); if (s2) out.push(s2); }
            });
            return out.join('\n');
        }
        // generic container: div, section, article, main, details, figure, ...
        kids = mdChildren(el, ctx);
        // a one-line flex row (label + value, key + shortcut) reads better joined than stacked
        if (kids.length >= 2 && kids.length <= 6 && el.isConnected) {
            try {
                var cs = getComputedStyle(el);
                if (/^flex/.test(cs.display) && /^row/.test(cs.flexDirection) &&
                    kids.every(function (k) { return k.indexOf('\n') < 0 && k.length < 120 && !/^(#|>|-\s|\d+\.\s|```|\|)/.test(k); }))
                    return kids.join(kids.length === 2 ? ' — ' : ' · ');
            } catch (e) { }
        }
        return kids.join('\n\n');
    }

    function mdList(el, ordered, ctx) {
        var items = [], n = parseInt(el.getAttribute('start'), 10);
        if (isNaN(n)) n = 1;
        [].forEach.call(el.children, function (li) {
            if (li.tagName.toLowerCase() !== 'li' || !mdVisible(li)) return;
            var task = '';
            var cb = li.querySelector(':scope > input[type=checkbox], :scope > label > input[type=checkbox], :scope > p > input[type=checkbox]');
            if (cb) task = (cb.checked ? '[x] ' : '[ ] ');
            var blocks = mdChildren(li, ctx);
            if (!blocks.length && !task) return;
            var text = '';
            blocks.forEach(function (b, i) {
                if (i === 0) { text = b; return; }
                var isList = /^(\s*)([-*+]|\d+[.)])\s/.test(b);
                text += (isList ? '\n' : '\n\n') + b;
            });
            var bullet = ordered ? (n++) + '. ' : '- ';
            var pad = new Array(bullet.length + 1).join(' ');
            var lines = (task + text).split('\n');
            items.push(bullet + lines[0] + lines.slice(1).map(function (l) { return l ? '\n' + pad + l : '\n'; }).join(''));
        });
        return items.join('\n');
    }

    function mdTable(el, ctx) {
        var rows = [];
        [].forEach.call(el.querySelectorAll('tr'), function (tr) {
            if (tr.closest('table') !== el || !mdVisible(tr)) return;
            var cells = [], isHead = false;
            [].forEach.call(tr.children, function (c) {
                var ct = c.tagName.toLowerCase();
                if ((ct !== 'td' && ct !== 'th') || !mdVisible(c)) return;
                if (ct === 'th') isHead = true;
                var t = mdChildren(c, ctx).join(' ').replace(/\s+/g, ' ').replace(/\|/g, '\\|').trim();
                cells.push(t);
                var span = parseInt(c.getAttribute('colspan'), 10);
                for (var k = 1; k < (span > 1 && span < 12 ? span : 1); k++) cells.push('');
            });
            if (cells.length) rows.push({ cells: cells, head: isHead && tr.parentElement.tagName.toLowerCase() !== 'tbody' || (isHead && !tr.querySelector('td')) });
        });
        if (!rows.length) return '';
        var cols = 0;
        rows.forEach(function (r) { if (r.cells.length > cols) cols = r.cells.length; });
        if (cols === 1) return rows.map(function (r) { return r.cells[0]; }).filter(Boolean).join('\n\n'); // layout table
        var out = [];
        rows.forEach(function (r) { while (r.cells.length < cols) r.cells.push(''); });
        var head = rows[0].cells, body = rows.slice(1);
        out.push('| ' + head.join(' | ') + ' |');
        out.push('| ' + head.map(function () { return '---'; }).join(' | ') + ' |');
        body.forEach(function (r) { out.push('| ' + r.cells.join(' | ') + ' |'); });
        return out.join('\n');
    }

    function mdMainRoot() {
        var body = document.body; if (!body) return null;
        var total = (body.innerText || '').length || 1;
        var sels = ['main', '[role=main]', 'article', '#mw-content-text', '.markdown-body', '#content', '#main', '.post-content', '.entry-content', '.article-body'];
        for (var i = 0; i < sels.length; i++) {
            var list = document.querySelectorAll(sels[i]);
            if (list.length !== 1) continue;
            var len = (list[0].innerText || '').length;
            if (len > 300 && len >= total * 0.3) return list[0];
        }
        return body;
    }

    function markdownInfo(sel, o) {
        o = o || {};
        var ctx = { full: !!o.full, noImages: o.images === false };
        var root = null, source = 'page', frag = null;
        if (o.selection) {
            var s = window.getSelection();
            if (s && s.rangeCount && !s.isCollapsed && String(s).trim()) {
                frag = document.createElement('div');
                for (var i = 0; i < s.rangeCount; i++) frag.appendChild(s.getRangeAt(i).cloneContents());
                root = frag; source = 'selection';
            } else return { md: '', title: document.title || '', url: location.href, source: 'selection', chars: 0, empty: 'Nothing is selected.' };
        } else if (sel) {
            root = resolve(sel); source = 'element';
            if (!root) return { md: '', title: document.title || '', url: location.href, source: 'element', chars: 0, empty: 'No element matches ' + sel };
            ctx.full = true;
        } else if (o.full) { root = document.body; }
        else { root = mdMainRoot(); source = root === document.body ? 'page' : 'main'; }
        if (!root) return { md: '', title: '', url: location.href, source: source, chars: 0, empty: 'This page has no content yet.' };
        var parts = root === frag ? mdChildren(frag, ctx) : mdChildren(null, ctx, [root]);
        var md = parts.join('\n\n').replace(/\n{3,}/g, '\n\n').trim();
        var title = (document.title || '').trim(), url = location.href;
        var head = '';
        if (o.meta !== false) {
            head = source === 'selection'
                ? '> Selection from [' + (title || url).replace(/[\[\]]/g, '') + '](' + mdUrl(url) + ')\n\n'
                : (title ? '# ' + title.replace(/\s+/g, ' ') + '\n\n' : '') + '> Source: ' + url + '\n\n';
        }
        var max = o.max > 0 ? o.max : 400000;
        var out = head + md, cut = 0;
        if (out.length > max) { cut = out.length - max; out = out.slice(0, max) + '\n\n…(truncated, ' + cut + ' more characters)'; }
        return { md: out, title: title, url: url, source: source, chars: out.length, truncated: cut > 0, empty: md ? '' : 'No readable text found on this page.' };
    }

    function markdown(sel, o) {
        var r = markdownInfo(sel, o);
        return r.md || ('(' + (r.empty || 'nothing to convert') + ')');
    }

    window.__brain = {
        v: 5,
        snapshot: snapshot, resolve: resolve, queryAll: queryAll, clickWatch: clickWatch, clickResult: clickResult, focusInfo: focusInfo,
        click: click, clickAt: clickAt, dblclick: dblclick, hover: hover,
        fill: fill, insertText: insertText, pressKey: pressKey,
        setChecked: setChecked, selectOption: selectOption,
        wheel: wheel, scrollTo: scrollTo, scrollPage: scrollPage,
        text: text, html: html, describe: describeSel, rect: rect,
        getCookies: getCookies, setCookie: setCookie, deleteCookie: deleteCookie,
        exists: exists, textIncludes: textIncludes, urlMatches: urlMatches,
        installConsoleHook: installConsoleHook, networkLog: networkLog,
        info: info, blurAll: blurAll,
        locator: locator, firstMatch: firstMatch, isSensitiveFocus: isSensitiveFocus,
        risk: risk, riskAt: riskAt, riskKey: riskKey,
        recStart: recStart, recStop: recStop,
        listeners: listeners, listenerSummary: listenerSummary, listenersText: listenersText, rawOn: rawOn,
        markdown: markdown, markdownInfo: markdownInfo,
        // toolbar() is attached by toolbar.js
    };

    // Navigation beacon: pagehide fires as the document unloads (link click,
    // form post, JS redirect, bfcache swap) — the earliest signal a load is
    // starting, so the host can show the progress bar before the next
    // document exists. The host-side watcher closes it out when the new
    // document is interactive, or auto-heals if the navigation aborted.
    rawOn(window, 'pagehide', function () {
        try {
            window.external && window.external.sendMessage(JSON.stringify({ b: 30 }));
        } catch (e) { }
    });

    // Earlier still: a qualifying link click or form submit means a load is
    // *probably* starting right now — pagehide only fires at commit (after
    // the server responds), so without this the bar waits out the whole TTFB.
    // Deferred a tick so e.defaultPrevented reflects the page's handlers:
    // SPA-intercepted clicks (pushState routing) don't light the bar, and if
    // the doc unloads before the timer runs, the pagehide beacon covers it.
    var navBeacon = function () {
        try {
            window.external && window.external.sendMessage(JSON.stringify({ b: 30 }));
        } catch (err) { }
    };
    var qualifiesNavTarget = function (t) {
        t = (t || '').toLowerCase();
        return !t || t === '_self' || t === '_parent' || t === '_top';
    };
    rawOn(window, 'click', function (e) {
        try {
            if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
            var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
            if (!a || a.hasAttribute('download') || !qualifiesNavTarget(a.getAttribute('target'))) return;
            var h = a.getAttribute('href') || '';
            if (!h || h.charAt(0) === '#' || /^(javascript|mailto|tel|data|blob):/i.test(h)) return;
            // Same-document fragment nav doesn't load anything.
            if (a.hash && a.href.split('#')[0] === location.href.split('#')[0]) return;
            setTimeout(function () { if (!e.defaultPrevented) navBeacon(); }, 0);
        } catch (err) { }
    }, true);
    rawOn(window, 'submit', function (e) {
        try {
            var f = e.target;
            if (!f || !qualifiesNavTarget(f.getAttribute && f.getAttribute('target'))) return;
            setTimeout(function () { if (!e.defaultPrevented) navBeacon(); }, 0);
        } catch (err) { }
    }, true);

    try { installConsoleHook(); } catch (e) { }
})();
