"""element.requestFullscreen(): the page takes the whole window, the toolbar steps aside and comes back painted."""
from mcp import *

start_site()
c = MCP(['--no-fullscreen'])
time.sleep(3.5)
c.call('navigate', url=SITE + '/full')
time.sleep(1)
snap = c.call('snapshot')
ref = re.search(r'\[ref=(e\d+)\] button "fs"', snap).group(1)
wid = window_id()
check('toolbar painted before', strip_stddev() > 8)

c.call('click', element=ref)
check('the window goes fullscreen', wait_until(lambda: 'FULLSCREEN' in wm_state(wid), 4), wm_state(wid))
check('the page sees the fullscreen element', wait_until(lambda: c.js('document.getElementById("out").textContent') == 'FULL', 3))
desk('06-fullscreen')

c.js('document.exitFullscreen(); 1')
check('the window leaves fullscreen', wait_until(lambda: 'FULLSCREEN' not in wm_state(wid), 4), wm_state(wid))
check('the page sees the exit', wait_until(lambda: c.js('document.getElementById("out").textContent') == 'NOT', 3))
x, y, w, h = window_geometry(wid)
check('the window is back at its normal size', (w, h) == (1440, 900), f'{w}x{h}')
check('the toolbar is painted again', wait_until(lambda: strip_stddev() > 8, 3.0), f'{strip_stddev():.1f}')
desk('06-after')
c.close()
finish()
