"""Keyboard focus moves between the toolbar and the page without losing or leaking keys.

Regression: WebKitGTK re-dispatches keys a document did not consume and flags the sending view to pass its NEXT key
through untouched. When focus moved to the other view in between, that flag stayed behind and the first letter typed
into the palette vanished, the first Esc did not close it, and the Esc also reached the page.
"""
from mcp import *

start_site()


def run(label, pointer, omnibox_first):
    c = MCP(['--no-fullscreen'], env={'BRAIN_DEBUG_BAR': '1'})
    time.sleep(3.5)
    c.call('navigate', url=SITE + '/index.html')
    time.sleep(1.2)
    sh(f'xdotool mousemove {pointer[0]} {pointer[1]}')
    time.sleep(0.6)
    if omnibox_first:
        sh('xdotool key ctrl+l'); time.sleep(0.5)
        sh('xdotool type --delay 50 "e"'); time.sleep(1.0)
        sh('xdotool key Escape'); time.sleep(1.5)
    sh('xdotool key ctrl+k')
    time.sleep(1.5)
    sh('xdotool type --delay 150 "abcd"')
    time.sleep(0.8)
    log = open('/tmp/brain.err', errors='replace').read()
    typed = ''.join(re.findall(r'KEY ([abcd]) target', log))
    check(f'every typed key reaches the palette ({label})', typed == 'abcd', f'got "{typed}"')
    sh('xdotool key Escape')
    time.sleep(1.2)
    x, y, w, h = window_geometry()
    check(f'the FIRST Esc closes the palette ({label})', px(x + w - 140, y + h - 15)[0] > 200)
    c.close()
    time.sleep(0.5)


run('pointer over the page, after an omnibox cycle', (700, 400), True)
run('pointer low in the window, after an omnibox cycle', (700, 700), True)
run('pointer in the toolbar, after an omnibox cycle', (700, 70), True)
run('no omnibox cycle', (700, 400), False)
run('pointer outside the window', (1590, 990), True)
finish()
