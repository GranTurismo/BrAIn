"""Helpers for the Linux end-to-end tests (they run inside the test container, see ../run.sh).

  MCP        a tiny MCP stdio client that launches /app/BrAIn and drives it like any MCP client would
  sh / px    shell and screen-pixel helpers (xdotool, ImageMagick) for REAL X input and what is really on screen
  check()    one PASS/FAIL line per assertion; finish() exits non-zero if anything failed

Nothing here touches the machine the container runs on: the app gets its own Xvfb display, home folder and clipboard.
"""
import base64, itertools, json, os, re, subprocess, sys, threading, time

SITE = 'http://127.0.0.1:8765'
FAILS = []


# ---------------------------------------------------------------- assertions

def check(name, ok, detail=''):
    print(('PASS  ' if ok else 'FAIL  ') + name + (f'   [{detail}]' if detail != '' else ''), flush=True)
    if not ok:
        FAILS.append(name)
    return bool(ok)


def finish():
    print(f'\n{"FAILED" if FAILS else "OK"}: {len(FAILS)} failing check(s)', flush=True)
    sys.exit(1 if FAILS else 0)


def wait_until(fn, timeout=6.0, every=0.25):
    """Poll fn() until it returns something truthy (returned) or the timeout passes (returns the last value)."""
    end = time.time() + timeout
    v = fn()
    while not v and time.time() < end:
        time.sleep(every)
        v = fn()
    return v


# ---------------------------------------------------------------- MCP client

class MCP:
    def __init__(self, args=(), env=None, err='/tmp/brain.err'):
        e = dict(os.environ)
        e.update(env or {})
        self.p = subprocess.Popen(['/app/BrAIn', *args], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=open(err, 'wb'), env=e)
        self.ids = itertools.count(1)
        self.q = {}
        self.cv = threading.Condition()
        threading.Thread(target=self._read, daemon=True).start()
        self.info = self.request('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {},
                                                'clientInfo': {'name': 'linux-e2e', 'version': '1'}})
        self.notify('notifications/initialized')

    def _read(self):
        for line in self.p.stdout:
            try:
                m = json.loads(line)
            except Exception:
                continue
            if 'id' in m and ('result' in m or 'error' in m):
                with self.cv:
                    self.q[m['id']] = m
                    self.cv.notify_all()

    def _send(self, m):
        self.p.stdin.write((json.dumps(m) + '\n').encode())
        self.p.stdin.flush()

    def notify(self, method, params=None):
        self._send({'jsonrpc': '2.0', 'method': method, **({'params': params} if params else {})})

    def request(self, method, params=None, timeout=90):
        i = next(self.ids)
        self._send({'jsonrpc': '2.0', 'id': i, 'method': method, 'params': params or {}})
        end = time.time() + timeout
        with self.cv:
            while i not in self.q:
                if time.time() > end:
                    raise TimeoutError(method)
                self.cv.wait(0.2)
            m = self.q.pop(i)
        if 'error' in m:
            raise RuntimeError(json.dumps(m['error']))
        return m['result']

    def call(self, name, timeout=90, **args):
        """Call a tool (name with or without the browser_ prefix); returns its text. Errors come back as 'ERROR: ...'."""
        if not name.startswith('browser_') and not name.startswith(('flow_', 'userscript_', 'api_')):
            name = 'browser_' + name
        r = self.request('tools/call', {'name': name, 'arguments': args}, timeout)
        out = []
        for c in r.get('content', []):
            if c.get('type') == 'text':
                out.append(c['text'])
            elif c.get('type') == 'image':
                path = f"/shots/{name}-{int(time.time() * 1000) % 100000}.png"
                open(path, 'wb').write(base64.b64decode(c['data']))
                out.append(f'[image saved {path}]')
        txt = '\n'.join(out)
        return ('ERROR: ' + txt) if r.get('isError') else txt

    def js(self, script, **kw):
        return self.call('evaluate', script=script, **kw)

    def tabs(self):
        """[(is_active, id, rest-of-line)] for every tab."""
        r = []
        for l in self.call('tabs').split('\n')[1:]:
            if l.strip():
                parts = l.replace('*', ' ', 1).split(None, 1)
                r.append((l.startswith('*'), parts[0], parts[1] if len(parts) > 1 else ''))
        return r

    def close(self, wait=8):
        try:
            self.p.stdin.close()
        except Exception:
            pass
        try:
            self.p.wait(wait)
        except Exception:
            self.p.kill()


def start_site():
    """Starts the fixture web server (127.0.0.1:8765) in this process."""
    import fixtures
    return fixtures.start()


# ---------------------------------------------------------------- X11 / pixels

def sh(cmd):
    return subprocess.run(cmd, shell=True, capture_output=True, text=True).stdout.strip()


def desk(name):
    """Screenshot of the whole virtual display into /shots (kept by run.sh --keep-shots)."""
    os.system(f'import -window root /shots/{name}.png')


def window_id():
    return sh("xdotool search --onlyvisible --class BrAIn | head -1")


def window_geometry(wid=None):
    """(x, y, width, height) of the browser window in screen pixels."""
    wid = wid or window_id()
    g = sh(f'xdotool getwindowgeometry {wid}')
    p = re.search(r'Position: (-?\d+),(-?\d+)', g)
    s = re.search(r'Geometry: (\d+)x(\d+)', g)
    return int(p.group(1)), int(p.group(2)), int(s.group(1)), int(s.group(2))


def wm_state(wid=None):
    return sh(f'xprop -id {wid or window_id()} _NET_WM_STATE | cut -d= -f2').strip()


def px(x, y):
    """RGB of one screen pixel."""
    r = sh(f"import -window root png:- | convert png:- -crop 1x1+{x}+{y} +repage -depth 8 txt:- | tail -1")
    m = re.search(r'srgb\((\d+),(\d+),(\d+)', r)
    return tuple(int(v) for v in m.groups()) if m else (0, 0, 0)


def strip_stddev():
    """Standard deviation (0-255) of the toolbar strip as it is on screen. A flat band (whatever its colour — the GTK
    theme grey is 246 and looks 'bright') is ~0, a painted toolbar is ~22. Do not test brightness: it was fooled for hours."""
    x, y, w, h = window_geometry()
    r = sh(f"import -window root png:- | convert png:- -crop {w}x46+{x}+{y} +repage -format '%[fx:standard_deviation*255]' info:")
    try:
        return float(r)
    except ValueError:
        return 0.0
