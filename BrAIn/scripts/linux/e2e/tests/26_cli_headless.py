"""The `brain` command line on a machine with no display: BrAIn starts on an invisible screen of its own, the commands drive it, and `brain view` mirrors it into a terminal with working mouse, keyboard, clipboard and text view"""
import io, json, os, stat, subprocess

import cairo

from mcp import *
from vt import FakeTerm

BRAIN = '/app/BrAIn'
STORE = os.path.expanduser('~/.local/share/BrAIn')
ENV = {k: v for k, v in os.environ.items() if k not in ('DISPLAY', 'WAYLAND_DISPLAY')}   # a server: no display at all


def brain(*args, timeout=60):
    r = subprocess.run([BRAIN, *args], capture_output=True, text=True, env=ENV, timeout=timeout)
    return r.returncode, (r.stdout + r.stderr).rstrip()


def ev(js):
    rc, out = brain('eval', js)
    try:
        return json.loads(out)
    except Exception:
        return out


def info():
    rc, out = brain('status')
    m = re.search(r'invisible screen (:\d+)', out)
    return m.group(1) if m else None


start_site()

# ------------------------------------------------------------------ the browser in the background, no display
rc, out = brain('start')
check('brain start works without any display', rc == 0, out[-120:])
display = info()
check('it runs headless, on an invisible screen of its own', display is not None and display != ':99', display)
check('Xvfb runs for it', 'Xvfb' in sh('ps -eo args'), sh('ps -eo args | grep -c Xvfb'))
sock = os.path.join(STORE, 'brain.sock')
check('the control socket is owner-only (0600)', os.path.exists(sock) and stat.S_IMODE(os.stat(sock).st_mode) == 0o600)
check('the headless folder is owner-only (0700)', stat.S_IMODE(os.stat(os.path.join(STORE, 'headless')).st_mode) == 0o700)
xauth = os.path.join(STORE, 'headless', 'Xauthority')
check('the display cookie is owner-only (0600)', stat.S_IMODE(os.stat(xauth).st_mode) == 0o600)
no_cookie = subprocess.run(['xdpyinfo', '-display', display], capture_output=True, env={**ENV, 'XAUTHORITY': '/nonexistent'})
with_cookie = subprocess.run(['xdpyinfo', '-display', display], capture_output=True, env={**ENV, 'XAUTHORITY': xauth})
check('the invisible screen refuses a client without the cookie', no_cookie.returncode != 0)
check('…and lets the cookie holder in', with_cookie.returncode == 0)
rc, out = brain('start')
check('a second start finds the running one', rc == 0 and 'already running' in out, out)

# ------------------------------------------------------------------ commands
def daemon_errors():
    try:
        lines = open(os.path.join(STORE, 'brain-daemon.log'), encoding='utf-8', errors='replace').read().splitlines()
    except OSError:
        return ''
    return ' | '.join(l.strip() for l in lines if ('Exception' in l or 'fail:' in l or 'error' in l.lower()) and not l.strip().startswith('at '))[-1500:]


