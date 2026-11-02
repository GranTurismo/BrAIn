"""Links and window.open(): plain requests become tabs, sized popups stay real windows that can talk to their opener."""
from mcp import *

start_site()
c = MCP(['--no-fullscreen'])
time.sleep(3.5)


def ref(snapshot, kind, name):
    m = re.search(r'\[ref=(e\d+)\][^\n]*' + kind + r' "' + name + '"', snapshot)
    return m.group(1) if m else None


def windows():
    out = []
    for w in sh("xdotool search --onlyvisible --class BrAIn").split():
        out.append((sh(f'xdotool getwindowname {w}'), sh(f'xdotool getwindowgeometry {w} | tr "\\n" " "')))
    return out


c.call('navigate', url=SITE + '/index.html')
time.sleep(1)
check('one tab at the start', len(c.tabs()) == 1)

snap = c.call('snapshot')
c.call('click', element=ref(snap, 'link', 'blank'))
check('a target=_blank link opens a new tab', wait_until(lambda: len(c.tabs()) == 2, 4), f'{len(c.tabs())} tabs')
check('...and the new tab is the active one', 'Page Two' in ''.join(r for a, _, r in c.tabs() if a))

first = [i for a, i, r in c.tabs() if 'Site One' in r][0]
c.call('switch_tab', id=first)
time.sleep(0.6)
snap = c.call('snapshot')
c.call('click', element=ref(snap, 'button', 'popup'))
check('window.open(url) without features opens a tab', wait_until(lambda: len(c.tabs()) == 3, 4), f'{len(c.tabs())} tabs')

c.call('navigate', url=SITE + '/opener.html')
time.sleep(1)
snap = c.call('snapshot')
tabs_before = len(c.tabs())
c.call('click', element=ref(snap, 'button', 'sized'))
child = wait_until(lambda: sh("xdotool search --onlyvisible --name '^child' | head -1"), 5)
check('window.open with width/height opens a separate window', bool(child))
check('...of the requested size', child and '420x300' in sh(f'xdotool getwindowgeometry {child}'), sh(f'xdotool getwindowgeometry {child} | tr "\\n" " "'))
check('...whose script can reach window.opener', wait_until(lambda: c.js('document.title') == 'got:hello-from-child', 4), c.js('document.title'))
check('...and it is not a browser tab', len(c.tabs()) == tabs_before, f'{len(c.tabs())} tabs')
desk('04-sized-popup')
c.close()
finish()
