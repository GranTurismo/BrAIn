"""Local fixture website for the Linux end-to-end tests: forms, popups, cookies, downloads, HTTP auth, fullscreen, images..."""
import base64, http.server, socketserver, threading

PNG_5X5 = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAUAAAAFCAYAAACNbyblAAAAHElEQVQI12P4//8/w38GIAXDIBKE0DHxgljNBAAO9TXL0Y4OHwAAAABJRU5ErkJggg==')

HTML = {
    '/signin.html': """<!doctype html><title>Sign-in test</title><body style="font:18px sans-serif;margin:8px">
<h1>Shop</h1><button id=go onclick="window.open('/signin-popup.html','signin','width=480,height=420')">Continue with Test Accounts</button>
<p id=out>signed out</p><input id=after placeholder="after the popup">
<script>addEventListener('message',e=>{document.title=e.data;document.getElementById('out').textContent=e.data})</script></body>""",
    '/signin-popup.html': """<!doctype html><title>Sign in - Test Accounts</title><body style="font:18px sans-serif;margin:12px">
<h2>Choose an account</h2><input id=u autofocus placeholder="Email or phone">
<button id=ok onclick="opener.postMessage('user:'+document.getElementById('u').value,'*');window.close()">Next step</button></body>""",
    '/view.html': """<!doctype html><title>View test</title><body style="font:18px sans-serif;margin:8px;background:#fff">
<h1>Terminal view test</h1><p>A link to the <a href="/two.html" id=l>second page</a>.</p>
<button id=big style="width:320px;height:110px;font-size:28px" onclick="this.textContent='clicked '+(window.n=(window.n||0)+1)">Big button</button>
<textarea id=ta style="width:420px;height:90px;font-size:18px"></textarea></body>""",
    '/index.html': """<!doctype html><title>Site One</title><body style="font:20px sans-serif;background:#eef;margin:0">
<h1 id=h>Hello from site one</h1><button id=b onclick="document.title='clicked';this.textContent='CLICKED'">Click me</button>
<input id=i placeholder=type> <select id=s><option value=a>A</option><option value=b>B</option></select>
<label><input type=checkbox id=c> check</label> <a href="/two.html" id=l>to page two</a>
<a href="/dl" id=dl>download</a> <a href="/two.html" target=_blank id=blank>blank</a>
<button id=pop onclick="window.open('/two.html','_blank')">popup</button>
<button id=sized onclick="window.open('/two.html','p','width=420,height=300')">sized</button>
<div style="height:3000px;background:linear-gradient(#eef,#fcd)">tall</div></body>""",
    '/two.html': "<!doctype html><title>Page Two</title><body><h1>Second page</h1><script>document.title='Page Two'</script></body>",
    '/cookie': "<!doctype html><title>cookie</title><body>set</body>",
    '/full': "<!doctype html><title>full</title><body style='margin:0;background:#222'><button id=fs onclick=\"document.documentElement.requestFullscreen()\">fs</button><div id=out></div><script>document.addEventListener('fullscreenchange',()=>{document.getElementById('out').textContent=document.fullscreenElement?'FULL':'NOT'})</script></body>",
    '/dark': "<!doctype html><title>dark</title><style>body{background:#fff;color:#000}@media (prefers-color-scheme:dark){body{background:#000;color:#fff}}</style><body><h1>scheme</h1><script>document.title=matchMedia('(prefers-color-scheme: dark)').matches?'DARK':'LIGHT'</script></body>",
    '/opener.html': """<!doctype html><title>Opener</title><body>
<button id=sized onclick="window.__w=window.open('/child.html','p','width=420,height=300')">sized</button>
<button id=plain onclick="window.__w=window.open('/child.html','_blank')">plain</button>
<script>window.addEventListener('message',e=>{document.title='got:'+e.data})</script></body>""",
    '/child.html': """<!doctype html><title>Child</title><body>child<script>
try{window.opener&&window.opener.postMessage('hello-from-child','*')}catch(e){}
document.title='child opener='+(window.opener?'yes':'no');</script></body>""",
    '/img.html': """<!doctype html><title>img</title><body style="margin:0;background:#fff">
<img id=im src="/big.png" width=200 height=200 style="image-rendering:pixelated;display:block;margin:40px">
<a id=lk href="/two.html">a link</a></body>""",
    '/upload.html': """<!doctype html><title>upload</title><body style="font:20px sans-serif">
<input type=file id=f> <span id=out></span>
<script>document.getElementById('f').addEventListener('change',e=>{var f=e.target.files[0];var r=new FileReader();r.onload=()=>{document.getElementById('out').textContent=f.name+'|'+f.size+'|'+r.result;document.title='uploaded'};r.readAsText(f)})</script></body>""",
    '/logs.html': """<!doctype html><title>logs</title><body><script>console.log('hello log');console.warn('careful');console.error('boom');fetch('/two.html?x=1').then(r=>r.text());fetch('/echo-cookies');</script></body>""",
}


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _send(self, code, ctype='text/html; charset=utf-8', body=b'', extra=()):
        self.send_response(code)
        self.send_header('Content-Type', ctype)
        for k, v in extra:
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        p = self.path.split('?')[0]
        if p == '/dl':
            body = b'hello download ' * 1000
            self._send(200, 'application/octet-stream', body,
                       [('Content-Disposition', 'attachment; filename="test file.bin"'), ('Content-Length', str(len(body)))])
        elif p == '/auth':
            if self.headers.get('Authorization') == 'Basic ' + base64.b64encode(b'user:pass').decode():
                self._send(200, body=b'<title>authed</title><h1>welcome user</h1>')
            else:
                self._send(401, extra=[('WWW-Authenticate', 'Basic realm="t"')])
        elif p == '/echo-cookies':
            self._send(200, body=('<title>echo</title><pre id=c>%s</pre>' % self.headers.get('Cookie', '')).encode())
        elif p == '/cookie':
            self._send(200, body=HTML['/cookie'].encode(),
                       extra=[('Set-Cookie', 'srv=1; Path=/; HttpOnly'), ('Set-Cookie', 'vis=2; Path=/; Max-Age=3600')])
        elif p == '/big.png':
            self._send(200, 'image/png', PNG_5X5)
        elif p in HTML:
            self._send(200, body=HTML[p].encode())
        else:
            self._send(404)


def start(port=8765):
    socketserver.TCPServer.allow_reuse_address = True
    srv = socketserver.ThreadingTCPServer(('127.0.0.1', port), Handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv
