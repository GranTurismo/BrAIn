"""prefers-color-scheme: pages default to LIGHT whatever the desktop theme, browser_emulate can force dark per tab.

Regression: the emulated scheme was applied to a global toolkit setting and lost the next time the tab was shown.
"""
from mcp import *

start_site()
c = MCP(['--no-fullscreen'])
time.sleep(4)


def dark():
    return c.js('matchMedia("(prefers-color-scheme: dark)").matches')


c.call('navigate', url=SITE + '/dark')
time.sleep(1.2)
first = c.tabs()[0][1]
check('a page defaults to light', c.js('document.title') == 'LIGHT' and dark() == 'false', c.js('document.title'))

c.call('emulate', colorScheme='dark')
time.sleep(0.6)
check('emulate dark: the tab reports dark', dark() == 'true')
c.call('navigate', url=SITE + '/dark')
time.sleep(1.0)
check('...and a page that loads then picks its dark style', c.js('document.title') == 'DARK', c.js('document.title'))

c.call('new_tab', url=SITE + '/dark')
time.sleep(1.5)
second = [i for a, i, r in c.tabs() if a][0]
check('a second tab is not affected', dark() == 'false')
c.call('switch_tab', id=first)
check('switching back: the first tab is still dark', wait_until(lambda: dark() == 'true', 3), dark())
c.call('switch_tab', id=second)
check('...and the second still light', wait_until(lambda: dark() == 'false', 3), dark())
c.call('switch_tab', id=first)
check('...and back again', wait_until(lambda: dark() == 'true', 3), dark())

c.call('emulate', reset=True)
time.sleep(0.6)
check('reset returns the tab to light', wait_until(lambda: dark() == 'false', 3), dark())
c.close()
finish()
