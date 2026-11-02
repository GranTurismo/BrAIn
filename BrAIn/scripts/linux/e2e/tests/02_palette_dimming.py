"""The command palette dims the WHOLE window (incl. the bottom rows), however the toolbar was used before it opened.

Regression: after small toolbar frames (a focused omnibox) the rows the toolbar grew into stayed unpainted, so the
palette's dimming stopped short of the bottom of the window.
"""
from mcp import *

start_site()


def fresh():
    c = MCP(['--no-fullscreen'])
    time.sleep(3.5)
    c.call('navigate', url=SITE + '/index.html')
    time.sleep(1)
    sh('xdotool mousemove 800 600 click 1')   # a real click in the page, so the page owns the keyboard
    time.sleep(0.3)
    return c


def bottom_pixel():
    x, y, w, h = window_geometry()
    return px(x + w - 140, y + h - 15)       # a page row near the bottom: light lavender when NOT dimmed


def palette_dimmed(wait=1.0):
    sh('xdotool key ctrl+k')
    time.sleep(wait)
    dim = bottom_pixel()[0] < 200
    sh('xdotool key Escape')
    time.sleep(0.8)
    return dim


def omnibox_cycle():
    sh('xdotool key ctrl+l'); time.sleep(0.5)
    sh('xdotool type --delay 50 "e"'); time.sleep(1.0)
    sh('xdotool key Escape'); time.sleep(1.5)


c = fresh(); check('palette only', palette_dimmed()); c.close()
c = fresh(); a = palette_dimmed(); b = palette_dimmed(); check('palette, then palette again', a and b, (a, b)); c.close()
c = fresh(); omnibox_cycle(); time.sleep(2.0); check('omnibox typed + Esc, wait, palette', palette_dimmed()); c.close()
c = fresh(); sh('xdotool key ctrl+l'); time.sleep(0.5); sh('xdotool key Escape'); time.sleep(1.5)
check('omnibox focused only + Esc, palette', palette_dimmed()); c.close()

x, y, w, h = (0, 0, 0, 0)
c = fresh(); x, y, w, h = window_geometry()
sh(f'xdotool mousemove {x + w - 169} {y + 22} click 1'); time.sleep(1.0)      # the three-dots menu
sh('xdotool key Escape'); time.sleep(1.0)
check('main menu opened + closed, palette', palette_dimmed()); c.close()

c = fresh(); x, y, w, h = window_geometry()
sh(f'xdotool mousemove {x + w - 355} {y + 22}'); time.sleep(1.2); sh('xdotool mousemove 800 600'); time.sleep(0.8)   # a tooltip came and went
check('tooltip shown + hidden, palette', palette_dimmed()); c.close()

# after the palette closes the page must be visible again (the dimming fades out in ~0.4 s)
c = fresh()
sh('xdotool key ctrl+k'); time.sleep(1.0); sh('xdotool key Escape')
x, y, w, h = window_geometry()
check('page is fully restored after the palette closes', wait_until(lambda: px(x + w - 140, y + h - 15)[0] > 200, 3.0))
c.close()
finish()
