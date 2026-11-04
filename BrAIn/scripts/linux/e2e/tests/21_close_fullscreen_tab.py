"""A tab whose page is fullscreen can be closed, or left for another tab, without the toolbar staying hidden.

Regression (reported on macOS, where closing the tab of a fullscreen video left a black screen and the video playing): the page
leaves fullscreen first now, and a view that is destroyed while fullscreen no longer leaves the chrome hidden / the window fullscreen.
"""
from mcp import *

start_site()
c = MCP(['--no-fullscreen'])
time.sleep(3.5)
wid = window_id()


def fullscreen_tab():
    """A fresh tab with the fullscreen test page, made fullscreen by a real click; returns its id."""
    c.call('new_tab', url=SITE + '/full')
    time.sleep(1.2)
    snap = c.call('snapshot')
    ref = re.search(r'\[ref=(e\d+)\] button "fs"', snap).group(1)
    c.call('click', element=ref)
    ok = wait_until(lambda: 'FULLSCREEN' in wm_state(wid), 4)
    return ok, [t for t in c.tabs() if t[0]][0][1]


c.call('navigate', url=SITE + '/index.html')
time.sleep(1)
base = [t[1] for t in c.tabs()][0]
check('toolbar painted to begin with', strip_stddev() > 8)

# ---- close the tab of a fullscreen page
ok, tab = fullscreen_tab()
check('the page is fullscreen (window fullscreen)', ok, wm_state(wid))
desk('21-fullscreen')
r = c.call('close_tab', id=tab)
check('close_tab answers', not r.startswith('ERROR'), r[:60])
check('the window leaves fullscreen', wait_until(lambda: 'FULLSCREEN' not in wm_state(wid), 5), wm_state(wid))
check('the toolbar is back and painted (not hidden for good)', wait_until(lambda: strip_stddev() > 8, 4), f'{strip_stddev():.1f}')
check('the other tab is still there and works', wait_until(lambda: c.js('document.title') == 'Site One', 4))
desk('21-after-close')

# ---- switch to another tab while a page is fullscreen
ok, tab = fullscreen_tab()
check('(again) the page is fullscreen', ok, wm_state(wid))
c.call('switch_tab', id=base)
check('switching away leaves fullscreen', wait_until(lambda: 'FULLSCREEN' not in wm_state(wid), 5), wm_state(wid))
check('the toolbar is back and painted after the switch', wait_until(lambda: strip_stddev() > 8, 4), f'{strip_stddev():.1f}')
check('the tab we switched to is showing', wait_until(lambda: c.js('document.title') == 'Site One', 4))
c.call('close_tab', id=tab)
check('the app is still running', c.p.poll() is None)
c.close()
finish()
