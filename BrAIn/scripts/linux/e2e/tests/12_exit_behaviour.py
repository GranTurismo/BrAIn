"""The process ends when it should, promptly: window closed with an MCP client attached, client disconnecting,
and a plain desktop launch (no client).

Regression: with a client attached the MCP host's stop could block for seconds or for good (its stdin reader cannot be
cancelled), leaving a windowless process behind.
"""
from mcp import *

start_site()


def click_close():
    x, y, w, h = window_geometry()
    sh(f'xdotool mousemove {x + w - 24} {y + 22} click 1')


for navigated in (True, False):
    c = MCP(['--no-fullscreen'])
    time.sleep(4)
    if navigated:
        c.call('navigate', url=SITE + '/two.html')
        time.sleep(1)
    t0 = time.time()
    click_close()
    ok = wait_until(lambda: c.p.poll() is not None, 6)
    check(f'window closed with an MCP client attached (tool used: {navigated}): the process ends within 3 s',
          ok and time.time() - t0 < 3.0, f'{time.time() - t0:.1f}s, exit code {c.p.poll()}')
    if c.p.poll() is None:
        c.p.kill()
    os.system('pkill -f WebKit 2>/dev/null; true')
    time.sleep(0.5)

for navigated in (True, False):
    c = MCP(['--no-fullscreen'])
    time.sleep(4)
    if navigated:
        c.call('navigate', url=SITE + '/two.html')
        time.sleep(1)
    t0 = time.time()
    c.p.stdin.close()
    ok = wait_until(lambda: c.p.poll() is not None, 6)
    check(f'the client disconnecting ends the app (tool used: {navigated})', ok and time.time() - t0 < 3.0, f'{time.time() - t0:.1f}s')
    if c.p.poll() is None:
        c.p.kill()
    os.system('pkill -f WebKit 2>/dev/null; true')
    time.sleep(0.5)

# a desktop launch has no client at all: stdin is /dev/null and the window decides
p = subprocess.Popen(['/app/BrAIn', '--no-fullscreen'], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=open('/tmp/brain.err', 'wb'))
time.sleep(5)
check('a desktop launch (no client) keeps running', p.poll() is None)
t0 = time.time()
click_close()
ok = wait_until(lambda: p.poll() is not None, 6)
check('...and ends with the window, exit code 0', ok and p.poll() == 0, f'{time.time() - t0:.1f}s, code {p.poll()}')
if p.poll() is None:
    p.kill()
finish()
