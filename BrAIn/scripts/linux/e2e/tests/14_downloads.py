"""Downloads: a link that downloads, the file in ~/Downloads, the downloads panel and the MCP downloads tool."""
from mcp import *

start_site()
c = MCP(['--no-fullscreen'])
time.sleep(3.5)
c.call('navigate', url=SITE + '/index.html')
time.sleep(1.2)
snap = c.call('snapshot')
c.call('click', element=re.search(r'\[ref=(e\d+)\][^\n]*"download"', snap).group(1))

path = os.path.expanduser('~/Downloads/test file.bin')
check('the file arrives in the Downloads folder', wait_until(lambda: os.path.exists(path) and os.path.getsize(path) == 15000, 6),
      os.path.getsize(path) if os.path.exists(path) else 'missing')
check('the downloads tool lists it as done', wait_until(lambda: 'done' in c.call('downloads') and 'test file.bin' in c.call('downloads'), 4), c.call('downloads')[:80])

sh('xdotool key ctrl+j')
time.sleep(1.2)
desk('14-downloads-panel')
x, y, w, h = window_geometry()
# the panel is a popover hanging below the toolbar at the right: it has content, so its area is not a flat colour
r = sh(f"import -window root png:- | convert png:- -crop 300x90+{x + w - 330}+{y + 56} +repage -format '%[fx:standard_deviation*255]' info:")
check('the downloads panel opens under the toolbar', float(r or 0) > 8, r)
sh('xdotool key Escape'); time.sleep(0.8)
check('the toolbar is still painted', strip_stddev() > 8)
c.close()
finish()
