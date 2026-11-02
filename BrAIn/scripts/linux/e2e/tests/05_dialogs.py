"""Native dialogs: a site asking for a permission, and HTTP basic authentication."""
import threading

from mcp import *

start_site()
c = MCP(['--no-fullscreen'])
time.sleep(3.5)
c.call('navigate', url=SITE + '/two.html')
time.sleep(1)

# ---- permission prompt (geolocation) -- sites must ask; the default answer is "Block"
c.js('window.__geo="pending"; navigator.geolocation.getCurrentPosition(p=>{window.__geo="allowed"},e=>{window.__geo="denied:"+e.code}); 1')
dlg = wait_until(lambda: sh("xdotool search --onlyvisible --name 'Permission request' | head -1"), 5)
check('a permission prompt appears', bool(dlg))
if dlg:
    desk('05-permission')
    check('the toolbar stays painted while a dialog is open', strip_stddev() > 8)
    sh(f'xdotool windowactivate {dlg}'); time.sleep(0.4)
    sh('xdotool key Return')                       # the focused default button is "Block"
    time.sleep(1.5)
    check('Block denies the request', c.js('window.__geo') == 'denied:1', c.js('window.__geo'))

# ---- HTTP basic auth
def navigate():
    try:
        c.call('navigate', url=SITE + '/auth', timeout=15)
    except Exception:
        pass

t = threading.Thread(target=navigate)
t.start()
dlg = wait_until(lambda: sh("xdotool search --onlyvisible --name 'Sign in' | head -1"), 6)
check('a sign-in dialog appears for HTTP auth', bool(dlg))
if dlg:
    desk('05-auth')
    sh(f'xdotool windowactivate {dlg}'); time.sleep(0.4)
    sh('xdotool type --delay 40 "user"'); sh('xdotool key Tab'); sh('xdotool type --delay 40 "pass"'); time.sleep(0.3)
    sh('xdotool key Return')
t.join(20)
time.sleep(1.0)
check('the credentials are accepted', 'welcome user' in c.call('text', maxLength=80), c.call('text', maxLength=40))
c.close()
finish()
