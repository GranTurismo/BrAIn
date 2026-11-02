"""Cookies and site data: private tabs and ghost mode are isolated, normal data survives a restart.

Regression: WebKitGTK keeps cookies in memory unless told where to store them, so every login was forgotten when
the app quit.
"""
from mcp import *

start_site()


def echo(c):
    return c.js("document.getElementById('c') ? document.getElementById('c').textContent : '(no #c)'")


def load_echo(c, **kw):
    c.call('navigate', url=SITE + '/echo-cookies') if not kw else c.call('new_tab', url=SITE + '/echo-cookies', **kw)
    time.sleep(1.4)


c = MCP(['--no-fullscreen'])
time.sleep(3.5)
c.call('navigate', url=SITE + '/cookie'); time.sleep(0.8)
load_echo(c)
check('a normal tab sees its cookies', echo(c) == 'srv=1; vis=2', echo(c))

# ---- private tabs
load_echo(c, priv=True)
check('a private tab starts with NO cookies', echo(c) == '', repr(echo(c)))
c.call('navigate', url=SITE + '/cookie'); time.sleep(0.8)
load_echo(c, priv=True)
check('private tabs share one private store', echo(c) == 'srv=1; vis=2', echo(c))
for active, tid, rest in c.tabs():
    if '[private]' in rest:
        c.call('close_tab', id=tid); time.sleep(0.4)
time.sleep(1.0)
load_echo(c, priv=True)
check('closing every private tab wipes that store', echo(c) == '', repr(echo(c)))
for active, tid, rest in c.tabs():
    if '[private]' in rest:
        c.call('close_tab', id=tid)
time.sleep(0.8)

# ---- ghost mode
c.call('ghost', action='on'); time.sleep(2.0)
check('ghost mode logs the active tab out', echo(c) == '', repr(echo(c)))
c.call('ghost', action='off'); time.sleep(2.0)
check('ghost mode off: the tab is logged in again', echo(c) == 'srv=1; vis=2', echo(c))
hist = open(os.path.expanduser('~/.local/share/BrAIn/history.json')).read()
check('history holds the normal pages', '/echo-cookies' in hist)

# ---- the data folder is private to the user
mode = oct(os.stat(os.path.expanduser('~/.local/share/BrAIn')).st_mode & 0o777)
check('the data folder is owner-only (0700)', mode == '0o700', mode)

c.close()
time.sleep(1.5)

# ---- restart: persistent cookie stays, session cookie goes
JS_SET = """(async function(){localStorage.setItem('k','persist-me');sessionStorage.setItem('s','tmp');
 await new Promise((res,rej)=>{var r=indexedDB.open('db',1);r.onupgradeneeded=()=>r.result.createObjectStore('o');r.onsuccess=()=>{var t=r.result.transaction('o','readwrite');t.objectStore('o').put('idb-value','x');t.oncomplete=()=>res()};r.onerror=()=>rej(r.error)});
 return 'set'})()"""
JS_GET = """(async function(){var v=await new Promise((res)=>{var r=indexedDB.open('db',1);r.onupgradeneeded=()=>{r.result.createObjectStore('o')};r.onsuccess=()=>{var g=r.result.transaction('o').objectStore('o').get('x');g.onsuccess=()=>res(g.result)};r.onerror=()=>res('err')});
 return JSON.stringify({ls:localStorage.getItem('k'),ss:sessionStorage.getItem('s'),idb:v})})()"""
c = MCP(['--no-fullscreen'])
time.sleep(3.5)
c.call('navigate', url=SITE + '/two.html'); time.sleep(1.0)
c.js(JS_SET); time.sleep(1.0)
c.close()
time.sleep(1.5)
c = MCP(['--no-fullscreen'])
time.sleep(3.5)
load_echo(c)
check('after a restart the persistent cookie survives and the session cookie is gone', echo(c) == 'vis=2', repr(echo(c)))
c.call('navigate', url=SITE + '/two.html'); time.sleep(1.0)
got = json.loads(c.js(JS_GET))
check('localStorage and IndexedDB survive a restart, sessionStorage does not',
      got == {'ls': 'persist-me', 'ss': None, 'idb': 'idb-value'}, got)
c.close()
finish()
