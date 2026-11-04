"""The password-save prompt still works on a normal web page.

It is the one feature where a web page's own copy of BrAIn's script (it sees the form being submitted) talks to the host and the host answers
the toolbar view (the card). It sits right on the line the host-isolation hardening drew (messages from a page view must carry the per-view
secret; a page view is only pushed what it needs), so this checks the whole round trip on a login form: submit -> prompt -> Save -> the
password is in the vault -> the next visit offers it back.
"""
from mcp import *
import http.server, socketserver, threading

PAGE = b"""<!doctype html><title>login</title><form method="post" action="/in"><input id="u" name="user" autocomplete="username">
<input id="p" name="pass" type="password" autocomplete="current-password"><button id="go">Sign in</button></form>"""


class H(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    def log_message(self, *a): pass
    def _send(self, body):
        self.send_response(200); self.send_header('Content-Type', 'text/html'); self.send_header('Content-Length', str(len(body)))
        self.end_headers(); self.wfile.write(body)
    def do_GET(self): self._send(PAGE)
    def do_POST(self):
        self.rfile.read(int(self.headers.get('Content-Length', 0))); self._send(b'<title>welcome</title><p>welcome back</p>')


class S(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True; allow_reuse_address = True


threading.Thread(target=S(('127.0.0.1', 8774), H).serve_forever, daemon=True).start()
URL = 'http://127.0.0.1:8774/'

c = MCP(['--no-fullscreen'])
time.sleep(4)
c.call('navigate', url=URL); time.sleep(1.2)
c.call('type', element='#u', text='alice'); c.call('type', element='#p', text='hunter2-e2e')
c.call('click', element='#go'); time.sleep(2.5)
desk('24_prompt')


def find_save_button():
    """Screen position of the card's blue Save button (the toolbar view paints it; searched for on screen, so the test needs no layout constants)."""
    x, y, w, h = 0, 90, 1700, 200
    out = sh(f"import -window root png:- | convert png:- -crop {w}x{h}+{x}+{y} +repage txt:- | grep -E 'srgb\\((9[0-9]|8[89]),(11[0-9]|12[0-9]),2[12][0-9]\\)' | head -400")
    pts = [tuple(int(v) for v in m.groups()) for m in re.finditer(r'^(\d+),(\d+):', out, re.M)]
    return (x + sum(p[0] for p in pts) // len(pts), y + sum(p[1] for p in pts) // len(pts)) if pts else None


pos = wait_until(find_save_button, 4)
check('a save prompt appears on the web page after submitting a login form', pos is not None, 'no Save button on screen')
if pos:
    sh(f'xdotool mousemove {pos[0]} {pos[1]} click 1'); time.sleep(1.2)
    check('the card goes away after Save', find_save_button() is None)
    c.call('navigate', url='about:passwords'); time.sleep(2.0)
    page = c.js('document.body.innerText')
    check('Save put the login in the vault (about:passwords lists it)', 'alice' in page and '127.0.0.1' in page, page[:200])
    check('...without ever showing the password in the list', 'hunter2-e2e' not in page)
c.close()
finish()
