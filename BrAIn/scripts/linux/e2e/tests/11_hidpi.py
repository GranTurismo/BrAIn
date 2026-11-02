# docker: -e GDK_SCALE=2 -e RES=3200x2000x24
"""A scaled (HiDPI) display. Regression: Photino gives windows a maximum size of INT_MAX and GDK multiplies size hints
by the scale factor, which overflowed to a negative maximum and squeezed the window to 1 pixel wide."""
from mcp import *

start_site()
c = MCP(['--no-fullscreen'])
time.sleep(4.5)
c.call('navigate', url=SITE + '/index.html')
time.sleep(1.5)

x, y, w, h = window_geometry()
check('the window opens at 1440x900 LOGICAL pixels (2880x1800 on screen)', (w, h) == (2880, 1800), f'{w}x{h}')
check('devicePixelRatio is 2', c.js('devicePixelRatio') == '2', c.js('devicePixelRatio'))
check('the viewport is 1440 CSS pixels wide', c.js('innerWidth') == '1440', c.js('innerWidth'))
check('the toolbar is painted', strip_stddev() > 8)

# a real click, in screen pixels, lands on the right element
bx = c.js("(function(){var r=document.getElementById('b').getBoundingClientRect();return Math.round(r.left+r.width/2)+','+Math.round(r.top+r.height/2)})()")
cx, cy = [int(v) for v in bx.split(',')]
sh(f'xdotool mousemove {x + cx * 2} {y + (cy + 46) * 2} click 1')
check('a real click on a scaled display hits the button', wait_until(lambda: c.js('document.title') == 'clicked', 3), c.js('document.title'))

# screenshots come back at the screen resolution
r = c.call('screenshot')
m = re.search(r'\[image saved (\S+)\]', r)
dims = sh(f'identify -format "%wx%h" {m.group(1)}') if m else ''
check('the screenshot tool returns a native-resolution image', dims.startswith('2880x'), dims)
desk('11-hidpi')
c.close()
finish()
