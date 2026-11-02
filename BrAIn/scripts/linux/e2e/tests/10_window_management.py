"""Window states and sizes through the MCP tools, and the chromeless window controls drawn by the toolbar (real mouse input)."""
from mcp import *

start_site()
c = MCP(['--no-fullscreen'])
time.sleep(4)
wid = window_id()


def geo():
    return window_geometry(wid)


def at_right(off, dy=22):
    """Screen point `off` px left of the window's right edge, in the toolbar row."""
    x, y, w, h = geo()
    return x + w - off, y + dy


CLOSE, MAXIMIZE, MINIMIZE, GAP = 23, 69, 115, 454   # button centres / the empty drag space, measured from the right edge


def click(point):
    sh(f'xdotool mousemove {point[0]} {point[1]} click 1')


def drag(start, moves):
    """A mouse drag at human speed (a window-manager move/resize loop needs the button to really be down)."""
    sh(f'xdotool mousemove {start[0]} {start[1]}'); time.sleep(0.3)
    sh('xdotool mousedown 1'); time.sleep(0.5)
    for mx, my in moves:
        sh(f'xdotool mousemove {mx} {my}'); time.sleep(0.2)
    sh('xdotool mouseup 1'); time.sleep(0.8)


check('opens at 1440x900', geo()[2:] == (1440, 900), geo()[2:])

# ---- MCP window tools
for state, want in (('maximized', 'MAXIMIZED_HORZ'), ('normal', ''), ('fullscreen', 'FULLSCREEN'), ('normal', ''), ('minimized', 'HIDDEN'), ('normal', '')):
    c.call('window_state', state=state)
    ok = wait_until(lambda: (want in wm_state(wid)) if want else not any(s in wm_state(wid) for s in ('MAXIMIZED', 'FULLSCREEN', 'HIDDEN')), 4)
    check(f'window_state {state}', ok, wm_state(wid))
check('restoring brings the original size back', wait_until(lambda: geo()[2:] == (1440, 900), 3), geo()[2:])
for w, h in ((375, 667), (820, 1180), (1100, 720), (260, 180)):
    c.call('window_resize', width=w, height=h)
    check(f'window_resize {w}x{h}', wait_until(lambda: geo()[2:] == (w, h), 4), geo()[2:])
    check(f'  the page viewport follows ({w} wide)', wait_until(lambda: c.js('innerWidth') == str(w), 3), c.js('innerWidth'))
c.call('window_resize', width=1100, height=720)
time.sleep(0.8)

# ---- move by dragging the empty toolbar space
x0, y0, w0, h0 = geo()
gx, gy = at_right(GAP)
drag((gx, gy), [(gx + 54, gy + 28), (gx + 120, gy + 68)])
x1, y1, _, _ = geo()
check('dragging the empty toolbar moves the window', (x1 - x0 >= 80 and y1 - y0 >= 40), (x1 - x0, y1 - y0))

# ---- resize from the right edge and from a corner
x, y, w, h = geo()
drag((x + w - 2, y + 400), [(x + w + 50, y + 400), (x + w + 100, y + 400)])
check('dragging the right edge widens the window', geo()[2] - w >= 70, (w, geo()[2]))
x, y, w, h = geo()
drag((x + w - 2, y + h - 2), [(x + w - 30, y + h - 30), (x + w - 60, y + h - 60)])
check('dragging a corner resizes in both directions', w - geo()[2] >= 40 and h - geo()[3] >= 40, ((w, h), geo()[2:]))

# ---- buttons and double-click
click(at_right(MAXIMIZE))
check('the maximize button maximizes', wait_until(lambda: 'MAXIMIZED_HORZ' in wm_state(wid), 3), wm_state(wid))
click(at_right(MAXIMIZE))
check('...and restores', wait_until(lambda: 'MAXIMIZED' not in wm_state(wid), 3), wm_state(wid))
sh(f'xdotool mousemove {at_right(GAP)[0]} {at_right(GAP)[1]} click --repeat 2 --delay 120 1')
check('double-clicking the empty toolbar maximizes', wait_until(lambda: 'MAXIMIZED_HORZ' in wm_state(wid), 3), wm_state(wid))
time.sleep(0.8)
sh(f'xdotool mousemove {at_right(GAP)[0]} {at_right(GAP)[1]} click --repeat 2 --delay 120 1')
check('...and double-clicking again restores', wait_until(lambda: 'MAXIMIZED' not in wm_state(wid), 3), wm_state(wid))
click(at_right(MINIMIZE))
check('the minimize button minimizes', wait_until(lambda: 'HIDDEN' in wm_state(wid), 3), wm_state(wid))
sh(f'xdotool windowmap {wid}; xdotool windowactivate {wid}'); time.sleep(0.8)

# ---- close
click(at_right(CLOSE))
t0 = time.time()
check('the close button ends the app', wait_until(lambda: c.p.poll() is not None, 8), f'{time.time() - t0:.1f}s')
finish()
