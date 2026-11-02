"""The app starts, the toolbar is really on screen, MCP answers, a page loads."""
from mcp import *

start_site()
c = MCP(['--no-fullscreen'])
time.sleep(4.5)

tools = c.request('tools/list')['tools']
check('MCP lists its tools', len(tools) >= 60, f'{len(tools)} tools')

x, y, w, h = window_geometry()
check('window opens at 1440x900', (w, h) == (1440, 900), f'{w}x{h}')
sd = strip_stddev()
# regression: WebKit's accelerated presentation once left the toolbar strip an empty flat band
check('toolbar strip is painted (not a flat band)', sd > 8, f'standard deviation {sd:.1f}')

c.call('navigate', url=SITE + '/index.html')
check('page loads', wait_until(lambda: c.js('document.title') == 'Site One'))
check('toolbar still painted after navigation', strip_stddev() > 8)
r = c.call('click', element='e99999')
check('a bad element reference gets an answer and the app stays up', bool(r) and c.js('document.title') == 'Site One', r[:70])
c.close()
finish()
