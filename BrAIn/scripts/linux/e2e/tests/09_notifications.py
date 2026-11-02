# needs: notifd
"""Web notifications become desktop notifications (libnotify -> org.freedesktop.Notifications). The service here is a
mock (lib/notifd.py) that logs calls and can pretend the user clicked or dismissed."""
from mcp import *

start_site()
c = MCP(['--no-fullscreen', '--grant-permissions'])
time.sleep(4)
c.call('navigate', url=SITE + '/two.html')
time.sleep(1.0)


def log():
    return open('/tmp/notif.log').read() if os.path.exists('/tmp/notif.log') else ''


def show(tag):
    c.js(f"window.__ev=window.__ev||[]; window.__n=new Notification('{tag}',{{body:'b-{tag}'}}); "
         f"__n.onclick=()=>__ev.push('{tag}:click'); __n.onclose=()=>__ev.push('{tag}:close'); __n.onshow=()=>__ev.push('{tag}:show'); 1")
    time.sleep(0.9)


def events():
    return json.loads(c.js('JSON.stringify(__ev)'))


check('the Notification API exists', c.js('typeof Notification') == 'function')
c.js('Notification.requestPermission(); 1'); time.sleep(0.8)
check('permission is granted (--grant-permissions)', c.js('Notification.permission') == 'granted')

show('A')
check('a notification reaches the notification service with app name, text and icon',
      "app=BrAIn summary='A' body='b-A'" in log() and 'icon=icon.png' in log(), log().strip().split('\n')[-1])
check('the page gets onshow', 'A:show' in events(), events())
open('/tmp/notif.dismiss', 'w').write('1'); time.sleep(1.0)
check('the user dismissing it fires onclose in the page', 'A:close' in events(), events())

show('B')
c.js('__n.close(); 1'); time.sleep(1.0)
check('notification.close() reaches the service', 'CLOSE' in log())
check('...and onclose fires', 'B:close' in events(), events())

show('C')
c.call('new_tab', url=SITE + '/index.html'); time.sleep(1.2)
check('another tab is in front', 'Site One' in ''.join(r for a, i, r in c.tabs() if a))
open('/tmp/notif.click', 'w').write('1'); time.sleep(1.6)
check('clicking the notification switches back to its tab', 'Page Two' in ''.join(r for a, i, r in c.tabs() if a), c.tabs())
ev = events()
check('...and fires onclick in the page', 'C:click' in ev, ev)
c.close()
finish()