rc, out = brain('open', SITE + '/view.html')
check('open navigates', rc == 0 and 'View test' in out, out + ' ' + ('' if rc == 0 else daemon_errors()))
rc, out = brain('read')
check('read gives the page as Markdown with links', '# Terminal view test' in out and '[second page](' in out, out[:200])
rc, out = brain('snap')
check('snap numbers the interactive elements', re.search(r'\[ref=e\d+\] button "Big button"', out) is not None, out[:300])
big = re.search(r'\[ref=e(\d+)\] button "Big button"', out).group(1)
rc, out = brain('click', big)
check('click <number> clicks the element from snap', rc == 0 and ev("document.getElementById('big').textContent") == 'clicked 1', out)
rc, out = brain('click', 'clicked 1')
check('click "<visible text>" works too', ev("document.getElementById('big').textContent") == 'clicked 2', out)
rc, out = brain('type', '#ta', 'typed by the cli')
check('type fills a field', ev("document.getElementById('ta').value") == 'typed by the cli', out)
rc, out = brain('click', 'no such thing on the page')
check('a click on something missing fails with exit status 1', rc == 1, out)
rc, out = brain('wait', 'never appears', '--timeout', '1')
check('wait times out with exit status 1', rc == 1, out)
rc, out = brain('wait', 'Terminal view', '--timeout', '3')
check('wait finds text', rc == 0, out)
rc, out = brain('shot', '/tmp/shot.png')
check('shot saves a PNG', rc == 0 and open('/tmp/shot.png', 'rb').read(8) == b'\x89PNG\r\n\x1a\n', out)
r = subprocess.run([BRAIN, 'shot'], capture_output=True, env=ENV)
check('shot into a pipe writes the PNG itself', r.stdout[:8] == b'\x89PNG\r\n\x1a\n', len(r.stdout))
rc, out = brain('new', SITE + '/two.html')
rc, out = brain('tabs')
check('tabs lists both tabs numbered, the new one active', re.search(r'^\*\s+2\s+Page Two', out, re.M) is not None, out)
brain('tab', '1')
rc, out = brain('tabs')
check('tab 1 switches back', re.search(r'^\*\s+1\s+View test', out, re.M) is not None, out)
brain('close', '2')
rc, out = brain('tabs')
check('close 2 closes the second tab', 'Page Two' not in out, out)
rc, out = brain('call', 'browser_zoom', 'percent=100')
check('call runs any tool with typed key=value arguments', rc == 0 and 'zoom 100%' in out, out)

