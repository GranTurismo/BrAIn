"""A small site for the DevTools tests: JSON/POST/404 endpoints, a slow one, a script, an image and a WebSocket echo (stdlib only).
The page runs its requests on load, so a reload with the network recorder on records them all.  netsite.start() -> base URL."""
import base64, hashlib, http.server, json, socketserver, struct, threading, time

PORT = 8771
BASE = f'http://127.0.0.1:{PORT}'
PNG = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==')
PAGE = f"""<!doctype html><meta charset=utf-8><title>netsite</title>
<img src="/x.png"><script src="/app.js"></script>
<script>
async function go(){{ try {{
  await fetch('/api/json').then(r=>r.json());
  await fetch('/api/post', {{method:'POST', headers:{{'Content-Type':'application/json','X-Custom':'abc'}}, body: JSON.stringify({{a:1,b:'two'}})}}).then(r=>r.text());
  await fetch('/api/404');
  await new Promise(res=>{{ var x=new XMLHttpRequest(); x.open('GET','/api/json?via=xhr'); x.setRequestHeader('X-From','xhr'); x.onloadend=res; x.send(); }});
  await new Promise(res=>{{ var ws=new WebSocket('ws://127.0.0.1:{PORT}/ws'); ws.onopen=function(){{ ws.send('hello'); ws.send('again'); }}; var n=0; ws.onmessage=function(e){{ if(++n==2) ws.close(); }}; ws.onclose=res; }});
  try {{ await fetch('http://127.0.0.1:1/unreachable'); }} catch(e) {{}}
}} catch(e) {{}} document.title='netsite done'; }}
window.addEventListener('load', go);
</script>"""


class H(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'   # libsoup (WebKitGTK) refuses a WebSocket upgrade answered with HTTP/1.0
    def log_message(self, *a): pass

    def _send(self, code, body, ct, extra=None):
        self.send_response(code); self.send_header('Content-Type', ct); self.send_header('Content-Length', str(len(body)))
        for k, v in (extra or {}).items(): self.send_header(k, v)
        self.end_headers(); self.wfile.write(body)

    def do_GET(self):
        p = self.path.split('?')[0]
        if p == '/': self._send(200, PAGE.encode(), 'text/html')
        elif p == '/app.js': self._send(200, b'window.appLoaded=1;', 'application/javascript')
        elif p == '/x.png': self._send(200, PNG, 'image/png')
        elif p == '/api/json': self._send(200, json.dumps({'hello': 'world', 'n': [1, 2, 3]}).encode(), 'application/json', {'X-Test': 'yes'})
        elif p == '/api/404': self._send(404, b'{"error":"nope"}', 'application/json')
        elif p == '/ws':
            key = self.headers['Sec-WebSocket-Key']
            acc = base64.b64encode(hashlib.sha1((key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').encode()).digest()).decode()
            self.send_response(101); self.send_header('Upgrade', 'websocket'); self.send_header('Connection', 'Upgrade')
            self.send_header('Sec-WebSocket-Accept', acc); self.end_headers(); self.close_connection = True
            f = self.rfile
            while True:
                h = f.read(2)
                if len(h) < 2: break
                op = h[0] & 15; ln = h[1] & 127
                if ln == 126: ln = struct.unpack('>H', f.read(2))[0]
                mask = f.read(4); data = bytes(b ^ mask[i % 4] for i, b in enumerate(f.read(ln)))
                if op == 8: self.wfile.write(b'\x88\x00'); break
                if op == 1: out = b'echo:' + data; self.wfile.write(bytes([0x81, len(out)]) + out)
        else: self._send(404, b'', 'text/plain')

    def do_POST(self):
        n = int(self.headers.get('Content-Length', 0)); body = self.rfile.read(n)
        self._send(201, json.dumps({'got': body.decode(), 'len': n}).encode(), 'application/json')


class T(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def start():
    srv = T(('127.0.0.1', PORT), H)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return BASE
