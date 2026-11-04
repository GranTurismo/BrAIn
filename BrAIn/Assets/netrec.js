/* BrAIn network recorder — injected ONLY while DevTools is open: as a document-start script in every tab view, and into the page that
 * is already showing. It watches fetch, XMLHttpRequest and WebSocket (method, headers, request body, response headers and text,
 * timing, frames) and every other request through the Performance Timeline (documents, scripts, styles, images, fonts: timing,
 * size, protocol). Passive: it never changes what a page sends or receives, and every hook is wrapped so a failure here can never
 * break a page. The host reads it with window.__brainNet.drain(since). Nothing is stored outside the page. */
(function () {
    'use strict';
    if (window.__brainNet) { try { window.__brainNet.start(); } catch (e) { } return; }

    var CAP_REC = 800, CAP_BODY = 262144, CAP_POST = 65536, CAP_FRAMES = 400, CAP_FRAME = 4096;
    var doc = Math.random().toString(36).slice(2, 6);
    var T0 = performance.timeOrigin || (Date.now() - performance.now());
    var recs = [], seq = 0, ver = 0, on = true, patched = false, observed = false;

    function now() { return performance.now(); }
    function r1(n) { return Math.round(n * 10) / 10; }
    function abs(u) { try { return new URL(String(u), location.href).href; } catch (e) { return String(u); } }
    function newRec(kind, method, url) {
        var r = { id: doc + '-' + (++seq), k: kind, m: method, u: abs(url), s: r1(now()), ty: kind, st: 0, v: ++ver, bv: 0 };
        recs.push(r);
        if (recs.length > CAP_REC) recs.splice(0, recs.length - CAP_REC);
        return r;
    }
    function touch(r, withBody) { r.v = ++ver; if (withBody) r.bv = r.v; }

    function norm(h) {
        var o = {};
        try {
            if (!h) return o;
            new Headers(h).forEach(function (v, k) { o[k] = v; });
        } catch (e) { }
        return o;
    }
    function parseRaw(raw) {
        var o = {};
        String(raw || '').trim().split(/[\r\n]+/).forEach(function (l) {
            var i = l.indexOf(':'); if (i > 0) { var k = l.slice(0, i).trim().toLowerCase(); o[k] = o[k] ? o[k] + ', ' + l.slice(i + 1).trim() : l.slice(i + 1).trim(); }
        });
        return o;
    }
    function postOf(b) {
        try {
            if (b == null) return null;
            if (typeof b === 'string') return b.length > CAP_POST ? b.slice(0, CAP_POST) + '…' : b;
            if (typeof URLSearchParams !== 'undefined' && b instanceof URLSearchParams) return b.toString();
            if (typeof FormData !== 'undefined' && b instanceof FormData) {
                var parts = [];
                b.forEach(function (v, k) { parts.push(k + '=' + (typeof v === 'string' ? v : '[file ' + (v.name || '') + ', ' + (v.size || 0) + ' B]')); });
                return '[FormData] ' + parts.join('&');
            }
            if (typeof Blob !== 'undefined' && b instanceof Blob) return '[Blob ' + b.size + ' bytes' + (b.type ? ', ' + b.type : '') + ']';
            if (b instanceof ArrayBuffer || ArrayBuffer.isView(b)) return '[binary ' + b.byteLength + ' bytes]';
            return String(b);
        } catch (e) { return null; }
    }
    function isText(ct) {
        return !!ct && /^(text\/|application\/(json|xml|javascript|x-javascript|x-www-form-urlencoded|graphql)|image\/svg)|[+\/](json|xml)(;|$)/i.test(ct) && !/event-stream/i.test(ct);
    }
    var bodyBytes = 0;
    function setBody(r, text) {
        r.bt = text.length > CAP_BODY; r.body = r.bt ? text.slice(0, CAP_BODY) : text; r.bl = text.length;
        bodyBytes += r.body.length;
        if (bodyBytes > 8000000) // a memory budget for the whole page: the oldest bodies go first
            for (var i = 0; i < recs.length && bodyBytes > 5000000; i++)
                if (recs[i].body && recs[i] !== r) { bodyBytes -= recs[i].body.length; delete recs[i].body; recs[i].bx = 1; touch(recs[i]); }
        touch(r, true);
    }

    // ---- fetch
    function patchFetch() {
        var nf = window.fetch;
        if (typeof nf !== 'function' || nf.__brainNet) return;
        var w = function fetch(input, init) {
            if (!on) return nf.apply(this, arguments);
            var r = null;
            try {
                var req = (typeof Request !== 'undefined' && input instanceof Request) ? input : null;
                r = newRec('fetch', String((init && init.method) || (req && req.method) || 'GET').toUpperCase(), req ? req.url : input);
                r.rq = norm((init && init.headers) || (req && req.headers));
                if (init && init.body != null) r.pd = postOf(init.body);
            } catch (e) { }
            var p = nf.apply(this, arguments);
            if (!r) return p;
            return p.then(function (res) {
                try {
                    r.ms = r1(now() - r.s); r.st = res.status; r.stt = res.statusText; r.rs = norm(res.headers);
                    r.ct = res.headers.get('content-type') || ''; if (res.redirected) { r.red = 1; r.fu = res.url; }
                    touch(r);
                    var len = +res.headers.get('content-length') || 0;
                    if (isText(r.ct) && len <= CAP_BODY * 4)
                        res.clone().text().then(function (t) { r.ms = r1(now() - r.s); r.dn = 1; setBody(r, t); }, function () { r.dn = 1; touch(r); });
                    else { r.dn = 1; touch(r); }
                } catch (e) { }
                return res;
            }, function (err) {
                try { r.er = String((err && err.message) || err); r.ms = r1(now() - r.s); r.dn = 1; touch(r); } catch (e) { }
                throw err;
            });
        };
        w.__brainNet = true;
        try { Object.defineProperty(w, 'length', { value: nf.length }); } catch (e) { }
        window.fetch = w;
    }

    // ---- XMLHttpRequest
    function patchXhr() {
        var XP = window.XMLHttpRequest && window.XMLHttpRequest.prototype;
        if (!XP || XP.open.__brainNet || typeof WeakMap === 'undefined') return;
        var xm = new WeakMap(), oo = XP.open, os = XP.send, oh = XP.setRequestHeader;
        XP.open = function (m, u) {
            try { if (on) { var r = newRec('xhr', String(m).toUpperCase(), u); r.rq = {}; xm.set(this, r); } } catch (e) { }
            return oo.apply(this, arguments);
        };
        XP.open.__brainNet = true;
        XP.setRequestHeader = function (k, v) {
            try { var r = xm.get(this); if (r) { var n = String(k).toLowerCase(); r.rq[n] = r.rq[n] ? r.rq[n] + ', ' + v : String(v); } } catch (e) { }
            return oh.apply(this, arguments);
        };
        XP.send = function (b) {
            var x = this, r = null;
            try {
                r = xm.get(x);
                if (r && on) {
                    r.s = r1(now()); r.pd = postOf(b); touch(r);
                    x.addEventListener('loadend', function () {
                        try {
                            r.ms = r1(now() - r.s); r.st = x.status; r.stt = x.statusText; r.rs = parseRaw(x.getAllResponseHeaders());
                            r.ct = r.rs['content-type'] || ''; r.dn = 1;
                            if (!x.status && !x.responseURL) r.er = 'request failed (network error, blocked or aborted)';
                            var t = null;
                            if (isText(r.ct) || x.responseType === 'json')
                                if (x.responseType === '' || x.responseType === 'text') t = x.responseText;
                                else if (x.responseType === 'json' && x.response != null) t = JSON.stringify(x.response);
                            if (t != null) setBody(r, String(t)); else touch(r);
                        } catch (e) { }
                    });
                }
            } catch (e) { }
            return os.apply(x, arguments);
        };
    }

    // ---- WebSocket (frames both ways)
    function patchWs() {
        var OW = window.WebSocket;
        if (typeof OW !== 'function' || OW.__brainNet) return;
        var W = function WebSocket(url, protocols) {
            var ws = protocols === undefined ? new OW(url) : new OW(url, protocols);
            try { if (on) track(ws, url); } catch (e) { }
            return ws;
        };
        W.prototype = OW.prototype;
        ['CONNECTING', 'OPEN', 'CLOSING', 'CLOSED'].forEach(function (k) { W[k] = OW[k]; });
        W.__brainNet = true;
        window.WebSocket = W;
    }
    function track(ws, url) {
        var r = newRec('websocket', 'GET', url); r.fr = []; r.nf = 0; r.ty = 'websocket';
        function frame(dir, data) {
            var txt = null, size = 0;
            if (typeof data === 'string') { size = data.length; txt = size > CAP_FRAME ? data.slice(0, CAP_FRAME) + '…' : data; }
            else { size = data && (data.byteLength || data.size) || 0; txt = '[binary ' + size + ' bytes]'; }
            r.nf++; touch(r);
            if (r.fr.length < CAP_FRAMES) r.fr.push({ d: dir, t: r1(now()), n: size, x: txt, v: r.v });
        }
        var send = ws.send;
        ws.send = function (d) { try { frame('s', d); } catch (e) { } return send.apply(ws, arguments); };
        ws.addEventListener('open', function () { r.st = 101; r.stt = 'Switching Protocols'; r.ms = r1(now() - r.s); touch(r); });
        ws.addEventListener('message', function (e) { try { frame('r', e.data); } catch (x) { } });
        ws.addEventListener('error', function () { r.er = 'WebSocket error'; touch(r); });
        ws.addEventListener('close', function (e) { r.dn = 1; r.ms = r1(now() - r.s); r.cc = e.code; r.cr = e.reason || ''; touch(r); });
    }

    // ---- everything else, and timing for fetch/XHR: the Performance Timeline
    function typeOf(e) {
        var it = e.initiatorType, n = String(e.name).split('?')[0].split('#')[0].toLowerCase();
        if (e.entryType === 'navigation') return 'document';
        if (it === 'fetch') return 'fetch';
        if (it === 'xmlhttprequest') return 'xhr';
        if (it === 'iframe' || it === 'frame') return 'document';
        if (it === 'video' || it === 'audio') return 'media';
        if (/\.(woff2?|ttf|otf|eot)$/.test(n)) return 'font';
        if (it === 'img' || it === 'image' || /\.(png|jpe?g|gif|webp|avif|svg|ico|bmp)$/.test(n)) return 'image';
        if (/\.css$/.test(n)) return 'stylesheet';
        if (it === 'script' || /\.m?js$/.test(n)) return 'script';
        return 'other';
    }
    function phases(e) {
        var z = function (a, b) { return (a > 0 && b > 0 && b >= a) ? r1(b - a) : 0; };
        return {
            q: e.domainLookupStart > e.fetchStart ? z(e.fetchStart, e.domainLookupStart) : 0,
            dns: z(e.domainLookupStart, e.domainLookupEnd), con: z(e.connectStart, e.connectEnd),
            tls: e.secureConnectionStart > 0 ? z(e.secureConnectionStart, e.connectEnd) : 0,
            wait: z(e.requestStart, e.responseStart), rcv: z(e.responseStart, e.responseEnd)
        };
    }
    function onEntry(e) {
        try {
            if (!on || e.name.indexOf('data:') === 0 || e.name.indexOf('blob:') === 0) return;
            var url = abs(e.name), ty = typeOf(e), r = null;
            if (ty === 'fetch' || ty === 'xhr')
                for (var i = recs.length - 1; i >= 0 && i > recs.length - 120; i--) {
                    var c = recs[i];
                    if ((c.k === 'fetch' || c.k === 'xhr') && !c.pe && (c.u === url || c.fu === url) && Math.abs(c.s - e.startTime) < 150) { r = c; break; }
                }
            if (!r) {
                for (var j = recs.length - 1; j >= 0 && j > recs.length - 200; j--) if (recs[j].pe && recs[j].u === url && Math.abs(recs[j].s - e.startTime) < 5) return; // same entry twice
                r = newRec(ty, 'GET', url); r.s = r1(e.startTime); r.ty = ty;
            }
            r.pe = 1; r.tm = phases(e); r.ms = r1(e.duration); r.dn = 1;
            r.sz = e.transferSize || e.encodedBodySize || r.sz || 0; r.dsz = e.decodedBodySize || 0; r.pr = e.nextHopProtocol || '';
            if (!r.st && e.responseStatus) r.st = e.responseStatus;
            if (e.entryType === 'navigation') r.m = 'GET';
            touch(r);
        } catch (x) { }
    }
    function observe() {
        if (observed) return; observed = true;
        try { if (performance.setResourceTimingBufferSize) performance.setResourceTimingBufferSize(1500); } catch (e) { }
        try {
            var po = new PerformanceObserver(function (l) { l.getEntries().forEach(onEntry); });
            try { po.observe({ type: 'navigation', buffered: true }); } catch (e) { }
            po.observe({ type: 'resource', buffered: true });
        } catch (e) {
            try { performance.getEntriesByType('navigation').concat(performance.getEntriesByType('resource')).forEach(onEntry); } catch (x) { }
        }
    }

    function pack(r, since) {
        var c = {};
        for (var k in r) { if (k === 'fr' || (k === 'body' && r.bv <= since)) continue; c[k] = r[k]; }
        if (r.fr) c.fr = r.fr.filter(function (f) { return f.v > since; });
        return c;
    }
    window.__brainNet = {
        v: 1, d: doc,
        start: function () { on = true; if (!patched) { patched = true; patchFetch(); patchXhr(); patchWs(); } observe(); },
        stop: function () { on = false; },
        clear: function () { recs.length = 0; bodyBytes = 0; },
        // records changed since version `since`; bodies and frames only when they are new
        drain: function (since) {
            since = since | 0; var out = [];
            for (var i = 0; i < recs.length; i++) if (recs[i].v > since) out.push(pack(recs[i], since));
            return { o: T0, d: doc, v: ver, r: out, n: recs.length };
        }
    };
    window.__brainNet.start();
})();