# brain mcp: an MCP client sharing the running browser
p = subprocess.Popen([BRAIN, 'mcp'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, env=ENV)
p.stdin.write((json.dumps({'jsonrpc': '2.0', 'id': 1, 'method': 'initialize', 'params': {'protocolVersion': '2025-06-18', 'capabilities': {}, 'clientInfo': {'name': 't', 'version': '1'}}}) + '\n').encode())
p.stdin.write((json.dumps({'jsonrpc': '2.0', 'method': 'notifications/initialized'}) + '\n').encode())
p.stdin.write((json.dumps({'jsonrpc': '2.0', 'id': 2, 'method': 'tools/call', 'params': {'name': 'browser_state', 'arguments': {}}}) + '\n').encode())
p.stdin.flush()
answers = [json.loads(p.stdout.readline()) for _ in range(2)]
p.stdin.close()
p.wait(5)
state = answers[1].get('result', {}).get('content', [{}])[0].get('text', '')
check('brain mcp speaks MCP and drives the same browser', 'view.html' in state, state[:100])

# ------------------------------------------------------------------ brain view in a kitty-like terminal
def screen(w, h):
    png = subprocess.run(['import', '-display', display, '-window', 'root', '-crop', f'{w}x{h}+0+0', 'png:-'],
                         capture_output=True, env={**ENV, 'XAUTHORITY': xauth}).stdout
    return cairo.ImageSurface.create_from_png(io.BytesIO(png))


def differing(a, b):
    """Fraction of pixels that differ clearly between two same-size surfaces."""
    a = getattr(a, 'surface', a)
    if a.get_width() != b.get_width() or a.get_height() != b.get_height():
        return 1.0
    da, db = a.get_data(), b.get_data()
    sa, sb = a.get_stride(), b.get_stride()
    bad = total = 0
    for y in range(0, a.get_height(), 3):
        for x in range(0, a.get_width(), 3):
            i, j = y * sa + x * 4, y * sb + x * 4
            total += 1
            if max(abs(da[i] - db[j]), abs(da[i + 1] - db[j + 1]), abs(da[i + 2] - db[j + 2])) > 40:
                bad += 1
    return bad / max(1, total)


def cell_of(t, comp, el):
    r = ev(f"(r=>({{x:r.x,y:r.y,w:r.width,h:r.height}}))(document.querySelector({json.dumps(el)}).getBoundingClientRect())")
    x, y = r['x'] + r['w'] / 2, 46 + r['y'] + r['h'] / 2      # the page starts under the 46 px toolbar
    return int(x // comp.K) + 1, int(y // comp.M) + 1


t = FakeTerm([BRAIN, 'view'], kind='kitty', cols=160, rows=45, cell=(8, 18), env=ENV)
check('view draws a first picture (kitty graphics)', t.wait_for(lambda: t.images, 20), t.text()[-300:])
time.sleep(1.5)
comp = t.composite()
check('cells map to whole pixel blocks (K=9, M=20 for 8x18 cells and a 1440 px page)', (comp.K, comp.M) == (9, 20), (comp.K, comp.M))
rc, out = brain('url')
check('the window was sized to the terminal (1440x880)', 'Window: 1440x880' in out, out)
time.sleep(1)
comp = t.composite()
diff = differing(comp, screen(comp.get_width(), comp.get_height()))
check('what the terminal shows is what is on the screen', diff < 0.02, f'{diff:.3%} differ')
comp.write_to_png('/shots/26_view_kitty.png')

cx, cy = cell_of(t, comp, '#big')
time.sleep(5)   # the toolbar's "agent at work" sweep from the eval calls above has finished (it runs ~3 s)
n0 = len(t.order)
time.sleep(2.5)
idle = [(t.images[i]['col'], t.images[i]['row'], t.images[i]['c'], t.images[i]['r']) for i in t.order[n0:] if i in t.images]
# (the text field focused by `brain type` above keeps its caret blinking: that is a real change, and it costs one 1x2-cell picture)
check('a quiet screen sends nothing but tiny updates (the blinking caret)', all(c * r <= 4 for _, _, c, r in idle), f'{len(idle)} pictures while idle: {idle}')
before = len(t.order)
t.click(cx, cy)
check('a mouse click in the terminal clicks the page (real X input)',
      wait_until(lambda: ev("document.getElementById('big').textContent") == 'clicked 3', 5), ev("document.getElementById('big').textContent"))
check('the change arrives as a small partial picture',
      t.wait_for(lambda: len(t.order) > before, 5) and any(t.images[i]['c'] < 160 for i in t.order[before:] if i in t.images))
time.sleep(0.5)
new = [t.images[i] for i in t.order[before:] if i in t.images]
over = [p for p in new if p['col'] <= cx < p['col'] + p['c'] and p['row'] <= cy < p['row'] + p['r']]
check('…a picture of just the button area is sent', any(p['c'] * p['r'] < 160 * 44 * 0.1 for p in over),
      [(p['col'], p['row'], p['c'], p['r']) for p in new][:8])

ex, ey = cell_of(t, comp, '#ta')
t.click(ex, ey)
time.sleep(0.3)
text = 'hello ქართული é 😀'
t.send(text)
check('typing reaches the focused field, Georgian, accents and emoji included',
      wait_until(lambda: ev("document.getElementById('ta').value") == 'typed by the cli' + text, 8), ev("document.getElementById('ta').value"))
t.key_ctrl('a')
time.sleep(0.2)
t.key_ctrl('c')
check('Ctrl+C in the browser copies to the terminal\'s clipboard (OSC 52)',
      t.wait_for(lambda: any(text in s for s in t.osc52), 5), t.osc52[-1:] if t.osc52 else 'none')
t.paste('PASTED')
check('a paste in the terminal types into the browser', wait_until(lambda: ev("document.getElementById('ta').value") == 'PASTED', 6),
      ev("document.getElementById('ta').value"))

# BrAIn's own keyboard shortcuts and toolbar, through the mirror
t.key_ctrl('l')
time.sleep(0.5)
t.send(SITE + '/index.html\r')
check('Ctrl+L + typing an address + Enter navigates (the real toolbar)',
      wait_until(lambda: 'Site One' in brain('url')[1], 8), brain('url')[1][:80])
time.sleep(1)
comp = t.composite()
t.wheel(80, 25, down=True, n=6)
check('the mouse wheel scrolls the page', wait_until(lambda: (ev('scrollY') or 0) > 100, 5), ev('scrollY'))
t.key_ctrl('t')
check('Ctrl+T opens a tab', wait_until(lambda: len(re.findall(r'^[ *]\s+\d', brain('tabs')[1], re.M)) == 2, 5), brain('tabs')[1])
t.key_ctrl('w')
check('Ctrl+W closes it', wait_until(lambda: len(re.findall(r'^[ *]\s+\d', brain('tabs')[1], re.M)) == 1, 5), brain('tabs')[1])
t.send('\x1b')   # Escape: the address bar keeps its suggestions open after the new tab closed
time.sleep(0.6)

# text view
brain('open', SITE + '/view.html')
time.sleep(0.8)
mark = t.mark()
t.key_ctrl('o')
check('Ctrl+O shows the page as text', t.wait_for(lambda: 'Terminal view test' in t.since(mark).decode('utf-8', 'replace'), 6))
check('…with numbered links', '[1]' in t.since(mark).decode('utf-8', 'replace'))
t.send('1\r')
check('typing a link number + Enter follows it', wait_until(lambda: 'two.html' in brain('url')[1], 8), brain('url')[1][:80])
before = len(t.order)
t.key_ctrl('o')
check('Ctrl+O goes back to the picture', t.wait_for(lambda: len(t.order) > before, 6))

# a resized terminal resizes the window
t.resize(120, 40)
check('a resized terminal gets a new picture of the new size',
      t.wait_for(lambda: any(p['c'] == 120 and p['r'] == 39 for p in list(t.images.values())), 8))
check('…and the window follows (1440x1053)', wait_until(lambda: 'Window: 1440x1053' in brain('url')[1], 5), brain('url')[1])

t.key_ctrl('q')
check('Ctrl+Q leaves the view', t.wait_for(lambda: t.p.poll() is not None, 5), t.p.poll())
check('…restoring the terminal (main screen, cursor, mouse off)', t.text().endswith('\x1b[?1049l'), repr(t.text()[-60:]))
check('…and the browser keeps running', brain('status')[0] == 0)
t.close()

# ------------------------------------------------------------------ other terminals
t = FakeTerm([BRAIN, 'view'], kind='iterm', cols=100, rows=30, cell=(10, 20), env=ENV)
check('iTerm2: pictures as inline images', t.wait_for(lambda: t.iterm, 20))
time.sleep(1)
comp = t.composite()
diff = differing(comp, screen(comp.get_width(), comp.get_height()))
check('iTerm2: the picture matches the screen', diff < 0.03, f'{diff:.3%}')
t.key_ctrl('q'); t.wait_for(lambda: t.p.poll() is not None, 5); t.close()

t = FakeTerm([BRAIN, 'view'], kind='sixel', cols=100, rows=30, cell=(10, 20), env=ENV)
check('sixel terminal: sixel pictures', t.wait_for(lambda: t.sixels, 20))
check('sixel: drawn at the terminal\'s pixel size (1000x580)', t.sixels and t.sixels[0][2].startswith(b'"1;1;1000;580'), t.sixels[0][2][:20] if t.sixels else '')
t.key_ctrl('q'); t.wait_for(lambda: t.p.poll() is not None, 5); t.close()

# a terminal without pictures: TEXT MODE — the page's text as real characters over coloured blocks
brain('open', SITE + '/view.html')
t = FakeTerm([BRAIN, 'view'], kind='plain', cols=100, rows=30, cell=(10, 20), env=ENV)
plain_text = lambda b: re.sub(r'\x1b\[[0-9;?]*[a-zA-Z]', '', b if isinstance(b, str) else b.decode('utf-8', 'replace'))
check('a plain terminal gets the page\'s text as real characters', t.wait_for(lambda: any('Terminal view test' in l for l in t.screen()), 20),
      re.findall(r'[A-Za-z][A-Za-z :·^,.]{14,}', re.sub(r'\x1b\[[0-9;?]*[a-zA-Z]', '', t.text()))[:6])
check('…the toolbar\'s address too', t.wait_for(lambda: '127.0.0.1' in t.screen()[0] + t.screen()[1], 5), (t.screen()[0] + '|' + t.screen()[1]).replace('▀', ' ').strip()[:160])
check('…over coloured blocks for everything else', t.text().count('▀') > 100)
check('…with the page laid out on a character grid while it is watched', ev("!!document.getElementById('__brain_grid')") is True)
# K = round(1280/100) = 13, M = round(13 * 20/10) = 26: a cell is 13x26 page pixels
r = ev("(r=>({x:r.x,y:r.y,w:r.width,h:r.height}))(document.getElementById('ta').getBoundingClientRect())")
tx, ty = int((r['x'] + 20) // 13) + 1, int((46 + r['y'] + r['h'] / 2) // 26) + 1
mark = t.mark()
t.click(tx, ty)
check('clicking a field shows the terminal cursor at its caret', t.wait_for(lambda: b'\x1b[?25h' in t.since(mark), 5))
brain('eval', "document.getElementById('ta').value=''")
t.send('typed in text mode')
check('…and typing goes into it', wait_until(lambda: ev("document.getElementById('ta').value") == 'typed in text mode', 6), ev("document.getElementById('ta').value"))
check('…and shows up on the screen', t.wait_for(lambda: any('typed in text mode' in l for l in t.screen()), 5), [l.replace('▀', ' ').strip() for l in t.screen() if l.strip(' ▀')][:8])
mark = t.mark()
brain('eval', "document.body.style.background='#c00'")
check('…and changes are sent as cells', t.wait_for(lambda: '48;2;' in t.since(mark).decode('utf-8', 'replace') or '48;5;' in t.since(mark).decode('utf-8', 'replace'), 5))
t.key_ctrl('q'); t.wait_for(lambda: t.p.poll() is not None, 5); t.close()
check('leaving the view gives the page its own fonts back', ev("!!document.getElementById('__brain_grid')") is False)

# a sign-in popup (window.open with a size: "Continue with Google" and the like) in text mode
def find(term, text):
    for y, line in enumerate(term.screen()):
        x = line.find(text)
        if x >= 0:
            return x + 1, y + 1
    return None

brain('open', SITE + '/signin.html')
t = FakeTerm([BRAIN, 'view'], kind='plain', cols=100, rows=30, cell=(10, 20), env=ENV)
at = t.wait_for(lambda: find(t, 'Continue with Test'), 20)
check('popup test: the page is there', at is not None, [l.replace('▀', ' ').strip() for l in t.screen() if l.strip(' ▀')][:5])
if at:
    t.click(at[0] + 2, at[1])
    check('a sign-in popup opens inside the view, with its page as text', t.wait_for(lambda: find(t, 'Choose an account'), 10),
          [l.replace('▀', ' ').strip() for l in t.screen() if l.strip(' ▀')][:12])
    check('…with its title bar (title and ✕)', t.wait_for(lambda: find(t, 'Sign in - Test Accounts') and find(t, '✕'), 5))
    geo = sh(f"XAUTHORITY={xauth} xwininfo -display {display} -name 'Sign in - Test Accounts' 2>/dev/null | grep -E 'Absolute|Width|Height' | awk '{{print $NF}}' | tr '\\n' ' '")
    nums = [int(v) for v in geo.split()] if geo.strip() else []
    check('…placed within the browser window (what the view shows)', len(nums) == 4 and nums[0] + nums[2] <= 1300 and nums[1] + nums[3] <= 754, geo)
    time.sleep(0.5)
    t.send('alice')
    check('typing goes into the popup (it has the keyboard)', t.wait_for(lambda: find(t, 'alice'), 6),
          [l.replace('▀', ' ').strip() for l in t.screen() if l.strip(' ▀')][:12])
    nx = t.wait_for(lambda: find(t, 'Next step'), 5)
    if nx:
        t.click(nx[0] + 2, nx[1])
    check('…its button works and the opener gets the answer (window.opener)', wait_until(lambda: 'user:alice' in brain('url')[1], 8), brain('url')[1][:80])
    check('…and the popup is gone from the view', t.wait_for(lambda: not find(t, 'Choose an account'), 6))
    af = t.wait_for(lambda: find(t, 'after the popup'), 5)
    if af:
        t.click(af[0] + 2, af[1])
        time.sleep(0.4)
        t.send('back')
    check('the browser window has the keyboard again afterwards', wait_until(lambda: ev("document.getElementById('after').value") == 'back', 6),
          ev("document.getElementById('after').value"))
t.key_ctrl('q'); t.wait_for(lambda: t.p.poll() is not None, 5); t.close()

t = FakeTerm([BRAIN, 'view', '--gfx', 'blocks'], kind='plain', cols=100, rows=30, cell=(10, 20), env=ENV)
check('--gfx blocks: coloured half blocks only', t.wait_for(lambda: t.text().count('▀') >= 100 * 29, 20), t.text().count('▀'))
t.key_ctrl('q'); t.wait_for(lambda: t.p.poll() is not None, 5); t.close()

# ------------------------------------------------------------------ lifetime
rc, out = brain('stop')
check('brain stop closes it', rc == 0 and 'stopped' in out, out)
check('…and nothing is left behind (Xvfb, the private bus, the socket)',
      wait_until(lambda: '-displayfd' not in sh('ps -eo args') and 'dbus-daemon --session --nofork' not in sh('ps -eo args'), 5) and not os.path.exists(sock),
      sh('ps -eo args | grep -E "Xvfb|dbus|BrAIn" | grep -v grep'))

# kill / systemctl stop / shutdown (SIGTERM) closes it properly too
brain('start')
pid = int(re.search(r'pid (\d+)', brain('status')[1]).group(1))
os.kill(pid, 15)
check('SIGTERM closes the background browser', wait_until(lambda: not os.path.exists(f'/proc/{pid}') or 'zombie' in sh(f'ps -o stat= -p {pid}').lower() or sh(f'ps -o stat= -p {pid}').startswith('Z'), 8))
check('…and cleans up after it (Xvfb, socket)', wait_until(lambda: '-displayfd' not in sh('ps -eo args') and not os.path.exists(sock), 5),
      sh('ps -eo args | grep -E "Xvfb|BrAIn" | grep -v grep'))

# started from a terminal session that then ends (an SSH logout): the browser stays
t = FakeTerm(['bash', '-c', f'{BRAIN} start; sleep 0.3'], kind='plain', env=ENV)
t.wait_for(lambda: t.p.poll() is not None, 30)
t.close()
time.sleep(1.5)
check('a browser started from a terminal survives the terminal closing', brain('status')[0] == 0 and 'headless' in brain('status')[1], brain('status')[1])
brain('stop')

# a BrAIn with a real window (an MCP client on a desktop) also answers the command, but has nothing to mirror
c = MCP(['--no-fullscreen'])
time.sleep(3)
rc, out = brain('status')
check('a desktop BrAIn (started by an MCP client) answers the brain command', rc == 0 and 'with a window' in out, out)
t = FakeTerm([BRAIN, 'view'], kind='plain', env=ENV)
t.wait_for(lambda: t.p.poll() is not None, 15)
check('brain view explains that a desktop BrAIn has nothing to mirror', t.p.poll() == 1 and 'window on a desktop' in t.text(), t.text()[-200:])
t.close()
c.close()
finish()
